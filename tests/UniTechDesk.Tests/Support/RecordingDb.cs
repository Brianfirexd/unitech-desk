using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using UniTechDesk.Data.Sql;

namespace UniTechDesk.Tests.Support;

/// <summary>
/// Conexión ADO.NET de mentira que NO guarda datos: solo registra cada sentencia SQL que ejecuta el código real de UniTechDesk.Data
/// (texto, parámetros con tipo, tamaño y valor, si iba dentro de una transacción y qué columnas leyó el código de cada resultado).
/// Sirve para comprobar sin SQL Server que el SQL y los parámetros son coherentes entre sí y con los scripts de la base.
/// </summary>
public sealed class RecordingDb : IDbConnectionFactory
{
    public sealed record Param(string Name, DbType Type, int Size, byte Precision, byte Scale, object? Value)
    {
        public bool IsNull => Value is null or DBNull;
    }

    public sealed class Statement
    {
        public string Label { get; init; } = "";
        public string Sql { get; init; } = "";
        public IReadOnlyList<Param> Params { get; init; } = Array.Empty<Param>();
        public bool InTransaction { get; init; }
        public string Kind { get; init; } = "";
        /// <summary>Nombres de columna que el código pidió a cada resultado (índice = resultado dentro del lote).</summary>
        public List<List<string>> ColumnsRead { get; } = new();
    }

    public List<Statement> Statements { get; } = new();
    public int Commits { get; private set; }
    public int Rollbacks { get; private set; }
    public int Opened { get; private set; }

    /// <summary>Texto que se anota en cada sentencia (para saber qué escenario la generó).</summary>
    public string Label { get; set; } = "";

    /// <summary>Si no es null, se invoca antes de ejecutar cada sentencia; puede lanzar una excepción o devolver las filas afectadas.</summary>
    public Func<Statement, int?>? OnExecute { get; set; }

    /// <summary>Filas afectadas que devuelve ExecuteNonQuery (1 por defecto: "el UPDATE encontró su fila").</summary>
    public int NonQueryResult { get; set; } = 1;

    /// <summary>Columnas que el lector informa como NULL (para ejercitar los caminos de "sin valor").</summary>
    public HashSet<string> NullColumns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Si es true, Rollback() falla como cuando un trigger ya canceló la transacción.</summary>
    public bool RollbackThrows { get; set; }

    public Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        Opened++;
        return Task.FromResult<DbConnection>(new FakeConnection(this));
    }

    // ------------------------------------------------------------------ Implementación ADO.NET ------------------------------------------------------------------

    private sealed class FakeConnection : DbConnection
    {
        private readonly RecordingDb _db;
        private ConnectionState _state = ConnectionState.Open;
        public FakeConnection(RecordingDb db) => _db = db;

        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "UniTechDesk";
        public override string DataSource => "fake";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTransaction(_db, this);
        protected override DbCommand CreateDbCommand() => new FakeCommand(_db) { Connection = this };
        protected override void Dispose(bool disposing) { _state = ConnectionState.Closed; base.Dispose(disposing); }
    }

    private sealed class FakeTransaction : DbTransaction
    {
        private readonly RecordingDb _db;
        private readonly DbConnection _conn;
        private bool _done;
        public FakeTransaction(RecordingDb db, DbConnection conn) { _db = db; _conn = conn; }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => _conn;
        public override void Commit() { _done = true; _db.Commits++; }
        public override void Rollback()
        {
            if (_db.RollbackThrows || _done) throw new InvalidOperationException("Esta transacción ya terminó; ya no se puede usar.");
            _done = true; _db.Rollbacks++;
        }
    }

    private sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull] public override string ParameterName { get; set; } = "";
        [AllowNull] public override string SourceColumn { get; set; } = "";
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override byte Precision { get; set; }
        public override byte Scale { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeParameters : DbParameterCollection
    {
        private readonly List<DbParameter> _items = new();
        public override int Count => _items.Count;
        public override object SyncRoot => ((ICollection)_items).SyncRoot;
        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) Add(v); }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
        public IReadOnlyList<DbParameter> Items => _items;
    }

    private sealed class FakeCommand : DbCommand
    {
        private readonly RecordingDb _db;
        private readonly FakeParameters _params = new();
        public FakeCommand(RecordingDb db) => _db = db;

        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeParameter();

        private Statement Record(string kind)
        {
            var st = new Statement
            {
                Label = _db.Label, Sql = CommandText, Kind = kind, InTransaction = DbTransaction is not null,
                Params = _params.Items.Select(p => new Param(p.ParameterName, p.DbType, p.Size, p.Precision, p.Scale, p.Value)).ToList()
            };
            _db.Statements.Add(st);
            return st;
        }

        public override int ExecuteNonQuery()
        {
            var st = Record("NonQuery");
            return _db.OnExecute?.Invoke(st) ?? _db.NonQueryResult;
        }

        public override object? ExecuteScalar()
        {
            var st = Record("Scalar");
            _db.OnExecute?.Invoke(st);
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var st = Record("Reader");
            _db.OnExecute?.Invoke(st);
            return new FakeReader(_db, st);
        }
    }

    /// <summary>
    /// Lector que inventa un valor para cualquier columna que el código le pida y anota el nombre. Cada resultado tiene exactamente una fila.
    /// Si el código pide una columna que el SELECT no devuelve, el verificador de SQL lo detecta al cruzar los nombres.
    /// </summary>
    private sealed class FakeReader : DbDataReader
    {
        private readonly RecordingDb _db;
        private readonly Statement _st;
        private int _set;
        private int _rowsInSet;
        private bool _closed;

        public FakeReader(RecordingDb db, Statement st) { _db = db; _st = st; EnsureSet(); }

        private void EnsureSet() { while (_st.ColumnsRead.Count <= _set) _st.ColumnsRead.Add(new List<string>()); }
        private List<string> Cols => _st.ColumnsRead[_set];

        public override int GetOrdinal(string name)
        {
            var i = Cols.IndexOf(name);
            if (i < 0) { Cols.Add(name); i = Cols.Count - 1; }
            return i;
        }

        public override bool Read() => ++_rowsInSet == 1;
        public override bool NextResult() { _set++; _rowsInSet = 0; EnsureSet(); return true; }

        public override bool IsDBNull(int ordinal) => _db.NullColumns.Contains(Cols[ordinal]);
        public override string GetString(int ordinal) => "x";
        public override int GetInt32(int ordinal) => 1;
        public override long GetInt64(int ordinal) => 1;
        public override byte GetByte(int ordinal) => 1;
        public override bool GetBoolean(int ordinal) => false;
        public override decimal GetDecimal(int ordinal) => 1m;
        public override DateTime GetDateTime(int ordinal) => new(2026, 10, 6, 15, 0, 0, DateTimeKind.Unspecified);
        public override object GetValue(int ordinal) => new byte[8];

        public override bool HasRows => true;
        public override int Depth => 0;
        public override int FieldCount => Cols.Count;
        public override bool IsClosed => _closed;
        public override int RecordsAffected => -1;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override void Close() => _closed = true;

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override Type GetFieldType(int ordinal) => throw new NotSupportedException();
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        public override string GetName(int ordinal) => Cols[ordinal];
        public override int GetValues(object[] values) => throw new NotSupportedException();
    }
}

/// <summary>Excepción de base de datos de mentira con los números de error de SQL Server (para probar la traducción de errores).</summary>
public sealed class FakeDbException : DbException
{
    public IReadOnlyList<int> Numbers { get; }
    public FakeDbException(params int[] numbers) : base("error de base de datos simulado") => Numbers = numbers;
}

public sealed class FakeSqlErrorClassifier : ISqlErrorClassifier
{
    public IReadOnlyList<int> ErrorNumbers(DbException ex) => ex is FakeDbException f ? f.Numbers : Array.Empty<int>();
}
