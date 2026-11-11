---
title: Agent panes
order: 3
section: agent-pane
---
An agent pane is a conversation with Claude, Codex, Antigravity, pi, opencode or Hermes. You write requests in the composer at the bottom and follow the progress and results above it.

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
Click the model chip to choose the {{ui:composer.label.runner}} (Claude, Codex, Antigravity, pi, opencode or Hermes) and the {{ui:composer.label.model}}. If the list looks wrong, click {{ui:composer.model.refresh}}. Switching agents starts a new conversation.

### Thinking effort {#effort}
{{ui:composer.effort.label}} sets how deeply the model thinks: Auto, Low, Medium, High, XHigh or Max. Only the levels the model supports are shown. Antigravity has none; pi has Off, Minimal, Low, Medium, High and XHigh; opencode has Low, Medium, High and Max, the variants its model offers; Hermes has Low, Medium, High, XHigh and Max.

### Advisor {#advisor}
A Claude pane has a light-bulb {{ui:composer.advisor.label}} button beside {{ui:composer.effort.label}}. It is an experimental Claude Code feature: Claude asks another model for advice while it works. {{ui:composer.advisor.follow}} (the default) passes nothing and runs as Claude Code is set up. Choosing a model makes it the advisor, and {{ui:composer.advisor.off}} turns the advisor off for this pane's requests only. An advisor uses more tokens. If you choose a model weaker than the main one, Claude Code reports an error and the pane shows it. Workspaces on another computer and other agents' panes have no such button.

### Permissions {#permission}
The permission mode sets what the agent may do without asking.

| Agent | Modes |
|---|---|
| Claude | Plan mode · Always ask · Accept file edits · Auto mode · Bypass |
| Codex | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.acceptEditsCodex}} · {{ui:permission.label.onRequest}} · {{ui:permission.label.fullAccess}} |
| Antigravity | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |
| pi | {{ui:permission.label.defaultCodex}} · {{ui:permission.label.fullAccess}} |
| opencode | {{ui:permission.label.default}} · {{ui:permission.label.plan}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |
| Hermes | {{ui:permission.label.default}} · {{ui:permission.label.acceptEdits}} · {{ui:permission.label.fullAccess}} |

- Claude's Always ask, Codex's {{ui:permission.label.onRequest}} every opencode mode but {{ui:permission.label.fullAccess}}, and Hermes's file edits and dangerous commands ask for approval in the pane when extra permission is needed. See [Approvals and questions](approvals.md#permission).
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

### pi {#pi}
pi (`pi`) is an open-source coding agent CLI that works with many model providers.

- Install: from the {{ui:setup.title}} screen (it installs Homebrew and Node.js first when they are missing), or `npm install -g @mariozechner/pi-coding-agent`. The app updates it with `pi update self`.
- Sign in: {{ui:settings.cliAccounts.buttonLogin}} on the {{ui:settings.nav.cli}} tab opens a terminal pane running `pi`. Type `/login` there and pick a subscription (Claude Pro/Max, ChatGPT, GitHub Copilot) or an API key. An API key in the environment (such as `ANTHROPIC_API_KEY`) or in `~/.pi/agent/auth.json` counts as signed in too. To sign out, type `/logout` inside pi.
- Model: once you are signed in, the list comes from `pi --list-models` as `provider/model`; the first entry follows pi's own settings. A model without thinking shows no {{ui:composer.effort.label}}; the levels are Off, Minimal, Low, Medium, High and XHigh.
- Permissions: pi has no approval window, so there are two modes. {{ui:permission.label.defaultCodex}} turns on only the read tools (read, grep, find, ls). {{ui:permission.label.fullAccess}} uses every tool without asking, outside the project too.
- Each request runs `pi --mode rpc` in the pane's folder and continues the pane's own session, saved in `~/.pi/agent/sessions`. {{ui:resume.choice.resume}} lists the pi sessions of the folder as well.
- Questions: pi has no question tool, so the app adds an `ask_user` tool as an extension of each run (your `~/.pi` settings are not changed). When pi asks through it, the [question card](approvals.md#questions) appears, and your answer continues the same request. Pick, confirm and input dialogs opened by other extensions appear as the same card.
- Not yet: pi panes take no part in [Mighty Orchestration](delegation.md), cannot be opened in a [remote workspace](remote.md), and show no approval cards. They work on the Mac only for now.

### opencode {#opencode}
opencode (`opencode`) is an open-source coding agent CLI that works with many model providers, and also runs the free models of its own service, opencode Zen, without a sign-in.

- Install: from the {{ui:setup.title}} screen (it installs Homebrew and Node.js first when they are missing), or `npm install -g @opencode/cli`. The app uses OpenCode 2; an OpenCode 1 found on the Mac shows as out of date, and installing from the setup screen removes the npm `opencode-ai` first. The app updates it with `opencode upgrade`.
- Sign in: {{ui:settings.cliAccounts.buttonLogin}} on the {{ui:settings.nav.cli}} tab opens a terminal pane running `opencode auth login`. Pick a provider there, then sign in with its subscription or enter an API key. A sign-in opencode keeps (`opencode auth list`) or an API key in the environment (such as `ANTHROPIC_API_KEY`) counts as signed in; without one only opencode Zen's free models are offered. To sign out, run `opencode auth logout` in a terminal.
- Model: the list comes from `opencode models` as `provider/model`; the first entry follows opencode's own settings.
- Permissions: the mode becomes opencode's own rules for the run, and what they leave to ask comes to the pane as an approval card, as for Claude. {{ui:permission.label.default}} asks before edits, commands, the web and anything outside the project; {{ui:permission.label.acceptEdits}} edits without asking; {{ui:permission.label.plan}} runs opencode's plan agent, which does not edit; {{ui:permission.label.fullAccess}} never asks.
- Each request starts `opencode acp` in the pane's folder (OpenCode 2, the Agent Client Protocol) and continues the pane's own session, kept in opencode's `opencode.db`. {{ui:resume.choice.resume}} lists the opencode sessions of the folder as well. Usage shows the session's cost, the context in use and the request's tokens.
- Questions: when opencode asks through its `question` tool, the [question card](approvals.md#questions) appears, and your answer continues the same request. If you click {{ui:phone.questionnaire.cancel}} on the card, opencode ends that request.
- Not yet: opencode panes take no part in [Mighty Orchestration](delegation.md) and cannot be opened in a [remote workspace](remote.md). They work on the Mac only for now.

### Hermes {#hermes}
Hermes (`hermes`) is Nous Research's open-source agent CLI (Hermes Agent). It works with many model providers, such as Nous Portal, OpenRouter, Anthropic and OpenAI.

- Install: from the {{ui:setup.title}} screen (it installs Git first when it is missing). The official install script puts Hermes in `~/.hermes` with its own Python environment and downloads its browser tools, so it takes a few minutes. To install it yourself, run `curl -fsSL https://hermes-agent.nousresearch.com/install.sh | bash`. The app updates it with `hermes update`.
- Sign in: {{ui:settings.cliAccounts.buttonLogin}} on the {{ui:settings.nav.cli}} tab opens a terminal pane running `hermes model`. Pick a model provider there, sign in or enter an API key, then pick a default model. The app reads only the name of the provider whose credentials Hermes finds. To remove credentials, use `hermes auth` in a terminal.
- Model: the first entry follows Hermes's own settings (the default model set with `hermes model`). Hermes lists models only once a session is open, so the `provider:model` list appears after the first request.
- Permissions: {{ui:permission.label.default}} asks with an approval card in the pane before every file edit; {{ui:permission.label.acceptEdits}} does not ask about edits inside the workspace folder; {{ui:permission.label.fullAccess}} is Hermes's `--yolo`, which does not ask about dangerous commands either. Dangerous commands (such as `rm -rf`) are asked about as Hermes's approvals setting (`approvals.mode`) says; other commands and reads are not asked about. There is no plan-only mode. {{ui:permission.label.fullAccess}} is fixed when a session starts, so a session already started gets it from a new conversation.
- Each request starts `hermes acp` in the pane's folder (the Agent Client Protocol) and continues the pane's own session. The work itself runs in Hermes's gateway, a background process that starts on demand and ends when idle. Sessions are kept in `~/.hermes/state.db`, and {{ui:resume.choice.resume}} lists the Hermes sessions of the folder as well. Usage shows the session's running token counts and the context in use; Hermes does not report cost.
- Questions: when Hermes asks through its clarify tool, the [question card](approvals.md#questions) appears, and your answer continues the same request. Hermes leaves this tool off for editor connections (ACP) by default, so before the first Hermes request the app turns it on by setting one Hermes config entry, `platform_toolsets.acp`, with `hermes config set`, and says so in the log. A list you saved there yourself is left alone. It applies from a new conversation; to undo it, run `hermes config unset platform_toolsets.acp` in a terminal. Hermes waits for an answer only for a while (90 seconds by default); after that the card closes and Hermes goes on without one.
- Not yet: Hermes panes take no part in [Mighty Orchestration](delegation.md) and cannot be opened in a [remote workspace](remote.md). They work on the Mac only for now.

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
- oh-my-claudecode starts the chosen CLI with its approvals and sandbox turned off, so it can change files and run commands in this folder without asking. Mighty Code's permission mode and approval cards cover only Claude's step that starts it. The composer shows this warning in one line while a CLI is chosen.
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

## The Mighty view {#views}
Claude, Codex and Antigravity panes show the {{ui:graph.view.mighty}} view: requests, sub-agents, background work and results as a flow of blocks. There is nothing to switch.

### The Mighty flow {#diagram}
Each request gets a request block with a {{ui:graph.block.execution}} block right below it. The request block holds only what you sent; what the agent did because of it (messages, tool calls, progress) shows in the {{ui:graph.block.execution}} block. Sub-agents and background tasks branch out below the {{ui:graph.block.execution}} block. When the request ends, a {{ui:graph.block.result}} card is attached.

The header of the {{ui:graph.block.execution}} block and of each sub-agent block shows who is working. The colour bar at its left edge and the icon's colour are the role (the main session, a sub-agent). After the title come the sub-agent's type (Explore, a custom agent's name and so on) and marks for background, fork and worktree; the {{ui:graph.block.execution}} block shows the thinking effort chosen in the pane. The model sits with the token count, and while there is no response yet a requested model is marked "requested". A custom agent whose definition file names a `color` is drawn in that colour. In a narrow block these marks hide first; hover the icon to read the whole line.

In a Claude pane's flow, a running sub-agent shows as a small card under the block that started it, side by side with the others. A sub-agent started in the background shows here too. The card has its name, its type and model, one line of what it is doing now, and its state with the elapsed time, the number of tool calls and the tokens. Only what the app actually saw is shown; there is no percentage. Click a card to open it as a full block, and fold it back with the {{ui:graph.card.fold}} button in the block's header. A foreground sub-agent becomes a block when it ends, as before. A background sub-agent stays a card after it ends; clicking it goes to its full block in the {{ui:graph.background.noticeRun}} diagram. A command run in the background (waiting on CI, an app build, a dev server) stands in the same row as a grey card, with its description, its command, the last line it printed, and how long it has been running, or how it ended and its exit code.

On the branch above the cards, one line says how many sub-agents the block handed work to, on which model, and how many of them are still running. When background commands are in the row too, it says how many run side by side and how many of them are sub-agents and how many tasks. Until the result is there, a small marker stands where it will go. While sub-agents run it says their reports are awaited, and once they have all ended while the main session still works it reads “{{ui:graph.return.reviewing}}”. When the result card appears the marker goes and the branches end in the card.

Under a Claude pane's flow, above the composer, is the {{ui:graph.log.title}} strip. Its header line is the status right now: the main session's model and state, how many of its sub-agents are running and how many background commands run, each in its role's colour. Opened, the strip lists the last five things that happened in the newest request: the time, who did it (the main session's model or a sub-agent's name) and what it was (a tool call, a sub-agent starting or ending, a question, a {{ui:graph.block.compact}}, a notice arriving). Click a line to move the flow to that block. The button at the right end folds the strip to its status line, and each pane remembers whether it is folded.

A request with an advisor on, or one that actually asked for advice, has an {{ui:graph.identity.role.advisor}} band on its left. Its header shows the advisor's model and what it is doing now ({{ui:graph.advisor.state.onCall}}, {{ui:graph.advisor.state.consulting}}). Under it is one line for each time advice was asked: who asked, when and how long the reply took, where in the work it was ({{ui:graph.advisor.position.beforeChange}}, {{ui:graph.advisor.position.during}}, {{ui:graph.advisor.position.lastStep}}), and how it ended. At the bottom are the number of calls and the tokens the advisor read. The advice normally goes to Claude encrypted, so its text is not shown, and the band says so. Dotted lines run from the band to the blocks that asked, and clicking a line moves to that block. The {{ui:graph.log.title}} also has a line for the advisor's start and end, and the status line gains the advisor's state.

While they run, a sub-agent started in the background and a background command (task) are not drawn as blocks of the request; they sit on the background work strip above the composer. On the Mac, a Claude pane also shows the sub-agents as the cards above. A row shows the kind, name, elapsed time and the last line of output, and opening a row shows the command it ran (a sub-agent shows the first line of its prompt) and the last lines of its output (up to 6). When a task ends and its notice goes to Claude, each answer of Claude's makes a new {{ui:graph.background.noticeRun}} diagram, with one block for each ended task: a kind line (sub-agent or background command, and how it ended), the command, the last lines of its output and a sub-agent's report. In {{ui:graph.view.timeline}} the diagram is a new row, and in {{ui:graph.view.structure}} a new card (board) at the end. The phone's Mighty view and pipeline work the same way.

When Claude or Codex makes a to-do list while it works on a request, a {{ui:graph.steps.title}} panel at the top left of the flow shows each step as done, in progress or pending. The button in its header folds it, and a new request clears the previous request's list. Codex only says whether a step is done, so its first unfinished step shows as the current one. Antigravity has no checklist to show, so its panes have no panel.

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
When a request ends, a result card sums up the result. A request reads completed once Claude's answer ends. While background work it started is still running (including a finished sub-agent it woke up again by sending it a message), or has ended but Claude has not answered its notice yet, the result card is the {{ui:graph.block.awaitingNotice}} block in the waiting colour: under the answer so far, each task reads {{ui:plan.background.awaitingNotice}} or {{ui:plan.background.noticeDelivered}}, with a count such as "Notices 1/2". Meanwhile the pet and the pane header read {{ui:graph.state.awaitingNotice}}. Once every notice has been answered it becomes {{ui:graph.block.result}}. When Claude answers a background task's notice later, that answer is not added to the request's result; every answer opens a new diagram of its own ([Background work](approvals.md#background)). While other background work of the same request is still running, that answer's card is titled "Result so far · background tasks waiting: N", which tells you how many are left. If the result names files, {{ui:graph.resultFiles.openButton}} lists them. Clicking a file path inside a block opens a preview. The latest result card fits the pane; if you resized it yourself, a {{ui:graph.result.fitToWindow}} button appears. For a source or Markdown file the request changed, the preview's head gets a {{ui:files.changes.show}} | {{ui:files.changes.full}} switch and opens on {{ui:files.changes.show}}: only the changed lines with 3 lines around them, old and new line numbers beside them, added lines on green and removed lines on red. The line above says what it compares with: opened from the result files list or a block, the file as it was before that request first changed it ({{ui:files.changes.baseRun}}), else the last commit. With nothing changed there is no switch and the whole file shows.

![[result-card]]
