import unittest

import numpy

import evaluate

TEST_DURATION_SECONDS = 180
TEST_PROFILE = 'musicnn-v1'
TEST_ENGINE_VERSION = evaluate.OUTER_SIGMOID_ENGINE_VERSION
TEST_MODEL = {'name': 'embedding', 'sha256': 'test-checksum'}
TEST_DESCRIPTOR = 'rock'
TEST_DESCRIPTOR_SCORE = 0.5
TEST_ROW_COUNT = 60
TEST_ARTIST_COUNT = 12
FIRST_IDENTIFIER = 1
SECOND_IDENTIFIER = 2
OTHER_ENGINE_VERSION = 'other-version'


def row(identifier):
    return {'identifier': identifier, 'content': identifier, 'artist': identifier % TEST_ARTIST_COUNT,
            'album': identifier, 'profile': TEST_PROFILE, 'engineVersion': TEST_ENGINE_VERSION,
            'models': [TEST_MODEL], 'embedding': [TEST_DESCRIPTOR_SCORE] * evaluate.EMBEDDING_DIMENSIONS,
            'features': {'durationSeconds': TEST_DURATION_SECONDS, 'tempo': TEST_DURATION_SECONDS, 'energy': TEST_DESCRIPTOR_SCORE, 'moods': {TEST_DESCRIPTOR: TEST_DESCRIPTOR_SCORE}}}


class EvaluationTests(unittest.TestCase):
    def test_empty_snapshot_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'empty_snapshot'):
            evaluate.prepare([])

    def test_full_evaluation_is_reproducible_with_controlled_clock(self):
        rows = [row(identifier) for identifier in range(TEST_ROW_COUNT)]
        first = evaluate.evaluate(rows, timer=lambda: 0.0)
        second = evaluate.evaluate(rows, timer=lambda: 0.0)
        self.assertEqual(first, second)
        self.assertEqual(first['seed_count'], TEST_ROW_COUNT)
        self.assertEqual(first['unique_audio'], TEST_ROW_COUNT)

    def test_outer_sigmoid_can_be_reversed_without_rerunning_inference(self):
        probabilities = numpy.asarray([0.0, 0.2, 0.5, 1.0])
        stored = 1 / (1 + numpy.exp(-probabilities))
        restored = evaluate.restore_descriptor_scores(stored)
        numpy.testing.assert_allclose(restored, probabilities, atol=evaluate.SCORE_TOLERANCE)

    def test_score_transform_rejects_values_outside_verified_upstream_range(self):
        with self.assertRaisesRegex(ValueError, 'unexpected_outer_sigmoid_range'):
            evaluate.restore_descriptor_scores(numpy.asarray([0.0]))

    def test_normalization_preserves_direction_and_handles_zero_vectors(self):
        values = numpy.asarray([[TEST_DESCRIPTOR_SCORE, TEST_DESCRIPTOR_SCORE], [0.0, 0.0]])
        normalized = evaluate.normalize(values)
        self.assertAlmostEqual(float(numpy.linalg.norm(normalized[0])), 1.0)
        self.assertTrue(numpy.isfinite(normalized).all())
        self.assertEqual(float(numpy.linalg.norm(normalized[1])), 0.0)

    def test_content_duplicates_are_removed_before_ranking(self):
        original = row(FIRST_IDENTIFIER)
        duplicate = row(SECOND_IDENTIFIER)
        duplicate['content'] = original['content']
        prepared, *_ = evaluate.prepare([original, duplicate])
        self.assertEqual(len(prepared), FIRST_IDENTIFIER)
        self.assertEqual(prepared[0]['identifier'], original['identifier'])

    def test_incompatible_models_cannot_be_compared(self):
        original = row(FIRST_IDENTIFIER)
        incompatible = row(SECOND_IDENTIFIER)
        incompatible['engineVersion'] = OTHER_ENGINE_VERSION
        with self.assertRaisesRegex(ValueError, 'incompatible_model_cohorts'):
            evaluate.prepare([original, incompatible])

    def test_invalid_embeddings_are_rejected(self):
        for embedding in ([0.0] * evaluate.EMBEDDING_DIMENSIONS, [float('nan')] * evaluate.EMBEDDING_DIMENSIONS, []):
            with self.subTest(embedding_length=len(embedding)):
                candidate = row(FIRST_IDENTIFIER)
                candidate['embedding'] = embedding
                with self.assertRaises(ValueError):
                    evaluate.prepare([candidate])

    def test_missing_or_out_of_range_descriptors_are_rejected(self):
        original = row(FIRST_IDENTIFIER)
        incompatible = row(SECOND_IDENTIFIER)
        incompatible['features']['moods'] = {}
        with self.assertRaisesRegex(ValueError, 'incompatible_descriptor_vocabularies'):
            evaluate.prepare([original, incompatible])
        original['features']['moods'][TEST_DESCRIPTOR] = float('inf')
        with self.assertRaisesRegex(ValueError, 'invalid_descriptors'):
            evaluate.prepare([original])

    def test_neighbours_exclude_nonfinite_entries_and_break_ties_stably(self):
        scores = numpy.asarray([1.0, -numpy.inf, 1.0, numpy.nan])
        self.assertEqual(evaluate.nearest(scores, evaluate.NEIGHBOUR_COUNT).tolist(), [0, 2])

    def test_radio_enforces_content_album_artist_and_short_track_limits(self):
        rows = [row(identifier) for identifier in range(TEST_ROW_COUNT)]
        rows[SECOND_IDENTIFIER]['features']['durationSeconds'] = 0
        rows[FIRST_IDENTIFIER]['content'] = rows[0]['content']
        vectors = evaluate.normalize(numpy.asarray([candidate['embedding'] for candidate in rows]))
        scores = numpy.ones(TEST_ROW_COUNT)
        selected = evaluate.radio(scores, rows, vectors, 0)
        self.assertEqual(len(selected), evaluate.RADIO_LENGTH)
        self.assertNotIn(0, selected)
        self.assertNotIn(FIRST_IDENTIFIER, selected)
        self.assertNotIn(SECOND_IDENTIFIER, selected)
        self.assertEqual(len({rows[index]['content'] for index in selected}), len(selected))
        for candidate in rows:
            self.assertLessEqual(sum(rows[index]['artist'] == candidate['artist'] for index in selected), evaluate.MAXIMUM_PER_ARTIST)
            self.assertLessEqual(sum(rows[index]['album'] == candidate['album'] for index in selected), evaluate.MAXIMUM_PER_ALBUM)

    def test_radio_returns_available_candidates_when_diversity_limits_cannot_fill_queue(self):
        rows = [row(identifier) for identifier in range(TEST_ROW_COUNT)]
        for candidate in rows:
            candidate['artist'] = FIRST_IDENTIFIER
        vectors = evaluate.normalize(numpy.asarray([candidate['embedding'] for candidate in rows]))
        selected = evaluate.radio(numpy.ones(TEST_ROW_COUNT), rows, vectors, 0)
        self.assertEqual(len(selected), evaluate.MAXIMUM_PER_ARTIST)


if __name__ == '__main__':
    unittest.main()
