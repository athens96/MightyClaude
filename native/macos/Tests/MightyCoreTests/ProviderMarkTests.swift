import CoreGraphics
import Testing
@testable import MightyCore

struct ProviderMarkTests {
    @Test func outlinesParseIntoClosedShapesInsideTheirBox() {
        for provider in ["claude", "codex", "gemini"] {
            let commands = ProviderMark.commands(ProviderMark.outline(provider: provider))
            #expect(commands.first?.kind == "M" && commands.last?.kind == "Z")
            // Only the prepared command set, each with the right number of values.
            let arity: [Character: Int] = ["M": 2, "L": 2, "C": 6, "Z": 0]
            #expect(commands.allSatisfy { arity[$0.kind] == $0.values.count })
            let bounds = ProviderMark.path(provider: provider, in: CGRect(x: 0, y: 0, width: 24, height: 24)).boundingBoxOfPath
            #expect(bounds.minX >= -0.01 && bounds.minY >= -0.01 && bounds.maxX <= 24.01 && bounds.maxY <= 24.01)
            #expect(bounds.width > 20 && bounds.height > 20)
        }
        // The Codex knot has its seven cut-outs; the Gemini spark is one contour.
        #expect(ProviderMark.commands(ProviderMark.codex).filter { $0.kind == "M" }.count == 8)
        #expect(ProviderMark.commands(ProviderMark.gemini).filter { $0.kind == "M" }.count == 1)
        #expect(ProviderMark.outline(provider: "unknown") == ProviderMark.claude)
    }

    @Test func marksScaleAndCentreInAnyRect() {
        let wide = ProviderMark.path(provider: "gemini", in: CGRect(x: 10, y: 5, width: 96, height: 48)).boundingBoxOfPath
        #expect(abs(wide.midX - 58) < 0.01 && abs(wide.midY - 29) < 0.01)
        #expect(abs(wide.width - 48) < 0.01 && abs(wide.height - 48) < 0.01)
        #expect(ProviderMark.commands("M1 2 L3.5 -4 Z") == [.init(kind: "M", values: [1, 2]), .init(kind: "L", values: [3.5, -4]), .init(kind: "Z", values: [])])
    }

    @Test func brandColours() {
        #expect(ProviderMark.colors(provider: "claude") == [0xD97757] && ProviderMark.colors(provider: "codex") == [0x10A37F])
        #expect(ProviderMark.colors(provider: "gemini").count == 3 && ProviderMark.colors(provider: "other") == [0xD97757])
    }
}
