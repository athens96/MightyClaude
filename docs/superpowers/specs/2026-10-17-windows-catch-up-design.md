# Windows catch-up: cross-agent children, phone direct path, remote workspaces

Date: 2026-10-17. The work is approach A: the Mac designs ported to C#, with shared contract fixtures binding the two apps.

## Order

The three parts are independent. Build them in this order, each with its own plan:

1. Cross-agent children: `docs/superpowers/plans/2026-10-17-windows-cross-agent-children.md`.
2. Phone direct path to a Windows host: `docs/superpowers/plans/2026-10-17-windows-phone-direct.md`.
3. Tailscale remote workspaces on Windows: `docs/superpowers/plans/2026-10-17-windows-remote-workspaces.md`.

Part 2 builds the Windows Tailscale address rule and the firewall rule helper. Part 3 reuses both.

## Decisions (user)

- All three parts are in scope.
- The order is: children, then phone direct, then remote workspaces.
- **Windows Firewall.** The app adds a narrow inbound rule itself.
  - The rule allows only TCP on the app's ports, only from 100.64.0.0/10 and fd7a:115c:a1e0::/48, and only for the app's own executable.
  - Adding it shows one UAC prompt, which the user sees.
  - Turning the feature off removes the rule, again with a UAC prompt.
  - This is the one place the app asks Windows to elevate a command. The command is fixed: `netsh advfirewall firewall add|set|delete rule …` (set rewrites the rule by that name in place, so rules never pile up), built from constants, the validated ports and the app's own path, with `localip=` and `remoteip=` both limited to the two Tailscale ranges.
- **Remote workspaces connect Mac↔Windows in every direction:** Windows→Windows, Windows→Mac and Mac→Windows.
  - A command pane runs in the host's shell: zsh on a Mac host, PowerShell on a Windows host.
  - The pane shows the host OS.

## 1. Cross-agent children (Windows)

This mirrors Mac commit 925311b (`DelegationProviders.swift`, `DelegationDelegateTool.swift`, `AppStore+Delegation.swift`, `SessionPaneView.swift`).

- **Tool.** `delegate` takes an optional `agent`: `claude`, `codex`, or `antigravity` (alias `gemini`). The default is the caller's own agent.
  - An unknown name is refused with `agent_unknown`.
  - A missing or signed-out CLI is refused with `agent_unavailable`. The detail names the agent and points to Settings › CLI.
  - `list_children` and `child_status` include the agent.
- **Modes across agents.** A child's capabilities must be a subset of its parent's. There are four capabilities: `writesAsking`, `writesUnasked`, `runsUnasked` and `fullAccess`. A parent's `writesUnasked` covers a child's `writesAsking`.
  - Within one agent, the existing rank comparison stays unchanged.
  - The capability table and the "widest child mode" table move to a new shared fixture, `native/contracts/fixtures/delegation-modes.json`. The Mac tests and the Windows Core tests both read it, and the Mac code is checked against it.
- **Children started by the human.**
  - Ctrl+Enter, or a click on the worktree button, starts a child of the same agent, as today.
  - Right-clicking the button opens a flyout listing Claude, Codex and Antigravity. Entries that can't be used are disabled, with the reason.
  - If a child of another agent fails to start, its text goes back to the composer.
- **Which side owns what.**
  - The child's own agent decides: CLI arguments, the writable root or `--add-dir`, MCP attachment, app auto-commit, and the conflict request.
  - The parent's agent decides: notices and `merge_human_only`.
- **Model and effort.**
  - A human-started child of another agent copies the model and effort from the last-used pane of that agent.
  - A child created by the agent gets that agent's defaults.

## 2. Phone direct path to a Windows host

This mirrors Mac 773c87f and 4c13fce (`MobileDirect.swift`, `MobileRemoteService.swift`, `TailscaleAddresses.swift`).

- **Address rule.** It matches the Mac: an interface counts as Tailscale only if it also carries an fd7a:115c:a1e0::/48 address, and its addresses in both ranges are used. On Windows, the interfaces come from `NetworkInterface.GetAllNetworkInterfaces()`.
  - The cases move to a shared fixture, `native/contracts/fixtures/tailscale-addresses.json`, read by both apps.
- **Listener.** A WebSocket listener on each Tailscale address, port 43139, speaking the relay's phone-facing protocol with the same session and auth.
  - Unauthenticated peers get the generic `unauthorized`.
  - Failed auth is limited to 5 per IP per 60 s.
  - At most 4 handshakes at once, 2 per IP, and 36 connections.
  - Peers outside the two ranges are dropped.
  - Ids start with `direct-`.
- **Advertising.** `auth_ok.direct` and `/m1/info` `direct` carry the endpoints, and the capability list includes `"direct"`. The phone needs no change.
- **Settings.** A direct switch (default on) and a port field in the Windows mobile remote settings, plus a status line and the firewall rule control.

## 3. Tailscale remote workspaces on Windows

This mirrors merge 76ca1cc and commit 9d41d6b (`RemoteHostService.swift`, `RemoteClientService.swift`, `RemoteTransport.swift`, `RemoteWorkspaceModels.swift`, `RemoteKeychain.swift`).

- **Protocol.** The same `/v2/` protocol, `x-mighty-remote-version: 2`, port 43137, a bearer key compared in constant time, and parsed events read by cursor.
  - Request, response and event samples move to a shared fixture, `native/contracts/fixtures/remote-v2/*.json`, checked by both apps.
- **Host on Windows.**
  - Shares only the workspaces the user picks.
  - Binds only Tailscale addresses.
  - Filters peers at accept and caps connections per peer.
  - Reads no request body without the key.
  - The switch "원격에서 전체 권한·명령 실행 허용" is off each time sharing starts.
  - A command pane runs `powershell.exe -NoProfile -Command` in the shared folder.
  - Heartbeat timeout: 30 s.
  - The firewall rule comes from part 2.
- **Client on Windows.**
  - Keeps connections in `remote-connections.json` without the key. The key is protected with DPAPI (CurrentUser).
  - Re-checks the tunnel route before every request.
  - Refuses redirects.
  - Caps a response at 2 MiB.
  - Drops a malformed event on its own, with a notice.
  - Keeps the newest 32 pending permissions.
- **State.** `Workspace.remote` with `version: 2`, the same JSON shape as the Mac. A Windows build older than this one drops remote entries.
- **Out of scope for remote panes, same as the Mac:** delegation, the files pane, the browser pane, and styles that read local files. The phone projection hides remote workspaces.

## Verification (all parts)

- Windows Core tests run on the Mac with a temporary try/catch wrapper; 9 Windows-only failures are expected.
- `bash scripts/check-winui-compile.sh`.
- WinUI smoke steps registered in `scripts/test-native-windows.ps1`.
- Mac suites that read the new shared fixtures.
- `check-locales` (both modes), `check-design-tokens` (the font-literal ratchet), `check-style-freeze`, `help/build.mjs --check`, and phone jest if the locales change.
- Real Windows PCs must still check:
  - the UAC prompt and the firewall rule;
  - a phone connecting directly to a Windows host;
  - remote workspaces Mac→Windows and Windows→Mac.
