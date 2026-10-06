using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using UniTechDesk.Core;

namespace UniTechDesk.Api.Infrastructure;

/// <summary>Errores en el formato que lee js/api.js: { title, detail, errors: { campo: [mensaje] } } (ProblemDetails).</summary>
public static class ApiErrors
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="headers">Cabeceras que deben viajar con el error (WWW-Authenticate, Retry-After). Se ponen DESPUÉS de limpiar la
    /// respuesta: Response.Clear() borra las cabeceras previas, así que fijarlas antes las perdería.</param>
    public static async Task WriteAsync(HttpContext ctx, int status, string message, IReadOnlyDictionary<string, string>? fieldErrors = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.Clear();
        if (headers is not null) foreach (var (name, value) in headers) ctx.Response.Headers[name] = value;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json; charset=utf-8";
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"https://httpstatuses.io/{status}",
            ["title"] = message,
            ["status"] = status,
            ["detail"] = message,
            ["traceId"] = ctx.TraceIdentifier
        };
        if (fieldErrors is { Count: > 0 }) body["errors"] = fieldErrors.ToDictionary(kv => kv.Key, kv => new[] { kv.Value });
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}

/// <summary>Convierte excepciones en respuestas claras en español. Nunca filtra detalles internos.</summary>
public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _log;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> log) { _next = next; _log = log; }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (AppException ex) { await ApiErrors.WriteAsync(ctx, ex.StatusCode, ex.Message, ex.FieldErrors); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { /* el cliente cerró la conexión */ }
        catch (BadHttpRequestException ex)
        {
            var status = ex.StatusCode is 413 ? 413 : 400;
            await ApiErrors.WriteAsync(ctx, status, status == 413 ? "Los archivos son demasiado grandes." : "La solicitud no es válida.");
        }
        catch (InvalidDataException) { await ApiErrors.WriteAsync(ctx, 400, "El formulario enviado no es válido."); }
        catch (JsonException) { await ApiErrors.WriteAsync(ctx, 400, "Los datos enviados no son válidos."); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error no controlado en {Method} {Path} (traza {Trace}).", ctx.Request.Method, ctx.Request.Path.Value, ctx.TraceIdentifier);
            await ApiErrors.WriteAsync(ctx, 500, "Ocurrió un error inesperado. Inténtalo de nuevo en unos minutos.");
        }
    }
}

/// <summary>Cabeceras de seguridad. La CSP es estricta porque el front no usa scripts ni estilos en línea.</summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _csp;

    public SecurityHeadersMiddleware(RequestDelegate next, bool allowTurnstile)
    {
        _next = next;
        var turnstile = allowTurnstile ? " https://challenges.cloudflare.com" : "";
        _csp = "default-src 'self'; " +
               $"script-src 'self'{turnstile}; " +
               "style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; " +
               $"frame-src{(allowTurnstile ? turnstile : " 'none'")}; " +
               "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            if (!h.ContainsKey("Content-Security-Policy")) h["Content-Security-Policy"] = _csp;
            if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
            return Task.CompletedTask;
        });
        return _next(ctx);
    }
}

/// <summary>Tope del tamaño del cuerpo por ruta: pequeño para JSON, mayor solo donde se suben archivos.</summary>
public sealed class BodyLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly long _uploadLimit;

    public BodyLimitMiddleware(RequestDelegate next, long uploadLimit) { _next = next; _uploadLimit = uploadLimit; }

    public Task InvokeAsync(HttpContext ctx)
    {
        var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            var path = ctx.Request.Path;
            var isUpload = HttpMethods.IsPost(ctx.Request.Method) &&
                (path.Equals("/api/tickets", StringComparison.OrdinalIgnoreCase) ||
                 path.Value?.EndsWith("/payment-proof", StringComparison.OrdinalIgnoreCase) == true);
            feature.MaxRequestBodySize = isUpload ? _uploadLimit : 64 * 1024;
        }
        return _next(ctx);
    }
}

public static class RateLimiting
{
    public const string Creation = "creation";
    public const string Lookup = "lookup";
    public const string Login = "login";

    public static void Configure(RateLimiterOptions o)
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.OnRejected = async (context, ct) =>
        {
            Dictionary<string, string>? headers = null;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                headers = new() { ["Retry-After"] = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            await ApiErrors.WriteAsync(context.HttpContext, 429, "Demasiadas solicitudes. Espera un momento e inténtalo de nuevo.", headers: headers);
        };

        // Por dirección IP. En una red universitaria muchas personas comparten IP, por eso los límites son generosos.
        string Ip(HttpContext c) => ClientKey(c.Connection.RemoteIpAddress);
        o.AddPolicy(Creation, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
        o.AddPolicy(Lookup, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 40, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
        o.AddPolicy(Login, c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
            c.Request.Path.StartsWithSegments("/api")
                ? RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter("static"));
    }

    /// <summary>
    /// Clave del límite de peticiones. Las direcciones IPv6 se agrupan por su prefijo /64 (lo que normalmente recibe un solo
    /// usuario o red): si no, quien tiene un /64 entero podría rotar de dirección y saltarse los límites.
    /// </summary>
    public static string ClientKey(System.Net.IPAddress? ip)
    {
        if (ip is null) return "desconocida";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        for (var i = 8; i < bytes.Length; i++) bytes[i] = 0;
        return new System.Net.IPAddress(bytes).ToString() + "/64";
    }
}
