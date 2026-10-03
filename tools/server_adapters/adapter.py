# SPDX-License-Identifier: BSD-2-Clause
"""Strict neutral-export validation and native server package generation."""
from __future__ import annotations
import hashlib
import json
import math
import re
from decimal import Decimal
from fractions import Fraction
from pathlib import Path

SECTIONS = ('items', 'tiles', 'maps', 'regions', 'decorations', 'loot', 'creatures')
CAPABILITIES = {'servuo': SECTIONS, 'runuo': SECTIONS, 'pol': ('items',), 'sphere': ('items',), 'uox3': ('items',)}
IDENTITY = re.compile(r'[a-z0-9][a-z0-9-]{0,63}:[a-z0-9][a-z0-9-]{0,63}\Z')


def require(value, message):
    if not value:
        raise ValueError(message)


def fields(row, required, optional=()):
    require(isinstance(row, dict) and set(required) <= row.keys() and row.keys() <= set(required) | set(optional), 'Missing or unknown fields: ' + str(row)[:180])


def integer(value, low, high):
    require(type(value) is int and low <= value <= high, f'Integer outside {low}..{high}: {value!r}')
    return value


def number(value, low, high):
    require(type(value) in (int, float) and math.isfinite(value) and low <= value <= high, 'Invalid finite number')
    return value


def text(value, size=100):
    require(isinstance(value, str) and 0 < len(value) <= size and all(ord(c) >= 32 and c not in '\u2028\u2029' for c in value), 'Invalid single-line text')
    return value


def rows(value, maximum):
    require(isinstance(value, list) and len(value) <= maximum, 'Invalid section size')
    return value


def unique(pairs, message):
    require(len(pairs) == len(set(pairs)), message)


def load(path):
    def object_pairs(pairs):
        unique([k for k, _ in pairs], 'Duplicate JSON field')
        return dict(pairs)
    require(path.stat().st_size <= 16 * 1024 * 1024, 'Export exceeds 16 MiB')
    return json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=object_pairs)


def validate(data, backend):
    require(backend in CAPABILITIES, 'Unknown backend')
    fields(data, ('schema', 'identity_hash'), SECTIONS)
    require(data['schema'] == 'guo/server-content@1', 'Unsupported schema')
    require(isinstance(data['identity_hash'], str) and re.fullmatch('[0-9a-f]{64}', data['identity_hash']), 'Invalid deployment hash')
    all_ids = []
    for section in SECTIONS:
        for row in rows(data.get(section, []), 65536 if section in ('items', 'tiles', 'maps') else 256):
            require(isinstance(row, dict) and isinstance(row.get('identity'), str) and IDENTITY.fullmatch(row['identity']), 'Invalid component identity')
            all_ids.append(row['identity'])
        require(not data.get(section) or section in CAPABILITIES[backend], backend + ' does not support ' + section + '; deployment refused before writing files')
    unique(all_ids, 'Duplicate component identity')
    items = {r['identity'] for r in data.get('items', [])}
    for row in data.get('items', []):
        fields(row, ('identity', 'graphic', 'name', 'weight', 'movable'))
        integer(row['graphic'], 1, 65534); text(row['name']); number(row['weight'], 0, 100000)
        require(type(row['movable']) is bool, 'movable must be boolean')
    for row in data.get('loot', []):
        fields(row, ('identity', 'content')); fields(row['content'], ('entries',))
        entries = rows(row['content']['entries'], 64); require(entries, 'Empty loot table')
        budget = 0
        for e in entries:
            fields(e, ('item', 'chance', 'min', 'max')); require(e['item'] in items, 'Unresolved loot item')
            number(e['chance'], 0, 1); integer(e['min'], 1, 256); integer(e['max'], e['min'], 256); budget += e['max']
        require(budget <= 256, 'Loot item budget exceeded')
    loot = {r['identity'] for r in data.get('loot', [])}
    for row in data.get('creatures', []):
        fields(row, ('identity', 'content')); c = row['content']
        fields(c, ('name', 'body', 'hue', 'sound', 'ai', 'strength', 'dexterity', 'intelligence', 'hits', 'damage_min', 'damage_max', 'armor', 'fame', 'karma', 'tactics', 'wrestling', 'resist'), ('loot',))
        text(c['name']); require(c['ai'] in ('animal', 'melee'), 'Unsupported creature AI')
        for key, low, high in [('body', 1, 65535), ('hue', 0, 16383), ('sound', -1, 65535), ('damage_min', 0, 10000), ('damage_max', c['damage_min'], 10000), ('armor', 0, 1000), ('fame', 0, 32000), ('karma', -32000, 32000)]: integer(c[key], low, high)
        for key in ('strength', 'dexterity', 'intelligence', 'hits'): integer(c[key], 1, 100000)
        for key in ('tactics', 'wrestling', 'resist'): number(c[key], 0, 120)
        require('loot' not in c or c['loot'] in loot, 'Unresolved creature loot')
    tile_ids = []
    for row in data.get('tiles', []):
        fields(row, ('identity', 'id', 'content')); tile_ids.append(integer(row['id'], 0, 65535)); c = row['content']
        fields(c, (), ('name', 'flags', 'height', 'weight', 'layer', 'animation', 'light'))
        # ItemData on these servers has no light field: never pretend it was applied.
        require(not ({'light', 'animation'} & c.keys()), backend + ' ItemData has no light/animation metadata override')
        for key, value in c.items():
            if key == 'name': text(value, 20)
            else: integer(value, 0, (2**64-1 if key == 'flags' else 65535 if key == 'animation' else 255))
    unique(tile_ids, 'Conflicting tile binding')
    blocks = []
    for row in data.get('maps', []):
        fields(row, ('identity', 'facet', 'content')); facet = integer(row['facet'], 0, 255); fields(row['content'], ('blocks',))
        require(row['content']['blocks'], 'Empty map component')
        for block in rows(row['content']['blocks'], 65536):
            fields(block, ('x', 'y', 'land'), ('statics',)); x = integer(block['x'], 0, 8191); y = integer(block['y'], 0, 8191); blocks.append((facet, x, y))
            require(len(rows(block['land'], 64)) == 64, 'Expected 64 row-major land cells')
            for cell in block['land']:
                fields(cell, ('graphic', 'z')); integer(cell['graphic'], 0, 16383); integer(cell['z'], -128, 127)
            for cell in rows(block.get('statics', []), 1024):
                fields(cell, ('graphic', 'x', 'y', 'z', 'hue')); integer(cell['graphic'], 1, 65534); integer(cell['x'], 0, 7); integer(cell['y'], 0, 7); integer(cell['z'], -128, 127); integer(cell['hue'], 0, 16383)
    unique(blocks, 'Conflicting map blocks'); require(len(blocks) <= 65536, 'Map block budget exceeded')
    names = []
    for row in data.get('regions', []):
        fields(row, ('identity', 'music_id', 'content')); integer(row['music_id'], -1, 65535); c = row['content']
        fields(c, ('name', 'facet', 'priority', 'areas'), ('enter_message', 'exit_message', 'music'))
        text(c['name']); integer(c['facet'], 0, 255); integer(c['priority'], 0, 150); names.append((c['facet'], c['name']))
        for key in ('enter_message', 'exit_message'):
            if key in c:
                require(isinstance(c[key], str), 'Region messages must be strings')
                if c[key]: text(c[key], 512)
        require(c['areas'], 'Empty region'); total = 0
        for a in rows(c['areas'], 64):
            fields(a, ('x', 'y', 'z', 'width', 'height', 'depth')); integer(a['x'], 0, 65535); integer(a['y'], 0, 65535); integer(a['z'], -128, 127); integer(a['width'], 1, 65535); integer(a['height'], 1, 65535); integer(a['depth'], 1, 128-a['z']); total += a['width'] * a['height']
        require(total <= 64*1024*1024, 'Region budget exceeded')
    unique(names, 'Duplicate region name on facet')
    total = 0
    for row in data.get('decorations', []):
        fields(row, ('identity', 'facet', 'items')); integer(row['facet'], 0, 255); ids = []
        require(row['items'], 'Empty decoration set')
        for c in rows(row['items'], 4096):
            fields(c, ('id', 'graphic', 'x', 'y', 'z', 'hue')); require(isinstance(c['id'], str) and re.fullmatch('[a-z0-9][a-z0-9-]{0,63}', c['id']), 'Invalid placement id'); ids.append(c['id'])
            for key, lo, hi in [('graphic', 1, 65534), ('x', 0, 65535), ('y', 0, 65535), ('z', -128, 127), ('hue', 0, 16383)]: integer(c[key], lo, hi)
            total += 1
        unique(ids, 'Duplicate placement id')
    require(total <= 16384, 'Decoration budget exceeded')


def symbol(identity):
    # Stable native name, independent of declaration order and graphic bindings.
    return 'guo_' + hashlib.sha256(identity.encode('ascii')).hexdigest()


def literal(value):
    if isinstance(value, str): return json.dumps(value, ensure_ascii=True)
    if type(value) is bool: return str(value).lower()
    return repr(value)


def native_name(name):
    # Native parsers interpret delimiters, comments, macros and percent plural forms.
    require(re.fullmatch(r"[A-Za-z0-9 .,!?_'()-]+", name), 'Native item names support plain ASCII letters, numbers and punctuation only')
    return name


def native(data, backend, slots, existing_graphics=()):
    files = {}; mapping = {}; lines = ['// GUO deployment ' + data['identity_hash']] if backend != 'pol' else ['# GUO deployment ' + data['identity_hash']]
    if backend == 'sphere':
        for graphic in sorted({r['graphic'] for r in data.get('items', [])} - set(existing_graphics)):
            lines += [f'[ITEMDEF 0{graphic:x}]', f'DEFNAME=i_guo_graphic_{graphic:x}', 'TYPE=t_normal', '']
    for r in sorted(data.get('items', []), key=lambda r: r['identity']):
        name = native_name(r['name']); key = symbol(r['identity']); graphic = r['graphic']; weight = Decimal(str(r['weight']))
        mapping[r['identity']] = key
        if backend == 'pol':
            require(r['identity'] in slots, 'POL requires an explicit --slots mapping from identity to a reserved object type')
            objtype = integer(slots[r['identity']], 0x10000, 0x7fffffff); frac = Fraction(weight)
            require(frac.numerator <= 65535 and frac.denominator <= 65535, 'POL weight fraction exceeds native ushort range')
            mapping[r['identity']] = objtype
            lines += [f'Item 0x{objtype:x}', '{', f'  Name {key}', f'  Desc {name}', f'  Graphic 0x{graphic:x}', f'  Weight {frac.numerator}/{frac.denominator}', f'  Movable {int(r["movable"])}', f'  CProp guo_identity s{r["identity"]}', '}']
        elif backend == 'sphere':
            require(weight * 10 == int(weight * 10) and weight <= Decimal('6553.4'), 'Sphere weight requires tenths of a stone, maximum 6553.4')
            lines += [f'[ITEMDEF i_{key}]', f'ID=0{graphic:x}', f'NAME={name}', 'TYPE=t_normal', f'WEIGHT={weight:.1f}', 'ON=@Create', f'ATTR=0{0x20 if r["movable"] else 0x10:x}', f'TAG.GUO_IDENTITY={r["identity"]}', '']
            mapping[r['identity']] = 'i_' + key
        else:
            require(weight * 100 == int(weight * 100) and weight <= 21474836, 'UOX3 weight requires hundredths of a stone within signed int range')
            lines += [f'[{key}]', '{', f'ID=0x{graphic:04x}', f'NAME={name}', f'WEIGHT={int(weight*100)}', f'MOVABLE={1 if r["movable"] else 2}', 'PILEABLE=0', '}']
    if backend == 'pol':
        unique(list(mapping.values()), 'Duplicate POL object type')
        files['pkg/guo_content/config/itemdesc.cfg'] = '\n'.join(lines)+'\n'
        files['pkg/guo_content/pkg.cfg'] = 'Enabled 1\nName guo_content\nVersion 1.0\n'
    elif backend == 'sphere': files['scripts/guo_content.scp'] = '\n'.join(lines)+'\n[EOF]\n'
    else: files['dfndata/items/guo_content.dfn'] = '\n'.join(lines)+'\n'
    return files, mapping


def render(data, backend, slots=None, existing_graphics=()):
    validate(data, backend)
    if backend in ('servuo', 'runuo'):
        from . import csharp
        files = {'Scripts/Custom/GUO/GUOContent.cs': csharp.render(data)}
        mapping = {r['identity']: r['identity'] for r in data.get('items', [])}
    else: files, mapping = native(data, backend, slots or {}, existing_graphics)
    manifest = {'schema': 'guo/server-adapter@1', 'backend': backend, 'identity_hash': data['identity_hash'],
        'export_sha256': hashlib.sha256(json.dumps(data, sort_keys=True, separators=(',', ':')).encode()).hexdigest(),
        'capabilities': list(CAPABILITIES[backend]), 'identities': mapping,
        'files': {name: hashlib.sha256(value.encode('utf-8')).hexdigest() for name, value in sorted(files.items())}}
    if backend == 'sphere':
        manifest['sphere_existing_graphics'] = sorted(set(integer(g, 1, 65534) for g in existing_graphics))
        manifest['sphere_new_graphics'] = sorted({r['graphic'] for r in data.get('items', [])} - set(existing_graphics))
    files['adapter-manifest.json'] = json.dumps(manifest, indent=2)+'\n'
    return files
