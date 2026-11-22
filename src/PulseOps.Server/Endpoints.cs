using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;

namespace PulseOps.Server;

internal static class Endpoints
{
    public static IResult Receive(TelemetryBatch batch, HttpContext context, PlatformOptions settings, Storage db,
        TimeProvider clock, bool anonymous)
    {
        var maximum = anonymous ? 12 : 100;
        if (batch.SchemaVersion != 1 || batch.Events is null || batch.Events.Length == 0 || batch.Events.Length > maximum ||
            !Validation.IsSlug(batch.Application) || !Validation.IsSlug(batch.Environment))
            return Results.BadRequest(new { error = "invalid_batch" });
        var scope = settings.Scopes.SingleOrDefault(s => s.Application == batch.Application && s.Environment == batch.Environment);
        if (anonymous)
        {
            if (scope is null || !scope.StartupOrigins.Contains(context.Request.Headers.Origin.ToString()))
                return Results.StatusCode(403);
        }
        else if (!Auth(context, scope))
            return Results.Unauthorized();
        // Operator kill switch. The SDK and bootstrap stop sending for the session when they receive 410.
        if (scope!.IntakeSuspended)
            return Results.StatusCode(StatusCodes.Status410Gone);

        var now = clock.GetUtcNow();
        if (batch.Events.Any(item => !Validation.IsEvent(item, scope!, now, anonymous)))
            return Results.BadRequest(new { error = "invalid_event" });
        try
        {
            int? accepted = db.Ingest(scope!, batch.Events.Select(e => e!), anonymous, now);
            if (accepted is not null)
                return Results.Accepted(null as string, new { accepted, duplicates = batch.Events.Length - accepted });
            return Retry(context, 429);
        }
        catch (SqliteException)
        {
            return Retry(context, 503);
        }
        catch (IOException)
        {
            return Retry(context, 503);
        }
    }

    public static IResult Release(ReleaseMarker marker, HttpContext context, PlatformOptions settings, Storage db, TimeProvider clock)
    {
        var scope = settings.Scopes.SingleOrDefault(s => s.Application == marker.Application && s.Environment == marker.Environment);
        if (!Auth(context, scope))
            return Results.Unauthorized();
        if (!Validation.IsVersion(marker.Version) || !Validation.IsAlias(marker.BuildId) ||
            !(Validation.IsHex(marker.CommitId, 40) || Validation.IsHex(marker.CommitId, 64)))
            return Results.BadRequest();
        try
        {
            db.AddRelease(scope!, marker, clock.GetUtcNow());
            return Results.Accepted();
        }
        catch (SqliteException) { return Retry(context, 503); }
    }

    private static bool Auth(HttpContext context, AppScope? scope) =>
        scope is not null && AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var header) &&
        header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && header.Parameter is { } token && scope.VerifyToken(token);

    private static IResult Retry(HttpContext context, int status)
    {
        context.Response.Headers.RetryAfter = "5";
        return Results.StatusCode(status);
    }
}

internal sealed record CommandLine(string Mode, string? Config, string? Path, string[] Remaining)
{
    public static CommandLine Parse(string[] args)
    {
        string? mode = null;
        string? config = null;
        string? path = null;
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    if (config is not null || i + 1 >= args.Length || !System.IO.Path.IsPathFullyQualified(args[i + 1]))
                        throw new ArgumentException("--config requires one absolute JSON file path.");
                    config = args[++i];
                    break;
                case "--worker" or "--migrate" or "--backup" or "--verify-backup" or "--restore" or "--export-audit"
                    or "--replay-dead-letters":
                    if (mode is not null)
                        throw new ArgumentException("Select only one PulseOps execution mode.");
                    mode = args[i][2..];
                    if (mode is not ("worker" or "migrate" or "replay-dead-letters"))
                    {
                        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                            throw new ArgumentException("The command requires a snapshot path.");
                        path = args[++i];
                    }
                    break;
                default:
                    remaining.Add(args[i]);
                    break;
            }
        }
        return new(mode ?? "web", config, path, [.. remaining]);
    }
}
