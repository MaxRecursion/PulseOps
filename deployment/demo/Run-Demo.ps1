#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)] [int] $CollectorPort = 5080,
    [ValidateRange(1, 65535)] [int] $AppPort = 5091,
    [string] $DotnetPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($CollectorPort -eq $AppPort) {
    throw 'Choose different values for -CollectorPort and -AppPort.'
}
if ($DotnetPath) {
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        throw '-DotnetPath must point to the dotnet executable.'
    }
    $dotnet = (Resolve-Path -LiteralPath $DotnetPath).Path
} else {
    $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
}

$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$solution = Join-Path $repo 'PulseOps.sln'
$collectorOrigin = "http://localhost:$CollectorPort"
$appOrigin = "http://localhost:$AppPort"
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()

function Assert-PortAvailable {
    param([int] $Port)

    $addresses = @([Net.IPAddress]::Loopback)
    if ([Net.Sockets.Socket]::OSSupportsIPv6) {
        $addresses += [Net.IPAddress]::IPv6Loopback
    }
    foreach ($address in $addresses) {
        $listener = [Net.Sockets.TcpListener]::new($address, $Port)
        try {
            $listener.Start()
        } catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -notin @('AddressNotAvailable', 'AddressFamilyNotSupported')) {
                throw "A loopback binding on port $Port is unavailable. Choose a different demo port."
            }
        } finally {
            $listener.Stop()
        }
    }
}

function Start-DotnetProcess {
    param([string[]] $Command, [string] $WorkingDirectory, [hashtable] $DemoSettings)

    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    foreach ($argument in $Command) { $start.ArgumentList.Add($argument) }

    if ($DemoSettings) {
        # Keep machine and shell hosting settings out of the disposable hosts.
        foreach ($name in @($start.Environment.Keys)) {
            if ($name -match '^(PulseOps[:_]|Kestrel[:_]|ASPNETCORE_|DOTNET_Kestrel[:_])' -or
                $name -match '^(DOTNET_)?(ENVIRONMENT|CONTENTROOT|URLS|HTTPS?_PORTS|AllowedHosts)$') {
                [void] $start.Environment.Remove($name)
            }
        }
        $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
        $start.Environment['DOTNET_ENVIRONMENT'] = 'Development'
        $start.Environment['Logging__LogLevel__Microsoft.AspNetCore'] = 'Warning'
        foreach ($name in $DemoSettings.Keys) { $start.Environment[$name] = $DemoSettings[$name] }
    }

    $process = [Diagnostics.Process]::Start($start)
    $ownedProcesses.Add($process)
    return $process
}

function Invoke-CheckedDotnet {
    param([string[]] $Command)

    $process = Start-DotnetProcess $Command $repo
    while (-not $process.WaitForExit(250)) { }
    if ($process.ExitCode -ne 0) {
        throw "dotnet $($Command[0]) failed (exit $($process.ExitCode)). To select another SDK executable, use -DotnetPath."
    }
}

function Assert-ProcessRunning {
    param([Diagnostics.Process] $Process, [string] $Name)

    if ($Process.HasExited) {
        throw "$Name exited (code $($Process.ExitCode)). Check the process output above."
    }
}

function Wait-DemoHealth {
    param([Diagnostics.Process] $Process, [string] $Name, [string] $Url)

    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    do {
        Assert-ProcessRunning $Process $Name
        try {
            $response = $client.GetAsync($Url).GetAwaiter().GetResult()
            try {
                if ($response.StatusCode -eq [Net.HttpStatusCode]::OK) {
                    Assert-ProcessRunning $Process $Name
                    return
                }
            } finally {
                $response.Dispose()
            }
        } catch [Net.Http.HttpRequestException], [OperationCanceledException] { }
        Start-Sleep -Milliseconds 250
    } while ($elapsed.Elapsed.TotalSeconds -lt 60)
    throw "$Name did not pass its health check within 60 seconds. Check the process output above."
}

Assert-PortAvailable $CollectorPort
Assert-PortAvailable $AppPort

$runDirectory = [IO.Directory]::CreateTempSubdirectory('pulseops-demo-').FullName
$artifacts = Join-Path $runDirectory 'artifacts'
$client = $null
try {
    Write-Host 'Preparing the PulseOps local demo with simulated data.'
    Invoke-CheckedDotnet @('restore', $solution, '--locked-mode', '--artifacts-path', $artifacts, '--disable-build-servers')
    Invoke-CheckedDotnet @('build', $solution, '-c', 'Release', '--no-restore', '--artifacts-path', $artifacts, '--disable-build-servers')

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(2)

    Assert-PortAvailable $CollectorPort
    $collector = Start-DotnetProcess @(
        (Join-Path $artifacts 'bin/PulseOps.Server/release/PulseOps.Server.dll'), '--urls', $collectorOrigin
    ) $runDirectory @{
        PulseOps__Demo = 'true'
        PulseOps__DatabasePath = (Join-Path $runDirectory 'pulseops.db')
        PulseOps__DataProtectionPath = (Join-Path $runDirectory 'keys')
        PulseOps__DemoBrowserOrigin = $appOrigin
        PulseOps__EmbeddedWorker = 'true'
    }
    Write-Host 'Waiting for the collector and its worker.'
    Wait-DemoHealth $collector 'PulseOps collector' "$collectorOrigin/health/ready"

    Assert-PortAvailable $AppPort
    $app = Start-DotnetProcess @(
        (Join-Path $artifacts 'bin/PulseOps.Demo.Api/release/PulseOps.Demo.Api.dll')
    ) $runDirectory @{
        PulseOps__BrowserOrigin = $appOrigin
        PulseOps__Server = $collectorOrigin
    }
    Wait-DemoHealth $app 'Demo application' "$appOrigin/api/health"

    Write-Host "Demo application  $appOrigin/"
    Write-Host "PulseOps portal   $collectorOrigin/"
    Write-Host 'Press Ctrl+C to stop the hosts and remove the disposable demo data.'
    while ($true) {
        Assert-ProcessRunning $collector 'PulseOps collector'
        Assert-ProcessRunning $app 'Demo application'
        Start-Sleep -Milliseconds 500
    }
} finally {
    if ($client) { $client.Dispose() }
    $allExited = $true
    for ($index = $ownedProcesses.Count - 1; $index -ge 0; $index--) {
        $process = $ownedProcesses[$index]
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { $allExited = $false }
            }
        } catch {
            if (-not $process.HasExited) { $allExited = $false }
        } finally {
            $process.Dispose()
        }
    }
    if ($allExited) {
        Remove-Item -LiteralPath $runDirectory -Recurse -Force
    } else {
        Write-Warning "A demo process has not stopped. Temporary files remain at $runDirectory."
    }
}
