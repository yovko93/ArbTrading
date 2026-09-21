# Product scope and deferred phases

The eventual product covers all available Polymarket and Kalshi market categories, with single-market, multi-outcome, cross-exchange, logical, and combinatorial strategies and a future optimization-based solver. Intended initial capital is USD 5,000–25,000; this is planning context, not an assumed balance, allocation, or funding action.

Paper, Manual, and Automatic are distinct modes. Phase 01A represents all three in the domain but accepts only the Paper environment; paper fills and all live execution capabilities are unavailable. Do not treat selecting Paper as an implemented simulator or silently downgrade an unavailable execution request.

Future AI-assisted market matching can approve automatically only when independent deterministic validation establishes the necessary rule compatibility. There are no AI calls or matching algorithms now.

Phase 01A delivers the narrow chain: persistent local profile → authenticated local API → authorized/audited workspace operation → actual backend state in a minimal WPF screen. Registration, password resets, email verification, social login, exchange connectivity/authentication, exchange credential collection, signing, orders, transfers, settlement, simulation, web UI, and cloud synchronization are excluded.

Phase 01B implements full desktop navigation and exactly Dark, Light, System themes. Persist the preference per OS user in desktop storage. Switch dynamically without recreating the window. In System mode respond to Windows application-theme changes. Presentation preferences are independent of backend trading settings. Phase 01A intentionally has no theme selector.

Phase 01C implements SignalR and comprehensive reconnect behavior. Authorize workspace subscriptions and avoid leaking private state across caches or events. The foundation's manual Refresh and one 401 credential refresh are not the complete reconnect design.

Initial deployment is one Windows computer. Later, the same backend can move to a Windows or Linux server after the identity/server ADR prerequisites are met, and a web client can reuse the HTTP API. None of these later steps enable trading by implication.
