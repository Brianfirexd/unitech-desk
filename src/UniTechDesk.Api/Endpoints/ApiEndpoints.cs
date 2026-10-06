using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using UniTechDesk.Api.Infrastructure;
using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Files;
using UniTechDesk.Core.Options;
using UniTechDesk.Core.Services;

namespace UniTechDesk.Api.Endpoints;

public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---------------- Públicos ----------------
        api.MapGet("/catalogs", async (CatalogService catalogs, CancellationToken ct) => Results.Ok(await catalogs.GetAsync(ct)));

        api.MapPost("/auth/login", async (LoginRequest? body, AuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.LoginAsync(body ?? new LoginRequest(), ct))).RequireRateLimiting(RateLimiting.Login);

        api.MapPost("/tickets", async (HttpRequest req, PublicTicketService svc, UploadOptions uploads, JsonSerializerOptions json, CancellationToken ct) =>
        {
            var (data, files) = await ReadMultipartAsync<CreateTicketRequest>(req, "files", uploads, json, ct);
            var created = await svc.CreateAsync(data, files, req.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.Json(created, json, statusCode: StatusCodes.Status201Created);
        }).RequireRateLimiting(RateLimiting.Creation);

        api.MapPost("/tickets/track", async (TrackRequest? body, PublicTicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.TrackAsync(body?.Code, body?.Email, ct))).RequireRateLimiting(RateLimiting.Lookup);

        api.MapPost("/tickets/{code}/quote-decision", async (string code, ClientQuoteDecisionRequest? body, PublicTicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.DecideQuoteAsync(code, body?.Email, body?.Decision, ct))).RequireRateLimiting(RateLimiting.Lookup);

        api.MapPost("/tickets/{code}/payment-proof", async (string code, HttpRequest req, PublicTicketService svc, UploadOptions uploads, JsonSerializerOptions json, CancellationToken ct) =>
        {
            var (data, files) = await ReadMultipartAsync<PaymentProofRequest>(req, "file", uploads, json, ct);
            if (files.Count > 1) throw AppException.Unprocessable("Adjunta un solo comprobante.");
            return Results.Ok(await svc.SubmitPaymentProofAsync(code, data.Email, data.Reference, files.FirstOrDefault(), ct));
        }).RequireRateLimiting(RateLimiting.Lookup);

        // Descarga de adjuntos con enlace firmado y de corta vida (lo entrega el detalle del ticket al personal).
        api.MapGet("/files/{id:int}", async (int id, long? e, string? s, HttpContext ctx, ITicketRepository tickets, IAttachmentStore store,
            IFileLinkSigner signer, BusinessClock clock, CancellationToken ct) =>
        {
            if (e is null || string.IsNullOrEmpty(s) || !signer.IsValid(id, e.Value, s, clock.UtcNow))
                throw AppException.Forbidden("El enlace del archivo venció. Abre el ticket de nuevo para obtener uno nuevo.");
            var att = await tickets.FindAttachmentAsync(id, ct) ?? throw AppException.NotFound("No encontramos ese archivo.");
            var stream = store.OpenRead(att.StoredName) ?? throw AppException.NotFound("No encontramos ese archivo.");

            var isImage = att.Mime.StartsWith("image/", StringComparison.Ordinal);
            var disposition = new ContentDispositionHeaderValue(isImage ? "inline" : "attachment");
            disposition.SetHttpFileName(att.OriginalName);
            ctx.Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
            // Aunque lo suba un cliente, el archivo se sirve aislado: sin scripts, sin sniffing y dentro de un sandbox.
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            ctx.Response.Headers["Cache-Control"] = "private, max-age=300";
            return Results.Stream(stream, isImage || att.Mime == "application/pdf" ? att.Mime : "application/octet-stream");
        });

        // ---------------- Personal (requieren sesión) ----------------
        var admin = api.MapGroup("/admin").RequireAuthorization();

        admin.MapGet("/tickets", async (string? status, string? service, string? urgency, string? payment, string? q, string? sort, int? page, int? pageSize,
            StaffTicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(new TicketListParams { Status = status, Service = service, Urgency = urgency, Payment = payment, Q = q, Sort = sort, Page = page, PageSize = pageSize }, ct)));

        admin.MapGet("/tickets/stats", async (StaffTicketService svc, CancellationToken ct) => Results.Ok(await svc.GetStatsAsync(ct)));
        admin.MapGet("/tickets/{id:int}", async (int id, StaffTicketService svc, CancellationToken ct) => Results.Ok(await svc.GetAsync(id, ct)));

        admin.MapPost("/tickets/{id:int}/status", async (int id, StatusChangeRequest? body, ClaimsPrincipal user, StaffTicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.ChangeStatusAsync(Actor(user), id, body ?? new StatusChangeRequest(), ct)));

        admin.MapPut("/tickets/{id:int}/quote", async (int id, SaveQuoteRequest? body, ClaimsPrincipal user, StaffTicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.SaveQuoteAsync(Actor(user), id, body ?? new SaveQuoteRequest(), ct)));

        admin.MapPost("/tickets/{id:int}/quote-decision", async (int id, StaffQuoteDecisionRequest? body, ClaimsPrincipal user, StaffTicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.RecordQuoteDecisionAsync(Actor(user), id, body ?? new StaffQuoteDecisionRequest(), ct)));

        admin.MapPost("/tickets/{id:int}/payment", async (int id, SetPaymentRequest? body, ClaimsPrincipal user, StaffTicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.SetPaymentAsync(Actor(user), id, body ?? new SetPaymentRequest(), ct)));

        admin.MapPut("/tickets/{id:int}/assignee", async (int id, AssignRequest? body, ClaimsPrincipal user, StaffTicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.AssignAsync(Actor(user), id, body ?? new AssignRequest(), ct)));

        admin.MapPost("/tickets/{id:int}/notes", async (int id, NoteRequest? body, ClaimsPrincipal user, StaffTicketService svc, CancellationToken ct) =>
            Results.Ok(await svc.AddNoteAsync(Actor(user), id, body ?? new NoteRequest(), ct)));

        admin.MapGet("/staff", async (StaffTicketService svc, CancellationToken ct) => Results.Ok(await svc.ListStaffAsync(ct)));

        // Cualquier otra ruta de /api: 404 en el mismo formato de error (en vez de devolver el index.html).
        api.MapFallback("{**rest}", (HttpContext ctx) => ApiErrors.WriteAsync(ctx, 404, "No encontramos ese recurso."));
    }

    private static StaffActor Actor(ClaimsPrincipal user) =>
        new(int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!, System.Globalization.CultureInfo.InvariantCulture), user.FindFirstValue(ClaimTypes.Name) ?? "Personal");

    /// <summary>
    /// Lee un formulario multipart con la parte "data" (JSON) y archivos en la parte indicada. Los archivos se copian a memoria con
    /// tope de tamaño (no se confía en Content-Length ni en el tipo declarado: FileInspector revisa el contenido real).
    /// </summary>
    private static async Task<(T Data, List<UploadedFile> Files)> ReadMultipartAsync<T>(HttpRequest req, string filePart, UploadOptions uploads,
        JsonSerializerOptions json, CancellationToken ct) where T : new()
    {
        if (!req.HasFormContentType) throw AppException.BadRequest("Se esperaba un formulario con la parte «data».");
        IFormCollection form;
        try { form = await req.ReadFormAsync(ct); }
        // Formulario cortado o mal formado → 400. BadHttpRequestException (p. ej. 413 por tamaño) también es IOException: se deja pasar.
        catch (Exception ex) when (ex is InvalidDataException || (ex is IOException && ex is not BadHttpRequestException))
        { throw AppException.BadRequest("El formulario enviado está incompleto o no es válido."); }
        var raw = form["data"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) throw AppException.BadRequest("Faltan los datos del formulario.");
        T data;
        try { data = JsonSerializer.Deserialize<T>(raw, json) ?? new T(); }
        catch (JsonException) { throw AppException.BadRequest("Los datos del formulario no son válidos."); }

        var maxBytes = uploads.MaxFileSizeMB * 1024L * 1024L;
        var files = new List<UploadedFile>();
        foreach (var f in form.Files.Where(f => f.Name == filePart))
        {
            var name = FileInspector.SafeDisplayName(f.FileName);
            if (f.Length > maxBytes) throw AppException.Unprocessable($"«{name}» pesa {FileInspector.FormatBytes(f.Length)}; el máximo es {uploads.MaxFileSizeMB} MB.");
            await using var s = f.OpenReadStream();
            using var ms = new MemoryStream((int)Math.Min(f.Length, maxBytes));
            await s.CopyToAsync(ms, ct);
            files.Add(new UploadedFile(name, f.ContentType, ms.ToArray()));
            if (files.Count > uploads.MaxFiles + 1) break;   // el servicio rechaza el exceso con un mensaje claro
        }
        return (data, files);
    }
}
