"""Independent pinned sound-loader proofs and native transaction scenarios."""
import base64
import hashlib
import json
from pathlib import Path
import struct
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run as parity
import sounds as native
import replacement
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'uodata_write'))
from guo import sound_records as codec
from test_sound_records import fixture, payload


@pytest.fixture(scope='session')
def oracle():
    path = parity.default_oracle(); parity.doctor(path); return path


@pytest.mark.parametrize('packaging', ['mul', 'uop'])
def test_pinned_sound_pcm_name_fingerprint_and_def_aliases(tmp_path, oracle, packaging):
    files = fixture(packaging)
    if packaging == 'mul': files['soundidx.mul'] += struct.pack('<iii', -1, -1, 0) * 2
    for name, data in files.items(): (tmp_path/name).write_bytes(data)
    definitions = tmp_path / 'Sound.def'
    definitions.write_text('1 {0 2}\n3 {1}\n1 {0}\n4 {0 -1 2}\n2 {0}\n')
    # A loose WAV must never substitute for the native record being certified.
    (tmp_path/'Sounds').mkdir(); (tmp_path/'Sounds/1.wav').write_bytes(b'not the native archive')
    library = codec.parse(files, packaging)
    result = parity.invoke(oracle, [str(tmp_path), 'sound-'+packaging, '7.0.107.76', '1,2,3,4', str(definitions)])
    for row in result['records']:
        raw = library.records[2]
        assert row['physical_id'] == 2 and row['affected_ids'] == [1, 2, 3, 4]
        assert base64.b64decode(row['raw_base64']) == raw
        assert row['source_sha256'] == codec.fingerprint(raw)
        assert row['pcm_sha256'] == hashlib.sha256(raw[40:]).hexdigest()
        assert row['name'] == raw[:40].decode() and row['samples'] == 1


@pytest.mark.parametrize('packaging', ['mul', 'uop'])
def test_pinned_reader_decodes_changed_unicode_name_and_pcm(tmp_path, oracle, packaging):
    original = codec.parse(fixture(packaging), packaging)
    incoming = codec.encode(original.records[0], '日本 é', b'\xff\x7f\x00\x80')
    for name, data in codec.patch(original, {0: incoming}).items(): (tmp_path/name).write_bytes(data)
    row = parity.invoke(oracle, [str(tmp_path), 'sound-'+packaging, '7.0.107.76', '0', '-'])['records'][0]
    assert base64.b64decode(row['raw_base64']) == incoming
    assert row['pcm_sha256'] == hashlib.sha256(incoming[40:]).hexdigest()
    assert row['source_sha256'] == codec.fingerprint(incoming)
    assert row['name'].split('\0', 1)[0] == '日本 é' and row['samples'] == 2


@pytest.mark.parametrize('header', [b'x'*40, b'name\0opaque\xff\xfe\xc0\xaf\xed\xa0\x80'.ljust(40, b'!')], ids=['full-name', 'opaque-invalid-utf8'])
def test_pinned_reader_preserves_opaque_names_and_decoding_fingerprint(tmp_path, oracle, header):
    files = fixture('mul'); data = bytearray(files['sound.mul']); data[:40] = header; files['sound.mul'] = bytes(data)
    for name, raw in files.items(): (tmp_path/name).write_bytes(raw)
    original = codec.parse(files, 'mul').records[0]
    result = parity.invoke(oracle, [str(tmp_path), 'sound-mul', '7.0.107.76', '0', '-'])['records'][0]
    assert result['source_sha256'] == codec.fingerprint(original)
    assert codec.encode(original, codec.name(original), b'\x02\x00')[:40] == header


def transaction_fixture(tmp_path, packaging):
    source = tmp_path/'source'; source.mkdir(); files = fixture(packaging)
    for name, data in files.items(): (source/name).write_bytes(data)
    (source/'Sound.def').write_text('1 {2}\n')
    pcm = tmp_path/'working.pcm'; pcm.write_bytes(b'\x08\x00'*9)
    edit = {'schema': 1, 'domain': 'sound', 'packaging': packaging, 'id': 1, 'name': 'edited 日本',
        'source_sha256': codec.fingerprint(codec.parse(files, packaging).records[2]),
        'pcm_file': str(pcm), 'pcm_sha256': parity.digest(pcm)}
    edits = tmp_path/'edits.json'; edits.write_text(json.dumps(edit))
    return source, tmp_path/'stage', edits, tmp_path/'plan', pcm


@pytest.mark.parametrize('packaging', ['mul', 'uop'])
def test_native_sound_prepare_apply_and_exact_prior_stage_recovery(tmp_path, oracle, packaging):
    source, stage, edits, plan, pcm = transaction_fixture(tmp_path, packaging)
    before = native.hashes(source, packaging)
    # An unrelated staged sound and non-audio file must survive replacement.
    staged = native.writer.Stage(stage, source)
    native.writer.write_records(staged, [native.AssetRecord('sound', 0, payload('prior'), {'packaging': packaging})], replace=True)
    (stage/'notes.txt').write_text('keep this staged content')
    prior = replacement.stage_hashes(stage)
    result = native.prepare(source, stage, edits, plan, '7.0.107.76', oracle)
    assert result['physical_id'] == 2 and result['affected_ids'] == [1, 2]
    assert result['current']['name'] == 'wind' and result['exported']['name'] == 'edited 日本'
    assert result['current']['samples'] == 1 and result['exported']['samples'] == 9
    assert replacement.stage_hashes(stage) == prior
    native.apply(plan/'plan.json', oracle)
    after = native.library(stage, packaging)
    assert after.records[0] == payload('prior') and after.records[2][40:] == pcm.read_bytes()
    assert (stage/'notes.txt').read_text() == 'keep this staged content'
    replacement.recover(stage)
    assert replacement.stage_hashes(stage) == prior and native.hashes(source, packaging) == before


@pytest.mark.parametrize('packaging', ['mul', 'uop'])
def test_native_sound_first_stage_can_recover_absence(tmp_path, oracle, packaging):
    source, stage, edits, plan, pcm = transaction_fixture(tmp_path, packaging)
    native.prepare(source, stage, edits, plan, '7.0.107.76', oracle)
    assert not stage.exists()
    native.apply(plan/'plan.json', oracle); assert stage.is_dir()
    replacement.recover(stage); assert not stage.exists()


@pytest.mark.parametrize('change', ['source', 'definition', 'pcm', 'edits', 'candidate', 'expected-pcm', 'expected-manifest', 'baseline', 'prior-stage'])
def test_stale_sound_review_refuses_apply_without_stage_mutation(tmp_path, oracle, change):
    source, stage, edits, plan, pcm = transaction_fixture(tmp_path, 'mul')
    native.prepare(source, stage, edits, plan, '7.0.107.76', oracle)
    if change == 'source': (source/'sound.mul').write_bytes((source/'sound.mul').read_bytes()+b'\0\0')
    elif change == 'definition': (source/'Sound.def').write_text('1 {0}\n')
    elif change == 'pcm': pcm.write_bytes(b'\x09\x00')
    elif change == 'edits': edits.write_text(edits.read_text()+' ')
    elif change == 'candidate': (plan/'candidate/sound.mul').write_bytes(b'changed')
    elif change == 'expected-pcm': (plan/'expected.bin').write_bytes(payload('tampered'))
    elif change == 'expected-manifest': (plan/'expected.json').write_text('{}')
    elif change == 'baseline': (plan/'baseline/soundidx.mul').write_bytes(b'changed')
    elif change == 'prior-stage': stage.mkdir(); (stage/'keep.txt').write_text('user created this')
    before = replacement.stage_hashes(stage)
    with pytest.raises(ValueError, match='changed'): native.apply(plan/'plan.json', oracle)
    assert replacement.stage_hashes(stage) == before


@pytest.mark.parametrize('damage', ['pcm', 'name', 'untouched-record', 'opaque-index', 'uop-header', 'uop-checksum'])
def test_independent_sound_verification_rejects_corruption(tmp_path, oracle, damage):
    packaging = 'uop' if damage.startswith('uop') else 'mul'
    source, stage, edits, plan, pcm = transaction_fixture(tmp_path, packaging)
    native.prepare(source, stage, edits, plan, '7.0.107.76', oracle)
    candidate = plan/'candidate'
    if packaging == 'uop':
        path = candidate/'soundLegacyMUL.uop'; raw = bytearray(path.read_bytes())
        parsed = codec.parse({path.name: bytes(raw)}, packaging); at, header, row = parsed.entries[2]
        offset = row[0] if damage == 'uop-header' else at+28
    elif damage == 'opaque-index':
        path = candidate/'soundidx.mul'; raw = bytearray(path.read_bytes()); offset = 8
    else:
        path = candidate/'sound.mul'; raw = bytearray(path.read_bytes())
        parsed = native.library(candidate, packaging)
        offset = 0 if damage == 'untouched-record' else parsed.entries[2][2][0]+(40 if damage == 'pcm' else 0)
    raw[offset] ^= 1; path.write_bytes(raw)
    report = native.verify(plan/'baseline', candidate, plan/'expected.json', plan/'damaged', oracle)
    assert report['outcome'] == 'error' and not stage.exists()


@pytest.mark.parametrize('destination', ['install', 'parent', 'traversal'])
def test_sound_output_overlap_refused(tmp_path, oracle, destination):
    source, stage, edits, plan, pcm = transaction_fixture(tmp_path, 'mul')
    stage = {'install': source/'stage', 'parent': tmp_path, 'traversal': tmp_path/'a'/'..'/'stage'}[destination]
    with pytest.raises(ValueError): native.prepare(source, stage, edits, plan, '7.0.107.76', oracle)
    assert not plan.exists()


def test_current_staged_def_controls_reviewed_physical_row(tmp_path, oracle):
    source, stage, edits, plan, pcm = transaction_fixture(tmp_path, 'mul')
    staged = native.writer.Stage(stage, source)
    staged.path('Sound.def').write_text('1 {0}\n'); staged.save()
    prior = replacement.stage_hashes(stage)
    result = native.prepare(source, stage, edits, plan, '7.0.107.76', oracle)
    assert result['physical_id'] == 0 and result['affected_ids'] == [0, 1]
    assert result['current']['name'] == 'clang'
    native.apply(plan/'plan.json', oracle)
    assert native.library(stage, 'mul').records[2] == payload('wind', b'\x02\x00')
    replacement.recover(stage); assert replacement.stage_hashes(stage) == prior


@pytest.mark.parametrize('damage', ['source-fingerprint', 'name', 'pcm-hash', 'unregistered-def'])
def test_invalid_sound_capture_or_unregistered_context_refused(tmp_path, oracle, damage):
    source, stage, edits, plan, pcm = transaction_fixture(tmp_path, 'mul')
    edit = json.loads(edits.read_text())
    if damage == 'source-fingerprint': edit['source_sha256'] = '0'*64
    elif damage == 'name': edit['name'] = 'x'*33
    elif damage == 'pcm-hash': edit['pcm_sha256'] = '0'*64
    else:
        staged = native.writer.Stage(stage, source); staged.save(); (stage/'Sound.def').write_text('1 {0}\n')
    edits.write_text(json.dumps(edit)); prior = replacement.stage_hashes(stage)
    with pytest.raises(ValueError): native.prepare(source, stage, edits, plan, '7.0.107.76', oracle)
    assert replacement.stage_hashes(stage) == prior
