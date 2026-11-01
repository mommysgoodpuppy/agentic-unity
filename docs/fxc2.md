# Heavy shaders and fxc2

Unity builds D3D11 shaders with Microsoft's FXC, which VRChat PC content requires. Most shaders are
fine. You only need this page if a shader takes minutes to compile, or FXC can't compile it at all.

[fxc2](https://github.com/MichaelMoroz/FXC2) replaces FXC and produces the same kind of bytecode
(DXBC), from vkd3d's HLSL compiler with patches. The
[`linux-unity-loader` branch of this fork](https://github.com/mommysgoodpuppy/FXC2) adds native Linux
editor support; see its `docs/unity-linux-loader.md`, which builds the DLL and makes a separate editor
copy using it. Leave your installed editor as it is.

For one very large shader it cut compile time from about 9 minutes to about 10 seconds, and could
compile things FXC rejects. Run your own before and after comparison, and compare results rather than
assuming they're identical.

## The trap: a build that "succeeds" with no shader

Unity kills a shader compile after 10 minutes, logs `Shader Compiler IPC Exception: Terminating shader
compiler process`, and **carries on**. The build succeeds without that program, and you only find out
in VRChat. Search the log for that line, and for `Shader error in`, after any build with a heavy
shader.

`tools/unity-watch.sh` runs a batch command and prints progress every 30 s. It stops Unity if the
shader compiler keeps crashing or nothing moves for too long (`CRASHES=5 STALL=600`).

## Why not just use the fxc2 editor?

On Linux:

- **Don't open the fxc2 editor with a window on a project you use.** The Linux editor draws with
  OpenGL, translated from the D3D compiler's bytecode. fxc2's bytecode translates to broken GLSL, even
  for Unity's own UI, leaving a half-black editor that crashes.
- **The shader cache doesn't record which compiler made an entry.** `Library/ShaderCache` and
  `Library/ShaderCache.db` are keyed by source and options. Broken entries survive a switch of editor.
  This is also why the recipe below works: D3D11 shaders that fxc2 cached are what a stock editor's
  build uses.
- `ulimit -c 0` in the shell that runs Unity. Each crash otherwise leaves a core dump of about 0.5 GB,
  and can take far more temporarily.

## Recipe: fxc2 shaders in a normal upload

1. Close every editor on the project. **Back up `Library/ShaderCache*`.**
2. If a stock editor has ever built this for Windows, or the fxc2 editor was opened with a window, move
   `Library/ShaderCache` and `Library/ShaderCache.db` out of the project. Otherwise those entries get
   used instead of fxc2's.
3. Compile headless with the fxc2 editor, always with `-nographics`. Either run a whole export
   (worlds), or only the heavy shaders (`AvatarAgentShaderCompile`, for example on an avatar):

   ```sh
   ulimit -c 0
   STALL=900 tools/unity-watch.sh Logs/export.log FXC2/out/unity-linux-clean/editor/Unity \
     -batchmode -nographics -buildTarget Win64 -projectPath . \
     -executeMethod AvatarAgentShaderCompile.Run -shaders Assets/Heavy.shader
   ```

4. Check the log: no `Shader error in` for your shaders. Errors in shaders you don't use may be
   expected, since fxc2 can't compile everything.
5. Publish, either way:
   - Headless from the fxc2 editor (`AgentWorldBuild.Publish`). Nothing gets compiled by FXC.
   - From the stock editor, which then has to find fxc2's entries in its cache. Check
     `~/.config/unity3d/Editor.log`: the heavy shader's `Compiling shader` blocks must say `Local cache
     hits` and `compiled 0 variants`. If one compiles instead, that was FXC, and the upload lacks the
     working program: go back to step 1. Any editor or build that sits on that shader for minutes is
     FXC compiling it, so cancel and go back to step 1.

## What differs

- fxc2 predefines `__FXC2__`, so a shader can take advantage of it and still build under FXC.
- It always optimises. `#pragma skip_optimizations` does nothing.
- Use a recent build: older ones compiled `float - uint` wrongly.
- Some instructions don't survive the layers that run Direct3D on Linux. `mulhi` (one `umul` or `imul`)
  turned into an invalid SPIR-V module for one project, which had to fall back to separate
  multiplications. Test on Linux clients if people will use them.
