using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace PulseOps.Portal.Services;

public sealed partial class PortalApi(HttpClient http)
{
    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/" + path, cancellationToken);
        EnsureSuccess(response);
        var json = (JsonTypeInfo<T>)PortalJson.Default.GetTypeInfo(typeof(T))!;
        return await response.Content.ReadFromJsonAsync(json, cancellationToken)
            ?? throw new PortalRequestException("PulseOps returned an empty response.", response.StatusCode);
    }

    public async Task UpdateIncidentAsync(string id, IncidentUpdate update, string csrfToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/incidents/" + Uri.EscapeDataString(id));
        request.Headers.Add("X-PulseOps-CSRF", csrfToken);
        request.Content = JsonContent.Create(update, PortalJson.Default.IncidentUpdate);
        using var response = await http.SendAsync(request, cancellationToken);
        EnsureSuccess(response);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Sign in to continue.",
            HttpStatusCode.Forbidden => "Your account does not have access to this resource.",
            HttpStatusCode.NotFound => "This record is unavailable in your current scope.",
            HttpStatusCode.Conflict => "This incident changed while you were editing it. Reload the incident and try again.",
            HttpStatusCode.BadRequest => "PulseOps could not apply the submitted values. Check the selected scope or action.",
            HttpStatusCode.TooManyRequests => "PulseOps is receiving too many requests. Try again in a moment.",
            _ => "PulseOps could not complete the request. Try again in a moment."
        };
        throw new PortalRequestException(message, response.StatusCode);
    }
}

public sealed class PortalRequestException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
