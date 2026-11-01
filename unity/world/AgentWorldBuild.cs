// Headless world export and upload through the SDK's own calls, for Assets/VRUnityAgent/Editor.
//   Unity -batchmode -quit -nographics -buildTarget Win64 -projectPath <proj> \
//     -executeMethod AgentWorldBuild.Export -worldScene Assets/....unity -logFile <log>
//   Unity -batchmode -nographics -buildTarget Win64 -projectPath <proj> \
//     -executeMethod AgentWorldBuild.Publish -worldScene Assets/....unity -logFile <log>
// Export is the build half of Build & Test: it writes the .vrcw's path to
// Logs/agent-last-build.txt, for tools/vrchat_local_test.py.
// Publish has no -quit: it exits when done, 0 once uploaded. It uploads a new version of the world
// the scene's PipelineManager names, using the login saved by the SDK panel. It never creates a
// world, and leaves the name, image and settings alone. AGENT_PUBLISH_DRY=1 does all but the upload.
// A failed batch run must call Exit(1): Unity otherwise exits 0 after an exception.
using System;
using System.Collections.Generic;
using System.IO;
using UdonSharp.Compiler;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Editor.Builder;
using VRC.SDKBase;
using VRC.SDKBase.Editor.Api;

public static class AgentWorldBuild
{
    const string ResultPath = "Logs/agent-last-build.txt";

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static void Fail(Exception e)
    {
        Debug.LogException(e);
        Debug.Log("[AgentWorldBuild] FAILED: " + e.Message);
        if (Application.isBatchMode) EditorApplication.Exit(1);
    }

    public static void Export()
    {
        try { Build(out _); }
        catch (Exception e) { Fail(e); }
    }

    public static void Publish()
    {
        try
        {
            string bundle = Build(out string signature);
            var pipeline = UnityEngine.Object.FindObjectOfType<PipelineManager>();
            string id = pipeline != null ? pipeline.blueprintId : null;
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("The scene's world has no ID; publish it once from the SDK panel. Refusing to create a world.");
            var descriptor = UnityEngine.Object.FindObjectOfType<VRC_SceneDescriptor>();
            Upload(id, bundle, signature, descriptor != null && descriptor.udonProducts != null ? new List<string>(descriptor.udonProducts) : null);
        }
        catch (Exception e) { Fail(e); }
    }

    static string Build(out string signature)
    {
        string scene = Arg("-worldScene") ?? throw new ArgumentException("Pass -worldScene Assets/....unity");
        EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
        // Must be synchronous in batch mode; otherwise proxy serialization fails with "outdated script version".
        UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions());
        string built = null, signed = null, error = null;
        var builder = (VRCWorldBuilder)Activator.CreateInstance(typeof(VRCWorldBuilder), true);
        builder.OnBuildProgress += (_, message) => Debug.Log("[AgentWorldBuild] " + message);
        builder.OnBuildSuccess += (_, result) => { built = result.Item1; signed = result.Item2; };
        builder.OnBuildError += (_, message) => error = message;
        bool ok = builder.ExportSceneResource();
        Debug.Log("[AgentWorldBuild] ok=" + ok + " built=" + built + " error=" + error);
        if (!ok || string.IsNullOrEmpty(built)) throw new InvalidOperationException("Export failed: " + error);
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ResultPath, built + "\n");
        signature = signed;
        return built;
    }

    static async void Upload(string id, string bundle, string signature, List<string> udonProducts)
    {
        try
        {
            VRCWorld world = await VRCApi.GetWorld(id, true);
            Debug.Log("[AgentWorldBuild] uploading " + bundle + " as \"" + world.Name + "\"");
            if (udonProducts != null) world.UdonProducts = udonProducts;
            if (Environment.GetEnvironmentVariable("AGENT_PUBLISH_DRY") == "1")
            {
                Debug.Log("[AgentWorldBuild] dry run: logged in, world found; nothing uploaded");
                EditorApplication.Exit(string.IsNullOrEmpty(signature) ? 1 : 0);
                return;
            }
            int shown = -1;
            await VRCApi.UpdateWorldBundle(id, world, bundle, signature, (status, progress) =>
            {
                int tenth = (int)(progress * 10f);
                if (tenth == shown) return;
                shown = tenth;
                Debug.Log("[AgentWorldBuild] " + status + " " + (progress * 100f).ToString("F0") + "%");
            });
            Debug.Log("[AgentWorldBuild] published \"" + world.Name + "\"");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Fail(e); }
    }
}
