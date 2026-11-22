using Microsoft.Extensions.Logging;

namespace PulseOps.Sdk.Blazor;

[ProviderAlias("PulseOps")]
internal sealed class PulseOpsLoggerProvider(PulseOpsClient client, PulseOpsOptions options) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new PulseOpsLogger(client, options.LoggerCategories.Contains(categoryName));

    public void Dispose()
    {
    }

    private sealed class PulseOpsLogger(PulseOpsClient client, bool allowed) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => client.IsEnabled && allowed && logLevel is LogLevel.Error or LogLevel.Critical;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && exception is not null)
                client.ReportException(exception);
        }
    }
}
