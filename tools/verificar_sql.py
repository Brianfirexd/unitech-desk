#!/usr/bin/env python3
"""
Verificador ESTÁTICO del SQL que ejecuta el backend contra los scripts de la base de datos.

No se conecta a ningún SQL Server. Lee:
  1) los scripts 01 a 05 de la base (tablas, restricciones, vistas, permisos), y
  2) el volcado que genera la prueba SqlShapeTests (todas las sentencias que UniTechDesk.Data ejecuta, con sus parámetros),
y comprueba, para cada sentencia:
  - que parsea como T-SQL y que no usa construcciones que el parser no entiende;
  - que cada tabla, vista y columna existe (también las que se asignan en UPDATE/INSERT y las que lee el código por nombre);
  - que cada parámetro tiene el tipo y el tamaño que corresponde a la columna con la que se compara o en la que se guarda;
  - que ninguna columna NOT NULL recibe NULL y que todos los CHECK de la tabla se cumplen con los valores reales enviados;
  - que el rol de la API tiene el permiso (GRANT) para cada lectura y escritura, y que no hay UPDATE sin WHERE;
  - que los códigos que usa el backend (estados, pagos, eventos…) coinciden con los catálogos y los CHECK de la base.

Uso (necesita:  pip install sqlglot):
  UTD_SQL_DUMP=/tmp/sql.json dotnet test --filter Volcado_de_sentencias
  python3 tools/verificar_sql.py --db <carpeta con 01_tablas.sql...> --dump /tmp/sql.json --src src/UniTechDesk.Core/Domain

Termina con código 1 si encuentra algún ERROR. Los AVISOS son cosas a revisar que no impiden funcionar.
"""
import argparse
import json
import re
import sys
from collections import defaultdict
from decimal import Decimal

try:
    import sqlglot
    from sqlglot import exp
    from sqlglot.optimizer.qualify import qualify
except ImportError:
    sys.exit("Falta sqlglot: pip install sqlglot")

SEEDS: dict[str, list[tuple]] = {}   # tabla -> filas sembradas por 03_catalogos.sql
ERRORS: list[str] = []
WARNINGS: list[str] = []
CHECKS = 0


def error(msg):
    ERRORS.append(msg)


def warn(msg):
    WARNINGS.append(msg)


def ok():
    global CHECKS
    CHECKS += 1


# ======================================================================================================================
# 1. Esquema a partir de los scripts SQL
# ======================================================================================================================

def read_sql(path):
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    text = re.sub(r"--[^\n]*", " ", text)
    return text


def balanced(text, start):
    """Texto entre el paréntesis que abre en text[start] y su pareja."""
    assert text[start] == "("
    depth, i, quoted = 0, start, False
    while i < len(text):
        ch = text[i]
        if ch == "'":
            quoted = not quoted
        elif not quoted:
            if ch == "(":
                depth += 1
            elif ch == ")":
                depth -= 1
                if depth == 0:
                    return text[start + 1:i], i
        i += 1
    raise ValueError("paréntesis sin cerrar")


def split_top(s, sep=","):
    parts, depth, cur, quoted = [], 0, [], False
    for ch in s:
        if ch == "'":
            quoted = not quoted
        if not quoted:
            if ch == "(":
                depth += 1
            elif ch == ")":
                depth -= 1
            elif ch == sep and depth == 0:
                parts.append("".join(cur))
                cur = []
                continue
        cur.append(ch)
    parts.append("".join(cur))
    return [p.strip() for p in parts if p.strip()]


class Column:
    def __init__(self, name, type_, length, nullable, identity, computed, has_default, default):
        self.name, self.type, self.length = name, type_, length
        self.nullable, self.identity, self.computed = nullable, identity, computed
        self.has_default, self.default = has_default, default


class Table:
    def __init__(self, name):
        self.name = name
        self.columns: dict[str, Column] = {}
        self.checks: list[tuple[str, str]] = []
        self.pk: list[str] = []
        self.fks: list[tuple[str, list[str], str, list[str]]] = []

    def col(self, name):
        for k, v in self.columns.items():
            if k.lower() == name.lower():
                return v
        return None


def parse_tables(sql):
    tables: dict[str, Table] = {}
    for m in re.finditer(r"CREATE\s+TABLE\s+dbo\.(\w+)\s*\(", sql, re.I):
        body, _ = balanced(sql, m.end() - 1)
        t = Table(m.group(1))
        for item in split_top(body):
            if re.match(r"CONSTRAINT\s+\w+\s+", item, re.I):
                name = re.match(r"CONSTRAINT\s+(\w+)", item, re.I).group(1)
                rest = item[re.match(r"CONSTRAINT\s+\w+\s+", item, re.I).end():]
                if re.match(r"CHECK\s*\(", rest, re.I):
                    inner, _ = balanced(rest, rest.index("("))
                    t.checks.append((name, inner))
                elif re.match(r"PRIMARY\s+KEY", rest, re.I):
                    t.pk = [c.strip() for c in re.search(r"\(([^)]*)\)", rest).group(1).split(",")]
                elif re.match(r"FOREIGN\s+KEY", rest, re.I):
                    mm = re.match(r"FOREIGN\s+KEY\s*\(([^)]*)\)\s*REFERENCES\s+dbo\.(\w+)\s*\(([^)]*)\)", rest, re.I | re.S)
                    t.fks.append((name, [c.strip() for c in mm.group(1).split(",")], mm.group(2), [c.strip() for c in mm.group(3).split(",")]))
                continue
            mm = re.match(r"(\w+)\s+(.*)$", item, re.S)
            name, rest = mm.group(1), mm.group(2)
            computed = bool(re.match(r"AS\s*\(", rest, re.I))
            tm = re.match(r"(\w+)\s*(?:\(\s*(\w+)(?:\s*,\s*\d+)?\s*\))?", rest)
            type_ = "COMPUTED" if computed else tm.group(1).upper()
            length = None
            if not computed and tm.group(2):
                length = tm.group(2).upper()
                length = None if length == "MAX" else (int(length) if length.isdigit() else None)
            if computed:
                type_, length = "VARCHAR", 14   # Ticket.Codigo: VARCHAR(14)
            not_null = bool(re.search(r"\bNOT\s+NULL\b", rest, re.I))
            identity = bool(re.search(r"\bIDENTITY\b", rest, re.I))
            dm = re.search(r"\bDEFAULT\s*\((.*)\)\s*$", rest, re.I | re.S)
            has_default = dm is not None
            default = dm.group(1).strip() if dm else None
            t.columns[name] = Column(name, type_, length, not (not_null or identity or type_ == "ROWVERSION"), identity, computed, has_default, default)
        tables[t.name] = t
    return tables


def parse_views(sql):
    views = {}
    for m in re.finditer(r"CREATE\s+(?:OR\s+ALTER\s+)?VIEW\s+dbo\.(\w+)\s+AS\s+(.*?)(?:\nGO|\Z)", sql, re.I | re.S):
        tree = sqlglot.parse_one(m.group(2).strip().rstrip(";"), dialect="tsql")
        views[m.group(1)] = tree.named_selects
    return views


def parse_grants(sql):
    grants = defaultdict(lambda: defaultdict(set))   # tabla -> permiso -> {None | columnas}
    denies = defaultdict(set)
    for m in re.finditer(r"\b(GRANT|DENY)\s+([A-Z, ]+?)(?:\(([^)]*)\))?\s+ON\s+dbo\.(\w+)\s+TO\s+rol_unitech_api", sql, re.I):
        kind, perms, cols, table = m.group(1).upper(), m.group(2), m.group(3), m.group(4)
        for p in [x.strip().upper() for x in perms.split(",") if x.strip()]:
            if kind == "GRANT":
                grants[table][p].add(tuple(c.strip() for c in cols.split(",")) if cols else None)
            else:
                denies[table].add(p)
    return grants, denies


# ======================================================================================================================
# 2. Evaluación de CHECK con los valores reales (lógica de tres valores como SQL Server)
# ======================================================================================================================

class Unsupported(Exception):
    pass


def like_to_regex(pattern):
    out = []
    for ch in pattern:
        out.append(".*" if ch == "%" else "." if ch == "_" else re.escape(ch))
    return re.compile("^" + "".join(out) + "$", re.I | re.S)


def lit(node):
    if isinstance(node, exp.National):
        return node.this
    if isinstance(node, exp.Literal):
        if node.is_string:
            return node.this
        return Decimal(node.this) if "." in node.this else int(node.this)
    if isinstance(node, exp.Null):
        return None
    raise Unsupported(f"literal {type(node).__name__}")


def ev(node, row):
    """Devuelve True/False/None (desconocido). Los valores sueltos devuelven su valor."""
    if isinstance(node, exp.Paren):
        return ev(node.this, row)
    if isinstance(node, (exp.Literal, exp.National, exp.Null)):
        return lit(node)
    if isinstance(node, exp.Column):
        key = node.name
        for k in row:
            if k.lower() == key.lower():
                return row[k]
        raise Unsupported(f"columna {key} sin valor")
    if isinstance(node, exp.And):
        a, b = ev(node.this, row), ev(node.expression, row)
        if a is False or b is False:
            return False
        return None if a is None or b is None else True
    if isinstance(node, exp.Or):
        a, b = ev(node.this, row), ev(node.expression, row)
        if a is True or b is True:
            return True
        return None if a is None or b is None else False
    if isinstance(node, exp.Not):
        a = ev(node.this, row)
        return None if a is None else (not a)
    if isinstance(node, exp.Is):
        v = ev(node.this, row)
        if not isinstance(node.expression, exp.Null):
            raise Unsupported("IS no nulo")
        return v is None
    cmp = {exp.EQ: lambda a, b: a == b, exp.NEQ: lambda a, b: a != b, exp.GT: lambda a, b: a > b, exp.GTE: lambda a, b: a >= b,
           exp.LT: lambda a, b: a < b, exp.LTE: lambda a, b: a <= b}
    for cls, fn in cmp.items():
        if isinstance(node, cls):
            a, b = ev(node.this, row), ev(node.expression, row)
            if a is None or b is None:
                return None
            if isinstance(a, str) and isinstance(b, str):
                a, b = a.lower(), b.lower()   # colación _CI_
            return fn(a, b)
    if isinstance(node, exp.In):
        v = ev(node.this, row)
        if v is None:
            return None
        vals = [ev(e, row) for e in node.expressions]
        return str(v).lower() in [str(x).lower() for x in vals if x is not None]
    if isinstance(node, exp.Between):
        v, lo, hi = ev(node.this, row), ev(node.args["low"], row), ev(node.args["high"], row)
        return None if v is None else lo <= v <= hi
    if isinstance(node, exp.Like):
        v = ev(node.this, row)
        pat = ev(node.expression, row)
        if v is None or pat is None:
            return None
        res = bool(like_to_regex(pat).match(str(v)))
        return (not res) if node.args.get("negate") else res
    if isinstance(node, exp.Cast):
        return ev(node.this, row)
    if isinstance(node, (exp.Length,)):
        v = ev(node.this, row)
        return None if v is None else len(str(v).rstrip(" "))
    if isinstance(node, exp.Trim):
        v = ev(node.this, row)
        if v is None:
            return None
        pos = (node.args.get("position") or "").upper()
        return str(v).lstrip(" ") if pos == "LEADING" else str(v).rstrip(" ") if pos == "TRAILING" else str(v).strip(" ")
    if isinstance(node, (exp.Coalesce,)):
        for e in [node.this] + list(node.expressions):
            v = ev(e, row)
            if v is not None:
                return v
        return None
    if isinstance(node, exp.Anonymous):
        name = node.name.upper()
        args = [ev(a, row) for a in node.expressions]
        if name == "LTRIM":
            return None if args[0] is None else str(args[0]).lstrip(" ")
        if name == "RTRIM":
            return None if args[0] is None else str(args[0]).rstrip(" ")
        if name == "LEN":
            return None if args[0] is None else len(str(args[0]).rstrip(" "))
        if name == "ISNULL":
            return args[1] if args[0] is None else args[0]
    raise Unsupported(type(node).__name__)


# ======================================================================================================================
# 3. Verificación de las sentencias
# ======================================================================================================================

FAMILY = {
    "VARCHAR": {"AnsiString"}, "NVARCHAR": {"String"}, "CHAR": {"AnsiString"}, "NCHAR": {"String"},
    "INT": {"Int32"}, "BIGINT": {"Int64"}, "SMALLINT": {"Int16", "Int32"}, "TINYINT": {"Byte", "Int32"}, "BIT": {"Boolean"},
    "DATETIME2": {"DateTime2"}, "DATE": {"Date"}, "DECIMAL": {"Decimal"}, "BINARY": {"Binary"}, "ROWVERSION": {"Binary"}, "VARBINARY": {"Binary"},
}


def convert(value, dbtype):
    if value is None:
        return None
    if dbtype in ("Int32", "Int64", "Int16", "Byte"):
        return int(value)
    if dbtype == "Boolean":
        return 1 if value in ("True", True) else 0
    if dbtype == "Decimal":
        return Decimal(value)
    return value


def sql_default_value(col: Column):
    d = (col.default or "").strip()
    while d.startswith("(") and d.endswith(")"):
        d = d[1:-1].strip()
    if re.fullmatch(r"-?\d+", d):
        return int(d)
    m = re.fullmatch(r"'([^']*)'", d)
    if m:
        return m.group(1)
    return "<ahora>"   # SYSUTCDATETIME() u otra función: no nula


def param_name(p):
    """exp.Parameter -> '@nombre' (None si es una variable del sistema como @@ROWCOUNT)."""
    inner = p.this
    if isinstance(inner, exp.Parameter):
        return None
    return "@" + (inner.name if hasattr(inner, "name") else str(inner))


def fk_domain_errors(table: "Table", row: dict, ctx: str, label: str):
    """Los códigos enviados a columnas con llave foránea hacia un catálogo sembrado deben existir en ese catálogo.
    Si la fila solo trae algunas columnas de una llave compuesta (un UPDATE), se valida la proyección sobre esas columnas."""
    for name, cols, ref, _refcols in table.fks:
        if ref not in SEEDS:
            continue
        idx, values = [], []
        for i, c in enumerate(cols):
            k = next((x for x in row if x.lower() == c.lower()), None)
            if k is not None:
                idx.append(i)
                values.append(row[k])
        if not idx or any(v is None for v in values) or any(isinstance(v, str) and v.startswith("<") for v in values):
            continue   # MATCH SIMPLE: con algún NULL no se valida; variables no evaluables
        keys = {tuple(str(r[i]).lower() for i in idx) for r in SEEDS[ref]}
        if tuple(str(v).lower() for v in values) not in keys:
            error(f"{ctx} {table.name}.{'/'.join(cols[i] for i in idx)} = {values} no existe en {ref} ({name}) — {label}")
        else:
            ok()


class Verifier:
    def __init__(self, tables, views, grants, denies):
        self.tables, self.views, self.grants, self.denies = tables, views, grants, denies
        self.schema = {"dbo": {}}
        for t in tables.values():
            self.schema["dbo"][t.name] = {c: "TEXT" for c in t.columns}
        for v, cols in views.items():
            self.schema["dbo"][v] = {c: "TEXT" for c in cols}

    # -- utilidades ----------------------------------------------------------------------------------------------------

    def table_of(self, name):
        for k, v in self.tables.items():
            if k.lower() == name.lower():
                return v
        return None

    def need_grant(self, ctx, table, perm, columns=None):
        g = self.grants.get(table, {})
        if perm in self.denies.get(table, set()):
            error(f"{ctx}: {perm} sobre {table} está denegado al rol de la API.")
            return
        entries = g.get(perm, set())
        if None in entries:
            ok()
            return
        if columns is not None:
            allowed = {c.lower() for e in entries if e for c in e}
            falta = [c for c in columns if c.lower() not in allowed]
            if not falta and entries:
                ok()
                return
            error(f"{ctx}: el rol de la API no tiene {perm} sobre {table}({', '.join(falta or columns)}).")
            return
        error(f"{ctx}: el rol de la API no tiene {perm} sobre {table}.")

    # -- una sentencia ---------------------------------------------------------------------------------------------------

    def verify_batch(self, label, sql, instances):
        ctx = f"[{label}]"
        try:
            trees = [t for t in sqlglot.parse(sql, dialect="tsql") if t is not None]
        except Exception as e:  # noqa: BLE001
            error(f"{ctx} no parsea como T-SQL: {str(e).splitlines()[0]}")
            return
        ok()
        flat = []
        for t in trees:
            if isinstance(t, exp.IfBlock):
                flat.extend(t.args["true"].expressions if isinstance(t.args.get("true"), exp.Block) else [t.args["true"]])
            else:
                flat.append(t)
        for t in flat:
            if isinstance(t, exp.Command):
                error(f"{ctx} el parser no entendió esta sentencia (Command): {t.sql()[:80]}")

        # Resultados que devuelve el lote, en orden (para cruzar con las columnas que lee el código).
        result_sets = []
        for t in flat:
            if isinstance(t, exp.Select):
                result_sets.append(t.named_selects)
            elif isinstance(t, (exp.Insert, exp.Update)) and t.args.get("returning") is not None and not t.args["returning"].args.get("into"):
                result_sets.append([c.name for c in t.args["returning"].expressions])

        for inst in instances:
            reads = inst["columnsRead"]
            reads = [r for r in reads if r] if reads else []
            if len(reads) > len(result_sets):
                error(f"{ctx} el código lee {len(reads)} resultados pero el SQL devuelve {len(result_sets)}.")
                continue
            for i, cols in enumerate(inst["columnsRead"]):
                if not cols:
                    continue
                have = {c.lower() for c in result_sets[i]}
                missing = [c for c in cols if c.lower() not in have]
                if missing:
                    error(f"{ctx} resultado #{i + 1}: el código lee {missing} pero el SELECT solo devuelve {result_sets[i]}.")
                else:
                    ok()
            break   # basta una instancia: el SQL es el mismo

        for t in flat:
            self.verify_statement(ctx, t, instances)

    def verify_statement(self, ctx, t, instances):
        if isinstance(t, (exp.Declare,)):
            return
        if isinstance(t, exp.Select):
            self.verify_select(ctx, t)
        elif isinstance(t, exp.Insert):
            self.verify_insert(ctx, t, instances)
        elif isinstance(t, exp.Update):
            self.verify_update(ctx, t, instances)
        else:
            error(f"{ctx} tipo de sentencia no esperado: {type(t).__name__}")

    # -- SELECT --------------------------------------------------------------------------------------------------------

    def verify_select(self, ctx, t):
        for tbl in t.find_all(exp.Table):
            name = tbl.name
            if tbl.args.get("db") is None:
                continue   # alias de CTE
            if self.table_of(name) is None and name not in self.views:
                error(f"{ctx} la tabla/vista dbo.{name} no existe.")
                continue
            ok()
            if name in self.views:
                self.need_grant(ctx, name, "SELECT")
            else:
                self.need_grant(ctx, name, "SELECT")
        try:
            q = qualify(t.copy(), schema=self.schema, dialect="tsql", validate_qualify_columns=True, infer_schema=False,
                        quote_identifiers=False, identify=False)
            ok()
            _ = q
        except Exception as e:  # noqa: BLE001
            error(f"{ctx} columna desconocida o ambigua: {str(e).splitlines()[0]} en: {' '.join(t.sql(dialect='tsql').split())[:160]}")
            return
        self.check_param_comparisons(ctx, t, self.alias_map(t), None)
        # Los SELECT nunca deberían traer la contraseña salvo el de login.
        for c in t.find_all(exp.Column):
            if c.name.lower() == "hashcontrasena" and "personal" not in {x.name.lower() for x in t.find_all(exp.Table)}:
                error(f"{ctx} lee HashContrasena fuera de dbo.Personal.")

    def alias_map(self, t):
        m = {}
        for tbl in t.find_all(exp.Table):
            if tbl.args.get("db") is not None:
                m[(tbl.alias or tbl.name).lower()] = tbl.name
        return m

    # -- Comparaciones columna ↔ parámetro ------------------------------------------------------------------------------

    def column_info(self, col: exp.Column, aliases, default_table):
        tname = None
        if col.table:
            tname = aliases.get(col.table.lower())
        elif default_table:
            tname = default_table
        elif len(set(aliases.values())) == 1:
            tname = next(iter(aliases.values()))
        if tname is None:
            return None
        t = self.table_of(tname)
        return t.col(col.name) if t else None

    def check_param_pair(self, ctx, col: Column, tname, pname, params, write):
        p = params.get(pname)
        if p is None:
            return
        want = FAMILY.get(col.type)
        if want and p["type"] not in want:
            msg = f"{ctx} {pname} es {p['type']} pero {tname}.{col.name} es {col.type}"
            if write or col.type not in ("VARCHAR", "NVARCHAR"):
                error(msg + ".")
            else:
                warn(msg + " (conversión implícita en la comparación; puede impedir usar el índice).")
        else:
            ok()
        if write and col.length and p["size"] and p["size"] > col.length:
            error(f"{ctx} {pname} admite {p['size']} caracteres pero {tname}.{col.name} solo {col.length}.")
        if write and col.length and p["size"] and p["size"] < col.length:
            warn(f"{ctx} {pname} admite {p['size']} caracteres y {tname}.{col.name} admite {col.length}: un texto más largo se truncaría en silencio.")
        if col.type == "DECIMAL" and write and (p["precision"], p["scale"]) != (11, 2):
            error(f"{ctx} {pname}: precisión/escala ({p['precision']},{p['scale']}) distinta de DECIMAL(11,2).")

    def check_param_comparisons(self, ctx, node, aliases, default_table, params=None, write_cols=None):
        params = params if params is not None else self.current_params
        cmp_types = (exp.EQ, exp.NEQ, exp.GT, exp.GTE, exp.LT, exp.LTE, exp.Like)
        for c in node.find_all(*cmp_types):
            a, b = c.this, c.expression
            if isinstance(a, exp.Parameter):
                a, b = b, a
            if isinstance(a, exp.Column) and isinstance(b, exp.Parameter):
                pname = param_name(b)
                info = self.column_info(a, aliases, default_table)
                if info is not None and pname:
                    self.check_param_pair(ctx, info, self.table_name_for(a, aliases, default_table), pname, params, write=False)

    def table_name_for(self, col, aliases, default_table):
        if col.table:
            return aliases.get(col.table.lower(), "?")
        return default_table or (next(iter(aliases.values())) if aliases else "?")

    # -- INSERT ----------------------------------------------------------------------------------------------------------

    def verify_insert(self, ctx, t, instances):
        schema = t.this
        tname = schema.this.name
        table = self.table_of(tname)
        if table is None:
            error(f"{ctx} INSERT en dbo.{tname}: la tabla no existe.")
            return
        cols = [i.name for i in schema.expressions]
        bad = [c for c in cols if table.col(c) is None]
        if bad:
            error(f"{ctx} INSERT en {tname}: columnas inexistentes {bad}.")
            return
        ok()
        self.need_grant(ctx, tname, "INSERT")
        values = t.args["expression"]
        if not isinstance(values, exp.Values):
            error(f"{ctx} INSERT sin VALUES (no verificable).")
            return
        row_exprs = values.expressions[0].expressions
        if len(row_exprs) != len(cols):
            error(f"{ctx} INSERT en {tname}: {len(cols)} columnas y {len(row_exprs)} valores.")
            return
        ok()
        ident = [c.name for c in table.columns.values() if c.identity]
        if set(map(str.lower, cols)) & set(map(str.lower, ident)):
            error(f"{ctx} INSERT en {tname} da valor a una columna IDENTITY.")
        for c in table.columns.values():
            if c.computed and c.name.lower() in {x.lower() for x in cols}:
                error(f"{ctx} INSERT en {tname} da valor a la columna calculada {c.name}.")
        # Columnas NOT NULL sin valor ni default.
        for c in table.columns.values():
            if not c.nullable and not c.identity and not c.computed and c.type != "ROWVERSION" and not c.has_default and c.name.lower() not in {x.lower() for x in cols}:
                error(f"{ctx} INSERT en {tname} no da valor a {c.name} (NOT NULL, sin DEFAULT).")

        for inst in instances:
            params = {p["name"]: p for p in inst["parameters"]}
            row = {}
            for c in table.columns.values():
                if c.computed:
                    row[c.name] = "UTD-1001"
                elif c.identity or c.type == "ROWVERSION":
                    row[c.name] = 1
                elif c.has_default:
                    row[c.name] = sql_default_value(c)
                else:
                    row[c.name] = None
            for cname, e in zip(cols, row_exprs):
                col = table.col(cname)
                if isinstance(e, exp.Parameter):
                    pname = param_name(e)
                    p = params.get(pname)
                    if p is None:
                        row[col.name] = "<variable>"
                        continue
                    if inst is instances[0]:
                        self.check_param_pair(ctx, col, tname, pname, params, write=True)
                    row[col.name] = None if p["isNull"] else convert(p["value"], p["type"])
                    if p["isNull"] and not col.nullable:
                        error(f"{ctx} INSERT en {tname}: {col.name} es NOT NULL pero recibió NULL ({inst['label']}).")
                else:
                    try:
                        row[col.name] = lit(e)
                    except Unsupported:
                        row[col.name] = "<expr>"
            fk_domain_errors(table, row, ctx, inst["label"])
            for cname, expr in table.checks:
                try:
                    res = ev(sqlglot.parse_one(expr, dialect="tsql"), row)
                except Unsupported as u:
                    warn(f"{ctx} no se pudo evaluar {tname}.{cname} ({u}).")
                    continue
                if res is False:
                    error(f"{ctx} INSERT en {tname} viola {cname} con los valores de «{inst['label']}»: {expr.strip()[:90]}")
                else:
                    ok()

    # -- UPDATE ----------------------------------------------------------------------------------------------------------

    def verify_update(self, ctx, t, instances):
        target = t.this
        tname = target.name
        cte = None
        w = t.args.get("with_")
        if w is not None:
            for c in w.expressions:
                if c.alias.lower() == tname.lower():
                    cte = c
        if cte is not None:
            inner = cte.this
            base = [x for x in inner.find_all(exp.Table) if x.args.get("db") is not None]
            if len(base) != 1:
                error(f"{ctx} UPDATE a través de un CTE con {len(base)} tablas (debe ser exactamente una).")
                return
            real = base[0].name
            visible = {c.name.lower() for c in inner.expressions}
            self.verify_select(ctx, inner)
            set_cols = [e.this.name for e in t.expressions]
            for c in set_cols:
                if c.lower() not in visible:
                    error(f"{ctx} el CTE no expone la columna {c}, que el UPDATE modifica.")
            aliases = {real.lower(): real}
            default_table = real
            tname_real = real
        else:
            tname_real = tname
            table = self.table_of(tname)
            if table is None:
                error(f"{ctx} UPDATE sobre dbo.{tname}: la tabla no existe.")
                return
            aliases = {tname.lower(): tname}
            default_table = tname
            set_cols = [e.this.name for e in t.expressions]
        table = self.table_of(tname_real)
        if table is None:
            error(f"{ctx} UPDATE sobre dbo.{tname_real}: la tabla no existe.")
            return
        bad = [c for c in set_cols if table.col(c) is None]
        if bad:
            error(f"{ctx} UPDATE de {tname_real}: columnas inexistentes {bad}.")
            return
        ok()
        for c in set_cols:
            col = table.col(c)
            if col.identity or col.computed or col.type == "ROWVERSION":
                error(f"{ctx} UPDATE de {tname_real}.{c}: columna IDENTITY/calculada/ROWVERSION.")
        self.need_grant(ctx, tname_real, "UPDATE", columns=set_cols) if tname_real == "Personal" else self.need_grant(ctx, tname_real, "UPDATE")
        self.need_grant(ctx, tname_real, "SELECT")   # el WHERE lee columnas
        if t.args.get("where") is None and not (cte is not None and cte.this.args.get("where") is not None):
            error(f"{ctx} UPDATE sobre {tname_real} SIN WHERE.")
        else:
            ok()
        # Columnas referenciadas en WHERE y en las expresiones de SET.
        for c in t.find_all(exp.Column):
            if c.table and c.table.upper() == "INSERTED":
                continue
            if table.col(c.name) is None and not (cte is not None and c.name.lower() in {x.name.lower() for x in cte.this.expressions}):
                error(f"{ctx} columna {c.name} no existe en {tname_real}.")
        # Tipos de los parámetros.
        inst = instances[0]
        params = {p["name"]: p for p in inst["parameters"]}
        self.current_params = params
        for e in t.expressions:
            col = table.col(e.this.name)
            rhs = e.expression
            # Solo asociamos cuando el parámetro es el valor directo (o COALESCE(@p, col) / CASE ... THEN @p ...).
            direct = []
            if isinstance(rhs, exp.Parameter):
                direct = [rhs]
            elif isinstance(rhs, exp.Coalesce):
                direct = [x for x in [rhs.this, *rhs.expressions] if isinstance(x, exp.Parameter)]
            elif isinstance(rhs, exp.Case):
                direct = [i.args["true"] for i in rhs.args["ifs"] if isinstance(i.args["true"], exp.Parameter)]
            for p in direct:
                pname = param_name(p)
                if pname:
                    self.check_param_pair(ctx, col, tname_real, pname, params, write=True)
        self.check_param_comparisons(ctx, t, aliases, default_table, params)
        for e in t.expressions:
            col = table.col(e.this.name)
            names = []
            if isinstance(e.expression, exp.Parameter):
                names = [param_name(e.expression)]
            elif isinstance(e.expression, exp.Coalesce):
                names = [param_name(x) for x in [e.expression.this, *e.expression.expressions] if isinstance(x, exp.Parameter)]
            for pn in names:
                for i in instances:
                    pp = {p["name"]: p for p in i["parameters"]}.get(pn)
                    if pp and not pp["isNull"]:
                        fk_domain_errors(table, {col.name: convert(pp["value"], pp["type"])}, ctx, i["label"])
        # NOT NULL: SET col = NULL
        for e in t.expressions:
            col = table.col(e.this.name)
            if isinstance(e.expression, exp.Null) and not col.nullable:
                error(f"{ctx} UPDATE de {tname_real}.{col.name}: se asigna NULL a una columna NOT NULL.")
            # Verifica que el parámetro nunca llegue NULL a una columna NOT NULL (en todas las instancias capturadas).
            if isinstance(e.expression, exp.Parameter) and not col.nullable:
                pn = param_name(e.expression)
                for i in instances:
                    pp = {p["name"]: p for p in i["parameters"]}.get(pn)
                    if pp and pp["isNull"]:
                        error(f"{ctx} UPDATE de {tname_real}.{col.name} recibe NULL en «{i['label']}».")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", required=True, help="carpeta con 01_tablas.sql … 05_seguridad.sql")
    ap.add_argument("--dump", required=True, help="JSON generado por SqlShapeTests (UTD_SQL_DUMP)")
    ap.add_argument("--src", required=True, help="carpeta UniTechDesk.Core/Domain (WorkflowCatalog.g.cs y Codes.cs)")
    args = ap.parse_args()

    import os
    p = lambda n: os.path.join(args.db, n)  # noqa: E731
    tables = parse_tables(read_sql(p("01_tablas.sql")))
    views = parse_views(read_sql(p("04_vistas.sql")))
    grants, denies = parse_grants(read_sql(p("05_seguridad.sql")))
    print(f"Esquema: {len(tables)} tablas, {len(views)} vistas; permisos de rol_unitech_api sobre {len(grants)} objetos.")

    dump = json.load(open(args.dump, encoding="utf-8"))
    by_sql = defaultdict(list)
    for s in dump:
        by_sql[s["sql"]].append(s)
    print(f"Volcado: {len(dump)} ejecuciones, {len(by_sql)} sentencias distintas.")

    load_seeds(read_sql(p("03_catalogos.sql")))
    v = Verifier(tables, views, grants, denies)
    for sql, instances in by_sql.items():
        v.current_params = {p["name"]: p for p in instances[0]["parameters"]}
        v.verify_batch(instances[0]["label"].split("#")[0].split("/")[0], sql, instances)

    verify_codes(args.src, tables, p("03_catalogos.sql"))

    print(f"\n{CHECKS} comprobaciones · {len(ERRORS)} errores · {len(WARNINGS)} avisos")
    for w in sorted(set(WARNINGS)):
        print("AVISO ", w)
    for e in sorted(set(ERRORS)):
        print("ERROR ", e)
    sys.exit(1 if ERRORS else 0)


# ======================================================================================================================
# 4. Catálogos y códigos: el backend y la base deben hablar los mismos códigos
# ======================================================================================================================

def sql_values(text, table):
    """Filas del INSERT … SELECT … FROM (VALUES …) AS v para una tabla de catálogo."""
    m = re.search(r"INSERT\s+INTO\s+dbo\." + table + r"\b.*?FROM\s*\(VALUES(.*?)\)\s*AS\s+v", text, re.I | re.S)
    if not m:
        return []
    rows = []
    for row in re.findall(r"\(([^()]*(?:'[^']*'[^()]*)*)\)", m.group(1)):
        vals = []
        for item in split_top(row):
            mm = re.fullmatch(r"N?'(.*)'", item, re.S)
            vals.append(mm.group(1) if mm else int(item))
        rows.append(tuple(vals))
    return rows


def load_seeds(cat):
    for t in ("TipoEquipo", "TipoTrabajoSoftware", "Accesorio", "Urgencia", "MetodoPago", "Moneda", "EstadoPago", "Rol", "EstadoTicket", "EstadoServicio", "TransicionEstado"):
        SEEDS[t] = sql_values(cat, t)
    for t in ("Carrera", "Recinto"):
        m = re.search(r"INSERT\s+INTO\s+dbo\." + t + r"\s*\(Id, Nombre\)\s*VALUES(.*?);", cat, re.I | re.S)
        SEEDS[t] = [(int(x),) for x in re.findall(r"\(\s*(\d+)\s*,", m.group(1))] if m else []


def verify_codes(src_dir, tables, catalog_path):
    import os
    cat = read_sql(catalog_path)
    g = open(os.path.join(src_dir, "WorkflowCatalog.g.cs"), encoding="utf-8-sig").read()
    codes = open(os.path.join(src_dir, "Codes.cs"), encoding="utf-8-sig").read()

    def cs_dict(name):
        m = re.search(r"\b" + name + r"\s*=\s*new Dictionary<[^>]*>\([^)]*\)\s*\{(.*?)\n\s*\};", g, re.S)
        return m.group(1) if m else ""

    def simple(name, table, with_order=False):
        cs = re.findall(r'\["(\w+)"\]\s*=\s*"', cs_dict(name))
        db = [r[0] for r in sql_values(cat, table)]
        if sorted(cs) != sorted(db):
            error(f"Catálogo {table}: backend {sorted(cs)} ≠ base {sorted(db)}")
        else:
            ok()

    simple("SoftwareKinds", "TipoTrabajoSoftware")
    simple("Accessories", "Accesorio")
    simple("Urgencies", "Urgencia")
    simple("PaymentMethods", "MetodoPago")
    simple("PaymentStatuses", "EstadoPago")
    simple("EquipmentTypes", "TipoEquipo")
    simple("Currencies", "Moneda")

    # Estados: código, a qué servicios aplica y si es final.
    st = re.findall(r'\["(\w+)"\]\s*=\s*new StatusInfo\("[^"]*",\s*new\[\]\s*\{([^}]*)\},\s*(true|false)\)', cs_dict("Statuses"))
    cs_states = {c: (sorted(re.findall(r'"(\w+)"', s)), f == "true") for c, s, f in st}
    db_states = {r[0]: r for r in sql_values(cat, "EstadoTicket")}
    db_serv = defaultdict(list)
    for r in sql_values(cat, "EstadoServicio"):
        db_serv[r[0]].append(r[1])
    if set(cs_states) != set(db_states):
        error(f"EstadoTicket: backend {sorted(cs_states)} ≠ base {sorted(db_states)}")
    else:
        for c, (svc, final) in cs_states.items():
            if sorted(db_serv[c]) != svc:
                error(f"EstadoServicio {c}: backend {svc} ≠ base {sorted(db_serv[c])}")
            elif bool(db_states[c][3]) != final:
                error(f"EstadoTicket {c}: EsFinal backend={final} base={bool(db_states[c][3])}")
            else:
                ok()
    # Transiciones.
    cs_tr = set()
    for src, dsts in re.findall(r'\["(\w+)"\]\s*=\s*(?:new\[\]\s*\{([^}]*)\}|Array\.Empty<string>\(\))', cs_dict("Transitions")):
        for d in re.findall(r'"(\w+)"', dsts):
            cs_tr.add((src, d))
    db_tr = set(sql_values(cat, "TransicionEstado"))
    if cs_tr != db_tr:
        error(f"TransicionEstado: solo en backend {sorted(cs_tr - db_tr)}; solo en base {sorted(db_tr - cs_tr)}")
    else:
        ok()
    # Urgencia: orden.
    rank = dict(re.findall(r'\["(\w+)"\]\s*=\s*(\d+)', cs_dict("UrgencyRank")))
    for code, _name, order in sql_values(cat, "Urgencia"):
        if int(rank.get(code, -1)) != order:
            error(f"Urgencia {code}: orden backend {rank.get(code)} ≠ base {order}")
        else:
            ok()

    # Códigos de Codes.cs frente a los CHECK de las tablas.
    def consts(cls):
        m = re.search(r"static class " + cls + r"\b[^{]*\{", codes)
        if not m:
            error(f"Codes.cs: no encontré la clase {cls}.")
            return []
        depth, i = 0, m.end() - 1
        while True:
            depth += {"{": 1, "}": -1}.get(codes[i], 0)
            if depth == 0:
                break
            i += 1
        return re.findall(r'const string \w+ = "([^"]+)"', codes[m.end():i])

    def in_list(table, constraint):
        for n, expr in tables[table].checks:
            if n == constraint:
                return set(re.findall(r"'(\w+)'", expr))
        return None

    def must_be_subset(label, values, allowed):
        extra = set(values) - set(allowed)
        if extra:
            error(f"{label}: el backend usa {sorted(extra)} que la base no permite ({sorted(allowed)}).")
        else:
            ok()

    must_be_subset("HistorialTicket.TipoEvento", consts("Event"), in_list("HistorialTicket", "CK_Historial_TipoEvento"))
    must_be_subset("HistorialTicket.AutorTipo", consts("Author"), in_list("HistorialTicket", "CK_Historial_AutorTipo"))
    must_be_subset("Adjunto.Categoria", consts("AttachmentCategory"), in_list("Adjunto", "CK_Adjunto_Categoria"))
    must_be_subset("Cotizacion.Decision", consts("Decision"), in_list("Cotizacion", "CK_Cotizacion_Decision"))
    must_be_subset("Cotizacion.DecididaPor", consts("DecidedBy"), in_list("Cotizacion", "CK_Cotizacion_DecididaPor"))
    must_be_subset("NotificacionCorreo.Estado", consts("MailState"), in_list("NotificacionCorreo", "CK_Notificacion_Estado"))
    must_be_subset("Solicitante.TipoSolicitante", consts("UserType"), in_list("Solicitante", "CK_Solicitante_Tipo"))
    must_be_subset("Ticket.TipoServicio", consts("Service"), in_list("Ticket", "CK_Ticket_TipoServicio"))
    plantilla = tables["NotificacionCorreo"].col("Plantilla").length
    for m in consts("Mail"):
        if len(m) > plantilla:
            error(f"Plantilla {m} ({len(m)}) no cabe en NotificacionCorreo.Plantilla VARCHAR({plantilla}).")
        else:
            ok()
    all_status = set(consts("Status"))
    if all_status != set(db_states):
        error(f"Codes.Status {sorted(all_status)} ≠ EstadoTicket {sorted(db_states)}")
    else:
        ok()
    if set(consts("PaymentStatus")) != {r[0] for r in sql_values(cat, "EstadoPago")}:
        error("Codes.PaymentStatus ≠ EstadoPago")
    else:
        ok()
    if set(consts("PaymentMethod")) != {r[0] for r in sql_values(cat, "MetodoPago")}:
        error("Codes.PaymentMethod ≠ MetodoPago")
    else:
        ok()


if __name__ == "__main__":
    main()
