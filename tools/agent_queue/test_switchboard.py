"""Compatibility, migration and lease tests against independent switchboard DDL."""
import json
import sqlite3
import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
import test_agent_queue
from contextlib import contextmanager, closing

@contextmanager
def connection(path):
    with closing(sqlite3.connect(path)) as c, c:
        yield c

# Deliberately independent of store.SCHEMA: this is the unextended bus shape.
BUS_SCHEMA = '''
CREATE TABLE messages(id INTEGER PRIMARY KEY AUTOINCREMENT, to_addr TEXT NOT NULL,
 from_addr TEXT NOT NULL, kind TEXT NOT NULL, text TEXT NOT NULL, ref_id INTEGER,
 provenance TEXT NOT NULL, source_url TEXT, source_key TEXT UNIQUE,
 status TEXT NOT NULL DEFAULT 'new', created TEXT NOT NULL,
 taken_by TEXT, taken_at TEXT, answered_at TEXT);
CREATE TABLE addresses(address TEXT PRIMARY KEY, project TEXT, tool TEXT, role TEXT,
 integrator INTEGER NOT NULL DEFAULT 0, registered TEXT, last_seen TEXT);
CREATE TABLE leases(resource TEXT PRIMARY KEY, holder TEXT NOT NULL, purpose TEXT,
 taken TEXT NOT NULL, until TEXT NOT NULL);
'''


class SwitchboardTests(unittest.TestCase):
    # Reuse the scratch CLI helpers; the legacy cases run once in their own suite.
    setUp = test_agent_queue.QueueTests.setUp
    reap = test_agent_queue.QueueTests.reap
    run_cli = test_agent_queue.QueueTests.run_cli
    spawn = test_agent_queue.QueueTests.spawn
    post = test_agent_queue.QueueTests.post
    lines = test_agent_queue.QueueTests.lines

    @contextmanager
    def bus(self):
        Path(self.db).parent.mkdir(parents=True, exist_ok=True)
        with connection(self.db) as c:
            c.executescript(BUS_SCHEMA)
            yield c

    def test_external_schema_round_trip(self):
        with self.bus() as c:
            c.execute("INSERT INTO messages(to_addr,from_addr,kind,text,provenance,created) VALUES('alpha','chat','note','external note','agent','2026-01-01T00:00:00+00:00')")
        rid = self.post('alpha', 'round trip')
        got = self.lines(self.run_cli('take', '--as', 'alpha'))
        self.assertEqual([g['kind'] for g in got], ['note', 'request'])
        self.assertEqual(got[-1]['to_addr'], 'alpha')
        reply = int(self.run_cli('reply', str(rid), '--from', 'alpha', 'done'))
        status = json.loads(self.run_cli('status'))
        self.assertEqual(status['addresses'][0]['address'], 'alpha')
        self.assertFalse(status['addresses'][0]['stale'])
        self.assertIsNotNone(status['addresses'][0]['last_seen'])
        with connection(self.db) as c:
            self.assertEqual(c.execute('SELECT kind,ref_id,to_addr FROM messages WHERE id=?', (reply,)).fetchone(), ('reply', rid, 'chat'))
            self.assertIsNotNone(c.execute('SELECT answered_at FROM messages WHERE id=?', (rid,)).fetchone()[0])
        self.assertEqual(self.lines(self.run_cli('replies', str(rid), '--wait', '0'))[0]['id'], reply)
        self.run_cli('post', '--to', 'chat', '--from', 'alpha', '--kind', 'note', '--ref', str(rid), 'progress')
        self.assertEqual([r['kind'] for r in self.lines(self.run_cli('replies', str(rid)))], ['reply', 'note'])
        self.assertEqual(json.loads(self.run_cli('show', str(rid)))['replies'][0]['text'], 'done')
        self.assertEqual(self.lines(self.run_cli('list', '--from', 'alpha'))[0]['kind'], 'reply')

    def test_legacy_migration_preserves_archive_and_attachments(self):
        Path(self.db).parent.mkdir(parents=True, exist_ok=True)
        with connection(self.db) as c:
            c.executescript('''
            CREATE TABLE requests(id INTEGER PRIMARY KEY, to_agent TEXT, from_agent TEXT,
              text TEXT, attachments TEXT, status TEXT, created TEXT, taken_by TEXT, taken_at TEXT);
            CREATE TABLE replies(id INTEGER PRIMARY KEY, request_id INTEGER,
              from_agent TEXT, text TEXT, attachments TEXT, created TEXT);
            INSERT INTO requests VALUES(7,'alpha','chat','old','["local.txt"]','answered','2026-01-01T00:00:00Z','alpha','2026-01-01T00:00:01Z');
            INSERT INTO replies VALUES(7,7,'alpha','old reply','["answer.txt"]','2026-01-01T00:00:02Z');
            INSERT INTO requests VALUES(9,'beta','chat','waiting','[]','new','2026-01-01T00:00:00Z',NULL,NULL);
            ''')
        old = json.loads(self.run_cli('show', '7'))
        self.assertEqual(old['attachments'], ['local.txt'])
        self.assertEqual(old['replies'][0]['request_id'], 7)
        self.assertEqual(old['replies'][0]['attachments'], ['answer.txt'])
        self.assertEqual(self.lines(self.run_cli('take', '--as', 'beta'))[0]['id'], 9)
        # Reopening never duplicates old rows; archives keep original ids.
        self.assertEqual(len(self.lines(self.run_cli('list'))), 3)
        with connection(self.db) as c:
            self.assertEqual(c.execute('SELECT id FROM replies_legacy').fetchone()[0], 7)
            self.assertEqual(c.execute('SELECT legacy_reply_id FROM message_attachments WHERE legacy_reply_id IS NOT NULL').fetchone()[0], 7)
        self.assertGreater(self.post('alpha'), 9)

    def test_approval_guard_and_source_key(self):
        self.run_cli('post','--to','alpha','--from','chat','--kind','approval','no',expect=1)
        for provenance in ('owner-discord','owner-reaction','owner-terminal','owner-dot-chat'):
            rid = self.run_cli('post','--to','alpha','--from','chat','--kind','approval','--provenance',provenance,'yes')
            self.assertEqual(json.loads(self.run_cli('show',rid))['provenance'], provenance)
        rid = self.run_cli('post','--to','alpha','--from','chat','--key','source-1','one')
        self.assertEqual(self.run_cli('post','--to','alpha','--from','chat','--key','source-1','duplicate'), 'None')
        self.assertEqual(json.loads(self.run_cli('show',rid))['text'],'one')

    def test_register_and_lease_exclusion(self):
        self.run_cli('register','alpha','--project','test','--tool','codex','--integrator')
        self.run_cli('register','beta','--project','test','--tool','claude','--integrator',expect=1)
        status=json.loads(self.run_cli('status'))
        self.assertTrue(status['addresses'][0]['stale'])
        self.assertIsNone(status['addresses'][0]['last_seen'])
        self.run_cli('lease','take','runtime','--holder','alpha','--minutes','1')
        self.run_cli('lease','take','runtime','--holder','beta',expect=1)
        self.run_cli('lease','release','runtime','--holder','beta',expect=1)
        self.assertEqual(self.lines(self.run_cli('lease','list'))[0]['holder'],'alpha')
        with connection(self.db) as c:
            c.execute("UPDATE leases SET until='2000-01-01T00:00:00Z'")
        self.assertTrue(json.loads(self.run_cli('status'))['leases'][0]['expired'])
        self.run_cli('lease','take','runtime','--holder','beta')
        self.run_cli('lease','release','runtime','--holder','beta')
        self.assertEqual(self.lines(self.run_cli('lease','list')),[])
        self.run_cli('lease','take','runtime','--holder','alpha','--minutes','0',expect=2)

    def test_take_empty_and_replies_timeout(self):
        self.assertEqual(self.run_cli('take','--as','alpha'),'')
        rid = self.post('alpha')
        self.run_cli('replies',str(rid),'--wait','0.05',expect=3)
        self.assertEqual(self.lines(self.run_cli('tail','--as','alpha','--once','--poll','0.01','--timeout','1'))[0]['id'],rid)

    def test_mixed_migration_refuses_without_losing_rows(self):
        with self.bus() as c:
            c.execute("INSERT INTO messages(to_addr,from_addr,kind,text,provenance,created) VALUES('alpha','chat','note','bus','agent','2026-01-01T00:00:00Z')")
            c.execute('CREATE TABLE requests(id INTEGER PRIMARY KEY)')
            c.execute('INSERT INTO requests VALUES(1)')
        self.run_cli('status',expect=1)
        with connection(self.db) as c:
            self.assertEqual(c.execute('SELECT COUNT(*) FROM messages').fetchone()[0],1)
            self.assertEqual(c.execute('SELECT COUNT(*) FROM requests').fetchone()[0],1)


if __name__ == '__main__':
    unittest.main()
