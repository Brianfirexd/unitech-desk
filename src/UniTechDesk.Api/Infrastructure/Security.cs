using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using BCrypt.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Options;

namespace UniTechDesk.Api.Infrastructure;

public sealed class BCryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 12;

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool Verify(string password, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(password, hash); }
        catch (SaltParseException) { return false; }        // el hash guardado no tiene formato BCrypt
        catch (ArgumentException) { return false; }
    }
}

/// <summary>Emite y valida el JWT del personal (HS256, con emisor, audiencia y vencimiento).</summary>
public sealed class JwtService : ITokenIssuer
{
    public const string Scheme = "StaffToken";

    private readonly JwtOptions _options;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials;

    public TokenValidationParameters Parameters { get; }

    public JwtService(JwtOptions options)
    {
        _options = options;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        Parameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = options.Issuer,
            ValidateAudience = true, ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = key,
            // Solo HS256: un token que declare "alg: none" u otro algoritmo se rechaza.
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            RequireSignedTokens = true, RequireExpirationTime = true, ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name", RoleClaimType = "role"
        };
    }

    public IssuedToken Issue(StaffAccount account, DateTime now)
    {
        var expires = now.AddMinutes(_options.ExpiresMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("sub", account.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim("name", account.FullName),
                new Claim("role", account.Role),
                new Claim("jti", Guid.NewGuid().ToString("N"))
            }),
            Issuer = _options.Issuer, Audience = _options.Audience,
            IssuedAt = now, NotBefore = now, Expires = expires,
            SigningCredentials = _credentials
        };
        return new IssuedToken(_handler.CreateToken(descriptor), expires);
    }

    public async Task<TokenValidationResult> ValidateAsync(string token) => await _handler.ValidateTokenAsync(token, Parameters);
}

/// <summary>
/// Autenticación del personal: "Authorization: Bearer &lt;jwt&gt;". Además de validar la firma y el vencimiento, comprueba (con una caché
/// de 30 s) que la cuenta siga activa, y toma el nombre y el rol vigentes de la base de datos.
/// </summary>
public sealed class StaffTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly JwtService _jwt;
    private readonly IStaffRepository _staff;
    private readonly IMemoryCache _cache;

    public StaffTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        JwtService jwt, IStaffRepository staff, IMemoryCache cache) : base(options, logger, encoder)
    {
        _jwt = jwt; _staff = staff; _cache = cache;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header)) return AuthenticateResult.NoResult();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[prefix.Length..].Trim();
        if (token.Length is 0 or > 4096) return AuthenticateResult.Fail("Token vacío o demasiado largo.");

        var result = await _jwt.ValidateAsync(token);
        if (!result.IsValid) return AuthenticateResult.Fail("Token no válido.");

        var sub = result.ClaimsIdentity?.FindFirst("sub")?.Value;
        if (!int.TryParse(sub, out var staffId)) return AuthenticateResult.Fail("Token sin identificador.");

        var account = await _cache.GetOrCreateAsync($"staff:{staffId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await _staff.FindByIdAsync(staffId, Context.RequestAborted);
        });
        if (account is null || !account.Active) return AuthenticateResult.Fail("Cuenta inactiva.");

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, account.FullName),
            new Claim(ClaimTypes.Role, account.Role)
        }, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        return ApiErrors.WriteAsync(Context, StatusCodes.Status401Unauthorized, "Tu sesión expiró. Inicia sesión de nuevo.",
            headers: new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer" });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiErrors.WriteAsync(Context, StatusCodes.Status403Forbidden, "No tienes permiso para hacer esto.");
}

/// <summary>Verificación Cloudflare Turnstile (opcional). Sin clave secreta no se verifica nada.</summary>
public sealed class TurnstileVerifier : ICaptchaVerifier
{
    private const string Endpoint = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    private readonly HttpClient _http;
    private readonly CaptchaOptions _options;
    private readonly ILogger<TurnstileVerifier> _log;

    public TurnstileVerifier(HttpClient http, CaptchaOptions options, ILogger<TurnstileVerifier> log)
    {
        _http = http; _options = options; _log = log;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_options.TurnstileSecret);

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        if (!Enabled) return true;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return false;
        try
        {
            var fields = new List<KeyValuePair<string, string>> { new("secret", _options.TurnstileSecret), new("response", token) };
            if (!string.IsNullOrEmpty(remoteIp)) fields.Add(new("remoteip", remoteIp));
            using var response = await _http.PostAsync(Endpoint, new FormUrlEncodedContent(fields), ct);
            if (!response.IsSuccessStatusCode) { _log.LogWarning("Turnstile respondió {Status}.", (int)response.StatusCode); return false; }
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            _log.LogWarning(ex, "No se pudo verificar Turnstile; se rechaza el envío por seguridad.");
            return false;
        }
    }
}
