import importlib.util
import json
from pathlib import Path
import socket
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('bootstrap', Path(__file__).with_name('run.py'))
bootstrap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bootstrap)

class SetupTests(unittest.TestCase):
    def test_archive_cannot_escape_destination(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root/'bad.zip'
            with zipfile.ZipFile(archive, 'w') as out:
                out.writestr('source/../../outside', 'bad')
            with self.assertRaisesRegex(ValueError, 'Unsafe archive'):
                bootstrap.unpack(archive, root/'destination')
            self.assertFalse((root/'outside').exists())

    def test_missing_local_config_uses_defaults(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'launchers/_shared').mkdir(parents=True)
            (root/'launchers/_shared/config.bat').write_text('if not defined SPRITEMOTION_PORT set "SPRITEMOTION_PORT=8774"')
            with patch.object(bootstrap, 'ROOT', root), patch.dict(bootstrap.os.environ, {}, clear=True):
                self.assertEqual(bootstrap.settings()['SPRITEMOTION_PORT'], '8774')

    def test_wrong_pack_does_not_start_worker(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'workspace/ultima-online/fit-lab/test').mkdir(parents=True)
            (root/'workspace/ultima-online/fit-lab/test/manifest.json').write_text('{}')
            with patch.object(bootstrap, 'read_url', return_value={'pack':'another'}), patch.object(bootstrap.subprocess, 'Popen') as launch:
                with self.assertRaisesRegex(ValueError, 'another pack'):
                    bootstrap.start(root, Path('python'), {'SPRITEMOTION_FIT_PACK':'test'})
                launch.assert_not_called()

    def test_unrecognized_listener_does_not_start_worker(self):
        with tempfile.TemporaryDirectory() as directory, socket.socket() as listener:
            listener.bind(('127.0.0.1', 0)); listener.listen()
            root = Path(directory)
            (root/'workspace/ultima-online/fit-lab/test').mkdir(parents=True)
            (root/'workspace/ultima-online/fit-lab/test/manifest.json').write_text('{}')
            with patch.object(bootstrap, 'read_url', side_effect=OSError), patch.object(bootstrap.subprocess, 'Popen') as launch:
                with self.assertRaisesRegex(ValueError, 'unrecognized service'):
                    bootstrap.start(root, Path('python'), {'SPRITEMOTION_FIT_PACK':'test', 'SPRITEMOTION_PORT':str(listener.getsockname()[1])})
                launch.assert_not_called()

if __name__ == '__main__': unittest.main()
