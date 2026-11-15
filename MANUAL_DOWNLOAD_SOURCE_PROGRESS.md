# Manual audio download sources

## Implemented

The Queue page's Audio downloads table now offers `add source manually` for albums
without a source, including failed albums. The failed-download table offers the
same action. The existing source picker can run a fresh Soulseek search using
editable artist/album terms, then submit a selected directory.

Search does not change queue state. Assignment validates every filename and size
against slskd's search, verifies that all selected files are queued, saves a new
pending source/import identifier and removes the failed marker in one database
transaction. An orphaned failed record recreates its queue record. Failed upstream
verification preserves the failed marker and original queue state. An existing
source, stale generation, deleted album or conflicting operation is rejected.

The automatic downloader now shares the album lock used by manual assignment,
source switching and import, and rechecks eligibility after obtaining it. This
prevents an old automatic-search snapshot from overwriting a manually assigned
source or restoring its failed marker. Existing change-source behavior is retained.

The modal blocks repeated submissions and searches while busy, discards stale
responses, refreshes the queue after assignment and removes the failed row locally.
Static asset versions were advanced together. No database migration is required.

## Verification

- `dotnet build StreamingServiceServer.API/StreamingServiceServer.API.csproj --no-restore --verbosity quiet`: passed, existing warnings remain.
- `node --check StreamingServiceServer.API/wwwroot/js/dashboard.js`: passed.
- `node --test tools/*.test.cjs`: 68 passed, including eight dedicated manual-source UI tests.
- With `COVER_TEST_POSTGRES='Host=/tmp/streaming-manual-source-postgres.W3IbD2;Port=56439;Database=postgres;Username=denis'`, `dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~ManualDownloadSourceTests|FullyQualifiedName~SoulseekDownloadRetryTests' --verbosity quiet`: 37 passed, none skipped. The dedicated manual-source class has 14 cases covering selection validation, admin authorization, search, recovery, queue restoration, stale/duplicate commands, partial enqueue, missing searches, tampered files and album ownership.
- `git diff --check`: passed.

Database tests used a newly initialized PostgreSQL server under `/tmp` and simulated
slskd HTTP responses. No production database, live Soulseek transfers, deployment
or browser session was used.

## Manual song retries

The album track list now labels its individual-track action `retry song`. It searches
Soulseek for that song and downloads only the selected file, with one initial attempt
and at most five retries. A failed attempt waits 30 seconds before another candidate
is tried; if fewer candidates exist, they are reused within the same six-attempt
budget. Status messages identify the current attempt. Each song/action has its own
budget. The user explicitly confirmed that automatic album recovery stays unlimited.

The server checks recording/album ownership, prevents duplicate active jobs and
scopes job status to its album. The UI disables duplicate submissions and restores
the song button after completion or error. Transfer matching now checks the full
selected path and size. Terminal error states are checked before success, a failed
HTTP enqueue consumes an attempt, silent enqueue failures stop waiting after the
appearance deadline, and a disk candidate must match the requested file size.

`SoulseekSongRetryTests` provides seven deterministic cases for the initial attempt
plus five retries, retry delays, peer fallback/reuse, immediate and final-attempt
success, cancellation, independent song budgets and empty searches. The source
integration class adds wrong-album and duplicate-job/status tests. Three additional
UI tests cover the song action, repeated clicks, attempt display and exhaustion.

Final validation:

- `node --check StreamingServiceServer.API/wwwroot/js/dashboard.js`: passed.
- `node --test tools/*.test.cjs`: 71 passed.
- With the disposable PostgreSQL connection above, `dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-restore --filter 'FullyQualifiedName~ManualDownloadSourceTests|FullyQualifiedName~SoulseekDownloadRetryTests|FullyQualifiedName~SoulseekSongRetryTests' --verbosity quiet`: 46 passed, none skipped. This builds the API and referenced projects; existing compiler warnings remain.
- `dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-build --no-restore --verbosity normal` without the PostgreSQL environment variable: 299 passed, 103 skipped, one existing failure in `AudioImportSafetyTests.ImportDestinationCannotEscapeMusicRootOrTraverseDirectoryLinks`. It expects `InvalidDataException`, but the unchanged path validator throws `InvalidOperationException`.
- `git diff --check`: passed.

The manual song job retains the existing in-memory background-job lifecycle;
restarting the server does not resume it. Live transfer completion, disk publication
and browser interaction remain unverified. These changes do not deploy the API.

## Delivery

Work is on `feat/manual-audio-download-source`, based on the freshly fetched
`origin/master`. The manual-source chapter is commit `7b70cea`; the song chapter is
committed separately. Unrelated local settings, notes and IDE files are excluded.

GitHub authentication was initially unavailable over HTTPS and SSH. The user supplied
a token through the private secrets card to finish pushing this branch and creating
its pull request to `master`. The disposable PostgreSQL instance was stopped after
validation. No runtime source changed during the delivery follow-up.
