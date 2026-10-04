"""Place a validated native building into a validated district's vacant parcel."""
from __future__ import annotations

import json
from pathlib import Path
import shutil

from layout_import import native, routing


def combine(cfg, district, building, offset, name, out):
    district,building,out = map(lambda p:Path(p).resolve(),(district,building,out))
    if out.is_relative_to(cfg.client_data.resolve()) or out==district or out==building or out.is_relative_to(district) or out.is_relative_to(building):
        raise ValueError("combined output must be separate from source inputs and retail data")
    record=json.loads((district/'district.json').read_text(encoding='utf-8'))
    scene=json.loads((district/'scene.json').read_text(encoding='utf-8'))
    side=json.loads((building/'multi.json').read_text(encoding='utf-8'))
    source=json.loads((building/'layout-build.json').read_text(encoding='utf-8'))
    if record['status']!='native-valid' or source['status']!='native-valid' or not side['valid']:
        raise ValueError("combined inputs must have passed native validation")
    if source['world_requirements']['excavation']:
        raise ValueError("combining basement buildings needs a new world excavation project")
    comps=[native.Component(*c) for c in json.loads((building/'components.json').read_text(encoding='utf-8'))]
    dx,dy=offset
    centre=[dx+side['centre'][0],dy+side['centre'][1]]
    parts=[]
    for part in scene['parts']:
        p=dict(part)
        p['comps']=[native.Component(*c) for c in json.loads((district/'parts'/f"{p['name']}.json").read_text(encoding='utf-8'))]
        parts.append(p)
    occupied={(c.x+p['centre'][0],c.y+p['centre'][1]) for p in parts for c in p['comps'] if c.visible}
    incoming={(c.x+centre[0],c.y+centre[1]) for c in comps if c.visible}
    if incoming & occupied:
        raise ValueError("building intersects an existing native multi")
    incoming_bounds=[min(x for x,y in incoming),min(y for x,y in incoming),max(x for x,y in incoming),max(y for x,y in incoming)]
    for part in parts:
        b=part['bounds'];a=incoming_bounds
        if not (a[2]<b[0] or b[2]<a[0] or a[3]<b[1] or b[3]<a[1]):
            raise ValueError("native multi bounds overlap; the shard could lose tiles at the seam")
    width,height=record['size']
    if any(not (0<=x<width and 0<=y<height) for x,y in incoming):
        raise ValueError("building extends beyond the district world project")
    ox,oy=record['origin']
    land,statics={},[]
    for file in (district/'world'/'blocks').glob('*/*.json'):
        block=json.loads(file.read_text(encoding='utf-8'))
        bx,by=block['block']
        for y,row in enumerate(block['land']):
            for x,cell in enumerate(row.split()):
                land[(bx*8+x-ox,by*8+y-oy)]=int(cell.split(':')[1])
        for item in block['statics']:
            statics.append(native.Component(int(item['id'],16),bx*8+item['x']-ox,by*8+item['y']-oy,item['z']))
    td=native.TileData(cfg.client_data)
    if incoming & {(c.x,c.y) for c in statics if td.static(c.item)['flags']&0x40}:
        raise ValueError("building intersects an existing world blocker")
    if any(land.get(p)!=0 for p in incoming):
        raise ValueError("building requires a flat zero-height vacant parcel")
    part_name='imported_'+str(len(parts))+'_'+building.name
    parts.append({'name':part_name,'centre':centre,'bounds':incoming_bounds,
                  'doors':side['doors'],'components':len(comps),'comps':comps})
    tour=list(scene['tour'])
    building_tour=[{**s,'name':part_name+'_'+s['name'],'x':s['x']+centre[0],'y':s['y']+centre[1]} for s in side['stops']]
    building_tour.append({**side['stops'][0],'name':part_name+'_exit','x':side['stops'][0]['x']+centre[0],'y':side['stops'][0]['y']+centre[1]})
    flights=[{'foot':[f['foot'][0]+dx,f['foot'][1]+dy],
              'arrive':[f['arrive'][0]+dx,f['arrive'][1]+dy],'z_from':f['z'],'z_to':f['z']+f['rise']}
             for f in side['local']['stairs']]
    tour.extend(native.fort.walk_tour(building_tour,flights))
    all_parts=parts+[{'centre':[0,0],'comps':statics}]
    tour,issues=routing.short_tour(all_parts,tour,cfg.client_data,land,max_steps=6)
    issues+=native.walkcheck.check_tour(all_parts,tour,routing.tile_info([c for p in all_parts for c in p['comps']],cfg.client_data),land)
    if issues:
        raise ValueError('combined movement failed: '+'; '.join(issues))
    out.mkdir(parents=True,exist_ok=True)
    shutil.copytree(district/'world',out/'world',dirs_exist_ok=True)
    (out/'parts').mkdir(exist_ok=True)
    for p in parts:
        (out/'parts'/f"{p['name']}.json").write_text(json.dumps([c.as_list() for c in p['comps']]),encoding='utf-8')
    scene.update(name=name,parts=[{k:v for k,v in p.items() if k!='comps'} for p in parts],tour=tour)
    record.update(name=name,combined_sources={'district':str(district),'building':str(building),'offset':list(offset),
                  'building_layout_hash':source['layout_hash']},proof_status='not-run',route_validation={'passed':True,'problems':[]})
    (out/'scene.json').write_text(json.dumps(scene,indent=1),encoding='utf-8')
    (out/'district.json').write_text(json.dumps(record,indent=1),encoding='utf-8')
    every=[native.Component(c.item,c.x+p['centre'][0],c.y+p['centre'][1],c.z,c.visible) for p in all_parts for c in p['comps']]
    native.render.render(every,cfg.client_data,out/'preview.png')
    native.render.render(every,cfg.client_data,out/'preview_noroof.png',max_z=26)
    return {'name':name,'parts':len(parts),'stops':len(tour),'status':'native-valid'}
