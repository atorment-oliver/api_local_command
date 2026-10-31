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
    var headerKey = http.Request.Headers["Idempotency-Key"].ToString().Trim();
    var bodyKey = request.TransactionId.Trim();
    if (!string.IsNullOrWhiteSpace(headerKey) &&
        !string.IsNullOrWhiteSpace(bodyKey) &&
        !string.Equals(headerKey, bodyKey, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Idempotency-Key y transactionId deben coincidir si ambos se envían." });

    var idempotencyKey = string.IsNullOrWhiteSpace(headerKey) ? bodyKey : headerKey;
    if (idempotencyKey.Length > 128 ||
        idempotencyKey.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_' and not '.' and not ':'))
        return Results.BadRequest(new { error = "Idempotency-Key debe tener hasta 128 caracteres alfanuméricos o . _ : -." });

    // El lector mantiene esta clave igual en cada retransmisión de una notificación.
    // Sin clave del cliente, se genera un ID nuevo para este POST.
    var transactionId = string.IsNullOrWhiteSpace(idempotencyKey)
        ? Guid.NewGuid().ToString("N")
        : idempotencyKey;

    try
    {
        var result = await sender.SendAsync(
            transactionId,
            bank,
            user,
            request.Amount,
            repeatCount,
            cancellationToken);

        if (result.Status == "OK")
        {
            logger.LogInformation(
                "Transacción {TransactionId} completada: {Bank}, {User}, {Amount}, {RepeatCount} créditos. Duplicada={Duplicate}.",
                transactionId, bank, user, request.Amount, repeatCount, result.Duplicate);

            return Results.Ok(new
            {
                transactionId,
                status = "OK",
                simulated = config.SimulationMode,
                duplicate = result.Duplicate,
                bank,
                user,
                amount = request.Amount,
                commandsSent = repeatCount,
                intervalMilliseconds = config.RepeatIntervalMilliseconds
            });
        }

        if (result.Status == "PENDIENTE")
        {
            logger.LogWarning("Transacción {TransactionId} sigue PENDIENTE; no se repetirá el comando.", transactionId);
            return Results.Json(new { transactionId, status = "PENDIENTE", error = result.Detail },
                statusCode: StatusCodes.Status409Conflict);
        }

        logger.LogError("Transacción {TransactionId} terminó con ERROR: {Error}", transactionId, result.Detail);
        return Results.Json(new { transactionId, status = "ERROR", error = result.Detail },
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (CreditAuditException ex)
    {
        logger.LogError(ex, "No se pudo iniciar el registro de auditoría de la transacción {TransactionId}.", transactionId);
        return Results.Json(new { transactionId, status = "ERROR", error = ex.Message },
            statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error no controlado en la transacción {TransactionId}.", transactionId);
        return Results.Json(new { transactionId, status = "ERROR", error = ex.GetBaseException().Message },
            statusCode: StatusCodes.Status500InternalServerError);
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
    // Alternativa al encabezado Idempotency-Key para clientes que envían la clave en JSON.
    public string TransactionId { get; init; } = "";
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
    public bool SimulationMode { get; init; }
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
            SimulationMode = bool.TryParse(configuration["Mdb:SimulationMode"], out var simulationMode) ? simulationMode : true,
            CreditUnitAmount = unitAmount,
            MaxCreditAmount = decimal.TryParse(configuration["LocalApi:MaxCreditAmount"], NumberStyles.Number, CultureInfo.InvariantCulture, out var max) && max > 0 ? max : 500m,
            RepeatIntervalMilliseconds = int.TryParse(configuration["Mdb:RepeatIntervalMilliseconds"], out var delay) ? Math.Clamp(delay, 30, 5000) : 400,
            MaxCreditRepeats = int.TryParse(configuration["Mdb:MaxCreditRepeats"], out var repeats) ? Math.Clamp(repeats, 1, 500) : 50
        };
    }
}

sealed class CreditSender(ApiSettings settings, ILogger<CreditSender> logger)
{
    private static readonly SemaphoreSlim TransactionLock = new(1, 1);

    public async Task<CreditTransactionResult> SendAsync(
        string transactionId,
        string bank,
        string user,
        decimal amount,
        int repeatCount,
        CancellationToken cancellationToken)
    {
        await TransactionLock.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindTransactionAsync(transactionId);
            if (existing is not null)
                return new CreditTransactionResult(existing.Status, existing.Detail, Duplicate: true);

            var transactionTime = DateTimeOffset.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.fff zzz",
                CultureInfo.InvariantCulture);
            var pending = new CreditLogEntry(
                transactionId,
                transactionTime,
                CleanField(bank),
                CleanField(user),
                amount.ToString("0.##", CultureInfo.InvariantCulture),
                "PENDIENTE",
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
                "Solicitud aceptada; comando aún no iniciado.");

            try
            {
                await AppendTransactionAsync(pending);
            }
            catch (Exception ex)
            {
                throw new CreditAuditException(
                    "No se pudo guardar el registro PENDIENTE; el comando no se ejecutó.",
                    ex);
            }

            try
            {
                await ExecuteCommandAsync(repeatCount);
            }
            catch (Exception commandException)
            {
                var error = CleanDetail(commandException.GetBaseException().Message);
                var failed = pending with
                {
                    Status = "ERROR",
                    UpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
                    Detail = error
                };
                try
                {
                    await ReplaceTransactionAsync(failed);
                }
                catch (Exception logException)
                {
                    logger.LogError(logException, "No se pudo actualizar la fila de auditoría {TransactionId}.", transactionId);
                    return new CreditTransactionResult(
                        "ERROR",
                        $"{error}. No se pudo actualizar el log; quedó PENDIENTE.",
                        Duplicate: false);
                }

                return new CreditTransactionResult("ERROR", error, Duplicate: false);
            }

            var completed = pending with
            {
                Status = "OK",
                UpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
                Detail = settings.SimulationMode
                    ? "Modo=simulación; no se envió comando físico."
                    : $"Modo=MDB; comandos={repeatCount}; intervaloMs={settings.RepeatIntervalMilliseconds}; la DLL terminó sin error."
            };
            try
            {
                await ReplaceTransactionAsync(completed);
                return new CreditTransactionResult("OK", completed.Detail, Duplicate: false);
            }
            catch
            {
                // El comando finalizó, pero la fila conserva PENDIENTE si no se pudo reemplazar.
                return new CreditTransactionResult(
                    "PENDIENTE",
                    "El comando terminó, pero no se pudo actualizar el log. Verifica antes de reintentar.",
                    Duplicate: false);
            }
        }
        finally
        {
            TransactionLock.Release();
        }
    }

    private async Task ExecuteCommandAsync(int repeatCount)
    {
        if (settings.SimulationMode)
        {
            if (!SimulateCommand(repeatCount, settings.RepeatIntervalMilliseconds))
                throw new InvalidOperationException("El método de simulación devolvió false.");
            return;
        }

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("ComandosMDB requiere Windows x64.");

        var dllPath = Path.IsPathRooted(settings.DllPath)
            ? settings.DllPath
            : Path.Combine(AppContext.BaseDirectory, settings.DllPath);
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("No se encontró ComandosMDB.dll.", dllPath);

        var command = ParseHexCommand(settings.CreditCommandHex);
        // Una sola invocación nativa; la DLL ejecuta el bucle de créditos y el intervalo.
        var result = await Task.Run(
            () => SendNative(dllPath, settings.PortName, command, repeatCount, settings.RepeatIntervalMilliseconds),
            CancellationToken.None);
        if (result != 0)
            throw new InvalidOperationException($"ComandosMDB devolvió el código {result}.");
    }

    private async Task<CreditLogEntry?> FindTransactionAsync(string transactionId)
    {
        var path = Path.GetFullPath(settings.LogPath);
        if (!File.Exists(path))
            return null;

        foreach (var line in File.ReadLines(path))
        {
            var entry = CreditLogEntry.Parse(line);
            if (entry is not null && string.Equals(entry.TransactionId, transactionId, StringComparison.Ordinal))
                return entry;
        }
        await Task.CompletedTask;
        return null;
    }

    private async Task AppendTransactionAsync(CreditLogEntry entry)
    {
        var path = PrepareLogPath();
        var bytes = Encoding.UTF8.GetBytes(entry.ToLogLine() + Environment.NewLine);
        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, CancellationToken.None);
        await stream.FlushAsync(CancellationToken.None);
        stream.Flush(flushToDisk: true);
    }

    private async Task ReplaceTransactionAsync(CreditLogEntry updated)
    {
        var path = PrepareLogPath();
        if (!File.Exists(path))
            throw new FileNotFoundException("No existe el log que contiene la transacción pendiente.", path);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var found = false;
        try
        {
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                foreach (var line in File.ReadLines(path))
                {
                    var entry = CreditLogEntry.Parse(line);
                    var text = line;
                    if (entry is not null &&
                        string.Equals(entry.TransactionId, updated.TransactionId, StringComparison.Ordinal))
                    {
                        if (found)
                            throw new InvalidDataException("El log ya contiene más de una fila para este ID de transacción.");
                        text = updated.ToLogLine();
                        found = true;
                    }

                    var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
                    await output.WriteAsync(bytes, CancellationToken.None);
                }
                await output.FlushAsync(CancellationToken.None);
                output.Flush(flushToDisk: true);
            }

            if (!found)
                throw new InvalidDataException("No se encontró la fila PENDIENTE para actualizar.");

            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private string PrepareLogPath()
    {
        var path = Path.GetFullPath(settings.LogPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        return path;
    }

    private static bool SimulateCommand(int repeatCount, int intervalMilliseconds) => true;

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

    private static string CleanField(string value) =>
        value.Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();

    private static string CleanDetail(string value)
    {
        var cleaned = value.Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();
        return cleaned.Length <= 500 ? cleaned : cleaned[..500];
    }
}

sealed record CreditTransactionResult(string Status, string Detail, bool Duplicate);

sealed record CreditLogEntry(
    string TransactionId,
    string TransactionTime,
    string Bank,
    string User,
    string Amount,
    string Status,
    string UpdatedAt,
    string Detail)
{
    public string ToLogLine() => string.Join("|",
        TransactionId,
        TransactionTime,
        Bank,
        User,
        Amount,
        Status,
        UpdatedAt,
        Detail);

    public static CreditLogEntry? Parse(string line)
    {
        var fields = line.Split(new[] { '|' }, 8);
        return fields.Length == 8
            ? new CreditLogEntry(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7])
            : null;
    }
}

sealed class CreditAuditException(string message, Exception innerException)
    : Exception(message, innerException);

[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
delegate int SendRepeatedCommandDelegate(
    [MarshalAs(UnmanagedType.LPStr)] string portName,
    [In] byte[] commands,
    UIntPtr length,
    int repeatCount,
    int intervalMilliseconds);

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
