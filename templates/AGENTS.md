# AGENTS.md

<One sentence: which avatar or world this is.>

- Unity project: `<path>`. Unity version: read `ProjectSettings/ProjectVersion.txt`.
- Tools: `<path to agentic-unity>`. Read its `README.md` and `docs/gotchas.md` before working, plus `docs/linux.md` on Linux. Run its
  Python tools with `uv run` from that folder.
- Install Unity packages with ALCOM or VCC.
- Record current state, versions and open problems in `STATUS.md`. Separate what the owner reported,
  what you read in files, and what you actually ran. "It compiled", "it built" and "it looked right
  in VRChat" are different results.
- Use `_scratch/` for agent scratch files and experiments.

## How to work

- **Ask which way the user wants to work** at the start, unless it's already clear; the helpers support two:
  - **Unity open, user directing:** they watch the Scene view and say what to change. Send recipes without
    `--batch`, leave saving and undo where they can see them, and don't start another editor on the project.
  - **Headless:** they're busy or already in VRChat and just want "fix X and reupload". Unity must be closed
    on that project; run batch commands and report back with the results.

## Rules for the Unity project

- Do not upgrade Unity, the SDK, shaders or packages unless asked. Read the project's recorded
  versions before opening it.
- Keep `.meta` files, project settings, and package manifests and locks. Losing a `.meta` breaks
  every reference to that asset.
- Work on generated drafts under `Assets/VRUnityGenerated/`. Leave the original scene and imported
  vendor assets alone, and copy a vendor asset before changing it. Originals from another machine or
  a Windows drive are read-only: edit a working copy.
- Before guessing a hierarchy path, inspect. Do not hand-edit scene YAML unless Unity is closed and
  you made a backup.
- Unity can crash: save the open draft after each change the owner confirmed or that passed its
  checks. Don't save a scene whose on-disk copy may be newer than the loaded one (after a batch run,
  for example); reload it first.
- Close the project's editor before batch runs. Never remove a lock file while an editor is running.
- Prefer Modular Avatar for new toggles. Existing VRCFury features can stay on VRCFury.
- Disabling a simplifier does not undo baked mesh reductions. Check the actual `sharedMesh`.

## Uploads and publishing

- Never upload an avatar or world unless the owner asks for that upload. <Or, if you want to allow
  it: "Updating `<scene>` in its existing slot needs no confirmation once checks pass.">
- The first upload of any avatar or world is done by the owner in the Unity GUI: the SDK panel asks them to
  accept an agreement there. Tell them when one is needed. After that, headless updates work.
- `AvatarAgentBatch.Upload` only updates the avatar named by the scene's PipelineManager. A new
  outfit or variant gets a new slot: clear the copied blueprint ID, and never overwrite an existing
  upload just because the draft was cloned from it.
- Do not post anywhere (GitHub, Discord, forums) without approval of the concrete action.
- If you make something large and generic while working here (a new agentic-unity helper, a real fix, a
  hard-won lesson), ask the owner whether you may open a PR to agentic-unity. Show them what would be
  sent, and remove avatar names, paths, IDs and paid assets first.
- Keep credentials, account and blueprint IDs, and paid assets out of notes, commits and uploads.

## Git

- Do not commit unless asked. <Or say how you like commits done.>
