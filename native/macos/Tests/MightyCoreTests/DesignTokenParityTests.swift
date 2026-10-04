import Foundation
import Testing
@testable import MightyCore

/// The same file the Windows Core tests read (`DesignTokenVerification`): the Mac palette
/// and the WinUI port must carry the same concept D colours, field for field, and derive
/// the same tone, segment and glyph colours from them.
struct DesignTokenParityTests {
    static let fixture: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/design-tokens.json")
    }()

    static func load() throws -> [String: Any] {
        try #require(JSONSerialization.jsonObject(with: Data(contentsOf: fixture)) as? [String: Any])
    }

    static let modes = ["light", "dark"]

    /// The palette's stored fields by name, read with `Mirror` so a field added to
    /// `DesignPalette` without the fixture (or the other way round) fails here.
    static func fields(_ palette: DesignPalette) -> [String: DesignColor] {
        var out: [String: DesignColor] = [:]
        for child in Mirror(reflecting: palette).children {
            if let label = child.label, let color = child.value as? DesignColor { out[label] = color }
        }
        return out
    }

    @Test(arguments: modes)
    func everyFieldEqualsTheFixture(_ mode: String) throws {
        let table = try #require(try Self.load()[mode] as? [String: String])
        let fields = Self.fields(DesignTokens.palette(theme: mode))
        #expect(fields.count == 41)
        #expect(Set(fields.keys) == Set(table.keys), "\(mode): palette-only \(Set(fields.keys).subtracting(table.keys)), fixture-only \(Set(table.keys).subtracting(fields.keys))")
        for (name, color) in fields {
            #expect(color.hex == table[name], "\(mode).\(name): \(color.hex) vs \(table[name] ?? "missing")")
        }
    }

    @Test(arguments: modes)
    func derivedTonesMatchTheFixture(_ mode: String) throws {
        let derived = try #require((try Self.load()["derived"] as? [String: Any])?[mode] as? [String: Any])
        let palette = DesignTokens.palette(theme: mode)
        #expect(palette.isDark == (derived["isDark"] as? Bool))
        #expect(palette.segmentTrack.hex == derived["segmentTrack"] as? String, "\(mode).segmentTrack")
        #expect(palette.segmentOn.hex == derived["segmentOn"] as? String, "\(mode).segmentOn")
        let tones = try #require(derived["tones"] as? [String: [String: String]])
        #expect(Set(tones.keys) == Set(DesignTone.allCases.map(\.rawValue)))
        for tone in DesignTone.allCases {
            let row = try #require(tones[tone.rawValue])
            #expect(palette.fill(tone).hex == row["fill"], "\(mode).fill(\(tone))")
            #expect(palette.text(tone).hex == row["text"], "\(mode).text(\(tone))")
            #expect(palette.soft(tone).hex == row["soft"], "\(mode).soft(\(tone))")
            #expect(palette.mark(tone).hex == row["mark"], "\(mode).mark(\(tone))")
            #expect(palette.glyph(tone).hex == row["glyph"], "\(mode).glyph(\(tone))")
        }
        let discs = try #require(derived["disc"] as? [String: [String: String]])
        for tone in [DesignTone.wait, .err] {
            let disc = try #require(discs[tone.rawValue])
            #expect(palette.discFill(tone).hex == disc["fill"], "\(mode).discFill(\(tone))")
            #expect(palette.discInk(tone).hex == disc["ink"], "\(mode).discInk(\(tone))")
        }
    }

    @Test func providerColoursMatchTheFixture() throws {
        let windowsOnly = try #require(try Self.load()["windowsOnly"] as? [String: Any])
        let providers = try #require(windowsOnly["provider"] as? [String: Any])
        for provider in ["claude", "codex", "gemini"] {
            let expected = try #require(providers[provider] as? [String])
            #expect(ProviderMark.colors(provider: provider).map { DesignColor($0).hex } == expected, "\(provider)")
        }
    }
}
