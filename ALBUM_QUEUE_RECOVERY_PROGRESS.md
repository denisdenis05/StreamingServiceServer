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
