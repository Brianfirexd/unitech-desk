using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Options;

namespace UniTechDesk.Core.Email;

/// <summary>Envía con la API v3 de SendGrid (POST /v3/mail/send). Responde 202 cuando acepta el correo.</summary>
public sealed class SendGridEmailSender : IEmailSender
{
    public const string Endpoint = "https://api.sendgrid.com/v3/mail/send";

    private readonly HttpClient _http;
    private readonly EmailOptions _options;

    public SendGridEmailSender(HttpClient http, EmailOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.SendGridApiKey))
            throw new EmailSendException("Falta la clave de SendGrid (Email:SendGridApiKey).", isPermanent: false);

        var payload = new SendGridPayload
        {
            Personalizations = { new Personalization { To = { new Address(message.To) } } },
            From = new Address(_options.FromEmail, NullIfEmpty(_options.FromName)),
            Subject = message.Subject,
            // SendGrid exige text/plain antes que text/html.
            Content = { new Content("text/plain", message.TextBody), new Content("text/html", message.HtmlBody) },
            // Sin rastreo de clics ni aperturas: los enlaces del correo llegan tal cual (sin pasar por un dominio de SendGrid).
            TrackingSettings = new Tracking { ClickTracking = new Toggle(false, false), OpenTracking = new Toggle(false, null) }
        };
        if (!string.IsNullOrWhiteSpace(_options.ReplyToEmail)) payload.ReplyTo = new Address(_options.ReplyToEmail, NullIfEmpty(_options.ReplyToName));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(payload, options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SendGridApiKey);

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new EmailSendException("No se pudo conectar con SendGrid.", false, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new EmailSendException("SendGrid tardó demasiado en responder.", false, ex); }

        using (response)
        {
            if ((int)response.StatusCode == 202 || response.IsSuccessStatusCode) return;
            var detail = await ReadErrorAsync(response, ct);
            var code = (int)response.StatusCode;
            // 400/413: el correo en sí es inválido → reintentar no sirve. 401/403: clave o remitente mal configurados → se reintenta
            // (al corregirlo, los pendientes salen). 429 y 5xx: transitorios.
            var permanent = code is 400 or 413 or 422;
            throw new EmailSendException($"SendGrid respondió {code}: {detail}", permanent);
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                var msg = first.TryGetProperty("message", out var m) ? m.GetString() : null;
                var field = first.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                return field is null ? msg ?? "sin detalle" : $"{msg} (campo {field})";
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException) { /* sin cuerpo útil */ }
        return "sin detalle";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed class SendGridPayload
    {
        [JsonPropertyName("personalizations")] public List<Personalization> Personalizations { get; } = new();
        [JsonPropertyName("from")] public Address From { get; set; } = null!;
        [JsonPropertyName("reply_to")] public Address? ReplyTo { get; set; }
        [JsonPropertyName("subject")] public string Subject { get; set; } = "";
        [JsonPropertyName("content")] public List<Content> Content { get; } = new();
        [JsonPropertyName("tracking_settings")] public Tracking? TrackingSettings { get; set; }
    }
    private sealed class Personalization { [JsonPropertyName("to")] public List<Address> To { get; } = new(); }
    private sealed record Address([property: JsonPropertyName("email")] string Email, [property: JsonPropertyName("name")] string? Name = null);
    private sealed record Content([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("value")] string Value);
    private sealed class Tracking
    {
        [JsonPropertyName("click_tracking")] public Toggle? ClickTracking { get; set; }
        [JsonPropertyName("open_tracking")] public Toggle? OpenTracking { get; set; }
    }
    private sealed record Toggle([property: JsonPropertyName("enable")] bool Enable, [property: JsonPropertyName("enable_text")] bool? EnableText);
}

/// <summary>Modo desarrollo: no envía nada; escribe el correo en el registro (con la dirección enmascarada).</summary>
public sealed class ConsoleEmailSender : IEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _log;
    public ConsoleEmailSender(ILogger<ConsoleEmailSender> log) => _log = log;

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        _log.LogInformation("[Correo simulado] Para: {To} · Asunto: {Subject}\n{Body}", Mask(message.To), message.Subject, message.TextBody);
        return Task.CompletedTask;
    }

    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? "***" : email[0] + "***" + email[at..];
    }
}
