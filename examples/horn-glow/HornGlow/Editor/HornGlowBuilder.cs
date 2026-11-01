// Builds the HornGlow prototype from code: horn mesh, material, FX controller, prefab and
// test scene, then checks it through the real avatar build pipeline (VRCFury included).
// Batch: Unity -batchmode -projectPath <project> -executeMethod HornGlowBuilder.BuildAll
//   HORNGLOW_OUT=<dir> also writes preview PNGs and report.json there.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using com.vrcfury.api;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;
using VRC.SDKBase.Editor.BuildPipeline;

public static class HornGlowBuilder
{
    const string Root = "Assets/HornGlow";
    const string Gen = Root + "/Generated";
    const string PrefabPath = Root + "/HornGlow.prefab";
    const string ScenePath = Root + "/Test/HornGlowTest.unity";
    const string ParamHand = "HornGlow/Hand";
    const string ParamSocket = "HornGlow/Socket";
    // How far along the horn the hand or socket is: 1 at the base, about 0.2 at the tip.
    const string ParamHandDepth = "HornGlow/HandDepth";
    const string ParamSocketDepth = "HornGlow/SocketDepth";
    // Stroke charge, computed on every client from the contacts (not synced). A hand or
    // socket arriving at the tip or base half of the horn is a stroke; strokes keep the horn
    // Active for ActiveSeconds. How fast the strokes come doesn't matter, only that they keep
    // coming: after WarmupSeconds of activity (going still restarts it) the horn is Warm and
    // Charge rises by one every RiseStep while active, up to MaxCharge, with the glow a level
    // per ChargePerLevel. Once activity stops it drains by one every DrainStep; at 0 the horn
    // is cold again.
    const string ParamCharge = "HornGlow/Charge";
    const string ParamStroked = "HornGlow/Stroked";
    const string ParamActive = "HornGlow/Active";
    const string ParamWarm = "HornGlow/Warm";
    const string ParamTimer = "HornGlow/ActiveTimer";
    const string ParamChargeTimer = "HornGlow/ChargeTimer";
    const string ParamWarmupTimer = "HornGlow/WarmupTimer";
    public const float WarmupSeconds = 4f;
    const int ChargePerLevel = 3;
    public const int MaxCharge = 16;
    // ActiveSeconds is the longest pause between strokes that still counts as activity; slow
    // strokes, or dragging a test socket by mouse, need about 2 s.
    const float ActiveSeconds = 2f, RiseStep = 0.95f, DrainStep = 0.3f;
    static readonly float[] GlowLevels = { 0.2f, 0.36f, 0.52f, 0.68f, 0.84f, 1f };
    // Menu toggle: full glow without touch. Synced and saved.
    const string ParamAlways = "HornGlow/Always";
    // Menu radial: hue of the magic colours. Synced and saved; the default is a cyan chosen to
    // match an outfit's emission colour. Must match the shaders' _Hue default.
    const string ParamHue = "HornGlow/Hue";
    const float DefaultHue = 0.5f;
    // Menu radial: makes the touch charge easier (strokes missed in VR at low frame rates).
    // Synced and saved, so every client charges alike. At 1, a pause of up to EasyActiveSeconds
    // between strokes still counts as activity, charge rises every EasyRiseStep and there is
    // no warm-up; the activity and rise speeds blend linearly in between, and the warm-up
    // shortens to about WarmupSeconds * (1 - ease). 0 is the normal behaviour above. Once the
    // warm-up is shorter than the activity from one stroke (from about 0.4), a pat lights it.
    const string ParamEase = "HornGlow/Ease";
    const string ParamActiveSpeed = "HornGlow/ActiveSpeed", ParamWarmupSpeed = "HornGlow/WarmupSpeed", ParamRiseSpeed = "HornGlow/RiseSpeed";
    const float EasyActiveSeconds = 3f, EasyRiseStep = 0.45f;

    // The eyes glow with the horn. The glow states also animate lilToon's second emission on
    // the avatar's face renderer (EyeRenderer, a path from the avatar root, so VRCFury resolves
    // it outside the prefab): _Emission2ndBlend follows the glow times EyeGlow, and the hue
    // layer sets _Emission2ndColor to EyeColor with the menu hue, times EyeIntensity. The face
    // material needs emission 2nd on with an iris mask (a copy of the face material).
    // WithHue is piecewise linear between multiples of 1/6, so HueSteps = 6 blends exactly.
    const string EyeRenderer = "/Body";
    const float EyeGlow = 1f, EyeIntensity = 1.6f;
    static readonly Color EyeColor = new Color(0.3f, 0.85f, 1f);
    const int HueSteps = 6;
    // First-person tint: the inside of a box around the eyes (FirstPersonTint, HornGlow/Vignette)
    // follows the glow and hue. Only the local player's FX enables its renderer (IsLocal), and a
    // VRCHeadChop keeps it full size although it is under the head. The box must hold the
    // avatar's view position with room to spare (VRChat's head shrinking may leave it centred
    // on the head bone instead); the shader draws for a camera within the material's _Reach.
    const string TintPath = "FirstPersonTint", ParamIsLocal = "IsLocal";
    const float TintHalf = 0.3f;
    static readonly Vector3 TintCenter = new Vector3(0, -0.06f, -0.02f);

    // Horn shape, in metres. The horn this replaced was 0.127 long with a 0.0134 base radius.
    public const float Length = 0.089f;
    public const float BaseRadius = 0.0168f;
    // Spiral groove with a cusp profile: shoulders curve smoothly down from the surface and
    // meet at a sharp crease. Depth falls off with the 4th power of the distance from it, so
    // the shoulders join the surface with no visible kink. GrooveDepth is a fraction of the
    // radius, GrooveWidth a fraction of a turn either side. The groove is full depth up to
    // GrooveFadeStart along the horn and fades out by GrooveFadeEnd; past that the tip is
    // plain rings (CapRings, densest at the point).
    // Spiral phase along the horn is Turns * (t + SpiralAccel * t^2): Turns per length at the
    // base, tightening toward the tip. The shaders' _SpiralTurns/_SpiralAccel must match.
    public const float Turns = 4f, SpiralAccel = 0.2f;
    static float Phase(float t) => Turns * (t + SpiralAccel * t * t);
    static float PhaseToT(float phase) =>
        (-1 + Mathf.Sqrt(1 + 4 * SpiralAccel * Mathf.Max(0, phase) / Turns)) / (2 * SpiralAccel);
    public const float GrooveDepth = 0.10f, GrooveWidth = 0.10f, GrooveFadeStart = 0.5f, GrooveFadeEnd = 0.93f;
    public const int CapRings = 16;
    // Normals are smoothed only across edges under SmoothAngle (the old FBX's import
    // setting) and never across the groove's bottom line. Where the groove fades out they
    // blend into the smooth horn's own normals, so the tip has neither a crease nor slivers.
    public const int Sides = 24;
    public const float SmoothAngle = 60f;
    // UV0 is squeezed into this small patch of the base material's textures; pick a
    // place where the base material's normal map is flat. The horn's own
    // coordinates go in UV1 (see BuildHornMesh).
    public static readonly Vector2 MaterialUvCenter = new Vector2(0.35f, 0.75f);
    public const float MaterialUvSize = 0.01f; // unused: UV0 is now the single point MaterialUvCenter
    // The avatar's horn axis is 63 degrees forward of vertical.
    public const float TiltDegrees = 63.2f;

    static readonly List<string> Log = new List<string>();
    static void Note(string s) { Log.Add(s); Debug.Log("[HornGlow] " + s); }

    [MenuItem("Tools/HornGlow/Build Prototype")]
    public static void BuildMenu() => Build(null);

    public static void BuildAll()
    {
        var ok = false;
        try { ok = Build(Environment.GetEnvironmentVariable("HORNGLOW_OUT")); }
        catch (Exception e) { Debug.LogException(e); }
        EditorApplication.Exit(ok ? 0 : 1);
    }

    static bool Build(string outDir)
    {
        Log.Clear();
        Directory.CreateDirectory(Gen);
        Directory.CreateDirectory(Root + "/Test");

        var mesh = SaveAsset(BuildHornMesh(), Gen + "/CuteHorn.asset");
        var material = BuildMaterial("HornGlow/Horn", "HornGlow");
        var overlay = BuildMaterial("HornGlow/HornOverlay", "HornOverlay");
        var auraMaterial = BuildMaterial("HornGlow/Aura", "HornAura");
        var auraMesh = SaveAsset(BuildBoxMesh(auraMaterial), Gen + "/AuraBox.asset");
        var tintMaterial = BuildMaterial("HornGlow/Vignette", "HornVignette");
        var tintMesh = SaveAsset(BuildCube(TintHalf), Gen + "/TintBox.asset");
        var controller = BuildController();
        var (menu, prms) = BuildMenuAssets();
        var prefab = BuildPrefab(mesh, new[] { material, overlay }, auraMesh, auraMaterial, tintMesh, tintMaterial, controller, menu, prms);
        BuildTestSocketPrefab();
        BuildTestScene(prefab);

        var ok = Verify();
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            RenderPreviews(prefab, outDir);
            File.WriteAllText(Path.Combine(outDir, "report.txt"), string.Join("\n", Log) + "\n");
        }
        Note(ok ? "RESULT PASS" : "RESULT FAIL");
        return ok;
    }

    static T SaveAsset<T>(T asset, string path) where T : UnityEngine.Object
    {
        var existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (existing != null)
        {
            EditorUtility.CopySerialized(asset, existing);
            UnityEngine.Object.DestroyImmediate(asset);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    // ---- Mesh -------------------------------------------------------------------------

    static float Sq(float x) => x * x;

    static float GrooveFade(float t) => 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(GrooveFadeStart, GrooveFadeEnd, t));

    // Normal of the groove-free horn (a surface of revolution) at height t, angle th.
    static Vector3 SmoothNormal(float t, float th)
    {
        const float e = 1e-4f;
        if (t > 1 - e) return Vector3.up;
        float dr = (Radius(Mathf.Min(1, t + e)) - Radius(Mathf.Max(0, t - e))) / (Mathf.Min(1, t + e) - Mathf.Max(0, t - e));
        return new Vector3(Mathf.Cos(th), -dr / Length, Mathf.Sin(th)).normalized;
    }

    static float Radius(float t) => BaseRadius * (1 - 0.5f * t) * Mathf.Sqrt(Mathf.Max(0, 1 - t * t * t));

    // Horn along local +Y from its base. Up to GrooveFadeEnd, mesh rows follow the spiral: a
    // row is a line of constant h = Phase(t) - angle / 2pi, and the groove bottom (whole h)
    // lies on mesh edges. That part ends on a plain ring at GrooveFadeEnd, where the tip cap
    // of ordinary rings takes over. UV1: u goes around, v from base (0) to tip (1); the seam
    // is on the underside, facing the head. UV0 is the single point MaterialUvCenter, and
    // tangents run around the horn, so a base material's normal map has no seam to show.
    static Mesh BuildHornMesh()
    {
        // Rows per turn: dense across the curved groove walls (closest near the crease), a few
        // across the land between grooves. `groove` is the profile, 1 at the crease.
        float w = GrooveWidth;
        var walls = new[] { 0.06f, 0.15f, 0.27f, 0.42f, 0.6f, 0.8f, 1f };
        var offsets = new List<float> { 0 };
        offsets.AddRange(walls.Select(x => x * w));
        offsets.AddRange(new[] { 0.35f, 0.5f, 0.65f });
        offsets.AddRange(walls.Reverse().Select(x => 1 - x * w));
        var rows = new List<(float h, float groove)>();
        int lastTurn = Mathf.CeilToInt(Phase(GrooveFadeEnd)) + 1;
        for (int n = -1; n <= lastTurn; n++)
            foreach (var o in offsets)
            {
                float fromBottom = Mathf.Min(o, 1 - o);
                rows.Add((n + o, Sq(Sq(Mathf.Max(0, 1 - fromBottom / w)))));
            }

        Vector3 Point(float t, int j, float groove)
        {
            float th = 2 * Mathf.PI * j / Sides + Mathf.PI / 2;
            float radius = Radius(t) * (1 - GrooveDepth * GrooveFade(t) * groove);
            // Base sits slightly below the origin so it sinks into the head.
            return new Vector3(radius * Mathf.Cos(th), Length * t - 0.15f * BaseRadius, radius * Mathf.Sin(th));
        }
        var grid = new Vector3[rows.Count, Sides + 1];
        var gridUv = new Vector2[rows.Count, Sides + 1];
        for (int r = 0; r < rows.Count; r++)
            for (int j = 0; j <= Sides; j++)
            {
                float t = Mathf.Min(PhaseToT(rows[r].h + (float)j / Sides), GrooveFadeEnd);
                grid[r, j] = Point(t, j % Sides, rows[r].groove);
                gridUv[r, j] = new Vector2((float)j / Sides, t);
            }
        // Going once around a row ends one turn further along, at the next turn's row; make
        // those seam points exactly equal so they share normals.
        int perTurn = offsets.Count;
        for (int r = 0; r + perTurn < rows.Count; r++) grid[r, Sides] = grid[r + perTurn, 0];

        // Unwelded triangles first, so creases can keep separate normals.
        var corners = new List<(Vector3 p, Vector2 uv, int face)>();
        var faceNormals = new List<Vector3>();
        // Which stretch between groove bottoms each face is on; normals never blend across one.
        // Band numbers count turns, so they step by one across the seam (see SameBand).
        var faceBand = new List<int>();
        var faceColumn = new List<int>();
        void Tri((Vector3 p, Vector2 uv) a, (Vector3 p, Vector2 uv) b, (Vector3 p, Vector2 uv) c, int band, int column)
        {
            var n = Vector3.Cross(b.p - a.p, c.p - a.p);
            // Rows clamped at the base or GrooveFadeEnd collapse; skip their zero-area triangles.
            if (n.sqrMagnitude < 1e-18f) return;
            faceNormals.Add(n);
            faceBand.Add(band);
            faceColumn.Add(column);
            foreach (var v in new[] { a, b, c }) corners.Add((v.p, v.uv, faceNormals.Count - 1));
        }
        (Vector3, Vector2) G(int r, int j) => (grid[r, j], gridUv[r, j]);
        for (int r = 0; r + 1 < rows.Count; r++)
        {
            int band = Mathf.FloorToInt((rows[r].h + rows[r + 1].h) * 0.5f);
            for (int j = 0; j < Sides; j++)
            {
                Tri(G(r, j), G(r + 1, j), G(r + 1, j + 1), band, j);
                Tri(G(r, j), G(r + 1, j + 1), G(r, j + 1), band, j);
            }
        }
        // Tip cap: rings from GrooveFadeEnd to the point, closing in on it quadratically.
        (Vector3, Vector2) Cap(int k, int j)
        {
            float f = (float)k / CapRings;
            float t = Mathf.Lerp(GrooveFadeEnd, 1, 1 - Sq(1 - f));
            return (Point(t, j % Sides, 0), new Vector2((float)j / Sides, t));
        }
        for (int k = 0; k < CapRings; k++)
            for (int j = 0; j < Sides; j++)
            {
                Tri(Cap(k, j), Cap(k + 1, j), Cap(k + 1, j + 1), int.MaxValue, -1);
                Tri(Cap(k, j), Cap(k + 1, j + 1), Cap(k, j + 1), int.MaxValue, -1);
            }

        // Face normals point outward for this winding; normals are the area-weighted sum of
        // the faces at the same point within SmoothAngle of the corner's own face, blended
        // into the groove-free horn's normal as the groove fades out.
        float cosLimit = Mathf.Cos(SmoothAngle * Mathf.Deg2Rad);
        Vector3Int Key(Vector3 p) => Vector3Int.RoundToInt(p * 1e6f);
        var facesAt = new Dictionary<Vector3Int, List<int>>();
        foreach (var c in corners)
        {
            if (!facesAt.TryGetValue(Key(c.p), out var list)) facesAt[Key(c.p)] = list = new List<int>();
            if (!list.Contains(c.face)) list.Add(c.face);
        }
        // Faces meeting across the seam: the one in the last column is a turn behind.
        bool SameBand(int f, int g)
        {
            int bf = faceBand[f], bg = faceBand[g];
            if (faceColumn[f] >= 0 && faceColumn[g] >= 0 && Mathf.Abs(faceColumn[f] - faceColumn[g]) > Sides / 2)
            {
                if (faceColumn[f] > faceColumn[g]) bf++; else bg++;
            }
            return bf == bg;
        }
        Vector3 Unit(Vector3 v) { float len = v.magnitude; return len > 0 ? v / len : Vector3.up; }

        // Weld corners that end up identical, so shading and tangents are continuous.
        var index = new Dictionary<(Vector3Int, Vector3Int, Vector2Int), int>();
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var hornUvs = new List<Vector2>();
        var tris = new List<int>();
        foreach (var c in corners)
        {
            var own = Unit(faceNormals[c.face]);
            var sum = Vector3.zero;
            foreach (var f in facesAt[Key(c.p)])
                if (SameBand(f, c.face) && Vector3.Dot(Unit(faceNormals[f]), own) >= cosLimit)
                    sum += faceNormals[f];
            float t = c.uv.y, th = 2 * Mathf.PI * c.uv.x + Mathf.PI / 2;
            var normal = Unit(Vector3.Lerp(SmoothNormal(t, th), Unit(sum), GrooveFade(t)));
            var key = (Key(c.p), Vector3Int.RoundToInt(normal * 1e4f), Vector2Int.RoundToInt(c.uv * 1e5f));
            if (!index.TryGetValue(key, out int v))
            {
                index[key] = v = verts.Count;
                verts.Add(c.p);
                normals.Add(normal);
                // Around the horn, perpendicular to the normal.
                var around = new Vector3(-Mathf.Sin(th), 0, Mathf.Cos(th));
                tangents.Add((Vector4)Unit(around - normal * Vector3.Dot(around, normal)) + new Vector4(0, 0, 0, 1));
                hornUvs.Add(c.uv);
            }
            tris.Add(v);
        }
        var mesh = new Mesh { name = "CuteHorn" };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetTangents(tangents);
        mesh.SetUVs(0, Enumerable.Repeat(MaterialUvCenter, verts.Count).ToList());
        mesh.SetUVs(1, hornUvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        // Seam check: vertices at u = 0 and u = 1 that share a position should share a normal
        // (groove-bottom points have one per side, so compare with the closest).
        float seamAngle = 0, worstT = 0; int seamPairs = 0, seamBad = 0;
        var atStart = new Dictionary<Vector3Int, List<Vector3>>();
        for (int i = 0; i < verts.Count; i++)
            if (hornUvs[i].x == 0)
            {
                if (!atStart.TryGetValue(Key(verts[i]), out var list)) atStart[Key(verts[i])] = list = new List<Vector3>();
                list.Add(normals[i]);
            }
        for (int i = 0; i < verts.Count; i++)
            if (hornUvs[i].x == 1 && atStart.TryGetValue(Key(verts[i]), out var other))
            {
                float angle = other.Min(n => Vector3.Angle(normals[i], n));
                seamPairs++;
                if (angle > 1) { seamBad++; Note($"  seam mismatch {angle:F1} deg at t {hornUvs[i].y:F3}"); }
                if (angle > seamAngle) { seamAngle = angle; worstT = hornUvs[i].y; }
            }
        Note($"Horn seam: {seamPairs} pairs, {seamBad} over 1 degree, largest {seamAngle:F3} degrees at t {worstT:F3}");
        Note($"Horn mesh: {tris.Count / 3} triangles, {verts.Count} vertices, {Phase(1):F1} spiral turns ({Turns} per length at the base), {Sides} sides");
        return mesh;
    }

    // The aura's ray-marching volume: a box matching the shader's _BoxMin/_BoxMax.
    static Mesh BuildBoxMesh(Material aura)
    {
        Vector3 lo = aura.GetVector("_BoxMin"), hi = aura.GetVector("_BoxMax");
        var verts = new List<Vector3>();
        for (int i = 0; i < 8; i++)
            verts.Add(new Vector3((i & 1) == 0 ? lo.x : hi.x, (i & 2) == 0 ? lo.y : hi.y, (i & 4) == 0 ? lo.z : hi.z));
        var mesh = new Mesh { name = "AuraBox" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(new[]
        {
            0, 2, 3, 0, 3, 1,  4, 5, 7, 4, 7, 6,  0, 1, 5, 0, 5, 4,
            2, 6, 7, 2, 7, 3,  0, 4, 6, 0, 6, 2,  1, 3, 7, 1, 7, 5,
        }, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // A cube of the given half size around the origin.
    static Mesh BuildCube(float half)
    {
        var mesh = new Mesh { name = "TintBox" };
        var v = new List<Vector3>();
        for (int i = 0; i < 8; i++) v.Add(new Vector3((i & 1) != 0 ? half : -half, (i & 2) != 0 ? half : -half, (i & 4) != 0 ? half : -half));
        mesh.SetVertices(v);
        mesh.SetTriangles(new[]
        {
            0, 2, 3, 0, 3, 1,  4, 5, 7, 4, 7, 6,  0, 1, 5, 0, 5, 4,
            2, 6, 7, 2, 7, 3,  0, 4, 6, 0, 6, 2,  1, 3, 7, 1, 7, 5,
        }, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---- Material and animator ---------------------------------------------------------

    static Material BuildMaterial(string shaderName, string name)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null) throw new Exception(shaderName + " shader not found");
        // Regenerated from the shader's defaults every build; tune looks in the shader.
        return SaveAsset(new Material(shader) { name = name }, Gen + "/" + name + ".mat");
    }

    static AnimationClip GlowClip(string name, float glow) => SaveAsset(MakeGlowClip(name, glow), Gen + "/" + name + ".anim");

    static void Set(AnimationClip clip, string path, Type type, string property, float value) =>
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property),
            new AnimationCurve(new Keyframe(0, value), new Keyframe(1f / 60, value)));

    static AnimationClip MakeGlowClip(string name, float glow)
    {
        var clip = new AnimationClip { name = name };
        foreach (var path in new[] { "Horn", "Horn/Aura", TintPath })
            Set(clip, path, typeof(MeshRenderer), "material._Glow", glow);
        Set(clip, EyeRenderer, typeof(SkinnedMeshRenderer), "material._Emission2ndBlend", glow * EyeGlow);
        return clip;
    }

    // The same as the shaders' WithHue: the colour with its hue replaced.
    static Color WithHue(Color c, float hue)
    {
        float hi = Mathf.Max(c.r, c.g, c.b), lo = Mathf.Min(c.r, c.g, c.b);
        float Pure(float offset) => Mathf.Clamp01(Mathf.Abs((hue + offset) % 1 * 6 - 3) - 1);
        return new Color(lo + (hi - lo) * Pure(1), lo + (hi - lo) * Pure(2f / 3), lo + (hi - lo) * Pure(1f / 3));
    }

    static AnimationClip HueClip(string name, float hue)
    {
        var clip = new AnimationClip { name = name };
        foreach (var path in new[] { "Horn", "Horn/Aura", TintPath })
            Set(clip, path, typeof(MeshRenderer), "material._Hue", hue);
        var eye = WithHue(EyeColor, hue) * EyeIntensity;
        foreach (var (channel, value) in new[] { ("r", eye.r), ("g", eye.g), ("b", eye.b), ("a", 1f) })
            Set(clip, EyeRenderer, typeof(SkinnedMeshRenderer), "material._Emission2ndColor." + channel, value);
        return SaveAsset(clip, Gen + "/" + name + ".anim");
    }

    static AnimatorController BuildController()
    {
        var path = Gen + "/HornGlowFX.controller";
        AssetDatabase.DeleteAsset(path);
        var off = GlowClip("HornGlow Off", 0);
        var full = GlowClip("HornGlow Full", 1);
        AssetDatabase.DeleteAsset(Gen + "/HornGlow Faint.anim");

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        foreach (var p in new[] { ParamHand, ParamSocket, ParamHandDepth, ParamSocketDepth, ParamTimer, ParamChargeTimer, ParamWarmupTimer })
            ctrl.AddParameter(p, AnimatorControllerParameterType.Float);
        ctrl.AddParameter(ParamAlways, AnimatorControllerParameterType.Bool);
        ctrl.AddParameter(new AnimatorControllerParameter
        {
            name = ParamHue, type = AnimatorControllerParameterType.Float, defaultFloat = DefaultHue,
        });
        ctrl.AddParameter(ParamEase, AnimatorControllerParameterType.Float);
        ctrl.AddParameter(ParamIsLocal, AnimatorControllerParameterType.Bool);
        foreach (var p in new[] { ParamActiveSpeed, ParamWarmupSpeed, ParamRiseSpeed })
            ctrl.AddParameter(new AnimatorControllerParameter { name = p, type = AnimatorControllerParameterType.Float, defaultFloat = 1 });
        ctrl.AddParameter(ParamCharge, AnimatorControllerParameterType.Int);
        foreach (var p in new[] { ParamStroked, ParamActive, ParamWarm })
            ctrl.AddParameter(p, AnimatorControllerParameterType.Bool);
        var layer = ctrl.layers[0];
        layer.name = "HornGlow";
        layer.defaultWeight = 1;
        ctrl.layers = new[] { layer };
        var sm = layer.stateMachine;

        AnimatorStateMachine NewLayer(string name)
        {
            ctrl.AddLayer(name);
            var all = ctrl.layers;
            all[all.Length - 1].defaultWeight = 1;
            ctrl.layers = all;
            return all[all.Length - 1].stateMachine;
        }
        AnimatorState State(AnimatorStateMachine machine, string name, Motion motion, Vector3 at)
        {
            var state = machine.AddState(name, at);
            state.motion = motion;
            state.writeDefaultValues = false;
            return state;
        }
        AnimatorStateTransition Go(AnimatorState from, AnimatorState to, float duration)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.hasFixedDuration = true;
            t.duration = duration;
            return t;
        }
        AnimatorStateTransition After(AnimatorState from, AnimatorState to, float exitTime)
        {
            var t = Go(from, to, 0);
            t.hasExitTime = true;
            t.exitTime = exitTime;
            return t;
        }
        // Runs on every client (localOnly off): Charge is computed locally from the contacts.
        void Drive(AnimatorState state, string name, VRC_AvatarParameterDriver.ChangeType type, float value)
        {
            var driver = state.behaviours.OfType<VRCAvatarParameterDriver>().FirstOrDefault()
                         ?? state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.localOnly = false;
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = name, type = type, value = value });
        }

        // An animated parameter running 0 to 1, so a state can wait out its length.
        AnimationClip Timer(string name, string param, float seconds)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), param),
                AnimationCurve.Linear(0, 0, seconds, 1));
            AssetDatabase.AddObjectToAsset(clip, ctrl);
            return clip;
        }
        // The state's playback speed follows a parameter (set by the ease layer).
        void SpeedBy(AnimatorState state, string param)
        {
            state.speedParameterActive = true;
            state.speedParameter = param;
        }

        // Glow: level 0 is Off; once warm, level k glows at GlowLevels[k - 1] when
        // Charge >= ChargePerLevel * (k - 1) + 1, one level at a time with crossfades.
        var levels = new AnimatorState[GlowLevels.Length + 1];
        levels[0] = State(sm, "Off", off, new Vector3(300, 0));
        for (int k = 1; k < levels.Length; k++)
        {
            var clip = MakeGlowClip("HornGlow Level " + k, GlowLevels[k - 1]);
            AssetDatabase.AddObjectToAsset(clip, ctrl);
            levels[k] = State(sm, "Charge " + k, clip, new Vector3(300, 70 * k));
        }
        for (int k = 0; k < levels.Length; k++)
        {
            if (k + 1 < levels.Length)
            {
                var up = Go(levels[k], levels[k + 1], 0.5f);
                up.AddCondition(AnimatorConditionMode.Greater, ChargePerLevel * k, ParamCharge);
                if (k == 0) up.AddCondition(AnimatorConditionMode.If, 0, ParamWarm);
            }
            if (k > 0) Go(levels[k], levels[k - 1], k == 1 ? 1f : 0.4f).AddCondition(AnimatorConditionMode.Less, ChargePerLevel * (k - 1) + 1, ParamCharge);
        }
        // Menu toggle: full glow regardless of charge. Switched off, it fades to Off, and the
        // charge levels can take over during that fade.
        var alwaysState = State(sm, "Always On", full, new Vector3(650, 200));
        sm.defaultState = levels[0];
        foreach (var level in levels)
            Go(level, alwaysState, 0.5f).AddCondition(AnimatorConditionMode.If, 0, ParamAlways);
        var fade = Go(alwaysState, levels[0], 1.5f);
        fade.interruptionSource = TransitionInterruptionSource.Destination;
        fade.AddCondition(AnimatorConditionMode.IfNot, 0, ParamAlways);

        // Strokes, one layer per source. Zones along the horn with hysteresis: arriving at the
        // tip or base half (from off the horn or from the other half) is a stroke.
        void StrokeLayer(string name, string on, string depth)
        {
            var machine = NewLayer(name);
            var away = State(machine, "Away", null, new Vector3(300, 0));
            var tip = State(machine, "Tip", null, new Vector3(150, 120));
            var bottom = State(machine, "Base", null, new Vector3(450, 120));
            machine.defaultState = away;
            foreach (var zone in new[] { tip, bottom })
            {
                Drive(zone, ParamStroked, VRC_AvatarParameterDriver.ChangeType.Set, 1);
                Go(zone, away, 0).AddCondition(AnimatorConditionMode.Less, 0.1f, on);
            }
            var t = Go(away, tip, 0);
            t.AddCondition(AnimatorConditionMode.Greater, 0.4f, on);
            t.AddCondition(AnimatorConditionMode.Less, 0.47f, depth);
            t = Go(away, bottom, 0);
            t.AddCondition(AnimatorConditionMode.Greater, 0.4f, on);
            t.AddCondition(AnimatorConditionMode.Greater, 0.47f, depth);
            Go(tip, bottom, 0).AddCondition(AnimatorConditionMode.Greater, 0.55f, depth);
            Go(bottom, tip, 0).AddCondition(AnimatorConditionMode.Less, 0.4f, depth);
        }
        StrokeLayer("HornGlow Hand Strokes", ParamHand, ParamHandDepth);
        StrokeLayer("HornGlow Socket Strokes", ParamSocket, ParamSocketDepth);

        // Activity: each stroke (re)starts an ActiveSeconds timer; Active while it runs.
        var activity = NewLayer("HornGlow Activity");
        var still = State(activity, "Still", null, new Vector3(300, 0));
        var active = State(activity, "Active", Timer("HornGlow Active", ParamTimer, ActiveSeconds), new Vector3(300, 120));
        activity.defaultState = still;
        SpeedBy(active, ParamActiveSpeed);
        Drive(still, ParamActive, VRC_AvatarParameterDriver.ChangeType.Set, 0);
        Drive(active, ParamActive, VRC_AvatarParameterDriver.ChangeType.Set, 1);
        Drive(active, ParamStroked, VRC_AvatarParameterDriver.ChangeType.Set, 0);
        foreach (var from in new[] { still, active })
            Go(from, active, 0).AddCondition(AnimatorConditionMode.If, 0, ParamStroked);
        After(active, still, 1);

        // Warm-up: WarmupSeconds of activity before anything glows; then Charge starts at 1.
        var warmMachine = NewLayer("HornGlow Warmup");
        var cold = State(warmMachine, "Cold", null, new Vector3(300, 0));
        var warming = State(warmMachine, "Warming", Timer("HornGlow Warmup", ParamWarmupTimer, WarmupSeconds), new Vector3(300, 120));
        var warm = State(warmMachine, "Warm", null, new Vector3(300, 240));
        warmMachine.defaultState = cold;
        SpeedBy(warming, ParamWarmupSpeed);
        Drive(cold, ParamWarm, VRC_AvatarParameterDriver.ChangeType.Set, 0);
        Drive(warm, ParamWarm, VRC_AvatarParameterDriver.ChangeType.Set, 1);
        Drive(warm, ParamCharge, VRC_AvatarParameterDriver.ChangeType.Set, 1);
        Go(cold, warming, 0).AddCondition(AnimatorConditionMode.If, 0, ParamActive);
        Go(warming, cold, 0).AddCondition(AnimatorConditionMode.IfNot, 0, ParamActive);
        After(warming, warm, 1);
        Go(warm, cold, 0).AddCondition(AnimatorConditionMode.Less, 1, ParamCharge);

        // Charge: rises a step every RiseStep while active and warm, falls every DrainStep
        // while not active. Add and Subtract wait out the next step themselves and re-enter
        // (their drivers fire on every entry), so a step costs no extra frames: at low frame
        // rates a step through empty states took noticeably longer.
        var chargeMachine = NewLayer("HornGlow Charge");
        var riseStep = Timer("HornGlow Rise Step", ParamChargeTimer, RiseStep);
        var fallStep = Timer("HornGlow Fall Step", ParamChargeTimer, DrainStep);
        var hold = State(chargeMachine, "Hold", null, new Vector3(300, 0));
        var rise = State(chargeMachine, "Rise", riseStep, new Vector3(100, 120));
        var add = State(chargeMachine, "Add", riseStep, new Vector3(100, 240));
        var fall = State(chargeMachine, "Fall", fallStep, new Vector3(500, 120));
        var sub = State(chargeMachine, "Subtract", fallStep, new Vector3(500, 240));
        chargeMachine.defaultState = hold;
        SpeedBy(rise, ParamRiseSpeed);
        SpeedBy(add, ParamRiseSpeed);
        Drive(add, ParamCharge, VRC_AvatarParameterDriver.ChangeType.Add, 1);
        Drive(sub, ParamCharge, VRC_AvatarParameterDriver.ChangeType.Add, -1);
        var startRise = Go(hold, rise, 0);
        startRise.AddCondition(AnimatorConditionMode.If, 0, ParamActive);
        startRise.AddCondition(AnimatorConditionMode.If, 0, ParamWarm);
        startRise.AddCondition(AnimatorConditionMode.Less, MaxCharge, ParamCharge);
        var startFall = Go(hold, fall, 0);
        startFall.AddCondition(AnimatorConditionMode.IfNot, 0, ParamActive);
        startFall.AddCondition(AnimatorConditionMode.Greater, 0, ParamCharge);
        foreach (var rising in new[] { rise, add })
        {
            Go(rising, hold, 0).AddCondition(AnimatorConditionMode.IfNot, 0, ParamActive);
            Go(rising, hold, 0).AddCondition(AnimatorConditionMode.IfNot, 0, ParamWarm);
        }
        Go(add, hold, 0).AddCondition(AnimatorConditionMode.Greater, MaxCharge - 1, ParamCharge);
        After(rise, add, 1);
        After(add, add, 1);
        Go(fall, hold, 0).AddCondition(AnimatorConditionMode.If, 0, ParamActive);
        Go(sub, hold, 0).AddCondition(AnimatorConditionMode.If, 0, ParamActive);
        Go(sub, hold, 0).AddCondition(AnimatorConditionMode.Less, 1, ParamCharge);
        After(fall, sub, 1);
        After(sub, sub, 1);

        // Hue follows the menu radial; 0 and 1 are both red, so _Hue blends linearly, and the
        // eye colour exactly, between the HueSteps + 1 clips.
        var hueTree = new BlendTree
        {
            name = "Hue",
            blendType = BlendTreeType.Simple1D,
            blendParameter = ParamHue,
            useAutomaticThresholds = false,
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(hueTree, ctrl);
        for (int k = 0; k <= HueSteps; k++)
            hueTree.AddChild(HueClip($"HornGlow Hue {k}-{HueSteps}", (float)k / HueSteps), (float)k / HueSteps);
        foreach (var old in new[] { "HornGlow Hue 0", "HornGlow Hue 1" })
            AssetDatabase.DeleteAsset(Gen + "/" + old + ".anim");
        State(NewLayer("HornGlow Hue"), "Hue", hueTree, new Vector3(300, 0));

        // Ease follows the menu radial: blends the timer speeds from normal to easy. The
        // warm-up speed, 1 / (1 - ease), is piecewise linear through the thresholds below and
        // ends fast enough to finish within a frame.
        AnimationClip Speeds(string name, float activeSpeed, float warmupSpeed, float riseSpeed)
        {
            var clip = new AnimationClip { name = name };
            foreach (var (param, value) in new[] { (ParamActiveSpeed, activeSpeed), (ParamWarmupSpeed, warmupSpeed), (ParamRiseSpeed, riseSpeed) })
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), param),
                    new AnimationCurve(new Keyframe(0, value), new Keyframe(1f / 60, value)));
            AssetDatabase.AddObjectToAsset(clip, ctrl);
            return clip;
        }
        var easeTree = new BlendTree
        {
            name = "Ease",
            blendType = BlendTreeType.Simple1D,
            blendParameter = ParamEase,
            useAutomaticThresholds = false,
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(easeTree, ctrl);
        foreach (var e in new[] { 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1f })
            easeTree.AddChild(Speeds($"HornGlow Ease {e:0.##}",
                Mathf.Lerp(1, ActiveSeconds / EasyActiveSeconds, e),
                e < 1 ? 1 / (1 - e) : 400,
                Mathf.Lerp(1, RiseStep / EasyRiseStep, e)), e);
        State(NewLayer("HornGlow Ease"), "Ease", easeTree, new Vector3(300, 0));

        // The first-person tint renders only on the wearer's own client.
        AnimationClip TintEnabled(string name, bool on)
        {
            var clip = new AnimationClip { name = name };
            Set(clip, TintPath, typeof(MeshRenderer), "m_Enabled", on ? 1 : 0);
            AssetDatabase.AddObjectToAsset(clip, ctrl);
            return clip;
        }
        var localSm = NewLayer("HornGlow Local");
        var remote = State(localSm, "Remote", TintEnabled("HornGlow Tint Off", false), new Vector3(300, 0));
        var local = State(localSm, "Local", TintEnabled("HornGlow Tint On", true), new Vector3(300, 80));
        Go(remote, local, 0).AddCondition(AnimatorConditionMode.If, 0, ParamIsLocal);
        Go(local, remote, 0).AddCondition(AnimatorConditionMode.IfNot, 0, ParamIsLocal);

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
        return ctrl;
    }

    // ---- Prefab --------------------------------------------------------------------------

    // A toggle and a hue radial with their saved, synced parameters, in a "Horn" submenu
    // (the AddMenu prefix); VRCFury merges both into the avatar's.
    static (VRCExpressionsMenu, VRCExpressionParameters) BuildMenuAssets()
    {
        var prms = ScriptableObject.CreateInstance<VRCExpressionParameters>();
        prms.parameters = new[]
        {
            new VRCExpressionParameters.Parameter
            {
                name = ParamAlways, valueType = VRCExpressionParameters.ValueType.Bool,
                defaultValue = 0, saved = true, networkSynced = true,
            },
            new VRCExpressionParameters.Parameter
            {
                name = ParamHue, valueType = VRCExpressionParameters.ValueType.Float,
                defaultValue = DefaultHue, saved = true, networkSynced = true,
            },
            new VRCExpressionParameters.Parameter
            {
                name = ParamEase, valueType = VRCExpressionParameters.ValueType.Float,
                defaultValue = 0, saved = true, networkSynced = true,
            },
        };
        var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
        menu.controls.Add(new VRCExpressionsMenu.Control
        {
            name = "Horn Magic",
            type = VRCExpressionsMenu.Control.ControlType.Toggle,
            parameter = new VRCExpressionsMenu.Control.Parameter { name = ParamAlways },
            value = 1,
        });
        menu.controls.Add(new VRCExpressionsMenu.Control
        {
            name = "Horn Hue",
            type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
            subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = ParamHue } },
        });
        menu.controls.Add(new VRCExpressionsMenu.Control
        {
            name = "Horn Ease",
            type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
            subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = ParamEase } },
        });
        return (SaveAsset(menu, Gen + "/HornGlowMenu.asset"), SaveAsset(prms, Gen + "/HornGlowParams.asset"));
    }

    static VRCContactReceiver AddReceiver(GameObject parent, string name, string param, params string[] tags)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var r = go.AddComponent<VRCContactReceiver>();
        r.parameter = param;
        r.collisionTags = tags.ToList();
        r.receiverType = ContactReceiver.ReceiverType.Proximity;
        r.allowSelf = true;
        r.allowOthers = true;
        r.localOnly = false;
        return r;
    }

    static GameObject BuildPrefab(Mesh mesh, Material[] materials, Mesh auraMesh, Material auraMaterial, Mesh tintMesh, Material tintMaterial,
                                  AnimatorController controller, VRCExpressionsMenu menu, VRCExpressionParameters prms)
    {
        var root = new GameObject("HornGlow");
        try
        {
            var horn = new GameObject("Horn");
            horn.transform.SetParent(root.transform, false);
            horn.transform.localRotation = Quaternion.Euler(TiltDegrees, 0, 0);
            horn.AddComponent<MeshFilter>().sharedMesh = mesh;
            // Slot 0 is the opaque horn (replaceable, e.g. by the original gold material);
            // slot 1 draws the same mesh again with the glow tint.
            horn.AddComponent<MeshRenderer>().sharedMaterials = materials;

            var aura = new GameObject("Aura");
            aura.transform.SetParent(horn.transform, false);
            aura.AddComponent<MeshFilter>().sharedMesh = auraMesh;
            var auraRenderer = aura.AddComponent<MeshRenderer>();
            auraRenderer.sharedMaterial = auraMaterial;
            auraRenderer.shadowCastingMode = ShadowCastingMode.Off;
            auraRenderer.receiveShadows = false;

            // Hands and fingers anywhere along the horn, with a little margin.
            var hand = AddReceiver(horn, "TouchHand", ParamHand, "Hand", "Finger");
            hand.shapeType = ContactBase.ShapeType.Capsule;
            hand.radius = 0.035f;
            hand.height = Length + 0.07f;
            hand.position = new Vector3(0, Length * 0.5f, 0);

            // How far along the horn: spheres at the base reaching just past the tip.
            var handDepth = AddReceiver(horn, "TouchHandDepth", ParamHandDepth, "Hand", "Finger");
            handDepth.shapeType = ContactBase.ShapeType.Sphere;
            handDepth.radius = Length + 0.02f;
            var socketDepth = AddReceiver(horn, "TouchSocketDepth", ParamSocketDepth, "SPSLL_Socket_Root", "TPS_Orf_Root");
            socketDepth.shapeType = ContactBase.ShapeType.Sphere;
            socketDepth.radius = Length + 0.02f;

            // SPS sockets (and legacy TPS orifices) send a point contact at their entrance.
            var socket = AddReceiver(horn, "TouchSocket", ParamSocket, "SPSLL_Socket_Root", "TPS_Orf_Root");
            socket.shapeType = ContactBase.ShapeType.Capsule;
            socket.radius = 0.04f;
            socket.height = Length + 0.08f;
            socket.position = new Vector3(0, Length * 0.5f, 0);

            var tint = new GameObject(TintPath);
            tint.transform.SetParent(root.transform, false);
            tint.transform.localPosition = TintCenter;
            tint.AddComponent<MeshFilter>().sharedMesh = tintMesh;
            var tintRenderer = tint.AddComponent<MeshRenderer>();
            tintRenderer.sharedMaterial = tintMaterial;
            tintRenderer.shadowCastingMode = ShadowCastingMode.Off;
            tintRenderer.receiveShadows = false;
            tintRenderer.enabled = false; // the FX enables it for the local player
            // VRChat shrinks the head's children in first person; keep the tint full size.
            var chop = new SerializedObject(root.AddComponent<VRCHeadChop>());
            var bones = chop.FindProperty("targetBones");
            bones.arraySize = 1;
            var bone = bones.GetArrayElementAtIndex(0);
            SerializedProperty Field(string field) => bone.FindPropertyRelative(field) ?? throw new Exception("VRCHeadChop has no " + field);
            Field("transform").objectReferenceValue = tint.transform;
            Field("scaleFactor").floatValue = 1;
            Field("applyCondition").enumValueIndex = 0; // AlwaysApply
            chop.ApplyModifiedPropertiesWithoutUndo();

            var fc = FuryComponents.CreateFullController(root);
            fc.AddController(controller);
            fc.AddMenu(menu, "Horn");
            fc.AddParams(prms);

            return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    // ---- Test scene ----------------------------------------------------------------------

    static Material Simple(string name, Color c)
    {
        var path = Gen + "/" + name + ".mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(m, path); }
        m.color = c;
        return m;
    }

    static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material m)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = m;
        return go;
    }

    // A standalone SPS socket to drop into an avatar's scene for play-mode testing. VRCFury
    // builds sockets outside avatars on entering play mode, and it isn't part of any upload.
    // Its +Z (the socket's front) should face the horn's base.
    static void BuildTestSocketPrefab()
    {
        var go = new GameObject("HornGlow Test Socket");
        try
        {
            var s = FuryComponents.CreateSocket(go);
            s.SetName("HornGlow test socket");
            s.SetMode(com.vrcfury.api.Components.FurySocket.Mode.Hole);
            PrefabUtility.SaveAsPrefabAsset(go, Root + "/Test/HornGlowTestSocket.prefab");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // A stand-in avatar: body, head with the horn, and an SPS socket on a stick in front.
    static void BuildTestScene(GameObject prefab)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var purple = Simple("TestBody", new Color(0.62f, 0.45f, 0.78f));
        var avatar = new GameObject("TestAvatar");
        avatar.AddComponent<Animator>();
        var desc = avatar.AddComponent<VRCAvatarDescriptor>();
        desc.ViewPosition = new Vector3(0, 1.45f, 0.1f);
        // A skinned "Body", like an avatar's face renderer, so the eye-glow bindings resolve.
        var body = Primitive(PrimitiveType.Capsule, "Body", avatar.transform, new Vector3(0, 0.75f, 0), new Vector3(0.4f, 0.75f, 0.4f), purple);
        var bodyMesh = body.GetComponent<MeshFilter>().sharedMesh;
        UnityEngine.Object.DestroyImmediate(body.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(body.GetComponent<MeshFilter>());
        var skin = body.AddComponent<SkinnedMeshRenderer>();
        skin.sharedMesh = bodyMesh;
        skin.sharedMaterial = purple;
        var head = Primitive(PrimitiveType.Sphere, "Head", avatar.transform, new Vector3(0, 1.45f, 0), Vector3.one * 0.25f, purple);
        var horn = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        horn.transform.SetParent(avatar.transform, false);
        // On the forehead, just in front of the top of the head.
        horn.transform.localPosition = new Vector3(0, 1.55f, 0.07f);

        var socket = new GameObject("TestSocket");
        socket.transform.SetParent(avatar.transform, false);
        // Just out of reach of the horn tip; drag it onto the horn in play mode to test.
        socket.transform.localPosition = new Vector3(0, 1.66f, 0.27f);
        socket.transform.localRotation = Quaternion.Euler(180 - TiltDegrees, 0, 0);
        var s = FuryComponents.CreateSocket(socket);
        s.SetName("HornGlow test socket");
        s.SetMode(com.vrcfury.api.Components.FurySocket.Mode.Hole);

        EditorSceneManager.SaveScene(scene, ScenePath);
        Note("Scene " + ScenePath + ": TestAvatar with HornGlow and a VRCFury SPS socket");
    }

    // ---- Verification ----------------------------------------------------------------------

    static bool Verify()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var original = scene.GetRootGameObjects().First(g => g.name == "TestAvatar");
        var clone = UnityEngine.Object.Instantiate(original);
        clone.name = "HornGlow build check";
        var ok = true;
        void Check(bool cond, string what) { Note((cond ? "PASS " : "FAIL ") + what); ok &= cond; }

        var built = VRCBuildPipelineCallbacks.OnPreprocessAvatar(clone);
        Check(built, "avatar build preprocessing (VRCFury) succeeded");
        if (!built) return false;

        var desc = clone.GetComponent<VRCAvatarDescriptor>();
        var fx = desc.baseAnimationLayers.FirstOrDefault(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController as AnimatorController;
        Check(fx != null, "built avatar has an FX controller");
        if (fx == null) return false;
        var layer = fx.layers.FirstOrDefault(l => l.stateMachine.states.Any(s => s.state.name == "Always On"));
        Check(layer != null, "FX contains the HornGlow layer (" + layer?.name + ")");

        // VRCFury may rename the parameter; find it by suffix.
        var always = fx.parameters.FirstOrDefault(p => p.type == AnimatorControllerParameterType.Bool && p.name.EndsWith(ParamAlways))?.name;
        var avatarParam = desc.expressionParameters?.parameters.FirstOrDefault(p => p.name == always);
        Check(always != null && avatarParam != null && avatarParam.saved && avatarParam.networkSynced,
            "toggle parameter '" + always + "' is in FX and the avatar's parameters (saved, synced)");
        bool HasToggle(VRCExpressionsMenu m, int depth) => m != null && depth < 8 && m.controls.Any(c =>
            (c.type == VRCExpressionsMenu.Control.ControlType.Toggle && c.parameter?.name == always && c.name == "Horn Magic")
            || (c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && HasToggle(c.subMenu, depth + 1)));
        Check(always != null && HasToggle(desc.expressionsMenu, 0), "avatar menu has the 'Horn Magic' toggle");
        var hue = fx.parameters.FirstOrDefault(p => p.type == AnimatorControllerParameterType.Float && p.name.EndsWith(ParamHue))?.name;
        var hueParam = desc.expressionParameters?.parameters.FirstOrDefault(p => p.name == hue);
        Check(hueParam != null && hueParam.saved && hueParam.networkSynced && Mathf.Approximately(hueParam.defaultValue, DefaultHue),
            "hue parameter '" + hue + "' is in the avatar's parameters (saved, synced, default " + DefaultHue + ")");
        bool HasRadial(VRCExpressionsMenu m, string param, string label, int depth) => m != null && depth < 8 && m.controls.Any(c =>
            (c.type == VRCExpressionsMenu.Control.ControlType.RadialPuppet && c.subParameters?.FirstOrDefault()?.name == param && c.name == label)
            || (c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && HasRadial(c.subMenu, param, label, depth + 1)));
        Check(hue != null && HasRadial(desc.expressionsMenu, hue, "Horn Hue", 0), "avatar menu has the 'Horn Hue' radial");
        var ease = fx.parameters.FirstOrDefault(p => p.type == AnimatorControllerParameterType.Float && p.name.EndsWith(ParamEase))?.name;
        var easeParam = desc.expressionParameters?.parameters.FirstOrDefault(p => p.name == ease);
        Check(easeParam != null && easeParam.saved && easeParam.networkSynced && easeParam.defaultValue == 0,
            "ease parameter '" + ease + "' is in the avatar's parameters (saved, synced, default 0)");
        Check(ease != null && HasRadial(desc.expressionsMenu, ease, "Horn Ease", 0), "avatar menu has the 'Horn Ease' radial");
        int bits = desc.expressionParameters.parameters.Where(p => p.networkSynced)
            .Sum(p => p.valueType == VRCExpressionParameters.ValueType.Bool ? 1 : 8);
        Note($"test avatar synced parameters: {bits} bits (HornGlow adds 17)");
        // VRCFury may fold the hue layer into a combined Direct blend tree, so search all trees.
        IEnumerable<BlendTree> Trees(Motion m) => m is BlendTree t
            ? new[] { t }.Concat(t.children.SelectMany(c => Trees(c.motion))) : Enumerable.Empty<BlendTree>();
        var hueTree = fx.layers.SelectMany(l => l.stateMachine.states).SelectMany(x => Trees(x.state.motion))
            .FirstOrDefault(t => t.blendType == BlendTreeType.Simple1D && t.blendParameter == hue);
        Check(hueTree != null && hueTree.children.Length == HueSteps + 1 && hueTree.children.All(c => c.motion is AnimationClip clip
                  && AnimationUtility.GetCurveBindings(clip).Count(b => b.propertyName == "material._Hue") == 3
                  && AnimationUtility.GetCurveBindings(clip).Count(b => b.path == "Body" && b.type == typeof(SkinnedMeshRenderer)
                      && b.propertyName.StartsWith("material._Emission2ndColor.")) == 4),
            "FX blends material._Hue on Horn, Aura and the tint, and the eye colour on Body, by '" + hue + "'");
        var eyeAtHue = hueTree?.children.Select(c => (c.threshold, clip: (AnimationClip)c.motion)).Select(x =>
            AnimationUtility.GetEditorCurve(x.clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "material._Emission2ndColor.g")).keys[0].value).ToArray();
        Note("eye emission green by hue step: " + (eyeAtHue == null ? "-" : string.Join(", ", eyeAtHue.Select(g => g.ToString("0.00")))));

        // First-person tint: full size in first person, enabled only for the local player.
        var tint = clone.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == TintPath);
        var chops = clone.GetComponentsInChildren<VRCHeadChop>(true);
        Check(tint != null && !tint.enabled && chops.Length == 1 && chops[0].targetBones.Length == 1
              && chops[0].targetBones[0].transform == tint.transform && chops[0].targetBones[0].scaleFactor == 1,
            "the first-person tint is disabled by default and kept full size by a VRCHeadChop");
        var view = clone.transform.TransformPoint(desc.ViewPosition);
        var viewInTint = tint == null ? Vector3.one : tint.transform.InverseTransformPoint(view);
        Check(tint != null && Mathf.Max(Mathf.Abs(viewInTint.x), Mathf.Abs(viewInTint.y), Mathf.Abs(viewInTint.z)) < TintHalf * 0.8f,
            $"the test avatar's view position is inside the tint box ({viewInTint.x:0.000}, {viewInTint.y:0.000}, {viewInTint.z:0.000})");
        // VRCFury folds the two-state Local layer into its combined Direct blend tree, so look
        // for the tint's m_Enabled clips anywhere: off when IsLocal is 0, on above it (VRCFury puts
        // the bool's on child at the smallest float above 0), and off by
        // default (the renderer is disabled in the prefab).
        var tintPath = tint == null ? null : AnimationUtility.CalculateTransformPath(tint.transform, clone.transform);
        float? EnabledBy(Motion m) => m is AnimationClip c && AnimationUtility.GetCurveBindings(c)
            .FirstOrDefault(b => b.path == tintPath && b.propertyName == "m_Enabled") is var b && b.path != null
            ? AnimationUtility.GetEditorCurve(c, b).keys[0].value : (float?)null;
        var localTree = fx.layers.SelectMany(l => l.stateMachine.states).SelectMany(x => Trees(x.state.motion))
            .FirstOrDefault(t => t.children.Any(c => EnabledBy(c.motion) != null));
        var localClips = localTree?.children.Where(c => EnabledBy(c.motion) != null).Select(c => (c.threshold, on: EnabledBy(c.motion).Value)).ToArray();
        Note("tint m_Enabled driven by '" + localTree?.blendParameter + "' (" + localTree?.blendType + "): "
             + (localClips == null ? "-" : string.Join(", ", localClips.Select(c => $"{c.threshold}->{c.on}"))));
        Check(localTree != null && localTree.blendParameter.EndsWith(ParamIsLocal) && localTree.blendType == BlendTreeType.Simple1D
              && localClips.Any(c => c.threshold == 0 && c.on == 0)
              && localClips.Where(c => c.threshold > 0).All(c => c.threshold <= 1 && c.on == 1) && localClips.Any(c => c.threshold > 0),
            "FX enables the tint renderer only when IsLocal ('" + localTree?.blendParameter + "')");

        var receivers = clone.GetComponentsInChildren<VRCContactReceiver>(true)
            .Where(r => r.name.StartsWith("TouchHand") || r.name.StartsWith("TouchSocket")).ToArray();
        Check(receivers.Length == 4, "all four touch receivers are on the built avatar");
        var fxParams = new HashSet<string>(fx.parameters.Select(p => p.name));
        foreach (var r in receivers)
            Check(fxParams.Contains(r.parameter), r.name + " drives FX parameter '" + r.parameter + "'");

        var senders = clone.GetComponentsInChildren<VRCContactSender>(true);
        var socketTags = new[] { "SPSLL_Socket_Root", "TPS_Orf_Root" };
        Check(senders.Any(s => s.collisionTags.Intersect(socketTags).Any()),
            "the SPS socket emits a tag that TouchSocket receives");

        var clip = layer?.stateMachine.states.First(s => s.state.name == "Off").state.motion as AnimationClip;
        if (clip != null)
            foreach (var r in clone.GetComponentsInChildren<MeshRenderer>(true).Where(m => m.name == "Horn" || m.name == "Aura" || m.name == TintPath))
            {
                var path = AnimationUtility.CalculateTransformPath(r.transform, clone.transform);
                var bound = AnimationUtility.GetCurveBindings(clip).Any(b => b.path == path && b.propertyName == "material._Glow");
                Check(bound, "Off clip animates material._Glow on '" + path + "'");
            }
        var full = layer?.stateMachine.states.FirstOrDefault(s => s.state.name == "Charge 6").state.motion as AnimationClip;
        var eyeGlow = full == null ? null : AnimationUtility.GetEditorCurve(full,
            EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "material._Emission2ndBlend"));
        Check(eyeGlow != null && Mathf.Approximately(eyeGlow.keys[0].value, EyeGlow),
            "Charge 6 animates the eye glow (material._Emission2ndBlend on 'Body') to " + EyeGlow);

        // Drivers and the drain timer must name parameters that exist after VRCFury's renaming.
        var driven = fx.layers.SelectMany(l => l.stateMachine.states).SelectMany(x => x.state.behaviours)
            .OfType<VRCAvatarParameterDriver>().SelectMany(d => d.parameters).Select(p => p.name).Distinct().ToArray();
        Check(driven.Length == 4 && driven.All(fxParams.Contains), "parameter drivers set FX parameters: " + string.Join(", ", driven));
        var animated = fx.animationClips.SelectMany(AnimationUtility.GetCurveBindings)
            .Where(b => b.type == typeof(Animator)).Select(b => b.propertyName).Distinct().ToArray();
        Check(new[] { ParamTimer, ParamChargeTimer, ParamWarmupTimer }.All(t => animated.Any(n => n.EndsWith(t))) && animated.All(fxParams.Contains),
            "animated parameters (drain timer, hue) exist in FX: " + string.Join(", ", animated));

        // The timer states' speed parameters must survive VRCFury's renaming too.
        var speedStates = fx.layers.SelectMany(l => l.stateMachine.states).Select(x => x.state).Where(s => s.speedParameterActive).ToArray();
        Check(speedStates.Length == 4 && speedStates.All(s => fxParams.Contains(s.speedParameter)),
            "timer speeds follow FX parameters: " + string.Join(", ", speedStates.Select(s => s.name + "=" + s.speedParameter)));

        if (layer != null && always != null && ease != null)
            foreach (var (easeValue, fps) in new[] { (0f, 30f), (0f, 12f), (0f, 8f), (1f, 30f), (1f, 8f) })
                ok &= Simulate(clone, fx, receivers, always, ease, easeValue, fps);
        ok &= CheckStereoVariants(Check);
        UnityEngine.Object.DestroyImmediate(clone);
        return ok;
    }

    // Compiles every pass of the horn shaders for Windows D3D11 (VRChat PC) in the stereo
    // variants VR uses: single-pass and single-pass instanced, with and without GPU instancing.
    static bool CheckStereoVariants(Action<bool, string> check)
    {
        var keywordSets = new[]
        {
            new string[0], new[] { "INSTANCING_ON" }, new[] { "UNITY_SINGLE_PASS_STEREO" },
            new[] { "STEREO_INSTANCING_ON" }, new[] { "STEREO_INSTANCING_ON", "INSTANCING_ON" },
        };
        var ok = true;
        foreach (var name in new[] { "HornGlow/Horn", "HornGlow/HornOverlay", "HornGlow/Aura", "HornGlow/Vignette" })
        {
            var data = ShaderUtil.GetShaderData(Shader.Find(name));
            var sub = data.GetSubshader(0);
            var failures = new List<string>();
            int compiled = 0;
            for (int p = 0; p < sub.PassCount; p++)
                foreach (var keywords in keywordSets)
                    foreach (var stage in new[] { UnityEditor.Rendering.ShaderType.Vertex, UnityEditor.Rendering.ShaderType.Fragment })
                    {
                        var info = sub.GetPass(p).CompileVariant(stage, keywords,
                            UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
                        compiled++;
                        if (!info.Success || info.ShaderData == null || info.ShaderData.Length == 0)
                            failures.Add($"pass {p} {stage} [{string.Join(" ", keywords)}]: " +
                                         string.Join("; ", info.Messages.Select(m => m.message)));
                    }
            check(failures.Count == 0, $"{name}: {compiled} D3D11 stereo/instancing variants compile"
                                       + (failures.Count > 0 ? "\n    " + string.Join("\n    ", failures) : ""));
            ok &= failures.Count == 0;
        }
        return ok;
    }

    // Runs the built FX controller by hand through pats, strokes, holding still, draining and
    // the menu toggle, applying parameter drivers the way the client does.
    static bool Simulate(GameObject avatar, AnimatorController fx, VRCContactReceiver[] receivers, string always,
                         string ease, float easeValue, float fps)
    {
        string Param(string receiver) => receivers.First(r => r.name == receiver).parameter;
        string hand = Param("TouchHand"), handDepth = Param("TouchHandDepth");
        string socket = Param("TouchSocket"), socketDepth = Param("TouchSocketDepth");
        var charge = fx.parameters.First(p => p.type == AnimatorControllerParameterType.Int && p.name.EndsWith(ParamCharge)).name;
        var layerIndex = Array.FindIndex(fx.layers, l => l.stateMachine.states.Any(s => s.state.name == "Always On"));
        var stateNames = fx.layers[layerIndex].stateMachine.states.ToDictionary(s => Animator.StringToHash(s.state.name), s => s.state.name);
        var types = fx.parameters.ToDictionary(p => p.name, p => p.type);
        var animator = avatar.GetComponent<Animator>();
        var graph = PlayableGraph.Create("HornGlow sim");
        var ok = true;
        AnimatorControllerPlayable playable = default;
        void ApplyDriver(VRC_AvatarParameterDriver driver, Animator target)
        {
            foreach (var p in driver.parameters)
            {
                if (!types.TryGetValue(p.name, out var type)) { Note("FAIL driver names a missing parameter " + p.name); ok = false; continue; }
                bool add = p.type == VRC_AvatarParameterDriver.ChangeType.Add;
                if (type == AnimatorControllerParameterType.Int)
                    playable.SetInteger(p.name, Mathf.Clamp(Mathf.RoundToInt((add ? playable.GetInteger(p.name) : 0) + p.value), 0, 255));
                else if (type == AnimatorControllerParameterType.Bool) playable.SetBool(p.name, p.value != 0);
                else playable.SetFloat(p.name, (add ? playable.GetFloat(p.name) : 0) + p.value);
            }
        }
        VRC_AvatarParameterDriver.OnApplySettings += ApplyDriver;
        try
        {
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            playable = AnimatorControllerPlayable.Create(graph, fx);
            AnimationPlayableOutput.Create(graph, "fx", animator).SetSourcePlayable(playable);
            graph.Play();
            var timeline = new StringBuilder();
            var steps = new List<(string name, float h, float hd, float s, float sd, bool on, float secs, Func<string, int, bool> expect)>
            {
                ("idle", 0, 0, 0, 0, false, 0.5f, (st, c) => st == "Off" && c == 0),
                ("pat", 0.7f, 0.3f, 0, 0, false, 0.2f, null),
                // One pat stays dark normally; with no warm-up (full ease) it lights the horn.
                ("pat released", 0, 0, 0, 0, false, 2.5f, (st, c) => easeValue > 0 ? c > 0 : st == "Off" && c == 0),
            };
            // Strokes in to the base half and back to the tip half; `half` seconds per half.
            void Strokes(string name, int count, float half, Func<string, int, bool> expect)
            {
                for (int i = 0; i < count; i++)
                {
                    steps.Add((name + " in", 0, 0, 0.9f, 0.7f, false, half, null));
                    steps.Add((name + " out", 0, 0, 0.9f, 0.3f, false, half, i == count - 1 ? expect : null));
                }
            }
            bool easy = easeValue > 0;
            // Fast strokes: dark for the 4 s warm-up, then a charge step per RiseStep.
            if (!easy)
            {
                Strokes("fast", 7, 0.25f, (st, c) => st == "Off" && c == 0);             // 3.5 s
                Strokes("fast", 13, 0.25f, (st, c) => c >= 6 && c <= 8);                 // 10 s
                Strokes("fast", 32, 0.25f, (st, c) => c == MaxCharge && st == "Charge 6"); // 26 s
                steps.AddRange(new (string, float, float, float, float, bool, float, Func<string, int, bool>)[]
                {
                    ("hold still", 0, 0, 0.9f, 0.7f, false, 3.0f, (st, c) => c >= 12 && c <= 15),
                    ("released", 0, 0, 0, 0, false, 8f, (st, c) => st == "Off" && c == 0),
                    ("toggle on", 0, 0, 0, 0, true, 1.0f, (st, c) => st == "Always On"),
                    ("toggle off", 0, 0, 0, 0, false, 2.0f, (st, c) => st == "Off"),
                    ("rub base", 0.7f, 0.7f, 0, 0, false, 0.25f, null),
                    ("rub tip", 0.7f, 0.3f, 0, 0, false, 0.25f, null),
                    ("rub base", 0.7f, 0.7f, 0, 0, false, 0.25f, null),
                    ("rub tip", 0.7f, 0.3f, 0, 0, false, 0.25f, null),
                    ("rubbed", 0.7f, 0.3f, 0, 0, false, 0.5f, (st, c) => c == 0 && st == "Off"),
                    ("rub ends", 0, 0, 0, 0, false, 3f, (st, c) => c == 0 && st == "Off"),
                });
                // Slow strokes (1.5 s per half, like dragging the test socket) build at the same rate
                // as fast ones: about the same charge after 10 s.
                Strokes("slow", 3, 1.5f, null);                                            // 9 s
                steps.Add(("slow in", 0, 0, 0.9f, 0.7f, false, 1f, (st, c) => c >= 6 && c <= 8)); // 10 s
            }
            steps.Add(("rest", 0, 0, 0, 0, false, 12f, (st, c) => st == "Off" && c == 0));
            // Sparse strokes, 2.5 s apart: more than ActiveSeconds, less than EasyActiveSeconds.
            // Normally each pause restarts the warm-up; at full ease they charge to the top.
            for (int i = 0; i < 6; i++)
            {
                steps.Add(("sparse in", 0, 0, 0.9f, 0.7f, false, 0.25f, null));
                steps.Add(("sparse out", 0, 0, 0.9f, 0.3f, false, 0.25f, null));
                steps.Add(("sparse pause", 0, 0, 0, 0, false, 2f, i < 5 ? null
                    : easy ? (st, c) => c >= 12 && st != "Off" : (Func<string, int, bool>)((st, c) => st == "Off" && c == 0)));
            }
            steps.Add(("released", 0, 0, 0, 0, false, 12f, (st, c) => st == "Off" && c == 0));
            float dt = 1f / fps;
            foreach (var step in steps)
            {
                for (float t = 0; t < step.secs; t += dt)
                {
                    playable.SetFloat(hand, step.h);
                    playable.SetFloat(handDepth, step.hd);
                    playable.SetFloat(socket, step.s);
                    playable.SetFloat(socketDepth, step.sd);
                    playable.SetBool(always, step.on);
                    playable.SetFloat(ease, easeValue);
                    graph.Evaluate(dt);
                }
                var cur = playable.GetCurrentAnimatorStateInfo(layerIndex);
                var next = playable.GetNextAnimatorStateInfo(layerIndex);
                var inTransition = playable.IsInTransition(layerIndex);
                string Name(int hash) => stateNames.TryGetValue(hash, out var n) ? n : "?";
                var state = Name(cur.shortNameHash);
                int c = playable.GetInteger(charge);
                var line = $"{step.name,-12} hand={step.h}/{step.hd} socket={step.s}/{step.sd} toggle={step.on} -> charge={c} state={state}"
                           + (inTransition ? $" transitioningTo={Name(next.shortNameHash)}" : "");
                if (step.expect != null)
                {
                    bool good = step.expect(state, c);
                    ok &= good;
                    line += good ? "  ok" : "  UNEXPECTED";
                }
                timeline.AppendLine(line);
            }
            Note($"Simulated timeline (ease {easeValue}, {fps} fps):\n" + timeline);
            Note((ok ? "PASS" : "FAIL") + $" (ease {easeValue}, {fps} fps) pat, warm-up, timed charge (fast and slow strokes), hold, drain and menu toggle sequence");
        }
        finally
        {
            VRC_AvatarParameterDriver.OnApplySettings -= ApplyDriver;
            graph.Destroy();
        }
        return ok;
    }

    // ---- Previews --------------------------------------------------------------------------

    static void RenderPreviews(GameObject prefab, string outDir)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Flat;
        var sh = new SphericalHarmonicsL2();
        sh.AddAmbientLight(new Color(0.45f, 0.45f, 0.5f));
        RenderSettings.ambientProbe = sh;
        var light = new GameObject("Sun").AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.0f;
        light.transform.rotation = Quaternion.Euler(40, -60, 0);

        var headMat = Simple("PreviewHead", new Color(0.62f, 0.5f, 0.85f));
        var glows = new[] { 0f, 0.4f, 0.7f, 1f };
        var eyes = new Vector3[glows.Length];
        for (int i = 0; i < glows.Length; i++)
        {
            var at = new Vector3(0, 0, -0.33f + i * 0.22f);
            Primitive(PrimitiveType.Sphere, "Head" + i, null, at, Vector3.one * 0.16f, headMat);
            var horn = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            horn.transform.position = at + new Vector3(0, 0.065f, 0.035f);
            var mpb = new MaterialPropertyBlock();
            mpb.SetFloat("_Glow", glows[i]);
            foreach (var r in horn.GetComponentsInChildren<MeshRenderer>()) r.SetPropertyBlock(mpb);
            // The local player's view: the tint enabled, the camera where the test avatar's eyes
            // are relative to the horn.
            horn.transform.Find(TintPath).GetComponent<MeshRenderer>().enabled = true;
            eyes[i] = horn.transform.position + new Vector3(0, -0.1f, 0.03f);
        }

        var cam = new GameObject("Cam").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.93f, 0.9f, 0.72f);
        cam.fieldOfView = 30;
        cam.nearClipPlane = 0.01f;
        cam.allowHDR = false;

        Shoot(cam, new Vector3(1.1f, 0.18f, 0), new Vector3(0, 0.08f, 0), 1600, 600, Path.Combine(outDir, "glow-steps.png"));
        cam.fieldOfView = 12;
        Shoot(cam, new Vector3(0.6f, 0.22f, 0.62f), new Vector3(0, 0.11f, 0.4f), 800, 800, Path.Combine(outDir, "glow-full-closeup.png"));
        // Stereo pair (64 mm apart, parallel), for checking that the ray-marched aura and its
        // depth line up between the eyes.
        Shoot(cam, new Vector3(0.6f, 0.22f, 0.62f), new Vector3(0, 0.11f, 0.4f), 800, 800, Path.Combine(outDir, "stereo-left.png"), -0.032f);
        Shoot(cam, new Vector3(0.6f, 0.22f, 0.62f), new Vector3(0, 0.11f, 0.4f), 800, 800, Path.Combine(outDir, "stereo-right.png"), 0.032f);
        // From inside the aura's box, as when someone leans in close.
        cam.fieldOfView = 70;
        cam.nearClipPlane = 0.005f;
        Shoot(cam, new Vector3(0.045f, 0.13f, 0.43f), new Vector3(0, 0.11f, 0.42f), 800, 800, Path.Combine(outDir, "inside-aura.png"));
        cam.nearClipPlane = 0.01f;
        cam.fieldOfView = 30;
        ShootMirror(cam, new Vector3(1.1f, 0.18f, 0), new Vector3(-0.6f, 0.08f, 0), -0.3f, 1600, 600, Path.Combine(outDir, "mirror.png"));
        // First person at glow 0.4 and 1 (16:9, 90 degrees, as on desktop); other cameras in
        // the scene above don't show the tint because they are outside its box.
        cam.fieldOfView = 60;
        cam.nearClipPlane = 0.01f;
        foreach (int i in new[] { 1, 3 })
            Shoot(cam, eyes[i], eyes[i] + Vector3.forward, 1280, 720, Path.Combine(outDir, $"first-person-glow{glows[i]:0.0}.png"));
        Note("Previews written to " + outDir + " (glow 0, 0.4, 0.7, 1 left to right; stereo pair, inside the aura, mirror, first person)");
    }

    // A VRChat-style mirror at x = mirrorX facing +X: reflected view, inverted culling and an
    // oblique near plane on the mirror (which changes the projection's depth range).
    static void ShootMirror(Camera cam, Vector3 pos, Vector3 target, float mirrorX, int w, int h, string file)
    {
        cam.transform.position = pos;
        cam.transform.LookAt(target);
        cam.aspect = (float)w / h;
        var reflect = Matrix4x4.identity;
        reflect.m00 = -1;
        reflect.m03 = 2 * mirrorX;
        cam.worldToCameraMatrix = cam.worldToCameraMatrix * reflect;
        var m = cam.worldToCameraMatrix;
        var cpos = m.MultiplyPoint(new Vector3(mirrorX, 0, 0));
        var cnormal = m.MultiplyVector(Vector3.right).normalized;
        cam.projectionMatrix = cam.CalculateObliqueMatrix(new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal)));
        GL.invertCulling = true;
        try { Shoot(cam, null, null, w, h, file); }
        finally
        {
            GL.invertCulling = false;
            cam.ResetWorldToCameraMatrix();
            cam.ResetProjectionMatrix();
            cam.ResetAspect();
        }
    }

    static void Shoot(Camera cam, Vector3? pos, Vector3? target, int w, int h, string file, float eyeOffset = 0)
    {
        if (pos.HasValue)
        {
            cam.transform.position = pos.Value;
            cam.transform.LookAt(target.Value);
            cam.transform.position += cam.transform.right * eyeOffset;
        }
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        var prev = RenderTexture.active;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            File.WriteAllBytes(file, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
        finally
        {
            cam.targetTexture = null;
            RenderTexture.active = prev;
            rt.Release();
        }
    }
}
