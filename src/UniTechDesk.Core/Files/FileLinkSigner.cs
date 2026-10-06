using System.Security.Cryptography;
using System.Text;
using UniTechDesk.Core.Abstractions;

namespace UniTechDesk.Core.Files;

/// <summary>
/// URL del tipo /api/files/{id}?e={expira}&amp;s={firma}. La firma es HMAC-SHA256 con una subclave derivada de la clave
/// JWT (otra "etiqueta", así un token de sesión nunca sirve como firma de archivo ni al revés). Dura pocos minutos.
/// </summary>
public sealed class FileLinkSigner : IFileLinkSigner
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;

    public FileLinkSigner(string masterKey, TimeSpan lifetime)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(masterKey));
        _key = h.ComputeHash(Encoding.UTF8.GetBytes("unitech-desk/file-link/v1"));
        _lifetime = lifetime;
    }

    public string CreateUrl(int attachmentId, DateTime nowUtc)
    {
        var exp = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).Add(_lifetime).ToUnixTimeSeconds();
        return $"/api/files/{attachmentId}?e={exp}&s={Sign(attachmentId, exp)}";
    }

    public bool IsValid(int attachmentId, long expiresUnix, string signature, DateTime nowUtc)
    {
        if (expiresUnix < new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(attachmentId, expiresUnix));
        var given = Encoding.ASCII.GetBytes(signature ?? "");
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    private string Sign(int id, long exp)
    {
        using var h = new HMACSHA256(_key);
        var mac = h.ComputeHash(Encoding.ASCII.GetBytes($"{id}|{exp}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
