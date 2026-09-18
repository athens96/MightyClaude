#!/usr/bin/env python3
"""Write the update manifest (latest.json) the app checks for new versions.

    make-update-manifest.py --macos release/MightyClaude-macos.zip \
        --base-url https://updates.example.com/mightyclaude/0.2.0 --out release/latest.json \
        [--sign-key ~/.config/mightyclaude/update-signing.key] [--version 0.2.0] [--notes-file notes.md] \
        [--windows-x64 …zip] [--windows-arm64 …zip]
    make-update-manifest.py --generate-key ~/.config/mightyclaude/update-signing.key   # prints the public key

With --sign-key the output is the signed envelope the app requires when it
was built with MIGHTY_UPDATE_PUBLIC_KEY; the plain manifest is written next
to it as latest.unsigned.json. The version defaults to the VERSION file and
must match the app's rule: N(.N){0,3} with optional -prerelease/+build.
Signing uses scripts/update-manifest-sign.swift (compiled with swiftc on
first use), so it runs on macOS/CI without extra Python packages.
"""
import argparse, base64, datetime, hashlib, json, os, re, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION_RE = re.compile(r"^v?[0-9]+(\.[0-9]+){0,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")
FORMAT = "mightyclaude-update-v1"

def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def asset(path, base):
    return {"url": base.rstrip("/") + "/" + os.path.basename(path), "sha256": digest(path), "size": os.path.getsize(path)}

def signer():
    source = os.path.join(ROOT, "scripts", "update-manifest-sign.swift")
    binary = os.path.join(ROOT, ".build", "update-manifest-sign")
    if not os.path.exists(binary) or os.path.getmtime(binary) < os.path.getmtime(source):
        os.makedirs(os.path.dirname(binary), exist_ok=True)
        subprocess.run(["swiftc", "-O", source, "-o", binary], check=True)
    return binary

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--macos"); p.add_argument("--windows-x64"); p.add_argument("--windows-arm64")
    p.add_argument("--base-url"); p.add_argument("--out")
    p.add_argument("--version"); p.add_argument("--build", type=int); p.add_argument("--notes-file"); p.add_argument("--minimum-system-version", default="14.0")
    p.add_argument("--sign-key"); p.add_argument("--generate-key")
    a = p.parse_args()
    if a.generate_key:
        if os.path.exists(a.generate_key): sys.exit(f"refusing to overwrite {a.generate_key}")
        public = subprocess.run([signer(), "keygen", a.generate_key], capture_output=True, text=True, check=True).stdout.strip()
        print(f"private key written to {a.generate_key} (keep it secret; give it to CI as MIGHTY_UPDATE_SIGNING_KEY)")
        print(f"public key (build with MIGHTY_UPDATE_PUBLIC_KEY): {public}")
        return
    if not a.base_url or not a.out: sys.exit("--base-url and --out are required")
    version = (a.version or open(os.path.join(ROOT, "VERSION")).read()).strip()
    if not VERSION_RE.match(version) or len(version) > 64: sys.exit(f"version {version!r} is not accepted by the app (N.N.N with optional -pre/+build)")
    build = a.build
    if build is None:
        try: build = int(subprocess.run(["git", "-C", ROOT, "rev-list", "--count", "HEAD"], capture_output=True, text=True, check=True).stdout.strip())
        except Exception: build = 0
    manifest = {"version": version.lstrip("vV"), "build": build, "publishedAt": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "minimumSystemVersion": a.minimum_system_version}
    if a.notes_file: manifest["notes"] = open(a.notes_file, encoding="utf-8").read().strip()
    if a.macos: manifest["macos"] = asset(a.macos, a.base_url)
    windows = {k: asset(v, a.base_url) for k, v in (("x64", a.windows_x64), ("arm64", a.windows_arm64)) if v}
    if windows: manifest["windows"] = windows
    if not a.macos and not windows: sys.exit("at least one package is required")
    for entry in ([manifest.get("macos")] + list(windows.values())):
        if entry and not entry["url"].startswith("https://"): sys.exit(f"package url must be https: {entry['url']}")
    os.makedirs(os.path.dirname(a.out) or ".", exist_ok=True)
    plain = json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    if a.sign_key:
        unsigned = os.path.splitext(a.out)[0] + ".unsigned.json"
        with open(unsigned, "w", encoding="utf-8") as f: f.write(plain)
        signature = subprocess.run([signer(), "sign", a.sign_key, unsigned], capture_output=True, text=True, check=True).stdout.strip()
        envelope = {"format": FORMAT, "version": manifest["version"], "payload": base64.b64encode(plain.encode("utf-8")).decode("ascii"), "signature": signature}
        with open(a.out, "w", encoding="utf-8") as f: json.dump(envelope, f, ensure_ascii=False, indent=2); f.write("\n")
        print(a.out, "(signed)", unsigned)
    else:
        with open(a.out, "w", encoding="utf-8") as f: f.write(plain)
        print(a.out, "(unsigned — build without MIGHTY_UPDATE_PUBLIC_KEY accepts it)")

if __name__ == "__main__":
    main()
