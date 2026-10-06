namespace UniTechDesk.Core.Contracts;

// Cuerpos que envía el front. Los nombres JSON salen en camelCase (así los espera js/api.js).

public sealed class HardwareDto
{
    public string? EquipmentType { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? PowersOn { get; set; }
    public List<string>? Accessories { get; set; }
    public string? AccessoriesOther { get; set; }
}

public sealed class SoftwareDto
{
    public string? Kind { get; set; }
    public string? DesiredDate { get; set; }
    public string? ReferenceUrl { get; set; }
}

public sealed class CreateTicketRequest
{
    public string? RequesterType { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? StudentId { get; set; }
    public int? MajorId { get; set; }
    public int? CampusId { get; set; }
    public string? Company { get; set; }
    public string? Ruc { get; set; }
    public string? ServiceType { get; set; }
    public HardwareDto? Hardware { get; set; }
    public SoftwareDto? Software { get; set; }
    public string? Description { get; set; }
    public string? Urgency { get; set; }
    public string? PreferredPaymentMethod { get; set; }
    public bool? BackupAck { get; set; }
    public bool? AcceptedTerms { get; set; }
    public string? CaptchaToken { get; set; }
    /// <summary>Campo trampa: las personas no lo ven, los bots lo llenan.</summary>
    public string? Hp { get; set; }
}

public sealed class TrackRequest
{
    public string? Code { get; set; }
    public string? Email { get; set; }
}

public sealed class ClientQuoteDecisionRequest
{
    public string? Email { get; set; }
    public string? Decision { get; set; }
}

public sealed class PaymentProofRequest
{
    public string? Email { get; set; }
    public string? Reference { get; set; }
}

public sealed class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class StatusChangeRequest
{
    public string? To { get; set; }
    public string? Note { get; set; }
}

public sealed class SaveQuoteRequest
{
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Description { get; set; }
    public string? ValidUntil { get; set; }
}

public sealed class StaffQuoteDecisionRequest
{
    public string? Note { get; set; }
}

public sealed class SetPaymentRequest
{
    public string? Status { get; set; }
}

public sealed class AssignRequest
{
    public int? StaffId { get; set; }
}

public sealed class NoteRequest
{
    public string? Text { get; set; }
}

/// <summary>Archivo ya leído de la petición (el endpoint lo copia a memoria con un tope de tamaño).</summary>
public sealed record UploadedFile(string FileName, string? ContentType, byte[] Content);
