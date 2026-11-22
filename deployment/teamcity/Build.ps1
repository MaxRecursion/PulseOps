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
$properties = @("-p:Version=$Version", '-p:ContinuousIntegrationBuild=true')
if ($Commit) {
    if ($Commit -notmatch '^[A-Fa-f0-9]{7,64}$') { throw 'Commit must be the hexadecimal Git revision.' }
    $properties += "-p:SourceRevisionId=$Commit"
}
$restore = @('-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-warnaserror:NU1900,NU1901,NU1902,NU1903,NU1904,NU1905')
if ($NuGetConfig) {
    $restore += @('--configfile', (Resolve-Path -LiteralPath $NuGetConfig).Path)
}
if (@(Get-ChildItem -LiteralPath $repo -Recurse -Filter packages.lock.json -File).Count -gt 0) {
    $restore += '--locked-mode'
}
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
    $setupFiles = @(
        'deployment/windows/PulseOps.ps1',
        'deployment/windows/PulseOps.Tools.psm1',
        'deployment/iis/Configure-Iis.ps1',
        'deployment/teamcity/README.md',
        'docs/Operations.md',
        'docs/SDK.md',
        'docs/Performance.md',
        'IMPLEMENTATION_STATUS.md'
    )
    foreach ($relativePath in $setupFiles) {
        $destination = Join-Path $release $relativePath
        [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        Copy-Item -LiteralPath (Join-Path $repo $relativePath) -Destination $destination
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
