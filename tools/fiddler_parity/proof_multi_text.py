"""Cross-tool TXT proof using the pinned Fiddler parser/exporter, not a self round-trip.

python tools/fiddler_parity/proof_multi_text.py --reference <Reference.dll> --out <new-folder>
Synthetic scalar records only; no client art, spatial conversion or other formats certified.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import random
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'multi'))
import formats
import multi_text_reference as reference


def prove(binary: Path, out: Path) -> dict:
    reference.parity.protect_install(out)
    out.mkdir(parents=True, exist_ok=False)
    rng = random.Random(610930)
    rows = [dict(id=ident, x=x, y=y, z=z, flags=flags) for ident, x, y, z, flags in (
        (0x1650, 120, -80, 7, 256), (0, -32768, 32767, -32768, -2147483648),
        (65535, 32767, -32768, 32767, 2147483647), (10, 0, 0, 0, 0), (10, 0, 0, 0, 0))]
    rows += [dict(id=rng.randrange(65536), x=rng.randint(-32768,32767), y=rng.randint(-32768,32767),
                  z=rng.randint(-32768,32767), flags=rng.randint(-2147483648,2147483647)) for _ in range(512)]
    tiles = [formats.Tile(r['id'], r['x'], r['y'], r['z'], r['flags'] != 0, text_flags=r['flags']) for r in rows]
    guo = out/'guo.txt'; guo.write_bytes(formats.write('txt-fiddler', tiles))
    got = reference.invoke(binary, 'read', guo)['records']
    expected = [dict(**r, unknown=0) for r in rows]
    if got != expected: raise AssertionError('Fiddler did not decode GUO TXT records exactly')
    spec = out/'write.json'; fid = out/'fiddler.txt'
    spec.write_text(json.dumps({'records': rows, 'output': str(fid)}), encoding='utf-8')
    reference.invoke(binary, 'write', spec)
    if guo.read_bytes() != fid.read_bytes(): raise AssertionError('GUO and Fiddler output bytes differ')
    if formats.read('txt-fiddler', fid.read_bytes()) != tiles: raise AssertionError('GUO did not decode Fiddler output exactly')
    # Bare decimal-looking IDs are independently established as hexadecimal.
    bare = out/'bare.txt'; bare.write_bytes(b'1650 120 -80 7 -2\r\n')
    actual = reference.invoke(binary, 'read', bare)['records'][0]
    if actual['id'] != 0x1650 or formats.read('txt-fiddler', bare.read_bytes())[0].item != actual['id']:
        raise AssertionError('Bare hexadecimal IDs differ')
    padded=out/'padded.txt'; padded.write_bytes(b'00001650 +012 -008 007 -0002\r\n')
    padded_actual=reference.invoke(binary,'read',padded)['records'][0]
    padded_tile=formats.read('txt-fiddler',padded.read_bytes())[0]
    if padded_actual != dict(id=padded_tile.item,x=padded_tile.x,y=padded_tile.y,z=padded_tile.z,flags=padded_tile.text_flags,unknown=0):
        raise AssertionError('Padded hexadecimal/decimal values differ')
    report = {'outcome':'pass', 'records':len(rows), 'both_directions':True, 'identical_export_bytes':True,
              'bare_hex_id':actual['id'], 'padded_input':True, 'sha256':hashlib.sha256(guo.read_bytes()).hexdigest(),
              'reference':json.loads((binary.parents[3]/'identity.json').read_text()),
              'scope':'TXT scalar record order, IDs, offsets, signed flags and Windows export bytes; no spatial renderer/other formats'}
    (out/'report.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    return report


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference', type=Path, required=True); parser.add_argument('--out', type=Path, required=True)
    args=parser.parse_args()
    print(json.dumps(prove(args.reference, args.out), indent=2))
