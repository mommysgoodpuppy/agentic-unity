# Gotchas

Things that cost real time. Have your agent read this before its first session on a project.

## Batch mode

- **Unity exits 0 after an exception** in `-executeMethod` unless the method calls
  `EditorApplication.Exit(1)`. Don't trust the exit code alone: have the method report success.
- **Only one editor per project.** Batch mode refuses a project that's open.
- **Stale locks.** Closing an editor started by an agent can leave helpers (shader compiler, a silent
  Unity Hub, its licensing client) holding `Temp/UnityLockfile`, plus a stale
  `Library/EditorInstance.json`. Batch mode then says "another Unity instance". Stop those helpers,
  check no editor is running on the project, then remove those two files. **Never remove a lock while
  an editor is using the project.**
- **`-nographics` breaks rendering, and AAO refuses it** during a build. Render, upload and optimize
  runs need graphics. Stats and dumps generally don't.
- **The agent's shell may have no display variables.** Headless Unity with graphics needs `DISPLAY` or
  `WAYLAND_DISPLAY`, `XAUTHORITY` and `XDG_RUNTIME_DIR`; on Linux get them from
  `systemctl --user show-environment`. The tools here do.
- **Long runs belong in the background.** The first import and full builds take minutes.
- **Sharing a project between two editors:** make a full separate copy, with its own `Library`. Don't
  hard-link writable assets or caches.

## Linux graphics

- **Scenes look wrong, or the log floods with keyword-space errors?** Check you're on Vulkan. Opening from ALCOM
  uses OpenGL unless you set `-force-vulkan` there ([setup.md](setup.md)).

## Saving and crashes

- **The Unity editor crashes, sometimes natively,** often around Play or script import. Save confirmed
  work, and use snapshots ([setup.md](setup.md)). A relaunch often works.
- **A batch run can leave an open editor's scene stale.** Reload before saving in the editor, or you
  overwrite the batch run's work.
- **Play mode pauses the live queue.**
- **Log floods:** one repeating shader error reached over 6 GB. Read bounded tails. Unity logs can
  include licensing details, so don't paste them publicly.

## Avatars

- **A preview isn't a build.** MA, VRCFury and NDMF only apply at build or in play mode. To learn what's
  really uploaded, build a copy with the SDK's preprocess callbacks and inspect it, as the helpers do.
- **Disabling an optimizer doesn't undo a mesh already decimated** and assigned to `sharedMesh`. Audit
  the mesh subasset (`audit-meshes`), not the FBX or renderer name: one FBX can hold full and reduced
  versions. A "full" source can lack shape controls the reduced one has, so check before swapping.
- **Vendor GUIDs collide.** Packages reuse GUIDs for different files. Import through
  `inspect_avatar_package.py`.
- **Toggles and defaults.** Modular Avatar returns a toggled object to its saved state when the toggle is off, so save it
  in the off state, even if the toggle defaults to on. A target saved in the switched state never changes. `add-toggle`
  does this for you. `AvatarAgentVerify` reads the built result, but can't judge a target whose mesh an optimizer merged,
  and doesn't run the animator, so test toggles in VRChat too.
- **Saved parameters.** VRCFury's and vendors' toggles may reset across worlds. `AvatarAgentSaved.Report`
  lists it.
- **Rig tools adjust bones continuously in edit mode,** so whole-hierarchy rotation snapshots can change
  with no edit having been made.
- **Clones keep the blueprint ID.** Uploading a clone replaces the original upload. Clear the ID on a new
  variant.
- **Headless builds aren't always 100% accurate.** If something looks or behaves wrong in VRChat, or before relying on an
important upload, build and publish from the editor's SDK panel to compare.
  Known differences: headless skips the panel's own validation and size checks, and can use different graphics
  settings and shader cache entries.
- **In-client checks are a separate result.** Headless builds tell you nothing about how physics,
  contacts, or first-person view feel. Say what hasn't been checked in VRChat.

## Agents

- **Inspect, don't guess.** Read the actual paths, shape names and components from a report.
- **Don't replay a mutating request that may have run.** Read its result first.
- **Use exact process names.** `pgrep -f Unity` matches the agent's own shell if its command line contains
  the word. Use `-x`.
- **Take screenshots of one window, not the whole desktop.** A desktop capture includes private things.
  `preview` and `closeup` usually mean no screenshot is needed.
- **Private things stay out of notes:** blueprint IDs, account details, and paid assets, which vendor
  licences generally prohibit sharing.
