using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PulseOps.Portal.Services;

// Monitors, history and change-control calls. Error bodies {"error","detail"} become operator-facing messages.
public sealed partial class PortalApi
{
    public async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> json, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("api/v1/" + path, cancellationToken);
        await EnsureAcceptedAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync(json, cancellationToken)
            ?? throw new PortalRequestException("PulseOps returned an empty response.", response.StatusCode);
    }

    /// <summary>Submits a proposal and returns the new change id.</summary>
    public async Task<string> ProposeChangeAsync(ChangeProposal proposal, string csrfToken, CancellationToken cancellationToken)
    {
        using var response = await PostAsync("admin/changes", JsonContent.Create(proposal, AdminJson.Default.ChangeProposal),
            csrfToken, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return text.Length > 0 ? JsonSerializer.Deserialize(text, AdminJson.Default.ChangeCreated)?.Id ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public async Task DecideChangeAsync(string id, bool approve, string? note, string csrfToken, CancellationToken cancellationToken)
    {
        var decision = new ChangeDecision(string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        using var response = await PostAsync($"admin/changes/{Uri.EscapeDataString(id)}/{(approve ? "approve" : "reject")}",
            JsonContent.Create(decision, AdminJson.Default.ChangeDecision), csrfToken, cancellationToken);
    }

    public async Task WithdrawChangeAsync(string id, string csrfToken, CancellationToken cancellationToken)
    {
        using var response = await PostAsync($"admin/changes/{Uri.EscapeDataString(id)}/withdraw",
            JsonContent.Create(new ChangeDecision(null), AdminJson.Default.ChangeDecision), csrfToken, cancellationToken);
    }

    /// <summary>Builds a relative API path with escaped, non-empty query values.</summary>
    public static string Query(string path, params (string Key, string? Value)[] parameters)
    {
        var query = string.Join("&", parameters.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value!)));
        return query.Length == 0 ? path : path + "?" + query;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, HttpContent content, string csrfToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/" + path) { Content = content };
        request.Headers.Add("X-PulseOps-CSRF", csrfToken);
        var response = await http.SendAsync(request, cancellationToken);
        try
        {
            await EnsureAcceptedAsync(response, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task EnsureAcceptedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        ApiError? error = null;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (text.Length is > 0 and <= 4096)
        {
            try { error = JsonSerializer.Deserialize(text, AdminJson.Default.ApiError); }
            catch (JsonException) { }
        }
        throw new PortalRequestException(Describe(response.StatusCode, error?.Error, error?.Detail), response.StatusCode);
    }

    internal static string Describe(HttpStatusCode status, string? error, string? detail) => (status, error) switch
    {
        (HttpStatusCode.Forbidden, "self_approval") => "A second administrator must approve this change.",
        (HttpStatusCode.Conflict, "stale") => "The monitor changed after this proposal. Propose the change again.",
        (HttpStatusCode.Conflict, "configuration_managed") => "This monitor is managed in configuration and cannot be changed here.",
        (HttpStatusCode.Conflict, "managed_monitor_limit") => "The managed monitor limit has been reached. Remove an unused managed monitor before adding another.",
        (HttpStatusCode.Conflict, "too_many_pending") =>
            "Too many changes are awaiting review. Ask an administrator to decide pending changes, or withdraw one of yours, then try again.",
        (HttpStatusCode.Conflict, "conflict") => "Another pending change already targets this item. Withdraw or decide that change first.",
        (HttpStatusCode.Conflict, "not_pending") => "This change is no longer pending. It was decided, withdrawn or expired. Refresh to see its status.",
        (HttpStatusCode.Conflict, "invalid_now") =>
            "This change can no longer be applied as proposed, for example because its maintenance window has already ended. Reject it and propose a new change.",
        (HttpStatusCode.Conflict, "not_found") => "The monitor or maintenance window no longer exists. Refresh and try again.",
        (HttpStatusCode.Conflict, _) => "The change conflicts with the current configuration. Refresh and try again.",
        (HttpStatusCode.BadRequest, "invalid_change") => Clip(detail) is { Length: > 0 } reason ? reason : "PulseOps rejected the proposed values.",
        (HttpStatusCode.BadRequest, "range_exceeds_hourly_retention") =>
            "Hourly rollups are not kept for the selected range. Choose a shorter range or daily resolution.",
        (HttpStatusCode.BadRequest, "range_too_large") =>
            "This selection holds too many rollup rows. Choose a shorter range, one event type or an alias.",
        (HttpStatusCode.BadRequest, "invalid_history_query") =>
            "PulseOps could not apply these history filters. Check the event type, alias, version and range.",
        (HttpStatusCode.BadRequest, _) => "PulseOps could not accept the request. Reload the page and try again.",
        (HttpStatusCode.Unauthorized, _) => "Your session has expired. Sign in to continue.",
        (HttpStatusCode.Forbidden, _) => "Your role does not permit this action in the selected scope.",
        (HttpStatusCode.NotFound, _) => "This record is unavailable in your current scope. It may have been removed.",
        (HttpStatusCode.TooManyRequests, _) => "PulseOps is receiving too many requests. Try again in a moment.",
        _ => "PulseOps could not complete the request. Try again in a moment."
    };

    /// <summary>Server validation detail is short; it is still bounded and stripped of control characters.</summary>
    private static string Clip(string? detail)
    {
        var text = new string((detail ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= 300 ? text : text[..299] + "…";
    }
}
