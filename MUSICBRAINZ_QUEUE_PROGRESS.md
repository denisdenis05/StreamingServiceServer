# MusicBrainz request queue

## Diagnosis

The metadata client reserved its rate-limit slot once before entering its retry loop. Retried requests therefore bypassed that limiter. Cover archive fallback metadata used a second, independent limiter. Search, catalog refresh, download metadata and cover fallback could therefore overlap and collectively exceed MusicBrainz's per-IP request budget.

MusicBrainz documents three reasons for HTTP 503: application throttling, source-IP throttling and global server load. Its normal source-IP budget is an average of one request per second. The existing log labelled every 503 as a rate limit without collecting upstream response details. The local pacing bugs are established from code; the exact upstream reason for each observed production 503 remains unverified.

Source: [MusicBrainz rate limiting and User-Agent requirements](https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting).

## Implemented

- API dependency injection provides one singleton queue to scoped MusicBrainz services and the singleton cover archive fallback. Constructor compatibility uses a shared default queue when called outside dependency injection.
- A bounded FIFO channel accepts up to 128 pending requests. One worker sends requests serially, with at least 1.1 seconds between attempt starts. Queue overflow throws a retryable HTTP exception instead of accumulating unbounded callers.
- All attempts, including retries, pass through that worker. HTTP 429 and 503 apply one shared cooldown. Retry-After delta and date values extend the normal incremental backoff, including after the final failed attempt. Transport failures and HTTP client timeouts retry, with four attempts maximum.
- Exhausted HTTP 429/503 propagate as failures rather than empty metadata. Other non-success metadata behavior, including actual 404 responses, stays unchanged.
- Cancellation works before enqueueing, while queued, during pacing and during transport. Late transport responses are disposed when their caller has cancelled. Stopping the queue cancels outstanding work. Successful responses stay owned by callers; retry responses are disposed.
- The fallback User-Agent identifies the repository instead of using a placeholder contact address. Existing configured User-Agent values remain authoritative. No API keys or paid sources were added.

## Feature tests

All tests are in `StreamingServiceServer.CoverTests/MusicBrainzRequestQueueTests.cs` and use an injected clock and transport. They do not contact MusicBrainz or production.

| Behavior | Dedicated tests |
| --- | --- |
| FIFO, no overlap and pacing | `ConcurrentRequestsRunInFifoOrderWithoutOverlapAndWithGlobalSpacing` |
| Shared retry cooldown, delta/date Retry-After, 429/503 | `RetriesHonorRetryAfterAndCooldownAlsoDelaysOtherCallers`, `HttpDateRetryAfterPausesTheWholeQueueAfterFinalFailure` |
| Retry bound and transport recovery | `TransportFailuresRetryWithinTheSameQueueAndPreserveTheAttemptBound`, `TimeoutRetriesButAnUnsuccessfulFinalResponseIsDisposed`, `UnexpectedTransportFailureDoesNotKillTheQueueOrRetryInvalidWork` |
| Queued, active, pacing and pre-cancelled work | `CancelledQueuedRequestsNeverReachTransportAndDoNotBlockFollowingRequests`, `CancellingAnActiveRequestCancelsTransportWithoutRetrying`, `CancellationDuringPacingDelaySkipsTransportAndPreservesTheNextSlot`, `PreCancelledRequestsAreRejectedWithoutInvokingTransport` |
| Response lifetime and stale completion | `SuccessfulResponsesRemainOwnedByTheCallerAndNotFoundIsNotRetried`, `CancellationDisposesALateTransportResponseWithoutAffectingTheNextCaller` |
| Capacity and shutdown | `QueueRejectsOverflowInsteadOfAccumulatingUnboundedWaiters`, `StoppingQueueCancelsActiveAndPendingRequestsAndRejectsNewWork` |
| Shared callers and metadata failures | `MusicMetadataAndCoverMetadataUseTheSameQueue`, `MetadataServicePropagatesExhausted503InsteadOfReturningMissingMetadata` |

## Validation

Command:

```sh
dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-restore --filter FullyQualifiedName~MusicBrainzRequestQueueTests --verbosity quiet
```

Result: 16 dedicated queue tests passed. The complete serialized suite passed all 473 tests using disposable PostgreSQL, and API Release publication succeeded. Builds emitted existing repository warnings. No test contacted production or MusicBrainz.

## Boundaries and next steps

The queue coordinates this API process. It cannot coordinate unrelated applications or multiple replicas sharing a public IP. A queue also cannot eliminate upstream global overload. Those cases can still produce 503, and the shared cooldown prevents this API from immediately adding retries to the load.

The queue change is prepared on fix/musicbrainz-global-request-queue from origin/master after the independent album recovery fixes. Implementation and tests are separate chapter commits. The deliverable is a PR targeting master; the queue has not been deployed.
