using UniTechDesk.Core.Abstractions;

namespace UniTechDesk.Core.Files;

/// <summary>Guarda los adjuntos en una carpeta fuera de wwwroot. El nombre en disco lo genera el servidor (GUID + extensión).</summary>
public sealed class DiskAttachmentStore : IAttachmentStore
{
    private readonly string _root;

    public DiskAttachmentStore(string rootFullPath)
    {
        _root = Path.GetFullPath(rootFullPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct)
    {
        var name = Guid.NewGuid().ToString("N") + "." + extension.TrimStart('.').ToLowerInvariant();
        if (!FileInspector.IsValidStoredName(name)) throw new InvalidOperationException("Extensión de adjunto no permitida.");
        var path = Path.Combine(_root, name);
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await fs.WriteAsync(content, ct);
        return name;
    }

    public Stream? OpenRead(string storedName)
    {
        if (!FileInspector.IsValidStoredName(storedName)) return null;
        var path = Path.Combine(_root, storedName);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null;
    }

    public void DeleteQuietly(string storedName)
    {
        try
        {
            if (FileInspector.IsValidStoredName(storedName)) File.Delete(Path.Combine(_root, storedName));
        }
        catch (IOException) { /* es limpieza; si falla, queda un archivo huérfano inofensivo */ }
        catch (UnauthorizedAccessException) { }
    }
}
