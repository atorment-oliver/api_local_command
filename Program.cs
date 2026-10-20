using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);
var settings = LocalSettings.From(builder.Configuration);

builder.WebHost.UseUrls(settings.ListenUrl);
builder.Host.UseWindowsService();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<CreditFileStore>();
builder.Services.AddSingleton<MdbCommandRunner>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("credit-requests", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

var app = builder.Build();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "running" }));
app.MapPost("/api/credits", HandleCreditAsync).RequireRateLimiting("credit-requests");

app.Run();

static async Task<IResult> HandleCreditAsync(
    HttpContext http,
    CreditRequest request,
    LocalSettings settings,
    CreditFileStore files,
    MdbCommandRunner mdb,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    if (!BasicAuthentication.IsValid(http.Request.Headers["Authorization"].ToString(), settings.Username, settings.Password))
    {
        http.Response.Headers["WWW-Authenticate"] = "Basic realm=\"VendingLocalApi\", charset=\"UTF-8\"";
        return Results.Unauthorized();
    }

    var errors = RequestValidation.Validate(request, settings.MaxCreditAmount, settings.CreditUnitAmount, settings.MaxCreditRepeats);
    if (errors.Count > 0) return Results.ValidationProblem(errors);

    var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(idempotencyKey)) idempotencyKey = Guid.NewGuid().ToString("N");
    if (idempotencyKey.Length > 256) return Results.BadRequest(new { error = "Idempotency-Key demasiado largo." });

    var record = new CreditRecord
    {
        RequestId = Guid.NewGuid().ToString("N"),
        IdempotencyKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant(),
        User = request.User.Trim(),
        Application = request.Application.Trim().ToUpperInvariant(),
        Amount = request.Amount,
        RepeatCount = decimal.ToInt32(request.Amount / settings.CreditUnitAmount),
        RepeatIntervalMilliseconds = settings.RepeatIntervalMilliseconds,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        Status = "Received"
    };

    CreditFileStore.CreateResult create;
    try
    {
        create = await files.CreateOrGetAsync(record, cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Could not persist credit request.");
        return Results.Problem("No se pudo guardar la solicitud de crédito.", statusCode: StatusCodes.Status500InternalServerError);
    }

    if (!create.Created)
    {
        if (create.Record.Status == "CommandWritten")
            return Results.Ok(new { requestId = create.Record.RequestId, status = create.Record.Status, duplicate = true });
        var statusCode = create.Record.Status == "PendingMdbConfiguration" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
        return Results.Json(new { requestId = create.Record.RequestId, status = create.Record.Status, duplicate = true, message = "La clave ya existe; revisa el JSON local antes de cualquier reintento." }, statusCode: statusCode);
    }

    if (string.IsNullOrWhiteSpace(settings.MdbDllPath) || string.IsNullOrWhiteSpace(settings.CreditCommandHexTemplate))
    {
        record.Status = "PendingMdbConfiguration";
        await files.UpdateAsync(record, create.Path, CancellationToken.None);
        return Results.Json(new { requestId = record.RequestId, status = record.Status, message = "Solicitud guardada; falta configurar la DLL MDB y la plantilla del comando de crédito." }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var result = await mdb.SendCreditAsync(record, settings, CancellationToken.None);
        record.MdbExitCode = result.ExitCode;
        record.MdbOutput = Limit(result.StandardOutput, 2000);
        record.MdbError = Limit(result.StandardError, 2000);
        record.Status = result.ExitCode == 0 ? "CommandWritten" : "MdbFailed";
        await files.UpdateAsync(record, create.Path, CancellationToken.None);

        if (result.ExitCode != 0)
            return Results.Json(new { requestId = record.RequestId, status = record.Status, message = "El comando MDB informó un error; revisar el registro antes de reintentar." }, statusCode: StatusCodes.Status502BadGateway);

        return Results.Ok(new { requestId = record.RequestId, status = record.Status, duplicate = false });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "MDB command failed for request {RequestId}.", record.RequestId);
        record.Status = "MdbStartFailed";
        record.MdbError = Limit(ex.Message, 2000);
        await files.UpdateAsync(record, create.Path, CancellationToken.None);
        return Results.Problem("No se pudo iniciar el componente MDB. Revisar el registro local.", statusCode: StatusCodes.Status502BadGateway);
    }
}

static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

sealed class CreditRequest
{
    [Required, StringLength(100, MinimumLength = 1)] public string User { get; init; } = "";
    [Required, StringLength(20, MinimumLength = 2)] public string Application { get; init; } = "";
    [Range(typeof(decimal), "0.01", "1000000")] public decimal Amount { get; init; }
}

sealed class CreditRecord
{
    public required string RequestId { get; init; }
    public required string IdempotencyKeyHash { get; init; }
    public required string User { get; init; }
    public required string Application { get; init; }
    public decimal Amount { get; init; }
    public int RepeatCount { get; init; }
    public int RepeatIntervalMilliseconds { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public string Status { get; set; } = "Received";
    public int? MdbExitCode { get; set; }
    public string? MdbOutput { get; set; }
    public string? MdbError { get; set; }
}

sealed class LocalSettings
{
    public required string ListenUrl { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string OutputDirectory { get; init; }
    public decimal MaxCreditAmount { get; init; }
    public decimal CreditUnitAmount { get; init; }
    public int RepeatIntervalMilliseconds { get; init; }
    public int MaxCreditRepeats { get; init; }
    public required string MdbDllPath { get; init; }
    public required string MdbPortName { get; init; }
    public required string CreditCommandHexTemplate { get; init; }

    public static LocalSettings From(IConfiguration config)
    {
        var password = config["LocalApi:Password"] ?? "";
        var username = config["LocalApi:Username"] ?? "";
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || password.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configure LocalApi:Username y LocalApi:Password mediante variables de entorno antes de iniciar la API.");

        return new LocalSettings
        {
            ListenUrl = config["LocalApi:ListenUrl"] ?? "http://127.0.0.1:5098",
            Username = username,
            Password = password,
            OutputDirectory = config["LocalApi:OutputDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "credit-requests"),
            MaxCreditAmount = decimal.TryParse(config["LocalApi:MaxCreditAmount"], NumberStyles.Number, CultureInfo.InvariantCulture, out var max) ? max : 1_000_000m,
            CreditUnitAmount = decimal.TryParse(config["Mdb:CreditUnitAmount"], NumberStyles.Number, CultureInfo.InvariantCulture, out var unit) && unit > 0 ? unit : 1m,
            RepeatIntervalMilliseconds = int.TryParse(config["Mdb:RepeatIntervalMilliseconds"], out var delay) ? Math.Clamp(delay, 30, 5000) : 400,
            MaxCreditRepeats = int.TryParse(config["Mdb:MaxCreditRepeats"], out var repeats) ? Math.Clamp(repeats, 1, 500) : 50,
            MdbDllPath = config["Mdb:DllPath"] ?? "",
            MdbPortName = config["Mdb:PortName"] ?? "COM4",
            CreditCommandHexTemplate = config["Mdb:CreditCommandHexTemplate"] ?? "",
        };
    }
}

static class BasicAuthentication
{
    public static bool IsValid(string header, string configuredUser, string configuredPassword)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var separator = decoded.IndexOf(':');
            if (separator <= 0) return false;
            var user = Encoding.UTF8.GetBytes(decoded[..separator]);
            var password = Encoding.UTF8.GetBytes(decoded[(separator + 1)..]);
            return FixedEquals(user, Encoding.UTF8.GetBytes(configuredUser)) && FixedEquals(password, Encoding.UTF8.GetBytes(configuredPassword));
        }
        catch (FormatException) { return false; }
    }

    private static bool FixedEquals(byte[] left, byte[] right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}

static class RequestValidation
{
    private static readonly HashSet<string> SupportedApps = new(StringComparer.OrdinalIgnoreCase) { "ALTOQUE", "YAPE", "BCP" };

    public static Dictionary<string, string[]> Validate(CreditRequest request, decimal maxAmount, decimal unitAmount, int maxRepeats)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var messages)) errors[field] = messages = [];
            messages.Add(message);
        }

        var validationResults = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true);
        foreach (var result in validationResults)
            foreach (var member in result.MemberNames.DefaultIfEmpty("request")) Add(member, result.ErrorMessage ?? "Valor inválido.");
        if (string.IsNullOrWhiteSpace(request.User)) Add("user", "El nombre no puede estar vacío.");
        if (!SupportedApps.Contains(request.Application ?? "")) Add("application", "Aplicación no admitida. Valores: ALTOQUE, YAPE o BCP.");
        if (request.Amount > maxAmount) Add("amount", $"El monto supera el máximo configurado ({maxAmount}).");
        if (decimal.Round(request.Amount, 2) != request.Amount) Add("amount", "El monto no puede tener más de dos decimales.");
        if (request.Amount > 0 && unitAmount > 0 && request.Amount % unitAmount != 0) Add("amount", $"El monto debe ser múltiplo exacto de {unitAmount} por crédito.");
        if (request.Amount > 0 && unitAmount > 0 && request.Amount / unitAmount > maxRepeats) Add("amount", $"El monto excede el máximo configurado de {maxRepeats} tramas.");
        return errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }
}

sealed class CreditFileStore(LocalSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public sealed record CreateResult(bool Created, CreditRecord Record, string Path);

    public async Task<CreateResult> CreateOrGetAsync(CreditRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.OutputDirectory);
        var path = Path.Combine(settings.OutputDirectory, record.IdempotencyKeyHash + ".json");
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return new CreateResult(true, record, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var existing = await JsonSerializer.DeserializeAsync<CreditRecord>(stream, JsonOptions, cancellationToken);
            if (existing is null) throw new InvalidDataException("El registro idempotente existente no se pudo leer.");
            return new CreateResult(false, existing, path);
        }
    }

    public async Task UpdateAsync(CreditRecord record, string path, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temp, path, overwrite: true);
    }
}

sealed class MdbCommandRunner
{
    public sealed record Result(int ExitCode, string StandardOutput, string StandardError);
    private readonly SemaphoreSlim _portGate = new(1, 1);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int SendCommandDelegate([MarshalAs(UnmanagedType.LPStr)] string portName, [In] byte[] commands, UIntPtr length, int repeatCount, int intervalMilliseconds);

    public async Task<Result> SendCreditAsync(CreditRecord record, LocalSettings settings, CancellationToken cancellationToken)
    {
        await _portGate.WaitAsync(cancellationToken);
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("ComandosMDB requiere Windows x64.");
            if (!File.Exists(settings.MdbDllPath)) throw new FileNotFoundException("No existe la DLL ComandosMDB configurada.", settings.MdbDllPath);
            var bytes = ParseCommandTemplate(settings.CreditCommandHexTemplate, record.Amount);
            if (bytes.Length is 0 or > 256) throw new InvalidOperationException("La plantilla MDB debe producir entre 1 y 256 bytes.");
            return await Task.Run(() => SendNative(settings, bytes), cancellationToken);
        }
        finally { _portGate.Release(); }
    }

    private static Result SendNative(LocalSettings settings, byte[] bytes)
    {
        var handle = NativeLibrary.Load(Path.GetFullPath(settings.MdbDllPath));
        try
        {
            var send = Marshal.GetDelegateForFunctionPointer<SendCommandDelegate>(NativeLibrary.GetExport(handle, "SendCreditRepeatsMDB"));
            var code = send(settings.MdbPortName, bytes, (UIntPtr)(uint)bytes.Length, record.RepeatCount, record.RepeatIntervalMilliseconds);
            return new Result(code, code == 0 ? $"{record.RepeatCount} tramas escritas en el puerto serie." : "ComandosMDB devolvió un error al escribir.", code == 0 ? "" : "Código nativo: " + code);
        }
        finally { NativeLibrary.Free(handle); }
    }

    private static byte[] ParseCommandTemplate(string template, decimal amount)
    {
        var centsDecimal = decimal.Multiply(amount, 100m);
        if (centsDecimal != decimal.Truncate(centsDecimal)) throw new InvalidOperationException("El monto debe convertirse exactamente a centavos.");
        var cents = (ulong)centsDecimal;
        var expanded = template.Replace("{amountCentsBE16}", Encode(cents, 2, bigEndian: true), StringComparison.Ordinal)
            .Replace("{amountCentsLE16}", Encode(cents, 2, bigEndian: false), StringComparison.Ordinal)
            .Replace("{amountCentsBE32}", Encode(cents, 4, bigEndian: true), StringComparison.Ordinal)
            .Replace("{amountCentsLE32}", Encode(cents, 4, bigEndian: false), StringComparison.Ordinal);
        if (System.Text.RegularExpressions.Regex.IsMatch(expanded, @"\{[^}]+\}"))
            throw new InvalidOperationException("La plantilla contiene un token MDB no admitido.");
        var tokens = expanded.Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 256) throw new InvalidOperationException("El comando MDB supera 256 bytes.");
        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? tokens[i][2..] : tokens[i];
            if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                throw new InvalidOperationException("La plantilla MDB debe contener bytes hexadecimales separados por espacios.");
        }
        return bytes;
    }

    private static string Encode(ulong value, int byteCount, bool bigEndian)
    {
        var max = byteCount == 2 ? ushort.MaxValue : uint.MaxValue;
        if (value > max) throw new InvalidOperationException($"El monto en centavos supera la capacidad de {byteCount} bytes configurada.");
        var parts = new string[byteCount];
        for (var i = 0; i < byteCount; i++)
        {
            var shift = (bigEndian ? byteCount - 1 - i : i) * 8;
            parts[i] = ((value >> shift) & 0xff).ToString("X2", CultureInfo.InvariantCulture);
        }
        return string.Join(' ', parts);
    }
}
