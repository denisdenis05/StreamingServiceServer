# Audio recommendation evaluation

Offline prototype for testing stored analysis vectors and radio selection. It is not registered in the API or connected to the player. Database access uses the existing server configuration without printing its connection string. No account, title, artist name, playlist or listen data is selected.

## Read-only snapshot

From the server repository:

```bash
dotnet run --project tools/audio-recommendations/Export.csproj -- \
  --configuration-file StreamingServiceServer.API/appsettings.json \
  --output /tmp/audio-analysis-features.jsonl
```

The exporter sets `default_transaction_read_only=on` in connection startup options, uses a repeatable-read transaction explicitly marked read-only and rolls it back after reading. Statement timeout is 20 seconds; lock timeout is one second. It selects at most 20,000 feature rows and aggregate job/coverage/model counts. It never reads the settings/token table or invokes the worker, migrations, analyzer or playback API.

Feature rows contain embeddings, descriptors, model identities and snapshot-local integer groups for recording, content hash, album and artist. Names, real recording identifiers, content hashes, filesystem paths, account identifiers and histories are not exported. The JSONL file is private local evaluation input and is ignored by Git. Group numbers are not stable across snapshots and should not be displayed or reused as application identifiers.

A feature snapshot still describes a private library. Keep it local and delete it after evaluation. Console output contains aggregate counts and public model identities. Failure output contains only exception type and SQL state, not messages or connection details.

## Offline evaluation

Use a Python environment with the measured NumPy version in `requirements.txt`:

```bash
OPENBLAS_NUM_THREADS=1 python3 tools/audio-recommendations/evaluate.py \
  /tmp/audio-analysis-features.jsonl /tmp/audio-analysis-results.json
python3 -m unittest discover -s tools/audio-recommendations -p 'test_*.py' -v
```

The evaluator removes repeated content hashes, rejects mixed model cohorts and samples 256 reproducible seeds. It compares raw cosine, centered cosine, stored descriptor cosine, restored descriptor cosine, a provisional hybrid, and random selection. Same-album and same-artist hits are proxy measures, not a relevance judgment. The cross-album artist measure excludes the seed album and includes only seeds with another album by that artist available.

For pinned AudioMuse 3.7.1, the final descriptor sigmoid is reversed in memory using `log(p / (1 - p))`. This recovers the upstream average patch score before its outer sigmoid. It does not recover individual patch predictions, calibrate probabilities or change stored scores. The evaluator checks the known engine version and expected value range before applying this transformation. Other source/model revisions require separate verification.

The radio prototype takes a candidate pool of 300, excludes seed content and album, excludes clips shorter than 45 analyzed seconds, applies at most two tracks per release artist and one per album, and penalizes vector similarity to already selected tracks. These are prototype policies, not production defaults. Release artist identity is used; featured artist credits are not evaluated. Sparse candidate sets return fewer tracks instead of violating caps.

All timing is local, warm-matrix NumPy time on this workspace. Hybrid timing reuses component scores already computed by the comparison loop and cannot be compared as a complete query latency. Radio timing covers candidate filtering/diverse selection after scores exist. No API, network, authorization, database reload or mobile latency is included.

`measured-results.json` contains aggregate results from the 2026-11-13 snapshot. It contains no individual recommendations or private identifiers. The full findings and next implementation steps are in [the evaluation report](../../AUDIO_RECOMMENDATION_EVALUATION.md).

## Tests

The dedicated tests cover normalization, content-hash deduplication, model-cohort isolation, invalid vectors, descriptor vocabulary/range checks, stable ranking and exclusion of nonfinite candidates, inverse-sigmoid restoration, score-range rejection, radio exclusions/caps and sparse candidate sets.

The export executable builds separately from the server solution. The actual read-only snapshot run confirms the PostgreSQL session reports `transaction_read_only=on`. It is a read-only integration probe, not a write-capable production test suite.
