# Notes

Maintainer notes for this repo. User-facing docs are in `docs/`.

## Where things came from

Copied on 2026-11-01. The originals stay authoritative for their own projects and were not changed.

| Here | From | Changed |
| --- | --- | --- |
| `unity/avatar/AvatarAgent*.cs`, `AvatarMeshGeometry.cs`, `VRUnityMigrationCheck.cs`, shaders | `../vrunity/scripts` | Avatar names removed from code and comments. `refine-outfit` and `audit-outfit` dropped. Bench: `-benchParam` replaces one avatar's parameter; first-person scenario uses the view position. Optimize: `bodyRenderer` config (`--body-renderer`). Migration check: `-migrationScene`. Snapshots moved to `~/UnitySnapshots`. Reports moved to `~/UnityAgentReports` |
| `unity/avatar/AvatarAgentCrossSection.cs`, `EditorSectionClip.shader`, `tools/plot_avatar_sections.py` | `../vrunity/scripts` | Any renderers instead of hat/head/hair; any anchor bone; headless `Export`; crossings for any pair. Outfit-only live commands dropped |
| `unity/avatar/AvatarAgentFirstPerson.cs` | `../vrunity/scripts` | New file: only the head-chop render survives, not one avatar's checks and fixes |
| `unity/avatar/AgentNormalDebug.shader` | A debug shader inside the maintainer's avatar project | Renamed |
| `unity/avatar/AvatarAgentShaderCompile.cs` | `../vrunity/scripts` | Rewritten: shaders come from `-shaders`; the hash proof for one patched asset is gone |
| `tools/*.py`, `tools/blender/loop_reduce.py` | `../vrunity/scripts` | Editor lookup moved to `unity_env.py`, with Windows and macOS paths |
| `tools/unity-watch.sh` | `../ShaderEmu/unity/linux-build` | Unchanged |
| `unity/world/AgentWorldBuild.cs` | `../vrmodem/unity/Editor/WorldExport.cs`, `../ShaderEmu/unity/linux-build/LinuxBuild.cs` | New, combining their generic parts |
| `tools/vrchat_local_test.py` | `../vrmodem/server/run_probe.ts` | New, Python, launch and log-following only |
| `examples/horn-glow/` | The maintainer's HornGlow prototype project, `Assets/HornGlow` (Editor, Shaders) | Comments only. Generated assets, prefab and test scene left out: the builder makes them |
| `examples/horn-collar/` | `../vrunity/scripts/AvatarAgentHornCollar.cs` | Comments, scene and folder paths, avatar lookup |
| `examples/README.md` | `../vrunity/HORNGLOW.md` | Rewritten as lessons |
| `tools/install_helpers.py`, `tools/init_snapshots.py`, `tools/unity_env.py` | | New |
| `docs/*`, `templates/AGENTS.md` | Those repos' docs and `AGENTS.md` | Rewritten for general use |

Left behind on purpose: helpers specific to one avatar, outfit or interaction, including the eye-glow setup and its mask, which derive from a paid avatar's texture; patches to paid assets;
vrmodem's protocol and ShaderEmu's emulator, which are projects of their own.

## Open

- Open-editor workflow on these copies: `scene-view`, live recipes, the Cross Section window.
- Optimizer on an avatar that exercises Meshia and PhysBone merging. The Robot sample's one mesh counts as its
  face, so it's kept, and it has too few PhysBones to merge.
- `AgentWorldBuild.Export` in Unity, and against the latest Worlds SDK.
- A real upload, after the owner's first GUI upload, on these copies.
- Try it on Windows. Decide how far to support it once a friend does.
