using System.Data.Common;
using Microsoft.Extensions.Logging;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Domain;
using UniTechDesk.Data.Sql;

namespace UniTechDesk.Data;

/// <summary>
/// Cuentas del personal. El rol de BD de la API solo puede LEER dbo.Personal y actualizar IntentosFallidos, BloqueadoHasta y
/// UltimoAcceso (GRANT por columna en 05_seguridad.sql): las cuentas y contraseñas las administra el DBA.
/// </summary>
public sealed class SqlStaffRepository : IStaffRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ISqlErrorClassifier _classifier;
    private readonly ILogger<SqlStaffRepository> _log;

    public SqlStaffRepository(IDbConnectionFactory db, ISqlErrorClassifier classifier, ILogger<SqlStaffRepository> log)
    {
        _db = db; _classifier = classifier; _log = log;
    }

    private const string AccountColumns = "Id, NombreUsuario, NombreCompleto, Correo, HashContrasena, RolCodigo, Activo, BloqueadoHasta";

    private static StaffAccount Read(Row r) => new()
    {
        Id = r.Int("Id"), Username = r.Str("NombreUsuario"), FullName = r.Str("NombreCompleto"), Email = r.StrN("Correo"),
        PasswordHash = r.Str("HashContrasena"), Role = r.Str("RolCodigo"), Active = r.Bool("Activo"), LockedUntil = r.UtcN("BloqueadoHasta")
    };

    public Task<StaffAccount?> FindByUsernameAsync(string username, CancellationToken ct) =>
        QueryOneAsync("SELECT " + AccountColumns + " FROM dbo.Personal WHERE NombreUsuario = @u;", c => c.NVarchar("@u", username, 50), ct);

    public Task<StaffAccount?> FindByIdAsync(int id, CancellationToken ct) =>
        QueryOneAsync("SELECT " + AccountColumns + " FROM dbo.Personal WHERE Id = @id;", c => c.Int("@id", id), ct);

    private async Task<StaffAccount?> QueryOneAsync(string sql, Func<DbCommand, DbCommand> bind, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = bind(conn.Command(sql));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? Read(new Row(reader)) : null;
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    public async Task<IReadOnlyList<StaffRef>> ListActiveAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command("SELECT Id, NombreCompleto FROM dbo.Personal WHERE Activo = 1 ORDER BY NombreCompleto, Id;");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<StaffRef>();
            while (await reader.ReadAsync(ct)) { var r = new Row(reader); list.Add(new StaffRef { Id = r.Int("Id"), Name = r.Str("NombreCompleto") }); }
            return list;
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    /// <summary>
    /// Suma un intento fallido en una sola sentencia (atómica). Si el bloqueo anterior ya venció, la cuenta vuelve a contar desde 1.
    /// </summary>
    public async Task RegisterFailedLoginAsync(int id, DateTime now, int maxAttempts, DateTime lockUntil, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command(@"
                UPDATE dbo.Personal
                SET IntentosFallidos = CASE WHEN BloqueadoHasta IS NOT NULL AND BloqueadoHasta <= @now THEN 1
                                            WHEN IntentosFallidos >= 250 THEN 250
                                            ELSE IntentosFallidos + 1 END,
                    BloqueadoHasta   = CASE WHEN (CASE WHEN BloqueadoHasta IS NOT NULL AND BloqueadoHasta <= @now THEN 1
                                                       WHEN IntentosFallidos >= 250 THEN 250
                                                       ELSE IntentosFallidos + 1 END) >= @max THEN @lock
                                            WHEN BloqueadoHasta IS NOT NULL AND BloqueadoHasta <= @now THEN NULL
                                            ELSE BloqueadoHasta END
                WHERE Id = @id;")
                .Utc("@now", now).Int("@max", maxAttempts).Utc("@lock", lockUntil).Int("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    public async Task RegisterSuccessfulLoginAsync(int id, DateTime now, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command("UPDATE dbo.Personal SET IntentosFallidos = 0, BloqueadoHasta = NULL, UltimoAcceso = @now WHERE Id = @id;")
                .Utc("@now", now).Int("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }
}
