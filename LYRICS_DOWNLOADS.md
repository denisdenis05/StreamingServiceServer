# Local lyrics downloads

The server downloads lyrics from LRCLIB without an API key or login. A separate background queue discovers existing and newly imported recordings. The API and audio playback do not wait for lyrics downloads.

Lyrics live in PostgreSQL alongside the local catalog. This deliberately avoids changing audio tags, creating files beside audio, or requiring another writable NAS mount. Download endpoints export the stored text as files. Once a result is stored, serving and exporting it require the local server/database, but no connection to LRCLIB or the public internet.

This change covers the server and ASP.NET dashboard. It does not add lyrics to the mobile player or download lyrics into phone storage. A phone disconnected from the server will need that separate client integration. Line timestamps and the original richer document are preserved for that integration; this server does not estimate timestamps or synthesize word timing.

## Eligibility and matching

The worker reads the same `AlbumAnalysisResult.TrackMatchIssuesJson` used by the dashboard's audio track checks. A `name_mismatch` or `artist_mismatch` for a recording excludes that recording. A length warning alone does not exclude lyrics. This is independent of the optional audio feature/embedding analyzer.

Both the per-album and library-wide track-check actions now persist their results. A recording's old flags are retained when its audio is missing, unreadable, or lacks the title/artist tags needed to recheck the name. A successful recheck replaces that recording's flags and makes its existing lyrics job due again. Malformed old analysis fails closed until all recordings in the album have been checked successfully.

The worker also requires a nonempty title, artist, album and a catalog duration between one second and one hour. Repeated recording artist credits are deduplicated before lookup. The album artist is used as the primary artist only when it is among the recording credits; otherwise the recording credits supply the artist. The full set of distinct credits remains part of the stored metadata revision. Placeholder identities such as `Unknown Artist` are skipped.

Lookup first requests the exact title, artist, album and duration. A genuine missing result or metadata mismatch permits one paced search request using the title and artist. Candidates must match the title, primary artist and duration within two seconds. Matching album candidates take precedence; otherwise conflicting lyric text, timing or instrumental classification is rejected. Search never guesses a different artist. Unicode compatibility, whitespace, case and typographic apostrophes are normalized; title version labels remain significant. Provider failures and malformed exact responses do not trigger another request.

A name such as a legitimate numeric title or an artist containing a slash is not rejected by a filename heuristic. The dashboard's actual mismatch flags determine the audio naming exclusion.

## Durable queue

`RecordingLyrics` holds one job/result per recording. Deleting a recording removes its lyrics. States are `Pending`, `Downloaded`, `Instrumental`, `NotFound`, `Skipped` and `Failed`.

`LyricsQueueStates` holds a database lease and the next provider admission time. This serializes processing across server instances and preserves provider cooldowns across restarts. An expired lease can be reclaimed; an old worker cannot complete a newer lease or release its provider gate. Shutdown releases ownership, and an operation deadline bounds the HTTP request and response body.

An already downloaded or instrumental result with unchanged metadata is reused without another provider request. Changed metadata or a new mismatch flag is checked before downloading and again before saving. Local reads also compare current metadata/flags, so earlier text is not exposed for a changed or flagged recording. The queue checks metadata periodically; stored content is not periodically fetched again from LRCLIB.

A not-found result retries after seven days by default. A lookup-version upgrade makes older not-found and unmatched jobs eligible once, without re-downloading completed results or bypassing naming flags. Transient failures use bounded exponential backoff for the affected recording. `Retry-After` in either seconds or HTTP-date form delays the shared queue, with a one-second safety margin. Failure state and provider cooldown are committed together so a restart cannot lose that delay. A malformed response does not pause unrelated recordings for the failed recording's exponential backoff. A rate-limit response without `Retry-After` uses a one-minute shared cooldown plus the safety margin. Response buffering is limited to one MiB; oversized responses are invalid-response failures for the affected recording. Only safe failure codes enter the queue status.

## API

All lyrics/export routes require the existing application authentication. They read local data and do not contact LRCLIB or wait for a queue job.

- `GET /api/lyrics/{recordingIdentifier}` returns the state, source/provider identifier, `plainLyrics`, `syncedLyrics`, original `lyricsfile`, last update time and any safe reason. Unknown recordings return 404. Unavailable, changed and skipped results do not expose earlier lyrics.
- `GET /api/lyrics/{recordingIdentifier}/download?format=Synced` returns a UTF-8 `.lrc` attachment. This is the default format.
- The same export with `format=Plain` returns `.txt`; `format=Lyricsfile` returns the original document as `.yaml`. A missing format returns 404 rather than exporting another format under the wrong extension.
- `GET /api/dashboard/lyrics` requires the Admin role. It returns catalog/job counts, state counts and a bounded page of jobs, including title, album artist, retry state and reason. It accepts `page`, `pageSize`, optional `state` and optional `search`. It never returns lyrics text.

The existing dashboard queue page shows lyrics below album covers, with state filtering, pagination and name/artist mismatch explanations. It uses the dashboard's existing visible-page polling and cancels stale requests when filters, page, authentication or visibility change.

## Configuration and deployment

Apply `20261120185624_AddRecordingLyricsQueue` and `20261120194359_AddLyricsLookupVersion` before starting this server version. The latter adds a lookup-version column to existing jobs and preserves their stored content. The existing Compose migrations service applies it during the normal deployment flow. No database outside the disposable test cluster was changed during development.

The `Lyrics` configuration section uses the normal ASP.NET environment binding, for example `Lyrics__Enabled`. Defaults are defined in `LyricsOptions`:

| Setting | Default |
| --- | --- |
| Enabled | true |
| DiscoveryBatchSize | 100 recordings |
| DiscoveryInterval | 5 minutes |
| PollInterval | 2 seconds |
| RequestInterval | 1 second minimum between requests and after processing |
| OperationTimeout | 30 seconds |
| LeaseLifetime | 2 minutes |
| MetadataRecheckInterval | 1 day |
| NotFoundRetryInterval | 7 days |
| InitialRetryDelay | 1 minute |
| MaximumRetryDelay | 6 hours |

The lease must exceed the operation timeout. Invalid limits fail startup validation. Disabling the worker stops downloads; local reads/exports remain available. No local appsettings or credentials are required for LRCLIB.

The provider implementation follows [LRCLIB's current API contract](https://lrclib.net/docs), including anonymous access, client identification, duration matching and rate-limit handling. Coverage depends on available matching lyrics; missing or uncertain matches remain unavailable.
