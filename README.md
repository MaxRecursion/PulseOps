# PulseOps

PulseOps is a self-hosted observability workbench for Blazor WebAssembly applications and their dependencies. It combines browser startup monitoring, request and business-operation telemetry, session and trace drill-downs, and incident triage. The normal deployment runs one ASP.NET Core process and a SQLite file on the same Windows server.

## Try the demo

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) version 10.0.404 or a later stable .NET 10 feature band. In the repository root, run:

```sh
dotnet restore PulseOps.sln --locked-mode
dotnet build PulseOps.sln --no-restore
dotnet test PulseOps.sln --no-build
```

Start the collector and portal on the loopback interface:

```sh
ASPNETCORE_ENVIRONMENT=Development PulseOps__Demo=true \
  dotnet run --project src/PulseOps.Server --no-build --no-launch-profile -- --urls http://localhost:5080
```

In PowerShell, set the environment variables and run the same collector:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:PulseOps__Demo = 'true'
dotnet run --project src/PulseOps.Server --no-build --no-launch-profile -- --urls http://localhost:5080
```

In a second terminal, start the instrumented Blazor finance workspace and mock REST backend:

```sh
dotnet run --project samples/PulseOps.Demo.Api --no-build --no-launch-profile -- --urls http://localhost:5091
```

1. Open [PulseOps](http://localhost:5080/). The dashboard loads labeled sample data while processing catches up.
2. Open the [Finance demo](http://localhost:5091/) and generate a successful report, then choose **HTTP 503** and generate another.
3. In PulseOps, select **Finance Demo Application** and the **demo** environment. Inspect the incident and follow the evidence into the session and trace. Acknowledge or resolve the incident from its detail.
4. Trigger a [pre-runtime startup failure](http://localhost:5091/boot-failure.html) and inspect startup events. Demo anonymous startup observations are treated as untrusted evidence.
5. Stop the PulseOps collector and run another successful finance report to verify the application's fail-open behavior.

## Set up Windows Server

Follow the [Windows deployment and recovery guide](docs/Operations.md). Obtain the .NET 10 ASP.NET Core runtime, an HTTPS certificate, an enterprise OIDC client and a local NTFS data disk. Use the PowerShell tooling to stage a verified artifact, place private configuration on the host, migrate SQLite and install the services. The embedded worker is the default. An optional second service executes the same binary with `--worker` on the same host.

Use [Blazor SDK integration](docs/SDK.md) to register approved route aliases, deploy an authenticated same-origin telemetry relay and add monitoring to existing clients. The guide covers SAP calls as observed from the browser. For release markers and package distribution see [TeamCity and Artifactory](deployment/teamcity/README.md).

- [Architecture and DataCat parity](ARCHITECTURE.md)
- [Telemetry and storage contract](docs/Telemetry.md)
- [Support investigation guide](docs/Support.md)
- [Security threat model and acceptance checklist](docs/Security.md)
- [Dependency inventory](docs/Dependencies.md)
- [Local performance benchmark](docs/Performance.md)
- [Verification, limitations and rollout status](IMPLEMENTATION_STATUS.md)

Local demo credentials and generated data are enabled only by explicit Development configuration. Production rollout requires environment validation and capacity evidence described in the implementation status.
