import hashlib
import pathlib
import subprocess
import sys

UPSTREAM_COMMIT = 'ac65c469d288a45092db77cc76c1e347a9e14f48'
UPSTREAM_PREIMAGES = {'config.py': '14ae75768ecef91f1c07b64c0e290f1f925bbd381226f9a70f2fd9de7b82e693', 'tasks/onnx_utils.py': 'f7d048143ac09d16d54abf66611a0b8deb292ecf0d7b26f0e66d638f1f8d1e44'}
BOOTSTRAP_CALL = '\n_apply_db_overrides()\n'
PLUGIN_FUNCTION_START = '\ndef _plugin_onnx_providers():\n'
PLUGIN_FUNCTION_END = '\ndef _default_sess_options():\n'
PLUGIN_DISABLED_FUNCTION = '\ndef _plugin_onnx_providers():\n    return []\n\n'


def prepare(source_directory):
    source_directory = pathlib.Path(source_directory)
    commit = subprocess.check_output(['git', '-C', str(source_directory), 'rev-parse', 'HEAD'], text=True).strip()
    if commit != UPSTREAM_COMMIT:
        raise ValueError('upstream_commit_mismatch')
    for filename, checksum in UPSTREAM_PREIMAGES.items():
        if hashlib.sha256((source_directory / filename).read_bytes()).hexdigest() != checksum:
            raise ValueError('upstream_source_mismatch')
    configuration_path = source_directory / 'config.py'
    configuration = configuration_path.read_text()
    if not configuration.endswith(BOOTSTRAP_CALL):
        raise ValueError('bootstrap_boundary_mismatch')
    configuration_path.write_text(configuration[:-len(BOOTSTRAP_CALL)] + '\n')
    provider_path = source_directory / 'tasks/onnx_utils.py'
    provider = provider_path.read_text()
    start = provider.index(PLUGIN_FUNCTION_START)
    end = provider.index(PLUGIN_FUNCTION_END, start)
    provider_path.write_text(provider[:start] + PLUGIN_DISABLED_FUNCTION + provider[end:])


if __name__ == '__main__':
    prepare(sys.argv[1])
