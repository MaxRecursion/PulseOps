#Requires -Version 5.1
<#
.SYNOPSIS
Runs the PulseOps staging smoke and capacity soak against a deployed staging host and archives the evidence.

.DESCRIPTION
Read-only for the host configuration: the script runs the published StagingSmoke and Soak harnesses (framework-dependent
.NET 10 builds of deployment/staging/StagingSmoke.cs and deployment/performance/Soak.cs), collects environment evidence
and writes JSON reports, logs, a transcript and SHA-256 checksums under a new results directory. It never installs,
starts, stops or reconfigures services or IIS, and it never prints or stores credential values: harnesses read the machine
tokens from the named environment variables themselves.

Build the harnesses on the build agent, not on the staging host:
  dotnet publish deployment/staging/StagingSmoke.cs -c Release -o <tools>\smoke
  dotnet publish deployment/performance/Soak.cs -c Release -o <tools>\soak
The staging scope must allowlist the synthetic event name (default smoke-check). See docs/Staging.md.

.EXAMPLE
$env:PULSEOPS_STAGING_TOKEN = '<from the approved secret store>'
.\Invoke-StagingAcceptance.ps1 -BaseUrl https://pulseops-staging.internal.example -Application staging-smoke `
    -Environment staging -TokenVariable PULSEOPS_STAGING_TOKEN -ToolsDirectory C:\PulseOpsTools `
    -ResultsDirectory C:\PulseOpsEvidence\2026-11-22 -SoakMinutes 60
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')] [string] $Application,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')] [string] $Environment,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]{0,127}$')] [string] $TokenVariable,
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]{0,127}$')] [string] $OtherTokenVariable,
    [Parameter(Mandatory)] [string] $ToolsDirectory,
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [ValidateRange(1, 1440)] [int] $SoakMinutes = 30,
    [ValidateRange(1, 5000)] [int] $SteadyRate = 200,
    [ValidateRange(1, 5000)] [int] $BurstRate = 1000,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')] [string] $EventName = 'smoke-check',
    [ValidateRange(0, 2147483647)] [int] $ServerProcessId = 0,
    [string] $DatabasePath,
    [string] $ManifestPath,
    [switch] $SkipSoak
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-HttpsOrigin {
    param([string] $Value)
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -ne 'https' -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or ($uri.AbsolutePath -ne '/')) {
        throw 'BaseUrl must be an https origin such as https://pulseops-staging.internal.example with no path, query or credentials.'
    }
    return $uri.GetLeftPart([UriPartial]::Authority)
}

function Assert-Variable {
    param([string] $Name)
    if ([string]::IsNullOrEmpty([System.Environment]::GetEnvironmentVariable($Name))) {
        throw "Environment variable $Name is not set. Load the staging machine credential from the approved secret store."
    }
}

function Find-Harness {
    param([string] $Name)
    $candidates = @(Get-ChildItem -LiteralPath $ToolsDirectory -Filter "$Name.dll" -File -Recurse -ErrorAction SilentlyContinue)
    if ($candidates.Count -ne 1) {
        throw "Expected exactly one $Name.dll under $ToolsDirectory. Publish it with dotnet publish (see docs/Staging.md)."
    }
    return $candidates[0].FullName
}

# Runs a harness with the dotnet host. Native stderr must not become a terminating error under Windows PowerShell 5.1.
function Invoke-Harness {
    param([string] $Name, [string[]] $Arguments, [string] $LogPath)
    Write-Host "Running $Name ..."
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $script:Dotnet @Arguments *>&1 | ForEach-Object { "$_" } | Out-File -LiteralPath $LogPath -Encoding utf8
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    Write-Host "$Name finished with exit code $code."
    return $code
}

function Get-EnvironmentEvidence {
    $evidence = [ordered]@{
        collectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        computerRole = 'staging acceptance runner'
        powershell = $PSVersionTable.PSVersion.ToString()
        processorCount = [System.Environment]::ProcessorCount
        osVersion = [System.Environment]::OSVersion.VersionString
    }
    # Windows PowerShell (Desktop) only runs on Windows; PowerShell 7 reports the platform through $IsWindows.
    $onWindows = $PSVersionTable.PSEdition -eq 'Desktop'
    if (-not $onWindows) {
        $variable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
        $onWindows = ($null -ne $variable) -and [bool] $variable.Value
    }
    if ($onWindows) {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem
        $cpu = @(Get-CimInstance -ClassName Win32_Processor)
        $system = Get-CimInstance -ClassName Win32_ComputerSystem
        $evidence.os = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
        $evidence.cpu = ($cpu | ForEach-Object { "$($_.Name.Trim()) ($($_.NumberOfCores) cores, $($_.NumberOfLogicalProcessors) logical)" }) -join '; '
        $evidence.memoryGiB = [Math]::Round($system.TotalPhysicalMemory / 1GB, 1)
    }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $runtimes = & $script:Dotnet --list-runtimes 2>&1 | ForEach-Object { "$_" }
    }
    finally {
        $ErrorActionPreference = $previous
    }
    $evidence.dotnetRuntimes = @($runtimes)
    if ($ManifestPath) {
        $evidence.manifestSha256 = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $evidence
}

$origin = Assert-HttpsOrigin $BaseUrl
Assert-Variable $TokenVariable
if ($OtherTokenVariable) {
    if ($OtherTokenVariable -eq $TokenVariable) { throw 'OtherTokenVariable must name a credential for a different scope.' }
    Assert-Variable $OtherTokenVariable
}
if (-not (Test-Path -LiteralPath $ToolsDirectory -PathType Container)) { throw "ToolsDirectory $ToolsDirectory does not exist." }
if ($DatabasePath -and -not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) { throw "DatabasePath $DatabasePath does not exist." }
if ($ManifestPath -and -not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "ManifestPath $ManifestPath does not exist." }
if (Test-Path -LiteralPath $ResultsDirectory) { throw "ResultsDirectory $ResultsDirectory already exists. Use a new directory per run." }
$command = Get-Command -Name dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $command) { throw 'The dotnet host was not found. Install the approved .NET 10 runtime.' }
$script:Dotnet = $command.Source
$smoke = Find-Harness 'StagingSmoke'
$soak = if ($SkipSoak) { $null } else { Find-Harness 'Soak' }

$results = (New-Item -ItemType Directory -Path $ResultsDirectory).FullName
$transcript = Join-Path $results 'transcript.txt'
Start-Transcript -LiteralPath $transcript | Out-Null
$failed = $false
try {
    Get-EnvironmentEvidence | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $results 'environment.json') -Encoding utf8

    $smokeArguments = @($smoke, '--target', $origin, '--application', $Application, '--environment', $Environment,
        '--token-env', $TokenVariable, '--event-name', $EventName, '--report', (Join-Path $results 'smoke.json'))
    if ($OtherTokenVariable) { $smokeArguments += @('--other-token-env', $OtherTokenVariable) }
    if ((Invoke-Harness 'staging smoke' $smokeArguments (Join-Path $results 'smoke.log')) -ne 0) {
        $failed = $true
        Write-Warning 'The staging smoke failed. The soak is skipped. Review smoke.json.'
    }
    elseif ($soak) {
        $soakArguments = @($soak, '--target', $origin, '--application', $Application, '--environment', $Environment,
            '--token-env', $TokenVariable, '--minutes', "$SoakMinutes", '--steady-rate', "$SteadyRate",
            '--burst-rate', "$BurstRate", '--event-name', $EventName, '--report', (Join-Path $results 'soak.json'))
        if ($ServerProcessId -gt 0) { $soakArguments += @('--server-pid', "$ServerProcessId") }
        if ($DatabasePath) { $soakArguments += @('--database', $DatabasePath) }
        if ((Invoke-Harness 'capacity soak' $soakArguments (Join-Path $results 'soak.log')) -ne 0) {
            $failed = $true
            Write-Warning 'The capacity soak missed one or more gates. Review soak.json.'
        }
    }
}
finally {
    Stop-Transcript | Out-Null
    $lines = Get-ChildItem -LiteralPath $results -File | Where-Object Name -ne 'checksums.sha256' | Sort-Object Name |
        ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
    $lines | Out-File -LiteralPath (Join-Path $results 'checksums.sha256') -Encoding ascii
}

if ($failed) {
    Write-Error "Staging acceptance failed. Evidence: $results"
    exit 1
}
Write-Host "Staging acceptance passed. Evidence: $results"
exit 0
