import Foundation
func ranges(_ predicate: (Unicode.Scalar.Properties) -> Bool) -> String {
    var values: [(UInt32, UInt32)] = []
    for code in UInt32(0)...UInt32(0x10ffff) {
        guard let scalar = Unicode.Scalar(code), predicate(scalar.properties) else { continue }
        if let last = values.last, last.1 + 1 == code { values[values.count - 1] = (last.0, code) }
        else { values.append((code, code)) }
    }
    return values.map { "(0x" + String($0.0, radix: 16) + ",0x" + String($0.1, radix: 16) + ")" }.joined(separator: ", ")
}
print("namespace MightyClaude.Core;\n")
print("// Generated from the reference macOS Swift Unicode.Scalar.Properties emoji tables.\n// A glyph is one grapheme with Emoji on its first scalar and Presentation or VS16.\ninternal static class StyleEmoji\n{")
print("    private static readonly (int First,int Last)[] Emoji = [" + ranges { $0.isEmoji } + "];")
print("    private static readonly (int First,int Last)[] Presentation = [" + ranges { $0.isEmojiPresentation } + "];")
print("""
    internal static bool Valid(string value)
    {
        if(StyleText.Length(value)!=1)return false;
        var scalars=value.EnumerateRunes().ToArray();if(scalars.Length==0)return false;
        var first=scalars[0].Value;return Emoji.Any(r=>first>=r.First&&first<=r.Last)&&(Presentation.Any(r=>first>=r.First&&first<=r.Last)||scalars.Any(r=>r.Value==0xFE0F));
    }
}
""")
