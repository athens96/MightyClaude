// The composer's part of the parity checks (crates/mighty-composer/tests/parity.rs): `composer <in> <out>` reads one vector a line,
// "<kind>\t<arguments…>" with every byte string in hex, runs the MightyCode.Core function the kind names and writes one answer a line.
//
//   inspect\t<declared type>\t<bytes>  AgentImageSupport.Inspect -> "ok <type> <width> <height>" or "error <AgentImageError>"
//   external\t<bytes>                   FilePreviewClassifier.SvgLoadsExternalContent -> true | false
//   svgsize\t<bytes>                    FilePreviewClassifier.SvgSize -> "<width bits> <height bits>" (hex IEEE-754) or null
//   svgdoc\t<bytes>                     AgentImageSupport.SvgDocument -> true | false
//   safesvg\t<bytes>                    SafeSvgDocument.Parse -> "ok <width bits> <height bits> <xml as hex UTF-8>" or "error <error>"
//   decode\t<0|1>\t<bytes>              FilePreviewClassifier.DecodeText(bytes, sample) -> "<TextEncodingKind> <text as hex UTF-8>" or null
//   next\t<text>                        NextActions.Parse -> "<label>:<fill>,…" (hex UTF-8)
//   media\t<bytes>                      AttachmentSupport.DetectMediaType
//   make\t<name>                        AttachmentSupport.Make(name, "x").Name as hex UTF-8, or "error"
//   frontmatter\t<text>                 SlashCommandCatalog.Frontmatter -> "<key>:<value>,…" sorted by key (hex UTF-8)
using System.Text;
using System.Text.Json;
using MightyCode.Core;

namespace MightyCode.Parity;

internal static class ComposerParity
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static string Hex(string text) => Convert.ToHexString(Utf8.GetBytes(text)).ToLowerInvariant();
    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("x16");

    internal static int Run(string input, string output)
    {
        Locale.LanguagePreference = "ko"; Locale.ResetCache();
        var answers = File.ReadAllLines(input, Utf8).Where(line => line.Length > 0).Select(Answer);
        File.WriteAllText(output, string.Join("\n", answers) + "\n", Utf8);
        return 0;
    }

    private static string Answer(string line)
    {
        var parts = line.Split('\t');
        byte[] Bytes(int at) => Convert.FromHexString(parts[at]);
        string Text(int at) => Utf8.GetString(Bytes(at));
        switch (parts[0])
        {
            case "inspect":
                try { var (type, width, height) = AgentImageSupport.Inspect(Bytes(2), parts[1]); return $"ok {type} {width} {height}"; }
                catch (AgentImageException ex) { return "error " + ex.Error; }
            case "external": return FilePreviewClassifier.SvgLoadsExternalContent(Bytes(1)) ? "true" : "false";
            case "svgsize": return FilePreviewClassifier.SvgSize(Bytes(1)) is { } size ? Bits(size.Width) + " " + Bits(size.Height) : "null";
            case "svgdoc": return AgentImageSupport.SvgDocument(Bytes(1)) ? "true" : "false";
            case "safesvg":
                try { var safe = SafeSvgDocument.Parse(Bytes(1)); return $"ok {Bits(safe.Width)} {Bits(safe.Height)} {Hex(safe.Xml)}"; }
                catch (AgentImageException ex) { return "error " + ex.Error; }
            case "decode":
                return FilePreviewClassifier.DecodeText(Bytes(2), parts[1] == "1") is { } decoded ? decoded.Encoding + " " + Hex(decoded.Text) : "null";
            case "next": return string.Join(",", NextActions.Parse(Text(1)).Select(a => Hex(a.Label) + ":" + Hex(a.Fill)));
            case "media": return AttachmentSupport.DetectMediaType(Bytes(1));
            case "make":
                try { return Hex(AttachmentSupport.Make(Text(1), "x"u8.ToArray()).Name); }
                catch (ArgumentException) { return "error"; }
            case "frontmatter": return string.Join(",", SlashCommandCatalog.Frontmatter(Text(1)).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Hex(p.Key) + ":" + Hex(p.Value)));
            default: return "unknown " + parts[0];
        }
    }
}
