# API local para créditos MDB

Proyecto .NET 8 para compilar desde Visual Studio y desplegar manualmente en IIS.

## Código fuente

- API y endpoint: [Program.cs](Program.cs)
- Solución para Visual Studio: [ApiLocalCommand.sln](ApiLocalCommand.sln)
- Proyecto .NET: [ApiLocalCommand.csproj](ApiLocalCommand.csproj)
- Componente nativo MDB: [native/ComandosMDB](native/ComandosMDB)
- Configuración: [appsettings.json](appsettings.json)

## Qué hace

1. Recibe banca, usuario y monto en `POST /api/credits`.
2. Valida usuario y contraseña HTTP Basic configurados en `appsettings.json` o en variables de entorno.
3. Calcula cuántas unidades de crédito corresponden al monto. Por ejemplo, con `Mdb:CreditUnitAmount = 1`, Bs 10 representa 10 tramas.
4. Con `Mdb:SimulationMode = true`, llama a un método vacío de la API que devuelve `true`; no carga ni invoca `ComandosMDB.dll`. Con simulación desactivada, la API llama una sola vez a `SendCreditRepeatsMDB`, y el bucle de repeticiones ocurre dentro de `ComandosMDB`.
5. Después de que la simulación o la llamada nativa termina correctamente, agrega una sola línea por transacción al archivo configurado en `LocalApi:CreditLogPath`, en formato `fecha|banca|usuario|monto`. Por ejemplo: `2026-10-20 17:30:00.000 -04:00|ALTOQUE|JOHN|10`.

`status: "Simulated"` confirma que la solicitud fue procesada por el método simulado y se guardó el log; no se envió crédito a la vending. Con simulación desactivada, `Logged` indica que se escribió la trama o las tramas en el puerto serie y se guardó el log. El controlador MDB no devuelve una confirmación funcional de crédito en el código disponible.

## Request

URL de prueba en Visual Studio:

- Perfil **IIS Express**: `https://localhost:44390/health`
- Perfil **ApiLocalCommand**: `http://127.0.0.1:5098/health`

Ambas rutas `/` y `/health` muestran el estado del servicio. Para enviar créditos, usa `POST` al mismo host y puerto con la ruta `/api/credits` (por ejemplo, `https://localhost:44390/api/credits` si ejecutas IIS Express).

JSON:

```json
{
  "bank": "ALTOQUE",
  "user": "JOHN",
  "amount": 10
}
```

Se acepta también el campo `application` en lugar de `bank` para compatibilidad con el notificador Android. La respuesta incluye el monto y `commandsSent`.

## Si Postman devuelve un error

Para probar solo el API sin el adaptador, deja `Mdb:SimulationMode` en `true`. La respuesta debe indicar `status: "Simulated"` y `simulated: true`; no se comunica con la vending. Para usar hardware real, cambia explícitamente a `false`, configura la trama, el puerto y compila x64.

## Configuración

En `appsettings.json`:

- `LocalApi:Username` y `LocalApi:Password`: credenciales Basic Auth. En Visual Studio, el perfil `ApiLocalCommand` ejecuta en `Development` y usa `vending-reader` / `dev-only-vending-password` si el password sigue como marcador de ejemplo. Estas credenciales son solo para depuración local.
- El perfil de inicio de Visual Studio define dirección y puerto para depuración: revisa `Properties/launchSettings.json`. El perfil IIS Express usa HTTPS en el puerto `44390`; el perfil `ApiLocalCommand` usa HTTP en `5098`.
- `LocalApi:CreditLogPath`: archivo de registro; predeterminado `C:\\Vending\\credits.log`.
- `LocalApi:MaxCreditAmount`: monto máximo por request.
- `Mdb:SimulationMode`: con `true`, el método vacío del API devuelve `true` y no llama la DLL. Está activado por defecto para esta etapa de pruebas. Cámbialo a `false` solo cuando vayas a probar el hardware real.
- `Mdb:DllPath`: DLL nativa, predeterminada junto a la API publicada como `ComandosMDB.dll` (solo se usa con simulación desactivada).
- `Mdb:PortName`: puerto del adaptador, por ejemplo `COM4`.
- `Mdb:CreditCommandHex`: bytes hexadecimales de una unidad de crédito, separados por espacios. Configura aquí la trama verificada para tu máquina.
- `Mdb:CreditUnitAmount`: valor de crédito por trama; predeterminado `1`.
- `Mdb:RepeatIntervalMilliseconds`: intervalo entre tramas; predeterminado `400` ms.
- `Mdb:MaxCreditRepeats`: cantidad máxima de tramas por request.

No se inventa la trama hexadecimal; debes colocar la trama válida de tu controlador MDB en `Mdb:CreditCommandHex`.

Para producción, configura credenciales reales mediante variables de entorno de Windows: `LocalApi__Username` y `LocalApi__Password`. La API rechaza el marcador `CHANGE_ME` fuera de `Development`.

## Compilar y publicar desde Visual Studio

Requisitos: Visual Studio 2022 con las cargas de trabajo **ASP.NET y desarrollo web** y **Desarrollo para el escritorio con C++**, además del SDK .NET 8 y Windows SDK.

1. Clona el repositorio y abre `ApiLocalCommand.sln`.
2. En la barra de Visual Studio, selecciona `Release` y `x64`.
3. Ejecuta **Build > Build Solution**. Se compilan la API y `ComandosMDB`; el proyecto copia `ComandosMDB.dll` a la carpeta de salida de la API.
4. En el Explorador de soluciones, haz clic derecho en `ApiLocalCommand` y selecciona **Publish** > **Folder**. Usa destino `win-x64` y despliegue dependiente del framework para IIS.
5. Copia el contenido publicado a la carpeta física del sitio IIS.

En el servidor instala el **ASP.NET Core Hosting Bundle de .NET 8**. Configura el Application Pool como **No Managed Code**, define las credenciales de la API y da permisos a la identidad del App Pool para escribir en la carpeta del log y acceder al puerto COM.

El workflow de GitHub Actions compila esta misma solución de Visual Studio en Windows x64.
