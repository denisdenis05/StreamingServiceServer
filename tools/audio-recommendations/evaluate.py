import argparse
import json
import time
from pathlib import Path

import numpy

EMBEDDING_DIMENSIONS = 200
SEED_COUNT = 256
NEIGHBOUR_COUNT = 10
RADIO_LENGTH = 20
RADIO_CANDIDATES = 300
MAXIMUM_PER_ARTIST = 2
MAXIMUM_PER_ALBUM = 1
MINIMUM_DURATION_SECONDS = 45
RANDOM_SEED = 20261113
EMBEDDING_WEIGHT = 0.8
DESCRIPTOR_WEIGHT = 0.2
DIVERSITY_WEIGHT = 0.15
ZERO_NORM_THRESHOLD = 1e-12
METHOD_NAMES = ('random', 'cosine', 'centered_cosine', 'descriptors', 'restored_descriptors', 'hybrid')
OUTER_SIGMOID_ENGINE_VERSION = '3.7.1'
MAXIMUM_OUTER_SIGMOID_SCORE = 1 / (1 + numpy.exp(-1))
MINIMUM_OUTER_SIGMOID_SCORE = 0.5
SCORE_TOLERANCE = 1e-6
PERCENTILE_95 = 0.95
MILLISECONDS_PER_SECOND = 1000


def normalize(matrix):
    norms = numpy.linalg.norm(matrix, axis=1, keepdims=True)
    return numpy.divide(matrix, norms, out=numpy.zeros_like(matrix), where=norms > ZERO_NORM_THRESHOLD)


def restore_descriptor_scores(scores):
    values = numpy.asarray(scores, dtype=numpy.float64)
    if not numpy.isfinite(values).all() or (values < MINIMUM_OUTER_SIGMOID_SCORE - SCORE_TOLERANCE).any() or (values > MAXIMUM_OUTER_SIGMOID_SCORE + SCORE_TOLERANCE).any():
        raise ValueError('unexpected_outer_sigmoid_range')
    return numpy.clip(numpy.log(values / (1 - values)), 0, 1).astype(numpy.float32)


def prepare(rows):
    if not rows:
        raise ValueError('empty_snapshot')
    cohorts = {(row['profile'], row['engineVersion'], json.dumps(row['models'], sort_keys=True)) for row in rows}
    if len(cohorts) != 1:
        raise ValueError('incompatible_model_cohorts')
    unique_rows = list({row['content']: row for row in reversed(rows)}.values())
    unique_rows.sort(key=lambda row: row['identifier'])
    embeddings = numpy.asarray([row['embedding'] for row in unique_rows], dtype=numpy.float32)
    if embeddings.shape != (len(unique_rows), EMBEDDING_DIMENSIONS) or not numpy.isfinite(embeddings).all():
        raise ValueError('invalid_embedding')
    if (numpy.linalg.norm(embeddings, axis=1) <= ZERO_NORM_THRESHOLD).any():
        raise ValueError('zero_embedding')
    labels = sorted(unique_rows[0]['features']['moods'])
    if not labels or any(sorted(row['features']['moods']) != labels for row in unique_rows):
        raise ValueError('incompatible_descriptor_vocabularies')
    descriptors = numpy.asarray([[row['features']['moods'][label] for label in labels] for row in unique_rows], dtype=numpy.float32)
    if not numpy.isfinite(descriptors).all() or ((descriptors < 0) | (descriptors > 1)).any():
        raise ValueError('invalid_descriptors')
    return unique_rows, normalize(embeddings), normalize(embeddings - embeddings.mean(axis=0)), normalize(descriptors), labels


def nearest(scores, count):
    available = numpy.flatnonzero(numpy.isfinite(scores))
    ordered = available[numpy.argsort(-scores[available], kind='stable')]
    return ordered[:count]


def radio(scores, rows, vectors, seed_index):
    candidate_scores = scores.copy()
    seed = rows[seed_index]
    for index, row in enumerate(rows):
        if row['content'] == seed['content'] or row['album'] == seed['album'] or row['features']['durationSeconds'] < MINIMUM_DURATION_SECONDS:
            candidate_scores[index] = -numpy.inf
    candidates = nearest(candidate_scores, RADIO_CANDIDATES).tolist()
    selected = []
    artist_counts = {}
    album_counts = {}
    while candidates and len(selected) < RADIO_LENGTH:
        eligible = [index for index in candidates if artist_counts.get(rows[index]['artist'], 0) < MAXIMUM_PER_ARTIST
                    and album_counts.get(rows[index]['album'], 0) < MAXIMUM_PER_ALBUM]
        if not eligible:
            break
        relevance = candidate_scores[eligible].copy()
        if selected:
            redundancy = (vectors[eligible] @ vectors[selected].T).max(axis=1)
            relevance -= DIVERSITY_WEIGHT * redundancy
        chosen = eligible[int(numpy.argmax(relevance))]
        selected.append(chosen)
        candidates.remove(chosen)
        artist = rows[chosen]['artist']
        album = rows[chosen]['album']
        artist_counts[artist] = artist_counts.get(artist, 0) + 1
        album_counts[album] = album_counts.get(album, 0) + 1
    return selected


def evaluate(rows, timer=time.perf_counter):
    prepared, cosine, centered, descriptors, labels = prepare(rows)
    if prepared[0]['engineVersion'] != OUTER_SIGMOID_ENGINE_VERSION:
        raise ValueError('unverified_score_transform_version')
    restored = normalize(restore_descriptor_scores(numpy.asarray([[row['features']['moods'][label] for label in labels] for row in prepared])))
    generator = numpy.random.default_rng(RANDOM_SEED)
    seed_indices = numpy.sort(generator.choice(len(prepared), min(SEED_COUNT, len(prepared)), replace=False))
    artists = numpy.asarray([row['artist'] for row in prepared])
    albums = numpy.asarray([row['album'] for row in prepared])
    metrics = {name: {'album_hit': [], 'artist_hit': [], 'cross_album_artist_hit': [], 'unique_artists': [], 'score_milliseconds': []} for name in METHOD_NAMES}
    radio_metrics = {'lengths': [], 'unique_artists': [], 'unique_albums': [], 'seed_album_excluded': [], 'contents_unique': [], 'selection_milliseconds': []}
    for seed_index in seed_indices:
        score_sets = {}
        for name in METHOD_NAMES:
            started = timer()
            if name == 'random':
                scores = generator.random(len(prepared))
            elif name == 'cosine':
                scores = cosine @ cosine[seed_index]
            elif name == 'centered_cosine':
                scores = centered @ centered[seed_index]
            elif name == 'descriptors':
                scores = descriptors @ descriptors[seed_index]
            elif name == 'restored_descriptors':
                scores = restored @ restored[seed_index]
            else:
                scores = EMBEDDING_WEIGHT * score_sets['centered_cosine'] + DESCRIPTOR_WEIGHT * score_sets['restored_descriptors']
            scores[seed_index] = -numpy.inf
            selected = nearest(scores, NEIGHBOUR_COUNT)
            elapsed = (timer() - started) * MILLISECONDS_PER_SECOND
            score_sets[name] = scores
            values = metrics[name]
            values['album_hit'].append(bool((albums[selected] == albums[seed_index]).any()))
            values['artist_hit'].append(bool((artists[selected] == artists[seed_index]).any()))
            values['unique_artists'].append(len(set(artists[selected].tolist())))
            cross_album = scores.copy()
            cross_album[albums == albums[seed_index]] = -numpy.inf
            cross_selected = nearest(cross_album, NEIGHBOUR_COUNT)
            if ((artists == artists[seed_index]) & (albums != albums[seed_index])).any():
                values['cross_album_artist_hit'].append(bool((artists[cross_selected] == artists[seed_index]).any()))
            values['score_milliseconds'].append(elapsed)
        radio_started = timer()
        selected = radio(score_sets['hybrid'], prepared, centered, seed_index)
        radio_metrics['selection_milliseconds'].append((timer() - radio_started) * MILLISECONDS_PER_SECOND)
        radio_metrics['lengths'].append(len(selected))
        radio_metrics['unique_artists'].append(len(set(artists[selected].tolist())))
        radio_metrics['unique_albums'].append(len(set(albums[selected].tolist())))
        radio_metrics['seed_album_excluded'].append(bool((albums[selected] != albums[seed_index]).all()))
        radio_metrics['contents_unique'].append(len({prepared[index]['content'] for index in selected}) == len(selected))
    outcomes = {}
    for name, values in metrics.items():
        outcomes[name] = {key: float(numpy.mean(value)) if value else None for key, value in values.items()}
        outcomes[name]['score_p95_milliseconds'] = float(numpy.quantile(values['score_milliseconds'], PERCENTILE_95))
        outcomes[name]['cross_album_eligible_seeds'] = len(values['cross_album_artist_hit'])
    embedding_matrix = numpy.asarray([row['embedding'] for row in rows], dtype=numpy.float32)
    scalar_quality = {}
    for field in ('tempo', 'energy', 'durationSeconds'):
        values = numpy.asarray([row['features'][field] for row in rows])
        scalar_quality[field] = {'finite': bool(numpy.isfinite(values).all()), 'minimum': float(values.min()),
                                'median': float(numpy.median(values)), 'maximum': float(values.max())}
    return {'rows': len(rows), 'unique_audio': len(prepared), 'repeated_audio_rows': len(rows) - len(prepared),
            'seed_count': len(seed_indices), 'neighbour_count': NEIGHBOUR_COUNT, 'labels': labels,
            'embedding_dimensions': EMBEDDING_DIMENSIONS, 'float32_embedding_bytes': embedding_matrix.nbytes,
            'zero_embeddings': int((numpy.linalg.norm(embedding_matrix, axis=1) <= ZERO_NORM_THRESHOLD).sum()),
            'scalar_quality': scalar_quality, 'methods': outcomes,
            'radio': {name: {'minimum': float(numpy.min(values)), 'mean': float(numpy.mean(values))} for name, values in radio_metrics.items()}}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('snapshot', type=Path)
    parser.add_argument('output', type=Path)
    arguments = parser.parse_args()
    rows = [json.loads(line) for line in arguments.snapshot.open()]
    arguments.output.write_text(json.dumps(evaluate(rows), indent=2) + '\n')


if __name__ == '__main__':
    main()
