"""Compare renders of a built source avatar and its optimized variant.

Reads <reportDir>/render/{source,variant}/<pose>-<mode>-y<yaw>-p<pitch>.png written by
AvatarAgentOptimize's render stage and writes <reportDir>/compare/:

- compare.json: per view, new back-face (magenta) pixels, silhouette lost/gained pixels and
  normal-angle statistics; the worst views first.
- sheets: rows source / variant / flagged, per pose and camera ring, for visual inspection.
  Flag colours on the variant: red = back faces seen only in the variant (hole or flipped face),
  yellow = background where the source showed avatar (missing surface), cyan = normals off by
  more than 35 degrees.

Usage: uv run tools/compare_optimized.py <reportDir>
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

BACKGROUND = np.array([26, 115, 38])  # camera clear colour (0.1, 0.45, 0.15)
MIN_CLUSTER = 3  # pixels: ignore isolated single-pixel edge noise


def load(path):
    return np.asarray(Image.open(path).convert("RGB")).astype(np.int16)


def is_background(img):
    return np.abs(img - BACKGROUND).max(axis=2) <= 12


def is_magenta(img):
    return (img[..., 0] > 200) & (img[..., 1] < 70) & (img[..., 2] > 200)


def erode(mask):
    # 3x3 erosion followed by dilation (opening) removes isolated edge pixels.
    p = np.pad(mask, 1)
    e = np.ones_like(mask)
    for dy in (0, 1, 2):
        for dx in (0, 1, 2):
            e &= p[dy:dy + mask.shape[0], dx:dx + mask.shape[1]]
    p = np.pad(e, 1)
    d = np.zeros_like(mask)
    for dy in (0, 1, 2):
        for dx in (0, 1, 2):
            d |= p[dy:dy + mask.shape[0], dx:dx + mask.shape[1]]
    return d & mask


def normals(img):
    n = img.astype(np.float32) / 255 * 2 - 1
    return n / np.maximum(np.linalg.norm(n, axis=2, keepdims=True), 1e-6)


def content_box(images, pad=0.04):
    """One crop box (left, top, right, bottom) around the avatar in every image, so a sheet's tiles share a scale."""
    m = np.zeros(images[0].shape[:2], bool)
    for img in images:
        m |= ~is_background(img)
    ys, xs = np.nonzero(m)
    if not len(xs):
        return (0, 0, m.shape[1], m.shape[0])
    p = int(pad * max(m.shape))
    return (max(xs.min() - p, 0), max(ys.min() - p, 0), min(xs.max() + p + 1, m.shape[1]), min(ys.max() + p + 1, m.shape[0]))


def tile_size(box, height=420):
    w, h = box[2] - box[0], box[3] - box[1]
    return max(int(height * w / h), 1), height


def main(report_dir):
    root = Path(report_dir)
    src_dir, var_dir, out = root / "render" / "source", root / "render" / "variant", root / "compare"
    out.mkdir(exist_ok=True)
    views = sorted({p.name.replace("-lit-", "-{}-") for p in src_dir.glob("*-lit-*.png")})
    results, flagged = [], {}
    for v in views:
        lit_s, lit_v = load(src_dir / v.format("lit")), load(var_dir / v.format("lit"))
        flag_s, flag_v = load(src_dir / v.format("flag")), load(var_dir / v.format("flag"))
        new_back = erode(is_magenta(flag_v) & ~is_magenta(flag_s))
        lost = erode(is_background(flag_v) & ~is_background(flag_s))
        gained = erode(~is_background(flag_v) & is_background(flag_s))
        r = {"view": v.format("*"), "newBackFace": int(new_back.sum()), "silhouetteLost": int(lost.sum()),
             "silhouetteGained": int(gained.sum())}
        norm_bad = np.zeros(new_back.shape, bool)
        if (src_dir / v.format("normal")).exists():
            ns, nv = load(src_dir / v.format("normal")), load(var_dir / v.format("normal"))
            both = ~is_background(ns) & ~is_background(nv)
            ang = np.degrees(np.arccos(np.clip((normals(ns) * normals(nv)).sum(axis=2), -1, 1)))
            a = ang[both]
            if a.size:
                r.update(normalMeanDeg=round(float(a.mean()), 2), normalP95Deg=round(float(np.percentile(a, 95)), 2),
                         normalOver35Pct=round(float((a > 35).mean() * 100), 2))
            norm_bad = erode(both & (ang > 35))
        overlay = lit_v.copy()
        overlay[norm_bad] = (overlay[norm_bad] * 0.4 + np.array([0, 220, 220]) * 0.6).astype(np.int16)
        overlay[lost] = [255, 230, 0]
        overlay[new_back] = [255, 0, 0]
        flagged[v] = (lit_s, lit_v, overlay)
        results.append(r)

    def score(r):
        return r["newBackFace"] * 4 + r["silhouetteLost"] + r.get("normalOver35Pct", 0) * 10

    results.sort(key=score, reverse=True)
    rings = {"side": "p00", "below": "p35", "above": "pm35"}
    sheets = []
    poses = sorted({v.split("-")[0] for v in views})
    for pose in poses:
        for ring, tag in rings.items():
            vs = sorted(v for v in views if v.startswith(pose + "-") and v.endswith(f"-{tag}.png"))
            if not vs:
                continue
            box = content_box([img for v in vs for img in flagged[v][:2]])
            tile_w, tile_h = tile_size(box)
            sheet = Image.new("RGB", (tile_w * len(vs), tile_h * 3), (20, 20, 20))
            for i, v in enumerate(vs):
                for row, img in enumerate(flagged[v]):
                    sheet.paste(Image.fromarray(img.astype(np.uint8)).crop(box).resize((tile_w, tile_h)), (i * tile_w, row * tile_h))
            name = out / f"sheet-{pose}-{ring}.png"
            sheet.save(name)
            sheets.append(str(name))
    # The cut itself: the variant with its fixed-on clothing hidden (no source counterpart).
    unc = root / "render" / "variant-uncovered"
    for pose in sorted({p.name.split("-")[0] for p in unc.glob("*-lit-*-p00.png")}):
        lits = sorted(unc.glob(f"{pose}-lit-*-p00.png"))
        box = content_box([load(p) for p in lits])
        tile_w, tile_h = tile_size(box)
        sheet = Image.new("RGB", (tile_w * len(lits), tile_h * 2), (20, 20, 20))
        for i, p in enumerate(lits):
            for row, q in enumerate((p, str(p).replace("-lit-", "-flag-"))):
                sheet.paste(Image.open(q).convert("RGB").crop(box).resize((tile_w, tile_h)), (i * tile_w, row * tile_h))
        name = out / f"sheet-uncovered-{pose}.png"
        sheet.save(name)
        sheets.append(str(name))
    # Close-ups (hands, head): rows source lit / variant lit / source normals / variant normals.
    fs, fv = root / "render" / "source-focus", root / "render" / "variant-focus"
    focus = []
    for bone in sorted({p.name.split("_")[0] for p in fs.glob("*_rest-lit-*.png")}):
        lits = sorted(fs.glob(f"{bone}_rest-lit-*-p00.png"))
        if not lits or not (fv / lits[0].name).exists():
            continue
        tile = 320
        sheet = Image.new("RGB", (tile * len(lits), tile * 4), (20, 20, 20))
        worst = 0.0
        for i, p in enumerate(lits):
            for row, q in enumerate((fs / p.name, fv / p.name, fs / p.name.replace("-lit-", "-normal-"), fv / p.name.replace("-lit-", "-normal-"))):
                if q.exists():
                    sheet.paste(Image.open(q).convert("RGB").resize((tile, tile)), (i * tile, row * tile))
            ns, nv = fs / p.name.replace("-lit-", "-normal-"), fv / p.name.replace("-lit-", "-normal-")
            if ns.exists() and nv.exists():
                a_, b_ = load(ns), load(nv)
                both = ~is_background(a_) & ~is_background(b_)
                ang = np.degrees(np.arccos(np.clip((normals(a_) * normals(b_)).sum(axis=2), -1, 1)))[both]
                if ang.size:
                    worst = max(worst, float((ang > 35).mean() * 100))
        name = out / f"sheet-focus-{bone}.png"
        sheet.save(name)
        sheets.append(str(name))
        focus.append({"bone": bone, "worstNormalOver35Pct": round(worst, 2)})
    totals = {k: sum(r[k] for r in results) for k in ("newBackFace", "silhouetteLost", "silhouetteGained")}
    summary = {"views": len(results), "totals": totals, "focus": focus, "worst": results[:12], "all": results, "sheets": sheets}
    (out / "compare.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps({"views": len(results), "totals": totals, "worst": results[:8]}, indent=1))
    print("sheets:", *sheets, sep="\n  ")


if __name__ == "__main__":
    main(sys.argv[1])
