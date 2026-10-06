namespace UniTechDesk.Core.Contracts;

public sealed record CreatedTicketResponse(string Code, DateTime CreatedAt);

public sealed record QuoteDto(
    decimal Amount, string Currency, string Description, string ValidUntil,
    bool Published, string? Decision, DateTime? DecidedAt, string? DecidedBy);

public sealed record BankAccountDto(string Bank, string Currency, string Number, string Holder);
public sealed record PaymentInstructionsDto(string Method, string Note, IReadOnlyList<BankAccountDto> Accounts);
public sealed record TimelineEventDto(DateTime At, string Type, string? Status, string Text);

/// <summary>Lo que ve el cliente en "Consultar ticket". Nunca incluye datos personales ni notas internas.</summary>
public sealed record PublicTicketDto(
    string Code, DateTime CreatedAt, DateTime UpdatedAt, string ServiceType, string Status, string PaymentStatus,
    string PreferredPaymentMethod, QuoteDto? Quote, PaymentInstructionsDto? PaymentInstructions,
    string? PaymentReference, IReadOnlyList<TimelineEventDto> Timeline);

public sealed record StaffRefDto(int Id, string Name);

public sealed record HardwareOut(string EquipmentType, string Brand, string Model, string Serial, string PowersOn,
    IReadOnlyList<string> Accessories, string AccessoriesOther);

public sealed record SoftwareOut(string Kind, string DesiredDate, string ReferenceUrl);

public sealed record PaymentOut(string? Reference, string ProofName, DateTime? ReportedAt);

public sealed record AttachmentOut(int Id, string Name, int Size, string Type, string? Url);

public sealed record HistoryOut(DateTime At, string By, string Type, string? From, string? To, string Text, bool Internal);

/// <summary>Ticket para el panel del personal (listado y detalle). En el listado "history" y "attachments" van vacíos.</summary>
public sealed record AdminTicketDto(
    int Id, string Code, DateTime CreatedAt, DateTime UpdatedAt,
    string RequesterType, string FullName, string Email, string Phone,
    string StudentId, int? MajorId, string MajorName, int? CampusId, string CampusName, string Company, string Ruc,
    string ServiceType, HardwareOut? Hardware, SoftwareOut? Software,
    string Description, string Urgency, string PreferredPaymentMethod, string Status, string PaymentStatus,
    StaffRefDto? Assignee, QuoteDto? Quote, PaymentOut? Payment,
    IReadOnlyList<AttachmentOut> Attachments, IReadOnlyList<HistoryOut> History);

public sealed record TicketPageDto(IReadOnlyList<AdminTicketDto> Items, int Total, int Page, int PageSize);
public sealed record StatsDto(int Total, int Open, int Active, int PendingPayment, int UrgentActive);

public sealed record UserDto(string Name, string Role);
public sealed record LoginResponse(string Token, DateTime ExpiresAt, UserDto User);
