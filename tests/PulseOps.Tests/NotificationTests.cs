using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed partial class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 8, 30, 0, TimeSpan.Zero);
    private const string Origin = "https://pulseops.example.test";
    private static readonly string[] OnCall = ["oncall@example.test", "lead@example.test"];
    private string _directory = null!;
    private PlatformOptions _options = null!;
    private AppScope _production = null!;
    private Storage _storage = null!;
    private FakeSmtpServer _smtp = null!;
    private Clock _clock = null!;
    private CapturingLogger _logger = null!;
    private RecordingHandler _webhooks = null!;
    private HttpClient _http = null!;

    [SetUp]
    public void Configure()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pulseops-notifications-" + Guid.NewGuid().ToString("N"));
        _production = new() { Application = "finance", Environment = "production", Label = "Finance", AllowedNames = ["report"] };
        _smtp = new FakeSmtpServer();
        _options = new()
        {
            DatabasePath = Path.Combine(_directory, "pulseops.db"), MinimumFreeMegabytes = 1,
            Scopes = [_production, new() { Application = "finance", Environment = "staging", Label = "Finance staging", AllowedNames = ["report"] }],
            Rules = [new() { Kind = "api", MinimumSamples = 3, MinimumFailures = 2, FailurePercent = 50 }],
            PortalOrigin = Origin,
            Smtp = new() { Host = "127.0.0.1", Port = _smtp.Port, UseTls = false, From = "pulseops@example.test", TimeoutSeconds = 5 },
            EmailRoutes = [new() { Name = "finance-oncall", Recipients = OnCall, Application = "finance" }]
        };
        // Worker-mode validation outside Development: a cleartext relay without credentials is accepted on loopback.
        _options.Validate(development: false, _directory, databaseOnly: true);
        _storage = new Storage(_options);
        _storage.Migrate();
        _clock = new Clock { Now = Now };
        _logger = new CapturingLogger();
        _webhooks = new RecordingHandler();
        _http = new HttpClient(_webhooks);
    }

    [TearDown]
    public async Task Cleanup()
    {
        _http.Dispose();
        await _smtp.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    [Test]
    public async Task EmailReachesEveryRouteRecipientWithoutEvidence()
    {
        var incident = OpenIncident();
        Assert.That(await Worker().DeliverOnceAsync(_http, CancellationToken.None), Is.True);

        var delivery = Delivery("finance-oncall");
        var message = _smtp.Messages.Single();
        string visible = message.Header("Subject") + "\n" + message.Body;
        Assert.Multiple(() =>
        {
            Assert.That((delivery.State, delivery.Attempts), Is.EqualTo(("delivered", 1L)));
            Assert.That(message.Sender, Is.EqualTo("pulseops@example.test"));
            Assert.That(message.Recipients, Is.EquivalentTo(OnCall));
            Assert.That(message.Header("To"), Does.Contain(OnCall[0]).And.Contain(OnCall[1]));
            Assert.That(message.Header("Subject"), Is.EqualTo("[PulseOps] HIGH Open: finance/production"));
            Assert.That(message.Header("X-PulseOps-Delivery"), Is.EqualTo(delivery.Id));
            Assert.That(message.Header("Message-ID"), Is.EqualTo($"<{delivery.Id}@example.test>"));
            Assert.That(message.Header("Auto-Submitted"), Is.EqualTo("auto-generated"));
            Assert.That(message.Body, Does.Contain("Incident: " + incident.Id));
            Assert.That(message.Body, Does.Contain("finance/production").And.Contain("high").And.Contain("Open"));
            Assert.That(message.Body, Does.Contain("2026-11-21T08:30:00Z"));
            Assert.That(message.Body, Does.Contain("Delivery: " + delivery.Id));
            Assert.That(message.Body, Does.Contain($"{Origin}/incidents/{incident.Id}"));
            Assert.That(visible, Does.Not.Contain(incident.Evidence));
            Assert.That(visible, Does.Not.Contain("observations").And.Not.Contain("report"), "No evidence text or telemetry names.");
            Assert.That(_webhooks.Requests, Is.Empty);
        });
        Assert.That(await Worker().DeliverOnceAsync(_http, CancellationToken.None), Is.False, "A delivered alert is not sent again.");
    }

    // Columns: greeting, reply to RCPT for each recipient, reply after DATA, resulting state, logged reply code.
    [TestCase("421 4.3.2 Service not available", null, null, null, "pending", 421)]
    [TestCase(null, "451 4.3.0 Try again later", "451 4.3.0 Try again later", null, "pending", 451)]
    [TestCase(null, "450 4.2.1 Mailbox busy", "550 5.1.1 No such user", null, "pending", 450)]
    [TestCase(null, null, "452 4.2.2 Mailbox full", null, "pending", 452)]
    [TestCase(null, null, null, "451 4.3.0 Local error in processing", "pending", 451)]
    [TestCase(null, "550 5.1.1 No such user", "553 5.1.3 Mailbox name not allowed", null, "failed", 550)]
    [TestCase(null, null, "550 5.1.1 No such user", null, "failed", 550)]
    [TestCase(null, null, null, "554 5.6.0 Transaction failed", "failed", 554)]
    public async Task SmtpRepliesDecideBetweenRetryAndFailure(string? greeting, string? onCallReply, string? leadReply,
        string? dataReply, string expected, int status)
    {
        if (greeting is not null)
            _smtp.Greeting = greeting;
        if (onCallReply is not null)
            _smtp.RecipientReplies[OnCall[0]] = onCallReply;
        if (leadReply is not null)
            _smtp.RecipientReplies[OnCall[1]] = leadReply;
        if (dataReply is not null)
            _smtp.DataReply = dataReply;
        OpenIncident();
        var worker = Worker();

        Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        var delivery = Delivery("finance-oncall");
        string logs = string.Join("\n", _logger.Lines);
        Assert.Multiple(() =>
        {
            Assert.That((delivery.State, delivery.Attempts, delivery.LeaseId), Is.EqualTo((expected, 1L, (string?)null)));
            Assert.That(logs, Does.Contain(delivery.Id).And.Contain("SMTP status " + status));
            Assert.That(logs, Does.Not.Contain("example.test"), "Recipients and server replies are never logged.");
        });
        if (expected == "pending")
        {
            Assert.That(delivery.NextAttempt, Is.InRange(Now.AddSeconds(11), Now.AddSeconds(15)), "The first retry follows the backoff.");
            _clock.Now = Now.AddSeconds(5);
            Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.False, "The retry waits for its backoff.");
        }
        else
        {
            _clock.Now = Now.AddHours(2);
            Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.False, "A permanent rejection is not retried.");
        }
    }

    [Test]
    public async Task DeferredEmailIsRetriedWithTheSameDeliveryIdAfterBackoff()
    {
        _smtp.DataReply = "451 4.3.0 Try again later";
        OpenIncident();
        var worker = Worker();
        Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        var deferred = Delivery("finance-oncall");
        Assert.That(_smtp.Messages, Is.Empty);

        _smtp.DataReply = "250 2.0.0 Queued";
        _clock.Now = deferred.NextAttempt;
        Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        var delivered = Delivery("finance-oncall");
        var message = _smtp.Messages.Single();
        Assert.Multiple(() =>
        {
            Assert.That((delivered.State, delivered.Attempts), Is.EqualTo(("delivered", 2L)));
            Assert.That(message.Header("X-PulseOps-Delivery"), Is.EqualTo(deferred.Id));
            Assert.That(message.Header("Message-ID"), Is.EqualTo($"<{deferred.Id}@example.test>"));
        });
    }

    [Test]
    public async Task TransientFailuresStopAfterSixAttempts()
    {
        _smtp.DataReply = "451 4.3.0 Try again later";
        OpenIncident();
        var worker = Worker();
        for (int attempt = 1; attempt <= NotificationWorker.MaximumAttempts; attempt++)
        {
            _clock.Now = Now.AddHours(attempt);
            Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.True, $"Attempt {attempt} is due.");
        }
        var delivery = Delivery("finance-oncall");
        Assert.Multiple(() =>
        {
            Assert.That((delivery.State, delivery.Attempts), Is.EqualTo(("failed", 6L)));
            Assert.That(_logger.Lines.Last(), Does.Contain("failed").And.Contain("SMTP status 451"));
        });
        _clock.Now = Now.AddDays(2);
        Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.False);
    }

    [Test]
    public async Task StalledAndUnreachableRelaysAreRetried()
    {
        _options.Smtp.TimeoutSeconds = 1;
        _smtp.Silent = true;
        OpenIncident();
        var worker = Worker();

        var elapsed = Stopwatch.StartNew();
        Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        elapsed.Stop();
        var stalled = Delivery("finance-oncall");
        Assert.Multiple(() =>
        {
            Assert.That((stalled.State, stalled.Attempts), Is.EqualTo(("pending", 1L)));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "A silent relay is abandoned after TimeoutSeconds.");
        });

        _options.Smtp.Port = ClosedPort();
        _clock.Now = stalled.NextAttempt;
        Assert.That(await worker.DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        var refused = Delivery("finance-oncall");
        Assert.Multiple(() =>
        {
            Assert.That((refused.State, refused.Attempts), Is.EqualTo(("pending", 2L)));
            Assert.That(_logger.Lines, Has.Count.EqualTo(2).And.All.Contain("SMTP status -1"), "Neither failure has a reply code.");
        });
    }

    [Test]
    public async Task ShutdownDuringSendLeavesTheDeliveryLeasedForLaterRetry()
    {
        _smtp.Silent = true;
        OpenIncident();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.That(async () => await Worker().DeliverOnceAsync(_http, shutdown.Token), Throws.InstanceOf<OperationCanceledException>());
        var leased = Delivery("finance-oncall");
        Assert.That((leased.State, leased.Attempts, leased.LeaseId is not null), Is.EqualTo(("pending", 1L, true)),
            "An interrupted send is neither failed nor delivered.");

        _smtp.Silent = false;
        _clock.Now = Now.AddMinutes(1);
        Assert.That(await Worker().DeliverOnceAsync(_http, CancellationToken.None), Is.False, "The lease still holds.");
        _clock.Now = Now.AddMinutes(2);
        Assert.That(await Worker().DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        var delivered = Delivery("finance-oncall");
        Assert.Multiple(() =>
        {
            Assert.That((delivered.State, delivered.Attempts), Is.EqualTo(("delivered", 2L)));
            Assert.That(_smtp.Messages.Single().Header("X-PulseOps-Delivery"), Is.EqualTo(leased.Id));
            Assert.That(_logger.Lines, Is.Empty, "Shutdown is not logged as a delivery failure.");
        });
    }

    [TestCase("high", new[] { "finance-oncall", "pager" })]
    [TestCase("critical", new[] { "critical-only", "finance-oncall", "pager" })]
    [TestCase("low", new[] { "triage" })]
    public async Task IncidentEnqueuesWebhookAndEmailRoutesBySeverity(string severity, string[] expected)
    {
        _options.Rules[0].Severity = severity;
        _options.Webhooks = [new() { Name = "pager", Url = "https://hooks.example.test/pulseops" }];
        _options.EmailRoutes =
        [
            new() { Name = "finance-oncall", Recipients = OnCall, Application = "finance" },
            new() { Name = "critical-only", Recipients = ["cio@example.test"], Severities = ["critical"] },
            new() { Name = "triage", Recipients = ["triage@example.test"], Severities = ["low"] }
        ];
        var incident = OpenIncident();
        Assert.That(Destinations(), Is.EqualTo(expected));

        var sender = new RecordingSender();
        var worker = Worker(sender);
        for (int i = 0; i < 10 && await worker.DeliverOnceAsync(_http, CancellationToken.None); i++) { }

        var routeOf = Rows("SELECT id, destination FROM alert_deliveries", row => (Id: row.GetString(0), Name: row.GetString(1)))
            .ToDictionary(x => x.Id, x => x.Name);
        Assert.Multiple(() =>
        {
            Assert.That(Rows("SELECT delivery_state FROM alert_deliveries", row => row.GetString(0)), Has.All.EqualTo("delivered"));
            Assert.That(_webhooks.Requests.Select(r => routeOf[r.IdempotencyKey!]), Is.EquivalentTo(expected.Where(x => x == "pager")));
            Assert.That(_webhooks.Requests.Select(r => r.Body), Has.All.Contain($"\"incidentUrl\":\"{Origin}/incidents/{incident.Id}\""));
            Assert.That(sender.Sent.Select(m => routeOf[m.DeliveryId]), Is.EquivalentTo(expected.Where(x => x != "pager")));
            Assert.That(_smtp.Messages, Is.Empty, "The injected sender replaces the SMTP relay.");
            foreach (var mail in sender.Sent)
            {
                Assert.That(mail.Recipients, Is.EqualTo(_options.EmailRoutes.Single(r => r.Name == routeOf[mail.DeliveryId]).Recipients));
                Assert.That(mail.Subject, Is.EqualTo($"[PulseOps] {severity.ToUpperInvariant()} Open: finance/production"));
                Assert.That(mail.From, Is.EqualTo("pulseops@example.test"));
            }
        });
    }

    [Test]
    public void RoutesScopedToAnotherApplicationOrEnvironmentAreNotEnqueued()
    {
        _options.EmailRoutes =
        [
            new() { Name = "payroll-oncall", Recipients = ["payroll@example.test"], Application = "payroll" },
            new() { Name = "finance-staging", Recipients = ["qa@example.test"], Application = "finance", Environment = "staging" },
            new() { Name = "finance-production", Recipients = OnCall, Application = "finance", Environment = "production" }
        ];
        OpenIncident();
        Assert.That(Destinations(), Is.EqualTo(new[] { "finance-production" }));
    }

    [Test]
    public async Task DeliveryWhoseRouteWasRemovedFails()
    {
        OpenIncident();
        _options.EmailRoutes = [];
        var sender = new RecordingSender();
        Assert.That(await Worker(sender).DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(Delivery("finance-oncall").State, Is.EqualTo("failed"));
            Assert.That(sender.Sent, Is.Empty);
        });
    }

    [Test]
    public void SmtpValidationAllowsCleartextOnlyToALoopbackRelayWithoutCredentials()
    {
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => WebHost("127.0.0.1", tls: false).Validate(false, _directory, false));
            Assert.DoesNotThrow(() => WebHost("localhost", tls: false).Validate(false, _directory, false));
            Assert.DoesNotThrow(() => WebHost("smtp.example.test", tls: true, "pulseops-relay", "placeholder").Validate(false, _directory, false));
            Assert.DoesNotThrow(() => WebHost("smtp.example.test", tls: false).Validate(true, _directory, false),
                "Development may use a cleartext relay without credentials.");
            Assert.Throws<InvalidOperationException>(() => WebHost("smtp.example.test", tls: false).Validate(false, _directory, false),
                "Cleartext to a remote relay is rejected outside Development.");
            Assert.Throws<InvalidOperationException>(() => WebHost("127.0.0.1", tls: false, "pulseops-relay", "placeholder").Validate(false, _directory, false),
                "Credentials require TLS.");
            Assert.Throws<InvalidOperationException>(() => WebHost("127.0.0.1", tls: false, "pulseops-relay", "placeholder").Validate(true, _directory, false),
                "Credentials require TLS in Development too.");
            Assert.Throws<InvalidOperationException>(() => WebHost("smtp.example.test", tls: true, "pulseops-relay").Validate(false, _directory, false),
                "A user name requires a password.");
        });
    }

    [Test]
    public void SmtpFailureClassificationFollowsReplyClasses()
    {
        static SmtpFailedRecipientException Recipient(SmtpStatusCode code) => new(code, "someone@example.test");
        Assert.Multiple(() =>
        {
            foreach (var code in new[] { SmtpStatusCode.ServiceNotAvailable, SmtpStatusCode.MailboxBusy,
                         SmtpStatusCode.LocalErrorInProcessing, SmtpStatusCode.InsufficientStorage, SmtpStatusCode.ClientNotPermitted })
                Assert.That(SmtpAlertEmailSender.IsTransient(new SmtpException(code)), Is.True, code.ToString());
            foreach (var code in new[] { SmtpStatusCode.MailboxUnavailable, SmtpStatusCode.MailboxNameNotAllowed,
                         SmtpStatusCode.TransactionFailed, SmtpStatusCode.SyntaxError, SmtpStatusCode.CommandUnrecognized,
                         SmtpStatusCode.MustIssueStartTlsFirst })
                Assert.That(SmtpAlertEmailSender.IsTransient(new SmtpException(code)), Is.False, code.ToString());
            Assert.That(SmtpAlertEmailSender.IsTransient(new SmtpException("Failure sending mail.", new IOException())), Is.True);
            Assert.That(SmtpAlertEmailSender.IsTransient(new IOException()), Is.True);
            Assert.That(SmtpAlertEmailSender.IsTransient(new SocketException((int)SocketError.ConnectionRefused)), Is.True);
            Assert.That(SmtpAlertEmailSender.IsTransient(new TaskCanceledException()), Is.True);
            Assert.That(SmtpAlertEmailSender.IsTransient(new InvalidOperationException()), Is.False);
            Assert.That(SmtpAlertEmailSender.IsTransient(Recipient(SmtpStatusCode.MailboxBusy)), Is.True);
            Assert.That(SmtpAlertEmailSender.IsTransient(Recipient(SmtpStatusCode.MailboxUnavailable)), Is.False);
            var mixed = new SmtpFailedRecipientsException("", [Recipient(SmtpStatusCode.MailboxUnavailable), Recipient(SmtpStatusCode.MailboxBusy)]);
            var rejected = new SmtpFailedRecipientsException("", [Recipient(SmtpStatusCode.MailboxUnavailable), Recipient(SmtpStatusCode.MailboxNameNotAllowed)]);
            Assert.That((SmtpAlertEmailSender.IsTransient(mixed), SmtpAlertEmailSender.ReplyCode(mixed)), Is.EqualTo((true, 450)));
            Assert.That((SmtpAlertEmailSender.IsTransient(rejected), SmtpAlertEmailSender.ReplyCode(rejected)), Is.EqualTo((false, 550)));
            Assert.That(SmtpAlertEmailSender.ReplyCode(new IOException()), Is.EqualTo(-1));
        });
    }

    [Test]
    public async Task TlsRequiredRelayWithoutStartTlsNeverReceivesTheEnvelope()
    {
        _options.Smtp.UseTls = true;
        OpenIncident();
        Assert.That(await Worker().DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        var delivery = Delivery("finance-oncall");
        Assert.Multiple(() =>
        {
            Assert.That(_smtp.Messages, Is.Empty);
            Assert.That(_smtp.Verbs, Does.Not.Contain("MAIL"), "No envelope is sent in cleartext.");
            Assert.That(_smtp.Verbs, Does.Not.Contain("RCPT"));
            Assert.That((delivery.State, delivery.Attempts), Is.EqualTo(("pending", 1L)));
        });
    }

    [Test]
    public async Task StartTlsPrecedesTheEnvelopeAndAnUntrustedRelayIsRefused()
    {
        _options.Smtp.UseTls = true;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=relay.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        _smtp.StartTlsCertificate = certificate;
        OpenIncident();
        Assert.That(await Worker().DeliverOnceAsync(_http, CancellationToken.None), Is.True);
        var delivery = Delivery("finance-oncall");
        Assert.Multiple(() =>
        {
            Assert.That(_smtp.Verbs, Does.Contain("STARTTLS"));
            Assert.That(_smtp.Verbs, Does.Not.Contain("MAIL"), "The envelope never precedes a trusted TLS session.");
            Assert.That(_smtp.Messages, Is.Empty);
            Assert.That(delivery.State, Is.EqualTo("pending"));
        });
    }

    [Test]
    public void HostedWorkerResolvesWithoutAnEmailSenderRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_options);
        services.AddSingleton(_storage);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddLogging();
        services.AddHttpClient();
        services.AddHostedService<NotificationWorker>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.That(provider.GetServices<IHostedService>().OfType<NotificationWorker>().Count(), Is.EqualTo(1));
    }

    private NotificationWorker Worker(IAlertEmailSender? sender = null) =>
        new(_options, _storage, new NoClients(), _clock, _logger, sender);

    private (string Id, string Evidence) OpenIncident()
    {
        _storage.Ingest(_production, [Event("success"), Event("failure"), Event("failure")], false, Now);
        _storage.Process(Now);
        return Rows("SELECT id, evidence FROM incidents", row => (row.GetString(0), row.GetString(1))).Single();
    }

    private string[] Destinations() =>
        [.. Rows("SELECT destination FROM alert_deliveries ORDER BY destination", row => row.GetString(0))];

    private (string Id, string State, long Attempts, DateTimeOffset NextAttempt, string? LeaseId) Delivery(string destination) =>
        Rows("SELECT id, delivery_state, attempts, next_attempt, lease_id FROM alert_deliveries WHERE destination = $destination",
            row => (row.GetString(0), row.GetString(1), row.GetInt64(2), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(3)),
                row.IsDBNull(4) ? null : row.GetString(4)), ("$destination", destination)).Single();

    private List<T> Rows<T>(string sql, Func<Microsoft.Data.Sqlite.SqliteDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        using var connection = _storage.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(map(reader));
        return rows;
    }

    private PlatformOptions WebHost(string host, bool tls, string user = "", string password = "") => new()
    {
        DatabasePath = Path.Combine(_directory, "validation.db"), DataProtectionPath = Path.Combine(_directory, "keys"),
        Auth = new() { Authority = "https://login.example.test", ClientId = "pulseops", ClientSecret = "placeholder" },
        PortalOrigin = Origin,
        Smtp = new() { Host = host, UseTls = tls, From = "pulseops@example.test", UserName = user, Password = password },
        EmailRoutes = [new() { Name = "ops", Recipients = ["ops@example.test"] }]
    };

    private static TelemetryEvent Event(string outcome) => new(Guid.NewGuid(), Now, "1.0.0", Guid.NewGuid(), "api", "report",
        outcome, 200, outcome == "failure" ? 503 : 200);

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Tests pass their HttpClient to DeliverOnceAsync.");
    }

    private sealed class RecordingSender : IAlertEmailSender
    {
        public ConcurrentQueue<AlertEmail> Sent { get; } = new();

        public Task SendAsync(AlertEmail message, CancellationToken token)
        {
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(string? IdempotencyKey, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Enqueue((request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private sealed class CapturingLogger : ILogger<NotificationWorker>
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    private sealed record SmtpMessage(string Sender, string[] Recipients, string Data)
    {
        private int Split => Data.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        public string Body => Data[(Split + 4)..];

        /// <summary>The single unfolded value of a header. Fails when the header is missing or repeated.</summary>
        public string Header(string name) => Data[..Split].Replace("\r\n ", " ").Replace("\r\n\t", "\t").Split("\r\n")
            .Where(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(name.Length + 1)..].Trim()).Single();
    }

    /// <summary>
    /// A loopback SMTP relay for one connection at a time. It answers EHLO/HELO, MAIL, RCPT, DATA, RSET, NOOP and QUIT,
    /// records command verbs and accepted messages, and can defer or reject the greeting, individual recipients or the
    /// message. With a certificate it offers STARTTLS and ends the session after the handshake attempt.
    /// </summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public FakeSmtpServer()
        {
            _listener.Start();
            _serving = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public ConcurrentQueue<SmtpMessage> Messages { get; } = new();
        public ConcurrentQueue<string> Verbs { get; } = new();
        public ConcurrentDictionary<string, string> RecipientReplies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public X509Certificate2? StartTlsCertificate { get; set; }
        public string Greeting { get; set; } = "220 relay.example.test ESMTP ready";
        public string DataReply { get; set; } = "250 2.0.0 Queued";
        /// <summary>Accepts connections but never greets, like a stalled relay.</summary>
        public bool Silent { get; set; }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient connection;
                try
                {
                    connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
                using (connection)
                {
                    try
                    {
                        await SessionAsync(connection.GetStream(), _stop.Token);
                    }
                    catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
                }
            }
        }

        private async Task SessionAsync(NetworkStream stream, CancellationToken token)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            if (Silent)
            {
                while (await reader.ReadLineAsync(token) is not null) { }
                return;
            }
            await writer.WriteLineAsync(Greeting);
            if (!Greeting.StartsWith("220", StringComparison.Ordinal))
                return;

            string sender = "";
            var recipients = new List<string>();
            while (await reader.ReadLineAsync(token) is { } line)
            {
                string verb = line.Split(' ', 2)[0].ToUpperInvariant();
                Verbs.Enqueue(verb);
                switch (verb)
                {
                    case "EHLO" or "HELO":
                        await writer.WriteLineAsync(StartTlsCertificate is null ? "250 relay.example.test" : "250-relay.example.test\r\n250 STARTTLS");
                        break;
                    case "STARTTLS" when StartTlsCertificate is not null:
                        await writer.WriteLineAsync("220 2.0.0 Ready to start TLS");
                        await using (var tls = new SslStream(stream, leaveInnerStreamOpen: true))
                        {
                            try
                            {
                                await tls.AuthenticateAsServerAsync(StartTlsCertificate);
                            }
                            catch (Exception exception) when (exception is AuthenticationException or IOException) { }
                        }
                        return;
                    case "MAIL":
                        (sender, recipients) = (Address(line), []);
                        await writer.WriteLineAsync("250 2.1.0 Sender OK");
                        break;
                    case "RCPT":
                        string recipient = Address(line);
                        string reply = RecipientReplies.GetValueOrDefault(recipient, "250 2.1.5 Recipient OK");
                        if (reply.StartsWith('2'))
                            recipients.Add(recipient);
                        await writer.WriteLineAsync(reply);
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(token) is { } content && content != ".")
                            data.Append(content.StartsWith("..", StringComparison.Ordinal) ? content[1..] : content).Append("\r\n");
                        if (DataReply.StartsWith('2'))
                            Messages.Enqueue(new SmtpMessage(sender, [.. recipients], data.ToString()));
                        await writer.WriteLineAsync(DataReply);
                        break;
                    case "RSET":
                        (sender, recipients) = ("", []);
                        await writer.WriteLineAsync("250 2.0.0 Reset");
                        break;
                    case "NOOP":
                        await writer.WriteLineAsync("250 2.0.0 OK");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("502 5.5.2 Command not implemented");
                        break;
                }
            }
        }

        private static string Address(string line)
        {
            int start = line.IndexOf('<');
            int end = line.IndexOf('>');
            return start >= 0 && end > start ? line[(start + 1)..end] : "";
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serving;
            _stop.Dispose();
        }
    }
}
