import Foundation
import Testing
@testable import MightyCore

/// §1.16.4: the Mac half of the widget parity. Each case mirrors one in
/// `mobile/src/__tests__/styles.test.ts` ("state widgets"), so a change to how
/// either side reads a bar, a list or a label fails on both.
struct StyleWidgetPresentationTests {

    // MARK: - Progress bar (phone: progressBarDisplay)

    @Test func aBarReadsAsDoneOfTotal() {
        #expect(StyleWidgetPresentation.make(.progressBar(value: 3, total: 7)) == .progressBar(fraction: 3.0 / 7.0, text: "3/7"))
    }

    @Test func moreDoneThanTotalIsAFullBarAtTheTotal() {
        #expect(StyleWidgetPresentation.make(.progressBar(value: 9, total: 4)) == .progressBar(fraction: 1, text: "4/4"))
    }

    @Test func noCurrentFileIsAnEmptyBarThatStillDraws() {
        let empty = StyleWidgetPresentation.make(.progressBar(value: 0, total: 0))
        #expect(empty == .progressBar(fraction: 0, text: "0/0"))
        #expect(empty?.isEmpty == false)
    }

    @Test func aBarWithoutTotalIsABareCount() {
        #expect(StyleWidgetPresentation.make(.progressBar(value: 5, total: nil)) == .progressBar(fraction: 0, text: "5"))
    }

    @Test func aNegativeCountIsDroppedAndANegativeTotalIsNoTotal() {
        #expect(StyleWidgetPresentation.make(.progressBar(value: -1, total: 3)) == nil)
        #expect(StyleWidgetPresentation.make(.progressBar(value: 2, total: -3)) == .progressBar(fraction: 0, text: "2"))
    }

    // MARK: - List

    @Test func aListKeepsItsLinesInOrder() {
        let list = StyleWidgetPresentation.make(.list(items: ["항목 1", "항목 2"]))
        #expect(list == .list(items: ["항목 1", "항목 2"]))
        #expect(list?.isEmpty == false)
    }

    @Test func aListIsCutToTheSameEightLinesAsThePhoneSkippingBlankOnes() {
        let items = ["  "] + (1...12).map { "항목 \($0)" }
        #expect(StyleWidgetPresentation.maximumListItems == 8)
        #expect(StyleWidgetPresentation.make(.list(items: items)) == .list(items: (1...8).map { "항목 \($0)" }))
    }

    @Test func aLongLineIsCutWhereThePhoneCutsIt() {
        let long = String(repeating: "가", count: 250)
        #expect(StyleWidgetPresentation.make(.list(items: [long])) == .list(items: [String(repeating: "가", count: 200)]))
        #expect(StyleWidgetPresentation.make(.label(text: long)) == .label(text: String(repeating: "가", count: 200)))
    }

    @Test func anEmptyListDrawsNothingButKeepsItsPlace() {
        let empty = StyleWidgetPresentation.make(.list(items: []))
        #expect(empty == .list(items: []))
        #expect(empty?.isEmpty == true)
    }

    // MARK: - Label

    @Test func aLabelIsOneTrimmedLine() {
        let label = StyleWidgetPresentation.make(.label(text: "  서브에이전트 3회 "))
        #expect(label == .label(text: "서브에이전트 3회"))
        #expect(label?.isEmpty == false)
    }

    // MARK: - Invisible and control characters (phone: inlineText, UNSAFE_INLINE)

    @Test func aLineBreakInsideTheTextClosesUpRatherThanBreakingTheLine() {
        #expect(StyleWidgetPresentation.make(.label(text: "first\nsecond")) == .label(text: "firstsecond"))
        #expect(StyleWidgetPresentation.make(.list(items: ["first\nsecond", "a\tb"])) == .list(items: ["firstsecond", "ab"]))
    }

    @Test func bidiOverridesAndZeroWidthCharactersAreStrippedWhereverTheyStand() {
        #expect(StyleWidgetPresentation.make(.label(text: "ab\u{202E}c\u{200B}d")) == .label(text: "abcd"))
        #expect(StyleWidgetPresentation.make(.list(items: ["ab\u{202E}c\u{200B}d", "\u{2066}\u{FEFF}\u{00AD}"]))
                == .list(items: ["abcd"]))
    }

    @Test func aJoinedEmojiKeepsItsJoinerAsThePhoneKeepsIt() {
        #expect(StyleWidgetPresentation.make(.label(text: "👩\u{200D}💻 done")) == .label(text: "👩\u{200D}💻 done"))
    }

    /// The Mac's set is §1.11's banned list less U+200D; this is the phone's
    /// `UNSAFE_INLINE` written out range by range, compared scalar by scalar.
    @Test func theStrippedSetIsExactlyThePhonesUnsafeInlineSet() {
        let phone: [ClosedRange<UInt32>] = [
            0x0000...0x001F, 0x007F...0x009F, 0x00AD...0x00AD, 0x061C...0x061C, 0x200B...0x200C, 0x200E...0x200F,
            0x2028...0x2028, 0x2029...0x2029, 0x202A...0x202E, 0x2060...0x2060, 0x2066...0x2069, 0xFEFF...0xFEFF,
        ]
        var differing: [String] = []
        for value in UInt32(0)...0x10FFFF {
            guard let scalar = Unicode.Scalar(value) else { continue }
            if StyleWidgetPresentation.isStrippedFromLine(scalar) != phone.contains(where: { $0.contains(value) }) {
                differing.append(String(format: "U+%04X", value))
            }
        }
        #expect(differing.isEmpty, "\(differing)")
    }

    @Test func theCutCountsCodePointsAsThePhonesArrayFromDoes() {
        // Four scalars, one grapheme: a cut by graphemes would keep it whole.
        let family = "👨\u{200D}👩\u{200D}👧"
        let text = String(repeating: "가", count: 198) + family
        let cut = String(repeating: "가", count: 198) + "👨\u{200D}"
        #expect(StyleWidgetPresentation.make(.label(text: text)) == .label(text: cut))
    }

    @Test func anEmptyLabelDrawsNothingButKeepsItsPlace() {
        let empty = StyleWidgetPresentation.make(.label(text: ""))
        #expect(empty == .label(text: ""))
        #expect(empty?.isEmpty == true)
    }

    // MARK: - The whole row

    @Test func widgetsKeepPayloadOrderWithOneEntryPerSource() {
        let widgets: [StylePanel.Widget] = [
            .progressBar(value: 0, total: 0), .list(items: []), .label(text: ""),
            .progressBar(value: -1, total: 3), .progressBar(value: 6, total: 10),
        ]
        #expect(StyleWidgetPresentation.make(widgets) == [
            .progressBar(fraction: 0, text: "0/0"), .list(items: []), .label(text: ""),
            .progressBar(fraction: 0.6, text: "6/10"),
        ])
    }

    /// The recorded golden the phone test reads too (styles.test.ts, "golden
    /// state widgets"): the same bytes present the same way on both sides.
    @Test func theSuperpowersGoldenWidgetsPresentAsThePhoneDrawsThem() throws {
        let url = StyleGolden.goldenDirectory.appendingPathComponent("superpowers.panel.json")
        let golden = try JSONDecoder().decode(StyleGolden.Projection.self, from: Data(contentsOf: url))
        let widgets = try #require(golden.withState?.widgets)
        #expect(StyleWidgetPresentation.make(widgets) == [
            .progressBar(fraction: 3.0 / 7.0, text: "3/7"),
            .label(text: "서브에이전트 2회 시작"),
        ])
    }

    /// The engine's own unread state for each kind lands on the empty state
    /// the phone draws for it.
    @Test func theEnginesEmptyWidgetsPresentAsTheDocumentedEmptyStates() {
        let presented = [StyleStateWidget.progressBar, .list, .label].map { StyleWidgetPresentation.make(StyleStateEngine.emptyWidget($0)) }
        #expect(presented == [.progressBar(fraction: 0, text: "0/0"), .list(items: []), .label(text: "")])
    }
}
