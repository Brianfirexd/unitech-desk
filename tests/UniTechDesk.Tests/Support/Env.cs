using Microsoft.Extensions.Logging.Abstractions;
using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Email;
using UniTechDesk.Core.Files;
using UniTechDesk.Core.Options;
using UniTechDesk.Core.Services;
using UniTechDesk.TestSupport;

namespace UniTechDesk.Tests.Support;

/// <summary>
/// Armado completo de servicios sobre la base en memoria. Cada prueba crea el suyo (reloj fijo: 2026-10-06 15:00 UTC = 09:00 en Managua).
/// </summary>
public sealed class Env
{
    public const string ClientEmail = "ana.lopez@example.com";
    public const string ClientPhone = "8888-1234";
    public const string ExternalEmail = "carlos@empresa.example";

    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero));
    public InMemoryStore Store { get; }
    public FakeEmailSender Sender { get; } = new();
    public MemoryAttachmentStore Files { get; } = new();
    public FakeCaptcha Captcha { get; } = new();
    public PlainHasher Hasher { get; } = new();

    public AppOptions App { get; } = new() { PublicBaseUrl = "https://soporte.example.edu", Name = "UniTech Desk", ContactLine = "Escríbenos a soporte@example.edu" };
    public EmailOptions Email { get; } = new() { Provider = "Console", FromEmail = "noreply@example.edu", MaxAttempts = 6 };
    public UploadOptions Uploads { get; } = new() { MaxFiles = 5, MaxFileSizeMB = 5 };
    public LoginOptions Login { get; } = new() { MaxAttempts = 5, LockMinutes = 15 };
    public PaymentOptions Payment { get; } = new()
    {
        BankAccounts =
        {
            new BankAccount { Bank = "Banco de Ejemplo", Currency = "NIO", Number = "1000-0000-0001", Holder = "Titular de Ejemplo" },
            new BankAccount { Bank = "Banco de Ejemplo", Currency = "USD", Number = "2000-0000-0002", Holder = "Titular de Ejemplo" }
        }
    };

    public BusinessClock Clock { get; private set; } = null!;
    public TicketMapper Mapper { get; private set; } = null!;
    public CatalogService Catalogs { get; private set; } = null!;
    public MailPlanner Planner { get; private set; } = null!;
    public PublicTicketService Public { get; private set; } = null!;
    public StaffTicketService Staff { get; private set; } = null!;
    public AuthService Auth { get; private set; } = null!;
    public EmailTemplates Templates { get; private set; } = null!;
    public OutboxDispatcher Dispatcher { get; private set; } = null!;
    public FileLinkSigner Signer { get; } = new("clave-de-prueba-clave-de-prueba-1234", TimeSpan.FromMinutes(10));
    public ITicketRepository Tickets { get; private set; } = null!;

    public int AdminId { get; }
    public int TecId { get; }   // 0 si se pidió seedStaff: false
    public StaffActor Admin => new(AdminId, "Administración");
    public StaffActor Tec => new(TecId, "Técnico Uno");

    /// <param name="tweak">Ajusta opciones antes de armar los servicios.</param>
    /// <param name="wrapTickets">Envuelve el repositorio de tickets (por ejemplo, para simular concurrencia).</param>
    /// <param name="seedStaff">false si la prueba va a cargar sus propios usuarios (por ejemplo, con SeedDemoAsync).</param>
    public Env(Action<Env>? tweak = null, Func<ITicketRepository, ITicketRepository>? wrapTickets = null, bool seedStaff = true)
    {
        Store = new InMemoryStore(Time);
        if (seedStaff)
        {
            AdminId = Store.AddStaff("admin", "Administración", "ADMIN", Hasher.Hash("Admin#2026"), email: "admin@example.edu");
            TecId = Store.AddStaff("tecnico1", "Técnico Uno", "TECNICO", Hasher.Hash("Tec#2026"));
        }
        tweak?.Invoke(this);

        Clock = new BusinessClock(Time, App);
        Tickets = wrapTickets?.Invoke(Store) ?? Store;
        Mapper = new TicketMapper(Payment, Signer, Clock);
        Catalogs = new CatalogService(Store, Time);
        Planner = new MailPlanner(Email);
        var signal = new NoOpSignal();
        Public = new PublicTicketService(Tickets, Catalogs, Files, Captcha, Planner, Clock, Mapper, signal, App, Uploads, NullLogger<PublicTicketService>.Instance);
        Staff = new StaffTicketService(Tickets, Store, Planner, Clock, Mapper, signal, NullLogger<StaffTicketService>.Instance);
        Auth = new AuthService(Store, Hasher, new FakeTokenIssuer(), Clock, Login, NullLogger<AuthService>.Instance);
        Templates = new EmailTemplates(App, Mapper);
        Dispatcher = new OutboxDispatcher(Store, Tickets, Sender, Templates, Clock, Email, NullLogger<OutboxDispatcher>.Instance);
    }

    // ---------------- Solicitudes de ejemplo ----------------

    public static CreateTicketRequest StudentHardware() => new()
    {
        RequesterType = "STUDENT", FullName = "Ana López", Email = ClientEmail, Phone = ClientPhone, StudentId = "20231234", MajorId = 1, CampusId = 1,
        ServiceType = "HARDWARE",
        Hardware = new HardwareDto { EquipmentType = "LAPTOP", Brand = "HP", Model = "Pavilion 15", Serial = "SN-123", PowersOn = "SOMETIMES", Accessories = new() { "CHARGER" }, AccessoriesOther = "" },
        Description = "La laptop se apaga sola después de unos minutos de uso y el ventilador hace mucho ruido.",
        Urgency = "MEDIUM", PreferredPaymentMethod = "TRANSFER", BackupAck = true, AcceptedTerms = true
    };

    public static CreateTicketRequest ExternalSoftware() => new()
    {
        RequesterType = "EXTERNAL", FullName = "Carlos Ruiz", Email = ExternalEmail, Phone = "2222-3333", Company = "Empresa ABC S.A.", Ruc = "J0310000012345",
        ServiceType = "SOFTWARE", Software = new SoftwareDto { Kind = "CUSTOM_DEV", ReferenceUrl = "https://example.com/referencia" },
        Description = "Necesitamos un sistema pequeño para controlar el inventario de la bodega y generar reportes.",
        Urgency = "LOW", PreferredPaymentMethod = "CASH", AcceptedTerms = true
    };

    public async Task<(int Id, string Code)> CreateAsync(CreateTicketRequest? request = null, params UploadedFile[] files)
    {
        var created = await Public.CreateAsync(request ?? StudentHardware(), files, "127.0.0.1", default);
        return (int.Parse(created.Code[4..], System.Globalization.CultureInfo.InvariantCulture), created.Code);
    }

    // ---------------- Atajos del personal ----------------

    public Task<AdminTicketDto> ToStatus(int id, string to, string? note = null, StaffActor? who = null) =>
        Staff.ChangeStatusAsync(who ?? Admin, id, new StatusChangeRequest { To = to, Note = note }, default);

    /// <summary>OPEN → IN_REVIEW: desde ahí se puede guardar la cotización.</summary>
    public Task<AdminTicketDto> Review(int id) => ToStatus(id, Codes.Status.InReview, "Estamos revisando tu solicitud.");

    public Task<AdminTicketDto> SaveQuote(int id, decimal amount = 850m, string currency = "NIO", string? validUntil = "2026-10-20", string description = "Limpieza interna y cambio de pasta térmica.") =>
        Staff.SaveQuoteAsync(Admin, id, new SaveQuoteRequest { Amount = amount, Currency = currency, Description = description, ValidUntil = validUntil }, default);

    /// <summary>OPEN → IN_REVIEW → (cotización guardada) QUOTED → AWAITING_APPROVAL. Deja la cotización publicada para el cliente.</summary>
    public async Task PublishQuoteAsync(int id, decimal amount = 850m, string currency = "NIO", string? validUntil = "2026-10-20")
    {
        await ToStatus(id, Codes.Status.InReview, "Estamos revisando tu equipo.");
        await SaveQuote(id, amount, currency, validUntil);
        await ToStatus(id, Codes.Status.Quoted);
        await ToStatus(id, Codes.Status.AwaitingApproval, "Te enviamos la cotización.");
    }

    public Task<PublicTicketDto> ClientDecides(string code, string decision, string email = ClientEmail) =>
        Public.DecideQuoteAsync(code, email, decision, default);

    public Task<AdminTicketDto> Get(int id) => Staff.GetAsync(id, default);

    public Task<AdminTicketDto> Pay(int id, string status) => Staff.SetPaymentAsync(Admin, id, new SetPaymentRequest { Status = status }, default);

    // ---------------- Errores ----------------

    public static async Task<AppException> Fails(Func<Task> action)
    {
        try { await action(); }
        catch (AppException ex) { return ex; }
        throw new Xunit.Sdk.XunitException("Se esperaba un AppException y la operación terminó bien.");
    }

    public static async Task<AppException> Fails(int status, Func<Task> action)
    {
        var ex = await Fails(action);
        Assert.Equal(status, ex.StatusCode);
        return ex;
    }
}
