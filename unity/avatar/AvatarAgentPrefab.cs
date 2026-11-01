using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static partial class AvatarAgent
{
    // Base centre and unit axis (base to tip) of a long, spike-like renderer, in world space.
    // The vertex farthest from the centroid lies on the wide base rim (for a cone the rim is
    // farther from the centroid than the tip); the tip is the vertex farthest from that rim
    // point, and the base is the centroid of the vertices farthest from the tip.
    static (Vector3 basePoint, Vector3 axis, float length) RendererAxis(Renderer renderer)
    {
        Vector3[] world;
        if (renderer is SkinnedMeshRenderer skin)
        {
            var baked = new Mesh();
            try
            {
                // BakeMesh leaves out the renderer's own scale, so convert without it.
                skin.BakeMesh(baked);
                var toWorld = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
                world = baked.vertices.Select(v => toWorld.MultiplyPoint3x4(v)).ToArray();
            }
            finally { UnityEngine.Object.DestroyImmediate(baked); }
        }
        else
        {
            var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) throw new ArgumentException("Alignment renderer has no mesh");
            world = mesh.vertices.Select(v => renderer.transform.TransformPoint(v)).ToArray();
        }
        if (world.Length < 4) throw new ArgumentException("Alignment renderer has too few vertices");
        var centroid = world.Aggregate(Vector3.zero, (a, v) => a + v) / world.Length;
        var rim = world.OrderByDescending(v => (v - centroid).sqrMagnitude).First();
        var tip = world.OrderByDescending(v => (v - rim).sqrMagnitude).First();
        float far = world.Max(v => (v - tip).magnitude);
        var ring = world.Where(v => (v - tip).magnitude >= far * 0.97f).ToArray();
        var basePoint = ring.Aggregate(Vector3.zero, (a, v) => a + v) / ring.Length;
        return (basePoint, (tip - basePoint).normalized, (tip - basePoint).magnitude);
    }

    // Instantiates a prefab under a guarded anchor transform as one Undo step.
    // With alignRenderer/alignChild, rotates and moves the instance so that alignChild's +Y
    // runs along that renderer's base-to-tip axis, starting at its base.
    // With realign, moves the anchor's existing child of that name instead of adding one.
    static Result AttachPrefab(GameObject avatar, Request req, Result result)
    {
        RequireDraft(avatar.scene.path);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(req.prefab);
        if (prefab == null) throw new ArgumentException("Prefab not found: " + req.prefab);
        var anchor = Find(avatar.transform, req.anchorPath);
        if (string.IsNullOrEmpty(req.anchorName) || anchor.name != req.anchorName)
            throw new ArgumentException("anchorName must match the inspected anchor transform");
        var name = string.IsNullOrWhiteSpace(req.instanceName) ? prefab.name : req.instanceName;
        var existing = anchor.Cast<Transform>().Where(c => c.name == name).ToArray();
        if (req.realign ? existing.Length != 1 : existing.Length != 0)
            throw new InvalidOperationException(req.realign ? "realign needs exactly one existing child named " + name
                                                            : "Anchor already has a child named " + name);
        Renderer alignTo = null;
        if (!string.IsNullOrEmpty(req.alignRenderer))
        {
            alignTo = Find(avatar.transform, req.alignRenderer).GetComponent<Renderer>();
            if (alignTo == null) throw new ArgumentException("alignRenderer has no renderer");
            if (string.IsNullOrEmpty(req.alignChild)) throw new ArgumentException("alignRenderer requires alignChild");
        }

        string label = "Avatar Agent: " + (string.IsNullOrWhiteSpace(req.label) ? "Attach " + name : req.label);
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        try
        {
            GameObject instance;
            if (req.realign)
            {
                instance = existing[0].gameObject;
                if (PrefabUtility.GetCorrespondingObjectFromSource(instance) != prefab)
                    throw new InvalidOperationException(name + " is not an instance of " + req.prefab);
                Undo.RecordObject(instance.transform, label);
            }
            else
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor);
                Undo.RegisterCreatedObjectUndo(instance, label);
                instance.name = name;
            }
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            if (alignTo != null)
            {
                var child = instance.transform.Find(req.alignChild);
                if (child == null) throw new ArgumentException("Prefab has no child " + req.alignChild);
                var (basePoint, axis, length) = RendererAxis(alignTo);
                instance.transform.rotation = Quaternion.FromToRotation(child.up, axis) * instance.transform.rotation;
                instance.transform.position += basePoint - child.position;
                result.notes.Add($"Aligned {req.alignChild} to {alignTo.name}: base {basePoint.ToString("F4")}, " +
                                 $"axis {axis.ToString("F3")}, original length {length:F4} m");
            }
            if (req.realign) PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(avatar.scene);
            result.notes.Add(label + " under " + anchor.name + " (one Undo step; not saved)");
        }
        catch { Undo.RevertAllDownToGroup(group); throw; }
        finally { Undo.IncrementCurrentGroup(); RefreshPreview(); }
        result.sceneDirty = avatar.scene.isDirty;
        Inspect(avatar, result);
        return result;
    }

    // Instantiates a prefab at the scene root, outside the avatar so it is never part of its
    // upload, posed relative to a guarded transform in the avatar: offset and euler are in that
    // transform's local space. One Undo step. With realign, moves the existing root object.
    static Result PlacePrefab(GameObject avatar, Request req, Result result)
    {
        RequireDraft(avatar.scene.path);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(req.prefab);
        if (prefab == null) throw new ArgumentException("Prefab not found: " + req.prefab);
        var anchor = Find(avatar.transform, req.anchorPath);
        if (string.IsNullOrEmpty(req.anchorName) || anchor.name != req.anchorName)
            throw new ArgumentException("anchorName must match the inspected anchor transform");
        var name = string.IsNullOrWhiteSpace(req.instanceName) ? prefab.name : req.instanceName;
        var existing = avatar.scene.GetRootGameObjects().Where(g => g.name == name).ToArray();
        if (req.realign ? existing.Length != 1 : existing.Length != 0)
            throw new InvalidOperationException(req.realign ? "realign needs exactly one root object named " + name
                                                            : "The scene already has a root object named " + name);

        string label = "Avatar Agent: " + (string.IsNullOrWhiteSpace(req.label) ? "Place " + name : req.label);
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        try
        {
            GameObject instance;
            if (req.realign)
            {
                instance = existing[0];
                if (PrefabUtility.GetCorrespondingObjectFromSource(instance) != prefab)
                    throw new InvalidOperationException(name + " is not an instance of " + req.prefab);
                Undo.RecordObject(instance.transform, label);
            }
            else
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar.scene);
                Undo.RegisterCreatedObjectUndo(instance, label);
                instance.name = name;
            }
            instance.transform.SetPositionAndRotation(anchor.TransformPoint(req.offset), anchor.rotation * Quaternion.Euler(req.euler));
            instance.transform.localScale = Vector3.one;
            if (req.realign) PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(avatar.scene);
            result.notes.Add($"{label} at the scene root: position {instance.transform.position.ToString("F4")}, " +
                             $"forward {instance.transform.forward.ToString("F3")} (one Undo step; not saved)");
        }
        catch { Undo.RevertAllDownToGroup(group); throw; }
        finally { Undo.IncrementCurrentGroup(); RefreshPreview(); }
        result.sceneDirty = avatar.scene.isDirty;
        return result;
    }
}
