# Audio data evaluation and recommendation plan

Measured on 2026-11-13. The user authorized read-only access to the real database. Two bounded read-only snapshots were taken while analysis continued; the second is the evaluation corpus below. No production rows, queue state, settings, files in the music library or playback state were changed. No titles, artist names, account data or listening histories were read or logged. Feature computation and ranking ran locally on anonymized snapshot groups.

## Current corpus

- 14,280 indexed recordings; 11,741 have analysis, about 82.2% coverage at the snapshot.
- 1,733 pending, one processing, 776 unavailable and 29 failed jobs. Processing was still active; these are not final totals. Failures were not investigated in this read-only recommendation evaluation.
- All analyzed rows have the same profile, feature schema, model version and two model hashes.
- Every embedding has 200 finite values and nonzero norm. Every row has the same 50 descriptor labels. Tempo, energy and duration are finite and nonzero in this corpus.
- 10,412 distinct audio content hashes; 1,329 rows repeat a hash. This is repeated file content indexed under multiple recording records, not a count of corrupt results.
- There are another 39 identical embedding vectors after hash deduplication. Different file tags/encodings or genuinely similar signals may explain them; identical embeddings alone do not establish recording identity.
- The embedding values require about 9.0 MiB as a float32 matrix for the analyzed rows, excluding IDs, metadata, runtime objects and additional normalized copies. For 14,280 rows the values alone take about 10.9 MiB. A loaded local matrix is practical at this library size; no full source database or vector database is required for the initial implementation.

The read-only exporter, ranking experiment, aggregate results and dedicated tests are in [tools/audio-recommendations](tools/audio-recommendations/README.md).

## Two interpretation issues

### Descriptor scores need a version-specific transformation

The pinned upstream [AudioMuse 3.7.1 analyzer](https://github.com/NeptuneHub/AudioMuse-AI/blob/ac65c469d288a45092db77cc76c1e347a9e14f48/tasks/analysis/song.py#L562) computes:

`stored = sigmoid(mean(sigmoid(patch_outputs)))`

All 587,050 descriptor values in the snapshot are therefore above 0.5. The measured range is approximately 0.500007 to 0.721177; the median is about 0.503236. A rule such as “genre score above 0.5” would assign every label to every track. Direct cosine comparison of stored descriptors is dominated by this shared offset.

The outer sigmoid is invertible. The prototype computes `log(stored / (1 - stored))` in memory to recover the average score before the final sigmoid. Raw values remain intact. This avoids rerunning audio inference and must be tied to the verified analyzer lineage. The restored values still are not calibrated genre/mood probabilities. Within-track label rank is unchanged by this monotonic transformation.

The 50 labels include broad genres, moods, instruments, vocal descriptors and decade-like labels. Keep separate descriptor categories in product filters. “90s” is an estimated sound descriptor, not a substitute for release date metadata. This vocabulary cannot provide arbitrary fine styles or text prompts.

### Analyzed duration is not authoritative track length

125 rows have an analyzed duration of exactly 600 seconds. The pinned upstream source documents its ten-minute decoding cap. Retain the library/file duration for track length, playback and history. The stored analysis duration describes the audio used for features; it can represent a partial long track. Tempo and key are estimates; tempo may have half/double-time ambiguity, and energy is not a calibrated human excitement rating.

## Similarity experiment

Method: remove repeated content hashes; draw 256 reproducible random seeds from 10,412 unique files; rank the other files; inspect the first ten. Album and artist identity are anonymous snapshot groups. Matching an album or artist is useful structural evidence, but music from different artists can be equally relevant. This experiment does not replace listening tests or prove personalization.

| Method | Same album in top ten | Same artist in top ten | Same artist from a different album |
| --- | ---: | ---: | ---: |
| Random | 0.4% | 3.1% | 4.0% |
| Raw embedding cosine | 50.4% | 63.3% | 45.8% |
| Centered embedding cosine | 50.4% | 62.1% | 47.5% |
| Stored descriptor cosine | 32.4% | 44.9% | 35.6% |
| Restored descriptor cosine | 32.0% | 45.7% | 36.7% |
| Provisional embedding/descriptor hybrid | 51.2% | 61.7% | 45.8% |

Cross-album evaluation has 177 eligible seeds and excludes the seed album from the candidate set. Hybrid weights were a provisional 80% centered embedding and 20% restored descriptor similarity. They were not optimized. Centering did slightly better on the cross-album proxy, but raw cosine did better on overall artist retrieval. This sample does not establish a clear winning transform or weight. Start with embedding retrieval, preserve alternatives behind one scorer interface, and use restored descriptors for optional filtering and explanations. Do not claim the hybrid improves relevance.

Raw embedding ranking, including sorting, took about 2.1 milliseconds at the measured 95th percentile against the loaded unique-file matrix with one OpenBLAS thread. These are local warm-cache Python measurements, not server API latency. Loading JSONB from PostgreSQL on every play request would add unnecessary work. Cache a model-specific normalized matrix and refresh it incrementally as rows finish analysis. Rebuild any centered matrix against a named/versioned corpus mean; changing that mean invalidates related cached scores.

## Radio prototype

For each of the 256 seeds, build a 20-track queue from the top 300 hybrid candidates, excluding the seed album/content, applying at most two tracks per release artist and one per album, and penalizing similarity to already selected tracks. Clips under 45 analyzed seconds were excluded for this experiment.

All 256 queues filled:

- 20 distinct content hashes and 20 albums in every queue.
- About 18 distinct release artists on average; minimum 14.
- No seed-album tracks and no violated artist/album caps.
- Diverse selection after scores existed averaged about 13.6 milliseconds locally.

These checks establish policy compliance, not the musical quality of the sequence. Exact hash deduplication does not detect every alternate encoding, remaster or near duplicate. The production flow also needs existing queue/current-track exclusions, canonical recording identity, history exclusions, user access/playability checks, cancellation and stale-request fencing. The prototype is not connected to the application player.

## Useful features and their flows

### 1. Track radio and queue continuation

User chooses Start radio on a track. Resolve the exact recording and current model cohort, get embedding neighbours, exclude current/queued/recently played content, apply access/playability checks, then diversify by artist and album. Use tempo/energy as soft continuity terms, not hard identity or genre filters. Return a bounded first page and an opaque continuation token tied to seed, user and feature generation.

If automatic continuation is enabled, request the next page before the queue empties. Append through the player's existing queue contract. Keep manual queue additions and order authoritative; a late response must not replace a newer queue. Keep radio opt-in. Unanalyzed tracks need a metadata fallback with an explicit explanation, or a clear unavailable result; new analysis should improve coverage without rebuilding a song catalog.

### 2. Similar tracks

An endpoint returning eligible nearby recordings with a short explanation can use the same retrieval/cache layer. Label similarity as “similar sound”; use high-ranking compatible descriptors for optional explanation. Do not expose raw vectors or represent scores as percentages of user enjoyment. Cross-artist results must remain possible.

### 3. Focused generated mixes

Use restored descriptor rank plus corpus-relative energy ranges and broad tempo windows to create mixes such as calmer selections, energetic tracks or instrumental candidates. Treat vocal/instrumental estimates as uncertain; user correction should override inferred labels. Match tempo softly with half/double-time alternatives where useful. Do not promise semantic prompts like “songs about winter” from a fixed 50-label sound classifier.

### 4. Playlist continuation

Use several representative tracks from the playlist and recent queue context rather than averaging the entire library into one vector. Retrieve candidates from those seed groups, remove playlist/current/history duplicates and diversify. Preserve saved playlists until the user explicitly adds or saves suggestions. Changing a playlist while a request runs must invalidate its completion.

### 5. Home recommendations and personal mixes

Combine qualified listens and liked/saved music with the audio features. Maintain several taste groups so dissimilar favourite genres do not collapse into one average vector. Blend familiar and less-played eligible music, rotate artist/album exposure and show the reason for a section. Keep existing recent album/playlist provenance intact. The present evaluation deliberately did not read user histories, so no personal preference or skip quality was measured.

Before learning from feedback, verify ownership of history writes, persistence of playback progress/reasons, and the meaning of skips. A failed stream or buffering problem must not become a dislike. Add exposure records before measuring click/play outcomes; recording which candidates were shown is necessary for meaningful evaluation.

## Implementation order

1. Add a model-cohort-aware feature repository/cache, raw embedding scorer and lineage-specific descriptor transformation. Preserve existing raw results. Test cache refresh, invalid data, mixed models and concurrent rebuilds.
2. Add authenticated similar-track and track-radio endpoints with eligibility filters, exact-content/canonical-ID exclusions, deterministic diversity and pagination. Test user isolation and small/sparse libraries.
3. Connect radio to the mobile queue contract with cancellation, generation fences and manual-edit preservation. Test both player-selected seed changes and external queue additions before connecting Jam synchronization.
4. Add multi-seed playlist continuation and generated mixes. Keep inferred descriptors overrideable, and record the scorer/policy version for reproducibility.
5. Add personalized home taste groups and feedback/exposure persistence after history integrity is verified. Listening tests and user feedback should choose transforms/weights; proxy metrics alone should not.

Do not rerun AudioMuse merely to experiment with recommendation weights or invert the known outer sigmoid. Existing stored embeddings/features are sufficient. Future model changes must create separate cohorts and may require new inference; recommendation policy changes do not.

## Verification and remaining work

The exporter build passed. Twelve dedicated offline tests passed for normalization, cohort isolation, content deduplication, bad inputs, score restoration, stable ranking and radio constraints/sparse candidates. Evaluation ran against the real read-only snapshot and the aggregate result file is versioned. No production database migrations or write-capable tests ran. No app endpoints, UI or playback behavior changed. The live analyzer continued its work.

Re-evaluate after the corpus completes, inspect the 29 retry/failure categories separately, and conduct listening tests across several styles and short/long tracks. Genre/mood labels need user-facing interpretation and overrides. Actual API latency, cache reload behavior, mobile queue integration and listener satisfaction are not verified by this experiment.
