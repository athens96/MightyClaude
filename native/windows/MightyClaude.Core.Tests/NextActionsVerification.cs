using System.Text.Json;
using MightyClaude.Core;

internal static class NextActionsVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    internal static Task SharedContract()
    {
        using var stream = typeof(NextActionsVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.NextActions.json")!;
        using var fixture = JsonDocument.Parse(stream); var rows = fixture.RootElement.EnumerateArray().ToArray();
        Check(rows.Length >= 20, "shared fixture was not loaded");
        foreach (var row in rows)
        {
            var line = row.GetProperty("line").GetString()!;
            var expected = row.GetProperty("options").EnumerateArray().Select(option => new NextAction(option.GetProperty("label").GetString()!, option.GetProperty("fill").GetString()!));
            Check(NextActions.Parse(line).SequenceEqual(expected), "breadcrumb differs from Mac/phone: " + line);
        }
        return Task.CompletedTask;
    }
    internal static Task LatestReplyAndDraftSafety()
    {
        var reply = new LogEntry("a", "assistant", "◆ done → next: `ooo run` or Review", "");
        Check(NextActions.Latest([reply, new("s", "system", "Read", "")]) is { EntryId: "a", Actions.Count: 2 }, "system log should retain last suggestions");
        Check(NextActions.Latest([reply, new("u", "user", "next", "")]) is null && NextActions.Latest([reply, new("a2", "assistant", "plain", "")]) is null, "new user/reply must invalidate suggestions");
        Check(NextActions.Latest([]) is null, "empty history has no suggestions");
        Check(new NextAction("After `ooo run`", "ooo run").DisplayLabel == "After ooo run" && new NextAction("``", "``").DisplayLabel == "``", "display strips only nonblank markup");
        foreach (var draft in new[] { "", " \n\u3000" }) Check(NextActions.Insertion(draft, "ooo run") == (true, "ooo run"), "blank replacement");
        foreach (var draft in new[] { "draft", "draft " }) Check(NextActions.Insertion(draft, "ooo run") == (false, "\nooo run"), "preserve draft with separator");
        foreach (var draft in new[] { "draft\n", "draft\r\n", "draft\n\n" }) Check(NextActions.Insertion(draft, "ooo run") == (false, "ooo run"), "do not add redundant separator");
        Check(NextActions.Insertion("\u00a0", "x") == (false, "\nx"), "NBSP is user content, not contract whitespace");
        return Task.CompletedTask;
    }
}
