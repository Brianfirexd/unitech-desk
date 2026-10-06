using System.Text.RegularExpressions;
using UniTechDesk.Core.Validation;

namespace UniTechDesk.Core.Files;

/// <summary>Resultado de revisar un archivo subido: tipo real (por su contenido) y nombre seguro para mostrar.</summary>
public sealed record InspectedFile(string Mime, string Extension, string DisplayName);

/// <summary>
/// Valida adjuntos por su CONTENIDO (firma de bytes), no por lo que diga el navegador. Solo tipos que acepta la BD
/// (CK_Adjunto_Mime): JPEG, PNG, GIF, WebP y PDF.
/// </summary>
public static class FileInspector
{
    public static readonly string[] RequestImageExts = { "jpg", "jpeg", "png", "gif", "webp" };
    public static readonly string[] RequestSoftwareExts = { "jpg", "jpeg", "png", "gif", "webp", "pdf" };
    public static readonly string[] ProofExts = { "jpg", "jpeg", "png", "pdf" };

    private static readonly Regex StoredNameRx = new(@"^[0-9a-f]{32}\.(jpg|png|gif|webp|pdf)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValidStoredName(string name) => StoredNameRx.IsMatch(name);

    public static string? DetectMime(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "image/png";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return "image/gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-') return "application/pdf";
        return null;
    }

    public static string ExtensionFor(string mime) => mime switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "application/pdf" => "pdf",
        _ => throw new ArgumentOutOfRangeException(nameof(mime))
    };

    private static string[] FamilyFor(string mime) => mime == "image/jpeg" ? new[] { "jpg", "jpeg" } : new[] { ExtensionFor(mime) };

    /// <summary>Valida un archivo y devuelve su tipo real. Lanza AppException (422) con un mensaje claro si no sirve.</summary>
    public static InspectedFile Inspect(string fileName, byte[] content, IReadOnlyCollection<string> allowedExts, int maxBytes)
    {
        var display = SafeDisplayName(fileName);
        if (content.Length == 0) throw AppException.Unprocessable($"«{display}» está vacío.");
        if (content.Length > maxBytes)
            throw AppException.Unprocessable($"«{display}» pesa {FormatBytes(content.Length)}; el máximo es {maxBytes / (1024 * 1024)} MB.");

        var ext = Path.GetExtension(display).TrimStart('.').ToLowerInvariant();
        if (!allowedExts.Contains(ext))
            throw AppException.Unprocessable($"«{display}»: formato no permitido (solo {string.Join(", ", allowedExts).ToUpperInvariant()}).");

        var mime = DetectMime(content);
        if (mime is null || !FamilyFor(mime).Any(allowedExts.Contains))
            throw AppException.Unprocessable($"«{display}» no es un archivo válido (su contenido no coincide con su formato).");

        return new InspectedFile(mime, ExtensionFor(mime), display);
    }

    /// <summary>Nombre para mostrar: sin carpetas, sin caracteres de control ni de dirección de texto, máx. 255 con su extensión.</summary>
    public static string SafeDisplayName(string? fileName)
    {
        var name = fileName ?? "";
        var cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (cut >= 0) name = name[(cut + 1)..];
        name = Rules.Clean(name);
        if (name.Length == 0 || name == "." || name == "..") name = "archivo";
        if (name.Length > 255)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = "";
            name = name[..(255 - ext.Length)] + ext;
        }
        return name;
    }

    public static string FormatBytes(long bytes) =>
        bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB"
        : (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}
