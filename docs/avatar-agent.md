# Agent-driven avatar work

The core interface is a **JSON recipe run by an editor script**. It works headless, and an optional
file queue runs the same code in an open editor, so you can direct changes and watch them appear.
Install the scripts with `tools/install_helpers.py PROJECT --kind avatar`.

Generated work goes under `Assets/VRUnityGenerated/`. Reports and the queue go in
`Library/VRUnityAgent/`. Live edits, checkpoints and assembled outputs refuse scenes outside the
generated folder, so your original scene stays protected.

## Two workflows

- **Unity open, the user directing:** recipes without `--batch`. Changes appear in the Scene view, and the user
  can undo, inspect and save.
- **Headless:** with `--batch` and the `-executeMethod` tools, with Unity closed on that project. Suits a quick
  "fix X and reupload" while the user is in VRChat.

Ask which one they want if it isn't clear.

## Running a recipe

```sh
# Headless: Unity starts, runs the recipe and exits. Close the project's editor first.
uv run tools/avatar_agent.py /path/to/Project recipe.json --batch

# Live: goes to the editor you have open. Unity must have imported the helpers (focus it if
# nothing happens) and not be in play mode.
uv run tools/avatar_agent.py /path/to/Project recipe.json
```

The launcher picks the project's recorded editor version; `--editor` or `$UNITY_EDITOR` overrides it.
Success means both a clean editor exit and an `ok` report. A live request that times out unclaimed is
withdrawn. One that was claimed is never replayed, so read its result before retrying a change.

For interactive work, open a generated draft and frame it:

```sh
Unity -projectPath /path/to/Project -force-vulkan -executeMethod AvatarAgent.OpenLive \
  -avatarScene Assets/VRUnityGenerated/Draft.unity
```

## Commands

Batch recipes include `scene`. `sourceId` (a GlobalObjectId from `list` or `inspect`) picks the
avatar; without it, the single active avatar is used, and more than one is an error.

| Command | Does |
| --- | --- |
| `list` | Scene avatars and their IDs |
| `inspect` | Hierarchy, components, transforms, skinned meshes, material slots, blendshapes |
| `import` | Import a hash-checked package after a collision and code preflight, then verify every GUID |
| `assemble` | Clone the base into a new scene, attach a Modular Avatar outfit prefab, check nothing else changed |
| `preview` | Front, side and back PNGs in an isolated scene |
| `closeup` | Renders of part of the avatar from several angles |
| `scene-view` | Live: the Scene view camera's pose, the selection, and a render of that view (`previewDirectory`). Moves nothing |
| `open`, `frame` | Live: open a generated draft (refuses to close a dirty scene); frame it in the Scene view |
| `edit` | Validated visibility, transform, blendshape or material edits as one Undo step |
| `undo`, `redo` | Live: Unity's shared Undo stack, which includes your own manual edits |
| `save` | Save the open generated draft |
| `checkpoint` | Save a copy as a new scene, without touching the open file |
| `refresh` | Refresh the asset database |
| `audit-meshes`, `swap-meshes` | Inventory real mesh subassets and sources; swap meshes after rig, bindpose and material checks |
| `inspect-accessory`, `attach-accessory`, `export-accessory` | Lift a skinned accessory and its bones from another outfit into a reusable prefab |
| `fix-accessory-bounds`, `audit-accessory-bounds` | Fix or check an accessory's culling bounds |
| `attach-prefab`, `place-prefab` | Instantiate a prefab under a guarded anchor |
| `add-toggle`, `add-fury-slider` | Add a Modular Avatar toggle; add a VRCFury blendshape slider |
| `upload` | Live: build and upload through the SDK panel. Needs the owner's say-so |

`edit` takes `edits`, each with `path`, `expectedName`, and `kind`:

| Kind | Fields |
| --- | --- |
| `active` | `active` (the avatar root can't be disabled) |
| `scale`, `position`, `rotation` | `value`: local x/y/z (rotation in Euler degrees) |
| `blendshape` | `shape`, `weight` |
| `material` | `slot`, `material` (an existing asset path) |
| `remove-component` | See `AvatarAgent.cs` |

Paths are sibling-index paths from `inspect`, so duplicate names are unambiguous. They are tied to
that hierarchy, so inspect again after restructuring. `expectedName` must match. All edits are checked
before any apply, and a failed batch reverts. Edits mark the draft dirty and don't save it.

```json
{"command": "inspect", "scene": "Assets/Scenes/Main.unity"}
```

```json
{
  "command": "assemble",
  "scene": "Assets/Scenes/Main.unity",
  "outputScene": "Assets/VRUnityGenerated/NewOutfitDraft.unity",
  "outfitPrefab": "Assets/Vendor/Outfit/[MA] Outfit.prefab",
  "matchOutfitBoneScale": true,
  "disablePaths": []
}
```

`assemble` clones the whole customized avatar, then checks transforms, blendshape weights and
materials against the original and refuses missing scripts, meshes or materials.
`matchOutfitBoneScale` copies the base's bone scales onto the outfit. It doesn't guess new
proportions; positions, body masks and conflicting clothes are further explicit steps.

`preview` and `closeup` show authoring state: they do **not** run the SDK's build, so MA and VRCFury
haven't been applied. A good preview isn't a good build.

## Taking in a package

```sh
uv run tools/inspect_avatar_package.py Outfit.zip /path/to/Project \
  --report /private/outfit-manifest.json \
  --stage /private/outfit-isolated.unitypackage \
  --remap-guid-collisions
```

It reads the archive without extracting arbitrary paths; rejects links, traversal, duplicates and
excessive sizes; and reports existing paths, GUID collisions and executable assets. Staging refuses
code and existing paths. Collisions get new IDs in the staged copy, because vendors do reuse GUIDs and
an import would otherwise silently replace an asset. Your download is unchanged.

Then run an `import` recipe with `scene`, `packagePath` and `packageManifest`. Treat a partial import
as a recovery job. Don't simply retry it.

## Headless checks

Each runs with `Unity -batchmode -projectPath <proj> -executeMethod <below> -logFile <log>`, with the
editor closed. Usage is at the top of each file. Lines in the log start with the class name.

| Method | Tells you |
| --- | --- |
| `AvatarAgentPerf.Stats` | Performance rank per category, as the SDK panel calculates it, for the scene and for a built copy |
| `AvatarAgentVerify.Toggle` | Which FX layers and clips animate a menu toggle's target, after a real build |
| `AvatarAgentSaved.Report` / `.Apply` | Which menu controls keep their value across worlds; mark them saved |
| `AvatarAgentDump.Run` | Plain hierarchy, components and shaders |
| `AvatarAgentBench.Run` | Runtime GPU and CPU cost in play mode; `-benchParam` also measures one bool-driven feature |
| `AvatarAgentOptimizeAudit` | Read-only geometry export, to help place mesh cutters |
| `AvatarAgentShaderCompile.Run` | Compile named shaders for Windows and fail on errors |

`AvatarAgentVerify` trusts its static layer-and-clip report. Its behavioural half always reports
"active" outside play mode, so ignore that half.

`AvatarAgentBench` paired-difference timings are stable between runs (about 0.3 ms), but it isn't the
VRChat client. Compare before and after on one machine. Don't read the numbers as absolute.

## Letting the agent see

| Tool | Shows | Editor |
| --- | --- | --- |
| `preview`, `closeup` recipes | Authoring state, from chosen angles (no build applied) | Either |
| `scene-view` recipe | Exactly what the user's Scene view shows, plus camera pose and selection | Open |
| `AvatarAgentCrossSection.Export` + `tools/plot_avatar_sections.py` | Nine slices near an anchor bone, through the posed meshes, with crossings marked: proof of clipping or clearance that a render can't give. Use `-renderers` to compare a few meshes, and `-anchor` for the area you care about | Closed |
| Tools > Avatar Agent > Cross Section | The same as a live window, with the plane drawn in the Scene view, and optional clipping | Open |
| `AvatarAgentFirstPerson.Render` | Approximately what the wearer sees, on a built copy | Closed |
| `AgentNormalDebug.shader` | World normals (or tangents, with `_ShowTangent`) as colour. Assign it through a material with `closeup` to find seams and bad normals | Either |
| `optimize_avatar.py` renders and `compare_optimized.py` | Source and variant in 15 poses, with differences flagged | Closed |

Ask before taking a screenshot of the desktop: it captures everything else on screen, and these usually make
it unnecessary.

## Upload

```sh
# A dry run builds and checks the login, then stops.
VRUNITY_UPLOAD_DRY=1 Unity -batchmode -force-vulkan -projectPath <proj> \
  -executeMethod AvatarAgentBatch.Upload -avatarScene Assets/....unity -logFile <log>
```

There is no `-quit`: it exits when finished, 0 on success. It needs graphics, since AAO refuses
`-nographics`. It runs the SDK's real export (with NDMF, MA and VRCFury) and uploads a new version of
the avatar its PipelineManager names, using the SDK's saved login. It never creates an avatar, and
leaves the name, image and settings alone. It skips the panel's own validation and size checks; the
server enforces limits.

The first upload of an avatar has to be done in the GUI: the SDK panel has an agreement to accept. So a **new
variant must get a new slot**: clear the copied blueprint ID and publish it once yourself in the SDK panel.
Otherwise a clone overwrites the avatar it was cloned from.

## Adding operations

Implement them as editor operations against a clone, with a recipe and a report, so they work both
headless and live. Validate everything before applying, use one Undo group, and don't edit scene YAML
to guess file IDs.
