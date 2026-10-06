using System.Net;
using UniTechDesk.Api;
using UniTechDesk.Api.Infrastructure;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Services;
using UniTechDesk.Tests.Support;

namespace UniTechDesk.Tests;

/// <summary>Correcciones surgidas de la revisión independiente del backend.</summary>
public class ReviewFixesTests
{
    [Fact]
    public async Task Si_falla_la_relectura_despues_de_guardar_la_nota_no_se_duplica()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.ToStatus(id, "IN_REVIEW", "Revisando.");
        // Lecturas: 1 = al empezar la operación; 2 = la relectura posterior a la confirmación (esta falla una vez).
        env.Store.DeadlockOnReadNumber = 2;

        var t = await env.Staff.AddNoteAsync(env.Admin, id, new NoteRequest { Text = "Nota única" }, default);

        Assert.Equal(1, t.History.Count(h => h.Type == "NOTE"));
        Assert.Equal(1, (await env.Get(id)).History.Count(h => h.Type == "NOTE"));
    }

    [Fact]
    public async Task Si_falla_la_relectura_despues_de_guardar_la_cotizacion_no_se_crea_otra_version()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.ToStatus(id, "IN_REVIEW", "Revisando.");
        env.Store.DeadlockOnReadNumber = 2;

        await env.SaveQuote(id, 500m);

        Assert.Equal(1, (await env.Get(id)).History.Count(h => h.Type == "QUOTE"));
    }

    [Fact]
    public async Task El_cliente_no_ve_el_paso_interno_Cotizado_sino_En_revision()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.ToStatus(id, "IN_REVIEW", "Revisando.");
        await env.SaveQuote(id);
        await env.ToStatus(id, "QUOTED");

        Assert.Equal("QUOTED", (await env.Get(id)).Status);                                         // el personal sí lo ve
        var t = await env.Public.TrackAsync(code, Env.ClientEmail, default);
        Assert.Equal("IN_REVIEW", t.Status);                                                         // el cliente, no
        Assert.Null(t.Quote);

        await env.ToStatus(id, "AWAITING_APPROVAL", "Te enviamos la cotización.");
        Assert.Equal("AWAITING_APPROVAL", (await env.Public.TrackAsync(code, Env.ClientEmail, default)).Status);
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:1:2:3:4", "2001:db8:1:2::/64")]
    public void Los_limites_por_IP_agrupan_IPv6_por_prefijo_64(string ip, string expected) =>
        Assert.Equal(expected, RateLimiting.ClientKey(IPAddress.Parse(ip)));

    [Fact]
    public void Sin_direccion_el_limite_usa_una_clave_fija() => Assert.Equal("desconocida", RateLimiting.ClientKey(null));

    [Theory]
    [InlineData("Server=x;Database=UniTechDesk;User=sa;Password=a")]
    [InlineData("Server=x;Database=UniTechDesk;User Id=SA;Password=a")]
    [InlineData("Server=x;Database=UniTechDesk;UID=sa")]
    public void El_inicio_como_sa_se_rechaza_con_cualquiera_de_sus_sinonimos(string cs)
    {
        var p = StartupChecks.Validate(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new TestEnv(), new(), new() { SigningKey = new string('k', 40) }, new() { Provider = "Console" }, new(), cs);
        Assert.Contains(p.Fatal, m => m.Contains("«sa»"));
    }

    [Fact]
    public void Sin_captcha_en_produccion_avisa()
    {
        var p = StartupChecks.Validate(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new TestEnv(), new(), new() { SigningKey = new string('k', 40) }, new() { Provider = "Console" }, new(), "Server=x;Database=d;User Id=unitech_api;Password=a");
        Assert.Contains(p.Warnings, m => m.Contains("Captcha:TurnstileSecret"));
    }

    private sealed class TestEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
