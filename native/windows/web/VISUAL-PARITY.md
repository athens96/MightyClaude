# Visual parity with the Mac

The page is held to the Mac's help pictures (`release/mac-shots/…/help-shots/ko/<id>-<theme>.png`, taken by CI from the demo
profile). Colours and surfaces come from the design tokens (owner decision); layout, sizes, type, shapes, icons, order and copy
follow the Mac.

## Capture

```
MightyClaude.exe --help-capture --smoke-exit --profile <empty dir under release\> -language ko
```

`MainWindow.HelpCapture.cs` reads `docs/help/shots.json` and the demo profile (both embedded), seeds the profile's workspaces,
panes and runs (the canned CLI lines played by the app's own binary through the real run path, `HelpDemoCli.cs`), sets the
window's content size, drives the page to each screen and saves `<profile>\help-shots\ko\<id>-<theme>.png` (CapturePreviewAsync;
a popover is cut out by its element's box) beside `manifest.json`. The page is drawn with reduced motion, as the Mac's CI
pictures are. A screen it does not draw yet is listed as `skipped`. `MIGHTY_HELP_SHOTS=<id>,<id>` takes only those shots.
The pictures are 1440×900 at 100 % display scale (the Mac's are the same points at 2×).

## Screens

Differences every screen shares: the Mac draws its traffic-light band over the sidebar (Windows has its own title bar above the
page, so the sidebar starts at the search field); paths are this PC's (`C:\…\Projects\my-app`); fonts are Segoe UI Variable and
Cascadia Mono; the Windows sidebar footer keeps its help button and beta badge, and the workspace header keeps a layout-preset
button (the Mac puts the presets in its menu bar, which Windows has not).

| Screen | What changed | What still differs, and why |
|---|---|---|
| welcome | Split-view mark, 27pt title, two-line body, the open-folder and connect buttons with their icons, shortcuts, the platform line at the foot; the sidebar's empty text and its open-folder bar. | Status bar says "실행기 확인 중…" until the runtime is read (the Mac's reads at once). |
| overview | Sidebar to the Mac's metrics (search with icon, dashboard tile, section icons, rows past the folder, add-pane row, app-icon footer); workspace header on two lines with counts and the folder button; pane header without the title (word, mono figures, Default/Mighty switch with icons, ··· menu); shell's slim ink bar with its state pill; brand-coloured provider marks; status glyphs on the Mac's 16-unit grid; terminal follows the page's theme. The Mighty composer's style row ("CLI ⌄ · 자유 요청": the WinUI pane's style chip, its menu and hint, read through the composer state); the sidebar footer's version is the build's (AssemblyInformationalVersion; the project's default is the repo's VERSION). | The grid's row split comes from the Windows layout rules (the Mac's top row is a little taller). |
| sidebar-collapsed | Same frame with the sidebar folded. | — |
| dashboard | Tiles share the row at 34pt, workspace heads with the folder and add-pane capsules, rows with 18pt glyphs, rules from the text's start, beta capsule as the Mac's; the model named as the composer chip names it ("Sonnet 5.5"). | docs-site's two waiting rows can swap: same order rule (state, then last update), the seeded runs end in a different order. |
| agent-basic | Conversation on the card surface, user bubble over the right 85 %, reply cards and tool chips at radius 11, 14pt filled state squares with their glyph. | Reply text wraps a little differently (font metrics). |
| agent-mighty | Mighty bar with icon segments and the zoom (magnifier icons) at its right; blocks with the 28pt head, the kind's symbol in its tint, bold title, bolt, state pill, mono capsule, fold and resize icons; result head as a filled strip; block transcripts without chip borders; the draft cleared as the Mac's capture clears it; the plugin button (puzzle) after the switch in the Mighty view, opening the WinUI plugin browser. Edges end in the Mac's 4×6 open arrowhead (checked at 4× zoom). | — |
| composer | White card at radius 16 on the pane, pills with glyph and chevron (model with the brand mark, effort brain, permission shield, Fast bolt, ··· circle), paperclip circle, resume mark, 24pt context ring, background-work and status-line toggles, red rounded stop beside the blue queue disc. | — |
| session-info | 370-wide popover: mark, title over the agent, state at the right; context box on the accent wash with a 42pt ring; label/value rows, divider, token scope and right-aligned mono figures, notes, session-id disclosure, report lines. | — |
| plan-card | Clipboard badge, expand button with its icon, plain mono code in the plan, hint and the four answers on one line at the right; in a short pane the plan's page gives up height before the answers. Where the hint and the answers do not fit at their own width, the Mac's second layout (ViewThatFits): no hint, Cancel and Revise on one row, the two approvals under them, both at the right. | — |
| question-card | Same card; "하나 선택" in the small caption; the question viewport 300 high as the Mac's ScrollView (yields to 56 in a short pane). | — |
| permission-card | Hand badge; the details viewport 180 high as the Mac's (yields to 40), the raw-JSON disclosure with a chevron. | — |
| terminal-pane | Light Alabaster surface in light theme, no PSReadLine colouring in the canned command, slim bar with "셸 · 로컬 터미널" and the state pill. | Prompt is PowerShell's `my-app > ` (the Mac's zsh shows `%`); footer names ConPTY and PowerShell; no cursor block (the terminal is not focused). |
| files-pane | Tree with chevrons and Lucide symbols (folder in the accent, `</>` for source), preview head with encoding before the Preview/Source segment, plain inline code in the rendered page. | The Windows preview head keeps an open-in-app button beside the folder. |

Not drawn by the Windows capture yet (skipped in its manifest): resume-choice, resume-list, agent-mighty-overview, agent-timeline,
agent-structure, result-card, composer-settings, plan-document, background-work, the settings screens, companion-pet and the
delegation screens.
