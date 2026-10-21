using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
var settings = ApiSettings.Load(builder.Configuration, builder.Environment.IsDevelopment());

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<CreditSender>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { status = "running", health = "/health", endpoint = "/api/credits" }));
app.MapGet("/health", () => Results.Ok(new { status = "running" }));
app.MapPost("/api/credits", async (
    HttpContext http,
    [FromBody] CreditRequest request,
    ApiSettings config,
    CreditSender sender,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!BasicAuth.IsValid(http.Request.Headers["Authorization"].ToString(), config.Username, config.Password))
    {
        http.Response.Headers["WWW-Authenticate"] = "Basic realm=\"VendingLocalApi\", charset=\"UTF-8\"";
        return Results.Unauthorized();
    }

    var bank = request.EffectiveBank.Trim().ToUpperInvariant();
    var user = request.User.Trim();
    if (string.IsNullOrWhiteSpace(bank) || bank.Length > 20)
        return Results.BadRequest(new { error = "bank es obligatorio y debe tener máximo 20 caracteres." });
    if (string.IsNullOrWhiteSpace(user) || user.Length > 100)
        return Results.BadRequest(new { error = "user es obligatorio y debe tener máximo 100 caracteres." });
    if (request.Amount <= 0 || request.Amount > config.MaxCreditAmount)
        return Results.BadRequest(new { error = $"amount debe ser mayor que cero y no superar {config.MaxCreditAmount}." });
    if (request.Amount % config.CreditUnitAmount != 0)
        return Results.BadRequest(new { error = $"amount debe ser múltiplo exacto de {config.CreditUnitAmount} por crédito." });

    var repeatCountDecimal = request.Amount / config.CreditUnitAmount;
    if (repeatCountDecimal > config.MaxCreditRepeats)
        return Results.BadRequest(new { error = $"La solicitud supera el máximo de {config.MaxCreditRepeats} créditos por llamada." });

    var repeatCount = decimal.ToInt32(repeatCountDecimal);

    try
    {
        // Una sola llamada nativa; ComandosMDB repite internamente la trama de una unidad.
        await sender.SendAsync(
            bank,
            user,
            request.Amount,
            repeatCount,
            cancellationToken);

        logger.LogInformation(
            "MDB terminó correctamente y se registró una transacción: {Bank}, {User}, {Amount}, {RepeatCount} repeticiones.",
            bank, user, request.Amount, repeatCount);

        return Results.Ok(new
        {
            status = "Logged",
            bank,
            user,
            amount = request.Amount,
            commandsSent = repeatCount,
            intervalMilliseconds = config.RepeatIntervalMilliseconds
        });
    }
    catch (CreditLogException ex)
    {
        logger.LogError(ex, "The MDB command was sent but the log file could not be written.");
        return Results.Problem(
            "El comando se envió, pero no se pudo guardar el log. No repitas la solicitud automáticamente.",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (InvalidOperationException ex)
    {
        logger.LogError(ex, "Credit request configuration or MDB command failed.");
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (FileNotFoundException ex)
    {
        logger.LogError(ex, "MDB native component was not found.");
        return Results.Problem("No se encontró ComandosMDB.dll. Compila la solución completa en x64 y verifica que la DLL esté junto al ejecutable.", statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (DllNotFoundException ex)
    {
        logger.LogError(ex, "MDB native component could not be loaded.");
        return Results.Problem("Windows no pudo cargar ComandosMDB.dll o alguna de sus dependencias. Revisa la salida de compilación x64 y los runtimes de Visual C++.", statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (EntryPointNotFoundException ex)
    {
        logger.LogError(ex, "MDB export was not found.");
        return Results.Problem("La DLL no contiene la función SendCreditRepeatsMDB esperada. Recompila ComandosMDB desde la solución de este repositorio.", statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Credit request failed.");
        return Results.Problem("Falló el envío MDB. Revisa la ventana Output/Depuración de Visual Studio para ver la causa registrada.", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

sealed class CreditRequest
{
    public string Bank { get; init; } = "";
    // Campo aceptado para mantener compatibilidad con versiones anteriores del notificador.
    public string Application { get; init; } = "";
    public string User { get; init; } = "";
    public decimal Amount { get; init; }
    public string EffectiveBank => string.IsNullOrWhiteSpace(Bank) ? Application : Bank;
}

sealed class ApiSettings
{
    public required string ListenUrl { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string LogPath { get; init; }
    public required string DllPath { get; init; }
    public required string PortName { get; init; }
    public required string CreditCommandHex { get; init; }
    public decimal CreditUnitAmount { get; init; }
    public decimal MaxCreditAmount { get; init; }
    public int RepeatIntervalMilliseconds { get; init; }
    public int MaxCreditRepeats { get; init; }

    public static ApiSettings Load(IConfiguration configuration, bool isDevelopment)
    {
        var username = configuration["LocalApi:Username"] ?? "";
        var password = configuration["LocalApi:Password"] ?? "";

        // Credenciales cómodas solo para F5/Development; nunca se aceptan en producción.
        if (isDevelopment)
        {
            if (string.IsNullOrWhiteSpace(username))
                username = "vending-reader";
            if (string.IsNullOrWhiteSpace(password) ||
                password.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
                password = "dev-only-vending-password";
        }

        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) ||
            password.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Configura LocalApi:Username y LocalApi:Password en appsettings.json o mediante variables de entorno. En Visual Studio, ejecuta con el perfil Development.");
        }

        var unitAmount = decimal.TryParse(
            configuration["Mdb:CreditUnitAmount"],
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsedUnit) && parsedUnit > 0 ? parsedUnit : 1m;

        return new ApiSettings
        {
            ListenUrl = configuration["LocalApi:ListenUrl"] ?? "http://127.0.0.1:5098",
            Username = username,
            Password = password,
            LogPath = configuration["LocalApi:CreditLogPath"] ?? Path.Combine(AppContext.BaseDirectory, "credits.log"),
            DllPath = configuration["Mdb:DllPath"] ?? "ComandosMDB.dll",
            PortName = configuration["Mdb:PortName"] ?? "COM4",
            CreditCommandHex = configuration["Mdb:CreditCommandHex"] ?? "",
            CreditUnitAmount = unitAmount,
            MaxCreditAmount = decimal.TryParse(configuration["LocalApi:MaxCreditAmount"], NumberStyles.Number, CultureInfo.InvariantCulture, out var max) && max > 0 ? max : 500m,
            RepeatIntervalMilliseconds = int.TryParse(configuration["Mdb:RepeatIntervalMilliseconds"], out var delay) ? Math.Clamp(delay, 30, 5000) : 400,
            MaxCreditRepeats = int.TryParse(configuration["Mdb:MaxCreditRepeats"], out var repeats) ? Math.Clamp(repeats, 1, 500) : 50
        };
    }
}

sealed class CreditSender(ApiSettings settings)
{
    private static readonly SemaphoreSlim TransactionLock = new(1, 1);

    public async Task SendAsync(string bank, string user, decimal amount, int repeatCount, CancellationToken cancellationToken)
    {
        await TransactionLock.WaitAsync(cancellationToken);
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("ComandosMDB requiere Windows x64.");

            var dllPath = Path.IsPathRooted(settings.DllPath)
                ? settings.DllPath
                : Path.Combine(AppContext.BaseDirectory, settings.DllPath);
            if (!File.Exists(dllPath))
                throw new FileNotFoundException("No se encontró ComandosMDB.dll.", dllPath);

            var command = ParseHexCommand(settings.CreditCommandHex);
            // SendNative se invoca exactamente una vez. La DLL ejecuta el bucle de repeticiones.
            var result = await Task.Run(
                () => SendNative(dllPath, settings.PortName, command, repeatCount, settings.RepeatIntervalMilliseconds),
                cancellationToken);
            if (result != 0)
                throw new InvalidOperationException($"ComandosMDB devolvió el código {result}.");

            // Registrar una sola línea por transacción y solo después de completar el envío nativo.
            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            var logLine = $"{timestamp}|{Clean(bank)}|{Clean(user)}|{amount.ToString("0.##", CultureInfo.InvariantCulture)}{Environment.NewLine}";
            var fullLogPath = Path.GetFullPath(settings.LogPath);
            var directory = Path.GetDirectoryName(fullLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            try
            {
                await File.AppendAllTextAsync(
                    fullLogPath,
                    logLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                throw new CreditLogException("MDB write completed, but credit log append failed.", ex);
            }
        }
        finally
        {
            TransactionLock.Release();
        }
    }

    private static int SendNative(string dllPath, string portName, byte[] command, int repeatCount, int intervalMilliseconds)
    {
        var handle = NativeLibrary.Load(dllPath);
        try
        {
            var send = Marshal.GetDelegateForFunctionPointer<SendRepeatedCommandDelegate>(
                NativeLibrary.GetExport(handle, "SendCreditRepeatsMDB"));
            return send(portName, command, (UIntPtr)(uint)command.Length, repeatCount, intervalMilliseconds);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    private static byte[] ParseHexCommand(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new InvalidOperationException("Configura Mdb:CreditCommandHex con la trama hexadecimal de un crédito.");
        var tokens = hex.Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > 256)
            throw new InvalidOperationException("El comando debe tener entre 1 y 256 bytes.");
        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? tokens[i][2..] : tokens[i];
            if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                throw new InvalidOperationException("Mdb:CreditCommandHex debe contener bytes hexadecimales separados por espacios.");
        }
        return bytes;
    }

    private static string Clean(string value) =>
        value.Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
delegate int SendRepeatedCommandDelegate(
    [MarshalAs(UnmanagedType.LPStr)] string portName,
    [In] byte[] commands,
    UIntPtr length,
    int repeatCount,
    int intervalMilliseconds);

sealed class CreditLogException(string message, Exception innerException) : Exception(message, innerException);

static class BasicAuth
{
    public static bool IsValid(string header, string configuredUser, string configuredPassword)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var separator = value.IndexOf(':');
            if (separator <= 0)
                return false;
            var user = Encoding.UTF8.GetBytes(value[..separator]);
            var password = Encoding.UTF8.GetBytes(value[(separator + 1)..]);
            return ConstantTimeEquals(user, Encoding.UTF8.GetBytes(configuredUser)) &&
                   ConstantTimeEquals(password, Encoding.UTF8.GetBytes(configuredPassword));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool ConstantTimeEquals(byte[] left, byte[] right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}
