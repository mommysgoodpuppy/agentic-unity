// Build a reduced base mesh in Unity from tools/blender/loop_reduce.py output, copying every attribute
// from the original imported mesh, and swap it onto a renderer in a generated scene.
//
//   Unity -batchmode -projectPath <proj> -executeMethod AvatarAgentBaseMesh.Build \
//     -sourceModel "Assets/MyAvatar/Model.fbx" -sourceMesh Body \
//     -reduction /abs/Body.unity.json -output Assets/VRUnityGenerated/BaseMeshes/MyAvatar/Body-reduced.asset -logFile <log>
//
//   ... -executeMethod AvatarAgentBaseMesh.Swap -scene Assets/VRUnityGenerated/....unity \
//     -renderer Body -mesh Assets/VRUnityGenerated/BaseMeshes/MyAvatar/Body-reduced.asset
//
// Build: Blender's vertices are matched to Unity's (split) vertices by position after finding the axis/scale
// mapping between the two frames; each triangle corner picks the split vertex with its UV. Positions,
// normals, tangents, all UV channels, colours, bone weights, bind poses and every blend shape frame (with
// normal/tangent deltas) are copied from the original; position deltas re-fitted in Blender replace the
// originals. Submeshes follow the original triangles' submeshes. The source model is never modified.
// Swap: only meshes under Assets/VRUnityGenerated/ onto renderers in scenes under Assets/VRUnityGenerated/;
// the replacement must have the same bind poses (so the renderer's bones stay valid). Saves the scene.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class AvatarAgentBaseMesh
{
    [Serializable] class ShapeFit { public string name; public int[] ids; public float[] deltas; }
    [Serializable] class Reduction
    {
        public string source; public int[] kept; public float[] keptBasis; public int[] triangles; public float[] cornerUV;
        public ShapeFit[] shapes; public int trianglesBefore, trianglesAfter;
    }

    const string Generated = "Assets/VRUnityGenerated/";
    static void Log(string s) => Debug.Log("[AvatarAgentBaseMesh] " + s);

    static string Arg(string name)
    {
        var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    static void Run(Action body)
    {
        int code = 1;
        try { body(); code = 0; Log("DONE"); }
        catch (Exception e) { Debug.LogException(e); Log("FAILED: " + e.Message); }
        EditorApplication.Exit(code);
    }

    public static void Build() => Run(() =>
    {
        string model = Arg("-sourceModel"), meshName = Arg("-sourceMesh"), json = Arg("-reduction"), output = Arg("-output");
        if (model == null || meshName == null || json == null || output == null) throw new ArgumentException("pass -sourceModel -sourceMesh -reduction -output");
        if (!output.StartsWith(Generated) || !output.EndsWith(".asset")) throw new ArgumentException("output must be a .asset under " + Generated);
        var src = AssetDatabase.LoadAllAssetsAtPath(model).OfType<Mesh>().SingleOrDefault(m => m.name == meshName)
                  ?? throw new ArgumentException($"No mesh '{meshName}' in {model}");
        var red = JsonUtility.FromJson<Reduction>(File.ReadAllText(json));
        var mesh = BuildMesh(src, red);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        if (AssetDatabase.LoadAssetAtPath<Mesh>(output) != null) throw new InvalidOperationException(output + " exists; choose a new name");
        AssetDatabase.CreateAsset(mesh, output);
        AssetDatabase.SaveAssets();
        Log($"wrote {output}: {Tris(src)} -> {Tris(mesh)} triangles, {src.vertexCount} -> {mesh.vertexCount} vertices, {mesh.blendShapeCount} blend shapes, {mesh.subMeshCount} submeshes");
    });

    static int Tris(Mesh m) => Enumerable.Range(0, m.subMeshCount).Sum(i => (int)m.GetIndexCount(i) / 3);

    // Candidate frame mappings Blender local -> Unity mesh: signed axis permutations, uniform scale from extents.
    static Func<Vector3, Vector3> FitFrame(Vector3[] blender, Vector3[] unity, Vector2[][] expectedUV, List<Vector2> sourceUV,
        out Matrix4x4 linear, out float err)
    {
        var grid = new Dictionary<Vector3Int, List<int>>();
        var ub = new Bounds(unity[0], Vector3.zero); foreach (var p in unity) ub.Encapsulate(p);
        float cell = ub.size.magnitude / 400f;
        for (int i = 0; i < unity.Length; i++)
        {
            var k = Vector3Int.FloorToInt(unity[i] / cell);
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(i);
        }
        float Nearest(Vector3 p, Vector2[] want = null)
        {
            var c = Vector3Int.FloorToInt(p / cell); float best = float.MaxValue;
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                if (grid.TryGetValue(c + new Vector3Int(x, y, z), out var l)) foreach (var i in l)
                {
                    float score = (unity[i] - p).magnitude;
                    if (want != null && want.Length > 0 && sourceUV.Count > 0)
                        score += want.Min(uv => (sourceUV[i] - uv).magnitude) * ub.size.magnitude;
                    best = Mathf.Min(best, score);
                }
            return best;
        }
        var bb = new Bounds(blender[0], Vector3.zero); foreach (var p in blender) bb.Encapsulate(p);
        var perms = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
        var sampleIds = Enumerable.Range(0, Math.Min(blender.Length, 400)).Select(i => i * blender.Length / Math.Min(blender.Length, 400)).ToArray();
        float bestErr = float.MaxValue; Matrix4x4 best = Matrix4x4.identity;
        foreach (var perm in perms)
            for (int signs = 0; signs < 8; signs++)
            {
                var m = Matrix4x4.zero;
                for (int r = 0; r < 3; r++) m[r, perm[r]] = (signs >> r & 1) == 1 ? -1 : 1;
                m[3, 3] = 1;
                var e = m.MultiplyVector(bb.size); e = new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z));
                float s = ub.size.magnitude / Mathf.Max(e.magnitude, 1e-9f);
                var sm = Matrix4x4.Scale(Vector3.one * s) * m;
                var t = ub.center - sm.MultiplyPoint3x4(bb.center);
                // Positions alone cannot distinguish mirror frames on a symmetric body or hair.
                // Anchor the frame with the source UVs before choosing a handedness.
                float total = sampleIds.Sum(i => Nearest(sm.MultiplyPoint3x4(blender[i]) + t, expectedUV[i]));
                if (total < bestErr) { bestErr = total; best = Matrix4x4.Translate(t) * sm; }
            }
        // Refine scale and translation by least squares on nearest-vertex pairs (bounding boxes of the kept subset
        // differ slightly from the full mesh's).
        int NearestIndex(Vector3 p)
        {
            var c = Vector3Int.FloorToInt(p / cell); float bd = float.MaxValue; int bi = -1;
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                if (grid.TryGetValue(c + new Vector3Int(x, y, z), out var l)) foreach (var i in l) { float d = (unity[i] - p).sqrMagnitude; if (d < bd) { bd = d; bi = i; } }
            return bi;
        }
        var rot = best; rot.SetColumn(3, new Vector4(0, 0, 0, 1));
        float scale = rot.GetColumn(0).magnitude + rot.GetColumn(1).magnitude + rot.GetColumn(2).magnitude; scale /= 3;
        var R = Matrix4x4.Scale(Vector3.one / scale) * rot;
        for (int iter = 0; iter < 6; iter++)
        {
            var pairs = blender.Select(p => (rp: R.MultiplyVector(p), q: NearestIndex(best.MultiplyPoint3x4(p)))).Where(x => x.q >= 0).ToArray();
            var pm = pairs.Aggregate(Vector3.zero, (acc, x) => acc + x.rp) / pairs.Length;
            var qm = pairs.Aggregate(Vector3.zero, (acc, x) => acc + unity[x.q]) / pairs.Length;
            double num = 0, den = 0;
            foreach (var (rp, q) in pairs) { var a = rp - pm; num += Vector3.Dot(a, unity[q] - qm); den += a.sqrMagnitude; }
            scale = (float)(num / Math.Max(den, 1e-30));
            best = Matrix4x4.Translate(qm - scale * pm) * Matrix4x4.Scale(Vector3.one * scale) * R;
        }
        bestErr = sampleIds.Sum(i => Nearest(best.MultiplyPoint3x4(blender[i])));
        err = bestErr / sampleIds.Length / Mathf.Max(ub.size.magnitude, 1e-9f);
        linear = best;
        var final = best;
        return p => final.MultiplyPoint3x4(p);
    }

    static Mesh BuildMesh(Mesh src, Reduction red)
    {
        var uPos = src.vertices; var uUv = new List<Vector2>(); src.GetUVs(0, uUv);
        var bPos = new Vector3[red.kept.Length];
        for (int k = 0; k < red.kept.Length; k++) bPos[k] = new Vector3(red.keptBasis[3 * k], red.keptBasis[3 * k + 1], red.keptBasis[3 * k + 2]);
        var byOriginal = new Dictionary<int, HashSet<Vector2>>();
        for (int i = 0; i < red.triangles.Length; i++)
        {
            int id = red.triangles[i];
            if (!byOriginal.TryGetValue(id, out var values)) byOriginal[id] = values = new HashSet<Vector2>();
            values.Add(new Vector2(red.cornerUV[2 * i], red.cornerUV[2 * i + 1]));
        }
        var expected = red.kept.Select(id => byOriginal.TryGetValue(id, out var values) ? values.ToArray() : new Vector2[0]).ToArray();
        var map = FitFrame(bPos, uPos, expected, uUv, out var frame, out var frameErr);
        Log($"frame fit: mean nearest distance {frameErr:E2} of mesh size");
        if (frameErr > 1e-4f) throw new InvalidOperationException("Blender and Unity vertices do not line up; wrong source mesh?");

        // Unity split vertices per Blender vertex (by position).
        float size = src.bounds.size.magnitude, eps = size * 1e-5f;
        var byPos = new Dictionary<Vector3Int, List<int>>();
        Vector3Int Key(Vector3 p) => Vector3Int.RoundToInt(p / (eps * 4));
        for (int i = 0; i < uPos.Length; i++) { var k = Key(uPos[i]); if (!byPos.TryGetValue(k, out var l)) byPos[k] = l = new List<int>(); l.Add(i); }
        var splits = new Dictionary<int, List<int>>();
        for (int k = 0; k < red.kept.Length; k++)
        {
            var p = map(bPos[k]); var c = Key(p); var found = new List<int>();
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                if (byPos.TryGetValue(c + new Vector3Int(x, y, z), out var l)) found.AddRange(l.Where(i => (uPos[i] - p).magnitude <= eps * 2));
            if (found.Count == 0) throw new InvalidOperationException($"Blender vertex {red.kept[k]} has no Unity vertex");
            splits[red.kept[k]] = found.Distinct().ToList();
        }

        // Original submesh of each Unity triangle, keyed by its sorted vertex triple's Blender-independent position set.
        var subOf = new Dictionary<int, int>();  // unity vertex -> submesh (first seen)
        for (int s = 0; s < src.subMeshCount; s++) foreach (var i in src.GetTriangles(s)) if (!subOf.ContainsKey(i)) subOf[i] = s;

        var used = new Dictionary<int, int>(); var newTris = Enumerable.Range(0, src.subMeshCount).Select(_ => new List<int>()).ToArray();
        var order = new List<int>();
        float maxUvError = 0;
        for (int t = 0; t < red.triangles.Length / 3; t++)
        {
            var corner = new int[3];
            for (int c = 0; c < 3; c++)
            {
                int bi = red.triangles[3 * t + c]; var uv = new Vector2(red.cornerUV[6 * t + 2 * c], red.cornerUV[6 * t + 2 * c + 1]);
                corner[c] = splits[bi].OrderBy(i => (uUv.Count > 0 ? (uUv[i] - uv).sqrMagnitude : 0)).First();
                if (uUv.Count > 0) maxUvError = Mathf.Max(maxUvError, (uUv[corner[c]] - uv).magnitude);
            }
            var sub = corner.Select(i => subOf.TryGetValue(i, out var s) ? s : 0).GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
            foreach (var i in corner)
            {
                if (!used.TryGetValue(i, out var n)) { n = order.Count; used[i] = n; order.Add(i); }
                newTris[sub].Add(n);
            }
        }
        Log($"maximum source-corner UV mismatch {maxUvError:E2}");
        // Allow half-float import quantization (about half a texel at 2048), but never atlas-scale mismatches.
        if (maxUvError > 0.00025f) throw new InvalidOperationException("Corner UVs do not match the source; possible ambiguous frame or split-vertex mapping");
        // Blender's triangle winding vs Unity's: flip if the face normals mostly disagree with the vertex normals.
        var nrm = src.normals;
        int agree = 0;
        for (int s = 0; s < newTris.Length; s++)
            for (int t = 0; t + 2 < newTris[s].Count && t < 3000; t += 3)
            {
                Vector3 a = uPos[order[newTris[s][t]]], b = uPos[order[newTris[s][t + 1]]], c = uPos[order[newTris[s][t + 2]]];
                agree += Vector3.Dot(Vector3.Cross(b - a, c - a), nrm[order[newTris[s][t]]]) > 0 ? 1 : -1;  // Unity's face normal is Cross(b-a, c-a)
            }
        if (agree < 0) foreach (var l in newTris) for (int t = 0; t + 2 < l.Count; t += 3) (l[t + 1], l[t + 2]) = (l[t + 2], l[t + 1]);

        T[] Pick<T>(T[] a) => a == null || a.Length == 0 ? a : order.Select(i => a[i]).ToArray();
        var mesh = new Mesh { name = src.name + " (reduced)", indexFormat = order.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.vertices = Pick(src.vertices); mesh.normals = Pick(src.normals); mesh.tangents = Pick(src.tangents); mesh.colors32 = Pick(src.colors32);
        for (int ch = 0; ch < 8; ch++)
        {
            var uvs = new List<Vector4>(); src.GetUVs(ch, uvs);
            if (uvs.Count > 0) mesh.SetUVs(ch, order.Select(i => uvs[i]).ToList());
        }
        var perVertex = src.GetBonesPerVertex(); var weights = src.GetAllBoneWeights();
        if (perVertex.Length > 0)
        {
            var start = new int[perVertex.Length]; for (int i = 1; i < start.Length; i++) start[i] = start[i - 1] + perVertex[i - 1];
            var nb = new Unity.Collections.NativeArray<byte>(order.Select(i => perVertex[i]).ToArray(), Unity.Collections.Allocator.Temp);
            var nw = new Unity.Collections.NativeArray<BoneWeight1>(order.SelectMany(i => Enumerable.Range(start[i], perVertex[i]).Select(j => weights[j])).ToArray(), Unity.Collections.Allocator.Temp);
            mesh.SetBoneWeights(nb, nw); nb.Dispose(); nw.Dispose();
        }
        mesh.bindposes = src.bindposes;
        mesh.subMeshCount = src.subMeshCount;
        for (int s = 0; s < src.subMeshCount; s++) mesh.SetTriangles(newTris[s], s, false);

        // Blend shapes: original frames; re-fitted position deltas (Blender frame -> Unity) where given.
        var fits = (red.shapes ?? new ShapeFit[0]).ToDictionary(f => f.name);
        var dv = new Vector3[src.vertexCount]; var dn = new Vector3[src.vertexCount]; var dt = new Vector3[src.vertexCount];
        int refit = 0;
        for (int s = 0; s < src.blendShapeCount; s++)
        {
            string name = src.GetBlendShapeName(s);
            int frames = src.GetBlendShapeFrameCount(s);
            for (int f = 0; f < frames; f++)
            {
                src.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                var v = Pick(dv);
                if (fits.TryGetValue(name, out var fit) && f == frames - 1)
                {
                    for (int j = 0; j < fit.ids.Length; j++)
                    {
                        var d = frame.MultiplyVector(new Vector3(fit.deltas[3 * j], fit.deltas[3 * j + 1], fit.deltas[3 * j + 2]));
                        if (!splits.TryGetValue(fit.ids[j], out var us)) continue;
                        foreach (var u in us) if (used.TryGetValue(u, out var n)) { v[n] = d; refit++; }
                    }
                }
                mesh.AddBlendShapeFrame(name, src.GetBlendShapeFrameWeight(s, f), v, Pick(dn), Pick(dt));
            }
        }
        mesh.RecalculateBounds();
        Log($"{used.Count} vertices used; {refit} blend-shape vertex deltas from the re-fit; winding {(agree < 0 ? "flipped" : "kept")}");
        return mesh;
    }

    public static void Swap() => Run(() =>
    {
        string scenePath = Arg("-scene"), rendererPath = Arg("-renderer"), meshPath = Arg("-mesh");
        if (scenePath == null || rendererPath == null || meshPath == null) throw new ArgumentException("pass -scene -renderer -mesh");
        if (!scenePath.StartsWith(Generated) || !meshPath.StartsWith(Generated)) throw new ArgumentException("scene and mesh must be under " + Generated);
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) ?? throw new ArgumentException("No mesh at " + meshPath);
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        var avatars = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true)).ToArray();
        if (avatars.Length != 1) throw new InvalidOperationException($"{scenePath} has {avatars.Length} avatars");
        var t = avatars[0].transform.Find(rendererPath) ?? throw new ArgumentException("No " + rendererPath);
        var smr = t.GetComponent<SkinnedMeshRenderer>();
        if (smr == null)
        {
            var filter = t.GetComponent<MeshFilter>() ?? throw new ArgumentException(rendererPath + " has no mesh renderer/filter");
            if (mesh.bindposes.Length > 0 || mesh.blendShapeCount > 0 || filter.sharedMesh.subMeshCount != mesh.subMeshCount)
                throw new InvalidOperationException("Static replacement must have matching slots and no rig/shapes");
            filter.sharedMesh = mesh; EditorUtility.SetDirty(filter); EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Save failed");
            Log("Static mesh swapped: " + rendererPath); return;
        }
        var old = smr.sharedMesh;
        if (old.bindposes.Length != mesh.bindposes.Length || old.bindposes.Zip(mesh.bindposes, (a, b) => a == b).Any(x => !x))
            throw new InvalidOperationException("bind poses differ; the renderer's bones would not match");
        if (old.subMeshCount != mesh.subMeshCount) throw new InvalidOperationException("submesh count differs");
        var weights = Enumerable.Range(0, old.blendShapeCount).ToDictionary(i => old.GetBlendShapeName(i), i => smr.GetBlendShapeWeight(i));
        Undo.RecordObject(smr, "Swap reduced mesh");
        smr.sharedMesh = mesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
            if (weights.TryGetValue(mesh.GetBlendShapeName(i), out var w)) smr.SetBlendShapeWeight(i, w);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Log($"{rendererPath}: {old.name} ({Tris(old)} tris) -> {mesh.name} ({Tris(mesh)} tris); blend shape weights carried over by name");
    });
}
