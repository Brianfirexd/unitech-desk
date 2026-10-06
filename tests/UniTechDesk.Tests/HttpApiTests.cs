using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UniTechDesk.Api.Infrastructure;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Options;
using UniTechDesk.TestSupport;
using UniTechDesk.Tests.Support;

namespace UniTechDesk.Tests;

/// <summary>Pruebas contra el servidor HTTP real (Kestrel + todo el middleware), con la base en memoria.</summary>
public class HttpApiTests
{
    private static ApiHost.FilePart Png(string name = "foto.png") => new(name, Samples.Png(), "image/png");

    // ------------------------------------------------------------------ Sitio y cabeceras ------------------------------------------------------------------

    [Fact]
    public async Task El_sitio_se_sirve_con_cabeceras_de_seguridad_y_CSP_estricta()
    {
        await using var h = await ApiHost.StartAsync();
        var r = await h.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("hola", await r.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", r.Headers.CacheControl!.ToString());
        var csp = r.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self';", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("frame-src 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await h.Http.GetAsync("/js/app.js")).StatusCode);
    }

    [Fact]
    public async Task Con_Turnstile_configurado_la_CSP_permite_solo_el_dominio_de_Cloudflare()
    {
        await using var h = await ApiHost.StartAsync(a => a.Add("--Captcha:TurnstileSecret=secreto-de-prueba"));
        var csp = (await h.Http.GetAsync("/")).Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self' https://challenges.cloudflare.com;", csp);
        Assert.Contains("frame-src https://challenges.cloudflare.com;", csp);
    }

    [Theory]
    [InlineData("/appsettings.json")]
    [InlineData("/storage/attachments/x.png")]
    [InlineData("/../appsettings.json")]
    [InlineData("/%2e%2e/appsettings.json")]
    [InlineData("/index.html/../../appsettings.json")]
    [InlineData("/UniTechDesk.Api.dll")]
    public async Task Los_archivos_de_configuracion_y_adjuntos_no_se_sirven_como_estaticos(string path)
    {
        await using var h = await ApiHost.StartAsync();
        var r = await h.Http.GetAsync(path);
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Las_respuestas_de_la_API_no_se_guardan_en_cache_y_no_hay_CORS()
    {
        await using var h = await ApiHost.StartAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/catalogs");
        req.Headers.Add("Origin", "https://sitio-malicioso.example");
        var r = await h.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(r.Headers.CacheControl!.NoStore);
        Assert.False(r.Headers.Contains("Access-Control-Allow-Origin"));
        using var pre = new HttpRequestMessage(HttpMethod.Options, "/api/tickets");
        pre.Headers.Add("Origin", "https://sitio-malicioso.example");
        pre.Headers.Add("Access-Control-Request-Method", "POST");
        Assert.False((await h.Http.SendAsync(pre)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Catalogos_y_ruta_desconocida()
    {
        await using var h = await ApiHost.StartAsync();
        var c = await h.Get("/api/catalogs");
        Assert.Equal(HttpStatusCode.OK, c.Status);
        Assert.Equal(8, c["majors"].GetArrayLength());
        Assert.Equal("Ingeniería en Sistemas", c["majors"][0].GetProperty("name").GetString());
        Assert.Equal(3, c["campuses"].GetArrayLength());

        var nf = await h.Get("/api/no-existe");
        Assert.Equal(HttpStatusCode.NotFound, nf.Status);
        Assert.Equal("application/problem+json", nf.Raw.Content.Headers.ContentType!.MediaType);
        Assert.Equal("No encontramos ese recurso.", nf.Detail);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Get("/api/admin/tickets/abc")).Status);
    }

    [Fact]
    public async Task Un_error_inesperado_da_500_generico_sin_detalles_internos()
    {
        await using var h = await ApiHost.StartAsync(services: s => s.AddSingleton<ICatalogRepository>(new ThrowingCatalogs()));
        var r = await h.Get("/api/catalogs");
        Assert.Equal(HttpStatusCode.InternalServerError, r.Status);
        Assert.Equal("Ocurrió un error inesperado. Inténtalo de nuevo en unos minutos.", r.Detail);
        var raw = r.Json.GetRawText();
        Assert.DoesNotContain("secreto interno", raw);
        Assert.DoesNotContain("   at ", raw);
        Assert.DoesNotContain("InvalidOperation", raw);
    }

    private sealed class ThrowingCatalogs : ICatalogRepository
    {
        public Task<Catalogs> GetCatalogsAsync(CancellationToken ct) => throw new InvalidOperationException("secreto interno: cadena de conexión Server=...");
    }

    // ------------------------------------------------------------------ Login y sesión ------------------------------------------------------------------

    [Fact]
    public async Task Login_correcto_devuelve_token_vencimiento_y_usuario()
    {
        await using var h = await ApiHost.StartAsync();
        var r = await h.Post("/api/auth/login", new { username = "admin", password = ApiHost.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(3, r["token"].GetString()!.Split('.').Length);
        Assert.True(r["expiresAt"].GetDateTime() > DateTime.UtcNow.AddMinutes(50));
        Assert.Equal("Administración", r["user"].GetProperty("name").GetString());
        Assert.Equal("ADMIN", r["user"].GetProperty("role").GetString());
        Assert.DoesNotContain("hash", r.Json.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_incorrecto_da_401_y_tras_5_fallos_la_cuenta_se_bloquea()
    {
        await using var h = await ApiHost.StartAsync();
        for (var i = 0; i < 5; i++)
        {
            var bad = await h.Post("/api/auth/login", new { username = "admin", password = "mala" + i });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.Status);
            Assert.Equal(UniTechDesk.Core.Services.AuthService.BadCredentials, bad.Detail);
        }
        var locked = await h.Post("/api/auth/login", new { username = "admin", password = ApiHost.AdminPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.Status);      // bloqueada: igual que una contraseña incorrecta
        Assert.Equal(UniTechDesk.Core.Services.AuthService.BadCredentials, locked.Detail);
    }

    [Theory]
    [InlineData("' OR 1=1 --", "x")]
    [InlineData("admin'; DROP TABLE Personal;--", "x")]
    [InlineData("admin", "' OR '1'='1")]
    public async Task Intentos_de_inyeccion_SQL_en_el_login_son_simples_credenciales_incorrectas(string user, string pass)
    {
        await using var h = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Post("/api/auth/login", new { username = user, password = pass })).Status);
    }

    [Fact]
    public async Task Login_con_cuerpo_invalido_o_vacio()
    {
        await using var h = await ApiHost.StartAsync();
        using var bad = new StringContent("{no es json", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Http.PostAsync("/api/auth/login", bad)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Post("/api/auth/login", new { })).Status);
    }

    [Fact]
    public async Task El_panel_exige_un_token_valido()
    {
        await using var h = await ApiHost.StartAsync();
        var none = await h.Get("/api/admin/tickets");
        Assert.Equal(HttpStatusCode.Unauthorized, none.Status);
        Assert.Equal("Bearer", none.Header("WWW-Authenticate"));
        Assert.Equal("Tu sesión expiró. Inicia sesión de nuevo.", none.Detail);

        foreach (var bad in new[] { "basura", "a.b.c", new string('x', 5000) })
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.Get("/api/admin/tickets", bad)).Status);
        using var basic = new HttpRequestMessage(HttpMethod.Get, "/api/admin/tickets");
        basic.Headers.TryAddWithoutValidation("Authorization", "Basic YWRtaW46YWRtaW4=");
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Http.SendAsync(basic)).StatusCode);

        var token = await h.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await h.Get("/api/admin/tickets", token)).Status);
    }

    [Fact]
    public async Task Tokens_vencidos_ajenos_o_de_cuentas_desactivadas_se_rechazan()
    {
        await using var h = await ApiHost.StartAsync();
        const string key = "clave-de-pruebas-http-clave-de-pruebas-http-0123456789";
        var account = new StaffAccount { Id = h.AdminId, FullName = "Administración", Role = "ADMIN", Active = true };

        var good = new JwtService(new JwtOptions { SigningKey = key });
        Assert.Equal(HttpStatusCode.OK, (await h.Get("/api/admin/staff", good.Issue(account, DateTime.UtcNow).Token)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Get("/api/admin/staff", good.Issue(account, DateTime.UtcNow.AddHours(-3)).Token)).Status);

        var otherKey = new JwtService(new JwtOptions { SigningKey = "otra-clave-completamente-distinta-0123456789-abcdef" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Get("/api/admin/staff", otherKey.Issue(account, DateTime.UtcNow).Token)).Status);

        var ghost = new StaffAccount { Id = 9999, FullName = "Fantasma", Role = "ADMIN", Active = true };
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Get("/api/admin/staff", good.Issue(ghost, DateTime.UtcNow).Token)).Status);

        var tecId = h.Store.AddStaff("inactivo", "Inactivo", "TECNICO", "x", active: false);
        var inactive = new StaffAccount { Id = tecId, FullName = "Inactivo", Role = "TECNICO", Active = true };
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Get("/api/admin/staff", good.Issue(inactive, DateTime.UtcNow).Token)).Status);
    }

    // ------------------------------------------------------------------ Crear solicitud ------------------------------------------------------------------

    [Fact]
    public async Task Crear_una_solicitud_con_foto_y_descargar_el_adjunto_con_enlace_firmado()
    {
        await using var h = await ApiHost.StartAsync();
        var r = await h.CreateTicketAsync(Env.StudentHardware(), Png());
        Assert.Equal(HttpStatusCode.Created, r.Status);
        Assert.Equal("UTD-1001", r["code"].GetString());
        Assert.EndsWith("Z", r["createdAt"].GetString());           // UTC explícito: el navegador no lo interpreta como hora local

        var token = await h.LoginAsync();
        var detail = await h.Get("/api/admin/tickets/1001", token);
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        Assert.Equal("Ana López", detail["fullName"].GetString());
        Assert.Equal("OPEN", detail["status"].GetString());
        var att = detail["attachments"][0];
        Assert.Equal("foto.png", att.GetProperty("name").GetString());
        Assert.Equal("image/png", att.GetProperty("type").GetString());
        var url = att.GetProperty("url").GetString()!;
        Assert.StartsWith("/api/files/", url);

        var file = await h.Http.GetAsync(url);                        // el enlace firmado no necesita el token
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/png", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Samples.Png(), await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("default-src 'none'; sandbox", file.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("inline", file.Content.Headers.ContentDisposition!.ToString());

        // ni el nombre en disco ni la ruta salen en ningún JSON
        Assert.DoesNotContain(".png\"", detail.Json.GetRawText().Replace("foto.png", ""));
        Assert.DoesNotContain(h.StorageDir, detail.Json.GetRawText());
    }

    [Fact]
    public async Task El_enlace_de_un_adjunto_no_sirve_alterado_ni_sin_firma()
    {
        await using var h = await ApiHost.StartAsync();
        await h.CreateTicketAsync(Env.StudentHardware(), Png());
        var token = await h.LoginAsync();
        var url = (await h.Get("/api/admin/tickets/1001", token))["attachments"][0].GetProperty("url").GetString()!;

        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.GetAsync("/api/files/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.GetAsync(url[..^1] + (url[^1] == '0' ? "1" : "0"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.GetAsync(url.Replace("/files/1?", "/files/2?"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Http.GetAsync("/api/files/1?e=1&s=" + new string('a', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Http.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Los_errores_de_validacion_llegan_por_campo_como_los_lee_el_front()
    {
        await using var h = await ApiHost.StartAsync();
        var bad = Env.StudentHardware();
        bad.Email = "no-es-correo"; bad.Hardware!.Brand = "";
        var r = await h.CreateTicketAsync(bad);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
        Assert.Equal("Revisa los datos del formulario.", r.Detail);
        Assert.Equal("Correo no válido.", r["errors"].GetProperty("email")[0].GetString());
        Assert.Equal("Escribe la marca.", r["errors"].GetProperty("hardware.brand")[0].GetString());
        Assert.Equal(0, h.Store.TicketCount);
    }

    [Fact]
    public async Task Formularios_mal_formados_dan_400()
    {
        await using var h = await ApiHost.StartAsync();

        using var json = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");        // no es multipart
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Http.PostAsync("/api/tickets", json)).StatusCode);

        using var noData = new MultipartFormDataContent { { new StringContent("x"), "otra" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Http.PostAsync("/api/tickets", noData)).StatusCode);

        using var badJson = new MultipartFormDataContent { { new StringContent("{ esto no es json"), "data" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Http.PostAsync("/api/tickets", badJson)).StatusCode);

        using var truncated = new StringContent("--x\r\nContent-Disposition: form-data; name=\"data\"\r\n\r\n{", System.Text.Encoding.UTF8);
        truncated.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data") { Parameters = { new System.Net.Http.Headers.NameValueHeaderValue("boundary", "x") } };
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Http.PostAsync("/api/tickets", truncated)).StatusCode);
        Assert.Equal(0, h.Store.TicketCount);
    }

    [Fact]
    public async Task Un_archivo_falso_se_rechaza_y_no_deja_nada_en_disco()
    {
        await using var h = await ApiHost.StartAsync();
        var r = await h.CreateTicketAsync(Env.StudentHardware(), new ApiHost.FilePart("virus.png", Samples.Exe(), "image/png"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
        Assert.Contains("virus.png", r.Detail);
        Assert.Equal(0, h.Store.TicketCount);
        Assert.False(Directory.Exists(h.StorageDir) && Directory.EnumerateFiles(h.StorageDir).Any());
    }

    [Fact]
    public async Task El_campo_trampa_los_archivos_de_mas_y_los_demasiado_grandes()
    {
        await using var h = await ApiHost.StartAsync();
        var trap = Env.StudentHardware(); trap.Hp = "bot";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await h.CreateTicketAsync(trap)).Status);

        var six = Enumerable.Range(0, 6).Select(i => Png($"f{i}.png")).ToArray();
        var many = await h.CreateTicketAsync(Env.StudentHardware(), six);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, many.Status);
        Assert.Contains("Máximo 5", many.Detail);

        var big = await h.CreateTicketAsync(Env.StudentHardware(), new ApiHost.FilePart("grande.png", Samples.Png(6 * 1024 * 1024), "image/png"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, big.Status);
        Assert.Contains("el máximo es 5 MB", big.Detail);
        Assert.Equal(0, h.Store.TicketCount);
    }

    [Fact]
    public async Task Un_cuerpo_gigante_se_corta_con_413_o_se_cierra_la_conexion_y_no_se_crea_nada()
    {
        await using var h = await ApiHost.StartAsync(a => { a.Add("--Uploads:MaxFiles=1"); a.Add("--Uploads:MaxFileSizeMB=1"); });
        try
        {
            var r = await h.CreateTicketAsync(Env.StudentHardware(), new ApiHost.FilePart("enorme.png", Samples.Png(6 * 1024 * 1024), "image/png"));
            Assert.True(r.Status is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity, "estado: " + r.Status);
        }
        catch (HttpRequestException) { /* el servidor cerró la conexión antes de recibir todo: también es un rechazo válido */ }
        Assert.Equal(0, h.Store.TicketCount);
    }

    [Fact]
    public async Task Un_JSON_enorme_en_un_endpoint_normal_se_rechaza()
    {
        await using var h = await ApiHost.StartAsync();
        var body = new { code = "UTD-1001", email = new string('a', 200 * 1024) + "@example.com" };
        try
        {
            var r = await h.Post("/api/tickets/track", body);
            Assert.True(r.Status is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, "estado: " + r.Status);
        }
        catch (HttpRequestException) { }
    }

    // ------------------------------------------------------------------ Flujo completo por HTTP ------------------------------------------------------------------

    [Fact]
    public async Task Flujo_completo_por_HTTP_de_la_solicitud_a_la_entrega()
    {
        await using var h = await ApiHost.StartAsync();
        var code = await h.CreateCodeAsync();
        var id = ApiHost.IdOf(code);
        var token = await h.LoginAsync();

        // 1) el cliente consulta
        var track = await h.Post("/api/tickets/track", new { code, email = Env.ClientEmail });
        Assert.Equal(HttpStatusCode.OK, track.Status);
        Assert.Equal("OPEN", track["status"].GetString());
        Assert.Equal(JsonValueKind.Null, track["quote"].ValueKind);
        Assert.Equal("TRANSFER", track["preferredPaymentMethod"].GetString());
        Assert.False(track.Json.TryGetProperty("email", out _));
        Assert.False(track.Json.TryGetProperty("phone", out _));
        Assert.False(track.Json.TryGetProperty("fullName", out _));

        // 2) el personal revisa, cotiza y envía la cotización
        await h.PublishQuoteAsync(token, code, 3000m);
        var quoted = await h.Post("/api/tickets/track", new { code, email = Env.ClientEmail });
        Assert.Equal("AWAITING_APPROVAL", quoted["status"].GetString());
        Assert.Equal(3000m, quoted["quote"].GetProperty("amount").GetDecimal());
        Assert.Equal("NIO", quoted["quote"].GetProperty("currency").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", quoted["quote"].GetProperty("validUntil").GetString());
        Assert.True(quoted["quote"].GetProperty("published").GetBoolean());

        // 3) un correo ajeno no puede responder; el cliente aprueba
        Assert.Equal(HttpStatusCode.NotFound, (await h.Post($"/api/tickets/{code}/quote-decision", new { email = "intruso@example.com", decision = "APPROVED" })).Status);
        var approved = await h.Post($"/api/tickets/{code}/quote-decision", new { email = Env.ClientEmail, decision = "APPROVED" });
        Assert.Equal(HttpStatusCode.OK, approved.Status);
        Assert.Equal("PENDING", approved["paymentStatus"].GetString());
        var pay = approved["paymentInstructions"];
        Assert.Equal("TRANSFER", pay.GetProperty("method").GetString());
        Assert.Equal("1000-0000-0001", pay.GetProperty("accounts")[0].GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await h.Post($"/api/tickets/{code}/quote-decision", new { email = Env.ClientEmail, decision = "REJECTED" })).Status);

        // 4) el personal inicia el trabajo; entregar antes de pagar se bloquea
        Assert.Equal(HttpStatusCode.OK, (await h.Post($"/api/admin/tickets/{id}/status", new { to = "IN_PROGRESS", note = "Empezamos." }, token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.Post($"/api/admin/tickets/{id}/status", new { to = "COMPLETED" }, token)).Status);
        var early = await h.Post($"/api/admin/tickets/{id}/status", new { to = "DELIVERED" }, token);
        Assert.Equal(HttpStatusCode.Conflict, early.Status);
        Assert.Equal("No se puede entregar hasta confirmar el pago.", early.Detail);

        // 5) el cliente informa su pago con comprobante (campo "file")
        var proof = await h.PostFormAsync($"/api/tickets/{code}/payment-proof", new { email = Env.ClientEmail, reference = "REF-2026-77" }, "file",
            new ApiHost.FilePart("comprobante.pdf", Samples.Pdf(), "application/pdf"));
        Assert.Equal(HttpStatusCode.OK, proof.Status);
        Assert.Equal("PROOF_SENT", proof["paymentStatus"].GetString());
        Assert.Equal("REF-2026-77", proof["paymentReference"].GetString());

        // 6) el personal ve el comprobante, confirma el pago y entrega
        var detail = await h.Get($"/api/admin/tickets/{id}", token);
        Assert.Equal("REF-2026-77", detail["payment"].GetProperty("reference").GetString());
        Assert.Equal("comprobante.pdf", detail["payment"].GetProperty("proofName").GetString());
        var proofFile = await h.Http.GetAsync(detail["attachments"][0].GetProperty("url").GetString()!);
        Assert.Equal("application/pdf", proofFile.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment", proofFile.Content.Headers.ContentDisposition!.ToString());

        var paid = await h.Post($"/api/admin/tickets/{id}/payment", new { status = "PAID" }, token);
        Assert.Equal("PAID", paid["paymentStatus"].GetString());
        var delivered = await h.Post($"/api/admin/tickets/{id}/status", new { to = "DELIVERED", note = "Entregado." }, token);
        Assert.Equal("DELIVERED", delivered["status"].GetString());

        // 7) la línea de tiempo del cliente cuenta la historia y no incluye notas internas
        var end = await h.Post("/api/tickets/track", new { code, email = Env.ClientEmail });
        var types = end["timeline"].EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Contains("CREATED", types); Assert.Contains("QUOTE_DECISION", types); Assert.Contains("PAYMENT", types); Assert.Contains("STATUS", types);
        Assert.DoesNotContain("NOTE", types);
        Assert.DoesNotContain("QUOTE", types);
        Assert.All(end["timeline"].EnumerateArray(), e => Assert.EndsWith("Z", e.GetProperty("at").GetString()));
    }

    [Fact]
    public async Task Rechazar_la_cotizacion_por_HTTP_cancela_el_ticket()
    {
        await using var h = await ApiHost.StartAsync();
        var code = await h.CreateCodeAsync();
        var token = await h.LoginAsync();
        await h.PublishQuoteAsync(token, code, 500m);
        var r = await h.Post($"/api/tickets/{code}/quote-decision", new { email = Env.ClientEmail, decision = "REJECTED" });
        Assert.Equal("CANCELLED", r["status"].GetString());
        Assert.Equal("REJECTED", r["quote"].GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Consultas_con_datos_incorrectos_o_hostiles_dan_404_sin_errores_internos()
    {
        await using var h = await ApiHost.StartAsync();
        await h.CreateCodeAsync();
        foreach (var (code, email) in new[] { ("UTD-1001", "otra@example.com"), ("UTD-9999", Env.ClientEmail), ("UTD-1001'; DROP TABLE Ticket;--", Env.ClientEmail),
                                              ("' OR 1=1 --", "' OR 1=1 --"), ("", ""), ("UTD-1001", Env.ClientEmail + "' OR '1'='1") })
        {
            var r = await h.Post("/api/tickets/track", new { code, email });
            Assert.Equal(HttpStatusCode.NotFound, r.Status);
            Assert.Equal("No encontramos un ticket con ese código y correo. Revisa que estén bien escritos.", r.Detail);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await h.Post("/api/tickets/track", new { })).Status);
        using var bad = new StringContent("{no", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Http.PostAsync("/api/tickets/track", bad)).StatusCode);
    }

    // ------------------------------------------------------------------ Panel del personal ------------------------------------------------------------------

    [Fact]
    public async Task Panel_listado_estadisticas_asignacion_y_notas()
    {
        await using var h = await ApiHost.StartAsync();
        var a = await h.CreateCodeAsync();
        var b = await h.CreateCodeAsync(Env.ExternalSoftware());
        var token = await h.LoginAsync();

        var list = await h.Get("/api/admin/tickets?status=OPEN&sort=date_asc&page=1&pageSize=1", token);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal(2, list["total"].GetInt32());
        Assert.Equal(1, list["pageSize"].GetInt32());
        Assert.Equal(a, list["items"][0].GetProperty("code").GetString());
        Assert.Equal(0, list["items"][0].GetProperty("history").GetArrayLength());

        var search = await h.Get("/api/admin/tickets?q=empresa%20abc", token);
        Assert.Equal(b, search["items"][0].GetProperty("code").GetString());
        Assert.Equal(1, search["total"].GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await h.Get("/api/admin/tickets?status=NADA", token)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Get("/api/admin/tickets?sort=precio", token)).Status);

        var stats = await h.Get("/api/admin/tickets/stats", token);
        Assert.Equal(2, stats["total"].GetInt32());
        Assert.Equal(2, stats["open"].GetInt32());
        Assert.Equal(2, stats["active"].GetInt32());
        Assert.Equal(0, stats["pendingPayment"].GetInt32());
        Assert.Equal(0, stats["urgentActive"].GetInt32());

        var staff = await h.Get("/api/admin/staff", token);
        Assert.Equal(2, staff.Json.GetArrayLength());
        var tecId = staff.Json.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Técnico Uno").GetProperty("id").GetInt32();

        var assigned = await h.Put("/api/admin/tickets/1001/assignee", new { staffId = tecId }, token);
        Assert.Equal("Técnico Uno", assigned["assignee"].GetProperty("name").GetString());
        var removed = await h.Put("/api/admin/tickets/1001/assignee", new { staffId = (int?)null }, token);
        Assert.Equal(JsonValueKind.Null, removed["assignee"].ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Put("/api/admin/tickets/1001/assignee", new { staffId = 9999 }, token)).Status);

        var note = await h.Post("/api/admin/tickets/1001/notes", new { text = "Cliente pidió llamar por la tarde." }, token);
        var last = note["history"].EnumerateArray().Last();
        Assert.Equal("NOTE", last.GetProperty("type").GetString());
        Assert.True(last.GetProperty("internal").GetBoolean());
        Assert.Equal("Administración", last.GetProperty("by").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await h.Post("/api/admin/tickets/1001/notes", new { text = "" }, token)).Status);

        Assert.Equal(HttpStatusCode.NotFound, (await h.Get("/api/admin/tickets/9999", token)).Status);
        // La cotización solo se edita en revisión/cotizado: en "Abierto" es un conflicto de estado; ya en revisión, los datos inválidos dan 422.
        Assert.Equal(HttpStatusCode.Conflict, (await h.Put("/api/admin/tickets/1001/quote", new { amount = 100, currency = "NIO", description = "Reparación completa." }, token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.Post("/api/admin/tickets/1001/status", new { to = "IN_REVIEW" }, token)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await h.Put("/api/admin/tickets/1001/quote", new { amount = -5, currency = "NIO", description = "x" }, token)).Status);
    }

    [Fact]
    public async Task Los_estados_validos_pasan_y_los_invalidos_dan_409_o_400()
    {
        await using var h = await ApiHost.StartAsync();
        var code = await h.CreateCodeAsync();
        var token = await h.LoginAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await h.Post("/api/admin/tickets/1001/status", new { to = "DELIVERED" }, token)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Post("/api/admin/tickets/1001/status", new { to = "NADA" }, token)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await h.Post("/api/admin/tickets/1001/status", new { to = "CANCELLED" }, token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await h.Post("/api/admin/tickets/1001/status", new { to = "CANCELLED", note = "Duplicado." }, token)).Status);
        var again = await h.Post("/api/admin/tickets/1001/status", new { to = "IN_REVIEW", note = "Reabierto." }, token);
        Assert.Equal("IN_REVIEW", again["status"].GetString());
        _ = code;
    }

    // ------------------------------------------------------------------ Correos ------------------------------------------------------------------

    [Fact]
    public async Task El_proceso_en_segundo_plano_envia_los_correos_pendientes()
    {
        await using var h = await ApiHost.StartAsync();
        await h.CreateCodeAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && h.Store.Mails().Any(m => m.State != "SENT")) await Task.Delay(100);
        var mail = Assert.Single(h.Store.Mails());
        Assert.Equal("TICKET_CREATED", mail.Template);
        Assert.Equal("SENT", mail.State);
    }

    // ------------------------------------------------------------------ Límites de uso ------------------------------------------------------------------

    [Fact]
    public async Task Limite_de_inicios_de_sesion_por_IP()
    {
        await using var h = await ApiHost.StartAsync();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 16; i++) statuses.Add((await h.Post("/api/auth/login", new { username = "nadie" + i, password = "x" })).Status);
        Assert.All(statuses.Take(15), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        var limited = await h.Post("/api/auth/login", new { username = "otro", password = "x" });
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[15]);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.NotNull(limited.Header("Retry-After"));
        Assert.Equal("Demasiadas solicitudes. Espera un momento e inténtalo de nuevo.", limited.Detail);
    }

    [Fact]
    public async Task Limite_de_solicitudes_nuevas_por_IP()
    {
        await using var h = await ApiHost.StartAsync();
        for (var i = 0; i < 15; i++) Assert.Equal(HttpStatusCode.Created, (await h.CreateTicketAsync()).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await h.CreateTicketAsync()).Status);
        Assert.Equal(15, h.Store.TicketCount);
    }

    [Fact]
    public async Task Limite_de_consultas_por_IP_frena_la_busqueda_de_codigos()
    {
        await using var h = await ApiHost.StartAsync();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 41; i++) codes.Add((await h.Post("/api/tickets/track", new { code = "UTD-" + (2000 + i), email = "x@example.com" })).Status);
        Assert.All(codes.Take(40), s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[40]);
    }
}
