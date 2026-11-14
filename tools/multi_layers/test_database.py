"""Synthetic source-custody checks; no installed art or shared database writes."""
from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
import database as D

class SourceTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name);self.db=self.root/"fixture.sqlite"
        self.room={"id":3,"name":"bar","z":0,"rects":[[1,2,3,4]]}
        self.building={"levels":[0,1],"rooms":[self.room],"map":"fixture","header":"0_0.lotheader","header_sha256":hashlib.sha256(b"fixture").hexdigest()}
        with closing(sqlite3.connect(self.db)) as c, c:
            c.executescript("CREATE TABLE current_snapshot(source,snapshot);CREATE TABLE building(snapshot,source_id,analysis);CREATE TABLE room(snapshot,source_id,room_id,geometry);CREATE TABLE pattern(snapshot,kind,key,data);")
            c.executemany("INSERT INTO current_snapshot VALUES(?,?)",[("project-zomboid","pz"),("ultima-online","uo")])
            c.execute("INSERT INTO building VALUES(?,?,?)",("pz","one",json.dumps(self.building)))
            c.execute("INSERT INTO room VALUES(?,?,?,?)",("pz","one",3,json.dumps(self.room)))
            c.executemany("INSERT INTO pattern VALUES(?,?,?,?)",[("uo","families","wooden","{}"),("uo","pieces","0x0001","{}")])
        folder=self.root/"media/maps/fixture";folder.mkdir(parents=True);(folder/"0_0.lotheader").write_bytes(b"fixture")
    def tearDown(self):self.tmp.cleanup()
    def test_read_only_and_current_snapshot(self):
        before=self.db.read_bytes();b,r,s,p=D.read_source(self.db,"one")
        self.assertEqual(r,[self.room]);self.assertEqual(s["project-zomboid"],"pz");self.assertEqual(before,self.db.read_bytes())
    def test_missing_source(self):
        with self.assertRaises(ValueError):D.read_source(self.db,"absent")
    def test_missing_db_is_not_created(self):
        absent=self.root/"absent.db"
        with self.assertRaises(sqlite3.OperationalError):D.read_source(absent,"one")
        self.assertFalse(absent.exists())
    def test_room_drift_refused(self):
        with closing(sqlite3.connect(self.db)) as c, c:c.execute("UPDATE room SET geometry='{}'")
        with self.assertRaises((ValueError,KeyError)):D.read_source(self.db,"one")
    def test_stale_header_refused(self):
        D.verify_header(self.root,self.building)
        (self.root/"media/maps/fixture/0_0.lotheader").write_bytes(b"changed")
        with self.assertRaises(ValueError):D.verify_header(self.root,self.building)
    def test_path_escape_refused(self):
        b={**self.building,"map":"../../../../outside"}
        with self.assertRaises(ValueError):D.verify_header(self.root,b)
    def test_non_two_storey_refused(self):
        with closing(sqlite3.connect(self.db)) as c, c:c.execute("UPDATE building SET analysis=?",(json.dumps({**self.building,"levels":[0]}),))
        with self.assertRaises(ValueError):D.read_source(self.db,"one")

if __name__=="__main__":unittest.main()
