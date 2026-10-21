using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

// The first-run setup screen's tools (필요한 도구), read from the catalog the app ships
// (native/contracts/fixtures/setup-catalog.json, embedded as is). The decoder is closed, as the
// Mac's is (M/MightyCore/SetupCatalog.swift): an unknown key, kind, host or program is an error,
// so the catalog can only ask for what this file knows how to probe and install.

public enum SetupGroup { Agent, Prerequisite, Style, Network, Dev }

public enum SetupPlatform { MacOS, Windows }

public enum SetupAfterInstallKind { None, Reprobe, SignIn }

public sealed record SetupAfterInstall(SetupAfterInstallKind Kind, string? Provider = null);

/// <summary>How an item is found. The probe, never an exit code, decides whether it is there.</summary>
public abstract record SetupProbe
{
    /// <summary>On the PATH (and, on Windows, in the folders the official installers use); with a minimum, <c>--version</c> must reach it.</summary>
    public sealed record Executable(string Name, string? MinimumVersion) : SetupProbe;
    /// <summary>A user-scope Claude plugin whose ID starts with <see cref="Prefix"/> (<c>ouroboros@</c>).</summary>
    public sealed record ClaudePlugin(string Prefix) : SetupProbe;
    /// <summary><c>~/.claude/skills/&lt;name&gt;/SKILL.md</c>.</summary>
    public sealed record Skill(string Name) : SetupProbe;
    /// <summary>Mac only: <c>xcode-select -p</c>, then git.</summary>
    public sealed record CommandLineTools : SetupProbe;
    /// <summary>Mac only: an app bundle, or its executable on the PATH.</summary>
    public sealed record AppBundle(string Name, string? ExecutableName) : SetupProbe;
}

/// <summary>How an item is installed.</summary>
public abstract record SetupRecipe
{
    /// <summary>One of <see cref="SetupCatalog.AllowedFlags"/>' programs with fixed arguments.</summary>
    public sealed record Argv(IReadOnlyList<string> Arguments, bool FinishesOutside) : SetupRecipe;
    /// <summary>A fixed https script on an allow-listed host; PowerShell on Windows.</summary>
    public sealed record OfficialScript(Uri Url, string Shell) : SetupRecipe;
    public sealed record BrewFormula(string Name) : SetupRecipe;
    public sealed record BrewCask(string Name) : SetupRecipe;
    public sealed record NpmGlobal(string Package) : SetupRecipe;
    public sealed record ClaudePlugin(string Marketplace, string Plugin) : SetupRecipe;
    /// <summary>Mac only: a repository pinned to a commit in <c>~/.claude/skills/&lt;folder&gt;</c>, then a script inside it.</summary>
    public sealed record Skill(Uri Repository, string Folder, string Commit, string? Setup) : SetupRecipe;
    /// <summary>Windows only: <c>winget install</c> of an exact package ID from the winget source, in user or machine scope.</summary>
    public sealed record Winget(string Id, string Scope) : SetupRecipe;
    /// <summary>Nothing runs: the app opens the page.</summary>
    public sealed record VendorPage(Uri Url) : SetupRecipe;

    public bool RunsInTerminal => this is not VendorPage;
}

public sealed record SetupPlatformEntry(SetupProbe Probe, SetupRecipe Install, IReadOnlyList<string> Prerequisites, Uri Source);

public sealed record SetupItem(string Id, SetupGroup Group, string LabelKey, string DescriptionKey, bool Required, SetupAfterInstall AfterInstall, IReadOnlyDictionary<SetupPlatform, SetupPlatformEntry> Platforms)
{
    /// <summary>The agent CLI's provider ID when this item is one.</summary>
    public string? Provider => AfterInstall.Kind == SetupAfterInstallKind.SignIn ? AfterInstall.Provider : null;
    public SetupPlatformEntry? Entry(SetupPlatform platform) => Platforms.GetValueOrDefault(platform);
}

public sealed class SetupCatalog(IReadOnlyList<SetupItem> items)
{
    public IReadOnlyList<SetupItem> Items { get; } = items;
    public SetupItem? Item(string id) => Items.FirstOrDefault(item => item.Id == id);

    public static string GroupTitleKey(SetupGroup group) => group switch
    {
        SetupGroup.Agent => "setup.group.agent", SetupGroup.Prerequisite => "setup.group.prerequisite", SetupGroup.Style => "setup.group.style",
        SetupGroup.Network => "setup.group.network", _ => "setup.group.dev",
    };

    /// <summary>Hosts an official script, a vendor page, a marketplace URL or a skill's repository may live on. The catalog cannot add one.</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude.ai", "chatgpt.com", "antigravity.google", "raw.githubusercontent.com", "astral.sh", "bun.com", "github.com", "tailscale.com",
    };
    /// <summary>The only programs an <c>argv</c> install may start, each with the only options it may be given.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedFlags = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["xcode-select"] = new HashSet<string>(StringComparer.Ordinal) { "--install" },
        ["npx"] = new HashSet<string>(StringComparer.Ordinal) { "--global", "--agent" },
    };
    /// <summary>The GitHub repositories a URL or a marketplace's owner/repo short form may name: only those the catalog uses.</summary>
    public static readonly IReadOnlySet<string> AllowedRepositories = new HashSet<string>(StringComparer.Ordinal)
    {
        "Homebrew/install", "Q00/ouroboros", "anthropics/claude-plugins-community", "Yeachan-Heo/oh-my-claudecode", "garrytan/gstack",
    };
    /// <summary>Every label and description key a catalog item may name, as the Mac keeps them (SetupCatalog.knownKeys).</summary>
    public static readonly IReadOnlySet<string> KnownKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "setup.item.claude.title", "setup.item.claude.detail", "setup.item.codex.title", "setup.item.codex.detail",
        "setup.item.agy.title", "setup.item.agy.detail", "setup.item.git.title", "setup.item.git.detail",
        "setup.item.homebrew.title", "setup.item.homebrew.detail", "setup.item.node.title", "setup.item.node.detail",
        "setup.item.bun.title", "setup.item.bun.detail", "setup.item.uv.title", "setup.item.uv.detail",
        "setup.item.ouroboros.title", "setup.item.ouroboros.detail", "setup.item.omc.title", "setup.item.omc.detail",
        "setup.item.gstack.title", "setup.item.gstack.detail", "setup.item.tailscale.title", "setup.item.tailscale.detail",
        "setup.item.gh.title", "setup.item.gh.detail", "setup.item.glab.title", "setup.item.glab.detail",
    };

    /// <summary>
    /// Where Windows words an item differently from the Mac (its installer, its folder): the Windows key, read instead of the catalog's.
    /// The catalog keeps one label per item, so these stay with the app, as the catalog's own keys do.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> WindowsTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["git"] = "setup.windows.item.git.title",
    };
    private static readonly IReadOnlyDictionary<string, string> WindowsDetails = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["codex"] = "setup.windows.item.codex.detail", ["agy"] = "setup.windows.item.agy.detail", ["git"] = "setup.windows.item.git.detail",
        ["node"] = "setup.windows.item.node.detail", ["gh"] = "setup.windows.item.gh.detail", ["glab"] = "setup.windows.item.glab.detail",
    };
    public static string TitleKey(SetupItem item) => WindowsTitles.GetValueOrDefault(item.Id) ?? item.LabelKey;
    public static string DetailKey(SetupItem item) => WindowsDetails.GetValueOrDefault(item.Id) ?? item.DescriptionKey;
    public static string Title(SetupItem item) => Locale.Get(TitleKey(item));
    public static string Detail(SetupItem item) => Locale.Get(DetailKey(item));

    /// <summary>The catalog the app carries: the contract file itself, embedded at build time.</summary>
    public static SetupCatalog Bundled()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MightyClaude.Core.Setup.setup-catalog.json")
            ?? throw new InvalidDataException("setup-catalog.json: the app carries no catalog");
        using var reader = new StreamReader(stream);
        return SetupCatalogDecoder.Decode(reader.ReadToEnd());
    }
}

public static partial class SetupCatalogDecoder
{
    public static SetupCatalog Decode(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw Fail("the catalog is not a JSON object"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Fail("the catalog is not a JSON object");
            Keys(root, ["schema", "note", "items"], "catalog");
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1) throw Fail("schema must be 1");
            if (!root.TryGetProperty("items", out var rawItems) || rawItems.ValueKind != JsonValueKind.Array || rawItems.GetArrayLength() is 0 or > 64) throw Fail("items must be 1 to 64 objects");
            var items = rawItems.EnumerateArray().Select((raw, index) => Item(raw, $"items[{index}]")).ToList();
            var ids = items.Select(item => item.Id).ToList();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw Fail("item IDs repeat");
            foreach (var item in items)
                foreach (var (platform, entry) in item.Platforms)
                    foreach (var prerequisite in entry.Prerequisites)
                        if (prerequisite == item.Id || !ids.Contains(prerequisite)) throw Fail($"{item.Id}: unknown prerequisite {prerequisite} on {Name(platform)}");
            var catalog = new SetupCatalog(items);
            foreach (var platform in Enum.GetValues<SetupPlatform>())
                if (!SetupPlan.HasNoCycle(catalog, platform)) throw Fail("prerequisites form a cycle on " + Name(platform));
            return catalog;
        }
    }

    private static string Name(SetupPlatform platform) => platform == SetupPlatform.MacOS ? "macos" : "windows";

    private static SetupItem Item(JsonElement raw, string path)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw Fail(path + " is not an object");
        Keys(raw, ["id", "group", "label", "description", "need", "afterInstall", "platforms"], path);
        var id = String(raw, "id", path);
        if (!PlainId().IsMatch(id)) throw Fail(path + ".id is not a plain ID");
        var group = String(raw, "group", path) switch
        {
            "agent" => SetupGroup.Agent, "prerequisite" => SetupGroup.Prerequisite, "style" => SetupGroup.Style, "network" => SetupGroup.Network, "dev" => SetupGroup.Dev,
            _ => throw Fail(path + ".group is unknown"),
        };
        var label = String(raw, "label", path); var description = String(raw, "description", path);
        if (!SetupCatalog.KnownKeys.Contains(label) || !SetupCatalog.KnownKeys.Contains(description)) throw Fail(path + " names a label key the app does not know");
        var need = String(raw, "need", path);
        if (need is not ("required" or "optional")) throw Fail(path + ".need must be required or optional");
        var after = AfterInstall(raw, path + ".afterInstall");
        if (!raw.TryGetProperty("platforms", out var rawPlatforms) || rawPlatforms.ValueKind != JsonValueKind.Object || !rawPlatforms.EnumerateObject().Any()) throw Fail(path + ".platforms is missing");
        var platforms = new Dictionary<SetupPlatform, SetupPlatformEntry>();
        foreach (var property in rawPlatforms.EnumerateObject())
        {
            var platform = property.Name switch { "macos" => SetupPlatform.MacOS, "windows" => SetupPlatform.Windows, _ => throw Fail($"{path}.platforms.{property.Name} is not a platform") };
            if (platforms.ContainsKey(platform)) throw Fail($"{path}.platforms.{property.Name} repeats");
            platforms[platform] = Entry(property.Value, platform, $"{path}.platforms.{property.Name}");
        }
        return new(id, group, label, description, need == "required", after, platforms);
    }

    private static SetupAfterInstall AfterInstall(JsonElement parent, string path)
    {
        if (!parent.TryGetProperty("afterInstall", out var raw) || raw.ValueKind != JsonValueKind.Object) throw Fail(path + " is missing");
        switch (String(raw, "kind", path))
        {
            case "none": Keys(raw, ["kind"], path); return new(SetupAfterInstallKind.None);
            case "reprobe": Keys(raw, ["kind"], path); return new(SetupAfterInstallKind.Reprobe);
            case "signIn":
                Keys(raw, ["kind", "provider"], path);
                var provider = String(raw, "provider", path);
                if (!Wire.Providers.Contains(provider)) throw Fail(path + ".provider is not an agent");
                return new(SetupAfterInstallKind.SignIn, provider);
            case var kind: throw Fail($"{path}.kind {kind} is unknown");
        }
    }

    private static SetupPlatformEntry Entry(JsonElement raw, SetupPlatform platform, string path)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw Fail(path + " is not an object");
        Keys(raw, ["probe", "install", "prerequisites", "source"], path);
        if (!raw.TryGetProperty("prerequisites", out var rawPrerequisites) || rawPrerequisites.ValueKind != JsonValueKind.Array || rawPrerequisites.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
            throw Fail(path + ".prerequisites must be distinct IDs");
        var prerequisites = rawPrerequisites.EnumerateArray().Select(value => value.GetString()!).ToList();
        if (prerequisites.Distinct(StringComparer.Ordinal).Count() != prerequisites.Count) throw Fail(path + ".prerequisites must be distinct IDs");
        if (!Uri.TryCreate(String(raw, "source", path), UriKind.Absolute, out var source) || source.Scheme != "https" || source.Host.Length == 0) throw Fail(path + ".source must be an https URL");
        if (!raw.TryGetProperty("probe", out var probe) || probe.ValueKind != JsonValueKind.Object) throw Fail(path + ".probe is missing");
        if (!raw.TryGetProperty("install", out var install) || install.ValueKind != JsonValueKind.Object) throw Fail(path + ".install is missing");
        return new(Probe(probe, platform, path + ".probe"), Recipe(install, platform, path + ".install"), prerequisites, source);
    }

    private static SetupProbe Probe(JsonElement raw, SetupPlatform platform, string path)
    {
        switch (String(raw, "kind", path))
        {
            case "executable":
                Keys(raw, ["kind", "name", "minimumVersion"], path);
                var name = PlainName(raw, "name", path);
                string? minimum = null;
                if (raw.TryGetProperty("minimumVersion", out _))
                {
                    minimum = String(raw, "minimumVersion", path);
                    if (!VersionPattern().IsMatch(minimum)) throw Fail(path + ".minimumVersion is not a version");
                }
                return new SetupProbe.Executable(name, minimum);
            case "claudePlugin":
                Keys(raw, ["kind", "prefix"], path);
                var prefix = String(raw, "prefix", path);
                if (!PluginPrefix().IsMatch(prefix)) throw Fail(path + ".prefix must name a whole plugin (name@)");
                return new SetupProbe.ClaudePlugin(prefix);
            case "skill":
                Keys(raw, ["kind", "name"], path);
                return new SetupProbe.Skill(FolderName(raw, "name", path));
            case "commandLineTools":
                Keys(raw, ["kind"], path);
                if (platform != SetupPlatform.MacOS) throw Fail(path + ": commandLineTools is a Mac probe");
                return new SetupProbe.CommandLineTools();
            case "appBundle":
                Keys(raw, ["kind", "name", "executable"], path);
                var bundle = String(raw, "name", path);
                if (platform != SetupPlatform.MacOS || !AppBundleName().IsMatch(bundle)) throw Fail(path + ".name must be an app bundle's name");
                return new SetupProbe.AppBundle(bundle, raw.TryGetProperty("executable", out _) ? PlainName(raw, "executable", path) : null);
            case var kind: throw Fail($"{path}.kind {kind} is unknown");
        }
    }

    private static SetupRecipe Recipe(JsonElement raw, SetupPlatform platform, string path)
    {
        switch (String(raw, "kind", path))
        {
            case "argv":
            {
                Keys(raw, ["kind", "argv", "finishesOutside"], path);
                if (!raw.TryGetProperty("argv", out var rawArgv) || rawArgv.ValueKind != JsonValueKind.Array || rawArgv.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    throw Fail(path + ".argv must start with an allowed program");
                var argv = rawArgv.EnumerateArray().Select(value => value.GetString()!).ToList();
                if (argv.Count is 0 or > 16 || !SetupCatalog.AllowedFlags.TryGetValue(argv[0], out var flags)) throw Fail(path + ".argv must start with an allowed program");
                if (argv[0] == "xcode-select" && platform != SetupPlatform.MacOS) throw Fail(path + ".argv: xcode-select is a Mac program");
                foreach (var argument in argv.Skip(1))
                {
                    if (!ArgvArgument().IsMatch(argument)) throw Fail(path + ".argv has an argument with characters the catalog does not allow");
                    if (argument.StartsWith('-') && !flags.Contains(argument)) throw Fail($"{path}.argv gives {argv[0]} an option it may not take: {argument}");
                }
                var outside = false;
                if (raw.TryGetProperty("finishesOutside", out var rawOutside))
                {
                    if (rawOutside.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Fail(path + ".finishesOutside must be true or false");
                    outside = rawOutside.GetBoolean();
                }
                return new SetupRecipe.Argv(argv, outside);
            }
            case "officialScript":
            {
                Keys(raw, ["kind", "url", "shell"], path);
                var url = AllowedUrl(raw, "url", path);
                var shell = String(raw, "shell", path);
                if (platform == SetupPlatform.MacOS ? shell is not ("bash" or "sh") : shell != "powershell") throw Fail($"{path}.shell {shell} is not one this platform runs");
                return new SetupRecipe.OfficialScript(url, shell);
            }
            case "brewFormula":
                Keys(raw, ["kind", "name"], path);
                if (platform != SetupPlatform.MacOS) throw Fail(path + ": Homebrew is a Mac install");
                return new SetupRecipe.BrewFormula(PackageName(raw, "name", path));
            case "brewCask":
                Keys(raw, ["kind", "name"], path);
                if (platform != SetupPlatform.MacOS) throw Fail(path + ": Homebrew is a Mac install");
                return new SetupRecipe.BrewCask(PackageName(raw, "name", path));
            case "npmGlobal":
                Keys(raw, ["kind", "package"], path);
                return new SetupRecipe.NpmGlobal(PackageName(raw, "package", path));
            case "claudePlugin":
            {
                Keys(raw, ["kind", "marketplace", "plugin"], path);
                var marketplace = String(raw, "marketplace", path);
                if (RepositoryShortForm().IsMatch(marketplace))
                {
                    if (marketplace.Split('/').Any(part => part is "." or "..") || !SetupCatalog.AllowedRepositories.Contains(marketplace)) throw Fail(path + ".marketplace is not a repository the catalog may use");
                }
                else AllowedUrl(marketplace, path + ".marketplace");
                var plugin = String(raw, "plugin", path);
                if (!PluginId().IsMatch(plugin)) throw Fail(path + ".plugin must be name@marketplace");
                return new SetupRecipe.ClaudePlugin(marketplace, plugin);
            }
            case "skill":
            {
                Keys(raw, ["kind", "repository", "folder", "commit", "setup"], path);
                // A skill's setup script is a POSIX shell script: Windows has no shell the app could vouch for to run it.
                if (platform != SetupPlatform.MacOS) throw Fail(path + ": a pinned skill is a Mac install");
                var repository = AllowedUrl(raw, "repository", path);
                var folder = FolderName(raw, "folder", path);
                var commit = String(raw, "commit", path);
                if (!CommitSha().IsMatch(commit)) throw Fail(path + ".commit must be a full commit SHA");
                string? setup = null;
                if (raw.TryGetProperty("setup", out _))
                {
                    setup = String(raw, "setup", path);
                    if (setup.StartsWith('/') || setup.Split('/').Any(part => part is "" or "." or "..") || !SetupPath().IsMatch(setup)) throw Fail(path + ".setup must be a path inside the clone");
                }
                return new SetupRecipe.Skill(repository, folder, commit, setup);
            }
            case "winget":
            {
                Keys(raw, ["kind", "id", "scope"], path);
                if (platform != SetupPlatform.Windows) throw Fail(path + ": winget is a Windows install");
                var id = String(raw, "id", path);
                if (!WingetId().IsMatch(id)) throw Fail(path + ".id is not a winget package ID");
                var scope = String(raw, "scope", path);
                if (scope is not ("user" or "machine")) throw Fail(path + ".scope must be user or machine");
                return new SetupRecipe.Winget(id, scope);
            }
            case "vendorPage":
                Keys(raw, ["kind", "url"], path);
                return new SetupRecipe.VendorPage(AllowedUrl(raw, "url", path));
            case var kind: throw Fail($"{path}.kind {kind} is unknown");
        }
    }

    // ── Fields ─────────────────────────────────────────────────────────────────

    private static InvalidDataException Fail(string message) => new("setup-catalog.json: " + message);

    private static void Keys(JsonElement raw, string[] allowed, string path)
    {
        if (raw.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).FirstOrDefault(name => !allowed.Contains(name)) is { } extra) throw Fail($"{path} has an unknown key {extra}");
    }

    private static string String(JsonElement raw, string key, string path) =>
        raw.TryGetProperty(key, out var value) ? Text(value, path + "." + key) : throw Fail($"{path}.{key} must be a string");

    private static string Text(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 512 } text) throw Fail(path + " must be a string");
        if (text.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)) throw Fail(path + " has a control character");
        return text;
    }

    private static string PlainName(JsonElement raw, string key, string path) =>
        String(raw, key, path) is var name && ProgramName().IsMatch(name) ? name : throw Fail($"{path}.{key} is not a program name");

    private static string FolderName(JsonElement raw, string key, string path) =>
        String(raw, key, path) is var name && FolderPattern().IsMatch(name) && name != ".." ? name : throw Fail($"{path}.{key} is not a folder name");

    private static string PackageName(JsonElement raw, string key, string path) =>
        String(raw, key, path) is var name && PackagePattern().IsMatch(name) ? name : throw Fail($"{path}.{key} is not a package name");

    private static Uri AllowedUrl(JsonElement raw, string key, string path) => AllowedUrl(String(raw, key, path), $"{path}.{key}");

    private static Uri AllowedUrl(string text, string path)
    {
        if (!UrlPattern().IsMatch(text) || !Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != "https" || !SetupCatalog.AllowedHosts.Contains(url.Host.ToLowerInvariant())
            || url.UserInfo.Length > 0 || !url.IsDefaultPort) throw Fail(path + " must be an https URL on an allowed host");
        // GitHub hosts many owners: only the repositories the catalog uses are let in.
        if (url.Host is "github.com" or "raw.githubusercontent.com")
        {
            var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // The text, not the normalised address: a dot segment is refused before the Uri folds it away.
            if (parts.Length < 2 || text.Split('/').Any(part => part is "." or "..")) throw Fail(path + " does not name a repository");
            var repository = parts[0] + "/" + (parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1]);
            if (!SetupCatalog.AllowedRepositories.Contains(repository)) throw Fail(path + " names a repository the catalog may not use");
        }
        return url;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z")] private static partial Regex PlainId();
    [GeneratedRegex(@"^[0-9]+(\.[0-9]+){0,2}\z")] private static partial Regex VersionPattern();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}@\z")] private static partial Regex PluginPrefix();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}\.app\z")] private static partial Regex AppBundleName();
    [GeneratedRegex(@"^[A-Za-z0-9@._/:=+-]{1,128}\z")] private static partial Regex ArgvArgument();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}/[A-Za-z0-9._-]{1,100}\z")] private static partial Regex RepositoryShortForm();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}@[a-z0-9][a-z0-9._-]{0,63}\z")] private static partial Regex PluginId();
    [GeneratedRegex(@"^[0-9a-f]{40}\z")] private static partial Regex CommitSha();
    [GeneratedRegex(@"^[A-Za-z0-9._/-]{1,128}\z")] private static partial Regex SetupPath();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,63}(\.[A-Za-z0-9][A-Za-z0-9-]{0,63}){1,3}\z")] private static partial Regex WingetId();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}\z")] private static partial Regex ProgramName();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}\z")] private static partial Regex FolderPattern();
    [GeneratedRegex(@"^@?[a-z0-9][a-z0-9._-]{0,99}(/[a-z0-9][a-z0-9._-]{0,99})?\z")] private static partial Regex PackagePattern();
    [GeneratedRegex(@"^https://[A-Za-z0-9./_-]+\z")] private static partial Regex UrlPattern();
}
