// Headless check that a menu toggle really switches its object once the avatar is built.
//   Unity -batchmode -nographics -projectPath <proj> -executeMethod AvatarAgentVerify.Toggle \
//     -avatarScene Assets/....unity -menuItem "My Toggle" -targetName <object name> -logFile <log>
// Builds a copy of the scene's avatar with the SDK's preprocess callbacks (NDMF, Modular Avatar,
// VRCFury), drives its FX controller with the menu item's parameter off and on, and checks the
// target follows. It repeats with an empty Animator added on the target as a control: that copy
// should not follow. Nothing is saved. Exits 0 when the real avatar follows. Lines start with
// [AvatarAgentVerify].
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
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
        try
        {
            string scenePath = Arg("-avatarScene") ?? throw new ArgumentException("pass -avatarScene");
            string item = Arg("-menuItem") ?? throw new ArgumentException("pass -menuItem");
            string targetName = Arg("-targetName") ?? throw new ArgumentException("pass -targetName");
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var avatar = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;

            bool real = Follows(avatar, item, targetName, false);
            bool control = Follows(avatar, item, targetName, true);
            Log($"RESULT saved avatar follows: {real}; with an Animator on the target (control): {control}");
            code = real ? 0 : 1;
            // Optional reference toggle, to tell a broken harness from a broken toggle.
            if (Arg("-refItem") is string refItem)
                Log($"RESULT reference {refItem} follows: {Follows(avatar, refItem, Arg("-refTarget"), false)}");
        }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
        EditorApplication.Exit(code);
    }

    // Builds a copy, sets the toggle off then on, and reports whether the target followed both times.
    static bool Follows(GameObject avatar, string item, string targetName, bool nestedAnimator)
    {
        string tag = nestedAnimator ? "control" : "saved";
        var copy = UnityEngine.Object.Instantiate(avatar);
        copy.name = avatar.name + " (verify " + tag + ")";
        try
        {
            // A path from the avatar root wins over a (unique) name anywhere below it.
            var target = copy.transform.Find(targetName) ?? copy.GetComponentsInChildren<Transform>(true).Single(t => t.name == targetName);
            var existing = target.GetComponents<Animator>();
            Log($"{tag}: target {targetName} has {existing.Length} Animator(s) before build");
            if (nestedAnimator && existing.Length == 0) target.gameObject.AddComponent<Animator>();
            if (!VRCBuildPipelineCallbacks.OnPreprocessAvatar(copy)) throw new InvalidOperationException("Preprocess failed");

            var descriptor = copy.GetComponent<VRCAvatarDescriptor>();
            var control = Controls(descriptor.expressionsMenu).FirstOrDefault(c => c.name == item)
                ?? throw new InvalidOperationException("No menu item " + item + " after build");
            string param = control.parameter.name;
            var fx = descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;
            var animator = copy.GetComponent<Animator>();
            animator.runtimeAnimatorController = fx;
            var type = animator.parameters.Single(p => p.name == param).type;
            Log($"{tag}: menu item {item} drives {param} ({type}); FX {fx.name}");
            Diagnose(animator, copy.transform, target, tag);

            // Animator.Update does not evaluate outside play mode; a PlayableGraph does.
            bool ok = true;
            foreach (bool on in new[] { false, true, false })
            {
                var graph = UnityEngine.Playables.PlayableGraph.Create("verify");
                try
                {
                    var playable = UnityEngine.Animations.AnimatorControllerPlayable.Create(graph, fx);
                    UnityEngine.Animations.AnimationPlayableOutput.Create(graph, "fx", animator).SetSourcePlayable(playable);
                    if (type == AnimatorControllerParameterType.Bool) playable.SetBool(param, on);
                    else if (type == AnimatorControllerParameterType.Int) playable.SetInteger(param, on ? 1 : 0);
                    else playable.SetFloat(param, on ? 1 : 0);
                    for (int i = 0; i < 30; i++) graph.Evaluate(0.1f);
                }
                finally { graph.Destroy(); }
                bool active = target.gameObject.activeSelf;
                Log($"{tag}: {param}={(on ? 1 : 0)} -> {targetName} active={active}");
                ok &= active == on;
            }
            return ok;
        }
        finally { UnityEngine.Object.DestroyImmediate(copy); }
    }

    // Logs every FX layer whose motions animate the target's active state, with its weight and state.
    static void Diagnose(Animator animator, Transform root, Transform target, string tag)
    {
        string path = AnimationUtility.CalculateTransformPath(target, root);
        var fx = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
        if (fx == null) { Log(tag + ": FX is not an AnimatorController"); return; }
        Log($"{tag}: {fx.layers.Length} FX layers; target path {path}");
        for (int l = 0; l < fx.layers.Length; l++)
        {
            var states = fx.layers[l].stateMachine.states.Select(c => c.state).ToArray();
            var hits = states.Where(st => Clips(st.motion).Any(c => AnimationUtility.GetCurveBindings(c)
                .Any(b => b.path == path && b.propertyName == "m_IsActive"))).ToArray();
            if (hits.Length == 0) continue;
            var cur = animator.GetCurrentAnimatorStateInfo(l).fullPathHash;
            var now = states.FirstOrDefault(st => Animator.StringToHash(fx.layers[l].stateMachine.name + "." + st.name) == cur);
            Log($"{tag}: layer {l} '{fx.layers[l].name}' weight {animator.GetLayerWeight(l)} states [{string.Join(", ", hits.Select(h => h.name + (h.writeDefaultValues ? " WD" : "")))}] now '{now?.name}'");
            foreach (var st in hits)
                foreach (var c in Clips(st.motion))
                    foreach (var b in AnimationUtility.GetCurveBindings(c).Where(b => b.path == path && b.propertyName == "m_IsActive"))
                        Log($"{tag}:   {st.name} clip '{c.name}' m_IsActive={AnimationUtility.GetEditorCurve(c, b).keys[0].value}");
        }
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
