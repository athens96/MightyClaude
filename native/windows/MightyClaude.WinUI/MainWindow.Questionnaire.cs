using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private sealed class QuestionDraft(string input)
        {
            internal readonly string Input = input;
            internal int Step;
            internal readonly Dictionary<int, HashSet<string>> Picks = [];
            internal readonly HashSet<int> Custom = [];
            internal readonly Dictionary<int, string> Text = [];
            internal bool Sending;
            internal string? Error;
            internal bool Answered(int step) => Picks.GetValueOrDefault(step)?.Count > 0 || Custom.Contains(step) && !string.IsNullOrWhiteSpace(Text.GetValueOrDefault(step));
        }
        private readonly Dictionary<string, QuestionDraft> questionDrafts = [];
        private Border? questionnaireCard;
        private string? questionnaireRequestId, questionnaireOriginalInput;
        private TextBlock? questionnaireWaiting;
        /// <summary>An answer tile's corner (M/UserQuestionnaireCard.swift:207).</summary>
        private const double ChoiceRadius = 11;

        /// <summary>
        /// The question card (M/UserQuestionnaireCard.swift:56-133), one question at a time: the "?" on its amber disc,
        /// the title, where the user is and a dot for each question; the question's header in <c>waitText</c> and
        /// whether one or several answers go, the question in 14.5 bold and its answers as tiles two to a row where
        /// the pane is wide enough; then what happens next with Cancel, Back and Next or Send answers.
        /// </summary>
        private bool TryRenderQuestionnaire(ToolPermissionRequest current, int count, bool preserve)
        {
            if (!current.CanAnswerQuestions || current.ToolName != "AskUserQuestion" || UserQuestionnaire.Parse(current.InputJson) is not { } questionnaire)
            { HideQuestionnaire(); return false; }
            foreach (var stale in questionDrafts.Keys.Where(key => !toolPermissions.Any(p => p.Id == key && p.State == "pending")).ToArray()) questionDrafts.Remove(stale);
            if (!questionDrafts.TryGetValue(current.Id, out var draft) || draft.Input != current.InputJson) questionDrafts[current.Id] = draft = new(current.InputJson);
            if (preserve && questionnaireCard is not null && questionnaireRequestId == current.Id && questionnaireOriginalInput == current.InputJson)
            {
                UpdateQuestionnaireWaiting(count);
                return true;
            }
            questionnaireRequestId = current.Id; questionnaireOriginalInput = current.InputJson;
            var total = questionnaire.Questions.Count;
            draft.Step = Math.Clamp(draft.Step, 0, total - 1);
            var step = draft.Step; var question = questionnaire.Questions[step];
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2);
            var body = new StackPanel { Spacing = 10 };

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var lead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            lead.Children.Add(WaitBadge(new TextBlock { Text = "?", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold, Foreground = b.Brush(DesignToken.OnWait), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) }));
            lead.Children.Add(new TextBlock { Text = Locale.Get("phone.questionnaire.title"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
            var progress = new TextBlock
            {
                Text = total > 1 ? Locale.Get("phone.questionnaire.progress", new Dictionary<string, string> { ["current"] = (step + 1).ToString(), ["total"] = total.ToString() }) : Locale.Get("questionnaire.oneQuestion"),
                FontSize = 12, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(progress, FontNumeralAlignment.Tabular);
            AutomationProperties.SetAutomationId(progress, "questionnaire-progress"); lead.Children.Add(progress);
            // A dot for each question (M/UserQuestionnaireCard.swift:137-153): amber for the one on screen, soft amber once answered, the track's grey before.
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            for (var index = 0; index < total; index++)
            {
                var target = index; var now = index == step; var done = draft.Answered(index);
                var dot = Button((index + 1).ToString(), () => { draft.Step = target; RenderToolPermissions(); return Task.CompletedTask; });
                var figure = new TextBlock { Text = (index + 1).ToString(), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = b.Brush(now ? DesignToken.OnWait : done ? DesignToken.WaitText : DesignToken.Ink2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(figure, FontNumeralAlignment.Tabular);
                dot.Content = figure; dot.Width = 18; dot.Height = 18; dot.MinWidth = 0; dot.MinHeight = 0; dot.Padding = new Thickness(0); dot.BorderThickness = new Thickness(0); dot.CornerRadius = new CornerRadius(9);
                var fill = b.Brush(now ? DesignToken.Wait : done ? DesignToken.WaitSoft : DesignToken.Track);
                owner.PaintPlainButton(dot, fill, fill);
                dot.IsEnabled = !draft.Sending && (index <= step || Enumerable.Range(0, index).All(draft.Answered));
                if (!dot.IsEnabled) dot.Opacity = DisabledDim;
                AutomationProperties.SetName(dot, Locale.Get("phone.questionnaire.jump", new Dictionary<string, string> { ["index"] = (index + 1).ToString() }));
                AutomationProperties.SetAutomationId(dot, "questionnaire-step-" + index);
                dots.Children.Add(dot);
            }
            if (total > 1) lead.Children.Add(dots);
            header.Children.Add(lead);
            questionnaireWaiting = new TextBlock { FontSize = 12, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center }; UpdateQuestionnaireWaiting(count);
            Grid.SetColumn(questionnaireWaiting, 1); header.Children.Add(questionnaireWaiting);
            body.Children.Add(header);

            var section = new StackPanel { Spacing = 8, IsHitTestVisible = !draft.Sending, Margin = new Thickness(0, 2, 5, 2) };
            var kind = new Grid { ColumnSpacing = 6 };
            kind.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); kind.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            kind.Children.Add(new TextBlock { Text = question.Header, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = b.Brush(DesignToken.WaitText), TextWrapping = TextWrapping.Wrap });
            var mode = new TextBlock { Text = Locale.Get(question.MultiSelect ? "phone.questionnaire.multiple" : "phone.questionnaire.single"), FontSize = 10.5, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(mode, 1); kind.Children.Add(mode); section.Children.Add(kind);
            section.Children.Add(new TextBlock { Text = question.Question, FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ink, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            var picks = draft.Picks.GetValueOrDefault(step) ?? []; draft.Picks[step] = picks;
            // Two tiles a row where the pane is wide enough, one where it is not (M/UserQuestionnaireCard.swift:167).
            var choices = new AdaptiveGridPanel { Minimum = 210, Gap = 8 };
            for (var index = 0; index < question.Options.Count; index++)
            {
                var option = question.Options[index];
                var choice = QuestionnaireChoice(option.Label, option.Description, option.Description, picks.Contains(option.Label), question.MultiSelect, () =>
                {
                    if (question.MultiSelect) { if (!picks.Remove(option.Label)) picks.Add(option.Label); }
                    else { picks.Clear(); picks.Add(option.Label); draft.Custom.Remove(step); }
                    draft.Error = null; RenderToolPermissions();
                });
                choice.IsEnabled = !draft.Sending; AutomationProperties.SetAutomationId(choice, $"questionnaire-option-{step}-{index}"); choices.Children.Add(choice);
            }
            var custom = QuestionnaireChoice(Locale.Get("phone.questionnaire.custom"), "", Locale.Get("phone.questionnaire.customHint"), draft.Custom.Contains(step), question.MultiSelect, () =>
            {
                if (!draft.Custom.Remove(step)) { draft.Custom.Add(step); if (!question.MultiSelect) picks.Clear(); }
                draft.Error = null; RenderToolPermissions();
            });
            custom.IsEnabled = !draft.Sending; AutomationProperties.SetAutomationId(custom, "questionnaire-custom-" + step); choices.Children.Add(custom); section.Children.Add(choices);
            Button? next = null;
            bool ValidAnswers()
            {
                try { questionnaire.ValidateAnswers(QuestionnaireAnswers(questionnaire, draft)); return true; } catch (ArgumentException) { return false; }
            }
            if (draft.Custom.Contains(step))
            {
                // AppKit's placeholder is the tertiary ink (M/UserQuestionnaireCard.swift:191).
                var text = new TextBox { AcceptsReturn = true, Text = draft.Text.GetValueOrDefault(step) ?? "", PlaceholderText = Locale.Get("phone.questionnaire.customPlaceholder"), PlaceholderForeground = owner.brushes.Tertiary, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 100, MaxLength = 8192, IsEnabled = !draft.Sending };
                AutomationProperties.SetName(text, Locale.Get("phone.questionnaire.customLabel", new Dictionary<string, string> { ["header"] = question.Header }));
                AutomationProperties.SetAutomationId(text, "questionnaire-custom-text-" + step);
                text.TextChanged += (_, _) => { draft.Text[step] = text.Text; if (next is not null) next.IsEnabled = !draft.Sending && (step + 1 < total ? draft.Answered(step) : ValidAnswers()); };
                section.Children.Add(text);
            }
            body.Children.Add(new ScrollViewer { Content = section, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled });
            if (draft.Error is { } error) body.Children.Add(new TextBlock { Text = error, Foreground = b.Brush(DesignToken.ErrText), FontSize = 11, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

            var last = step + 1 == total;
            // What happens next, then Cancel, Back and the ink button that goes forward (M/UserQuestionnaireCard.swift:99-126).
            var footer = new Grid { ColumnSpacing = 8 };
            footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            footer.Children.Add(new TextBlock { Text = Locale.Get(last ? "phone.questionnaire.hintSubmit" : "phone.questionnaire.hintNext"), FontSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsHitTestVisible = !draft.Sending };
            if (draft.Sending)
            {
                var sending = new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, Foreground = ink2 };
                AutomationProperties.SetAutomationId(sending, "questionnaire-sending"); actions.Children.Add(sending);
            }
            var cancel = Button(Locale.Get("phone.questionnaire.cancel"), () => { OnPermissionDeny(); return Task.CompletedTask; }); PaintCardButton(cancel, prominent: false);
            AutomationProperties.SetAutomationId(cancel, "questionnaire-cancel"); actions.Children.Add(cancel);
            if (step > 0)
            {
                var back = Button(Locale.Get("phone.questionnaire.back"), () => { draft.Step--; RenderToolPermissions(); return Task.CompletedTask; }); PaintCardButton(back, prominent: false);
                AutomationProperties.SetAutomationId(back, "questionnaire-back"); actions.Children.Add(back);
            }
            next = Button(Locale.Get(last ? "phone.questionnaire.submit" : "phone.questionnaire.next"), () =>
            {
                if (draft.Sending) return Task.CompletedTask;
                if (!last) { if (draft.Answered(step)) draft.Step++; RenderToolPermissions(); return Task.CompletedTask; }
                try
                {
                    var answers = QuestionnaireAnswers(questionnaire, draft); questionnaire.ValidateAnswers(answers); draft.Sending = true;
                    owner.service.AnswerQuestionnaire(id, current.Id, answers);
                    ReceiveToolPermission(current with { State = "answered" });
                }
                catch (Exception ex) { draft.Sending = false; draft.Error = ex.Message; RenderToolPermissions(); }
                return Task.CompletedTask;
            });
            next.IsEnabled = !draft.Sending && (last ? ValidAnswers() : draft.Answered(step)); PaintCardButton(next, prominent: true);
            AutomationProperties.SetAutomationId(next, last ? "questionnaire-submit" : "questionnaire-next"); actions.Children.Add(next);
            Grid.SetColumn(actions, 1); footer.Children.Add(actions); body.Children.Add(footer);
            if (questionnaireCard is not null) toolPermissionHost.Children.Remove(questionnaireCard);
            if (toolPermissionHost.Children.Count > 0) toolPermissionHost.Children[0].Visibility = Visibility.Collapsed;
            questionnaireCard = WaitCard(body);
            AutomationProperties.SetAutomationId(questionnaireCard, "questionnaire-" + current.Id);
            toolPermissionHost.Children.Add(questionnaireCard); toolPermissionHost.Visibility = Visibility.Visible;
            return true;
        }

        /// <summary>
        /// One answer tile (M/UserQuestionnaireCard.swift:204-232): the radio or check mark at 14, the answer in 13 bold over
        /// its note in 11.5 <c>ink2</c>, padded h11 v9 and at least 50 high at radius 11 — <c>card</c> with a 1pt <c>line</c>,
        /// or <c>waitSoft</c> with a 2pt <c>wait</c> edge once chosen. The look is on the tile's face; the toggle draws nothing.
        /// </summary>
        private ToggleButton QuestionnaireChoice(string title, string detail, string spoken, bool selected, bool multiple, Action action)
        {
            var b = owner.brushes; var edge = selected ? DesignMetrics.Stroke.Active : DesignMetrics.Stroke.Line;
            var mark = ComposerGlyph.Icon(multiple ? (selected ? "" : "") : (selected ? "" : ""), 14, 17, 17).Ink(b.Brush(selected ? DesignToken.WaitText : DesignToken.Ink2));
            mark.View.VerticalAlignment = VerticalAlignment.Top; mark.View.Margin = new Thickness(0, 1, 0, 0);
            var label = new StackPanel { Spacing = 3 };
            label.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap });
            if (detail.Length > 0) label.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = b.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.Children.Add(mark.View); Grid.SetColumn(label, 1); row.Children.Add(label);
            var face = new Border
            {
                Child = row, MinHeight = 50, CornerRadius = new CornerRadius(ChoiceRadius), BorderThickness = new Thickness(edge), Padding = new Thickness(11 - edge, 9 - edge, 11 - edge, 9 - edge),
                Background = b.Brush(selected ? DesignToken.WaitSoft : DesignToken.Card), BorderBrush = b.Brush(selected ? DesignToken.Wait : DesignToken.Line),
            };
            var button = new ToggleButton
            {
                Content = face, IsChecked = selected, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(ChoiceRadius),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            PaintPlainToggle(button);
            AutomationProperties.SetName(button, title + (spoken.Length > 0 ? ". " + spoken : "")); button.Click += (_, _) => action(); return button;
        }

        private static IReadOnlyDictionary<string, UserQuestionAnswer> QuestionnaireAnswers(UserQuestionnaire questionnaire, QuestionDraft draft) =>
            questionnaire.Questions.Select((q, index) => (q, index)).ToDictionary(pair => pair.q.Question,
                pair => new UserQuestionAnswer(pair.q.Options.Where(o => draft.Picks.GetValueOrDefault(pair.index)?.Contains(o.Label) == true).Select(o => o.Label).ToList(), draft.Custom.Contains(pair.index) ? draft.Text.GetValueOrDefault(pair.index) : null), StringComparer.Ordinal);

        private void UpdateQuestionnaireWaiting(int count)
        {
            if (questionnaireWaiting is null) return;
            questionnaireWaiting.Text = Locale.Get("phone.questionnaire.waiting", new Dictionary<string, string> { ["count"] = count.ToString() });
            questionnaireWaiting.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HideQuestionnaire()
        {
            questionnaireRequestId = questionnaireOriginalInput = null; questionnaireWaiting = null;
            if (questionnaireCard is not null) { toolPermissionHost.Children.Remove(questionnaireCard); questionnaireCard = null; }
            if (toolPermissionHost.Children.Count > 0) toolPermissionHost.Children[0].Visibility = Visibility.Visible;
            foreach (var stale in questionDrafts.Keys.Where(key => !toolPermissions.Any(p => p.Id == key && p.State == "pending")).ToArray()) questionDrafts.Remove(stale);
        }
    }
}
