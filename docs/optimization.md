# Optimized avatar variants

Make a Poor or Medium copy of an avatar in one command. All of the optimization is build-time NDMF
components, so the variant scene's meshes and your source scene stay as they were.

Tool versions and the rank limits were checked in October 2026. Check them again: see VRChat's
[PC rank table](https://creators.vrchat.com/avatars/avatar-performance-ranking-system/).

## The pipeline

```sh
uv run tools/optimize_avatar.py /path/to/Project Assets/VRUnityGenerated/MyAvatar.unity \
    --fixed-on "Jacket=Outfit/Jacket"     # big overlaid clothing, kept always on
# Options: --rank Poor|Medium, --keep-full PATH, --base-mesh RENDERER=ASSET, --spatial-cuts FILE,
#          --slot-from SCENE, --no-render, --resume REPORT_DIR --from STAGE, and see --help
```

Needs AAO and Meshia ([full list](dependencies.md#the-optimizer-has-the-most-dependencies)), and Unity closed. Each stage is a separate headless run
(about 20 to 30 s to start; about 10 to 12 minutes in all on the tested avatar).

Output: a dated scene at `Assets/VRUnityGenerated/Optimized/<scene>-<Rank>-<date>.unity`, and reports,
renders and contact sheets under `~/UnityAgentReports/optimize/<label>/`. Read `summary.json` first.
Nothing is uploaded, and the variant's blueprint ID is cleared, so its first upload needs a new slot.

| Stage | What it does |
| --- | --- |
| variant | Copy the scene, rename, clear the blueprint ID. Fix `--fixed-on` clothing on. Add root MA Mesh Settings (so merged meshes light alike) and AAO Trace and Optimize |
| analyze | Optional hidden-surface cuts, **off unless you pass `--automatic-cuts`** |
| probe | On a built copy, count triangles per renderer, PhysBones, animation bindings, parameters |
| autophysbones | If over the limit, AAO Merge PhysBone for sibling chains with identical settings |
| decimate | Meshia cascading simplifier at a uniform ratio, keeping the face and borders. Rescaled and rebuilt if still over |
| stats | SDK stats of the built variant and source |
| render | Source and variant in 15 poses and 16 views. `compare_optimized.py` flags new back faces, lost silhouette and bad normals |

`summary.json` should show no new unresolved animation bindings, and only the fixed-on toggles'
parameters removed.

**Automatic cuts are opt-in for a reason.** On the avatar this came from they deleted things that
mattered, and hand-placed cutters did better. `--spatial-cuts` takes a JSON list of MA Mesh Cutter
boxes. `AvatarAgentOptimizeAudit` exports geometry to help place them.

## Choosing tools

| Tool | Role |
| --- | --- |
| **Meshia Mesh Simplification** | Decimation, as an NDMF pass with an avatar-wide target and per-renderer entries. Source meshes aren't changed |
| **AAO: Avatar Optimizer** | Hidden-mesh cuts (box, mask, blendshape, UV tile), Trace and Optimize, merging of skinned meshes, PhysBones and materials, Max Texture Size |
| **MA Shape Changer, Delete mode** | Deletes vertices a body blendshape moves while a garment is on. The toggle-aware way to cut, if the body has suitable shapes |
| **d4rkAvatarOptimizer** | Overlaps heavily with AAO. Its main extra is merging separately toggled meshes |
| TexTransTool | Non-destructive atlasing, if AAO Merge Material isn't enough for Medium. Not tried here |
| Thry's Avatar Performance Tools | VRAM breakdown including meshes. Not tried here |
| lilNDMFMeshSimplifier | Archived in April 2026; its author points to Meshia |

### AAO or d4rk

| | AAO | d4rk |
| --- | --- | --- |
| Build hook | NDMF Optimizing phase, with MA and Meshia | VRC preprocess callback, after NDMF and VRCFury |
| Stance | Trace and Optimize must not change appearance or behaviour; the rest is opt-in | Standard preset keeps behaviour; shader toggles and Aggressive trade correctness for counts |
| Hidden-mesh cuts | Yes | No |
| Skinned mesh merge | Automatic where safe, plus a manual component | Same-animation merge, plus separately toggled meshes via NaNimation or shader toggles |
| Material merge | Manual packing with lilToon support | Identical slots; rewriting shaders refuses lilToon |
| PhysBones | Merges non-grabbable ones | Disable-when-unused only |
| Quest | Usable | Not supported (custom shaders) |

**Recommendation:** AAO as the optimizer. Reach for d4rk only when just over a limit, and then use its
mesh merge with NaNimation rather than shader toggles. Both can technically run, with d4rk last, but
neither documents combined use, so treat it as untested.

If an avatar already has a d4rk component you don't want, turn off **Apply On Upload** rather than
removing it: its global "always optimize" setting could add a new one at build time. Likewise
Meshia scans inactive cascade components and doesn't check the enabled flag, so remove it rather than
deactivating its object.

### Build order

Read from the installed sources (Meshia 3.2.0, AAO 1.9.20): MA and VRCFury, then Meshia decimation,
then AAO's cuts and merges, then d4rk if enabled. MA Shape Changer runs before all of them.

So AAO's mesh cuts run **after** decimation, and part of the decimation budget goes on triangles that
are later deleted. Raise a cut renderer's Meshia target by its cut fraction, then check the built
count.

## Going further: reducing a base mesh in Blender

Generic decimation can spoil a face or hands. `tools/blender/loop_reduce.py` removes edge loops from
the body, checking against the higher-resolution source (`--game`, `--quad`), and
`AvatarAgentBaseMesh.Build` makes a Unity mesh from the result, copying UVs, weights, bindposes and
blendshapes. `tools/audit_base_mesh_uv.py` compares the UVs independently. Pass the result to the
pipeline with `--base-mesh`. This is advanced, and was built around one avatar's mesh: read the usage
at the top of each file, and expect to tune it.

## What's easy to get wrong in the code

- `AvatarPerformanceStats.Initialize()` must run before calculating stats in batch mode.
- AAO renames merged objects in built copies (`Outfit$Jacket$22`). Map them back through the source
  path.
- Materials created at runtime render as error magenta in this headless path; save them as assets.
- An enabled Animator resets the bones when a preview scene renders, so every render comes out as a
  T-pose. Cache the bones and disable it.
- `HumanPoseHandler` poses came out differently on AAO-built and source copies, so use direct bone
  rotations and check they moved the skeleton.
- Merging PhysBones can change how things can be grabbed or posed. Test in the client.
