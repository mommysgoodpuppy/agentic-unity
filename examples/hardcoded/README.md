# Hardcoded examples

**These won't run on your avatar as they are.** They were written for one avatar and outfit: scene paths,
object names, measurements and limits are all that avatar's, written straight into the code. They're here as
examples of *kinds of tool* an agent can write for a specific job. Point your agent at one and ask for the same
idea for your own avatar.

No assets come with them. Where one mentions a vendor's asset, it's a path in the project of someone who bought
it, so you'd need your own copy. They compile alongside `unity/avatar/`, since they use its helpers, and were
compile-checked here, but they haven't been run since being copied. To try one, copy it into
`Assets/VRUnityAgent/Editor`.

They're written in a hurry, for one purpose, and it shows. That's fine. A throwaway tool an agent writes in ten
minutes for one fit is often the right tool.

| File | Kind of tool | Idea to take away |
| --- | --- | --- |
| [`AvatarAgentLeash.cs`](AvatarAgentLeash.cs) | Repairing and setting up a physics rig | See below |
| [`AvatarAgentClipFit.cs`](AvatarAgentClipFit.cs) | Fixing clipping with corrective blendshapes | Measure first; apply a small correction as a new shape on a *copy* of the mesh |
| [`AvatarAgentHatFit.cs`](AvatarAgentHatFit.cs) | Moving an outfit's rigid accessory onto your avatar | Check that the move changed no geometry, and adjust with numbers checked against cross-sections |
| [`AvatarAgentSock.cs`](AvatarAgentSock.cs) | Reusing a mesh from the avatar's own FBX on the current rig | Map bones by path and refuse if one is missing; a live request file to trigger actions |

## Leash: a helper for a common asset

`AvatarAgentLeash` works with [Reava's CHIRP collar and leash](https://reava.gumroad.com/l/CHIRP_Collars) and with upstream
[OSCLeash](https://github.com/ZenithVal/OSCLeash). It only refers to those assets by path. You need your own
copies. It shows a whole small job:

- **`Apply`** repairs a leash PhysBone that prefab overrides had removed, by copying the settings back from the
  vendor's prefab, adds a collider fitted to the avatar, and installs OSCLeash. It skips what's already there,
  so it can be rerun.
- **`Check`** builds a copy with the SDK's callbacks, then checks the wiring in the result.
- **`Preview`** enters play mode, lets the leash settle under physics, logs each bone's gap to the collider, and
  renders it.

What was learned:

- **Inspect prefab overrides when something doesn't move.** A leash that "hangs stiff" had its PhysBone removed
  as an override, which then got cloned into every draft.
- **Measure the body before fitting a collider,** and test by settling in play mode. An upright capsule made the
  leash slide round the side, and one lying across the chest made it drape in front.
- **Let physics settle, then measure.** A rendered gap, in numbers, beats a guess.
- **VRChat caches an avatar's OSC config,** so a stale one can hide new parameters. Moving it aside makes VRChat
  regenerate it.
- **OSC ports can clash.** Something else may already hold UDP 9001; OSCLeash's OSCQuery mode avoids that.

## ClipFit: corrective shapes

The core is `AddCorrection`: it takes a function from a vertex's posed world position to the offset you want,
converts that through the skinning matrices back to bind space, and adds it as a blendshape. Because it's a shape on
a copied mesh, the original is untouched, it's easy to switch off, and it survives further edits.

## HatFit and the live hook

`Prepare` and `Position` were called from the cross-section tool's live request queue (`prepare-hat`,
`position-hat`), which `unity/avatar/AvatarAgentCrossSection.cs` no longer has. Add a menu item or your own request
file to call them.
