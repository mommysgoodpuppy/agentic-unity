using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static partial class AvatarAgent
{
    [Serializable] public class MeshReplacement
    {
        public string path, expectedName, asset, meshName;
    }
    [Serializable] public class MeshRecord
    {
        public string path, objectName, asset, meshName;
        public long localId, triangles;
        public int vertices, submeshes, bindposes;
        public string[] bones, shapes, materials;
    }
    static Mesh RendererMesh(Renderer renderer)
    {
        if (renderer is SkinnedMeshRenderer skin) return skin.sharedMesh;
        var filter = renderer.GetComponent<MeshFilter>();
        return filter != null ? filter.sharedMesh : null;
    }
    static MeshRecord DescribeMesh(Renderer renderer, Transform root)
    {
        var mesh = RendererMesh(renderer);
        if (mesh == null) return null;
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId);
        long triangles = 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
            if (mesh.GetTopology(i) == MeshTopology.Triangles) triangles += (long)mesh.GetIndexCount(i) / 3;
        return new MeshRecord { path = Key(renderer.transform, root), objectName = renderer.name,
            asset = AssetDatabase.GetAssetPath(mesh), meshName = mesh.name, localId = localId,
            vertices = mesh.vertexCount, triangles = triangles, submeshes = mesh.subMeshCount, bindposes = mesh.bindposes.Length,
            bones = renderer is SkinnedMeshRenderer skin ? skin.bones.Select(b => b == null ? null : b.name).ToArray() : new string[0],
            shapes = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray(),
            materials = renderer.sharedMaterials.Select(m => m == null ? null : AssetDatabase.GetAssetPath(m)).ToArray() };
    }
    static Result AuditMeshes(GameObject source, Request req, Result result)
    {
        foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
        {
            var record = DescribeMesh(renderer, source.transform);
            if (record != null) result.meshes.Add(record);
        }
        var paths = new HashSet<string>(result.meshes.Select(m => m.asset));
        foreach (var guid in AssetDatabase.FindAssets("t:Model", req.modelRoots)) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
        foreach (var path in paths.OrderBy(p => p))
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model == null) continue;
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                var record = DescribeMesh(renderer, model.transform);
                if (record != null) result.meshCandidates.Add(record);
            }
        }
        result.notes.Add("Audited " + result.meshes.Count + " current meshes and " + result.meshCandidates.Count + " source renderers.");
        return result;
    }
    static Result SwapMeshes(GameObject source, Request req, Result result)
    {
        RequireDraft(source.scene.path);
        if (req.meshReplacements.Length == 0) throw new ArgumentException("No mesh replacements supplied");
        var plans = new List<(SkinnedMeshRenderer renderer, Mesh mesh, Dictionary<string, float> weights, Material[] materials)>();
        foreach (var replacement in req.meshReplacements)
        {
            var target = Find(source.transform, replacement.path);
            if (target.name != replacement.expectedName) throw new ArgumentException("Target name changed");
            var skin = target.GetComponent<SkinnedMeshRenderer>();
            if (skin == null || skin.sharedMesh == null) throw new ArgumentException("Expected skinned mesh renderer");
            var mesh = AssetDatabase.LoadAllAssetsAtPath(replacement.asset).OfType<Mesh>().Single(m => m.name == replacement.meshName);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(replacement.asset);
            var original = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.sharedMesh == mesh);
            if (!skin.bones.Select(b => b == null ? null : b.name).SequenceEqual(original.bones.Select(b => b == null ? null : b.name)))
                throw new InvalidOperationException("Bone order/names differ; explicit rig remapping required: " + target.name);
            var oldPoses = skin.sharedMesh.bindposes; var newPoses = mesh.bindposes;
            if (oldPoses.Length != newPoses.Length) throw new InvalidOperationException("Bindpose count differs");
            float maxError = 0;
            for (int i = 0; i < oldPoses.Length; i++) for (int e = 0; e < 16; e++)
                maxError = Mathf.Max(maxError, Mathf.Abs(oldPoses[i][e] - newPoses[i][e]));
            if (maxError > 0.001f) throw new InvalidOperationException("Bindpose mismatch: " + target.name + " max error " + maxError);
            var weights = new Dictionary<string, float>();
            for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++)
            {
                string name = skin.sharedMesh.GetBlendShapeName(i); float weight = skin.GetBlendShapeWeight(i);
                if (weight != 0 && mesh.GetBlendShapeIndex(name) < 0)
                    throw new InvalidOperationException("Replacement would lose an active blendshape: " + name);
                weights.Add(name, weight);
            }
            var materials = skin.sharedMaterials;
            if (materials.Length == 1 && mesh.subMeshCount > 1)
                materials = Enumerable.Repeat(materials[0], mesh.subMeshCount).ToArray();
            if (materials.Length != mesh.subMeshCount || materials.Any(m => m == null))
                throw new InvalidOperationException("Explicit material-slot mapping required");
            result.notes.Add(target.name + ": " + skin.sharedMesh.name + " -> " + mesh.name + "; bindpose max error " + maxError);
            result.notes.Add(target.name + ": old bounds " + skin.sharedMesh.bounds + "; source bounds " + mesh.bounds);
            plans.Add((skin, mesh, weights, materials));
        }
        if (plans.Select(p => p.renderer).Distinct().Count() != plans.Count) throw new ArgumentException("Duplicate mesh target");
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        const string label = "Avatar Agent: restore source mesh resolution";
        Undo.SetCurrentGroupName(label);
        try
        {
            Undo.RegisterCompleteObjectUndo(plans.Select(p => (UnityEngine.Object)p.renderer).ToArray(), label);
            foreach (var plan in plans)
            {
                plan.renderer.sharedMesh = plan.mesh;
                plan.renderer.sharedMaterials = plan.materials;
                for (int i = 0; i < plan.mesh.blendShapeCount; i++)
                    plan.renderer.SetBlendShapeWeight(i, plan.weights.TryGetValue(plan.mesh.GetBlendShapeName(i), out var weight) ? weight : 0);
                if (PrefabUtility.IsPartOfPrefabInstance(plan.renderer)) PrefabUtility.RecordPrefabInstancePropertyModifications(plan.renderer);
                result.meshes.Add(DescribeMesh(plan.renderer, source.transform));
            }
            Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(source.scene); result.sceneDirty = true;
            result.appliedEdits = plans.Count;
            result.notes.Add("Renderer bones, root bones, transforms, and material assets retained. New material slots repeat the existing material where needed.");
            Inspect(source, result); return result;
        }
        catch { Undo.RevertAllDownToGroup(group); throw; }
        finally { Undo.IncrementCurrentGroup(); RefreshPreview(); }
    }
}
