# Image cache and background worker review

Reviewed on 2026-10-17 against server `ca911c6` and mobile `8422383`, including the installed `react-native-fs` 2.20.0 source.

## Implementation follow-up

The findings below describe the reviewed commits, before the subsequent repairs. Implementation, tests and remaining limits are recorded in [server transfer repairs](IMAGE_TRANSFER_REPAIR_PROGRESS.md), [server logic](SERVER_IMAGE_LOGIC_PROGRESS.md), [server performance](SERVER_IMAGE_PERFORMANCE_PROGRESS.md) and [mobile transfer repairs](../StreamingServiceMobile/IMAGE_TRANSFER_REPAIR_PROGRESS.md). The follow-up fixes server scheduling/ownership and transfer leaks. Native mobile full-decode validation and cache-hit/preview scheduling remain separate open performance items.

## Conclusion

The pipeline needs more work. There are identifiable causes of slow processing, permanent mobile object/listener retention, conditional iOS queue stalls and server temporary files that cleanup never collects. The previous functionality tests do not establish throughput or absence of leaks.

The server findings are mostly repeated work, blocked workers, disk growth and temporary memory spikes. This review does **not** establish an ever-growing managed heap in the server. The mobile dependency contains direct retention paths on successful and failed transfers.

This was source analysis. No tests, load generators, production queries, Nginx checks or device profilers were run. Memory sizes and timing examples below are calculations, not measurements from the user's server. Only this report was added; runtime code and local settings were not changed.

## How the pipeline currently works

1. Metadata saves create one job per release or recording.
2. Every worker pass backfills jobs, inspects completed jobs and sometimes scans the entire asset directory. Only then does it process up to 50 pending jobs, with three concurrent jobs, followed by a five-second delay.
3. The store shares active downloads by source URL. It keeps a content-hashed original, starts FFmpeg separately for each of three sizes and publishes a manifest.
4. Completed jobs are eligible for integrity checking after five minutes. Each healthy check hashes all three files again. External sources become eligible for refreshing after a day.
5. Mobile downloads into its own disk cache, validates dimensions through native image decoding, hashes the file, publishes it, and scans the cache directory before releasing the result to the view.

## Findings at a glance

| ID | Severity | Area | Finding |
| --- | --- | --- | --- |
| S1 | High | Server availability | Response bodies have no download deadline; a batch can remain blocked indefinitely. |
| S2 | High | Server throughput | Per-recording integrity checks repeatedly read shared image files; advertised repair cadence cannot scale. |
| S3 | High | Server scheduling | Maintenance is a prerequisite for processing new work, including on startup and after storage errors. |
| S4 | High at large catalogs | Server memory/startup | Pruning materializes every stored manifest and scans the entire directory. |
| S5 | High | Server disk | Crash leftovers include temporary filenames that pruning never deletes. |
| S6 | Medium | Server disk | No disk budget; failed decodes leave originals; unused content defaults to one-year retention. |
| S7 | Medium | Server CPU/concurrency | Three separate decodes per image and no shared render operation per content identity. |
| S8 | Medium | Server cancellation | Shared downloads inherit the first caller's cancellation token. |
| S9 | Medium | Server network/queue | Permanent failures retry forever; refreshes download bodies without conditional requests. |
| S10 | Medium | Server resource limits | FFmpeg's allocation setting is not a process memory cap; admission is incomplete. |
| S11 | High | Server response logic | Playlist recordings bypass the completed local cover and omit smaller tiers. |
| S12 | High | Server data ownership | Release propagation can overwrite a recording's newer, independently selected cover. |
| S13 | High | Server response logic | Stale or missing local assets remain preferred; source removal does not clear derivatives. |
| S14 | Medium | Server eligibility | Only the full source is watched and fetched; smaller available sources are not fallback inputs. |
| S15 | Medium | Server queue design | Release/recording duplication and lack of priority delay newly needed covers. |
| S16 | Medium | Server manual refresh | Refresh-all loads the catalog and performs sequential metadata requests inside an HTTP request. |
| M1 | High | Mobile memory | RNFS retains event listeners after failed downloads. |
| M2 | High | Mobile native memory | RNFS retains completed download objects in native dictionaries. |
| M3 | High | iOS availability | Cancellation/resumable errors can leave transfers unsettled and exhaust all cache slots. |
| M4 | Medium | Mobile CPU/memory | Validation decodes a temporary image before display decodes the final path. |
| M5 | Medium | Mobile latency | Each downloaded image scans and sorts the cache before it becomes available. |
| M6 | Medium | Mobile JavaScript | Display telemetry searches up to 1,000 entries and recomputes SHA-256 repeatedly. |
| M7 | Medium | Mobile disk | Removal failures are hidden and releasing pins does not trigger eviction. |
| M8 | Medium | Mobile first display | Full images and previews compete at the same priority; disk hits share download slots. |

## Server findings

### S1 — Download bodies can hold a worker indefinitely

Evidence: `CoverAssetStore.cs:81-86,107`; `Program.cs:38-40`; `CoverAssetJobService.cs:38,47`.

`SendAsync` uses `ResponseHeadersRead`. The configured 30-second HttpClient timeout covers receiving headers, while the subsequent stream-copy loop receives only the caller's cancellation token. In the hosted worker that token normally cancels on shutdown. A peer can return headers and then stop sending, or drip data indefinitely below the byte limit. This behavior is documented by [Microsoft](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-9.0).

One stuck task prevents the current `Parallel.ForEachAsync` call from finishing even if other slots finish their jobs. The next batch, repair pass and pending-count log never run. Three stuck streams can occupy all slots. The five-minute lease is not renewed and does not cancel the old operation, so another process can eventually duplicate the same work.

Required change: an operation-owned total deadline and body-idle deadline, separate caller waiting cancellation, and lease renewal or a lease longer than the enforced total job deadline. Ensure batch progress does not depend on its slowest transfer forever.

### S2 — Integrity maintenance repeats work per recording

Evidence: `CoverAssetJobService.cs:135-176`; `CoverAssetStore.cs:166-180`; `CoverAssetContracts.cs:33-40`.

Completed jobs are keyed by entity, not shared image identity. For each eligible job, repair makes separate source and local-cover database queries and hashes full, small and thumbnail files sequentially. Twelve recordings and their release can therefore verify the same three files thirteen times. Cached source reuse also hashes the original before validating all variants (`CoverAssetStore.cs:57-64,123-128`).

With defaults, a pass checks at most 50 completed jobs, then sleeps at least five seconds. Its theoretical upper bound is under ten completed checks per second once any real work is included. Only 3,000 checks fit in five minutes even with free database and filesystem operations. A 50,000-job catalog needs at least about 83 minutes per sweep. Five minutes is an eligibility delay, not a guaranteed verification interval.

On the user's NAS, repeated whole-file reads and metadata operations are plausible major costs. Actual I/O volume and latency remain unmeasured.

Required change: a shared asset record referenced by entity jobs, batched metadata projections, cheap existence/size checks, and a separately scheduled integrity scrub that hashes each distinct asset once per interval. Keep cryptographic validation on initial publication and when evidence indicates corruption.

### S3 — Maintenance can block all new image processing

Evidence: `CoverAssetJobService.cs:17-28,161-188`.

`RepairAsync` must finish before `ProcessDueAsync`. A large startup prune, slow integrity check or failing directory operation delays otherwise independent new jobs. If maintenance throws every pass, the outer catch skips pending-job processing every pass. Syntactically valid but incomplete manifest JSON can also deserialize with null variants and throw outside the narrow JSON exception handler.

NFS latency matters here: asynchronous method signatures do not make synchronous directory enumeration, file opening and metadata calls cancellable. A hard-mounted filesystem stall can block these operations. A cancellation token alone cannot guarantee recovery from a kernel filesystem stall.

Required change: independent maintenance and processing schedules, per-item failure isolation, explicit manifest schema validation, bounded maintenance time/work per tick and separate health reporting. Do not hold new work behind the entire maintenance pass.

### S4 — Pruning creates catalog-sized memory and I/O spikes

Evidence: `CoverAssetJobService.cs:179-188`; `CoverAssetStore.cs:191-217`.

On the first pass after each process start, and daily afterward, all nonempty job manifests are loaded with `ToListAsync`. Each entity stores its own copy, even when assets are shared. The code then deserializes every manifest and builds a retained-identity set before walking every file and inspecting timestamps. Restarting repeats this work immediately.

This is transient catalog-sized memory, not proof of a permanent heap leak. Directory enumeration itself is streaming; the manifest list is not. It runs before any pending images are processed.

Required change: paged distinct asset references or a dedicated asset table, persisted maintenance progress and incremental directory cleanup. Avoid keeping all duplicated manifest strings alive for a full prune.

### S5 — Several crash leftovers are never collected

Evidence: `CoverAssetStore.cs:97-121,145-156,200-206,234-238`.

Normal exceptions run `finally`, but process death, container kill and failed cleanup can leave files behind. Import temporaries use a 32-character GUID plus `.partial`; pruning requires a 64-character hash prefix. JSON temporaries end in `.asset.json<GUID>.partial` or `.source.json<GUID>.partial`; they match neither accepted final suffix nor `-v1-`. These are skipped forever.

Variant temporaries contain `-v1-`, but pruning skips any file whose hash prefix is retained. A leftover variant temporary can therefore survive indefinitely when that asset remains referenced. Other eligible leftovers wait the full one-year asset retention period.

Required change: a separate temporary-file policy with a short grace period and active-writer protection, covering every generated filename. Test recovery after process death, not just exceptions that execute `finally`.

### S6 — Storage has an age policy but no capacity policy

Evidence: `CoverAssetStore.cs:115-121,191-217`; `CoverAssetContracts.cs:26-40`; `CoverAssetTests.cs:103-108`.

The input is moved into its permanent `.original` filename before rendering succeeds. A valid signature followed by corrupt image content or a renderer failure leaves that original behind. The current failure test checks manifests and `.partial` files, not `.original` files. Content deduplication limits repeated identical failures, but different invalid payloads still accumulate.

There is no total byte/file budget or free-space threshold. Three derivatives and an original are kept per distinct image, and unused managed files default to 365-day retention. This is intentional retention plus missing admission/cleanup policy; it can still fill the NAS.

Required change: distinguish validated originals from rejected inputs, use a short retention for failed/unpublished work, reserve disk capacity before admission, and expose retained/unreferenced/temporary bytes. Define which old public URLs must survive and for how long before deleting successful assets.

### S7 — Rendering decodes each image three times

Evidence: `CoverAssetStore.cs:123-150`; `FfmpegCoverImageRenderer.cs:14-19`.

Full, small and thumbnail generation each starts a new FFmpeg process and reads/decodes the original again. The semaphore bounds render operations globally, but is not a shared operation keyed by content identity. Different source URLs with identical bytes or concurrent uploads can enter with the same missing manifest, both acquire different permits, and render the same outputs concurrently. URL download sharing does not prevent this case, nor does it coordinate multiple server processes.

Required change: decode once and produce all variants through a split filter graph or image pipeline, with one shared render operation per identity. Add a cross-process publication/ownership strategy when multiple API replicas share the directory. Preserve deterministic names and atomic publication.

### S8 — One consumer can cancel another consumer's shared download

Evidence: `CoverAssetStore.cs:39-49`.

The shared Lazy task captures the token of the caller that creates it. Later callers wait on that same operation with their own tokens. Canceling the first caller aborts everyone; canceling a later caller only stops its own wait. Hosted-worker callers normally share shutdown cancellation, so this is primarily a contract/concurrency defect rather than the main current worker bottleneck.

Required change: an operation-owned lifetime with reference-counted consumers, or a bounded shared operation that survives individual callers. Define cancellation behavior explicitly and test both caller orderings.

### S9 — Failures and remote refreshes generate unnecessary work

Evidence: `CoverAssetJobService.cs:119-128,174-175`; `CoverAssetStore.cs:57-89`.

All errors receive exponential retries capped at one hour, with no permanent-failure classification or terminal/quarantined state. A permanent 404, unsupported URL or invalid image retries indefinitely. There is no Retry-After handling or per-origin scheduling. Expired source mappings trigger full unconditional GETs; ETag and Last-Modified are not retained. Stable images therefore transfer their bodies again after expiry when requested by a job.

Required change: classify missing/invalid/transient failures, honor throttling, add manual/metadata-change reactivation and use HTTP validators for refreshes. Separate source freshness from local integrity so successful local content is not treated like a fresh processing job each time.

### S10 — Native memory and queued imports are insufficiently bounded

Evidence: `FfmpegCoverImageRenderer.cs:16-25`; `CoverAssetStore.cs:97-129`; `Program.cs:22-27`.

`-max_alloc 67108864` limits a single FFmpeg allocation, not total process RSS. The [FFmpeg documentation](https://ffmpeg.org/ffmpeg.html) describes it as a limit on an allocation block. There is no server-side input pixel-count check or process memory limit here. Decoder/encoder thread settings do not explicitly set filter thread counts.

The worker has three processing slots, but uploads can write and validate originals before waiting on the renderer semaphore. The semaphore does not bound the number of waiting uploads, retained request state or temporary files. Current startup validation also omits source lifetime, retention and retry interval validation, and checks the lease against one render timeout rather than the whole job.

Required change: bounded admission before reading imports, input dimension limits, explicit subprocess resource policy, bounded diagnostics, and validation of all time/resource settings. Distinguish process memory pressure from a managed-memory leak.

## Additional server logic review

The server-specific follow-up traced response projections, metadata updates and release-to-recording propagation. The following are additional findings, rather than measurements of the deployed server.

### S11 — Playlist playback bypasses completed background downloads

Evidence: `StreamingServiceServer.Business/Services/Library/LibraryService.cs:96-143`, especially the projection at line 131.

GetPlaylistRecordingsAsync chooses Recording.Cover or the playlist row's Cover. It does not use LocalCover or OptimizedCover and does not populate SmallCover/VerySmallCover. For remotely sourced artwork, the worker intentionally keeps Cover as the remote source while putting generated addresses in Local fields. A successfully completed job therefore does not improve this playlist response. Album/library projections use the new selection policy, so behavior differs by screen.

The mobile cache can consequently receive the original remote/full URL with no thumbnail alternatives during playlist playback. This can explain slow artwork even when all relevant background jobs report completed. Generated uploads whose Cover already is local are an exception.

Required change: project all available representations through the same response policy after the database page is selected, preserving playlist-only/unmatched recordings. Add a playlist-specific test; current Library/metadata response tests do not exercise this projection.

### S12 — Release propagation can destroy newer recording artwork

Evidence: `CoverAssetJobService.cs:90-101`; `CoverJobInterceptor.cs:18-32`.

The release worker updates a recording when either its source matches the release source **or its LocalCover matches the release's previous local cover**. The latter condition does not verify the recording's current source or pending job version.

Example ordering:

1. A recording shares cached album image A.
2. Its source is changed to custom image C; its local image remains A until its own job completes.
3. A release job publishes image B. The recording still has LocalCover A, so the release update overwrites its source C with B.
4. The recording's job for C later sees the changed source and switches to B. The intended C selection is lost.

The transaction and lease/version guards protect the release's own job. They do not establish ownership of these sibling recording updates. This is a source-level race scenario, not a regression reproduced in this review.

Required change: explicit inherited-versus-custom artwork ownership, or an equivalent source/version guard on each affected recording. Updating a release must not overwrite independently changed child sources simply because an old cached derivative is still shared.

### S13 — Local preference is disconnected from source freshness and availability

Evidence: `ImageService.cs:16-19`; `CoverPolicy.cs:13-14`; `CoverJobInterceptor.cs:28-32`; `CoverAssetJobService.cs:165-175`.

Scheduling a changed source does not clear or invalidate its previous Local fields. Responses continue selecting the old local image until replacement succeeds, potentially forever if the new source fails. Keeping the previous image during refresh can be a valid product choice, but there is no explicit stale-state policy or source-to-derivative binding in the response selection.

The stronger failure case is a local file removed or unavailable on storage: nonempty LocalCover remains preferred even when it returns 404. The response does not include a separate original-source fallback. Repair has to find and successfully fix that asset before clients can recover. Similarly, changing Cover to empty is skipped by the interceptor; later repair removes the job but does not clear old Local/Optimized fields, so old artwork can remain visible after source removal.

Required change: explicit current/pending/previous asset ownership and healthy/degraded state. Define stale-image behavior, clear derivative ownership when artwork is removed, and expose a usable fallback during repair. Avoid per-response filesystem probes that would move NAS latency into every API request.

### S14 — Smaller source variants are ignored as download fallbacks

Evidence: `CoverJobInterceptor.cs:18-21`; `CoverAssetJobService.cs:54-59,80-83,140-147`; source models/mappers contain SmallCover and VerySmallCover.

Only changes to Cover enqueue work; backfill also requires Cover to be present. The worker captures and fetches only that address. A usable SmallCover with no full Cover is not scheduled. A full image that exceeds the input limit, is unsupported or fails while a smaller representation works has no fallback path. Changing just the small/thumbnail source does not trigger work.

Required change: define the asset's source selection contract. Prefer the best acceptable original, permit a smaller valid source when appropriate, and include all relevant inputs in the source revision. Continue deriving consistent tiers locally, with no upscaling claims when the fallback is small.

### S15 — Queue work follows entity count and has no user-facing priority

Evidence: `CoverJobInterceptor.cs:18-32`; `CoverAssetJobService.cs:35-39,96-101,135-157`; `CoverAssetJob.cs`.

A release and every recording each own a job/manifest. The release can already fill all matching recording Local fields, but their jobs still need claims, source reads, validation and completion. Active URL sharing helps downloads, not that repeated database and disk work. Maintenance later repeats it again (S2).

Backfill can add up to 50 release jobs and 50 recording jobs per pass while processing selects at most 50 pending jobs. The queue has due-time ordering but no distinction between a missing newly needed image, historical backfill, a stale-source refresh and repair of an already displayable image. Visible-cover demand does not reprioritize work. A partially slow batch also holds back the next set of jobs until the entire current batch finishes.

Backfill repeats an anti-join and DateAdded/Id ordering from the beginning each pass. The current model snapshot has no matching DateAdded/Id index. Whether this is a costly scan/sort needs a production-sized EXPLAIN plan; it is not established by source alone.

Required change: one asset/source job with entity references, explicit work classes and bounded fair priority, continuous worker replenishment, incremental indexed backfill and lightweight association updates. Prioritize actually missing covers over periodic refreshes while preventing permanent backfill starvation.

### S16 — Manual refresh can create a large request and enqueue burst

Evidence: `MetadataService.cs:504-547`; `MetadataController.cs:161-166`; `CoverJobInterceptor.cs:22-32`.

The admin refresh-all endpoint waits while every release and its recordings are loaded into one tracked DbContext. It then performs an external metadata lookup for each release sequentially and saves the entire result once at the end. It has no cancellation token parameter or durable checkpoint. This is outside the hosted downloader loop but directly feeds it and can be mistaken for that loop being slow.

Large catalogs retain the whole graph and accumulated changes while waiting on network requests. A failure before the final save loses that pass's progress. The interceptor then looks up jobs for large identifier groups and repeatedly searches local/stored collections per changed entity, adding further CPU and memory cost to a large batch.

Required change: make refresh-all a durable, paged metadata refresh operation with bounded provider concurrency/rate limits, cancellation and progress. Save bounded batches and use dictionary lookups when associating changes with jobs. Do not hold the entire catalog inside an HTTP request.

## Mobile findings

These matter because the app now routes ordinary remote images through the shared RNFS cache. They are not evidence that the server is leaking memory.

### M1 — Failed RNFS downloads leave JavaScript event listeners registered

Evidence: `../StreamingServiceMobile/node_modules/react-native-fs/FS.common.js:499-516,538-544`; adapter `NativeAssetCache.ts:45-61`.

The installed wrapper removes DownloadBegin/DownloadProgress subscriptions only on success. Its rejection handler rethrows without removing subscriptions. Our adapter supplies both callbacks, so each rejected attempt leaves two registered listeners. They retain callbacks/options and receive later download events, even though their job identifier no longer matches. Retries and canceled downloads that reject amplify the leak.

Required change: repair the dependency through a maintained patch or replace this transfer boundary. Remove listeners in every terminal path. An app-level Promise.race does not unregister dependency-owned listeners.

### M2 — Native RNFS downloader dictionaries grow across transfers

Evidence: Android `RNFSManager.java:60,723-735,774-790`; iOS `RNFSManager.m:502-523,553-571`; Android `Downloader.java:19-24`.

Both native managers insert each downloader by job identifier without removing it on terminal success/failure. Calling stopDownload also does not remove the entry. Android downloader objects retain their DownloadParams and callbacks. iOS downloader objects retain parameters/task/session references; successful iOS sessions are invalidated, but the manager's dictionary entry remains. The optional iOS background UUID dictionary is a separate concern; this adapter does not enable background downloads.

This is concrete per-transfer native object retention in the installed dependency. It does not mean each completed image retains its entire bitmap through that dictionary. A device heap profile is needed to quantify retained bytes and release behavior on bridge teardown.

Required change: remove terminal native job entries safely and release callbacks, with synchronization against native cancellation/completion. Keep resumable jobs only when the product explicitly owns them. A success callback can race job registration, so simply adding an unsynchronized dictionary remove is insufficient.

### M3 — iOS cancellation can permanently consume cache slots

Evidence: iOS RNFS `Downloader.m:142-179`; adapter `NativeAssetCache.ts:45-61`; `AssetCache.ts:78-87,123-133,147`.

On iOS, stopDownload uses cancelByProducingResumeData. If resume data exists and no resumable callback was supplied, neither resolve nor reject is called. A non-cancellation error carrying resume data has a similar path. Our adapter does not supply a resumable callback. The native download Promise can remain pending.

Canceling the public asset lease rejects the consumer, but `execute` still awaits the native Promise. The active-download count only decreases when `execute` finishes. Three such transfers can exhaust all image-cache slots; later requests keep queuing. iOS sessions are explicitly invalidated in the success path, not these error paths.

Required change: native terminal settlement on explicit cancellation/nonresumable failure, listener/session/job cleanup, and an adapter watchdog. Fence late writes before freeing a slot and deleting its temporary path. This needs an actual iOS lifecycle check as well as deterministic tests.

### M4 — Image validation performs a full decode under a temporary path

Evidence: `NativeAssetCache.ts:63-73`; installed React Native `ImageLoaderModule.kt:80-106`; `AssetCache.ts:154-166`.

The header check correctly rejects oversized dimensions early, but validation then calls Image.getSize. Installed Android React Native implements that with fetchDecodedImage, not just a header read. The component later loads the moved file under a different URI, so reuse of that decoded cache entry cannot be assumed.

A permitted 16-million-pixel image can need roughly 61 MiB for a four-byte-per-pixel bitmap, before decoder overhead. Three concurrent validations can create substantial native pressure. React Native closes the returned image reference; this call alone is not proof of a permanent bitmap leak.

Required change: native metadata validation without a full-size decode, or a small bounded validation decode that does not pollute the display cache. Use tighter tier-specific limits and measure native memory during scrolling.

### M5 — Full directory maintenance is on the image delivery path

Evidence: `AssetCache.ts:156-180,215-233`; `NativeAssetCache.ts:27-29`.

Every new image waits for the exclusive publication chain, a complete RNFS.readDir and a sort of asset candidates before its lease resolves. Android readDir also reads metadata for every child. All file records cross to JavaScript. Repeating this as the cache fills causes roughly quadratic cumulative directory work before eviction stabilizes size. Even unrelated finished downloads wait behind this maintenance.

Required change: maintain indexed byte/access accounting, reconcile incrementally, publish validated images promptly, and run bounded eviction separately while preserving pins and disk reservations. Do not replace the scan with an unbounded in-memory catalog.

### M6 — Display telemetry repeatedly hashes the request catalog

Evidence: `ImageDownloadService.ts:67-75`; `AssetCache.ts:55-57`; `AssetIdentity.ts`.

For each displayed image, imageDisplayed copies and searches up to 1,000 recent requests. Each candidate calls cache.path, which recomputes the synchronous JavaScript SHA-256 identity. The same lookup pattern exists in invalidation. The observer set is empty unless someone subscribes, but the lookup still runs. This is avoidable JavaScript work during image rendering; its frame-time impact has not been measured.

Required change: carry the resolved identity/path in the lease or maintain a bounded reverse index. Avoid scanning and hashing unrelated requests merely to emit one timing event.

### M7 — The mobile disk budget is not reliably enforced

Evidence: `NativeAssetCache.ts:27-29,33-38`; `AssetCache.ts:78-88,215-233`.

The adapter suppresses directory-listing and unlink errors. Eviction therefore subtracts bytes and reports success even when the file was not deleted. Pins correctly protect displayed files, but releasing the last pin does not schedule eviction. A cache that grew above its soft limit while files were pinned can stay above it until another successful download. Eviction also lacks a repair pass for orphan completion sidecars.

Required change: expose deletion failures, reconcile actual storage, and trigger bounded cleanup after pin release and at startup/idle. Keep the configured soft limit and any hard free-space admission threshold explicit.

### M8 — Preview and cached-image scheduling can delay first display

Evidence: `CachedImage.tsx:39-49`; `ImageDownloadService.ts:49-61`; `AssetCache.ts:123-147`.

The desired tier is queued before its thumbnail, both at visible priority. Slow full downloads can consume slots before previews. Disk-cache reads also use those same three work slots; after a fresh JavaScript runtime, the in-memory preview map is empty, so a cached image can wait behind network downloads even though its file is already on disk. There is no separate disk-hit scheduling path.

Required change: separate bounded cache lookup from transfer slots, prefer a preview when it is needed, and reserve capacity for newly visible images. Measure cold-start and warm-cache time to first image separately.

## Existing safeguards worth keeping

- Metadata and job insertion share a database transaction.
- Job version/lease checks guard publication and roll back stale completion writes.
- Source downloads and worker/render concurrency have partial bounds; the server pending dictionary removes completed tasks.
- Content-derived derivative names, checksums and manifest publication prevent accepting many partial/corrupt outputs.
- Mobile leases, generation checks and component cleanup protect against stale image completion.
- Recent mobile request lookups are capped at 1,000; telemetry stores a latest event, not an unbounded history.
- HTTP responses, files and normal renderer processes are disposed on ordinary completion paths. There is no evidence here of a general missing-dispose leak in the server.

## Recommended implementation order

1. Fix RNFS terminal cleanup and iOS settlement. Add native object/listener counters and cancellation/failure regression coverage before increasing traffic through the cache.
2. Add server total/body deadlines, whole-job lease policy and independent processing/maintenance loops. Ensure one bad manifest or stalled transfer cannot stop unrelated jobs.
3. Collect crash leftovers with a distinct temporary policy; add disk admission and failed-original cleanup.
4. Introduce shared asset verification state and batch metadata reads. Replace per-recording full-file scrubbing and catalog-wide startup materialization.
5. Share renders by content identity and decode once for all variants. Classify failures and use conditional HTTP refreshes.
6. Remove mobile full-decode validation and synchronous telemetry scans; move full-directory eviction away from delivery and prioritize previews/disk hits.

Refactor scheduling, maintenance and transfer ownership. Preserve the working publication, versioning and stale-result guards. Increasing ParallelJobs first would magnify NAS traffic, subprocess memory and duplicate work.

## Verification needed after implementation

These checks are proposed, not executed in this review:

| Scenario | Required evidence |
| --- | --- |
| Headers arrive, body stalls or drips | Deadline terminates the job; unrelated jobs continue; lease is released safely. |
| First/later caller cancels shared source | Remaining consumers finish; no stale publication or unobserved retained operation. |
| Kill process during import/render/manifest write | Startup cleanup removes abandoned temporaries without deleting active work or public assets. |
| Many recordings share one album | Verification/render/download counts follow unique assets, not recording count. |
| Large catalog and slow/unavailable storage | Bounded startup allocation; new jobs progress independently from maintenance; database query count is bounded per batch. |
| Playlist after local cover generation | Full/small/thumbnail response fields point to the owned assets; unmatched playlist rows retain their fallback. |
| Recording custom source changes during release processing | Release propagation preserves the newer custom source and its job. |
| Cover removed, source replaced, or local file unavailable | Response behavior follows the explicit stale/fallback policy; no permanently preferred broken derivative. |
| Full source fails but smaller input is valid | Defined fallback produces acceptable tiers without retrying the same unusable input forever. |
| Refresh-all interrupted midway through a large catalog | Bounded memory and durable progress; completed pages are not lost. |
| Hundreds of failed/canceled/successful downloads | RNFS listener count and terminal native job count return to baseline on Android and iOS. |
| iOS cancel with resume data | Native Promise settles; sessions terminate; slots return to capacity; later images load. |
| Warm disk cache behind slow network requests | Disk hits and previews meet a separately measured first-display target. |
| Full cache and failed unlink | Errors are observable; accounting matches disk; releasing pins schedules cleanup. |
| Large images and repeated scrolling | Native/managed heap, process RSS and JavaScript frame times stabilize after warmup. |

Record server batch time, per-stage time, distinct assets verified, bytes read from NAS, renderer invocations/RSS, active/expired leases, retained/temporary bytes and failure classes. On mobile record active native jobs, listener counts, queued work, native bitmap memory, disk-hit latency and first-thumbnail/full-image latency. Existing aggregate test totals cannot substitute for those measurements.
