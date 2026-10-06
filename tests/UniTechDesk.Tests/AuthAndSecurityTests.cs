using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using UniTechDesk.Api.Infrastructure;
using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Files;
using UniTechDesk.Core.Options;
using UniTechDesk.Core.Services;
using UniTechDesk.TestSupport;
using UniTechDesk.Tests.Support;

namespace UniTechDesk.Tests;

public class AuthServiceTests
{
    private static Task<LoginResponse> Login(Env env, string user, string pass) =>
        env.Auth.LoginAsync(new LoginRequest { Username = user, Password = pass }, default);

    [Fact]
    public async Task Inicio_de_sesion_correcto_devuelve_token_nombre_y_rol()
    {
        var env = new Env();
        var r = await Login(env, "admin", "Admin#2026");
        Assert.Equal($"token-{env.AdminId}", r.Token);
        Assert.Equal("Administración", r.User.Name);
        Assert.Equal("ADMIN", r.User.Role);
        Assert.True(r.ExpiresAt > env.Clock.UtcNow);
    }

    [Fact]
    public async Task El_usuario_no_distingue_mayusculas_ni_espacios()
    {
        var env = new Env();
        Assert.Equal("ADMIN", (await Login(env, "  ADMIN ", "Admin#2026")).User.Role);
    }

    [Theory]
    [InlineData("admin", "equivocada")]
    [InlineData("nadie", "Admin#2026")]
    [InlineData("admin", "")]
    [InlineData("", "Admin#2026")]
    [InlineData("admin", "admin#2026")]
    public async Task Cualquier_credencial_incorrecta_da_el_mismo_401(string user, string pass)
    {
        var env = new Env();
        var ex = await Env.Fails(401, () => Login(env, user, pass));
        Assert.Equal(AuthService.BadCredentials, ex.Message);        // no revela si el usuario existe
    }

    [Fact]
    public async Task Contrasena_de_mas_de_72_bytes_o_usuario_enorme_se_rechazan_sin_consultar_la_base()
    {
        var env = new Env();
        await Env.Fails(401, () => Login(env, "admin", new string('a', 73)));
        await Env.Fails(401, () => Login(env, new string('u', 51), "Admin#2026"));
        Assert.Equal(0, env.Store.FailedAttempts("admin"));
    }

    [Fact]
    public async Task Tras_5_fallos_la_cuenta_se_bloquea_aunque_despues_se_use_la_contrasena_correcta()
    {
        var env = new Env();
        for (var i = 0; i < 5; i++) await Env.Fails(401, () => Login(env, "admin", "mala"));
        // Bloqueada: responde igual que una contraseña incorrecta (así el aviso no revela que el usuario existe).
        var ex = await Env.Fails(401, () => Login(env, "admin", "Admin#2026"));
        Assert.Equal(AuthService.BadCredentials, ex.Message);
        var unknown = await Env.Fails(401, () => Login(env, "no-existe", "Admin#2026"));
        Assert.Equal(unknown.Message, ex.Message);
        await Env.Fails(401, () => Login(env, "admin", "mala"));
    }

    [Fact]
    public async Task El_bloqueo_termina_a_los_15_minutos_y_un_acceso_correcto_reinicia_el_contador()
    {
        var env = new Env();
        for (var i = 0; i < 5; i++) await Env.Fails(401, () => Login(env, "admin", "mala"));
        env.Time.Advance(TimeSpan.FromMinutes(14));
        await Env.Fails(401, () => Login(env, "admin", "Admin#2026"));
        env.Time.Advance(TimeSpan.FromMinutes(2));
        await Login(env, "admin", "Admin#2026");
        Assert.Equal(0, env.Store.FailedAttempts("admin"));
    }

    [Fact]
    public async Task Los_fallos_de_una_cuenta_no_bloquean_otra()
    {
        var env = new Env();
        for (var i = 0; i < 6; i++) await Env.Fails(401, () => Login(env, "admin", "mala"));
        await Login(env, "tecnico1", "Tec#2026");
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_puede_entrar_aunque_la_contrasena_sea_correcta()
    {
        var env = new Env();
        env.Store.SetActive(env.TecId, false);
        await Env.Fails(401, () => Login(env, "tecnico1", "Tec#2026"));
    }
}

public class PasswordHasherTests
{
    private readonly BCryptPasswordHasher _hasher = new();

    [Fact]
    public void Hash_y_verificacion()
    {
        var hash = _hasher.Hash("Admin#2026");
        Assert.StartsWith("$2", hash);
        Assert.NotEqual("Admin#2026", hash);
        Assert.True(_hasher.Verify("Admin#2026", hash));
        Assert.False(_hasher.Verify("admin#2026", hash));
        Assert.NotEqual(hash, _hasher.Hash("Admin#2026"));         // sal distinta cada vez
    }

    [Fact]
    public void El_hash_de_los_datos_de_demostracion_de_06_corresponde_a_Admin_2026()
    {
        // Copiado de database/06_datos_demo.sql
        const string demo = "$2b$12$FHj6vdN.HrVNu1dmrU9j6eRqICwksTpJ4Bs79v62/E9mqd4a8V4D2";
        Assert.True(_hasher.Verify("Admin#2026", demo));
        Assert.False(_hasher.Verify("Admin#2025", demo));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-un-hash")]
    [InlineData("$2b$12$corto")]
    [InlineData("plain:Admin#2026")]
    public void Un_hash_con_formato_invalido_nunca_acepta_ni_lanza(string stored) => Assert.False(_hasher.Verify("Admin#2026", stored));
}

public class JwtTests
{
    private const string Key = "una-clave-de-prueba-muy-larga-de-mas-de-32-caracteres";
    private static DateTime Now => DateTime.UtcNow;

    private static JwtService Service(string key = Key, int minutes = 60, string issuer = "UniTechDesk", string audience = "UniTechDesk.Staff") =>
        new(new JwtOptions { SigningKey = key, ExpiresMinutes = minutes, Issuer = issuer, Audience = audience });

    private static StaffAccount Account => new() { Id = 7, FullName = "Ana Técnica", Role = "TECNICO", Active = true };

    [Fact]
    public async Task Token_valido_lleva_id_nombre_y_rol()
    {
        var svc = Service();
        var now = DateTime.UtcNow;
        var issued = svc.Issue(Account, now);
        var r = await svc.ValidateAsync(issued.Token);
        Assert.True(r.IsValid);
        Assert.Equal("7", r.ClaimsIdentity.FindFirst("sub")!.Value);
        Assert.Equal("TECNICO", r.ClaimsIdentity.FindFirst("role")!.Value);
        Assert.Equal(now.AddMinutes(60), issued.ExpiresAt);
    }

    [Fact]
    public async Task Token_vencido_se_rechaza_pasada_la_tolerancia_de_30_segundos()
    {
        var svc = Service(minutes: 60);
        // venció hace 50 s: fuera de la tolerancia → inválido; venció hace 10 s: dentro → todavía válido
        Assert.False((await svc.ValidateAsync(svc.Issue(Account, DateTime.UtcNow.AddMinutes(-60).AddSeconds(-50)).Token)).IsValid);
        Assert.True((await svc.ValidateAsync(svc.Issue(Account, DateTime.UtcNow.AddMinutes(-60).AddSeconds(-10)).Token)).IsValid);
        Assert.False((await svc.ValidateAsync(svc.Issue(Account, DateTime.UtcNow.AddDays(-2)).Token)).IsValid);
    }

    [Fact]
    public async Task Token_firmado_con_otra_clave_se_rechaza()
    {
        var other = Service("otra-clave-distinta-de-mas-de-32-caracteres-xx");
        Assert.False((await Service().ValidateAsync(other.Issue(Account, Now).Token)).IsValid);
    }

    [Theory]
    [InlineData("OtroEmisor", "UniTechDesk.Staff")]
    [InlineData("UniTechDesk", "OtraAudiencia")]
    public async Task Token_de_otro_emisor_o_audiencia_se_rechaza(string issuer, string audience)
    {
        var foreign = Service(issuer: issuer, audience: audience);
        Assert.False((await Service().ValidateAsync(foreign.Issue(Account, Now).Token)).IsValid);
    }

    [Fact]
    public async Task Token_con_payload_alterado_se_rechaza()
    {
        var svc = Service();
        var parts = svc.Issue(Account, Now).Token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("TECNICO", "ADMIN");
        var forged = parts[0] + "." + Base64UrlEncoder.Encode(payload) + "." + parts[2];
        Assert.False((await svc.ValidateAsync(forged)).IsValid);
    }

    [Fact]
    public async Task Token_con_algoritmo_none_se_rechaza()
    {
        var header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sub"] = "1", ["role"] = "ADMIN", ["name"] = "x", ["iss"] = "UniTechDesk", ["aud"] = "UniTechDesk.Staff",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        }));
        Assert.False((await Service().ValidateAsync(header + "." + payload + ".")).IsValid);
        Assert.False((await Service().ValidateAsync(header + "." + payload + ".AAAA")).IsValid);
    }

    [Fact]
    public async Task Token_firmado_con_la_misma_clave_pero_otro_algoritmo_se_rechaza()
    {
        var longKey = new string('k', 80);                       // HS512 exige una clave de al menos 512 bits
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(longKey));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[] { new Claim("sub", "1"), new Claim("role", "ADMIN") }),
            Issuer = "UniTechDesk", Audience = "UniTechDesk.Staff", Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha512)
        });
        Assert.False((await Service(longKey).ValidateAsync(token)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b.c")]
    [InlineData("Bearer x")]
    public async Task Basura_se_rechaza_sin_lanzar_excepciones(string token) => Assert.False((await Service().ValidateAsync(token)).IsValid);
}

public class FileLinkSignerTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc);
    private readonly FileLinkSigner _signer = new("clave-de-prueba-clave-de-prueba-1234", TimeSpan.FromMinutes(10));

    private static (long e, string s) Parse(string url)
    {
        var q = System.Web.HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..]);
        return (long.Parse(q["e"]!), q["s"]!);
    }

    [Fact]
    public void El_enlace_vale_para_ese_archivo_hasta_que_vence()
    {
        var url = _signer.CreateUrl(42, Now);
        Assert.StartsWith("/api/files/42?e=", url);
        var (e, s) = Parse(url);
        Assert.True(_signer.IsValid(42, e, s, Now));
        Assert.True(_signer.IsValid(42, e, s, Now.AddMinutes(9)));
        Assert.False(_signer.IsValid(42, e, s, Now.AddMinutes(11)));
    }

    [Fact]
    public void No_sirve_para_otro_archivo_ni_con_la_firma_o_el_vencimiento_alterados()
    {
        var (e, s) = Parse(_signer.CreateUrl(42, Now));
        Assert.False(_signer.IsValid(43, e, s, Now));
        Assert.False(_signer.IsValid(42, e + 3600, s, Now));
        Assert.False(_signer.IsValid(42, e, s[..^1] + (s[^1] == '0' ? "1" : "0"), Now));
        Assert.False(_signer.IsValid(42, e, "", Now));
        Assert.False(_signer.IsValid(42, e, null!, Now));
        Assert.False(_signer.IsValid(42, e, new string('0', 64), Now));
    }

    [Fact]
    public void Otra_clave_no_valida_la_firma()
    {
        var other = new FileLinkSigner("otra-clave-distinta-otra-clave-distinta", TimeSpan.FromMinutes(10));
        var (e, s) = Parse(_signer.CreateUrl(42, Now));
        Assert.False(other.IsValid(42, e, s, Now));
    }

    [Fact]
    public void Un_token_de_sesion_no_sirve_como_firma_de_archivo()
    {
        // la subclave del enlace se deriva con otra etiqueta: firmar "42|exp" con la clave JWT cruda no coincide
        const string master = "clave-de-prueba-clave-de-prueba-1234";
        var (e, s) = Parse(_signer.CreateUrl(42, Now));
        using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(master));
        var naive = Convert.ToHexString(h.ComputeHash(Encoding.ASCII.GetBytes($"42|{e}"))).ToLowerInvariant();
        Assert.NotEqual(naive, s);
    }
}

public class BusinessClockTests
{
    [Fact]
    public void Hoy_se_calcula_en_la_zona_horaria_del_negocio()
    {
        // 03:00 UTC del 7 de octubre = 21:00 del 6 de octubre en Managua (UTC-6)
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero));
        var clock = new BusinessClock(time, new AppOptions { TimeZoneId = "America/Managua" });
        Assert.Equal(new DateOnly(2026, 10, 6), clock.Today);
        time.Advance(TimeSpan.FromHours(4));
        Assert.Equal(new DateOnly(2026, 10, 7), clock.Today);
    }

    [Fact]
    public void Una_zona_inexistente_usa_UTC_menos_6_como_respaldo()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero));
        var clock = new BusinessClock(time, new AppOptions { TimeZoneId = "Zona/Que/No/Existe" });
        Assert.Equal(new DateOnly(2026, 10, 6), clock.Today);
    }

    [Fact]
    public void La_hora_UTC_no_tiene_fracciones_de_segundo_y_es_Utc()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero).AddTicks(1234567));
        var now = new BusinessClock(time, new AppOptions()).UtcNow;
        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.Equal(0, now.Ticks % TimeSpan.TicksPerSecond);
    }
}
