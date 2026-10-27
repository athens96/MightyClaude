# Windows web shell

The Windows app's front end as one React page in a WebView2 (plan: `.omc/plans/windows-web-ui-migration.md`). It is the
window's default front end; the WinUI screens remain behind an escape hatch and as the forms the page reads.
Vite + React 19 + TypeScript (strict), no UI library. Strings come from the repo's `locales/*.json` and colours and
metrics from `native/contracts/fixtures/design-tokens.json`, both imported where they live (aliases `@locales`, `@tokens`).

## Build

```
npm ci
npm run build        # → dist/, relative asset paths
npm run typecheck
npm test             # vitest: store and deltas, bridge, locale, tokens, conversation (Markdown, references, questionnaire, windowing, views), composer (keys, slash palette, attachments, menus, clock), cards (question steps, card keyboard, the cards push), mighty (graph vectors, layout rules, run sharing, render cost), shell (layout geometry, search, shortcuts, chosen pane, dialogs), panes (files tree keys and source spans, terminal keys and output, browser slot bounds, slim kinds), native (views, node placement and controls, text setting)
```

`dotnet build` of `MightyClaude.WinUI` runs `npm ci` (only when `node_modules` is missing) and `npm run build` itself
when a source, a locale file or the tokens changed, and ships `dist/` as `Assets\Web`. `-p:MightySkipWebBuild=true`
skips npm and ships whatever `dist/` holds.

Building needs Node 22 (CI pins it with actions/setup-node).

## Run in the app

The window shows this page (`WebShell.Configure`). An empty `ui-winui` file in the state folder
(`%APPDATA%\MightyClaudeNative`, or the `--profile` folder) brings the WinUI screens back; so does a missing WebView2
runtime, with the `browser.runtime.missing` message in the error banner. The old opt-in file `ui-web` is still read and
changes nothing. An empty `web-devtools` file there turns on the developer tools and the context menu.

## Smoke, perf and CI

- Smoke: `MIGHTY_SMOKE_WEB=1 MightyClaude.exe --smoke-test --smoke-exit --profile <empty dir>` checks the page's DOM
  (`MainWindow.WebShell*.cs`: conversation and the reference bubble opened from a reply's `src/a.ts:3`, composer, cards, Mighty
  views and a structure card revealed in the diagram, frame, panes with the browser stepping aside under a tab menu opened over
  it, Settings and dialogs), then the live steps beside the page (`MainWindow.WebShell.LiveSmoke.cs`, each under its key and in
  `live`: settings forms, setup, plugins, remote workspaces' settings half, updates, delegation, the companion overlay, screen
  sharing, the notifier, picture decoding, a locale-key leak scan). Without the variable a smoke run writes `ui-winui` into its
  profile and runs the WinUI fallback's short smoke (`MainWindow.Smoke.cs`: it starts, shows a Claude and a shell pane, opens
  and closes Settings). `scripts/test-native-windows.ps1 -Web` runs the web smoke and checks each step's keys.
- Perf: `powershell -File scripts/perf-scenario.ps1 -Executable <exe> [-State <folder>] [-Runs N]` measures the page: each
  op (tab clicks, the Default | Mighty switch, the add-pane menu and tab close, typing into the composer during a streamed
  answer) is timed in the page with `performance.now()` until the animation frame after the DOM shows the result; each phase
  keeps the host's stall probe and GC figures and adds `pageLongTasks` (PerformanceObserver 'longtask': count, sum, max,
  over 100/250 ms). Results go to `release/perf/`.
- CI (`.github/workflows/native-windows.yml`): `npm ci`, `npm run typecheck` and `npm test` here before the .NET build;
  the web smoke after the WinUI fallback's, its `smoke-result.json` and pictures uploaded as `MightyClaude-windows-<arch>-web-smoke`. The
  publish (`scripts/build-windows.ps1`) and the installer check require `Assets/Web/index.html`.

## Develop

`npm run dev` serves the page in a browser with a stand-in host (`src/bridge/devHost.ts`) and fixture panes.

## Layout

- `src/bridge/` — the host protocol (`protocol.ts`, mirrored by `MightyClaude.WinUI/WebBridge.cs`) and the call/push client.
- `src/store/` — wire models (`models.ts`, from the C# records as `Wire.Json` writes them), the pure state reducer and the
  pane deltas (`delta.ts`: a pane's changed fields, and its `logs` / `graphRuns` as dropped, replaced and added records by id;
  `MightyClaude.WinUI/WebDelta.cs` is the host's half).
- `src/conversation/` — the agent pane's conversation: Markdown (`markdown.ts`, the WinUI transcript's subset and limits, no
  dependency), file references and questionnaires ported from Core, the windowed list (`virtual.ts`, `Conversation.tsx`). A file
  named in a conversation, a diagram block or a timeline row (`referenceClick.ts`) opens in the pane's reference bubble
  (`src/pane/ReferenceBubble.tsx`, sizing rules ported in `src/pane/reference.ts`), drawn with the files pane's preview. Host
  methods it calls: `openUrl`, `imageData`, `referencePreview`, `referenceOpen`, `referenceReveal`, `referenceSize`
  (`MainWindow.WebShell.cs`). The provider marks come from
  `mobile/src/lib/provider-marks.ts` (alias `@mobile`).
- `src/composer/` — the agent pane's composer and header figures. The draft is mirrored into a never-shown WinUI pane view
  (`MainWindow.WebShell.Composer.cs`, made the way the phone's are), so send, queue, stop, block reasons, attachments, the
  pills' menus (the WinUI MenuFlyouts read row by row), run settings and session info are the WinUI composer's code; the host
  pushes a `composer` state per pane when it changes. Pure parts here: Enter rules (`keys.ts`), slash palette ranking and keys
  (`slash.ts`, mirroring Core's SlashCommands.cs), attachment limits, the header clock. Host methods: `composerState`,
  `updateDraft`, `send`, `stop`, `queueRemove`, `queueRunNext`, `installCli`, `pickAttachments`, `addAttachments`,
  `removeAttachment`, `dismissAttachmentError`, `attachmentPreview`, `slashCommands`, `slashAction`, `composerMenu`,
  `composerMenuChoose`, `toggleFast`, `runSettings`, `applyRunSettings`, `sessionInfo`, `newConversation`.
  `send` takes `{paneId, text, second, attachments, clientId?}` and answers `{accepted, outcome, entryId?, runId?, clientId?, draft,
  state}` (Core `SubmitReceipt.SendAnswer`, docs/interaction.md "보내기 응답").
- `src/cards/` — the cards over the composer (spec §4) and the status line in it (§5): next actions, plan history, background
  work, the waiting tool permission / question / plan (Markdown by `src/conversation`), the Antigravity card, web-open prompts
  and the sign-in card. The host reads them from the same never-shown WinUI pane view (`MainWindow.WebShell.Cards.cs`) and
  pushes `{kind:'cards', paneId, cards}` when they change; every answer goes through the WinUI card's own method. Pure parts
  here: the question card's steps (`questionnaire.ts`) and the card keyboard (`keys.ts`). A next action's words come back from
  the host and reach the composer as the store's `fill`. Host methods: `cards`, `permissionAnswer`, `questionnaireAnswer`,
  `planAnswer`, `antigravityAnswer`, `webOpenChoose`, `loginAction`, `backgroundWork`, `nextActionFill`, `statusLineTrust`.
- `src/mighty/` — the Mighty views in place of the conversation when the pane is in Mighty mode (spec: `.omc/plans/mighty-spec.md`).
  The diagram is laid out here: `layout.ts` and `camera.ts` port MightyGraphLayout, the camera, follow, zoom and wheel, dot grid and
  activity outline rules, `prefs.ts` the saved-size rules, checked against `native/contracts/graph-vectors.json` (alias `@contracts`).
  Each block's copy, the layout's inputs (waiting plan, answered plans, pictures, executions, result files, history), the timeline's
  groups and the pipeline's boards come from the never-shown pane view (`MainWindow.WebShell.Mighty.cs`) as `{kind:'mighty'}`, only
  while the page shows the view, at most every 120 ms, blocks by id. `share.ts` keeps a streamed run's unchanged lists, so only the
  changed blocks draw (`data-renders` counts each block's draws); a block out of view draws its header alone, and a block's
  transcript is windowed as the conversation is. Blocks resize from any border (`resize.ts`, Core ResizeEdgeRules) with the
  opposite corner pinned. The structure view's pan, wheel tracker, column follow and board navigation are ported in
  `structureMotion.ts`; a revealed structure card or an opened timeline row aims the diagram (`Aim` in `Diagram.tsx`). Host
  methods: `mightyVisible`, `mightySetMode`, `mightySetPresentation`, `mightySaveSize`, `mightyExpand`, `mightyResultFiles`,
  `mightyLoadOlder`, `mightyImages`, `mightyTimelineFlip`, `mightyOpenDashboard`.
- `src/shell/`, `src/layout/`, `src/dashboard/`, `src/dialogs/` — the frame (spec: `.omc/plans/shell-spec.md`): sidebar, banners,
  workspace header, status bar and usage popover, the layout of splits and tab groups (each group keeps the panes it has shown
  mounted, hidden behind the selected one, so a tab switch draws no pane again), the dashboard and the empty states, and the
  window's shortcuts (`shortcuts.ts`; the host clears its accelerators). Every word, count and order comes from the host as one
  model (`{kind:'shell'}`, `MainWindow.WebShell.Shell.cs` WebShellModel, read from the WinUI frame's own rules and parts); every
  action is the window's own method. A click shows its pane at once (the store's `chosen`) until the host's state follows. The
  dialogs of the window's flows (remove workspace, delegation, parent close, rename, resume) are pushed as `{kind:'dialog'}` and
  answered with `dialogAnswer`; the flow itself stays the host's. Ported here only for the drag preview and the scroll area:
  PaneLayout's drop zone, share bounds and minimums (`layout/geometry.ts`) and the sidebar search (`shell/filter.ts`). Host
  methods: `shellState`, `selectWorkspace`, `selectSession`, `activatePane`, `showDashboard`, `hideDashboard`, `toggleDisclosure`,
  `setSidebarWidth`, `setSidebarCollapsed`, `applyLayoutPreset`, `resizeSplit`, `resetSplit`, `dockSession`, `closeSession`,
  `renameSession`, `renameWorkspace`, `renameCheck`, `removeWorkspace`, `showInExplorer`, `openFiles`, `focusPane`, `childAction`,
  `addMenu`, `addPaneEntry`, `newPane`, `pickFolder`, `connectRemote`, `openSettings`, `openSetup`, `openHelp`, `setTheme`,
  `textSize`, `dismissError`, `dismissResource`, `restartForUpdate`, `postponeUpdate`, `usageRefresh`, `usageDirect`,
  `dialogAnswer`, `resumeRows`. A notification's click pushes `{kind:'select'}`.
- `src/panes/` — the panes that are not a conversation, under the slim bar with their kind in words (spec §4; the layout's pane slot activates on a press). The host side is `MainWindow.WebShell.Panes.cs`. `files/`: the tree and the preview; folders, the filter and opened folders are the workspace's Core FilePaneTree, listings WorkspaceFiles.List under the workspace, previews FilePreviewLoader and SourceHighlighter (the WinUI pane's caps and notes); Markdown renders with `src/conversation`; pictures, SVG and a PDF's first page come as data URLs; up/down/left/right/Enter as the WinUI tree. `terminal/`: xterm.js 6.0.0 and @xterm/addon-fit 0.11.0 as npm packages, the versions vendored in `MightyClaude.WinUI/Assets/Terminal` (MIT, licences there as `xterm-LICENSE`, `addon-fit-LICENSE`), loaded as their own chunk with the first terminal; the shell's or agent terminal's lifecycle stays the never-shown WinUI view's (PseudoTerminal, flush, exit, restart), which pushes `{kind:'terminal'}` (output as base64 UTF-8, the footer when it changes); a remote workspace's shell keeps the conversation and composer. `browser/`: the toolbar and an empty slot; the host lays a WebView2 of the workspace's browser profile over the slot by the device-pixel bounds the page reports, collapsed while the slot is hidden (another tab, a dialog, a page menu or popover over it — `src/shell/overlay.ts`, the store's `overlays` — the pane gone); BrowserAddress and BrowserHistory decide, `{kind:'browser'}` pushes its state. An agent's links open there too. Host methods: `terminalOpen`, `terminalMessage`, `terminalRestart`, `terminalHistory`, `filesState`, `filesToggle`, `filesReveal`, `filesFilter`, `filesSelect`, `filesRefresh`, `filesPreview`, `filesShowInExplorer`, `filesOpen`, `browserOpen`, `browserNavigate`, `browserBack`, `browserForward`, `browserReload`, `browserBounds`, `browserInstall`.
- `src/native/` — the host's WinUI forms drawn by the page (`MainWindow.WebShell.Settings.cs`, `MightyClaude.WinUI/WebNative.cs`): Settings (SettingsNavigation's categories, the saved SettingsPane, each category's sections built by SettingsGroups), the setup screen (BuildSetupContent and its never-show box), the plugin browser, the style approval sheet and every ContentDialog a flow shows (`ShowDialogAsync`: terminal history, model id, the settings, setup, styles, phone and screen-share confirmations, attachments, pictures, the plan document), and the companion popover. Each form is built by the WinUI builder and kept in the window's tree behind the page, invisible, so its Loaded handlers, refreshes and timers run; WebNativeReader reads its controls as a node tree (text, switches, segmented pickers, pop-ups, fields, buttons, pull-down menus, grids and boxes with their token colours, pictures such as the pairing QR as data URLs) pushed as `{kind:'native', id, place, view}` when it changed, and plays the page's input back on the same controls (Invoke, Toggle, SelectedIndex, Text), so every rule stays the section's C#. The text setting comes as `{kind:'text'}` and sets `--text-scale`, the `--type-*` sizes and the families (`theme/text.ts`). The status bar's paw and agent capsule come in the frame's model (`companion`). Host methods: `nativeAction`, `nativeNav`, `nativeButton`, `nativeClose`, `nativeState`, `companionStatus`, `companionToggle`.
- `src/pane/` — one pane (header, conversation or Mighty, cards, composer). `src/i18n/`, `src/theme/` — strings and design tokens.
- `src/icons/` — the icons in the place of the Mac's SF Symbols: `lucide.ts` holds the Lucide icons the page draws (ISC, `native/licenses/lucide-LICENSE`, shipped in `ThirdPartyLicenses`), generated by `scripts/lucide-icons.mjs` from an unpacked `lucide-static` package (not a dependency); `Icon.tsx` draws one at the Mac's point size. `src/assets/app-icon.png` is the app icon at 64 px for the sidebar's footer.
- Visual parity with the Mac: `VISUAL-PARITY.md` (the help capture, `MightyClaude.exe --help-capture --smoke-exit --profile <empty dir> -language ko`, MainWindow.HelpCapture.cs).

## Known gaps

What the page does not do yet, or does differently from the WinUI screens:

- Settings and other native forms: the generic node-tree renderer (`src/native/`) draws the control kinds WebNativeReader
  knows; custom-drawn WinUI parts outside them are skipped or shown as plain boxes, and layout is approximated. A picture
  decoded from a stream by code that does not hand its bytes to `WebNativeReader.RememberStream` (none is known in the forms
  today) still has no data URL: a BitmapImage cannot be read back.
- Reference bubble: an HTML file is shown as its source, where the WinUI bubble renders it in a local WebView2.
- Structure view: a browser wheel event carries no trackpad gesture, so every wheel event is a step of the tracker; the
  column under the pointer and inner scroll views take the wheel by the browser's own rules.
- Timeline: an opened row aims the diagram when the diagram is shown next (the page keeps one Mighty view mounted).
- Terminal: the WinUI escape hatch's `Assets/Terminal/terminal.js` keeps its own copy of the palette the page now reads
  from the design tokens (`windowsOnly.terminal`).
