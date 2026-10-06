using UniTechDesk.Core.Domain;

namespace UniTechDesk.Core.Abstractions;

public sealed record CatalogItem(int Id, string Name);
public sealed record Catalogs(IReadOnlyList<CatalogItem> Majors, IReadOnlyList<CatalogItem> Campuses);

public interface ICatalogRepository
{
    Task<Catalogs> GetCatalogsAsync(CancellationToken ct);
}

public sealed class TicketQuery
{
    public string? Status { get; init; }
    public string? Service { get; init; }
    public string? Urgency { get; init; }
    public string? Payment { get; init; }
    public string? Text { get; init; }
    public string Sort { get; init; } = "date_desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 10;
}

public sealed record TicketPage(IReadOnlyList<TicketRecord> Items, int Total, int Page, int PageSize);
public sealed record TicketStats(int Total, int Open, int Active, int PendingPayment, int UrgentActive);

public interface ITicketRepository
{
    /// <summary>Crea solicitante, ticket, detalle, adjuntos, historial inicial y correos en una sola transacción.</summary>
    Task<CreatedTicket> CreateAsync(NewTicket ticket, CancellationToken ct);

    /// <summary>Ticket completo (con historial, adjuntos, cotización vigente y pago) o null.</summary>
    Task<TicketRecord?> FindByIdAsync(int id, CancellationToken ct);

    /// <summary>Busca por código (UTD-1001) y correo. Devuelve null si no coinciden (no se distingue el motivo).</summary>
    Task<TicketRecord?> FindByCodeAndEmailAsync(string code, string email, CancellationToken ct);

    /// <summary>Página de tickets para el panel (sin historial ni adjuntos).</summary>
    Task<TicketPage> ListAsync(TicketQuery query, CancellationToken ct);

    Task<TicketStats> GetStatsAsync(CancellationToken ct);

    /// <summary>Metadatos de un adjunto (para descargarlo). Null si no existe.</summary>
    Task<AttachmentRecord?> FindAttachmentAsync(int id, CancellationToken ct);

    /// <summary>Aplica el cambio en una transacción. Lanza ConcurrencyException si la versión esperada ya no es la vigente.</summary>
    Task ApplyAsync(TicketChangeSet changes, CancellationToken ct);
}

public sealed class StaffAccount
{
    public int Id { get; init; }
    public string Username { get; init; } = "";
    public string FullName { get; init; } = "";
    public string? Email { get; init; }
    public string PasswordHash { get; init; } = "";
    public string Role { get; init; } = "";
    public bool Active { get; init; }
    public DateTime? LockedUntil { get; init; }
}

public interface IStaffRepository
{
    Task<StaffAccount?> FindByUsernameAsync(string username, CancellationToken ct);
    Task<StaffAccount?> FindByIdAsync(int id, CancellationToken ct);
    /// <summary>Personal activo, para el selector de "Asignar a".</summary>
    Task<IReadOnlyList<StaffRef>> ListActiveAsync(CancellationToken ct);
    Task RegisterFailedLoginAsync(int id, DateTime now, int maxAttempts, DateTime lockUntil, CancellationToken ct);
    Task RegisterSuccessfulLoginAsync(int id, DateTime now, CancellationToken ct);
}

public interface IOutboxRepository
{
    /// <summary>Reserva (con una "concesión" de tiempo) los correos vencidos y aumenta su contador de intentos.</summary>
    Task<IReadOnlyList<OutboxItem>> ClaimDueAsync(DateTime now, int batchSize, TimeSpan lease, CancellationToken ct);
    Task MarkSentAsync(long id, DateTime now, CancellationToken ct);
    /// <summary>Reprograma el envío; si "giveUp" es true queda en FAILED definitivo.</summary>
    Task MarkFailedAsync(long id, string error, DateTime nextAttempt, bool giveUp, CancellationToken ct);
}

/// <summary>Archivos adjuntos en disco (fuera de la carpeta pública).</summary>
public interface IAttachmentStore
{
    /// <summary>Guarda los bytes con un nombre generado por el servidor y devuelve ese nombre.</summary>
    Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct);
    Stream? OpenRead(string storedName);
    void DeleteQuietly(string storedName);
}

public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

public interface IEmailSender
{
    /// <summary>Envía un correo. Lanza EmailSendException con IsPermanent=true si reintentar no sirve.</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class EmailSendException : Exception
{
    public bool IsPermanent { get; }
    public EmailSendException(string message, bool isPermanent, Exception? inner = null) : base(message, inner) => IsPermanent = isPermanent;
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public sealed record IssuedToken(string Token, DateTime ExpiresAt);

public interface ITokenIssuer
{
    IssuedToken Issue(StaffAccount account, DateTime now);
}

public interface ICaptchaVerifier
{
    /// <summary>true si el token es válido (o si la verificación no está activada).</summary>
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
    bool Enabled { get; }
}

/// <summary>Despierta al proceso que envía correos cuando hay algo nuevo en la bandeja de salida.</summary>
public interface IOutboxSignal
{
    void Notify();
}

/// <summary>Enlaces firmados y de corta vida para descargar adjuntos (un &lt;a href&gt; no puede enviar el token Bearer).</summary>
public interface IFileLinkSigner
{
    string CreateUrl(int attachmentId, DateTime nowUtc);
    bool IsValid(int attachmentId, long expiresUnix, string signature, DateTime nowUtc);
}
