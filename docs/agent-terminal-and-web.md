# Agent Terminal and Web-Open Bridge

Every Claude and Codex agent pane in MightyClaude gets a dedicated terminal pane and a URL-opening flow through a per-pane bundled stdio MCP server. This document explains the routing rules, the run/read/stop cycle, the URL choice card, pane lifetime, permission handling, and the per-pane token rules.

---

## 1. Routing: what goes to the terminal, what stays in Bash

The per-pane MCP server exposes exactly four tools under the name `mighty-terminal`:

| Tool | Purpose |
|------|---------|
| `run_in_terminal` | Run a command in the user's dedicated terminal pane where they can watch it live. Use for commands the user should see and for long-running processes such as dev servers, watchers and long builds. |
| `read_latest_output` | Read the output produced since the last read by a process started with `run_in_terminal`. |
| `stop` | Stop a process started with `run_in_terminal` with an escalating signal sequence (see §2). |
| `open_url` | Open an http or https URL in the in-app browser pane or the system browser per the user's choice for this workspace. |

The tool descriptions deliberately guide the agent: **short internal work** (grep, file reads, quick build or test checks) stays in the agent's built-in Bash tool and runs quietly. Only commands the user should observe, and long-running processes, go through `run_in_terminal`. The server also sends this instruction at `initialize` time, so it reaches the agent even before it reads any one tool's description.

The routing choice is made by the agent following the tool descriptions; the Mac app adds no extra approval surface beyond the agent's own permission mode (see §5).

---

## 2. The run / read / stop cycle

### Running a command

`run_in_terminal` launches the command in the agent pane's dedicated terminal pane, then waits until the process exits or a fixed **12 seconds** (`AgentTerminalRunner.initialWaitSeconds`) pass.

- If the process exits within 12 seconds, the response has `status: done`, the full captured output, and the exit code. No handle is needed for follow-up.
- If the process is still running after 12 seconds, the response has `status: running`, the output captured so far, no exit code, and a **handle** (a UUID string). The agent stores the handle and polls with `read_latest_output`.

The wait and the stop timers below all read an injectable clock, so the timer logic is tested with a fake clock. A few tests also run real PTY processes, and those take real time (the stubborn-process stop test takes about 8 seconds).

### Reading output

`read_latest_output(handle)` returns:

- `output` — combined process output and user-typed text captured since the last read for that handle, capped at **64 KB** (65,536 bytes, `AgentTerminalRunner.readChunkBytes`) per call. The cut never lands inside a UTF-8 character.
- `status` — `running` or `done`.
- `exitCode` — set once `status` is `done` and the process exited normally.
- `signal` — set once `status` is `done` and a signal ended the process instead.
- `outputDropped` — `true` if older bytes were evicted from the ring buffer since the caller's last read.
- `moreRemains` — `true` if more unread bytes follow; call again to continue reading.

Each call advances an internal read cursor so successive reads do not repeat the same bytes.

**Ring-buffer limit:** each process keeps the most recent **1 MB** (1,048,576 bytes, `PTYAgentTerminalPane.ringBufferMaxBytes`) of combined output and user-typed text. Bytes beyond that limit are evicted from the oldest end and `outputDropped` is set.

**User input** typed into the terminal pane does not get appended to the output buffer directly. It is written to the command's PTY, and the tty's own echo writes it back into that same PTY's output stream — the same path a real terminal uses — so it shows up in the next `read_latest_output` exactly as the terminal displayed it. Input the program hides (a password prompt with echo off) is never echoed, so it never appears in the agent's output either.

**Agent-facing cleanup:** the text handed back to the agent has ANSI/VT escape sequences (CSI, OSC, and other ESC-introduced sequences) stripped, and CR LF and bare CR line endings normalized to LF (`AgentTerminalOutputCleaner`). This cleanup applies only to what the agent reads — the visible terminal pane and the raw per-handle buffers are untouched, so colours, cursor movement and progress bars still render normally on screen.

### Stopping a process

`stop(handle)` sends an escalating signal sequence to the command's process group:

1. **SIGINT** immediately.
2. **SIGTERM** after 3 seconds, if the process is still running.
3. **SIGKILL** after 5 more seconds, if the process is still running.

The result — the final output plus the exit code or, when a signal ended it, the signal — is reported within 10 seconds of the call, whatever the process does. The agent does not need to poll after calling `stop`.

---

## 3. Per-workspace URL choice, the 30-second fallback, and Settings

### First open

When the agent calls `open_url` with an http or https URL and the workspace has no remembered choice, the app shows a small card **inside the agent pane that asked**, above its composer — not a separate window. Only that request waits on the user; the rest of the app, and that pane's own transcript, stay usable, and a second pane asking for a different URL gets its own card.

The card shows the URL, a **"Remember my choice for this workspace"** checkbox, and two buttons: **Open in App** and **Open in Browser**.

- **Open in App** — opens the URL in the agent pane's own in-app browser pane.
- **Open in Browser** — hands the URL to the system's default browser.
- **Remember checkbox** — if ticked, the choice is saved for this workspace and the card is not shown again for it.

**30-second fallback:** if the user does not answer within 30 seconds (`WebOpenService.defaultPromptTimeoutSeconds`, also read from an injectable clock in tests), the app takes the card down and opens the URL **in-app** without saving anything. The question is still unanswered for this workspace, so the next `open_url` call without a remembered choice shows the card again. The agent does not wait on the card's answer itself; the 30-second clock runs entirely on the app's side.

### Remembered choice

When the workspace has a remembered destination, `open_url` opens immediately with no card. A rejected URL (wrong scheme, empty, or over 8,192 characters) returns an error and opens nothing; no card is shown for it either.

**Accepted schemes:** `http` and `https` (including localhost), and the URL must be 8,192 characters or fewer. All other schemes — `file:`, `javascript:`, `data:`, and everything else — plus empty input and over-long URLs are rejected with a clear error message.

### Settings entry

Settings shows a per-workspace picker labelled **"Open agent links"** with three values:

| Value | Behaviour |
|-------|-----------|
| Ask each time | The card appears on the next `open_url` call. |
| In app | Always open in the in-app browser pane; no card. |
| In system browser | Always open in the system browser; no card. |

The remembered choice — whether set from the card's checkbox or from this picker — is stored in `UserDefaults` under the key `agentTerminal.webOpenChoices`, keyed by workspace id, and survives a restart. Settings and every open agent pane read the same store, so a change made in Settings is in force on the very next `open_url` call from panes that are already open; there is no per-pane copy that can go stale.

### In-app opening and the fallback to the system browser

An in-app open shows the page in **one agent-browser pane per agent pane**: the pane is made on that agent pane's first in-app open and reused (navigated to the new URL) for every later one. It sits next to the agent pane, the same way the terminal pane does.

The in-app browser pane needs the CEF browser engine, which is **off by default** (Settings → Display → "Use browser panes (experimental)", `browser.engineEnabled`) and, once turned on, only takes effect after the app is **restarted** — the setting is read once at launch. When the engine is off, missing from the build, the agent pane it belongs to is already gone, or the layout has no room for another pane, the in-app browser pane cannot be made, and `open_url` falls back to the system browser instead. The tool result is truthful about this: it tells the agent the page opened in the user's system browser because the in-app browser pane was chosen but could not show it, rather than claiming it opened in the app.

---

## 4. Pane lifetime

### Terminal pane

One PTY-backed terminal pane is created for an agent pane on the **first use** of `run_in_terminal` and reused for every later command from the same agent pane. It opens to the right of the agent pane (or as a tab when the layout has no room for a split) **without taking focus** — the agent pane stays selected. The terminal uses the app's existing Ghostty-based terminal view, the same one manual terminal panes use.

**Typing:** the user can type into the terminal pane. Keystrokes go to the most recently launched command that is still running; if none is running, keystrokes are dropped. What is typed reaches the agent's `read_latest_output` through the tty's echo (see §2).

**Closing:** closing the terminal pane leaves the running processes and the pane's scrollback alone — nothing is torn down. While the agent pane is still open, its header's terminal button, or the agent's next `run_in_terminal` command, reopens the same terminal pane with its history intact. A closed agent pane cannot be reopened; once it is closed, its processes keep running until the app quits and can only be reached by typing in its terminal pane (if that is still open).

### Process lifetime

Processes started through the terminal tool keep running after the terminal pane or the agent pane that started them is closed. They are stopped only when the app quits: `AgentProcessRegistry.terminateAll` sends SIGTERM to every still-running process group, waits up to a 1-second grace period, then sends SIGKILL to whatever is still running. The app does not rely on the OS to reap them.

The `AgentProcessRegistry` is keyed by agent pane id and lives for the app's lifetime. Every new agent run in the same agent pane gets a **new MCP token** (see §6) but re-attaches to the same runner, so it can call `read_latest_output` or `stop` on handles started by earlier runs in that pane.

### Browser pane

The in-app browser pane opened by `open_url` (see §3) is a CEF browser pane. An agent pane owns at most one browser pane; a later in-app open navigates that same pane rather than opening a new one, and brings it back if the user closed it. An external open opens no in-app pane.

### Relay pane list

Both the terminal pane and the browser pane appear in the relay pane-list payload alongside the agent pane that owns them, so the phone can see them. They carry `terminal: true` and are view-only from the phone: the phone lists them but does not let the user send commands to them. Rendering their contents on the phone is out of scope.

---

## 5. Permission handling

Commands sent through `run_in_terminal` follow the agent's **own permission mode**:

- For Claude: the run's own `--permission-mode` (`default`, `acceptEdits`, `bypassPermissions`, and so on) and its own prompt flow.
- For Codex: the run's own approval policy and sandbox mode.

The Mac app adds no extra approval window of its own. The terminal tool is subject to the same permission checks as any other tool the agent calls; the user's existing permission settings govern whether the tool call proceeds.

Claude also keeps the user's own configured MCP servers for the run: the per-pane server is added to the run's `--mcp-config` alongside them, and the app does not pass `--strict-mcp-config` (which would otherwise limit the run to only the servers named there).

---

## 6. Per-pane token rules

### Generation

A fresh **256-bit random token** (32 bytes, hex-encoded) is generated for each agent pane immediately before the CLI is spawned, held only in memory:

- **Claude** gets `--mcp-config` JSON that only names the `mighty-terminal` server (command and arguments, no `env` block). The token and socket path are set in the Claude CLI's own process environment (`MIGHTY_PANE_TOKEN`, `MIGHTY_AGENT_IO_SOCKET`), which the stdio server inherits; they never appear in argv or in the config JSON.
- **Codex** receives it via `-c mcp_servers.mighty-terminal.*` flags, with `mcp_servers.mighty-terminal.env_vars` naming the two environment variables (`MIGHTY_PANE_TOKEN`, `MIGHTY_AGENT_IO_SOCKET`) to pass through from the Codex process environment.

### Transport: the unix socket

The per-pane MCP server forwards each tool call over an app-side unix socket. The socket lives in a directory created with **0700** permissions, and the socket file itself is **0600**; the server only accepts connections from a peer running as the same user. The preferred path is under the profile's data directory; when that path is too long to fit a `sockaddr_un`, it falls back to a short, stable path under `$TMPDIR` (an FNV-1a hash of the profile path, so it stays the same across runs for the same profile).

### Scope and rejection

Every tool call arriving on the socket carries the token. `PaneMCPBindingRegistry` resolves the token to the one agent pane that owns it, using a constant-time comparison; no pane id travels as a tool argument. A foreign or unknown token resolves to no pane, and the call fails with an error telling the agent to reopen the pane. A handle from another pane is rejected the same way: each pane's runner only recognizes handles it issued itself, so a foreign handle passed to `read_latest_output` or `stop` returns an "unknown handle" error rather than reaching another pane's process.

### Never logged

The token is never written to disk and never logged. `PaneMCPBinding`'s `description` and `debugDescription` substitute `<pane-token-redacted>` so that interpolating the binding into a log line cannot leak the token, and `redactingToken(in:)` replaces the token in any other text before it reaches a log.

### Revocation

- **Pane close** — `PaneMCPBindingRegistry.revoke(agentPaneId:)` removes the entry immediately. Any tool call with the old token then resolves to no pane.
- **App quit** — `PaneMCPBindingRegistry.revokeAll()` clears every entry.
- **New run in the same agent pane** — the run gets a new token and a new MCP server binding, but re-attaches to the same `AgentProcessRegistry` entry and its surviving process handles (see §4).

Tokens are never reused across runs.

---

## How to check it

Build and test only through the project's Swift test script, with the Command Line Tools as the active developer directory:

```sh
export DEVELOPER_DIR=/Library/Developer/CommandLineTools
bash scripts/test-native-macos.sh --scratch-path /tmp/mc-agent-terminal-scratch --filter <Name>
```

| Filter | Covers |
|--------|--------|
| `AgentTerminalTool` | A command run through `run_in_terminal` returns its full output and exit code. |
| `LongRunningProcess` | The 12-second running/handle path, 64 KB reads with 1 MB retention, and the stop escalation. |
| `TerminalPaneLifetime` | One pane per agent pane, reuse, survival past pane close, and typed input reaching read-back. |
| `OpenURLChoice` | The URL choice card, the remembered choice, the 30-second fallback, and Settings. |
| `PaneBinding` | Per-pane MCP tokens, pane isolation, and revocation. |
| `AgentToolSurface` | Exactly the four tools are exposed, with the expected routing guidance. |
| `AgentPaneListPayload` | The terminal and browser panes appear in the relay pane-list payload. |

Run each filter separately, substituting it for `<Name>` above.
