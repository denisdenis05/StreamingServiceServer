import hashlib
import hmac
import importlib
import json
import logging
import math
import os
import re
import tempfile
import threading
import uuid
from email import policy
from email.parser import BytesParser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

PROTOCOL_VERSION = 1
FEATURE_SCHEMA_VERSION = 1
PROFILE = 'musicnn-v1'
ENGINE_NAME = 'AudioMuse-AI'
ENGINE_VERSION = '3.7.1'
UPSTREAM_COMMIT = 'ac65c469d288a45092db77cc76c1e347a9e14f48'
EMBEDDING_DIMENSION = 200
HASH_PATTERN = re.compile(r'^[0-9a-f]{64}$')
DEFAULT_MAX_UPLOAD_BYTES = 536936448
DEFAULT_TIMEOUT_SECONDS = 120
READ_CHUNK_BYTES = 1048576
MAX_MULTIPART_PARTS = 3
REQUIRED_MODEL_NAMES = frozenset(('embedding', 'prediction'))
MODEL_FILENAMES = {'embedding': 'musicnn_embedding.onnx', 'prediction': 'musicnn_prediction.onnx'}
MODEL_RELEASE_URL = 'https://github.com/NeptuneHub/AudioMuse-AI/releases/download/v5.0.0-model/'
ERROR_CODES = {400: 'invalid_request', 401: 'unauthorized', 404: 'not_found', 413: 'upload_too_large', 429: 'busy', 500: 'analysis_failed', 503: 'unavailable'}


class InvalidRequest(Exception):
    pass


def finite_number(value, minimum=0, maximum=None):
    if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value):
        raise ValueError('invalid_number')
    if value < minimum or maximum is not None and value > maximum:
        raise ValueError('invalid_number')
    return float(value)


def file_sha256(file_path):
    digest = hashlib.sha256()
    with open(file_path, 'rb') as audio_file:
        for chunk in iter(lambda: audio_file.read(READ_CHUNK_BYTES), b''):
            digest.update(chunk)
    return digest.hexdigest()


class AudioMuseEngine:
    def __init__(self, manifest_path, model_directory):
        manifest = json.loads(Path(manifest_path).read_text())
        if manifest.get('engineCommit') != UPSTREAM_COMMIT or manifest.get('profile') != PROFILE:
            raise ValueError('incompatible_manifest')
        self.model_directory = Path(model_directory).resolve()
        self.models = []
        self.model_paths = {}
        model_entries = manifest.get('models', [])
        if not isinstance(model_entries, list) or len(model_entries) != len(REQUIRED_MODEL_NAMES):
            raise ValueError('invalid_models')
        for model in model_entries:
            name = model.get('name')
            checksum = model.get('sha256', '')
            if name not in REQUIRED_MODEL_NAMES or name in self.model_paths or not HASH_PATTERN.fullmatch(checksum):
                raise ValueError('invalid_model')
            filename = MODEL_FILENAMES[name]
            if model.get('url') != MODEL_RELEASE_URL + filename:
                raise ValueError('invalid_model_url')
            model_path = self.model_directory / filename
            if model_path.is_symlink() or file_sha256(model_path) != checksum:
                raise ValueError('model_integrity_failed')
            self.model_paths[name] = str(model_path)
            self.models.append({'name': name, 'sha256': checksum})
        os.environ['EMBEDDING_MODEL_PATH'] = self.model_paths['embedding']
        os.environ['PREDICTION_MODEL_PATH'] = self.model_paths['prediction']
        os.environ['CUDA_VISIBLE_DEVICES'] = ''
        logging.disable(logging.CRITICAL)
        self.configuration = importlib.import_module('config')
        self.analyzer = importlib.import_module('tasks.analysis.song')
        self.sessions = self.analyzer.load_musicnn_sessions(self.model_paths)
        if self.sessions is None:
            raise ValueError('models_unavailable')

    def analyze(self, audio_path):
        analysis, embedding = self.analyzer.analyze_track(str(audio_path), self.configuration.MOOD_LABELS, self.model_paths, onnx_sessions=self.sessions)
        if analysis is None or embedding is None:
            raise ValueError('analysis_failed')
        return analysis, embedding.tolist()


def response_envelope(request_identifier, audio_hash, engine, analysis, embedding):
    if not isinstance(embedding, list) or len(embedding) != EMBEDDING_DIMENSION:
        raise ValueError('invalid_embedding')
    embedding_values = [finite_number(value, minimum=-float('inf')) for value in embedding]
    moods = analysis.get('moods')
    if not isinstance(moods, dict) or not moods or any(not isinstance(label, str) or not label or len(label) > 128 for label in moods):
        raise ValueError('invalid_moods')
    for field_name in ('key', 'scale'):
        if not isinstance(analysis.get(field_name), str) or not analysis[field_name] or len(analysis[field_name]) > 128:
            raise ValueError('invalid_features')
    duration = finite_number(analysis.get('duration_seconds'))
    if duration <= 0:
        raise ValueError('invalid_duration')
    return {'protocolVersion': PROTOCOL_VERSION, 'requestId': request_identifier, 'audioSha256': audio_hash, 'profile': PROFILE, 'engineName': ENGINE_NAME, 'engineVersion': ENGINE_VERSION, 'featureSchemaVersion': FEATURE_SCHEMA_VERSION, 'models': engine.models, 'embedding': embedding_values, 'features': {'tempo': finite_number(analysis.get('tempo')), 'energy': finite_number(analysis.get('energy')), 'key': analysis['key'], 'scale': analysis['scale'], 'durationSeconds': duration, 'moods': {label: finite_number(score, maximum=1) for label, score in moods.items()}}}


def parse_request(content_type, body):
    if '\r' in content_type or '\n' in content_type:
        raise InvalidRequest()
    message = BytesParser(policy=policy.default).parsebytes(('Content-Type: ' + content_type + '\r\nMIME-Version: 1.0\r\n\r\n').encode('ascii') + body)
    if message.get_content_type() != 'multipart/form-data' or not message.is_multipart() or message.defects:
        raise InvalidRequest()
    parts = list(message.iter_parts())
    if len(parts) != MAX_MULTIPART_PARTS:
        raise InvalidRequest()
    fields = {}
    for part in parts:
        name = part.get_param('name', header='content-disposition')
        if part.get_content_disposition() != 'form-data' or name not in ('audio', 'request_id', 'profile') or name in fields or part.is_multipart() or part.defects:
            raise InvalidRequest()
        fields[name] = part.get_payload(decode=True)
    try:
        request_identifier = str(uuid.UUID(fields['request_id'].decode('ascii')))
        selected_profile = fields['profile'].decode('ascii')
    except (ValueError, KeyError, UnicodeError, AttributeError):
        raise InvalidRequest() from None
    if selected_profile != PROFILE or not fields.get('audio'):
        raise InvalidRequest()
    return request_identifier, fields['audio']


class BridgeApplication:
    def __init__(self, token, engine=None, maximum_upload_bytes=DEFAULT_MAX_UPLOAD_BYTES, timeout_seconds=DEFAULT_TIMEOUT_SECONDS):
        if not token or maximum_upload_bytes <= 0 or timeout_seconds <= 0:
            raise ValueError('invalid_configuration')
        self.token = token.encode('utf-8')
        self.engine = engine
        self.maximum_upload_bytes = maximum_upload_bytes
        self.timeout_seconds = timeout_seconds
        self.analysis_lock = threading.Lock()

    def authorized(self, header):
        return hmac.compare_digest(header.encode('utf-8'), b'Bearer ' + self.token)

    def analyze(self, request_identifier, audio):
        with tempfile.TemporaryDirectory(prefix='audiomuse-') as temporary_directory:
            audio_path = Path(temporary_directory) / 'audio.bin'
            audio_path.write_bytes(audio)
            audio_hash = hashlib.sha256(audio).hexdigest()
            analysis, embedding = self.engine.analyze(audio_path)
            return response_envelope(request_identifier, audio_hash, self.engine, analysis, embedding)


def handler_factory(application):
    class BridgeRequestHandler(BaseHTTPRequestHandler):
        def log_message(self, format_string, *arguments):
            pass

        def setup(self):
            super().setup()
            self.connection.settimeout(application.timeout_seconds)

        def send_payload(self, status, payload):
            body = json.dumps(payload, allow_nan=False, separators=(',', ':')).encode('utf-8')
            self.send_response(status)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Connection', 'close')
            self.end_headers()
            self.wfile.write(body)
            self.close_connection = True

        def send_failure(self, status):
            self.send_payload(status, {'error': ERROR_CODES[status]})

        def authenticate(self):
            if not application.authorized(self.headers.get('Authorization', '')):
                self.send_failure(401)
                return False
            return True

        def do_GET(self):
            if not self.authenticate():
                return
            if self.path != '/health':
                self.send_failure(404)
                return
            self.send_payload(200, {'protocolVersion': PROTOCOL_VERSION, 'engineName': ENGINE_NAME, 'engineVersion': ENGINE_VERSION, 'profile': PROFILE, 'ready': application.engine is not None, 'busy': application.analysis_lock.locked()})

        def do_POST(self):
            if not self.authenticate():
                return
            if self.path != '/v1/analyze':
                self.send_failure(404)
                return
            if application.engine is None:
                self.send_failure(503)
                return
            if not application.analysis_lock.acquire(blocking=False):
                self.send_failure(429)
                return
            try:
                length_values = self.headers.get_all('Content-Length', [])
                if self.headers.get('Transfer-Encoding') or len(length_values) != 1 or not length_values[0].isdigit():
                    raise InvalidRequest()
                body_length = int(length_values[0])
                if body_length > application.maximum_upload_bytes:
                    self.send_failure(413)
                    return
                if body_length <= 0:
                    raise InvalidRequest()
                body = self.rfile.read(body_length)
                if len(body) != body_length:
                    raise InvalidRequest()
                request_identifier, audio = parse_request(self.headers.get('Content-Type', ''), body)
                self.send_payload(200, application.analyze(request_identifier, audio))
            except (InvalidRequest, UnicodeError, TimeoutError):
                self.send_failure(400)
            except Exception:
                self.send_failure(500)
            finally:
                application.analysis_lock.release()
    return BridgeRequestHandler


def main():
    token = os.environ.get('AUDIOMUSE_BRIDGE_TOKEN', '')
    if not token:
        raise SystemExit('AUDIOMUSE_BRIDGE_TOKEN is required')
    engine = None
    try:
        engine = AudioMuseEngine(os.environ.get('AUDIOMUSE_MODEL_MANIFEST', '/models/manifest.json'), os.environ.get('AUDIOMUSE_MODEL_DIRECTORY', '/models'))
    except Exception:
        print('AudioMuse models unavailable', flush=True)
    application = BridgeApplication(token, engine, int(os.environ.get('AUDIOMUSE_MAX_UPLOAD_BYTES', DEFAULT_MAX_UPLOAD_BYTES)), int(os.environ.get('AUDIOMUSE_READ_TIMEOUT_SECONDS', DEFAULT_TIMEOUT_SECONDS)))
    server = ThreadingHTTPServer((os.environ.get('AUDIOMUSE_BIND', '127.0.0.1'), int(os.environ.get('AUDIOMUSE_PORT', '8786'))), handler_factory(application))
    server.serve_forever()


if __name__ == '__main__':
    main()
