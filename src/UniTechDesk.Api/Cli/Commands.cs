using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UniTechDesk.Api.Infrastructure;
using UniTechDesk.Core.Abstractions;
using UniTechDesk.Core.Options;
using UniTechDesk.Data.Sql;

namespace UniTechDesk.Api.Cli;

/// <summary>Comandos de ayuda para instalar y revisar el sistema (no arrancan el servidor web).</summary>
public static class Commands
{
    private static readonly string[] Names = { "hash-password", "generate-key", "check", "test-email", "sync-frontend", "help", "--help", "-h" };
    public static bool IsCommand(string arg) => Names.Contains(arg, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "generate-key":
                Console.WriteLine(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
                Console.Error.WriteLine("Guárdala con:  dotnet user-secrets set \"Jwt:SigningKey\" \"<la clave de arriba>\"  (no la compartas ni la subas al repositorio).");
                return 0;

            case "hash-password":
                return HashPassword();

            case "check":
                return await CheckAsync(args);

            case "test-email":
                return await TestEmailAsync(args);

            case "sync-frontend":
                return SyncFrontend(args);

            default:
                Console.WriteLine("""
                    Comandos:
                      hash-password          Genera el hash BCrypt de una contraseña (se pide sin mostrarla).
                      generate-key           Genera una clave aleatoria para Jwt:SigningKey.
                      check                  Revisa la configuración y la conexión a la base de datos.
                      test-email <correo>    Envía un correo de prueba con el proveedor configurado.
                      sync-frontend <carpeta-del-front> [destino]
                                             Copia tu frontend a wwwroot y lo ajusta para usar esta API (ver README).
                    """);
                return 0;
        }
    }

    private static int HashPassword()
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Este comando pide la contraseña de forma interactiva (para que no quede en el historial).");
            return 2;
        }
        var password = ReadHidden("Contraseña nueva: ");
        var again = ReadHidden("Repite la contraseña: ");
        if (password != again) { Console.Error.WriteLine("No coinciden."); return 1; }
        if (password.Length < 10) { Console.Error.WriteLine("Usa al menos 10 caracteres."); return 1; }
        if (Encoding.UTF8.GetByteCount(password) > 72) { Console.Error.WriteLine("BCrypt solo usa los primeros 72 bytes: usa una contraseña más corta."); return 1; }
        var hash = new BCryptPasswordHasher().Hash(password);
        Console.WriteLine();
        Console.WriteLine("Hash BCrypt (guárdalo en dbo.Personal.HashContrasena; la contraseña no se guarda en ningún lado):");
        Console.WriteLine(hash);
        Console.WriteLine();
        Console.WriteLine("Ejemplo para crear una persona del personal (ejecútalo como administrador de la BD, no con la cuenta de la API):");
        Console.WriteLine($"  INSERT INTO dbo.Personal (NombreUsuario, NombreCompleto, HashContrasena, RolCodigo) VALUES (N'usuario', N'Nombre Apellido', '{hash}', 'TECNICO');");
        return 0;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
    }

    private static WebApplication BuildForCommand(string[] args) => UniTechApp.Build(args.Skip(1).Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToArray());

    private static async Task<int> CheckAsync(string[] args)
    {
        WebApplication app;
        try { app = BuildForCommand(args); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine("✗ " + ex.Message); return 1; }

        var problems = app.Services.GetRequiredService<StartupProblems>();
        Console.WriteLine("✓ Configuración válida.");
        foreach (var w in problems.Warnings) Console.WriteLine("! " + w);

        try
        {
            var factory = app.Services.GetRequiredService<IDbConnectionFactory>();
            await using var conn = await factory.OpenAsync(CancellationToken.None);
            Console.WriteLine("✓ Conexión a SQL Server correcta.");
            var staff = await ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.Personal WHERE Activo = 1;");
            var majors = await ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.Carrera WHERE Activo = 1;");
            var transitions = await ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.TransicionEstado;");
            Console.WriteLine($"✓ Personal activo: {staff} · Carreras: {majors} · Transiciones de estado: {transitions}");
            if (Convert.ToInt32(staff) == 0) Console.WriteLine("! No hay personal activo: nadie podrá iniciar sesión en el panel (ver README, «Crear el primer usuario»).");
            foreach (var u in await FindDemoAccountsAsync(app.Services.GetRequiredService<IStaffRepository>(), app.Services.GetRequiredService<IPasswordHasher>(), CancellationToken.None))
                Console.WriteLine($"! La cuenta «{u}» sigue activa con la contraseña de demostración ({DemoPassword}), que es pública. Desactívala o cambia su contraseña antes de publicar el sistema (ver README, «Antes de publicar»).");
            var pending = await ScalarAsync(conn, "SELECT COUNT(*) FROM dbo.NotificacionCorreo WHERE Estado = 'PENDING';");
            Console.WriteLine($"✓ Correos pendientes en la bandeja de salida: {pending}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("✗ No se pudo usar la base de datos: " + ex.GetType().Name + " — " + FirstLine(ex.Message));
            Console.Error.WriteLine("  Revisa: ¿corrió database/00 a 05?, ¿el servidor/instancia es correcto?, ¿el usuario unitech_api está en el rol rol_unitech_api?");
            return 1;
        }
        return 0;
    }

    /// <summary>Contraseña de las cuentas de demostración de database/06_datos_demo.sql (es pública: está en el script).</summary>
    public const string DemoPassword = "Admin#2026";
    private static readonly string[] DemoUsernames = { "admin", "tecnico1", "tecnico2", "desarrollador1" };

    /// <summary>Nombres de las cuentas de demostración que siguen activas con la contraseña pública.</summary>
    public static async Task<IReadOnlyList<string>> FindDemoAccountsAsync(IStaffRepository staff, IPasswordHasher hasher, CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var name in DemoUsernames)
        {
            var account = await staff.FindByUsernameAsync(name, ct);
            if (account is { Active: true } && hasher.Verify(DemoPassword, account.PasswordHash)) found.Add(account.Username);
        }
        return found;
    }

    private static async Task<object?> ScalarAsync(System.Data.Common.DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<int> TestEmailAsync(string[] args)
    {
        var to = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(to) || !UniTechDesk.Core.Validation.Rules.IsEmail(to)) { Console.Error.WriteLine("Uso: test-email <correo-destino>"); return 2; }
        WebApplication app;
        try { app = BuildForCommand(args); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine("✗ " + ex.Message); return 1; }

        var sender = app.Services.GetRequiredService<IEmailSender>();
        var name = app.Services.GetRequiredService<AppOptions>().Name;
        try
        {
            await sender.SendAsync(new EmailMessage(to, $"Prueba de {name}", $"Si lees esto, {name} puede enviar correos.", $"<p>Si lees esto, <strong>{System.Net.WebUtility.HtmlEncode(name)}</strong> puede enviar correos.</p>"), CancellationToken.None);
            Console.WriteLine("✓ El proveedor aceptó el correo. Revisa la bandeja (y la carpeta de spam).");
            return 0;
        }
        catch (EmailSendException ex)
        {
            Console.Error.WriteLine("✗ " + ex.Message);
            return 1;
        }
    }

    private static int SyncFrontend(string[] args)
    {
        var positional = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (positional.Length is < 1 or > 2) { Console.Error.WriteLine("Uso: sync-frontend <carpeta-del-front> [carpeta-destino]"); return 2; }
        var destination = positional.Length == 2 ? positional[1] : DefaultWebRoot();
        try
        {
            var r = FrontendSync.Run(positional[0], destination);
            Console.WriteLine($"✓ {r.FilesCopied} archivos copiados a {Path.GetFullPath(destination)}");
            foreach (var a in r.Applied) Console.WriteLine("  · ajustado: " + a);
            foreach (var d in r.AlreadyDone) Console.WriteLine("  · ya estaba: " + d);
            foreach (var s in r.Skipped.Take(20)) Console.WriteLine("  · omitido: " + s);
            if (r.Skipped.Count > 20) Console.WriteLine($"  · … y {r.Skipped.Count - 20} omitidos más");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("✗ " + ex.Message);
            return 1;
        }
    }

    /// <summary>Desde la raíz del repositorio (donde se ejecuta dotnet run): src/UniTechDesk.Api/wwwroot; si no existe, ./wwwroot.</summary>
    private static string DefaultWebRoot()
    {
        var fromRoot = Path.Combine(Directory.GetCurrentDirectory(), "src", "UniTechDesk.Api", "wwwroot");
        return Directory.Exists(Path.GetDirectoryName(fromRoot)) ? fromRoot : Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
}
