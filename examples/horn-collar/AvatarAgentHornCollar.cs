// EXAMPLE: not installed by install_helpers. Set Scene below, and rename "Hat" and "Horn" to suit.
// Torn-fabric collar where a HornGlow horn comes out through a hat,
// so the horn looks punched through from inside. Generated from the live scene: the hat's
// actual skinned surface around the horn is sampled, and the collar is built in the horn's
// local space, like CuteHorn (HornGlowBuilder in the HornGlow prototype). Change the code
// or the request's parameters, not the generated mesh.
//
// Live only. Write Library/VRUnityAgent/HornCollar/request.json:
//   {"command":"build"}            build or rebuild the mesh and the "Hat Collar" child, one Undo step
//   {"command":"report"}           sample and report only; no asset or scene change
//   {"command":"view"}             render the Scene view's camera as it is (not moved) to view.png
// Optional overrides: petals, seed, edgeScale, spikeMin, spikeMax, spikeWidth, sweep, flare,
// curl, thickness, uvScale (see Request).
// The result goes to report.json (or error.txt) in the same folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

[InitializeOnLoad]
public static class AvatarAgentHornCollar
{
    [Serializable] public class Request
    {
        public string command;
        // Torn fabric pushed out by the horn: a low ragged edge all round the hole (edgeScale x
        // the horn's radius where it leaves the hat) and `petals` pointed flaps, each
        // spikeMin..spikeMax x that radius long (random per petal), covering spikeWidth of
        // their share of the circle, swept round by `sweep` radians per horn radius of length.
        public int petals = 11, seed = 5;
        public float edgeScale = 0.25f, spikeMin = 0.3f, spikeMax = 0.75f, spikeWidth = 0.85f, sweep = 0.12f;
        // The fabric leaves the hat `flare` degrees above its surface and bends `curl` degrees
        // further outward by the tips. `thickness` is in metres, horn space.
        public float flare = 50, curl = 30, thickness = 0.001f;
        // The fabric's root sits on whichever is higher, the hat or anything lying on it near
        // the horn (the ribbon), plus `raise` metres, smoothed around the hole.
        public float raise = 0.0005f;
        // Before flaring, the torn edge rises `sleeve` metres straight up against the horn, so
        // nothing under it shows between the horn and the fabric.
        public float sleeve = 0.0025f;
        // UV0 continues the hat's own mapping at the exit point linearly (scaled by uvScale),
        // so the collar shows the same fabric without crossing the hat's UV seams.
        public float uvScale = 1;
    }
    [Serializable] public class Report
    {
        public string command, scene, hornPath, hatPath, material, meshAsset;
        public float exitHeight, maxRootLift, exitRadius, innerRadius, tipLength, edgeLength, centerHatThickness;
        public int vertices, triangles, missedRays, submesh;
        public Vector2 uvMin, uvMax;
        public string[] hatBonesAtExit;
        public string[] notes;
    }

    const string Scene = "Assets/VRUnityGenerated/MyAvatar-Hat.unity";
    const string Folder = "Assets/VRUnityGenerated/HornCollar";
    const string MeshPath = Folder + "/HornCollar.asset";
    const string ChildName = "Hat Collar";
    static string Work => Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/VRUnityAgent/HornCollar"));

    // CuteHorn's shape (HornGlowBuilder): along +Y from its base, sunk 0.15 radius below the origin.
    const float HornLength = 0.089f, HornBaseRadius = 0.0168f;
    static float HornRadius(float t) => HornBaseRadius * (1 - 0.5f * t) * Mathf.Sqrt(Mathf.Max(0, 1 - t * t * t));
    static float HornRadiusAt(float y) => HornRadius(Mathf.Clamp01((y + 0.15f * HornBaseRadius) / HornLength));

    // Fabric grid: rings from the hole's edge at the horn (0) to the torn outline (1), denser
    // toward the tips, and columns around the horn, several per petal so the points stay sharp.
    static readonly float[] Rings = { 0, 0.12f, 0.24f, 0.36f, 0.47f, 0.57f, 0.66f, 0.74f, 0.81f, 0.87f, 0.92f, 0.96f, 1 };
    const int ColumnsPerPetal = 20;

    static AvatarAgentHornCollar() { EditorApplication.update += Poll; }

    static void Poll()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying) return;
        string request = Path.Combine(Work, "request.json");
        if (!File.Exists(request)) return;
        var input = JsonUtility.FromJson<Request>(File.ReadAllText(request));
        File.Delete(request);
        try
        {
            if (input.command == "view") { RenderSceneView(Path.Combine(Work, "view.png")); return; }
            if (input.command != "build" && input.command != "report") throw new Exception("Unknown command " + input.command);
            var report = Run(input);
            File.WriteAllText(Path.Combine(Work, "report.json"), JsonUtility.ToJson(report, true));
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(Work, "error.txt"), e.ToString()); }
    }

    static Report Run(Request req)
    {
        if (req.petals < 3 || req.edgeScale <= 0 || req.spikeMin < 0 || req.spikeMax < req.spikeMin || req.spikeWidth <= 0 || req.spikeWidth > 1
            || req.thickness <= 0 || req.flare <= 0 || req.flare - req.curl < -30 || req.flare > 85)
            throw new Exception("Invalid collar parameters");
        var avatar = UnityEngine.Object.FindObjectsOfType<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>().Single().gameObject;
        if (avatar.scene.path != Scene) throw new Exception("Horn collar: open the draft named by Scene");
        var hat = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "Hat" && r.enabled && r.gameObject.activeInHierarchy);
        var horn = avatar.GetComponentsInChildren<MeshRenderer>(true).Single(r => r.name == "Horn" && r.transform.parent && r.transform.parent.name == "HornGlow").transform;
        var notes = new List<string>();

        // Hat triangles in horn space.
        var toHorn = horn.worldToLocalMatrix;
        var hatVerts = AvatarMeshGeometry.WorldVertices(hat).Select(v => toHorn.MultiplyPoint3x4(v)).ToArray();
        var hatMesh = hat.sharedMesh;
        var hatUv = hatMesh.uv;
        var hatTris = hatMesh.triangles;
        var triSubmesh = new int[hatTris.Length / 3];
        for (int s = 0; s < hatMesh.subMeshCount; s++)
        {
            int count = (int)hatMesh.GetIndexCount(s) / 3;
            int first = (int)hatMesh.GetIndexStart(s) / 3;
            for (int i = 0; i < count; i++) triSubmesh[first + i] = s;
        }

        // The outer surface faces up the horn (+Y); the lining faces back toward the head.
        // Rays run along +Y; take the outward-facing hit nearest the expected height.
        (bool ok, float y, Vector2 uv, int tri) Surface(float x, float z, float near)
        {
            bool found = false; float bestY = 0, bestD = float.MaxValue; Vector2 bestUv = default; int bestTri = -1;
            for (int i = 0; i < hatTris.Length; i += 3)
            {
                Vector3 a = hatVerts[hatTris[i]], b = hatVerts[hatTris[i + 1]], c = hatVerts[hatTris[i + 2]];
                // 2D barycentric test in the XZ plane, then the height on the triangle.
                float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(d) < 1e-14f) continue;
                float u = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
                float v = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
                float w = 1 - u - v;
                if (u < 0 || v < 0 || w < 0) continue;
                if (Vector3.Cross(b - a, c - a).y <= 0) continue; // lining or side walls
                float y = u * a.y + v * b.y + w * c.y;
                if (Mathf.Abs(y - near) >= bestD) continue;
                bestD = Mathf.Abs(y - near); bestY = y; found = true; bestTri = i / 3;
                bestUv = u * hatUv[hatTris[i]] + v * hatUv[hatTris[i + 1]] + w * hatUv[hatTris[i + 2]];
            }
            return (found, bestY, bestUv, bestTri);
        }

        // Where the horn leaves the hat: start from the horn's mid-height and settle on the
        // surface hit at the horn's edge.
        var center = Surface(0, 0, HornLength * 0.25f);
        if (!center.ok) throw new Exception("Horn axis does not cross the hat's outer surface");
        float exitY = center.y;
        float exitR = HornRadiusAt(exitY);
        float innerR = 0.97f * exitR; // the hole's edge, just inside the horn's spiral lands
        int submesh = triSubmesh[center.tri];
        // Linear UV map of the exit triangle, as a function of horn-space x and z.
        Vector2 UvAt(float x, float z)
        {
            int i = center.tri * 3;
            Vector3 a = hatVerts[hatTris[i]], b = hatVerts[hatTris[i + 1]], c = hatVerts[hatTris[i + 2]];
            Vector2 ua = hatUv[hatTris[i]], ub = hatUv[hatTris[i + 1]], uc = hatUv[hatTris[i + 2]];
            float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            float u = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
            float v = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
            return center.uv + req.uvScale * (u * ua + v * ub + (1 - u - v) * uc - center.uv);
        }
        var material = hat.sharedMaterials[Mathf.Min(submesh, hat.sharedMaterials.Length - 1)];
        // Hat thickness along the axis, for the report: the lining hit below the exit.
        float lining = float.NaN;
        for (int i = 0; i < hatTris.Length; i += 3)
        {
            Vector3 a = hatVerts[hatTris[i]], b = hatVerts[hatTris[i + 1]], c = hatVerts[hatTris[i + 2]];
            if (Vector3.Cross(b - a, c - a).y >= 0) continue;
            var hit = RayY(a, b, c);
            if (hit.HasValue && hit.Value < exitY && (float.IsNaN(lining) || hit.Value > lining)) lining = hit.Value;
        }

        // Surfaces lying on the hat around the horn, such as the ribbon (part of the hat mesh,
        // or another renderer): triangles near the hole, in horn space.
        var occluders = new List<Vector3>();
        foreach (var other in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!other.enabled || !other.gameObject.activeInHierarchy || !other.sharedMesh) continue;
            var points = other == hat ? hatVerts : AvatarMeshGeometry.WorldVertices(other).Select(v => toHorn.MultiplyPoint3x4(v)).ToArray();
            var otherTris = other.sharedMesh.triangles;
            for (int t = 0; t < otherTris.Length; t += 3)
            {
                Vector3 a = points[otherTris[t]], b = points[otherTris[t + 1]], c = points[otherTris[t + 2]];
                bool near = new[] { a, b, c }.Any(v => new Vector2(v.x, v.z).magnitude < 2.5f * exitR && v.y > exitY - 0.005f && v.y < exitY + 0.02f);
                if (near) { occluders.Add(a); occluders.Add(b); occluders.Add(c); }
            }
        }
        // Highest occluder surface over (x, z) below `ceiling`, either side, or -infinity.
        float OccluderTop(float x, float z, float ceiling)
        {
            float best = float.NegativeInfinity;
            for (int t = 0; t < occluders.Count; t += 3)
            {
                Vector3 a = occluders[t] - new Vector3(x, 0, z), b = occluders[t + 1] - new Vector3(x, 0, z), c = occluders[t + 2] - new Vector3(x, 0, z);
                var y = RayY(a, b, c);
                if (y.HasValue && y.Value <= ceiling) best = Mathf.Max(best, y.Value);
            }
            return best;
        }

        // Petals. Each gets a deterministic random length, width and angular jitter, so the
        // tear doesn't look machine-cut. Between petals only the low torn edge remains.
        var random = new System.Random(req.seed);
        int petals = req.petals, columns = petals * ColumnsPerPetal;
        var tipAngle = new float[petals];
        var tipLength = new float[petals];
        var halfWidth = new float[petals];
        for (int p = 0; p < petals; p++)
        {
            tipAngle[p] = 2 * Mathf.PI * (p + 0.35f * ((float)random.NextDouble() - 0.5f)) / petals;
            tipLength[p] = exitR * Mathf.Lerp(req.spikeMin, req.spikeMax, (float)random.NextDouble());
            halfWidth[p] = req.spikeWidth * Mathf.PI / petals * Mathf.Lerp(0.75f, 1.25f, (float)random.NextDouble());
        }
        float edgeLength = req.edgeScale * exitR;
        // Length of fabric from the hole's edge at angle th.
        float Length(float th)
        {
            float length = edgeLength;
            for (int q = 0; q < petals; q++)
            {
                float d = Mathf.Abs(Mathf.DeltaAngle(th * Mathf.Rad2Deg, tipAngle[q] * Mathf.Rad2Deg)) * Mathf.Deg2Rad;
                float s = d / halfWidth[q];
                if (s < 1) length = Mathf.Max(length, edgeLength + tipLength[q] * Mathf.Pow(1 - s, 1.4f));
            }
            // A small ragged wobble along the torn edge.
            return length + 0.04f * exitR * Mathf.Sin(th * 23 + req.seed) * Mathf.Sin(th * 11 + 1.3f * req.seed);
        }

        // Each column leaves the hat at the hole's edge and runs `Length` along a profile
        // that starts `flare` above the hat's surface (taken as the horn-space XZ plane, which
        // it nearly is) and turns outward by `curl`: the angle falls linearly with distance,
        // so the profile is a circular arc.
        float a0 = req.flare * Mathf.Deg2Rad, bend = req.curl * Mathf.Deg2Rad;
        // Position along the arc after fraction u of its length, as (outward, up) per unit length.
        Vector2 Arc(float u) => Mathf.Abs(bend) < 1e-4f
            ? u * new Vector2(Mathf.Cos(a0), Mathf.Sin(a0))
            : new Vector2(Mathf.Sin(a0) - Mathf.Sin(a0 - bend * u), Mathf.Cos(a0 - bend * u) - Mathf.Cos(a0)) / bend;
        const float Sink = 0.0015f; // the root starts below the hat surface so no gap shows
        int ringsN = Rings.Length;
        var top = new Vector3[ringsN, columns + 1];
        var bottom = new Vector3[ringsN, columns + 1];
        var uvs = new Vector2[ringsN, columns + 1];
        int missed = 0;
        // Root heights: the hat or what lies on it, sampled at the root and partway out along
        // the torn edge, then smoothed around the hole so the root doesn't step at the
        // ribbon's edges.
        var rootY = new float[columns];
        var rootLift = new float[columns];
        for (int j = 0; j < columns; j++)
        {
            float phi = 2 * Mathf.PI * j / columns;
            var dir = new Vector3(Mathf.Cos(phi), 0, Mathf.Sin(phi));
            var hit = Surface(dir.x * innerR, dir.z * innerR, exitY);
            if (!hit.ok) { missed++; hit.y = exitY; }
            rootY[j] = hit.y;
            // Anything up to MaxLift above the hat fabric counts as lying on it.
            const float MaxLift = 0.012f;
            foreach (float r in new[] { innerR, innerR + edgeLength })
            {
                float above = OccluderTop(dir.x * r, dir.z * r, hit.y + MaxLift) - hit.y;
                if (above > 0) rootLift[j] = Mathf.Max(rootLift[j], above);
            }
        }
        const int Smooth = 4; // columns either side; a window max, then a window average
        var spread = Enumerable.Range(0, columns).Select(j => Enumerable.Range(-Smooth, 2 * Smooth + 1).Max(k => rootLift[(j + k + columns) % columns])).ToArray();
        var lift = Enumerable.Range(0, columns).Select(j => Enumerable.Range(-Smooth, 2 * Smooth + 1).Average(k => spread[(j + k + columns) % columns])).ToArray();
        for (int j = 0; j <= columns; j++)
        {
            float phi = 2 * Mathf.PI * (j % columns) / columns;
            float length = Length(phi);
            float baseY = rootY[j % columns] + (float)lift[j % columns] + req.raise;
            for (int i = 0; i < ringsN; i++)
            {
                float rho = Rings[i];
                float th = phi + req.sweep * rho * length / exitR;
                var dir = new Vector3(Mathf.Cos(th), 0, Mathf.Sin(th));
                // Ring 0 is the sleeve's foot, sunk into the hat; the flare starts at its top.
                var arc = Arc(rho) * length;
                float rise = i == 0 ? -Sink : req.sleeve + arc.y;
                float radius = i == 0 ? innerR : 0.97f * HornRadiusAt(baseY + req.sleeve) + arc.x;
                top[i, j] = dir * radius + Vector3.up * (baseY + rise);
                // The fabric's inner face looks up and in, toward the horn; thickness goes behind it.
                float angle = a0 - bend * rho;
                var inner = Vector3.up * Mathf.Cos(angle) - dir * Mathf.Sin(angle);
                bottom[i, j] = top[i, j] - inner * req.thickness;
                uvs[i, j] = UvAt(top[i, j].x, top[i, j].z);
            }
        }

        // Closed shell: the inner face (toward the horn), the outer face, the torn edge and
        // the root (hidden in the hat). Separate vertices per part keep the edges crisp.
        var verts = new List<Vector3>();
        var uv0 = new List<Vector2>();
        var tris = new List<int>();
        int Add(Vector3 p, Vector2 uv) { verts.Add(p); uv0.Add(uv); return verts.Count - 1; }
        void Quad(int a, int b, int c, int d, bool flip)
        {
            if (flip) tris.AddRange(new[] { a, c, b, a, d, c });
            else tris.AddRange(new[] { a, b, c, a, c, d });
        }
        // Unity's front-face normal is cross(b - a, c - a). For grid quads (i,j) (i+1,j)
        // (i+1,j+1) that is along-the-fabric x tangential, which points away from the horn and
        // down, so the inner face flips and the outer face doesn't. For wall quads (back j,
        // front j, front j+1) it is (inner normal) x tangential, which points out along the fabric.
        void Sheet(Vector3[,] grid, bool flip)
        {
            var index = new int[ringsN, columns + 1];
            // The last column is the first again, welded so no crease shows along it.
            for (int i = 0; i < ringsN; i++) for (int j = 0; j <= columns; j++) index[i, j] = j < columns ? Add(grid[i, j], uvs[i, j]) : index[i, 0];
            for (int i = 0; i + 1 < ringsN; i++) for (int j = 0; j < columns; j++)
                Quad(index[i, j], index[i + 1, j], index[i + 1, j + 1], index[i, j + 1], flip);
        }
        Sheet(top, true);
        Sheet(bottom, false);
        void Wall(int ring, bool flip)
        {
            var front = new int[columns + 1];
            var back = new int[columns + 1];
            for (int j = 0; j <= columns; j++) { front[j] = Add(top[ring, j], uvs[ring, j]); back[j] = Add(bottom[ring, j], uvs[ring, j]); }
            for (int j = 0; j < columns; j++) Quad(back[j], front[j], front[j + 1], back[j + 1], flip);
        }
        Wall(ringsN - 1, false);
        Wall(0, true);

        // Rebuild the saved asset in place through the Mesh API. Copying serialized data into
        // it (EditorUtility.CopySerialized) updated the file but not what the open editor drew.
        var existing = req.command == "build" ? AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath) : null;
        var mesh = existing ? existing : new Mesh();
        mesh.Clear();
        mesh.name = "HornCollar";
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uv0);
        mesh.SetTriangles(tris, 0);
        // Check the winding rather than trust the comment: the inner face points up the horn,
        // the torn edge away from it.
        mesh.RecalculateNormals();
        int sheet = ringsN * columns; // welded: one vertex per column
        var normals = mesh.normals;
        float upness = 0, outness = 0;
        for (int k = 0; k < sheet; k++) upness += normals[k].y;
        for (int k = 2 * sheet; k < 2 * sheet + 2 * (columns + 1); k++) outness += Vector3.Dot(normals[k], new Vector3(verts[k].x, 0, verts[k].z).normalized);
        if (upness <= 0 || outness <= 0) throw new Exception($"Collar winding is wrong (up {upness}, out {outness})");
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();

        var report = new Report
        {
            command = req.command, scene = avatar.scene.path,
            hornPath = AnimationUtility.CalculateTransformPath(horn, avatar.transform),
            hatPath = AnimationUtility.CalculateTransformPath(hat.transform, avatar.transform),
            material = AssetDatabase.GetAssetPath(material), submesh = submesh,
            exitHeight = exitY, maxRootLift = rootLift.Max(), exitRadius = exitR, innerRadius = innerR, tipLength = tipLength.Max(), edgeLength = edgeLength,
            centerHatThickness = exitY - lining,
            vertices = verts.Count, triangles = tris.Count / 3, missedRays = missed,
            uvMin = new Vector2(uv0.Min(u => u.x), uv0.Min(u => u.y)), uvMax = new Vector2(uv0.Max(u => u.x), uv0.Max(u => u.y)),
            hatBonesAtExit = HatBones(hat, hatTris, center.tri),
        };
        if (req.command == "report") { UnityEngine.Object.DestroyImmediate(mesh); report.notes = notes.ToArray(); return report; }

        // Save the mesh, then add or update the collar under the horn as one Undo step.
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/VRUnityGenerated", "HornCollar");
        if (existing) EditorUtility.SetDirty(mesh);
        else AssetDatabase.CreateAsset(mesh, MeshPath);
        AssetDatabase.SaveAssets();
        report.meshAsset = MeshPath;

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Hat collar around horn");
        int group = Undo.GetCurrentGroup();
        var child = horn.Find(ChildName);
        if (!child)
        {
            var go = new GameObject(ChildName, typeof(MeshFilter), typeof(MeshRenderer));
            Undo.RegisterCreatedObjectUndo(go, "Hat collar around horn");
            Undo.SetTransformParent(go.transform, horn, "Hat collar around horn");
            child = go.transform;
            notes.Add("Created " + ChildName);
        }
        Undo.RecordObject(child, "Hat collar around horn");
        child.localPosition = Vector3.zero; child.localRotation = Quaternion.identity; child.localScale = Vector3.one;
        child.gameObject.layer = horn.gameObject.layer;
        var filter = child.GetComponent<MeshFilter>();
        var renderer = child.GetComponent<MeshRenderer>();
        Undo.RecordObject(filter, "Hat collar around horn");
        Undo.RecordObject(renderer, "Hat collar around horn");
        filter.sharedMesh = mesh;
        renderer.sharedMaterials = new[] { material };
        // Light it like the hat it belongs to.
        renderer.probeAnchor = hat.probeAnchor;
        renderer.lightProbeUsage = hat.lightProbeUsage;
        renderer.reflectionProbeUsage = hat.reflectionProbeUsage;
        renderer.shadowCastingMode = hat.shadowCastingMode;
        renderer.receiveShadows = hat.receiveShadows;
        foreach (var o in new UnityEngine.Object[] { child, filter, renderer }) PrefabUtility.RecordPrefabInstancePropertyModifications(o);
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(avatar.scene);
        SceneView.RepaintAll();
        report.notes = notes.ToArray();
        return report;
    }

    // What the user sees: the last active Scene view's camera, copied without moving it.
    static void RenderSceneView(string path)
    {
        var view = SceneView.lastActiveSceneView;
        if (!view || !view.camera) throw new Exception("No active Scene view");
        int width = Mathf.Max(64, (int)view.camera.pixelWidth), height = Mathf.Max(64, (int)view.camera.pixelHeight);
        var go = new GameObject("Scene view copy") { hideFlags = HideFlags.HideAndDontSave };
        var texture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try
        {
            var camera = go.AddComponent<Camera>();
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
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // Height where the vertical line through the horn axis crosses triangle abc, if it does.
    static float? RayY(Vector3 a, Vector3 b, Vector3 c)
    {
        float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        if (Mathf.Abs(d) < 1e-14f) return null;
        float u = ((b.z - c.z) * (-c.x) + (c.x - b.x) * (-c.z)) / d;
        float v = ((c.z - a.z) * (-c.x) + (a.x - c.x) * (-c.z)) / d;
        float w = 1 - u - v;
        if (u < 0 || v < 0 || w < 0) return null;
        return u * a.y + v * b.y + w * c.y;
    }

    // Bones (and summed weights) of the hat triangle the horn exits through; if PhysBone-driven
    // bones dominate, the hat can sway away from the rigid horn and its collar.
    static string[] HatBones(SkinnedMeshRenderer hat, int[] tris, int tri)
    {
        var mesh = hat.sharedMesh;
        var sums = new Dictionary<string, float>();
        var perVertex = mesh.GetBonesPerVertex();
        var weights = mesh.GetAllBoneWeights();
        var starts = new int[mesh.vertexCount];
        for (int v = 0, s = 0; v < mesh.vertexCount; v++) { starts[v] = s; s += perVertex[v]; }
        for (int k = 0; k < 3; k++)
        {
            int v = tris[tri * 3 + k];
            for (int i = 0; i < perVertex[v]; i++)
            {
                var w = weights[starts[v] + i];
                string name = hat.bones[w.boneIndex] ? hat.bones[w.boneIndex].name : "(missing)";
                sums[name] = (sums.TryGetValue(name, out var x) ? x : 0) + w.weight / 3;
            }
        }
        return sums.OrderByDescending(p => p.Value).Select(p => p.Key + " " + p.Value.ToString("F3")).ToArray();
    }
}
