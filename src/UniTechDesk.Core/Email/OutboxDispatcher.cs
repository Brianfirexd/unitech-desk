using Microsoft.Extensions.Logging;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Options;
using UniTechDesk.Core.Services;

namespace UniTechDesk.Core.Email;

/// <summary>
/// Envía los correos de la bandeja de salida (NotificacionCorreo). Reserva los pendientes con una "concesión", los renderiza con
/// el estado ACTUAL del ticket y los manda. Un fallo se reintenta con espera creciente; pasados los intentos queda en FAILED.
/// </summary>
public sealed class OutboxDispatcher
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly int[] BackoffMinutes = { 1, 5, 15, 60, 180, 360 };

    private readonly IOutboxRepository _outbox;
    private readonly ITicketRepository _tickets;
    private readonly IEmailSender _sender;
    private readonly EmailTemplates _templates;
    private readonly BusinessClock _clock;
    private readonly EmailOptions _options;
    private readonly ILogger<OutboxDispatcher> _log;

    public OutboxDispatcher(IOutboxRepository outbox, ITicketRepository tickets, IEmailSender sender, EmailTemplates templates,
        BusinessClock clock, EmailOptions options, ILogger<OutboxDispatcher> log)
    {
        _outbox = outbox; _tickets = tickets; _sender = sender; _templates = templates; _clock = clock; _options = options; _log = log;
    }

    /// <summary>Procesa un lote. Devuelve cuántos correos se enviaron.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var items = await _outbox.ClaimDueAsync(_clock.UtcNow, Math.Max(1, _options.BatchSize), Lease, ct);
        var sent = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ticket = await _tickets.FindByIdAsync(item.TicketId, ct);
                if (ticket is null) { await GiveUpAsync(item, "El ticket ya no existe.", ct); continue; }

                var message = _templates.Render(item.Template, item.Recipient, ticket);
                if (message is null) { await GiveUpAsync(item, "Omitido: el correo ya no aplica al estado actual del ticket.", ct); continue; }

                await _sender.SendAsync(message, ct);
                await _outbox.MarkSentAsync(item.Id, _clock.UtcNow, ct);
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (EmailSendException ex)
            {
                await FailAsync(item, ex.Message, ex.IsPermanent, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error inesperado al enviar el correo #{Id} ({Template}).", item.Id, item.Template);
                await FailAsync(item, "Error inesperado: " + ex.GetType().Name, permanent: false, ct);
            }
        }
        return sent;
    }

    private Task GiveUpAsync(OutboxItem item, string reason, CancellationToken ct) =>
        _outbox.MarkFailedAsync(item.Id, Truncate(reason), _clock.UtcNow, giveUp: true, ct);

    private async Task FailAsync(OutboxItem item, string error, bool permanent, CancellationToken ct)
    {
        var giveUp = permanent || item.Attempts >= _options.MaxAttempts;
        var wait = BackoffMinutes[Math.Min(Math.Max(item.Attempts, 1), BackoffMinutes.Length) - 1];
        _log.LogWarning("Correo #{Id} ({Template}) no enviado (intento {Attempt}): {Error}{Final}", item.Id, item.Template, item.Attempts, error, giveUp ? " — se descarta" : "");
        await _outbox.MarkFailedAsync(item.Id, Truncate(error), _clock.UtcNow.AddMinutes(wait), giveUp, ct);
    }

    private static string Truncate(string s) => s.Length <= 480 ? s : s[..480];
}
