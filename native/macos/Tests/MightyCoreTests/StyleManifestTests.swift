import Foundation
import Testing
@testable import MightyCore

/// One refusing fixture for every code of docs/mighty-styles.md §2, and the
/// smallest manifest that passes.
struct StyleManifestTests {
    @Test func theSmallestValidManifestDecodes() throws {
        let manifest = try StyleFixtures.manifest()
        #expect(manifest.schema == 1 && manifest.id == "flow" && manifest.actions.count == 1)
        #expect(manifest.prerequisites.report == .first && manifest.prerequisites.probes.isEmpty)
        #expect(manifest.actions[0].foldText == nil && manifest.actions[0].requiresText == false)
        #expect(manifest.presentation.icon == nil && manifest.presentation.tint == nil)
        // A bundled id is only reserved against the other two sources.
        #expect(StyleFixtures.code(StyleFixtures.data(StyleFixtures.flat, ["id": "\"ouroboros\""]), source: .bundled) == nil)
    }

    @Test func everyErrorCodeHasARefusingFixture() throws {
        let f = StyleFixtures.self
        var cases: [(String, Data)] = []

        // ⓪ pre-scan and ① size
        cases.append(("E_TOO_LARGE", Data(repeating: UInt8(ascii: " "), count: StyleLimits.maximumBytes + 1)))
        cases.append(("E_TOO_DEEP", f.data(f.flat, ["guidance": "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":1}}}}}}}}"])))
        cases.append(("E_DUPLICATE_KEY", Data(f.text().replacingOccurrences(of: "\"enter\":{\"kind\":\"verbatim\"}",
                                                                            with: "\"enter\":{\"kind\":\"verbatim\"},\"enter\":{\"kind\":\"verbatim\"}").utf8)))
        cases.append(("E_SCHEMA_NOT_FIRST", Data("{\"id\":\"flow\",\"schema\":1}".utf8)))
        cases.append(("E_NOT_JSON", Data("{\"schema\": 1, ".utf8)))
        cases.append(("E_SCHEMA_MISSING", Data("{}".utf8)))
        cases.append(("E_SCHEMA_VERSION", f.data(f.flat, ["schema": "2"])))

        // ③ structure and types
        cases.append(("E_UNKNOWN_FIELD", f.data(f.flat, [:], drop: [], extra: [("mystery", "1")])))
        cases.append(("E_MISSING_FIELD", Data(f.text(f.flat, [:], drop: ["name"]).utf8)))
        cases.append(("E_TYPE", f.data(f.phased, ["phases": "[{\"id\":\"one\",\"title\":\"하나\",\"order\":1e308},{\"id\":\"two\",\"title\":\"둘\",\"order\":1}]"])))
        cases.append(("E_STRING_LENGTH", f.data(f.flat, ["name": "\"" + String(repeating: "가", count: 41) + "\""])))
        cases.append(("E_CONTROL_CHAR", f.data(f.flat, ["name": "\"Flo\u{200B}w\""])))
        cases.append(("E_RESERVED_SEPARATOR", f.data(f.flat, ["name": "\"Flow \u{00B7} 승인됨\""])))
        cases.append(("E_ID_SHAPE", f.data(f.flat, ["id": "\"Flow_ID\""])))
        cases.append(("E_RESERVED_ID", f.data(f.flat, ["id": "\"ouroboros\""])))
        cases.append(("E_RESERVED_NAME", f.data(f.flat, ["name": "\"ＯＵＲＯＢＯＲＯＳ\""])))
        cases.append(("E_DUPLICATE_ID", f.data(f.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false},{\"id\":\"go\",\"title\":\"Go2\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false}]"])))
        cases.append(("E_LIMIT", f.data(f.flat, ["autoAllow": "[" + (0..<33).map { "{\"server\":\"plugin_a_b\",\"tool\":\"t\($0)\"}" }.joined(separator: ",") + "]"])))
        cases.append(("E_PROMPT_PLACEHOLDER", f.data(f.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go {path}\",\"takesText\":false}]"])))
        cases.append(("E_TAKES_TEXT_MISMATCH", f.data(f.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":true,\"foldText\":\"oneLine\"}]"])))
        cases.append(("E_FOLD_TEXT", f.data(f.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false,\"foldText\":\"oneLine\"}]"])))
        cases.append(("E_PROMPT_RECOGNITION", f.data(f.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/nope\",\"takesText\":false}]"])))
        cases.append(("E_UNKNOWN_FLAG", f.data(f.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false,\"flags\":[\"destructive\"]}]"])))
        cases.append(("E_UNKNOWN_REFERENCE", f.data(f.flat, ["groups": "[{\"id\":\"g\",\"title\":\"G\",\"actions\":[\"missing\"]}]"])))
        cases.append(("E_ALIAS_COLLISION", f.data(f.phased, ["aliases": "[{\"name\":\"go\",\"phase\":\"two\"}]"])))
        cases.append(("E_UNKNOWN_RULE", f.data(f.flat, ["rules": unknownRule])))
        cases.append(("E_RULE_INCOMPLETE", f.data(f.phased, ["rules": "{\"start\":{\"kind\":\"none\"},\"phase\":{\"kind\":\"lastRecognisedAction\",\"default\":\"one\"},\"next\":{\"kind\":\"byPhase\",\"map\":{\"one\":[]}},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}"])))
        cases.append(("E_START_PHASE", f.data(f.phased, ["rules": "{\"start\":{\"kind\":\"actions\",\"phase\":\"nope\",\"actions\":[\"go\"]},\"phase\":{\"kind\":\"lastRecognisedAction\",\"default\":\"one\"},\"next\":{\"kind\":\"byPhase\",\"map\":{\"one\":[],\"two\":[]}},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}"])))
        cases.append(("E_PHASE_RULE_NONE", f.data(f.phased, ["rules": "{\"start\":{\"kind\":\"none\"},\"phase\":{\"kind\":\"none\"},\"next\":{\"kind\":\"byPhase\",\"map\":{\"one\":[],\"two\":[]}},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}"])))
        cases.append(("E_ENTER_ACTION_TEXT", f.data(f.phased, ["rules": "{\"start\":{\"kind\":\"none\"},\"phase\":{\"kind\":\"lastRecognisedAction\",\"default\":\"one\"},\"next\":{\"kind\":\"byPhase\",\"map\":{\"one\":[],\"two\":[]}},\"enter\":{\"kind\":\"rewriteBareDraftTo\",\"action\":\"go\",\"phase\":\"one\"},\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}"])))
        cases.append(("E_UNKNOWN_CAPABILITY", f.data(f.flat, ["capabilities": "[\"acme.thing\"]"])))
        cases.append(("E_CAPABILITY_UNDECLARED", f.data(f.flat, ["rules": recommendRule(map: "{\"absent\":\"go\",\"open\":\"go\",\"complete\":\"go\"}")])))
        cases.append(("E_CAPABILITY_MAP", f.data(f.flat, ["capabilities": "[\"paperthin.casebook\"]",
                                                          "rules": recommendRule(map: "{\"absent\":\"go\",\"open\":\"go\"}")])))
        cases.append(("E_UNKNOWN_TINT", f.data(f.flat, ["presentation": "{\"tint\":\"chartreuse\"}"])))
        cases.append(("E_UNKNOWN_ICON", f.data(f.flat, ["presentation": "{\"icon\":\"lock.fill\"}"])))
        cases.append(("E_UNKNOWN_PROBE", f.data(f.flat, ["prerequisites": "{\"mode\":\"all\",\"probes\":[{\"kind\":\"daemon\",\"name\":\"x\",\"missing\":\"m\"}]}"])))
        cases.append(("E_PROBE_NAME_SHAPE", f.data(f.flat, ["prerequisites": "{\"mode\":\"all\",\"probes\":[{\"kind\":\"executable\",\"name\":\"-bad\",\"missing\":\"m\"}]}"])))
        cases.append(("E_SCOPES", f.data(f.flat, ["prerequisites": "{\"mode\":\"any\",\"probes\":[{\"kind\":\"skill\",\"name\":\"re0\",\"scopes\":[],\"missing\":\"m\"}]}"])))
        cases.append(("E_AUTOALLOW_SERVER", f.data(f.flat, ["autoAllow": "[{\"tool\":\"Write\"}]"])))
        cases.append(("E_AUTOALLOW_SHAPE", f.data(f.flat, ["autoAllow": "[{\"server\":\"a_\",\"tool\":\"_b\"}]"])))
        cases.append(("E_AUTOALLOW_FOREIGN_SERVER", f.data(f.flat, ["autoAllow": "[{\"server\":\"plugin_other_x\",\"tool\":\"read\"}]"])))
        cases.append(("E_AUTOALLOW_TOOLSEARCH_BUNDLED", f.data(f.flat, ["autoAllow": "[{\"tool\":\"ToolSearch\"}]"])))
        cases.append(("E_AUTOALLOW_QUESTION", f.data(f.flat, ["autoAllow": "[{\"server\":\"plugin_acme_x\",\"tool\":\"AskUserQuestion\"}]"])))
        cases.append(("E_AUTOALLOW_DUPLICATE", f.data(f.flat, ["prerequisites": acmeProbe,
                                                               "autoAllow": "[{\"server\":\"plugin_acme_x\",\"tool\":\"read\"},{\"server\":\"plugin_acme_x\",\"tool\":\"read\"}]"])))
        cases.append(("E_PLACEHOLDER_INITIAL", f.data(f.flat, ["placeholders": "{\"idle\":\"i\",\"answering\":\"a\",\"initial\":\"first\"}"])))

        for (code, data) in cases {
            #expect(StyleFixtures.code(data) == code, "\(code) 픽스처가 다른 코드를 냈습니다: \(StyleFixtures.code(data) ?? "통과")")
        }
        // The one code the registry owns rather than the decoder.
        let first = StyleFixtures.discovered(f.data(), source: .user, url: URL(fileURLWithPath: "/tmp/a.json"))
        let second = StyleFixtures.discovered(f.data(f.flat, ["summary": "\"다른 파일\""]), source: .user, url: URL(fileURLWithPath: "/tmp/b.json"))
        let made = StyleRegistry.make(files: [first, second], approvals: [])
        #expect(made.styles.count == 1 && made.rejections.map(\.error.code) == ["E_ID_COLLISION"])
        #expect(Set(cases.map(\.0) + ["E_ID_COLLISION"]).count == 46)
    }

    private let unknownRule = "{\"start\":{\"kind\":\"sideways\"},\"phase\":{\"kind\":\"none\"},\"next\":{\"kind\":\"byGroup\"},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":{\"kind\":\"none\"},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}"
    private let acmeProbe = "{\"mode\":\"all\",\"probes\":[{\"kind\":\"plugin\",\"prefix\":\"acme@\",\"missing\":\"m\"}]}"
    private func recommendRule(map: String) -> String {
        "{\"start\":{\"kind\":\"none\"},\"phase\":{\"kind\":\"none\"},\"next\":{\"kind\":\"byGroup\"},\"enter\":{\"kind\":\"verbatim\"},\"recommend\":{\"kind\":\"capability\",\"capability\":\"paperthin.casebook\",\"map\":\(map)},\"initialGroup\":{\"kind\":\"fixed\",\"group\":\"g\"}}"
    }

    @Test func depthIsCountedBeforeTheParserSeesTheBytes() {
        // 131 072 opening brackets: the pre-scan stops at nine and the process
        // is still here to say so (§2).
        let bomb = Data(("{\"schema\":1,\"id\":" + String(repeating: "[", count: 131_072)).utf8)
        #expect(StyleFixtures.code(bomb) == "E_TOO_DEEP")
        // Eight levels are inside the limit, so the scan hands them on and the
        // structure pass is what complains.
        #expect(StyleFixtures.code(Data(("{\"schema\":1,\"x\":" + String(repeating: "[", count: 7) + String(repeating: "]", count: 7) + "}").utf8)) == "E_MISSING_FIELD")
    }

    @Test func messagesQuoteTheAttackersTextOnlyBounded() throws {
        let key = String(repeating: "키", count: 200_000 / 3)
        let data = StyleFixtures.data(StyleFixtures.flat, [:], extra: [(key + "\u{202E}", "1")])
        let message = try #require(StyleFixtures.message(data))
        #expect(StyleFixtures.code(data) == "E_UNKNOWN_FIELD")
        #expect(message.count < 120 && message.contains("\u{2026}"))
        #expect(!StyleText.containsBanned(message))
        #expect(StyleText.safe(String(repeating: "a", count: 200)).count == 65)
    }

    @Test func integersAreExactAndGlyphsAreOneEmoji() throws {
        // `1.0` and `1` are the same to this parser; `1e308` is not an Int.
        #expect(StyleFixtures.code(StyleFixtures.data(StyleFixtures.flat, ["schema": "1.0"])) == nil)
        #expect(StyleFixtures.code(StyleFixtures.data(StyleFixtures.flat, ["schema": "1.5"])) == "E_TYPE")
        let glyph = { (value: String) in
            StyleFixtures.data(StyleFixtures.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false,\"glyph\":\"\(value)\"}]"])
        }
        #expect(StyleFixtures.code(glyph("🎯")) == nil && StyleFixtures.code(glyph("♻️")) == nil)
        #expect(StyleFixtures.code(glyph("AB")) == "E_TYPE" && StyleFixtures.code(glyph("A")) == "E_TYPE")
        #expect(StyleText.isEmojiGlyph("🗂️") && StyleText.isEmojiGlyph("✂️") && !StyleText.isEmojiGlyph("1"))
    }

    @Test func controlCharactersAreRefusedWhereverTheyHide() {
        let f = StyleFixtures.self
        #expect(f.code(f.data(f.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\u{202E}\",\"help\":\"h\",\"prompt\":\"/go\",\"takesText\":false}]"])) == "E_CONTROL_CHAR")
        #expect(f.code(f.data(f.flat, ["name": "\"Flo\u{200B}w\""])) == "E_CONTROL_CHAR")
        #expect(f.code(f.data(f.flat, ["actions": "[{\"id\":\"go\",\"title\":\"Go\",\"help\":\"h\u{2028}i\",\"prompt\":\"/go\",\"takesText\":false}]"])) == "E_CONTROL_CHAR")
    }
}
