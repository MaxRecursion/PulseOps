using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PulseOps.Demo.Blazor;
using PulseOps.Sdk.Blazor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

builder.Services.AddPulseOps(options =>
{
    options.Application = "finance-demo";
    options.Environment = "demo";
    options.Version = "1.0.0";
    options.IngestionEndpoint = "/pulseops/telemetry";
    options.SamplingRate = 1;
    options.FlushInterval = TimeSpan.FromSeconds(2);
    options.ApiEndpoints["/api/report"] = "report-api";
    options.NavigationRoutes["/"] = "finance-workspace";
    options.AllowedOperations.UnionWith(["generate-report", "download-report", "parse-report", "render-grid", "sign-in"]);
});

builder.Services.AddHttpClient("Reports", client =>
{
    client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress);
    client.Timeout = TimeSpan.FromSeconds(12);
}).AddHttpMessageHandler<PulseOpsHttpHandler>();

var host = builder.Build();
await host.Services.GetRequiredService<PulseOpsClient>().StartAsync();
await host.RunAsync();
