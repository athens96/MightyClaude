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
//   permission\t<tool>\t<input>         ToolPermissionPresentation.Make -> "<title> <headline|-> <label>:<value>:<code>,…"
//   questionnaire\t<input>              UserQuestionnaire.Parse -> its questions and options, or null
//   markdown\t<text>                    AgentMarkdownImages.Extract -> "<alt>:<source>,…"
//   base64\t<text>                      AgentImageSupport.DecodeBase64 -> the bytes, or "error <error>"
//   filter\t<query>                     SlashCommandCatalog.Filter over a fixed list -> the invocations in order
//   headline\t<plan>                    PlanCardSupport.Headline
//   display\t<width>\t<height>          AgentImageRtf.DisplaySize (numbers as they are)
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
            case "permission":
            {
                var shown = ToolPermissionPresentation.Make(Text(1), Text(2));
                return Hex(shown.Title) + " " + (shown.Headline is { } headline ? Hex(headline) : "-") + " " + string.Join(",", shown.Fields.Select(f => Hex(f.Label) + ":" + Hex(f.Value) + ":" + (f.Code ? 1 : 0)));
            }
            case "questionnaire":
                return UserQuestionnaire.Parse(Text(1)) is { } asked
                    ? string.Join(",", asked.Questions.Select(q => Hex(q.Header) + ":" + Hex(q.Question) + ":" + (q.MultiSelect ? 1 : 0) + ":" + string.Join(";", q.Options.Select(o => Hex(o.Label) + "=" + Hex(o.Description)))))
                    : "null";
            case "markdown": return string.Join(",", AgentMarkdownImages.Extract(Text(1)).Select(i => Hex(i.Alt) + ":" + Hex(i.Source)));
            case "base64":
                try { return Convert.ToHexString(AgentImageSupport.DecodeBase64(Text(1))).ToLowerInvariant(); }
                catch (AgentImageException ex) { return "error " + ex.Error; }
            case "filter":
            {
                SlashCommand[] commands = [.. new[] { "archify", "sc:analyze", "sc:build", "oh-my-claudecode:autopilot", "review", "Plan-Review", "İstanbul", "ß-sharp" }
                    .Select(i => new SlashCommand(i, i == "review" ? "Analyze a PR" : i == "İstanbul" ? "Straße" : "", "x", SlashCommandOrigin.User))];
                return string.Join(",", SlashCommandCatalog.Filter(commands, Text(1)).Select(c => Hex(c.Invocation)));
            }
            case "headline": return Hex(PlanCardSupport.Headline(Text(1)));
            case "display": { var (w, h) = AgentImageRtf.DisplaySize(int.Parse(parts[1]), int.Parse(parts[2])); return $"{w} {h}"; }
            default: return "unknown " + parts[0];
        }
    }
}
