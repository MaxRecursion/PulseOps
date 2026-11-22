using System.Globalization;

namespace PulseOps.Portal.Services;

internal static class Presentation
{
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    public static string Percent(long amount, long total) => total == 0 ? "—" : (amount / (double)total).ToString("P1", CultureInfo.InvariantCulture);
    public static string Duration(double? milliseconds) => milliseconds is not { } ms ? "—"
        : ms >= 1000 ? (ms / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " s"
        : ms.ToString(ms is > 0 and < 1 ? "0.0" : "0", CultureInfo.InvariantCulture) + " ms";
    public static string Date(DateTimeOffset value) => value.UtcDateTime.ToString("dd MMM yyyy, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    public static string ShortDate(DateTimeOffset value) => value.UtcDateTime.ToString("dd MMM, HH:mm", CultureInfo.InvariantCulture);
    public static string Title(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-', ' ').Replace('_', ' '));
    public static string Id(string id) => id.Length <= 12 ? id : id[..8] + "…" + id[^4..];
    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    public static string Tone(string? status) => status?.ToLowerInvariant() switch
    {
        "critical" or "failed" or "error" or "failure" or "fatal" => "danger",
        "high" or "warning" or "degraded" or "reopened" or "investigating" => "warning",
        "healthy" or "resolved" or "mitigated" or "success" or "succeeded" => "good",
        "open" or "acknowledged" or "detected" or "info" => "accent",
        _ => "neutral"
    };
}
