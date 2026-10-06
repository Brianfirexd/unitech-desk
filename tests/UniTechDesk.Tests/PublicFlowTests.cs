using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Services;
using UniTechDesk.TestSupport;
using UniTechDesk.Tests.Support;

namespace UniTechDesk.Tests;

/// <summary>Lo que hace el cliente desde la web: crear, consultar, aprobar o rechazar la cotización e informar el pago.</summary>
public class PublicFlowTests
{
    // ------------------------------------------------------------------ Crear ------------------------------------------------------------------

    [Fact]
    public async Task Crear_devuelve_el_codigo_y_deja_el_ticket_abierto_sin_cobro()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        Assert.Equal("UTD-1001", code);
        var t = await env.Get(id);
        Assert.Equal("OPEN", t.Status);
        Assert.Equal("NONE", t.PaymentStatus);
        Assert.Null(t.Quote);
        Assert.Equal("Ana López", t.FullName);
        Assert.Equal(Env.ClientEmail, t.Email);
        var created = Assert.Single(t.History);
        Assert.Equal("CREATED", created.Type);
        Assert.False(created.Internal);
    }

    [Fact]
    public async Task Los_codigos_son_consecutivos()
    {
        var env = new Env();
        Assert.Equal("UTD-1001", (await env.CreateAsync()).Code);
        Assert.Equal("UTD-1002", (await env.CreateAsync(Env.ExternalSoftware())).Code);
    }

    [Fact]
    public async Task Crear_envia_el_correo_al_cliente_y_avisa_al_personal_si_hay_destinatarios()
    {
        var env = new Env(e => e.Email.StaffNotifyTo.AddRange(new[] { "soporte@example.edu", "SOPORTE@example.edu", "otro@example.edu", " " }));
        await env.CreateAsync();
        var mails = env.Store.Mails();
        Assert.Contains(mails, m => m.Template == "TICKET_CREATED" && m.Recipient == Env.ClientEmail);
        Assert.Equal(2, mails.Count(m => m.Template == "STAFF_NEW_TICKET"));        // sin repetidos ni vacíos
        Assert.All(mails, m => Assert.Equal("PENDING", m.State));
    }

    [Fact]
    public async Task Sin_destinatarios_del_personal_solo_se_escribe_al_cliente()
    {
        var env = new Env();
        await env.CreateAsync();
        var mail = Assert.Single(env.Store.Mails());
        Assert.Equal("TICKET_CREATED", mail.Template);
    }

    [Fact]
    public async Task Con_el_proveedor_None_no_se_crea_ningun_correo()
    {
        var env = new Env(e => e.Email.Provider = "None");
        await env.CreateAsync();
        Assert.Empty(env.Store.Mails());
    }

    [Fact]
    public async Task El_campo_trampa_lleno_se_rechaza_sin_crear_nada()
    {
        var env = new Env();
        var r = Env.StudentHardware(); r.Hp = "http://spam.example";
        await Env.Fails(422, () => env.Public.CreateAsync(r, Array.Empty<UploadedFile>(), null, default));
        Assert.Equal(0, env.Store.TicketCount);
    }

    [Fact]
    public async Task Con_captcha_activo_un_token_invalido_se_rechaza_y_uno_valido_pasa()
    {
        var env = new Env(e => e.Captcha.Enabled = true);
        var r = Env.StudentHardware();
        await Env.Fails(422, () => env.Public.CreateAsync(r, Array.Empty<UploadedFile>(), null, default));
        r.CaptchaToken = "token-bueno";
        await env.Public.CreateAsync(r, Array.Empty<UploadedFile>(), null, default);
        Assert.Equal(1, env.Store.TicketCount);
        Assert.Equal(2, env.Captcha.Calls);
    }

    [Fact]
    public async Task Datos_invalidos_devuelven_422_con_errores_por_campo_y_no_guardan_nada()
    {
        var env = new Env();
        var r = Env.StudentHardware(); r.Email = "no-es-correo"; r.Phone = "123";
        var ex = await Env.Fails(422, () => env.Public.CreateAsync(r, Array.Empty<UploadedFile>(), null, default));
        Assert.True(ex.FieldErrors!.ContainsKey("email"));
        Assert.True(ex.FieldErrors.ContainsKey("phone"));
        Assert.Equal(0, env.Store.TicketCount);
        Assert.Equal(0, env.Files.Count);
    }

    // ------------------------------------------------------------------ Adjuntos ------------------------------------------------------------------

    [Fact]
    public async Task Los_adjuntos_se_guardan_con_su_tipo_real_y_se_listan_con_enlace_firmado()
    {
        var env = new Env();
        var (id, _) = await env.CreateAsync(Env.StudentHardware(),
            new UploadedFile("foto equipo.PNG", "image/png", Samples.Png()), new UploadedFile("otra.jpg", "image/jpeg", Samples.Jpeg()));
        Assert.Equal(2, env.Files.Count);
        var t = await env.Get(id);
        Assert.Equal(2, t.Attachments.Count);
        Assert.Equal("foto equipo.PNG", t.Attachments[0].Name);
        Assert.Equal("image/png", t.Attachments[0].Type);
        Assert.StartsWith("/api/files/", t.Attachments[0].Url);
        // la ruta en disco nunca se expone
        Assert.DoesNotContain(env.Files.Names.First(), System.Text.Json.JsonSerializer.Serialize(t));
    }

    [Fact]
    public async Task Un_archivo_falso_rechaza_toda_la_solicitud_y_no_deja_archivos_sueltos()
    {
        var env = new Env();
        var ex = await Env.Fails(422, () => env.CreateAsync(Env.StudentHardware(),
            new UploadedFile("buena.png", "image/png", Samples.Png()), new UploadedFile("mala.png", "image/png", Samples.Exe())));
        Assert.Contains("mala.png", ex.Message);
        Assert.Equal(0, env.Files.Count);
        Assert.Equal(0, env.Store.TicketCount);
    }

    [Fact]
    public async Task Mas_archivos_que_el_maximo_se_rechaza()
    {
        var env = new Env();
        var files = Enumerable.Range(0, 6).Select(i => new UploadedFile($"f{i}.png", "image/png", Samples.Png())).ToArray();
        var ex = await Env.Fails(422, () => env.CreateAsync(Env.StudentHardware(), files));
        Assert.Contains("Máximo 5", ex.Message);
        Assert.Equal(0, env.Files.Count);
    }

    [Fact]
    public async Task Hardware_no_acepta_pdf_pero_software_si()
    {
        var env = new Env();
        await Env.Fails(422, () => env.CreateAsync(Env.StudentHardware(), new UploadedFile("a.pdf", "application/pdf", Samples.Pdf())));
        await env.CreateAsync(Env.ExternalSoftware(), new UploadedFile("brief.pdf", "application/pdf", Samples.Pdf()));
        Assert.Equal(1, env.Store.TicketCount);
    }

    [Fact]
    public async Task Si_la_base_falla_los_archivos_ya_guardados_se_borran()
    {
        var env = new Env();
        env.Store.FailNextWithDeadlock = 3;      // los 3 intentos fallan → 503
        await Env.Fails(503, () => env.CreateAsync(Env.StudentHardware(), new UploadedFile("a.png", "image/png", Samples.Png())));
        Assert.Equal(0, env.Files.Count);
        Assert.Single(env.Files.Deleted);
        Assert.Equal(0, env.Store.TicketCount);
    }

    [Fact]
    public async Task Un_interbloqueo_aislado_se_reintenta_y_la_solicitud_se_crea()
    {
        var env = new Env();
        env.Store.FailNextWithDeadlock = 1;
        var (_, code) = await env.CreateAsync();
        Assert.Equal("UTD-1001", code);
        Assert.Equal(1, env.Store.TicketCount);
    }

    // ------------------------------------------------------------------ Consultar ------------------------------------------------------------------

    [Fact]
    public async Task Consultar_con_codigo_y_correo_correctos()
    {
        var env = new Env();
        var (_, code) = await env.CreateAsync();
        var t = await env.Public.TrackAsync(code, Env.ClientEmail, default);
        Assert.Equal(code, t.Code);
        Assert.Equal("OPEN", t.Status);
        Assert.Null(t.Quote);
        Assert.Null(t.PaymentInstructions);
        Assert.Single(t.Timeline);
    }

    [Theory]
    [InlineData("  utd-1001  ", "ANA.LOPEZ@EXAMPLE.COM")]
    [InlineData("UTD-1001", "  ana.lopez@example.com ")]
    public async Task El_codigo_y_el_correo_no_distinguen_mayusculas_ni_espacios(string code, string email)
    {
        var env = new Env();
        await env.CreateAsync();
        Assert.Equal("UTD-1001", (await env.Public.TrackAsync(code, email, default)).Code);
    }

    [Theory]
    [InlineData("UTD-1001", "otra@example.com")]      // correo distinto
    [InlineData("UTD-9999", Env.ClientEmail)]         // no existe
    [InlineData("1001", Env.ClientEmail)]             // formato inválido
    [InlineData("UTD-1001", "no-es-correo")]
    [InlineData("", "")]
    [InlineData("UTD-1001'; DROP TABLE Ticket;--", Env.ClientEmail)]
    public async Task Cualquier_combinacion_incorrecta_da_el_mismo_404(string code, string email)
    {
        var env = new Env();
        await env.CreateAsync();
        var ex = await Env.Fails(404, () => env.Public.TrackAsync(code, email, default));
        Assert.Equal(PublicTicketService.NotFoundMessage, ex.Message);      // no revela si el código existe
    }

    [Fact]
    public async Task La_consulta_publica_nunca_incluye_datos_personales_notas_ni_costos_internos()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.ToStatus(id, "IN_REVIEW", "Revisando.");
        await env.Staff.AddNoteAsync(env.Admin, id, new NoteRequest { Text = "NOTA INTERNA SECRETA" }, default);
        await env.SaveQuote(id, 850m);                                   // aún no se publica
        var json = System.Text.Json.JsonSerializer.Serialize(await env.Public.TrackAsync(code, Env.ClientEmail, default));
        Assert.DoesNotContain("NOTA INTERNA SECRETA", json);
        Assert.DoesNotContain(Env.ClientPhone, json);
        Assert.DoesNotContain("20231234", json);
        Assert.DoesNotContain("Cotización guardada", json);
        var t = await env.Public.TrackAsync(code, Env.ClientEmail, default);
        Assert.Null(t.Quote);                                            // una cotización interna no se ve
    }

    // ------------------------------------------------------------------ Cotización ------------------------------------------------------------------

    [Fact]
    public async Task El_cliente_ve_la_cotizacion_solo_cuando_se_publica()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 3000m);
        var t = await env.Public.TrackAsync(code, Env.ClientEmail, default);
        Assert.Equal("AWAITING_APPROVAL", t.Status);
        Assert.NotNull(t.Quote);
        Assert.Equal(3000m, t.Quote!.Amount);
        Assert.Equal("NIO", t.Quote.Currency);
        Assert.Equal("2026-10-20", t.Quote.ValidUntil);
        Assert.True(t.Quote.Published);
        Assert.Null(t.Quote.Decision);
        Assert.Null(t.PaymentInstructions);          // todavía no aprobó
    }

    [Fact]
    public async Task Aprobar_con_monto_deja_el_pago_pendiente_y_muestra_instrucciones_de_la_moneda_de_la_cotizacion()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m, "USD");
        var t = await env.ClientDecides(code, "APPROVED");
        Assert.Equal("PENDING", t.PaymentStatus);
        Assert.Equal("AWAITING_APPROVAL", t.Status);               // el trabajo lo inicia el personal
        Assert.Equal("APPROVED", t.Quote!.Decision);
        Assert.Equal("CLIENT", t.Quote.DecidedBy);
        var pay = t.PaymentInstructions!;
        Assert.Equal("TRANSFER", pay.Method);
        var account = Assert.Single(pay.Accounts);               // solo la cuenta en dólares
        Assert.Equal("USD", account.Currency);
        Assert.Contains(t.Timeline, e => e.Type == "QUOTE_DECISION");
        Assert.Contains(env.Store.Mails(), m => m.Template == "QUOTE_APPROVED" && m.Recipient == Env.ClientEmail);
    }

    [Fact]
    public async Task Aprobar_una_cotizacion_sin_costo_no_genera_pago()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 0m);
        var t = await env.ClientDecides(code, "APPROVED");
        Assert.Equal("NONE", t.PaymentStatus);
        Assert.Null(t.PaymentInstructions);
    }

    [Fact]
    public async Task Rechazar_cancela_el_ticket_y_avisa()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id);
        var t = await env.ClientDecides(code, "REJECTED");
        Assert.Equal("CANCELLED", t.Status);
        Assert.Equal("REJECTED", t.Quote!.Decision);
        Assert.Equal("NONE", t.PaymentStatus);
        Assert.Contains(t.Timeline, e => e.Status == "CANCELLED");
        Assert.Contains(env.Store.Mails(), m => m.Template == "QUOTE_REJECTED");
    }

    [Fact]
    public async Task Solo_se_puede_responder_una_vez()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id);
        await env.ClientDecides(code, "APPROVED");
        await Env.Fails(409, () => env.ClientDecides(code, "REJECTED"));
        await Env.Fails(409, () => env.ClientDecides(code, "APPROVED"));
    }

    [Fact]
    public async Task No_se_puede_responder_si_no_hay_cotizacion_publicada()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await Env.Fails(409, () => env.ClientDecides(code, "APPROVED"));
        await env.ToStatus(id, "IN_REVIEW");
        await env.SaveQuote(id);
        await Env.Fails(409, () => env.ClientDecides(code, "APPROVED"));       // guardada pero interna
    }

    [Theory]
    [InlineData("MAYBE")]
    [InlineData("")]
    [InlineData("approved")]
    public async Task Decision_invalida(string decision)
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id);
        await Env.Fails(400, () => env.ClientDecides(code, decision));
    }

    [Fact]
    public async Task No_se_puede_aprobar_una_cotizacion_vencida()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m, "NIO", "2026-10-08");
        env.Time.Advance(TimeSpan.FromDays(3));                                     // ya es 2026-10-09 en Managua
        var ex = await Env.Fails(409, () => env.ClientDecides(code, "APPROVED"));
        Assert.Contains("venció", ex.Message);
    }

    [Fact]
    public async Task El_ultimo_dia_de_vigencia_todavia_se_puede_aprobar()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m, "NIO", "2026-10-08");
        env.Time.Advance(TimeSpan.FromDays(2).Add(TimeSpan.FromHours(7)));          // 2026-10-08 22:00 UTC = 16:00 en Managua
        var t = await env.ClientDecides(code, "APPROVED");
        Assert.Equal("APPROVED", t.Quote!.Decision);
    }

    [Fact]
    public async Task Un_correo_ajeno_no_puede_responder_la_cotizacion()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id);
        await Env.Fails(404, () => env.ClientDecides(code, "APPROVED", "intruso@example.com"));
        Assert.Equal("AWAITING_APPROVAL", (await env.Get(id)).Status);
        Assert.Null((await env.Get(id)).Quote!.Decision);
    }

    // ------------------------------------------------------------------ Comprobante de pago ------------------------------------------------------------------

    private static async Task<(Env env, int id, string code)> ApprovedWithAmountAsync()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        await env.ClientDecides(code, "APPROVED");
        return (env, id, code);
    }

    [Fact]
    public async Task Informar_el_pago_con_comprobante()
    {
        var (env, id, code) = await ApprovedWithAmountAsync();
        var t = await env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "  REF-2026-0042 ", new UploadedFile("comprobante.pdf", "application/pdf", Samples.Pdf()), default);
        Assert.Equal("PROOF_SENT", t.PaymentStatus);
        Assert.Equal("REF-2026-0042", t.PaymentReference);
        Assert.Contains(t.Timeline, e => e.Type == "PAYMENT");

        var admin = await env.Get(id);
        Assert.Equal("REF-2026-0042", admin.Payment!.Reference);
        Assert.Equal("comprobante.pdf", admin.Payment.ProofName);
        Assert.Contains(admin.Attachments, a => a.Name == "comprobante.pdf");
        Assert.Contains(env.Store.Mails(), m => m.Template == "PROOF_RECEIVED" && m.Recipient == Env.ClientEmail);
    }

    [Fact]
    public async Task Informar_el_pago_sin_archivo_tambien_vale()
    {
        var (env, _, code) = await ApprovedWithAmountAsync();
        var t = await env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-1234", null, default);
        Assert.Equal("PROOF_SENT", t.PaymentStatus);
    }

    [Fact]
    public async Task Se_puede_corregir_la_referencia_mientras_no_este_confirmado()
    {
        var (env, id, code) = await ApprovedWithAmountAsync();
        await env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-AAAA", null, default);
        var t = await env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-BBBB", new UploadedFile("nuevo.png", "image/png", Samples.Png()), default);
        Assert.Equal("REF-BBBB", t.PaymentReference);
        Assert.Equal("nuevo.png", (await env.Get(id)).Payment!.ProofName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12345678901234567890123456789012345678901")]   // 41
    public async Task Referencia_de_4_a_40_caracteres(string reference)
    {
        var (env, _, code) = await ApprovedWithAmountAsync();
        var ex = await Env.Fails(422, () => env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, reference, null, default));
        Assert.True(ex.FieldErrors!.ContainsKey("reference"));
    }

    [Fact]
    public async Task El_comprobante_solo_acepta_jpg_png_o_pdf_reales()
    {
        var (env, _, code) = await ApprovedWithAmountAsync();
        await Env.Fails(422, () => env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-1234", new UploadedFile("p.gif", "image/gif", Samples.Gif()), default));
        await Env.Fails(422, () => env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-1234", new UploadedFile("p.png", "image/png", Samples.Exe()), default));
        await Env.Fails(422, () => env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-1234", new UploadedFile("p.pdf", "application/pdf", Array.Empty<byte>()), default));
        Assert.Equal(0, env.Files.Count);
    }

    [Fact]
    public async Task No_se_puede_informar_un_pago_si_no_hay_nada_pendiente()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await Env.Fails(409, () => env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-1234", null, default));         // sin cotización
        await env.PublishQuoteAsync(id, 850m);
        await Env.Fails(409, () => env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-1234", null, default));         // aún sin aprobar
    }

    [Fact]
    public async Task Despues_de_confirmado_el_pago_ya_no_se_acepta_otro_comprobante()
    {
        var (env, id, code) = await ApprovedWithAmountAsync();
        await env.Pay(id, "PAID");
        await Env.Fails(409, () => env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-9999", null, default));
    }

    [Fact]
    public async Task Un_correo_ajeno_no_puede_subir_comprobantes()
    {
        var (env, _, code) = await ApprovedWithAmountAsync();
        await Env.Fails(404, () => env.Public.SubmitPaymentProofAsync(code, "intruso@example.com", "REF-1234", null, default));
        Assert.Equal(0, env.Files.Count);
    }

    [Fact]
    public async Task Si_la_base_falla_el_comprobante_guardado_se_borra()
    {
        var (env, _, code) = await ApprovedWithAmountAsync();
        env.Store.FailNextWithDeadlock = 3;
        await Env.Fails(503, () => env.Public.SubmitPaymentProofAsync(code, Env.ClientEmail, "REF-1234", new UploadedFile("p.png", "image/png", Samples.Png()), default));
        Assert.Equal(0, env.Files.Count);
    }

    // ------------------------------------------------------------------ Concurrencia ------------------------------------------------------------------

    [Fact]
    public async Task Si_el_personal_cambia_el_ticket_a_la_vez_la_respuesta_del_cliente_se_reintenta()
    {
        InterferingTicketRepository? interfering = null;
        Env? envRef = null;
        var env = new Env(wrapTickets: inner => interfering = new InterferingTicketRepository(inner));
        envRef = env;
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);

        var fired = false;
        interfering!.BeforeApply = async tid =>
        {
            if (fired) return;
            fired = true;
            // el personal agrega una nota justo antes de que se guarde la decisión del cliente → la versión esperada cambió
            await envRef.Staff.AddNoteAsync(envRef.Admin, tid, new NoteRequest { Text = "Llamé al cliente." }, default);
        };
        var t = await env.ClientDecides(code, "APPROVED");
        Assert.Equal("APPROVED", t.Quote!.Decision);
        Assert.True(env.Store.ConcurrencyConflicts >= 1);
        Assert.Contains((await env.Get(id)).History, h => h.Text == "Llamé al cliente.");
    }

    [Fact]
    public async Task Si_el_conflicto_no_se_resuelve_tras_3_intentos_responde_409()
    {
        InterferingTicketRepository? interfering = null;
        var env = new Env(wrapTickets: inner => interfering = new InterferingTicketRepository(inner));
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        var busy = false;
        interfering!.BeforeApply = async tid =>
        {
            if (busy) return;                       // la propia nota pasa por el mismo repositorio: evitar la recursión
            busy = true;
            try { await env.Staff.AddNoteAsync(env.Admin, tid, new NoteRequest { Text = "otra nota" }, default); }   // interfiere en cada intento
            finally { busy = false; }
        };
        var applyBefore = interfering.ApplyCalls;
        var ex = await Env.Fails(409, () => env.ClientDecides(code, "APPROVED"));
        Assert.Contains("cambió", ex.Message);
        Assert.Null((await env.Get(id)).Quote!.Decision);
        Assert.True(interfering.ApplyCalls - applyBefore >= 3);
    }

    [Fact]
    public async Task Dos_aprobaciones_simultaneas_solo_registran_una()
    {
        var env = new Env();
        var (id, code) = await env.CreateAsync();
        await env.PublishQuoteAsync(id, 850m);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try { await env.ClientDecides(code, "APPROVED"); return 200; }
            catch (AppException ex) { return ex.StatusCode; }
        }));
        Assert.Equal(1, results.Count(r => r == 200));
        Assert.All(results.Where(r => r != 200), r => Assert.Equal(409, r));
        var t = await env.Get(id);
        Assert.Equal(1, t.History.Count(h => h.Type == "QUOTE_DECISION"));
        Assert.Equal(1, env.Store.Mails().Count(m => m.Template == "QUOTE_APPROVED"));
    }
}
