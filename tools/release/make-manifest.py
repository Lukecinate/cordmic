#!/usr/bin/env python3
"""Build an unsigned release-manifest.json from files on disk. Signing stays manual."""
import argparse, hashlib, json, os, sys

def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()

def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--version", required=True)
    p.add_argument("--channel", choices=("stable", "beta"), default="stable")
    p.add_argument("--out", required=True)
    p.add_argument("files", nargs="+", help="asset paths to include")
    args = p.parse_args()
    files = []
    for path in args.files:
        if not os.path.isfile(path):
            print("error: not a file:", path, file=sys.stderr)
            return 1
        files.append({
            "name": os.path.basename(path),
            "size": os.path.getsize(path),
            "sha256": sha256(path),
        })
    doc = {
        "product": "CordMic",
        "version": args.version.lstrip("vV"),
        "channel": args.channel,
        "files": files,
        "revokedKeyIds": [],
    }
    data = (json.dumps(doc, indent=2) + "\n").encode("utf-8")
    if len(data) > 64 * 1024:
        print("error: manifest would exceed 64 KiB", file=sys.stderr)
        return 1
    out_dir = os.path.dirname(os.path.abspath(args.out))
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    with open(args.out, "wb") as f:
        f.write(data)
    print("wrote", args.out, "files=", len(files))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
