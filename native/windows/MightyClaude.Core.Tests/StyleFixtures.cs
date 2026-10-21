using System.Reflection;
using System.Security.Cryptography;
using MightyClaude.Core;

/// <summary>
/// The test-only manifests of <c>styles/fixtures/</c>, the same files the Mac reads (M/Tests/MightyCoreTests/StyleFixtures.swift
/// <c>fixtureStyle</c>, <c>testCapabilities</c>) and recorded in <c>styles/golden</c> like a style (§8.4): since v7 no bundled style
/// declares state sources or a catalogue fed by a built-in feature, so the checks that need one read these instead. Each is
/// decoded as an approved user style under <see cref="TestCapabilities"/>, the only vocabulary <c>deck</c> decodes under.
/// </summary>
internal static class StyleFixtures
{
    private const string Prefix = "MightyClaude.Core.Tests.Styles.fixtures.";

    /// <summary>Every fixture manifest embedded from <c>styles/fixtures/*.json</c>, by file name.</summary>
    internal static IReadOnlyDictionary<string, byte[]> Files()
    {
        var assembly = Assembly.GetExecutingAssembly(); var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!; using var output = new MemoryStream(); stream.CopyTo(output);
            found[name[Prefix.Length..]] = output.ToArray();
        }
        return found;
    }

    internal static byte[] Data(string id) =>
        Files().TryGetValue(id + ".json", out var bytes) ? bytes : throw new InvalidOperationException("styles/fixtures/" + id + ".json is not embedded");

    internal static RegisteredStyle Style(string id) => Registered(Data(id), "/tmp/" + id + ".json", TestCapabilities);

    /// <summary>The app's own vocabulary of built-in features is empty since v7 (§1.8); the checks of the field and the two rules that name a feature run against this one instead.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> TestCapabilities = new Dictionary<string, string[]> { ["acme.thing"] = ["absent", "open", "complete"] };

    /// <summary>A user style, approved, as the registry would hold it.</summary>
    internal static RegisteredStyle Registered(byte[] bytes, string path, IReadOnlyDictionary<string, string[]>? capabilities = null) =>
        new(StyleManifestDecoder.Decode(bytes, "user", capabilities), "user", path, null, Convert.ToHexStringLower(SHA256.HashData(bytes)), "approved", bytes);

    /// <summary><c>styles/fixtures/loop.json</c>: a four-phase loop that reads its own state (§1.16) — the newest current checklist under <c>docs/plans/</c> as a bar, <c>subagent.start</c> as a count, and two state overrides that move the phase.</summary>
    internal static RegisteredStyle StatefulStyle => Style("loop");

    /// <summary><c>styles/fixtures/deck.json</c>: a catalogue — no phases, two groups drawn as a map, Enter verbatim, a recommendation and first group read from a built-in feature that exists only in <see cref="TestCapabilities"/> (§1.8), and one action carrying both flags.</summary>
    internal static RegisteredStyle CatalogueStyle => Style("deck");
}
