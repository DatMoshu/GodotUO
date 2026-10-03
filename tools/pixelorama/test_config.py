"""Regression checks for patching Godot ConfigFile preferences without INI parsing."""
import unittest

from run import enable_extension_text


class ExtensionConfigTests(unittest.TestCase):
    def test_preserves_multiline_variants(self):
        before = '[preferences]\nlayout={\n"a": Vector2(1, 2),\n"b": [1, 2]\n}\n\n[extensions]\nGUOTools=false\nother=true\n'
        self.assertEqual(enable_extension_text(before), before.replace('GUOTools=false', 'GUOTools=true'))

    def test_only_changes_extension_section(self):
        before = '[other]\nGUOTools=false\n[extensions]\nGUOTools=false\n[after]\nvalue="100%"\n'
        self.assertEqual(enable_extension_text(before), before.replace('[extensions]\nGUOTools=false', '[extensions]\nGUOTools=true'))

    def test_adds_missing_key_without_overwriting_next_section(self):
        before = '[extensions]\nOther=true\n[after]\nnumber=2\n'
        self.assertEqual(enable_extension_text(before), '[extensions]\nOther=true\nGUOTools=true\n[after]\nnumber=2\n')

    def test_new_section_and_idempotence(self):
        result = enable_extension_text('[preferences]\nvalue=1')
        self.assertEqual(result, '[preferences]\nvalue=1\n[extensions]\nGUOTools=true\n')
        self.assertEqual(enable_extension_text(result), result)

    def test_preserves_crlf(self):
        before = '[extensions]\r\nGUOTools=false\r\n'
        self.assertEqual(enable_extension_text(before), '[extensions]\r\nGUOTools=true\r\n')


if __name__ == '__main__':
    unittest.main()
