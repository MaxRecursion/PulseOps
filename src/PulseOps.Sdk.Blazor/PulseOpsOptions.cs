using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace PulseOps.Sdk.Blazor;

public sealed class PulseOpsOptions
{
    public string Application { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string IngestionEndpoint { get; set; } = "/pulseops/telemetry";
    public Func<CancellationToken, ValueTask<string?>>? GetRelayAntiforgeryTokenAsync { get; set; }
    public bool Enabled { get; set; } = true;
    internal bool ConfigurationValid { get; set; } = true;
    public bool EnableApiMonitoring { get; set; } = true;
    public bool EnableErrorTracking { get; set; } = true;
    public bool EnableNavigationTracking { get; set; } = true;
    public bool EnablePerformanceMonitoring { get; set; } = true;
    public bool EnableSessionDiagnostics { get; set; } = true;
    public bool EnableStartupMonitoring { get; set; } = true;
    public double SamplingRate { get; set; } = .1;
    public int BatchSize { get; set; } = 40;
    public int QueueCapacity { get; set; } = 400;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);
    public Dictionary<string, string> ApiEndpoints { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> NavigationRoutes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> AllowedOperations { get; } = new(StringComparer.Ordinal);
    public HashSet<string> LoggerCategories { get; } = new(StringComparer.Ordinal);
    public HashSet<string> TracePropagationOrigins { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static partial class PulseOpsServiceCollectionExtensions
{
    public static IServiceCollection AddPulseOps(this IServiceCollection services, Action<PulseOpsOptions> configure)
    {
        var options = new PulseOpsOptions();
        configure(options);
        if (!IsScope(options.Application) ||
            !IsScope(options.Environment) ||
            !IsToken(options.Version, 32) ||
            !double.IsFinite(options.SamplingRate) || options.SamplingRate is < 0 or > 1 ||
            options.BatchSize is < 1 or > 50 || options.QueueCapacity < options.BatchSize || options.QueueCapacity > 2_000 ||
            options.FlushInterval < TimeSpan.FromSeconds(1) || options.FlushInterval > TimeSpan.FromMinutes(1) ||
            options.AllowedOperations.Concat(options.ApiEndpoints.Values).Concat(options.NavigationRoutes.Values)
                .Any(name => !IsToken(name, 64)))
        {
            options.ConfigurationValid = false;
        }

        services.AddSingleton(options);
        services.AddSingleton<PulseOpsClient>();
        services.AddTransient<PulseOpsHttpHandler>();
        if (options.EnableErrorTracking && options.LoggerCategories.Count > 0)
        {
            services.AddSingleton<ILoggerProvider, PulseOpsLoggerProvider>();
        }

        return services;
    }

    private static bool IsToken(string value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength && TokenPattern().IsMatch(value);

    private static bool IsScope(string value) => value is { Length: <= 40 } && ScopePattern().IsMatch(value);

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ScopePattern();

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
