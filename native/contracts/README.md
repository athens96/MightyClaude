# Native client contracts

The Swift and C# clients implement the same version 1 JSON contract based on
the original `shared/types.ts`, with the optional native extensions below. They run independently without an Electron,
Node.js, or browser host. Provider CLIs retain their own installation/runtime
requirements.

## State

`workspace-state.json` remains version 1. Optional legacy `provider` and
`settings` fields default to Claude and automatic effort/manual permission.
Only `running` states become `stopped` during restore. A remote workspace keeps
the host's display path and `{connectionId, workspaceId, hostName}` reference;
it never becomes a locally executable path.

Native profiles are separate from the Electron profile. Initial migration reads
the old snapshot and saves a copy; it never overwrites the old file. Secret
connection keys do not appear in snapshots. Old Electron-encrypted keys require
re-entry because the native applications use Keychain/DPAPI directly.

## Remote protocol

- Transport: HTTP over a Tailscale interface, default port `43137`.
- Every request: `Authorization: Bearer <key>` and
  `x-mighty-remote-version: 1`. Responses include the same version header.
- Keys: 32 cryptographically random bytes encoded as unpadded base64url,
  rotated every time sharing starts. Sharing is off after app startup.
- Host: only explicitly selected, registered local workspaces.
- Client: verify every resolved address belongs to the connected tailnet,
  pin the resolved IP, disable proxies and redirects, bound responses.
- Limits: request body 512 KiB by default; authenticated `/v1/runs` uploads
  allow 12 MiB. Response 2 MiB, 16 concurrent jobs,
  per-job output ring 256 events/512 KiB, 20-second client polling lease.
- Loopback exceptions exist only as constructor options in native test code.

| Method | Route | JSON |
| --- | --- | --- |
| GET | `/v1/info` | `{protocol:1, hostId, hostName, workspaces, runtime}` |
| POST | `/v1/runs` | Request `{request: StartRunRequest}`; response HTTP 202 `{protocol:1, jobId}` |
| GET | `/v1/runs/{jobId}/events?cursor=0` | `{protocol:1, cursor, lastCursor, gap, done, events:[{cursor,event}]}` |
| POST | `/v1/runs/{jobId}/stop` | Stops the host process and its descendants |

The host generates `jobId` independently of any client pane ID. Poll events
contain that job ID; the client remaps it to its local pane ID. Disconnecting
stops the client's jobs. Reconnecting requires an explicit user operation.

## Providers

Structured `AgentActivity` may include `durationMs`, measured on the execution
host from the first observed tool start to its first terminal event. It is a
finite nonnegative number capped at 30 days, omitted when no matching start was
seen. It is shown for completed, failed or stopped tools, not for active tools or
whole-turn activity. Older clients and hosts can omit or ignore this optional
field. Invalid optional measurements are discarded without losing the tool result.

`kind: "claude"` remains the compatible wire name for an AI pane. `provider`
selects `claude`, `codex`, or `gemini`; `kind: "shell"` starts a command.
Prompts travel over stdin. CLI argument arrays are never assembled by shell
interpolation. Provider changes reset model, settings, and resume identity.

Model source labels distinguish installed-CLI metadata from fallback examples.
Unsupported effort, permission, turn, and budget settings are rejected by the
execution boundary, including requests received from a remote client.

The existing `mods/mighty-bridge` TypeScript hook is loaded inside Claude Code.
Its language does not require a Node.js server in either desktop client.

## Attachments

`StartRunRequest.attachments` is an optional array. Missing or null means no
attachments; an empty array is omitted when encoding requests to preserve legacy
version 1 host compatibility. Each item contains `id`, `name`, `mediaType`, and
`dataBase64`. Client filesystem paths are never transmitted. Native providers
advertise `ProviderCapabilities.attachments: true`; absent means false, and the
client rejects attachment submission before POST when the host lacks support.

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
legacy state loading. Default-valued new fields are omitted when encoded so that
older version 1 hosts with strict field validation can still accept basic runs.

| Field | Default | Accepted values / scope |
| --- | --- | --- |
| `permissionMode` | `manual` | `manual`, `plan` (Claude/Gemini), `acceptEdits`, `auto` (Claude only), `fullAccess` |
| `fastMode` | `false` | Codex only |
| `webSearch` | `default` | Codex: `default`, `disabled`, `cached`, `live` |
| `networkAccess` | `false` | Codex with `acceptEdits` only |

`ProviderCapabilities` advertises `fastMode`, `webSearch`, and `networkAccess`.
Absent capability fields decode as false. A remote client checks the host's
advertised capabilities before sending selected non-default settings; unsupported
settings must not silently become a different execution policy.

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
workspace state or sent by remote hosts. The runner defaults this channel off;
remote Claude and metadata probes continue using `--permission-prompts none`.
No persistent permission rule is created by an app approval.

Claude `auto` maps directly to `--permission-mode auto`, retaining the local
stdio approval channel. Runtime advertisements include it only after discovery
of a Claude version supported by this app. Schema loading preserves saved Auto
settings independently of discovery. Old remote hosts that do not advertise Auto
reject it before a request is posted; there is no silent fallback to Bypass.
The CLI owns account, model and administrator eligibility and may require manual
approval under its policy. Existing `manual` defaults are unchanged.

Codex Fast on explicitly sets `features.fast_mode=true` and `service_tier="fast"`;
off sets `features.fast_mode=false` and `service_tier="default"`. This prevents a
global Fast preference from contradicting the native toggle. Model/account support
and provider billing still apply. These are per-run overrides, not edits to the
user's global CLI configuration.

### Saved execution time (macOS)

Mac snapshots may include `autoUpdateCLIs`, an optional Boolean. Only explicit
`true` enables CLI updates once at application startup; absent, null or malformed
values leave this opt-in disabled. This preference is local to the Mac app profile
and is not sent as part of remote run requests.

`RunSession.runTiming` is optional saved metadata. It contains ISO 8601 strings
`startedAt`, `lastObservedAt`, optional `finishedAt`, and boolean `isApproximate`.
Missing or damaged timing does not discard the session or its conversation.
Whole-run status events control this clock across providers, including remote
requests. Individual tool completion does not finish it. macOS saves a checkpoint
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

Remote event polls opt in with `x-mighty-activity: 1` and `x-mighty-usage: 1`.
Without those headers, hosts preserve event cursors and final status while
degrading the new transient types for original version 1 clients. Windows does
not advertise graph or permission-response support. Its Claude runner retains
`--permission-prompts none`; interactive allow-once approval remains local macOS
functionality, and saved permission settings never imply blanket approval.

Snapshots also retain optional `paneLayoutModes` and `paneLayoutActiveSessionIds`
dictionaries, keyed by workspace ID, so each workspace keeps its display mode
and selected tab independently.
