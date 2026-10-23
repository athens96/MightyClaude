# Windows web shell

The Windows app's front end as one React page in a WebView2 (plan: `.omc/plans/windows-web-ui-migration.md`).
Vite + React 19 + TypeScript (strict), no UI library. Strings come from the repo's `locales/*.json` and colours and
metrics from `native/contracts/fixtures/design-tokens.json`, both imported where they live (aliases `@locales`, `@tokens`).

## Build

```
npm ci
npm run build        # → dist/, relative asset paths
npm run typecheck
npm test             # vitest: store/delta, bridge, locale, tokens
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
- `src/store/` — wire models (`models.ts`, from the C# records as `Wire.Json` writes them) and the pure state reducer.
- `src/shell/`, `src/pane/` — the views. `src/i18n/`, `src/theme/` — strings and design tokens.
