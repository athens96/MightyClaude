#!/usr/bin/env python3
"""Render a task-scoped source-change report as HTML.

    changes-report.py --title "제목" --base <tree-or-ref> --out artifacts/reports/x.changes.html [paths...]

Modified files appear as unified diffs against --base; files that did not
exist at --base appear in full. Run with DEVELOPER_DIR pointing at the
Command Line Tools when Xcode's license is not accepted.
"""
import argparse, html, os, subprocess, sys

CSS = """:root { color-scheme: light dark; --bg:#fff; --fg:#1d1d1f; --muted:#6e6e73; --add:#1a7f37; --addbg:#dafbe1; --del:#cf222e; --delbg:#ffebe9; --hunk:#6639ba; --panel:#f6f8fa; --border:#d0d7de; }
@media (prefers-color-scheme: dark) { :root { --bg:#0d1117; --fg:#e6edf3; --muted:#8b949e; --add:#3fb950; --addbg:#12261e; --del:#f85149; --delbg:#2d1214; --hunk:#a371f7; --panel:#161b22; --border:#30363d; } }
body { margin:0; padding:32px 40px; font:14px/1.5 -apple-system, "Apple SD Gothic Neo", sans-serif; background:var(--bg); color:var(--fg); }
h1 { font-size:20px; margin:0 0 4px; } .sub { color:var(--muted); margin-bottom:20px; }
table { border-collapse:collapse; margin-bottom:28px; } td { padding:3px 12px 3px 0; font-family:ui-monospace, Menlo, monospace; font-size:12px; } a { color:inherit; }
details { border:1px solid var(--border); border-radius:8px; margin-bottom:16px; background:var(--panel); } summary { padding:8px 12px; font-family:ui-monospace, Menlo, monospace; font-size:12px; cursor:pointer; }
pre { margin:0; padding:10px 12px; overflow:auto; max-height:70vh; font:12px/1.45 ui-monospace, Menlo, monospace; background:var(--bg); border-top:1px solid var(--border); }
.add { color:var(--add); } .del { color:var(--del); } .new { color:var(--hunk); }
pre .add { display:block; background:var(--addbg); } pre .del { display:block; background:var(--delbg); } pre .hunk { display:block; color:var(--hunk); }"""

def git(*args):
    return subprocess.run(["git", "-c", "core.pager=cat", *args], capture_output=True, text=True, check=False).stdout

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--title", required=True); parser.add_argument("--base", required=True); parser.add_argument("--out", required=True)
    parser.add_argument("paths", nargs="*")
    args = parser.parse_args()
    base_files = set(git("ls-tree", "-r", "--name-only", args.base).split("\n"))
    changed = [line[3:] for line in git("status", "--short", "--untracked-files=all", "--", *args.paths).split("\n") if line.strip()]
    changed = sorted({p.split(" -> ")[-1] for p in changed if not p.endswith("/")})
    sections, rows = [], []
    for path in changed:
        if not os.path.isfile(path): continue
        if path in base_files:
            diff = git("diff", args.base, "--", path)
            if not diff.strip(): continue
            body = []
            for line in diff.split("\n"):
                cls = "add" if line.startswith("+") and not line.startswith("+++") else "del" if line.startswith("-") and not line.startswith("---") else "hunk" if line.startswith("@@") else ""
                body.append(f'<span class="{cls}">{html.escape(line)}</span>' if cls else html.escape(line))
            added = sum(1 for l in diff.split("\n") if l.startswith("+") and not l.startswith("+++"))
            removed = sum(1 for l in diff.split("\n") if l.startswith("-") and not l.startswith("---"))
            rows.append(f'<tr><td>수정</td><td><a href="#{html.escape(path)}">{html.escape(path)}</a></td><td><span class="add">+{added}</span> <span class="del">−{removed}</span></td></tr>')
            sections.append(f'<details id="{html.escape(path)}" open><summary>수정 · {html.escape(path)}</summary><pre>{chr(10).join(body)}</pre></details>')
        else:
            with open(path, encoding="utf-8", errors="replace") as handle: text = handle.read()
            rows.append(f'<tr><td class="new">새 파일</td><td><a href="#{html.escape(path)}">{html.escape(path)}</a></td><td>{text.count(chr(10))}줄</td></tr>')
            sections.append(f'<details id="{html.escape(path)}"><summary>새 파일 · {html.escape(path)}</summary><pre>{html.escape(text)}</pre></details>')
    page = (f'<!doctype html><html lang="ko"><head><meta charset="utf-8"><title>변경된 소스 · {html.escape(args.title)}</title><style>{CSS}</style></head><body>'
            f'<h1>변경된 소스 · {html.escape(args.title)}</h1><div class="sub">기준: {html.escape(args.base)} · 파일 {len(rows)}개 · 수정 파일은 diff, 새 파일은 전체</div>'
            f'<table>{"".join(rows)}</table>{"".join(sections)}</body></html>')
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as handle: handle.write(page)
    print(f"{args.out}: {len(rows)} files")

if __name__ == "__main__":
    sys.exit(main())
