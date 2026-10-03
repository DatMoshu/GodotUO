# SPDX-License-Identifier: BSD-2-Clause
import copy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
sys.path.insert(0,str(Path(__file__).resolve().parent))
from server_adapters.adapter import CAPABILITIES, load, render, symbol, validate
from server_adapters.run import generate, install
from fixtures import full_export, item_export

class Adapters(unittest.TestCase):
    def test_all_backends_generate_native_items(self):
        for backend in CAPABILITIES:
            files=render(item_export(),backend,{'test:stone':0x50000})
            manifest=json.loads(files['adapter-manifest.json'])
            self.assertEqual(manifest['backend'],backend)
            self.assertEqual(manifest['identity_hash'],'a'*64)
            self.assertIn('test:stone',manifest['identities'])
            self.assertTrue(manifest['files'])

    def test_csharp_all_sections(self):
        for backend in ('servuo','runuo'):
            code=render(full_export(),backend)['Scripts/Custom/GUO/GUOContent.cs']
            for value in ('Items.Add(', 'Loot.Add(', 'Creatures.Add(', 'StageBlock(', 'StageRegion(', 'StageDecorations(', 'TileData.ItemTable[id]=tile'):
                self.assertIn(value,code)

    def test_unsupported_sections_fail_closed(self):
        for backend in ('pol','sphere','uox3'):
            for section in ('maps','tiles','regions','decorations','loot','creatures'):
                value=item_export();value[section]=full_export()[section]
                with self.assertRaisesRegex(ValueError,'does not support '+section):render(value,backend,{'test:stone':0x50000})

    def test_native_units_and_mobility(self):
        for movable in (True,False):
            data=item_export();data['items'][0]['movable']=movable
            pol=render(data,'pol',{'test:stone':0x50000})['pkg/guo_content/config/itemdesc.cfg']
            sphere=render(data,'sphere')['scripts/guo_content.scp']
            uox=render(data,'uox3')['dfndata/items/guo_content.dfn']
            self.assertIn('Weight 3/2',pol);self.assertIn('Movable '+str(int(movable)),pol)
            self.assertIn('WEIGHT=1.5',sphere);self.assertIn('ATTR=0'+('20' if movable else '10'),sphere)
            self.assertIn('WEIGHT=150',uox);self.assertIn('MOVABLE='+('1' if movable else '2'),uox)

    def test_no_lossy_weight_rounding(self):
        for backend in ('sphere','uox3'):
            value=item_export();value['items'][0]['weight']=1.234
            with self.assertRaises(ValueError):render(value,backend)

    def test_explicit_pol_slots_and_collision(self):
        with self.assertRaisesRegex(ValueError,'explicit'):render(item_export(),'pol')
        data=item_export();data['items'].append(dict(data['items'][0],identity='test:other'))
        with self.assertRaisesRegex(ValueError,'Duplicate POL'):render(data,'pol',{'test:stone':0x50000,'test:other':0x50000})

    def test_native_names_cannot_inject_directives(self):
        for name in ('stone\nON=@Create','<eval 1>','foo//bar','stone{bad}','foo%bar','stone\x00'):
            for backend in CAPABILITIES:
                value=item_export();value['items'][0]['name']=name
                if backend in ('servuo','runuo') and '\n' not in name and '\x00' not in name:continue
                with self.assertRaises(ValueError):render(value,backend,{'test:stone':0x50000})

    def test_csharp_string_literal_escaping(self):
        value=item_export();value['items'][0]['name']='quote " \\ end'
        code=render(value,'servuo')['Scripts/Custom/GUO/GUOContent.cs']
        self.assertIn('quote \\" \\\\ end',code)

    def test_ids_stable_across_other_items_and_deployments(self):
        original=render(item_export(),'sphere');data=item_export();data['identity_hash']='b'*64
        data['items'].insert(0,dict(data['items'][0],identity='a:first'))
        changed=render(data,'sphere')
        self.assertEqual(json.loads(original['adapter-manifest.json'])['identities']['test:stone'],json.loads(changed['adapter-manifest.json'])['identities']['test:stone'])
        self.assertNotEqual(symbol('a-b:c'),symbol('a:b-c'))

    def test_unknown_fields_duplicates_and_schema(self):
        for change in [lambda d:d.update(schema='other'),lambda d:d.update(executable='bad'),lambda d:d['items'].append(d['items'][0]),lambda d:d['items'][0].update(weight=float('nan')),lambda d:d['items'][0].update(graphic=True)]:
            value=item_export();change(value)
            with self.assertRaises(ValueError):render(value,'servuo')

    def test_references_and_budgets(self):
        for change in [lambda d:d['loot'][0]['content']['entries'][0].update(item='missing:item'),lambda d:d['creatures'][0]['content'].update(loot='missing:loot'),lambda d:d['maps'][0]['content']['blocks'][0]['land'].pop(),lambda d:d['decorations'][0]['items'].append(d['decorations'][0]['items'][0]),lambda d:d['tiles'][0]['content'].update(animation=1)]:
            data=full_export();change(data)
            with self.assertRaises(ValueError):render(data,'servuo')

    def test_duplicate_json_fields(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'bad.json';path.write_text('{"schema":1,"schema":2}')
            with self.assertRaisesRegex(ValueError,'Duplicate JSON'):load(path)

    def test_generate_refuses_partial_or_existing_output(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);source=root/'input.json';source.write_text(json.dumps(full_export()))
            with self.assertRaises(ValueError):generate(source,'sphere',root/'out')
            self.assertFalse((root/'out').exists())
            source.write_text(json.dumps(item_export()));generate(source,'sphere',root/'out')
            with self.assertRaises(ValueError):generate(source,'sphere',root/'out')

    def test_install_update_preserves_unrelated_and_refuses_edits(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);source=root/'input.json';source.write_text(json.dumps(item_export()));bundle=generate(source,'sphere',root/'bundle');shard=root/'shard';shard.mkdir();(shard/'world.save').write_text('keep')
            install(bundle,shard);install(bundle,shard)
            self.assertEqual((shard/'world.save').read_text(),'keep')
            (shard/'scripts/guo_content.scp').write_text('custom edit')
            with self.assertRaisesRegex(ValueError,'edited'):install(bundle,shard)
            self.assertEqual((shard/'scripts/guo_content.scp').read_text(),'custom edit')

    def test_bundle_tampering_and_unowned_file_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);source=root/'input.json';source.write_text(json.dumps(item_export()));bundle=generate(source,'sphere',root/'bundle');shard=root/'shard';(shard/'scripts').mkdir(parents=True)
            (shard/'scripts/guo_content.scp').write_text('mine')
            with self.assertRaisesRegex(ValueError,'unowned'):install(bundle,shard)
            (bundle/'scripts/guo_content.scp').write_text('tampered')
            with self.assertRaisesRegex(ValueError,'checksum'):install(bundle,shard)

    def test_sphere_new_graphic_conflicts_require_explicit_reuse(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);source=root/'input.json';source.write_text(json.dumps(item_export()));shard=root/'shard';(shard/'scripts').mkdir(parents=True)
            (shard/'scripts/existing.scp').write_text('[ITEMDEF 01771]\nNAME=Existing\n')
            bundle=generate(source,'sphere',root/'bundle')
            with self.assertRaisesRegex(ValueError,'already exists'):install(bundle,shard)
            bundle=generate(source,'sphere',root/'reuse',existing_graphics=[6001]);install(bundle,shard)
            self.assertNotIn('[ITEMDEF 01771]',(shard/'scripts/guo_content.scp').read_text())
            self.assertEqual((shard/'scripts/existing.scp').read_text(),'[ITEMDEF 01771]\nNAME=Existing\n')

    def test_published_deployment_cannot_desynchronize_via_standalone_install(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);source=root/'input.json';source.write_text(json.dumps(item_export()));shard=root/'shard';shard.mkdir();bundle=generate(source,'sphere',root/'bundle')
            install(bundle,shard,{'Data/GUO/server-content.json':b'export','Data/GUO/public/shard-content.json':b'descriptor'})
            with self.assertRaisesRegex(ValueError,'together'):install(bundle,shard)

    def test_install_lock_prevents_concurrent_writers(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);(root/'Data/GUO').mkdir(parents=True);lock=root/'Data/GUO/adapter-install.lock';lock.write_text('another installer')
            with self.assertRaisesRegex(ValueError,'installation is active'):install(root/'bundle',root)
            self.assertEqual(lock.read_text(),'another installer')

    def test_publication_failure_rolls_back_previous_deployment(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);source=root/'input.json';data=item_export();source.write_text(json.dumps(data));bundle=generate(source,'sphere',root/'first');shard=root/'shard';shard.mkdir()
            extra={'Data/GUO/server-content.json':b'old export','Data/GUO/public/shard-content.json':b'old descriptor'};install(bundle,shard,extra)
            before={str(p.relative_to(shard)):p.read_bytes() for p in shard.rglob('*') if p.is_file() and 'revisions' not in p.parts}
            data['items'][0]['name']='Updated';source.write_text(json.dumps(data));bundle=generate(source,'sphere',root/'next');real=os.replace;calls=[]
            def fail_once(a,b):
                calls.append(b)
                if len(calls)==3:raise OSError('injected publication failure')
                return real(a,b)
            with patch('server_adapters.run.os.replace',side_effect=fail_once):
                with self.assertRaises(OSError):install(bundle,shard,{k:b'new' for k in extra})
            for name,expected in before.items():self.assertEqual((shard/name).read_bytes(),expected)

if __name__=='__main__':unittest.main()
