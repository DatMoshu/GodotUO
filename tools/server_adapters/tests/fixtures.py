# SPDX-License-Identifier: BSD-2-Clause
import copy

BASE = {'schema': 'guo/server-content@1', 'identity_hash': 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'items': [{'identity': 'test:stone', 'graphic': 6001, 'name': 'Test stone', 'weight': 1.5, 'movable': True}], 'loot': [{'identity': 'test:loot', 'content': {'entries': [{'item': 'test:stone', 'chance': 1.0, 'min': 1, 'max': 2}]}}], 'creatures': [{'identity': 'test:creature', 'content': {'name': 'Test beast', 'body': 25, 'hue': 0, 'sound': -1, 'ai': 'melee', 'strength': 100, 'dexterity': 50, 'intelligence': 50, 'hits': 120, 'damage_min': 2, 'damage_max': 4, 'armor': 10, 'fame': 100, 'karma': -100, 'tactics': 50.0, 'wrestling': 50.0, 'resist': 30.0, 'loot': 'test:loot'}}], 'regions': [{'identity': 'test:region', 'music_id': -1, 'content': {'name': 'GUO test region', 'facet': 0, 'priority': 50, 'areas': [{'x': 100, 'y': 100, 'z': -128, 'width': 8, 'height': 8, 'depth': 256}]}}], 'decorations': [{'identity': 'test:decor', 'facet': 0, 'items': [{'id': 'one', 'graphic': 6001, 'x': 100, 'y': 100, 'z': 0, 'hue': 0}]}], 'maps': [{'identity': 'test:map', 'facet': 0, 'content': {'blocks': [{'x': 10, 'y': 10, 'statics': [{'graphic': 6001, 'x': 1, 'y': 1, 'z': 0, 'hue': 0}]}]}}], 'tiles': [{'identity': 'test:tile', 'id': 6001, 'content': {'name': 'Test tile', 'flags': 64, 'height': 5, 'weight': 1, 'layer': 0}}]}

def full_export():
    value = copy.deepcopy(BASE)
    value['maps'][0]['content']['blocks'][0]['land'] = [{'graphic': 3, 'z': 0} for _ in range(64)]
    return value

def item_export():
    return {k: v for k, v in full_export().items() if k in ('schema', 'identity_hash', 'items')}
