// Headless VRChat performance stats, the same calculation as the SDK panel's ranking.
//   Unity -batchmode -nographics -projectPath <proj> -executeMethod AvatarAgentPerf.Stats \
//     -avatarScene Assets/....unity -logFile <log>
// Reports the scene avatar as it is and a copy built with the SDK's preprocess callbacks
// (VRCFury, Modular Avatar, NDMF), which is closer to what is uploaded. Every stat and the
// rating per category; nothing is saved. Lines start with [AvatarAgentPerf].
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

public static class AvatarAgentPerf
{
    static void Log(string s) => Debug.Log("[AvatarAgentPerf] " + s);

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void Stats()
    {
        int code = 1;
        GameObject copy = null;
        try
        {
            AvatarPerformanceStats.Initialize(); // the rating thresholds, as the SDK loads them in the editor
            string scenePath = Arg("-avatarScene") ?? throw new ArgumentException("pass -avatarScene");
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var avatar = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;
            Report("scene", avatar);
            copy = UnityEngine.Object.Instantiate(avatar);
            if (!VRCBuildPipelineCallbacks.OnPreprocessAvatar(copy)) throw new InvalidOperationException("Preprocess failed");
            Report("built", copy);
            code = 0;
        }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
        finally { if (copy != null) UnityEngine.Object.DestroyImmediate(copy); }
        EditorApplication.Exit(code);
    }

    // The avatar's child that contains t, for grouping.
    static string Ancestor(Transform t, Transform root)
    {
        while (t.parent != null && t.parent != root) t = t.parent;
        return t == root ? "(root)" : t.name;
    }

    static void Report(string tag, GameObject avatar)
    {
        // As the SDK panel does first, so constraint stats are accurate; the manager is internal.
        var constraints = avatar.GetComponentsInChildren<VRCConstraintBase>(true);
        typeof(VRCConstraintBase).Assembly.GetTypes().FirstOrDefault(t => t.Name == "VRCConstraintManager")
            ?.GetMethod("Sdk_ManuallyRefreshGroups", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(null, new object[] { constraints });
        var stats = new AvatarPerformanceStats(false);
        AvatarPerformance.CalculatePerformanceStats(avatar.name, avatar, stats, false);
        // Every populated stat, by reflection so new SDK fields show up too.
        foreach (var m in typeof(AvatarPerformanceStats).GetMembers(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m is FieldInfo || (m is PropertyInfo p && p.GetIndexParameters().Length == 0)))
        {
            object v = m is FieldInfo f ? f.GetValue(stats) : ((PropertyInfo)m).GetValue(stats);
            if (v == null || v is string || v.GetType().IsArray) continue;
            Log($"{tag}: {m.Name} = {v}");
        }
        // Components by type, and where the constraints and PhysBones are.
        var all = avatar.GetComponentsInChildren<Component>(true).Where(c => c != null).ToArray();
        foreach (var g in all.GroupBy(c => c.GetType().FullName).OrderByDescending(g => g.Count()).Take(25))
            Log($"{tag}: component {g.Key} x{g.Count()}");
        foreach (var g in all.Where(c => c is UnityEngine.Animations.IConstraint || c is VRCConstraintBase || c.GetType().Name.Contains("PhysBone"))
                     .GroupBy(c => c.GetType().Name + " under " + Ancestor(c.transform, avatar.transform)).OrderByDescending(g => g.Count()))
            Log($"{tag}: {g.Key} x{g.Count()}");
        foreach (AvatarPerformanceCategory c in Enum.GetValues(typeof(AvatarPerformanceCategory)))
        {
            try { Log($"{tag}: rating {c} = {stats.GetPerformanceRatingForCategory(c)}"); }
            catch (Exception) { }
        }
    }
}
