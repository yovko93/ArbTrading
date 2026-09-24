# Phase 04H.3 desktop capability cleanup

This phase removes stale Phase 01 presentation from Dashboard, Strategies, and Analytics. It adds no backend endpoint, migration, financial calculation service, market acquisition, or execution capability. Burn-in remains a separate operational activity.

## Page ownership

- **Dashboard** presents the latest local backend and workspace snapshot, execution capability labels, exchange integration states, and links to the operational pages. Unknown and stale values remain labeled as such.
- **Strategies** is a read-only explanation of deterministic, relationship-verified, fee-aware opportunity evaluation. The current Contracts expose opportunity strategy values but no strategy catalog or editable configuration read model, so the page does not present a hardcoded list or controls.
- **Analytics** reads the current paper account, performance, executable valuation, and current reliability campaign from existing local workspace APIs. Its tables keep venue/currency buckets separate and show missing valuation values as unavailable. Portfolio remains the record-level view; Paper Reliability remains the evidence authority.

Analytics refreshes once on activation and on explicit Refresh. It has no background polling loop. Navigation away, workspace/access changes, and backend-instance changes invalidate displayed data and pending responses. Requests use only local `/paper/account`, `/paper/performance`, `/paper/valuation`, and `/paper/reliability/current` reads.

## Verification

Focused tests cover no generation, funded but unexecuted generation, executions and realized P&L, available and unavailable valuation, USD and USDC separation, absent and collecting campaigns, read-only route allowlisting, and late responses after navigation, denial, or workspace change. WPF fixtures render Light and Dark Dashboard, Strategies, and populated/empty Analytics. The full repository verification remains `scripts/verify.ps1` on Windows.
