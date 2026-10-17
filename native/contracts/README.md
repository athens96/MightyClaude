# Native client contracts

The Swift and C# clients implement the same version 1 JSON contract, with the
optional native extensions below. They run independently without a Node.js or
browser host. Provider CLIs retain their own installation/runtime
requirements.

## State

`workspace-state.json` remains version 1. Optional legacy `provider` and
`settings` fields default to Claude and automatic effort/manual permission.
Only `running` states become `stopped` during restore.

Native profiles are separate from the Electron profile. Initial migration reads
the old snapshot and saves a copy; it never overwrites the old file.

Optional, on both desktops: a delegated child pane's `RunSession` may carry
`parentSessionId` (its parent pane's session id) and `workingFolder` (the absolute
path of its worktree). Restore and normalize keep them on agent panes, including
a child whose parent pane was closed, and drop malformed values. A new pane never
copies them from its template, and `RunSettings` never carries them. Snapshots
without them load unchanged. The delegation side file is their authority; the
Windows app reads and writes them the same way (`MightyClaude.Core/Models.cs`,
`StateStore.cs`).

A child record in that side file (`delegation-state.json`) may carry
`startedByHuman: true`, for a child the human started from the composer's second
send (⌘Enter on the Mac, Ctrl+Enter on Windows, on a busy Claude, Codex or
Antigravity pane, with delegation on — on by default in both apps; the Mac's
defaults key `delegation.enabled` false, or the Windows REG_DWORD
`delegation.enabled` 0 under `HKCU\Software\MightyClaudeNative`, turns it off).
Such a child
sends its parent no notices, the parent's delegation tools neither list it nor
act on it (merge and follow_up refuse with reason code `human_started`), and it
counts toward its own cap of 3 open children per parent, apart from the 3 the
parent's `delegate` may make; records without the key are agent-made and load
unchanged. The phone wire is unchanged: `POST /m1/sessions/{id}/submit` with mode
`steer` still steers into a running Claude pane, `queue` still queues.

In both apps, a child runs its parent's agent by default, and a record may carry
`provider` (`codex` or `gemini`), the child's own agent; records without it are
Claude children and load unchanged, and a Claude child's record never stores it.
In both apps, a child may run another agent than its parent's: `delegate`
takes an optional `agent` (`claude`, `codex`, `antigravity`, or `gemini` for
Antigravity) and refuses with reason code `agent_unknown` or `agent_unavailable`
(the CLI the app last found not installed or signed out), and the composer's
worktree button has a menu of the three agents (a right-click on Windows). A child of another agent may start only in
a mode that may do nothing the parent's mode may not (edit files after asking,
edit files without asking, run shell commands without asking, have full access;
editing without asking covers editing after asking). Everything that runs the child (its mode, its commits) follows the
child's agent; notices and merges follow the parent's. The capability and widest-mode tables are `fixtures/delegation-modes.json` ([Delegation modes](#delegation-modes-fixturesdelegation-modesjson)). Claude panes, and Codex
panes in acceptEdits or fullAccess, get the delegation tools (a manual Codex run
refuses MCP calls it cannot ask about, and the app declines an onRequest run's
MCP prompts); Antigravity panes have none and make children only with the
composer's second send.
A Codex or Antigravity pane makes children only in a mode where the child can
change files (Codex acceptEdits, onRequest, fullAccess; Antigravity acceptEdits,
fullAccess). A Codex or Antigravity child's work is committed by the app on its
branch (`mighty: <child id> report r<n>`) when a run finishes normally with
REPORT.md changed, never while `git ls-files -u` lists paths, never outside the
child's own worktree, and never with new files that look like secrets or are
over 5 MB. A parent whose agent is not Claude may merge by tool only when its
run started in fullAccess, and is otherwise refused with reason code
`merge_human_only` (the human merges from the child's card). A Codex exec run
pre-approves mighty-terminal only in fullAccess (in acceptEdits only
`read_latest_output` and `open_url`) and mighty-delegation in acceptEdits and
fullAccess; onRequest asks the human.

## Mobile protocol (m1) over the relay

Phones reach the desktop through a relay (`relay/`, Node + `ws`) that both sides dial outbound; the relay only forwards ciphertext. The host keeps a control socket (`/ws?serverId=…&role=server&v=1&hostToken=…`) and opens one data socket per phone (`…&connectionId=…`, same `hostToken`); `serverId` is the lowercase hex SHA-256 of `hostToken`, which the relay checks on every host socket (4401 otherwise). On each data socket: plaintext `hello`/`ready` (X25519 keys + 16-byte nonces), HKDF-SHA256 (`mightyclaude-relay-v1`), then ChaCha20-Poly1305 frames `[12B nonce = direction ‖ 0,0,0 ‖ counter][ciphertext+tag]` with strictly increasing counters. The first encrypted message is `auth` carrying the pairing key; the host answers `auth_ok` or `auth_error`. Full text: `docs/relay.md`.

Requests travel as `{id, method, path, body?}` → `{id, status, body}`; the host also pushes `{type:"notify", scope, revision}` and answers `ping` with `pong`.

| Method | Path | Notes |
|---|---|---|
| GET | `/m1/info` | `{protocol, hostId, hostName, appVersion, platform}` |
| GET | `/m1/state?since=<rev>&wait=<0..10>` | Long-poll until the state revision passes `since`; `MobileState` |
| GET | `/m1/sessions/{id}?since=<rev>&wait=<0..10>` | Long-poll per session; `MobileSessionDetail` (last 80 entries, pending permissions as structured cards, queue, usage) |
| POST | `/m1/sessions/{id}/submit` | `{text}` → 202 `{accepted: started\|steered\|queued}`, 409 when blocked |
| POST | `/m1/sessions/{id}/stop` | `{protocol, stopped: true}` |
| POST | `/m1/sessions/{id}/permission` | `{requestId, runId, allow}` → `{protocol, ok: true}` |
| POST | `/m1/sessions/{id}/answers` | `{requestId, runId, answers: {question: {selectedOptions, customText?}}}` |
| POST | `/m1/workspaces/{id}/sessions` | `{kind, provider?}` → 201 `{sessionId}` |
| GET | `/m1/workspaces/{id}/files?path=<relative>` | Capability `files`. Read-only folder listing `{workspaceId, path, entries: [{name, relativePath, kind: folder\|file\|symlink-folder\|symlink-file, size?, modified?, noise}], truncated}`; Mac pane order, at most 2,000 entries |
| GET | `/m1/workspaces/{id}/file?path=<relative>` | Capability `files`. Read-only preview `{name, size, modified?, type: source\|markdown\|image\|unsupported, …}`: text as UTF-8 with `encoding`, `language`, `lineCount`, `truncated` (first 512 KiB); images as a JPEG/PNG thumbnail (≤ 2,048 px, ≤ 512 KiB, base64 `data`, `mime`, original `width`/`height`); otherwise `reason: binary\|notRegularFile\|tooLarge\|undecodable` |

Pairing string: `mightyclaude://pair?v=2&sid=<serverId>&pk=<base64url X25519 public key>&relay=<ws(s)://host[:port]>&key=<pairing key>&name=<host name>`. Keys live in `<data>/mobile-remote/` (`mobile-remote.key`, `relay-keypair.json`, `relay-host-token.json`, owner-only). Limits: 64 KiB body, 32 KiB text, 16 KiB request path (a longer one is answered 414 `{code: "badPath"}`, never dropped), 8 in-flight requests per phone, 32 phones, 10 s maximum wait.

The file routes reuse the Mac files pane's containment exactly (`WorkspaceFiles.list` / `openFile`: symlinks resolved and refused outside the root, `O_NOFOLLOW|O_NONBLOCK`, regular files only, `F_GETPATH` re-checked, reads through the descriptor); the phone's `path` is refused first when absolute or containing `..` (`outsideWorkspace`), or NUL, over 4,096 bytes, or with an empty or `.` component (`badPath`). Only workspaces the Mac has are served. Every reply body stays under 768 KiB (the relay drops frames over 1 MiB and downloads have no chunking): text is cut further and entries dropped, both marked `truncated`. Errors carry `{protocol, error, code}` with `code` one of `workspaceNotFound` (404), `notFound` (404), `outsideWorkspace` (403), `notReadable` (403), `notDirectory` (400), `badPath` (400, or 414 from the tunnel), `superseded` (409). A path under a folder that does not resolve inside the root (e.g. below a link that leaves it) is always `notFound`, whether or not anything is there; `outsideWorkspace` is only for an item directly in a folder inside the root. Folders are enumerated up to 20,000 names, then `truncated`. Image decodes (bitmap, pdf, svg) hold the one preview slot of their own — text previews and attachment submits never wait for it — and a newer preview from the same phone replaces one still waiting (409 `superseded`); a cancelled request stops between decode steps. Bitmaps over 100 million header pixels are `tooLarge` without decoding; svg/pdf sizes that are not finite, positive and at most 10,000,000 points are `undecodable`; an svg is only drawn when `FilePreviewClassifier.svgLoadsExternalContent` finds no `href`/`src`/`url(` other than `#…` or a non-svg `data:`, no `@import`, entity, identified DOCTYPE, `xml:base` or CSS escape (CoreSVG would otherwise read `file:`, absolute and relative paths), else `undecodable`. Wire types and caps: `MightyCore/Remote/MobileWorkspaceFiles.swift`; full text: `docs/mobile-remote.md` "파일". The phone's highlighter is a port of `SourceHighlighter`, both checked against `fixtures/source-highlight.json`. Wire types: `MightyCore/Remote/MobileRemoteModels.swift`; crypto: `RelayChannel.swift`.

## Providers

Structured `AgentActivity` may include `durationMs`, measured on the execution
host from the first observed tool start to its first terminal event. It is a
finite nonnegative number capped at 30 days, omitted when no matching start was
seen. It is shown for completed, failed or stopped tools, not for active tools or
whole-turn activity. Older clients and hosts can omit or ignore this optional
field. Invalid optional measurements are discarded without losing the tool result.

Graph nodes carry `kind: "main"`, `"agent"`, or `"task"`. A `task` node is a
backgrounded command (`run_in_background`) shown as a child block; its state
comes from the engine's task notification. Clients that predate `task` drop
such nodes during normalization without affecting the rest of the run.
Nodes may carry an optional `usage` object (`inputTokens`, `outputTokens`,
`cacheReadTokens`, `cacheCreationTokens`; non-negative integers) summed from
the engine's per-message `usage` on the execution host. Older clients ignore it.

`kind: "claude"` remains the compatible wire name for an AI pane. `provider`
selects `claude`, `codex`, or `gemini`; `kind: "shell"` starts a command. The
`gemini` id is kept on the wire and in saved panes: macOS and Windows run
Google's Antigravity CLI (`agy`; Windows also looks for `agy.exe` in
`%LOCALAPPDATA%\agy\bin`, where its PowerShell installer puts it) and label
the agent "Antigravity".
Prompts travel over stdin. CLI argument arrays are never assembled by shell
interpolation. Provider changes reset model, settings, and resume identity.

Model source labels distinguish installed-CLI metadata from fallback examples.
Unsupported effort, permission, turn, and budget settings are rejected by the
execution boundary.

The existing `mods/mighty-bridge` TypeScript hook is loaded inside Claude Code.
Its language does not require a Node.js server in either desktop client.

## Attachments

`StartRunRequest.attachments` is an optional array. Missing or null means no
attachments; an empty array is omitted when encoding requests. Each item contains `id`, `name`, `mediaType`, and
`dataBase64`. Client filesystem paths are never transmitted. Native providers
advertise `ProviderCapabilities.attachments: true`; absent means false, and the
client rejects attachment submission when the provider lacks support.

The limits are 8 files, 5 MiB per file, and 8 MiB total decoded bytes. Validation
bounds encoded data before decoding, checks bare filenames and MIME signatures,
and rejects malformed payloads. Supported media labels are PNG, JPEG, GIF, WebP,
PDF, plain text, and generic binary. These labels do not imply that every model
can understand every file format. Empty text is valid only for AI requests with
at least one attachment; shell requests cannot carry attachments.

Claude receives images and PDFs as structured stream-JSON content blocks.
Codex receives image paths through `--image`. Antigravity gets the single temporary
attachment directory through `--add-dir` and each copy's plain path in the prompt. Other files
are referenced by staged paths in the prompt. Files are staged under generated
names in a private per-run directory and removed after completion, cancellation,
or startup failure. Originals are untouched. Attachments must be sent again if
a later resumed turn needs to reread the file.

Composer attachment drafts live in memory and are excluded from workspace state.
Only the captured attachment IDs are removed after accepted submission, so files
added during startup remain in the draft. Upload requests use a 60-second timeout.

## Composer settings

The native clients add optional fields to `RunSettings`. Missing fields preserve
legacy state loading. Default-valued new fields are omitted when encoded.

| Field | Default | Accepted values / scope |
| --- | --- | --- |
| `permissionMode` | `manual` | `manual`, `plan` (Claude/Gemini), `acceptEdits`, `auto` (Claude only), `fullAccess` |
| `fastMode` | `false` | Codex only |
| `webSearch` | `default` | Codex: `default`, `disabled`, `cached`, `live` |
| `networkAccess` | `false` | Codex with `acceptEdits` only |

`ProviderCapabilities` advertises `fastMode`, `webSearch`, and `networkAccess`.
Absent capability fields decode as false. The client checks the advertised
capabilities before sending selected non-default settings; unsupported settings
must not silently become a different execution policy.

Codex uses `approval_policy="never"` because the current executor cannot answer
interactive approval requests. Its permission modes map to `read-only`,
`workspace-write`, and `danger-full-access`. The workspace sandbox's outbound
network flag is separate from the model's web search setting. `fullAccess` removes
that sandbox restriction; it is not a default. Claude maps `fullAccess` to
`bypassPermissions` and Antigravity to
`--dangerously-skip-permissions`. Their `manual` modes retain CLI permission rules
and do not promise a read-only filesystem sandbox. Antigravity runs headless
(`agy --print= --input-format stream-json --output-format stream-json`, the prompt
as one `{"event":"user",…}` stdin line): `plan` and `acceptEdits` map to
`--mode plan` / `--mode accept-edits`. agy 1.3.5 has no `--mode default`
(it warns and ignores it), so `manual` follows the Agent Mode saved in agy (review
unless changed there); in review mode what it would ask about (file edits, shell
commands) is declined unless its own `permissions.allow` rules allow it, and the
result's `denied_actions` are shown as a notice. Headless `plan` writes its plan in
agy's own folder and stops without editing the project. agy expands skills sent
as `/name`; its read-only commands (`/help`, `/skills`, …) answer only in their own
`agy -p "/help"` run and end with an error result when sent over stdin. Token
usage is per conversation; agy's `input_tokens` excludes `cache_read_tokens`, so
both apps add them to keep input inclusive of cached input. Once per profile
(`antigravityMigrated` in the Mac and Windows snapshots), `gemini` panes drop the resume id and
any model Gemini CLI saved that agy does not list; a model agy's live list lacks is
reset to `default` before a run, since agy refuses an unknown `--model`.

An Antigravity pane has the Mighty views on macOS and Windows (the `gemini` id joins
`MightyGraphSupport.providers`). The
graph comes from agy's stream: each `agent_response` step's usage is summed on the
request block (`input_tokens` without `cache_read_tokens`, which is its own field),
the latest streamed reply, trimmed, is both the transcript's message and the
request's answer, and a `subagent` step (`invoke_subagent`, `subagent_info.subagents`)
is one tool row plus one `agent` node per sub-agent conversation, titled
`role (type_name)` with `initial_prompt` as its input. The stream says nothing
of a sub-agent's work, so the host reads the sub-agent's own
`~/.gemini/antigravity-cli/brain/<conversation>/.system_generated/logs/transcript_full.jsonl`
(`%USERPROFILE%\.gemini\antigravity-cli\…` on Windows) about once a second while the run lasts and once more when it ends
(`AntigravitySubagentWatcher`): instructions from its parent (SYSTEM_MESSAGE
`sender=<parent> … content=`) are its input and later assignments, PLANNER_RESPONSE
text and tool calls are its entries and usage, the GENERIC steps that follow
settle those calls (output kept for commands and failures only; file contents
are never copied), a `send_message` to the parent is its answer, and a nested
`invoke_subagent` adds a child under it. A sub-agent is not done while a helper
it started has not reported back; a helper's message continues its assignment
(completed → running without a new generation). Statuses other than DONE and
ERROR (CANCELED, …) end a step as stopped. Older requests load from the
conversation's own `transcript_full.jsonl` (a USER_INPUT step's `<USER_REQUEST>`
opens a request; its steps are replayed as the stream events agy printed, and
its sub-agents' transcripts are read the same way, only up to when the next
request was sent and within one byte budget per chunk). The resume list reads
`conversation_summaries.db` read-only (SQLite, `mode=ro`, or `immutable=1` while
agy is not running; newest rows first; Windows reads it through the system's own
`winsqlite3.dll`): rows whose `workspace_uris` include the workspace and whose
`parent_conversation_id` is empty, with a transcript; the title is the first
request. Continuing one passes `--conversation <id>`. The composer's slash list
offers agy's built-in skills (`builtin/skills`), the user's
(`~/.gemini/antigravity-cli/skills`) and the workspace's `.agents/skills`.

The local Claude runners (macOS, and Windows where the app shows the approval
bar) explicitly opt into the SDK stdio approval channel. Ephemeral `permission` events carry the run/request/tool IDs and complete
bounded input for an allow-once or deny response. These requests are not saved in
workspace state. The runner defaults this channel off; metadata probes continue
using `--permission-prompts none`.
No persistent permission rule is created by an app approval; the only mode change
an answer carries is the session-only `setMode` of an approved plan (below).

Claude `auto` maps directly to `--permission-mode auto`, retaining the local
stdio approval channel. Runtime advertisements include it only after discovery
of a Claude version supported by this app. Schema loading preserves saved Auto
settings independently of discovery. A runtime that does not advertise Auto
rejects it before a request starts; there is no silent fallback to Bypass.
The CLI owns account, model and administrator eligibility and may require manual
approval under its policy. Existing `manual` defaults are unchanged.

Codex Fast on explicitly sets `features.fast_mode=true` and `service_tier="fast"`;
off sets `features.fast_mode=false` and `service_tier="default"`. This prevents a
global Fast preference from contradicting the native toggle. Model/account support
and provider billing still apply. These are per-run overrides, not edits to the
user's global CLI configuration.

### Saved execution time (macOS)

Mac snapshots may include `autoUpdateCLIs`, an optional Boolean. Only explicit
`true` enables background CLI updates: at application startup, then every six
hours and after a provider that was busy has been idle for three minutes; absent,
null or malformed values leave this opt-in disabled. Mac snapshots may also include
`autoUpdatePlugins`, an optional Boolean; anything but explicit `false` keeps
background plugin updates on (user-scope Claude plugins without accepting a changed
marketplace command, and Codex Git marketplaces) while CLI updates are on. Both
preferences are local to the Mac app profile, and an update applies from the next
request. Mac and Windows snapshots may also include `autoLoginCLIs`, an optional
Boolean; anything but explicit `false` keeps the automatic sign-in on: a run that
lost its Claude or Codex sign-in starts one background sign-in per provider (Antigravity,
which has no sign-in command, opens one sign-in terminal running `agy` instead — a
terminal pane on macOS, a separate console window on Windows — only when `agy models`
reports signed out, and counts as signed in once `agy models` succeeds), and
none again for two minutes after one failed, was cancelled or succeeded. A request
that login recovery itself resent, or that started before the last sign-in, only
shows the card.

`RunSession.runTiming` is optional saved metadata. It contains ISO 8601 strings
`startedAt`, `lastObservedAt`, optional `finishedAt`, and boolean `isApproximate`.
Missing or damaged timing does not discard the session or its conversation.
Whole-run status events control this clock across providers. Individual tool completion does not finish it. macOS saves a checkpoint
while a request runs; after a restart, an interrupted request freezes at the
checkpoint instead of adding application downtime. Legacy conversation timestamps
may provide an explicitly approximate duration. Older clients can ignore this field.

### References

The composer puts model, effort, permission, and speed controls below the editor,
with secondary settings in a popover and a trailing send button. It follows the
interaction patterns described or shown in these official references:

- [Codex features](https://developers.openai.com/codex/app/features/): progressive disclosure for composer actions.
- [Claude model and effort controls](https://support.claude.com/en/articles/8664678-change-the-model-effort-and-thinking-settings): selectors beside the send button.
- [Paseo product preview](https://paseo.sh/): compact model, reasoning, and permission row.
- [Codex speed](https://learn.chatgpt.com/docs/agent-configuration/speed): Fast is separate from reasoning effort and increases usage.
- [Codex configuration](https://learn.chatgpt.com/docs/config-file/config-reference): service tier, web search, and sandbox network settings.

CLI argument availability was also checked against installed Claude Code 2.1.273,
Codex CLI 0.153.4, and Gemini CLI 0.43.0 help output. No AI prompt was sent for these checks.
The macOS Antigravity arguments and its stream-json events were checked against
Antigravity CLI 1.3.5 (`agy --help` and small `gemini-3.8-flash-low` runs).

### Windows structured events and saved measurements

Windows Core accepts the same optional `LogEntry.activity`, `RunEvent.activity`,
`RunEvent.usage`, `RunSession.runTiming`, and `RunSession.sessionUsage` fields as
macOS. Tool rows update by stable run-scoped IDs. `durationMs` measures each tool
on the execution host; only the final run status stops the session clock.
Windows saves timing checkpoints with state updates and freezes interrupted runs
at their last checkpoint when restoring a profile. Damaged optional measurements
do not discard otherwise valid saved conversation text.

`SessionUsage` distinguishes `response`, `run`, and `session` token scopes.
Input includes cached input, while cache and reasoning counts are subsets.
Claude's authenticated Mods snapshots can supply actual context occupancy and
capacity. Codex/Gemini/Antigravity cumulative result counters replace prior snapshots and do
not imply current context size or a context percentage. Quota observations retain
their separate `rateLimitsUpdatedAt`; ordinary output does not make them fresh.

Windows Core launches Claude with `--permission-prompts host --permission-prompt-tool stdio`
only when the local approval bar is present (`RunManager`'s `permissionRequested`);
headless paths, such as metadata probes, keep `--permission-prompts none`. Saved
permission settings never imply blanket approval.

Snapshots also retain optional `paneLayoutModes` and `paneLayoutActiveSessionIds`
dictionaries, keyed by workspace ID, so each workspace keeps its display mode
and selected tab independently.

The macOS files pane (`kind: "files"`, id `files:<workspaceId>`) is local to a
running app: snapshots never store it and the relay pane list never carries it
(docs/file-pane.md).

## Next actions (`fixtures/next-actions.json`)

Ouroboros replies end with a breadcrumb `◆ <state> → next: <actions>`. When the
last assistant entry of a finished turn (no user entry after it, pane not
running) carries one, both clients draw its options as buttons: the Mac under
the transcript or graph, the phone under that reply in the log view and docked
above the composer in the Mighty blocks view. A tap fills the composer and never
sends. Parsers: `MightyCore/NextActions.swift`, `MightyClaude.Core/NextActions.cs`
and `mobile/src/lib/next-actions.ts`. Windows uses native buttons above its
composer and preserves the existing TextBox, draft and undo history.

- Both parsers work on Unicode scalars (the phone's code points) and match
  literally, with no normalization or canonical equivalence: a decomposed (NFD)
  `또는` is not a separator and is not stripped. "Whitespace" is exactly
  space, tab, CR, LF and U+3000; trimming removes only those (NEL, BOM and
  NBSP stay in the text).
- The text is split on LF. The line is the last one whose trimmed text starts
  with `◆` and contains `→ next:` or `-> next:`; the options are the trimmed
  text after the first marker.
- Alternatives split on `, 또는 `, `, or `, ` 또는 `, ` or ` (ASCII case-insensitive),
  never inside a backtick code span or parentheses. ` — ` splits only when
  `or `/`또는 ` follows it (that word goes with the separator); any other
  ` — ` and the text after it stay in the current option as a note.
- Commas are ordered steps within one option, unless the whole list ends with
  ` 중 선택`, ` 중에서 선택` or ` 중 하나`: that suffix is dropped and top-level
  commas split too.
- Each option is trimmed and loses a leading `또는`/`or` that whitespace follows;
  empty ones are dropped. `label` is the option text. `fill` is the first code
  span that is a command (trimmed, `ooo` or `/name[:sub]`, then a space, a tab
  or the end), else a code span that is the whole option (trimmed), else the
  label. An option whose fill is blank (a whitespace-only code span) is dropped,
  then at most 4 are kept.
- Buttons show and read the label without backticks (the label itself if that
  would leave it blank); the visible text may be truncated.
- A fill never loses the draft: a blank draft (after trimming) is replaced by
  the fill, anything else keeps its text and gets the fill on a new line after
  it (no separator is added when the draft already ends with `\n`), caret at
  the end. On the Mac this is one undoable edit, and a Korean
  composition in progress is committed first.

`fixtures/next-actions.json` is `[{line, options: [{label, fill}]}]` with real
breadcrumbs plus made-up edge cases (NFD `또는`, trailing NEL/BOM, a combining
mark after ` or `, CRLF, U+3000, a blank code span); `NextActionsTests` (Swift)
`NextActionsVerification` (Windows Core) and `next-actions.test.ts` (jest) all
read it and must agree with every case.

## Claude plan mode (`fixtures/claude-plan-mode.json`)

Claude Code ends plan mode with an `ExitPlanMode` `can_use_tool` request on the
stdio approval channel; its `input` carries the Markdown `plan` (and
`planFilePath`), both injected by the CLI from its plan file. The request keeps
`canAllow: false` (the CLI marks it `requires_user_interaction`) and gains
`canAnswerPlan` and `receivedAt`; `PlanApprovalRequest` is the typed view of a
pending one. Its four answers are `control_response` bodies checked against the
2.1.289 bundle's SDK permission-result schema:

- approve (auto-edit / confirm-each): `{behavior: "allow", updatedInput: {},
  updatedPermissions: [{type: "setMode", mode, destination: "session"}],
  toolUseID}`, as the CLI's own "Yes" choices send it (an empty `updatedInput`
  keeps the model's input; an echoed `plan` would count as a plan the user
  edited). `mode` is the button's (`acceptEdits` / `default`) for a pane stored in
  `plan`; for a pane stored in another mode (Claude planned on its own) it never
  goes below that mode's CLI form: manual `default` < `acceptEdits` < `auto` <
  fullAccess `bypassPermissions`. `destination: "session"` is never written to
  settings files.
- revise: `{behavior: "deny", message: "<fixed English lead-in>\n<feedback>",
  toolUseID}`; Claude stays in plan mode and calls ExitPlanMode again.
- cancel: `{behavior: "deny", message, interrupt: true, toolUseID}`; the run
  ends `stopped`.

A plan whose input passes the 64 KiB display bound is denied with a request for
a shorter plan. Every settled plan (answered, denied from a generic card, too
long, withdrawn by the CLI or still open when the run ends; all but answers count
as cancelled) becomes a `PlanRecord` event (`type: "plan"`), sent after the reply
is written. Panes keep the last 10 in `RunSession.planHistory` (plan text up to
32 KiB, feedback up to 4 KiB, and 96 KiB in total: newest records stay whole,
older ones keep their first 8 lines and are marked `planTruncated`; a damaged
saved record is dropped alone). An approval sets the pane's own
`settings.permissionMode` to `acceptEdits` or `manual` only when the pane was
stored in `plan`. `StartRunRequest.permissionModeOverride` (`plan` only, Claude
only, never saved or taken from the phone) starts one request in plan mode
without touching the stored mode, for a guided plan style.

`type: "todos"` carries `TodoProgress` (saved as `RunSession.todoProgress`):
the main agent's TodoWrite list, or the same list built from TaskCreate (id from
`tool_use_result.task.id` or the `Task #N created` text) and successful
TaskUpdate calls (the CLI's own reader: input aliases, updates applied as called,
an unknown id added with its subject or id). A resumed run continues the pane's
saved list. Sub-agent frames are ignored.

`type: "background"` carries `BackgroundWork` (saved as
`RunSession.backgroundWork`, cleared when a run starts): tasks from the CLI's
`system` events `task_started`, `task_updated`, `task_progress`,
`task_notification` and `background_tasks_changed`, with `turnEnded` once the
request's own `result` arrived (`waitingOnBackground`: turn done, tasks still
running). Foreground tasks (`is_backgrounded: false`) and `ambient` ones stay out
until moved to the background; a `task_started` arriving after the level event
fills in the tool id and kind. A task still running when the process ends, or
when a saved state is restored, is `unknown`.

While such tasks run after the request's result, the runner keeps stdin and the
approval channel open (with `CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS=1`), so
follow-up turns can still ask and new input joins the same process. It closes
input at the next top-level `result` or `session_state_changed: idle` with no
task running, or after 120 s of silence once the last task ended; the CLI's own
background-wait ceiling is left at its default. Stopping the pane still ends the
whole process group.

The desktop plan card (Mac `PlanApprovalCard`, Windows `MainWindow.PlanCard.cs`)
and the phone's `PlanCard` read `PlanCardSupport` (both Cores): the first pending
`canAnswerPlan` request is the card; the Mighty diagram draws it as a `plan` node
where its unfinished request's result will go (else it docks above the composer),
and an empty or over-long change request is refused before it is sent (an
over-long one says `plan.error.feedbackTooLong` as soon as it is typed). The
diagram node is in the shared layout vectors (`graph-vectors.json`
`pending-plan-takes-the-result-place`).

Plan history follows one rule on both desktops: in the Mighty diagram each
`PlanRecord` is a `planRecord` block attached beside the request whose graph run
it names (`PlanRecord.graphRunId`, the run's `MightyGraphRun.sourceRunID`, set by
the runner: the activity id on macOS, the graph tracker's id on Windows, where
`runId` is the pane's id); a record whose run is not drawn has no block. Outside
the diagram (the default view and the timeline) the records show as one folded
strip above the composer (`PlanCardSupport.showsHistoryStrip`).

The phone receives the plan as `MobilePermission.plan` (+ `receivedAt`) and
answers with `POST /m1/sessions/{id}/plan` (`docs/mobile-remote.md`). The plan is
also the permission's one field (label "계획"/"Plan"), so a phone that does not
know `plan` still shows the text it is asked to approve.

The fixture holds request recognition, exact response bodies, checklist frames
and background-event sequences; `ClaudePlanModeTests` (Swift) and
`ClaudePlanModeVerification` (Windows Core) both read it.

## Delegation modes (`fixtures/delegation-modes.json`)

A child of another agent than its parent's may start only in a mode that may do
nothing the parent's mode may not. A mode is described by four capabilities:
`writesAsking` (changes files after the human approves each change),
`writesUnasked` (changes files without asking, sandboxed or not), `runsUnasked`
(runs shell commands without asking, sandboxed or not) and `fullAccess` (no
sandbox and no asking). A child's mode fits under a parent's when each of its
capabilities is the parent's too, except that the parent's `writesUnasked` covers
the child's `writesAsking`. Within one agent, modes still compare by their own
order (`plan < manual < acceptEdits < onRequest < auto < fullAccess`, each agent
using the ones it has). The fixture is `{agents, capabilities: {agent: {mode:
[capability]}}, widestChildMode: {parentAgent: {parentMode: {childAgent: mode |
null}}}}` over the provider ids `claude`, `codex` and `gemini` (Antigravity).
`widestChildMode` is the mode the worktree button's menu starts a child of that
agent in: the widest that fits and in which the child can change files (the most
capabilities, then the later in its own order; for the parent's own agent, the
parent's mode whenever its child can write in it), or null when none fits.
`DelegationModesFixtureTests` (Swift) and `DelegationCrossAgentVerification`
(Windows Core) read it. Rules: `MightyCore/DelegationProviders.swift`,
`MightyClaude.Core/DelegationProviders.cs`.

## Model labels (`fixtures/model-labels.json`)

Every place a model is chosen or shown labels it with its family and version:
`claude-opus-5-5` → `Opus 5.5`, `claude-haiku-4-5-20251001` → `Haiku 4.5`,
`gpt-5.2-codex` → `GPT-5.2 Codex`, `gemini-3-pro-preview` → `Gemini 3 Pro`. Only the
label changes; the value saved and sent to the CLI is untouched. An alias
(`opus`) takes its version from the id it resolves to — the CLI catalogue's
`resolvedModel`, or, for a family alias only, the model the pane last reported
while that same alias was selected (`SessionUsage.selectedModel`, optional,
absent in older saves) — and stays the bare family name (`Opus`) when nothing
says which version it is. Other aliases (`default`, `opusplan`, `best`) keep their
own name and add the resolved model after ` · `. `[1m]` adds ` (1M)`; a Bedrock
`-v1:0` is dropped, any other trailing `v2` is kept. Formatters: `MightyCore/ModelLabel.swift` and
`mobile/src/lib/model-label.ts`; `ModelLabelTests` (Swift) and
`model-label.test.ts` (jest) both read the fixture
`[{model, resolved?, fallback?, expected}]`. The graph vectors below keep the
catalogue-only names (`ModelUsageFormat` with `versioned: false`); the Mac views
pass `versioned: true`. macOS, Windows (`MightyClaude.Core/ModelLabel.cs`) and the phone
read an Antigravity slug's last `low`/`medium`/`high` as its effort
(`gemini-3.8-flash-low` → `Gemini 3.8 Flash (Low)`, as `agy models` names it), and the
phone names an Antigravity pane's Claude and GPT-OSS slugs as the Mac's catalogue does
(`claude-opus-5-5-high` → `Claude Opus 5.5 (High)`, `gpt-oss-120b-medium` →
`GPT-OSS 120B (Medium)`); these ids are tested per client, not in the shared fixture.

## Help site (`fixtures/help-site.json`)

Every app opens the user guide `scripts/help/build.mjs` publishes at
`<base>/<lang>/`, where `<lang>` is the app's resolved display language (`ko`,
`en`, `zh` or `ja`, never the raw "system" preference). The Mac opens it from
Help → Mighty Claude 도움말 (⌘?) and Settings → 앱 정보, Windows from the
sidebar footer's help symbol and F1, the phone from the hosts tab. The fixture is
`{base, cases: [{language, url}]}`; `HelpSiteTests` (Swift),
`HelpSiteVerification` (Windows Core) and `help-site.test.ts` (jest) all read it.
Builders: `MightyCore/HelpSite.swift`, `MightyClaude.Core/HelpSite.cs`,
`mobile/src/lib/help-site.ts`.

## Execution graph vectors (`graph-vectors.json`)

`graph-vectors.json` is the shared execution-graph contract: one file that both
platforms read, in the same pattern as `toolkit-hash-parity.json`. Every case
carries authored `inputs` and the `expected` macOS output. The groups, and the
minimum number of cases each must hold, are:

| Group | Minimum | What one case describes |
| --- | --- | --- |
| `claudeStream` | 6 | Ordered Claude stream-json frames (plus `steer`/`finish` steps) driving the tracker |
| `codexStream` | 4 | Ordered `codex exec --json` items driving the tracker |
| `mods` | 3 | Mods `agent.spawn` / `turn.complete` metadata and tool activity |
| `bounds` | 3 | Saved runs through `MightyGraphSupport.normalized` / `boundedLiveHistory` |
| `layout` | 8 | Runs, draft, running, expanded and viewport through `MightyGraphLayout.make` |
| `camera` | 6 | One `MightyGraphCamera` (or `cameraOffset`) rule with its arguments |
| `capsule` | 8 | One model/usage/title formatting rule with its arguments |
| `resultFiles` | 4 | Final result texts plus a listed workspace tree |

A tracker case is `{ runId, input, provider, configuredModel, steps[] }`; a step
is `frame`, `mod`, `steer`, `activity` or `finish`. Its expectation is the latest
snapshot of every emitted node, in first-emission order. Wall-clock fields
(`updatedAt`, entry `timestamp`) are never part of an expectation. Frames compare
within 0.001 and result paths compare with forward slashes.

Expectations are never written by hand. `GraphParityVectorTests` (macOS) derives
each one from the committed inputs by running the Swift implementation, so the
file is macOS truth. To regenerate after a deliberate behaviour change:

```
DEVELOPER_DIR=/Library/Developer/CommandLineTools MIGHTY_GRAPH_VECTORS_WRITE=1 \
  bash scripts/test-native-macos.sh --scratch-path /tmp/graph-vectors \
  --filter GraphParityVectorTests
```

That run rewrites every `expected` and leaves the authored inputs alone; review
the diff before committing. It is never part of verification — a normal test run
compares instead of writing.

Display text in the expectations is the Korean copy. The Windows port takes that
text from `locales/ko.json` / `en.json` and compares against these vectors with
the `ko` locale selected.

## Help capture demo profile (`fixtures/demo-profile.json`)

The demo state the macOS `--help-capture` mode loads to take the help site's
pictures (`docs/help/shots.json` lists the screens; CI runs the capture once per
language). Everything in it is fictional: three workspaces (`my-app`,
`docs-site`, `shop-api`) created under the capture's profile folder, their
files, and panes whose runs are the CLIs' own stream lines (`frames`: Claude
stream-json, Codex exec JSONL). `HelpDemoReplay` (`MightyCore/HelpCapture.swift`)
replays them through `CLIStreamParser` and the Claude permission channel, so the
diagram, plan, question and permission cards, checklist and background work are
what a real run produces. A JSON object whose keys are only language codes is
one user-visible string and carries all four languages; program output stays as
the program prints it. Both files are copied byte-identically into
`MightyCore/Resources/Help/` for the app bundle; `HelpCaptureTests` checks the
copies, the four languages and the replayed state.

`delegation` is the agent-delegation demo in `shop-api`: a parent pane with
three open children (reported, running, waiting on a permission), as many as
delegate allows a pane, a merged child whose pane was then closed (its card
still offers undo), and the child of a parent whose pane is closed, with the
notices held for them. Its workspace and panes are seeded when the first
delegation shot is taken (those shots come last, so no other picture shows
them); the open children open beside their parent as delegate opens them, and
`HelpDemoDelegation` turns the records into the delegation file the capture
loads into the app's coordinator. `HelpCaptureDelegationTests` checks the
records, the cap on open children, the sidebar tree, the cards and the held
notices they give.

## Setup catalog (`fixtures/setup-catalog.json`)

The tools the first-run setup screen (필요한 도구, also Settings → 도구 →
구성 요소) checks and installs: the agent CLIs, their prerequisites, the style
tools, Tailscale, gh and glab. It ships with the app and is read-only; nothing the
user edits (the toolkit file included) adds an item. Each item has an `id`, a
`group` (`agent`, `prerequisite`, `style`, `network`, `dev`), label and
description keys from the locale catalogs, `need` (`required` or `optional`;
required is only Git (the command line tools on a Mac), and the first-run rule also wants any
one working agent CLI), an
`afterInstall` action (`none`, `reprobe`, or `signIn` with a provider) and, per
platform (`macos`, `windows`), a `probe`, an `install` recipe, its
`prerequisites` (item IDs, acyclic) and the official `source` page the command
came from.

Both kinds are closed sets. Probes: `executable` (on the login shell's PATH,
optionally `minimumVersion` read from `--version`), `claudePlugin` (a user-scope
record in `~/.claude/plugins/installed_plugins.json` whose ID starts with
`prefix`), `skill` (`~/.claude/skills/<name>/SKILL.md`), `commandLineTools`
(`xcode-select -p`, then `/usr/bin/git --version`) and `appBundle` (`name` in
/Applications or ~/Applications, or `executable` on the PATH). Installs: `argv`
(only `xcode-select` with `--install` and `npx` with `--global`/`--agent`; any
other argument starting with `-` is refused; `finishesOutside` when a system
window completes it),
`officialScript` (an https URL on the allow-listed hosts, fetched and run with
`bash` or `sh`, PowerShell on Windows), `brewFormula`, `brewCask`, `npmGlobal`,
`claudePlugin` (marketplace + `name@marketplace`; not pinned, the marketplace
decides the version), `skill` (a repository pinned to a full `commit`, placed in
`~/.claude/skills/<folder>`, then an optional fixed script inside it),
`winget` (Windows only: an exact package `id` from the winget source and a
`scope` of `user` or `machine`; machine scope may show Windows' own
administrator prompt) and `vendorPage` (the app opens the page; nothing runs).
`commandLineTools`, `appBundle`, `brewFormula`, `brewCask` and `xcode-select`
are Mac only; the Windows decoder (`MightyClaude.Core/SetupCatalog.cs`) also
refuses a `skill` recipe under `windows`, as its setup is a POSIX shell script.
On Windows an `executable` probe also looks in the folders the official
installers use, with PATHEXT. URLs on github.com and
raw.githubusercontent.com, and a marketplace's owner/repo short form, must name
one of `SetupCatalog.allowedRepositories`. The decoder
(`MightyCore/SetupCatalog.swift`) refuses an unknown key, kind, host, program or
label key. The Mac copies the file byte-identically into
`MightyCore/Resources/Setup/` and `--verify-resources` checks it is there;
`SetupCatalogTests` checks the copy, the validation rules and the four languages.
The Windows app embeds the contract file itself and `SetupVerification` checks
the same rules there.

## Tailscale addresses (`fixtures/tailscale-addresses.json`)

The rule that decides which of this computer's addresses are Tailscale's, shared by the phone's direct path and the remote workspaces. An interface counts only if it carries an address in fd7a:115c:a1e0::/48, which only Tailscale hands out; its addresses in 100.64.0.0/10 and that range then count, each once in its canonical spelling without `%scope`, IPv4 first and each family sorted ordinally. A carrier-grade NAT can put 100.64.0.0/10 on Wi-Fi and other VPNs (Cloudflare WARP's 100.96.0.0/12) on their own tunnel, so the CGNAT range alone never counts. `contains` lists addresses in and out of the two ranges (brackets accepted, `%scope` refused, an IPv4-mapped IPv6 address counts as its IPv4). `cases` are interface lists with each platform's answer: the Mac (`darwin`, `MightyCore/Remote/TailscaleAddresses.swift`, `RemoteWorkspaceAddressFixtureTests`) also requires a `utun` name; Windows (`win32`, `MightyClaude.Core/Remote/TailscaleAddresses.cs`, `TailscaleAddressesVerification`) cannot, as adapters are renamable and the Wintun adapter's type is no named `NetworkInterfaceType`. The two answers differ only in the cases that say so. Every address in the file is fake.
