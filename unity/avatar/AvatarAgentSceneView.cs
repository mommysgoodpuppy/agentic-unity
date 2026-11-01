// See what the user is looking at. Live only: the Scene view doesn't exist in batch mode.
//   {"command":"scene-view","previewDirectory":"/absolute/private/dir"}
// Reports the Scene view camera's pose, the pivot it orbits and the selected objects, and renders that
// view to a new PNG in previewDirectory. The view isn't moved and nothing in the scene changes, so "that
// bit there looks wrong" can be answered without a screenshot of the user's desktop.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static partial class AvatarAgent
{
    static Result SceneViewShot(Request req, Result result)
    {
        if (Application.isBatchMode) throw new InvalidOperationException("scene-view needs an open editor: run it without --batch");
        var view = SceneView.lastActiveSceneView;
        if (view == null || view.camera == null) throw new InvalidOperationException("No Scene view is open");
        if (string.IsNullOrEmpty(req.previewDirectory) || !Path.IsPathRooted(req.previewDirectory))
            throw new ArgumentException("previewDirectory must be an absolute private path");

        var pose = view.camera.transform;
        result.notes.Add($"Camera position {pose.position:F4}, euler {pose.eulerAngles:F2}, forward {pose.forward:F3}");
        result.notes.Add($"Pivot {view.pivot:F4}, distance {view.cameraDistance:F3}, field of view {view.camera.fieldOfView:F1}, orthographic {view.orthographic}");
        result.notes.Add("Selected: " + (Selection.gameObjects.Length == 0 ? "nothing"
            : string.Join(", ", Selection.gameObjects.Select(g => g.scene.IsValid() ? ScenePath(g.transform) : g.name))));

        int width = Mathf.Max(64, view.camera.pixelWidth), height = Mathf.Max(64, view.camera.pixelHeight);
        Directory.CreateDirectory(req.previewDirectory);
        string path = Path.Combine(req.previewDirectory, $"scene-view-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var holder = new GameObject("Scene view copy") { hideFlags = HideFlags.HideAndDontSave };
        var texture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try
        {
            var camera = holder.AddComponent<Camera>();
            camera.enabled = false;
            camera.CopyFrom(view.camera);
            camera.targetTexture = texture;
            camera.Render();
            RenderTexture.active = texture;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(texture);
            UnityEngine.Object.DestroyImmediate(holder);
        }
        result.notes.Add("Rendered " + path);
        return result;
    }

    static string ScenePath(Transform t) => t.parent == null ? t.name : ScenePath(t.parent) + "/" + t.name;
}
