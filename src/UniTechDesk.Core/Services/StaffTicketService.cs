using Microsoft.Extensions.Logging;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Validation;

namespace UniTechDesk.Core.Services;

/// <summary>Quién hace el cambio (sale del token del personal).</summary>
public sealed record StaffActor(int Id, string Name);

public sealed class TicketListParams
{
    public string? Status { get; init; }
    public string? Service { get; init; }
    public string? Urgency { get; init; }
    public string? Payment { get; init; }
    public string? Q { get; init; }
    public string? Sort { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>Operaciones del panel de administración. Aplica las mismas reglas que UTD.rules y que la mock-api del front.</summary>
public sealed class StaffTicketService
{
    private static readonly string[] SortModes = { "date_desc", "date_asc", "urgency_desc" };

    private readonly ITicketRepository _tickets;
    private readonly IStaffRepository _staff;
    private readonly MailPlanner _mail;
    private readonly BusinessClock _clock;
    private readonly TicketMapper _mapper;
    private readonly IOutboxSignal _signal;
    private readonly ILogger<StaffTicketService> _log;

    public StaffTicketService(ITicketRepository tickets, IStaffRepository staff, MailPlanner mail, BusinessClock clock,
        TicketMapper mapper, IOutboxSignal signal, ILogger<StaffTicketService> log)
    {
        _tickets = tickets; _staff = staff; _mail = mail; _clock = clock; _mapper = mapper; _signal = signal; _log = log;
    }

    // ---------- Lecturas ----------

    public async Task<TicketPageDto> ListAsync(TicketListParams p, CancellationToken ct)
    {
        void CheckIn(string? value, IEnumerable<string> allowed, string name)
        {
            if (!string.IsNullOrEmpty(value) && !allowed.Contains(value)) throw AppException.BadRequest($"Filtro no válido: {name}.");
        }
        CheckIn(p.Status, WorkflowCatalog.Statuses.Keys, "estado");
        CheckIn(p.Service, WorkflowCatalog.ServiceTypes.Keys, "servicio");
        CheckIn(p.Urgency, WorkflowCatalog.Urgencies.Keys, "urgencia");
        CheckIn(p.Payment, WorkflowCatalog.PaymentStatuses.Keys, "pago");
        CheckIn(p.Sort, SortModes, "orden");

        var text = Rules.Clean(p.Q);
        if (text.Length > 100) text = text[..100];
        var query = new TicketQuery
        {
            Status = NullIfEmpty(p.Status), Service = NullIfEmpty(p.Service), Urgency = NullIfEmpty(p.Urgency), Payment = NullIfEmpty(p.Payment),
            Text = NullIfEmpty(text), Sort = string.IsNullOrEmpty(p.Sort) ? "date_desc" : p.Sort,
            Page = Math.Max(p.Page ?? 1, 1), PageSize = Math.Min(Math.Max(p.PageSize ?? 10, 1), 1000)
        };
        var page = await _tickets.ListAsync(query, ct);
        return new TicketPageDto(page.Items.Select(t => _mapper.ToAdmin(t, detail: false)).ToList(), page.Total, page.Page, page.PageSize);
    }

    public async Task<StatsDto> GetStatsAsync(CancellationToken ct)
    {
        var s = await _tickets.GetStatsAsync(ct);
        return new StatsDto(s.Total, s.Open, s.Active, s.PendingPayment, s.UrgentActive);
    }

    public async Task<AdminTicketDto> GetAsync(int id, CancellationToken ct) => _mapper.ToAdmin(await LoadAsync(id, ct), detail: true);

    public async Task<IReadOnlyList<StaffRefDto>> ListStaffAsync(CancellationToken ct) =>
        (await _staff.ListActiveAsync(ct)).Select(s => new StaffRefDto(s.Id, s.Name)).ToList();

    // ---------- Cambios ----------

    public Task<AdminTicketDto> ChangeStatusAsync(StaffActor actor, int id, StatusChangeRequest req, CancellationToken ct) =>
        Retry.RunAsync(async () =>
        {
            var t = await LoadAsync(id, ct);
            var to = req.To ?? "";
            var note = Rules.Clean(req.Note, allowNewlines: true);
            if (!WorkflowCatalog.Statuses.ContainsKey(to)) throw AppException.BadRequest("Estado no válido.");
            var blocker = Workflow.TransitionBlocker(t, to, requirePaymentBeforeDelivery: true);
            if (blocker is not null) throw AppException.Conflict(blocker);
            if (Workflow.NeedsReason(to) && note.Length < 5)
                throw AppException.Unprocessable("Falta el motivo.", new Dictionary<string, string> { ["note"] = "Escribe el motivo (mínimo 5 caracteres). El cliente lo verá." });
            if (note.Length > 500)
                throw AppException.Unprocessable("El mensaje es muy largo.", new Dictionary<string, string> { ["note"] = "Máximo 500 caracteres." });

            var from = t.Status;
            var now = _clock.UtcNow;
            var change = new TicketChangeSet { TicketId = t.Id, ExpectedVersion = t.RowVersion, Now = now, NewStatus = to };

            if (to == Codes.Status.AwaitingApproval)
            {
                if (t.Quote!.ValidUntil is { } until && until < _clock.Today)
                    throw AppException.Conflict("La vigencia de la cotización ya pasó; actualízala antes de enviarla al cliente.");
                change.QuoteOp = QuoteOp.Publish; change.QuoteId = t.Quote.Id;
            }
            else if (to == Codes.Status.Quoted && from == Codes.Status.AwaitingApproval)
            {
                // Retirar la cotización (solo posible mientras el cliente no responda): vuelve a ser interna.
                change.QuoteOp = QuoteOp.Unpublish; change.QuoteId = t.Quote!.Id;
            }
            else if (to == Codes.Status.InReview && from == Codes.Status.Cancelled && t.Quote is not null)
            {
                // Reabrir un ticket cancelado: la cotización anterior se descarta (queda en el histórico) y se hace una nueva.
                if (t.PaymentStatus == Codes.PaymentStatus.Paid)
                    throw AppException.Conflict("Este ticket ya tiene un pago confirmado; no se puede reabrir. Registra una nota interna o crea un ticket nuevo.");
                change.QuoteOp = QuoteOp.Discard; change.QuoteId = t.Quote.Id;
                if (t.PaymentStatus != Codes.PaymentStatus.None) change.NewPaymentStatus = Codes.PaymentStatus.None;
            }

            change.History.Add(Staff(actor, now, Codes.Event.Status, note, internalOnly: to == Codes.Status.Quoted, from, to));   // "Cotizado" es un paso interno

            var template = MailPlanner.TemplateForStatus(from, to);
            if (template is not null) _mail.ToClient(change.Outbox, t.Id, template, t.Email);

            await _tickets.ApplyAsync(change, ct);
            _signal.Notify();
            _log.LogInformation("Ticket {Code}: {From} → {To} por personal #{StaffId}.", t.Code, from, to, actor.Id);
            return await ReloadAdminAsync(id, ct);
        });

    public Task<AdminTicketDto> SaveQuoteAsync(StaffActor actor, int id, SaveQuoteRequest req, CancellationToken ct) =>
        Retry.RunAsync(async () =>
        {
            var t = await LoadAsync(id, ct);
            if (!Workflow.QuoteEditable(t.Status))
                throw AppException.Conflict("La cotización solo se puede editar mientras el ticket está en revisión o cotizado.");
            if (t.PaymentStatus == Codes.PaymentStatus.Paid)
                throw AppException.Conflict("El pago de la cotización vigente ya fue confirmado; no se puede reemplazar.");

            var e = new FieldErrors();
            decimal amount = 0;
            if (req.Amount is not { } raw || raw < 0m || raw > 999_999_999.99m) e.Add("amount", "Escribe un monto válido (0 si no tiene costo).");
            else amount = Math.Round(raw, 2, MidpointRounding.AwayFromZero);
            var currency = req.Currency ?? "";
            e.Need("currency", WorkflowCatalog.Currencies.ContainsKey(currency), "Selecciona la moneda.");
            var desc = Rules.Clean(req.Description, allowNewlines: true);
            e.Need("description", desc.Length >= 5, "Describe qué incluye la cotización (mínimo 5 caracteres).");
            if (desc.Length > 500) e.Add("description", "Máximo 500 caracteres.");
            DateOnly? until = null;
            var rawUntil = (req.ValidUntil ?? "").Trim();
            if (rawUntil.Length > 0)
            {
                if (!Rules.TryParseDate(rawUntil, out var d)) e.Add("validUntil", "Fecha no válida.");
                else if (d < _clock.Today) e.Add("validUntil", "La vigencia no puede ser una fecha pasada.");
                else until = d;
            }
            e.ThrowIfAny("Revisa la cotización.");

            var now = _clock.UtcNow;
            var change = new TicketChangeSet
            {
                TicketId = t.Id, ExpectedVersion = t.RowVersion, Now = now, QuoteOp = QuoteOp.Replace, CreatedById = actor.Id,
                NewQuote = new QuoteRecord { Amount = amount, Currency = currency, Description = desc, ValidUntil = until }
            };
            if (t.PaymentStatus != Codes.PaymentStatus.None) change.NewPaymentStatus = Codes.PaymentStatus.None;
            change.History.Add(Staff(actor, now, Codes.Event.Quote, $"Cotización guardada: {Money.Format(amount, currency)}.", internalOnly: true));
            await _tickets.ApplyAsync(change, ct);
            return await ReloadAdminAsync(id, ct);
        });

    /// <summary>El personal registra que el cliente aprobó por teléfono o WhatsApp.</summary>
    public Task<AdminTicketDto> RecordQuoteDecisionAsync(StaffActor actor, int id, StaffQuoteDecisionRequest req, CancellationToken ct) =>
        Retry.RunAsync(async () =>
        {
            var t = await LoadAsync(id, ct);
            var q = t.Quote;
            if (!(t.Status == Codes.Status.AwaitingApproval && q is { Published: true, Decision: null }))
                throw AppException.Conflict("No hay una cotización pendiente de aprobación.");
            var note = Rules.Clean(req.Note);
            if (note.Length > 300) throw AppException.Unprocessable("La nota es muy larga.", new Dictionary<string, string> { ["note"] = "Máximo 300 caracteres." });

            var now = _clock.UtcNow;
            var change = new TicketChangeSet { TicketId = t.Id, ExpectedVersion = t.RowVersion, Now = now, QuoteOp = QuoteOp.Decide, QuoteId = q.Id, Decision = Codes.Decision.Approved, DecidedBy = Codes.DecidedBy.Staff };
            if (q.Amount > 0m) change.NewPaymentStatus = Codes.PaymentStatus.Pending;
            change.History.Add(Staff(actor, now, Codes.Event.QuoteDecision,
                "Cotización aprobada por el cliente (confirmada por el personal)." + (note.Length > 0 ? " " + note : ""), internalOnly: false));
            _mail.ToClient(change.Outbox, t.Id, Codes.Mail.QuoteApproved, t.Email);
            await _tickets.ApplyAsync(change, ct);
            _signal.Notify();
            return await ReloadAdminAsync(id, ct);
        });

    public Task<AdminTicketDto> SetPaymentAsync(StaffActor actor, int id, SetPaymentRequest req, CancellationToken ct) =>
        Retry.RunAsync(async () =>
        {
            var t = await LoadAsync(id, ct);
            var status = req.Status ?? "";
            if (!Workflow.RequiresPayment(t))
                throw AppException.Conflict("El pago se habilita cuando la cotización es aprobada y tiene un monto mayor a cero.");
            if (status is not (Codes.PaymentStatus.Pending or Codes.PaymentStatus.ProofSent or Codes.PaymentStatus.Paid))
                throw AppException.BadRequest("Estado de pago no válido.");
            if (t.PaymentStatus == status) return _mapper.ToAdmin(t, detail: true);

            var now = _clock.UtcNow;
            var change = new TicketChangeSet
            {
                TicketId = t.Id, ExpectedVersion = t.RowVersion, Now = now, NewPaymentStatus = status,
                PaymentQuoteId = t.Quote!.Id, PaymentMethod = t.PreferredPaymentMethod
            };
            if (status == Codes.PaymentStatus.Paid)
            {
                change.PaymentOp = PaymentOp.Confirm; change.ConfirmedById = actor.Id;
                change.History.Add(Staff(actor, now, Codes.Event.Payment, "Pago confirmado. ¡Gracias!", internalOnly: false));
                _mail.ToClient(change.Outbox, t.Id, Codes.Mail.PaymentConfirmed, t.Email);
            }
            else
            {
                if (t.PaymentStatus == Codes.PaymentStatus.Paid) change.PaymentOp = PaymentOp.Unconfirm;
                change.History.Add(Staff(actor, now, Codes.Event.Payment,
                    $"Estado de pago actualizado a «{WorkflowCatalog.PaymentStatuses[status]}».", internalOnly: true));
            }
            await _tickets.ApplyAsync(change, ct);
            _signal.Notify();
            return await ReloadAdminAsync(id, ct);
        });

    public Task<AdminTicketDto> AssignAsync(StaffActor actor, int id, AssignRequest req, CancellationToken ct) =>
        Retry.RunAsync(async () =>
        {
            var t = await LoadAsync(id, ct);
            var now = _clock.UtcNow;
            var change = new TicketChangeSet { TicketId = t.Id, ExpectedVersion = t.RowVersion, Now = now, ChangeAssignee = true };
            if (req.StaffId is null)
            {
                if (t.Assignee is null) return _mapper.ToAdmin(t, detail: true);
                change.NewAssigneeId = null;
                change.History.Add(Staff(actor, now, Codes.Event.Assign, "Se quitó la asignación.", internalOnly: true));
            }
            else
            {
                var target = (await _staff.ListActiveAsync(ct)).FirstOrDefault(s => s.Id == req.StaffId.Value)
                    ?? throw AppException.BadRequest("Persona asignada no válida.");
                if (t.Assignee?.Id == target.Id) return _mapper.ToAdmin(t, detail: true);
                change.NewAssigneeId = target.Id;
                change.History.Add(Staff(actor, now, Codes.Event.Assign, $"Asignado a {target.Name}.", internalOnly: true));
            }
            await _tickets.ApplyAsync(change, ct);
            return await ReloadAdminAsync(id, ct);
        });

    public Task<AdminTicketDto> AddNoteAsync(StaffActor actor, int id, NoteRequest req, CancellationToken ct) =>
        Retry.RunAsync(async () =>
        {
            var t = await LoadAsync(id, ct);
            var text = Rules.Clean(req.Text, allowNewlines: true);
            if (text.Length < 2) throw AppException.Unprocessable("La nota está vacía.", new Dictionary<string, string> { ["text"] = "Escribe la nota." });
            if (text.Length > 1000) throw AppException.Unprocessable("La nota es muy larga.", new Dictionary<string, string> { ["text"] = "Máximo 1000 caracteres." });
            var now = _clock.UtcNow;
            var change = new TicketChangeSet { TicketId = t.Id, ExpectedVersion = t.RowVersion, Now = now };
            change.History.Add(Staff(actor, now, Codes.Event.Note, text, internalOnly: true));
            await _tickets.ApplyAsync(change, ct);
            return await ReloadAdminAsync(id, ct);
        });

    // ---------- Apoyo ----------

    /// <summary>
    /// Lee el ticket ya guardado para devolverlo. Tiene su propio reintento, separado del de la operación: si esta lectura falla
    /// por un interbloqueo, NO se debe repetir la operación completa (duplicaría la nota o la versión de cotización ya confirmadas).
    /// </summary>
    private async Task<AdminTicketDto> ReloadAdminAsync(int id, CancellationToken ct) =>
        _mapper.ToAdmin(await Retry.RunAsync(() => LoadAsync(id, ct)), detail: true);

    private async Task<TicketRecord> LoadAsync(int id, CancellationToken ct) =>
        await _tickets.FindByIdAsync(id, ct) ?? throw AppException.NotFound("No encontramos ese ticket.");

    private static HistoryRecord Staff(StaffActor actor, DateTime now, string type, string text, bool internalOnly, string? from = null, string? to = null) =>
        new() { At = now, AuthorType = Codes.Author.Staff, StaffId = actor.Id, AuthorName = actor.Name, Type = type, Text = text, Internal = internalOnly, From = from, To = to };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
