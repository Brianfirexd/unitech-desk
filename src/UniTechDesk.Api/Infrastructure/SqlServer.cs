using System.Data.Common;
using Microsoft.Data.SqlClient;
using UniTechDesk.Data.Sql;

namespace UniTechDesk.Api.Infrastructure;

/// <summary>Abre conexiones con Microsoft.Data.SqlClient. Aquí (y solo aquí) se usa el proveedor concreto.</summary>
public sealed class SqlServerConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    public SqlServerConnectionFactory(string connectionString) => _connectionString = connectionString;

    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

public sealed class SqlServerErrorClassifier : ISqlErrorClassifier
{
    public IReadOnlyList<int> ErrorNumbers(DbException ex)
    {
        if (ex is not SqlException sql) return Array.Empty<int>();
        var numbers = new List<int>(sql.Errors.Count);
        foreach (SqlError e in sql.Errors) numbers.Add(e.Number);
        if (numbers.Count == 0) numbers.Add(sql.Number);
        return numbers;
    }
}
