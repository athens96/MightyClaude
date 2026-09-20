using System.Reflection;
using System.Text.Json;
using MightyClaude.Core;

internal static class RenameVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task renameValidationAcceptsValidNames()
    {
        Check(RenameSupport.DisplayName("Hello") == "Hello", "plain ASCII must be accepted");
        Check(RenameSupport.DisplayName("  Hello  ") == "Hello", "leading/trailing whitespace must be trimmed");
        Check(RenameSupport.DisplayName("안녕하세요") == "안녕하세요", "Korean text must be accepted");
        Check(RenameSupport.DisplayName("🎉") == "🎉", "single emoji must be accepted");
        Check(RenameSupport.DisplayName(new string('a', 120)) is not null, "exactly 120 ASCII chars must be accepted");
        Check(RenameSupport.DisplayName(new string('가', 120)) is not null, "exactly 120 Korean chars must be accepted");
        return Task.CompletedTask;
    }

    internal static Task renameValidationRefusesEmpty()
    {
        Check(RenameSupport.DisplayName("") is null, "empty string must be refused");
        Check(RenameSupport.DisplayName("   ") is null, "whitespace-only must be refused");
        Check(RenameSupport.DisplayName("\n\t\r") is null, "newlines and tab only must be refused");
        return Task.CompletedTask;
    }

    internal static Task renameValidationRefusesOver120()
    {
        Check(RenameSupport.DisplayName(new string('a', 121)) is null, "121 ASCII chars must be refused");
        Check(RenameSupport.DisplayName(new string('가', 121)) is null, "121 Korean chars must be refused");
        Check(RenameSupport.DisplayName(string.Concat(Enumerable.Repeat("🎉", 121))) is null, "121 emoji must be refused");
        return Task.CompletedTask;
    }

    internal static Task renameValidationRefusesControlCharacters()
    {
        Check(RenameSupport.DisplayName("hello\nworld") is null, "embedded newline must be refused");
        Check(RenameSupport.DisplayName("hello\rworld") is null, "embedded carriage return must be refused");
        Check(RenameSupport.DisplayName("a\u0000b") is null, "embedded null must be refused");
        Check(RenameSupport.DisplayName("a\u001Bb") is null, "embedded escape must be refused");
        return Task.CompletedTask;
    }

    internal static Task renameWorkspaceStoresNameInSnapshot()
    {
        var workspace = new Workspace { Name = "Old Name" };
        var session = new RunSession { WorkspaceId = workspace.Id };
        var snapshot = new AppSnapshot { Workspaces = [workspace], Sessions = [session] };
        var renamed = RenameSupport.RenameWorkspace(snapshot, workspace.Id, "새 이름");
        Check(renamed.Workspaces.Single(w => w.Id == workspace.Id).Name == "새 이름", "renamed workspace must have new name");
        Check(renamed.Workspaces.Single(w => w.Id == workspace.Id).Path == workspace.Path, "workspace path must not change");
        Check(renamed.Sessions[0].Title == session.Title, "session title must be unchanged by workspace rename");
        return Task.CompletedTask;
    }

    internal static Task renameSessionStoresTitleInSnapshot()
    {
        var workspace = new Workspace();
        var session = new RunSession { WorkspaceId = workspace.Id, Title = "Claude" };
        var snapshot = new AppSnapshot { Workspaces = [workspace], Sessions = [session] };
        var renamed = RenameSupport.RenameSession(snapshot, session.Id, "내 실행 창");
        Check(renamed.Sessions.Single(s => s.Id == session.Id).Title == "내 실행 창", "renamed session must have new title");
        Check(renamed.Workspaces[0].Name == workspace.Name, "workspace name must not change on session rename");
        return Task.CompletedTask;
    }

    internal static Task renameSessionTitleSurvivesNewOutput()
    {
        var workspace = new Workspace();
        var session = new RunSession { WorkspaceId = workspace.Id, Title = "Claude" };
        var snapshot = new AppSnapshot { Workspaces = [workspace], Sessions = [session] };
        var renamed = RenameSupport.RenameSession(snapshot, session.Id, "사용자 지정 이름");
        var after = renamed.Apply(RunEvent.Log(session.Id, "assistant", "some output"));
        Check(after.Sessions.Single(s => s.Id == session.Id).Title == "사용자 지정 이름", "user-given title must survive new output");
        return Task.CompletedTask;
    }

    internal static async Task renameNameSurvivesRestart()
    {
        var native = Verification.Temp(); var workspacePath = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = workspacePath };
            var session = new RunSession { WorkspaceId = workspace.Id, Title = "나만의 이름" };
            var snap = new AppSnapshot { Workspaces = [workspace], Sessions = [session] };
            await File.WriteAllTextAsync(Path.Combine(native, "workspace-state.json"), JsonSerializer.Serialize(snap, Wire.Json));
            var restored = await new StateStore(native).LoadAsync();
            Check(restored.Sessions.Single(s => s.Id == session.Id).Title == "나만의 이름", "renamed session title must survive a simulated restart");
            Check(restored.Workspaces.Single(w => w.Id == workspace.Id).Name == workspace.Name, "workspace name must survive a restart");
        }
        finally { Directory.Delete(native, true); Directory.Delete(workspacePath, true); }
    }

    internal static Task renameRejectsInvalidName()
    {
        var workspace = new Workspace();
        var session = new RunSession { WorkspaceId = workspace.Id };
        var snapshot = new AppSnapshot { Workspaces = [workspace], Sessions = [session] };
        try { RenameSupport.RenameWorkspace(snapshot, workspace.Id, ""); }
        catch (ArgumentException) { goto sessionCheck; }
        throw new InvalidOperationException("empty workspace name must be rejected");
        sessionCheck:
        try { RenameSupport.RenameSession(snapshot, session.Id, "   "); }
        catch (ArgumentException) { return Task.CompletedTask; }
        throw new InvalidOperationException("whitespace-only session name must be rejected");
    }

    internal static Task renameRejectsUnknownTarget()
    {
        var snapshot = new AppSnapshot();
        try { RenameSupport.RenameWorkspace(snapshot, "no-such-id", "X"); }
        catch (ArgumentException) { goto sessionCheck; }
        throw new InvalidOperationException("unknown workspace id must be rejected");
        sessionCheck:
        try { RenameSupport.RenameSession(snapshot, "no-such-id", "X"); }
        catch (ArgumentException) { return Task.CompletedTask; }
        throw new InvalidOperationException("unknown session id must be rejected");
    }

    internal static async Task renameViaDesktopServicePersistsName()
    {
        var directory = Verification.Temp(); var workspacePath = Verification.Temp();
        await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null));
        var service = new DesktopService(directory, null, "", catalog, testLoopback: true);
        try
        {
            await service.InitializeAsync();
            var workspace = await service.AddWorkspaceAsync(workspacePath);
            var session = new RunSession { WorkspaceId = workspace.Id, Title = "Claude" };
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Append(session).ToList() });
            await service.RenameWorkspaceAsync(workspace.Id, "나의 프로젝트");
            Check(service.Snapshot.Workspaces.Single(w => w.Id == workspace.Id).Name == "나의 프로젝트", "workspace name must be updated via DesktopService");
            await service.RenameSessionAsync(session.Id, "내 실행 창");
            Check(service.Snapshot.Sessions.Single(s => s.Id == session.Id).Title == "내 실행 창", "session title must be updated via DesktopService");
        }
        finally { await service.DisposeAsync(); Directory.Delete(directory, true); Directory.Delete(workspacePath, true); }
    }

    private static readonly Dictionary<string, string> RenameMacOS = new()
    {
        ["MenuEntry"] = "이름 변경…",
        ["FieldLabel"] = "이름",
        ["HeadingWorkspace"] = "워크스페이스 이름 변경",
        ["HeadingSession"] = "실행 창 이름 변경",
        ["HintWorkspace"] = "앱에 표시되는 이름만 바뀌며 폴더 이름과 경로는 유지됩니다.",
        ["HintSession"] = "사이드바와 탭에 같은 이름이 표시됩니다.",
        ["ErrorTooLong"] = "이름은 120자 이내로 입력하세요.",
        ["ErrorControlCharacter"] = "이름은 줄바꿈 없이 입력하세요.",
        ["ButtonCancel"] = "취소",
        ["ButtonSave"] = "저장",
        ["ErrorNotFound"] = "대상을 찾을 수 없습니다. 창을 닫고 다시 시도하세요.",
    };

    private static string? ValidateStrings(Dictionary<string, string> actual)
    {
        var seen = new Dictionary<string, string>();
        foreach (var (name, value) in actual)
        {
            if (value.Length == 0) return name + " is empty";
            if (seen.TryGetValue(value, out var twin)) return name + " duplicates " + twin;
            seen[value] = name;
            if (!RenameMacOS.TryGetValue(name, out var expected)) return name + " mirrors no macOS literal";
            if (value != expected) return name + " differs from macOS: " + value;
        }
        foreach (var name in RenameMacOS.Keys)
            if (!actual.ContainsKey(name)) return "missing " + name;
        return null;
    }

    internal static Task renameStringsMatchMacOS()
    {
        var actual = typeof(RenameStrings)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        if (ValidateStrings(actual) is { } reason) throw new InvalidOperationException(reason);

        Dictionary<string, string> Broken(string field, string val) => new(actual) { [field] = val };
        Check(ValidateStrings(Broken("MenuEntry", "")) is not null, "an empty value must fail");
        Check(ValidateStrings(Broken("ButtonSave", "Save")) is not null, "a value differing from macOS must fail");
        Check(ValidateStrings(Broken("ButtonCancel", actual["ButtonSave"])) is not null, "a duplicate value must fail");
        var missing = new Dictionary<string, string>(actual); missing.Remove("ErrorNotFound");
        Check(ValidateStrings(missing) is not null, "a missing literal must fail");
        return Task.CompletedTask;
    }
}
