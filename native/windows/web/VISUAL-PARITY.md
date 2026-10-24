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
pictures are. A screen it does not draw yet is listed as `skipped`. `MIGHTY_HELP_SHOTS=<id>,<id>` takes only those shots;
`MIGHTY_HELP_DOM=1` writes each shot's markup beside its picture. The pictures are 1440×900 at 100 % display scale (the Mac's
are the same points at 2×); a sheet or popover is its own box, as the Mac's sheet and popover windows are.

The capture also writes the demo's Claude session records (the resume list) under `<profile>\help-demo\home`, which it makes
the history home of this process alone (`PaneView.HistoryHomeOverride`), and starts the pet as a normal start does; the pet's
picture is the one its own window last drew (`CompanionOverlay.SavePicture`), as the Mac's is its panel's.

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
| resume-choice | The sheet as the Mac's: mark, title, message, Cancel at the left, Continue… and the accent Start new at the right. | Message wraps a word earlier (font metrics). |
| resume-list | Mark and title over the mono path, the search field with its magnifier, rows with the mark, title and "N분 전 · 요청 N개 · Sonnet 5.5", Show all, the note and Cancel. | — |
| agent-mighty-overview | The whole diagram in the 1500-high window: result with its file panel, request, 실행 and task blocks; the request block's arrow glyph and "요청 2 · ✳ Claude". | Block heights follow the Windows sizing of the canned transcript (a little shorter). |
| agent-timeline | The history card on top; requests with the chevron, "요청 N · ✳ Claude" in the heading font at 16, the state pill, prompt and tally indented under it; rows on 24pt discs with the kind's glyph on 3-wide rails in the next tone, white cards at radius 14 with title, glyph and "kind · capsule", the latest line in mono, running cards with the 2pt run edge; the result card (green strip, mono caption) under every request, open or not. | The disc glyphs are Lucide's nearest (the Mac's are SF Symbols). |
| agent-structure | ‹ 요청 2 / 2 ⌄ ›, the bolt and state pill under it over a rule; column chips (done green ✓, current accent ●, pending grey ○) with counts, joined by a line green while done; the request's words with the reveal button; 실행 with bolt, pill, tally, latest reply and tool calls with their glyphs; 결과를 기다리는 중; the task's small card with its status bar. | — |
| result-card | Result strip without a pill, the files button with its count; the file panel titled with its count, rows with the file glyph, name and mono path. | The link-check block lists one tool row fewer (the canned run's timings and rows are Windows'). |
| composer-settings | The run settings popover: model, permission, limits with their fields, the next-request note and Cancel / Apply. | No popover arrow (the page's popover has none). |
| plan-document | 860×677 sheet: clipboard glyph and 계획 over a rule, Close at the right, the plan padded 12 with plain inline code. | Corners are rounded (the Mac's sheet is square at the top). |
| background-work | Default view with the transcript, the strip with the layers glyph, chevron and eye-off. | — |
| settings-general | Mac header (gear in the accent, title at 17 in the heading font, platform line, grey close disc), Lucide category symbols, grey section titles, theme and language segments and the text pickers filling their row. | Surfaces from the tokens (the Mac's grouped boxes are grey on grey); Windows rows keep their dividers; the delegation switch reads this profile's value. |
| settings-models | Same frame; every pop-up fills its fixed column (the phase models' 220 and the effort's 110, the aliases' 220 at the right). | The registered-name row keeps its label beside the field (the Mac puts it in the placeholder). |
| settings-styles | Same frame; registration buttons, the built-in styles with their source, hash, counts and file. | The built-in styles' path is the Windows resource name. |
| settings-tools | Same frame. | The components list is empty: the Mac capture fakes every component as installed, the Windows one does not probe this PC's tools. |
| settings-cli | Same frame; the three CLIs with their versions and states. | Accounts show "확인 중…": the capture does not fake signed-in accounts as the Mac's does. |
| settings-mobile | Same frame and order. | Windows adds the firewall rule rows; the screen-share section has the Windows controls. |
| settings-companion | Same frame; the pet picker filling its row. | The import / refresh / reset buttons stack (the WinUI form's column); no pet thumbnail beside the picker. |
| settings-about | Same frame; versions read from the build. | Windows lists its keyboard notes under the version. |
| companion-pet | The pet's own window (282×494): the approval bubble (hand, 승인 요청, docs-site · 미리보기 배포, the command in its box, Open / Deny / Allow once, the pager 4 / 4) over the pet. | The bubble keeps the Windows expand button; Allow once is the accent (the Mac's is grey). |
