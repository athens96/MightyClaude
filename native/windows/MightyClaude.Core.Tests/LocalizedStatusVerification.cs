using System.Reflection;
using MightyClaude.Core;

internal static class LocalizedStatusVerification
{
    internal static async Task LanguageChangesReachNotificationsAndUsage()
    {
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var before = Locale.LanguagePreference;
        try
        {
            Locale.LanguagePreference = "ko";
            var koreanTitle = CompletionNotificationStrings.NotificationTitle;
            var koreanChip = AccountUsageStrings.ChipBeforeFirstRun;
            Locale.LanguagePreference = "en";
            Check(CompletionNotificationStrings.NotificationTitle == "MightyClaude · Task complete" && CompletionNotificationStrings.NotificationBodyTemplate.Replace("{title}", "Fixture") == "Fixture has finished.", "notifications must use the saved English preference after an earlier Korean read");
            Check(AccountUsageStrings.ChipBeforeFirstRun == "Shown after a run" && AccountUsageSupport.WindowLabel("30m") == "30 min" && AccountUsageSupport.WindowLabel("2h") == "2 hours", "usage status and arbitrary duration windows must follow English");
            foreach (var type in new[] { typeof(AccountUsageStrings), typeof(CompletionNotificationStrings) })
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)))
                {
                    var value = (string)property.GetValue(null)!;
                    Check(!string.IsNullOrWhiteSpace(value) && !value.StartsWith("windows.", StringComparison.Ordinal) && !value.StartsWith("usage.reset.", StringComparison.Ordinal), "all visible status strings must resolve: " + property.Name);
                    Check(!value.Any(c => c is >= '\uAC00' and <= '\uD7A3'), "English status copy must not retain Korean literals: " + property.Name);
                }
            var stamp = DateTimeOffset.Parse("2026-09-20T10:00:00Z");
            var fixture = new AccountUsageSnapshot { Provider = "codex", Status = "available", FetchedAt = stamp.ToString("O"), Windows = [new("five_hour", 25, stamp.AddHours(2).ToString("O"))] };
            await using var usage = new AccountUsageStatus(new AccountUsageService((_, _) => Task.FromResult(fixture), () => stamp), () => stamp);
            usage.Update(new AppSnapshot { Workspaces = [new() { Id = "w" }], Sessions = [new() { Id = "c", WorkspaceId = "w", Kind = "claude", Provider = "codex" }] });
            await usage.RefreshAsync();
            var card = usage.Cards().Single();
            Check(usage.Chips().Single().Text == "Session 25%" && card.Windows.Single().Used == "25% used" && card.CheckedAt!.StartsWith("Checked ", StringComparison.Ordinal), "real status projections must use localized labels and templates");
            Check(card.Windows.Single().Reset == "Resets " + stamp.AddHours(2).ToLocalTime().ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en-US")), "explicit English controls date formatting independently of host culture");
            Locale.LanguagePreference = "ko";
            Check(CompletionNotificationStrings.NotificationTitle == koreanTitle && AccountUsageStrings.ChipBeforeFirstRun == koreanChip, "switching back must not retain cached English copy");
        }
        finally { Locale.LanguagePreference = before; }
    }
}
