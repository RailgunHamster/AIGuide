"""Verify built-in AIGuide in fresh projects without installing a project plugin."""

import json
import os
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

GODOT = Path(__file__).resolve().parents[1]


class BuiltInTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        editor = os.environ.get("AIGUIDE_TEST_EDITOR")
        if not editor:
            raise unittest.SkipTest("Set AIGUIDE_TEST_EDITOR to an integrated Godot .NET editor")
        cls.editor = Path(editor).resolve()
        if not cls.editor.is_file():
            raise RuntimeError(f"Editor does not exist: {cls.editor}")

    def verify_project(self, csharp=False):
        with tempfile.TemporaryDirectory(prefix="aiguide-builtin-") as temp:
            project = Path(temp).resolve() / "Project with spaces"
            project.mkdir()
            features = '"4.8", "GL Compatibility"' + (', "C#"' if csharp else "")
            config = (
                'config_version=5\n[application]\nconfig/name="AIGuide built-in test"\n'
                f"config/features=PackedStringArray({features})\n"
                '[rendering]\nrenderer/rendering_method="gl_compatibility"\n'
            )
            csproj = None
            if csharp:
                config += '[dotnet]\nproject/assembly_name="Probe"\n'
                sdk = ET.parse(GODOT / "AIGuide.Godot.csproj").getroot().attrib["Sdk"]
                csproj = project / "Probe.csproj"
                csproj.write_text(
                    f'<Project Sdk="{sdk}"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                    "<EnableDynamicLoading>true</EnableDynamicLoading></PropertyGroup></Project>\n",
                    encoding="utf-8",
                )
                (project / "Probe.cs").write_text(
                    "using Godot;\npublic partial class Probe : Node {}\n", encoding="utf-8"
                )
                result = subprocess.run(
                    ["dotnet", "build", str(csproj), "--nologo", "-v:q"],
                    cwd=project,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    check=False,
                )
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            (project / "project.godot").write_text(config, encoding="utf-8")
            original_csproj = csproj.read_bytes() if csproj else None
            with socket.socket() as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            env = os.environ.copy()
            env["GODOT_AI_GUIDE_PORT"] = str(port)
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

            def rpc(method, params=None):
                request = urllib.request.Request(
                    f"http://127.0.0.1:{port}/mcp",
                    data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}).encode(),
                    headers={"Content-Type": "application/json"},
                    method="POST",
                )
                with opener.open(request, timeout=2) as response:
                    value = json.load(response)
                if "error" in value:
                    raise RuntimeError(str(value["error"]))
                return value["result"]

            with tempfile.TemporaryFile(mode="w+", encoding="utf-8") as log:
                process = subprocess.Popen(
                    [str(self.editor), "--headless", "--editor", "--path", str(project)],
                    env=env,
                    stdout=log,
                    stderr=subprocess.STDOUT,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                )
                try:
                    deadline = time.monotonic() + 45
                    while True:
                        if process.poll() is not None or time.monotonic() >= deadline:
                            log.seek(0)
                            self.fail("Built-in AIGuide did not start:\n" + log.read()[-5000:])
                        try:
                            info = rpc("initialize", {"protocolVersion": "2025-06-18"})
                            break
                        except (OSError, urllib.error.URLError):
                            time.sleep(0.1)
                    self.assertEqual(info["serverInfo"]["name"], "godot-ai-guide")
                    tools = rpc("tools/list")["tools"]
                    names = {tool["name"] for tool in tools}
                    self.assertTrue({"editor_state", "ui_snapshot", "ui_act", "editor_exec"}.issubset(names))
                    self.assertEqual(len(names), len(tools))
                    result = rpc("tools/call", {"name": "editor_state", "arguments": {}})
                    self.assertFalse(result.get("isError"), result)
                    state = json.loads(result["content"][0]["text"])
                    self.assertEqual(Path(state["project_path"]).resolve(), project)
                finally:
                    if process.poll() is None:
                        try:
                            rpc(
                                "tools/call",
                                {
                                    "name": "editor_exec",
                                    "arguments": {
                                        "code": "EditorInterface.get_base_control().get_tree().quit()\nreturn true"
                                    },
                                },
                            )
                        except (OSError, urllib.error.URLError, RuntimeError):
                            pass
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait(timeout=5)
            self.assertFalse((project / "addons").exists())
            self.assertNotIn("[editor_plugins]", (project / "project.godot").read_text())
            if csproj:
                self.assertEqual(csproj.read_bytes(), original_csproj)
                self.assertEqual(list(project.glob("*.csproj")), [csproj])
            else:
                self.assertFalse(list(project.glob("*.csproj")))

    def test_gdscript_needs_no_csharp_host_or_addon(self):
        self.verify_project()

    def test_existing_csharp_project_needs_no_addon(self):
        self.verify_project(csharp=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
