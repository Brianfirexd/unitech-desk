using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Data;
using UniTechDesk.Tests.Support;
using UniTechDesk.TestSupport;

namespace UniTechDesk.Tests;

/// <summary>
/// El acceso a SQL Server (UniTechDesk.Data) ejecutado de verdad, pero contra una conexión que solo graba lo que recibe.
/// No sustituye probar contra SQL Server (eso lo hace 07_pruebas.sql + el smoke test), pero atrapa en segundos las clases de error más comunes:
/// parámetros sin declarar o sobrantes, textos más largos que su parámetro (SqlClient los trunca en silencio), escrituras que el rol de la API
/// no tiene permitidas, transacciones sin confirmar y errores de SQL Server mal traducidos.
/// Si se define la variable de entorno UTD_SQL_DUMP, además vuelca todas las sentencias a ese archivo JSON para verificarlas contra los scripts.
/// </summary>
public sealed class SqlShapeTests
{
    // ------------------------------------------------------------------ Escenario ------------------------------------------------------------------

    private sealed class CapturingTicketRepository : ITicketRepository
    {
        private readonly ITicketRepository _inner;
        public List<NewTicket> Created { get; } = new();
        public List<TicketChangeSet> Applied { get; } = new();
        public CapturingTicketRepository(ITicketRepository inner) => _inner = inner;

        public Task<CreatedTicket> CreateAsync(NewTicket ticket, CancellationToken ct) { Created.Add(ticket); return _inner.CreateAsync(ticket, ct); }
        public Task<TicketRecord?> FindByIdAsync(int id, CancellationToken ct) => _inner.FindByIdAsync(id, ct);
        public Task<TicketRecord?> FindByCodeAndEmailAsync(string code, string email, CancellationToken ct) => _inner.FindByCodeAndEmailAsync(code, email, ct);
        public Task<TicketPage> ListAsync(TicketQuery query, CancellationToken ct) => _inner.ListAsync(query, ct);
        public Task<TicketStats> GetStatsAsync(CancellationToken ct) => _inner.GetStatsAsync(ct);
        public Task<AttachmentRecord?> FindAttachmentAsync(int id, CancellationToken ct) => _inner.FindAttachmentAsync(id, ct);
        public Task ApplyAsync(TicketChangeSet changes, CancellationToken ct) { Applied.Add(changes); return _inner.ApplyAsync(changes, ct); }
    }

    /// <summary>Recorre los flujos reales del negocio y devuelve exactamente lo que el servicio le pidió al repositorio.</summary>
    private static async Task<(IReadOnlyList<NewTicket> Created, IReadOnlyList<TicketChangeSet> Applied)> RunBusinessScenarioAsync()
    {
        CapturingTicketRepository? cap = null;
        var env = new Env(wrapTickets: inner => cap = new CapturingTicketRepository(inner));
        var png = new UploadedFile("foto.png", "image/png", Samples.Png());
        var jpg = new UploadedFile("otra foto.jpg", "image/jpeg", Samples.Jpeg());
        var pdf = new UploadedFile("comprobante.pdf", "application/pdf", Samples.Pdf());
        var brief = new UploadedFile("brief.pdf", "application/pdf", Samples.Pdf());   // los PDF solo se aceptan en solicitudes de software y comprobantes

        // A) Estudiante, hardware, con pago: cotizar, retirar, re-cotizar (reemplaza), publicar, aprobar, pagar, entregar.
        var (a, codeA) = await env.CreateAsync(Env.StudentHardware(), png, jpg);
        await env.Review(a);
        await env.SaveQuote(a);
        await env.ToStatus(a, Codes.Status.Quoted);
        await env.ToStatus(a, Codes.Status.AwaitingApproval, "Te enviamos la cotización.");
        await env.ToStatus(a, Codes.Status.Quoted, "La retiro para corregirla.");                  // Unpublish
        await env.SaveQuote(a, 900m, "USD");                                                       // Replace sobre una vigente
        await env.ToStatus(a, Codes.Status.AwaitingApproval, "Cotización corregida.");             // Publish
        await env.ClientDecides(codeA, Codes.Decision.Approved);                                   // Decide (cliente) + pago pendiente
        await env.Public.SubmitPaymentProofAsync(codeA, Env.ClientEmail, "REF-2026-0001", pdf, default);   // ReportProof con comprobante (INSERT de Pago)
        await env.Public.SubmitPaymentProofAsync(codeA, Env.ClientEmail, "REF-2026-0002", null, default);  // ReportProof sin archivo (UPDATE de Pago)
        await env.Pay(a, Codes.PaymentStatus.Paid);                                                // Confirm
        await env.Pay(a, Codes.PaymentStatus.ProofSent);                                           // Unconfirm
        await env.Pay(a, Codes.PaymentStatus.Paid);                                                // Confirm otra vez
        await env.ToStatus(a, Codes.Status.InProgress);
        await env.Staff.AssignAsync(env.Admin, a, new AssignRequest { StaffId = env.TecId }, default);
        await env.Staff.AssignAsync(env.Admin, a, new AssignRequest { StaffId = null }, default);
        await env.Staff.AddNoteAsync(env.Admin, a, new NoteRequest { Text = "Se cambió la pasta térmica." }, default);
        await env.ToStatus(a, Codes.Status.Ready, "Listo para retirar.");
        await env.ToStatus(a, Codes.Status.Delivered);

        // B) Cliente externo, software, sin costo: no hay pago.
        var (b, codeB) = await env.CreateAsync(Env.ExternalSoftware(), brief);
        await env.Review(b);
        await env.SaveQuote(b, 0m, "NIO", validUntil: null);
        await env.ToStatus(b, Codes.Status.Quoted);
        await env.ToStatus(b, Codes.Status.AwaitingApproval, "Cotización enviada.");
        await env.ClientDecides(codeB, Codes.Decision.Approved, Env.ExternalEmail);
        await env.ToStatus(b, Codes.Status.InDevelopment);
        await env.ToStatus(b, Codes.Status.Testing);
        await env.ToStatus(b, Codes.Status.Completed);
        await env.ToStatus(b, Codes.Status.Delivered);

        // C) El personal registra la aprobación por WhatsApp y confirma el pago sin que exista aún la fila de pago.
        var (c, _) = await env.CreateAsync(Env.StudentHardware());
        await env.PublishQuoteAsync(c);
        await env.Staff.RecordQuoteDecisionAsync(env.Admin, c, new StaffQuoteDecisionRequest { Note = "Aprobó por WhatsApp." }, default);
        await env.Pay(c, Codes.PaymentStatus.Paid);

        // D) El cliente rechaza (se cancela) y el personal reabre (se descarta la cotización).
        var (d, codeD) = await env.CreateAsync(Env.ExternalSoftware());
        await env.PublishQuoteAsync(d);
        await env.ClientDecides(codeD, Codes.Decision.Rejected, Env.ExternalEmail);
        await env.ToStatus(d, Codes.Status.InReview, "Reabierto a pedido del cliente.");

        // E) Cancelado antes de cotizar y sin reparación.
        var (e, _) = await env.CreateAsync(Env.StudentHardware());
        await env.ToStatus(e, Codes.Status.Cancelled, "El cliente ya no lo necesita.");
        var (f, _) = await env.CreateAsync(Env.StudentHardware());
        await env.Review(f);
        await env.ToStatus(f, Codes.Status.Unrepairable, "La placa base no tiene reparación.");

        return (cap!.Created, cap.Applied);
    }

    private sealed record Replay(RecordingDb Db, int Creates, int Applies);

    private static async Task<Replay> ReplayAsync(Action<RecordingDb>? configure = null)
    {
        var (created, applied) = await RunBusinessScenarioAsync();
        var db = new RecordingDb();
        configure?.Invoke(db);
        var classifier = new FakeSqlErrorClassifier();
        var repo = new SqlTicketRepository(db, classifier, NullLogger<SqlTicketRepository>.Instance);

        foreach (var n in created) { db.Label = "Create:" + n.ServiceType; await repo.CreateAsync(n, default); }
        var i = 0;
        foreach (var c in applied)
        {
            db.Label = $"Apply:{c.QuoteOp}/{c.PaymentOp}/{(c.NewStatus ?? "-")}#{++i}";
            await repo.ApplyAsync(c, default);
        }

        // Lecturas del repositorio de tickets: todos los filtros y todos los órdenes.
        db.Label = "Read";
        await repo.FindByIdAsync(1001, default);
        await repo.FindByCodeAndEmailAsync("UTD-1001", Env.ClientEmail, default);
        await repo.FindAttachmentAsync(1, default);
        await repo.GetStatsAsync(default);
        await repo.ListAsync(new TicketQuery(), default);
        foreach (var sort in new[] { "date_desc", "date_asc", "urgency_desc" })
            await repo.ListAsync(new TicketQuery { Status = "OPEN", Service = "HARDWARE", Urgency = "HIGH", Payment = "PENDING", Text = "50%_[a]\\", Sort = sort, Page = 2, PageSize = 25 }, default);

        var staff = new SqlStaffRepository(db, classifier, NullLogger<SqlStaffRepository>.Instance);
        db.Label = "Staff";
        await staff.FindByUsernameAsync("admin", default);
        await staff.FindByIdAsync(1, default);
        await staff.ListActiveAsync(default);
        await staff.RegisterFailedLoginAsync(1, DateTime.UtcNow, 5, DateTime.UtcNow.AddMinutes(15), default);
        await staff.RegisterSuccessfulLoginAsync(1, DateTime.UtcNow, default);

        var outbox = new SqlOutboxRepository(db, classifier, NullLogger<SqlOutboxRepository>.Instance);
        db.Label = "Outbox";
        await outbox.ClaimDueAsync(DateTime.UtcNow, 20, TimeSpan.FromMinutes(5), default);
        await outbox.MarkSentAsync(1, DateTime.UtcNow, default);
        await outbox.MarkFailedAsync(1, "Error 429", DateTime.UtcNow.AddMinutes(5), giveUp: false, default);
        await outbox.MarkFailedAsync(1, "Error 400", DateTime.UtcNow, giveUp: true, default);

        db.Label = "Catalogs";
        await new SqlCatalogRepository(db, classifier, NullLogger<SqlCatalogRepository>.Instance).GetCatalogsAsync(default);

        return new Replay(db, created.Count, applied.Count);
    }

    // ------------------------------------------------------------------ Pruebas ------------------------------------------------------------------

    [Fact]
    public async Task El_escenario_ejercita_todas_las_ramas_de_escritura()
    {
        var (created, applied) = await RunBusinessScenarioAsync();
        Assert.Contains(created, n => n.Hardware is not null && n.Attachments.Count == 2);
        Assert.Contains(created, n => n.Software is not null);
        Assert.Contains(created, n => n.RequesterType == "EXTERNAL");
        foreach (var op in Enum.GetValues<QuoteOp>().Where(o => o != QuoteOp.None)) Assert.Contains(applied, c => c.QuoteOp == op);
        foreach (var op in Enum.GetValues<PaymentOp>().Where(o => o != PaymentOp.None)) Assert.Contains(applied, c => c.PaymentOp == op);
        Assert.Contains(applied, c => c.NewProof is not null);
        Assert.Contains(applied, c => c.PaymentOp == PaymentOp.ReportProof && c.NewProof is null);
        Assert.Contains(applied, c => c.ChangeAssignee && c.NewAssigneeId is not null);
        Assert.Contains(applied, c => c.ChangeAssignee && c.NewAssigneeId is null);
        Assert.Contains(applied, c => c.Outbox.Count > 0);
    }

    [Fact]
    public async Task Cada_sentencia_declara_exactamente_los_parametros_que_usa()
    {
        var r = await ReplayAsync();
        Assert.True(r.Db.Statements.Count > 100, "El escenario debería generar muchas sentencias.");
        foreach (var st in r.Db.Statements)
        {
            var used = UsedParameters(st.Sql);
            var declaredHere = Regex.Matches(st.Sql, @"DECLARE\s+(@\w+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var passed = st.Params.Select(p => p.Name).ToList();

            Assert.Equal(passed.Count, passed.Distinct(StringComparer.OrdinalIgnoreCase).Count());            // sin nombres repetidos
            Assert.All(passed, n => Assert.StartsWith("@", n));
            var missing = used.Where(u => !declaredHere.Contains(u) && !passed.Contains(u, StringComparer.OrdinalIgnoreCase)).ToList();
            var unused = passed.Where(p => !used.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            Assert.True(missing.Count == 0, $"[{st.Label}] parámetros usados pero no enviados: {string.Join(", ", missing)}\n{st.Sql}");
            Assert.True(unused.Count == 0, $"[{st.Label}] parámetros enviados pero no usados: {string.Join(", ", unused)}\n{st.Sql}");
        }
    }

    [Fact]
    public async Task Los_textos_caben_en_el_tamano_declarado_del_parametro()
    {
        // SqlClient trunca en silencio un valor más largo que Size: se perdería información sin ningún error.
        var r = await ReplayAsync();
        foreach (var st in r.Db.Statements)
            foreach (var p in st.Params)
            {
                if (p.Type is DbType.String or DbType.AnsiString)
                {
                    Assert.True(p.Size > 0, $"[{st.Label}] {p.Name}: un parámetro de texto sin tamaño.");
                    if (p.Value is string s) Assert.True(s.Length <= p.Size, $"[{st.Label}] {p.Name}: {s.Length} caracteres no caben en {p.Size}.");
                }
                if (p.Type == DbType.Binary && p.Value is byte[] bytes) Assert.True(bytes.Length <= p.Size, $"[{st.Label}] {p.Name}: {bytes.Length} bytes no caben en {p.Size}.");
            }
    }

    [Fact]
    public async Task Las_busquedas_de_texto_escapan_los_comodines_de_LIKE()
    {
        var r = await ReplayAsync();
        var q = r.Db.Statements.Where(s => s.Label == "Read" && s.Params.Any(p => p.Name == "@q")).Select(s => (string)s.Params.First(p => p.Name == "@q").Value!).Distinct().Single();
        Assert.Equal(@"%50\%\_\[a]\\%", q);                 // % _ [ y \ del usuario no actúan como comodines
        Assert.All(r.Db.Statements.Where(s => s.Sql.Contains("LIKE @q")), s => Assert.Contains("ESCAPE '\\'", s.Sql));
    }

    [Fact]
    public async Task Todo_valor_viaja_como_parametro_y_nunca_pegado_al_SQL()
    {
        // El texto de cada sentencia es el mismo sin importar qué escriba la persona: se compara el SQL de dos escenarios con datos distintos.
        var a = await ReplayAsync();
        var texts = a.Db.Statements.Select(s => s.Sql).ToList();
        foreach (var p in a.Db.Statements.SelectMany(s => s.Params).Where(p => p.Value is string { Length: >= 6 } v && !Regex.IsMatch(v, "^[A-Z_]+$")).Select(p => (string)p.Value!).Distinct())
            Assert.DoesNotContain(texts, t => t.Contains(p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Solo_se_ejecutan_escrituras_que_el_rol_de_la_API_tiene_permitidas()
    {
        var r = await ReplayAsync();
        var insertable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Solicitante", "Ticket", "DetalleHardware", "DetalleSoftware", "TicketAccesorio", "Adjunto", "Cotizacion", "Pago", "NotificacionCorreo", "HistorialTicket" };
        var updatable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ticket", "Cotizacion", "Pago", "NotificacionCorreo", "Personal" };   // nunca HistorialTicket
        var personalColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IntentosFallidos", "BloqueadoHasta", "UltimoAcceso" };

        foreach (var st in r.Db.Statements)
        {
            var sql = Regex.Replace(st.Sql, @"'[^']*'", "''");
            Assert.DoesNotMatch(@"(?i)\b(DELETE|TRUNCATE|DROP|ALTER|CREATE|EXEC|EXECUTE|MERGE|GRANT|SELECT\s+.*\bINTO\b)\b", sql);
            foreach (Match m in Regex.Matches(sql, @"(?i)\bINSERT\s+INTO\s+dbo\.(\w+)")) Assert.Contains(m.Groups[1].Value, insertable);
            foreach (Match m in Regex.Matches(sql, @"(?i)\bUPDATE\s+(?:dbo\.)?(\w+)\s+SET"))
            {
                var table = m.Groups[1].Value.Equals("due", StringComparison.OrdinalIgnoreCase) ? "NotificacionCorreo" : m.Groups[1].Value;   // "due" es el CTE de la bandeja
                Assert.Contains(table, updatable);
                if (table.Equals("Personal", StringComparison.OrdinalIgnoreCase))
                {
                    var setClause = sql[(m.Index + m.Length)..];
                    setClause = setClause[..setClause.LastIndexOf("WHERE", StringComparison.OrdinalIgnoreCase)];
                    var assigned = Regex.Matches(setClause, @"(?<![<>!=])\b([A-Za-z_]\w*)\s*=(?!=)").Select(x => x.Groups[1].Value).Distinct().ToList();
                    Assert.NotEmpty(assigned);
                    Assert.All(assigned, col => Assert.Contains(col, personalColumns));
                }
            }
        }
    }

    [Fact]
    public async Task Las_escrituras_de_un_ticket_van_en_una_sola_transaccion_confirmada()
    {
        var r = await ReplayAsync();
        var writes = r.Db.Statements.Where(s => s.Label.StartsWith("Create:") || s.Label.StartsWith("Apply:")).ToList();
        Assert.NotEmpty(writes);
        Assert.All(writes, s => Assert.True(s.InTransaction, $"[{s.Label}] fuera de transacción:\n{s.Sql}"));
        Assert.Equal(r.Creates + r.Applies, r.Db.Commits);
        Assert.Equal(0, r.Db.Rollbacks);
        // Las lecturas y las operaciones de una sola sentencia (login, bandeja) no abren transacción.
        Assert.All(r.Db.Statements.Where(s => s.Label is "Read" or "Staff" or "Outbox" or "Catalogs"), s => Assert.False(s.InTransaction));
    }

    [Fact]
    public async Task Cada_cambio_de_un_ticket_empieza_por_el_UPDATE_protegido_por_ROWVERSION()
    {
        var r = await ReplayAsync();
        var applies = r.Db.Statements.Where(s => s.Label.StartsWith("Apply:")).GroupBy(s => s.Label).ToList();
        Assert.Equal(r.Applies, applies.Count);
        foreach (var g in applies)
        {
            var first = g.First();
            Assert.Contains("UPDATE dbo.Ticket", first.Sql);
            Assert.Contains("VersionFila = @version", first.Sql);
            Assert.Equal(DbType.Binary, first.Params.Single(p => p.Name == "@version").Type);
            Assert.Equal(8, first.Params.Single(p => p.Name == "@version").Size);
        }
    }

    [Fact]
    public async Task El_historial_nunca_se_actualiza_ni_se_borra()
    {
        var r = await ReplayAsync();
        Assert.All(r.Db.Statements, s => Assert.DoesNotMatch(@"(?i)\bUPDATE\s+(dbo\.)?HistorialTicket\b", s.Sql));
        Assert.Contains(r.Db.Statements, s => Regex.IsMatch(s.Sql, @"INSERT INTO dbo\.HistorialTicket"));
    }

    [Fact]
    public async Task El_ticket_se_crea_sin_OUTPUT_porque_la_tabla_tiene_trigger()
    {
        // OUTPUT sin INTO sobre una tabla con trigger da el error 334 en SQL Server.
        var r = await ReplayAsync();
        Assert.All(r.Db.Statements, s =>
        {
            Assert.DoesNotMatch(@"(?is)INSERT\s+INTO\s+dbo\.Ticket\b[^;]*\bOUTPUT\b", s.Sql);
            Assert.DoesNotMatch(@"(?is)UPDATE\s+dbo\.Ticket\b[^;]*\bOUTPUT\b", s.Sql);
        });
        var insert = r.Db.Statements.First(s => s.Sql.Contains("INSERT INTO dbo.Ticket"));
        Assert.Contains("SCOPE_IDENTITY()", insert.Sql);
    }

    [Fact]
    public async Task Los_opcionales_vacios_se_guardan_como_NULL_y_los_datos_por_tipo_son_coherentes()
    {
        var r = await ReplayAsync();
        var inserts = r.Db.Statements.Where(s => s.Sql.Contains("INSERT INTO dbo.Solicitante")).ToList();
        Assert.Contains(inserts, s => ValueOf(s, "@tipo") == "STUDENT");
        Assert.Contains(inserts, s => ValueOf(s, "@tipo") == "EXTERNAL");
        foreach (var s in inserts)
        {
            var student = ValueOf(s, "@tipo") == "STUDENT";
            // CK_Solicitante_DatosPorTipo: los campos del otro tipo deben ser NULL (no cadenas vacías).
            foreach (var name in student ? new[] { "@empresa", "@ruc" } : new[] { "@carnet", "@carrera", "@recinto" })
                Assert.True(s.Params.Single(p => p.Name == name).IsNull, $"{name} debe ser NULL para {(student ? "STUDENT" : "EXTERNAL")}");
            foreach (var name in student ? new[] { "@carnet", "@carrera", "@recinto" } : new[] { "@empresa", "@ruc" })
                Assert.False(s.Params.Single(p => p.Name == name).IsNull, $"{name} es obligatorio para {(student ? "STUDENT" : "EXTERNAL")}");
        }
        // Serie y "otros accesorios" vacíos → NULL, no "".
        Assert.All(r.Db.Statements.SelectMany(s => s.Params).Where(p => p.Value is string), p => Assert.NotEqual("", (string)p.Value!));
    }

    [Fact]
    public async Task Las_lecturas_toleran_columnas_nulas_y_no_piden_columnas_de_mas()
    {
        var nullable = new[] { "CarreraId", "CarreraNombre", "RecintoId", "RecintoNombre", "Carnet", "Empresa", "Ruc", "AsignadoAId", "AsignadoANombre", "TipoEquipoCodigo",
            "Marca", "Modelo", "Serie", "Enciende", "OtrosAccesorios", "TipoTrabajoCodigo", "FechaDeseada", "UrlReferencia", "CotizacionId", "VigenteHasta", "FechaPublicacion",
            "Decision", "DecididaPor", "FechaDecision", "Referencia", "ComprobanteAdjuntoId", "ComprobanteNombre", "FechaReporte", "ConfirmadoPorId", "FechaConfirmacion",
            "PersonalId", "PersonalNombre", "EstadoAnterior", "EstadoNuevo", "Texto", "Correo", "BloqueadoHasta" };
        var r = await ReplayAsync(db => { foreach (var c in nullable) db.NullColumns.Add(c); });
        Assert.True(r.Db.Statements.Count > 100);
    }

    // ------------------------------------------------------------------ Traducción de errores ------------------------------------------------------------------

    private static (SqlTicketRepository Repo, RecordingDb Db) NewRepo(Func<RecordingDb.Statement, int?>? onExecute = null, bool rollbackThrows = false)
    {
        var db = new RecordingDb { OnExecute = onExecute, RollbackThrows = rollbackThrows };
        return (new SqlTicketRepository(db, new FakeSqlErrorClassifier(), NullLogger<SqlTicketRepository>.Instance), db);
    }

    private static async Task<TicketChangeSet> AnyChangeAsync()
    {
        var (_, applied) = await RunBusinessScenarioAsync();
        return applied.First(c => c.NewStatus == Codes.Status.Delivered);
    }

    private static async Task<Exception> Catch(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { return ex; }
        throw new Xunit.Sdk.XunitException("Se esperaba una excepción.");
    }

    [Fact]
    public async Task Los_errores_de_SQL_Server_se_traducen_a_respuestas_claras()
    {
        var change = await AnyChangeAsync();

        async Task<Exception> WithErrors(params int[] numbers)
        {
            var (repo, _) = NewRepo(st => st.Sql.Contains("UPDATE dbo.Ticket") ? throw new FakeDbException(numbers) : null);
            return await Catch(() => repo.ApplyAsync(change, default));
        }

        var trigger1 = Assert.IsType<AppException>(await WithErrors(51001));
        Assert.Equal(409, trigger1.StatusCode);
        Assert.Contains("aprueba la cotización", trigger1.Message);

        var trigger2 = Assert.IsType<AppException>(await WithErrors(51002));
        Assert.Equal(409, trigger2.StatusCode);
        Assert.Contains("pago pendiente", trigger2.Message);

        foreach (var transient in new[] { 1205, -2, 1222 }) Assert.IsType<TransientStoreException>(await WithErrors(transient));

        foreach (var dup in new[] { 2627, 2601 })
        {
            var e = Assert.IsType<AppException>(await WithErrors(dup));
            Assert.Equal(409, e.StatusCode);
        }
        var fk = Assert.IsType<AppException>(await WithErrors(547));
        Assert.Equal(409, fk.StatusCode);

        // Un error desconocido sube tal cual (la API lo convierte en un 500 genérico sin detalles).
        Assert.IsType<FakeDbException>(await WithErrors(9999));
    }

    [Fact]
    public async Task Si_un_trigger_ya_cancelo_la_transaccion_el_error_real_no_se_pierde()
    {
        // Los triggers hacen ROLLBACK y luego THROW: al intentar el rollback propio, SqlClient avisa que la transacción ya terminó.
        var change = await AnyChangeAsync();
        var (repo, db) = NewRepo(st => st.Sql.Contains("UPDATE dbo.Ticket") ? throw new FakeDbException(51002) : null, rollbackThrows: true);
        var e = Assert.IsType<AppException>(await Catch(() => repo.ApplyAsync(change, default)));
        Assert.Equal(409, e.StatusCode);
        Assert.Equal(0, db.Commits);
    }

    [Fact]
    public async Task Un_fallo_a_mitad_de_la_operacion_revierte_y_no_confirma()
    {
        var change = await AnyChangeAsync();
        var (repo, db) = NewRepo(st => st.Sql.Contains("INSERT INTO dbo.HistorialTicket") ? throw new FakeDbException(1205) : null);
        Assert.IsType<TransientStoreException>(await Catch(() => repo.ApplyAsync(change, default)));
        Assert.Equal(0, db.Commits);
        Assert.Equal(1, db.Rollbacks);
    }

    [Fact]
    public async Task Un_UPDATE_que_no_encuentra_su_fila_es_conflicto_de_concurrencia()
    {
        var (_, applied) = await RunBusinessScenarioAsync();

        // Versión vencida del ticket: el UPDATE no afecta ninguna fila.
        var (repo, db) = NewRepo(_ => 0);
        await Assert.ThrowsAsync<ConcurrencyException>(() => repo.ApplyAsync(applied[0], default));
        Assert.Equal(0, db.Commits);
        Assert.Equal(1, db.Rollbacks);

        // La cotización ya cambió (el UPDATE del ticket sí encontró su fila, el de la cotización no).
        var publish = applied.First(c => c.QuoteOp == QuoteOp.Publish);
        var (repo2, db2) = NewRepo(st => st.Sql.Contains("UPDATE dbo.Cotizacion") ? 0 : 1);
        await Assert.ThrowsAsync<ConcurrencyException>(() => repo2.ApplyAsync(publish, default));
        Assert.Equal(0, db2.Commits);
    }

    // ------------------------------------------------------------------ Volcado para el verificador de SQL ------------------------------------------------------------------

    [Fact]
    public async Task Volcado_de_sentencias_para_verificar_contra_los_scripts()
    {
        var path = Environment.GetEnvironmentVariable("UTD_SQL_DUMP");
        if (string.IsNullOrWhiteSpace(path)) return;
        var r = await ReplayAsync();

        static object? Show(RecordingDb.Param p) => p.Value switch
        {
            null or DBNull => null,
            byte[] b => $"<{b.Length} bytes>",
            DateTime d => d.ToString("o", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString()
        };

        var dump = r.Db.Statements.Select(s => new
        {
            label = s.Label, kind = s.Kind, inTransaction = s.InTransaction, sql = s.Sql, columnsRead = s.ColumnsRead,
            parameters = s.Params.Select(p => new { name = p.Name, type = p.Type.ToString(), size = p.Size, precision = p.Precision, scale = p.Scale, isNull = p.IsNull, value = Show(p) })
        });
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ Apoyo ------------------------------------------------------------------

    private static HashSet<string> UsedParameters(string sql)
    {
        var clean = Regex.Replace(sql, @"'[^']*'", "''");
        return Regex.Matches(clean, @"(?<![@\w])@([A-Za-z_]\w*)").Select(m => "@" + m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ValueOf(RecordingDb.Statement s, string name) => s.Params.Single(p => p.Name == name).Value as string;
}
