# Lyrics downloads progress

## Scope and branch

Implemented on `feat/local-lyrics-download-queue`, created from freshly fetched `origin/master`. The user clarified that exclusion means the existing ASP.NET dashboard's audio name/artist mismatch flags, not guessed filename quality. The server owns this queue.

The mobile repository has no `master`; its current native player lives on an unmerged feature branch while `main` is older. No mobile branch, playback engine, device storage or player UI was changed. This chapter supplies the server integration and preserves timestamp data for a future client change.

User-owned changes to appsettings, recommendation documents, README, `.idea` and `SOURCE_DATA_DATABASE_DESIGN.md` are outside this change and must remain unstaged.

## Implemented

- Anonymous LRCLIB adapter with identified requests, exact metadata validation, duration tolerance, response limits, preserved plain/timed/richer lyrics and instrumental handling.
- Persistent per-recording jobs/results and a shared database admission gate. Bounded discovery, deduplication, cross-worker serialization, restart recovery, lease fencing, cancellation, deadlines, backoff and persisted provider cooldowns.
- Existing dashboard audio naming flags exclude lyrics. Per-album checks now persist their flags; both check paths preserve old mismatches for audio that cannot be rechecked and wake affected lyrics jobs after successful checks.
- Metadata/flag checks before lookup, after lookup and when serving locally prevent stale lyrics from being exposed. Stored matching results need no provider call.
- Authenticated local JSON and UTF-8 LRC/text/YAML exports; Admin queue status with bounded paging and filtering.
- Lyrics queue panel in the existing ASP.NET queue view, with clear skipped reasons, polling, pagination, authentication clearing and stale-response cancellation.
- Additive EF migration `20261120185624_AddRecordingLyricsQueue`. [LYRICS_DOWNLOADS.md](LYRICS_DOWNLOADS.md) documents behavior, routes, settings and deployment.

## Feature-to-test mapping

| Functionality | Dedicated checks |
| --- | --- |
| Token-free encoded lookup and retained formats | `LyricsProviderTests.AnonymousLookupEncodesMetadataAndPreservesAllDownloadedFormats` |
| Exact title/artist/album/duration boundaries | `LyricsProviderTests.WrongTitleArtistAlbumOrDurationCannotDownloadLyrics`, `CaseWhitespaceAndDurationBoundaryAreAcceptedWithoutRemovingVersionLabels` |
| Existing dashboard flags, unrelated tracks and length warnings | `LyricsProviderTests.DashboardFlagsSkipNameAndArtistMismatchesOnly`, `LyricsIntegrationTests.TrackLengthFlagDoesNotExcludeLyricsAndMetadataChangesHideStaleText` |
| Persistent flags and correction | `LyricsIntegrationTests.PersistedDashboardFlagImmediatelyHidesEarlierLyricsAndRequeuesAfterCorrection`, `UnreadableOrMissingAudioCannotEraseAnExistingDashboardMismatch`, `PartialTagCheckRetainsPreviousFlagAndAddsNewMismatch`, `MalformedPreviousAnalysisIsRetainedUntilEveryTrackIsRechecked` |
| Bounded discovery, deduplication and discovery pacing | `LyricsIntegrationTests.DiscoveryIsBoundedIdempotentAndIncludesExistingAndNewRecordings`, `LyricsWorkerTests.EmptyQueueDiscoveryIsThrottledUntilItsConfiguredInterval` |
| Competing workers, restart cooldown and old leases | `LyricsIntegrationTests.SeparateWorkersCannotClaimOverlappingProviderRequests`, `SharedCooldownSurvivesStoreRecreationAndDelaysOtherSongs`, `ExpiredLeaseRecoversAndRejectsLateCompletionsAndLateGateRelease` |
| Mid-download metadata/flag changes | `LyricsWorkerTests.NewFlagDuringDownloadDiscardsTheResponse`, `ChangedTitleDuringDownloadDiscardsTheResponse` |
| Offline reuse, JSON and all file formats | `LyricsWorkerTests.ExistingLocalLyricsAreReusedWithoutAnExternalConnection`, `LyricsIntegrationTests.LocalJsonAndFileExportsWorkWithoutAnyRegisteredExternalProvider` |
| Not found, instrumental, invalid responses and bounded bodies | Dedicated `LyricsProviderTests` and `LyricsWorkerTests` cases |
| Transport errors, Retry-After forms and shared cooldown | `LyricsProviderTests.ProviderCooldownIsReadBeforeParsingAnErrorBody`, `ProviderCooldownSupportsAnHttpDate`, `LyricsWorkerTests.RateLimitCooldownDelaysTheWholeQueueRatherThanOnlyTheFailedSong` |
| Cancellation, deadline, duplicate processing and retry limits | `LyricsWorkerTests.ShutdownCancelsDownloadAndReleasesItsLeaseWithoutAStoredFailure`, `OperationDeadlineCancelsDownloadAndStoresATimeoutRetry`, `ConcurrentProcessingCallsCannotDownloadTheSameJobTwice`, `RetryDelayIsBoundedForInitialNegativeAndVeryLargeAttemptCounts` |
| API roles, invalid formats/states, search/paging, cascade removal and migration | Dedicated `LyricsIntegrationTests` cases |
| Composition, identification, redirects and invalid settings | `LyricsRegistrationTests` |
| Dashboard rendering, filtering, pagination, escaping, logout and stale responses | `tools/lyrics-queue.test.cjs` |

## Verification

The isolated PostgreSQL cluster uses `/tmp/streaming-lyrics-postgres.5ttnt3jl/socket`, port 56441. The existing fixture creates and drops its own uniquely named test databases. No production database or deployed media was used.

Final checks on the completed source:

- `COVER_TEST_POSTGRES='Host=/tmp/streaming-lyrics-postgres.5ttnt3jl/socket;Port=56441;Database=postgres;Username=denis' dotnet test StreamingServiceServer.CoverTests --no-restore --filter FullyQualifiedName~Lyrics --verbosity quiet`: 65 passed, none failed or skipped.
- `node --test tools/*.test.cjs`: 81 passed, including 10 new lyrics panel tests and existing dashboard, cover and browser-player tests.
- `dotnet publish StreamingServiceServer.API --configuration Release --no-restore --output /tmp/streaming-lyrics-publish --verbosity quiet`: exit 0. Existing nullable and dependency-version warnings remain; no warnings refer to the new lyrics sources.
- `dotnet ef migrations has-pending-model-changes --no-build --project StreamingServiceServer.Data --startup-project StreamingServiceServer.Data`: exit 0, no pending model changes.
- `node --check StreamingServiceServer.API/wwwroot/js/lyrics-queue.js` and the same check for `dashboard.js`: passed.
- A temporary console outside the repository called the actual `LrclibLyricsProvider` anonymously for Coldplay / Yellow / Parachutes, duration 269 seconds. The result contained both plain and synchronized lyrics. No lyrics text or credentials were printed or saved in the repository. This is a provider smoke check, not library-wide coverage evidence.
- `git diff --check`: passed.

The broad server run uses the same PostgreSQL environment and `dotnet test StreamingServiceServer.CoverTests --no-build --no-restore --logger 'console;verbosity=normal' --logger 'trx;LogFileName=/tmp/lyrics-final-server-tests.trx'`. It completed with 565 passing and one failing test: `AudioRecommendationLogicTests.ConcurrentDailyRegenerationHasOneWinnerAndConcurrentSaveCreatesOnePlaylist` returned two different playlist identifiers at its existing assertion. That test passed in an isolated recheck. An earlier full run before the last added cases passed all 565 tests then present.

For comparison, an untouched `git archive origin/master` was built and tested in `/tmp/streaming-lyrics-master-baseline`. Its full suite had 500 passing tests and one different existing concurrency failure: `LikedSongsPlaylistTests.ConcurrentSelectionsCannotLeaveTwoDesignatedPlaylists` received a wrapped PostgreSQL serialization failure. The recommendation/liked-song implementations and their tests are unchanged by this PR. These are intermittent broad-suite failures, not a claim that the complete historical suite is consistently green. All 65 lyrics cases passed in the final broad run as well as their dedicated run. The PR must report this limitation.

The migration, its designer and EF model snapshot are generated framework metadata, kept under the existing `Data/Migrations` boundary. Their generator-owned schema strings and boilerplate are separate from the authored services, models, controllers and tests. The generated migration was reviewed for additive changes limited to the two lyrics tables, their foreign key and due-work index. New authored code has no comments and uses descriptive names and owned protocol/configuration constants.

Host tests and static dashboard checks do not establish a deployed migration, real browser rendering, physical-device playback or phone-side offline storage. Nothing was deployed, merged or switched to another playback engine.

## Delivery

Implementation commit: `ae51dae`. The final validation commit centralizes the dashboard mismatch field names and records the checks. The user supplied GitHub authentication through a private credential card after the initial HTTPS push reported missing credentials. Publish `feat/local-lyrics-download-queue` and create its PR against `master`; no deployment or merge is authorized. The disposable PostgreSQL instance was stopped after validation. Phone-side lyrics remain separate work.
