# Phase 04H.4 Settings capability truthfulness

Settings now displays a read-only capability matrix sourced from the existing authenticated `MainViewModel` snapshot labels: Paper execution, live order submission, manual live execution, and automatic live execution. The section also shows the snapshot age. Without an authorized snapshot, capability values remain `Unknown`; a retained disconnected snapshot is marked stale. Automatic Paper is described separately as an explicitly armed Paper simulation controlled from Trading.

A bounded audit of primary Desktop views found no other inaccurate Phase 01 execution or strategy claims in normal operation. The remaining live-unavailable statements refer specifically to live execution, and paper-service fallback text refers to missing shell services. The burn-in runbook does not quote the removed Settings sentence, so it was not changed.

`SettingsCapabilityWpfTests` checks the rendered matrix for current Paper-only, future automatic-live, no-snapshot, stale, and access-denied fixtures. Light and Dark connected captures, plus no-snapshot captures, are rendered for visual inspection. This phase changes presentation only: no backend endpoint, capability contract, trading behavior, migration, or data was changed. BurnIn-01 was not started.
