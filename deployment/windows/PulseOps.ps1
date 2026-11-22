#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('Initialize', 'Validate', 'Stage', 'Deploy', 'Start', 'Stop', 'Remove', 'Verify', 'Migrate', 'Backup', 'VerifyBackup', 'Restore', 'Rollback')]
    [string] $Action,
    [string] $Root,
    [string] $ReleaseDirectory,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string] $ManifestSha256,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [uri] $HealthUrl,
    [string] $SnapshotPath,
    [switch] $DeferStart,
    [ValidateRange(5, 300)]
    [int] $HealthWaitSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PulseOps.Tools.psm1')

function Assert-HealthUrl {
    if (-not $HealthUrl -or -not $HealthUrl.IsAbsoluteUri -or $HealthUrl.Scheme -ne 'https' -or
        $HealthUrl.AbsolutePath -ne '/' -or $HealthUrl.Query -or $HealthUrl.Fragment -or $HealthUrl.UserInfo) {
        throw 'Pass the trusted HTTPS site origin as -HealthUrl.'
    }
}

if ($Action -eq 'Verify') {
    Assert-HealthUrl
    Test-PulseHealth $HealthUrl $HealthWaitSeconds
    Write-Host 'PulseOps liveness and readiness passed.'
    return
}

if (-not $IsWindows -or -not [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this action in elevated PowerShell 7 on the Windows Server.'
}

if (-not $Root) { $Root = Join-Path $env:ProgramData 'PulseOps' }
if ($Root -notmatch '^[A-Za-z]:\\' -or $Root.Contains('"')) { throw 'Install to an absolute local NTFS path.' }
$installRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot("$installRoot\"))
if ($installRoot.Length -lt 4 -or $volume.DriveType -ne 'Fixed' -or $volume.DriveFormat -ne 'NTFS') {
    throw 'Install beneath a local fixed NTFS drive.'
}
for ($path = $installRoot; $path; $path = Split-Path -Parent $path) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Installation path contains a reparse point at '$path'."
    }
}

$marker = Join-Path $installRoot '.pulseops-installation'
$iisMarker = Join-Path $installRoot '.pulseops-iis'
$configPath = Join-Path $installRoot 'appsettings.Production.json'
$releases = Join-Path $installRoot 'releases'
$data = Join-Path $installRoot 'data'
$db = Join-Path $data 'pulseops.db'
$keys = Join-Path $installRoot 'keys'
$backups = Join-Path $installRoot 'backups'
$serverName = 'PulseOps'
$workerName = 'PulseOpsWorker'
$sc = Join-Path $env:windir 'System32/sc.exe'
$icacls = Join-Path $env:windir 'System32/icacls.exe'

function Assert-Runtime {
    $dotnet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw 'Install the native .NET 10 ASP.NET Core runtime from the approved feed.'
    }
    $runtimes = & $dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate the .NET runtimes.' }
    $minimum = [version] '10.0.15'
    foreach ($framework in @('Microsoft.AspNetCore.App', 'Microsoft.NETCore.App')) {
        $pattern = '^' + [regex]::Escape($framework) + ' (?<version>10\.\d+\.\d+) \['
        $supported = $runtimes | Where-Object { $_ -match $pattern -and [version] $Matches.version -ge $minimum }
        if (-not $supported) { throw "Install native $framework 10.0.15 or a later .NET 10 servicing release." }
    }
}

function Read-Config {
    try {
        $configuration = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
        if (-not $configuration.PulseOps) { throw 'Missing settings.' }
    } catch {
        throw "Create valid private JSON configuration at '$configPath'."
    }

    foreach ($name in @('PulseOps__DatabasePath', 'PulseOps__DataProtectionPath', 'PulseOps__EmbeddedWorker',
            'ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT')) {
        if ([Environment]::GetEnvironmentVariable($name, 'Machine')) {
            throw "The installer expects '$name' to be controlled by the deployment. Remove the conflicting machine setting."
        }
    }
    if ($configuration.PulseOps.DatabasePath -ne $db -or $configuration.PulseOps.DataProtectionPath -ne $keys) {
        throw "Use '$db' and '$keys' for the installer-managed paths."
    }
    foreach ($path in @($data, $keys, $backups, $releases, $db, $configPath)) {
        $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Managed path '$path' must be a local file or directory."
        }
    }
    if ($null -ne $configuration.PulseOps.EmbeddedWorker -and $configuration.PulseOps.EmbeddedWorker -isnot [bool]) {
        throw 'PulseOps.EmbeddedWorker must be a JSON boolean.'
    }
    return $configuration
}

function Get-ServiceRelease {
    param([string] $Name)

    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'"
    if (-not $service) { return }
    $rootPattern = [regex]::Escape("$releases\")
    $configPattern = [regex]::Escape('"' + $configPath + '"')
    if ($service.PathName -notmatch ('^"' + $rootPattern +
            '(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)\\server\\PulseOps\.Server\.exe" --config ' +
            $configPattern + '( --worker)?$')) {
        throw "The service '$Name' already exists with a different command or installation directory."
    }
    return $Matches.version
}

function Stop-PulseServices {
    $managed = @($serverName, $workerName) | Where-Object { Get-ServiceRelease $_ }
    foreach ($name in $managed) {
        $service = Get-Service -Name $name
        if ($service.Status -ne 'Stopped') {
            $service.Stop()
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45))
        }
    }
}

function Disable-PulseServices {
    $managed = @($serverName, $workerName) | Where-Object { Get-ServiceRelease $_ }
    foreach ($name in $managed) { Invoke-PulseProcess $sc @('config', $name, 'start=', 'disabled') }
}

function Assert-PulseServicesStopped {
    foreach ($name in @($serverName, $workerName)) {
        if ((Get-ServiceRelease $name) -and (Get-Service -Name $name).Status -ne 'Stopped') {
            throw "Stop '$name' before database maintenance."
        }
    }
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='PulseOps.Server.exe'")) {
        if ($process.ExecutablePath -and $process.ExecutablePath.StartsWith("$releases\", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Stop PulseOps process $($process.ProcessId) before database maintenance."
        }
    }
    if (Test-Path -LiteralPath $iisMarker) {
        $appcmd = Join-Path $env:windir 'System32/inetsrv/appcmd.exe'
        $pools = & $appcmd list apppool PulseOps /text:state
        if ($LASTEXITCODE -ne 0 -or ($pools -join '').Trim() -ne 'Stopped') {
            throw 'Stop the PulseOps IIS application pool before database maintenance.'
        }
        $processes = & $appcmd list wp /apppool.name:PulseOps
        if ($LASTEXITCODE -ne 0 -or $processes) { throw 'Wait for the PulseOps IIS worker process to exit.' }
    }
}

function Get-ReleaseExecutable {
    param([Parameter(Mandatory)] [string] $ReleaseVersion)

    $directory = Join-Path $releases $ReleaseVersion
    $manifest = Read-PulseManifest $directory
    if ($manifest.version -ne $ReleaseVersion) { throw 'Staged release version mismatch.' }
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($manifest.runtime -ne "win-$architecture") { throw "This system requires a win-$architecture artifact." }
    return [IO.Path]::GetFullPath((Join-Path $directory 'server/PulseOps.Server.exe'))
}

function Add-StagedRelease {
    if (-not $ReleaseDirectory -or -not $ManifestSha256) {
        throw 'Pass the extracted release directory and its trusted -ManifestSha256.'
    }

    $source = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
    $manifest = Read-PulseManifest $source $ManifestSha256
    $destination = Join-Path $releases $manifest.version
    if (Test-Path -LiteralPath $destination) {
        [void] (Read-PulseManifest $destination $ManifestSha256)
    } else {
        $temp = Join-Path $releases ('.staging-' + [Guid]::NewGuid().ToString('N'))
        [void] [IO.Directory]::CreateDirectory($temp)
        try {
            Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $temp -Recurse
            [void] (Read-PulseManifest $temp $ManifestSha256)
            [IO.Directory]::Move($temp, $destination)
        } finally {
            if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
        }
    }
    return $manifest.version
}

function Invoke-HostCommand {
    param([string] $Executable, [string[]] $Command)

    Invoke-PulseProcess -FilePath $Executable -Arguments (@('--config', $configPath) + $Command) -ProductionHost
}

function Set-PulseService {
    param([string] $Name, [string] $Executable, [switch] $Worker)

    $identity = "NT SERVICE\$Name"
    $command = '"' + $Executable + '" --config "' + $configPath + '"'
    if ($Worker) { $command += ' --worker' }

    if (Get-ServiceRelease $Name) {
        Invoke-PulseProcess $sc @('config', $Name, 'binpath=', $command, 'start=', 'disabled',
            'obj=', $identity, 'password=', '')
    } else {
        Invoke-PulseProcess $sc @('create', $Name, 'binpath=', $command, 'start=', 'disabled',
            'obj=', $identity, 'password=', '')
    }
    Invoke-PulseProcess $sc @('failure', $Name, 'reset=', '86400', 'actions=', 'restart/5000/restart/30000/restart/120000')
    Invoke-PulseProcess $sc @('failureflag', $Name, '1')
}

function Grant-PulseServiceAccess {
    param([string] $Name)

    $identity = "NT SERVICE\$Name"
    Invoke-PulseProcess $icacls @($installRoot, '/grant:r', "${identity}:(RX)")
    Invoke-PulseProcess $icacls @($releases, '/grant:r', "${identity}:(OI)(CI)(RX)")
    Invoke-PulseProcess $icacls @($data, '/grant:r', "${identity}:(OI)(CI)(M)")
    Invoke-PulseProcess $icacls @($keys, '/grant:r', "${identity}:(OI)(CI)(M)")
    Invoke-PulseProcess $icacls @($configPath, '/grant:r', "${identity}:(R)")
}

function Start-PulseServices {
    param([bool] $Detached)

    $webVersion = Get-ServiceRelease $serverName
    if (-not $webVersion) { throw 'Deploy an application release first.' }
    if ($Detached -and (Get-ServiceRelease $workerName) -ne $webVersion) {
        throw 'Deploy matching releases of the server and standalone worker first.'
    }
    $services = if ($Detached) { @($workerName, $serverName) } else { @($serverName) }

    try {
        foreach ($name in $services) {
            Grant-PulseServiceAccess $name
            Invoke-PulseProcess $sc @('config', $name, 'start=', 'demand')
            Start-Service -Name $name
            (Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(45))
        }
        Test-PulseHealth $HealthUrl $HealthWaitSeconds
        foreach ($name in $services) { Invoke-PulseProcess $sc @('config', $name, 'start=', 'delayed-auto') }
    } catch {
        Stop-PulseServices
        Disable-PulseServices
        throw
    }
}

function Get-SnapshotPath {
    param([string] $Prefix)

    $timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    return Join-Path $backups "$Prefix-$timestamp-$([Guid]::NewGuid().ToString('N').Substring(0, 8)).db"
}

function Backup-Database {
    param([string] $Executable, [string] $Destination)
    if (Test-Path -LiteralPath $Destination) { throw "Snapshot already exists at '$Destination'." }
    $staging = "$Destination.partial-$([Guid]::NewGuid().ToString('N'))"
    try {
        Invoke-HostCommand $Executable @('--backup', $staging)
        Invoke-HostCommand $Executable @('--verify-backup', $staging)
        [IO.File]::Move($staging, $Destination)
    } finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Force }
    }
    Write-Host "Verified SQLite backup at $Destination"
}

function Backup-LiveDatabase {
    param([string] $Executable, [string] $Prefix)

    $destination = Get-SnapshotPath $Prefix
    try {
        Backup-Database $Executable $destination
        return
    } catch {
        Write-Warning 'The preferred release could not back up the live database. Checking other trusted staged releases.'
    }

    $alternatives = Get-ChildItem -LiteralPath $releases -Directory | Sort-Object LastWriteTimeUtc -Descending
    foreach ($candidate in $alternatives) {
        if ($candidate.Name -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') { continue }
        try {
            $alternative = Get-ReleaseExecutable $candidate.Name
            if ($alternative -eq $Executable) { continue }
            Backup-Database $alternative $destination
            return
        } catch {
            Write-Warning "Staged release '$($candidate.Name)' could not create a verified backup."
        }
    }
    throw 'No staged release could create a verified pre-change snapshot. The live database was preserved.'
}

if ($DeferStart -and $Action -ne 'Deploy') { throw '-DeferStart is available only for Deploy.' }
if ($Action -in @('Rollback', 'Start') -or ($Action -eq 'Deploy' -and -not $DeferStart)) { Assert-HealthUrl }
if ($Action -in @('Migrate', 'Rollback') -and -not $Version) { throw 'Specify a staged -Version.' }
if ($Action -in @('VerifyBackup', 'Restore') -and -not $SnapshotPath) { throw 'Specify -SnapshotPath.' }

if ($Action -eq 'Initialize') {
    if (Test-Path -LiteralPath $marker) {
        Write-Host "PulseOps already initialized at $installRoot"
        return
    }
    if (Test-Path -LiteralPath $installRoot) { throw 'Use a new installation directory.' }
    if ($PSCmdlet.ShouldProcess($installRoot, 'Initialize private PulseOps directories')) {
        [void] [IO.Directory]::CreateDirectory($installRoot)
        Invoke-PulseProcess $icacls @($installRoot, '/grant:r', '*S-1-5-18:(OI)(CI)(F)', '*S-1-5-32-544:(OI)(CI)(F)', '/inheritance:r')
        foreach ($path in @($data, $keys, $backups, $releases)) { [void] [IO.Directory]::CreateDirectory($path) }
        Set-Content -LiteralPath $marker -Value 'PulseOps installation layout 1' -Encoding ascii
        Write-Host "Configure $configPath with database '$db' and Data Protection directory '$keys'."
    }
    return
}

if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Initialize the PulseOps installation first.' }
if ($Action -in @('Deploy', 'Rollback', 'Start') -and (Test-Path -LiteralPath $iisMarker)) {
    throw 'This installation uses IIS. Follow the IIS release procedure.'
}
if (-not $PSCmdlet.ShouldProcess($installRoot, $Action)) { return }

$lock = [IO.File]::Open((Join-Path $installRoot '.operations.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    if ($Action -eq 'Stop') { Stop-PulseServices; return }
    if ($Action -eq 'Remove') {
        Stop-PulseServices
        foreach ($name in @($serverName, $workerName)) {
            if (Get-ServiceRelease $name) { Invoke-PulseProcess $sc @('delete', $name) }
        }
        Write-Host "Removed service registrations. Configuration, keys, data and releases remain under $installRoot."
        return
    }

    Assert-Runtime
    if ($Action -eq 'Stage') {
        $staged = Add-StagedRelease
        [void] (Get-ReleaseExecutable $staged)
        Write-Host "Staged PulseOps release $staged."
        return
    }

    $configuration = Read-Config
    $detached = $configuration.PulseOps.EmbeddedWorker -eq $false
    if ($Action -eq 'Validate') {
        Write-Host 'Installation paths, configuration layout, and .NET runtime checks passed.'
        return
    }
    if ($Action -eq 'Start') { Start-PulseServices $detached; return }

    $current = Get-ServiceRelease $serverName
    [void] (Get-ServiceRelease $workerName)
    if ($Action -eq 'Deploy') { $Version = Add-StagedRelease }
    $target = if ($Version) { $Version } else { $current }
    if (-not $target) { throw 'No active service release. Specify a staged -Version.' }
    $exe = Get-ReleaseExecutable $target

    $snapshot = if ($SnapshotPath) { [IO.Path]::GetFullPath($SnapshotPath) }
    switch ($Action) {
        'Backup' {
            if (-not $snapshot) { $snapshot = Get-SnapshotPath 'manual' }
            Backup-Database $exe $snapshot
        }
        'VerifyBackup' { Invoke-HostCommand $exe @('--verify-backup', $snapshot) }
        'Migrate' {
            Assert-PulseServicesStopped
            Disable-PulseServices
            Invoke-HostCommand $exe @('--migrate')
        }
        'Restore' {
            Assert-PulseServicesStopped
            Invoke-HostCommand $exe @('--verify-backup', $snapshot)
            Disable-PulseServices
            if (Test-Path -LiteralPath $db) { Backup-LiveDatabase $exe 'before-restore' }
            Invoke-HostCommand $exe @('--restore', $snapshot)
        }
        { $_ -in @('Deploy', 'Rollback') } {
            if ($Action -eq 'Rollback' -and $snapshot) { Invoke-HostCommand $exe @('--verify-backup', $snapshot) }
            Stop-PulseServices
            Assert-PulseServicesStopped
            Disable-PulseServices
            if (Test-Path -LiteralPath $db) {
                $backupExe = if ($current) { Get-ReleaseExecutable $current } else { $exe }
                Backup-LiveDatabase $backupExe 'before-release'
            }
            if ($Action -eq 'Rollback' -and $snapshot) { Invoke-HostCommand $exe @('--restore', $snapshot) }
            Invoke-HostCommand $exe @('--migrate')

            if ($detached) {
                Set-PulseService $workerName $exe -Worker
            } elseif (Get-ServiceRelease $workerName) {
                Invoke-PulseProcess $sc @('config', $workerName, 'start=', 'disabled')
            }
            Set-PulseService $serverName $exe
            if ($DeferStart) {
                Write-Host "PulseOps release $target is registered and disabled. Run Start once host prerequisites are complete."
                return
            }
            Start-PulseServices $detached
            Write-Host "PulseOps release $target is ready."
        }
    }
} finally {
    $lock.Dispose()
}
