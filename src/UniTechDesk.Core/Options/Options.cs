namespace UniTechDesk.Core.Options;

public sealed class AppOptions
{
    public const string Section = "App";
    /// <summary>Nombre que ven los clientes en los correos.</summary>
    public string Name { get; set; } = "UniTech Desk";
    /// <summary>URL pública del sitio (para el enlace "Consultar mi ticket" de los correos). Sin barra final.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";
    public string TermsVersion { get; set; } = "1.0";
    /// <summary>Zona horaria del negocio, para decidir si una cotización venció "hoy".</summary>
    public string TimeZoneId { get; set; } = "America/Managua";
    /// <summary>Datos de contacto que se agregan al pie de los correos (opcional).</summary>
    public string ContactLine { get; set; } = "";
}

public sealed class UploadOptions
{
    public const string Section = "Uploads";
    public int MaxFiles { get; set; } = 5;
    public int MaxFileSizeMB { get; set; } = 5;
    /// <summary>Carpeta (fuera de wwwroot) donde se guardan los archivos.</summary>
    public string StoragePath { get; set; } = "storage/attachments";
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "UniTechDesk";
    public string Audience { get; set; } = "UniTechDesk.Staff";
    /// <summary>Clave HMAC de al menos 32 caracteres. Va en user-secrets o en una variable de entorno, NUNCA en el repositorio.</summary>
    public string SigningKey { get; set; } = "";
    public int ExpiresMinutes { get; set; } = 60;
}

public sealed class LoginOptions
{
    public const string Section = "Login";
    public int MaxAttempts { get; set; } = 5;
    public int LockMinutes { get; set; } = 15;
}

public sealed class PaymentOptions
{
    public const string Section = "Payment";
    public List<BankAccount> BankAccounts { get; set; } = new();
    public string CashNote { get; set; } = "Paga en la caja de la universidad y presenta tu código de ticket.";
    public string CardNote { get; set; } = "El personal te enviará un enlace de pago seguro de la pasarela de pagos. Nunca te pediremos datos de tarjeta por chat ni por correo.";
    public string TransferNote { get; set; } = "Realiza la transferencia por el monto de tu cotización y luego registra el número de referencia aquí abajo.";
}

public sealed class BankAccount
{
    public string Bank { get; set; } = "";
    public string Currency { get; set; } = "NIO";
    public string Number { get; set; } = "";
    public string Holder { get; set; } = "";
}

public sealed class EmailOptions
{
    public const string Section = "Email";
    /// <summary>"SendGrid" envía de verdad; "Console" solo escribe en el registro (desarrollo); "None" desactiva los envíos.</summary>
    public string Provider { get; set; } = "Console";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "UniTech Desk";
    public string ReplyToEmail { get; set; } = "";
    public string ReplyToName { get; set; } = "";
    /// <summary>Clave de SendGrid (permiso "Mail Send"). Solo en user-secrets / variable de entorno.</summary>
    public string SendGridApiKey { get; set; } = "";
    /// <summary>Direcciones del personal que reciben avisos (ticket nuevo, comprobante recibido…). Vacío = sin avisos al personal.</summary>
    public List<string> StaffNotifyTo { get; set; } = new();
    public int BatchSize { get; set; } = 20;
    public int PollSeconds { get; set; } = 15;
    public int MaxAttempts { get; set; } = 6;
}

public sealed class CaptchaOptions
{
    public const string Section = "Captcha";
    /// <summary>Clave secreta de Cloudflare Turnstile. Vacía = sin verificación (solo campo trampa + límite de peticiones).</summary>
    public string TurnstileSecret { get; set; } = "";
}
