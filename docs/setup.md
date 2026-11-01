# Setup

Written from a Linux setup (Arch-based, KDE, AMD GPU). On Windows, use VCC or ALCOM and Unity Hub
as usual, and skip the Linux parts. Check VRChat's
[current Unity version](https://creators.vrchat.com/sdk/upgrade/current-unity-version/) before
installing; these notes used 2022.3.22f1.

## Toolchain

- **Unity Hub and the matching editor.** Use the version the project records. Add the **Windows
  Build Support (Mono)** module from the Hub GUI: PC VRChat content builds for Windows, and a Linux
  editor doesn't include it. Hub's `--headless` CLI hands off to a running Hub window and can hang.
- **[ALCOM](https://vrc-get.anatawa12.com/en/alcom/)** for VPM projects and packages: an unofficial
  open-source VCC alternative with native Linux builds. `vrc-get` is its CLI. Official VCC only
  supports its desktop app on Windows.
- **[uv](https://docs.astral.sh/uv/)** runs the Python tools. `uv sync` in this repo sets up Python and its
  packages; use `uv run tools/...` from then on.
- **Blender**, matching the version your `.blend` files were saved with if you can. Unity needs
  `blender` on its PATH to import `.blend` files. Distro packages can be far newer than your add-ons
  support.
- **`libxml2-legacy`** or your distro's equivalent, on rolling distros: the editor needs
  `libxml2.so.2`.

Log in once through Hub for the licence, and once in the SDK panel of a normal editor window. Batch
uploads reuse that saved SDK login.

## Linux: make the editor draw correctly

Launch with **`-force-vulkan`**. On one tested AMD system the default OpenGL editor showed inverted
looking surfaces and flooded the log with `State comes from an incompatible keyword space`. lilToon
materials exceeded OpenGL's 32 texture limit. Vulkan fixed both with no material or mesh changes.

**Opening a project from ALCOM uses OpenGL until you set this,** even though the tools here pass the flag
themselves. Either set it for one project (its page, the dropdown next to **Open Unity**, **Change launch
options**, **Customize command line arguments**, add `-force-vulkan`), or set ALCOM's default Unity arguments in
its Settings, for every project. Keep any arguments already there. To confirm, search `~/.config/unity3d/Editor.log`
for `Vulkan`; `Creating OpenGL` means the flag didn't apply.

```sh
~/Unity/Hub/Editor/<version>/Editor/Unity -projectPath /path/to/Project -force-vulkan
```

## Package feeds

Add what your project uses in ALCOM or VCC. ALCOM matches installed packages by name, so you don't
need to rewrite the project's manifests. Assets imported directly under `Assets/` don't become VPM
packages by adding a feed.

| Feed | Provides |
| --- | --- |
| [VRCFury](https://vcc.vrcfury.com/) | VRCFury |
| [bd_/nadena](https://vpm.nadena.dev/vpm.json) | Modular Avatar, NDMF |
| [lilxyzw](https://lilxyzw.github.io/vpm-repos/vpm.json) | lilToon |
| [anatawa12](https://vpm.anatawa12.com/vpm.json) | AAO: Avatar Optimizer |
| [Ram.Type-0](https://ramtype0.github.io/VpmRepository/index.json) | Meshia Mesh Simplification |
| [d4rkpl4y3r](https://d4rkc0d3r.github.io/vpm-repos/main.json) | d4rkAvatarOptimizer |
| [Hai](https://hai-vr.github.io/vpm-listing/index.json) | Lightbox Viewer |

See [dependencies.md](dependencies.md) for which helpers need which packages and versions.

## Moving a project from Windows

Keep the original untouched. On Linux, mount the Windows volume read-only and check that it really
is: `findmnt -T /path`. `tools/copy_unity_project.py` refuses a writable source or an existing
destination, keeps bytes and timestamps, writes hash manifests, and leaves out only root `Library`
and `Logs`.

Then:

1. Identify project roots by their `Assets`, `Packages` and `ProjectSettings`. Before copying, look
   for symlinks, local packages, Git dependencies and files outside the project.
2. Register the feeds, then open the copy with the recorded Unity version. The first import takes a
   while.
3. Copy `unity/avatar/VRUnityMigrationCheck.cs` into an Editor folder and run it headless. It
   reports missing scripts, meshes and materials, without saving, building or uploading:

   ```sh
   Unity -batchmode -quit -projectPath <proj> -executeMethod VRUnityMigrationCheck.Run \
     -migrationScene Assets/Main.unity -migrationReport /path/report.json -logFile <log>
   ```

4. Shader `#include`s that worked on Windows can fail on Linux because of filename case.
   `tools/fix_shader_include_case.py` previews by default; `--apply` changes only unambiguous
   spellings. Check its header, which targets a legacy Poiyomi tree.
5. Check materials, menus, parameters and tracking features. A copied project isn't a working
   project until you have checked this, and a clean diagnostic doesn't prove it looks right.

## Save snapshots

Unity crashes, and this recovers your scenes and settings after one:

```sh
uv run tools/init_snapshots.py /path/to/Project
```

This makes `~/UnitySnapshots/<project>.git`, separate from any repository the project has. With the
avatar helpers installed, each save commits Unity's text data in the background. Textures, models
and audio are left out, so it isn't a full backup. Recover with
`git --git-dir=$HOME/UnitySnapshots/<project>.git log`, then `diff` or `restore`. If it stops
working, see `Library/VRUnityAgent/Snapshot/last.log`. It uses `/bin/sh`, so it is Linux/macOS only.
