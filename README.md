# API local para créditos MDB

API ASP.NET Core 8 para recibir el nombre del pagador, la banca de origen y el monto; guardar un JSON local y enviar una trama binaria al puerto serie mediante la DLL nativa `ComandosMDB`.

## Contrato Android

`POST http://10.0.2.2:5098/api/credits`

```json
{
  "user": "JOHN O",
  "application": "ALTOQUE",
  "amount": 1.00
}
```

La app usa HTTP Basic y envía una cabecera `Idempotency-Key`. Se aceptan `ALTOQUE`, `YAPE` y `BCP`. La API guarda cada evento como JSON en `LocalApi:OutputDirectory`; el nombre se calcula con SHA-256 de la clave de idempotencia. Las credenciales no se guardan en el repositorio: define `LocalApi__Username` y `LocalApi__Password` en el entorno de Windows. El servicio deja de iniciar si conserva el password de ejemplo.

## ComandosMDB

`native/ComandosMDB` contiene el proyecto C++ nativo extraído de `atorment-oliver/vending`. Esta DLL exporta `SendCommandMDB(portName, commands, length)`: abre el puerto COM y escribe los bytes indicados. La DLL original **no convierte un monto a una orden de crédito MDB**, no documenta el formato de la trama ni valida una respuesta MDB. Por eso la API necesita que se configure la plantilla hexadecimal completa y no trae una trama inventada.

Configura:

- `Mdb:DllPath`: ruta a `ComandosMDB.dll` de 64 bits.
- `Mdb:PortName`: puerto serie, por ejemplo `COM4`.
- `Mdb:CreditCommandHexTemplate`: trama hexadecimal completa para la máquina, separada por espacios. Puede incluir exactamente un formato de monto acordado con el protocolo existente: `{amountCentsBE16}`, `{amountCentsLE16}`, `{amountCentsBE32}` o `{amountCentsLE32}`. Estos marcadores solo convierten el monto a centavos y bytes; no describen ni garantizan un comando MDB válido.

Si DLL o plantilla están vacías, el endpoint conserva el JSON con estado `PendingMdbConfiguration` y responde `503`, sin tocar la máquina. Antes de ponerla en servicio, confirma la trama con el firmware/controlador de esa vending y prueba con la máquina aislada. El código nativo devuelve error de escritura cuando el puerto no abre o falla `WriteFile`, pero el diseño de origen no comprueba una confirmación funcional MDB.

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

## Compilación Windows x64

Requiere .NET 8 SDK, Visual Studio Build Tools con C++ y Windows SDK:

```powershell
msbuild native\ComandosMDB\ComandosMDB.vcxproj /p:Configuration=Release /p:Platform=x64
 dotnet restore ApiLocalCommand.csproj
 dotnet build ApiLocalCommand.csproj -c Release
```

El workflow de GitHub Actions compila la DLL x64 y la API en Windows. Para ejecutar como servicio, publica la API, coloca `ComandosMDB.dll` junto a ella o usa la ruta configurada, define las variables de entorno para la cuenta del servicio y registra el ejecutable publicado con `sc.exe`.

`web.config` solo hace falta si se publica detrás de IIS. Para esta etapa local como servicio de Windows, la configuración se mantiene en `appsettings.json` más variables de entorno.
