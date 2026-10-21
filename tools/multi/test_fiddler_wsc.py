"""Synthetic regressions for the explicit reviewed WSC profile."""
from pathlib import Path
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parent))
import formats
import gen_cli


class FiddlerWscTests(unittest.TestCase):
    def source(self,rows):
        return formats.write_wsc([formats.Tile(*row) for row in rows])

    def test_positive_world_coordinates_match_fiddler_heuristic(self):
        text=self.source([(1650,120,80,7),(42,123,82,-2)])
        result=formats.read_fiddler_wsc(text)
        self.assertEqual([(t.x,t.y,t.z) for t in result],[(0,0,7),(3,2,-2)])
        self.assertEqual(formats.read_wsc(text)[0].x,120)
        seeded=formats.read_fiddler_wsc(self.source([(1650,15000,15000,7)]))
        self.assertEqual((seeded[0].x,seeded[0].y),(5000,5000))
        unchanged=formats.read_fiddler_wsc(self.source([(1650,20000,20000,7)]))
        self.assertEqual((unchanged[0].x,unchanged[0].y),(20000,20000))

    def test_relative_coordinates_order_duplicates_and_dynamic_ids(self):
        text=self.source([(1650,120,-8,7),(1650,120,-8,7),(42,123,-6,-2)])
        result=formats.read_fiddler_wsc(text,{1650})
        self.assertEqual([(t.item,t.x,t.visible,t.text_flags) for t in result],[(1650,120,False,0),(1650,120,False,0),(42,123,True,1)])
        imported=gen_cli.run_op(gen_cli.Context(user=False),'import',{'format':'wsc-fiddler','data':text,'dynamic_ids':[1650]})
        self.assertEqual(imported['text_flags'],[0,0,1])
        self.assertEqual(imported['components'][0],[1650,120,-8,7,0])

    def test_export_uses_tabs_crlf_color_zero_and_no_flags(self):
        value=formats.write_fiddler_wsc([formats.Tile(1650,-2,-3,7,False,21,text_flags=-2)])
        self.assertEqual(value,'SECTION WORLDITEM 0\r\n{\r\n\tID\t1650\r\n\tX\t-2\r\n\tY\t-3\r\n\tZ\t7\r\n\tColor\t0\r\n}\r\n')
        result=formats.read_fiddler_wsc(value)[0]
        self.assertEqual((result.visible,result.hue,result.text_flags),(True,0,1))

    def test_invalid_records_and_sentinel_are_refused(self):
        valid=self.source([(1650,-2,-3,7)])
        for value in ('',valid.replace('ID 1650','ID 65535'),valid.replace('X -2','X 32768'),
                      valid.replace('Z 7','Z 1.5'),valid.replace('Z 7','Z 7\nZ 8'),valid.replace('Z 7',''),
                      valid.replace('ID 1650','ID 0x672')):
            with self.subTest(value=value),self.assertRaises(formats.FormatError): formats.read_fiddler_wsc(value)
        with self.assertRaises(formats.FormatError): formats.write_fiddler_wsc([formats.Tile(65535,0,0,0)])

    def test_large_padded_numbers_are_bounded_without_python_int_limit(self):
        text=self.source([(1650,-2,-3,7)]).replace('ID 1650','ID '+'0'*5000+'1650')
        self.assertEqual(formats.read_fiddler_wsc(text)[0].item,1650)

    def test_byte_and_component_budgets_are_enforced(self):
        with self.assertRaises(formats.FormatError): formats.read_fiddler_wsc(' '*(4*1024*1024+1))
        with self.assertRaises(formats.FormatError):
            formats.read_fiddler_wsc('SECTION WORLDITEM\nID 1\nX 0\nY 0\nZ 0\n'*65536)
        with self.assertRaises(formats.FormatError):
            formats.write_fiddler_wsc([formats.Tile(65534,-32768,-32768,-32768)]*50000)
        for ids in ('1650',[1.5],[-1],[65536]):
            self.assertIn('error',gen_cli.run_op(gen_cli.Context(user=False),'import',
                {'format':'wsc-fiddler','data':self.source([(1650,0,0,0)]),'dynamic_ids':ids}))

    def test_cli_validation_and_install_guard(self):
        ctx=gen_cli.Context(user=False)
        for payload in ({'components':[[1.5,0,0,0]]},{'components':[[1,0,0,32768]]},
                        {'components':[[65535,0,0,0]]},{'components':[[1,0,0,0]],'text_flags':[2**31]}):
            self.assertIn('error',gen_cli.run_op(ctx,'export',dict(payload,format='wsc-fiddler')))
        with tempfile.TemporaryDirectory(prefix='guo-wsc-') as folder:
            root=Path(folder); install=root/'install'; install.mkdir()
            source=install/'source.wsc'; source.write_bytes(b'unchanged')
            cfg=SimpleNamespace(client_data=install,client_data_env=str(install),client_data_setting='')
            payload={'format':'wsc-fiddler','components':[[1650,-2,-3,7]]}
            with patch('guo.load_config',return_value=cfg):
                self.assertIn('error',gen_cli.run_op(ctx,'export',dict(payload,path=str(source))))
                self.assertEqual(source.read_bytes(),b'unchanged')
                dest=root/'output.wsc'; self.assertNotIn('error',gen_cli.run_op(ctx,'export',dict(payload,path=str(dest))))
                self.assertEqual(dest.read_bytes(),formats.write('wsc-fiddler',[formats.Tile(1650,-2,-3,7)]))


if __name__=='__main__': unittest.main()
