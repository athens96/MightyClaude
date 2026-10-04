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
            draft.Step = Math.Clamp(draft.Step, 0, questionnaire.Questions.Count - 1);
            var step = draft.Step; var question = questionnaire.Questions[step];
            var body = new StackPanel { Spacing = 10 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock { Text = "?", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            header.Children.Add(new TextBlock { Text = Locale.Get("phone.questionnaire.title"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = Locale.Get("phone.questionnaire.progress", new Dictionary<string, string> { ["current"] = (step + 1).ToString(), ["total"] = questionnaire.Questions.Count.ToString() }), FontSize = 11, Opacity = .7 });
            questionnaireWaiting = new TextBlock { FontSize = 11 }; UpdateQuestionnaireWaiting(count); header.Children.Add(questionnaireWaiting);
            body.Children.Add(header);
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            for (var index = 0; index < questionnaire.Questions.Count; index++)
            {
                var target = index;
                var dot = Button((index + 1).ToString(), () => { draft.Step = target; RenderToolPermissions(); return Task.CompletedTask; });
                dot.IsEnabled = !draft.Sending && (index <= step || Enumerable.Range(0, index).All(draft.Answered)); dot.MinWidth = 24; dot.MinHeight = 24; dot.Padding = new(4, 1, 4, 1);
                if (index == step) dot.Background = new SolidColorBrush(Colors.Goldenrod);
                AutomationProperties.SetName(dot, Locale.Get("phone.questionnaire.jump", new Dictionary<string, string> { ["index"] = (index + 1).ToString() }));
                dots.Children.Add(dot);
            }
            if (questionnaire.Questions.Count > 1) body.Children.Add(dots);
            var section = new StackPanel { Spacing = 8, IsHitTestVisible = !draft.Sending };
            section.Children.Add(new TextBlock { Text = question.Header + " · " + Locale.Get(question.MultiSelect ? "phone.questionnaire.multiple" : "phone.questionnaire.single"), FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            section.Children.Add(new TextBlock { Text = question.Question, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            var picks = draft.Picks.GetValueOrDefault(step) ?? []; draft.Picks[step] = picks;
            var choices = new PillWrapPanel();
            for (var index = 0; index < question.Options.Count; index++)
            {
                var option = question.Options[index];
                var choice = QuestionnaireChoice(option.Label, option.Description, picks.Contains(option.Label), () =>
                {
                    if (question.MultiSelect) { if (!picks.Remove(option.Label)) picks.Add(option.Label); }
                    else { picks.Clear(); picks.Add(option.Label); draft.Custom.Remove(step); }
                    draft.Error = null; RenderToolPermissions();
                });
                choice.IsEnabled = !draft.Sending; AutomationProperties.SetAutomationId(choice, $"questionnaire-option-{step}-{index}"); choices.Children.Add(choice);
            }
            var custom = QuestionnaireChoice(Locale.Get("phone.questionnaire.custom"), Locale.Get("phone.questionnaire.customHint"), draft.Custom.Contains(step), () =>
            {
                if (!draft.Custom.Remove(step)) { draft.Custom.Add(step); if (!question.MultiSelect) picks.Clear(); }
                draft.Error = null; RenderToolPermissions();
            });
            custom.IsEnabled = !draft.Sending; choices.Children.Add(custom); section.Children.Add(choices);
            Button? next = null;
            bool ValidAnswers()
            {
                try { questionnaire.ValidateAnswers(QuestionnaireAnswers(questionnaire, draft)); return true; } catch (ArgumentException) { return false; }
            }
            if (draft.Custom.Contains(step))
            {
                var text = new TextBox { AcceptsReturn = true, Text = draft.Text.GetValueOrDefault(step) ?? "", PlaceholderText = Locale.Get("phone.questionnaire.customPlaceholder"), TextWrapping = TextWrapping.Wrap, MaxHeight = 100, MaxLength = 8192, IsEnabled = !draft.Sending };
                AutomationProperties.SetName(text, Locale.Get("phone.questionnaire.customLabel", new Dictionary<string, string> { ["header"] = question.Header }));
                text.TextChanged += (_, _) => { draft.Text[step] = text.Text; if (next is not null) next.IsEnabled = !draft.Sending && (step + 1 < questionnaire.Questions.Count ? draft.Answered(step) : ValidAnswers()); };
                section.Children.Add(text);
            }
            body.Children.Add(new ScrollViewer { Content = section, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            if (draft.Error is { } error) body.Children.Add(new TextBlock { Text = error, Foreground = new SolidColorBrush(Colors.OrangeRed), FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = !draft.Sending };
            actions.Children.Add(Button(Locale.Get("phone.questionnaire.cancel"), () => { OnPermissionDeny(); return Task.CompletedTask; }));
            if (step > 0) actions.Children.Add(Button(Locale.Get("phone.questionnaire.back"), () => { draft.Step--; RenderToolPermissions(); return Task.CompletedTask; }));
            var last = step + 1 == questionnaire.Questions.Count;
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
            next.IsEnabled = !draft.Sending && (last ? ValidAnswers() : draft.Answered(step));
            AutomationProperties.SetAutomationId(next, last ? "questionnaire-submit" : "questionnaire-next"); actions.Children.Add(next); body.Children.Add(actions);
            if (questionnaireCard is not null) toolPermissionHost.Children.Remove(questionnaireCard);
            if (toolPermissionHost.Children.Count > 0) toolPermissionHost.Children[0].Visibility = Visibility.Collapsed;
            questionnaireCard = new Border { Child = body, Padding = new(12), CornerRadius = new(12), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Colors.Goldenrod), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 180, 50)) };
            AutomationProperties.SetAutomationId(questionnaireCard, "questionnaire-" + current.Id);
            toolPermissionHost.Children.Add(questionnaireCard); toolPermissionHost.Visibility = Visibility.Visible;
            return true;
        }

        private static ToggleButton QuestionnaireChoice(string title, string detail, bool selected, Action action)
        {
            var label = new StackPanel { Spacing = 3 };
            label.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (detail.Length > 0) label.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = .75, TextWrapping = TextWrapping.Wrap });
            var button = new ToggleButton { Content = label, IsChecked = selected, Width = 210, MinHeight = 50, Padding = new(10, 8, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(button, title + (detail.Length > 0 ? ". " + detail : "")); button.Click += (_, _) => action(); return button;
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
