// Temporary Unity editor diagnostic. Copy into an Editor folder in the working
// project, execute Run with -migrationReport PATH and -migrationScene Assets/....unity, then
// remove the helper.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class VRUnityMigrationCheck
{
    [Serializable]
    private class AvatarReport
    {
        public string name;
        public bool active;
        public int missingScripts;
        public int skinnedRenderersWithoutMesh;
        public int nullMaterialSlots;
        public int errorShaderMaterials;
    }

    [Serializable]
    private class Report
    {
        public string unityVersion;
        public string scene;
        public int gameObjects;
        public int avatarDescriptors;
        public int renderers;
        public int missingScripts;
        public int nullMaterialSlots;
        public int errorShaderMaterials;
        public int skinnedRenderersWithoutMesh;
        public int missingPrefabObjects;
        public List<string> blenderDependencies = new List<string>();
        public List<AvatarReport> avatars = new List<AvatarReport>();
        public List<string> details = new List<string>();
    }

    public static void Run()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-migrationReport");
            if (index < 0 || index + 1 >= args.Length)
                throw new ArgumentException("Missing -migrationReport PATH");
            int sceneIndex = Array.IndexOf(args, "-migrationScene");
            if (sceneIndex < 0 || sceneIndex + 1 >= args.Length)
                throw new ArgumentException("Missing -migrationScene Assets/....unity");
            var scene = EditorSceneManager.OpenScene(args[sceneIndex + 1], OpenSceneMode.Single);
            var report = new Report { unityVersion = Application.unityVersion, scene = scene.path };
            foreach (var dependency in AssetDatabase.GetDependencies(scene.path, true))
                if (dependency.EndsWith(".blend", StringComparison.OrdinalIgnoreCase))
                    report.blenderDependencies.Add(dependency);
            var errorMaterials = new HashSet<Material>();
            foreach (var root in scene.GetRootGameObjects())
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var obj = transform.gameObject;
                report.gameObjects++;
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(obj);
                report.missingScripts += missing;
                if (missing > 0) report.details.Add("Missing script: " + Hierarchy(transform));
                if (PrefabUtility.GetPrefabInstanceStatus(obj) == PrefabInstanceStatus.MissingAsset)
                {
                    report.missingPrefabObjects++;
                    report.details.Add("Missing prefab: " + Hierarchy(transform));
                }
                foreach (var component in obj.GetComponents<Component>())
                    if (component != null && component.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor")
                    {
                        report.avatarDescriptors++;
                        var avatar = new AvatarReport { name = obj.name, active = obj.activeInHierarchy };
                        foreach (var child in obj.GetComponentsInChildren<Transform>(true))
                            avatar.missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject);
                        var badMaterials = new HashSet<Material>();
                        foreach (var avatarRenderer in obj.GetComponentsInChildren<Renderer>(true))
                        {
                            var skin = avatarRenderer as SkinnedMeshRenderer;
                            if (skin != null && skin.sharedMesh == null) avatar.skinnedRenderersWithoutMesh++;
                            foreach (var material in avatarRenderer.sharedMaterials)
                                if (material == null) avatar.nullMaterialSlots++;
                                else if (material.shader == null || material.shader.name == "Hidden/InternalErrorShader") badMaterials.Add(material);
                        }
                        avatar.errorShaderMaterials = badMaterials.Count;
                        report.avatars.Add(avatar);
                    }
                foreach (var renderer in obj.GetComponents<Renderer>())
                {
                    report.renderers++;
                    var skinned = renderer as SkinnedMeshRenderer;
                    if (skinned != null && skinned.sharedMesh == null)
                    {
                        report.skinnedRenderersWithoutMesh++;
                        report.details.Add("No skinned mesh: " + Hierarchy(transform));
                    }
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null) report.nullMaterialSlots++;
                        else if (material.shader == null || material.shader.name == "Hidden/InternalErrorShader")
                            errorMaterials.Add(material);
                    }
                }
            }
            report.errorShaderMaterials = errorMaterials.Count;
            foreach (var material in errorMaterials)
                report.details.Add("Missing/error shader: " + AssetDatabase.GetAssetPath(material));
            File.WriteAllText(Path.GetFullPath(args[index + 1]), JsonUtility.ToJson(report, true));
            Debug.Log("VRUnityMigrationCheck: scene inspection complete; report saved.");
            // This diagnostic loads the scene but never saves it or builds/uploads avatars.
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static string Hierarchy(Transform transform)
    {
        var names = new List<string>();
        for (var current = transform; current != null; current = current.parent)
            names.Insert(0, current.name);
        return string.Join("/", names);
    }
}
