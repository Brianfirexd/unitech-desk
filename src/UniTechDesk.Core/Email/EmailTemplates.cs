using System.Text;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Options;
using UniTechDesk.Core.Services;

namespace UniTechDesk.Core.Email;

/// <summary>
/// Plantillas de correo en español (texto plano + HTML). Todo valor que escribió una persona (nombre, mensajes del personal)
/// se escapa para HTML. Devuelve null cuando el correo ya no aplica (por ejemplo, la cotización fue retirada).
/// </summary>
public sealed class EmailTemplates
{
    private readonly AppOptions _app;
    private readonly TicketMapper _mapper;

    public EmailTemplates(AppOptions app, TicketMapper mapper) { _app = app; _mapper = mapper; }

    public string TrackUrl(TicketRecord t) => $"{_app.PublicBaseUrl.TrimEnd('/')}/#/consultar?codigo={Uri.EscapeDataString(t.Code)}";
    private string AdminUrl() => $"{_app.PublicBaseUrl.TrimEnd('/')}/#/admin";

    public EmailMessage? Render(string template, string recipient, TicketRecord t)
    {
        var b = new Body(_app);
        string subject;
        var first = FirstName(t.FullName);
        string? buttonText = "Consultar mi ticket";
        var buttonUrl = TrackUrl(t);
        var service = WorkflowCatalog.ServiceTypes.TryGetValue(t.ServiceType, out var sv) ? sv : t.ServiceType;

        switch (template)
        {
            case Codes.Mail.TicketCreated:
                subject = $"Recibimos tu solicitud {t.Code}";
                b.P($"Hola {first}, recibimos tu solicitud de {service.ToLowerInvariant()}.");
                b.Highlight("Tu código de ticket", t.Code);
                b.P("Guárdalo: con este código y tu correo puedes consultar el avance en cualquier momento. Te avisaremos por este medio cuando haya novedades.");
                b.P("Todavía no se te cobra nada: primero revisamos tu solicitud y, si corresponde, te enviamos una cotización para que la apruebes.");
                break;

            case Codes.Mail.StatusUpdate:
                if (t.Status == Codes.Status.Quoted) return null;          // paso interno: el cliente aún no ve la cotización
                subject = $"Tu ticket {t.Code}: {Workflow.StatusLabel(t.Status)}";
                b.P($"Hola {first}, tu ticket {t.Code} ahora está en «{Workflow.StatusLabel(t.Status)}».");
                var msg = LastPublicStatusText(t);
                if (!string.IsNullOrWhiteSpace(msg)) b.Quote(Workflow.NeedsReason(t.Status) ? "Motivo" : "Mensaje del equipo", msg!);
                break;

            case Codes.Mail.QuotePublished:
                if (t.Quote is not { Published: true, Decision: null } q1 || t.Status != Codes.Status.AwaitingApproval) return null;
                subject = $"Tu cotización {t.Code} está lista";
                b.P($"Hola {first}, preparamos la cotización de tu ticket {t.Code}.");
                b.Highlight("Monto", q1.Amount > 0 ? Money.Format(q1.Amount, q1.Currency) : "Sin costo");
                b.Quote("Incluye", q1.Description);
                if (q1.ValidUntil is { } v) b.P($"Vigente hasta el {SpanishDate(v)}.");
                b.P("Para continuar, entra a «Consultar ticket» y aprueba o rechaza la cotización. No empezamos el trabajo hasta que la apruebes.");
                buttonText = "Ver y responder la cotización";
                break;

            case Codes.Mail.QuoteApproved:
                if (t.Quote?.Decision != Codes.Decision.Approved) return null;
                subject = $"Cotización aprobada · {t.Code}";
                b.P($"Hola {first}, registramos la aprobación de tu cotización del ticket {t.Code}. ¡Gracias!");
                var pay = _mapper.Instructions(t);
                if (pay is not null)
                {
                    b.Highlight("Monto a pagar", Money.Format(t.Quote.Amount, t.Quote.Currency));
                    b.P(pay.Note);
                    foreach (var a in pay.Accounts) b.Quote($"{a.Bank} · {(a.Currency == "USD" ? "Dólares" : "Córdobas")}", $"Cuenta: {a.Number}\nTitular: {a.Holder}");
                    if (pay.Method == Codes.PaymentMethod.Transfer) b.P("Cuando pagues, entra a «Consultar ticket» y registra el número de referencia (puedes adjuntar el comprobante).");
                }
                else b.P("Esta cotización no tiene costo, así que no hay nada que pagar. Continuamos con el trabajo.");
                break;

            case Codes.Mail.QuoteRejected:
                subject = $"Cancelamos tu solicitud {t.Code}";
                b.P($"Hola {first}, registramos que no aceptaste la cotización, así que cancelamos el ticket {t.Code}.");
                b.P("Si fue un error o quieres que revisemos otra opción, escríbenos y lo reabrimos.");
                break;

            case Codes.Mail.ProofReceived:
                subject = $"Recibimos tu comprobante · {t.Code}";
                b.P($"Hola {first}, recibimos tu comprobante de pago del ticket {t.Code}" +
                    (string.IsNullOrEmpty(t.Payment?.Reference) ? "." : $" (referencia {t.Payment!.Reference})."));
                b.P("Lo estamos verificando. Te avisaremos cuando el pago esté confirmado.");
                break;

            case Codes.Mail.PaymentConfirmed:
                if (t.PaymentStatus != Codes.PaymentStatus.Paid) return null;
                subject = $"Pago confirmado · {t.Code}";
                b.P($"Hola {first}, confirmamos el pago de tu ticket {t.Code}. ¡Gracias!");
                break;

            case Codes.Mail.ReadyForPickup:
                if (t.Status != Codes.Status.Ready) return null;
                subject = $"Tu ticket {t.Code} está listo para entrega";
                b.P(t.ServiceType == Codes.Service.Hardware
                    ? $"Hola {first}, tu equipo del ticket {t.Code} está listo para retirar."
                    : $"Hola {first}, tu trabajo del ticket {t.Code} está listo para entrega.");
                var note = LastPublicStatusText(t);
                if (!string.IsNullOrWhiteSpace(note)) b.Quote("Mensaje del equipo", note!);
                if (Workflow.RequiresPayment(t) && t.PaymentStatus != Codes.PaymentStatus.Paid)
                    b.P("Recuerda que debes completar el pago para poder recibirlo.");
                b.P("Presenta tu código de ticket al retirarlo.");
                break;

            // ----- Avisos para el personal (no llevan datos de contacto del cliente) -----
            case Codes.Mail.StaffNewTicket:
                subject = $"[{_app.Name}] Nueva solicitud {t.Code}";
                b.P($"Entró una nueva solicitud: {t.Code}.");
                b.Quote("Resumen", $"Servicio: {service}\nUrgencia: {Label(WorkflowCatalog.Urgencies, t.Urgency)}\nSolicitante: {t.FullName}");
                buttonText = "Abrir el panel"; buttonUrl = AdminUrl();
                break;

            case Codes.Mail.StaffQuoteDecision:
                if (t.Quote?.Decision is null) return null;
                subject = $"[{_app.Name}] Cotización {(t.Quote.Decision == Codes.Decision.Approved ? "aprobada" : "rechazada")} · {t.Code}";
                b.P($"El cliente {(t.Quote.Decision == Codes.Decision.Approved ? "aprobó" : "rechazó")} la cotización del ticket {t.Code}.");
                buttonText = "Abrir el panel"; buttonUrl = AdminUrl();
                break;

            case Codes.Mail.StaffProofReceived:
                subject = $"[{_app.Name}] Comprobante de pago recibido · {t.Code}";
                b.P($"El cliente informó su pago del ticket {t.Code}" + (string.IsNullOrEmpty(t.Payment?.Reference) ? "." : $" (referencia {t.Payment!.Reference})."));
                b.P("Verifícalo y confirma el pago en el panel.");
                buttonText = "Abrir el panel"; buttonUrl = AdminUrl();
                break;

            default:
                return null;
        }

        var isStaff = template.StartsWith("STAFF_", StringComparison.Ordinal);
        if (!isStaff && !string.IsNullOrWhiteSpace(_app.ContactLine)) b.Small(_app.ContactLine);
        if (!isStaff) b.Small("Este correo es automático por una solicitud hecha en nuestro sitio. Si no fuiste tú, puedes ignorarlo.");
        return new EmailMessage(recipient, subject, b.ToText(buttonText, buttonUrl), b.ToHtml(subject, buttonText, buttonUrl));
    }

    // ---------- Apoyo ----------
    private static readonly string[] Months = { "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre" };

    /// <summary>"20 de octubre de 2026". Nombres propios: no depende de que el servidor tenga instalados los datos de cultura.</summary>
    public static string SpanishDate(DateOnly d) => $"{d.Day:00} de {Months[d.Month - 1]} de {d.Year}";

    private static string FirstName(string full)
    {
        var f = full.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return f.Length == 0 ? "" : f;
    }

    private static string Label(IReadOnlyDictionary<string, string> d, string code) => d.TryGetValue(code, out var v) ? v : code;

    /// <summary>Último mensaje visible para el cliente que acompañó el cambio al estado actual.</summary>
    private static string? LastPublicStatusText(TicketRecord t) =>
        t.History.Where(e => e.Type == Codes.Event.Status && !e.Internal && e.To == t.Status).OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Select(e => e.Text).FirstOrDefault();

    /// <summary>Armador de cuerpo: cada pieza sale en texto y en HTML (con escape).</summary>
    private sealed class Body
    {
        private readonly AppOptions _app;
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _html = new();
        private readonly StringBuilder _footerText = new();
        private readonly StringBuilder _footerHtml = new();

        public Body(AppOptions app) => _app = app;
        /// <summary>Escapa lo que HTML interpreta (& < > " '). Las tildes y la ñ van tal cual: el documento declara UTF-8.</summary>
        private static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

        public void P(string s)
        {
            _text.Append(s).Append("\n\n");
            _html.Append("<p style=\"margin:0 0 14px;line-height:1.5\">").Append(E(s)).Append("</p>");
        }

        public void Highlight(string label, string value)
        {
            _text.Append(label).Append(": ").Append(value).Append("\n\n");
            _html.Append("<p style=\"margin:0 0 14px\"><span style=\"display:block;font-size:12px;color:#475569\">").Append(E(label))
                 .Append("</span><span style=\"display:block;font-size:24px;font-weight:700;color:#0f2a5c\">").Append(E(value)).Append("</span></p>");
        }

        public void Quote(string label, string value)
        {
            _text.Append(label).Append(":\n").Append(value).Append("\n\n");
            _html.Append("<div style=\"margin:0 0 14px;padding:10px 14px;border-left:4px solid #1d4ed8;background:#f1f5f9\"><div style=\"font-size:12px;color:#475569\">")
                 .Append(E(label)).Append("</div><div style=\"white-space:pre-wrap;line-height:1.5\">").Append(E(value)).Append("</div></div>");
        }

        public void Small(string s)
        {
            _footerText.Append(s).Append('\n');
            _footerHtml.Append("<p style=\"margin:0 0 6px;font-size:12px;color:#64748b;line-height:1.4\">").Append(E(s)).Append("</p>");
        }

        public string ToText(string? buttonText, string buttonUrl)
        {
            var sb = new StringBuilder(_text.ToString());
            if (buttonText is not null) sb.Append(buttonText).Append(": ").Append(buttonUrl).Append("\n\n");
            sb.Append("— ").Append(_app.Name).Append('\n');
            if (_footerText.Length > 0) sb.Append('\n').Append(_footerText);
            return sb.ToString();
        }

        public string ToHtml(string title, string? buttonText, string buttonUrl)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
              .Append(E(title)).Append("</title></head><body style=\"margin:0;background:#f1f5f9;font-family:Arial,Helvetica,sans-serif;color:#0f172a\">");
            sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\" style=\"padding:24px 12px\">");
            sb.Append("<table role=\"presentation\" width=\"100%\" style=\"max-width:560px;background:#ffffff;border-radius:8px\" cellpadding=\"0\" cellspacing=\"0\">");
            sb.Append("<tr><td style=\"background:#0f2a5c;color:#ffffff;padding:16px 24px;border-radius:8px 8px 0 0;font-size:18px;font-weight:700\">").Append(E(_app.Name)).Append("</td></tr>");
            sb.Append("<tr><td style=\"padding:24px\">").Append(_html);
            if (buttonText is not null)
                sb.Append("<p style=\"margin:20px 0\"><a href=\"").Append(E(buttonUrl)).Append("\" style=\"display:inline-block;background:#1d4ed8;color:#ffffff;text-decoration:none;font-weight:700;padding:12px 20px;border-radius:6px\">")
                  .Append(E(buttonText)).Append("</a></p><p style=\"margin:0 0 14px;font-size:12px;color:#64748b;word-break:break-all\">Si el botón no funciona, copia este enlace: ").Append(E(buttonUrl)).Append("</p>");
            sb.Append(_footerHtml).Append("</td></tr></table></td></tr></table></body></html>");
            return sb.ToString();
        }
    }
}
