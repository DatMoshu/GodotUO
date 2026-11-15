"""Original metadata fixtures for GUO ADR-0052 source-object behavior."""
from collections import Counter
import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from layout_import import pz_objects as O


def metadata(name, group='Neutral', family='original', **props):
    return {'tileset': family, 'properties': {'CustomName': name, 'GroupName': group,
            'IsMoveAble': '', **props}}


class SourceObjectsTest(unittest.TestCase):
    def test_structural_properties_win_over_fixture_names(self):
        for key in ('WallN', 'collideW', 'DoorWallN', 'doorW', 'windowN', 'solidfloor', 'stairsTN'):
            with self.subTest(key=key):
                result = O.classify(metadata('Bath', **{key: ''}), Counter())
                self.assertEqual(result['kind'], 'structural')
                self.assertIsNone(result['role'])

    def test_detail_names_and_tileset_substrings_cannot_become_fixtures(self):
        for name in ('Bathroom Rug', 'Bath Mirror', 'Bath Towel', 'Bath Mat', 'Bath Poster'):
            with self.subTest(name=name):
                self.assertEqual(O.classify(metadata(name), Counter())['kind'], 'detail')
        self.assertEqual(O.classify(metadata('Wooden Frame', family='beds_bathroom'), Counter())['role'], 'unresolved')
        self.assertEqual(O.classify(metadata('Bedside Ornament'), Counter())['role'], 'unresolved')

    def test_real_fixtures_and_custom_name_precedence(self):
        for name in ('Sink', 'Toilet', 'Bath', 'Bathtub', 'Shower'):
            self.assertEqual(O.classify(metadata(name), Counter())['role'], 'bath')
        self.assertEqual(O.classify(metadata('Counter', group='Bath'), Counter())['role'], 'counter')
        self.assertEqual(O.classify(metadata('Sink Counter'), Counter())['role'], 'unresolved')

    def test_object_evidence_is_required_and_shared_groups_are_explicit(self):
        value = {'tileset': 'original', 'properties': {'CustomName': 'Sink', 'GroupName': 'Original'}}
        self.assertEqual(O.classify(value, Counter())['kind'], 'unresolved-detail')
        result = O.classify(value, Counter({('original', 'Original'): 2}))
        self.assertEqual(result['role'], 'bath')
        self.assertIn('shared-tileset-group', result['evidence'])

    def test_property_backed_aliases_use_existing_roles_and_keep_unknown_objects(self):
        cases = {'Mattress': 'bed', 'Microwave': 'oven', 'Medical Stool': 'chair',
                 'Freezer': 'container', 'Box': 'container', 'Display': 'display',
                 'Radio': 'tools', 'Computer': 'tools', 'Terminal': 'tools'}
        for name, role in cases.items():
            with self.subTest(name=name):
                result = O.classify(metadata(name), Counter())
                self.assertEqual((result['kind'], result['role']), ('object', role))
                self.assertIn('CustomName:' + role, result['evidence'])
                without_evidence = {'tileset': 'original', 'properties': {'CustomName': name}}
                self.assertEqual(O.classify(without_evidence, Counter())['kind'], 'unresolved-detail')
                structural = metadata(name, WallN='')
                self.assertEqual(O.classify(structural, Counter())['kind'], 'structural')
        for name, group in [('Switch', 'Light'), ('Clock', 'Wall'), ('Television', 'Neutral'),
                            ('Radiogram', 'Neutral'), ('Boxwood', 'Neutral'), ('Computerized', 'Neutral'),
                            ('Radio Cabinet', 'Neutral'), ('Microwave Counter', 'Neutral')]:
            with self.subTest(unresolved=name):
                result = O.classify(metadata(name, group=group), Counter())
                self.assertEqual((result['kind'], result['role']), ('object', 'unresolved'))
        self.assertEqual(O.classify(metadata('Radio Poster'), Counter())['kind'], 'detail')

    def bed(self, offsets=((0, 0), (1, 0))):
        definitions = {f'original_{i}': metadata('Bed', group='Blue', SpriteGridPos=f'{x},{y}', Facing='E')
                       for i, (x, y) in enumerate(offsets)}
        counts = O.definition_groups(definitions)
        observations = [O.observation(key, [10 + x, 20 + y, 0], 0, definitions[key], counts)
                        for key, (x, y) in zip(definitions, offsets)]
        return definitions, observations

    def test_adjacent_identical_beds_keep_separate_source_anchors(self):
        definitions, observations = self.bed()
        second = copy.deepcopy(observations)
        for o in second:
            o['at'][0] += 2
        groups = O.assemble(observations + second, O.expected_grids(definitions))
        self.assertEqual(len(groups), 2)
        self.assertEqual([len(g['tiles']) for g in groups], [2, 2])
        self.assertEqual({g['status'] for g in groups}, {'resolved'})
        self.assertEqual(groups, O.assemble(list(reversed(observations + second)), O.expected_grids(definitions)))

    def test_trimmed_multipart_bed_remains_one_disconnected_object(self):
        definitions, observations = self.bed()
        groups = O.assemble(observations, O.expected_grids(definitions))
        cells = {(x, y): {'role': 'wall' if x in (1, 2) else 'floor', 'indoors': True}
                 for x in range(4) for y in range(2)}
        projected = O.project(groups, cells, [10, 20])
        self.assertEqual(len(projected), 1)
        self.assertEqual(projected[0]['cells'], [[0, 0], [0, 1], [3, 0], [3, 1]])
        self.assertEqual(len(projected[0]['intended_cells']), 8)
        self.assertEqual(projected[0]['status'], 'resolved')

    def test_missing_or_duplicate_grid_parts_stay_unresolved(self):
        definitions, observations = self.bed()
        for values in (observations[:1], observations + [copy.deepcopy(observations[0])]):
            groups = O.assemble(values, O.expected_grids(definitions))
            self.assertEqual(groups[0]['status'], 'unresolved')
            self.assertTrue(groups[0]['diagnostics'])

    def test_counter_group_survives_mixed_names_and_structural_trim(self):
        a, b = metadata('Counter'), metadata('Corner Counter')
        observations = [O.observation('original_0', [5, 5, 0], 0, a, Counter()),
                        O.observation('original_1', [6, 5, 0], 0, b, Counter())]
        groups = O.assemble(observations, {})
        self.assertEqual(len(groups), 1)
        self.assertEqual(groups[0]['role'], 'counter')
        cells = {(x, y): {'role': 'wall' if x == 2 else 'floor', 'indoors': True}
                 for x in range(4) for y in range(2)}
        self.assertEqual(len(O.project(groups, cells, [5, 5])), 1)

    def test_sink_and_counter_on_one_source_cell_are_two_objects(self):
        definitions = {'counter_0': metadata('Counter', family='counter'),
                       'sink_0': metadata('Sink', family='sink')}
        observations = [O.observation(key, [5, 5, 0], i, meta, O.definition_groups(definitions))
                        for i, (key, meta) in enumerate(definitions.items())]
        groups = O.assemble(observations, {})
        cells = {(x, y): {'role': 'floor', 'indoors': True} for x in range(2) for y in range(2)}
        projected = O.project(groups, cells, [5, 5])
        self.assertEqual(len(projected), 2)
        self.assertEqual({g['role'] for g in projected}, {'counter', 'bath'})
        self.assertEqual(projected[0]['cells'], projected[1]['cells'])

    def test_no_surviving_support_is_not_silently_omitted(self):
        definitions, observations = self.bed()
        cells = {(x, y): {'role': 'wall', 'indoors': True} for x in range(4) for y in range(2)}
        projected = O.project(O.assemble(observations, O.expected_grids(definitions)), cells, [10, 20])
        self.assertEqual(projected[0]['cells'], [])
        self.assertEqual(projected[0]['status'], 'unresolved')

    def test_movable_floor_litter_is_detail_but_storage_bin_is_container(self):
        self.assertEqual(O.classify(metadata('Trash', MoveType='FloorRug'), {})['kind'], 'detail')
        self.assertEqual(O.classify(metadata('Bin', container='bin'), {})['role'], 'container')

    def test_incompatible_grid_facings_at_one_anchor_cannot_pass_separately(self):
        definitions, observations = self.bed()
        observations[1]['facing'] = 'W'
        groups = O.assemble(observations, O.expected_grids(definitions))
        self.assertEqual(len(groups), 1)
        self.assertEqual(groups[0]['status'], 'unresolved')
        self.assertIn('incompatible source object facings', groups[0]['diagnostics'])




class SourceObjectIntegrationTest(unittest.TestCase):
    def level(self):
        cells = [{'x': x, 'y': y, 'role': 'floor', 'indoors': True, 'furnishing_role': None,
                  'furniture': 'f_null', 'access': 'open', 'walkable': True}
                 for y in range(6) for x in range(6)]
        defs = {'a': metadata('Sink', family='sink'), 'b': metadata('Counter', family='counter')}
        obs = [O.observation(k, [1, 1, 0], i, m, O.definition_groups(defs))
               for i, (k, m) in enumerate(defs.items())]
        groups = O.project(O.assemble(obs, {}), {(c['x'], c['y']): c for c in cells}, [0, 0])
        return {'z': 0, 'cells': cells, 'rooms': [{'id': 1, 'cells': [[c['x'], c['y']] for c in cells]}],
                'furnishing_groups': groups}

    def test_native_fits_and_reports_both_stacked_objects(self):
        from layout_import import native
        from types import SimpleNamespace
        level = self.level()
        templates = [SimpleNamespace(id=i, kinds={role: 1}, on_wall=False, against='',
                     cells={(0, 0)}, uses=1, items=[[100 + i, 0, 0, 0]])
                     for i, role in enumerate(('bath', 'counter'))]
        storey = {'z': 7, 'walls': [], 'floor': level['rooms'][0]['cells'], 'doors': []}
        items, mapped, problems = native.furnish(level, storey, SimpleNamespace(templates=templates), native.DEFAULT_THEME, set())
        self.assertEqual(problems, [])
        self.assertEqual(len(items), 2)
        self.assertEqual(len(mapped), 2)
        self.assertEqual({m['status'] for m in mapped}, {'mapped'})
        self.assertEqual(len({m['id'] for m in mapped}), 2)
        self.assertNotEqual(mapped[0]['target_cells'], mapped[1]['target_cells'])

    def test_native_unknown_and_empty_support_are_individually_blocked(self):
        from layout_import import native
        from types import SimpleNamespace
        level = self.level()
        level['furnishing_groups'][0].update(status='unresolved', diagnostics=['uncertain fixture'])
        level['furnishing_groups'][1]['cells'] = []
        storey = {'z': 7, 'walls': [], 'floor': level['rooms'][0]['cells'], 'doors': []}
        items, mapped, problems = native.furnish(level, storey, SimpleNamespace(templates=[]), native.DEFAULT_THEME, set())
        self.assertEqual(items, [])
        self.assertEqual(len(mapped), 2)
        self.assertEqual(len(problems), 2)
        self.assertEqual({m['status'] for m in mapped}, {'blocked'})

    def test_native_inventory_rejects_duplicates_and_invalid_masks(self):
        from layout_import import native, semantic
        for mode in ('duplicate-id', 'missing-cell', 'duplicate-cell', 'outside-intended'):
            level = self.level()
            a, b = level['furnishing_groups']
            if mode == 'duplicate-id': b['id'] = a['id']
            if mode == 'missing-cell': a['cells'] = [[100, 100]]
            if mode == 'duplicate-cell': a['cells'].append(a['cells'][0])
            if mode == 'outside-intended': a['cells'] = [[0, 0]]
            with self.subTest(mode=mode), self.assertRaises(semantic.ResolutionError):
                native.groups(level)

    def test_invalid_inventory_and_nonfloor_support_cannot_reach_material_access(self):
        from layout_import import native, semantic
        for mode in ('not-list', 'not-object', 'no-source-id', 'invalid-diagnostics', 'false-resolved', 'wall-support'):
            level = self.level()
            groups = level['furnishing_groups']
            if mode == 'not-list': level['furnishing_groups'] = {}
            if mode == 'not-object': groups[0] = None
            if mode == 'no-source-id': groups[0].pop('source_id')
            if mode == 'invalid-diagnostics': groups[0]['diagnostics'] = 'bad'
            if mode == 'false-resolved': groups[0]['diagnostics'] = ['not resolved']
            if mode == 'wall-support':
                pos = groups[0]['cells'][0]
                next(c for c in level['cells'] if [c['x'], c['y']] == pos)['role'] = 'wall'
            with self.subTest(mode=mode), self.assertRaises(semantic.ResolutionError):
                native.compile_layout({'levels': [level], 'furnishing_contract': O.CONTRACT}, None, None)

    def test_native_rejects_unknown_or_missing_contract_before_material_access(self):
        from layout_import import native, semantic
        for contract in (None, 'unknown/2'):
            layout = {'levels': [self.level()]}
            if contract: layout['furnishing_contract'] = contract
            with self.assertRaises(semantic.ResolutionError):
                native.compile_layout(layout, None, None)

    def test_extension_hash_is_sensitive_and_legacy_hash_is_unchanged(self):
        from layout_import import semantic, cdda
        level = self.level()
        layout = {'format': 1, 'width': 6, 'height': 6, 'levels': [level]}
        expected = cdda.digest({'format': 1, 'width': 6, 'height': 6,
            'levels': [{'z': 0, 'cells': [[c['x'], c['y'], c['role'], c['indoors'],
                        c['furnishing_role'], c['access']] for c in level['cells']]}]})
        self.assertEqual(semantic.layout_hash(layout), expected)
        layout['furnishing_contract'] = O.CONTRACT
        before = semantic.layout_hash(layout)
        self.assertNotEqual(before, expected)
        level['furnishing_groups'][0]['tiles'][0]['stack'] += 1
        self.assertNotEqual(before, semantic.layout_hash(layout))

    def test_rotation_preserves_source_observations_and_rotates_both_masks(self):
        from layout_import import semantic
        layout = {'format': 1, 'width': 6, 'height': 6, 'levels': [self.level()], 'furnishing_contract': O.CONTRACT}
        before = copy.deepcopy(layout['levels'][0]['furnishing_groups'])
        rotated = semantic.rotate(layout, 1)['levels'][0]['furnishing_groups']
        for a, b in zip(before, rotated):
            self.assertEqual(a['tiles'], b['tiles'])
            for field in ('cells', 'intended_cells'):
                self.assertEqual(b[field], sorted([[5-y, x] for x, y in a[field]]))
        self.assertEqual(semantic.layout_hash(layout), semantic.layout_hash(semantic.rotate(layout, 4)))

    def test_flat_and_nested_definition_load_order_and_no_mixing(self):
        from layout_import import zomboid
        import struct
        import tempfile
        def tdef(props):
            ints = lambda *n: struct.pack('<' + 'i' * len(n), *n)
            return b'tdef' + ints(1, 1) + b'original\noriginal.png\n' + ints(1, 1, 1, 1, len(props)) + b''.join(
                k.encode() + b'\n' + v.encode() + b'\n' for k, v in props.items())
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            media = root/'media'; media.mkdir()
            files = {'newtiledefinitions.tiles': {'CustomName': 'Bed', 'old': ''},
                     'tiledefinitions_erosion.tiles': {'CustomName': 'Counter'},
                     'tiledefinitions_noiseworks.patch.tiles': {'Facing': 'N'}}
            for name, props in files.items(): (media/name).write_bytes(tdef(props))
            flat, sources = zomboid.properties(root)
            self.assertEqual(flat['original_0']['properties'], {'CustomName': 'Counter', 'Facing': 'N'})
            nested = media/'tile_definitions'; nested.mkdir()
            with self.assertRaises(ValueError): zomboid.properties(root)
            for name, props in files.items(): (nested/name).write_bytes(tdef(props))
            (media/'jumbo_trees.tiles').write_bytes(tdef({'CustomName': 'Wrong Flat Override'}))
            self.assertEqual(zomboid.properties(root), (flat, sources))


if __name__ == '__main__':
    unittest.main()
