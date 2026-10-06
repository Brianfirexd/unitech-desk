using System.Data.Common;
using Microsoft.Extensions.Logging;
using UniTechDesk.Core;

namespace UniTechDesk.Data.Sql;

/// <summary>Convierte errores de SQL Server en excepciones del dominio con mensajes claros.</summary>
internal static class SqlFailures
{
    public static Exception Translate(DbException ex, ISqlErrorClassifier classifier, ILogger log)
    {
        var numbers = classifier.ErrorNumbers(ex);

        if (numbers.Contains(51001)) return AppException.Conflict("El trabajo solo puede iniciar cuando el cliente aprueba la cotización.");
        if (numbers.Contains(51002)) return AppException.Conflict("No se puede entregar: la cotización aprobada tiene el pago pendiente.");
        if (numbers.Contains(1205) || numbers.Contains(-2) || numbers.Contains(1222)) return new TransientStoreException("Interbloqueo o tiempo de espera en la base de datos.", ex);
        if (numbers.Contains(2627) || numbers.Contains(2601))
        {
            // No se registra el mensaje: en estos errores SQL Server incluye el valor duplicado (podría ser un correo o un usuario).
            log.LogWarning("Violación de unicidad en la base de datos (error {Number}).", numbers.FirstOrDefault(n => n is 2627 or 2601));
            return AppException.Conflict("Otra persona acaba de hacer un cambio equivalente. Recarga la página e inténtalo de nuevo.");
        }
        if (numbers.Contains(547))
        {
            log.LogError(ex, "Restricción de la base de datos rechazó el cambio (error 547).");
            return AppException.Conflict("No se pudo guardar: los datos no cumplen una regla de la base de datos. Revisa el valor e inténtalo de nuevo.");
        }
        log.LogError(ex, "Error de base de datos ({Numbers}).", string.Join(",", numbers));
        return ex;
    }
}
