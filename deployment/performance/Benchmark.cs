#:property PublishAot=false
// Launch with .NET 10 file-based applications. This harness starts its own loopback demo server.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

if (args.Length is < 1 or > 5 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run --file deployment/performance/Benchmark.cs -- <published server DLL> [steady seconds] [burst seconds] [steady events/sec] [burst events/sec]");
    return 2;
}

int steadySeconds = Number(1, 45, 600);
int burstSeconds = Number(2, 10, 60);
int steadyRate = Number(3, 200, 5000);
int burstRate = Number(4, 1000, 5000);
string server = Path.GetFullPath(args[0]);
int port;
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    port = ((IPEndPoint)listener.LocalEndpoint).Port;
}

var parent = Directory.CreateTempSubdirectory("pulseops-benchmark-");
var configuration = Path.Combine(parent.FullName, "benchmark.json");
var applications = Enumerable.Range(0, 4).Select(i => $"load-app-{i + 1}").ToArray();
var sessions = applications.Select(_ => Enumerable.Range(0, 125).Select(_ => Guid.NewGuid()).ToArray()).ToArray();
const string tokenPrefix = "local-benchmark-credential-";
const string endpointName = "benchmark-read";
await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
{
    Logging = new { LogLevel = new { Default = "Warning" } },
    PulseOps = new
    {
        DatabasePath = Path.Combine(parent.FullName, "benchmark.db"),
        Demo = true,
        MinimumFreeMegabytes = 64,
        Scopes = applications.Select((application, i) => new
        {
            Application = application, Environment = "load", Label = application, Demo = true,
            AllowedNames = new[] { endpointName }, IngestToken = tokenPrefix + i
        })
    }
}));

var processInfo = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
{
    WorkingDirectory = Path.GetDirectoryName(server)!,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
foreach (string key in processInfo.Environment.Keys.Where(key => key.StartsWith("PulseOps__", StringComparison.OrdinalIgnoreCase) ||
    key.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase) || key.Equals("AllowedHosts", StringComparison.OrdinalIgnoreCase)).ToArray())
    processInfo.Environment.Remove(key);
processInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
processInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
string origin = $"http://127.0.0.1:{port}";
foreach (string argument in new[] { server, "--config", configuration, "--urls", origin })
    processInfo.ArgumentList.Add(argument);

var logs = new ConcurrentQueue<string>();
using var process = Process.Start(processInfo) ?? throw new InvalidOperationException("Could not start the local benchmark server.");
process.OutputDataReceived += RecordLog;
process.ErrorDataReceived += RecordLog;
process.BeginOutputReadLine();
process.BeginErrorReadLine();
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
{
    BaseAddress = new Uri(origin),
    Timeout = TimeSpan.FromSeconds(8)
};
try
{
    var readiness = Stopwatch.StartNew();
    while (!await Ready())
    {
        if (process.HasExited || readiness.Elapsed > TimeSpan.FromSeconds(30))
            throw new InvalidOperationException("The benchmark server did not become ready. " + string.Join(Environment.NewLine, logs));
        await Task.Delay(200);
    }

    await WaitForDrain();
    var initial = await Platform();
    process.Refresh();
    TimeSpan cpuBefore = process.TotalProcessorTime;
    long peakRss = process.WorkingSet64;
    long peakPending = 0;
    var queries = new ConcurrentBag<Sample>();
    var measurement = Stopwatch.StartNew();
    using var active = new CancellationTokenSource();
    Task poller = Poll(active.Token);
    PhaseResult steady;
    PhaseResult burst;
    try
    {
        steady = await Phase(steadySeconds, steadyRate);
        burst = await Phase(burstSeconds, burstRate);
        await WaitForDrain();
    }
    finally
    {
        await active.CancelAsync();
        await poller;
    }

    var final = await Platform();
    process.Refresh();
    TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
    var output = new
    {
        measurement = "Loopback development Kestrel, embedded worker, four generated scopes, 500 virtual session IDs",
        steady,
        burst,
        dashboard = Describe(queries),
        server = new
        {
            wallSeconds = Math.Round(measurement.Elapsed.TotalSeconds, 2),
            cpuSeconds = Math.Round(cpu.TotalSeconds, 2),
            meanPercentOneCore = Math.Round(cpu / measurement.Elapsed * 100, 1),
            peakSampledWorkingSetBytes = peakRss,
            peakSampledPending = peakPending,
            pendingAfterDrain = final.Pending,
            processedDelta = final.Processed - initial.Processed,
            databaseStartBytes = initial.Bytes,
            databaseEndBytes = final.Bytes,
            databaseGrowthBytes = final.Bytes - initial.Bytes
        }
    };
    Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    return steady.Accepted + burst.Accepted == steady.Offered + burst.Offered && final.Pending == 0 && queries.All(s => s.Status == 200)
        ? 0
        : 1;

    async Task<PhaseResult> Phase(int seconds, int eventsPerSecond)
    {
        const int batchSize = 25;
        int offered = seconds * eventsPerSecond;
        int count = (offered + batchSize - 1) / batchSize;
        var requests = new ConcurrentBag<Sample>();
        var phase = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (batch, cancellation) =>
            {
                int start = batch * batchSize;
                TimeSpan delay = TimeSpan.FromSeconds((double)start / eventsPerSecond) - phase.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
                int index = batch % applications.Length;
                int length = Math.Min(batchSize, offered - start);
                var events = Enumerable.Range(0, length).Select(i =>
                {
                    bool failed = index == 0 ? i % 3 == 0 : (start + i) % 7 == 0;
                    var activity = ActivityTraceId.CreateRandom();
                    return new
                    {
                        id = Guid.NewGuid(), occurredAt = DateTimeOffset.UtcNow, version = "1.0.0-load",
                        sessionId = sessions[index][(batch / applications.Length * batchSize + i) % sessions[index].Length],
                        kind = "api", name = endpointName, outcome = failed ? "failure" : "success",
                        durationMs = Random.Shared.Next(25, 1800), statusCode = failed ? 503 : 200,
                        traceId = activity.ToHexString(), spanId = ActivitySpanId.CreateRandom().ToHexString()
                    };
                });
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/telemetry/v1/batch")
                {
                    Content = JsonContent.Create(new { schemaVersion = 1, application = applications[index], environment = "load", events })
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenPrefix + index);
                var latency = Stopwatch.StartNew();
                try
                {
                    using var response = await http.SendAsync(request, cancellation);
                    int accepted = 0;
                    if (response.StatusCode == HttpStatusCode.Accepted)
                    {
                        using var result = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellation));
                        accepted = result.RootElement.GetProperty("accepted").GetInt32();
                    }
                    requests.Add(new((int)response.StatusCode, latency.Elapsed.TotalMilliseconds, accepted));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    requests.Add(new(0, latency.Elapsed.TotalMilliseconds, 0));
                }
            });

        // Count the full configured interval, including the unscheduled tail of one batch.
        TimeSpan tail = TimeSpan.FromSeconds(seconds) - phase.Elapsed;
        if (tail > TimeSpan.Zero) await Task.Delay(tail);
        int accepted = requests.Sum(request => request.Accepted);
        return new(seconds, eventsPerSecond, offered, accepted, Math.Round(accepted / phase.Elapsed.TotalSeconds, 2), Describe(requests));
    }

    async Task Poll(CancellationToken cancellation)
    {
        int i = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var timer = Stopwatch.StartNew();
                int status;
                try
                {
                    using var response = await http.GetAsync(
                        $"/api/v1/overview?application={applications[i++ % applications.Length]}&environment=load&hours=1", cancellation);
                    status = (int)response.StatusCode;
                }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellation.IsCancellationRequested)
                {
                    status = 0;
                }
                queries.Add(new(status, timer.Elapsed.TotalMilliseconds, 0));
                var platform = await Platform(cancellation);
                peakPending = Math.Max(peakPending, platform.Pending);
                process.Refresh();
                peakRss = Math.Max(peakRss, process.WorkingSet64);
                await Task.Delay(500, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    async Task WaitForDrain()
    {
        var wait = Stopwatch.StartNew();
        while ((await Platform()).Pending > 0)
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("The local telemetry queue did not drain in 30 seconds.");
            await Task.Delay(100);
        }
    }

    async Task<(long Pending, long Processed, long Bytes)> Platform(CancellationToken cancellation = default)
    {
        using var result = await http.GetFromJsonAsync<JsonDocument>("/api/v1/platform", cancellation)
            ?? throw new InvalidOperationException("The platform health endpoint returned no data.");
        return (result.RootElement.GetProperty("pending").GetInt64(), result.RootElement.GetProperty("processed").GetInt64(),
            result.RootElement.GetProperty("databaseBytes").GetInt64());
    }
}
finally
{
    if (!process.HasExited)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
    parent.Delete(recursive: true);
}

int Number(int index, int fallback, int maximum) => args.Length <= index ? fallback :
    int.TryParse(args[index], out int number) && number > 0 && number <= maximum
        ? number : throw new ArgumentException($"Argument {index + 1} must be between 1 and {maximum}.");

async Task<bool> Ready()
{
    try
    {
        using var response = await http.GetAsync("/health/ready");
        return response.StatusCode == HttpStatusCode.OK;
    }
    catch (HttpRequestException) { return false; }
}

void RecordLog(object sender, DataReceivedEventArgs entry)
{
    if (entry.Data is null) return;
    logs.Enqueue(entry.Data);
    if (logs.Count > 50) logs.TryDequeue(out _);
}

static Latency Describe(IEnumerable<Sample> samples)
{
    var observed = samples.OrderBy(sample => sample.Milliseconds).ToArray();
    double Percentile(double fraction) => observed.Length == 0 ? 0 :
        Math.Round(observed[(int)Math.Ceiling(observed.Length * fraction) - 1].Milliseconds, 2);
    return new(observed.Length, Percentile(.5), Percentile(.95), Percentile(.99),
        observed.GroupBy(sample => sample.Status).ToDictionary(group => group.Key, group => group.Count()));
}

record Sample(int Status, double Milliseconds, int Accepted);
record Latency(int Requests, double P50Ms, double P95Ms, double P99Ms, Dictionary<int, int> StatusCounts);
record PhaseResult(int Seconds, int TargetEventsPerSecond, int Offered, int Accepted, double AcceptedPerSecond, Latency Http);
