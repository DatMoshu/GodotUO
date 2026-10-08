"""Neutral temporary evidence fixtures; no retail data or engine processes."""
import copy
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from layout_import import evidence, deploy


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.built, self.stage = self.root / "built", self.root / "stage"
        self.built.mkdir(); self.stage.mkdir(); (self.built / "parts").mkdir(); (self.built / "world").mkdir()
        self.write(self.built / "district.json", {"status":"native-valid", "name":"district", "origin":[8,16],
            "parcels":[{"part":"a", "build_id":"build"}]})
        self.scene = {"parts":[{"name":"a", "id":10, "centre":[1,2], "doors":[], "components":1}],
                      "tour":[{"name":"a_entry", "x":1, "y":2, "z":0}, {"name":"a_room", "x":2, "y":2, "z":0}]}
        self.write(self.built / "scene.json", self.scene)
        self.write(self.built / "parts/a.json", [[1,0,0,0,1]])
        self.write(self.stage / "scenes.json", {"district":self.scene})
        self.write(self.stage / "district-stage.json", {"district":str(self.built), "passed":True})
        (self.stage / "map.mul").write_bytes(b"stage-content")
        (self.stage / "files_override.txt").write_text("map0.mul=" + str(self.stage / "map.mul"))
        self.write(self.stage / "district-stage.json", {"format":2,"district":str(self.built),"passed":True,
            "input_content":{**evidence.content(self.built), **evidence.content(self.built,("parts","world"))},
            "stage_content":evidence.stage_files(self.stage),"overrides":evidence.override_hashes(self.stage)})
        self.con = sqlite3.connect(":memory:")
        self.addCleanup(self.con.close)
        self.con.executescript("CREATE TABLE build(id,status); CREATE TABLE artifact(build_id,kind,path,sha256); CREATE TABLE validation(build_id,kind,evidence_hash,result_json)")
        self.con.execute("INSERT INTO build VALUES('build','native-valid')")
        self.con.execute("INSERT INTO artifact VALUES(?,?,?,?)", ("build","native-components",str(self.built / "parts/a.json"),evidence.file_hash(self.built / "parts/a.json")))
        self.raw = {"district":"district", "scope":"whole-district", "site":[8,16,0],
                    "proof_contract":evidence.contract(self.built,self.stage), "place":{"district.a":{"ok":True,"components":1,"doors":0,"at":[9,18,0]}},
                    "stops":{t["name"]:{"local":[t["x"],t["y"],t["z"]], "target":[8+t["x"],16+t["y"],t["z"]], "arrived":True} for t in self.scene["tour"]}}

    def write(self, path, value):
        path.write_text(json.dumps(value), encoding="utf-8")

    def test_whole_tour_passes(self):
        self.assertEqual(evidence.validate(self.con,"build",self.raw)[::2], (True,True))

    def test_scoped_tour_is_not_whole_build_acceptance(self):
        self.raw.update(scope="a", proof_contract=evidence.contract(self.built,self.stage,"a"))
        self.assertEqual(evidence.validate(self.con,"build",self.raw)[::2], (True,False))
        report=self.root / "report.json"; self.write(report,self.raw)
        self.assertFalse(deploy.record_evidence(self.con,"build","gameplay",report))
        stored=json.loads(self.con.execute("SELECT result_json FROM validation").fetchone()[0])
        self.assertTrue(stored["scope_passed"]); self.assertFalse(stored["whole_build"])

    def test_wrong_build_with_same_components_refused(self):
        self.con.execute("INSERT INTO build VALUES('other','native-valid')")
        self.con.execute("INSERT INTO artifact SELECT 'other',kind,path,sha256 FROM artifact")
        self.assertFalse(evidence.validate(self.con,"other",self.raw)[0])

    def test_missing_selected_build_component_refused(self):
        self.con.execute("INSERT INTO artifact VALUES('build','native-components','elsewhere/parts/b.json','unproved')")
        self.assertFalse(evidence.validate(self.con,"build",self.raw)[0])

    def test_wrong_district_omitted_stop_omitted_part_and_teleport_refused(self):
        for mutate in (lambda r:r.update(district="other"), lambda r:r["stops"].pop("a_room"),
                       lambda r:r["place"].clear(), lambda r:r["stops"]["a_room"].update(jump=True),
                       lambda r:r["stops"]["a_room"].update(target=[0,0,0]),
                       lambda r:r["place"]["district.a"].update(components=0),
                       lambda r:r["place"]["district.a"].update(at=[0,0,0]),
                       lambda r:r["place"]["district.a"].update(doors=1)):
            raw=copy.deepcopy(self.raw); mutate(raw)
            self.assertFalse(evidence.validate(self.con,"build",raw)[0])

    def test_stage_and_component_mutation_refused(self):
        for path in (self.stage / "map.mul", self.built / "parts/a.json"):
            original=path.read_bytes(); path.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError,"stale district stage"):
                evidence.validate(self.con,"build",self.raw)
            path.write_bytes(original)

    def test_stale_stage_before_proof_and_legacy_stage_refused(self):
        (self.stage / "map.mul").write_bytes(b"changed before proof")
        with self.assertRaisesRegex(ValueError,"stale district stage"):
            evidence.contract(self.built,self.stage)
        self.write(self.stage / "district-stage.json", {"format":1,"district":str(self.built),"passed":True})
        with self.assertRaisesRegex(ValueError,"unbound"):
            evidence.contract(self.built,self.stage)

    def test_legacy_report_cannot_certify_build(self):
        self.raw.pop("proof_contract")
        self.assertFalse(evidence.validate(self.con,"build",self.raw)[0])

    def test_reused_combine_and_hybrid_outputs_refuse_before_input_read(self):
        from types import SimpleNamespace
        from layout_import.combine import combine
        from layout_import.hybrid import populate
        out=self.root / "old"; out.mkdir(); (out / "stale").write_text("keep")
        cfg=SimpleNamespace(client_data=self.root / "retail")
        with self.assertRaisesRegex(ValueError,"new or empty"):
            combine(cfg,self.built,self.root / "building",[0,0],"test",out)
        with self.assertRaisesRegex(ValueError,"new or empty"):
            populate(cfg,self.built,self.root / "source",None,None,None,None,"seed",1,"test",out)
        self.assertEqual((out / "stale").read_text(),"keep")


if __name__ == "__main__":
    unittest.main()
