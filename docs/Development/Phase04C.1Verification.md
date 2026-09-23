# Phase 04C.1 verification — RealtimeSession wake/dispose concurrency

Date: 2026-09-23.

## A–C. Baseline, CI and original failure

The repository root is `C:\Users\Yovko\source\repos\ArbTrading`. Work started on `main`, ordinary checkout, clean working tree, at the exact published Phase 04C commit `8cc878eda554a847022faf3817d47ea7a9e2f60e` (`Add read-only paper valuation and portfolio risk analytics`). Root AGENTS instructions were read. No branch, remote, history, migration or existing user work was replaced.

A read-only public GitHub API lookup for this exact commit observed [run 35859757395](https://github.com/yovko93/ArbTrading/actions/runs/35859757395) completed successfully. No workflow was triggered or changed. The source race remains real even though this Phase 04C run passed.

The motivating Phase 04B Windows failure was `RealtimeRetryTests.Initial_failure_uses_bounded_backoff_manual_refresh_retries_and_shutdown_cancels`: `System.Threading.SemaphoreFullException: Adding the specified count to the semaphore would cause it to exceed its maximum count`, escaping through `RealtimeSession.Signal()` during disposal. Linux passed. This was a desktop synchronization/lifecycle defect, unrelated to paper accounting or settlement.

## D–I. Root cause and corrected invariant

The former implementation split wake ownership between a `SemaphoreSlim(0, 1)` count and an independent `wakeQueued` integer. During backoff, timeout and cancellation races, a waiter could detach or complete while the integer was reset independently. The semaphore could still contain a permit while bookkeeping said no wake was queued. A later refresh, invalidation, hub close or shutdown then called `Release()` against count one and raised `SemaphoreFullException`.

`CoalescingWake` is now the sole wake authority. It uses a bounded channel with capacity one, one reader, concurrent writers and `DropWrite` for duplicates. One unread byte means “work may have changed”; any number of extra signals coalesces. `Signal()` is nonthrowing, storage remains bounded, and there is no independent count to drift. This is an in-process control notification, not data; dropping a duplicate while one is already pending preserves its semantics.

Every wait owns its cancellation. The timeout overload links the caller token to its deadline and awaits the channel read through completion. In exponential backoff, when the delay wins, the loop cancels and awaits the wake reader until it has detached. A signal that won just before cancellation is knowingly consumed because the loop is already advancing; a signal after detachment stays as the one bounded item for the next iteration. When the wake wins, manual Refresh still advances immediately and cancels the delay. There is no polling or timing expansion.

Access invalidation wakes the loop but remains unauthorized: after consuming that wake, the top-level pause check continues while `authPaused` is true. Refresh clears the pause before signaling, so it remains the explicit retry path. Stopped-backend refresh, hub Closed, credential-rotation recovery, state invalidation and active consistency refresh retain their prior meanings.

`Start` and `Dispose` now share a lifecycle gate. A disposed guard makes cancellation, event unsubscription and shutdown signaling execute once. Start after disposal is a safe no-op. Repeated Dispose, concurrent Dispose, `StopAsync` plus Dispose, and a later using-disposal are safe. Synchronization primitives are intentionally not disposed while the runner may still use them; the run loop continues to own HubConnection disposal. `StopAsync` captures and awaits the runner after the idempotent transition.

## J–K. Focused deterministic and repeated tests

`RealtimeRetryTests` now deterministically covers:

- 64 concurrent signals coalescing into exactly one bounded unread wake;
- cancellation fully detaching an old reader before a later signal is consumed;
- a pending wake with Dispose twice, Start after disposal, StopAsync and later Dispose;
- 64 concurrent Refresh calls racing Dispose and StopAsync during controlled backoff;
- access invalidation pause, explicit Refresh retry and disposal;
- the original initial-failure, bounded-backoff, manual-refresh and shutdown behavior.

The focused class passed 6/6. A supplemental bounded repeat ran the complete focused class 20 times: 20/20 runs passed (120 test executions). Related focused tests passed: Backend realtime/retry/state and local backend lifecycle 28/28; Desktop realtime/process lifecycle 5/5. The ACL/process tests required normal Windows protected temporary storage outside the workspace sandbox; no test was skipped or weakened.

## L–O. Full verification

- `dotnet restore ArbitrageTrading.sln`: passed.
- `dotnet build ArbitrageTrading.sln -c Release --no-restore`: passed with zero warnings and zero errors.
- `dotnet test ArbitrageTrading.sln -c Release --no-build --no-restore`: **616 passed, 0 failed, 0 skipped** — Domain 55, Application 223, Backend integration 322 and Desktop 16. This preserves the 611-test Phase 04C baseline and adds five deterministic tests.
- `scripts/verify.ps1`: independently passed restore, the zero-warning/error Release build and the same **616 tests**.
- `dotnet ef migrations has-pending-model-changes --project src/Arbitrage.Infrastructure --configuration Release --no-build`: no changes since the last migration. No model or migration file changed.
- `dotnet list ArbitrageTrading.sln package --vulnerable --include-transitive`: passed against the configured sources; no vulnerable packages were reported across all 13 projects. Auditing remains enabled.
- The actual Ubuntu WSL invocation of `bash scripts/verify-backend.sh` stopped at line 5 with `dotnet: command not found`. No local Linux/.NET success is claimed. The separate published Phase 04C GitHub run had both jobs green.
- `git diff --check`: passed. Tracked sensitive/runtime filename inspection returned no matches.

## P–R. Scope and completion state

Only `RealtimeSession` wake/lifecycle synchronization, its focused regression tests and this verification note changed. Phase 04A–04C execution, balances, settlement, realized/unrealized P&L, valuation, risk, fees, monitoring, relationships and orderbooks are unchanged. No schema change or migration exists. No Phase 04D work was started.

The channel remains process-local and carries no payload, so process termination can discard a wake as before. Coalescing intentionally does not guarantee one loop iteration per caller; it guarantees at least one bounded wake while a notification is pending. Timing-sensitive behavior still depends on OS/task scheduling, while correctness no longer depends on matching a separate counter to a semaphore count.

Changes remain uncommitted. No commit, push, merge or pull request was made.

## Changed files

- `src/Arbitrage.Desktop/Services/RealtimeSession.cs`
- `tests/Arbitrage.Backend.IntegrationTests/RealtimeRetryTests.cs`
- `docs/Development/Phase04C.1Verification.md`
