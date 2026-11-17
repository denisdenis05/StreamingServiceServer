# Soulseek track search fallback

## Completed chapter: discovery and album selection

Normal album searches retain their existing query order and source ranking. When every normal search returns no files, the automatic Soulseek provider searches track titles without including the artist or album, so a blocked artist or album term is not repeated. A normal search with files, even unsuitable files, prevents this fallback. Unavailable search results are not treated as confirmed empty results.

The fallback considers the first three recordings in album position order. One- and two-track releases use only their available recordings. Blank titles are skipped and repeated normalized titles are searched once; neither causes a fourth recording to be searched. Each request uses the same search pacing as normal album queries.

Track results are ranked using the existing artist/album path matching, aliases, audio size/lock/format checks and source scoring. Seed selection requires one audio file rather than the complete album. Each matching peer/directory is expanded once per track search using slskd's peer-directory endpoint. The expanded source is ranked again with the full expected recording count and the configured format order.

Required recordings are matched against remote filenames with the existing track match policy. Each remote file can satisfy only one recording. A source must provide every required recording; unrelated bonus files and duplicate encodings are excluded from the download manifest. The selected files use the existing batch enqueue and partial-transfer recovery flow. Directory errors, rejected enqueues, unsuitable files and empty results advance through candidates and then the remaining seed tracks. Caller cancellation stops discovery and enqueueing.

Exhausted search cleanup now actually sends its DELETE request; previously the helper constructed the request without sending it. Successful searches are retained. Failed fallback searches are cleaned up even when the caller cancels. Cleanup HTTP failures are logged without replacing the search outcome.

The directory adapter follows the upstream [slskd users controller](https://github.com/slskd/slskd/blob/master/src/slskd/Users/API/Controllers/UsersController.cs), using `POST /api/v0/users/{username}/directory` with a `directory` property. It reads the returned directory array, joins bare filenames to their directory, preserves full remote filenames and file properties, rejects files outside the exact requested directory, and escapes usernames.

## Feature-to-test mapping

`StreamingServiceServer.CoverTests/SoulseekTrackSearchFallbackTests.cs` contains 28 deterministic cases with an injected search client, metadata source and search pacing callback. No real searches, delays or downloads are needed.

- Trigger and stopping: `EmptyAlbumSearchesExpandFirstTrackAndEnqueueOnlyRequiredTracks`, `NormalResultsNeverTriggerTrackFallback`, `ResponsesWithoutFilesStillTriggerFallback`, `UnavailableSearchResultsDoNotCountAsAnEmptySearch`, `MissingMetadataDoesNotStartAnySearch`.
- Ordered attempts and boundaries: `UnhelpfulTracksAdvanceToNextTrackUntilSuccessful`, `OnlyFirstThreeTracksAreSearchedEvenWhenFourthHasResults`, `ShortReleasesSearchOnlyTheirAvailableTracks`, `SingleTrackReleaseCanExpandAndDownload`, `DuplicateAndBlankSeedTitlesDoNotCauseRepeatedOrExtraTrackSearches`, `TrackPositionDeterminesSeedOrder`.
- Complete and exact selection: `IncompleteOrWrongTrackDirectoriesCannotSatisfyAlbum`, `LockedOrDisallowedFilesCannotCompleteAlbum`, `OneRemoteFileCannotSatisfyTwoRecordingsWithTheSameTitle`.
- Formats and recovery: `FormatPreferenceIsPreservedWhenDirectoryHasMultipleEncodings`, `DirectoryOrEnqueueFailureAdvancesToNextSeed`, `FailedPreferredFormatEnqueueTriesNextFormatFromSameDirectory`, `CancellationDuringExpansionStopsLaterSearchesAndCleansSeedSearch`.
- HTTP boundary: `DirectoryEndpointEscapesPeerAndScopesRelativeAndFullFilenames`, `DirectoryHttpFailureIsReportedToFallback`, `SearchCleanupSendsDeleteRequestToSlskd`, `SearchCleanupHttpFailureDoesNotReplaceTheSearchOutcome`, `SearchCleanupPreservesCallerCancellation`.

## Verification

Commands run from `StreamingServiceServer`:

- `dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --filter 'FullyQualifiedName~SoulseekTrackSearchFallbackTests|FullyQualifiedName~AlbumQueueMatchingTests|FullyQualifiedName~SoulseekDownloadRetryTests' --no-build --no-restore --verbosity quiet`: 79 passed, none failed or skipped.
- `dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-restore --verbosity quiet`: 394 passed, 107 skipped, none failed, 501 total. The skipped tests retain their existing environment requirements.
- `dotnet build StreamingServiceServer.API/StreamingServiceServer.API.csproj --configuration Release --no-restore --verbosity quiet`: passed, zero errors, 32 existing warnings on the final incremental build (119 on the preceding broader Release build).
- `git diff --check`: passed.

No production database changes, deployment or live peer downloads were performed. Exact peer-directory behavior is covered by simulated HTTP responses; a live slskd smoke test remains unverified. The fallback is automatic queue discovery; manual dashboard searches continue using their existing explicit query behavior. Expanded directory contents are stored in the selected source manifest, not injected into slskd's retained track search results. The dashboard's other-source list therefore still reflects the original track search responses.

## Delivery

Implementation is on `fix/soulseek-track-search-fallback`, created from fetched `origin/master` at `9c69de6`. Unrelated settings, recommendation documentation and IDE files are excluded. Commit the completed chapter, push the task branch and create a pull request targeting `master`. HTTPS Git push currently fails because no GitHub credentials are configured; delivery requires authenticated GitHub access.
