# agentic-unity

Tools and notes for having a coding agent (Claude Code, Codex and the like) do VRChat Unity work
with you: inspect and edit avatars, measure and optimize them, and build worlds, mostly without
clicking through the editor.

The idea: **a JSON recipe, or a static method run by Unity in batch mode, does the work and writes
a report the agent can read.** With the editor open, the same recipes go through a file queue, so
you watch each change land in the Scene view and can undo it with Ctrl+Z.

These tools grew out of real avatar and world projects on Linux. See [What's tested](#whats-tested).

## Start

1. Install the toolchain: [docs/setup.md](docs/setup.md), plus [docs/linux.md](docs/linux.md) on Linux. Check [docs/dependencies.md](docs/dependencies.md) for what you need.
2. Back up your project, or work in a copy.
3. Set up Python, then install the editor helpers:

   ```sh
   uv sync
   uv run tools/install_helpers.py /path/to/Project --kind avatar   # or --kind world
   ```

4. Copy [templates/AGENTS.md](templates/AGENTS.md) next to your project as `AGENTS.md`, or into a
   folder of notes about it, and fill it in. It tells your agent the safety rules. Change them to
   suit you.
5. Point your agent at this repo and try a read-only job:

   ```sh
   echo '{"command":"inspect","scene":"Assets/Scenes/Main.unity"}' > /tmp/inspect.json
   uv run tools/avatar_agent.py /path/to/Project /tmp/inspect.json --batch   # the project must be closed in Unity
   ```

## Two ways to work

Tell your agent which you want, or let it ask:

- **Unity open, you directing.** You watch the Scene view and say what to change; recipes land live and Ctrl+Z
  works. Good for fitting and anything about looks. The agent can also see what you're looking at: `scene-view`
  gives it the editor camera's pose and your selection, and renders that exact view, so you can just say "that
  bit there" without a screenshot.
- **Headless.** Unity closed, the agent does it all. Good for "fix X and reupload" while you're in VRChat. These
  builds aren't always 100% accurate, so if something seems off in VRChat, build and publish from the editor to
  compare.

## Your first upload has to be by hand

**The first upload of an avatar or world must be done in the Unity GUI,** through the SDK panel: it has an
agreement you need to accept, and you need to log in there anyway. The headless tools only update uploads that
already exist, and never create one. So do it by hand for each new avatar, outfit variant or world. After that,
an agent can update it, if you allow that.

## What's here

| Path | What |
| --- | --- |
| [docs/avatar-agent.md](docs/avatar-agent.md) | Recipes, live editing, package intake, headless checks and upload |
| [docs/optimization.md](docs/optimization.md) | One-command Poor/Medium variants; AAO, Meshia and d4rk compared |
| [docs/world-builds.md](docs/world-builds.md) | Headless world export, local test in the real client, reading its logs, publish |
| [docs/fxc2.md](docs/fxc2.md) | Shaders too heavy for Microsoft's compiler, and building them on Linux |
| [docs/linux.md](docs/linux.md) | **On Linux, start here:** ALCOM, Vulkan, missing libraries, shaders, testing, VRChat under Proton |
| [docs/gotchas.md](docs/gotchas.md) | The expensive lessons. Worth having your agent read this first |
| [docs/dependencies.md](docs/dependencies.md) | What each part needs, and what fails without it. The optimizer needs the most |
| [examples/](examples/README.md) | A worked example of an agent modeling and shading a horn from code, with what it learned; plus [tools hardcoded to one avatar](examples/hardcoded/README.md) (leash, clipping, hat, sock) as examples of other kinds of tool |
| `unity/avatar/` | Editor scripts for avatar projects (needs the Avatars SDK and Modular Avatar) |
| `unity/world/` | Editor script for world projects (needs the Worlds SDK) |
| `tools/` | Python and shell tools that drive Unity |
| `templates/` | An `AGENTS.md` for your own project |

## What's tested

- **Run for real on 1 November 2026,** on Linux with Unity 2022.3.22f1 and the latest packages then available
  (versions in [docs/dependencies.md](docs/dependencies.md)), in a disposable project using VRChat's sample Robot
  avatar and the horn example:
  - everything compiles;
  - the recipes `inspect`, `preview`, `audit-meshes` and `add-toggle`;
  - `AvatarAgentPerf`, `Saved`, `Dump`, `Bench` (with `-benchParam`), `ShaderCompile`, `CrossSection` and
    `FirstPerson`;
  - the optimizer from start to finish, with its AAO stages and comparison renders;
  - the horn example's 35 checks;
  - in an open editor: `list`, `inspect`, `open`, `frame`, `preview`, `closeup`, `edit` (including rejected batches),
    `undo`, `redo`, `checkpoint`, `save`, `refresh`, `scene-view` (its render was confirmed to match the Scene view),
    and the Cross Section tool's window, `capture`, `open` and `capture-view`.
- **Used for real on an actual avatar, on older package versions:** live recipes, the optimizer with decimation
  and mesh cuts, and headless avatar upload (in VRChat). World export and publish also, in an earlier form.
- **On a real, complex avatar on the same packages:** a real headless upload, confirmed in VRChat; the optimizer with
  Meshia decimation and PhysBone merging (its renders still need a person's review: the test result had visible damage);
  `assemble` with a real outfit; and the accessory commands. `AgentWorldBuild.Export` built a real world; `Publish` was
  only run as far as its refusal for a world with no ID.
- **Known not to work:** `AvatarAgentVerify` wrongly reports Modular Avatar toggles as not switching, even a plain new one.
- **Not run on these copies yet:** the SDK-panel `upload` command, a world publish, and the revert when an `edit` fails
  partway (bad batches were all rejected before applying).
- **Windows and macOS are untested.** The editor lookup handles them, and on those systems the Linux-only
  `-force-vulkan` is left out. `vrchat_local_test.py`, `copy_unity_project.py` and save snapshots are
  Linux/macOS-minded (Steam and Proton paths, `findmnt`, `/bin/sh`).

Some helpers rest on assumptions from the avatars they were made for. Inspect the result rather
than trusting a clean exit.

## Contributing

If your agent makes something others could use, have it ask you about opening a PR. See `AGENTS.md`.

## Licence

MIT. See [LICENSE](LICENSE). Installed Unity packages and avatar assets keep their own licences.

## Related projects

- [vrmodem](https://github.com/mommysgoodpuppy/vrmodem) and
  [ShaderEmu (fork)](https://github.com/mommysgoodpuppy/ShaderEmu): the world projects the world
  build tooling came from. Good examples of an agent-run world, from scene generation to in-client
  log analysis.
- [FXC2 (fork)](https://github.com/mommysgoodpuppy/FXC2): the shader compiler in
  [docs/fxc2.md](docs/fxc2.md).
