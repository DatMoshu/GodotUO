"""Local-only CLI refusal controls; invalid spellings never reach filesystem probes."""
import contextlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import run_native


class RunnerInputs(unittest.TestCase):
    def test_network_device_uri_and_drive_relative_refused_before_filesystem(self):
        bad = [r'\\invalid-host\share\run', '//invalid-host/share/run', '/\\invalid-host/share/run',
               r'\\?\C:\run', r'\\?\UNC\invalid-host\share', r'\\.\pipe\run', r'\??\C:\run',
               r'\Device\HarddiskVolume1\run', 'file:///C:/run', 'https://invalid-host/run',
               'C:drive-relative', 'local:stream', 'bad\0path']
        for argument in ['--out', '--assembly']:
            for raw in bad:
                with self.subTest(argument=argument, raw=raw):
                    argv = ['run_native.py', '--out', 'fresh-local-output']
                    if argument == '--out': argv[-1] = raw
                    else: argv += ['--assembly', raw]
                    with patch('sys.argv', argv), contextlib.redirect_stderr(io.StringIO()) as err, \
                         patch.object(Path, 'resolve', side_effect=AssertionError('unexpected resolve')), \
                         patch.object(Path, 'exists', side_effect=AssertionError('unexpected exists')), \
                         patch.object(Path, 'mkdir', side_effect=AssertionError('unexpected mkdir')), \
                         patch('subprocess.Popen', side_effect=AssertionError('unexpected child')):
                        with self.assertRaises(SystemExit) as exit:
                            run_native.main()
                    self.assertEqual(exit.exception.code, 2)
                    self.assertIn('local path spellings', err.getvalue())

    def test_literal_local_spellings_are_accepted_without_expansion(self):
        for raw in ['relative space/%ENV%/!bang! &/é›ª', 'C:/literal %ENV% !bang! & é›ª', r'D:\local\file.dll']:
            with self.subTest(raw=raw): self.assertTrue(run_native.local_spelling(raw))

    def test_unicode_console_uses_safe_escaped_fallback(self):
        buffer = io.BytesIO()
        console = io.TextIOWrapper(buffer, encoding="cp1252")
        with patch("sys.stdout", console): run_native.report_line("literal %ENV% !bang! & 雪")
        console.flush()
        self.assertEqual(buffer.getvalue().decode("cp1252").strip(), r"literal %ENV% !bang! & \u96ea")

    def test_existing_output_refused_without_child_or_fixture_mutation(self):
        with tempfile.TemporaryDirectory(prefix='guo-layers-cli-') as temp:
            root = Path(temp); witness = root / 'literal %ENV% !bang! & é›ª'
            witness.mkdir(); (witness / 'kept.txt').write_text('kept')
            before = {p.name: p.read_bytes() for p in witness.iterdir()}
            with patch('sys.argv', ['run_native.py', '--out', str(witness)]), \
                 contextlib.redirect_stderr(io.StringIO()), \
                 patch('subprocess.Popen', side_effect=AssertionError('unexpected child')), \
                 patch('tempfile.mkdtemp', side_effect=AssertionError('unexpected fixture')):
                with self.assertRaises(SystemExit) as exit: run_native.main()
            self.assertEqual(exit.exception.code, 2)
            self.assertEqual(before, {p.name: p.read_bytes() for p in witness.iterdir()})

    def test_checkout_output_refused_before_fixture_or_child(self):
        target = Path(__file__).resolve().parents[3] / 'new-synthetic-output-must-not-exist'
        self.assertFalse(target.exists())
        with patch('sys.argv', ['run_native.py', '--out', str(target)]), \
             contextlib.redirect_stderr(io.StringIO()), \
             patch('subprocess.Popen', side_effect=AssertionError('unexpected child')), \
             patch('tempfile.mkdtemp', side_effect=AssertionError('unexpected fixture')):
            with self.assertRaises(SystemExit) as exit: run_native.main()
        self.assertEqual(exit.exception.code, 2)
        self.assertFalse(target.exists())


if __name__ == '__main__':
    unittest.main()
