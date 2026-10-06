using UniTechDesk.Api;
using UniTechDesk.Api.Cli;

// Comandos de ayuda:  dotnet run --project src/UniTechDesk.Api -- <comando>
//   hash-password   genera el hash BCrypt de una contraseña (para crear personal en la BD)
//   generate-key    genera una clave aleatoria para Jwt:SigningKey
//   check           revisa la configuración y la conexión a la BD
//   test-email <a>  envía un correo de prueba con el proveedor configurado
if (args.Length > 0 && Commands.IsCommand(args[0])) return await Commands.RunAsync(args);

var app = UniTechApp.Build(args);
await app.RunAsync();
return 0;
