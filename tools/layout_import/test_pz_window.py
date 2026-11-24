"""Original binary fixtures for bounded PZ cell windows; no installed game data."""
import copy
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from layout_import import zomboid as Z


def ints(*values):
    return struct.pack('<' + 'i' * len(values), *values)


def write_cell(folder, cell, tiles, observations, rect=(20, 20, 2, 2), room='kitchen'):
    cx, cy = cell
    header = folder / f'{cx}_{cy}.lotheader'
    data = (b'LOTH' + ints(1, len(tiles)) + b''.join(t.encode() + b'\n' for t in tiles)
            + ints(8, 8, 0, 0, 1) + room.encode() + b'\n'
            + ints(0, 1, *rect, 0, 1, 1, 0))
    header.write_bytes(data)
    payload = bytearray(ints(-1, 64))
    offsets = [8204] * 1024
    chunks = sorted({(x // 8, y // 8) for x, y, z in observations})
    for qx, qy in chunks:
        offsets[qx * 32 + qy] = 8204 + len(payload)
        for x in range(qx * 8, qx * 8 + 8):
            for y in range(qy * 8, qy * 8 + 8):
                stack = observations.get((x, y, 0), [])
                payload.extend(ints(len(stack) + 1, 0, *[tiles.index(t) for t in stack]) if stack else ints(0))
    pack = folder / f'world_{cx}_{cy}.lotpack'
    pack.write_bytes(b'LOTP' + ints(1, 0) + b''.join(ints(o, 0) for o in offsets) + payload)
    return header, pack


class WindowTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.folder = self.root / 'media/maps/Original'
        self.folder.mkdir(parents=True)

    def tearDown(self):
        self.tmp.cleanup()

    def pair(self, delta, bounds):
        primary = [2, 3]
        neighbor = [2 + delta[0], 3 + delta[1]]
        write_cell(self.folder, primary, ['floor_a'], {(255 if delta[0] > 0 else 0, 255 if delta[1] > 0 else 0, 0): ['floor_a']})
        write_cell(self.folder, neighbor, ['floor_b'], {(255 if delta[0] < 0 else 0, 255 if delta[1] < 0 else 0, 0): ['floor_b']})
        return Z.header(self.folder / '2_3.lotheader')

    def test_seams_use_neighbor_dictionary_in_continuous_primary_frame(self):
        cases = [((1, 0), [254, 0, 258, 2], (255, 0, 0), (256, 0, 0)),
                 ((0, 1), [0, 254, 2, 258], (0, 255, 0), (0, 256, 0)),
                 ((-1, 0), [-2, 0, 2, 2], (0, 0, 0), (-1, 0, 0)),
                 ((0, -1), [0, -2, 2, 2], (0, 0, 0), (0, -1, 0))]
        for delta, bounds, a, b in cases:
            with self.subTest(delta=delta):
                primary = self.pair(delta, bounds)
                decoded, sources = Z.source_window(self.folder, primary, bounds)
                self.assertEqual(decoded[a]['tiles'], ['floor_a'])
                self.assertEqual(decoded[b]['tiles'], ['floor_b'])
                self.assertEqual(len(sources), 2)
                self.assertEqual([s['cell'] for s in sources], sorted(s['cell'] for s in sources))
                self.assertTrue(all(len(s['header_sha256']) == len(s['pack_sha256']) == 64 for s in sources))

    def test_four_cell_corner_is_complete(self):
        for dx in (0, 1):
            for dy in (0, 1):
                write_cell(self.folder, (2 + dx, 3 + dy), [f'tile_{dx}_{dy}'],
                           {(255 if dx == 0 else 0, 255 if dy == 0 else 0, 0): [f'tile_{dx}_{dy}']})
        decoded, sources = Z.source_window(self.folder, Z.header(self.folder / '2_3.lotheader'), [254, 254, 258, 258])
        self.assertEqual(len(sources), 4)
        self.assertEqual(set(decoded), {(255, 255, 0), (255, 256, 0), (256, 255, 0), (256, 256, 0)})

    def test_missing_malformed_and_wrong_identity_fail(self):
        bounds = [254, 0, 258, 2]
        primary = self.pair((1, 0), bounds)
        neighbor = self.folder / '3_3.lotheader'
        original = neighbor.read_bytes()
        neighbor.unlink()
        with self.assertRaisesRegex(ValueError, 'missing source window'):
            Z.source_window(self.folder, primary, bounds)
        neighbor.write_bytes(b'LOTH' + ints(99))
        with self.assertRaisesRegex(ValueError, 'unsupported lotheader'):
            Z.source_window(self.folder, primary, bounds)
        neighbor.write_bytes(original)
        pack = self.folder / 'world_3_3.lotpack'
        original_pack = pack.read_bytes()
        pack.unlink()
        with self.assertRaisesRegex(ValueError, 'missing source window'):
            Z.source_window(self.folder, primary, bounds)
        pack.write_bytes(b'LOTP' + ints(99))
        with self.assertRaisesRegex(ValueError, 'unsupported lotpack'):
            Z.source_window(self.folder, primary, bounds)
        pack.write_bytes(original_pack)
        wrong = copy.deepcopy(primary); wrong['cell'] = [99, 99]
        with patch.object(Z, 'header', return_value=wrong):
            with self.assertRaisesRegex(ValueError, 'identity'):
                Z.source_window(self.folder, primary, bounds)

    def test_excessive_bounds_and_duplicate_observations_fail(self):
        primary = self.pair((1, 0), [254, 0, 258, 2])
        for bounds in ([0, 0, 257, 2], [0, 0, 2, 257], [2, 0, 1, 2], [256, 0, 260, 2], [0, 0, True, 2]):
            with self.subTest(bounds=bounds), self.assertRaises(ValueError):
                Z.source_window(self.folder, primary, bounds)
        with patch.object(Z, 'chunk', return_value={(0, 0, 0): {'room': 0, 'tiles': ['floor_a']}}):
            with self.assertRaisesRegex(ValueError, 'duplicate translated'):
                Z.source_window(self.folder, primary, [246, 0, 258, 2])

    def authored(self, rect):
        definitions = {
            'floor': {'tileset': 'floor', 'properties': {'solidfloor': ''}},
            'door_n': {'tileset': 'door', 'properties': {'doorN': ''}},
            'door_w': {'tileset': 'door', 'properties': {'doorW': ''}},
            **{f'bed_{i}': {'tileset': 'bed', 'properties': {'IsMoveAble': '', 'CustomName': 'Bed',
                  'GroupName': 'Original', 'Facing': 'E', 'SpriteGridPos': f'{i},0'}} for i in (0, 1)}}
        x, y, w, h = rect
        observations = {(xx, yy, 0): ['floor'] for xx in range(x, x + w) for yy in range(y, y + h)}
        observations[(x + 1, y + 1, 0)].append('bed_0')
        observations[(x + 2, y + 1, 0)].append('bed_1')
        observations[(x + 2, y, 0)].append('door_n')
        observations[(x, y + 1, 0)].append('door_w')
        cells = {(xx // 256, yy // 256) for xx in (x - 2, x + w + 1) for yy in (y - 2, y + h + 1)}
        for dx, dy in cells:
            tiles = sorted(definitions, reverse=(dx != 0 or dy != 0))
            local = {(sx % 256, sy % 256, z): stack for (sx, sy, z), stack in observations.items()
                     if (sx // 256, sy // 256) == (dx, dy)}
            write_cell(self.folder, (2 + dx, 3 + dy), tiles, local,
                       rect if (dx, dy) == (0, 0) else (0, 0, 12, 12),
                       'kitchen' if (dx, dy) == (0, 0) else 'unrelated-library')
        return definitions

    def test_resolve_preserves_room_ownership_doors_and_multipart_object_across_seam(self):
        definitions = self.authored((254, 20, 4, 3))
        with patch.object(Z, 'properties', return_value=(definitions, [])):
            with self.assertRaisesRegex(ValueError, 'cross-cell building'):
                Z.resolve(self.root, 'Original', '2_3.lotheader', 0, 'original')
            layout = Z.resolve(self.root, 'Original', '2_3.lotheader', 0, 'original', True)
        provenance = layout['provenance']
        self.assertEqual(provenance['source_window_contract'], 'pz-cell-window/1')
        self.assertEqual(provenance['source_window_bounds'], [252, 18, 260, 25])
        level = layout['levels'][0]
        self.assertEqual([r['name'] for r in level['authored_rooms']], ['kitchen'])
        self.assertEqual(level['authored_rooms'][0]['source_rects'], [(254, 20, 4, 3)])
        cells = {(c['x'], c['y']): c for c in level['cells']}
        self.assertEqual(cells[9, 4]['role'], 'door')  # north edge
        self.assertEqual(cells[4, 7]['role'], 'door')  # west edge
        self.assertEqual(cells[8, 6]['source']['cell'], [3, 3])
        self.assertEqual(cells[8, 6]['source']['at'], [0, 21, 0])
        self.assertEqual(cells[8, 6]['source']['frame_at'], [256, 21, 0])
        self.assertEqual(cells[8, 6]['source']['room_header'], '2_3.lotheader')
        group, = level['furnishing_groups']
        self.assertEqual(group['status'], 'resolved')
        self.assertEqual(len(group['intended_cells']), 8)
        self.assertEqual([o['at'] for o in group['tiles']], [[255, 21, 0], [256, 21, 0]])
        self.assertEqual([o['source_cell'] for o in group['tiles']], [[2, 3], [3, 3]])
        self.assertEqual([o['source_at'] for o in group['tiles']], [[255, 21, 0], [0, 21, 0]])

    def test_single_cell_flag_is_exact_noop(self):
        definitions = self.authored((20, 20, 4, 3))
        with patch.object(Z, 'properties', return_value=(definitions, [])):
            old = Z.resolve(self.root, 'Original', '2_3.lotheader', 0, 'original')
            opted_in = Z.resolve(self.root, 'Original', '2_3.lotheader', 0, 'original', True)
        self.assertEqual(old, opted_in)
        self.assertNotIn('source_window_contract', old['provenance'])


if __name__ == '__main__':
    unittest.main()
