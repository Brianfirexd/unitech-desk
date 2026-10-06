using UniTechDesk.Api.Cli;

namespace UniTechDesk.Tests;

/// <summary>El comando sync-frontend: copia el front a wwwroot y lo ajusta para usar la API real.</summary>
public sealed class FrontendSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "utd-sync-" + Guid.NewGuid().ToString("N"));
    public FrontendSyncTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Dir(string name) { var p = Path.Combine(_root, name); Directory.CreateDirectory(p); return p; }
    private static void Write(string dir, string rel, string content)
    {
        var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private const string Index = """
        <!doctype html><html><head>
          <script defer src="js/config.js"></script>
          <script defer src="js/api.js"></script>
          <!-- Solo desarrollo: elimina esta línea y pon config.useMock = false al conectar el backend. -->
          <script defer src="js/mock-api.js"></script>
          <script defer src="js/views/new-ticket.js"></script>
        </head><body>hola</body></html>
        """;
    private const string Config = "UTD.config = {\n  useMock: true,\n  apiBaseUrl: '/api',\n  notifications: {\n      email: false\n    },\n  limits: { maxFiles: 5 }\n};\n";
    private const string NewTicket = "(function(){ var IMAGE_EXTS = ['jpg','png'];\n  var DOC_EXTS = ['pdf', 'txt', 'docx', 'xlsx'];\n })();\n";

    private string FakeFront()
    {
        var d = Dir("front");
        Write(d, "index.html", Index);
        Write(d, "js/config.js", Config);
        Write(d, "js/api.js", "// api");
        Write(d, "js/mock-api.js", "// solo demostración");
        Write(d, "js/views/new-ticket.js", NewTicket);
        Write(d, "dist/styles.css", "body{}");
        Write(d, "assets/favicon.svg", "<svg/>");
        return d;
    }

    private static string Read(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Copia_el_front_y_lo_ajusta_para_la_API_real()
    {
        var front = FakeFront();
        var web = Path.Combine(_root, "wwwroot");
        var r = FrontendSync.Run(front, web);

        Assert.Equal(4, r.Applied.Count);
        Assert.Empty(r.AlreadyDone);
        Assert.Contains("useMock: false", Read(web, "js/config.js"));
        Assert.DoesNotContain("useMock: true", Read(web, "js/config.js"));
        Assert.Matches(@"notifications:\s*\{\s*email:\s*true", Read(web, "js/config.js"));
        Assert.DoesNotContain("mock-api", Read(web, "index.html"));
        Assert.DoesNotContain("Solo desarrollo", Read(web, "index.html"));
        Assert.Contains("js/views/new-ticket.js", Read(web, "index.html"));       // el resto de los scripts se conserva
        Assert.Contains("js/api.js", Read(web, "index.html"));
        Assert.Contains("var DOC_EXTS = ['pdf'];", Read(web, "js/views/new-ticket.js"));
        Assert.False(File.Exists(Path.Combine(web, "js", "mock-api.js")));          // el simulador no se copia
        Assert.True(File.Exists(Path.Combine(web, "dist", "styles.css")));
        Assert.True(File.Exists(Path.Combine(web, "assets", "favicon.svg")));
        Assert.Contains(r.Skipped, s => s.Contains("mock-api.js"));
        // El origen no se modifica.
        Assert.Contains("useMock: true", Read(front, "js/config.js"));
        Assert.Contains("mock-api.js", Read(front, "index.html"));
    }

    [Fact]
    public void Es_idempotente_y_el_resultado_es_identico()
    {
        var front = FakeFront();
        var web = Path.Combine(_root, "wwwroot");
        FrontendSync.Run(front, web);
        var first = new[] { "index.html", "js/config.js", "js/views/new-ticket.js" }.Select(f => Read(web, f)).ToArray();

        // Segunda vez desde el mismo origen: mismo resultado.
        FrontendSync.Run(front, web);
        Assert.Equal(first, new[] { "index.html", "js/config.js", "js/views/new-ticket.js" }.Select(f => Read(web, f)));

        // Y sincronizar un front que ya viene ajustado no cambia nada ni falla.
        var again = Path.Combine(_root, "wwwroot2");
        var r = FrontendSync.Run(web, again);
        Assert.Empty(r.Applied);
        Assert.Equal(4, r.AlreadyDone.Count);
        Assert.Equal(first, new[] { "index.html", "js/config.js", "js/views/new-ticket.js" }.Select(f => Read(again, f)));
    }

    [Fact]
    public void Los_archivos_que_ya_no_existen_en_el_front_no_se_quedan_en_wwwroot()
    {
        var front = FakeFront();
        var web = Path.Combine(_root, "wwwroot");
        Write(web, "index.html", "viejo");
        Write(web, "js/viejo.js", "// obsoleto");
        FrontendSync.Run(front, web);
        Assert.False(File.Exists(Path.Combine(web, "js", "viejo.js")));
    }

    [Fact]
    public void Ignora_la_basura_que_no_es_parte_del_front()
    {
        var front = FakeFront();
        Write(front, "js/oracleJdk-27/bin/java.exe", new string('x', 100));
        Write(front, "js/oracleJdk-27/lib/modules", new string('x', 100));
        Write(front, "js/.git/config", "x");
        Write(front, "js/node_modules/pkg/index.js", "x");
        File.WriteAllBytes(Path.Combine(front, "js", "enorme.js"), new byte[6 * 1024 * 1024]);
        var web = Path.Combine(_root, "wwwroot");
        var r = FrontendSync.Run(front, web);
        Assert.False(Directory.Exists(Path.Combine(web, "js", "node_modules")));
        Assert.False(Directory.Exists(Path.Combine(web, "js", ".git")));
        Assert.False(File.Exists(Path.Combine(web, "js", "enorme.js")));
        Assert.False(File.Exists(Path.Combine(web, "js", "oracleJdk-27", "bin", "java.exe")));
        Assert.False(File.Exists(Path.Combine(web, "js", "oracleJdk-27", "lib", "modules")));
        Assert.Contains(r.Skipped, s => s.Contains("enorme.js"));
    }

    [Fact]
    public void Se_detiene_si_la_carpeta_no_parece_un_front()
    {
        var front = FakeFront();
        for (var i = 0; i < 305; i++) Write(front, $"assets/img{i}.png", "x");
        var ex = Assert.Throws<InvalidOperationException>(() => FrontendSync.Run(front, Path.Combine(_root, "wwwroot")));
        Assert.Contains("no parece ser solo el front", ex.Message);
    }

    [Fact]
    public void Si_algo_falla_el_wwwroot_anterior_queda_intacto_y_sin_basura()
    {
        var web = Dir("wwwroot");
        Write(web, "index.html", "versión que funcionaba");
        Write(web, "js/app.js", "// versión que funcionaba");

        var front = FakeFront();
        Write(front, "js/config.js", "UTD.config = { };");                       // no se puede ajustar → falla después de copiar
        Assert.Throws<InvalidOperationException>(() => FrontendSync.Run(front, web));

        Assert.Equal("versión que funcionaba", Read(web, "index.html"));
        Assert.Equal("// versión que funcionaba", Read(web, "js/app.js"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, ".utd-sync-*"));     // la carpeta temporal se borró
    }

    [Fact]
    public void Errores_claros_si_el_origen_o_el_destino_no_sirven()
    {
        Assert.Contains("index.html", Assert.Throws<InvalidOperationException>(() => FrontendSync.Run(Dir("vacia"), Path.Combine(_root, "w"))).Message);

        var front = FakeFront();
        Assert.Contains("misma carpeta", Assert.Throws<InvalidOperationException>(() => FrontendSync.Run(front, front)).Message);

        // Nunca se borra dentro de una carpeta cualquiera con otros archivos.
        var other = Dir("documentos");
        Write(other, "tesis.docx", "importante");
        Write(other, "js/mis-notas.js", "importante");
        Assert.Contains("no es un wwwroot", Assert.Throws<InvalidOperationException>(() => FrontendSync.Run(front, other)).Message);
        Assert.True(File.Exists(Path.Combine(other, "js", "mis-notas.js")));
        Assert.True(File.Exists(Path.Combine(other, "tesis.docx")));
    }

    [Fact]
    public void Si_el_front_cambio_de_forma_avisa_en_vez_de_dejarlo_a_medias()
    {
        var front = FakeFront();
        Write(front, "js/config.js", "UTD.config = { apiBaseUrl: '/api' };");   // sin useMock ni notifications
        var ex = Assert.Throws<InvalidOperationException>(() => FrontendSync.Run(front, Path.Combine(_root, "wwwroot")));
        Assert.Contains("useMock", ex.Message);
        Assert.Contains("README", ex.Message);
    }

    [Fact]
    public void Conserva_la_marca_BOM_de_los_archivos()
    {
        var front = FakeFront();
        File.WriteAllText(Path.Combine(front, "js", "config.js"), Config, new System.Text.UTF8Encoding(true));
        var web = Path.Combine(_root, "wwwroot");
        FrontendSync.Run(front, web);
        var bytes = File.ReadAllBytes(Path.Combine(web, "js", "config.js"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Contains("useMock: false", System.Text.Encoding.UTF8.GetString(bytes));
    }
}
