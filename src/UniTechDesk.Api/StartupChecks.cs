using UniTechDesk.Core.Options;

namespace UniTechDesk.Api;

/// <summary>Problemas de configuración encontrados al arrancar. Los fatales impiden iniciar y dicen exactamente cómo arreglarlos.</summary>
public sealed class StartupProblems
{
    public List<string> Fatal { get; } = new();
    public List<string> Warnings { get; } = new();

    public void ThrowIfFatal()
    {
        if (Fatal.Count == 0) return;
        throw new InvalidOperationException("No se puede iniciar UniTech Desk por problemas de configuración:\n - " + string.Join("\n - ", Fatal));
    }
}

public static class StartupChecks
{
    public static StartupProblems Validate(IConfiguration cfg, IHostEnvironment env, AppOptions app, JwtOptions jwt, EmailOptions email, UploadOptions uploads, string connectionString)
    {
        var p = new StartupProblems();

        if (string.IsNullOrWhiteSpace(connectionString))
            p.Fatal.Add("Falta la cadena de conexión. Guárdala con:  dotnet user-secrets set \"ConnectionStrings:UniTechDesk\" \"Server=localhost;Database=UniTechDesk;User Id=unitech_api;Password=<tu-contraseña>;TrustServerCertificate=True\"  (ver README).");
        else
        {
            var cs = connectionString.Replace(" ", "", StringComparison.Ordinal);
            if (System.Text.RegularExpressions.Regex.IsMatch(cs, @"(^|;)(UserId|UID|User)=sa(;|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                p.Fatal.Add("La API no debe conectarse como «sa». Usa el inicio de sesión restringido unitech_api (rol rol_unitech_api; ver database/05_seguridad.sql).");
            else if (cs.Contains("IntegratedSecurity=true", StringComparison.OrdinalIgnoreCase) || cs.Contains("IntegratedSecurity=SSPI", StringComparison.OrdinalIgnoreCase) || cs.Contains("Trusted_Connection=true", StringComparison.OrdinalIgnoreCase))
                p.Warnings.Add("La conexión usa autenticación de Windows: sirve para desarrollo, pero en producción usa el inicio de sesión restringido unitech_api (mínimo privilegio).");
        }

        if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
            p.Fatal.Add("Falta Jwt:SigningKey (mínimo 32 caracteres). Genera una con:  dotnet run --project src/UniTechDesk.Api -- generate-key  y guárdala con  dotnet user-secrets set \"Jwt:SigningKey\" \"<la-clave>\".");
        if (jwt.ExpiresMinutes is < 5 or > 1440) p.Fatal.Add("Jwt:ExpiresMinutes debe estar entre 5 y 1440.");

        var provider = email.Provider ?? "";
        if (!new[] { "SendGrid", "Console", "None" }.Contains(provider, StringComparer.OrdinalIgnoreCase))
            p.Fatal.Add("Email:Provider debe ser SendGrid, Console o None.");
        if (string.Equals(provider, "SendGrid", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(email.SendGridApiKey))
                p.Fatal.Add("Email:Provider es SendGrid pero falta la clave. Guárdala con:  dotnet user-secrets set \"Email:SendGridApiKey\" \"<tu-clave>\"  (nunca la pegues en un chat ni en el repositorio).");
            if (string.IsNullOrWhiteSpace(email.FromEmail))
                p.Fatal.Add("Falta Email:FromEmail: debe ser el remitente verificado en SendGrid (Single Sender).");
        }
        else if (string.Equals(provider, "Console", StringComparison.OrdinalIgnoreCase) && !env.IsDevelopment())
            p.Warnings.Add("Email:Provider es «Console»: los correos NO se envían, solo se escriben en el registro. Para producción usa SendGrid.");
        else if (string.Equals(provider, "None", StringComparison.OrdinalIgnoreCase))
            p.Warnings.Add("Email:Provider es «None»: no se enviará ningún correo. Pon config.notifications.email = false en el front.");

        if (!env.IsDevelopment() && string.IsNullOrWhiteSpace(cfg["Captcha:TurnstileSecret"]))
            p.Warnings.Add("Captcha:TurnstileSecret está vacío: el formulario público solo se protege con el campo trampa y el límite por IP. Cualquiera puede crear tickets (y provocar correos) con el correo de otra persona. Para publicar, activa Cloudflare Turnstile (ver README).");

        if (!Uri.TryCreate(app.PublicBaseUrl, UriKind.Absolute, out var baseUrl) || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
            p.Fatal.Add("App:PublicBaseUrl debe ser una URL http(s) completa (es el enlace «Consultar mi ticket» de los correos).");
        else if (!env.IsDevelopment() && baseUrl.Scheme != Uri.UriSchemeHttps)
            p.Warnings.Add("App:PublicBaseUrl no usa https: los enlaces de los correos viajarán sin cifrar.");

        if (uploads.MaxFiles is < 1 or > 10 || uploads.MaxFileSizeMB is < 1 or > 10)
            p.Fatal.Add("Uploads:MaxFiles debe estar entre 1 y 10 y Uploads:MaxFileSizeMB entre 1 y 10.");

        var accounts = cfg.GetSection(PaymentOptions.Section).Get<PaymentOptions>()?.BankAccounts ?? new();
        if (accounts.Count == 0 || accounts.Any(a => a.Holder.Contains("ejemplo", StringComparison.OrdinalIgnoreCase) || a.Number.StartsWith("000-", StringComparison.Ordinal)))
            p.Warnings.Add("Payment:BankAccounts tiene datos de EJEMPLO o está vacío: los clientes verían cuentas falsas. Configura las cuentas reales antes de publicar.");

        return p;
    }
}
