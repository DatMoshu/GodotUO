"""Regression: explicit output directory must not choose an unrelated tiledata file."""
import importlib.util,json,struct,tempfile,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('world_run',Path(__file__).with_name('run.py'))
world=importlib.util.module_from_spec(spec);spec.loader.exec_module(world)
class OutputPathTests(unittest.TestCase):
    def test_detached_export_and_readback(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);data=root/'install';project=root/'project';out=root/'elsewhere/export'
            for p in [data,project/'assets',out,root/'elsewhere/assets']:p.mkdir(parents=True)
            # Modern tiledata: full land section plus two static groups.
            raw=bytes(512*(4+32*30)+2*(4+32*41));(data/'tiledata.mul').write_bytes(raw)
            (project/'assets/tiledata.json').write_text(json.dumps({'0x000A':{'flags':'0x10000200','height':5,'name':'roof'}}))
            (root/'elsewhere/assets/tiledata.json').write_text(json.dumps({'0x000B':{'flags':'0x40','height':99,'name':'WRONG'}}))
            assets={k:[] for k in ['land','statics','gumps','hues','texmaps']};lines=[]
            self.assertEqual(world.export_assets(data,assets,out,lines,{},project=project),0)
            self.assertTrue(any(x.startswith('tiledata.mul=') for x in lines))
            rows=world.project_tiledata(project)
            self.assertEqual(world.verify_tiledata(data,rows,out),0)
            self.assertEqual((data/'tiledata.mul').read_bytes(),raw)
            changed=bytearray((out/'tiledata.mul').read_bytes());changed[0]=1;(out/'tiledata.mul').write_bytes(changed)
            self.assertEqual(world.verify_tiledata(data,rows,out),1)
            (out/'tiledata.mul').unlink();self.assertEqual(world.verify_tiledata(data,rows,out),1)
if __name__=='__main__':unittest.main()
