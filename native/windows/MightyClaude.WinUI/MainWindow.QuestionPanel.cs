using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>A GUI smoke's stand-in for <see cref="DesktopService.AnswerQuestionnaire"/> from a style's panel; null answers through the service.</summary>
    internal Action<string, string, IReadOnlyDictionary<string, UserQuestionAnswer>>? smokeQuestionAnswerer;

    private sealed partial class PaneView
    {
        /// <summary>Where the user is in the question the style's panel shows, and what its last answer did (M/AppStore.swift guidedProgress).</summary>
        private QuestionnaireProgress? guidedProgress;
        private string? guidedQuestionInput, guidedQuestionError;
        /// <summary>The question panel's corner and its tiles' (M/AgentQuestionPanel.swift:37, 66).</summary>
        private const double QuestionPanelRadius = 14, QuestionTileRadius = DesignMetrics.Radius.Entry;

        /// <summary>The agent's question a pane in a style answers from the style's panel (<see cref="GuidedQuestions.Waiting"/>); none without a style.</summary>
        private (ToolPermissionRequest Request, UserQuestionnaire Questionnaire)? GuidedQuestion => activeStyle is null ? null : GuidedQuestions.Waiting(toolPermissions);

        /// <summary>What the permission card shows: under a style, every waiting request but the question its panel answers (M/ToolPermissionBar.swift:14-17).</summary>
        private List<ToolPermissionRequest> CardRequests => activeStyle is null ? toolPermissions.Where(p => p.State == "pending").ToList() : GuidedQuestions.Visible(toolPermissions).ToList();

        /// <summary>The progress kept for <paramref name="request"/>; another request, or the same one asked anew, starts over.</summary>
        private QuestionnaireProgress GuidedProgress(ToolPermissionRequest request)
        {
            var key = request.RunId + "|" + request.Id;
            if (guidedProgress?.RequestKey != key || guidedQuestionInput != request.InputJson) { guidedProgress = new(key); guidedQuestionInput = request.InputJson; guidedQuestionError = null; }
            return guidedProgress;
        }

        /// <summary>
        /// The agent's question in the style's panel (M/AgentQuestionPanel.swift:17-67), one question at a time on the amber
        /// wait card: the "?" disc, the question's header, where the user is, Back and 답하지 않기; the question in 13.5 bold;
        /// its answers as tiles two to a row where the pane is wide enough — a single-choice tile answers at a tap, a
        /// multi-choice one toggles; then how to answer, and 선택 완료 once something is picked. Typed text answers on Enter.
        /// </summary>
        private Border QuestionPanel(ToolPermissionRequest request, UserQuestionnaire questionnaire)
        {
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2);
            var progress = GuidedProgress(request); var current = progress.Current(questionnaire) ?? questionnaire.Questions[^1];
            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };

            var header = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) header.ColumnDefinitions.Add(new() { Width = width });
            header.Children.Add(WaitBadge(new TextBlock { Text = "?", TextSize = 13, FontWeight = FontWeights.ExtraBold, Foreground = b.Brush(DesignToken.OnWait), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) }));
            var title = new TextBlock { Text = current.Header, TextSize = 12, FontWeight = FontWeights.Bold, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1); header.Children.Add(title);
            if (questionnaire.Questions.Count > 1)
            {
                var step = new TextBlock
                {
                    Text = Locale.Get("phone.questionnaire.progress", new Dictionary<string, string> { ["current"] = (progress.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ["total"] = questionnaire.Questions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                    TextSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center,
                };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(step, FontNumeralAlignment.Tabular);
                AutomationProperties.SetAutomationId(step, "agent-question-progress-" + id);
                Grid.SetColumn(step, 2); header.Children.Add(step);
            }
            if (progress.Index > 0)
            {
                var back = Button(Locale.Get("phone.questionnaire.back"), () => { progress.Back(questionnaire); guidedQuestionError = null; RenderGuidedStyle(); return Task.CompletedTask; }); PaintCardButton(back, prominent: false);
                AutomationProperties.SetAutomationId(back, "agent-question-back-" + id);
                Grid.SetColumn(back, 4); header.Children.Add(back);
            }
            var skip = Button(Locale.Get("question.panel.skip"), () => { DenyGuidedQuestion(request); return Task.CompletedTask; }); PaintCardButton(skip, prominent: false);
            AutomationProperties.SetAutomationId(skip, "agent-question-skip-" + id);
            Grid.SetColumn(skip, 5); header.Children.Add(skip);
            body.Children.Add(header);

            var question = new TextBlock { Text = current.Question, TextSize = 13.5, FontWeight = FontWeights.Bold, Foreground = ink, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            AutomationProperties.SetAutomationId(question, "agent-question-" + id);
            body.Children.Add(question);

            // Two tiles a row where the composer is wide enough, one where it is not (M/AgentQuestionPanel.swift:34).
            var tiles = new AdaptiveGridPanel { Minimum = 200, Gap = DesignMetrics.Spacing.Sm };
            for (var index = 0; index < current.Options.Count; index++)
            {
                var option = current.Options[index]; var picked = progress.Selected.Contains(option.Label); var edge = picked ? DesignMetrics.Stroke.Active : DesignMetrics.Stroke.Line;
                var mark = ComposerGlyph.Icon(current.MultiSelect ? (picked ? "" : "") : (picked ? "" : ""), 12, 14, 14).Ink(b.Brush(picked ? DesignToken.WaitText : DesignToken.Ink2));
                mark.View.VerticalAlignment = VerticalAlignment.Top; mark.View.Margin = new Thickness(0, 1, 0, 0);
                var words = new StackPanel { Spacing = 1 };
                words.Children.Add(new TextBlock { Text = option.Label, TextSize = 12, FontWeight = FontWeights.Bold, Foreground = ink, TextWrapping = TextWrapping.Wrap });
                if (option.Description.Length > 0) words.Children.Add(new TextBlock { Text = option.Description, TextSize = 10.5, Foreground = ink2, TextWrapping = TextWrapping.Wrap });
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                row.Children.Add(mark.View); Grid.SetColumn(words, 1); row.Children.Add(words);
                var face = new Border
                {
                    Child = row, CornerRadius = new CornerRadius(QuestionTileRadius), BorderThickness = new Thickness(edge), Padding = new Thickness(DesignMetrics.Spacing.Md - edge, DesignMetrics.Spacing.Sm - edge, DesignMetrics.Spacing.Md - edge, DesignMetrics.Spacing.Sm - edge),
                    Background = b.Brush(picked ? DesignToken.WaitSoft : DesignToken.Card), BorderBrush = b.Brush(picked ? DesignToken.Wait : DesignToken.Line),
                };
                var label = option.Label;
                var tile = Button(label, () => { ChooseGuidedOption(request, questionnaire, label); return Task.CompletedTask; });
                tile.Content = face; tile.Padding = new Thickness(0); tile.MinWidth = 0; tile.MinHeight = 0; tile.BorderThickness = new Thickness(0); tile.CornerRadius = new CornerRadius(QuestionTileRadius);
                tile.HorizontalAlignment = HorizontalAlignment.Stretch; tile.HorizontalContentAlignment = HorizontalAlignment.Stretch; tile.VerticalContentAlignment = VerticalAlignment.Stretch;
                owner.PaintPlainButton(tile, b.Transparent, b.Transparent);
                AutomationProperties.SetName(tile, option.Label + (option.Description.Length > 0 ? ". " + option.Description : "") + ", " + Locale.Get(picked ? "accessibility.selected" : "accessibility.notSelected"));
                AutomationProperties.SetAutomationId(tile, $"agent-question-option-{index}-{id}");
                tiles.Children.Add(tile);
            }
            body.Children.Add(tiles);

            var footer = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            footer.Children.Add(new TextBlock { Text = Locale.Get(current.MultiSelect ? "question.panel.hintMultiple" : "question.panel.hintSingle"), TextSize = 10.5, Foreground = ink2, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            if (current.MultiSelect && progress.Selected.Count > 0)
            {
                var done = Button(Locale.Get("question.panel.doneSelecting"), () => { if (progress.Commit("", questionnaire) is { } step) FinishGuidedStep(step, request, questionnaire); return Task.CompletedTask; }); PaintCardButton(done, prominent: true);
                AutomationProperties.SetAutomationId(done, "agent-question-done-" + id);
                Grid.SetColumn(done, 1); footer.Children.Add(done);
            }
            body.Children.Add(footer);
            if (guidedQuestionError is { } error)
            {
                var failed = new TextBlock { Text = error, TextSize = 10, Foreground = b.Brush(DesignToken.WaitText), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                AutomationProperties.SetAutomationId(failed, "agent-question-error-" + id);
                body.Children.Add(failed);
            }
            // Concept D: the amber edge says this waits on the user.
            var card = new Border
            {
                Child = body, CornerRadius = new CornerRadius(QuestionPanelRadius), BorderThickness = new Thickness(DesignMetrics.Stroke.Active), BorderBrush = b.Brush(DesignToken.Wait),
                Background = b.Brush(DesignToken.Card), Padding = new Thickness(DesignMetrics.Spacing.Md - DesignMetrics.Stroke.Active),
            };
            AutomationProperties.SetAutomationId(card, "agent-question-panel-" + id);
            return card;
        }

        /// <summary>A tile: a single-choice one answers the question, a multi-choice one toggles (M/AppStore+Styles.swift guidedChoose).</summary>
        private void ChooseGuidedOption(ToolPermissionRequest request, UserQuestionnaire questionnaire, string label)
        {
            if (composingInput) return;
            guidedQuestionError = null;
            if (GuidedProgress(request).Choose(label, questionnaire) is { } step) FinishGuidedStep(step, request, questionnaire);
            else RenderGuidedStyle();
        }

        /// <summary>
        /// Enter (or the send button) while a question waits in the style's panel (M/SessionPaneView.swift:913-921): a waiting
        /// question always wins. The typed text, with any toggled tiles, answers it and the draft is cleared; with nothing to
        /// answer with the draft is left alone. True when the question took the key.
        /// </summary>
        private async Task<bool> AnswerGuidedQuestion()
        {
            if (GuidedQuestion is not { } asked) return false;
            if (composingInput) return true;
            var typed = input.Text;
            if (GuidedProgress(asked.Request).Commit(typed, asked.Questionnaire) is not { } step) return true;
            guidedQuestionError = null;
            if (input.Text == typed) { updating = true; try { input.Text = ""; } finally { updating = false; } await Change(p => p with { Draft = "" }); }
            FinishGuidedStep(step, asked.Request, asked.Questionnaire);
            return true;
        }

        /// <summary>
        /// The next question, or the answers sent once every question has one (M/AppStore+Styles.swift finishGuidedStep). A send
        /// that fails says so on the panel and keeps what was answered, ready to send again.
        /// </summary>
        private void FinishGuidedStep(QuestionnaireStep step, ToolPermissionRequest request, UserQuestionnaire questionnaire)
        {
            if (step.Answers is not { } answers) { RenderGuidedStyle(); return; }
            try
            {
                questionnaire.ValidateAnswers(answers);
                if (owner.smokeQuestionAnswerer is { } fake) fake(id, request.Id, answers);
                else owner.service.AnswerQuestionnaire(id, request.Id, answers);
                guidedProgress = null; guidedQuestionInput = null;
                // The channel's own settled request removes it as well; this is the same removal, once.
                ReceiveToolPermission(request with { State = "answered" });
            }
            catch (Exception ex)
            {
                guidedQuestionError = ex.Message.Length > 0 ? ex.Message : Locale.Get("styles.guided.answerFailed");
                RenderGuidedStyle();
            }
        }

        /// <summary>답하지 않기: the question is denied, as the question card's cancel does (M/AgentQuestionPanel.swift:29).</summary>
        private void DenyGuidedQuestion(ToolPermissionRequest request)
        {
            if (owner.smokePermissionResponses is { } responses) { responses[request.Id] = false; ReceiveToolPermission(request with { State = "denied" }); return; }
            try { owner.service.RespondToToolPermission(id, request.Id, false); }
            catch (Exception ex) { guidedQuestionError = ex.Message; RenderGuidedStyle(); }
        }
    }
}
