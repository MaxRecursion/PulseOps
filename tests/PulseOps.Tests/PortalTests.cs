using Bunit;
using NUnit.Framework;
using PulseOps.Portal.Components;
using PulseOps.Portal.Services;

namespace PulseOps.Tests;

public sealed class PortalTests
{
    [Test]
    public void ErrorStateExposesRetryAndInvokesIt()
    {
        using var context = new BunitContext();
        bool attempted = false;
        var page = context.Render<DataState>(parameters => parameters
            .Add(p => p.Error, "The selected query is unavailable.")
            .Add(p => p.Retry, () => attempted = true));
        Assert.That(page.Find("[role=alert]").TextContent, Does.Contain("selected query"));
        page.Find("button").Click();
        Assert.That(attempted, Is.True);
    }

    [Test]
    public void LoadingStateAnnouncesThePendingQuery()
    {
        using var context = new BunitContext();
        var page = context.Render<DataState>(parameters => parameters.Add(p => p.Loading, true));
        Assert.That(page.Find("[role=status]").TextContent, Does.Contain("Loading data"));
    }

    [Test]
    public void NoMeasurementsShowAnEmptyState()
    {
        using var context = new BunitContext();
        var page = context.Render<TimeSeries>(parameters => parameters
            .Add(p => p.Label, "Request latency")
            .Add(p => p.Times, Array.Empty<DateTimeOffset>())
            .Add(p => p.Series, Array.Empty<ChartSeries>()));
        Assert.That(page.Markup, Does.Contain("No chart samples"));
        Assert.That(page.FindAll("svg[role=img]"), Is.Empty);
    }

    [Test]
    public void ChartValuesPreserveMissingSamplesAndAccessibleLabels()
    {
        using var context = new BunitContext();
        var start = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);
        var page = context.Render<TimeSeries>(parameters => parameters
            .Add(p => p.Label, "Request latency")
            .Add(p => p.Times, new[] { start, start.AddMinutes(5) })
            .Add(p => p.Series, new[] { new ChartSeries("P95", "#5271ff", [122, null]) })
            .Add(p => p.Unit, "ms"));
        Assert.That(page.Find("svg[role=img]").GetAttribute("aria-label"), Is.EqualTo("Request latency"));
        Assert.That(page.Find("tbody tr:last-child .numeric").TextContent, Is.EqualTo("—"));
    }
}
