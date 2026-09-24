using MightyClaude.Core;

// Windows parses toolkit.json but never installs: file-reading only.
internal static class ToolkitFileVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // toolkit.json containing all five install template kinds plus approval data.
    private const string AllFiveTemplates = """
        {
          "version": 1,
          "entries": [
            {
              "id": "my-plugin",
              "displayName": "My Plugin",
              "install": {
                "kind": "plugin",
                "source": "athens96/mighty-styles",
                "pluginID": "mighty-styles@mighty-styles"
              },
              "approval": {
                "contentHash": "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890"
              }
            },
            {
              "id": "my-mcp",
              "displayName": "My MCP Server",
              "install": {
                "kind": "mcp",
                "name": "my-mcp",
                "executable": "/usr/local/bin/my-mcp-server",
                "args": ["--port", "3000"]
              }
            },
            {
              "id": "my-skill",
              "displayName": "My Skill",
              "install": {
                "kind": "skill",
                "url": "https://github.com/example/my-skill.git"
              },
              "approval": {
                "contentHash": "fedcba0987654321fedcba0987654321fedcba0987654321fedcba0987654321"
              }
            },
            {
              "id": "my-package",
              "displayName": "My Brew Package",
              "install": {
                "kind": "package",
                "manager": "brew",
                "name": "ripgrep"
              }
            },
            {
              "id": "my-repo-script",
              "displayName": "My Repo Script",
              "install": {
                "kind": "repoScript",
                "url": "https://github.com/example/setup-scripts.git",
                "ref": "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
                "scriptPath": "scripts/install.sh"
              },
              "approval": {
                "contentHash": "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
                "resolvedCommit": "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
              }
            }
          ]
        }
        """;

    internal static Task ToolkitFileParsesAllFiveTemplates()
    {
        var file = ToolkitFileReader.Parse(AllFiveTemplates);
        Check(file.Entries.Count == 5, "should have 5 entries, got " + file.Entries.Count);

        // plugin entry
        var plugin = file.Entries[0];
        Check(plugin.Id == "my-plugin", "plugin id");
        Check(plugin.DisplayName == "My Plugin", "plugin displayName");
        Check(plugin.InstallKind == "plugin", "plugin kind");
        Check(plugin.Install is ToolkitFileReader.PluginSpec ps && ps.Source == "athens96/mighty-styles" && ps.PluginId == "mighty-styles@mighty-styles", "plugin spec");
        Check(plugin.Approval is { ContentHash.Length: > 0 }, "plugin approval");
        Check(plugin.Approval!.ResolvedCommit is null, "plugin approval has no resolvedCommit");

        // mcp entry
        var mcp = file.Entries[1];
        Check(mcp.Id == "my-mcp", "mcp id");
        Check(mcp.InstallKind == "mcp", "mcp kind");
        Check(mcp.Install is ToolkitFileReader.McpSpec ms && ms.Name == "my-mcp" && ms.Executable == "/usr/local/bin/my-mcp-server" && ms.Args.Count == 2, "mcp spec");
        Check(mcp.Approval is null, "mcp has no approval");

        // skill entry
        var skill = file.Entries[2];
        Check(skill.Id == "my-skill", "skill id");
        Check(skill.InstallKind == "skill", "skill kind");
        Check(skill.Install is ToolkitFileReader.SkillSpec ss && ss.Url == "https://github.com/example/my-skill.git", "skill spec");
        Check(skill.Approval is { ContentHash.Length: > 0 }, "skill approval");

        // package entry
        var package = file.Entries[3];
        Check(package.Id == "my-package", "package id");
        Check(package.InstallKind == "package", "package kind");
        Check(package.Install is ToolkitFileReader.PackageSpec pks && pks.Manager == "brew" && pks.Name == "ripgrep", "package spec");
        Check(package.Approval is null, "package has no approval");

        // repoScript entry
        var repo = file.Entries[4];
        Check(repo.Id == "my-repo-script", "repoScript id");
        Check(repo.InstallKind == "repoScript", "repoScript kind");
        Check(repo.Install is ToolkitFileReader.RepoScriptSpec rs
            && rs.Url == "https://github.com/example/setup-scripts.git"
            && rs.Ref == "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"
            && rs.ScriptPath == "scripts/install.sh", "repoScript spec");
        Check(repo.Approval is { ResolvedCommit: "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2" }, "repoScript approval has resolvedCommit");

        return Task.CompletedTask;
    }

    internal static Task ToolkitFileRefusesVersionMismatch()
    {
        try { ToolkitFileReader.Parse("{\"version\":2,\"entries\":[]}"); }
        catch (InvalidDataException) { return Task.CompletedTask; }
        throw new InvalidOperationException("Expected failure for wrong version");
    }

    internal static Task ToolkitFileSkipsUnknownKinds()
    {
        var json = """
            {
              "version": 1,
              "entries": [
                {
                  "id": "good",
                  "displayName": "Good",
                  "install": { "kind": "skill", "url": "https://github.com/example/x.git" }
                },
                {
                  "id": "bad",
                  "displayName": "Bad",
                  "install": { "kind": "shell", "command": "rm -rf /" }
                }
              ]
            }
            """;
        var file = ToolkitFileReader.Parse(json);
        Check(file.Entries.Count == 1 && file.Entries[0].Id == "good",
            "unknown kind is skipped; only the valid entry remains");
        return Task.CompletedTask;
    }

    internal static Task NpmPackageNameIsAccepted()
    {
        var json = """
            {
              "version": 1,
              "entries": [
                {
                  "id": "my-npm-tool",
                  "displayName": "NPM Tool",
                  "install": { "kind": "package", "manager": "npm", "name": "typescript" }
                }
              ]
            }
            """;
        var file = ToolkitFileReader.Parse(json);
        Check(file.Entries.Count == 1 && file.Entries[0].Install is ToolkitFileReader.PackageSpec { Manager: "npm", Name: "typescript" },
            "npm package entry parsed");
        return Task.CompletedTask;
    }
}
