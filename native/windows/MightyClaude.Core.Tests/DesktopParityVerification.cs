using MightyClaude.Core;

internal static class DesktopParityVerification
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static Task NewPanesInheritChoicesWithoutConversationIdentity()
    {
        var older = new RunSession { Id = "older", CreatedAt = "2026-09-20T10:00:00Z", Model = "opus", Settings = new(Effort: "high", PermissionMode: "onRequest", NetworkAccess: true), AgentViewMode = "mighty", ResumeId = "private-conversation", Draft = "old draft", Logs = [new("entry", "assistant", "old output", "2026-10-04T10:00:00Z")] };
        var newer = new RunSession { Id = "newer", CreatedAt = "2026-10-03T10:00:00Z", Model = "sonnet" };
        var target = new RunSession { Id = "fresh", WorkspaceId = "different-workspace", Title = "Fresh Claude" };
        var inherited = SessionTemplate.Inherit(target, [older, newer, new() { Provider = "codex", Model = "wrong" }]);
        Check(inherited.Model == "opus" && inherited.Settings == older.Settings && inherited.AgentViewMode == "mighty", "most recently used provider's choices are copied across workspaces");
        Check(inherited.Id == target.Id && inherited.Title == target.Title && inherited.WorkspaceId == target.WorkspaceId && inherited.ResumeId is null && inherited.Logs.Count == 0 && inherited.Draft.Length == 0 && inherited.GraphRuns is null && inherited.SessionUsage is null, "conversation identity and history stay fresh");
        Check(SessionTemplate.Inherit(target with { Kind = "shell" }, [older]).Model == "default", "shell never inherits an agent's settings");
        Check(SessionTemplate.Inherit(target with { Provider = "codex" }, [older]).Model == "default", "another provider is not a template");
        return Task.CompletedTask;
    }

    internal static Task GitStatusParsesOnlyLocalRepositoryMetadata()
    {
        var parsed = WorkspaceGitInfo.Parse("# branch.oid abcdef0123456789\n# branch.head feature/한글\n# branch.ab +2 -3\n? secret-file.env\n");
        Check(parsed is { Branch: "feature/한글", IsDirty: true, Ahead: 2, Behind: 3 } && parsed.Badge.Contains("↑2 ↓3", StringComparison.Ordinal), "status has branch, dirty flag and local upstream counts");
        Check(!parsed!.ToString().Contains("secret-file", StringComparison.Ordinal), "filenames never enter the displayed metadata");
        Check(WorkspaceGitInfo.Parse("# branch.head (detached)\n# branch.oid abcdef0123456789\n")?.Label == "HEAD · abcdef0", "detached HEAD names revision");
        Check(WorkspaceGitInfo.Parse("# branch.head \u001bspoof\n") is null, "control characters are rejected");
        var command = WorkspaceGitInfo.Command("git", Path.GetTempPath());
        Check(!command.UseShellExecute && command.ArgumentList.Contains("--no-optional-locks") && command.ArgumentList.Contains("core.fsmonitor=false") && command.ArgumentList.Contains("core.untrackedCache=false") && command.ArgumentList.Contains("gc.auto=0"), "read-only probe disables fsmonitor/index maintenance");
        Check(command.Environment.Where(e => e.Key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).All(e => e.Key is "GIT_OPTIONAL_LOCKS" or "GIT_TERMINAL_PROMPT"), "ambient GIT_* cannot redirect the probe");
        return Task.CompletedTask;
    }

    internal static Task TranscriptAndResultFilesResolveTheSameSafePreview()
    {
        var parent = Verification.Temp(); var root = Path.Combine(parent, "workspace"); Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "한글.cs"), "var text = \"한글\";\n");
            var fromList = ReferencePreview.Resolve("src/한글.cs", root, 2);
            var source = "결과는 src/한글.cs:2 에 있어요";
            var tapped = ReferencePreview.TextTargetAt(source, source.IndexOf("한글", StringComparison.Ordinal));
            Check(tapped is { Path: "src/한글.cs", Line: 2 }, "click recovers path and line without launching anything");
            var fromTranscript = ReferencePreview.Resolve(tapped!.Value.Path, root, tapped.Value.Line);
            Check(fromList == fromTranscript && fromList?.RelativePath == "src/한글.cs", "both routes use the same normalized target");
            Check(ReferencePreview.Load(fromList!, root).Text?.Contains("한글", StringComparison.Ordinal) == true, "preview reads the selected file");
            File.WriteAllText(Path.Combine(root, "README.md"), "# Preview");
            Check(ReferencePreview.TextTargetAt("설명 (README.md)", 6)?.Path == "README.md", "root-level Markdown destinations can be clicked");
            Check(ReferencePreview.TextTargetAt("설명 (my docs/한글 파일.md:3)", 17) is { Path: "my docs/한글 파일.md", Line: 3 }, "rendered Markdown links retain spaces in names");
            Check(ReferencePreview.TextTargetAt("https://example.com/src/file.cs", 25) is null, "web URL suffix never becomes a workspace file");
            Check(ResultFiles.Matches(@"See C:\work\한글.cs:7 and src\other.cs").Select(match => match.Path).SequenceEqual([@"C:\work\한글.cs", @"src\other.cs"]), "Windows drive and relative backslash paths enter the shared result inventory");
            Check(ResultFiles.Matches(@"C:\readme.md").Single().Path == @"C:\readme.md", "drive-root files retain the whole absolute path");
            Check(ResultFiles.LocalPath(@"C:\work\file.cs") == @"C:\work\file.cs", "drive-letter paths are local references");
            Check(ResultFiles.LocalPath("file:///C:/work/file.cs") == "C:/work/file.cs", "Windows file URI is normalized");
        }
        finally { Directory.Delete(parent, true); }
        return Task.CompletedTask;
    }

    internal static Task ReferencePreviewsRefuseEscapesAndFinalSymlinks()
    {
        var parent = Verification.Temp(); var root = Path.Combine(parent, "workspace"); Directory.CreateDirectory(root);
        try
        {
            var outside = Path.Combine(parent, "private.txt"); File.WriteAllText(outside, "not displayed");
            Check(ReferencePreview.Resolve("../private.txt", root) is null && ReferencePreview.Resolve(outside, root) is null, "relative and absolute escape paths rejected");
            Check(ReferencePreview.Resolve("file://server/share/private.txt", root) is null, "remote file URLs are never accessed");
            var link = Path.Combine(root, "looks-local.txt");
            try { File.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException) { return Task.CompletedTask; }
            Check(ResultFiles.Resolve("looks-local.txt", root) is null, "the last path component's symlink cannot escape");
            Check(ReferencePreview.Resolve("looks-local.txt", root) is null && ResultFiles.In(["[open](looks-local.txt)"], root).Count == 0, "preview and final-result inventory agree");
        }
        finally { Directory.Delete(parent, true); }
        return Task.CompletedTask;
    }
}
