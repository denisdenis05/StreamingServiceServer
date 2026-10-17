# Album cover progress

## Implemented

The browser dashboard's queue page shows cover downloads above the existing audio queue:

- A library-wide percentage and completed album count.
- Per-album progress and status, with links to the album details.
- Search by album or artist, filters for missing/downloaded covers, and pagination.
- Refresh through the dashboard's existing ten-second polling cycle. Leaving the page or signing out cancels the pending request; stale responses cannot replace newer results.

The total includes all library releases, including albums whose audio has already downloaded. Search and filters do not change that total. An album counts as complete when all three local cover variants have published paths. Per-album percentages describe completed variants (0, 33.3, 66.7 or 100 percent), rather than transferred bytes. A completed cover remains complete while a replacement job runs.

Statuses distinguish downloaded, downloading, partly downloaded, queued, retrying, unavailable and missing artwork sources. Scheduled retry times appear when available. A suspended job means automatic attempts are currently suspended; it does not prove artwork can never become available.

## Server contract

`GET /api/dashboard/covers` requires the Admin role. Parameters are `page`, `pageSize`, `filter` (`All`, `Missing`, `Downloaded`) and `search`. Defaults are page 1 and 25 albums; the maximum page size is 100 and search length is 200 characters. Results have stable title/identifier ordering. Pages beyond the remaining results are clamped to the last page.

The endpoint reads release metadata and release cover jobs without tracking entities or writing records. It does not inspect NAS files, decode images or make provider requests. Published paths are the completion evidence; the existing integrity worker repairs missing files separately. Consequently, physical storage loss can briefly leave a completed status until that worker updates the metadata.

No new migration is introduced here. The cover-job migrations from the preceding worker changes must be deployed before using this endpoint. The database inspected during the screenshot investigation did not have those tables. No production database changes were made during this work.

## Checks completed

- Full server suite: 116 passed, including disposable PostgreSQL integration cases. The progress query also ran inside a read-only transaction and left no tracked entities.
- New progress coverage: 16 server tests and 12 JavaScript tests covering completion/status rules, aggregate counts, filters, search, pagination, authorization guards, cancellation, stale responses, debounce and refresh failures.
- Release publish succeeded. Existing nullable and dependency-version warnings remain.
- Chromium rendered the actual dashboard assets against a local fixture containing downloaded, partial, active and unavailable covers. This was a visual check with sample responses, not a production deployment.

Run the browser controller checks with `node --test tools/cover-progress.test.cjs`. PostgreSQL tests use the existing disposable `/tmp` socket fixture; never configure them with the application database connection.

## Remaining acceptance

Deploy the updated server and the preceding worker migrations through the normal deployment process, then verify live progress while the worker downloads covers. No push or deployment is included in this chapter.
