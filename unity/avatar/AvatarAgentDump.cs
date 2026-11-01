// Read-only hierarchy dump of a draft scene's avatar: paths, active state, components,
// renderer materials/shaders. Lines start with [AvatarAgentDump]; nothing is saved.
//   Unity -batchmode -nographics -projectPath <proj> -executeMethod AvatarAgentDump.Run -avatarScene <scene> -out <file.txt> [-bones 1] -logFile <log>
using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

public static class AvatarAgentDump
{
    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static string PathOf(Transform t, Transform root) =>
        t == root ? "" : (t.parent == root ? t.name : PathOf(t.parent, root) + "/" + t.name);

    public static void Run()
    {
        int code = 0;
        try
        {
            var scene = EditorSceneManager.OpenScene(Arg("-avatarScene"), OpenSceneMode.Single);
            var avatar = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single();
            var sb = new StringBuilder();
            foreach (var t in avatar.GetComponentsInChildren<Transform>(true))
            {
                var comps = t.GetComponents<Component>().Where(c => !(c is Transform))
                    .Select(c => c == null ? "MISSING" : c.GetType().Name).ToArray();
                bool bone = comps.Length == 0;
                if (bone && Arg("-bones") == null) continue;
                sb.Append($"[AvatarAgentDump] {(t.gameObject.activeSelf ? "+" : "-")} {PathOf(t, avatar.transform)} :: {string.Join(", ", comps)}");
                var r = t.GetComponent<Renderer>();
                if (r != null) sb.Append(" || " + string.Join("; ", r.sharedMaterials.Select(m => m == null ? "null" : $"{m.name} [{(m.shader ? m.shader.name : "noshader")}] {AssetDatabase.GetAssetPath(m)}")));
                var smr = t.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null) sb.Append($" || mesh {smr.sharedMesh.name} shapes {smr.sharedMesh.blendShapeCount}");
                sb.Append('\n');
            }
            System.IO.File.WriteAllText(Arg("-out"), sb.ToString());
        }
        catch (Exception e) { Debug.LogException(e); code = 1; }
        EditorApplication.Exit(code);
    }
}
