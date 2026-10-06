using System.Net;
using System.Text;
using System.Text.Json;
using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Email;
using UniTechDesk.Core.Options;
using UniTechDesk.Core.Services;
using UniTechDesk.TestSupport;
using UniTechDesk.Tests.Support;

namespace UniTechDesk.Tests;

public class MailPlannerTests
{
    [Theory]
    [InlineData("OPEN", "IN_REVIEW", "STATUS_UPDATE")]
    [InlineData("IN_REVIEW", "QUOTED", null)]
    [InlineData("QUOTED", "AWAITING_APPROVAL", "QUOTE_PUBLISHED")]
    [InlineData("AWAITING_APPROVAL", "QUOTED", null)]
    [InlineData("AWAITING_APPROVAL", "IN_PROGRESS", "STATUS_UPDATE")]
    [InlineData("AWAITING_APPROVAL", "IN_DEVELOPMENT", "STATUS_UPDATE")]
    [InlineData("IN_DEVELOPMENT", "TESTING", null)]
    [InlineData("TESTING", "IN_DEVELOPMENT", null)]
    [InlineData("IN_PROGRESS", "READY", "READY_FOR_PICKUP")]
    [InlineData("COMPLETED", "READY", "READY_FOR_PICKUP")]
    [InlineData("READY", "DELIVERED", "STATUS_UPDATE")]
    [InlineData("OPEN", "CANCELLED", "STATUS_UPDATE")]
    [InlineData("IN_REVIEW", "UNREPAIRABLE", "STATUS_UPDATE")]
    public void Que_cambios_de_estado_avisan_al_cliente(string from, string to, string? expected) =>
        Assert.Equal(expected, MailPlanner.TemplateForStatus(from, to));

    [Theory]
    [InlineData("None", false)]
    [InlineData("none", false)]
    [InlineData("Console", true)]
    [InlineData("SendGrid", true)]
    public void El_proveedor_None_desactiva_los_correos(string provider, bool enabled) =>
        Assert.Equal(enabled, new MailPlanner(new EmailOptions { Provider = provider }).Enabled);
}

public class EmailTemplatesTests
{
    private static async Task<(Env env, int id, string code)> NewAsync(CreateTicketRequest? request = null)
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync(request);
        return (env, id, code);
    }

    private static async Task<TicketRecord> Load(Env env, int id) => (await env.Store.FindByIdAsync(id, default))!;

    private static EmailMessage Render(Env env, string template, TicketRecord t, string? to = null)
    {
        var m = env.Templates.Render(template, to ?? Env.ClientEmail, t);
        Assert.NotNull(m);
        return m!;
    }

    [Fact]
    public async Task Ticket_creado_lleva_el_codigo_el_enlace_y_el_pie()
    {
        var (env, id, code) = await NewAsync();
        var m = Render(env, "TICKET_CREATED", await Load(env, id));
        Assert.Equal(Env.ClientEmail, m.To);
        Assert.Equal("Recibimos tu solicitud UTD-1001", m.Subject);
        Assert.Contains("Hola Ana", m.TextBody);
        Assert.Contains(code, m.TextBody);
        Assert.Contains("https://soporte.example.edu/#/consultar?codigo=UTD-1001", m.TextBody);
        Assert.Contains("https://soporte.example.edu/#/consultar?codigo=UTD-1001", m.HtmlBody);
        Assert.Contains("Todavía no se te cobra nada", m.TextBody);
        Assert.Contains("Escríbenos a soporte@example.edu", m.TextBody);
        Assert.Contains("Si no fuiste tú", m.HtmlBody);
        Assert.StartsWith("<!doctype html>", m.HtmlBody);
    }

    [Fact]
    public async Task Lo_que_escribe_el_cliente_se_escapa_en_el_html()
    {
        var r = Env.StudentHardware();
        r.FullName = "<b>Ana</b> López";
        r.Description = "La laptop <script>alert('x')</script> no enciende desde ayer por la tarde.";
        var (env, id, _) = await NewAsync(r);
        var t = await Load(env, id);
        var m = Render(env, "TICKET_CREATED", t);
        Assert.Contains("&lt;b&gt;Ana&lt;/b&gt;", m.HtmlBody);
        Assert.DoesNotContain("<b>Ana</b>", m.HtmlBody);
        Assert.DoesNotContain("<script", m.HtmlBody);

        var staff = Render(env, "STAFF_NEW_TICKET", t, "soporte@example.edu");
        Assert.DoesNotContain("<b>Ana</b>", staff.HtmlBody);
        Assert.Contains("&lt;b&gt;Ana&lt;/b&gt; López", staff.HtmlBody);
    }

    [Fact]
    public async Task El_mensaje_del_personal_se_escapa_y_se_muestra_con_su_etiqueta()
    {
        var (env, id, _) = await NewAsync();
        await env.ToStatus(id, "IN_REVIEW", "Pasa por <img src=x onerror=alert(1)> recepción");
        var m = Render(env, "STATUS_UPDATE", await Load(env, id));
        Assert.Equal("Tu ticket UTD-1001: En revisión", m.Subject);
        Assert.Contains("Mensaje del equipo", m.HtmlBody);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", m.HtmlBody);
        Assert.DoesNotContain("<img", m.HtmlBody);
    }

    [Fact]
    public async Task Al_cancelar_el_motivo_se_etiqueta_como_Motivo()
    {
        var (env, id, _) = await NewAsync();
        await env.ToStatus(id, "CANCELLED", "Solicitud duplicada.");
        var m = Render(env, "STATUS_UPDATE", await Load(env, id));
        Assert.Contains("Motivo", m.TextBody);
        Assert.Contains("Solicitud duplicada.", m.TextBody);
    }

    [Fact]
    public async Task Ningun_asunto_contiene_saltos_de_linea()
    {
        var r = Env.StudentHardware(); r.FullName = "Ana\r\nBcc: victima@example.com";
        var (env, id, _) = await NewAsync(r);
        var t = await Load(env, id);
        foreach (var template in new[] { "TICKET_CREATED", "STATUS_UPDATE", "STAFF_NEW_TICKET" })
        {
            var m = Render(env, template, t);
            Assert.DoesNotContain("\n", m.Subject);
            Assert.DoesNotContain("\r", m.Subject);
        }
    }

    [Fact]
    public async Task Cotizacion_publicada_muestra_monto_detalle_y_vigencia()
    {
        var (env, id, _) = await NewAsync();
        await env.PublishQuoteAsync(id, 3000m, "NIO", "2026-10-20");
        var m = Render(env, "QUOTE_PUBLISHED", await Load(env, id));
        Assert.Equal("Tu cotización UTD-1001 está lista", m.Subject);
        Assert.Contains("C$ 3,000.00", m.TextBody);
        Assert.Contains("Limpieza interna y cambio de pasta térmica.", m.TextBody);
        Assert.Contains("Vigente hasta el 20 de octubre de 2026.", m.TextBody);
        Assert.Contains("Ver y responder la cotización", m.HtmlBody);
    }

    [Fact]
    public async Task Cotizacion_publicada_sin_costo_dice_Sin_costo()
    {
        var (env, id, _) = await NewAsync();
        await env.PublishQuoteAsync(id, 0m, "NIO", null);
        var m = Render(env, "QUOTE_PUBLISHED", await Load(env, id));
        Assert.Contains("Sin costo", m.TextBody);
        Assert.DoesNotContain("Vigente hasta", m.TextBody);
    }

    [Fact]
    public async Task Correos_que_ya_no_aplican_devuelven_null()
    {
        var (env, id, code) = await NewAsync();
        await env.ToStatus(id, "IN_REVIEW");
        await env.SaveQuote(id);
        Assert.Null(env.Templates.Render("QUOTE_PUBLISHED", Env.ClientEmail, await Load(env, id)));          // aún interna
        Assert.Null(env.Templates.Render("QUOTE_APPROVED", Env.ClientEmail, await Load(env, id)));
        Assert.Null(env.Templates.Render("PAYMENT_CONFIRMED", Env.ClientEmail, await Load(env, id)));
        Assert.Null(env.Templates.Render("READY_FOR_PICKUP", Env.ClientEmail, await Load(env, id)));
        Assert.Null(env.Templates.Render("STAFF_QUOTE_DECISION", "x@example.edu", await Load(env, id)));
        Assert.Null(env.Templates.Render("NO_EXISTE", Env.ClientEmail, await Load(env, id)));

        await env.ToStatus(id, "QUOTED");
        await env.ToStatus(id, "AWAITING_APPROVAL");
        Assert.NotNull(env.Templates.Render("QUOTE_PUBLISHED", Env.ClientEmail, await Load(env, id)));
        await env.ClientDecides(code, "APPROVED");
        Assert.Null(env.Templates.Render("QUOTE_PUBLISHED", Env.ClientEmail, await Load(env, id)));          // ya respondió
        Assert.NotNull(env.Templates.Render("QUOTE_APPROVED", Env.ClientEmail, await Load(env, id)));
    }

    [Fact]
    public async Task Aprobada_con_transferencia_muestra_solo_las_cuentas_de_la_moneda()
    {
        var (env, id, code) = await NewAsync();
        await env.PublishQuoteAsync(id, 850m, "NIO");
        await env.ClientDecides(code, "APPROVED");
        var m = Render(env, "QUOTE_APPROVED", await Load(env, id));
        Assert.Contains("C$ 850.00", m.TextBody);
        Assert.Contains("1000-0000-0001", m.TextBody);
        Assert.DoesNotContain("2000-0000-0002", m.TextBody);
        Assert.Contains("registra el número de referencia", m.TextBody);
    }

    [Fact]
    public async Task Aprobada_en_efectivo_no_lista_cuentas_y_sin_costo_no_pide_pago()
    {
        var r = Env.StudentHardware(); r.PreferredPaymentMethod = "CASH";
        var (env, id, code) = await NewAsync(r);
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");
        var cash = Render(env, "QUOTE_APPROVED", await Load(env, id));
        Assert.DoesNotContain("1000-0000-0001", cash.TextBody);
        Assert.Contains("Paga en la caja", cash.TextBody);

        var (env2, id2, code2) = await NewAsync();
        await env2.PublishQuoteAsync(id2, 0m);
        await env2.ClientDecides(code2, "APPROVED");
        var free = Render(env2, "QUOTE_APPROVED", await Load(env2, id2));
        Assert.Contains("no tiene costo", free.TextBody);
    }

    [Fact]
    public async Task Comprobante_recibido_incluye_la_referencia_y_pago_confirmado_solo_si_esta_pagado()
    {
        var (env, id, code) = await NewAsync();
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");
        await env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-7788", null, default);
        Assert.Contains("(referencia REF-7788)", Render(env, "PROOF_RECEIVED", await Load(env, id)).TextBody);
        Assert.Contains("(referencia REF-7788)", Render(env, "STAFF_PROOF_RECEIVED", await Load(env, id), "soporte@example.edu").TextBody);
        Assert.Null(env.Templates.Render("PAYMENT_CONFIRMED", Env.ClientEmail, await Load(env, id)));
        await env.Pay(id, "PAID");
        Assert.NotNull(env.Templates.Render("PAYMENT_CONFIRMED", Env.ClientEmail, await Load(env, id)));
    }

    [Fact]
    public async Task Listo_para_entrega_recuerda_el_pago_pendiente()
    {
        var (env, id, code) = await NewAsync();
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");
        await env.ToStatus(id, "IN_PROGRESS");
        await env.ToStatus(id, "READY", "Ya puedes pasar.");
        var m = Render(env, "READY_FOR_PICKUP", await Load(env, id));
        Assert.Equal("Tu ticket UTD-1001 está listo para entrega", m.Subject);
        Assert.Contains("tu equipo del ticket UTD-1001 está listo para retirar", m.TextBody);
        Assert.Contains("Ya puedes pasar.", m.TextBody);
        Assert.Contains("Recuerda que debes completar el pago", m.TextBody);
        await env.Pay(id, "PAID");
        Assert.DoesNotContain("Recuerda que debes completar el pago", Render(env, "READY_FOR_PICKUP", await Load(env, id)).TextBody);
    }

    [Fact]
    public async Task Los_avisos_al_personal_no_llevan_correo_ni_telefono_del_cliente_ni_el_pie_para_clientes()
    {
        var (env, id, _) = await NewAsync();
        var t = await Load(env, id);
        foreach (var template in new[] { "STAFF_NEW_TICKET" })
        {
            var m = Render(env, template, t, "soporte@example.edu");
            Assert.StartsWith("[UniTech Desk] Nueva solicitud UTD-1001", m.Subject);
            Assert.DoesNotContain(Env.ClientEmail, m.TextBody + m.HtmlBody);
            Assert.DoesNotContain(Env.ClientPhone, m.TextBody + m.HtmlBody);
            Assert.DoesNotContain("Si no fuiste tú", m.TextBody);
            Assert.Contains("https://soporte.example.edu/#/admin", m.TextBody);
        }
    }

    [Theory]
    [InlineData(2027, 1, 1, "01 de enero de 2027")]
    [InlineData(2026, 10, 20, "20 de octubre de 2026")]
    [InlineData(2026, 12, 31, "31 de diciembre de 2026")]
    public void Fechas_en_espanol_sin_depender_de_la_cultura_del_servidor(int year, int month, int day, string expected) =>
        Assert.Equal(expected, EmailTemplates.SpanishDate(new DateOnly(year, month, day)));
}

public class OutboxDispatcherTests
{
    private static async Task<Env> WithOneMailAsync(Action<Env>? tweak = null)
    {
        var env = new Env(tweak);
        await env.CreateAsync();
        return env;
    }

    [Fact]
    public async Task Envia_los_pendientes_una_sola_vez()
    {
        var env = await WithOneMailAsync();
        Assert.Equal(1, await env.Dispatcher.ProcessBatchAsync(default));
        var sent = Assert.Single(env.Sender.Snapshot());
        Assert.Equal(Env.ClientEmail, sent.To);
        Assert.Equal("Recibimos tu solicitud UTD-1001", sent.Subject);
        Assert.Equal("SENT", env.Store.Mails()[0].State);
        Assert.Equal(0, await env.Dispatcher.ProcessBatchAsync(default));
        Assert.Single(env.Sender.Snapshot());
    }

    [Fact]
    public async Task Un_fallo_temporal_se_reintenta_con_espera_creciente()
    {
        var env = await WithOneMailAsync();
        env.Sender.Failure = _ => new EmailSendException("SendGrid respondió 503: caído", isPermanent: false);

        await env.Dispatcher.ProcessBatchAsync(default);                                  // intento 1 → espera 1 min
        var m = env.Store.Mails()[0];
        Assert.Equal("PENDING", m.State); Assert.Equal(1, m.Attempts); Assert.Contains("503", m.LastError!);

        env.Time.Advance(TimeSpan.FromSeconds(59));
        await env.Dispatcher.ProcessBatchAsync(default);
        Assert.Equal(1, env.Store.Mails()[0].Attempts);                                   // todavía no toca
        env.Time.Advance(TimeSpan.FromSeconds(1));
        await env.Dispatcher.ProcessBatchAsync(default);                                  // intento 2 → espera 5 min
        Assert.Equal(2, env.Store.Mails()[0].Attempts);

        env.Time.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(59)));
        await env.Dispatcher.ProcessBatchAsync(default);
        Assert.Equal(2, env.Store.Mails()[0].Attempts);
        env.Time.Advance(TimeSpan.FromSeconds(1));
        await env.Dispatcher.ProcessBatchAsync(default);                                  // intento 3 → espera 15 min
        Assert.Equal(3, env.Store.Mails()[0].Attempts);

        env.Sender.Failure = null;                                                        // SendGrid se recupera
        env.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(1, await env.Dispatcher.ProcessBatchAsync(default));
        Assert.Equal("SENT", env.Store.Mails()[0].State);
        Assert.Single(env.Sender.Snapshot());
    }

    [Fact]
    public async Task Tras_el_maximo_de_intentos_queda_FAILED_y_no_se_vuelve_a_intentar()
    {
        var env = await WithOneMailAsync(e => e.Email.MaxAttempts = 3);
        env.Sender.Failure = _ => new EmailSendException("falla", false);
        for (var i = 0; i < 3; i++)
        {
            await env.Dispatcher.ProcessBatchAsync(default);
            env.Time.Advance(TimeSpan.FromHours(7));
        }
        var m = env.Store.Mails()[0];
        Assert.Equal("FAILED", m.State);
        Assert.Equal(3, m.Attempts);
        env.Sender.Failure = null;
        await env.Dispatcher.ProcessBatchAsync(default);
        Assert.Empty(env.Sender.Snapshot());
    }

    [Fact]
    public async Task Un_error_permanente_descarta_el_correo_de_inmediato()
    {
        var env = await WithOneMailAsync();
        env.Sender.Failure = _ => new EmailSendException("SendGrid respondió 400: Does not contain a valid address.", isPermanent: true);
        await env.Dispatcher.ProcessBatchAsync(default);
        var m = env.Store.Mails()[0];
        Assert.Equal("FAILED", m.State);
        Assert.Equal(1, m.Attempts);
        Assert.Contains("400", m.LastError!);
    }

    [Fact]
    public async Task Un_error_inesperado_se_reintenta_y_no_filtra_su_mensaje()
    {
        var env = await WithOneMailAsync();
        env.Sender.Failure = _ => new InvalidOperationException("detalle interno con ana.lopez@example.com");
        await env.Dispatcher.ProcessBatchAsync(default);
        var m = env.Store.Mails()[0];
        Assert.Equal("PENDING", m.State);
        Assert.Equal("Error inesperado: InvalidOperationException", m.LastError);
        Assert.DoesNotContain("ana.lopez", m.LastError!);
    }

    [Fact]
    public async Task Un_correo_que_ya_no_aplica_se_omite_con_un_motivo_claro()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);          // genera TICKET_CREATED, STATUS_UPDATE (en revisión) y QUOTE_PUBLISHED
        await env.ToStatus(id, "QUOTED");                // el personal retira la cotización antes de que salga el correo
        await env.Dispatcher.ProcessBatchAsync(default);

        // solo sale el de "solicitud recibida"; el de "En revisión" (ahora el ticket está en el paso interno Cotizado) y el de
        // la cotización retirada se omiten
        var sent = env.Sender.Snapshot().Select(m => m.Subject).ToList();
        Assert.Equal(new[] { "Recibimos tu solicitud UTD-1001" }, sent);
        var skipped = env.Store.Mails().Single(m => m.Template == "QUOTE_PUBLISHED");
        Assert.Equal("FAILED", skipped.State);
        Assert.StartsWith("Omitido", skipped.LastError!);
    }

    [Fact]
    public async Task El_correo_usa_el_estado_actual_no_el_del_momento_del_evento()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.ToStatus(id, "IN_REVIEW", "Primero lo revisamos.");
        await env.ToStatus(id, "CANCELLED", "Duplicado.");        // cuando salga el primer correo, el ticket ya está cancelado
        await env.Dispatcher.ProcessBatchAsync(default);
        Assert.All(env.Sender.Snapshot().Where(m => m.Subject.StartsWith("Tu ticket")), m => Assert.Contains("Cancelado", m.Subject));
    }

    [Fact]
    public async Task Un_proceso_que_se_cae_no_pierde_el_correo_la_reserva_vence_y_se_reintenta()
    {
        var env = await WithOneMailAsync();
        var first = await env.Store.ClaimDueAsync(env.Clock.UtcNow, 10, TimeSpan.FromMinutes(5), default);
        Assert.Single(first);
        var second = await env.Store.ClaimDueAsync(env.Clock.UtcNow, 10, TimeSpan.FromMinutes(5), default);
        Assert.Empty(second);                                                      // reservado: otro proceso no lo toma
        env.Time.Advance(TimeSpan.FromMinutes(6));
        var third = await env.Store.ClaimDueAsync(env.Clock.UtcNow, 10, TimeSpan.FromMinutes(5), default);
        Assert.Equal(2, Assert.Single(third).Attempts);
    }

    [Fact]
    public async Task Respeta_el_tamano_del_lote()
    {
        var env = new Env(e => e.Email.BatchSize = 2);
        for (var i = 0; i < 3; i++) await env.CreateAsync();
        Assert.Equal(2, await env.Dispatcher.ProcessBatchAsync(default));
        Assert.Equal(1, await env.Dispatcher.ProcessBatchAsync(default));
        Assert.Equal(0, await env.Dispatcher.ProcessBatchAsync(default));
    }

    [Fact]
    public async Task Un_fallo_no_impide_enviar_los_demas_del_lote()
    {
        var env = new Env();
        await env.CreateAsync();
        var r2 = Env.ExternalSoftware();
        await env.CreateAsync(r2);
        env.Sender.Failure = m => m.To == Env.ClientEmail ? new EmailSendException("falla solo para este", false) : null;
        Assert.Equal(1, await env.Dispatcher.ProcessBatchAsync(default));
        Assert.Equal("carlos@empresa.example", Assert.Single(env.Sender.Snapshot()).To);
    }
}

public class SendGridEmailSenderTests
{
    private const string Key = "SG.clave-de-prueba-que-nunca-debe-aparecer-en-un-error";

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Request = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return Respond(request);
        }
    }

    private static (SendGridEmailSender sender, Handler handler) Make(Action<EmailOptions>? tweak = null)
    {
        var options = new EmailOptions { Provider = "SendGrid", SendGridApiKey = Key, FromEmail = "brianfire03@gmail.com", FromName = "UniTech Desk" };
        tweak?.Invoke(options);
        var handler = new Handler();
        return (new SendGridEmailSender(new HttpClient(handler), options), handler);
    }

    private static readonly EmailMessage Message = new("ana@example.com", "Recibimos tu solicitud UTD-1001 «ñandú»", "Hola Ana\nTexto", "<p>Hola Ana</p>");

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Arma_la_peticion_de_SendGrid_v3_como_indica_su_documentacion()
    {
        var (sender, h) = Make(o => { o.ReplyToEmail = "soporte@example.edu"; o.ReplyToName = "Soporte"; });
        await sender.SendAsync(Message, default);

        Assert.Equal(HttpMethod.Post, h.Request!.Method);
        Assert.Equal("https://api.sendgrid.com/v3/mail/send", h.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", h.Request.Headers.Authorization!.Scheme);
        Assert.Equal(Key, h.Request.Headers.Authorization.Parameter);
        Assert.Contains("application/json", h.Request.Content!.Headers.ContentType!.ToString());

        using var doc = JsonDocument.Parse(h.Body);
        var root = doc.RootElement;
        Assert.Equal("ana@example.com", root.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("brianfire03@gmail.com", root.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("UniTech Desk", root.GetProperty("from").GetProperty("name").GetString());
        Assert.Equal("soporte@example.edu", root.GetProperty("reply_to").GetProperty("email").GetString());
        Assert.Equal("Recibimos tu solicitud UTD-1001 «ñandú»", root.GetProperty("subject").GetString());
        var content = root.GetProperty("content");
        Assert.Equal("text/plain", content[0].GetProperty("type").GetString());      // SendGrid exige text/plain primero
        Assert.Equal("Hola Ana\nTexto", content[0].GetProperty("value").GetString());
        Assert.Equal("text/html", content[1].GetProperty("type").GetString());
        var tracking = root.GetProperty("tracking_settings");
        Assert.False(tracking.GetProperty("click_tracking").GetProperty("enable").GetBoolean());
        Assert.False(tracking.GetProperty("open_tracking").GetProperty("enable").GetBoolean());
    }

    [Fact]
    public async Task Sin_responder_a_no_se_envia_el_campo_reply_to()
    {
        var (sender, h) = Make();
        await sender.SendAsync(Message, default);
        using var doc = JsonDocument.Parse(h.Body);
        Assert.False(doc.RootElement.TryGetProperty("reply_to", out _));
    }

    [Fact]
    public async Task La_clave_nunca_va_en_el_cuerpo()
    {
        var (sender, h) = Make();
        await sender.SendAsync(Message, default);
        Assert.DoesNotContain(Key, h.Body);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    public async Task Respuestas_exitosas(int status)
    {
        var (sender, h) = Make();
        h.Respond = _ => new HttpResponseMessage((HttpStatusCode)status);
        await sender.SendAsync(Message, default);
    }

    [Fact]
    public async Task Error_400_es_permanente_y_muestra_el_motivo_de_SendGrid()
    {
        var (sender, h) = Make();
        h.Respond = _ => Json(HttpStatusCode.BadRequest, "{\"errors\":[{\"message\":\"The from address does not match a verified Sender Identity.\",\"field\":\"from\",\"help\":\"x\"}]}");
        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message, default));
        Assert.True(ex.IsPermanent);
        Assert.Contains("400", ex.Message);
        Assert.Contains("verified Sender Identity", ex.Message);
        Assert.Contains("campo from", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Theory]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(413, true)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    public async Task Clasificacion_de_errores(int status, bool permanent)
    {
        var (sender, h) = Make();
        h.Respond = _ => Json((HttpStatusCode)status, "{\"errors\":[{\"message\":\"detalle\",\"field\":null}]}");
        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message, default));
        Assert.Equal(permanent, ex.IsPermanent);
        Assert.Contains(status.ToString(), ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task Cuerpo_de_error_ilegible_no_rompe_el_manejo_del_error()
    {
        var (sender, h) = Make();
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>Bad gateway</html>") };
        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message, default));
        Assert.False(ex.IsPermanent);
        Assert.Contains("502", ex.Message);
    }

    [Fact]
    public async Task Sin_conexion_es_un_error_temporal_sin_filtrar_la_clave()
    {
        var (sender, h) = Make();
        h.Respond = _ => throw new HttpRequestException("Name or service not known (api.sendgrid.com:443)");
        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message, default));
        Assert.False(ex.IsPermanent);
        Assert.Equal("No se pudo conectar con SendGrid.", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task Tiempo_de_espera_agotado_es_temporal()
    {
        var (sender, h) = Make();
        h.Respond = _ => throw new TaskCanceledException("timeout");
        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message, default));
        Assert.False(ex.IsPermanent);
    }

    [Fact]
    public async Task Si_la_aplicacion_se_apaga_la_cancelacion_se_propaga()
    {
        var (sender, _) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Message, cts.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sin_clave_configurada_falla_sin_llamar_a_SendGrid(string key)
    {
        var (sender, h) = Make(o => o.SendGridApiKey = key);
        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Message, default));
        Assert.False(ex.IsPermanent);
        Assert.Equal(0, h.Calls);
    }
}

public class ConsoleSenderTests
{
    [Theory]
    [InlineData("ana.lopez@example.com", "a***@example.com")]
    [InlineData("a@x.com", "***")]
    [InlineData("sin-arroba", "***")]
    public void Enmascara_el_destinatario(string email, string expected) => Assert.Equal(expected, ConsoleEmailSender.Mask(email));
}
