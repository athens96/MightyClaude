using System.Text.Json;
using MightyCode.Core;

// A command in an agent pane's terminal waiting for input becomes a card the user answers from the pane or the phone
// (TerminalPrompt.cs; mirrors M/Tests/MightyCoreTests/TerminalPromptTests.swift; docs/terminal-prompts.md).
internal static class TerminalPromptVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task YesNoQuestionsAndTheirDefault()
    {
        var eas = TerminalPromptDetector.Detect("✔ Using remote Android credentials (Expo server)\n? Generate a new Android Keystore? › (Y/n)");
        Check(eas is { Kind: "yesNo", DefaultYes: true, Text: "? Generate a new Android Keystore? › (Y/n)" }, "expo yes/no with default yes");
        Check(TerminalPromptDetector.Detect("Overwrite the file? [y/N] ")?.DefaultYes == false, "[y/N] defaults to no");
        Check(TerminalPromptDetector.Detect("Continue? (yes/no) ") is { Kind: "yesNo", DefaultYes: null }, "(yes/no) has no default");
        Check(TerminalPromptDetector.Detect("Proceed (y/n)?")?.Kind == "yesNo", "(y/n) before a question mark");
        return Task.CompletedTask;
    }

    internal static Task SecretsTextAndNotPrompts()
    {
        Check(TerminalPromptDetector.Detect("Enter passphrase for key '/Users/me/.ssh/id_ed25519': ")?.Kind == "secret", "passphrase");
        Check(TerminalPromptDetector.Detect("[sudo] password for me:")?.Kind == "secret", "sudo password");
        Check(TerminalPromptDetector.Detect("\uBE44\uBC00\uBC88\uD638: ")?.Kind == "secret", "korean password");
        Check(TerminalPromptDetector.Detect("Project name:")?.Kind == "text", "a line");
        Check(TerminalPromptDetector.Detect("? What is your app named? › my-app") is null, "a prefilled answer is not a question mark at the end");
        Check(TerminalPromptDetector.Detect("? Package name ›")?.Kind == "text", "a pointer at the end");
        Check(TerminalPromptDetector.Detect("Downloading 45%") is null, "a progress line");
        Check(TerminalPromptDetector.Detect("Compressing project files...") is null, "a line still loading");
        Check(TerminalPromptDetector.Detect("Build finished.\n") is null, "a finished line");
        Check(TerminalPromptDetector.Detect("") is null, "nothing");
        return Task.CompletedTask;
    }

    internal static Task AListMovedWithTheArrowKeys()
    {
        var list = TerminalPromptDetector.Detect("? Select a build profile › - Use arrow-keys. Return to submit.\n❯   preview\n    production\n    development");
        Check(list is { Kind: "choice" } && list.Value.Text.StartsWith("? Select a build profile", StringComparison.Ordinal), "a list");
        Check(list!.Value.Options.SequenceEqual(["❯   preview", "production", "development"]), "its options, trimmed");
        Check(TerminalPromptDetector.Detect("? Done\n  a\n  b") is null, "lines without a marked choice are no list");
        return Task.CompletedTask;
    }

    internal static Task AnswersTypeWhatTheTerminalWouldGet()
    {
        Check(TerminalPromptAnswer.Yes.Input == "y\r" && TerminalPromptAnswer.No.Input == "n\r", "yes and no");
        Check(TerminalPromptAnswer.Typed("my-app").Input == "my-app\r", "a line");
        Check(TerminalPromptAnswer.Typed("one\nrm -rf /").Input == "one\r", "one answer is one line");
        Check(TerminalPromptAnswer.Key("down").Input == "\u001b[B" && TerminalPromptAnswer.Key("up").Input == "\u001b[A", "arrows");
        Check(TerminalPromptAnswer.Key("enter").Input == "\r" && TerminalPromptAnswer.Key("space").Input == " ", "enter and space");
        return Task.CompletedTask;
    }

    internal static Task ThePhonesAnswerIsReadStrictly()
    {
        Check(TerminalPromptAnswer.FromPhone("h1:40", "yes", null) == TerminalPromptAnswer.Yes, "yes");
        Check(TerminalPromptAnswer.FromPhone("h1:40", "down", null) == TerminalPromptAnswer.Key("down"), "a key");
        Check(TerminalPromptAnswer.FromPhone("h1:40", "text", "my-app") == TerminalPromptAnswer.Typed("my-app"), "text");
        Check(TerminalPromptAnswer.FromPhone("h1:40", "text", null) is null && TerminalPromptAnswer.FromPhone("h1:40", "text", "") is null, "text needs words");
        Check(TerminalPromptAnswer.FromPhone("h1:40", "text", new string('a', 4_097)) is null, "text over 4,096 bytes");
        Check(TerminalPromptAnswer.FromPhone("h1:40", "text", new string('a', 4_096)) is not null, "text of 4,096 bytes");
        Check(TerminalPromptAnswer.FromPhone("h1:40", "rm -rf", null) is null, "an unknown answer");
        Check(TerminalPromptAnswer.FromPhone("", "yes", null) is null && TerminalPromptAnswer.FromPhone("a\nb", "yes", null) is null && TerminalPromptAnswer.FromPhone(new string('a', 201), "yes", null) is null, "a malformed id");
        var prompt = new TerminalPrompt("h1:40", "h1", "Continue? (Y/n)", [], "yesNo", true);
        var json = JsonSerializer.Serialize(prompt, Wire.Json);
        Check(json.Contains("\"kind\":\"yesNo\"") && json.Contains("\"defaultYes\":true"), "the card travels as plain JSON: " + json);
        return Task.CompletedTask;
    }

    internal static Task TheMonitorWaitsForQuietOutputAndHidesAnAnsweredPrompt()
    {
        var pane = new FakePane();
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var monitor = new TerminalPromptMonitor(0.8, () => clock);
        pane.Output["h1"] = "\u001b[32m✔\u001b[0m Ready\r\n? Generate a new Android Keystore? › (Y/n)";
        Check(monitor.Poll(pane) is null, "just printed: not quiet yet");
        clock += TimeSpan.FromSeconds(1);
        var prompt = monitor.Poll(pane);
        Check(prompt is { Kind: "yesNo", Handle: "h1", Text: "? Generate a new Android Keystore? › (Y/n)" }, "quiet: the prompt");

        monitor.Answered(prompt!);
        clock += TimeSpan.FromSeconds(5);
        Check(monitor.Poll(pane) is null, "answered: hidden while nothing new is printed");
        pane.Output["h1"] += "y\r\nPassword: ";
        _ = monitor.Poll(pane); clock += TimeSpan.FromSeconds(1);
        var next = monitor.Poll(pane);
        Check(next is { Kind: "secret" } && next.Id != prompt!.Id, "more output: a new prompt");

        pane.Running = [];
        Check(monitor.Poll(pane) is null, "a command that ended waits for nothing");
        return Task.CompletedTask;
    }

    internal static async Task TheAgentTerminalKeepsAReadCursorApartFromTheAgentsReads()
    {
        await using var terminal = new AgentTerminal(Path.GetTempPath());
        Check(terminal.RunningHandles().Count == 0 && terminal.ReadOutput("none", 5, 10) is { Output: "", NextOffset: 5, MoreRemains: false }, "nothing runs");
        Check(!terminal.WriteTo("none", "x") && !terminal.IsCommandRunning("none"), "no such command takes no typing");
    }

    private sealed class FakePane : IPromptTerminal
    {
        internal readonly Dictionary<string, string> Output = [];
        internal IReadOnlyList<string>? Running;
        public IReadOnlyList<string> RunningHandles() => Running ?? Output.Keys.Order().ToArray();
        public PromptRead ReadOutput(string handle, int offset, int maxChars)
        {
            var text = Output.GetValueOrDefault(handle) ?? "";
            var start = Math.Min(offset, text.Length);
            return new(text[start..], text.Length, false);
        }
    }
}
