# UniTech Desk · Backend

API en **.NET 10** para el sistema de tickets de reparación de hardware y desarrollo de software. Atiende al frontend (`wwwroot`), guarda todo en la base **SQL Server** `UniTechDesk`, inicia sesión del personal con **JWT** y envía los correos con **SendGrid**.

> **Lee primero la sección 9 («Qué se verificó y qué no»).** El código y las pruebas se hicieron en un entorno sin SQL Server, así que hay cosas que solo tú puedes confirmar en tu equipo. Ahí está la lista exacta y cómo confirmarlas en 10 minutos.

---

## Contenido

1. [Qué incluye](#1-qué-incluye)
2. [Requisitos](#2-requisitos)
3. [Instalación paso a paso](#3-instalación-paso-a-paso)
4. [Cómo funciona](#4-cómo-funciona)
5. [La API](#5-la-api)
6. [Reglas del negocio](#6-reglas-del-negocio)
7. [Correos (SendGrid)](#7-correos-sendgrid)
8. [Seguridad](#8-seguridad)
9. [Qué se verificó y qué no](#9-qué-se-verificó-y-qué-no)
10. [Antes de publicar](#10-antes-de-publicar)
11. [Problemas frecuentes](#11-problemas-frecuentes)
12. [Cambios que el backend hace al frontend](#12-cambios-que-el-backend-hace-al-frontend)

---

## 1. Qué incluye

```
UniTechDesk.sln
src/
  UniTechDesk.Core/        Reglas del negocio, servicios, validaciones, plantillas de correo. Sin paquetes NuGet.
  UniTechDesk.Data/        Acceso a SQL Server con ADO.NET (SQL explícito y parametrizado). Sin paquetes NuGet.
  UniTechDesk.Api/         Servidor web: endpoints, JWT, seguridad HTTP, SendGrid, comandos de ayuda, wwwroot (tu front).
  UniTechDesk.TestSupport/ Base de datos en memoria y dobles de prueba (solo para las pruebas).
tests/UniTechDesk.Tests/   413 pruebas.
tools/
  generar_workflow.js      Genera WorkflowCatalog.g.cs desde js/constants.js del front (estados y transiciones).
  verificar_sql.py         Verifica el SQL del backend contra los scripts de tu base de datos (ver sección 9).
```

## 2. Requisitos

| Qué | Para qué |
|---|---|
| .NET 10 SDK | compilar y ejecutar |
| SQL Server 2017 o superior (sirve Express o Developer) | la base `UniTechDesk` (tus scripts `00` a `05`) |
| Cuenta de SendGrid | enviar correos (también funciona sin ella con `Email:Provider=Console`, que solo escribe los correos en la consola) |
| El frontend (`Front.zip`) | se copia a `wwwroot` con un comando (paso 3.6) |

Los comandos están escritos para **PowerShell**. Todo se ejecuta desde la carpeta que contiene `UniTechDesk.sln`.

---

## 3. Instalación paso a paso

### 3.1 Crear la base de datos

En SQL Server Management Studio (o `sqlcmd`) ejecuta, **en orden**, los scripts de tu carpeta `database`:

`00_crear_base.sql` → `01_tablas.sql` → `02_indices_y_reglas.sql` → `03_catalogos.sql` → `04_vistas.sql` → `05_seguridad.sql`

Luego, **solo en desarrollo**, `06_datos_demo.sql` (crea las cuentas `admin`, `tecnico1`, `tecnico2`, `desarrollador1` y tickets de ejemplo) y finalmente `07_pruebas.sql`: debe mostrar todos los pasos con ✓. **Si algún paso falla, copia el mensaje completo y mándamelo** (es la parte que no pude ejecutar yo; ver sección 9).

### 3.2 Crear el usuario de la API (nunca `sa`)

La API debe conectarse con un usuario del rol `rol_unitech_api`, que solo tiene los permisos que necesita. Ejecuta esto con una contraseña larga y única (es el bloque que viene comentado al final de `05_seguridad.sql`):

```sql
USE master;
CREATE LOGIN unitech_api WITH PASSWORD = N'<contraseña-larga-y-única>', CHECK_POLICY = ON;
GO
USE UniTechDesk;
CREATE USER unitech_api FOR LOGIN unitech_api;
ALTER ROLE rol_unitech_api ADD MEMBER unitech_api;
GO
```

Para que `unitech_api` pueda entrar, el servidor debe aceptar **autenticación mixta** (SQL Server y Windows). Si instalaste solo con autenticación de Windows, cámbialo en *SSMS → clic derecho en el servidor → Propiedades → Seguridad* y reinicia el servicio.

### 3.3 Guardar los secretos (nunca en un archivo del proyecto)

Los secretos se guardan con `dotnet user-secrets`, que los deja fuera de la carpeta del proyecto (en tu perfil de Windows). **No pegues la clave de SendGrid ni la contraseña de la base en el chat ni en un commit.**

```powershell
cd src\UniTechDesk.Api

# 1) Conexión a la base. Ajusta el servidor: localhost, .\SQLEXPRESS, etc.
dotnet user-secrets set "ConnectionStrings:UniTechDesk" "Server=localhost;Database=UniTechDesk;User Id=unitech_api;Password=<la-contraseña-del-paso-3.2>;TrustServerCertificate=True"

# 2) Clave para firmar los tokens del personal (genera una aleatoria)
dotnet run -- generate-key
dotnet user-secrets set "Jwt:SigningKey" "<pega-aquí-la-clave-generada>"

# 3) Correo
dotnet user-secrets set "Email:Provider" "SendGrid"
dotnet user-secrets set "Email:SendGridApiKey" "<tu-api-key-de-sendgrid>"
dotnet user-secrets set "Email:FromEmail" "<el-remitente-verificado-en-sendgrid>"
dotnet user-secrets set "Email:StaffNotifyTo:0" "<correo-del-personal-que-recibe-avisos>"
```

Notas sobre la cadena de conexión:

- Instancia con nombre (SQL Server Express): `Server=.\SQLEXPRESS;...`
- `TrustServerCertificate=True` solo es aceptable en tu equipo local. En un servidor real usa un certificado válido y quítalo.
- Si prefieres **autenticación de Windows** en desarrollo: `Server=localhost;Database=UniTechDesk;Integrated Security=True;TrustServerCertificate=True`. Entonces la API se conecta con *tu* usuario de Windows (que probablemente tiene más permisos que el rol limitado); sirve para empezar, no para publicar.
- `dotnet run` ya arranca en modo *Development* (lo fija `Properties/launchSettings.json`), que es lo que hace que `user-secrets` se lea. En producción usa variables de entorno (sección 10).

### 3.4 SendGrid

1. En SendGrid: *Settings → API Keys → Create API Key → Restricted Access* y activa **solo «Mail Send»** (nada más). Cópiala una sola vez y guárdala con el comando de arriba.
2. *Settings → Sender Authentication → Single Sender Verification*: verifica el correo que usarás como remitente (`Email:FromEmail`). SendGrid solo envía desde remitentes verificados.
3. **Gmail/Outlook/Yahoo como remitente:** SendGrid avisa que esos dominios tienen políticas DMARC estrictas y los correos pueden caer en spam o ser rechazados. Para pruebas sirve; para producción usa un correo de un dominio tuyo (o de la universidad) con *Domain Authentication*.
4. Tu período de prueba de SendGrid termina el **4 de diciembre de 2026**; revisa qué plan te queda después.

### 3.5 Crear el primer usuario del personal

Las cuentas del personal se crean en la base (la API no crea ni cambia contraseñas, a propósito):

```powershell
cd src\UniTechDesk.Api
dotnet run -- hash-password        # te pide la contraseña sin mostrarla y escribe el hash BCrypt
```

Copia el `INSERT` que imprime y ejecútalo en SSMS **como administrador de la base, no con `unitech_api`**. Los roles válidos son `ADMIN`, `TECNICO` y `DESARROLLADOR`.

Si cargaste `06_datos_demo.sql`, esas cuentas tienen la contraseña **pública** `Admin#2026`. Desactívalas o cámbiales la contraseña antes de publicar (`dotnet run -- check` te avisa si siguen así).

### 3.6 Conectar el frontend

Tu frontend viene con un simulador (`mock-api.js`) para trabajar sin backend. Este comando copia el front a `wwwroot` y lo deja usando la API real:

```powershell
cd src\UniTechDesk.Api
dotnet run -- sync-frontend "C:\ruta\a\tu\front"     # la carpeta que contiene index.html, js, dist y assets
```

Qué ajusta (y nada más): ver [sección 12](#12-cambios-que-el-backend-hace-al-frontend). Es idempotente: puedes repetirlo cada vez que cambies el front. Si el front cambia de forma y no encuentra algo que ajustar, se detiene con un mensaje en vez de dejarlo a medias. Solo copia archivos de tipo web, ignora carpetas basura (`node_modules`, `.git`, un JDK pegado por error en `js/`…) y nunca borra en una carpeta que no parezca un `wwwroot`.

### 3.7 Revisar y arrancar

```powershell
cd src\UniTechDesk.Api
dotnet run -- check                         # valida configuración, conexión, datos base y cuentas de demostración
dotnet run -- test-email tucorreo@gmail.com # envía un correo real con SendGrid
dotnet run                                  # arranca en http://localhost:5080
```

Abre `http://localhost:5080`. El panel del personal está en `http://localhost:5080/#/admin`.

Si `check` dice que todo está bien pero algo falla al usar el sistema, lo más probable es lo de la sección 9.

---

## 4. Cómo funciona

**Capas.** `Core` (reglas) no conoce SQL ni HTTP; `Data` (SQL) no conoce HTTP; `Api` conecta todo. Por eso las reglas se pueden probar sin base de datos.

**Decisiones que conviene que conozcas:**

- **ADO.NET en vez de Entity Framework.** La base ya tiene triggers, claves compuestas, `ROWVERSION`, un índice único filtrado y un modelo con códigos de texto como llaves; casi todo lo importante está en SQL. Con SQL explícito y parametrizado ves exactamente qué se ejecuta, y cada operación de escritura se hace en **una sola transacción**. Si prefieres EF Core, las notas de tu `README` de la base de datos siguen sirviendo para eso; este backend no las usa.
- **Control de concurrencia.** Si dos personas editan el mismo ticket, la segunda recibe un `409` («otra persona modificó este ticket…») gracias a `Ticket.VersionFila`, en vez de pisar el cambio de la primera.
- **JWT propio y pequeño** (HS256, `Microsoft.IdentityModel.JsonWebTokens`) en lugar del esquema `JwtBearer` completo: menos piezas, y cada petición del personal comprueba además que la cuenta siga activa (con una caché de 30 s). Desactivar a alguien en la base le quita el acceso en menos de un minuto aunque su token no haya vencido.
- **Correos con bandeja de salida (outbox).** Cada cambio que debe avisar al cliente guarda una fila en `NotificacionCorreo` *dentro de la misma transacción*. Un proceso en segundo plano las envía con reintentos (1, 5, 15 min, 1, 3 y 6 h). Si SendGrid está caído, el ticket se guarda igual y el correo sale cuando vuelva. Los correos se arman con el estado **actual** del ticket al enviarlos.
- **Estados y transiciones salen del front.** `WorkflowCatalog.g.cs` se generó desde `js/constants.js` (`tools/generar_workflow.js`) y coincide con lo que sembró `03_catalogos.sql` (13 estados, 32 transiciones). Una prueba lo comprueba.

## 5. La API

Todas las respuestas son JSON. Los errores usan `application/problem+json` con mensajes en español (`detail`, y `errors` por campo cuando aplica), que es lo que el front ya sabe mostrar.

**Públicos** (sin sesión):

| Método y ruta | Qué hace | Límite por IP |
|---|---|---|
| `GET /api/catalogs` | carreras, recintos, tipos de equipo, etc. | general |
| `POST /api/tickets` | crea el ticket (`multipart`: parte `data` en JSON + `files`) | 15 / 10 min |
| `POST /api/tickets/track` | consulta con código **y** correo | 40 / 5 min |
| `POST /api/tickets/{code}/quote-decision` | el cliente aprueba o rechaza la cotización | 40 / 5 min |
| `POST /api/tickets/{code}/payment-proof` | el cliente envía referencia y comprobante (`multipart`) | 40 / 5 min |
| `GET /api/files/{id}?e=…&s=…` | descarga un adjunto con **enlace firmado** (vale 10 min) | general |
| `POST /api/auth/login` | inicia sesión del personal | 15 / 5 min |

**Del personal** (`Authorization: Bearer <token>`):

| Método y ruta | Qué hace |
|---|---|
| `GET /api/admin/tickets` · `/stats` · `/{id}` | lista con filtros y paginación, estadísticas, detalle |
| `POST /api/admin/tickets/{id}/status` | cambia el estado (con motivo cuando es obligatorio) |
| `PUT /api/admin/tickets/{id}/quote` | crea o actualiza la cotización |
| `POST /api/admin/tickets/{id}/quote-decision` | registra la decisión del cliente por teléfono/WhatsApp |
| `POST /api/admin/tickets/{id}/payment` | cambia el estado del pago |
| `PUT /api/admin/tickets/{id}/assignee` | asigna responsable |
| `POST /api/admin/tickets/{id}/notes` | agrega una nota interna |
| `GET /api/admin/staff` | personal activo (selector «Asignar a») |

## 6. Reglas del negocio

El flujo, tal como lo recorre la prueba de extremo a extremo:

1. El cliente crea el ticket. **No se paga nada al crearlo.** Recibe un correo con su código y un enlace.
2. El personal pasa el ticket a **En revisión** y guarda la **cotización** (solo editable en «En revisión» o «Cotizado»; monto `0` = sin costo).
3. **Cotizado** es un paso interno: el cliente aún no la ve (y su pantalla sigue diciendo «En revisión»). Al pasar a **Esperando aprobación** se **publica** y se le avisa por correo.
4. El cliente la aprueba o rechaza desde «Consultar ticket». Si **rechaza**, el ticket pasa a **Cancelado**. Si **aprueba** con monto mayor a cero, el pago queda **Pendiente**. El personal también puede registrar la aprobación si el cliente la dio por teléfono.
5. El trabajo solo inicia con la cotización aprobada: el servicio no deja pasar a **En reparación / En desarrollo** antes, y el trigger de la base de datos lo exige además para Esperando repuestos y Pruebas.
6. Con transferencia, el cliente registra su referencia (y un comprobante opcional) → pago **Comprobante enviado**; el personal lo marca **Pagado**.
7. **Entregado** no se permite con un pago pendiente (si la cotización tiene monto): lo bloquea el servicio y, aunque alguien lo saltara, el trigger de la base de datos.
8. Un ticket **Cancelado** puede reabrirse (vuelve a En revisión y la cotización anterior queda descartada); no si ya está pagado.
9. Las **notas internas** y los cambios internos nunca se muestran al cliente.

**Adjuntos.** Solo imágenes (JPG, PNG, GIF, WEBP) y PDF, hasta 5 archivos de 5 MB. El tipo se comprueba por el **contenido** del archivo (los primeros bytes), no por la extensión: un texto renombrado a `.png` se rechaza (la prueba del navegador lo comprobó). Las solicitudes de hardware aceptan imágenes; los PDF solo en software y en comprobantes de pago. Se guardan fuera de `wwwroot` (`storage/attachments`) con un nombre aleatorio, y solo se descargan con enlaces firmados.

## 7. Correos (SendGrid)

Correos al **cliente**: solicitud recibida, cambios de estado visibles (en revisión, en reparación, listo para entrega, entregado…), cotización lista, cotización aprobada (con el monto y las instrucciones de pago), cancelación (también cuando rechaza la cotización), comprobante recibido y pago confirmado. Al **personal** (`Email:StaffNotifyTo`): ticket nuevo, cotización aprobada, comprobante recibido.

- `Email:Provider`: `SendGrid` (envía de verdad), `Console` (solo escribe en la consola, para desarrollo) o `None`.
- Los correos llevan texto simple y HTML. El seguimiento de clics y de aperturas de SendGrid está **desactivado** en cada envío (los enlaces llegan tal cual, sin pasar por un redireccionador).
- En el modo `Console` las direcciones salen enmascaradas (`e***@gmail.com`); en ningún modo se escriben teléfonos, tokens ni la clave de SendGrid. Si SendGrid rechazara un envío, el texto de su error se guarda en `NotificacionCorreo.UltimoError` y en el registro, y podría contener la dirección: trata los registros como datos con acceso restringido.
- El enlace del correo apunta a `App:PublicBaseUrl` (ponlo con `https` y tu dirección real al publicar).
- El correo de «Cotización aprobada» incluye las cuentas de `Payment:BankAccounts` (banco, número y titular), igual que la pantalla «Cómo pagar»: por eso importa poner los datos reales (sección 10).

## 8. Seguridad

- Contraseñas con **BCrypt** (factor 12). Bloqueo de cuenta tras 5 intentos fallidos durante 15 minutos (`Login:*`), y límite de peticiones por IP en login, creación y consultas. El login responde **igual** si el usuario no existe, la contraseña falla o la cuenta está bloqueada (mismo mensaje, mismo código 401 y tiempo parecido), para no revelar qué usuarios existen. Costo conocido del bloqueo temporal: quien conozca un nombre de usuario puede mantenerlo bloqueado fallando 5 veces cada 15 minutos (el límite por IP, 15 intentos cada 5 min, no lo impide). Si eso te preocupa, la salida es Turnstile en el login o bloquear por usuario **e** IP; dímelo y lo cambiamos.
- La API usa el rol `rol_unitech_api` (mínimos permisos; no puede borrar ni modificar el historial, y de `Personal` solo actualiza los contadores de acceso).
- Todo el SQL está parametrizado: ningún dato del usuario se concatena en una sentencia.
- Cabeceras de seguridad y **CSP estricta** (sin scripts ni estilos en línea); el navegador no registró ninguna violación al recorrer todo el flujo.
- Límites de tamaño por ruta (64 KB para JSON; solo las rutas de subida permiten archivos).
- Honeypot anti-bots (solo frena a los bots que llenan el formulario; quien llama directo a la API simplemente no envía ese campo) y, opcional, **Cloudflare Turnstile**: pon la clave secreta en `Captcha:TurnstileSecret` (user-secrets) **y** la *site key* en `js/config.js` → `captcha.siteKey` (si pones solo una, nadie podrá enviar el formulario). Sin Turnstile cualquiera puede crear tickets con el correo de otra persona, y esa persona recibirá los correos del ticket: la API lo avisa al arrancar en producción.
- Límites por IP: las direcciones IPv6 se agrupan por /64. Detrás de un proxy hay que activar `Hosting:TrustForwardedHeaders` (si no, toda la universidad compartiría un solo contador); en ese caso **la API no debe ser alcanzable directamente**, solo a través del proxy, o alguien podría falsificar la cabecera `X-Forwarded-For`.
- El token del personal dura 60 minutos (`Jwt:ExpiresMinutes`) y el front lo guarda en `sessionStorage`. Cerrar sesión borra el token del navegador, pero un JWT sin estado no se puede «anular» en el servidor: para cortar un acceso inmediatamente, desactiva la cuenta en la base (se aplica en menos de 30 s).
- Qué **no** hace (por decisión de alcance): recuperación de contraseña, 2FA, ni administración de personal desde la web. Tampoco distingue permisos por rol: todo el personal activo (`TECNICO`, `DESARROLLADOR`, `ADMIN`) puede hacer lo mismo en el panel, incluida la confirmación de pagos.

**Limitación conocida: el código del ticket es secuencial (`UTD-1001`, `UTD-1002`…).** Para ver o responder un ticket hacen falta el código **y** el correo; como los códigos se pueden adivinar, en la práctica la protección es el correo (más el límite de 40 consultas cada 5 minutos por IP). Quien conozca el correo de otra persona podría ver su cotización y datos de pago, o rechazar su cotización. Los datos más sensibles (teléfono, nombre del personal, notas internas) no se muestran en esa vista. La solución completa es dar a cada ticket un código de acceso aleatorio (se envía por correo y se pide al consultar); implica cambiar el formulario del front y la tabla `Ticket`. Si el sistema va a recibir clientes externos reales, te recomiendo hacerlo antes de publicar.

## 9. Qué se verificó y qué no

**Lo que sí se hizo** (en un entorno sin SQL Server, sin Docker y sin acceso a NuGet):

- **413 pruebas automáticas** correctas: reglas, servicios, flujo público y del personal, correos (incluido el JSON exacto que se envía a SendGrid), seguridad, JWT, el servidor HTTP real (Kestrel) de punta a punta, y la forma del SQL.
- **Prueba de extremo a extremo con un Chromium real** (27 pasos) contra este backend y tu front: crear ticket con foto, rechazo de archivo falso, consulta (y que un correo equivocado no revele nada), login, cotizar `3,000` (queda `3,000.00`), publicar, aprobar, pagar con comprobante PDF, confirmar pago, entregar, rechazo y reapertura, notas internas ocultas, bloqueo por intentos. Cero errores de JavaScript ni de CSP.
- **Revisión independiente** (tres revisores que leyeron el código por separado: seguridad, SQL contra tus scripts y contrato front↔API). No encontraron fallos críticos; todo lo corregible se corrigió (reintentos que podían duplicar una nota o una versión de cotización, mensaje de bloqueo que revelaba usuarios, el estado interno «Cotizado» visible al cliente, límites IPv6, validaciones de arranque, actualización de la pantalla tras un conflicto 409, etc.). Lo que **no** se cambió está en la sección 8 («Limitación conocida») y en la lista de abajo.
- **Verificación estática del SQL** (`tools/verificar_sql.py`): se capturó cada sentencia SQL que el código ejecuta (con una conexión de mentira que las graba) y se contrastó con tus scripts: que cada tabla y columna exista, que los `INSERT` incluyan las columnas obligatorias, que los parámetros coincidan con el SQL, que los valores de código existan en los catálogos y que cada `UPDATE` tenga `WHERE`. 1 172 comprobaciones, 0 errores (y se comprobó que el verificador detecta errores inyectándole fallos a propósito).

**Lo que NO se pudo verificar — y por qué importa:**

1. **El SQL nunca se ejecutó contra un SQL Server real.** Las pruebas usan una base **en memoria** que imita las restricciones y los triggers de tu esquema; la comprobación estática detecta columnas, tipos y parámetros mal puestos, pero no sustituye ejecutar. Puntos concretos donde un servidor real podría sorprender: el comportamiento de `ROLLBACK` + `THROW` dentro de los triggers, `OUTPUT` a través de una expresión de tabla (CTE), y el orden en que se evalúan los triggers.
2. **Los paquetes no se descargaron de NuGet aquí.** `Microsoft.Data.SqlClient`, `BCrypt.Net-Next`, `Microsoft.IdentityModel.JsonWebTokens` y las de pruebas (`xunit`, `Microsoft.NET.Test.Sdk`) se probaron con piezas equivalentes; el código que toca el proveedor real de SQL son 35 líneas (`Infrastructure/SqlServer.cs`) con API estándar, pero es posible que tu primer `dotnet build` pida corregir una versión.
3. **Las pruebas se ejecutaron con un ejecutor propio** compatible con xunit 2.x, no con `dotnet test` real. Debería funcionar igual; si `dotnet test` da algún problema de configuración, es del proyecto de pruebas, no de la aplicación.
4. **SendGrid real:** el formato de la petición está probado con un servidor falso, no con tu cuenta. `test-email` lo comprueba de verdad.

**Tres cosas que conviene mirar en la primera ejecución real** (los revisores no vieron un fallo, pero no se pueden comprobar sin SQL Server):

1. *Error 1934 (ARITHABORT).* `dbo.Ticket` tiene índices sobre una columna calculada. Si al crear el primer ticket SQL Server responde «INSERT failed because the following SET options have incorrect settings: 'ARITHABORT'», hay que anteponer `SET ARITHABORT ON;` a las sentencias (en `Db.Command`, `UniTechDesk.Data/Db.cs`). Lo esperado es que no pase (ANSI_WARNINGS ya lo implica).
2. *La intercalación `Modern_Spanish_100_CI_AI`* de `00_crear_base.sql`: si el script falla con el error 448, el nombre no existe en tu versión; puedes quitar el `COLLATE` y usar la del servidor.
3. *Los pasos 6 y 7 de `07_pruebas.sql`* (los que prueban los triggers) deben mostrar ✓; si no, copia el mensaje completo.

Además: `07_pruebas.sql` consume números de ticket, así que ejecuta `06` **antes** que `07` y no te extrañes si tus primeros tickets reales no empiezan en `UTD-1001`.

**Cómo confirmarlo tú en 10 minutos:**

```powershell
dotnet build                                   # 1) compila con los paquetes reales
dotnet test                                    # 2) las 413 pruebas con xunit real
cd src\UniTechDesk.Api
dotnet run -- check                            # 3) conexión, datos base y cuentas de demostración
dotnet run -- test-email tucorreo@gmail.com    # 4) SendGrid de verdad
dotnet run                                     # 5) recorre el flujo en el navegador (sección 6) con los tickets de demo
```

Si 5 funciona, el SQL está validado en lo que importa. Si algo falla, **copia el mensaje completo** (la API devuelve un error genérico al navegador y deja el detalle técnico en la consola del servidor).

Opcional, para quien quiera repetir la verificación estática:

```powershell
pip install sqlglot
$env:UTD_SQL_DUMP = "$PWD\sqldump.json"; dotnet test --filter "FullyQualifiedName~SqlShapeTests"
python tools\verificar_sql.py --db <carpeta-database-de-tu-bd> --dump sqldump.json --src src\UniTechDesk.Core\Domain
```

## 10. Antes de publicar

- [ ] **Cuentas de demostración**: desactiva o cambia la contraseña de `admin`, `tecnico1`, `tecnico2`, `desarrollador1` y no ejecutes `06_datos_demo.sql` en producción.
- [ ] **Datos bancarios reales** en `Payment:BankAccounts`. Si dejas los de ejemplo, los clientes verán cuentas falsas (la API lo avisa al arrancar). Ejemplo con variables de entorno: `Payment__BankAccounts__0__Bank`, `…__Number`, `…__Holder`, `…__Currency` (`NIO` o `USD`).
- [ ] **Secretos por variables de entorno** (el doble guion bajo reemplaza los `:`): `ConnectionStrings__UniTechDesk`, `Jwt__SigningKey`, `Email__SendGridApiKey`, `Email__Provider=SendGrid`, `Email__FromEmail`. Con `ASPNETCORE_ENVIRONMENT=Production`.
- [ ] **Turnstile** (sección 8): `Captcha:TurnstileSecret` + `captcha.siteKey`.
- [ ] **HTTPS** y `App:PublicBaseUrl` con tu dirección `https://…`. Si hay un proxy inverso delante (IIS, nginx, Azure…), activa `Hosting:TrustForwardedHeaders=true` para que los límites por IP vean la IP real del cliente; si no, todos parecerían la misma IP. Activa `Hosting:RedirectToHttps` si el proxy no lo hace.
- [ ] `AllowedHosts`: cambia `*` por tu dominio.
- [ ] Certificado válido para SQL Server y **quita** `TrustServerCertificate=True`.
- [ ] Respaldos de la base **y** de la carpeta `storage/attachments` (los adjuntos están en disco, no en la base).
- [ ] Remitente de correo con dominio propio y *Domain Authentication* en SendGrid (sección 3.4).
- [ ] Datos de contacto reales en `js/config.js` (WhatsApp, teléfono) y `App:ContactLine` si quieres un pie en los correos.
- [ ] Plan de SendGrid después del 4 de diciembre de 2026.

## 11. Problemas frecuentes

| Síntoma | Causa probable |
|---|---|
| Al arrancar: «Falta Jwt:SigningKey…» | no guardaste el secreto del paso 3.3, o arrancaste sin `dotnet run` desde `src\UniTechDesk.Api` |
| `check`: no se pudo usar la base de datos | servidor/instancia incorrectos, `unitech_api` sin contraseña correcta, o no está en `rol_unitech_api` (paso 3.2). Si dice *Login failed*, revisa la autenticación mixta |
| `check`: «Personal activo: 0» | no cargaste `06_datos_demo.sql` ni creaste un usuario (paso 3.5) |
| `test-email` falla con 401/403 | la API key no tiene permiso «Mail Send» o está mal copiada |
| `test-email` funciona pero no llega | el remitente no está verificado, o cayó en spam (Gmail como remitente: ver 3.4) |
| El cliente no recibe correos pero el ticket se crea | normal si `Email:Provider` es `Console` o `None`; los pendientes se ven en `dbo.NotificacionCorreo` (`Estado`, `Intentos`, `UltimoError`) |
| Error 429 al probar mucho | el límite por IP funciona; espera unos minutos o reinicia el servidor en desarrollo |
| El front sigue usando datos de mentira | no corriste `sync-frontend` (sección 3.6) o el navegador guardó `config.js` en caché: recarga con Ctrl+F5 |
| Los PDF o archivos `.txt/.docx/.xlsx` no se aceptan en el formulario | es a propósito: la base solo admite imágenes y PDF (sección 12) |
| «Otra persona modificó este ticket» | alguien cambió el ticket mientras lo tenías abierto; presiona Actualizar |

## 12. Cambios al frontend

**a) Los cuatro ajustes de `sync-frontend`** (los aplica sobre una **copia**; tu carpeta de origen no se modifica):

| Archivo | Cambio | Por qué |
|---|---|---|
| `js/config.js` | `useMock: true` → `false` | usar la API real |
| `js/config.js` | `notifications.email: false` → `true` | el backend sí envía correos, y el front lo dice a los clientes |
| `index.html` | quita la carga de `js/mock-api.js` | el simulador solo era para demostración |
| `js/views/new-ticket.js` | documentos permitidos `['pdf','txt','docx','xlsx']` → `['pdf']` | la base solo admite imágenes y PDF (`CK_Adjunto_Mime`) |

**El último cambia lo que tu front aceptaba:** antes se podían adjuntar `.txt`, `.docx` y `.xlsx`; ahora no. Si los necesitas, hay que ampliar el `CHECK` `CK_Adjunto_Mime` de la base **y** la lista de tipos permitidos en `UniTechDesk.Core/Files` (con su comprobación por contenido); avísame y lo hacemos juntos.

**b) Correcciones hechas en el propio código del front** (vienen en `Front-corregido.zip`; la carpeta `wwwroot` ya las trae). Salieron de la revisión y no dependen del backend, así que también valen con el simulador:

| Archivo | Corrección |
|---|---|
| `js/views/admin.js` | si el servidor responde 409 (otra persona cambió el ticket o el paso ya no aplica), el diálogo se **recarga** con los datos nuevos en vez de quedarse con la vista vieja |
| `js/views/track.js` | lo mismo al enviar el comprobante de pago |
| `js/views/admin.js` | el login muestra el mensaje del servidor (explica el bloqueo temporal); la persona asignada que ya no esté activa aparece en la lista («inactivo») para que guardar no la quite sin querer |
| `js/ui.js` | el aviso de «demasiados intentos» dice «unos minutos» (el límite real dura 5) |
| `js/views/new-ticket.js` | el texto de ayuda de los adjuntos se arma con la lista real de documentos permitidos (ya no promete TXT/DOCX/XLSX); se agregan dos nombres de campo para que los errores del servidor se muestren junto al campo |
| `js/views/admin.js` | el diálogo de gestión usaba niveles de encabezado saltados (h3 → h4); ahora h2 → h3 (lo detectaba tu prueba de accesibilidad con axe-core, `tests/smoke.test.js`: ahora 49 de 49) |
| `js/utils.js` | la exportación CSV también neutraliza fórmulas precedidas de espacios |

Nota sobre el CSV: los teléfonos que empiezan con `+` salen con un apóstrofo delante (`'+505…`); es la protección contra «inyección de fórmulas» de Excel y se mantiene a propósito.
