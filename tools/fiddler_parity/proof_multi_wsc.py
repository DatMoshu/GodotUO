"""Bidirectional scalar proof of the explicit WSC profile against pinned Fiddler."""
import argparse
import hashlib
import json
from pathlib import Path
import random
import sys

import multi_wsc_reference as reference
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'multi'))
import formats


def prove(binary: Path,out: Path):
    reference.shared.parity.protect_install(out); out.mkdir(parents=True,exist_ok=False)
    rng=random.Random(610930)
    rows=[dict(id=1650,x=120,y=-8,z=7,flags=0),dict(id=42,x=-32768,y=32767,z=-32768,flags=256),
          dict(id=65534,x=32767,y=-32768,z=32767,flags=-2147483648),dict(id=42,x=-32768,y=32767,z=-32768,flags=256),
          dict(id=0,x=0,y=0,z=0,flags=1)]
    rows += [dict(id=rng.randrange(65535),x=rng.randint(-32768,32767),y=rng.randint(-32768,32767),z=rng.randint(-32768,32767),flags=rng.randint(-2147483648,2147483647)) for _ in range(512)]
    tiles=[formats.Tile(r['id'],r['x'],r['y'],r['z'],r['flags']!=0,text_flags=r['flags']) for r in rows]
    guo=out/'guo.wsc'; guo.write_bytes(formats.write('wsc-fiddler',tiles))
    dynamic=[1650,42]
    read_spec=out/'read.json'; read_spec.write_text(json.dumps({'input':str(guo),'dynamic_ids':dynamic}))
    actual=reference.invoke(binary,'read',read_spec)['records']
    decoded=formats.read_fiddler_wsc(guo.read_text(),set(dynamic))
    expected=[dict(id=t.item,x=t.x,y=t.y,z=t.z,flags=t.text_flags,unknown=0) for t in decoded]
    if actual!=expected: raise AssertionError('Fiddler and GUO WSC decoding differ')
    if [(r['id'],r['x'],r['y'],r['z']) for r in actual] != [(r['id'],r['x'],r['y'],r['z']) for r in rows]:
        raise AssertionError('Relative fixture order/offsets changed')
    export=out/'fiddler.wsc'; spec=out/'write.json'; spec.write_text(json.dumps({'records':rows,'output':str(export)}))
    reference.invoke(binary,'write',spec)
    if guo.read_bytes()!=export.read_bytes(): raise AssertionError('WSC export bytes differ')
    if formats.read_fiddler_wsc(export.read_text(),set(dynamic))!=decoded: raise AssertionError('Fiddler WSC did not decode identically in GUO')
    # Dedicated absolute and seeded-minimum cases exercise the Fiddler heuristic.
    for name,x,y,want in [('world',120,80,(0,0)),('seeded',15000,15000,(5000,5000)),('large',20000,20000,(20000,20000))]:
        source=out/(name+'.wsc'); source.write_bytes(formats.write('wsc-fiddler',[formats.Tile(1650,x,y,7)]))
        spec=out/(name+'.json'); spec.write_text(json.dumps({'input':str(source)}))
        ref=reference.invoke(binary,'read',spec)['records'][0]; own=formats.read('wsc-fiddler',source.read_bytes())[0]
        if (ref['x'],ref['y'])!=want or (own.x,own.y)!=want: raise AssertionError(name+' heuristic mismatch')
    corrupt=out/'corrupt.wsc'; corrupt.write_bytes(guo.read_bytes().replace(b'\tID\t1650',b'\tID\tbad',1))
    spec=out/'corrupt.json'; spec.write_text(json.dumps({'input':str(corrupt)}))
    try: reference.invoke(binary,'read',spec)
    except ValueError: pass
    else: raise AssertionError('Pinned reader accepted corrupt decimal ID')
    try: formats.read('wsc-fiddler',corrupt.read_bytes())
    except formats.FormatError: pass
    else: raise AssertionError('GUO accepted corrupt decimal ID')
    report={'outcome':'pass','records':len(rows),'both_directions':True,'identical_export_bytes':True,'corrupt_id_refused_by_both':True,
        'dynamic_ids':dynamic,'coordinate_heuristic_cases':3,'sha256':hashlib.sha256(guo.read_bytes()).hexdigest(),
        'reference':json.loads((binary.parents[3]/'identity.json').read_text()),
        'expected_loss':'WSC has Color 0 and no stored flags; dynamic IDs reconstruct visibility on import',
        'scope':'scalar WSC records, dynamic classification, coordinate heuristic and Windows bytes; no spatial rendering or Fiddler GUI settings certification'}
    (out/'report.json').write_text(json.dumps(report,indent=2)+'\n'); return report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('--reference',type=Path,required=True); parser.add_argument('--out',type=Path,required=True)
    args=parser.parse_args(); print(json.dumps(prove(args.reference,args.out),indent=2))
