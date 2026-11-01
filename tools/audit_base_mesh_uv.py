"""Independently compare a generated Unity text mesh's UVs with its Blender recipe.

Usage: uv run tools/audit_base_mesh_uv.py MESH.asset RECIPE.json [--output REPORT.json]
Reads only; useful even while Unity is busy.
The recipe is loop_reduce.py's full JSON, not its flattened .unity.json.
"""
import argparse
import collections
import itertools
import json
import re
from pathlib import Path

import numpy as np
from scipy.spatial import cKDTree


def read_mesh(path):
    text = Path(path).read_text()
    vertex = text[text.index("  m_VertexData:"):]
    count = int(re.search(r"m_VertexCount: (\d+)", vertex)[1])
    data = bytes.fromhex(re.search(r"_typelessdata: ([0-9a-f]+)", vertex)[1])
    channels = [tuple(map(int, x)) for x in re.findall(
        r"- stream: (\d+)\n      offset: (\d+)\n      format: (\d+)\n      dimension: (\d+)",
        vertex[:vertex.index("m_DataSize:")])]
    sizes = {0: 4, 1: 2, 2: 1, 3: 1, 4: 2, 5: 2, 6: 1, 7: 1, 8: 2, 9: 2, 10: 4, 11: 4}
    strides, starts = {}, {}
    for stream, offset, fmt, dim in channels:
        if dim:
            strides[stream] = max(strides.get(stream, 0), offset + sizes[fmt] * dim)
    start = 0
    for stream, stride in sorted(strides.items()):
        starts[stream] = start
        start = (start + count * stride + 15) // 16 * 16

    def channel(index):
        stream, offset, fmt, dim = channels[index]
        if fmt != 0:
            raise ValueError("Expected generated Float32 position/UV channels")
        return np.ndarray((count, dim), dtype="<f4", buffer=data,
                          offset=starts[stream] + offset, strides=(strides[stream], 4)).copy()

    index_format = int(re.search(r"m_IndexFormat: (\d+)", text)[1])
    triangles = np.frombuffer(bytes.fromhex(re.search(r"m_IndexBuffer: ([0-9a-f]+)", text)[1]),
                              dtype="<u2" if index_format == 0 else "<u4").reshape(-1, 3)
    return channel(0), channel(4)[:, :2], triangles


def audit(mesh_path, recipe_path, tolerance=.00025):
    points, uv, actual_triangles = read_mesh(mesh_path)
    recipe = json.loads(Path(recipe_path).read_text())
    ids = list(map(int, recipe["keptBasis"]))
    original = np.array([recipe["keptBasis"][str(i)] for i in ids])
    lookup = {value: index for index, value in enumerate(ids)}
    tree = cKDTree(points)
    key = lambda p: tuple(np.round(p, 5))
    actual = collections.defaultdict(list)
    for tri in actual_triangles:
        actual[tuple(sorted(key(points[i]) for i in tri))].append(tri)
    frames = []
    extent = np.linalg.norm(np.ptp(points, axis=0))
    for permutation in itertools.permutations(range(3)):
        for signs in itertools.product((-1, 1), repeat=3):
            mapped = original[:, permutation] * signs
            scale = extent / np.linalg.norm(np.ptp(mapped, axis=0))
            shift = (points.max(0) + points.min(0) - scale * (mapped.max(0) + mapped.min(0))) / 2
            mapped = mapped * scale + shift
            distances, nearest = tree.query(mapped)
            if distances.max() > extent * .001:
                continue
            matched, errors = 0, []
            for tri, corners in zip(recipe["triangles"], recipe["cornerUV"]):
                positions = [key(points[nearest[lookup[i]]]) for i in tri]
                candidates = actual.get(tuple(sorted(positions)), [])
                if not candidates:
                    continue
                matched += 1
                best = None
                for candidate in candidates:
                    values = [min(np.linalg.norm(uv[j] - want) for j in candidate if key(points[j]) == position)
                              for position, want in zip(positions, corners)]
                    if best is None or sum(values) < sum(best):
                        best = values
                errors.extend(best)
            fraction = matched / len(recipe["triangles"])
            if fraction >= .95:
                frames.append({"permutation": permutation, "signs": signs, "scale": float(scale),
                               "matchingTriangleFraction": fraction, "meanUvError": float(np.mean(errors)),
                               "maxUvError": float(max(errors)),
                               "cornersOverTolerance": int(sum(e > tolerance for e in errors))})
    if not frames:
        raise ValueError("No frame reproduces at least 95% of the recipe's geometry")
    frame = max(frames, key=lambda row: row["matchingTriangleFraction"])
    return {"mesh": str(mesh_path), "recipe": str(recipe_path), "uvTolerance": tolerance, "frame": frame,
            "uvCorrespondencePass": frame["cornersOverTolerance"] == 0}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mesh", type=Path)
    parser.add_argument("recipe", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--tolerance", type=float, default=.00025)
    args = parser.parse_args()
    report = audit(args.mesh, args.recipe, args.tolerance)
    text = json.dumps(report, indent=2)
    print(text)
    if args.output:
        args.output.write_text(text + "\n")
    raise SystemExit(0 if report["uvCorrespondencePass"] else 2)
