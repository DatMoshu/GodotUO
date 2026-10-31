"""Synthetic Fiddler CSV scalar/preservation regressions."""
from pathlib import Path
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import formats
import gen_cli


class FiddlerCsvTests(unittest.TestCase):
    def test_bare_hex_ids_flags_and_signed_extra(self):
        rows=formats.read_fiddler_csv('TileID,OffsetX,OffsetY,OffsetZ,Flag,Unk1\n1650,12,-8,7,100,0xffffffff\n0xffff,13,-7,-2,80000000,-2147483648\n')
        self.assertEqual([(t.item,t.text_flags,t.text_extra) for t in rows],[(0x1650,256,-1),(65535,-2147483648,-2147483648)])
        self.assertEqual(formats.write_fiddler_csv(rows),'TileID,OffsetX,OffsetY,OffsetZ,Flag,Unk1\r\n0x1650,12,-8,7,0x100,0xffffffff\r\n0xffff,13,-7,-2,0x80000000,0x80000000\r\n')

    def test_old_empty_or_absent_extra_is_zero(self):
        for row in ('1,0,0,0,1','1,0,0,0,1,'):
            self.assertEqual(formats.read_fiddler_csv(row)[0].text_extra,0)
        self.assertEqual(formats.read_fiddler_csv('1,0,0,0,1,256')[0].text_extra,256)

    def test_cli_default_coordinates_flags_extra_and_centering(self):
        ctx=gen_cli.Context(user=False)
        payload={'format':'csv-fiddler','data':'1650,12,-8,7,ffffffff,0x100'}
        imported=gen_cli.run_op(ctx,'import',payload)
        self.assertEqual(imported['components'],[[0x1650,12,-8,7]])
        self.assertEqual(imported['text_flags'],[-1]); self.assertEqual(imported['text_extra'],[256])
        centered=gen_cli.run_op(ctx,'import',dict(payload,recentre=True))
        self.assertEqual(centered['components'],[[0x1650,0,0,7]]); self.assertEqual(centered['text_extra'],[256])
        exported=gen_cli.run_op(ctx,'export',dict(imported,format='csv-fiddler'))
        self.assertTrue(exported['data'].endswith('0x1650,12,-8,7,0xffffffff,0x100\r\n'))

    def test_invalid_records_and_extra_alignment_are_refused(self):
        for row in ('','1,0,0,0','1,0,0,0,1,bad','1,0,0,0,100000000,0','10000,0,0,0,1,0',
                    '1,32768,0,0,1,0','1,0,0,0,1,2147483648','1,0,0,0,1,0,ignored',
                    '1,0,0,0,0x0X100,0','1,0,0,0,1,0x0X100'):
            with self.subTest(row=row),self.assertRaises(formats.FormatError): formats.read_fiddler_csv(row)
        ctx=gen_cli.Context(user=False)
        for extra in ([],[2**31],[1.5],['1']):
            self.assertIn('error',gen_cli.run_op(ctx,'export',{'format':'csv-fiddler','components':[[1,0,0,0]],'text_extra':extra}))

    def test_other_formats_refuse_nonzero_extra(self):
        tile=formats.Tile(1,0,0,0,text_flags=1,text_extra=256)
        for writer in (formats.write_fiddler_txt,formats.write_fiddler_uoa,formats.write_fiddler_wsc):
            with self.subTest(writer=writer.__name__),self.assertRaises(formats.FormatError): writer([tile])
        for fmt in formats.FORMATS:
            if fmt=='csv-fiddler': continue
            with self.subTest(fmt=fmt),self.assertRaises(formats.FormatError): formats.write(fmt,[tile])

    def test_budget_and_preserved_order_duplicates(self):
        tile=formats.Tile(65535,-32768,32767,-32768,text_flags=-2147483648,text_extra=2147483647)
        self.assertEqual(formats.read('csv-fiddler',formats.write('csv-fiddler',[tile,tile])),[tile,tile])
        with self.assertRaises(formats.FormatError): formats.read_fiddler_csv(' '*(4*1024*1024+1))
        with self.assertRaises(formats.FormatError): formats.write_fiddler_csv([tile]*65536)

    def test_install_guard_and_atomic_external_bytes(self):
        with tempfile.TemporaryDirectory(prefix='guo-csv-') as folder:
            root=Path(folder); install=root/'install'; install.mkdir(); source=install/'keep.csv'; source.write_bytes(b'keep')
            cfg=SimpleNamespace(client_data=install,client_data_env=str(install),client_data_setting='')
            payload={'format':'csv-fiddler','components':[[1,0,0,0]],'text_flags':[-1],'text_extra':[256]}
            with patch('guo.load_config',return_value=cfg):
                self.assertIn('error',gen_cli.run_op(gen_cli.Context(user=False),'export',dict(payload,path=str(source))))
                self.assertEqual(source.read_bytes(),b'keep')
                target=root/'out.csv'; self.assertNotIn('error',gen_cli.run_op(gen_cli.Context(user=False),'export',dict(payload,path=str(target))))
                self.assertTrue(target.read_bytes().endswith(b'0x0001,0,0,0,0xffffffff,0x100\r\n'))


if __name__=='__main__': unittest.main()
