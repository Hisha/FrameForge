#!/usr/bin/env python3
"""Build the isolated FrameForge v2 golden client-test EPF deterministically."""

import argparse
import json
from pathlib import Path
import zipfile


PACKAGE_ROOT = Path(__file__).resolve().parent
REPOSITORY_ROOT = PACKAGE_ROOT.parents[2]
MANIFEST_PATH = PACKAGE_ROOT / "manifest.json"
GOLDEN_PATH = REPOSITORY_ROOT / "examples/frameforge-v2-golden.Design.xml"
PACKAGED_DESIGN_PATH = (
    PACKAGE_ROOT / "client/Interface/FrameXML/FrameForgeGoldenDesign.xml"
)
FIXED_TIME = (1980, 1, 1, 0, 0, 0)


def archive_entry(name: str, payload: bytes) -> tuple[zipfile.ZipInfo, bytes]:
    info = zipfile.ZipInfo(name, FIXED_TIME)
    info.compress_type = zipfile.ZIP_STORED
    info.external_attr = 0o100644 << 16
    return info, payload


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()

    if options.output.exists():
        raise SystemExit(f"Refusing to overwrite {options.output}")

    manifest_bytes = MANIFEST_PATH.read_bytes()
    manifest = json.loads(manifest_bytes)
    if manifest.get("schema") != 3:
        raise SystemExit("The golden client test requires Content Manager schema 3")
    if "frameForgeWowUi" in manifest:
        raise SystemExit("The legacy frameForgeWowUi importer is not permitted")

    golden_bytes = GOLDEN_PATH.read_bytes()
    packaged_design_bytes = PACKAGED_DESIGN_PATH.read_bytes()
    if packaged_design_bytes != golden_bytes:
        raise SystemExit(
            "FrameForgeGoldenDesign.xml differs from the checked-in golden export"
        )

    members: list[tuple[str, bytes]] = [("manifest.json", manifest_bytes)]
    for item in manifest["content"]:
        source = item["source"]
        path = PACKAGE_ROOT / source
        members.append((source, path.read_bytes()))
    toc_source = manifest["clientFrameXml"]["stockTocSource"]
    members.append((toc_source, (PACKAGE_ROOT / toc_source).read_bytes()))

    names = [name for name, _ in members]
    if len(names) != len(set(names)):
        raise SystemExit("Duplicate EPF member")

    options.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(options.output, "w") as archive:
        for name, payload in members:
            info, body = archive_entry(name, payload)
            archive.writestr(info, body)

    print(f"wrote {options.output} ({len(members)} files)")


if __name__ == "__main__":
    main()
