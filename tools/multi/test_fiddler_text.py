"""Regression checks for explicit Fiddler TXT semantics, without client data."""
import dataclasses
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import formats
import gen_cli


class FiddlerTextTests(unittest.TestCase):
    def test_uoa_decimal_ids_flags_and_canonical_header(self):
        text='77 version\r\n98 template id\r\n33 item version\r\n2 num components\r\n1650 12 -8 7 -2\r\n65535 -32768 32767 0 2147483647\r\n'
        tiles=formats.read('uoa-fiddler',text.encode())
        self.assertEqual(tiles[0],formats.Tile(1650,12,-8,7,True,text_flags=-2))
        output=formats.write('uoa-fiddler',tiles).decode()
        self.assertEqual(output,text.replace('77 version','6 version').replace('98 template id','1 template id').replace('33 item version','-1 item version'))

    def test_uoa_refuses_count_mismatch_hex_ids_and_invalid_bounds(self):
        prefix='6 version\n1 template id\n-1 item version\n1 num components\n'
        for value in (prefix, prefix+'0x1650 0 0 0 1', prefix+'65536 0 0 0 1', prefix+'1 32768 0 0 1',
                      prefix+'1 0 0 0 -2147483649',prefix+'1 0 0 0 1\n1 0 0 0 1',prefix+'1 0 0 0 1 extra'):
            with self.subTest(value=value),self.assertRaises(formats.FormatError): formats.read_fiddler_uoa(value)

    def test_uoa_cli_default_coordinates_and_raw_flags(self):
        ctx=gen_cli.Context(user=False)
        imported=gen_cli.run_op(ctx,'import',{'format':'uoa-fiddler','data':'6 version\n1 template id\n-1 item version\n1 num components\n1650 12 -8 7 -2'})
        self.assertEqual(imported['components'],[[1650,12,-8,7]])
        self.assertEqual(imported['text_flags'],[-2])
        exported=gen_cli.run_op(ctx,'export',dict(imported,format='uoa-fiddler'))
        self.assertTrue(exported['data'].endswith('1650 12 -8 7 -2\r\n'))

    def test_uoa_cli_protects_install_and_writes_canonical_bytes(self):
        with tempfile.TemporaryDirectory(prefix='guo-multi-uoa-') as folder:
            root=Path(folder); install=root/'install'; install.mkdir()
            source=install/'keep.uoa'; source.write_bytes(b'unchanged source')
            cfg=SimpleNamespace(client_data=install, client_data_env=str(install), client_data_setting='')
            payload={'format':'uoa-fiddler','components':[[1650,12,-8,7]],'text_flags':[-2]}
            with patch('guo.load_config',return_value=cfg):
                refused=gen_cli.run_op(gen_cli.Context(user=False),'export',dict(payload,path=str(source)))
                self.assertIn('error',refused); self.assertEqual(source.read_bytes(),b'unchanged source')
                target=root/'output'/'multi.uoa'
                accepted=gen_cli.run_op(gen_cli.Context(user=False),'export',dict(payload,path=str(target)))
                self.assertNotIn('error',accepted)
                self.assertEqual(target.read_bytes(),b'6 version\r\n1 template id\r\n-1 item version\r\n1 num components\r\n1650 12 -8 7 -2\r\n')
                self.assertFalse(list(target.parent.glob('*.tmp-*')))

    def test_bare_ids_are_hex_only_in_explicit_profile(self):
        self.assertEqual(formats.read('txt-fiddler', b'1650 12 -8 7 1')[0].item, 0x1650)
        self.assertEqual(formats.read('txt', b'1650 12 -8 7 1')[0].item, 1650)
        self.assertEqual(formats.detect('house.txt', b'1650 12 -8 7 1'), 'txt')
        self.assertEqual(formats.read_fiddler_txt('00001650 +012 -008 007 -0002')[0], formats.Tile(0x1650,12,-8,7,True,text_flags=-2))

    def test_signed_flags_order_and_duplicate_components_survive(self):
        source = '0xFFFF -32768 32767 -32768 -2147483648\r\n0x0 0 0 0 2147483647\r\n0x0 0 0 0 2147483647\r\n'
        tiles = formats.read_fiddler_txt(source)
        self.assertEqual(formats.write_fiddler_txt(tiles), source)
        self.assertEqual([tile.text_flags for tile in tiles], [-2147483648, 2147483647, 2147483647])

    def test_visibility_edits_override_flags_without_erasing_original(self):
        tile = formats.read_fiddler_txt('0xAB 0 0 0 256')[0]
        hidden = dataclasses.replace(tile, visible=False)
        self.assertEqual(formats.write_fiddler_txt([hidden]), '0xAB 0 0 0 0\r\n')
        self.assertEqual(hidden.text_flags, 256)
        self.assertEqual(formats.write_fiddler_txt([dataclasses.replace(hidden, visible=True)]), '0xAB 0 0 0 256\r\n')

    def test_invalid_or_overflowing_records_are_refused(self):
        for text in ('', '# comment', '0x10000 0 0 0 1', '0x1 32768 0 0 1', '0x1 0 -32769 0 1',
                     '0x1 0 0 32768 1', '0x1 0 0 0 2147483648', '0x1 0 0 0 -2147483649',
                     '0x1 0 0 0 0x1', '0x1 0 0 0', '0x1 0 0 0 1 extra'):
            with self.subTest(text=text), self.assertRaises(formats.FormatError):
                formats.read_fiddler_txt(text)
        with self.assertRaises(formats.FormatError):
            formats.read_fiddler_txt('0 0 0 0 1\n' * 65536)
        with self.assertRaises(formats.FormatError):
            formats.write_fiddler_txt([formats.Tile(1, 0, 0, 32768)])

    def test_cli_preserves_coordinates_and_flags_by_default(self):
        ctx = gen_cli.Context(user=False)
        imported = gen_cli.run_op(ctx, 'import', {'format': 'txt-fiddler', 'data': '1650 120 -80 7 -2'})
        self.assertEqual(imported['components'], [[0x1650, 120, -80, 7]])
        self.assertEqual(imported['text_flags'], [-2])
        exported = gen_cli.run_op(ctx, 'export', {'format': 'txt-fiddler', **imported})
        self.assertEqual(exported['data'], '0x1650 120 -80 7 -2\r\n')

    def test_recentering_carries_flags_and_missing_file_returns_error(self):
        ctx = gen_cli.Context(user=False)
        result = gen_cli.run_op(ctx, 'import', {'format': 'txt-fiddler', 'data': 'A 120 -80 7 256', 'recentre': True})
        self.assertEqual(result['text_flags'], [256])
        self.assertEqual(result['components'][0][1:3], [0, 0])
        self.assertIn('error', gen_cli.run_op(ctx, 'import', {'format': 'txt-fiddler', 'path': str(Path(__file__)/'missing')}))

    def test_cli_refuses_truncation_and_misaligned_flags(self):
        ctx = gen_cli.Context(user=False)
        for fields in ({'components': [[1.5, 0, 0, 0]]}, {'components': [[1, 0, 0, 0]], 'text_flags': []},
                       {'components': [[1, 0, 0, 0, 256]]}, {'components': [[1, 0, 0, 0]], 'text_flags': [2147483648]}):
            with self.subTest(fields=fields):
                self.assertIn('error', gen_cli.run_op(ctx, 'export', {'format': 'txt-fiddler', **fields}))

    def test_cli_export_protects_install_and_writes_exact_bytes_elsewhere(self):
        with tempfile.TemporaryDirectory(prefix='guo-multi-txt-') as folder:
            root=Path(folder); install=root/'install'; install.mkdir()
            source=install/'keep.txt'; source.write_bytes(b'unchanged source')
            cfg=SimpleNamespace(client_data=install, client_data_env=str(install), client_data_setting='')
            payload={'format':'txt-fiddler','components':[[0x1650,12,-8,7]],'text_flags':[-2]}
            with patch('guo.load_config',return_value=cfg):
                refused=gen_cli.run_op(gen_cli.Context(user=False),'export',dict(payload,path=str(source)))
                self.assertIn('error',refused); self.assertEqual(source.read_bytes(),b'unchanged source')
                target=root/'install-copy'/'multi.txt'
                accepted=gen_cli.run_op(gen_cli.Context(user=False),'export',dict(payload,path=str(target)))
                self.assertNotIn('error',accepted)
                self.assertEqual(target.read_bytes(),b'0x1650 12 -8 7 -2\r\n')
                self.assertFalse(list(target.parent.glob('*.tmp-*')))

    def test_tagged_transform_requires_actual_one_to_one_order(self):
        import orient
        from multifile import Component
        from test_gen import FAKE
        table=orient.table_from_styles({'a':FAKE['a']})
        gen_cli.assert_entry_order([Component(0x301,0,0,7),Component(0x301,2,3,7)],'rot90',table)
        with self.assertRaises(formats.FormatError):
            gen_cli.assert_entry_order([Component(0x104,0,0,7)],'rot90',table)


if __name__ == '__main__': unittest.main()
