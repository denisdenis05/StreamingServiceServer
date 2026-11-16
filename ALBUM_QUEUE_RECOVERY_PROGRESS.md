# Album queue recovery — 2026-11-16

## Cause and repair

Production investigation found five fully published albums blocked by source deletion: the API runs as UID 1000, while the source directories are root-owned with mode 755. Other imports repeatedly resumed incomplete journals, bypassing provider stall/replacement logic. Loose source scoring accepted The Doors from a 3 Doors Down directory and DAMN. from a sample pack; track-number-only fallback then published unrelated songs under expected recording identifiers. Those selections were reproduced locally using the same source files as the deployed checkout.

- Verified audio and saved metadata can finish when source deletion is denied. The source remains intact, its path is recorded in the journal, and the failure is logged. Changed-source checksum failures and unavailable destination audio still prevent completion. No permissions are broadened.
- Incomplete imports persist their last successful publication time. After 30 minutes without progress they request a replacement source through the existing bounded retry policy. Legacy journals inherit their previous modification time. Replacing a source retains download files and old transfer evidence.
- Source selection requires the artist (including MusicBrainz credit names) and album in the directory path. Download availability or audio quality cannot override identity. Candidates with fewer files than the chosen release's expected tracks are rejected before enqueue.
- Track matching requires compatible titles and available album/artist tags. A matching track number alone is insufficient in either matching entry point. Punctuation-only titles and collaboration credits are handled explicitly.
- Existing pending import receipts receive a one-time audit under the new policy. Rejected, unchanged destination files are moved into `.audio-import-rejected` beneath the music root, preserving them while preventing normal playback under the wrong song identifier. Source files remain intact and incomplete imports request a replacement. Already changed destination files are not moved. Expected catalog identifiers remain unchanged.
- Artist credit names are stored with import snapshots so renamed artists such as Ye/Kanye West can match without accepting arbitrary artists. No database migration is needed.
- The import boundary now converts rejected destination paths into InvalidDataException, matching its established safety contract.

## Validation and rollout

Regression coverage exercises wrong artist/album sources, undersized editions, contradictory tags, track-number-only matching, artist credit names, collaboration tags, punctuation titles, denied source cleanup, incomplete imports across restart, and revalidation/quarantine of legacy receipts. Tests use generated files and a disposable PostgreSQL cluster on a private `/tmp` socket, never production data.

All 445 tests passed with test collections run serially, including every disposable PostgreSQL case. Release publication succeeded. Parallel runs exposed intermittent failures in unchanged playlist/recommendation concurrency and preview timing tests; this repair does not change those features. The import destination exception mismatch observed in the earlier investigation is fixed and its existing regression passes.

The user authorized PR creation and merge into master, followed by `git pull` and `docker compose up -d --build` on `192.168.1.182`. Deployment results and production observations will be recorded after rollout. Unrelated local settings, IDE files and recommendation documents are excluded.

## Production follow-up

PR #5 was merged and deployed at master 934d4a0 with a successful Compose build and healthy API. Four albums blocked by denied source cleanup left the queue: Speaking in Tongues, Black Panther, Brat, and Good News for People Who Love Bad News. Further inspection found Donda's remaining track had a featured-artist suffix, while retained Operation: Doomsday audio had the same credit-format variation. Wrong-release Cigarettes After Sex files and unrelated Doors files remain rejected.

The follow-up accepts explicit feat/ft/featuring title credits while retaining rejection of live/remix versions. Both pending imports and queued albums can recover matching audio from retained quarantine files; those files remain preserved. Queued recovery uses the existing album ownership lock, validates metadata destinations, and removes the queue entry only after verified completion. Completed old journals do not cancel a later explicit redownload. Missing current metadata defers a legacy audit. Transient provider/metadata exceptions keep albums queued for retry instead of marking them unavailable.

Feature mapping: AlbumQueueMatchingTests covers featured-title boundaries and unavailable metadata; AudioImportSafetyTests covers deferred legacy audits and preserved retained audio; QueuedAudioRecoveryTests covers local completion, explicit redownload, journal identity validation, and featured-credit recovery from retained files using disposable PostgreSQL. All 454 tests passed serially; Release publication succeeded. Production rollout and resulting queue state remain to be verified.

## Track retry and stall detection

PR #6 deployed successfully at 369ec40 with a healthy API and HTTP 200 response. Production exposed another independent loop: requesting a missing track, including simply waiting for its retry schedule, reset the album's last-progress timer and skipped the stall checks. A rejected HTTP 500 retry also escaped the album check before stall handling.

Track retry submission now leaves the album progress clock untouched and always proceeds to transfer failure/stall evaluation. Retry HTTP/transport failures are best effort; caller cancellation still propagates. Only actual observed progress resets the existing timer. Sources and transfer evidence remain retained when the existing replacement policy runs. Dedicated SoulseekDownloadRetryTests cover rejected requests, unavailable snapshots, and cancellation. Aggregate verification passed all 473 tests, including the separately staged MusicBrainz queue tests.

Four newly failed album markers caused by transient metadata unavailability were backed up to /tmp/queue-recovery-transient-failures-backup.json and removed transactionally to permit retry: Cigarettes After Sex, HIT ME HARD AND SOFT, Hot Space, The Doors. Existing failed entries were preserved. This does not accept their old rejected audio.
