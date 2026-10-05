# Retained requirements; implementation reset

These are confirmed design inputs, not implemented or accepted capabilities. They are restated without private source documents or old progress.

- New independent product; do not read, import, probe, migrate or provide compatibility with the previous PMM database or backups
- Intended .NET 10/C# 14, one Kestrel host with Razor MVC, HTTP and MCP
- Bootstrap 5, jQuery and Axios; UI assets packaged locally
- SQLite 3 and EF Core 10 Code First; durable HostedServices; no Redis or external message broker
- Intended xUnit, real isolated SQLite/filesystem tests, API contracts and Playwright; Windows/Linux/macOS evidence kept separate
- GitHub/Actions/Issues; Bash and PowerShell 7 planned for their actual platforms
- Rules and TMDB by default; AI optional; no implicit paid AI activation from MCP-client proposals
- Local works, manual metadata, complex episode coverage, preserving original audio and controlled source/target overlap remain in product scope
- No automatic overwrite by default; preserve before replacement; undo follows actual disk state; UI/HTTP/MCP share domain rules
- Existing approved visual direction is a design reference; object-level playback/download links do not add a downloader or playback server

Engineering proposals for journaling, leases, immutable plans and capability authorization remain proposals to be reviewed when their implementation cards open. No field-level approval is inferred from old documents.

Unresolved product policies remain open: move/copy defaults; conflict behavior; preservation/retention; roles and remote/LAN access; platform acceptance order; directory reuse; coverage/NFO compatibility; AI budgets and models; cleanup; restore cutover; update/rollback; notifications/network egress; and settings consumers. They do not block governance-only adoption and do not authorize any real operation.
