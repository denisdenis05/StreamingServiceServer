import hashlib
import http.client
import json
import pathlib
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import bridge

TEST_TOKEN = 'test-secret-never-logged'
TEST_REQUEST_ID = 'a93f4e73-5fa4-47bc-bf19-a45a71b9cddc'
TEST_AUDIO = b'not-real-audio-private'
TEST_BOUNDARY = 'test-multipart-boundary'
TEST_CHECKSUM = 'a' * 64


def multipart(fields=None):
    fields = fields or [('request_id', TEST_REQUEST_ID.encode()), ('profile', bridge.PROFILE.encode()), ('audio', TEST_AUDIO)]
    body = b''
    for name, value in fields:
        body += ('--' + TEST_BOUNDARY + '\r\nContent-Disposition: form-data; name="' + name + '"\r\n\r\n').encode() + value + b'\r\n'
    return body + ('--' + TEST_BOUNDARY + '--\r\n').encode()


class FakeEngine:
    models = [{'name': 'embedding', 'sha256': TEST_CHECKSUM}, {'name': 'prediction', 'sha256': TEST_CHECKSUM}]

    def __init__(self):
        self.observed_path = None
        self.failure = None

    def analyze(self, audio_path):
        self.observed_path = audio_path
        if self.failure:
            raise self.failure
        if audio_path.read_bytes() != TEST_AUDIO:
            raise ValueError('wrong_bytes')
        return {'tempo': 90, 'energy': 0.1, 'key': 'C', 'scale': 'minor', 'duration_seconds': 100, 'moods': {'Hip-Hop': 0.7}}, [0.1] * bridge.EMBEDDING_DIMENSION


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.engine = FakeEngine()
        self.application = bridge.BridgeApplication(TEST_TOKEN, self.engine)
        self.server = bridge.ThreadingHTTPServer(('127.0.0.1', 0), bridge.handler_factory(self.application))
        self.server_thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.server_thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.server_thread.join()

    def request(self, method='POST', path='/v1/analyze', body=None, token=TEST_TOKEN, headers=None):
        connection = http.client.HTTPConnection(*self.server.server_address)
        request_headers = {'Authorization': 'Bearer ' + token, 'Content-Type': 'multipart/form-data; boundary=' + TEST_BOUNDARY}
        request_headers.update(headers or {})
        connection.request(method, path, body if body is not None else multipart(), headers=request_headers)
        response = connection.getresponse()
        result = response.status, json.loads(response.read())
        connection.close()
        return result

    def test_analysis_returns_exact_content_identity_and_full_features(self):
        status, payload = self.request()
        self.assertEqual(status, 200)
        self.assertEqual(payload['requestId'], TEST_REQUEST_ID)
        self.assertEqual(payload['audioSha256'], hashlib.sha256(TEST_AUDIO).hexdigest())
        self.assertEqual(len(payload['embedding']), bridge.EMBEDDING_DIMENSION)
        self.assertEqual(payload['features']['moods'], {'Hip-Hop': 0.7})
        self.assertFalse(self.engine.observed_path.exists())
        self.assertEqual(self.engine.observed_path.name, 'audio.bin')

    def test_requires_authentication_for_health_and_analysis(self):
        for method, path in [('GET', '/health'), ('POST', '/v1/analyze')]:
            self.assertEqual(self.request(method, path, token='wrong')[0], 401)

    def test_health_reports_readiness_without_secrets_or_paths(self):
        status, payload = self.request('GET', '/health')
        self.assertEqual(status, 200)
        self.assertTrue(payload['ready'])
        self.assertNotIn(TEST_TOKEN, json.dumps(payload))
        self.assertNotIn('models', payload)
        self.application.engine = None
        self.assertFalse(self.request('GET', '/health')[1]['ready'])
        self.assertEqual(self.request()[0], 503)

    def test_busy_worker_rejects_new_analysis(self):
        self.application.analysis_lock.acquire()
        try:
            self.assertEqual(self.request()[0], 429)
        finally:
            self.application.analysis_lock.release()

    def test_upload_limit_rejects_before_analysis(self):
        self.application.maximum_upload_bytes = 1
        self.assertEqual(self.request()[0], 413)
        self.assertIsNone(self.engine.observed_path)

    def test_bad_uuid_profile_missing_duplicate_and_empty_audio_rejected(self):
        valid = [('request_id', TEST_REQUEST_ID.encode()), ('profile', bridge.PROFILE.encode()), ('audio', TEST_AUDIO)]
        invalid_cases = [valid[:2], valid + [('audio', TEST_AUDIO)], [('request_id', b'invalid')] + valid[1:], [valid[0], ('profile', b'other'), valid[2]], valid[:2] + [('audio', b'')]]
        for fields in invalid_cases:
            with self.subTest(fields_count=len(fields)):
                self.assertEqual(self.request(body=multipart(fields))[0], 400)

    def test_failure_is_sanitized_and_temporary_audio_removed(self):
        self.engine.failure = RuntimeError('private title /private/path token')
        status, payload = self.request()
        self.assertEqual((status, payload), (500, {'error': 'analysis_failed'}))
        self.assertFalse(self.engine.observed_path.exists())
        self.assertFalse(self.application.analysis_lock.locked())

    def test_invalid_multipart_and_unknown_routes_rejected(self):
        self.assertEqual(self.request(body=b'invalid')[0], 400)
        self.assertEqual(self.request(path='/api/analysis/start')[0], 404)

    def test_transfer_encoding_and_duplicate_content_length_rejected(self):
        self.assertEqual(self.request(headers={'Transfer-Encoding': 'chunked'})[0], 400)


class EnvelopeTests(unittest.TestCase):
    def setUp(self):
        self.engine = FakeEngine()
        self.analysis = {'tempo': 90, 'energy': 0.1, 'key': 'C', 'scale': 'minor', 'duration_seconds': 100, 'moods': {'Hip-Hop': 0.7}}
        self.embedding = [0.1] * bridge.EMBEDDING_DIMENSION

    def test_rejects_nonfinite_and_wrong_dimension_embeddings(self):
        for embedding in ([float('nan')] * bridge.EMBEDDING_DIMENSION, [0.1], [True] * bridge.EMBEDDING_DIMENSION):
            with self.assertRaises(ValueError):
                bridge.response_envelope(TEST_REQUEST_ID, TEST_CHECKSUM, self.engine, self.analysis, embedding)

    def test_rejects_invalid_duration_score_energy_and_tempo(self):
        for field_name, value in [('duration_seconds', 0), ('energy', -1), ('tempo', float('inf')), ('moods', {'rock': 1.1}), ('key', None)]:
            analysis = dict(self.analysis, **{field_name: value})
            with self.assertRaises(ValueError):
                bridge.response_envelope(TEST_REQUEST_ID, TEST_CHECKSUM, self.engine, analysis, self.embedding)

    def test_models_require_manifest_pin_and_real_file_hashes(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            manifest_path = pathlib.Path(temporary_directory) / 'manifest.json'
            manifest_path.write_text(json.dumps({'engineCommit': 'wrong', 'profile': bridge.PROFILE}))
            with self.assertRaises(ValueError):
                bridge.AudioMuseEngine(manifest_path, temporary_directory)


if __name__ == '__main__':
    unittest.main()
