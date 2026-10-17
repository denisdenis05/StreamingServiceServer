# Server image processing performance work

Updated 2026-10-17. Changes are uncommitted. Local application settings and `.idea` are outside this work.

## Implemented

### Independent processing and maintenance

`CoverAssetJobService` now runs pending work independently of `CoverAssetMaintenanceService`. A maintenance exception or blocked maintenance stage does not prevent the processing loop from starting. Each maintenance stage and each completed job has its own failure boundary. New processing logs report admissions, elapsed time, pending count and oldest pending work; maintenance stage timings are available at Debug level.

The processing scheduler replenishes a free slot when another job finishes. A slow first download does not hold the next job behind a complete batch. A bounded pass attempts each release/recording pair at most once. Its first nine admissions prefer user changes and repairs; every tenth admission chooses the oldest due job regardless of class. Historical work therefore progresses while newer work has priority. PostgreSQL leases and source/version checks remain the publication authority across server instances.

Backfill has a persisted cursor for each entity kind. Each maintenance pass reads at most one metadata page per kind, projects only source fields and checks job existence for that page. Cursor and inserted jobs are saved together. `(DateAdded, Id)` indexes support traversal. The cursor survives service restarts and wraps daily to recover older holes; newly created metadata still enqueues work through the save interceptor immediately.

### Shared integrity checks and bounded cleanup references

Completed jobs and current source/local fields are fetched in bounded batches. Matching image identities share one verification within the batch. Routine checks inspect file presence and length; cryptographic scrubs run on a separate configured interval.

Successful scrub deadlines are persisted in `CoverAssetIntegrityStates`, keyed by asset identity. Restarting the service or evicting its bounded memory cache does not force a healthy image to be hashed again before that deadline. Missing files invalidate the deadline immediately. The ledger itself has bounded cleanup of expired, unreferenced records. Simultaneous server instances can still duplicate a scrub when both observe an expired deadline; they do not share unchecked publication rights.

`AssetIdentity` and `OriginalIdentity` on jobs provide indexed retention lookups. Existing manifest references are migrated a bounded page at a time. Before that migration finishes, final assets are conservatively retained while temporary cleanup still runs. Pruning no longer loads every duplicated manifest into memory.

### Decode once and reject oversized images early

The renderer accepts all output variants in one operation. One FFmpeg process decodes the source and splits it into three scaled outputs. Decoder, filter and encoder thread counts are bounded. PNG, JPEG and WebP dimensions are checked from at most 256 KiB of headers before FFmpeg starts. Inputs above the configured pixel limit are refused.

Both process output streams are drained; retained diagnostics are capped. Cancellation and the render deadline kill and reap the subprocess tree. Pixel admission, one decoder and bounded threads reduce resource exposure. They are not a hard process RSS limit; use container memory limits for that boundary. A hard-mounted NAS can still block an operating-system filesystem call beyond a managed cancellation deadline.

### Durable manual metadata refresh

`POST /Metadata/refresh-all-album-covers` now returns 202 after persisting a refresh request. Repeated requests while pending coalesce. `GET` on the same route returns checkpoint/progress/failure state; `DELETE` cancels the operation.

A separate worker performs one provider lookup per second at most per server instance, handles one release at a time and commits that release with its checkpoint. Recreating the worker resumes the next release. Source comparisons prevent an old provider result from replacing an independently changed release. Recording updates are set based and apply only to matching inherited source tuples; custom sources are preserved. Cancellation changes the operation version, preventing an in-flight result from publishing. Provider failure records its type and preserves the checkpoint for retry.

The external metadata provider's existing interface does not expose transport cancellation. Shutdown stops awaiting it, and operation-version checks fence publication; cancellation of the underlying HTTP request remains controlled by that provider. Provider concurrency/rate is per instance rather than a distributed global quota.

## Feature tests

| Functionality | Dedicated checks |
| --- | --- |
| Shared scrub interval | 100 requests use one hash operation; next interval verifies again |
| Missing files | A successful cached check is invalidated and the repaired asset is reverified |
| Bounded memory | Eviction does not grow the verification cache; persisted deadlines avoid rehashing |
| Restarted maintenance | Database scrub deadline survives a new maintenance instance |
| Bounded backfill | One-row pages persist and resume after maintenance recreation |
| Continuous processing | Third job starts while the first of two slots remains blocked |
| Priority and fairness | First nine user changes run before backfill; oldest backfill gets the tenth admission |
| Corrupt maintenance state | An incomplete manifest does not stop unrelated pending work |
| Decode admission | Huge encoded dimensions are rejected before executable startup |
| Batch rendering | Three variants produce exactly one FFmpeg process invocation |
| Renderer lifetime | Deadline kills and reaps a sleeping child process tree |
| Durable refresh enqueue | Request does not call a provider; duplicate requests preserve one pending operation |
| Durable refresh resume | A recreated worker resumes the next release without repeating the committed release |
| Refresh cancellation | A provider result completing after cancellation cannot publish |
| Provider failure | Failed lookup preserves the cursor and successful retry clears failure state |

Final integrated command:

```sh
COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Database=postgres' dotnet test StreamingServiceServer/StreamingServiceServer.CoverTests --no-restore --nologo
```

Result: 80 passed, zero failures and zero skips, including PostgreSQL integration and real FFmpeg checks. The batch renderer test measured one process start for three variants. The cancellation test verified bounded completion after killing a sleeping process tree. `git diff --check` passed. The database fixture accepts only a disposable `/tmp` PostgreSQL socket and creates/drops generated test databases. No production database was accessed.

## Deployment

Apply the additive migrations before running the updated application. `deployment/harden-cover-asset-scheduling.sql` contains the idempotent upgrade after `20261016190024_AddCoverAssetJobs`, including scheduling/source fields, metadata refresh state, backfill indexes, the shared integrity ledger and backfill cursors.

New settings have defaults in `CoverAssetOptions`; no actual configuration values were committed or changed for this work. Pending imports, image byte/pixel limits, operation/render/idle deadlines, lease lifetime and maintenance limits are validated at startup.

## Remaining verification

Measure the deployed server on its actual NAS: first-image latency, cold and warm batch duration, bytes read, distinct scrub count, FFmpeg process RSS and external provider response distribution. The regression tests establish operation counts and ordering, not a production throughput or memory percentage improvement. No Nginx tests or production migrations were run.

## Integrated result

The final combined server suite passed 91 tests after transfer cleanup and ownership cross-review. See [IMAGE_TRANSFER_REPAIR_PROGRESS.md](IMAGE_TRANSFER_REPAIR_PROGRESS.md) for the shared test/build/deployment state. Earlier per-track counts above record the checks at those implementation points.
