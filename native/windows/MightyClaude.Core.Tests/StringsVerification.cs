using System.Reflection;
using MightyClaude.Core;

// One check over both Korean copy classes. The rules live in Validate so the
// check can prove each rule rejects its own failure before trusting a pass.
internal static class StringsVerification
{
    // The macOS literal every constant mirrors, by field name.
    // SlashCommands.swift / SlashCommandPalette.swift
    private static readonly Dictionary<string, string> SlashMacOS = new()
    {
        ["AppSource"] = "앱 기능",
        ["ModelSource"] = "모델",
        ["PermissionSource"] = "작업 권한",
        ["UserSkillSource"] = "사용자 스킬",
        ["ProjectSkillSource"] = "프로젝트 스킬",
        ["UserCommandSource"] = "사용자 명령",
        ["ProjectCommandSource"] = "프로젝트 명령",
        ["CodexSkillSource"] = "Codex 스킬",
        ["PaletteMove"] = "↑↓ 이동",
        ["PaletteSelect"] = "Enter · Tab 선택",
        ["PaletteDismiss"] = "Esc 닫기",
        ["PaletteCountTemplate"] = "{count}개",
        ["PaletteNoDescription"] = "설명 없음",
        ["PaletteActionTooltip"] = "앱에서 바로 실행됩니다",
        ["PaletteArgumentTooltip"] = "이어서 선택합니다",
    };

    // StatusLineView.swift / StatusLine.swift
    private static readonly Dictionary<string, string> StatusLineMacOS = new()
    {
        ["TrustPromptTemplate"] = "{source}에 statusLine 명령이 있습니다. 이 워크스페이스에서 실행할까요?",
        ["TrustAllow"] = "이 워크스페이스에서 허용",
        ["TrustDeny"] = "지금은 안 함",
        ["TrustNote"] = "저장소가 바꾼 명령은 다시 묻습니다.",
        ["AccessibilityLabel"] = "상태 줄",
        ["ErrorStartTemplate"] = "명령을 시작하지 못했습니다: {reason}",
        ["ErrorTimeout"] = "상태 줄 명령이 제한 시간 안에 끝나지 않았습니다.",
        ["ErrorExitTemplate"] = "상태 줄 명령이 종료 코드 {code}로 끝났습니다.",
        ["SourceWorkspaceLocal"] = "프로젝트 로컬 설정",
        ["SourceWorkspace"] = "프로젝트 설정",
        ["SourceUser"] = "사용자 설정",
    };

    /// Returns the reason the class fails, or null when the copy is the macOS copy.
    private static string? Validate(string className, IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> macOS)
    {
        var seen = new Dictionary<string, string>();
        foreach (var (name, value) in actual)
        {
            if (value.Length == 0) return className + "." + name + " is empty";
            if (seen.TryGetValue(value, out var twin)) return className + "." + name + " duplicates " + twin;
            seen[value] = name;
            if (Placeholders(value) is { } bad) return className + "." + name + " has a placeholder that is not {name}: " + bad;
            if (!macOS.TryGetValue(name, out var expected)) return className + "." + name + " mirrors no macOS literal";
            if (value != expected) return className + "." + name + " differs from macOS: " + value;
        }
        foreach (var name in macOS.Keys)
            if (!actual.ContainsKey(name)) return className + " is missing " + name;
        return null;
    }

    /// Null when every brace pair in the text is a {name}; otherwise the offending fragment.
    private static string? Placeholders(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '}') return "}";
            if (text[i] != '{') continue;
            var close = text.IndexOf('}', i);
            if (close < 0) return text[i..];
            var name = text[(i + 1)..close];
            if (name.Length == 0 || !char.IsAsciiLetter(name[0]) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return "{" + name + "}";
            i = close;
        }
        return null;
    }

    private static Dictionary<string, string> Constants(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

    internal static Task MatchMacOS()
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        var slash = Constants(typeof(SlashCommandStrings));
        var statusLine = Constants(typeof(StatusLineStrings));
        Check(Validate(nameof(SlashCommandStrings), slash, SlashMacOS) is null, Validate(nameof(SlashCommandStrings), slash, SlashMacOS) ?? "");
        Check(Validate(nameof(StatusLineStrings), statusLine, StatusLineMacOS) is null, Validate(nameof(StatusLineStrings), statusLine, StatusLineMacOS) ?? "");

        // Each rule must reject its own failure, or a pass above would mean nothing.
        Dictionary<string, string> Broken(string field, string value) { var copy = new Dictionary<string, string>(slash) { [field] = value }; return copy; }
        string? Bad(Dictionary<string, string> copy) => Validate(nameof(SlashCommandStrings), copy, SlashMacOS);
        Check(Bad(Broken("ModelSource", "")) is not null, "an empty value must fail");
        Check(Bad(Broken("ModelSource", SlashCommandStrings.AppSource)) is not null, "a duplicate value inside one class must fail");
        Check(Bad(Broken("PaletteCountTemplate", "{ count }개")) is not null, "a placeholder that is not {name} must fail");
        Check(Bad(Broken("PaletteCountTemplate", "{count개")) is not null, "an unclosed placeholder must fail");
        Check(Bad(Broken("ModelSource", "Model")) is not null, "a value that differs from the macOS literal must fail");
        Check(Bad(new Dictionary<string, string>(slash) { ["Extra"] = "새 문구" }) is not null, "a constant with no macOS literal must fail");
        var missing = new Dictionary<string, string>(slash); missing.Remove("PaletteDismiss");
        Check(Bad(missing) is not null, "a missing literal must fail");
        return Task.CompletedTask;
    }
}
