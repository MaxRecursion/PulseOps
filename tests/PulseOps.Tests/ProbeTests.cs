using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using PulseOps.Server;

namespace PulseOps.Tests;

public sealed class ProbeTests
{
    [Test]
    public async Task UntrustedCertificateIsReportedAsTlsWithRemainingValidity()
    {
        var now = DateTimeOffset.UtcNow;
        using var certificate = SelfSigned(now.AddDays(-1), now.AddDays(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            using var tls = new SslStream(connection.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(certificate);
            }
            catch (Exception exception) when (exception is IOException or AuthenticationException) { }
        });

        var result = await ProbeClient.CheckAsync(Probe($"https://localhost:{port}/health"), now, CancellationToken.None);
        listener.Stop();
        await server;

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo("failure"));
            Assert.That(result.Category, Is.EqualTo("tls"));
            Assert.That(result.CertificateDays, Is.InRange(9d, 10d));
            Assert.That(result.StatusCode, Is.Null);
        });
    }

    [Test]
    public async Task UnresolvableHostIsReportedAsDns()
    {
        var result = await ProbeClient.CheckAsync(Probe("https://pulseops-probe-test.invalid/health"),
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.That((result.Outcome, result.Category), Is.EqualTo(("failure", "dns")));
    }

    [Test]
    public async Task RefusedConnectionIsReportedAsConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var result = await ProbeClient.CheckAsync(Probe($"https://127.0.0.1:{port}/health"),
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.That((result.Outcome, result.Category), Is.EqualTo(("failure", "connection")));
    }

    [Test]
    public void ExpiringCertificateDegradesAPassingCheck()
    {
        var now = DateTimeOffset.UtcNow;
        var degraded = ProbeClient.Evaluate(Probe("https://status.example.test/"), 200, null, now.AddDays(5), now, 12);
        var disabled = ProbeClient.Evaluate(Probe("https://status.example.test/", warningDays: 0), 200, null, now.AddDays(5), now, 12);
        var healthy = ProbeClient.Evaluate(Probe("https://status.example.test/"), 200, null, now.AddDays(90), now, 12);
        var failed = ProbeClient.Evaluate(Probe("https://status.example.test/"), 503, null, now.AddDays(90), now, 12);
        var unexpected = ProbeClient.Evaluate(Probe("https://status.example.test/"), 302, null, null, now, 12);

        Assert.Multiple(() =>
        {
            Assert.That((degraded.Outcome, degraded.Category, degraded.CertificateDays), Is.EqualTo(("degraded", "tls", (double?)5)));
            Assert.That((disabled.Outcome, disabled.Category), Is.EqualTo(("success", "probe")));
            Assert.That((healthy.Outcome, healthy.CertificateDays), Is.EqualTo(("success", (double?)90)));
            Assert.That((failed.Outcome, failed.Category, failed.StatusCode), Is.EqualTo(("failure", "server-error", (int?)503)));
            Assert.That((unexpected.Outcome, unexpected.Category), Is.EqualTo(("failure", "http")));
        });
    }

    [Test]
    public void ProbeCertificateWarningIsBounded()
    {
        var options = new PlatformOptions
        {
            DatabasePath = "/tmp/unused.db",
            Scopes = [new() { Application = "finance", Environment = "production", Label = "Finance", AllowedNames = ["status"] }],
            Probes = [new() { Application = "finance", Environment = "production", Name = "status", Url = "https://status.example.test/", CertificateWarningDays = 400 }]
        };
        Assert.Throws<InvalidOperationException>(() => options.Validate(true, "/tmp", true));
    }

    private static ProbeSettings Probe(string url, int warningDays = 14) => new()
    {
        Application = "finance", Environment = "production", Name = "status", Url = url, ExpectedStatus = 200,
        CertificateWarningDays = warningDays
    };

    private static X509Certificate2 SelfSigned(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(notBefore, notAfter);
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}
