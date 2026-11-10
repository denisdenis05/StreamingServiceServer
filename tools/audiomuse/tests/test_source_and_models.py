import hashlib
import io
import json
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import bridge
import download_models
import prepare_upstream

TEST_MODEL_BYTES = b'fake model fixture'
TEST_MODEL_HASH = hashlib.sha256(TEST_MODEL_BYTES).hexdigest()


def fixture_manifest():
    return {'engineCommit': bridge.UPSTREAM_COMMIT, 'profile': bridge.PROFILE, 'models': [{'name': name, 'sha256': TEST_MODEL_HASH, 'url': bridge.MODEL_RELEASE_URL + filename} for name, filename in bridge.MODEL_FILENAMES.items()]}


class ModelTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.model_directory = pathlib.Path(self.directory.name)
        self.manifest_path = self.model_directory / 'manifest.json'
        self.manifest_path.write_text(json.dumps(fixture_manifest()))
        for filename in bridge.MODEL_FILENAMES.values():
            (self.model_directory / filename).write_bytes(TEST_MODEL_BYTES)

    def tearDown(self):
        self.directory.cleanup()

    def test_model_integrity_failure_happens_before_importing_engine(self):
        (self.model_directory / bridge.MODEL_FILENAMES['embedding']).write_bytes(b'tampered')
        with mock.patch.object(bridge.importlib, 'import_module') as importer:
            with self.assertRaises(ValueError):
                bridge.AudioMuseEngine(self.manifest_path, self.model_directory)
            importer.assert_not_called()

    def test_duplicate_model_unknown_url_and_symlink_rejected(self):
        manifest = fixture_manifest()
        manifest['models'][1] = dict(manifest['models'][0])
        self.manifest_path.write_text(json.dumps(manifest))
        with self.assertRaises(ValueError):
            bridge.AudioMuseEngine(self.manifest_path, self.model_directory)
        manifest = fixture_manifest()
        manifest['models'][0]['url'] = 'https://example.com/model'
        self.manifest_path.write_text(json.dumps(manifest))
        with self.assertRaises(ValueError):
            bridge.AudioMuseEngine(self.manifest_path, self.model_directory)
        self.manifest_path.write_text(json.dumps(fixture_manifest()))
        embedding_path = self.model_directory / bridge.MODEL_FILENAMES['embedding']
        embedding_path.unlink()
        embedding_path.symlink_to(self.model_directory / bridge.MODEL_FILENAMES['prediction'])
        with self.assertRaises(ValueError):
            bridge.AudioMuseEngine(self.manifest_path, self.model_directory)

    def test_pinned_models_load_real_entrypoint_and_keep_full_model_identity(self):
        configuration = mock.Mock(MOOD_LABELS=['rock'])
        analyzer = mock.Mock()
        analyzer.load_musicnn_sessions.return_value = {'embedding': object(), 'prediction': object()}
        analyzer.analyze_track.return_value = ({'tempo': 90}, mock.Mock())
        analyzer.analyze_track.return_value[1].tolist.return_value = [0.1] * bridge.EMBEDDING_DIMENSION
        with mock.patch.object(bridge.importlib, 'import_module', side_effect=[configuration, analyzer]), mock.patch.object(bridge.logging, 'disable'):
            engine = bridge.AudioMuseEngine(self.manifest_path, self.model_directory)
        analysis, embedding = engine.analyze(self.model_directory / 'opaque.bin')
        self.assertEqual(analysis['tempo'], 90)
        self.assertEqual(len(embedding), bridge.EMBEDDING_DIMENSION)
        self.assertEqual({model['name'] for model in engine.models}, bridge.REQUIRED_MODEL_NAMES)
        analyzer.analyze_track.assert_called_once()

    def test_download_reuses_valid_models_without_network(self):
        with mock.patch.object(download_models, 'MANIFEST_PATH', self.manifest_path):
            with tempfile.TemporaryDirectory() as destination:
                for filename in bridge.MODEL_FILENAMES.values():
                    (pathlib.Path(destination) / filename).write_bytes(TEST_MODEL_BYTES)
                opener = mock.Mock(side_effect=AssertionError('network forbidden'))
                download_models.download_models(destination, open_url=opener)
                opener.assert_not_called()
                self.assertTrue((pathlib.Path(destination) / 'manifest.json').exists())

    def test_download_rejects_corrupt_model_and_cleans_partial_file(self):
        response = io.BytesIO(b'corrupt')
        response.geturl = lambda: 'https://release-assets.githubusercontent.com/file'
        with mock.patch.object(download_models, 'MANIFEST_PATH', self.manifest_path):
            with tempfile.TemporaryDirectory() as destination:
                with self.assertRaises(ValueError):
                    download_models.download_models(destination, open_url=mock.Mock(return_value=response))
                self.assertEqual(list(pathlib.Path(destination).iterdir()), [])


class SourceAdaptationTests(unittest.TestCase):
    def test_source_commit_mismatch_rejects_without_modifying_files(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            with mock.patch.object(prepare_upstream.subprocess, 'check_output', return_value='wrong'):
                with self.assertRaises(ValueError):
                    prepare_upstream.prepare(temporary_directory)
            self.assertEqual(list(pathlib.Path(temporary_directory).iterdir()), [])

    def test_preimage_mismatch_rejects_before_modification(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            configuration_path = pathlib.Path(temporary_directory) / 'config.py'
            configuration_path.write_text('wrong source')
            with mock.patch.object(prepare_upstream.subprocess, 'check_output', return_value=bridge.UPSTREAM_COMMIT):
                with self.assertRaises(ValueError):
                    prepare_upstream.prepare(temporary_directory)
            self.assertEqual(configuration_path.read_text(), 'wrong source')

    def test_only_documented_bootstrap_and_provider_boundaries_are_changed(self):
        configuration = 'VALUE = 1\n\ndef _apply_db_overrides():\n    pass\n\n_apply_db_overrides()\n'
        provider = 'VALUE = 2\n\ndef _plugin_onnx_providers():\n    raise RuntimeError()\n\ndef _default_sess_options():\n    return 3\n'
        fixtures = {'config.py': configuration, 'tasks/onnx_utils.py': provider}
        hashes = {filename: hashlib.sha256(content.encode()).hexdigest() for filename, content in fixtures.items()}
        with tempfile.TemporaryDirectory() as temporary_directory:
            for filename, content in fixtures.items():
                path = pathlib.Path(temporary_directory) / filename
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content)
            with mock.patch.object(prepare_upstream.subprocess, 'check_output', return_value=bridge.UPSTREAM_COMMIT), mock.patch.object(prepare_upstream, 'UPSTREAM_PREIMAGES', hashes):
                prepare_upstream.prepare(temporary_directory)
            modified_configuration = (pathlib.Path(temporary_directory) / 'config.py').read_text()
            modified_provider = (pathlib.Path(temporary_directory) / 'tasks/onnx_utils.py').read_text()
            self.assertIn('def _apply_db_overrides():', modified_configuration)
            self.assertFalse(modified_configuration.endswith('\n_apply_db_overrides()\n'))
            self.assertIn('def _plugin_onnx_providers():\n    return []', modified_provider)
            self.assertIn('def _default_sess_options():\n    return 3', modified_provider)


if __name__ == '__main__':
    unittest.main()
