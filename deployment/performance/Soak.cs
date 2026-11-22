#:property PublishAot=false
// PulseOps capacity soak. Run with .NET 10 file-based applications; the procedure is in docs/Staging.md.
//   dotnet run --file deployment/performance/Soak.cs -- --local <published PulseOps.Server.dll> [options]
//   dotnet run --file deployment/performance/Soak.cs -- --target <https origin> --application <slug> --environment <slug>
//       --token-env <VARIABLE> [more --application/--environment/--token-env groups] [--server-pid <id>] [--database <path>] [options]
// Options: --minutes 30 --steady-rate 200 --burst-rate 1000 --burst-seconds 10 --burst-every 5 (minutes) --query-rate 2
//          --event-name smoke-check --report <new file>
// Local mode starts a disposable loopback Development demo server with temporary SQLite, four generated scopes and 500
// generated session IDs, as Benchmark.cs does, and also drives dashboard queries. Remote mode sends ingest load only and never
// signs in as an operator; --server-pid and --database sample the server process and SQLite files when run on the server itself.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

SoakSettings settings;
try
{
    settings = SoakSettings.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(SoakSettings.Usage);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) =>
{
    press.Cancel = true;
    cancellation.Cancel();
};

var startedAt = DateTimeOffset.UtcNow;
var environment = new Dictionary<string, object?>
{
    ["os"] = RuntimeInformation.OSDescription,
    ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
    ["framework"] = RuntimeInformation.FrameworkDescription,
    ["processorCount"] = Environment.ProcessorCount,
    ["mode"] = settings.Server is null ? "remote" : "local"
};
LocalServer? local = null;
Process? watched = null;
SoakOutcome outcome;
try
{
    if (settings.Server is { } server)
    {
        environment["serverAssembly"] = Path.GetFileName(server);
        await using (var file = File.OpenRead(server))
            environment["serverSha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation.Token));
        environment["topology"] = "Development demo host on loopback HTTP, embedded worker, load generator on the same machine";
        Console.Error.WriteLine($"PulseOps soak: starting a local Development host from {server}");
        local = await LocalServer.Start(server, settings.EventName, cancellation.Token);
        using var http = Soak.Client(local.Site, remote: false);
        await local.WaitForDrain(http, TimeSpan.FromSeconds(120), cancellation.Token);
        outcome = await new Soak(settings, http, local.Scopes, new ServerSampler(local.Process), local.Database, operatorApi: true)
            .Run(cancellation.Token);
    }
    else
    {
        environment["target"] = settings.Target!.GetLeftPart(UriPartial.Authority) + "/";
        environment["topology"] = "Remote ingest-only load over HTTPS" +
            (settings.ServerPid is null ? "" : "; server process sampled locally") + (settings.Database is null ? "" : "; SQLite files sampled locally");
        if (settings.ServerPid is int pid)
            watched = Process.GetProcessById(pid);
        using var http = Soak.Client(settings.Target!, remote: true);
        Console.Error.WriteLine($"PulseOps soak: {settings.Minutes} minute(s) against {environment["target"]}");
        outcome = await new Soak(settings, http, settings.Scopes.ToArray(), new ServerSampler(watched), settings.Database, operatorApi: false)
            .Run(cancellation.Token);
    }
}
catch (Exception exception) when (exception is InvalidOperationException or ArgumentException && !cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine($"The soak could not start: {exception.Message}");
    local?.WriteTail();
    return 1;
}
finally
{
    if (local is not null)
        await local.DisposeAsync();
    watched?.Dispose();
}

string report = JsonSerializer.Serialize(new
{
    tool = "PulseOps capacity soak",
    startedAt,
    finishedAt = DateTimeOffset.UtcNow,
    environment,
    parameters = new
    {
        minutes = settings.Minutes, steadyEventsPerSecond = settings.SteadyRate, burstEventsPerSecond = settings.BurstRate,
        burstSeconds = settings.BurstSeconds, burstEveryMinutes = settings.BurstEvery, queryRatePerSecond = settings.Server is null ? 0 : settings.QueryRate,
        batchSize = Soak.BatchSize, sampleSeconds = Soak.SampleSeconds, maxInFlight = Soak.MaxInFlight, sessions = Soak.Sessions,
        eventName = settings.EventName,
        scopes = (local?.Scopes ?? settings.Scopes.ToArray()).Select(scope => new { scope.Application, scope.Environment })
    },
    outcome.Timeline,
    outcome.Summary,
    outcome.Gates,
    result = outcome.Passed ? "pass" : "fail"
}, Soak.Json);
Console.WriteLine(report);
if (settings.Report is { } path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
    await output.WriteLineAsync(report);
}
foreach (var gate in outcome.Gates)
    Console.Error.WriteLine($"{gate.Status.ToUpperInvariant(),-12} {gate.Name,-22} observed {gate.Observed}; threshold {gate.Threshold}");
Console.Error.WriteLine($"Soak {(outcome.Passed ? "passed" : "FAILED")}.");
return outcome.Passed ? 0 : 1;

sealed record TargetScope(string Application, string Environment, string Token)
{
    // Never render the credential.
    public override string ToString() => $"{Application}/{Environment}";
}

sealed class SoakSettings
{
    public const string Usage = """
        Usage:
          Soak --local <published PulseOps.Server.dll> [options]
          Soak --target <https origin> --application <slug> --environment <slug> --token-env <VARIABLE>
               [--application <slug> --environment <slug> --token-env <VARIABLE> ...] [--server-pid <id>] [--database <path>] [options]
        Options: --minutes 30 (1-1440)  --steady-rate 200  --burst-rate 1000 (events/s, 1-5000)  --burst-seconds 10
                 --burst-every 5 (minutes)  --query-rate 2 (dashboard queries/s in local mode, 0-20)
                 --event-name smoke-check  --report <new file>
        """;

    public string? Server { get; private init; }
    public Uri? Target { get; private init; }
    public IReadOnlyList<TargetScope> Scopes { get; private init; } = [];
    public int Minutes { get; private init; } = 30;
    public int SteadyRate { get; private init; } = 200;
    public int BurstRate { get; private init; } = 1000;
    public int BurstSeconds { get; private init; } = 10;
    public int BurstEvery { get; private init; } = 5;
    public double QueryRate { get; private init; } = 2;
    public string EventName { get; private init; } = "smoke-check";
    public int? ServerPid { get; private init; }
    public string? Database { get; private init; }
    public string? Report { get; private init; }

    public static SoakSettings Parse(string[] args)
    {
        string[] repeatable = ["--application", "--environment", "--token-env"];
        string[] single = ["--local", "--target", "--minutes", "--steady-rate", "--burst-rate", "--burst-seconds", "--burst-every",
            "--query-rate", "--event-name", "--server-pid", "--database", "--report"];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = repeatable.ToDictionary(name => name, _ => new List<string>());
        for (int i = 0; i < args.Length; i += 2)
        {
            bool known = repeatable.Contains(args[i]) || single.Contains(args[i]);
            if (!known || i + 1 >= args.Length || (single.Contains(args[i]) && !values.TryAdd(args[i], args[i + 1])))
                throw new ArgumentException(Regex.IsMatch(args[i], @"\A--[a-z-]{1,32}\z")
                    ? $"Unexpected, repeated or incomplete option '{args[i]}'." : $"Unexpected argument at position {i + 1}.");
            if (repeatable.Contains(args[i]))
                groups[args[i]].Add(args[i + 1]);
        }

        int Number(string name, int fallback, int minimum, int maximum) => !values.TryGetValue(name, out var text) ? fallback :
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= minimum && number <= maximum
                ? number : throw new ArgumentException($"{name} must be an integer from {minimum} to {maximum}.");

        string? report = values.TryGetValue("--report", out var file) ? Path.GetFullPath(file) : null;
        if (report is not null && File.Exists(report))
            throw new ArgumentException("The report file already exists. Choose a new path.");
        string eventName = values.GetValueOrDefault("--event-name") ?? "smoke-check";
        if (!Regex.IsMatch(eventName, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z"))
            throw new ArgumentException("--event-name must be an allowlisted telemetry alias.");
        double queryRate = !values.TryGetValue("--query-rate", out var rate) ? 2 :
            double.TryParse(rate, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double parsed) && parsed is >= 0 and <= 20
                ? parsed : throw new ArgumentException("--query-rate must be from 0 to 20 queries per second.");
        int minutes = Number("--minutes", 30, 1, 1440), burstEvery = Number("--burst-every", 5, 1, 1440);
        int burstSeconds = Number("--burst-seconds", 10, 1, 3600);
        if (burstSeconds >= burstEvery * 60)
            throw new ArgumentException("--burst-seconds must be shorter than the --burst-every interval.");

        string? server = null;
        Uri? target = null;
        var scopes = new List<TargetScope>();
        if (values.TryGetValue("--local", out var local))
        {
            if (values.ContainsKey("--target") || groups.Values.Any(list => list.Count > 0) || values.ContainsKey("--server-pid") || values.ContainsKey("--database"))
                throw new ArgumentException("--local cannot be combined with remote target options.");
            server = Path.GetFullPath(local);
            if (!File.Exists(server) || !server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--local requires the published PulseOps.Server.dll.");
        }
        else
        {
            if (!values.TryGetValue("--target", out var origin) || !Uri.TryCreate(origin, UriKind.Absolute, out target) ||
                target.Scheme != Uri.UriSchemeHttps || target.AbsolutePath != "/" || target.Query.Length > 0 || target.Fragment.Length > 0 ||
                target.UserInfo.Length > 0)
                throw new ArgumentException("Pass --local, or --target with the HTTPS site origin (no path, query or credentials).");
            int count = groups["--application"].Count;
            if (count is < 1 or > 16 || groups.Values.Any(list => list.Count != count))
                throw new ArgumentException("Give 1 to 16 complete --application, --environment and --token-env groups.");
            for (int i = 0; i < count; i++)
            {
                string application = groups["--application"][i], scopeEnvironment = groups["--environment"][i], variable = groups["--token-env"][i];
                if (!Regex.IsMatch(application, @"\A[a-z0-9][a-z0-9-]{0,39}\z") || !Regex.IsMatch(scopeEnvironment, @"\A[a-z0-9][a-z0-9-]{0,39}\z"))
                    throw new ArgumentException("--application and --environment require PulseOps slugs.");
                if (!Regex.IsMatch(variable, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z"))
                    throw new ArgumentException("--token-env must name an environment variable.");
                string? token = System.Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(token) || token.Length > 256 || token.Any(c => c < 33 || c > 126))
                    throw new ArgumentException($"Environment variable {variable} must hold a scoped ingest credential.");
                scopes.Add(new(application, scopeEnvironment, token));
            }
            if (scopes.GroupBy(scope => (scope.Application, scope.Environment)).Any(group => group.Count() > 1))
                throw new ArgumentException("Each application and environment may be given once.");
        }

        string? database = values.TryGetValue("--database", out var path) ? Path.GetFullPath(path) : null;
        if (database is not null && !File.Exists(database))
            throw new ArgumentException("--database must name the server's existing SQLite file.");
        return new()
        {
            Server = server, Target = target, Scopes = scopes, Minutes = minutes,
            SteadyRate = Number("--steady-rate", 200, 1, 5000), BurstRate = Number("--burst-rate", 1000, 1, 5000),
            BurstSeconds = burstSeconds, BurstEvery = burstEvery, QueryRate = queryRate, EventName = eventName,
            ServerPid = values.ContainsKey("--server-pid") ? Number("--server-pid", 0, 1, int.MaxValue) : null,
            Database = database, Report = report
        };
    }
}

class Counters
{
    public long Offered, Accepted, Requests, Status202, Status429, Status5xx, OtherRejected, TransportErrors;
    public readonly ConcurrentQueue<double> Latency = new();

    public void Count(int status, int accepted, double milliseconds)
    {
        Interlocked.Increment(ref Requests);
        Interlocked.Add(ref Accepted, accepted);
        switch (status)
        {
            case 202: Interlocked.Increment(ref Status202); break;
            case 429: Interlocked.Increment(ref Status429); break;
            case >= 500: Interlocked.Increment(ref Status5xx); break;
            case 0: Interlocked.Increment(ref TransportErrors); break;
            default: Interlocked.Increment(ref OtherRejected); break;
        }
        Latency.Enqueue(milliseconds);
    }
}

sealed class Interval : Counters
{
    public long Queries;
    public readonly ConcurrentQueue<double> QueryLatency = new();
}

sealed class EndpointTotals
{
    public readonly Dictionary<int, int> Statuses = [];
    public readonly List<double> Latency = [];
}

sealed record ProcessSample(double? CpuPercent, TimeSpan Cpu, long WorkingSet, long? PrivateBytes, int? Threads, bool Exited);

sealed record Sample(double AtSeconds, string Phase, long Offered, long Accepted, long Requests, long Status429, long Status5xx,
    long OtherRejected, long TransportErrors, double? IntakeP50Ms, double? IntakeP95Ms, double? IntakeP99Ms, long Queries,
    double? QueryP95Ms, double? CpuPercent, long? WorkingSetBytes, long? PrivateBytes, int? Threads, long? DatabaseBytes,
    long? WalBytes, long? ShmBytes, long? Pending, double? OldestPendingAgeSeconds, int InFlight);

sealed record Gate(string Name, string Status, string Observed, string Threshold);

sealed record SoakOutcome(IReadOnlyList<Sample> Timeline, object Summary, IReadOnlyList<Gate> Gates, bool Passed);

/// <summary>Samples a server process: CPU from TotalProcessorTime deltas, memory and threads. Unavailable values are null.</summary>
sealed class ServerSampler(Process? process)
{
    private TimeSpan _cpu;
    private long _at;

    public bool Available => process is not null;

    /// <summary>Exit check for the load loop. It must not reset the CPU baseline that <see cref="Read"/> measures from.</summary>
    public bool HasExited()
    {
        if (process is null)
            return false;
        try
        {
            process.Refresh();
            return process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public ProcessSample? Read()
    {
        if (process is null)
            return null;
        try
        {
            process.Refresh();
            if (process.HasExited)
                return new(null, _cpu, 0, null, null, true);
            var cpu = process.TotalProcessorTime;
            long now = Stopwatch.GetTimestamp();
            double? percent = _at == 0 ? null : (cpu - _cpu).TotalMilliseconds / Stopwatch.GetElapsedTime(_at, now).TotalMilliseconds * 100;
            _cpu = cpu;
            _at = now;
            long privateBytes = process.PrivateMemorySize64;
            int? threads;
            try
            {
                threads = process.Threads.Count;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                threads = null;
            }
            // macOS reports no private bytes; record null rather than a misleading zero.
            return new(percent is null ? null : Math.Round(percent.Value, 1), cpu, process.WorkingSet64,
                privateBytes > 0 ? privateBytes : null, threads, false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

sealed class Soak(SoakSettings settings, HttpClient http, TargetScope[] scopes, ServerSampler server, string? database, bool operatorApi)
{
    public const int BatchSize = 25, SampleSeconds = 10, MaxInFlight = 256, Sessions = 500;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Endpoints = ["overview", "dependencies", "events", "history", "monitors", "audit", "platform"];
    private readonly Stopwatch _clock = new();
    private readonly Counters[] _phases = [new(), new()];
    private readonly Counters _queries = new();
    private readonly Dictionary<string, EndpointTotals> _endpoints = Endpoints.ToDictionary(name => name, _ => new EndpointTotals());
    private readonly List<Sample> _timeline = [];
    private readonly SemaphoreSlim _inflight = new(MaxInFlight);
    private readonly Guid[][] _sessions = scopes.Select(_ => Enumerable.Range(0, Math.Max(1, Sessions / scopes.Length))
        .Select(_ => Guid.NewGuid()).ToArray()).ToArray();
    private Interval _interval = new();
    private double _lastSample;
    private long _clientWaits;
    private long _peakPending;
    private double? _peakPendingAge;

    public static HttpClient Client(Uri site, bool remote) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = remote,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    }) { BaseAddress = site, Timeout = TimeSpan.FromSeconds(30) };

    public async Task<SoakOutcome> Run(CancellationToken cancellation)
    {
        double seconds = settings.Minutes * 60.0;
        var initial = server.Read();
        _clock.Start();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var queries = Query(load.Token);
        var sampling = Sampling(load.Token);
        bool aborted = false;
        try
        {
            await Generate(seconds, load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            aborted = true;
        }
        double loadSeconds = _clock.Elapsed.TotalSeconds;
        await load.CancelAsync();
        await Task.WhenAll(queries, sampling);
        // Let accepted requests finish so every offered batch is accounted for.
        for (int i = 0; i < MaxInFlight; i++)
            if (!await _inflight.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None))
                break;
        await Record(Phase(loadSeconds, final: true));
        var end = server.Read();

        bool? drained = null;
        double drainSeconds = 0;
        long? pending = null;
        if (operatorApi && !cancellation.IsCancellationRequested)
        {
            var drain = Stopwatch.StartNew();
            while (true)
            {
                pending = (await Platform(CancellationToken.None))?.Pending;
                if (pending == 0 || drain.Elapsed >= TimeSpan.FromSeconds(120))
                    break;
                if (_clock.Elapsed.TotalSeconds - _lastSample >= SampleSeconds)
                    await Record("drain");
                await Task.Delay(1000, CancellationToken.None);
            }
            drained = pending == 0;
            drainSeconds = Math.Round(drain.Elapsed.TotalSeconds, 1);
            await Record("drain");
        }
        return Outcome(seconds, loadSeconds, initial, end, drained, drainSeconds, pending, aborted || cancellation.IsCancellationRequested);
    }

    // Open-loop pacing: events are due at the configured rate whether or not earlier requests have completed.
    private async Task Generate(double seconds, CancellationToken cancellation)
    {
        long dispatched = 0;
        int sequence = 0;
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (true)
        {
            double now = Math.Min(_clock.Elapsed.TotalSeconds, seconds);
            bool final = now >= seconds;
            long due = (long)Due(now);
            int phase = InBurst(now) ? 1 : 0;
            while (due - dispatched >= BatchSize || (final && due > dispatched))
            {
                int count = (int)Math.Min(BatchSize, due - dispatched);
                if (!_inflight.Wait(0))
                {
                    Interlocked.Increment(ref _clientWaits);
                    await _inflight.WaitAsync(cancellation);
                }
                dispatched += count;
                _ = Send(sequence++, count, phase, cancellation);
            }
            if (final)
                return;
            if (server.HasExited())
                throw new InvalidOperationException("The PulseOps server process exited during the soak.");
            await tick.WaitForNextTickAsync(cancellation);
        }
    }

    private double Due(double t) => settings.SteadyRate * t + (settings.BurstRate - settings.SteadyRate) * BurstTime(t);

    // Bursts start at every whole multiple of the interval after the first, and are shorter than the interval.
    private double BurstTime(double t)
    {
        double period = settings.BurstEvery * 60.0;
        int completed = (int)(t / period);
        return completed == 0 ? 0 : (completed - 1) * (double)settings.BurstSeconds + Math.Min(settings.BurstSeconds, t - completed * period);
    }

    private bool InBurst(double t) => t >= settings.BurstEvery * 60.0 && t % (settings.BurstEvery * 60.0) < settings.BurstSeconds;

    private async Task Send(int sequence, int count, int phase, CancellationToken cancellation)
    {
        try
        {
            int index = sequence % scopes.Length;
            var scope = scopes[index];
            var sessions = _sessions[index];
            var occurred = DateTimeOffset.UtcNow;
            var events = new object[count];
            for (int i = 0; i < count; i++)
            {
                bool failed = index == 0 ? i % 3 == 0 : (sequence + i) % 7 == 0;
                events[i] = new
                {
                    id = Guid.NewGuid(), occurredAt = occurred, version = "1.0.0-soak",
                    sessionId = sessions[(sequence / scopes.Length * BatchSize + i) % sessions.Length], kind = "api",
                    name = settings.EventName, outcome = failed ? "failure" : "success", durationMs = Random.Shared.Next(25, 1800),
                    statusCode = failed ? 503 : 200, traceId = ActivityTraceId.CreateRandom().ToHexString(),
                    spanId = ActivitySpanId.CreateRandom().ToHexString()
                };
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/telemetry/v1/batch")
            {
                Content = JsonContent.Create(new { schemaVersion = 1, application = scope.Application, environment = scope.Environment, events })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scope.Token);
            Interlocked.Add(ref Volatile.Read(ref _interval).Offered, count);
            Interlocked.Add(ref _phases[phase].Offered, count);
            var timer = Stopwatch.StartNew();
            int status = 0, accepted = 0;
            try
            {
                using var response = await http.SendAsync(request, cancellation);
                status = (int)response.StatusCode;
                if (status == 202)
                {
                    using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation), default, cancellation);
                    accepted = result.RootElement.GetProperty("accepted").GetInt32();
                }
            }
            catch (HttpRequestException) { status = 0; }
            catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { status = 0; }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { accepted = 0; }
            double milliseconds = timer.Elapsed.TotalMilliseconds;
            Volatile.Read(ref _interval).Count(status, accepted, milliseconds);
            _phases[phase].Count(status, accepted, milliseconds);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            _inflight.Release();
        }
    }

    private async Task Query(CancellationToken cancellation)
    {
        if (!operatorApi || settings.QueryRate <= 0)
            return;
        var period = TimeSpan.FromSeconds(1 / settings.QueryRate);
        try
        {
            for (int i = 0; !cancellation.IsCancellationRequested; i++)
            {
                var started = _clock.Elapsed;
                string name = Endpoints[i % Endpoints.Length];
                var scope = scopes[i / Endpoints.Length % scopes.Length];
                string filter = $"application={Uri.EscapeDataString(scope.Application)}&environment={Uri.EscapeDataString(scope.Environment)}";
                string path = name switch
                {
                    "overview" => $"/api/v1/overview?{filter}&hours=1",
                    "dependencies" => $"/api/v1/dependencies?{filter}&hours=1",
                    "events" => $"/api/v1/events?{filter}&hours=1&q=outcome%3Afailure",
                    "history" => $"/api/v1/history?{filter}&days=30",
                    "monitors" => $"/api/v1/monitors?{filter}",
                    "audit" => "/api/v1/admin/audit?limit=50",
                    _ => "/api/v1/platform"
                };
                var timer = Stopwatch.StartNew();
                int status = 0;
                try
                {
                    using var response = await http.GetAsync(path, cancellation);
                    status = (int)response.StatusCode;
                    await response.Content.CopyToAsync(Stream.Null, cancellation);
                }
                catch (HttpRequestException) { status = 0; }
                catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { status = 0; }
                double milliseconds = timer.Elapsed.TotalMilliseconds;
                var totals = _endpoints[name];
                totals.Statuses[status] = totals.Statuses.GetValueOrDefault(status) + 1;
                // 404 means the endpoint is absent from this build. It is reported per endpoint and not counted as a query.
                if (status != 404)
                {
                    totals.Latency.Add(milliseconds);
                    _queries.Count(status, 0, milliseconds);
                    var interval = Volatile.Read(ref _interval);
                    Interlocked.Increment(ref interval.Queries);
                    interval.QueryLatency.Enqueue(milliseconds);
                }
                var wait = period - (_clock.Elapsed - started);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task Sampling(CancellationToken cancellation)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(SampleSeconds));
        try
        {
            while (await tick.WaitForNextTickAsync(cancellation))
                await Record(Phase(_clock.Elapsed.TotalSeconds, final: false));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private string Phase(double at, bool final)
    {
        double start = Math.Min(_lastSample, settings.Minutes * 60.0), end = Math.Min(at, settings.Minutes * 60.0);
        return end > start && BurstTime(end) - BurstTime(start) > 0 ? "burst" : final && end <= start ? "drain" : "steady";
    }

    private async Task Record(string phase)
    {
        double at = _clock.Elapsed.TotalSeconds;
        var finished = Interlocked.Exchange(ref _interval, new Interval());
        await Task.Delay(50);
        var process = server.Read();
        var platform = await Platform(CancellationToken.None);
        if (platform is { } observed)
        {
            _peakPending = Math.Max(_peakPending, observed.Pending);
            if (observed.OldestAgeSeconds is double age)
                _peakPendingAge = Math.Max(_peakPendingAge ?? 0, age);
        }
        var intake = Sorted(finished.Latency);
        var query = Sorted(finished.QueryLatency);
        var sample = new Sample(Math.Round(at, 1), phase, finished.Offered, finished.Accepted, finished.Requests, finished.Status429,
            finished.Status5xx, finished.OtherRejected, finished.TransportErrors, Percentile(intake, .50), Percentile(intake, .95),
            Percentile(intake, .99), finished.Queries, Percentile(query, .95), process?.CpuPercent, process?.WorkingSet,
            process?.PrivateBytes, process?.Threads, Size(database), Size(database is null ? null : database + "-wal"),
            Size(database is null ? null : database + "-shm"), platform?.Pending, platform?.OldestAgeSeconds is double oldest ? Math.Round(oldest, 1) : null,
            MaxInFlight - _inflight.CurrentCount);
        _timeline.Add(sample);
        _lastSample = at;
        Console.Error.WriteLine(
            $"[{sample.AtSeconds,7:0.0}s {phase,-6}] offered {sample.Offered,6} accepted {sample.Accepted,6} 429 {sample.Status429} 5xx {sample.Status5xx} " +
            $"net {sample.TransportErrors} p95 {Text(sample.IntakeP95Ms, "ms")} | queries {sample.Queries} p95 {Text(sample.QueryP95Ms, "ms")} | " +
            $"cpu {Text(sample.CpuPercent, "%")} ws {Mebibytes(sample.WorkingSetBytes)} | wal {Mebibytes(sample.WalBytes)} | pending {sample.Pending?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
    }

    private async Task<(long Pending, double? OldestAgeSeconds)?> Platform(CancellationToken cancellation)
    {
        if (!operatorApi)
            return null;
        try
        {
            using var response = await http.GetAsync("/api/v1/platform", cancellation);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation), default, cancellation);
            var root = document.RootElement;
            double? age = root.TryGetProperty("oldestPendingAt", out var oldest) && oldest.ValueKind == JsonValueKind.String
                ? Math.Max(0, (DateTimeOffset.UtcNow - oldest.GetDateTimeOffset()).TotalSeconds) : null;
            return (root.GetProperty("pending").GetInt64(), age);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private SoakOutcome Outcome(double seconds, double loadSeconds, ProcessSample? initial, ProcessSample? end, bool? drained,
        double drainSeconds, long? finalPending, bool aborted)
    {
        var steady = _phases[0];
        var burst = _phases[1];
        double? acceptance = steady.Offered == 0 ? null : Math.Round(100.0 * steady.Accepted / steady.Offered, 3);
        long serverErrors = steady.Status5xx + burst.Status5xx + _queries.Status5xx;
        long transport = steady.TransportErrors + burst.TransportErrors + _queries.TransportErrors;
        var loadSamples = _timeline.Where(sample => sample.Phase != "drain").ToArray();
        var secondHalf = loadSamples.Where(sample => sample.AtSeconds >= seconds / 2 && sample.WorkingSetBytes is not null).ToArray();
        (double Slope, double Growth)? memory = null;
        if (secondHalf.Length >= 3 && secondHalf[^1].AtSeconds > secondHalf[0].AtSeconds)
        {
            // Least-squares fit of working set over time; growth compares the fitted end of the half with its fitted start.
            double meanT = secondHalf.Average(sample => sample.AtSeconds), meanW = secondHalf.Average(sample => (double)sample.WorkingSetBytes!.Value);
            double slope = secondHalf.Sum(sample => (sample.AtSeconds - meanT) * (sample.WorkingSetBytes!.Value - meanW)) /
                secondHalf.Sum(sample => Math.Pow(sample.AtSeconds - meanT, 2));
            double start = meanW + slope * (secondHalf[0].AtSeconds - meanT);
            memory = (slope * 3600 / 1048576, start > 0 ? slope * (secondHalf[^1].AtSeconds - secondHalf[0].AtSeconds) / start * 100 : double.PositiveInfinity);
        }
        long? peakWal = _timeline.Max(sample => sample.WalBytes);
        double? meanCpu = initial is not null && end is not null && !end.Exited && loadSeconds > 0
            ? Math.Round((end.Cpu - initial.Cpu).TotalSeconds / loadSeconds * 100, 1) : null;

        var gates = new List<Gate>
        {
            new("steady-acceptance", acceptance is >= 99 ? "pass" : "fail",
                acceptance is null ? "no steady events offered" : $"{acceptance:0.###}% ({steady.Accepted} of {steady.Offered})", ">= 99%"),
            new("zero-5xx", serverErrors == 0 ? "pass" : "fail", $"{serverErrors} responses (intake {steady.Status5xx + burst.Status5xx}, queries {_queries.Status5xx})", "0"),
            new("zero-transport-errors", transport == 0 ? "pass" : "fail", $"{transport} failed connections or timeouts", "0"),
            drained is null
                ? new("pending-drained", "not-measured", "requires the operator platform API (local mode)", "0 pending within 120 s")
                : new("pending-drained", drained.Value ? "pass" : "fail", $"{finalPending?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} pending after {drainSeconds:0.0} s", "0 pending within 120 s"),
            memory is null
                ? new("working-set-growth", "not-measured", server.Available ? "fewer than 3 second-half samples" : "server process not sampled", "< 25% over the second half")
                : new("working-set-growth", memory.Value.Growth < 25 ? "pass" : "fail",
                    $"{memory.Value.Growth:0.0}% ({memory.Value.Slope:0.0} MiB/hour)", "< 25% over the second half"),
            peakWal is null
                ? new("wal-size", "not-measured", "SQLite files not sampled", "< 256 MiB")
                : new("wal-size", peakWal < 256L * 1048576 ? "pass" : "fail", $"peak {peakWal / 1048576.0:0.0} MiB", "< 256 MiB")
        };
        if (aborted)
            gates.Add(new("completed", "fail", "the soak stopped before its configured duration", $"{settings.Minutes} minute(s)"));

        var summary = new
        {
            durationSeconds = Math.Round(loadSeconds, 1),
            steady = Describe(steady),
            burst = Describe(burst),
            queries = new
            {
                requests = _queries.Requests,
                p50Ms = Percentile(Sorted(_queries.Latency), .50),
                p95Ms = Percentile(Sorted(_queries.Latency), .95),
                p99Ms = Percentile(Sorted(_queries.Latency), .99),
                endpoints = _endpoints.ToDictionary(entry => entry.Key, entry => new
                {
                    statusCounts = entry.Value.Statuses.OrderBy(status => status.Key).ToDictionary(status => status.Key.ToString(CultureInfo.InvariantCulture), status => status.Value),
                    absent = entry.Value.Statuses.Keys.All(status => status == 404) && entry.Value.Statuses.Count > 0,
                    p50Ms = Percentile(entry.Value.Latency.Order().ToArray(), .50),
                    p95Ms = Percentile(entry.Value.Latency.Order().ToArray(), .95)
                })
            },
            server = new
            {
                meanCpuPercentOfOneCore = meanCpu,
                peakCpuPercentOfOneCore = _timeline.Max(sample => sample.CpuPercent),
                peakWorkingSetBytes = _timeline.Max(sample => sample.WorkingSetBytes),
                peakPrivateBytes = _timeline.Max(sample => sample.PrivateBytes),
                peakThreads = _timeline.Max(sample => sample.Threads),
                workingSetSlopeMiBPerHour = memory is null ? (double?)null : Math.Round(memory.Value.Slope, 2),
                secondHalfWorkingSetGrowthPercent = memory is null ? (double?)null : Math.Round(memory.Value.Growth, 2),
                peakPending = operatorApi ? _peakPending : (long?)null,
                peakOldestPendingAgeSeconds = _peakPendingAge is null ? (double?)null : Math.Round(_peakPendingAge.Value, 1),
                pendingAfterDrain = finalPending,
                drainSeconds = operatorApi ? drainSeconds : (double?)null,
                peakWalBytes = peakWal,
                finalDatabaseBytes = _timeline.LastOrDefault()?.DatabaseBytes,
                loadGeneratorWaitsForInFlightSlot = _clientWaits
            }
        };
        return new(_timeline, summary, gates, gates.All(gate => gate.Status != "fail"));
    }

    private static object Describe(Counters counters)
    {
        var latency = Sorted(counters.Latency);
        return new
        {
            offered = counters.Offered, accepted = counters.Accepted,
            acceptancePercent = counters.Offered == 0 ? (double?)null : Math.Round(100.0 * counters.Accepted / counters.Offered, 3),
            requests = counters.Requests, status202 = counters.Status202, status429 = counters.Status429, status5xx = counters.Status5xx,
            otherRejected = counters.OtherRejected, transportErrors = counters.TransportErrors,
            p50Ms = Percentile(latency, .50), p95Ms = Percentile(latency, .95), p99Ms = Percentile(latency, .99),
            maxMs = latency.Length == 0 ? (double?)null : Math.Round(latency[^1], 2)
        };
    }

    private static double[] Sorted(IEnumerable<double> values) => values.Order().ToArray();

    // Nearest-rank percentile, as in Benchmark.cs.
    private static double? Percentile(double[] sorted, double fraction) =>
        sorted.Length == 0 ? null : Math.Round(sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1], 2);

    private static long? Size(string? path)
    {
        if (path is null)
            return null;
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Text(double? value, string unit) => value is null ? "n/a" : $"{value:0.0} {unit}";

    private static string Mebibytes(long? bytes) => bytes is null ? "n/a" : $"{bytes / 1048576.0:0.0} MiB";
}

/// <summary>A disposable Development demo host, configured like Benchmark.cs, with credentials passed through its environment.</summary>
sealed class LocalServer : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly DirectoryInfo _root;

    private LocalServer(DirectoryInfo root, Process process, Uri site, string database, TargetScope[] scopes)
    {
        _root = root;
        Process = process;
        Site = site;
        Database = database;
        Scopes = scopes;
    }

    public Process Process { get; }
    public Uri Site { get; }
    public string Database { get; }
    public TargetScope[] Scopes { get; }

    public static async Task<LocalServer> Start(string server, string eventName, CancellationToken cancellation)
    {
        var root = Directory.CreateTempSubdirectory("pulseops-soak-");
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        string database = Path.Combine(root.FullName, "soak.db");
        string configuration = Path.Combine(root.FullName, "soak.json");
        var scopes = Enumerable.Range(1, 4)
            .Select(i => new TargetScope($"load-app-{i}", "load", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))).ToArray();
        await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
        {
            Logging = new { LogLevel = new { Default = "Warning" } },
            PulseOps = new
            {
                DatabasePath = database,
                Demo = true,
                MinimumFreeMegabytes = 64,
                Scopes = scopes.Select(scope => new
                {
                    scope.Application, scope.Environment, Label = $"Soak {scope.Application}", Demo = true, AllowedNames = new[] { eventName }
                })
            }
        }), cancellation);

        var info = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = Path.GetDirectoryName(server)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string key in info.Environment.Keys.Where(key => key.StartsWith("PulseOps__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("AllowedHosts", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        info.Environment["DOTNET_ENVIRONMENT"] = "Development";
        for (int i = 0; i < scopes.Length; i++)
            info.Environment[$"PulseOps__Scopes__{i}__IngestToken"] = scopes[i].Token;
        var site = new Uri($"http://127.0.0.1:{port}/");
        foreach (string argument in new[] { server, "--config", configuration, "--urls", site.GetLeftPart(UriPartial.Authority) })
            info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the local soak server.");
        var host = new LocalServer(root, process, site, database, scopes);
        process.OutputDataReceived += host.Record;
        process.ErrorDataReceived += host.Record;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var http = Soak.Client(site, remote: false);
        var readiness = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var response = await http.GetAsync("/health/ready", cancellation);
                if (response.StatusCode == HttpStatusCode.OK)
                    return host;
            }
            catch (HttpRequestException) { }
            if (process.HasExited || readiness.Elapsed > TimeSpan.FromSeconds(60))
            {
                await host.DisposeAsync();
                throw new InvalidOperationException("The local soak server did not become ready within 60 seconds.");
            }
            await Task.Delay(250, cancellation);
        }
    }

    /// <summary>Waits for demo seeding to be processed so the measurement starts from an empty queue.</summary>
    public async Task WaitForDrain(HttpClient http, TimeSpan limit, CancellationToken cancellation)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < limit)
        {
            using var document = await http.GetFromJsonAsync<JsonDocument>("/api/v1/platform", cancellation)
                ?? throw new InvalidOperationException("The platform endpoint returned no data.");
            if (document.RootElement.GetProperty("pending").GetInt64() == 0)
                return;
            await Task.Delay(250, cancellation);
        }
        throw new InvalidOperationException("Demo seed data was not processed within 120 seconds.");
    }

    public void WriteTail()
    {
        foreach (string line in _logs.TakeLast(40))
            Console.Error.WriteLine(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            try
            {
                Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            await Process.WaitForExitAsync();
        }
        Process.Dispose();
        try
        {
            _root.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not delete {_root.FullName}: {exception.GetType().Name}. Remove it manually.");
        }
    }

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

    private void Record(object sender, DataReceivedEventArgs entry)
    {
        if (entry.Data is null)
            return;
        _logs.Enqueue(entry.Data);
        while (_logs.Count > 200 && _logs.TryDequeue(out _)) { }
    }
}
