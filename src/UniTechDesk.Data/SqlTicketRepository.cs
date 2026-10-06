using System.Data.Common;
using System.Text;
using Microsoft.Extensions.Logging;
using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Domain;
using UniTechDesk.Data.Sql;

namespace UniTechDesk.Data;

/// <summary>
/// Tickets en SQL Server con SQL explícito. Cada escritura es UNA transacción. Los cambios de un ticket existente se protegen
/// con el ROWVERSION (VersionFila): si otra persona lo cambió, el UPDATE no afecta filas y se lanza ConcurrencyException.
/// Nota: dbo.Ticket tiene un trigger, por eso no se usa OUTPUT directo en sus INSERT/UPDATE (SQL Server lo prohíbe: error 334).
/// </summary>
public sealed class SqlTicketRepository : ITicketRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ISqlErrorClassifier _classifier;
    private readonly ILogger<SqlTicketRepository> _log;

    public SqlTicketRepository(IDbConnectionFactory db, ISqlErrorClassifier classifier, ILogger<SqlTicketRepository> log)
    {
        _db = db; _classifier = classifier; _log = log;
    }

    // ============================== Lectura ==============================

    private const string HeaderColumns = @"
        t.Id, t.Codigo, t.FechaCreacion, t.FechaActualizacion, t.VersionFila,
        t.TipoServicio, t.EstadoCodigo, t.EstadoPagoCodigo, t.UrgenciaCodigo, t.MetodoPagoPreferido, t.Descripcion,
        t.AsignadoAId, pa.NombreCompleto AS AsignadoANombre,
        s.TipoSolicitante, s.NombreCompleto, s.Correo, s.Telefono, s.Carnet, s.CarreraId, ca.Nombre AS CarreraNombre,
        s.RecintoId, re.Nombre AS RecintoNombre, s.Empresa, s.Ruc,
        hw.TipoEquipoCodigo, hw.Marca, hw.Modelo, hw.Serie, hw.Enciende, hw.OtrosAccesorios,
        sw.TipoTrabajoCodigo, sw.FechaDeseada, sw.UrlReferencia";

    private const string HeaderJoins = @"
        FROM dbo.Ticket AS t
        INNER JOIN dbo.Solicitante AS s ON s.Id = t.SolicitanteId
        INNER JOIN dbo.Urgencia AS u ON u.Codigo = t.UrgenciaCodigo
        LEFT JOIN dbo.Carrera AS ca ON ca.Id = s.CarreraId
        LEFT JOIN dbo.Recinto AS re ON re.Id = s.RecintoId
        LEFT JOIN dbo.DetalleHardware AS hw ON hw.TicketId = t.Id
        LEFT JOIN dbo.DetalleSoftware AS sw ON sw.TicketId = t.Id
        LEFT JOIN dbo.Personal AS pa ON pa.Id = t.AsignadoAId";

    private static TicketRecord ReadHeader(Row r)
    {
        var t = new TicketRecord
        {
            Id = r.Int("Id"), Code = r.Str("Codigo"), CreatedAt = r.Utc("FechaCreacion"), UpdatedAt = r.Utc("FechaActualizacion"),
            RowVersion = r.Bytes("VersionFila"),
            ServiceType = r.Str("TipoServicio"), Status = r.Str("EstadoCodigo"), PaymentStatus = r.Str("EstadoPagoCodigo"),
            Urgency = r.Str("UrgenciaCodigo"), PreferredPaymentMethod = r.Str("MetodoPagoPreferido"), Description = r.Str("Descripcion"),
            RequesterType = r.Str("TipoSolicitante"), FullName = r.Str("NombreCompleto"), Email = r.Str("Correo"), Phone = r.Str("Telefono"),
            StudentId = r.Str("Carnet"), MajorId = r.IntN("CarreraId"), MajorName = r.Str("CarreraNombre"),
            CampusId = r.IntN("RecintoId"), CampusName = r.Str("RecintoNombre"), Company = r.Str("Empresa"), Ruc = r.Str("Ruc")
        };
        if (r.IntN("AsignadoAId") is { } aid) t.Assignee = new StaffRef { Id = aid, Name = r.Str("AsignadoANombre") };
        if (!r.IsNull("TipoEquipoCodigo"))
            t.Hardware = new HardwareInfo
            {
                EquipmentType = r.Str("TipoEquipoCodigo"), Brand = r.Str("Marca"), Model = r.Str("Modelo"), Serial = r.Str("Serie"),
                PowersOn = r.Str("Enciende"), AccessoriesOther = r.Str("OtrosAccesorios")
            };
        if (!r.IsNull("TipoTrabajoCodigo"))
            t.Software = new SoftwareInfo { Kind = r.Str("TipoTrabajoCodigo"), DesiredDate = r.DateN("FechaDeseada"), ReferenceUrl = r.Str("UrlReferencia") };
        return t;
    }

    private static QuoteRecord ReadQuote(Row r, string idColumn) => new()
    {
        Id = r.Int(idColumn), Amount = r.Dec("Monto"), Currency = r.Str("MonedaCodigo"), Description = r.Str("Detalle"),
        ValidUntil = r.DateN("VigenteHasta"), Published = r.Bool("Publicada"), PublishedAt = r.UtcN("FechaPublicacion"),
        Decision = r.StrN("Decision"), DecidedBy = r.StrN("DecididaPor"), DecidedAt = r.UtcN("FechaDecision")
    };

    public async Task<TicketRecord?> FindByIdAsync(int id, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            // Un solo viaje a la BD con varios resultados: encabezado, accesorios, cotización vigente, pago, adjuntos e historial.
            await using var cmd = conn.Command(@"
                SELECT " + HeaderColumns + HeaderJoins + @" WHERE t.Id = @id;
                SELECT AccesorioCodigo FROM dbo.TicketAccesorio WHERE TicketId = @id ORDER BY AccesorioCodigo;
                SELECT TOP (1) c.Id AS CotizacionId, c.Monto, c.MonedaCodigo, c.Detalle, c.VigenteHasta, c.Publicada, c.FechaPublicacion,
                       c.Decision, c.DecididaPor, c.FechaDecision
                FROM dbo.Cotizacion AS c WHERE c.TicketId = @id AND c.Descartada = 0;
                SELECT pg.Id AS PagoId, pg.CotizacionId, pg.MetodoCodigo, pg.Referencia, pg.ComprobanteAdjuntoId,
                       ad.NombreOriginal AS ComprobanteNombre, pg.FechaReporte, pg.ConfirmadoPorId, pg.FechaConfirmacion
                FROM dbo.Pago AS pg LEFT JOIN dbo.Adjunto AS ad ON ad.Id = pg.ComprobanteAdjuntoId
                WHERE pg.TicketId = @id;
                SELECT a.Id, a.Categoria, a.NombreOriginal, a.NombreAlmacenado, a.TipoMime, a.TamanoBytes, a.FechaSubida
                FROM dbo.Adjunto AS a WHERE a.TicketId = @id ORDER BY a.Id;
                SELECT h.Id, h.FechaEvento, h.TipoEvento, h.AutorTipo, h.PersonalId, pe.NombreCompleto AS PersonalNombre,
                       h.EstadoAnterior, h.EstadoNuevo, h.Texto, h.EsInterno
                FROM dbo.HistorialTicket AS h LEFT JOIN dbo.Personal AS pe ON pe.Id = h.PersonalId
                WHERE h.TicketId = @id ORDER BY h.FechaEvento, h.Id;").Int("@id", id);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var t = ReadHeader(new Row(reader));

            await reader.NextResultAsync(ct);
            var accessories = new List<string>();
            while (await reader.ReadAsync(ct)) accessories.Add(new Row(reader).Str("AccesorioCodigo"));
            t.Hardware?.Accessories.AddRange(accessories);

            await reader.NextResultAsync(ct);
            if (await reader.ReadAsync(ct)) t.Quote = ReadQuote(new Row(reader), "CotizacionId");

            await reader.NextResultAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var r = new Row(reader);
                t.Payment = new PaymentRecord
                {
                    Id = r.Int("PagoId"), QuoteId = r.Int("CotizacionId"), MethodCode = r.Str("MetodoCodigo"), Reference = r.StrN("Referencia"),
                    ProofAttachmentId = r.IntN("ComprobanteAdjuntoId"), ProofName = r.StrN("ComprobanteNombre"), ReportedAt = r.UtcN("FechaReporte"),
                    ConfirmedById = r.IntN("ConfirmadoPorId"), ConfirmedAt = r.UtcN("FechaConfirmacion")
                };
            }

            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var r = new Row(reader);
                t.Attachments.Add(new AttachmentRecord
                {
                    Id = r.Int("Id"), Category = r.Str("Categoria"), OriginalName = r.Str("NombreOriginal"), StoredName = r.Str("NombreAlmacenado"),
                    Mime = r.Str("TipoMime"), Size = r.Int("TamanoBytes"), UploadedAt = r.Utc("FechaSubida")
                });
            }

            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var r = new Row(reader);
                var author = r.Str("AutorTipo");
                t.History.Add(new HistoryRecord
                {
                    Id = r.Long("Id"), At = r.Utc("FechaEvento"), Type = r.Str("TipoEvento"), AuthorType = author, StaffId = r.IntN("PersonalId"),
                    AuthorName = author switch { Codes.Author.Staff => r.Str("PersonalNombre"), Codes.Author.Client => "Cliente", _ => "Sistema" },
                    From = r.StrN("EstadoAnterior"), To = r.StrN("EstadoNuevo"), Text = r.Str("Texto"), Internal = r.Bool("EsInterno")
                });
            }
            return t;
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    public async Task<TicketRecord?> FindByCodeAndEmailAsync(string code, string email, CancellationToken ct)
    {
        int? id;
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command(@"
                SELECT t.Id FROM dbo.Ticket AS t INNER JOIN dbo.Solicitante AS s ON s.Id = t.SolicitanteId
                WHERE t.Codigo = @code AND s.Correo = @email;")
                .Varchar("@code", code, 14).NVarchar("@email", email, 254);
            var o = await cmd.ExecuteScalarAsync(ct);
            id = o is null or DBNull ? null : Convert.ToInt32(o);
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
        return id is null ? null : await FindByIdAsync(id.Value, ct);
    }

    public async Task<TicketPage> ListAsync(TicketQuery q, CancellationToken ct)
    {
        var where = new StringBuilder(" WHERE 1 = 1");
        void Bind(DbCommand c)
        {
            if (q.Status is not null) c.Varchar("@status", q.Status, 20);
            if (q.Service is not null) c.Varchar("@service", q.Service, 10);
            if (q.Urgency is not null) c.Varchar("@urgency", q.Urgency, 10);
            if (q.Payment is not null) c.Varchar("@payment", q.Payment, 20);
            if (q.Text is not null) c.NVarchar("@q", "%" + EscapeLike(q.Text) + "%", 210);
        }
        if (q.Status is not null) where.Append(" AND t.EstadoCodigo = @status");
        if (q.Service is not null) where.Append(" AND t.TipoServicio = @service");
        if (q.Urgency is not null) where.Append(" AND t.UrgenciaCodigo = @urgency");
        if (q.Payment is not null) where.Append(" AND t.EstadoPagoCodigo = @payment");
        if (q.Text is not null)
            where.Append(@" AND (t.Codigo LIKE @q ESCAPE '\' OR s.NombreCompleto LIKE @q ESCAPE '\' OR s.Correo LIKE @q ESCAPE '\'
                OR s.Carnet LIKE @q ESCAPE '\' OR s.Ruc LIKE @q ESCAPE '\' OR s.Empresa LIKE @q ESCAPE '\' OR t.Descripcion LIKE @q ESCAPE '\'
                OR CONCAT(hw.Marca, N' ', hw.Modelo) LIKE @q ESCAPE '\')");

        var order = q.Sort switch
        {
            "date_asc" => "t.FechaCreacion ASC, t.Id ASC",
            "urgency_desc" => "u.Orden DESC, t.FechaCreacion DESC, t.Id DESC",
            _ => "t.FechaCreacion DESC, t.Id DESC"
        };

        try
        {
            await using var conn = await _db.OpenAsync(ct);

            int total;
            await using (var count = conn.Command("SELECT COUNT(*) " + HeaderJoins + where))
            {
                Bind(count);
                total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
            }
            var pages = Math.Max(1, (int)Math.Ceiling(total / (double)q.PageSize));
            var page = Math.Min(Math.Max(q.Page, 1), pages);

            var items = new List<TicketRecord>();
            await using (var cmd = conn.Command(@"
                SELECT " + HeaderColumns + @",
                       c.Id AS CotizacionId, c.Monto, c.MonedaCodigo, c.Detalle, c.VigenteHasta, c.Publicada, c.FechaPublicacion, c.Decision, c.DecididaPor, c.FechaDecision "
                + HeaderJoins + @"
                LEFT JOIN dbo.Cotizacion AS c ON c.TicketId = t.Id AND c.Descartada = 0 " + where + @"
                ORDER BY " + order + @"
                OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;"))
            {
                Bind(cmd);
                cmd.Int("@skip", (page - 1) * q.PageSize).Int("@take", q.PageSize);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var r = new Row(reader);
                    var t = ReadHeader(r);
                    if (!r.IsNull("CotizacionId")) t.Quote = ReadQuote(r, "CotizacionId");
                    items.Add(t);
                }
            }
            return new TicketPage(items, total, page, q.PageSize);
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    private static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");

    public async Task<TicketStats> GetStatsAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command("SELECT Total, Abiertos, EnCurso, PagosPendientes, UrgentesActivos FROM dbo.vw_EstadisticasPanel;");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return new TicketStats(0, 0, 0, 0, 0);
            var r = new Row(reader);
            return new TicketStats(r.Int("Total"), r.Int("Abiertos"), r.Int("EnCurso"), r.Int("PagosPendientes"), r.Int("UrgentesActivos"));
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    public async Task<AttachmentRecord?> FindAttachmentAsync(int id, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.Command(@"
                SELECT Id, Categoria, NombreOriginal, NombreAlmacenado, TipoMime, TamanoBytes, FechaSubida
                FROM dbo.Adjunto WHERE Id = @id;").Int("@id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var r = new Row(reader);
            return new AttachmentRecord
            {
                Id = r.Int("Id"), Category = r.Str("Categoria"), OriginalName = r.Str("NombreOriginal"), StoredName = r.Str("NombreAlmacenado"),
                Mime = r.Str("TipoMime"), Size = r.Int("TamanoBytes"), UploadedAt = r.Utc("FechaSubida")
            };
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    // ============================== Escritura ==============================

    public async Task<CreatedTicket> CreateAsync(NewTicket n, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                int solicitanteId;
                await using (var c = conn.Command(@"
                    INSERT INTO dbo.Solicitante (TipoSolicitante, NombreCompleto, Correo, Telefono, Carnet, CarreraId, RecintoId, Empresa, Ruc, FechaCreacion)
                    OUTPUT INSERTED.Id
                    VALUES (@tipo, @nombre, @correo, @tel, @carnet, @carrera, @recinto, @empresa, @ruc, @now);", tx)
                    .Varchar("@tipo", n.RequesterType, 10).NVarchar("@nombre", n.FullName, 120).NVarchar("@correo", n.Email, 254)
                    .Varchar("@tel", n.Phone, 20).Varchar("@carnet", n.StudentId, 15).Int("@carrera", n.MajorId).Int("@recinto", n.CampusId)
                    .NVarchar("@empresa", n.Company, 150).Varchar("@ruc", n.Ruc, 20).Utc("@now", n.Now))
                    solicitanteId = Convert.ToInt32(await c.ExecuteScalarAsync(ct));

                int ticketId;
                string code;
                // SCOPE_IDENTITY y no OUTPUT: dbo.Ticket tiene trigger (SQL Server no permite OUTPUT sin INTO en ese caso).
                await using (var c = conn.Command(@"
                    INSERT INTO dbo.Ticket (SolicitanteId, TipoServicio, UrgenciaCodigo, MetodoPagoPreferido, Descripcion,
                                            AceptoCondicionesEn, VersionCondiciones, FechaCreacion, FechaActualizacion)
                    VALUES (@sol, @tipo, @urg, @pago, @desc, @acepto, @ver, @now, @now);
                    DECLARE @id INT = CAST(SCOPE_IDENTITY() AS INT);
                    SELECT @id AS Id, Codigo FROM dbo.Ticket WHERE Id = @id;", tx)
                    .Int("@sol", solicitanteId).Varchar("@tipo", n.ServiceType, 10).Varchar("@urg", n.Urgency, 10)
                    .Varchar("@pago", n.PreferredPaymentMethod, 20).NVarchar("@desc", n.Description, 2000)
                    .Utc("@acepto", n.AcceptedTermsAt).Varchar("@ver", n.TermsVersion, 20).Utc("@now", n.Now))
                {
                    await using var reader = await c.ExecuteReaderAsync(ct);
                    await reader.ReadAsync(ct);
                    var r = new Row(reader);
                    ticketId = r.Int("Id");
                    code = r.Str("Codigo");
                }

                if (n.Hardware is { } hw)
                {
                    await using (var c = conn.Command(@"
                        INSERT INTO dbo.DetalleHardware (TicketId, TipoEquipoCodigo, Marca, Modelo, Serie, Enciende, OtrosAccesorios, AvisoRespaldoAceptado)
                        VALUES (@id, @equipo, @marca, @modelo, @serie, @enciende, @otros, 1);", tx)
                        .Int("@id", ticketId).Varchar("@equipo", hw.EquipmentType, 20).NVarchar("@marca", hw.Brand, 60).NVarchar("@modelo", hw.Model, 80)
                        .NVarchar("@serie", Db.OrNull(hw.Serial), 60).Varchar("@enciende", hw.PowersOn, 10).NVarchar("@otros", Db.OrNull(hw.AccessoriesOther), 200))
                        await c.ExecuteNonQueryAsync(ct);

                    foreach (var accessory in hw.Accessories)
                    {
                        await using var c = conn.Command("INSERT INTO dbo.TicketAccesorio (TicketId, AccesorioCodigo) VALUES (@id, @a);", tx)
                            .Int("@id", ticketId).Varchar("@a", accessory, 20);
                        await c.ExecuteNonQueryAsync(ct);
                    }
                }
                if (n.Software is { } sw)
                {
                    await using var c = conn.Command(@"
                        INSERT INTO dbo.DetalleSoftware (TicketId, TipoTrabajoCodigo, FechaDeseada, UrlReferencia)
                        VALUES (@id, @tipo, @fecha, @url);", tx)
                        .Int("@id", ticketId).Varchar("@tipo", sw.Kind, 20).DateParam("@fecha", sw.DesiredDate).NVarchar("@url", Db.OrNull(sw.ReferenceUrl), 300);
                    await c.ExecuteNonQueryAsync(ct);
                }

                foreach (var a in n.Attachments) await InsertAttachmentAsync(conn, tx, ticketId, a, ct);

                await InsertHistoryAsync(conn, tx, ticketId, new HistoryRecord
                {
                    At = n.Now, Type = Codes.Event.Created, AuthorType = Codes.Author.System, To = Codes.Status.Open, Text = "Solicitud recibida.", Internal = false
                }, ct);

                foreach (var o in n.Outbox) await InsertOutboxAsync(conn, tx, ticketId, o, n.Now, ct);

                await tx.CommitAsync(ct);
                return new CreatedTicket(ticketId, code, n.Now);
            }
            catch { await RollbackQuietly(tx); throw; }
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    public async Task ApplyAsync(TicketChangeSet c, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // 1) El ticket, con la versión esperada. Si 0 filas: alguien lo modificó antes.
                int rows;
                await using (var cmd = conn.Command(@"
                    UPDATE dbo.Ticket
                    SET EstadoCodigo = COALESCE(@estado, EstadoCodigo),
                        EstadoPagoCodigo = COALESCE(@pago, EstadoPagoCodigo),
                        AsignadoAId = CASE WHEN @cambiaAsignado = 1 THEN @asignado ELSE AsignadoAId END,
                        FechaActualizacion = @now
                    WHERE Id = @id AND VersionFila = @version;", tx)
                    .Varchar("@estado", c.NewStatus, 20).Varchar("@pago", c.NewPaymentStatus, 20)
                    .Bit("@cambiaAsignado", c.ChangeAssignee).Int("@asignado", c.NewAssigneeId)
                    .Utc("@now", c.Now).Int("@id", c.TicketId).Binary("@version", c.ExpectedVersion, 8))
                    rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows != 1) throw new ConcurrencyException();

                // 2) Cotización
                switch (c.QuoteOp)
                {
                    case QuoteOp.Replace:
                    {
                        var q = c.NewQuote ?? throw new InvalidOperationException("Falta la cotización nueva.");
                        // Primero se descarta la vigente: el índice único filtrado solo admite una con Descartada = 0.
                        await ExecAsync(conn, tx, "UPDATE dbo.Cotizacion SET Descartada = 1, FechaActualizacion = @now WHERE TicketId = @id AND Descartada = 0;",
                            ct, k => k.Utc("@now", c.Now).Int("@id", c.TicketId));
                        await ExecAsync(conn, tx, @"
                            INSERT INTO dbo.Cotizacion (TicketId, Monto, MonedaCodigo, Detalle, VigenteHasta, CreadaPorId, Publicada, FechaCreacion, FechaActualizacion)
                            VALUES (@id, @monto, @moneda, @detalle, @hasta, @por, 0, @now, @now);",
                            ct, k => k.Int("@id", c.TicketId).Money("@monto", q.Amount).Varchar("@moneda", q.Currency, 3).NVarchar("@detalle", q.Description, 500)
                                      .DateParam("@hasta", q.ValidUntil).Int("@por", c.CreatedById).Utc("@now", c.Now));
                        break;
                    }
                    case QuoteOp.Publish:
                        await ExpectOneAsync(conn, tx, @"
                            UPDATE dbo.Cotizacion SET Publicada = 1, FechaPublicacion = @now, Decision = NULL, DecididaPor = NULL, FechaDecision = NULL, FechaActualizacion = @now
                            WHERE Id = @q AND TicketId = @id AND Descartada = 0;", ct, k => k.Utc("@now", c.Now).Int("@q", c.QuoteId).Int("@id", c.TicketId));
                        break;
                    case QuoteOp.Unpublish:
                        await ExpectOneAsync(conn, tx, @"
                            UPDATE dbo.Cotizacion SET Publicada = 0, FechaPublicacion = NULL, Decision = NULL, DecididaPor = NULL, FechaDecision = NULL, FechaActualizacion = @now
                            WHERE Id = @q AND TicketId = @id AND Descartada = 0;", ct, k => k.Utc("@now", c.Now).Int("@q", c.QuoteId).Int("@id", c.TicketId));
                        break;
                    case QuoteOp.Decide:
                        await ExpectOneAsync(conn, tx, @"
                            UPDATE dbo.Cotizacion SET Decision = @d, DecididaPor = @por, FechaDecision = @now, FechaActualizacion = @now
                            WHERE Id = @q AND TicketId = @id AND Descartada = 0 AND Publicada = 1 AND Decision IS NULL;",
                            ct, k => k.Varchar("@d", c.Decision, 10).Varchar("@por", c.DecidedBy, 10).Utc("@now", c.Now).Int("@q", c.QuoteId).Int("@id", c.TicketId));
                        break;
                    case QuoteOp.Discard:
                        await ExpectOneAsync(conn, tx, "UPDATE dbo.Cotizacion SET Descartada = 1, FechaActualizacion = @now WHERE Id = @q AND TicketId = @id AND Descartada = 0;",
                            ct, k => k.Utc("@now", c.Now).Int("@q", c.QuoteId).Int("@id", c.TicketId));
                        break;
                }

                // 3) Pago
                int? proofId = null;
                switch (c.PaymentOp)
                {
                    case PaymentOp.ReportProof:
                        if (c.NewProof is not null) proofId = await InsertAttachmentAsync(conn, tx, c.TicketId, c.NewProof, ct);
                        await ExecAsync(conn, tx, @"
                            UPDATE dbo.Pago SET CotizacionId = @q, MetodoCodigo = @m, Referencia = @ref, ComprobanteAdjuntoId = COALESCE(@adj, ComprobanteAdjuntoId),
                                                FechaReporte = @now, ConfirmadoPorId = NULL, FechaConfirmacion = NULL
                            WHERE TicketId = @id;
                            IF @@ROWCOUNT = 0
                                INSERT INTO dbo.Pago (TicketId, CotizacionId, MetodoCodigo, Referencia, ComprobanteAdjuntoId, FechaReporte, FechaCreacion)
                                VALUES (@id, @q, @m, @ref, @adj, @now, @now);",
                            ct, k => k.Int("@id", c.TicketId).Int("@q", c.PaymentQuoteId).Varchar("@m", c.PaymentMethod, 20)
                                      .NVarchar("@ref", c.PaymentReference, 40).Int("@adj", proofId).Utc("@now", c.Now));
                        break;
                    case PaymentOp.Confirm:
                        await ExecAsync(conn, tx, @"
                            UPDATE dbo.Pago SET CotizacionId = @q, ConfirmadoPorId = @por, FechaConfirmacion = @now WHERE TicketId = @id;
                            IF @@ROWCOUNT = 0
                                INSERT INTO dbo.Pago (TicketId, CotizacionId, MetodoCodigo, ConfirmadoPorId, FechaConfirmacion, FechaCreacion)
                                VALUES (@id, @q, @m, @por, @now, @now);",
                            ct, k => k.Int("@id", c.TicketId).Int("@q", c.PaymentQuoteId).Varchar("@m", c.PaymentMethod, 20).Int("@por", c.ConfirmedById).Utc("@now", c.Now));
                        break;
                    case PaymentOp.Unconfirm:
                        await ExecAsync(conn, tx, "UPDATE dbo.Pago SET ConfirmadoPorId = NULL, FechaConfirmacion = NULL WHERE TicketId = @id;",
                            ct, k => k.Int("@id", c.TicketId));
                        break;
                }

                // 4) Historial y 5) correos
                foreach (var h in c.History) await InsertHistoryAsync(conn, tx, c.TicketId, h, ct);
                foreach (var o in c.Outbox) await InsertOutboxAsync(conn, tx, c.TicketId, o, c.Now, ct);

                await tx.CommitAsync(ct);
            }
            catch { await RollbackQuietly(tx); throw; }
        }
        catch (DbException ex) { throw SqlFailures.Translate(ex, _classifier, _log); }
    }

    // ============================== Apoyo ==============================

    private static async Task<int> InsertAttachmentAsync(DbConnection conn, DbTransaction tx, int ticketId, AttachmentRecord a, CancellationToken ct)
    {
        await using var cmd = conn.Command(@"
            INSERT INTO dbo.Adjunto (TicketId, Categoria, NombreOriginal, NombreAlmacenado, TipoMime, TamanoBytes, HashSha256, FechaSubida)
            OUTPUT INSERTED.Id
            VALUES (@id, @cat, @orig, @alm, @mime, @tam, @hash, @now);", tx)
            .Int("@id", ticketId).Varchar("@cat", a.Category, 20).NVarchar("@orig", a.OriginalName, 255).Varchar("@alm", a.StoredName, 100)
            .Varchar("@mime", a.Mime, 100).Int("@tam", a.Size).Binary("@hash", a.Sha256, 32).Utc("@now", a.UploadedAt);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task InsertHistoryAsync(DbConnection conn, DbTransaction tx, int ticketId, HistoryRecord h, CancellationToken ct)
    {
        await using var cmd = conn.Command(@"
            INSERT INTO dbo.HistorialTicket (TicketId, FechaEvento, TipoEvento, AutorTipo, PersonalId, EstadoAnterior, EstadoNuevo, Texto, EsInterno)
            VALUES (@id, @fecha, @tipo, @autor, @personal, @desde, @hasta, @texto, @interno);", tx)
            .Int("@id", ticketId).Utc("@fecha", h.At).Varchar("@tipo", h.Type, 20).Varchar("@autor", h.AuthorType, 10).Int("@personal", h.StaffId)
            .Varchar("@desde", h.From, 20).Varchar("@hasta", h.To, 20).NVarchar("@texto", Db.OrNull(h.Text), 1000).Bit("@interno", h.Internal);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertOutboxAsync(DbConnection conn, DbTransaction tx, int ticketId, OutboxItem o, DateTime now, CancellationToken ct)
    {
        await using var cmd = conn.Command(@"
            INSERT INTO dbo.NotificacionCorreo (TicketId, Plantilla, Destinatario, Estado, Intentos, ProximoIntentoEn, FechaCreacion)
            VALUES (@id, @plantilla, @dest, 'PENDING', 0, @now, @now);", tx)
            .Int("@id", ticketId).Varchar("@plantilla", o.Template, 40).NVarchar("@dest", o.Recipient, 254).Utc("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, Func<DbCommand, DbCommand> bind)
    {
        await using var cmd = bind(conn.Command(sql, tx));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ejecuta un UPDATE que debe afectar exactamente una fila; si no, el estado cambió bajo nuestros pies.</summary>
    private static async Task ExpectOneAsync(DbConnection conn, DbTransaction tx, string sql, CancellationToken ct, Func<DbCommand, DbCommand> bind)
    {
        await using var cmd = bind(conn.Command(sql, tx));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ConcurrencyException();
    }

    private static async Task RollbackQuietly(DbTransaction tx)
    {
        // Si un trigger ya hizo ROLLBACK (THROW 51001/51002), la transacción está cerrada y esto falla: se ignora.
        try { await tx.RollbackAsync(); } catch (Exception ex) when (ex is InvalidOperationException or DbException) { }
    }
}
