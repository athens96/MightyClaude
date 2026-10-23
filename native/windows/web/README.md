# Windows web shell

The Windows app's front end as one React page in a WebView2 (plan: `.omc/plans/windows-web-ui-migration.md`).
Vite + React 19 + TypeScript (strict), no UI library. Strings come from the repo's `locales/*.json` and colours and
metrics from `native/contracts/fixtures/design-tokens.json`, both imported where they live (aliases `@locales`, `@tokens`).

## Build

```
npm ci
npm run build        # → dist/, relative asset paths
npm run typecheck
npm test             # vitest: store and deltas, bridge, locale, tokens, conversation (Markdown, references, questionnaire, windowing, views)
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
- `src/shell/`, `src/pane/` — the views. `src/i18n/`, `src/theme/` — strings and design tokens.
