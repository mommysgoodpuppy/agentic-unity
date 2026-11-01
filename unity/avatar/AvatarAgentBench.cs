// Runtime cost of the avatar, measured headlessly in play mode (VRCFury and MA build it on
// entering play mode, as for an upload).
//   Unity -batchmode -force-vulkan -projectPath <proj> -executeMethod AvatarAgentBench.Run \
//     -avatarScene Assets/....unity [-benchFrames 300] [-benchRemove Name,Other] [-benchPerRenderer 1] [-benchParam Name] -logFile <log>
// -benchParam adds scenarios with that bool parameter on: the cost of one animated feature.
// -benchPerRenderer adds a GPU cost per renderer at 1.5 m (each one hidden versus all shown).
// -benchRemove deletes those children of the avatar root from the opened scene before the
// play-mode build (nothing is saved), to measure the avatar without them.
// For each scenario it records:
// - GPU: synchronous renders of one VR eye (2016x2240, 4x MSAA) from the scenario's camera,
//   each waited for by a 1-pixel readback, alternating the avatar hidden and shown; "+ms" is
//   the median of the paired differences, the avatar's own cost.
// - CPU: the per-frame average of profiler markers for animators, PhysBones, contacts,
//   skinning and constraints over -benchFrames player-loop frames.
// The FX controller runs alone (no base or gesture layers, so the body holds its default
// pose and PhysBones barely move); IsLocal is 0 except in first person. Not the VRChat
// client, but the same frames before and after a change are comparable. Lines start with
// [AvatarAgentBench].
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Debug = UnityEngine.Debug;

public static class AvatarAgentBench
{
    static string featureParam => BenchArg("-benchParam");
    static string BenchArg(string name) { var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    const int Width = 2016, Height = 2240, Msaa = 4, Warmup = 20, Renders = 100;
    // Profiler markers worth reporting, by name fragment.
    static readonly string[] MarkerKeys = { "PlayerLoop", "GUIView.RepaintAll", "PhysBone", "Contact", "Animators.Update", "DirectorUpdateAnimation", "Animator.", "SkinnedMesh", "MeshSkinning", "Constraint" };
    // The summary per scenario: main-thread total, then the big pieces (PhysBones run as jobs
    // on worker threads; the main thread waits for them in the constraint update).
    static readonly (string label, string[] names)[] Summary =
    {
        ("main thread", new[] { "PlayerLoop" }),
        // The editor repaints inside the player loop; subtract it for what the avatar costs.
        ("of which editor repaint", new[] { "GUIView.RepaintAll.PlayerLoopController" }),
        ("animators", new[] { "PreLateUpdate.DirectorUpdateAnimationBegin", "PreLateUpdate.DirectorUpdateAnimationEnd" }),
        ("constraint update (incl. waiting for PhysBones)", new[] { "PreLateUpdate.ConstraintManagerUpdate" }),
        ("PhysBone jobs", new[] { "PhysBoneManager:PhysBoneJob" }),
        ("VRC constraints", new[] { "VRCConstraintsUpdate" }),
        ("skinning", new[] { "PostLateUpdate.UpdateAllSkinnedMeshes" }),
        ("contacts", new[] { "ContactManager:UpdateReceivers" }),
    };

    static void Log(string s) => Debug.Log("[AvatarAgentBench] " + s);

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Run the jobs as a player would: Burst-compiled before use, without the editor's job
    // debugger and leak detection. (Set through reflection; Burst is an SDK dependency.)
    static void PlayerLikeJobs()
    {
        var burst = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Unity.Burst.BurstCompiler")).FirstOrDefault(t => t != null);
        var options = burst?.GetField("Options")?.GetValue(null) ?? burst?.GetProperty("Options")?.GetValue(null);
        foreach (var (name, value) in new[] { ("EnableBurstCompilation", true), ("EnableBurstCompileSynchronously", true), ("EnableBurstSafetyChecks", false) })
            options?.GetType().GetProperty(name)?.SetValue(options, value);
        Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobDebuggerEnabled = false;
        Unity.Collections.NativeLeakDetection.Mode = Unity.Collections.NativeLeakDetectionMode.Disabled;
        Log($"Burst {(options == null ? "not found" : "compilation " + options.GetType().GetProperty("EnableBurstCompilation")?.GetValue(options))}, job debugger off, leak detection off");
    }

    public static void Run()
    {
        try
        {
            PlayerLikeJobs();
            string scenePath = Arg("-avatarScene") ?? throw new ArgumentException("pass -avatarScene");
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (Arg("-benchRemove") is string remove)
            {
                var root = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().transform;
                foreach (var name in remove.Split(','))
                {
                    var child = root.Find(name) ?? throw new ArgumentException("The avatar has no child " + name);
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                    Log("removed " + name + " for this run (scene not saved)");
                }
            }
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += Start;
            };
            EditorApplication.EnterPlaymode();
        }
        catch (Exception e) { Fail(e); }
    }

    static int Triangles(Renderer r)
    {
        Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
        return mesh != null ? mesh.triangles.Length / 3 : 0;
    }

    static void Fail(Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); EditorApplication.Exit(1); }

    class Scenario
    {
        public string name;
        public bool avatar = true, local, feature, physBones = true, fx = true, perRenderer;
        public Func<(Vector3 pos, Vector3 look)> camera;
    }

    static void Start()
    {
        int frames = int.TryParse(Arg("-benchFrames"), out var f) ? f : 300;
        GameObject avatar = null;
        Animator animator = null;
        Camera cam = null;
        RenderTexture rt = null;
        Texture2D pixel = null;
        List<(string name, ProfilerCategory category)> markers = null;
        var scenarios = new List<Scenario>();
        var results = new List<string>();
        int index = -1, phase = 0, phaseFrame = 0;
        double phaseStart = 0;
        List<ProfilerRecorder> recorders = null;

        void SetParam(string suffix, bool on)
        {
            foreach (var p in animator.parameters.Where(p => p.name == suffix || p.name.EndsWith("/" + suffix.Split('/').Last()) && p.name.EndsWith(suffix)))
            {
                if (p.type == AnimatorControllerParameterType.Float) animator.SetFloat(p.name, on ? 1 : 0);
                else if (p.type == AnimatorControllerParameterType.Int) animator.SetInteger(p.name, on ? 1 : 0);
                else animator.SetBool(p.name, on);
            }
        }

        double Render(Stopwatch sw)
        {
            sw.Restart();
            cam.Render();
            RenderTexture.active = rt;
            pixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false); // waits for the GPU
            RenderTexture.active = null;
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        double Median(List<double> xs) { xs.Sort(); return xs[xs.Count / 2]; }
        // Renders with the avatar hidden and shown, alternating (and alternating which goes
        // first), so other load on the GPU hits both alike; the avatar's cost is the median of
        // the paired differences.
        (double hidden, double shown, double cost) GpuPaired(Renderer[] only = null)
        {
            var renderers = only ?? avatar.GetComponentsInChildren<Renderer>(true);
            var sw = new Stopwatch();
            var hidden = new List<double>(); var shown = new List<double>(); var cost = new List<double>();
            try
            {
                for (int i = 0; i < Warmup + Renders; i++)
                {
                    double h = 0, v = 0;
                    foreach (bool show in i % 2 == 0 ? new[] { false, true } : new[] { true, false })
                    {
                        foreach (var r in renderers) r.forceRenderingOff = !show;
                        if (show) v = Render(sw); else h = Render(sw);
                    }
                    if (i < Warmup) continue;
                    hidden.Add(h); shown.Add(v); cost.Add(v - h);
                }
            }
            finally { foreach (var r in renderers) r.forceRenderingOff = false; }
            return (Median(hidden), Median(shown), Median(cost));
        }

        EditorApplication.CallbackFunction step = null;
        step = () =>
        {
            try
            {
                if (index < 0)
                {
                    if (phaseStart == 0) { phaseStart = EditorApplication.timeSinceStartup; return; }
                    if (EditorApplication.timeSinceStartup - phaseStart < 3) return; // the play-mode build
                    avatar = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>().Single().gameObject;
                    var desc = avatar.GetComponent<VRCAvatarDescriptor>();
                    animator = avatar.GetComponent<Animator>();
                    animator.runtimeAnimatorController = desc.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;
                    var head = animator.GetBoneTransform(HumanBodyBones.Head);
                    Vector3 Face() => head.position + avatar.transform.up * 0.06f;
                    Vector3 Fwd() => avatar.transform.forward;
                    scenarios.Add(new Scenario { name = "no avatar, 1.5 m", avatar = false, camera = () => (Face() + Fwd() * 1.5f, Face()) });
                    scenarios.Add(new Scenario { name = "1.5 m", camera = () => (Face() + Fwd() * 1.5f, Face()) });
                    if (featureParam != null) scenarios.Add(new Scenario { name = "1.5 m, feature on", feature = true, camera = () => (Face() + Fwd() * 1.5f, Face()) });
                    scenarios.Add(new Scenario { name = "0.35 m", camera = () => (Face() + Fwd() * 0.35f + avatar.transform.up * 0.05f, Face() + avatar.transform.up * 0.06f) });
                    if (featureParam != null) scenarios.Add(new Scenario { name = "0.35 m, feature on", feature = true, camera = () => (Face() + Fwd() * 0.35f + avatar.transform.up * 0.05f, Face() + avatar.transform.up * 0.06f) });
                    scenarios.Add(new Scenario { name = "first person", local = true, feature = featureParam != null,
                        camera = () => { var eye = avatar.transform.TransformPoint(desc.ViewPosition); return (eye, eye + Fwd()); } });
                    // What each subsystem costs: switch it off and compare with "1.5 m".
                    scenarios.Add(new Scenario { name = "1.5 m, PhysBones off", physBones = false, camera = () => (Face() + Fwd() * 1.5f, Face()) });
                    scenarios.Add(new Scenario { name = "1.5 m, FX animator off", fx = false, camera = () => (Face() + Fwd() * 1.5f, Face()) });
                    scenarios.Add(new Scenario { name = "1.5 m, both off", physBones = false, fx = false, camera = () => (Face() + Fwd() * 1.5f, Face()) });
                    // Two rounds, to show how repeatable the numbers are.
                    scenarios.AddRange(scenarios.ToList());
                    if (Arg("-benchPerRenderer") != null)
                        scenarios.Add(new Scenario { name = "1.5 m, per renderer", perRenderer = true, camera = () => (Face() + Fwd() * 1.5f, Face()) });

                    // The PhysBones, biggest first.
                    foreach (var x in avatar.GetComponentsInChildren<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>(true)
                                 .Select(pb => (pb, root: pb.rootTransform != null ? pb.rootTransform : pb.transform))
                                 .Select(x => (x.pb, x.root, bones: x.root.GetComponentsInChildren<Transform>(true).Length - (x.pb.ignoreTransforms?.Count ?? 0)))
                                 .OrderByDescending(x => x.bones).ToArray())
                        Log($"PhysBone {AnimationUtility.CalculateTransformPath(x.root, avatar.transform)}: ~{x.bones} transforms, {x.pb.colliders?.Count(c => c != null) ?? 0} colliders, grab {x.pb.allowGrabbing}, collision {x.pb.allowCollision}");

                    cam = new GameObject("bench camera").AddComponent<Camera>();
                    cam.enabled = false; // rendered by hand
                    cam.clearFlags = CameraClearFlags.Skybox;
                    cam.fieldOfView = 105;
                    cam.nearClipPlane = 0.01f;
                    rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = Msaa };
                    cam.targetTexture = rt;
                    pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    if (!UnityEngine.Object.FindObjectsOfType<Light>().Any(l => l.type == LightType.Directional && l.isActiveAndEnabled))
                    {
                        var sun = new GameObject("bench sun").AddComponent<Light>();
                        sun.type = LightType.Directional;
                        sun.transform.rotation = Quaternion.Euler(40, -60, 0);
                    }

                    var handles = new List<ProfilerRecorderHandle>();
                    ProfilerRecorderHandle.GetAvailable(handles);
                    markers = handles.Select(ProfilerRecorderHandle.GetDescription)
                        .Where(d => d.UnitType == ProfilerMarkerDataUnit.TimeNanoseconds && MarkerKeys.Any(k => d.Name.Contains(k)))
                        .Select(d => (d.Name, d.Category)).Distinct().OrderBy(d => d.Name).ToList();
                    Log($"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}); {Width}x{Height} {Msaa}x MSAA; {frames} CPU frames per scenario");
                    Log("CPU markers: " + string.Join(", ", markers.Select(m => m.name)));
                    index = 0; phase = 0;
                    return;
                }
                if (index >= scenarios.Count)
                {
                    EditorApplication.update -= step;
                    foreach (var line in results) Log(line);
                    Log("RESULT DONE");
                    EditorApplication.Exit(0);
                    return;
                }
                var s = scenarios[index];
                if (phase == 0)
                {
                    avatar.SetActive(s.avatar);
                    if (s.avatar)
                    {
                        foreach (var pb in avatar.GetComponentsInChildren<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>(true)) pb.enabled = s.physBones;
                        animator = avatar.GetComponent<Animator>();
                        animator.enabled = s.fx;
                        SetParam("IsLocal", s.local);
                        if (featureParam != null) SetParam(featureParam, s.feature);
                    }
                    phase = 1; phaseStart = EditorApplication.timeSinceStartup;
                    return;
                }
                if (phase == 1)
                {
                    if (EditorApplication.timeSinceStartup - phaseStart < 2.5) return; // fades settle
                    recorders = markers.Select(m => ProfilerRecorder.StartNew(m.category, m.name, frames)).ToList();
                    phaseFrame = Time.frameCount;
                    phase = 2;
                    return;
                }
                if (Time.frameCount - phaseFrame < frames) return;
                // Per-frame average of each marker; the same name can come from several
                // threads or categories, so sum those.
                var perMarker = new Dictionary<string, double>();
                for (int i = 0; i < recorders.Count; i++)
                {
                    var r = recorders[i];
                    double sum = 0; int n = r.Count;
                    for (int k = 0; k < n; k++) sum += r.GetSample(k).Value;
                    r.Dispose();
                    double ms = n > 0 ? sum / n / 1e6 : 0;
                    perMarker[markers[i].name] = perMarker.TryGetValue(markers[i].name, out var had) ? had + ms : ms;
                }
                Log($"{s.name} all markers: " + string.Join(", ", perMarker.Where(kv => kv.Value >= 0.005).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:0.000}")));
                var cpu = Summary.Select(x => $"{x.label} {x.names.Sum(n => perMarker.TryGetValue(n, out var v) ? v : 0):0.00}").ToList();
                var (pos, look) = s.camera();
                cam.transform.position = pos;
                cam.transform.LookAt(look);
                string gpu = "-";
                if (s.perRenderer)
                {
                    // Each visible renderer's own cost: it alone hidden versus everything shown.
                    var costs = avatar.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && r.gameObject.activeInHierarchy)
                        .Select(r => (r, g: GpuPaired(new[] { r })))
                        .OrderByDescending(x => x.g.cost).ToList();
                    foreach (var (r, g) in costs)
                        Log($"renderer {AnimationUtility.CalculateTransformPath(r.transform, avatar.transform)}: +{g.cost:0.00} ms; " +
                            $"{Triangles(r)} tris; " +
                            string.Join(", ", r.sharedMaterials.Where(m => m != null).Select(m => m.name + " [" + m.shader.name + "]")));
                    gpu = $"sum of renderers +{costs.Sum(x => x.g.cost):0.00} ms";
                }
                else if (s.avatar)
                {
                    var g = GpuPaired();
                    gpu = $"+{g.cost:0.00} ms ({g.hidden:0.00} hidden, {g.shown:0.00} shown)";
                }
                string round = index >= scenarios.Count / 2 ? "round 2" : "round 1";
                results.Add($"[{round}] {s.name}: GPU {gpu}; CPU ms/frame: {string.Join(", ", cpu)}");
                Log(results.Last());
                index++; phase = 0;
            }
            catch (Exception e) { EditorApplication.update -= step; Fail(e); }
        };
        EditorApplication.update += step;
    }
}
