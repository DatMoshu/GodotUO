"""Seeded selection of PZ houses for vacant CDDA parcels, with native checks."""
from __future__ import annotations

import contextlib
import hashlib
import json
from pathlib import Path
import random
import shutil

from layout_import import run
from layout_import.combine import combine


def eligible_house(building, max_storeys=1):
    levels=building['levels']
    return (levels == list(range(len(levels))) and 1<=len(levels)<=max_storeys
            and 3<=len(building['rooms'])<=12
            and building['bounds'][2]-building['bounds'][0]<=10
            and building['bounds'][3]-building['bounds'][1]<=10
            and any('bedroom' in room['name'] for room in building['rooms']))


def populate(cfg, district, source, catalogue, database, native_catalogue, decor_database, seed, count, name, out, max_storeys=1):
    district,source,out=Path(district).resolve(),Path(source).resolve(),Path(out).resolve()
    if out.is_relative_to(source) or out.is_relative_to(cfg.client_data.resolve()) or out.is_relative_to(district):
        raise ValueError("hybrid outputs must be separate from input district and source installations")
    census=json.loads(Path(catalogue).read_text(encoding='utf-8'))
    original=json.loads((district/'district.json').read_text(encoding='utf-8'))
    selection_inputs={'census_sha256':hashlib.sha256(Path(catalogue).read_bytes()).hexdigest(),
                      'district_sha256':hashlib.sha256((district/'district.json').read_bytes()).hexdigest(),
                      'selection_algorithm':'python-random-shuffle-v1'}
    if original['status']!='native-valid':
        raise ValueError("hybrid needs an already validated CDDA district")
    if max_storeys not in (1,2) or count<1:
        raise ValueError('hybrid requires a positive count and max-storeys 1 or 2')
    candidates=[b for b in census['buildings'] if eligible_house(b,max_storeys)]
    vacant=[p for p in original['parcels'] if p['overmap_terrain']=='field' and not p.get('native')]
    rng=random.Random(seed)
    candidates.sort(key=lambda b:(b['header'],b['building']))
    vacant.sort(key=lambda p:p['omt'])
    rng.shuffle(candidates);rng.shuffle(vacant)
    if not candidates or not vacant:
        raise ValueError("no eligible house or vacant field parcel")
    out.mkdir(parents=True,exist_ok=True)
    current=district
    accepted,blocked=[],[]
    x0,y0,_,_=original['bounds']
    for parcel_index,parcel in enumerate(vacant[:count]):
        for attempt in range(min(8,len(candidates))):
            candidate=candidates.pop()
            index=len(accepted)
            work=out/'imports'/f'{parcel_index}_{attempt}'
            semantic_out=work/'semantic';native_out=work/'native'
            work.mkdir(parents=True,exist_ok=True)
            with (work/'import.log').open('w',encoding='utf-8') as log, contextlib.redirect_stdout(log),contextlib.redirect_stderr(log):
                status=run.main(['zomboid-resolve','--source',str(source),'--map',census['map'],
                    '--header',candidate['header'],'--building',str(candidate['building']),
                    '--name',f'{name}_house{index}','--out',str(semantic_out),'--db',str(database)])
                if status==0:
                    layout=json.loads((semantic_out/'layout.json').read_text(encoding='utf-8'))
                    status=run.main(['build','--db',str(database),'--profile',layout['profile_id'],
                        '--layout',str(semantic_out/'layout.json'),'--catalogue',str(native_catalogue),
                        '--decor-db',str(decor_database),'--out',str(native_out)])
            if status:
                blocked.append({'header':candidate['header'],'building':candidate['building'],'log':str(work/'import.log')})
                continue
            offset=[(parcel['omt'][0]-x0)*24,(parcel['omt'][1]-y0)*24]
            assembled=out/'assembly'/str(index)
            try:
                result=combine(cfg,current,native_out,offset,name,assembled)
            except ValueError as exc:
                blocked.append({'header':candidate['header'],'building':candidate['building'],'reason':str(exc)})
                continue
            accepted.append({'header':candidate['header'],'building':candidate['building'],'parcel':parcel['omt'],
                'offset':offset,'native':str(native_out),
                'catalogue':json.loads((native_out/'catalogue-build.json').read_text(encoding='utf-8'))})
            current=assembled
            break
    if not accepted:
        (out/'hybrid-selection.json').write_text(json.dumps({'seed':seed,'max_storeys':max_storeys,
            'inputs':selection_inputs,'accepted':[],'blocked':blocked},indent=1),encoding='utf-8')
        raise ValueError("no candidate passed native geometry, parcel placement and movement; inspect hybrid-selection.json")
    for folder in ('world','parts'):
        shutil.copytree(current/folder,out/folder,dirs_exist_ok=True)
    for file in ('scene.json','district.json','preview.png','preview_noroof.png'):
        shutil.copyfile(current/file,out/file)
    selection={'format':1,'seed':seed,'max_storeys':max_storeys,'inputs':selection_inputs,'requested':count,'accepted':accepted,'blocked':blocked,
               'vacant_parcels':len(vacant),'status':'native-valid','gameplay':'not-run'}
    (out/'hybrid-selection.json').write_text(json.dumps(selection,indent=1),encoding='utf-8')
    record=json.loads((out/'district.json').read_text(encoding='utf-8'))
    record['hybrid_selection']=selection
    (out/'district.json').write_text(json.dumps(record,indent=1),encoding='utf-8')
    return {'seed':seed,'requested':count,'accepted':len(accepted),'blocked_attempts':len(blocked),
            'vacant_parcels':len(vacant),'status':'native-valid','gameplay':'not-run'}
