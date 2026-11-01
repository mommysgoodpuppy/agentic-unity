"""Edge-loop reduction of a game mesh using its authored quad topology (Blender, headless).

    blender -b --factory-startup --python loop_reduce.py -- \
        --game GAME.fbx --quad QUAD.fbx --object Body --out OUT_DIR [--tolerance 0.0004] [--uv-texels 1.5]

The game mesh is a triangulated copy of the authored quad mesh with identical vertex order. Quads are
rebuilt by dissolving each quad's diagonal(s). Closed edge loops (rings) are then scored and the
cheapest dissolved, in passes, never two neighbouring loops in one pass. A loop vertex v between its
two cross neighbours a and b disappears onto the segment a-b; its cost is the largest of
  - distance from v to the segment, in the basis and in every shape key, as a fraction of the mesh height,
  - bone-weight change against the interpolated weights,
  - UV change against the interpolated UV, in texels.
Loops on UV seams, material borders, mesh borders or with shape-key-only motion beyond the tolerance stay.

Writes OUT_DIR/<object>.json: kept original vertex indices and the new triangles as original vertex
indices plus per-corner UV0 (so Unity can pick the right split vertex), and OUT_DIR/<object>.blend.
Nothing here edits the input files.
"""
import argparse
import json
import os
import sys

import bmesh
import bpy
from mathutils import Vector
from mathutils.geometry import intersect_point_line


def args():
    p = argparse.ArgumentParser()
    p.add_argument("--game", required=True)
    p.add_argument("--quad", required=True)
    p.add_argument("--object", required=True, help="mesh object in --game")
    p.add_argument("--quad-object", help="mesh object(s) in --quad, comma-separated parts joined (default: --object)")
    p.add_argument("--out", required=True)
    p.add_argument("--tolerance", type=float, default=0.0004, help="max surface deviation, fraction of mesh height")
    p.add_argument("--weight-tolerance", type=float, default=0.08, help="max summed bone-weight change")
    p.add_argument("--uv-texels", type=float, default=1.5)
    p.add_argument("--texture-size", type=int, default=2048)
    p.add_argument("--max-passes", type=int, default=12, help="passes per tolerance level")
    p.add_argument("--target-triangles", type=int, default=0,
                   help="raise the tolerance in steps (cheapest loops first) until the mesh has at most this many triangles")
    p.add_argument("--max-tolerance", type=float, default=0.008, help="upper bound for --target-triangles (fraction of height)")
    p.add_argument("--region-profile", help="JSON: {\"default\": 1, \"groups\": [[\"Hand_*\", 0.3], ...]} tolerance "
                   "multipliers by vertex group (glob); <1 keeps an area at higher quality")
    p.add_argument("--hard-weight-tolerance", type=float, default=0.12,
                   help="absolute bone-weight error cap, never relaxed by target/region multipliers")
    p.add_argument("--min-normal-dot", type=float, default=0.2,
                   help="hard minimum dot against original surface normals for either new quad diagonal")
    p.add_argument("--protect-shapes", default="", help="comma-separated shape keys held to --tolerance (default: all; 'none': none)")
    p.add_argument("--other-shape-factor", type=float, default=0.0,
                   help="with --protect-shapes: tolerance multiplier for the other shape keys while choosing loops "
                        "(0 = they don't limit the choice; all shapes are re-fitted afterwards either way)")
    p.add_argument("--protected-shape-factor", type=float, default=1.0,
                   help="tolerance multiplier for protected shape keys while choosing loops")
    p.add_argument("--refit-weight", type=float, default=1.0,
                   help="re-fit: how strongly kept vertices hold their own original shape offsets")
    return p.parse_args(sys.argv[sys.argv.index("--") + 1:])


def import_mesh(path, name):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    new = [o for o in set(bpy.data.objects) - before]
    names = [n.strip() for n in name.split(",")]
    objs = []
    for n in names:  # exact name first (an FBX can hold "Hair" and "Hair.001")
        meshes = [o for o in new if o.type == "MESH"]
        o = next((o for o in meshes if o.name == n), None) or next((o for o in meshes if o.name.split(".")[0] == n), None)
        if o is None:
            sys.exit(f"no mesh {n} in {path}")
        objs.append(o)
    for o in new:
        if o not in objs and o.type == "MESH":
            bpy.data.objects.remove(o)
    return objs


def quad_faces_on_game(game, quads, eps):
    """Authored faces as ordered game vertex indices. The game mesh must be the authored part(s) with the
    same vertices: either in the same order, or the parts concatenated in some order (found greedily by
    world position)."""
    gm, gw = game.data, game.matrix_world
    if len(quads) == 1:  # same mesh, same vertex order (local coordinates; the files' object transforms may differ)
        qm = quads[0].data
        if len(qm.vertices) == len(gm.vertices) and all((x.co - y.co).length <= eps for x, y in zip(gm.vertices, qm.vertices)):
            return [list(p.vertices) for p in qm.polygons], "index"
    eps = eps * max(gw.to_scale())
    gpos = [gw @ v.co for v in gm.vertices]
    parts = {q.name: (q, [q.matrix_world @ v.co for v in q.data.vertices]) for q in quads}
    if sum(len(p) for _, p in parts.values()) != len(gpos):
        sys.exit(f"vertex counts differ: game {len(gpos)}, authored {sum(len(p) for _, p in parts.values())}")
    polys, off, order = [], 0, []
    while parts:
        hit = next((n for n, (q, pos) in parts.items()
                    if all((gpos[off + k] - pos[k]).length <= eps for k in range(len(pos)))), None)
        if hit is None:
            sys.exit(f"no authored part continues the game mesh at vertex {off}")
        q, pos = parts.pop(hit)
        polys += [[off + i for i in p.vertices] for p in q.data.polygons]
        order.append(hit)
        off += len(pos)
    return polys, "parts in order " + ", ".join(order)


def rebuild_quads(bm, polys):
    """polys: authored faces as ordered game vertex indices."""
    faces = [frozenset(p) for p in polys]
    boundary = set()
    for p in polys:
        for k in range(len(p)):
            boundary.add(frozenset((p[k], p[(k + 1) % len(p)])))
    bm.verts.ensure_lookup_table()
    by_key = {frozenset((e.verts[0].index, e.verts[1].index)): e for e in bm.edges}
    internal = []
    for fs in faces:
        if len(fs) < 4:
            continue
        vs = sorted(fs)
        for i in range(len(vs)):
            for j in range(i + 1, len(vs)):
                k = frozenset((vs[i], vs[j]))
                if k not in boundary and k in by_key:
                    internal.append(by_key[k])
    bmesh.ops.dissolve_edges(bm, edges=list(set(internal)), use_verts=False, use_face_split=False)
    return len(faces)


def is_uv_seam(e, uvl):
    if uvl is None or len(e.link_faces) != 2:
        return False
    f1, f2 = e.link_faces
    for v in e.verts:
        u1 = next(l[uvl].uv for l in f1.loops if l.vert is v)
        u2 = next(l[uvl].uv for l in f2.loops if l.vert is v)
        if (u1 - u2).length > 1e-6:
            return True
    return False


def regular(v):
    return len(v.link_edges) == 4 and len(v.link_faces) == 4 and all(len(f.verts) == 4 for f in v.link_faces)


def edge_loops(bm, closed_only=False):
    """Edge loops through regular (4-valent, all-quad) vertices: list of (interior verts, edges).
    Open loops run until a pole or border; only their interior vertices are removed."""
    seen, loops = set(), []

    def walk(e, v):
        verts, edges = [], []
        while regular(v):
            face_edges = {x for f in e.link_faces for x in f.edges}
            nxt = [x for x in v.link_edges if x is not e and x not in face_edges]
            if len(nxt) != 1:
                break
            verts.append(v)
            e = nxt[0]
            if e in edges or len(edges) > 4000:
                return verts, edges, True
            edges.append(e)
            v = e.other_vert(v)
        return verts, edges, False

    for e0 in bm.edges:
        if e0.index in seen:
            continue
        va, ea, closed = walk(e0, e0.verts[1])
        if closed:
            verts, edges = va, [e0] + ea[:-1] if ea and ea[-1] is e0 else [e0] + ea
        else:
            vb, eb, _ = walk(e0, e0.verts[0])
            verts, edges = vb[::-1] + va, eb[::-1] + [e0] + ea
        for x in edges:
            seen.add(x.index)
        if closed or not closed_only:
            if len(verts) >= 2:
                loops.append((verts, edges))
    return loops


def refit_shapes(mesh, kept, removed, lam, names, protect):
    """Least-squares re-fit of shape offsets on the kept vertices. Each removed vertex is a fixed blend of
    kept vertices (chained through later passes); for every shape, find kept offsets x minimising
    |C x - D_removed|^2 + lam |x - D_kept|^2. Returns per-shape errors (mesh units) and changed offsets."""
    import numpy as np
    memo = {}

    def combo(i):
        if i in memo:
            return memo[i]
        if i not in removed:
            memo[i] = {i: 1.0}
            return memo[i]
        a, b, t = removed[i]
        out = {}
        for j, w in combo(a).items():
            out[j] = out.get(j, 0.0) + (1 - t) * w
        for j, w in combo(b).items():
            out[j] = out.get(j, 0.0) + t * w
        memo[i] = out
        return out

    col = {v: k for k, v in enumerate(kept)}
    rem = sorted(removed)
    rows, cols, vals = [], [], []
    for r, i in enumerate(rem):
        for j, w in combo(i).items():
            if j not in col:
                print("UNMAPPED", j, i, removed.get(j)); raise SystemExit(1)
            rows.append(r); cols.append(col[j]); vals.append(w)
    rows, cols, vals = np.array(rows), np.array(cols), np.array(vals)[:, None]
    nk, nr = len(kept), len(rem)
    n = len(mesh.vertices)
    basis = np.empty(n * 3); mesh.vertices.foreach_get("co", basis); basis = basis.reshape(n, 3)

    def C(x):
        out = np.zeros((nr, 3)); np.add.at(out, rows, vals * x[cols]); return out

    def Ct(y):
        out = np.zeros((nk, 3)); np.add.at(out, cols, vals * y[rows]); return out

    errors, deltas = [], {}
    keys = mesh.shape_keys.key_blocks[1:] if mesh.shape_keys else []
    for key in keys:
        co = np.empty(n * 3); key.data.foreach_get("co", co)
        D = co.reshape(n, 3) - basis
        Dk, Dr = D[kept], D[rem]
        before = float(np.linalg.norm(C(Dk) - Dr, axis=1).max()) if nr else 0.0
        if before < 1e-7:
            errors.append({"shape": key.name, "protected": key.name in protect, "before": before, "after": before, "keptMoved": 0.0})
            continue
        b = Ct(Dr) + lam * Dk
        x = Dk.copy()
        r = b - (Ct(C(x)) + lam * x); p = r.copy(); rs = (r * r).sum()
        for _ in range(200):
            Ap = Ct(C(p)) + lam * p
            alpha = rs / max((p * Ap).sum(), 1e-30)
            x += alpha * p; r -= alpha * Ap
            rs_new = (r * r).sum()
            if rs_new < 1e-20:
                break
            p = r + (rs_new / rs) * p; rs = rs_new
        after = max(float(np.linalg.norm(C(x) - Dr, axis=1).max()), float(np.linalg.norm(x - Dk, axis=1).max()))
        if after >= before:  # never make a shape worse
            x, after = Dk, before
        moved = np.linalg.norm(x - Dk, axis=1)
        errors.append({"shape": key.name, "protected": key.name in protect, "before": before, "after": after,
                       "keptMoved": float(moved.max())})
        changed = np.nonzero(moved > 1e-7)[0]
        if len(changed):
            deltas[key.name] = [[int(kept[k])] + [float(c) for c in x[k]] for k in changed]
    return {"errors": errors, "deltas": deltas}


def main():
    a = args()
    os.makedirs(a.out, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    game, = import_mesh(a.game, a.object)
    quads = import_mesh(a.quad, a.quad_object or a.object)
    gm = game.data
    height = max(v.co.z for v in gm.vertices) - min(v.co.z for v in gm.vertices)
    height = max(height, max(v.co.y for v in gm.vertices) - min(v.co.y for v in gm.vertices))
    tol = a.tolerance * height
    uv_tol = a.uv_texels / a.texture_size
    tris_before = sum(len(p.vertices) - 2 for p in gm.polygons)

    bm = bmesh.new()
    bm.from_mesh(gm)
    orig = bm.verts.layers.int.new("orig")
    for v in bm.verts:
        v[orig] = v.index
    polys, matched_by = quad_faces_on_game(game, quads, height * 1e-6)
    authored_face_vertices = {frozenset(p) for p in polys}
    print(f"authored faces matched by {matched_by}: {len(polys)}")
    n_quads = rebuild_quads(bm, polys)
    names = [k.name for k in gm.shape_keys.key_blocks[1:]] if gm.shape_keys else []
    none = a.protect_shapes.strip().lower() == "none"   # nothing protected: every shape only re-fitted
    protect = set() if none else {x.strip() for x in a.protect_shapes.split(",") if x.strip()}
    missing = protect - set(names)
    if missing:
        sys.exit(f"--protect-shapes not on {a.object}: {sorted(missing)}")
    # (layer, tolerance multiplier); shapes that move nothing are skipped.
    shapes = [(bm.verts.layers.shape[n], a.protected_shape_factor if (not protect and not none) or n in protect else a.other_shape_factor)
              for n in names]
    shapes = [(layer, f) for layer, f in shapes if f > 0]
    deform = bm.verts.layers.deform.active
    uvl = bm.loops.layers.uv.active

    from collections import Counter

    def side_uvs(v, n):
        """UV of v -> UV of neighbour n, over the faces on edge v-n (per UV island)."""
        e = next(x for x in v.link_edges if x.other_vert(v) is n)
        out = {}
        for f in e.link_faces:
            uv_v = next(l[uvl].uv for l in f.loops if l.vert is v)
            uv_n = next(l[uvl].uv for l in f.loops if l.vert is n)
            out[tuple(round(c, 5) for c in uv_v)] = uv_n.copy()
        return out

    def loop_cost(verts, edges):
        """(cost, cross-neighbour ids, reason) where cost <= 1 is within every tolerance."""
        if len({f.material_index for e in edges for f in e.link_faces}) > 1:
            return 9, set(), "material border"
        if any(e.is_boundary or not e.smooth or e.seam or is_uv_seam(e, uvl) for e in edges):
            return 9, set(), "loop runs along a seam/border"
        # Removing a ring joins each pair of incident quads. A small point-to-segment
        # error alone can still turn one resulting triangle inside out at a knuckle.
        # Require both possible diagonals to face the original surface; the global
        # target must never relax this gate.
        for e in edges:
            ends = set(e.verts)
            outer = []
            for face in e.link_faces:
                opposite = [(l.vert, l.link_loop_next.vert) for l in face.loops
                            if l.vert not in ends and l.link_loop_next.vert not in ends]
                if len(opposite) != 1:
                    return 9, set(), "merged quad topology"
                outer.extend(opposite[0])
            if len(outer) != 4 or len(set(outer)) != 4:
                return 9, set(), "merged quad topology"
            reference = sum((gm.vertices[v[orig]].normal for v in outer), Vector()).normalized()
            for i, j, k in ((0, 1, 2), (0, 2, 3), (0, 1, 3), (1, 2, 3)):
                normal = (outer[j].co - outer[i].co).cross(outer[k].co - outer[i].co)
                if normal.length_squared < 1e-20 or normal.normalized().dot(reference) < a.min_normal_dot:
                    return 9, set(), "triangle normal flip"
        edge_set, cost, cross, parts = set(edges), 0.0, set(), Counter()
        for v in verts:
            nb = [e.other_vert(v) for e in v.link_edges if e not in edge_set]
            if len(nb) != 2:
                return 9, set(), "topology"
            pa, pb = nb
            cross.update((pa.index, pb.index))
            _, t = intersect_point_line(v.co, pa.co, pb.co)
            t = min(max(t, 0.0), 1.0)
            g = (v.co - pa.co.lerp(pb.co, t)).length / tol
            sh = max([(v[s] - pa[s].lerp(pb[s], t)).length / (tol * f) for s, f in shapes] or [0])
            w = 0.0
            if deform is not None:
                wv, wa, wb = v[deform], pa[deform], pb[deform]
                w = sum(abs(wv.get(k, 0.0) - (wa.get(k, 0.0) * (1 - t) + wb.get(k, 0.0) * t))
                        for k in set(wv.keys()) | set(wa.keys()) | set(wb.keys())) / a.weight_tolerance
            u = 0.0
            if uvl is not None:
                sa, sb = side_uvs(v, pa), side_uvs(v, pb)
                if set(sa) != set(sb):
                    return 9, set(), "uv island topology"
                for k in sa:
                    u = max(u, (Vector(k) - sa[k].lerp(sb[k], t)).length / uv_tol)
            for name, val in (("geometry", g), ("shape keys", sh), ("weights", w), ("uv", u)):
                parts[name] = max(parts[name], val)
            cost = max(cost, max(g, sh, w, u) / (level * region[v[orig]]),
                       w * a.weight_tolerance / a.hard_weight_tolerance)
        return cost, cross, max(parts, key=parts.get)

    # Region multipliers per original vertex: weight-blended over its vertex groups.
    region = [1.0] * len(gm.vertices)
    vertex_region = ["other"] * len(gm.vertices)
    retain_floors = {}
    if a.region_profile:
        import fnmatch
        import math
        prof = json.load(open(a.region_profile))
        default = float(prof.get("default", 1.0))
        names_by_index = {g.index: g.name for g in game.vertex_groups}

        def mult(name):
            return next((float(f) for pat, f in prof.get("groups", []) if fnmatch.fnmatchcase(name, pat)), default)
        for v in gm.vertices:
            ws = [(g.weight, mult(names_by_index[g.group])) for g in v.groups if g.weight > 0]
            tw = sum(w for w, _ in ws)
            region[v.index] = sum(w * f for w, f in ws) / tw if tw > 0 else default
        # Region floors are vertex-retention budgets. Unlike tolerance multipliers,
        # these cannot disappear when the global target raises the tolerance.
        regions = prof.get("regions", [])
        joint_centers = {}
        for spec in regions:
            joint_centers[spec["name"]] = [game.matrix_world.inverted() @ arm.matrix_world @ bone.head_local
                for arm in bpy.data.objects if arm.type == "ARMATURE" for bone in arm.data.bones
                if any(fnmatch.fnmatchcase(bone.name, pat) for pat in spec.get("joints", []))]
        # Lock whole influenced areas (e.g. nails and fingertip joints), with an optional
        # neighbouring ring so dissolving the next loop cannot retriangulate their faces.
        influenced = {}
        adjacency = [set() for _ in gm.vertices]
        for polygon in gm.polygons:
            for i in polygon.vertices:
                adjacency[i].update(polygon.vertices)
        for spec in regions:
            ids = {v.index for v in gm.vertices if any(
                g.weight >= spec.get("minWeight", 0.01)
                and any(fnmatch.fnmatchcase(names_by_index[g.group], pat) for pat in spec.get("anyGroups", []))
                for g in v.groups)}
            if spec.get("sourceFolds", False):
                from mathutils.kdtree import KDTree
                tree = KDTree(len(gm.vertices))
                for v in gm.vertices:
                    tree.insert(v.co, v.index)
                tree.balance()
                folds = set()
                for polygon in gm.polygons:
                    reference = sum((gm.vertices[i].normal for i in polygon.vertices), Vector()).normalized()
                    if polygon.normal.dot(reference) < 0:
                        folds.update(polygon.vertices)
                radius = height * spec.get("radius", 0.002)
                ids |= {j for i in folds for _, j, _ in tree.find_range(gm.vertices[i].co, radius)}
                # A covering triangle can pass over the fold while all three corners
                # lie outside the radius. Protect those faces, not only nearby points.
                from mathutils.bvhtree import BVHTree
                surface = BVHTree.FromPolygons([v.co for v in gm.vertices],
                                              [list(p.vertices) for p in gm.polygons], all_triangles=True)
                for i in folds:
                    for _, _, face_index, _ in surface.find_nearest_range(gm.vertices[i].co, radius):
                        ids.update(gm.polygons[face_index].vertices)
            for _ in range(int(spec.get("grow", 0))):
                ids |= {j for i in ids for j in adjacency[i]}
            influenced[spec["name"]] = ids
        for v in gm.vertices:
            dominant = names_by_index[max(v.groups, key=lambda g: g.weight).group] if v.groups else ""
            for spec in regions:
                matched = any(fnmatch.fnmatchcase(dominant, pat) for pat in spec.get("groups", []))
                matched |= v.index in influenced[spec["name"]]
                matched |= any((v.co - center).length <= height * spec.get("radius", 0.04) for center in joint_centers[spec["name"]])
                if matched:
                    vertex_region[v.index] = spec["name"]
                    region[v.index] = min(region[v.index], float(spec.get("multiplier", region[v.index])))
                    break
        before_regions = Counter(vertex_region)
        retain_floors = {s["name"]: int(math.ceil(before_regions[s["name"]] * s.get("retain", 0))) for s in regions}
        print(f"region vertex floors: {retain_floors}; original {dict(before_regions)}", flush=True)
        print(f"region profile {a.region_profile}: multipliers {min(region):.2f}..{max(region):.2f}")

    before_regions = Counter(vertex_region)
    remaining_regions = before_regions.copy()

    def triangles():
        return sum(len(f.verts) - 2 for f in bm.faces)

    removed_loops, removed_verts, removed = 0, 0, {}
    level, max_level = 1.0, max(a.max_tolerance / a.tolerance, 1.0)
    while True:
        for pass_no in range(a.max_passes):
            if a.target_triangles and triangles() <= a.target_triangles:
                break
            bm.verts.ensure_lookup_table(); bm.edges.ensure_lookup_table(); bm.faces.ensure_lookup_table()
            candidates, why = [], Counter()
            all_loops = edge_loops(bm)
            for verts, edges in all_loops:
                cost, cross, reason = loop_cost(verts, edges)
                if cost <= 1.0:
                    candidates.append((cost, verts, edges, cross))
                else:
                    why[reason] += 1
            candidates.sort(key=lambda x: x[0])
            taken, touched = [], set()
            for cost, verts, edges, cross in candidates:
                ids = {v.index for v in verts}
                if ids & touched or cross & touched:
                    continue
                loss = Counter(vertex_region[v[orig]] for v in verts)
                if any(remaining_regions[r] - count < retain_floors.get(r, 0) for r, count in loss.items()):
                    continue
                remaining_regions.subtract(loss)
                taken.append((cost, verts, edges))
                touched |= ids | cross
            print(f"level {level:.2f} pass {pass_no + 1}: {len(all_loops)} loops, {len(candidates)} within tolerance, "
                  f"dissolving {len(taken)}; over/blocked by {dict(why)}", flush=True)
            if not taken:
                break
            for _, verts, edges in taken:  # remember each removed vertex as a blend of its two cross neighbours
                edge_set = set(edges)
                for v in verts:
                    pa, pb = [e.other_vert(v) for e in v.link_edges if e not in edge_set]
                    _, t = intersect_point_line(v.co, pa.co, pb.co)
                    removed[v[orig]] = (pa[orig], pb[orig], min(max(t, 0.0), 1.0))
            dissolve = [e for _, _, edges in taken for e in edges]
            nv = sum(len(v) for _, v, _ in taken)
            # Dissolve the loops' edges, then only their recorded vertices (use_verts=True would also take poles
            # left with two edges, which have no recorded blend for the shape re-fit).
            loop_verts = list(dict.fromkeys(v for _, verts, _ in taken for v in verts))
            bmesh.ops.dissolve_edges(bm, edges=dissolve, use_verts=False, use_face_split=False)
            bmesh.ops.dissolve_verts(bm, verts=[v for v in loop_verts if v.is_valid], use_face_split=False, use_boundary_tear=False)
            removed_loops += len(taken); removed_verts += nv
        done = not a.target_triangles or triangles() <= a.target_triangles
        if done or level >= max_level:
            if not done:
                print(f"target {a.target_triangles} not reached at the maximum tolerance; {triangles()} triangles")
            break
        level = min(level * 1.35, max_level)
    print(f"final tolerance level {level:.2f} (= {a.tolerance * level:.5f} of height)")

    # Preserve the game's original triangle diagonals wherever a face was untouched.
    # Retaining every hand vertex must not silently retriangulate the hand's quads.
    original_triangles = {}
    by_vertex = {}
    for p in gm.polygons:
        if len(p.vertices) != 3:
            continue
        ids = list(p.vertices)
        key = frozenset(ids)
        original_triangles[key] = (ids, [list(gm.uv_layers.active.data[i].uv) for i in p.loop_indices] if uvl else [], p.material_index)
        for i in ids:
            by_vertex.setdefault(i, set()).add(key)
    preserved = {}
    preserved_layer = bm.faces.layers.int.new("preserved_game_face")
    for f in list(bm.faces):
        ids = frozenset(v[orig] for v in f.verts)
        # A merged quad can coincidentally contain two old triangles without
        # those triangles covering its boundary. Only untouched authored faces
        # may reuse their old diagonal; otherwise this can duplicate a neighbour's
        # triangle and leave a hole in the newly merged face.
        if ids not in authored_face_vertices:
            continue
        candidates = {key for i in ids for key in by_vertex.get(i, set()) if key <= ids}
        if len(candidates) == len(ids) - 2 and all(original_triangles[key][2] == f.material_index for key in candidates):
            tag = len(preserved) + 1
            preserved[tag] = [original_triangles[key] for key in sorted(candidates, key=lambda k: sorted(k))]
            f[preserved_layer] = tag
            if len(ids) == 4 and len(candidates) == 2:
                diagonal = set.intersection(*(set(key) for key in candidates))
                if len(diagonal) == 2:
                    ends = [v for v in f.verts if v[orig] in diagonal]
                    split, _ = bmesh.utils.face_split(f, ends[0], ends[1])
                    split[preserved_layer] = tag
    tri = bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method="BEAUTY", ngon_method="BEAUTY")
    bm.faces.ensure_lookup_table()
    tris, uvs, mats = [], [], []
    emitted = set()
    for f in bm.faces:
        tag = f[preserved_layer]
        if tag:
            if tag not in emitted:
                for ids, corners, material in preserved[tag]:
                    tris.append(ids); uvs.append(corners); mats.append(material)
                emitted.add(tag)
            continue
        tris.append([l.vert[orig] for l in f.loops])
        uvs.append([list(l[uvl].uv) for l in f.loops] if uvl else [])
        mats.append(f.material_index)
    if len(tris) != len(bm.faces):
        raise RuntimeError("Preserved triangulation changed the triangle count")
    if len({frozenset(t) for t in tris}) != len(tris):
        raise RuntimeError("Reduction introduced duplicate triangles")
    kept = sorted({i for t in tris for i in t})
    lost = set(range(len(gm.vertices))) - set(kept) - set(removed)
    print(f"vertices gone without a recorded blend: {len(lost)}; kept but unused: {len({v[orig] for v in bm.verts}) - len(kept)}")
    shape_fit = refit_shapes(gm, kept, removed, a.refit_weight, names, protect)
    unit_mm = game.matrix_world.to_scale()[0] * 1000.0
    for row in shape_fit["errors"]:
        for k in ("before", "after", "keptMoved"):
            row[k + "Mm"] = round(row.pop(k) * unit_mm, 3)
    worst = sorted(shape_fit["errors"], key=lambda r: -r["afterMm"])[:8]
    print("shape re-fit, worst after (mm):", ", ".join(f"{r['shape']} {r['beforeMm']}->{r['afterMm']}" for r in worst))
    report = {"object": a.object, "protectedShapes": sorted(protect), "otherShapeFactor": a.other_shape_factor, "game": os.path.abspath(a.game), "quad": os.path.abspath(a.quad),
              "tolerance": a.tolerance, "toleranceUnits": tol, "weightTolerance": a.weight_tolerance, "uvTexels": a.uv_texels,
              "trianglesBefore": tris_before, "trianglesAfter": len(tris), "verticesBefore": len(gm.vertices),
              "verticesAfter": len(kept), "quadsRebuilt": n_quads, "loopsRemoved": removed_loops,
              "shapeKeys": len(names), "unitMm": unit_mm, "heightUnits": height,
              "shapeErrors": shape_fit["errors"], "shapeDeltas": shape_fit["deltas"],
              "regionVerticesBefore": dict(before_regions), "regionVerticesAfter": dict(Counter(vertex_region[i] for i in kept)),
              "regionRetentionFloors": retain_floors,
              "regionTriangleCounts": dict(Counter(Counter(vertex_region[i] for i in t).most_common(1)[0][0] for t in tris)),
              "hardWeightTolerance": a.hard_weight_tolerance,
              "minNormalDot": a.min_normal_dot,
              "originalTrianglesPreserved": sum(len(preserved[tag]) for tag in emitted),
              "keptBasis": {str(i): list(gm.vertices[i].co) for i in kept}, "triangles": tris, "cornerUV": uvs, "materials": mats}
    with open(os.path.join(a.out, f"{a.object}.json"), "w") as fh:
        json.dump(report, fh)
    # Flat layout for Unity's JsonUtility (AvatarAgentBaseMesh): positions/UVs/deltas as flat float arrays.
    unity = {"source": a.object, "kept": kept, "keptBasis": [c for i in kept for c in gm.vertices[i].co],
             "triangles": [i for t in tris for i in t], "cornerUV": [c for corners in uvs for uv in corners for c in uv],
             "shapes": [{"name": n, "ids": [d[0] for d in rows], "deltas": [c for d in rows for c in d[1:]]}
                        for n, rows in shape_fit["deltas"].items()],
             "trianglesBefore": tris_before, "trianglesAfter": len(tris)}
    with open(os.path.join(a.out, f"{a.object}.unity.json"), "w") as fh:
        json.dump(unity, fh)
    bm.to_mesh(gm)
    bm.free()
    for q in quads:
        bpy.data.objects.remove(q)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(a.out, f"{a.object}.blend"))
    print(f"RESULT {a.object}: triangles {tris_before} -> {len(tris)}, vertices {len(qm.vertices) if False else report['verticesBefore']} -> {len(kept)}, loops removed {removed_loops}")


main()
