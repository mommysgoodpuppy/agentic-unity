// Headless avatar build and upload, after ShaderEmu's LinuxBuild.Publish. Close every editor on
// the project first, then:
//   Unity -batchmode -force-vulkan -projectPath <proj> -executeMethod AvatarAgentBatch.Upload \
//     -avatarScene Assets/....unity -logFile <log>
// Needs graphics (DISPLAY etc. from `systemctl --user show-environment`): AAO refuses -nographics.
// No -quit: it exits when done (0 uploaded, 1 failed). It builds the scene's one avatar with the
// SDK's own export (the build half of the panel's Build and Upload, NDMF/Modular Avatar/VRCFury
// hooks included) and uploads the bundle as a new version of the avatar its PipelineManager
// names, with the SDK's saved login (log in once in the SDK panel of a normal editor). It never
// creates an avatar; the name, image and settings stay as they are. VRUNITY_UPLOAD_DRY=1 does
// everything but the upload. Progress lines start with [AvatarAgentBatch].
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using VRC.SDKBase.Editor.BuildPipeline;

public static class AvatarAgentBatch
{
    static void Log(string s) => Debug.Log("[AvatarAgentBatch] " + s);

    static void Fail(Exception e)
    {
        Debug.LogException(e);
        Log("upload FAILED: " + e.Message);
        EditorApplication.Exit(1);
    }

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void Upload()
    {
        try
        {
            string scenePath = Arg("-avatarScene") ?? throw new ArgumentException("pass -avatarScene");
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var descriptors = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).ToArray();
            if (descriptors.Length != 1) throw new InvalidOperationException($"{scenePath} has {descriptors.Length} avatars; expected one");
            var avatar = descriptors[0].gameObject;
            string id = avatar.GetComponent<PipelineManager>()?.blueprintId;
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("The avatar has no blueprint ID; refusing to create a new avatar");
            Log("building " + avatar.name + " from " + scenePath);

            if (!VRCBuildPipelineCallbacks.OnVRCSDKBuildRequested(VRCSDKRequestedBuildType.Avatar))
                throw new InvalidOperationException("Build was blocked by an SDK callback");
            built = null; error = null;
            VRC_SdkBuilder.shouldBuildUnityPackage = false;
            // The callback registration is internal to the SDK: called by reflection.
            Internal("ClearCallbacks");
            Register("RegisterBuildProgressCallback", nameof(OnProgress));
            Register("RegisterBuildErrorCallback", nameof(OnError));
            Register("RegisterBuildSuccessCallback", nameof(OnSuccess));
            try { VRC_SdkBuilder.RunExportAvatarBlueprint(avatar); }
            catch (Exception e) { error ??= e.Message; }
            Internal("ClearCallbacks");
            if (string.IsNullOrEmpty(built) || !File.Exists(built)) throw new InvalidOperationException("Build failed: " + error);
            // The panel's size-limit checks use SDK-internal helpers; VRChat's server enforces them too.
            Log($"built {built} ({new FileInfo(built).Length / 1048576f:F1} MB)");

            UploadBundle(id, built);
        }
        catch (Exception e) { Fail(e); }
    }

    static string built, error;
    static void OnProgress(object _, string s) => Log("build: " + s);
    static void OnError(object _, string s) => error = s;
    static void OnSuccess(object _, (string path, string signature) r) => built = r.path;

    static MethodInfo SdkMethod(string name) =>
        typeof(VRC_SdkBuilder).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("VRC_SdkBuilder." + name);

    static void Internal(string name) => SdkMethod(name).Invoke(null, null);

    static void Register(string name, string handler)
    {
        var method = SdkMethod(name);
        var type = method.GetParameters()[0].ParameterType;
        var target = typeof(AvatarAgentBatch).GetMethod(handler, BindingFlags.Static | BindingFlags.NonPublic);
        method.Invoke(null, new object[] { Delegate.CreateDelegate(type, target) });
    }

    static async void UploadBundle(string id, string bundle)
    {
        try
        {
            var existing = await VRCApi.GetAvatar(id, true);
            Log("logged in; uploading as an update of '" + existing.Name + "'");
            if (Environment.GetEnvironmentVariable("VRUNITY_UPLOAD_DRY") == "1")
            {
                Log("dry run: built and found the avatar; nothing uploaded");
                EditorApplication.Exit(0);
                return;
            }
            int shown = -1;
            await VRCApi.UpdateAvatarBundle(id, existing, bundle, (status, progress) =>
            {
                int tenth = (int)(progress * 10f);
                if (tenth == shown) return;
                shown = tenth;
                Log($"{status} {progress * 100f:F0}%");
            });
            Log("uploaded '" + existing.Name + "'");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Fail(e); }
    }
}
