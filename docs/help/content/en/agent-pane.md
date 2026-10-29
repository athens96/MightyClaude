---
title: Agent panes
order: 3
section: agent-pane
---
An agent pane is a conversation with Claude, Codex or Antigravity. You write requests in the composer at the bottom and follow the progress and results above it.

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
- {{kbd:⌘Enter}} or the {{ui:queue.worktree}} button: in a Claude pane on this Mac, or a Codex or Antigravity pane in a mode where its child can change files, with Mighty Orchestration on, in a git repository on a branch with a commit, run it right away in a new child pane with its own git worktree, beside this one, while the running task goes on; when the child is done, merge its result from its card in the sidebar ([Run in a new worktree](delegation.md#worktree)).
- In a child pane, outside such a repository, or with Mighty Orchestration off, {{kbd:⌘Enter}} queues it too, as {{kbd:Enter}} does. A text whose child could not be made or started goes to the queue as well ([Run in a new worktree](delegation.md#worktree)). Nothing you send joins the running task any more; in older records the Mighty view still shows such texts as orange {{ui:graph.block.steer}} blocks. A delegation notice handed straight to a running Claude parent shows as a {{ui:graph.block.delegationNotice}} block of the same colour ([Notices and held items](delegation.md#notices)).
- Each request in the queue has a {{ui:queue.runNow}} button beside its ✕. Where a new worktree can be made (the same conditions as {{kbd:⌘Enter}} above), it runs that request now in a new child pane, and the running task goes on. Right-click (or Control-click on the Mac) to pick another agent. The button's tooltip says which will happen. A request with attachments but no text never goes to a worktree.
- Where no worktree can be made, the request is handled in this pane. A pane that is idle, such as one stopped by an error, starts it now. A Claude pane whose answer is done and which only waits on background work takes it straight into the running Claude, where it shows as a {{ui:graph.block.steer}} block in the Mighty view (a request with attachments waits until the work ends). Otherwise the button is greyed out and its tooltip says why: the current task must end first, items held for the pane go first, or a CLI update is under way. The request then waits its turn. The follow-up of an Antigravity card only ever runs in its own pane.

Stopping the task cancels the queued requests too, except in a pane whose turn only waits on background work: there Stop keeps the queue and starts its first request now. The queue is emptied when the app reopens.

## Composer tools {#composer}
The row under the composer has, from the left: attach, model, thinking effort, permission and more run settings (…). In a narrow pane some of them fold into one menu. They can't be changed while a task runs, and a change applies from the next request.

### Model {#model}
Click the model chip to choose the {{ui:composer.label.runner}} (Claude, Codex or Antigravity) and the {{ui:composer.label.model}}. If the list looks wrong, click {{ui:composer.model.refresh}}. Switching agents starts a new conversation.

### Thinking effort {#effort}
{{ui:composer.effort.label}} sets how deeply the model thinks: Auto, Low, Medium, High, XHigh or Max. Only the levels the model supports are shown. Antigravity has none.

### Permissions {#permission}
The permission mode sets what the agent may do without asking.

| Agent | Modes |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Antigravity | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- Claude's Always ask and Codex's {{ui:permission.label.onRequest}} ask for approval in the pane when extra permission is needed. See [Approvals and questions](approvals.md#permission).
- Auto mode appears only when the installed Claude CLI supports it.
- Bypass and {{ui:permission.label.fullAccess}} reach files and commands outside the project too. Use them only when you must.

### Antigravity {#antigravity}
Google's agent runs through Antigravity CLI (`agy`), which can also run Claude and GPT-OSS models.

- Install: on the Mac, `curl -fsSL https://antigravity.google/cli/install.sh | bash` puts `agy` in `~/.local/bin`. On Windows, `irm https://antigravity.google/cli/install.ps1 | iex` in PowerShell puts `agy.exe` in `%LOCALAPPDATA%\agy\bin`. The app updates it with `agy update`.
- Sign in: run `agy` once in a terminal pane and sign in with your Google account in the browser. {{ui:settings.cliAccounts.buttonLogin}} on the {{ui:settings.nav.cli}} tab does this for you. Antigravity has no sign-out command; to switch accounts, use `/logout` inside `agy`.
- Permissions: Antigravity cannot ask you in the app. {{ui:permission.label.default}} follows the Agent Mode saved in agy (review, unless you changed it in agy's `/config`): it reads the workspace's files but declines the file edits and shell commands it would ask about, finishes the turn, and then asks about what it declined in a permission card. {{ui:permission.allowOnce}} or {{ui:antigravity.permission.allowAlways}} continues in the same conversation ([Antigravity's permission card](approvals.md#antigravity-permission)). {{ui:permission.label.plan}} writes a plan in agy's own folder and stops without editing the project; send the next request in another mode to carry it out. To allow something ahead of time, add a rule under `permissions.allow` in `~/.gemini/antigravity-cli/settings.json` yourself (for example `command(git status)`; a rule allows every command that starts with its text, so keep it narrow), or choose {{ui:permission.label.acceptEdits}} (file edits) or {{ui:permission.label.fullAccess}} (everything).
- Each Antigravity model carries its effort in its name, so there is no separate {{ui:composer.effort.label}}.
- The {{ui:graph.view.mighty}} view works as for Claude and Codex: each sub-agent Antigravity starts is a block of its own under the request, filled from the sub-agent's own record in `~/.gemini/antigravity-cli` (`%USERPROFILE%\.gemini\antigravity-cli` on Windows; its instructions, messages, tool calls and the answer it sent back; files it read are named, not copied). Earlier requests of the conversation load from the same records, and {{ui:resume.choice.resume}} lists this folder's Antigravity conversations.
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

### Send to {#ask-target}
A Claude pane with oh-my-claudecode installed shows a {{ui:composer.ask.menuLabel}} switch in the composer. It lists the other CLIs that are installed (Codex, Antigravity and so on); once you pick one, your requests go to it through oh-my-claudecode's `ask`. The answer comes back inside Claude's reply. Pick {{ui:composer.ask.claude}} to send to Claude as before. It works on the Mac, in the Windows app's web window and on the phone.
- Attachments can't be sent while you are asking another CLI.
- oh-my-claudecode starts the chosen CLI with its approvals and sandbox turned off, so it can change files and run commands in this folder without asking. Mighty Claude's permission mode and approval cards cover only Claude's step that starts it. The composer shows this warning in one line while a CLI is chosen.
- On Windows, older versions of oh-my-claudecode refuse to ask Antigravity. If that answer comes back, update oh-my-claudecode or use Gemini CLI.
- Text that starts with `/` still goes to Claude as it is.

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

Codex and Antigravity use slightly different names (Codex: `/new`, `/approvals`, `/status`; Antigravity: `/usage`, `/config`, for example). Any other `/name` is passed to the CLI as a skill, user command or plugin command. Antigravity expands its skills this way, and the list offers agy's built-in skills, yours in `~/.gemini/antigravity-cli/skills` and the folder's `.agents/skills`; its own read-only commands such as `/help` or `/skills` answer only when you run them as `agy -p "/help"` in a terminal, so in the app they end with agy's error.

### Context and usage {#session-info}
The ring beside the send button shows how much context the conversation has used. Click it for {{ui:composer.sessionInfo.contextUsage}}, input, output and cache tokens, cost, the model used and the session IDs. Only values the CLI reported are shown; nothing is estimated.

![[session-info]]

## Default and Mighty views {#views}
Claude, Codex and Antigravity panes have a {{ui:graph.view.default}} | {{ui:graph.view.mighty}} switch in the header. Switching keeps the conversation and your draft, and each pane remembers its choice.
- {{ui:graph.view.default}}: the conversation as a log you read top to bottom.
- {{ui:graph.view.mighty}}: requests, sub-agents, background work and results as a flow of blocks.

### The Mighty flow {#diagram}
Each request gets a request block with a {{ui:graph.block.execution}} block right below it. The request block holds only what you sent; what the agent did because of it (messages, tool calls, progress) shows in the {{ui:graph.block.execution}} block. Sub-agents and background tasks branch out below the {{ui:graph.block.execution}} block. When the request ends, a {{ui:graph.block.result}} card is attached.

While they run, a sub-agent started in the background and a background command (task) are not drawn as blocks of the request; they sit on the background work strip above the composer. A row shows the kind, name, elapsed time and the last line of output, and opening a row shows the command it ran (a sub-agent shows the first line of its prompt) and the last lines of its output (up to 6). When a task ends and its notice goes to Claude, each answer of Claude's makes a new {{ui:graph.background.noticeRun}} diagram, with one block for each ended task: a kind line (sub-agent or background command, and how it ended), the command, the last lines of its output and a sub-agent's report. In {{ui:graph.view.timeline}} the diagram is a new row, and in {{ui:graph.view.structure}} a new card (board) at the end. The phone's Mighty view and pipeline work the same way.

- Drag the empty background or scroll to move around. The zoom buttons go from 50% to 150%.
- Click a block to scroll inside it. Click the background or press {{kbd:Esc}} to deselect.
- Drag a block's bottom-right corner to resize it. Right-click for {{ui:graph.block.resetSize}}. Sizes are saved per session.
- Scroll to the top and use {{ui:graph.history.load}} to load earlier requests, 10 at a time. Loaded history is view-only.
- In Claude and Codex panes, the puzzle button in the header opens the installed plugins and the marketplace.

![[agent-mighty]]

![[agent-mighty-overview]]

### Log {#timeline}
Use the {{ui:graph.view.diagram}} | {{ui:graph.view.timeline}} switch at the top of the Mighty view to see requests as a list in time order. Each request row is followed by its {{ui:graph.block.execution}} row, then its sub-agents.

![[agent-timeline]]

### Pipeline {#structure}
Pick {{ui:graph.view.structure}} on the same switch to see requests as a kanban-style pipeline.
- The view shows one thing at a time: one request in a pane without a style, one pipeline in a pane that uses a style. A pipeline is a run of requests whose phase only moves forward; a request whose phase goes back before the previous request's starts a new pipeline.
- ◀ {{ui:graph.structure.board.previous}} and ▶ {{ui:graph.structure.board.next}} at the top step back and forth. Click the title between them, such as "Request 3 / 7", to open the list with each one's number, title, status and steps, and pick any of them.
- Below that is the status (and, in a pane with a style, the pipeline's name). The steps are the chips at the top of each column, joined by lines. In a pane without a style they are {{ui:graph.structure.column.request}} → {{ui:graph.structure.column.execution}} → {{ui:graph.structure.column.result}}; in a pane that uses a style, the mode's phases.
- The content sits in columns side by side, one per step, each headed by the step's chip and its number of cards. Without a style, the {{ui:graph.structure.column.request}} column holds the request, the {{ui:graph.structure.column.execution}} column a card with the progress (the latest message and tool calls) and the status followed by one card per sub-agent, and the {{ui:graph.structure.column.result}} column the result. With a style, each phase column holds that phase's request cards (the request, a run summary and the result on one line) with their sub-agent cards under them. Nested sub-agents are indented and say which block they run under.
- Colour shows the state too. Each column is washed in its step's state colour (green when done, blue while running, yellow while waiting, red on an error, grey when stopped), the current step follows its newest request, and a step not reached yet stays plain. Every card has a bar of that colour down its left edge and a wash lighter than its column's; a running card keeps its moving outline, a waiting card its yellow outline, and the status words stay as they are.
- Long text is folded; {{ui:graph.structure.more}} opens it in place. The small button in a card's header shows that block in the flow.
- The view opens on the newest one and, left there, moves on to each new request or pipeline. While you look at an older one, a new one leaves you where you are and puts a {{ui:graph.structure.board.newRequest}} or {{ui:graph.structure.board.newPipeline}} badge next to ▶; click it to go to the newest.
- Each column moves on its own: its header stays on top while its cards move up and down. Scroll up or down over a column with the trackpad or the mouse wheel, or drag an empty spot in it, to move that column only; when the columns don't all fit, scroll or drag sideways to move the board sideways.
- On the newest one, a column showing its end follows new content down. A column you moved yourself, or one where you opened a text, stays where it is until you come back to its end. A new request shows every column's end, and an older one opens with every column at its top. In the Windows app the whole pipeline still moves as one.

![[agent-structure]]

### The result card {#result}
When a request ends, a result card sums up the result. A request reads completed once Claude's answer ends. While background work it started is still running, the card is titled "Result so far · background tasks waiting: N", which tells you how many are left; when the last one ends it becomes {{ui:graph.block.result}}. When Claude answers a background task's notice later, that answer is not added to the request's result; every answer opens a new diagram of its own ([Background work](approvals.md#background)). If the result names files, {{ui:graph.resultFiles.openButton}} lists them. Clicking a file path inside a block opens a preview. The latest result card fits the pane; if you resized it yourself, a {{ui:graph.result.fitToWindow}} button appears.

![[result-card]]
