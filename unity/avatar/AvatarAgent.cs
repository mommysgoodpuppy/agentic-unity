// Editor-only local file queue. Install in Assets/VRUnityAgent/Editor.
// Requests/results are private local files outside Assets; no network listener.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using nadena.dev.modular_avatar.core;

[InitializeOnLoad]
public static partial class AvatarAgent
{
    [Serializable] public class Request
    {
        public string command, scene, sourceId, outputScene, outfitPrefab, packagePath, packageManifest, previewDirectory;
        public string[] disablePaths = new string[0];
        public bool matchOutfitBoneScale;
        public string label;
        public string outfitPath, maskPath, generatedAssets;
        public string[] modelRoots = new string[0];
        public MeshReplacement[] meshReplacements = new MeshReplacement[0];
        public string accessoryRenderer, anchorPath, accessoryName, outputPrefab;
        public string prefab, anchorName, instanceName, alignRenderer, alignChild;
        public bool realign;
        public Vector3 offset, euler;
        public string focusPath, previewLabel;
        public bool useFocusRegion;
        public Vector3 focusRegionMin, focusRegionMax;
        public float[] yaws = new float[0];
        public float glow = -1, zoomOut = 1, pitch = 10;
        public bool defaultOn;
        public float sliderDefault;
        public Edit[] edits = new Edit[0];
    }
    [Serializable] public class Edit
    {
        public string path, kind, shape, material, component;
        public string expectedName;
        public bool active;
        public Vector3 value;
        public float weight;
        public int slot;
    }
    [Serializable] public class Node
    {
        public string path, name;
        public bool active;
        public Vector3 position, scale;
        public Quaternion rotation;
        public string[] components;
    }
    [Serializable] public class Shape { public string name; public float weight; }
    [Serializable] public class PackageAsset { public string path, guid; }
    [Serializable] public class PackageManifest
    {
        public string package_sha256;
        public int path_collisions, guid_collisions, code_assets;
        public PackageAsset[] assets;
    }
    [Serializable] public class Skin
    {
        public string path, mesh;
        public string[] materials;
        public List<Shape> blendshapes = new List<Shape>();
    }
    [Serializable] public class Avatar
    {
        public string id, name, scene;
        public bool active;
    }
    [Serializable] public class Result
    {
        public bool ok;
        public string error, command, sourceId, outputScene;
        public bool baseTransformsPreserved, baseBlendshapesPreserved, baseMaterialsPreserved;
        public bool sceneDirty;
        public int appliedEdits;
        public int matchedOutfitBones, missingScripts, missingMeshes, nullMaterials;
        public List<Avatar> avatars = new List<Avatar>();
        public List<Node> nodes = new List<Node>();
        public List<Skin> skins = new List<Skin>();
        public List<string> notes = new List<string>();
        public List<MeshRecord> meshes = new List<MeshRecord>();
        public List<MeshRecord> meshCandidates = new List<MeshRecord>();
        public CullingReport culling;
    }
    static string Queue => Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/VRUnityAgent"));
    static double nextPoll;
    static bool busy;
    static AvatarAgent() { EditorApplication.update += Poll; }

    // Startup entry point for the interactive preview; never saves the old scene.
    public static void OpenLive()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-avatarScene");
        if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing -avatarScene");
        OpenDraft(args[i + 1]);
        EditorApplication.delayCall += () => Frame(Source(null));
    }
    static void OpenDraft(string path)
    {
        RequireDraft(path);
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("Unsaved scene open; save or checkpoint it before switching scenes");
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
    }
    static void RequireDraft(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/VRUnityGenerated/", StringComparison.Ordinal) ||
            !path.EndsWith(".unity", StringComparison.Ordinal) || path.Split('/').Any(x => x == ".." || x == ".") || path.Contains('\\'))
            throw new ArgumentException("Live edits/checkpoints require a scene under Assets/VRUnityGenerated/");
    }
    static void RefreshPreview()
    {
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }
    static void Frame(GameObject root)
    {
        Selection.activeGameObject = root;
        var view = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
        var renderers = root.GetComponentsInChildren<Renderer>().Where(x => x.enabled && x.gameObject.activeInHierarchy).ToArray();
        var bounds = new Bounds(root.transform.position, Vector3.one);
        if (renderers.Length > 0)
        {
            bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
        }
        view.sceneLighting = false;
        view.LookAt(bounds.center, Quaternion.LookRotation(Vector3.back), Mathf.Max(bounds.extents.magnitude, 0.1f) * 1.15f, true, true);
        view.Show(); view.Focus(); RefreshPreview();
    }

    // Same implementation as the live queue, usable without a graphical editor.
    public static void RunBatch()
    {
        string reportPath = null;
        try
        {
            var args = Environment.GetCommandLineArgs();
            string Value(string key)
            {
                int i = Array.IndexOf(args, key);
                if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing " + key);
                return args[i + 1];
            }
            reportPath = Path.GetFullPath(Value("-avatarReport"));
            var req = JsonUtility.FromJson<Request>(File.ReadAllText(Value("-avatarRecipe")));
            if (string.IsNullOrEmpty(req.scene)) throw new ArgumentException("Batch recipe requires scene");
            EditorSceneManager.OpenScene(req.scene, OpenSceneMode.Single);
            Dispatch(req, result =>
            {
                File.WriteAllText(reportPath, JsonUtility.ToJson(result, true));
                EditorApplication.Exit(result.ok ? 0 : 1);
            });
        }
        catch (Exception e)
        {
            if (reportPath != null) File.WriteAllText(reportPath, JsonUtility.ToJson(new Result { error = e.ToString() }, true));
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static void Poll()
    {
        if (Application.isBatchMode || busy) return;
        if (EditorApplication.timeSinceStartup < nextPoll || EditorApplication.isCompiling ||
            EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        nextPoll = EditorApplication.timeSinceStartup + 0.5;
        Directory.CreateDirectory(Queue);
        foreach (var path in Directory.GetFiles(Queue, "*.request.json").OrderBy(x => x).Take(1))
        {
            string running = path.Replace(".request.json", ".running.json");
            string response = path.Replace(".request.json", ".result.json");
            File.Move(path, running); // Claim once; never replay an interrupted mutation.
            busy = true;
            Action<Result> finish = result =>
            {
                try
                {
                    File.WriteAllText(response + ".tmp", JsonUtility.ToJson(result, true));
                    File.Move(response + ".tmp", response);
                    File.Move(running, running.Replace(".running.json", ".done.json"));
                }
                finally { busy = false; }
            };
            try { Dispatch(JsonUtility.FromJson<Request>(File.ReadAllText(running)), finish); }
            catch (Exception e) { finish(new Result { error = e.ToString() }); }
        }
    }
    static void Dispatch(Request req, Action<Result> done)
    {
        try
        {
            if (req.command == "upload") { Upload(req, done); return; }
            if (req.command != "import")
            {
                var result = Execute(req); result.ok = true; done(result); return;
            }
            var manifest = CheckPackage(req);
            AssetDatabase.ImportPackageCallback completed = null, cancelled = null;
            AssetDatabase.ImportPackageFailedCallback failed = null;
            Action detach = () =>
            {
                AssetDatabase.importPackageCompleted -= completed;
                AssetDatabase.importPackageCancelled -= cancelled;
                AssetDatabase.importPackageFailed -= failed;
            };
            completed = name =>
            {
                detach();
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        foreach (var asset in manifest.assets)
                            if (AssetDatabase.AssetPathToGUID(asset.path) != asset.guid)
                                throw new InvalidOperationException("Imported GUID mismatch: " + asset.path);
                        var result = new Result { ok = true, command = "import" };
                        result.notes.Add("Imported and verified " + manifest.assets.Length + " assets with no existing path/GUID overwritten.");
                        done(result);
                    }
                    catch (Exception e) { done(new Result { error = e.ToString() }); }
                };
            };
            cancelled = name => { detach(); done(new Result { error = "Package import cancelled" }); };
            failed = (name, message) => { detach(); done(new Result { error = message }); };
            AssetDatabase.importPackageCompleted += completed;
            AssetDatabase.importPackageCancelled += cancelled;
            AssetDatabase.importPackageFailed += failed;
            try { AssetDatabase.ImportPackage(req.packagePath, false); }
            catch { detach(); throw; }
        }
        catch (Exception e) { done(new Result { error = e.ToString() }); }
    }
    static PackageManifest CheckPackage(Request req)
    {
        var manifest = JsonUtility.FromJson<PackageManifest>(File.ReadAllText(req.packageManifest));
        if (manifest.path_collisions != 0 || manifest.guid_collisions != 0 || manifest.code_assets != 0)
            throw new InvalidOperationException("Package preflight was not clean");
        using (var sha = SHA256.Create())
        using (var file = File.OpenRead(req.packagePath))
            if (BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant() != manifest.package_sha256)
                throw new InvalidOperationException("Package differs from inspected bytes");
        foreach (var asset in manifest.assets)
            if (!asset.path.StartsWith("Assets/", StringComparison.Ordinal) || asset.path.Split('/').Contains("..") ||
                File.Exists(asset.path) || Directory.Exists(asset.path) || !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(asset.guid)))
                throw new InvalidOperationException("Package collision or invalid path: " + asset.path);
        return manifest;
    }
    static IEnumerable<Component> Descriptors()
    {
        return Resources.FindObjectsOfTypeAll<Component>().Where(c => c != null &&
            c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor" &&
            c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded &&
            !EditorSceneManager.IsPreviewScene(c.gameObject.scene));
    }
    static GameObject Source(string id)
    {
        var candidates = Descriptors().Where(c => string.IsNullOrEmpty(id) ? c.gameObject.activeInHierarchy :
            GlobalObjectId.GetGlobalObjectIdSlow(c).ToString() == id).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("Select an exact sourceId; matching avatars: " + candidates.Length);
        return candidates[0].gameObject;
    }
    // Sibling indices make paths unambiguous even when outfit/bone names repeat.
    static string Key(Transform t, Transform root)
    {
        if (t == root) return "";
        var parts = new List<string>();
        while (t != root) { parts.Add(t.GetSiblingIndex().ToString()); t = t.parent; }
        parts.Reverse(); return string.Join("/", parts);
    }
    static Transform Find(Transform root, string key)
    {
        var t = root;
        if (string.IsNullOrEmpty(key)) return t;
        foreach (string part in key.Split('/')) t = t.GetChild(int.Parse(part));
        return t;
    }
    static void Inspect(GameObject root, Result r)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            r.nodes.Add(new Node { path = Key(t, root.transform), name = t.name, active = t.gameObject.activeSelf,
                position = t.localPosition, rotation = t.localRotation, scale = t.localScale,
                components = t.GetComponents<Component>().Select(c => c == null ? "MISSING" : c.GetType().FullName).ToArray() });
            r.missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
        }
        foreach (var s in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var skin = new Skin { path = Key(s.transform, root.transform), mesh = AssetDatabase.GetAssetPath(s.sharedMesh),
                materials = s.sharedMaterials.Select(m => m == null ? null : AssetDatabase.GetAssetPath(m)).ToArray() };
            if (s.sharedMesh == null) r.missingMeshes++;
            else for (int i = 0; i < s.sharedMesh.blendShapeCount; i++)
                skin.blendshapes.Add(new Shape { name = s.sharedMesh.GetBlendShapeName(i), weight = s.GetBlendShapeWeight(i) });
            r.nullMaterials += s.sharedMaterials.Count(m => m == null);
            r.skins.Add(skin);
        }
    }
    static void VerifyBase(Result before, GameObject clone, Result result)
    {
        foreach (var node in before.nodes)
        {
            var t = Find(clone.transform, node.path);
            if (!t.localPosition.Equals(node.position) || !t.localRotation.Equals(node.rotation) || !t.localScale.Equals(node.scale))
                throw new InvalidOperationException("Base transform changed: " + node.path);
        }
        foreach (var skin in before.skins)
        {
            var s = Find(clone.transform, skin.path).GetComponent<SkinnedMeshRenderer>();
            if (!s.sharedMaterials.Select(m => m == null ? null : AssetDatabase.GetAssetPath(m)).SequenceEqual(skin.materials))
                throw new InvalidOperationException("Base material assignments changed: " + skin.path);
            foreach (var shape in skin.blendshapes)
                if (s.GetBlendShapeWeight(s.sharedMesh.GetBlendShapeIndex(shape.name)) != shape.weight)
                    throw new InvalidOperationException("Base blendshape changed: " + skin.path + ":" + shape.name);
        }
        result.baseTransformsPreserved = result.baseBlendshapesPreserved = result.baseMaterialsPreserved = true;
    }
    static void CheckSceneReferences(GameObject clone)
    {
        foreach (var component in clone.GetComponentsInChildren<Component>(true))
        {
            if (component == null) continue;
            using (var serialized = new SerializedObject(component))
            {
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    var obj = value as GameObject;
                    if (value is Component reference) obj = reference.gameObject;
                    if (obj != null && !EditorUtility.IsPersistent(obj) && obj.scene.IsValid() && obj.scene != clone.scene)
                        throw new InvalidOperationException("External scene reference needs explicit migration: " + component.name + "." + property.propertyPath);
                }
            }
        }
    }
    static void ResolveModularReferences(GameObject clone, Result result)
    {
        int repaired = 0;
        foreach (var component in clone.GetComponentsInChildren<AvatarTagComponent>(true))
        using (var serialized = new SerializedObject(component))
        {
            var property = serialized.GetIterator();
            while (property.Next(true))
            {
                if (property.type != "AvatarObjectReference") continue;
                var target = property.FindPropertyRelative("targetObject");
                var resolved = AvatarObjectReference.Get(property);
                if (resolved != null && target.objectReferenceValue != resolved)
                {
                    target.objectReferenceValue = resolved;
                    repaired++;
                }
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        result.notes.Add("Resolved MA path references on clone: " + repaired);
    }
    static Result Execute(Request req)
    {
        var r = new Result { command = req.command };
        if (req.command == "refresh")
        {
            AssetDatabase.Refresh(); RefreshPreview(); return r;
        }
        if (!Application.isBatchMode && req.command == "open")
        {
            OpenDraft(req.scene); Frame(Source(req.sourceId)); return r;
        }
        foreach (var d in Descriptors()) r.avatars.Add(new Avatar { id = GlobalObjectId.GetGlobalObjectIdSlow(d).ToString(),
            name = d.name, scene = d.gameObject.scene.path, active = d.gameObject.activeInHierarchy });
        if (req.command == "list") return r;
        if (req.command == "scene-view") return SceneViewShot(req, r);
        var source = Source(req.sourceId);
        if (!Application.isBatchMode && !string.IsNullOrEmpty(req.scene) && source.scene.path != req.scene)
            throw new InvalidOperationException("Recipe scene does not match the loaded avatar scene");
        r.sourceId = r.avatars.Single(a => a.id == GlobalObjectId.GetGlobalObjectIdSlow(
            source.GetComponents<Component>().First(c => c != null && c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")).ToString()).id;
        r.sceneDirty = source.scene.isDirty;
        if (req.command == "inspect") { Inspect(source, r); return r; }
        if (req.command == "audit-meshes") return AuditMeshes(source, req, r);
        if (req.command == "swap-meshes") return SwapMeshes(source, req, r);
        if (req.command == "inspect-accessory" || req.command == "attach-accessory") return Accessory(source, req, r);
        if (req.command == "export-accessory") return ExportAccessory(source, req, r);
        if (req.command == "fix-accessory-bounds" || req.command == "audit-accessory-bounds") return AccessoryBounds(source, req, r);
        if (req.command == "attach-prefab") return AttachPrefab(source, req, r);
        if (req.command == "place-prefab") return PlacePrefab(source, req, r);
        if (req.command == "closeup") return Closeup(source, req, r);
        if (req.command == "add-toggle") return AddToggle(source, req, r);
        if (req.command == "add-fury-slider") return AddFurySlider(source, req, r);
        if (req.command == "save") return SaveDraft(source, r);
        if (req.command == "frame") { Frame(source); return r; }
        if (req.command == "edit")
        {
            RequireDraft(source.scene.path);
            if (req.edits == null || req.edits.Length == 0) throw new ArgumentException("No edits supplied");
            var targets = new List<UnityEngine.Object>();
            foreach (var edit in req.edits)
            {
                var t = Find(source.transform, edit.path);
                if (string.IsNullOrEmpty(edit.expectedName) || t.name != edit.expectedName)
                    throw new ArgumentException("Each edit requires a matching expectedName to guard hierarchy changes");
                if (edit.kind == "active")
                {
                    if (t == source.transform) throw new ArgumentException("Cannot disable the avatar root");
                    targets.Add(t.gameObject);
                }
                else if (edit.kind == "scale" || edit.kind == "position" || edit.kind == "rotation")
                {
                    if (!float.IsFinite(edit.value.x) || !float.IsFinite(edit.value.y) || !float.IsFinite(edit.value.z))
                        throw new ArgumentException("Non-finite transform value");
                    if (edit.kind == "scale" && (edit.value.x <= 0 || edit.value.y <= 0 || edit.value.z <= 0))
                        throw new ArgumentException("Scale components must be positive");
                    targets.Add(t);
                }
                else if (edit.kind == "blendshape")
                {
                    var skin = t.GetComponent<SkinnedMeshRenderer>();
                    if (skin == null || skin.sharedMesh == null || skin.sharedMesh.GetBlendShapeIndex(edit.shape) < 0 || !float.IsFinite(edit.weight))
                        throw new ArgumentException("Invalid blendshape/weight");
                    targets.Add(skin);
                }
                else if (edit.kind == "material")
                {
                    var renderer = t.GetComponent<Renderer>();
                    if (renderer == null || edit.slot < 0 || edit.slot >= renderer.sharedMaterials.Length ||
                        AssetDatabase.LoadAssetAtPath<Material>(edit.material) == null)
                        throw new ArgumentException("Invalid material assignment");
                    targets.Add(renderer);
                }
                else if (edit.kind == "remove-component")
                {
                    // Only an Animator without a controller: a nested Animator blocks the
                    // avatar's own animations below it and otherwise does nothing.
                    if (edit.component != "UnityEngine.Animator" || t == source.transform)
                        throw new ArgumentException("remove-component only removes a non-root Animator");
                    var animator = t.GetComponent<Animator>();
                    if (animator == null || animator.runtimeAnimatorController != null)
                        throw new ArgumentException("No controller-less Animator on " + t.name);
                }
                else throw new ArgumentException("Unsupported edit kind: " + edit.kind);
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            string label = "Avatar Agent: " + (string.IsNullOrWhiteSpace(req.label) ? "Edit draft" : req.label);
            Undo.SetCurrentGroupName(label);
            try
            {
                Undo.RegisterCompleteObjectUndo(targets.Distinct().ToArray(), label);
                foreach (var edit in req.edits)
                {
                    var t = Find(source.transform, edit.path);
                    switch (edit.kind)
                    {
                        case "active": t.gameObject.SetActive(edit.active); break;
                        case "scale": t.localScale = edit.value; break;
                        case "position": t.localPosition = edit.value; break;
                        case "rotation": t.localRotation = Quaternion.Euler(edit.value); break;
                        case "blendshape":
                            var skin = t.GetComponent<SkinnedMeshRenderer>();
                            skin.SetBlendShapeWeight(skin.sharedMesh.GetBlendShapeIndex(edit.shape), edit.weight); break;
                        case "material":
                            var renderer = t.GetComponent<Renderer>(); var materials = renderer.sharedMaterials;
                            materials[edit.slot] = AssetDatabase.LoadAssetAtPath<Material>(edit.material);
                            renderer.sharedMaterials = materials; break;
                        case "remove-component":
                            Undo.DestroyObjectImmediate(t.GetComponent<Animator>()); break;
                    }
                }
                foreach (var target in targets.Distinct())
                    if (PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(group);
                EditorSceneManager.MarkSceneDirty(source.scene);
                r.appliedEdits = req.edits.Length;
                r.notes.Add(label + " (one Undo step; not saved)");
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            finally { Undo.IncrementCurrentGroup(); RefreshPreview(); }
            r.sceneDirty = source.scene.isDirty;
            Inspect(source, r); return r;
        }
        if (req.command == "undo" || req.command == "redo")
        {
            RequireDraft(source.scene.path);
            if (Application.isBatchMode) throw new InvalidOperationException("Undo/redo requires the live editor session");
            if (req.command == "undo") Undo.PerformUndo(); else Undo.PerformRedo();
            RefreshPreview(); r.sceneDirty = source.scene.isDirty; Inspect(source, r); return r;
        }
        if (req.command == "checkpoint")
        {
            RequireDraft(source.scene.path); RequireDraft(req.outputScene);
            if (File.Exists(req.outputScene)) throw new IOException("Checkpoint exists");
            Directory.CreateDirectory(Path.GetDirectoryName(req.outputScene));
            AssetDatabase.Refresh();
            if (!EditorSceneManager.SaveScene(source.scene, req.outputScene, true)) throw new IOException("Checkpoint save failed");
            r.outputScene = req.outputScene; r.sceneDirty = source.scene.isDirty;
            r.notes.Add("Saved a scene copy; current draft path and unsaved state retained."); return r;
        }
        if (req.command == "preview")
        {
            if (string.IsNullOrEmpty(req.previewDirectory) || !Path.IsPathRooted(req.previewDirectory))
                throw new ArgumentException("previewDirectory must be an absolute private path");
            Directory.CreateDirectory(req.previewDirectory);
            foreach (string view in new[] { "front", "side", "back" })
            {
                string path = Path.Combine(req.previewDirectory, view + ".png");
                if (File.Exists(path)) throw new IOException("Preview exists: " + path);
                var utility = new PreviewRenderUtility();
                try
                {
                    var instance = UnityEngine.Object.Instantiate(source);
                    instance.transform.position = Vector3.zero; instance.transform.rotation = Quaternion.identity;
                    utility.AddSingleGO(instance);
                    var renderers = instance.GetComponentsInChildren<Renderer>().Where(x => x.enabled && x.gameObject.activeInHierarchy).ToArray();
                    if (renderers.Length == 0) throw new InvalidOperationException("No visible renderers");
                    var bounds = renderers[0].bounds;
                    foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                    var direction = view == "front" ? Vector3.forward : view == "back" ? Vector3.back : Vector3.right;
                    float radius = Mathf.Max(bounds.extents.magnitude, 0.1f);
                    utility.camera.fieldOfView = 30;
                    utility.camera.nearClipPlane = 0.01f; utility.camera.farClipPlane = radius * 20 + 10;
                    utility.camera.transform.position = bounds.center + direction * radius * 4;
                    utility.camera.transform.LookAt(bounds.center);
                    utility.camera.clearFlags = CameraClearFlags.SolidColor;
                    utility.camera.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1);
                    utility.lights[0].intensity = 1.1f;
                    utility.lights[0].transform.rotation = Quaternion.Euler(35, -30, 0);
                    utility.lights[1].intensity = 0.7f;
                    utility.ambientColor = new Color(0.45f, 0.45f, 0.45f);
                    utility.BeginStaticPreview(new Rect(0, 0, 1024, 1024));
                    utility.Render(true);
                    var texture = utility.EndStaticPreview();
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(texture);
                    r.notes.Add(path);
                }
                finally { utility.Cleanup(); }
            }
            r.notes.Add("Authoring previews only; build-time MA/VRCFury processing is not applied.");
            return r;
        }
        if (req.command != "assemble") throw new ArgumentException("Unknown command: " + req.command);
        string output = req.outputScene;
        if (string.IsNullOrEmpty(output) || !output.StartsWith("Assets/VRUnityGenerated/", StringComparison.Ordinal) ||
            !output.EndsWith(".unity", StringComparison.Ordinal) || output.Split('/').Any(x => x == ".." || x == ".") || output.Contains('\\'))
            throw new ArgumentException("outputScene must be a new scene under Assets/VRUnityGenerated/");
        if (File.Exists(output)) throw new IOException("Refusing to replace existing scene: " + output);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(req.outfitPrefab);
        if (prefab == null || !PrefabUtility.IsPartOfPrefabAsset(prefab)) throw new ArgumentException("Missing outfit prefab");
        var before = new Result(); Inspect(source, before);
        var previousActive = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        GameObject clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(source);
            SceneManager.MoveGameObjectToScene(clone, scene);
            clone.name = source.name + " + " + prefab.name;
            // Unpack only the clone; shared meshes/materials/controllers remain unchanged.
            if (PrefabUtility.IsPartOfPrefabInstance(clone))
                PrefabUtility.UnpackPrefabInstance(clone, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            foreach (var key in req.disablePaths ?? new string[0])
            {
                if (string.IsNullOrEmpty(key)) throw new ArgumentException("Cannot disable avatar root");
                Find(clone.transform, key).gameObject.SetActive(false);
            }
            var outfit = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            outfit.transform.SetParent(clone.transform, false);
            ResolveModularReferences(clone, r);
            var mergers = outfit.GetComponentsInChildren<ModularAvatarMergeArmature>(true);
            if (mergers.Length == 0) throw new InvalidOperationException("Outfit needs configured MA Merge Armature; automatic rig guessing is unsupported");
            foreach (var merge in mergers)
            {
                var target = merge.mergeTargetObject;
                if (target == null || !target.transform.IsChildOf(clone.transform) || target.transform.IsChildOf(outfit.transform))
                    throw new InvalidOperationException("Unresolved or incorrect MA merge target: " + merge.name);
                var mapping = merge.GetBonesMapping();
                if (mapping == null || mapping.Count == 0) throw new InvalidOperationException("No matching outfit bones");
                if (req.matchOutfitBoneScale) mapping.Add((target.transform, merge.transform));
                foreach (var pair in mapping)
                {
                    if (req.matchOutfitBoneScale)
                    {
                        pair.Item2.localScale = pair.Item1.localScale;
                        var baseAdjuster = pair.Item1.GetComponent<ModularAvatarScaleAdjuster>();
                        var outfitAdjuster = pair.Item2.GetComponent<ModularAvatarScaleAdjuster>();
                        if (baseAdjuster != null)
                        {
                            if (outfitAdjuster == null) outfitAdjuster = pair.Item2.gameObject.AddComponent<ModularAvatarScaleAdjuster>();
                            outfitAdjuster.Scale = baseAdjuster.Scale;
                        }
                        else if (outfitAdjuster != null) UnityEngine.Object.DestroyImmediate(outfitAdjuster);
                    }
                    r.matchedOutfitBones++;
                }
                r.notes.Add("Merge target resolved: " + target.name);
            }
            VerifyBase(before, clone, r);
            CheckSceneReferences(clone);
            Inspect(clone, r);
            if (r.missingScripts > before.missingScripts || r.missingMeshes > before.missingMeshes || r.nullMaterials > before.nullMaterials)
                throw new InvalidOperationException("Outfit introduced missing scripts/meshes/materials; refusing to save");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            AssetDatabase.Refresh();
            if (!EditorSceneManager.SaveScene(scene, output, false)) throw new IOException("Could not save generated scene");
            r.outputScene = output;
            r.notes.Add("New authoring scene only; no build, upload, material edits, or source-scene save.");
        }
        finally
        {
            SceneManager.SetActiveScene(previousActive);
            EditorSceneManager.CloseScene(scene, true);
        }
        return r;
    }
}
