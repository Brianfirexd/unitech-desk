using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Options;

namespace UniTechDesk.Core.Services;

/// <summary>Convierte los registros de dominio en los JSON que espera el front.</summary>
public sealed class TicketMapper
{
    private static readonly string[] PublicEvents = { Codes.Event.Created, Codes.Event.Status, Codes.Event.QuoteDecision, Codes.Event.Payment };

    private readonly PaymentOptions _payment;
    private readonly IFileLinkSigner _signer;
    private readonly BusinessClock _clock;

    public TicketMapper(PaymentOptions payment, IFileLinkSigner signer, BusinessClock clock)
    {
        _payment = payment;
        _signer = signer;
        _clock = clock;
    }

    public static string DateText(DateOnly? d) => d is null ? "" : d.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static QuoteDto ToQuote(QuoteRecord q) =>
        new(q.Amount, q.Currency, q.Description, DateText(q.ValidUntil), q.Published, q.Decision, q.DecidedAt, q.DecidedBy);

    public PublicTicketDto ToPublic(TicketRecord t)
    {
        var quote = t.Quote is { Published: true } q ? ToQuote(q) : null;
        var timeline = t.History
            .Where(e => !e.Internal && PublicEvents.Contains(e.Type))
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => new TimelineEventDto(e.At, e.Type, e.Type is Codes.Event.Status or Codes.Event.Created ? e.To : null, e.Text))
            .ToList();
        return new PublicTicketDto(
            t.Code, t.CreatedAt, t.UpdatedAt, t.ServiceType, PublicStatus(t.Status), t.PaymentStatus, t.PreferredPaymentMethod,
            quote, quote?.Decision == Codes.Decision.Approved ? Instructions(t) : null,
            t.Payment?.Reference, timeline);
    }

    /// <summary>
    /// «Cotizado» es un paso interno (la cotización aún no se publica): el cliente sigue viendo «En revisión» hasta que se le
    /// envía la cotización («Esperando aprobación»). Así el estado que ve coincide con su historial y con lo que le llegó por correo.
    /// </summary>
    public static string PublicStatus(string status) => status == Codes.Status.Quoted ? Codes.Status.InReview : status;

    public PaymentInstructionsDto? Instructions(TicketRecord t)
    {
        if (!Workflow.RequiresPayment(t) || t.PaymentStatus == Codes.PaymentStatus.Paid) return null;
        switch (t.PreferredPaymentMethod)
        {
            case Codes.PaymentMethod.Transfer:
                var all = _payment.BankAccounts.Where(a => !string.IsNullOrWhiteSpace(a.Number)).ToList();
                var sameCurrency = all.Where(a => string.Equals(a.Currency, t.Quote!.Currency, StringComparison.OrdinalIgnoreCase)).ToList();
                var shown = (sameCurrency.Count > 0 ? sameCurrency : all).Select(a => new BankAccountDto(a.Bank, a.Currency, a.Number, a.Holder)).ToList();
                return new PaymentInstructionsDto(Codes.PaymentMethod.Transfer, _payment.TransferNote, shown);
            case Codes.PaymentMethod.Cash:
                return new PaymentInstructionsDto(Codes.PaymentMethod.Cash, _payment.CashNote, Array.Empty<BankAccountDto>());
            default:
                return new PaymentInstructionsDto(Codes.PaymentMethod.Card, _payment.CardNote, Array.Empty<BankAccountDto>());
        }
    }

    /// <summary>Detalle (con historial y adjuntos con enlace firmado) o fila de listado (sin ellos).</summary>
    public AdminTicketDto ToAdmin(TicketRecord t, bool detail)
    {
        var now = _clock.UtcNow;
        var hw = t.Hardware is null ? null
            : new HardwareOut(t.Hardware.EquipmentType, t.Hardware.Brand, t.Hardware.Model, t.Hardware.Serial, t.Hardware.PowersOn,
                t.Hardware.Accessories, t.Hardware.AccessoriesOther);
        var sw = t.Software is null ? null : new SoftwareOut(t.Software.Kind, DateText(t.Software.DesiredDate), t.Software.ReferenceUrl);
        var payment = t.Payment is { ReportedAt: not null } p ? new PaymentOut(p.Reference, p.ProofName ?? "", p.ReportedAt) : null;
        var attachments = detail
            ? t.Attachments.OrderBy(a => a.Id).Select(a => new AttachmentOut(a.Id, a.OriginalName, a.Size, a.Mime, _signer.CreateUrl(a.Id, now))).ToList()
            : new List<AttachmentOut>();
        var history = detail
            ? t.History.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new HistoryOut(e.At, e.AuthorName, e.Type, e.From, e.To, e.Text, e.Internal)).ToList()
            : new List<HistoryOut>();
        return new AdminTicketDto(
            t.Id, t.Code, t.CreatedAt, t.UpdatedAt, t.RequesterType, t.FullName, t.Email, t.Phone,
            t.StudentId, t.MajorId, t.MajorName, t.CampusId, t.CampusName, t.Company, t.Ruc,
            t.ServiceType, hw, sw, t.Description, t.Urgency, t.PreferredPaymentMethod, t.Status, t.PaymentStatus,
            t.Assignee is null ? null : new StaffRefDto(t.Assignee.Id, t.Assignee.Name),
            t.Quote is null ? null : ToQuote(t.Quote), payment, attachments, history);
    }
}
