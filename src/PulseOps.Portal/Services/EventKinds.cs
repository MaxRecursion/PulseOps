namespace PulseOps.Portal.Services;

internal static class EventKinds
{
    public static readonly (string Value, string Label)[] All =
    [
        ("api", "API request"), ("error", "Exception"), ("operation", "Business operation"),
        ("startup", "Startup"), ("navigation", "Navigation"), ("metric", "Browser metric"),
        ("session", "Session"), ("synthetic", "Synthetic check"), ("log", "Log")
    ];

    public static string LabelFor(string kind) => All.FirstOrDefault(item => item.Value == kind).Label ?? kind;
}
