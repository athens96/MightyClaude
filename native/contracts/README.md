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
selects `claude`, `codex`, or `gemini`; `kind: "shell"` starts a command.
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
Codex receives image paths through `--image`. Gemini receives platform-escaped `@file`
references and access to the single temporary attachment directory. Other files
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
`bypassPermissions`, and Gemini maps it to `yolo`. Their `manual` modes retain CLI
permission rules and do not promise a read-only filesystem sandbox.

The macOS local Claude runner explicitly opts into the SDK stdio approval
channel. Ephemeral `permission` events carry the run/request/tool IDs and complete
bounded input for an allow-once or deny response. These requests are not saved in
workspace state. The runner defaults this channel off; metadata probes continue
using `--permission-prompts none`.
No persistent permission rule is created by an app approval.

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
request.

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
capacity. Codex/Gemini cumulative result counters replace prior snapshots and do
not imply current context size or a context percentage. Quota observations retain
their separate `rateLimitsUpdatedAt`; ordinary output does not make them fresh.

Windows does not advertise graph or permission-response support. Its Claude runner retains
`--permission-prompts none`; interactive allow-once approval remains local macOS
functionality, and saved permission settings never imply blanket approval.

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
sends. Parsers: `MightyCore/NextActions.swift` and `mobile/src/lib/next-actions.ts`.

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
and `next-actions.test.ts` (jest) both read it and must agree with every case.

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
