# VRChat Unity work on Linux

Doing this natively on Linux works, including avatar uploads and world publishing, but it has edges that Windows
users never see. This collects them, from real sessions on one setup: Arch-based CachyOS, KDE, AMD GPU, Unity
2022.3.22f1, in late 2026. **Other distros, NVIDIA and Intel GPUs haven't been tried, and nothing was recorded about
Wayland versus X11 specifically.** If something here is wrong for you, that's worth a note or a PR.

Quick checklist:

| You need | Why |
| --- | --- |
| [ALCOM](https://vrc-get.anatawa12.com/en/alcom/), not VCC | VCC's desktop app is Windows-only |
| Unity Hub, with the project's exact editor version | Use the version in `ProjectSettings/ProjectVersion.txt` |
| **Windows Build Support (Mono)** module | VRChat PC content builds for Windows. Add it in Hub's GUI: Installs, your version, Add modules |
| `-force-vulkan` when opening the editor | OpenGL draws avatars wrongly: [below](#the-editor-looks-wrong) |
| `libxml2-legacy` or your distro's equivalent | The editor needs `libxml2.so.2`, which rolling distros have moved on from |
| Blender, on Unity's `PATH` | Otherwise `.blend` files in a project fail to import |
| A Proton build with working video, such as `proton-rtsp` | For VRChat itself: [below](#running-vrchat) |

You don't need Wine or Proton for Unity or ALCOM. Everything here runs natively, apart from VRChat.

## The editor looks wrong

**Inverted-looking surfaces, or a log flooding with `State comes from an incompatible keyword space`:** you're on
OpenGL. It's the default on Linux, and opening a project from ALCOM uses it until told otherwise. Shaders like
lilToon need more textures than OpenGL's limit of 32. Set `-force-vulkan`: for one project, on its page in ALCOM, the
dropdown next to **Open Unity**, **Change launch options**, **Customize command line arguments**. ALCOM's Settings
also has default Unity arguments for all projects. Keep any arguments already there.

To check: `grep -m1 -E 'Vulkan API|Creating OpenGL' ~/.config/unity3d/Editor.log`

## Unity won't start, or won't start again

- **`libxml2.so.2` missing:** see the checklist.
- **"Another Unity instance" with no editor open.** Closing an editor can leave a shader compiler, a silent Unity
  Hub and its licensing client holding `Temp/UnityLockfile`, with a stale `Library/EditorInstance.json`. Stop those
  processes, check that no editor has the project open, then remove those two files. Never remove a lock from a
  project that's in use.
- **Crashes.** The editor does crash natively, around Play or script import, and a relaunch often works. One crash
  on Play was inside Vulkan's window setup (`VKWindow::Reshape`) with no known fix. So save often, and use
  [save snapshots](setup.md#save-snapshots). Crash files (`mono_crash.*`) can pile up in the project folder.
- **Disk.** Set `ulimit -c 0` in shells that run Unity: a crash can otherwise leave a 0.5 GB core dump, and take far
  more while writing it. One log flood reached 6 GB (`~/.config/unity3d/Editor.log`).
- **Windows Build Support won't install from the command line.** Hub's `--headless` hands off to a running Hub
  window and can hang. Use its GUI.

## ALCOM and packages

- **ALCOM has no command line:** even `alcom --help` opens the window. Its sibling
  [`vrc-get`](https://vrc-get.anatawa12.com/) is the CLI, and `vrc-get resolve` restores a project's packages.
- **Don't edit ALCOM's files while it's running.** It owns
  `~/.local/share/VRChatCreatorCompanion/` and rewrites it. Add projects with **Add Existing Project**.
- Feeds carry over by URL: see the table in [setup.md](setup.md#package-feeds).

## Bringing a project over from Windows

- **Linux filenames are case-sensitive.** A `#include` that worked on Windows can fail here. See
  `tools/fix_shader_include_case.py`.
- **Copy, don't work on the Windows drive.** Mount it read-only and check with `findmnt -T <path>`.
  `tools/copy_unity_project.py` does a verified copy. NTFS's executable bits mean nothing, so permissions need
  resetting. See [setup.md](setup.md#moving-a-project-from-windows).
- **Windows-only pieces in assets.** Editor tools that ship a native `.dll` won't load. Some vendors include a Linux
  build as well (look for `linux-x64`), so check before reaching for Wine. An edition may also lack a feature on Linux,
  so check the tool's own output, not just that its menu appears.
- **Tools that call the Windows shader compiler** may misbehave or be very slow: see below.

## Shaders: what's different

Unity on Linux builds Windows shaders by running Microsoft's compiler, `D3DCompiler_47.dll`, through its own built-in
Windows DLL loader. Most of the time you won't notice. Then:

- **Heavy shaders.** Unity kills a compile after 10 minutes and **carries on, producing a build without that
  shader**. If a shader takes that long, or FXC refuses it, see [fxc2.md](fxc2.md). It's rare, and you'll probably
  never need it. Compute-heavy shaders are the likely cases.
- **What you see isn't what VRChat compiles.** The editor makes its Vulkan and OpenGL shaders by translating the D3D
  bytecode, and that can fail on shaders that are fine in VRChat (`wrong operand types`, "double precision"). So an
  error in the editor isn't always a real problem. Check for the Windows target with `AvatarAgentShaderCompile`.
- **The shader cache doesn't record which compiler made an entry,** so broken entries survive a change of editor.
  Back up and then move `Library/ShaderCache*` if things stay broken after a fix. It rebuilds, slowly.
- **Some instructions break under the layers that run Direct3D on Linux.** One shader's `umul` turned into an invalid
  SPIR-V module for Linux players. Friends on Proton are worth testing with.

## Testing in VRChat

- **The SDK's Build & Test button doesn't work as is:** it starts a Windows `VRChat.exe` directly. Use
  `tools/vrchat_local_test.py`, which launches through Steam with the same arguments, translating the path to Wine's
  `Z:` drive. It's written for worlds. It should apply to avatars with changes, but that hasn't been tried.
- **Or upload a private version and test that.** Publishing from the Linux editor works normally, through the SDK
  panel or headless. The first upload has to be through the panel.
- **The Linux editor can't play these videos:** video players need testing in the client.
- **Gesture Manager:** check that its emulator is on before deciding something's broken in Play mode.

## Running VRChat

Beyond this repo's scope, but what affects testing. [VRonLinux](https://vronlinux.org/docs/games/vrchat/) has much
more.

- **Use a Proton build with working video:** in practice, people on Linux use `proton-rtsp`, installed with
  ProtonUp-Qt and forced as VRChat's compatibility tool. Video players don't work reliably under stock Proton.
- **Video goes through Wine's Media Foundation,** so behaviour may differ from Windows.
- **Logs and settings are in the Proton prefix:**
  `~/.local/share/Steam/steamapps/compatdata/438100/pfx/drive_c/users/steamuser/AppData/LocalLow/VRChat/VRChat`.
- **OSC:** VRChat caches each avatar's OSC config in that prefix, and a stale one hides new parameters. Move it aside
  to regenerate it. If UDP 9001 is taken (eye-tracking software, for one), use a tool's OSCQuery mode.
  [OSCLeash](https://github.com/ZenithVal/OSCLeash) has a Linux AppImage, currently as a prerelease.
- **Frame times:** MangoHud works under Proton. Agents can compare avatars with `AvatarAgentBench`, but only on one
  machine, and not as the client's numbers.

## For agents

- The shell may have no display variables. Headless Unity with graphics needs them, from
  `systemctl --user show-environment`. The tools here handle it.
- Use exact process names: `pgrep -x Unity`, `pkill -x VRChat.exe`. With `-f`, you match your own shell.
- Keep long Unity runs in the background.
