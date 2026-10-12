import subprocess
import unittest
from pathlib import Path
from unittest.mock import patch

import avd
from run import app_folder, emulator_media_path

DEVICES = """List of devices attached
0a1b2c3d\tdevice
emulator-5556\tdevice
emulator-5554\toffline
"""

ADB = Path("adb")
PHONE = avd.Avd("guo_phone", 5556, "pixel_9", "system-images;android-35;google_apis;x86_64")


class CatalogTests(unittest.TestCase):
    def test_guo_phone_is_on_its_own_port(self):
        phone = avd.find("guo_phone")
        self.assertEqual(phone.serial, "emulator-5556")
        self.assertIn("x86_64", phone.system_image)

    def test_every_avd_is_listed_once(self):
        names = [p["avd"] for p in avd.catalog()["profiles"]]
        self.assertEqual(sorted(set(names)), sorted(avd.avds()))

    def test_an_avd_on_two_ports_is_refused(self):
        data = {"system_image": "img", "profiles": [
            {"avd": "a", "port": 5554, "sdk_device": "x"}, {"avd": "a", "port": 5556, "sdk_device": "x"}]}
        with self.assertRaises(ValueError):
            avd.avds(data)

    def test_an_unknown_name_exits(self):
        with self.assertRaises(SystemExit):
            avd.find("someone_elses_avd")


class StateTests(unittest.TestCase):
    def test_parse_state(self):
        self.assertEqual(avd.parse_state(DEVICES, "emulator-5556"), "device")
        self.assertEqual(avd.parse_state(DEVICES, "emulator-5554"), "offline")
        self.assertIsNone(avd.parse_state(DEVICES, "emulator-5558"))

    def test_a_physical_serial_is_never_mistaken_for_the_emulator(self):
        self.assertIsNone(avd.parse_state("List of devices attached\n0a1b2c3d\tdevice\n", "emulator-5556"))


class StartStopTests(unittest.TestCase):
    def test_does_not_start_with_too_little_memory(self):
        with patch.object(avd, "adb_state", return_value=None), \
             patch.object(avd, "existing", return_value=["guo_phone"]), \
             patch.object(avd, "free_ram_gb", return_value=6.0), \
             patch.object(avd.subprocess, "Popen") as popen:
            with self.assertRaises(SystemExit) as stop:
                avd.start(Path("sdk"), ADB, PHONE, Path("log"))
        popen.assert_not_called()
        self.assertIn("not starting", str(stop.exception))

    def test_a_port_held_by_another_avd_is_left_alone(self):
        with patch.object(avd, "adb_state", return_value="device"), \
             patch.object(avd, "avd_name_of", return_value="jarvis_thor"), \
             patch.object(avd.subprocess, "Popen") as popen:
            with self.assertRaises(SystemExit):
                avd.start(Path("sdk"), ADB, PHONE, Path("log"))
        popen.assert_not_called()

    def test_an_already_running_guo_phone_is_reused_and_not_owned(self):
        with patch.object(avd, "adb_state", return_value="device"), \
             patch.object(avd, "avd_name_of", return_value="guo_phone"), \
             patch.object(avd, "wait_booted"), \
             patch.object(avd.subprocess, "Popen") as popen:
            self.assertIsNone(avd.start(Path("sdk"), ADB, PHONE, Path("log")))
        popen.assert_not_called()

    def test_stop_refuses_another_avd_on_the_port(self):
        with patch.object(avd, "adb_state", return_value="device"), \
             patch.object(avd, "avd_name_of", return_value="jarvis_thor"), \
             patch.object(avd.subprocess, "run") as run:
            self.assertFalse(avd.stop(ADB, PHONE))
        run.assert_not_called()

    def test_stop_uses_the_console_on_its_own_serial(self):
        states = iter(["device", None])
        calls = []

        def fake_run(cmd, **kw):
            calls.append(cmd)
            return subprocess.CompletedProcess(cmd, 0)

        with patch.object(avd, "adb_state", side_effect=lambda *_: next(states, None)), \
             patch.object(avd, "avd_name_of", return_value="guo_phone"), \
             patch.object(avd.subprocess, "run", side_effect=fake_run):
            self.assertTrue(avd.stop(ADB, PHONE))
        self.assertEqual(calls, [["adb", "-s", "emulator-5556", "emu", "kill"]])


class DataPathTests(unittest.TestCase):
    def test_app_folder(self):
        self.assertEqual(app_folder("/sdcard/Android/data/org.guo.client/files/uo", "org.guo.client"),
                         "/sdcard/Android/data/org.guo.client")
        self.assertEqual(app_folder("/sdcard/uo", "org.guo.client"), "/sdcard/uo")

    def test_media_path_under_fuse(self):
        self.assertEqual(emulator_media_path("/sdcard/Android/data/org.guo.client"),
                         "/data/media/0/Android/data/org.guo.client")
        self.assertEqual(emulator_media_path("/storage/emulated/0/x"), "/data/media/0/x")


if __name__ == "__main__":
    unittest.main()
