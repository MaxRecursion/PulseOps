#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [string] $Commit = $env:BUILD_VCS_NUMBER,
    [string] $BuildNumber = $env:BUILD_NUMBER,
    [string] $NuGetConfig,
    [string] $OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Import-Module (Join-Path $repo 'deployment/windows/PulseOps.Tools.psm1')
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts' }
$artifact = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) "PulseOps-$Version-$Runtime"
if (Test-Path -LiteralPath $artifact) {
    throw 'The artifact directory already exists. Use a new release version or a clean output location.'
}

$solution = Join-Path $repo 'PulseOps.sln'
if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) { $solution = Join-Path $repo 'PulseOps.slnx' }
$server = Join-Path $repo 'src/PulseOps.Server/PulseOps.Server.csproj'
$sdk = Join-Path $repo 'src/PulseOps.Sdk.Blazor/PulseOps.Sdk.Blazor.csproj'
$setupFiles = @(
    'README.md', 'ARCHITECTURE.md', 'IMPLEMENTATION_STATUS.md',
    'global.json', 'Directory.Build.props', 'Directory.Packages.props',
    'docs/Operations.md', 'docs/PulseOps-Operator-Guide.docx', 'docs/Staging.md',
    'docs/Dependencies.md', 'docs/Performance.md', 'docs/SDK.md', 'docs/Security.md',
    'docs/Support.md', 'docs/Telemetry.md',
    'deployment/windows/PulseOps.ps1', 'deployment/windows/PulseOps.Tools.psm1',
    'deployment/windows/Invoke-StagingAcceptance.ps1', 'deployment/iis/Configure-Iis.ps1',
    'deployment/teamcity/README.md', 'deployment/teamcity/Mark-Release.ps1',
    'deployment/staging/StagingSmoke.cs', 'deployment/staging/packages.lock.json',
    'deployment/performance/Benchmark.cs', 'deployment/performance/Soak.cs',
    'deployment/performance/packages.lock.json', 'third_party/datacat/LICENSE',
    'third_party/datacat/NOTICE'
)
foreach ($relativePath in $setupFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $relativePath) -PathType Leaf)) {
        throw "Missing required release file '$relativePath'."
    }
}
$properties = @("-p:Version=$Version", '-p:ContinuousIntegrationBuild=true')
if ($Commit) {
    if ($Commit -notmatch '^[A-Fa-f0-9]{7,64}$') { throw 'Commit must be the hexadecimal Git revision.' }
    $properties += "-p:SourceRevisionId=$Commit"
}
$restore = @('-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-warnaserror:NU1900,NU1901,NU1902,NU1903,NU1904,NU1905')
if ($NuGetConfig) {
    $restore += @('--configfile', (Resolve-Path -LiteralPath $NuGetConfig).Path)
}
$restore += '--locked-mode'
[void] [IO.Directory]::CreateDirectory($artifact)

Push-Location $repo
try {
    Invoke-PulseProcess $dotnet (@('restore', $solution) + $restore)
    Invoke-PulseProcess $dotnet (@('build', $solution, '-c', 'Release', '--no-restore') + $properties)
    Invoke-PulseProcess $dotnet @('test', $solution, '-c', 'Release', '--no-build', '--no-restore',
        '--logger', 'trx', '--results-directory', (Join-Path $artifact 'tests'))
    Invoke-PulseProcess $dotnet (@('pack', $sdk, '-c', 'Release', '--no-build', '--no-restore',
        '-o', (Join-Path $artifact 'packages')) + $properties)
    $release = Join-Path $artifact 'release'
    Invoke-PulseProcess $dotnet (@('publish', $server, '-c', 'Release', '-r', $Runtime,
        '--self-contained', 'false', '--no-restore', '-o', (Join-Path $release 'server')) + $properties)
    $harnesses = @{
        smoke = 'deployment/staging/StagingSmoke.cs'
        soak = 'deployment/performance/Soak.cs'
    }
    foreach ($harness in $harnesses.GetEnumerator()) {
        $source = Join-Path $repo $harness.Value
        $destination = Join-Path $release "deployment/staging-tools/$($harness.Key)"
        Invoke-PulseProcess $dotnet (@('restore', $source) + $restore)
        Invoke-PulseProcess $dotnet @('publish', $source, '-c', 'Release', '--no-restore',
            '--self-contained', 'false', '-p:UseAppHost=false', '-p:DebugType=None', '-o', $destination)
        $name = [IO.Path]::GetFileNameWithoutExtension($source)
        foreach ($suffix in '.dll', '.deps.json', '.runtimeconfig.json') {
            if (-not (Test-Path -LiteralPath (Join-Path $destination "$name$suffix") -PathType Leaf)) {
                throw "Missing staging harness payload '$name$suffix'."
            }
        }
    }
    foreach ($relativePath in $setupFiles) {
        $destination = Join-Path $release $relativePath
        [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        Copy-Item -LiteralPath (Join-Path $repo $relativePath) -Destination $destination
    }
    foreach ($notice in 'server/ThirdParty/DataCat/LICENSE', 'server/ThirdParty/DataCat/NOTICE', 'server/wwwroot/fonts/OFL.txt') {
        if (-not (Test-Path -LiteralPath (Join-Path $release $notice) -PathType Leaf)) {
            throw "Missing published notice '$notice'."
        }
    }
    Write-PulseManifest -Directory $release -Version $Version -Runtime $Runtime -Commit $Commit -BuildNumber $BuildNumber
    [void] (Read-PulseManifest -Directory $release)
    $archive = Join-Path $artifact "PulseOps-$Version-$Runtime.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($release, $archive)
    (Get-FileHash -LiteralPath (Join-Path $release 'manifest.json') -Algorithm SHA256).Hash |
        Set-Content -LiteralPath (Join-Path $artifact 'manifest.sha256') -Encoding ascii
    Write-Host "Built release archive, manifest digest, test results and SDK package in $artifact"
} finally {
    Pop-Location
}
