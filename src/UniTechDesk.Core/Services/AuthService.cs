using Microsoft.Extensions.Logging;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Options;

namespace UniTechDesk.Core.Services;

/// <summary>Inicio de sesión del personal: contraseña con hash (BCrypt), bloqueo temporal por intentos y token con vencimiento.</summary>
public sealed class AuthService
{
    public const string BadCredentials = "Usuario o contraseña incorrectos. Tras varios intentos fallidos, la cuenta se bloquea unos minutos.";

    private readonly IStaffRepository _staff;
    private readonly IPasswordHasher _hasher;
    private readonly ITokenIssuer _tokens;
    private readonly BusinessClock _clock;
    private readonly LoginOptions _options;
    private readonly ILogger<AuthService> _log;
    private readonly Lazy<string> _dummyHash;

    public AuthService(IStaffRepository staff, IPasswordHasher hasher, ITokenIssuer tokens, BusinessClock clock, LoginOptions options, ILogger<AuthService> log)
    {
        _staff = staff; _hasher = hasher; _tokens = tokens; _clock = clock; _options = options; _log = log;
        // Hash de relleno: si el usuario no existe, igual se hace una verificación para que la respuesta tarde lo mismo.
        _dummyHash = new Lazy<string>(() => hasher.Hash(Guid.NewGuid().ToString("N")));
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var username = (req.Username ?? "").Trim();
        var password = req.Password ?? "";
        // BCrypt solo usa los primeros 72 bytes: una contraseña más larga nunca pudo haberse guardado tal cual.
        if (username.Length is 0 or > 50 || password.Length is 0 || System.Text.Encoding.UTF8.GetByteCount(password) > 72)
        {
            _ = _hasher.Verify("x", _dummyHash.Value);
            throw AppException.Unauthorized(BadCredentials);
        }

        var now = _clock.UtcNow;
        var account = await _staff.FindByUsernameAsync(username, ct);
        if (account is null)
        {
            _ = _hasher.Verify(password, _dummyHash.Value);
            throw AppException.Unauthorized(BadCredentials);
        }

        // Una cuenta bloqueada responde igual que una contraseña incorrecta (y tarda lo mismo): así nadie puede usar el aviso de
        // bloqueo para averiguar qué usuarios existen. El mensaje general ya explica que puede haber un bloqueo temporal.
        if (account.LockedUntil is { } until && until > now)
        {
            _ = _hasher.Verify(password, _dummyHash.Value);
            throw AppException.Unauthorized(BadCredentials);
        }

        var ok = _hasher.Verify(password, account.PasswordHash) && account.Active;
        if (!ok)
        {
            await _staff.RegisterFailedLoginAsync(account.Id, now, _options.MaxAttempts, now.AddMinutes(_options.LockMinutes), ct);
            _log.LogWarning("Inicio de sesión fallido para el personal #{StaffId}.", account.Id);
            throw AppException.Unauthorized(BadCredentials);
        }

        await _staff.RegisterSuccessfulLoginAsync(account.Id, now, ct);
        var token = _tokens.Issue(account, now);
        return new LoginResponse(token.Token, token.ExpiresAt, new UserDto(account.FullName, account.Role));
    }
}
