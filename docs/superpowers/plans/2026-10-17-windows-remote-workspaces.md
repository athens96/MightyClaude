# Windows Remote Workspaces Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Windows app shares its workspaces and opens other computers' workspaces over Tailscale with the Mac's `/v2/` remote-workspace protocol, so Windows→Windows, Windows→Mac and Mac→Windows all work, and every pane shows which OS its host runs.

**Architecture:** A shared fixture folder (`native/contracts/fixtures/remote-v2/`) pins every request, response, wire event and saved `remote` reference. The Mac suite and the Windows Core suite both read it, and the Mac is the reference. Windows Core gets the protocol in `MightyClaude.Core/Remote/`: wire records and validation, a converter between Windows `RunEvent`s and the Mac's wire events, a DPAPI key vault, a client (`RemoteClientService`) over a no-redirect `HttpClient` that re-checks the Tailscale route before every request, and a host (`RemoteHostService`) on a `TcpListener` HTTP/1.1 server bound only to Tailscale addresses. The host runs client requests through its own `RunManager`. `DesktopService` routes remote panes to the client. WinUI gets the Settings page, the sidebar badge, remote agent and command panes, and a loopback smoke step.

**Tech Stack:** C# / .NET 10 (`MightyClaude.Core`, `MightyClaude.Core.Tests`), WinUI 3 (`MightyClaude.WinUI`), Swift 6 + Swift Testing (Mac reference and fixture check), `System.Net.Sockets.TcpListener`, `SocketsHttpHandler`, DPAPI (`crypt32!CryptProtectData`, current user), Node scripts (`check-locales`, `check-design-tokens`, `help/build.mjs`).

## Global Constraints

- **Order.** This plan runs after `docs/superpowers/plans/2026-10-17-windows-phone-direct.md`. That plan adds `native/windows/MightyClaude.Core/Remote/TailscaleAddresses.cs` and `native/windows/MightyClaude.Core/WindowsFirewallRule.cs`. This plan consumes them and never rebuilds them.
- **Before Task 1, reconcile the consumed names.** Run `grep -n "public static" native/windows/MightyClaude.Core/Remote/TailscaleAddresses.cs native/windows/MightyClaude.Core/WindowsFirewallRule.cs`. This plan calls the members listed below. If the phone-direct plan landed them under other names, use the landed names everywhere this plan calls them. Do not add a second helper.
  - `TailscaleAddresses.Contains(string address) → bool`
  - `TailscaleAddresses.Current() → IReadOnlyList<string>`
  - `TailscaleAddresses.Canonical(string address) → string`
  - `WindowsFirewallRuleRunner.ForThisApp()`, then `ApplyAsync(IReadOnlyCollection<int> ports, CancellationToken) → Task<FirewallChangeResult>` (UAC prompt; an empty set removes) and `RemoveAsync(CancellationToken)`. Success is `result.Kind == FirewallChange.Done` (phone-direct plan, Task 8).
  - `WindowsFirewallRule.Arguments(IReadOnlyCollection<int> ports, string program) → IReadOnlyList<string>?`. Pure; null when refused.
- **Protocol.** `/v2/` only, with the header `x-mighty-remote-version: 2` on every request and response. Port 43137. A bearer key of 256 random bits in base64url (43 characters), compared after SHA-256 in constant time. Events are read by cursor, and the poll is the heartbeat.
- **Security as on the Mac.**
  - The host binds only Tailscale addresses.
  - It drops non-Tailscale peers at accept and allows at most 8 open connections per peer.
  - It reads no body byte without the key and refuses any `Origin` header.
  - Rate limits: 300 requests/s per peer, and 600/s in total counting only keyed requests.
  - It serves only shared local workspaces.
  - "원격에서 전체 권한·명령 실행 허용" is off each time sharing starts.
  - A run nobody polls for 30 s stops.
  - The client refuses redirects, uses no proxy or cookies, reads at most 2 MiB per response, gives each exchange 15 s (60 s for a run with attachments), and re-checks the route before every request.
  - The key never goes into a URL, a log, `workspace-state.json` or `remote-connections.json`.
- **Never** launch the app, run `netsh`, touch the firewall, or run `tailscale` in a test or a step. Loopback stands in for Tailscale only through `AllowLoopbackForTests`, which only tests and the smoke run set, in code.
- **WinUI rules.**
  - Write resource keys once (`SetResourcesOnce`).
  - Every `Unloaded` teardown checks `IsLoaded`.
  - Text sizes go through `TextSize`/`TextFont` (font-literal ratchet), colours through `DesignBrushes`, and spacing through `DesignMetrics`.
  - No Korean literals in Core or WinUI sources: every word comes from `Locale.Get`.
  - Code comments stay in English.
  - Smoke code never touches the real clipboard (`clipboardUsed` stays false).
- **Computed properties** on any record that is serialized get `[JsonIgnore]`.
- **No personal IPs or hostnames** in code, fixtures, tests or docs. Use `100.101.102.103`, `fd7a:115c:a1e0::5`, `studio-pc`, `my-mac`, `/Users/example/…`, `C:\work\…`.
- **Commit messages** are `(windows) …`, or `(mac, windows) …` when both apps change. Every commit body ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push.
- **CORE-RUN** (how every Windows Core check in this plan is run on the Mac):

```bash
cd "$(git rev-parse --show-toplevel)"
V=native/windows/MightyClaude.Core.Tests/Verification.cs
perl -0pi -e 's/private static async Task Test\(string name, Func<Task> action\) \{ await action\(\); passed\+\+; Console\.WriteLine\("PASS " \+ name\); \}/private static async Task Test(string name, Func<Task> action) { try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); } }/' "$V"
TMPDIR=/private/tmp/ dotnet run --project native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj > /private/tmp/core-run.log 2>&1; echo "exit $?"
perl -0pi -e 's/private static async Task Test\(string name, Func<Task> action\) \{ try \{ await action\(\); passed\+\+; Console\.WriteLine\("PASS " \+ name\); \} catch \(Exception ex\) \{ Console\.WriteLine\("FAIL " \+ name \+ ": " \+ ex\.GetBaseException\(\)\.Message\); \} \}/private static async Task Test(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }/' "$V"
git diff --stat -- "$V"   # only this task's registrations may remain
grep -c '^FAIL' /private/tmp/core-run.log; grep -E '^(FAIL|PASS) [Rr]emote' /private/tmp/core-run.log
```

  - **Pass:** exactly 9 `FAIL` lines, all Windows-only (ConPTY, Job Object, real PowerShell, and the like), and every `remote …` registration printed as `PASS`.
  - **Expected failure** before an implementation exists: a build error naming the missing type or member (`CS0246`/`CS0117`/`CS1061`), or `FAIL remote …` lines.
- **Mac suites** run with `bash scripts/test-native-macos.sh --filter '<Suite>'`. The unfiltered suite deadlocks locally.

---

## File Structure

**Shared contract (new)**

| Path | Responsibility |
|---|---|
| `native/contracts/fixtures/remote-v2/endpoints.json` | Index: each endpoint's method, path, status, request and response sample |
| `native/contracts/fixtures/remote-v2/{info,start,start-accepted,poll,poll-gap,poll-malformed,steer,permission,questions,plan-approved,plan-revised,stop,answer,error}.json` | One sample per request or response body |
| `native/contracts/fixtures/remote-v2/graph-run-event.json` | The Windows-only `graph_run` wire event, sent only to a client that asks with `x-mighty-remote-graph: run` |
| `native/contracts/fixtures/remote-v2/state-references.json` | Saved `remote` values that are kept (version 2) or dropped (unversioned and other shapes) |
| `native/contracts/README.md` | New section "Remote workspaces (`fixtures/remote-v2/`)" |
| `native/macos/Tests/MightyCoreTests/RemoteWireFixtureTests.swift` | The Mac decodes and re-encodes every sample unchanged |

**Windows Core (new, `native/windows/MightyClaude.Core/Remote/`)**

| File | Responsibility |
|---|---|
| `RemoteWindowsStrings.cs` | Every Windows-only `remote.windows.*` string and the shared `remote.hostOS.*` labels |
| `RemoteWorkspaceModels.cs` | `RemoteProtocol` constants, `RemoteSharedWorkspace`, `RemoteHostState`, `RemoteConnectionInfo`, the wire records, `RemoteHttpFailure`, `RemoteWorkspacePhone` |
| `RemoteWorkspaceValidation.cs` | Key shape, constant-time compare, workspace, `/info`, permission mode, phase models, capability checks |
| `RemoteWireEvents.cs` | Windows `RunEvent`/`ToolPermissionRequest`/`ExecutionGraphNode` ↔ wire event JSON; timestamp normalizing; `RemotePendingPermissions` |
| `RemoteKeyVault.cs` | `IRemoteKeyVault`, `DpapiRemoteKeyVault` (current-user DPAPI), `MemoryRemoteKeyVault` |
| `RemoteConnectionFile.cs` | `remote-connections.json` (version 2, never a key) |
| `RemoteAddress.cs` | `RemoteAddress.Parse`, `RemotePeerPolicy`, `RemoteRoute` (source address toward a host) |
| `RemoteTransport.cs` | `RemoteTarget`, resolve, one authenticated request |
| `RemoteClientService.cs` | Saved connections, connect/refresh/disconnect/remove/open, runs, polling, answers |
| `RemoteHttpListener.cs` | One-request-per-connection HTTP/1.1 server on `TcpListener` |
| `RemoteHostService.cs` | "이 컴퓨터 공유": sharing lifecycle, authorization, `/info`, runs, event buffers, heartbeat |
| `RemoteCommandShell.cs` | `powershell.exe -NoProfile -NonInteractive -Command <text>` from the system folder |
| `RemoteWorkspaceFeatures.cs` | What a remote workspace's panes may not do; host OS label; firewall port set; host runtime stand-in |

**Windows Core (modified):**
- `Models.cs`: `RemoteWorkspaceReference`, `Workspace.Remote`.
- `StateStore.cs`: keep version 2, `ApproveRemote`, `KeepsWorkspace`, and the refusals.
- `RunManager.cs`: refuses remote workspaces; adds `GraphNodes`, `PowerShellCommands` and `IRemoteRunner`.
- `DesktopService.cs` plus a new `DesktopService.Remote.cs`: routing.
- `Remote/MobileDesktopRouter.cs`: the phone does not see remote workspaces.
- `SettingsSections.cs`, `SettingsNavigation.cs`: the remote page.
- `AddPaneMenu.cs`: the remote menu.
- `MightyClaude.Core.Tests.csproj`: embeds the fixtures.

**Windows Core tests (new):** `RemoteWireFixtureVerification.cs`, `RemoteWorkspaceStateVerification.cs`, `RemoteClientVerification.cs`, `RemoteHostVerification.cs`, `RemoteLoopbackVerification.cs`, and `RemoteFakeHost.cs` (a scripted test server).

**WinUI (new):** `MainWindow.RemoteWorkspaces.cs` (Settings page, actions, key copy) and `MainWindow.RemoteWorkspacesSmoke.cs`.

**WinUI (modified):**
- `MainWindow.cs`: construct, shut down, `Copy` stays.
- `MainWindow.Settings.cs`, `MainWindow.SettingsNavigation.cs`: page and glyph.
- `MainWindow.Sidebar.cs`: badge.
- `MainWindow.Shell.cs`: header badge and status bar.
- `MainWindow.Terminal.cs`: no ConPTY for remote command panes.
- `MainWindow.Files.cs`, `MainWindow.Browser.cs`, `MainWindow.Styles.cs`, `MainWindow.AgentIO.cs`, `MainWindow.DashboardWorkspaces.cs`, `MainWindow.Navigation.cs`: gates.
- `MainWindow.UpdateAdmission.cs`, `MainWindow.AutomaticUpdates.cs`: host runs hold updates.
- `MainWindow.Smoke.cs`.
- `scripts/test-native-windows.ps1`.

**Mac (modified, small):**
- `native/macos/Sources/MightyCore/Remote/RemoteWorkspaceModels.swift`: `RemoteHostOS`.
- `native/macos/Sources/MightyClaude/SessionInfoViews.swift` and `WorkspaceView.swift`: show the host OS.

**Locales:** `locales/{ko,en,zh,ja}.json` plus byte-identical copies in `native/macos/Sources/MightyCore/Resources/Locales/` and `mobile/src/locales/`. `docs/i18n.md` is regenerated.

**Docs:** `docs/remote-workspaces.md`, `docs/help/content/{ko,en,zh,ja}/remote.md` and `windows.md`, and `docs/windows-parity.md`.

---

## Stage A — Shared fixtures

### Task 1: The `remote-v2` fixtures and the Mac's check

**Files:**
- Create: `native/contracts/fixtures/remote-v2/*.json` (17 files listed below)
- Create: `native/macos/Tests/MightyCoreTests/RemoteWireFixtureTests.swift`
- Modify: `native/contracts/README.md` (append a section)

**Interfaces:**
- Consumes (Mac, `@testable import MightyCore`):
  - `RemoteWireInfo`, `RemoteWireStart`, `RemoteWireJob`, `RemoteWirePoll`, `RemoteWireSteer`, `RemoteWirePermission`, `RemoteWireQuestions`, `RemoteWirePlan`, `RemoteWireAnswer`
  - `RemoteWorkspaceValidation.event(_:sessionId:) -> Bool`
  - `RemoteWorkspaceValidation.info(_:) throws`
  - `StateRepository.keepsWorkspace(_ value: Any) -> Bool`
- Produces: the fixture files, whose names Tasks 3–5, 9 and 10 read through the embedded resource `MightyClaude.Core.Tests.RemoteV2.<file>`.

**The rule for this task:** the Mac is the reference. If the Mac's re-encoding of a sample differs from it (outside `null` values), fix the sample, never Mac code.

- [ ] **Step 1: Write the fixture files**

`native/contracts/fixtures/remote-v2/endpoints.json`:

```json
[
  { "name": "info", "method": "GET", "path": "/v2/info", "status": 200, "response": "info.json" },
  { "name": "start", "method": "POST", "path": "/v2/runs", "request": "start.json", "status": 202, "response": "start-accepted.json" },
  { "name": "events", "method": "GET", "path": "/v2/runs/{jobId}/events?cursor={cursor}", "status": 200, "response": "poll.json", "note": "the poll is the heartbeat: a run nobody polls for 30 s stops" },
  { "name": "stop", "method": "POST", "path": "/v2/runs/{jobId}/stop", "request": "stop.json", "status": 200, "response": "answer.json" },
  { "name": "steer", "method": "POST", "path": "/v2/runs/{jobId}/steer", "request": "steer.json", "status": 200, "response": "answer.json" },
  { "name": "permission", "method": "POST", "path": "/v2/runs/{jobId}/permission", "request": "permission.json", "status": 200, "response": "answer.json" },
  { "name": "questions", "method": "POST", "path": "/v2/runs/{jobId}/questions", "request": "questions.json", "status": 200, "response": "answer.json" },
  { "name": "plan", "method": "POST", "path": "/v2/runs/{jobId}/plan", "request": "plan-revised.json", "status": 200, "response": "answer.json" },
  { "name": "refused", "method": "GET", "path": "/v2/info", "status": 401, "response": "error.json" }
]
```

`info.json` (a Windows host; the second workspace is a Mac-style path, so both decoders take both shapes):

```json
{
  "protocol": 2,
  "hostId": "6f1c2d3e-0000-4000-8000-000000000001",
  "hostName": "studio-pc",
  "workspaces": [
    { "id": "ws-project", "name": "project", "path": "C:\\work\\project" },
    { "id": "ws-site", "name": "site", "path": "/Users/example/site" }
  ],
  "runtime": {
    "platform": "win32",
    "appVersion": "0.4.30",
    "claudeAvailable": true,
    "claudeVersion": "2.1.300",
    "modelCatalog": { "source": "cli", "models": [ { "value": "default", "displayName": "Default", "description": "The CLI's own choice" } ], "detail": "" },
    "providers": [
      { "id": "claude", "name": "Claude Code", "available": true, "version": "2.1.300", "detail": "Available",
        "modelCatalog": { "source": "cli", "models": [ { "value": "default", "displayName": "Default", "description": "The CLI's own choice" } ], "detail": "" },
        "capabilities": { "effort": true, "permissionModes": ["plan", "manual", "acceptEdits", "auto", "fullAccess"], "maxTurns": true, "maxBudgetUsd": true, "resume": true, "fastMode": false, "webSearch": false, "networkAccess": false, "attachments": true } },
      { "id": "codex", "name": "Codex CLI", "available": false, "detail": "Not installed",
        "modelCatalog": { "source": "fallback", "models": [], "detail": "" },
        "capabilities": { "effort": true, "permissionModes": ["manual", "onRequest", "acceptEdits", "fullAccess"], "maxTurns": false, "maxBudgetUsd": false, "resume": true, "fastMode": true, "webSearch": true, "networkAccess": true, "attachments": true } },
      { "id": "gemini", "name": "Antigravity CLI", "available": false, "detail": "Not installed",
        "modelCatalog": { "source": "fallback", "models": [], "detail": "" },
        "capabilities": { "effort": false, "permissionModes": ["manual", "plan", "acceptEdits", "fullAccess"], "maxTurns": false, "maxBudgetUsd": false, "resume": true, "fastMode": false, "webSearch": false, "networkAccess": false, "attachments": true } }
    ],
    "mods": { "status": "available", "minimumVersion": "2.1.271", "detail": "Available" }
  }
}
```

`start.json` (what a client posts; `registeredModels` is always present, even when empty):

```json
{
  "request": {
    "sessionId": "pane-1",
    "workspaceId": "ws-project",
    "kind": "claude",
    "input": "Summarize README.md",
    "model": "default",
    "provider": "claude",
    "settings": { "effort": "high", "permissionMode": "acceptEdits", "maxTurns": null, "maxBudgetUsd": null },
    "resumeId": "11111111-2222-4333-8444-555555555555",
    "attachments": [ { "id": "att-1", "name": "notes.txt", "mediaType": "text/plain", "dataBase64": "aGVsbG8=" } ]
  },
  "permissionModeOverride": "plan",
  "registeredModels": [ { "name": "claude-opus-4-1", "supportsEffort": true, "supportedEffortLevels": ["low", "high"] } ],
  "todoProgress": { "items": [ { "id": "1", "content": "Read the file", "activeForm": "Reading the file", "status": "in_progress" } ] },
  "phaseModels": {
    "claudeMain": "default", "claudeOpusAlias": "default", "claudeSonnetAlias": "default", "claudeHaikuAlias": "default",
    "claudeSubagentDefault": "claude-sonnet-4-5", "codexReviewModel": "default", "codexSubagentDefault": "default",
    "codexPlanModeReasoningEffort": "default", "claudeMainEffort": "high"
  }
}
```

`start-accepted.json`:

```json
{ "protocol": 2, "jobId": "job-1" }
```

`poll.json` (every event type a host sends, in one poll; timestamps are always `yyyy-MM-ddTHH:mm:ss.fffZ` on the wire):

```json
{
  "protocol": 2, "cursor": 11, "lastCursor": 11, "gap": false, "done": true,
  "events": [
    { "cursor": 1, "event": { "sessionId": "job-1", "type": "status", "status": "running" } },
    { "cursor": 2, "event": { "sessionId": "job-1", "type": "resume", "resumeId": "11111111-2222-4333-8444-555555555555" } },
    { "cursor": 3, "event": { "sessionId": "job-1", "type": "log", "entry": { "id": "log-1", "kind": "assistant", "text": "Reading README.md.", "timestamp": "2026-10-17T01:02:03.456Z", "provider": "claude" } } },
    { "cursor": 4, "event": { "sessionId": "job-1", "type": "activity", "activity": { "id": "act-1", "provider": "claude", "kind": "read", "state": "completed", "toolName": "Read", "summary": "README.md", "durationMs": 12 } } },
    { "cursor": 5, "event": { "sessionId": "job-1", "type": "usage", "usage": { "provider": "claude", "source": "claude.stream-json", "tokenScope": "run", "inputTokens": 1200, "outputTokens": 300, "costUSD": 0.01, "costScope": "run", "updatedAt": "2026-10-17T01:02:04.000Z" } } },
    { "cursor": 6, "event": { "sessionId": "job-1", "type": "permission", "permission": { "id": "perm-1", "runId": "job-1", "toolUseId": "toolu_1", "toolName": "Bash", "inputJSON": "{\"command\":\"ls\"}", "summary": "ls", "state": "pending", "canAllow": true, "canAnswerQuestions": false, "canAnswerPlan": false, "receivedAt": "2026-10-17T01:02:05.000Z" } } },
    { "cursor": 7, "event": { "sessionId": "job-1", "type": "plan", "plan": { "id": "plan-1", "runId": "job-1", "plan": "# Plan\n1. Read", "receivedAt": "2026-10-17T01:02:06.000Z", "decidedAt": "2026-10-17T01:02:07.000Z", "outcome": "approvedAuto" } } },
    { "cursor": 8, "event": { "sessionId": "job-1", "type": "todos", "todos": { "items": [ { "id": "1", "content": "Read the file", "activeForm": "Reading the file", "status": "completed" } ] } } },
    { "cursor": 9, "event": { "sessionId": "job-1", "type": "background", "background": { "tasks": [ { "id": "task-1", "kind": "shell", "description": "npm test", "startedAt": "2026-10-17T01:02:08.000Z", "status": "completed", "endedAt": "2026-10-17T01:02:09.000Z" } ], "turnEnded": true } } },
    { "cursor": 10, "event": { "sessionId": "job-1", "type": "graph", "graph": { "id": "node-1", "runId": "act-1", "kind": "main", "state": "completed", "title": "Request", "entries": [], "updatedAt": "2026-10-17T01:02:10.000Z" } } },
    { "cursor": 11, "event": { "sessionId": "job-1", "type": "status", "status": "completed" } }
  ]
}
```

`poll-gap.json` (cursors 2–7 left the host's buffer; the run ended on a lost sign-in, which is `"auth"` on the wire):

```json
{
  "protocol": 2, "cursor": 9, "lastCursor": 9, "gap": true, "done": true,
  "events": [
    { "cursor": 8, "event": { "sessionId": "job-1", "type": "log", "entry": { "id": "log-8", "kind": "error", "text": "Failed to authenticate", "timestamp": "2026-10-17T01:03:00.000Z" } } },
    { "cursor": 9, "event": { "sessionId": "job-1", "type": "status", "status": "error", "reason": "auth" } }
  ]
}
```

`poll-malformed.json` (cursor 2 decodes but fails validation; the client drops it alone, says so once, and keeps the run):

```json
{
  "protocol": 2, "cursor": 3, "lastCursor": 3, "gap": false, "done": true,
  "events": [
    { "cursor": 1, "event": { "sessionId": "job-1", "type": "log", "entry": { "id": "log-1", "kind": "output", "text": "ok", "timestamp": "2026-10-17T01:04:00.000Z" } } },
    { "cursor": 2, "event": { "sessionId": "job-1", "type": "log", "entry": { "id": "log-2", "kind": "nonsense", "text": "?", "timestamp": "2026-10-17T01:04:01.000Z" } } },
    { "cursor": 3, "event": { "sessionId": "job-1", "type": "status", "status": "completed" } }
  ]
}
```

`steer.json`: `{ "text": "Also check CHANGELOG.md" }`

`permission.json`: `{ "runId": "job-1", "requestId": "perm-1", "allow": true }`

`questions.json`: `{ "runId": "job-1", "requestId": "perm-2", "answers": { "Which file?": { "selectedOptions": ["README.md"], "customText": "and the docs" } } }`

`plan-approved.json`: `{ "runId": "job-1", "requestId": "perm-3", "outcome": "approvedAuto" }`

`plan-revised.json`: `{ "runId": "job-1", "requestId": "perm-3", "outcome": "revised", "feedback": "Keep the tests" }`

`stop.json`: `{}`

`answer.json`: `{ "protocol": 2, "accepted": true }`

`error.json`: `{ "protocol": 2, "error": "The key does not match." }`

`graph-run-event.json` (Windows only; a host sends it only to a client whose `POST /v2/runs` carried `x-mighty-remote-graph: run`, and then sends no `graph` events for that run):

```json
{ "cursor": 4, "event": { "sessionId": "job-1", "type": "graph_run", "graphRun": { "id": "gr-1", "input": "Summarize README.md", "status": "running", "rootEntries": [], "agents": [], "resultEntries": [], "sourceRunID": "gr-1", "provider": "claude" } } }
```

`state-references.json`:

```json
{
  "cases": [
    { "name": "version 2 is a remote workspace", "kept": true,  "workspace": { "id": "far", "name": "Far", "path": "C:\\work\\project", "createdAt": "2026-10-17T00:00:00.000Z", "remote": { "version": 2, "connectionId": "conn-1", "workspaceId": "ws-project", "hostName": "studio-pc" } } },
    { "name": "no remote key is local",          "kept": true,  "workspace": { "id": "near", "name": "Near", "path": "/Users/example/near", "createdAt": "2026-10-17T00:00:00.000Z" } },
    { "name": "an explicit null is local",       "kept": true,  "workspace": { "id": "near2", "name": "Near", "path": "/Users/example/near", "createdAt": "2026-10-17T00:00:00.000Z", "remote": null } },
    { "name": "unversioned (before 2026-09-28)", "kept": false, "workspace": { "id": "old", "name": "Old", "path": "/Users/example/old", "createdAt": "2026-09-01T00:00:00.000Z", "remote": { "connectionId": "conn-1", "workspaceId": "w1", "hostName": "Peer" } } },
    { "name": "version 1",                       "kept": false, "workspace": { "id": "v1", "name": "V1", "path": "/Users/example/v1", "createdAt": "2026-09-01T00:00:00.000Z", "remote": { "version": 1, "connectionId": "conn-1", "workspaceId": "w1", "hostName": "Peer" } } },
    { "name": "version as a string",             "kept": false, "workspace": { "id": "vs", "name": "VS", "path": "/Users/example/vs", "createdAt": "2026-09-01T00:00:00.000Z", "remote": { "version": "2", "connectionId": "conn-1", "workspaceId": "w1", "hostName": "Peer" } } },
    { "name": "version as a boolean",            "kept": false, "workspace": { "id": "vb", "name": "VB", "path": "/Users/example/vb", "createdAt": "2026-09-01T00:00:00.000Z", "remote": { "version": true, "connectionId": "conn-1", "workspaceId": "w1", "hostName": "Peer" } } },
    { "name": "a remote that is not an object",  "kept": false, "workspace": { "id": "vx", "name": "VX", "path": "/Users/example/vx", "createdAt": "2026-09-01T00:00:00.000Z", "remote": "studio-pc" } },
    { "name": "version 3",                       "kept": false, "workspace": { "id": "v3", "name": "V3", "path": "/Users/example/v3", "createdAt": "2026-09-01T00:00:00.000Z", "remote": { "version": 3, "connectionId": "conn-1", "workspaceId": "w1", "hostName": "Peer" } } }
  ]
}
```

- [ ] **Step 2: Write the failing Mac test**

`native/macos/Tests/MightyCoreTests/RemoteWireFixtureTests.swift`:

```swift
import Testing
import Foundation
@testable import MightyCore

/// native/contracts/fixtures/remote-v2/: the remote-workspace wire both apps speak.
/// The Mac is the reference: every sample decodes into the Mac's own type and
/// encodes back to itself (null values aside). The Windows Core reads the same
/// files in RemoteWireFixtureVerification.
struct RemoteWireFixtureTests {
    static let folder: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/remote-v2")
    }()
    static func data(_ name: String) throws -> Data { try Data(contentsOf: folder.appendingPathComponent(name)) }

    /// Nulls removed, so an explicit null and a missing key read the same.
    static func plain(_ value: Any) -> Any {
        if let object = value as? [String: Any] { return object.filter { !($0.value is NSNull) }.mapValues(plain) }
        if let array = value as? [Any] { return array.map(plain) }
        return value
    }
    static func same(_ lhs: Data, _ rhs: Data) throws -> Bool {
        let a = plain(try JSONSerialization.jsonObject(with: lhs)), b = plain(try JSONSerialization.jsonObject(with: rhs))
        return NSDictionary(dictionary: ["v": a]).isEqual(to: ["v": b])
    }
    static func roundTrips<T: Codable>(_ name: String, _ type: T.Type) throws -> Bool {
        let raw = try data(name)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        return try same(raw, encoder.encode(JSONDecoder().decode(T.self, from: raw)))
    }

    @Test func everyBodyIsTheMacsOwnEncoding() throws {
        #expect(try Self.roundTrips("info.json", RemoteWireInfo.self))
        #expect(try Self.roundTrips("start.json", RemoteWireStart.self))
        #expect(try Self.roundTrips("start-accepted.json", RemoteWireJob.self))
        #expect(try Self.roundTrips("poll.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("poll-gap.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("poll-malformed.json", RemoteWirePoll.self))
        #expect(try Self.roundTrips("steer.json", RemoteWireSteer.self))
        #expect(try Self.roundTrips("permission.json", RemoteWirePermission.self))
        #expect(try Self.roundTrips("questions.json", RemoteWireQuestions.self))
        #expect(try Self.roundTrips("plan-approved.json", RemoteWirePlan.self))
        #expect(try Self.roundTrips("plan-revised.json", RemoteWirePlan.self))
        #expect(try Self.roundTrips("answer.json", RemoteWireAnswer.self))
        let error = try JSONSerialization.jsonObject(with: Self.data("error.json")) as? [String: Any]
        #expect(error?["protocol"] as? Int == RemoteProtocol.version && error?["error"] is String)
        #expect((try JSONSerialization.jsonObject(with: Self.data("stop.json")) as? [String: Any])?.isEmpty == true)
    }

    @Test func theIndexNamesEveryRouteAndSample() throws {
        let index = try #require(JSONSerialization.jsonObject(with: Self.data("endpoints.json")) as? [[String: Any]])
        #expect(Set(index.compactMap { $0["name"] as? String }) == ["info", "start", "events", "stop", "steer", "permission", "questions", "plan", "refused"])
        for entry in index {
            #expect((entry["path"] as? String)?.hasPrefix(RemoteProtocol.pathPrefix) == true)
            for key in ["request", "response"] { if let name = entry[key] as? String { #expect(FileManager.default.fileExists(atPath: Self.folder.appendingPathComponent(name).path), "\(name)") } }
        }
    }

    @Test func theMacAcceptsEverySampleEventAndDropsOnlyTheMalformedOne() throws {
        let info = try JSONDecoder().decode(RemoteWireInfo.self, from: Self.data("info.json"))
        try RemoteWorkspaceValidation.info(info)
        #expect(info.runtime.platform == "win32")
        for name in ["poll.json", "poll-gap.json"] {
            let poll = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data(name))
            for item in poll.events { #expect(RemoteWorkspaceValidation.event(item.event, sessionId: "job-1"), "\(name) cursor \(item.cursor)") }
        }
        let malformed = try JSONDecoder().decode(RemoteWirePoll.self, from: Self.data("poll-malformed.json"))
        #expect(malformed.events.map { RemoteWorkspaceValidation.event($0.event, sessionId: "job-1") } == [true, false, true])
        // The Windows-only graph_run event is never one a Mac client takes; Windows hosts send it only on request.
        let graphRun = try #require(JSONSerialization.jsonObject(with: Self.data("graph-run-event.json")) as? [String: Any])
        let event = try JSONDecoder().decode(RunEvent.self, from: JSONSerialization.data(withJSONObject: graphRun["event"]!))
        #expect(!RemoteWorkspaceValidation.event(event, sessionId: "job-1"))
    }

    @Test func savedReferencesAreKeptOnlyAtVersionTwo() throws {
        let file = try #require(JSONSerialization.jsonObject(with: Self.data("state-references.json")) as? [String: Any])
        let cases = try #require(file["cases"] as? [[String: Any]])
        #expect(cases.count == 9)
        for item in cases {
            #expect(StateRepository.keepsWorkspace(item["workspace"]!) == (item["kept"] as? Bool), "\(item["name"] ?? "")")
        }
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `bash scripts/test-native-macos.sh --filter RemoteWireFixtureTests`

Expected before Step 1's files exist: FAIL with "The file "info.json" couldn't be opened". With the files in place, any sample that fails is one that does not match the Mac encoder. Correct the sample until all four tests pass, and leave Mac sources unchanged.

- [ ] **Step 4: Document the fixture in `native/contracts/README.md`**

Append:

````markdown
## Remote workspaces (`fixtures/remote-v2/`)

Tailscale remote workspaces connect Mighty Claude apps in every direction (Mac→Mac,
Mac→Windows, Windows→Mac, Windows→Windows) over one HTTP/1.1 protocol: `/v2/` paths,
`x-mighty-remote-version: 2` on every request and answer, a bearer key, and run events
read by cursor (`docs/remote-workspaces.md`). The Mac is the reference.

- `endpoints.json` is `[{name, method, path, request?, status, response, note?}]`; every
  other file is one body it names. `GET /v2/runs/{id}/events?cursor=N` is also the
  heartbeat: a run nobody polls for 30 s stops.
- `start.json` carries `registeredModels` always (an empty array when there are none),
  and `StartRunRequest` encoded as the Mac encodes it (`settings` with explicit null
  `maxTurns`/`maxBudgetUsd`, `attachments` only when non-empty). A Windows client
  never sends the request's `registeredModels`; they ride beside it.
- Wire events are the Mac's `RunEvent` JSON with `sessionId` = the host's job id.
  Timestamps are `yyyy-MM-ddTHH:mm:ss.fffZ`. A lost sign-in is status `error` with
  `reason: "auth"` (Windows reads it as its own `"authentication"`). A permission's
  input is `inputJSON`. Images are left out (the cache keys name host files).
  `graph` carries an `ExecutionGraphNode`; a Windows client ignores it silently.
  `graph-run-event.json` is Windows-only: a Windows host sends `graph_run` (a
  `MightyGraphRun`) instead of `graph` only to a client whose `POST /v2/runs` carried
  `x-mighty-remote-graph: run`. A Mac host ignores that header.
- `poll-malformed.json` holds one event that decodes but fails validation: the
  client drops it alone, adds one `remote.run.notice.skipped` line, and the run goes on.
- `state-references.json` is `{cases: [{name, kept, workspace}]}`: a saved workspace
  is remote only when `remote.version` is the JSON number 2; any other `remote`
  value drops the workspace with its panes on load.
- `/info` `runtime.platform` (`darwin`, `win32`, `linux`) is the host's OS; both apps
  show it on remote panes.

Readers: `RemoteWireFixtureTests` (Swift), `RemoteWireFixtureVerification` and
`RemoteWorkspaceStateVerification` (Windows Core). Both must agree with every file.
Implementations: `MightyCore/Remote/RemoteWorkspaceModels.swift`, `StateRepository.swift`;
`MightyClaude.Core/Remote/RemoteWorkspaceModels.cs`, `RemoteWireEvents.cs`, `StateStore.cs`.
````

- [ ] **Step 5: Run it to verify it passes**

Run: `bash scripts/test-native-macos.sh --filter 'RemoteWireFixtureTests|RemoteWorkspace[A-Za-z]*Tests'`
Expected: PASS for the 4 new tests, and every existing `RemoteWorkspace*Tests` still passes.

- [ ] **Step 6: Commit**

```bash
git add native/contracts/fixtures/remote-v2 native/contracts/README.md native/macos/Tests/MightyCoreTests/RemoteWireFixtureTests.swift
git commit -m "(mac, windows) the remote-workspace wire is pinned in shared fixtures the Mac decodes and re-encodes unchanged

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
## Stage B — Strings, models and state (Windows Core)

### Task 2: Windows-only strings and the shared host-OS labels

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteWindowsStrings.cs`
- Create: `native/windows/MightyClaude.Core.Tests/RemoteStringsVerification.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (register)
- Modify: `locales/{ko,en,zh,ja}.json`, plus copies in `native/macos/Sources/MightyCore/Resources/Locales/` and `mobile/src/locales/`, and the regenerated `docs/i18n.md`

**Interfaces:**
- Consumes: `Locale.Get(string key)` and `Locale.Get(string key, IReadOnlyDictionary<string, string> values)`.
- Produces: `public static class RemoteWindowsStrings`, used by Tasks 3, 9, 10, 13, 15 and 17–19:
  - Properties: `LocalTailscaleOff`, `ConnectedSaved`, `ConnectedMemory`, `HostLocalOnly`, `HostNoTailscale`, `NotLocal`, `PaneUnavailable`, `SettingsTailscaleOff`, `SettingsShareNote`, `SettingsFirewallNote`, `SettingsScopeKey`, `SettingsAllowFullAccessDescription`, `KeyCopied`, `FirewallAdd`, `FirewallRemove`, `SessionInfoHostOS`
  - `string CommandNote(string host, string? platform)`
  - `string FirewallFailed(string error)`
  - `string HostOS(string? platform)`
  - `string HostWithOS(string host, string? platform)`

- [ ] **Step 1: Write the failing test**

`native/windows/MightyClaude.Core.Tests/RemoteStringsVerification.cs`:

```csharp
using MightyClaude.Core;

internal static class RemoteStringsVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task EveryWindowsOnlyStringResolves()
    {
        var values = new Dictionary<string, string>
        {
            ["localTailscaleOff"] = RemoteWindowsStrings.LocalTailscaleOff, ["connectedSaved"] = RemoteWindowsStrings.ConnectedSaved,
            ["connectedMemory"] = RemoteWindowsStrings.ConnectedMemory, ["hostLocalOnly"] = RemoteWindowsStrings.HostLocalOnly,
            ["hostNoTailscale"] = RemoteWindowsStrings.HostNoTailscale, ["notLocal"] = RemoteWindowsStrings.NotLocal,
            ["paneUnavailable"] = RemoteWindowsStrings.PaneUnavailable, ["settingsTailscaleOff"] = RemoteWindowsStrings.SettingsTailscaleOff,
            ["settingsShareNote"] = RemoteWindowsStrings.SettingsShareNote, ["settingsFirewallNote"] = RemoteWindowsStrings.SettingsFirewallNote,
            ["settingsScopeKey"] = RemoteWindowsStrings.SettingsScopeKey, ["allowFullAccess"] = RemoteWindowsStrings.SettingsAllowFullAccessDescription,
            ["keyCopied"] = RemoteWindowsStrings.KeyCopied, ["firewallAdd"] = RemoteWindowsStrings.FirewallAdd, ["firewallRemove"] = RemoteWindowsStrings.FirewallRemove,
            ["sessionInfoHostOS"] = RemoteWindowsStrings.SessionInfoHostOS,
        };
        foreach (var (name, value) in values)
            Check(value.Length > 0 && !value.StartsWith("remote.", StringComparison.Ordinal), name + " has no text: " + value);
        // Korean is pinned by the runner, so these are the ko.json values.
        Check(RemoteWindowsStrings.HostLocalOnly.Contains("이 PC", StringComparison.Ordinal), "the Windows host says 이 PC, never 이 Mac");
        Check(RemoteWindowsStrings.CommandNote("studio-pc", "win32").Contains("studio-pc", StringComparison.Ordinal) && RemoteWindowsStrings.CommandNote("studio-pc", "win32").Contains("Windows", StringComparison.Ordinal), "the command note names the host and its OS");
        Check(RemoteWindowsStrings.FirewallFailed("denied").Contains("denied", StringComparison.Ordinal), "the firewall failure carries its error");
        return Task.CompletedTask;
    }

    internal static Task HostOSLabelsCoverEveryPlatform()
    {
        Check(RemoteWindowsStrings.HostOS("darwin") == "macOS" && RemoteWindowsStrings.HostOS("win32") == "Windows" && RemoteWindowsStrings.HostOS("linux") == "Linux", "the three platforms have their names");
        Check(RemoteWindowsStrings.HostOS(null) == RemoteWindowsStrings.HostOS("plan9") && RemoteWindowsStrings.HostOS(null).Length > 0, "an unknown platform reads as unknown");
        Check(RemoteWindowsStrings.HostWithOS("my-mac", "darwin") == "my-mac · macOS", "host and OS read as one label");
        return Task.CompletedTask;
    }
}
```

Register it in `Verification.RunAsync`, right after `await Test("saved remote workspaces are dropped with their panes and layout entries", …);`:

```csharp
        await Test("remote strings: every Windows-only string resolves", RemoteStringsVerification.EveryWindowsOnlyStringResolves);
        await Test("remote strings: host OS labels cover every platform", RemoteStringsVerification.HostOSLabelsCoverEveryPlatform);
```

- [ ] **Step 2: Run it to verify it fails**

Run: CORE-RUN.
Expected: the build fails with `CS0103: The name 'RemoteWindowsStrings' does not exist`.

- [ ] **Step 3: Add the keys in all four languages**

Write the script to `/private/tmp/add-remote-windows-keys.mjs`. It sets each key, so it also changes the shared `settings.remote.intro`, which now names Mac and Windows.

```javascript
import fs from 'node:fs';
const keys = {
  'remote.windows.connect.error.localTailscaleOff': {
    ko: '이 PC의 Tailscale이 꺼져 있습니다. Tailscale을 켜고 로그인한 뒤 다시 연결하세요.',
    en: 'Tailscale is off on this PC. Turn it on, sign in, and connect again.',
    zh: '此电脑上的 Tailscale 已关闭。请打开 Tailscale 并登录后重新连接。',
    ja: 'このPCのTailscaleがオフです。Tailscaleをオンにしてログインしてから、もう一度接続してください。' },
  'remote.windows.connect.detail.connectedSaved': {
    ko: '연결됨 · 연결 키는 이 Windows 사용자만 풀 수 있게 암호화해 저장했습니다.',
    en: 'Connected · The connection key is saved encrypted so only this Windows user can read it.',
    zh: '已连接 · 连接密钥已加密保存，只有当前 Windows 用户可以读取。',
    ja: '接続済み · 接続キーは、このWindowsユーザーだけが読めるよう暗号化して保存しました。' },
  'remote.windows.connect.detail.connectedMemory': {
    ko: '연결됨 · 연결 키를 암호화해 저장하지 못해 앱을 끌 때까지만 유지합니다.',
    en: 'Connected · The connection key could not be saved encrypted, so it is kept only until the app quits.',
    zh: '已连接 · 无法加密保存连接密钥，它只保留到应用退出。',
    ja: '接続済み · 接続キーを暗号化して保存できなかったため、アプリを終了するまでだけ保持します。' },
  'remote.windows.host.error.localOnly': {
    ko: '이 PC에 등록된 로컬 워크스페이스만 공유할 수 있습니다.',
    en: 'Only local workspaces registered on this PC can be shared.',
    zh: '只能共享在此电脑上登记的本地工作区。',
    ja: 'このPCに登録されたローカルワークスペースだけを共有できます。' },
  'remote.windows.host.error.noTailscale': {
    ko: '이 PC에 Tailscale 주소가 없습니다. Tailscale을 켜고 로그인한 뒤 다시 시작하세요.',
    en: 'This PC has no Tailscale address. Turn Tailscale on, sign in, and start again.',
    zh: '此电脑没有 Tailscale 地址。请打开 Tailscale 并登录后重新开始。',
    ja: 'このPCにTailscaleアドレスがありません。Tailscaleをオンにしてログインしてから、もう一度開始してください。' },
  'remote.windows.workspace.error.notLocal': {
    ko: '원격 워크스페이스는 그 컴퓨터에서 실행됩니다. 이 PC의 폴더로 쓸 수 없습니다.',
    en: 'A remote workspace runs on its own computer. It cannot be used as a folder on this PC.',
    zh: '远程工作区在它所在的电脑上运行，不能当作此电脑上的文件夹使用。',
    ja: 'リモートワークスペースはそのコンピューターで実行されます。このPCのフォルダーとしては使えません。' },
  'remote.windows.workspace.paneUnavailable': {
    ko: '원격 워크스페이스에서는 파일 창과 브라우저 창을 쓸 수 없습니다. 이 PC의 디스크와 네트워크를 읽기 때문입니다.',
    en: "Files and browser panes are not available in a remote workspace, because they read this PC's disk and network.",
    zh: '远程工作区中不能使用文件窗格和浏览器窗格，因为它们读取的是此电脑的磁盘和网络。',
    ja: 'リモートワークスペースではファイルペインとブラウザーペインを使えません。このPCのディスクとネットワークを読むためです。' },
  'remote.windows.settings.tailscaleOff': {
    ko: '이 PC에 Tailscale 주소가 없습니다. Tailscale을 켜고 로그인하세요.',
    en: 'This PC has no Tailscale address. Turn Tailscale on and sign in.',
    zh: '此电脑没有 Tailscale 地址。请打开 Tailscale 并登录。',
    ja: 'このPCにTailscaleアドレスがありません。Tailscaleをオンにしてログインしてください。' },
  'remote.windows.settings.shareNote': {
    ko: '이 PC의 Tailscale 주소에서만 연결을 받습니다. 연결하는 쪽에 주소와 연결 키를 알려 주세요. Tailscale Serve·Funnel이나 공유기 포트 포워딩은 필요하지 않습니다.',
    en: "Connections are accepted only on this PC's Tailscale addresses. Give the other computer the address and the connection key. Tailscale Serve or Funnel and router port forwarding are not needed.",
    zh: '只在此电脑的 Tailscale 地址上接受连接。请把地址和连接密钥告诉要连接的一方。不需要 Tailscale Serve、Funnel 或路由器端口转发。',
    ja: 'このPCのTailscaleアドレスでだけ接続を受け付けます。接続する側にアドレスと接続キーを伝えてください。Tailscale Serve・Funnelやルーターのポート転送は不要です。' },
  'remote.windows.settings.firewallNote': {
    ko: 'Windows 방화벽은 Tailscale 대역(100.64.0.0/10, fd7a:115c:a1e0::/48)에서 오는 연결에만 이 앱의 포트를 여는 규칙 하나로 엽니다. 규칙을 추가하거나 지울 때 Windows가 관리자 확인 창을 한 번 띄웁니다.',
    en: "Windows Firewall is opened by one rule that allows only this app's ports, and only from Tailscale's ranges (100.64.0.0/10, fd7a:115c:a1e0::/48). Adding or removing the rule shows one Windows administrator prompt.",
    zh: 'Windows 防火墙只通过一条规则打开：仅允许来自 Tailscale 地址段（100.64.0.0/10、fd7a:115c:a1e0::/48）的连接访问本应用的端口。添加或删除该规则时，Windows 会弹出一次管理员确认。',
    ja: 'Windowsファイアウォールは、Tailscaleの範囲（100.64.0.0/10、fd7a:115c:a1e0::/48）からの接続にだけこのアプリのポートを開く1つのルールで開きます。ルールの追加・削除のときにWindowsの管理者確認が1回表示されます。' },
  'remote.windows.settings.scopeKey': {
    ko: '• 연결 키를 가진 기기는 공유한 워크스페이스에서 작업과 명령을 실행할 수 있습니다. 키는 신뢰하는 기기에만 알려 주세요. 키는 이 Windows 사용자만 풀 수 있게 암호화해 저장합니다.',
    en: '• A device with the connection key can run work and commands in the shared workspaces. Give the key only to devices you trust. The key is saved encrypted so only this Windows user can read it.',
    zh: '• 持有连接密钥的设备可以在共享的工作区中执行任务和命令。只把密钥告诉你信任的设备。密钥加密保存，只有当前 Windows 用户可以读取。',
    ja: '• 接続キーを持つデバイスは、共有したワークスペースで作業やコマンドを実行できます。キーは信頼できるデバイスにだけ伝えてください。キーはこのWindowsユーザーだけが読めるよう暗号化して保存します。' },
  'remote.windows.settings.allowFullAccessDescription': {
    ko: '켜면 연결한 컴퓨터가 원격 명령 창(이 PC에서는 PowerShell)을 쓰고, 에이전트를 전체 권한(Claude Bypass, Codex 전체 접근, Antigravity 전체 권한)으로 실행할 수 있습니다. 공유를 시작할 때마다 꺼집니다.',
    en: 'When on, connected computers may use remote command panes (PowerShell on this PC) and run agents with full access (Claude Bypass, Codex full access, Antigravity full access). It is off whenever sharing starts.',
    zh: '打开后，已连接的电脑可以使用远程命令窗格（在此电脑上是 PowerShell），并以完全权限（Claude Bypass、Codex 完全访问、Antigravity 完全权限）运行代理。每次开始共享时都会关闭。',
    ja: 'オンにすると、接続したコンピューターがリモートコマンドペイン（このPCではPowerShell）を使い、エージェントをフルアクセス（Claude Bypass、Codexフルアクセス、Antigravityフルアクセス）で実行できます。共有を開始するたびにオフになります。' },
  'remote.windows.settings.keyCopied': {
    ko: '연결 키를 복사했습니다. 클립보드 기록과 다른 기기에 남지 않고, 60초 뒤에도 그대로면 지웁니다.',
    en: 'Connection key copied. It stays out of clipboard history and other devices, and is cleared after 60 seconds if it is still there.',
    zh: '已复制连接密钥。它不会进入剪贴板历史，也不会同步到其他设备；60 秒后若仍在剪贴板中将被清除。',
    ja: '接続キーをコピーしました。クリップボードの履歴やほかのデバイスには残らず、60秒後もそのままなら消去します。' },
  'remote.windows.firewall.add': { ko: '방화벽 규칙 추가', en: 'Add firewall rule', zh: '添加防火墙规则', ja: 'ファイアウォールルールを追加' },
  'remote.windows.firewall.remove': { ko: '방화벽 규칙 제거', en: 'Remove firewall rule', zh: '删除防火墙规则', ja: 'ファイアウォールルールを削除' },
  'remote.windows.firewall.failed': {
    ko: '방화벽 규칙을 바꾸지 못했습니다: {error}', en: 'Could not change the firewall rule: {error}',
    zh: '无法更改防火墙规则：{error}', ja: 'ファイアウォールルールを変更できませんでした: {error}' },
  'remote.windows.pane.commandNote': {
    ko: '명령은 {host}({os})의 공유 폴더에서 실행됩니다. 명령마다 그 컴퓨터의 셸(Windows는 PowerShell, Mac은 로그인 셸)을 새로 시작하며, 대화형 프로그램과 비밀번호 입력은 지원하지 않습니다.',
    en: 'Commands run in the shared folder on {host} ({os}). Each command starts a new shell there (PowerShell on Windows, the login shell on a Mac); interactive programs and password prompts are not supported.',
    zh: '命令在 {host}（{os}）的共享文件夹中运行。每条命令都会在那台电脑上新开一个 shell（Windows 上是 PowerShell，Mac 上是登录 shell），不支持交互式程序和密码输入。',
    ja: 'コマンドは{host}（{os}）の共有フォルダーで実行されます。コマンドごとにそのコンピューターのシェル（WindowsではPowerShell、Macではログインシェル）を新しく起動し、対話型プログラムやパスワード入力には対応しません。' },
  // Shared by both apps from Task 16 on.
  'remote.hostOS.darwin': { ko: 'macOS', en: 'macOS', zh: 'macOS', ja: 'macOS' },
  'remote.hostOS.win32': { ko: 'Windows', en: 'Windows', zh: 'Windows', ja: 'Windows' },
  'remote.hostOS.linux': { ko: 'Linux', en: 'Linux', zh: 'Linux', ja: 'Linux' },
  'remote.hostOS.unknown': { ko: 'OS 모름', en: 'unknown OS', zh: '未知系统', ja: 'OS不明' },
  'remote.hostWithOS': { ko: '{host} · {os}', en: '{host} · {os}', zh: '{host} · {os}', ja: '{host} · {os}' },
  'remote.sessionInfo.hostOS': { ko: '실행 컴퓨터 OS', en: 'Host OS', zh: '运行电脑的系统', ja: '実行コンピューターのOS' },
  // Changed: the shared intro names both kinds of computer now.
  'settings.remote.intro': {
    ko: 'Tailscale로 연결된 두 컴퓨터(Mac·Windows)의 Mighty Claude를 이어, 한쪽에서 다른 쪽의 워크스페이스를 열고 그 컴퓨터의 CLI로 에이전트와 명령을 실행합니다. 파일은 복사하지 않습니다.',
    en: "Joins Mighty Claude on two computers (Mac or Windows) linked by Tailscale: one opens the other's workspaces and runs agents and commands with that computer's CLIs. Files are not copied.",
    zh: '把通过 Tailscale 相连的两台电脑（Mac 或 Windows）上的 Mighty Claude 连接起来：一方打开另一方的工作区，并用那台电脑的 CLI 运行代理和命令。不会复制文件。',
    ja: 'Tailscaleでつながった2台のコンピューター（Mac・Windows）のMighty Claudeを結び、一方からもう一方のワークスペースを開いて、そのコンピューターのCLIでエージェントとコマンドを実行します。ファイルはコピーしません。' },
};
for (const language of ['ko', 'en', 'zh', 'ja']) {
  const file = `locales/${language}.json`;
  const values = JSON.parse(fs.readFileSync(file, 'utf8'));
  for (const [key, text] of Object.entries(keys)) values[key] = text[language];
  const output = JSON.stringify(values, null, 2) + '\n';
  for (const target of [file, `native/macos/Sources/MightyCore/Resources/Locales/${language}.json`, `mobile/src/locales/${language}.json`]) fs.writeFileSync(target, output);
}
```

Run: `cd "$(git rev-parse --show-toplevel)" && node /private/tmp/add-remote-windows-keys.mjs && rm /private/tmp/add-remote-windows-keys.mjs`

- [ ] **Step 4: Write `RemoteWindowsStrings`**

`native/windows/MightyClaude.Core/Remote/RemoteWindowsStrings.cs`:

```csharp
namespace MightyClaude.Core;

/// <summary>
/// Remote-workspace words only the Windows app says (remote.windows.*: "이 PC", DPAPI, PowerShell, the firewall rule), and the
/// host OS labels both apps show on remote panes (remote.hostOS.*). Everything else reuses the Mac's remote.* and
/// settings.remote.* keys as they are.
/// </summary>
public static class RemoteWindowsStrings
{
    public static string LocalTailscaleOff => Locale.Get("remote.windows.connect.error.localTailscaleOff");
    public static string ConnectedSaved => Locale.Get("remote.windows.connect.detail.connectedSaved");
    public static string ConnectedMemory => Locale.Get("remote.windows.connect.detail.connectedMemory");
    public static string HostLocalOnly => Locale.Get("remote.windows.host.error.localOnly");
    public static string HostNoTailscale => Locale.Get("remote.windows.host.error.noTailscale");
    public static string NotLocal => Locale.Get("remote.windows.workspace.error.notLocal");
    public static string PaneUnavailable => Locale.Get("remote.windows.workspace.paneUnavailable");
    public static string SettingsTailscaleOff => Locale.Get("remote.windows.settings.tailscaleOff");
    public static string SettingsShareNote => Locale.Get("remote.windows.settings.shareNote");
    public static string SettingsFirewallNote => Locale.Get("remote.windows.settings.firewallNote");
    public static string SettingsScopeKey => Locale.Get("remote.windows.settings.scopeKey");
    public static string SettingsAllowFullAccessDescription => Locale.Get("remote.windows.settings.allowFullAccessDescription");
    public static string KeyCopied => Locale.Get("remote.windows.settings.keyCopied");
    public static string FirewallAdd => Locale.Get("remote.windows.firewall.add");
    public static string FirewallRemove => Locale.Get("remote.windows.firewall.remove");
    public static string SessionInfoHostOS => Locale.Get("remote.sessionInfo.hostOS");
    public static string FirewallFailed(string error) => Locale.Get("remote.windows.firewall.failed", new Dictionary<string, string> { ["error"] = error });
    public static string CommandNote(string host, string? platform) => Locale.Get("remote.windows.pane.commandNote", new Dictionary<string, string> { ["host"] = host, ["os"] = HostOS(platform) });

    /// <summary>The host's OS from <c>/info</c> <c>runtime.platform</c>.</summary>
    public static string HostOS(string? platform) => platform switch
    {
        "darwin" => Locale.Get("remote.hostOS.darwin"),
        "win32" => Locale.Get("remote.hostOS.win32"),
        "linux" => Locale.Get("remote.hostOS.linux"),
        _ => Locale.Get("remote.hostOS.unknown"),
    };
    public static string HostWithOS(string host, string? platform) => Locale.Get("remote.hostWithOS", new Dictionary<string, string> { ["host"] = host, ["os"] = HostOS(platform) });
}
```

- [ ] **Step 5: Run the checks to verify they pass**

```bash
cd "$(git rev-parse --show-toplevel)"
node scripts/check-locales.js            # rewrites docs/i18n.md
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)"
(cd mobile && npx jest --silent)
```

Then run CORE-RUN.

Expected:
- `check-locales` reports no missing or unused keys and identical copies.
- jest passes.
- CORE-RUN: `PASS remote strings: …` twice, 9 FAILs.

- [ ] **Step 6: Commit**

```bash
git add locales native/macos/Sources/MightyCore/Resources/Locales mobile/src/locales docs/i18n.md native/windows/MightyClaude.Core/Remote/RemoteWindowsStrings.cs native/windows/MightyClaude.Core.Tests/RemoteStringsVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) remote workspaces have their Windows-only words (이 PC, DPAPI, PowerShell, the firewall rule) and both apps a name for the host's OS

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `Workspace.Remote` version 2 in the Windows state

**Files:**
- Modify: `native/windows/MightyClaude.Core/Models.cs` (after `Workspace`, ~line 28)
- Modify: `native/windows/MightyClaude.Core/StateStore.cs` (`LoadAsync` ~lines 38–69, `ApproveLocal`, `ResolveLocalAsync`, `SaveAsync`, `ValidWorkspace`, `Normalize`)
- Modify: `native/windows/MightyClaude.Core/RunManager.cs` (after `var workspace = await resolveWorkspace(request.WorkspaceId);`, ~line 206)
- Modify: `native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj` (embed the fixtures)
- Create: `native/windows/MightyClaude.Core.Tests/RemoteWorkspaceStateVerification.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (register)

**Interfaces:**
- Consumes: `RemoteWindowsStrings.NotLocal` (Task 2) and `state-references.json` (Task 1).
- Produces:
  - `public sealed record RemoteWorkspaceReference(string ConnectionId, string WorkspaceId, string HostName) { public const int CurrentVersion = 2; public int Version { get; init; } }`
  - `Workspace.Remote { get; init; }`, plus `[JsonIgnore] Workspace.IsRemote`
  - `StateStore.KeepsWorkspace(JsonElement) → bool` (static)
  - `StateStore.RemotePath(string) → bool` (static)
  - `StateStore.ApproveRemote(string connectionId, string hostWorkspaceId, string name, string path, string hostName) → Workspace`
  - `ResolveLocalAsync` refuses a remote workspace.
  - `RunManager` refuses to run in one.

- [ ] **Step 1: Embed the fixtures for every Windows check**

In `MightyClaude.Core.Tests.csproj`, add inside the first `<ItemGroup>` of embedded resources:

```xml
    <EmbeddedResource Include="../../contracts/fixtures/remote-v2/*.json" LogicalName="MightyClaude.Core.Tests.RemoteV2.%(Filename)%(Extension)" />
```

- [ ] **Step 2: Write the failing tests**

`native/windows/MightyClaude.Core.Tests/RemoteWorkspaceStateVerification.cs`:

```csharp
using System.Text.Json;
using MightyClaude.Core;

/// <summary>Workspace.remote version 2 on Windows: the same JSON as the Mac, older shapes dropped, a local workspace never read as remote.</summary>
internal static class RemoteWorkspaceStateVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string message) { try { await action(); } catch (Exception) { return; } throw new InvalidOperationException(message); }
    internal static JsonDocument Fixture(string name)
    {
        using var stream = typeof(RemoteWorkspaceStateVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.RemoteV2." + name) ?? throw new InvalidOperationException(name + " is not embedded");
        return JsonDocument.Parse(stream);
    }

    internal static Task SharedReferenceCasesDecideWhatIsKept()
    {
        using var file = Fixture("state-references.json");
        var cases = file.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Check(cases.Count == 9, "the fixture has 9 cases");
        foreach (var item in cases)
            Check(StateStore.KeepsWorkspace(item.GetProperty("workspace")) == item.GetProperty("kept").GetBoolean(), "case: " + item.GetProperty("name").GetString());
        return Task.CompletedTask;
    }

    internal static async Task VersionTwoLoadsAsRemoteAndOlderReferencesAreDropped()
    {
        var directory = Verification.Temp(); var folder = Verification.Temp();
        try
        {
            var local = JsonSerializer.Serialize(folder, Wire.Json);
            var state = "{\"version\":1,\"workspaces\":["
                + "{\"id\":\"local\",\"name\":\"Local\",\"path\":" + local + "},"
                + "{\"id\":\"far\",\"name\":\"Far\",\"path\":\"C:\\\\work\\\\project\",\"remote\":{\"version\":2,\"connectionId\":\"conn-1\",\"workspaceId\":\"ws-project\",\"hostName\":\"studio-pc\"}},"
                + "{\"id\":\"mac\",\"name\":\"Mac\",\"path\":\"/Users/example/site\",\"remote\":{\"version\":2,\"connectionId\":\"conn-2\",\"workspaceId\":\"ws-site\",\"hostName\":\"my-mac\"}},"
                + "{\"id\":\"old\",\"name\":\"Old\",\"path\":" + local + ",\"remote\":{\"connectionId\":\"conn-1\",\"workspaceId\":\"w1\",\"hostName\":\"Peer\"}},"
                + "{\"id\":\"vs\",\"name\":\"VS\",\"path\":" + local + ",\"remote\":{\"version\":\"2\",\"connectionId\":\"conn-1\",\"workspaceId\":\"w1\",\"hostName\":\"Peer\"}},"
                + "{\"id\":\"vx\",\"name\":\"VX\",\"path\":" + local + ",\"remote\":\"studio-pc\"}],"
                + "\"sessions\":[{\"id\":\"s-local\",\"workspaceId\":\"local\"},{\"id\":\"s-far\",\"workspaceId\":\"far\"},{\"id\":\"s-far-cmd\",\"workspaceId\":\"far\",\"kind\":\"shell\"},{\"id\":\"s-old\",\"workspaceId\":\"old\"},{\"id\":\"s-vx\",\"workspaceId\":\"vx\"}]}";
            await File.WriteAllTextAsync(Path.Combine(directory, "workspace-state.json"), state);
            var store = new StateStore(directory); var loaded = await store.LoadAsync();
            Check(loaded.Workspaces.Select(w => w.Id).SequenceEqual(["local", "far", "mac"]), "kept: " + string.Join(",", loaded.Workspaces.Select(w => w.Id)));
            Check(loaded.Sessions.Select(s => s.Id).SequenceEqual(["s-local", "s-far", "s-far-cmd"]), "panes of dropped workspaces go with them");
            var far = store.GetWorkspace("far");
            Check(far.Remote is { Version: 2, ConnectionId: "conn-1", WorkspaceId: "ws-project", HostName: "studio-pc" } && far.IsRemote && far.Path == "C:\\work\\project", "a Windows host path stays the host's");
            Check(store.GetWorkspace("mac").Path == "/Users/example/site", "a Mac host path is valid on Windows too");
            await store.SaveAsync(loaded);
            var saved = await File.ReadAllTextAsync(Path.Combine(directory, "workspace-state.json"));
            Check(saved.Contains("\"remote\":{", StringComparison.Ordinal) && saved.Contains("\"version\":2", StringComparison.Ordinal) && !saved.Contains("\"old\"", StringComparison.Ordinal), "saved with the version, without the dropped ones");
            Check(!saved.Contains("isRemote", StringComparison.OrdinalIgnoreCase), "the computed IsRemote is never written");
            var again = await new StateStore(directory).LoadAsync();
            Check(again.Workspaces.Single(w => w.Id == "far").Remote == far.Remote, "a reload reads the same reference");
        }
        finally { Directory.Delete(directory, true); Directory.Delete(folder, true); }
    }

    internal static async Task ALocalWorkspaceIsNeverWrittenOrReadAsRemote()
    {
        var directory = Verification.Temp(); var folder = Verification.Temp();
        try
        {
            var store = new StateStore(directory); await store.LoadAsync();
            var local = store.ApproveLocal(folder);
            var remote = store.ApproveRemote("conn-1", "ws-project", "project", "C:\\work\\project", "studio-pc");
            Check(store.ApproveRemote("conn-1", "ws-project", "project", "C:\\work\\project", "studio-pc").Id == remote.Id, "opening the same host workspace again returns the one listed");
            Check(store.ApproveLocal(folder).Id == local.Id && local.Remote is null, "a local folder stays local");
            Check(store.ApproveRemote("conn-2", "ws-project", "project", folder, "other").Id != local.Id, "a remote workspace never takes a local one's place, even at the same path");
            await Reject(() => store.ResolveLocalAsync(remote.Id), "a remote workspace never resolves as a local folder");
            await Reject(() => Task.FromResult(store.ApproveRemote("conn-1", "ws-project", "x", "relative\\path", "studio-pc")), "a relative host path is refused");
            await Reject(() => Task.FromResult(store.ApproveRemote("bad id", "ws-project", "x", "C:\\work", "studio-pc")), "a bad connection id is refused");
            var snapshot = new AppSnapshot { Workspaces = [local, remote] };
            await store.SaveAsync(snapshot);
            await Reject(() => store.SaveAsync(snapshot with { Workspaces = [local, remote with { Remote = null }] }), "a remote workspace cannot be saved as local");
            await Reject(() => store.SaveAsync(snapshot with { Workspaces = [local with { Remote = new("conn-9", "ws", "x") }, remote] }), "a local workspace cannot be saved as remote");
            var runs = new RunManager(_ => Task.FromResult(remote), new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null)), directory, _ => { });
            await Reject(() => runs.StartReachedAsync(new StartRunRequest("pane-1", remote.Id, "shell", "echo hi", [])), "a runner never starts in a remote workspace");
            await runs.DisposeAsync();
        }
        finally { Directory.Delete(directory, true); Directory.Delete(folder, true); }
    }
}
```

Register after the Task 2 lines:

```csharp
        await Test("remote state: the shared reference cases decide what is kept", RemoteWorkspaceStateVerification.SharedReferenceCasesDecideWhatIsKept);
        await Test("remote state: version 2 loads as remote and older references are dropped", RemoteWorkspaceStateVerification.VersionTwoLoadsAsRemoteAndOlderReferencesAreDropped);
        await Test("remote state: a local workspace is never written or read as remote", RemoteWorkspaceStateVerification.ALocalWorkspaceIsNeverWrittenOrReadAsRemote);
```

- [ ] **Step 3: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS0117: 'StateStore' does not contain a definition for 'KeepsWorkspace'` and `CS0246: 'RemoteWorkspaceReference'`.

- [ ] **Step 4: Implement**

`Models.cs`. Add before `public sealed record Workspace`:

```csharp
/// <summary>
/// A workspace that lives on another computer running Mighty Claude, reached over Tailscale (docs/remote-workspaces.md).
/// Saved as the workspace's "remote" value with the Mac's JSON: {version: 2, connectionId, workspaceId, hostName}.
/// <see cref="StateStore.KeepsWorkspace"/> drops any other shape, including the unversioned one written before 2026-09-28.
/// </summary>
public sealed record RemoteWorkspaceReference(string ConnectionId, string WorkspaceId, string HostName)
{
    public const int CurrentVersion = 2;
    public int Version { get; init; } = CurrentVersion;
}
```

Inside `Workspace`, after `ModelDefaults`:

```csharp
    // Set when the folder is on another computer; Path is then the host's path and never a local folder.
    public RemoteWorkspaceReference? Remote { get; init; }
    [JsonIgnore] public bool IsRemote => Remote is not null;
```

`StateStore.cs`, in `LoadAsync`, replace the block from `var restored = json.RootElement.Deserialize<AppSnapshot>(…)` through `Snapshot = Normalize(restored with { Workspaces = … }, true);` with:

```csharp
                // A workspace whose "remote" value is not today's (version 2) names a folder on another computer in a shape
                // this app does not read; it is left out before decoding, and Normalize then drops everything keyed by its id.
                var root = System.Text.Json.Nodes.JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
                root["workspaces"] = new System.Text.Json.Nodes.JsonArray(workspaces.EnumerateArray().Where(KeepsWorkspace).Select(w => System.Text.Json.Nodes.JsonNode.Parse(w.GetRawText())).ToArray());
                var restored = root.Deserialize<AppSnapshot>(Wire.Json) ?? throw new InvalidDataException(Locale.Get("store.error.noSnapshot"));
                // Once per profile, on load: `gemini` panes now run Antigravity (AntigravityMigration).
                if (restored.AntigravityMigrated != true) restored = restored with { Sessions = (restored.Sessions ?? []).Select(s => s is null ? s! : AntigravityMigration.Migrated(s)).ToList(), AntigravityMigrated = true };
                Snapshot = Normalize(restored, true);
```

In `ApproveLocal`, change the lookup to `approved.Values.FirstOrDefault(w => w.Remote is null && w.Path == path)`.

Add after `ApproveLocal`:

```csharp
    /// <summary>
    /// A workspace another computer shares (RemoteClientService): its path is the host's and is never checked or used
    /// here. Opening the same host workspace through the same connection again returns the one already listed.
    /// </summary>
    public Workspace ApproveRemote(string connectionId, string hostWorkspaceId, string name, string path, string hostName)
    {
        var host = Wire.Clean(hostName, 120).Trim();
        if (!Wire.Identifier(connectionId) || !Wire.Identifier(hostWorkspaceId) || !RemotePath(path) || host.Length == 0) throw new ArgumentException(Locale.Get("remote.workspace.error.badWorkspace"));
        lock (approved)
        {
            var existing = approved.Values.FirstOrDefault(w => w.Remote?.ConnectionId == connectionId && w.Remote.WorkspaceId == hostWorkspaceId);
            var item = new Workspace { Id = existing?.Id ?? Wire.Id(), Name = Wire.Clean(name, 120), Path = path, CreatedAt = existing?.CreatedAt ?? Wire.Now(), Remote = new(connectionId, hostWorkspaceId, host) };
            approved[item.Id] = item;
            return item;
        }
    }
```

In `ResolveLocalAsync`, first line after `var workspace = GetWorkspace(id);`:

```csharp
        if (workspace.Remote is not null) throw new InvalidOperationException(RemoteWindowsStrings.NotLocal);
```

In `SaveAsync`, change the approval check to:

```csharp
                    if (!approved.TryGetValue(workspace.Id, out var original) || workspace.Path != original.Path || workspace.Remote != original.Remote) throw new InvalidOperationException(Locale.Get("store.error.pathNotApproved"));
```

Replace `ValidWorkspace` and add `KeepsWorkspace` and `RemotePath`:

```csharp
    public static bool ValidWorkspace(Workspace w) => w is not null && Wire.Identifier(w.Id) && w.Path is { Length: > 0 and <= 4096 } && !w.Path.Contains('\0')
        && (w.Remote is not { } remote ? Path.IsPathFullyQualified(w.Path)
            : remote.Version == RemoteWorkspaceReference.CurrentVersion && Wire.Identifier(remote.ConnectionId) && Wire.Identifier(remote.WorkspaceId) && !string.IsNullOrWhiteSpace(remote.HostName) && RemotePath(w.Path));

    /// <summary>A host's folder: absolute on the host's own OS (a Mac's "/…", a Windows drive or UNC path), whatever this OS calls absolute.</summary>
    public static bool RemotePath(string? path) => path is { Length: > 0 and <= 4096 } && !path.Contains('\0')
        && (path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:[\\/]"));

    /// <summary>
    /// Whether a saved workspace object is kept on load (M/StateRepository.keepsWorkspace): a local one (no "remote", or an
    /// explicit null), or a remote one whose reference carries version 2 as a JSON number.
    /// </summary>
    public static bool KeepsWorkspace(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (!value.TryGetProperty("remote", out var remote) || remote.ValueKind == JsonValueKind.Null) return true;
        return remote.ValueKind == JsonValueKind.Object && remote.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var number) && number == RemoteWorkspaceReference.CurrentVersion;
    }
```

In `Normalize`, change the workspace `Select` to:

```csharp
.Select(w => w with { Name = Wire.Clean(w.Name, 120), Remote = w.Remote is { } r ? r with { HostName = Wire.Clean(r.HostName, 120) } : null })
```

`RunManager.cs`, right after `var workspace = await resolveWorkspace(request.WorkspaceId); token.ThrowIfCancellationRequested();`:

```csharp
            // A remote workspace's path is the host's: it never becomes a local working folder (M/ProcessRunner.start).
            if (workspace.Remote is not null) throw new InvalidOperationException(Locale.Get("run.error.approvedWorkspaceOnly"));
```

- [ ] **Step 5: Run them to verify they pass**

Run: CORE-RUN.
Expected:
- `PASS remote state: …` three times.
- The existing `saved remote workspaces are dropped with their panes and layout entries` still passes, because its reference is unversioned.
- 9 FAILs.

- [ ] **Step 6: Commit**

```bash
git add native/windows/MightyClaude.Core/Models.cs native/windows/MightyClaude.Core/StateStore.cs native/windows/MightyClaude.Core/RunManager.cs native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj native/windows/MightyClaude.Core.Tests/RemoteWorkspaceStateVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a workspace saved with remote version 2 comes back as another computer's, older remote shapes are still dropped, and no local folder is ever read or run as remote

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 4: Wire records and validation, checked against the fixtures

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteWorkspaceModels.cs`
- Create: `native/windows/MightyClaude.Core/Remote/RemoteWorkspaceValidation.cs`
- Create: `native/windows/MightyClaude.Core.Tests/RemoteWireFixtureVerification.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (register)

**Interfaces:**
- Consumes:
  - `StateStore.RemotePath` (Task 3)
  - `RunSettings`, `RunAttachment`, `RegisteredModelEntry`, `TodoProgress`, `PhaseModelsSnapshot`, `RuntimeInfo`, `UserQuestionAnswer`, `PlanDecision`, `PlanOutcome`
- Produces:
  - **`RemoteProtocol` constants:**
    - `Version = 2`, `Header = "x-mighty-remote-version"`, `PathPrefix = "/v2/"`, `DefaultPort = 43137`
    - `HeartbeatTimeout = 30 s`, `PollRetryWindow = 10 s`, `RequestTimeout = 15 s`, `UploadTimeout = 60 s`
    - `MaximumSharedWorkspaces = 64`, `MaximumRuns = 16`, `MaximumConnectionsPerPeer = 8`, `MaximumPendingPermissions = 32`, `MaximumConnections = 16`
    - `MaximumRequestBytes = 512 KiB`, `MaximumResponseBytes = 2 MiB`, `MaximumPollBytes = 1536 KiB`, `MaximumPollEvents = 256`, `MaximumBufferedBytes = 4 MiB`, `MaximumBufferedEvents = 4096`
    - `GraphHeader = "x-mighty-remote-graph"`, `GraphRuns = "run"`
  - **Records:**
    - `RemoteSharedWorkspace(Id, Name, Path)`
    - `RemoteHostState { Enabled, Addresses, Port, Key, WorkspaceIds, ActiveRuns, AllowsFullAccess }`
    - `enum RemoteConnectionStatus { Saved, Connected, Disconnected }`
    - `RemoteConnectionInfo(Id, Name, Address, Status, HostName?, Workspaces?, Runtime?, Detail?)`
    - `RemoteWireInfo`, `RemoteWireRequest`, `RemoteWireModel`, `RemoteWireStart`
    - `RemoteWireJob`, `RemoteWireEvent(int Cursor, JsonObject Event)`, `RemoteWirePoll`
    - `RemoteWireSteer`, `RemoteWirePermission`, `RemoteWireQuestions`, `RemoteWirePlan`, `RemoteWireAnswer`, `RemoteWireError`
  - **`RemoteHttpFailure(int status, string message) : Exception`**, with `.Status` and `.Transient`.
  - **`RemoteWorkspacePhone.Workspaces(...)` / `.Sessions(...)`**
  - **`RemoteWorkspaceValidation`:**
    - `Key(string?) → bool`
    - `KeysMatch(string presented, string expected) → bool`
    - `SharedWorkspace(RemoteSharedWorkspace?) → bool`
    - `PermissionMode(string mode, string provider) → bool`
    - `Info(RemoteWireInfo?)`, which throws `InvalidDataException`
    - `PhaseModels(PhaseModelsSnapshot) → bool`
    - `CapabilityProblem(StartRunRequest, ProviderCapabilities) → string?`
    - `EventTypes : IReadOnlySet<string>`

- [ ] **Step 1: Write the failing test**

`native/windows/MightyClaude.Core.Tests/RemoteWireFixtureVerification.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// <summary>native/contracts/fixtures/remote-v2/ read by the Windows Core: every body decodes into the Windows wire type and encodes back to the Mac's own JSON (null values aside).</summary>
internal static class RemoteWireFixtureVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static JsonNode Node(string name)
    {
        using var stream = typeof(RemoteWireFixtureVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.RemoteV2." + name) ?? throw new InvalidOperationException(name + " is not embedded");
        return JsonNode.Parse(stream)!;
    }
    /// <summary>Nulls removed, so an explicit null and a missing key compare the same (the Mac's rule in RemoteWireFixtureTests).</summary>
    internal static JsonNode? Plain(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Where(p => p.Value is not null).Select(p => KeyValuePair.Create(p.Key, Plain(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Plain).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };
    internal static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(Plain(a), Plain(b));
    private static T Decode<T>(string name) => Node(name).Deserialize<T>(Wire.Json) ?? throw new InvalidOperationException(name + " decodes to null");
    private static void RoundTrips<T>(string name) => Check(Same(Node(name), JsonSerializer.SerializeToNode(Decode<T>(name), Wire.Json)), name + " changed through " + typeof(T).Name + ": " + JsonSerializer.Serialize(Decode<T>(name), Wire.Json));

    internal static Task EveryBodyIsTheMacsShape()
    {
        RoundTrips<RemoteWireInfo>("info.json");
        RoundTrips<RemoteWireStart>("start.json");
        RoundTrips<RemoteWireJob>("start-accepted.json");
        RoundTrips<RemoteWirePoll>("poll.json");
        RoundTrips<RemoteWirePoll>("poll-gap.json");
        RoundTrips<RemoteWirePoll>("poll-malformed.json");
        RoundTrips<RemoteWireSteer>("steer.json");
        RoundTrips<RemoteWirePermission>("permission.json");
        RoundTrips<RemoteWireQuestions>("questions.json");
        RoundTrips<RemoteWirePlan>("plan-approved.json");
        RoundTrips<RemoteWirePlan>("plan-revised.json");
        RoundTrips<RemoteWireAnswer>("answer.json");
        RoundTrips<RemoteWireError>("error.json");
        // A client built from a pane's request writes the same body the Mac's client does.
        var start = Decode<RemoteWireStart>("start.json");
        var pane = start.ToRequest("pane-1", "ws-project");
        Check(Same(Node("start.json"), JsonSerializer.SerializeToNode(RemoteWireStart.From(pane, "ws-project"), Wire.Json)), "From(ToRequest(start)) is start");
        var bare = RemoteWireStart.From(new StartRunRequest("pane-2", "local-ws", "shell", "dir", []), "ws-project");
        Check(JsonSerializer.SerializeToNode(bare, Wire.Json)!["registeredModels"] is JsonArray { Count: 0 }, "registeredModels is always sent, even empty");
        Check(JsonSerializer.SerializeToNode(bare, Wire.Json)!["request"]!["registeredModels"] is null, "the request itself never carries registeredModels");
        return Task.CompletedTask;
    }

    internal static Task EverySampleIsAcceptedByTheWindowsChecks()
    {
        var info = Decode<RemoteWireInfo>("info.json");
        RemoteWorkspaceValidation.Info(info);
        Check(info.Runtime.Platform == "win32" && info.Workspaces.All(RemoteWorkspaceValidation.SharedWorkspace), "a Windows path and a Mac path are both a host's folder");
        var bad = info with { Runtime = info.Runtime with { Platform = "plan9" } };
        try { RemoteWorkspaceValidation.Info(bad); throw new InvalidOperationException("an unknown platform must be refused"); } catch (InvalidDataException) { }
        var start = Decode<RemoteWireStart>("start.json");
        var request = start.ToRequest("job-1", "ws-project").Validate();
        Check(request.PermissionModeOverride == "plan" && request.TodoProgress is { Items.Count: 1 } && request.PhaseModels is { ClaudeSubagentDefault: "claude-sonnet-4-5" } && request.RegisteredModels.Single().Name == "claude-opus-4-1", "the extras ride beside the request");
        Check(RemoteWorkspaceValidation.PhaseModels(start.PhaseModels!), "the sample's phase models are valid");
        Check(!RemoteWorkspaceValidation.PhaseModels(start.PhaseModels! with { ClaudeMain = "bad model!" }) && !RemoteWorkspaceValidation.PhaseModels(start.PhaseModels! with { ClaudeMainEffort = "ludicrous" }), "a bad model or effort is refused");
        var claude = info.Runtime.Providers.Single(p => p.Id == "claude").Capabilities;
        Check(RemoteWorkspaceValidation.CapabilityProblem(request, claude) is null, "the host supports the sample's settings");
        Check(RemoteWorkspaceValidation.CapabilityProblem(request with { Settings = request.Settings! with { FastMode = true } }, claude) is not null, "a setting the host lacks is refused");
        Check(Decode<RemoteWirePlan>("plan-approved.json").Decision() == PlanDecision.ApproveAutoEdit && Decode<RemoteWirePlan>("plan-revised.json").Decision() is { Kind: "revise", Feedback: "Keep the tests" }, "plan outcomes map to decisions");
        Check((Decode<RemoteWirePlan>("plan-revised.json") with { Feedback = null }).Decision() is null, "revised without feedback is no decision");
        Check(RemoteWirePlan.From("job-1", "perm-3", PlanDecision.Revise("Keep the tests")) == Decode<RemoteWirePlan>("plan-revised.json"), "a revise decision writes the sample");
        var key = new string('A', 43);
        Check(RemoteWorkspaceValidation.Key(key) && !RemoteWorkspaceValidation.Key(key[..42]) && !RemoteWorkspaceValidation.Key(key + "+") && !RemoteWorkspaceValidation.Key(new string('A', 129)), "key shape");
        Check(RemoteWorkspaceValidation.KeysMatch(key, key) && !RemoteWorkspaceValidation.KeysMatch(key, key + "B") && !RemoteWorkspaceValidation.KeysMatch("", key), "keys compare whole");
        Check(RemoteWorkspaceValidation.PermissionMode("auto", "claude") && !RemoteWorkspaceValidation.PermissionMode("auto", "codex") && RemoteWorkspaceValidation.PermissionMode("onRequest", "codex") && !RemoteWorkspaceValidation.PermissionMode("onRequest", "gemini"), "permission modes per provider");
        Check(new RemoteHttpFailure(429, "busy").Transient && new RemoteHttpFailure(503, "busy").Transient && !new RemoteHttpFailure(401, "key").Transient, "429 and 503 are passing failures");
        return Task.CompletedTask;
    }
}
```

Register after the Task 3 lines:

```csharp
        await Test("remote wire: every body is the Mac's shape", RemoteWireFixtureVerification.EveryBodyIsTheMacsShape);
        await Test("remote wire: every sample is accepted by the Windows checks", RemoteWireFixtureVerification.EverySampleIsAcceptedByTheWindowsChecks);
```

- [ ] **Step 2: Run it to verify it fails**

Run: CORE-RUN.
Expected: the build fails with `CS0246: The type or namespace name 'RemoteWireInfo' could not be found`.

- [ ] **Step 3: Write the records**

`native/windows/MightyClaude.Core/Remote/RemoteWorkspaceModels.cs`:

```csharp
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>
/// Tailscale remote workspaces (docs/remote-workspaces.md): one Mighty Claude opens a workspace another computer's Mighty Claude
/// shares and runs agents and commands there. The protocol is the Mac's (M/Remote/RemoteWorkspaceModels.swift), pinned by
/// native/contracts/fixtures/remote-v2/. The host is RemoteHostService, the client RemoteClientService.
/// </summary>
public static class RemoteProtocol
{
    /// <summary>Sent and required as x-mighty-remote-version. The remote workspaces removed on 2026-09-28 spoke version 1 under /v1/.</summary>
    public const int Version = 2;
    public const string Header = "x-mighty-remote-version";
    public const string PathPrefix = "/v2/";
    public const int DefaultPort = 43137;
    /// <summary>A run the client has not polled for this long is stopped on the host.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    /// <summary>A client asks again this long after a poll failed for a passing reason before it calls the connection lost.</summary>
    public static readonly TimeSpan PollRetryWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(60);
    public const int MaximumSharedWorkspaces = 64, MaximumRuns = 16, MaximumConnectionsPerPeer = 8, MaximumPendingPermissions = 32, MaximumConnections = 16;
    public const int MaximumRequestBytes = 512 * 1024, MaximumResponseBytes = 2 * 1024 * 1024, MaximumPollBytes = 1_536 * 1024, MaximumPollEvents = 256;
    public const int MaximumBufferedBytes = 4 * 1024 * 1024, MaximumBufferedEvents = 4096;
    /// <summary>A Windows client asks a Windows host for graph_run events (MightyGraphRun) instead of graph nodes; a Mac host ignores it.</summary>
    public const string GraphHeader = "x-mighty-remote-graph";
    public const string GraphRuns = "run";
}

/// <summary>A workspace the host shares, as the client lists it.</summary>
public sealed record RemoteSharedWorkspace(string Id, string Name, string Path);

/// <summary>What "이 컴퓨터 공유" shows. Sharing is always off when the app starts.</summary>
public sealed record RemoteHostState
{
    public bool Enabled { get; init; }
    /// <summary>Where clients reach this computer, address:port, one per Tailscale address.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public int? Port { get; init; }
    /// <summary>The connection key while sharing; stopping revokes it for good.</summary>
    public string? Key { get; init; }
    public IReadOnlyList<string> WorkspaceIds { get; init; } = [];
    public int ActiveRuns { get; init; }
    /// <summary>"원격에서 전체 권한·명령 실행 허용": off every time sharing starts.</summary>
    public bool AllowsFullAccess { get; init; }
}

public enum RemoteConnectionStatus { Saved, Connected, Disconnected }

/// <summary>One saved "다른 컴퓨터 연결" entry. Its key is in the DPAPI vault, never here.</summary>
public sealed record RemoteConnectionInfo(string Id, string Name, string Address, RemoteConnectionStatus Status = RemoteConnectionStatus.Saved, string? HostName = null,
    IReadOnlyList<RemoteSharedWorkspace>? Workspaces = null, RuntimeInfo? Runtime = null, string? Detail = null)
{
    [JsonIgnore] public IReadOnlyList<RemoteSharedWorkspace> Shared => Workspaces ?? [];
}

// Wire, with the Mac's JSON (Wire.Json writes camelCase and leaves nulls out).

public sealed record RemoteWireInfo(int Protocol, string HostId, string HostName, IReadOnlyList<RemoteSharedWorkspace> Workspaces, RuntimeInfo Runtime);

/// <summary>A pane's request as the Mac's StartRunRequest encodes it: never registeredModels or anything local.</summary>
public sealed record RemoteWireRequest(string SessionId, string WorkspaceId, string Kind, string Input, string Model, string Provider, RunSettings Settings, string? ResumeId = null, IReadOnlyList<RunAttachment>? Attachments = null)
{
    public static RemoteWireRequest From(StartRunRequest request, string hostWorkspaceId) =>
        new(request.SessionId, hostWorkspaceId, request.Kind, request.Input, request.Model, request.Provider, request.Settings ?? new(), request.ResumeId, request.Attachments);
}

/// <summary>RegisteredModelEntry as the Mac writes it: supportedEffortLevels always present.</summary>
public sealed record RemoteWireModel(string Name, bool SupportsEffort, string[] SupportedEffortLevels)
{
    public static RemoteWireModel From(RegisteredModelEntry entry) => new(entry.Name, entry.SupportsEffort, entry.SupportedEffortLevels ?? []);
    public RegisteredModelEntry ToEntry() => new(Name, SupportsEffort, SupportedEffortLevels);
}

/// <summary>A run request: what a pane saves, plus what the app adds per run beside it.</summary>
public sealed record RemoteWireStart(RemoteWireRequest Request, IReadOnlyList<RemoteWireModel> RegisteredModels, string? PermissionModeOverride = null, TodoProgress? TodoProgress = null, PhaseModelsSnapshot? PhaseModels = null)
{
    public static RemoteWireStart From(StartRunRequest request, string hostWorkspaceId) =>
        new(RemoteWireRequest.From(request, hostWorkspaceId), request.RegisteredModels.Select(RemoteWireModel.From).ToList(), request.PermissionModeOverride, request.TodoProgress, request.Kind == "claude" ? request.PhaseModels : null);

    /// <summary>The host's run: its own job id and workspace id, the extras validated by the caller.</summary>
    public StartRunRequest ToRequest(string sessionId, string workspaceId) =>
        new StartRunRequest(sessionId, workspaceId, Request.Kind, Request.Input, RegisteredModels.Take(64).Select(m => m.ToEntry()).ToList(), Request.Model, Request.Provider, Request.Settings, Request.ResumeId, Request.Attachments)
        { PermissionModeOverride = PermissionModeOverride, TodoProgress = TodoProgress, PhaseModels = PhaseModels };
}

public sealed record RemoteWireJob(int Protocol, string JobId);
public sealed record RemoteWireEvent(int Cursor, JsonObject Event);
/// <param name="Cursor">The last cursor in Events (the request's when none).</param>
/// <param name="LastCursor">The run's newest cursor; more is waiting while it is past Cursor.</param>
/// <param name="Gap">Events after the request's cursor were dropped from the host's buffer.</param>
public sealed record RemoteWirePoll(int Protocol, int Cursor, int LastCursor, bool Gap, bool Done, IReadOnlyList<RemoteWireEvent> Events);
public sealed record RemoteWireSteer(string Text);
public sealed record RemoteWirePermission(string RunId, string RequestId, bool Allow);
public sealed record RemoteWireQuestions(string RunId, string RequestId, IReadOnlyDictionary<string, UserQuestionAnswer> Answers);
public sealed record RemoteWirePlan(string RunId, string RequestId, string Outcome, string? Feedback = null)
{
    public static RemoteWirePlan From(string runId, string requestId, PlanDecision decision) => new(runId, requestId, decision.Outcome, decision.Kind == "revise" ? decision.Feedback : null);
    /// <summary>The decision this outcome stands for; null for revised without feedback or an unknown outcome.</summary>
    public PlanDecision? Decision() => Outcome switch
    {
        PlanOutcome.ApprovedAuto => PlanDecision.ApproveAutoEdit,
        PlanOutcome.ApprovedConfirm => PlanDecision.ApproveConfirmEach,
        PlanOutcome.Revised => Feedback is { } feedback ? PlanDecision.Revise(feedback) : null,
        PlanOutcome.Cancelled => PlanDecision.Cancel,
        _ => null,
    };
}
public sealed record RemoteWireAnswer(int Protocol, bool Accepted);
public sealed record RemoteWireError(int Protocol, string Error);

/// <summary>The host answered with an error status; its message is what the user reads.</summary>
public sealed class RemoteHttpFailure(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
    /// <summary>Busy for a moment (rate limit, a local update holding runs): worth asking again.</summary>
    public bool Transient => Status is 429 or 503;
}

/// <summary>Remote workspaces are not mirrored to the phone: it sees this computer's own workspaces and panes only.</summary>
public static class RemoteWorkspacePhone
{
    public static IReadOnlyList<Workspace> Workspaces(IEnumerable<Workspace> all) => all.Where(w => w.Remote is null).ToList();
    public static IReadOnlyList<RunSession> Sessions(IEnumerable<RunSession> sessions, IEnumerable<Workspace> workspaces)
    {
        var local = workspaces.Where(w => w.Remote is null).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        return sessions.Where(s => local.Contains(s.WorkspaceId)).ToList();
    }
}
```

- [ ] **Step 4: Write the validation**

`native/windows/MightyClaude.Core/Remote/RemoteWorkspaceValidation.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public static class RemoteWorkspaceValidation
{
    /// <summary>A connection key: 256 random bits, base64url (43 characters) or longer.</summary>
    public static bool Key(string? value) => value is not null && Regex.IsMatch(value, @"^[A-Za-z0-9_-]{43,128}\z");

    /// <summary>Constant-time: both sides are hashed first, so neither the length nor the first differing byte shows in the time taken.</summary>
    public static bool KeysMatch(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public static bool SharedWorkspace(RemoteSharedWorkspace? value) => value is not null && Wire.Identifier(value.Id) && value.Name is { Length: > 0 and <= 120 }
        && !value.Name.Any(char.IsControl) && StateStore.RemotePath(value.Path);

    public static bool PermissionMode(string mode, string provider) =>
        mode is "manual" or "plan" or "acceptEdits" or "fullAccess" || mode == "auto" && provider == "claude" || mode == "onRequest" && provider == "codex";

    public static readonly IReadOnlySet<string> EventTypes = new HashSet<string>(StringComparer.Ordinal) { "log", "status", "resume", "activity", "usage", "graph", "permission", "plan", "todos", "background" };

    public static void Info(RemoteWireInfo? value)
    {
        if (value is null || value.Protocol != RemoteProtocol.Version || !Wire.Identifier(value.HostId) || value.HostName is not { Length: > 0 and <= 120 } || value.Workspaces is null
            || value.Workspaces.Count > RemoteProtocol.MaximumSharedWorkspaces || !value.Workspaces.All(SharedWorkspace) || value.Workspaces.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != value.Workspaces.Count
            || value.Runtime is null || value.Runtime.Platform is not ("darwin" or "win32" or "linux") || (value.Runtime.AppVersion?.Length ?? 0) > 80 || (value.Runtime.Providers?.Count ?? 0) > Wire.Providers.Length)
            throw new InvalidDataException(Locale.Get("remote.connect.error.notMighty"));
        foreach (var provider in value.Runtime.Providers ?? [])
            if (!Wire.Providers.Contains(provider.Id) || provider.Name is null || provider.Name.Length > 120 || (provider.Detail?.Length ?? 0) > 2_000 || (provider.Version?.Length ?? 0) > 80
                || provider.ModelCatalog?.Models is null || provider.ModelCatalog.Models.Count > 128 || (provider.ModelCatalog.Detail?.Length ?? 0) > 2_000
                || !provider.ModelCatalog.Models.All(m => Wire.Model(m.Value) && (m.DisplayName?.Length ?? 0) <= 160 && (m.Description?.Length ?? 0) <= 2_400)
                || provider.Capabilities?.PermissionModes is null || !provider.Capabilities.PermissionModes.All(mode => PermissionMode(mode, provider.Id)))
                throw new InvalidDataException(Locale.Get("remote.connect.error.badRuntime"));
    }

    /// <summary>Settings → Models as the client sent it: every value a model name or "default", every effort a known one.</summary>
    public static bool PhaseModels(PhaseModelsSnapshot value)
    {
        string[] models = [value.ClaudeMain, value.ClaudeOpusAlias, value.ClaudeSonnetAlias, value.ClaudeHaikuAlias, value.ClaudeSubagentDefault, value.CodexReviewModel, value.CodexSubagentDefault];
        string[] efforts = [value.CodexPlanModeReasoningEffort, value.ClaudeMainEffort ?? "default", value.CodexMainEffort ?? "default", value.CodexSubagentEffort ?? "default"];
        return models.All(m => m == "default" || Wire.Model(m)) && efforts.All(e => e == "default" || Wire.Efforts.Contains(e));
    }

    /// <summary>What the host's CLI cannot run (M/CoreValidation.validateCapabilities); null when it can.</summary>
    public static string? CapabilityProblem(StartRunRequest request, ProviderCapabilities caps)
    {
        var s = request.Settings ?? new();
        if (!caps.Attachments && request.Attachments is { Count: > 0 }) return Locale.Get("validation.run.attachmentsUnsupported");
        var supported = caps.PermissionModes.Contains(s.PermissionMode) && (request.PermissionModeOverride is null || caps.PermissionModes.Contains(request.PermissionModeOverride))
            && (caps.Effort || s.Effort == "default") && (caps.MaxTurns || s.MaxTurns is null) && (caps.MaxBudgetUsd || s.MaxBudgetUsd is null)
            && (caps.FastMode || !s.FastMode) && (caps.WebSearch || s.WebSearch == "default") && (caps.NetworkAccess || !s.NetworkAccess)
            && (!s.NetworkAccess || request.Provider == "codex" && s.PermissionMode is "acceptEdits" or "onRequest") && (caps.Resume || request.ResumeId is null);
        return supported ? null : Locale.Get("validation.run.settingsUnsupported");
    }
}
```

- [ ] **Step 5: Run it to verify it passes**

Run: CORE-RUN.
Expected: `PASS remote wire: …` twice, and 9 FAILs.

If a round trip fails, the printed JSON shows the Windows encoding. Change the Windows wire record or converter until it writes the sample; never change the sample. The Mac is the reference.

- [ ] **Step 6: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteWorkspaceModels.cs native/windows/MightyClaude.Core/Remote/RemoteWorkspaceValidation.cs native/windows/MightyClaude.Core.Tests/RemoteWireFixtureVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the remote-workspace wire records read and write every shared sample exactly as the Mac does, with the Mac's key, info, mode and capability checks

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Converting run events to and from the wire

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteWireEvents.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/RemoteWireFixtureVerification.cs` (add two checks)
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (register)

**Interfaces:**
- Consumes:
  - `RunEvent` and `RunEvent.Valid()`
  - `ToolPermissionRequest` (Windows names its input `InputJson`)
  - `ExecutionGraphNode`, `ActivitySupport.Clean(string, int)`, `ActivitySupport.MaximumMessageBytes`
  - `RemoteWorkspaceValidation.EventTypes` (Task 4)
- Produces:
  - `public sealed record RemoteIncoming(RunEvent? Event = null, ToolPermissionRequest? Permission = null, string? HostRunId = null, bool Ignored = false)`, with `[JsonIgnore] bool Usable`.
  - `RemoteWireEvents`:
    - `FromRunEvent(RunEvent, string jobId, bool graphRuns) → JsonObject?`
    - `FromPermission(ToolPermissionRequest, string jobId) → JsonObject`
    - `FromGraphNode(ExecutionGraphNode, string jobId) → JsonObject`
    - `Read(JsonObject wire, string jobId, string paneId, bool graphRuns) → RemoteIncoming`
    - `MacTimestamp(string) → string?`
    - `Normalize(JsonNode?)`
  - `public sealed class RemotePendingPermissions`, with `Admit(ToolPermissionRequest) → IReadOnlyList<ToolPermissionRequest>` and `Forget(string paneId)`.

The mapping, both directions:

| Windows | Wire (Mac shape) |
|---|---|
| `status` with `Reason = "authentication"` | `reason: "auth"` (any other reason is left out) |
| `ToolPermissionRequest.InputJson`, delivered through `permissionRequested` | `{type: "permission", permission: {…, inputJSON}}`, `runId` = job id |
| `graph_run` | sent only when the client asked with `x-mighty-remote-graph: run` |
| tracker node (`ExecutionGraphNode`) | `{type: "graph", graph}`, sent when it did not ask; a Windows client ignores it silently |
| `entry.images` | left out |
| `Wire.Now()` "O" timestamps | `yyyy-MM-ddTHH:mm:ss.fffZ` |

- [ ] **Step 1: Write the failing tests**

Append to `RemoteWireFixtureVerification`:

```csharp
    internal static Task EveryWireEventReadsAndWritesBack()
    {
        foreach (var name in new[] { "poll.json", "poll-gap.json" })
            foreach (var item in Decode<RemoteWirePoll>(name).Events)
            {
                var incoming = RemoteWireEvents.Read(item.Event, "job-1", "pane-1", graphRuns: false);
                Check(incoming.Usable, name + " cursor " + item.Cursor + " is usable");
                if (item.Event["type"]!.GetValue<string>() == "graph") { Check(incoming.Ignored && incoming.Event is null, "a Mac graph node is ignored silently"); continue; }
                if (incoming.Permission is { } permission)
                {
                    Check(permission.RunId == "pane-1" && incoming.HostRunId == "job-1" && permission.InputJson == "{\"command\":\"ls\"}", "a tool request is the pane's, its host run kept");
                    Check(Same(item.Event, RemoteWireEvents.FromPermission(permission with { RunId = incoming.HostRunId! }, "job-1")), "the permission writes back as the Mac's: " + RemoteWireEvents.FromPermission(permission, "job-1").ToJsonString());
                    continue;
                }
                var value = incoming.Event!;
                Check(value.SessionId == "pane-1", "an event becomes the pane's");
                if (value.Type == "status" && value.Status == "error") Check(value.Reason == "authentication", "auth on the wire is the Windows sign-in reason");
                Check(Same(item.Event, RemoteWireEvents.FromRunEvent(value, "job-1", graphRuns: false)), name + " cursor " + item.Cursor + " writes back as the Mac's: " + RemoteWireEvents.FromRunEvent(value, "job-1", false)?.ToJsonString());
            }
        var malformed = Decode<RemoteWirePoll>("poll-malformed.json").Events.Select(e => RemoteWireEvents.Read(e.Event, "job-1", "pane-1", false).Usable).ToList();
        Check(malformed.SequenceEqual([true, false, true]), "only the malformed event is unusable");
        Check(!RemoteWireEvents.Read(Decode<RemoteWirePoll>("poll.json").Events[0].Event, "job-2", "pane-1", false).Usable, "another job's event is unusable");
        var graphRun = Node("graph-run-event.json")["event"]!.AsObject();
        Check(!RemoteWireEvents.Read(graphRun, "job-1", "pane-1", graphRuns: false).Usable, "graph_run is unusable to a client that did not ask");
        var run = RemoteWireEvents.Read(graphRun, "job-1", "pane-1", graphRuns: true).Event;
        Check(run is { Type: "graph_run", GraphRun.Id: "gr-1" }, "graph_run reaches a client that asked");
        Check(RemoteWireEvents.FromRunEvent(run!, "job-1", graphRuns: false) is null && Same(graphRun, RemoteWireEvents.FromRunEvent(run!, "job-1", graphRuns: true)), "graph_run travels only on request, as the sample");
        return Task.CompletedTask;
    }

    internal static Task TimestampsImagesAndPendingRequestsAreBounded()
    {
        var log = RemoteWireEvents.FromRunEvent(RunEvent.Log("job-9", "output", "hi"), "job-9", false)!;
        var stamp = log["entry"]!["timestamp"]!.GetValue<string>();
        Check(System.Text.RegularExpressions.Regex.IsMatch(stamp, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\z"), "Windows timestamps travel as the Mac reads them: " + stamp);
        Check(RemoteWireEvents.MacTimestamp("2026-10-17T10:02:03.1234567+09:00") == "2026-10-17T01:02:03.123Z" && RemoteWireEvents.MacTimestamp("not a time") is null, "offsets become UTC");
        var picture = RunEvent.Log("job-9", "image", "a picture") with { };
        picture = picture with { Entry = picture.Entry! with { Images = [new AgentImageRef("h", "image/png", 1, 1, 10, "tool")] } };
        Check(RemoteWireEvents.FromRunEvent(picture, "job-9", false)!["entry"]!["images"] is null, "pictures name host files and stay home");
        var pending = new RemotePendingPermissions(); var evicted = new List<ToolPermissionRequest>();
        for (var i = 0; i < 40; i++) evicted.AddRange(pending.Admit(new ToolPermissionRequest("r" + i, "pane-1", "t" + i, "Bash", "{}", "ls")));
        Check(evicted.Select(r => r.Id).SequenceEqual(Enumerable.Range(0, 8).Select(i => "r" + i)) && evicted.All(r => r.State == "cancelled"), "the oldest are withdrawn: " + string.Join(",", evicted.Select(r => r.Id)));
        Check(pending.Admit(new ToolPermissionRequest("r39", "pane-1", "t39", "Bash", "{}", "ls", State: "allowed")).Count == 0 && pending.Count("pane-1") == 31, "a settled request leaves the count");
        pending.Forget("pane-1"); Check(pending.Count("pane-1") == 0, "a finished run forgets its requests");
        return Task.CompletedTask;
    }
```

`AgentImageRef`'s constructor order is `(Hash, MediaType, Width, Height, Bytes, Source, Path?)` (`AgentImages.cs:15`). If the record differs, build it with its own named arguments.

Register:

```csharp
        await Test("remote wire: every wire event reads and writes back", RemoteWireFixtureVerification.EveryWireEventReadsAndWritesBack);
        await Test("remote wire: timestamps, images and pending requests are bounded", RemoteWireFixtureVerification.TimestampsImagesAndPendingRequestsAreBounded);
```

- [ ] **Step 2: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS0103: The name 'RemoteWireEvents' does not exist`.

- [ ] **Step 3: Implement**

`native/windows/MightyClaude.Core/Remote/RemoteWireEvents.cs`:

```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>What one wire event is to a Windows client: a pane event, a tool request (with the host's run id to answer with), ignored, or unusable.</summary>
public sealed record RemoteIncoming(RunEvent? Event = null, ToolPermissionRequest? Permission = null, string? HostRunId = null, bool Ignored = false)
{
    [JsonIgnore] public bool Usable => Event is not null || Permission is not null || Ignored;
}

/// <summary>
/// Windows run events ↔ the Mac's wire events (native/contracts/fixtures/remote-v2/poll.json). A host writes with From*, a client
/// reads with Read; the Mac is the reference for every field.
/// </summary>
public static class RemoteWireEvents
{
    private static readonly Regex Timestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant);

    /// <summary>A host's event as the wire carries it; null when it does not travel (graph_run to a client that did not ask).</summary>
    public static JsonObject? FromRunEvent(RunEvent value, string jobId, bool graphRuns)
    {
        if (value.Type == "graph_run" && !graphRuns || value.Type is "permission" or "graph") return null;
        var node = JsonSerializer.SerializeToNode(value with { SessionId = jobId }, Wire.Json)!.AsObject();
        if (value.Type == "status")
        {
            node.Remove("reason");
            if (value.Reason == "authentication") node["reason"] = "auth";
        }
        if (node["entry"] is JsonObject entry) entry.Remove("images");
        Normalize(node);
        return node;
    }

    /// <summary>A tool request from the host's run (RunManager's permissionRequested), as the Mac's "permission" event.</summary>
    public static JsonObject FromPermission(ToolPermissionRequest value, string jobId)
    {
        var permission = JsonSerializer.SerializeToNode(value with { RunId = jobId }, Wire.Json)!.AsObject();
        if (permission["inputJson"] is { } input) { permission.Remove("inputJson"); permission["inputJSON"] = input; }
        var node = new JsonObject { ["sessionId"] = jobId, ["type"] = "permission", ["permission"] = permission };
        Normalize(node);
        return node;
    }

    /// <summary>One execution-graph node of the host's run, as the Mac's "graph" event.</summary>
    public static JsonObject FromGraphNode(ExecutionGraphNode value, string jobId)
    {
        var graph = JsonSerializer.SerializeToNode(value, Wire.Json)!.AsObject();
        if (graph["entries"] is JsonArray entries) foreach (var entry in entries.OfType<JsonObject>()) entry.Remove("images");
        var node = new JsonObject { ["sessionId"] = jobId, ["type"] = "graph", ["graph"] = graph };
        Normalize(node);
        return node;
    }

    /// <summary>One event of the host's run, as this client's pane would have it, or why not.</summary>
    public static RemoteIncoming Read(JsonObject wire, string jobId, string paneId, bool graphRuns)
    {
        try
        {
            if (wire["sessionId"]?.GetValue<string>() != jobId || wire["type"]?.GetValue<string>() is not { } type) return new();
            if (type == "graph") return new(Ignored: true);
            if (type == "permission")
            {
                if (wire["permission"]?.DeepClone() is not JsonObject raw) return new();
                if (raw["inputJSON"] is { } input) { raw.Remove("inputJSON"); raw["inputJson"] = input; }
                var permission = raw.Deserialize<ToolPermissionRequest>(Wire.Json);
                if (permission is null || !Wire.Identifier(permission.RunId) || permission.Id is not { Length: > 0 and <= 256 } || permission.ToolName is not { Length: <= 256 }
                    || permission.InputJson is null || Encoding.UTF8.GetByteCount(permission.InputJson) > 65_536 || permission.Summary is null || Encoding.UTF8.GetByteCount(permission.Summary) > 16_384
                    || permission.State is not { Length: > 0 and <= 32 }) return new();
                return new(Permission: permission with { RunId = paneId }, HostRunId: permission.RunId);
            }
            if (!(RemoteWorkspaceValidation.EventTypes.Contains(type) || type == "graph_run" && graphRuns)) return new();
            var copy = wire.DeepClone().AsObject();
            if (type == "status" && copy["reason"] is { } reason)
            {
                if (reason.GetValue<string>() != "auth") return new();
                copy["reason"] = "authentication";
            }
            if (copy["entry"] is JsonObject entry) entry.Remove("images");
            var value = copy.Deserialize<RunEvent>(Wire.Json);
            if (value is null || type == "usage" && value.Usage is null) return new();
            value = value with { SessionId = paneId };
            if (value.Entry is { } log) value = value with { Entry = log with { Text = ActivitySupport.Clean(log.Text ?? "", log.Kind == "assistant" ? ActivitySupport.MaximumMessageBytes : 32768) } };
            return value.Valid() ? new(value) : new();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return new(); }
    }

    /// <summary>yyyy-MM-ddTHH:mm:ss.fffZ, the form the Mac's ISO 8601 reader takes (Windows writes "O": seven digits and an offset); null when not a timestamp.</summary>
    public static string? MacTimestamp(string value) =>
        Timestamp.IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : null;

    /// <summary>Every timestamp string in a wire event rewritten by <see cref="MacTimestamp"/>.</summary>
    public static void Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && MacTimestamp(s) is { } t) { if (t != s) o[key] = t; }
                    else Normalize(o[key]);
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && MacTimestamp(s) is { } t) { if (t != s) a[i] = t; }
                    else Normalize(a[i]);
                break;
        }
    }
}

/// <summary>
/// A host can send any number of tool requests; a remote pane keeps only the newest
/// <see cref="RemoteProtocol.MaximumPendingPermissions"/> waiting (M/RemotePendingPermissions). Older ones are withdrawn as
/// "cancelled" changes, so the pane's cards go too.
/// </summary>
public sealed class RemotePendingPermissions
{
    private readonly Dictionary<string, List<ToolPermissionRequest>> pending = new(StringComparer.Ordinal);

    /// <returns>The requests this one pushed out, each already marked cancelled.</returns>
    public IReadOnlyList<ToolPermissionRequest> Admit(ToolPermissionRequest request)
    {
        lock (pending)
        {
            if (!pending.TryGetValue(request.RunId, out var list)) pending[request.RunId] = list = [];
            list.RemoveAll(r => r.Id == request.Id);
            if (request.State != "pending") return [];
            list.Add(request);
            var evicted = new List<ToolPermissionRequest>();
            while (list.Count > RemoteProtocol.MaximumPendingPermissions) { evicted.Add(list[0] with { State = "cancelled" }); list.RemoveAt(0); }
            return evicted;
        }
    }
    public int Count(string paneId) { lock (pending) return pending.TryGetValue(paneId, out var list) ? list.Count : 0; }
    public void Forget(string paneId) { lock (pending) pending.Remove(paneId); }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: CORE-RUN.
Expected: `PASS remote wire: …` four times, and 9 FAILs.

When a write-back differs, the message shows the Windows JSON. Fix the conversion until it writes the sample. For example, if the Windows `SessionUsage` converter writes a key the sample lacks, drop that key in `FromRunEvent`.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteWireEvents.cs native/windows/MightyClaude.Core.Tests/RemoteWireFixtureVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) run events, tool requests and graph nodes cross the remote wire in the Mac's shape both ways: auth reason, inputJSON, Mac timestamps, no pictures, graph_run only on request, and at most 32 waiting requests per pane

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 6: The key vault (DPAPI) and the saved-connections file

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteKeyVault.cs`
- Create: `native/windows/MightyClaude.Core/Remote/RemoteConnectionFile.cs`
- Create: `native/windows/MightyClaude.Core.Tests/RemoteClientVerification.cs` (first two checks; Tasks 7–10 add to it)
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (register)

**Interfaces:**
- Consumes: `StateStore.AtomicWriteAsync(string, byte[])`, `Wire.Identifier`.
- Produces:
  - `public interface IRemoteKeyVault { string? Load(string id); bool Save(string id, string key); void Delete(string id); }`
  - `public sealed class DpapiRemoteKeyVault(string directory) : IRemoteKeyVault`. Files go under `<directory>\remote-keys\<sha256(id)>.bin`. On a non-Windows OS, `Save` returns false and `Load` returns null.
  - `public sealed class MemoryRemoteKeyVault : IRemoteKeyVault`
  - `public sealed record RemoteSavedConnection(string Id, string Name, string Address)`
  - `RemoteConnectionFile.FileName = "remote-connections.json"`
  - `RemoteConnectionFile.Read(string directory) → IReadOnlyList<RemoteSavedConnection>`
  - `RemoteConnectionFile.WriteAsync(string directory, IEnumerable<RemoteSavedConnection>) → Task`

- [ ] **Step 1: Write the failing tests**

`native/windows/MightyClaude.Core.Tests/RemoteClientVerification.cs`:

```csharp
using System.Text.Json;
using MightyClaude.Core;

/// <summary>The Windows client of remote workspaces (docs/remote-workspaces.md): saved connections, keys, addresses, transport, runs.</summary>
internal static partial class RemoteClientVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string message, Func<Exception, bool>? expected = null)
    {
        try { await action(); }
        catch (Exception ex) { if (expected is null || expected(ex)) return; throw new InvalidOperationException(message + " (threw " + ex.GetType().Name + ": " + ex.Message + ")"); }
        throw new InvalidOperationException(message);
    }
    internal static readonly string Key = new('k', 43);

    internal static async Task SavedConnectionsKeepNoKey()
    {
        var directory = Verification.Temp();
        try
        {
            await RemoteConnectionFile.WriteAsync(directory, [new("conn-1", "studio", "100.101.102.103:43137"), new("conn-2", "mac", "[fd7a:115c:a1e0::5]:43137")]);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName));
            Check(text.Contains("\"version\":2", StringComparison.Ordinal) && !text.Contains("key", StringComparison.OrdinalIgnoreCase), "the file carries the version and no key: " + text);
            Check(RemoteConnectionFile.Read(directory).Select(c => c.Id).SequenceEqual(["conn-1", "conn-2"]), "read back in order");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":1,\"connections\":[{\"id\":\"old\",\"name\":\"x\",\"address\":\"100.1.2.3\"}]}");
            Check(RemoteConnectionFile.Read(directory).Count == 0, "a version 1 file (before 2026-09-28) is not read");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":[{\"id\":\"bad id\",\"name\":\"x\",\"address\":\"a\"},{\"id\":\"ok\",\"name\":\"y\",\"address\":\"b\"},{\"id\":\"ok\",\"name\":\"z\",\"address\":\"c\"}]}");
            Check(RemoteConnectionFile.Read(directory).Select(c => c.Name).SequenceEqual(["y"]), "bad and repeated ids are skipped");
            await File.WriteAllBytesAsync(Path.Combine(directory, RemoteConnectionFile.FileName), new byte[256 * 1024 + 1]);
            Check(RemoteConnectionFile.Read(directory).Count == 0, "an oversize file is not read");
            Check(RemoteConnectionFile.Read(Path.Combine(directory, "missing")).Count == 0, "no file, no connections");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static Task KeysStayInTheVault()
    {
        var directory = Verification.Temp();
        try
        {
            var memory = new MemoryRemoteKeyVault();
            Check(memory.Save("conn-1", Key) && memory.Load("conn-1") == Key, "the memory vault keeps a key");
            memory.Delete("conn-1"); Check(memory.Load("conn-1") is null, "and forgets it");
            var dpapi = new DpapiRemoteKeyVault(directory);
            if (OperatingSystem.IsWindows())
            {
                Check(dpapi.Save("conn:1", Key), "DPAPI saves for this Windows user");
                var file = Directory.GetFiles(Path.Combine(directory, "remote-keys")).Single();
                Check(!File.ReadAllText(file).Contains(Key, StringComparison.Ordinal) && !Path.GetFileName(file).Contains("conn", StringComparison.Ordinal), "the file is ciphertext under a hashed name");
                Check(new DpapiRemoteKeyVault(directory).Load("conn:1") == Key, "a new vault reads it back");
                File.WriteAllBytes(file, [1, 2, 3]); Check(dpapi.Load("conn:1") is null, "a damaged file reads as no key");
                dpapi.Delete("conn:1"); Check(!File.Exists(file), "delete removes the file");
            }
            else Check(!dpapi.Save("conn-1", Key) && dpapi.Load("conn-1") is null && !Directory.Exists(Path.Combine(directory, "remote-keys")), "without DPAPI nothing is written and the key stays in memory");
            Check(!memory.Save("conn-1", "short"), "a key of the wrong shape is never saved");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
}
```

Register:

```csharp
        await Test("remote client: saved connections keep no key", RemoteClientVerification.SavedConnectionsKeepNoKey);
        await Test("remote client: keys stay in the vault", RemoteClientVerification.KeysStayInTheVault);
```

- [ ] **Step 2: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS0103: The name 'RemoteConnectionFile' does not exist`.

- [ ] **Step 3: Implement**

`native/windows/MightyClaude.Core/Remote/RemoteKeyVault.cs`:

```csharp
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MightyClaude.Core;

/// <summary>Where the connection keys of saved remote connections live: never in workspace state or remote-connections.json.</summary>
public interface IRemoteKeyVault
{
    string? Load(string id);
    bool Save(string id, string key);
    void Delete(string id);
}

/// <summary>Keys for this launch only: tests, and the fallback when DPAPI refuses.</summary>
public sealed class MemoryRemoteKeyVault : IRemoteKeyVault
{
    private readonly ConcurrentDictionary<string, string> keys = new(StringComparer.Ordinal);
    public string? Load(string id) => keys.TryGetValue(id, out var key) ? key : null;
    public bool Save(string id, string key) { if (!RemoteWorkspaceValidation.Key(key)) return false; keys[id] = key; return true; }
    public void Delete(string id) => keys.TryRemove(id, out _);
}

/// <summary>
/// One file per connection under &lt;profile&gt;\remote-keys, encrypted with DPAPI for the current Windows user (CryptProtectData
/// without CRYPTPROTECT_LOCAL_MACHINE: what ProtectedData.Protect(…, DataProtectionScope.CurrentUser) calls, without adding the
/// package). Only this user on this PC can read a key back. A key is read on a connection's first use, never at launch.
/// </summary>
public sealed class DpapiRemoteKeyVault(string directory) : IRemoteKeyVault
{
    private string Folder => Path.Combine(directory, "remote-keys");
    private string FileFor(string id) => Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".bin");

    public string? Load(string id)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var file = FileFor(id);
            if (!File.Exists(file) || new FileInfo(file).Length is 0 or > 16_384) return null;
            var plain = Transform(File.ReadAllBytes(file), decrypt: true);
            try { var key = Encoding.UTF8.GetString(plain); return RemoteWorkspaceValidation.Key(key) ? key : null; }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or Win32Exception) { return null; }
    }

    public bool Save(string id, string key)
    {
        if (!OperatingSystem.IsWindows() || !RemoteWorkspaceValidation.Key(key)) return false;
        try
        {
            Directory.CreateDirectory(Folder);
            StateStore.AtomicWriteAsync(FileFor(id), Transform(Encoding.UTF8.GetBytes(key), decrypt: false)).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or Win32Exception) { return false; }
    }

    public void Delete(string id) { try { File.Delete(FileFor(id)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static byte[] Transform(byte[] value, bool decrypt)
    {
        var pointer = Marshal.AllocHGlobal(value.Length); Blob result = default;
        try
        {
            Marshal.Copy(value, 0, pointer, value.Length); var input = new Blob { Size = value.Length, Data = pointer };
            var ok = decrypt ? CryptUnprotectData(ref input, 0, 0, 0, 0, UiForbidden, out result) : CryptProtectData(ref input, "MightyClaude remote key", 0, 0, 0, UiForbidden, out result);
            if (!ok) throw new Win32Exception();
            if (result.Size is < 1 or > 16_384) throw new CryptographicException("DPAPI output size");
            var output = new byte[result.Size]; Marshal.Copy(result.Data, output, 0, output.Length); return output;
        }
        finally
        {
            for (var i = 0; i < value.Length; i++) Marshal.WriteByte(pointer, i, 0);
            Marshal.FreeHGlobal(pointer);
            if (result.Data != 0) { for (var i = 0; i < result.Size; i++) Marshal.WriteByte(result.Data, i, 0); LocalFree(result.Data); }
            CryptographicOperations.ZeroMemory(value);
        }
    }
    private const uint UiForbidden = 1;
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
```

`native/windows/MightyClaude.Core/Remote/RemoteConnectionFile.cs`:

```csharp
using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>One saved connection: id, name, address. Never its key.</summary>
public sealed record RemoteSavedConnection(string Id, string Name, string Address);

/// <summary>The data folder's remote-connections.json (version 2, the Mac's shape), user-only on every OS StateStore writes to.</summary>
public static class RemoteConnectionFile
{
    public const string FileName = "remote-connections.json";
    private sealed record SavedFile(int Version, IReadOnlyList<RemoteSavedConnection> Connections);

    public static IReadOnlyList<RemoteSavedConnection> Read(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) return [];
            var file = JsonSerializer.Deserialize<SavedFile>(File.ReadAllBytes(path), Wire.Json);
            if (file is not { Version: 2, Connections: { } connections }) return [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return connections.Where(c => c is not null && Wire.Identifier(c.Id) && c.Address is { Length: > 0 and <= 300 } && seen.Add(c.Id))
                .Take(RemoteProtocol.MaximumConnections).Select(c => c with { Name = Wire.Clean(c.Name, 120) }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public static Task WriteAsync(string directory, IEnumerable<RemoteSavedConnection> connections) =>
        StateStore.AtomicWriteAsync(Path.Combine(directory, FileName), JsonSerializer.SerializeToUtf8Bytes(new SavedFile(2, connections.Take(RemoteProtocol.MaximumConnections).ToList()), Wire.Json));
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: CORE-RUN.
Expected: `PASS remote client: saved connections keep no key` and `PASS remote client: keys stay in the vault`, and 9 FAILs.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteKeyVault.cs native/windows/MightyClaude.Core/Remote/RemoteConnectionFile.cs native/windows/MightyClaude.Core.Tests/RemoteClientVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) saved remote connections keep id, name and address in remote-connections.json and their keys in DPAPI files only this Windows user can read

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Stage C — Transport and client

### Task 7: Addresses, the peer rule and the tunnel route

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteAddress.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/RemoteClientVerification.cs`, `Verification.cs`

**Interfaces:**
- Consumes: `TailscaleAddresses.Contains`, `.Current`, `.Canonical` (phone-direct plan).
- Produces:
  - `public sealed record RemoteAddress(string Host, int Port)`, with `[JsonIgnore] string Display` and `static RemoteAddress Parse(string input)`. `Parse` throws `ArgumentException` with `remote.connect.error.badAddress`/`badPort`.
  - `RemotePeerPolicy.Allowed(string address, bool allowLoopback = false) → bool` and `Allowed(IPAddress, bool) → bool`.
  - `RemoteRoute.SourceAddressToward(string ip) → string?` and `RemoteRoute.ThroughTailscale(string ip) → bool`.

- [ ] **Step 1: Write the failing tests**

Append to `RemoteClientVerification`:

```csharp
    internal static Task AddressesParseWithTheDefaultPort()
    {
        Check(RemoteAddress.Parse("100.101.102.103") == new RemoteAddress("100.101.102.103", 43137), "a bare IP takes 43137");
        Check(RemoteAddress.Parse(" http://100.1.2.3:5000/ ").Display == "100.1.2.3:5000", "http:// and a trailing slash are dropped");
        Check(RemoteAddress.Parse("Studio-PC.tail1234.ts.net").Display == "studio-pc.tail1234.ts.net:43137", "MagicDNS names are lower-cased");
        Check(RemoteAddress.Parse("studio-pc:43200").Display == "studio-pc:43200", "a name with a port");
        Check(RemoteAddress.Parse("[fd7a:115c:a1e0::1]:43200").Display == "[fd7a:115c:a1e0::1]:43200", "bracketed IPv6 with a port");
        Check(RemoteAddress.Parse("fd7a:115c:a1e0:0::1").Display == "[fd7a:115c:a1e0::1]:43137", "IPv6 is canonical");
        foreach (var bad in new[] { "", "https://100.1.2.3", "ftp://x", "user@100.1.2.3", "100.1.2.3/path", "100.1.2.3?k=v", "100.1.2.3#x", "100.1.2.3:80", "100.1.2.3:99999", "100.1.2.3:abc", "[fd7a::1", "-host", "a..b", "host name", "fe80::1%en0", new string('a', 400) })
        {
            try { RemoteAddress.Parse(bad); throw new InvalidOperationException("accepted " + bad); } catch (ArgumentException) { }
        }
        return Task.CompletedTask;
    }

    internal static Task OnlyTailscalePeersAndLoopbackWhenInjected()
    {
        Check(!RemotePeerPolicy.Allowed("127.0.0.1") && !RemotePeerPolicy.Allowed("::1"), "loopback is never a peer by default");
        Check(RemotePeerPolicy.Allowed("127.0.0.1", allowLoopback: true) && RemotePeerPolicy.Allowed("::1", allowLoopback: true) && !RemotePeerPolicy.Allowed("192.168.0.2", allowLoopback: true), "loopback only when injected");
        Check(RemotePeerPolicy.Allowed("100.90.1.1") && RemotePeerPolicy.Allowed("fd7a:115c:a1e0::5%17") && !RemotePeerPolicy.Allowed("fe80::1%17"), "the address decides, not its scope");
        Check(RemotePeerPolicy.Allowed(System.Net.IPAddress.Parse("::ffff:100.90.1.1")) && !RemotePeerPolicy.Allowed(System.Net.IPAddress.Parse("10.0.0.1")), "IPv4-mapped peers read as IPv4");
        Check(RemoteRoute.SourceAddressToward("127.0.0.1") == "127.0.0.1", "the route to loopback leaves from loopback, with nothing sent");
        Check(!RemoteRoute.ThroughTailscale("127.0.0.1") && RemoteRoute.SourceAddressToward("not an ip") is null, "loopback is not the tunnel; garbage has no route");
        return Task.CompletedTask;
    }
```

Register:

```csharp
        await Test("remote client: addresses parse with the default port", RemoteClientVerification.AddressesParseWithTheDefaultPort);
        await Test("remote client: only Tailscale peers, loopback only when injected", RemoteClientVerification.OnlyTailscalePeersAndLoopbackWhenInjected);
```

- [ ] **Step 2: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS0246: 'RemoteAddress'`.

- [ ] **Step 3: Implement**

`native/windows/MightyClaude.Core/Remote/RemoteAddress.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>A host address as the user typed it: a Tailscale IP or a MagicDNS name, an optional port (43137 when left out) and an optional http://.</summary>
public sealed record RemoteAddress(string Host, int Port)
{
    /// <summary>host:port, IPv6 in brackets: what is saved and shown.</summary>
    [JsonIgnore] public string Display => (Host.Contains(':') ? "[" + Host + "]" : Host) + ":" + Port;

    public static RemoteAddress Parse(string input)
    {
        static ArgumentException Bad() => new(Locale.Get("remote.connect.error.badAddress"));
        var text = (input ?? "").Trim();
        if (text.Length is 0 or > 300 || text.Contains('\0')) throw Bad();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = text[7..];
        if (text.EndsWith('/')) text = text[..^1];
        if (text.IndexOfAny(['/', '@', '?', '#', '%']) >= 0 || text.Contains("://", StringComparison.Ordinal)) throw Bad();
        var host = text; var port = RemoteProtocol.DefaultPort;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) throw Bad();
            host = text[1..close]; var rest = text[(close + 1)..];
            if (rest.Length > 0) { if (!rest.StartsWith(':') || !int.TryParse(rest[1..], System.Globalization.NumberStyles.None, null, out port)) throw Bad(); }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':'); host = text[..colon];
            if (!int.TryParse(text[(colon + 1)..], System.Globalization.NumberStyles.None, null, out port)) throw Bad();
        }
        host = host.ToLowerInvariant();
        if (port is < 1024 or > 65535) throw new ArgumentException(Locale.Get("remote.connect.error.badPort"));
        if (host.Length is 0 or > 253 || !Regex.IsMatch(host, @"^[a-z0-9.:-]+\z") || host.StartsWith('-') || host.StartsWith('.') || host.Contains("..", StringComparison.Ordinal)) throw Bad();
        // A colon is only ever an IPv6 literal's.
        if (host.Contains(':'))
        {
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) throw Bad();
            host = TailscaleAddresses.Canonical(host);
        }
        return new(host, port);
    }
}

/// <summary>
/// Which peers the remote-workspace host answers and the client connects to: Tailscale addresses only. Loopback is allowed only
/// where a test (or the smoke run) injects allowLoopback in code; no setting, connection field or IPC reaches it.
/// </summary>
public static class RemotePeerPolicy
{
    public static bool Allowed(string address, bool allowLoopback = false)
    {
        // A peer's address may carry its interface (fd7a:…%17); the address alone decides.
        var unscoped = address.Split('%', 2)[0];
        if (TailscaleAddresses.Contains(unscoped)) return true;
        if (!allowLoopback || !IPAddress.TryParse(unscoped.Trim('[', ']'), out var ip)) return false;
        return ip.AddressFamily == AddressFamily.InterNetwork ? ip.GetAddressBytes()[0] == 127 : ip.Equals(IPAddress.IPv6Loopback);
    }
    public static bool Allowed(IPAddress address, bool allowLoopback = false) => Allowed((address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString(), allowLoopback);
}

/// <summary>
/// Whether a host is reached through this PC's Tailscale tunnel. A carrier-grade NAT network (or another VPN) can route
/// 100.64/10 over Wi-Fi, outside WireGuard, and routes change while the app runs, so the client asks before every request.
/// </summary>
public static class RemoteRoute
{
    /// <summary>The local address the routing table picks toward <paramref name="ip"/>: a UDP socket is connected, nothing is sent.</summary>
    public static string? SourceAddressToward(string ip)
    {
        if (!IPAddress.TryParse(ip.Trim('[', ']'), out var target)) return null;
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, 9));
            return socket.LocalEndPoint is IPEndPoint local ? TailscaleAddresses.Canonical((local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address).ToString().Split('%')[0]) : null;
        }
        catch (SocketException) { return null; }
    }
    public static bool ThroughTailscale(string ip) => SourceAddressToward(ip) is { } source && TailscaleAddresses.Current().Contains(source);
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: CORE-RUN.
Expected: both `PASS remote client: …` lines, and 9 FAILs.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteAddress.cs native/windows/MightyClaude.Core.Tests/RemoteClientVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) remote addresses parse as on the Mac, only Tailscale peers count, and the route to a host is checked against this PC's tunnel without sending anything

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: The transport: one authenticated request, no redirects, at most 2 MiB

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteTransport.cs`
- Create: `native/windows/MightyClaude.Core.Tests/RemoteFakeHost.cs` (a scripted HTTP/1.1 test server)
- Modify: `RemoteClientVerification.cs`, `Verification.cs`
- Modify: `locales/*.json` plus copies (one key: `remote.windows.connect.error.timeout`) and `RemoteWindowsStrings.cs` (`Timeout`)

**Interfaces:**
- Consumes: `RemoteAddress`, `RemotePeerPolicy` (Task 7), `RemoteWorkspaceValidation.Key`, `RemoteHttpFailure` (Task 4).
- Produces:
  - `public sealed record RemoteTarget(RemoteAddress Address, string Ip, int Port)`
  - `RemoteTransport.ResolveAsync(RemoteAddress, bool allowLoopback, CancellationToken = default) → Task<RemoteTarget>`
  - `RemoteTransport.RequestAsync(RemoteTarget target, string key, HttpMethod method, string path, byte[]? body = null, TimeSpan? timeout = null, int maximumBytes = RemoteProtocol.MaximumResponseBytes, IReadOnlyDictionary<string, string>? headers = null, CancellationToken token = default) → Task<byte[]>`
  - `RemoteTransport.Transient(Exception) → bool`
  - Test helper `RemoteFakeHost(Func<RemoteFakeHost.Request, Task<RemoteFakeHost.Reply>>)`, with `.Port`, `.Seen`, and `static Reply Fixture(string name, int status = 200)`.

- [ ] **Step 1: Add the timeout string**

Write `/private/tmp/add-remote-timeout-key.mjs`:

```javascript
import fs from 'node:fs';
const text = {
  ko: '연결한 컴퓨터가 제때 답하지 않았습니다. 그 컴퓨터가 켜져 있고 Tailscale에 연결되어 있는지 확인하세요.',
  en: 'The other computer did not answer in time. Check that it is on and connected to Tailscale.',
  zh: '对方电脑没有及时响应。请确认它已开机并连接到 Tailscale。',
  ja: '接続先のコンピューターが時間内に応答しませんでした。電源が入っていてTailscaleに接続されているか確認してください。',
};
for (const language of ['ko', 'en', 'zh', 'ja']) {
  const values = JSON.parse(fs.readFileSync(`locales/${language}.json`, 'utf8'));
  values['remote.windows.connect.error.timeout'] = text[language];
  const output = JSON.stringify(values, null, 2) + '\n';
  for (const target of [`locales/${language}.json`, `native/macos/Sources/MightyCore/Resources/Locales/${language}.json`, `mobile/src/locales/${language}.json`]) fs.writeFileSync(target, output);
}
```

Run: `cd "$(git rev-parse --show-toplevel)" && node /private/tmp/add-remote-timeout-key.mjs && rm /private/tmp/add-remote-timeout-key.mjs && node scripts/check-locales.js`

Add this to `RemoteWindowsStrings`:

```csharp
    public static string Timeout => Locale.Get("remote.windows.connect.error.timeout");
```

Add `["timeout"] = RemoteWindowsStrings.Timeout,` to the dictionary in `RemoteStringsVerification.EveryWindowsOnlyStringResolves`.

- [ ] **Step 2: Write the fake host and the failing tests**

`native/windows/MightyClaude.Core.Tests/RemoteFakeHost.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>A scripted HTTP/1.1 server on loopback for the remote client checks: one request per connection, answers built by the test.</summary>
internal sealed class RemoteFakeHost : IAsyncDisposable
{
    internal sealed record Request(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body);
    internal sealed record Reply(int Status, string Body, IReadOnlyDictionary<string, string>? Headers = null, bool VersionHeader = true, TimeSpan? Delay = null);
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Func<Request, Task<Reply>> answer;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    internal readonly ConcurrentQueue<Request> Seen = new();
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    internal RemoteFakeHost(Func<Request, Task<Reply>> answer) { this.answer = answer; listener.Start(); loop = Task.Run(LoopAsync); }
    internal RemoteFakeHost(Func<Request, Reply> answer) : this(request => Task.FromResult(answer(request))) { }
    internal static Reply Fixture(string name, int status = 200) => new(status, RemoteWireFixtureVerification.Node(name).ToJsonString());

    private async Task LoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream(); var buffer = new List<byte>(); var chunk = new byte[8192]; int end;
            while ((end = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0) { var read = await stream.ReadAsync(chunk); if (read == 0) return; buffer.AddRange(chunk[..read]); }
            var head = Encoding.UTF8.GetString(buffer.ToArray(), 0, end).Split("\r\n");
            var line = head[0].Split(' ');
            var headers = head.Skip(1).Select(h => h.Split(':', 2)).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
            var length = headers.TryGetValue("content-length", out var text) ? int.Parse(text) : 0;
            var body = buffer.Skip(end + 4).ToList();
            while (body.Count < length) { var read = await stream.ReadAsync(chunk); if (read == 0) break; body.AddRange(chunk[..read]); }
            var request = new Request(line[0], line[1], headers, body.ToArray()); Seen.Enqueue(request);
            var reply = await answer(request);
            if (reply.Delay is { } delay) await Task.Delay(delay);
            var payload = Encoding.UTF8.GetBytes(reply.Body);
            var response = new StringBuilder($"HTTP/1.1 {reply.Status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n");
            if (reply.VersionHeader) response.Append("x-mighty-remote-version: 2\r\n");
            foreach (var (name, value) in reply.Headers ?? new Dictionary<string, string>()) response.Append(name).Append(": ").Append(value).Append("\r\n");
            response.Append("\r\n");
            try { await stream.WriteAsync(Encoding.UTF8.GetBytes(response.ToString())); await stream.WriteAsync(payload); } catch (IOException) { }
        }
    }
    private static int IndexOf(List<byte> data, byte[] pattern) { for (var i = 0; i + pattern.Length <= data.Count; i++) { var hit = true; for (var j = 0; j < pattern.Length && hit; j++) hit = data[i + j] == pattern[j]; if (hit) return i; } return -1; }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); try { await loop; } catch (Exception) { } }
}
```

Append to `RemoteClientVerification`:

```csharp
    internal static RemoteTarget Loopback(int port) => new(new RemoteAddress("127.0.0.1", port), "127.0.0.1", port);

    internal static async Task OneAuthenticatedRequestFollowsNoRedirectAndReadsAtMostTwoMebibytes()
    {
        await using var elsewhere = new RemoteFakeHost(_ => RemoteFakeHost.Fixture("info.json"));
        await using var host = new RemoteFakeHost(request => request.Target switch
        {
            "/v2/info" => RemoteFakeHost.Fixture("info.json"),
            "/v2/moved" => new(302, "{}", new Dictionary<string, string> { ["Location"] = $"http://127.0.0.1:{elsewhere.Port}/v2/info" }),
            "/v2/plain" => new(200, "{}", VersionHeader: false),
            "/v2/huge" => new(200, "\"" + new string('x', RemoteProtocol.MaximumResponseBytes) + "\""),
            "/v2/refused" => RemoteFakeHost.Fixture("error.json", 401),
            "/v2/busy" => new(503, "{\"protocol\":2,\"error\":\"busy\"}"),
            "/v2/slow" => new(200, "{}", Delay: TimeSpan.FromSeconds(3)),
            _ => new(404, "{\"protocol\":2,\"error\":\"no\"}"),
        });
        var target = Loopback(host.Port);
        var info = await RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/info");
        Check(RemoteWireFixtureVerification.Same(System.Text.Json.Nodes.JsonNode.Parse(info), RemoteWireFixtureVerification.Node("info.json")), "the body arrives whole");
        var seen = host.Seen.Last();
        Check(seen.Headers["authorization"] == "Bearer " + Key && seen.Headers["x-mighty-remote-version"] == "2" && !seen.Headers.ContainsKey("origin") && !seen.Headers.ContainsKey("cookie"), "the key rides in Authorization only, with the version header");
        Check(!seen.Target.Contains(Key, StringComparison.Ordinal), "the key is never in the URL");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/moved"), "a redirect is an error", ex => ex is InvalidDataException);
        Check(elsewhere.Seen.IsEmpty, "a redirect is never followed");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/plain"), "an answer without the version header is not Mighty's", ex => ex is InvalidDataException && ex.Message == Locale.Get("remote.connect.error.notMighty"));
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/huge"), "over 2 MiB is refused", ex => ex.Message == Locale.Get("remote.connect.error.responseTooLarge"));
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/refused"), "an error status carries the host's message", ex => ex is RemoteHttpFailure { Status: 401, Transient: false } f && f.Message == "The key does not match.");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/busy"), "503 is a passing failure", ex => RemoteTransport.Transient(ex));
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/slow", timeout: TimeSpan.FromMilliseconds(400)), "a slow host times out", ex => ex is TimeoutException && RemoteTransport.Transient(ex) && ex.Message == RemoteWindowsStrings.Timeout);
        var before = host.Seen.Count;
        await Reject(() => RemoteTransport.RequestAsync(target, "short", HttpMethod.Get, "/v2/info"), "a malformed key is refused before sending");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v1/info"), "only /v2/ paths");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/info?x=<script>"), "only plain path characters");
        Check(host.Seen.Count == before, "nothing refused locally reaches the host");
        Check(!RemoteTransport.Transient(new InvalidDataException("x")) && !RemoteTransport.Transient(new RemoteHttpFailure(400, "x")), "bad answers are not passing failures");
    }

    internal static async Task HostsResolveOnlyToTailscale()
    {
        Check((await RemoteTransport.ResolveAsync(new("127.0.0.1", 43137), allowLoopback: true)).Ip == "127.0.0.1", "loopback when injected");
        Check((await RemoteTransport.ResolveAsync(new("localhost", 43137), allowLoopback: true)).Ip == "127.0.0.1", "a name resolves once, IPv4 first");
        Check((await RemoteTransport.ResolveAsync(new("100.101.102.103", 43137), allowLoopback: false)).Ip == "100.101.102.103", "a Tailscale literal needs no lookup");
        await Reject(() => RemoteTransport.ResolveAsync(new("127.0.0.1", 43137), allowLoopback: false), "loopback is not Tailscale", ex => ex.Message == Locale.Get("remote.connect.error.notTailscale"));
        await Reject(() => RemoteTransport.ResolveAsync(new("192.168.1.5", 43137), allowLoopback: true), "a LAN address is refused", ex => ex.Message == Locale.Get("remote.connect.error.notTailscale"));
    }
```

Register:

```csharp
        await Test("remote client: one authenticated request follows no redirect and reads at most 2 MiB", RemoteClientVerification.OneAuthenticatedRequestFollowsNoRedirectAndReadsAtMostTwoMebibytes);
        await Test("remote client: hosts resolve only to Tailscale", RemoteClientVerification.HostsResolveOnlyToTailscale);
```

- [ ] **Step 3: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS0246: 'RemoteTarget'`.

- [ ] **Step 4: Implement**

`native/windows/MightyClaude.Core/Remote/RemoteTransport.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>Where a connection's requests go: the address checked once, at connect or refresh, and the one IP every request uses.</summary>
public sealed record RemoteTarget(RemoteAddress Address, string Ip, int Port);

public static class RemoteTransport
{
    /// <summary>Resolves a MagicDNS name (or takes a literal) and accepts it only when every address it names is a Tailscale one.</summary>
    public static async Task<RemoteTarget> ResolveAsync(RemoteAddress address, bool allowLoopback, CancellationToken token = default)
    {
        IPAddress[] found;
        if (IPAddress.TryParse(address.Host, out var literal)) found = [literal];
        else
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
            try { found = await Dns.GetHostAddressesAsync(address.Host, limit.Token); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !token.IsCancellationRequested) { throw new InvalidOperationException(Locale.Get("remote.connect.error.unresolved")); }
        }
        var ips = found.Take(32).Select(ip => TailscaleAddresses.Canonical((ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString())).Distinct(StringComparer.Ordinal).ToList();
        if (ips.Count == 0 || !ips.All(ip => RemotePeerPolicy.Allowed(ip, allowLoopback))) throw new InvalidOperationException(Locale.Get("remote.connect.error.notTailscale"));
        var chosen = ips.OrderBy(ip => ip.Contains(':')).ThenBy(ip => ip, StringComparer.Ordinal).First();
        return new(address, chosen, address.Port);
    }

    /// <summary>
    /// One authenticated request. The key rides in the Authorization header and a prompt in the body, never in the URL; a redirect
    /// is answered, never followed; no proxy, cookie or cache; the answer is read up to <paramref name="maximumBytes"/>.
    /// </summary>
    public static async Task<byte[]> RequestAsync(RemoteTarget target, string key, HttpMethod method, string path, byte[]? body = null, TimeSpan? timeout = null,
        int maximumBytes = RemoteProtocol.MaximumResponseBytes, IReadOnlyDictionary<string, string>? headers = null, CancellationToken token = default)
    {
        if (!RemoteWorkspaceValidation.Key(key) || !path.StartsWith(RemoteProtocol.PathPrefix, StringComparison.Ordinal) || !Regex.IsMatch(path, @"^[A-Za-z0-9/._:?=-]+\z"))
            throw new InvalidOperationException(Locale.Get("remote.connect.error.badRequest"));
        var limitTime = timeout ?? RemoteProtocol.RequestTimeout;
        var host = target.Ip.Contains(':') ? "[" + target.Ip + "]" : target.Ip;
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = limitTime, PooledConnectionLifetime = TimeSpan.Zero };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(limitTime);
        using var request = new HttpRequestMessage(method, new Uri("http://" + host + ":" + target.Port.ToString(CultureInfo.InvariantCulture) + path)) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation(RemoteProtocol.Header, RemoteProtocol.Version.ToString(CultureInfo.InvariantCulture));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.ConnectionClose = true;
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!response.Headers.TryGetValues(RemoteProtocol.Header, out var versions) || versions.SingleOrDefault() != RemoteProtocol.Version.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException(Locale.Get("remote.connect.error.notMighty"));
            var status = (int)response.StatusCode;
            if (status is not (>= 200 and <= 299 or >= 400 and <= 599))
                throw new InvalidDataException(Locale.Get("remote.connect.error.unexpectedStatus", new Dictionary<string, string> { ["status"] = status.ToString(CultureInfo.InvariantCulture) }));
            if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException(Locale.Get("remote.connect.error.responseTooLarge"));
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            using var data = new MemoryStream(); var buffer = new byte[16 * 1024]; int count;
            while ((count = await stream.ReadAsync(buffer, limit.Token)) > 0)
            {
                if (data.Length + count > maximumBytes) throw new InvalidDataException(Locale.Get("remote.connect.error.responseTooLarge"));
                data.Write(buffer, 0, count);
            }
            if (status >= 400)
            {
                string? message = null;
                try { using var json = JsonDocument.Parse(data.ToArray()); message = json.RootElement.Text("error"); } catch (JsonException) { }
                message ??= Locale.Get("remote.connect.error.unexpectedStatus", new Dictionary<string, string> { ["status"] = status.ToString(CultureInfo.InvariantCulture) });
                throw new RemoteHttpFailure(status, message.Length > 300 ? message[..300] : message);
            }
            return data.ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(RemoteWindowsStrings.Timeout); }
    }

    /// <summary>A failed request worth asking again for a while: the host was busy (429, 503) or the network stumbled.</summary>
    public static bool Transient(Exception error) => error switch
    {
        RemoteHttpFailure failure => failure.Transient,
        TimeoutException or HttpRequestException or IOException => true,
        _ => false,
    };
}
```

- [ ] **Step 5: Run them to verify they pass**

Run: CORE-RUN.
Expected: both new `PASS remote client: …` lines, plus every earlier `remote` line, and 9 FAILs. Also run `node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)"`, which passes.

- [ ] **Step 6: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteTransport.cs native/windows/MightyClaude.Core/Remote/RemoteWindowsStrings.cs native/windows/MightyClaude.Core.Tests/RemoteFakeHost.cs native/windows/MightyClaude.Core.Tests/RemoteClientVerification.cs native/windows/MightyClaude.Core.Tests/RemoteStringsVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs locales native/macos/Sources/MightyCore/Resources/Locales mobile/src/locales docs/i18n.md
git commit -m "(windows) a remote request carries its key only in Authorization, follows no redirect, uses no proxy or cookie, reads at most 2 MiB in 15 s, and hosts resolve only to Tailscale addresses

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 9: The client's connections

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteClientService.cs`
- Modify: `RemoteClientVerification.cs`, `Verification.cs`

**Interfaces:**
- Consumes: `StateStore.ApproveRemote` (Task 3); `RemoteTransport` (Task 8); `IRemoteKeyVault`, `RemoteConnectionFile` (Task 6); `RemoteRoute.ThroughTailscale`, `RemoteAddress` (Task 7); `RemoteWorkspaceValidation.Info` (Task 4); `RemoteWindowsStrings` (Task 2).
- Produces:

```csharp
public sealed record RemoteClientOptions
{
    public bool AllowLoopbackForTests { get; init; }
    public Func<string, bool>? RouteCheck { get; init; }
    public Func<IReadOnlyList<string>>? LocalAddresses { get; init; }
    public TimeSpan PollRetryWindow { get; init; } = RemoteProtocol.PollRetryWindow;
}
public sealed partial class RemoteClientService : IAsyncDisposable
{
    public RemoteClientService(StateStore store, string dataDirectory, IRemoteKeyVault keys, Action<RunEvent> onEvent, Action<ToolPermissionRequest> onPermission, RemoteClientOptions? options = null);
    public event Action? Changed;
    public IReadOnlyList<RemoteConnectionInfo> List();
    public RemoteConnectionInfo? Connection(string id);
    public Task<RemoteConnectionInfo> ConnectAsync(string name, string address, string key);
    public Task<RemoteConnectionInfo> RefreshAsync(string id);
    public Task<RemoteConnectionInfo> EnsureConnectedAsync(string id);
    public Task DisconnectAsync(string id);
    public Task RemoveAsync(string id);
    public Workspace OpenWorkspace(string connectionId, string hostWorkspaceId);
    public ValueTask DisposeAsync();
}
```

- [ ] **Step 1: Write the failing tests**

Append to `RemoteClientVerification`:

```csharp
    internal sealed class Collected
    {
        private readonly List<RunEvent> events = []; private readonly List<ToolPermissionRequest> permissions = [];
        internal void Event(RunEvent value) { lock (events) events.Add(value); }
        internal void Permission(ToolPermissionRequest value) { lock (events) permissions.Add(value); }
        internal RunEvent[] Events { get { lock (events) return [.. events]; } }
        internal ToolPermissionRequest[] Permissions { get { lock (events) return [.. permissions]; } }
    }

    internal static async Task<(StateStore Store, string Directory)> ClientStore()
    {
        var directory = Verification.Temp(); var store = new StateStore(directory); await store.LoadAsync(); return (store, directory);
    }

    internal static async Task ConnectionsAreSavedWithoutTheirKeyAndReadItOnFirstUse()
    {
        await using var host = new RemoteFakeHost(request => request.Target == "/v2/info" ? request.Headers["authorization"] == "Bearer " + Key ? RemoteFakeHost.Fixture("info.json") : RemoteFakeHost.Fixture("error.json", 401) : new(404, "{}"));
        var (store, directory) = await ClientStore(); var vault = new MemoryRemoteKeyVault(); var seen = new Collected();
        try
        {
            var options = new RemoteClientOptions { AllowLoopbackForTests = true };
            await using var client = new RemoteClientService(store, directory, vault, seen.Event, seen.Permission, options);
            var info = await client.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
            Check(info is { Status: RemoteConnectionStatus.Connected, Name: "studio-pc", HostName: "studio-pc", Runtime.Platform: "win32" } && info.Shared.Count == 2, "connected with the host's name, OS and shared workspaces");
            Check(info.Detail == RemoteWindowsStrings.ConnectedSaved && vault.Load(info.Id) == Key, "the key went to the vault");
            var file = await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName));
            Check(!file.Contains(Key, StringComparison.Ordinal) && file.Contains("127.0.0.1:" + host.Port, StringComparison.Ordinal), "the file names the address, never the key");
            // A new key for the same address replaces the saved one and keeps the connection.
            Check((await client.ConnectAsync("Studio", "127.0.0.1:" + host.Port, Key)).Id == info.Id && client.List().Count == 1, "same address, same connection");
            await Reject(() => client.ConnectAsync("", "127.0.0.1:" + host.Port, "short"), "a malformed key is refused", ex => ex.Message == Locale.Get("remote.connect.error.badKey"));
            await Reject(() => client.ConnectAsync("", "127.0.0.1:" + host.Port, new string('x', 43)), "a wrong key is the host's 401", ex => ex is RemoteHttpFailure { Status: 401 });
            Check(client.List().Count == 1, "a refused connect saves nothing");

            var workspace = client.OpenWorkspace(info.Id, "ws-project");
            Check(workspace.Remote is { ConnectionId: var c, WorkspaceId: "ws-project", HostName: "studio-pc" } && c == info.Id && workspace.Path == "C:\\work\\project", "the host's workspace is listed as remote");
            Check(client.OpenWorkspace(info.Id, "ws-project").Id == workspace.Id, "opening it again lists the same one");
            try { client.OpenWorkspace(info.Id, "not-shared"); throw new InvalidOperationException("an unshared workspace opened"); } catch (InvalidOperationException ex) when (ex.Message == Locale.Get("remote.workspace.error.notShared")) { }

            await client.DisconnectAsync(info.Id);
            Check(client.Connection(info.Id)?.Status == RemoteConnectionStatus.Disconnected, "disconnect keeps the connection, not connected");
            Check((await client.EnsureConnectedAsync(info.Id)).Status == RemoteConnectionStatus.Connected, "a send reconnects");

            // After a restart the connection is saved, its key unread until first use.
            await using var later = new RemoteClientService(store, directory, vault, seen.Event, seen.Permission, options);
            Check(later.List().Single() is { Status: RemoteConnectionStatus.Saved } saved && saved.Detail == Locale.Get("remote.connect.detail.saved"), "listed as saved after a restart");
            Check((await later.RefreshAsync(info.Id)).Status == RemoteConnectionStatus.Connected, "refresh reads the key from the vault and connects");
            await later.RemoveAsync(info.Id);
            Check(later.List().Count == 0 && vault.Load(info.Id) is null && RemoteConnectionFile.Read(directory).Count == 0, "remove forgets the connection and its key");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task NoConnectionLeavesThisPCsTunnel()
    {
        await using var host = new RemoteFakeHost(_ => RemoteFakeHost.Fixture("info.json"));
        var (store, directory) = await ClientStore(); var seen = new Collected();
        try
        {
            var tunnel = true;
            await using var client = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, new() { AllowLoopbackForTests = true, RouteCheck = _ => tunnel });
            var info = await client.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
            tunnel = false; var before = host.Seen.Count;
            await Reject(() => client.EnsureConnectedAsync(info.Id), "a route outside the tunnel ends the connection", ex => ex.Message == Locale.Get("remote.connect.error.notViaTailscale"));
            Check(client.Connection(info.Id)?.Status == RemoteConnectionStatus.Disconnected && host.Seen.Count == before, "disconnected, and nothing was sent");
            await Reject(() => client.RefreshAsync(info.Id), "refresh refuses the same route", ex => ex.Message == Locale.Get("remote.connect.error.notViaTailscale"));
            Check(host.Seen.Count == before, "still nothing sent");
            await using var offline = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, new() { LocalAddresses = () => [] });
            await Reject(() => offline.ConnectAsync("", "100.101.102.103", Key), "no Tailscale on this PC, no connect", ex => ex.Message == RemoteWindowsStrings.LocalTailscaleOff);
        }
        finally { Directory.Delete(directory, true); }
    }
```

Register:

```csharp
        await Test("remote client: connections are saved without their key and read it on first use", RemoteClientVerification.ConnectionsAreSavedWithoutTheirKeyAndReadItOnFirstUse);
        await Test("remote client: no connection leaves this PC's tunnel", RemoteClientVerification.NoConnectionLeavesThisPCsTunnel);
```

- [ ] **Step 2: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS0246: 'RemoteClientService'`.

- [ ] **Step 3: Implement**

`native/windows/MightyClaude.Core/Remote/RemoteClientService.cs`:

```csharp
using System.Text.Json;

namespace MightyClaude.Core;

public sealed record RemoteClientOptions
{
    /// <summary>Loopback stands in for Tailscale; only tests and the smoke run set it, in code.</summary>
    public bool AllowLoopbackForTests { get; init; }
    /// <summary>Whether the route to a host's IP still leaves from this PC's Tailscale tunnel; asked before every request.</summary>
    public Func<string, bool>? RouteCheck { get; init; }
    /// <summary>This PC's Tailscale addresses (empty while Tailscale is off); tests replace it.</summary>
    public Func<IReadOnlyList<string>>? LocalAddresses { get; init; }
    public TimeSpan PollRetryWindow { get; init; } = RemoteProtocol.PollRetryWindow;
}

/// <summary>
/// "다른 컴퓨터 연결" (M/RemoteClientService.swift): saved connections to other computers' Mighty Claude, the remote workspaces
/// opened through them, and the runs of their panes. A run's events are the host runner's own, read by cursor and applied as
/// the pane's; polling a run is also its heartbeat.
/// </summary>
public sealed partial class RemoteClientService : IAsyncDisposable
{
    private sealed class Connection(RemoteConnectionInfo info)
    {
        public RemoteConnectionInfo Info = info;
        public string? Key;
        public RemoteTarget? Target;
        /// <summary>Saved with a key in the vault that was not read yet (read on first use).</summary>
        public bool KeyPending;
        public Guid Revision = Guid.NewGuid();
    }

    private readonly StateStore store;
    private readonly string dataDirectory;
    private readonly IRemoteKeyVault keys;
    private readonly Action<RunEvent> onEvent;
    private readonly Action<ToolPermissionRequest> onPermission;
    private readonly RemoteClientOptions options;
    private readonly Func<string, bool> routeCheck;
    private readonly object gate = new();
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private readonly HashSet<string> connecting = new(StringComparer.Ordinal);
    private bool loaded, closing;

    public event Action? Changed;

    public RemoteClientService(StateStore store, string dataDirectory, IRemoteKeyVault keys, Action<RunEvent> onEvent, Action<ToolPermissionRequest> onPermission, RemoteClientOptions? options = null)
    {
        this.store = store; this.dataDirectory = dataDirectory; this.keys = keys; this.onEvent = onEvent; this.onPermission = onPermission;
        this.options = options ?? new();
        routeCheck = this.options.RouteCheck ?? (this.options.AllowLoopbackForTests ? _ => true : RemoteRoute.ThroughTailscale);
    }

    public IReadOnlyList<RemoteConnectionInfo> List() { LoadSaved(); lock (gate) return order.Select(id => connections[id].Info).ToList(); }
    public RemoteConnectionInfo? Connection(string id) { LoadSaved(); lock (gate) return connections.TryGetValue(id, out var c) ? c.Info : null; }

    /// <summary>Connects with a new address and key, or replaces the key of the saved connection at the same address.</summary>
    public async Task<RemoteConnectionInfo> ConnectAsync(string name, string address, string key)
    {
        LoadSaved();
        var label = (name ?? "").Trim(); var trimmed = (key ?? "").Trim();
        if (label.Length > 120 || !RemoteWorkspaceValidation.Key(trimmed)) throw new ArgumentException(Locale.Get("remote.connect.error.badKey"));
        var parsed = RemoteAddress.Parse(address);
        Connection? existing;
        lock (gate)
        {
            if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            existing = connections.Values.FirstOrDefault(c => c.Info.Address == parsed.Display);
            if (existing is null && connections.Count >= RemoteProtocol.MaximumConnections) throw new InvalidOperationException(Locale.Get("remote.connect.error.tooMany", new Dictionary<string, string> { ["count"] = RemoteProtocol.MaximumConnections.ToString() }));
            if (!connecting.Add(parsed.Display)) throw new InvalidOperationException(Locale.Get("remote.connect.error.busy"));
        }
        try
        {
            var (target, info) = await ReachAsync(parsed, trimmed);
            var id = existing?.Info.Id ?? Wire.Id();
            if (existing is not null) await DropRunsAsync(id, null);
            var saved = keys.Save(id, trimmed);
            var view = new RemoteConnectionInfo(id, label.Length > 0 ? label : existing?.Info.Name ?? info.HostName, parsed.Display, RemoteConnectionStatus.Connected, info.HostName, info.Workspaces, info.Runtime,
                saved ? RemoteWindowsStrings.ConnectedSaved : RemoteWindowsStrings.ConnectedMemory);
            lock (gate)
            {
                if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
                connections[id] = new Connection(view) { Key = trimmed, Target = target };
                if (!order.Contains(id)) order.Add(id);
            }
            await WriteSavedAsync(); Changed?.Invoke();
            return view;
        }
        finally { lock (gate) connecting.Remove(parsed.Display); }
    }

    /// <summary>Reconnects a saved connection; its key is read from the vault here the first time, never at launch.</summary>
    public async Task<RemoteConnectionInfo> RefreshAsync(string id)
    {
        LoadSaved();
        Connection current; Guid revision; string key;
        lock (gate)
        {
            if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            if (!connections.TryGetValue(id, out current!)) throw new InvalidOperationException(Locale.Get("remote.connect.error.unknown"));
            if (current.Key is null && current.KeyPending) { current.Key = keys.Load(id); current.KeyPending = false; }
            if (current.Key is null)
            {
                current.Info = current.Info with { Status = RemoteConnectionStatus.Disconnected, Detail = Locale.Get("remote.connect.error.keyMissing") };
                throw new InvalidOperationException(Locale.Get("remote.connect.error.keyMissing"));
            }
            if (!connecting.Add(current.Info.Address)) throw new InvalidOperationException(Locale.Get("remote.connect.error.busy"));
            key = current.Key; revision = current.Revision;
        }
        try
        {
            var (target, info) = await ReachAsync(RemoteAddress.Parse(current.Info.Address), key);
            lock (gate)
            {
                if (closing || current.Revision != revision) throw new InvalidOperationException(Locale.Get("remote.connect.error.changed"));
                current.Target = target;
                current.Info = current.Info with { Status = RemoteConnectionStatus.Connected, HostName = info.HostName, Workspaces = info.Workspaces, Runtime = info.Runtime, Detail = Locale.Get("remote.connect.detail.connected") };
            }
            Changed?.Invoke();
            return current.Info;
        }
        catch (Exception ex)
        {
            bool same; lock (gate) same = current.Revision == revision;
            if (same) await FailAsync(id, ex.Message);
            throw;
        }
        finally { lock (gate) connecting.Remove(current.Info.Address); }
    }

    /// <summary>Connected now, reconnecting first when it is not: a remote pane's send does this.</summary>
    public async Task<RemoteConnectionInfo> EnsureConnectedAsync(string id)
    {
        LoadSaved();
        var deadline = DateTimeOffset.UtcNow + RemoteProtocol.RequestTimeout + TimeSpan.FromSeconds(5);
        // Two panes sending at once after launch share one connect.
        while (true)
        {
            lock (gate) if (!connections.TryGetValue(id, out var c) || !connecting.Contains(c.Info.Address) || DateTimeOffset.UtcNow >= deadline) break;
            await Task.Delay(50);
        }
        RemoteConnectionInfo? info; RemoteTarget? target;
        lock (gate) { info = connections.TryGetValue(id, out var c) ? c.Info : null; target = connections.TryGetValue(id, out c) ? c.Target : null; }
        if (info is { Status: RemoteConnectionStatus.Connected } && target is not null) { await EnsureRouteAsync(id, target); return info; }
        return await RefreshAsync(id);
    }

    /// <summary>Stops this connection's runs and keeps it saved; a refresh connects again.</summary>
    public async Task DisconnectAsync(string id)
    {
        await DropRunsAsync(id, null);
        lock (gate)
            if (connections.TryGetValue(id, out var c)) { c.Revision = Guid.NewGuid(); c.Target = null; c.Info = c.Info with { Status = RemoteConnectionStatus.Disconnected, Detail = Locale.Get("remote.connect.detail.disconnected") }; }
        Changed?.Invoke();
    }

    /// <summary>Forgets the connection and its key. Its workspaces stay listed until removed.</summary>
    public async Task RemoveAsync(string id)
    {
        await DisconnectAsync(id);
        lock (gate) { connections.Remove(id); order.Remove(id); }
        keys.Delete(id);
        await WriteSavedAsync(); Changed?.Invoke();
    }

    /// <summary>Lists the host's shared workspace in this app as a remote workspace.</summary>
    public Workspace OpenWorkspace(string connectionId, string hostWorkspaceId)
    {
        RemoteConnectionInfo info;
        lock (gate)
        {
            if (!connections.TryGetValue(connectionId, out var c) || c.Info.Status != RemoteConnectionStatus.Connected) throw new InvalidOperationException(Locale.Get("remote.workspace.error.notShared"));
            info = c.Info;
        }
        var shared = info.Shared.FirstOrDefault(w => w.Id == hostWorkspaceId);
        if (shared is null || !RemoteWorkspaceValidation.SharedWorkspace(shared)) throw new InvalidOperationException(Locale.Get("remote.workspace.error.notShared"));
        return store.ApproveRemote(connectionId, shared.Id, shared.Name, shared.Path, info.HostName ?? info.Name);
    }

    private async Task<(RemoteTarget Target, RemoteWireInfo Info)> ReachAsync(RemoteAddress address, string key)
    {
        IReadOnlyList<string> local = options.LocalAddresses?.Invoke() ?? (options.AllowLoopbackForTests ? ["127.0.0.1"] : TailscaleAddresses.Current());
        if (local.Count == 0) throw new InvalidOperationException(RemoteWindowsStrings.LocalTailscaleOff);
        var target = await RemoteTransport.ResolveAsync(address, options.AllowLoopbackForTests);
        // The peer must be reached through this PC's own tunnel: a carrier-grade NAT network can route 100.64/10 over Wi-Fi.
        if (!routeCheck(target.Ip)) throw new InvalidOperationException(Locale.Get("remote.connect.error.notViaTailscale"));
        var data = await RemoteTransport.RequestAsync(target, key, HttpMethod.Get, RemoteProtocol.PathPrefix + "info");
        RemoteWireInfo? info;
        try { info = JsonSerializer.Deserialize<RemoteWireInfo>(data, Wire.Json); } catch (JsonException) { throw new InvalidDataException(Locale.Get("remote.connect.error.notMighty")); }
        RemoteWorkspaceValidation.Info(info);
        return (target, info!);
    }

    /// <summary>Before each request to the host: the route still goes through the tunnel, or the connection is lost (never retried).</summary>
    private async Task EnsureRouteAsync(string connectionId, RemoteTarget target)
    {
        if (routeCheck(target.Ip)) return;
        await FailAsync(connectionId, Locale.Get("remote.connect.error.notViaTailscale"));
        throw new InvalidOperationException(Locale.Get("remote.connect.error.notViaTailscale"));
    }

    /// <summary>The network or the host failed: the connection shows the error and its runs end in error.</summary>
    private async Task FailAsync(string id, string message)
    {
        lock (gate)
            if (connections.TryGetValue(id, out var c)) { c.Revision = Guid.NewGuid(); c.Target = null; c.Info = c.Info with { Status = RemoteConnectionStatus.Disconnected, Detail = message.Length > 300 ? message[..300] : message }; }
        await DropRunsAsync(id, message);
        Changed?.Invoke();
    }

    private void LoadSaved()
    {
        lock (gate)
        {
            if (loaded) return;
            loaded = true;
            foreach (var item in RemoteConnectionFile.Read(dataDirectory))
            {
                RemoteAddress address;
                try { address = RemoteAddress.Parse(item.Address); } catch (ArgumentException) { continue; }
                if (connections.ContainsKey(item.Id) || connections.Values.Any(c => c.Info.Address == address.Display)) continue;
                connections[item.Id] = new Connection(new(item.Id, item.Name, address.Display, Detail: Locale.Get("remote.connect.detail.saved"))) { KeyPending = true };
                order.Add(item.Id);
            }
        }
    }

    private async Task WriteSavedAsync()
    {
        List<RemoteSavedConnection> saved; lock (gate) saved = order.Select(id => new RemoteSavedConnection(id, connections[id].Info.Name, connections[id].Info.Address)).ToList();
        // The connection still works for this launch when the file cannot be written; only its listing is not kept.
        try { await RemoteConnectionFile.WriteAsync(dataDirectory, saved); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
```

`DropRunsAsync` and `DisposeAsync` belong to the runs half (Task 10). So that this task compiles, add `RemoteClientService.Runs.cs` now with only these two members. Task 10 fills in the rest:

```csharp
namespace MightyClaude.Core;

public sealed partial class RemoteClientService
{
    private Task DropRunsAsync(string connectionId, string? message) => Task.CompletedTask;
    public ValueTask DisposeAsync() { lock (gate) closing = true; return ValueTask.CompletedTask; }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: CORE-RUN.
Expected: both new `PASS remote client: …` lines, and 9 FAILs.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteClientService.cs native/windows/MightyClaude.Core/Remote/RemoteClientService.Runs.cs native/windows/MightyClaude.Core.Tests/RemoteClientVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) another computer is connected, refreshed, disconnected, removed and its shared workspaces opened, with the key read from the vault on first use and every request kept inside this PC's tunnel

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: The client's runs: start, poll, retry, answer, stop

**Files:**
- Modify: `native/windows/MightyClaude.Core/Remote/RemoteClientService.Runs.cs` (replace the stub)
- Modify: `RemoteClientVerification.cs`, `Verification.cs`

**Interfaces:**
- Consumes: `RemoteWireStart.From` (Task 4); `RemoteWireEvents.Read` (Task 5); `RemoteTransport` (Task 8); the connections half (Task 9).
- Produces:
  - `public bool IsRunning(string paneId)`
  - `public Task StartAsync(StartRunRequest request, Workspace workspace)`
  - `public Task StopAsync(string paneId)`
  - `public Task<bool> SteerAsync(string paneId, string text)`
  - `public Task RespondToPermissionAsync(string paneId, string requestId, bool allow)`
  - `public Task AnswerQuestionsAsync(string paneId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers)`
  - `public Task AnswerPlanAsync(string paneId, string requestId, PlanDecision decision)`

- [ ] **Step 1: Write the failing tests**

Append to `RemoteClientVerification`. `ScriptedHost` serves `/v2/info` and then whatever polls the test queues:

```csharp
    internal sealed class ScriptedHost : IAsyncDisposable
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<RemoteFakeHost.Reply> Polls = new();
        internal RemoteFakeHost.Reply? Start;
        internal readonly RemoteFakeHost Host;
        internal ScriptedHost() => Host = new RemoteFakeHost(request => request.Target switch
        {
            "/v2/info" => RemoteFakeHost.Fixture("info.json"),
            "/v2/runs" => Start ?? RemoteFakeHost.Fixture("start-accepted.json", 202),
            // Nothing queued: an empty poll at the client's own cursor, so the run just keeps waiting.
            var t when t.StartsWith("/v2/runs/job-1/events", StringComparison.Ordinal) => Polls.TryDequeue(out var reply) ? reply
                : new(200, "{\"protocol\":2,\"cursor\":" + t[(t.IndexOf("cursor=", StringComparison.Ordinal) + 7)..] + ",\"lastCursor\":" + t[(t.IndexOf("cursor=", StringComparison.Ordinal) + 7)..] + ",\"gap\":false,\"done\":false,\"events\":[]}"),
            _ => RemoteFakeHost.Fixture("answer.json"),
        });
        internal RemoteFakeHost.Request[] Seen(string prefix) => Host.Seen.Where(r => r.Target.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    internal static async Task<(RemoteClientService Client, Workspace Workspace, Collected Seen, string Directory)> Connected(ScriptedHost scripted, RemoteClientOptions? options = null)
    {
        var (store, directory) = await ClientStore(); var seen = new Collected();
        var client = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, options ?? new() { AllowLoopbackForTests = true });
        var info = await client.ConnectAsync("", "127.0.0.1:" + scripted.Host.Port, Key);
        return (client, client.OpenWorkspace(info.Id, "ws-project"), seen, directory);
    }

    internal static async Task ARunPostsTheMacsRequestAndAppliesTheHostsEventsAsThePanes()
    {
        await using var scripted = new ScriptedHost(); scripted.Polls.Enqueue(RemoteFakeHost.Fixture("poll.json"));
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            var fixture = RemoteWireFixtureVerification.Node("start.json").Deserialize<RemoteWireStart>(Wire.Json)!;
            var pane = fixture.ToRequest("pane-1", workspace.Id);
            await client.StartAsync(pane, workspace);
            await Verification.Until(() => seen.Events.Any(e => e.Type == "status" && e.Status == "completed"));
            var posted = scripted.Seen("/v2/runs").First(r => r.Method == "POST");
            Check(RemoteWireFixtureVerification.Same(System.Text.Json.Nodes.JsonNode.Parse(posted.Body), RemoteWireFixtureVerification.Node("start.json")), "the body is the Mac's start sample: " + System.Text.Encoding.UTF8.GetString(posted.Body));
            Check(posted.Headers.GetValueOrDefault("x-mighty-remote-graph") == "run", "a Windows client asks for graph runs");
            Check(seen.Events.All(e => e.SessionId == "pane-1") && seen.Events.Select(e => e.Type).SequenceEqual(["status", "resume", "log", "activity", "usage", "plan", "todos", "background", "status"]), "every usable event became the pane's, the Mac graph node left out: " + string.Join(",", seen.Events.Select(e => e.Type)));
            Check(seen.Permissions.Single() is { RunId: "pane-1", Id: "perm-1", InputJson: "{\"command\":\"ls\"}" }, "the tool request reached the pane");
            Check(!client.IsRunning("pane-1"), "a done poll ends the run");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task AnswersGoToTheHostRunAndAStopReachesTheHost()
    {
        await using var scripted = new ScriptedHost();
        var permission = RemoteWireFixtureVerification.Node("poll.json")["events"]![5]!.DeepClone();
        scripted.Polls.Enqueue(new(200, "{\"protocol\":2,\"cursor\":6,\"lastCursor\":6,\"gap\":false,\"done\":false,\"events\":[" + permission.ToJsonString() + "]}"));
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "claude", "hi", []), workspace);
            await Verification.Until(() => seen.Permissions.Length == 1);
            await client.RespondToPermissionAsync("pane-1", "perm-1", true);
            var answer = scripted.Seen("/v2/runs/job-1/permission").Single();
            Check(RemoteWireFixtureVerification.Same(System.Text.Json.Nodes.JsonNode.Parse(answer.Body), RemoteWireFixtureVerification.Node("permission.json")), "the answer is the Mac's sample, with the host's run id");
            await client.AnswerPlanAsync("pane-1", "perm-3", PlanDecision.Revise("Keep the tests"));
            Check(RemoteWireFixtureVerification.Same(System.Text.Json.Nodes.JsonNode.Parse(scripted.Seen("/v2/runs/job-1/plan").Single().Body), RemoteWireFixtureVerification.Node("plan-revised.json")), "a plan answer is the Mac's sample");
            Check(await client.SteerAsync("pane-1", "Also check CHANGELOG.md") && RemoteWireFixtureVerification.Same(System.Text.Json.Nodes.JsonNode.Parse(scripted.Seen("/v2/runs/job-1/steer").Single().Body), RemoteWireFixtureVerification.Node("steer.json")), "a steer is the Mac's sample");
            await client.StopAsync("pane-1");
            Check(scripted.Seen("/v2/runs/job-1/stop").Length == 1 && seen.Events.Last() is { Type: "status", Status: "stopped" } && !client.IsRunning("pane-1"), "a stop reaches the host and ends the pane");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task OneUnusableEventIsSkippedAndAGapIsSaid()
    {
        await using var scripted = new ScriptedHost(); scripted.Polls.Enqueue(RemoteFakeHost.Fixture("poll-malformed.json"));
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.Status == "completed"));
            Check(seen.Events.Count(e => e.Entry?.Text == Locale.Get("remote.run.notice.skipped")) == 1 && seen.Events.Any(e => e.Entry?.Text == "ok"), "the bad one is skipped and said once; the good ones arrive");
            Check(client.Connection(workspace.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Connected, "a skipped event keeps the connection");
            scripted.Polls.Enqueue(RemoteFakeHost.Fixture("poll-gap.json"));
            await client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "claude", "hi", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-2" && e.Status == "error"));
            Check(seen.Events.Any(e => e.SessionId == "pane-2" && e.Entry?.Text == Locale.Get("remote.run.notice.gap")) && seen.Events.Last(e => e.SessionId == "pane-2").Reason == "authentication", "a gap is said, and a lost sign-in reads as Windows' own");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task ABusyHostIsAskedAgainForTenSecondsAndThenTheConnectionIsLost()
    {
        await using var scripted = new ScriptedHost();
        for (var i = 0; i < 2; i++) scripted.Polls.Enqueue(new(503, "{\"protocol\":2,\"error\":\"busy\"}"));
        scripted.Polls.Enqueue(RemoteFakeHost.Fixture("poll-malformed.json"));
        var (client, workspace, seen, directory) = await Connected(scripted, new() { AllowLoopbackForTests = true, PollRetryWindow = TimeSpan.FromSeconds(5) });
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.Status == "completed"), 10000);
            Check(!seen.Events.Any(e => e.Status == "error"), "two busy answers are ridden out");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }

        await using var down = new ScriptedHost();
        for (var i = 0; i < 50; i++) down.Polls.Enqueue(new(503, "{\"protocol\":2,\"error\":\"busy\"}"));
        var (lost, place, events, folder) = await Connected(down, new() { AllowLoopbackForTests = true, PollRetryWindow = TimeSpan.FromMilliseconds(1500) });
        try
        {
            await lost.StartAsync(new StartRunRequest("pane-1", place.Id, "shell", "dir", []), place);
            await Verification.Until(() => events.Events.Any(e => e.Status == "error"), 10000);
            Check(events.Events.Any(e => e.Entry?.Kind == "error" && e.Entry.Text == Locale.Get("remote.run.error.connectionLost", new Dictionary<string, string> { ["detail"] = "busy" })), "the pane says the connection was lost, with the host's reason");
            Check(lost.Connection(place.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Disconnected, "past the window the connection is lost");
        }
        finally { await lost.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task UnsupportedRunsAndARefusedKeyNeverRun()
    {
        await using var scripted = new ScriptedHost();
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await Reject(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "claude", "hi", [], Settings: new(PermissionMode: "acceptEdits", FastMode: false, NetworkAccess: false)) with { Provider = "codex" }, workspace), "the host's missing Codex is refused locally", ex => ex.Message == Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name("codex") }));
            Check(scripted.Seen("/v2/runs").Length == 0, "nothing was posted");
            scripted.Start = RemoteFakeHost.Fixture("error.json", 401);
            await Reject(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace), "a host that no longer takes the key refuses", ex => ex is RemoteHttpFailure { Status: 401 });
            Check(client.Connection(workspace.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Disconnected, "a 401 marks the connection lost");
            await Reject(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace), "a lost connection runs nothing", ex => ex.Message == Locale.Get("remote.run.error.notConnected"));
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }
```

Add `using System.Text.Json;` at the top of the file if it is missing (it is there from Task 6). Register:

```csharp
        await Test("remote client: a run posts the Mac's request and applies the host's events as the pane's", RemoteClientVerification.ARunPostsTheMacsRequestAndAppliesTheHostsEventsAsThePanes);
        await Test("remote client: answers go to the host run and a stop reaches the host", RemoteClientVerification.AnswersGoToTheHostRunAndAStopReachesTheHost);
        await Test("remote client: one unusable event is skipped and a gap is said", RemoteClientVerification.OneUnusableEventIsSkippedAndAGapIsSaid);
        await Test("remote client: a busy host is asked again for a while, then the connection is lost", RemoteClientVerification.ABusyHostIsAskedAgainForTenSecondsAndThenTheConnectionIsLost);
        await Test("remote client: unsupported runs and a refused key never run", RemoteClientVerification.UnsupportedRunsAndARefusedKeyNeverRun);
```

- [ ] **Step 2: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS1061: 'RemoteClientService' does not contain a definition for 'StartAsync'`.

- [ ] **Step 3: Implement**

Replace `native/windows/MightyClaude.Core/Remote/RemoteClientService.Runs.cs` with:

```csharp
using System.Text.Json;

namespace MightyClaude.Core;

public sealed partial class RemoteClientService
{
    private sealed class Run(string connectionId, RemoteTarget target, string key)
    {
        public readonly string ConnectionId = connectionId;
        public readonly RemoteTarget Target = target;
        public readonly string Key = key;
        public readonly CancellationTokenSource Cancel = new();
        /// <summary>Tool requests by id, with the host's run id the answer carries (a Mac host's is its activity id).</summary>
        public readonly Dictionary<string, string> HostRunIds = new(StringComparer.Ordinal);
        public string? JobId;
        public int Cursor;
        public bool Stopping;
    }
    private static readonly IReadOnlyDictionary<string, string> GraphRunsHeader = new Dictionary<string, string> { [RemoteProtocol.GraphHeader] = RemoteProtocol.GraphRuns };
    private readonly Dictionary<string, Run> runs = new(StringComparer.Ordinal);

    public bool IsRunning(string paneId) { lock (gate) return runs.ContainsKey(paneId); }

    /// <summary>Sends one request of a remote workspace's pane to the host and follows its output.</summary>
    public async Task StartAsync(StartRunRequest request, Workspace workspace)
    {
        request = request.Validate();
        Run run; string hostWorkspaceId;
        lock (gate)
        {
            if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            if (runs.ContainsKey(request.SessionId)) throw new InvalidOperationException(Locale.Get("run.error.paneAlreadyRunning"));
            if (runs.Count >= RemoteProtocol.MaximumRuns) throw new InvalidOperationException(Locale.Get("run.error.concurrentLimit"));
            if (workspace.Id != request.WorkspaceId || workspace.Remote is not { } reference || !connections.TryGetValue(reference.ConnectionId, out var connection)
                || connection.Info.Status != RemoteConnectionStatus.Connected || connection.Target is not { } target || connection.Key is not { } key || !connection.Info.Shared.Any(w => w.Id == reference.WorkspaceId))
                throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
            if (request.Kind == "claude")
            {
                if (connection.Info.Runtime?.Providers?.FirstOrDefault(p => p.Id == request.Provider) is not { Available: true } provider)
                    throw new InvalidOperationException(Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(request.Provider) }));
                if (RemoteWorkspaceValidation.CapabilityProblem(request, provider.Capabilities) is { } problem) throw new InvalidOperationException(problem);
            }
            run = new Run(reference.ConnectionId, target, key); hostWorkspaceId = reference.WorkspaceId;
            runs[request.SessionId] = run;
        }
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(RemoteWireStart.From(request, hostWorkspaceId), Wire.Json);
            if (body.Length > AttachmentSupport.MaximumRequestBytes || request.Attachments is null && body.Length > RemoteProtocol.MaximumRequestBytes) throw new InvalidOperationException(Locale.Get("remote.run.error.tooLarge"));
            await EnsureRouteAsync(run.ConnectionId, run.Target);
            var data = await RemoteTransport.RequestAsync(run.Target, run.Key, HttpMethod.Post, RemoteProtocol.PathPrefix + "runs", body,
                request.Attachments is null ? TimeSpan.FromSeconds(30) : RemoteProtocol.UploadTimeout, headers: GraphRunsHeader);
            RemoteWireJob? accepted;
            try { accepted = JsonSerializer.Deserialize<RemoteWireJob>(data, Wire.Json); } catch (JsonException) { accepted = null; }
            if (accepted is null || accepted.Protocol != RemoteProtocol.Version || !Wire.Identifier(accepted.JobId)) throw new InvalidDataException(Locale.Get("remote.connect.error.notMighty"));
            bool cancelled;
            lock (gate) { cancelled = closing || !runs.TryGetValue(request.SessionId, out var current) || current != run || run.Stopping; run.JobId = accepted.JobId; }
            if (cancelled)
            {
                await StopOnHostAsync(run);
                lock (gate) if (runs.TryGetValue(request.SessionId, out var current) && current == run) runs.Remove(request.SessionId);
                throw new OperationCanceledException();
            }
            _ = Task.Run(() => FollowAsync(request.SessionId, run));
        }
        catch (Exception ex)
        {
            lock (gate) if (runs.TryGetValue(request.SessionId, out var current) && current == run) runs.Remove(request.SessionId);
            // The host no longer takes this key (it restarted sharing): the connection is not connected any more.
            if (ex is RemoteHttpFailure { Status: 401 } failure) await FailAsync(run.ConnectionId, failure.Message);
            throw;
        }
    }

    private bool Current(string paneId, Run run) { lock (gate) return runs.TryGetValue(paneId, out var current) && current == run && !run.Stopping; }

    private async Task FollowAsync(string paneId, Run run)
    {
        var quiet = 0; DateTimeOffset? failingSince = null;
        while (Current(paneId, run))
        {
            byte[] data;
            try
            {
                if (!routeCheck(run.Target.Ip)) { await FailAsync(run.ConnectionId, Locale.Get("remote.connect.error.notViaTailscale")); return; }
                data = await RemoteTransport.RequestAsync(run.Target, run.Key, HttpMethod.Get, $"{RemoteProtocol.PathPrefix}runs/{run.JobId}/events?cursor={run.Cursor}", token: run.Cancel.Token);
                failingSince = null;
            }
            catch (OperationCanceledException) when (run.Cancel.IsCancellationRequested) { return; }
            catch (Exception ex) when (RemoteTransport.Transient(ex) && DateTimeOffset.UtcNow - (failingSince ?? DateTimeOffset.UtcNow) < options.PollRetryWindow)
            {
                // Asked again for a while, well inside the host's heartbeat.
                failingSince ??= DateTimeOffset.UtcNow;
                if (!await Pause(run, TimeSpan.FromSeconds(1))) return;
                continue;
            }
            catch (Exception ex) { if (Current(paneId, run)) await FailAsync(run.ConnectionId, ex.Message); return; }
            if (!Current(paneId, run)) return;
            RemoteWirePoll? poll;
            try { poll = JsonSerializer.Deserialize<RemoteWirePoll>(data, Wire.Json); } catch (JsonException) { poll = null; }
            if (poll is null || poll.Protocol != RemoteProtocol.Version || poll.Cursor < run.Cursor || poll.LastCursor < poll.Cursor || poll.Events is null || poll.Events.Count > RemoteProtocol.MaximumPollEvents)
            { await FailAsync(run.ConnectionId, Locale.Get("remote.run.error.badOutput")); return; }
            if (poll.Gap) onEvent(RunEvent.Log(paneId, "system", Locale.Get("remote.run.notice.gap")));
            var cursor = run.Cursor; var terminal = false; var skipped = false;
            foreach (var item in poll.Events)
            {
                if (item.Cursor <= cursor || item.Cursor > poll.Cursor) { await FailAsync(run.ConnectionId, Locale.Get("remote.run.error.badOutput")); return; }
                cursor = item.Cursor;
                // One event this app cannot use is left out, and said so; the run goes on.
                var incoming = RemoteWireEvents.Read(item.Event, run.JobId!, paneId, graphRuns: true);
                if (!incoming.Usable) { skipped = true; continue; }
                if (incoming.Permission is { } permission) { lock (gate) run.HostRunIds[permission.Id] = incoming.HostRunId!; onPermission(permission); continue; }
                if (incoming.Event is not { } value) continue;
                onEvent(value);
                if (value.Type == "status" && value.Status is "completed" or "error" or "stopped") terminal = true;
            }
            if (skipped) onEvent(RunEvent.Log(paneId, "system", Locale.Get("remote.run.notice.skipped")));
            run.Cursor = poll.Cursor;
            if (poll.Done)
            {
                lock (gate) if (runs.TryGetValue(paneId, out var current) && current == run) runs.Remove(paneId);
                if (!terminal) onEvent(RunEvent.State(paneId, "error"));
                return;
            }
            // More waits on the host: read it soon (at most ten polls a second per run). Otherwise wait longer while nothing comes.
            if (poll.LastCursor > run.Cursor) { quiet = 0; if (!await Pause(run, TimeSpan.FromMilliseconds(100))) return; continue; }
            quiet = poll.Events.Count == 0 ? Math.Min(quiet + 1, 4) : 0;
            if (!await Pause(run, TimeSpan.FromMilliseconds(250 + 150 * quiet))) return;
        }
    }

    private static async Task<bool> Pause(Run run, TimeSpan delay)
    {
        try { await Task.Delay(delay, run.Cancel.Token); return true; } catch (OperationCanceledException) { return false; }
    }

    public async Task StopAsync(string paneId)
    {
        Run? run;
        lock (gate) { if (!runs.TryGetValue(paneId, out run) || run.Stopping) return; run.Stopping = true; }
        run.Cancel.Cancel();
        await StopOnHostAsync(run);
        lock (gate) { if (!runs.TryGetValue(paneId, out var current) || current != run) return; runs.Remove(paneId); }
        onEvent(RunEvent.State(paneId, "stopped"));
    }

    private async Task StopOnHostAsync(Run run)
    {
        if (run.JobId is null || !routeCheck(run.Target.Ip)) return;
        try { await RemoteTransport.RequestAsync(run.Target, run.Key, HttpMethod.Post, $"{RemoteProtocol.PathPrefix}runs/{run.JobId}/stop", "{}"u8.ToArray(), TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private async Task<bool> SendAsync<T>(string paneId, string action, Func<Run, T> body)
    {
        Run run;
        lock (gate) if (!runs.TryGetValue(paneId, out run!) || run.Stopping || run.JobId is null) throw new InvalidOperationException(Locale.Get("run.error.permissionRunGone"));
        await EnsureRouteAsync(run.ConnectionId, run.Target);
        var data = await RemoteTransport.RequestAsync(run.Target, run.Key, HttpMethod.Post, $"{RemoteProtocol.PathPrefix}runs/{run.JobId}/{action}", JsonSerializer.SerializeToUtf8Bytes(body(run), Wire.Json));
        try { return JsonSerializer.Deserialize<RemoteWireAnswer>(data, Wire.Json)?.Accepted == true; } catch (JsonException) { return false; }
    }
    private string HostRunId(Run run, string requestId) { lock (gate) return run.HostRunIds.GetValueOrDefault(requestId) ?? run.JobId!; }

    /// <summary>Hands a follow-up to the running Claude turn on the host; false when it did not take it.</summary>
    public async Task<bool> SteerAsync(string paneId, string text) { try { return await SendAsync(paneId, "steer", _ => new RemoteWireSteer(text)); } catch (Exception ex) when (ex is not OutOfMemoryException) { return false; } }
    public Task RespondToPermissionAsync(string paneId, string requestId, bool allow) => SendAsync(paneId, "permission", run => new RemoteWirePermission(HostRunId(run, requestId), requestId, allow));
    public Task AnswerQuestionsAsync(string paneId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers) => SendAsync(paneId, "questions", run => new RemoteWireQuestions(HostRunId(run, requestId), requestId, answers));
    public Task AnswerPlanAsync(string paneId, string requestId, PlanDecision decision) => SendAsync(paneId, "plan", run => RemoteWirePlan.From(HostRunId(run, requestId), requestId, decision));

    private async Task DropRunsAsync(string connectionId, string? message)
    {
        List<(string Pane, Run Run)> active;
        lock (gate) { active = runs.Where(p => p.Value.ConnectionId == connectionId).Select(p => (p.Key, p.Value)).ToList(); foreach (var (pane, _) in active) runs.Remove(pane); }
        foreach (var (pane, run) in active)
        {
            run.Cancel.Cancel();
            if (message is not null)
            {
                onEvent(RunEvent.Log(pane, "error", Locale.Get("remote.run.error.connectionLost", new Dictionary<string, string> { ["detail"] = message.Length > 300 ? message[..300] : message })));
                onEvent(RunEvent.State(pane, "error"));
            }
            else onEvent(RunEvent.State(pane, "stopped"));
        }
        // The host stops a run it no longer hears from anyway; this is only sooner.
        foreach (var (_, run) in active) await StopOnHostAsync(run);
    }

    public async ValueTask DisposeAsync()
    {
        string[] panes; lock (gate) { closing = true; panes = [.. runs.Keys]; }
        foreach (var pane in panes) await StopAsync(pane);
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: CORE-RUN.
Expected: every `remote client: …` line PASSes, and 9 FAILs.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteClientService.Runs.cs native/windows/MightyClaude.Core.Tests/RemoteClientVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a remote pane's request runs on the host and its events, tool requests, plans and steering come and go in the Mac's shape; a busy host is asked again for 10 s, one unusable event is skipped with a notice, and a lost key or route ends the connection

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
## Stage D — The host

### Task 11: A `TcpListener` HTTP/1.1 server that refuses at accept and reads no body without leave

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteHttpListener.cs`
- Create: `native/windows/MightyClaude.Core.Tests/RemoteHostVerification.cs`
- Modify: `Verification.cs` (register)

**Interfaces:**
- Consumes: `AttachmentSupport.MaximumRequestBytes` (12 MiB), `RemoteProtocol.RequestTimeout`/`UploadTimeout`, and the existing `http.error.*` keys.
- Produces:
  - `public sealed record RemoteHttpRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body, string RemoteAddress)`. Header names are lower-case.
  - `public sealed record RemoteHttpResponse(int Status, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)`, with `static Json(int, object, headers?)`.
  - `public sealed class RemoteHttpListener(IPAddress address, int port, Func<RemoteHttpRequest, Task<RemoteHttpResponse>> handler, Func<RemoteHttpRequest, int>? bodyLimit = null, Func<IPAddress, bool>? acceptPeer = null, int? maximumConnectionsPerPeer = null) : IAsyncDisposable`, with `int Start()` (returns the bound port) and `int Port`.

- [ ] **Step 1: Write the failing tests**

`native/windows/MightyClaude.Core.Tests/RemoteHostVerification.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

/// <summary>"이 컴퓨터 공유" on Windows (docs/remote-workspaces.md): the listener, the host, and host and client over loopback.</summary>
internal static partial class RemoteHostVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /// <summary>One raw exchange, for requests HttpClient will not send (no version header, an Origin, a bad head).</summary>
    internal static async Task<string> Raw(int port, string head, byte[]? body = null)
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        try { await stream.WriteAsync(Encoding.ASCII.GetBytes(head)); if (body is not null) await stream.WriteAsync(body); } catch (IOException) { }
        using var answer = new MemoryStream(); var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { int read; while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0) answer.Write(buffer, 0, read); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        return Encoding.UTF8.GetString(answer.ToArray());
    }

    internal static async Task ARefusedPeerIsCutOffAtAcceptAndOnePeerHoldsAtMostItsShare()
    {
        var handled = 0;
        await using (var refused = new RemoteHttpListener(IPAddress.Loopback, 0, _ => { Interlocked.Increment(ref handled); return Task.FromResult(RemoteHttpResponse.Json(200, new { })); }, acceptPeer: _ => false))
        {
            var port = refused.Start();
            Check(await Raw(port, "GET /v2/info HTTP/1.1\r\n\r\n") == "" && handled == 0, "a refused peer gets nothing and nothing is handled");
        }
        var release = new TaskCompletionSource();
        await using var capped = new RemoteHttpListener(IPAddress.Loopback, 0, async _ => { await release.Task; return RemoteHttpResponse.Json(200, new { ok = true }); }, maximumConnectionsPerPeer: 2);
        var cappedPort = capped.Start();
        var held = new[] { Raw(cappedPort, "GET /a HTTP/1.1\r\n\r\n"), Raw(cappedPort, "GET /b HTTP/1.1\r\n\r\n") };
        await Task.Delay(300);
        Check(await Raw(cappedPort, "GET /c HTTP/1.1\r\n\r\n") == "", "a third connection from the same peer is closed at accept");
        release.SetResult();
        Check((await Task.WhenAll(held)).All(r => r.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)), "the two held ones are answered");
        Check((await Raw(cappedPort, "GET /d HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "a freed slot takes a new connection");
    }

    internal static async Task NoBodyIsReadWithoutLeaveAndTheHeadIsBounded()
    {
        var seenBody = -1;
        await using var server = new RemoteHttpListener(IPAddress.Loopback, 0, request => { seenBody = request.Body.Length; return Task.FromResult(RemoteHttpResponse.Json(request.Headers.ContainsKey("authorization") ? 200 : 401, new { })); },
            bodyLimit: head => head.Headers.ContainsKey("authorization") ? 1024 : 0);
        var port = server.Start();
        Check((await Raw(port, "POST /v2/runs HTTP/1.1\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n\r\n")).StartsWith("HTTP/1.1 401", StringComparison.Ordinal) && seenBody == 0, "the head alone is answered; no body byte is read");
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: 2048\r\n\r\n", new byte[2048])).StartsWith("HTTP/1.1 413", StringComparison.Ordinal), "over the head's limit is 413");
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: 2\r\n\r\n{}")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && seenBody == 2, "within it, read whole");
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "chunked bodies are refused");
        Check((await Raw(port, "GET /v2/x HTTP/1.1\r\nX-Big: " + new string('a', 8300) + "\r\n\r\n")).StartsWith("HTTP/1.1 431", StringComparison.Ordinal), "an oversize head is 431");
        Check((await Raw(port, "GET /v2/x HTTP/1.1\r\nA: 1\r\na: 2\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a repeated header is refused");
        Check((await Raw(port, "DELETE /v2/x HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "only GET and POST");
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: 2\r\n\r\n{}GET / HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "one request per connection");
        var answer = await Raw(port, "GET /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\n\r\n");
        Check(answer.Contains("Connection: close", StringComparison.Ordinal) && answer.Contains("Cache-Control: no-store", StringComparison.Ordinal) && answer.Contains("X-Content-Type-Options: nosniff", StringComparison.Ordinal), "every answer closes and is never cached");
    }
}
```

Register:

```csharp
        await Test("remote host: a refused peer is cut off at accept and one peer holds at most its share", RemoteHostVerification.ARefusedPeerIsCutOffAtAcceptAndOnePeerHoldsAtMostItsShare);
        await Test("remote host: no body is read without leave and the head is bounded", RemoteHostVerification.NoBodyIsReadWithoutLeaveAndTheHeadIsBounded);
```

- [ ] **Step 2: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS0246: 'RemoteHttpListener'`.

- [ ] **Step 3: Implement**

`native/windows/MightyClaude.Core/Remote/RemoteHttpListener.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed record RemoteHttpRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body, string RemoteAddress);
public sealed record RemoteHttpResponse(int Status, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static RemoteHttpResponse Json(int status, object value, IReadOnlyDictionary<string, string>? headers = null) => new(status, JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json), headers);
}

/// <summary>
/// A bounded HTTP/1.1 endpoint on one address (M/Remote/HTTPServer.swift). Each connection carries one request and closes. A peer is
/// refused at accept, before anything is read, when <c>acceptPeer</c> says no or it already holds its share of connections. The
/// head decides how much body is read; 0 means none, and the handler answers the head alone.
/// </summary>
public sealed class RemoteHttpListener(IPAddress address, int port, Func<RemoteHttpRequest, Task<RemoteHttpResponse>> handler,
    Func<RemoteHttpRequest, int>? bodyLimit = null, Func<IPAddress, bool>? acceptPeer = null, int? maximumConnectionsPerPeer = null) : IAsyncDisposable
{
    public const int HeaderLimit = 8_192, DefaultBodyLimit = 512 * 1024, MaximumConnections = 64;
    private static readonly Dictionary<int, string> Reasons = new()
    {
        [200] = "OK", [202] = "Accepted", [400] = "Bad Request", [401] = "Unauthorized", [403] = "Forbidden", [404] = "Not Found", [409] = "Conflict", [413] = "Payload Too Large",
        [415] = "Unsupported Media Type", [426] = "Upgrade Required", [429] = "Too Many Requests", [431] = "Request Header Fields Too Large", [500] = "Internal Server Error", [503] = "Service Unavailable",
    };
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, int> peerConnections = new(StringComparer.Ordinal);
    private readonly HashSet<Socket> open = [];
    private TcpListener? listener;
    private Task loop = Task.CompletedTask;

    public int Port => listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;

    /// <summary>Binds the one address (no dual-stack, no shared port) and starts accepting; the port bound.</summary>
    public int Start()
    {
        if (listener is not null || stop.IsCancellationRequested) throw new InvalidOperationException(Locale.Get("http.error.alreadyStarted"));
        var created = new TcpListener(address, port);
        if (address.AddressFamily == AddressFamily.InterNetworkV6) created.Server.DualMode = false;
        if (OperatingSystem.IsWindows()) created.ExclusiveAddressUse = true;
        created.Start(MaximumConnections);
        listener = created;
        loop = Task.Run(AcceptLoopAsync);
        return Port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener!.AcceptSocketAsync(stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
            var remote = socket.RemoteEndPoint is IPEndPoint endpoint ? endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address : IPAddress.None;
            var peer = remote.ToString();
            bool admitted;
            lock (open)
            {
                admitted = acceptPeer?.Invoke(remote) != false && open.Count < MaximumConnections && (maximumConnectionsPerPeer is not { } cap || peerConnections.GetValueOrDefault(peer) < cap);
                if (admitted) { open.Add(socket); peerConnections[peer] = peerConnections.GetValueOrDefault(peer) + 1; }
            }
            if (!admitted) { Abort(socket); continue; }
            _ = Task.Run(() => ServeAsync(socket, peer));
        }
    }

    private static void Abort(Socket socket)
    {
        try { socket.LingerState = new LingerOption(true, 0); socket.Close(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(Socket socket, string peer)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(RemoteProtocol.RequestTimeout);
        var stream = new NetworkStream(socket, ownsSocket: false);
        var drain = false;
        try
        {
            var (response, unread) = await ReadAndAnswerAsync(stream, peer, deadline);
            drain = unread;
            if (response is not null) await WriteAsync(stream, response, deadline.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            try
            {
                socket.Shutdown(SocketShutdown.Send);
                // A refused head (431, 400, 413) may leave request bytes unread; reading them briefly lets the answer arrive instead of a reset.
                if (drain) { var sink = new byte[8192]; socket.ReceiveTimeout = 1000; for (var total = 0; total < 64 * 1024;) { var read = socket.Receive(sink); if (read == 0) break; total += read; } }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            await stream.DisposeAsync(); socket.Close();
            lock (open) { open.Remove(socket); var left = peerConnections.GetValueOrDefault(peer) - 1; if (left > 0) peerConnections[peer] = left; else peerConnections.Remove(peer); }
        }
    }

    private static (RemoteHttpResponse, bool) Refuse(int status, string key) => (RemoteHttpResponse.Json(status, new { error = Locale.Get(key) }), true);

    private async Task<(RemoteHttpResponse? Response, bool Unread)> ReadAndAnswerAsync(NetworkStream stream, string peer, CancellationTokenSource deadline)
    {
        var buffer = new byte[HeaderLimit + 1]; var filled = 0; int end;
        while ((end = HeadEnd(buffer, filled)) < 0)
        {
            if (filled >= buffer.Length) return Refuse(431, "http.error.headerTooLarge");
            var read = await stream.ReadAsync(buffer.AsMemory(filled), deadline.Token);
            if (read == 0) return (null, false);
            filled += read;
        }
        var lines = Encoding.UTF8.GetString(buffer, 0, end).Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length != 3 || start[0] is not ("GET" or "POST") || start[2] != "HTTP/1.1" || !start[1].StartsWith('/') || lines.Length > 34) return Refuse(400, "http.error.badRequest");
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.StartsWith(' ') || line.StartsWith('\t')) return Refuse(400, "http.error.badHeader");
            var name = line[..colon].ToLowerInvariant();
            if (!Regex.IsMatch(name, @"^[a-z0-9-]+\z") || headers.ContainsKey(name)) return Refuse(400, "http.error.duplicateHeader");
            headers[name] = line[(colon + 1)..].Trim(' ', '\t');
        }
        if (headers.ContainsKey("transfer-encoding")) return Refuse(400, "http.error.lengthRequired");
        var head = new RemoteHttpRequest(start[0], start[1], headers, [], peer);
        var limit = Math.Min(AttachmentSupport.MaximumRequestBytes, Math.Max(0, bodyLimit?.Invoke(head) ?? DefaultBodyLimit));
        var lengthText = headers.GetValueOrDefault("content-length") ?? "0";
        if (!Regex.IsMatch(lengthText, @"^[0-9]{1,9}\z") || !int.TryParse(lengthText, out var length) || length > limit && limit != 0) return Refuse(413, "http.error.badLength");
        // Nothing of the body is read for a head the server refuses it to: the handler answers the head alone (an unauthorized request's 401).
        if (limit == 0) return (await handler(head), false);
        if (length > DefaultBodyLimit) deadline.CancelAfter(RemoteProtocol.UploadTimeout);
        var already = filled - (end + 4);
        if (already > length) return Refuse(400, "http.error.oneRequest");
        var body = new byte[length];
        Array.Copy(buffer, end + 4, body, 0, already);
        for (var got = already; got < length;)
        {
            var read = await stream.ReadAsync(body.AsMemory(got), deadline.Token);
            if (read == 0) return (null, false);
            got += read;
        }
        // The arrival deadline covered slow delivery; the handler gets its own generous one, as on the Mac.
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        return (await handler(head with { Body = body }), false);
    }

    private static int HeadEnd(byte[] buffer, int filled)
    {
        for (var i = 0; i + 3 < filled; i++) if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, RemoteHttpResponse response, CancellationToken token)
    {
        var head = new StringBuilder($"HTTP/1.1 {response.Status} {Reasons.GetValueOrDefault(response.Status, "Response")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {response.Body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
        foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
            if (name.IndexOfAny(['\r', '\n']) < 0 && value.IndexOfAny(['\r', '\n']) < 0) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token);
        await stream.WriteAsync(response.Body, token);
        await stream.FlushAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener?.Stop();
        Socket[] sockets; lock (open) sockets = [.. open];
        foreach (var socket in sockets) Abort(socket);
        try { await loop; } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: CORE-RUN.
Expected: both `PASS remote host: …` lines, and 9 FAILs.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteHttpListener.cs native/windows/MightyClaude.Core.Tests/RemoteHostVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the sharing server is a one-request HTTP/1.1 listener on one address that drops refused peers at accept, caps connections per peer and reads no body its head does not allow

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Host hooks in `RunManager`: graph nodes, PowerShell commands, `IRemoteRunner`

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteCommandShell.cs`
- Modify: `native/windows/MightyClaude.Core/RunManager.cs`. This touches the interface block (lines 5–9), the class declaration (line 17), the public properties (~49–54), the tracker callback (~line 122), the shell branch (~line 216) and `ChildProcess.Start` (~line 256).
- Modify: `RemoteHostVerification.cs`, `Verification.cs`

**Interfaces:**
- Consumes: `ChildProcess.QuoteWindows`, `WindowsCommand.NoCurrentDirectorySearch`, and the `ExecutionGraphTracker` emit callback.
- Produces:

```csharp
public interface IRemoteRunner : IAsyncDisposable
{
    Task StartAsync(StartRunRequest request);
    Task StopAsync(string sessionId);
    Task<bool> TrySteerAsync(string sessionId, string text);
    void RespondToToolPermission(string sessionId, string requestId, bool allow);
    void AnswerQuestionnaire(string sessionId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers);
    PlanRecord AnswerPlan(string sessionId, string requestId, PlanDecision decision);
}
public sealed record RemoteRunnerSinks(Action<RunEvent> Event, Action<ToolPermissionRequest> Permission, Action<string, ExecutionGraphNode> GraphNode);
// RunManager : IRunManager, IRemoteRunner
public Action<string, ExecutionGraphNode>? GraphNodes { get; set; }
public bool PowerShellCommands { get; set; }
public static class RemoteCommandShell
{
    public static (string Binary, IReadOnlyList<string> Arguments) Launch(string command, string systemFolder);
    public static string CommandLine(string command, string systemFolder);
}
```

- [ ] **Step 1: Write the failing test**

Append to `RemoteHostVerification`:

```csharp
    internal static async Task CommandsRunInPowerShellFromTheSystemFolderAndGraphNodesReachTheHost()
    {
        var (binary, arguments) = RemoteCommandShell.Launch("Get-Location; echo \"a b\"", @"C:\Windows\System32\");
        Check(binary == @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" && arguments.SequenceEqual(["-NoProfile", "-NonInteractive", "-Command", "Get-Location; echo \"a b\""]), "powershell.exe -NoProfile -NonInteractive -Command <text>, from the system folder");
        var line = RemoteCommandShell.CommandLine("echo \"a b\"", @"C:\Windows\System32");
        Check(line.Contains(" -NoProfile -NonInteractive -Command ", StringComparison.Ordinal) && line.EndsWith(ChildProcess.QuoteWindows("echo \"a b\""), StringComparison.Ordinal), "the text is one quoted argument: " + line);

        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin")); await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
            var events = new ConcurrentQueue<RunEvent>(); var nodes = new ConcurrentQueue<(string Session, ExecutionGraphNode Node)>();
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "codex", Path.Combine(directory, "codex"))));
            await using var runner = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, events.Enqueue, _ => { }) { PowerShellCommands = true, GraphNodes = (id, node) => nodes.Enqueue((id, node)) };
            IRemoteRunner remote = runner;
            await remote.StartAsync(new StartRunRequest("job-a", workspace.Id, "claude", "hello", [], Provider: "codex"));
            await Verification.Until(() => events.Any(e => e.SessionId == "job-a" && e.Status is "completed" or "error"), 20000);
            Check(nodes.Any(n => n.Session == "job-a" && n.Node.Id.Length > 0), "the run's graph nodes reach the host as they change");
            await remote.StartAsync(new StartRunRequest("job-b", workspace.Id, "shell", "echo remote-ok", []));
            await Verification.Until(() => events.Any(e => e.SessionId == "job-b" && e.Status is "completed" or "error"), 20000);
            Check(events.Any(e => e.SessionId == "job-b" && e.Entry?.Text.Contains("remote-ok", StringComparison.Ordinal) == true), "a command runs (PowerShell on Windows, /bin/sh elsewhere)");
        }
        finally { Directory.Delete(directory, true); }
    }
```

Register:

```csharp
        await Test("remote host: commands run in PowerShell from the system folder and graph nodes reach the host", RemoteHostVerification.CommandsRunInPowerShellFromTheSystemFolderAndGraphNodesReachTheHost);
```

- [ ] **Step 2: Run it to verify it fails**

Run: CORE-RUN.
Expected: the build fails with `CS0103: 'RemoteCommandShell'` and `CS0117: 'RunManager' does not contain a definition for 'GraphNodes'`.

- [ ] **Step 3: Implement**

`native/windows/MightyClaude.Core/Remote/RemoteCommandShell.cs`:

```csharp
namespace MightyClaude.Core;

/// <summary>
/// A remote command pane's text on a Windows host: Windows PowerShell from the system folder Windows reports (never a PATH or
/// current-folder lookup), without the user's profile, one process per command in the shared folder.
/// </summary>
public static class RemoteCommandShell
{
    public static (string Binary, IReadOnlyList<string> Arguments) Launch(string command, string systemFolder) =>
        (systemFolder.TrimEnd('\\', '/') + @"\WindowsPowerShell\v1.0\powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command]);

    /// <summary>The command line CreateProcess receives (ChildProcess quotes each argument the same way).</summary>
    public static string CommandLine(string command, string systemFolder)
    {
        var (binary, arguments) = Launch(command, systemFolder);
        return string.Join(" ", new[] { binary }.Concat(arguments).Select(ChildProcess.QuoteWindows));
    }
}
```

`RunManager.cs`:
1. After the `IRunManager` interface, add `IRemoteRunner` and `RemoteRunnerSinks` exactly as in **Interfaces** above.
2. Change the class declaration to `public sealed class RunManager(…) : IRunManager, IRemoteRunner`.
3. Add these after `DelegationHandler`:

```csharp
    /// <summary>Every execution-graph node of a run as it changes, by session id: a sharing host sends them as the Mac's "graph" events.</summary>
    public Action<string, ExecutionGraphNode>? GraphNodes { get; set; }
    /// <summary>A sharing host's runner: a shell request runs in Windows PowerShell (RemoteCommandShell) instead of cmd.exe.</summary>
    public bool PowerShellCommands { get; set; }
```

4. Replace the tracker callback `_ => Interlocked.Exchange(ref graphDirty, 1)` with:

```csharp
node => { Interlocked.Exchange(ref graphDirty, 1); GraphNodes?.Invoke(request.SessionId, node); }
```

5. Replace the shell line (`if (request.Kind == "shell") { binary = … }`) with:

```csharp
            if (request.Kind == "shell")
            {
                if (PowerShellCommands && OperatingSystem.IsWindows())
                {
                    (binary, var shellArguments) = RemoteCommandShell.Launch(request.Input, Environment.GetFolderPath(Environment.SpecialFolder.System));
                    arguments = shellArguments;
                    // The command runs in the shared folder: nothing there may stand in for a program it names.
                    environment[WindowsCommand.NoCurrentDirectorySearch] = "1";
                }
                else { binary = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? "C:\\Windows\\System32\\cmd.exe" : "/bin/sh"; arguments = OperatingSystem.IsWindows() ? ["/d", "/s", "/c", request.Input] : ["-c", request.Input]; }
            }
```

6. In the `ChildProcess.Start(…)` call, change the second argument to `OperatingSystem.IsWindows() && request.Kind == "shell" && !PowerShellCommands ? request.Input : null`, so PowerShell's arguments are quoted one by one and never pass through `cmd /s /c`.

- [ ] **Step 4: Run it to verify it passes**

Run: CORE-RUN.
Expected: `PASS remote host: commands run in PowerShell …`, all earlier `remote` lines and every existing `RunManager` check still pass, and 9 FAILs.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteCommandShell.cs native/windows/MightyClaude.Core/RunManager.cs native/windows/MightyClaude.Core.Tests/RemoteHostVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a sharing host's runner runs command panes in PowerShell from the system folder and hands every graph node to the host

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 13: The host: sharing, the key, authorization and `/info`

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/RemoteHostService.cs` (lifecycle, authorization, `/info`; Task 14 adds runs in `RemoteHostService.Runs.cs`)
- Modify: `RemoteHostVerification.cs`, `Verification.cs`

**Interfaces:**
- Consumes: `RemoteHttpListener` (Task 11); `IRemoteRunner`, `RemoteRunnerSinks` (Task 12); `RemotePeerPolicy`, `RemoteAddress` (Task 7); `RemoteWorkspaceValidation` (Task 4); `TailscaleAddresses.Current` (phone-direct).
- Produces:

```csharp
public sealed record RemoteHostOptions
{
    public required Func<IReadOnlyList<Workspace>> Workspaces { get; init; }
    public required Func<string, Task<Workspace>> ResolveLocal { get; init; }
    public required Func<Task<RuntimeInfo>> Runtime { get; init; }
    public required Func<RemoteRunnerSinks, IRemoteRunner> CreateRunner { get; init; }
    public required string HostName { get; init; }
    public Func<StartRunRequest, string?> Admission { get; init; }
    public Action<int> RunsChanged { get; init; }
    public bool AllowLoopbackForTests { get; init; }
    public TimeSpan Heartbeat { get; init; }
    public Func<IReadOnlyList<string>>? Addresses { get; init; }
    public Func<DateTimeOffset> Clock { get; init; }
}
public sealed class RemoteRateLimiter(Func<DateTimeOffset> clock) { public bool AllowPeer(string peer); public bool AllowKeyed(); }
public sealed partial class RemoteHostService(RemoteHostOptions options) : IAsyncDisposable
{
    public RemoteHostState Current();
    public RemoteHostState SetAllowsFullAccess(bool allowed);
    public Task<RemoteHostState> StartAsync(IReadOnlyList<string> workspaceIds, int port = RemoteProtocol.DefaultPort);
    public Task<RemoteHostState> StopAsync();
    public static int BodyLimit(RemoteHttpRequest head, string key, bool allowLoopback);
    public static string? FullAccessRefusal(StartRunRequest request, bool allowed);
    public ValueTask DisposeAsync();
}
```

- [ ] **Step 1: Write the failing tests**

Append to `RemoteHostVerification`:

```csharp
    internal static string Key => RemoteClientVerification.Key;

    /// <summary>A scripted runner for the host checks: "hold" waits for a stop, "ask" asks one tool request, "graph" sends a node and a graph run.</summary>
    internal sealed class FakeRemoteRunner(RemoteRunnerSinks sinks) : IRemoteRunner
    {
        internal readonly ConcurrentQueue<StartRunRequest> Started = new();
        internal readonly ConcurrentQueue<string> Stopped = new();
        internal readonly ConcurrentQueue<(string Session, string Request, bool Allow)> Answers = new();
        private readonly ConcurrentDictionary<string, bool> waiting = new();
        public Task StartAsync(StartRunRequest request)
        {
            Started.Enqueue(request); var id = request.SessionId;
            sinks.Event(RunEvent.State(id, "running"));
            sinks.Event(RunEvent.Log(id, "output", "echo:" + request.Input));
            switch (request.Input)
            {
                case "hold": waiting[id] = true; break;
                case "ask": waiting[id] = true; sinks.Permission(new ToolPermissionRequest("perm-1", id, "toolu_1", "Bash", "{\"command\":\"ls\"}", "ls", ReceivedAt: "2026-10-17T01:02:05.000Z")); break;
                case "graph":
                    sinks.GraphNode(id, new ExecutionGraphNode("node-1", "run-1", null, "main", "completed", "Request"));
                    var run = RemoteWireFixtureVerification.Node("graph-run-event.json")["event"]!["graphRun"].Deserialize<MightyGraphRun>(Wire.Json)!;
                    sinks.Event(new RunEvent(id, "graph_run", GraphRun: run));
                    sinks.Event(RunEvent.State(id, "completed")); break;
                default: sinks.Event(RunEvent.State(id, "completed")); break;
            }
            return Task.CompletedTask;
        }
        public Task StopAsync(string id) { Stopped.Enqueue(id); if (waiting.TryRemove(id, out _)) sinks.Event(RunEvent.State(id, "stopped")); return Task.CompletedTask; }
        public Task<bool> TrySteerAsync(string id, string text) => Task.FromResult(waiting.ContainsKey(id));
        public void RespondToToolPermission(string id, string requestId, bool allow)
        {
            Answers.Enqueue((id, requestId, allow));
            sinks.Permission(new ToolPermissionRequest(requestId, id, "toolu_1", "Bash", "{}", "ls", State: allow ? "allowed" : "denied"));
            if (waiting.TryRemove(id, out _)) sinks.Event(RunEvent.State(id, "completed"));
        }
        public void AnswerQuestionnaire(string id, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers) => Answers.Enqueue((id, requestId, true));
        public PlanRecord AnswerPlan(string id, string requestId, PlanDecision decision) { Answers.Enqueue((id, requestId, decision.Kind != "cancel")); return new(requestId, id, "plan", Wire.Now(), Wire.Now(), decision.Outcome); }
        public ValueTask DisposeAsync() { foreach (var id in waiting.Keys) _ = StopAsync(id); return ValueTask.CompletedTask; }
    }

    internal sealed class HostFixture : IAsyncDisposable
    {
        internal required string Directory; internal required StateStore Store; internal required Workspace Shared, Private, Remote;
        internal required RemoteHostService Host; internal FakeRemoteRunner? Runner;
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }

    internal static async Task<HostFixture> MakeHost(TimeSpan? heartbeat = null, Func<StartRunRequest, string?>? admission = null, Func<HostFixture, RemoteRunnerSinks, IRemoteRunner>? runner = null)
    {
        var directory = Verification.Temp(); var store = new StateStore(Path.Combine(directory, "host")); await store.LoadAsync();
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "project")); System.IO.Directory.CreateDirectory(Path.Combine(directory, "private"));
        var shared = store.ApproveLocal(Path.Combine(directory, "project")); var hidden = store.ApproveLocal(Path.Combine(directory, "private"));
        var remote = store.ApproveRemote("conn-x", "ws-x", "far", "C:\\far", "other");
        await store.SaveAsync(new AppSnapshot { Workspaces = [shared, hidden, remote] });
        HostFixture fixture = null!;
        var host = new RemoteHostService(new RemoteHostOptions
        {
            Workspaces = () => store.Snapshot.Workspaces, ResolveLocal = store.ResolveLocalAsync,
            Runtime = () => Task.FromResult(RemoteWireFixtureVerification.Node("info.json")["runtime"].Deserialize<RuntimeInfo>(Wire.Json)!),
            CreateRunner = sinks => runner?.Invoke(fixture, sinks) ?? (fixture.Runner = new FakeRemoteRunner(sinks)),
            HostName = "studio-pc", AllowLoopbackForTests = true, Heartbeat = heartbeat ?? RemoteProtocol.HeartbeatTimeout, Admission = admission ?? (_ => null),
        });
        return fixture = new HostFixture { Directory = directory, Store = store, Shared = shared, Private = hidden, Remote = remote, Host = host };
    }

    internal static Task OnlyAKeyedRunRequestGetsTheAttachmentBody()
    {
        var key = new string('k', 43);
        RemoteHttpRequest Head(string target = "/v2/runs", Dictionary<string, string>? extra = null, string peer = "100.90.1.1")
        {
            var headers = new Dictionary<string, string> { ["authorization"] = "Bearer " + key, ["x-mighty-remote-version"] = "2" };
            foreach (var (name, value) in extra ?? []) headers[name] = value;
            return new("POST", target, headers, [], peer);
        }
        Check(RemoteHostService.BodyLimit(Head(), key, false) == AttachmentSupport.MaximumRequestBytes, "a keyed run request may carry attachments");
        Check(RemoteHostService.BodyLimit(Head(extra: new() { ["authorization"] = "Bearer " + new string('x', 43) }), key, false) == 0, "a wrong key reads nothing");
        Check(RemoteHostService.BodyLimit(Head(extra: new() { ["x-mighty-remote-version"] = "1" }), key, false) == 0, "an old protocol reads nothing");
        Check(RemoteHostService.BodyLimit(Head(extra: new() { ["origin"] = "https://example.com" }), key, false) == 0, "a browser page reads nothing");
        Check(RemoteHostService.BodyLimit(Head("/v2/runs/x/steer"), key, false) == RemoteProtocol.MaximumRequestBytes, "other routes get 512 KiB");
        Check(RemoteHostService.BodyLimit(Head(peer: "192.168.1.5"), key, false) == 0 && RemoteHostService.BodyLimit(Head(peer: "127.0.0.1"), key, false) == 0, "non-Tailscale peers read nothing");
        Check(RemoteHostService.BodyLimit(Head(peer: "127.0.0.1"), key, true) == AttachmentSupport.MaximumRequestBytes, "loopback only when injected");
        var shell = new StartRunRequest("j", "w", "shell", "dir", []);
        var full = new StartRunRequest("j", "w", "claude", "hi", [], Settings: new(PermissionMode: "fullAccess"));
        var edits = new StartRunRequest("j", "w", "claude", "hi", [], Settings: new(PermissionMode: "acceptEdits"));
        Check(RemoteHostService.FullAccessRefusal(shell, false) == Locale.Get("remote.host.error.commandsOff") && RemoteHostService.FullAccessRefusal(full, false) == Locale.Get("remote.host.error.fullAccessOff"), "commands and full access wait for the switch");
        Check(RemoteHostService.FullAccessRefusal(edits, false) is null && RemoteHostService.FullAccessRefusal(shell, true) is null && RemoteHostService.FullAccessRefusal(full with { Provider = "codex" }, true) is null, "the switch allows them; other modes never needed it");
        var now = DateTimeOffset.UnixEpoch; var limiter = new RemoteRateLimiter(() => now);
        Check(Enumerable.Range(0, 300).All(_ => limiter.AllowPeer("100.90.1.1")) && !limiter.AllowPeer("100.90.1.1") && limiter.AllowPeer("100.90.1.2"), "300 a second per peer, each peer its own budget");
        Check(Enumerable.Range(0, 600).All(_ => limiter.AllowKeyed()) && !limiter.AllowKeyed(), "600 keyed requests a second in all");
        now += TimeSpan.FromSeconds(1);
        Check(limiter.AllowPeer("100.90.1.1") && limiter.AllowKeyed(), "the next second starts afresh");
        return Task.CompletedTask;
    }

    internal static async Task SharingStartsWithANewKeyAndStopRevokesIt()
    {
        await using var fixture = await MakeHost();
        var host = fixture.Host;
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([]), "nothing picked", ex => ex.Message == Locale.Get("remote.host.error.pickWorkspace"));
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([fixture.Remote.Id]), "a remote workspace is never shared", ex => ex.Message == RemoteWindowsStrings.HostLocalOnly);
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([fixture.Shared.Id], 80), "ports below 1024 are refused", ex => ex.Message == Locale.Get("remote.connect.error.badPort"));
        var state = await host.StartAsync([fixture.Shared.Id], 0);
        Check(state is { Enabled: true, AllowsFullAccess: false, Port: > 0 } && RemoteWorkspaceValidation.Key(state.Key) && state.Addresses.SequenceEqual(["127.0.0.1:" + state.Port]), "sharing on loopback (injected), a fresh key, full access off");
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([fixture.Shared.Id], 0), "one sharing at a time", ex => ex.Message == Locale.Get("remote.host.error.busy"));
        var target = RemoteClientVerification.Loopback(state.Port!.Value);
        var info = JsonSerializer.Deserialize<RemoteWireInfo>(await RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Get, "/v2/info"), Wire.Json)!;
        RemoteWorkspaceValidation.Info(info);
        Check(info is { Protocol: 2, HostName: "studio-pc" } && info.Workspaces.Single().Id == fixture.Shared.Id && info.Runtime.Platform == "win32", "only the shared workspace, and the host's OS");
        await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(target, new string('x', 43), HttpMethod.Get, "/v2/info"), "a wrong key is 401", ex => ex is RemoteHttpFailure { Status: 401 });
        Check((await Raw(state.Port.Value, $"GET /v2/info HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 1\r\n\r\n")).StartsWith("HTTP/1.1 426", StringComparison.Ordinal), "an old protocol is 426");
        Check((await Raw(state.Port.Value, $"GET /v2/info HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\nOrigin: https://example.com\r\n\r\n")).StartsWith("HTTP/1.1 403", StringComparison.Ordinal), "a browser page is 403");
        var answer = await Raw(state.Port.Value, $"GET /v2/nothing HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\n\r\n");
        Check(answer.StartsWith("HTTP/1.1 404", StringComparison.Ordinal) && answer.Contains("x-mighty-remote-version: 2", StringComparison.Ordinal), "every answer carries the version");
        Check(host.SetAllowsFullAccess(true).AllowsFullAccess, "the switch turns on while sharing");
        var stopped = await host.StopAsync();
        Check(!stopped.Enabled && stopped.Key is null && !stopped.AllowsFullAccess, "stopping forgets the key and the switch");
        await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Get, "/v2/info"), "nothing answers after a stop");
        var again = await host.StartAsync([fixture.Shared.Id], 0);
        Check(again.Key != state.Key && !again.AllowsFullAccess, "a new sharing has a new key and the switch off");
        await host.DisposeAsync();
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([fixture.Shared.Id], 0), "no sharing after shutdown", ex => ex.Message == Locale.Get("common.appClosing"));
    }
```

In `RemoteClientVerification`, change `private static async Task Reject(…)` to `internal static async Task RejectAsync(…)`, keep `Reject` as a one-line forwarder, and make `Loopback` `internal` (it already is).

Register:

```csharp
        await Test("remote host: only a keyed run request gets the attachment body", RemoteHostVerification.OnlyAKeyedRunRequestGetsTheAttachmentBody);
        await Test("remote host: sharing starts with a new key and a stop revokes it", RemoteHostVerification.SharingStartsWithANewKeyAndStopRevokesIt);
```

- [ ] **Step 2: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS0246: 'RemoteHostService'`.

- [ ] **Step 3: Implement**

`native/windows/MightyClaude.Core/Remote/RemoteHostService.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MightyClaude.Core;

public sealed record RemoteHostOptions
{
    /// <summary>The saved workspaces; only local ones the user picked are ever shared.</summary>
    public required Func<IReadOnlyList<Workspace>> Workspaces { get; init; }
    /// <summary>A registered local workspace whose folder exists (StateStore.ResolveLocalAsync), or why not.</summary>
    public required Func<string, Task<Workspace>> ResolveLocal { get; init; }
    /// <summary>This PC's CLIs as /info reports them, with the app's version and platform "win32".</summary>
    public required Func<Task<RuntimeInfo>> Runtime { get; init; }
    /// <summary>The runner client runs go through: no pane MCP, no delegation, no picture cache.</summary>
    public required Func<RemoteRunnerSinks, IRemoteRunner> CreateRunner { get; init; }
    public required string HostName { get; init; }
    /// <summary>Why this PC cannot start a client's run now (its CLIs or plugins are changing); null when it can.</summary>
    public Func<StartRunRequest, string?> Admission { get; init; } = _ => null;
    /// <summary>Told how many client runs go on this PC whenever that changes: while any does, CLI updates wait.</summary>
    public Action<int> RunsChanged { get; init; } = _ => { };
    /// <summary>Loopback stands in for Tailscale; only tests and the smoke run set it, in code.</summary>
    public bool AllowLoopbackForTests { get; init; }
    public TimeSpan Heartbeat { get; init; } = RemoteProtocol.HeartbeatTimeout;
    public Func<IReadOnlyList<string>>? Addresses { get; init; }
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
}

/// <summary>
/// A client polls each of its runs at most ten times a second. Each peer has its own budget of 300 a second; only requests with
/// the key count toward the shared 600, so a device without it cannot crowd the real client out.
/// </summary>
public sealed class RemoteRateLimiter(Func<DateTimeOffset> clock)
{
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> peers = new(StringComparer.Ordinal);
    private DateTimeOffset window = DateTimeOffset.MinValue; private int keyed;
    public bool AllowPeer(string peer)
    {
        lock (peers)
        {
            var now = clock();
            var entry = peers.TryGetValue(peer, out var known) ? known : (Start: now, Count: 0);
            if (now - entry.Start >= TimeSpan.FromSeconds(1)) entry = (now, 0);
            entry.Count++;
            if (!peers.ContainsKey(peer) && peers.Count >= 64) peers.Clear();
            peers[peer] = entry;
            return entry.Count <= 300;
        }
    }
    public bool AllowKeyed()
    {
        lock (peers)
        {
            var now = clock();
            if (now - window >= TimeSpan.FromSeconds(1)) { window = now; keyed = 0; }
            return ++keyed <= 600;
        }
    }
}

/// <summary>
/// "이 컴퓨터 공유" (M/RemoteHostService.swift): serves the chosen local workspaces to other Mighty Claude apps on this PC's Tailscale
/// addresses only. Runs go through this PC's own RunManager with its CLIs and sign-ins; their events wait in a per-run buffer the
/// client reads by cursor. A run its client stops polling for the heartbeat is stopped. Sharing never survives the app; stopping
/// it revokes the key.
/// </summary>
public sealed partial class RemoteHostService(RemoteHostOptions options) : IAsyncDisposable
{
    private sealed class HostFailure(int status, string message) : Exception(message) { public int Status { get; } = status; }
    private static readonly IReadOnlyDictionary<string, string> VersionHeaders = new Dictionary<string, string> { [RemoteProtocol.Header] = RemoteProtocol.Version.ToString() };

    private readonly object gate = new();
    private readonly string hostId = Wire.Id();
    private readonly RemoteRateLimiter rates = new(options.Clock);
    private readonly List<RemoteHttpListener> servers = [];
    private RemoteHostState state = new();
    private IRemoteRunner? runner;
    private Guid generation = Guid.NewGuid();
    private CancellationTokenSource? lease;
    private bool changing, closed, allowsFullAccess;

    public RemoteHostState Current() { lock (gate) return state with { ActiveRuns = jobs.Values.Count(j => !j.Done), AllowsFullAccess = allowsFullAccess }; }

    /// <summary>Whether clients may run commands and full-access agents here; takes effect from the next run they start.</summary>
    public RemoteHostState SetAllowsFullAccess(bool allowed) { lock (gate) allowsFullAccess = allowed; return Current(); }

    /// <summary>What a client may not run here while full access is off: a command pane, or an agent in full access; null when it may.</summary>
    public static string? FullAccessRefusal(StartRunRequest request, bool allowed) =>
        allowed ? null : request.Kind == "shell" ? Locale.Get("remote.host.error.commandsOff") : request.Settings?.PermissionMode == "fullAccess" ? Locale.Get("remote.host.error.fullAccessOff") : null;

    /// <summary>Starts sharing <paramref name="workspaceIds"/> (registered local workspaces) on every Tailscale address this PC has, with a new key.</summary>
    public async Task<RemoteHostState> StartAsync(IReadOnlyList<string> workspaceIds, int port = RemoteProtocol.DefaultPort)
    {
        Guid revision;
        lock (gate)
        {
            if (changing || state.Enabled || servers.Count > 0) throw new InvalidOperationException(Locale.Get("remote.host.error.busy"));
            if (workspaceIds.Count is 0 or > RemoteProtocol.MaximumSharedWorkspaces || workspaceIds.Distinct(StringComparer.Ordinal).Count() != workspaceIds.Count || !workspaceIds.All(Wire.Identifier))
                throw new ArgumentException(Locale.Get("remote.host.error.pickWorkspace"));
            if (!(port is >= 1024 and <= 65535 || options.AllowLoopbackForTests && port == 0)) throw new ArgumentException(Locale.Get("remote.connect.error.badPort"));
            if (closed) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            changing = true; revision = generation = Guid.NewGuid();
        }
        try
        {
            var saved = options.Workspaces();
            foreach (var id in workspaceIds)
            {
                if (!saved.Any(w => w.Id == id && w.Remote is null)) throw new InvalidOperationException(RemoteWindowsStrings.HostLocalOnly);
                await options.ResolveLocal(id);
            }
            Checked(revision);
            Func<IReadOnlyList<string>> source = options.Addresses ?? (options.AllowLoopbackForTests ? () => ["127.0.0.1"] : TailscaleAddresses.Current);
            var addresses = source();
            if (addresses.Count == 0) throw new InvalidOperationException(RemoteWindowsStrings.HostNoTailscale);
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var created = options.CreateRunner(new(value => Receive(value, revision), value => ReceivePermission(value, revision), (id, node) => ReceiveGraph(id, node, revision)));
            lock (gate) runner = created;
            var bound = port;
            foreach (var address in addresses)
            {
                Checked(revision);
                // Refused at accept: anything but a Tailscale peer, and a ninth connection from one peer.
                var server = new RemoteHttpListener(IPAddress.Parse(address), bound, request => ServeAsync(request, revision), head => BodyLimit(head, key, options.AllowLoopbackForTests),
                    peer => RemotePeerPolicy.Allowed(peer, options.AllowLoopbackForTests), RemoteProtocol.MaximumConnectionsPerPeer);
                lock (gate) servers.Add(server);
                bound = server.Start();
            }
            lock (gate)
            {
                if (revision != generation || closed) throw new OperationCanceledException();
                state = new RemoteHostState { Enabled = true, Key = key, Port = bound, WorkspaceIds = [.. workspaceIds], Addresses = addresses.Select(a => new RemoteAddress(a, bound).Display).ToList() };
                allowsFullAccess = false;
                lease = new CancellationTokenSource();
                _ = LeaseLoopAsync(revision, lease.Token);
            }
            return Current();
        }
        catch (Exception ex)
        {
            bool mine; lock (gate) mine = revision == generation;
            // A stop that came in meanwhile already closed what this start had opened.
            if (mine) await StopNowAsync();
            if (ex is OperationCanceledException) throw new InvalidOperationException(Locale.Get("remote.host.error.startCancelled"));
            if (ex is SocketException) throw new InvalidOperationException(Locale.Get("remote.host.error.listenFailed", new Dictionary<string, string> { ["error"] = ex.Message }));
            throw;
        }
        finally { lock (gate) changing = false; }
    }

    private void Checked(Guid revision) { lock (gate) if (revision != generation || closed) throw new OperationCanceledException(); }

    /// <summary>Stops sharing: every remote run here stops and the key is gone for good.</summary>
    public async Task<RemoteHostState> StopAsync() { await StopNowAsync(); return Current(); }

    private async Task StopNowAsync()
    {
        List<RemoteHttpListener> old; IRemoteRunner? oldRunner; CancellationTokenSource? oldLease;
        lock (gate)
        {
            generation = Guid.NewGuid(); old = [.. servers]; servers.Clear(); oldRunner = runner; runner = null;
            state = new(); allowsFullAccess = false; oldLease = lease; lease = null; jobs.Clear();
        }
        oldLease?.Cancel();
        options.RunsChanged(0);
        foreach (var server in old) await server.DisposeAsync();
        if (oldRunner is not null) await oldRunner.DisposeAsync();
    }

    public async ValueTask DisposeAsync() { lock (gate) closed = true; await StopNowAsync(); }

    /// <summary>
    /// How much body is read, decided from the head before any of it: none without the key (the head alone gets its 401), the larger
    /// attachment body only for an authenticated run request.
    /// </summary>
    public static int BodyLimit(RemoteHttpRequest head, string key, bool allowLoopback)
    {
        var authorization = head.Headers.GetValueOrDefault("authorization") ?? "";
        if (head.Headers.ContainsKey("origin") || !RemotePeerPolicy.Allowed(head.RemoteAddress, allowLoopback) || head.Headers.GetValueOrDefault(RemoteProtocol.Header) != "2"
            || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 256 || !RemoteWorkspaceValidation.KeysMatch(authorization[7..], key)) return 0;
        return head.Method == "POST" && head.Target == RemoteProtocol.PathPrefix + "runs" ? AttachmentSupport.MaximumRequestBytes : RemoteProtocol.MaximumRequestBytes;
    }

    /// <summary>Every request: sharing on, no browser origin, a Tailscale peer, within the rate limits, the right key, the protocol version.</summary>
    private void Authorize(RemoteHttpRequest request, Guid revision)
    {
        string key;
        lock (gate)
        {
            if (!state.Enabled || revision != generation || state.Key is not { } current) throw new HostFailure(503, Locale.Get("remote.host.error.stopped"));
            key = current;
        }
        if (request.Headers.ContainsKey("origin") || !RemotePeerPolicy.Allowed(request.RemoteAddress, options.AllowLoopbackForTests)) throw new HostFailure(403, Locale.Get("remote.host.error.tailscaleOnly"));
        if (!rates.AllowPeer(request.RemoteAddress)) throw new HostFailure(429, Locale.Get("remote.host.error.tooMany"));
        var authorization = request.Headers.GetValueOrDefault("authorization") ?? "";
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 256 || !RemoteWorkspaceValidation.KeysMatch(authorization[7..], key)) throw new HostFailure(401, Locale.Get("remote.host.error.badKey"));
        if (!rates.AllowKeyed()) throw new HostFailure(429, Locale.Get("remote.host.error.tooMany"));
        if (request.Headers.GetValueOrDefault(RemoteProtocol.Header) != "2") throw new HostFailure(426, Locale.Get("remote.host.error.version"));
    }

    private static RemoteHttpResponse Reply(int status, object value) => RemoteHttpResponse.Json(status, value, VersionHeaders);
    private static RemoteHttpResponse Failure(int status, string message) => Reply(status, new RemoteWireError(RemoteProtocol.Version, message.Length > 1000 ? message[..1000] : message));

    private async Task<RemoteHttpResponse> ServeAsync(RemoteHttpRequest request, Guid revision)
    {
        try
        {
            Authorize(request, revision);
            if (!request.Target.StartsWith(RemoteProtocol.PathPrefix, StringComparison.Ordinal) || !Uri.TryCreate("http://host" + request.Target, UriKind.Absolute, out var url)) throw new HostFailure(400, Locale.Get("remote.host.error.badPath"));
            var route = url.AbsolutePath[RemoteProtocol.PathPrefix.Length..].Split('/');
            var query = url.Query;
            if (request.Method == "GET" && route is ["info"] && query.Length == 0) return await InfoAsync(revision);
            return await ServeRunsAsync(request, route, query, revision);
        }
        catch (HostFailure failure) { return Failure(failure.Status, failure.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Failure(500, ex.Message); }
    }

    private List<Workspace> SharedWorkspaces()
    {
        IReadOnlyList<string> ids; lock (gate) ids = state.WorkspaceIds;
        return options.Workspaces().Where(w => w.Remote is null && ids.Contains(w.Id) && RemoteWorkspaceValidation.SharedWorkspace(new(w.Id, w.Name, w.Path))).ToList();
    }

    private async Task<RemoteHttpResponse> InfoAsync(Guid revision)
    {
        var shared = SharedWorkspaces();
        var runtime = await options.Runtime();
        lock (gate) if (revision != generation) throw new HostFailure(503, Locale.Get("remote.host.error.stopped"));
        var name = options.HostName.Length == 0 ? "Windows" : options.HostName.Length > 120 ? options.HostName[..120] : options.HostName;
        return Reply(200, new RemoteWireInfo(RemoteProtocol.Version, hostId, name, shared.Select(w => new RemoteSharedWorkspace(w.Id, w.Name, w.Path)).ToList(), runtime));
    }
}
```

So that this task compiles, create `native/windows/MightyClaude.Core/Remote/RemoteHostService.Runs.cs` with the members Task 14 fills in:

```csharp
namespace MightyClaude.Core;

public sealed partial class RemoteHostService
{
    private sealed class Job { public bool Done; }
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private Task<RemoteHttpResponse> ServeRunsAsync(RemoteHttpRequest request, string[] route, string query, Guid revision) => throw new HostFailure(404, Locale.Get("remote.host.error.badPath"));
    private void Receive(RunEvent value, Guid revision) { }
    private void ReceivePermission(ToolPermissionRequest value, Guid revision) { }
    private void ReceiveGraph(string sessionId, ExecutionGraphNode node, Guid revision) { }
    private Task LeaseLoopAsync(Guid revision, CancellationToken token) => Task.CompletedTask;
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: CORE-RUN.
Expected: both new `PASS remote host: …` lines, and 9 FAILs.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteHostService.cs native/windows/MightyClaude.Core/Remote/RemoteHostService.Runs.cs native/windows/MightyClaude.Core.Tests/RemoteHostVerification.cs native/windows/MightyClaude.Core.Tests/RemoteClientVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) this PC shares its picked local workspaces on its Tailscale addresses with a fresh key each time, answers /info with its CLIs and OS, refuses wrong keys, old protocols, browser pages and floods, and forgets the key and the full-access switch on stop

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: The host's runs, and host and client together over loopback

**Files:**
- Modify: `native/windows/MightyClaude.Core/Remote/RemoteHostService.Runs.cs` (replace the stub)
- Create: `native/windows/MightyClaude.Core.Tests/RemoteLoopbackVerification.cs`
- Modify: `Verification.cs`

**Interfaces:**
- Consumes: `RemoteWireEvents.FromRunEvent`/`FromPermission`/`FromGraphNode` (Task 5); `RemoteWireStart.ToRequest` (Task 4); the client (Tasks 9–10); `RunManager` with `PowerShellCommands`/`GraphNodes` (Task 12).
- Produces: routes `POST /v2/runs`, `GET /v2/runs/{id}/events?cursor=N`, and `POST /v2/runs/{id}/stop|steer|permission|questions|plan`. Also the per-run buffer (4096 events / 4 MiB), the poll limits (256 events / 1.5 MiB), the gap, the heartbeat stop, the pruning, and `RunsChanged`.

- [ ] **Step 1: Write the failing tests**

`native/windows/MightyClaude.Core.Tests/RemoteLoopbackVerification.cs`:

```csharp
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

/// <summary>Host and client of remote workspaces in one process over loopback (injected in code, as only checks may).</summary>
internal static class RemoteLoopbackVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Key => RemoteClientVerification.Key;

    private static async Task<(RemoteHostVerification.HostFixture Host, RemoteClientService Client, Workspace Workspace, RemoteClientVerification.Collected Seen, string Folder, RemoteHostState State)> Pair(
        TimeSpan? heartbeat = null, Func<StartRunRequest, string?>? admission = null, Func<RemoteHostVerification.HostFixture, RemoteRunnerSinks, IRemoteRunner>? runner = null)
    {
        var fixture = await RemoteHostVerification.MakeHost(heartbeat, admission, runner);
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var (store, folder) = await RemoteClientVerification.ClientStore(); var seen = new RemoteClientVerification.Collected();
        var client = new RemoteClientService(store, folder, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, new() { AllowLoopbackForTests = true });
        var info = await client.ConnectAsync("", "127.0.0.1:" + state.Port, state.Key!);
        return (fixture, client, client.OpenWorkspace(info.Id, fixture.Shared.Id), seen, folder, state);
    }

    internal static async Task CommandsAndFullAccessWaitForTheSwitchThenRunOnTheHost()
    {
        var (fixture, client, workspace, seen, folder, _) = await Pair();
        try
        {
            await RemoteClientVerification.RejectAsync(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace), "commands wait for the switch", ex => ex is RemoteHttpFailure { Status: 403 } && ex.Message == Locale.Get("remote.host.error.commandsOff"));
            await RemoteClientVerification.RejectAsync(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "claude", "hi", [], Settings: new(PermissionMode: "fullAccess")), workspace), "full access waits for the switch", ex => ex is RemoteHttpFailure { Status: 403 } && ex.Message == Locale.Get("remote.host.error.fullAccessOff"));
            Check(fixture.Runner is null || fixture.Runner.Started.IsEmpty, "nothing ran");
            fixture.Host.SetAllowsFullAccess(true);
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-1" && e.Status == "completed"));
            Check(seen.Events.Where(e => e.SessionId == "pane-1").Select(e => e.Type + ":" + (e.Status ?? e.Entry?.Text)).SequenceEqual(["status:running", "log:echo:dir", "status:completed"]), "the host's events became the pane's in order");
            var ran = fixture.Runner!.Started.Single();
            Check(ran.WorkspaceId == fixture.Shared.Id && ran.SessionId != "pane-1", "the host ran it in its shared workspace under its own job id");
            Check(fixture.Host.Current().ActiveRuns == 0, "a finished run is not active");
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task ToolRequestsAreAnsweredFromTheClientAndGraphRunsGoOnlyWhereAsked()
    {
        var (fixture, client, workspace, seen, folder, state) = await Pair();
        try
        {
            await client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "claude", "ask", []), workspace);
            await Verification.Until(() => seen.Permissions.Any(p => p.State == "pending"));
            Check(seen.Permissions[0] is { RunId: "pane-2", Id: "perm-1", InputJson: "{\"command\":\"ls\"}" }, "the host's tool request is the pane's");
            await client.RespondToPermissionAsync("pane-2", "perm-1", true);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-2" && e.Status == "completed"));
            var answer = fixture.Runner!.Answers.Single();
            Check(answer.Request == "perm-1" && answer.Allow && answer.Session == fixture.Runner.Started.Single().SessionId, "the answer reached the host's run");
            Check(seen.Permissions.Any(p => p.Id == "perm-1" && p.State == "allowed"), "the settled request reached the pane");

            await client.StartAsync(new StartRunRequest("pane-3", workspace.Id, "claude", "graph", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-3" && e.Status == "completed"));
            Check(seen.Events.Any(e => e.SessionId == "pane-3" && e.Type == "graph_run" && e.GraphRun?.Id == "gr-1"), "a Windows client that asked gets graph runs");
            // A client that did not ask (the Mac's) gets graph nodes and no graph runs.
            var target = RemoteClientVerification.Loopback(state.Port!.Value);
            var start = new RemoteWireStart(new RemoteWireRequest("mac-pane", fixture.Shared.Id, "claude", "graph", "default", "claude", new()), []);
            var job = JsonSerializer.Deserialize<RemoteWireJob>(await RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Post, "/v2/runs", JsonSerializer.SerializeToUtf8Bytes(start, Wire.Json)), Wire.Json)!;
            await Task.Delay(300);
            var poll = Encoding.UTF8.GetString(await RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Get, $"/v2/runs/{job.JobId}/events?cursor=0"));
            Check(poll.Contains("\"type\":\"graph\"", StringComparison.Ordinal) && !poll.Contains("graph_run", StringComparison.Ordinal), "a Mac client gets the Mac's graph events: " + poll);
            Check(System.Text.RegularExpressions.Regex.IsMatch(poll, "\"updatedAt\":\"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z\""), "every timestamp is one the Mac reads");
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task UnsharedFoldersBusyHostsAndOversizeRequestsAreRefused()
    {
        var holding = false;
        var (fixture, client, workspace, seen, folder, state) = await Pair(admission: _ => holding ? Locale.Get("run.blocked.changingPlugins") : null);
        try
        {
            var target = RemoteClientVerification.Loopback(state.Port!.Value);
            var unshared = new RemoteWireStart(new RemoteWireRequest("x", fixture.Private.Id, "claude", "hi", "default", "claude", new()), []);
            await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Post, "/v2/runs", JsonSerializer.SerializeToUtf8Bytes(unshared, Wire.Json)), "an unshared workspace is refused", ex => ex is RemoteHttpFailure { Status: 403 } && ex.Message == Locale.Get("remote.host.error.notShared"));
            holding = true;
            await RemoteClientVerification.RejectAsync(() => client.StartAsync(new StartRunRequest("pane-4", workspace.Id, "claude", "hi", []), workspace), "a host busy with its own update holds the run", ex => ex is RemoteHttpFailure { Status: 503, Transient: true });
            holding = false;
            var huge = await RemoteHostVerification.Raw(state.Port.Value, $"POST /v2/runs HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\nContent-Type: application/json\r\nContent-Length: {13 * 1024 * 1024}\r\n\r\n");
            Check(huge.StartsWith("HTTP/1.1 413", StringComparison.Ordinal), "over 12 MiB is refused before reading");
            var bad = await RemoteHostVerification.Raw(state.Port.Value, $"GET /v2/runs/nope/events?cursor=0 HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\n\r\n");
            Check(bad.StartsWith("HTTP/1.1 404", StringComparison.Ordinal), "an unknown run is gone");
            Check(fixture.Runner is null || fixture.Runner.Started.IsEmpty, "nothing refused ever ran");
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task ARunNobodyPollsIsStoppedAndAStoppedHostEndsTheClientsRuns()
    {
        var (fixture, client, workspace, seen, folder, state) = await Pair(heartbeat: TimeSpan.FromSeconds(1));
        try
        {
            fixture.Host.SetAllowsFullAccess(true);
            var target = RemoteClientVerification.Loopback(state.Port!.Value);
            var start = new RemoteWireStart(new RemoteWireRequest("silent", fixture.Shared.Id, "shell", "hold", "default", "claude", new()), []);
            var job = JsonSerializer.Deserialize<RemoteWireJob>(await RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Post, "/v2/runs", JsonSerializer.SerializeToUtf8Bytes(start, Wire.Json)), Wire.Json)!;
            await Verification.Until(() => fixture.Runner!.Stopped.Contains(job.JobId), 6000);
            var poll = JsonSerializer.Deserialize<RemoteWirePoll>(await RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Get, $"/v2/runs/{job.JobId}/events?cursor=0"), Wire.Json)!;
            Check(poll.Done && poll.Events[^1].Event["status"]?.GetValue<string>() == "stopped", "an unpolled run was stopped after the heartbeat");

            await client.StartAsync(new StartRunRequest("pane-5", workspace.Id, "shell", "hold", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-5" && e.Type == "log"));
            await Task.Delay(1500);
            Check(!seen.Events.Any(e => e.SessionId == "pane-5" && e.Status == "stopped"), "a polled run outlives the heartbeat");
            await fixture.Host.StopAsync();
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-5" && e.Status == "error"), 20000);
            Check(client.List().Single().Status == RemoteConnectionStatus.Disconnected, "a host that stopped sharing ends the connection");
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task ARealCommandRunsInTheSharedFolderAndStops()
    {
        var plugin = Verification.Temp();
        var (fixture, client, workspace, seen, folder, _) = await Pair(runner: (host, sinks) =>
            new RunManager(host.Store.ResolveLocalAsync, new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null)), plugin, sinks.Event, sinks.Permission) { PowerShellCommands = true, GraphNodes = sinks.GraphNode, DelegationEnabled = () => false });
        try
        {
            fixture.Host.SetAllowsFullAccess(true);
            await client.StartAsync(new StartRunRequest("pane-6", workspace.Id, "shell", OperatingSystem.IsWindows() ? "(Get-Location).Path" : "pwd", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.SessionId == "pane-6" && e.Status is "completed" or "error"), 20000);
            Check(seen.Events.Any(e => e.SessionId == "pane-6" && e.Entry?.Text.Trim() == fixture.Shared.Path), "the command ran in the shared folder: " + string.Join(" | ", seen.Events.Where(e => e.Entry is not null).Select(e => e.Entry!.Text)));
            await client.StartAsync(new StartRunRequest("pane-7", workspace.Id, "shell", OperatingSystem.IsWindows() ? "Start-Sleep 30" : "sleep 30", []), workspace);
            await Task.Delay(500);
            await client.StopAsync("pane-7");
            Check(seen.Events.Last(e => e.SessionId == "pane-7").Status == "stopped", "a stop ends the pane at once");
            await Verification.Until(() => fixture.Host.Current().ActiveRuns == 0, 15000);
        }
        finally { await client.DisposeAsync(); await fixture.DisposeAsync(); Directory.Delete(folder, true); Directory.Delete(plugin, true); }
    }
}
```

Register:

```csharp
        await Test("remote loopback: commands and full access wait for the switch, then run on the host", RemoteLoopbackVerification.CommandsAndFullAccessWaitForTheSwitchThenRunOnTheHost);
        await Test("remote loopback: tool requests are answered from the client and graph runs go only where asked", RemoteLoopbackVerification.ToolRequestsAreAnsweredFromTheClientAndGraphRunsGoOnlyWhereAsked);
        await Test("remote loopback: unshared folders, busy hosts and oversize requests are refused", RemoteLoopbackVerification.UnsharedFoldersBusyHostsAndOversizeRequestsAreRefused);
        await Test("remote loopback: a run nobody polls is stopped and a stopped host ends the client's runs", RemoteLoopbackVerification.ARunNobodyPollsIsStoppedAndAStoppedHostEndsTheClientsRuns);
        await Test("remote loopback: a real command runs in the shared folder and stops", RemoteLoopbackVerification.ARealCommandRunsInTheSharedFolderAndStops);
```

- [ ] **Step 2: Run them to verify they fail**

Run: CORE-RUN.
Expected: `FAIL remote loopback: …` lines. The stub answers every run route with 404, so `StartAsync` throws `RemoteHttpFailure` 404.

- [ ] **Step 3: Implement**

Replace `native/windows/MightyClaude.Core/Remote/RemoteHostService.Runs.cs` with:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed partial class RemoteHostService
{
    private sealed class Job
    {
        public readonly List<(int Cursor, JsonObject Event, int Size)> Events = [];
        public int Cursor, Bytes;
        public DateTimeOffset LastPoll;
        public bool Done, Stopping, GraphRuns;
        public DateTimeOffset? FinishedAt;
    }
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);

    private static string ContentType(RemoteHttpRequest request) => (request.Headers.GetValueOrDefault("content-type") ?? "").Split(';')[0].Trim();
    private bool HasJob(string id) { lock (gate) return jobs.ContainsKey(id); }

    private async Task<RemoteHttpResponse> ServeRunsAsync(RemoteHttpRequest request, string[] route, string query, Guid revision)
    {
        if (request.Method == "POST" && route is ["runs"] && query.Length == 0) return await StartRunAsync(request, revision);
        if (route is not ["runs", var id, var action] || !Wire.Identifier(id) || !HasJob(id)) throw new HostFailure(404, Locale.Get("remote.host.error.runGone"));
        if (request.Method == "GET" && action == "events") return Events(id, query);
        if (request.Method != "POST" || query.Length != 0) throw new HostFailure(404, Locale.Get("remote.host.error.badPath"));
        if (action == "stop") { await StopJobAsync(id); return Reply(200, new RemoteWireAnswer(RemoteProtocol.Version, true)); }
        if (ContentType(request) != "application/json") throw new HostFailure(415, Locale.Get("remote.host.error.jsonOnly"));
        IRemoteRunner active;
        lock (gate) { if (runner is null || !jobs.TryGetValue(id, out var job) || job.Done) throw new HostFailure(409, Locale.Get("remote.host.error.runGone")); active = runner; }
        try
        {
            switch (action)
            {
                case "steer":
                    var steer = JsonSerializer.Deserialize<RemoteWireSteer>(request.Body, Wire.Json) ?? throw new JsonException();
                    if (string.IsNullOrWhiteSpace(steer.Text) || Encoding.UTF8.GetByteCount(steer.Text) > 400_000 || steer.Text.Contains('\0')) throw new HostFailure(400, Locale.Get("validation.run.badInput"));
                    return Reply(200, new RemoteWireAnswer(RemoteProtocol.Version, await active.TrySteerAsync(id, steer.Text)));
                case "permission":
                    var permission = JsonSerializer.Deserialize<RemoteWirePermission>(request.Body, Wire.Json) ?? throw new JsonException();
                    if (!Wire.Identifier(permission.RunId) || permission.RequestId is not { Length: > 0 and <= 256 }) throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
                    active.RespondToToolPermission(id, permission.RequestId, permission.Allow); break;
                case "questions":
                    var questions = JsonSerializer.Deserialize<RemoteWireQuestions>(request.Body, Wire.Json) ?? throw new JsonException();
                    if (questions.RequestId is not { Length: > 0 and <= 256 } || questions.Answers is null) throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
                    active.AnswerQuestionnaire(id, questions.RequestId, questions.Answers); break;
                case "plan":
                    var plan = JsonSerializer.Deserialize<RemoteWirePlan>(request.Body, Wire.Json) ?? throw new JsonException();
                    active.AnswerPlan(id, plan.RequestId, plan.Decision() ?? throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"))); break;
                default: throw new HostFailure(404, Locale.Get("remote.host.error.badPath"));
            }
        }
        catch (JsonException) { throw new HostFailure(400, Locale.Get("remote.host.error.badRequest")); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { throw new HostFailure(409, ex.Message); }
        return Reply(200, new RemoteWireAnswer(RemoteProtocol.Version, true));
    }

    private async Task<RemoteHttpResponse> StartRunAsync(RemoteHttpRequest request, Guid revision)
    {
        if (ContentType(request) != "application/json") throw new HostFailure(415, Locale.Get("remote.host.error.jsonOnly"));
        RemoteWireStart? wire;
        try { wire = JsonSerializer.Deserialize<RemoteWireStart>(request.Body, Wire.Json); } catch (JsonException) { wire = null; }
        if (wire?.Request is null || wire.RegisteredModels is null || wire.RegisteredModels.Any(m => m is null || !Wire.Model(m.Name)) || wire.PhaseModels is { } phase && !RemoteWorkspaceValidation.PhaseModels(phase))
            throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
        var id = Wire.Id();
        StartRunRequest run;
        try { run = wire.ToRequest(id, wire.Request.WorkspaceId).Validate(); } catch (ArgumentException ex) { throw new HostFailure(400, ex.Message); }
        // Only a shared workspace's own folder is ever the working folder; the client sends an id, never a path.
        var workspace = SharedWorkspaces().FirstOrDefault(w => w.Id == run.WorkspaceId) ?? throw new HostFailure(403, Locale.Get("remote.host.error.notShared"));
        Workspace folder;
        try { folder = await options.ResolveLocal(workspace.Id); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { throw new HostFailure(403, ex.Message); }
        if (folder.Remote is not null || folder.Path != workspace.Path) throw new HostFailure(403, Locale.Get("remote.host.error.notShared"));
        bool allowed; lock (gate) allowed = allowsFullAccess;
        if (FullAccessRefusal(run, allowed) is { } refusal) throw new HostFailure(403, refusal);
        // This PC's own CLI update, plugin change or model reload holds client runs too.
        if (options.Admission(run) is { } reason) throw new HostFailure(503, reason);
        if (run.Kind == "claude")
        {
            // What this PC cannot run is refused here, before anything starts.
            var runtime = await options.Runtime();
            if (runtime.Providers?.FirstOrDefault(p => p.Id == run.Provider) is not { Available: true } provider)
                throw new HostFailure(400, Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(run.Provider) }));
            if (RemoteWorkspaceValidation.CapabilityProblem(run, provider.Capabilities) is { } problem) throw new HostFailure(400, problem);
        }
        IRemoteRunner active;
        lock (gate)
        {
            if (revision != generation || runner is null) throw new HostFailure(503, Locale.Get("remote.host.error.stopped"));
            if (jobs.Values.Count(j => !j.Done) >= RemoteProtocol.MaximumRuns) throw new HostFailure(429, Locale.Get("remote.host.error.runLimit", new Dictionary<string, string> { ["count"] = RemoteProtocol.MaximumRuns.ToString() }));
            Prune();
            jobs[id] = new Job { LastPoll = options.Clock(), GraphRuns = request.Headers.GetValueOrDefault(RemoteProtocol.GraphHeader) == RemoteProtocol.GraphRuns };
            active = runner;
        }
        AnnounceRuns();
        _ = LaunchAsync(run, active, revision);
        return Reply(202, new RemoteWireJob(RemoteProtocol.Version, id));
    }

    private async Task LaunchAsync(StartRunRequest run, IRemoteRunner active, Guid revision)
    {
        lock (gate) if (revision != generation || !jobs.TryGetValue(run.SessionId, out var job) || job.Done || job.Stopping) return;
        try { await active.StartAsync(run); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            bool stopping;
            lock (gate) { if (revision != generation || !jobs.TryGetValue(run.SessionId, out var job) || job.Done) return; stopping = job.Stopping; }
            Receive(RunEvent.Log(run.SessionId, "error", ex.Message), revision);
            Receive(RunEvent.State(run.SessionId, stopping ? "stopped" : "error"), revision);
        }
    }

    private bool GraphRunsFor(string id) { lock (gate) return jobs.TryGetValue(id, out var job) && job.GraphRuns; }
    private void Receive(RunEvent value, Guid revision)
    {
        if (RemoteWireEvents.FromRunEvent(value, value.SessionId, GraphRunsFor(value.SessionId)) is { } wire)
            Append(value.SessionId, wire, revision, value.Type == "status" && value.Status is "completed" or "error" or "stopped");
    }
    private void ReceivePermission(ToolPermissionRequest value, Guid revision) => Append(value.RunId, RemoteWireEvents.FromPermission(value, value.RunId), revision, false);
    private void ReceiveGraph(string sessionId, ExecutionGraphNode node, Guid revision) { if (!GraphRunsFor(sessionId)) Append(sessionId, RemoteWireEvents.FromGraphNode(node, sessionId), revision, false); }

    private void Append(string jobId, JsonObject wire, Guid revision, bool ended)
    {
        lock (gate)
        {
            if (revision != generation || !jobs.TryGetValue(jobId, out var job) || job.Done) return;
            job.Cursor++;
            var size = JsonSerializer.SerializeToUtf8Bytes(new RemoteWireEvent(job.Cursor, wire), Wire.Json).Length;
            // One event too large for any poll is never sent; its cursor shows as a gap.
            if (size <= RemoteProtocol.MaximumPollBytes - 4096)
            {
                job.Events.Add((job.Cursor, wire, size)); job.Bytes += size;
                while (job.Events.Count > RemoteProtocol.MaximumBufferedEvents || job.Bytes > RemoteProtocol.MaximumBufferedBytes) { job.Bytes -= job.Events[0].Size; job.Events.RemoveAt(0); }
            }
            if (!ended) return;
            job.Done = true; job.FinishedAt = options.Clock();
        }
        AnnounceRuns();
    }

    private RemoteHttpResponse Events(string id, string query)
    {
        lock (gate)
        {
            var match = Regex.Match(query, @"^\?cursor=([0-9]{1,12})\z");
            if (!jobs.TryGetValue(id, out var job) || !match.Success || !long.TryParse(match.Groups[1].Value, out var requested) || requested > job.Cursor) throw new HostFailure(400, Locale.Get("remote.host.error.badCursor"));
            var cursor = (int)requested;
            job.LastPoll = options.Clock();
            var selected = new List<RemoteWireEvent>(); var bytes = 0;
            foreach (var item in job.Events.Where(e => e.Cursor > cursor))
            {
                if (selected.Count >= RemoteProtocol.MaximumPollEvents || bytes + item.Size > RemoteProtocol.MaximumPollBytes) break;
                selected.Add(new(item.Cursor, item.Event)); bytes += item.Size;
            }
            // A cursor skipped between the request's and what follows: the buffer dropped it.
            var expected = cursor + 1; var gap = false;
            foreach (var item in selected) { if (item.Cursor != expected) gap = true; expected = item.Cursor + 1; }
            if (selected.Count == 0 && job.Cursor > cursor) gap = true;
            var last = selected.Count > 0 ? selected[^1].Cursor : gap ? job.Cursor : cursor;
            return Reply(200, new RemoteWirePoll(RemoteProtocol.Version, last, job.Cursor, gap, job.Done && last >= job.Cursor, selected));
        }
    }

    private async Task StopJobAsync(string id)
    {
        IRemoteRunner? active; Guid revision;
        lock (gate) { if (!jobs.TryGetValue(id, out var job) || job.Done || job.Stopping) return; job.Stopping = true; active = runner; revision = generation; }
        try { if (active is not null) await active.StopAsync(id); } catch (TimeoutException) { }
        bool open; lock (gate) open = jobs.TryGetValue(id, out var job) && !job.Done;
        if (open) Receive(RunEvent.State(id, "stopped"), revision);
    }

    /// <summary>The heartbeat: a run whose client stopped polling is stopped.</summary>
    private async Task LeaseLoopAsync(Guid revision, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                string[] orphaned;
                lock (gate)
                {
                    if (revision != generation) return;
                    var now = options.Clock();
                    orphaned = jobs.Where(p => !p.Value.Done && !p.Value.Stopping && now - p.Value.LastPoll >= options.Heartbeat).Select(p => p.Key).ToArray();
                }
                foreach (var id in orphaned) await StopJobAsync(id);
                lock (gate) Prune();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Under the gate: finished runs older than two minutes go, and the oldest finished ones while there are 32 or more.</summary>
    private void Prune()
    {
        var now = options.Clock();
        foreach (var id in jobs.Where(p => p.Value.Done && now - (p.Value.FinishedAt ?? now) > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToList()) jobs.Remove(id);
        while (jobs.Count >= 2 * RemoteProtocol.MaximumRuns && jobs.Where(p => p.Value.Done).OrderBy(p => p.Value.FinishedAt).Select(p => p.Key).FirstOrDefault() is { } oldest) jobs.Remove(oldest);
    }

    private void AnnounceRuns() { int count; lock (gate) count = jobs.Values.Count(j => !j.Done); options.RunsChanged(count); }
}
```

Delete the `jobs`/`Job` stub lines Task 13 left in this file; this version replaces them.

- [ ] **Step 4: Run them to verify they pass**

Run: CORE-RUN.
Expected: all five `PASS remote loopback: …` lines, every earlier `remote` line, and 9 FAILs.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Remote/RemoteHostService.Runs.cs native/windows/MightyClaude.Core.Tests/RemoteLoopbackVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a sharing PC runs other computers' requests with its own CLIs in the shared folder, buffers their events for polling, sends Mac graph events or Windows graph runs as asked, takes answers and steering, holds runs while it updates, and stops any run its client stops polling for 30 s

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
## Stage E — Wiring the app, both apps naming the host OS, WinUI

### Task 15: `DesktopService` routes remote panes; the phone never sees them; what remote panes may not do

**Files:**
- Create: `native/windows/MightyClaude.Core/DesktopService.Remote.cs`
- Create: `native/windows/MightyClaude.Core/Remote/RemoteWorkspaceFeatures.cs`
- Modify: `native/windows/MightyClaude.Core/DesktopService.cs` (constructor field, `Launch`, `StopAsync`, `IsSessionRunning`, `TrySteerAsync`, `RespondToToolPermission`, `AnswerQuestionnaire`, `AnswerPlan`, `HasActiveProvider`, `Receive`, `DisposeAsync`)
- Modify: `native/windows/MightyClaude.Core/DesktopService.Delegation.cs` (`WorktreeHostFolder` ~line 324, `SecondSend` ~line 343)
- Modify: `native/windows/MightyClaude.Core/Remote/MobileDesktopRouter.cs` (`Observe` ~62–77, `Session` ~87, the workspace routes ~131–140, `State` ~266–270)
- Create: `native/windows/MightyClaude.Core.Tests/RemoteDesktopVerification.cs`
- Modify: `Verification.cs`

**Interfaces:**
- Consumes: everything in Tasks 3–14.
- Produces:
  - `DesktopService`:
    - `AttachRemote(string dataDirectory, IRemoteKeyVault keys, RemoteClientOptions? options = null) → RemoteClientService`
    - `Remote → RemoteClientService?`
    - `IsRemoteWorkspace(string?) → bool`
    - `IsRemoteSession(string) → bool`
    - `OpenRemoteWorkspaceAsync(string connectionId, string hostWorkspaceId) → Task<Workspace>`
    - `ResolveSharedAsync(string id) → Task<Workspace>`
    - `CreateRemoteRunner(RemoteRunnerSinks) → IRemoteRunner`
    - `AnswerPlan` now returns `PlanRecord?` (null for a remote pane, whose answer goes out asynchronously).
  - `RemoteWorkspaceFeatures`:
    - `AddPaneEntries(Workspace?) → IReadOnlyList<string>`
    - `AllowsLocalOnly(Workspace?) → bool`
    - `UsesLocalTerminal(RunSession, Workspace?) → bool`
    - `ProviderRuntime(string provider, RemoteWorkspaceReference, RemoteConnectionInfo?) → ProviderRuntime`
    - `BlockedReason(RunSession, RemoteWorkspaceReference, RemoteConnectionInfo?) → string?`
    - `FirewallPorts(int? phoneDirectPort, int? sharingPort) → IReadOnlyList<int>`
  - `RemoteSecretCopy`:
    - `Key` (not in clipboard history, not roamable, cleared after 60 s)
    - `ShouldClear(string? clipboardText, string copied) → bool`

- [ ] **Step 1: Write the failing tests**

`native/windows/MightyClaude.Core.Tests/RemoteDesktopVerification.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using MightyClaude.Core;

internal static class RemoteDesktopVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task ARemotePaneRunsThroughTheClientAndThePhoneNeverSeesIt()
    {
        await using var fixture = await RemoteHostVerification.MakeHost();
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0); fixture.Host.SetAllowsFullAccess(true);
        var directory = Verification.Temp(); var local = Verification.Temp();
        try
        {
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync();
            var client = service.AttachRemote(Path.Combine(directory, "state"), new MemoryRemoteKeyVault(), new() { AllowLoopbackForTests = true });
            var near = await service.AddWorkspaceAsync(local);
            var info = await client.ConnectAsync("", "127.0.0.1:" + state.Port, state.Key!);
            var far = await service.OpenRemoteWorkspaceAsync(info.Id, fixture.Shared.Id);
            Check(service.IsRemoteWorkspace(far.Id) && !service.IsRemoteWorkspace(near.Id) && far.Path == fixture.Shared.Path, "the host's workspace is listed as remote with the host's path");
            var permissions = new ConcurrentQueue<ToolPermissionRequest>(); service.ToolPermissionChanged += permissions.Enqueue;
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "cmd", WorkspaceId = far.Id, Kind = "shell" }, new RunSession { Id = "agent", WorkspaceId = far.Id }, new RunSession { Id = "mine", WorkspaceId = near.Id }] });

            await service.StartAsync(new StartRunRequest("cmd", far.Id, "shell", "dir", []));
            await Verification.Until(() => service.Snapshot.Sessions.Single(s => s.Id == "cmd").Status == "completed");
            Check(service.Snapshot.Sessions.Single(s => s.Id == "cmd").Logs.Any(l => l.Text == "echo:dir"), "the host's output is the pane's");

            await service.StartAsync(new StartRunRequest("agent", far.Id, "claude", "ask", []));
            await Verification.Until(() => permissions.Any(p => p.RunId == "agent" && p.State == "pending"));
            service.RespondToToolPermission("agent", "perm-1", true);
            await Verification.Until(() => fixture.Runner!.Answers.Any(a => a.Request == "perm-1" && a.Allow));
            await Verification.Until(() => service.Snapshot.Sessions.Single(s => s.Id == "agent").Status == "completed");

            await service.StartAsync(new StartRunRequest("agent", far.Id, "claude", "hold", []));
            await Verification.Until(() => service.IsSessionRunning("agent"));
            Check(!service.HasActiveProvider("claude"), "a remote run never holds this PC's CLI updates");
            Check(service.SecondSend(service.Snapshot.Sessions.Single(s => s.Id == "agent")) != ComposerSecondSend.Worktree, "a remote pane never makes a child worktree");
            await service.StopAsync("agent");
            await Verification.Until(() => service.Snapshot.Sessions.Single(s => s.Id == "agent").Status == "stopped");
            Check(fixture.Runner!.Stopped.Count == 1, "the stop reached the host");

            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([]));
            router.Observe();
            var scopes = router.TakeNotifications().Select(n => n.Scope).ToList();
            Check(scopes.Contains("session:mine") && !scopes.Contains("session:cmd") && !scopes.Contains("session:agent"), "the phone is told of this PC's panes only: " + string.Join(",", scopes));
            var reply = JsonSerializer.Serialize(await router.RouteAsync("GET", "/m1/state?since=0&wait=0", null, "device-1", CancellationToken.None), Wire.Json);
            Check(reply.Contains(near.Id, StringComparison.Ordinal) && !reply.Contains(far.Id, StringComparison.Ordinal) && !reply.Contains("\"cmd\"", StringComparison.Ordinal), "the phone's state leaves remote workspaces out: " + reply);
        }
        finally { Directory.Delete(directory, true); Directory.Delete(local, true); }
    }

    internal static Task RemotePanesKeepToTheHostsFeatures()
    {
        var local = new Workspace { Path = "C:\\work" };
        var remote = new Workspace { Path = "C:\\far", Remote = new("conn-1", "ws-1", "studio-pc") };
        Check(RemoteWorkspaceFeatures.AddPaneEntries(local).SequenceEqual(AddPaneMenu.Entries()), "a local workspace keeps its menu");
        var entries = RemoteWorkspaceFeatures.AddPaneEntries(remote);
        Check(entries.Contains(AddPaneMenu.Shell) && !entries.Contains(AddPaneMenu.Browser) && !entries.Contains(AddPaneMenu.Files) && Wire.Providers.All(p => entries.Contains(AddPaneMenu.AgentPrefix + p)), "a remote workspace offers agents and commands, never files or a browser");
        Check(RemoteWorkspaceFeatures.AllowsLocalOnly(local) && !RemoteWorkspaceFeatures.AllowsLocalOnly(remote), "local-only features stay local");
        Check(RemoteWorkspaceFeatures.UsesLocalTerminal(new RunSession { Kind = "shell" }, local) && !RemoteWorkspaceFeatures.UsesLocalTerminal(new RunSession { Kind = "shell" }, remote), "a remote command pane is never a local ConPTY terminal");
        var unknown = RemoteWorkspaceFeatures.ProviderRuntime("codex", remote.Remote!, null);
        Check(unknown.Available && unknown.Capabilities.PermissionModes.Contains("onRequest") && unknown.Detail.Contains("studio-pc", StringComparison.Ordinal), "before the host answered, nothing is blocked");
        Check(RemoteWorkspaceFeatures.BlockedReason(new RunSession(), remote.Remote!, null) == Locale.Get("remote.workspace.connectionGone", new Dictionary<string, string> { ["host"] = "studio-pc" }), "a removed connection blocks the pane");
        var info = RemoteWireFixtureVerification.Node("info.json").Deserialize<RemoteWireInfo>(Wire.Json)!;
        var connected = new RemoteConnectionInfo("conn-1", "studio", "100.101.102.103:43137", RemoteConnectionStatus.Connected, "studio-pc", info.Workspaces, info.Runtime);
        Check(RemoteWorkspaceFeatures.BlockedReason(new RunSession { Provider = "codex" }, remote.Remote!, connected) == Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name("codex") }), "the host's missing CLI blocks the pane");
        Check(RemoteWorkspaceFeatures.BlockedReason(new RunSession { Kind = "shell" }, remote.Remote!, connected) is null, "commands never wait for a CLI");
        Check(RemoteWorkspaceFeatures.FirewallPorts(43139, 43137).SequenceEqual([43137, 43139]) && RemoteWorkspaceFeatures.FirewallPorts(null, 43137).SequenceEqual([43137]) && RemoteWorkspaceFeatures.FirewallPorts(null, null).Count == 0 && RemoteWorkspaceFeatures.FirewallPorts(80, null).Count == 0, "the firewall rule opens the ports in use, nothing else");
        Check(RemoteSecretCopy.Key is { AllowedInHistory: false, Roamable: false } && RemoteSecretCopy.Key.ClearAfter == TimeSpan.FromSeconds(60), "the key stays out of clipboard history and other devices, and goes after a minute");
        Check(RemoteSecretCopy.ShouldClear("k", "k") && !RemoteSecretCopy.ShouldClear("other", "k") && !RemoteSecretCopy.ShouldClear(null, "k"), "only the key itself is cleared");
        return Task.CompletedTask;
    }
}
```

Register:

```csharp
        await Test("remote desktop: a remote pane runs through the client and the phone never sees it", RemoteDesktopVerification.ARemotePaneRunsThroughTheClientAndThePhoneNeverSeesIt);
        await Test("remote desktop: remote panes keep to the host's features", RemoteDesktopVerification.RemotePanesKeepToTheHostsFeatures);
```

- [ ] **Step 2: Run them to verify they fail**

Run: CORE-RUN.
Expected: the build fails with `CS1061: 'DesktopService' does not contain a definition for 'AttachRemote'` and `CS0103: 'RemoteWorkspaceFeatures'`.

- [ ] **Step 3: Write `RemoteWorkspaceFeatures`**

`native/windows/MightyClaude.Core/Remote/RemoteWorkspaceFeatures.cs`:

```csharp
namespace MightyClaude.Core;

/// <summary>
/// What a remote workspace's panes may and may not do (M/AppStore+RemoteWorkspaces.swift, docs/remote-workspaces.md): delegation,
/// the files pane, the browser pane, styles that read files, the local terminal, agent I/O tools, the status line and the git
/// badge all read this PC's disk, network or processes, so a remote workspace has none of them.
/// </summary>
public static class RemoteWorkspaceFeatures
{
    /// <summary>The add-pane menu: agents and a remote command for a remote workspace, never a browser or files pane.</summary>
    public static IReadOnlyList<string> AddPaneEntries(Workspace? workspace) => workspace?.Remote is null ? AddPaneMenu.Entries()
        : [.. Wire.Providers.Select(p => AddPaneMenu.AgentPrefix + p), AddPaneMenu.Separator, AddPaneMenu.Shell, AddPaneMenu.Separator, AddPaneMenu.OpenProject];

    /// <summary>Delegation, files, browser, file-reading styles, agent I/O, the status line and the git badge.</summary>
    public static bool AllowsLocalOnly(Workspace? workspace) => workspace?.Remote is null;

    /// <summary>A command pane is this PC's ConPTY terminal only in a local workspace; a remote one sends each command to the host.</summary>
    public static bool UsesLocalTerminal(RunSession pane, Workspace? workspace) => pane.Kind == "shell" && workspace?.Remote is null;

    /// <summary>The host's CLI as its last answer described it; before this launch heard from the host, a stand-in that blocks nothing.</summary>
    public static ProviderRuntime ProviderRuntime(string provider, RemoteWorkspaceReference reference, RemoteConnectionInfo? connection)
    {
        if (connection?.Runtime?.Providers?.FirstOrDefault(p => p.Id == provider) is { } known) return known;
        return new(provider, ProviderCatalog.Name(provider), true, null, Locale.Get("remote.workspace.runtimeUnknown", new Dictionary<string, string> { ["host"] = reference.HostName }),
            ProviderCatalog.Fallback(provider), ProviderCatalog.Capabilities(provider) with { PermissionModes = ProviderCatalog.PermissionModes(provider) });
    }

    /// <summary>Why a remote pane cannot send now: its connection was removed, or the host said its CLI is missing or lacks the mode.</summary>
    public static string? BlockedReason(RunSession pane, RemoteWorkspaceReference reference, RemoteConnectionInfo? connection)
    {
        if (connection is null) return Locale.Get("remote.workspace.connectionGone", new Dictionary<string, string> { ["host"] = reference.HostName });
        if (pane.Kind == "shell" || connection.Status != RemoteConnectionStatus.Connected || connection.Runtime?.Providers?.FirstOrDefault(p => p.Id == pane.Provider) is not { } provider) return null;
        if (!provider.Available) return Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(pane.Provider) });
        if (!provider.Capabilities.PermissionModes.Contains(pane.Settings.PermissionMode)) return Locale.Get("remote.workspace.modeUnsupported", new Dictionary<string, string> { ["host"] = reference.HostName });
        return null;
    }

    /// <summary>The Windows Firewall rule's ports (WindowsFirewallRule): the phone's direct port and the sharing port, whichever are on.</summary>
    public static IReadOnlyList<int> FirewallPorts(int? phoneDirectPort, int? sharingPort) =>
        new[] { phoneDirectPort, sharingPort }.OfType<int>().Where(p => p is >= 1024 and <= 65535).Distinct().Order().ToList();
}

/// <summary>How the connection key is copied (M/RemoteSettingsView copyButton secret:): never in clipboard history or the cloud clipboard, cleared after a minute if still there.</summary>
public sealed record RemoteSecretCopy(bool AllowedInHistory, bool Roamable, TimeSpan ClearAfter)
{
    public static readonly RemoteSecretCopy Key = new(false, false, TimeSpan.FromSeconds(60));
    public static bool ShouldClear(string? clipboardText, string copied) => clipboardText is not null && clipboardText == copied;
}
```

- [ ] **Step 4: Write `DesktopService.Remote.cs`**

```csharp
namespace MightyClaude.Core;

/// <summary>Remote workspaces in the desktop service (M/AppStore+RemoteWorkspaces.swift): a remote pane's runs go through the client, never RunManager.</summary>
public sealed partial class DesktopService
{
    private readonly RemotePendingPermissions remotePending = new();

    /// <summary>The client of "다른 컴퓨터 연결"; null until <see cref="AttachRemote"/>.</summary>
    public RemoteClientService? Remote { get; private set; }

    /// <summary>Creates the client once; its events and tool requests arrive as a local run's do.</summary>
    public RemoteClientService AttachRemote(string dataDirectory, IRemoteKeyVault keys, RemoteClientOptions? options = null) =>
        Remote ??= new RemoteClientService(store, dataDirectory, keys, Receive, ReceiveRemotePermission, options);

    public bool IsRemoteWorkspace(string? workspaceId) { lock (sync) return snapshot.Workspaces.Any(w => w.Id == workspaceId && w.Remote is not null); }
    public bool IsRemoteSession(string sessionId)
    {
        lock (sync) return snapshot.Sessions.FirstOrDefault(s => s.Id == sessionId) is { } pane && snapshot.Workspaces.Any(w => w.Id == pane.WorkspaceId && w.Remote is not null);
    }

    /// <summary>Lists a connected computer's shared workspace in the sidebar and makes it active.</summary>
    public async Task<Workspace> OpenRemoteWorkspaceAsync(string connectionId, string hostWorkspaceId)
    {
        var client = Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
        var workspace = client.OpenWorkspace(connectionId, hostWorkspaceId);
        await UpdateAsync(s => WorkspaceDisclosure.Open(s with { Workspaces = s.Workspaces.Any(w => w.Id == workspace.Id) ? s.Workspaces.Select(w => w.Id == workspace.Id ? workspace : w).ToList() : [.. s.Workspaces, workspace] }, workspace.Id) with { ActiveWorkspaceId = workspace.Id });
        return workspace;
    }

    /// <summary>A workspace a sharing host may serve: registered, local, its folder present.</summary>
    public Task<Workspace> ResolveSharedAsync(string id) => ResolveLocal(id);

    /// <summary>The runner a sharing host's client runs go through: this PC's CLIs, no pane MCP, no delegation, no picture cache.</summary>
    public IRemoteRunner CreateRemoteRunner(RemoteRunnerSinks sinks) =>
        new RunManager(ResolveLocal, Providers, pluginDirectory, sinks.Event, sinks.Permission) { PowerShellCommands = true, GraphNodes = sinks.GraphNode, DelegationEnabled = () => false };

    private void ReceiveRemotePermission(ToolPermissionRequest value)
    {
        // A host can send any number; a remote pane keeps the newest 32 waiting and withdraws older ones.
        foreach (var withdrawn in remotePending.Admit(value)) ToolPermissionChanged?.Invoke(withdrawn);
        ToolPermissionChanged?.Invoke(value);
    }

    private async Task StartRemoteAsync(StartRunRequest request, Workspace workspace)
    {
        var client = Remote ?? throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
        // The first send after launch connects (and reads the key from the vault) here.
        await client.EnsureConnectedAsync(workspace.Remote!.ConnectionId);
        await client.StartAsync(request, workspace);
    }

    /// <summary>An answer to a remote pane's request goes out without holding the caller (often the UI thread); a failure is said in the pane.</summary>
    private void AnswerRemote(string sessionId, Func<RemoteClientService, Task> answer)
    {
        if (Remote is not { } client) return;
        _ = Task.Run(async () =>
        {
            try { await answer(client); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Receive(RunEvent.Log(sessionId, "error", ex.Message)); }
        });
    }
}
```

- [ ] **Step 5: Route through it in `DesktopService.cs`**

Make these edits:
1. Add the field `private readonly string pluginDirectory;`, and assign `this.pluginDirectory = pluginDirectory;` in the constructor.
2. In `Launch`, change the first line to `var request = value.Validate(); Route route; string runId; Workspace workspace;`. Right after the registration check inside the first `lock (sync)`, add `workspace = snapshot.Workspaces.First(w => w.Id == request.WorkspaceId);`. Then replace `route.Started = true; started = reach ? local.StartReachedAsync(request) : local.StartAsync(request);` with:

```csharp
                route.Started = true;
                started = workspace.Remote is not null ? StartRemoteAsync(request, workspace) : reach ? local.StartReachedAsync(request) : local.StartAsync(request);
```

3. In `StopAsync`, change the last line to `return IsRemoteSession(id) && Remote is { } client ? client.StopAsync(id) : local.StopAsync(id);`.
4. Change `IsSessionRunning` to `public bool IsSessionRunning(string id) => local.IsRunning(id) || Remote?.IsRunning(id) == true;`.
5. Change `TrySteerAsync`'s first line to `if (!(IsRemoteSession(sessionId) ? Remote is { } client && await client.SteerAsync(sessionId, text) : await local.TrySteerAsync(sessionId, text))) return false;`.
6. Change the answer methods to:

```csharp
    public void RespondToToolPermission(string sessionId, string requestId, bool allow)
    {
        if (IsRemoteSession(sessionId)) { AnswerRemote(sessionId, client => client.RespondToPermissionAsync(sessionId, requestId, allow)); return; }
        local.RespondToToolPermission(sessionId, requestId, allow);
    }
    public void AnswerQuestionnaire(string sessionId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers)
    {
        if (IsRemoteSession(sessionId)) { AnswerRemote(sessionId, client => client.AnswerQuestionsAsync(sessionId, requestId, answers)); return; }
        local.AnswerQuestionnaire(sessionId, requestId, answers);
    }
    /// <summary>Answers one pending ExitPlanMode request; null for a remote pane, whose answer goes to the host and whose record comes back as an event.</summary>
    public PlanRecord? AnswerPlan(string sessionId, string requestId, PlanDecision decision)
    {
        if (IsRemoteSession(sessionId)) { AnswerRemote(sessionId, client => client.AnswerPlanAsync(sessionId, requestId, decision)); return null; }
        return local.AnswerPlan(sessionId, requestId, decision);
    }
```

7. In `HasActiveProvider`, count only local panes, because another computer's runs do not hold this PC's CLI updates:

```csharp
        lock (sync) return snapshot.Sessions.Any(s => s.Kind == "claude" && s.Provider == provider && !snapshot.Workspaces.Any(w => w.Id == s.WorkspaceId && w.Remote is not null) && (s.Status == "running" || routes.ContainsKey(s.Id)));
```

8. In `Receive`, after the terminal-status line, add `if (value.Type == "status" && value.Status is "stopped" or "completed" or "error") remotePending.Forget(value.SessionId);`.
9. In `DisposeAsync`, before `await local.DisposeAsync();`, add `if (Remote is { } client) await client.DisposeAsync();`.

`DesktopService.Delegation.cs`:
- `WorktreeHostFolder`: `… is { Remote: null } workspace ? DelegationPanes.RunWorkspace(session, workspace).Path : null;`
- `SecondSend`: in the `ComposerSecondSends.Choose(…)` call, the delegation-on argument becomes `delegation is not null && delegationEnabled() && !IsRemoteWorkspace(session.WorkspaceId)`.

`Remote/MobileDesktopRouter.cs`, in the four places:
- In `State()`, `workspaces = RemoteWorkspacePhone.Workspaces(snapshot.Workspaces).Select(w => new { w.Id, w.Name, w.Path })` and `sessions = RemoteWorkspacePhone.Sessions(snapshot.Sessions, snapshot.Workspaces).Where(s => s.Kind != "files").Select(…)`.
- In `Session(string id)`, look the pane up in `RemoteWorkspacePhone.Sessions(service.Snapshot.Sessions, service.Snapshot.Workspaces)`.
- In `Observe()`, read sessions and workspaces through the same two filters.
- In the `/m1/workspaces/{id}/…` lookup, use `RemoteWorkspacePhone.Workspaces(service.Snapshot.Workspaces)`.

- [ ] **Step 6: Run them to verify they pass**

Run: CORE-RUN.
Expected:
- `PASS remote desktop: …` twice.
- Every `Mobile host`, `Delegation…` and `remote …` check still passes.
- 9 FAILs.

`bash scripts/check-winui-compile.sh` must also stay clean, because `AnswerPlan` now returns `PlanRecord?`. WinUI callers that ignore the result compile unchanged. Any caller that uses it gets a null check.

- [ ] **Step 7: Commit**

```bash
git add native/windows/MightyClaude.Core/DesktopService.Remote.cs native/windows/MightyClaude.Core/Remote/RemoteWorkspaceFeatures.cs native/windows/MightyClaude.Core/DesktopService.cs native/windows/MightyClaude.Core/DesktopService.Delegation.cs native/windows/MightyClaude.Core/Remote/MobileDesktopRouter.cs native/windows/MightyClaude.Core.Tests/RemoteDesktopVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a remote workspace's panes send, stop, steer and answer through the client, keep the newest 32 waiting requests, never make child worktrees or hold local CLI updates, and the phone never sees them

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 16: Both apps name the host's OS (the Mac side)

**Files:**
- Modify: `native/macos/Sources/MightyCore/Remote/RemoteWorkspaceModels.swift` (add `RemoteHostOS`)
- Modify: `native/macos/Sources/MightyClaude/SessionInfoViews.swift:258` (an OS row under 실행 컴퓨터)
- Modify: `native/macos/Sources/MightyClaude/WorkspaceView.swift:610` (the status bar names the OS)
- Modify: `native/macos/Tests/MightyCoreTests/RemoteWireFixtureTests.swift` (one test)

**Interfaces:**
- Consumes: the locale keys `remote.hostOS.*`, `remote.hostWithOS` and `remote.sessionInfo.hostOS` (Task 2); `RemoteConnectionInfo.runtime?.platform`.
- Produces: `public enum RemoteHostOS { static func label(_ platform: String?) -> String; static func hostWithOS(_ host: String, platform: String?) -> String }`.

- [ ] **Step 1: Write the failing Mac test**

Append inside `struct RemoteWireFixtureTests`:

```swift
    @Test func theHostOSHasOneNamePerPlatform() throws {
        #expect(RemoteHostOS.label("darwin") == "macOS" && RemoteHostOS.label("win32") == "Windows" && RemoteHostOS.label("linux") == "Linux")
        #expect(RemoteHostOS.label(nil) == RemoteHostOS.label("plan9") && !RemoteHostOS.label(nil).isEmpty && RemoteHostOS.label(nil) != "remote.hostOS.unknown")
        #expect(RemoteHostOS.hostWithOS("studio-pc", platform: "win32") == "studio-pc · Windows")
        let info = try JSONDecoder().decode(RemoteWireInfo.self, from: Self.data("info.json"))
        #expect(RemoteHostOS.label(info.runtime.platform) == "Windows")
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `bash scripts/test-native-macos.sh --filter RemoteWireFixtureTests`
Expected: the build fails with "cannot find 'RemoteHostOS' in scope".

- [ ] **Step 3: Implement**

In `RemoteWorkspaceModels.swift`, after `RemotePendingPermissions`:

```swift
/// The host's OS from its `/info` `runtime.platform`, as both apps show it on
/// remote panes: a command pane runs zsh on a Mac host and PowerShell on a Windows one.
public enum RemoteHostOS {
    public static func label(_ platform: String?) -> String {
        switch platform {
        case "darwin": return L("remote.hostOS.darwin")
        case "win32": return L("remote.hostOS.win32")
        case "linux": return L("remote.hostOS.linux")
        default: return L("remote.hostOS.unknown")
        }
    }
    public static func hostWithOS(_ host: String, platform: String?) -> String { L("remote.hostWithOS", ["host": host, "os": label(platform)]) }
}
```

In `SessionInfoViews.swift` after the 실행 컴퓨터 row (line 258):

```swift
                        if let remote = workspace.remote { row(L("remote.sessionInfo.hostOS"), RemoteHostOS.label(store.remoteConnection(for: workspace)?.runtime?.platform), key: "hostOS") }
```

If that view has no `store`, add `@EnvironmentObject var store: AppStore` the way its siblings in the same file declare it.

In `WorkspaceView.swift`'s status bar (line 610), pass the host with its OS:

```swift
                Text(L(connected ? "remote.status.connected" : "remote.status.notConnected", ["host": RemoteHostOS.hostWithOS(remote.hostName, platform: store.remoteConnection(for: workspace)?.runtime?.platform)]))
```

- [ ] **Step 4: Run it to verify it passes**

```bash
bash scripts/test-native-macos.sh --filter 'RemoteWireFixtureTests|RemoteWorkspace[A-Za-z]*Tests'
swift build --package-path native/macos --product MightyClaude
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)"
```

Expected: the tests PASS, the app target builds, and check-locales is clean (the three shared keys now have a Mac caller too).

- [ ] **Step 5: Commit**

```bash
git add native/macos/Sources/MightyCore/Remote/RemoteWorkspaceModels.swift native/macos/Sources/MightyClaude/SessionInfoViews.swift native/macos/Sources/MightyClaude/WorkspaceView.swift native/macos/Tests/MightyCoreTests/RemoteWireFixtureTests.swift
git commit -m "(mac) a remote pane names its host's OS, macOS or Windows, in the status bar and the session info

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 17: WinUI Settings → 원격 연결 (Tailscale)

**Files:**
- Modify: `native/windows/MightyClaude.Core/SettingsSections.cs` (slot `RemoteWorkspaces`, after `MobileRemote` in `MacOrder`)
- Modify: `native/windows/MightyClaude.Core/SettingsNavigation.cs` (category `remote` between `mobile` and `companion`)
- Modify: `native/windows/MightyClaude.Core/StateStore.cs` (`Normalize` keeps `SettingsPane = "remote"`)
- Modify: `native/windows/MightyClaude.Core.Tests/SettingsSectionsVerification.cs` (expected order)
- Create: `native/windows/MightyClaude.WinUI/MainWindow.RemoteWorkspaces.cs`
- Modify:
  - `native/windows/MightyClaude.WinUI/MainWindow.Settings.cs` (`BuilderFor`, `SettingsGroups`)
  - `MainWindow.SettingsNavigation.cs` (`SettingsCategoryGlyph`)
  - `MainWindow.cs` (initialize and shut down)
  - `MainWindow.UpdateAdmission.cs:31` and `MainWindow.AutomaticUpdates.cs` (host runs hold updates)

**Interfaces:**
- Consumes:
  - `DesktopService.AttachRemote`, `.Remote`, `.ResolveSharedAsync`, `.CreateRemoteRunner`, `.OpenRemoteWorkspaceAsync` (Task 15)
  - `RemoteHostService` (Tasks 13–14)
  - `RemoteSecretCopy`, `RemoteWorkspaceFeatures.FirewallPorts` (Task 15)
  - `WindowsFirewallRuleRunner.ApplyAsync` / `RemoveAsync` (phone-direct, Task 8)
  - `RemoteWindowsStrings` (Task 2)
  - the Settings helpers `SettingsText`, `SettingsTitled`, `SettingsLabeled`, `SettingsRow`, `ReplaceSettingsRows`, `SettingsSwitch`, `SettingsField`, `SettingsPush`, `SafeButton`, `SettingsGlyphLabel`
- Produces:
  - `SettingsSections.RemoteWorkspaces = "remoteWorkspaces"`
  - `MainWindow.InitializeRemoteWorkspaces()`, `ShutdownRemoteWorkspacesAsync()`, `BuildRemoteWorkspacesSection()`, `CopyRemoteKeyAsync(string)`, `RemoteHostAdmission(StartRunRequest)`, `remoteHost`, `remoteHostRuns`

- [ ] **Step 1: Write the failing Core check**

In `SettingsSectionsVerification.SectionsAppearInMacOrderWithTheirTitles`, insert `Locale.Get("settings.remote.navTitle"),` right after `Locale.Get("settings.mobileRemote.sectionTitle"),` in the expected titles. Then append this method:

```csharp
    internal static Task TheRemotePageSitsBetweenMobileAndCompanion()
    {
        var ids = SettingsNavigation.Available.Select(c => c.Id).ToList();
        Check(ids.IndexOf("remote") == ids.IndexOf("mobile") + 1 && ids.IndexOf("companion") == ids.IndexOf("remote") + 1, "the Mac's pane order: mobile, remote, companion: " + string.Join(",", ids));
        Check(SettingsNavigation.Available.Single(c => c.Id == "remote").Sections.SequenceEqual([SettingsSections.RemoteWorkspaces]), "the remote page holds the remote section");
        Check(StateStore.Normalize(new AppSnapshot { SettingsPane = "remote" }, false).SettingsPane == "remote", "the remote page is remembered");
        return Task.CompletedTask;
    }
```

Register it next to the other `SettingsSectionsVerification` registrations:

```csharp
        await Test("remote settings: the remote page sits between mobile and companion", SettingsSectionsVerification.TheRemotePageSitsBetweenMobileAndCompanion);
```

- [ ] **Step 2: Run it to verify it fails**

Run: CORE-RUN.
Expected: the build fails with `CS0117: 'SettingsSections' does not contain a definition for 'RemoteWorkspaces'`.

- [ ] **Step 3: Register the page in Core**

`SettingsSections.cs`:
- Add `public const string RemoteWorkspaces = "remoteWorkspaces";`.
- In `MacOrder`, after the `MobileRemote` line, add `new(RemoteWorkspaces, Locale.Get("settings.remote.navTitle")),`.

`SettingsNavigation.cs`: after the `mobile` category, add `new("remote", Locale.Get("settings.remote.navTitle"), [SettingsSections.RemoteWorkspaces]),`.

`StateStore.Normalize`: add `or "remote"` to the `SettingsPane` list.

- [ ] **Step 4: Write the page and the wiring**

`native/windows/MightyClaude.WinUI/MainWindow.RemoteWorkspaces.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MightyClaude.Core;
using Windows.ApplicationModel.DataTransfer;

namespace MightyClaude.WinUI;

/// <summary>Settings → 원격 연결 (Tailscale) on Windows (M/RemoteSettingsView.swift), and the host and client behind it (docs/remote-workspaces.md).</summary>
public sealed partial class MainWindow
{
    private RemoteHostService? remoteHost;
    private int remoteHostRuns;
    private bool remoteBusy;
    private string? remoteError;
    private readonly List<Action> remoteViews = [];

    /// <summary>Builds the client and the host once the service is up. Only a smoke run passes loopback in place of Tailscale.</summary>
    private void InitializeRemoteWorkspaces(bool loopback = false)
    {
        if (remoteHost is not null) return;
        var directory = StateDirectory;
        IRemoteKeyVault keys = loopback ? new MemoryRemoteKeyVault() : new DpapiRemoteKeyVault(directory);
        var client = service.AttachRemote(directory, keys, new RemoteClientOptions { AllowLoopbackForTests = loopback });
        client.Changed += () => DispatcherQueue.TryEnqueue(() => { if (!closing) { RenderSidebar(); RefreshRemoteViews(); } });
        remoteHost = new RemoteHostService(new RemoteHostOptions
        {
            Workspaces = () => service.Snapshot.Workspaces,
            ResolveLocal = service.ResolveSharedAsync,
            Runtime = async () => (await service.Providers.GetRuntimeAsync()) with { AppVersion = AppVersion.Normalized(AppVersionText) ?? "0.1.0" },
            CreateRunner = service.CreateRemoteRunner,
            HostName = Environment.MachineName,
            Admission = RemoteHostAdmission,
            RunsChanged = count => DispatcherQueue.TryEnqueue(() => { remoteHostRuns = count; if (!closing) { RefreshRemoteViews(); NotifyAutomaticUpdates(); } }),
            AllowLoopbackForTests = loopback,
        });
    }

    private async Task ShutdownRemoteWorkspacesAsync() { if (remoteHost is { } host) await host.DisposeAsync(); }

    /// <summary>Why this PC cannot take another computer's run now (M/AppStore+RemoteWorkspaces.remoteHostAdmission); null when it can.</summary>
    private string? RemoteHostAdmission(StartRunRequest request)
    {
        if (closing) return Locale.Get("common.appClosing");
        if (request.Kind == "shell") return null;
        if (AnyCliUpdateRunning || manuallyUpdatingProvider is not null) return Locale.Get("run.blocked.updatingCLI", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(request.Provider) });
        if (pluginOperations.IsRunning && request.Provider is "claude" or "codex") return Locale.Get("run.blocked.changingPlugins");
        return null;
    }

    private void RefreshRemoteViews() { foreach (var refresh in remoteViews.ToList()) refresh(); }

    /// <summary>One remote action at a time; its error shows on the page, and the page reads both sides again after it.</summary>
    private async Task RemoteAction(Func<Task> operation)
    {
        if (remoteBusy) return;
        remoteBusy = true; remoteError = null; RefreshRemoteViews();
        try { await operation(); } catch (Exception ex) when (ex is not OutOfMemoryException) { remoteError = ex.Message; }
        finally { remoteBusy = false; if (!closing) { RefreshRemoteViews(); RenderSidebar(); } }
    }

    /// <summary>
    /// The key copy: never in clipboard history or the cloud clipboard, and cleared after a minute if the clipboard still holds it.
    /// The smoke run never calls it (it must not touch the real clipboard).
    /// </summary>
    private async Task CopyRemoteKeyAsync(string key)
    {
        var package = new DataPackage(); package.SetText(key);
        Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = RemoteSecretCopy.Key.AllowedInHistory, IsRoamable = RemoteSecretCopy.Key.Roamable });
        await Task.Delay(RemoteSecretCopy.Key.ClearAfter);
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text) && RemoteSecretCopy.ShouldClear(await content.GetTextAsync(), key)) Clipboard.Clear();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
    }

    private StackPanel BuildRemoteWorkspacesSection()
    {
        var rows = new StackPanel(); const string dynamicRow = "remote-dynamic";
        SettingsRow(rows, SettingsText(Locale.Get("settings.remote.intro"), 11, DesignToken.Ink2));
        var selected = new HashSet<string>(StringComparer.Ordinal); var showsKey = false;
        var port = SettingsField(new TextBox { Text = RemoteProtocol.DefaultPort.ToString(), MaxLength = 5 });
        AutomationProperties.SetName(port, Locale.Get("settings.remote.portLabel")); AutomationProperties.SetAutomationId(port, "settings-remote-port");
        var address = SettingsField(new TextBox { PlaceholderText = Locale.Get("settings.remote.addressPlaceholder"), MaxLength = 300 });
        AutomationProperties.SetName(address, Locale.Get("settings.remote.addressField")); AutomationProperties.SetAutomationId(address, "settings-remote-address");
        var key = new PasswordBox { PlaceholderText = Locale.Get("settings.remote.keyPlaceholder"), MaxLength = 128 };
        AutomationProperties.SetName(key, Locale.Get("settings.remote.keyField")); AutomationProperties.SetAutomationId(key, "settings-remote-key");
        var name = SettingsField(new TextBox { PlaceholderText = Locale.Get("settings.remote.namePlaceholder"), MaxLength = 120 });
        AutomationProperties.SetName(name, Locale.Get("settings.remote.nameField")); AutomationProperties.SetAutomationId(name, "settings-remote-name");

        void Refresh()
        {
            var host = remoteHost?.Current() ?? new RemoteHostState();
            IReadOnlyList<string> tailscale = options.SmokeTest ? ["127.0.0.1"] : TailscaleAddresses.Current();
            var content = new List<FrameworkElement>();
            // 이 컴퓨터 공유
            var share = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            share.Children.Add(SettingsText(Locale.Get("settings.remote.shareTitle"), 13, medium: true));
            var status = SettingsText(host.Enabled ? Locale.Get("settings.remote.sharingOn", new Dictionary<string, string> { ["count"] = host.WorkspaceIds.Count.ToString() }) : tailscale.Count == 0 ? RemoteWindowsStrings.SettingsTailscaleOff : Locale.Get("settings.remote.sharingOff"), 11, DesignToken.Ink2);
            AutomationProperties.SetAutomationId(status, "settings-remote-share-status"); share.Children.Add(status);
            if (remoteHostRuns > 0) share.Children.Add(SettingsText(Locale.Get("settings.remote.activeRuns", new Dictionary<string, string> { ["count"] = remoteHostRuns.ToString() }), 11, DesignToken.Ink2));
            if (host.Enabled && host.Key is { } current)
            {
                foreach (var line in host.Addresses)
                    share.Children.Add(SettingsLabeled(SettingsText(Locale.Get("settings.remote.addressLabel"), 11), SettingsText(line, 11, mono: true, selectable: true)));
                var keyText = SettingsText(showsKey ? current : new string('•', 16), 11, mono: true, selectable: showsKey);
                AutomationProperties.SetAutomationId(keyText, "settings-remote-key-text");
                var toggle = SettingsPush(Button(Locale.Get(showsKey ? "settings.remote.hideKeyButton" : "settings.remote.showKeyButton"), () => { showsKey = !showsKey; Refresh(); return Task.CompletedTask; }), SettingsControlSize.Small);
                var copy = SettingsPush(Button(Locale.Get("settings.remote.copyButton"), () => { if (!options.SmokeTest) { _ = CopyRemoteKeyAsync(current); remoteError = null; } return Task.CompletedTask; }), SettingsControlSize.Small);
                AutomationProperties.SetAutomationId(copy, "settings-remote-copy-key"); ToolTipService.SetToolTip(copy, RemoteWindowsStrings.KeyCopied);
                var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm }; keyRow.Children.Add(keyText); keyRow.Children.Add(toggle); keyRow.Children.Add(copy);
                share.Children.Add(SettingsLabeled(SettingsText(Locale.Get("settings.remote.keyLabel"), 11), keyRow));
                var full = SettingsSwitch(Locale.Get("settings.remote.allowFullAccess"), host.AllowsFullAccess, "settings-remote-full-access");
                full.Click += (_, _) => { remoteHost?.SetAllowsFullAccess(full.IsChecked == true); Refresh(); };
                share.Children.Add(SettingsLabeled(SettingsText(Locale.Get("settings.remote.allowFullAccess"), 12), full));
                share.Children.Add(SettingsText(RemoteWindowsStrings.SettingsAllowFullAccessDescription, 11, DesignToken.Ink2));
                share.Children.Add(SettingsText(Locale.Get("settings.remote.sharedListTitle"), 11, DesignToken.Ink2, medium: true));
                foreach (var shared in service.Snapshot.Workspaces.Where(w => host.WorkspaceIds.Contains(w.Id))) share.Children.Add(SettingsText(shared.Name + "  " + shared.Path, 11));
                var stopSharing = SettingsPush(SafeButton(Locale.Get("settings.remote.stopSharing"), () => RemoteAction(async () => { showsKey = false; if (remoteHost is { } h) await h.StopAsync(); })), SettingsControlSize.Small, destructive: true);
                AutomationProperties.SetAutomationId(stopSharing, "settings-remote-stop-sharing"); share.Children.Add(stopSharing);
                share.Children.Add(SettingsText(Locale.Get("settings.remote.stopSharingNote"), 11, DesignToken.Ink2));
            }
            else
            {
                var locals = service.Snapshot.Workspaces.Where(w => w.Remote is null).ToList();
                if (locals.Count == 0) share.Children.Add(SettingsText(Locale.Get("settings.remote.noLocalWorkspaces"), 11, DesignToken.Ink2));
                else
                {
                    if (selected.Count == 0) selected.Add(locals[0].Id);
                    share.Children.Add(SettingsText(Locale.Get("settings.remote.pickWorkspaces"), 11, DesignToken.Ink2, medium: true));
                    foreach (var local in locals)
                    {
                        var pick = new CheckBox { Content = local.Name, IsChecked = selected.Contains(local.Id) };
                        AutomationProperties.SetAutomationId(pick, "settings-remote-share-" + local.Id); ToolTipService.SetToolTip(pick, local.Path);
                        pick.Click += (_, _) => { if (pick.IsChecked == true) selected.Add(local.Id); else selected.Remove(local.Id); };
                        share.Children.Add(pick);
                    }
                }
                share.Children.Add(SettingsLabeled(SettingsText(Locale.Get("settings.remote.portLabel"), 12), port));
                var start = SettingsPush(SafeButton(Locale.Get("settings.remote.startSharing"), () => RemoteAction(async () =>
                {
                    if (remoteHost is not { } h || !int.TryParse(port.Text, out var number)) throw new ArgumentException(Locale.Get("remote.connect.error.badPort"));
                    await h.StartAsync([.. selected], options.SmokeTest ? 0 : number);
                })), SettingsControlSize.Small, prominent: true);
                start.IsEnabled = !remoteBusy && locals.Count > 0 && tailscale.Count > 0;
                AutomationProperties.SetAutomationId(start, "settings-remote-start-sharing"); share.Children.Add(start);
                share.Children.Add(SettingsText(RemoteWindowsStrings.SettingsShareNote, 11, DesignToken.Ink2));
            }
            // The Windows Firewall rule (WindowsFirewallRule from the phone-direct plan): one UAC prompt, never in a smoke run.
            share.Children.Add(SettingsText(RemoteWindowsStrings.SettingsFirewallNote, 11, DesignToken.Ink2));
            var firewall = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            var ports = RemoteWorkspaceFeatures.FirewallPorts(service.Snapshot.MobileRemote.Enabled ? MobileDirectPortOrNull() : null, host.Enabled ? host.Port : null);
            var add = SettingsPush(SafeButton(RemoteWindowsStrings.FirewallAdd, () => RemoteAction(async () => { if (options.SmokeTest) return; var result = await WindowsFirewallRuleRunner.ForThisApp().ApplyAsync(ports, CancellationToken.None); if (result.Kind != FirewallChange.Done) throw new InvalidOperationException(RemoteWindowsStrings.FirewallFailed(result.Detail ?? result.Kind.ToString())); })), SettingsControlSize.Small);
            var remove = SettingsPush(SafeButton(RemoteWindowsStrings.FirewallRemove, () => RemoteAction(async () => { if (options.SmokeTest) return; var result = await WindowsFirewallRuleRunner.ForThisApp().RemoveAsync(CancellationToken.None); if (result.Kind != FirewallChange.Done) throw new InvalidOperationException(RemoteWindowsStrings.FirewallFailed(result.Detail ?? result.Kind.ToString())); })), SettingsControlSize.Small, destructive: true);
            add.IsEnabled = ports.Count > 0 && !remoteBusy; AutomationProperties.SetAutomationId(add, "settings-remote-firewall-add"); AutomationProperties.SetAutomationId(remove, "settings-remote-firewall-remove");
            firewall.Children.Add(add); firewall.Children.Add(remove); share.Children.Add(firewall);
            content.Add(share);

            // 다른 컴퓨터 연결
            var connect = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            connect.Children.Add(SettingsText(Locale.Get("settings.remote.connectTitle"), 13, medium: true));
            connect.Children.Add(SettingsLabeled(SettingsText(Locale.Get("settings.remote.addressField"), 12), address));
            connect.Children.Add(SettingsLabeled(SettingsText(Locale.Get("settings.remote.keyField"), 12), key));
            connect.Children.Add(SettingsLabeled(SettingsText(Locale.Get("settings.remote.nameField"), 12), name));
            var go = SettingsPush(SafeButton(Locale.Get("settings.remote.connectButton"), () => RemoteAction(async () => { var typed = key.Password; key.Password = ""; await service.Remote!.ConnectAsync(name.Text, address.Text, typed); })), SettingsControlSize.Small, prominent: true);
            go.IsEnabled = !remoteBusy; AutomationProperties.SetAutomationId(go, "settings-remote-connect"); connect.Children.Add(go);
            connect.Children.Add(SettingsText(Locale.Get("settings.remote.connectNote"), 11, DesignToken.Ink2));
            content.Add(connect);
            if (remoteError is { } failure) { var error = SettingsText(failure, 11, DesignToken.ErrorText); AutomationProperties.SetAutomationId(error, "settings-remote-error"); content.Add(error); }

            // 연결한 컴퓨터
            var connections = service.Remote?.List() ?? [];
            if (connections.Count > 0)
            {
                var list = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
                list.Children.Add(SettingsText(Locale.Get("settings.remote.connectionsTitle"), 13, medium: true));
                foreach (var connection in connections) list.Children.Add(ConnectionRow(connection));
                content.Add(list);
            }
            // 원격 워크스페이스에서 되는 것과 안 되는 것
            var scope = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
            scope.Children.Add(SettingsText(Locale.Get("settings.remote.scopeTitle"), 13, medium: true));
            foreach (var line in new[] { Locale.Get("settings.remote.scope.runs"), Locale.Get("settings.remote.scope.notMirrored"), Locale.Get("settings.remote.scope.outOfScope"), RemoteWindowsStrings.SettingsScopeKey })
                scope.Children.Add(SettingsText(line, 11, DesignToken.Ink2));
            AutomationProperties.SetAutomationId(scope, "settings-remote-scope");
            content.Add(scope);
            ReplaceSettingsRows(rows, dynamicRow, content);
        }

        FrameworkElement ConnectionRow(RemoteConnectionInfo connection)
        {
            var row = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            head.Children.Add(SettingsText(connection.Name, 12, medium: true));
            head.Children.Add(SettingsText(connection.Address, 11, DesignToken.Ink2, mono: true));
            if (connection.Runtime?.Platform is { } platform) head.Children.Add(SettingsText(RemoteWindowsStrings.HostOS(platform), 11, DesignToken.Ink2));
            var refresh = SettingsPush(SafeButton(Locale.Get("settings.remote.refreshButton"), () => RemoteAction(() => service.Remote!.RefreshAsync(connection.Id))), SettingsControlSize.Small);
            refresh.IsEnabled = !remoteBusy; head.Children.Add(refresh);
            if (connection.Status == RemoteConnectionStatus.Connected)
            {
                var disconnect = SettingsPush(SafeButton(Locale.Get("settings.remote.disconnectButton"), () => RemoteAction(() => service.Remote!.DisconnectAsync(connection.Id))), SettingsControlSize.Small);
                disconnect.IsEnabled = !remoteBusy; head.Children.Add(disconnect);
            }
            var remove = SettingsPush(SafeButton(Locale.Get("settings.remote.removeHelp"), () => RemoteAction(() => service.Remote!.RemoveAsync(connection.Id))), SettingsControlSize.Small, destructive: true);
            remove.IsEnabled = !remoteBusy; AutomationProperties.SetName(remove, Locale.Get("settings.remote.removeHelp")); head.Children.Add(remove);
            row.Children.Add(head);
            if (connection.Detail is { } detail) row.Children.Add(SettingsText(detail, 11, DesignToken.Ink2));
            if (connection.Status == RemoteConnectionStatus.Connected)
            {
                if (connection.Shared.Count == 0) row.Children.Add(SettingsText(Locale.Get("settings.remote.noShared"), 11, DesignToken.Ink2));
                foreach (var shared in connection.Shared)
                {
                    var opened = service.Snapshot.Workspaces.Any(w => w.Remote?.ConnectionId == connection.Id && w.Remote.WorkspaceId == shared.Id);
                    var open = SettingsPush(SafeButton(Locale.Get(opened ? "settings.remote.openedButton" : "settings.remote.openButton"), () => RemoteAction(async () =>
                    {
                        var workspace = await service.OpenRemoteWorkspaceAsync(connection.Id, shared.Id);
                        settingsWindow?.Close();
                        if (!service.Snapshot.Sessions.Any(s => s.WorkspaceId == workspace.Id)) await AddPane("claude");
                    })), SettingsControlSize.Small);
                    AutomationProperties.SetAutomationId(open, "settings-remote-open-" + shared.Id);
                    row.Children.Add(SettingsLabeled(SettingsText(shared.Name + "  " + shared.Path, 11), open));
                }
            }
            return row;
        }

        rows.Loaded += (_, _) => { if (!remoteViews.Contains(Refresh)) remoteViews.Add(Refresh); Refresh(); };
        rows.Unloaded += (_, _) => { if (rows.IsLoaded) return; remoteViews.Remove(Refresh); showsKey = false; key.Password = ""; };
        return rows;
    }
}
```

Three of these names need checking against the code as it is:
- `MobileDirectPortOrNull()` is the phone-direct plan's accessor for its port while direct is on. Use the name it gave. If it gave none, add `private int? MobileDirectPortOrNull() => service.Snapshot.MobileRemote.DirectEnabled ? service.Snapshot.MobileRemote.DirectPort : null;` with that plan's field names.
- `DesignToken.ErrorText` is the error ink the other settings sections use for an error line. Use whichever token `BuildMobileRemoteSection` uses for its error.
- `settingsWindow?.Close()` closes the Settings window, as the Mac does with `showSettings = false`.

`MainWindow.Settings.cs`:
- In `BuilderFor`, add `SettingsSections.RemoteWorkspaces => BuildRemoteWorkspacesSection,`.
- In `SettingsGroups`, add `"remote" => [Slot(SettingsSections.RemoteWorkspaces)],`.

`MainWindow.SettingsNavigation.cs`: in `SettingsCategoryGlyph`, add `"remote" => "\uE968",` (Segoe Fluent "Network").

`MainWindow.cs`:
- In the non-smoke initialization, after `InitializeAgentIO();`, add `InitializeRemoteWorkspaces();`.
- In the `AppWindow.Closing` chain, before `await service.DisposeAsync();`, add `await ShutdownRemoteWorkspacesAsync();`.

`MainWindow.UpdateAdmission.cs:31`: add `|| remoteHostRuns > 0` to the busy condition.
`MainWindow.AutomaticUpdates.cs:43`: add `|| remoteHostRuns > 0` to the early-return condition. While another computer's runs go on here, this PC's CLIs are in use.

- [ ] **Step 5: Run the checks to verify they pass**

```bash
cd "$(git rev-parse --show-toplevel)"
bash scripts/check-winui-compile.sh
node scripts/check-design-tokens.js --check
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)"
```

Then run CORE-RUN.

Expected:
- WinUI shows only the expected `InitializeComponent` line.
- The design-token, font, colour and spacing ratchets are unchanged.
- check-locales finds no Korean literal and no unused key.
- CORE-RUN: `PASS remote settings: the remote page sits between mobile and companion`, the updated `SectionsAppearInMacOrderWithTheirTitles` passes, and 9 FAILs.

If the spacing ratchet counts a literal, replace it with a `DesignMetrics.Spacing` value.

- [ ] **Step 6: Commit**

```bash
git add native/windows/MightyClaude.Core/SettingsSections.cs native/windows/MightyClaude.Core/SettingsNavigation.cs native/windows/MightyClaude.Core/StateStore.cs native/windows/MightyClaude.Core.Tests/SettingsSectionsVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs native/windows/MightyClaude.WinUI/MainWindow.RemoteWorkspaces.cs native/windows/MightyClaude.WinUI/MainWindow.Settings.cs native/windows/MightyClaude.WinUI/MainWindow.SettingsNavigation.cs native/windows/MightyClaude.WinUI/MainWindow.cs native/windows/MightyClaude.WinUI/MainWindow.UpdateAdmission.cs native/windows/MightyClaude.WinUI/MainWindow.AutomaticUpdates.cs
git commit -m "(windows) Settings → 원격 연결 (Tailscale) shares this PC's workspaces (address, a key copied out of clipboard history, picks, the full-access switch, the firewall rule) and connects to other computers; their runs here hold CLI updates

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---
### Task 18: WinUI sidebar badge, remote agent and command panes, local-only features off

**Files:**
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.Sidebar.cs:270-272` (glyph and the host line)
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.Shell.cs` (`RefreshStatusBar` ~263, `RefreshWorkspaceHeader` ~279: the host badge, files button off)
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.cs` (`PaneView.Capabilities` ~366, the detail line ~853, a `PaneRuntime` helper)
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.QueuedComposer.cs:266,273` (the host's runtime decides the block)
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.Terminal.cs:144` (no ConPTY for a remote command pane)
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.Files.cs:65`, `MainWindow.Browser.cs:50`, `MainWindow.Styles.cs:237`, `MainWindow.AgentIO.cs:21`
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.DashboardWorkspaces.cs:175-189` and `MainWindow.Navigation.cs:207-226` (the remote menu)

**Interfaces:**
- Consumes:
  - `RemoteWorkspaceFeatures.AddPaneEntries`, `.AllowsLocalOnly`, `.UsesLocalTerminal`, `.ProviderRuntime`, `.BlockedReason` (Task 15)
  - `RemoteWindowsStrings.HostWithOS`, `.PaneUnavailable`, `.CommandNote` (Task 2)
  - `service.Remote.Connection(id)`
- Produces:
  - `PaneView.PaneRuntime(string provider) → ProviderRuntime?`
  - `MainWindow.RemoteHostLabel(Workspace) → string?` (host · OS, or null for a local workspace)
  - `MainWindow.OpenRemoteSettings()`

- [ ] **Step 1: The failing check is the smoke (Task 19); here the gate is the compile.** First add the two helpers that the smoke and every gate use, to `MainWindow.RemoteWorkspaces.cs`:

```csharp
    /// <summary>"host · OS" for a remote workspace (the OS from its connection's last answer), null for a local one.</summary>
    internal string? RemoteHostLabel(Workspace workspace) => workspace.Remote is { } remote
        ? RemoteWindowsStrings.HostWithOS(remote.HostName, service.Remote?.Connection(remote.ConnectionId)?.Runtime?.Platform) : null;

    internal bool RemoteConnected(Workspace workspace) => workspace.Remote is { } remote && service.Remote?.Connection(remote.ConnectionId)?.Status == RemoteConnectionStatus.Connected;

    /// <summary>Opens Settings on its 원격 연결 page (the sidebar header button, the add-pane menu's 다른 컴퓨터 연결…).</summary>
    internal Task OpenRemoteSettings() => Act(async () => { await service.UpdateAsync(s => s with { SettingsPane = "remote" }); await ShowCategorizedSettingsAsync(); });
```

Run `bash scripts/check-winui-compile.sh`. It is expected to stay clean.

- [ ] **Step 2: Sidebar.** In `MainWindow.Sidebar.cs`, replace the `FontIcon` line and the `title` placement:

```csharp
                label.Children.Add(new FontIcon { Glyph = workspace.Remote is null ? "" : "", TextSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = brushes.Brush(selected ? DesignToken.SidebarAccent : DesignToken.SidebarInk2) });
                var title = new TextBlock { Text = workspace.Name, TextSize = DesignMetrics.Type.Row, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, Margin = MacLine(), VerticalAlignment = VerticalAlignment.Center };
                FrameworkElement name = title;
                if (RemoteHostLabel(workspace) is { } host)
                {
                    // The Mac's computer icon and host name under the workspace (M/WorkspaceView.swift), with the host's OS.
                    var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; stack.Children.Add(title);
                    var line = new TextBlock { Text = host, TextSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(DesignToken.SidebarInk2), TextTrimming = TextTrimming.CharacterEllipsis };
                    AutomationProperties.SetAutomationId(line, "sidebar-remote-host-" + workspace.Id);
                    stack.Children.Add(line);
                    AutomationProperties.SetName(stack, Locale.Get("remote.workspace.badgeAccessibility", new Dictionary<string, string> { ["host"] = host }));
                    name = stack;
                }
                Grid.SetColumn(name, 1); label.Children.Add(name);
```

Remove the old `Grid.SetColumn(title, 1); label.Children.Add(title);`.

- [ ] **Step 3: Header, status bar, pane detail.**

In `RefreshStatusBar`, after `status.Text = …`:

```csharp
        if (state.Workspaces.FirstOrDefault(w => w.Id == state.ActiveWorkspaceId) is { Remote: not null } active && RemoteHostLabel(active) is { } host)
            status.Text = Locale.Get(RemoteConnected(active) ? "remote.status.connected" : "remote.status.notConnected", new Dictionary<string, string> { ["host"] = host });
```

In `RefreshWorkspaceHeader`, beside the git badge:

```csharp
            workspaceHeaderFiles.IsEnabled = RemoteWorkspaceFeatures.AllowsLocalOnly(workspace);
            if (workspace.Remote is not null) ToolTipService.SetToolTip(workspaceHeaderFiles, RemoteWindowsStrings.PaneUnavailable);
            workspaceHeaderGit.Child = workspace.Remote is null ? WorkspaceGitBadge(workspace) : RemoteHostBadge(workspace);
```

Then add `RemoteHostBadge` to `MainWindow.RemoteWorkspaces.cs`. It is a capsule in accent while connected and ink2 otherwise, at the Mac's 0.09 fill:

```csharp
    private Border RemoteHostBadge(Workspace workspace)
    {
        var connected = RemoteConnected(workspace); var host = RemoteHostLabel(workspace) ?? "";
        var ink = connected ? DesignToken.Accent : DesignToken.Ink2;
        var badge = new Border { Child = SettingsGlyphLabel("", host, DesignMetrics.Type.Small), Background = brushes.Brush(ink, 0.09), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Padding = new Thickness(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(badge, "workspace-remote-host");
        ToolTipService.SetToolTip(badge, Locale.Get(connected ? "remote.status.connected" : "remote.status.notConnected", new Dictionary<string, string> { ["host"] = host }));
        return badge;
    }
```

In `MainWindow.cs` `PaneView`:

```csharp
        /// <summary>The pane's CLI: this PC's, or for a remote workspace the host's as its last answer said (or a stand-in before it answered).</summary>
        internal ProviderRuntime? PaneRuntime(string provider) => Workspace.Remote is { } remote
            ? RemoteWorkspaceFeatures.ProviderRuntime(provider, remote, owner.service.Remote?.Connection(remote.ConnectionId)) : owner.Runtime(provider);
```

In the `Capabilities` getter, use `PaneRuntime(pane.Provider)?.Capabilities ?? ProviderCatalog.Capabilities(pane.Provider)`.

In `MainWindow.QueuedComposer.cs:266`, use `RunBlock.Reason(Session, PaneRuntime(Session.Provider)) ?? (Workspace.Remote is { } remote ? RemoteWorkspaceFeatures.BlockedReason(Session, remote, owner.service.Remote?.Connection(remote.ConnectionId)) : null)`. Line 273 gets the same change.

In the detail line (line 853), replace `Locale.Get("workspace.location.thisComputer")` with `(owner.RemoteHostLabel(Workspace) ?? Locale.Get("workspace.location.thisComputer"))`.

For an empty remote pane's note: where the pane draws its empty state, add this text for a remote workspace:

```csharp
Session.Kind == "shell" ? RemoteWindowsStrings.CommandNote(Workspace.Remote.HostName, owner.service.Remote?.Connection(Workspace.Remote.ConnectionId)?.Runtime?.Platform) : Locale.Get("remote.pane.agentNote", new Dictionary<string, string> { ["host"] = Workspace.Remote.HostName })
```

Give it automation id `pane-remote-note-<id>`.

- [ ] **Step 4: Local-only features off.**

`MainWindow.Terminal.cs:144`, first line of `InitializeTerminal`:

```csharp
            // A remote command pane sends each command to the host through the composer: no local ConPTY.
            if (Session.Kind == "shell" && !RemoteWorkspaceFeatures.UsesLocalTerminal(Session, Workspace)) return;
```

`MainWindow.Files.cs` `OpenFilePane`, right after `workspace` is found:

```csharp
        if (!RemoteWorkspaceFeatures.AllowsLocalOnly(workspace)) throw new InvalidOperationException(RemoteWindowsStrings.PaneUnavailable);
```

`MainWindow.Browser.cs` `AddBrowserPane`, first line:

```csharp
        if (service.IsRemoteWorkspace(service.Snapshot.ActiveWorkspaceId)) { error.Text = RemoteWindowsStrings.PaneUnavailable; return; }
```

`MainWindow.Styles.cs:237`:

```csharp
            var pane=Session;var visible=pane.Kind=="claude"&&pane.Provider=="claude"&&pane.AgentViewMode=="mighty"&&RemoteWorkspaceFeatures.AllowsLocalOnly(Workspace);
```

`MainWindow.AgentIO.cs:21`:

```csharp
        service.ConfigureAgentIO((request, workspace) => workspace.Remote is null ? server.Bindings.Bind(request.SessionId, workspace, request.Provider, server.Name, executable) : null);
```

Both add-pane menus (`DashboardAddMenu` and `NewSessionMenu`): iterate over `RemoteWorkspaceFeatures.AddPaneEntries(workspace)` instead of `AddPaneMenu.Entries()`. Pass the same list to `MarkAddPaneMenu`. For a remote workspace, title `AddPaneMenu.Shell` with `Locale.Get("remote.menu.newCommand")` and create the pane titled `Locale.Get("remote.pane.commandTitle")`. Add a last item `Locale.Get("remote.menu.connect")` that calls `OpenRemoteSettings()`, with automation id `workspace-connect-remote-<id>`. In `NewSessionMenu`, resolve the workspace as `service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == service.Snapshot.ActiveWorkspaceId)`. The delegation worktree button needs no WinUI change: `SecondSend` already refuses a remote pane (Task 15).

- [ ] **Step 5: Run the checks to verify they pass**

```bash
bash scripts/check-winui-compile.sh
node scripts/check-design-tokens.js --check
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)"
```

Expected: the expected single `InitializeComponent` line, the ratchets unchanged, and no Korean literal. Then run CORE-RUN: 9 FAILs, and every `remote …` line passes.

- [ ] **Step 6: Commit**

```bash
git add native/windows/MightyClaude.WinUI
git commit -m "(windows) a remote workspace shows its host and OS in the sidebar, header and status bar; its agent and command panes run on the host by its CLIs, and files, browser, styles, agent I/O and child worktrees stay off

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 19: WinUI smoke: host and client in one process over loopback

**Files:**
- Create: `native/windows/MightyClaude.WinUI/MainWindow.RemoteWorkspacesSmoke.cs`
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs` (after the `setupTools` checkpoint, ~line 128)
- Modify: `scripts/test-native-windows.ps1` (after the `setupTools` block, ~line 172)

**Interfaces:**
- Consumes:
  - `InitializeRemoteWorkspaces(loopback: true)`, `BuildRemoteWorkspacesSection`, `RemoteHostLabel`, `remoteHost`, `smokeSend`/`smokeStart` (Task 17/18)
  - `RemoteWorkspaceFeatures`, `MobileDesktopRouter`
- Produces: the smoke key `remoteWorkspaces` with booleans `settingsPage`, `sharing`, `connected`, `workspaceOpened`, `sidebarBadge`, `commandPaneComposer`, `commandRan`, `localOnlyOff`, `phoneHidden`, `keyCopyOptions`, `stopEndsRuns`, `passed`.

- [ ] **Step 1: Write the smoke step (the failing check: the ps1 now requires the key)**

`scripts/test-native-windows.ps1`, after the `setupTools` loop:

```powershell
    # Remote workspaces (MainWindow.RemoteWorkspacesSmoke.cs): a host and a client in this one process over loopback (injected, never a
    # setting), the settings page, a remote command pane run through the composer, local-only features off, the phone not told.
    foreach ($key in @('settingsPage', 'sharing', 'connected', 'workspaceOpened', 'sidebarBadge', 'commandPaneComposer', 'commandRan', 'localOnlyOff', 'phoneHidden', 'keyCopyOptions', 'stopEndsRuns', 'passed')) {
        if ($result.remoteWorkspaces.$key -ne $true) {
            $msg = "Windows remote workspaces smoke failed: remoteWorkspaces.$key"
            Write-SmokeAnnotation $msg; throw $msg
        }
    }
```

`MainWindow.Smoke.cs`, after `Checkpoint("setupTools", "passed");`:

```csharp
            Checkpoint("remoteWorkspaces", "running");
            result["remoteWorkspaces"] = await RunRemoteWorkspacesSmoke();
            Checkpoint("remoteWorkspaces", "passed");
```

`native/windows/MightyClaude.WinUI/MainWindow.RemoteWorkspacesSmoke.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MightyClaude.Core;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// Remote workspaces with nothing outside this process: the app's own host shares the fixture project on loopback (the injected
    /// seam only a smoke run sets), the app's own client connects to it, opens the shared workspace and runs one command through a
    /// remote command pane's composer by the real host runner. No clipboard, no firewall, no Tailscale, no network.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunRemoteWorkspacesSmoke()
    {
        var checks = new Dictionary<string, object?>();
        InitializeRemoteWorkspaces(loopback: true);
        var host = remoteHost ?? throw new InvalidOperationException("no remote host");
        var shared = service.Snapshot.Workspaces.First(w => w.Remote is null);

        var page = BuildRemoteWorkspacesSection();
        Require(page.Children.Count > 0, "the settings page builds (its rows fill in once loaded)");
        checks["settingsPage"] = true;

        var state = await host.StartAsync([shared.Id], 0);
        Require(state is { Enabled: true, AllowsFullAccess: false } && state.Addresses.Single().StartsWith("127.0.0.1:", StringComparison.Ordinal), "sharing on the loopback seam with full access off");
        host.SetAllowsFullAccess(true);
        checks["sharing"] = true;

        var info = await service.Remote!.ConnectAsync("", state.Addresses.Single(), state.Key!);
        Require(info.Status == RemoteConnectionStatus.Connected && info.Runtime?.Platform == "win32", "the client reached the host and read its OS");
        checks["connected"] = true;

        var workspace = await service.OpenRemoteWorkspaceAsync(info.Id, shared.Id);
        Require(workspace.Remote is not null && service.Snapshot.ActiveWorkspaceId == workspace.Id, "the shared workspace opened as remote");
        checks["workspaceOpened"] = true;

        RenderSidebar(); await WaitUI(() => true);
        Require(SettingsElements(root).Any(e => AutomationProperties.GetAutomationId(e) == "sidebar-remote-host-" + workspace.Id && e is TextBlock { Text: var text } && text.Contains("Windows", StringComparison.Ordinal)), "the sidebar shows the host and its OS");
        checks["sidebarBadge"] = true;

        await AddPane("shell");
        var paneId = service.Snapshot.ActiveSessionId!;
        await WaitUI(() => views.ContainsKey(paneId));
        Require(views[paneId].ComposerVisible, "a remote command pane keeps its composer (no local terminal)");
        checks["commandPaneComposer"] = true;

        await service.SendAsync(new StartRunRequest(paneId, workspace.Id, "shell", "echo remote-smoke", []));
        await WaitUI(() => service.Snapshot.Sessions.First(s => s.Id == paneId).Status is "completed" or "error", seconds: 20);
        Require(service.Snapshot.Sessions.First(s => s.Id == paneId).Logs.Any(l => l.Text.Contains("remote-smoke", StringComparison.Ordinal)), "the host ran the command in PowerShell and the pane shows it");
        checks["commandRan"] = true;

        Require(!RemoteWorkspaceFeatures.AddPaneEntries(workspace).Contains(AddPaneMenu.Files) && !RemoteWorkspaceFeatures.AddPaneEntries(workspace).Contains(AddPaneMenu.Browser), "no files or browser pane");
        Require(service.SecondSend(service.Snapshot.Sessions.First(s => s.Id == paneId)) != ComposerSecondSend.Worktree, "no child worktree");
        checks["localOnlyOff"] = true;

        Require(!RemoteWorkspacePhone.Workspaces(service.Snapshot.Workspaces).Any(w => w.Id == workspace.Id) && !RemoteWorkspacePhone.Sessions(service.Snapshot.Sessions, service.Snapshot.Workspaces).Any(s => s.Id == paneId), "the phone is never told");
        checks["phoneHidden"] = true;

        Require(RemoteSecretCopy.Key is { AllowedInHistory: false, Roamable: false }, "the key copy stays out of clipboard history (checked without touching the clipboard)");
        checks["keyCopyOptions"] = true;

        await service.SendAsync(new StartRunRequest(paneId, workspace.Id, "shell", "Start-Sleep 30", []));
        await WaitUI(() => service.IsSessionRunning(paneId), seconds: 10);
        await host.StopAsync();
        await WaitUI(() => service.Snapshot.Sessions.First(s => s.Id == paneId).Status == "error", seconds: 25);
        Require(service.Remote.Connection(info.Id)?.Status == RemoteConnectionStatus.Disconnected, "a host that stops sharing ends the client's runs and connection");
        checks["stopEndsRuns"] = true;

        checks["passed"] = true;
        return checks;
    }
}
```

`views[paneId].ComposerVisible` is the pane's composer visibility. If `PaneView` has no such member, add `internal bool ComposerVisible => composer.Visibility == Visibility.Visible;`, naming the composer field it hides in `InitializeTerminal`. `WaitUI(Func<bool> predicate, string condition = "", double seconds = 4)` (`MainWindow.Smoke.cs:1403`) and `SettingsElements(FrameworkElement?)` (`MainWindow.SettingsNavigation.cs:675`) are the existing smoke helpers.

- [ ] **Step 2: Run the checks to verify they pass where they can**

```bash
bash scripts/check-winui-compile.sh
node scripts/check-design-tokens.js --check
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)"
```

Expected: all clean (`*Smoke*.cs` is exempt from the font ratchet). The smoke itself runs on the Windows CI runner through `scripts/test-native-windows.ps1`, which now fails without `remoteWorkspaces.passed`.

- [ ] **Step 3: Commit**

```bash
git add native/windows/MightyClaude.WinUI/MainWindow.RemoteWorkspacesSmoke.cs native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs scripts/test-native-windows.ps1
git commit -m "(windows) the GUI smoke shares a workspace and connects to it in one process over loopback, runs a remote command through the composer, and checks the badge, the gates, the phone and a stop

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Stage F — Docs

### Task 20: Docs and help in four languages

**Files:**
- Modify: `docs/remote-workspaces.md`
- Modify: `docs/help/content/{ko,en,zh,ja}/remote.md`, `docs/help/content/{ko,en,zh,ja}/windows.md`
- Modify: `docs/windows-parity.md:116` (the Tailscale row)
- Regenerated by the script: `docs/help/site/**` (only if `node scripts/help/build.mjs` writes it in this repo; `--check` must pass)

**Interfaces:**
- Consumes: the behaviour of Tasks 1–19.
- Produces: no code.

- [ ] **Step 1: `docs/remote-workspaces.md`**
  - Change the opening paragraph to say that two computers (Mac or Windows) join by Tailscale.
  - Replace the sentence "Windows 앱에는 아직 없다" with a pointer to the new section.
  - Then add the section below, before `## 검사`.

```markdown
## Windows와 Mac↔Windows

Windows 앱도 같은 `/v2/` 프로토콜로 공유하고 연결한다. 그래서 Mac→Mac, Mac→Windows, Windows→Mac, Windows→Windows가 모두 된다. 두 앱이 같은 요청·응답·이벤트를 쓰는지는 공유 픽스처 `native/contracts/fixtures/remote-v2/`가 묶는다. Mac이 기준이고, Mac(`RemoteWireFixtureTests`)과 Windows Core(`RemoteWireFixtureVerification`)가 같은 파일을 읽는다.

- **호스트(Windows)**: `MightyClaude.Core/Remote/RemoteHostService.cs`가 Tailscale 주소마다 `RemoteHttpListener`(TcpListener, HTTP/1.1, 연결마다 요청 하나)를 그 주소에만 바인딩한다. 받는 순간 상대를 거르고 상대마다 연결 8개, 키 없는 요청의 본문은 읽지 않음, SHA-256 뒤 상수 시간 비교, 상대마다 초당 300번·키가 맞는 요청 전체 600번은 Mac과 같다. 실행은 호스트의 `RunManager`(그 PC의 CLI·로그인, 창별 MCP·위임·그림 캐시 없음)로 한다.
- **명령 창**: Windows 호스트에서는 명령마다 시스템 폴더의 `powershell.exe -NoProfile -NonInteractive -Command <글>`을 공유 폴더에서 새로 띄운다(`RemoteCommandShell`, `NoDefaultCurrentDirectoryInExePath=1`). Mac 호스트는 `$SHELL -l -c`. 창과 상태 막대·세션 정보는 호스트의 OS(`/info`의 `runtime.platform`: `darwin`·`win32`)를 보여 준다.
- **이벤트 맞추기**: Windows 실행 이벤트는 Mac 모양으로 바꿔 보낸다. 로그인 만료 사유는 `auth`이고, 권한 요청 입력은 `inputJSON`이며, 시각은 `yyyy-MM-ddTHH:mm:ss.fffZ`로 쓰고, 그림은 뺀다. 그래프는 Mac의 `graph`(ExecutionGraphNode)로 보낸다. Windows 클라이언트가 실행 요청에 `x-mighty-remote-graph: run`을 붙이면 Windows 호스트는 그 실행에 `graph` 대신 Windows의 `graph_run`(MightyGraphRun)을 보내고, Mac 호스트는 이 머리를 무시한다. Windows 클라이언트는 Mac 호스트의 `graph`를 조용히 버리므로, Mac에서 도는 실행의 Windows 마이티 보기는 대화 기록으로 묶은 블록만 보인다.
- **클라이언트(Windows)**: `RemoteClientService`는 요청마다 그 IP로 가는 출발 주소가 이 PC의 Tailscale 주소인지(UDP 소켓 연결, 보내는 것 없음) 다시 본다. 리디렉션을 따르지 않고 프록시·쿠키를 쓰지 않으며, 응답은 2 MiB까지, 15초(첨부 실행 60초) 안에 읽는다. 폴링이 429·503·시간 초과로 실패하면 10초 동안 다시 묻는다. 쓸 수 없는 이벤트 하나는 빼고 안내 줄을 남기며, 기다리는 권한 요청은 창마다 최신 32개만 둔다.
- **저장(Windows)**: 연결 목록은 `remote-connections.json`(버전 2, 키 없음, 이 사용자만 읽음), 키는 `<프로필>\remote-keys\<id의 SHA-256>.bin`에 현재 Windows 사용자 DPAPI(`CryptProtectData`, `ProtectedData`의 CurrentUser와 같음)로 암호화해 둔다. DPAPI가 거절하면 키는 앱을 끌 때까지만 메모리에 있다. `Workspace.remote`는 Mac과 같은 `{"version": 2, …}`이며 버전이 없거나 다른 값이면 불러올 때 버린다.
- **방화벽**: 설정의 **방화벽 규칙 추가**가 휴대폰 직접 연결과 같은 규칙 하나(TCP, 이 앱 실행 파일, 100.64.0.0/10·fd7a:115c:a1e0::/48에서만)에 켜진 포트(공유 포트, 휴대폰 직접 포트)를 넣는다. 추가·제거할 때 Windows가 관리자 확인 창(UAC)을 한 번 띄운다. 앱은 이 밖에 권한 상승을 요청하지 않는다.
- **키 복사**: 클립보드 기록(Win+V)과 클라우드 클립보드에 남지 않게 복사하고(`ClipboardContentOptions` IsAllowedInHistory·IsRoamable 끔), 60초 뒤에도 그 키가 클립보드에 있으면 지운다.
- **기기 확인 남음**: UAC 창과 방화벽 규칙, Mac→Windows와 Windows→Mac 실제 연결은 Windows PC에서 확인해야 한다(`docs/windows-screen-checklist.md`).
```

In `## 검사`, add a line naming `RemoteWireFixtureTests`, the Windows Core checks (`RemoteWorkspaceStateVerification`, `RemoteWireFixtureVerification`, `RemoteClientVerification`, `RemoteHostVerification`, `RemoteLoopbackVerification`, `RemoteDesktopVerification`) and the smoke key `remoteWorkspaces`.

- [ ] **Step 2: Help pages.**

In `docs/help/content/ko/remote.md`:
- In the first paragraph, replace "Mac 두 대의" with "Mac이나 Windows 컴퓨터 두 대의", and delete `{{platform:mac}} Windows 앱에는 아직 없습니다.`.
- In 준비, replace "두 Mac에" with "두 컴퓨터에".
- After the Keychain sentence in `#connect`, add:

```markdown
{{platform:windows}} Windows에서는 연결 키를 이 Windows 사용자만 풀 수 있게 암호화해 앱의 데이터 폴더에 저장합니다. 공유할 때는 {{ui:remote.windows.firewall.add}} 단추로 Tailscale 대역에서 오는 연결에만 앱의 포트를 여는 방화벽 규칙 하나를 추가합니다. 이때 Windows가 관리자 확인 창을 한 번 띄웁니다.
```

- In `#use`, add:

```markdown
- 원격 창은 상태 막대와 세션 정보에 실행 컴퓨터의 OS(macOS·Windows)를 보여 줍니다. {{ui:remote.menu.newCommand}} 창은 Mac 호스트에서는 로그인 셸, Windows 호스트에서는 PowerShell로 명령을 실행합니다.
```

Make the same edits in `en`, `zh` and `ja`, with:
- en: "{{platform:windows}} On Windows the connection key is saved encrypted so only this Windows user can read it, in the app's data folder. When sharing, the {{ui:remote.windows.firewall.add}} button adds one firewall rule that opens the app's ports only to Tailscale's ranges; Windows shows its administrator prompt once." and "- A remote pane shows its host's OS (macOS or Windows) in the status bar and the session info. A {{ui:remote.menu.newCommand}} pane runs commands in the login shell on a Mac host and in PowerShell on a Windows host."
- zh: "{{platform:windows}} 在 Windows 上，连接密钥加密保存在应用的数据文件夹中，只有当前 Windows 用户可以读取。共享时，{{ui:remote.windows.firewall.add}} 按钮会添加一条防火墙规则，只向 Tailscale 地址段打开应用的端口；Windows 会弹出一次管理员确认。" and "- 远程窗格会在状态栏和会话信息中显示运行电脑的系统（macOS 或 Windows）。{{ui:remote.menu.newCommand}} 窗格在 Mac 主机上用登录 shell、在 Windows 主机上用 PowerShell 运行命令。"
- ja: "{{platform:windows}} Windowsでは接続キーを、このWindowsユーザーだけが読めるよう暗号化してアプリのデータフォルダーに保存します。共有するときは {{ui:remote.windows.firewall.add}} ボタンで、Tailscaleの範囲からの接続にだけアプリのポートを開くファイアウォールルールを1つ追加します。このときWindowsの管理者確認が1回表示されます。" and "- リモートペインは、ステータスバーとセッション情報に実行コンピューターのOS（macOS・Windows）を表示します。{{ui:remote.menu.newCommand}} ペインは、Macホストではログインシェル、WindowsホストではPowerShellでコマンドを実行します。"

In each `windows.md`, add one row to the differences table at the end (`| 기능 | Windows |` style, matching the existing last table):
- ko: `| 원격 연결 (Tailscale) | Mac과 같은 화면과 프로토콜이며 Mac↔Windows 어느 방향이든 연결됩니다. 연결 키는 Keychain 대신 이 Windows 사용자만 풀 수 있는 암호화 파일에 두고, 공유하려면 방화벽 규칙 하나를 추가합니다(관리자 확인 한 번). 원격 명령은 Windows 호스트에서 PowerShell로 실행합니다. |`
- en, zh and ja: the same sentence in each language, matching the help remote.md wording above.

In `docs/help/content/*/settings.md` `#remote`, replace "이 Mac의"/"다른 Mac이" (and their en/zh/ja equivalents) with "이 컴퓨터의"/"다른 컴퓨터가".

- [ ] **Step 3: `docs/windows-parity.md:116`.** Replace the Windows column and the status column of the Tailscale row with:
  - Windows column: `Core Remote/RemoteHostService·RemoteClientService·RemoteHttpListener·RemoteTransport·RemoteWorkspaceModels·RemoteWireEvents·RemoteKeyVault(DPAPI)·RemoteAddress·RemoteCommandShell, Models.cs Workspace.Remote(버전 2), StateStore(KeepsWorkspace·ApproveRemote); WinUI MainWindow.RemoteWorkspaces.cs(설정 → 원격 연결), 사이드바·머리글·상태 막대의 호스트·OS, 원격 에이전트·명령 창, 스모크 remoteWorkspaces; 공유 픽스처 native/contracts/fixtures/remote-v2`
  - Status column: `완료 — 기기 미확인. Core 검사 remote state/wire/client/host/loopback/desktop, Mac RemoteWireFixtureTests. UAC·방화벽 규칙, Mac→Windows·Windows→Mac 실제 연결은 Windows PC에서 확인 대기`

- [ ] **Step 4: Run the doc checks**

```bash
node scripts/help/build.mjs --check
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)"
```

Expected: help `--check` passes (every `{{ui:…}}` key exists, including `remote.windows.firewall.add`), and check-locales is clean.

- [ ] **Step 5: Commit**

```bash
git add docs/remote-workspaces.md docs/help/content docs/windows-parity.md
git commit -m "(windows) the docs and help describe remote workspaces on Windows and Mac↔Windows: the shared fixtures, PowerShell commands, the host OS, DPAPI keys, the firewall rule and the clipboard-safe key copy

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 21: The whole verification pass

**Files:** none changed, unless a check fails. Fix the cause, then commit the fix as `(windows) …`.

- [ ] **Step 1: Windows Core.** Run CORE-RUN.
Expected:
- Exactly 9 `FAIL` lines, all Windows-only.
- Every `remote …` line PASSes: strings, state, wire, client, host, loopback, desktop and settings.
- `git diff --stat -- native/windows/MightyClaude.Core.Tests/Verification.cs` shows nothing.

- [ ] **Step 2: WinUI compile.** Run `bash scripts/check-winui-compile.sh`.
Expected: `WinUI: C# 컴파일 오류 없음 (XAML 전용 InitializeComponent 1건만 예상대로 남음)`.

- [ ] **Step 3: Mac suites.**

```bash
bash scripts/test-native-macos.sh --filter 'RemoteWireFixtureTests|\.RemoteWorkspace[A-Za-z]*Tests/|StateRepository|MobileRemote'
swift build --package-path native/macos --product MightyClaude
```

Expected: every test passes, and the app target builds.

- [ ] **Step 4: Repository checks.**

```bash
node scripts/check-locales.js
node scripts/check-locales.js --check --touched-since "$(cat scripts/locale-touched-baseline)"
node scripts/check-design-tokens.js --check
bash scripts/check-style-freeze.sh
node scripts/help/build.mjs --check
(cd mobile && npx jest --silent)
git status --short
```

Expected:
- Each command exits 0.
- `git status` shows nothing uncommitted, except `.ouroboros_eval_artifact.md` and `docs/superpowers/`, which were there before this plan.

- [ ] **Step 5: Record what only a Windows PC can confirm.** Add three rows to `docs/windows-screen-checklist.md` (the device checks):
  - the UAC prompt and the firewall rule for port 43137;
  - a Mac connecting to a Windows host and running an agent and a PowerShell command;
  - a Windows PC connecting to a Mac host, with the status bar saying macOS.

  Commit:

```bash
git add docs/windows-screen-checklist.md
git commit -m "(windows) the device checklist names what remote workspaces still need a real Windows PC for: the UAC prompt and firewall rule, Mac→Windows and Windows→Mac

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Self-review against the spec

**Spec §3 and the top decisions, requirement by requirement:**

| Spec requirement | Where |
|---|---|
| Same `/v2/` protocol, `x-mighty-remote-version: 2`, port 43137, bearer key, constant-time compare, events by cursor | Tasks 4 (constants, `KeysMatch`), 8 (transport), 13 (authorize), 14 (cursor polls) |
| Request, response and event samples in `native/contracts/fixtures/remote-v2/*.json`, checked by both apps | Task 1 (files, Mac check, README), Tasks 3–5 (Windows checks) |
| Host shares only the picked workspaces | Task 13 (`StartAsync` local-only check), Task 14 (`SharedWorkspaces` on every run) |
| Host binds only Tailscale addresses | Task 13 (`TailscaleAddresses.Current`, one `RemoteHttpListener` per address) |
| Peers filtered at accept, connections per peer capped | Task 11 (`acceptPeer`, `maximumConnectionsPerPeer`), Task 13 (8, `RemotePeerPolicy`) |
| No request body read without the key | Task 11 (body limit 0 means the head alone), Task 13 (`BodyLimit`) |
| "원격에서 전체 권한·명령 실행 허용" off each time sharing starts | Task 13 (`StartAsync`/`StopNowAsync` reset; tests), Task 17 (switch) |
| Command pane runs `powershell.exe -NoProfile -NonInteractive -Command` in the shared folder | Task 12 (`RemoteCommandShell`, `RunManager.PowerShellCommands`), Task 14 (real command in the shared folder) |
| Heartbeat timeout 30 s | Task 4 (constant), Task 14 (`LeaseLoopAsync`, test with 1 s) |
| Firewall rule from part 2 | Task 15 (`FirewallPorts`), Task 17 (buttons calling `WindowsFirewallRuleRunner.ApplyAsync`/`RemoveAsync`) |
| Client keeps `remote-connections.json` without the key; key DPAPI CurrentUser | Task 6 |
| Re-checks the tunnel route before every request | Task 7 (`RemoteRoute`), Tasks 9–10 (`EnsureRouteAsync` on connect, ensure, start, poll, answer, stop) |
| Refuses redirects; caps a response at 2 MiB | Task 8 |
| Drops a malformed event on its own, with a notice | Task 5 (`Read`), Task 10 (skipped notice, test) |
| Keeps the newest 32 pending permissions | Task 5 (`RemotePendingPermissions`), Task 15 (`ReceiveRemotePermission`) |
| `Workspace.remote` version 2, same JSON as the Mac; unversioned dropped | Task 3, plus `state-references.json` in Task 1 |
| Out of scope for remote panes: delegation, files pane, browser pane, file-reading styles | Task 15 (`SecondSend`, `WorktreeHostFolder`, `RemoteWorkspaceFeatures`), Task 18 (WinUI gates) |
| The phone projection hides remote workspaces | Task 15 (`MobileDesktopRouter`), Task 19 (smoke) |
| Mac↔Windows in every direction; the pane shows the host OS | Task 1 (fixtures with a Windows host and a Mac path), Tasks 5 and 14 (Mac-shaped events from a Windows host), Task 16 (Mac shows the OS), Task 18 (Windows shows the OS). `/info` already carries `runtime.platform` (`darwin`/`win32`), so no new field is needed. |
| Verification list | CORE-RUN in every Core task; `check-winui-compile` in Tasks 15, 17–19 and 21; the smoke registered in Task 19; the Mac suites in Tasks 1, 16 and 21; locales, design tokens, style freeze, help and jest in Task 21 |
| Real-PC checks | Task 21, Step 5 |

**Placeholder scan.** No TBD, TODO or "similar to Task N". Four places tell the executor to use a name that is already there instead of the one written:
- the two consumed helpers (phone-direct names; see Global Constraints);
- `MobileDirectPortOrNull()` and `DesignToken.ErrorText` in Task 17;
- `ComposerVisible` in Task 19, with the exact fallback line given;
- the Mac `store` environment object in Task 16.

These are reconciliations with code this plan cannot see yet, not unfinished design.

**Type consistency.** The names used across tasks match their definitions:
- `RemoteWireStart.From`/`ToRequest`, `RemoteWireEvents.Read`/`FromRunEvent`/`FromPermission`/`FromGraphNode`
- `RemoteIncoming.Usable`/`Ignored`/`HostRunId`
- `RemoteClientService.ConnectAsync`/`RefreshAsync`/`EnsureConnectedAsync`/`DisconnectAsync`/`RemoveAsync`/`OpenWorkspace`/`StartAsync`/`StopAsync`/`SteerAsync`/`RespondToPermissionAsync`/`AnswerQuestionsAsync`/`AnswerPlanAsync`/`IsRunning`
- `RemoteHostService.StartAsync`/`StopAsync`/`Current`/`SetAllowsFullAccess`/`BodyLimit`/`FullAccessRefusal`
- `IRemoteRunner`, `RemoteRunnerSinks(Event, Permission, GraphNode)`
- `DesktopService.AttachRemote`/`Remote`/`OpenRemoteWorkspaceAsync`/`ResolveSharedAsync`/`CreateRemoteRunner`/`IsRemoteWorkspace`/`IsRemoteSession`
- `RemoteWorkspaceFeatures.*`, `RemoteSecretCopy.Key`/`ShouldClear`, `RemoteWindowsStrings.*`
- `AnswerPlan` becomes `PlanRecord?`; the WinUI callers that ignore it compile unchanged (checked in Task 15, Step 6).

**Known limits, written down rather than built:**
- A Windows client's Mighty views of a run on a Mac host get no graph nodes, because the Windows client ignores the Mac's `graph` event. The transcript-built view still shows. A reducer for Mac nodes on Windows is follow-up work.
- The host's admission reads WinUI flags from the request thread without marshalling. A race only lets one run through, or holds one, around the moment an update starts.
