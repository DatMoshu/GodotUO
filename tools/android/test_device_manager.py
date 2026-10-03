import unittest
from unittest.mock import patch
from queue import Queue
import tempfile,json
from pathlib import Path
import device_manager as d

class DeviceManagerTests(unittest.TestCase):
 def manager(self,selected='fold',active='fold'):
  m=d.DeviceManager.__new__(d.DeviceManager);m.selected=selected;m.active=active;m.messages=Queue();m.events=[];m.recording=None
  return m
 def test_catalog_ports_and_ids(self):
  d.validate_catalog();self.assertEqual(len(d.PROFILES),15)
 def test_handheld_aliases_share_one_profile(self):
  self.assertEqual(set(d.PROFILES['handheld1080']['covers']),{'AYN Odin 2','AYN Odin 2 Mini','Retroid Pocket 5'})
 def test_selected_preset_cannot_capture_old_geometry(self):
  m=self.manager('rgcube','handheld1080')
  with patch.object(d,'adb') as adb:
   with self.assertRaisesRegex(RuntimeError,'Launch / apply'):m.screenshot()
   adb.assert_not_called()
 def test_unrelated_emulator_is_rejected(self):
  m=self.manager()
  with patch.object(d,'adb',return_value='someone_elses_avd\nOK'):
   with self.assertRaisesRegex(RuntimeError,'belongs to'):m.require_active()
 def test_cannot_change_profile_while_recording(self):
  m=self.manager();m.recording=('emulator-5554','12','/sdcard/a.mp4','a.mp4')
  with patch.object(d,'adb') as adb:
   with self.assertRaisesRegex(RuntimeError,'Stop and save'):m.prepare()
   adb.assert_not_called()
 def test_phone_rejects_fold_event(self):
  m=self.manager('pixel9','pixel9')
  with patch.object(d,'adb',return_value='guo_pixel9\nOK') as adb:
   with self.assertRaisesRegex(RuntimeError,'no native hinge'):m.fold(1,90)
   self.assertEqual(adb.call_count,1)
 def test_rotation_evidence(self):
  m=self.manager()
  with patch.object(d,'adb',return_value='guo_fold\nOK') as adb:
   m.rotate(3);self.assertEqual(m.events[-1]['value'],3)
   adb.assert_any_call('emulator-5554','shell','wm','user-rotation','lock','3')
 def test_stop_record_does_not_kill_reused_pid(self):
  m=self.manager();m.recording=('emulator-5554','12','/sdcard/a.mp4','a.mp4');m.record_profile='fold';m.record_start='test'
  with patch.object(d,'adb',return_value='unrelated-process') as adb,patch.object(d.time,'sleep'),patch.object(m,'metadata'):
   m.stop_record()
   self.assertFalse(any('kill' in c.args for c in adb.call_args_list))
   self.assertIsNone(m.recording)
 def test_recording_metadata_keeps_original_profile(self):
  m=self.manager('rgcube');m.recording=('emulator-5554','12','/sdcard/a.mp4','a.mp4');m.record_profile='fold';m.record_start='test'
  with patch.object(d,'adb',return_value='screenrecord\x00/sdcard/a.mp4'),patch.object(d.time,'sleep'),patch.object(m,'metadata') as meta:
   m.stop_record();meta.assert_called_once_with('a.mp4','fold',started='test')
 def test_sidecar_contains_geometry_and_steps(self):
  m=self.manager();m.event('fold',state=1,angle=90)
  with tempfile.TemporaryDirectory() as temp,patch.object(d,'OUT',Path(temp)),patch.object(d,'adb',return_value='physical 1080x2400'),patch.object(d,'run',return_value='abc123'):
   m.metadata('capture.png','fold');data=json.loads((Path(temp)/'capture.png.json').read_text())
   self.assertEqual(data['profile']['id'],'fold');self.assertEqual(data['events'][0]['angle'],90)
   self.assertEqual(data['git_revision'],'abc123');self.assertIn('display_size',data)

if __name__=='__main__': unittest.main()
