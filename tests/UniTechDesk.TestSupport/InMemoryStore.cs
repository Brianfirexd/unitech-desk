using System.Globalization;
using System.Text;
using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Domain;

namespace UniTechDesk.TestSupport;

/// <summary>Una restricción de la base (CHECK, FK, UNIQUE) que el servicio habría violado. En SQL Server sería el error 547/2627.</summary>
public sealed class ConstraintViolationException : InvalidOperationException
{
    public string Constraint { get; }
    public ConstraintViolationException(string constraint) : base("Restricción violada: " + constraint) => Constraint = constraint;
}

/// <summary>
/// "Base de datos" en memoria para probar servicios y la API sin SQL Server. Imita lo que database/01 y 02 imponen: CHECK, llaves
/// foráneas (incluida la compuesta estado/servicio y la tabla de transiciones), índice único de cotización vigente, ROWVERSION,
/// los dos triggers y la transacción única por operación. NO reemplaza probar contra SQL Server real (ver README).
/// </summary>
public sealed class InMemoryStore : ITicketRepository, IStaffRepository, ICatalogRepository, IOutboxRepository
{
    // ---------- Filas (inmutables: la "transacción" es copiar las listas y restaurarlas si algo falla) ----------
    private sealed record Sol(int Id, string Type, string Name, string Email, string Phone, string? StudentId, int? MajorId, int? CampusId, string? Company, string? Ruc);
    private sealed record Tk(int Id, int SolId, string Service, string Status, string Pay, string Urgency, string PrefMethod, string Description, int? AssigneeId,
        DateTime AcceptedAt, DateTime CreatedAt, DateTime UpdatedAt, long Version, HardwareInfo? Hw, SoftwareInfo? Sw);
    private sealed record Qt(int Id, int TicketId, decimal Amount, string Currency, string Detail, DateOnly? ValidUntil, int CreatedById, bool Published,
        DateTime? PublishedAt, string? Decision, string? DecidedBy, DateTime? DecidedAt, bool Discarded);
    private sealed record Pg(int Id, int TicketId, int QuoteId, string Method, string? Reference, int? ProofId, DateTime? ReportedAt, int? ConfirmedById, DateTime? ConfirmedAt);
    private sealed record Att(int Id, int TicketId, AttachmentRecord Rec);
    private sealed record Hi(long Id, int TicketId, HistoryRecord Rec);
    private sealed record Ml(long Id, int TicketId, string Template, string Recipient, string State, int Attempts, string? LastError, DateTime Next, DateTime? SentAt);
    private sealed record St(int Id, string Username, string FullName, string? Email, string Hash, string Role, bool Active, int Failed, DateTime? LockedUntil, DateTime? LastAccess);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private List<Sol> _sols = new(); private List<Tk> _tks = new(); private List<Qt> _qts = new(); private List<Pg> _pgs = new();
    private List<Att> _atts = new(); private List<Hi> _his = new(); private List<Ml> _mls = new(); private List<St> _sts = new();
    private int _solSeq, _tkSeq = 1000, _qtSeq, _pgSeq, _attSeq, _stSeq;
    private long _hiSeq, _mlSeq, _version;

    public InMemoryStore(TimeProvider time) => _time = time;

    // Catálogos (los mismos del front / 03_catalogos.sql)
    public List<CatalogItem> Majors { get; } = new()
    {
        new(1, "Ingeniería en Sistemas"), new(2, "Ingeniería Química"), new(3, "Ingeniería en Computación"), new(4, "Ingeniería en Telecomunicaciones"),
        new(5, "Ingeniería Industrial"), new(6, "Ingeniería Civil"), new(7, "Ingeniería Electrónica"), new(8, "Arquitectura")
    };
    public List<CatalogItem> Campuses { get; } = new() { new(1, "RUSB"), new(2, "RUPAP"), new(3, "CUR ESTELI") };

    /// <summary>Cuántas veces se produjo un ConcurrencyException por la versión (para pruebas de reintento).</summary>
    public int ConcurrencyConflicts { get; private set; }
    /// <summary>Si es mayor que 0, las próximas N llamadas a ApplyAsync/CreateAsync fallan como un interbloqueo (error 1205).</summary>
    public int FailNextWithDeadlock { get; set; }
    /// <summary>Si es N &gt; 0, la lectura FindByIdAsync número N (contando desde ahora) falla una vez como un interbloqueo. Sirve para simular un fallo AL RELEER un ticket ya confirmado.</summary>
    public int DeadlockOnReadNumber { get; set; }

    // ====================================================== Personal ======================================================
    public int AddStaff(string username, string fullName, string role, string passwordHash, bool active = true, string? email = null)
    {
        lock (_gate)
        {
            Check(!_sts.Any(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase)), "UQ_Personal_NombreUsuario");
            Check(username.Length >= 3, "CK_Personal_NombreUsuario");
            Check(role is "ADMIN" or "TECNICO" or "DESARROLLADOR", "FK_Personal_Rol");
            var id = ++_stSeq;
            _sts.Add(new St(id, username, fullName, email, passwordHash, role, active, 0, null, null));
            return id;
        }
    }

    public int FailedAttempts(string username) { lock (_gate) return _sts.First(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase)).Failed; }
    public void SetActive(int id, bool active) { lock (_gate) Replace(_sts, s => s.Id == id, s => s with { Active = active }); }

    private static StaffAccount ToAccount(St s) => new()
    {
        Id = s.Id, Username = s.Username, FullName = s.FullName, Email = s.Email, PasswordHash = s.Hash, Role = s.Role, Active = s.Active, LockedUntil = s.LockedUntil
    };

    public Task<StaffAccount?> FindByUsernameAsync(string username, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_sts.Where(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase)).Select(ToAccount).FirstOrDefault());
    }

    // Implementación explícita: IStaffRepository.FindByIdAsync tiene la misma firma que ITicketRepository.FindByIdAsync
    // (en producción son clases distintas, aquí comparten almacén).
    Task<StaffAccount?> IStaffRepository.FindByIdAsync(int id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_sts.Where(s => s.Id == id).Select(ToAccount).FirstOrDefault());
    }

    public Task<IReadOnlyList<StaffRef>> ListActiveAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<StaffRef>>(_sts.Where(s => s.Active).OrderBy(s => s.FullName).ThenBy(s => s.Id).Select(s => new StaffRef { Id = s.Id, Name = s.FullName }).ToList());
    }

    public Task RegisterFailedLoginAsync(int id, DateTime now, int maxAttempts, DateTime lockUntil, CancellationToken ct)
    {
        lock (_gate)
            Replace(_sts, s => s.Id == id, s =>
            {
                var expired = s.LockedUntil is { } l && l <= now;
                var attempts = expired ? 1 : Math.Min(s.Failed + 1, 250);
                var locked = attempts >= maxAttempts ? lockUntil : expired ? (DateTime?)null : s.LockedUntil;
                return s with { Failed = attempts, LockedUntil = locked };
            });
        return Task.CompletedTask;
    }

    public Task RegisterSuccessfulLoginAsync(int id, DateTime now, CancellationToken ct)
    {
        lock (_gate) Replace(_sts, s => s.Id == id, s => s with { Failed = 0, LockedUntil = null, LastAccess = now });
        return Task.CompletedTask;
    }

    // ====================================================== Catálogos ======================================================
    public Task<Catalogs> GetCatalogsAsync(CancellationToken ct) => Task.FromResult(new Catalogs(Majors.ToList(), Campuses.ToList()));

    // ====================================================== Lectura de tickets ======================================================
    private TicketRecord Build(Tk t, bool detail)
    {
        var s = _sols.First(x => x.Id == t.SolId);
        var r = new TicketRecord
        {
            Id = t.Id, Code = "UTD-" + t.Id, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt, RowVersion = BitConverter.GetBytes(t.Version).Reverse().ToArray(),
            RequesterType = s.Type, FullName = s.Name, Email = s.Email, Phone = s.Phone, StudentId = s.StudentId ?? "", MajorId = s.MajorId,
            MajorName = Majors.FirstOrDefault(m => m.Id == s.MajorId)?.Name ?? "", CampusId = s.CampusId, CampusName = Campuses.FirstOrDefault(c => c.Id == s.CampusId)?.Name ?? "",
            Company = s.Company ?? "", Ruc = s.Ruc ?? "", ServiceType = t.Service, Description = t.Description, Urgency = t.Urgency, PreferredPaymentMethod = t.PrefMethod,
            Status = t.Status, PaymentStatus = t.Pay,
            Hardware = t.Hw is null ? null : new HardwareInfo { EquipmentType = t.Hw.EquipmentType, Brand = t.Hw.Brand, Model = t.Hw.Model, Serial = t.Hw.Serial, PowersOn = t.Hw.PowersOn, AccessoriesOther = t.Hw.AccessoriesOther, Accessories = t.Hw.Accessories.OrderBy(a => a, StringComparer.Ordinal).ToList() },
            Software = t.Sw is null ? null : new SoftwareInfo { Kind = t.Sw.Kind, DesiredDate = t.Sw.DesiredDate, ReferenceUrl = t.Sw.ReferenceUrl }
        };
        if (t.AssigneeId is { } a) r.Assignee = new StaffRef { Id = a, Name = _sts.First(x => x.Id == a).FullName };
        var q = _qts.FirstOrDefault(x => x.TicketId == t.Id && !x.Discarded);
        if (q is not null)
            r.Quote = new QuoteRecord { Id = q.Id, Amount = q.Amount, Currency = q.Currency, Description = q.Detail, ValidUntil = q.ValidUntil, Published = q.Published, PublishedAt = q.PublishedAt, Decision = q.Decision, DecidedBy = q.DecidedBy, DecidedAt = q.DecidedAt };
        if (!detail) return r;
        var p = _pgs.FirstOrDefault(x => x.TicketId == t.Id);
        if (p is not null)
            r.Payment = new PaymentRecord { Id = p.Id, QuoteId = p.QuoteId, MethodCode = p.Method, Reference = p.Reference, ProofAttachmentId = p.ProofId, ProofName = _atts.FirstOrDefault(x => x.Id == p.ProofId)?.Rec.OriginalName, ReportedAt = p.ReportedAt, ConfirmedById = p.ConfirmedById, ConfirmedAt = p.ConfirmedAt };
        r.Attachments = _atts.Where(x => x.TicketId == t.Id).OrderBy(x => x.Id).Select(x => Copy(x.Rec, x.Id)).ToList();
        r.History = _his.Where(x => x.TicketId == t.Id).OrderBy(x => x.Rec.At).ThenBy(x => x.Id).Select(x => new HistoryRecord
        {
            Id = x.Id, At = x.Rec.At, AuthorType = x.Rec.AuthorType, StaffId = x.Rec.StaffId, Type = x.Rec.Type, From = x.Rec.From, To = x.Rec.To, Text = x.Rec.Text ?? "", Internal = x.Rec.Internal,
            AuthorName = x.Rec.AuthorType switch { Codes.Author.Staff => _sts.First(z => z.Id == x.Rec.StaffId).FullName, Codes.Author.Client => "Cliente", _ => "Sistema" }
        }).ToList();
        return r;
    }

    private static AttachmentRecord Copy(AttachmentRecord a, int id) => new() { Id = id, Category = a.Category, OriginalName = a.OriginalName, StoredName = a.StoredName, Mime = a.Mime, Size = a.Size, Sha256 = a.Sha256, UploadedAt = a.UploadedAt };

    public Task<TicketRecord?> FindByIdAsync(int id, CancellationToken ct)
    {
        if (DeadlockOnReadNumber > 0 && --DeadlockOnReadNumber == 0) throw new TransientStoreException("Interbloqueo simulado al leer (1205).");
        lock (_gate) return Task.FromResult(_tks.Where(t => t.Id == id).Select(t => Build(t, true)).FirstOrDefault());
    }

    public Task<TicketRecord?> FindByCodeAndEmailAsync(string code, string email, CancellationToken ct)
    {
        lock (_gate)
        {
            var t = _tks.FirstOrDefault(x => ("UTD-" + x.Id).Equals(code, StringComparison.OrdinalIgnoreCase) && Fold(_sols.First(s => s.Id == x.SolId).Email) == Fold(email));
            return Task.FromResult(t is null ? null : Build(t, true));
        }
    }

    public Task<AttachmentRecord?> FindAttachmentAsync(int id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_atts.Where(a => a.Id == id).Select(a => Copy(a.Rec, a.Id)).FirstOrDefault());
    }

    /// <summary>Comparación sin mayúsculas ni tildes (como la collation Modern_Spanish_100_CI_AI de la BD).</summary>
    private static string Fold(string s) =>
        new string(s.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();

    public Task<TicketPage> ListAsync(TicketQuery q, CancellationToken ct)
    {
        lock (_gate)
        {
            IEnumerable<Tk> rows = _tks;
            if (q.Status is not null) rows = rows.Where(t => t.Status == q.Status);
            if (q.Service is not null) rows = rows.Where(t => t.Service == q.Service);
            if (q.Urgency is not null) rows = rows.Where(t => t.Urgency == q.Urgency);
            if (q.Payment is not null) rows = rows.Where(t => t.Pay == q.Payment);
            if (q.Text is not null)
            {
                var needle = Fold(q.Text);
                rows = rows.Where(t =>
                {
                    var s = _sols.First(x => x.Id == t.SolId);
                    var hay = string.Join("\u0001", "UTD-" + t.Id, s.Name, s.Email, s.StudentId, s.Ruc, s.Company, t.Description, t.Hw is null ? "" : t.Hw.Brand + " " + t.Hw.Model);
                    return Fold(hay).Contains(needle, StringComparison.Ordinal);
                });
            }
            var rank = (Tk t) => WorkflowCatalog.UrgencyRank[t.Urgency];
            var ordered = q.Sort switch
            {
                "date_asc" => rows.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
                "urgency_desc" => rows.OrderByDescending(rank).ThenByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id),
                _ => rows.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            };
            var all = ordered.ToList();
            var pages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)q.PageSize));
            var page = Math.Min(Math.Max(q.Page, 1), pages);
            var items = all.Skip((page - 1) * q.PageSize).Take(q.PageSize).Select(t => Build(t, false)).ToList();
            return Task.FromResult(new TicketPage(items, all.Count, page, q.PageSize));
        }
    }

    public Task<TicketStats> GetStatsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            bool Active(Tk t) => !WorkflowCatalog.Statuses[t.Status].IsFinal;
            return Task.FromResult(new TicketStats(_tks.Count, _tks.Count(t => t.Status == Codes.Status.Open), _tks.Count(Active),
                _tks.Count(t => t.Pay is Codes.PaymentStatus.Pending or Codes.PaymentStatus.ProofSent), _tks.Count(t => t.Urgency == "HIGH" && Active(t))));
        }
    }

    // ====================================================== Escritura ======================================================
    private (List<Sol>, List<Tk>, List<Qt>, List<Pg>, List<Att>, List<Hi>, List<Ml>, int, int, int, int, long, long) Snapshot() =>
        (_sols.ToList(), _tks.ToList(), _qts.ToList(), _pgs.ToList(), _atts.ToList(), _his.ToList(), _mls.ToList(), _solSeq, _tkSeq, _qtSeq, _pgSeq, _hiSeq, _mlSeq);

    private void Restore((List<Sol>, List<Tk>, List<Qt>, List<Pg>, List<Att>, List<Hi>, List<Ml>, int, int, int, int, long, long) s)
    {
        (_sols, _tks, _qts, _pgs, _atts, _his, _mls, _solSeq, _tkSeq, _qtSeq, _pgSeq, _hiSeq, _mlSeq) = s;
    }

    private void MaybeDeadlock()
    {
        if (FailNextWithDeadlock > 0) { FailNextWithDeadlock--; throw new TransientStoreException("Interbloqueo simulado (1205)."); }
    }

    public Task<CreatedTicket> CreateAsync(NewTicket n, CancellationToken ct)
    {
        lock (_gate)
        {
            MaybeDeadlock();
            var snap = Snapshot();
            try
            {
                // Solicitante
                MaxLen(n.FullName, 120, "Solicitante.NombreCompleto"); MaxLen(n.Email, 254, "Solicitante.Correo"); MaxLen(n.Phone, 20, "Solicitante.Telefono");
                Check(n.RequesterType is "STUDENT" or "EXTERNAL", "CK_Solicitante_Tipo");
                Check(n.Email.Length >= 5 && n.Email.Contains('@') && !n.Email.Contains(' ') && n.Email.IndexOf('.', n.Email.IndexOf('@')) > n.Email.IndexOf('@') + 1, "CK_Solicitante_Correo");
                Check(n.Phone.Length >= 7, "CK_Solicitante_Telefono");
                var student = n.RequesterType == "STUDENT";
                Check(student
                    ? n.StudentId != null && n.MajorId != null && n.CampusId != null && n.Company == null && n.Ruc == null
                    : n.Company != null && n.Ruc != null && n.StudentId == null && n.MajorId == null && n.CampusId == null, "CK_Solicitante_DatosPorTipo");
                if (n.StudentId != null) MaxLen(n.StudentId, 15, "Solicitante.Carnet");
                if (n.Ruc != null) MaxLen(n.Ruc, 20, "Solicitante.Ruc");
                if (n.Company != null) MaxLen(n.Company, 150, "Solicitante.Empresa");
                if (n.MajorId != null) Check(Majors.Any(m => m.Id == n.MajorId), "FK_Solicitante_Carrera");
                if (n.CampusId != null) Check(Campuses.Any(m => m.Id == n.CampusId), "FK_Solicitante_Recinto");
                var sol = new Sol(++_solSeq, n.RequesterType, n.FullName, n.Email, n.Phone, n.StudentId, n.MajorId, n.CampusId, n.Company, n.Ruc);
                _sols.Add(sol);

                // Ticket
                Check(n.ServiceType is "HARDWARE" or "SOFTWARE", "CK_Ticket_TipoServicio");
                Check(n.Description.Trim().Length >= 15, "CK_Ticket_Descripcion"); MaxLen(n.Description, 2000, "Ticket.Descripcion");
                Check(WorkflowCatalog.Urgencies.ContainsKey(n.Urgency), "FK_Ticket_Urgencia");
                Check(WorkflowCatalog.PaymentMethods.ContainsKey(n.PreferredPaymentMethod), "FK_Ticket_MetodoPago");
                MaxLen(n.TermsVersion, 20, "Ticket.VersionCondiciones");
                var tk = new Tk(++_tkSeq, sol.Id, n.ServiceType, Codes.Status.Open, Codes.PaymentStatus.None, n.Urgency, n.PreferredPaymentMethod, n.Description, null,
                    n.AcceptedTermsAt, n.Now, n.Now, ++_version, null, null);

                // Subtipos (llave compuesta con TipoServicio)
                if (n.Hardware is { } hw)
                {
                    Check(n.ServiceType == "HARDWARE", "FK_DetalleHardware_Ticket");
                    Check(WorkflowCatalog.EquipmentTypes.ContainsKey(hw.EquipmentType), "FK_DetalleHardware_TipoEquipo");
                    Check(hw.PowersOn is "YES" or "NO" or "SOMETIMES", "CK_DetalleHardware_Enciende");
                    MaxLen(hw.Brand, 60, "DetalleHardware.Marca"); MaxLen(hw.Model, 80, "DetalleHardware.Modelo"); MaxLen(hw.Serial, 60, "DetalleHardware.Serie"); MaxLen(hw.AccessoriesOther, 200, "DetalleHardware.OtrosAccesorios");
                    Check(hw.Brand.Length > 0 && hw.Model.Length > 0, "NOT NULL Marca/Modelo");
                    foreach (var a in hw.Accessories) Check(WorkflowCatalog.Accessories.ContainsKey(a), "FK_TicketAccesorio_Accesorio");
                    Check(hw.Accessories.Distinct().Count() == hw.Accessories.Count, "PK_TicketAccesorio");
                    tk = tk with { Hw = hw };
                }
                if (n.Software is { } sw)
                {
                    Check(n.ServiceType == "SOFTWARE", "FK_DetalleSoftware_Ticket");
                    Check(WorkflowCatalog.SoftwareKinds.ContainsKey(sw.Kind), "FK_DetalleSoftware_Tipo");
                    Check(string.IsNullOrEmpty(sw.ReferenceUrl) || sw.ReferenceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || sw.ReferenceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase), "CK_DetalleSoftware_Url");
                    MaxLen(sw.ReferenceUrl, 300, "DetalleSoftware.UrlReferencia");
                    tk = tk with { Sw = sw };
                }
                Check((n.ServiceType == "HARDWARE") == (tk.Hw != null) || n.ServiceType != "HARDWARE", "FK_DetalleHardware_Ticket");
                _tks.Add(tk);
                RunTicketTrigger(tk);

                foreach (var a in n.Attachments) AddAttachment(tk.Id, a);
                AddHistory(tk.Id, new HistoryRecord { At = n.Now, Type = Codes.Event.Created, AuthorType = Codes.Author.System, To = Codes.Status.Open, Text = "Solicitud recibida.", Internal = false });
                foreach (var o in n.Outbox) AddMail(tk.Id, o, n.Now);
                return Task.FromResult(new CreatedTicket(tk.Id, "UTD-" + tk.Id, n.Now));
            }
            catch { Restore(snap); throw; }
        }
    }

    public Task ApplyAsync(TicketChangeSet c, CancellationToken ct)
    {
        lock (_gate)
        {
            MaybeDeadlock();
            var snap = Snapshot();
            try
            {
                var current = _tks.FirstOrDefault(t => t.Id == c.TicketId);
                var expected = BitConverter.ToInt64(c.ExpectedVersion.Reverse().ToArray(), 0);
                if (current is null || current.Version != expected) { ConcurrencyConflicts++; throw new ConcurrencyException(); }

                var updated = current with
                {
                    Status = c.NewStatus ?? current.Status, Pay = c.NewPaymentStatus ?? current.Pay,
                    AssigneeId = c.ChangeAssignee ? c.NewAssigneeId : current.AssigneeId, UpdatedAt = c.Now, Version = ++_version
                };
                Check(WorkflowCatalog.Statuses.TryGetValue(updated.Status, out var info) && info.Services.Contains(updated.Service), "FK_Ticket_EstadoServicio");
                Check(WorkflowCatalog.PaymentStatuses.ContainsKey(updated.Pay), "FK_Ticket_EstadoPago");
                if (updated.AssigneeId is { } who) Check(_sts.Any(s => s.Id == who), "FK_Ticket_Asignado");
                Replace(_tks, t => t.Id == c.TicketId, _ => updated);
                RunTicketTrigger(updated);

                switch (c.QuoteOp)
                {
                    case QuoteOp.Replace:
                    {
                        var q = c.NewQuote!;
                        Replace(_qts, x => x.TicketId == c.TicketId && !x.Discarded, x => x with { Discarded = true });
                        Check(q.Amount is >= 0m and <= 999999999.99m, "CK_Cotizacion_Monto");
                        Check(decimal.Round(q.Amount, 2) == q.Amount, "DECIMAL(11,2) scale");
                        Check(q.Description.Trim().Length >= 5, "CK_Cotizacion_Detalle"); MaxLen(q.Description, 500, "Cotizacion.Detalle");
                        Check(WorkflowCatalog.Currencies.ContainsKey(q.Currency), "FK_Cotizacion_Moneda");
                        Check(_sts.Any(s => s.Id == c.CreatedById), "FK_Cotizacion_Creador");
                        Check(!_qts.Any(x => x.TicketId == c.TicketId && !x.Discarded), "UX_Cotizacion_Vigente");
                        _qts.Add(new Qt(++_qtSeq, c.TicketId, q.Amount, q.Currency, q.Description, q.ValidUntil, c.CreatedById, false, null, null, null, null, false));
                        break;
                    }
                    case QuoteOp.Publish:
                        ExpectOne(_qts, x => x.Id == c.QuoteId && x.TicketId == c.TicketId && !x.Discarded, x => x with { Published = true, PublishedAt = c.Now, Decision = null, DecidedBy = null, DecidedAt = null });
                        break;
                    case QuoteOp.Unpublish:
                        ExpectOne(_qts, x => x.Id == c.QuoteId && x.TicketId == c.TicketId && !x.Discarded, x => x with { Published = false, PublishedAt = null, Decision = null, DecidedBy = null, DecidedAt = null });
                        break;
                    case QuoteOp.Decide:
                        Check(c.Decision is "APPROVED" or "REJECTED", "CK_Cotizacion_Decision"); Check(c.DecidedBy is "CLIENT" or "STAFF", "CK_Cotizacion_DecididaPor");
                        ExpectOne(_qts, x => x.Id == c.QuoteId && x.TicketId == c.TicketId && !x.Discarded && x.Published && x.Decision == null, x => x with { Decision = c.Decision, DecidedBy = c.DecidedBy, DecidedAt = c.Now });
                        break;
                    case QuoteOp.Discard:
                        ExpectOne(_qts, x => x.Id == c.QuoteId && x.TicketId == c.TicketId && !x.Discarded, x => x with { Discarded = true });
                        break;
                }
                foreach (var q in _qts)
                {
                    Check((!q.Published && q.PublishedAt == null && q.Decision == null) || (q.Published && q.PublishedAt != null), "CK_Cotizacion_Publicacion");
                    Check((q.Decision == null && q.DecidedBy == null && q.DecidedAt == null) || (q.Decision != null && q.DecidedBy != null && q.DecidedAt != null), "CK_Cotizacion_DecisionCompleta");
                }

                int? proofId = null;
                switch (c.PaymentOp)
                {
                    case PaymentOp.ReportProof:
                        if (c.NewProof is not null) proofId = AddAttachment(c.TicketId, c.NewProof);
                        UpsertPayment(c, p => p with { QuoteId = c.PaymentQuoteId!.Value, Method = c.PaymentMethod!, Reference = c.PaymentReference, ProofId = proofId ?? p.ProofId, ReportedAt = c.Now, ConfirmedById = null, ConfirmedAt = null },
                            () => new Pg(++_pgSeq, c.TicketId, c.PaymentQuoteId!.Value, c.PaymentMethod!, c.PaymentReference, proofId, c.Now, null, null));
                        break;
                    case PaymentOp.Confirm:
                        UpsertPayment(c, p => p with { QuoteId = c.PaymentQuoteId!.Value, ConfirmedById = c.ConfirmedById, ConfirmedAt = c.Now },
                            () => new Pg(++_pgSeq, c.TicketId, c.PaymentQuoteId!.Value, c.PaymentMethod!, null, null, null, c.ConfirmedById, c.Now));
                        break;
                    case PaymentOp.Unconfirm:
                        Replace(_pgs, p => p.TicketId == c.TicketId, p => p with { ConfirmedById = null, ConfirmedAt = null });
                        break;
                }
                foreach (var p in _pgs)
                {
                    Check(p.ConfirmedById.HasValue == p.ConfirmedAt.HasValue, "CK_Pago_Confirmacion");
                    Check(_qts.Any(q => q.Id == p.QuoteId && q.TicketId == p.TicketId), "FK_Pago_Cotizacion");
                    Check(WorkflowCatalog.PaymentMethods.ContainsKey(p.Method), "FK_Pago_Metodo");
                    if (p.Reference != null) MaxLen(p.Reference, 40, "Pago.Referencia");
                    if (p.ProofId != null) Check(_atts.Any(a => a.Id == p.ProofId && a.TicketId == p.TicketId), "FK_Pago_Comprobante");
                }

                foreach (var h in c.History) AddHistory(c.TicketId, h);
                foreach (var o in c.Outbox) AddMail(c.TicketId, o, c.Now);
                return Task.CompletedTask;
            }
            catch { Restore(snap); throw; }
        }
    }

    private void UpsertPayment(TicketChangeSet c, Func<Pg, Pg> update, Func<Pg> insert)
    {
        if (_pgs.Any(p => p.TicketId == c.TicketId)) Replace(_pgs, p => p.TicketId == c.TicketId, update);
        else _pgs.Add(insert());
    }

    /// <summary>Los dos triggers de 02_indices_y_reglas.sql (error 51001 y 51002), con el mismo efecto para la API.</summary>
    private void RunTicketTrigger(Tk t)
    {
        var approved = _qts.Any(q => q.TicketId == t.Id && !q.Discarded && q.Decision == "APPROVED");
        if (Workflow.IsWorkStatus(t.Status) && !approved)
            throw AppException.Conflict("El trabajo solo puede iniciar cuando el cliente aprueba la cotización.");
        var approvedWithAmount = _qts.Any(q => q.TicketId == t.Id && !q.Discarded && q.Decision == "APPROVED" && q.Amount > 0);
        if (t.Status == "DELIVERED" && approvedWithAmount && t.Pay != "PAID")
            throw AppException.Conflict("No se puede entregar: la cotización aprobada tiene el pago pendiente.");
    }

    private int AddAttachment(int ticketId, AttachmentRecord a)
    {
        Check(a.Category is "SOLICITUD" or "COMPROBANTE_PAGO", "CK_Adjunto_Categoria");
        Check(a.Mime is "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "application/pdf", "CK_Adjunto_Mime");
        Check(a.Size is >= 1 and <= 26214400, "CK_Adjunto_Tamano");
        Check(!_atts.Any(x => x.Rec.StoredName == a.StoredName), "UQ_Adjunto_NombreAlmacenado");
        MaxLen(a.OriginalName, 255, "Adjunto.NombreOriginal"); MaxLen(a.StoredName, 100, "Adjunto.NombreAlmacenado");
        if (a.Sha256 != null) Check(a.Sha256.Length == 32, "BINARY(32)");
        var id = ++_attSeq;
        _atts.Add(new Att(id, ticketId, Copy(a, id)));
        return id;
    }

    private void AddHistory(int ticketId, HistoryRecord h)
    {
        var text = (h.Text ?? "").Trim();
        Check(h.Type is "CREATED" or "STATUS" or "QUOTE" or "QUOTE_DECISION" or "PAYMENT" or "ASSIGN" or "NOTE", "CK_Historial_TipoEvento");
        Check(h.AuthorType is "SYSTEM" or "CLIENT" or "STAFF", "CK_Historial_AutorTipo");
        Check((h.AuthorType == "STAFF" && h.StaffId != null) || (h.AuthorType != "STAFF" && h.StaffId == null), "CK_Historial_Autor");
        if (h.StaffId != null) Check(_sts.Any(s => s.Id == h.StaffId), "FK_Historial_Personal");
        Check((h.Type == "CREATED" && h.From == null && h.To == "OPEN") || (h.Type == "STATUS" && h.From != null && h.To != null) || (h.Type is not ("CREATED" or "STATUS") && h.From == null && h.To == null), "CK_Historial_Estados");
        Check(!(h.Type == "STATUS" && h.To is "CANCELLED" or "UNREPAIRABLE" && text.Length < 5), "CK_Historial_MotivoObligatorio");
        Check(h.Type != "NOTE" || (h.Internal && text.Length >= 2), "CK_Historial_Nota");
        if (h.To != null) Check(WorkflowCatalog.Statuses.ContainsKey(h.To), "FK_Historial_EstadoNuevo");
        if (h.From != null && h.To != null) Check(WorkflowCatalog.Transitions.TryGetValue(h.From, out var allowed) && allowed.Contains(h.To), "FK_Historial_Transicion");
        MaxLen(h.Text ?? "", 1000, "Historial.Texto");
        _his.Add(new Hi(++_hiSeq, ticketId, h));
    }

    private void AddMail(int ticketId, OutboxItem o, DateTime now)
    {
        MaxLen(o.Template, 40, "Notificacion.Plantilla"); MaxLen(o.Recipient, 254, "Notificacion.Destinatario");
        _mls.Add(new Ml(++_mlSeq, ticketId, o.Template, o.Recipient, "PENDING", 0, null, now, null));
    }

    // ====================================================== Bandeja de salida ======================================================
    public Task<IReadOnlyList<OutboxItem>> ClaimDueAsync(DateTime now, int batchSize, TimeSpan lease, CancellationToken ct)
    {
        lock (_gate)
        {
            var due = _mls.Where(m => m.State == "PENDING" && m.Next <= now).OrderBy(m => m.Next).ThenBy(m => m.Id).Take(batchSize).ToList();
            var result = new List<OutboxItem>();
            foreach (var m in due)
            {
                var upd = m with { Attempts = Math.Min(m.Attempts + 1, 250), Next = now + lease };
                Replace(_mls, x => x.Id == m.Id, _ => upd);
                result.Add(new OutboxItem { Id = m.Id, TicketId = m.TicketId, Template = m.Template, Recipient = m.Recipient, Attempts = upd.Attempts });
            }
            return Task.FromResult<IReadOnlyList<OutboxItem>>(result);
        }
    }

    public Task MarkSentAsync(long id, DateTime now, CancellationToken ct)
    {
        lock (_gate) Replace(_mls, m => m.Id == id, m => m with { State = "SENT", SentAt = now, LastError = null });
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(long id, string error, DateTime nextAttempt, bool giveUp, CancellationToken ct)
    {
        lock (_gate)
        {
            MaxLen(error, 500, "Notificacion.UltimoError");
            Replace(_mls, m => m.Id == id, m => m with { State = giveUp ? "FAILED" : "PENDING", LastError = error, Next = nextAttempt });
        }
        return Task.CompletedTask;
    }

    // ====================================================== Apoyo para pruebas ======================================================
    public IReadOnlyList<(string Template, string Recipient, string State, int Attempts, string? LastError)> Mails()
    {
        lock (_gate) return _mls.Select(m => (m.Template, m.Recipient, m.State, m.Attempts, m.LastError)).ToList();
    }

    public int TicketCount { get { lock (_gate) return _tks.Count; } }
    public int QuoteVersions(int ticketId) { lock (_gate) return _qts.Count(q => q.TicketId == ticketId); }
    public bool HasPayment(int ticketId) { lock (_gate) return _pgs.Any(p => p.TicketId == ticketId); }

    /// <summary>Los 3 tickets de demostración (los mismos de la mock-api del front y de 06_datos_demo.sql).</summary>
    public async Task SeedDemoAsync(Func<string, string> hasher, string demoPassword)
    {
        var hash = hasher(demoPassword);
        var tec1 = AddStaff("tecnico1", "Técnico 1", "TECNICO", hash);
        AddStaff("tecnico2", "Técnico 2", "TECNICO", hash);
        AddStaff("desarrollador1", "Desarrollador 1", "DESARROLLADOR", hash);
        var adm = AddStaff("admin", "Administración", "ADMIN", hash);
        var now = _time.GetUtcNow().UtcDateTime;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        var t1 = await CreateAsync(new NewTicket
        {
            RequesterType = "STUDENT", FullName = "María García", Email = "maria.garcia@example.com", Phone = "8888-1111", StudentId = "20230501", MajorId = 8, CampusId = 1,
            ServiceType = "SOFTWARE", Software = new SoftwareInfo { Kind = "INSTALL_SUPPORT" },
            Description = "Necesito ayuda instalando y configurando AutoCAD 2026 en mi nueva laptop. Me da un error de licencia de red.",
            Urgency = "HIGH", PreferredPaymentMethod = "TRANSFER", AcceptedTermsAt = now.AddDays(-5), Now = now.AddDays(-5)
        }, default);
        var t2 = await CreateAsync(new NewTicket
        {
            RequesterType = "EXTERNAL", FullName = "Carlos Ruiz", Email = "cruiz@empresaabc.example", Phone = "2222-3333", Company = "Empresa ABC S.A.", Ruc = "J0310000012345",
            ServiceType = "HARDWARE", Hardware = new HardwareInfo { EquipmentType = "DESKTOP", Brand = "Dell", Model = "OptiPlex 3080", PowersOn = "NO", Accessories = { "CHARGER" } },
            Description = "La computadora del área de recepción no enciende. Hace unos pitidos constantes.",
            Urgency = "MEDIUM", PreferredPaymentMethod = "CASH", AcceptedTermsAt = now.AddDays(-2), Now = now.AddDays(-2)
        }, default);
        var t3 = await CreateAsync(new NewTicket
        {
            RequesterType = "STUDENT", FullName = "Luis Mendoza", Email = "luis.mendoza@example.com", Phone = "7777-2222", StudentId = "20221877", MajorId = 1, CampusId = 2,
            ServiceType = "HARDWARE", Hardware = new HardwareInfo { EquipmentType = "LAPTOP", Brand = "Lenovo", Model = "IdeaPad 3", PowersOn = "SOMETIMES", Accessories = { "CHARGER", "BAG" } },
            Description = "La laptop se apaga sola a los pocos minutos y el ventilador hace mucho ruido. Creo que necesita limpieza o cambio de pasta térmica.",
            Urgency = "LOW", PreferredPaymentMethod = "TRANSFER", AcceptedTermsAt = now.AddDays(-3), Now = now.AddDays(-3)
        }, default);

        TicketChangeSet Next(int id, DateTime at)
        {
            var rec = FindByIdAsync(id, default).Result!;
            return new TicketChangeSet { TicketId = id, ExpectedVersion = rec.RowVersion, Now = at };
        }
        static HistoryRecord Staff(StaffRef who, DateTime at, string type, string text, bool inner, string? from = null, string? to = null) =>
            new() { At = at, AuthorType = "STAFF", StaffId = who.Id, AuthorName = who.Name, Type = type, Text = text, Internal = inner, From = from, To = to };
        var admin = new StaffRef { Id = adm, Name = "Administración" };
        var tec = new StaffRef { Id = tec1, Name = "Técnico 1" };

        var c = Next(t1.Id, now.AddDays(-4)); c.NewStatus = "IN_REVIEW"; c.History.Add(Staff(admin, now.AddDays(-4), "STATUS", "Estamos revisando tu solicitud.", false, "OPEN", "IN_REVIEW"));
        c.ChangeAssignee = true; c.NewAssigneeId = _sts.First(s => s.Username == "desarrollador1").Id; await ApplyAsync(c, default);

        c = Next(t3.Id, now.AddDays(-2)); c.NewStatus = "IN_REVIEW"; c.ChangeAssignee = true; c.NewAssigneeId = tec1;
        c.History.Add(Staff(tec, now.AddDays(-2), "STATUS", "Recibimos tu equipo y lo estamos diagnosticando.", false, "OPEN", "IN_REVIEW")); await ApplyAsync(c, default);
        c = Next(t3.Id, now.AddDays(-1)); c.QuoteOp = QuoteOp.Replace; c.CreatedById = tec1;
        c.NewQuote = new QuoteRecord { Amount = 850m, Currency = "NIO", Description = "Limpieza interna, cambio de pasta térmica y revisión del ventilador." };
        c.History.Add(Staff(tec, now.AddDays(-1), "QUOTE", "Cotización guardada: C$ 850.00.", true)); await ApplyAsync(c, default);
        c = Next(t3.Id, now.AddDays(-1).AddMinutes(1)); c.NewStatus = "QUOTED"; c.History.Add(Staff(tec, now.AddDays(-1).AddMinutes(1), "STATUS", "", true, "IN_REVIEW", "QUOTED")); await ApplyAsync(c, default);
        var q = (await FindByIdAsync(t3.Id, default))!.Quote!;
        c = Next(t3.Id, now.AddDays(-1).AddHours(1)); c.NewStatus = "AWAITING_APPROVAL"; c.QuoteOp = QuoteOp.Publish; c.QuoteId = q.Id;
        c.History.Add(Staff(tec, now.AddDays(-1).AddHours(1), "STATUS", "Te enviamos la cotización para que la revises.", false, "QUOTED", "AWAITING_APPROVAL")); await ApplyAsync(c, default);
        _ = t2;
    }

    // ---------- utilidades ----------
    private static void Check(bool condition, string constraint) { if (!condition) throw new ConstraintViolationException(constraint); }
    private static void MaxLen(string? s, int max, string column) { if (s != null && s.Length > max) throw new ConstraintViolationException($"String or binary data would be truncated ({column} ≤ {max})"); }
    private static void Replace<T>(List<T> list, Func<T, bool> where, Func<T, T> change) { for (var i = 0; i < list.Count; i++) if (where(list[i])) list[i] = change(list[i]); }
    private static void ExpectOne<T>(List<T> list, Func<T, bool> where, Func<T, T> change)
    {
        var idx = list.FindIndex(x => where(x));
        if (idx < 0) throw new ConcurrencyException();     // el UPDATE no afectó ninguna fila
        list[idx] = change(list[idx]);
    }
}
