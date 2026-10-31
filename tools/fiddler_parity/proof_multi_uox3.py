"""Compare UOX3 export bytes with the unchanged pinned Fiddler exporter."""
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
    rows=[dict(id=0,x=-32768,y=32767,z=-32768,flags=-2147483648),dict(id=65535,x=32767,y=-32768,z=32767,flags=2147483647),
          dict(id=0xabcd,x=12,y=-8,z=7,flags=0),dict(id=0xabcd,x=12,y=-8,z=7,flags=0)]
    rows += [dict(id=rng.randrange(65536),x=rng.randint(-32768,32767),y=rng.randint(-32768,32767),z=rng.randint(-32768,32767),flags=rng.randint(-2147483648,2147483647)) for _ in range(512)]
    tiles=[formats.Tile(r['id'],r['x'],r['y'],r['z'],r['flags']!=0,text_flags=r['flags']) for r in rows]
    own=out/'guo.dfn'; own.write_bytes(formats.write('uox3-fiddler',tiles))
    other=out/'fiddler.dfn'; spec=out/'write.json';spec.write_text(json.dumps({'records':rows,'output':str(other)}))
    reference.invoke(binary,'write-uox3',spec)
    if own.read_bytes()!=other.read_bytes(): raise AssertionError('UOX3 export bytes differ')
    legacy=formats.write('uox3',tiles)
    if own.read_bytes()==legacy: raise AssertionError('Legacy whitespace distinction missing')
    corrupt=out/'corrupt.json';corrupt.write_text(json.dumps({'records':[dict(id=65536,x=0,y=0,z=0,flags=1)],'output':str(out/'invalid.dfn')}))
    try: reference.invoke(binary,'write-uox3',corrupt)
    except ValueError: pass
    else: raise AssertionError('Pinned exporter wrapper accepted invalid uint16')
    try: formats.write('uox3-fiddler',[formats.Tile(65536,0,0,0)])
    except formats.FormatError: pass
    else: raise AssertionError('GUO accepted invalid uint16')
    report={'outcome':'pass','records':len(rows),'identical_export_bytes':True,'legacy_bytes_differ':True,'invalid_id_refused':True,
        'sha256':hashlib.sha256(own.read_bytes()).hexdigest(),'reference':json.loads((binary.parents[3]/'identity.json').read_text()),
        'scope':'export-only scalar IDs/coordinates/order/duplicates and Windows bytes; flags omitted by Fiddler; no independent importer/spatial rendering'}
    (out/'report.json').write_text(json.dumps(report,indent=2)+'\n');return report


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--reference',type=Path,required=True);parser.add_argument('--out',type=Path,required=True)
    args=parser.parse_args();print(json.dumps(prove(args.reference,args.out),indent=2))
