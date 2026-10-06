using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Options;

namespace UniTechDesk.Core.Services;

/// <summary>Reintenta una operación completa (releyendo el ticket) cuando otra persona lo modificó a la vez o hubo un interbloqueo.</summary>
public static class Retry
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation, int attempts = 3)
    {
        for (var i = 1; ; i++)
        {
            try { return await operation(); }
            catch (ConcurrencyException) when (i < attempts) { }
            catch (TransientStoreException) when (i < attempts) { await Task.Delay(40 * i); }
            catch (ConcurrencyException) { throw AppException.Conflict("El ticket cambió mientras lo editabas. Recarga la página e inténtalo de nuevo."); }
            catch (TransientStoreException) { throw new AppException(503, "El servidor está ocupado. Inténtalo de nuevo en unos segundos."); }
        }
    }
}

/// <summary>Decide qué correos genera cada evento y los deja listos para la bandeja de salida.</summary>
public sealed class MailPlanner
{
    private readonly EmailOptions _options;
    public MailPlanner(EmailOptions options) => _options = options;

    public bool Enabled => !string.Equals(_options.Provider, "None", StringComparison.OrdinalIgnoreCase);

    public void ToClient(IList<OutboxItem> outbox, int ticketId, string template, string clientEmail)
    {
        if (!Enabled) return;
        outbox.Add(new OutboxItem { TicketId = ticketId, Template = template, Recipient = clientEmail });
    }

    public void ToStaff(IList<OutboxItem> outbox, int ticketId, string template)
    {
        if (!Enabled) return;
        foreach (var to in _options.StaffNotifyTo.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            outbox.Add(new OutboxItem { TicketId = ticketId, Template = template, Recipient = to });
    }

    /// <summary>Cambios de estado que el cliente debe recibir por correo. Los pasos internos o muy ruidosos no se envían.</summary>
    public static string? TemplateForStatus(string from, string to) => to switch
    {
        Codes.Status.Quoted => null,                                   // paso interno: el cliente aún no ve la cotización
        Codes.Status.Testing => null,                                  // demasiado frecuente
        Codes.Status.InDevelopment when from == Codes.Status.Testing => null,
        Codes.Status.AwaitingApproval => Codes.Mail.QuotePublished,
        Codes.Status.Ready => Codes.Mail.ReadyForPickup,
        _ => Codes.Mail.StatusUpdate
    };
}

public static class Money
{
    public static string Format(decimal amount, string currency) =>
        (currency == "USD" ? "US$ " : "C$ ") + amount.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Catálogos de carreras y recintos con una pequeña caché (cambian muy poco). Si dos peticiones refrescan a la vez, no pasa nada.</summary>
public sealed class CatalogService
{
    private readonly ICatalogRepository _repo;
    private readonly TimeProvider _time;
    private volatile Cached? _cached;

    private sealed record Cached(Catalogs Data, DateTimeOffset Expires);

    public CatalogService(ICatalogRepository repo, TimeProvider time) { _repo = repo; _time = time; }

    public async Task<Catalogs> GetAsync(CancellationToken ct)
    {
        var c = _cached;
        if (c is not null && _time.GetUtcNow() < c.Expires) return c.Data;
        var fresh = await _repo.GetCatalogsAsync(ct);
        _cached = new Cached(fresh, _time.GetUtcNow().AddMinutes(5));
        return fresh;
    }
}
