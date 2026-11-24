"""Property-backed PZ objects; portable implementation of GUO ADR-0052.

Pure source-metadata analysis. No source art, target materials or game-specific
themes. Assembly happens before structural projection can split an object.
"""
from __future__ import annotations

from collections import Counter, defaultdict
import hashlib
import json
import re

CONTRACT = 'pz-source-objects/1'
DETAIL = {'rug', 'carpet', 'mat', 'towel', 'mirror', 'poster', 'curtain'}
ROLES = {
    'bed': {'bed', 'beds', 'mattress'}, 'bath': {'sink', 'toilet', 'bath', 'bathtub', 'shower'},
    'oven': {'stove', 'oven', 'microwave'}, 'bookcase': {'book', 'books', 'bookcase', 'bookshelf', 'bookshelves'},
    'chest': {'crate', 'chest'}, 'counter': {'counter'}, 'chair': {'chair', 'sofa', 'couch', 'ottoman', 'stool'},
    'table': {'table', 'desk'},
    'container': {'fridge', 'refrigerator', 'freezer', 'cabinet', 'locker', 'shelf', 'shelves',
                  'drawers', 'wardrobe', 'bin', 'box'},
    'display': {'display'}, 'tools': {'radio', 'computer', 'terminal'},
    'light': {'lamp', 'lights'}, 'plant': {'plant'},
}


def tokens(value):
    return set(re.findall(r'[a-z0-9]+', value.casefold()))


def definition_groups(definitions):
    return Counter((m['tileset'], m['properties'].get('GroupName')) for m in definitions.values()
                   if m['properties'].get('GroupName'))


def classify(metadata, group_counts):
    props = metadata['properties']
    structure = sorted(k for k in props if k.casefold() == 'solidfloor'
                       or k.casefold().startswith(('wall', 'collide', 'door', 'window', 'stairs')))
    if structure:
        return {'kind': 'structural', 'role': None, 'evidence': ['property:' + k for k in structure]}
    if props.get('MoveType') == 'FloorRug':
        return {'kind': 'detail', 'role': None, 'evidence': ['property:MoveType=FloorRug']}
    names = {key: tokens(props.get(key, '')) for key in ('CustomName', 'GroupName')}
    detail = sorted((names['CustomName'] | names['GroupName']) & DETAIL)
    if detail:
        return {'kind': 'detail', 'role': None, 'evidence': ['detail-token:' + k for k in detail]}
    evidence = ['property:' + k for k in ('container', 'IsMoveAble') if k in props]
    if props.get('GroupName') and group_counts.get((metadata['tileset'], props['GroupName']), 0) > 1:
        evidence.append('shared-tileset-group')
    if not evidence:
        return {'kind': 'unresolved-detail', 'role': None, 'evidence': ['no-object-evidence']}
    for key, words in names.items():
        roles = sorted(role for role, nameset in ROLES.items() if words & nameset)
        if len(roles) == 1:
            return {'kind': 'object', 'role': roles[0], 'evidence': evidence + [key + ':' + roles[0]]}
        if roles:
            return {'kind': 'object', 'role': 'unresolved',
                    'evidence': evidence + [key + ':ambiguous:' + ','.join(roles)]}
    return {'kind': 'object', 'role': 'unresolved', 'evidence': evidence + ['no-role-token']}


def grid_position(value):
    if value is None:
        return None
    match = re.fullmatch(r'\s*(\d+)\s*,\s*(\d+)\s*', value)
    if not match or any(int(n) > 64 for n in match.groups()):
        raise ValueError('invalid source SpriteGridPos')
    return tuple(map(int, match.groups()))


def expected_grids(definitions):
    grids = defaultdict(set)
    for meta in definitions.values():
        props = meta['properties']
        try:
            offset = grid_position(props.get('SpriteGridPos'))
        except ValueError:
            continue  # a placed malformed record is diagnosed by observation()
        if offset is not None:
            grids[(meta['tileset'], props.get('GroupName', ''), props.get('Facing', ''))].add(offset)
    return grids


def observation(tile_id, at, stack_index, metadata, group_counts):
    props = metadata['properties']
    decision = classify(metadata, group_counts)
    diagnostics = []
    try:
        offset = grid_position(props.get('SpriteGridPos'))
    except ValueError as exc:
        offset = None
        diagnostics.append(str(exc))
    return {'tile': tile_id, 'at': list(at), 'stack': stack_index, 'tileset': metadata['tileset'],
            'group': props.get('GroupName', ''), 'custom_name': props.get('CustomName', ''),
            'facing': props.get('Facing', ''), 'sprite_grid': list(offset) if offset is not None else None,
            **decision, 'diagnostics': diagnostics}


def _connected(points):
    remaining = set(points)
    while remaining:
        start = min(remaining)
        remaining.remove(start)
        pending, component = [start], {start}
        while pending:
            x, y = pending.pop()
            for q in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if q in remaining:
                    remaining.remove(q)
                    component.add(q)
                    pending.append(q)
        yield component


def assemble(observations, grids):
    """Return source objects with exact observations; no target-mask heuristics.

    Grid offsets establish an object anchor. Other same-family groups use source
    adjacency, which survives later structural trimming. Ambiguity is retained.
    """
    anchored, adjacent = defaultdict(list), defaultdict(list)
    for obs in observations:
        if obs['kind'] != 'object':
            continue
        x, y, z = obs['at']
        key = (z, obs['tileset'], obs['group'])
        if obs['sprite_grid'] is not None:
            dx, dy = obs['sprite_grid']
            anchored[(*key, x - dx, y - dy)].append(obs)
        else:
            adjacent[key].append(obs)
    sets = list(anchored.values())
    for members in adjacent.values():
        by_position = defaultdict(list)
        for obs in members:
            by_position[tuple(obs['at'][:2])].append(obs)
        for component in _connected(by_position):
            sets.append([obs for p in sorted(component) for obs in by_position[p]])
    result = []
    for members in sets:
        members = sorted(members, key=lambda r: (r['at'], r['stack'], r['tile']))
        roles = {r['role'] for r in members}
        diagnostics = [d for r in members for d in r['diagnostics']]
        if len(roles) != 1 or 'unresolved' in roles:
            diagnostics.append('ambiguous or unresolved source object role')
        first = members[0]
        if len({r['facing'] for r in members}) > 1:
            diagnostics.append('incompatible source object facings')
        offsets = [tuple(r['sprite_grid']) for r in members if r['sprite_grid'] is not None]
        if offsets:
            expected = grids.get((first['tileset'], first['group'], first['facing']), set())
            if not expected or set(offsets) != expected:
                diagnostics.append('incomplete or variant-ambiguous source sprite grid')
            if len(offsets) != len(set(offsets)):
                diagnostics.append('duplicate source sprite-grid offset')
        elif len({tuple(r['at']) for r in members}) != len(members):
            diagnostics.append('ambiguous overlapping source objects in one group')
        identity = hashlib.sha256(json.dumps(members, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        result.append({'id': identity, 'source_id': 'pz:' + first['tileset'] + ':' + first['group'],
                       'role': next(iter(roles)) if len(roles) == 1 else 'unresolved',
                       'status': 'unresolved' if diagnostics else 'resolved', 'tiles': members,
                       'diagnostics': sorted(set(diagnostics))})
    return sorted(result, key=lambda group: (group['tiles'][0]['at'], group['id']))


def project(groups, cells, source_origin):
    """Attach intended and surviving target masks without splitting source objects."""
    output = []
    for group in groups:
        intended = {(2 * (o['at'][0] - source_origin[0]) + dx,
                     2 * (o['at'][1] - source_origin[1]) + dy)
                    for o in group['tiles'] for dx in (0, 1) for dy in (0, 1)}
        if not any(cells.get(p, {}).get('indoors') for p in intended):
            continue  # source object outside the selected authored building
        support = [p for p in sorted(intended) if cells.get(p, {}).get('indoors')
                   and cells[p]['role'] == 'floor']
        diagnostics = list(group['diagnostics'])
        if not support:
            diagnostics.append('source object has no surviving floor support')
        output.append({**group, 'intended_cells': [list(p) for p in sorted(intended)],
                       'cells': [list(p) for p in support], 'diagnostics': diagnostics,
                       'status': 'unresolved' if diagnostics else group['status']})
    return output
