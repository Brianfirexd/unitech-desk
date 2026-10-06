using System.Text;
using System.Text.RegularExpressions;

namespace UniTechDesk.Api.Cli;

/// <summary>
/// Copia el frontend (la carpeta que contiene index.html) a wwwroot y lo deja listo para hablar con esta API:
///   · config.js:     useMock = false (usa la API real) y notifications.email = true (el backend sí envía correos);
///   · index.html:    quita la carga de js/mock-api.js (solo era para demostración);
///   · new-ticket.js: los documentos permitidos son solo PDF (la base de datos solo acepta JPG, PNG, GIF, WEBP y PDF).
/// Es idempotente: si el front ya está ajustado, no cambia nada. Si una pieza esperada no está (porque el front cambió de forma),
/// se detiene con un mensaje claro en vez de dejar un front a medias.
/// </summary>
public static class FrontendSync
{
    public sealed record Result(int FilesCopied, IReadOnlyList<string> Applied, IReadOnlyList<string> AlreadyDone, IReadOnlyList<string> Skipped);

    private static readonly string[] CopyTop = { "index.html", "js", "dist", "assets" };
    private const long MaxFileBytes = 5L * 1024 * 1024;   // el front real no tiene archivos así de grandes: algo más grande es basura (por ejemplo, un JDK)
    private const int MaxFiles = 300;                      // el front real tiene unas 30; si hay muchos más, se copió algo que no es el front

    /// <summary>Solo se copian tipos de archivo que un front estático necesita; el resto (binarios, .java, .zip…) se ignora.</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".js", ".css", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".woff", ".woff2", ".ttf", ".otf", ".webmanifest" };

    private sealed record Patch(string File, string Name, Regex Pattern, string Replacement, Regex Done);

    private static readonly Patch[] Patches =
    {
        new("js/config.js", "config.useMock = false", new Regex(@"useMock:\s*true"), "useMock: false", new Regex(@"useMock:\s*false")),
        new("js/config.js", "config.notifications.email = true", new Regex(@"(notifications:\s*\{\s*email:\s*)false"), "${1}true", new Regex(@"notifications:\s*\{\s*email:\s*true")),
        new("index.html", "quitar js/mock-api.js", new Regex(@"[ \t]*(?:<!--[^>]*?mock[^>]*?-->\s*)?<script[^>]*src=""js/mock-api\.js""[^>]*></script>[ \t]*\r?\n?", RegexOptions.IgnoreCase), "",
            new Regex(@"^(?![\s\S]*js/mock-api\.js)[\s\S]*$")),
        new("js/views/new-ticket.js", "documentos permitidos: solo PDF", new Regex(@"var DOC_EXTS\s*=\s*\[[^\]]*\];"), "var DOC_EXTS = ['pdf'];", new Regex(@"var DOC_EXTS\s*=\s*\['pdf'\];")),
    };

    [ThreadStatic] private static int _copiedThisRun;

    public static Result Run(string source, string destination)
    {
        _copiedThisRun = 0;
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (!File.Exists(Path.Combine(source, "index.html")))
            throw new InvalidOperationException($"No encuentro index.html en «{source}». Indica la carpeta del frontend (la que contiene index.html, js, dist y assets).");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El origen y el destino son la misma carpeta.");

        // Esta herramienta borra index.html, js, dist y assets del destino antes de copiar: solo se permite sobre una carpeta nueva, vacía
        // o que ya parezca un wwwroot (con index.html), nunca sobre una carpeta cualquiera con otros archivos.
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() && !File.Exists(Path.Combine(destination, "index.html")))
            throw new InvalidOperationException($"«{destination}» tiene archivos pero no es un wwwroot (no contiene index.html). Por seguridad no se toca; elige una carpeta vacía o la wwwroot de la API.");

        var skipped = new List<string>();
        var copied = 0;
        Directory.CreateDirectory(destination);

        // Todo se prepara en una carpeta temporal junto al destino y solo al final reemplaza lo anterior: si algo falla a la mitad,
        // el wwwroot que ya funcionaba queda intacto.
        var stage = Path.Combine(Directory.GetParent(destination)?.FullName ?? destination, ".utd-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var applied = new List<string>();
        var done = new List<string>();
        try
        {
            foreach (var top in CopyTop)
            {
                var from = Path.Combine(source, top);
                if (File.Exists(from)) copied += CopyFile(from, Path.Combine(stage, top), top, skipped);
                else if (Directory.Exists(from)) copied += CopyDirectory(from, Path.Combine(stage, top), top, skipped);
                else skipped.Add($"{top} (no existe en el origen)");
            }

            foreach (var patch in Patches)
            {
                var path = Path.Combine(stage, patch.File.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) throw new InvalidOperationException($"Falta {patch.File} en el front: no se puede ajustar «{patch.Name}».");
                var original = ReadUtf8(path, out var hadBom);
                if (patch.Done.IsMatch(original)) done.Add(patch.Name);
                else if (patch.Pattern.IsMatch(original))
                {
                    File.WriteAllText(path, patch.Pattern.Replace(original, patch.Replacement, 1), new UTF8Encoding(hadBom));
                    applied.Add(patch.Name);
                }
                else throw new InvalidOperationException($"No encontré dónde ajustar «{patch.Name}» en {patch.File}. El front cambió de forma: revisa el README (sección «Conectar el frontend»).");
            }

            foreach (var top in CopyTop)
            {
                var target = Path.Combine(destination, top);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else if (File.Exists(target)) File.Delete(target);
                var staged = Path.Combine(stage, top);
                if (Directory.Exists(staged)) Directory.Move(staged, target);
                else if (File.Exists(staged)) File.Move(staged, target);
            }
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch (IOException) { /* queda una carpeta temporal; no afecta nada */ }
        }
        return new Result(copied, applied, done, skipped);
    }

    private static int CopyDirectory(string from, string to, string label, List<string> skipped)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("mock-api.js", StringComparison.OrdinalIgnoreCase)) { skipped.Add($"{label}/{name} (solo demostración)"); continue; }
            count += CopyFile(file, Path.Combine(to, name), $"{label}/{name}", skipped);
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) { skipped.Add($"{label}/{name}/"); continue; }
            count += CopyDirectory(dir, Path.Combine(to, name), $"{label}/{name}", skipped);
        }
        return count;
    }

    private static int CopyFile(string from, string to, string label, List<string> skipped)
    {
        var info = new FileInfo(from);
        if (!AllowedExtensions.Contains(Path.GetExtension(from))) { skipped.Add($"{label} (tipo de archivo que el front no usa)"); return 0; }
        if (info.Length > MaxFileBytes) { skipped.Add($"{label} ({info.Length / (1024 * 1024)} MB: demasiado grande para ser parte del front)"); return 0; }
        if (info.LinkTarget is not null) { skipped.Add($"{label} (enlace simbólico)"); return 0; }
        if (++_copiedThisRun > MaxFiles)
            throw new InvalidOperationException($"Se iban a copiar más de {MaxFiles} archivos: la carpeta de origen no parece ser solo el front. Revisa que sea la correcta (index.html, js, dist, assets).");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
        return 1;
    }

    private static string ReadUtf8(string path, out bool hadBom)
    {
        var bytes = File.ReadAllBytes(path);
        hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return new UTF8Encoding(false).GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));
    }
}
