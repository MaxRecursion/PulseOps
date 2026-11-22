# PulseOps engineering instructions

- Build native .NET 10 projects with centrally pinned dependencies. Use SQLite on a local filesystem for every environment.
- Use DataCat upstream code where a licensed adaptation benefits PulseOps. Attribute copied or translated code and keep licensing with any distribution. Keep upstream branding and implementation dependencies outside PulseOps.
- Never capture body content, user identifiers, raw exception messages, URL parameters, cookies, authorization values or financial data. Maintain separate scoped browser and machine trust boundaries. Default to denying access.
- Keep ingest acknowledgement after durable commit. Keep processing effects idempotent and transactionally coupled to receipt state. Keep policies bounded.
- Never publish packages, deploy, or modify IIS or Windows services during development. Use clearly labeled demo data in local development.
- Run restore, build and critical tests after meaningful changes. Update IMPLEMENTATION_STATUS.md with actual verification and limitations.
