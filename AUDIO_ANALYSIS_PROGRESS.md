# Local audio analysis

## Chapter 1: durable storage

Implemented on 2026-11-10 in StreamingServiceServer.

Migration `20261110194920_AddAudioAnalysisProcessing` adds three tables and leaves recording/library data intact:

| Table | Purpose |
| --- | --- |
| AudioAnalysisSettings | Singleton processing switch, endpoint, protected token, profile and revision. Processing starts disabled. |
| AudioAnalysisJobs | One durable job per indexed recording, attempts, safe failure category, next attempt, source identity and expiring versioned lease. |
| RecordingAudioAnalyses | One current successful result per recording, source hash, analyzer/model lineage, full features, moods and 200-dimensional embedding. |

The current result is replaced only after successful validation. Failures retain the previous result. There is no growing response/job history and no copy of a MusicBrainz or ListenBrainz database. Source audio stays in the existing music library.

Settings revision and job version are concurrency tokens. Jobs have due-time and lease indexes. Recording foreign keys cascade when a recording is deleted. Features, embedding and model manifests are stored as JSONB; tempo, energy, key, scale and duration also have typed columns. This preserves data for later recommendation work. No similarity search endpoint or vector index is introduced in this chapter.

The six schema tests check migration scope, defaults, keys, bounds, JSONB columns, indexes and concurrency tokens. The integrated worker tests also apply the actual migration to a disposable PostgreSQL database and verify the disabled singleton and empty analysis tables.

Generated EF migration/designer/snapshot output retains framework naming, literals and generated comments. Authored code follows the local quality skill; generated files are the explicit boundary.

## Chapter 2: local AudioMuse adapter

The stock AudioMuse API starts scans through configured music-server providers. It does not expose the per-file upload contract needed here. `tools/audiomuse` supplies a small authenticated adapter around the existing upstream MusiCNN analyzer, pinned to AudioMuse-AI v3.7.1 and its exact source commit. It removes import-time database bootstrap and unrelated accelerator plugin discovery through preimage-checked source adaptations. The analyzer algorithm stays upstream.

Only the two MusiCNN ONNX assets are needed. Their official release hashes and download locations are in the manifest; their combined size is 3,173,194 bytes. Python dependencies and the container image require additional disk space. No full music database, Navidrome instance or stock AudioMuse database is required.

The adapter validates model checksums, accepts opaque audio uploads, serializes inference admission and returns actual features, embedding and model lineage. Authentication, request bounds, temporary cleanup and fixed error categories are covered by 20 deterministic Python tests. These use a fake analyzer and do not establish real inference performance.

`compose.audio-analysis.yaml` is an optional deployment profile with no analyzer host port, a private network, read-only model mount, unprivileged process, bounded temporary storage and CPU/memory limits. Its configuration was validated using dummy deployment values; no service was started. See [adapter setup](tools/audiomuse/README.md).

## Chapter 3: worker, API and administrator controls

The hosted worker admits one job at a time. It discovers indexed recordings in bounded batches, opens only existing supported audio files inside configured music storage and never initiates a preview or download. Files over 512 MiB, unsupported files and missing audio are unavailable. Symlink paths are rejected. SHA256 identifies the actual content; names and library metadata are not sent to the analyzer.

Successful responses must match the attempt UUID, audio hash, profile, protocol and feature schema. Embeddings must have 200 finite values; model hashes, feature types and score ranges are validated before publication. All returned features, model identities and vector values are retained. An unchanged source/profile reuses its current analysis without another inference request.

The default operation timeout is 10 minutes and the lease lasts 12 minutes. Settings are checked every 2 seconds while a job runs. Disabling processing or changing its revision cancels the HTTP operation; a transactional settings-row lock plus lease compare-and-swap prevents a late response from being published. Native inference already running in the separate adapter can continue until it finishes; its late response is discarded. Requests after a provider failure pause for at least one minute, with per-job retry delay growing from one minute to six hours. Expired leases recover after a process restart. Completed and unavailable jobs are checked again after 24 hours, including files downloaded later. Daily checks hash files, so their disk cost still needs measurement on the actual library.

The admin Settings page now has the processing switch, local endpoint, token controls, five job counts, explicit refresh and a saved-connection test. All API endpoints require the Admin role. Empty token input retains the saved secret; explicit removal clears it. Processing cannot be enabled without a token. Concurrent saves require the current revision, and a conflict preserves the browser draft. Password values never come back in settings responses or enter browser storage.

Tokens use ASP.NET Data Protection. Compose persists its keys in the `audio-analysis-keys` volume; the Docker image prepares that directory for its default application user. Back up and restrict access to this key volume with the database. Losing it requires entering the token again. If `SERVER_USER` is overridden, ensure that user can write the key volume. Outside Compose, persist `AudioAnalysis:KeyDirectory` yourself; the development default is `.audio-analysis-keys` under the application content root, excluded from Git. The endpoint is configured by a trusted administrator; it can be an HTTP(S) hostname or private address, without embedded credentials, query or fragment. HTTP redirects are disabled. Use a private network, or HTTPS outside the host.

### Activation

1. Follow [adapter setup](tools/audiomuse/README.md) to download the two checksummed models and build/start the optional local service.
2. Apply server migrations through the existing migration deployment process. Compose runs its migration service before starting the server.
3. Sign in to the ASP.NET dashboard as an administrator and open Settings.
4. Set the local endpoint, such as `http://audiomuse:8786` on the optional Compose network, and its token. Save while processing is off, then use Test saved connection.
5. Enable processing and save. Refresh the counters to observe progress. Disable and save to stop further admissions and cancel the active HTTP operation.

Worker timing and batch defaults can be configured under `AudioAnalysis:Worker`; invalid or nonpositive settings fail startup. The lease must exceed the operation timeout. These are host configuration options; the dashboard switch remains the ordinary control.

### Feature tests

| Behavior | Dedicated evidence |
| --- | --- |
| Schema, disabled defaults and constraints | AudioAnalysisSchemaTests |
| Admin validation, secret retention/removal, concurrent saves and safe errors | AudioAnalysisAdministrationTests |
| Durable claim, expired lease, restart retry, disable/revision fences, cancellation and cooldown | AudioAnalysisJobTests, including real PostgreSQL cases |
| API envelope, request identity, source identity, malformed shapes and feature boundaries | AudioAnalysisClientTests |
| Existing files, unavailable audio, content replacement and symlink rejection | AudioAnalysisSourceTests |
| Dependency wiring, worker option validation, persisted keys and redirect rejection | AudioAnalysisRegistrationTests |
| Dashboard toggle, stale requests, revision conflicts, token lifecycle and account changes | tools/audio-analysis-settings.test.cjs |
| Adapter upload/authentication, cleanup, busy handling, model manifest and source adaptation | tools/audiomuse/tests |

### Final source verification

Commands run from the server repository unless noted:

- `COVER_TEST_POSTGRES=<disposable /tmp socket connection> dotnet test StreamingServiceServer.CoverTests --no-restore --filter FullyQualifiedName~AudioAnalysis -v:q`: 95 passed, zero failed or skipped. This includes actual migration application and PostgreSQL concurrency/recovery tests in isolated databases, removed after each test.
- `python3 -m unittest discover -s tests -v` from `tools/audiomuse`: 20 passed, using a fake analyzer. Initial discovery from the parent adapter folder found no tests; rerunning with the documented tests directory executed all 20.
- `node --test tools/audio-analysis-settings.test.cjs tools/dashboard-polling.test.cjs tools/browser-player.test.cjs tools/cover-progress.test.cjs`: 60 passed, including 14 dedicated analysis-settings tests.
- `dotnet build StreamingServiceServer.sln --no-restore -v:q`: passed. The existing NuGet vulnerability-cache warning remains; an incremental build does not establish a warning-free clean build.
- EF pending-model check: no changes since the migration.
- Optional Compose configuration check with dummy variables: passed. No containers started.
- `git diff --check`: passed.

Implementation and source verification are complete. Models have not been downloaded, no analyzer image has been built and no real track has been analyzed. No production database migration or deployment was performed. Before bulk processing, verify one actual downloaded track, the emitted model/features, measured CPU/memory/disk use, and recovery after switching processing off or restarting the service. Real throughput and the total time for 14,280 tracks remain unmeasured. Recommendation/radio consumers are a separate implementation step; this service populates their source data.
