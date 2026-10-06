namespace UniTechDesk.Core;

/// <summary>Error de negocio con código HTTP y mensaje en español que se muestra tal cual al usuario.</summary>
public class AppException : Exception
{
    public int StatusCode { get; }
    public IReadOnlyDictionary<string, string>? FieldErrors { get; }

    public AppException(int statusCode, string message, IReadOnlyDictionary<string, string>? fieldErrors = null)
        : base(message)
    {
        StatusCode = statusCode;
        FieldErrors = fieldErrors;
    }

    public static AppException BadRequest(string message) => new(400, message);
    public static AppException Unauthorized(string message) => new(401, message);
    public static AppException Forbidden(string message) => new(403, message);
    public static AppException NotFound(string message) => new(404, message);
    public static AppException Conflict(string message) => new(409, message);
    public static AppException TooLarge(string message) => new(413, message);
    public static AppException Unprocessable(string message, IReadOnlyDictionary<string, string>? fields = null) => new(422, message, fields);
    public static AppException TooManyRequests(string message) => new(429, message);
}

/// <summary>La fila de Ticket cambió desde que se leyó (otro usuario la modificó): hay que releer y reintentar.</summary>
public sealed class ConcurrencyException : Exception
{
    public ConcurrencyException() : base("El ticket cambió mientras se procesaba la operación.") { }
}

/// <summary>Fallo transitorio de la base de datos (interbloqueo, tiempo de espera): se puede reintentar.</summary>
public sealed class TransientStoreException : Exception
{
    public TransientStoreException(string message, Exception? inner = null) : base(message, inner) { }
}
