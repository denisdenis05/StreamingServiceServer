# Soulseek individual track retries

## Behavior

The existing flow searches for an album, ranks directories, submits the selected files to slskd and saves their exact names and sizes in the pending source identity. The pending worker checks progress before importing the album. Previously, an album containing both successful and failed tracks stayed incomplete until the 30-minute stall timer requeued the whole album. Album retries stopped after three requeues.

The worker now checks for failed or missing selected tracks before album failure and stall handling. It submits one eligible track per check, using the original username, remote filename and size. Completed and active tracks are retained. Individual recovery has no attempt limit and keeps the album pending while failures or missing files remain. The strict all-files verification for an explicit dashboard source switch remains in place.

Submissions for an album are at least 30 seconds apart. A repeatedly failing track waits 30, 60, 120, 240 and then 300 seconds between attempts. Checks run on the existing worker interval, so actual delays may be longer. Each attempt consumes its delay before making the HTTP request, including when slskd returns HTTP 429. Other eligible tracks can be submitted while one track is waiting.

Initial album selection retains a partially queued source instead of abandoning its successful tracks. It also retains a source if failed transfers are visible, allowing recovery when every selected track has failed. If the request fails and no selected transfer can be verified, candidate selection still proceeds as before.

Failure detection now includes slskd's rejected, timed-out and aborted states as well as errors and cancellations. These terminal failures carry the Completed flag; they must not count as successful album completion.

Retries obtain the existing album lock and verify the pending source and import identifier before contacting slskd. Each retry reads a fresh transfer snapshot. A source switch starts a new retry schedule, and removed queue entries lose their in-memory schedules. No database migration is required.

## Upstream check

The upstream [enqueue controller](https://github.com/slskd/slskd/blob/master/src/slskd/Transfers/API/Controllers/TransfersController.cs) can return HTTP 201 with both successful and failed files. Its [download service](https://github.com/slskd/slskd/blob/master/src/slskd/Transfers/Downloads/DownloadService.cs) supersedes terminal attempts when a file is resubmitted and protects active attempts. Recovery therefore submits the individual file without deleting transfer records or files. The [state categories](https://github.com/slskd/slskd/blob/master/src/slskd/Transfers/Types/TransferStateCategories.cs) define the terminal failure states used by this fix.

## Feature tests

`StreamingServiceServer.CoverTests/SoulseekDownloadRetryTests.cs` covers:

- Partially queued initial albums, wholly failed transfers and complete rejection without visible transfers.
- Strict source-switch verification after a partial enqueue.
- Individual retries for errors, both cancellation spellings, timeouts, rejections and aborts.
- Missing files, legacy filename reconstruction and healthy duplicate attempts.
- Retaining completed and unrelated transfers without DELETE requests.
- HTTP 429 cooldown and later recovery, transfer-snapshot failure and cancellation.
- Increasing delay, the five-minute cap, continued attempts past the former album limit and scheduling another eligible track.
- Preventing partial albums from being reported complete and stopping recovery after every selected track succeeds.

## Verification

- `dotnet test StreamingServiceServer/StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --filter FullyQualifiedName~SoulseekDownloadRetryTests --no-restore --verbosity quiet`: 23 passed, none failed or skipped. This builds the referenced server projects; existing compiler warnings remain.
- `dotnet test StreamingServiceServer/StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-build --no-restore --verbosity normal`: 289 passed, 90 skipped, one failure in `AudioImportSafetyTests.ImportDestinationCannotEscapeMusicRootOrTraverseDirectoryLinks`. The same test fails in isolation: it expects `InvalidDataException`, while `MusicLocationHelper.GetRelativeFolderPathForRecording` throws `InvalidOperationException` for the rejected path. Those files are unchanged by this task.
- `git -C StreamingServiceServer diff --check`: passed.

No live Soulseek transfers, deployment or production database changes were performed. Tests use simulated HTTP responses and an injected clock. Database-dependent tests were skipped by the existing suite configuration.

## Limits and next steps

Retries use the selected peer; they do not search other peers for individual tracks. A permanently unavailable peer can therefore remain pending indefinitely. Album removal stops recovery, and source switching redirects it to the replacement source. Existing handling of stalled active transfers remains in place when there are no failed or missing tracks.

Retry timing is in memory. A server restart resets delays, but the persisted pending source manifest lets recovery resume. Legacy source identities can recover visible failed tracks; they cannot reconstruct files absent from both the manifest and transfer list. Recovery requires the Soulseek provider to remain enabled and reachable.

Deploy the updated server, then verify an album with mixed successful and rejected transfers against a real slskd instance. The unrelated import-path exception mismatch remains open.
