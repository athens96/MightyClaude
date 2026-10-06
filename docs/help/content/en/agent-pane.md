---
title: Agent panes
order: 3
section: agent-pane
---
An agent pane is a conversation with Claude, Codex or Gemini. You write requests in the composer at the bottom and follow the progress and results above it.

![[agent-basic]]

## Send a request {#send}
1. Type what you want done in the composer.
2. Press {{kbd:Enter}} or click the send button. Press {{kbd:⇧Enter}} for a new line.
3. While it runs, a stop button appears beside send. Click it to stop.

An {{kbd:Enter}} pressed while an input method is still composing (Korean, for example) finishes the composition instead of sending.

![[composer]]

### Keep asking while it runs {#queue}
You can write the next request while a task is running.
- {{kbd:Enter}}: queue it as the next request. Queued requests run in order once the current task ends. A pane holds up to 16.
- {{kbd:⌘Enter}}: send it to the running Claude right away. This works only in Claude panes on this Mac. The Mighty view shows it as an orange {{ui:graph.block.steer}} block.
- Click {{ui:queue.runNext}} in the queue to run the first queued request now.

Stopping the task cancels the queued requests too. The queue is emptied when the app reopens.

## Composer tools {#composer}
The row under the composer has, from the left: attach, model, thinking effort, permission and more run settings (…). In a narrow pane some of them fold into one menu. They can't be changed while a task runs, and a change applies from the next request.

### Model {#model}
Click the model chip to choose the {{ui:composer.label.runner}} (Claude, Codex or Gemini) and the {{ui:composer.label.model}}. If the list looks wrong, click {{ui:composer.model.refresh}}. Switching agents starts a new conversation.

### Thinking effort {#effort}
{{ui:composer.effort.label}} sets how deeply the model thinks: Auto, Low, Medium, High, XHigh or Max. Only the levels the model supports are shown. Gemini has none.

### Permissions {#permission}
The permission mode sets what the agent may do without asking.

| Agent | Modes |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Gemini | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- Claude's Always ask and Codex's {{ui:permission.label.onRequest}} ask for approval in the pane when extra permission is needed. See [Approvals and questions](approvals.md#permission).
- Auto mode appears only when the installed Claude CLI supports it.
- Bypass and {{ui:permission.label.fullAccess}} reach files and commands outside the project too. Use them only when you must.

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

Codex and Gemini use slightly different names (Codex: `/new`, `/approvals`, `/status`, for example). Any other `/name` is passed to the CLI as a skill, user command or plugin command.

### Context and usage {#session-info}
The ring beside the send button shows how much context the conversation has used. Click it for {{ui:composer.sessionInfo.contextUsage}}, input, output and cache tokens, cost, the model used and the session IDs. Only values the CLI reported are shown; nothing is estimated.

![[session-info]]

## Default and Mighty views {#views}
Claude and Codex panes have a {{ui:graph.view.default}} | {{ui:graph.view.mighty}} switch in the header. Switching keeps the conversation and your draft, and each pane remembers its choice. Gemini panes have only the default view.
- {{ui:graph.view.default}}: the conversation as a log you read top to bottom.
- {{ui:graph.view.mighty}}: requests, sub-agents, background work and results as a diagram of blocks.

### The Mighty diagram {#diagram}
Each request gets a block, and its sub-agents and background tasks branch out below it. When it ends, a {{ui:graph.block.result}} card is attached.
- Drag the empty background or scroll to move around. The zoom buttons go from 50% to 150%.
- Click a block to scroll inside it. Click the background or press {{kbd:Esc}} to deselect.
- Drag a block's bottom-right corner to resize it. Right-click for {{ui:graph.block.resetSize}}. Sizes are saved per session.
- Scroll to the top and use {{ui:graph.history.load}} to load earlier requests, 10 at a time. Loaded history is view-only.
- The puzzle button in the header opens the installed plugins and the marketplace.

![[agent-mighty]]

![[agent-mighty-overview]]

### Timeline {#timeline}
Use the {{ui:graph.view.diagram}} | {{ui:graph.view.timeline}} switch at the top of the Mighty view to see requests as a list in time order.

![[agent-timeline]]

### The result card {#result}
When a request ends, the {{ui:graph.block.result}} card sums up the result. If the result names files, {{ui:graph.resultFiles.openButton}} lists them. Clicking a file path inside a block opens a preview. The latest result card fits the pane; if you resized it yourself, a {{ui:graph.result.fitToWindow}} button appears.

![[result-card]]
