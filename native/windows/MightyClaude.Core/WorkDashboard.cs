using System.Globalization;

namespace MightyClaude.Core;

/// <summary>Counts and ordering from the macOS/phone work dashboard. Unknown usage stays unknown.</summary>
public static class WorkDashboard
{
    public sealed record Attention(int Questions = 0, int Permissions = 0) { public int Total => Questions + Permissions; }
    public sealed record Stats(int Running, int Waiting, int Done);
    public sealed record Badges(int Questions = 0, int Permissions = 0, int Errors = 0, int Running = 0, int Done = 0, int Stopped = 0, int Idle = 0);
    public sealed record Card(RunSession Session, Attention Attention, string DisplayStatus, double? ContextPercent, string? LastActivity, DateTimeOffset? UpdatedAt, bool ActivityIsError = false, string? Tool = null)
    {
        public AgentRunTiming? Timing => Session.RunTiming is { IsValid: true } timing ? timing : null;
        public string? Model => string.IsNullOrWhiteSpace(Session.Model) || Session.Model.Trim() == "default" ? null : Session.Model.Trim();
    }
    public static bool IsCounted(string kind) => kind is not ("files" or "agent-terminal" or "agent-browser");
    public static Attention Pending(IEnumerable<ToolPermissionRequest> requests) => new(requests.Count(r => r.State == "pending" && r.CanAnswerQuestions), requests.Count(r => r.State == "pending" && !r.CanAnswerQuestions));
    public static Stats Count(IEnumerable<RunSession> sessions, Func<string, Attention> attention)
    {
        var values = sessions.Where(s => IsCounted(s.Kind)).ToArray();
        return new(values.Count(s => s.Status == "running"), values.Sum(s => attention(s.Id).Total), values.Count(s => s.Status == "completed"));
    }
    public static Badges WorkspaceBadges(IEnumerable<RunSession> sessions, Func<string, Attention> attention)
    {
        var result = new Badges();
        foreach (var pane in sessions.Where(s => IsCounted(s.Kind)))
        {
            var pending = attention(pane.Id);
            result = result with { Questions = result.Questions + pending.Questions, Permissions = result.Permissions + pending.Permissions };
            result = StatusGlyph.Tone(StatusGlyph.DisplayStatus(pane.Status, pending.Total)) switch
            {
                DesignTone.Err => result with { Errors = result.Errors + 1 },
                DesignTone.Run => result with { Running = result.Running + 1 },
                DesignTone.Done => result with { Done = result.Done + 1 },
                DesignTone.Stop => result with { Stopped = result.Stopped + 1 },
                DesignTone.Idle => result with { Idle = result.Idle + 1 },
                _ => result,
            };
        }
        return result;
    }
    public static Card MakeCard(RunSession session, Attention attention)
    {
        var usage = session.SessionUsage?.Provider == session.Provider ? session.SessionUsage : null;
        double? percent = usage?.ContextPercent is { } p && double.IsFinite(p) ? Math.Clamp(p, 0, 100) : null;
        string? last = null, tool = null; var isError = false;
        foreach (var entry in session.Logs.AsEnumerable().Reverse())
        {
            if (entry.Kind == "image" && entry.Activity is null) continue;
            var line = entry.Activity is { } a ? string.Join(" · ", new[] { FirstLine(a.ToolName ?? ""), FirstLine(a.Summary) }.Where(s => s.Length > 0)) : FirstLine(entry.Text);
            if (line.Length == 0) continue; last = line;
            tool = entry.Activity?.ToolName is { } name ? FirstLine(name) : null;
            if (tool?.Length == 0) tool = null;
            isError = entry.Activity is { } activity ? activity.State == "error" : entry.Kind == "error";
            break;
        }
        var timestamp = session.Logs.LastOrDefault()?.Timestamp ?? session.CreatedAt;
        return new(session, attention, attention.Total > 0 ? "waiting" : session.Status, percent, last,
            AgentRunTiming.Parse(timestamp), isError, tool);
    }
    public static IReadOnlyList<Card> Ordered(IEnumerable<Card> cards) => cards.OrderBy(c => StatusGlyph.Tone(c.DisplayStatus) switch { DesignTone.Wait => 0, DesignTone.Run => 1, DesignTone.Err => 2, DesignTone.Done => 3, DesignTone.Stop => 4, _ => 5 }).ThenByDescending(c => c.UpdatedAt).ToArray();
    public sealed record Age(string Unit, int Count);
    public static Age Since(DateTimeOffset date, DateTimeOffset now)
    {
        var minutes = (int)Math.Clamp((now - date).TotalMinutes, 0, int.MaxValue);
        return minutes < 1 ? new("now", 0) : minutes < 60 ? new("minutes", minutes) : minutes < 1440 ? new("hours", minutes / 60) : new("days", minutes / 1440);
    }
    public static string AgeLabel(DateTimeOffset date, DateTimeOffset now)
    {
        var age = Since(date, now);
        var values = new Dictionary<string, string> { ["count"] = age.Count.ToString(CultureInfo.InvariantCulture) };
        return age.Unit switch
        {
            "minutes" => Locale.Get("dashboard.age.minutes", values),
            "hours" => Locale.Get("dashboard.age.hours", values),
            "days" => Locale.Get("dashboard.age.days", values),
            _ => Locale.Get("dashboard.age.now"),
        };
    }
    public static string SidebarDetail(Card card, DateTimeOffset now)
    {
        if (card.Session.Kind != "claude") return "";
        var parts = new List<string>();
        if (card.Session.Status == "running")
        {
            if (card.Timing is { } timing) parts.Add(timing.Label(now));
            if (card.ContextPercent is { } percent) parts.Add(Math.Round(percent, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%");
        }
        else
        {
            if (StatusGlyph.Tone(card.DisplayStatus) == DesignTone.Err && card.ActivityIsError && card.LastActivity is { } activity)
                parts.Add(card.Tool is { } tool ? Locale.Get("dashboard.card.toolFailed", new Dictionary<string, string> { ["tool"] = tool }) : Shorten(activity, 40));
            if (card.UpdatedAt is { } date) parts.Add(AgeLabel(date, now));
        }
        return string.Join(" · ", parts);
    }
    private static string Shorten(string value, int maximum)
    {
        var indices = StringInfo.ParseCombiningCharacters(value);
        return indices.Length <= maximum ? value : value[..indices[maximum - 1]] + "…";
    }
    private static string FirstLine(string text)
    {
        var start = 0; while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        var elements = StringInfo.GetTextElementEnumerator(text, start); var count = 0; var end = start;
        while (elements.MoveNext())
        {
            if (count++ == 200 || text[end] is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029') break;
            end += elements.GetTextElement().Length;
        }
        return text[start..end].TrimEnd();
    }
}
