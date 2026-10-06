---
title: Styles
order: 4
section: styles
---
A style is a ready-made way of working: it comes with phases and buttons, so a long task follows a set order. Without a style you send requests freely, as usual (CLI).

## Pick a style {#pick}
Styles work in Claude panes on this Mac, in the Mighty view.
1. Switch the Claude pane to the {{ui:graph.view.mighty}} view.
2. Open the style menu ({{ui:guidedPanel.stylesMenuAccessibility}}) above the composer.
3. Pick a style. Pick CLI to go back to free requests.

Each pane remembers its style. It can't be changed while a task runs. If the style needs a plugin or tool you don't have, an {{ui:guidedPanel.installButton}} button appears. It fills the install command into a new terminal pane; you press {{kbd:Enter}} yourself.

> [!note]
> The bundled styles' phase names and buttons are in Korean only for now.

## Bundled styles {#bundled}
| Style | What it does |
|---|---|
| Ouroboros | Interviews you to pin down the goal, then goes seed → run → evaluate → evolve. Your first request becomes the interview. Needs the Ouroboros plugin and `uvx`. |
| Paperthin | Buttons for small skills that remove rather than add. Your text is sent as you typed it. |
| Superpowers | Goes brainstorm → plan → execute → finish. The phase and progress bar move on their own from the checkboxes in the plan file. |
| Claude plan (클러드 플랜) | Every new request starts in Claude's plan mode, and runs once you approve the plan card. Widgets show checklist progress and background tasks. |

## The plan card {#plan-card}
When Claude finishes a plan in plan mode (Plan mode, or the Claude plan style), a plan card appears.

![[plan-card]]

1. Read the plan. Click {{ui:plan.card.expand}} to read it as a full document.
2. Choose one:
   - {{ui:plan.card.approveAuto}}: approve, and file edits go ahead without asking.
   - {{ui:plan.card.approveConfirm}}: approve, and confirm each action.
   - {{ui:plan.card.revise}}: write what to change and click {{ui:plan.card.reviseSend}}; Claude plans again.
   - {{ui:plan.card.cancel}}: turn the plan down and stop this task.

Answered plans stay in the history so you can look at them again.

![[plan-document]]

## Add other styles {#custom}
Besides the bundled styles you can use styles from files.
1. In {{ui:menu.settings}} → {{ui:settings.nav.styles}}, click {{ui:settings.styles.registerButton}}. Styles in a repository's `.claude/mighty-styles/` folder are found on their own. If the list looks wrong, click {{ui:settings.styles.rescanButton}}.
2. A new style must be reviewed once. Use {{ui:settings.styles.viewButton}} to check its source, the tools it allows automatically, its install command and every button.
3. If it looks right, click {{ui:settings.styles.allowButton}}.

If the style file changes, you are asked again. To take the approval back, click {{ui:settings.styles.revokeButton}} in the same place.

> [!warning]
> A style that allows tools automatically runs those tools without asking. Allow it only if you trust where it came from.
