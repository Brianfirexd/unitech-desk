using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UniTechDesk.Api;
using UniTechDesk.Api.Infrastructure;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.TestSupport;

namespace UniTechDesk.Tests.Support;

/// <summary>
/// Arranca la API REAL (UniTechApp.Build: middleware, autenticación JWT, límites, CSP, endpoints) en un puerto local libre, con la base
/// en memoria en lugar de SQL Server. Así las pruebas ejercitan exactamente el mismo código HTTP que producción.
/// </summary>
public sealed class ApiHost : IAsyncDisposable
{
    public const string AdminPassword = "Admin#2026";
    private static readonly Lazy<string> AdminHash = new(() => new BCryptPasswordHasher().Hash(AdminPassword));
    public static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web);

    public WebApplication App { get; }
    public HttpClient Http { get; }
    public InMemoryStore Store { get; }
    public string BaseUrl { get; }
    public string StorageDir { get; }
    public string Root { get; }
    public int AdminId { get; }

    private ApiHost(WebApplication app, HttpClient http, InMemoryStore store, string baseUrl, string root, string storage, int adminId)
    {
        App = app; Http = http; Store = store; BaseUrl = baseUrl; Root = root; StorageDir = storage; AdminId = adminId;
    }

    public static async Task<ApiHost> StartAsync(Action<List<string>>? extraArgs = null, Action<IServiceCollection>? services = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "utd-host-" + Guid.NewGuid().ToString("N"));
        var web = Path.Combine(root, "wwwroot");
        var storage = Path.Combine(root, "storage");
        Directory.CreateDirectory(Path.Combine(web, "js"));
        await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<!doctype html><html><head><title>UniTech Desk</title></head><body>hola</body></html>");
        await File.WriteAllTextAsync(Path.Combine(web, "js", "app.js"), "console.log('ok');");

        var args = new List<string>
        {
            "--ConnectionStrings:UniTechDesk=Server=127.0.0.1;Database=UniTechDesk;User Id=unitech_api;Password=prueba;TrustServerCertificate=True",
            "--Jwt:SigningKey=clave-de-pruebas-http-clave-de-pruebas-http-0123456789",
            "--Email:Provider=Console",
            "--App:PublicBaseUrl=http://127.0.0.1",
            "--Uploads:StoragePath=" + storage,
            "--Payment:BankAccounts:0:Bank=Banco de Ejemplo", "--Payment:BankAccounts:0:Currency=NIO",
            "--Payment:BankAccounts:0:Number=1000-0000-0001", "--Payment:BankAccounts:0:Holder=Titular de Ejemplo",
            "--Logging:LogLevel:Default=Error", "--Logging:LogLevel:Microsoft=Error"
        };
        extraArgs?.Invoke(args);

        var store = new InMemoryStore(TimeProvider.System);
        var adminId = store.AddStaff("admin", "Administración", "ADMIN", AdminHash.Value);
        store.AddStaff("tecnico1", "Técnico Uno", "TECNICO", AdminHash.Value);

        var app = UniTechApp.Build(args.ToArray(), s =>
        {
            s.AddSingleton<ITicketRepository>(store);
            s.AddSingleton<IStaffRepository>(store);
            s.AddSingleton<ICatalogRepository>(store);
            s.AddSingleton<IOutboxRepository>(store);
            services?.Invoke(s);
        }, new WebApplicationOptions { ContentRootPath = root, WebRootPath = web, EnvironmentName = Environments.Production, Args = args.ToArray() });
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var url = app.Urls.First().TrimEnd('/');
        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
        return new ApiHost(app, http, store, url, root, storage, adminId);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }

    // ---------------- Atajos ----------------

    public sealed record Reply(HttpStatusCode Status, JsonElement Json, HttpResponseMessage Raw)
    {
        public string? Header(string name) =>
            Raw.Headers.TryGetValues(name, out var v) || Raw.Content.Headers.TryGetValues(name, out v) ? string.Join(", ", v) : null;

        public string Detail => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
        public JsonElement this[string name] => Json.GetProperty(name);
    }

    private static async Task<Reply> ToReply(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        JsonElement json = default;
        if (!string.IsNullOrWhiteSpace(text) && r.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            json = JsonDocument.Parse(text).RootElement.Clone();
        return new Reply(r.StatusCode, json, r);
    }

    public async Task<Reply> SendAsync(HttpMethod method, string url, object? body = null, string? token = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body, Camel), Encoding.UTF8, "application/json");
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await Http.SendAsync(req);
        return await ToReply(resp);
    }

    public Task<Reply> Get(string url, string? token = null) => SendAsync(HttpMethod.Get, url, null, token);
    public Task<Reply> Post(string url, object? body = null, string? token = null) => SendAsync(HttpMethod.Post, url, body ?? new { }, token);
    public Task<Reply> Put(string url, object? body = null, string? token = null) => SendAsync(HttpMethod.Put, url, body ?? new { }, token);

    public async Task<string> LoginAsync(string user = "admin", string password = AdminPassword)
    {
        var r = await Post("/api/auth/login", new { username = user, password });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r["token"].GetString()!;
    }

    public sealed record FilePart(string Name, byte[] Content, string ContentType = "application/octet-stream");

    /// <summary>Igual que js/api.js: parte "data" con el JSON como texto y las partes de archivos con su nombre.</summary>
    public async Task<Reply> PostFormAsync(string url, object data, string filePart, params FilePart[] files)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(data, Camel), Encoding.UTF8), "data");
        foreach (var f in files)
        {
            var c = new ByteArrayContent(f.Content);
            c.Headers.ContentType = new MediaTypeHeaderValue(f.ContentType);
            form.Add(c, filePart, f.Name);
        }
        var resp = await Http.PostAsync(url, form);
        return await ToReply(resp);
    }

    public Task<Reply> CreateTicketAsync(CreateTicketRequest? request = null, params FilePart[] files) =>
        PostFormAsync("/api/tickets", request ?? Env.StudentHardware(), "files", files);

    public async Task<string> CreateCodeAsync(CreateTicketRequest? request = null)
    {
        var r = await CreateTicketAsync(request);
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return r["code"].GetString()!;
    }

    public static int IdOf(string code) => int.Parse(code[4..], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Desde Abierto hasta "Esperando aprobación" usando la API del personal.</summary>
    public async Task PublishQuoteAsync(string token, string code, decimal amount = 850m, string currency = "NIO")
    {
        var id = IdOf(code);
        var until = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/admin/tickets/{id}/status", new { to = "IN_REVIEW", note = "Revisando." }, token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Put($"/api/admin/tickets/{id}/quote", new { amount, currency, description = "Limpieza interna y pasta térmica.", validUntil = until }, token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/admin/tickets/{id}/status", new { to = "QUOTED" }, token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/admin/tickets/{id}/status", new { to = "AWAITING_APPROVAL", note = "Te enviamos la cotización." }, token)).Status);
    }
}
