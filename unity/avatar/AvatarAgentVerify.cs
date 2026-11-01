// Headless check that a menu toggle can switch its object once the avatar is built.
//   Unity -batchmode -force-vulkan -projectPath <proj> -executeMethod AvatarAgentVerify.Toggle \
//     -avatarScene Assets/....unity -menuItem "My Toggle" -targetName <object name> -logFile <log>
// Builds a copy of the scene's avatar with the SDK's preprocess callbacks (NDMF, Modular Avatar,
// VRCFury), then reads the built FX controller: the menu item's parameter must drive a layer that
// animates the target, and the target must have both an on and an off value. It doesn't run the
// animator, whose edit-mode evaluation didn't change Modular Avatar's toggles, so confirm in VRChat.
// Nothing is saved. Exits 0 when the toggle passes, and 1 when the parameter drives nothing or the
// target only has one value. Exits 2 when the parameter drives something else but not the target by
// this path: an optimizer may have merged its mesh (it lists what is driven), or the target may be
// wrong. Test those in VRChat.
// Lines start with [AvatarAgentVerify].
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;

public static class AvatarAgentVerify
{
    static void Log(string s) => Debug.Log("[AvatarAgentVerify] " + s);

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void Toggle()
    {
        int code = 1;
        GameObject copy = null;
        try
        {
            string scenePath = Arg("-avatarScene") ?? throw new ArgumentException("pass -avatarScene");
            string item = Arg("-menuItem") ?? throw new ArgumentException("pass -menuItem");
            string targetName = Arg("-targetName") ?? throw new ArgumentException("pass -targetName");
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var avatar = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;
            copy = UnityEngine.Object.Instantiate(avatar);
            // A path from the avatar root wins over a (unique) name anywhere below it. Taken before
            // the build, which can remove the object.
            var target = copy.transform.Find(targetName) ?? copy.GetComponentsInChildren<Transform>(true).Single(t => t.name == targetName);
            string path = AnimationUtility.CalculateTransformPath(target, copy.transform);
            if (!VRCBuildPipelineCallbacks.OnPreprocessAvatar(copy)) throw new InvalidOperationException("Preprocess failed");

            var descriptor = copy.GetComponent<VRCAvatarDescriptor>();
            var control = Controls(descriptor.expressionsMenu).FirstOrDefault(c => c.name == item)
                ?? throw new InvalidOperationException("No menu item " + item + " after build");
            string param = control.parameter.name;
            var fx = descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController as UnityEditor.Animations.AnimatorController
                ?? throw new InvalidOperationException("FX is not an AnimatorController");
            Log($"menu item {item} drives {param}; target path {path}; {fx.layers.Length} FX layers");

            bool driven = false;
            var other = new System.Collections.Generic.HashSet<string>();
            var values = new System.Collections.Generic.HashSet<bool>();
            foreach (var layer in fx.layers)
            {
                var states = layer.stateMachine.states.Select(c => c.state).ToArray();
                var clips = states.SelectMany(st => Clips(st.motion)).Distinct().Where(c => c != null).ToArray();
                var found = clips.SelectMany(c => AnimationUtility.GetCurveBindings(c).Where(b => b.path == path && b.propertyName == "m_IsActive")
                    .Select(b => AnimationUtility.GetEditorCurve(c, b).keys[0].value > 0.5f)).ToArray();
                if (found.Length == 0)
                {
                    if (Uses(layer.stateMachine, states, param))
                        other.UnionWith(clips.SelectMany(AnimationUtility.GetCurveBindings).Select(b => (b.path.Length == 0 ? "(root)" : b.path) + " " + b.propertyName));
                    continue;
                }
                values.UnionWith(found);
                bool uses = Uses(layer.stateMachine, states, param);
                driven |= uses;
                Log($"layer '{layer.name}': sets active {string.Join("/", found.Distinct())}{(uses ? ", uses " + param : "")}");
            }
            bool both = values.Contains(true) && values.Contains(false);
            Log($"RESULT parameter drives the target: {driven}; has on and off values: {both}");
            code = driven && both ? 0 : 1;
            if (values.Count == 0 && other.Count > 0)
            {
                Log("RESULT can't judge: nothing animates this path, but " + param + " drives: " + string.Join(", ", other.Take(8)));
                code = 2;
            }
        }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
        finally { if (copy != null) UnityEngine.Object.DestroyImmediate(copy); }
        EditorApplication.Exit(code);
    }

    // Whether any state or transition in the layer reads the parameter.
    static bool Uses(UnityEditor.Animations.AnimatorStateMachine machine, UnityEditor.Animations.AnimatorState[] states, string param)
    {
        bool InTree(Motion m) => m is UnityEditor.Animations.BlendTree t && (t.blendParameter == param || t.blendParameterY == param
            || t.children.Any(c => c.directBlendParameter == param || InTree(c.motion)));
        return states.Any(st => InTree(st.motion) || st.timeParameter == param || st.speedParameter == param || st.mirrorParameter == param || st.cycleOffsetParameter == param
                || st.transitions.Any(t => t.conditions.Any(c => c.parameter == param)))
            || machine.anyStateTransitions.Any(t => t.conditions.Any(c => c.parameter == param))
            || machine.entryTransitions.Any(t => t.conditions.Any(c => c.parameter == param));
    }

    static System.Collections.Generic.IEnumerable<AnimationClip> Clips(Motion m)
    {
        if (m is AnimationClip c) yield return c;
        else if (m is UnityEditor.Animations.BlendTree t)
            foreach (var child in t.children) foreach (var x in Clips(child.motion)) yield return x;
    }

    static System.Collections.Generic.IEnumerable<VRCExpressionsMenu.Control> Controls(VRCExpressionsMenu menu, int depth = 0)
    {
        if (menu == null || depth > 8) yield break;
        foreach (var c in menu.controls)
        {
            yield return c;
            if (c.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                foreach (var s in Controls(c.subMenu, depth + 1)) yield return s;
        }
    }
}
