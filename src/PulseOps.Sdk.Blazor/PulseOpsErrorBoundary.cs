using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace PulseOps.Sdk.Blazor;

public sealed class PulseOpsErrorBoundary : ErrorBoundary
{
    [Inject] private PulseOpsClient PulseOps { get; set; } = default!;

    protected override Task OnErrorAsync(Exception exception)
    {
        PulseOps.ReportBoundaryError(exception);
        return base.OnErrorAsync(exception);
    }
}

public sealed class PulseOpsReady : ComponentBase
{
    [Inject] private PulseOpsClient PulseOps { get; set; } = default!;

    protected override Task OnAfterRenderAsync(bool firstRender) =>
        firstRender ? PulseOps.MarkApplicationReadyAsync().AsTask() : Task.CompletedTask;
}
