using MightyClaude.Core;

internal static class SlashCommandVerification
{
    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "slash-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task QueryParsing()
    {
        Check(SlashCommandCatalog.Query("/") == "", "/ alone should return empty string");
        Check(SlashCommandCatalog.Query("/ar") == "ar", "/ar should return \"ar\"");
        Check(SlashCommandCatalog.Query("/archify make a diagram") == null, "draft with space should be null");
        Check(SlashCommandCatalog.Query("hello /ar") == null, "draft not starting with / should be null");
        Check(SlashCommandCatalog.Query("/" + new string('a', 81)) == null, "81-char rest should be null (over limit)");
        Check(SlashCommandCatalog.Query("/" + new string('a', 80)) == new string('a', 80), "80-char rest should be valid");
        Check(SlashCommandCatalog.Query("hello") == null, "no leading slash should be null");
        return Task.CompletedTask;
    }

    internal static Task ArgumentQuery()
    {
        var r = SlashCommandCatalog.ArgumentQuery("/model ");
        Check(r?.Command == "model" && r?.Query == "", "/model<space> should give command=model query=empty");
        r = SlashCommandCatalog.ArgumentQuery("/model cla");
        Check(r?.Command == "model" && r?.Query == "cla", "/model cla should give command=model query=cla");
        r = SlashCommandCatalog.ArgumentQuery("/approval-mode plan");
        Check(r?.Command == "approval-mode" && r?.Query == "plan", "hyphenated command should work");
        Check(SlashCommandCatalog.ArgumentQuery("/model") == null, "/model without space should be null");
        Check(SlashCommandCatalog.ArgumentQuery("/model a b") == null, "second space in query should be null");
        Check(SlashCommandCatalog.ArgumentQuery("/model  ") == null, "space then space in query should be null");
        Check(SlashCommandCatalog.ArgumentQuery("/bad name! x") == null, "invalid command name should be null");
        Check(SlashCommandCatalog.ArgumentQuery("model x") == null, "no leading / should be null");
        // Argument completion choices (model filter)
        var choices = new[] { "default", "claude-opus-5", "claude-sonnet-5" }
            .Select(v => new SlashCommand("model " + v, v, SlashCommandStrings.ModelSource, SlashCommandOrigin.App, SlashCommandAction.SetModel, null, v))
            .ToArray();
        var filtered = SlashCommandCatalog.Filter(choices, "model cla");
        Check(filtered.Select(c => c.Invocation).SequenceEqual(["model claude-opus-5", "model claude-sonnet-5"]),
            "argument completion filter must match by invocation prefix");
        Check(SlashCommandCatalog.Filter(choices, "model ").Length == 3, "empty argument query returns all choices");
        return Task.CompletedTask;
    }

    internal static Task BuiltinsPerProvider()
    {
        var claude = SlashCommandCatalog.Builtins("claude");
        Check(claude.Select(c => c.Invocation).SequenceEqual(["plugin", "model", "permissions", "clear", "cost", "usage", "config", "rename", "help"]),
            "Claude builtins must match macOS order exactly");
        Check(claude.All(c => c.Source == SlashCommandStrings.AppSource && (c.Action != null) != (c.Argument != null)),
            "Every Claude builtin must have exactly one of action or argument, never both or neither");
        Check(claude.First(c => c.Invocation == "plugin").Action == SlashCommandAction.OpenPlugins, "plugin action");
        Check(claude.First(c => c.Invocation == "model").Argument == SlashArgument.Model, "model argument");
        Check(claude.First(c => c.Invocation == "permissions").Argument == SlashArgument.Permission, "permissions argument");
        var codex = SlashCommandCatalog.Builtins("codex");
        Check(codex.Select(c => c.Invocation).SequenceEqual(["plugins", "model", "approvals", "new", "status", "settings", "rename", "help"]),
            "Codex builtins must match macOS order exactly");
        var gemini = SlashCommandCatalog.Builtins("gemini");
        Check(gemini.Select(c => c.Invocation).SequenceEqual(["model", "approval-mode", "clear", "stats", "settings", "rename", "help"]),
            "Gemini builtins must match macOS order exactly");
        Check(!gemini.Any(c => c.Action == SlashCommandAction.OpenPlugins), "Gemini must not have plugin browser");
        Check(SlashCommandCatalog.Builtins("shell").Length == 0, "shell provider should have no builtins");
        var help = SlashCommandCatalog.HelpText("claude");
        Check(help.StartsWith("앱 명령 · Claude 실행 창\n/plugin · ") && help.Contains("\n/clear · ") && help.Contains("CLI에 전달"),
            "Claude help text must match macOS prefix and content");
        // Prefix-first order in mixed list
        var planReview = new SlashCommand("plan-review", "", "x", SlashCommandOrigin.User);
        var mixed = SlashCommandCatalog.Filter([.. claude, planReview], "pl");
        Check(mixed.Select(c => c.Invocation).SequenceEqual(["plugin", "plan-review"]),
            "Builtins sort into the same prefix-first order as scanned commands");
        return Task.CompletedTask;
    }

    internal static Task Filter()
    {
        var commands = new[] { "archify", "sc:analyze", "sc:build", "oh-my-claudecode:autopilot", "review" }
            .Select(inv => new SlashCommand(inv, inv == "review" ? "Analyze a PR" : "", "x", SlashCommandOrigin.User))
            .ToArray();
        Check(SlashCommandCatalog.Filter(commands, "").Select(c => c.Invocation).SequenceEqual(commands.Select(c => c.Invocation)),
            "empty query returns all in original order");
        Check(SlashCommandCatalog.Filter(commands, "a").Select(c => c.Invocation).SequenceEqual(["archify", "sc:analyze", "oh-my-claudecode:autopilot", "review"]),
            "query a: prefix > after-colon > description-contains");
        Check(SlashCommandCatalog.Filter(commands, "auto").Select(c => c.Invocation).SequenceEqual(["oh-my-claudecode:autopilot"]),
            "after-colon autopilot matches");
        Check(SlashCommandCatalog.Filter(commands, "SC:").Select(c => c.Invocation).SequenceEqual(["sc:analyze", "sc:build"]),
            "case-insensitive prefix SC: matches sc: commands");
        Check(SlashCommandCatalog.Filter(commands, "zzz").Length == 0, "no match returns empty");
        return Task.CompletedTask;
    }

    internal static Task Frontmatter()
    {
        var fm = SlashCommandCatalog.Frontmatter("---\nname: x\ndescription: >\n  folded\n---");
        Check(fm.Count == 1 && fm["name"] == "x", "folded-style description must be excluded");
        Check(SlashCommandCatalog.Frontmatter("# no frontmatter").Count == 0, "no frontmatter returns empty dict");
        var normal = SlashCommandCatalog.Frontmatter("---\nname: gstack\ndescription: \"Router for the suite\"\n---\n# body");
        Check(normal.TryGetValue("name", out var n) && n == "gstack", "double-quoted name must be unquoted");
        Check(normal.TryGetValue("description", out var d) && d == "Router for the suite", "double-quoted description must be unquoted");
        var single = SlashCommandCatalog.Frontmatter("---\nname: 'archify'\n---");
        Check(single.TryGetValue("name", out var sn) && sn == "archify", "single-quoted value must be stripped");
        return Task.CompletedTask;
    }

    internal static Task Discovery()
    {
        var root = Temp();
        try
        {
            var home = Path.Combine(root, "home");
            var workspace = Path.Combine(root, "repo");
            // User skills
            Write(Path.Combine(home, ".claude", "skills", "_gstack-command", "SKILL.md"),
                "---\nname: gstack\ndescription: \"Router for the suite\"\n---\n# body\n");
            Write(Path.Combine(home, ".claude", "skills", "archify", "SKILL.md"),
                "---\ndescription: 'Make diagrams'\n---\n");
            Write(Path.Combine(home, ".claude", "skills", "plain", "SKILL.md"),
                "no frontmatter here\n");
            Write(Path.Combine(home, ".claude", "skills", "weird dir", "SKILL.md"),
                "---\nname: bad name!\n---\n");
            // User commands
            Write(Path.Combine(home, ".claude", "commands", "sc", "analyze.md"),
                "---\nallowed-tools: [Read]\ndescription: \"Analyze code\"\n---\n");
            Write(Path.Combine(home, ".claude", "commands", "deploy.md"),
                "Just a prompt body line\nmore\n");
            // Plugin (ralph@official); ghost@official references a missing path; ../x@y has invalid key
            var pluginPath = Path.Combine(root, "cache", "official", "ralph", "1.0.0");
            Write(Path.Combine(pluginPath, "skills", "ralph-loop", "SKILL.md"),
                "---\nname: ralph-loop\ndescription: Loop\n---\n");
            Write(Path.Combine(pluginPath, "commands", "cancel-ralph.md"),
                "---\ndescription: Cancel\n---\n");
            var escapedPluginPath = pluginPath.Replace("\\", "\\\\");
            var escapedMissingPath = Path.Combine(root, "missing").Replace("\\", "\\\\");
            Write(Path.Combine(home, ".claude", "plugins", "installed_plugins.json"),
                $"{{\"version\":2,\"plugins\":{{" +
                $"\"ralph@official\":[{{\"scope\":\"user\",\"installPath\":\"{escapedPluginPath}\"}}]," +
                $"\"ghost@official\":[{{\"installPath\":\"{escapedMissingPath}\"}}]," +
                $"\"../x@y\":[{{\"installPath\":\"{escapedPluginPath}\"}}]}}}}");
            // Project skill shadows user archify
            Write(Path.Combine(workspace, ".claude", "skills", "archify", "SKILL.md"),
                "---\nname: archify\ndescription: Project override\n---\n");
            // Codex skill
            Write(Path.Combine(home, ".codex", "skills", "hatch-pet", "SKILL.md"),
                "---\nname: hatch-pet\ndescription: Pets\n---\n");

            var claude = SlashCommandCatalog.Commands("claude", workspace, home);
            Check(claude.Select(c => c.Invocation).SequenceEqual(["archify", "deploy", "gstack", "plain", "ralph:cancel-ralph", "ralph:ralph-loop", "sc:analyze"]),
                "Claude discovery must produce sorted, deduplicated list matching macOS test fixture");
            Check(claude.First(c => c.Invocation == "archify").Description == "Project override",
                "Project skill must shadow user skill with same name");
            Check(claude.First(c => c.Invocation == "archify").Source == SlashCommandStrings.ProjectSkillSource,
                "Project skill source badge must be 프로젝트 스킬");
            Check(claude.First(c => c.Invocation == "gstack").Description == "Router for the suite",
                "User skill description via frontmatter name redirect");
            Check(claude.First(c => c.Invocation == "deploy").Description == "Just a prompt body line",
                "Command without frontmatter description uses first non-heading line");
            Check(claude.First(c => c.Invocation == "ralph:ralph-loop").Source == "플러그인 ralph",
                "Plugin source badge must be 플러그인 <name>");
            Check(claude.First(c => c.Invocation == "sc:analyze").Description == "Analyze code",
                "Grouped command description from frontmatter");
            var codex = SlashCommandCatalog.Commands("codex", workspace, home);
            Check(codex.Length == 1 && codex[0].Invocation == "hatch-pet" && codex[0].Source == SlashCommandStrings.CodexSkillSource,
                "Codex discovery must use only its own skill paths");
            Check(SlashCommandCatalog.Commands("gemini", null, home).Length == 0,
                "Gemini has no discovery paths");
            Check(SlashCommandCatalog.Commands("claude", null, Path.Combine(root, "nowhere")).Length == 0,
                "Missing home directory returns empty list");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;
    }

    internal static Task CapAt400()
    {
        var root = Temp();
        try
        {
            var home = Path.Combine(root, "home");
            for (var i = 0; i < 500; i++)
                Write(Path.Combine(home, ".claude", "skills", $"skill{i:D4}", "SKILL.md"),
                    $"---\ndescription: skill {i}\n---\n");
            var result = SlashCommandCatalog.Commands("claude", null, home);
            Check(result.Length == SlashCommandCatalog.MaximumCommands,
                $"Discovery must be capped at {SlashCommandCatalog.MaximumCommands}, got {result.Length}");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;
    }
}
