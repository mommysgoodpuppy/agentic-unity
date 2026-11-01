// Read-only geometry export for fitting optimization cutters. Never saves a scene.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

public static class AvatarAgentOptimizeAudit
{
    [Serializable] public class Geometry
    {
        public string path, mesh; public Vector3[] vertices, normals; public Vector2[] uv;
        public int[] triangles; public string[] materials; public Vector3 scale;
    }
    [Serializable] public class Bone { public string name; public Vector3 position; }
    [Serializable] public class Report { public Geometry[] meshes; public Bone[] bones; }
    static string Arg(string key) { var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, key); return i < 0 ? null : a[i + 1]; }
    public static void Run()
    {
        try
        {
            var scene = EditorSceneManager.OpenScene(Arg("-scene"), OpenSceneMode.Single);
            var avatar = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;
            var root = avatar.transform;
            string PathOf(Transform t) => AnimationUtility.CalculateTransformPath(t, root);
            var meshes = avatar.GetComponentsInChildren<Renderer>(true).Where(r => r is SkinnedMeshRenderer || r is MeshRenderer)
                .Select(r => {
                    Mesh m = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    if (m == null) return null;
                    var p = r is SkinnedMeshRenderer smr ? AvatarMeshGeometry.WorldVertices(smr) : m.vertices.Select(v => r.transform.TransformPoint(v)).ToArray();
                    return new Geometry { path = PathOf(r.transform), mesh = AssetDatabase.GetAssetPath(m), vertices = p.Select(v => root.InverseTransformPoint(v)).ToArray(),
                        triangles = m.triangles, uv = m.uv, normals = m.normals, scale = r.transform.lossyScale,
                        materials = r.sharedMaterials.Select(x => AssetDatabase.GetAssetPath(x)).ToArray() };
                }).Where(m => m != null).ToArray();
            var animator = avatar.GetComponent<Animator>();
            var bones = Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>().Where(b => b != HumanBodyBones.LastBone)
                .Select(b => (b, t: animator.GetBoneTransform(b))).Where(x => x.t != null)
                .Select(x => new Bone { name = x.b.ToString(), position = root.InverseTransformPoint(x.t.position) }).ToArray();
            File.WriteAllText(Arg("-output"), JsonUtility.ToJson(new Report { meshes = meshes, bones = bones }));
            Debug.Log("[AvatarAgentOptimizeAudit] DONE"); EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }
}
