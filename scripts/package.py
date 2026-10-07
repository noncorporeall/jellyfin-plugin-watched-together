#!/usr/bin/env python3
"""
Package a built plugin DLL into a Jellyfin plugin zip and (optionally) update manifest.json.

Usage:
  package.py zip  --dll PATH --version 1.0.0.12 --target-abi 12.1.0.0 --out dist/
  package.py manifest --manifest manifest.json --zip dist/x.zip --version ... --target-abi ...
                      --source-url URL [--changelog TEXT] [--owner NAME] [--image-url URL]
"""
import argparse
import datetime
import hashlib
import json
import os
import zipfile

GUID = "d73b0b70-2b96-4358-861f-78d5edb72753"
NAME = "Watched Together"
DESCRIPTION = "Home screen shelf showing what other users on the server watched recently."
OVERVIEW = ("Adds a 'Recently Watched on This Server' row to the home screen (via Home Screen Sections) "
            "with each watcher's profile picture on the card. Requires Home Screen Sections, "
            "File Transformation and Plugin Pages.")
CATEGORY = "General"


def now_iso():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def make_zip(args):
    os.makedirs(args.out, exist_ok=True)
    meta = {
        "category": CATEGORY,
        "changelog": args.changelog,
        "description": DESCRIPTION,
        "guid": GUID,
        "name": NAME,
        "overview": OVERVIEW,
        "owner": args.owner,
        "targetAbi": args.target_abi,
        "timestamp": now_iso(),
        "version": args.version,
        "status": "Active",
        "autoUpdate": True,
        "imagePath": "",
        "assemblies": [],
    }
    zip_path = os.path.join(args.out, f"watched-together_{args.version}.zip")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(args.dll, os.path.basename(args.dll))
        z.writestr("meta.json", json.dumps(meta, indent=2))
    print(zip_path)


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def update_manifest(args):
    manifest = []
    if os.path.exists(args.manifest):
        with open(args.manifest, encoding="utf-8") as f:
            manifest = json.load(f)

    entry = next((p for p in manifest if p.get("guid") == GUID), None)
    if entry is None:
        entry = {"guid": GUID, "versions": []}
        manifest.append(entry)

    entry.update({
        "name": NAME,
        "description": DESCRIPTION,
        "overview": OVERVIEW,
        "owner": args.owner,
        "category": CATEGORY,
        "imageUrl": args.image_url or "",
    })

    versions = [v for v in entry.get("versions", []) if v.get("version") != args.version]
    versions.insert(0, {
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": args.target_abi,
        "sourceUrl": args.source_url,
        "checksum": md5(args.zip),
        "timestamp": now_iso(),
    })
    # Highest version first so Jellyfin picks the newest compatible build.
    versions.sort(key=lambda v: [int(x) for x in v["version"].split(".")], reverse=True)
    entry["versions"] = versions

    with open(args.manifest, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"manifest updated with {args.version} ({args.target_abi})")


def main():
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)

    z = sub.add_parser("zip")
    z.add_argument("--dll", required=True)
    z.add_argument("--version", required=True)
    z.add_argument("--target-abi", required=True)
    z.add_argument("--out", default="dist")
    z.add_argument("--owner", default="")
    z.add_argument("--changelog", default="")

    m = sub.add_parser("manifest")
    m.add_argument("--manifest", default="manifest.json")
    m.add_argument("--zip", required=True)
    m.add_argument("--version", required=True)
    m.add_argument("--target-abi", required=True)
    m.add_argument("--source-url", required=True)
    m.add_argument("--owner", default="")
    m.add_argument("--changelog", default="")
    m.add_argument("--image-url", default="")

    args = p.parse_args()
    make_zip(args) if args.cmd == "zip" else update_manifest(args)


if __name__ == "__main__":
    main()
