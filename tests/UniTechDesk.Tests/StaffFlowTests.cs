using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Services;
using UniTechDesk.TestSupport;
using UniTechDesk.Tests.Support;

namespace UniTechDesk.Tests;

/// <summary>Lo que hace el personal desde el panel: estados, cotización, pagos, asignación y notas.</summary>
public class StaffFlowTests
{
    // ------------------------------------------------------------------ Flujo completo ------------------------------------------------------------------

    [Fact]
    public async Task Flujo_completo_de_hardware_de_la_solicitud_a_la_entrega_con_pago()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();

        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");
        await env.ToStatus(id, "IN_PROGRESS", "Empezamos la reparación.");
        await env.ToStatus(id, "WAITING_PARTS", "Esperamos una pieza.");
        await env.ToStatus(id, "IN_PROGRESS");
        await env.ToStatus(id, "COMPLETED", "Reparado.");
        await env.ToStatus(id, "READY", "Puedes pasar a retirarlo.");

        // todavía sin pagar: no se puede entregar
        var blocked = await Env.Fails(409, () => env.ToStatus(id, "DELIVERED"));
        Assert.Equal("No se puede entregar hasta confirmar el pago.", blocked.Message);

        await env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-0001", null, default);
        await Env.Fails(409, () => env.ToStatus(id, "DELIVERED"));           // en verificación tampoco

        var paid = await env.Pay(id, "PAID");
        Assert.Equal("PAID", paid.PaymentStatus);
        Assert.NotNull(paid.Payment);

        var done = await env.ToStatus(id, "DELIVERED", "Entregado a la persona.");
        Assert.Equal("DELIVERED", done.Status);

        // el historial cuenta la historia completa y en orden
        var statuses = done.History.Where(h => h.Type == "STATUS").Select(h => h.To).ToArray();
        Assert.Equal(new[] { "IN_REVIEW", "QUOTED", "AWAITING_APPROVAL", "IN_PROGRESS", "WAITING_PARTS", "IN_PROGRESS", "COMPLETED", "READY", "DELIVERED" }, statuses);

        // correos que vio el cliente: avisos relevantes, sin los pasos internos (Cotizado, En pruebas…)
        var templates = env.Store.Mails().Select(m => m.Template).ToList();
        Assert.Contains("TICKET_CREATED", templates);
        Assert.Contains("QUOTE_PUBLISHED", templates);
        Assert.Contains("QUOTE_APPROVED", templates);
        Assert.Contains("READY_FOR_PICKUP", templates);
        Assert.Contains("PROOF_RECEIVED", templates);
        Assert.Contains("PAYMENT_CONFIRMED", templates);
        Assert.Equal(1, templates.Count(t => t == "QUOTE_PUBLISHED"));
    }

    [Fact]
    public async Task Flujo_completo_de_software_con_cotizacion_sin_costo_se_entrega_sin_pago()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync(Env.ExternalSoftware());
        await env.PublishQuoteAsync(id, 0m);
        await env.ClientDecides(code, "APPROVED", Env.ExternalEmail);
        await env.ToStatus(id, "IN_DEVELOPMENT");
        await env.ToStatus(id, "TESTING");
        await env.ToStatus(id, "IN_DEVELOPMENT");
        await env.ToStatus(id, "COMPLETED");
        var t = await env.ToStatus(id, "DELIVERED");
        Assert.Equal("DELIVERED", t.Status);
        Assert.Equal("NONE", t.PaymentStatus);
    }

    [Fact]
    public async Task El_trabajo_no_inicia_sin_cotizacion_aprobada()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.PublishQuoteAsync(id);
        var ex = await Env.Fails(409, () => env.ToStatus(id, "IN_PROGRESS"));
        Assert.Equal("El trabajo inicia cuando el cliente aprueba la cotización.", ex.Message);
    }

    [Fact]
    public async Task El_personal_puede_registrar_la_aprobacion_hecha_por_telefono()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        var t = await env.Staff.RecordQuoteDecisionAsync(env.Admin, id, new StaffQuoteDecisionRequest { Note = "Aprobó por WhatsApp." }, default);
        Assert.Equal("APPROVED", t.Quote!.Decision);
        Assert.Equal("STAFF", t.Quote.DecidedBy);
        Assert.Equal("PENDING", t.PaymentStatus);
        Assert.Contains(t.History, h => h.Type == "QUOTE_DECISION" && h.Text.Contains("Aprobó por WhatsApp."));
        Assert.Contains(env.Store.Mails(), m => m.Template == "QUOTE_APPROVED");
        Assert.Equal("APPROVED", (await env.Public.TrackAsync(code, Env.ClientEmail, default)).Quote!.Decision);
        await Env.Fails(409, () => env.Staff.RecordQuoteDecisionAsync(env.Admin, id, new StaffQuoteDecisionRequest(), default));   // ya no está pendiente
    }

    // ------------------------------------------------------------------ Cambios de estado ------------------------------------------------------------------

    [Theory]
    [InlineData("COMPLETED")]
    [InlineData("DELIVERED")]
    [InlineData("QUOTED")]
    [InlineData("IN_PROGRESS")]
    public async Task Transiciones_no_permitidas_desde_Abierto(string to)
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        var ex = await Env.Fails(409, () => env.ToStatus(id, to));
        Assert.StartsWith("No se puede pasar de «Abierto» a «", ex.Message);
        Assert.Equal("OPEN", (await env.Get(id)).Status);
    }

    [Fact]
    public async Task Estado_inexistente_da_400()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await Env.Fails(400, () => env.ToStatus(id, "NO_EXISTE"));
        await Env.Fails(400, () => env.ToStatus(id, ""));
    }

    [Fact]
    public async Task Un_ticket_de_software_no_puede_ir_a_estados_de_hardware()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync(Env.ExternalSoftware());
        await env.PublishQuoteAsync(id, 100m);
        await env.ClientDecides(code, "APPROVED", Env.ExternalEmail);
        var ex = await Env.Fails(409, () => env.ToStatus(id, "IN_PROGRESS"));
        Assert.Contains("No se puede pasar", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no")]
    [InlineData("    ")]
    public async Task Cancelar_exige_un_motivo_de_al_menos_5_caracteres(string note)
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        var ex = await Env.Fails(422, () => env.ToStatus(id, "CANCELLED", note));
        Assert.True(ex.FieldErrors!.ContainsKey("note"));
        Assert.Equal("OPEN", (await env.Get(id)).Status);
    }

    [Fact]
    public async Task Cancelar_con_motivo_se_registra_y_el_cliente_lo_ve()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.ToStatus(id, "CANCELLED", "Duplicado de otra solicitud.");
        var t = await env.Public.TrackAsync(code, Env.ClientEmail, default);
        Assert.Equal("CANCELLED", t.Status);
        Assert.Contains(t.Timeline, e => e.Status == "CANCELLED" && e.Text == "Duplicado de otra solicitud.");
    }

    [Fact]
    public async Task El_mensaje_no_puede_pasar_de_500_caracteres()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await Env.Fails(422, () => env.ToStatus(id, "IN_REVIEW", new string('x', 501)));
        await env.ToStatus(id, "IN_REVIEW", new string('x', 500));
    }

    [Fact]
    public async Task El_paso_Cotizado_es_interno_y_no_aparece_en_la_linea_de_tiempo_del_cliente()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.ToStatus(id, "IN_REVIEW", "Revisando.");
        await env.SaveQuote(id);
        await env.ToStatus(id, "QUOTED", "nota interna del paso");
        var t = await env.Public.TrackAsync(code, Env.ClientEmail, default);
        Assert.DoesNotContain(t.Timeline, e => e.Status == "QUOTED");
        Assert.DoesNotContain(t.Timeline, e => e.Text == "nota interna del paso");
        // solo "En revisión" avisó al cliente; "Cotizado" es un paso interno y no genera correo
        Assert.Equal(1, env.Store.Mails().Count(m => m.Template == "STATUS_UPDATE"));
    }

    [Fact]
    public async Task No_se_puede_guardar_ni_enviar_una_cotizacion_que_no_existe()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.ToStatus(id, "IN_REVIEW");
        Assert.Equal("Primero guarda la cotización.", (await Env.Fails(409, () => env.ToStatus(id, "QUOTED"))).Message);
    }

    // ------------------------------------------------------------------ Cotización ------------------------------------------------------------------

    [Fact]
    public async Task Guardar_cotizacion_la_deja_interna_y_en_decimales()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.Review(id);
        var t = await env.SaveQuote(id, 3000m, "NIO", "2026-10-20", "  Cambio de pantalla  y revisión general. ");
        Assert.Equal(3000m, t.Quote!.Amount);                        // el bug del front: 3,000 no puede quedar en 3
        Assert.False(t.Quote.Published);
        Assert.Equal("Cambio de pantalla  y revisión general.", t.Quote.Description);
        Assert.Contains(t.History, h => h.Type == "QUOTE" && h.Internal && h.Text.Contains("C$ 3,000.00"));
    }

    [Fact]
    public async Task Guardar_de_nuevo_reemplaza_la_cotizacion_y_conserva_la_anterior_descartada()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.Review(id);
        await env.SaveQuote(id, 500m);
        var t = await env.SaveQuote(id, 700m);
        Assert.Equal(700m, t.Quote!.Amount);
        Assert.Equal(2, env.Store.QuoteVersions(id));
    }

    [Theory]
    [InlineData(-1, "NIO", "Detalle válido", "2026-10-20", "amount")]
    [InlineData(1000000000, "NIO", "Detalle válido", "2026-10-20", "amount")]
    [InlineData(100, "EUR", "Detalle válido", "2026-10-20", "currency")]
    [InlineData(100, "NIO", "no", "2026-10-20", "description")]
    [InlineData(100, "NIO", "Detalle válido", "2026-10-05", "validUntil")]
    [InlineData(100, "NIO", "Detalle válido", "20/10/2026", "validUntil")]
    public async Task Cotizacion_invalida(double amount, string currency, string description, string validUntil, string field)
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.Review(id);
        var ex = await Env.Fails(422, () => env.SaveQuote(id, (decimal)amount, currency, validUntil, description));
        Assert.True(ex.FieldErrors!.ContainsKey(field), "falta el error de " + field);
    }

    [Fact]
    public async Task La_cotizacion_sin_monto_es_invalida()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.Review(id);
        var ex = await Env.Fails(422, () => env.Staff.SaveQuoteAsync(env.Admin, id, new SaveQuoteRequest { Currency = "NIO", Description = "Detalle válido" }, default));
        Assert.True(ex.FieldErrors!.ContainsKey("amount"));
    }

    [Fact]
    public async Task La_vigencia_es_opcional_y_el_monto_cero_es_valido()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.Review(id);
        var t = await env.SaveQuote(id, 0m, "NIO", null);
        Assert.Equal("", t.Quote!.ValidUntil);
        Assert.Equal(0m, t.Quote.Amount);
    }

    [Fact]
    public async Task Los_montos_con_mas_de_2_decimales_se_redondean()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.Review(id);
        Assert.Equal(10.13m, (await env.SaveQuote(id, 10.125m)).Quote!.Amount);
    }

    [Fact]
    public async Task La_cotizacion_no_se_edita_una_vez_enviada_al_cliente()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.PublishQuoteAsync(id);
        var ex = await Env.Fails(409, () => env.SaveQuote(id, 1m));
        Assert.Contains("solo se puede editar", ex.Message);
    }

    [Fact]
    public async Task Retirar_la_cotizacion_la_vuelve_interna_y_se_puede_editar_otra_vez()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        var t = await env.ToStatus(id, "QUOTED");
        Assert.False(t.Quote!.Published);
        Assert.Null((await env.Public.TrackAsync(code, Env.ClientEmail, default)).Quote);
        await env.SaveQuote(id, 900m);
        await env.ToStatus(id, "AWAITING_APPROVAL");
        Assert.Equal(900m, (await env.Public.TrackAsync(code, Env.ClientEmail, default)).Quote!.Amount);
    }

    [Fact]
    public async Task No_se_puede_retirar_una_cotizacion_que_el_cliente_ya_respondio()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id);
        await env.ClientDecides(code, "APPROVED");
        var ex = await Env.Fails(409, () => env.ToStatus(id, "QUOTED"));
        Assert.Equal("El cliente ya respondió la cotización; no se puede retirar.", ex.Message);
    }

    [Fact]
    public async Task No_se_envia_una_cotizacion_con_la_vigencia_vencida()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.ToStatus(id, "IN_REVIEW");
        await env.SaveQuote(id, 850m, "NIO", "2026-10-07");
        await env.ToStatus(id, "QUOTED");
        env.Time.Advance(TimeSpan.FromDays(5));
        var ex = await Env.Fails(409, () => env.ToStatus(id, "AWAITING_APPROVAL"));
        Assert.Contains("vigencia", ex.Message);
    }

    [Fact]
    public async Task Despues_de_aprobada_la_cotizacion_ya_no_se_puede_reemplazar()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id);
        await env.ClientDecides(code, "APPROVED");
        // en AWAITING_APPROVAL la cotización ya no es editable
        await Env.Fails(409, () => env.SaveQuote(id, 1m));
    }

    // ------------------------------------------------------------------ Reabrir ------------------------------------------------------------------

    [Fact]
    public async Task Reabrir_un_ticket_cancelado_descarta_la_cotizacion_anterior_y_reinicia_el_cobro()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "REJECTED");
        Assert.Equal("CANCELLED", (await env.Get(id)).Status);

        var t = await env.ToStatus(id, "IN_REVIEW", "Reabierto a pedido del cliente.");
        Assert.Equal("IN_REVIEW", t.Status);
        Assert.Null(t.Quote);
        Assert.Equal("NONE", t.PaymentStatus);
        Assert.Equal(1, env.Store.QuoteVersions(id));            // la anterior queda en el histórico (descartada)

        // se puede cotizar de nuevo y el cliente puede aprobar la nueva
        await env.SaveQuote(id, 600m);
        await env.ToStatus(id, "QUOTED");
        await env.ToStatus(id, "AWAITING_APPROVAL");
        var after = await env.ClientDecides(code, "APPROVED");
        Assert.Equal(600m, after.Quote!.Amount);
        Assert.Equal("PENDING", after.PaymentStatus);
    }

    [Fact]
    public async Task Reabrir_un_ticket_cancelado_con_pago_confirmado_no_se_permite()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");
        await env.ToStatus(id, "IN_PROGRESS");
        await env.Pay(id, "PAID");
        await env.ToStatus(id, "CANCELLED", "El cliente ya no lo necesita.");
        var ex = await Env.Fails(409, () => env.ToStatus(id, "IN_REVIEW", "Reabrir"));
        Assert.Contains("pago confirmado", ex.Message);
    }

    [Fact]
    public async Task Reabrir_un_ticket_cancelado_sin_cotizacion_es_simple()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.ToStatus(id, "CANCELLED", "Error al enviarlo.");
        Assert.Equal("IN_REVIEW", (await env.ToStatus(id, "IN_REVIEW")).Status);
    }

    // ------------------------------------------------------------------ Pagos ------------------------------------------------------------------

    [Fact]
    public async Task Confirmar_y_revertir_el_pago()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");

        var paid = await env.Pay(id, "PAID");
        Assert.Equal("PAID", paid.PaymentStatus);
        Assert.Contains(env.Store.Mails(), m => m.Template == "PAYMENT_CONFIRMED");
        Assert.Null((await env.Public.TrackAsync(code, Env.ClientEmail, default)).PaymentInstructions);     // ya pagó

        var back = await env.Pay(id, "PENDING");
        Assert.Equal("PENDING", back.PaymentStatus);
        Assert.NotNull((await env.Public.TrackAsync(code, Env.ClientEmail, default)).PaymentInstructions);
    }

    [Fact]
    public async Task Marcar_el_mismo_estado_de_pago_no_cambia_nada()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");
        var before = (await env.Get(id)).History.Count;
        await env.Pay(id, "PENDING");
        Assert.Equal(before, (await env.Get(id)).History.Count);
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("REFUNDED")]
    [InlineData("")]
    public async Task Estado_de_pago_no_valido(string status)
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");
        await Env.Fails(400, () => env.Pay(id, status));
    }

    [Fact]
    public async Task El_pago_solo_se_habilita_con_cotizacion_aprobada_y_monto_mayor_a_cero()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await Env.Fails(409, () => env.Pay(id, "PAID"));                         // sin cotización
        await env.PublishQuoteAsync(id, 850m);
        await Env.Fails(409, () => env.Pay(id, "PAID"));                         // sin aprobar

        var (id2, code2) = await env.CreateAsync(Env.ExternalSoftware());
        await env.PublishQuoteAsync(id2, 0m);
        await env.ClientDecides(code2, "APPROVED", Env.ExternalEmail);
        await Env.Fails(409, () => env.Pay(id2, "PAID"));                        // monto cero
    }

    // ------------------------------------------------------------------ Reglas de la "base" (triggers) ------------------------------------------------------------------

    [Fact]
    public async Task El_trigger_de_entrega_protege_aunque_el_servicio_se_salte()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");
        await env.ToStatus(id, "IN_PROGRESS");
        await env.ToStatus(id, "COMPLETED");
        var current = (await env.Store.FindByIdAsync(id, default))!;
        var change = new TicketChangeSet { TicketId = id, ExpectedVersion = current.RowVersion, Now = env.Clock.UtcNow, NewStatus = "DELIVERED" };
        change.History.Add(new HistoryRecord { At = change.Now, AuthorType = "STAFF", StaffId = env.AdminId, AuthorName = "x", Type = "STATUS", From = "COMPLETED", To = "DELIVERED", Text = "" });
        var ex = await Assert.ThrowsAsync<AppException>(() => env.Store.ApplyAsync(change, default));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("COMPLETED", (await env.Get(id)).Status);               // la transacción se deshizo
    }

    [Fact]
    public async Task El_trigger_de_inicio_de_trabajo_protege_aunque_el_servicio_se_salte()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        var current = (await env.Store.FindByIdAsync(id, default))!;
        var change = new TicketChangeSet { TicketId = id, ExpectedVersion = current.RowVersion, Now = env.Clock.UtcNow, NewStatus = "IN_PROGRESS" };
        change.History.Add(new HistoryRecord { At = change.Now, AuthorType = "STAFF", StaffId = env.AdminId, AuthorName = "x", Type = "STATUS", From = "AWAITING_APPROVAL", To = "IN_PROGRESS", Text = "" });
        await Assert.ThrowsAsync<AppException>(() => env.Store.ApplyAsync(change, default));
    }

    [Fact]
    public async Task Una_operacion_que_viola_una_restriccion_se_deshace_por_completo()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        var current = (await env.Store.FindByIdAsync(id, default))!;
        var change = new TicketChangeSet { TicketId = id, ExpectedVersion = current.RowVersion, Now = env.Clock.UtcNow, NewStatus = "IN_REVIEW" };
        change.History.Add(new HistoryRecord { At = change.Now, AuthorType = "STAFF", StaffId = env.AdminId, AuthorName = "x", Type = "STATUS", From = "OPEN", To = "IN_REVIEW", Text = "ok" });
        change.History.Add(new HistoryRecord { At = change.Now, AuthorType = "STAFF", StaffId = env.AdminId, AuthorName = "x", Type = "NOTE", Text = "", Internal = true });   // CK_Historial_Nota
        await Assert.ThrowsAsync<ConstraintViolationException>(() => env.Store.ApplyAsync(change, default));
        var after = await env.Get(id);
        Assert.Equal("OPEN", after.Status);                                  // ni el estado ni el primer historial quedaron
        Assert.Single(after.History);
    }

    // ------------------------------------------------------------------ Asignación y notas ------------------------------------------------------------------

    [Fact]
    public async Task Asignar_cambiar_y_quitar()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        var t = await env.Staff.AssignAsync(env.Admin, id, new AssignRequest { StaffId = env.TecId }, default);
        Assert.Equal(env.TecId, t.Assignee!.Id);
        Assert.Contains(t.History, h => h.Type == "ASSIGN" && h.Internal && h.Text == "Asignado a Técnico Uno.");

        var same = await env.Staff.AssignAsync(env.Admin, id, new AssignRequest { StaffId = env.TecId }, default);
        Assert.Equal(1, same.History.Count(h => h.Type == "ASSIGN"));        // sin cambio no se repite el evento

        var none = await env.Staff.AssignAsync(env.Admin, id, new AssignRequest { StaffId = null }, default);
        Assert.Null(none.Assignee);
        Assert.Contains(none.History, h => h.Text == "Se quitó la asignación.");
    }

    [Fact]
    public async Task No_se_asigna_a_personal_inexistente_o_inactivo()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await Env.Fails(400, () => env.Staff.AssignAsync(env.Admin, id, new AssignRequest { StaffId = 9999 }, default));
        env.Store.SetActive(env.TecId, false);
        await Env.Fails(400, () => env.Staff.AssignAsync(env.Admin, id, new AssignRequest { StaffId = env.TecId }, default));
        var staff = await env.Staff.ListStaffAsync(default);
        Assert.DoesNotContain(staff, s => s.Id == env.TecId);
    }

    [Fact]
    public async Task Las_notas_son_internas_y_nunca_llegan_al_cliente()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        var t = await env.Staff.AddNoteAsync(env.Tec, id, new NoteRequest { Text = "  Cliente muy amable, pidió urgencia.  " }, default);
        var note = t.History.Single(h => h.Type == "NOTE");
        Assert.True(note.Internal);
        Assert.Equal("Cliente muy amable, pidió urgencia.", note.Text);
        Assert.Equal("Técnico Uno", note.By);
        var pub = await env.Public.TrackAsync(code, Env.ClientEmail, default);
        Assert.DoesNotContain(pub.Timeline, e => e.Type == "NOTE");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("   ")]
    public async Task Nota_vacia_o_muy_corta(string text)
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await Env.Fails(422, () => env.Staff.AddNoteAsync(env.Admin, id, new NoteRequest { Text = text }, default));
    }

    [Fact]
    public async Task Nota_de_mas_de_1000_caracteres_se_rechaza()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync();
        await Env.Fails(422, () => env.Staff.AddNoteAsync(env.Admin, id, new NoteRequest { Text = new string('n', 1001) }, default));
        await env.Staff.AddNoteAsync(env.Admin, id, new NoteRequest { Text = new string('n', 1000) }, default);
    }

    [Fact]
    public async Task Ticket_inexistente_da_404()
    {
        var env = new Env();
        await Env.Fails(404, () => env.Get(9999));
        await Env.Fails(404, () => env.ToStatus(9999, "IN_REVIEW"));
        await Env.Fails(404, () => env.Staff.AddNoteAsync(env.Admin, 9999, new NoteRequest { Text = "hola" }, default));
    }

    // ------------------------------------------------------------------ Listado ------------------------------------------------------------------

    private static async Task<Env> SeededAsync()
    {
        var env = new Env(seedStaff: false);
        await env.Store.SeedDemoAsync(env.Hasher.Hash, "Admin#2026");
        return env;
    }

    [Fact]
    public async Task Listado_con_filtros_orden_y_paginas()
    {
        var env = await SeededAsync();
        var all = await env.Staff.ListAsync(new TicketListParams(), default);
        Assert.Equal(3, all.Total);
        Assert.Equal("UTD-1002", all.Items[0].Code);                          // más reciente primero (creado hace 2 días)

        var byStatus = await env.Staff.ListAsync(new TicketListParams { Status = "AWAITING_APPROVAL" }, default);
        Assert.Equal("UTD-1003", Assert.Single(byStatus.Items).Code);

        var asc = await env.Staff.ListAsync(new TicketListParams { Sort = "date_asc" }, default);
        Assert.Equal("UTD-1001", asc.Items[0].Code);

        var urgent = await env.Staff.ListAsync(new TicketListParams { Sort = "urgency_desc" }, default);
        Assert.Equal("HIGH", urgent.Items[0].Urgency);

        var paged = await env.Staff.ListAsync(new TicketListParams { PageSize = 2, Page = 2 }, default);
        Assert.Equal(1, paged.Items.Count);
        Assert.Equal(3, paged.Total);
        Assert.Equal(2, paged.Page);

        var beyond = await env.Staff.ListAsync(new TicketListParams { PageSize = 2, Page = 99 }, default);
        Assert.Equal(2, beyond.Page);                                         // se ajusta a la última página
    }

    [Fact]
    public async Task La_busqueda_no_distingue_tildes_ni_mayusculas_y_busca_en_varios_campos()
    {
        var env = await SeededAsync();
        Assert.Equal("UTD-1001", Assert.Single((await env.Staff.ListAsync(new TicketListParams { Q = "maria garcia" }, default)).Items).Code);
        Assert.Equal("UTD-1001", Assert.Single((await env.Staff.ListAsync(new TicketListParams { Q = "MARÍA" }, default)).Items).Code);
        Assert.Equal("UTD-1002", Assert.Single((await env.Staff.ListAsync(new TicketListParams { Q = "empresa abc" }, default)).Items).Code);
        Assert.Equal("UTD-1003", Assert.Single((await env.Staff.ListAsync(new TicketListParams { Q = "ideapad" }, default)).Items).Code);
        Assert.Equal("UTD-1003", Assert.Single((await env.Staff.ListAsync(new TicketListParams { Q = "utd-1003" }, default)).Items).Code);
        Assert.Empty((await env.Staff.ListAsync(new TicketListParams { Q = "zzzz-no-existe" }, default)).Items);
    }

    [Fact]
    public async Task La_busqueda_con_comodines_de_SQL_se_toma_literal()
    {
        var env = await SeededAsync();
        Assert.Empty((await env.Staff.ListAsync(new TicketListParams { Q = "%" }, default)).Items);
        Assert.Empty((await env.Staff.ListAsync(new TicketListParams { Q = "_" }, default)).Items);
        Assert.Empty((await env.Staff.ListAsync(new TicketListParams { Q = "[a-z]" }, default)).Items);
    }

    [Theory]
    [InlineData("status", "NO_EXISTE")]
    [InlineData("service", "OTRO")]
    [InlineData("urgency", "ALTISIMA")]
    [InlineData("payment", "DEBE")]
    [InlineData("sort", "price_desc")]
    public async Task Filtros_desconocidos_dan_400(string which, string value)
    {
        var env = new Env();
        var p = which switch
        {
            "status" => new TicketListParams { Status = value },
            "service" => new TicketListParams { Service = value },
            "urgency" => new TicketListParams { Urgency = value },
            "payment" => new TicketListParams { Payment = value },
            _ => new TicketListParams { Sort = value }
        };
        await Env.Fails(400, () => env.Staff.ListAsync(p, default));
    }

    [Fact]
    public async Task El_tamano_de_pagina_se_limita()
    {
        var env = await SeededAsync();
        Assert.Equal(1000, (await env.Staff.ListAsync(new TicketListParams { PageSize = 100000 }, default)).PageSize);
        Assert.Equal(1, (await env.Staff.ListAsync(new TicketListParams { PageSize = -5 }, default)).PageSize);
    }

    [Fact]
    public async Task El_listado_no_trae_historial_ni_adjuntos_pero_el_detalle_si()
    {
        var env = await SeededAsync();
        var list = await env.Staff.ListAsync(new TicketListParams { Status = "AWAITING_APPROVAL" }, default);
        Assert.Empty(list.Items[0].History);
        Assert.Empty(list.Items[0].Attachments);
        var detail = await env.Get(list.Items[0].Id);
        Assert.NotEmpty(detail.History);
    }

    [Fact]
    public async Task Estadisticas_del_panel()
    {
        var env = await SeededAsync();
        var s = await env.Staff.GetStatsAsync(default);
        Assert.Equal(3, s.Total);
        Assert.Equal(1, s.Open);                 // UTD-1002 sigue abierto
        Assert.Equal(3, s.Active);
        Assert.Equal(1, s.UrgentActive);         // UTD-1001 es urgente
        Assert.Equal(0, s.PendingPayment);
    }
}
