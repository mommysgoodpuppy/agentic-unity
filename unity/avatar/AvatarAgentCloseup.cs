using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static partial class AvatarAgent
{
    // Close-up renders of part of an avatar, from several angles around the avatar's up axis.
    // The camera is framed on the renderers under focusPath (measured before any toggles,
    // so different toggle sets share the same camera). `edits` of kind "active" and `glow`
    // (>= 0 sets _Glow on every renderer) apply only to the preview copy; the scene is untouched.
    // Edits of kind "material" swap a material slot on the preview copy only.
    static Result Closeup(GameObject avatar, Request req, Result result)
    {
        if (string.IsNullOrEmpty(req.previewDirectory) || !Path.IsPathRooted(req.previewDirectory))
            throw new ArgumentException("previewDirectory must be an absolute private path");
        if (req.edits.Any(e => e.kind != "active" && e.kind != "material"))
            throw new ArgumentException("closeup only accepts edits of kind active or material (applied to the preview copy)");
        var label = string.IsNullOrWhiteSpace(req.previewLabel) ? "closeup" : req.previewLabel;
        if (label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Invalid previewLabel");
        var yaws = req.yaws != null && req.yaws.Length > 0 ? req.yaws : new[] { 0f, 45f, 90f, 135f, 180f, 270f };
        Directory.CreateDirectory(req.previewDirectory);

        var utility = new PreviewRenderUtility();
        try
        {
            var instance = UnityEngine.Object.Instantiate(avatar);
            instance.transform.position = Vector3.zero; instance.transform.rotation = Quaternion.identity;
            utility.AddSingleGO(instance);
            var focus = Find(instance.transform, req.focusPath);
            var focusRenderers = focus.GetComponentsInChildren<Renderer>().Where(x => x.enabled && x.gameObject.activeInHierarchy).ToArray();
            if (focusRenderers.Length == 0) throw new InvalidOperationException("No visible renderers under focusPath");
            // Renderer bounds may be padded for physics/culling or cover an entire FBX.
            // Frame the actual posed geometry for a useful inspection close-up.
            var focusPoints = focusRenderers.SelectMany(renderer => renderer is SkinnedMeshRenderer skin
                ? AvatarMeshGeometry.WorldVertices(skin)
                : new[] { renderer.bounds.min, renderer.bounds.max }).ToArray();
            if(req.useFocusRegion)
            {
                var region=new Bounds((req.focusRegionMin+req.focusRegionMax)*.5f,req.focusRegionMax-req.focusRegionMin);
                focusPoints=focusPoints.Where(p=>region.Contains(instance.transform.InverseTransformPoint(p))).ToArray();
                if(focusPoints.Length==0)throw new ArgumentException("No geometry in requested focus region");
            }
            var bounds = new Bounds(focusPoints[0], Vector3.zero);
            foreach (var point in focusPoints) bounds.Encapsulate(point);

            foreach (var edit in req.edits)
            {
                var t = Find(instance.transform, edit.path);
                if (string.IsNullOrEmpty(edit.expectedName) || t.name != edit.expectedName)
                    throw new ArgumentException("Each toggle requires a matching expectedName");
                if (edit.kind == "active") { t.gameObject.SetActive(edit.active); continue; }
                var renderer = t.GetComponent<Renderer>();
                var material = AssetDatabase.LoadAssetAtPath<Material>(edit.material);
                if (renderer == null || material == null || edit.slot < 0 || edit.slot >= renderer.sharedMaterials.Length)
                    throw new ArgumentException("Invalid preview material swap");
                var materials = renderer.sharedMaterials;
                materials[edit.slot] = material;
                renderer.sharedMaterials = materials;
            }
            if (req.glow >= 0)
            {
                var block = new MaterialPropertyBlock();
                block.SetFloat("_Glow", req.glow);
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) renderer.SetPropertyBlock(block);
            }

            float radius = Mathf.Max(bounds.extents.magnitude, 0.02f) * Mathf.Max(req.zoomOut, 1f);
            var camera = utility.camera;
            camera.fieldOfView = 30;
            camera.nearClipPlane = 0.005f; camera.farClipPlane = 20;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1);
            utility.lights[0].intensity = 1.1f;
            utility.lights[0].transform.rotation = Quaternion.Euler(35, -30, 0);
            utility.lights[1].intensity = 0.7f;
            utility.ambientColor = new Color(0.45f, 0.45f, 0.45f);
            foreach (var yaw in yaws)
            {
                string path = Path.Combine(req.previewDirectory, $"{label}-{yaw:000}.png");
                if (File.Exists(path)) throw new IOException("Preview exists: " + path);
                // Yaw 0 looks at the avatar's face; 90 from its left side. Negative pitch looks up from below.
                var direction = Quaternion.Euler(req.pitch, yaw, 0) * Vector3.forward;
                camera.transform.position = bounds.center + direction * radius * 4;
                camera.transform.LookAt(bounds.center);
                utility.BeginStaticPreview(new Rect(0, 0, 768, 768));
                utility.Render(true);
                var texture = utility.EndStaticPreview();
                File.WriteAllBytes(path, texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
                result.notes.Add(path);
            }
        }
        finally { utility.Cleanup(); }
        result.notes.Add("Authoring close-ups only; build-time MA/VRCFury processing is not applied.");
        return result;
    }
}
