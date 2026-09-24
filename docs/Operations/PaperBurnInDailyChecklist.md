# Paper burn-in daily checklist

Use with the [operator runbook](PaperBurnInRunbook.md). Record UTC date/time and campaign ID in a private operator log. This is a review, not a daily campaign completion or permission to trade live.

- [ ] Repository commit and saved configuration-freeze record still match the running build and effective risk, automation, monitoring, fee, generation and kill revisions/settings. Record any boundary before changing configuration.
- [ ] Backend is connected, `TradingMode=Paper`, campaign is `Collecting`, and last `EvaluatedAt` is current enough for review. Record any pause/restart and excluded downtime.
- [ ] `EvidenceGapDetected=false`; no telemetry persistence failure, interval/retention limit, or unexplained event gap. If a gap exists, retain the report and investigate; do not call it a pass.
- [ ] No `InvariantViolation`, `Corrupt` generation, `Unknown` mandatory invariant, or unexpected worker fault. Run the explicit generation reconciliation when integrity is uncertain; do not reset to hide a fault.
- [ ] Kill switch is clear, or a latch reason/time/revision has been reviewed and Auto Paper is stopped. A reset never rearms.
- [ ] Monitoring is `Running`; note `ApprovedRelationshipsAvailable`, `RelationshipsMonitored`, `RelationshipsSkippedByBound`, `CoveragePartial`, `PlansWithBooksAvailable`, `PlansWithActionableBooks`, `PlansWithResolvedFees`, and FeeAdjusted/GrossOnly/NearEdge/Blocked counts. Do not claim full coverage when partial.
- [ ] Check current Kalshi `Continuous` and Polymarket `BestEffort` source/age/availability, saved BestEffort acknowledgment, and fee state; record ordinary network or credential limitations without bypassing gates.
- [ ] Record Auto Paper state/session ID, armed/healthy duration, explicit arms/disarms, candidate inputs/distinct inputs, sizing attempts, automatic committed/rejected counts, cooldown/limit outcomes, duplicate suppression and any anomaly.
- [ ] Review current paper generation/balances and economics **separately by exchange and currency**. Never sum USD with USDC or treat expected payout allocation as actual venue outcome.
- [ ] Use **Paper Reliability Campaign → Evaluate Now…** for a persisted daily snapshot; record `CampaignId`, `EvaluatedAt`, `State`, `EvidenceFingerprint`, and any new limitation. Export an interim report if needed. Leave the campaign collecting until the planned completion procedure.
