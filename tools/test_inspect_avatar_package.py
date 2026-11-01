import io
from pathlib import Path
import tarfile
import tempfile
import unittest

from inspect_avatar_package import inventory, remap_package

GUID = "1" * 32
NEW_GUID = "2" * 32


def package(entries):
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode="w:gz") as tar:
        for name, data in entries:
            member = tarfile.TarInfo(name)
            member.size = len(data)
            tar.addfile(member, io.BytesIO(data))
    stream.seek(0)
    return stream


class PackageChecks(unittest.TestCase):
    def test_excludes_only_selected_folder_guid(self):
        child = "4" * 32
        source = package([(GUID + "/pathname", b"Assets/existing"),
                          (GUID + "/asset.meta", b"folderAsset: yes\n"),
                          (child + "/pathname", b"Assets/existing/new.asset"),
                          (child + "/asset", b"new data")])
        target = io.BytesIO()
        remap_package(source, target, {}, {GUID})
        target.seek(0)
        with tarfile.open(fileobj=target) as tar:
            self.assertEqual(set(tar.getnames()), {child + "/pathname", child + "/asset"})

    def test_rejects_traversal_and_links(self):
        with tempfile.TemporaryDirectory() as directory:
            stream = package([(GUID + "/pathname", b"Assets/../../outside")])
            with self.assertRaises(ValueError):
                inventory(stream, Path(directory))
            stream = io.BytesIO()
            with tarfile.open(fileobj=stream, mode="w:gz") as tar:
                member = tarfile.TarInfo(GUID + "/asset")
                member.type = tarfile.SYMTYPE
                member.linkname = "/etc/passwd"
                tar.addfile(member)
            stream.seek(0)
            with self.assertRaises(ValueError):
                inventory(stream, Path(directory))

    def test_reports_collisions_and_code(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Assets").mkdir()
            (root / "Assets/existing.asset.meta").write_text("guid: " + GUID + "\n")
            (root / "Assets/new.cs").touch()
            stream = package([(GUID + "/pathname", b"Assets/new.cs")])
            report = inventory(stream, root)
            self.assertEqual((report["guid_collisions"], report["path_collisions"], report["code_assets"]), (1, 1, 1))

    def test_rewrites_yaml_references_and_meta_but_preserves_binary(self):
        yaml = b"%YAML 1.1\nreference: {guid: " + GUID.encode() + b", type: 3}\n"
        binary = b"\x00binary guid: " + GUID.encode()
        source = package([(GUID + "/pathname", b"Assets/new.prefab"),
                          (GUID + "/asset.meta", ("guid: " + GUID + "\n").encode()),
                          (GUID + "/asset", yaml),
                          ("3" * 32 + "/asset", binary)])
        target = io.BytesIO()
        remap_package(source, target, {GUID: NEW_GUID})
        target.seek(0)
        with tarfile.open(fileobj=target) as tar:
            self.assertIn(NEW_GUID.encode(), tar.extractfile(NEW_GUID + "/asset").read())
            self.assertIn(NEW_GUID.encode(), tar.extractfile(NEW_GUID + "/asset.meta").read())
            self.assertEqual(tar.extractfile("3" * 32 + "/asset").read(), binary)


if __name__ == "__main__":
    unittest.main()
