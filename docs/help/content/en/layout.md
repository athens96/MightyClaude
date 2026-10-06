---
title: The window
order: 2
section: layout
---
The window has a sidebar on the left and a work area on the right. In the work area you can stack panes as tabs or split them side by side or top and bottom.

![[overview]]

## Sidebar {#sidebar}
From the top, the sidebar has:
- {{ui:phone.dashboard.title}}: the state of every pane in every workspace, in one place.
- {{ui:sidebar.searchPlaceholder}}: press {{kbd:⌘K}} to jump to the search field.
- The {{ui:sidebar.workspacesHeader}} list: click a workspace to switch to it. Click the arrow beside it to show or hide its panes.
- At the bottom: the theme button (dark or light) and the settings button.

When a workspace is expanded, its last row is {{ui:workspace.addPane}}. Right-click a pane row for {{ui:menu.rename}} and {{ui:menu.closePane}}.

### Resize the sidebar {#sidebar-resize}
Drag the border between the sidebar and the work area to change the sidebar's width ({{ui:sidebar.resize}}).
- The border turns the accent colour when you point at it. Drag it left or right to set the width. The width stays within set limits and is kept after you reopen the app.
- Double-click the border to go back to the default width.
- Drag the border all the way to the left to collapse the sidebar. When you bring it back, it has the width it had before the drag.

### Collapse the sidebar {#sidebar-collapse}
Collapse the sidebar to give the work area more room.
1. Click the sidebar button at the top-left of the work area's header or press {{kbd:⌃⌘S}}. The {{ui:phone.dashboard.title}} screen and the first screen have the same button in the same place. The View menu in the menu bar also has {{ui:sidebar.collapse}}.
2. Use the same button or {{kbd:⌃⌘S}} to bring it back ({{ui:sidebar.expand}}).

The sidebar stays collapsed after you reopen the app. Pressing {{kbd:⌘K}} while it is collapsed opens it and jumps to search.

![[sidebar-collapsed]]

## Work status {#dashboard}
Click {{ui:phone.dashboard.title}} at the top of the sidebar to see the panes of all workspaces, sorted by state.
- The tiles at the top count {{ui:phone.dashboard.stat.running}}, {{ui:phone.dashboard.stat.waiting}} and {{ui:phone.dashboard.stat.done}}.
- The {{ui:dashboard.usage.title}} card shows your account usage. The limits are shared with other apps and sessions on the same account.
- Each workspace has a {{ui:files.pane.title}} button and an {{ui:workspace.addPane}} menu.
- Click a card to go to that pane. Choosing a workspace or pane leaves the dashboard.

![[dashboard]]

## Add a pane {#add-pane}
A pane is an AI agent (Claude, Codex or Gemini), a terminal, a browser or a files pane.

1. Click {{ui:workspace.addPane}} in the sidebar.
2. Pick what to add: a Claude, Codex or Gemini pane, {{ui:workspace.newTerminal}} or {{ui:browser.newTab}}. Another folder opens with {{ui:menu.openProject}} in the same menu.
3. A new AI pane takes the model, thinking effort, permission and view of the most recently used pane of the same agent. The conversation starts fresh.

Shortcuts: {{ui:menu.newClaudePane}} is {{kbd:⌘N}}, {{ui:menu.addTerminalPane}} is {{kbd:⌘T}} and {{ui:menu.showFiles}} is {{kbd:⇧⌘E}}.

> [!note]
> Codex and Gemini panes carry a {{ui:badge.beta}} label.

### Continue an earlier session {#resume}
When you add a Claude or Codex pane and this folder has earlier sessions, you are asked what to do.
1. Click {{ui:resume.choice.startNew}} for a new conversation, or {{ui:resume.choice.resume}} for an earlier one.
2. Pick a session in {{ui:resume.title}}. Use {{ui:resume.search}} to find it.
3. The new pane continues that session.

![[resume-choice]]

![[resume-list]]

## Tabs and splits {#tabs-splits}
- Drag a tab onto the middle of another group to merge it as a tab ({{ui:layout.drop.merge}}).
- Drop it on the left, right, top or bottom edge of a group to split that way.
- Drag a divider to resize; double-click it to split evenly.
- Press {{kbd:Esc}} while dragging to cancel.
- Right-click a tab for {{ui:menu.rename}}, {{ui:menu.focusPane}} and {{ui:menu.closeTab}}.

The layout is saved per workspace. Switching workspaces keeps each layout as it was.

### The pane menu {#pane-menu}
The … button in a pane's header has:
- {{ui:menu.rename}}
- {{ui:menu.focusPane}}: show only this pane, large. Click again for {{ui:pane.menu.restoreLayout}}.
- {{ui:pane.menu.copyLog}}
- {{ui:pane.menu.newConversation}}: the next request no longer continues the earlier conversation.
- {{ui:menu.closePane}}

## Keyboard shortcuts {#shortcuts}
| Shortcut | Action |
|---|---|
| {{kbd:⌘O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{ui:menu.settings}} |
| {{kbd:Enter}} | Send the request |
| {{kbd:⇧Enter}} | New line |
| {{kbd:⌘Enter}} | Send to the running Claude now |
| {{kbd:Esc}} | Close a sheet or the slash list |
