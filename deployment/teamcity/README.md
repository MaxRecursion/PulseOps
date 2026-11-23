# TeamCity and Artifactory

Create a build configuration on a clean agent with an approved .NET SDK compatible with `global.json` and PowerShell 7.4 or newer. Provide an approved NuGet configuration, with package source mapping, an internal Artifactory restore source, and a reachable approved vulnerability data source. Keep restore credentials in the agent's configured credential provider. Treat missing audit data and NuGet advisories as build failures until reviewed. [NuGet configuration reference](https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file)

## Build and archive

Use a PowerShell file runner or a command-line step in the checkout directory. Set `env.PULSEOPS_VERSION` to the approved package version and optionally set `env.PULSEOPS_RESTORE_CONFIG` to the agent's private NuGet config path. TeamCity supplies `BUILD_NUMBER` and the Git VCS revision. For multiple VCS roots explicitly pass the revision of the PulseOps checkout as `-Commit`. [TeamCity build parameters](https://www.jetbrains.com/help/teamcity/predefined-build-parameters.html)

```powershell
pwsh -NoProfile -NonInteractive -File deployment/teamcity/Build.ps1 `
    -Version $env:PULSEOPS_VERSION -Runtime win-x64 -NuGetConfig $env:PULSEOPS_RESTORE_CONFIG
```

The script restores, checks NuGet advisories, builds, runs solution tests, packs the Blazor SDK, and publishes the Windows server with the bundled WASM assets. Review the TRX results. Configure TeamCity's XML Report Processing to import `artifacts/**/tests/*.trx` as VSTest results. Publish the zip, `manifest.sha256`, SDK packages and test results as TeamCity artifacts. For validation evidence, also archive the approved vulnerability scan, full dependency inventory, and artifact-signing or attestation record. Authorize the expected manifest digest from trusted build provenance before deployment. [TeamCity TRX import](https://www.jetbrains.com/help/teamcity/xml-report-processing.html)

The release ZIP contains the server under `server`, the setup and acceptance scripts under `deployment`, the operator guide at `docs/PulseOps-Operator-Guide.docx`, and the Markdown setup, recovery and staging guides. It includes the DataCat notices and the OFL notice for the portal fonts. The release manifest covers the documentation, scripts and staging harnesses along with the server.

The staging runner needs the .NET 10 runtime and HTTPS access to the staging installation. From the extracted release directory, load the scoped machine credential into the named environment variable and run the following command. The wrapper locates the compiled smoke and soak tools in `deployment/staging-tools`. Use a new evidence directory for each run. The source and package locks are included for independent rebuilds. [Staging scope setup, test gates and evidence](../../docs/Staging.md)

```powershell
./deployment/windows/Invoke-StagingAcceptance.ps1 `
    -BaseUrl 'https://pulseops-staging.internal.example' -Application staging-smoke `
    -Environment staging -TokenVariable PULSEOPS_STAGING_TOKEN `
    -ManifestPath ./manifest.json `
    -ResultsDirectory 'C:\PulseOpsEvidence\release-acceptance' -SoakMinutes 60
```

Run the opt-in browser suite as a separate release gate with the two isolated demo hosts and approved Chromium. Leave the local simulation data off the production host. [Browser test setup](../../docs/SDK.md)

## Promote the SDK

Use a separate manually authorized promotion configuration with an artifact dependency on the exact successful build. Configure an approved Artifactory NuGet destination and a restricted publisher identity. With NuGet 7.6 or newer, supply `NUGET_API_KEY` as a TeamCity Password environment parameter. Configure the destination according to Artifactory's repository authentication policy. Use an approved NuGet user configuration or credential provider for HTTP authentication as required. [NuGet secret settings](https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file) [Artifactory NuGet repositories](https://docs.jfrog.com/artifactory/docs/nuget-repositories)

```powershell
dotnet nuget push <approved-package.nupkg> --source <approved-artifactory-local-source>
```

Confirm that the exact approved SDK version restores in a clean consumer project through the internal Artifactory endpoint. Update dependency approvals before promoting package upgrades.

## Deploy the server

Create a separate authorized deployment configuration on the approved deployment agent or have an operator execute the deployment on the server. Supply the independently verified manifest digest and immutable archive. Follow the setup and deployment guide to initialize permissions and OIDC, stage the release, migrate the database, restart the chosen topology, and verify readiness. Keep runtime secrets on the host through the approved secrets process. [Windows and IIS setup](../../docs/Operations.md)

## Mark monitored application releases

After a monitored application deployment passes its own health checks, run the marker script from that application's pipeline. Provide the matching scoped machine credential as the TeamCity Password environment parameter `env.PULSEOPS_RELEASE_TOKEN`. A stable build ID makes retries idempotent. The PulseOps server supplies the deployment timestamp at acceptance.

```powershell
pwsh -NoProfile -NonInteractive -File deployment/teamcity/Mark-Release.ps1 `
    -PulseOpsOrigin 'https://pulseops.internal.example' `
    -Application 'finance-dashboard' -Environment 'production' `
    -Version $env:RELEASE_VERSION -BuildId $env:BUILD_NUMBER -Commit $env:BUILD_VCS_NUMBER
```

Use version and build identifiers formed from letters, digits, periods, underscores, and hyphens. Keep the build ID at 64 characters or fewer and the release version at 32 or fewer. The marker records a time correlation for investigation. The marker carries no report data or deployment secrets.
