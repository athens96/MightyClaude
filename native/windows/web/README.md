# Windows web shell

The Windows app's front end as one React page in a WebView2 (plan: `.omc/plans/windows-web-ui-migration.md`).
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

## Run in the app

Create an empty file `ui-web` in the state folder (`%APPDATA%\MightyClaudeNative`, or the `--profile` folder) and start
the app: the window shows this page instead of the WinUI screens. An empty `web-devtools` file there turns on the
developer tools and the context menu. Smoke: `MIGHTY_SMOKE_WEB=1` with `--smoke-test` checks the page's DOM instead of
the WinUI screens (`MainWindow.WebShell.cs`).

## Develop

`npm run dev` serves the page in a browser with a stand-in host (`src/bridge/devHost.ts`) and fixture panes.

## Layout

- `src/bridge/` — the host protocol (`protocol.ts`, mirrored by `MightyClaude.WinUI/WebBridge.cs`) and the call/push client.
- `src/store/` — wire models (`models.ts`, from the C# records as `Wire.Json` writes them), the pure state reducer and the
  pane deltas (`delta.ts`: a pane's changed fields, and its `logs` / `graphRuns` as dropped, replaced and added records by id;
  `MightyClaude.WinUI/WebDelta.cs` is the host's half).
- `src/conversation/` — the agent pane's conversation: Markdown (`markdown.ts`, the WinUI transcript's subset and limits, no
  dependency), file references and questionnaires ported from Core, the windowed list (`virtual.ts`, `Conversation.tsx`). Host
  methods it calls: `openUrl`, `openReference`, `imageData` (`MainWindow.WebShell.cs`). The provider marks come from
  `mobile/src/lib/provider-marks.ts` (alias `@mobile`).
- `src/composer/` — the agent pane's composer and header figures. The draft is mirrored into a never-shown WinUI pane view
  (`MainWindow.WebShell.Composer.cs`, made the way the phone's are), so send, queue, stop, block reasons, attachments, the
  pills' menus (the WinUI MenuFlyouts read row by row), run settings and session info are the WinUI composer's code; the host
  pushes a `composer` state per pane when it changes. Pure parts here: Enter rules (`keys.ts`), slash palette ranking and keys
  (`slash.ts`, mirroring Core's SlashCommands.cs), attachment limits, the header clock. Host methods: `composerState`,
  `updateDraft`, `send`, `stop`, `queueRemove`, `queueRunNext`, `installCli`, `pickAttachments`, `addAttachments`,
  `removeAttachment`, `dismissAttachmentError`, `attachmentPreview`, `slashCommands`, `slashAction`, `composerMenu`,
  `composerMenuChoose`, `toggleFast`, `runSettings`, `applyRunSettings`, `sessionInfo`, `newConversation`.
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
  changed blocks draw (`data-renders` counts each block's draws); a block out of view draws its header alone. Host methods:
  `mightyVisible`, `mightySetMode`, `mightySetPresentation`, `mightySaveSize`, `mightyExpand`, `mightyResultFiles`, `mightyLoadOlder`,
  `mightyImages`, `mightyTimelineFlip`.
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
- `src/panes/` — the panes that are not a conversation, under the slim bar with their kind in words (spec §4; the layout's pane slot activates on a press). The host side is `MainWindow.WebShell.Panes.cs`. `files/`: the tree and the preview; folders, the filter and opened folders are the workspace's Core FilePaneTree, listings WorkspaceFiles.List under the workspace, previews FilePreviewLoader and SourceHighlighter (the WinUI pane's caps and notes); Markdown renders with `src/conversation`; pictures, SVG and a PDF's first page come as data URLs; up/down/left/right/Enter as the WinUI tree. `terminal/`: xterm.js 6.0.0 and @xterm/addon-fit 0.11.0 as npm packages, the versions vendored in `MightyClaude.WinUI/Assets/Terminal` (MIT, licences there as `xterm-LICENSE`, `addon-fit-LICENSE`), loaded as their own chunk with the first terminal; the shell's or agent terminal's lifecycle stays the never-shown WinUI view's (PseudoTerminal, flush, exit, restart), which pushes `{kind:'terminal'}` (output as base64 UTF-8, the footer when it changes); a remote workspace's shell keeps the conversation and composer. `browser/`: the toolbar and an empty slot; the host lays a WebView2 of the workspace's browser profile over the slot by the device-pixel bounds the page reports, collapsed while the slot is hidden (another tab, a dialog, the pane gone); BrowserAddress and BrowserHistory decide, `{kind:'browser'}` pushes its state. An agent's links open there too. Host methods: `terminalOpen`, `terminalMessage`, `terminalRestart`, `terminalHistory`, `filesState`, `filesToggle`, `filesReveal`, `filesFilter`, `filesSelect`, `filesRefresh`, `filesPreview`, `filesShowInExplorer`, `filesOpen`, `browserOpen`, `browserNavigate`, `browserBack`, `browserForward`, `browserReload`, `browserBounds`, `browserInstall`.
- `src/native/` — the host's WinUI forms drawn by the page (`MainWindow.WebShell.Settings.cs`, `MightyClaude.WinUI/WebNative.cs`): Settings (SettingsNavigation's categories, the saved SettingsPane, each category's sections built by SettingsGroups), the setup screen (BuildSetupContent and its never-show box), the plugin browser, the style approval sheet and every ContentDialog a flow shows (`ShowDialogAsync`: terminal history, model id, the settings, setup, styles, phone and screen-share confirmations, attachments, pictures, the plan document), and the companion popover. Each form is built by the WinUI builder and kept in the window's tree behind the page, invisible, so its Loaded handlers, refreshes and timers run; WebNativeReader reads its controls as a node tree (text, switches, segmented pickers, pop-ups, fields, buttons, pull-down menus, grids and boxes with their token colours, pictures such as the pairing QR as data URLs) pushed as `{kind:'native', id, place, view}` when it changed, and plays the page's input back on the same controls (Invoke, Toggle, SelectedIndex, Text), so every rule stays the section's C#. The text setting comes as `{kind:'text'}` and sets `--text-scale`, the `--type-*` sizes and the families (`theme/text.ts`). The status bar's paw and agent capsule come in the frame's model (`companion`). Host methods: `nativeAction`, `nativeNav`, `nativeButton`, `nativeClose`, `nativeState`, `companionStatus`, `companionToggle`.
- `src/pane/` — one pane (header, conversation or Mighty, cards, composer). `src/i18n/`, `src/theme/` — strings and design tokens.
