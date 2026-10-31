"""Characterize WSC before changing the existing GUO interchange contract.

Checks a pinned independent Fiddler reader/exporter. Known differences from
GUO's legacy profile are recorded, never counted as parity passes.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

import multi_wsc_reference as reference

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'multi'))
import formats


def probe(binary: Path, out: Path) -> dict:
    reference.shared.parity.protect_install(out)
    out.mkdir(parents=True, exist_ok=False)
    cases = []

    def read_case(name, tiles, expected, dynamic=None):
        source = out/(name+'.wsc')
        source.write_bytes(formats.write('wsc', tiles))
        spec = out/(name+'.json')
        spec.write_text(json.dumps({'input':str(source), **({'dynamic_ids':dynamic} if dynamic is not None else {})}))
        actual = reference.invoke(binary, 'read', spec)['records']
        rows = [(r['id'],r['x'],r['y'],r['z'],r['flags']) for r in actual]
        if rows != expected: raise AssertionError(f'{name}: expected {expected}, got {rows}')
        legacy = formats.read('wsc', source.read_bytes())
        same = [(t.item,t.x,t.y,t.z,1 if t.visible else 0) for t in legacy] == rows
        cases.append({'name':name, 'fiddler':actual, 'legacy_guo_scalar_match':same,
                      'legacy_hues':[t.hue for t in legacy], 'dynamic_ids':dynamic})

    read_case('relative', [formats.Tile(1650,-2,-3,7),formats.Tile(42,1,2,-2)],
              [(1650,-2,-3,7,1),(42,1,2,-2,1)])
    read_case('positive_world', [formats.Tile(1650,120,80,7),formats.Tile(42,123,82,-2)],
              [(1650,0,0,7,1),(42,3,2,-2,1)])
    read_case('one_positive_axis', [formats.Tile(1650,120,-8,7),formats.Tile(42,123,-6,-2)],
              [(1650,120,-8,7,1),(42,123,-6,-2,1)])
    read_case('dynamic_items', [formats.Tile(1650,-2,-3,7),formats.Tile(42,1,2,-2)],
              [(1650,-2,-3,7,0),(42,1,2,-2,1)], [1650])
    read_case('ignored_color_and_visibility', [formats.Tile(1650,-2,-3,7,False,21)],
              [(1650,-2,-3,7,1)])
    # The original uses 0xFFFF as an internal sentinel and leaves a zero-filled
    # allocated slot. Record this defect so production import can refuse it.
    read_case('ffff_sentinel', [formats.Tile(65535,-2,-3,7)], [(0,0,0,0,0)])
    # The original minimum starts at 10000 instead of int.MaxValue.
    read_case('minimum_seed_10000', [formats.Tile(1650,20000,20000,7)],
              [(1650,20000,20000,7,1)])

    rows = [dict(id=1650,x=-2,y=-3,z=7,flags=0),dict(id=42,x=1,y=2,z=-2,flags=256)]
    exported = out/'fiddler-export.wsc'; spec = out/'write.json'
    spec.write_text(json.dumps({'records':rows,'output':str(exported)}))
    reference.invoke(binary,'write',spec)
    expected = ''.join(f'SECTION WORLDITEM {i}\r\n{{\r\n\tID\t{r["id"]}\r\n\tX\t{r["x"]}\r\n\tY\t{r["y"]}\r\n\tZ\t{r["z"]}\r\n\tColor\t0\r\n}}\r\n' for i,r in enumerate(rows)).encode('ascii')
    if exported.read_bytes() != expected: raise AssertionError('Pinned WSC export syntax changed')
    report = {'outcome':'characterized', 'reference':json.loads((binary.parents[3]/'identity.json').read_text()),
        'cases':cases, 'canonical_export_bytes_checked':True,
        'legacy_guo_export_bytes_match': formats.write('wsc',[formats.Tile(r['id'],r['x'],r['y'],r['z'],r['flags']!=0) for r in rows]) == expected,
        'scope':'pinned scalar WSC behavior and explicit existing GUO differences; not a certified editor WSC profile'}
    (out/'report.json').write_text(json.dumps(report,indent=2)+'\n')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference',type=Path,required=True); parser.add_argument('--out',type=Path,required=True)
    args = parser.parse_args()
    print(json.dumps(probe(args.reference,args.out),indent=2))
