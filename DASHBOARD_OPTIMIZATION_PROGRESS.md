# Dashboard polling progress

Updated 2026-10-17. Covers O07 and the browser part of O08 in `../REMAINING_OPTIMIZATION_REVIEW.md`.

## Implemented

- Replaced the shared ten-second interval with independent resource controllers. Each resource starts its next ten-second wait after its request settles. Manual refreshes join an outstanding request instead of creating another one.
- Overview requests stats, queued albums and downloaded albums independently. Queue requests audio queue and cover progress independently. Downloaded and failed pages request their own collections. Settings, playlists, album details, login and analysis do not run these background library polls.
- Navigation renders immediately. Initial rendering no longer waits for four complete collections or provider responses.
- Hidden tabs, leaving a resource's view, logout and account changes abort requests, remove scheduled refreshes and invalidate response generations. Returning starts fresh requests immediately.
- Cover progress keeps its existing filter/search/pagination controller. View and visibility changes deactivate it and abort its own request. Cover refresh never awaits stats or audio queue responses.
- Downloaded-album search uses the same cancellation and generation rules. Superseded searches cannot publish later results.
- Authentication prompts from aborted requests or a previous authentication identity are suppressed.

The scheduler owns polling and publication eligibility. The dashboard owns routing, authentication and rendering. Cover progress owns its own interactive request replacement. Existing action handlers and URLs are preserved.

## Feature tests

`node --test tools/dashboard-polling.test.cjs tools/cover-progress.test.cjs`

Result: **29 passed**, comprising 17 new scheduler/dashboard integration tests and the existing 12 cover-progress tests.

| Behavior | Dedicated coverage |
| --- | --- |
| Slow provider does not block covers | Scheduler blocked-resource case and actual dashboard wiring with pending queue request |
| Independent overview requests | Dashboard wiring publishes queued results while stats remains pending |
| No overlapping polls | Repeated manual refresh joins active requests |
| Hidden, off-view and logged-out resources | Separate cancellation, late-result and immediate-return cases |
| Account isolation | Old identity response cannot replace a current result |
| Failure recovery | Failed resource schedules its own retry without restarting other work |
| Cleanup | Dispose aborts active requests and removes timers |
| Search replacement | Actual dashboard wiring aborts old downloaded search and encodes new query |
| Cover behavior | Existing rendering, filter/search replacement, pagination, authorization and error-retention tests |

Also checked JavaScript syntax for the scheduler, dashboard and existing cover controller. These are deterministic source/runtime tests with fake network and clocks, not measured production performance or a real-browser navigation trace.

## Remaining

- O08 backend still loads whole queue collections and performs per-album provider work. Browser cancellation reduces abandoned client work; server/provider cancellation and shared snapshots require separate backend changes.
- O24 cover aggregate sharing remains open. This change reduces off-view calls but does not cache catalog aggregates across browser tabs.
- Downloaded/failed collection responses remain unpaged. The browser still renders the collection returned by the server.
- User-started scan, mismatch and recording-redownload job status loops retain their existing action-specific lifecycle. They are separate from the removed general library interval and need their own scoped follow-up.
- Validate navigation, auth transitions, background/foreground behavior and provider outages in a deployed browser before claiming latency or provider-request reductions.

No production database, provider, NAS or Nginx access was used. No settings changed. No commit made by this implementation agent.
