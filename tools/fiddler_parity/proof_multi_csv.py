"""Bidirectional Fiddler CSV proof including signed Unk1 preservation."""
import argparse
import hashlib
import json
from pathlib import Path
import random
import sys

import multi_text_reference as reference
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'multi'))
import formats


def prove(binary: Path,out: Path):
    reference.parity.protect_install(out); out.mkdir(parents=True,exist_ok=False)
    rng=random.Random(610930)
    rows=[dict(id=0x1650,x=120,y=-80,z=7,flags=256,unknown=256),dict(id=0,x=-32768,y=32767,z=-32768,flags=-2147483648,unknown=-2147483648),
          dict(id=65535,x=32767,y=-32768,z=32767,flags=2147483647,unknown=2147483647),dict(id=10,x=0,y=0,z=0,flags=0,unknown=-1),dict(id=10,x=0,y=0,z=0,flags=0,unknown=-1)]
    rows += [dict(id=rng.randrange(65536),x=rng.randint(-32768,32767),y=rng.randint(-32768,32767),z=rng.randint(-32768,32767),flags=rng.randint(-2147483648,2147483647),unknown=rng.randint(-2147483648,2147483647)) for _ in range(512)]
    tiles=[formats.Tile(r['id'],r['x'],r['y'],r['z'],r['flags']!=0,text_flags=r['flags'],text_extra=r['unknown']) for r in rows]
    own=out/'guo.csv'; own.write_bytes(formats.write('csv-fiddler',tiles))
    if reference.invoke(binary,'read-csv',own)['records']!=rows: raise AssertionError('Fiddler CSV decoded different records')
    other=out/'fiddler.csv'; spec=out/'write.json'; spec.write_text(json.dumps({'records':rows,'output':str(other)}))
    reference.invoke(binary,'write-csv',spec)
    if own.read_bytes()!=other.read_bytes(): raise AssertionError('CSV export bytes differ')
    if formats.read('csv-fiddler',other.read_bytes())!=tiles: raise AssertionError('GUO did not retain Fiddler CSV fields')
    variants=out/'variants.csv'; variants.write_bytes(b'TileID,OffsetX,OffsetY,OffsetZ,Flag,Cliloc\r\n1650,12,-8,7,ffffffff,\r\n0x0001,0,0,0,100,256\r\n0x0001,0,0,0,1,0x80000000\r\n')
    actual=reference.invoke(binary,'read-csv',variants)['records']; decoded=formats.read('csv-fiddler',variants.read_bytes())
    expected=[dict(id=t.item,x=t.x,y=t.y,z=t.z,flags=t.text_flags,unknown=t.text_extra) for t in decoded]
    if actual!=expected: raise AssertionError('Legacy empty/decimal/hex extra or bare hex IDs differ')
    corrupt=out/'corrupt.csv'; corrupt.write_bytes(own.read_bytes().replace(b'0x1650',b'invalid',1))
    try: reference.invoke(binary,'read-csv',corrupt)
    except ValueError: pass
    else: raise AssertionError('Pinned CSV reader accepted corrupt ID')
    try: formats.read('csv-fiddler',corrupt.read_bytes())
    except formats.FormatError: pass
    else: raise AssertionError('GUO CSV reader accepted corrupt ID')
    report={'outcome':'pass','records':len(rows),'both_directions':True,'identical_export_bytes':True,'legacy_extra_variants':True,
        'corrupt_id_refused_by_both':True,'sha256':hashlib.sha256(own.read_bytes()).hexdigest(),
        'reference':json.loads((binary.parents[3]/'identity.json').read_text()),'scope':'CSV scalar records, raw signed flags/Unk1 and Windows bytes; no spatial renderer'}
    (out/'report.json').write_text(json.dumps(report,indent=2)+'\n'); return report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('--reference',type=Path,required=True); parser.add_argument('--out',type=Path,required=True)
    args=parser.parse_args(); print(json.dumps(prove(args.reference,args.out),indent=2))
