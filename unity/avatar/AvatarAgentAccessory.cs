using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using nadena.dev.modular_avatar.core;

public static partial class AvatarAgent
{
    [Serializable] public class CullingReport
    {
        public Bounds previousLocalBounds, actualRootBounds, correctedLocalBounds;
        public int vertices, verticesOutsideBefore, verticesOutsideAfter;
        public int cameraSamples, falseCullSamplesBefore, falseCullSamplesAfter;
    }
    static Vector3[] RootSpaceVertices(SkinnedMeshRenderer renderer)
    {
        var baked = new Mesh();
        try
        {
            renderer.BakeMesh(baked);
            var root = renderer.rootBone != null ? renderer.rootBone : renderer.transform;
            var matrix = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            return baked.vertices.Select(v => matrix.MultiplyPoint3x4(v)).ToArray();
        }
        finally { UnityEngine.Object.DestroyImmediate(baked); }
    }
    static Bounds MotionBounds(Vector3[] vertices)
    {
        float radius = vertices.Max(v => v.magnitude) + 0.15f;
        return new Bounds(Vector3.zero, Vector3.one * radius * 2);
    }
    static void ConfigureAccessoryBounds(GameObject accessory, SkinnedMeshRenderer renderer, bool undo)
    {
        var bounds = MotionBounds(RootSpaceVertices(renderer));
        if (undo) Undo.RecordObject(renderer, "Avatar Agent: correct accessory culling bounds");
        renderer.localBounds = bounds;
        // Keep a fixed, padded box rather than recomputing skinning while offscreen.
        renderer.updateWhenOffscreen = false;
        var settings = accessory.GetComponent<ModularAvatarMeshSettings>();
        if (settings == null) settings = undo ? Undo.AddComponent<ModularAvatarMeshSettings>(accessory) : accessory.AddComponent<ModularAvatarMeshSettings>();
        else if (undo) Undo.RecordObject(settings, "Avatar Agent: correct accessory culling bounds");
        settings.InheritBounds = ModularAvatarMeshSettings.InheritMode.Set;
        settings.RootBone = new AvatarObjectReference(renderer.rootBone.gameObject);
        settings.Bounds = bounds;
    }
    static Bounds WorldBox(Bounds box, Transform root)
    {
        var result = new Bounds(root.TransformPoint(box.min), Vector3.zero);
        for (int i = 0; i < 8; i++) result.Encapsulate(root.TransformPoint(new Vector3(
            (i & 1) == 0 ? box.min.x : box.max.x, (i & 2) == 0 ? box.min.y : box.max.y, (i & 4) == 0 ? box.min.z : box.max.z)));
        return result;
    }
    static Result AccessoryBounds(GameObject avatar, Request req, Result result)
    {
        var accessory = avatar.GetComponentsInChildren<Transform>(true).Single(t => t.name == req.accessoryName).gameObject;
        var renderer = accessory.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single();
        var vertices = RootSpaceVertices(renderer);
        var actual = new Bounds(vertices[0], Vector3.zero);
        foreach (var v in vertices) actual.Encapsulate(v);
        var report = new CullingReport { previousLocalBounds = renderer.localBounds, actualRootBounds = actual,
            vertices = vertices.Length, verticesOutsideBefore = vertices.Count(v => !renderer.localBounds.Contains(v)) };
        if (req.command == "fix-accessory-bounds")
        {
            RequireDraft(avatar.scene.path);
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar Agent: correct accessory culling bounds");
            try
            {
                ConfigureAccessoryBounds(accessory, renderer, true);
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group);
                EditorSceneManager.MarkSceneDirty(avatar.scene);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            finally { Undo.IncrementCurrentGroup(); RefreshPreview(); }
        }
        report.correctedLocalBounds = renderer.localBounds;
        report.verticesOutsideAfter = vertices.Count(v => !renderer.localBounds.Contains(v));
        var root = renderer.rootBone != null ? renderer.rootBone : renderer.transform;
        var actualWorld = WorldBox(actual, root);
        var beforeWorld = WorldBox(report.previousLocalBounds, root);
        var afterWorld = WorldBox(renderer.localBounds, root);
        var preview = new PreviewRenderUtility();
        try
        {
            var camera = preview.camera; camera.fieldOfView = 35; camera.aspect = 1;
            camera.nearClipPlane = 0.01f; camera.farClipPlane = 20;
            float distance = Mathf.Max(actualWorld.extents.magnitude * 2, 0.35f);
            foreach (float pitch in new[] { -45f, 0f, 45f })
            for (int yaw = 0; yaw < 360; yaw += 15)
            {
                camera.transform.position = actualWorld.center + Quaternion.Euler(pitch, yaw, 0) * Vector3.forward * distance;
                camera.transform.LookAt(actualWorld.center);
                var planes = GeometryUtility.CalculateFrustumPlanes(camera);
                if (!GeometryUtility.TestPlanesAABB(planes, actualWorld)) continue;
                report.cameraSamples++;
                if (!GeometryUtility.TestPlanesAABB(planes, beforeWorld)) report.falseCullSamplesBefore++;
                if (!GeometryUtility.TestPlanesAABB(planes, afterWorld)) report.falseCullSamplesAfter++;
            }
        }
        finally { preview.Cleanup(); }
        result.culling = report; result.sceneDirty = avatar.scene.isDirty;
        result.notes.Add("Bounds measured in the renderer root bone's space; includes motion margin and explicit MA build-time mesh settings.");
        return result;
    }
    static Result ExportAccessory(GameObject avatar, Request req, Result result)
    {
        var accessory = avatar.GetComponentsInChildren<Transform>(true).Single(t => t.name == req.accessoryName);
        if (string.IsNullOrEmpty(req.outputPrefab) || !req.outputPrefab.StartsWith("Assets/VRUnityGenerated/", StringComparison.Ordinal) ||
            !req.outputPrefab.EndsWith(".prefab", StringComparison.Ordinal) || req.outputPrefab.Contains("..") || req.outputPrefab.Contains('\\') ||
            File.Exists(req.outputPrefab)) throw new ArgumentException("Expected a new generated prefab path");
        Directory.CreateDirectory(Path.GetDirectoryName(req.outputPrefab)); AssetDatabase.Refresh();
        var prefab = PrefabUtility.SaveAsPrefabAsset(accessory.gameObject, req.outputPrefab);
        if (prefab == null) throw new IOException("Could not save accessory prefab");
        var transforms = new HashSet<Transform>(prefab.GetComponentsInChildren<Transform>(true));
        foreach (var renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer.sharedMesh == null || renderer.sharedMaterials.Any(m => m == null) ||
                renderer.bones.Any(b => b == null || !transforms.Contains(b)) || !transforms.Contains(renderer.rootBone))
                throw new InvalidOperationException("Accessory prefab contains missing/external renderer dependencies");
        result.notes.Add("Saved reusable head accessory: " + req.outputPrefab);
        result.notes.Add("All skinned renderer bones are internal to the prefab; mesh/material assets remain package dependencies.");
        return result;
    }
    static Result Accessory(GameObject avatar, Request req, Result result)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(req.outfitPrefab);
        if (prefab == null) throw new ArgumentException("Missing source prefab");
        var source = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(s => s.name == req.accessoryRenderer);
        if (source.sharedMesh == null || source.sharedMaterials.Length == 0 || source.sharedMaterials.Any(m => m == null))
            throw new InvalidOperationException("Source mesh/material dependencies are missing; import companion assets first");
        var used = new HashSet<int>();
        using (var weights = source.sharedMesh.GetAllBoneWeights())
            foreach (var weight in weights) if (weight.weight > 0) used.Add(weight.boneIndex);
        var anchor = source.bones.Single(b => b != null && b.name == "Head");
        foreach (int index in used)
            if (source.bones[index] == null || !source.bones[index].IsChildOf(anchor))
                throw new InvalidOperationException("Accessory has weighted bones outside its source Head");
        result.notes.Add("Mesh: " + source.sharedMesh.name + "; weighted bones: " + string.Join(", ", used.Select(i => source.bones[i].name)));
        result.notes.Add("Source head components: " + string.Join(", ", anchor.GetComponentsInChildren<Component>(true).Where(c => c != null && !(c is Transform)).Select(c => c.name + ":" + c.GetType().FullName)));
        if (req.command == "inspect-accessory") return result;
        RequireDraft(avatar.scene.path);
        var targetAnchor = Find(avatar.transform, req.anchorPath);
        if (targetAnchor.name != "Head") throw new ArgumentException("Target must be an inspected Head bone");
        if (avatar.GetComponentsInChildren<Transform>(true).Any(t => t.name == req.accessoryName))
            throw new InvalidOperationException("Accessory already exists");
        var keep = new HashSet<Transform> { anchor };
        foreach (int index in used)
            for (var bone = source.bones[index]; bone != anchor; bone = bone.parent) keep.Add(bone);
        foreach (var component in anchor.GetComponentsInChildren<Component>(true))
            if (component != null && component.GetType().FullName.EndsWith(".VRCPhysBone", StringComparison.Ordinal) && keep.Contains(component.transform))
                foreach (var child in component.GetComponentsInChildren<Transform>(true)) keep.Add(child);
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        const string label = "Avatar Agent: kitbash standalone accessory";
        Undo.SetCurrentGroupName(label);
        GameObject container = null;
        try
        {
            container = new GameObject(req.accessoryName);
            container.transform.SetParent(targetAnchor, false);
            container.transform.localPosition = Vector3.zero;
            container.transform.localRotation = Quaternion.identity;
            container.transform.localScale = Vector3.one;
            var skeleton = UnityEngine.Object.Instantiate(anchor.gameObject, container.transform, false);
            if (PrefabUtility.IsPartOfPrefabInstance(skeleton))
                PrefabUtility.UnpackPrefabInstance(skeleton, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            skeleton.name = "Accessory Rig";
            skeleton.transform.localPosition = Vector3.zero;
            skeleton.transform.localRotation = Quaternion.identity;
            skeleton.transform.localScale = Vector3.one;
            var boneMap = new Dictionary<Transform, Transform>();
            foreach (var bone in keep) boneMap[bone] = Find(skeleton.transform, Key(bone, anchor));
            var cloneTransforms = skeleton.GetComponentsInChildren<Transform>(true);
            var keptClones = new HashSet<Transform>(boneMap.Values);
            // Remove unrelated bones and all non-physics components.
            foreach (var component in skeleton.GetComponentsInChildren<Component>(true))
                if (component != null && !(component is Transform) &&
                    !component.GetType().FullName.StartsWith("VRC.SDK3.Dynamics.PhysBone.", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(component);
            foreach (var t in cloneTransforms.Reverse())
                if (t != null && !keptClones.Contains(t)) UnityEngine.Object.DestroyImmediate(t.gameObject);
            var rendererObject = new GameObject(req.accessoryRenderer);
            rendererObject.transform.SetParent(container.transform, false);
            Matrix4x4 matrix = anchor.worldToLocalMatrix * source.transform.localToWorldMatrix;
            rendererObject.transform.localPosition = matrix.GetColumn(3);
            rendererObject.transform.localRotation = matrix.rotation;
            rendererObject.transform.localScale = matrix.lossyScale;
            var renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();
            EditorUtility.CopySerialized(source, renderer);
            renderer.bones = source.bones.Select((b, i) => used.Contains(i) ? boneMap[b] : skeleton.transform).ToArray();
            renderer.rootBone = skeleton.transform;
            // localBounds follows rootBone space, not BakeMesh's renderer-local space.
            ConfigureAccessoryBounds(container, renderer, false);
            // Reject source-prefab references left by physics cloning rather than saving broken references.
            foreach (var component in container.GetComponentsInChildren<Component>(true))
            using (var serialized = new SerializedObject(component))
            {
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    var referenced = value is Component c ? c.gameObject : value as GameObject;
                    if (component == container.transform && referenced == targetAnchor.gameObject) continue;
                    if (referenced != null && !referenced.transform.IsChildOf(container.transform))
                        throw new InvalidOperationException("External accessory reference: " + component.name + "." + property.propertyPath + " -> " + referenced.name);
                }
            }
            CheckSceneReferences(container);
            Undo.RegisterCreatedObjectUndo(container, label); // Capture the finished hierarchy for Undo/Redo.
            Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(avatar.scene);
            result.sceneDirty = true;
            result.notes.Add("Attached under avatar Head with only weighted bow bones and physics; materials/mesh shared with source.");
            Inspect(avatar, result); Frame(avatar); return result;
        }
        catch
        {
            Undo.RevertAllDownToGroup(group);
            if (container != null) UnityEngine.Object.DestroyImmediate(container);
            throw;
        }
        finally { Undo.IncrementCurrentGroup(); RefreshPreview(); }
    }
}
