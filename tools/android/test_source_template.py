"""Protect the Gradle template from Godot imports and unsafe archive paths."""
import tempfile
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace
from run import ensure_android_source

class SourceTemplateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.paths = SimpleNamespace(project=root / 'project', templates_dir=root / 'templates', templates_version='test')
        self.paths.templates_dir.mkdir()

    def archive(self, files):
        with zipfile.ZipFile(self.paths.templates_dir / 'android_source.zip', 'w') as z:
            for name, data in files.items(): z.writestr(name, data)

    def test_installs_source_and_prevents_import_scanning(self):
        self.archive({'build.gradle': '// pinned', 'res/drawable/icon.png': 'image'})
        ensure_android_source(self.paths)
        build = self.paths.project / 'android/build'
        self.assertTrue((build / '.gdignore').exists())
        self.assertEqual((build / 'build.gradle').read_text(), '// pinned')

    def test_second_export_preserves_customizations_and_removes_generated_imports(self):
        self.archive({'build.gradle': '// pinned'})
        ensure_android_source(self.paths)
        build = self.paths.project / 'android/build'
        (build / 'build.gradle').write_text('// local change')
        (build / 'res/drawable').mkdir(parents=True)
        generated = build / 'res/drawable/icon.webp.import'
        generated.write_text('Godot metadata')
        ensure_android_source(self.paths)
        self.assertFalse(generated.exists())
        self.assertEqual((build / 'build.gradle').read_text(), '// local change')

    def test_rejects_path_traversal(self):
        self.archive({'../../outside': 'bad'})
        with self.assertRaises(ValueError): ensure_android_source(self.paths)

if __name__ == '__main__': unittest.main()
