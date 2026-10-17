# Windows Cross-Agent Children Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On Windows, a Mighty Orchestration child may run another agent than its parent's, as on the Mac since 925311b:
- a Claude or Codex parent's `delegate` call takes an optional `agent` (`claude`, `codex`, `antigravity`, alias `gemini`), refused with `agent_unknown` or `agent_unavailable`;
- `list_children` and `child_status` name each child's agent;
- right-clicking the composer's worktree button opens a flyout of Claude, Codex and Antigravity, whose blocked entries are greyed out with the reason;
- a child of another agent may do nothing its parent's mode may not, and the human's child of another agent takes that agent's last-used model and effort;
- text whose other-agent child cannot start goes back to the composer.

**Architecture:**
- **Shared fixture.** `native/contracts/fixtures/delegation-modes.json` pins every agent's mode capabilities and the widest child mode for each (parent agent, parent mode, child agent). It is generated from the Mac's current `DelegationProviders`. A new Mac suite and a new Windows Core check both read it.
- **Windows Core.** Ports the Mac rules into the existing files:
  - `DelegationProviders.cs`: capabilities, the subset rule, widest and human child mode, availability, the menu's choices.
  - `Delegation.cs`: reason codes, the menu-only rule, the give-back rule.
  - `DelegationCoordinator.cs`, `DelegationDelegateTool.cs`, `DelegationStatusTools.cs`, `DelegationMcp.cs`, `DelegationModel.cs`: the tool surface, the request key, the agent in every answer.
  - `DelegationAppHost.cs`: model and effort for the human's other-agent child.
  - `DesktopService.Delegation.cs`: the host's availability, read from the window's cached CLI status, and the menu entry points.
- **WinUI.** A new partial, `MainWindow.WorktreeMenu.cs`, owns the flyout and the give-back path. `MainWindow.QueuedComposer.cs` and `MainWindow.Delegation.cs` call it.
- **Smoke.** A smoke step in `MainWindow.DelegationSmoke.cs` starts a Claude child of a read-only Codex pane, and `scripts/test-native-windows.ps1` checks it.

**Tech Stack:**
- C# / .NET 10 (`MightyClaude.Core`, `MightyClaude.Core.Tests`) and WinUI 3 (`MightyClaude.WinUI`).
- Swift 6 + Swift Testing for the Mac fixture check.
- System.Text.Json.
- Node scripts: `check-locales`, `check-design-tokens`, `help/build.mjs`.

## Global Constraints

- **Never launch apps.** Do not run the Mac or Windows app, its installer or its smoke locally. The WinUI smoke runs only in Windows CI.
- **WinUI rules.**
  - Write resource keys once, through `SetResourcesOnce`.
  - Every `Unloaded` handler checks `IsLoaded` first.
  - Never walk `.Parent`.
  - Give font sizes through `AppText` (`TextSize`/`TextLineHeight`/`TextFont`), never `FontSize`/`LineHeight`/`FontFamily`. `scripts/check-design-tokens.js` holds that as a ratchet.
  - No Korean literals in Core or WinUI sources: every word comes from `Locale.Get`.
- **System.Text.Json serializes computed properties.** Any computed (get-only, expression-bodied) property added to a record that is serialized gets `[JsonIgnore]`. This plan adds none: every new member of a serialized record (`DelegationChildInfo.Agent`, `DelegationChildSummary.Agent`, `DelegationChildStatus.Agent`) is a positional parameter, sent on the pane pipe on purpose, as on the Mac.
- **Public repo hygiene.** No personal paths, hostnames, account names or IPs in code, fixtures, tests or docs. Use `C:\w\…`, `/w/…`, `fixture@example.invalid`.
- **Commits.**
  - Subjects are `(windows) …` or `(mac) …`.
  - Every body ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Never push, never amend.
- **Line numbers** in **Files** are those of the plan's base commit, `13c855e3`. Once an earlier task has edited the same file, find the spot by the member or quoted text the step names; the line number is only a pointer.
- **Words the agent reads are the Mac's** (`DelegationToolManifest`, MCP answers, refusal details), byte for byte, except "this Mac", which is "this PC" on Windows.
- **CORE-RUN** is how every Windows Core check in this plan is run on the Mac. It wraps `Test` temporarily so a failing check prints `FAIL` instead of stopping the run. It restores the wrapper but keeps the new registration lines.

```bash
cd /Users/young/Work/MightyClaude
V=native/windows/MightyClaude.Core.Tests/Verification.cs
perl -0pi -e 's/private static async Task Test\(string name, Func<Task> action\) \{ await action\(\); passed\+\+; Console\.WriteLine\("PASS " \+ name\); \}/private static async Task Test(string name, Func<Task> action) { try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); } }/' "$V"
TMPDIR=/private/tmp/ dotnet run --project native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj > /private/tmp/core-run.log 2>&1; echo "exit $?"
perl -0pi -e 's/private static async Task Test\(string name, Func<Task> action\) \{ try \{ await action\(\); passed\+\+; Console\.WriteLine\("PASS " \+ name\); \} catch \(Exception ex\) \{ Console\.WriteLine\("FAIL " \+ name \+ ": " \+ ex\.GetBaseException\(\)\.Message\); \} \}/private static async Task Test(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }/' "$V"
grep -n 'private static async Task Test(' "$V"   # must show the one-line original again
grep -c '^FAIL' /private/tmp/core-run.log; grep -E '^FAIL' /private/tmp/core-run.log; grep -E '^(FAIL|PASS) delegation' /private/tmp/core-run.log
```

  - **Pass:** exactly 9 `FAIL` lines, the same 9 recorded in `/private/tmp/core-baseline-fails.txt` (Task 2, Step 0), and no `FAIL delegation …` line.
  - **Expected failure** before an implementation exists: a build error naming the missing type or member (`error CS0117`, `CS1061`, `CS0246`, `CS1739`), or `FAIL delegation …` lines naming the new checks.
- **MAC-RUN** is how the Mac delegation suites are run (Command Line Tools toolchain, own scratch path):

```bash
cd /Users/young/Work/MightyClaude
DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-xa --filter '\.(Delegation[A-Za-z]*Tests)/'
```

---

## File Structure

| Path | Change | Responsibility |
|---|---|---|
| `native/contracts/fixtures/delegation-modes.json` | Create | The shared tables: every agent's modes and their capabilities, and the widest child mode for each (parent agent, parent mode, child agent), or null. |
| `native/contracts/README.md` | Modify | Documents the fixture, and says both apps now run children of another agent. |
| `native/macos/Tests/MightyCoreTests/DelegationModesFixtureTests.swift` | Create | The Mac suite: `DelegationProviders` agrees with the fixture. |
| `native/windows/MightyClaude.Core/DelegationProviders.cs` | Modify | `ModeCapability`, `Capabilities`, `IsCovered`, the 4-argument `IsNoWider`, `WidestChildMode`, `HumanChildMode`, `MayHaveHumanChild`, `Agent`, `AgentName`, `Availability`, `HumanChildChoices`, and the `DelegationAgentAvailability`, `DelegationChildBlock` and `DelegationChildChoice` types. |
| `native/windows/MightyClaude.Core/Delegation.cs` | Modify | Reason codes `AgentUnknown` and `AgentUnavailable`, `DelegationResponse.Refusal(reason, detail)`, `DelegationChildInfo.Agent`, `ComposerSecondSends.MenuOnly` and `ReturnsToComposer`. |
| `native/windows/MightyClaude.Core/DelegationCoordinator.cs` | Modify | `Argument.Required`, the delegate/list/status words, `ArgumentProblem` with optional arguments, the `agent` argument passed to `DelegateNow`, and `IDelegationHost.AgentAvailability`. |
| `native/windows/MightyClaude.Core/DelegationDelegateTool.cs` | Modify | `DelegateNow` with `agent`, `Unavailable`, `DelegateRefusal` by child agent, `StartChild` with the parent's agent, `FirstInput`/`HumanFirstRun`/`HumanNote` with `parentAgent`, and `StartHumanChild` with `agent`. |
| `native/windows/MightyClaude.Core/DelegationStatusTools.cs` | Modify | `Agent` on `DelegationChildSummary` and `DelegationChildStatus`. |
| `native/windows/MightyClaude.Core/DelegationMcp.cs` | Modify | Only required arguments in `required`, a refusal's detail, and the agent in child, list and status answers. |
| `native/windows/MightyClaude.Core/DelegationModel.cs` | Modify | `DelegationRequestKey.Make(…, otherAgent)`. |
| `native/windows/MightyClaude.Core/DelegationSidebar.cs` | Modify | `DelegationChildRow.StartedByHuman`/`Agent`, and `DelegationSidebar.OpenHumanChildCount`. |
| `native/windows/MightyClaude.Core/DelegationAppHost.cs` | Modify | `ChildSession(…, agentDefaults)`, and `AddingChild` finding the agent's last-used pane. |
| `native/windows/MightyClaude.Core/DesktopService.Delegation.cs` | Modify | `AgentAvailabilityNow`, `IDelegationHost.AgentAvailability`, `WorktreeChildChoices`, `WorktreeMenuOnly`, `OffersWorktreeMenu`, the readiness gate, and `StartHumanChildAsync(…, agent)`. |
| `native/windows/MightyClaude.WinUI/MainWindow.WorktreeMenu.cs` | Create | The window's CLI availability, the worktree flyout, the menu's send, and the give-back to the composer. |
| `native/windows/MightyClaude.WinUI/MainWindow.QueuedComposer.cs` | Modify | Wires the flyout to the worktree button, and shows the button for its menu alone. |
| `native/windows/MightyClaude.WinUI/MainWindow.Delegation.cs` | Modify | Hands the service the availability hook; `RunInNewWorktree(…, agent)`. |
| `native/windows/MightyClaude.WinUI/MainWindow.DelegationSmoke.cs` | Modify | Step 9, `otherAgentChildStarted`, and the smoke helpers. |
| `scripts/test-native-windows.ps1` | Modify | Checks `delegation.otherAgentChildStarted`. |
| `native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs` | Create | The new Windows checks. |
| `native/windows/MightyClaude.Core.Tests/DelegationDeliveryVerification.cs` | Modify | `DelegationTestHost` learns agent availability. |
| `native/windows/MightyClaude.Core.Tests/DelegationVerification.cs` | Modify | Reason-code list, tool arguments and descriptions. |
| `native/windows/MightyClaude.Core.Tests/DelegationCoordinatorVerification.cs` | Modify | The MCP answers' new words. |
| `native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj` | Modify | Embeds the fixture. |
| `native/windows/MightyClaude.Core.Tests/Verification.cs` | Modify | Registers the new checks. |
| `locales/{ko,en,zh,ja}.json`, plus byte-identical copies in `native/macos/Sources/MightyCore/Resources/Locales/` and `mobile/src/locales/` | Modify | Two Windows keys, and the Windows worktree hint. |
| `docs/i18n.md` | Modify | Regenerated by `check-locales`. |
| `docs/help/content/{ko,en,zh,ja}/delegation.md`, `docs/help/content/{ko,en,zh,ja}/windows.md` | Modify | The help pages. |
| `docs/windows-parity.md` | Modify | Row 78: done on Windows. |

---

## Task 1: The shared mode tables and the Mac's check

**Files:**
- Create: `native/contracts/fixtures/delegation-modes.json`
- Create: `native/macos/Tests/MightyCoreTests/DelegationModesFixtureTests.swift`
- Modify: `native/contracts/README.md`. Insert a new section before line 456 (`## Model labels (\`fixtures/model-labels.json\`)`).

**Interfaces:**
- Consumes (Swift, unchanged):
  - `DelegationProviders.startingModes(provider: String) -> [String]`
  - `DelegationProviders.capabilities(provider: String, mode: String) -> Set<ModeCapability>`
  - `DelegationProviders.widestChildMode(child: String, parent: String, parentMode: String) -> String?`
  - `ProviderOptions.ids: [String]`
- Produces:
  - The fixture shape `{agents: [String], capabilities: {agent: {mode: [capability]}}, widestChildMode: {parentAgent: {parentMode: {childAgent: String | null}}}}`.
  - The Swift suite `DelegationModesFixtureTests`.

- [ ] **Step 1: Write the failing test**

Create `native/macos/Tests/MightyCoreTests/DelegationModesFixtureTests.swift`:

```swift
import Foundation
import Testing
@testable import MightyCore

/// The file the Windows Core checks read too (`DelegationCrossAgentVerification`):
/// what each agent's mode may do, and the mode a child of each agent starts in
/// under each parent agent's mode. The Mac is the reference: these tables were
/// written from its rules, and its rules must still give them.
@Suite(.delegationLane) struct DelegationModesFixtureTests {
    struct Fixture: Decodable {
        let agents: [String]
        let capabilities: [String: [String: [String]]]
        let widestChildMode: [String: [String: [String: String?]]]
    }

    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/delegation-modes.json")
    }()

    static func load() throws -> Fixture { try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url)) }

    @Test func everyAgentsModesHaveTheFixturesCapabilities() throws {
        let fixture = try Self.load()
        #expect(fixture.agents == ProviderOptions.ids)
        #expect(Set(fixture.capabilities.keys) == Set(ProviderOptions.ids))
        for (provider, modes) in fixture.capabilities {
            #expect(Set(modes.keys) == Set(DelegationProviders.startingModes(provider: provider)), "\(provider)")
            for (mode, names) in modes {
                let actual = Set(DelegationProviders.capabilities(provider: provider, mode: mode).map { String(describing: $0) })
                #expect(actual == Set(names), "\(provider) \(mode)")
            }
        }
    }

    @Test func everyParentModesWidestChildModeIsTheFixtures() throws {
        let fixture = try Self.load()
        #expect(Set(fixture.widestChildMode.keys) == Set(ProviderOptions.ids))
        for (parent, modes) in fixture.widestChildMode {
            #expect(Set(modes.keys) == Set(DelegationProviders.startingModes(provider: parent)), "\(parent)")
            for (parentMode, children) in modes {
                #expect(Set(children.keys) == Set(ProviderOptions.ids), "\(parent) \(parentMode)")
                for (child, mode) in children {
                    #expect(DelegationProviders.widestChildMode(child: child, parent: parent, parentMode: parentMode) == mode, "\(child) under \(parent) \(parentMode)")
                }
            }
        }
    }
}
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-xa --filter '\.(DelegationModesFixtureTests)/'`
Expected: both tests fail with a file-not-found error for `delegation-modes.json` (`NSCocoaErrorDomain Code=260`).

- [ ] **Step 3: Implement (write the fixture and document it)**

Create `native/contracts/fixtures/delegation-modes.json`. The values were computed from the Mac's `capabilities`, `isCovered`, `isNoWider` and `widestChildMode`, and agree with `DelegationCrossAgentTests.theWidestChildModeOfEachAgentUnderEachParentMode`:

```json
{
  "agents": ["claude", "codex", "gemini"],
  "capabilities": {
    "claude": {
      "plan": [],
      "manual": ["writesAsking"],
      "acceptEdits": ["writesUnasked"],
      "auto": ["writesUnasked", "runsUnasked"],
      "fullAccess": ["writesAsking", "writesUnasked", "runsUnasked", "fullAccess"]
    },
    "codex": {
      "manual": ["runsUnasked"],
      "acceptEdits": ["writesUnasked", "runsUnasked"],
      "onRequest": ["writesUnasked"],
      "fullAccess": ["writesAsking", "writesUnasked", "runsUnasked", "fullAccess"]
    },
    "gemini": {
      "plan": [],
      "manual": [],
      "acceptEdits": ["writesUnasked"],
      "fullAccess": ["writesAsking", "writesUnasked", "runsUnasked", "fullAccess"]
    }
  },
  "widestChildMode": {
    "claude": {
      "plan": { "claude": "plan", "codex": null, "gemini": null },
      "manual": { "claude": "manual", "codex": null, "gemini": null },
      "acceptEdits": { "claude": "acceptEdits", "codex": "onRequest", "gemini": "acceptEdits" },
      "auto": { "claude": "auto", "codex": "acceptEdits", "gemini": "acceptEdits" },
      "fullAccess": { "claude": "fullAccess", "codex": "fullAccess", "gemini": "fullAccess" }
    },
    "codex": {
      "manual": { "claude": "plan", "codex": null, "gemini": null },
      "acceptEdits": { "claude": "auto", "codex": "acceptEdits", "gemini": "acceptEdits" },
      "onRequest": { "claude": "acceptEdits", "codex": "onRequest", "gemini": "acceptEdits" },
      "fullAccess": { "claude": "fullAccess", "codex": "fullAccess", "gemini": "fullAccess" }
    },
    "gemini": {
      "plan": { "claude": "plan", "codex": null, "gemini": null },
      "manual": { "claude": "plan", "codex": null, "gemini": null },
      "acceptEdits": { "claude": "acceptEdits", "codex": "onRequest", "gemini": "acceptEdits" },
      "fullAccess": { "claude": "fullAccess", "codex": "fullAccess", "gemini": "fullAccess" }
    }
  }
}
```

In `native/contracts/README.md`, insert this section before the line `## Model labels (\`fixtures/model-labels.json\`)`, followed by one blank line:

```markdown
## Delegation modes (`fixtures/delegation-modes.json`)

A child of another agent than its parent's may start only in a mode that may do
nothing the parent's mode may not. A mode is described by four capabilities:
`writesAsking` (changes files after the human approves each change),
`writesUnasked` (changes files without asking, sandboxed or not), `runsUnasked`
(runs shell commands without asking, sandboxed or not) and `fullAccess` (no
sandbox and no asking). A child's mode fits under a parent's when each of its
capabilities is the parent's too, except that the parent's `writesUnasked` covers
the child's `writesAsking`. Within one agent, modes still compare by their own
order (`plan < manual < acceptEdits < onRequest < auto < fullAccess`, each agent
using the ones it has). The fixture is `{agents, capabilities: {agent: {mode:
[capability]}}, widestChildMode: {parentAgent: {parentMode: {childAgent: mode |
null}}}}` over the provider ids `claude`, `codex` and `gemini` (Antigravity).
`widestChildMode` is the mode the worktree button's menu starts a child of that
agent in: the widest that fits and in which the child can change files (the most
capabilities, then the later in its own order; for the parent's own agent, the
parent's mode whenever its child can write in it), or null when none fits.
`DelegationModesFixtureTests` (Swift) and `DelegationCrossAgentVerification`
(Windows Core) read it. Rules: `MightyCore/DelegationProviders.swift`,
`MightyClaude.Core/DelegationProviders.cs`.
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `DEVELOPER_DIR=/Library/Developer/CommandLineTools bash scripts/test-native-macos.sh --scratch-path /tmp/mc-xa --filter '\.(DelegationModesFixtureTests)/'`
Expected: 2 tests passed.
Then run MAC-RUN. Expected: every `Delegation*Tests` suite passes.

- [ ] **Step 5: Commit**

```bash
git add native/contracts/fixtures/delegation-modes.json native/macos/Tests/MightyCoreTests/DelegationModesFixtureTests.swift native/contracts/README.md
git commit -m "(mac) the delegation mode tables become a shared fixture: every agent's modes and what each may do, and the widest child mode of each agent under each parent mode, in native/contracts/fixtures/delegation-modes.json, which a new Mac suite holds DelegationProviders to" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 2: Windows modes across agents, held to the fixture

**Files:**
- Modify: `native/windows/MightyClaude.Core/DelegationProviders.cs`:
  - lines 3-18: the class summary;
  - lines 65-71: the 3-argument `IsNoWider`, replaced by both `IsNoWider` overloads followed by the new members;
  - after line 78 (the class's closing brace), before the `ChildAutoCommit` summary: the new enum.
- Modify: `native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj`, line 15 (after the `mobile-structure.json` resource).
- Create: `native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs`, after line 438 (the `GitSaysWhyItGaveNoResultAndOnlyItsOwnLockGoes` registration).

**Interfaces:**
- Consumes:
  - `DelegationProviders.StartingModes(string provider) -> IReadOnlyList<string>`
  - `DelegationProviders.ChildCanWrite(string provider, string mode) -> bool`
  - `DelegationProviders.IsAgentPane(string kind, string provider) -> bool`
  - `Wire.Providers` (`["claude", "codex", "gemini"]`)
- Produces (C#):
  - `public enum ModeCapability { WritesAsking, WritesUnasked, RunsUnasked, FullAccess }`
  - `public static IReadOnlySet<ModeCapability> DelegationProviders.Capabilities(string provider, string mode)`
  - `public static bool DelegationProviders.IsCovered(IReadOnlySet<ModeCapability> child, IReadOnlySet<ModeCapability> parent)`
  - `public static bool DelegationProviders.IsNoWider(string mode, string child, string stored, string parent)`
  - `public static string? DelegationProviders.WidestChildMode(string child, string parent, string parentMode)`
  - `public static string? DelegationProviders.HumanChildMode(string child, string parent, string parentMode, bool planFirst)`
  - `public static bool DelegationProviders.MayHaveHumanChild(string kind, string provider, string mode)`
  - `public static string? DelegationProviders.Agent(string name)`
  - `public static string DelegationProviders.AgentName(string provider)`
  - `internal static Task DelegationCrossAgentVerification.TheSharedModeTablesHold()`
  - `internal static Task DelegationCrossAgentVerification.AgentsAndModesAcrossAgents()`
- Mirrors (Swift): `capabilities(provider:mode:)`, `isCovered(_:by:)`, `isNoWider(_:child:than:parent:)`, `widestChildMode(child:parent:parentMode:)`, `humanChildMode(child:parent:parentMode:planFirst:)`, `mayHaveHumanChild(kind:provider:mode:)`, `agent(named:)`, `agentName(_:)`.

- [ ] **Step 0: Record the baseline**

Run CORE-RUN on the untouched tree, then:

```bash
grep -E '^FAIL' /private/tmp/core-run.log > /private/tmp/core-baseline-fails.txt; wc -l < /private/tmp/core-baseline-fails.txt
grep -c '^PASS delegation' /private/tmp/core-run.log > /private/tmp/core-baseline-delegation-pass.txt
```

Expected: `9`. Every later CORE-RUN must show exactly these 9 lines. The second file holds the baseline count of `PASS delegation` lines, used in Task 13.

- [ ] **Step 1: Write the failing test**

In `MightyClaude.Core.Tests.csproj`, after the line `<EmbeddedResource Include="../../contracts/fixtures/mobile-structure.json" LogicalName="MightyClaude.Core.Tests.MobileStructure.json" />`, add:

```xml
    <EmbeddedResource Include="../../contracts/fixtures/delegation-modes.json" LogicalName="MightyClaude.Core.Tests.DelegationModes.json" />
```

Create `native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using static DelegationSupport;
using Providers = MightyClaude.Core.DelegationProviders;

/// <summary>
/// A child of another agent than its parent's, ported from the Mac's DelegationCrossAgentTests and held to
/// native/contracts/fixtures/delegation-modes.json (DelegationModesFixtureTests): which mode it starts in, how delegate names
/// its agent, what the worktree button's menu offers, the pane it opens in, and the child a parent or the human makes.
/// </summary>
internal static class DelegationCrossAgentVerification
{
    private static readonly Lazy<JsonElement> Modes = new(() =>
    {
        const string name = "MightyClaude.Core.Tests.DelegationModes.json";
        using var stream = typeof(DelegationCrossAgentVerification).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in MightyClaude.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    /// <summary>A capability as the fixture spells it: <c>writesAsking</c>, <c>writesUnasked</c>, <c>runsUnasked</c>, <c>fullAccess</c>.</summary>
    private static string Name(ModeCapability capability) => JsonNamingPolicy.CamelCase.ConvertName(capability.ToString());

    internal static Task TheSharedModeTablesHold()
    {
        var fixture = Modes.Value;
        SameList(fixture.GetProperty("agents").EnumerateArray().Select(agent => agent.GetString()!), Wire.Providers, "the agents are the app's three, in order");
        SameList(fixture.GetProperty("capabilities").EnumerateObject().Select(agent => agent.Name), Wire.Providers, "every agent has its capabilities");
        foreach (var provider in fixture.GetProperty("capabilities").EnumerateObject())
        {
            SameList(provider.Value.EnumerateObject().Select(mode => mode.Name).Order(StringComparer.Ordinal), Providers.StartingModes(provider.Name).Order(StringComparer.Ordinal), provider.Name + "'s modes");
            foreach (var mode in provider.Value.EnumerateObject())
                SameList(Providers.Capabilities(provider.Name, mode.Name).Select(Name).Order(StringComparer.Ordinal), mode.Value.EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal), $"what {provider.Name} {mode.Name} may do");
        }
        SameList(fixture.GetProperty("widestChildMode").EnumerateObject().Select(agent => agent.Name), Wire.Providers, "every agent is a parent");
        foreach (var parent in fixture.GetProperty("widestChildMode").EnumerateObject())
        {
            SameList(parent.Value.EnumerateObject().Select(mode => mode.Name).Order(StringComparer.Ordinal), Providers.StartingModes(parent.Name).Order(StringComparer.Ordinal), parent.Name + "'s modes as a parent");
            foreach (var parentMode in parent.Value.EnumerateObject())
            {
                SameList(parentMode.Value.EnumerateObject().Select(child => child.Name), Wire.Providers, $"every child agent under {parent.Name} {parentMode.Name}");
                foreach (var child in parentMode.Value.EnumerateObject())
                    Same(Providers.WidestChildMode(child.Name, parent.Name, parentMode.Name), child.Value.ValueKind == JsonValueKind.Null ? null : child.Value.GetString(), $"{child.Name} under {parent.Name} {parentMode.Name}");
            }
        }
        return Task.CompletedTask;
    }

    internal static Task AgentsAndModesAcrossAgents()
    {
        // delegate's agent names an agent by its word, Antigravity also by its provider id, in any case.
        foreach (var (word, provider) in new[] { ("claude", "claude"), ("codex", "codex"), ("antigravity", "gemini"), ("gemini", "gemini"), ("Codex", "codex"), (" ANTIGRAVITY\n", "gemini") })
            Same(Providers.Agent(word), provider, "the agent named " + word.Trim());
        foreach (var word in new[] { "", "gpt", "openai", "agy", "claude code", "codex-cli" }) Same(Providers.Agent(word), null, "no agent is named '" + word + "'");
        SameList(Wire.Providers.Select(Providers.AgentName), ["claude", "codex", "antigravity"], "the tools name the agents by their words");

        // Editing unasked covers editing after asking; everything else is a plain subset.
        static HashSet<ModeCapability> Of(params ModeCapability[] capabilities) => [.. capabilities];
        Check(Providers.IsCovered(Of(ModeCapability.WritesAsking), Of(ModeCapability.WritesUnasked)) && !Providers.IsCovered(Of(ModeCapability.WritesUnasked), Of(ModeCapability.WritesAsking))
            && !Providers.IsCovered(Of(ModeCapability.RunsUnasked), Of(ModeCapability.WritesUnasked)) && Providers.IsCovered(Of(), Of()), "the subset rule");
        // A Claude parent in plan or manual mode gets no Codex or Antigravity child at all.
        foreach (var parentMode in new[] { "plan", "manual" })
            foreach (var (child, modes) in new[] { ("codex", new[] { "manual", "acceptEdits", "onRequest", "fullAccess" }), ("gemini", new[] { "acceptEdits", "fullAccess" }) })
            {
                foreach (var mode in modes) Check(!Providers.IsNoWider(mode, child, parentMode, "claude"), $"{child} {mode} under claude {parentMode}");
                Same(Providers.WidestChildMode(child, "claude", parentMode), null, $"{child} under claude {parentMode}");
            }
        // Claude acceptEdits edits unasked and asks before Bash; auto also runs commands unasked.
        Check(Providers.IsNoWider("onRequest", "codex", "acceptEdits", "claude") && Providers.IsNoWider("acceptEdits", "gemini", "acceptEdits", "claude") && !Providers.IsNoWider("acceptEdits", "codex", "acceptEdits", "claude"), "under Claude acceptEdits");
        Check(Providers.IsNoWider("acceptEdits", "codex", "auto", "claude") && !Providers.IsNoWider("fullAccess", "codex", "auto", "claude"), "under Claude auto");
        // A Codex onRequest parent edits unasked in its sandbox and asks for commands.
        Check(Providers.IsNoWider("acceptEdits", "claude", "onRequest", "codex") && Providers.IsNoWider("manual", "claude", "onRequest", "codex") && !Providers.IsNoWider("auto", "claude", "onRequest", "codex")
            && Providers.IsNoWider("auto", "claude", "acceptEdits", "codex"), "under Codex onRequest and acceptEdits");
        // A read-only Codex edits nothing: only a Claude plan child fits, and it may write once its plan is approved.
        Check(Providers.IsNoWider("plan", "claude", "manual", "codex") && Providers.ChildCanWrite("claude", "plan") && new[] { "manual", "acceptEdits", "auto", "fullAccess" }.All(mode => !Providers.IsNoWider(mode, "claude", "manual", "codex")), "under a read-only Codex");
        Same(Providers.WidestChildMode("claude", "codex", "manual"), "plan", "a read-only Codex's Claude child");
        Same(Providers.WidestChildMode("gemini", "codex", "manual"), null, "a read-only Codex's Antigravity child");
        // A mode that is not its own agent's, on either side, is never no wider.
        Check(!Providers.IsNoWider("onRequest", "claude", "fullAccess", "codex") && !Providers.IsNoWider("acceptEdits", "codex", "onRequest", "claude"), "a mode the agent lacks");
        Same(Providers.WidestChildMode("codex", "claude", "sideways"), null, "an unknown parent mode");
        Same(Providers.WidestChildMode("other", "claude", "fullAccess"), null, "an unknown child agent");
        // One agent's modes compare by its own order, exactly as before.
        foreach (var provider in Wire.Providers)
        {
            var modes = Providers.StartingModes(provider);
            for (var own = 0; own < modes.Count; own++)
                for (var asked = 0; asked < modes.Count; asked++)
                {
                    Same(Providers.IsNoWider(modes[asked], provider, modes[own], provider), asked <= own, $"{provider} {modes[asked]} under {modes[own]}");
                    Same(Providers.IsNoWider(modes[asked], modes[own], provider), asked <= own, $"{provider} {modes[asked]} under {modes[own]} (3 arguments)");
                }
        }
        // The human's child of the parent's agent starts as before; of another, in its widest mode, without the style's plan.
        Same(Providers.HumanChildMode("claude", "claude", "auto", false), "auto", "Claude's own child");
        Same(Providers.HumanChildMode("claude", "claude", "auto", true), "plan", "Claude's own child, plan first");
        Same(Providers.HumanChildMode("codex", "codex", "onRequest", true), "onRequest", "Codex has no plan mode");
        Same(Providers.HumanChildMode("codex", "codex", "manual", false), null, "a read-only Codex's own child");
        Same(Providers.HumanChildMode("gemini", "gemini", "acceptEdits", true), null, "Antigravity's plan child cannot write");
        Same(Providers.HumanChildMode("codex", "claude", "auto", true), "acceptEdits", "Claude auto's Codex child");
        Same(Providers.HumanChildMode("claude", "codex", "onRequest", true), "acceptEdits", "Codex onRequest's Claude child");
        Same(Providers.HumanChildMode("claude", "codex", "manual", false), "plan", "a read-only Codex's Claude child");
        Same(Providers.HumanChildMode("gemini", "claude", "plan", false), null, "Claude plan's Antigravity child");
        // Where the pane's own child cannot write, another agent's may.
        foreach (var (provider, mode) in new[] { ("codex", "manual"), ("gemini", "plan"), ("gemini", "manual") }) Check(Providers.MayHaveHumanChild("claude", provider, mode), $"{provider} {mode} may have a Claude child");
        Check(Providers.MayHaveHumanChild("claude", "claude", "plan") && !Providers.MayHaveHumanChild("shell", "claude", "fullAccess"), "only an agent pane");
        return Task.CompletedTask;
    }
}
```

In `Verification.cs`, after line 438 (`await Test("delegation: git says why it gave no result, and only its own lock goes", …);`), add:

```csharp
        await Test("delegation: every agent's modes and widest child modes are native/contracts/fixtures/delegation-modes.json", DelegationCrossAgentVerification.TheSharedModeTablesHold);
        await Test("delegation: agents are named by their words, and modes compare within and across agents", DelegationCrossAgentVerification.AgentsAndModesAcrossAgents);
```

- [ ] **Step 2: Run it and expect it to fail**

Run CORE-RUN.
Expected: the build fails with `error CS0117: 'DelegationProviders' does not contain a definition for 'Capabilities'` (and for `WidestChildMode`, `Agent`, `AgentName`, `IsCovered`, `HumanChildMode`, `MayHaveHumanChild`), and `CS0246` for `ModeCapability`.

- [ ] **Step 3: Implement**

In `DelegationProviders.cs`, replace the class summary (lines 3-18) with:

```csharp
/// <summary>
/// Which agent panes take part in delegation, and how (M/DelegationProviders.swift). A child runs the agent its parent chose:
/// its own by default, or any of Claude, Codex and Antigravity.
/// <list type="bullet">
/// <item>Claude and Codex panes may be parents by agent: their runs get the delegation server, and their delegate call makes
/// children, of their own agent unless the call names another (<c>agent</c>). A Codex run gets it only where its tool calls can
/// work: acceptEdits or fullAccess.</item>
/// <item>Claude, Codex and Antigravity panes may be parents by human: Ctrl+Enter on a busy one runs the text in a new child
/// worktree of the same agent, in the parent's mode; the worktree button's menu picks any agent, in <see cref="WidestChildMode"/>.
/// Antigravity has no delegation tools, so its children come only from the human.</item>
/// <item>A child of the parent's agent may start in a mode no wider than the parent's by rank. A child of another agent may start
/// in a mode that may do nothing the parent's mode may not (<see cref="Capabilities"/>). A child's mode is always one of its own agent's.</item>
/// <item>Only a Claude run takes text while it runs (steer).</item>
/// <item>A Claude child commits its own work. A Codex or Antigravity child cannot commit outside fullAccess, so the app commits
/// what it leaves when its report changes (<see cref="ChildAutoCommit"/>). Everything that runs or reads the child follows the
/// child's agent; notices and merges follow the parent's.</item>
/// <item>A Claude parent's merge tool call follows its CLI's permission mode; any other parent may merge by tool only when its run
/// started in fullAccess, and is otherwise refused with <c>merge_human_only</c>: the human merges from the child's card.</item>
/// </list>
/// </summary>
```

Replace the 3-argument `IsNoWider` (lines 65-71, its summary included) with:

```csharp
    /// <summary>Whether <paramref name="mode"/> lets a <paramref name="provider"/> agent act no more freely than <paramref name="stored"/> does. A mode that is not one of the provider's, on either side, is never no wider.</summary>
    public static bool IsNoWider(string mode, string stored, string provider) => IsNoWider(mode, provider, stored, provider);

    /// <summary>
    /// Whether <paramref name="mode"/>, one of the <paramref name="child"/> agent's modes, lets it act no more freely than the
    /// <paramref name="parent"/> agent's <paramref name="stored"/> mode. For one agent this is its own modes' order; for another,
    /// the child's mode may do nothing the parent's may not (<see cref="Capabilities"/>). A mode that is not its own agent's, on
    /// either side, is never no wider.
    /// </summary>
    public static bool IsNoWider(string mode, string child, string stored, string parent)
    {
        if (!StartingModes(child).Contains(mode) || !StartingModes(parent).Contains(stored)) return false;
        if (child == parent) return Array.IndexOf(ModeRank, mode) <= Array.IndexOf(ModeRank, stored);
        return IsCovered(Capabilities(child, mode), Capabilities(parent, stored));
    }

    /// <summary>
    /// What a run of <paramref name="provider"/> in <paramref name="mode"/> may do, as the app starts it (M/DelegationProviders.swift
    /// capabilities). Claude: plan edits nothing until its plan is approved; manual asks before each edit; acceptEdits edits unasked
    /// and still asks before Bash; auto also runs commands unasked. Codex: manual runs commands unasked but edits nothing; onRequest
    /// edits in its sandbox unasked and asks to go beyond it; acceptEdits edits and runs commands unasked. Antigravity: plan and
    /// manual edit nothing; acceptEdits edits unasked. fullAccess is full access for every agent.
    /// </summary>
    public static IReadOnlySet<ModeCapability> Capabilities(string provider, string mode) => (provider, mode) switch
    {
        ("claude", "manual") => new HashSet<ModeCapability> { ModeCapability.WritesAsking },
        ("claude", "acceptEdits") or ("codex", "onRequest") or ("gemini", "acceptEdits") => new HashSet<ModeCapability> { ModeCapability.WritesUnasked },
        ("claude", "auto") or ("codex", "acceptEdits") => new HashSet<ModeCapability> { ModeCapability.WritesUnasked, ModeCapability.RunsUnasked },
        ("codex", "manual") => new HashSet<ModeCapability> { ModeCapability.RunsUnasked },
        (_, "fullAccess") => Enum.GetValues<ModeCapability>().ToHashSet(),
        _ => new HashSet<ModeCapability>(),
    };

    /// <summary>Whether a mode with <paramref name="child"/> capabilities does nothing a mode with <paramref name="parent"/> capabilities may not: each is the parent's too, except that editing after asking is covered by editing unasked.</summary>
    public static bool IsCovered(IReadOnlySet<ModeCapability> child, IReadOnlySet<ModeCapability> parent) =>
        child.All(capability => parent.Contains(capability) || capability == ModeCapability.WritesAsking && parent.Contains(ModeCapability.WritesUnasked));

    /// <summary>
    /// The widest of the <paramref name="child"/> agent's modes that is no wider than the <paramref name="parent"/> agent's
    /// <paramref name="parentMode"/> and in which that child can change files, or null when it has none: the one with the most
    /// capabilities, then the later in its own order. For the parent's own agent it is the last by its own order: the parent's
    /// mode whenever its child can write in it.
    /// </summary>
    public static string? WidestChildMode(string child, string parent, string parentMode)
    {
        var fitting = StartingModes(child).Where(mode => IsNoWider(mode, child, parentMode, parent) && ChildCanWrite(child, mode)).ToList();
        if (child == parent) return fitting.LastOrDefault();
        return fitting.Select((mode, index) => (Mode: mode, Index: index)).OrderBy(item => Capabilities(child, item.Mode).Count).ThenBy(item => item.Index).Select(item => item.Mode).LastOrDefault();
    }

    /// <summary>
    /// The mode a child of the <paramref name="child"/> agent that the human starts from a <paramref name="parent"/> pane in
    /// <paramref name="parentMode"/> begins in, or null when there is none in which it could change files. A child of the parent's
    /// own agent starts in the parent's mode, or in plan mode when <paramref name="planFirst"/> and the agent has one, as
    /// Ctrl+Enter always did; any other agent's in <see cref="WidestChildMode"/>.
    /// </summary>
    public static string? HumanChildMode(string child, string parent, string parentMode, bool planFirst)
    {
        if (child != parent) return WidestChildMode(child, parent, parentMode);
        var mode = planFirst && StartingModes(child).Contains("plan") ? "plan" : parentMode;
        return ChildCanWrite(child, mode) ? mode : null;
    }

    /// <summary>Whether the pane, in <paramref name="mode"/> now, may have a child the human starts of any agent: its own (<see cref="CanParentByHuman"/>), or another with a mode in which its child can change files.</summary>
    public static bool MayHaveHumanChild(string kind, string provider, string mode) =>
        IsAgentPane(kind, provider) && Wire.Providers.Any(agent => HumanChildMode(agent, provider, mode, false) is not null);

    /// <summary>The provider id delegate's <c>agent</c> argument names: claude, codex or antigravity (or Antigravity's provider id, gemini), in any case; null for any other word.</summary>
    public static string? Agent(string name) => name.Trim().ToLowerInvariant() switch
    {
        "claude" => "claude",
        "codex" => "codex",
        "antigravity" or "gemini" => "gemini",
        _ => null,
    };

    /// <summary>The name the delegation tools give the agent <paramref name="provider"/>: its <c>agent</c> argument's word (antigravity for gemini).</summary>
    public static string AgentName(string provider) => provider == "gemini" ? "antigravity" : provider;
```

After the class's closing brace (line 78) and before the `ChildAutoCommit` summary, add:

```csharp
/// <summary>What a run in a permission mode may do on its own, to compare the modes of different agents (<see cref="DelegationProviders.Capabilities"/>).</summary>
public enum ModeCapability
{
    /// <summary>It changes files only after the human approves each change.</summary>
    WritesAsking,
    /// <summary>It changes files without asking, sandboxed or not.</summary>
    WritesUnasked,
    /// <summary>It runs shell commands without asking the human, sandboxed or not.</summary>
    RunsUnasked,
    /// <summary>No sandbox and no asking.</summary>
    FullAccess,
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run CORE-RUN.
Expected:
- `PASS delegation: every agent's modes and widest child modes are native/contracts/fixtures/delegation-modes.json`
- `PASS delegation: agents are named by their words, and modes compare within and across agents`
- every other `delegation` line is PASS;
- the 9 baseline FAILs only.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/DelegationProviders.cs native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) Mighty Orchestration compares modes across agents as the Mac does: each mode's capabilities (edits asked or not, commands run unasked, full access), a child of another agent only in a mode its parent's covers, the widest such mode in which it can change files, delegate's agent words, all held to native/contracts/fixtures/delegation-modes.json" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 3: Availability, the menu's choices and its cap

**Files:**
- Modify: `native/windows/MightyClaude.Core/DelegationProviders.cs`:
  - inside the class, after `AgentName`: `Availability`, `HumanChildChoices`;
  - after `ModeCapability`: the new types.
- Modify: `native/windows/MightyClaude.Core/Delegation.cs`, lines 78-91 (`ComposerSecondSends`): add `ReturnsToComposer`, `MenuOnly`.
- Modify: `native/windows/MightyClaude.Core/DelegationSidebar.cs`:
  - line 13 and lines 16-18: the `DelegationChildRow` record and `Of`;
  - after line 64: `OpenHumanChildCount`.
- Modify: `native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs`, before the class's closing brace.
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs`, after the registrations Task 2 added.

**Interfaces:**
- Consumes:
  - `CliAccountStatus` (`Installed`, `LoggedIn`)
  - `ChildState.IsOpen()`
  - `ChildRecord.NotifiesParent`, `ChildRecord.Agent`
  - `DelegationCoordinator.MaximumOpenChildren`
- Produces (C#):
  - `public enum DelegationAgentAvailability { Ready, NotInstalled, SignedOut }`. `Ready` is first, so a host call that throws reads as ready.
  - `public enum DelegationChildBlock { NotInstalled, SignedOut, NoMode, Cap }`
  - `public sealed record DelegationChildChoice(string Agent, string? Mode, DelegationChildBlock? Blocked)`
  - `public static DelegationAgentAvailability DelegationProviders.Availability(bool? installed, CliAccountStatus? account)`
  - `public static IReadOnlyList<DelegationChildChoice> DelegationProviders.HumanChildChoices(string parent, string parentMode, bool planFirst, int openHumanChildren, Func<string, DelegationAgentAvailability> availability)`
  - `public static bool ComposerSecondSends.ReturnsToComposer(string? childAgent, string parentAgent)`
  - `public static bool ComposerSecondSends.MenuOnly(string kind, string provider, string mode, bool isChild, bool delegationOn, bool canHostWorktree, IReadOnlyList<DelegationChildChoice> choices)`
  - `public sealed record DelegationChildRow(…, bool StartedByHuman = false, string Agent = "claude")`
  - `public static int DelegationSidebar.OpenHumanChildCount(string parentId, IEnumerable<DelegationChildRow> children)`
- Mirrors (Swift): `availability(installed:account:)`, `humanChildChoices(parent:parentMode:planFirst:openHumanChildren:availability:)`, `ComposerSecondSend.returnsToComposer(childAgent:parentAgent:)`, `ComposerSecondSend.menuOnly(…)`, `DelegationSidebar.openHumanChildCount(of:children:)`.

- [ ] **Step 1: Write the failing test**

Append to `DelegationCrossAgentVerification` (before its closing brace):

```csharp
    internal static Task AvailabilityTheMenuAndItsCap()
    {
        const DelegationAgentAvailability Ready = DelegationAgentAvailability.Ready, NotInstalled = DelegationAgentAvailability.NotInstalled, SignedOut = DelegationAgentAvailability.SignedOut;
        // Only what the app last learnt holds an agent back; what it could not read holds nothing back.
        Same(Providers.Availability(null, null), Ready, "nothing known");
        Same(Providers.Availability(true, new CliAccountStatus { Provider = "codex", LoggedIn = true }), Ready, "found and signed in");
        Same(Providers.Availability(true, new CliAccountStatus { Provider = "codex", LoggedIn = null }), Ready, "sign-in unreadable");
        Same(Providers.Availability(false, null), NotInstalled, "not found");
        Same(Providers.Availability(true, new CliAccountStatus { Provider = "codex", Installed = false }), NotInstalled, "says it is not installed");
        Same(Providers.Availability(null, new CliAccountStatus { Provider = "gemini", LoggedIn = false }), SignedOut, "signed out");

        // A Claude parent in auto: Claude is its own agent, so its CLI is not asked about.
        var states = new Dictionary<string, DelegationAgentAvailability> { ["claude"] = NotInstalled, ["codex"] = SignedOut, ["gemini"] = NotInstalled };
        SameList<DelegationChildChoice>(Providers.HumanChildChoices("claude", "auto", false, 0, agent => states[agent]),
            [new("claude", "auto", null), new("codex", "acceptEdits", DelegationChildBlock.SignedOut), new("gemini", "acceptEdits", DelegationChildBlock.NotInstalled)], "Claude auto's menu");
        var ready = Providers.HumanChildChoices("claude", "auto", true, 2, _ => Ready);
        SameList<string?>(ready.Select(choice => choice.Mode), ["plan", "acceptEdits", "acceptEdits"], "plan first stays with Claude's own child");
        Check(ready.All(choice => choice.Blocked is null), "two open children leave room");
        SameList<DelegationChildBlock?>(Providers.HumanChildChoices("claude", "manual", false, 0, _ => Ready).Select(choice => choice.Blocked), [null, DelegationChildBlock.NoMode, DelegationChildBlock.NoMode], "Claude manual gets no Codex or Antigravity child");
        SameList<DelegationChildChoice>(Providers.HumanChildChoices("codex", "manual", false, 0, _ => Ready),
            [new("claude", "plan", null), new("codex", null, DelegationChildBlock.NoMode), new("gemini", null, DelegationChildBlock.NoMode)], "a read-only Codex offers only a Claude plan child");
        var codex = Providers.HumanChildChoices("codex", "onRequest", false, 0, _ => Ready);
        SameList<string?>(codex.Select(choice => choice.Mode), ["acceptEdits", "onRequest", "acceptEdits"], "Codex onRequest's menu");
        Check(codex.All(choice => choice.Blocked is null), "Codex onRequest offers all three");
        SameList<DelegationChildBlock?>(Providers.HumanChildChoices("codex", "fullAccess", false, 3, _ => Ready).Select(choice => choice.Blocked), [DelegationChildBlock.Cap, DelegationChildBlock.Cap, DelegationChildBlock.Cap], "three open children, of any agents: none more");

        // The menu's cap counts the open children the human started from that parent, of every agent.
        DelegationChildRow[] rows =
        [
            new("a", "p", ChildState.Running, StartedByHuman: true, Agent: "codex"), new("b", "p", ChildState.Reported, StartedByHuman: true), new("c", "p", ChildState.Running),
            new("d", "p", ChildState.Closed, StartedByHuman: true, Agent: "gemini"), new("e", "q", ChildState.Running, StartedByHuman: true),
        ];
        Same(DelegationSidebar.OpenHumanChildCount("p", rows), 2, "open human children of p");
        var file = DelegationFile.Of([ChildRecord.Make("x", "p", "/w/x", "main", Commit, "acceptEdits", "k", startedByHuman: true, provider: "codex")]);
        Check(DelegationSidebar.Rows(file).Single() is { Agent: "codex", StartedByHuman: true }, "a row carries the human's mark and the child's agent");

        // The worktree button shows for its menu alone only where the pane's own child cannot write but another's can.
        DelegationChildChoice[] other = [new("codex", null, DelegationChildBlock.NoMode), new("claude", "plan", null)];
        bool MenuOnly(string provider, string mode, IReadOnlyList<DelegationChildChoice> choices, bool isChild = false, bool on = true, bool host = true) =>
            ComposerSecondSends.MenuOnly("claude", provider, mode, isChild, on, host, choices);
        Check(MenuOnly("codex", "manual", other), "a read-only Codex shows the menu alone");
        Check(!MenuOnly("codex", "manual", other, isChild: true) && !MenuOnly("codex", "manual", other, on: false) && !MenuOnly("codex", "manual", other, host: false), "not in a child, with the switch off, or without a checkout");
        Check(!MenuOnly("codex", "acceptEdits", other) && !MenuOnly("claude", "plan", other), "where the pane's own child can write, Ctrl+Enter and the click make one");
        foreach (var (provider, mode) in new[] { ("codex", "manual"), ("gemini", "plan"), ("gemini", "manual") })
        {
            var choices = Providers.HumanChildChoices(provider, mode, false, 0, _ => Ready);
            SameList(choices.Where(choice => choice.Blocked is null).Select(choice => choice.Agent), ["claude"], $"{provider} {mode}: only Claude");
            Same(choices[0].Mode, "plan", $"{provider} {mode}: in plan mode");
            Check(MenuOnly(provider, mode, choices), $"{provider} {mode}: the menu alone");
            var without = Providers.HumanChildChoices(provider, mode, false, 0, agent => agent == "claude" ? NotInstalled : Ready);
            Check(!MenuOnly(provider, mode, without), $"{provider} {mode}: nothing to run, no button");
        }
        // A text for another agent goes back to the composer, never to the pane.
        Check(ComposerSecondSends.ReturnsToComposer("codex", "claude") && ComposerSecondSends.ReturnsToComposer("claude", "gemini")
            && !ComposerSecondSends.ReturnsToComposer(null, "claude") && !ComposerSecondSends.ReturnsToComposer("codex", "codex"), "who gets a text whose child did not start");
        return Task.CompletedTask;
    }
```

In `Verification.cs`, after the two lines Task 2 added, add:

```csharp
        await Test("delegation: what the app knows of each CLI, the worktree menu and its cap", DelegationCrossAgentVerification.AvailabilityTheMenuAndItsCap);
```

- [ ] **Step 2: Run it and expect it to fail**

Run CORE-RUN.
Expected: the build fails with `CS0246` for `DelegationAgentAvailability`, `DelegationChildChoice` and `DelegationChildBlock`; `CS0117` for `Availability`, `HumanChildChoices`, `OpenHumanChildCount`, `MenuOnly` and `ReturnsToComposer`; and `CS1739` for `StartedByHuman` on `DelegationChildRow`.

- [ ] **Step 3: Implement**

In `DelegationProviders.cs`, inside the class after `AgentName`, add:

```csharp
    /// <summary>
    /// Whether an agent's CLI can run a child, as the app last learnt it: <paramref name="installed"/> from its CLI search,
    /// <paramref name="account"/> from its sign-in status. Either one unknown (null) does not hold a child back; the app never asks
    /// a CLI for this.
    /// </summary>
    public static DelegationAgentAvailability Availability(bool? installed, CliAccountStatus? account)
    {
        if (installed == false || account is { Installed: false }) return DelegationAgentAvailability.NotInstalled;
        if (account is { LoggedIn: false }) return DelegationAgentAvailability.SignedOut;
        return DelegationAgentAvailability.Ready;
    }

    /// <summary>
    /// What the worktree button's menu offers on a <paramref name="parent"/> pane in <paramref name="parentMode"/> with
    /// <paramref name="openHumanChildren"/> open children the human started: one row per agent, Claude, Codex, Antigravity. A row
    /// says why it cannot run: the agent's CLI (not for the parent's own agent, whose run is going), no mode in which its child
    /// could change files, or the cap.
    /// </summary>
    public static IReadOnlyList<DelegationChildChoice> HumanChildChoices(string parent, string parentMode, bool planFirst, int openHumanChildren, Func<string, DelegationAgentAvailability> availability) =>
        Wire.Providers.Select(agent =>
        {
            var mode = HumanChildMode(agent, parent, parentMode, planFirst);
            DelegationChildBlock? blocked = (agent == parent ? DelegationAgentAvailability.Ready : availability(agent)) switch
            {
                DelegationAgentAvailability.NotInstalled => DelegationChildBlock.NotInstalled,
                DelegationAgentAvailability.SignedOut => DelegationChildBlock.SignedOut,
                _ => mode is null ? DelegationChildBlock.NoMode : openHumanChildren >= DelegationCoordinator.MaximumOpenChildren ? DelegationChildBlock.Cap : (DelegationChildBlock?)null,
            };
            return new DelegationChildChoice(agent, mode, blocked);
        }).ToList();
```

After the `ModeCapability` enum, add:

```csharp
/// <summary>Whether an agent's CLI can run a child now, as the app last learnt it. Ready is the default, so a host that knows nothing holds no child back.</summary>
public enum DelegationAgentAvailability
{
    Ready,
    /// <summary>The CLI was not found, or says it is not installed.</summary>
    NotInstalled,
    /// <summary>The CLI says it is signed out.</summary>
    SignedOut,
}

/// <summary>Why the worktree button's menu cannot run a child of one agent now.</summary>
public enum DelegationChildBlock
{
    NotInstalled,
    SignedOut,
    /// <summary>No mode of the agent, no wider than the parent's, lets its child change files.</summary>
    NoMode,
    /// <summary>The parent has its 3 open children the human started.</summary>
    Cap,
}

/// <summary>One agent the composer's worktree button offers in its menu (M/DelegationProviders.swift DelegationChildChoice).</summary>
/// <param name="Agent">The provider id.</param>
/// <param name="Mode">The mode the child would start in; null when none would do.</param>
/// <param name="Blocked">null when the child can start.</param>
public sealed record DelegationChildChoice(string Agent, string? Mode, DelegationChildBlock? Blocked);
```

In `Delegation.cs`, inside `ComposerSecondSends`, after `Fallback` (line 90), add:

```csharp
    /// <summary>
    /// Whether a text whose child could not be made or started goes back to the composer instead of to the pane: the human picked
    /// another agent than the pane's from the worktree button's menu (M/Delegation.swift returnsToComposer).
    /// </summary>
    public static bool ReturnsToComposer(string? childAgent, string parentAgent) => childAgent is not null && childAgent != parentAgent;

    /// <summary>
    /// Whether the worktree button shows for its menu alone: where <see cref="Choose"/> does not open a new worktree because the
    /// pane's own agent's child could not change files in <paramref name="mode"/>, yet the pane may have a child of another agent,
    /// and one of <paramref name="choices"/> can run now. Its click then opens the menu, and Ctrl+Enter does what it did before.
    /// </summary>
    public static bool MenuOnly(string kind, string provider, string mode, bool isChild, bool delegationOn, bool canHostWorktree, IReadOnlyList<DelegationChildChoice> choices) =>
        delegationOn && !isChild && canHostWorktree && DelegationProviders.IsAgentPane(kind, provider)
        && !DelegationProviders.CanParentByHuman(kind, provider, mode) && choices.Any(choice => choice.Blocked is null);
```

In `DelegationSidebar.cs`:
- Add these `<param>` lines to the `DelegationChildRow` summary:

```csharp
/// <param name="StartedByHuman">The human started it from its parent's composer.</param>
/// <param name="Agent">The agent CLI it runs (<see cref="ChildRecord.Agent"/>).</param>
```

- Replace lines 13-18 (the record line and `Of`) with:

```csharp
public sealed record DelegationChildRow(string Id, string ParentSessionId, ChildState State, string? Task = null, string ParentBranch = "", string? ParentCheckout = null, bool CanUndo = false, int HeldNotices = 0,
    bool StartedByHuman = false, string Agent = "claude")
{
    /// <summary>The row of <paramref name="record"/>, its task read from <paramref name="file"/>'s stored copy.</summary>
    public static DelegationChildRow Of(ChildRecord record, DelegationFile file) => new(record.Id, record.ParentSessionId, record.State,
        file.Copy(record.Id, DelegationCopyKind.Task) is { } task ? PaneTitle.Shortened(task.Text) : null, record.ParentBranch, record.ParentCheckout,
        file.UndoableMerge(record.Id) is not null, file.HeldNotices(record.Id).Count, !record.NotifiesParent, record.Agent);
```

- After `OpenChildCount` (after line 64), add:

```csharp
    /// <summary>How many open children the human started from the parent pane <paramref name="parentId"/>, as the worktree button's menu counts them toward its cap.</summary>
    public static int OpenHumanChildCount(string parentId, IEnumerable<DelegationChildRow> children) =>
        children.Count(child => child.ParentSessionId == parentId && child.StartedByHuman && child.State.IsOpen());
```

- [ ] **Step 4: Run it and expect it to pass**

Run CORE-RUN.
Expected: `PASS delegation: what the app knows of each CLI, the worktree menu and its cap`, all other `delegation` lines PASS, and the 9 baseline FAILs only.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/DelegationProviders.cs native/windows/MightyClaude.Core/Delegation.cs native/windows/MightyClaude.Core/DelegationSidebar.cs native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the worktree menu's rules as the Mac's: what the app last learnt of each agent's CLI holds it back, one row per agent with why it cannot run (not installed, not signed in, no mode that can edit, the cap of 3 counted over every agent), the button shows for its menu alone where only another agent's child fits, and such a text goes back to the composer" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 4: The tool surface names the agent

**Files:**
- Modify: `native/windows/MightyClaude.Core/Delegation.cs`:
  - lines 127-137 (`DelegationReasonCode`): append two codes;
  - lines 170-176 (`DelegationResponse`): add the `Refusal` overload;
  - lines 178-182 (`DelegationChildInfo`).
- Modify: `native/windows/MightyClaude.Core/DelegationCoordinator.cs`:
  - lines 5-30: the manifest summary, `Argument`, the `Delegate`, `ListChildren` and `ChildStatus` words;
  - lines 258-264: `ArgumentProblem`.
- Modify: `native/windows/MightyClaude.Core/DelegationStatusTools.cs`, lines 3-23.
- Modify: `native/windows/MightyClaude.Core/DelegationMcp.cs`:
  - line 107 (`required`);
  - lines 116-121 (`Hint`);
  - lines 132-152 (`Result`).
- Modify: `native/windows/MightyClaude.Core/DelegationModel.cs`, lines 144-161 (the `DelegationRequestKey` summary and class).
- Modify: `native/windows/MightyClaude.Core/DelegationDelegateTool.cs`, after line 43 (`IsNoWider`): the static `Unavailable`.
- Modify (existing checks):
  - `native/windows/MightyClaude.Core.Tests/DelegationVerification.cs`, lines 123-127, 423, 425-426;
  - `native/windows/MightyClaude.Core.Tests/DelegationCoordinatorVerification.cs`, lines 95-96, 164, 482, 492, 496, 499, 546, 548.
- Modify: `native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs`, `Verification.cs`.

**Interfaces:**
- Consumes: `DelegationProviders.AgentName`, `ChildRecord.Agent`.
- Produces (C#):
  - `DelegationReasonCode.AgentUnknown` (wire `agent_unknown`), `DelegationReasonCode.AgentUnavailable` (wire `agent_unavailable`)
  - `public static DelegationResponse DelegationResponse.Refusal(DelegationReasonCode reason, string detail)`
  - `public sealed record DelegationChildInfo(string Id, ChildState State, string Branch, string StartingMode, string Agent = "claude")`
  - `public sealed record DelegationChildSummary(string Id, ChildState State, int Revision, string Agent = "claude")`
  - `public sealed record DelegationChildStatus(string Id, ChildState State, int Revision, string? Head, string? Report, bool ReportTruncated, string Agent = "claude")`
  - `public sealed record DelegationToolManifest.Argument(string Name, string Description, bool Required = true)`
  - `public static string DelegationRequestKey.Make(string parentSessionId, string parentRunId, string task, string startingMode, string? otherAgent = null)`
- Mirrors (Swift): `DelegationReasonCode.agentUnknown/agentUnavailable`, `DelegationResponse.refusal(_:detail:)`, `DelegationChildInfo.agent`, `DelegationChildSummary.agent`, `DelegationChildStatus.agent`, `DelegationToolManifest.Argument.required`, `DelegationRequestKey.make(…otherAgent:)`, `DelegationMCPServer.result(_:)`.

- [ ] **Step 1: Write the failing test (and update the checks whose words change)**

Append to `DelegationCrossAgentVerification`:

```csharp
    private static string TextOf(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    internal static Task TheToolTakesAnOptionalAgentAndNamesItEverywhere()
    {
        Same(DelegationNames.Of(DelegationReasonCode.AgentUnknown), "agent_unknown", "agent_unknown on the wire");
        Same(DelegationNames.Of(DelegationReasonCode.AgentUnavailable), "agent_unavailable", "agent_unavailable on the wire");
        // delegate takes an optional agent and no other new argument.
        var delegate_ = DelegationToolManifest.Delegate;
        SameList(delegate_.Arguments.Select(argument => argument.Name), ["task", "mode", "agent"], "delegate's arguments");
        SameList(delegate_.Arguments.Select(argument => argument.Required), [true, true, false], "only agent is optional");
        Check(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["task"] = "t", ["mode"] = "plan" }, delegate_) is null
            && DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["task"] = "t", ["mode"] = "plan", ["agent"] = "codex" }, delegate_) is null, "with or without agent");
        const string problem = "delegate takes exactly these string arguments: task, mode, and optionally agent.";
        Same(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["task"] = "t", ["agent"] = "codex" }, delegate_), problem, "mode is still required");
        Same(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["task"] = "t", ["mode"] = "plan", ["model"] = "opus" }, delegate_), problem, "no other argument");
        Check(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string> { ["child"] = "c" }, DelegationToolManifest.ChildStatus) is null, "child_status takes its child");
        Same(DelegationCoordinator.ArgumentProblem(new Dictionary<string, string>(), DelegationToolManifest.ChildStatus), "child_status takes exactly these string arguments: child.", "child_status without its child");
        SameList(DelegationMcp.Definition(delegate_)["inputSchema"]!["required"]!.AsArray().Select(name => name!.GetValue<string>()), ["task", "mode"], "the schema requires task and mode");
        foreach (var agent in new[] { "Claude", "Codex", "Antigravity" }) Check(delegate_.Arguments[1].Description.Contains(agent + " child", StringComparison.Ordinal), "mode names a " + agent + " child's modes");

        // A refusal names what it is about after its one code.
        var detail = DelegationCoordinator.Unavailable("codex", DelegationAgentAvailability.SignedOut) ?? throw new InvalidOperationException("no detail for a signed-out agent");
        Same(detail, "Codex is not signed in on this PC; the user can check it in Mighty Claude's Settings › CLI", "a signed-out agent's detail");
        Check(DelegationCoordinator.Unavailable("gemini", DelegationAgentAvailability.NotInstalled)?.EndsWith("Settings › CLI", StringComparison.Ordinal) == true
            && DelegationCoordinator.Unavailable("codex", DelegationAgentAvailability.Ready) is null, "a missing agent's detail, and none for a ready one");
        var refused = DelegationMcp.Result(DelegationResponse.Refusal(DelegationReasonCode.AgentUnavailable, detail));
        Same(refused["structuredContent"]!.ToJsonString(), """{"refused":"agent_unavailable"}""", "the refusal is tool data");
        Same(TextOf(refused), $"Refused: agent_unavailable ({detail}). Nothing was changed.", "the refusal's text names the agent");
        Same(TextOf(DelegationMcp.Result(DelegationResponse.Refusal(DelegationReasonCode.AgentUnknown))), "Refused: agent_unknown (agent must be claude, codex or antigravity, or left out for your own). Nothing was changed.", "agent_unknown says what to do");

        // The request key adds the child's agent only when it is not the caller's own.
        Same(DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits"), "0767d17eeeaaa20c3f5c0e3a11dc260bd005cc116a76ea02adb2c78fb3a48ef7", "the caller's own agent keeps its key");
        Same(DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits", null), "0767d17eeeaaa20c3f5c0e3a11dc260bd005cc116a76ea02adb2c78fb3a48ef7", "no other agent is no fifth part");
        Same(DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits", "codex"), "f051d5f56c52b257a1483760fee991d3622cd74bfb03cd1a90da23be50177e8b", "a Codex child's key");
        Same(DelegationRequestKey.Make("parent-1", "run-1", "Fix the login bug", "acceptEdits", "gemini"), "28e97194accdf6d2519a39c9a1144dda67ee4d149fd3193204edc986fc040e3c", "an Antigravity child's key");

        // delegate's child, list_children and child_status name the child's agent by its word.
        var gemini = ChildRecord.Make("c1", "p", "/w/c1", "main", Commit, "acceptEdits", "k", state: ChildState.Running, provider: "gemini");
        Same(DelegationChildInfo.Of(gemini).Agent, "antigravity", "delegate's child");
        Same(DelegationChildSummary.Of(gemini).Agent, "antigravity", "list_children's row");
        Same(DelegationChildStatus.Of(gemini, null).Agent, "antigravity", "child_status");
        Same(DelegationChildInfo.Of(gemini with { Provider = null }).Agent, "claude", "a Claude child");
        var made = DelegationMcp.Result(DelegationResponse.Delegated(gemini));
        Same(made["structuredContent"]!["child"]!.ToJsonString(), """{"id":"c1","state":"running","agent":"antigravity","branch":"mighty/c1","mode":"acceptEdits"}""", "the child is tool data with its agent");
        Same(TextOf(made), "Child c1 is running, runs antigravity, on the branch mighty/c1, starting in acceptEdits mode.", "the child's text names its agent");
        var listed = DelegationMcp.Result(new(Children: [DelegationChildSummary.Of(gemini)]));
        Same(TextOf(listed), "Your children:\n- c1: antigravity, running, report revision 0", "the list names each agent");
        Same(listed["structuredContent"]!["children"]![0]!.ToJsonString(), """{"id":"c1","agent":"antigravity","state":"running","revision":0}""", "the list is tool data with each agent");
        var status = DelegationMcp.Result(new(Status: DelegationChildStatus.Of(gemini, null)));
        Same(TextOf(status), "Child c1 runs antigravity and is running. It has not reported yet.", "the status names the agent");
        Same(status["structuredContent"]!["status"]!["agent"]!.GetValue<string>(), "antigravity", "the status is tool data with its agent");
        Check(JsonSerializer.Serialize(DelegationChildInfo.Of(gemini), Wire.Json).Contains("\"agent\":\"antigravity\"", StringComparison.Ordinal), "the pane pipe carries the agent");
        return Task.CompletedTask;
    }
```

In `Verification.cs`, after the Task 3 line, add:

```csharp
        await Test("delegation: delegate takes an optional agent, and every answer names the child's agent", DelegationCrossAgentVerification.TheToolTakesAnOptionalAgentAndNamesItEverywhere);
```

Update the existing checks:

1. `DelegationVerification.cs` line 127: replace `"workspace_has_children", "held_not_removable", "human_started", "merge_human_only"], "the reason codes are the ontology list in order");` with:

```csharp
            "workspace_has_children", "held_not_removable", "human_started", "merge_human_only", "agent_unknown", "agent_unavailable"], "the reason codes are the ontology list in order");
```

2. `DelegationVerification.cs` line 423: replace `["delegate"] = ["task", "mode"]` with `["delegate"] = ["task", "mode", "agent"]`.

3. `DelegationVerification.cs` lines 425-426: replace the two `Delegate.Description` lines with:

```csharp
        Check(DelegationToolManifest.Delegate.Description.StartsWith("Start a child pane that works on a task in its own git worktree, on the flat branch mighty/<child id> made from your current branch head. It runs your own agent CLI unless agent names another: claude, codex or antigravity, each only if installed and signed in on this PC.", StringComparison.Ordinal)
            && DelegationToolManifest.Delegate.Description.EndsWith("At most 3 open children per pane, of all agents together, and a child cannot delegate. The same call twice in one of your runs returns the same child.", StringComparison.Ordinal)
```

4. `DelegationCoordinatorVerification.cs` lines 95-96: replace with:

```csharp
            Check(result["isError"] is null && Data(result, "child")!.ToJsonString() == $$"""{"id":"{{info.Id}}","state":"creating","agent":"claude","branch":"{{info.Branch}}","mode":"acceptEdits"}"""
                && TextOf(result) == $"Child {info.Id} is creating, runs claude, on the branch {info.Branch}, starting in acceptEdits mode.", "the child is tool data");
```

5. Line 164: replace `"delegate takes exactly these string arguments: task, mode."` with `"delegate takes exactly these string arguments: task, mode, and optionally agent."`.

6. Line 482: replace the expected list text with:

```csharp
                && TextOf(listed) == "Your children:\n- running: claude, running, report revision 0\n- reported: claude, reported, report revision 2\n- failed: claude, failed, report revision 0\n- closed: claude, closed, report revision 3\n- discarded: claude, discarded, report revision 0", "the list is tool data");
```

7. Line 492: replace `$"Child reported is reported. Its report revision 2` with `$"Child reported runs claude and is reported. Its report revision 2`.

8. Line 496: replace `"Child running is running. It has not reported yet."` with `"Child running runs claude and is running. It has not reported yet."`.

9. Line 499: replace `$"Child closed is closed. Its report revision 3` with `$"Child closed runs claude and is closed. Its report revision 3`.

10. Line 546: replace `.SequenceEqual(manifest.Arguments.Select(argument => argument.Name))` (the one inside `schema["required"]`) with `.SequenceEqual(manifest.Arguments.Where(argument => argument.Required).Select(argument => argument.Name))`.

11. Line 548: replace `manifest.Name + " lists its string arguments, all required"` with `manifest.Name + " lists its string arguments, each required but delegate's agent"`.

- [ ] **Step 2: Run it and expect it to fail**

Run CORE-RUN.
Expected: the build fails with:
- `CS0117` for `AgentUnknown`, `AgentUnavailable` and `Unavailable`;
- `CS1061` for `Agent` on `DelegationChildInfo`, `DelegationChildSummary` and `DelegationChildStatus`, and for `Required` on `Argument`;
- `CS1501` for `Refusal` with 2 arguments and `Make` with 5.

- [ ] **Step 3: Implement**

`Delegation.cs`, the enum: after `MergeHumanOnly,`, add:

```csharp
    /// <summary>delegate's <c>agent</c> names no agent a child can run.</summary>
    AgentUnknown,
    /// <summary>The agent delegate named is not installed or not signed in on this PC, as the app last learnt it.</summary>
    AgentUnavailable,
```

`Delegation.cs`, `DelegationResponse`:
- Change the `Error` param doc to `/// <param name="Error">The call reached no delegation tool, or the tool could not take it as given. With Refused, what the refusal is about (the agent it names), shown after its code.</param>`.
- After `Refusal(DelegationReasonCode reason)`, add:

```csharp
    public static DelegationResponse Refusal(DelegationReasonCode reason, string detail) => new(Refused: reason, Error: detail);
```

`Delegation.cs`, replace lines 178-182 with:

```csharp
/// <summary>One child as its parent's tools show it (M/Delegation.swift DelegationChildInfo). The branch is always <c>mighty/&lt;id&gt;</c>.</summary>
/// <param name="Agent">The agent the child runs, as delegate's <c>agent</c> names it.</param>
public sealed record DelegationChildInfo(string Id, ChildState State, string Branch, string StartingMode, string Agent = "claude")
{
    public static DelegationChildInfo Of(ChildRecord child) => new(child.Id, child.State, child.Branch, child.StartingMode, DelegationProviders.AgentName(child.Agent));
}
```

`DelegationCoordinator.cs`:
- Change the manifest summary sentence `Every argument is a required string.` to `Every argument is a string, and every one is required but delegate's agent.`.
- Replace lines 12-14 with:

```csharp
    public sealed record Argument(string Name, string Description, bool Required = true);
    /// <param name="Arguments">All strings, no others.</param>
    public sealed record Tool(string Name, string Description, IReadOnlyList<Argument> Arguments);
```

- Replace `Delegate`, `ListChildren` and `ChildStatus` (lines 18-24) with:

```csharp
    public static readonly Tool Delegate = new("delegate",
        "Start a child pane that works on a task in its own git worktree, on the flat branch mighty/<child id> made from your current branch head. It runs your own agent CLI unless agent names another: claude, codex or antigravity, each only if installed and signed in on this PC. Use it only when the user asks for parallel or delegated work, or when the task clearly splits into independent parts that can run at the same time; never fan out speculatively, and do the work yourself otherwise. The child starts in the permission mode you ask for, which must be one of the child agent's modes and one in which it can change files. For your own agent it must be no wider than your own mode (Claude: plan < manual < acceptEdits < auto < fullAccess; Codex: manual < acceptEdits < onRequest < fullAccess). For another agent, the child's mode may do nothing yours may not: edit files without asking only if yours does (Claude acceptEdits and auto, Codex onRequest and acceptEdits, Antigravity acceptEdits, any fullAccess), edit after asking only if yours edits at all, run shell commands without asking only if yours does (Claude auto, Codex manual and acceptEdits, any fullAccess), and have full access only from your fullAccess. So from Claude acceptEdits a Codex child gets onRequest, not acceptEdits (which runs commands without asking), and from Claude plan or manual no Codex or Antigravity child can start. Answers as soon as the child exists; its run goes on in the background. The child writes its result to REPORT.md, and you get one notice per report. At most 3 open children per pane, of all agents together, and a child cannot delegate. The same call twice in one of your runs returns the same child.",
        [new("task", "What the child should do. It is saved as the child's TASK.md."),
         new("mode", "The child's starting permission mode, one of the child agent's in which it can change files, no wider than yours: plan, manual, acceptEdits, auto or fullAccess for a Claude child; acceptEdits, onRequest or fullAccess for a Codex child; acceptEdits or fullAccess for an Antigravity child."),
         new("agent", "Optional: the agent CLI the child runs, claude, codex or antigravity. Leave it out for your own agent.", Required: false)]);

    public static readonly Tool ListChildren = new("list_children", "List your children with their id, agent, state and current report revision.", []);

    public static readonly Tool ChildStatus = new("child_status", "Read one of your children: its agent, its state, its head and the body of its latest REPORT.md.", [Child]);
```

- Replace `ArgumentProblem` (lines 258-264) with:

```csharp
    /// <summary>Why <paramref name="arguments"/> are not <paramref name="tool"/>'s (every required one, and no other), or null when they are.</summary>
    public static string? ArgumentProblem(IReadOnlyDictionary<string, string> arguments, DelegationToolManifest.Tool tool)
    {
        var names = tool.Arguments.Select(argument => argument.Name).ToList();
        var required = tool.Arguments.Where(argument => argument.Required).Select(argument => argument.Name).ToList();
        if (arguments.Keys.All(names.Contains) && required.All(arguments.ContainsKey)) return null;
        if (names.Count == 0) return $"{tool.Name} takes no arguments.";
        var optional = tool.Arguments.Where(argument => !argument.Required).Select(argument => argument.Name).ToList();
        return $"{tool.Name} takes exactly these string arguments: {string.Join(", ", required)}" + (optional.Count == 0 ? "." : $", and optionally {string.Join(", ", optional)}.");
    }
```

`DelegationStatusTools.cs`, replace lines 3-23 with:

```csharp
/// <summary>One child as list_children shows it (M/DelegationStatusTools.swift DelegationChildSummary).</summary>
/// <param name="Revision">The current report revision: 0 until the first report.</param>
/// <param name="Agent">The agent the child runs, as delegate's <c>agent</c> names it.</param>
public sealed record DelegationChildSummary(string Id, ChildState State, int Revision, string Agent = "claude")
{
    public static DelegationChildSummary Of(ChildRecord child) => new(child.Id, child.State, child.ReportRevision, DelegationProviders.AgentName(child.Agent));
}

/// <summary>One child as child_status shows it: where it stands and what it last reported (M/DelegationStatusTools.swift DelegationChildStatus).</summary>
/// <param name="Revision">The current report revision: 0 until the first report.</param>
/// <param name="Head">The head the current report was made at, which merge takes as its expected_head. null until the first report.</param>
/// <param name="Report">The stored copy of the current revision's REPORT.md, cut at 64 KiB with a visible marker. null until the first report, or once pruning dropped it.</param>
/// <param name="ReportTruncated">The report is longer than its copy; the whole file stays in the child's worktree until cleanup.</param>
/// <param name="Agent">The agent the child runs, as delegate's <c>agent</c> names it.</param>
public sealed record DelegationChildStatus(string Id, ChildState State, int Revision, string? Head, string? Report, bool ReportTruncated, string Agent = "claude")
{
    /// <summary><paramref name="child"/> with <paramref name="report"/>, its stored REPORT.md copy, when that copy holds the current revision.</summary>
    public static DelegationChildStatus Of(ChildRecord child, DelegationCopy? report)
    {
        var current = report is not null && report.ChildId == child.Id && report.Kind == DelegationCopyKind.Report && report.Revision == child.ReportRevision && child.ReportRevision > 0 ? report : null;
        return new(child.Id, child.State, child.ReportRevision, child.ReportHead, current?.Text, current?.Truncated ?? false, DelegationProviders.AgentName(child.Agent));
    }
}
```

In the same file, change the `ListChildren` summary from `…each with its id, state and current report revision.` to `…each with its id, agent, state and current report revision.`.

`DelegationMcp.cs`:
- In `Definition` (line 107), replace `["required"] = new JsonArray([.. tool.Arguments.Select(argument => (JsonNode)argument.Name)])` with `["required"] = new JsonArray([.. tool.Arguments.Where(argument => argument.Required).Select(argument => (JsonNode)argument.Name)])`.
- In `Hint`, before `_ => "",`, add:

```csharp
        DelegationReasonCode.AgentUnknown => " (agent must be claude, codex or antigravity, or left out for your own)",
```

- In `Result`, replace the refusal, child, children and status branches (lines 132-152) with:

```csharp
        if (response.Refused is { } reason)
        {
            var code = DelegationNames.Of(reason);
            var about = response.Error is { } detail ? $" ({detail})" : Hint(reason);
            return new() { ["content"] = Content($"Refused: {code}{about}. Nothing was changed."), ["structuredContent"] = new JsonObject { ["refused"] = code }, ["isError"] = true };
        }
        if (response.Child is { } child)
            return Answer($"Child {child.Id} is {DelegationNames.Of(child.State)}, runs {child.Agent}, on the branch {child.Branch}, starting in {child.StartingMode} mode.", "child",
                new JsonObject { ["id"] = child.Id, ["state"] = DelegationNames.Of(child.State), ["agent"] = child.Agent, ["branch"] = child.Branch, ["mode"] = child.StartingMode });
        if (response.Children is { } children)
        {
            var lines = children.Select(listed => $"- {listed.Id}: {listed.Agent}, {DelegationNames.Of(listed.State)}, report revision {listed.Revision}");
            return Answer(children.Count == 0 ? "You have no children." : string.Join("\n", lines.Prepend("Your children:")), "children",
                new JsonArray([.. children.Select(listed => new JsonObject { ["id"] = listed.Id, ["agent"] = listed.Agent, ["state"] = DelegationNames.Of(listed.State), ["revision"] = listed.Revision })]));
        }
        if (response.Status is { } status)
        {
            var text = $"Child {status.Id} runs {status.Agent} and is {DelegationNames.Of(status.State)}.";
            if (status.Revision == 0) text += " It has not reported yet.";
            else text += $" Its report revision {status.Revision} was made at head {status.Head ?? "unknown"}." + (status.Report is { } report ? $"\n\nREPORT.md:\n{report}" : " Its REPORT.md copy is no longer kept.");
            return Answer(text, "status", new JsonObject { ["id"] = status.Id, ["agent"] = status.Agent, ["state"] = DelegationNames.Of(status.State), ["revision"] = status.Revision, ["head"] = status.Head, ["report"] = status.Report, ["reportTruncated"] = status.ReportTruncated });
        }
```

- Update the `Result` summary to say `A refusal names its one reason code in the text and as structured content, with what it is about when the app said, or else a word on what to do for a code whose name does not say it; …`.

`DelegationModel.cs`, replace `DelegationRequestKey` with its summary (lines 144-161) with:

```csharp
/// <summary>
/// The idempotency key of one delegate call (M/DelegationModel.swift DelegationRequestKey): from the calling pane's session id,
/// its current run id, the task text and the starting mode, and the child's agent when it is not the caller's own. Each part is
/// length-prefixed before hashing, so no two different part lists share a key; a child of the caller's own agent keeps the key
/// it always had.
/// </summary>
public static class DelegationRequestKey
{
    public static string Make(string parentSessionId, string parentRunId, string task, string startingMode, string? otherAgent = null)
    {
        using var bytes = new MemoryStream(); Span<byte> length = stackalloc byte[8];
        string[] parts = otherAgent is null ? [parentSessionId, parentRunId, task, startingMode] : [parentSessionId, parentRunId, task, startingMode, otherAgent];
        foreach (var part in parts)
        {
            var utf8 = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)utf8.Length); bytes.Write(length); bytes.Write(utf8);
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
}
```

`DelegationDelegateTool.cs`: add the static `Unavailable` used by the test, after `IsNoWider` (line 43):

```csharp
    /// <summary>Why the <paramref name="provider"/> agent cannot run a child, naming it, or null when it can.</summary>
    public static string? Unavailable(string provider, DelegationAgentAvailability availability) => availability switch
    {
        DelegationAgentAvailability.NotInstalled => $"{ProviderMark.Label(provider)} is not installed on this PC; the user can check it in Mighty Claude's Settings › CLI",
        DelegationAgentAvailability.SignedOut => $"{ProviderMark.Label(provider)} is not signed in on this PC; the user can check it in Mighty Claude's Settings › CLI",
        _ => null,
    };
```

- [ ] **Step 4: Run it and expect it to pass**

Run CORE-RUN.
Expected: `PASS delegation: delegate takes an optional agent, and every answer names the child's agent`, the updated older delegation checks still PASS, and the 9 baseline FAILs only.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/Delegation.cs native/windows/MightyClaude.Core/DelegationCoordinator.cs native/windows/MightyClaude.Core/DelegationStatusTools.cs native/windows/MightyClaude.Core/DelegationMcp.cs native/windows/MightyClaude.Core/DelegationModel.cs native/windows/MightyClaude.Core/DelegationDelegateTool.cs native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs native/windows/MightyClaude.Core.Tests/DelegationVerification.cs native/windows/MightyClaude.Core.Tests/DelegationCoordinatorVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the delegation tools speak of agents as the Mac's do: delegate lists an optional agent, reason codes agent_unknown and agent_unavailable carry what they are about, the request key adds another agent's name, and delegate's child, list_children and child_status name each child's agent" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 5: A child's words and pane follow its agent

**Files:**
- Modify: `native/windows/MightyClaude.Core/DelegationDelegateTool.cs`, lines 150-184 (`FirstInput`, `HumanFirstRun`, `HumanNote`).
- Modify: `native/windows/MightyClaude.Core/DelegationAppHost.cs`:
  - lines 11-28 (`ChildSession`);
  - line 42 (inside `AddingChild`).
- Modify: `native/windows/MightyClaude.Core/DelegationCoordinator.cs`, line 47 (the `StartedByHuman` and `Provider` param docs of `DelegationChildPane`).
- Modify: `native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs`, `Verification.cs`.

**Interfaces:**
- Consumes: `SessionTemplate.Find(RunSession target, IEnumerable<RunSession> sessions) -> RunSession?`.
- Produces (C#):
  - `public static string DelegationCoordinator.FirstInput(string task, ChildWorktree worktree, string provider = "claude", string? parentAgent = null)`
  - `public static HumanChildFirstRun DelegationCoordinator.HumanFirstRun(string text, ChildWorktree worktree, string provider = "claude", string? parentAgent = null)`
  - `public static string DelegationCoordinator.HumanNote(ChildWorktree worktree, string provider = "claude", string? parentAgent = null)`
  - `public static RunSession DelegationPanes.ChildSession(DelegationChildPane pane, string workspaceId, string title, RunSession? parent = null, RunSession? agentDefaults = null)`
- Mirrors (Swift): `firstInput(task:worktree:provider:parentAgent:)`, `humanFirstRun(text:worktree:provider:parentAgent:)`, `humanNote(worktree:provider:parentAgent:)`, `DelegationPanes.childSession(_:workspaceId:title:parent:agentDefaults:)`, and `AppStore.createPane`'s `RunSession.template`.

- [ ] **Step 1: Write the failing test**

Append to `DelegationCrossAgentVerification`:

```csharp
    internal static Task AChildsWordsFollowItsAgent()
    {
        var worktree = new ChildWorktree("c1", @"C:\w\c1", @"C:\w\c1", "mighty/c1", "main", Commit);
        var codexOfClaude = DelegationCoordinator.FirstInput("Do it.", worktree, "codex", "claude");
        Check(codexOfClaude.StartsWith("Another Claude pane", StringComparison.Ordinal) && codexOfClaude.Contains("Do not commit", StringComparison.Ordinal), "a Codex child of a Claude pane");
        var claudeOfCodex = DelegationCoordinator.FirstInput("Do it.", worktree, "claude", "codex");
        Check(claudeOfCodex.StartsWith("Another pane", StringComparison.Ordinal) && claudeOfCodex.Contains("commit your work", StringComparison.Ordinal) && !claudeOfCodex.Contains("Do not commit", StringComparison.Ordinal), "a Claude child of a Codex pane");
        // The parent's own agent says what it always said.
        Same(DelegationCoordinator.FirstInput("Do it.", worktree, "codex", "codex"), DelegationCoordinator.FirstInput("Do it.", worktree, "codex"), "a Codex child of a Codex pane");
        Same(DelegationCoordinator.FirstInput("Do it.", worktree, "claude", "claude"), DelegationCoordinator.FirstInput("Do it.", worktree), "a Claude child of a Claude pane");
        var note = DelegationCoordinator.HumanNote(worktree, "gemini", "claude");
        Check(note.Contains("beside another Claude pane", StringComparison.Ordinal) && note.Contains("Do not commit", StringComparison.Ordinal), "the note to an Antigravity child of a Claude pane");
        Check(DelegationCoordinator.HumanNote(worktree, "claude", "codex").Contains("beside another pane so", StringComparison.Ordinal), "the note to a Claude child of a Codex pane");
        Same(DelegationCoordinator.HumanFirstRun("Fix it.", worktree, "gemini", "claude").Input, "Fix it.\n\n" + note, "the human's first run carries that note");
        return Task.CompletedTask;
    }

    internal static Task AChildOfAnotherAgentTakesThatAgentsDefaults()
    {
        var parent = new RunSession { Id = "parent", WorkspaceId = "ws", Kind = "claude", Provider = "claude", Model = "opus", Settings = new(Effort: "high", PermissionMode: "auto"), AgentViewMode = "mighty", GraphViewMode = "timeline", MightyStyle = "superpowers", MightyStyleHash = "h1" };
        var codexPane = new RunSession { Id = "codex-pane", WorkspaceId = "ws", Kind = "claude", Provider = "codex", Model = "gpt-5.6-sol", Settings = new(Effort: "low", PermissionMode: "fullAccess") };
        foreach (var human in new[] { true, false })
        {
            var pane = new DelegationChildPane("c1", "parent", "onRequest", "/w/c1", StartedByHuman: human, Provider: "codex");
            var child = DelegationPanes.ChildSession(pane, "ws", "Fix it", parent, codexPane);
            Check(child.Provider == "codex" && child.Settings.PermissionMode == "onRequest", $"human {human}: a Codex child in its starting mode");
            // The human's child takes what a new Codex pane would; one the parent's delegate made, plain defaults.
            Check(human ? child.Model == "gpt-5.6-sol" && child.Settings.Effort == "low" : child.Model == "default" && child.Settings.Effort == "default", $"human {human}: model and effort");
            Check(child is { AgentViewMode: "mighty", GraphViewMode: "timeline", MightyStyle: null, MightyStyleHash: null }, $"human {human}: the parent's view, no style");
            // With no pane of the agent yet, a new pane's defaults.
            var fresh = DelegationPanes.ChildSession(pane, "ws", "Fix it", parent);
            Check(fresh.Model == "default" && fresh.Settings.Effort == "default", $"human {human}: no pane of the agent yet");
        }
        // A Claude child of a Codex parent the human started: Claude's last-used model and effort, not the Codex model.
        var claude = DelegationPanes.ChildSession(new("c2", "codex-pane", "fullAccess", "/w/c2", StartedByHuman: true), "ws", "t", codexPane, parent);
        Check(claude is { Provider: "claude", Model: "opus", MightyStyle: null } && claude.Settings.Effort == "high", "a Claude child of a Codex pane");
        // The app finds that agent's last-used top-level pane itself.
        var state = new AppSnapshot { Workspaces = [new Workspace { Id = "ws", Path = Path.GetTempPath() }], Sessions = [parent, codexPane], ActiveWorkspaceId = "ws", ActiveSessionId = "parent" };
        var next = DelegationPanes.AddingChild(state, new("c3", "parent", "acceptEdits", Path.GetTempPath(), StartedByHuman: true, Provider: "codex"), "Try it") ?? throw new InvalidOperationException("no child pane");
        Check(next.Sessions.Single(session => session.Id == "c3") is { Provider: "codex", Model: "gpt-5.6-sol" } made && made.Settings is { Effort: "low", PermissionMode: "acceptEdits" }, "the Codex template's model and effort, its own mode");
        return Task.CompletedTask;
    }
```

In `Verification.cs`, after the Task 4 line, add:

```csharp
        await Test("delegation: a child's words follow its agent and name a Claude parent as one", DelegationCrossAgentVerification.AChildsWordsFollowItsAgent);
        await Test("delegation: the human's child of another agent takes that agent's last-used model and effort", DelegationCrossAgentVerification.AChildOfAnotherAgentTakesThatAgentsDefaults);
```

- [ ] **Step 2: Run it and expect it to fail**

Run CORE-RUN.
Expected: the build fails with `CS1501: No overload for method 'FirstInput' takes 4 arguments` (likewise `HumanNote`, `HumanFirstRun`), and `CS1501` for `ChildSession` with 5 arguments.

- [ ] **Step 3: Implement**

`DelegationDelegateTool.cs`, replace `FirstInput` and its summary:

```csharp
    /// <summary>
    /// The first message of a child's first run: its task and how it reports (M/DelegationDelegateTool.swift firstInput). A
    /// Claude child commits its work itself; a child of any other agent leaves it for the app's commit (<see cref="ChildAutoCommit"/>).
    /// It names a Claude parent as one.
    /// </summary>
    public static string FirstInput(string task, ChildWorktree worktree, string provider = "claude", string? parentAgent = null)
    {
        var from = (parentAgent ?? provider) == "claude" ? "Another Claude pane" : "Another pane";
```

The rest of its body is unchanged.

Replace `HumanFirstRun` and `HumanNote` with:

```csharp
    public static HumanChildFirstRun HumanFirstRun(string text, ChildWorktree worktree, string provider = "claude", string? parentAgent = null)
    {
        var note = HumanNote(worktree, provider, parentAgent);
        return text.StartsWith('/') ? new(text, note) : new(text + "\n\n" + note, null);
    }

    /// <summary>The app's note to a child the human started. A Claude child commits its work itself; a child of any other agent leaves it for the app. It names a Claude parent as one.</summary>
    public static string HumanNote(ChildWorktree worktree, string provider = "claude", string? parentAgent = null)
    {
        var beside = (parentAgent ?? provider) == "claude" ? "another Claude pane" : "another pane";
        return provider == "claude"
            ? $"[Mighty Claude] This request runs in its own git worktree on the branch {worktree.Branch}, opened beside {beside} so that pane keeps working undisturbed. When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write a short report to {worktree.ReportFile}: what you did and what is left. The person who sent this merges your branch from the app. Do not merge, push or switch branches."
            : $"[Mighty Claude] This request runs in its own git worktree on the branch {worktree.Branch}, opened beside {beside} so that pane keeps working undisturbed. When you are done, write a short report to {worktree.ReportFile}: what you did and what is left. {AppCommitNote(worktree)} The person who sent this merges your branch from the app. Do not merge, push or switch branches.";
    }
```

Keep the existing summary above `HumanFirstRun`.

`DelegationAppHost.cs`, replace `ChildSession` and its summary (lines 11-28) with:

```csharp
    /// <summary>
    /// A new agent pane for <paramref name="pane"/> in the workspace <paramref name="workspaceId"/>, running <c>pane.Provider</c>
    /// (the child's agent). Its mode is the starting mode the parent asked for, written here once and never copied from the
    /// last-used pane; it is linked to its parent and works in its worktree folder. Every child of an agent pane
    /// <paramref name="parent"/> shows its requests the way the parent does: basic or Mighty, and in Mighty the diagram, timeline or
    /// pipeline. A child of the parent's own agent that the human started also takes the parent's model and effort, and a Claude
    /// one its Mighty style. A child of another agent that the human started runs the model and effort a new pane of that agent
    /// would: those of <paramref name="agentDefaults"/>, the agent's last-used pane, or its defaults without one. Any child the
    /// parent's delegate made runs its agent's defaults (M/DelegationAppHost.swift childSession).
    /// </summary>
    public static RunSession ChildSession(DelegationChildPane pane, string workspaceId, string title, RunSession? parent = null, RunSession? agentDefaults = null)
    {
        var session = new RunSession { Id = pane.SessionId, WorkspaceId = workspaceId, Title = title, Kind = "claude", Provider = pane.Provider, Settings = new(PermissionMode: pane.Mode), ParentSessionId = pane.ParentSessionId, WorkingFolder = pane.Folder };
        if (parent is null || !DelegationProviders.IsAgentPane(parent.Kind, parent.Provider)) return session;
        session = session with { AgentViewMode = parent.AgentViewMode, GraphViewMode = parent.GraphViewMode };
        if (!pane.StartedByHuman) return session;
        if (parent.Provider != pane.Provider)
            return agentDefaults is { } defaults && defaults.Provider == pane.Provider ? session with { Model = defaults.Model, Settings = session.Settings with { Effort = defaults.Settings.Effort } } : session;
        session = session with { Model = parent.Model, Settings = session.Settings with { Effort = parent.Settings.Effort } };
        // The same approved bytes; its state starts over at the child's own first request.
        return pane.Provider == "claude" ? session with { MightyStyle = parent.MightyStyle, MightyStyleHash = parent.MightyStyleHash } : session;
    }
```

`DelegationAppHost.cs`, in `AddingChild`, replace line 42 (`var child = ChildSession(pane, parent.WorkspaceId, …, parent) with { TitleMode = PaneTitle.Fixed };`) with:

```csharp
        // A child of another agent takes what a new pane of that agent would: its last-used top-level pane's model and effort.
        var agentDefaults = parent.Provider == pane.Provider ? null : SessionTemplate.Find(new RunSession { Id = pane.SessionId, WorkspaceId = parent.WorkspaceId, Kind = "claude", Provider = pane.Provider }, state.Sessions);
        var child = ChildSession(pane, parent.WorkspaceId, PaneTitle.Shortened(task) ?? ProviderCatalog.Name(pane.Provider), parent, agentDefaults) with { TitleMode = PaneTitle.Fixed };
```

`DelegationCoordinator.cs`, the `DelegationChildPane` param docs (lines 46-48): replace them with:

```csharp
/// <param name="StartedByHuman">The human started it from the parent's composer: a child of the parent's agent takes the parent's model and effort, one of another agent that agent's last-used pane's, and the app shows it at once.</param>
/// <param name="Provider">The agent CLI the child runs: its parent's, or the one the parent's delegate call or the human's menu chose.</param>
```

- [ ] **Step 4: Run it and expect it to pass**

Run CORE-RUN.
Expected:
- `PASS delegation: a child's words follow its agent and name a Claude parent as one`
- `PASS delegation: the human's child of another agent takes that agent's last-used model and effort`
- `PASS delegation: records and panes carry the human's mark and the parent's agent` (unchanged behaviour for same-agent children);
- the 9 baseline FAILs only.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/DelegationDelegateTool.cs native/windows/MightyClaude.Core/DelegationAppHost.cs native/windows/MightyClaude.Core/DelegationCoordinator.cs native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a child of another agent is told it works beside a Claude pane only when its parent is one, opens in its parent's view, and when the human started it takes the model and effort of that agent's last-used pane, as the Mac's" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 6: `delegate` runs a child of the agent it names

**Files:**
- Modify: `native/windows/MightyClaude.Core/DelegationCoordinator.cs`:
  - inside `IDelegationHost`, after its `Note` member (line 90): `AgentAvailability`;
  - line 246 (`Perform`): pass `agent`.
- Modify: `native/windows/MightyClaude.Core/DelegationDelegateTool.cs`:
  - lines 45-95 (`DelegateNow` and its summary);
  - lines 97-118 (`StartChild` and its summary);
  - line 211, line 221 (the `StartHumanChild` calls of `DelegateRefusal`);
  - line 234 (the `StartHumanChild` call of `StartChild`);
  - lines 241-253 (`DelegateRefusal`).
- Modify: `native/windows/MightyClaude.Core.Tests/DelegationDeliveryVerification.cs`, `DelegationTestHost` (lines 10-121).
- Modify: `native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs`, `Verification.cs`.

**Interfaces:**
- Consumes:
  - `DelegationProviders.Agent`, `DelegationProviders.IsNoWider(mode, child, stored, parent)`
  - `DelegationCoordinator.Unavailable`
  - `DelegationRequestKey.Make(…, otherAgent)`
  - `DelegationResponse.Refusal(reason, detail)`
- Produces (C#):
  - `Task<DelegationAgentAvailability> IDelegationHost.AgentAvailability(string provider) => Task.FromResult(DelegationAgentAvailability.Ready);` (a default interface member)
  - `private async Task<DelegationResponse> DelegationCoordinator.DelegateNow(string task, string mode, string? agent, DelegationCaller caller)`
  - `private async Task DelegationCoordinator.StartChild(string id, ChildWorktreeBase start, string task, string parentAgent, HumanChildStarter? human = null)`
  - `private DelegationReasonCode? DelegationCoordinator.DelegateRefusal(string parentId, DelegationPaneState parent, string mode, string child, bool byHuman = false)`
  - Test host: `DelegationTestHost.SetAgent(string provider, DelegationAgentAvailability availability)`, `DelegationTestHost.AvailabilityAsked`
- Mirrors (Swift): `DelegationHost.agentAvailability(_:)`, `delegate(task:mode:agent:caller:)`, `startChild(_:base:task:parentAgent:human:)`, `refusal(of:_:mode:child:byHuman:)`.

- [ ] **Step 1: Write the failing test**

In `DelegationDeliveryVerification.cs`, in `DelegationTestHost`:
- after the `OnFirstRun` field (line 31), add:

```csharp
    private readonly Dictionary<string, DelegationAgentAvailability> agents = [];
    private readonly List<string> asked = [];
    /// <summary>Each agent the coordinator asked about, in order.</summary>
    internal List<string> AvailabilityAsked { get { lock (gate) return [.. asked]; } }
    /// <summary>What the host last learnt of <paramref name="provider"/>'s CLI; anything not set is ready.</summary>
    internal void SetAgent(string provider, DelegationAgentAvailability availability) { lock (gate) agents[provider] = availability; }
```

- after `Note` (the method ending near line 90), add:

```csharp
    public Task<DelegationAgentAvailability> AgentAvailability(string provider)
    {
        lock (gate) { asked.Add(provider); return Task.FromResult(agents.GetValueOrDefault(provider, DelegationAgentAvailability.Ready)); }
    }
```

Append to `DelegationCrossAgentVerification`:

```csharp
    /// <summary>A temp repository whose pane "parent", running <paramref name="Provider"/> in <paramref name="Mode"/>, calls the tools from a run started in that mode.</summary>
    private sealed record CrossPlace(DelegationPlace Files, DelegationTestHost Host, DelegationCoordinator Coordinator, string Provider, string Mode)
    {
        internal byte[] Bytes() => File.ReadAllBytes(Files.Store.FilePath);
        internal Task<DelegationResponse> Call(string tool, params (string Name, string Value)[] arguments) =>
            Coordinator.Handle(Request(tool, arguments), new DelegationCaller("parent", "claude", Provider, Delegation: true, PermissionMode: Mode));
        /// <summary>The human's send of <paramref name="text"/> from the parent's worktree menu, naming <paramref name="agent"/>; null is Ctrl+Enter.</summary>
        internal Task<HumanChildStart> Send(string text, string? agent, bool planFirst = false) =>
            Coordinator.StartHumanChild(text, "parent", new((childId, _) => Task.FromResult<string?>("send-run-of-" + childId), _ => Task.CompletedTask), planFirst, agent);
        internal Task<bool> WaitFor(string id, ChildState state) => Eventually(async () => Coordinator.File.Children.Find(child => child.Id == id)?.State == state && !await Coordinator.IsStarting(id));
        /// <summary>A refused call made nothing: the same file, no worktree, no pane or run.</summary>
        internal void NothingMade(byte[] before) =>
            Check(Bytes().SequenceEqual(before) && !Directory.Exists(Files.Root) && Host.CreatedPanes.Count == 0 && Host.StartedRuns.Count == 0, "the refused call made nothing");
        /// <summary>The child's run finished with its report written.</summary>
        internal async Task Report(ChildRecord child, string runId)
        {
            File.WriteAllText(ChildWorktree.ReportFileIn(child.WorktreePath), "# Report\n\nDone.\n");
            await Coordinator.ChildRunEnded(child.Id, runId, ChildRunEnd.Finished);
        }
        internal void Remove() => Files.Remove();
    }

    private static async Task<CrossPlace> Cross(string provider, string mode)
    {
        var files = new DelegationPlace();
        var store = new DelegationFileStore(files.Profile); store.Save(DelegationFile.Empty);
        var host = new DelegationTestHost(store) { FirstRun = id => "run-of-" + id, MakesPanes = true };
        Directory.CreateDirectory(files.Repo); await files.Repository(1);
        host.Set(new DelegationPaneState("parent", mode, files.Repo, Provider: provider, RunId: "run-1", Activity: DelegationPaneActivity.Running));
        return new(files, host, new(store, host, new ChildWorktreeMaker(files.Root, _ => 50_000_000_000), () => true), provider, mode);
    }

    private static ChildRecord Made(HumanChildStart answer) => answer is HumanChildStart.Started { Child: var child } ? child : throw new InvalidOperationException("No child was made: " + answer);

    internal static async Task AClaudeParentsCodexChildGetsTheAppsCommit()
    {
        var place = await Cross("claude", "auto");
        try
        {
            var start = await place.Files.Git("rev-parse", "HEAD");
            var info = (await place.Call("delegate", ("task", "Add a note."), ("mode", "onRequest"), ("agent", "codex"))).Child ?? throw new InvalidOperationException("delegate made no Codex child");
            Check(info is { Agent: "codex", StartingMode: "onRequest" } && await place.WaitFor(info.Id, ChildState.Running), "a Codex child in onRequest runs");
            var child = place.Coordinator.File.Children.Single();
            Check(child is { Provider: "codex", Agent: "codex", NotifiesParent: true }, "its record runs Codex and notifies its parent");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Provider), ["codex"], "its pane runs Codex");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Mode), ["onRequest"], "in onRequest");
            SameList(place.Host.AvailabilityAsked, ["codex"], "the host was asked about Codex once");
            var first = place.Host.StartedRuns.Single().Input;
            Check(first.StartsWith("Another Claude pane", StringComparison.Ordinal) && first.Contains("Do not commit", StringComparison.Ordinal), "a Codex child's first words, from a Claude pane");
            // The app commits what it left when its report changed.
            File.WriteAllText(Path.Combine(child.WorktreePath, "note.txt"), "Note\n");
            await place.Report(child, "run-of-" + child.Id);
            var head = await place.Files.GitIn(child.WorktreePath, "rev-parse", "HEAD");
            Check(head != start && await place.Files.GitIn(child.WorktreePath, "log", "-1", "--format=%s") == ChildAutoCommit.Message(child.Id, 1) && place.Coordinator.File.Children.Single().ReportHead == head, "the app committed the Codex child's work");
            // The parent's tools name its agent, and its notice goes to the parent like any other.
            SameList((await place.Call("list_children")).Children!.Select(listed => listed.Agent), ["codex"], "list_children");
            Same((await place.Call("child_status", ("child", child.Id))).Status?.Agent, "codex", "child_status");
            SameList(place.Coordinator.File.Notices.Select(notice => notice.ChildId), [child.Id], "one notice for the parent");
        }
        finally { place.Remove(); }
    }

    internal static async Task ACodexParentsClaudeChildCommitsItsOwnWork()
    {
        var place = await Cross("codex", "acceptEdits");
        try
        {
            var start = await place.Files.Git("rev-parse", "HEAD");
            var info = (await place.Call("delegate", ("task", "Add a note."), ("mode", "acceptEdits"), ("agent", "claude"))).Child ?? throw new InvalidOperationException("delegate made no Claude child");
            Check(info.Agent == "claude" && await place.WaitFor(info.Id, ChildState.Running), "a Claude child runs");
            var child = place.Coordinator.File.Children.Single();
            // A Claude child's record keeps no provider, as every Claude child's.
            Check(child is { Provider: null, Agent: "claude" }, "its record is a Claude child's");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Provider), ["claude"], "its pane runs Claude");
            var first = place.Host.StartedRuns.Single().Input;
            Check(first.StartsWith("Another pane", StringComparison.Ordinal) && first.Contains("commit your work", StringComparison.Ordinal) && !first.Contains("Do not commit", StringComparison.Ordinal), "a Claude child's first words, from a Codex pane");
            // What it leaves uncommitted stays so: the app never commits for a Claude child.
            File.WriteAllText(Path.Combine(child.WorktreePath, "note.txt"), "Note\n");
            await place.Report(child, "run-of-" + child.Id);
            Same(await place.Files.GitIn(child.WorktreePath, "rev-parse", "HEAD"), start, "no app commit");
            Same(place.Coordinator.File.Children.Single().ReportHead, start, "the report is at the start");
            // The Codex parent still may not merge by tool outside fullAccess.
            Same(await place.Call("merge", ("child", child.Id), ("expected_head", start)), DelegationResponse.Refusal(DelegationReasonCode.MergeHumanOnly), "merges follow the parent's agent");
        }
        finally { place.Remove(); }
    }

    internal static async Task TheWrongAgentOrModeIsRefusedAndNothingIsMade()
    {
        var place = await Cross("claude", "acceptEdits");
        try
        {
            place.Host.SetAgent("gemini", DelegationAgentAvailability.NotInstalled);
            place.Host.SetAgent("codex", DelegationAgentAvailability.SignedOut);
            var before = place.Bytes();
            var unknown = await place.Call("delegate", ("task", "Do it."), ("mode", "acceptEdits"), ("agent", "gpt"));
            Check(unknown.Refused == DelegationReasonCode.AgentUnknown && unknown.Error?.Contains("\"gpt\"", StringComparison.Ordinal) == true, "an unknown agent");
            Same(await place.Call("delegate", ("task", "Do it."), ("mode", "acceptEdits"), ("agent", "antigravity")),
                DelegationResponse.Refusal(DelegationReasonCode.AgentUnavailable, "Antigravity is not installed on this PC; the user can check it in Mighty Claude's Settings › CLI"), "an agent not installed");
            Same(await place.Call("delegate", ("task", "Do it."), ("mode", "onRequest"), ("agent", "codex")),
                DelegationResponse.Refusal(DelegationReasonCode.AgentUnavailable, "Codex is not signed in on this PC; the user can check it in Mighty Claude's Settings › CLI"), "an agent signed out");
            // A mode that is not the agent's, or one in which its child cannot write: an error.
            Same((await place.Call("delegate", ("task", "Do it."), ("mode", "auto"), ("agent", "codex"))).Error, "mode must be one of a Codex child's: manual, acceptEdits, onRequest, fullAccess.", "not a Codex mode");
            Check((await place.Call("delegate", ("task", "Do it."), ("mode", "manual"), ("agent", "codex"))).Error?.StartsWith("A Codex child in manual mode cannot change files", StringComparison.Ordinal) == true, "a read-only Codex child");
            Check((await place.Call("delegate", ("task", "Do it."), ("mode", "plan"), ("agent", "gemini"))).Error?.StartsWith("An Antigravity child in plan mode cannot change files", StringComparison.Ordinal) == true, "an Antigravity plan child");
            // Claude acceptEdits asks before Bash: a Codex child that runs commands unasked, or has full access, is wider.
            place.Host.SetAgent("codex", DelegationAgentAvailability.Ready);
            Same(await place.Call("delegate", ("task", "Do it."), ("mode", "acceptEdits"), ("agent", "codex")), DelegationResponse.Refusal(DelegationReasonCode.WiderMode), "Codex acceptEdits is wider");
            Same(await place.Call("delegate", ("task", "Do it."), ("mode", "fullAccess"), ("agent", "codex")), DelegationResponse.Refusal(DelegationReasonCode.WiderMode), "Codex fullAccess is wider");
            place.NothingMade(before);
            Check(!place.Host.AvailabilityAsked.Contains("claude"), "an agent the parent runs itself is never asked about");
        }
        finally { place.Remove(); }
    }

    internal static async Task TheCapCountsEveryAgent()
    {
        var place = await Cross("claude", "fullAccess");
        try
        {
            var own = (await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"))).Child ?? throw new InvalidOperationException("no own child");
            var codex = (await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"), ("agent", "codex"))).Child ?? throw new InvalidOperationException("no Codex child");
            Check(own.Id != codex.Id && own.Agent == "claude" && codex.Agent == "codex", "the same call for another agent is another child");
            // Naming the caller's own agent is leaving it out; repeats get the same children.
            Same((await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"), ("agent", "claude"))).Child?.Id, own.Id, "agent claude is the caller's own");
            Same((await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"), ("agent", " "))).Child?.Id, own.Id, "a blank agent is the caller's own");
            Same((await place.Call("delegate", ("task", "Same task."), ("mode", "acceptEdits"), ("agent", "CODEX"))).Child?.Id, codex.Id, "the repeat for Codex");
            var gemini = (await place.Call("delegate", ("task", "Same task."), ("mode", "fullAccess"), ("agent", "antigravity"))).Child ?? throw new InvalidOperationException("no Antigravity child");
            Check(gemini is { Agent: "antigravity", StartingMode: "fullAccess" }, "an Antigravity child in fullAccess");
            Same(await place.Call("delegate", ("task", "A fourth."), ("mode", "acceptEdits"), ("agent", "codex")), DelegationResponse.Refusal(DelegationReasonCode.WidthCap), "three open children of any agents");
            foreach (var info in new[] { own, codex, gemini }) Check(await place.WaitFor(info.Id, ChildState.Running), info.Agent + " runs");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Provider), ["claude", "codex", "gemini"], "each pane runs its agent");
        }
        finally { place.Remove(); }
    }
```

In `Verification.cs`, after the Task 5 lines, add:

```csharp
        await Test("delegation: a Claude parent's Codex child gets the worktree and the app's commit", DelegationCrossAgentVerification.AClaudeParentsCodexChildGetsTheAppsCommit);
        await Test("delegation: a Codex parent's Claude child commits its own work", DelegationCrossAgentVerification.ACodexParentsClaudeChildCommitsItsOwnWork);
        await Test("delegation: an unknown or unavailable agent, or a mode it cannot take, is refused and nothing is made", DelegationCrossAgentVerification.TheWrongAgentOrModeIsRefusedAndNothingIsMade);
        await Test("delegation: the cap counts every agent, and the same call for another agent is another child", DelegationCrossAgentVerification.TheCapCountsEveryAgent);
```

- [ ] **Step 2: Run it and expect it to fail**

Run CORE-RUN.
Expected: the build succeeds (the test host's `AgentAvailability` is an ordinary public method until the interface declares it).
- The four new checks print `FAIL delegation: …`. `delegate` ignores `agent`, so the Codex child runs Claude and the `Agent: "codex"` checks fail.
- The unknown-agent case answers a child instead of `agent_unknown`.

- [ ] **Step 3: Implement**

`DelegationCoordinator.cs`, inside `IDelegationHost` after `Note`, add:

```csharp
    /// <summary>
    /// Whether the <paramref name="provider"/> agent's CLI can run a child, as the app last learnt it, for a child of an agent
    /// other than its parent's. The app never asks a CLI for this. A host that knows nothing against any agent keeps this.
    /// </summary>
    Task<DelegationAgentAvailability> AgentAvailability(string provider) => Task.FromResult(DelegationAgentAvailability.Ready);
```

`DelegationCoordinator.cs`, line 246 (`Perform`): replace it with:

```csharp
        if (tool == DelegationToolManifest.Delegate) return DelegateNow(Value("task"), Value("mode"), arguments.GetValueOrDefault("agent"), caller);
```

`DelegationDelegateTool.cs`, replace the `DelegateNow` summary and body (lines 45-95) with:

```csharp
    /// <summary>
    /// delegate(task, mode, agent) from <paramref name="caller"/> (M/DelegationDelegateTool.swift delegate). The first rule that applies answers:
    /// 1. <c>agent_unknown</c>: <paramref name="agent"/> names no agent (claude, codex, antigravity or gemini, any case). Left out or
    ///    blank, the child runs the caller's own agent.
    /// 2. A mode that is not one of the child agent's, one in which that child could not change files (a manual Codex child, a plan
    ///    or manual Antigravity child), or an empty task, one with a NUL character or one over 64 KiB, is answered with an error.
    /// 3. The same call earlier in the caller's current run (the same request key) gets that same child, in whatever state it is now.
    /// 4. <c>child_cannot_delegate</c> (the caller has a parent link), <c>width_cap</c> (it has three open children, of any agents),
    ///    <c>wider_mode</c> (the mode is wider than the caller's stored mode now: by its agent's order, or for another agent by
    ///    <see cref="DelegationProviders.Capabilities"/>), <c>agent_unavailable</c> (another agent than the caller's whose CLI the
    ///    app last found not installed or signed out).
    /// 5. <c>not_git</c>, <c>detached_head</c>, <c>unborn_branch</c>, <c>low_disk</c>: the caller's folder cannot host a child worktree.
    /// 6. <c>store_full</c>: even after pruning, the delegation file has no room for the child's record and its report copy.
    /// Every refusal comes before anything is made and stores no key. Otherwise the child's record is saved in creating, with
    /// the call's request key and a copy of its TASK.md, and the answer names it while its start goes on in the background.
    /// </summary>
    private async Task<DelegationResponse> DelegateNow(string task, string mode, string? agent, DelegationCaller caller)
    {
        string childAgent;
        if (agent is { } named && !string.IsNullOrWhiteSpace(named))
        {
            if (DelegationProviders.Agent(named) is not { } found)
                return DelegationResponse.Refusal(DelegationReasonCode.AgentUnknown, $"\"{DelegationNames.Prefix(named, 40)}\" is not an agent; agent must be claude, codex or antigravity, or left out for your own");
            childAgent = found;
        }
        else childAgent = caller.Provider;
        var modes = DelegationProviders.StartingModes(childAgent); var label = ProviderMark.Label(childAgent);
        if (!modes.Contains(mode)) return DelegationResponse.Failure(childAgent == caller.Provider ? $"mode must be one of {string.Join(", ", modes)}." : $"mode must be one of a {label} child's: {string.Join(", ", modes)}.");
        if (!DelegationProviders.ChildCanWrite(childAgent, mode))
            return DelegationResponse.Failure($"{(label.StartsWith('A') ? "An" : "A")} {label} child in {mode} mode cannot change files, so it could not do the task or leave its work for a merge. Ask for one of {string.Join(", ", modes.Where(writing => DelegationProviders.ChildCanWrite(childAgent, writing)))}, no wider than your own mode.");
        if (string.IsNullOrWhiteSpace(task)) return DelegationResponse.Failure("delegate needs a task: what the child should do.");
        if (task.Contains('\0')) return DelegationResponse.Failure("A task may not contain a NUL character.");
        if (Encoding.UTF8.GetByteCount(task) > DelegationFileStore.MaximumCopyBytes) return DelegationResponse.Failure("A task may be at most 64 KiB.");
        var parentId = caller.PaneId;
        if (await Host(() => host.PaneState(parentId)) is not { } parent) return DelegationResponse.Failure("The calling pane is closed.");
        var otherAgent = childAgent == caller.Provider ? null : childAgent;
        var key = DelegationRequestKey.Make(parentId, parent.RunId ?? "", task, mode, otherAgent);
        if (SameCall(key, parentId) is { } same) return DelegationResponse.Delegated(same);
        if (DelegateRefusal(parentId, parent, mode, childAgent) is { } reason) return DelegationResponse.Refusal(reason);
        if (otherAgent is not null && Unavailable(childAgent, await Host(() => host.AgentAvailability(childAgent))) is { } why) return DelegationResponse.Refusal(DelegationReasonCode.AgentUnavailable, why);
        ChildWorktreeBase start;
        switch (await worktrees.Check(parent.Folder))
        {
            case ChildWorktreeCheck.Ready ready: start = ready.Base; break;
            case ChildWorktreeCheck.Refused refused: return DelegationResponse.Refusal(refused.Reason);
            default: throw new InvalidOperationException();
        }
        var context = await PruneContext();
        // Nothing below suspends until the record is saved, so two calls at once cannot both pass these checks.
        if (SameCall(key, parentId) is { } repeated) return DelegationResponse.Delegated(repeated);
        if (DelegateRefusal(parentId, parent, mode, childAgent) is { } late) return DelegationResponse.Refusal(late);
        var id = Wire.Id();
        var child = ChildRecord.Make(id, parentId, worktrees.WorktreePath(id), start.ParentBranch, start.BaseCommit, mode, key, parentCheckout: start.Repository, provider: childAgent);
        try
        {
            if (File.Admitting(child, context, DelegationCopy.Of(id, DelegationCopyKind.Task, 0, Encoding.UTF8.GetBytes(task))) is not DelegationAdmission.Admitted admitted) return DelegationResponse.Refusal(DelegationReasonCode.StoreFull);
            Commit(admitted.File, context);
        }
        catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Full) { return DelegationResponse.Refusal(DelegationReasonCode.StoreFull); }
        catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { return DelegationResponse.Failure("The delegation file could not be saved, so no child was made."); }
        starting.Add(id);
        _ = turn.StartNew(() => StartChild(id, start, task, parent.Provider));
        return DelegationResponse.Delegated(child);
    }
```

`DelegationDelegateTool.cs`, `StartChild`:
- In its summary, change `its pane of the parent's agent in the starting mode` to `its pane of its own agent in the starting mode`.
- Change its signature to `private async Task StartChild(string id, ChildWorktreeBase start, string task, string parentAgent, HumanChildStarter? human = null)`.
- Replace the line `runId = human is not null ? await Host(() => human.Start(id, HumanFirstRun(task, worktree, child.Agent))) : await Host(() => host.StartRun(id, FirstInput(task, worktree, child.Agent)));` with:

```csharp
                        runId = human is not null ? await Host(() => human.Start(id, HumanFirstRun(task, worktree, child.Agent, parentAgent))) : await Host(() => host.StartRun(id, FirstInput(task, worktree, child.Agent, parentAgent)));
```

`DelegationDelegateTool.cs`, replace `DelegateRefusal` and its summary (lines 241-253) with:

```csharp
    /// <summary>
    /// Why the pane <paramref name="parentId"/>, now <paramref name="parent"/>, may not have a new child of the
    /// <paramref name="child"/> agent in <paramref name="mode"/>, or null when it may. The cap counts the open children of the same
    /// kind, of every agent: those its delegate made, or (<paramref name="byHuman"/>) those the human started from its composer, so
    /// neither kind blocks the other.
    /// </summary>
    private DelegationReasonCode? DelegateRefusal(string parentId, DelegationPaneState parent, string mode, string child, bool byHuman = false)
    {
        // A pane with a parent link is already one layer down.
        var depth = parent.ParentSessionId is not null || File.Children.Any(record => record.Id == parentId) ? 1 : 0;
        if (depth >= MaximumDepth) return DelegationReasonCode.ChildCannotDelegate;
        if (File.Children.Count(record => record.ParentSessionId == parentId && record.State.IsOpen() && record.NotifiesParent != byHuman) >= MaximumOpenChildren) return DelegationReasonCode.WidthCap;
        return DelegationProviders.IsNoWider(mode, child, parent.PermissionMode, parent.Provider) ? null : DelegationReasonCode.WiderMode;
    }
```

`StartHumanChild` keeps compiling with the parent's agent until Task 8:
- Replace `DelegateRefusal(parentId, parent, mode, byHuman: true)` (lines 211 and 221) with `DelegateRefusal(parentId, parent, mode, parent.Provider, byHuman: true)`.
- Replace `_ = turn.StartNew(() => StartChild(id, start, text, starter));` (line 234) with `_ = turn.StartNew(() => StartChild(id, start, text, parent.Provider, starter));`.

- [ ] **Step 4: Run it and expect it to pass**

Run CORE-RUN.
Expected:
- the four new `delegation:` checks PASS;
- `delegate starts a child in its own worktree, once per call, three at most` and `delegate refuses before anything is made` still PASS;
- the 9 baseline FAILs only.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/DelegationCoordinator.cs native/windows/MightyClaude.Core/DelegationDelegateTool.cs native/windows/MightyClaude.Core.Tests/DelegationDeliveryVerification.cs native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a Claude or Codex parent's delegate call runs a child of the agent it names: an unknown agent is refused with agent_unknown, one the app last found not installed or signed out with agent_unavailable naming it, a mode wider than the parent's by capabilities with wider_mode, and the cap counts children of every agent; the child's agent runs, commits and reports it while merges follow the parent's" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 7: Locale keys for the Windows menu and errors

**Files:**
- Modify: `locales/ko.json`, `locales/en.json`, `locales/zh.json`, `locales/ja.json`:
  - after line 1398 (`delegation.human.error.agentNotInstalled`);
  - after line 1774 (`composer.worktreeMenu.sameAgent`);
  - line 1782 (`queue.worktreeHint`).
- Modify (copies): `native/macos/Sources/MightyCore/Resources/Locales/{ko,en,zh,ja}.json`, `mobile/src/locales/{ko,en,zh,ja}.json`.
- Modify: `docs/i18n.md` (regenerated).

**Interfaces:**
- Consumes. These existing keys are reused as they are (checked with `git grep -n "composer.worktreeMenu\|delegation.human.error\|delegation.human.returnedToComposer\|queue.worktreeMenuHintMac" -- locales/en.json`):
  - `composer.worktreeMenu.title`, `.agent`, `.blocked`, `.notInstalled`, `.signedOut`, `.noMode`, `.cap`;
  - `delegation.human.error.agentSignedOut`, `delegation.human.error.agentNoMode`, `delegation.human.error.readOnlyMode`, `delegation.human.error.startFailed`;
  - `delegation.human.returnedToComposer`;
  - `queue.worktreeMenuHintMac`: its text names no Mac-only thing, so Windows uses it for the menu-only button's hint.
- Produces. Only the keys whose Mac text names ⌘ or "this Mac" get a Windows twin:
  - `composer.worktreeMenu.sameAgentWindows` (`{agent}`, `{mode}`)
  - `delegation.human.error.agentNotInstalledWindows` (`{agent}`)
  - the new text of `queue.worktreeHint` (Windows-only already; it now mentions the right-click).

- [ ] **Step 1: Write the failing test**

The locale checker is the test.
- `locales/en.json`, after the `"delegation.human.error.agentNotInstalled"` line, add:

```json
  "delegation.human.error.agentNotInstalledWindows": "{agent} is not installed on this PC, so it cannot run a child pane. Check Settings › CLI.",
```

- `locales/en.json`, after the `"composer.worktreeMenu.sameAgent"` line, add:

```json
  "composer.worktreeMenu.sameAgentWindows": "{agent} · {mode} (Ctrl+Enter)",
```

- `locales/en.json`, replace the `"queue.worktreeHint"` line with:

```json
  "queue.worktreeHint": "Run now in a child pane with its own new worktree, beside this one (Ctrl+Enter). Right-click to pick its agent.",
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `node scripts/check-locales.js --check`
Expected: it fails.
- en has two keys ko lacks (`delegation.human.error.agentNotInstalledWindows`, `composer.worktreeMenu.sameAgentWindows`).
- The Mac and phone copies differ from `locales/en.json`.
- The two new keys are called from no client source (rule 4).

- [ ] **Step 3: Implement**

`locales/ko.json`:

```json
  "delegation.human.error.agentNotInstalledWindows": "{agent}: 이 PC에 설치되어 있지 않아 자식 창을 실행할 수 없습니다. 설정 › CLI를 확인하세요.",
```

```json
  "composer.worktreeMenu.sameAgentWindows": "{agent} · {mode} (Ctrl+Enter)",
```

```json
  "queue.worktreeHint": "이 창 옆에 자식 창을 열어 새 worktree에서 바로 실행 (Ctrl+Enter). 오른쪽 클릭하면 에이전트를 고릅니다.",
```

`locales/zh.json`:

```json
  "delegation.human.error.agentNotInstalledWindows": "{agent} 未安装在这台电脑上，无法运行子窗格。请查看“设置 › CLI”。",
```

```json
  "composer.worktreeMenu.sameAgentWindows": "{agent} · {mode}（Ctrl+Enter）",
```

```json
  "queue.worktreeHint": "在此窗格旁打开子窗格，立即在新的 worktree 中运行（Ctrl+Enter）。右键单击可选择其代理。",
```

`locales/ja.json`:

```json
  "delegation.human.error.agentNotInstalledWindows": "{agent} はこのPCにインストールされていないため、子ペインを実行できません。設定 › CLIを確認してください。",
```

```json
  "composer.worktreeMenu.sameAgentWindows": "{agent} · {mode} (Ctrl+Enter)",
```

```json
  "queue.worktreeHint": "このペインの隣に子ペインを開き、新しいworktreeですぐ実行 (Ctrl+Enter)。右クリックでエージェントを選べます。",
```

Each file takes the same positions: after the `agentNotInstalled` line, after the `sameAgent` line, and replacing the `queue.worktreeHint` line.

Copy the originals byte for byte into the two client copies:

```bash
cd /Users/young/Work/MightyClaude
for lang in ko en zh ja; do cp locales/$lang.json native/macos/Sources/MightyCore/Resources/Locales/$lang.json; cp locales/$lang.json mobile/src/locales/$lang.json; done
```

The two new keys get their callers in Task 8 (`agentNotInstalledWindows`, Core) and Task 10 (`sameAgentWindows`, WinUI). Until then rule 4 reports them unused. That is expected here, and Step 4 only checks the other rules.

- [ ] **Step 4: Run it and expect it to pass (except the two not-yet-called keys)**

Run: `node scripts/check-locales.js` (it rewrites `docs/i18n.md` even when a rule fails), then `node scripts/check-locales.js --check`
Expected: in both runs the only failure is rule 4 naming exactly `delegation.human.error.agentNotInstalledWindows` and `composer.worktreeMenu.sameAgentWindows` as unused. Rules 1-3, 5 and 6 report no problem, and `--check` does not complain about `docs/i18n.md`.
Run: `cd /Users/young/Work/MightyClaude/mobile && npx jest --silent`
Expected: all suites pass.

- [ ] **Step 5: Commit**

```bash
git add docs/i18n.md locales/ko.json locales/en.json locales/zh.json locales/ja.json native/macos/Sources/MightyCore/Resources/Locales/ko.json native/macos/Sources/MightyCore/Resources/Locales/en.json native/macos/Sources/MightyCore/Resources/Locales/zh.json native/macos/Sources/MightyCore/Resources/Locales/ja.json mobile/src/locales/ko.json mobile/src/locales/en.json mobile/src/locales/zh.json mobile/src/locales/ja.json
git commit -m "(windows) words for the Windows worktree menu in all four languages: the pane's own agent's row names Ctrl+Enter, an agent not installed is not installed on this PC, and the worktree button's hint says a right-click picks the agent" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 8: The human's send names the child's agent

**Files:**
- Modify: `native/windows/MightyClaude.Core/DelegationDelegateTool.cs`, lines 186-236 (the `StartHumanChild` summary and body).
- Modify: `native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs`, `Verification.cs`.

**Interfaces:**
- Consumes:
  - `DelegationProviders.HumanChildMode`
  - `IDelegationHost.AgentAvailability`
  - the locale keys `delegation.human.error.agentNotInstalledWindows`, `delegation.human.error.agentSignedOut`, `delegation.human.error.agentNoMode`, `delegation.human.error.readOnlyMode`
- Produces (C#): `public Task<HumanChildStart> DelegationCoordinator.StartHumanChild(string text, string parentId, HumanChildStarter starter, bool planFirst = false, string? agent = null)`
- Mirrors (Swift): `startHumanChild(_:parentId:planFirst:agent:starter:)`.

- [ ] **Step 1: Write the failing test**

Append to `DelegationCrossAgentVerification`:

```csharp
    private static Dictionary<string, string> AgentWords(string label) => new() { ["agent"] = label };

    internal static async Task TheHumansMenuStartsTheNamedAgentsChild()
    {
        var place = await Cross("claude", "auto");
        try
        {
            // The parent's style starts in plan, which stays with Claude's own children.
            var codex = Made(await place.Send("Try it in Codex.", "codex", planFirst: true));
            Check(codex is { Provider: "codex", StartingMode: "acceptEdits", StartedByHuman: true }, "a Codex child in its widest mode");
            var gemini = Made(await place.Send("Try it in Antigravity.", "gemini"));
            Check(gemini is { Provider: "gemini", StartingMode: "acceptEdits" }, "an Antigravity child");
            var claude = Made(await place.Send("Plan it in Claude.", null, planFirst: true));
            Check(claude is { Provider: null, StartingMode: "plan" }, "Ctrl+Enter's Claude child in plan");
            foreach (var id in new[] { codex.Id, gemini.Id, claude.Id }) Check(await place.WaitFor(id, ChildState.Running), id + " runs");
            SameList(place.Host.CreatedPanes.Select(pane => pane.Provider), ["codex", "gemini", "claude"], "each pane runs its agent");
            Check(place.Host.CreatedPanes.All(pane => pane.StartedByHuman), "each is the human's");
            // Three open children the human started, of any agents: the cap.
            Same(await place.Send("One more.", "codex"), new HumanChildStart.Refused(DelegationReasonCode.WidthCap), "the cap");
            // The parent's tools still neither list nor act on them.
            Same((await place.Call("list_children")).Children!.Count, 0, "the parent lists none of them");
        }
        finally { place.Remove(); }
    }

    internal static async Task TheHumansMenuSaysWhy()
    {
        // A Claude pane in manual mode asks before each edit: no Codex child may edit unasked under it.
        var place = await Cross("claude", "manual");
        try
        {
            var before = place.Bytes();
            Same(await place.Send("Try it.", "codex"), new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentNoMode", AgentWords("Codex"))), "no Codex mode fits");
            place.Host.SetAgent("gemini", DelegationAgentAvailability.SignedOut);
            Same(await place.Send("Try it.", "gemini"), new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentSignedOut", AgentWords("Antigravity"))), "Antigravity is signed out");
            place.Host.SetAgent("codex", DelegationAgentAvailability.NotInstalled);
            Same(await place.Send("Try it.", "codex"), new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentNotInstalledWindows", AgentWords("Codex"))), "Codex is not installed");
            var words = Locale.Get("delegation.human.error.agentNotInstalledWindows", AgentWords("Codex"));
            Check(words.Contains("› CLI", StringComparison.Ordinal) && !words.Contains("Mac", StringComparison.Ordinal), "the words point to Settings › CLI and name no Mac");
            Same(await place.Send("Try it.", "other"), new HumanChildStart.Refused(DelegationReasonCode.AgentUnknown), "no such agent");
            place.NothingMade(before);
        }
        finally { place.Remove(); }

        // A read-only Codex pane gets a Claude child only in plan mode.
        var readOnly = await Cross("codex", "manual");
        try
        {
            var claude = Made(await readOnly.Send("Plan it.", "claude"));
            Check(claude is { Provider: null, StartingMode: "plan" }, "a Claude plan child of a read-only Codex");
            Same(await readOnly.Send("Try it.", "gemini"), new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentNoMode", AgentWords("Antigravity"))), "no Antigravity mode fits");
        }
        finally { readOnly.Remove(); }
    }

    internal static async Task ThePanesOwnAgentIsNeverAskedAbout()
    {
        var place = await Cross("claude", "auto");
        try
        {
            // Even what the app last learnt against Claude does not hold back a child of the running Claude pane.
            place.Host.SetAgent("claude", DelegationAgentAvailability.NotInstalled);
            var first = Made(await place.Send("First.", null));
            var second = Made(await place.Send("Second.", "claude"));
            foreach (var id in new[] { first.Id, second.Id }) Check(await place.WaitFor(id, ChildState.Running), id + " runs");
            Same(place.Host.AvailabilityAsked.Count, 0, "the pane's own agent is never asked about");
            // Another agent is asked about once per send.
            _ = await place.Send("Third.", "codex");
            SameList(place.Host.AvailabilityAsked, ["codex"], "Codex asked once");
        }
        finally { place.Remove(); }
    }
```

In `Verification.cs`, after the Task 6 lines, add:

```csharp
        await Test("delegation: the human's menu starts a child of the agent it names, in that agent's widest mode", DelegationCrossAgentVerification.TheHumansMenuStartsTheNamedAgentsChild);
        await Test("delegation: the human's menu says why another agent cannot run the text", DelegationCrossAgentVerification.TheHumansMenuSaysWhy);
        await Test("delegation: the human's child of the pane's own agent never asks about its CLI", DelegationCrossAgentVerification.ThePanesOwnAgentIsNeverAskedAbout);
```

- [ ] **Step 2: Run it and expect it to fail**

Run CORE-RUN.
Expected: the build fails with `CS1739: The best overload for 'StartHumanChild' does not have a parameter named 'agent'` (positional: `CS1501` with 5 arguments).

- [ ] **Step 3: Implement**

Replace `StartHumanChild`, its summary included, with:

```csharp
    /// <summary>
    /// The human's run of <paramref name="text"/> in a new child of the Claude, Codex or Antigravity pane <paramref name="parentId"/>,
    /// from its composer's second send (<see cref="ComposerSecondSend"/>) or from its worktree button's menu, which names the child's
    /// <paramref name="agent"/> (M/DelegationDelegateTool.swift startHumanChild). The child runs the parent's agent unless
    /// <paramref name="agent"/> names another, and is made as delegate makes one, in <see cref="DelegationProviders.HumanChildMode"/>:
    /// for the parent's agent, its permission mode now, or plan mode when <paramref name="planFirst"/> and the agent has one; for
    /// another agent, its widest mode no wider than the parent's in which it can change files. It is marked started by the human,
    /// so it sends the parent no notices and the parent's tools neither list it nor act on it; it has its own cap of 3 open children
    /// started by the human, of any agents, apart from the 3 the parent's delegate may have. The first rule that applies answers,
    /// and nothing is made by any but the last:
    /// 1. Delegation is switched off, or <paramref name="text"/> is no task (empty, with a NUL character or over 64 KiB): a failure in the app's language.
    /// 2. <c>parent_closed</c> (no such pane), <c>claude_only</c> (no agent pane), <c>agent_unknown</c> (<paramref name="agent"/>
    ///    is no agent's provider id), another agent whose CLI the app last found not installed or signed out, or no mode in which
    ///    the child could change files (a failure in the app's language naming why), then delegate's own refusals:
    ///    <c>child_cannot_delegate</c>, <c>width_cap</c>, <c>wider_mode</c>; <c>not_git</c>, <c>detached_head</c>,
    ///    <c>unborn_branch</c>, <c>low_disk</c>; <c>store_full</c>.
    /// 3. A delegation file the disk refuses: a failure.
    /// Otherwise the child's record is saved in creating and the answer names it, while its worktree, pane and first run follow in
    /// the background: <paramref name="starter"/> starts that run in the child's pane (<see cref="HumanFirstRun"/>), and hears when
    /// the worktree, the pane or that run could not be made after all.
    /// </summary>
    public Task<HumanChildStart> StartHumanChild(string text, string parentId, HumanChildStarter starter, bool planFirst = false, string? agent = null) => Isolated(async () =>
    {
        if (!isSwitchOn()) return (HumanChildStart)new HumanChildStart.Failed(Locale.Get("delegation.human.error.switchedOff"));
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > DelegationFileStore.MaximumCopyBytes) return new HumanChildStart.Failed(Locale.Get("delegation.human.error.invalidTask"));
        if (await Host(() => host.PaneState(parentId)) is not { } parent) return new HumanChildStart.Refused(DelegationReasonCode.ParentClosed);
        if (!DelegationProviders.IsAgentPane(parent.Kind, parent.Provider)) return new HumanChildStart.Refused(DelegationReasonCode.ClaudeOnly);
        var childAgent = agent ?? parent.Provider;
        if (!Wire.Providers.Contains(childAgent)) return new HumanChildStart.Refused(DelegationReasonCode.AgentUnknown);
        var words = new Dictionary<string, string> { ["agent"] = ProviderMark.Label(childAgent) };
        if (childAgent != parent.Provider)
            switch (await Host(() => host.AgentAvailability(childAgent)))
            {
                case DelegationAgentAvailability.NotInstalled: return new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentNotInstalledWindows", words));
                case DelegationAgentAvailability.SignedOut: return new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentSignedOut", words));
            }
        if (DelegationProviders.HumanChildMode(childAgent, parent.Provider, parent.PermissionMode, planFirst) is not { } mode)
            return new HumanChildStart.Failed(childAgent == parent.Provider ? Locale.Get("delegation.human.error.readOnlyMode") : Locale.Get("delegation.human.error.agentNoMode", words));
        if (DelegateRefusal(parentId, parent, mode, childAgent, byHuman: true) is { } reason) return new HumanChildStart.Refused(reason);
        ChildWorktreeBase start;
        switch (await worktrees.Check(parent.Folder))
        {
            case ChildWorktreeCheck.Ready ready: start = ready.Base; break;
            case ChildWorktreeCheck.Refused refused: return new HumanChildStart.Refused(refused.Reason);
            default: throw new InvalidOperationException();
        }
        var context = await PruneContext();
        // Nothing below suspends until the record is saved, so two sends at once cannot both pass the width cap.
        if (DelegateRefusal(parentId, parent, mode, childAgent, byHuman: true) is { } late) return new HumanChildStart.Refused(late);
        var id = Wire.Id();
        // No repeat to recognise: each send is its own child.
        var key = DelegationRequestKey.Make(parentId, "human-" + id, text, mode);
        var child = ChildRecord.Make(id, parentId, worktrees.WorktreePath(id), start.ParentBranch, start.BaseCommit, mode, key, parentCheckout: start.Repository, startedByHuman: true, provider: childAgent);
        try
        {
            if (File.Admitting(child, context, DelegationCopy.Of(id, DelegationCopyKind.Task, 0, Encoding.UTF8.GetBytes(text))) is not DelegationAdmission.Admitted admitted) return new HumanChildStart.Refused(DelegationReasonCode.StoreFull);
            Commit(admitted.File, context);
        }
        catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Full) { return new HumanChildStart.Refused(DelegationReasonCode.StoreFull); }
        catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { return new HumanChildStart.Failed(Locale.Get("delegation.human.error.notSaved")); }
        starting.Add(id);
        _ = turn.StartNew(() => StartChild(id, start, text, parent.Provider, starter));
        return new HumanChildStart.Started(child);
    });
```

- [ ] **Step 4: Run it and expect it to pass**

Run CORE-RUN.
Expected:
- the three new checks PASS;
- `the human's send makes a child that never notifies its parent` and `the parent's tools and caps leave the human's child alone` still PASS;
- the 9 baseline FAILs only.

Run: `node scripts/check-locales.js`, then `node scripts/check-locales.js --check`
Expected: rule 4 now names only `composer.worktreeMenu.sameAgentWindows`.

- [ ] **Step 5: Commit**

```bash
git add docs/i18n.md native/windows/MightyClaude.Core/DelegationDelegateTool.cs native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the human's send may name the child's agent: another agent's child starts in its widest mode the pane's mode covers, an agent not installed, not signed in or with no mode that can edit says why in the app's language, the pane's own agent is never asked about, and the 3 the human may start count every agent" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 9: The desktop service offers the menu

**Files:**
- Modify: `native/windows/MightyClaude.Core/DesktopService.Delegation.cs`:
  - after line 106 (`DelegatedRunRequest`): the availability hook;
  - after line 300 (`IDelegationHost.StopRun`): `IDelegationHost.AgentAvailability`;
  - lines 334-365 (`SecondSend` and `RefreshWorktreeReadiness`, with their summaries);
  - lines 373-394 (`StartHumanChildAsync` with its summary).
- Modify: `native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs`, `Verification.cs`.

**Interfaces:**
- Consumes:
  - `DelegationProviders.HumanChildChoices`, `DelegationProviders.MayHaveHumanChild`
  - `DelegationSidebar.OpenHumanChildCount`
  - `ComposerSecondSends.MenuOnly`
  - `DelegationCoordinator.StartHumanChild(…, agent)`
- Produces (C#):
  - `public Func<string, DelegationAgentAvailability>? DesktopService.AgentAvailabilityNow { get; set; }`
  - `Task<DelegationAgentAvailability> IDelegationHost.AgentAvailability(string provider)` (explicit)
  - `public IReadOnlyList<DelegationChildChoice> DesktopService.WorktreeChildChoices(RunSession session, bool planFirst)`
  - `public bool DesktopService.WorktreeMenuOnly(RunSession session, bool planFirst)`
  - `public bool DesktopService.OffersWorktreeMenu(RunSession session, bool planFirst)`
  - `public async Task<HumanChildStart> DesktopService.StartHumanChildAsync(string parentId, string text, IReadOnlyList<RunAttachment> attachments, bool planFirst, Func<string, Task> failed, string? agent = null)`
- Mirrors (Swift): `AppStore.agentAvailabilityNow`, `worktreeChildChoices`, `worktreeMenuOnly`, `offersWorktreeMenu`, `refreshWorktreeReadiness` (with `mayHaveHumanChild`), `runInNewWorktree(…agent:)`.

- [ ] **Step 1: Write the failing test**

Append to `DelegationCrossAgentVerification`:

```csharp
    internal static async Task TheServiceOffersTheMenuAndStartsAnotherAgentsChild()
    {
        var place = new DelegationPlace();
        try
        {
            await place.Repository();
            await using var s = await DelegationLaunch.Start(place.Profile, Path.Combine(place.Base, "fake"), place.Repo, place.Root, parentMode: "auto");
            // The Claude parent's model, the Codex pane used last, and a read-only Codex pane.
            await s.Service.UpdateAsync(state => state with
            {
                Sessions =
                [
                    .. state.Sessions.Select(pane => pane.Id == DelegationLaunch.Parent ? pane with { Model = "opus", Settings = pane.Settings with { Effort = "high" } } : pane),
                    new RunSession { Id = "codex-manual", WorkspaceId = s.Workspace.Id, Kind = "claude", Provider = "codex", Settings = new(PermissionMode: "manual") },
                    new RunSession { Id = "codex-template", WorkspaceId = s.Workspace.Id, Kind = "claude", Provider = "codex", Model = "gpt-5.6-sol", Settings = new(Effort: "low", PermissionMode: "fullAccess"), Logs = [new("l", "user", "hi", "2099-01-01T00:00:00Z")] },
                ],
            });
            s.Service.AgentAvailabilityNow = provider => provider == "gemini" ? DelegationAgentAvailability.NotInstalled : DelegationAgentAvailability.Ready;
            SameList<DelegationChildChoice>(s.Service.WorktreeChildChoices(s.Pane(DelegationLaunch.Parent), false),
                [new("claude", "auto", null), new("codex", "acceptEdits", null), new("gemini", "acceptEdits", DelegationChildBlock.NotInstalled)], "a Claude pane in auto offers its menu from what the window knows");
            // The read-only Codex pane: its own child cannot write, a Claude plan child can, so the button shows for its menu alone.
            var manual = s.Pane("codex-manual");
            s.Service.RefreshWorktreeReadiness(manual);
            Check(await Eventually(() => s.Service.WorktreeMenuOnly(manual, false)) && s.Service.SecondSend(manual) == ComposerSecondSend.Queue && s.Service.OffersWorktreeMenu(manual, false), "a read-only Codex pane offers the menu alone");
            Check(!s.Service.WorktreeMenuOnly(s.Pane(DelegationLaunch.Parent), false), "a Claude pane's own child can write: not the menu alone");
            // Another agent's child the human starts takes that agent's last-used pane's model and effort, in its widest mode.
            var answer = await s.Service.StartHumanChildAsync(DelegationLaunch.Parent, "Try it in Codex.", [], false, _ => Task.CompletedTask, "codex");
            var child = Made(answer);
            Check(child is { Provider: "codex", StartingMode: "acceptEdits", StartedByHuman: true }, "a Codex child in acceptEdits");
            Check(await Eventually(() => s.Service.Snapshot.Sessions.Any(pane => pane.Id == child.Id)), "its pane opens");
            Check(s.Pane(child.Id) is { Provider: "codex", Model: "gpt-5.6-sol", ParentSessionId: DelegationLaunch.Parent } made && made.Settings is { Effort: "low", PermissionMode: "acceptEdits" }, "it takes the Codex template's model and effort");
            Check(s.Service.DelegationChildren.Single(row => row.Id == child.Id) is { Agent: "codex", StartedByHuman: true }, "its row names its agent");
            Same(await s.Service.StartHumanChildAsync(DelegationLaunch.Parent, "Try it in Antigravity.", [], false, _ => Task.CompletedTask, "gemini"),
                new HumanChildStart.Failed(Locale.Get("delegation.human.error.agentNotInstalledWindows", AgentWords("Antigravity"))), "an agent the window found not installed is refused in the app's words");
        }
        finally { place.Remove(); }
    }
```

In `Verification.cs`, after the Task 8 lines, add:

```csharp
        await Test("delegation: the service offers the worktree menu and starts another agent's child", DelegationCrossAgentVerification.TheServiceOffersTheMenuAndStartsAnotherAgentsChild);
```

- [ ] **Step 2: Run it and expect it to fail**

Run CORE-RUN.
Expected: the build fails with `CS1061: 'DesktopService' does not contain a definition for 'AgentAvailabilityNow'` (and for `WorktreeChildChoices`, `WorktreeMenuOnly`, `OffersWorktreeMenu`), and `CS1501` for `StartHumanChildAsync` with 6 arguments.

- [ ] **Step 3: Implement**

After `DelegatedRunRequest` (line 106), add:

```csharp
    /// <summary>
    /// Whether an agent's CLI can run a child, as the window last learnt it from its CLI search and the sign-in status Settings last
    /// read (M/AppStore+Delegation.swift agentAvailabilityNow); never by asking a CLI. Called from the coordinator's thread. null
    /// knows nothing against any agent.
    /// </summary>
    public Func<string, DelegationAgentAvailability>? AgentAvailabilityNow { get; set; }
```

After `IDelegationHost.StopRun` (line 300), add:

```csharp
    /// <summary>What the window last learnt of <paramref name="provider"/>'s CLI (<see cref="AgentAvailabilityNow"/>).</summary>
    Task<DelegationAgentAvailability> IDelegationHost.AgentAvailability(string provider) => Task.FromResult(AgentAvailabilityNow?.Invoke(provider) ?? DelegationAgentAvailability.Ready);
```

Replace `SecondSend` and `RefreshWorktreeReadiness`, with their summaries (lines 334-365), with:

```csharp
    /// <summary>
    /// What the composer's second send (Ctrl+Enter) does with the text while the pane is busy: a new child worktree for a Claude,
    /// Codex or Antigravity pane that may have children (delegation on and loaded, its folder last checked able to host one),
    /// otherwise steer or queue as before (<see cref="ComposerSecondSends.Choose"/>).
    /// </summary>
    public ComposerSecondSend SecondSend(RunSession session) =>
        ComposerSecondSends.Choose(session.Kind, session.Provider, session.Settings.PermissionMode, IsDelegatedChild(session), delegation is not null && delegationEnabled(), CanHostWorktree(session), DelegationProviders.CanSteer(session.Kind, session.Provider));

    /// <summary>
    /// What the worktree button's menu on the agent pane <paramref name="session"/> offers: Claude, Codex and Antigravity, each with
    /// why it cannot run the text now, read from what the window already knows (<see cref="AgentAvailabilityNow"/>), never by asking
    /// a CLI. <paramref name="planFirst"/>: the pane's style starts every request in plan mode.
    /// </summary>
    public IReadOnlyList<DelegationChildChoice> WorktreeChildChoices(RunSession session, bool planFirst) =>
        DelegationProviders.HumanChildChoices(session.Provider, session.Settings.PermissionMode, planFirst, DelegationSidebar.OpenHumanChildCount(session.Id, DelegationChildren),
            provider => AgentAvailabilityNow?.Invoke(provider) ?? DelegationAgentAvailability.Ready);

    /// <summary>Whether the busy pane shows the worktree button for its menu alone: its own agent's child could not change files in its mode, but another agent's could (<see cref="ComposerSecondSends.MenuOnly"/>).</summary>
    public bool WorktreeMenuOnly(RunSession session, bool planFirst) =>
        ComposerSecondSends.MenuOnly(session.Kind, session.Provider, session.Settings.PermissionMode, IsDelegatedChild(session), delegation is not null && delegationEnabled(), CanHostWorktree(session), WorktreeChildChoices(session, planFirst));

    /// <summary>Whether the worktree button, and its menu, shows on the busy pane.</summary>
    public bool OffersWorktreeMenu(RunSession session, bool planFirst) => SecondSend(session) == ComposerSecondSend.Worktree || WorktreeMenuOnly(session, planFirst);

    private bool IsDelegatedChild(RunSession session) => session.ParentSessionId is not null || delegation?.File.Children.Any(child => child.Id == session.Id) == true;

    private bool CanHostWorktree(RunSession session)
    {
        var folder = WorktreeHostFolder(session);
        lock (sync) return folder is not null && worktreeReadiness.GetValueOrDefault(folder);
    }

    /// <summary>
    /// Checks again whether <paramref name="session"/>'s folder can host a child worktree, for <see cref="SecondSend"/> and the
    /// worktree button's menu: a top-level Claude, Codex or Antigravity pane whose child, of its own agent or another, could change
    /// files, with delegation on. One check per folder at a time; a changed answer raises <see cref="DelegationChanged"/>.
    /// </summary>
    public void RefreshWorktreeReadiness(RunSession session)
    {
        if (delegation is not { } coordinator || session.ParentSessionId is not null || !DelegationProviders.MayHaveHumanChild(session.Kind, session.Provider, session.Settings.PermissionMode)
            || WorktreeHostFolder(session) is not { } folder || !delegationEnabled()) return;
        lock (sync) if (!worktreeReadinessChecks.Add(folder)) return;
        _ = Task.Run(async () =>
        {
            bool ready;
            try { ready = (await coordinator.Worktrees.Check(folder)).CanHostWorktree; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ready = false; }
            bool changed;
            lock (sync) { worktreeReadinessChecks.Remove(folder); changed = worktreeReadiness.GetValueOrDefault(folder) != ready || !worktreeReadiness.ContainsKey(folder); worktreeReadiness[folder] = ready; }
            if (changed) RaiseDelegationChanged();
        });
    }
```

Replace `StartHumanChildAsync` with its summary (lines 373-394) with:

```csharp
    /// <summary>
    /// The composer's second send on the busy agent pane <paramref name="parentId"/> (<see cref="ComposerSecondSend.Worktree"/>), or
    /// the worktree button's menu naming the child's <paramref name="agent"/>: <paramref name="text"/> and its attachments run now
    /// in a new child pane, in its own worktree beside the pane, made by delegation as delegate makes one, in plan mode when
    /// <paramref name="planFirst"/> and the child runs the pane's agent. The pane's run goes on undisturbed, and the new child shows
    /// at once. What delegation answered; when the child's worktree, pane or first run could not be made after the answer,
    /// <paramref name="failed"/> hears it with the child's id (the child stays listed as failed to start), and the text needs another way.
    /// </summary>
    public async Task<HumanChildStart> StartHumanChildAsync(string parentId, string text, IReadOnlyList<RunAttachment> attachments, bool planFirst, Func<string, Task> failed, string? agent = null)
    {
        if (delegation is not { } coordinator) return new HumanChildStart.Failed(Locale.Get("delegation.human.error.switchedOff"));
        var starter = new HumanChildStarter(async (childId, run) =>
        {
            // Kept before the run starts, so a command that ends at once still finds its note.
            if (run.FollowingNote is { } note) lock (sync) followingNotes[childId] = note;
            var runId = await StartDelegatedRun(childId, run.Input, attachments);
            if (runId is null) lock (sync) followingNotes.Remove(childId);
            return runId;
        }, failed);
        return await coordinator.StartHumanChild(text, parentId, starter, planFirst, agent);
    }
```

- [ ] **Step 4: Run it and expect it to pass**

Run CORE-RUN.
Expected:
- `PASS delegation: the service offers the worktree menu and starts another agent's child`;
- `the desktop service hosts delegation and offers the window its entry points` still PASS;
- the 9 baseline FAILs only.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.Core/DesktopService.Delegation.cs native/windows/MightyClaude.Core.Tests/DelegationCrossAgentVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the desktop service offers the worktree menu: each agent's row from what the window last learnt of its CLI, the button for its menu alone where only another agent's child fits, worktree readiness learnt for such panes too, and the human's send naming the child's agent" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 10: The worktree button's flyout in WinUI

**Files:**
- Create: `native/windows/MightyClaude.WinUI/MainWindow.WorktreeMenu.cs`
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.QueuedComposer.cs`:
  - lines 26-33 (fields);
  - lines 57-61 (the worktree button's construction);
  - lines 142-154 (`RefreshQueuedComposer`).
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.Delegation.cs`:
  - line 48 (`InitializeDelegation`);
  - lines 381-404 (`RunInNewWorktree` with its summary).

**Interfaces:**
- Consumes:
  - `DesktopService.AgentAvailabilityNow`, `WorktreeChildChoices`, `WorktreeMenuOnly`, `OffersWorktreeMenu`, `StartHumanChildAsync(…, agent)`
  - `ComposerSecondSends.ReturnsToComposer`
  - `DelegationProviders.Availability`
  - `CliAccountsCoordinator.Statuses`
  - `RuntimeInfo.Providers`
  - `PaneView.MenuHeader(string)`, `PaneView.PermissionLabel(string, string)`, `PaneView.SlashNote(string)`
  - `ProviderCatalog.BetaLabel`
- Produces (C#, WinUI):
  - `private DelegationAgentAvailability MainWindow.AgentAvailabilityFromCli(string provider)`
  - `private readonly ConcurrentDictionary<string, bool> MainWindow.smokeAgentsReady`
  - `PaneView.worktreeMenu` (a `MenuFlyout`), `PaneView.worktreeMenuOnly`
  - `private void PaneView.FillWorktreeMenu()`
  - `private static string PaneView.WorktreeChoiceTitle(DelegationChildChoice choice, string parent)`
  - `private async Task PaneView.SendToWorktreeAgent(string agent)`
  - `private async Task PaneView.ReturnToComposer(string text, IReadOnlyList<RunAttachment> files, string reason)`
  - `private void PaneView.RunInNewWorktree(string text, IReadOnlyList<RunAttachment> files, string? agent = null)`
  - AutomationIds: `composer-worktree-{paneId}` (the button, unchanged); `composer-worktree-claude-{paneId}`, `composer-worktree-codex-{paneId}`, `composer-worktree-gemini-{paneId}` (the flyout items, as the Mac's).
- Mirrors (Swift): `SessionPaneView.worktreeButton`/`worktreeMenuButton`/`worktreeAgentMenu`/`worktreeChoiceTitle`, `AppStore.submit(_:steering:agent:)`, `returnToComposer`.

- [ ] **Step 1: Write the failing test**

WinUI has no unit tests; its compile check and the Task 11 smoke are the test.
- Change the smoke helper in `MainWindow.DelegationSmoke.cs` line 261 to call the new signature:

```csharp
        internal void DelegationSmokeRunInNewWorktree(string text, string? agent = null) => RunInNewWorktree(text, [], agent);
```

- In `MainWindow.Delegation.cs` `InitializeDelegation`, after `service.DelegatedRunBlocked = DelegatedRunBlocked;` (line 48), add:

```csharp
        service.AgentAvailabilityNow = AgentAvailabilityFromCli;
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash scripts/check-winui-compile.sh`
Expected: it fails with `CS1501: No overload for method 'RunInNewWorktree' takes 3 arguments` and `CS0103: The name 'AgentAvailabilityFromCli' does not exist`.

- [ ] **Step 3: Implement**

Create `native/windows/MightyClaude.WinUI/MainWindow.WorktreeMenu.cs`:

```csharp
using System.Collections.Concurrent;
using MightyClaude.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

/// <summary>
/// The composer's worktree button's agent menu (M/SessionPaneView.swift worktreeAgentMenu, M/AppStore+Delegation.swift
/// worktreeChildChoices/agentAvailabilityNow/returnToComposer): a click or Ctrl+Enter runs the draft in a child of the pane's own
/// agent; a right-click (or Shift+F10, or the menu key) picks the child's agent among Claude, Codex and Antigravity, each greyed out
/// with why it cannot run now. A text whose other-agent child cannot start goes back to the composer, never to the pane.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The agents a smoke run treats as ready, in what the window learnt of their CLIs' place.</summary>
    private readonly ConcurrentDictionary<string, bool> smokeAgentsReady = new();

    /// <summary>
    /// Whether <paramref name="provider"/>'s CLI can run a child, as the window last learnt it: found by its CLI search
    /// (<see cref="runtime"/>), and not signed out by the account status Settings last read. Called from the coordinator's thread:
    /// the runtime reference and the accounts' concurrent dictionary are each read once, and no CLI is asked.
    /// </summary>
    private DelegationAgentAvailability AgentAvailabilityFromCli(string provider)
    {
        if (smokeAgentsReady.ContainsKey(provider)) return DelegationAgentAvailability.Ready;
        var known = runtime;
        return DelegationProviders.Availability(known?.Providers.FirstOrDefault(p => p.Id == provider)?.Available, accountsCoordinator.Statuses.GetValueOrDefault(provider));
    }

    private sealed partial class PaneView
    {
        /// <summary>The worktree button's agent menu: its right-click flyout, and its click's where only another agent's child fits.</summary>
        private readonly MenuFlyout worktreeMenu = new();
        /// <summary>The button shows for its menu alone: this pane's own agent cannot have a child in its mode, another can.</summary>
        private bool worktreeMenuOnly;
        /// <summary>Whether the button's tooltip names the menu alone, so it is set again only when that changes.</summary>
        private bool worktreeTipIsMenu;

        /// <summary>One row per agent under the menu's header, the pane's own marked as Ctrl+Enter's; an agent that cannot run the child now is greyed out with why.</summary>
        private void FillWorktreeMenu()
        {
            worktreeMenu.Items.Clear();
            worktreeMenu.Items.Add(MenuHeader(Locale.Get("composer.worktreeMenu.title")));
            var pane = Session;
            foreach (var choice in owner.service.WorktreeChildChoices(pane, LaunchesInPlanMode))
            {
                var agent = choice.Agent;
                var item = new MenuFlyoutItem { Text = ProviderCatalog.BetaLabel(agent, WorktreeChoiceTitle(choice, pane.Provider)), IsEnabled = choice.Blocked is null };
                item.Click += async (_, _) => await owner.Act(() => SendToWorktreeAgent(agent));
                AutomationProperties.SetAutomationId(item, $"composer-worktree-{agent}-{id}");
                worktreeMenu.Items.Add(item);
            }
        }

        /// <summary><c>Codex · Working folder</c>, <c>Claude · Accept file edits (Ctrl+Enter)</c>, or <c>Codex — not signed in</c>.</summary>
        private static string WorktreeChoiceTitle(DelegationChildChoice choice, string parent)
        {
            var words = new Dictionary<string, string> { ["agent"] = ProviderMark.Label(choice.Agent) };
            if (choice.Blocked is not { } blocked)
            {
                words["mode"] = PermissionLabel(choice.Agent, choice.Mode ?? "");
                return Locale.Get(choice.Agent == parent ? "composer.worktreeMenu.sameAgentWindows" : "composer.worktreeMenu.agent", words);
            }
            words["reason"] = Locale.Get(blocked switch
            {
                DelegationChildBlock.NotInstalled => "composer.worktreeMenu.notInstalled",
                DelegationChildBlock.SignedOut => "composer.worktreeMenu.signedOut",
                DelegationChildBlock.NoMode => "composer.worktreeMenu.noMode",
                _ => "composer.worktreeMenu.cap",
            });
            return Locale.Get("composer.worktreeMenu.blocked", words);
        }

        /// <summary>
        /// The menu's pick (M/AppStore.swift submit(agent:)): the composer's words and files run now in a new child of
        /// <paramref name="agent"/>, of another agent too, and never in this pane. Offered no more, the draft stays where it is.
        /// </summary>
        private async Task SendToWorktreeAgent(string agent)
        {
            var text = input.Text; var files = pendingAttachments.ToArray();
            if (string.IsNullOrWhiteSpace(text) || composingInput || attachmentsLoading || !owner.service.OffersWorktreeMenu(Session, LaunchesInPlanMode)) return;
            // Taken from the composer now, so a second pick cannot send it again.
            var sent = files.Select(file => file.Id).ToHashSet(); pendingAttachments.RemoveAll(file => sent.Contains(file.Id)); RefreshAttachments();
            if (input.Text == text) { updating = true; input.Text = ""; updating = false; await Change(s => s with { Draft = "" }); }
            RunInNewWorktree(text, files, agent);
            RefreshComposerState();
        }

        /// <summary>
        /// The text of a child of another agent that could not be made or started goes back to the composer with its files, ahead of
        /// anything typed there meanwhile, and the transcript says why (M/AppStore+Delegation.swift returnToComposer). It never runs in this pane.
        /// </summary>
        private async Task ReturnToComposer(string text, IReadOnlyList<RunAttachment> files, string reason)
        {
            if (!QueuePaneAlive) return;
            await SlashNote(Locale.Get("delegation.human.returnedToComposer", new Dictionary<string, string> { ["reason"] = reason }));
            var draft = input.Text.Length == 0 ? text : text + "\n\n" + input.Text;
            updating = true; input.Text = draft; updating = false; await Change(p => p with { Draft = draft });
            var kept = pendingAttachments.Select(file => file.Id).ToHashSet(); pendingAttachments.InsertRange(0, files.Where(file => !kept.Contains(file.Id))); RefreshAttachments();
            RefreshComposerState();
        }
    }
}
```

`MainWindow.QueuedComposer.cs`:
- Replace the worktree button's summary (lines 26-29) with:

```csharp
        /// <summary>
        /// The second send in steer's place when it opens a new child worktree (<see cref="ComposerSecondSend.Worktree"/>), or for its
        /// agent menu alone: the same disc with the branch, shown while the draft has words for the child's task
        /// (M/SessionPaneView.swift worktreeButton/worktreeMenuButton). A right-click opens the menu (MainWindow.WorktreeMenu.cs).
        /// </summary>
```

- Replace line 58 (`worktreeButton.Click += async (_, _) => await Send(steering: true);`) with:

```csharp
            // A click (as Ctrl+Enter) runs the draft in a child of this pane's agent; where only another agent's child fits it opens the menu.
            worktreeButton.Click += async (_, _) => { if (worktreeMenuOnly) worktreeMenu.ShowAt(worktreeButton); else await Send(steering: true); };
            worktreeMenu.Opening += (_, _) => FillWorktreeMenu();
            worktreeButton.ContextFlyout = worktreeMenu;
```

- In `RefreshQueuedComposer`, replace lines 144-146 (`var second = …`, `var steers = …`, `offersWorktree = …`) with:

```csharp
            var second = queueable ? owner.service.SecondSend(Session) : ComposerSecondSend.Queue;
            // Where this pane's own agent cannot have a child in its mode but another agent's can, the same button shows for its menu alone.
            worktreeMenuOnly = queueable && second == ComposerSecondSend.Queue && owner.service.WorktreeMenuOnly(Session, LaunchesInPlanMode);
            var steers = second == ComposerSecondSend.Steer && pendingAttachments.Count == 0;
            offersWorktree = (second == ComposerSecondSend.Worktree || worktreeMenuOnly) && !string.IsNullOrWhiteSpace(input.Text);
            if (worktreeTipIsMenu != worktreeMenuOnly) { worktreeTipIsMenu = worktreeMenuOnly; ToolTipService.SetToolTip(worktreeButton, Locale.Get(worktreeMenuOnly ? "queue.worktreeMenuHintMac" : "queue.worktreeHint")); }
```

`DeferBusyComposer` is unchanged. Ctrl+Enter opens a worktree only where `SecondSend` is `Worktree`, so on a menu-only pane it queues as before.

`MainWindow.Delegation.cs`, replace `RunInNewWorktree` with its summary (lines 381-404) with:

```csharp
        /// <summary>
        /// The composer's second send on this busy pane (<see cref="ComposerSecondSend.Worktree"/>), or the worktree menu's pick of
        /// the child's <paramref name="agent"/> (M/AppStore+Delegation.swift runInNewWorktree): the text and its attachments run now in
        /// a new child pane, in its own worktree beside this one, made as delegate makes one, in plan mode when the pane's style
        /// starts every request so and the child runs this pane's agent. This pane's run goes on undisturbed, and the new child shows
        /// at once. When no child can be made, or it cannot start after all, the transcript says why and the text goes on by
        /// <see cref="RunAfterNoWorktree"/>; a text meant for another agent goes back to the composer instead (<see cref="ReturnToComposer"/>).
        /// </summary>
        private void RunInNewWorktree(string text, IReadOnlyList<RunAttachment> files, string? agent = null)
        {
            var planFirst = LaunchesInPlanMode; var parentId = id;
            var toComposer = ComposerSecondSends.ReturnsToComposer(agent, Session.Provider);
            // A pane that went away meanwhile has no composer to fall back to: the text is the child's failed task on its card.
            Task FallBack(string reason) => owner.OnWindow(async () =>
            {
                if (owner.views.TryGetValue(parentId, out var view))
                {
                    if (toComposer) await view.ReturnToComposer(text, files, reason);
                    else await view.RunAfterNoWorktree(text, files, reason);
                }
                return true;
            });
            _ = owner.Act(async () =>
            {
                var answer = await owner.service.StartHumanChildAsync(parentId, text, files, planFirst, _ => FallBack(Locale.Get("delegation.human.error.startFailed")), agent);
                if (answer switch { HumanChildStart.Refused refused => DelegationText.Reason(refused.Reason), HumanChildStart.Failed failed => failed.Message, _ => null } is { } reason) await FallBack(reason);
                if (!owner.closing) owner.Render();
            });
        }
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash scripts/check-winui-compile.sh`
Expected: it passes, with only the expected `InitializeComponent` note.
Run: `node scripts/check-design-tokens.js`
Expected: `✓ design-token ratchets hold.` The new code sets no FontSize, LineHeight or FontFamily, and no colour literal.
Run: `node scripts/check-locales.js`, then `node scripts/check-locales.js --check`
Expected: both pass. `composer.worktreeMenu.sameAgentWindows` now has its caller, and the Korean-literal budget is unchanged.
Run CORE-RUN.
Expected: the 9 baseline FAILs only. The Core suite does not compile WinUI; this guards that nothing in Core moved.

- [ ] **Step 5: Commit**

```bash
git add docs/i18n.md native/windows/MightyClaude.WinUI/MainWindow.WorktreeMenu.cs native/windows/MightyClaude.WinUI/MainWindow.QueuedComposer.cs native/windows/MightyClaude.WinUI/MainWindow.Delegation.cs native/windows/MightyClaude.WinUI/MainWindow.DelegationSmoke.cs
git commit -m "(windows) right-clicking the composer's worktree button opens a menu of Claude, Codex and Antigravity: each row names the mode its child would start in, the pane's own as Ctrl+Enter's, and greys out with why one cannot run (not installed, not signed in, no mode that can edit, 3 open); where only another agent's child fits a click opens it, and text whose other-agent child cannot start goes back to the composer" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 11: A smoke step starts an other-agent human child

**Files:**
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.DelegationSmoke.cs`:
  - lines 14-24: the summary;
  - after line 179 (`checks["removedAfterDiscard"] = true;`): step 9;
  - lines 259-268 (`PaneView` smoke helpers).
- Modify: `scripts/test-native-windows.ps1`, after line 115 (the `removeRefusedOpenChild` check).

**Interfaces:**
- Consumes:
  - `PaneView.FillWorktreeMenu`, `PaneView.worktreeMenu`, `PaneView.RunInNewWorktree(…, agent)`
  - `MainWindow.smokeAgentsReady`, `MainWindow.SetDelegationOn`
  - `DesktopService.WorktreeChildChoices`, `WorktreeMenuOnly`, `RefreshWorktreeReadiness`
- Produces:
  - the smoke result key `delegation.otherAgentChildStarted = {parent: "codex", child: "claude", mode: "plan", state: <state>}`;
  - `internal IReadOnlyList<MenuFlyoutItem> PaneView.DelegationSmokeWorktreeMenu()`.
- Mirrors (Swift): `AppStore+DelegationSmoke.swift` step 9 `otherAgentChildStarted`.

- [ ] **Step 1: Write the failing test**

In `scripts/test-native-windows.ps1`, after line 115, add:

```powershell
    # A child of another agent (MainWindow.DelegationSmoke.cs step 9): the worktree menu's pick of Claude on a read-only Codex pane starts a Claude child in plan mode.
    $cross = $result.delegation.otherAgentChildStarted
    if ($null -eq $cross -or $cross.parent -ne 'codex' -or $cross.child -ne 'claude' -or $cross.mode -ne 'plan' -or $cross.state -eq 'failed' -or $cross.state -eq 'creating') { throw "Windows delegation smoke failed: delegation.otherAgentChildStarted=$($cross | ConvertTo-Json -Compress)" }
```

In `MainWindow.DelegationSmoke.cs`'s `PaneView` (after `DelegationSmokeRunInNewWorktree`, line 261), add:

```csharp
        /// <summary>The worktree menu's agent rows as this pane fills them on opening: Claude, Codex, Antigravity, by their automation ids.</summary>
        internal IReadOnlyList<MenuFlyoutItem> DelegationSmokeWorktreeMenu()
        {
            FillWorktreeMenu();
            return worktreeMenu.Items.OfType<MenuFlyoutItem>().Where(item => AutomationProperties.GetAutomationId(item).StartsWith("composer-worktree-", StringComparison.Ordinal)).ToList();
        }
```

- [ ] **Step 2: Run it and expect it to fail**

The smoke cannot run on the Mac (never launch apps). The script check is the failing test: without step 9, `$result.delegation.otherAgentChildStarted` is `$null`, so the Windows CI smoke would throw `Windows delegation smoke failed: delegation.otherAgentChildStarted=`.
Locally, run: `bash scripts/check-winui-compile.sh`
Expected: it passes (the helper compiles), so the only missing piece is the step the script now demands.

- [ ] **Step 3: Implement**

At the end of the `RunDelegationSmoke` summary (before `/// </summary>`, line 24), add:

```csharp
    /// Then the worktree menu's pick of Claude on a read-only Codex pane (which never runs) starts a Claude child in plan mode, run
    /// by the scripted Claude (M/AppStore+DelegationSmoke.swift step 9).
```

After line 179 (`checks["removedAfterDiscard"] = true;`), insert:

```csharp

            // 9. A child of another agent: on a Codex pane in manual mode, whose own child could not change files, the worktree
            //    menu offers only a Claude child in plan mode, and its pick starts one, run by the scripted Claude. The menu is
            //    filled as the pane fills it on opening; the pick goes the way the menu item's send does. Claude's CLI is the
            //    smoke's scripted one, so the smoke treats it as ready.
            SetDelegationOn(true); smokeAgentsReady["claude"] = true;
            try
            {
                var crossRepo = Path.Combine(root, "cross"); Directory.CreateDirectory(crossRepo);
                await DelegationSmokeGit(crossRepo, "init", "-q", "-b", "main");
                File.WriteAllText(Path.Combine(crossRepo, "README.md"), "# Cross-agent smoke\n");
                await DelegationSmokeGit(crossRepo, "add", "-A"); await DelegationSmokeCommit(crossRepo, "Start");
                var crossWorkspace = await service.AddWorkspaceAsync(crossRepo);
                var codexPane = Wire.Id();
                await service.UpdateAsync(s => s with { Sessions = [.. s.Sessions, new RunSession { Id = codexPane, WorkspaceId = crossWorkspace.Id, Kind = "claude", Provider = "codex", Title = "Codex parent", Settings = new(PermissionMode: "manual") }], ActiveWorkspaceId = crossWorkspace.Id, ActiveSessionId = codexPane });
                Render();
                var codex = service.Snapshot.Sessions.First(s => s.Id == codexPane);
                service.RefreshWorktreeReadiness(codex);
                await DelegationSmokeUntil("the Codex pane's worktree menu", 30, () => service.WorktreeMenuOnly(codex, false));
                Require(service.WorktreeChildChoices(codex, false).Select(choice => (choice.Agent, choice.Mode, choice.Blocked))
                    .SequenceEqual([("claude", (string?)"plan", (DelegationChildBlock?)null), ("codex", null, DelegationChildBlock.NoMode), ("gemini", null, DelegationChildBlock.NoMode)]),
                    "The read-only Codex pane's menu does not offer only a Claude child in plan mode.");
                Require(MobilePane(codexPane).DelegationSmokeWorktreeMenu().Select(item => (AutomationProperties.GetAutomationId(item), item.IsEnabled))
                    .SequenceEqual([("composer-worktree-claude-" + codexPane, true), ("composer-worktree-codex-" + codexPane, false), ("composer-worktree-gemini-" + codexPane, false)]),
                    "The worktree menu's rows are not Claude (enabled), then Codex and Antigravity (greyed out with why).");
                MobilePane(codexPane).DelegationSmokeRunInNewWorktree("Smoke cross-agent child: write a note.", "claude");
                var crossChild = await DelegationSmokeWait("the Codex pane's Claude child", 60, () => delegation.File.Children.FirstOrDefault(child => child.ParentSessionId == codexPane && child.StartedByHuman == true && child.State != ChildState.Creating));
                Require(crossChild.State != ChildState.Failed && crossChild.Provider is null && crossChild.Agent == "claude" && crossChild.StartingMode == "plan"
                    && service.Snapshot.Sessions.FirstOrDefault(s => s.Id == crossChild.Id) is { Provider: "claude", ParentSessionId: var crossParent } && crossParent == codexPane
                    && service.Snapshot.Sessions.First(s => s.Id == codexPane).Provider == "codex" && service.DelegationChildren.FirstOrDefault(row => row.Id == crossChild.Id)?.Agent == "claude",
                    $"The Codex pane's pick of Claude did not start a Claude child ({DelegationNames.Of(crossChild.State)}).");
                checks["otherAgentChildStarted"] = new Dictionary<string, object?> { ["parent"] = "codex", ["child"] = "claude", ["mode"] = crossChild.StartingMode, ["state"] = DelegationNames.Of(crossChild.State) };
            }
            finally { smokeAgentsReady.TryRemove("claude", out _); SetDelegationOn(false); }
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash scripts/check-winui-compile.sh`
Expected: it passes.
Run: `node scripts/check-design-tokens.js`
Expected: the ratchets hold.
Run: `node scripts/check-locales.js --check`
Expected: it passes. Smoke code is exempt from the Korean-literal ratchet, and this step adds none.
Run: `grep -n "otherAgentChildStarted" scripts/test-native-windows.ps1 native/windows/MightyClaude.WinUI/MainWindow.DelegationSmoke.cs`
Expected: the key appears in both files.
The real pass is the Windows CI `native-windows` GUI smoke job, read after push by the person who pushes. This plan does not push.

- [ ] **Step 5: Commit**

```bash
git add native/windows/MightyClaude.WinUI/MainWindow.DelegationSmoke.cs scripts/test-native-windows.ps1
git commit -m "(windows) the delegation smoke starts a child of another agent: on a read-only Codex pane the worktree menu offers only a Claude child in plan mode, greys out Codex and Antigravity, and its pick starts that Claude child, checked as delegation.otherAgentChildStarted" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 12: Help, contracts and the parity row

**Files:**
- Modify: `docs/help/content/en/delegation.md`:
  - line 44: append one Windows bullet after it;
  - line 58: drop the Mac-only marker;
  - line 70: drop the Mac-only opening;
  - line 72: delete.
- Modify: `docs/help/content/ko/delegation.md`, `docs/help/content/zh/delegation.md`, `docs/help/content/ja/delegation.md`: the same four lines.
- Modify: `docs/help/content/{en,ko,zh,ja}/windows.md`, line 54 (the Mighty Orchestration row).
- Modify: `native/contracts/README.md`, lines 40-50 (the "On the Mac only …" paragraph).
- Modify: `docs/windows-parity.md`, line 78.

**Interfaces:**
- Consumes: the help tokens `{{platform:windows}}`, `{{ui:queue.worktree}}`, `{{ui:composer.effort.label}}`, `{{kbd:Ctrl+Enter}}`.
- Produces: help pages that describe the Windows menu.

- [ ] **Step 1: Write the failing test**

The help build and a grep are the test. Run:

```bash
cd /Users/young/Work/MightyClaude
grep -n "On Windows a child always runs its parent's agent\|Windows에서는 자식이 언제나\|在 Windows 上，子窗格总是\|Windowsでは、子はいつも" docs/help/content/*/delegation.md
```

- [ ] **Step 2: Run it and expect it to fail**

Expected: four lines are found (en, ko, zh and ja line 72). The pages still say Windows lacks the feature, which is the failure to fix.

- [ ] **Step 3: Implement**

In each `delegation.md`:
- **Delete line 72**, the `{{platform:windows}}` sentence, and the blank line that follows it.
- **Line 70, first words:**
  - en: `{{platform:mac}} On the Mac any agent can have a child of any of the three.` → `Any agent can have a child of any of the three.`
  - ko: `{{platform:mac}} Mac에서는 어느 에이전트든` → `어느 에이전트든`
  - zh: `{{platform:mac}} 在 Mac 上，任何代理都可以` → `任何代理都可以`
  - ja: `{{platform:mac}} Macでは、どのエージェントも` → `どのエージェントも`
- **Line 58:**
  - en: `(hand over a task; {{platform:mac}} on the Mac its \`agent\` can name` → `(hand over a task; its \`agent\` can name`
  - ko: `(맡기기. {{platform:mac}} Mac에서는 \`agent\`로` → `(맡기기. \`agent\`로`
  - zh: `（交出任务；{{platform:mac}} 在 Mac 上可以用 \`agent\`` → `（交出任务；可以用 \`agent\``
  - ja: `(任せる。{{platform:mac}} Macでは\`agent\`で` → `(任せる。\`agent\`で`
- **After line 44** (the `{{platform:mac}}` bullet about the menu), add one bullet:
  - en:

```markdown
- {{platform:windows}} To run the request with another agent, right-click the {{ui:queue.worktree}} button (or press Shift+F10 on it) and pick Claude, Codex or Antigravity. {{kbd:Ctrl+Enter}} and a plain click keep the pane's own agent. The rest works as on the Mac above: a greyed-out entry says why it cannot run, a child of another agent starts in the widest of its own modes that does nothing the pane's mode does not allow, with that agent's last-used model and {{ui:composer.effort.label}}, and text whose child cannot start goes back to the composer. Where only another agent's child fits, a click opens the menu and {{kbd:Ctrl+Enter}} queues the request as before.
```

  - ko:

```markdown
- {{platform:windows}} 요청을 다른 에이전트로 실행하려면 {{ui:queue.worktree}} 버튼을 오른쪽 클릭(또는 버튼에서 Shift+F10)해서 Claude, Codex, Antigravity 중에서 고릅니다. {{kbd:Ctrl+Enter}}와 그냥 클릭은 창과 같은 에이전트를 씁니다. 나머지는 위의 Mac과 같습니다. 흐리게 표시된 항목은 실행할 수 없는 까닭을 보여 주고, 다른 에이전트의 자식은 창의 모드가 허락하지 않는 일은 하지 않는 자기 모드 중 가장 넓은 모드에서 그 에이전트의 마지막 창 모델과 {{ui:composer.effort.label}}로 시작하며, 자식을 띄우지 못한 글은 입력창으로 돌아옵니다. 다른 에이전트의 자식만 가능한 곳에서는 클릭하면 메뉴가 열리고, {{kbd:Ctrl+Enter}}는 예전처럼 요청을 대기열에 넣습니다.
```

  - zh:

```markdown
- {{platform:windows}} 若要用另一个代理运行请求，请右键单击 {{ui:queue.worktree}} 按钮（或在按钮上按 Shift+F10），然后选择 Claude、Codex 或 Antigravity。{{kbd:Ctrl+Enter}} 和普通单击仍使用窗格自己的代理。其余与上面的 Mac 相同：灰色的项目会说明无法运行的原因；另一个代理的子窗格以它自己的模式中不做窗格模式不允许之事的最宽模式启动，并使用该代理最近使用的窗格的模型和{{ui:composer.effort.label}}；无法启动子窗格的文本会回到输入框。只有另一个代理的子窗格可用时，单击会打开菜单，{{kbd:Ctrl+Enter}} 则像以前一样把请求放入队列。
```

  - ja:

```markdown
- {{platform:windows}} 別のエージェントでリクエストを実行するには、{{ui:queue.worktree}}ボタンを右クリック(またはボタン上でShift+F10)して、Claude、Codex、Antigravityから選びます。{{kbd:Ctrl+Enter}}とふつうのクリックは、ペインと同じエージェントのままです。ほかは上のMacと同じです。グレーの項目には実行できない理由が出ます。別のエージェントの子は、ペインのモードが許さないことをしない自分のモードのうち最も広いモードで、そのエージェントの最後に使ったペインのモデルと{{ui:composer.effort.label}}で始まり、子を起動できなかったテキストは入力欄に戻ります。別のエージェントの子しか使えない場所では、クリックでメニューが開き、{{kbd:Ctrl+Enter}}は以前どおりリクエストをキューに入れます。
```

In each `windows.md` line 54, insert a sentence before the closing ` |` of the Mighty Orchestration row:
- en: ` Right-click the {{ui:queue.worktree}} button to run a request in a child of another agent ([Run in a new worktree](delegation.md#worktree)).`
- ko: ` {{ui:queue.worktree}} 버튼을 오른쪽 클릭하면 다른 에이전트의 자식에서 요청을 실행합니다([새 worktree에서 실행하기](delegation.md#worktree)).`
- zh: `右键单击 {{ui:queue.worktree}} 按钮，可在另一个代理的子窗格中运行请求（[在新 worktree 中运行](delegation.md#worktree)）。`
- ja: `{{ui:queue.worktree}}ボタンを右クリックすると、別のエージェントの子でリクエストを実行できます([新しいworktreeで実行](delegation.md#worktree))。`

In `native/contracts/README.md`, in the paragraph that starts `In both apps, a child runs its parent's agent by default,`:
- Replace `On the Mac only, a child may run another agent than its parent's:` with `In both apps, a child may run another agent than its parent's:`.
- Replace `worktree button has a menu of the three agents.` with `worktree button has a menu of the three agents (a right-click on Windows).`.
- Replace `notices and merges follow the parent's. Windows does not do this yet.` with `notices and merges follow the parent's. The capability and widest-mode tables are \`fixtures/delegation-modes.json\` ([Delegation modes](#delegation-modes-fixturesdelegation-modesjson)).`.

In `docs/windows-parity.md`, replace line 78 with:

```markdown
| 마이티 오케스트레이션: 다른 에이전트의 자식 (Claude·Codex·Antigravity 어느 부모든 셋 중 아무 에이전트의 자식) | `MightyCore/DelegationProviders.swift`(`widestChildMode`·`humanChildMode`·`humanChildChoices`·`availability`; 같은 에이전트는 예전처럼 `modeRank`로, 다른 에이전트는 `capabilities`(묻고 편집·묻지 않고 편집·묻지 않는 명령 실행·전체 권한, 묻지 않고 편집은 묻고 편집을 포함) 부분집합으로 비교), `DelegationDelegateTool.swift`(`delegate`의 선택 인자 `agent` — `claude`·`codex`·`antigravity`, `gemini`도 받음; 이유 코드 `agent_unknown`·`agent_unavailable`), `DelegationAppHost.swift`(사람이 띄운 다른 에이전트 자식은 그 에이전트의 마지막 창 모델·노력, 부모 보기, 스타일 없음), `MightyClaude/SessionPaneView.swift`(새 worktree 단추의 메뉴 — 오른쪽 클릭·Control-클릭, 막힌 항목은 이유와 함께 흐리게; 다른 에이전트를 고른 글을 못 띄우면 입력창으로 돌려줌), `AppStore+Delegation.swift`(`worktreeChildChoices`·`agentAvailabilityNow` — 앱이 이미 아는 CLI 검색 결과와 마지막 로그인 상태만 읽음); Mac 검사 `DelegationCrossAgentTests.swift`·`DelegationModesFixtureTests.swift`, 위임 스모크 단계 `otherAgentChildStarted`, `scripts/delegation-record.py` 픽스처 `cross-agent-children` | Core `DelegationProviders.cs`(`Capabilities`·`IsCovered`·`WidestChildMode`·`HumanChildMode`·`HumanChildChoices`·`Availability`·`Agent`), `DelegationDelegateTool.cs`(`delegate`의 `agent`, 이유 코드 `agent_unknown`·`agent_unavailable` — 세부 문구는 "this PC"), `DelegationAppHost.cs`(사람이 띄운 다른 에이전트 자식은 그 에이전트 마지막 창의 모델·노력), `DesktopService.Delegation.cs`(`WorktreeChildChoices`·`WorktreeMenuOnly`·`AgentAvailabilityNow` — 창이 이미 아는 CLI 검색 결과와 마지막 로그인 상태만 읽음); WinUI `MainWindow.WorktreeMenu.cs`(새 worktree 단추 오른쪽 클릭·Shift+F10 메뉴, 막힌 항목은 이유와 함께 흐리게, 다른 에이전트를 고른 글을 못 띄우면 입력창으로 돌려줌); 공유 픽스처 `native/contracts/fixtures/delegation-modes.json`; Core 검사 `DelegationCrossAgentVerification.cs`, 위임 스모크 단계 `otherAgentChildStarted` | 1단계 | Mac 먼저, Windows는 나중 (2026-10-16 사용자 결정); Windows 이식 2026-10-17 | 코드 구현; Core 검사·스모크 단계, 기기 미확인 |
```

- [ ] **Step 4: Run it and expect it to pass**

Run the Step 1 grep again.
Expected: no output.
Run: `node scripts/help/build.mjs --check`
Expected: it passes, with no unknown ui key, no particle error, and all four languages matching ko's structure.
Run: `node scripts/check-locales.js`, then `node scripts/check-locales.js --check`
Expected: both pass. The first rewrites `docs/i18n.md` if its tables changed.
Run: `bash scripts/check-style-freeze.sh`
Expected: it passes, since no style manifest or engine file was touched.

- [ ] **Step 5: Commit**

```bash
git add docs/help/content/en/delegation.md docs/help/content/ko/delegation.md docs/help/content/zh/delegation.md docs/help/content/ja/delegation.md docs/help/content/en/windows.md docs/help/content/ko/windows.md docs/help/content/zh/windows.md docs/help/content/ja/windows.md native/contracts/README.md docs/windows-parity.md docs/i18n.md
git commit -m "(windows) the help says Windows runs children of another agent too, with a right-click on the worktree button and delegate's agent, in all four languages; the contracts readme and the parity row say both apps do it" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Task 13: Whole-plan verification

**Files:** none changed. If a check fails, fix it in the task that owns the file, re-run that task's Step 4, and amend nothing (make a new commit).

**Interfaces:** consumes everything above; produces the evidence.

- [ ] **Step 1: Core.** Run CORE-RUN.
Expected:
- exactly the 9 FAIL lines of `/private/tmp/core-baseline-fails.txt`;
- `grep -c '^PASS delegation' /private/tmp/core-run.log` is the number in `/private/tmp/core-baseline-delegation-pass.txt` plus 14: the new registrations are 2 (Task 2) + 1 (Task 3) + 1 (Task 4) + 2 (Task 5) + 4 (Task 6) + 3 (Task 8) + 1 (Task 9). `grep -c 'DelegationCrossAgentVerification\.' native/windows/MightyClaude.Core.Tests/Verification.cs` prints `14`;
- `git diff -- native/windows/MightyClaude.Core.Tests/Verification.cs` is empty, so the wrapper was restored.
- [ ] **Step 2: WinUI.** Run `bash scripts/check-winui-compile.sh`. Expected: it passes.
- [ ] **Step 3: Mac.** Run MAC-RUN. Expected: every `Delegation*Tests` suite passes, `DelegationModesFixtureTests` and `DelegationCrossAgentTests` among them.
- [ ] **Step 4: Locales, tokens, styles, help.** Run:

```bash
cd /Users/young/Work/MightyClaude
node scripts/check-locales.js && node scripts/check-locales.js --check
node scripts/check-design-tokens.js
bash scripts/check-style-freeze.sh
node scripts/help/build.mjs --check
cd mobile && npx jest --silent
```

Expected: every command exits 0, and `git status --short` shows no change left by the first command.
- [ ] **Step 5: Hygiene.** Run `git diff main --stat` and `git diff main | grep -nE '/Users/|C:\\\\Users\\\\|@gbike|young'`.
Expected:
- The stat lists only the files in the File Structure table.
- The grep prints nothing: no personal paths or names in the diff.
- No `.skip`, `TODO` or `FIXME` was added (`git diff main | grep -nE '^\+.*(TODO|FIXME|\.skip\()'` prints nothing).

---

## Self-Review Checklist (spec section 1 → tasks)

| Spec bullet (docs/superpowers/specs/2026-10-17-windows-catch-up-design.md §1) | Task |
|---|---|
| `delegate` takes an optional `agent`: `claude`, `codex`, `antigravity` (alias `gemini`); the default is the caller's own agent | 2 (`Agent`), 4 (manifest, `ArgumentProblem`), 6 (`DelegateNow`) |
| An unknown name is refused with `agent_unknown` | 4 (enum, wire name, hint), 6 (refusal with detail), 8 (human send) |
| A missing or signed-out CLI is refused with `agent_unavailable`, naming the agent and pointing to Settings › CLI | 4 (`Unavailable`), 6 (refusal), 9 (`AgentAvailabilityNow` from the cached CLI status), 10 (`AgentAvailabilityFromCli`) |
| `list_children` and `child_status` include the agent | 4 |
| A child's capabilities are a subset of its parent's; four capabilities; `writesUnasked` covers `writesAsking` | 2 |
| Within one agent the rank comparison stays unchanged | 2 (`AgentsAndModesAcrossAgents`, the per-agent loop) |
| The capability and widest-child-mode tables move to `native/contracts/fixtures/delegation-modes.json`; Mac tests and Windows Core tests read it; Mac code checked against it | 1 (fixture, Mac suite, README), 2 (Windows check) |
| Ctrl+Enter or a click on the worktree button starts a child of the same agent, as today | 10 (click unchanged; `DeferBusyComposer` untouched), 8 (`agent: null`) |
| Right-click opens a flyout listing Claude, Codex, Antigravity; unusable entries disabled with the reason | 3 (choices), 9 (service), 10 (flyout, AutomationIds, `WorktreeChoiceTitle`), 11 (smoke checks the rows) |
| If a child of another agent fails to start, its text goes back to the composer | 3 (`ReturnsToComposer`), 10 (`ReturnToComposer`, `RunInNewWorktree`) |
| The child's own agent decides CLI arguments, writable root / `--add-dir`, MCP attachment, app auto-commit, the conflict request | Already keyed on the child's pane provider and `child.Agent` (`ProviderCatalog.Arguments`, `DelegationWorktreeRoot`, `AgentIOBinding`, `DelegationReports` → `ChildAutoCommit.Run(found.Agent)`, `AskChildToResolveConflict` → `CanCommit(pane.Provider…)`); proven across agents by 6 (`AClaudeParentsCodexChildGetsTheAppsCommit`, `ACodexParentsClaudeChildCommitsItsOwnWork`) |
| The parent's agent decides notices and `merge_human_only` | 5 (`FirstInput`/`HumanNote` name the parent), 6 (notice to the parent; Codex parent's `merge_human_only`) |
| A human-started child of another agent copies model and effort from the last-used pane of that agent | 5 (`ChildSession`, `AddingChild`), 9 (service end to end) |
| A child created by the agent gets that agent's defaults | 5 (`human: false` case) |
| Smoke step registered in `scripts/test-native-windows.ps1` | 11 |
| Help (`delegation.md` ×4, `windows.md`), `docs/windows-parity.md` row | 12 |
| Locales reuse `composer.worktreeMenu.*`, `delegation.human.error.*`, `delegation.human.returnedToComposer`; only Mac-specific texts get Windows twins | 7 |
| Verification: Core with the wrapper (9 Windows-only FAILs), `check-winui-compile`, Mac `Delegation*Tests`, `check-locales` (both modes), `check-design-tokens`, `check-style-freeze`, `help/build.mjs --check`, phone jest | Global Constraints (CORE-RUN, MAC-RUN), each task's Step 4, Task 13 |
