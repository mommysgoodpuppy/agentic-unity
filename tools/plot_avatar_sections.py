#!/usr/bin/env python3
"""Plot AvatarAgentCrossSection exports, marking where meshes cross. Coordinates are from its anchor bone.

    uv run tools/plot_avatar_sections.py SNAPSHOT.json OUTPUT.png [--pair Hat:Hair ...]

Also writes OUTPUT.crossings.json with the crossing counts. Without --pair, every pair of meshes is checked, which
is slow on dense meshes. A crossing is a place one surface goes through another in that slice: clipping,
unless the two are meant to intersect.
"""
import argparse
import itertools
import json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection


def points(layer):
    return np.array([[[s['a']['x'], s['a']['y']], [s['b']['x'], s['b']['y']]] for s in layer['segments']], dtype=float).reshape(-1, 2, 2)


def crossings(a, b):
    found = []
    def cross(x, y):
        return x[..., 0] * y[..., 1] - x[..., 1] * y[..., 0]
    for edge in a:
        p, r = edge[0], edge[1] - edge[0]
        q, s = b[:, 0], b[:, 1] - b[:, 0]
        den = cross(r, s)
        valid = np.abs(den) > 1e-12
        t = np.divide(cross(q - p, s), den, out=np.zeros_like(den), where=valid)
        u = np.divide(cross(q - p, r), den, out=np.zeros_like(den), where=valid)
        for value in t[valid & (t >= 0) & (t <= 1) & (u >= 0) & (u <= 1)]:
            hit = p + value * r
            if not any(np.linalg.norm(hit - other) < 0.0001 for other in found):
                found.append(hit)
    return np.array(found).reshape(-1, 2)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('snapshot', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--pair', action='append', default=[], metavar='A:B', help='mesh labels to check against each other')
    args = parser.parse_args()
    data = json.loads(args.snapshot.read_text())
    plt.style.use('dark_background')
    fig, axes = plt.subplots(3, 3, figsize=(14, 14), constrained_layout=True)
    stats = []
    for ax, section in zip(axes.flat, data['sections']):
        grouped = {}
        for layer in section['layers']:
            line = points(layer)
            if not len(line):
                continue
            c = layer['color']
            ax.add_collection(LineCollection(line * 1000, colors=[(c['r'], c['g'], c['b'])], linewidths=1.5, label=layer['label']))
            grouped.setdefault(layer['label'], []).append(line)
        grouped = {key: np.concatenate(value) for key, value in grouped.items()}
        entry = {'section': section['label'], 'intersections': {}}
        pairs = [tuple(pair.split(':', 1)) for pair in args.pair] or list(itertools.combinations(grouped, 2))
        for first, second in pairs:
            if first in grouped and second in grouped:
                hits = crossings(grouped[first], grouped[second])
                entry['intersections'][f'{first}/{second}'] = len(hits)
                if len(hits):
                    ax.scatter(hits[:, 0] * 1000, hits[:, 1] * 1000, s=22, marker='x', color='white', zorder=5)
        stats.append(entry)
        ax.autoscale()
        ax.set_aspect('equal')
        ax.margins(.08)
        axis = section['axis']
        ax.set_xlabel(['Back ← Z (mm) → Front', 'X (mm)', 'X (mm)'][axis])
        ax.set_ylabel(['Y (mm)', 'Back ← Z (mm) → Front', 'Y (mm)'][axis])
        # Only pairs that really cross fit in a title; every pair is in the .crossings.json.
        crossing = sorted(((v, k) for k, v in entry['intersections'].items() if v), reverse=True)
        shown = ', '.join(f'{k} {v}' for v, k in crossing[:3]) or 'none'
        more = f' +{len(crossing) - 3} more' if len(crossing) > 3 else ''
        ax.set_title(f"{section['label']} m | crossings: {shown}{more}", fontsize=8)
        ax.grid(alpha=.15)
        ax.legend(loc='best', fontsize=8)
    fig.suptitle('Posed mesh cross-sections · white crosses = surfaces crossing', fontsize=15)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(args.output, dpi=145)
    args.output.with_suffix('.crossings.json').write_text(json.dumps(stats, indent=2))
    print(json.dumps(stats, indent=2))


if __name__ == '__main__':
    main()
