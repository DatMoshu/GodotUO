"""Read local B42 lot headers/packs and tile properties; never export source art.

Format contracts checked against the installed game's LotHeader, IsoLot,
IsoMetaGrid loader and IsoWorld tile-definition readers. Unknown versions fail.
The edge-to-cell conversion is an explicit 2:1 layout adaptation.
"""
from __future__ import annotations

import collections
import hashlib
import io
import json
from pathlib import Path
import re
import struct

from layout_import.cdda import digest
from layout_import import semantic


class Reader:
    def __init__(self, data):
        self.stream = io.BytesIO(data)

    def take(self, n):
        value = self.stream.read(n)
        if len(value) != n:
            raise ValueError("truncated Zomboid binary")
        return value

    def integer(self):
        return struct.unpack('<i', self.take(4))[0]

    def count(self, maximum=1_000_000):
        n = self.integer()
        if not 0 <= n <= maximum:
            raise ValueError(f"invalid Zomboid count: {n}")
        return n

    def line(self):
        result = self.stream.readline(65537)
        if not result.endswith(b'\n') or len(result) > 65536:
            raise ValueError("invalid Zomboid string")
        return result.rstrip(b'\r\n').decode('utf-8').strip()


def header(path):
    path = Path(path)
    data = path.read_bytes()
    r = Reader(data)
    magic = r.take(4)
    if magic != b'LOTH':
        r.stream.seek(0)
    version = r.integer()
    if version not in (0, 1):
        raise ValueError(f"unsupported lotheader version {version}")
    tiles = [r.line() for _ in range(r.count())]
    if version == 0:
        r.take(1)
    width, height = r.integer(), r.integer()
    if (width, height) != (8, 8):
        raise ValueError(f"unsupported chunk dimensions {width}x{height}")
    low, high = (0, r.integer()-1) if version == 0 else (r.integer(), r.integer())
    if not -32 <= low <= high <= 31:
        raise ValueError("invalid Zomboid levels")
    coords = re.fullmatch(r'(-?\d+)_(-?\d+)\.lotheader', path.name)
    if not coords:
        raise ValueError("lotheader filename must identify its cell")
    cx, cy = map(int, coords.groups())
    rooms = []
    for ident in range(r.count()):
        name, z = r.line(), r.integer()
        rects = [tuple(r.integer() for _ in range(4)) for _ in range(r.count())]
        if any(w <= 0 or h <= 0 or w*h > 1_000_000 for x,y,w,h in rects):
            raise ValueError("invalid room rectangle")
        objects = [tuple(r.integer() for _ in range(3)) for _ in range(r.count())]
        rooms.append({'id':ident,'name':name,'z':z,'rects':rects,'meta_objects':objects})
    buildings = []
    for ident in range(r.count()):
        ids = [r.integer() for _ in range(r.count())]
        if any(i < 0 or i >= len(rooms) for i in ids):
            raise ValueError("invalid building room reference")
        buildings.append({'id':ident,'rooms':ids})
    return {'path':str(path.resolve()),'sha256':hashlib.sha256(data).hexdigest(),
            'version':version,'cell':[cx,cy],'chunk_size':8,'cell_size':256,
            'min_level':low,'max_level':high,'tiles':tiles,'rooms':rooms,'buildings':buildings}


def tile_definitions(path):
    r = Reader(Path(path).read_bytes())
    if r.take(4) != b'tdef' or r.integer() != 1:
        raise ValueError(f"unsupported tile-definition format: {path}")
    result = {}
    for _ in range(r.count(1024)):
        name, image = r.line(), r.line()
        width,height,number = r.integer(),r.integer(),r.integer()
        for index in range(r.count(1024)):
            props = {r.line():r.line() for _ in range(r.count(4096))}
            result[f'{name}_{index}'] = {'properties':props,'tileset':name,'index':index}
    if r.stream.read(1):
        raise ValueError(f"trailing tile-definition data: {path}")
    return result


def properties(install):
    media = Path(install)/'media'
    # This is the installed core load order, not alphabetical patch order.
    names = ['newtiledefinitions.tiles','tiledefinitions_erosion.tiles',
             'tiledefinitions_overlays.tiles','tiledefinitions_b42chunkcaching.tiles',
             'tiledefinitions_noiseworks.patch.tiles','jumbo_trees.tiles','jumbo_trees_big.tiles']
    out, sources = {}, []
    for name in names:
        path = media/name
        if path.exists():
            records = tile_definitions(path)
            for key, record in records.items():
                if 'patch' in name and key in out:
                    out[key]['properties'].update(record['properties'])
                else:
                    out[key] = record
            sources.append({'file':name,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
    return out, sources


def chunk(path, info, x, y):
    """Decode one 8x8 chunk, in z/x/y order, with cross-level empty RLE."""
    data = Path(path).read_bytes()
    r = Reader(data)
    version = 0
    if r.take(4) == b'LOTP':
        version = r.integer()
        if version not in (0,1):
            raise ValueError(f"unsupported lotpack version {version}")
    base = 8 if version >= 1 else 0
    if not 0 <= x < 32 or not 0 <= y < 32:
        raise ValueError("chunk outside lot cell")
    r.stream.seek(base+4+(x*32+y)*8)
    offset = r.integer()
    if not base+8196 <= offset < len(data):
        raise ValueError("invalid lotpack chunk offset")
    r.stream.seek(offset)
    out, skip = {}, 0
    total = (info['max_level']-info['min_level']+1)*64
    position = 0
    for z in range(info['min_level'],info['max_level']+1):
        for sx in range(8):
            for sy in range(8):
                position += 1
                if skip:
                    skip -= 1
                    continue
                count = r.integer()
                if count == -1:
                    n = r.integer()
                    if not 1 <= n <= total-position+1:
                        raise ValueError("invalid lotpack empty run")
                    skip = n-1
                    continue
                if not 0 <= count <= 4096:
                    raise ValueError("invalid lotpack stack count")
                if count > 1:
                    room = r.integer()
                    indices = [r.integer() for _ in range(count-1)]
                    if any(i < 0 or i >= len(info['tiles']) for i in indices):
                        raise ValueError("invalid lotpack tile reference")
                    out[(x*8+sx,y*8+sy,z)] = {'room':room,'tiles':[info['tiles'][i] for i in indices]}
    return out


def scan(install, out, map_name='Muldraugh, KY'):
    folder = Path(install)/'media'/'maps'/map_name
    entries, problems = [], []
    for path in sorted(folder.glob('*.lotheader')):
        try:
            info = header(path)
            for building in info['buildings']:
                rooms = [info['rooms'][i] for i in building['rooms']]
                interior = [room for room in rooms if room['name'] != 'emptyoutside']
                if not interior:
                    continue
                rects = [rect for room in interior for rect in room['rects']]
                entries.append({'header':path.name,'header_sha256':info['sha256'],
                    'cell':info['cell'],'building':building['id'],'rooms':rooms,
                    'bounds':[min(q[0] for q in rects),min(q[1] for q in rects),
                              max(q[0]+q[2] for q in rects),max(q[1]+q[3] for q in rects)],
                    'levels':sorted({room['z'] for room in interior})})
        except ValueError as exc:
            problems.append({'file':path.name,'error':str(exc)})
    if not entries:
        raise ValueError(f"no supported buildings in {folder}")
    result = {'format':1,'source':'project-zomboid','map':map_name,'buildings':entries,
              'problems':problems,'policy':'local source geometry only; no source artwork redistribution'}
    Path(out).mkdir(parents=True,exist_ok=True)
    (Path(out)/'catalogue.json').write_text(json.dumps(result,indent=1),encoding='utf-8')
    return result


def room_use(name):
    key = name.lower()
    for use, words in [('bedroom',['bedroom']),('bathroom',['bathroom','toilet']),
                       ('kitchen',['kitchen']),('dining',['dining','living']),
                       ('storage',['storage','closet']),('shop',['store','shop']),
                       ('workshop',['garage','workshop']),('library',['library'])]:
        if any(word in key for word in words):
            return use
    return 'unclassified'


def stair_links(decoded, metadata, owned=None):
    """B/M/T triplets climb north or west; upper exit is beyond the top."""
    flags={}
    for at,stack in decoded.items():
        flags[at]=set()
        for tile in stack['tiles']:
            if tile not in metadata:
                raise ValueError(f'missing tile metadata: {tile}')
            flags[at].update(key for key in metadata[tile]['properties'] if key.startswith('stairs'))
    links=[]
    for (x,y,z),keys in flags.items():
        if owned is not None and (x,y,z) not in owned:
            continue
        for axis,(dx,dy) in [('N',(0,-1)),('W',(-1,0))]:
            if 'stairsB'+axis not in keys:
                continue
            middle=(x+dx,y+dy,z);top=(x+2*dx,y+2*dy,z)
            if 'stairsM'+axis not in flags.get(middle,set()) or 'stairsT'+axis not in flags.get(top,set()):
                raise ValueError('incomplete authored stair triplet')
            links.append({'from':[x,y,z],'to':[x+3*dx,y+3*dy,z+1],
                          'cells':[[x+i*dx,y+i*dy,z] for i in range(3)],'axis':axis})
    return links


def resolve(install, map_name, header_name, building_id, name):
    folder = Path(install)/'media'/'maps'/map_name
    if Path(header_name).name != header_name:
        raise ValueError("header must be a filename")
    info = header(folder/header_name)
    building = next((b for b in info['buildings'] if b['id']==building_id),None)
    if building is None:
        raise ValueError("unknown building")
    rooms = [info['rooms'][i] for i in building['rooms'] if info['rooms'][i]['name']!='emptyoutside']
    if not rooms:
        raise ValueError("building has no authored interior")
    rects = [q for room in rooms for q in room['rects']]
    x0,y0 = min(q[0] for q in rects)-2,min(q[1] for q in rects)-2
    x1,y1 = max(q[0]+q[2] for q in rects)+2,max(q[1]+q[3] for q in rects)+2
    if not (0<=x0<x1<=256 and 0<=y0<y1<=256):
        raise ValueError("cross-cell building requires neighboring headers; explicit block")
    tiles, defs = properties(install)
    pack = folder/f"world_{info['cell'][0]}_{info['cell'][1]}.lotpack"
    decoded = {}
    for cx in range(x0//8,(x1-1)//8+1):
        for cy in range(y0//8,(y1-1)//8+1):
            decoded.update(chunk(pack,info,cx,cy))
    w,h = (x1-x0)*2+1,(y1-y0)*2+1
    levels, pending = [], collections.Counter()
    authored = {}
    for room in rooms:
        for rx,ry,rw,rh in room['rects']:
            for x in range(rx,rx+rw):
                for y in range(ry,ry+rh):
                    authored[(x,y,room['z'])] = room
    relevant={at:stack for at,stack in decoded.items() if x0<=at[0]<x1 and y0<=at[1]<y1 and at[2] in {r['z'] for r in rooms}}
    connections=stair_links(relevant,tiles,set(authored))
    for z in sorted({room['z'] for room in rooms}):
        cells = {}
        for y in range(h):
            for x in range(w):
                sx,sy = x0+x//2,y0+y//2
                own = authored.get((sx,sy,z))
                cells[(x,y)] = {'x':x,'y':y,'terrain':'pz:floor' if own else 'pz:outside',
                    'furniture':'f_null','role':'floor' if own else 'exterior',
                    'indoors':bool(own),'walkable':True,'furnishing_role':None,'access':'open',
                    'flags':['INDOORS'] if own else [],'source_furniture_blocks':False,
                    'authored_room_name':own['name'] if own else None,
                    'authored_room_use':room_use(own['name']) if own else None,
                    'source':{'source':'project-zomboid','cell':info['cell'],'at':[sx,sy,z],
                              'room_id':own['id'] if own else None},'source_tiles':[]}
        for (sx,sy,sz), stack in decoded.items():
            if sz!=z or not x0<=sx<x1 or not y0<=sy<y1:
                continue
            x,y=(sx-x0)*2,(sy-y0)*2
            for tile in stack['tiles']:
                metadata = tiles.get(tile)
                if metadata is None:
                    raise ValueError(f"missing tile metadata: {tile}; cannot infer structural safety")
                props=metadata['properties']
                for dx in (0,1):
                    for dy in (0,1):
                        cells[(x+dx,y+dy)]['source_tiles'].append({'id':tile,'properties':props})
                for axis, keys in [('N',('WallN','WallNW','collideN','DoorWallN','doorN','windowN')),
                                   ('W',('WallW','WallNW','collideW','DoorWallW','doorW','windowW'))]:
                    if not any(k in props for k in keys):
                        continue
                    kind = 'door' if any(k in props for k in (f'DoorWall{axis}',f'door{axis}')) else 'window' if f'window{axis}' in props else 'wall'
                    for offset in range(3):
                        pos=(x+offset,y) if axis=='N' else (x,y+offset)
                        if pos not in cells:
                            continue
                        c=cells[pos]
                        # One native opening in the middle of each source edge.
                        role=kind if offset==1 else 'wall'
                        if c['role'] in ('door','window') and offset!=1:
                            continue
                        c.update(role=role,walkable=role=='door',terrain=f'pz:{role}',access='closed' if role=='door' else 'open')
                item_name=' '.join([tile,props.get('CustomName',''),props.get('GroupName','')]).lower()
                role=None
                for value, keys in [('bed',['bed']),('bath',['sink','toilet','bath','shower']),
                                    ('oven',['stove','oven']),('bookcase',['book']),('chest',['crate']),
                                    ('counter',['counter']),('chair',['chair','sofa']),('table',['table']),
                                    ('container',['fridge','cabinet','locker','shelf'])]:
                    if any(k in item_name for k in keys):
                        role=value
                        break
                if role and cells[(x+1,y+1)]['indoors']:
                    group='pz:'+metadata['tileset']+':'+props.get('GroupName','')+':'+props.get('CustomName',role)
                    for dx in (0,1):
                        for dy in (0,1):
                            c=cells[(x+dx,y+dy)]
                            if c['indoors'] and c['role']=='floor':
                                c.update(furniture=group,furnishing_role=role)
                elif 'solidfloor' not in props and not any(k.startswith('stairs') for k in props) and not any(k in props for k in ('WallN','WallW','WallNW','collideN','collideW','doorN','doorW','windowN','windowW')):
                    pending['unmapped-detail-tile']+=1
        for c in cells.values():
            if z>0 and c['indoors'] and c['role']=='floor' and not any('solidfloor' in t['properties'] for t in c['source_tiles']):
                c.update(role='void',walkable=False,terrain='pz:floor-void')
        for connection in connections:
            sx,sy,sz=connection['from'];tx,ty,tz=connection['to']
            if sz==z:
                at=((sx-x0)*2+1,(sy-y0)*2+1)
                if at not in cells or not cells[at]['indoors']:
                    raise ValueError('stair foot is outside the selected authored building')
                cells[at].update(role='stair-up',walkable=True,terrain='pz:stair-up')
                cells[at]['flags'].append('GOES_UP')
            if tz==z:
                at=((tx-x0)*2+1,(ty-y0)*2+1)
                if at not in cells or not cells[at]['indoors'] or not cells[at]['walkable']:
                    raise ValueError('stair has no authored upper floor landing')
                cells[at].update(role='stair-down',terrain='pz:stair-down')
                cells[at]['flags'].append('GOES_DOWN')
                for bx,by,_ in connection['cells']:
                    for dx in (0,1):
                        for dy in (0,1):
                            p=((bx-x0)*2+dx,(by-y0)*2+dy)
                            if p in cells and cells[p]['role']=='void' and cells[p]['indoors']:
                                cells[p]['stairwell']=True
        for cell in cells.values():
            cell['lineage']=cell['source']
        level=semantic.infer_rooms({'z':z,'cells':sorted(cells.values(),key=lambda c:(c['y'],c['x']))})
        level['authored_rooms']=[{'id':r['id'],'name':r['name'],'use':room_use(r['name']),
            'source_rects':r['rects'],'cells':[[c['x'],c['y']] for c in level['cells']
                if c['source']['room_id']==r['id'] and c['role']=='floor']}
            for r in rooms if r['z']==z]
        for room in level['rooms']:
            votes=collections.Counter(authored[(x0+x//2,y0+y//2,z)]['name'] for x,y in room['cells'] if (x0+x//2,y0+y//2,z) in authored)
            if votes:
                label=votes.most_common(1)[0][0]
                room.update(use=room_use(label),label_origin='authored',confidence=1.0,evidence=[f'PZ room: {label}'],source_room_name=label)
        levels.append(level)
    # Native UO flat roof is a documented adaptation of the authored footprint.
    top=max(l['z'] for l in levels)
    mask={(c['x'],c['y']) for l in levels for c in l['cells'] if c['indoors'] or c['role'] in ('wall','door','window')}
    roof=[]
    for y in range(h):
        for x in range(w):
            c=dict(levels[-1]['cells'][y*w+x])
            c.update(role='roof' if (x,y) in mask else 'void',walkable=(x,y) in mask,indoors=False,furniture='f_null',furnishing_role=None,terrain='pz:adapted-roof' if (x,y) in mask else 'pz:void')
            roof.append(c)
    levels.append(semantic.infer_rooms({'z':top+1,'cells':roof}))
    result={'format':1,'kind':'semantic-layout','name':name,'width':w,'height':h,'levels':levels,
            'provenance':{'source':'project-zomboid','map':map_name,'header':header_name,'header_sha256':info['sha256'],
                'pack_sha256':hashlib.sha256(pack.read_bytes()).hexdigest(),'tile_definitions':defs,
                'building':building_id,'cell':info['cell'],'source_bounds':[x0,y0,x1,y1],
                'license':'proprietary local installation; source artwork and binary files excluded'},
            'adaptations':[{'kind':'edge-wall-to-cell-grid','scale':2},
                           {'kind':'native-flat-roof-from-authored-footprint'}],
            'source_coverage':dict(pending)}
    result['provenance']['authored_stair_connections']=connections
    supplied={room['z'] for room in rooms}
    if any(c['to'][2] not in supplied or c['from'][2] not in supplied for c in connections):
        result['blocked']='source stair connection extends beyond supplied authored building floors'
    elif len(supplied)>1 and not connections:
        result['blocked']='multilevel building has no supported authored B/M/T staircase'
    result['layout_hash']=semantic.layout_hash(result)
    return result
