# Ask Another CLI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In a Claude agent pane with oh-my-claudecode installed, the composer gets a per-pane "send to" switch. Each send to a chosen installed CLI goes through `/oh-my-claudecode:ask <id> <text>`. The switch exists on Mac, Windows and the phone.

**Architecture:**
- A shared contract fixture fixes the targets, the wrapping rule and the visibility rule.
- Mac MightyCore and Windows Core each hold one pure rule type, tested against that fixture.
- The hosts keep a per-pane `askTarget` field, probe which target executables are installed, wrap the text on send, and tell the phone which targets exist.
- The composers (Mac SwiftUI, Windows web, phone) only show the switch and set the field.

**Tech Stack:** Swift/SwiftUI (MightyCore, MightyClaude), C# (.NET Core + WinUI host), React/TypeScript (`native/windows/web`), Expo React Native (`mobile/`).

**Spec:** `docs/superpowers/specs/2026-10-28-ask-other-cli-design.md`

## Global Constraints
- **Targets:** listed in this order. Neither `claude` nor `muse` is a target.

  | id | label | executable |
  |---|---|---|
  | codex | Codex | codex |
  | antigravity | Antigravity | agy |
  | gemini | Gemini CLI | gemini |
  | grok | Grok | grok |
  | cursor | Cursor | cursor-agent |

- **Visibility:** all three must hold:
  - the pane is a Claude agent pane (`kind == "claude" && provider == "claude"`);
  - the pane's slash command list contains `oh-my-claudecode:ask`;
  - at least one target executable is installed.
- **Wrapping:**
  - Target nil or `claude`: send the text unchanged.
  - Text that is empty after trimming: send nothing.
  - Text that starts with `/` after trimming leading whitespace: send it unchanged.
  - Otherwise: send `/oh-my-claudecode:ask <id> <text trimmed>`.
- **Fallback:** a saved target that is no longer available (OMC missing, or its executable gone) reads as Claude. The pane shows the `composer.ask.fellBack` notice once.
- **Persistence:** `askTarget` is per pane and optional. Old saves read it as nil, which means Claude.
- **Attachments:** while a non-Claude target is selected, the attach control is disabled and a message using `composer.ask.attachmentsOff` explains why.
- **Locale keys**, in `locales/{ko,en,zh,ja}.json` plus the synced copies (run `node scripts/check-locales.js`, then `--check`). No hard-coded Korean.
  - `composer.ask.menuLabel`: "보낼 대상"
  - `composer.ask.claude`: "Claude (기본)"
  - `composer.ask.placeholder`: "{name}에게 묻기…"
  - `composer.ask.attachmentsOff`: "다른 CLI에게 물을 때는 첨부를 보낼 수 없습니다"
  - `composer.ask.fellBack`: "{name}을(를) 찾을 수 없어 Claude로 보냅니다"
- **Phone wire:**
  - Session detail gains optional `askTargets?: [{id, label}]`, only when visible, and `askTarget?: string`.
  - New desktop action `setAskTarget` with body `{ target: string | null }`.
  - The phone sends plain text; the host wraps it.
- **Process:**
  - Work on main in `~/Work/MightyClaude` and do not push. The repo is public.
  - Never commit `.ouroboros_eval_artifact.md`. `git config user.email` must be `11283764+athens96@users.noreply.github.com`.
  - Commit subjects are `(area) what the user now gets`, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Do not touch `native/macos/Sources/MightyCore/Styles/**`.
- **Checks:**
  - No builds or full suites. Write tests.
  - Allowed: `xcrun swiftc -parse`, `npx tsc --noEmit` (web or mobile), jest/vitest on the touched files, `check-locales`, `check-design-tokens`, `check-style-freeze`, `help/build.mjs --check`.
  - Never launch the GUI app.

---

### Task 1: Contract + Mac core rules

**Files:**
- Create: `native/contracts/fixtures/ask-targets.json`, `native/macos/Sources/MightyCore/AskTargets.swift`, `native/macos/Tests/MightyCoreTests/AskTargetsTests.swift`
- Modify: `native/macos/Sources/MightyCore/Models.swift` (`RunSession` ~294: add `askTarget: String?`), `native/macos/Sources/MightyCore/StateRepository.swift`, if it lists `RunSession` keys explicitly. Check whether `RunSession` uses synthesized Codable.

**Interfaces (produces):**
```swift
public struct AskTarget: Equatable, Sendable { public let id: String; public let label: String; public let executable: String }
public enum AskTargets {
    public static let skill = "oh-my-claudecode:ask"
    public static let all: [AskTarget]                       // contract order
    /// Targets shown for a pane: empty unless Claude agent pane + skill present.
    public static func available(isClaudeAgent: Bool, slashInvocations: Set<String>, installed: Set<String>) -> [AskTarget]
    /// The target a send uses: nil (Claude) when the saved id is not in `available`.
    public static func effective(saved: String?, available: [AskTarget]) -> AskTarget?
    /// What is sent: nil when nothing should be sent.
    public static func wrap(_ text: String, target: AskTarget?) -> String?
}
```
`installed` holds the executable names that were found, e.g. `["codex","agy"]`.

- [ ] **Step 1: Fixture.** Create `native/contracts/fixtures/ask-targets.json`:
```json
{
  "about": "Composer 'send to' targets that ask another CLI through oh-my-claudecode's ask skill. Mac AskTargets, Windows Core AskTargets and the phone read this file.",
  "skill": "oh-my-claudecode:ask",
  "targets": [
    { "id": "codex", "label": "Codex", "executable": "codex" },
    { "id": "antigravity", "label": "Antigravity", "executable": "agy" },
    { "id": "gemini", "label": "Gemini CLI", "executable": "gemini" },
    { "id": "grok", "label": "Grok", "executable": "grok" },
    { "id": "cursor", "label": "Cursor", "executable": "cursor-agent" }
  ],
  "available": [
    { "name": "claude pane with omc and two CLIs", "isClaudeAgent": true, "slash": ["oh-my-claudecode:ask", "clear"], "installed": ["agy", "codex"], "expect": ["codex", "antigravity"] },
    { "name": "no omc", "isClaudeAgent": true, "slash": ["clear"], "installed": ["codex"], "expect": [] },
    { "name": "no other CLI", "isClaudeAgent": true, "slash": ["oh-my-claudecode:ask"], "installed": ["claude"], "expect": [] },
    { "name": "not a claude agent pane", "isClaudeAgent": false, "slash": ["oh-my-claudecode:ask"], "installed": ["codex"], "expect": [] }
  ],
  "effective": [
    { "name": "saved and available", "saved": "codex", "available": ["codex", "antigravity"], "expect": "codex" },
    { "name": "saved but gone falls back to claude", "saved": "grok", "available": ["codex"], "expect": null },
    { "name": "nothing saved", "saved": null, "available": ["codex"], "expect": null },
    { "name": "saved claude", "saved": "claude", "available": ["codex"], "expect": null }
  ],
  "wrap": [
    { "name": "claude sends as is", "target": null, "text": "hello", "expect": "hello" },
    { "name": "codex wraps", "target": "codex", "text": "review this", "expect": "/oh-my-claudecode:ask codex review this" },
    { "name": "trims around", "target": "antigravity", "text": "  look at it \n", "expect": "/oh-my-claudecode:ask antigravity look at it" },
    { "name": "empty sends nothing", "target": "codex", "text": "   ", "expect": null },
    { "name": "slash command passes through", "target": "codex", "text": "  /clear", "expect": "  /clear" },
    { "name": "multi-line kept", "target": "codex", "text": "a\nb", "expect": "/oh-my-claudecode:ask codex a\nb" }
  ]
}
```
Note on "claude sends as is": for a nil target, `wrap` returns the text unchanged. The composer's own empty check already handles blank text for Claude.

- [ ] **Step 2: Failing tests** in `AskTargetsTests.swift`. Use Swift Testing and load the fixture the way `PendingTests.swift` does (copy its fixture-path helper).
  - `AskTargets.all` must equal the fixture's targets (id, label, executable, order), and `skill` must match.
  - For every `available` case, `available(...)` gives exactly the expected ids in that order.
  - For every `effective` case, the result id is the expected value (nil means Claude). Build `available` from `all` filtered by the case's ids.
  - For every `wrap` case, `wrap(text, target:)` equals the expected value (nil means nothing is sent).
  - `RunSession` with `askTarget` round-trips through JSON, and JSON without the key decodes as nil.
- [ ] **Step 3: Implement** `AskTargets.swift`:
```swift
import Foundation

/// Composer "send to" targets: another CLI asked through oh-my-claudecode's ask skill
/// (native/contracts/fixtures/ask-targets.json).
public struct AskTarget: Equatable, Sendable {
    public let id: String
    public let label: String
    public let executable: String
}

public enum AskTargets {
    public static let skill = "oh-my-claudecode:ask"
    public static let all: [AskTarget] = [
        AskTarget(id: "codex", label: "Codex", executable: "codex"),
        AskTarget(id: "antigravity", label: "Antigravity", executable: "agy"),
        AskTarget(id: "gemini", label: "Gemini CLI", executable: "gemini"),
        AskTarget(id: "grok", label: "Grok", executable: "grok"),
        AskTarget(id: "cursor", label: "Cursor", executable: "cursor-agent"),
    ]

    public static func available(isClaudeAgent: Bool, slashInvocations: Set<String>, installed: Set<String>) -> [AskTarget] {
        guard isClaudeAgent, slashInvocations.contains(skill) else { return [] }
        return all.filter { installed.contains($0.executable) }
    }

    public static func effective(saved: String?, available: [AskTarget]) -> AskTarget? {
        guard let saved else { return nil }
        return available.first { $0.id == saved }
    }

    public static func wrap(_ text: String, target: AskTarget?) -> String? {
        guard let target else { return text }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { return nil }
        if trimmed.hasPrefix("/") { return text }
        return "/\(skill) \(target.id) \(trimmed)"
    }
}
```
  Add `public var askTarget: String?` to `RunSession`, with an init parameter that is trailing and defaults to nil. If `RunSession` has hand-written `CodingKeys`, `init(from:)` or `encode(to:)`, add the key there with `decodeIfPresent`/`encodeIfPresent`. Check every `RunSession(` call site that still compiles. In `StateRepository`, keep the field when sessions are restored.
- [ ] **Step 4: Checks.** Run `xcrun swiftc -parse` on the touched Swift files, then `bash scripts/check-style-freeze.sh`.
- [ ] **Step 5: Commit.** `(mac) the rules for asking another CLI through oh-my-claudecode: which targets a pane offers, which one a send uses, and the text it sends`

---

### Task 2: Mac app (probe, switch, send, phone fields, copy)

**Files:**
- Modify: `native/macos/Sources/MightyClaude/SessionPaneView.swift` (`composerToolbar` ~805, `overflowMenu` ~491, `attachmentButton` ~1058, placeholder, `submitComposer` ~1012), `native/macos/Sources/MightyClaude/ComposerControls.swift` (`ComposerToolbarMetrics.style` ~49: count the new pill when visible)
- Create: `native/macos/Sources/MightyClaude/AppStore+AskTargets.swift`
- Modify: the submit path in `AppStore` where a composer send becomes the run input (find `func submit(` in `AppStore.swift`). Also the mobile session detail and actions: `native/macos/Sources/MightyCore/Remote/MobileRemoteModels.swift` (`MobileSessionDetail`) and `native/macos/Sources/MightyClaude/AppStore+MobileRemote.swift` (desktop action switch; `mobileSubmit`).
- Modify: `locales/*.json` and the copies.
- Test: a MightyCore test for the mobile detail fields, placed next to the existing mobile remote tests.

**Interfaces:**
- Consumes (Task 1): `AskTargets`, `AskTarget`, `RunSession.askTarget`.
- Produces:
  - `AppStore.installedAskExecutables: Set<String>`, `@Published`, refreshed with the slash catalog: at app start and in `refreshSlashCommands`.
  - `func askTargets(for session: RunSession) -> [AskTarget]`
  - `func setAskTarget(_ id: String, target: String?)`
  - The wire: `MobileSessionDetail.askTargets: [MobileAskTarget]?` (`MobileAskTarget { id, label }`) and `askTarget: String?`, plus the desktop action `"setAskTarget"`.

- [ ] **Step 1: Copy.** Add the five locale keys with en/zh/ja translations (en: "Send to", "Claude (default)", "Ask {name}…", "Attachments can't be sent when asking another CLI", "{name} wasn't found, so this goes to Claude"). Sync, then run `--check`.
- [ ] **Step 2: Probe.** In `AppStore+AskTargets.swift`:
  - Resolve each `AskTargets.all[i].executable` off the main actor, using the same PATH lookup and login-shell environment the app uses for provider CLIs. Reuse `SetupProbe.executables(name, environment:)` or the `ProviderService` PATH walk; do not write a new PATH parser.
  - Publish the set only when it changes. Call it from app start and from `refreshSlashCommands`.
  - `askTargets(for:)` returns `AskTargets.available(isClaudeAgent: session.kind == "claude" && session.provider == "claude", slashInvocations: Set(slashCommands(for: session).map(\.invocation)), installed: installedAskExecutables)`.
- [ ] **Step 3: Switch.** In the composer toolbar, when `askTargets(for:)` is non-empty, add a `Menu` with a `ComposerPill` label. The label is the effective target's label, or "Claude" when none is selected; tint it with the accent when a target is selected. Menu items:
  - `composer.ask.claude`
  - a `Divider`
  - one item per target, with a checkmark on the current one
  - Choosing an item calls `setAskTarget`, which updates `RunSession.askTarget` through the existing session update path, so it is saved, and notifies the phone.
  - Count the pill in `ComposerToolbarMetrics`. When the row is narrow, put the same items in `overflowMenu`. The row must never grow a second line.
- [ ] **Step 4: Composer state while a target is set.**
  - Placeholder: `composer.ask.placeholder` with `name`.
  - Attach button disabled, with a tooltip using `composer.ask.attachmentsOff`. If attachments are already staged, show that text as a small notice above the composer and leave them in place.
  - **Fallback:** when the saved `askTarget` is non-nil but `effective` is nil, show `composer.ask.fellBack` once per pane per change through the existing composer notice or status line. Then clear the saved value with `setAskTarget(id, target: nil)`.
- [ ] **Step 5: Wrapping on send.** In the store's submit path for this pane:
  - Compute `let target = AskTargets.effective(saved: session.askTarget, available: askTargets(for: session))`.
  - Apply `AskTargets.wrap(text, target:)` to the text that becomes the run input or the queued item. Send nothing if it returns nil.
  - Do this at the single place both the composer and the phone submit pass through, so `mobileSubmit` wraps too.
  - Nothing else changes: queueing, pending and own-submit behave as before.
- [ ] **Step 6: Phone fields.**
  - Fill `askTargets` (only when non-empty) and `askTarget` in the session detail.
  - Handle the action `setAskTarget` (`{ target: string|null }`): a target must be in `askTargets` or be null, otherwise answer 400 with the existing bad-request error.
  - Include `askTarget` and the target list in the session detail digest so the phone hears of changes.
  - Test in MightyCore: encoding a detail with the fields, and leaving them out when empty.
- [ ] **Step 7: Checks.** Run `xcrun swiftc -parse` on the touched files, then check-locales, check-design-tokens and check-style-freeze.
- [ ] **Step 8: Commit.** `(mac) a Claude pane with oh-my-claudecode gets a 보낼 대상 switch that sends each request to Codex, Antigravity or another installed CLI, from the Mac or the phone`

---

### Task 3: Windows Core + host + web composer

**Files:**
- Create: `native/windows/MightyClaude.Core/AskTargets.cs`, `native/windows/MightyClaude.Core.Tests/AskTargetsVerification.cs`. Wire the verification in `Verification.cs` next to `BackgroundOutputVerification`.
- Modify: Core `Models.cs` (`RunSession`: `string? AskTarget` with init and JSON name `askTarget`); the host composer (`MainWindow.WebShell.Composer.cs`, `composerMenu` and `send`); the host's slash and CLI probes (`SlashCommands.cs` `SlashCatalogCache`, `ProviderCatalog`/`CliUpdateService` style PATH lookup); mobile detail and actions (`MainWindow.MobileRemote.cs`, Core mobile models); web `native/windows/web/src/composer/Toolbar.tsx`, `Composer.tsx` and its types.

**Interfaces:**
- Consumes: the fixture and the Mac rule shapes from Task 1.
- Produces:
  - C# `AskTargets.All`, `Available(bool isClaudeAgent, IReadOnlySet<string> slash, IReadOnlySet<string> installed)`, `Effective(string? saved, IReadOnlyList<AskTarget> available)`, `Wrap(string text, AskTarget? target)`.
  - Web composer state `askTargets: {id,label}[]` and `askTarget: string | null` from the host.
  - Bridge call `setAskTarget({paneId, target})`.

- [ ] **Step 1:** Write `AskTargetsVerification.cs` over every fixture case (targets, available, effective, wrap) and a `RunSession` JSON round trip that includes old JSON without the key. Use the same style as `PendingVerification`/`BackgroundOutputVerification`.
- [ ] **Step 2:** Implement `AskTargets.cs` with exactly the Mac rules. For trimming, `string.Trim()` must match Swift's `.whitespacesAndNewlines` for the fixture cases.
- [ ] **Step 3: Host.**
  - Probe the installed target executables off the UI thread with the existing Windows executable lookup (PATH and PATHEXT, as `ProviderCatalog.Executable` does). Refresh it with the slash catalog.
  - Expose `askTargets`/`askTarget` in the composer state the web page reads.
  - Handle the `setAskTarget` bridge call: validate, set `RunSession.AskTarget` through the existing session update, save, and notify the phone.
  - In the single send path shared by page and phone, apply `Wrap` with `Effective`.
  - Fallback: same rule and notice as the Mac.
- [ ] **Step 4: Web composer.**
  - In `Toolbar.tsx`, add a pill and menu, shown only when `askTargets.length > 0`. It follows the existing pill/menu patterns and uses the existing `t()` keys.
  - Selected target: tinted pill, placeholder `composer.ask.placeholder`, and the attach control disabled with `composer.ask.attachmentsOff`.
  - Add a vitest that shows the pill only with targets and that choosing an item calls `setAskTarget`. Follow the existing composer test patterns.
- [ ] **Step 5: Mobile.**
  - Session detail: add `askTargets`/`askTarget` and include them in the digest.
  - Desktop action: `setAskTarget`, validated as on the Mac.
  - Core verification: the fields are present or absent as required.
- [ ] **Step 6: Checks.** `cd native/windows/web && npx tsc --noEmit && npx vitest run <touched tests>`, then check-locales, check-design-tokens and check-style-freeze. The C# is not compiled here, so re-read it for compile errors under TreatWarningsAsErrors and Nullable.
- [ ] **Step 7: Commit.** `(windows) a Claude pane with oh-my-claudecode gets the 보낼 대상 switch in the web window, and sends from the page or the phone go to the chosen CLI`

---

### Task 4: Phone + docs + help

**Files:**
- Create: `mobile/src/lib/ask-targets.ts`, `mobile/src/__tests__/ask-targets.test.ts`
- Modify: `mobile/src/api/types.ts` (session detail), `mobile/src/components/composer.tsx` (switch), `mobile/app/host/[hostId]/session/[sessionId].tsx` (wire `setAskTarget` through the existing desktop action client call), `docs/mobile-remote.md`, `docs/help/content/{ko,en,zh,ja}/` (the composer page; find it with `grep -rln "첨부" docs/help/content/ko`)

**Interfaces:**
- Consumes: the wire fields and action from Tasks 2 and 3.
- Produces: `parseAskTargets(detail): { targets: {id,label}[]; selected: string | null }`. It accepts only well-formed entries and drops unknown shapes. A `selected` value not among the targets reads as null.

- [ ] **Step 1:** Write the test.
  - `parseAskTargets` on the fields from both hosts' shapes.
  - Missing fields give no targets.
  - Junk entries are dropped.
  - A selected value outside the list reads as null.
  - Also read `native/contracts/fixtures/ask-targets.json`, the way `pending.test.ts` reads `pending.json`, and assert that its target labels are the ones the phone would show.
- [ ] **Step 2:** Implement `ask-targets.ts` and add the optional fields to `types.ts`.
- [ ] **Step 3: Composer switch.**
  - When there are targets, show a pill next to the existing composer controls. Use theme tokens only; the raw-spacing ratchet must not rise.
  - The pill opens a menu or sheet with "Claude (기본)" and the targets, the current one checked.
  - Choosing an item sends `setAskTarget`. Keep the choice optimistic until the next detail arrives; on failure, revert and show the existing error toast.
  - While a target is selected: the placeholder uses `composer.ask.placeholder`, and the attach button is disabled.
- [ ] **Step 4: Docs.**
  - In `docs/mobile-remote.md`, document:
    - `askTargets` and `askTarget` on the session detail;
    - the `setAskTarget` action and its 400 rule;
    - that the host wraps sends, and that old hosts and old phones simply don't show or know the switch.
  - Help, all 4 languages: a short paragraph on the switch.
    - It appears when oh-my-claudecode is installed.
    - It lists installed CLIs.
    - Each request goes to the chosen CLI through `ask`.
    - The answer comes back in Claude's reply.
    - Attachments are off while it is in use.
- [ ] **Step 5: Checks.** `cd mobile && npx tsc --noEmit && npx jest src/__tests__/ask-targets.test.ts`, then check-locales, check-design-tokens and `node scripts/help/build.mjs --check`.
- [ ] **Step 6: Commit.** `(phone) the phone shows the 보낼 대상 switch for a Claude pane with oh-my-claudecode, and the contract and help say how it works`
