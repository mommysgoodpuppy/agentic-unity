// Optimized avatar variants (Poor/Medium). See docs/optimization.md in agentic-unity.
//   Unity -batchmode -force-vulkan -projectPath <proj> -executeMethod AvatarAgentOptimize.Run \
//     -optConfig /abs/private/config.json -optStage <stage> -logFile <log>
// Stages, in order:
//   variant   copy the source scene to a new variant scene: clear the blueprint ID, rename,
//             fix big overlaid clothing on (drop its MA toggle), add AAO Trace and Optimize.
//   analyze   remove generated automatic cuts by default; with explicit automaticCuts opt-in,
//             ray cast a built copy in several poses and generate AAO UV-mask cuts.
//   probe     build a copy without decimation or automatic mesh merging; per-renderer triangles.
//   decimate  add a Meshia cascading simplifier sized from the probe so the build fits the target.
//   stats     build a copy as configured; SDK stats, ratings and per-renderer triangles.
//   render    render the built source and the built variant from identical cameras and poses
//             (lit, back-face flag, normals) for compare_optimized.py; needs a display.
// Every stage writes <reportDir>/<stage>.json. Only `variant`, `analyze` and `decimate` save the
// variant scene; the source scene is never saved.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

public static class AvatarAgentOptimize
{
    [Serializable] public class FixedOn { public string toggle; public string target; }
    [Serializable] public class SpatialCut { public string name, renderer; public string[] additionalRenderers = new string[0]; public Vector3 center, size; public string[] cover = new string[0]; }
    [Serializable] public class HandControl { public string name, mesh, mode; public string[] disableObjects = new string[0]; }
    [Serializable] public class CutBoneProtection { public string renderer; public string[] bones = new string[0]; }
    [Serializable] public class DecimationTarget { public string renderer; public int triangles; }
    // PhysBones merged into one AAO Merge PhysBone under `parent` (Make Children of Me). Their settings
    // must match except endpoint positions, which become real end bones (AAO "Clear").
    [Serializable] public class PhysBoneMerge { public string name, parent; public string[] sources = new string[0]; }

    [Serializable]
    public class Config
    {
        public string source, variant, assetDir, avatarName, reportDir, rank = "Poor";
        // An earlier optimized scene of the same variant whose upload slot (blueprint ID) this one continues.
        // Without it the copied ID is cleared and the first upload creates a new slot.
        public string slotFrom = "";
        // render: close-ups (lit + normals) around these humanoid bones, source vs variant, rest pose.
        public string[] renderFocus = { "LeftHand", "RightHand", "Head" };
        public string[] focusPoses = { "rest", "fist" };
        public bool renderNoOutline = false;
        public bool renderNoDecimation = false;      // QA only: inspect cuts independently of simplifier damage
        public bool renderUncovered = true;
        public bool renderAuditBody = false;
        // The body renderer, relative to the avatar root, for the hand controls and the body audit.
        public string bodyRenderer = "Body";
        public SpatialCut[] spatialCuts = new SpatialCut[0];
        public HandControl[] handControls = new HandControl[0];
        public CutBoneProtection[] cutBoneProtection = new CutBoneProtection[0];
        public FixedOn[] fixedOn = new FixedOn[0];   // big overlaid clothing: always on, toggle removed
        public string[] cut = new string[0];         // renderer paths whose hidden triangles are removed
        public bool automaticCuts = false;           // explicit opt-in only; manual MA volumes are independent
        public float margin = 0.03f;                 // metres of surface kept around anything visible
        public int maskSize = 2048;
        public string[] keepFull = new string[0];    // renderer paths never decimated
        public int minDecimate = 2000;               // smaller renderers are not decimated
        public int triangleTarget = 68000;
        public int minCut = 200;                     // auto cuts smaller than this many triangles are dropped
        public bool normalizeMeshSettings = true;    // root MA Mesh Settings + common renderer flags, so AAO can merge meshes
        public int physBoneLimit = 32;               // autophysbones merges until the built count is within this
        public float ratioScale = 1f;                // decimate: extra factor on the computed ratio (driver iteration)
        public DecimationTarget[] decimationTargets = new DecimationTarget[0];
        // decimate: budget share grows with size^sizeExponent (1 = uniform ratio; lower spares small, detailed
        // meshes such as ribbons and shoes and takes more from large ones).
        public float sizeExponent = 0.65f;
        // decimate: Meshia options. SmartLink (welding nearby unconnected vertices) is off: it fused hair strands
        // and fingers into dark, broken shading. minNormalDot refuses collapses that tilt faces too far.
        public bool meshiaSmartLink = false, meshiaPreserveCurvature = true;
        public float meshiaMinNormalDot = 0.6f;
        public PhysBoneMerge[] mergePhysBones = new PhysBoneMerge[0];
        public string[] renderPoses = { "rest", "armsUp", "sitA", "bendFwd", "twistL" };
        public int renderSize = 512;
    }

    [Serializable] public class RendererInfo { public string path, sourcePath, kind; public int triangles, sourceTriangles, materials; public bool active; }
    [Serializable] public class Rating { public string category, rating; }
    [Serializable] public class PhysBoneInfo { public string path, root, grab, settings; public int transforms, colliders; }
    [Serializable]
    public class StatsReport
    {
        public string stage, scene; public int polyCount, skinnedMeshCount, meshCount, materialCount, boneCount,
            physBoneComponents, physBoneTransforms, physBoneColliders, physBoneCollisionChecks, contactCount;
        public float textureMegabytes; public string overall;
        public Rating[] ratings; public RendererInfo[] renderers; public PhysBoneInfo[] physBones; public int animationBindings, unresolvedBindings, autoMergeReduction; public string[] unresolved, parameters, notes;
    }
    [Serializable]
    public class CutReport
    {
        public string path; public int triangles, visible, removable, maskRemoved, occluderTriangles;
        public string[] masks; public string[] poseNewlyVisible;
    }
    [Serializable] public class AnalyzeReport { public string scene; public CutReport[] cuts; public string[] notes; public int removedAutomaticMaskSlots; }
    [Serializable] public class DecimateEntry { public string path; public int sourceTriangles, probeTriangles, meshiaTarget; public bool decimated; public float ratio = 1; }
    [Serializable] public class DecimateReport { public int probeTotal, fixedTotal, budget, triangleTarget; public float ratio; public DecimateEntry[] entries; public string[] notes; }
    [Serializable] public class SimpleReport { public string stage, scene; public string[] notes; }

    const int RayLayer = 31;
    static readonly List<string> notes = new List<string>();
    static void Log(string s) { Debug.Log("[AvatarAgentOptimize] " + s); }
    static void Note(string s) { notes.Add(s); Log(s); }

    static string Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void Run()
    {
        int code = 1;
        try
        {
            var configPath = Arg("-optConfig") ?? throw new ArgumentException("pass -optConfig");
            var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(configPath));
            var stage = Arg("-optStage") ?? throw new ArgumentException("pass -optStage");
            Validate(cfg, stage);
            Directory.CreateDirectory(cfg.reportDir);
            switch (stage)
            {
                case "variant": Variant(cfg); break;
                case "analyze": Analyze(cfg); break;
                case "spatial": SpatialCuts(cfg); break;
                case "manual-check": SpatialCuts(cfg); Stats(cfg, "stats"); Render(cfg); break;
                case "physbones": PhysBones(cfg); break;
                case "autophysbones": AutoPhysBones(cfg); break;
                case "probe": Stats(cfg, "probe"); break;
                case "breakdown": Stats(cfg, "breakdown"); break;
                case "decimate": Decimate(cfg); break;
                case "targets": SetDecimationTargets(cfg); break;
                case "body-check": SetDecimationTargets(cfg); Stats(cfg, "stats"); Render(cfg); break;
                case "stats": Stats(cfg, "stats"); break;
                case "measure": Stats(cfg, "measure"); break;
                case "slot": Slot(cfg); break;  // build and measure `variant` only; any scene, never saved
                case "render": Render(cfg); break;
                case "hands": RenderHandControls(cfg); break;
                default: throw new ArgumentException("Unknown stage " + stage);
            }
            Log("DONE " + stage);
            code = 0;
        }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
        EditorApplication.Exit(code);
    }

    // Freeze an exact save snapshot for QA while another session continues authoring the source.
    // The QA scene receives a new GUID and no upload ID; the authoring scene is never overwritten.
    public static void FreezeBaseline()
    {
        try
        {
            string snapshot = Arg("-sourceSnapshot"), output = Arg("-baselineScene");
            if (snapshot == null || !Path.IsPathRooted(snapshot) || !File.Exists(snapshot)
                || output == null || !output.StartsWith("Assets/VRUnityGenerated/OptimizationBaselines/") || !output.EndsWith(".unity") || File.Exists(output))
                throw new ArgumentException("Pass an existing absolute -sourceSnapshot and a new generated -baselineScene");
            Directory.CreateDirectory(Path.GetDirectoryName(output)); File.Copy(snapshot, output);
            AssetDatabase.ImportAsset(output, ImportAssetOptions.ForceSynchronousImport);
            var scene = EditorSceneManager.OpenScene(output, OpenSceneMode.Single);
            var pm = FindAvatar(scene).GetComponents<Component>().Single(c => c != null && c.GetType().Name == "PipelineManager");
            var so = new SerializedObject(pm); so.FindProperty("blueprintId").stringValue = ""; so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Baseline save failed");
            Log("Frozen QA baseline " + output + "; upload ID cleared"); Log("DONE"); EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void Validate(Config cfg, string stage)
    {
        const string generated = "Assets/VRUnityGenerated/";
        if (string.IsNullOrEmpty(cfg.source) || !File.Exists(cfg.source)) throw new ArgumentException("source scene missing");
        if (string.IsNullOrEmpty(cfg.variant) || !cfg.variant.StartsWith(generated) || !cfg.variant.EndsWith(".unity"))
            throw new ArgumentException("variant must be a .unity path under " + generated);
        if (string.IsNullOrEmpty(cfg.assetDir) || !cfg.assetDir.StartsWith(generated)) throw new ArgumentException("assetDir must be under " + generated);
        if (string.IsNullOrEmpty(cfg.reportDir) || !Path.IsPathRooted(cfg.reportDir)) throw new ArgumentException("reportDir must be absolute");
        if (cfg.source == cfg.variant && stage != "measure") throw new ArgumentException("variant must differ from source");
    }

    static void WriteReport(Config cfg, string name, object report)
    {
        var path = Path.Combine(cfg.reportDir, name + ".json");
        File.WriteAllText(path, JsonUtility.ToJson(report, true));
        Log("report " + path);
    }

    // ---------- scene helpers ----------

    static GameObject FindAvatar(Scene scene)
    {
        var found = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true))
            .Where(d => d.gameObject.activeInHierarchy).ToArray();
        if (found.Length != 1) throw new InvalidOperationException($"Expected one active avatar in {scene.path}, found {found.Length}");
        return found[0].gameObject;
    }

    static Transform Find(Transform root, string path)
    {
        var t = root.Find(path);
        if (t == null) throw new ArgumentException($"No '{path}' under {root.name}");
        return t;
    }

    static string RelPath(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (; t != null && t != root; t = t.parent) parts.Add(t.name);
        if (t == null) throw new ArgumentException("Not under avatar root");
        parts.Reverse();
        return string.Join("/", parts);
    }

    static Type FindType(string fullName)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);
        return type ?? throw new InvalidOperationException("Type not found (package missing?): " + fullName);
    }

    static bool IsType(Component c, string fullName) => c != null && c.GetType().FullName == fullName;
    const string TraceAndOptimizeType = "Anatawa12.AvatarOptimizer.TraceAndOptimize";
    const string RemoveMeshByMaskType = "Anatawa12.AvatarOptimizer.RemoveMeshByMask";
    const string MeshiaCascadeType = "Meshia.MeshSimplification.Ndmf.MeshiaCascadingAvatarMeshSimplifier";
    const string MeshiaEntryType = "Meshia.MeshSimplification.Ndmf.MeshiaCascadingAvatarMeshSimplifierRendererEntry";

    static string BlueprintId(string scenePath)
    {
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        var pm = FindAvatar(scene).GetComponents<Component>().Single(c => c != null && c.GetType().Name == "PipelineManager");
        return new SerializedObject(pm).FindProperty("blueprintId").stringValue;
    }

    // The slot to continue: from an earlier optimized scene, never the source's own slot (that is the
    // unoptimized avatar). Null when no slotFrom is configured.
    static string SlotId(Config cfg)
    {
        if (string.IsNullOrEmpty(cfg.slotFrom)) return null;
        if (!cfg.slotFrom.StartsWith("Assets/VRUnityGenerated/Optimized/") || cfg.slotFrom == cfg.source || cfg.slotFrom == cfg.variant)
            throw new ArgumentException("slotFrom must be an earlier scene under Assets/VRUnityGenerated/Optimized/");
        string id = BlueprintId(cfg.slotFrom), sourceId = BlueprintId(cfg.source);
        if (string.IsNullOrEmpty(id)) throw new InvalidOperationException(cfg.slotFrom + " has no blueprint ID (never uploaded?)");
        if (id == sourceId) throw new InvalidOperationException("slotFrom has the source avatar's own ID; refusing");
        return id;
    }

    // Give an existing variant scene the slot of cfg.slotFrom (for variants made before slotFrom existed).
    static void Slot(Config cfg)
    {
        string id = SlotId(cfg) ?? throw new ArgumentException("pass slotFrom");
        var scene = OpenVariant(cfg);
        var pm = FindAvatar(scene).GetComponents<Component>().Single(c => c != null && c.GetType().Name == "PipelineManager");
        var so = new SerializedObject(pm);
        if (!string.IsNullOrEmpty(so.FindProperty("blueprintId").stringValue)) throw new InvalidOperationException(cfg.variant + " already has a blueprint ID");
        so.FindProperty("blueprintId").stringValue = id;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Note($"{cfg.variant} now continues the upload slot of {cfg.slotFrom}");
        WriteReport(cfg, "slot", new SimpleReport { stage = "slot", scene = cfg.variant, notes = notes.ToArray() });
    }

    static Scene OpenVariant(Config cfg)
    {
        if (!File.Exists(cfg.variant)) throw new InvalidOperationException("Run the variant stage first");
        return EditorSceneManager.OpenScene(cfg.variant, OpenSceneMode.Single);
    }

    // MA's six intersecting axis filters form an editable box in avatar coordinates.
    // These cutters are permanently active; source assets and vendor meshes stay intact.
    static void SpatialCuts(Config cfg)
    {
        var scene = OpenVariant(cfg); var avatar = FindAvatar(scene); var root = avatar.transform;
        var container = root.Find("VRUnity Spatial Cuts")?.gameObject;
        if (container != null) UnityEngine.Object.DestroyImmediate(container);
        container = new GameObject("VRUnity Spatial Cuts"); container.transform.SetParent(root, false);
        foreach (var spec in cfg.spatialCuts)
        {
            if (spec.size.x <= 0 || spec.size.y <= 0 || spec.size.z <= 0 || spec.cover.Length == 0)
                throw new ArgumentException("A spatial cut requires positive size and explicit permanent cover");
            foreach (var path in spec.cover)
            {
                var cover = Find(root, path);
                if (!cover.gameObject.activeInHierarchy || cover.GetComponent<Renderer>()?.enabled != true)
                    throw new InvalidOperationException("Cover must be visible: " + path);
                if (cfg.fixedOn.Any(f => f.target == path)) continue;
                // Animated visibility on a cover object (or ancestor) is unsafe.
                if (IsToggled(cover, root, ToggledPaths(avatar))) throw new InvalidOperationException("Cover can be hidden: " + path);
            }
            var targets = new[] { spec.renderer }.Concat(spec.additionalRenderers).Distinct().ToArray();
            var volume = new GameObject(spec.name); volume.transform.SetParent(container.transform, false);
            foreach (var targetPath in targets)
            {
                var target = Find(root, targetPath).GetComponent<SkinnedMeshRenderer>() ?? throw new ArgumentException("No skinned cut target " + targetPath);
                if ((target.transform.lossyScale - Vector3.one).sqrMagnitude > 1e-8f)
                    throw new InvalidOperationException("Spatial cutter fitting currently requires a renderer at unit world scale: " + spec.renderer);
                var go = targets.Length == 1 ? volume : new GameObject(targetPath.Split('/').Last());
                if (go != volume) go.transform.SetParent(volume.transform, false);
                var cutter = go.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarMeshCutter>();
                cutter.Object.Set(target.gameObject);
                cutter.MultiMode = nadena.dev.modular_avatar.core.MeshCutterMultiMode.VertexIntersection;
                for (int dim = 0; dim < 3; dim++) for (int sign = -1; sign <= 1; sign += 2)
                {
                    var point = spec.center; point[dim] += sign * spec.size[dim] / 2;
                    var axis = Vector3.zero; axis[dim] = -sign;
                    var filter = go.AddComponent<nadena.dev.modular_avatar.core.vertex_filters.VertexFilterByAxisComponent>();
                    filter.Center = target.transform.InverseTransformPoint(root.TransformPoint(point));
                    filter.Axis = target.transform.localToWorldMatrix.transpose.MultiplyVector(root.TransformDirection(axis)).normalized;
                }
                Note($"MA Mesh Cutter {spec.name}: {targetPath}; avatar-space center {spec.center}, size {spec.size}");
            }
        }
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Save failed");
        WriteReport(cfg, "spatial", new SimpleReport { stage = "spatial", scene = cfg.variant, notes = notes.ToArray() });
    }

    static int Triangles(Mesh mesh)
    {
        if (mesh == null) return 0;
        long n = 0;
        for (int s = 0; s < mesh.subMeshCount; s++) n += mesh.GetIndexCount(s) / 3;
        return (int)n;
    }

    // TryGetComponent: GetComponent's missing-component placeholder is not C# null, so `?.` would throw on it
    // (e.g. particle renderers).
    static Mesh MeshOf(Renderer r) => r is SkinnedMeshRenderer s ? s.sharedMesh
        : r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var f) ? f.sharedMesh : null;

    // A built copy of the avatar, as the SDK would upload it. `prepare` runs on the copy first.
    static GameObject BuildCopy(GameObject avatar, Action<GameObject> prepare)
    {
        var copy = UnityEngine.Object.Instantiate(avatar);
        copy.name = avatar.name;
        prepare?.Invoke(copy);
        if (!VRCBuildPipelineCallbacks.OnPreprocessAvatar(copy)) throw new InvalidOperationException("Preprocess failed");
        return copy;
    }

    static void StripOptimizers(GameObject copy)
    {
        foreach (var c in copy.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            var n = c.GetType().FullName;
            if (n.StartsWith("Anatawa12.AvatarOptimizer.") || n.StartsWith("Meshia.MeshSimplification.")) UnityEngine.Object.DestroyImmediate(c);
        }
    }

    static void NoAutoMerge(GameObject copy)
    {
        foreach (var c in copy.GetComponentsInChildren<Component>(true).Where(c => IsType(c, TraceAndOptimizeType)))
        {
            var so = new SerializedObject(c);
            so.FindProperty("mergeSkinnedMesh").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    static void NoDecimation(GameObject copy)
    {
        foreach (var c in copy.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().FullName.StartsWith("Meshia.MeshSimplification.")))
            UnityEngine.Object.DestroyImmediate(c);
    }

    // ---------- stage: variant ----------

    static void Variant(Config cfg)
    {
        if (File.Exists(cfg.variant)) throw new InvalidOperationException("Variant exists; choose a new dated path: " + cfg.variant);
        string slotId = SlotId(cfg);
        var scene = EditorSceneManager.OpenScene(cfg.source, OpenSceneMode.Single);
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.variant));
        if (!EditorSceneManager.SaveScene(scene, cfg.variant, true)) throw new IOException("Could not save variant copy");
        AssetDatabase.Refresh();
        scene = EditorSceneManager.OpenScene(cfg.variant, OpenSceneMode.Single);
        var avatar = FindAvatar(scene);
        var root = avatar.transform;
        Note($"Copied {cfg.source} -> {cfg.variant}");

        if (string.IsNullOrWhiteSpace(cfg.avatarName)) cfg.avatarName = $"{avatar.name} ({cfg.rank})";
        avatar.name = cfg.avatarName;
        var pipeline = avatar.GetComponents<Component>().Single(c => c != null && c.GetType().Name == "PipelineManager");
        var pso = new SerializedObject(pipeline);
        bool hadId = !string.IsNullOrEmpty(pso.FindProperty("blueprintId").stringValue);
        pso.FindProperty("blueprintId").stringValue = slotId ?? "";
        pso.ApplyModifiedPropertiesWithoutUndo();
        Note(slotId != null
            ? $"Renamed avatar to '{cfg.avatarName}'; continues the upload slot of {cfg.slotFrom}"
            : $"Renamed avatar to '{cfg.avatarName}'; blueprint ID cleared (had one: {hadId}). A new ID is assigned on first upload.");

        foreach (var f in cfg.fixedOn)
        {
            var toggle = Find(root, f.toggle);
            var target = Find(root, f.target);
            var comps = toggle.GetComponents<Component>().Where(c => c != null && !(c is Transform)).ToArray();
            var objectToggle = comps.SingleOrDefault(c => c.GetType().Name == "ModularAvatarObjectToggle")
                ?? throw new InvalidOperationException($"{f.toggle} has no MA Object Toggle");
            var objects = new SerializedObject(objectToggle).FindProperty("m_objects");
            var paths = Enumerable.Range(0, objects.arraySize)
                .Select(i => objects.GetArrayElementAtIndex(i).FindPropertyRelative("Object.referencePath").stringValue).ToArray();
            if (paths.Length != 1 || paths[0] != f.target)
                throw new InvalidOperationException($"{f.toggle} toggles [{string.Join(", ", paths)}], expected only {f.target}");
            if (toggle.childCount != 0) throw new InvalidOperationException($"{f.toggle} has children; refusing to delete it");
            var allowed = new[] { "ModularAvatarObjectToggle", "ModularAvatarMenuItem", "ModularAvatarMenuInstaller" };
            var other = comps.Where(c => !allowed.Contains(c.GetType().Name)).Select(c => c.GetType().Name).ToArray();
            if (other.Length > 0) throw new InvalidOperationException($"{f.toggle} has other components: {string.Join(", ", other)}");
            UnityEngine.Object.DestroyImmediate(toggle.gameObject);
            target.gameObject.SetActive(true);
            Note($"Fixed on: {f.target} (removed toggle object {f.toggle}; target now active)");
        }

        if (cfg.normalizeMeshSettings) NormalizeMeshSettings(avatar);
        if (!avatar.GetComponents<Component>().Any(c => IsType(c, TraceAndOptimizeType)))
        {
            avatar.AddComponent(FindType(TraceAndOptimizeType));
            Note("Added AAO Trace And Optimize (defaults) to the avatar root");
        }
        foreach (var d in avatar.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == "d4rkAvatarOptimizer"))
        {
            var so = new SerializedObject(d);
            var p = so.FindProperty("ApplyOnUpload");
            if (p != null && p.boolValue) { p.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo(); }
            Note("d4rkAvatarOptimizer stays present with ApplyOnUpload off");
        }
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Save failed");
        WriteReport(cfg, "variant", new SimpleReport { stage = "variant", scene = cfg.variant, notes = notes.ToArray() });
    }

    // AAO only merges meshes whose anchor, root bone, bounds and renderer flags match. Give the whole
    // avatar one MA Mesh Settings (reusing an existing avatar-wide anchor if there is one) and the most
    // common value of each renderer flag.
    static void NormalizeMeshSettings(GameObject avatar)
    {
        var existing = avatar.GetComponentsInChildren<nadena.dev.modular_avatar.core.ModularAvatarMeshSettings>(true);
        var reference = existing.FirstOrDefault(m => m.gameObject != avatar
            && m.InheritProbeAnchor != nadena.dev.modular_avatar.core.ModularAvatarMeshSettings.InheritMode.Inherit
            && m.InheritBounds != nadena.dev.modular_avatar.core.ModularAvatarMeshSettings.InheritMode.Inherit
            && m.ProbeAnchor?.Get(m) != null && m.RootBone?.Get(m) != null);
        var animator = avatar.GetComponent<Animator>();
        Transform anchor, rootBone; Bounds bounds;
        if (reference != null)
        {
            anchor = reference.ProbeAnchor.Get(reference).transform; rootBone = reference.RootBone.Get(reference).transform; bounds = reference.Bounds;
        }
        else
        {
            rootBone = animator.GetBoneTransform(HumanBodyBones.Hips);
            anchor = animator.GetBoneTransform(HumanBodyBones.Chest) ?? rootBone;
            var pts = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null)
                .SelectMany(r => AvatarMeshGeometry.WorldVertices(r)).Select(rootBone.InverseTransformPoint).ToArray();
            bounds = new Bounds(pts[0], Vector3.zero); foreach (var p in pts) bounds.Encapsulate(p);
            bounds.Expand(bounds.size.magnitude * 0.2f);
        }
        var settings = avatar.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarMeshSettings>()
            ?? avatar.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarMeshSettings>();
        settings.InheritProbeAnchor = nadena.dev.modular_avatar.core.ModularAvatarMeshSettings.InheritMode.Set;
        settings.ProbeAnchor = new nadena.dev.modular_avatar.core.AvatarObjectReference(); settings.ProbeAnchor.Set(anchor.gameObject);
        settings.InheritBounds = nadena.dev.modular_avatar.core.ModularAvatarMeshSettings.InheritMode.Set;
        settings.RootBone = new nadena.dev.modular_avatar.core.AvatarObjectReference(); settings.RootBone.Set(rootBone.gameObject);
        settings.Bounds = bounds;
        EditorUtility.SetDirty(settings);
        foreach (var m in existing.Where(m => m != settings))
        {
            m.InheritProbeAnchor = nadena.dev.modular_avatar.core.ModularAvatarMeshSettings.InheritMode.Inherit;
            m.InheritBounds = nadena.dev.modular_avatar.core.ModularAvatarMeshSettings.InheritMode.Inherit;
            EditorUtility.SetDirty(m);
        }
        Note($"Avatar-wide MA Mesh Settings: anchor {anchor.name}, root bone {rootBone.name}, bounds {bounds.size}" +
             (reference != null ? $" (from {reference.name})" : " (computed)") + $"; {existing.Count(m => m != settings)} nested settings now inherit");

        var smrs = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        T Most<T>(Func<SkinnedMeshRenderer, T> f) => smrs.GroupBy(f).OrderByDescending(g => g.Count()).First().Key;
        var shadow = Most(r => r.shadowCastingMode); var receive = Most(r => r.receiveShadows);
        var light = Most(r => r.lightProbeUsage); var refl = Most(r => r.reflectionProbeUsage);
        var quality = Most(r => r.quality); var offscreen = Most(r => r.updateWhenOffscreen);
        var motion = Most(r => r.skinnedMotionVectors); var occl = Most(r => r.allowOcclusionWhenDynamic);
        int changed = 0;
        foreach (var r in smrs)
        {
            bool diff = r.shadowCastingMode != shadow || r.receiveShadows != receive || r.lightProbeUsage != light || r.reflectionProbeUsage != refl
                || r.quality != quality || r.updateWhenOffscreen != offscreen || r.skinnedMotionVectors != motion || r.allowOcclusionWhenDynamic != occl;
            if (!diff) continue;
            r.shadowCastingMode = shadow; r.receiveShadows = receive; r.lightProbeUsage = light; r.reflectionProbeUsage = refl;
            r.quality = quality; r.updateWhenOffscreen = offscreen; r.skinnedMotionVectors = motion; r.allowOcclusionWhenDynamic = occl;
            EditorUtility.SetDirty(r); changed++;
        }
        Note($"Renderer flags aligned on {changed} of {smrs.Length} skinned meshes (shadows {shadow}, light probes {light}, reflection probes {refl}, quality {quality})");
    }

    // ---------- poses ----------

    // Poses are direct rotations of humanoid bones in avatar space (avatar facing +Z, up +Y), applied
    // parent before child. Unlike muscle values through HumanPoseHandler, they give the same pose on
    // every build of the same skeleton (AAO's bone changes made muscle poses differ between builds).
    abstract class PoseStep { public abstract void Apply(Func<HumanBodyBones, Transform> bone, Transform root); }

    // Aim a bone so the direction to its child bone points along `dir` (x is mirrored for right limbs).
    class Aim : PoseStep
    {
        readonly HumanBodyBones bone, child; readonly Vector3 dir;
        public Aim(HumanBodyBones bone, HumanBodyBones child, Vector3 dir) { this.bone = bone; this.child = child; this.dir = dir; }
        public override void Apply(Func<HumanBodyBones, Transform> get, Transform root)
        {
            var b = get(bone); var c = get(child);
            if (b == null || c == null) throw new InvalidOperationException($"Missing humanoid bone {bone}/{child}");
            var hips = get(HumanBodyBones.Hips);
            float side = Mathf.Sign(root.InverseTransformPoint(b.position).x - root.InverseTransformPoint(hips.position).x);
            var target = root.TransformDirection(new Vector3(dir.x * side, dir.y, dir.z)).normalized;
            b.rotation = Quaternion.FromToRotation(c.position - b.position, target) * b.rotation;
        }
    }

    // Rotate a bone about an avatar-space axis (e.g. +X: bend forward).
    class Turn : PoseStep
    {
        readonly HumanBodyBones bone; readonly Vector3 axis; readonly float degrees;
        public Turn(HumanBodyBones bone, Vector3 axis, float degrees) { this.bone = bone; this.axis = axis; this.degrees = degrees; }
        public override void Apply(Func<HumanBodyBones, Transform> get, Transform root)
        {
            var b = get(bone);
            if (b == null) return;  // optional bones (UpperChest) may be absent
            b.rotation = Quaternion.AngleAxis(degrees, root.TransformDirection(axis)) * b.rotation;
        }
    }

    class PoseDef { public string name; public PoseStep[] steps; }

    static PoseStep[] Arms(Vector3 dir) => new PoseStep[]
    {
        new Aim(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, dir), new Aim(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, dir),
    };
    static PoseStep[] Legs(Vector3 upper, Vector3 lower) => new PoseStep[]
    {
        new Aim(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, upper), new Aim(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, upper),
        new Aim(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, lower), new Aim(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, lower),
    };
    static PoseStep[] Spine(Vector3 axis, float degreesEach) => new PoseStep[]
    {
        new Turn(HumanBodyBones.Spine, axis, degreesEach), new Turn(HumanBodyBones.Chest, axis, degreesEach), new Turn(HumanBodyBones.UpperChest, axis, degreesEach),
    };
    static PoseStep[] Seq(params PoseStep[][] parts) => parts.SelectMany(x => x).ToArray();

    static PoseStep[] Fist()
    {
        var steps = new List<PoseStep>();
        foreach (var side in new[] { "Left", "Right" })
            foreach (var finger in new[] { "Index", "Middle", "Ring", "Little" })
                foreach (var joint in new[] { "Proximal", "Intermediate", "Distal" })
                    steps.Add(new Turn((HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), side + finger + joint),
                        Vector3.forward, side == "Left" ? 65 : -65));
        return steps.ToArray();
    }

    static readonly PoseDef[] Poses =
    {
        new PoseDef { name = "rest", steps = new PoseStep[0] },
        new PoseDef { name = "headLeft", steps = new PoseStep[] { new Turn(HumanBodyBones.Head, Vector3.up, 45) } },
        new PoseDef { name = "headDown", steps = new PoseStep[] { new Turn(HumanBodyBones.Head, Vector3.right, 30) } },
        new PoseDef { name = "fist", steps = Fist() },
        new PoseDef { name = "armsDown", steps = Arms(new Vector3(0.25f, -1, 0)) },
        new PoseDef { name = "armsUp", steps = Arms(new Vector3(0.3f, 1, 0)) },
        new PoseDef { name = "armsFront", steps = Arms(new Vector3(0.15f, 0, 1)) },
        new PoseDef { name = "armsBack", steps = Arms(new Vector3(0.3f, -0.7f, -0.6f)) },
        new PoseDef { name = "sitA", steps = Legs(new Vector3(0.1f, -0.15f, 1), new Vector3(0, -1, 0)) },
        new PoseDef { name = "sitB", steps = Legs(new Vector3(0.1f, -1, 0.1f), new Vector3(0, -0.3f, -1)) },   // kneeling
        new PoseDef { name = "legsApart", steps = Legs(new Vector3(0.6f, -1, 0), new Vector3(0.6f, -1, 0)) },
        new PoseDef { name = "bendFwd", steps = Spine(Vector3.right, 22) },
        new PoseDef { name = "bendBack", steps = Spine(Vector3.right, -12) },
        new PoseDef { name = "twistL", steps = Seq(Spine(Vector3.up, 15), Arms(new Vector3(0.15f, -0.3f, 1))) },
        new PoseDef { name = "twistR", steps = Seq(Spine(Vector3.up, -15), Arms(new Vector3(0.15f, -0.3f, 1))) },
        new PoseDef { name = "leanL", steps = Spine(Vector3.forward, 12) },
        new PoseDef { name = "leanR", steps = Spine(Vector3.forward, -12) },
        new PoseDef { name = "crouch", steps = Seq(Spine(Vector3.right, 15), Legs(new Vector3(0.1f, -0.6f, 1), new Vector3(0, -1, -0.6f)), Arms(new Vector3(0.15f, -0.3f, 1))) },
    };

    class Poser
    {
        readonly Transform[] all; readonly Vector3[] pos, scale; readonly Quaternion[] rot;
        readonly Animator animator; readonly Transform root;
        public Poser(GameObject avatar)
        {
            all = avatar.GetComponentsInChildren<Transform>(true);
            pos = all.Select(t => t.localPosition).ToArray(); rot = all.Select(t => t.localRotation).ToArray(); scale = all.Select(t => t.localScale).ToArray();
            animator = avatar.GetComponent<Animator>(); root = avatar.transform;
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman) throw new InvalidOperationException("Avatar needs a humanoid Animator");
            // Cache the bones, then stop the Animator: an enabled Animator rewrites the bones to its default pose
            // whenever the preview scene renders, so the poses never reached the renders.
            foreach (HumanBodyBones hb in Enum.GetValues(typeof(HumanBodyBones)))
                if (hb != HumanBodyBones.LastBone) bones[hb] = animator.GetBoneTransform(hb);
            animator.enabled = false;
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
        }
        readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
        public string Describe(GameObject avatar)
        {
            var hips = bones[HumanBodyBones.Hips]; var spine = bones[HumanBodyBones.Spine];
            return $"avatar {animator.avatar.name}; hips {(hips ? RelPath(hips, avatar.transform) : "none")}; spine {(spine ? RelPath(spine, avatar.transform) : "none")}";
        }
        public float Moved()  // largest local rotation change from rest, degrees
        {
            float m = 0; for (int i = 0; i < all.Length; i++) if (all[i]) m = Mathf.Max(m, Quaternion.Angle(all[i].localRotation, rot[i])); return m;
        }
        public void Restore() { for (int i = 0; i < all.Length; i++) if (all[i]) { all[i].localPosition = pos[i]; all[i].localRotation = rot[i]; all[i].localScale = scale[i]; } }
        public void Apply(PoseDef p)
        {
            Restore();
            foreach (var step in p.steps) step.Apply(b => bones.TryGetValue(b, out var t) ? t : null, root);
            if (p.steps.Length > 0 && Moved() < 5f) throw new InvalidOperationException($"Pose {p.name} did not move the skeleton (max {Moved():F1} degrees)");
        }
    }

    static PoseDef PoseByName(string n) => Poses.FirstOrDefault(p => p.name == n) ?? throw new ArgumentException("Unknown pose " + n);

    // ---------- geometry ----------

    static Vector3[] WorldVertices(Renderer r)
    {
        if (r is SkinnedMeshRenderer s) return AvatarMeshGeometry.WorldVertices(s);
        var mesh = MeshOf(r); var m = r.transform.localToWorldMatrix;
        return mesh.vertices.Select(v => m.MultiplyPoint3x4(v)).ToArray();
    }

    class RendererGeo
    {
        public Renderer renderer; public Mesh mesh; public int[] tris; public int[] triSubmesh; public Vector2[] uv;
        public bool[] occludes;          // per triangle: opaque enough to block a view (pose independent)
        public float[] dynamicWeight;    // per vertex: weight on PhysBone-moved bones
        public HashSet<int>[] dynamicBones; // per vertex: which bone indices carry dynamic weight
    }

    static RendererGeo Geo(Renderer r, HashSet<Transform> dynamic, Dictionary<Texture, (Color[] px, int w, int h)> textureCache)
    {
        var mesh = MeshOf(r);
        var g = new RendererGeo { renderer = r, mesh = mesh, uv = mesh.uv };
        var tris = new List<int>(); var sub = new List<int>();
        for (int s = 0; s < mesh.subMeshCount; s++) { var t = mesh.GetTriangles(s); tris.AddRange(t); for (int i = 0; i < t.Length / 3; i++) sub.Add(s); }
        g.tris = tris.ToArray(); g.triSubmesh = sub.ToArray();
        if (g.uv == null || g.uv.Length != mesh.vertexCount) g.uv = new Vector2[mesh.vertexCount];

        // Occlusion by material: transparent never blocks; cutout blocks where the main texture's alpha passes.
        var mats = r.sharedMaterials;
        g.occludes = new bool[g.triSubmesh.Length];
        for (int t = 0; t < g.triSubmesh.Length; t++)
        {
            var mat = g.triSubmesh[t] < mats.Length ? mats[g.triSubmesh[t]] : (mats.Length > 0 ? mats[mats.Length - 1] : null);
            if (mat == null) continue;
            int queue = mat.renderQueue;
            if (queue >= 3000) continue;
            if (queue < 2450 || !mat.HasProperty("_MainTex") || mat.mainTexture == null) { g.occludes[t] = true; continue; }
            float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;
            var tex = mat.mainTexture;
            if (!textureCache.TryGetValue(tex, out var px)) textureCache[tex] = px = ReadTexture(tex);
            Vector2 a = g.uv[g.tris[3 * t]], b = g.uv[g.tris[3 * t + 1]], c = g.uv[g.tris[3 * t + 2]];
            var samples = new[] { (a + b + c) / 3, (a + b) / 2, (b + c) / 2, (c + a) / 2 };
            int pass = samples.Count(uv => Sample(px, uv).a >= cutoff);
            g.occludes[t] = pass >= 3;
        }

        g.dynamicWeight = new float[mesh.vertexCount];
        g.dynamicBones = new HashSet<int>[mesh.vertexCount];
        if (r is SkinnedMeshRenderer skin)
        {
            var bones = skin.bones; int index = 0;
            using (var counts = mesh.GetBonesPerVertex()) using (var weights = mesh.GetAllBoneWeights())
                for (int v = 0; v < mesh.vertexCount; v++)
                    for (int k = 0; k < counts[v]; k++)
                    {
                        var w = weights[index++];
                        if (w.weight <= 0 || w.boneIndex >= bones.Length || bones[w.boneIndex] == null || !dynamic.Contains(bones[w.boneIndex])) continue;
                        g.dynamicWeight[v] += w.weight;
                        (g.dynamicBones[v] ??= new HashSet<int>()).Add(bones[w.boneIndex].GetInstanceID());
                    }
        }
        return g;
    }

    static (Color[] px, int w, int h) ReadTexture(Texture tex)
    {
        int w = Mathf.Min(tex.width, 1024), h = Mathf.Min(tex.height, 1024);
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, rt);
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var t2 = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        t2.ReadPixels(new Rect(0, 0, w, h), 0, 0); t2.Apply();
        RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
        var px = t2.GetPixels(); UnityEngine.Object.DestroyImmediate(t2);
        return (px, w, h);
    }

    static Color Sample((Color[] px, int w, int h) t, Vector2 uv)
    {
        int x = Mathf.Clamp(Mathf.FloorToInt((uv.x - Mathf.Floor(uv.x)) * t.w), 0, t.w - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt((uv.y - Mathf.Floor(uv.y)) * t.h), 0, t.h - 1);
        return t.px[y * t.w + x];
    }

    // Paths whose GameObject activeness or renderer enabled state any playable layer animates.
    static HashSet<string> ToggledPaths(GameObject built)
    {
        var d = built.GetComponent<VRCAvatarDescriptor>();
        var set = new HashSet<string>();
        foreach (var clip in d.baseAnimationLayers.Concat(d.specialAnimationLayers).Where(l => l.animatorController != null)
                     .SelectMany(l => l.animatorController.animationClips).Where(c => c != null).Distinct())
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
                if ((b.type == typeof(GameObject) && b.propertyName == "m_IsActive") || (typeof(Renderer).IsAssignableFrom(b.type) && b.propertyName == "m_Enabled"))
                    set.Add(b.path);
        return set;
    }

    static bool IsToggled(Transform t, Transform root, HashSet<string> toggled)
    {
        for (; t != null && t != root; t = t.parent) if (toggled.Contains(RelPath(t, root))) return true;
        return false;
    }

    static HashSet<Transform> DynamicTransforms(GameObject avatar)
    {
        var set = new HashSet<Transform>();
        foreach (var pb in avatar.GetComponentsInChildren<VRCPhysBoneBase>(true))
        {
            var root = pb.rootTransform != null ? pb.rootTransform : pb.transform;
            var ignored = new HashSet<Transform>((pb.ignoreTransforms ?? new List<Transform>()).Where(t => t != null)
                .SelectMany(t => t.GetComponentsInChildren<Transform>(true)));
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root && !ignored.Contains(t)) set.Add(t);  // the root itself does not move
        }
        return set;
    }

    static Vector3[] Fibonacci(int n)
    {
        var r = new Vector3[n]; float golden = Mathf.PI * (3 - Mathf.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float y = 1 - (i + 0.5f) * 2f / n, rad = Mathf.Sqrt(1 - y * y), th = golden * i;
            r[i] = new Vector3(Mathf.Cos(th) * rad, y, Mathf.Sin(th) * rad);
        }
        return r;
    }

    // ---------- stage: analyze ----------

    static void Analyze(Config cfg)
    {
        var scene = OpenVariant(cfg);
        var avatar = FindAvatar(scene);
        if (!cfg.automaticCuts)
        {
            int removed = 0;
            foreach (var component in avatar.GetComponentsInChildren(FindType(RemoveMeshByMaskType), true))
            {
                var so = new SerializedObject(component); var slots = so.FindProperty("materials");
                int changed = 0;
                for (int i = 0; i < slots.arraySize; i++)
                {
                    var slot = slots.GetArrayElementAtIndex(i); var mask = slot.FindPropertyRelative("mask");
                    string path = AssetDatabase.GetAssetPath(mask.objectReferenceValue);
                    if (!path.StartsWith("Assets/VRUnityGenerated/Optimized/") || !Path.GetFileName(path).EndsWith("-mask.png")) continue;
                    slot.FindPropertyRelative("enabled").boolValue = false; mask.objectReferenceValue = null;
                    changed++; removed++;
                }
                if (changed == 0) continue;
                so.ApplyModifiedPropertiesWithoutUndo();
                bool any = Enumerable.Range(0, slots.arraySize).Any(i => slots.GetArrayElementAtIndex(i).FindPropertyRelative("enabled").boolValue);
                bool manualConfigured = Enumerable.Range(0, slots.arraySize).Any(i => slots.GetArrayElementAtIndex(i).FindPropertyRelative("mask").objectReferenceValue != null);
                Note("Removed generated automatic cut on " + component.name);
                if (!any && !manualConfigured) UnityEngine.Object.DestroyImmediate(component);
            }
            if (removed > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Save failed");
            }
            Note($"Automatic visibility cuts disabled; removed {removed} generated mask slots; manual cutters retained");
            WriteReport(cfg, "analyze", new AnalyzeReport { scene = cfg.variant, cuts = new CutReport[0], notes = notes.ToArray(), removedAutomaticMaskSlots = removed });
            return;
        }
        var copy = BuildCopy(avatar, StripOptimizers);
        var report = new AnalyzeReport { scene = cfg.variant };
        var cuts = new List<CutReport>();
        var masksByTarget = new Dictionary<string, (Texture2D[] masks, int submeshes)>();
        try
        {
            var dynamic = DynamicTransforms(copy);
            var textureCache = new Dictionary<Texture, (Color[] px, int w, int h)>();
            var renderers = copy.GetComponentsInChildren<Renderer>(true)
                .Where(r => (r is SkinnedMeshRenderer || r is MeshRenderer) && r.enabled && r.gameObject.activeInHierarchy && MeshOf(r) != null)
                .ToArray();
            var geos = renderers.Select(r => Geo(r, dynamic, textureCache)).ToArray();
            Note($"Analysis copy: {renderers.Length} visible renderers, {dynamic.Count} PhysBone-moved transforms");
            var poser = new Poser(copy);
            var directions = Fibonacci(160);

            // Renderers a menu can hide must never count as cover.
            var toggled = ToggledPaths(copy);
            var toggleable = new HashSet<RendererGeo>(geos.Where(g => IsToggled(g.renderer.transform, copy.transform, toggled)));
            Note($"Toggleable renderers (not used as cover): {string.Join(", ", toggleable.Select(g => RelPath(g.renderer.transform, copy.transform)))}");
            var keepFull = new HashSet<string>(cfg.keepFull);
            // Never cut the face: talking and expressions reveal the mouth and other parts hidden at rest.
            var faceDescriptor = copy.GetComponent<VRCAvatarDescriptor>();
            foreach (var face in new Renderer[] { faceDescriptor.VisemeSkinnedMesh, faceDescriptor.customEyeLookSettings.eyelidsSkinnedMesh }.Where(r => r != null))
                keepFull.Add(RelPath(face.transform, copy.transform));
            var targets = (cfg.cut.Length > 0
                ? cfg.cut.Select(path =>
                {
                    var t = Find(copy.transform, path).GetComponent<SkinnedMeshRenderer>() ?? throw new ArgumentException(path + " is not a skinned mesh");
                    var g = geos.FirstOrDefault(x => x.renderer == t) ?? throw new ArgumentException(path + " is not visible in the default state");
                    return (path, g);
                })
                : geos.Where(g => g.renderer is SkinnedMeshRenderer && !toggleable.Contains(g))
                    .Select(g => (path: RelPath(g.renderer.transform, copy.transform), g))
                    .Where(x => !keepFull.Contains(x.path) && avatar.transform.Find(x.path) != null)).ToArray();
            Note($"Cut candidates ({(cfg.cut.Length > 0 ? "configured" : "automatic")}): {string.Join(", ", targets.Select(x => x.path))}");

            // Per pose: world vertices of every renderer.
            var posed = new Dictionary<string, Vector3[][]>();
            foreach (var pose in Poses)
            {
                poser.Apply(pose);
                posed[pose.name] = geos.Select(g => WorldVertices(g.renderer)).ToArray();
            }
            poser.Restore();

            foreach (var (path, target) in targets)
            {
                int ti = Array.IndexOf(geos, target);
                int triCount = target.tris.Length / 3;
                // Bones the target itself follows: occluders moved by those bones move with it.
                var targetBones = new HashSet<int>(target.dynamicBones.Where(b => b != null).SelectMany(b => b));
                var visible = new bool[triCount];
                var newly = new List<string>();
                int occluderTris = 0;

                // Outward orientation of the target's winding, from the rest pose.
                var rest = posed["rest"][ti];
                var center = rest.Aggregate(Vector3.zero, (a, v) => a + v) / rest.Length;
                int outward = 0;
                for (int t = 0; t < triCount; t++)
                {
                    Vector3 a = rest[target.tris[3 * t]], b = rest[target.tris[3 * t + 1]], c = rest[target.tris[3 * t + 2]];
                    if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3 - center) > 0) outward++;
                }
                float sign = outward >= triCount / 2 ? 1 : -1;
                Note($"{path}: {triCount} triangles; winding sign {sign} ({outward} of {triCount} face away from the centre)");

                foreach (var pose in Poses)
                {
                    var verts = posed[pose.name];
                    // Occluder mesh: the target itself plus opaque triangles of other renderers that are not
                    // swung by PhysBones the target doesn't share.
                    var ov = new List<Vector3>(); var ot = new List<int>();
                    for (int gi = 0; gi < geos.Length; gi++)
                    {
                        var g = geos[gi]; var vs = verts[gi]; int baseIndex = ov.Count; ov.AddRange(vs);
                        for (int t = 0; t < g.tris.Length / 3; t++)
                        {
                            if (gi == ti)
                            {
                                // The target hides itself only with parts that don't swing on PhysBones.
                                if (Enumerable.Range(0, 3).Any(k => g.dynamicWeight[g.tris[3 * t + k]] > 0.1f)) continue;
                            }
                            else
                            {
                                if (!g.occludes[t] || toggleable.Contains(g)) continue;
                                bool loose = false;
                                for (int k = 0; k < 3 && !loose; k++)
                                {
                                    int v = g.tris[3 * t + k];
                                    if (g.dynamicWeight[v] > 0.1f && g.dynamicBones[v].Any(b => !targetBones.Contains(b))) loose = true;
                                }
                                if (loose) continue;
                            }
                            ot.Add(baseIndex + g.tris[3 * t]); ot.Add(baseIndex + g.tris[3 * t + 1]); ot.Add(baseIndex + g.tris[3 * t + 2]);
                        }
                    }
                    occluderTris = Math.Max(occluderTris, ot.Count / 3 - triCount);
                    var occMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    occMesh.SetVertices(ov); occMesh.SetTriangles(ot, 0);
                    var occGo = new GameObject("VRUnity occluder") { layer = RayLayer };
                    var collider = occGo.AddComponent<MeshCollider>(); collider.sharedMesh = occMesh;
                    Physics.SyncTransforms();
                    int before = visible.Count(x => x);
                    try { CastRays(target, verts[ti], sign, visible, directions); }
                    finally { UnityEngine.Object.DestroyImmediate(occGo); UnityEngine.Object.DestroyImmediate(occMesh); }
                    int after = visible.Count(x => x);
                    newly.Add($"{pose.name}: +{after - before} (visible {after})");
                    Log($"{path} pose {pose.name}: visible {after}/{triCount}");
                }

                foreach (var protection in cfg.cutBoneProtection.Where(p => p.renderer == path))
                    Note($"{path}: kept {ProtectCutBones(target, copy, protection.bones, visible)} additional triangles for cut-protected bones");
                var keep = Grow(target, rest, visible, cfg.margin);
                for (int t = 0; t < triCount; t++)   // never remove surface that swings on PhysBones
                    if (Enumerable.Range(0, 3).Any(k => target.dynamicWeight[target.tris[3 * t + k]] > 0.1f)) keep[t] = true;
                int removable = keep.Count(k => !k);
                int maskRemoved = 0;
                var masks = new Texture2D[target.mesh.subMeshCount];
                if (removable >= cfg.minCut) masks = BuildMasks(cfg, path, target, keep, out maskRemoved);
                if (removable >= cfg.minCut && maskRemoved < cfg.minCut)
                {
                    foreach (var m in masks.Where(m => m != null)) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(m));
                    masks = new Texture2D[target.mesh.subMeshCount];
                    Note($"{path}: mask would remove only {maskRemoved} triangles; skipped");
                    maskRemoved = 0;
                }
                masksByTarget[path] = (masks, target.mesh.subMeshCount);
                cuts.Add(new CutReport
                {
                    path = path, triangles = triCount, visible = visible.Count(x => x), removable = removable, maskRemoved = maskRemoved,
                    occluderTriangles = occluderTris, masks = masks.Where(m => m != null).Select(AssetDatabase.GetAssetPath).ToArray(),
                    poseNewlyVisible = newly.ToArray()
                });
                Note($"{path}: visible {visible.Count(x => x)}, removable after {cfg.margin} m margin {removable}, removed by the mask rule {maskRemoved}");
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(copy); }

        // Components on the variant (replacing any earlier run's).
        var removeType = FindType(RemoveMeshByMaskType);
        foreach (var old in avatar.GetComponentsInChildren(removeType, true))
            if (!masksByTarget.ContainsKey(RelPath(old.transform, avatar.transform))) { Note("Removing earlier cut on " + old.name); UnityEngine.Object.DestroyImmediate(old); }
        foreach (var path in masksByTarget.Keys)
        {
            var go = Find(avatar.transform, path).gameObject;
            foreach (var old in go.GetComponents(removeType)) UnityEngine.Object.DestroyImmediate(old);
            var (masks, submeshes) = masksByTarget[path];
            if (masks.All(m => m == null)) { Note($"{path}: nothing to remove; no component added"); continue; }
            var comp = go.AddComponent(removeType);
            removeType.GetMethod("Initialize").Invoke(comp, new object[] { 1 });
            var so = new SerializedObject(comp);
            var arr = so.FindProperty("materials");
            arr.arraySize = submeshes;
            for (int s = 0; s < submeshes; s++)
            {
                var e = arr.GetArrayElementAtIndex(s);
                e.FindPropertyRelative("enabled").boolValue = masks[s] != null;
                e.FindPropertyRelative("mask").objectReferenceValue = masks[s];
                e.FindPropertyRelative("mode").enumValueIndex = 1; // RemoveWhite
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            Note($"{path}: AAO Remove Mesh By Mask (white removes) on {masks.Count(m => m != null)} of {submeshes} material slots");
        }
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Save failed");
        report.cuts = cuts.ToArray(); report.notes = notes.ToArray();
        WriteReport(cfg, "analyze", report);
    }

    static void CastRays(RendererGeo target, Vector3[] verts, float sign, bool[] visible, Vector3[] directions)
    {
        var query = new QueryParameters(1 << RayLayer, false, QueryTriggerInteraction.Ignore, true);
        const float offset = 0.0005f, distance = 5f;
        var pending = Enumerable.Range(0, visible.Length).Where(t => !visible[t]).ToArray();
        const int chunk = 3000;
        var sampleDirs = new List<Vector3>();
        for (int start = 0; start < pending.Length; start += chunk)
        {
            var batch = pending.Skip(start).Take(chunk).ToArray();
            var commands = new List<RaycastCommand>(); var owner = new List<int>();
            foreach (var t in batch)
            {
                Vector3 a = verts[target.tris[3 * t]], b = verts[target.tris[3 * t + 1]], c = verts[target.tris[3 * t + 2]];
                var cross = Vector3.Cross(b - a, c - a);
                if (cross.sqrMagnitude < 1e-14f) continue;   // degenerate: decided by its neighbours' margin
                var n = cross.normalized * sign;
                var g = (a + b + c) / 3;
                var points = new[] { g, g + 0.66f * (a - g), g + 0.66f * (b - g), g + 0.66f * (c - g) };
                foreach (var d in directions)
                {
                    if (Vector3.Dot(d, n) < 0.15f) continue;
                    foreach (var p in points) { commands.Add(new RaycastCommand(p + n * offset, d, query, distance)); owner.Add(t); }
                }
            }
            if (commands.Count == 0) continue;
            using (var cmd = new NativeArray<RaycastCommand>(commands.ToArray(), Allocator.TempJob))
            using (var hits = new NativeArray<RaycastHit>(commands.Count, Allocator.TempJob))
            {
                RaycastCommand.ScheduleBatch(cmd, hits, 256, 1).Complete();
                for (int i = 0; i < hits.Length; i++) if (hits[i].distance <= 0f) visible[owner[i]] = true;
            }
        }
    }

    // Keep every triangle within `margin` (surface distance, rest pose) of a visible one.
    static bool[] Grow(RendererGeo g, Vector3[] rest, bool[] visible, float margin)
    {
        int vc = rest.Length; var weld = new int[vc]; var key = new Dictionary<Vector3Int, int>();
        for (int v = 0; v < vc; v++)
        {
            var k = Vector3Int.RoundToInt(rest[v] * 1e5f);
            if (!key.TryGetValue(k, out var id)) key[k] = id = key.Count;
            weld[v] = id;
        }
        int n = key.Count; var pos = new Vector3[n]; for (int v = 0; v < vc; v++) pos[weld[v]] = rest[v];
        var adj = new List<int>[n]; for (int i = 0; i < n; i++) adj[i] = new List<int>();
        int triCount = g.tris.Length / 3;
        for (int t = 0; t < triCount; t++)
            for (int k = 0; k < 3; k++) { int a = weld[g.tris[3 * t + k]], b = weld[g.tris[3 * t + (k + 1) % 3]]; adj[a].Add(b); adj[b].Add(a); }
        var dist = Enumerable.Repeat(float.PositiveInfinity, n).ToArray();
        var queue = new SortedSet<(float, int)>();
        for (int t = 0; t < triCount; t++) if (visible[t]) for (int k = 0; k < 3; k++) { int a = weld[g.tris[3 * t + k]]; if (dist[a] > 0) { dist[a] = 0; queue.Add((0, a)); } }
        while (queue.Count > 0)
        {
            var (d, a) = queue.Min; queue.Remove(queue.Min);
            if (d > margin) break;
            foreach (var b in adj[a])
            {
                float nd = d + Vector3.Distance(pos[a], pos[b]);
                if (nd < dist[b]) { if (!float.IsPositiveInfinity(dist[b])) queue.Remove((dist[b], b)); dist[b] = nd; queue.Add((nd, b)); }
            }
        }
        var keep = new bool[triCount];
        for (int t = 0; t < triCount; t++) keep[t] = visible[t] || Enumerable.Range(0, 3).Any(k => dist[weld[g.tris[3 * t + k]]] <= margin);
        return keep;
    }

    static int ProtectCutBones(RendererGeo target, GameObject avatar, string[] names, bool[] visible)
    {
        if (!(target.renderer is SkinnedMeshRenderer skin)) throw new ArgumentException("Cut bone protection needs a skinned renderer");
        var animator = avatar.GetComponent<Animator>();
        var roots = names.Select(n => animator.GetBoneTransform((HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), n)))
            .Where(t => t != null).ToArray();
        if (roots.Length != names.Length) throw new ArgumentException("A cut-protected humanoid bone is missing");
        var protectBone = skin.bones.Select(b => b != null && roots.Any(root => b == root || b.IsChildOf(root))).ToArray();
        var protectVertex = new bool[target.mesh.vertexCount]; int index = 0;
        using (var counts = target.mesh.GetBonesPerVertex()) using (var weights = target.mesh.GetAllBoneWeights())
            for (int v = 0; v < protectVertex.Length; v++)
                for (int k = 0; k < counts[v]; k++)
                {
                    var weight = weights[index++];
                    if (weight.weight > .001f && weight.boneIndex < protectBone.Length && protectBone[weight.boneIndex]) protectVertex[v] = true;
                }
        int kept = 0;
        for (int t = 0; t < visible.Length; t++)
            if (Enumerable.Range(0, 3).Any(k => protectVertex[target.tris[3 * t + k]]))
            { if (!visible[t]) kept++; visible[t] = true; }
        return kept;
    }

    // White = remove. Kept triangles are painted black over it, grown by 2 px, so overlapping
    // (e.g. mirrored) UVs and small post-decimation UV shifts err towards keeping.
    static Texture2D[] BuildMasks(Config cfg, string path, RendererGeo g, bool[] keep, out int maskRemoved)
    {
        int W = cfg.maskSize, subs = g.mesh.subMeshCount; var result = new Texture2D[subs]; maskRemoved = 0;
        Directory.CreateDirectory(cfg.assetDir);
        for (int s = 0; s < subs; s++)
        {
            var remove = new bool[W * W]; var kept = new bool[W * W]; bool any = false;
            for (int t = 0; t < g.triSubmesh.Length; t++)
            {
                if (g.triSubmesh[t] != s) continue;
                Vector2 a = g.uv[g.tris[3 * t]], b = g.uv[g.tris[3 * t + 1]], c = g.uv[g.tris[3 * t + 2]];
                if (keep[t]) { FillTri(kept, W, a, b, c, 1.0f); foreach (var p in new[] { a, b, c }) kept[Pixel(p, W)] = true; }
                else { FillTri(remove, W, a, b, c, 0f); any = true; }
            }
            if (!any) continue;
            kept = Dilate(kept, W, 2);
            var px = new Color32[W * W]; int white = 0;
            for (int i = 0; i < px.Length; i++) { bool w = remove[i] && !kept[i]; px[i] = w ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255); if (w) white++; }
            for (int t = 0; t < g.triSubmesh.Length; t++)
            {
                if (g.triSubmesh[t] != s) continue;
                if (Enumerable.Range(0, 3).All(k => px[Pixel(g.uv[g.tris[3 * t + k]], W)].r > 127)) maskRemoved++;
            }
            if (white == 0) continue;
            var file = $"{cfg.assetDir}/{SafeName(path)}-slot{s}-mask.png";
            var tex = new Texture2D(W, W, TextureFormat.RGB24, false, true); tex.SetPixels32(px); tex.Apply();
            File.WriteAllBytes(file, tex.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(file, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(file);
            imp.isReadable = true; imp.mipmapEnabled = false; imp.sRGBTexture = false; imp.filterMode = FilterMode.Point;
            imp.textureCompression = TextureImporterCompression.Uncompressed; imp.npotScale = TextureImporterNPOTScale.None; imp.maxTextureSize = Math.Max(2048, W);
            imp.SaveAndReimport();
            result[s] = AssetDatabase.LoadAssetAtPath<Texture2D>(file);
            File.Copy(file, Path.Combine(cfg.reportDir, Path.GetFileName(file)), true);
        }
        return result;
    }

    // AAO moves renderers whose parents it removes to the avatar root, named like
    // "Outfit$Jacket$30"; map those back to their scene path.
    static string SourcePath(string builtPath)
    {
        if (!builtPath.Contains("$")) return builtPath;
        var parts = builtPath.Split('$').ToList();
        if (parts.Count > 1 && int.TryParse(parts[parts.Count - 1], out _)) parts.RemoveAt(parts.Count - 1);
        return string.Join("/", parts);
    }

    // Every serialized PhysBone property that a merge requires to match, at full precision. Excludes what
    // AAO Merge PhysBone handles per chain (root, ignores, endpoint via Clear, colliders keyed separately)
    // and editor display state.
    static readonly HashSet<string> MergeExempt = new HashSet<string>
        { "rootTransform", "ignoreTransforms", "endpointPosition", "colliders", "multiChildType", "showGizmos", "boneOpacity", "limitOpacity" };
    static string SettingsKey(VRCPhysBoneBase pb)
    {
        var so = new SerializedObject(pb); var it = so.GetIterator(); var parts = new List<string>();
        bool enter = true;
        while (it.Next(enter))
        {
            enter = false;
            var name = it.propertyPath.Split('.')[0];
            if (it.depth == 0 && (name.StartsWith("m_") || MergeExempt.Contains(name) || name.StartsWith("foldout"))) continue;
            switch (it.propertyType)
            {
                case SerializedPropertyType.Float: parts.Add($"{it.propertyPath}={it.floatValue:R}"); break;
                case SerializedPropertyType.Integer: case SerializedPropertyType.Enum: parts.Add($"{it.propertyPath}={it.longValue}"); break;
                case SerializedPropertyType.Boolean: parts.Add($"{it.propertyPath}={it.boolValue}"); break;
                case SerializedPropertyType.String: parts.Add($"{it.propertyPath}={it.stringValue}"); break;
                case SerializedPropertyType.Vector3: parts.Add($"{it.propertyPath}={it.vector3Value.x:R},{it.vector3Value.y:R},{it.vector3Value.z:R}"); break;
                case SerializedPropertyType.AnimationCurve:
                    parts.Add($"{it.propertyPath}=" + string.Join(";", (it.animationCurveValue?.keys ?? new Keyframe[0]).Select(k => $"{k.time:R}:{k.value:R}:{k.inTangent:R}:{k.outTangent:R}")));
                    break;
                case SerializedPropertyType.ObjectReference: break;  // references are keyed separately
                default: enter = it.hasVisibleChildren; break;      // structs: descend into their fields
            }
        }
        return string.Join("|", parts);
    }

    // The physics-relevant PhysBone values, for deciding which can share one merged component.
    static string SettingsText(VRCPhysBoneBase pb)
    {
        var names = new[] { "version", "integrationType", "pull", "spring", "stiffness", "gravity", "gravityFalloff", "immobileType", "immobile",
            "limitType", "maxAngleX", "maxAngleZ", "radius", "allowCollision", "allowGrabbing", "allowPosing", "grabMovement", "snapToHand",
            "maxStretch", "maxSquish", "stretchMotion", "endpointPosition", "isAnimated", "resetWhenDisabled", "parameter" };
        var parts = new List<string>();
        foreach (var n in names)
        {
            var f = typeof(VRCPhysBoneBase).GetField(n);
            if (f == null) continue;
            var v = f.GetValue(pb);
            parts.Add(n + "=" + (v is float x ? x.ToString("0.###") : v?.ToString()));
            var curve = typeof(VRCPhysBoneBase).GetField(n + "Curve")?.GetValue(pb) as AnimationCurve;
            if (curve != null && curve.length > 0) parts.Add(n + "Curve=" + string.Join(",", curve.keys.Select(k => $"{k.time:0.##}:{k.value:0.###}")));
        }
        return string.Join(" ", parts);
    }

    static string SafeName(string path) => new string(path.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
    static int Wrap(int x, int w) => ((x % w) + w) % w;
    static int Pixel(Vector2 uv, int W) => Wrap(Mathf.FloorToInt(uv.y * W), W) * W + Wrap(Mathf.FloorToInt(uv.x * W), W);

    static void FillTri(bool[] m, int W, Vector2 a, Vector2 b, Vector2 c, float grow)
    {
        var off = new Vector2(Mathf.Floor((a.x + b.x + c.x) / 3), Mathf.Floor((a.y + b.y + c.y) / 3));
        a = (a - off) * W; b = (b - off) * W; c = (c - off) * W;
        float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (Mathf.Abs(area) < 1e-6f) return;
        if (area < 0) { var tmp = b; b = c; c = tmp; }
        int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - grow - 1), x1 = Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + grow + 1);
        int y0 = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - grow - 1), y1 = Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + grow + 1);
        float Edge(Vector2 p, Vector2 q, Vector2 r) { var e = q - p; return (e.x * (r.y - p.y) - e.y * (r.x - p.x)) / e.magnitude; }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                if (Edge(a, b, p) >= -grow && Edge(b, c, p) >= -grow && Edge(c, a, p) >= -grow) m[Wrap(y, W) * W + Wrap(x, W)] = true;
            }
    }

    static bool[] Dilate(bool[] m, int W, int r)
    {
        var o = (bool[])m.Clone();
        for (int y = 0; y < W; y++) for (int x = 0; x < W; x++)
        {
            if (!m[y * W + x]) continue;
            for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) o[Wrap(y + dy, W) * W + Wrap(x + dx, W)] = true;
        }
        return o;
    }

    // ---------- stage: physbones ----------

    static void PhysBones(Config cfg)
    {
        var scene = OpenVariant(cfg);
        var avatar = FindAvatar(scene);
        ApplyPhysBoneMerges(avatar, cfg.mergePhysBones);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Save failed");
        WriteReport(cfg, "physbones", new SimpleReport { stage = "physbones", scene = cfg.variant, notes = notes.ToArray() });
    }

    const string AutoMergeSuffix = " PhysBones (auto merged)";

    // Merge PhysBones with identical settings until the built count fits the limit. Groups need the same
    // parent of their roots, identical settings apart from endpoint position, and (first pass) the same
    // chain length. Largest groups first; each merged component only allows one grab at a time.
    static void AutoPhysBones(Config cfg)
    {
        var probe = JsonUtility.FromJson<StatsReport>(File.ReadAllText(Path.Combine(cfg.reportDir, "probe.json")));
        var scene = OpenVariant(cfg);
        var avatar = FindAvatar(scene); var root = avatar.transform;
        var mergeType = FindType("Anatawa12.AvatarOptimizer.MergePhysBone");
        // Earlier automatic merges are rebuilt from scratch; their sources are counted again below.
        var earlier = avatar.GetComponentsInChildren(mergeType, true).Where(c => c.name.EndsWith(AutoMergeSuffix)).ToArray();
        int count = probe.physBoneComponents + probe.autoMergeReduction;
        foreach (var c in earlier) UnityEngine.Object.DestroyImmediate(c.gameObject);
        var merges = new List<PhysBoneMerge>();
        int need = count - cfg.physBoneLimit;
        Note($"PhysBones built without automatic merges: {count}; limit {cfg.physBoneLimit}; need to remove {Math.Max(need, 0)}");
        if (need > 0)
        {
            var manual = new HashSet<VRCPhysBoneBase>(avatar.GetComponentsInChildren(mergeType, true)
                .SelectMany(c => (ICollection<VRCPhysBoneBase>)mergeType.GetProperty("PhysBones").GetValue(c)));
            var candidates = avatar.GetComponentsInChildren<VRCPhysBoneBase>(true)
                .Where(pb => pb.enabled && pb.gameObject.activeInHierarchy && !manual.Contains(pb)
                    && pb.GetComponents<VRCPhysBoneBase>().Length == 1
                    && (pb.ignoreTransforms == null || pb.ignoreTransforms.All(t => t == null))
                    && string.IsNullOrEmpty(pb.parameter))  // isAnimated must merely match (it is in the settings key)
                .Select(pb =>
                {
                    var r = pb.rootTransform != null ? pb.rootTransform : pb.transform;
                    var settings = SettingsKey(pb);
                    var colliders = string.Join(",", (pb.colliders ?? new List<VRCPhysBoneColliderBase>()).Where(c => c != null).Select(c => c.GetInstanceID()).OrderBy(x => x));
                    return (pb, parent: r.parent, chain: r.GetComponentsInChildren<Transform>(true).Length, key: settings + "|" + colliders);
                })
                .Where(x => x.parent != null && x.parent != root).ToList();
            foreach (var g in candidates.GroupBy(x => x.parent).OrderByDescending(g => g.Count()))
                Log($"PhysBone candidates under {g.Key.name}: " + string.Join("; ", g.GroupBy(x => (x.key, x.chain)).Select(k => $"{k.Count()}x chain {k.Key.chain} key {Hash128.Compute(k.Key.key).ToString().Substring(0, 6)} [{string.Join(",", k.Select(x => x.pb.name))}]")));
            foreach (var pb in avatar.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(pb => !candidates.Any(c => c.pb == pb)))
                Log($"Not merged automatically: {pb.name}: " + string.Join(" ", new[]
                {
                    !pb.enabled || !pb.gameObject.activeInHierarchy ? "inactive" : null, manual.Contains(pb) ? "manually merged" : null,
                    pb.GetComponents<VRCPhysBoneBase>().Length != 1 ? "shares its object" : null,
                    pb.ignoreTransforms != null && pb.ignoreTransforms.Any(t => t != null) ? "has ignore transforms" : null,
                    !string.IsNullOrEmpty(pb.parameter) ? "has parameter " + pb.parameter : null,
                    (pb.rootTransform != null ? pb.rootTransform : pb.transform).parent == root ? "root is the avatar" : null,
                }.Where(x => x != null)));
            var used = new HashSet<VRCPhysBoneBase>();
            foreach (bool strict in new[] { true, false })
            {
                var groups = candidates.Where(x => !used.Contains(x.pb))
                    .GroupBy(x => (x.parent, x.key, chain: strict ? x.chain : 0))
                    .Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).ToList();
                foreach (var g in groups)
                {
                    if (need <= 0) break;
                    var members = g.Select(x => x.pb).ToList();
                    foreach (var pb in members) used.Add(pb);
                    need -= members.Count - 1;
                    merges.Add(new PhysBoneMerge
                    {
                        name = $"{g.Key.parent.name} {merges.Count + 1}{AutoMergeSuffix}",
                        parent = RelPath(g.Key.parent, root),
                        sources = members.Select(pb => RelPath(pb.transform, root)).ToArray(),
                    });
                    Note($"Auto merge ({(strict ? "same chain length" : "chain lengths differ")}): {members.Count} under {RelPath(g.Key.parent, root)}: {string.Join(", ", members.Select(pb => pb.name))}");
                }
            }
            if (need > 0) Note($"Still {need} PhysBones over the limit after merging every identical group; needs a decision (different settings).");
        }
        ApplyPhysBoneMerges(avatar, merges.ToArray());
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Save failed");
        WriteReport(cfg, "autophysbones", new SimpleReport { stage = "autophysbones", scene = cfg.variant, notes = notes.ToArray() });
        if (need > 0) throw new InvalidOperationException("PhysBone limit not reached automatically");
    }

    static void ApplyPhysBoneMerges(GameObject avatar, PhysBoneMerge[] merges)
    {
        var root = avatar.transform;
        var mergeType = FindType("Anatawa12.AvatarOptimizer.MergePhysBone");
        foreach (var m in merges)
        {
            var parent = Find(root, m.parent);
            var old = parent.Find(m.name);
            if (old != null)
            {
                if (old.childCount != 0) throw new InvalidOperationException($"{m.name} already has children (an earlier build?)");
                UnityEngine.Object.DestroyImmediate(old.gameObject);
            }
            var sources = m.sources.Select(p => Find(root, p).GetComponents<VRCPhysBoneBase>().Single()).ToArray();
            var settings = sources.Select(SettingsKey).Distinct().ToArray();
            if (settings.Length != 1) throw new InvalidOperationException($"{m.name}: sources differ in more than endpoint position:\n" + string.Join("\n", settings));
            var go = new GameObject(m.name);
            go.transform.SetParent(parent, false);
            var comp = go.AddComponent(mergeType);
            mergeType.GetMethod("Initialize").Invoke(comp, new object[] { 1 });
            mergeType.GetProperty("MakeParent").SetValue(comp, true);
            var set = (ICollection<VRCPhysBoneBase>)mergeType.GetProperty("PhysBones").GetValue(comp);
            foreach (var pb in sources) set.Add(pb);
            var so = new SerializedObject(comp);
            so.FindProperty("endpointPositionConfig.override").enumValueIndex = 0; // Clear: endpoint positions become end bones
            so.ApplyModifiedPropertiesWithoutUndo();
            Note($"{m.name}: merges {sources.Length} PhysBones under {m.parent} (endpoint positions cleared to end bones)");
        }
    }

    // ---------- stages: probe / stats ----------

    static void Stats(Config cfg, string stage)
    {
        if (stage == "stats")
        {
            var sourceScene = EditorSceneManager.OpenScene(cfg.source, OpenSceneMode.Single);
            var sourceCopy = BuildCopy(FindAvatar(sourceScene), null);
            try { WriteReport(cfg, "stats-source", Measure(sourceCopy, "stats-source", cfg.source)); }
            finally { UnityEngine.Object.DestroyImmediate(sourceCopy); }
        }
        var scene = OpenVariant(cfg);
        var avatar = FindAvatar(scene);
        var source = avatar.GetComponentsInChildren<Renderer>(true).GroupBy(r => RelPath(r.transform, avatar.transform))
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? Triangles(MeshOf(g.First())) : -1);  // -1: ambiguous path
        // PhysBones that automatic merges present in the scene already save (autophysbones rebuilds them).
        var mergeType = FindType("Anatawa12.AvatarOptimizer.MergePhysBone");
        int autoMergeReduction = avatar.GetComponentsInChildren(mergeType, true).Where(c => c.name.EndsWith(AutoMergeSuffix))
            .Sum(c => ((ICollection<VRCPhysBoneBase>)mergeType.GetProperty("PhysBones").GetValue(c)).Count - 1);
        Action<GameObject> prepare = stage == "probe" ? (copy => { NoAutoMerge(copy); NoDecimation(copy); }) : (Action<GameObject>)null;
        if (stage == "breakdown") prepare = NoAutoMerge;
        var copy = BuildCopy(avatar, prepare);
        try
        {
            var report = Measure(copy, stage, cfg.variant);
            report.autoMergeReduction = autoMergeReduction;
            foreach (var r in report.renderers)
            {
                r.sourcePath = SourcePath(r.path);
                r.sourceTriangles = source.TryGetValue(r.sourcePath, out var n) ? n : -1;
            }
            report.notes = notes.Concat(stage == "probe" ? new[] { "Probe: Meshia removed and AAO automatic mesh merging off on the copy; cuts and other AAO steps applied." } : new string[0]).ToArray();
            if (stage == "breakdown") report.notes = notes.Concat(new[] { "Breakdown: AAO automatic mesh merging disabled on the copy only; decimation and manual cuts retained. Renderer counts/rating are diagnostic, not upload counts." }).ToArray();
            WriteReport(cfg, stage, report);
            Log($"{stage}: {report.polyCount} triangles, {report.skinnedMeshCount} skinned meshes, {report.materialCount} materials, {report.physBoneComponents} PhysBones, overall {report.overall}");
        }
        finally { UnityEngine.Object.DestroyImmediate(copy); }
    }

    static StatsReport Measure(GameObject avatar, string stage, string scene)
    {
        var constraints = avatar.GetComponentsInChildren<VRC.Dynamics.VRCConstraintBase>(true);
        typeof(VRC.Dynamics.VRCConstraintBase).Assembly.GetTypes().FirstOrDefault(t => t.Name == "VRCConstraintManager")
            ?.GetMethod("Sdk_ManuallyRefreshGroups", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(null, new object[] { constraints });
        AvatarPerformanceStats.Initialize(); // rating thresholds, as the SDK loads them in the editor
        var stats = new AvatarPerformanceStats(false);
        AvatarPerformance.CalculatePerformanceStats(avatar.name, avatar, stats, false);
        object pb = typeof(AvatarPerformanceStats).GetField("physBone")?.GetValue(stats);
        int PB(string f) { if (pb == null) return -1; var v = pb.GetType().GetField(f)?.GetValue(pb) ?? pb.GetType().GetProperty(f)?.GetValue(pb); return v is int i ? i : -1; }
        var report = new StatsReport
        {
            stage = stage, scene = scene,
            polyCount = stats.polyCount ?? -1, skinnedMeshCount = stats.skinnedMeshCount ?? -1, meshCount = stats.meshCount ?? -1,
            materialCount = stats.materialCount ?? -1, boneCount = stats.boneCount ?? -1, contactCount = stats.contactCount ?? -1,
            textureMegabytes = stats.textureMegabytes ?? -1,
            physBoneComponents = PB("componentCount"), physBoneTransforms = PB("transformCount"),
            physBoneColliders = PB("colliderCount"), physBoneCollisionChecks = PB("collisionCheckCount"),
            overall = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall).ToString(),
        };
        var ratings = new List<Rating>();
        foreach (AvatarPerformanceCategory c in Enum.GetValues(typeof(AvatarPerformanceCategory)))
        {
            try { var r = stats.GetPerformanceRatingForCategory(c); if (r.ToString() != "None") ratings.Add(new Rating { category = c.ToString(), rating = r.ToString() }); }
            catch (Exception) { }
        }
        report.ratings = ratings.ToArray();
        report.renderers = avatar.GetComponentsInChildren<Renderer>(true).Where(r => r is SkinnedMeshRenderer || r is MeshRenderer)
            .Select(r => new RendererInfo
            {
                path = RelPath(r.transform, avatar.transform), kind = r is SkinnedMeshRenderer ? "skinned" : "mesh",
                triangles = Triangles(MeshOf(r)), materials = r.sharedMaterials.Length, active = r.gameObject.activeInHierarchy && r.enabled
            }).OrderByDescending(r => r.triangles).ToArray();
        // Every animation binding in the built playable layers should still find its target.
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        var clips = descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers)
            .Where(l => l.animatorController != null).SelectMany(l => l.animatorController.animationClips).Where(c => c != null).Distinct().ToArray();
        var unresolved = new SortedSet<string>(); int bindings = 0;
        foreach (var clip in clips)
            foreach (var b in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
            {
                if (b.type == typeof(Animator)) continue;  // animator parameters
                bindings++;
                var target = avatar.transform.Find(b.path);
                bool ok = target != null && (b.type == typeof(GameObject) || b.type == typeof(Transform) || target.GetComponent(b.type) != null);
                if (ok && b.propertyName.StartsWith("blendShape.") && target.GetComponent<SkinnedMeshRenderer>() is SkinnedMeshRenderer smr)
                    ok = smr.sharedMesh != null && smr.sharedMesh.GetBlendShapeIndex(b.propertyName.Substring(11)) >= 0;
                if (!ok) unresolved.Add($"{b.path} {b.type.Name}.{b.propertyName}");
            }
        report.animationBindings = bindings; report.unresolvedBindings = unresolved.Count; report.unresolved = unresolved.Take(2000).ToArray();
        report.parameters = descriptor.expressionParameters != null
            ? descriptor.expressionParameters.parameters.Where(p => !string.IsNullOrEmpty(p.name)).Select(p => $"{p.name}:{p.valueType}:{(p.saved ? "saved" : "")}:{p.networkSynced}:{p.defaultValue}").OrderBy(x => x).ToArray()
            : new string[0];
        report.physBones = avatar.GetComponentsInChildren<VRCPhysBoneBase>(true).Select(pb =>
        {
            var root = pb.rootTransform != null ? pb.rootTransform : pb.transform;
            var ignored = new HashSet<Transform>((pb.ignoreTransforms ?? new List<Transform>()).Where(t => t != null).SelectMany(t => t.GetComponentsInChildren<Transform>(true)));
            return new PhysBoneInfo
            {
                path = RelPath(pb.transform, avatar.transform), root = RelPath(root, avatar.transform),
                transforms = root.GetComponentsInChildren<Transform>(true).Count(t => !ignored.Contains(t)),
                colliders = pb.colliders?.Count(c => c != null) ?? 0,
                grab = pb.allowGrabbing.ToString(), settings = SettingsText(pb),
            };
        }).ToArray();
        return report;
    }

    // ---------- stage: decimate ----------

    // Reallocate selected existing entries without changing the other parts' budgets.
    static void SetDecimationTargets(Config cfg)
    {
        var scene = OpenVariant(cfg);
        var avatar = FindAvatar(scene);
        var type = FindType(MeshiaCascadeType);
        var comp = avatar.GetComponentsInChildren(type, true).Single();
        var list = (IList)type.GetField("Entries").GetValue(comp);
        var entryType = FindType(MeshiaEntryType);
        var seen = new HashSet<string>();
        foreach (var target in cfg.decimationTargets)
        {
            if (!seen.Add(target.renderer) || target.triangles <= 0)
                throw new ArgumentException("Invalid/duplicate decimation target: " + target.renderer);
            var renderer = Find(avatar.transform, target.renderer).GetComponent<Renderer>();
            int source = Triangles(MeshOf(renderer));
            if (target.triangles > source) throw new ArgumentException("Target exceeds source: " + target.renderer);
            var entry = list.Cast<object>().Single(e =>
                ((nadena.dev.modular_avatar.core.AvatarObjectReference)entryType.GetField("RendererObjectReference").GetValue(e)).Get(avatar.transform) == renderer.gameObject);
            entryType.GetField("TargetTriangleCount").SetValue(entry, target.triangles);
            entryType.GetField("Enabled").SetValue(entry, target.triangles < source);
            entryType.GetField("Fixed").SetValue(entry, target.triangles >= source);
            Note($"Explicit Meshia target {target.renderer}: {target.triangles}/{source}");
        }
        EditorUtility.SetDirty(comp);
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Save failed");
    }

    static void Decimate(Config cfg)
    {
        var probePath = Path.Combine(cfg.reportDir, "probe.json");
        if (!File.Exists(probePath)) throw new InvalidOperationException("Run the probe stage first");
        var probe = JsonUtility.FromJson<StatsReport>(File.ReadAllText(probePath));
        var scene = OpenVariant(cfg);
        var avatar = FindAvatar(scene); var root = avatar.transform;
        var cascadeType = FindType(MeshiaCascadeType); var entryType = FindType(MeshiaEntryType);

        foreach (var old in avatar.GetComponentsInChildren(cascadeType, true)) { Note("Replacing existing Meshia cascading simplifier on " + old.name); UnityEngine.Object.DestroyImmediate(old); }

        var keepFull = new HashSet<string>(cfg.keepFull);
        // The face (visemes, eyelids) keeps full resolution: its many blendshapes deform badly when decimated.
        var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
        foreach (var face in new Renderer[] { descriptor.VisemeSkinnedMesh, descriptor.customEyeLookSettings.eyelidsSkinnedMesh }.Where(r => r != null))
            if (keepFull.Add(RelPath(face.transform, root))) Note("Keeping the face mesh at full resolution: " + RelPath(face.transform, root));
        // Prebuilt reduced base meshes (loop-reduced, original normals) are never decimated again.
        foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (smr.sharedMesh != null && AssetDatabase.GetAssetPath(smr.sharedMesh).StartsWith("Assets/VRUnityGenerated/BaseMeshes/")
                && keepFull.Add(RelPath(smr.transform, root)))
                Note("Keeping prebuilt base mesh undecimated: " + RelPath(smr.transform, root));
        var entries = new List<DecimateEntry>();
        foreach (var r in probe.renderers)
        {
            var t = root.Find(r.sourcePath);
            var renderer = t != null ? t.GetComponent<Renderer>() : null;
            bool candidate = renderer != null && (renderer is SkinnedMeshRenderer || renderer is MeshRenderer) && !keepFull.Contains(r.path) && !keepFull.Contains(r.sourcePath) && r.triangles >= cfg.minDecimate && r.sourceTriangles > 0;
            entries.Add(new DecimateEntry { path = r.sourcePath, sourceTriangles = r.sourceTriangles, probeTriangles = r.triangles, decimated = candidate });
        }
        int total = probe.polyCount;
        int decimatable = entries.Where(e => e.decimated).Sum(e => e.probeTriangles);
        int fixedTotal = total - decimatable;
        int budget = cfg.triangleTarget - fixedTotal;
        float ratio = (decimatable > 0 ? (float)budget / decimatable : 1) * cfg.ratioScale;
        var report = new DecimateReport { probeTotal = total, fixedTotal = fixedTotal, budget = budget, triangleTarget = cfg.triangleTarget, ratio = ratio };
        if (total <= cfg.triangleTarget) { Note($"Probe total {total} already within {cfg.triangleTarget}; no decimation"); ratio = 1; }
        else if (ratio < 0.15f) throw new InvalidOperationException($"Would need ratio {ratio:F2} on {decimatable} decimatable triangles (fixed {fixedTotal}); too aggressive, adjust keepFull/minDecimate");

        // Size-weighted shares (water-filling): target_i = c * size_i^e, capped at size_i, summing to the budget.
        if (ratio < 1)
        {
            var free = entries.Where(e => e.decimated).ToList(); float left = budget * cfg.ratioScale; float c = 0;
            while (free.Count > 0)
            {
                c = left / free.Sum(e => Mathf.Pow(e.probeTriangles, cfg.sizeExponent));
                var capped = free.Where(e => c * Mathf.Pow(e.probeTriangles, cfg.sizeExponent) >= e.probeTriangles).ToList();
                if (capped.Count == 0) break;
                foreach (var e in capped) { e.ratio = 1; left -= e.probeTriangles; free.Remove(e); }
            }
            foreach (var e in free) e.ratio = Mathf.Max(0.15f, c * Mathf.Pow(e.probeTriangles, cfg.sizeExponent) / e.probeTriangles);
            Note($"Size-weighted ratios (exponent {cfg.sizeExponent}): " + string.Join(", ", entries.Where(e => e.decimated)
                .OrderByDescending(e => e.probeTriangles).Select(e => $"{e.path.Split('/').Last()} {e.ratio:F2}")));
        }

        var go = root.Find("VRUnity Optimize")?.gameObject ?? new GameObject("VRUnity Optimize");
        go.transform.SetParent(root, false);
        var comp = go.AddComponent(cascadeType);
        cascadeType.GetField("AutoAdjustEnabled").SetValue(comp, false);
        cascadeType.GetField("TargetTriangleCount").SetValue(comp, cfg.triangleTarget);
        var list = (IList)cascadeType.GetField("Entries").GetValue(comp);
        foreach (var e in entries)
        {
            var t = root.Find(e.path); var renderer = t != null ? t.GetComponent<Renderer>() : null;
            if (renderer == null || !(renderer is SkinnedMeshRenderer || renderer is MeshRenderer) || MeshOf(renderer) == null) continue;
            var entry = Activator.CreateInstance(entryType, renderer);
            bool reduce = e.decimated && ratio < 1 && e.ratio < 0.999f;
            e.meshiaTarget = reduce ? Mathf.RoundToInt(e.ratio * e.sourceTriangles) : e.sourceTriangles;
            entryType.GetField("TargetTriangleCount").SetValue(entry, e.meshiaTarget);
            entryType.GetField("Enabled").SetValue(entry, reduce);
            entryType.GetField("Fixed").SetValue(entry, !reduce);
            var optField = entryType.GetField("Options"); var options = optField.GetValue(entry);
            options.GetType().GetField("PreserveBorderEdges").SetValue(options, true);  // boxed struct
            options.GetType().GetField("EnableSmartLink").SetValue(options, cfg.meshiaSmartLink);
            options.GetType().GetField("PreserveSurfaceCurvature").SetValue(options, cfg.meshiaPreserveCurvature);
            options.GetType().GetField("MinNormalDot").SetValue(options, cfg.meshiaMinNormalDot);
            optField.SetValue(entry, options);
            list.Add(entry);
        }
        // Meshia's entry constructor did not leave a reference behind when called by reflection (the field stayed
        // null and saved empty, so Meshia skipped every entry). Set the MA reference explicitly.
        var refField = entryType.GetField("RendererObjectReference");
        int idx = 0;
        foreach (var e in entries)
        {
            var t = root.Find(e.path); var renderer = t != null ? t.GetComponent<Renderer>() : null;
            if (renderer == null || !(renderer is SkinnedMeshRenderer || renderer is MeshRenderer) || MeshOf(renderer) == null) continue;
            var reference = new nadena.dev.modular_avatar.core.AvatarObjectReference();
            reference.Set(renderer.gameObject);
            refField.SetValue(list[idx++], reference);
        }
        EditorUtility.SetDirty(comp);
        if (idx != list.Count) throw new InvalidOperationException($"Entry count mismatch {idx} vs {list.Count}");
        Note($"Meshia: ratio {ratio:F3} on {entries.Count(e => e.decimated)} renderers ({decimatable} probe triangles); fixed {fixedTotal}; target {cfg.triangleTarget}");
        if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Save failed");
        report.ratio = ratio; report.entries = entries.ToArray(); report.notes = notes.ToArray();
        WriteReport(cfg, "decimate", report);
    }

    // ---------- stage: render ----------

    [Serializable] class Frame { public string pose; public Vector3 center; public float radius; }
    [Serializable] class RenderReport { public string[] views; public string[] notes; }

    static readonly (float yaw, float pitch)[] Views =
    {
        (0, 0), (45, 0), (90, 0), (135, 0), (180, 0), (225, 0), (270, 0), (315, 0),
        (0, 35), (90, 35), (180, 35), (270, 35),       // from below
        (0, -35), (90, -35), (180, -35), (270, -35),   // from above
    };

    // Source and candidate base meshes through the same build and camera, with no secondary simplifier.
    // Only clones are edited, and no scene is saved. Left hand first keeps diagnostic iterations short.
    static void RenderHandControls(Config cfg)
    {
        var scene = EditorSceneManager.OpenScene(cfg.source, OpenSceneMode.Single);
        var avatar = FindAvatar(scene);
        var shader = Shader.Find("Hidden/VRUnity/FaceCheck") ?? throw new InvalidOperationException("FaceCheck shader missing");
        var flag = DiagnosticMaterial(shader, "Assets/VRUnityAgent/Editor/FaceCheck-Flag.mat", 0);
        var normal = DiagnosticMaterial(shader, "Assets/VRUnityAgent/Editor/FaceCheck-Normal.mat", 1);
        var frames = new Dictionary<string, Frame>(); var views = new List<string>();
        foreach (var control in cfg.handControls)
        {
            if (string.IsNullOrEmpty(control.name) || control.name.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
                throw new ArgumentException("Hand control name must be letters, digits or hyphens");
            RenderSet(cfg, avatar, control.name, copy => {
                foreach (var path in control.disableObjects) Find(copy.transform, path).gameObject.SetActive(false);
                if (control.mode == "no-body-mask")
                {
                    foreach (var c in Find(copy.transform, cfg.bodyRenderer).GetComponents<Component>().Where(c => IsType(c, RemoveMeshByMaskType)))
                        UnityEngine.Object.DestroyImmediate(c);
                }
                else if (control.mode == "no-meshia") NoDecimation(copy);
                else if (control.mode != "configured") StripOptimizers(copy);
                if (string.IsNullOrEmpty(control.mesh)) return;
                var smr = Find(copy.transform, cfg.bodyRenderer).GetComponent<SkinnedMeshRenderer>();
                var mesh = control.mesh.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
                    ? AssetDatabase.LoadAllAssetsAtPath(control.mesh).OfType<Mesh>().Single(m => m.name == Path.GetFileName(cfg.bodyRenderer))
                    : AssetDatabase.LoadAssetAtPath<Mesh>(control.mesh);
                if (mesh == null) throw new ArgumentException("Missing mesh " + control.mesh);
                var old = smr.sharedMesh;
                if (old.bindposes.Length != mesh.bindposes.Length || old.bindposes.Zip(mesh.bindposes, (a, b) => a == b).Any(x => !x))
                    throw new InvalidOperationException("Hand-control bind poses differ");
                var weights = Enumerable.Range(0, old.blendShapeCount).ToDictionary(i => old.GetBlendShapeName(i), i => smr.GetBlendShapeWeight(i));
                smr.sharedMesh = mesh;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    smr.SetBlendShapeWeight(i, weights.TryGetValue(mesh.GetBlendShapeName(i), out var w) ? w : 0);
            }, frames, flag, normal, Path.Combine(cfg.reportDir, "render"), views, cfg.focusPoses, true,
                focuses: (cfg.renderFocus.Length > 0 ? cfg.renderFocus : new[] { "LeftHand" })
                    .Select(bone => (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), bone)).ToArray());
        }
        WriteReport(cfg, "hands", new SimpleReport { stage = "hands", scene = cfg.source, notes = notes.ToArray() });
    }

    static void Render(Config cfg)
    {
        var shader = Shader.Find("Hidden/VRUnity/FaceCheck") ?? throw new InvalidOperationException("FaceCheck shader missing");
        foreach (var m in ShaderUtil.GetShaderMessages(shader)) Log($"FaceCheck shader message: {m.severity} {m.message} (line {m.line}, {m.platform})");
        Log($"FaceCheck shader supported {shader.isSupported}, passes {shader.passCount}, has error {ShaderUtil.ShaderHasError(shader)}");
        if (ShaderUtil.ShaderHasError(shader) || !shader.isSupported) throw new InvalidOperationException("FaceCheck shader unusable");
        // Materials created at runtime render as the error shader in this headless preview path; assets work.
        var flag = DiagnosticMaterial(shader, "Assets/VRUnityAgent/Editor/FaceCheck-Flag.mat", 0);
        var normal = DiagnosticMaterial(shader, "Assets/VRUnityAgent/Editor/FaceCheck-Normal.mat", 1);
        var outDir = Path.Combine(cfg.reportDir, "render");
        var frames = new Dictionary<string, Frame>();
        var views = new List<string>();

        foreach (var (label, scenePath) in new[] { ("source", cfg.source), ("variant", cfg.variant) })
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var avatar = FindAvatar(scene);
            if (cfg.renderPoses.Length > 0)
                RenderSet(cfg, avatar, label, null, frames, flag, normal, outDir, views, cfg.renderPoses, true);
            if (cfg.renderFocus.Length > 0 && cfg.focusPoses.Length > 0)
                RenderSet(cfg, avatar, label + "-focus", null, frames, flag, normal, outDir, views, cfg.focusPoses, true,
                    focuses: cfg.renderFocus.Select(bone => (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), bone)).ToArray());
            if (cfg.renderNoOutline)
                RenderSet(cfg, avatar, label + "-nooutline", null, frames, flag, normal, outDir, views, cfg.focusPoses, false,
                    noOutline: true, focuses: cfg.renderFocus.Where(b => b.EndsWith("Hand"))
                        .Select(bone => (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), bone)).ToArray());
            if (label == "variant" && cfg.fixedOn.Length > 0 && cfg.renderUncovered)
                // The cut body with the fixed-on clothing hidden: shows exactly what was removed.
                RenderSet(cfg, avatar, "variant-uncovered", copy => NoAutoMerge(copy), frames, flag, normal, outDir, views, new[] { "rest", "armsUp" }, false,
                    cfg.fixedOn.Select(f => f.target).ToArray());
        }
        WriteReport(cfg, "render", new RenderReport { views = views.ToArray(), notes = notes.ToArray() });
    }

    static Material DiagnosticMaterial(Shader shader, string path, float mode)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
        if (m.shader != shader || m.GetFloat("_Mode") != mode) { m.shader = shader; m.SetFloat("_Mode", mode); EditorUtility.SetDirty(m); AssetDatabase.SaveAssets(); }
        return m;
    }

    static void RenderSet(Config cfg, GameObject avatar, string label, Action<GameObject> prepare, Dictionary<string, Frame> frames,
        Material flag, Material normal, string outDir, List<string> views, string[] poseNames, bool allModes, string[] hide = null,
        HumanBodyBones? focus = null, bool noOutline = false, HumanBodyBones[] focuses = null)
    {
        var copy = BuildCopy(avatar, clone => { if (cfg.renderNoDecimation) NoDecimation(clone); prepare?.Invoke(clone); });
        if (cfg.renderAuditBody)
        {
            var body = copy.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Single(r => SourcePath(RelPath(r.transform, copy.transform)) == cfg.bodyRenderer);
            WriteReport(cfg, label + "-body", new AvatarAgentOptimizeAudit.Geometry
            {
                path = cfg.bodyRenderer, mesh = body.sharedMesh.name,
                vertices = AvatarMeshGeometry.WorldVertices(body), triangles = body.sharedMesh.triangles,
                uv = body.sharedMesh.uv, normals = body.sharedMesh.normals, scale = body.transform.lossyScale,
            });
        }
        if (hide != null)
            foreach (var h in hide)
            {
                // AAO may have moved and renamed it (e.g. "Outfit$Jacket$22"); match by source path.
                var t = copy.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x != copy.transform && SourcePath(RelPath(x, copy.transform)) == h)
                    ?? throw new ArgumentException($"No '{h}' in the built copy");
                t.gameObject.SetActive(false);
            }
        var utility = new PreviewRenderUtility();
        var dir = Path.Combine(outDir, label); Directory.CreateDirectory(dir);
        try
        {
            var poser = new Poser(copy);
            Log($"{label}: {poser.Describe(copy)}");
            copy.transform.position = Vector3.zero; copy.transform.rotation = Quaternion.identity;
            utility.AddSingleGO(copy);
            var camera = utility.camera;
            camera.fieldOfView = 30; camera.nearClipPlane = 0.01f; camera.farClipPlane = 30;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.1f, 0.45f, 0.15f, 1);
            camera.allowMSAA = false;
            utility.lights[0].intensity = 1.1f; utility.lights[0].transform.rotation = Quaternion.Euler(35, -30, 0);
            utility.lights[1].intensity = 0.7f; utility.ambientColor = new Color(0.45f, 0.45f, 0.45f);
            var renderers = copy.GetComponentsInChildren<Renderer>(true);
            if (noOutline)
                foreach (var r in renderers)
                    r.sharedMaterials = r.sharedMaterials.Select(m => {
                        if (m == null || !m.HasProperty("_OutlineWidth")) return m;
                        string p = cfg.assetDir + "/Diagnostic-NoOutline-" + Hash128.Compute(AssetDatabase.GetAssetPath(m) + ":" + m.name) + ".mat";
                        var diagnostic = AssetDatabase.LoadAssetAtPath<Material>(p);
                        if (diagnostic == null) { diagnostic = new Material(m); AssetDatabase.CreateAsset(diagnostic, p); }
                        else EditorUtility.CopySerialized(m, diagnostic);
                        diagnostic.SetFloat("_OutlineWidth", 0); EditorUtility.SetDirty(diagnostic);
                        return diagnostic;
                    }).ToArray();
            var original = renderers.Select(r => r.sharedMaterials).ToArray();
            var modes = allModes ? new[] { "lit", "flag", "normal" } : new[] { "lit", "flag" };
            var targets = focuses != null ? focuses.Select(b => (HumanBodyBones?)b).ToArray() : new[] { focus };
            foreach (var selectedFocus in targets)
            {
            focus = selectedFocus;
            foreach (var mode in modes)
            {
                for (int i = 0; i < renderers.Length; i++)
                    renderers[i].sharedMaterials = mode == "lit" ? original[i]
                        : Enumerable.Repeat(mode == "flag" ? flag : normal, original[i].Length).ToArray();
                foreach (var poseName in poseNames)
                {
                    poser.Apply(PoseByName(poseName));
                    string frameKey = focus.HasValue ? $"{poseName}@{focus}" : poseName;
                    if (focus.HasValue && !frames.ContainsKey(frameKey))
                    {
                        var b = copy.GetComponent<Animator>().GetBoneTransform(focus.Value) ?? throw new ArgumentException("No bone " + focus);
                        // Hands: centre between wrist and middle finger; head: the bone itself, a bit larger.
                        var tip = focus == HumanBodyBones.LeftHand ? HumanBodyBones.LeftMiddleIntermediate
                            : focus == HumanBodyBones.RightHand ? HumanBodyBones.RightMiddleIntermediate
                            : focus == HumanBodyBones.LeftFoot ? HumanBodyBones.LeftToes
                            : focus == HumanBodyBones.RightFoot ? HumanBodyBones.RightToes : (HumanBodyBones?)null;
                        var t2 = tip.HasValue ? copy.GetComponent<Animator>().GetBoneTransform(tip.Value) : null;
                        var c = t2 != null ? (b.position + t2.position) / 2 : b.position + Vector3.up * 0.06f;
                        bool foot = focus == HumanBodyBones.LeftFoot || focus == HumanBodyBones.RightFoot;
                        bool knee = focus == HumanBodyBones.LeftLowerLeg || focus == HumanBodyBones.RightLowerLeg;
                        if (knee) c = b.position;
                        frames[frameKey] = new Frame { pose = frameKey, center = c, radius = foot ? 0.10f : knee ? 0.14f : t2 != null ? 0.11f : 0.2f };
                    }
                    if (!frames.TryGetValue(frameKey, out var frame))
                    {
                        var pts = renderers.Where(r => r.enabled && r.gameObject.activeInHierarchy && (r is SkinnedMeshRenderer || r is MeshRenderer) && MeshOf(r) != null)
                            .SelectMany(WorldVertices).ToArray();
                        // 1st-99th percentile per axis, so thin outliers (a leash line, a far tail tip) don't shrink the avatar.
                        float Pct(Func<Vector3, float> f, float q) { var v = pts.Select(f).OrderBy(x => x).ToArray(); return v[(int)(q * (v.Length - 1))]; }
                        var min = new Vector3(Pct(p => p.x, .01f), Pct(p => p.y, .01f), Pct(p => p.z, .01f));
                        var max = new Vector3(Pct(p => p.x, .99f), Pct(p => p.y, .99f), Pct(p => p.z, .99f));
                        var b = new Bounds((min + max) / 2, max - min);
                        frames[poseName] = frame = new Frame { pose = poseName, center = b.center, radius = b.extents.magnitude * 1.08f };
                    }
                    foreach (var (yaw, pitch) in Views)
                    {
                        var name = $"{(focus.HasValue ? focus + "_" : "")}{poseName}-{mode}-y{yaw:000}-p{(pitch < 0 ? "m" : "")}{Mathf.Abs(pitch):00}.png";
                        var direction = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
                        camera.transform.position = frame.center + direction * frame.radius * 3.9f;
                        camera.transform.LookAt(frame.center);
                        utility.BeginStaticPreview(new Rect(0, 0, cfg.renderSize, cfg.renderSize));
                        utility.Render(true);
                        var tex = utility.EndStaticPreview();
                        File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
                        UnityEngine.Object.DestroyImmediate(tex);
                        if (label == "source") views.Add(name);
                    }
                }
            }
            }
            Note($"Rendered {label}: {targets.Length} frames x {poseNames.Length} poses x {Views.Length} views x {modes.Length} modes");
        }
        finally { utility.Cleanup(); UnityEngine.Object.DestroyImmediate(copy); }
    }
}
