"""Make an optimized (Poor/Medium) variant of an avatar scene in one command.

    python optimize_avatar.py <project> <source scene> [--rank Poor] \
        [--fixed-on TOGGLE=TARGET ...] [--keep-full PATH ...] [--cut PATH ...]

Runs AvatarAgentOptimize's stages headlessly (Unity must be closed for the project):
variant -> analyze (automatic cuts off by default) -> probe -> autophysbones -> decimate -> stats (rescaled and rebuilt if still over the
triangle limit) -> render, then compare_optimized.py. The variant scene is a new dated scene under
Assets/VRUnityGenerated/Optimized/; the source scene is never saved. Reports, renders and contact sheets
go to a private directory under ~/UnityAgentReports/optimize/. Nothing is uploaded.

--fixed-on: big overlaid clothing to keep always on (its MA Object Toggle object is removed) so the
            body under it can be cut. Paths are relative to the avatar root.
--keep-full: renderers never decimated (the face mesh is kept automatically).
--automatic-cuts: explicitly enable experimental visibility-based cuts (disabled by default).
--cut: restrict those automatic cuts to these renderers (used only with --automatic-cuts).
--protect-cut-bones: retain a renderer's geometry weighted to selected humanoid bones and descendants
                     during automatic hidden-surface cuts (RENDERER=LeftHand,RightHand).
--slot-from: continue the upload slot of an earlier optimized scene (default: new slot on first upload).
--base-mesh: replace a renderer's mesh in the variant with a prebuilt reduced base mesh before analysis
             (RENDERER=Assets/VRUnityGenerated/BaseMeshes/...asset, from AvatarAgentBaseMesh.Build).
"""
import argparse
import datetime
import json
import os
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    import compare_optimized  # noqa: E402
except ImportError as e:
    raise SystemExit(f"optimize_avatar.py needs numpy and Pillow ({e}). Run it with uv: uv run tools/optimize_avatar.py")
import unity_env  # noqa: E402

LIMITS = {  # VRChat PC rank limits (creators.vrchat.com, checked 2026-10-27)
    "Poor": {"polyCount": 70000, "skinnedMeshCount": 16, "materialCount": 32, "physBoneComponents": 32,
             "physBoneTransforms": 256, "boneCount": 400, "textureMegabytes": 150},
    "Medium": {"polyCount": 70000, "skinnedMeshCount": 8, "materialCount": 16, "physBoneComponents": 16,
               "physBoneTransforms": 128, "boneCount": 256, "textureMegabytes": 110},
}
TRIANGLE_MARGIN = 2000
REQUIRED_PACKAGES = ("nadena.dev.modular-avatar", "com.anatawa12.avatar-optimizer", "com.ramtype0.meshia.mesh-simplification")
ALLOWED_NEW_UNRESOLVED = ("$AvatarOptimizerClipLengthDummy$",)


class Runner:
    def __init__(self, project, config_path, report_dir):
        self.project, self.config_path, self.report_dir = project, config_path, report_dir
        self.editor, self.env, self.n = unity_env.find_editor(project), unity_env.display_env(), 0

    def stage(self, name):
        if unity_env.editor_open(self.project):
            raise SystemExit("This project is open in a Unity editor; close it and rerun.")
        self.n += 1
        log = self.report_dir / f"{self.n:02d}-{name}.log"
        print(f"[{self.n:02d}] {name} ...", flush=True)
        proc = subprocess.run([str(self.editor), "-batchmode", *unity_env.graphics_args(), "-projectPath", str(self.project),
                               "-executeMethod", "AvatarAgentOptimize.Run", "-optConfig", str(self.config_path),
                               "-optStage", name, "-logFile", str(log)], env=self.env)
        lines = [l.split("] ", 1)[1].rstrip() for l in log.read_text(errors="replace").splitlines()
                 if l.startswith("[AvatarAgentOptimize]") and "report " not in l]
        for l in lines:
            if " pose " not in l:
                print("     " + l[:240])
        if proc.returncode != 0 or not any(l.startswith("DONE") for l in lines):
            raise SystemExit(f"Stage {name} failed (exit {proc.returncode}); see {log}")

    def method(self, method, name, extra):
        """Run another agent batch method (its log lines start with [<class>])."""
        if unity_env.editor_open(self.project):
            raise SystemExit("This project is open in a Unity editor; close it and rerun.")
        self.n += 1
        log = self.report_dir / f"{self.n:02d}-{name}.log"
        print(f"[{self.n:02d}] {name} ...", flush=True)
        proc = subprocess.run([str(self.editor), "-batchmode", *unity_env.graphics_args(), "-projectPath", str(self.project),
                               "-executeMethod", method, *extra, "-logFile", str(log)], env=self.env)
        tag = "[" + method.split(".")[0] + "]"
        lines = [l.split("] ", 1)[1].rstrip() for l in log.read_text(errors="replace").splitlines() if l.startswith(tag)]
        for l in lines:
            print("     " + l[:240])
        if proc.returncode != 0 or "DONE" not in lines:
            raise SystemExit(f"{name} failed (exit {proc.returncode}); see {log}")

    def report(self, name):
        return json.loads((self.report_dir / f"{name}.json").read_text())


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("project", type=Path)
    ap.add_argument("source", help="scene path inside the project, e.g. Assets/VRUnityGenerated/MyAvatar-Outfit.unity")
    ap.add_argument("--rank", choices=sorted(LIMITS), default="Poor")
    ap.add_argument("--fixed-on", action="append", default=[], metavar="TOGGLE=TARGET")
    ap.add_argument("--keep-full", action="append", default=[], metavar="PATH")
    ap.add_argument("--cut", action="append", default=[], metavar="PATH")
    ap.add_argument("--automatic-cuts", action="store_true",
                    help="opt in to experimental visibility-based triangle deletion; manual cutters do not need this")
    def cut_protection(value):
        renderer, sep, bones = value.partition("=")
        names = [name.strip() for name in bones.split(",") if name.strip()]
        if not sep or not renderer or not names:
            raise argparse.ArgumentTypeError("use RENDERER=LeftHand,RightHand")
        return {"renderer": renderer, "bones": names}
    ap.add_argument("--protect-cut-bones", action="append", default=[], type=cut_protection,
                    metavar="RENDERER=BONE,BONE")
    ap.add_argument("--base-mesh", action="append", default=[], metavar="RENDERER=ASSET")
    ap.add_argument("--spatial-cuts", type=Path, help="JSON array of MA Mesh Cutter boxes (name, renderer, center, size, cover)")
    ap.add_argument("--slot-from", default="", metavar="SCENE",
                    help="earlier optimized scene whose upload slot (blueprint ID) the new variant continues")
    ap.add_argument("--body-renderer", default="Body", metavar="PATH",
                    help="body renderer relative to the avatar root, for the hand controls and body audit")
    ap.add_argument("--no-render", action="store_true")
    ap.add_argument("--resume", type=Path, metavar="REPORT_DIR", help="continue an earlier run (its config.json)")
    ap.add_argument("--from", dest="start", default="variant", metavar="STAGE",
                    help="with --resume: first stage to run (analyze, probe, autophysbones, decimate, stats, render)")
    args = ap.parse_args()

    project = args.project.resolve()
    if not (project / args.source).is_file():
        raise SystemExit(f"No scene {args.source} in {project}")
    missing = [p for p in REQUIRED_PACKAGES if p not in unity_env.installed_packages(project)]
    if missing:
        raise SystemExit(f"The project needs {', '.join(missing)} for this pipeline. Add them with ALCOM or VCC: "
                         "see docs/dependencies.md.")
    limits = LIMITS[args.rank]
    if args.resume:
        report_dir = args.resume.resolve()
        config = json.loads((report_dir / "config.json").read_text())
        variant = config["variant"]
        limits = LIMITS[config["rank"]]
    else:
        stem = Path(args.source).stem
        date = datetime.date.today().strftime("%Y%m%d")
        for i in range(1, 100):
            label = f"{stem}-{args.rank}-{date}" + ("" if i == 1 else f"-{i}")
            variant = f"Assets/VRUnityGenerated/Optimized/{label}.unity"
            report_dir = Path.home() / "UnityAgentReports" / "optimize" / label
            if not (project / variant).exists() and not report_dir.exists():
                break
        report_dir.mkdir(parents=True)
    config = config if args.resume else {
        "source": args.source, "variant": variant, "assetDir": variant[:-len(".unity")], "avatarName": "",
        "reportDir": str(report_dir), "rank": args.rank,
        "fixedOn": [dict(zip(("toggle", "target"), f.split("=", 1))) for f in args.fixed_on],
        "cut": args.cut, "keepFull": args.keep_full,
        "automaticCuts": args.automatic_cuts,
        "cutBoneProtection": args.protect_cut_bones,
        "spatialCuts": json.loads(args.spatial_cuts.read_text()) if args.spatial_cuts else [],
        "slotFrom": args.slot_from,
        "bodyRenderer": args.body_renderer,
        "baseMeshes": [dict(zip(("renderer", "mesh"), b.split("=", 1))) for b in args.base_mesh],
        "triangleTarget": limits["polyCount"] - TRIANGLE_MARGIN, "physBoneLimit": limits["physBoneComponents"],
    }
    if args.resume and args.automatic_cuts:
        config["automaticCuts"] = True
    config_path = report_dir / "config.json"
    config_path.write_text(json.dumps(config, indent=2))
    print(f"Variant: {variant}\nReports: {report_dir}", flush=True)
    run = Runner(project, config_path, report_dir)
    order = ["variant", "analyze", "probe", "autophysbones", "decimate", "stats", "render"]
    if args.start not in order:
        raise SystemExit(f"--from must be one of {order}")
    todo = order[order.index(args.start):]

    if "variant" in todo:
        run.stage("variant")
        for b in config.get("baseMeshes", []):
            run.method("AvatarAgentBaseMesh.Swap", "basemesh", ["-scene", variant, "-renderer", b["renderer"], "-mesh", b["mesh"]])
        if config.get("spatialCuts"):
            run.stage("spatial")
    if "analyze" in todo or not config.get("automaticCuts", False):
        run.stage("analyze")
        if run.report("analyze").get("removedAutomaticMaskSlots", 0) and "probe" not in todo:
            print("     Automatic cuts removed; rebuilding cached performance reports.")
            todo = order[order.index("probe"):]
    if "probe" in todo:
        run.stage("probe")
    if "autophysbones" in todo:
        if run.report("probe")["physBoneComponents"] > limits["physBoneComponents"] or args.start == "autophysbones":
            run.stage("autophysbones")
            run.stage("probe")
    if "decimate" in todo:
        run.stage("decimate")
    if "stats" in todo:
        run.stage("stats")
    for attempt in range(2):  # Meshia may stop short of a target (preserved borders); rescale and rebuild
        stats, dec = run.report("stats"), run.report("decimate")
        if stats["polyCount"] <= limits["polyCount"]:
            break
        decimated_now = stats["polyCount"] - dec["fixedTotal"]
        wanted = config["triangleTarget"] - dec["fixedTotal"]
        if decimated_now <= 0 or wanted <= 0:
            break
        config["ratioScale"] = config.get("ratioScale", 1.0) * wanted / decimated_now
        config_path.write_text(json.dumps(config, indent=2))
        print(f"     {stats['polyCount']} triangles is over {limits['polyCount']}; ratio scale -> {config['ratioScale']:.3f}")
        run.stage("decimate")
        run.stage("stats")
    if not args.no_render and "render" in todo:
        run.stage("render")
        compare_optimized.main(str(report_dir))

    # Summary: rank per category and behavioural checks against the built source.
    stats, source = run.report("stats"), run.report("stats-source")
    over = {k: (stats[k], v) for k, v in limits.items() if stats[k] > v}
    new_unresolved = sorted(u for u in set(stats["unresolved"]) - set(source["unresolved"])
                            if not u.startswith(ALLOWED_NEW_UNRESOLVED))
    removed_params = sorted(set(source["parameters"]) - set(stats["parameters"]))
    summary = {
        "variant": variant, "rank": config["rank"], "overall": stats["overall"], "overLimit": over,
        "stats": {k: stats[k] for k in limits}, "source": {k: source[k] for k in limits},
        "newUnresolvedBindings": new_unresolved, "removedParameters": removed_params,
        "addedParameters": sorted(set(stats["parameters"]) - set(source["parameters"])),
    }
    cmp_path = report_dir / "compare" / "compare.json"
    if cmp_path.exists():
        c = json.loads(cmp_path.read_text())
        summary["visual"] = {"totals": c["totals"], "sheets": c["sheets"]}
    (report_dir / "summary.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps({k: v for k, v in summary.items() if k != "visual"}, indent=1))
    print(f"Summary: {report_dir / 'summary.json'}")
    print("Within limits for", config["rank"] if not over else f"{config['rank']}: NO, over in {sorted(over)}")


if __name__ == "__main__":
    main()
