import json
import pathlib
import shutil
import sys
import tempfile
import urllib.request

from bridge import MODEL_FILENAMES, MODEL_RELEASE_URL, REQUIRED_MODEL_NAMES, UPSTREAM_COMMIT, PROFILE, file_sha256

DOWNLOAD_TIMEOUT_SECONDS = 120
MAXIMUM_MODEL_BYTES = 16777216
DOWNLOAD_CHUNK_BYTES = 1048576
MANIFEST_PATH = pathlib.Path(__file__).with_name('manifest.json')


def download_models(destination_directory, open_url=urllib.request.urlopen):
    destination_directory = pathlib.Path(destination_directory)
    destination_directory.mkdir(parents=True, exist_ok=True)
    manifest = json.loads(MANIFEST_PATH.read_text())
    if manifest['engineCommit'] != UPSTREAM_COMMIT or manifest['profile'] != PROFILE or {model['name'] for model in manifest['models']} != REQUIRED_MODEL_NAMES:
        raise ValueError('invalid_manifest')
    with tempfile.TemporaryDirectory(prefix='model-download-', dir=destination_directory) as temporary_directory:
        for model in manifest['models']:
            filename = MODEL_FILENAMES[model['name']]
            if model['url'] != MODEL_RELEASE_URL + filename:
                raise ValueError('invalid_model_url')
            target_path = destination_directory / filename
            if target_path.is_file() and not target_path.is_symlink() and file_sha256(target_path) == model['sha256']:
                continue
            temporary_path = pathlib.Path(temporary_directory) / filename
            total_bytes = 0
            with open_url(model['url'], timeout=DOWNLOAD_TIMEOUT_SECONDS) as response, temporary_path.open('wb') as model_file:
                if not response.geturl().startswith('https://'):
                    raise ValueError('invalid_download_transport')
                while True:
                    chunk = response.read(DOWNLOAD_CHUNK_BYTES)
                    if not chunk:
                        break
                    total_bytes += len(chunk)
                    if total_bytes > MAXIMUM_MODEL_BYTES:
                        raise ValueError('model_too_large')
                    model_file.write(chunk)
            if file_sha256(temporary_path) != model['sha256']:
                raise ValueError('model_integrity_failed')
            temporary_path.replace(target_path)
        shutil.copyfile(MANIFEST_PATH, destination_directory / MANIFEST_PATH.name)


if __name__ == '__main__':
    download_models(sys.argv[1])
