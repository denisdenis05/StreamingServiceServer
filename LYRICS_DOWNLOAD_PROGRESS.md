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


## Correction chapter: catalog artist credits and lookup recovery

Branch `fix/lyrics-search-fallback` starts from freshly fetched `origin/master` at `6ee9245`, after PR #10 was merged. The original tests and one Coldplay smoke check missed duplicated credits in the actual catalog. They did not establish that the six user-reported recordings worked.

A read-only production catalog query showed repeated credits, including `Nelly Furtado & Nelly Furtado` and `Ty Dolla $ign & 2 Chainz & 2 Chainz & Ty Dolla $ign`. Those values were sent to LRCLIB and caused the not-found results. The dashboard displayed the release artist instead, hiding the discrepancy. No production rows were changed during this diagnosis.

Before the fix, the dedicated catalog regression `RepeatedArtistCreditsDoNotCorruptTheLookupArtist` failed, as did the dashboard regression `missing lyrics status is shown once rather than repeated as its own reason`. Logs are `/tmp/lyrics-credit-regression-before.log` and `/tmp/lyrics-display-regression-before.log`.

Three independent council reviewers examined matching, queue safety and validation. Their initial reviews found inadequate coverage and additional recovery/concurrency gaps. The corrections cover canonical credits, a bounded paced search fallback, conservative candidate selection, a versioned retry upgrade, atomic failure/cooldown persistence and duplicate status rendering. Final scores and completed verification are recorded below after the checks finish.

### Live catalog/provider verification

An external scratch console loaded each of the six actual catalog recordings through `LyricsCatalog` in an explicitly read-only database transaction, rolled it back, then called the registered LRCLIB adapter. It did not start the background worker or write production results. It logged metadata, result kind, provider identifier and request count; it did not print or save lyric text or credentials.

| Recording | Corrected lookup artist | Actual provider outcome | HTTP requests |
| --- | --- | --- | --- |
| Girl’s Best Friend | 2 Chainz | Synchronized lyrics, provider 2527493 | 1 |
| No Hay Igual | Nelly Furtado | Synchronized lyrics, provider 88762 | 1 |
| Little Homies | Vince Staples | Synchronized lyrics, provider 11099139 | 1 |
| He’s Got the Whole World in His Hands | Nina Simone | Synchronized lyrics, provider 23216871 | 1 |
| Kisser | Unknown Artist | Skipped: invalid metadata | 0 |
| Acrid Avid Jam Shred | Aphex Twin | Instrumental, provider 18706779 | 1 |

Command: `dotnet run --project /tmp/streaming-lyrics-catalog-review/Review.csproj --verbosity quiet`. Evidence: `/tmp/lyrics-corrected-live-catalog.log`. These are live lookup outcomes, not deployment or persistent-queue execution evidence. The local routes and persistence are verified independently with the disposable PostgreSQL fixture.

### Correction feature-to-test mapping

| Behavior | Dedicated regression coverage |
| --- | --- |
| Duplicate credits, primary/featured identity, stable ordering, credit revision changes | `LyricsArtistPolicyTests`, `LyricsIntegrationTests.RepeatedArtistCreditsDoNotCorruptTheLookupArtist`, `DuplicatePrimaryAndFeaturedCreditsKeepPrimaryAndFenceCreditChanges` |
| Empty credited name uses linked artist | `LyricsIntegrationTests.BlankCreditNameUsesItsLinkedArtistRatherThanAlbumArtist` |
| Legacy single-artist revision compatibility | `LyricsArtistPolicyTests.SingleArtistRevisionRetainsLegacySerializedIdentity` |
| Exact response mismatch requires bounded search | `LyricsProviderTests.MismatchedExactResponseRequiresSearchConfirmation`, `MissingExactResultSearchesAndAcceptsAnUnambiguousAlternateAlbum`, `MatchingExactResultNeverSearchesOrWaits` |
| Search album preference, conflicting text/timing/classification, richer consistent text | `LyricsProviderTests.SearchPrefersMatchingAlbumOverConflictingOtherAlbum`, `ConflictingViableSearchLyricsAreRejected`, `ConflictingInstrumentalClassificationIsRejected`, `EquivalentPlainAndSyncedCandidatesPreferAvailableTiming` |
| Artist, title/version, duration, apostrophes and unknown identity | `LyricsProviderTests.SearchNeverRelaxesArtistTitleVersionOrDuration`, `SearchDoesNotCrossPerformanceVersionAlbumLabels`, `FeaturedArtistAloneCannotReplaceTheKnownPrimaryArtist`, `TypographicApostrophesMatchStraightApostrophesAndSearchUsesStraightForm`, `UnknownArtistMetadataNeverContactsProvider` |
| Pacing, injected clock, cancellation, no fallback on error, bounded JSON and oversized-response classification | `LyricsRequestDelayTests`, `LyricsProviderTests.CancellationDuringRequestSpacingPreventsSearch`, `ExactProviderFailureNeverTriggersSearch`, `InvalidExactJsonNeverTriggersSearch`, `NullSearchEntriesAndOversizedCandidateListsAreRejected`, `HtmlAndOversizedResponsesAreNotAccepted`, `RateLimitWithoutRetryHeaderStillRequestsASharedCooldown` |
| Existing failed jobs retry once, good/excluded jobs preserved, crash recovery, stale failure fencing | `LyricsQueueRegressionTests.OldMissingAndUnmatchedResultsRetryOnceWithoutWaitingForTheirPreviousSchedule`, `UpgradeDoesNotRescheduleGoodResultsNamingExclusionsInvalidMetadataOrTransientFailures`, `CrashedUpgradeClaimRecoversAtLeaseExpiryAndRejectsTheOldOwnersFailure` |
| Atomic failure/cooldown, rollback, unrelated recording backoff | `LyricsQueueRegressionTests.FailurePersistsSharedCooldownBeforeReleaseAndOldReleaseCannotShortenIt`, `FailedRecordingUpdateRollsBackTheProviderCooldownInTheSameTransaction`, `PerRecordingBackoffAllowsOtherDueSongsAfterTheProviderRequestInterval` |
| Populated previous schema migration retains content and admits old misses | `LyricsQueueRegressionTests.MigrationUpgradesPopulatedPreviousSchemaWithoutDiscardingLyricsAndDiscoveryStillWorks` |
| Status reason displayed once and ambiguity explained | `tools/lyrics-queue.test.cjs`: `missing lyrics status is shown once rather than repeated as its own reason`, `conflicting matches explain why lyrics were skipped` |

### Final correction verification

- Disposable PostgreSQL remains the socket/port above; all database cases ran, with no skipped tests.
- `COVER_TEST_POSTGRES='Host=/tmp/streaming-lyrics-postgres.5ttnt3jl/socket;Port=56441;Database=postgres;Username=denis' dotnet test StreamingServiceServer.CoverTests --no-restore --filter FullyQualifiedName~Lyrics --verbosity quiet`: **112 passed, zero failed/skipped**, after the final oversized-response classification correction. Evidence: `/tmp/lyrics-correction-feature-tests.log`. That specific strengthened regression failed before the correction in `/tmp/lyrics-oversized-regression-before.log`.
- `node --test tools/*.test.cjs`: **83 passed, zero failed/skipped**, including 12 lyrics dashboard tests. Evidence: `/tmp/lyrics-correction-browser-tests.log`.
- `dotnet publish StreamingServiceServer.API --configuration Release --no-restore --output /tmp/streaming-lyrics-correction-publish --verbosity quiet`: exit 0. Existing unrelated nullable/dependency warnings remain. Evidence: `/tmp/lyrics-correction-release-publish.log`.
- `dotnet ef migrations has-pending-model-changes --no-build --project StreamingServiceServer.Data --startup-project StreamingServiceServer.Data`: no pending changes. The new migration is additive generated EF metadata; generated schema literals/comments remain within the framework boundary described above.
- `node --check StreamingServiceServer.API/wwwroot/js/lyrics-queue.js` and `git diff --check`: passed.
- Final broad run: `COVER_TEST_POSTGRES='Host=/tmp/streaming-lyrics-postgres.5ttnt3jl/socket;Port=56441;Database=postgres;Username=denis' dotnet test StreamingServiceServer.CoverTests --no-restore --logger 'console;verbosity=normal' --logger 'trx;LogFileName=/tmp/lyrics-correction-final-full-suite.trx'`: **613 passed, zero failed/skipped**. Evidence: `/tmp/lyrics-correction-final-full-suite.log`. An earlier broad run during this correction had 609 passing/one failing test at the unchanged `AudioRecommendationLogicTests.ConcurrentDailyRegenerationHasOneWinnerAndConcurrentSaveCreatesOnePlaylist` assertion, the same intermittent failure recorded in the original chapter. The final passing run does not establish that unrelated concurrency test is consistently reliable.
- Final council reviews: matching **9/10**, queue **9/10**, independent validation **9/10**; no blocking findings remained. Reviewers were asked for candid scores and findings without being told a target score.

No live migration, deployed worker recovery, actual browser rendering, mobile playback or phone-side offline storage was verified. No deployment or merge was performed. User-owned settings/recommendation changes remain unstaged.

The final review also caught a provider admission race when metadata changed during a lookup: atomic failure persistence used the pre-lookup admission deadline. The worker now calculates the next provider admission from the post-lookup clock. `ChangedTitleDuringDownloadDiscardsTheResponse` advances injected time during lookup and checks the atomic failure deadline separately from final lease release. This strengthened test failed before the fix (`/tmp/lyrics-metadata-pacing-regression-before.log`); the final 613-test full run and Release publish include the correction. The queue council re-reviewed it and retained 9/10 with no blocking findings.
