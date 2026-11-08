"""Install the Godot editor plugin from this checkout into a Godot project."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "addons/ai_guide"
PLUGIN = "res://addons/ai_guide/plugin.cfg"
RECORD = ".aiguide-install.json"
SECTION = re.compile(r"(?m)^\[([^\]\r\n]+)\][ \t]*(?:;[^\r\n]*)?$")


class InstallError(RuntimeError):
    pass


def sha(content):
    return hashlib.sha256(content).hexdigest()


def setting_span(text, section, key):
    sections = list(SECTION.finditer(text))
    matches = [i for i, match in enumerate(sections) if match[1] == section]
    if len(matches) > 1:
        raise InstallError(f"Duplicate [{section}] sections; resolve them before installing")
    if not matches:
        return None, len(text), False
    index = matches[0]
    start = sections[index].end()
    end = sections[index + 1].start() if index + 1 < len(sections) else len(text)
    entries = list(re.finditer(r"(?m)^" + re.escape(key) + r"[ \t]*=[ \t]*", text[start:end]))
    if len(entries) > 1:
        raise InstallError(f"Duplicate {section}/{key} settings")
    if not entries:
        return None, end, True
    entry = entries[0]
    value_start = start + entry.end()
    # Project settings used here are strings or PackedStringArray, possibly spanning lines.
    pos, quote, escaped, depth = value_start, False, False, 0
    while pos < end:
        char = text[pos]
        if quote:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                quote = False
        elif char == '"':
            quote = True
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
        elif char in "\n;" and depth == 0:
            break
        pos += 1
    if quote or depth != 0:
        raise InstallError(f"Unterminated value for {section}/{key}")
    return (value_start, pos), end, True


def value_of(text, section, key):
    span, _, _ = setting_span(text, section, key)
    return text[span[0] : span[1]].strip() if span else None


def set_value(text, section, key, value):
    span, end, present = setting_span(text, section, key)
    if span:
        return text[: span[0]] + value + text[span[1] :]
    addition = f"{key}={value}\n"
    if present:
        return text[:end].rstrip("\n") + "\n" + addition + "\n" + text[end:]
    return text.rstrip("\n") + f"\n\n[{section}]\n\n" + addition


def add_array_value(text, section, key, item):
    current = value_of(text, section, key)
    values = []
    if current is not None:
        match = re.fullmatch(r"PackedStringArray\((.*)\)", current, re.S)
        if not match:
            raise InstallError(f"Expected PackedStringArray for {section}/{key}; no settings were changed")
        try:
            values = json.loads("[" + match[1] + "]")
        except ValueError as error:
            raise InstallError(f"Invalid string array for {section}/{key}") from error
        if not all(isinstance(value, str) for value in values):
            raise InstallError(f"Expected strings for {section}/{key}")
    if item not in values:
        values.append(item)
    return set_value(
        text, section, key, "PackedStringArray(" + ", ".join(json.dumps(x, ensure_ascii=False) for x in values) + ")"
    )


def project_file(path):
    return path.read_bytes().decode("utf-8-sig").replace("\r\n", "\n")


def encode_project(text, original):
    if b"\r\n" in original:
        text = text.replace("\n", "\r\n")
    return (b"\xef\xbb\xbf" if original.startswith(b"\xef\xbb\xbf") else b"") + text.encode("utf-8")


def safe_target(project, relative):
    path = project / relative
    if not path.resolve().is_relative_to(project):
        raise InstallError(f"Installation target escapes project: {path}")
    for parent in [path, *path.parents]:
        if parent == project:
            break
        if parent.is_symlink() or (hasattr(parent, "is_junction") and parent.is_junction()):
            raise InstallError(f"Linked installation target requires manual management: {parent}")
    return path


def source_files():
    files = {}
    for path in SOURCE.rglob("*"):
        if path.is_symlink():
            raise InstallError(f"Unexpected source symlink: {path}")
        if path.is_file() and path.name != RECORD:
            files[path.relative_to(SOURCE).as_posix()] = path.read_bytes()
    if "plugin.cfg" not in files or "AIGuidePlugin.cs" not in files:
        raise InstallError("Incomplete Godot checkout: missing plugin sources")
    return files


def csharp_project(project, text, create, selected=None):
    if selected:
        path = selected if selected.is_absolute() else project / selected
        path = safe_target(project, path.relative_to(project))
        candidates = [path] if path.is_file() else []
    else:
        candidates = sorted(project.glob("*.csproj"))
    if len(candidates) > 1:
        name = value_of(text, "dotnet", "project/assembly_name")
        expected = project / (json.loads(name) + ".csproj") if name else None
        if expected in candidates:
            candidates = [expected]
        else:
            raise InstallError("Multiple C# projects; select one using --csproj")
    if candidates:
        path = safe_target(project, candidates[0].relative_to(project))
        document = ET.fromstring(path.read_text(encoding="utf-8-sig"))
        if not document.attrib.get("Sdk", "").startswith("Godot.NET.Sdk/"):
            raise InstallError("The selected project must use Godot.NET.Sdk")
        frameworks = [element.text for element in document.iter() if element.tag.split("}")[-1] == "TargetFramework"]
        if "net10.0" not in frameworks:
            raise InstallError("This plugin targets Godot 4.8 / net10.0; update the C# project before installing")
        assembly_names = [element.text for element in document.iter() if element.tag.split("}")[-1] == "AssemblyName"]
        assembly = assembly_names[0] if assembly_names else path.stem
        if not assembly or "$" in assembly:
            raise InstallError("Set an explicit AssemblyName in the C# project before installing")
        configured_name = value_of(text, "dotnet", "project/assembly_name")
        if configured_name and json.loads(configured_name) != assembly:
            raise InstallError("Selected C# assembly does not match dotnet/project/assembly_name")
        text = set_value(text, "dotnet", "project/assembly_name", json.dumps(assembly))
        text = add_array_value(text, "application", "config/features", "C#")
        return path, None, text
    if selected:
        raise InstallError("Selected C# project does not exist")
    if not create:
        raise InstallError(
            "No C# project found. For a GDScript project add --create-csharp-host (requires Godot .NET)."
        )
    name = value_of(text, "dotnet", "project/assembly_name")
    assembly = json.loads(name) if name else "AIGuideHost"
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.-]*", assembly):
        raise InstallError("Set a simple dotnet/project/assembly_name before creating the C# host")
    template = ET.parse(ROOT / "AIGuide.Godot.csproj").getroot()
    sdk = template.attrib["Sdk"]
    content = (
        f'<Project Sdk="{sdk}">\n  <PropertyGroup>\n'
        "    <TargetFramework>net10.0</TargetFramework>\n"
        "    <EnableDynamicLoading>true</EnableDynamicLoading>\n"
        f"    <RootNamespace>{assembly.replace('-', '_')}</RootNamespace>\n"
        "  </PropertyGroup>\n</Project>\n"
    ).encode()
    text = set_value(text, "dotnet", "project/assembly_name", json.dumps(assembly))
    if value_of(text, "application", "config/features") is None:
        text = add_array_value(text, "application", "config/features", "4.8")
    text = add_array_value(text, "application", "config/features", "C#")
    return safe_target(project, assembly + ".csproj"), content, text


def prepare(project, create=False, csproj=None):
    config = project / "project.godot"
    if not config.is_file():
        raise InstallError(f"Not a Godot project: {config}")
    original = config.read_bytes()
    text = project_file(config)
    csharp, new_project, text = csharp_project(project, text, create, csproj)
    text = add_array_value(text, "editor_plugins", "enabled", PLUGIN)
    safe_target(project, "addons/ai_guide")
    record_path = safe_target(project, "addons/ai_guide/" + RECORD)
    record = json.loads(record_path.read_text(encoding="utf-8")) if record_path.exists() else {}
    if not isinstance(record, dict) or (
        record and (record.get("schema") != 1 or not isinstance(record.get("files"), dict))
    ):
        raise InstallError("Unrecognized AI Guide installation manifest")
    owned = record.get("files", {})
    if not all(isinstance(name, str) and isinstance(digest, str) for name, digest in owned.items()):
        raise InstallError("Invalid file inventory in AI Guide installation manifest")
    incoming = source_files()
    changes = {config: encode_project(text, original)}
    if new_project is not None:
        changes[csharp] = new_project
    for relative in sorted(set(incoming) | set(owned)):
        relative_path = Path(relative)
        if relative_path.is_absolute() or ".." in relative_path.parts or relative == RECORD:
            raise InstallError("Invalid file path in AI Guide installation manifest")
        target = safe_target(project, "addons/ai_guide/" + relative)
        before = target.read_bytes() if target.exists() else None
        after = incoming.get(relative)
        if before is not None and before != after and sha(before) != owned.get(relative):
            raise InstallError(
                f"Local plugin changes would be overwritten: {target}. Back them up or resolve them first."
            )
        changes[target] = after
    report = {
        "project": str(project),
        "csproj": str(csharp),
        "creates_csharp": new_project is not None,
        "plugin_files": len(incoming),
        "changes": [
            str(p.relative_to(project))
            for p, data in changes.items()
            if (p.read_bytes() if p.exists() else None) != data
        ],
    }
    report["status"] = "installed" if not report["changes"] and record else "ready_to_install"
    return changes, record_path, incoming, report


def build(csharp):
    result = subprocess.run(["dotnet", "build", str(csharp), "-c", "Debug", "--nologo"], cwd=csharp.parent, check=False)
    if result.returncode:
        raise InstallError("dotnet build failed; see build output")


def install(project, *, create=False, csproj=None, no_build=False, editor=None):
    if no_build and editor:
        raise InstallError("Omit --no-build when verifying installation with --editor")
    changes, record_path, incoming, report = prepare(project, create, csproj)
    revision = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=ROOT.parent, capture_output=True, text=True, check=False
    )
    record = {
        "schema": 1,
        "source_commit": revision.stdout.strip() if revision.returncode == 0 else None,
        "files": {name: sha(data) for name, data in incoming.items()},
    }
    changes[record_path] = (json.dumps(record, indent=2) + "\n").encode("utf-8")
    before = {path: path.read_bytes() if path.exists() else None for path in changes}
    existing_directories = {parent for path in changes for parent in path.parents if parent.is_dir()}
    try:
        for path, content in changes.items():
            if content is None:
                path.unlink(missing_ok=True)
            elif before[path] != content:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(content)
        if not no_build:
            build(Path(report["csproj"]))
        if editor:
            report["verification"] = verify(project, editor)
    except Exception:
        for path, content in before.items():
            if content is None:
                path.unlink(missing_ok=True)
            else:
                path.write_bytes(content)
        # Remove only directories created by this installation, after restoring files.
        for directory in sorted({p.parent for p in changes}, key=lambda p: len(p.parts), reverse=True):
            if (
                directory not in existing_directories
                and directory != project
                and directory.is_dir()
                and not any(directory.iterdir())
            ):
                directory.rmdir()
        raise
    report["status"] = "installed_unbuilt" if no_build else "installed"
    return report


def verify(project, editor, timeout=45):
    editor = editor.expanduser().resolve()
    if not editor.is_file():
        raise InstallError(f"Editor does not exist: {editor}")
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    env = os.environ.copy()
    env["GODOT_AI_GUIDE_PORT"] = str(port)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def rpc(method, params=None):
        message = {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}
        request = urllib.request.Request(
            f"http://127.0.0.1:{port}/mcp",
            data=json.dumps(message).encode(),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with opener.open(request, timeout=2) as response:
            value = json.load(response)
        if "error" in value:
            raise InstallError(str(value["error"]))
        return value["result"]

    with tempfile.TemporaryFile(mode="w+", encoding="utf-8") as log:
        process = subprocess.Popen(
            [str(editor), "--headless", "--editor", "--path", str(project)],
            env=env,
            stdout=log,
            stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
        try:
            deadline = time.monotonic() + timeout
            while True:
                if process.poll() is not None:
                    log.seek(0)
                    raise InstallError("Editor exited before plugin startup:\n" + log.read()[-5000:])
                try:
                    info = rpc("initialize", {"protocolVersion": "2025-06-18"})
                    break
                except (OSError, urllib.error.URLError):
                    if time.monotonic() >= deadline:
                        log.seek(0)
                        raise InstallError("Plugin startup timed out:\n" + log.read()[-5000:])
                    time.sleep(0.1)
            tools = rpc("tools/list")["tools"]
            if not {"editor_state", "ui_snapshot", "ui_act", "editor_exec"}.issubset({tool["name"] for tool in tools}):
                raise InstallError("MCP endpoint is missing AI Guide tools")
            result = rpc("tools/call", {"name": "editor_state", "arguments": {}})
            if result.get("isError"):
                raise InstallError(str(result))
            state = json.loads(result["content"][0]["text"])
            if Path(state["project_path"]).resolve() != project:
                raise InstallError("MCP endpoint belongs to a different project")
            return {"server": info["serverInfo"], "tool_count": len(tools), "project": str(project)}
        finally:
            if process.poll() is None:
                try:
                    rpc(
                        "tools/call",
                        {
                            "name": "editor_exec",
                            "arguments": {"code": "EditorInterface.get_base_control().get_tree().quit()\nreturn true"},
                        },
                    )
                except (OSError, urllib.error.URLError, InstallError):
                    pass
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.terminate()
                    process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("check", "install", "build", "verify"))
    parser.add_argument("--project", type=Path, required=True, help="Target directory containing project.godot")
    parser.add_argument(
        "--create-csharp-host", action="store_true", help="Create a minimal C# host for a GDScript-only project"
    )
    parser.add_argument("--csproj", type=Path, help="Select a C# project if the target contains several")
    parser.add_argument("--no-build", action="store_true", help="Copy and enable only; build before opening the editor")
    parser.add_argument("--dry-run", action="store_true", help="Inspect the installation without writing files")
    parser.add_argument(
        "--editor", type=Path, help="Godot 4.8 .NET executable; install also verifies startup when supplied"
    )
    args = parser.parse_args()
    project = args.project.expanduser().resolve()
    try:
        if args.action == "check" or args.dry_run:
            result = prepare(project, args.create_csharp_host, args.csproj)[3]
        elif args.action == "install":
            result = install(
                project, create=args.create_csharp_host, csproj=args.csproj, no_build=args.no_build, editor=args.editor
            )
        elif args.action == "build":
            report = prepare(project, False, args.csproj)[3]
            if report["status"] != "installed":
                raise InstallError("Run install first")
            build(Path(report["csproj"]))
            result = {"status": "built", "project": str(project)}
        else:
            if not args.editor:
                raise InstallError("verify requires --editor pointing to a Godot .NET executable")
            if prepare(project, False, args.csproj)[3]["status"] != "installed":
                raise InstallError("Run install before verifying")
            result = verify(project, args.editor)
        print(json.dumps(result, indent=2, ensure_ascii=False))
    except (InstallError, OSError, ValueError, ET.ParseError) as error:
        print(f"AI Guide: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
