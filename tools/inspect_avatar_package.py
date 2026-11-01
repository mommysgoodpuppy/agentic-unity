#!/usr/bin/env python3
"""Inventory a ZIP-wrapped Unity package without importing or executing assets.

Keep the report private: it contains licensed asset paths and dependency GUIDs.
Optional --stage produces a checked .unitypackage for Unity's own importer.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import tarfile
import tempfile
import zipfile
import uuid
import io

MAX_PACKAGE = 4 * 1024**3
MAX_ASSET = 1024**3
CODE_EXTENSIONS = {".cs", ".dll", ".js", ".boo", ".asmdef", ".asmref", ".rsp", ".so", ".exe"}


def inventory(stream, project):
    records = {}
    total = 0
    with tarfile.open(fileobj=stream, mode="r|gz") as tar:
        for member in tar:
            name = member.name.removeprefix("./").rstrip("/")
            parts = name.split("/")
            if not re.fullmatch(r"[0-9a-fA-F]{32}", parts[0]):
                raise ValueError(f"Unexpected package entry: {name}")
            if member.isdir() and len(parts) == 1:
                continue
            if not member.isfile() or len(parts) != 2 or parts[1] not in ("asset", "asset.meta", "pathname", "preview.png"):
                raise ValueError(f"Unsafe package entry: {name}")
            if member.size > MAX_ASSET:
                raise ValueError(f"Oversize entry: {name}")
            total += member.size
            if total > MAX_PACKAGE:
                raise ValueError("Package exceeds uncompressed size limit")
            record = records.setdefault(parts[0], {"guid": parts[0], "members": []})
            if parts[1] in record["members"]:
                raise ValueError(f"Duplicate package member: {name}")
            record["members"].append(parts[1])
            if parts[1] == "pathname":
                if member.size > 16384:
                    raise ValueError("Oversize asset pathname")
                record["path"] = tar.extractfile(member).read().decode("utf-8-sig").rstrip("\x00\r\n")
    existing_guids = {}
    for root in (project / "Assets", project / "Packages"):
        for meta in root.rglob("*.meta"):
            with meta.open("rb") as f:
                match = re.search(rb"(?m)^guid: ([0-9a-f]{32})\s*$", f.read(65536))
            if match:
                existing_guids[match[1].decode()] = str(meta.relative_to(project))
    seen_paths = set()
    assets = []
    for record in records.values():
        path = record.get("path", "")
        parts = PurePosixPath(path).parts
        if not parts or parts[0] != "Assets" or len(parts) < 2 or any(p in ("..", ".") for p in parts) or "\\" in path or "\x00" in path:
            raise ValueError(f"Invalid asset path: {path!r}")
        if path in seen_paths:
            raise ValueError(f"Duplicate asset path: {path}")
        seen_paths.add(path)
        record["path_exists"] = (project / path).exists()
        record["guid_exists"] = existing_guids.get(record["guid"])
        record["contains_code"] = PurePosixPath(path).suffix.lower() in CODE_EXTENSIONS
        assets.append(record)
    return {"assets": assets, "uncompressed_bytes": total,
            "path_collisions": sum(a["path_exists"] for a in assets),
            "guid_collisions": sum(bool(a["guid_exists"]) for a in assets),
            "code_assets": sum(a["contains_code"] for a in assets)}


def remap_package(source, destination, mapping, excluded_guids=()):
    """Rewrite Unity YAML/meta GUID fields, never arbitrary binary payloads."""
    with tarfile.open(fileobj=source, mode="r|gz") as original, tarfile.open(fileobj=destination, mode="w:gz") as output:
        for member in original:
            parts = member.name.removeprefix("./").rstrip("/").split("/")
            if parts[0] in excluded_guids:
                continue
            if parts[0] in mapping:
                parts[0] = mapping[parts[0]]
            member.name = "/".join(parts)
            if member.isdir():
                output.addfile(member)
                continue
            data = original.extractfile(member).read()
            if parts[-1] == "asset.meta" or (parts[-1] == "asset" and data.startswith(b"%YAML")):
                data = re.sub(rb"\bguid: ([0-9a-f]{32})\b",
                    lambda m: b"guid: " + mapping.get(m[1].decode(), m[1].decode()).encode(), data)
            member.size = len(data)
            output.addfile(member, io.BytesIO(data))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("project", type=Path)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--stage", type=Path)
    parser.add_argument("--remap-guid-collisions", action="store_true",
                        help="stage an isolated package with new GUIDs for colliding assets and rewritten YAML references")
    parser.add_argument("--skip-existing-folders", action="store_true",
                        help="omit folder records only when path and GUID already match the project")
    args = parser.parse_args()
    args.report.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with zipfile.ZipFile(args.archive) as archive:
        packages = [i for i in archive.infolist() if i.filename.endswith(".unitypackage") and not i.is_dir()]
        if len(packages) != 1:
            parser.error("Expected exactly one .unitypackage")
        if packages[0].file_size > MAX_PACKAGE:
            parser.error("Package too large")
        with tempfile.TemporaryFile() as staged:
            with archive.open(packages[0]) as source:
                shutil.copyfileobj(source, staged)
            staged.seek(0)
            digest = hashlib.file_digest(staged, "sha256").hexdigest()
            staged.seek(0)
            report = inventory(staged, args.project.resolve())
            report["package_sha256"] = digest
            report["package_name"] = packages[0].filename
            if args.skip_existing_folders:
                if not args.stage:
                    parser.error("skipping folder records requires --stage")
                skipped = [a for a in report["assets"] if "asset" not in a["members"]
                           and (args.project / a["path"]).is_dir() and a["guid_exists"] == a["path"] + ".meta"]
                if skipped:
                    staged.seek(0)
                    with tempfile.TemporaryFile() as filtered:
                        remap_package(staged, filtered, {}, {a["guid"] for a in skipped})
                        staged.seek(0); staged.truncate(); filtered.seek(0)
                        shutil.copyfileobj(filtered, staged)
                    staged.seek(0)
                    report = inventory(staged, args.project.resolve())
                    staged.seek(0)
                    report["package_sha256"] = hashlib.file_digest(staged, "sha256").hexdigest()
                    report["source_package_sha256"] = digest
                    report["skipped_existing_folders"] = [a["path"] for a in skipped]
                    report["package_name"] = packages[0].filename
            if args.remap_guid_collisions:
                if not args.stage or report["path_collisions"] or report["code_assets"]:
                    parser.error("remapping requires --stage, no existing asset paths, and no code assets")
                mapping = {a["guid"]: uuid.uuid5(uuid.NAMESPACE_URL, digest + ":" + a["guid"]).hex
                           for a in report["assets"] if a["guid_exists"]}
                if mapping:
                    staged.seek(0)
                    with tempfile.TemporaryFile() as remapped:
                        remap_package(staged, remapped, mapping)
                        staged.seek(0); staged.truncate()
                        remapped.seek(0); shutil.copyfileobj(remapped, staged)
                    staged.seek(0)
                    report = inventory(staged, args.project.resolve())
                    staged.seek(0)
                    report["package_sha256"] = hashlib.file_digest(staged, "sha256").hexdigest()
                    report["source_package_sha256"] = digest
                    report["guid_remapping"] = mapping
                    report["package_name"] = packages[0].filename
            args.report.write_text(json.dumps(report, indent=2))
            args.report.chmod(0o600)
            if args.stage:
                if any(report[k] for k in ("path_collisions", "guid_collisions", "code_assets")):
                    raise SystemExit("Refusing unattended staging: collisions or executable assets; review report")
                staged.seek(0)
                with args.stage.open("xb") as dest:
                    shutil.copyfileobj(staged, dest)
                args.stage.chmod(0o600)
    print(json.dumps({k: v for k, v in report.items() if k != "assets"}, indent=2))


if __name__ == "__main__":
    main()
