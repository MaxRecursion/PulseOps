#:property PublishAot=false
// PulseOps staging smoke. Run with .NET 10 file-based applications; the procedure is in docs/Staging.md.
//   dotnet run --file deployment/staging/StagingSmoke.cs -- --local <published PulseOps.Server.dll> [--report <path>]
//   dotnet run --file deployment/staging/StagingSmoke.cs -- --target <https origin> --application <slug> --environment <slug>
//       --token-env <VARIABLE> [--other-token-env <VARIABLE>] [--event-name <alias>] [--report <path>]
// Local mode runs a disposable Production host over HTTPS with a generated certificate that only this client trusts.
// Remote mode validates TLS normally and sends labelled synthetic telemetry to a dedicated staging scope.
// Credentials come from environment variables. They are never printed or written to the report.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

SmokeSettings settings;
try
{
    settings = SmokeSettings.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(SmokeSettings.Usage);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) =>
{
    press.Cancel = true;
    cancellation.Cancel();
};

var log = new CheckLog();
var startedAt = DateTimeOffset.UtcNow;
var environment = new Dictionary<string, object?>
{
    ["os"] = RuntimeInformation.OSDescription,
    ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
    ["framework"] = RuntimeInformation.FrameworkDescription,
    ["processorCount"] = Environment.ProcessorCount,
    ["mode"] = settings.Server is null ? "remote" : "local"
};
try
{
    if (settings.Server is { } server)
    {
        environment["serverAssembly"] = Path.GetFileName(server);
        await using (var file = File.OpenRead(server))
            environment["serverSha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation.Token));
        environment["target"] = $"https://{LocalSmoke.Host}/ (loopback, generated certificate)";
        environment["application"] = LocalSmoke.Application;
        environment["environment"] = "staging";
        Console.Error.WriteLine($"PulseOps staging smoke: local Production host from {server}");
        await LocalSmoke.Run(server, log, cancellation.Token);
    }
    else
    {
        environment["target"] = settings.Target!.GetLeftPart(UriPartial.Authority) + "/";
        environment["application"] = settings.Application;
        environment["environment"] = settings.Environment;
        environment["eventName"] = settings.EventName;
        Console.Error.WriteLine($"PulseOps staging smoke: {environment["target"]} {settings.Application}/{settings.Environment}");
        await Smoke.Remote(settings, log, cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    log.Add("run", false, null, 0, "cancelled before completion");
}

int passed = log.Items.Count(check => check.Status == "pass");
int failed = log.Items.Count(check => check.Status == "fail");
bool success = failed == 0 && passed > 0;
string report = JsonSerializer.Serialize(new
{
    tool = "PulseOps staging smoke",
    startedAt,
    finishedAt = DateTimeOffset.UtcNow,
    environment,
    checks = log.Items,
    summary = new { passed, failed, skipped = log.Items.Count(check => check.Status == "skip"), result = success ? "pass" : "fail" }
}, Smoke.Json);
Console.WriteLine(report);
if (settings.Report is { } path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
    await output.WriteLineAsync(report);
}
Console.Error.WriteLine($"Smoke {(success ? "passed" : "FAILED")}: {passed} passed, {failed} failed, {log.Items.Count - passed - failed} skipped.");
return success ? 0 : 1;

sealed class SmokeSettings
{
    public const string Usage = """
        Usage:
          StagingSmoke --local <published PulseOps.Server.dll> [--report <new file>]
          StagingSmoke --target <https origin> --application <slug> --environment <slug> --token-env <VARIABLE>
                       [--other-token-env <VARIABLE>] [--event-name <allowlisted alias>] [--report <new file>]
        Remote mode reads scoped ingest credentials from the named environment variables.
        """;

    public string? Server { get; private init; }
    public Uri? Target { get; private init; }
    public string? Application { get; private init; }
    public string? Environment { get; private init; }
    public string? Token { get; private init; }
    public string? OtherToken { get; private init; }
    public string EventName { get; private init; } = "smoke-check";
    public string? Report { get; private init; }

    public static SmokeSettings Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = ["--local", "--target", "--application", "--environment", "--token-env", "--other-token-env", "--event-name", "--report"];
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i]) || i + 1 >= args.Length || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException(Regex.IsMatch(args[i], @"\A--[a-z-]{1,32}\z")
                    ? $"Unexpected, repeated or incomplete option '{args[i]}'." : $"Unexpected argument at position {i + 1}.");
        }
        string? report = values.TryGetValue("--report", out var file) ? Path.GetFullPath(file) : null;
        if (report is not null && File.Exists(report))
            throw new ArgumentException("The report file already exists. Choose a new path.");

        if (values.TryGetValue("--local", out var local))
        {
            if (values.Keys.Any(key => key is not ("--local" or "--report")))
                throw new ArgumentException("--local accepts only --report.");
            string server = Path.GetFullPath(local);
            if (!File.Exists(server) || !server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--local requires the published PulseOps.Server.dll.");
            return new() { Server = server, Report = report };
        }

        if (!values.TryGetValue("--target", out var target) || !Uri.TryCreate(target, UriKind.Absolute, out var site) ||
            site.Scheme != Uri.UriSchemeHttps || site.AbsolutePath != "/" || site.Query.Length > 0 || site.Fragment.Length > 0 ||
            site.UserInfo.Length > 0)
            throw new ArgumentException("Pass --local, or --target with the HTTPS site origin (no path, query or credentials).");
        string eventName = values.GetValueOrDefault("--event-name") ?? "smoke-check";
        if (!Regex.IsMatch(eventName, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z"))
            throw new ArgumentException("--event-name must be an allowlisted telemetry alias.");
        string token = Secret(values, "--token-env") ?? throw new ArgumentException("--token-env is required.");
        string? other = Secret(values, "--other-token-env");
        if (other == token)
            throw new ArgumentException("--other-token-env must hold the credential of a different scope.");
        return new()
        {
            Target = site, Application = Slug(values, "--application"), Environment = Slug(values, "--environment"),
            Token = token, OtherToken = other, EventName = eventName, Report = report
        };
    }

    private static string Slug(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && Regex.IsMatch(value, @"\A[a-z0-9][a-z0-9-]{0,39}\z")
            ? value : throw new ArgumentException($"{name} requires a PulseOps slug (lowercase letters, digits and hyphens).");

    // Accepts a variable name, never a value, so credentials stay out of command lines and process listings.
    private static string? Secret(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var variable))
            return null;
        if (!Regex.IsMatch(variable, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z"))
            throw new ArgumentException($"{name} must name an environment variable.");
        string? token = System.Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(token) || token.Length > 256 || token.Any(c => c < 33 || c > 126))
            throw new ArgumentException($"Environment variable {variable} named by {name} must hold a scoped ingest credential.");
        return token;
    }
}

sealed record Scope(string Application, string Environment, string Token, string? OtherToken, string EventName);

sealed record Check(string Name, string Status, int? HttpStatus, double DurationMs, string Detail);

sealed class CheckLog
{
    public List<Check> Items { get; } = [];

    public void Add(string name, bool? passed, int? status, double milliseconds, string detail)
    {
        string result = passed switch { true => "pass", false => "fail", null => "skip" };
        Items.Add(new(name, result, status, Math.Round(milliseconds, 1), detail));
        Console.Error.WriteLine($"{result.ToUpperInvariant(),-4} {name,-34} {(status is int code ? $"HTTP {code}" : ""),-8} {milliseconds,9:0.0} ms  {detail}");
    }
}

sealed record Reply(int Status, Dictionary<string, string> Headers, string Body, double Milliseconds, string? Error)
{
    public int? HttpStatus => Status == 0 ? null : Status;
    public string Describe => Error ?? $"HTTP {Status}{(Smoke.Text(Body, "error") is { } code ? $" ({code})" : "")}";
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
    public bool Has(string name) => Headers.ContainsKey(name);
    public string Expect(int status, string success) => Status == status ? success : $"expected HTTP {status}, received {Describe}";
}

sealed class Probe(HttpClient http, Uri site, Func<bool> alive, Func<string?> hint)
{
    public Uri Site => site;
    public bool Alive => alive();

    public async Task<Reply> Send(HttpMethod method, string path, CancellationToken cancellation, string? json = null,
        string? token = null, string? origin = null, bool expectContinue = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(site, path));
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (expectContinue)
            request.Headers.ExpectContinue = true;
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                headers[header.Key] = string.Join(", ", header.Value);
            // Bounded read: only status, headers and small JSON error codes are evaluated.
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var buffer = new byte[32_768];
            int length = 0, read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellation)) > 0)
                length += read;
            return new((int)response.StatusCode, headers, Encoding.UTF8.GetString(buffer, 0, length), timer.Elapsed.TotalMilliseconds, null);
        }
        catch (HttpRequestException exception)
        {
            string cause = exception.GetBaseException() switch
            {
                SocketException socket => $"socket {socket.SocketErrorCode}",
                System.Security.Authentication.AuthenticationException => "TLS authentication failed",
                var other => other.GetType().Name
            };
            string? context = hint();
            return new(0, new(StringComparer.OrdinalIgnoreCase), "", timer.Elapsed.TotalMilliseconds,
                $"transport {exception.HttpRequestError}: {cause}{(context is null ? "" : $"; {context}")}");
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new(0, new(StringComparer.OrdinalIgnoreCase), "", timer.Elapsed.TotalMilliseconds, "timeout");
        }
    }
}

static class Smoke
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string Intake = "/api/telemetry/v1/batch";
    private const string Version = "0.0.0-smoke";

    public static async Task Remote(SmokeSettings settings, CheckLog log, CancellationToken cancellation)
    {
        // Regular chain and host name validation; the callback only records why a handshake was rejected.
        var observed = SslPolicyErrors.None;
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    observed = errors;
                    return errors == SslPolicyErrors.None;
                }
            }
        }) { Timeout = TimeSpan.FromSeconds(30) };
        var probe = new Probe(http, settings.Target!, () => true,
            () => observed == SslPolicyErrors.None ? null : $"certificate {observed}");
        var scope = new Scope(settings.Application!, settings.Environment!, settings.Token!, settings.OtherToken, settings.EventName);
        if (!await Poll(probe, log, "health-live", "/health/live", TimeSpan.FromSeconds(30), cancellation))
            return;
        await Poll(probe, log, "health-ready", "/health/ready", TimeSpan.FromSeconds(60), cancellation);
        await Surface(probe, log, cancellation);
        await Ingest(probe, scope, log, cancellation);
    }

    public static async Task<bool> Poll(Probe probe, CheckLog log, string name, string path, TimeSpan limit,
        CancellationToken cancellation)
    {
        var timer = Stopwatch.StartNew();
        int attempts = 0;
        Reply reply;
        do
        {
            if (attempts++ > 0)
                await Task.Delay(500, cancellation);
            reply = await probe.Send(HttpMethod.Get, path, cancellation);
        }
        while (reply.Status != 200 && timer.Elapsed < limit && probe.Alive);
        bool ready = reply.Status == 200;
        log.Add(name, ready, reply.HttpStatus, timer.Elapsed.TotalMilliseconds,
            ready ? $"HTTP 200 after {attempts} attempt(s)" : $"{reply.Describe} after {attempts} attempt(s) in {timer.Elapsed.TotalSeconds:0} s");
        return ready;
    }

    public static async Task Surface(Probe probe, CheckLog log, CancellationToken cancellation)
    {
        var shell = await probe.Send(HttpMethod.Get, "/", cancellation);
        string type = shell.Header("Content-Type");
        log.Add("portal-shell", shell.Status == 200 && type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase),
            shell.HttpStatus, shell.Milliseconds, shell.Status == 0 ? shell.Describe : $"Content-Type {Printable(type)}");

        // ASP.NET Core never emits HSTS for loopback host names, so it is only required for real host names.
        bool hsts = !IsLoopback(probe.Site);
        var problems = new List<string>();
        if (!shell.Header("Content-Security-Policy").Contains("default-src", StringComparison.OrdinalIgnoreCase))
            problems.Add("Content-Security-Policy missing");
        if (!shell.Header("X-Content-Type-Options").Equals("nosniff", StringComparison.OrdinalIgnoreCase))
            problems.Add("X-Content-Type-Options is not nosniff");
        if (shell.Header("Referrer-Policy").Length == 0)
            problems.Add("Referrer-Policy missing");
        if (hsts && !Regex.IsMatch(shell.Header("Strict-Transport-Security"), @"(\A|;)\s*max-age=[1-9][0-9]*", RegexOptions.IgnoreCase))
            problems.Add("Strict-Transport-Security missing");
        if (shell.Has("Server"))
            problems.Add("Server header present");
        string notes = (hsts ? "" : "; HSTS is not sent to loopback host names") +
            (shell.Has("X-Powered-By") ? "; advisory: X-Powered-By is present" : "");
        log.Add("security-headers", shell.Status != 0 && problems.Count == 0, shell.HttpStatus, shell.Milliseconds,
            shell.Status == 0 ? shell.Describe : (problems.Count == 0
                ? $"CSP, nosniff, Referrer-Policy{(hsts ? ", HSTS" : "")}, no Server header" : string.Join("; ", problems)) + notes);

        var context = await probe.Send(HttpMethod.Get, "/api/v1/context", cancellation);
        log.Add("operator-context-unauthenticated", context.Status == 401, context.HttpStatus, context.Milliseconds,
            context.Expect(401, "rejected without an operator session"));
        log.Add("operator-api-no-store", context.Status != 0 &&
            context.Header("Cache-Control").Contains("no-store", StringComparison.OrdinalIgnoreCase), context.HttpStatus, 0,
            context.Status == 0 ? context.Describe : $"Cache-Control {Printable(context.Header("Cache-Control"))}");
        var audit = await probe.Send(HttpMethod.Get, "/api/v1/admin/audit", cancellation);
        log.Add("audit-unauthenticated", audit.Status == 404 ? null : audit.Status == 401, audit.HttpStatus, audit.Milliseconds,
            audit.Status == 404 ? "endpoint not present in this build" : audit.Expect(401, "rejected without an operator session"));
    }

    /// <summary>Runs the machine intake checks and returns the accepted batch so a restart can prove durable deduplication.</summary>
    public static async Task<(string Batch, int Count)> Ingest(Probe probe, Scope scope, CheckLog log, CancellationToken cancellation)
    {
        const int count = 5;
        var session = Guid.NewGuid();
        var occurred = DateTimeOffset.UtcNow.AddSeconds(-2);
        string batch = Batch(scope, Enumerable.Range(0, count).Select(_ => Event(scope.EventName, session, occurred)));

        var anonymous = await probe.Send(HttpMethod.Post, Intake, cancellation, batch);
        log.Add("intake-without-token", anonymous.Status == 401, anonymous.HttpStatus, anonymous.Milliseconds,
            anonymous.Expect(401, "rejected without a bearer credential"));
        var first = await probe.Send(HttpMethod.Post, Intake, cancellation, batch, scope.Token);
        Counted(log, "intake-accepted", first, count, 0);
        var second = await probe.Send(HttpMethod.Post, Intake, cancellation, batch, scope.Token);
        Counted(log, "intake-duplicate", second, 0, count);

        if (scope.OtherToken is { } other)
        {
            var crossed = await probe.Send(HttpMethod.Post, Intake, cancellation,
                Batch(scope, [Event(scope.EventName, session, occurred)]), other);
            log.Add("intake-other-scope-token", crossed.Status == 401, crossed.HttpStatus, crossed.Milliseconds,
                crossed.Expect(401, "another scope's credential was rejected"));
        }
        else
            log.Add("intake-other-scope-token", null, null, 0, "no --other-token-env supplied");

        var unapproved = await probe.Send(HttpMethod.Post, Intake, cancellation,
            Batch(scope, [Event("smoke-unapproved-name", session, occurred)]), scope.Token);
        log.Add("intake-unapproved-name", unapproved.Status == 400, unapproved.HttpStatus, unapproved.Milliseconds,
            unapproved.Expect(400, $"rejected ({Text(unapproved.Body, "error") ?? "no error code"})"));

        string startup = Batch(scope, [new
        {
            id = Guid.NewGuid(), occurredAt = occurred, version = Version, sessionId = Guid.NewGuid(),
            kind = "startup", name = "PageLoaded", outcome = "success", durationMs = 1.0
        }]);
        var withoutOrigin = await probe.Send(HttpMethod.Post, "/api/telemetry/v1/startup", cancellation, startup);
        log.Add("startup-without-origin", withoutOrigin.Status == 403, withoutOrigin.HttpStatus, withoutOrigin.Milliseconds,
            withoutOrigin.Expect(403, "anonymous startup intake refused without an approved Origin"));
        var foreign = await probe.Send(HttpMethod.Post, "/api/telemetry/v1/startup", cancellation, startup,
            origin: "https://unapproved.invalid");
        log.Add("startup-unapproved-origin", foreign.Status == 403 && !foreign.Has("Access-Control-Allow-Origin"),
            foreign.HttpStatus, foreign.Milliseconds, foreign.Has("Access-Control-Allow-Origin")
                ? "CORS granted to an unapproved origin" : foreign.Expect(403, "refused, no CORS grant"));

        string marker = JsonSerializer.Serialize(new
        {
            application = scope.Application, environment = scope.Environment, version = Version,
            buildId = $"staging-smoke-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}",
            commitId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20))
        }, Json);
        var release = await probe.Send(HttpMethod.Post, "/api/telemetry/v1/releases", cancellation, marker, scope.Token);
        log.Add("release-marker", release.Status == 202, release.HttpStatus, release.Milliseconds,
            release.Expect(202, "labelled smoke release marker accepted"));

        // Expect: 100-continue lets the server refuse by Content-Length before the body is transmitted.
        string item = JsonSerializer.Serialize(Event(scope.EventName, session, occurred), Json);
        var large = new StringBuilder(210_000).Append("{\"schemaVersion\":1,\"application\":\"").Append(scope.Application)
            .Append("\",\"environment\":\"").Append(scope.Environment).Append("\",\"events\":[");
        while (large.Length < 200 * 1024)
            large.Append(item).Append(',');
        large.Append(item).Append("]}");
        var oversized = await probe.Send(HttpMethod.Post, Intake, cancellation, large.ToString(), scope.Token, expectContinue: true);
        log.Add("oversized-body", oversized.Status == 413, oversized.HttpStatus, oversized.Milliseconds,
            oversized.Expect(413, $"{large.Length / 1024} KiB body refused"));

        var malformed = await probe.Send(HttpMethod.Post, Intake, cancellation, batch[..(batch.Length / 2)], scope.Token);
        log.Add("malformed-json", malformed.Status == 400, malformed.HttpStatus, malformed.Milliseconds,
            malformed.Expect(400, "truncated JSON rejected"));
        return (batch, count);
    }

    public static void Counted(CheckLog log, string name, Reply reply, int accepted, int duplicates)
    {
        int? actual = Number(reply.Body, "accepted"), repeated = Number(reply.Body, "duplicates");
        log.Add(name, reply.Status == 202 && actual == accepted && repeated == duplicates, reply.HttpStatus, reply.Milliseconds,
            reply.Status == 202 ? $"accepted {actual}, duplicates {repeated} (expected {accepted} and {duplicates})"
                : $"expected HTTP 202, received {reply.Describe}");
    }

    private static object Event(string name, Guid session, DateTimeOffset occurred) => new
    {
        id = Guid.NewGuid(), occurredAt = occurred, version = Version, sessionId = session, kind = "api", name,
        outcome = "success", durationMs = 12.5, statusCode = 200,
        traceId = ActivityTraceId.CreateRandom().ToHexString(), spanId = ActivitySpanId.CreateRandom().ToHexString()
    };

    private static string Batch(Scope scope, IEnumerable<object> events) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, application = scope.Application, environment = scope.Environment, events = events.ToArray()
    }, Json);

    private static int? Number(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(property, out var value) &&
                value.TryGetInt32(out int number) ? number : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A short machine error code from a JSON body, never free text.</summary>
    public static string? Text(string body, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { } text && Regex.IsMatch(text, @"\A[a-z_]{1,40}\z") ? text : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Printable(string value) => value.Length == 0 ? "(absent)" :
        new string(value.Take(80).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    private static bool IsLoopback(Uri site) => site.IsLoopback ||
        site.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || site.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}

static class LocalSmoke
{
    public const string Application = "staging-smoke";
    // Requests use this reserved name, so ASP.NET Core emits HSTS as it would for a real host; the client connects to loopback.
    public const string Host = "pulseops-smoke.test";

    public static async Task Run(string server, CheckLog log, CancellationToken cancellation)
    {
        var root = Directory.CreateTempSubdirectory("pulseops-smoke-");
        LocalHost? host = null;
        try
        {
            string database = Path.Combine(root.FullName, "data", "pulseops.db");
            string keys = Path.Combine(root.FullName, "keys");
            string certificate = Path.Combine(root.FullName, "tls", "smoke.pfx");
            string configuration = Path.Combine(root.FullName, "pulseops.production.json");
            string restoreConfiguration = Path.Combine(root.FullName, "pulseops.restore.json");
            string restored = Path.Combine(root.FullName, "restore", "pulseops.db");
            string password = Secret();
            string staging = Secret();
            string production = Secret();
            Directory.CreateDirectory(Path.GetDirectoryName(certificate)!);
            string thumbprint = Certificate(certificate, password);
            await File.WriteAllTextAsync(configuration, Configuration(database, keys), cancellation);
            await File.WriteAllTextAsync(restoreConfiguration, Configuration(restored, keys), cancellation);
            // Ingest credentials and the PFX password reach the child only through its environment.
            host = new LocalHost(server, new Dictionary<string, string>
            {
                ["PulseOps__Scopes__0__IngestToken"] = staging,
                ["PulseOps__Scopes__1__IngestToken"] = production
            });

            var migrate = await host.Command(configuration, cancellation, "--migrate");
            log.Add("migrate", migrate.ExitCode == 0, null, migrate.Milliseconds, migrate.Outcome("Production schema created"));
            if (migrate.ExitCode != 0)
                return;

            int port = FreePort();
            host.Start(configuration, port, certificate, password);
            string? batch = null;
            using (var http = LoopbackClient(() => port, thumbprint))
            {
                var probe = new Probe(http, new Uri($"https://{Host}/"), () => host.Running, () => host.Running ? null : host.ExitDescription);
                if (await Smoke.Poll(probe, log, "health-live", "/health/live", TimeSpan.FromSeconds(30), cancellation))
                {
                    if (!await Smoke.Poll(probe, log, "health-ready", "/health/ready", TimeSpan.FromSeconds(60), cancellation))
                        host.WriteTail("readiness failed");
                    await Smoke.Surface(probe, log, cancellation);
                    (batch, _) = await Smoke.Ingest(probe, new Scope(Application, "staging", staging, production, "smoke-check"), log, cancellation);
                }
                else
                    host.WriteTail("liveness failed");
            }
            var stop = await host.Stop();
            log.Add("host-stop", stop.Passed, null, stop.Milliseconds, stop.Detail);

            string snapshot = Path.Combine(root.FullName, "backups", $"smoke-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            var backup = await host.Command(configuration, cancellation, "--backup", snapshot);
            bool snapshotted = backup.ExitCode == 0 && File.Exists(snapshot);
            log.Add("backup", snapshotted, null, backup.Milliseconds,
                backup.Outcome(snapshotted ? $"online backup API snapshot of {new FileInfo(snapshot).Length} bytes" : "snapshot missing"));
            if (snapshotted)
            {
                var verify = await host.Command(configuration, cancellation, "--verify-backup", snapshot);
                log.Add("verify-backup", verify.ExitCode == 0, null, verify.Milliseconds, verify.Outcome("identity, schema and integrity_check passed"));
                var restore = await host.Command(restoreConfiguration, cancellation, "--restore", snapshot);
                var check = restore.ExitCode == 0 ? await host.Command(configuration, cancellation, "--verify-backup", restored) : restore;
                log.Add("restore-rehearsal", restore.ExitCode == 0 && check.ExitCode == 0, null, restore.Milliseconds + (restore.ExitCode == 0 ? check.Milliseconds : 0),
                    restore.ExitCode != 0 ? restore.Outcome("") : check.Outcome("restored into a separate database that passed verification"));
            }
            else
            {
                log.Add("verify-backup", null, null, 0, "no snapshot to verify");
                log.Add("restore-rehearsal", null, null, 0, "no snapshot to restore");
            }

            string export = Path.Combine(root.FullName, "audit", "audit-export.ndjson");
            Directory.CreateDirectory(Path.GetDirectoryName(export)!);
            var audit = await host.Command(configuration, cancellation, "--export-audit", export);
            var exported = Regex.Match(audit.Output, @"Exported (?<count>[0-9]+) verified audit entries\. Chain head (?<head>[0-9A-Za-z:+/=_-]*)\.");
            if (audit.ExitCode != 0 && audit.Output.Contains("System.NotImplementedException", StringComparison.Ordinal))
                log.Add("export-audit", null, null, audit.Milliseconds, "audit export is not implemented in this build");
            else
                log.Add("export-audit", audit.ExitCode == 0 && exported.Success && File.Exists(export), null, audit.Milliseconds,
                    audit.Outcome(exported.Success
                        ? $"{exported.Groups["count"].Value} verified entries, chain head {(exported.Groups["head"].Length == 0 ? "(empty)" : exported.Groups["head"].Value)}, " +
                          $"{(File.Exists(export) ? File.ReadLines(export).Count(line => line.Length > 0) : 0)} NDJSON lines"
                        : "export summary not reported"));

            port = FreePort();
            host.Start(configuration, port, certificate, password);
            using (var http = LoopbackClient(() => port, thumbprint))
            {
                var probe = new Probe(http, new Uri($"https://{Host}/"), () => host.Running, () => host.Running ? null : host.ExitDescription);
                if (await Smoke.Poll(probe, log, "restart-ready", "/health/ready", TimeSpan.FromSeconds(60), cancellation))
                {
                    if (batch is not null)
                    {
                        var repeat = await probe.Send(HttpMethod.Post, "/api/telemetry/v1/batch", cancellation, batch, staging);
                        Smoke.Counted(log, "restart-durable-deduplication", repeat, 0, 5);
                    }
                    else
                        log.Add("restart-durable-deduplication", null, null, 0, "no accepted batch before restart");
                }
                else
                    host.WriteTail("readiness after restart failed");
            }
            var final = await host.Stop();
            if (!final.Passed)
                Console.Error.WriteLine($"Final host stop: {final.Detail}");
        }
        finally
        {
            if (host is not null)
                await host.DisposeAsync();
            try
            {
                root.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not delete the temporary directory {root.FullName}: {exception.GetType().Name}. Remove it manually.");
            }
        }
    }

    private static string Configuration(string database, string keys) => JsonSerializer.Serialize(new
    {
        AllowedHosts = Host,
        Logging = new { LogLevel = new { Default = "Warning" } },
        PulseOps = new
        {
            DatabasePath = database,
            DataProtectionPath = keys,
            Demo = false,
            MinimumFreeMegabytes = 64,
            // OIDC metadata is fetched on the first challenge, which the smoke never triggers.
            Auth = new { Authority = "https://login.invalid/", ClientId = "staging-smoke-placeholder", ClientSecret = "placeholder-not-a-secret" },
            Scopes = new[] { "staging", "production" }.Select(environment => new
            {
                Application, Environment = environment, Label = $"Staging smoke ({environment})", AllowedNames = new[] { "smoke-check" }
            })
        }
    }, new JsonSerializerOptions { WriteIndented = true });

    private static string Certificate(string path, string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Host);
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(path, options))
            file.Write(certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password));
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    private static HttpClient LoopbackClient(Func<int> port, string thumbprint) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (_, cancellation) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port()), cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
        // Trust exactly the generated certificate, by SHA-256 thumbprint.
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null &&
                string.Equals(presented.GetCertHashString(HashAlgorithmName.SHA256), thumbprint, StringComparison.OrdinalIgnoreCase)
        }
    }) { Timeout = TimeSpan.FromSeconds(20) };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Secret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

sealed record CommandResult(int ExitCode, string Output, double Milliseconds)
{
    /// <summary>The success text, or the exit code and exception type. Full output goes to standard error only.</summary>
    public string Outcome(string success) => ExitCode == 0 ? success :
        $"exit code {ExitCode}{(Regex.Match(Output, @"[A-Za-z0-9_.]+Exception") is { Success: true } type ? $", {type.Value}" : "")}";
}

sealed record StopResult(bool Passed, double Milliseconds, string Detail);

sealed class LocalHost(string server, IReadOnlyDictionary<string, string> secrets) : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _output = new();
    private Process? _process;

    public bool Running => _process is { HasExited: false };
    public string ExitDescription => _process is { HasExited: true } process ? $"host exited with code {process.ExitCode}" : "host not running";

    public async Task<CommandResult> Command(string configuration, CancellationToken cancellation, params string[] arguments)
    {
        var timer = Stopwatch.StartNew();
        using var process = Process.Start(StartInfo(configuration, arguments, null))
            ?? throw new InvalidOperationException("Could not start the PulseOps command.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            cancellation.ThrowIfCancellationRequested();
            return new(-1, "timed out after 120 seconds", timer.Elapsed.TotalMilliseconds);
        }
        var result = new CommandResult(process.ExitCode, await output + await errors, timer.Elapsed.TotalMilliseconds);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"--- PulseOps {arguments.FirstOrDefault()} exited with code {result.ExitCode} ---");
            Console.Error.WriteLine(string.Join(Environment.NewLine, result.Output.Split('\n').TakeLast(30)));
        }
        return result;
    }

    public void Start(string configuration, int port, string certificate, string password)
    {
        _process?.Dispose();
        _output.Clear();
        var process = Process.Start(StartInfo(configuration, [], new Dictionary<string, string>
        {
            ["Kestrel__Endpoints__Https__Url"] = $"https://127.0.0.1:{port}",
            ["Kestrel__Endpoints__Https__Certificate__Path"] = certificate,
            ["Kestrel__Endpoints__Https__Certificate__Password"] = password
        })) ?? throw new InvalidOperationException("Could not start the PulseOps host.");
        process.OutputDataReceived += Record;
        process.ErrorDataReceived += Record;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    /// <summary>Requests a graceful stop with SIGTERM where available; Windows console hosts are terminated.</summary>
    public async Task<StopResult> Stop()
    {
        var timer = Stopwatch.StartNew();
        if (_process is not { } process)
            return new(false, 0, "host was not started");
        if (process.HasExited)
            return new(false, 0, $"host had already exited with code {process.ExitCode}");
        if (!OperatingSystem.IsWindows() && Native.Kill(process.Id, 15) == 0)
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(grace.Token);
                return new(true, timer.Elapsed.TotalMilliseconds, $"graceful stop on SIGTERM, exit code {process.ExitCode}");
            }
            catch (OperationCanceledException) { }
        }
        Terminate(process);
        await process.WaitForExitAsync();
        return OperatingSystem.IsWindows()
            ? new(true, timer.Elapsed.TotalMilliseconds, "terminated; Windows console hosts have no portable stop signal")
            : new(false, timer.Elapsed.TotalMilliseconds, "did not stop within 20 s of SIGTERM and was killed");
    }

    public void WriteTail(string reason)
    {
        Console.Error.WriteLine($"--- {reason}; last host output ---");
        foreach (string line in _output.TakeLast(40))
            Console.Error.WriteLine(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false } process)
        {
            Terminate(process);
            await process.WaitForExitAsync();
        }
        _process?.Dispose();
    }

    private ProcessStartInfo StartInfo(string configuration, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? extra)
    {
        var info = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = Path.GetDirectoryName(server)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Like the benchmark: no inherited host, Kestrel, logging or runtime settings, so the run is reproducible.
        foreach (string key in info.Environment.Keys.Where(Inherited).ToArray())
            info.Environment.Remove(key);
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        foreach (var (key, value) in secrets.Concat(extra ?? new Dictionary<string, string>()))
            info.Environment[key] = value;
        info.ArgumentList.Add(server);
        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(configuration);
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static bool Inherited(string key) =>
        key.StartsWith("PulseOps__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Logging__", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) || key.Equals("AllowedHosts", StringComparison.OrdinalIgnoreCase) ||
        (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) && !key.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase));

    private static string DotnetHost()
    {
        string executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            string.Equals(Path.GetFileName(Environment.ProcessPath), executable, StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath : null,
            Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root ? Path.Combine(root, executable) : null
        ];
        return candidates.FirstOrDefault(path => !string.IsNullOrEmpty(path) && File.Exists(path)) ?? "dotnet";
    }

    private static void Terminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    private void Record(object sender, DataReceivedEventArgs entry)
    {
        if (entry.Data is null)
            return;
        _output.Enqueue(entry.Data);
        while (_output.Count > 200 && _output.TryDequeue(out _)) { }
    }
}

static class Native
{
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static extern int Kill(int pid, int signal);
}
