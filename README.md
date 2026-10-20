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
3. Calcula cuántas unidades de crédito corresponden al monto. Por ejemplo, con `Mdb:CreditUnitAmount = 1`, Bs 10 significa 10 tramas.
4. Envía la misma trama hexadecimal una vez por unidad, con pausa predeterminada de 400 ms entre tramas.
5. Cuando termina la escritura serial correctamente, agrega una sola línea por transacción al archivo configurado en `LocalApi:CreditLogPath`, en formato `fecha|banca|usuario|monto`. Por ejemplo: `2026-10-20 17:30:00.000 -04:00|ALTOQUE|JOHN|10`.

El estado `Logged` confirma que se escribió la trama o las tramas en el puerto serie y se guardó el log. El controlador MDB no devuelve una confirmación funcional de crédito en el código disponible.

## Request

URL local predeterminada:

```
POST http://127.0.0.1:5098/api/credits
```

JSON:

```json
{
  "bank": "ALTOQUE",
  "user": "JOHN",
  "amount": 10
}
```

Se acepta también el campo `application` en lugar de `bank` para compatibilidad con el notificador Android. La respuesta incluye el monto y `commandsSent`.

## Configuración

En `appsettings.json`:

- `LocalApi:Username` y `LocalApi:Password`: credenciales Basic Auth. En Visual Studio, el perfil `ApiLocalCommand` ejecuta en `Development` y usa `vending-reader` / `dev-only-vending-password` si el password sigue como marcador de ejemplo. Estas credenciales son solo para depuración local.
- `LocalApi:ListenUrl`: dirección y puerto local de escucha.
- `LocalApi:CreditLogPath`: archivo de registro; predeterminado `C:\\Vending\\credits.log`.
- `LocalApi:MaxCreditAmount`: monto máximo por request.
- `Mdb:DllPath`: DLL nativa, predeterminada junto a la API publicada como `ComandosMDB.dll`.
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
