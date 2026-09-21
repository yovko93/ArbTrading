# ADR 001 — Persistent local ownership with future authenticated server identities

Status: accepted for Phase 01A.

## Decision

Use an independent ASP.NET Core modular monolith and a local SQLite transactional database. WPF consumes the same versioned HTTP contracts intended for a future web client. Domain is transport/persistence independent; Application defines use cases and ports; implementation modules depend inward; Backend composes them. No microservices or speculative permission framework are needed.

ApplicationUser is a stable internal GUID identity. Workspace is an explicit resource-ownership boundary. WorkspaceMembership grants access (Owner only in this phase). LocalProfile links this installation's authenticated local client to its persisted user and default workspace. The first successful initialization creates all four atomically and only once.

An authentication-provider identity is distinct from an ApplicationUser: future validated issuer/subject pairs map to stable internal UserIds. An email string is not proof of identity and must not be used for automatic linking. Exchange accounts are also distinct: they represent the account against which a workspace's trading records operate, not the actor authenticating an API request.

Importing a local profile into a server requires explicit, authorized ownership linking. Never grant imported data to the first remote login. Preserve all internal identifiers, memberships, audit actors, and workspace ownership during migration.

## Future ownership rules

- Private settings, risk configuration, strategy configuration, orders, positions, balances, and execution history belong to an explicit workspace.
- Trading records also identify the relevant exchange account. The actor causing a change is recorded separately from resource ownership.
- Public market metadata can be shared. Private portfolio, execution, alert, and account data must not leak through shared caches or events.
- SignalR connections and each workspace subscription must be authorized and workspace-scoped, with revocation enforced.
- Background trading jobs carry explicit workspace, exchange-account, and execution-authority scope. They cannot depend on HttpContext or global current-user state.

## Server migration prerequisites

Server mode is explicitly unavailable now. Before enabling it: implement standard authenticated access over HTTPS, validated external-provider identities, workspace permissions, session revocation, and a server-side secret store. Retire the local-owner credential for remote access.

PostgreSQL requires provider-specific migrations and verified data transfer, including row counts, ownership, identifiers, exact timestamps and financial representations. Replacing a connection string is not a migration. Require explicit credential re-enrollment or a separately secured transfer process; do not copy plaintext local credentials into server configuration.

Cutover must establish one active execution authority per trading account and stop the previous authority before activating the new one. Distributed synchronization, custom identity providers, and multi-engine coordination are deferred.

Local-first means local ownership and initial operation. It does not mean executing live trades offline or queuing stale trade intents for submission when connectivity returns.

## Persistence consequences

SQLite EF migrations are versioned source. Domain timestamps are DateTimeOffset normalized to UTC and persisted as INTEGER UTC ticks, with tests for exact roundtrip and SQL filtering/sorting. No financial columns are necessary yet. Future money must use decimal in code and a tested exact persisted representation; converting financial values to double or SQLite REAL is prohibited. Future numeric database comparisons and sorting must be tested against the selected representation.

The transactional settings database is not an orderbook event archive. High-frequency market recording needs a separate storage design when required.
