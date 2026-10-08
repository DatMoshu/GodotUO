"""Portable export policy and unsafe PCK path negative controls."""
import hashlib
import importlib.util
from pathlib import Path
import struct
import tempfile
from types import SimpleNamespace
import unittest

import audit_scanner as scanner


def package(name):
    payload=b"fixture"; encoded=name.encode()+b"\0"
    offset=100+4+len(encoded)+16+16+4
    header=struct.pack('<6I',0x43504447,2,4,7,2,0)+struct.pack('<Q',0)+bytes(64)+struct.pack('<I',1)
    return header+struct.pack('<I',len(encoded))+encoded+struct.pack('<2Q',offset,len(payload))+hashlib.md5(payload).digest()+struct.pack('<I',0)+payload


class WindowsTests(unittest.TestCase):
    def test_pck_root_dot_aliases_refused(self):
        for name in ('res://.', 'res://', 'res://a/./b', 'res://a//b', 'res://a/../b', 'res:///a'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                list(scanner.pck_payloads(package(name)))

    def test_nearby_paths_and_checksum_controls(self):
        for name in ('icon.png','.godot/exported/resource.res','assets/a.txt'):
            self.assertEqual(list(scanner.pck_payloads(package('res://'+name))),[(name,b'fixture')])
        changed=bytearray(package('res://icon.png')); changed[-1]^=1
        with self.assertRaises(ValueError):
            list(scanner.pck_payloads(bytes(changed)))

    def test_normal_renderer_contains_exact_reviewed_exclusions(self):
        spec=importlib.util.spec_from_file_location('windows_run',Path(__file__).with_name('run.py'))
        run=importlib.util.module_from_spec(spec); spec.loader.exec_module(run)
        expected=['addons/'+name+'/*' for name in ('guo_editor','godot_cef','guo_editor_assets','guo_editor_gumps','guo_editor_mapgen','guo_editor_multiedit','guo_editor_store','guo_posture')]
        expected+=['src/Input/Touch/Pregame/Accounts/'+name+'.cs' for name in ('AccountBook','AndroidKeystoreStore','DevLogin','LibsecretStore','SecretStore')]
        with tempfile.TemporaryDirectory() as directory:
            p=SimpleNamespace(preset_file=Path(directory)/'preset.cfg')
            run.render_preset(p,Path(directory)/'GUO.exe')
            text=p.preset_file.read_text(encoding='utf-8')
            line=next(line for line in text.splitlines() if line.startswith('exclude_filter='))
            self.assertEqual(line.split('"')[1].split(','),expected)
            self.assertIn('dotnet/include_scripts_content=false',text)
            self.assertNotIn('Accounts/*',line)


if __name__ == '__main__':
    unittest.main()
