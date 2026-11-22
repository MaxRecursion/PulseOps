namespace PulseOps.Portal.Services;

public sealed record ChartSeries(string Label, string Color, IReadOnlyList<double?> Values, bool Columns = false, bool Dashed = false);
