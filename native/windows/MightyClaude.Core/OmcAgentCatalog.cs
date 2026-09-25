using System.Text.Json;

namespace MightyClaude.Core;

/// 설치된 oh-my-claudecode 플러그인의 에이전트 목록과 frontmatter 기본 모델을 읽는다.
///
/// macOS의 `OmcAgentCatalog.swift`와 같은 규칙이다: installed_plugins.json에서
/// "oh-my-claudecode@"로 시작하는 플러그인 id를 찾고, user 범위 기록의
/// installPath 아래 agents/*.md가 있을 때만 목록을 만든다. 없으면 null —
/// 곧 omc가 설치되지 않았다는 뜻이고, 화면에서 omc 묶음이 통째로 빠진다.
///
/// 값의 주인은 여전히 config.jsonc의 agents.&lt;key&gt;.model이다. 여기서 읽는
/// frontmatter `model:`은 화면에 보여 주는 기본값일 뿐, 쓰이지 않는다.
///
/// homeDirectory를 넘기면 검사에서 실제 사용자 파일을 건드리지 않는다.
public sealed class OmcAgentCatalog(string? homeDirectory = null)
{
    public string HomeDirectory { get; } =
        homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string InstalledPluginsPath =>
        Path.Combine(HomeDirectory, ".claude", "plugins", "installed_plugins.json");

    /// [camelCase 키 → frontmatter 기본 모델]. omc가 설치되어 있지 않으면 null.
    public Dictionary<string, string>? Scan()
    {
        var path = InstalledPluginsPath;
        if (!File.Exists(path)) return null;

        JsonElement root;
        try { root = JsonDocument.Parse(File.ReadAllBytes(path)).RootElement; }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("plugins", out var plugins) ||
            plugins.ValueKind != JsonValueKind.Object) return null;

        foreach (var plugin in plugins.EnumerateObject())
        {
            if (!plugin.Name.StartsWith("oh-my-claudecode@", StringComparison.Ordinal)) continue;
            if (plugin.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var record in plugin.Value.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object) continue;
                if (!record.TryGetProperty("scope", out var scope) || scope.GetString() != "user") continue;
                if (!record.TryGetProperty("installPath", out var installPath)) continue;
                var install = installPath.GetString();
                if (install is null) continue;

                var agentsDirectory = Path.Combine(install, "agents");
                if (!Directory.Exists(agentsDirectory)) continue;
                var files = Directory.GetFiles(agentsDirectory, "*.md");
                if (files.Length == 0) continue;

                var result = new Dictionary<string, string>();
                foreach (var file in files)
                    result[KebabToCamelCase(Path.GetFileNameWithoutExtension(file))] =
                        FrontmatterModel(file) ?? "default";
                return result;
            }
        }
        return null;
    }

    /// 파일 이름(kebab-case)을 omc의 키(lowerCamelCase)로 바꾼다.
    /// 한 낱말이면 그대로 둔다: code-reviewer → codeReviewer, verifier → verifier.
    public static string KebabToCamelCase(string value)
    {
        var parts = value.Split('-');
        if (parts.Length < 2) return value;
        return parts[0] + string.Concat(parts.Skip(1).Select(part =>
            part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    /// `---`로 둘러싸인 YAML frontmatter의 `model:` 스칼라. 없으면 null.
    internal static string? FrontmatterModel(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return null; }

        var inside = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "---")
            {
                if (!inside) { inside = true; continue; }
                break;
            }
            if (inside && line.StartsWith("model:", StringComparison.Ordinal))
                return line["model:".Length..].Trim();
        }
        return null;
    }
}
