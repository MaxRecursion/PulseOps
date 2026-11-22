using System.Globalization;

namespace PulseOps.Portal.Services;

public sealed record ViewScope(string? Application, string? Environment, int Hours, string? Version)
{
    public string Url(string path, params (string Key, string? Value)[] more)
    {
        (string Key, string? Value)[] filters =
        [
            ("application", Application), ("environment", Environment),
            ("hours", Hours.ToString(CultureInfo.InvariantCulture)), ("version", Version)
        ];
        var query = filters.Concat(more)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value!));
        return path + "?" + string.Join("&", query);
    }

    public ViewScope For(string application, string environment) => this with { Application = application, Environment = environment };
}
