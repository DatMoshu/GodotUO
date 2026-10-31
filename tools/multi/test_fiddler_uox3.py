"""Fiddler UOX3 export-only contract and refusal regressions."""
from pathlib import Path
import sys
import unittest
import tempfile
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parent))
import formats
import gen_cli


class FiddlerUox3Tests(unittest.TestCase):
    def test_exact_bytes_and_duplicate_order(self):
        tile=formats.Tile(0xabcd,-32768,32767,-32768,text_flags=-1)
        data=formats.write('uox3-fiddler',[tile,tile])
        expected=''.join(f'[HOUSE ITEM {i}]\r\n{{\r\nITEM=0xABCD\r\nX=-32768\r\nY=32767\r\nZ=-32768\r\n}}\r\n\r\n' for i in range(2)).encode('ascii')
        self.assertEqual(data,expected)
        self.assertNotEqual(data,formats.write('uox3',[tile,tile]))

    def test_export_only_and_legacy_import_retained(self):
        data=formats.write('uox3-fiddler',[formats.Tile(1,12,-8,7)])
        with self.assertRaisesRegex(formats.FormatError,'export-only'): formats.read('uox3-fiddler',data)
        self.assertEqual(formats.read('uox3',data),[formats.Tile(1,12,-8,7)])

    def test_bounds_and_extra_loss_refused(self):
        for tiles in ([],[formats.Tile(65536,0,0,0)],[formats.Tile(1,32768,0,0)],
                      [formats.Tile(1,0,0,0,text_extra=1)],[formats.Tile(1,0,0,0)]*65536):
            with self.subTest(count=len(tiles)),self.assertRaises(formats.FormatError): formats.write('uox3-fiddler',tiles)

    def test_cli_scalar_validation_and_no_recentering(self):
        ctx=gen_cli.Context(user=False)
        out=gen_cli.run_op(ctx,'export',{'format':'uox3-fiddler','components':[[65535,12,-8,7]],'text_flags':[-1]})
        self.assertIn('ITEM=0xFFFF\r\nX=12\r\nY=-8\r\nZ=7',out['data'])
        for row in ([1,0.5,0,0],['1',0,0,0],[1,0,0,0,2]):
            self.assertIn('error',gen_cli.run_op(ctx,'export',{'format':'uox3-fiddler','components':[row]}))

    def test_cli_protected_install_and_atomic_external_output(self):
        with tempfile.TemporaryDirectory(prefix='guo-uox3-') as folder:
            root=Path(folder); install=root/'install';install.mkdir();source=install/'keep.dfn';source.write_bytes(b'keep')
            cfg=SimpleNamespace(client_data=install,client_data_env=str(install),client_data_setting='')
            payload={'format':'uox3-fiddler','components':[[1,12,-8,7]],'text_flags':[1]}
            with patch('guo.load_config',return_value=cfg):
                self.assertIn('error',gen_cli.run_op(gen_cli.Context(user=False),'export',dict(payload,path=str(source))))
                self.assertEqual(source.read_bytes(),b'keep')
                target=root/'out.dfn';self.assertNotIn('error',gen_cli.run_op(gen_cli.Context(user=False),'export',dict(payload,path=str(target))))
                self.assertIn(b'ITEM=0x0001\r\nX=12\r\nY=-8\r\nZ=7',target.read_bytes())
                self.assertEqual(list(root.glob('*.tmp-*')),[])


if __name__=='__main__': unittest.main()
