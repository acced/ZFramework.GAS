"""Tests for project creation guards and package provenance; these do not run Unity."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("prepare_iron_oath_unity", ROOT / "Tools~" / "prepare_iron_oath_unity.py")
BOOTSTRAP = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BOOTSTRAP)


class UnityProjectPreparationTests(unittest.TestCase):
    def test_new_project_pins_packages_and_preserves_sample_guids(self):
        with tempfile.TemporaryDirectory(prefix="iron-oath-bootstrap-") as directory:
            project = Path(directory) / "Project with spaces"
            BOOTSTRAP.prepare(project, "6000.3.0f1", None)
            manifest = json.loads((project / "Packages" / "manifest.json").read_text())
            dependencies = manifest["dependencies"]
            self.assertEqual("https://github.com/acced/ZFramework.GameplayTags.git#" + BOOTSTRAP.TAGS_COMMIT,
                             dependencies[BOOTSTRAP.TAGS_PACKAGE])
            gas_reference = dependencies["com.acced.zframework.gas"].removeprefix("file:")
            self.assertEqual(ROOT.resolve(), (project / "Packages" / gas_reference).resolve())
            for relative in ("Scenes/IronOathDemo.unity", "Scenes/IronOathDemo.unity.meta",
                             "Runtime/Presentation/IronOathDemo.cs.meta"):
                self.assertEqual((ROOT / "Samples~" / "IronOath" / relative).read_bytes(),
                                 (project / "Assets" / "IronOath" / relative).read_bytes())
            self.assertTrue((project / "Assets" / "IronOathTests" / "PlayMode" / "IronOathPlayableFlowTests.cs").is_file())
            self.assertEqual((ROOT / "Tests~" / "IronOath.Unity.meta").read_bytes(),
                             (project / "Assets" / "IronOathTests.meta").read_bytes())
            self.assertFalse((project / "Assets" / "IronOath" / "OptionalSpine~").exists())
            self.assertFalse((project / "Assets" / "IronOath" / "OptionalSpine~.meta").exists())

    def test_existing_nonempty_directory_is_unchanged(self):
        with tempfile.TemporaryDirectory(prefix="iron-oath-bootstrap-") as directory:
            project = Path(directory)
            sentinel = project / "keep-me.txt"
            sentinel.write_text("user work", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "new or completely empty"):
                BOOTSTRAP.prepare(project, "6000.3.0f1", None)
            self.assertEqual("user work", sentinel.read_text())
            self.assertEqual([sentinel], list(project.iterdir()))

    def test_empty_existing_directory_is_supported(self):
        with tempfile.TemporaryDirectory(prefix="iron-oath-bootstrap-") as directory:
            project = Path(directory)
            BOOTSTRAP.prepare(project, "6000.3.0f1", None)
            self.assertTrue((project / "Packages" / "manifest.json").is_file())

    def test_package_tree_destination_is_rejected_before_writing(self):
        with self.assertRaisesRegex(ValueError, "outside the GAS"):
            BOOTSTRAP.prepare(ROOT / "ShouldNeverBeCreated", "6000.3.0f1", None)
        self.assertFalse((ROOT / "ShouldNeverBeCreated").exists())

    def test_wrong_editor_version_is_rejected_before_writing(self):
        with tempfile.TemporaryDirectory(prefix="iron-oath-bootstrap-") as directory:
            project = Path(directory) / "new-project"
            with self.assertRaisesRegex(ValueError, "6000.3"):
                BOOTSTRAP.prepare(project, "2022.3.0f1", None)
            self.assertFalse(project.exists())

    def test_dirty_or_unpinned_local_dependency_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix="iron-oath-bootstrap-") as directory:
            tags = Path(directory) / "Tags"
            tags.mkdir()
            (tags / "package.json").write_text(json.dumps({"name": BOOTSTRAP.TAGS_PACKAGE}))
            for revision, status in (("wrong-head", ""), (BOOTSTRAP.TAGS_COMMIT, " M Runtime.cs")):
                with self.subTest(revision=revision, status=status):
                    project = Path(directory) / "new-project"
                    replies = [subprocess.CompletedProcess([], 0, revision + "\n", ""),
                               subprocess.CompletedProcess([], 0, status, "")]
                    with patch.object(BOOTSTRAP.subprocess, "run", side_effect=replies):
                        with self.assertRaisesRegex(ValueError, "clean at pinned commit"):
                            BOOTSTRAP.prepare(project, "6000.3.0f1", tags)
                    self.assertFalse(project.exists())


if __name__ == "__main__":
    unittest.main()
