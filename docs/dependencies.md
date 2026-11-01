# Dependencies

What each part needs, and what happens without it. "Compiles without it" was tested by removing
the package's assemblies and compiling the helpers. Tools outside that test were read from their
imports, or are noted as such.

## Everything

- Unity at the version the project records (these were built on **2022.3.22f1**), with **Windows Build
  Support (Mono)** for anything that builds for VRChat PC.
- **[uv](https://docs.astral.sh/uv/) for `tools/`.** Run `uv sync` once in this repo, then
  `uv run tools/<tool>.py` from its root. uv installs a suitable Python and the packages in
  `pyproject.toml` (NumPy, Pillow, SciPy, Matplotlib), pinned by `uv.lock`, so there's nothing else to install. From
  another folder: `uv run --project <repo> <repo>/tools/<tool>.py`.
- **[ALCOM](https://vrc-get.anatawa12.com/en/alcom/) or [VCC](https://vcc.docs.vrchat.com/) for Unity
  packages.** Add the packages below to your project with one of them, after adding the feeds in
  [setup.md](setup.md). Don't edit the manifests by hand. Use ALCOM on Linux, where VCC only has a limited
  CLI.

## Unity packages

All editor scripts in `unity/avatar/` compile together, so **a missing compile-time dependency breaks
all of them**, not just the one helper that uses it.

| Package | Needed for | Compile-time? | Without it |
| --- | --- | --- | --- |
| VRChat Avatars SDK | All of `unity/avatar/` | **Yes** | Nothing compiles; the installer refuses |
| Modular Avatar **1.14 or later** | `assemble`, `add-toggle`, `AvatarAgentSaved`, optimizer mesh cutters and more | **Yes** | Nothing compiles; the installer refuses, but doesn't check the version |
| AAO: Avatar Optimizer | `optimize_avatar.py` | No | `optimize_avatar.py` refuses to start |
| Meshia Mesh Simplification | `optimize_avatar.py` | No | `optimize_avatar.py` refuses to start |
| VRCFury | `add-fury-slider`; `AvatarAgentSaved` also reads its toggles | No | Those features fail or find nothing. Other helpers run |
| lilToon | Nothing in `unity/avatar/`. The pipeline's AAO Merge Material benefits, and the horn's eye glow animates it | No | Only those uses are affected |
| d4rkAvatarOptimizer | Nothing. The pipeline just avoids conflicting with an existing one | No | Nothing changes |
| VRChat Worlds SDK (includes UdonSharp and ClientSim) | `unity/world/` | **Yes** | The installer refuses |

Modular Avatar's floor comes from `AvatarAgentOptimize.cs` using its Mesh Cutter component directly,
and the changelog adds that in 1.14.0. **That file is why a project on an older MA can't compile the
helpers.** Update MA, or remove `AvatarAgentOptimize.cs` and `AvatarAgentOptimizeAudit.cs` from the
installed folder if you don't want the pipeline. NDMF comes in with Modular Avatar. Meshia pulls in a
localization package from the [anatawa12 feed](https://vpm.anatawa12.com/vpm.json), so add that feed
too.

Feed URLs are in [setup.md](setup.md). The helpers were run against these, the latest on 1 November 2026
(see the README for what exactly ran):

| Package | Version |
| --- | --- |
| VRChat Avatars SDK | 3.10.6 |
| Modular Avatar | 1.18.7 |
| NDMF | 1.14.8 |
| AAO: Avatar Optimizer | 1.9.21 |
| Meshia Mesh Simplification | 3.2.0 |
| VRCFury | 1.1436.0 |
| lilToon | 2.4.1 |
| d4rkAvatarOptimizer | 4.7.0 |
| VRChat Worlds SDK | 3.10.5 (compiled only; not the latest) |

Some of this reaches into internals that may change between versions: the upload helpers call
SDK-internal methods by reflection, and the optimizer finds AAO and Meshia types by name. If a new
version breaks one, you'll see `Type not found` or `MissingMethodException`. Going back to the versions
above is the quickest way to tell.

## The optimizer has the most dependencies

`tools/optimize_avatar.py` needs all of:

- **Unity packages:** Avatars SDK, Modular Avatar, AAO, Meshia.
- **Python:** NumPy and Pillow, even with `--no-render`, since the driver imports `compare_optimized.py`.
  `uv run` provides them.
- **Graphics:** AAO refuses `-nographics`, so it needs a display, even though no window opens.
- **Disk and time:** about 10 to 12 minutes on the tested avatar, plus its renders and reports.
- **The project closed in Unity** throughout.
- **Installed helpers,** including `VRUnityFaceCheck.shader` for the comparison renders.

It checks the packages and the Python modules before starting. The rest shows up as a failure in a stage.

The optional base-mesh step adds more:

| Part | Needs |
| --- | --- |
| `tools/blender/loop_reduce.py` | **Blender** run headless (developed on 4.3), plus both a game FBX and the authored quad FBX with matching vertex order |
| `tools/audit_base_mesh_uv.py` | NumPy and SciPy, from `uv run` |
| `AvatarAgentBaseMesh` | Only the Avatars SDK helpers |

## Tools

| Tool | Needs besides `uv` |
| --- | --- |
| `avatar_agent.py`, `install_helpers.py`, `inspect_avatar_package.py` | Nothing |
| `init_snapshots.py`, and the snapshot hook | `git`. The hook also needs `/bin/sh`, so Linux or macOS |
| `compare_optimized.py`, `audit_base_mesh_uv.py`, `plot_avatar_sections.py` | Nothing beyond `uv run` |
| `copy_unity_project.py` | `findmnt` (Linux) |
| `vrchat_local_test.py` | Linux, Steam, VRChat under Proton, `pgrep` |
| `unity-watch.sh` | Linux shell tools: `bash`, `ps`, `pkill`, `stat` |

Graphical batch runs on Linux read display settings through `systemctl --user`, if available.

## Accounts

- **Unity licence:** log in through Hub.
- **VRChat SDK login:** log in once in a normal editor's SDK panel. Headless uploads reuse it. You can
  only upload to slots your account owns.
- **First upload:** by hand in the GUI, for each new avatar or world, to accept the SDK's agreement.

## fxc2 (only for very heavy shaders)

A second editor copy with FXC2's compiler, built with the toolchain in its
[`docs/unity-linux-loader.md`](https://github.com/mommysgoodpuppy/FXC2/blob/linux-unity-loader/docs/unity-linux-loader.md).
See [fxc2.md](fxc2.md).

## Examples

- `examples/horn-glow`: Avatars SDK and **VRCFury at compile time** (`com.vrcfury.api`). Modular
  Avatar isn't needed. lilToon is only for the eye glow.
- `examples/horn-collar`: Avatars SDK and `AvatarMeshGeometry.cs` from `unity/avatar/`.
