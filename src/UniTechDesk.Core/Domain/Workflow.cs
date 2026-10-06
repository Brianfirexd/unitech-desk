namespace UniTechDesk.Core.Domain;

/// <summary>Información de un estado del ticket (se genera en WorkflowCatalog.g.cs desde constants.js).</summary>
public sealed record StatusInfo(string Label, string[] Services, bool IsFinal);

/// <summary>Reglas del flujo. Réplica de UTD.rules (js/constants.js): el front solo guía, esta clase decide.</summary>
public static class Workflow
{
    public static string StatusLabel(string code) =>
        WorkflowCatalog.Statuses.TryGetValue(code, out var s) ? s.Label : code;

    public static bool IsFinal(string status) =>
        WorkflowCatalog.Statuses.TryGetValue(status, out var s) && s.IsFinal;

    /// <summary>El ticket necesita cobro cuando hay una cotización aprobada con monto mayor a cero.</summary>
    public static bool RequiresPayment(TicketRecord t) =>
        t.Quote is { Decision: Codes.Decision.Approved } q && q.Amount > 0m;

    /// <summary>La cotización solo se puede editar antes de enviarla al cliente.</summary>
    public static bool QuoteEditable(string status) => status is Codes.Status.InReview or Codes.Status.Quoted;

    public static IReadOnlyList<string> AllowedTransitions(string serviceType, string status)
    {
        if (!WorkflowCatalog.Transitions.TryGetValue(status, out var all)) return Array.Empty<string>();
        return all.Where(to => WorkflowCatalog.Statuses.TryGetValue(to, out var s) && s.Services.Contains(serviceType)).ToArray();
    }

    /// <summary>Estados que exigen escribir un motivo.</summary>
    public static bool NeedsReason(string to) => to is Codes.Status.Cancelled or Codes.Status.Unrepairable;

    /// <summary>Estados en los que el trabajo ya inició (la BD exige cotización aprobada: trigger TR_Ticket_ReglasDeNegocio).</summary>
    public static bool IsWorkStatus(string status) =>
        status is Codes.Status.InProgress or Codes.Status.WaitingParts or Codes.Status.InDevelopment or Codes.Status.Testing;

    /// <summary>
    /// Motivo por el que NO se puede pasar a "to", o null si se puede. Mismas reglas y mismos mensajes que
    /// UTD.rules.transitionBlocker del front.
    /// </summary>
    public static string? TransitionBlocker(TicketRecord t, string to, bool requirePaymentBeforeDelivery)
    {
        if (!AllowedTransitions(t.ServiceType, t.Status).Contains(to))
            return $"No se puede pasar de «{StatusLabel(t.Status)}» a «{StatusLabel(to)}».";

        var q = t.Quote;
        if (to == Codes.Status.Quoted && q is null) return "Primero guarda la cotización.";
        if (to == Codes.Status.AwaitingApproval && q is null) return "Primero guarda la cotización para poder enviarla al cliente.";
        if (to == Codes.Status.Quoted && t.Status == Codes.Status.AwaitingApproval && q?.Decision is not null)
            return "El cliente ya respondió la cotización; no se puede retirar.";
        if (to is Codes.Status.InProgress or Codes.Status.InDevelopment && q?.Decision != Codes.Decision.Approved)
            return "El trabajo inicia cuando el cliente aprueba la cotización.";
        if (to == Codes.Status.Delivered && requirePaymentBeforeDelivery && RequiresPayment(t) && t.PaymentStatus != Codes.PaymentStatus.Paid)
            return "No se puede entregar hasta confirmar el pago.";
        return null;
    }
}
