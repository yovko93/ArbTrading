# Enduring rules

- Preserve local-first ownership and fail closed on authentication, authorization, storage, and execution availability.
- Domain is independent. Application depends on Domain. EF/SQLite stay in Infrastructure. Desktop references only Contracts and obtains business data over HTTP.
- Resolve actors from authenticated request context; check workspace membership explicitly. Record actor separately from workspace ownership.
- Never log credentials or collect exchange secrets. No live trading or simulated fills are implemented in Phase 01A.
- Use GUID identifiers, decimal financial values, UTC DateTimeOffset instants, cancellation, and migration-backed real SQLite tests.
- Preserve existing changes and repository history. Do not commit or push without instruction.
- Validate with scripts/verify.ps1 on Windows or scripts/verify-backend.sh on Linux. Keep docs consistent with actual capabilities.
