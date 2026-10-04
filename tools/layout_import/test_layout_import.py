"""Neutral synthetic catalogue checks; no CDDA install or retail art required."""
from __future__ import annotations

import json
import contextlib
import io
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from layout_import import cdda, db, run


class CatalogueTests(unittest.TestCase):
    def test_seeded_house_eligibility_requires_contiguous_authored_floors(self):
        from layout_import.hybrid import eligible_house
        house={'levels':[0,1],'rooms':[{'name':'bedroom'},{'name':'kitchen'},{'name':'bathroom'}],
               'bounds':[0,0,9,9]}
        self.assertFalse(eligible_house(house))
        self.assertTrue(eligible_house(house,2))
        for levels in ([1],[-1,0],[0,2],[0,1,2],[]):
            house['levels']=levels
            self.assertFalse(eligible_house(house,2))
        house['levels']=[0]
        self.assertTrue(eligible_house(house,2))
        house['bounds']=[0,0,11,9]
        self.assertFalse(eligible_house(house,2))

    def test_usage_event_is_idempotent_and_room_edits_have_distinct_instances(self):
        from layout_import import catalogue,semantic
        snapshot=cdda.scan(self.con,self.source)
        profile=cdda.make_profile(self.con,snapshot,[])
        cell={'x':0,'y':0,'role':'floor','indoors':True,'walkable':True,'furnishing_role':None,
              'access':'open','terrain':'neutral','furniture':'f_null'}
        room={'id':'r','cells':[[0,0]],'bounds':[0,0,0,0],'area':1,'use':'storage',
              'label_origin':'authored','confidence':1,'evidence':['neutral']}
        layout={'format':1,'name':'neutral','width':1,'height':1,'provenance':{},
                'levels':[{'z':0,'cells':[cell],'rooms':[room]}]}
        layout['layout_hash']=semantic.layout_hash(layout)
        first=catalogue.save_layout(self.con,profile,layout)
        self.assertEqual(first,catalogue.save_layout(self.con,profile,layout))
        room['use']='library'
        self.assertNotEqual(first,catalogue.save_layout(self.con,profile,layout))
        theme={'name':'neutral','version':1}
        build=catalogue.save_build(self.con,first,theme,'test',{'status':'native-valid'})
        catalogue.save_build(self.con,first,theme,'another-version',{'status':'native-valid'})
        self.assertEqual(cdda.coverage(self.con,profile)['generated_instances'],1)
        event={'id':'neutral','build':build,'project':'test','region':'test','placement':{'x':1},'state':'placed','timestamp':'2026-01-01T00:00:00Z'}
        catalogue.usage(self.con,event);catalogue.usage(self.con,event)
        self.assertEqual(self.con.execute('SELECT COUNT(*) FROM usage_event').fetchone()[0],1)
        event['placement']={'x':2}
        with self.assertRaises(ValueError): catalogue.usage(self.con,event)

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.source = self.root / "source"
        self.source.mkdir()
        (self.source / "LICENSE.txt").write_text("Synthetic test source; no imported layouts.")
        self.write("data/json/base.json", [
            {"type": "palette", "id": "shell", "terrain": {"壁": "t_wall"}},
            {"type": "mapgen", "om_terrain": "pilot", "weight": 10,
             "object": {"palettes": ["shell"], "rows": ["壁..", "..."]}},
            {"type": "mapgen", "om_terrain": "pilot", "weight": 20,
             "object": {"palettes": ["shell"], "rows": ["...", "..."]}},
            {"type": "mapgen", "nested_mapgen_id": "room", "object": {"mapgensize": [2, 1], "rows": [".."]}},
            {"type": "mapgen", "om_terrain": "nested", "object": {
                "place_nested": [{"chunks": [["room", 2], "null"], "else_chunks": ["absent"]}]}},
            {"type": "palette", "id": "dynamic", "palettes": [{"param": "theme"}]},
            {"type": "terrain", "abstract": "a", "copy-from": "b"},
            {"type": "terrain", "id": "b", "copy-from": "a"},
        ])
        self.write("data/mods/folder_not_id/modinfo.json", [{"type": "MOD_INFO", "id": "theme_mod", "dependencies": ["dda"]}])
        self.write("data/mods/folder_not_id/content.json", [{"type": "palette", "id": "shell", "terrain": {"壁": "t_other"}}])
        self.con = db.open_db(self.root / "catalogue.sqlite")

    def tearDown(self):
        self.con.close()
        self.tmp.cleanup()

    def write(self, rel, value):
        path = self.source / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")

    def test_repeat_scan_is_immutable_and_deduplicated(self):
        first = cdda.scan(self.con, self.source)
        profile = cdda.make_profile(self.con, first, [])
        before = cdda.coverage(self.con, profile)
        self.assertEqual(first, cdda.scan(self.con, self.source))
        self.assertEqual(profile, cdda.make_profile(self.con, first, []))
        self.assertEqual(before, cdda.coverage(self.con, profile))
        self.assertEqual(self.con.execute("SELECT COUNT(*) FROM definition").fetchone()[0], 10)
        self.assertEqual(before["sqlite_integrity"], "ok")
        self.assertFalse(before["foreign_key_errors"])

    def test_mod_identity_and_all_override_candidates_are_retained(self):
        snapshot = cdda.scan(self.con, self.source)
        core = cdda.make_profile(self.con, snapshot, [])
        themed = cdda.make_profile(self.con, snapshot, ["theme_mod"])
        self.assertEqual(cdda.coverage(self.con, themed)["load_order"], ["dda", "theme_mod"])
        core_count = self.con.execute("SELECT COUNT(*) FROM dependency_candidate WHERE profile_id=?", (core,)).fetchone()[0]
        mod_count = self.con.execute("SELECT COUNT(*) FROM dependency_candidate WHERE profile_id=?", (themed,)).fetchone()[0]
        self.assertEqual(mod_count, core_count + 2)
        self.assertEqual(self.con.execute("SELECT COUNT(*) FROM identity WHERE kind='om_terrain' AND value='pilot'").fetchone()[0], 2)

    def test_dynamic_missing_cycles_and_unicode_are_explicit(self):
        snapshot = cdda.scan(self.con, self.source)
        profile = cdda.make_profile(self.con, snapshot, [])
        result = cdda.coverage(self.con, profile)
        self.assertEqual(result["dependencies"]["missing"], 1)
        self.assertEqual(result["dependencies"]["dynamic"], 1)
        self.assertEqual(result["dependencies"]["no-op"], 1)
        widths = self.con.execute("SELECT row_codepoints_json FROM definition WHERE source_path='data/json/base.json' AND pointer='/1'").fetchone()[0]
        self.assertEqual(json.loads(widths), [3, 3])
        cyclic = self.con.execute("SELECT COUNT(*) FROM coverage WHERE profile_id=? AND reason LIKE '%cycle%'", (profile,)).fetchone()[0]
        self.assertEqual(cyclic, 2)
        self.assertEqual(sum(item["count"] for item in result["ledger"]), 10)

    def test_changed_source_creates_new_snapshot_without_deleting_old(self):
        original = cdda.scan(self.con, self.source)
        self.write("data/json/extra.json", [{"type": "furniture", "id": "f_test"}])
        changed = cdda.scan(self.con, self.source)
        self.assertNotEqual(original, changed)
        self.assertEqual(self.con.execute("SELECT COUNT(*) FROM definition WHERE snapshot_id=?", (original,)).fetchone()[0], 10)
        self.assertEqual(self.con.execute("SELECT COUNT(*) FROM definition WHERE snapshot_id=?", (changed,)).fetchone()[0], 11)

    def test_mod_cycles_and_missing_mods_block_profile(self):
        self.write("data/mods/loop/modinfo.json", [{"type": "MOD_INFO", "id": "loop", "dependencies": ["loop"]}])
        snapshot = cdda.scan(self.con, self.source)
        for mods in (["loop"], ["absent_mod"]):
            profile = cdda.make_profile(self.con, snapshot, mods)
            self.assertEqual(cdda.coverage(self.con, profile)["profile_status"], "blocked")

    def test_parse_error_preserved_and_profile_blocked(self):
        (self.source / "data/json/bad.json").write_text("[")
        snapshot = cdda.scan(self.con, self.source)
        profile = cdda.make_profile(self.con, snapshot, [])
        self.assertEqual(cdda.coverage(self.con, profile)["profile_status"], "blocked")
        self.assertIsNotNone(self.con.execute("SELECT parse_error FROM source_file WHERE path='data/json/bad.json'").fetchone()[0])

    def test_migrations_reopen_and_refuse_legacy_or_future_schema(self):
        reopened = db.open_db(self.root / "catalogue.sqlite")
        self.assertEqual(reopened.execute("PRAGMA user_version").fetchone()[0], db.SCHEMA_VERSION)
        reopened.close()
        for name, sql in (("legacy", "CREATE TABLE source_file(path TEXT)"), ("future", "PRAGMA user_version=999")):
            path = self.root / (name + ".sqlite")
            con = sqlite3.connect(path)
            con.execute(sql)
            con.close()
            with self.assertRaises(ValueError):
                db.open_db(path)

    def test_foreign_keys_reject_orphan(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.con.execute("INSERT INTO identity VALUES('missing','palette','shell')")

    def test_cli_rejects_source_write_and_catalogue_overwrite(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(run.main(["scan", "--source", str(self.source), "--db",
                                       str(self.source / "out.sqlite")]), 2)
            self.assertEqual(run.main(["coverage", "--db", str(self.root / "catalogue.sqlite"),
                                       "--profile", "none", "--out", str(self.root / "catalogue.sqlite")]), 2)
        self.assertFalse((self.source / "out.sqlite").exists())
        self.assertEqual(self.con.execute("PRAGMA integrity_check").fetchone()[0], "ok")


class AdapterTests(unittest.TestCase):
    def test_authored_stair_triplets_link_the_right_upper_exit(self):
        from layout_import.zomboid import stair_links
        tiles={key:{'properties':{flag:''}} for key,flag in [('b','stairsBN'),('m','stairsMN'),('t','stairsTN')]}
        decoded={(5,6,0):{'tiles':['b']},(5,5,0):{'tiles':['m']},(5,4,0):{'tiles':['t']}}
        self.assertEqual(stair_links(decoded,tiles)[0]['to'],[5,3,1])
        self.assertEqual(stair_links(decoded,tiles,owned={(8,8,0)}),[])
        del decoded[(5,4,0)]
        with self.assertRaises(ValueError): stair_links(decoded,tiles)

    def test_large_native_part_cut_preserves_cells_and_visibility_reach(self):
        from layout_import import native
        comps=[native.Component(1,x,y,7) for x in range(72) for y in range(72)]
        cut=native.fort.cut('neutral',comps)
        self.assertGreater(len(cut),1)
        restored=[c for _,part in cut for c in part]
        self.assertEqual(set(restored),set(comps))
        self.assertEqual(len(restored),len(comps))
        for _,part in cut:
            self.assertLessEqual(len(part),4676)
            self.assertLessEqual(max(c.x for c in part)-min(c.x for c in part),34)
            self.assertLessEqual(max(c.y for c in part)-min(c.y for c in part),34)
    def test_material_family_keeps_remote_edge_variants_out(self):
        from layout_import.district import coherent_ids
        self.assertEqual(coherent_ids([6,3,5,4,1400,1401,1402,1403]),[3,4,5,6])
    def test_impassable_cot_surface_cannot_be_used_as_a_step(self):
        from layout_import import native
        kinds={'0x0001':{'flags':['surface'],'height':0},'0x0002':{'flags':['surface','impassable'],'height':2}}
        stand,covered=native.walkcheck.surfaces([{'centre':[0,0],'comps':[native.Component(1,0,0,7),native.Component(2,0,0,9)]}],kinds)
        self.assertNotIn((0,0),stand)
        self.assertIn((0,0),covered)
    def test_rle_rejects_truncation_overrun_and_boolean_indices(self):
        from layout_import.bake import expand
        self.assertEqual(expand([[1,2],[0,1]],['a','b'],3),['b','b','a'])
        for runs in ([[0,2]],[[0,4]],[[True,3]],[[0,0]],[[2,3]],[[0,-1]]):
            with self.assertRaises(ValueError):
                expand(runs,['a','b'],3)

    def test_locked_metal_door_is_an_opening_not_wall(self):
        from layout_import.semantic import cell_semantics
        class Definitions:
            def terrain(self,_):
                return {'name':'locked metal door','move_cost':0,'flags':['LOCKED','WALL']}
            def furniture(self,_):
                return {}
        cell=cell_semantics('synthetic-door','f_null',Definitions())
        self.assertEqual((cell['role'],cell['access'],cell['walkable']),('door','locked',True))

    def test_void_never_becomes_an_interior_room(self):
        from layout_import.semantic import infer_rooms
        cells=[{'x':x,'y':y,'role':'void','walkable':False,'indoors':True,'furnishing_role':None,'access':'open'} for y in range(3) for x in range(3)]
        self.assertEqual(infer_rooms({'z':1,'cells':cells})['rooms'],[])

    def test_four_rotations_preserve_cells_and_source_lineage(self):
        from layout_import.semantic import rotate,layout_hash
        cells=[{'x':x,'y':y,'role':'exterior','walkable':True,'indoors':False,'furnishing_role':None,'access':'open','lineage':[x,y,0]} for y in range(2) for x in range(3)]
        layout={'format':1,'name':'neutral','width':3,'height':2,'levels':[{'z':0,'cells':cells}]}
        layout['layout_hash']=layout_hash(layout)
        turned=layout
        for _ in range(4):
            turned=rotate(turned,1)
        self.assertEqual(turned['levels'][0]['cells'],cells)
        self.assertEqual(turned['layout_hash'],layout['layout_hash'])

    def test_zomboid_header_authored_room_and_building_reference(self):
        from layout_import import zomboid
        import struct
        def ints(*n): return struct.pack('<'+'i'*len(n),*n)
        data=b'LOTH'+ints(1,1)+b'neutral_0\n'+ints(8,8,0,0,1)+b'kitchen\n'+ints(0,1,2,3,4,5,0,1,1,0)
        with tempfile.TemporaryDirectory() as root:
            p=Path(root)/'2_3.lotheader';p.write_bytes(data)
            result=zomboid.header(p)
            self.assertEqual(result['rooms'][0]['rects'],[(2,3,4,5)])
            self.assertEqual(result['buildings'][0]['rooms'],[0])
            p.write_bytes(data[:-1])
            with self.assertRaises(ValueError): zomboid.header(p)
            p.write_bytes(b'LOTH'+ints(99))
            with self.assertRaises(ValueError): zomboid.header(p)

    def test_zomboid_chunk_xy_order_empty_run_and_bad_palette(self):
        from layout_import import zomboid
        import struct
        def ints(*n): return struct.pack('<'+'i'*len(n),*n)
        info={'min_level':0,'max_level':0,'tiles':['neutral_0']}
        prefix=b'LOTP'+ints(1,0)+ints(8204,0)*1024
        with tempfile.TemporaryDirectory() as root:
            p=Path(root)/'neutral.lotpack'
            p.write_bytes(prefix+ints(2,4,0,-1,63))
            self.assertEqual(zomboid.chunk(p,info,0,0),{(0,0,0):{'room':4,'tiles':['neutral_0']}})
            p.write_bytes(prefix+ints(2,4,9,-1,63))
            with self.assertRaises(ValueError): zomboid.chunk(p,info,0,0)
            p.write_bytes(prefix+ints(-1,65))
            with self.assertRaises(ValueError): zomboid.chunk(p,info,0,0)

    def test_zomboid_tile_properties_preserve_edge_flags(self):
        from layout_import import zomboid
        import struct
        def ints(*n): return struct.pack('<'+'i'*len(n),*n)
        data=b'tdef'+ints(1,1)+b'neutral\nneutral.png\n'+ints(1,1,1,1,2)+b'WallN\n\nCustomName\nDoor\n'
        with tempfile.TemporaryDirectory() as root:
            p=Path(root)/'neutral.tiles';p.write_bytes(data)
            props=zomboid.tile_definitions(p)['neutral_0']['properties']
            self.assertEqual(props,{'WallN':'','CustomName':'Door'})
            p.write_bytes(data+b'unexpected')
            with self.assertRaises(ValueError): zomboid.tile_definitions(p)

    def test_schema_two_upgrade_keeps_old_rows_and_enforces_new_keys(self):
        with tempfile.TemporaryDirectory() as root:
            p=Path(root)/'catalogue.sqlite'
            con=sqlite3.connect(p)
            con.executescript(db.MIGRATIONS[1]+db.MIGRATIONS[2])
            con.execute("INSERT INTO source_snapshot VALUES('s','neutral','1',NULL,'unverified','neutral','{}','{}')")
            con.execute('PRAGMA user_version=2');con.commit();con.close()
            con=db.open_db(p)
            self.assertEqual(con.execute('SELECT id FROM source_snapshot').fetchone()[0],'s')
            with self.assertRaises(sqlite3.IntegrityError):
                con.execute("INSERT INTO layout_level VALUES('orphan',0,1,1,'{}')")
            con.close()


if __name__ == "__main__":
    unittest.main()
