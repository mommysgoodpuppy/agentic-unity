"""Finding the Unity editor and the environment it needs. Shared by the other tools."""
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path


def installed_packages(project):
    """Package names from the VPM and Unity manifests and from embedded package folders."""
    names = set()
    packages = Path(project) / "Packages"
    for manifest, key in (("vpm-manifest.json", "locked"), ("manifest.json", "dependencies")):
        path = packages / manifest
        if path.exists():
            names.update(json.loads(path.read_text()).get(key, {}))
    if packages.is_dir():
        names.update(p.name for p in packages.iterdir() if p.is_dir())
    return names


def project_version(project):
    text = (Path(project) / "ProjectSettings" / "ProjectVersion.txt").read_text()
    return text.split("m_EditorVersion:")[1].split()[0]


def editor_candidates(version):
    if sys.platform == "win32":
        return [Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Unity/Hub/Editor" / version / "Editor/Unity.exe"]
    if sys.platform == "darwin":
        return [Path("/Applications/Unity/Hub/Editor") / version / "Unity.app/Contents/MacOS/Unity"]
    return [Path.home() / "Unity/Hub/Editor" / version / "Editor/Unity"]


def find_editor(project, override=None):
    """The editor for the project's recorded version: --editor, then $UNITY_EDITOR, then Hub's folder."""
    chosen = override or os.environ.get("UNITY_EDITOR")
    if chosen:
        return Path(chosen)
    version = project_version(project)
    for candidate in editor_candidates(version):
        if candidate.exists():
            return candidate
    raise SystemExit(f"Unity {version} (the project's version) not found; looked in "
                     f"{[str(c) for c in editor_candidates(version)]}. Install it or set UNITY_EDITOR. "
                     "Do not open the project with another version: that can upgrade it.")


def graphics_args():
    """Linux's default OpenGL editor misdraws avatar shaders (docs/gotchas.md); elsewhere keep Unity's default."""
    return ["-force-vulkan"] if sys.platform.startswith("linux") else []


def display_env():
    """An agent's shell often has no display variables; graphical batch runs need them on Linux."""
    env = dict(os.environ)
    if sys.platform.startswith("linux") and shutil.which("systemctl"):
        out = subprocess.run(["systemctl", "--user", "show-environment"], capture_output=True, text=True).stdout
        for line in out.splitlines():
            key, _, value = line.partition("=")
            if key in ("DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_RUNTIME_DIR"):
                env[key] = value
    return env


def editor_open(project):
    """Whether a running Unity editor has this project open. Other projects' editors don't matter."""
    try:
        listing = subprocess.run(["pgrep", "-a", "-x", "Unity"], capture_output=True, text=True).stdout
    except FileNotFoundError:
        return False  # No pgrep here; Unity itself refuses a project that's open.
    return str(Path(project).resolve()) in listing
