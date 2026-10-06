using UniTechDesk.Core;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Contracts;
using UniTechDesk.Core.Domain;
using UniTechDesk.Core.Files;
using UniTechDesk.Core.Services;
using UniTechDesk.TestSupport;
using UniTechDesk.Tests.Support;

namespace UniTechDesk.Tests;

public class FileInspectorTests
{
    private const int Max = 5 * 1024 * 1024;

    [Fact]
    public void Detecta_cada_formato_por_su_firma()
    {
        Assert.Equal("image/jpeg", FileInspector.DetectMime(Samples.Jpeg()));
        Assert.Equal("image/png", FileInspector.DetectMime(Samples.Png()));
        Assert.Equal("image/gif", FileInspector.DetectMime(Samples.Gif()));
        Assert.Equal("image/webp", FileInspector.DetectMime(Samples.Webp()));
        Assert.Equal("application/pdf", FileInspector.DetectMime(Samples.Pdf()));
        Assert.Null(FileInspector.DetectMime(Samples.Exe()));
        Assert.Null(FileInspector.DetectMime(Array.Empty<byte>()));
    }

    [Fact]
    public void Acepta_una_imagen_valida()
    {
        var r = FileInspector.Inspect("foto.JPG", Samples.Jpeg(), FileInspector.RequestImageExts, Max);
        Assert.Equal("image/jpeg", r.Mime);
        Assert.Equal("jpg", r.Extension);
        Assert.Equal("foto.JPG", r.DisplayName);
    }

    [Fact]
    public void Un_ejecutable_con_extension_png_se_rechaza()
    {
        var ex = Assert.Throws<AppException>(() => FileInspector.Inspect("virus.png", Samples.Exe(), FileInspector.RequestImageExts, Max));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("no es un archivo válido", ex.Message);
    }

    [Fact]
    public void Un_html_con_extension_jpg_se_rechaza()
    {
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        Assert.Equal(422, Assert.Throws<AppException>(() => FileInspector.Inspect("a.jpg", html, FileInspector.RequestImageExts, Max)).StatusCode);
    }

    [Theory]
    [InlineData("doc.exe")]
    [InlineData("doc.svg")]
    [InlineData("doc.html")]
    [InlineData("doc")]
    [InlineData("doc.pdf")]     // las solicitudes de hardware solo aceptan imágenes
    public void Extensiones_no_permitidas_en_solicitudes_de_hardware(string name)
    {
        var ex = Assert.Throws<AppException>(() => FileInspector.Inspect(name, Samples.Png(), FileInspector.RequestImageExts, Max));
        Assert.Contains("formato no permitido", ex.Message);
    }

    [Fact]
    public void Software_acepta_pdf_y_comprobante_acepta_pdf_pero_no_gif()
    {
        Assert.Equal("application/pdf", FileInspector.Inspect("brief.pdf", Samples.Pdf(), FileInspector.RequestSoftwareExts, Max).Mime);
        Assert.Equal("application/pdf", FileInspector.Inspect("pago.pdf", Samples.Pdf(), FileInspector.ProofExts, Max).Mime);
        Assert.Throws<AppException>(() => FileInspector.Inspect("pago.gif", Samples.Gif(), FileInspector.ProofExts, Max));
        Assert.Throws<AppException>(() => FileInspector.Inspect("pago.webp", Samples.Webp(), FileInspector.ProofExts, Max));
    }

    [Fact]
    public void Un_pdf_con_extension_png_se_rechaza_en_hardware()
    {
        // contenido PDF, extensión permitida, pero el contenido no es una imagen
        Assert.Throws<AppException>(() => FileInspector.Inspect("x.png", Samples.Pdf(), FileInspector.RequestImageExts, Max));
    }

    [Fact]
    public void Vacio_y_demasiado_grande()
    {
        Assert.Contains("vacío", Assert.Throws<AppException>(() => FileInspector.Inspect("a.png", Array.Empty<byte>(), FileInspector.RequestImageExts, Max)).Message);
        var big = Samples.Png(Max);
        var ex = Assert.Throws<AppException>(() => FileInspector.Inspect("a.png", big, FileInspector.RequestImageExts, Max));
        Assert.Contains("el máximo es 5 MB", ex.Message);
    }

    [Theory]
    [InlineData("..\\..\\windows\\foto.png", "foto.png")]
    [InlineData("../../etc/passwd.png", "passwd.png")]
    [InlineData("C:\\Users\\ana\\foto.png", "foto.png")]
    [InlineData("", "archivo")]
    [InlineData("..", "archivo")]
    [InlineData("factura\u202Egnp.exe", "facturagnp.exe")]
    public void Nombre_para_mostrar_sin_carpetas_ni_trucos(string input, string expected) =>
        Assert.Equal(expected, FileInspector.SafeDisplayName(input));

    [Fact]
    public void Nombre_largo_se_recorta_conservando_la_extension()
    {
        var name = FileInspector.SafeDisplayName(new string('a', 400) + ".png");
        Assert.Equal(255, name.Length);
        Assert.EndsWith(".png", name);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.png", true)]
    [InlineData("0123456789abcdef0123456789abcdef.pdf", true)]
    [InlineData("0123456789abcdef0123456789abcdef.exe", false)]
    [InlineData("../0123456789abcdef0123456789abcdef.png", false)]
    [InlineData("0123456789ABCDEF0123456789abcdef.png", false)]
    [InlineData("abc.png", false)]
    public void Nombre_almacenado_valido(string name, bool expected) => Assert.Equal(expected, FileInspector.IsValidStoredName(name));

    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void Formato_de_tamano(long bytes, string expected) => Assert.Equal(expected, FileInspector.FormatBytes(bytes));
}

public class DiskAttachmentStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "utd-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task Guarda_con_nombre_generado_y_lo_devuelve_y_lo_borra()
    {
        var store = new DiskAttachmentStore(_dir);
        var name = await store.SaveAsync(Samples.Png(), "png", default);
        Assert.True(FileInspector.IsValidStoredName(name));
        using (var s = store.OpenRead(name)) { Assert.NotNull(s); Assert.Equal(Samples.Png().Length, s!.Length); }
        store.DeleteQuietly(name);
        Assert.Null(store.OpenRead(name));
        store.DeleteQuietly(name);   // borrar dos veces no falla
    }

    [Theory]
    [InlineData("../secreto.txt")]
    [InlineData("..\\secreto.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\windows\\win.ini")]
    [InlineData("0123456789abcdef0123456789abcdef.png/../../x")]
    [InlineData("")]
    public void No_abre_rutas_fuera_de_la_carpeta(string name)
    {
        var store = new DiskAttachmentStore(_dir);
        Assert.Null(store.OpenRead(name));
        store.DeleteQuietly(name);   // no debe lanzar ni borrar nada
    }

    [Fact]
    public async Task Rechaza_extensiones_raras_al_guardar()
    {
        var store = new DiskAttachmentStore(_dir);
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(new byte[] { 1 }, "exe", default));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(new byte[] { 1 }, "../x", default));
    }
}

public class TicketIntakeTests
{
    private static readonly Catalogs Catalogs = new(
        new List<CatalogItem> { new(1, "Ingeniería en Sistemas"), new(2, "Ingeniería Química") },
        new List<CatalogItem> { new(1, "RUSB"), new(2, "RUPAP") });
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly DateTime Now = new(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc);

    private static NewTicket Validate(CreateTicketRequest r) => TicketIntake.Validate(r, Catalogs, Today, Now, "1.0");

    private static IReadOnlyDictionary<string, string> Errors(CreateTicketRequest r)
    {
        var ex = Assert.Throws<AppException>(() => Validate(r));
        Assert.Equal(422, ex.StatusCode);
        return ex.FieldErrors!;
    }

    [Fact]
    public void Solicitud_valida_de_estudiante_con_hardware()
    {
        var t = Validate(Env.StudentHardware());
        Assert.Equal("STUDENT", t.RequesterType);
        Assert.Equal("20231234", t.StudentId);
        Assert.Null(t.Company);
        Assert.Null(t.Ruc);
        Assert.Equal("HARDWARE", t.ServiceType);
        Assert.Equal("HP", t.Hardware!.Brand);
        Assert.Equal(new[] { "CHARGER" }, t.Hardware.Accessories);
        Assert.Null(t.Software);
        Assert.Equal(Now, t.AcceptedTermsAt);
    }

    [Fact]
    public void Solicitud_valida_de_cliente_externo_con_software()
    {
        var t = Validate(Env.ExternalSoftware());
        Assert.Equal("EXTERNAL", t.RequesterType);
        Assert.Null(t.StudentId);
        Assert.Null(t.MajorId);
        Assert.Null(t.CampusId);
        Assert.Equal("J0310000012345", t.Ruc);
        Assert.Equal("CUSTOM_DEV", t.Software!.Kind);
        Assert.Null(t.Hardware);
    }

    [Fact]
    public void Un_estudiante_no_guarda_datos_de_empresa_aunque_los_envie()
    {
        var r = Env.StudentHardware();
        r.Company = "Empresa que no corresponde"; r.Ruc = "J0310000012345";
        var t = Validate(r);
        Assert.Null(t.Company);
        Assert.Null(t.Ruc);
    }

    [Fact]
    public void Un_externo_no_guarda_carnet_ni_carrera()
    {
        var r = Env.ExternalSoftware();
        r.StudentId = "20231234"; r.MajorId = 1; r.CampusId = 1;
        var t = Validate(r);
        Assert.Null(t.StudentId); Assert.Null(t.MajorId); Assert.Null(t.CampusId);
    }

    [Fact]
    public void Un_formulario_vacio_marca_todos_los_campos_obligatorios()
    {
        var e = Errors(new CreateTicketRequest());
        foreach (var key in new[] { "requesterType", "fullName", "email", "phone", "serviceType", "description", "urgency", "preferredPaymentMethod", "acceptedTerms" })
            Assert.True(e.ContainsKey(key), "falta el error de " + key);
    }

    [Fact]
    public void Hardware_exige_marca_modelo_enciende_y_aviso_de_respaldo()
    {
        var r = Env.StudentHardware();
        r.Hardware = new HardwareDto();
        r.BackupAck = false;
        var e = Errors(r);
        foreach (var key in new[] { "hardware.equipmentType", "hardware.brand", "hardware.model", "hardware.powersOn", "backupAck" })
            Assert.True(e.ContainsKey(key), "falta el error de " + key);
    }

    [Fact]
    public void Software_exige_el_tipo_y_valida_el_enlace_y_la_fecha()
    {
        var r = Env.ExternalSoftware();
        r.Software = new SoftwareDto { Kind = "NADA", ReferenceUrl = "javascript:alert(1)", DesiredDate = "2020-01-01" };
        var e = Errors(r);
        Assert.True(e.ContainsKey("software.kind"));
        Assert.True(e.ContainsKey("software.referenceUrl"));
        Assert.True(e.ContainsKey("software.desiredDate"));
    }

    [Fact]
    public void La_fecha_deseada_de_hoy_o_posterior_es_valida()
    {
        var r = Env.ExternalSoftware();
        r.Software!.DesiredDate = "2026-10-06";
        Assert.Equal(new DateOnly(2026, 10, 6), Validate(r).Software!.DesiredDate);
    }

    [Theory]
    [InlineData("corta", false)]
    [InlineData("exactamente 15 c", true)]
    public void Descripcion_minima_de_15_caracteres(string text, bool ok)
    {
        var r = Env.StudentHardware(); r.Description = text;
        if (ok) Validate(r); else Assert.True(Errors(r).ContainsKey("description"));
    }

    [Fact]
    public void Descripcion_maxima_de_2000_caracteres()
    {
        var r = Env.StudentHardware(); r.Description = new string('a', 2001);
        Assert.True(Errors(r).ContainsKey("description"));
        r.Description = new string('a', 2000);
        Validate(r);
    }

    [Fact]
    public void Carrera_y_recinto_deben_existir()
    {
        var r = Env.StudentHardware(); r.MajorId = 99; r.CampusId = 99;
        var e = Errors(r);
        Assert.True(e.ContainsKey("majorId")); Assert.True(e.ContainsKey("campusId"));
    }

    [Fact]
    public void Los_textos_se_limpian_y_se_respetan_los_largos_de_las_columnas()
    {
        var r = Env.StudentHardware();
        r.FullName = new string('n', 121); r.Hardware!.Brand = new string('b', 61); r.Hardware.Model = new string('m', 81); r.Hardware.Serial = new string('s', 61);
        r.Hardware.AccessoriesOther = new string('o', 201);
        var e = Errors(r);
        foreach (var key in new[] { "fullName", "hardware.brand", "hardware.model", "hardware.serial", "hardware.accessoriesOther" })
            Assert.True(e.ContainsKey(key), key);
    }

    [Fact]
    public void Accesorios_desconocidos_y_repetidos_se_descartan()
    {
        var r = Env.StudentHardware();
        r.Hardware!.Accessories = new() { "CHARGER", "CHARGER", "NO_EXISTE", "MOUSE" };
        Assert.Equal(new[] { "CHARGER", "MOUSE" }, Validate(r).Hardware!.Accessories);
    }

    [Fact]
    public void Nombre_y_correo_se_recortan()
    {
        var r = Env.StudentHardware(); r.FullName = "  Ana   López \u202E"; r.Email = "  ana.lopez@example.com ";
        var t = Validate(r);
        Assert.Equal("Ana   López", t.FullName);
        Assert.Equal("ana.lopez@example.com", t.Email);
    }

    [Fact]
    public void Debe_aceptar_las_condiciones()
    {
        var r = Env.StudentHardware(); r.AcceptedTerms = null;
        Assert.True(Errors(r).ContainsKey("acceptedTerms"));
    }
}
