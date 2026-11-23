Set-StrictMode -Version Latest

function Invoke-PulseProcess {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [switch] $ProductionHost,
        [ValidateRange(1, 86400)] [int] $TimeoutSeconds
    )

    $processInfo = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $processInfo.UseShellExecute = $false
    $processInfo.WorkingDirectory = (Get-Location).ProviderPath
    foreach ($argument in $Arguments) { $processInfo.ArgumentList.Add($argument) }

    if ($ProductionHost) {
        # Match the service's machine settings, without importing an operator's local development settings.
        $machine = [Environment]::GetEnvironmentVariables('Machine')
        foreach ($name in @($processInfo.Environment.Keys)) {
            if ($name -match '^(PulseOps__|Kestrel__|ASPNETCORE_|DOTNET_|AllowedHosts$)') {
                [void] $processInfo.Environment.Remove($name)
            }
        }
        foreach ($name in $machine.Keys) {
            if ($name -match '^(PulseOps__|Kestrel__|ASPNETCORE_|DOTNET_|AllowedHosts$)') {
                $processInfo.Environment[$name] = $machine[$name]
            }
        }
        $processInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
        $processInfo.Environment['DOTNET_ENVIRONMENT'] = 'Production'
        $processInfo.WorkingDirectory = Split-Path -Parent $FilePath
    }

    $process = [Diagnostics.Process]::Start($processInfo)
    try {
        if (-not $TimeoutSeconds) {
            $process.WaitForExit()
        } elseif (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch {
                if (-not $process.HasExited) {
                    throw [TimeoutException]::new("'$(Split-Path -Leaf $FilePath)' timed out and could not be stopped. Confirm it has exited before retrying.")
                }
            }
            $exited = $process.WaitForExit(5000)
            $outcome = if ($exited) { 'The process was stopped.' } else { 'Confirm the process has exited before retrying.' }
            throw [TimeoutException]::new("'$(Split-Path -Leaf $FilePath)' exceeded $TimeoutSeconds seconds. $outcome")
        }
        if ($process.ExitCode -ne 0) {
            throw "'$(Split-Path -Leaf $FilePath)' exited with code $($process.ExitCode)."
        }
    } finally {
        $process.Dispose()
    }
}

function Write-PulseManifest {
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $Runtime,
        [string] $Commit,
        [string] $BuildNumber
    )

    $root = (Resolve-Path -LiteralPath $Directory).Path
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [ordered] @{
            path = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $manifest = [ordered] @{
        version = $Version
        runtime = $Runtime
        commit = $Commit
        buildNumber = $BuildNumber
        builtUtc = [DateTime]::UtcNow.ToString('O')
        files = $files
    }

    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'manifest.json') -Encoding utf8
}

function Read-PulseManifest {
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [string] $ExpectedHash
    )

    $root = (Resolve-Path -LiteralPath $Directory).Path
    $manifestPath = Join-Path $root 'manifest.json'
    $items = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if (((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })) {
        throw 'The release must contain ordinary files and directories.'
    }

    if ($ExpectedHash -and (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $ExpectedHash) {
        throw 'The release manifest does not match the trusted SHA-256.'
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$' -or
        $manifest.runtime -notin @('win-x64', 'win-arm64')) {
        throw 'Invalid release version or runtime identifier.'
    }

    $hashes = @{}
    $releasePaths = @(
        '(?:server|deployment/(?:windows|iis)|docs)/(?:[^/:\\]+/)*[^/:\\]+'
        'deployment/teamcity/(?:README\.md|Mark-Release\.ps1)'
        'deployment/staging/(?:StagingSmoke\.cs|packages\.lock\.json)'
        'deployment/performance/(?:(?:Benchmark|Soak)\.cs|packages\.lock\.json)'
        'deployment/staging-tools/(?:smoke/StagingSmoke|soak/Soak)\.(?:dll|deps\.json|runtimeconfig\.json)'
        'third_party/datacat/(?:LICENSE|NOTICE)'
        '(?:README|ARCHITECTURE|IMPLEMENTATION_STATUS)\.md'
        'global\.json|Directory\.(?:Build|Packages)\.props'
    )
    $allowedPath = '^(?:' + ($releasePaths -join '|') + ')$'
    foreach ($entry in $manifest.files) {
        if ($entry.path -notmatch $allowedPath -or
            $entry.path.Split('/') -contains '..' -or $entry.path.Split('/') -contains '.' -or
            $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $hashes.ContainsKey($entry.path)) {
            throw 'Invalid path, duplicate file, or invalid file digest in manifest.'
        }
        $hashes.Add($entry.path, $entry.sha256)
    }

    $payload = @($items | Where-Object { -not $_.PSIsContainer -and $_.FullName -ne $manifestPath })
    if ($payload.Count -ne $hashes.Count -or -not $hashes.ContainsKey('server/PulseOps.Server.exe')) {
        throw 'The release does not contain the exact server payload listed in the manifest.'
    }
    foreach ($file in $payload) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        if (-not $hashes.ContainsKey($relative) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $hashes[$relative]) {
            throw "Incorrect release file hash for '$relative'."
        }
    }
    return $manifest
}

function Test-PulseHealth {
    param(
        [Parameter(Mandatory)] [uri] $BaseUri,
        [int] $WaitSeconds = 60
    )

    if (-not $BaseUri.IsAbsoluteUri -or $BaseUri.Scheme -ne 'https' -or $BaseUri.AbsolutePath -ne '/' -or
        $BaseUri.Query -or $BaseUri.Fragment -or $BaseUri.UserInfo) {
        throw 'Pass the trusted HTTPS site origin as -HealthUrl.'
    }

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(5)
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    $last = 'No response'
    try {
        do {
            foreach ($path in @('/health/live', '/health/ready')) {
                try {
                    $response = $client.GetAsync([uri]::new($BaseUri, $path)).GetAwaiter().GetResult()
                    try {
                        $last = "$path HTTP $([int] $response.StatusCode)"
                        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { break }
                    } finally {
                        $response.Dispose()
                    }
                } catch {
                    $last = "$path $($_.Exception.GetBaseException().Message)"
                    break
                }
                if ($path -eq '/health/ready') { return }
            }
            if ([DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2 }
        } until ([DateTime]::UtcNow -ge $deadline)
        throw "PulseOps HTTPS health probe failed. $last"
    } finally {
        $client.Dispose()
    }
}

Export-ModuleMember -Function Invoke-PulseProcess, Write-PulseManifest, Read-PulseManifest, Test-PulseHealth
