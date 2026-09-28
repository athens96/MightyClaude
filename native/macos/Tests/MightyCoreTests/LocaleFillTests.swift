import Testing
@testable import MightyCore

/// `fill` substitutes in one pass: a value that itself looks like a placeholder
/// (an untrusted manifest glob on the approval card) is shown as written.
@Suite struct LocaleFillTests {
    @Test func substitutedValuesAreNeverSubstitutedAgain() {
        let subs = ["path": "{parser}/{widget}.md", "parser": "json", "widget": "list"]
        // Dictionary order varies between runs; every order must give the same text.
        for _ in 0..<50 {
            #expect(fill("파일 {path} · {parser} → {widget}", subs) == "파일 {parser}/{widget}.md · json → list")
        }
    }

    @Test func unknownAndUnclosedBracesStayLiteral() {
        #expect(fill("{a} {missing} {b", ["a": "1"]) == "1 {missing} {b")
        #expect(fill("{{a}}", ["a": "x"]) == "{x}")
        #expect(fill("no placeholders", ["a": "x"]) == "no placeholders")
        #expect(fill("{a}{a}", ["a": "ab"]) == "abab")
    }
}
