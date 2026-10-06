using System.Data.Common;
using Microsoft.Extensions.Logging;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Domain;
using UniTechDesk.Data.Sql;

namespace UniTechDesk.Data;

/// <summary>Bandeja de salida de correos (dbo.NotificacionCorreo).</summary>
public sealed class SqlOutboxRepository : IOutboxRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ISqlErrorClassifier _classifier;
    private readonly ILogger<SqlOutboxRepository> _log;

    public SqlOutboxRepository(IDbConnectionFactory db, ISqlErrorClassifier classifier, ILogger<SqlOutboxRepository> log)
    {
        _db = db; _classifier = classifier; _log = log;
    }

    /// <summary>
    /// Reserva los pendientes vencidos en una sola sentencia: UPDLOCK + READPAST evitan que dos procesos tomen el mismo correo, y
    /// ProximoIntentoEn se adelanta como "concesión" (si el proceso muere, el correo vuelve a estar disponible al vencer).
    /// </summary>
    public async Task<IReadOnlyList<OutboxItem>> ClaimDueAsync(DateTime now, int batchSize, TimeSpan lease, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command(@"
                WITH due AS (
                    SELECT TOP (@n) Id, TicketId, Plantilla, Destinatario, Intentos, ProximoIntentoEn
                    FROM dbo.NotificacionCorreo WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE Estado = 'PENDING' AND ProximoIntentoEn <= @now
                    ORDER BY ProximoIntentoEn, Id
                )
                UPDATE due
                SET Intentos = CASE WHEN Intentos >= 250 THEN 250 ELSE Intentos + 1 END,
                    ProximoIntentoEn = @lease
                OUTPUT INSERTED.Id, INSERTED.TicketId, INSERTED.Plantilla, INSERTED.Destinatario, INSERTED.Intentos;")
                .Int("@n", batchSize).Utc("@now", now).Utc("@lease", now + lease);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<OutboxItem>();
            while (await reader.ReadAsync(ct))
            {
                var r = new Row(reader);
                list.Add(new OutboxItem { Id = r.Long("Id"), TicketId = r.Int("TicketId"), Template = r.Str("Plantilla"), Recipient = r.Str("Destinatario"), Attempts = r.Byte("Intentos") });
            }
            return list;
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    public async Task MarkSentAsync(long id, DateTime now, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command("UPDATE dbo.NotificacionCorreo SET Estado = 'SENT', FechaEnvio = @now, UltimoError = NULL WHERE Id = @id AND Estado = 'PENDING';")
                .Utc("@now", now).LongParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    public async Task MarkFailedAsync(long id, string error, DateTime nextAttempt, bool giveUp, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command(@"
                UPDATE dbo.NotificacionCorreo
                SET Estado = CASE WHEN @giveUp = 1 THEN 'FAILED' ELSE 'PENDING' END, UltimoError = @err, ProximoIntentoEn = @next
                WHERE Id = @id AND Estado = 'PENDING';")
                .Bit("@giveUp", giveUp).NVarchar("@err", error, 500).Utc("@next", nextAttempt).LongParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }
}
