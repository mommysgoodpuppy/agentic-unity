# AGENTS.md

Shared tools and notes for agent-driven VRChat Unity work on avatars and worlds. No avatar or
project lives here.

- **Ask which way the user wants to work** at the start, unless it's already clear; the helpers support two:
  - **Unity open, user directing:** they watch the Scene view and say what to change. Send recipes without
    `--batch`, leave saving and undo where they can see them, and don't start another editor on the project.
  - **Headless:** they're busy or already in VRChat and just want "fix X and reupload". Unity must be closed
    on that project; run batch commands and report back with the results.
- `docs/`: settled documentation. Start with `README.md`, then `docs/gotchas.md`.
- `markdown/`: plans, work in progress and notes that should outlast a session. Start with
  `markdown/README.md`, which also says where each file came from. Keep project memory here, not in
  a harness's own memory system.
- `unity/avatar/`, `unity/world/`: editor scripts, installed into a project by
  `tools/install_helpers.py`. Edit them here, not in the installed copy.
- `tools/`: scripts that drive Unity. `unity_env.py` holds the shared editor lookup.
- `examples/`: worked examples of an agent modeling and shading from code. Not installed. Not re-run since
  being copied.
- `templates/AGENTS.md`: the rules for a user's own project. When someone starts work on a project
  with no `AGENTS.md`, offer this template.
- `research/`: gitignored reference material. Read from it; do not commit it.
- Use `_scratch/` for agent scratch files, experiments and local test artifacts. It is ignored by
  Git. Don't use `project/tmp/` or others, only `_scratch/`.
- When a dependency changes, update `docs/dependencies.md`, and the checks in `install_helpers.py` and
  `optimize_avatar.py`.
- Python: `uv run tools/<tool>.py`; add packages with `uv add`, keeping `uv.lock` committed. Blender scripts
  run in Blender's own Python.
- Check: `uv run python -m unittest discover -s tools -p 'test_*.py'`. C# has no test here: compile it
  against a real project by reusing that project's
  `Library/Bee/artifacts/*/Assembly-CSharp-Editor.rsp` with Unity's bundled `csc`, leaving out its
  `Assembly-CSharp-Editor` reference, or import it into a throwaway project.
- This repo is shared with friends. Nothing private goes in: no avatar or vendor names, personal
  paths, account, blueprint or world IDs, logs, paid assets, or code that patches a paid asset. Keep
  avatar-specific work in that avatar's own project notes. Before committing something copied from
  a private project, search it for those names.
- When a helper is only useful with one avatar or outfit, leave it out, or make the avatar-specific
  part an argument. It can also go in `examples/` if it teaches something, with names removed.
- State what has actually been run. "Compiles", "ran in batch mode" and "checked in the VRChat
  client" are different claims; `README.md` records which apply.

## Related projects

- `../vrunity`: the maintainer's private avatar hub, the source of the avatar helpers. It stays
  private. Move generic fixes here, scrubbed, one direction at a time.
- `../vrmodem`, `../ShaderEmu`: world projects that supplied the world build, local test and fxc2
  material. Their own docs stay the authority on their projects.

## Git

Manage git however you see fit. Primary is `main`.

- Commit as the configured git identity. No `Co-Authored-By` or other co-author trailers.
- Never push, open PRs or issues, or publish anything to external sites unless asked for that
  specific action.
- **Large contributions:** if you built something others could use (a new helper, a real fix, a hard-won
  lesson), ask your user whether you may open a PR for it against this repo. Please do ask: that's how this
  gets better. Wait for a yes, and show them what would be sent. Remove their private details first (see
  above). Small or avatar-specific changes don't need the question.
- End every agent-made commit message with:

  ```
  Agent: <harness> <version>; model <exact model id>; thread <harness thread/session id>
  ```
