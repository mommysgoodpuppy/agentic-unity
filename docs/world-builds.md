# Building and testing worlds without clicking

An agent can run the whole loop: generate the scene, export it, open it in the real VRChat client as
a hidden local instance, and read what happened from the client's log.

Install with `tools/install_helpers.py PROJECT --kind world`. For complete examples, see
[vrmodem](https://github.com/mommysgoodpuppy/vrmodem) (`server/run_probe.ts`, `analyze.ts`,
`unity/Editor/`) and [ShaderEmu's fork](https://github.com/mommysgoodpuppy/ShaderEmu)
(`docs/linux-build.md`).

## Generate the scene from code

Keep the source in a Git repo, symlink it into the project (`ln -s ~/git/myworld/unity
Assets/myworld`), and have an editor script build the scene from scratch on each export. That makes
the scene reviewable, and no one is tempted to hand-edit a generated file. `.meta` files and compiled
UdonSharp assets change with the scripts, and belong in Git.

## The loop

```sh
# 1. Export: the build half of Build & Test. Close the editor first.
Unity -batchmode -quit -nographics -buildTarget Win64 -projectPath <proj> \
  -executeMethod AgentWorldBuild.Export -worldScene Assets/Scenes/World.unity -logFile <log>

# 2. Launch the real client on that build and follow its log.
uv run tools/vrchat_local_test.py --project <proj> --until "[MyWorld] done" --filter 'MyWorld|Exception'
```

The `.vrcw` lands in the SDK's world folder, with its path written to `Logs/agent-last-build.txt`.

Have your Udon code `Debug.Log` lines with a unique tag and a timestamp, ending with one "done" line.
Then the agent can tell when the run finished and analyze the lines afterwards.

This is the real client, logged in as whoever owns the Steam account, in a hidden local instance.
Nothing is uploaded. Ask before launching it: it takes over the machine's VRChat. Add `--vr` to stay
in VR. Runs take minutes, so start them in the background.

`--print-only` shows the launch arguments without starting anything.

## Publish

```sh
AGENT_PUBLISH_DRY=1 Unity -batchmode -nographics -buildTarget Win64 -projectPath <proj> \
  -executeMethod AgentWorldBuild.Publish -worldScene Assets/Scenes/World.unity -logFile <log>
```

Remove `AGENT_PUBLISH_DRY` for the real upload. There's no `-quit`; it exits when finished. It uploads
a new version of the world in the scene's PipelineManager, using the saved SDK login, and leaves the
name, image and settings alone. The world must already exist: its first upload has to be done in the SDK panel, which has an agreement to
accept. Only
do this when the owner asks.

**Headless builds aren't always 100% accurate.** If something looks or behaves wrong in VRChat, or before relying on an
important upload, build and publish from the editor's SDK panel to compare.

## Gotchas specific to worlds

- **The `.vrcw` filename changes on every export,** so `--watch-worlds` doesn't catch a rebuild in a
  running client. Close VRChat between builds.
- **Use exact process names:** `pkill -x VRChat.exe`. `pkill -f` matches any shell whose command line
  contains the pattern, including the agent's own.
- **UdonSharp must compile synchronously** in batch mode
  (`UdonSharpCompilerV1.CompileSync`), or proxy serialization fails with "outdated script version".
  `AgentWorldBuild` does this.
- **Video players added from code default to looping.**
- **Video on Linux:** stock Proton doesn't play video players reliably. A compatibility tool with RTSP
  and media support, such as a `proton-rtsp` build, has worked.
- **A baked scene loses its lighting if the builder recreates its objects,** since lightmaps are
  keyed by object IDs. Don't regenerate a baked scene unless you mean to rebake. Baking on Linux runs
  on the CPU: two hours for a modest room. With no bake and no lights set up, a world can upload black.
- **Check for a silently missing shader.** See [fxc2.md](fxc2.md): a build can "succeed" with a
  shader compile killed partway.
