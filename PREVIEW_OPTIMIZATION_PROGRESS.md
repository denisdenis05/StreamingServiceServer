# Preview and streaming work

Updated 2026-10-17. Covers O01 and the fallback classification part of O02 in `../REMAINING_OPTIMIZATION_REVIEW.md`.

## Implemented

- Preview service is a singleton with two concurrent process pipelines by default and at most 64 active/queued recording identities. Concurrent callers for one recording share work. Canceling one caller leaves other callers running; canceling the last caller stops its pipeline. Saturation returns HTTP 503 with Retry-After.
- `Music:PreviewDownloader:MaximumConcurrency` and `Music:PreviewDownloader:DeadlineSeconds` override positive defaults of two pipelines and 120 seconds. The deadline includes admission, download and validation. No credentials were added to settings.
- Downloader and FFprobe drain both redirected streams while running. Each diagnostic stream retains at most 8,192 characters. Cancellation kills a running process tree, then bounds process exit and pipe cleanup waits to five seconds.
- Downloads use unique `.preview-*` staging directories within TEMP. Candidate admission requires one supported nonempty audio file, at most 128 MiB, without a symbolic link. FFprobe must identify an audio stream before atomic rename into the final TEMP path. Downloader arguments also request a 128 MiB source limit and disable playlists. The postprocessing size check remains necessary because extracted audio size differs from downloaded source size.
- Success, failure and cancellation remove the operation's own staging directory. Incomplete staging files are never considered streamable. Published preview paths are relative, such as `TEMP/<recording-id>.mp3`, so X-Accel redirects no longer contain the absolute host storage root.
- Stream requests fall back only after `RecordingAudioUnavailableException`. Database failures, root storage failures, permission errors, unrelated IO errors and cancellation propagate instead of invoking an external download. Missing album/TEMP directories are classified only after checking the configured storage root.
- Request cancellation reaches local lookup waits, metadata waits, preview admission and subprocess execution.
- Shared work starts outside the admission lock, so synchronous storage checks do not hold that lock. Root containment accepts paths with or without a trailing directory separator.

## Feature tests

Final integrated checks: 18 preview cases pass, including two trailing-separator cases; the complete server suite passes 142 cases with no failures or skips. Release publish and the 29 dashboard JavaScript cases pass. Existing nullable and EF dependency-version warnings remain. These checks use disposable storage and PostgreSQL fixtures, not the application database.

`StreamingServiceServer.CoverTests/PreviewOptimizationTests.cs` covers:

| Behavior | Dedicated regression |
| --- | --- |
| Full stdout/stderr pipes | ProcessRunnerDrainsBothPipesWithoutRetainingUnboundedDiagnostics |
| Child cancellation | ProcessRunnerCancelsSleepingChild |
| Failed child diagnostics | ProcessRunnerReportsExitFailureWithBoundedDiagnostics |
| Shared caller lifecycle, validation, publication, relative cache hit | SharedPreviewSurvivesOneCanceledWaiterAndPublishesRelativeValidatedFile |
| Last caller cancels and cleans up | FinalWaiterCancellationStopsWorkAndRemovesTemporaryDirectory |
| Total deadline | DeadlineStopsAbandonedDownloader |
| Process concurrency | ProcessQuotaSerializesDifferentRecordings |
| Invalid output rejection | InvalidAudioIsNeverPublished |
| Storage failure classification | MissingStorageRootDoesNotBecomeUnavailableMedia |
| Genuine absence classification | MissingMediaWithinHealthyStorageUsesTypedUnavailableResult |
| Infrastructure failure avoids fallback | DatabaseFailureDoesNotStartMetadataOrPreviewFallback |
| HTTP cancellation and path encoding | PreviewFallbackPropagatesCancellationAndEncodesOnlyRelativeSegments |
| Bounded pending identities | PendingRecordingAdmissionRejectsOverflowWithoutLaunchingAnotherProcess |
| Pre-canceled requests | AlreadyCanceledCallerDoesNotStartPreviewWork |
| Retryable saturation response | PreviewCapacityProducesRetryableHttpResponse |

Command: `dotnet test StreamingServiceServer.CoverTests --filter FullyQualifiedName~PreviewOptimizationTests --nologo --no-restore`.

No production database was accessed. Real provider downloads, NFS failure injection and Nginx were not tested.

## Remaining work and limits

- O02 still needs persisted exact audio paths and a small metadata projection: this change still loads the existing recording response and enumerates matching filenames in one recording folder. It stops enumerating the entire folder eagerly, but does not add a storage-path migration.
- The existing metadata interface has no cancellation argument. `WaitAsync` releases an abandoned HTTP caller, but cannot cancel the already-started metadata database/provider operation. O20 requires interface-wide provider cancellation.
- Filesystem calls remain synchronous. Kernel-level hard NFS stalls cannot be interrupted by a .NET cancellation token. A NAS unmounted to an otherwise accessible empty mountpoint cannot be distinguished reliably from genuinely absent files without a configured mount/sentinel health contract.
- Concurrency and shared work are per server process, not distributed across replicas. Do not use multiple independent writers as a distributed quota.
- Existing nonempty published previews are reused without per-request FFprobe. Validation applies to new publication; existing legacy files need a deliberate migration/scrub policy if corruption is suspected.
- Normal terminal paths clean staging directories. A server crash or forced process termination can leave `.preview-*` directories; they are excluded from streaming. Do not delete another live instance's staging directory. A future shared-storage inventory/lease policy should reclaim crash orphans and expire final previews with an explicit retention promise.
- The downloader source-size flag and final-size admission are not an operating-system disk quota. Temporary extractor/postprocessing output can exceed the final-size cap before rejection. Use a bounded storage filesystem for a hard capacity guarantee.
- Killing a running process tree is covered through a sleeping child fixture. A tool that deliberately detaches descendants or exits while descendants retain pipes needs platform process-group containment; pipe cleanup remains time bounded.
