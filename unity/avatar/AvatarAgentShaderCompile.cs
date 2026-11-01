// Compile named shaders for Windows, headless, and fail on errors. Run it with the fxc2 editor when a
// shader is too slow for FXC (docs/fxc2.md); the compiled variants land in the project's shader cache.
//   Unity -batchmode -nographics -buildTarget Win64 -projectPath <proj> \
//     -executeMethod AvatarAgentShaderCompile.Run -shaders Assets/A.shader,Assets/B.shader -logFile <log>
// Exits 0 only if every shader compiled without an error. No -quit: it exits by itself.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class AvatarAgentShaderCompile
{
    public static void Run()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-shaders");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Pass -shaders Assets/A.shader,Assets/B.shader");
            var paths = args[index + 1].Split(',');
            string directory = "Library/VRUnityAgent/ShaderBundle";
            Directory.CreateDirectory(directory);
            var start = DateTime.UtcNow;
            var bundle = new AssetBundleBuild { assetBundleName = "agent-shaders", assetNames = paths };
            if (BuildPipeline.BuildAssetBundles(directory, new[] { bundle }, BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64) == null)
                throw new InvalidOperationException("The shader bundle failed to build");
            foreach (var path in paths)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null) throw new ArgumentException("No shader at " + path);
                foreach (var message in ShaderUtil.GetShaderMessages(shader))
                    if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                        throw new InvalidOperationException($"{path}: {message.message} (line {message.line})");
                if (ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Shader compilation errors: " + path);
            }
            Debug.Log("[AvatarAgentShaderCompile] PASS seconds=" + (DateTime.UtcNow - start).TotalSeconds);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
