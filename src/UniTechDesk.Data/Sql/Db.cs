using System.Data;
using System.Data.Common;

namespace UniTechDesk.Data.Sql;

/// <summary>Abre conexiones. La implementación concreta (Microsoft.Data.SqlClient) vive en UniTechDesk.Api.</summary>
public interface IDbConnectionFactory
{
    /// <summary>Devuelve una conexión ya abierta; quien la pide la cierra (using).</summary>
    Task<DbConnection> OpenAsync(CancellationToken ct);
}

/// <summary>Traduce una excepción del proveedor a sus números de error de SQL Server (la lista completa del lote).</summary>
public interface ISqlErrorClassifier
{
    IReadOnlyList<int> ErrorNumbers(DbException ex);
}

/// <summary>Ayudas para escribir SQL parametrizado sobre System.Data.Common. Todo valor viaja como parámetro, nunca concatenado.</summary>
internal static class Db
{
    public static DbCommand Command(this DbConnection conn, string sql, DbTransaction? tx = null, int timeoutSeconds = 30)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.CommandTimeout = timeoutSeconds;
        return cmd;
    }

    private static DbParameter Add(this DbCommand cmd, string name, DbType type, object? value, int size = 0)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        if (size > 0) p.Size = size;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    public static DbCommand Int(this DbCommand c, string name, int? v) { c.Add(name, DbType.Int32, v); return c; }
    public static DbCommand LongParam(this DbCommand c, string name, long v) { c.Add(name, DbType.Int64, v); return c; }
    public static DbCommand Bit(this DbCommand c, string name, bool v) { c.Add(name, DbType.Boolean, v); return c; }
    public static DbCommand Varchar(this DbCommand c, string name, string? v, int size) { c.Add(name, DbType.AnsiString, v, size); return c; }
    public static DbCommand NVarchar(this DbCommand c, string name, string? v, int size) { c.Add(name, DbType.String, v, size); return c; }
    public static DbCommand Utc(this DbCommand c, string name, DateTime? v) { c.Add(name, DbType.DateTime2, v); return c; }
    public static DbCommand DateParam(this DbCommand c, string name, DateOnly? v) { c.Add(name, DbType.Date, v?.ToDateTime(TimeOnly.MinValue)); return c; }
    public static DbCommand Binary(this DbCommand c, string name, byte[]? v, int size) { c.Add(name, DbType.Binary, v, size); return c; }

    public static DbCommand Money(this DbCommand c, string name, decimal v)
    {
        var p = c.Add(name, DbType.Decimal, v);
        p.Precision = 11;
        p.Scale = 2;
        return c;
    }

    /// <summary>Convierte "" en NULL (las columnas opcionales guardan NULL, no cadena vacía).</summary>
    public static string? OrNull(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>Lectura por nombre de columna con manejo de NULL.</summary>
internal readonly struct Row
{
    private readonly DbDataReader _r;
    public Row(DbDataReader r) => _r = r;

    private int O(string c) => _r.GetOrdinal(c);
    public bool IsNull(string c) => _r.IsDBNull(O(c));
    public string Str(string c) { var o = O(c); return _r.IsDBNull(o) ? "" : _r.GetString(o); }
    public string? StrN(string c) { var o = O(c); return _r.IsDBNull(o) ? null : _r.GetString(o); }
    public int Int(string c) => _r.GetInt32(O(c));
    public int? IntN(string c) { var o = O(c); return _r.IsDBNull(o) ? null : _r.GetInt32(o); }
    public long Long(string c) => _r.GetInt64(O(c));
    public bool Bool(string c) => _r.GetBoolean(O(c));
    public decimal Dec(string c) => _r.GetDecimal(O(c));
    public byte Byte(string c) => _r.GetByte(O(c));
    public DateTime Utc(string c) => DateTime.SpecifyKind(_r.GetDateTime(O(c)), DateTimeKind.Utc);
    public DateTime? UtcN(string c) { var o = O(c); return _r.IsDBNull(o) ? null : DateTime.SpecifyKind(_r.GetDateTime(o), DateTimeKind.Utc); }
    public DateOnly? DateN(string c) { var o = O(c); return _r.IsDBNull(o) ? null : DateOnly.FromDateTime(_r.GetDateTime(o)); }
    public byte[] Bytes(string c) => (byte[])_r.GetValue(O(c));
    public byte[]? BytesN(string c) { var o = O(c); return _r.IsDBNull(o) ? null : (byte[])_r.GetValue(o); }
}
