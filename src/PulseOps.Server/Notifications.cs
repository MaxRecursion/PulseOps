using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net.Mime;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PulseOps.Server;

internal sealed record AlertDelivery(string Id, string LeaseId, string IncidentId, string Application,
    string Environment, string Destination, string Severity, string Status, int Attempt)
{
    /// <summary>When the incident transition was enqueued (alert_deliveries.created).</summary>
    public DateTimeOffset Created { get; init; }
}

/// <summary>
/// A plain-text incident alert. It identifies the incident, scope and transition and links to the portal.
/// Evidence, telemetry values and credentials are never part of a message.
/// </summary>
internal sealed record AlertEmail(string DeliveryId, string From, IReadOnlyList<string> Recipients, string Subject, string Body)
{
    // The generated record ToString would print recipients and the body into any log or assertion message.
    public override string ToString() => "AlertEmail " + DeliveryId;
}

internal interface IAlertEmailSender
{
    Task SendAsync(AlertEmail message, CancellationToken token);
}

internal static class NotificationRouting
{
    internal static IEnumerable<string> MatchingDestinations(PlatformOptions options, string application, string environment, string severity)
    {
        return options.Webhooks
            .Where(w => Matches(w.Application, w.Environment, w.Severities))
            .Select(w => w.Name)
            .Concat(options.EmailRoutes
                .Where(r => Matches(r.Application, r.Environment, r.Severities))
                .Select(r => r.Name));

        bool Matches(string routeApplication, string routeEnvironment, string[] severities) =>
            (routeApplication.Length == 0 || routeApplication == application) &&
            (routeEnvironment.Length == 0 || routeEnvironment == environment) && severities.Contains(severity);
    }
}

public sealed partial class Storage
{
    // Destination names are unique across webhooks and e-mail routes (validated), so alert_deliveries.destination
    // identifies the adapter. An active maintenance window for the scope suppresses delivery and records why.
    private void EnqueueAlerts(SqliteConnection connection, SqliteTransaction transaction, string incident,
        string application, string environment, string severity, string status, long timestamp)
    {
        var destinations = NotificationRouting.MatchingDestinations(options, application, environment, severity).ToArray();
        if (destinations.Length == 0)
            return;
        if (ActiveMaintenance(connection, transaction, application, environment, timestamp) is { } window)
        {
            using var assignee = connection.Command(transaction, "SELECT assignee FROM incidents WHERE id=$id", ("$id", incident));
            WriteHistory(connection, transaction, incident, timestamp, status, "PulseOps",
                $"Notifications suppressed by maintenance window {window.Id}.", assignee.ExecuteScalar() as string ?? "");
            return;
        }
        foreach (var destination in destinations)
        {
            using var enqueue = connection.Command(transaction, """
                INSERT INTO alert_deliveries(id,incident_id,application,environment,destination,severity,status,created,next_attempt)
                VALUES($id,$incident,$app,$env,$destination,$severity,$status,$at,$at)
                """, ("$id", Guid.NewGuid().ToString("D")), ("$incident", incident), ("$app", application),
                ("$env", environment), ("$destination", destination), ("$severity", severity),
                ("$status", status), ("$at", timestamp));
            enqueue.ExecuteNonQuery();
        }
    }

    internal AlertDelivery? ClaimAlert(DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string lease = Guid.NewGuid().ToString("D");
        using var command = connection.Command(transaction, """
            UPDATE alert_deliveries SET lease_until = $lease, lease_id = $leaseId, attempts = attempts + 1
            WHERE id = (
                SELECT id FROM alert_deliveries WHERE delivery_state = 'pending' AND next_attempt <= $now
                    AND (lease_until IS NULL OR lease_until <= $now)
                ORDER BY next_attempt, id LIMIT 1
            )
            RETURNING id,incident_id,application,environment,destination,severity,status,attempts,created
            """, ("$lease", now.AddMinutes(2).ToUnixTimeMilliseconds()), ("$leaseId", lease), ("$now", now.ToUnixTimeMilliseconds()));
        var claimed = Read(command, row => new AlertDelivery(row.GetString(0), lease, row.GetString(1), row.GetString(2),
            row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(6), row.GetInt32(7)) { Created = row.Time(8) })
            .SingleOrDefault();
        transaction.Commit();
        return claimed;
    }

    internal void CompleteAlert(AlertDelivery delivery, DateTimeOffset now, bool sent, bool retry)
    {
        using var connection = Open();
        using var command = connection.Command(null, """
            UPDATE alert_deliveries SET delivery_state=$state,next_attempt=$at,lease_until=NULL,lease_id=NULL
            WHERE id=$id AND lease_id=$leaseId
            """, ("$state", sent ? "delivered" : retry ? "pending" : "failed"),
            ("$at", now.ToUnixTimeMilliseconds()), ("$id", delivery.Id), ("$leaseId", delivery.LeaseId));
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Delivers the durable alert outbox one claimed row at a time. The destination name selects the adapter: an approved
/// HTTPS webhook or an SMTP e-mail route. Delivery is at least once; receivers deduplicate on the delivery id.
/// </summary>
internal sealed class NotificationWorker(PlatformOptions options, Storage storage, IHttpClientFactory clients,
    TimeProvider clock, ILogger<NotificationWorker> logger, IAlertEmailSender? email = null) : BackgroundService
{
    internal const int MaximumAttempts = 6;

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var client = clients.CreateClient("outbound");
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (await DeliverOnceAsync(client, token))
                    continue;
            }
            catch (SqliteException ex)
            {
                logger.LogWarning("Notification queue will retry. SQLite code {Code}.", ex.SqliteErrorCode);
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    /// <summary>Claims one due delivery and dispatches it by destination name. Returns false when nothing is due.</summary>
    internal async Task<bool> DeliverOnceAsync(HttpClient client, CancellationToken token)
    {
        if (storage.ClaimAlert(clock.GetUtcNow()) is not { } delivery)
            return false;
        // A pending row can survive a restart that removes the destination or restricts its scope.
        if (!NotificationRouting.MatchingDestinations(options, delivery.Application, delivery.Environment, delivery.Severity)
            .Contains(delivery.Destination))
        {
            logger.LogWarning("Notification {Delivery} failed. Its destination no longer accepts this alert.", delivery.Id);
            storage.CompleteAlert(delivery, clock.GetUtcNow(), false, false);
        }
        else if (options.Webhooks.SingleOrDefault(w => w.Name == delivery.Destination) is { } webhook)
            await SendWebhook(delivery, webhook, client, token);
        else
            await SendEmail(delivery, options.EmailRoutes.Single(r => r.Name == delivery.Destination), token);
        return true;
    }

    private async Task SendWebhook(AlertDelivery delivery, WebhookSettings destination, HttpClient client, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, destination.Url)
        {
            Content = JsonContent.Create(new
            {
                deliveryId = delivery.Id, incidentId = delivery.IncidentId, delivery.Application, delivery.Environment,
                delivery.Severity, delivery.Status, incidentUrl = IncidentUrl(options, delivery)
            })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", delivery.Id);
        if (destination.BearerToken.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destination.BearerToken);

        bool sent = false;
        bool retry = true;
        TimeSpan guidance = TimeSpan.Zero;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            sent = response.IsSuccessStatusCode;
            retry = (int)response.StatusCode is 408 or 429 or >= 500;
            guidance = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.Zero;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        Complete(delivery, sent, retry, guidance);
    }

    private async Task SendEmail(AlertDelivery delivery, EmailRoute route, CancellationToken token)
    {
        bool sent = false;
        bool retry = false;
        try
        {
            await (email ?? new SmtpAlertEmailSender(options.Smtp)).SendAsync(ComposeEmail(delivery, route, options), token);
            sent = true;
        }
        // One delivery must never stop the worker. Shutdown leaves the row leased so it is claimed again after restart.
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            retry = SmtpAlertEmailSender.IsTransient(exception);
            // Only the delivery id and the reply code are logged: SMTP replies and exception messages can quote recipients.
            logger.LogWarning("E-mail delivery {Delivery} {Outcome}. SMTP status {Status}.", delivery.Id,
                retry && delivery.Attempt < MaximumAttempts ? "will be retried" : "failed", SmtpAlertEmailSender.ReplyCode(exception));
        }
        Complete(delivery, sent, retry, TimeSpan.Zero);
    }

    // Exponential backoff with jitter (about 10, 20, 40, 80 and 160 seconds), raised to a receiver's Retry-After and capped
    // at one hour. After MaximumAttempts the delivery stays failed and is counted in platform health.
    private void Complete(AlertDelivery delivery, bool sent, bool retry, TimeSpan guidance)
    {
        retry &= !sent && delivery.Attempt < MaximumAttempts;
        var delay = Math.Max(Math.Pow(2, Math.Min(8, delivery.Attempt)) * 5 + Random.Shared.Next(1, 6), guidance.TotalSeconds);
        storage.CompleteAlert(delivery, clock.GetUtcNow().AddSeconds(Math.Clamp(delay, 1, 3600)), sent, retry);
    }

    private static string IncidentUrl(PlatformOptions options, AlertDelivery delivery) =>
        options.PortalOrigin + "/incidents/" + delivery.IncidentId;

    internal static AlertEmail ComposeEmail(AlertDelivery delivery, EmailRoute route, PlatformOptions options)
    {
        string scope = delivery.Application + "/" + delivery.Environment;
        string body = string.Join("\r\n",
            "PulseOps incident notification",
            "",
            "Incident: " + delivery.IncidentId,
            "Scope:    " + scope,
            "Severity: " + delivery.Severity,
            "Status:   " + delivery.Status,
            "Changed:  " + delivery.Created.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture),
            "Delivery: " + delivery.Id,
            "",
            "Open the incident in the PulseOps portal:",
            IncidentUrl(options, delivery),
            "",
            "Evidence and telemetry are available only in the portal to authorized operators.",
            "");
        return new AlertEmail(delivery.Id, options.Smtp.From, [.. route.Recipients],
            $"[PulseOps] {delivery.Severity.ToUpperInvariant()} {delivery.Status}: {scope}", body);
    }
}

/// <summary>
/// Sends alert e-mail through the configured relay with System.Net.Mail. UseTls issues STARTTLS and fails rather than
/// continuing in cleartext when the relay does not offer it. Credentials are sent only when a user name is configured;
/// the service account's own Windows credentials are never offered.
/// </summary>
internal sealed class SmtpAlertEmailSender(SmtpSettings settings) : IAlertEmailSender
{
    public async Task SendAsync(AlertEmail message, CancellationToken token)
    {
        var from = new MailAddress(message.From);
        using var mail = new MailMessage
        {
            From = from, Subject = message.Subject, Body = message.Body, IsBodyHtml = false, BodyEncoding = Encoding.UTF8,
            BodyTransferEncoding = Ascii.IsValid(message.Body) ? TransferEncoding.SevenBit : TransferEncoding.QuotedPrintable
        };
        foreach (var recipient in message.Recipients)
            mail.To.Add(new MailAddress(recipient));
        mail.Headers.Add("X-PulseOps-Delivery", message.DeliveryId);
        mail.Headers.Add("Auto-Submitted", "auto-generated");
        mail.Headers.Add("X-Auto-Response-Suppress", "All");
        // A retried delivery keeps its Message-ID, so receiving systems can recognize a duplicate.
        if (Uri.CheckHostName(from.Host) == UriHostNameType.Dns && Ascii.IsValid(from.Host))
            mail.Headers.Add("Message-ID", $"<{message.DeliveryId}@{from.Host}>");

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network, EnableSsl = settings.UseTls, UseDefaultCredentials = false,
            Timeout = settings.TimeoutSeconds * 1000
        };
        if (settings.UserName.Length > 0)
            client.Credentials = new NetworkCredential(settings.UserName, settings.Password);
        // On .NET 10 SmtpClient.Timeout also ends a stalled asynchronous send (SmtpException, GeneralFailure). The linked
        // token enforces the same limit independently of SmtpClient internals and carries host shutdown.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        await client.SendMailAsync(mail, timeout.Token);
    }

    /// <summary>
    /// Transient: every 4yz reply (421, 450, 451, 452, 454), failures without a reply (refused or dropped connection,
    /// TLS negotiation, timeout) and recipient rejections where at least one recipient was deferred with 4yz.
    /// Permanent: 5yz replies (550, 553, 554 and the 50x syntax replies) and any other exception.
    /// </summary>
    internal static bool IsTransient(Exception exception) => exception switch
    {
        SmtpFailedRecipientsException recipients => recipients.InnerExceptions.Any(r => IsTransient(r.StatusCode)),
        SmtpFailedRecipientException recipient => IsTransient(recipient.StatusCode),
        SmtpException smtp => smtp.StatusCode == SmtpStatusCode.GeneralFailure || IsTransient(smtp.StatusCode),
        IOException or SocketException or TimeoutException or AuthenticationException or OperationCanceledException => true,
        _ => false
    };

    /// <summary>The reply code behind a failure, or -1 when the relay gave none (connection, TLS or timeout).</summary>
    internal static int ReplyCode(Exception exception) => exception switch
    {
        SmtpFailedRecipientsException { InnerExceptions.Length: > 0 } recipients => (int)(recipients.InnerExceptions
            .FirstOrDefault(r => IsTransient(r.StatusCode)) ?? recipients.InnerExceptions[0]).StatusCode,
        SmtpException smtp => (int)smtp.StatusCode,
        _ => (int)SmtpStatusCode.GeneralFailure
    };

    private static bool IsTransient(SmtpStatusCode status) => (int)status is >= 400 and <= 499;
}
