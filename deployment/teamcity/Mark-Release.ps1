#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [uri] $PulseOpsOrigin,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')] [string] $Application,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')] [string] $Environment,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$')] [string] $Version,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')] [string] $BuildId,
    [Parameter(Mandatory)] [ValidatePattern('^(?:[A-Fa-f0-9]{40}|[A-Fa-f0-9]{64})$')] [string] $Commit
)

$ErrorActionPreference = 'Stop'
if (-not $PulseOpsOrigin.IsAbsoluteUri -or $PulseOpsOrigin.Scheme -ne 'https' -or
    $PulseOpsOrigin.AbsolutePath -ne '/' -or $PulseOpsOrigin.Query -or $PulseOpsOrigin.Fragment -or $PulseOpsOrigin.UserInfo) {
    throw 'Use a trusted PulseOps HTTPS origin.'
}
if (-not $env:PULSEOPS_RELEASE_TOKEN) { throw 'Set PULSEOPS_RELEASE_TOKEN from protected CI credentials.' }

$body = [ordered] @{
    application = $Application
    environment = $Environment
    version = $Version
    buildId = $BuildId
    commitId = $Commit.ToLowerInvariant()
} | ConvertTo-Json -Compress

$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)
$client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $env:PULSEOPS_RELEASE_TOKEN)
$endpoint = [uri]::new($PulseOpsOrigin, '/api/telemetry/v1/releases')
try {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $content = [Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
        try {
            $response = $client.PostAsync($endpoint, $content).GetAwaiter().GetResult()
            try {
                $status = [int] $response.StatusCode
                if ($status -eq 202) {
                    Write-Host "PulseOps accepted release '$BuildId' for '$Application/$Environment'."
                    return
                }
                if ($attempt -eq 3 -or $status -notin @(429, 503)) {
                    throw "PulseOps rejected the release marker with HTTP $status."
                }
                $delay = 5
                $guidance = $response.Headers.RetryAfter
                if ($guidance -and $guidance.Delta) {
                    $delay = [Math]::Clamp($guidance.Delta.TotalSeconds, 1, 15)
                } elseif ($guidance -and $guidance.Date) {
                    $delay = [Math]::Clamp(($guidance.Date - [DateTimeOffset]::UtcNow).TotalSeconds, 1, 15)
                }
            } finally {
                $response.Dispose()
            }
        } finally {
            $content.Dispose()
        }
        Start-Sleep -Seconds $delay
    }
} finally {
    $client.Dispose()
}
