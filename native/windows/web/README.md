# Windows web shell

The Windows app's front end as one React page in a WebView2 (plan: `.omc/plans/windows-web-ui-migration.md`).
Vite + React 19 + TypeScript (strict), no UI library. Strings come from the repo's `locales/*.json` and colours and
metrics from `native/contracts/fixtures/design-tokens.json`, both imported where they live (aliases `@locales`, `@tokens`).

## Build

```
npm ci
npm run build        # → dist/, relative asset paths
npm run typecheck
npm test             # vitest: store and deltas, bridge, locale, tokens, conversation (Markdown, references, questionnaire, windowing, views), composer (keys, slash palette, attachments, menus, clock), cards (question steps, card keyboard, the cards push), mighty (graph vectors, layout rules, run sharing, render cost)
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
- `src/shell/`, `src/pane/` — the views. `src/i18n/`, `src/theme/` — strings and design tokens.
