using System.Data.Common;
using Microsoft.Extensions.Logging;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Data.Sql;

namespace UniTechDesk.Data;

public sealed class SqlCatalogRepository : ICatalogRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ISqlErrorClassifier _classifier;
    private readonly ILogger<SqlCatalogRepository> _log;

    public SqlCatalogRepository(IDbConnectionFactory db, ISqlErrorClassifier classifier, ILogger<SqlCatalogRepository> log)
    {
        _db = db; _classifier = classifier; _log = log;
    }

    public async Task<Catalogs> GetCatalogsAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command(@"
                SELECT Id, Nombre FROM dbo.Carrera WHERE Activo = 1 ORDER BY Id;
                SELECT Id, Nombre FROM dbo.Recinto WHERE Activo = 1 ORDER BY Id;");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var majors = new List<CatalogItem>();
            while (await reader.ReadAsync(ct)) { var r = new Row(reader); majors.Add(new CatalogItem(r.Int("Id"), r.Str("Nombre"))); }
            await reader.NextResultAsync(ct);
            var campuses = new List<CatalogItem>();
            while (await reader.ReadAsync(ct)) { var r = new Row(reader); campuses.Add(new CatalogItem(r.Int("Id"), r.Str("Nombre"))); }
            return new Catalogs(majors, campuses);
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }
}
