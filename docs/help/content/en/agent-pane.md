---
title: Agent panes
order: 3
section: agent-pane
---
An agent pane is a conversation with Claude, Codex or Antigravity (Gemini on Windows). You write requests in the composer at the bottom and follow the progress and results above it.

![[agent-basic]]

## Send a request {#send}
1. Type what you want done in the composer.
2. Press {{kbd:Enter}} or click the send button. Press {{kbd:⇧Enter}} for a new line.
3. While it runs, a stop button takes the send button's place. Click it to stop.

An {{kbd:Enter}} pressed while an input method is still composing (Korean, for example) finishes the composition instead of sending.

![[composer]]

### Keep asking while it runs {#queue}
You can write the next request while a task is running. Once the composer holds text, round buttons like send appear beside stop.
- {{kbd:Enter}} or the {{ui:queue.add}} button: queue it as the next request. Queued requests run in order once the current task ends. A pane holds up to 16.
- {{kbd:⌘Enter}} or the {{ui:phone.composer.steer}} button: send it to the running Claude right away. This works only in Claude panes on this Mac. The Mighty view shows it as an orange {{ui:graph.block.steer}} block.
- Click {{ui:queue.runNext}} in the queue to run the first queued request now.

Stopping the task cancels the queued requests too. The queue is emptied when the app reopens.

## Composer tools {#composer}
The row under the composer has, from the left: attach, model, thinking effort, permission and more run settings (…). In a narrow pane some of them fold into one menu. They can't be changed while a task runs, and a change applies from the next request.

### Model {#model}
Click the model chip to choose the {{ui:composer.label.runner}} (Claude, Codex or Antigravity) and the {{ui:composer.label.model}}. If the list looks wrong, click {{ui:composer.model.refresh}}. Switching agents starts a new conversation.

### Thinking effort {#effort}
{{ui:composer.effort.label}} sets how deeply the model thinks: Auto, Low, Medium, High, XHigh or Max. Only the levels the model supports are shown. Antigravity and Gemini have none.

### Permissions {#permission}
The permission mode sets what the agent may do without asking.

| Agent | Modes |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Antigravity (Gemini on Windows) | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- Claude's Always ask and Codex's {{ui:permission.label.onRequest}} ask for approval in the pane when extra permission is needed. See [Approvals and questions](approvals.md#permission).
- Auto mode appears only when the installed Claude CLI supports it.
- Bypass and {{ui:permission.label.fullAccess}} reach files and commands outside the project too. Use them only when you must.

### Antigravity on the Mac {#antigravity}
On the Mac, Google's agent runs through Antigravity CLI (`agy`), which can also run Claude and GPT-OSS models. The Windows app still uses Gemini CLI for now.

- Install: `curl -fsSL https://antigravity.google/cli/install.sh | bash` puts `agy` in `~/.local/bin`. The app updates it with `agy update`.
- Sign in: run `agy` once in a terminal pane and sign in with your Google account in the browser. {{ui:settings.cliAccounts.buttonLogin}} on the {{ui:settings.nav.cli}} tab does this for you. Antigravity has no sign-out command; to switch accounts, use `/logout` inside `agy`.
- Permissions: Antigravity cannot ask you in the app. {{ui:permission.label.default}} follows the Agent Mode saved in agy (review, unless you changed it in agy's `/config`): it reads the workspace's files but declines the file edits and shell commands it would ask about, and the pane says what it declined. {{ui:permission.label.plan}} writes a plan in agy's own folder and stops without editing the project; send the next request in another mode to carry it out. Allow them under `permissions.allow` in `~/.gemini/antigravity-cli/settings.json` (for example `command(git)`), or choose {{ui:permission.label.acceptEdits}} (file edits) or {{ui:permission.label.fullAccess}} (everything).
- Each Antigravity model carries its effort in its name, so there is no separate {{ui:composer.effort.label}}.
- A pane that ran Gemini CLI before keeps its history, but its next request starts a new conversation, and a Gemini CLI model it had is set back to the default.

### More run settings {#run-settings}
Click the … button to open {{ui:settings.run.title}}.
- Claude: {{ui:settings.run.maxTurnsLabel}} and {{ui:settings.run.maxBudgetLabel}}. Leave them empty for no limit.
- Codex: {{ui:settings.run.webSearchLabel}} and {{ui:settings.run.shellNetworkToggle}}. The Fast chip in the composer answers faster on supported models and accounts but uses more of your usage.

Change the values, then click {{ui:settings.run.applyButton}}.

![[composer-settings]]

### Attach files and pictures {#attachments}
Click the paperclip, drag files onto the composer, or paste a picture with {{kbd:⌘V}}.
- Up to 8 files, 5 MiB each, 8 MiB in total.
- You can preview them and remove them one by one before sending. You can send attachments alone.
- Terminal panes can't take attachments.

### Slash commands {#slash}
Type `/` in the composer to see the command list. Move with {{kbd:↑}}{{kbd:↓}}, insert with {{kbd:Enter}} or {{kbd:Tab}}, and close with {{kbd:Esc}}.

| Command (Claude) | What it does |
|---|---|
| `/model` | {{ui:slash.builtin.model}} |
| `/permissions` | {{ui:slash.builtin.permission}} |
| `/clear` | {{ui:slash.builtin.newConversation}} |
| `/cost`, `/usage` | {{ui:slash.builtin.usage}} |
| `/plugin` | {{ui:slash.builtin.plugin}} |
| `/config` | {{ui:slash.builtin.settings}} |
| `/rename` | {{ui:slash.builtin.rename}} |
| `/help` | {{ui:slash.builtin.help}} |

Codex and Antigravity use slightly different names (Codex: `/new`, `/approvals`, `/status`; Antigravity: `/usage`, `/config`, for example). Any other `/name` is passed to the CLI as a skill, user command or plugin command. Antigravity expands its skills this way; its own read-only commands such as `/help` or `/skills` answer only when you run them as `agy -p "/help"` in a terminal, so in the app they end with agy's error.

### Context and usage {#session-info}
The ring beside the send button shows how much context the conversation has used. Click it for {{ui:composer.sessionInfo.contextUsage}}, input, output and cache tokens, cost, the model used and the session IDs. Only values the CLI reported are shown; nothing is estimated.

![[session-info]]

## Default and Mighty views {#views}
Claude and Codex panes have a {{ui:graph.view.default}} | {{ui:graph.view.mighty}} switch in the header. Switching keeps the conversation and your draft, and each pane remembers its choice. Antigravity and Gemini panes have only the default view.
- {{ui:graph.view.default}}: the conversation as a log you read top to bottom.
- {{ui:graph.view.mighty}}: requests, sub-agents, background work and results as a flow of blocks.

### The Mighty flow {#diagram}
Each request gets a request block with a {{ui:graph.block.execution}} block right below it. The request block holds only what you sent; what the agent did because of it (messages, tool calls, progress) shows in the {{ui:graph.block.execution}} block. Sub-agents and background tasks branch out below the {{ui:graph.block.execution}} block. When the request ends, a {{ui:graph.block.result}} card is attached.
- Drag the empty background or scroll to move around. The zoom buttons go from 50% to 150%.
- Click a block to scroll inside it. Click the background or press {{kbd:Esc}} to deselect.
- Drag a block's bottom-right corner to resize it. Right-click for {{ui:graph.block.resetSize}}. Sizes are saved per session.
- Scroll to the top and use {{ui:graph.history.load}} to load earlier requests, 10 at a time. Loaded history is view-only.
- The puzzle button in the header opens the installed plugins and the marketplace.

![[agent-mighty]]

![[agent-mighty-overview]]

### Log {#timeline}
Use the {{ui:graph.view.diagram}} | {{ui:graph.view.timeline}} switch at the top of the Mighty view to see requests as a list in time order. Each request row is followed by its {{ui:graph.block.execution}} row, then its sub-agents.

![[agent-timeline]]

### Board {#structure}
Pick {{ui:graph.view.structure}} on the same switch to see requests as a kanban board.
- The view shows one thing at a time: one request in a pane without a style, one pipeline in a pane that uses a style. A pipeline is a run of requests whose phase only moves forward; a request whose phase goes back before the previous request's starts a new pipeline.
- ◀ {{ui:graph.structure.board.previous}} and ▶ {{ui:graph.structure.board.next}} at the top step back and forth. Click the title between them, such as "Request 3 / 7", to open the list with each one's number, title, status and steps, and pick any of them.
- Below that is the progress: the status (and, in a pane with a style, the pipeline's name), then the step chips joined by lines. In a pane without a style they are {{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}; in a pane that uses a style, the mode's phases. Finished steps are green, the current one is in the accent colour and the rest are grey.
- The content sits in columns side by side, one per step, each headed by the step's name and its number of cards. Without a style, the {{ui:graph.structure.column.request}} column holds the request, the {{ui:graph.structure.column.execution}} column a card with the progress (the latest message and tool calls) and the status followed by one card per sub-agent, and the {{ui:graph.structure.column.result}} column the result. With a style, each phase column holds that phase's request cards (the request, a run summary and the result on one line) with their sub-agent cards under them. Nested sub-agents are indented and say which block they run under.
- Long text is folded; {{ui:graph.structure.more}} opens it in place. The small button in a card's header shows that block in the flow.
- The view opens on the newest one and, left there, moves on to each new request or pipeline. While you look at an older one, a new one leaves you where you are and puts a {{ui:graph.structure.board.newRequest}} or {{ui:graph.structure.board.newPipeline}} badge next to ▶; click it to go to the newest.
- Each column moves on its own: its header stays on top while its cards move up and down. Scroll up or down over a column with the trackpad or the mouse wheel, or drag an empty spot in it, to move that column only; when the columns don't all fit, scroll or drag sideways to move the board sideways.
- On the newest one, a column showing its end follows new content down. A column you moved yourself, or one where you opened a text, stays where it is until you come back to its end. A new request shows every column's end, and an older one opens with every column at its top. In the Windows app the whole board still moves as one.

![[agent-structure]]

### The result card {#result}
When a request ends, the {{ui:graph.block.result}} card sums up the result. If the result names files, {{ui:graph.resultFiles.openButton}} lists them. Clicking a file path inside a block opens a preview. The latest result card fits the pane; if you resized it yourself, a {{ui:graph.result.fitToWindow}} button appears.

![[result-card]]
