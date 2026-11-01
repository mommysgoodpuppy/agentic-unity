// Which menu controls keep their value across worlds, headless. Close every editor on the
// project first, then:
//   Unity -batchmode -nographics -projectPath <proj> -executeMethod AvatarAgentSaved.Report \
//     -avatarScene Assets/VRUnityGenerated/....unity -logFile <log>
// Report builds a copy with the SDK's preprocess callbacks (VRCFury, Modular Avatar) and walks
// the built expressions menu: every control's parameters with their synced and saved flags.
// It also lists the source settings that decide "saved": MA Menu Items, MA Parameters entries,
// VRCFury toggles and the parameter assets the avatar uses. Nothing is saved. Lines start with
// [AvatarAgentSaved]; exits 0 when it ran.
// AvatarAgentSaved.Apply (same arguments) marks every MA Menu Item and VRCFury toggle or slider
// on the avatar as saved, skipping buttons and hold buttons, and saves the scene. Vendor
// parameter assets are left alone; Report shows whether any control still resets.
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;

public static class AvatarAgentSaved
{
    static void Log(string s) => Debug.Log("[AvatarAgentSaved] " + s);

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static GameObject OpenAvatar()
    {
        string scenePath = Arg("-avatarScene") ?? throw new ArgumentException("pass -avatarScene");
        if (!scenePath.StartsWith("Assets/VRUnityGenerated/")) throw new ArgumentException("Only draft scenes under Assets/VRUnityGenerated/");
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        return scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;
    }

    static void Walk(VRCExpressionsMenu menu, string path, Dictionary<string, VRCExpressionParameters.Parameter> prms, HashSet<VRCExpressionsMenu> seen)
    {
        if (menu == null || !seen.Add(menu)) return;
        foreach (var c in menu.controls)
        {
            string here = path + "/" + c.name;
            if (c.type == VRCExpressionsMenu.Control.ControlType.SubMenu) { Walk(c.subMenu, here, prms, seen); }
            var names = new[] { c.parameter?.name }.Concat(c.subParameters?.Select(p => p?.name) ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n)).Distinct();
            foreach (var n in names)
            {
                prms.TryGetValue(n, out var p);
                string flags = p == null ? "NOT DECLARED" : $"{p.valueType} synced={p.networkSynced} saved={p.saved} default={p.defaultValue}";
                Log($"{(p != null && !p.saved ? "UNSAVED " : "")}control {here} [{c.type}] param {n}: {flags}");
            }
        }
    }

    public static void Apply()
    {
        int code = 1;
        try
        {
            var avatar = OpenAvatar();
            var root = avatar.transform;
            int changed = 0;
            foreach (var mi in avatar.GetComponentsInChildren<ModularAvatarMenuItem>(true))
            {
                if (mi.isSaved || mi.Control.type == VRCExpressionsMenu.Control.ControlType.Button) continue;
                mi.isSaved = true;
                EditorUtility.SetDirty(mi);
                changed++;
                Log($"MA Menu Item {AnimationUtility.CalculateTransformPath(mi.transform, root)} is now saved");
            }
            foreach (var mb in avatar.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().FullName == "VF.Model.VRCFury"))
            {
                var so = new SerializedObject(mb);
                var content = so.FindProperty("content");
                if (content == null || !content.managedReferenceFullTypename.EndsWith("VF.Model.Feature.Toggle")) continue;
                var saved = content.FindPropertyRelative("saved");
                if (saved.boolValue || content.FindPropertyRelative("holdButton").boolValue) continue;
                saved.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
                changed++;
                Log($"VRCFury toggle '{content.FindPropertyRelative("name").stringValue}' on {AnimationUtility.CalculateTransformPath(mb.transform, root)} is now saved");
            }
            if (changed > 0)
            {
                EditorSceneManager.MarkSceneDirty(avatar.scene);
                if (!EditorSceneManager.SaveScene(avatar.scene)) throw new System.IO.IOException("Scene save failed");
                Log($"saved {avatar.scene.path} ({changed} changed)");
            }
            else Log("every toggle was already saved");
            code = 0;
        }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
        EditorApplication.Exit(code);
    }

    public static void Report()
    {
        int code = 1;
        GameObject copy = null;
        try
        {
            var avatar = OpenAvatar();
            var root = avatar.transform;
            var desc = avatar.GetComponent<VRCAvatarDescriptor>();
            Log($"descriptor menu {AssetDatabase.GetAssetPath(desc.expressionsMenu)}, parameters {AssetDatabase.GetAssetPath(desc.expressionParameters)}");
            foreach (var p in desc.expressionParameters?.parameters ?? new VRCExpressionParameters.Parameter[0])
                Log($"  descriptor param {p.name} {p.valueType} synced={p.networkSynced} saved={p.saved}");
            foreach (var mi in avatar.GetComponentsInChildren<ModularAvatarMenuItem>(true))
                Log($"MA Menu Item {AnimationUtility.CalculateTransformPath(mi.transform, root)}: param '{mi.Control.parameter?.name}' isSaved={mi.isSaved} isSynced={mi.isSynced}");
            foreach (var mp in avatar.GetComponentsInChildren<ModularAvatarParameters>(true))
                foreach (var p in mp.parameters)
                    Log($"MA Parameters {AnimationUtility.CalculateTransformPath(mp.transform, root)}: {p.nameOrPrefix} -> '{p.remapTo}' {p.syncType} saved={p.saved} internal={p.internalParameter}");
            foreach (var mb in avatar.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().FullName == "VF.Model.VRCFury"))
            {
                var so = new SerializedObject(mb);
                var it = so.GetIterator();
                string type = null, name = null, saved = null, global = null; var prmAssets = new List<string>();
                while (it.Next(true))
                {
                    if (it.propertyType == SerializedPropertyType.ManagedReference && it.managedReferenceFullTypename.Contains("VF.Model.Feature.")) type ??= it.managedReferenceFullTypename.Split('.').Last();
                    if (it.name == "name" && it.propertyType == SerializedPropertyType.String && name == null) name = it.stringValue;
                    if (it.name == "saved" && it.propertyType == SerializedPropertyType.Boolean) saved = it.boolValue.ToString();
                    if (it.name == "globalParam" && it.propertyType == SerializedPropertyType.String) global = it.stringValue;
                    if (it.name == "objRef" && it.propertyType == SerializedPropertyType.ObjectReference && it.objectReferenceValue is VRCExpressionParameters ep) prmAssets.Add(AssetDatabase.GetAssetPath(ep));
                }
                Log($"VRCFury {AnimationUtility.CalculateTransformPath(mb.transform, root)}: {type} '{name}' saved={saved ?? "-"} globalParam='{global}' paramAssets=[{string.Join(", ", prmAssets.Distinct())}]");
            }

            copy = UnityEngine.Object.Instantiate(avatar);
            if (!VRCBuildPipelineCallbacks.OnPreprocessAvatar(copy)) throw new InvalidOperationException("Preprocess failed");
            var built = copy.GetComponent<VRCAvatarDescriptor>();
            var prms = built.expressionParameters.parameters.GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First());
            Walk(built.expressionsMenu, "", prms, new HashSet<VRCExpressionsMenu>());
            foreach (var p in built.expressionParameters.parameters.Where(p => !p.saved))
                Log($"built unsaved param {p.name} {p.valueType} synced={p.networkSynced}");
            Log($"built cost {built.expressionParameters.CalcTotalCost()} bits");
            code = 0;
        }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
        finally { if (copy != null) UnityEngine.Object.DestroyImmediate(copy); }
        EditorApplication.Exit(code);
    }
}
