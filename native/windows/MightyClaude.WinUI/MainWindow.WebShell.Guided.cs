using MightyClaude.Core;
using Microsoft.UI.Xaml;

namespace MightyClaude.WinUI;

/// <summary>
/// The style's guide panel in the web page (native/windows/web/src/composer/Guided.tsx): the same decisions
/// <see cref="PaneView.RenderGuidedStyle"/> makes for the WinUI <c>guidedBody</c> — the strip of a style that still needs a
/// yes, the phase bar, what is missing, the state widgets, the group map, the action chips (or a spinner), the reset chip,
/// the guidance line and the Enter prefix — sent in the composer's state; its buttons come back through <c>guidedAction</c>.
/// A waiting question or plan answer is drawn by the page's own question and plan cards (<see cref="PaneView.WebCardRequests"/>).
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterGuidedMethods(WebBridge bridge)
    {
        // One method for the panel's buttons: the action chips, a group tile, the reset chip, install, recheck and 내용 보기.
        bridge.Register("guidedAction", async args =>
        {
            var view = WebPane(args.Text("paneId"));
            await view.WebGuidedAction(args.Text("what") ?? "", args.Text("id"));
            return null;
        });
    }

    private sealed partial class PaneView
    {
        /// <summary>
        /// What the page's panel shows (M/GuidedPanel.swift:94-127, <see cref="RenderGuidedStyle"/>); null while the style row is
        /// hidden or no style is chosen. Read live from the same evaluator calls, never from the WinUI elements.
        /// </summary>
        private object? WebGuided()
        {
            if (styleHost.Visibility != Visibility.Visible) return null;
            var pane = Session;
            if (activeStyle is not { } style)
            {
                if (pane.MightyStyle is null) return null;
                // The pane aims at a style that has not been said yes to (M/GuidedActionChip.swift:62-80).
                var pending = styleRegistry?.Styles.FirstOrDefault(s => s.Id == pane.MightyStyle);
                return new
                {
                    approval = new
                    {
                        name = pending?.Manifest.Name ?? pane.MightyStyle,
                        source = pending is { Source: not "bundled" } ? StylePresentation.Source(pending.Source) : null,
                        canView = pending is not null && pending.Approval != "revoked" && styleRegistry?.TrustLocked != true,
                    },
                };
            }
            var evaluator = style.Evaluator; var phase = evaluator.EffectivePhase(GuidedPhase(style), styleStartingNew);
            var busy = pane.Status == "running" || starting; var job = evaluator.JobOpen(pane);
            var ordered = style.Manifest.Phases.OrderBy(p => p.Order).ToArray();
            var phases = ordered.Select(p => new { id = p.Id, title = p.Title, state = p.Id == phase?.Id ? "current" : phase is not null && p.Order <= phase.Order ? "reached" : "ahead" }).ToArray();
            var enter = WebEnterPrefix(style, phase);
            // A waiting question or the plan's answers always win (M/GuidedPanel.swift:112-119): the page draws them as its cards.
            if (GuidedQuestion is not null || PanelPlan is not null) return new { phases, waiting = true, enter };

            object? setup = stylePrerequisites is { Ready: false } prerequisites
                ? new { missing = prerequisites.Missing, hint = prerequisites.Hint is { Length: > 0 } hint ? hint : null, installCommand = prerequisites.InstallCommand }
                : null;
            var now = DateTimeOffset.UtcNow;
            var widgets = new List<object>();
            foreach (var widget in LiveStyleReading(style).Widgets)
            {
                if (widget.Kind == "taskList")
                {
                    var tasks = StylePresentation.Tasks(widget);
                    if (tasks.Count > 0) widgets.Add(new { kind = "taskList", tasks = tasks.Select(t => new { text = t.Text, detail = t.KindTitle + " · " + t.StatusTitle + (t.Elapsed(now) is { Length: > 0 } elapsed ? " · " + elapsed : ""), running = t.Running }).ToArray() });
                }
                else if (widget.Kind == "progressBar" && StylePresentation.Progress(widget) is { } progress)
                    widgets.Add(new { kind = "progressBar", fraction = Math.Clamp(progress.Fraction, 0, 1), text = progress.Text });
                else if (widget.Kind == "list")
                {
                    var items = (widget.Items ?? []).Select(StylePresentation.Inline).Where(v => v.Length > 0).Take(20).ToArray();
                    if (items.Length > 0) widgets.Add(new { kind = "list", items });
                }
                else if (!string.IsNullOrEmpty(widget.Text)) widgets.Add(new { kind = "text", text = StylePresentation.Inline(widget.Text) });
            }
            var group = style.Manifest.Groups.FirstOrDefault(g => g.Id == styleGroup) ?? evaluator.InitialGroup(NoCapabilityStates);
            var groups = evaluator.DrawsGroupMap
                ? style.Manifest.Groups.Select(g => new { id = g.Id, title = g.Title, axis = g.Axis ?? "", help = g.Question ?? g.Title, chosen = g.Id == group?.Id }).ToArray()
                : null;
            var spinner = busy && evaluator.DrawsPhaseProgress && !job;
            object[] actions = [];
            object? reset = null;
            if (!spinner)
            {
                var visible = evaluator.VisibleActions(phase, group, busy, job, styleStartingNew); var recommended = evaluator.RecommendedAction(NoCapabilityStates);
                var prominent = job || evaluator.AtStart(phase) || evaluator.DrawsPhaseProgress ? visible.FirstOrDefault()?.Id : null;
                var draft = input.Text;
                actions = visible.Select(action =>
                {
                    var flags = action.Flags ?? [];
                    return (object)new
                    {
                        id = action.Id, title = action.Title,
                        mark = action.Glyph is { Length: > 0 } glyph ? glyph : StylePresentation.Icon(action.Icon) is { Length: > 0 } icon ? icon : null,
                        userInvoked = flags.Contains("userInvoked"), readOnly = flags.Contains("readOnly"),
                        emphasised = action.Id == prominent || action.Id == recommended,
                        tint = WebTint(action.Tint),
                        enabled = !action.RequiresText || !string.IsNullOrWhiteSpace(draft),
                        help = StylePresentation.ActionHelp(action),
                    };
                }).ToArray();
                // The reset chip: back to the start row, or out of it again (M/GuidedPanel.swift:349-362).
                if (styleStartingNew) reset = new { kind = "cancel", title = Locale.Get("guidedPanel.cancelButton"), help = (string?)null };
                else if (!evaluator.AtStart(phase) && evaluator.ResetTitle is { } title) reset = new { kind = "new", title, help = (string?)Locale.Get("guidedPanel.resetHelp") };
            }
            return new
            {
                phases, waiting = false, enter, setup,
                widgets = widgets.Count > 0 ? widgets : null,
                groups, groupQuestion = evaluator.DrawsGroupMap && group?.Question is { Length: > 0 } question ? question : null,
                spinner, actions, catalogue = evaluator.DrawsGroupMap, reset,
                guidance = evaluator.Guidance(phase, busy, job),
            };
        }

        /// <summary>The command Enter is about to send in place of the draft (<see cref="RefreshStyleComposer"/>'s chip), or null.</summary>
        private string? WebEnterPrefix(RegisteredStyle style, StylePhase? phase)
        {
            var pane = Session;
            if (GuidedQuestion is not null) return null;
            var actionId = style.Evaluator.RewriteAction(input.Text, phase, pendingAttachments.Count > 0, pane.Status == "running" || starting, pane.Logs.Any(l => l.Kind == "user") || pane.GraphRuns is { Count: > 0 }, styleStartingNew);
            return actionId is null ? null : style.Manifest.Actions.First(a => a.Id == actionId).Prompt("").Trim();
        }

        /// <summary>A manifest tint as the page's ink variable (<see cref="StyleTint"/>'s tokens).</summary>
        private static string WebTint(string? tint) => tint switch
        {
            "purple" => "agent-text", "teal" => "task-text", "indigo" => "question-text", "mint" => "compact-text", "orange" => "steer-text",
            "green" => "done-text", "red" => "err-text", "secondary" => "ink2", _ => "accent",
        };

        /// <summary>A panel button, run as the WinUI one runs it; the page hears of the change with the next composer push.</summary>
        internal async Task WebGuidedAction(string what, string? value)
        {
            switch (what)
            {
                case "action":
                    if (activeStyle is not { } style || value is null || style.Manifest.Actions.All(a => a.Id != value)) throw new ArgumentException("Unknown action.");
                    await InvokeStyleAction(value);
                    break;
                case "group":
                    if (activeStyle is not { } grouped || grouped.Manifest.Groups.All(g => g.Id != value)) throw new ArgumentException("Unknown group.");
                    styleGroup = value; RenderGuidedStyle();
                    break;
                case "reset":
                    styleStartingNew = value == "new"; RenderGuidedStyle(); RefreshDraftBlock();
                    break;
                case "install":
                    if (activeStyle is { } installed && stylePrerequisites?.InstallCommand is { } command) await InstallStyle(installed, command);
                    break;
                case "recheck":
                    await LoadStyles();
                    break;
                case "view":
                    if (styleRegistry?.Styles.FirstOrDefault(s => s.Id == Session.MightyStyle) is { } pending) await ChooseStyle(pending);
                    break;
                default: throw new ArgumentException("Unknown guided action.");
            }
            if (QueuePaneAlive) RefreshComposerState();
        }
    }
}
