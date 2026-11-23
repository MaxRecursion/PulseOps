# Dependency inventory

The central package file pins direct NuGet versions. Committed package locks record the resolved package versions and content hashes, including transitive dependencies. Generate the release inventory after restore using `dotnet package list PulseOps.sln --include-transitive`. Pair that inventory with the Windows .NET runtime and platform components recorded in the deployment manifest. See [Directory.Packages.props](../Directory.Packages.props).

| Dependency | Scope | License | Purpose and alternative |
| --- | --- | --- | --- |
| .NET and ASP.NET Core shared frameworks, Microsoft.Extensions packages | Server, worker, SDK and tooling | MIT for .NET and the Microsoft libraries | Host, configuration, diagnostics, HTTP, logging, auth, storage integration and dependency injection. Framework-native implementation. |
| Microsoft.AspNetCore.Components.Web and Microsoft.AspNetCore.Components.WebAssembly packages | SDK, portal, demo | MIT | Razor and Blazor WebAssembly runtime. The server-side package hosts static client assets. |
| Microsoft.AspNetCore.Authentication.OpenIdConnect and Microsoft/System IdentityModel packages | Server | MIT | Confidential OIDC flow and token/claim validation. Organization-provided IdP with standard OIDC. |
| Microsoft.AspNetCore.SignalR.Client | Portal | MIT | Live refresh. Scoped polling is available through the query API. |
| Microsoft.Extensions.Hosting.WindowsServices | Windows server and worker | MIT | SCM lifecycle integration. IIS with the Hosting Bundle is the alternative web-host option. |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core | Server and worker | MIT | Parameterized ADO.NET SQLite access. SQLite's C API is an alternative, with added interop and maintenance. |
| SQLitePCLRaw bundle, config, core and provider | Server and worker | Apache-2.0 | Native SQLite bridge and Native NuGet packaging. Using the OS-supplied SQLite library would require separate deployment and native-version validation. |
| SQLite native library in the SQLite NuGet package | Server and worker | SQLite public domain | Local transactional database with WAL and online backups. No separate DB service. [SQLite copyright](https://sqlite.org/copyright.html) |
| NUnit and NUnit3TestAdapter | Test | MIT | Required test framework and test runner integration. |
| Microsoft.NET.Test.Sdk | Test | MIT | .NET test runner. |
| bUnit | Test | MIT | Exercises portal Razor components with test-controlled dependencies. Browser tests cover the complete hosted UI separately. [bUnit license](https://github.com/bUnit-dev/bUnit/blob/main/LICENSE) |
| Microsoft.Playwright and browser binaries, where browser scenarios are enabled | Test | MIT for Microsoft.Playwright. Review driver and browser notices separately. | Browser automation used for end-to-end validation. Headless browsers are test tooling. [Playwright .NET license](https://github.com/microsoft/playwright-dotnet/blob/main/LICENSE) |

Deployment and CI use PowerShell 7. Optional IIS automation uses built-in Windows/IIS management APIs and the Microsoft ASP.NET Core Hosting Bundle. IIS, Windows Server, TeamCity, Artifactory, and the identity provider are environment components under their own licensing policies. Required third-party source notices are maintained in the restricted source distribution and release metadata. The portal uses original SVG and CSS visualizations. It self-hosts the Geist and Geist Mono Latin subsets (`src/PulseOps.Portal/wwwroot/fonts`) under the SIL Open Font License 1.1, whose text ships beside the font files and in every published portal; no font is loaded from an external service. Removing the two `@font-face` rules falls back to system fonts.
