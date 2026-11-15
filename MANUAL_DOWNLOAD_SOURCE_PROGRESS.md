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

## Remaining work

The user added a single-song retry request during implementation. The next chapter
will bound the explicit song retry action and report its attempts. Unless clarified
otherwise, the five-retry limit applies to that manual action, while existing
automatic album recovery remains unlimited.

After the song work, finish validation, stop the disposable PostgreSQL server,
push `feat/manual-audio-download-source` and open its pull request to `master`.
