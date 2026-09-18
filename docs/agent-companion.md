# Agent activity and desktop companion (macOS)

The native Mac client renders Foundation-parsed Markdown in one read-only AppKit NSTextView. Headings, lists, checklists, quotes, native tables, inline styles and fenced code keep their structure. Drag selection and copy cross paragraph, tool and message boundaries. Selection anchors follow entry IDs through streaming updates and log pruning. Code can be copied. HTTP/HTTPS/mailto links open on click; message images do not trigger automatic network requests.

An existing conversation opens at its latest message after the native viewport has finished its first layout. Scrolling up or selecting text keeps that reading position through later output updates.

Tool rows show an SF Symbol and the actual command, path or query. Clicking the disclosure shows bounded output. Older unstructured entries remain readable. Workspace and session rows show running AI requests separately from open shells.

Completed tool rows also show their elapsed duration beside the summary (for example `250ms`, `1.2초` or `2분 3초`). The execution host measures from the first observed start/wait event to the first terminal event using a monotonic clock; repeated events do not reset it. The measurement includes permission waiting, survives saved history and travels with remote activity. A result without an observed start, including old history, does not invent a duration. The request timer in the pane and pet remains separate.

## Live activity

Claude uses the bundled `mighty-bridge` Mods observation hooks and stream-json events. Codex and Gemini use their CLI JSON event streams. No provider transcript/log files are scanned for pet state. Tool events have stable IDs so a result updates its existing row, and late starts cannot revive completed tools. Whole-run completion comes from the child process lifecycle, not an individual tool or model turn.

The remote protocol negotiates optional structured activity with `x-mighty-activity: 1`. Older clients keep the original event types. Remote hosts must be updated to expose detailed activity; regular running/completed state still works without it.

A waiting indication means an explicit provider event (for example a Claude permission check or AskUserQuestion), not an inferred idle timeout. Local Claude runs now expose one-use tool approval cards. Remote runs and other providers keep their existing noninteractive execution permission settings.

## Pet and status UI

- Bottom right paw: toggle desktop pet.
- Bottom right status button: open the agent list, current actions and jump to an agent.
- Click the pet to show or hide its task bubble. Click the bubble (or the pet's Open agent context-menu item) to activate MightyClaude and select that agent, including its workspace and dock tab.
- Drag the pet itself to move its window. Moving right or left plays the matching walking animation; releasing it restores the animation for its current task. A drag does not toggle the bubble. Both the pet setting and macOS Reduce Motion can keep frames static.
- Task animations follow the same structured activity and lifecycle as the bubble: reading/searching/reviewing, working/thinking, waiting, failure, and completed celebration. Legacy summaries are only a fallback for choosing a working animation; they never manufacture a waiting or failure state.
- A completed request's bubble hides after six seconds; the pet remains visible. A deliberate click cancels the hiding deadline. The next request opens the bubble again; ordinary tool updates preserve the user's hidden/shown choice.
- The bubble's header shows the workspace name above the agent title (and the approval card shows "workspace · agent"), so with several agents running it is clear which one the pet is talking about. The completion notification carries the same "workspace · agent" label.
- With more than one agent running or waiting, a pager appears under the bubble: `‹ ● ○ ○ 2 / 3 ›`. The chevrons, a sideways drag on the bubble, or a two-finger horizontal swipe over the pet turn to the next busy agent (wrapping, in sidebar order). The agent the user turned to stays shown while it is busy; when it finishes, the pet goes back to choosing the most urgent agent itself. The approval card follows the page: it shows for the agent on screen, and when another agent's request is hidden behind the chosen page the pager shows an orange hand that jumps to it. Turning back to the agent the pet would have chosen anyway releases the pick. The swipe counts only over a bubble (not over the pet), once per gesture, and follows the system scroll direction.
- The bubble separates the latest submitted request from the current activity. Drafts are not shown. Requests come directly from submission events, with the app's own saved conversation as a restore fallback; external provider logs are not scanned.
- Agent headers, the status list and the pet show the current request's elapsed time. The clock starts on submission, includes preparation and permission waits, and freezes on whole-run completion, error or stop. Tool completion does not stop it. A new request resets the clock; switching workspaces does not. Timing is saved with the session and restored when the app reopens. Interrupted runs stop at their last saved observation, excluding time while the app was closed. Older sessions can recover an approximate duration from their last submitted request and subsequent response/activity timestamps; the UI marks these values with `약` (approximately).
- Settings → Pet and task notifications: choose a pet, import a Codex-compatible folder, pet.json or PNG/WebP sheet, toggle the task bubble/reduced animation/completion notifications.
- Installed `${CODEX_HOME:-~/.codex}/pets` packages are available for selection. Imports are copied into the MightyClaude profile's `pets` folder; the original Codex installation is not modified.
- Codex v1/v2/v3 sheets are accepted; the shared first nine animation rows are used. Frames are decoded only when needed and held in a bounded cache.
- When a local Claude pane is waiting for a tool approval, the bubble switches to an approval card: what the tool does (e.g. "명령 실행"), the model's description, the command or path in a code box, and **거부 / 이번만 허용** buttons that send the same one-time answer as the pane's own approval bar. A single-question, single-choice `AskUserQuestion` shows its options as buttons; multi-question, multi-select or free-text answers, and requests the app cannot allow, offer **열기** to jump to the pane. The panel grows upward while a request is pending and returns to its normal size afterwards.
- A successful run sends one native macOS completion notification. Click it to open that agent. macOS must allow notifications for MightyClaude. Stopped/failed runs do not send a success notification.

Preferences are in `companion-settings.json` next to the workspace state. The desktop panel is nonactivating and can be moved by dragging the pet. Its status bubble stays above other apps. It never includes a prompt or tool output in system notifications.

## References and assets

The interaction and Codex sprite format were checked against the local `amon-dev/amon/macos/Sources/AIMonitor` implementation (`PetOverlay.swift`, `PetSettingsRow.swift`, `CodexPetSpriteLayout.swift`). MightyClaude supplies its own event-driven controller; Amon's log collectors and upload pipeline are not included.

The default Mighty Raccoon is generated from the existing app icon as a full-body superhero without a computer. `hatch-pet` and the built-in image generation tool produce separate state strips; deterministic extraction/validation packs a Codex-compatible 1536×1872 atlas. The runtime package is `assets/pets/mighty-raccoon`.

## Verification

`--agent-smoke-test --smoke-exit --profile <temporary-directory>` checks Markdown structure, native multi-message mouse selection and private-pasteboard copy, selection retention during updates, permission-card lifecycle, submitted-request/activity separation, tool-vs-run completion, deduplicated completion, focus navigation, pet import/atlas frames/path validation and preference persistence, and captures the actual native views. It sends no AI request or macOS notification and leaves the system clipboard unchanged. Native banner delivery/OS permission remains a system integration dependent on the user's notification settings.

## Local Claude tool approvals

The Claude selector uses Plan mode (`plan`), Always ask (`manual`), Accept file edits (`acceptEdits`), Auto mode (`auto`) and Bypass (`fullAccess`). Auto invokes Claude's risk classifier; it is separate from Bypass and does not change saved CLI rules. A supported CLI must be discovered before the app advertises Auto. Model/provider/admin eligibility is enforced by the CLI, and explicit asks or blocked actions can still need the approval UI. Existing sessions keep their chosen mode. See the official [permission modes](https://code.claude.com/docs/en/permission-modes) reference.

Local Claude runs opt into the native SDK control channel with `--permission-prompts host --permission-prompt-tool stdio --input-format stream-json`. The app waits for the `initialize` acknowledgement, then sends the user message and keeps stdin open for approval replies until the result arrives. Metadata probes and remote-host runs retain `--permission-prompts none`; remote approval forwarding is not implemented.

A `can_use_tool` control request is shown in readable form: a title for what the tool does, the model's `description`, and named fields such as the command, file path, old/new text or search pattern in code boxes (`ToolPermissionPresentation`); the exact JSON input stays available under **원본 JSON**, together with the decision reason and blocked path when present. Display never alters the input that approval returns. **Allow once** returns the unchanged original input for that request; **Deny** returns a rejection. The chosen permission mode and earlier Claude permission rules continue to apply. No `updatedPermissions`, persistent allow rule, home-directory allowlist or global settings change is sent. Pending approvals are memory-only and are cleared on cancellation, stop, pane close or process exit. Request and run IDs prevent a late click from approving a newer run in the same pane.

The complete JSON display is limited to 64 KiB and at most 16 approvals can wait at once. Oversized inputs are denied rather than approved from a truncated preview. `AskUserQuestion` and requests marked `requires_user_interaction` require a richer input surface and cannot be approved by the generic button. The app offers denial or stopping the run for these requests.

Protocol references: [Claude approval and user-input guide](https://code.claude.com/docs/en/agent-sdk/user-input), [permission evaluation](https://code.claude.com/docs/en/agent-sdk/permissions), and [CLI reference](https://code.claude.com/docs/en/cli-reference). The exact `SDKControlPermissionRequest`, `SDKControlResponse` and `SDKControlCancelRequest` envelopes and `--permission-prompt-tool stdio` launch mapping were checked against the installed official `@anthropic-ai/claude-agent-sdk` 0.3.273 source. Installed Claude Code 2.1.273 accepted a zero-prompt initialization using an isolated profile, safe mode and no session persistence; no model request was made. Native `PermissionTests` exercise the channel with a fake CLI process, including one-time allow/deny, duplicate/cancel/stale requests, unchanged input, mode preservation and child cleanup. Actual paid WebSearch/file-access turns remain untested.
