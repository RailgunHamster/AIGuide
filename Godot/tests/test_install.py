"""Installer regressions; optional real build/startup via AIGUIDE_TEST_EDITOR."""

import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

GODOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("aiguide_install", GODOT / "install.py")
INSTALL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSTALL)

PROJECT = """; Keep this comment.
config_version=5

[application]
config/name="AI Guide installation test"
config/features=PackedStringArray("4.8", "GL Compatibility")

[rendering]
renderer/rendering_method="gl_compatibility"
"""
CSHARP = """<Project Sdk="Godot.NET.Sdk/4.8.0-beta">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework>
  <EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup>
</Project>
"""


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="aiguide-install-")
        self.addCleanup(self.temp.cleanup)
        self.project = Path(self.temp.name).resolve() / "Project with spaces"
        self.project.mkdir()
        (self.project / "project.godot").write_text(PROJECT, encoding="utf-8")

    def snapshot(self):
        return {
            str(path.relative_to(self.project)): path.read_bytes() for path in self.project.rglob("*") if path.is_file()
        }

    def install(self, **kwargs):
        return INSTALL.install(self.project, create=True, no_build=True, **kwargs)

    def test_check_does_not_write(self):
        before = self.snapshot()
        report = INSTALL.prepare(self.project, create=True)[3]
        self.assertTrue(report["creates_csharp"])
        self.assertEqual(self.snapshot(), before)

    def test_gdscript_conversion_requires_explicit_choice(self):
        with self.assertRaisesRegex(INSTALL.InstallError, "create-csharp-host"):
            INSTALL.install(self.project, no_build=True)
        self.assertEqual(len(self.snapshot()), 1)

    def test_create_host_copy_and_enable(self):
        self.install()
        self.assertTrue((self.project / "AIGuideHost.csproj").is_file())
        text = (self.project / "project.godot").read_text()
        self.assertIn("; Keep this comment.", text)
        self.assertIn('"GL Compatibility"', text)
        self.assertIn('"C#"', text)
        self.assertIn(INSTALL.PLUGIN, text)
        self.assertIn('project/assembly_name="AIGuideHost"', text)
        for relative, content in INSTALL.source_files().items():
            self.assertEqual((self.project / "addons/ai_guide" / relative).read_bytes(), content)

    def test_existing_csharp_project_is_preserved(self):
        path = self.project / "Existing.csproj"
        path.write_text(CSHARP)
        before = path.read_bytes()
        INSTALL.install(self.project, no_build=True)
        self.assertEqual(path.read_bytes(), before)
        self.assertFalse((self.project / "AIGuideHost.csproj").exists())
        self.assertIn('project/assembly_name="Existing"', (self.project / "project.godot").read_text())

    def test_other_enabled_plugins_and_settings_are_preserved(self):
        path = self.project / "project.godot"
        path.write_text(
            PROJECT
            + '\n[editor_plugins]\nenabled=PackedStringArray(\n"res://addons/other/plugin.cfg"\n)\n\n[custom]\nvalue=42\n'
        )
        self.install()
        text = path.read_text()
        self.assertIn('"res://addons/other/plugin.cfg"', text)
        self.assertIn("value=42", text)
        self.assertEqual(text.count(INSTALL.PLUGIN), 1)

    def test_reinstall_is_byte_identical(self):
        self.install()
        before = self.snapshot()
        self.install()
        self.assertEqual(self.snapshot(), before)

    def test_windows_newlines_and_bom_are_preserved(self):
        path = self.project / "project.godot"
        path.write_bytes(b"\xef\xbb\xbf" + PROJECT.replace("\n", "\r\n").encode())
        self.install()
        raw = path.read_bytes()
        self.assertTrue(raw.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\n", raw.replace(b"\r\n", b""))

    def test_unknown_existing_plugin_edits_are_not_overwritten(self):
        directory = self.project / "addons/ai_guide"
        directory.mkdir(parents=True)
        (directory / "AIGuidePlugin.cs").write_text("// Local customized plugin\n")
        before = self.snapshot()
        with self.assertRaisesRegex(INSTALL.InstallError, "Local plugin changes"):
            self.install()
        self.assertEqual(self.snapshot(), before)

    def test_modified_installed_plugin_is_protected(self):
        self.install()
        path = self.project / "addons/ai_guide/AIGuidePlugin.cs"
        path.write_bytes(path.read_bytes() + b"\n// Local edit\n")
        before = self.snapshot()
        with self.assertRaises(INSTALL.InstallError):
            self.install()
        self.assertEqual(self.snapshot(), before)

    def test_known_unmodified_plugin_can_be_updated(self):
        self.install()
        updated = INSTALL.source_files()
        updated["AIGuidePlugin.cs"] += b"\n// New upstream version\n"
        with patch.object(INSTALL, "source_files", return_value=updated):
            self.install()
        self.assertEqual((self.project / "addons/ai_guide/AIGuidePlugin.cs").read_bytes(), updated["AIGuidePlugin.cs"])

    def test_obsolete_owned_files_are_removed_but_user_files_survive(self):
        self.install()
        directory = self.project / "addons/ai_guide"
        (directory / "UserNotes.txt").write_text("Keep my notes")
        updated = INSTALL.source_files()
        del updated["AIGuidePlugin.cs.uid"]
        with patch.object(INSTALL, "source_files", return_value=updated):
            self.install()
        self.assertFalse((directory / "AIGuidePlugin.cs.uid").exists())
        self.assertEqual((directory / "UserNotes.txt").read_text(), "Keep my notes")

    def test_build_failure_restores_source_and_settings(self):
        before = self.snapshot()
        with (
            patch.object(INSTALL, "build", side_effect=INSTALL.InstallError("Build failed")),
            self.assertRaises(INSTALL.InstallError),
        ):
            INSTALL.install(self.project, create=True)
        self.assertEqual(self.snapshot(), before)

    def test_verification_failure_restores_source_and_settings(self):
        before = self.snapshot()
        with (
            patch.object(INSTALL, "build"),
            patch.object(INSTALL, "verify", side_effect=INSTALL.InstallError("Not running")),
            self.assertRaises(INSTALL.InstallError),
        ):
            INSTALL.install(self.project, create=True, editor=Path("unused"))
        self.assertEqual(self.snapshot(), before)

    def test_verification_cannot_skip_build(self):
        before = self.snapshot()
        with self.assertRaises(INSTALL.InstallError):
            self.install(editor=Path("unused"))
        self.assertEqual(self.snapshot(), before)

    def test_malformed_plugin_array_is_not_replaced(self):
        path = self.project / "project.godot"
        path.write_text(PROJECT + '\n[editor_plugins]\nenabled="custom expression"\n')
        before = self.snapshot()
        with self.assertRaises(INSTALL.InstallError):
            self.install()
        self.assertEqual(self.snapshot(), before)

    def test_ambiguous_csharp_projects_require_selection(self):
        (self.project / "One.csproj").write_text(CSHARP)
        (self.project / "Two.csproj").write_text(CSHARP)
        with self.assertRaisesRegex(INSTALL.InstallError, "Multiple C#"):
            self.install()
        report = self.install(csproj=Path("One.csproj"))
        self.assertEqual(Path(report["csproj"]).name, "One.csproj")

    def test_manifest_cannot_escape_the_addon(self):
        self.install()
        path = self.project / "addons/ai_guide" / INSTALL.RECORD
        record = json.loads(path.read_text())
        record["files"]["../../project.godot"] = "invalid"
        path.write_text(json.dumps(record))
        before = self.snapshot()
        with self.assertRaises(INSTALL.InstallError):
            self.install()
        self.assertEqual(self.snapshot(), before)

    def test_real_build_and_plugin_startup(self):
        editor = os.environ.get("AIGUIDE_TEST_EDITOR")
        if not editor:
            self.skipTest("Set AIGUIDE_TEST_EDITOR to a Godot 4.8 .NET executable")
        result = INSTALL.install(self.project, create=True, editor=Path(editor))
        self.assertEqual(result["verification"]["server"]["name"], "godot-ai-guide")
        self.assertGreaterEqual(result["verification"]["tool_count"], 10)


if __name__ == "__main__":
    unittest.main(verbosity=2)
