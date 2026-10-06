namespace UniTechDesk.Core.Domain;

public sealed class HardwareInfo
{
    public string EquipmentType { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string PowersOn { get; set; } = "";
    public List<string> Accessories { get; set; } = new();
    public string AccessoriesOther { get; set; } = "";
}

public sealed class SoftwareInfo
{
    public string Kind { get; set; } = "";
    public DateOnly? DesiredDate { get; set; }
    public string ReferenceUrl { get; set; } = "";
}

public sealed class StaffRef
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class QuoteRecord
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Description { get; set; } = "";
    public DateOnly? ValidUntil { get; set; }
    public bool Published { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? Decision { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
}

public sealed class PaymentRecord
{
    public int Id { get; set; }
    public int QuoteId { get; set; }
    public string MethodCode { get; set; } = "";
    public string? Reference { get; set; }
    public int? ProofAttachmentId { get; set; }
    public string? ProofName { get; set; }
    public DateTime? ReportedAt { get; set; }
    public int? ConfirmedById { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}

public sealed class AttachmentRecord
{
    public int Id { get; set; }
    public string Category { get; set; } = Codes.AttachmentCategory.Request;
    public string OriginalName { get; set; } = "";
    public string StoredName { get; set; } = "";
    public string Mime { get; set; } = "";
    public int Size { get; set; }
    public byte[]? Sha256 { get; set; }
    public DateTime UploadedAt { get; set; }
}

public sealed class HistoryRecord
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string AuthorType { get; set; } = Codes.Author.System;
    public int? StaffId { get; set; }
    public string AuthorName { get; set; } = "Sistema";
    public string Type { get; set; } = "";
    public string? From { get; set; }
    public string? To { get; set; }
    public string Text { get; set; } = "";
    public bool Internal { get; set; }
}

/// <summary>Ticket completo tal como lo lee y modifica la capa de servicios.</summary>
public sealed class TicketRecord
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string RequesterType { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string StudentId { get; set; } = "";
    public int? MajorId { get; set; }
    public string MajorName { get; set; } = "";
    public int? CampusId { get; set; }
    public string CampusName { get; set; } = "";
    public string Company { get; set; } = "";
    public string Ruc { get; set; } = "";

    public string ServiceType { get; set; } = "";
    public HardwareInfo? Hardware { get; set; }
    public SoftwareInfo? Software { get; set; }
    public string Description { get; set; } = "";
    public string Urgency { get; set; } = "";
    public string PreferredPaymentMethod { get; set; } = "";

    public string Status { get; set; } = Codes.Status.Open;
    public string PaymentStatus { get; set; } = Codes.PaymentStatus.None;
    public StaffRef? Assignee { get; set; }

    public QuoteRecord? Quote { get; set; }
    public PaymentRecord? Payment { get; set; }
    public List<AttachmentRecord> Attachments { get; set; } = new();
    public List<HistoryRecord> History { get; set; } = new();

    /// <summary>ROWVERSION de la fila de Ticket: control de concurrencia optimista (la BD lo cambia en cada UPDATE).</summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>Ticket nuevo (todavía sin Id) con sus adjuntos ya guardados en disco.</summary>
public sealed class NewTicket
{
    public string RequesterType { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? StudentId { get; set; }
    public int? MajorId { get; set; }
    public int? CampusId { get; set; }
    public string? Company { get; set; }
    public string? Ruc { get; set; }
    public string ServiceType { get; set; } = "";
    public HardwareInfo? Hardware { get; set; }
    public SoftwareInfo? Software { get; set; }
    public string Description { get; set; } = "";
    public string Urgency { get; set; } = "";
    public string PreferredPaymentMethod { get; set; } = "";
    public DateTime AcceptedTermsAt { get; set; }
    public string TermsVersion { get; set; } = "1.0";
    public DateTime Now { get; set; }
    public List<AttachmentRecord> Attachments { get; set; } = new();
    public List<OutboxItem> Outbox { get; set; } = new();
}

public sealed record CreatedTicket(int Id, string Code, DateTime CreatedAt);

/// <summary>Correo pendiente de enviar (tabla NotificacionCorreo).</summary>
public sealed class OutboxItem
{
    public long Id { get; set; }
    public int TicketId { get; set; }
    public string Template { get; set; } = "";
    public string Recipient { get; set; } = "";
    public int Attempts { get; set; }
}

/// <summary>Un cambio de quote dentro de un TicketChangeSet.</summary>
public enum QuoteOp
{
    None = 0,
    /// <summary>Descarta la cotización vigente (si hay) e inserta una nueva sin publicar.</summary>
    Replace,
    Publish,
    Unpublish,
    Decide,
    Discard
}

public enum PaymentOp
{
    None = 0,
    /// <summary>El cliente informó su pago (referencia y comprobante).</summary>
    ReportProof,
    /// <summary>El personal confirma el pago.</summary>
    Confirm,
    /// <summary>El personal revierte la confirmación (vuelve a pendiente o en verificación).</summary>
    Unconfirm
}

/// <summary>
/// Todo lo que cambia una operación sobre un ticket existente. El repositorio lo aplica en UNA transacción:
/// primero el UPDATE de Ticket con la versión esperada (ROWVERSION), después cotización, pago, adjunto, historial y correos.
/// </summary>
public sealed class TicketChangeSet
{
    public int TicketId { get; init; }
    public byte[] ExpectedVersion { get; init; } = Array.Empty<byte>();
    public DateTime Now { get; init; }

    public string? NewStatus { get; set; }
    public string? NewPaymentStatus { get; set; }
    public bool ChangeAssignee { get; set; }
    public int? NewAssigneeId { get; set; }

    public QuoteOp QuoteOp { get; set; }
    public QuoteRecord? NewQuote { get; set; }           // Replace
    public int? QuoteId { get; set; }                     // Publish / Unpublish / Decide / Discard
    public string? Decision { get; set; }                 // Decide
    public string? DecidedBy { get; set; }                // Decide
    public int CreatedById { get; set; }                  // Replace: quién la crea (Personal.Id)

    public PaymentOp PaymentOp { get; set; }
    public int? PaymentQuoteId { get; set; }
    public string? PaymentMethod { get; set; }
    public string? PaymentReference { get; set; }
    public AttachmentRecord? NewProof { get; set; }
    public int? ConfirmedById { get; set; }

    public List<HistoryRecord> History { get; } = new();
    public List<OutboxItem> Outbox { get; } = new();
}
