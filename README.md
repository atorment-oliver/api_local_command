# API local para créditos MDB

Proyecto .NET 8 para compilar desde Visual Studio y desplegar manualmente en IIS.

## Código fuente

- API y endpoint: [Program.cs](Program.cs)
- Solución para Visual Studio: [ApiLocalCommand.sln](ApiLocalCommand.sln)
- Proyecto .NET: [ApiLocalCommand.csproj](ApiLocalCommand.csproj)
- Componente nativo MDB: [native/ComandosMDB](native/ComandosMDB)
- Configuración: [appsettings.json](appsettings.json)

## Funcionamiento

1. Recibe banca, usuario y monto en `POST /api/credits`.
2. Valida usuario y contraseña HTTP Basic configurados en `appsettings.json` o mediante variables de entorno.
3. Calcula la cantidad de créditos según `Mdb:CreditUnitAmount`. Con el valor predeterminado de 1, Bs 10 equivale a 10 créditos.
4. Con `Mdb:SimulationMode: true`, usa el método de prueba, que no carga ni llama la DLL.
5. Con `Mdb:SimulationMode: false`, invoca `SendCreditRepeatsMDB` de `ComandosMDB.dll`. La DLL envía una trama por crédito y espera el intervalo configurado entre tramas.
6. Solo después de que la simulación o el envío MDB finaliza correctamente, agrega una línea al log con formato `fecha|banca|usuario|monto`.

La trama cargada desde el código compartido es `08 52 00 5A` (4 bytes por crédito). El intervalo configurado es **400 ms (0,4 segundos)**. Por ejemplo, un request por Bs 10 genera 10 envíos de esa trama, separados por 400 ms.

La respuesta `status: "Simulated"` indica que se procesó la prueba y se guardó el log; no se envió crédito a la vending. `status: "Logged"` indica que la DLL terminó sin reportar error y se guardó el log. El componente MDB no confirma en la API que la máquina haya aceptado físicamente cada crédito.

## Request

JSON para `POST /api/credits`:

```json
{
  "bank": "ALTOQUE",
  "user": "JOHN",
  "amount": 10
}
```

También se acepta `application` en lugar de `bank` para compatibilidad con el notificador Android. La autenticación es HTTP Basic usando las credenciales configuradas en la API.

## Configuración del puerto y la trama

El puerto COM y la trama se configuran en `appsettings.json`, dentro de `Mdb`. No se envían dentro del request HTTP. La API lee esos valores al iniciar:

```json
"Mdb": {
  "DllPath": "ComandosMDB.dll",
  "PortName": "COM3",
  "CreditCommandHex": "08 52 00 5A",
  "SimulationMode": true,
  "CreditUnitAmount": 1,
  "RepeatIntervalMilliseconds": 400,
  "MaxCreditRepeats": 50
}
```

- `Mdb:PortName`: puerto serial del adaptador MDB. El valor inicial `COM3` corresponde al código compartido; si Windows asigna otro puerto, reemplázalo aquí, por ejemplo `COM4`.
- `Mdb:CreditCommandHex`: trama hexadecimal de un crédito. Cada envío usa exactamente estos bytes.
- `Mdb:SimulationMode`: `true` ejecuta el método de prueba sin cargar la DLL; `false` activa ComandosMDB.
- `Mdb:CreditUnitAmount`: monto representado por cada trama; predeterminado `1`.
- `Mdb:RepeatIntervalMilliseconds`: espera entre envíos; predeterminada `400` ms.
- `Mdb:MaxCreditRepeats`: máximo de créditos permitidos en una llamada.

Puedes cambiar `PortName` y `CreditCommandHex` y volver a iniciar la API; no necesitas recompilar. En IIS, reinicia el sitio o recicla el Application Pool para que cargue la configuración nueva.

## Resto de la configuración

- `LocalApi:Username` y `LocalApi:Password`: credenciales Basic Auth. Configura una contraseña propia; el marcador `CHANGE_ME` no se acepta en producción.
- `LocalApi:ListenUrl`: dirección local para el perfil Kestrel.
- `LocalApi:CreditLogPath`: ruta del archivo de log; predeterminada `C:\\Vending\\credits.log`.
- `LocalApi:MaxCreditAmount`: monto máximo permitido por request.
- `Mdb:DllPath`: ruta a `ComandosMDB.dll`, normalmente junto a la API publicada.

Para IIS, configura las credenciales mediante variables de entorno `LocalApi__Username` y `LocalApi__Password`. Da a la identidad del Application Pool permiso de escritura en la carpeta del log y acceso al puerto COM.

## Compilar y publicar desde Visual Studio

Requisitos: Visual Studio 2022 con las cargas de trabajo **ASP.NET y desarrollo web** y **Desarrollo para el escritorio con C++**, además del SDK .NET 8 y Windows SDK.

1. Clona el repositorio y abre `ApiLocalCommand.sln`.
2. Selecciona `Release` y `x64`.
3. Ejecuta **Build > Build Solution** para compilar la API y ComandosMDB.
4. Publica el proyecto `ApiLocalCommand` a una carpeta para Windows x64. Para alojarlo en IIS, usa despliegue dependiente del framework e instala el **ASP.NET Core Hosting Bundle de .NET 8**.
5. En IIS, usa un Application Pool **No Managed Code** y confirma que `ComandosMDB.dll` esté junto a los archivos publicados.
6. Para probar solo el API, deja `SimulationMode` en `true`. Para enviar créditos reales, verifica primero puerto, DLL y trama, luego cambia a `false`.

El perfil IIS Express usa `https://localhost:44390`; el perfil Kestrel de desarrollo usa `http://127.0.0.1:5098`. El workflow de GitHub Actions compila la solución en Windows x64.
