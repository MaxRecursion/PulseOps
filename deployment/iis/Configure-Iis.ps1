#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Root = (Join-Path $env:ProgramData 'PulseOps'),
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [Parameter(Mandatory)]
    [ValidatePattern('^(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$')]
    [string] $DnsName,
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string] $CertificateThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this script with elevated Windows PowerShell 5.1.' }

$installRoot = (Resolve-Path -LiteralPath $Root).Path
$layout = Join-Path $installRoot '.pulseops-installation'
$marker = Join-Path $installRoot '.pulseops-iis'
if (-not (Test-Path -LiteralPath $layout -PathType Leaf)) { throw 'Initialize the PulseOps installation first.' }
if (Get-Service -Name 'PulseOps', 'PulseOpsWorker' -ErrorAction SilentlyContinue) {
    throw 'Remove the Windows Service registrations before configuring the IIS deployment.'
}

$source = Join-Path $installRoot "releases\$Version\server"
$config = Join-Path $installRoot 'appsettings.Production.json'
try {
    $settings = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    $embedded = $settings.PulseOps.PSObject.Properties['EmbeddedWorker']
    if (($embedded -and $embedded.Value -ne $true) -or $settings.PSObject.Properties['Kestrel']) {
        throw 'Invalid IIS settings.'
    }
} catch {
    throw 'Use the IIS private configuration from the setup guide. Embedded processing and IIS TLS are required.'
}

$certificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$CertificateThumbprint"
if (-not $certificate.HasPrivateKey -or $certificate.NotBefore -ge (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
    throw 'Install a valid site certificate with a private key in LocalMachine\My.'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'web.config') -PathType Leaf)) {
    throw 'Stage an approved PulseOps server release first.'
}

$assembly = Join-Path $env:windir 'System32\inetsrv\Microsoft.Web.Administration.dll'
$ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
if (-not (Test-Path -LiteralPath $assembly) -or -not (Test-Path -LiteralPath $ancm)) {
    throw 'Install IIS Management Scripts and Tools and the .NET 10 Hosting Bundle.'
}
Add-Type -Path $assembly

if (-not $PSCmdlet.ShouldProcess($DnsName, "Configure PulseOps IIS release $Version")) { return }
$lock = [IO.File]::Open((Join-Path $installRoot '.operations.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $manager = [Microsoft.Web.Administration.ServerManager]::new()
    try {
        $siteName = 'PulseOps'
        $pool = $manager.ApplicationPools[$siteName]
        $site = $manager.Sites[$siteName]
        $bound = "*:443:${DnsName}"
        if ($site -or $pool) {
            if (-not (Test-Path -LiteralPath $marker) -or -not $site -or -not $pool) {
                throw 'The PulseOps site or pool already exists outside this installation.'
            }
            if ($site.State -ne 'Stopped' -or $pool.State -ne 'Stopped') { throw 'Stop both the site and pool first.' }
            if ($site.Bindings.Count -ne 1 -or $site.Bindings[0].BindingInformation -ne $bound -or
                [BitConverter]::ToString($site.Bindings[0].CertificateHash).Replace('-', '') -ne $CertificateThumbprint) {
                throw 'Adjust DNS and TLS bindings through approved IIS administration before changing the release.'
            }
        } else {
            foreach ($other in $manager.Sites) {
                if ($other.Bindings | Where-Object { $_.Protocol -eq 'https' -and $_.Host -eq $DnsName -and $_.EndPoint.Port -eq 443 }) {
                    throw 'Another site owns that HTTPS hostname and port.'
                }
            }
        }

        $iisReleases = Join-Path $installRoot 'iis'
        [void] [IO.Directory]::CreateDirectory($iisReleases)
        $target = Join-Path $iisReleases $Version
        if (-not (Test-Path -LiteralPath $target)) {
            $stage = Join-Path $iisReleases ('.staging-' + [Guid]::NewGuid().ToString('N'))
            try {
                Copy-Item -LiteralPath $source -Destination $stage -Recurse
                $webConfig = Join-Path $stage 'web.config'
                $xml = [xml] (Get-Content -LiteralPath $webConfig -Raw)
                $node = $xml.SelectSingleNode('//aspNetCore')
                if (-not $node) { throw 'Published ASP.NET Core web.config is missing its application handler.' }
                $node.SetAttribute('arguments', ($node.GetAttribute('arguments') + ' --config "' + $config +
                        '" --contentRoot "' + $target + '"').Trim())
                $xml.Save($webConfig)
                [IO.Directory]::Move($stage, $target)
            } finally {
                if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Force -Recurse }
            }
        }

        if (-not $site) {
            $pool = $manager.ApplicationPools.Add($siteName)
            $site = $manager.Sites.Add($siteName, $bound, $target, $certificate.GetCertHash(), 'My',
                [Microsoft.Web.Administration.SslFlags]::Sni)
        }
        $pool.ManagedRuntimeVersion = ''
        $pool.AutoStart = $false
        $pool.Enable32BitAppOnWin64 = $false
        $pool.StartMode = [Microsoft.Web.Administration.StartMode]::AlwaysRunning
        $pool.ProcessModel.IdleTimeout = [TimeSpan]::Zero
        $pool.ProcessModel.MaxProcesses = 1
        $pool.ProcessModel.IdentityType = [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity
        $pool.Recycling.DisallowOverlappingRotation = $true
        $site.ServerAutoStart = $false
        $application = $site.Applications['/']
        $application.ApplicationPoolName = $siteName
        $application['preloadEnabled'] = $true
        $application.VirtualDirectories['/'].PhysicalPath = $target
        $hostConfig = $manager.GetApplicationHostConfiguration()
        $filtering = $hostConfig.GetSection('system.webServer/security/requestFiltering', $siteName)
        # IIS is only a coarse outer bound. ASP.NET Core enforces the 128 KiB body limit and answers 413, as on Kestrel;
        # IIS request filtering at that size would answer 404.13 instead.
        $filtering.GetChildElement('requestLimits')['maxAllowedContentLength'] = [uint32] 1048576
        try {
            # IIS 10 version 1709 (Windows Server 2019) and later. Matches Kestrel, which sends no Server header.
            $filtering['removeServerHeader'] = $true
        } catch {
            Write-Warning 'This IIS version cannot remove the Server header (IIS 10 version 1709 or later is required). The staging smoke reports the header.'
        }
        $anonymous = $hostConfig.GetSection('system.webServer/security/authentication/anonymousAuthentication', $siteName)
        $anonymous['enabled'] = $true
        $anonymous['userName'] = ''
        $manager.CommitChanges()
    } finally {
        $manager.Dispose()
    }

    Set-Content -LiteralPath $marker -Value 'PulseOps' -Encoding ascii
    $identity = 'IIS AppPool\PulseOps'
    $icacls = Join-Path $env:windir 'System32\icacls.exe'
    foreach ($permission in @(
        @{ path = $installRoot; access = 'RX' },
        @{ path = $iisReleases; access = '(OI)(CI)(RX)' },
        @{ path = (Join-Path $installRoot 'data'); access = '(OI)(CI)(M)' },
        @{ path = (Join-Path $installRoot 'keys'); access = '(OI)(CI)(M)' },
        @{ path = $config; access = 'R' }
    )) {
        & $icacls $permission.path '/grant:r' "${identity}:$($permission.access)"
        if ($LASTEXITCODE -ne 0) { throw 'Could not grant the dedicated application pool its runtime permissions.' }
    }
    Write-Host 'The PulseOps site and pool are stopped. Migrate, start the pool and site, and verify using the setup guide.'
} finally {
    $lock.Dispose()
}
