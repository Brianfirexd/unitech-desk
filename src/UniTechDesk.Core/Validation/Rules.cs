using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UniTechDesk.Core.Validation;

/// <summary>Reglas de formato. Réplica de js/validation.js (los mismos formatos y mensajes), con \d escrito como [0-9] para no aceptar dígitos no ASCII.</summary>
public static class Rules
{
    private static readonly Regex EmailRx = new(@"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PhoneSep = new(@"[\s().\-]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NicaPhone = new(@"^[2578][0-9]{7}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NicaIntl = new(@"^\+505[2578][0-9]{7}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex OtherIntl = new(@"^\+[0-9]{8,15}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StudentIdRx = new(@"^[A-Za-z0-9-]{6,15}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RucRx = new(@"^[A-Za-z0-9]{14}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CodeRx = new(@"^UTD-[0-9]{3,8}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsEmail(string v) => v.Length <= 254 && EmailRx.IsMatch(v);

    /// <summary>Nicaragua: 8 dígitos que empiezan en 2, 5, 7 u 8 (con o sin +505). También acepta números internacionales con "+".</summary>
    public static bool IsPhone(string v)
    {
        var s = PhoneSep.Replace(v, "");
        if (s.Length == 0) return false;
        if (s[0] == '+') return s.StartsWith("+505", StringComparison.Ordinal) ? NicaIntl.IsMatch(s) : OtherIntl.IsMatch(s);
        return NicaPhone.IsMatch(s);
    }

    /// <summary>La columna Telefono admite 20 caracteres: si lo escrito no cabe, se guarda sin separadores.</summary>
    public static string StoredPhone(string v)
    {
        var t = v.Trim();
        return t.Length <= 20 ? t : PhoneSep.Replace(t, "");
    }

    public static bool IsStudentId(string v) => StudentIdRx.IsMatch(v);
    public static bool IsRuc(string v) => RucRx.IsMatch(v);
    public static bool IsTicketCode(string v) => CodeRx.IsMatch(v);

    /// <summary>http(s) con servidor. La BD además exige que empiece por http:// o https://.</summary>
    public static bool IsWebUrl(string v)
    {
        if (!(v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return false;
        return Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host);
    }

    public static bool TryParseDate(string? v, out DateOnly date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(v) && v.Length == 10
            && DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>
    /// Limpia texto escrito por personas: recorta, quita caracteres de control y los de dirección de texto
    /// (U+202A–202E, U+2066–2069, U+200E/F) que sirven para disfrazar nombres o enlaces. Los saltos de línea solo se
    /// conservan si allowNewlines (descripción); en el resto se cambian por un espacio.
    /// </summary>
    public static string Clean(string? value, bool allowNewlines = false)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in NormalizeSafely(value))
        {
            if (ch is '\r') continue;
            if (ch is '\n') { sb.Append(allowNewlines ? '\n' : ' '); continue; }
            if (ch is '\t') { sb.Append(' '); continue; }
            if (char.IsControl(ch)) continue;
            var cp = (int)ch;
            if (cp is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069 or 0x200E or 0x200F or 0xFEFF or 0x2028 or 0x2029) continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Normaliza a FormC. Un texto con un "sustituto" UTF-16 suelto (por ejemplo un JSON con "\ud800") hace que string.Normalize lance
    /// ArgumentException; en vez de dejar que eso termine en un error 500, se descartan esos caracteres inválidos.
    /// </summary>
    private static string NormalizeSafely(string value)
    {
        try { return value.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException)
        {
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { sb.Append(c).Append(value[++i]); }
                else if (!char.IsSurrogate(c)) sb.Append(c);
            }
            var cleaned = sb.ToString();
            try { return cleaned.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return cleaned; }
        }
    }

    /// <summary>Normaliza un código de ticket escrito por la persona ("utd-1001 " → "UTD-1001").</summary>
    public static string NormalizeCode(string? code) => (code ?? "").Trim().ToUpperInvariant();
}

/// <summary>Acumula errores por campo (el primero de cada campo gana). Las claves son las que usa el front.</summary>
public sealed class FieldErrors
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    public void Add(string key, string message) => _errors.TryAdd(key, message);
    public void Need(string key, bool condition, string message) { if (!condition) Add(key, message); }
    public bool Any => _errors.Count > 0;
    public IReadOnlyDictionary<string, string> All => _errors;

    public void ThrowIfAny(string message)
    {
        if (_errors.Count > 0) throw AppException.Unprocessable(message, new Dictionary<string, string>(_errors));
    }
}
