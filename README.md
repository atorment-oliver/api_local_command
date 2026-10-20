# API local para créditos MDB

API ASP.NET Core 8 para recibir el nombre del pagador, la banca de origen y el monto; guardar un JSON local y enviar una trama binaria al puerto serie mediante la DLL nativa `ComandosMDB`.

## Contrato Android

`POST http://10.0.2.2:5098/api/credits`

```json
{
  "user": "JOHN O",
  "bank": "ALTOQUE",
  "amount": 1.00
}
```

La app usa HTTP Basic y envía una cabecera `Idempotency-Key`. Se aceptan `ALTOQUE`, `YAPE` y `BCP` como banca. La API acepta el campo `bank` y también `application` para compatibilidad con la app Android. Cada evento queda en JSON en `LocalApi:OutputDirectory`; además, cada carga exitosa agrega una línea al archivo `LocalApi:CreditLogPath` con formato `fecha|banca|usuario|crédito`. Las credenciales no se guardan en el repositorio: define `LocalApi__Username` y `LocalApi__Password` en el entorno de Windows. El servicio deja de iniciar si conserva el password de ejemplo.

## ComandosMDB

`native/ComandosMDB` contiene el proyecto C++ nativo extraído de `atorment-oliver/vending`. La API interpreta `Mdb:CreditUnitAmount` como el valor acreditado por una trama. Por ejemplo, con unidad Bs 1, una solicitud de Bs 10 envía diez veces la misma trama hexadecimal. La DLL exporta `SendCreditRepeatsMDB(portName, commands, length, repeatCount, intervalMilliseconds)`: abre el puerto COM una vez, escribe cada trama por separado y espera entre envíos. La DLL original **no convierte un monto a una orden de crédito MDB**, no documenta el formato de la trama ni valida una respuesta MDB. Por eso la API necesita que se configure la plantilla hexadecimal completa y no trae una trama inventada.

Configura:

- `LocalApi:Username` y `LocalApi:Password`: credenciales Basic Auth. En producción se recomienda definirlas como variables de entorno (`LocalApi__Username`, `LocalApi__Password`); `appsettings.json` contiene un password de ejemplo que impide iniciar hasta configurarlo.
- `LocalApi:CreditLogPath`: ruta completa del archivo `.log`; por defecto `C:\\Vending\\credits.log`. El proceso necesita permiso de escritura en esa carpeta.
- `Mdb:DllPath`: ruta de la DLL; por defecto `ComandosMDB.dll` junto a la API publicada (x64).
- `Mdb:PortName`: puerto serie, por ejemplo `COM4`.
- `Mdb:CreditUnitAmount`: monto de crédito por trama (por defecto `1`). El monto recibido debe ser múltiplo exacto de esta unidad.
- `Mdb:RepeatIntervalMilliseconds`: pausa entre tramas (por defecto `400` ms, es decir 0,4 s; mínimo `30`, máximo `5000`).
- `Mdb:MaxCreditRepeats`: máximo de repeticiones aceptadas (por defecto `50`, máximo `500`).
- `Mdb:CreditCommandHexTemplate`: trama hexadecimal completa para la máquina, separada por espacios. Puede incluir exactamente un formato de monto acordado con el protocolo existente: `{amountCentsBE16}`, `{amountCentsLE16}`, `{amountCentsBE32}` o `{amountCentsLE32}`. Estos marcadores solo convierten el monto a centavos y bytes; no describen ni garantizan un comando MDB válido.

Si DLL o plantilla están vacías, el endpoint conserva el JSON con estado `PendingMdbConfiguration` y responde `503`, sin tocar la máquina. Antes de ponerla en servicio, confirma la trama con el firmware/controlador de esa vending y prueba con la máquina aislada. El JSON registra cuántas tramas se solicitaron. El código nativo devuelve error cuando el puerto no abre o falla `WriteFile`, pero no comprueba una confirmación funcional MDB; el estado `Logged` significa que la escritura serial terminó y se creó la línea del log, no que la vending haya confirmado el crédito.

## Seguridad y operación

- Kestrel escucha por defecto solo en `127.0.0.1:5098`; `10.0.2.2` del emulador estándar Android redirige al loopback del equipo anfitrión.
- La API requiere HTTP Basic, compara credenciales en tiempo constante, limita a 20 requests/minuto, valida campos y monto, guarda la clave de idempotencia como hash y evita concatenar comandos de shell.
- La API local usa HTTP solo sobre el loopback del equipo/emulador. No cambies `ListenUrl` para exponerla a la LAN o Internet: Basic Auth en HTTP no cifra las credenciales.
- El JSON guarda usuario, aplicación, monto, fecha, estado y errores técnicos; limita las salidas/errores locales a 2,000 caracteres.
- Ejecuta el servicio con una cuenta de Windows con acceso al directorio de registros y al puerto COM. No lo ejecutes como administrador salvo que el driver lo requiera.

## Configuración de desarrollo

En PowerShell:

```powershell
$env:LocalApi__Username = "vending-reader"
$env:LocalApi__Password = "reemplaza-por-una-clave-larga"
$env:Mdb__DllPath = "C:\Vending\ComandosMDB.dll"
$env:Mdb__PortName = "COM4"
$env:Mdb__CreditCommandHexTemplate = "<trama verificada para tu vending>"
```

`appsettings.json` también configura la carpeta de salida y el máximo aceptado. No comitees contraseñas reales ni la trama de producción si la consideras sensible.

## Compilar y publicar manualmente en IIS (Windows x64)

El código de la API está en [Program.cs](Program.cs); el proyecto está en [ApiLocalCommand.csproj](ApiLocalCommand.csproj). Para generar la carpeta publicable, instala .NET 8 SDK, Visual Studio Build Tools con el componente C++ y Windows SDK. Abre **Developer PowerShell for Visual Studio** en la carpeta clonada y ejecuta:

```powershell
.\publish-iis.ps1
```

El script compila `ComandosMDB.dll`, publica la API para `win-x64` y deja todo en `publish\iis`. El SDK genera el `web.config` de ASP.NET Core durante `dotnet publish`; la DLL nativa queda al lado de la API.

Copia el contenido de `publish\iis` a la carpeta física del sitio IIS. Instala el **ASP.NET Core Hosting Bundle de .NET 8** en el servidor, crea el sitio o aplicación IIS y usa un Application Pool con **No Managed Code**. Configura `LocalApi__Username` y `LocalApi__Password` como variables de entorno para la identidad del App Pool, y concede a esa identidad permisos de escritura en la carpeta del log y acceso al puerto COM. Configura también en `appsettings.json` el puerto, la trama hexadecimal verificada y la ruta de log.

Para compilar solamente sin preparar publicación, el workflow de GitHub Actions y estos comandos generan build de desarrollo:

```powershell
msbuild native\ComandosMDB\ComandosMDB.vcxproj /p:Configuration=Release /p:Platform=x64
dotnet restore ApiLocalCommand.csproj
dotnet build ApiLocalCommand.csproj -c Release
```

La salida de `dotnet build` es de compilación, no el paquete de despliegue IIS; para ese paquete usa `publish-iis.ps1`.
