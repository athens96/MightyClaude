using System.Text;
using System.Text.Json.Nodes;
using MightyClaude.Core;

internal static class StyleSurfacesVerification
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    internal static Task ApprovalAndReset()
    {
        var root = Verification.Temp();
        try
        {
            var registry = StyleRegistry.Load(root, root);
            var style = registry.Styles.Single(s => s.Id == "ouroboros");
            var sections = StylePresentation.Approval(style);
            var automatic = sections.Single(s => s.Id == "autoAllow");
            Check(!automatic.Foldable && automatic.Lines.Count == 17 && automatic.Lines.Contains("mcp__plugin_ouroboros_ouroboros__ouroboros_interview"), "Approval must expose every exact automatic permission outside collapsed details.");
            Check(sections.Select(s => s.Id).Take(4).SequenceEqual(["origin", "autoAllow", "install", "enter"]), "Risk-bearing sections precede cosmetic metadata.");
            Check(sections.Single(s => s.Id == "enter").Lines.Contains("/ouroboros:interview {text}"), "Preview must contain the exact Enter rewrite.");
            var decision = new StyleApprovalDecision(automatic.Lines.Count);
            Check(!decision.Press(false) && !decision.Confirming, "An unshown permission block cannot start approval.");
            Check(!decision.Press(true) && decision.Confirming, "Automatic permission approval needs a second explicit press.");
            Check(!decision.Press(false) && decision.Press(true), "The confirming press still requires a visible permission block.");
            Check(new StyleApprovalDecision(0).Press(true), "A style without automatic permissions needs one explicit approval.");
            Check(!new StyleApprovalDecision(1).Press(true), "Confirmation cannot carry into a fresh preview.");
            var evaluator = style.Evaluator;
            var completed = evaluator.CurrentPhase(["/ouroboros:evaluate"]);
            Check(completed?.Id == "evaluate" && !evaluator.AtStart(completed), "Fixture reached a later phase.");
            var reset = evaluator.EffectivePhase(completed, true);
            Check(evaluator.AtStart(reset) && evaluator.VisibleActions(reset, null, false, startingNew: true).Select(a => a.Id).SequenceEqual(["interview", "auto"]), "Reset exposes the entry phase and start actions.");
            Check(evaluator.RewriteAction("a new goal", reset, false, false, true, true) == "interview", "A new goal rewrites the first Enter despite older requests.");
            Check(evaluator.RewriteAction("/help", reset, false, false, true, true) is null && evaluator.RewriteAction("a goal", reset, true, false, true, true) is null, "Reset cannot rewrite explicit commands or attached input.");
            Check(evaluator.EffectivePhase(completed, false) == completed, "Cancel reset restores the current phase.");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task MetadataAndCasebook()
    {
        var root = Verification.Temp();
        try
        {
            var registry = StyleRegistry.Load(root, root);
            var paper = registry.Styles.Single(s => s.Id == "paperthin");
            var action = paper.Manifest.Actions.Single(a => a.Id == "macrothink");
            Check(action.Scope is { Length: > 0 } && action.Flags!.SequenceEqual(["userInvoked", "readOnly"]), "Scope and both action flags survive manifest decoding.");
            var absent = StylePrerequisites.Capabilities(paper.Manifest, root);
            Check(absent.States["paperthin.casebook"] == "absent" && absent.Files.Count == 0, "An absent casebook has no fabricated files.");
            var folder = Path.Combine(root, ".re0", "iteration", "1-check"); Directory.CreateDirectory(folder);
            for (var index = 0; index < 28; index++) File.WriteAllText(Path.Combine(folder, $"REF-{index:00}.local.md"), "reference");
            File.WriteAllText(Path.Combine(folder, "RETRO.local.md"), "retrospective"); File.WriteAllText(Path.Combine(folder, "DESIGN.local.md"), "design");
            var result = StylePrerequisites.Capabilities(paper.Manifest, root);
            Check(result.States["paperthin.casebook"] == "complete", "Known files must survive an arbitrary directory enumeration with more than 24 references.");
            Check(result.Files.Count == 24 && result.Files[0].Title == "DESIGN" && result.Files[1].Title == "RETRO", "The 24-file limit is applied after prioritizing known casebook files.");
            Check(result.Files.All(f => f.Detail == "1-check · full" && f.ReadOnly && File.Exists(f.Path)), "File chips retain cycle/weight details and real read-only paths.");
            Check(paper.Evaluator.RecommendedAction(result.States) == "re0-work" && paper.Evaluator.DrawsGroupMap, "Casebook completion drives catalog recommendations and map presentation.");
            var numeric = StylePresentation.Progress(new("progressBar", Value: 7));
            Check(numeric is { Fraction: 0, Text: "7" }, "A bare counter displays its count without inventing a total.");
            Check(StylePresentation.Progress(new("progressBar", Value: 7, Total: 4)) is { Fraction: 1, Text: "4/4" }, "Progress clamps to a declared total.");
            Check(!StylePresentation.Inline("a\u202eb\u0000c").Contains('\u202e'), "Inline widget text removes display-direction controls.");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task TrustedRequestProjectionAndLockedRegistry()
    {
        var root = Verification.Temp();
        try
        {
            var registry = StyleRegistry.Load(root, root); var bundled = registry.Styles.Single(s => s.Id == "ouroboros");
            var json = JsonNode.Parse(bundled.Bytes.Span)!; json["id"] = "custom-flow"; json["name"] = "Custom"; json["autoAllow"] = new JsonArray(); json["actions"]![0]!["requestTitle"] = "Custom interview";
            var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
            var modified = bundled with { Manifest = StyleManifestDecoder.Decode(bytes, "user"), Source = "user", Approval = "approved", Bytes = bytes };
            Check(StylePresentation.RequestPrefix([modified], "/ouroboros:interview goal") == "Custom interview", "Explicit action request titles take priority within a trusted style.");
            Check(StylePresentation.RequestPrefix([modified with { Approval = "pending" }], "/ouroboros:interview goal") is null, "An unapproved style cannot influence a request's identity.");
            Check(StylePresentation.RequestPrefix([modified, bundled], "/ouroboros:interview goal") == bundled.Manifest.Phases.Single(p => p.Id == "interview").Title, "Bundled-first recognition is stable even when input order is reversed.");
            Check(StylePresentation.RequestPrefix([bundled], "ooo qa") == bundled.Manifest.Phases.Single(p => p.Id == "evaluate").Title, "Aliases project their phase titles.");
            var path = Path.Combine(root, "styles"); Directory.CreateDirectory(path); File.WriteAllBytes(Path.Combine(path, "custom.json"), bytes);
            var trust = Path.Combine(root, "style-trust"); Directory.CreateDirectory(trust); File.WriteAllText(Path.Combine(trust, "approvals.json"), "{\"version\":1,\"records\":null}");
            var locked = StyleRegistry.Load(root, root);
            Check(locked.TrustLocked && locked.Rejections.Any(r => r.Code == "E_TRUST_LOCKED"), "Malformed approval storage is visible as a locked trust store.");
            Check(locked.Styles.Single(s => s.Id == "custom-flow").Approval == "pending" && locked.Styles.Where(s => s.Source == "bundled").All(s => s.Runnable), "Locked storage keeps custom previews visible without granting permission or disabling bundled styles.");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
