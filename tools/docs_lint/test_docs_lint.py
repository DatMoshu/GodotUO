import tempfile
from pathlib import Path
import unittest
from run import anchors, check, missing_readmes


class DocsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "README.md"

    def run_fixture(self, text, other="# Target\n"):
        self.source.write_text(text, encoding="utf-8")
        (self.root / "other.md").write_text(other, encoding="utf-8")
        return check(self.root, [self.source])

    def test_duplicate_and_formatted_headings(self):
        self.assertEqual(anchors("# Hello, `world`!\n# Hello, world!\n# café & tea\n"),
                         {"hello-world", "hello-world-1", "café--tea"})

    def test_valid_links_references_and_html_anchor(self):
        self.assertEqual(self.run_fixture('[a](other.md#target) [b][label]\n[label]: other.md#explicit\n', '# Target\n<a id="explicit"></a>'), [])

    def test_missing_file_anchor_and_reference(self):
        issues = self.run_fixture('[a](missing.md)\n[b](other.md#absent)\n[c][undefined]\n')
        self.assertEqual([i.line for i in issues], [1, 2, 3])
        self.assertEqual(len(issues), 3)

    def test_examples_and_external_links_are_ignored(self):
        self.assertEqual(self.run_fixture('```md\n[a](missing.md)\n```\n`[b](missing.md)`\n[x](https://example.com)\n<!-- [c](missing.md) -->'), [])

    def test_setext_heading_and_encoded_path(self):
        (self.root / "with space.md").write_text("A title\n=======\n", encoding="utf-8")
        self.assertEqual(self.run_fixture('[a](with%20space.md#a-title)'), [])

    def test_outside_repository_is_rejected(self):
        self.assertIn("leaves repository", self.run_fixture('[a](../outside.md)')[0].message)

    def test_fenced_heading_is_not_an_anchor(self):
        self.assertEqual(anchors('~~~\n# Fake\n~~~\n## Real\n'), {"real"})

    def test_commented_heading_is_not_an_anchor(self):
        issues = self.run_fixture('[a](other.md#hidden)', '<!--\n# Hidden\n-->\n# Visible\n')
        self.assertEqual(len(issues), 1)
        self.assertIn("missing anchor", issues[0].message)

    def test_separator_after_heading_is_not_a_duplicate(self):
        self.assertEqual(anchors('# Real\n---\n'), {"real"})
        issues = self.run_fixture('[a](other.md#real-1)', '# Real\n---\n')
        self.assertEqual(len(issues), 1)

    def test_every_job_folder_needs_a_readme(self):
        for name in ("documented", "bare", "__pycache__", ".cache"):
            (self.root / "tools" / name).mkdir(parents=True)
        (self.root / "tools" / "documented" / "README.md").write_text("# documented\n", encoding="utf-8")
        (self.root / "tools" / "loose.py").write_text("", encoding="utf-8")
        issues = missing_readmes(self.root)
        self.assertEqual([i.path.name for i in issues], ["bare"])
        self.assertIn("no README.md", issues[0].message)

    def test_no_tools_folder_is_not_an_issue(self):
        self.assertEqual(missing_readmes(self.root), [])


if __name__ == "__main__":
    unittest.main()
