using System.Buffers.Binary;

namespace PulseOps.Server;

internal static class DemoData
{
    private const string DemoToken = "pulseops-demo-local-only-91fd-4ad1";
    private static readonly string[] Labels =
        ["Finance Reporting", "Payments Gateway", "Treasury Console"];

    public static AppScope[] Scopes(string browserOrigin) =>
    [
        Demo("finance-reports", "production", Labels[0], ["sap-reporting", "load-report", "report-render"]),
        Demo("payments-gateway", "production", Labels[1], ["payments-status", "load-payments", "payments-view"]),
        Demo("treasury-console", "staging", Labels[2], ["cash-positions", "load-positions", "positions-view"]),
        new AppScope
        {
            Application = "finance-demo", Environment = "demo", Label = "Finance Demo Application", Demo = true,
            IngestToken = DemoToken, StartupOrigins = [browserOrigin],
            AllowedNames = ["report-api", "generate-report", "download-report", "parse-report", "render-grid", "sign-in",
                "managed-exception", "render-exception", "unobserved-task", "javascript-error", "promise-rejection",
                "finance-workspace", "lcp", "inp", "cls", "fcp", "ttfb", "navigation-duration", "long-task-count",
                "opened", "online", "offline", "visible", "hidden", "ended"]
        }
    ];

    /// <summary>Labelled Development-only monitors that exercise latency, no-data and release regression evaluation.</summary>
    public static MonitorPolicy[] Monitors() =>
    [
        new()
        {
            Name = "demo-sap-reporting-p95", Application = "finance-reports", Environment = "production", Type = "latency",
            Kind = "api", Target = "sap-reporting", Percentile = 95, ThresholdMs = 900, WindowMinutes = 60,
            EvaluationDelayMinutes = 0, MinimumSamples = 20, Severity = "medium"
        },
        new()
        {
            Name = "demo-payments-heartbeat", Application = "payments-gateway", Environment = "production", Type = "no-data",
            Kind = "api", Target = "payments-status", WindowMinutes = 15, NoDataMinutes = 30, EvaluationDelayMinutes = 0,
            MinimumSamples = 1, Severity = "high"
        },
        new()
        {
            Name = "demo-reporting-release", Application = "finance-reports", Environment = "production", Type = "regression",
            Kind = "api", Target = "sap-reporting", Percentile = 95, ComparisonHours = 24, WindowMinutes = 30,
            EvaluationDelayMinutes = 0, MinimumSamples = 30, FailureIncreasePercent = 5, Severity = "high"
        }
    ];

    private static AppScope Demo(string application, string environment, string label, string[] names) =>
        new() { Application = application, Environment = environment, Label = label, Demo = true, AllowedNames = names };

    public static void Seed(Storage storage, PlatformOptions options, DateTimeOffset now)
    {
        var scopes = Scopes(options.DemoBrowserOrigin);
        for (var scopeIndex = 0; scopeIndex < Labels.Length; scopeIndex++)
        {
            var scope = options.Scopes.Single(s => s.Application == scopes[scopeIndex].Application);
            using (var db = storage.Open())
            using (var check = db.Command(null, "SELECT 1 FROM releases WHERE application=$app AND environment=$env AND build_id='demo-143'",
                       ("$app", scope.Application), ("$env", scope.Environment)))
            {
                if (check.ExecuteScalar() is not null)
                    continue;
            }

            var random = new Random(91 + scopeIndex);
            for (int bucket = 0; bucket < 48; bucket++)
            {
                var reports = new List<TelemetryEvent>();
                var at = now.AddMinutes(-(48 - bucket) * 30 + 3);
                var volume = 8 + (int)(12 * Math.Abs(Math.Sin(bucket / 7.3))) + random.Next(2, 9);
                for (int index = 0; index < volume; index++)
                {
                    int seed = (scopeIndex + 1) * 100_000 + bucket * 1000 + index * 4;
                    var session = Key(seed - index % 4 * 4, 2026);
                    var trace = Key(seed, 42).ToString("N");
                    var critical = scopeIndex == 0 && bucket >= 41 && index < volume / 3;
                    var intermittent = scopeIndex == 1 && bucket >= 44 && index < volume / 3;
                    var failed = critical || intermittent || random.NextDouble() < (scopeIndex == 2 ? 0.012 : 0.023);
                    var version = bucket > 34 ? "2.8.3" : "2.8.2";
                    double elapsed = failed ? random.Next(1050, 2100) : (scopeIndex + 1) * 70 + random.Next(90, 420);
                    var time = at.AddSeconds(index * 5);
                    reports.Add(new(Key(seed, 2025), time, version, session, "api", scope.AllowedNames[0],
                        failed ? "failure" : "success", elapsed, failed ? 503 : 200, trace, "1000000000000001",
                        Category: failed ? "http" : null));
                    if (failed)
                        reports.Add(new(Key(seed + 1, 2025), time.AddMilliseconds(10), version, session,
                            "error", scope.AllowedNames[2], "failure", TraceId: trace,
                            SpanId: "1000000000000002", ParentSpanId: "1000000000000001", Category: "exception"));
                    if (index % 4 == 0)
                        reports.Add(new(Key(seed + 2, 2025), time.AddMilliseconds(-10), version, session, "operation",
                            scope.AllowedNames[1], failed ? "failure" : "success", elapsed + 52,
                            TraceId: trace, SpanId: "1000000000000003"));
                    if (scopeIndex == 2 && bucket >= 44 && index < 4)
                        reports.Add(new(Key(seed + 3, 2025), time, version, session, "startup", "AssetLoadFailure",
                            "failure", Category: "resource"));
                }
                if (storage.Ingest(scope, reports, false, now) is null)
                    throw new InvalidOperationException("Demo data requires capacity for the seed inbox.");
            }
            storage.AddRelease(scope, new(scope.Application, scope.Environment, "2.8.2", "demo-142", new string('a', 40)), now.AddHours(-22));
            storage.AddRelease(scope, new(scope.Application, scope.Environment, "2.8.3", "demo-143", new string('b', 40)), now.AddHours(-7));
        }
    }

    private static Guid Key(int value, int salt)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], value);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], salt);
        return new Guid(bytes);
    }
}
