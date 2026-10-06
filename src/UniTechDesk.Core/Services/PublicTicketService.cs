using Microsoft.Extensions.Logging;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Files;
using UniTechDesk.Core.Options;
using UniTechDesk.Core.Validation;

namespace UniTechDesk.Core.Services;

/// <summary>Operaciones del cliente: crear solicitud, consultar con código + correo, responder la cotización, informar el pago.</summary>
public sealed class PublicTicketService
{
    public const string NotFoundMessage = "No encontramos un ticket con ese código y correo. Revisa que estén bien escritos.";

    private readonly ITicketRepository _tickets;
    private readonly CatalogService _catalogs;
    private readonly IAttachmentStore _store;
    private readonly ICaptchaVerifier _captcha;
    private readonly MailPlanner _mail;
    private readonly BusinessClock _clock;
    private readonly TicketMapper _mapper;
    private readonly IOutboxSignal _signal;
    private readonly AppOptions _app;
    private readonly UploadOptions _uploads;
    private readonly ILogger<PublicTicketService> _log;

    public PublicTicketService(ITicketRepository tickets, CatalogService catalogs, IAttachmentStore store, ICaptchaVerifier captcha,
        MailPlanner mail, BusinessClock clock, TicketMapper mapper, IOutboxSignal signal, AppOptions app, UploadOptions uploads,
        ILogger<PublicTicketService> log)
    {
        _tickets = tickets; _catalogs = catalogs; _store = store; _captcha = captcha; _mail = mail; _clock = clock;
        _mapper = mapper; _signal = signal; _app = app; _uploads = uploads; _log = log;
    }

    public async Task<CreatedTicketResponse> CreateAsync(CreateTicketRequest request, IReadOnlyList<UploadedFile> files, string? remoteIp, CancellationToken ct)
    {
        // Campo trampa: una persona nunca lo ve. Se rechaza sin pistas de por qué.
        if (!string.IsNullOrEmpty(request.Hp)) throw AppException.Unprocessable("No pudimos procesar tu solicitud. Recarga la página e inténtalo de nuevo.");

        if (_captcha.Enabled && !await _captcha.VerifyAsync(request.CaptchaToken, remoteIp, ct))
            throw AppException.Unprocessable("No pudimos verificar que eres una persona. Completa la verificación e inténtalo de nuevo.");

        var catalogs = await _catalogs.GetAsync(ct);
        var now = _clock.UtcNow;
        var ticket = TicketIntake.Validate(request, catalogs, _clock.Today, now, _app.TermsVersion);

        // Adjuntos: se revisan TODOS antes de escribir nada en disco.
        if (files.Count > _uploads.MaxFiles) throw AppException.Unprocessable($"Máximo {_uploads.MaxFiles} archivos por solicitud.");
        var allowed = ticket.ServiceType == Codes.Service.Software ? FileInspector.RequestSoftwareExts : FileInspector.RequestImageExts;
        var maxBytes = _uploads.MaxFileSizeMB * 1024 * 1024;
        var inspected = files.Select(f => (File: f, Info: FileInspector.Inspect(f.FileName, f.Content, allowed, maxBytes))).ToList();

        var saved = new List<string>();
        try
        {
            foreach (var (file, info) in inspected)
            {
                var stored = await _store.SaveAsync(file.Content, info.Extension, ct);
                saved.Add(stored);
                ticket.Attachments.Add(new AttachmentRecord
                {
                    Category = Codes.AttachmentCategory.Request, OriginalName = info.DisplayName, StoredName = stored,
                    Mime = info.Mime, Size = file.Content.Length, Sha256 = System.Security.Cryptography.SHA256.HashData(file.Content), UploadedAt = now
                });
            }

            _mail.ToClient(ticket.Outbox, 0, Codes.Mail.TicketCreated, ticket.Email);
            _mail.ToStaff(ticket.Outbox, 0, Codes.Mail.StaffNewTicket);

            var created = await Retry.RunAsync(() => _tickets.CreateAsync(ticket, ct));
            _signal.Notify();
            _log.LogInformation("Ticket {Code} creado ({Service}, {Files} adjuntos).", created.Code, ticket.ServiceType, ticket.Attachments.Count);
            return new CreatedTicketResponse(created.Code, created.CreatedAt);
        }
        catch
        {
            foreach (var name in saved) _store.DeleteQuietly(name);   // no dejar archivos huérfanos si la BD falló
            throw;
        }
    }

    public async Task<PublicTicketDto> TrackAsync(string? code, string? email, CancellationToken ct)
    {
        var t = await FindOrThrowAsync(code, email, ct);
        return _mapper.ToPublic(t);
    }

    public Task<PublicTicketDto> DecideQuoteAsync(string? code, string? email, string? decision, CancellationToken ct) =>
        Retry.RunAsync(async () =>
        {
            var t = await FindOrThrowAsync(code, email, ct);
            if (decision is not (Codes.Decision.Approved or Codes.Decision.Rejected)) throw AppException.BadRequest("Decisión no válida.");

            var q = t.Quote;
            if (!(t.Status == Codes.Status.AwaitingApproval && q is { Published: true, Decision: null }))
                throw AppException.Conflict("Esta cotización ya no está pendiente de tu respuesta.");
            if (q.ValidUntil is { } until && until < _clock.Today)
                throw AppException.Conflict("Esta cotización venció. Escríbenos para que la actualicemos.");

            var now = _clock.UtcNow;
            var change = new TicketChangeSet { TicketId = t.Id, ExpectedVersion = t.RowVersion, Now = now, QuoteOp = QuoteOp.Decide, QuoteId = q.Id, Decision = decision, DecidedBy = Codes.DecidedBy.Client };
            if (decision == Codes.Decision.Approved)
            {
                if (q.Amount > 0m) change.NewPaymentStatus = Codes.PaymentStatus.Pending;
                change.History.Add(Event(now, Codes.Event.QuoteDecision, "Cotización aprobada por el cliente."));
                _mail.ToClient(change.Outbox, t.Id, Codes.Mail.QuoteApproved, t.Email);
            }
            else
            {
                change.NewStatus = Codes.Status.Cancelled;
                change.History.Add(Event(now, Codes.Event.QuoteDecision, "Cotización rechazada por el cliente."));
                var h = Event(now, Codes.Event.Status, "Cancelado: el cliente no aceptó la cotización.");
                h.From = t.Status; h.To = Codes.Status.Cancelled;
                change.History.Add(h);
                _mail.ToClient(change.Outbox, t.Id, Codes.Mail.QuoteRejected, t.Email);
            }
            _mail.ToStaff(change.Outbox, t.Id, Codes.Mail.StaffQuoteDecision);

            await _tickets.ApplyAsync(change, ct);
            _signal.Notify();
            return _mapper.ToPublic(await ReloadAsync(t.Id, ct));
        });

    public async Task<PublicTicketDto> SubmitPaymentProofAsync(string? code, string? email, string? reference, UploadedFile? file, CancellationToken ct)
    {
        var reference2 = Rules.Clean(reference);
        if (reference2.Length is < 4 or > 40)
            throw AppException.Unprocessable("Revisa el número de referencia.",
                new Dictionary<string, string> { ["reference"] = "Escribe la referencia de tu pago (4 a 40 caracteres)." });

        InspectedFile? info = null;
        if (file is not null) info = FileInspector.Inspect(file.FileName, file.Content, FileInspector.ProofExts, _uploads.MaxFileSizeMB * 1024 * 1024);

        string? storedName = null;
        var committed = false;
        try
        {
            var result = await Retry.RunAsync(async () =>
            {
                var t = await FindOrThrowAsync(code, email, ct);
                if (!Workflow.RequiresPayment(t) || t.PaymentStatus == Codes.PaymentStatus.Paid)
                    throw AppException.Conflict("Este ticket no tiene un pago pendiente.");

                var now = _clock.UtcNow;
                var change = new TicketChangeSet
                {
                    TicketId = t.Id, ExpectedVersion = t.RowVersion, Now = now, PaymentOp = PaymentOp.ReportProof,
                    PaymentQuoteId = t.Quote!.Id, PaymentMethod = t.PreferredPaymentMethod, PaymentReference = reference2,
                    NewPaymentStatus = Codes.PaymentStatus.ProofSent
                };
                if (file is not null && info is not null)
                {
                    storedName ??= await _store.SaveAsync(file.Content, info.Extension, ct);
                    change.NewProof = new AttachmentRecord
                    {
                        Category = Codes.AttachmentCategory.PaymentProof, OriginalName = info.DisplayName, StoredName = storedName,
                        Mime = info.Mime, Size = file.Content.Length, Sha256 = System.Security.Cryptography.SHA256.HashData(file.Content), UploadedAt = now
                    };
                }
                change.History.Add(Event(now, Codes.Event.Payment, $"Comprobante recibido (referencia {reference2}). Lo estamos verificando."));
                _mail.ToClient(change.Outbox, t.Id, Codes.Mail.ProofReceived, t.Email);
                _mail.ToStaff(change.Outbox, t.Id, Codes.Mail.StaffProofReceived);
                await _tickets.ApplyAsync(change, ct);
                committed = true;
                return t.Id;
            });
            _signal.Notify();
            return _mapper.ToPublic(await ReloadAsync(result, ct));
        }
        catch
        {
            // Si el cambio ya se confirmó, el archivo es parte del ticket: solo se borra si la operación NO llegó a guardarse.
            if (storedName is not null && !committed) _store.DeleteQuietly(storedName);
            throw;
        }
    }

    // ---------- Apoyo ----------

    private async Task<TicketRecord> FindOrThrowAsync(string? code, string? email, CancellationToken ct)
    {
        var c = Rules.NormalizeCode(code);
        var e = Rules.Clean(email);
        // El mismo mensaje para "no existe" y "correo distinto": no se revela qué códigos existen.
        if (!Rules.IsTicketCode(c) || !Rules.IsEmail(e)) throw AppException.NotFound(NotFoundMessage);
        return await _tickets.FindByCodeAndEmailAsync(c, e, ct) ?? throw AppException.NotFound(NotFoundMessage);
    }

    /// <summary>Lectura posterior a la confirmación, con reintento propio (ver StaffTicketService.ReloadAdminAsync).</summary>
    private async Task<TicketRecord> ReloadAsync(int id, CancellationToken ct) =>
        await Retry.RunAsync(async () => await _tickets.FindByIdAsync(id, ct) ?? throw AppException.NotFound(NotFoundMessage));

    private static HistoryRecord Event(DateTime now, string type, string text) =>
        new() { At = now, AuthorType = Codes.Author.Client, AuthorName = "Cliente", Type = type, Text = text, Internal = false };
}
