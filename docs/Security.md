# Security and threat model

PulseOps receives operational measurements from untrusted browsers and server-side producers. The portal exposes scoped diagnostic information to approved support users and stores it in a local SQLite database. Threat boundaries include the browser, the monitored application's authenticated relay, PulseOps HTTP endpoints, the OIDC provider, and the Windows host with process identity and local storage.

| Boundary and threat | Implemented control | Environment validation |
| --- | --- | --- |
| Browser sends sensitive fields or fabricated telemetry | SDK uses configured aliases and produces structured events. Server rejects unmapped fields, invalid identifiers, disallowed names/categories, invalid timestamp ranges and malformed trace identifiers. | Review every custom alias and category. Verify tokens, URLs, identifiers, finance values and raw content stay out of telemetry and front-door logs. |
| Browser impersonates another app/environment | Ordinary ingest checks the unique server-scoped bearer credential against the supplied scope. Startup collection requires a configured exact origin and a stricter envelope. | Relay authenticates users, checks CSRF and pins scope server-side. Treat anonymous events as forgeable even with the origin and CORS checks. Anonymous failures do not trigger incidents. |
| Client overload or forged volume | Limits on request bodies, batches, pending events, per-scope backpressure and rate limiting. Query concurrency is bounded. | Test ingress/gateway limits and rate-limit behavior under relay/NAT concentration. Monitor queue and disk conditions. |
| Support user accesses another app/environment | Server validates OIDC identity/role and matches viewer groups on reads and mutations. Administrators have global access. | Test independent application and environment scopes, privilege changes, group-claim format and overage behavior with the actual IdP. |
| Cross-site incident writes or logout | Secure session cookie in production, antiforgery header verification, POST actions and same-origin portal API. | Test with the production browser, IdP callback and configured hostname. Verify cookies, CSRF and TLS behavior through the actual topology. |
| Executable markup in diagnostics | Fixed event schema, encoded Razor text output, restrictive Content Security Policy, same-origin static assets and no third-party chart scripts. | Attach no external UI scripts or unsafe HTML to diagnostic surfaces. Review CSP and browser behavior after any UI changes. |
| Unauthorized reading or overwriting SQLite, settings, backups or key files | Dedicated service identity and administrator ACLs in Windows installer. Database access uses parameterized SQL. Binaries are staged from a verified manifest. | Protect host, local disk, private settings, keys and backups with enterprise ACLs and encryption. Confirm the monitored environment's storage and key requirements. |
| Synthetic requests or notifications reach sensitive hosts | Static operator-supplied HTTPS destinations, no redirects, cookies or automatic proxy use. Synthetic checks only send GET. Webhooks send compact incident metadata. | Restrict egress, control DNS and approve the exact URLs independently. Avoid probe paths that change business state or reveal protected data. Use approved webhook receivers and avoid temporarily reusing a destination alias across trust zones. |
| Secret disclosure and token replay | HTTPS, explicit per-scope token checks, TLS-trusted relay/CI, and credentials outside the browser. | Protect secrets at rest, rotate them, restrict network reachability, and avoid logging request bodies and authorization headers in gateways and request logging. |
| Insider misuse of notes, scopes or release data | Server-enforced incident roles and recorded incident history. Configuration is restricted to operators with host access. | Record config edits through approved external change control. Treat incident notes, actor subjects and assignees as operational records. |

The SQLite runtime must include the WAL concurrency fix. The service checks the bundled engine at startup/migration. Keep the pinned package, Windows OS, ASP.NET Core shared runtime, and published WebAssembly client updated through change control. [SQLite WAL documentation](https://www.sqlite.org/wal.html)

## Data rules

Use only semantic operational facts. Examples include a declared dependency alias, elapsed time, HTTP status, exception category, anonymous random session ID, and W3C trace IDs. Review custom labels and configuration as controlled metadata. Never embed user input, URLs with parameters, authentication credentials or SAP/report content in metadata. Operator incident notes and assignment fields are free text with length limits. Use incident references and technical summaries, and keep customer, employee or report details in the approved systems of record.

Limit SQLite database and snapshot access to authorized operators. Host-level administrators and the configured service identities can access data within their assigned Windows ACLs. Use an approved encrypted volume and encrypted backup destination. On Windows, PulseOps protects the persisted ASP.NET Data Protection keys using machine DPAPI. Protect the key directory with host ACLs and encrypted storage. A recovery onto a different machine requires operator reauthentication. A configured hardware/key-management integration and central audit export are environment-level additions. Include the applicable incident-history and backup retention requirements in the organization's approval process.

## Release security verification

- Review dependency licenses, full package lock files, Windows runtime version, vulnerability scan and artifact provenance for the release.
- Validate TLS, OIDC identity mapping, role/group scope, cookie behavior, CSRF and negative authorization tests on the target platform.
- Confirm source instrumentation and relay filtering, anonymous startup choice, production data retention, storage encryption and restore procedures.
- Verify logs exclude payloads and secrets. Inspect the application relay and upstream gateway separately.
- Verify synthetic allowlists and outbound rules. Confirm external monitoring detects a full host outage.
- Record the approved production threat review, residual operational risks, and named role owners in the organization's change record.

See [Windows operations and acceptance checks](Operations.md) for installation and validation commands.
