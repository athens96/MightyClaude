#!/usr/bin/env python3
"""Release Mighty Code one step at a time (docs/release.md).

    release.py check --version 0.4.48
    release.py version --summary "<한 줄 요약>"
    release.py push --yes
    release.py ci [--commit <later CI-fix sha on main>]
    release.py download
    release.py manifest [--apk PATH]      (CI signs: release-manifest.yml; --sign-key PATH signs locally instead)
    release.py page
    release.py upload --dry-run | --yes
    release.py github --yes
    release.py verify
    release.py status | next [...]

State lives in release/<ver>/state.json (release/ is gitignored). Every step needs the steps
before it (unless --force). Steps that publish (push, upload, github) do nothing without --yes.
latest.json is signed by the release-manifest.yml workflow with the repository secret MIGHTY_UPDATE_SIGNING_KEY
(the only copy of the key). With --sign-key the key is only a path handed to make-update-manifest.py; this script never reads it.
"""
import argparse, base64, datetime, difflib, hashlib, json, os, re, shutil, subprocess, sys, time, urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELEASE_ROOT = os.path.join(ROOT, "release")
PUBLIC_BASE = "https://pub-b705901d18c940159d40f58683d758ad.r2.dev/mightycode"
BUCKET = "mightycode"
# Where the apps from before the rename to Mighty Code (0.4.53 and older) look for updates and the download page
# (docs/rename-identifiers.md). Each release also writes its signed latest.json there, whose package addresses point
# into the new bucket, and a page that sends a visitor on to the new one.
LEGACY_BASE = "https://pub-fd035e0a9ad7411f8d8d8963cc2b9702.r2.dev/mightyclaude"
LEGACY_BUCKET = "mightyclaude"
# The public half of the update signing key (also in .github/workflows/native-windows.yml).
PUBLIC_KEY = "C02DD3qWG+A5gJdYwpaEnvpzYugo354CLtUtU+oXmZ4="
FORMAT = "mightyclaude-update-v1"
REPO = "athens96/MightyCode"
STEPS = ["check", "version", "push", "ci", "download", "manifest", "page", "upload", "github", "verify"]
# Re-running these replaces files the later steps used, so the later steps must run again.
PRODUCERS = {"ci", "download", "manifest", "page"}
PUBLISHING = {"push", "upload", "github"}
# `wrangler r2 object put` refuses larger files (MAX_UPLOAD_SIZE_BYTES in wrangler 4).
MAX_UPLOAD_BYTES = 300 * 1024 * 1024
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+$")
PLACEHOLDER = "(채워 주세요)"
WORKFLOWS = {"macos": "native-macos.yml", "windows": "native-windows.yml"}
# Signs latest.json with the repository secret MIGHTY_UPDATE_SIGNING_KEY, the only copy of the private key.
MANIFEST_WORKFLOW = "release-manifest.yml"
MANIFEST_ARTIFACT = "MightyCode-release-manifest"
ARTIFACTS = {  # artifact name -> (workflow, files inside)
    "MightyCode-native-macos": ("macos", ["MightyCode-macos.zip"]),
    "MightyCode-native-windows-x64": ("windows", ["MightyCode-windows-x64.zip", "MightyCode-windows-x64.zip.sha256",
                                                    "MightyCode-windows-x64-setup.exe", "MightyCode-windows-x64-setup.exe.sha256"]),
    "MightyCode-native-windows-arm64": ("windows", ["MightyCode-windows-arm64.zip", "MightyCode-windows-arm64.zip.sha256",
                                                      "MightyCode-windows-arm64-setup.exe", "MightyCode-windows-arm64-setup.exe.sha256"]),
}
# The five packages, in upload order; the manifest names the three zips.
PACKAGES = ["MightyCode-macos.zip", "MightyCode-windows-x64.zip", "MightyCode-windows-arm64.zip",
            "MightyCode-windows-x64-setup.exe", "MightyCode-windows-arm64-setup.exe"]
MANIFEST_ROLES = {"macos": "MightyCode-macos.zip", "x64": "MightyCode-windows-x64.zip", "arm64": "MightyCode-windows-arm64.zip"}
# Shipped when the run made them: the Mac dmg comes from the notarize-macos job, which runs only once
# notarization is set up (repository variable MIGHTY_MAC_NOTARIZE). artifact -> files it may hold.
OPTIONAL_ARTIFACT_FILES = {"MightyCode-native-macos": ["MightyCode-macos.dmg"]}
OPTIONAL_PACKAGES = ["MightyCode-macos.dmg"]


class ReleaseError(Exception):
    pass


class FetchError(ReleaseError):
    """A public URL could not be read (network, HTTP error, or not JSON)."""


def say(message):
    print(message, flush=True)


# ---------------------------------------------------------------- small helpers (patched in tests)

def sh(args, check=True, capture=True, cwd=ROOT):
    """Run a command; its stdout as text. A missing tool or a failure is a ReleaseError."""
    exe = shutil.which(args[0]) or args[0]  # wrangler/gh are .cmd shims on Windows
    try:
        done = subprocess.run([exe] + list(args[1:]), cwd=cwd, capture_output=capture, text=True, encoding="utf-8", errors="replace")
    except FileNotFoundError:
        raise ReleaseError(f"{args[0]}을(를) 찾을 수 없습니다")
    if check and done.returncode != 0:
        detail = (done.stderr or done.stdout or "").strip() if capture else ""
        raise ReleaseError(f"실패: {' '.join(args)}" + (f"\n{detail}" if detail else ""))
    return (done.stdout or "").strip() if capture else ""


def sh_ok(args):
    try:
        exe = shutil.which(args[0])
        return bool(exe) and subprocess.run([exe] + list(args[1:]), cwd=ROOT, capture_output=True).returncode == 0
    except OSError:
        return False


def fetch(url):
    request = urllib.request.Request(url, headers={"Cache-Control": "no-cache", "User-Agent": "mightyclaude-release"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.read()
    except (OSError, ValueError) as error:  # URLError/HTTPError/timeouts are OSErrors
        raise FetchError(f"{url}을(를) 읽지 못했습니다: {error}")


def head_size(url):
    request = urllib.request.Request(url, method="HEAD", headers={"Cache-Control": "no-cache", "User-Agent": "mightyclaude-release"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return int(response.headers.get("Content-Length", "-1"))
    except (OSError, ValueError) as error:
        raise FetchError(f"{url}: {error}")


def fetch_json(url):
    try:
        return json.loads(fetch(url))
    except ValueError as error:
        raise FetchError(f"{url}이(가) JSON이 아닙니다: {error}")


def read_text(path):
    """A notes file as text; a BOM from Windows editors is dropped."""
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def envelope_payload(envelope):
    try:
        return json.loads(base64.b64decode(envelope["payload"]).decode("utf-8"))
    except (KeyError, ValueError) as error:
        raise ReleaseError(f"봉투의 payload를 읽을 수 없습니다: {error}")


def check_upload_size(path):
    size = os.path.getsize(path)
    if size > MAX_UPLOAD_BYTES:
        raise ReleaseError(f"{os.path.basename(path)}이(가) {size:,} B로 wrangler r2 object put 한도(300 MiB)를 넘습니다 — "
                           "패키지를 줄이거나 다른 업로드 방법이 필요합니다")


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def rel(path):
    try:
        return os.path.relpath(path, ROOT)
    except ValueError:  # another drive on Windows
        return path


def version_tuple(version):
    if not VERSION_RE.match(version or ""):
        raise ReleaseError(f"버전 {version!r}은(는) x.y.z 꼴이 아닙니다")
    return tuple(int(p) for p in version.split("."))


def read_version_file():
    with open(os.path.join(ROOT, "VERSION"), encoding="utf-8") as f:
        return f.read().strip()


# ---------------------------------------------------------------- state

def release_dir(version):
    return os.path.join(RELEASE_ROOT, version)


def current_pointer():
    return os.path.join(RELEASE_ROOT, ".current")


def resolve_version(given):
    if given:
        return given
    if os.path.exists(current_pointer()):
        with open(current_pointer(), encoding="utf-8") as f:
            return f.read().strip()
    raise ReleaseError("진행 중인 릴리스가 없습니다. 먼저 `release.py check --version x.y.z`를 실행하세요")


def load_state(version):
    path = os.path.join(release_dir(version), "state.json")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    return {"version": version, "done": {}, "data": {}}


def save_state(state):
    folder = release_dir(state["version"])
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, "state.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(state, f, ensure_ascii=False, indent=2)
        f.write("\n")


def mark_done(state, step, changed=True):
    state["done"][step] = datetime.datetime.now().isoformat(timespec="seconds")
    if step in PRODUCERS and changed:
        later = [s for s in STEPS[STEPS.index(step) + 1:] if s in state["done"]]
        for s in later:
            del state["done"][s]
        if later:
            say(f"  ※ 파일이 새로 만들어져 다음 단계를 다시 해야 합니다: {', '.join(later)}")
    save_state(state)


def next_step(state):
    return next((s for s in STEPS if s not in state["done"]), None)


def require_previous(state, step, force=False):
    missing = [s for s in STEPS[:STEPS.index(step)] if s not in state["done"]]
    if missing and not force:
        raise ReleaseError(f"'{step}' 전에 끝내야 할 단계가 있습니다: {', '.join(missing)} (무시하려면 --force)")


# ---------------------------------------------------------------- notes

def notes_path(version):
    return os.path.join(RELEASE_ROOT, f"{version}-notes.md")


def update_notes_path(version):
    return os.path.join(release_dir(version), "update-notes.md")


def notes_template(version):
    rows = "\n".join(f"| {name}{' (Apple Silicon)' if 'macos' in name else ''} | `{{{{SHA:{name}}}}}` | {{{{SIZE:{name}}}}} |"
                     for name in ["MightyCode-macos.zip", "MightyCode-windows-x64-setup.exe", "MightyCode-windows-arm64-setup.exe",
                                  "MightyCode-windows-x64.zip", "MightyCode-windows-arm64.zip"])
    return f"""{{{{DATE}}}} · Mac build {{{{MAC_BUILD}}}} · Windows (beta) x64·ARM64 · Android {{{{ANDROID}}}}

<details open>
<summary><b>한국어</b></summary>

{PLACEHOLDER} {version}에서 바뀐 점

</details>

<details>
<summary><b>English</b></summary>

{PLACEHOLDER} What changed in {version}

</details>

## Downloads · 내려받기 · 下载 · ダウンロード
{PUBLIC_BASE}/

Mac and Windows can also update from inside the app. · 맥과 윈도우는 앱 안의 업데이트로도 받을 수 있습니다. · Mac 和 Windows 也可以在应用内更新。 · Mac と Windows はアプリ内のアップデートでも入手できます。

| File · 파일 · 文件 · ファイル | SHA-256 | Size · 크기 · 大小 · サイズ |
|---|---|---|
{rows}
{{{{DMG_ROW}}}}{{{{ANDROID_ROW}}}}
"""


def ensure_notes(version):
    """Create the release notes and the short update notes from templates when missing; the paths created."""
    created = []
    if not os.path.exists(notes_path(version)):
        os.makedirs(RELEASE_ROOT, exist_ok=True)
        with open(notes_path(version), "w", encoding="utf-8", newline="\n") as f:
            f.write(notes_template(version))
        created.append(notes_path(version))
    if not os.path.exists(update_notes_path(version)):
        os.makedirs(release_dir(version), exist_ok=True)
        with open(update_notes_path(version), "w", encoding="utf-8", newline="\n") as f:
            f.write(f"- {PLACEHOLDER} 앱의 업데이트 안내에 보일 짧은 한국어 요약 (한 줄에 하나)\n")
        created.append(update_notes_path(version))
    return created


def fill_notes(text, values):
    """Replace {{TOKEN}} placeholders the release now knows; unknown tokens stay."""
    return re.sub(r"\{\{([A-Z_]+(?::[^}]+)?)\}\}", lambda m: str(values[m.group(1)]) if m.group(1) in values else m.group(0), text)


def unfinished_notes(text):
    return [m for m in ([PLACEHOLDER, "(fill in)"] + re.findall(r"\{\{[^}]+\}\}", text)) if m in text]


def rendered_notes_path(version):
    """The GitHub release body: release/<ver>-notes.md (kept with its {{…}} tokens) filled from the current state."""
    return os.path.join(release_dir(version), "release-notes.md")


def note_values(state):
    data = state["data"]
    values = {"DATE": data.get("date") or datetime.date.today().isoformat()}
    if "build" in data:
        values["MAC_BUILD"] = data["build"]
    for name, info in data.get("files", {}).items():
        values[f"SHA:{name}"], values[f"SIZE:{name}"] = info["sha256"], info["size"]
    dmg = data.get("files", {}).get("MightyCode-macos.dmg")
    values["DMG_ROW"] = f"| MightyCode-macos.dmg (Apple Silicon) | `{dmg['sha256']}` | {dmg['size']} |\n" if dmg else ""
    apk, old = data.get("apk"), data.get("previousApk")
    if apk:
        values["ANDROID"] = apk["version"]
        values["ANDROID_ROW"] = f"| MightyCode-{apk['version']}.apk (Android) | `{apk['sha256']}` | {apk['size']} |"
    elif old:
        values["ANDROID"] = old
        values["ANDROID_ROW"] = f"\nAndroid: {old} APK — https://github.com/{REPO}/releases/tag/v{old}"
    return values


def render_notes(state):
    """Write the release body from the template and today's state (re-rendered every time); its text."""
    version = state["version"]
    template = read_text(notes_path(version))
    if not template.strip():
        raise ReleaseError(f"{rel(notes_path(version))}이(가) 비었습니다")
    text = fill_notes(template, note_values(state))
    os.makedirs(release_dir(version), exist_ok=True)
    with open(rendered_notes_path(version), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return text


# ---------------------------------------------------------------- manifest

def make_manifest(version, build, folder, sign_key, notes_file, out, base=PUBLIC_BASE):
    """Run scripts/make-update-manifest.py for the three zips in folder; it reads the key, not us."""
    args = [sys.executable, os.path.join(ROOT, "scripts", "make-update-manifest.py"),
            "--macos", os.path.join(folder, MANIFEST_ROLES["macos"]),
            "--windows-x64", os.path.join(folder, MANIFEST_ROLES["x64"]),
            "--windows-arm64", os.path.join(folder, MANIFEST_ROLES["arm64"]),
            "--base-url", f"{base}/{version}", "--out", out, "--version", version, "--build", str(build),
            "--notes-file", notes_file, "--sign-key", sign_key]
    done = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if done.returncode != 0:
        raise ReleaseError("make-update-manifest.py 실패:\n" + (done.stderr or done.stdout).strip())


def verify_envelope(envelope, version, build, folder, public_key=PUBLIC_KEY, base=PUBLIC_BASE):
    """Check the signed envelope against the public key and the files in folder; the payload, or ReleaseError."""
    from cryptography.exceptions import InvalidSignature
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
    if envelope.get("format") != FORMAT:
        raise ReleaseError(f"봉투 format이 {envelope.get('format')!r}입니다")
    try:
        payload_bytes = base64.b64decode(envelope["payload"], validate=True)
        signature = base64.b64decode(envelope["signature"], validate=True)
        Ed25519PublicKey.from_public_bytes(base64.b64decode(public_key)).verify(signature, payload_bytes)
    except InvalidSignature:
        raise ReleaseError("서명이 공개 키와 맞지 않습니다 (다른 키로 서명했거나 내용이 바뀌었습니다)")
    except (KeyError, ValueError) as error:
        raise ReleaseError(f"봉투를 읽을 수 없습니다: {error}")
    payload = json.loads(payload_bytes.decode("utf-8"))
    problems = []
    if payload.get("version") != version or envelope.get("version") != version:
        problems.append(f"버전 {payload.get('version')}/{envelope.get('version')} ≠ {version}")
    if payload.get("build") != build:
        problems.append(f"build {payload.get('build')} ≠ {build}")
    if not str(payload.get("notes", "")).strip() or PLACEHOLDER in str(payload.get("notes", "")):
        problems.append("notes가 비었거나 채워지지 않았습니다")
    entries = {"macos": payload.get("macos"), "x64": (payload.get("windows") or {}).get("x64"),
               "arm64": (payload.get("windows") or {}).get("arm64")}
    for role, name in MANIFEST_ROLES.items():
        entry, path = entries[role], os.path.join(folder, name)
        if not entry:
            problems.append(f"{role} 항목이 없습니다")
            continue
        if entry.get("url") != f"{base}/{version}/{name}":
            problems.append(f"{role} url {entry.get('url')}")
        if entry.get("size") != os.path.getsize(path):
            problems.append(f"{role} size {entry.get('size')} ≠ {os.path.getsize(path)}")
        if entry.get("sha256") != digest(path):
            problems.append(f"{role} sha256이 파일과 다릅니다")
    if problems:
        raise ReleaseError("매니페스트 확인 실패: " + "; ".join(problems))
    return payload


# ---------------------------------------------------------------- download page

PAGE_TEMPLATE = os.path.join(ROOT, "scripts", "download-page", "index.html")
PAGE_ICON = os.path.join(ROOT, "scripts", "download-page", "icon.png")


def page_version(html):
    """The version a download page announces (`버전 X`, in the old page's bare <p> or the template's)."""
    found = re.search(r'<p(?: class="version")?>버전 (\d+\.\d+\.\d+)</p>', html)
    return found.group(1) if found else None


def render_page(version, apk_version, has_dmg, template=None):
    """The download page for version from scripts/download-page: its {{…}} fields filled, the app icon inlined,
    and the <!-- dmg-begin --> … <!-- dmg-end --> blocks kept only when the release ships a dmg."""
    html = template if template is not None else read_text(PAGE_TEMPLATE)
    block = re.compile(r"[ \t]*<!-- dmg-begin -->\n(.*?)[ \t]*<!-- dmg-end -->\n", re.S)
    html = block.sub((lambda m: m.group(1)) if has_dmg else "", html)
    with open(PAGE_ICON, "rb") as f:
        icon = "data:image/png;base64," + base64.b64encode(f.read()).decode("ascii")
    html = html.replace("{{VERSION}}", version).replace("{{APK_VERSION}}", apk_version).replace("{{ICON}}", icon)
    left = re.findall(r"\{\{[A-Z_]+\}\}|<!-- dmg-(?:begin|end) -->", html)
    if left:
        raise ReleaseError(f"내려받기 페이지 템플릿에 채우지 못한 자리가 있습니다: {', '.join(sorted(set(left)))}")
    return html


def page_apk_version(html):
    found = re.search(r'download="MightyCode-(\d+\.\d+\.\d+)\.apk"', html)
    return found.group(1) if found else None


# ---------------------------------------------------------------- upload

CONTENT_TYPES = {".zip": "application/zip", ".exe": "application/vnd.microsoft.portable-executable", ".dmg": "application/x-apple-diskimage",
                 ".apk": "application/vnd.android.package-archive", ".json": "application/json", ".html": "text/html; charset=utf-8"}


def upload_plan(version, folder, apk=None, names=PACKAGES):
    """(key, file, content type, cache control) in upload order: packages, latest.json, APK, download page.
    The APK key is fixed (the old one is overwritten), so it goes only once the update itself is live."""
    def entry(key, path, cache=None):
        return (f"mightycode/{key}", path, CONTENT_TYPES[os.path.splitext(path)[1]], cache)
    plan = [entry(f"{version}/{name}", os.path.join(folder, name)) for name in names]
    plan.append(entry("latest.json", os.path.join(folder, "latest.json"), "no-cache"))
    if apk:
        plan.append(entry("android/MightyCode-android.apk", apk, "no-cache"))  # fixed key, so never cached long
    # The public bucket URL does not add index.html, so the page is also written at the folder key.
    page = os.path.join(folder, "index.html")
    plan += [entry("index.html", page, "no-cache"), ("mightycode/", page, CONTENT_TYPES[".html"], "no-cache")]
    return plan


def legacy_page():
    """The page left at the old download address: it sends the visitor on to the new one."""
    return (f'<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta http-equiv="refresh" content="0; url={PUBLIC_BASE}/">'
            f'<title>Mighty Code</title></head><body><p>Mighty Code는 <a href="{PUBLIC_BASE}/">{PUBLIC_BASE}/</a>로 옮겼습니다. '
            f'Mighty Code moved to <a href="{PUBLIC_BASE}/">{PUBLIC_BASE}/</a>.</p></body></html>\n')


def legacy_plan(folder):
    """(bucket, key, file, content type, cache control) for the old address, after the new one is live: the same signed
    latest.json, so an app from before the rename updates, and the page that points on."""
    page = os.path.join(folder, "legacy-index.html")
    with open(page, "w", encoding="utf-8") as f:
        f.write(legacy_page())
    html = CONTENT_TYPES[".html"]
    return [(LEGACY_BUCKET, "mightyclaude/latest.json", os.path.join(folder, "latest.json"), CONTENT_TYPES[".json"], "no-cache"),
            (LEGACY_BUCKET, "mightyclaude/index.html", page, html, "no-cache"),
            (LEGACY_BUCKET, "mightyclaude/", page, html, "no-cache")]


def wrangler_command(key, path, content_type, cache, bucket=BUCKET):
    command = ["wrangler", "r2", "object", "put", f"{bucket}/{key}", "--file", path, "--content-type", content_type]
    if cache:
        command += ["--cache-control", cache]
    return command + ["--remote"]


# ---------------------------------------------------------------- steps

def files_of(state):
    return state["data"].get("files", {})


def package_names(files):
    """The packages this release ships, in upload order: the five, then any optional one the run made."""
    return PACKAGES + [name for name in OPTIONAL_PACKAGES if name in files]


def has_packages(files):
    return set(PACKAGES) <= set(files) and set(files) <= set(PACKAGES + OPTIONAL_PACKAGES)


def step_check(state, a):
    version = state["version"]
    rows = []

    def item(ok, text, hint=""):
        rows.append(ok)
        say(f"  [{'v' if ok else ' '}] {text}" + (f" — {hint}" if hint and not ok else ""))

    say(f"릴리스 {version} 준비 확인")
    branch = sh(["git", "rev-parse", "--abbrev-ref", "HEAD"])
    item(branch == "main", f"main 브랜치 (지금 {branch})", "git switch main")
    item(sh(["git", "status", "--porcelain"]) == "", "작업 트리가 깨끗함", "커밋하거나 치워 두세요")
    sh(["git", "fetch", "origin", "main", "--quiet"], check=False)
    behind = sh(["git", "rev-list", "--count", "HEAD..origin/main"], check=False)
    item(behind == "0", "origin/main보다 뒤처지지 않음", "git pull --ff-only")
    ahead = sh(["git", "rev-list", "--count", "origin/main..HEAD"], check=False)
    item(ahead == "0", "push 안 된 로컬 커밋 없음", f"{ahead}개 — 먼저 push하거나 정리하세요")
    item(sh_ok(["gh", "auth", "status"]), "gh 로그인", "gh auth login")
    item(bool(shutil.which("wrangler")), "wrangler 설치됨 (로그인은 확인하지 않음 — upload 전에 wrangler login)", "npm i -g wrangler")
    try:
        import cryptography.hazmat.primitives.asymmetric.ed25519  # noqa: F401
        item(True, "python cryptography")
    except ImportError:
        item(False, "python cryptography", "pip install cryptography")
    key = a.sign_key or state["data"].get("signKey")
    if key:
        item(os.path.isfile(os.path.expanduser(key)), f"로컬 서명 키 경로 {key}", "--sign-key PATH (빼면 CI가 서명합니다)")
        if os.path.isfile(os.path.expanduser(key)):
            state["data"]["signKey"] = os.path.abspath(os.path.expanduser(key))
    else:
        item(sh_ok(["gh", "workflow", "view", MANIFEST_WORKFLOW]), f"CI 서명 워크플로 {MANIFEST_WORKFLOW} (기본 브랜치에 있어야 함)",
             "main에 .github/workflows/release-manifest.yml이 있어야 합니다")
    current = read_version_file()
    try:
        version_tuple(version)
        item(version_tuple(current) <= version_tuple(version), f"VERSION {current} → {version}")
    except ReleaseError as error:
        item(False, str(error))
    try:
        live = fetch_json(f"{PUBLIC_BASE}/latest.json").get("version", "")
        item(version_tuple(version) > version_tuple(live), f"공개 latest.json {live} < {version}")
    except ReleaseError as error:
        item(False, f"공개 latest.json 읽기 ({error})")
    for path in ensure_notes(version):
        say(f"  · 템플릿을 만들었습니다: {rel(path)}")
    for path in (notes_path(version), update_notes_path(version)):
        left = unfinished_notes(read_text(path))
        say(f"  · {rel(path)}" + (f" — 아직 채울 곳: {', '.join(sorted(set(left)))[:120]}" if left else " 준비됨"))
    if all(rows):
        mark_done(state, "check")
        say("확인 완료. 다음: release.py version --summary \"…\"")
    else:
        save_state(state)
        raise ReleaseError("확인하지 못한 항목이 있습니다")


def step_version(state, a):
    version = state["version"]
    if sh(["git", "rev-parse", "--abbrev-ref", "HEAD"]) != "main":
        raise ReleaseError("main 브랜치에서 실행하세요")
    subject = sh(["git", "log", "-1", "--format=%s"])
    if read_version_file() == version and subject.startswith(f"(release) {version}:"):
        say(f"이미 릴리스 커밋이 있습니다: {subject}")
    else:
        if not a.summary:
            raise ReleaseError("--summary \"한 줄 요약\"이 필요합니다")
        if sh(["git", "status", "--porcelain"]) != "":
            raise ReleaseError("작업 트리가 깨끗하지 않습니다")
        path = os.path.join(ROOT, "VERSION")
        with open(path, "rb") as f:
            newline = "\r\n" if f.read().endswith(b"\r\n") else "\n"
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(version + newline)
        message = ["-m", f"(release) {version}: {a.summary}"] + (["-m", f"Co-Authored-By: {a.co_author}"] if a.co_author else [])
        sh(["git", "add", "VERSION"])
        sh(["git", "commit", "--quiet"] + message)
        say(f"릴리스 커밋을 만들었습니다: (release) {version}: {a.summary}")
    state["data"]["commit"] = sh(["git", "rev-parse", "HEAD"])
    ensure_notes(version)
    mark_done(state, "version")
    say("다음 (올리기): release.py push --yes   (= git push origin main)")


def step_push(state, a):
    commit = state["data"]["commit"]
    sh(["git", "fetch", "origin", "main", "--quiet"], check=False)
    if sh_ok(["git", "merge-base", "--is-ancestor", commit, "origin/main"]):
        say("origin/main에 이미 릴리스 커밋이 있습니다")
    else:
        if not a.yes:
            say("올리려면: release.py push --yes   (git push origin main 을 실행합니다)")
            raise ReleaseError("--yes 없이 push하지 않습니다")
        say("git push origin main …")
        sh(["git", "push", "origin", "main"], capture=False)
    mark_done(state, "push")
    say("다음: release.py ci")


def find_run(workflow, commit):
    # The commit's push run, or a run started by hand on main for the same commit (a push run GitHub left queued, as
    # 0.4.57's fd01f1d3 runs were): the newest of them.
    runs = []
    for event in ("push", "workflow_dispatch"):
        found = json.loads(sh(["gh", "run", "list", "--commit", commit, "--workflow", workflow, "--event", event,
                               "--json", "databaseId,status,conclusion,url,headBranch,createdAt", "--limit", "20"]) or "[]")
        runs += [r for r in found if (event == "push" or r.get("headBranch") == "main") and r.get("conclusion") != "cancelled"]
    runs.sort(key=lambda r: (r.get("headBranch") == "main", r.get("createdAt", "")), reverse=True)
    return runs[0] if runs else None


def build_skipped(run_id):
    """Whether a completed run skipped every build job: its `changes` job found only the other platform's files changed
    (scripts/ci-changes.sh), so it made no package."""
    jobs = json.loads(sh(["gh", "run", "view", str(run_id), "--json", "jobs"]) or "{}").get("jobs", [])
    builds = [j for j in jobs if j.get("name") != "changes"]
    return bool(builds) and all(j.get("conclusion") == "skipped" for j in builds)


def step_ci(state, a):
    version, commit = state["version"], a.commit or state["data"].get("buildCommit") or state["data"]["commit"]
    commit = sh(["git", "rev-parse", commit])
    sh(["git", "fetch", "origin", "main", "--quiet"], check=False)
    if not sh_ok(["git", "merge-base", "--is-ancestor", commit, "origin/main"]):
        raise ReleaseError(f"{commit[:8]}이(가) origin/main에 없습니다 — 먼저 main에 push하세요 (main의 CI만 릴리스에 씁니다)")
    if commit != state["data"]["commit"]:
        # A CI fix after the release commit (as 0.4.47 shipped from 73879aeb): same VERSION, on top of the release commit.
        if not sh_ok(["git", "merge-base", "--is-ancestor", state["data"]["commit"], commit]):
            raise ReleaseError(f"{commit[:8]}은(는) 릴리스 커밋 뒤의 커밋이 아닙니다")
        if sh(["git", "show", f"{commit}:VERSION"]).strip() != version:
            raise ReleaseError(f"{commit[:8]}의 VERSION이 {version}이 아닙니다")
    build = int(sh(["git", "rev-list", "--count", commit]))
    say(f"빌드 커밋 {commit[:8]} (Mac build {build})의 CI를 기다립니다")
    deadline = time.time() + a.timeout_min * 60
    seen = {}
    dispatched = {}  # workflow name -> when release.py asked for its full run (runs made before it do not count)
    while True:
        runs = {name: find_run(workflow, commit) for name, workflow in WORKFLOWS.items()}
        for name, since in dispatched.items():
            if runs[name] and runs[name].get("createdAt", "") < since:
                runs[name] = None
        for name, run in runs.items():
            status = (run or {}).get("status", "아직 없음")
            if seen.get(name) != status:
                say(f"  {name}: {status}" + (f"  {run['url']}" if run else ""))
                seen[name] = status
        if all(r and r["status"] == "completed" for r in runs.values()):
            # A push run that skipped this platform (only the other one's files changed) made no package: a full run
            # started by hand on main builds it, when the build commit is main's head (a dispatch builds main's head).
            skipped = [name for name, r in runs.items() if r["conclusion"] == "success" and name not in dispatched and build_skipped(r["databaseId"])]
            if not skipped:
                break
            if sh(["git", "rev-parse", "origin/main"]) != commit:
                raise ReleaseError(f"{', '.join(skipped)} CI가 {commit[:8]}의 빌드를 건너뛰었습니다(다른 플랫폼 파일만 바뀜). "
                                   "main의 마지막 커밋으로 `release.py ci --commit <sha>`를 하면 그 커밋의 전체 실행을 시작합니다")
            for name in skipped:
                since = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() - 5))
                sh(["gh", "workflow", "run", WORKFLOWS[name], "--ref", "main", "-f", "full=true"])
                dispatched[name] = since
                seen.pop(name, None)
                say(f"  {name}: 이 커밋의 푸시 실행이 빌드를 건너뛰어 전체 실행을 시작했습니다")
            continue
        if time.time() > deadline:
            raise ReleaseError("CI가 시간 안에 끝나지 않았습니다. 나중에 다시 `release.py ci`")
        time.sleep(a.poll_sec)
    failed = [f"{name}: {r['conclusion']} {r['url']}  (로그: gh run view {r['databaseId']} --log-failed)"
              for name, r in runs.items() if r["conclusion"] != "success"]
    if failed:
        raise ReleaseError("CI 실패 — 고친 커밋을 main에 올린 뒤 `release.py ci --commit <sha>`:\n  " + "\n  ".join(failed))
    new = {"buildCommit": commit, "build": build, "runs": {n: r["databaseId"] for n, r in runs.items()}}
    changed = any(state["data"].get(k) != v for k, v in new.items())
    state["data"].update(new)
    mark_done(state, "ci", changed)
    say("CI 성공. 다음: release.py download")


def read_sha256_file(path):
    with open(path, encoding="ascii") as f:
        return f.read().split()[0].lower()


MAC_ENGINE = "MightyCode.app/Contents/Frameworks/Chromium Embedded Framework.framework/"


def check_mac_package(path, version):
    """A Mac zip a release can ship: this version, the update channel's key and address, and the bundled browser engine."""
    import plistlib, zipfile
    try:
        with zipfile.ZipFile(path) as z:
            info = plistlib.loads(z.read("MightyCode.app/Contents/Info.plist"))
            engine = any(n.startswith(MAC_ENGINE) for n in z.namelist())
    except (OSError, KeyError, zipfile.BadZipFile, plistlib.InvalidFileException) as e:
        raise ReleaseError(f"Mac zip을 읽지 못했습니다: {e}")
    problems = []
    if info.get("CFBundleShortVersionString") != version:
        problems.append(f"버전 {info.get('CFBundleShortVersionString')!r} (기대 {version})")
    if info.get("MightyUpdatePublicKey") != PUBLIC_KEY:
        problems.append("업데이트 공개 키가 없거나 다름 (이 앱은 업데이트를 확인하지 못함)")
    if info.get("MightyUpdateManifestURL") != f"{PUBLIC_BASE}/latest.json":
        problems.append("업데이트 주소가 없거나 다름")
    if not engine:
        problems.append("브라우저 엔진(Chromium Embedded Framework)이 들어 있지 않음")
    if problems:
        raise ReleaseError("Mac zip을 릴리스할 수 없습니다: " + "; ".join(problems)
                           + " — main push의 native-macos.yml 빌드인지 확인하세요")
    say("  Mac zip: 버전·업데이트 키·주소·브라우저 엔진 확인")


def step_download(state, a):
    folder, runs = release_dir(state["version"]), state["data"]["runs"]
    sources = state["data"].setdefault("sources", {})  # artifact -> the run its files came from
    for artifact, (workflow, names) in ARTIFACTS.items():
        have = all(os.path.exists(os.path.join(folder, n)) for n in names)
        if have and sources.get(artifact) == runs[workflow] and not a.redownload:
            say(f"  {artifact}: 이미 있음 (실행 {runs[workflow]})")
            continue
        sources.pop(artifact, None)
        temp = os.path.join(folder, "_download", artifact)
        shutil.rmtree(temp, ignore_errors=True)
        say(f"  {artifact} 내려받는 중 …")
        sh(["gh", "run", "download", str(runs[workflow]), "-n", artifact, "-D", temp])
        for name in names:
            source = os.path.join(temp, name)
            if not os.path.exists(source):
                raise ReleaseError(f"{artifact}에 {name}이(가) 없습니다")
            os.replace(source, os.path.join(folder, name))
        for name in OPTIONAL_ARTIFACT_FILES.get(artifact, []):
            source, target = os.path.join(temp, name), os.path.join(folder, name)
            if os.path.exists(source):
                os.replace(source, target)
            elif os.path.exists(target):
                os.remove(target)  # left from another run; this run made none
        sources[artifact] = runs[workflow]
        save_state(state)
    shutil.rmtree(os.path.join(folder, "_download"), ignore_errors=True)
    files = {}
    for name in PACKAGES + [n for n in OPTIONAL_PACKAGES if os.path.exists(os.path.join(folder, n))]:
        path = os.path.join(folder, name)
        files[name] = {"sha256": digest(path), "size": os.path.getsize(path)}
        compared = os.path.exists(path + ".sha256")
        if compared and read_sha256_file(path + ".sha256") != files[name]["sha256"]:
            raise ReleaseError(f"{name}의 sha256이 .sha256 파일과 다릅니다")
        say(f"  {name:<38} {files[name]['size']:>13,} B  {files[name]['sha256'][:16]}…" + ("  (.sha256 일치)" if compared else ""))
        check_upload_size(path)
    check_mac_package(os.path.join(folder, "MightyCode-macos.zip"), state["version"])
    state["data"]["files"] = files
    mark_done(state, "download")
    say("다음: release.py manifest [--apk PATH]")


def ci_manifest(state, a, notes):
    """Dispatch release-manifest.yml for this release, wait for it, and put its latest.json(+unsigned) in the release folder."""
    version, folder, data = state["version"], release_dir(state["version"]), state["data"]
    token = f"{version}-{os.urandom(4).hex()}"  # the run name carries it, so the run found is this dispatch's
    # No build input: the workflow counts it from the runs' commit, and the signed payload must match data["build"].
    inputs = {"version": version, "macos_run": str(data["runs"]["macos"]),
              "windows_run": str(data["runs"]["windows"]), "notes_b64": base64.b64encode(notes.encode("utf-8")).decode("ascii"),
              "request": token}
    command = ["gh", "workflow", "run", MANIFEST_WORKFLOW, "--ref", "main"]
    for name, value in inputs.items():
        command += ["-f", f"{name}={value}"]
    shown = [f"notes_b64=<update-notes.md, base64 {len(inputs['notes_b64'])}자>" if c.startswith("notes_b64=") else c for c in command]
    say("CI가 release 환경의 시크릿 키로 latest.json에 서명합니다 (아무것도 내보내지 않음):")
    say("  " + " ".join(shown))
    for name in ("latest.json", "latest.unsigned.json"):  # never verify a manifest left from an earlier run
        if os.path.exists(os.path.join(folder, name)):
            os.remove(os.path.join(folder, name))
    sh(command)
    deadline = time.time() + a.timeout_min * 60
    seen = None
    while True:
        runs = json.loads(sh(["gh", "run", "list", "--workflow", MANIFEST_WORKFLOW, "--event", "workflow_dispatch", "--branch", "main",
                              "--json", "databaseId,displayTitle,status,conclusion,url,createdAt,headBranch,headSha",
                              "--limit", "20"]) or "[]")
        mine = [r for r in runs if r.get("headBranch") == "main" and f"[{token}]" in r.get("displayTitle", "")]
        if len(mine) > 1:
            raise ReleaseError(f"요청 {token}의 실행이 {len(mine)}개입니다: " + ", ".join(str(r["databaseId"]) for r in mine))
        run = mine[0] if mine else None
        status = (run or {}).get("status", "아직 없음")
        if status != seen:
            say(f"  {MANIFEST_WORKFLOW}: {status}" + (f"  {run['url']}" if run else ""))
            seen = status
        if run and run["status"] == "completed":
            break
        if time.time() > deadline:
            raise ReleaseError(f"{MANIFEST_WORKFLOW} 실행이 시간 안에 끝나지 않았습니다 (요청 {token}). 다시 `release.py manifest`")
        time.sleep(min(a.poll_sec, 15))
    data["manifestRun"], data["manifestRunSha"] = run["databaseId"], run.get("headSha")
    save_state(state)
    if run["conclusion"] != "success":
        raise ReleaseError(f"{MANIFEST_WORKFLOW}: {run['conclusion']} {run['url']}  (로그: gh run view {run['databaseId']} --log-failed)")
    temp = os.path.join(folder, "_download", MANIFEST_ARTIFACT)
    shutil.rmtree(temp, ignore_errors=True)
    sh(["gh", "run", "download", str(run["databaseId"]), "-n", MANIFEST_ARTIFACT, "-D", temp])
    for name in ("latest.json", "latest.unsigned.json"):
        if not os.path.exists(os.path.join(temp, name)):
            raise ReleaseError(f"{MANIFEST_ARTIFACT}에 {name}이(가) 없습니다")
        os.replace(os.path.join(temp, name), os.path.join(folder, name))
    shutil.rmtree(os.path.join(folder, "_download"), ignore_errors=True)


def step_manifest(state, a):
    version, folder, data = state["version"], release_dir(state["version"]), state["data"]
    key = a.sign_key or data.get("signKey")  # a local key file is the alternative; without one CI signs
    if key and not os.path.isfile(os.path.expanduser(key)):
        raise ReleaseError(f"서명 키 파일이 없습니다: {key} (CI가 서명하게 하려면 --sign-key 없이, state.json의 signKey를 지우세요)")
    short = read_text(update_notes_path(version)) if os.path.exists(update_notes_path(version)) else ""
    if not short.strip() or unfinished_notes(short):
        raise ReleaseError(f"{rel(update_notes_path(version))}을(를) 먼저 채우세요 (앱의 업데이트 안내)")
    if not files_of(state):
        raise ReleaseError("받은 패키지 기록이 없습니다. release.py download")
    for name, info in files_of(state).items():
        if digest(os.path.join(folder, name)) != info["sha256"]:
            raise ReleaseError(f"{name}이(가) download 뒤에 바뀌었습니다. release.py download --redownload")
    if a.apk:
        if not os.path.isfile(a.apk):
            raise ReleaseError(f"APK가 없습니다: {a.apk}")
        check_upload_size(a.apk)
        apk_version = a.apk_version or version
        copy = os.path.join(folder, f"MightyCode-{apk_version}.apk")
        if os.path.abspath(a.apk) != os.path.abspath(copy):
            shutil.copyfile(a.apk, copy)
        data["apk"] = {"version": apk_version, "path": copy, "sha256": digest(copy), "size": os.path.getsize(copy)}
        say(f"  APK {apk_version}: {data['apk']['size']:,} B")
    elif a.no_apk:
        data.pop("apk", None)
    if "apk" not in data:
        say("  APK 없음 — 폰 APK는 그대로 둡니다")
    out = os.path.join(folder, "latest.json")
    if key:
        make_manifest(version, data["build"], folder, os.path.expanduser(key), update_notes_path(version), out)
    else:
        ci_manifest(state, a, short)
    with open(out, encoding="utf-8") as f:
        payload = verify_envelope(json.load(f), version, data["build"], folder, public_key=PUBLIC_KEY)
    if not key and payload.get("notes") != short.strip():
        raise ReleaseError(f"CI가 서명한 notes가 {rel(update_notes_path(version))}과(와) 다릅니다")
    say(f"  latest.json 서명·버전·build {payload['build']}·3개 패키지의 url/sha256/size 확인")
    data["date"] = datetime.date.today().isoformat()
    if "apk" not in data:
        try:
            data["previousApk"] = page_apk_version(fetch(f"{PUBLIC_BASE}/").decode("utf-8"))
        except ReleaseError as error:
            say(f"  (공개 페이지에서 이전 APK 버전을 읽지 못했습니다: {error})")
    # The template keeps its {{…}} tokens; the body is rendered again from the state each time.
    filled = render_notes(state)
    say(f"  {rel(rendered_notes_path(version))}: 날짜·build·SHA-256·크기를 채운 릴리스 본문")
    left = unfinished_notes(filled)
    if left:
        say(f"  ※ 릴리스 노트에 아직 채울 곳: {', '.join(sorted(set(left)))} (github 단계 전에)")
    mark_done(state, "manifest")
    say("다음: release.py page")


def step_page(state, a):
    version, folder = state["version"], release_dir(state["version"])
    live = fetch(f"{PUBLIC_BASE}/").decode("utf-8")
    apk = state["data"].get("apk")
    if page_version(live) == version and os.path.exists(os.path.join(folder, "index.html")):
        say("공개 페이지가 이미 이 버전입니다 — 만들어 둔 index.html을 그대로 씁니다")
    else:
        # Without a new APK the phone download keeps the version the live page names.
        apk_version = apk["version"] if apk else page_apk_version(live)
        if not apk_version:
            raise ReleaseError("공개 페이지에서 Android APK 버전을 찾지 못했습니다 (--apk로 새 APK를 주세요)")
        page = render_page(version, apk_version, "MightyCode-macos.dmg" in files_of(state))
        for href in re.findall(rf'href="{re.escape(version)}/([^"]+)"', page):
            if href not in package_names(files_of(state)):
                raise ReleaseError(f"페이지가 올리지 않는 파일을 가리킵니다: {version}/{href}")
        with open(os.path.join(folder, "index.html"), "w", encoding="utf-8", newline="") as f:
            f.write(page)
        shown = re.sub(r"data:image/png;base64,[A-Za-z0-9+/=]+", "data:image/png;base64,…", page)
        say(f"내려받기 페이지 {page_version(live) or '?'} → {version} (scripts/download-page에서):")
        for line in difflib.unified_diff(live.splitlines(), shown.splitlines(), "공개 페이지", "release/" + version + "/index.html", lineterm="", n=0):
            say("  " + line)
    mark_done(state, "page")
    say("다음: release.py upload --dry-run  →  release.py upload --yes")


def step_upload(state, a):
    version, folder, data = state["version"], release_dir(state["version"]), state["data"]
    apk = data.get("apk")
    files = files_of(state)
    plan = upload_plan(version, folder, apk and apk["path"], package_names(files))
    # Nothing goes up unless every file is the one recorded, and the manifest still matches them and the public key.
    if not has_packages(files):
        raise ReleaseError("받은 패키지 기록이 없거나 모자랍니다. release.py download")
    check_recorded(state, [path for _, path, *_ in plan])
    for _, path, *_ in plan:
        check_upload_size(path)
    with open(os.path.join(folder, "latest.json"), encoding="utf-8") as f:
        local = json.load(f)
    payload = verify_envelope(local, version, data["build"], folder)
    try:
        live = fetch_json(f"{PUBLIC_BASE}/latest.json")
        live_version = live.get("version", "0.0.0")
        if version_tuple(live_version) > version_tuple(version):
            raise ReleaseError(f"공개 latest.json이 더 새 버전({live_version})입니다")
        if live_version == version:
            def sums(p):
                return [(p.get("macos") or {}).get("sha256")] + [w.get("sha256") for _, w in sorted((p.get("windows") or {}).items())]
            if sums(envelope_payload(live)) != sums(payload) and not a.force_overwrite_live:
                raise ReleaseError(f"공개 latest.json이 이미 {version}인데 다른 패키지를 가리킵니다 — 덮어쓰려면 --force-overwrite-live")
            say(f"  공개 latest.json이 이미 {version}입니다 (다시 올리기)")
    except FetchError as error:
        if not a.allow_unreadable_live:
            raise ReleaseError(f"공개 latest.json을 확인할 수 없어 멈춥니다 (버전 되돌림 방지): {error}\n"
                               "  그래도 올리려면 --allow-unreadable-live")
        say(f"  (공개 latest.json을 읽지 못했지만 --allow-unreadable-live: {error})")
    # The old address comes last, once the new one is live.
    commands = [(BUCKET, *entry) for entry in plan] + legacy_plan(folder)
    if a.dry_run or not a.yes:
        say(f"올릴 순서 ({len(commands)}개, 아무것도 보내지 않음):")
        for bucket, *entry in commands:
            say("  " + " ".join(f'"{p}"' if " " in p else p for p in wrangler_command(*entry, bucket=bucket)))
        if not a.dry_run:
            raise ReleaseError("올리려면 --yes (wrangler가 로그인되어 있어야 합니다)")
        return
    for index, (bucket, *entry) in enumerate(commands, 1):
        say(f"  [{index}/{len(commands)}] {bucket}/{entry[0]} ({entry[2]})")
        for attempt in (1, 2, 3):  # R2 answers an occasional 500 "internal error, try again"
            try:
                sh(wrangler_command(*entry, bucket=bucket))
                break
            except ReleaseError as error:
                if attempt == 3:
                    raise ReleaseError(f"{entry[0]} 세 번 실패 — 다시 `release.py upload --yes` (처음부터 다시 올려도 됩니다)\n{error}")
                say(f"    다시 시도 ({attempt})")
                time.sleep(attempt * 5)
    mark_done(state, "upload")
    say("올리기 완료. 다음: release.py github --yes")


def release_assets(state):
    folder = release_dir(state["version"])
    assets = [os.path.join(folder, n) for n in package_names(files_of(state))]
    if state["data"].get("apk"):
        assets.append(state["data"]["apk"]["path"])
    return assets


def recorded_sums(state):
    """file path -> the sha256 the release recorded for it (packages from download, APK from manifest)."""
    folder, data = release_dir(state["version"]), state["data"]
    sums = {os.path.join(folder, name): info["sha256"] for name, info in files_of(state).items()}
    if data.get("apk"):
        sums[data["apk"]["path"]] = data["apk"]["sha256"]
    return sums


def check_recorded(state, paths):
    sums = recorded_sums(state)
    for path in paths:
        if not os.path.exists(path):
            raise ReleaseError(f"{path}이(가) 없습니다")
        if path in sums and digest(path) != sums[path]:
            raise ReleaseError(f"{os.path.basename(path)}이(가) 기록된 sha256과 다릅니다 (download/manifest 뒤에 바뀜)")


def step_github(state, a):
    version, tag = state["version"], f"v{state['version']}"
    if not has_packages(files_of(state)):
        raise ReleaseError("받은 패키지 기록이 없거나 모자랍니다. release.py download")
    notes = render_notes(state)
    left = unfinished_notes(notes)
    if left:
        raise ReleaseError(f"{rel(notes_path(version))}에 채울 곳이 남았습니다: {', '.join(sorted(set(left)))}")
    apk = state["data"].get("apk")
    header = notes.splitlines()[0]
    if apk and f"Android {apk['version']}" not in header:
        raise ReleaseError(f"노트 첫 줄이 Android {apk['version']}을(를) 말하지 않습니다: {header}")
    if not apk and f"Android {version}" in header:
        raise ReleaseError(f"APK를 넣지 않았는데 노트 첫 줄이 Android {version}입니다: {header}")
    assets = release_assets(state)
    check_recorded(state, assets)
    try:
        existing = json.loads(sh(["gh", "release", "view", tag, "--json", "isDraft,assets"]))
    except (ReleaseError, ValueError):
        existing = None
    commands = []
    if existing is None:
        # A draft first, so the release is never public without its files; published once all are attached.
        commands.append(["gh", "release", "create", tag, "--draft", "--target", state["data"]["buildCommit"],
                         "--title", f"Mighty Code {version}", "--notes-file", rendered_notes_path(version)] + assets)
    else:
        names = {x["name"] for x in existing.get("assets", [])}
        missing = [p for p in assets if os.path.basename(p) not in names]
        say(f"GitHub 릴리스 {tag}이(가) 이미 있습니다" + (" (초안)" if existing.get("isDraft") else "")
            + (f" — 빠진 첨부 {len(missing)}개" if missing else " — 첨부 모두 있음"))
        if missing:
            commands.append(["gh", "release", "upload", tag] + missing)
    if existing is None or existing.get("isDraft"):
        commands.append(["gh", "release", "edit", tag, "--draft=false", "--latest"])
    if commands and not a.yes:
        for command in commands:
            say("할 명령: " + " ".join(command))
        raise ReleaseError("하려면 --yes")
    for command in commands:
        sh(command, capture=False)
    mark_done(state, "github")
    say("다음: release.py verify")


def step_verify(state, a):
    version, folder, data = state["version"], release_dir(state["version"]), state["data"]
    problems = []
    for base in (PUBLIC_BASE, LEGACY_BASE):
        try:
            verify_envelope(fetch_json(f"{base}/latest.json"), version, data["build"], folder)
            say(f"  [v] 공개 latest.json {version}, 서명 확인 ({base})")
        except ReleaseError as error:
            problems.append(str(error))
    checks = [(f"{PUBLIC_BASE}/{version}/{n}", info["size"]) for n, info in files_of(state).items()]
    if data.get("apk"):
        checks.append((f"{PUBLIC_BASE}/android/MightyCode-android.apk", data["apk"]["size"]))
    for url, size in checks:
        try:
            got = head_size(url)
        except FetchError as error:
            say(f"  [ ] {url}  읽지 못함")
            problems.append(str(error))
            continue
        say(f"  [{'v' if got == size else ' '}] {url}  {got:,} B")
        if got != size:
            problems.append(f"{url} 크기 {got} ≠ {size}")
    try:
        page = fetch(f"{PUBLIC_BASE}/").decode("utf-8")
        ok = page_version(page) == version and f'href="{version}/' in page
    except FetchError as error:
        page, ok = None, False
        problems.append(str(error))
    say(f"  [{'v' if ok else ' '}] 내려받기 페이지 버전 {version}")
    if page is not None and not ok:
        problems.append("내려받기 페이지가 새 버전이 아닙니다")
    try:
        release = json.loads(sh(["gh", "release", "view", f"v{version}", "--json", "assets,isDraft"]))
        names = {x["name"] for x in release["assets"]}
        missing = [os.path.basename(p) for p in release_assets(state) if os.path.basename(p) not in names]
        say(f"  [{' ' if missing or release.get('isDraft') else 'v'}] GitHub 릴리스 v{version} 첨부 {len(names)}개")
        if missing:
            problems.append("GitHub 릴리스에 없는 첨부: " + ", ".join(missing))
        if release.get("isDraft"):
            problems.append("GitHub 릴리스가 아직 초안입니다")
    except (ReleaseError, ValueError, KeyError) as error:
        say(f"  [ ] GitHub 릴리스 v{version}")
        problems.append(f"GitHub 릴리스를 읽지 못했습니다: {error}")
    if problems:
        raise ReleaseError("확인 실패:\n  " + "\n  ".join(problems))
    mark_done(state, "verify")
    say(f"릴리스 {version} 끝. (도움말 사이트를 바꿨다면 scripts/help/upload.sh)")


def step_status(state, a):
    say(f"릴리스 {state['version']}")
    for step in STEPS:
        say(f"  [{'v' if step in state['done'] else ' '}] {step:<9} {state['done'].get(step, '')}")
    upcoming = next_step(state)
    say(f"다음: {upcoming}" if upcoming else "모든 단계를 마쳤습니다")


HANDLERS = {"check": step_check, "version": step_version, "push": step_push, "ci": step_ci, "download": step_download,
            "manifest": step_manifest, "page": step_page, "upload": step_upload, "github": step_github, "verify": step_verify}


def run_step(step, state, a):
    if a.force and step in PUBLISHING:
        raise ReleaseError(f"'{step}'는 내보내는 단계라 --force를 받지 않습니다. 앞 단계를 끝내세요")
    if step != "check":
        require_previous(state, step, a.force)
    HANDLERS[step](state, a)


def parser():
    p = argparse.ArgumentParser(description="Mighty Code 릴리스를 한 단계씩 (docs/release.md)")
    p.add_argument("step", choices=STEPS + ["status", "next"])
    p.add_argument("--version", help="릴리스 버전 x.y.z (check에서 정하면 이후 단계는 기억합니다)")
    p.add_argument("--summary", help="version: 릴리스 커밋 제목의 한 줄 요약")
    p.add_argument("--co-author", help="version: 커밋에 붙일 Co-Authored-By 값")
    p.add_argument("--sign-key", help="로컬 서명 키 파일 경로 — 주면 CI 대신 로컬에서 서명 (내용은 make-update-manifest.py만 읽습니다)")
    p.add_argument("--apk", help="manifest: 이번에 함께 낼 Android APK")
    p.add_argument("--apk-version", help="manifest: APK 버전 (기본: 릴리스 버전)")
    p.add_argument("--no-apk", action="store_true", help="manifest: 전에 고른 APK를 빼기")
    p.add_argument("--commit", help="ci: 릴리스 커밋 대신 쓸 그 뒤의 main 커밋 (CI 수정)")
    p.add_argument("--timeout-min", type=int, default=180)
    p.add_argument("--poll-sec", type=int, default=60)
    p.add_argument("--dry-run", action="store_true", help="upload: 명령만 보여 주기")
    p.add_argument("--yes", action="store_true", help="push/upload/github: 정말 내보내기")
    p.add_argument("--force", action="store_true", help="앞 단계가 끝나지 않았어도 실행 (push/upload/github 제외)")
    p.add_argument("--redownload", action="store_true", help="download: 이미 받은 파일도 다시 받기")
    p.add_argument("--allow-unreadable-live", action="store_true", help="upload: 공개 latest.json을 읽지 못해도 올리기")
    p.add_argument("--force-overwrite-live", action="store_true", help="upload: 같은 버전의 다른 공개 latest.json을 덮어쓰기")
    return p


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except AttributeError:
            pass
    a = parser().parse_args(argv)
    try:
        if a.step == "check" and a.version:
            version_tuple(a.version)
            os.makedirs(RELEASE_ROOT, exist_ok=True)
            with open(current_pointer(), "w", encoding="utf-8") as f:
                f.write(a.version)
        state = load_state(resolve_version(a.version))
        step = next_step(state) if a.step == "next" else a.step
        if step is None:
            say("모든 단계를 마쳤습니다")
        elif step == "status":
            step_status(state, a)
        else:
            if a.step == "next":
                say(f"다음 단계: {step}")
            run_step(step, state, a)
    except ReleaseError as error:
        say(f"멈춤: {error}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
