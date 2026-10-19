"""Offline installer contracts; every installation is a disposable fixture."""
import hashlib
import importlib.util
import io
import json
import os
import subprocess
import sys
import types
from pathlib import Path

import pytest

spec = importlib.util.spec_from_file_location('setup_studio', Path(__file__).with_name('setup_studio.py'))
studio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(studio)


@pytest.fixture
def entry():
    data = b'verified synthetic model'
    return {'file': 'model.bin', 'dest': 'checkpoints', 'sha256': hashlib.sha256(data).hexdigest(),
            'size': len(data), 'source': 'https', 'url': 'https://example.org/model.bin', 'data': data}


def test_manifest_is_pinned():
    manifest = studio.load_manifest(studio.MANIFEST)
    assert len([e for e in manifest['models'] if not studio.informational(e)]) == 12


@pytest.mark.parametrize('path', ['../outside', '/absolute', 'C:/drive', '..\\outside'])
def test_reject_escaping_paths(tmp_path, path):
    with pytest.raises(ValueError):
        studio.child(tmp_path, path)


def test_doctor_hashes_existing_alias_without_writes(tmp_path, entry, monkeypatch):
    entry['aliases'] = ['alias.bin']
    dest = tmp_path / 'models' / 'checkpoints'
    dest.mkdir(parents=True)
    (dest / 'alias.bin').write_bytes(entry['data'])
    monkeypatch.setattr(studio.urllib.request, 'urlopen', lambda *a, **k: pytest.fail('network in doctor'))
    before = {p: p.stat().st_mtime_ns for p in tmp_path.rglob('*')}
    assert studio.cmd_check(tmp_path, {'custom_nodes': [], 'models': [entry]}) == 0
    assert before == {p: p.stat().st_mtime_ns for p in tmp_path.rglob('*')}
    (dest / 'alias.bin').write_bytes(b'x' * entry['size'])
    assert studio.cmd_check(tmp_path, {'custom_nodes': [], 'models': [entry]}) == 1


class Response(io.BytesIO):
    def __init__(self, data, status=200, content_range=''):
        super().__init__(data)
        self.status = status
        self.headers = {'Content-Range': content_range}


@pytest.mark.parametrize('resume,status', [(False, 200), (True, 200), (True, 206)])
def test_download_resume_and_range_ignored(tmp_path, entry, monkeypatch, resume, status):
    dest = tmp_path / 'model.bin'
    part = dest.with_suffix('.bin.part')
    if resume:
        part.write_bytes(entry['data'][:4])
    body = entry['data'][4:] if status == 206 else entry['data']
    def fetch(request, **kwargs):
        assert bool(request.get_header('Range')) == resume
        return Response(body, status, f"bytes 4-{entry['size']-1}/{entry['size']}")
    monkeypatch.setattr(studio.urllib.request, 'urlopen', fetch)
    assert studio.download_http(entry, dest)
    assert dest.read_bytes() == entry['data']
    assert not part.exists()


def test_bad_hash_never_promoted(tmp_path, entry, monkeypatch):
    monkeypatch.setattr(studio.urllib.request, 'urlopen', lambda *a, **k: Response(b'x' * entry['size']))
    dest = tmp_path / 'model.bin'
    assert not studio.download_http(entry, dest)
    assert not dest.exists()


def test_wrong_range_refused(tmp_path, entry, monkeypatch):
    dest = tmp_path / 'model.bin'
    dest.with_suffix('.bin.part').write_bytes(b'veri')
    monkeypatch.setattr(studio.urllib.request, 'urlopen', lambda *a, **k: Response(b'bad', 206, 'bytes 1-3/9'))
    with pytest.raises(ValueError, match='resume offset'):
        studio.download_http(entry, dest)
    assert not dest.exists()


def test_existing_drift_never_replaced(tmp_path, entry, monkeypatch):
    dest = tmp_path / 'models' / 'checkpoints' / 'model.bin'
    dest.parent.mkdir(parents=True)
    dest.write_bytes(b'personal model')
    monkeypatch.setattr(studio, 'download_http', lambda *a: pytest.fail('must not download over drift'))
    assert studio.cmd_install(tmp_path, {'custom_nodes': [], 'models': [entry]}, '') == 1
    assert dest.read_bytes() == b'personal model'


def test_node_pin_and_dirty_tree(tmp_path):
    pack = tmp_path / 'pack'
    pack.mkdir()
    def git(*args):
        return subprocess.run(['git', '-C', str(pack), *args], check=True, capture_output=True, text=True).stdout.strip()
    git('init')
    (pack / 'node.py').write_text('# fixture')
    git('add', 'node.py')
    git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.org', '-c', 'commit.gpgsign=false', 'commit', '-m', 'fixture')
    commit = git('rev-parse', 'HEAD')
    index_time = (pack / '.git' / 'index').stat().st_mtime_ns
    source_time = (pack / 'node.py').stat().st_mtime_ns
    os.utime(pack / 'node.py', ns=(source_time + 2_000_000_000, source_time + 2_000_000_000))
    assert studio.node_verified(pack, commit)
    assert (pack / '.git' / 'index').stat().st_mtime_ns == index_time
    assert not studio.node_verified(pack, '0' * 40)
    (pack / 'node.py').write_text('# drift')
    assert not studio.node_verified(pack, commit)
    assert not studio.git_clone({'commit': commit}, pack)
    assert (pack / 'node.py').read_text() == '# drift'


def test_unpinned_manifest_rejected(entry):
    entry['sha256'] = None
    with pytest.raises(ValueError, match='hash'):
        studio.validate_manifest({'format': 1, 'custom_nodes': [], 'models': [entry]})


def test_configured_directory_order(tmp_path, monkeypatch):
    monkeypatch.setenv('UO_COMFY_DIR', str(tmp_path / 'env'))
    assert studio.resolve_comfy_dir({}, str(tmp_path / 'cli')) == tmp_path / 'cli'
    assert studio.resolve_comfy_dir({}, None) == tmp_path / 'env'


def test_interrupted_download_preserves_resumable_partial(tmp_path, entry, monkeypatch):
    class Interrupted(Response):
        def read(self, size):
            if self.tell():
                raise OSError('fixture connection lost')
            return super().read(4)
    dest = tmp_path / 'model.bin'
    monkeypatch.setattr(studio.urllib.request, 'urlopen', lambda *a, **k: Interrupted(entry['data']))
    with pytest.raises(OSError, match='connection lost'):
        studio.download_http(entry, dest)
    assert not dest.exists()
    assert dest.with_suffix('.bin.part').read_bytes() == entry['data'][:4]


def test_managed_models_never_claim_integrity(tmp_path, capsys):
    (tmp_path / 'models').mkdir()
    assert studio.cmd_check(tmp_path, {'custom_nodes': [], 'models': [
        {'source': 'node-managed', 'file': 'voice', 'dest': 'voice'}]}) == 0
    assert '[UNVERIFIED] voice' in capsys.readouterr().out


def test_node_install_fetches_pinned_commit_instead_of_latest(tmp_path):
    repo = tmp_path / 'upstream'
    repo.mkdir()
    def git(*args):
        return subprocess.run(['git', '-C', str(repo), *args], check=True,
                              capture_output=True, text=True).stdout.strip()
    git('init')
    source = repo / 'node.py'
    source.write_text('# pinned')
    git('add', 'node.py')
    identity = ('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.org',
                '-c', 'commit.gpgsign=false')
    git(*identity, 'commit', '-m', 'pinned')
    pinned = git('rev-parse', 'HEAD')
    source.write_text('# newer upstream')
    git('add', 'node.py')
    git(*identity, 'commit', '-m', 'newer')
    dest = tmp_path / 'install' / 'pack'
    assert studio.git_clone({'repo': str(repo), 'commit': pinned}, dest)
    assert (dest / 'node.py').read_text() == '# pinned'
    assert studio.node_verified(dest, pinned)


def test_check_cli_is_offline_and_ignores_other_run_modules(tmp_path, monkeypatch):
    (tmp_path / 'models').mkdir()
    manifest = tmp_path / 'manifest.json'
    manifest.write_text(json.dumps({'format': 1, 'custom_nodes': [], 'models': []}))
    monkeypatch.setitem(sys.modules, 'run', types.ModuleType('unrelated_tool_run'))
    monkeypatch.setattr(studio.urllib.request, 'urlopen', lambda *a, **k: pytest.fail('network in check'))
    assert studio.main(['--check', '--manifest', str(manifest), '--comfy-dir', str(tmp_path)]) == 0


def test_missing_git_names_dependency_and_doctor(monkeypatch, capsys):
    monkeypatch.setattr(studio.shutil, 'which', lambda name: None)
    assert studio.main(['--check']) == 1
    message = capsys.readouterr().out
    assert 'install Git' in message and 'doctor.bat' in message
