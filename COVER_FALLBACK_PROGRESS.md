# Album artwork fallback — 2026-11-15

## Diagnosis

The deployed worker remains active. Its logs showed 123 cover job failures over two hours, all with three 30-second HTTP timeouts. Some persisted jobs had reached 594 attempts. A direct request to an affected `*.us.archive.org` image timed out from both the server host and its API container; an IPv4 container probe timed out connecting to port 443. Cover Art Archive itself and unrelated HTTPS endpoints remained reachable. The mounted cover directory had 1.3 TB available. This establishes an artwork-host connectivity problem from this deployment, not an Internet Archive-wide outage.

Database inspection used read-only connections and transactions. Ten releases had no artwork source. Those releases are outside this fallback's scope because they have no existing archive download job.

## Implementation

- Added Deezer album search as an independent source without an API key or paid service. Verified the public endpoint and matching JPEG from the deployed API container for Nirvana's MTV Unplugged In New York.
- After the first failed archive image candidate, release and recording jobs try the album fallback before the remaining archive image sizes. Successful downloads use the existing bounded image validation, rendering and transactional publication pipeline, including release-to-recording propagation. Original artwork metadata remains unchanged.
- Match artist and album after Unicode, case and whitespace normalization. Different titles, artists and editions are rejected. Only HTTPS artwork on Deezer's image CDN is accepted. A search match identifies an album; it does not prove identical artwork for a particular physical pressing.
- Limit search responses to 1 MiB, searches to ten seconds, and search starts to one per second. A bounded one-day cache shares successful and empty lookups, including concurrent consumers. Provider errors are not cached as missing artwork.
- Failed fallback attempts retain normal candidate processing and persisted retry behavior. User uploads and unrelated image hosts do not trigger album replacement. Existing cached local assets continue through their usual path.

## Verification and deployment

The full server suite ran against an isolated PostgreSQL cluster on a private `/tmp` Unix socket: 424 passed, one failed. The failure is in unchanged `AudioImportSafetyTests.ImportDestinationCannotEscapeMusicRootOrTraverseDirectoryLinks`: expected InvalidDataException, received InvalidOperationException from MusicLocationHelper. No cover code appears in that failure path.

All 35 focused provider, worker, archive fallback and job integration tests pass, including archive timeouts, successful publication of all three variants, metadata matching, rejection of unrelated sources and untrusted CDN addresses, caching, cancellation, and malformed/error responses. Release publication succeeds with existing compiler warnings.

No production data changes or deployment were performed. The user requested a pull request on 2026-11-16; the fallback implementation, dedicated tests and this report are included in that change. No migration or credentials are required. Existing delayed jobs use the fallback when their next retry becomes due after deployment. Albums with no matching Deezer result retain their normal retry/missing-cover behavior.
