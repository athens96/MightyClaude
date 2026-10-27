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

    /// `metrics.spacing`, `metrics.inset` and `metrics.layout`: the Mac's `DesignMetrics` names
    /// the same keys with the same values, so a size changed on one side fails here.
    @Test(arguments: ["spacing", "inset", "layout"])
    func metricsMatchTheFixture(_ section: String) throws {
        let metrics = try #require(try Self.load()["metrics"] as? [String: Any])
        let table = try #require(metrics[section] as? [String: Double], "metrics.\(section)")
        let mac: [String: CGFloat] = switch section {
        case "spacing": DesignMetrics.Spacing.all
        case "inset": DesignMetrics.Inset.all
        default: DesignMetrics.Layout.all
        }
        #expect(Set(mac.keys) == Set(table.keys), "\(section): Mac-only \(Set(mac.keys).subtracting(table.keys).sorted()), fixture-only \(Set(table.keys).subtracting(mac.keys).sorted())")
        for (name, value) in table {
            #expect(mac[name].map(Double.init) == value, "metrics.\(section).\(name): fixture \(value), Mac \(mac[name].map { "\($0)" } ?? "missing")")
        }
    }

    /// The constants the views read and the tables above are the same numbers.
    @Test func metricTablesListTheirConstants() {
        #expect(DesignMetrics.Spacing.all["md"] == DesignMetrics.Spacing.md && DesignMetrics.Spacing.all.count == 6)
        #expect(DesignMetrics.Inset.all["dock"] == DesignMetrics.Inset.dock && DesignMetrics.Inset.all["listGap"] == DesignMetrics.Inset.listGap)
        #expect(DesignMetrics.Layout.all["toolbar"] == DesignMetrics.Layout.toolbar && DesignMetrics.Layout.all["tab"] == DesignMetrics.Layout.tab)
        // Every inset and size sits on the 2pt half-grid of the spacing scale but the 3pt pane rows and status bar.
        for (name, value) in DesignMetrics.Inset.all where !["paneRowV", "statusBarV"].contains(name) {
            #expect(value.truncatingRemainder(dividingBy: 2) == 0, "inset.\(name) \(value) is off the 2pt grid")
        }
        // A tab sits inside its strip, and the toolbar's buttons stay a usable target.
        #expect(DesignMetrics.Layout.tab < DesignMetrics.Layout.tabStrip)
        #expect(DesignMetrics.Layout.toolbar >= 24 && DesignMetrics.Layout.tab >= 22 && DesignMetrics.Layout.paneHeader >= 24)
    }

    /// `pipeline`: the 파이프라인 view's tints and status bar, the same numbers as `DesignMetrics.Pipeline`.
    @Test func pipelineTintsMatchTheFixture() throws {
        let table = try #require(try Self.load()["pipeline"] as? [String: Double], "pipeline")
        #expect(Set(table.keys) == Set(DesignMetrics.Pipeline.all.keys), "pipeline: Mac-only \(Set(DesignMetrics.Pipeline.all.keys).subtracting(table.keys).sorted()), fixture-only \(Set(table.keys).subtracting(DesignMetrics.Pipeline.all.keys).sorted())")
        for (name, value) in table {
            #expect(DesignMetrics.Pipeline.all[name] == value, "pipeline.\(name): fixture \(value), Mac \(DesignMetrics.Pipeline.all[name].map { "\($0)" } ?? "missing")")
        }
        // The card is lighter than its column, so it stands out in a column of its own tone.
        #expect(DesignMetrics.Pipeline.columnTintOpacity == 0.6)
        #expect(DesignMetrics.Pipeline.cardTintOpacity == 0.35)
        #expect(DesignMetrics.Pipeline.cardBarWidth == 3)
    }

    /// `motion`: the durations and the easing curve, the same numbers as `DesignMetrics.Motion`.
    @Test func motionMatchesTheFixture() throws {
        let motion = try #require(try Self.load()["motion"] as? [String: Any], "motion")
        let durations = DesignMetrics.Motion.durations
        #expect(Set(motion.keys) == Set(durations.keys).union(["easing"]), "motion: Mac-only \(Set(durations.keys).union(["easing"]).subtracting(motion.keys).sorted()), fixture-only \(Set(motion.keys).subtracting(durations.keys).subtracting(["easing"]).sorted())")
        for (name, value) in durations {
            #expect(motion[name] as? Int == value, "motion.\(name): fixture \(motion[name].map { "\($0)" } ?? "missing"), Mac \(value)")
        }
        #expect(motion["easing"] as? [Double] == DesignMetrics.Motion.easing, "motion.easing")
        // The named constants are the table's, and a token reads as seconds for an animation.
        #expect(durations.count == 6 && durations["scroll"] == DesignMetrics.Motion.scroll && durations["highlight"] == DesignMetrics.Motion.highlight)
        #expect(DesignMetrics.Motion.seconds(DesignMetrics.Motion.scroll) == 0.28)
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
