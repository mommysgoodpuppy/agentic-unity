// EXAMPLE, HARDCODED FOR ONE AVATAR: scene paths, object names and measurements below are that avatar's,
// so this won't run on yours as it is. It's an example of a kind of tool: see examples/hardcoded/README.md.
// No assets are included; any vendor asset it names is a path in a project that owns a copy.
// The CHIRP collar's leash on Velle, headless. The Windows scene removed the leash's PhysBone
// (and its chest collider) as prefab overrides, so the leash came over without one: it hangs
// stiff and can't be grabbed. Close every editor on the project first, then:
//   Unity -batchmode -nographics -projectPath <proj> -executeMethod AvatarAgentLeash.Apply \
//     -avatarScene Assets/VRUnityGenerated/....unity -logFile <log>
// Apply copies the PhysBone from CHIRP's Leash.prefab back onto "Leash Physbone", rooted at the
// scene's own clasp bone. CHIRP's chest collider was a template never fitted to Velle; instead
// a capsule fitted to her chest and belly ("Leash Body Collider" on the Chest bone, see
// BodyCollider) keeps the leash in front of the body. Its parameter stays "Leash", which is what
// OSCLeash (github.com/ZenithVal/OSCLeash) reads as Leash_IsGrabbed and Leash_Stretch. It then
// adds upstream's OSCLeash prefab (Assets/VRUnityGenerated/OSCLeash) at the avatar root minus its
// example PhysBone: the Compass follows the leash's first bone, its Aim Needle aims at the
// handle, and six local-only receivers report the pull direction as Leash_X+ ... Leash_Z-.
// Both steps skip what is already there; the scene is saved. AvatarAgentLeash.Check builds a
// copy with the SDK's preprocess callbacks and checks the wiring; nothing is saved.
// AvatarAgentLeash.Preview enters play mode, lets the leash settle and renders it from the side
// and front into -previewOut; run it without -nographics. Lines start with [AvatarAgentLeash];
// all exit 0 on success.
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase.Editor.BuildPipeline;

public static class AvatarAgentLeash
{
    const string ChirpLeashPath = "Assets/_Reava_/CHIRP Collars/Prefabs/Leash.prefab";
    const string OscLeashPath = "Assets/VRUnityGenerated/OSCLeash/OSCLeash.prefab";
    const string PhysBoneObject = "Leash Physbone";
    const string HandleBone = "Leash.Handle";
    const string Parameter = "Leash";
    static readonly string[] Directions = { "X+", "X-", "Y+", "Y-", "Z+", "Z-" };

    // Measured on the baked meshes (2026-10-25), in avatar-root metres, +Z forward: the front of
    // the body and outfit is at z 0.13 from y 0.85 to 1.05, 0.126 at 1.10 and 0.10 at 1.15; the
    // back is at about -0.07. The clasp hangs at y 1.17, z 0.022. The capsule lies across the
    // chest (axis along X) with its axis behind the clasp, so the falling leash lands on its front
    // slope and drapes over the bust instead of sliding round the side, as an upright capsule
    // centred at z 0.045 made it do. Radius 0.13 at z 0.005 puts the front at 0.135 at y 1.0;
    // the straight middle (|x| < 0.03) gives no sideways push. The leash bones' own radius
    // (8 mm at the clasp to 27 mm midway) adds to it.
    const string ColliderName = "Leash Body Collider";
    const float ColliderRadius = 0.13f;
    static readonly Vector3 ColliderCentre = new Vector3(0, 1.0f, 0.005f);
    const float ColliderHeight = 0.32f; // end to end along X

    static void Log(string s) => Debug.Log("[AvatarAgentLeash] " + s);

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

    static Transform Unique(Transform under, string name)
    {
        var found = under.GetComponentsInChildren<Transform>(true).Where(t => t.name == name).ToArray();
        if (found.Length != 1) throw new InvalidOperationException($"{found.Length} objects named {name} under {under.name}, expected 1");
        return found[0];
    }

    // The leash's own bones: the PhysBone object's parent holds the leash armature and mesh.
    static (Transform physBoneObject, Transform root, Transform handle) Leash(GameObject avatar, string rootName)
    {
        var pbObject = Unique(avatar.transform, PhysBoneObject);
        var leash = pbObject.parent;
        return (pbObject, Unique(leash, rootName), Unique(leash, HandleBone));
    }

    static void SetSource(Component constraint, Transform source)
    {
        var so = new SerializedObject(constraint);
        so.FindProperty("Sources.source0.SourceTransform").objectReferenceValue = source;
        if (so.FindProperty("Sources.totalLength").intValue != 1) throw new InvalidOperationException(constraint.name + " should have exactly one source");
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // On the Chest bone so it follows the torso, but placed against the avatar root (the bone
    // leans back about 10 degrees). Sizes are set in world metres, so they're divided by the
    // bone's scale. An existing one is reset to these values.
    static VRCPhysBoneCollider BodyCollider(GameObject avatar)
    {
        var chest = avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Chest) ?? throw new InvalidOperationException("No Chest bone");
        var go = chest.Find(ColliderName)?.gameObject;
        if (go == null) { go = new GameObject(ColliderName); go.transform.SetParent(chest, false); }
        // The capsule's axis is its local Y; turn it to run across the chest.
        go.transform.SetPositionAndRotation(avatar.transform.TransformPoint(ColliderCentre), avatar.transform.rotation * Quaternion.Euler(0, 0, 90));
        var collider = go.GetComponent<VRCPhysBoneCollider>() ?? go.AddComponent<VRCPhysBoneCollider>();
        float scale = go.transform.lossyScale.y;
        collider.shapeType = VRC.Dynamics.VRCPhysBoneColliderBase.ShapeType.Capsule;
        collider.radius = ColliderRadius / scale;
        collider.height = ColliderHeight / scale;
        collider.insideBounds = false;
        Log($"added {ColliderName} on {chest.name}: capsule r {ColliderRadius} m, {ColliderHeight} m tall, centre {ColliderCentre} from the avatar root (bone scale {scale:0.###})");
        return collider;
    }

    public static void Apply()
    {
        int code = 1;
        try
        {
            var avatar = OpenAvatar();
            var chirp = AssetDatabase.LoadAssetAtPath<GameObject>(ChirpLeashPath) ?? throw new InvalidOperationException("Missing " + ChirpLeashPath);
            var template = chirp.GetComponentsInChildren<VRCPhysBone>(true).Single(p => p.name == PhysBoneObject);
            var (pbObject, root, handle) = Leash(avatar, template.rootTransform.name);

            var physBone = pbObject.GetComponent<VRCPhysBone>();
            if (physBone != null) Log($"{PhysBoneObject} already has a PhysBone; left as is");
            else
            {
                if (!ComponentUtility.CopyComponent(template) || !ComponentUtility.PasteComponentAsNew(pbObject.gameObject))
                    throw new InvalidOperationException("Could not copy the PhysBone from " + ChirpLeashPath);
                physBone = pbObject.GetComponent<VRCPhysBone>();
                physBone.rootTransform = root;
                // CHIRP's template chest collider is replaced by the fitted one below.
                physBone.colliders.Clear();
                Log($"restored the PhysBone on {PhysBoneObject}: root {root.name}, parameter {physBone.parameter}, max stretch {physBone.maxStretch}");
            }
            if (physBone.parameter != Parameter) throw new InvalidOperationException($"PhysBone parameter is {physBone.parameter}, OSCLeash expects {Parameter}");
            var bodyCollider = BodyCollider(avatar);
            if (!physBone.colliders.Contains(bodyCollider))
            {
                physBone.colliders.RemoveAll(c => c == null);
                physBone.colliders.Add(bodyCollider);
                Log($"the leash PhysBone now collides with {ColliderName}");
            }

            if (avatar.transform.Find("OSCLeash") != null) Log("OSCLeash is already on the avatar root; left as is");
            else
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OscLeashPath) ?? throw new InvalidOperationException("Missing " + OscLeashPath);
                var osc = (GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar.transform);
                PrefabUtility.UnpackPrefabInstance(osc, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                osc.name = "OSCLeash";
                osc.transform.localPosition = Vector3.zero;
                osc.transform.localRotation = Quaternion.identity;
                osc.transform.localScale = Vector3.one;
                // Upstream ships an example leash PhysBone; Velle's is CHIRP's.
                UnityEngine.Object.DestroyImmediate(osc.transform.Find(PhysBoneObject).gameObject);
                var compass = osc.transform.Find("Compass");
                SetSource(compass.GetComponent<VRCPositionConstraint>(), root);
                SetSource(compass.Find("Aim Needle").GetComponent<VRCAimConstraint>(), handle);
                Log($"added OSCLeash: Compass follows {root.name}, Aim Needle aims at {handle.name}");
            }

            EditorSceneManager.MarkSceneDirty(avatar.scene);
            if (!EditorSceneManager.SaveScene(avatar.scene)) throw new System.IO.IOException("Scene save failed");
            Log("saved " + avatar.scene.path);
            code = 0;
        }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
        EditorApplication.Exit(code);
    }

    // World-metre distance from p to the capsule's surface (negative inside).
    static float CapsuleGap(VRCPhysBoneCollider c, Vector3 p)
    {
        float s = c.transform.lossyScale.y;
        var half = c.transform.up * Mathf.Max(0, c.height / 2 - c.radius) * s;
        Vector3 a = c.transform.position - half, b = c.transform.position + half;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, b - a) / Mathf.Max(1e-6f, (b - a).sqrMagnitude));
        return Vector3.Distance(p, a + t * (b - a)) - c.radius * s;
    }

    // Play mode (VRCFury and MA build the avatar, PhysBones simulate): after the leash has hung
    // for a few seconds, logs each leash bone's gap to the body collider and renders the chest
    // from the side and front into -previewOut.
    public static void Preview()
    {
        try
        {
            OpenAvatar();
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += RunPreview;
            };
            EditorApplication.EnterPlaymode();
        }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); EditorApplication.Exit(1); }
    }

    static void RunPreview()
    {
        double start = EditorApplication.timeSinceStartup;
        EditorApplication.CallbackFunction step = null;
        step = () =>
        {
            if (EditorApplication.timeSinceStartup - start < 6) return;
            EditorApplication.update -= step;
            int code = 1;
            try
            {
                string outDir = Arg("-previewOut") ?? throw new ArgumentException("pass -previewOut");
                System.IO.Directory.CreateDirectory(outDir);
                var avatar = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>().Single().gameObject;
                var pb = avatar.GetComponentsInChildren<VRCPhysBone>(true).Single(p => p.parameter == Parameter);
                var body = avatar.GetComponentsInChildren<VRCPhysBoneCollider>(true).SingleOrDefault(c => c.name == ColliderName);
                var root = avatar.transform;
                foreach (var t in pb.GetRootTransform().GetComponentsInChildren<Transform>(true))
                {
                    var local = root.InverseTransformPoint(t.position);
                    Log($"settled {t.name}: {local:F3} from the root" + (body != null ? $", {CapsuleGap(body, t.position) * 100:0.0} cm outside the collider" : ""));
                }
                var cam = new GameObject("Leash preview").AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.2f, 0.2f, 0.24f);
                cam.fieldOfView = 30;
                cam.nearClipPlane = 0.01f;
                var focus = root.TransformPoint(new Vector3(0, 0.98f, 0.05f));
                foreach (var (name, dir) in new[] { ("side", root.right), ("front", (root.forward + root.right * 0.5f).normalized) })
                {
                    cam.transform.position = focus + dir * 1.3f;
                    cam.transform.LookAt(focus);
                    var rt = new RenderTexture(800, 1000, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"leash-{name}.png"), tex.EncodeToPNG());
                    RenderTexture.active = null;
                    cam.targetTexture = null;
                    rt.Release();
                }
                Log("previews written to " + outDir);
                code = 0;
            }
            catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
            EditorApplication.Exit(code);
        };
        EditorApplication.update += step;
    }

    public static void Check()
    {
        int code = 1;
        GameObject copy = null;
        try
        {
            var avatar = OpenAvatar();
            copy = UnityEngine.Object.Instantiate(avatar);
            if (!VRCBuildPipelineCallbacks.OnPreprocessAvatar(copy)) throw new InvalidOperationException("Preprocess failed");
            bool ok = true;
            void Report(bool pass, string what) { Log((pass ? "PASS " : "FAIL ") + what); ok &= pass; }

            var physBones = copy.GetComponentsInChildren<VRCPhysBone>(true).Where(p => p.parameter == Parameter).ToArray();
            Report(physBones.Length == 1, $"one PhysBone has parameter {Parameter} ({physBones.Length})");
            var pb = physBones.FirstOrDefault();
            if (pb != null)
            {
                Report(pb.gameObject.activeInHierarchy && pb.enabled, $"the leash PhysBone is active at {AnimationUtility.CalculateTransformPath(pb.transform, copy.transform)}");
                var root = pb.GetRootTransform();
                Report(root != null && root.GetComponentsInChildren<Transform>(true).Any(t => t.name == HandleBone),
                    $"its chain runs from {root?.name} to {HandleBone}, under {root?.parent?.name}");
                Report(pb.allowGrabbing == VRC.Dynamics.VRCPhysBoneBase.AdvancedBool.True && pb.maxStretch > 0,
                    $"it can be grabbed and stretched (max stretch {pb.maxStretch})");
                var body = pb.colliders.OfType<VRCPhysBoneCollider>().SingleOrDefault(c => c != null && c.name == ColliderName);
                var chest = copy.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Chest);
                Report(body != null && body.transform.parent == chest, $"it collides with {ColliderName} on the Chest bone");
                if (body != null && root != null)
                {
                    // Distance from the clasp to the capsule's axis segment, in world metres.
                    var up = body.transform.up * (body.height / 2 - body.radius) * body.transform.lossyScale.y;
                    var a = body.transform.position - up; var b = body.transform.position + up;
                    var p = root.position;
                    var onAxis = a + Vector3.Project(p - a, b - a);
                    if (Vector3.Dot(onAxis - a, b - a) < 0) onAxis = a; else if ((onAxis - a).magnitude > (b - a).magnitude) onAxis = b;
                    float gap = Vector3.Distance(p, onAxis) - body.radius * body.transform.lossyScale.y;
                    Report(gap > 0.005f, $"the clasp is outside the collider ({gap * 100:0.0} cm)");
                }

                var compass = copy.GetComponentsInChildren<VRCPositionConstraint>(true).SingleOrDefault(c => c.name == "Compass");
                var needle = copy.GetComponentsInChildren<VRCAimConstraint>(true).SingleOrDefault(c => c.name == "Aim Needle");
                Report(compass != null && compass.Sources.Count == 1 && compass.Sources[0].SourceTransform == root, "the Compass follows the leash's first bone");
                Report(needle != null && needle.Sources.Count == 1 && needle.Sources[0].SourceTransform?.name == HandleBone, "the Aim Needle aims at the handle");
                Report(compass != null && compass.transform.parent?.parent == copy.transform, "OSCLeash sits on the avatar root, outside the armature");
            }
            var receivers = copy.GetComponentsInChildren<VRCContactReceiver>(true).Where(r => r.parameter.StartsWith(Parameter + "_")).ToArray();
            foreach (var d in Directions)
            {
                var r = receivers.SingleOrDefault(x => x.parameter == Parameter + "_" + d);
                Report(r != null && r.localOnly && r.allowSelf && r.collisionTags.Contains("Leash_Direction"), $"receiver {Parameter}_{d} is local-only and senses the needle");
            }
            Report(copy.GetComponentsInChildren<VRCContactSender>(true).Any(s => s.name == "Aim Needle" && s.collisionTags.Contains("Leash_Direction")), "the Aim Needle sends Leash_Direction");
            Log(ok ? "RESULT PASS" : "RESULT FAIL");
            code = ok ? 0 : 1;
        }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
        finally { if (copy != null) UnityEngine.Object.DestroyImmediate(copy); }
        EditorApplication.Exit(code);
    }
}
