using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using UniTechDesk.Api.Endpoints;
using UniTechDesk.Api.Infrastructure;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Email;
using UniTechDesk.Core.Files;
using UniTechDesk.Core.Options;
using UniTechDesk.Core.Services;
using UniTechDesk.Data;
using UniTechDesk.Data.Sql;

namespace UniTechDesk.Api;

/// <summary>Arma la aplicación. Se separa de Program para poder arrancarla también desde pruebas (cambiando repositorios).</summary>
public static class UniTechApp
{
    public static WebApplication Build(string[] args, Action<IServiceCollection>? overrideServices = null, WebApplicationOptions? hostOptions = null)
    {
        var builder = hostOptions is null ? WebApplication.CreateBuilder(args) : WebApplication.CreateBuilder(hostOptions);
        var cfg = builder.Configuration;

        // Opciones tipadas (se leen una vez al arrancar).
        var app = Bind<AppOptions>(cfg, AppOptions.Section);
        var uploads = Bind<UploadOptions>(cfg, UploadOptions.Section);
        var jwt = Bind<JwtOptions>(cfg, JwtOptions.Section);
        var login = Bind<LoginOptions>(cfg, LoginOptions.Section);
        var payment = Bind<PaymentOptions>(cfg, PaymentOptions.Section);
        var email = Bind<EmailOptions>(cfg, EmailOptions.Section);
        var captcha = Bind<CaptchaOptions>(cfg, CaptchaOptions.Section);
        var connectionString = cfg.GetConnectionString("UniTechDesk") ?? "";

        var problems = StartupChecks.Validate(cfg, builder.Environment, app, jwt, email, uploads, connectionString);
        problems.ThrowIfFatal();

        var s = builder.Services;
        s.AddSingleton(app); s.AddSingleton(uploads); s.AddSingleton(jwt); s.AddSingleton(login);
        s.AddSingleton(payment); s.AddSingleton(email); s.AddSingleton(captcha);
        s.AddSingleton(problems);

        s.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
            o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        });
        s.AddSingleton(sp => sp.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions);

        s.Configure<FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = BodyLimit(uploads);
            o.ValueLengthLimit = 64 * 1024;          // la parte "data" (JSON) del formulario
            o.ValueCountLimit = 20;
            o.MultipartHeadersLengthLimit = 16 * 1024;
        });

        s.AddMemoryCache();
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<BusinessClock>();

        // Datos
        s.AddSingleton<IDbConnectionFactory>(_ => new SqlServerConnectionFactory(connectionString));
        s.AddSingleton<ISqlErrorClassifier, SqlServerErrorClassifier>();
        s.AddSingleton<ITicketRepository, SqlTicketRepository>();
        s.AddSingleton<IStaffRepository, SqlStaffRepository>();
        s.AddSingleton<ICatalogRepository, SqlCatalogRepository>();
        s.AddSingleton<IOutboxRepository, SqlOutboxRepository>();

        // Archivos
        var storagePath = Path.IsPathRooted(uploads.StoragePath) ? uploads.StoragePath : Path.Combine(builder.Environment.ContentRootPath, uploads.StoragePath);
        s.AddSingleton<IAttachmentStore>(_ => new DiskAttachmentStore(storagePath));
        s.AddSingleton<IFileLinkSigner>(_ => new FileLinkSigner(jwt.SigningKey, TimeSpan.FromMinutes(10)));

        // Seguridad
        s.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        s.AddSingleton<JwtService>();
        s.AddSingleton<ITokenIssuer>(sp => sp.GetRequiredService<JwtService>());
        s.AddAuthentication(JwtService.Scheme).AddScheme<AuthenticationSchemeOptions, StaffTokenHandler>(JwtService.Scheme, _ => { });
        s.AddAuthorization();
        s.AddRateLimiter(RateLimiting.Configure);
        s.AddHttpClient<TurnstileVerifier>(c => c.Timeout = TimeSpan.FromSeconds(8));
        s.AddSingleton<ICaptchaVerifier>(sp => sp.GetRequiredService<TurnstileVerifier>());

        // Servicios de negocio
        s.AddSingleton(sp => new TicketMapper(payment, sp.GetRequiredService<IFileLinkSigner>(), sp.GetRequiredService<BusinessClock>()));
        s.AddSingleton<CatalogService>();
        s.AddSingleton<MailPlanner>();
        s.AddSingleton<PublicTicketService>();
        s.AddSingleton<StaffTicketService>();
        s.AddSingleton<AuthService>();

        // Correos
        s.AddSingleton<EmailTemplates>();
        s.AddSingleton<OutboxSignal>();
        s.AddSingleton<IOutboxSignal>(sp => sp.GetRequiredService<OutboxSignal>());
        s.AddSingleton<OutboxDispatcher>();
        if (string.Equals(email.Provider, "SendGrid", StringComparison.OrdinalIgnoreCase))
        {
            s.AddHttpClient<IEmailSender, SendGridEmailSender>(c => c.Timeout = TimeSpan.FromSeconds(20));
        }
        else s.AddSingleton<IEmailSender, ConsoleEmailSender>();
        if (!string.Equals(email.Provider, "None", StringComparison.OrdinalIgnoreCase)) s.AddHostedService<OutboxWorker>();

        if (cfg.GetValue<bool>("Hosting:TrustForwardedHeaders"))
        {
            s.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();   // el proxy inverso es de confianza (se asume que solo él llega a la API)
            });
        }

        overrideServices?.Invoke(s);

        var web = builder.Build();

        foreach (var warning in web.Services.GetRequiredService<StartupProblems>().Warnings)
            web.Logger.LogWarning("{Warning}", warning);

        if (cfg.GetValue<bool>("Hosting:TrustForwardedHeaders")) web.UseForwardedHeaders();
        web.UseMiddleware<ExceptionMiddleware>();
        if (!web.Environment.IsDevelopment()) web.UseHsts();
        if (cfg.GetValue<bool>("Hosting:RedirectToHttps")) web.UseHttpsRedirection();
        web.UseMiddleware<SecurityHeadersMiddleware>(!string.IsNullOrWhiteSpace(captcha.TurnstileSecret));
        web.UseMiddleware<BodyLimitMiddleware>(BodyLimit(uploads));
        web.UseDefaultFiles();
        web.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                // index.html y los .js/.css del front cambian con cada versión: que el navegador los revalide siempre.
                ctx.Context.Response.Headers.CacheControl = "no-cache";
            }
        });
        web.UseRouting();
        web.UseRateLimiter();
        web.UseAuthentication();
        web.UseAuthorization();

        ApiEndpoints.Map(web);
        return web;
    }

    /// <summary>Tope del cuerpo de una solicitud con archivos: archivos + un margen para el JSON y los encabezados.</summary>
    private static long BodyLimit(UploadOptions u) => (long)(u.MaxFiles + 1) * u.MaxFileSizeMB * 1024 * 1024 + 256 * 1024;

    private static T Bind<T>(IConfiguration cfg, string section) where T : new()
    {
        var o = new T();
        cfg.GetSection(section).Bind(o);
        return o;
    }
}
