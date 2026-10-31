"""F14d: reviewed native sound replacement and recoverable staged promotion."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import shutil
import struct
import sys
import zlib

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hues
import replacement as transaction
import run as parity
from guo import sound_records as codec
from guo.uorecord import AssetRecord
from uodata_write import uodata as writer


def file(root: Path, name: str, optional=False) -> Path | None:
    found = [p for p in root.iterdir() if p.name.lower() == name.lower()]
    if not found and optional: return None
    if len(found) != 1 or not found[0].is_file() or found[0].is_symlink():
        raise ValueError(f'Expected one regular {name}')
    return found[0]


def hashes(root: Path, packaging: str) -> dict:
    return {n: parity.digest(p) if (p := file(root, n, n == 'Sound.def')) else None
            for n in (*codec.filenames(packaging), 'Sound.def')}


def library(root: Path, packaging: str) -> codec.Library:
    return codec.parse({n: hues.read(file(root, n), codec.MAX_FILE) for n in codec.filenames(packaging)}, packaging)


def decoded(oracle: Path, root: Path, ident: int, packaging: str, definitions: Path | None, version: str) -> dict:
    library(root, packaging)
    if definitions: hues.read(definitions, 2 * 1024 * 1024)
    result = parity.invoke(oracle, [str(root), 'sound-' + packaging, version, str(ident), str(definitions) if definitions else '-'])
    if len(result['records']) != 1 or result['records'][0]['id'] != ident:
        raise ValueError('Independent sound reader returned another ID')
    row = result['records'][0]; raw = base64.b64decode(row['raw_base64'], validate=True); codec.validate(raw)
    if (row['raw_sha256'] != hashlib.sha256(raw).hexdigest() or row['source_sha256'] != codec.fingerprint(raw)
        or row['pcm_sha256'] != hashlib.sha256(raw[40:]).hexdigest() or row['samples'] * 2 != len(raw) - 40):
        raise ValueError('Independent native sound name/PCM differs')
    return result


def summary(raw: bytes) -> dict:
    return {'name': codec.name(raw), 'samples': (len(raw)-40)//2, 'seconds': (len(raw)-40)/44100,
            'pcm_sha256': hashlib.sha256(raw[40:]).hexdigest(), 'raw_sha256': hashlib.sha256(raw).hexdigest()}


def verify(baseline: Path, candidate: Path, expected: Path, out: Path, oracle: Path) -> dict:
    spec = json.loads(hues.read(expected, 1024 * 1024)); packaging = spec['packaging']
    if spec.get('schema') != 1 or spec.get('domain') != 'sound': raise ValueError('Expected a native sound verification manifest')
    before_hashes, after_hashes = hashes(baseline, packaging), hashes(candidate, packaging)
    manifest_hash = parity.digest(expected)
    report = {'schema': 1, 'domain': 'sound', 'outcome': 'error'}
    try:
        before, after = library(baseline, packaging), library(candidate, packaging)
        target = spec['physical_id']; payload = hues.read(expected.parent/'expected.bin', codec.MAX_PCM + 40); codec.validate(payload)
        if parity.digest(expected.parent/'expected.bin') != spec['payload_sha256']: raise ValueError('Reviewed PCM payload changed')
        if before.records.keys() != after.records.keys(): raise ValueError('Native sound occupancy changed')
        if after.records[target] != payload or any(after.records[i] != row for i, row in before.records.items() if i != target):
            raise ValueError('Edited or untouched native sound payload differs')
        changed = payload != before.records[target]
        if packaging == 'mul':
            old_data, old_index = before.files['sound.mul'], before.files['soundidx.mul']
            wanted_index = bytearray(old_index)
            if changed: struct.pack_into('<ii', wanted_index, target * 12, len(old_data), len(payload))
            if after.files['soundidx.mul'] != wanted_index or after.files['sound.mul'] != old_data + (payload if changed else b''):
                raise ValueError('Sound index, opaque extra words or exact original MUL prefix differs')
            headers = 0; appended = len(payload) if changed else 0
        else:
            old_data, new_data = before.files['soundLegacyMUL.uop'], after.files['soundLegacyMUL.uop']
            at, header, row = before.entries[target]
            if changed:
                if new_data[:at] != old_data[:at] or new_data[at+34:len(old_data)] != old_data[at+34:] or new_data[len(old_data):] != header + payload:
                    raise ValueError('Original sound UOP bytes or exact appended payload differs')
                selected = after.entries[target]
                if selected[1] != header or selected[2] != (len(old_data), len(header), len(payload), len(payload), row[4], zlib.adler32(payload), 0):
                    raise ValueError('Edited sound UOP header or entry layout differs')
            elif new_data != old_data: raise ValueError('Identity sound UOP export changed bytes')
            if any(after.entries[i] != entry for i, entry in before.entries.items() if i != target):
                raise ValueError('Untouched sound UOP entry metadata differs')
            headers = len(before.entries); appended = len(header) + len(payload) if changed else 0
        proof = decoded(oracle, candidate, spec['id'], packaging, file(baseline, 'Sound.def', True), spec['client_version'])
        actual = proof['records'][0]
        if actual['physical_id'] != target or actual['affected_ids'] != spec['affected_ids'] or actual['raw_sha256'] != spec['payload_sha256']:
            raise ValueError('Pinned loader sound mapping or payload differs')
        report.update(outcome='pass', id=spec['id'], physical_id=target, affected_ids=actual['affected_ids'],
            preserved_records=len(before.records)-int(changed), preserved_uop_headers=headers, appended_bytes=appended,
            current=summary(before.records[target]), exported=summary(payload),
            oracle={'identity': proof['identity'], 'runtime': proof['runtime'], 'binary_sha256': parity.digest(oracle)})
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        report['error'] = str(error)
    report['inputs_unchanged'] = hashes(baseline, packaging) == before_hashes and hashes(candidate, packaging) == after_hashes and parity.digest(expected) == manifest_hash
    if not report['inputs_unchanged']: report.update(outcome='error', error='Sound verification inputs changed')
    out.mkdir(parents=True, exist_ok=True); transaction.json_write(out/'report.json', report)
    return report


def prepare(source: Path, stage: Path, edits: Path, plan: Path, version: str, oracle: Path) -> dict:
    source, edits = source.resolve(), edits.resolve(); stage, plan = hues.output(stage), hues.output(plan)
    edit = json.loads(hues.read(edits, 32 * 1024))
    if edit.get('schema') != 1 or edit.get('domain') != 'sound': raise ValueError('Expected a reviewed sound snapshot')
    ident, packaging = edit['id'], edit['packaging']; codec.filenames(packaging)
    if type(ident) is not int or not 0 <= ident < codec.LIMIT: raise ValueError('Sound ID is outside the loader range')
    pcm_path = Path(edit['pcm_file']).resolve()
    transaction.disjoint(source, stage, edits, pcm_path, plan)
    if plan.exists(): raise ValueError('Sound plan folder must be new')
    original_hashes, edit_hash, pcm_hash = hashes(source, packaging), parity.digest(edits), parity.digest(pcm_path)
    pcm = hues.read(pcm_path, codec.MAX_PCM)
    if hashlib.sha256(pcm).hexdigest() != edit['pcm_sha256']: raise ValueError('Working PCM hash differs')
    original = decoded(oracle, source, ident, packaging, file(source, 'Sound.def', True), version)['records'][0]
    if original['source_sha256'] != edit['source_sha256']: raise ValueError('Sound document belongs to another native source; reopen it')
    prior = transaction.stage_hashes(stage)
    if stage.exists():
        if not prior: raise ValueError('Existing empty stage refused')
        meta = json.loads(hues.read(stage/'stage.json', 1024*1024))
        if Path(meta['install']).resolve() != source: raise ValueError('Stage belongs to another installation')
        for name in (*codec.filenames(packaging), 'Sound.def'):
            if file(stage, name, True) and name.lower() not in meta['files']:
                raise ValueError('Unregistered staged sound file refused; register or move it before export')
    plan.mkdir(parents=True); baseline, candidate = plan/'baseline', plan/'candidate'; baseline.mkdir()
    if prior: shutil.copytree(stage, candidate)
    else: candidate.mkdir()
    staged = writer.Stage(candidate, source)
    for name in (*codec.filenames(packaging), 'Sound.def'):
        try: path = staged.read_path(name)
        except FileNotFoundError:
            if name == 'Sound.def': continue
            raise
        shutil.copyfile(path, baseline/name)
    mapped = decoded(oracle, baseline, ident, packaging, file(baseline, 'Sound.def', True), version)['records'][0]
    target = mapped['physical_id']; current = base64.b64decode(mapped['raw_base64'], validate=True)
    payload = codec.encode(current, edit['name'], pcm)
    (plan/'current.pcm').write_bytes(current[40:])
    # The UI's loaded name/PCM must match the native archive. Loose WAVs have a
    # different source fingerprint and are refused above rather than overwritten.
    writer.write_records(staged, [AssetRecord('sound', target, payload, {'packaging': packaging})], replace=True)
    (plan/'expected.bin').write_bytes(payload)
    expected = {'schema': 1, 'domain': 'sound', 'packaging': packaging, 'id': ident, 'physical_id': target,
        'affected_ids': mapped['affected_ids'], 'client_version': version, 'payload_sha256': hashlib.sha256(payload).hexdigest()}
    transaction.json_write(plan/'expected.json', expected)
    report = verify(baseline, candidate, plan/'expected.json', plan/'verification', oracle)
    if report['outcome'] != 'pass': raise ValueError('Independent sound verification failed; see plan/verification/report.json')
    if hashes(source, packaging) != original_hashes or parity.digest(edits) != edit_hash or parity.digest(pcm_path) != pcm_hash or transaction.stage_hashes(stage) != prior:
        raise ValueError('Sound source, working PCM, edits or stage changed during preparation')
    spec = {**expected, 'state': 'prepared', 'source': str(source), 'stage': str(stage), 'plan': str(plan),
        'source_hashes': original_hashes, 'prior_hashes': prior, 'candidate_hashes': transaction.stage_hashes(candidate),
        'baseline_hashes': transaction.stage_hashes(baseline), 'expected_sha256': parity.digest(plan/'expected.json'),
        'edit_file': str(edits), 'edit_sha256': edit_hash, 'pcm_file': str(pcm_path), 'pcm_file_sha256': pcm_hash,
        'current': report['current'], 'exported': report['exported'], 'appended_bytes': report['appended_bytes']}
    transaction.json_write(plan/'plan.json', spec); return spec


def apply(path: Path, oracle: Path) -> dict:
    spec, source, stage, plan = transaction.load_plan(path)
    if spec.get('domain') != 'sound': raise ValueError('Expected a reviewed sound plan')
    hues.output(stage); hues.output(plan)
    with transaction.stage_lock(stage):
        if (transaction.stage_hashes(stage) != spec['prior_hashes']
            or transaction.stage_hashes(plan/'candidate') != spec['candidate_hashes']
            or transaction.stage_hashes(plan/'baseline') != spec['baseline_hashes']
            or hashes(source, spec['packaging']) != spec['source_hashes']
            or parity.digest(Path(spec['edit_file'])) != spec['edit_sha256']
            or parity.digest(Path(spec['pcm_file'])) != spec['pcm_file_sha256']
            or parity.digest(plan/'expected.bin') != spec['payload_sha256']
            or parity.digest(plan/'expected.json') != spec['expected_sha256']):
            raise ValueError('Sound review inputs changed; prepare again')
        if verify(plan/'baseline', plan/'candidate', plan/'expected.json', plan/'recheck', oracle)['outcome'] != 'pass':
            raise ValueError('Independent sound recheck failed; stage is unchanged')
        return transaction.promote(spec, source, stage, plan)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__); commands = parser.add_subparsers(dest='command', required=True)
    prep = commands.add_parser('prepare')
    for name in ('source', 'stage', 'edits', 'plan'): prep.add_argument('--'+name, required=True, type=Path)
    prep.add_argument('--client-version', required=True); prep.add_argument('--oracle', type=Path, default=parity.default_oracle())
    apply_parser = commands.add_parser('apply'); apply_parser.add_argument('--plan', type=Path, required=True)
    apply_parser.add_argument('--confirm', action='store_true', required=True); apply_parser.add_argument('--oracle', type=Path, default=parity.default_oracle())
    recover = commands.add_parser('recover'); recover.add_argument('--stage', type=Path, required=True); recover.add_argument('--confirm', action='store_true', required=True)
    args = parser.parse_args()
    try:
        result = prepare(args.source, args.stage, args.edits, args.plan, args.client_version, args.oracle) if args.command == 'prepare' else apply(args.plan, args.oracle) if args.command == 'apply' else transaction.recover(hues.output(args.stage))
        print(json.dumps(result, indent=2)); return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        print(f'[sound-stage] {error}', file=sys.stderr); return 1


if __name__ == '__main__': raise SystemExit(main())
