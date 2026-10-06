using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Domain;

namespace UniTechDesk.TestSupport;

/// <summary>Archivos "en disco" guardados en memoria; registra qué se guardó y qué se borró (para probar que no quedan huérfanos).</summary>
public sealed class MemoryAttachmentStore : IAttachmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _files = new();
    public List<string> Deleted { get; } = new();

    public Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct)
    {
        var name = Guid.NewGuid().ToString("N") + "." + extension;
        lock (_gate) _files[name] = content.ToArray();
        return Task.FromResult(name);
    }

    public Stream? OpenRead(string storedName)
    {
        lock (_gate) return _files.TryGetValue(storedName, out var b) ? new MemoryStream(b.ToArray(), writable: false) : null;
    }

    public void DeleteQuietly(string storedName)
    {
        lock (_gate) { _files.Remove(storedName); Deleted.Add(storedName); }
    }

    public int Count { get { lock (_gate) return _files.Count; } }
    public IReadOnlyCollection<string> Names { get { lock (_gate) return _files.Keys.ToList(); } }
}

/// <summary>Hash "de mentira" (rápido) para las pruebas de servicios. La prueba de BCrypt real está aparte.</summary>
public sealed class PlainHasher : IPasswordHasher
{
    public string Hash(string password) => "plain:" + password;
    public bool Verify(string password, string hash) => hash == "plain:" + password;
}

public sealed class FakeTokenIssuer : ITokenIssuer
{
    public IssuedToken Issue(StaffAccount account, DateTime now) => new($"token-{account.Id}", now.AddMinutes(60));
}

public sealed class FakeCaptcha : ICaptchaVerifier
{
    public bool Enabled { get; set; }
    public bool Result { get; set; } = true;
    public int Calls { get; private set; }

    public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Result && !string.IsNullOrEmpty(token));
    }
}

/// <summary>
/// Repositorio de tickets que ejecuta una acción justo antes de cada ApplyAsync. Sirve para simular que otra persona modificó
/// el ticket mientras este usuario lo editaba (la versión esperada ya no coincide → ConcurrencyException → reintento).
/// </summary>
public sealed class InterferingTicketRepository : ITicketRepository
{
    private readonly ITicketRepository _inner;
    public Func<int, Task>? BeforeApply { get; set; }
    public int ApplyCalls { get; private set; }

    public InterferingTicketRepository(ITicketRepository inner) => _inner = inner;

    public Task<CreatedTicket> CreateAsync(NewTicket ticket, CancellationToken ct) => _inner.CreateAsync(ticket, ct);
    public Task<TicketRecord?> FindByIdAsync(int id, CancellationToken ct) => _inner.FindByIdAsync(id, ct);
    public Task<TicketRecord?> FindByCodeAndEmailAsync(string code, string email, CancellationToken ct) => _inner.FindByCodeAndEmailAsync(code, email, ct);
    public Task<TicketPage> ListAsync(TicketQuery query, CancellationToken ct) => _inner.ListAsync(query, ct);
    public Task<TicketStats> GetStatsAsync(CancellationToken ct) => _inner.GetStatsAsync(ct);
    public Task<AttachmentRecord?> FindAttachmentAsync(int id, CancellationToken ct) => _inner.FindAttachmentAsync(id, ct);

    public async Task ApplyAsync(TicketChangeSet changes, CancellationToken ct)
    {
        ApplyCalls++;
        var hook = BeforeApply;
        if (hook is not null) await hook(changes.TicketId);
        await _inner.ApplyAsync(changes, ct);
    }
}

/// <summary>Archivos de ejemplo con la firma (los primeros bytes) correcta de cada formato.</summary>
public static class Samples
{
    public static byte[] Png(int extra = 24) => Concat(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, extra);
    public static byte[] Jpeg(int extra = 24) => Concat(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, extra);
    public static byte[] Gif(int extra = 24) => Concat(System.Text.Encoding.ASCII.GetBytes("GIF89a"), extra);
    public static byte[] Webp(int extra = 24) => Concat(System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP"), extra);
    public static byte[] Pdf(int extra = 24) => Concat(System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n"), extra);
    /// <summary>Ejecutable de Windows ("MZ"): no es ninguno de los formatos permitidos.</summary>
    public static byte[] Exe(int extra = 24) => Concat(System.Text.Encoding.ASCII.GetBytes("MZ"), extra);

    private static byte[] Concat(byte[] head, int extra) => head.Concat(Enumerable.Repeat((byte)0x41, extra)).ToArray();
}
