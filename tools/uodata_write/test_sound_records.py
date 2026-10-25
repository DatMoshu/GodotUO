"""Synthetic native sound preservation and refusal proofs; no client data."""
import hashlib
from pathlib import Path
import struct
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from guo import sound_records as codec
from guo.uorecord import AssetRecord
import uodata as writer


def payload(name='clang', samples=b'\x01\x00\xff\xff'):
    return name.encode().ljust(40, b'\0') + samples


def fixture(packaging):
    rows = {0: payload(), 2: payload('wind', b'\x02\x00')}
    if packaging == 'mul':
        first = rows[0]; second = rows[2]
        index = struct.pack('<iii', 0, len(first), -987) + struct.pack('<iii', -1, -1, 123) + struct.pack('<iii', len(first), len(second), 765)
        return {'sound.mul': first + second, 'soundidx.mul': index}
    header = struct.pack('<IIIqIi', 0x50594D, 5, 0x12345678, 28, 3, 2)
    table = struct.pack('<iq', 3, 0)
    data, entries = bytearray(), []
    for ident in (0, 2):
        offset = 28 + 12 + 3 * 34 + len(data)
        extra = bytes([ident + 7]) * 12
        row = rows[ident]
        entries.append(struct.pack('<qiiiQIh', offset, 12, len(row), len(row), codec.uop_hash(codec.PATTERN.format(ident)), 0x123, 0))
        data += extra + row
    entries.insert(1, bytes(34))
    return {'soundLegacyMUL.uop': header + table + b''.join(entries) + data}


@pytest.mark.parametrize('packaging', ['mul', 'uop'])
def test_identity_and_replacement_preserve_opaque_original_bytes(packaging):
    files = fixture(packaging); original = codec.parse(files, packaging)
    assert codec.patch(original, {0: original.records[0]}) == files
    incoming = codec.encode(original.records[0], '日本', b'\x03\x00' * 7)
    result = codec.patch(original, {0: incoming})
    parsed = codec.parse(result, packaging)
    assert parsed.records[0] == incoming and parsed.records[2] == original.records[2]
    if packaging == 'mul':
        assert result['sound.mul'] == files['sound.mul'] + incoming
        assert result['soundidx.mul'][8:] == files['soundidx.mul'][8:]
        assert struct.unpack_from('<ii', result['soundidx.mul']) == (len(files['sound.mul']), len(incoming))
    else:
        before, after = files['soundLegacyMUL.uop'], result['soundLegacyMUL.uop']
        at, entry_header, row = original.entries[0]
        assert after[:at] == before[:at] and after[at+34:len(before)] == before[at+34:]
        assert after[len(before):] == entry_header + incoming
        assert parsed.entries[0][1] == entry_header
        assert parsed.entries[2] == original.entries[2]


def test_pcm_only_edit_preserves_all_forty_name_bytes():
    original = b'clang\0opaque name tail'.ljust(40, b'!') + b'\x00\x01'
    result = codec.encode(original, 'clang', b'\xff\x7f')
    assert result[:40] == original[:40]
    assert codec.fingerprint(original) == hashlib.sha256(b'40:' + original).hexdigest()


@pytest.mark.parametrize('packaging', ['mul', 'uop'])
@pytest.mark.parametrize('ident', [1, -1, 0xFFFF, True])
def test_empty_invalid_and_boolean_ids_cannot_allocate(packaging, ident):
    library = codec.parse(fixture(packaging), packaging)
    with pytest.raises(ValueError, match='occupied physical'):
        codec.patch(library, {ident: payload()})


@pytest.mark.parametrize('raw', [b'', b'x'*40, b'x'*41, b'x'*43], ids=['empty', 'no-pcm', 'odd-one', 'odd-three'])
def test_invalid_sound_payloads_refused(raw):
    with pytest.raises(ValueError): codec.validate(raw)


@pytest.mark.parametrize('name', ['x'*33, '日本'*6, 'nul\0name'])
def test_native_name_budgets(name):
    with pytest.raises(ValueError): codec.encode(payload(), name, b'\0\0')


@pytest.mark.parametrize('damage', ['cycle', 'table-bounds', 'payload-bounds', 'compressed', 'duplicate', 'unknown', 'table-overlap'])
def test_malformed_uop_refused(damage):
    files = fixture('uop'); raw = bytearray(files['soundLegacyMUL.uop']); at = 40
    if damage == 'cycle': struct.pack_into('<q', raw, 32, 28)
    elif damage == 'table-bounds': struct.pack_into('<i', raw, 28, 999999)
    elif damage == 'payload-bounds': struct.pack_into('<q', raw, at, len(raw))
    elif damage == 'compressed': struct.pack_into('<h', raw, at+32, 1)
    elif damage == 'duplicate': raw[at+68+20:at+68+28] = raw[at+20:at+28]
    elif damage == 'unknown': struct.pack_into('<Q', raw, at+20, 123)
    elif damage == 'table-overlap':
        # A valid-looking sound payload cannot overlap the table/header itself.
        struct.pack_into('<q', raw, at, 0x10)
    with pytest.raises(ValueError): codec.parse({'soundLegacyMUL.uop': bytes(raw)}, 'uop')


@pytest.mark.parametrize('damage', ['truncated-index', 'negative-offset', 'outside-data', 'short-payload'])
def test_malformed_mul_refused(damage):
    files = fixture('mul'); index = bytearray(files['soundidx.mul'])
    if damage == 'truncated-index': index.pop()
    elif damage == 'negative-offset': struct.pack_into('<i', index, 0, -1)
    elif damage == 'outside-data': struct.pack_into('<i', index, 0, 999999)
    elif damage == 'short-payload': struct.pack_into('<i', index, 4, 40)
    files['soundidx.mul'] = bytes(index)
    with pytest.raises(ValueError): codec.parse(files, 'mul')


@pytest.mark.parametrize('packaging', ['mul', 'uop'])
def test_typed_sound_writer_replaces_and_reads_back_without_changing_install(tmp_path, packaging):
    install = tmp_path / 'source'; install.mkdir(); files = fixture(packaging)
    for name, data in files.items(): (install / name).write_bytes(data)
    stage = writer.Stage(tmp_path / 'stage', install)
    record = AssetRecord('sound', 0, payload('edited', b'\x07\x00'*8), {'packaging': packaging})
    writer.write_records(stage, [record], replace=True)
    assert writer.read_back_equal(stage, record)
    assert not stage.check_install_unchanged()
    assert {name: (install/name).read_bytes() for name in files} == files
    assert writer.sound_library(stage, packaging).records[2] == payload('wind', b'\x02\x00')


@pytest.mark.parametrize('damage', ['implicit', 'metadata', 'duplicate', 'empty-row', 'invalid-pcm', 'boolean', 'mixed-packaging', 'hardlink', 'unregistered'])
def test_typed_sound_entire_batch_refused_before_any_copy(tmp_path, damage):
    install = tmp_path / 'source'; install.mkdir()
    for name, data in fixture('mul').items(): (install/name).write_bytes(data)
    stage = writer.Stage(tmp_path / 'stage', install)
    good = AssetRecord('sound', 0, payload('edited'), {'packaging': 'mul'})
    bad = AssetRecord('sound', 2, payload(), {'packaging': 'mul'})
    records = [good, bad]
    if damage == 'metadata': bad.meta['unknown'] = 1
    elif damage == 'duplicate': bad.id = 0
    elif damage == 'empty-row': bad.id = 1
    elif damage == 'invalid-pcm': bad.data = bytes(41)
    elif damage == 'boolean': bad.id = True
    elif damage == 'mixed-packaging': bad.meta['packaging'] = 'uop'
    elif damage == 'hardlink':
        import os
        os.link(install/'sound.mul', stage.root/'sound.mul')
    elif damage == 'unregistered': (stage.root/'sound.mul').write_bytes(b'user content')
    before = {p.name: p.read_bytes() for p in stage.root.iterdir()}
    with pytest.raises((ValueError, FileNotFoundError)):
        writer.write_records(stage, records, replace=damage != 'implicit')
    assert {p.name: p.read_bytes() for p in stage.root.iterdir()} == before
