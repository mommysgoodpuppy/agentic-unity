// What the wearer sees, approximately: renders from the avatar's view position, on a built copy.
//   Unity -batchmode -force-vulkan -projectPath <proj> -executeMethod AvatarAgentFirstPerson.Render \
//     -avatarScene Assets/....unity -out <new directory> [-fov 70] -logFile <log>
// Writes normal.png, and head-shrunk.png with the head bone scaled down as VRChat does in first person.
// Objects under a VRCHeadChop keep their scale. It lists the head chops, and any constraint that overrides
// the head's scale, since those are what usually go wrong (a hat the wearer can see inside, for instance).
// This approximates HeadChop and doesn't emulate the VRChat client: confirm anything important in a headset.
// Nothing is saved. Lines start with [AvatarAgentFirstPerson]; exits 0 when it ran.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDKBase.Editor.BuildPipeline;

public static class AvatarAgentFirstPerson
{
    static void Log(string s) => Debug.Log("[AvatarAgentFirstPerson] " + s);

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void Render()
    {
        int code = 0;
        GameObject copy = null;
        var utility = new PreviewRenderUtility();
        try
        {
            string output = Arg("-out") ?? throw new ArgumentException("Pass -out <new directory>");
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Not empty: " + output);
            Directory.CreateDirectory(output);
            var scene = EditorSceneManager.OpenScene(Arg("-avatarScene") ?? throw new ArgumentException("Pass -avatarScene"), OpenSceneMode.Single);
            var source = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;
            copy = UnityEngine.Object.Instantiate(source);
            if (!VRCBuildPipelineCallbacks.OnPreprocessAvatar(copy)) throw new InvalidOperationException("The avatar's build callbacks failed");
            var root = copy.transform;
            var descriptor = copy.GetComponent<VRCAvatarDescriptor>();
            var head = copy.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head) ?? throw new InvalidOperationException("Avatar has no humanoid Head");
            var kept = copy.GetComponentsInChildren<VRCHeadChop>(true).Where(c => c.enabled).SelectMany(c => c.targetBones)
                .Where(b => b.transform != null && b.transform.IsChildOf(head) && b.transform != head && b.scaleFactor > 0).Select(b => b.transform).Distinct().ToArray();
            foreach (var t in kept) Log("head chop keeps " + AnimationUtility.CalculateTransformPath(t, root));
            foreach (var c in copy.GetComponentsInChildren<VRCScaleConstraint>(true))
                if (c.TargetTransform == head) Log("WARNING: scale constraint on the head: " + AnimationUtility.CalculateTransformPath(c.transform, root));

            utility.AddSingleGO(copy);
            var camera = utility.camera;
            camera.fieldOfView = float.Parse(Arg("-fov") ?? "70", System.Globalization.CultureInfo.InvariantCulture);
            camera.nearClipPlane = 0.01f; camera.farClipPlane = 100;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.gray;
            camera.transform.position = root.TransformPoint(descriptor.ViewPosition);
            camera.transform.rotation = root.rotation;
            Shader.SetGlobalFloat("_VRChatMirrorMode", 0);
            Shader.SetGlobalFloat("_VRChatCameraMode", 0);

            Save(utility, Path.Combine(output, "normal.png"));
            var scales = kept.Select(t => t.lossyScale).ToArray();
            head.localScale *= 0.0001f;
            for (int i = 0; i < kept.Length; i++)
            {
                var after = kept[i].lossyScale;
                kept[i].localScale = Vector3.Scale(kept[i].localScale, new Vector3(scales[i].x / after.x, scales[i].y / after.y, scales[i].z / after.z));
            }
            Save(utility, Path.Combine(output, "head-shrunk.png"));
            Log("wrote " + output);
        }
        catch (Exception e) { Debug.LogException(e); code = 1; }
        finally
        {
            utility.Cleanup();
            if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
        }
        EditorApplication.Exit(code);
    }

    static void Save(PreviewRenderUtility utility, string path)
    {
        utility.BeginStaticPreview(new Rect(0, 0, 960, 540));
        utility.Render(true);
        var texture = utility.EndStaticPreview();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
    }
}
