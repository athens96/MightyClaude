# Windows Phone Direct Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A phone on the same tailnet connects straight to a Windows host over Tailscale. It uses the relay's own handshake, encryption and auth, and falls back to the relay otherwise. The Windows app can add and remove one narrow Windows Firewall rule for this, behind one UAC prompt.

**Architecture:**
- **Address rule.** A pure `TailscaleAddresses.Select` picks this PC's Tailscale addresses: an adapter counts only if it carries an fd7a:115c:a1e0::/48 address. A shared fixture binds this rule to the Mac's.
- **Listener.** `MobileDirectListener` opens one `TcpListener` per address on port 43139. The app reads the WebSocket upgrade itself, then calls `WebSocket.CreateFromStream`.
- **Host.** `MobileRelayHost` hands the socket to the same `Connection` class that serves relay data sockets. The connection is refactored to take any `WebSocket`. It applies the Mac's limits, the generic `unauthorized` and `direct-` ids.
- **Advertising.** The host advertises `"direct"`, `auth_ok.direct`, `/m1/info` `direct` and the QR's `direct=`.
- **Firewall.** `WindowsFirewallRule` builds the fixed `netsh` argv. `WindowsFirewallRuleRunner` runs the query unelevated and the add/delete through `runas`, behind a seam that the tests and the smoke fill with a recorder.

**Tech Stack:**
- C# / .NET 10: `System.Net.Sockets`, `System.Net.WebSockets`, `System.Net.NetworkInformation`, `System.Diagnostics.Process`.
- WinUI 3, built in code.
- Swift Testing for the Mac fixture test.
- Node scripts for locales, design tokens and the help site.

## Global Constraints

**Never touch the real machine**
- Never launch the app, in either build. No `--smoke-test` run on the Mac; the GUI smoke runs only in Windows CI.
- Never run `netsh` or change the firewall during development. Tests and the smoke give `WindowsFirewallRuleRunner` a recording delegate that starts nothing.

**Elevation and the firewall rule**
- At run time, the app elevates exactly one program, `%SystemRoot%\System32\netsh.exe`, and only after a user click. The click is the firewall row's button, or a switch that turns the path off while the rule exists.
- The path to netsh comes from `Environment.SystemDirectory`. The verb `runas` appears in exactly one source file, `WindowsFirewallRule.cs`.
- The rule's argv comes only from constants, the validated exe path and the validated ports:
  - `name=Mighty Claude (Tailscale)`, `dir=in`, `action=allow`, `protocol=TCP`, `localport=<ports>`, `remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48`, `program=<app exe full path>`, `profile=any`.
  - The exe path must be a drive-letter absolute path ending in `.exe`, without `"`, `%`, control characters, `/` or a second `:`. No segment may be `.` or `..`.
  - Ports are 1024–65535.

**Direct path limits (the Mac's)**
- Port 43139 by default; the setting `directPort` takes 1024–65535. `directEnabled` defaults to true.
- At most 4 pending handshakes, and 2 per remote address.
- At most 36 connections across the listeners: 4 pending + 32 phones.
- Frames up to 1 MiB.
- A 10 s handshake deadline, which covers the HTTP upgrade too.
- 5 failed auths per remote address per 60 s. After that, new sockets from that address are reset unread.
- A direct refusal is always `{"type":"auth_error","reason":"unauthorized"}`.
- Peers outside 100.64.0.0/10 and fd7a:115c:a1e0::/48 are reset at accept.
- Direct connection ids start with `direct-`.
- Relay redials, relay outages and relay `connected`/`disconnected` notices never touch direct connections.

**Test seams**
- The loopback seam is `MobileRelayHost.SetDirectAddressSource`. It is `internal`, and only `MightyClaude.Core.Tests` sees it, through `InternalsVisibleTo`.
- The app only ever binds addresses from `TailscaleAddresses.Current()`.

**WinUI rules**
- The UI is built in code (no XAML).
- No Korean or other user-visible literal in `.cs`: words come from `Locale.Get`, through a Core `*Strings` class.
- No `FontSize`, `LineHeight` or `FontFamily` assignment. Use the `SettingsText`/`SettingsField` helpers.
- No raw spacing number ≥ 2 in `new Thickness(...)` or `Spacing =`. Use `DesignMetrics.Spacing.*`.
- Never drop a Task (CS4014). User actions go through `Act`/`SafeButton`.
- Check `IsLoaded` in `Unloaded`/async continuations.
- A smoke run (`options.SmokeTest`) never probes or changes the machine.

**Code rules**
- Put `[JsonIgnore]` on every computed property of a serialized record.
- `TreatWarningsAsErrors` is on (`native/windows/Directory.Build.props`), so nullable warnings fail the build.

**Addresses in code, fixtures and docs.** No personal or real IPs. Use only these fake ones, already in the Mac tests:
- `100.101.102.103`, `100.101.102.104`, `100.101.1.2`, `100.100.1.2`, `100.64.9.9`, `100.70.1.2`, `100.72.1.9`, `100.80.4.4`, `100.96.0.2`, `100.96.0.3`;
- `fd7a:115c:a1e0::1`, `::5`, `::9`, `::a`, `::b`, `::1234:5678`;
- the range-edge literals;
- `127.0.0.1` in tests.

**Commits**
- Each subject starts with `(windows) `.
- Every commit message ends with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Stage only the files the task names.

**Windows Core check baseline.** Run on this Mac with the wrapper below, the suite has exactly 9 Windows-only FAILs. A task passes when its new checks print PASS and the FAIL total stays 9.

## Commands used by every task

Create this helper once, outside the repository. If a step finds it missing, create it again. It wraps `Verification.Test` in try/catch so failures print `FAIL`, runs the Windows Core checks, and then restores the file. A task's registration lines stay.

```bash
cat > /private/tmp/mighty-core-checks.sh <<'SH'
#!/bin/bash
# Usage: bash /private/tmp/mighty-core-checks.sh '<extended regex of check names to show>'
# Runs every Windows Core check on this Mac with each failure printed (FAIL <name>: <reason>) instead of thrown,
# then puts Verification.Test back exactly as it was; registration lines a task added stay.
set -uo pipefail
cd /Users/young/Work/MightyClaude || exit 2
FILE=native/windows/MightyClaude.Core.Tests/Verification.cs
LOG=/private/tmp/mighty-core-checks.log
wrap()    { perl -pi -e 's/\{ await action\(\); passed\+\+; Console\.WriteLine\("PASS " \+ name\); \}/{ try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); } }/' "$FILE"; }
restore() { perl -pi -e 's/\{ try \{ await action\(\); passed\+\+; Console\.WriteLine\("PASS " \+ name\); \} catch \(Exception ex\) \{ Console\.WriteLine\("FAIL " \+ name \+ ": " \+ ex\.Message\); \} \}/{ await action(); passed++; Console.WriteLine("PASS " + name); }/' "$FILE"; }
wrap; trap restore EXIT
grep -q 'catch (Exception ex) { Console.WriteLine("FAIL "' "$FILE" || { echo "wrapper not applied"; exit 2; }
TMPDIR=/private/tmp/ dotnet run --project native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj > "$LOG" 2>&1
status=$?
grep -E "error CS[0-9]+" "$LOG" | sort -u | head -20
echo "exit: $status   PASS: $(grep -c '^PASS ' "$LOG")   FAIL: $(grep -c '^FAIL ' "$LOG")"
grep -E "^(PASS|FAIL) .*(${1:-.})" "$LOG"
SH
```

After every run, `git diff native/windows/MightyClaude.Core.Tests/Verification.cs` must show only the task's added `await Test(...)` lines. It must not show the try/catch.

Mac tests: `bash scripts/test-native-macos.sh --filter "<Suite|Suite>"`. The unfiltered suite deadlocks locally.

---

## File Structure

| File | Responsibility |
|---|---|
| `native/contracts/fixtures/tailscale-addresses.json` (create) | Platform-neutral cases of the address rule: `contains` lists, and `cases` with interface lists and the expected selection per platform (`darwin`, `win32`). |
| `native/contracts/README.md` (modify) | Describes the fixture. |
| `native/macos/Tests/MightyCoreTests/RemoteWorkspaceTests.swift` (modify) | `RemoteWorkspaceAddressFixtureTests`: the Mac's `TailscaleAddresses` checked against the fixture. |
| `native/windows/MightyClaude.Core/Remote/TailscaleAddresses.cs` (create) | `Contains`, `Select`, `Owned`, `Canonical`, `InterfaceAddresses`, `Current`. Part 3 reuses it. |
| `native/windows/MightyClaude.Core/Remote/MobileDirect.cs` (create) | Constants and limits, endpoints, the WebSocket upgrade, admission, `DirectAuthFailures`, `MobileDirectStatus` and its text. |
| `native/windows/MightyClaude.Core/Remote/MobileDirectListener.cs` (create) | `MobileDirectListener`: per-address `TcpListener`s, the shared 36-slot count, and `DirectIncoming`. |
| `native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs` (modify) | Settings fields, the `Connection` refactor to any `WebSocket`, the direct path lifecycle, admission, re-detection, `auth_ok.direct`, and the internal seam. |
| `native/windows/MightyClaude.Core/Remote/MobileDesktopRouter.cs` (modify) | The `"direct"` capability and `/m1/info` `direct`. |
| `native/windows/MightyClaude.Core/Remote/MobileIdentity.cs` (modify) | The QR's `direct=`. |
| `native/windows/MightyClaude.Core/WindowsFirewallRule.cs` (create) | Rule argv, validation, start infos, and `WindowsFirewallRuleRunner`. Part 3 reuses it with a port set. |
| `native/windows/MightyClaude.Core/Remote/MobileDirectStrings.cs` (create) | The Windows-only words (`mobileRemote.direct.windows.*`). |
| `native/windows/MightyClaude.Core/MightyClaude.Core.csproj` (modify) | `InternalsVisibleTo MightyClaude.Core.Tests`. |
| `native/windows/MightyClaude.Core.Tests/TailscaleAddressesVerification.cs` (create) | Fixture check plus a detection sanity check. |
| `native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs` (create) | Pure, listener, host, advertising, words and WinUI wiring checks. |
| `native/windows/MightyClaude.Core.Tests/WindowsFirewallVerification.cs` (create) | Argv, validation and runner checks; no netsh runs. |
| `native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj` (modify) | Embeds the fixture. |
| `native/windows/MightyClaude.Core.Tests/Verification.cs` (modify) | Registration lines. |
| `native/windows/MightyClaude.WinUI/MainWindow.MobileDirect.cs` (create) | Firewall state, runner, `ChangeFirewallRule`, `FirewallPorts`, and the smoke status override. |
| `native/windows/MightyClaude.WinUI/MainWindow.MobileRemote.cs` (modify) | Direct switch, port field and apply, status line, firewall row, and router wiring. |
| `native/windows/MightyClaude.WinUI/MainWindow.MobileDirectSmoke.cs` (create) | `RunMobileDirectSmoke`. |
| `native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs` (modify) | Registers the smoke step. |
| `scripts/test-native-windows.ps1` (modify) | Reads `mobileDirect.*`. |
| `locales/{ko,en,zh,ja}.json` + `mobile/src/locales/*` + `native/macos/Sources/MightyCore/Resources/Locales/*` + `docs/i18n.md` | 12 new `mobileRemote.direct.windows.*` keys. |
| `docs/relay.md`, `docs/mobile-remote.md`, `docs/windows-mobile-remote.md`, `docs/windows-parity.md`, `docs/help/content/{ko,en,zh,ja}/{phone,windows}.md` | Windows host notes. |

**Fixture decision.** The cases are platform-neutral interface lists. Each case carries `expected.darwin` and `expected.win32` (the `platform` values `/m1/info` already uses), and a per-platform `interfacePrefix` field is not used. Reasons:

1. Windows cannot keep the Mac's `utun` guard. Windows lets the user rename an adapter ("Tailscale" by default), and the Wintun adapter's interface type is not a named `NetworkInterfaceType`. So the fd7a:115c:a1e0::/48 address is the only reliable mark.
2. The Mac keeps its stricter `utun` guard from 4c13fce unchanged, so no Mac code changes.
3. With explicit expectations, both rules stay readable in one file, and drift on either side fails a test. A prefix field would hide a derivation in each reader. It also could not express "a Windows-named adapter is rejected on the Mac and accepted on Windows" without per-platform answers anyway.

The two platforms' answers differ in exactly two cases:
- the fd7a prefix on a non-tunnel interface;
- the Windows-named Tailscale adapter.

**MagicDNS.** The Windows host advertises addresses only, no MagicDNS name. The phone stores names but never dials them (docs/relay.md, step 1), so a name would add a `tailscale.exe` call with no effect.

---

### Task 1: Shared Tailscale address fixture, read by the Mac

**Files:**
- Create: `native/contracts/fixtures/tailscale-addresses.json`
- Modify: `native/contracts/README.md` (append a section at the end)
- Test: `native/macos/Tests/MightyCoreTests/RemoteWorkspaceTests.swift` (append a struct at the end of the file)

**Interfaces:**
- Consumes (Mac):
  - `TailscaleAddresses.contains(_ address: String) -> Bool`
  - `TailscaleAddresses.select(interfaces: [(name: String, address: String)]) -> [String]`
- Produces: the fixture file. Its shape:

  ```
  { comment, contains: { accepted: [String], rejected: [String] },
    cases: [{ name, interfaces: [{ name, addresses: [String] }],
              expected: { darwin: [String], win32: [String] } }] }
  ```

  Task 2 reads the same file.

- [ ] **Step 1: Write the failing test**

Append to `native/macos/Tests/MightyCoreTests/RemoteWorkspaceTests.swift`:

```swift

/// native/contracts/fixtures/tailscale-addresses.json, which the Windows Core checks read too
/// (TailscaleAddressesVerification): the same interface lists give each platform the addresses the
/// file says. The Mac also requires a `utun` name; Windows cannot (adapters are renamable), so the
/// two answers differ only where the file says so.
struct RemoteWorkspaceAddressFixtureTests {
    struct Fixture: Decodable { let contains: Contains; let cases: [Case] }
    struct Contains: Decodable { let accepted: [String]; let rejected: [String] }
    struct Case: Decodable { let name: String; let interfaces: [Interface]; let expected: [String: [String]] }
    struct Interface: Decodable { let name: String; let addresses: [String] }

    static let url: URL = {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("native/contracts/fixtures/tailscale-addresses.json")
    }()

    static func fixture() throws -> Fixture { try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: url)) }

    @Test func everyAddressIsInOrOutOfTheRangesAsTheFileSays() throws {
        let fixture = try Self.fixture()
        #expect(!fixture.contains.accepted.isEmpty && !fixture.contains.rejected.isEmpty)
        for address in fixture.contains.accepted { #expect(TailscaleAddresses.contains(address), "\(address)") }
        for address in fixture.contains.rejected { #expect(!TailscaleAddresses.contains(address), "\(address)") }
    }

    @Test func everyInterfaceListSelectsTheMacsExpectedAddresses() throws {
        let cases = try Self.fixture().cases
        #expect(cases.count >= 12)
        for item in cases {
            let read = item.interfaces.flatMap { interface in interface.addresses.map { (name: interface.name, address: $0) } }
            #expect(item.expected["darwin"] != nil && item.expected["win32"] != nil, "\(item.name) names both platforms")
            #expect(TailscaleAddresses.select(interfaces: read) == item.expected["darwin"], "\(item.name)")
        }
    }
}
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash scripts/test-native-macos.sh --filter "RemoteWorkspaceAddressFixture"`
Expected: FAIL. Both tests throw, because the file `tailscale-addresses.json` does not exist (`NSCocoaErrorDomain` "couldn't be opened").

- [ ] **Step 3: Create the fixture and its README section**

Create `native/contracts/fixtures/tailscale-addresses.json`:

```json
{
  "comment": "What counts as this computer's Tailscale address, for the Mac (MightyCore/Remote/TailscaleAddresses.swift, select(interfaces:)) and Windows (MightyClaude.Core/Remote/TailscaleAddresses.cs, Select). An interface counts only if it carries an fd7a:115c:a1e0::/48 address; then its 100.64.0.0/10 and fd7a:115c:a1e0::/48 addresses count, each once in its canonical spelling without %scope, IPv4 first, each family sorted ordinally. The Mac also requires the interface name to start with utun; Windows does not (adapters are renamable and Wintun's type is no named NetworkInterfaceType), so each case gives both platforms' answer. Addresses are fake.",
  "contains": {
    "accepted": ["100.64.0.0", "100.64.0.1", "100.100.100.100", "100.101.102.103", "100.127.255.255", "fd7a:115c:a1e0::1", "fd7a:115c:a1e0:ffff:ffff:ffff:ffff:ffff", "[fd7a:115c:a1e0:ab12:4843:cd96:6258:b240]", "FD7A:115C:A1E0::9", "::ffff:100.100.1.1"],
    "rejected": ["100.63.255.255", "100.128.0.0", "100.128.0.1", "10.0.0.1", "192.168.0.5", "192.168.1.2", "127.0.0.1", "0.0.0.0", "::1", "::", "fd7a:115c:a1e1::1", "fd7a:115c:a1df:ffff::1", "fe80::1", "fe80::1%utun4", "fd7a:115c:a1e0::1%utun4", "my-mac", "", "100.64.0"]
  },
  "cases": [
    {
      "name": "no interfaces",
      "interfaces": [],
      "expected": { "darwin": [], "win32": [] }
    },
    {
      "name": "a WARP-like tunnel with only 100.96/12 is another VPN",
      "interfaces": [
        { "name": "utun3", "addresses": ["100.96.0.2", "fe80::1"] },
        { "name": "Cloudflare WARP", "addresses": ["100.96.0.3", "fd00:abcd::2"] }
      ],
      "expected": { "darwin": [], "win32": [] }
    },
    {
      "name": "a hotspot's CGNAT address on Wi-Fi is not Tailscale",
      "interfaces": [
        { "name": "en0", "addresses": ["100.70.1.2", "192.168.43.20"] },
        { "name": "Wi-Fi", "addresses": ["100.72.1.9", "fe80::1c2b:3d4e:5f60:7182%11"] }
      ],
      "expected": { "darwin": [], "win32": [] }
    },
    {
      "name": "a CGNAT address alone is never taken, however the interface is named",
      "interfaces": [
        { "name": "utun7", "addresses": ["100.101.102.103"] },
        { "name": "Tailscale", "addresses": ["100.101.102.104"] }
      ],
      "expected": { "darwin": [], "win32": [] }
    },
    {
      "name": "the fd7a prefix on a non-tunnel interface",
      "interfaces": [
        { "name": "en0", "addresses": ["fd7a:115c:a1e0::9", "100.101.102.103"] }
      ],
      "expected": { "darwin": [], "win32": ["100.101.102.103", "fd7a:115c:a1e0::9"] }
    },
    {
      "name": "the IPv6 prefix alone is enough",
      "interfaces": [
        { "name": "utun7", "addresses": ["fd7a:115c:a1e0::9"] }
      ],
      "expected": { "darwin": ["fd7a:115c:a1e0::9"], "win32": ["fd7a:115c:a1e0::9"] }
    },
    {
      "name": "range edges on the Tailscale interface",
      "interfaces": [
        { "name": "utun4", "addresses": ["fd7a:115c:a1e0::1", "100.64.0.0", "100.127.255.255", "100.63.255.255", "100.128.0.0", "fd7a:115c:a1e1::1", "fd7a:115c:a1df:ffff::1", "fd7a:115c:a1e0:ffff:ffff:ffff:ffff:ffff"] }
      ],
      "expected": {
        "darwin": ["100.127.255.255", "100.64.0.0", "fd7a:115c:a1e0::1", "fd7a:115c:a1e0:ffff:ffff:ffff:ffff:ffff"],
        "win32": ["100.127.255.255", "100.64.0.0", "fd7a:115c:a1e0::1", "fd7a:115c:a1e0:ffff:ffff:ffff:ffff:ffff"]
      }
    },
    {
      "name": "an address read with its %scope is taken without it",
      "interfaces": [
        { "name": "utun7", "addresses": ["fd7a:115c:a1e0::9%utun7", "100.101.102.103"] }
      ],
      "expected": { "darwin": ["100.101.102.103", "fd7a:115c:a1e0::9"], "win32": ["100.101.102.103", "fd7a:115c:a1e0::9"] }
    },
    {
      "name": "Windows adapter names: loopback, Ethernet, CGNAT Wi-Fi and the Tailscale adapter with scoped addresses",
      "interfaces": [
        { "name": "Loopback Pseudo-Interface 1", "addresses": ["127.0.0.1", "::1"] },
        { "name": "Ethernet", "addresses": ["192.168.0.5", "fe80::1c2b:3d4e:5f60:7182%4"] },
        { "name": "Wi-Fi", "addresses": ["100.70.1.2"] },
        { "name": "Tailscale", "addresses": ["fe80::abcd%7", "fd7a:115c:a1e0::1234:5678%7", "100.101.102.103"] }
      ],
      "expected": { "darwin": [], "win32": ["100.101.102.103", "fd7a:115c:a1e0::1234:5678"] }
    },
    {
      "name": "loopback and LAN are never taken",
      "interfaces": [
        { "name": "lo0", "addresses": ["127.0.0.1", "::1"] },
        { "name": "en0", "addresses": ["192.168.0.5", "fe80::1c2b:3d4e:5f60:7182%en0", "100.70.1.2"] },
        { "name": "utun2", "addresses": ["100.96.0.2", "fd00:abcd::2"] },
        { "name": "utun3", "addresses": ["10.8.0.2"] },
        { "name": "utun4", "addresses": ["fe80::abcd%utun4", "fd7a:115c:a1e0::1234:5678", "100.101.102.103", "100.128.0.1", "10.0.0.9"] }
      ],
      "expected": { "darwin": ["100.101.102.103", "fd7a:115c:a1e0::1234:5678"], "win32": ["100.101.102.103", "fd7a:115c:a1e0::1234:5678"] }
    },
    {
      "name": "each address once, in its canonical spelling, IPv4 first, each family sorted",
      "interfaces": [
        { "name": "utun4", "addresses": ["fe80::1", "fd7a:115c:a1e0:0::5", "100.101.1.2", "192.168.1.3", "FD7A:115C:A1E0::5", "100.101.1.2", "127.0.0.1", "100.64.9.9", "fd7a:115c:a1e0::b", "100.100.1.2", "fd7a:115c:a1e0::a"] }
      ],
      "expected": {
        "darwin": ["100.100.1.2", "100.101.1.2", "100.64.9.9", "fd7a:115c:a1e0::5", "fd7a:115c:a1e0::a", "fd7a:115c:a1e0::b"],
        "win32": ["100.100.1.2", "100.101.1.2", "100.64.9.9", "fd7a:115c:a1e0::5", "fd7a:115c:a1e0::a", "fd7a:115c:a1e0::b"]
      }
    },
    {
      "name": "WARP beside Tailscale: only Tailscale's tunnel counts",
      "interfaces": [
        { "name": "utun3", "addresses": ["100.96.0.2", "fe80::1"] },
        { "name": "utun7", "addresses": ["100.80.4.4"] },
        { "name": "utun4", "addresses": ["100.101.1.2", "fd7a:115c:a1e0::5"] }
      ],
      "expected": { "darwin": ["100.101.1.2", "fd7a:115c:a1e0::5"], "win32": ["100.101.1.2", "fd7a:115c:a1e0::5"] }
    }
  ]
}
```

Append to `native/contracts/README.md`:

```markdown

## Tailscale addresses (`fixtures/tailscale-addresses.json`)

The rule that decides which of this computer's addresses are Tailscale's, shared by the phone's direct path and the remote workspaces. An interface counts only if it carries an address in fd7a:115c:a1e0::/48, which only Tailscale hands out; its addresses in 100.64.0.0/10 and that range then count, each once in its canonical spelling without `%scope`, IPv4 first and each family sorted ordinally. A carrier-grade NAT can put 100.64.0.0/10 on Wi-Fi and other VPNs (Cloudflare WARP's 100.96.0.0/12) on their own tunnel, so the CGNAT range alone never counts. `contains` lists addresses in and out of the two ranges (brackets accepted, `%scope` refused, an IPv4-mapped IPv6 address counts as its IPv4). `cases` are interface lists with each platform's answer: the Mac (`darwin`, `MightyCore/Remote/TailscaleAddresses.swift`, `RemoteWorkspaceAddressFixtureTests`) also requires a `utun` name; Windows (`win32`, `MightyClaude.Core/Remote/TailscaleAddresses.cs`, `TailscaleAddressesVerification`) cannot, as adapters are renamable and the Wintun adapter's type is no named `NetworkInterfaceType`. The two answers differ only in the cases that say so. Every address in the file is fake.
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash scripts/test-native-macos.sh --filter "RemoteWorkspaceAddressFixture"`
Expected: PASS for both tests.

Run: `bash scripts/test-native-macos.sh --filter "RemoteWorkspaceAddressTests"`
Expected: PASS. These are the existing address tests, unchanged.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/contracts/fixtures/tailscale-addresses.json native/contracts/README.md native/macos/Tests/MightyCoreTests/RemoteWorkspaceTests.swift
git commit -m "(windows) the Tailscale address rule's cases move to a shared fixture with each platform's answer, and the Mac's tests read it" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Windows `TailscaleAddresses`

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/TailscaleAddresses.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj` (embed the fixture)
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (registration)
- Test: `native/windows/MightyClaude.Core.Tests/TailscaleAddressesVerification.cs` (create)

**Interfaces:**
- Consumes: `native/contracts/fixtures/tailscale-addresses.json` (Task 1).
- Produces (`namespace MightyClaude.Core`):
  - `public static class TailscaleAddresses`
  - `public static bool Contains(string address)`
  - `public static bool InTailscaleIPv6Range(string address)`
  - `public static IReadOnlyList<string> Select(IEnumerable<(string InterfaceName, string Address)> interfaces)`
  - `public static IReadOnlyList<(string InterfaceName, string Address)> Owned(IEnumerable<(string InterfaceName, string Address)> interfaces)`
  - `public static IReadOnlyList<(string InterfaceName, string Address)> InterfaceAddresses()`
  - `public static IReadOnlyList<string> Current()`
  - `public static string Canonical(string address)` (drops `%scope` and brackets, lowercases, and gives the IPv6 compressed form)

- [ ] **Step 1: Write the failing test**

Create `native/windows/MightyClaude.Core.Tests/TailscaleAddressesVerification.cs`:

```csharp
using System.Text.Json;
using MightyClaude.Core;

// native/contracts/fixtures/tailscale-addresses.json is the address rule the Mac (RemoteWorkspaceAddressFixtureTests) checks
// too; Windows reads each case's `win32` answer.
internal static class TailscaleAddressesVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    internal static Task SharedFixture()
    {
        using var stream = typeof(TailscaleAddressesVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.TailscaleAddresses.json")!;
        using var fixture = JsonDocument.Parse(stream); var root = fixture.RootElement;
        foreach (var address in root.GetProperty("contains").GetProperty("accepted").EnumerateArray())
            Check(TailscaleAddresses.Contains(address.GetString()!), "in Tailscale's ranges: " + address.GetString());
        foreach (var address in root.GetProperty("contains").GetProperty("rejected").EnumerateArray())
            Check(!TailscaleAddresses.Contains(address.GetString()!), "outside Tailscale's ranges: " + address.GetString());
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Check(cases.Length >= 12, "the shared fixture was not loaded");
        foreach (var item in cases)
        {
            var read = item.GetProperty("interfaces").EnumerateArray()
                .SelectMany(adapter => adapter.GetProperty("addresses").EnumerateArray().Select(address => (InterfaceName: adapter.GetProperty("name").GetString()!, Address: address.GetString()!)))
                .ToArray();
            var expected = item.GetProperty("expected").GetProperty("win32").EnumerateArray().Select(address => address.GetString()!).ToArray();
            var actual = TailscaleAddresses.Select(read);
            Check(actual.SequenceEqual(expected), item.GetProperty("name").GetString() + ": got [" + string.Join(", ", actual) + "]");
        }
        return Task.CompletedTask;
    }

    internal static Task DetectionOffersOnlyTailscaleAddresses()
    {
        var read = TailscaleAddresses.InterfaceAddresses();
        foreach (var (name, address) in TailscaleAddresses.Owned(read))
            Check(TailscaleAddresses.Contains(address) && read.Any(entry => entry.InterfaceName == name && TailscaleAddresses.InTailscaleIPv6Range(TailscaleAddresses.Canonical(entry.Address))),
                "detection offered " + address + " on " + name);
        Check(TailscaleAddresses.Current().All(TailscaleAddresses.Contains), "whatever this machine has, only Tailscale addresses come back");
        Check(TailscaleAddresses.Canonical("FD7A:115C:A1E0:0::5%12") == "fd7a:115c:a1e0::5" && TailscaleAddresses.Canonical("[fd7a:115c:a1e0::5]") == "fd7a:115c:a1e0::5"
            && TailscaleAddresses.Canonical("100.101.1.2") == "100.101.1.2", "one spelling per address");
        return Task.CompletedTask;
    }
}
```

In `native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj`, add inside the `ItemGroup` that holds `mobile-structure.json`, after that line:

```xml
    <EmbeddedResource Include="../../contracts/fixtures/tailscale-addresses.json" LogicalName="MightyClaude.Core.Tests.TailscaleAddresses.json" />
```

In `native/windows/MightyClaude.Core.Tests/Verification.cs`, add right after the line `await Test("Mobile host: a display that disappears moves its viewers to the main one", MobileHostVerification.ADisplayThatDisappearsMovesItsViewersToTheMainOne);`:

```csharp
        await Test("tailscale addresses: the shared fixture's cases", TailscaleAddressesVerification.SharedFixture);
        await Test("tailscale addresses: detection offers only Tailscale addresses", TailscaleAddressesVerification.DetectionOffersOnlyTailscaleAddresses);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash /private/tmp/mighty-core-checks.sh 'tailscale addresses'`
Expected: the build fails with `error CS0103: The name 'TailscaleAddresses' does not exist in the current context`.

- [ ] **Step 3: Implement**

Create `native/windows/MightyClaude.Core/Remote/TailscaleAddresses.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MightyClaude.Core;

/// <summary>
/// This PC's Tailscale addresses (M/MightyCore/Remote/TailscaleAddresses.swift), read from its adapters without the Tailscale CLI.
/// Tailscale gives every device an IPv4 address in 100.64.0.0/10 and an IPv6 address in fd7a:115c:a1e0::/48. An adapter counts
/// as Tailscale's only when it carries an address in that IPv6 range, and then its addresses in both ranges count: a carrier-grade
/// NAT can hand Wi-Fi a 100.64/10 address and other VPNs (Cloudflare WARP's 100.96.0.0/12) put one on their own adapter. Unlike the
/// Mac, the adapter's name is not part of the rule: Windows lets the user rename an adapter, and the Wintun adapter's type is no
/// named NetworkInterfaceType. The cases are native/contracts/fixtures/tailscale-addresses.json, which both apps read.
/// </summary>
public static class TailscaleAddresses
{
    private static readonly byte[] Prefix = [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0];

    /// <summary>Whether <paramref name="address"/> (an IPv4 or IPv6 literal, optionally in brackets) is in one of Tailscale's two ranges. An IPv4-mapped IPv6 address counts as its IPv4; a <c>%scope</c> never counts.</summary>
    public static bool Contains(string address)
    {
        var value = address.Trim('[', ']').ToLowerInvariant();
        if (!value.Contains(':')) return IPv4(value) is { } v4 && InCgnat(v4[0], v4[1]);
        if (value.Contains('%') || !IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (ip.IsIPv4MappedToIPv6) { var mapped = ip.MapToIPv4().GetAddressBytes(); return InCgnat(mapped[0], mapped[1]); }
        return ip.GetAddressBytes().AsSpan(0, 6).SequenceEqual(Prefix);
    }

    /// <summary>fd7a:115c:a1e0::/48, which only Tailscale hands out (no <c>%scope</c>).</summary>
    public static bool InTailscaleIPv6Range(string address)
    {
        var value = address.Trim('[', ']').ToLowerInvariant();
        return value.Contains(':') && !value.Contains('%') && IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            && !ip.IsIPv4MappedToIPv6 && ip.GetAddressBytes().AsSpan(0, 6).SequenceEqual(Prefix);
    }

    /// <summary>The Tailscale addresses among <paramref name="interfaces"/> (adapter name and address, as read). Pure, so the rule is checked without this PC's adapters.</summary>
    public static IReadOnlyList<string> Select(IEnumerable<(string InterfaceName, string Address)> interfaces) => [.. Owned(interfaces).Select(entry => entry.Address)];

    /// <summary><see cref="Select"/> with each address's adapter: each address once in its canonical spelling, IPv4 first, each family sorted ordinally.</summary>
    public static IReadOnlyList<(string InterfaceName, string Address)> Owned(IEnumerable<(string InterfaceName, string Address)> interfaces)
    {
        var read = interfaces.Select(entry => (InterfaceName: entry.InterfaceName, Address: Unscoped(entry.Address))).ToArray();
        var tailnet = read.Where(entry => InTailscaleIPv6Range(entry.Address)).Select(entry => entry.InterfaceName).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = read.Where(entry => tailnet.Contains(entry.InterfaceName) && Contains(entry.Address))
            .Select(entry => (InterfaceName: entry.InterfaceName, Address: Canonical(entry.Address)))
            .Where(entry => seen.Add(entry.Address)).ToArray();
        return [.. found.Where(entry => !entry.Address.Contains(':')).OrderBy(entry => entry.Address, StringComparer.Ordinal),
                .. found.Where(entry => entry.Address.Contains(':')).OrderBy(entry => entry.Address, StringComparer.Ordinal)];
    }

    /// <summary>Every up adapter's unicast addresses, as Windows reports them; empty when they cannot be read.</summary>
    public static IReadOnlyList<(string InterfaceName, string Address)> InterfaceAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Select(unicast => (InterfaceName: adapter.Name, Address: unicast.Address.ToString())))];
        }
        catch (NetworkInformationException) { return []; }
    }

    /// <summary>The addresses this PC has on Tailscale now; empty while Tailscale is off.</summary>
    public static IReadOnlyList<string> Current() => Select(InterfaceAddresses());

    /// <summary>One spelling per address: no brackets or <c>%scope</c>, lowercase, IPv6 compressed.</summary>
    public static string Canonical(string address)
    {
        var value = Unscoped(address).Trim('[', ']').ToLowerInvariant();
        return value.Contains(':') && IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? ip.ToString() : value;
    }

    private static string Unscoped(string address) => address.Split('%', 2)[0];
    private static bool InCgnat(byte first, byte second) => first == 100 && second is >= 64 and <= 127;

    /// <summary>Four dotted decimal parts, 0-255, no leading zeros: the strict form (IPAddress.TryParse also takes "100.64.0").</summary>
    private static byte[]? IPv4(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 4) return null;
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) || part.Length > 1 && part[0] == '0') return null;
            var number = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number > 255) return null;
            bytes[i] = (byte)number;
        }
        return bytes;
    }
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-core-checks.sh 'tailscale addresses'`
Expected: both `PASS tailscale addresses: …` lines, and `FAIL: 9`.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/windows/MightyClaude.Core/Remote/TailscaleAddresses.cs native/windows/MightyClaude.Core.Tests/TailscaleAddressesVerification.cs native/windows/MightyClaude.Core.Tests/MightyClaude.Core.Tests.csproj native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) this PC's Tailscale addresses are the adapters carrying fd7a:115c:a1e0::/48 and their two ranges' addresses, checked against the Mac's shared cases" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `MobileDirect`: limits, endpoints, upgrade, admission and status

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/MobileDirect.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (registration)
- Test: `native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs` (create)

**Interfaces:**
- Consumes: `TailscaleAddresses.Contains(string)` (Task 2), `Locale.Get(string, IReadOnlyDictionary<string,string>?)`.
- Produces (`namespace MightyClaude.Core`):
  - `public static class MobileDirect` with these constants:
    - `DefaultPort = 43139`, `MaximumFrameBytes = 1048576`
    - `MaximumUnauthenticated = 4`, `MaximumUnauthenticatedPerPeer = 2`
    - `MaximumClients = 32`, `ConnectionLimit = 36`
    - `MaximumAuthFailures = 5`, `MaximumEndpoints = 8`, `MaximumRequestHeadBytes = 8192`
    - `ConnectionPrefix = "direct-"`, `GenericRefusal = "unauthorized"`
  - and these static readonly `TimeSpan`s: `AuthFailureWindow` (60 s), `HandshakeDeadline` (10 s), `RedetectInterval` (15 s).
  - `MobileDirect` methods:
    - `bool ValidPort(int)`
    - `bool IsDirect(string connectionId)`
    - `string Endpoint(string host, int port)`
    - `IReadOnlyList<string> Advertised(IEnumerable<(string Host, int Port)> bound, bool allowAnyHost = false)`
    - `string AcceptKey(string key)`
    - `string? UpgradeResponse(string head)`
    - `Task<WebSocket?> AcceptWebSocketAsync(Stream stream, CancellationToken token)`
    - `bool Admit(string? peer, bool allowAnyPeer, int clients, IReadOnlyCollection<string> pendingPeers, int recentFailures)`
  - `public sealed class DirectAuthFailures`:
    - `int Recent(string peer, DateTimeOffset now)`
    - `void Record(string peer, DateTimeOffset now)`
    - `int Peers { get; }`
  - `public sealed record MobileDirectStatus(string State, IReadOnlyList<string> Endpoints, string? Error = null)`:
    - `State` is one of `off`, `disabled`, `notFound`, `listening`, `failed`.
    - `static readonly MobileDirectStatus Off`
    - `[JsonIgnore] bool Listening`
  - `public static class MobileDirectStatusText` with `string Detail(MobileDirectStatus status)`.

- [ ] **Step 1: Write the failing test**

Create `native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs`:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

// The phone's direct path to a Windows host (docs/relay.md, "Tailscale 직접 연결"; spec
// docs/superpowers/specs/2026-10-17-windows-catch-up-design.md section 2). Loopback stands in for Tailscale only through
// the host's internal SetDirectAddressSource seam; nothing here runs netsh or binds a real Tailscale address.
internal static class MobileDirectVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    internal static Task EndpointsUpgradeAdmissionAndStatus()
    {
        // Endpoints: host:port, IPv6 in brackets; only Tailscale addresses, each once, at most eight.
        Check(MobileDirect.Endpoint("100.101.102.103", 43139) == "100.101.102.103:43139" && MobileDirect.Endpoint("fd7a:115c:a1e0::1", 43139) == "[fd7a:115c:a1e0::1]:43139", "endpoints are host:port, IPv6 in brackets");
        Check(MobileDirect.Advertised([("100.101.102.103", 43139), ("fd7a:115c:a1e0::1", 43139), ("192.168.0.5", 43139), ("100.101.102.103", 43139)])
            .SequenceEqual(["100.101.102.103:43139", "[fd7a:115c:a1e0::1]:43139"]), "only Tailscale endpoints, each once");
        Check(MobileDirect.Advertised([("127.0.0.1", 50000)], allowAnyHost: true).SequenceEqual(["127.0.0.1:50000"]) && MobileDirect.Advertised([("127.0.0.1", 50000)]).Count == 0, "loopback is advertised only through the seam");
        Check(MobileDirect.Advertised(Enumerable.Range(1, 12).Select(i => ("100.101.0." + i.ToString(CultureInfo.InvariantCulture), 43139))).Count == MobileDirect.MaximumEndpoints, "at most eight endpoints");
        Check(MobileDirect.ValidPort(1024) && MobileDirect.ValidPort(65535) && !MobileDirect.ValidPort(1023) && !MobileDirect.ValidPort(65536) && !MobileDirect.ValidPort(0), "ports are 1024-65535");
        Check(MobileDirect.IsDirect("direct-0f8fad5b-d9cb-469f-a165-70867728950e") && !MobileDirect.IsDirect("0f8fad5b-d9cb-469f-a165-70867728950e"), "direct ids start with direct-");
        Check(MobileDirect.DefaultPort == 43139 && MobileDirect.ConnectionLimit == 36 && MobileDirect.MaximumFrameBytes == 1024 * 1024 && MobileDirect.HandshakeDeadline == TimeSpan.FromSeconds(10)
            && MobileDirect.AuthFailureWindow == TimeSpan.FromSeconds(60) && MobileDirect.RedetectInterval == TimeSpan.FromSeconds(15) && MobileDirect.GenericRefusal == "unauthorized", "the Mac's limits");

        // The phone's upgrade (RFC 6455 section 1.3's sample key): GET /ws, HTTP/1.1, websocket, version 13, a 16-byte key.
        Check(MobileDirect.AcceptKey("dGhlIHNhbXBsZSBub25jZQ==") == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", "the accept key");
        const string request = "GET /ws?v=1 HTTP/1.1\r\nHost: 100.101.102.103:43139\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        Check(MobileDirect.UpgradeResponse(request) == "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n", "a phone's upgrade is accepted");
        Check(MobileDirect.UpgradeResponse(request.Replace("Connection: Upgrade", "Connection: keep-alive, Upgrade").Replace("Upgrade: websocket", "upgrade: WebSocket")) is not null, "header names and tokens are case-insensitive");
        foreach (var bad in new[]
                 {
                     request.Replace("GET /ws?v=1", "GET /other"), request.Replace("GET", "POST"), request.Replace("HTTP/1.1", "HTTP/1.0"),
                     request.Replace("Sec-WebSocket-Version: 13", "Sec-WebSocket-Version: 8"), request.Replace("Upgrade: websocket\r\n", ""),
                     request.Replace("dGhlIHNhbXBsZSBub25jZQ==", "c2hvcnQ="), request.Replace("Connection: Upgrade", "Connection: close"),
                 })
            Check(MobileDirect.UpgradeResponse(bad) is null, "a refused upgrade: " + bad.Split("\r\n")[0]);

        // Admission at accept, before anything is read.
        const string tailnet = "100.101.102.103";
        Check(MobileDirect.Admit(tailnet, false, 0, [], 0), "a tailnet peer gets a slot");
        Check(!MobileDirect.Admit("192.168.0.5", false, 0, [], 0) && !MobileDirect.Admit("127.0.0.1", false, 0, [], 0) && !MobileDirect.Admit(null, false, 0, [], 0), "a peer outside the ranges is dropped");
        Check(MobileDirect.Admit("127.0.0.1", true, 0, [], 0), "loopback only through the seam");
        Check(!MobileDirect.Admit(tailnet, false, MobileDirect.MaximumClients, [], 0), "a full phone table admits none");
        Check(!MobileDirect.Admit(tailnet, false, 4, ["100.64.0.1", "100.64.0.2", "100.64.0.3", "100.64.0.4"], 0), "four handshakes at once");
        Check(!MobileDirect.Admit(tailnet, false, 2, [tailnet, tailnet], 0) && MobileDirect.Admit(tailnet, false, 2, [tailnet, "100.64.0.1"], 0), "two handshakes per address");
        Check(!MobileDirect.Admit(tailnet, false, 0, [], MobileDirect.MaximumAuthFailures) && MobileDirect.Admit(tailnet, false, 0, [], MobileDirect.MaximumAuthFailures - 1), "five failed auths shut the address out");

        // Failed auths per address inside 60 s.
        var failures = new DirectAuthFailures(); var start = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 5; i++) failures.Record(tailnet, start.AddSeconds(i));
        Check(failures.Recent(tailnet, start.AddSeconds(10)) == 5 && failures.Recent("100.64.0.1", start) == 0, "failures count per address");
        Check(failures.Recent(tailnet, start.AddSeconds(62)) == 2, "a failure 60 s old no longer counts");
        Check(failures.Recent(tailnet, start.AddSeconds(70)) == 0 && failures.Peers == 0, "an address with no recent failure is forgotten");
        for (var i = 0; i < 256; i++) failures.Record("100.65." + (i / 256).ToString(CultureInfo.InvariantCulture) + "." + (i % 256).ToString(CultureInfo.InvariantCulture), start);
        failures.Record("100.66.0.1", start.AddSeconds(61));
        Check(failures.Peers == 1, "a full table drops the addresses whose failures are all old");

        // The status line says what the Mac's says.
        Check(MobileDirectStatusText.Detail(MobileDirectStatus.Off) == "", "off says nothing");
        Check(MobileDirectStatusText.Detail(new("disabled", [])) == Locale.Get("mobileRemote.direct.status.off"), "the switch off");
        Check(MobileDirectStatusText.Detail(new("notFound", [])) == Locale.Get("mobileRemote.direct.status.notFound"), "no Tailscale");
        Check(MobileDirectStatusText.Detail(new("listening", ["100.101.102.103:43139", "[fd7a:115c:a1e0::1]:43139"]))
            == Locale.Get("mobileRemote.direct.status.listeningTemplate", new Dictionary<string, string> { ["addresses"] = "100.101.102.103:43139, [fd7a:115c:a1e0::1]:43139" }), "the endpoints listened on");
        Check(MobileDirectStatusText.Detail(new("failed", [], "in use")) == Locale.Get("mobileRemote.direct.status.failedTemplate", new Dictionary<string, string> { ["error"] = "in use" }), "why binding failed");
        Check(new MobileDirectStatus("listening", ["x"]).Listening && !MobileDirectStatus.Off.Listening, "Listening is computed");
        Check(JsonSerializer.Serialize(MobileDirectStatus.Off, Wire.Json) == "{\"state\":\"off\",\"endpoints\":[]}", "the computed property is never serialized");
        return Task.CompletedTask;
    }
}
```

In `Verification.cs`, add after the two `tailscale addresses:` lines from Task 2:

```csharp
        await Test("mobile direct: endpoints, upgrade, admission and status", MobileDirectVerification.EndpointsUpgradeAdmissionAndStatus);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct'`
Expected: the build fails with `error CS0103: The name 'MobileDirect' does not exist in the current context`. `DirectAuthFailures`, `MobileDirectStatus` and `MobileDirectStatusText` are missing the same way.

- [ ] **Step 3: Implement**

Create `native/windows/MightyClaude.Core/Remote/MobileDirect.cs`:

```csharp
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>
/// The direct phone path (docs/relay.md, "Tailscale 직접 연결"; M/MightyCore/Remote/MobileDirect.swift): a phone on the same tailnet
/// opens its data socket straight to this PC instead of to the relay, at <c>ws://&lt;endpoint&gt;/ws?v=1</c>. Everything after the
/// upgrade is the relay path unchanged: the same hello/ready handshake, ChaCha20-Poly1305 frames and auth.
/// </summary>
public static class MobileDirect
{
    /// <summary>The listener's port unless Settings changes it.</summary>
    public const int DefaultPort = 43139;
    /// <summary>The relay's own frame limit, kept for the socket that replaces it.</summary>
    public const int MaximumFrameBytes = 1024 * 1024;
    /// <summary>Phones inside the handshake on the direct path, counted apart from the relay's.</summary>
    public const int MaximumUnauthenticated = 4;
    /// <summary>Of those, at most this many from one remote address.</summary>
    public const int MaximumUnauthenticatedPerPeer = 2;
    /// <summary>The phone table, shared with the relay path (docs/relay.md: 32 per host).</summary>
    public const int MaximumClients = 32;
    /// <summary>Connections the listeners hold together: four in their handshake and the 32 phones.</summary>
    public const int ConnectionLimit = MaximumUnauthenticated + MaximumClients;
    /// <summary>Failed authentications one address may have inside <see cref="AuthFailureWindow"/> before its new sockets are reset unread.</summary>
    public const int MaximumAuthFailures = 5;
    public static readonly TimeSpan AuthFailureWindow = TimeSpan.FromSeconds(60);
    /// <summary>From the socket to auth_ok, the HTTP upgrade included.</summary>
    public static readonly TimeSpan HandshakeDeadline = TimeSpan.FromSeconds(10);
    /// <summary>How often the addresses are read again while the path is on, on top of every address change Windows reports.</summary>
    public static readonly TimeSpan RedetectInterval = TimeSpan.FromSeconds(15);
    /// <summary>At most this many endpoints travel in a QR, an auth_ok or /m1/info.</summary>
    public const int MaximumEndpoints = 8;
    /// <summary>The phone's upgrade request, headers included.</summary>
    public const int MaximumRequestHeadBytes = 8192;
    public const string ConnectionPrefix = "direct-";
    /// <summary>A direct refusal says one word, so a tailnet peer learns nothing of which ids or keys this PC knows.</summary>
    public const string GenericRefusal = "unauthorized";
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static bool ValidPort(int port) => port is >= 1024 and <= 65535;
    public static bool IsDirect(string connectionId) => connectionId.StartsWith(ConnectionPrefix, StringComparison.Ordinal);

    /// <summary><c>host:port</c>, an IPv6 literal in brackets.</summary>
    public static string Endpoint(string host, int port) => (host.Contains(':') ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the host advertises: the bound addresses with their ports, Tailscale addresses only (any host through the seam), each once, at most eight.</summary>
    public static IReadOnlyList<string> Advertised(IEnumerable<(string Host, int Port)> bound, bool allowAnyHost = false)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. bound.Where(entry => allowAnyHost || TailscaleAddresses.Contains(entry.Host)).Select(entry => Endpoint(entry.Host, entry.Port)).Where(seen.Add).Take(MaximumEndpoints)];
    }

    public static string AcceptKey(string key) => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

    /// <summary>
    /// The 101 for a phone's upgrade request head (up to and with its blank line), or null: <c>GET /ws</c> (its query is not read)
    /// over HTTP/1.1, <c>Upgrade: websocket</c>, <c>Connection</c> carrying <c>Upgrade</c>, version 13 and a 16-byte key. Nothing
    /// else is negotiated: no subprotocol, no extension.
    /// </summary>
    public static string? UpgradeResponse(string head)
    {
        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" || request[1].Split('?', 2)[0] != "/ws") return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
        if (!headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)) return null;
        if (!headers.TryGetValue("Connection", out var connection) || !connection.Split(',').Any(token => token.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase))) return null;
        if (!headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13") return null;
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || !IsKey(key)) return null;
        return "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + AcceptKey(key) + "\r\n\r\n";
    }

    private static bool IsKey(string key)
    {
        try { return Convert.FromBase64String(key).Length == 16; }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// Reads the phone's upgrade from <paramref name="stream"/> (at most 8 KiB, nothing after its blank line: a client speaks only after
    /// the 101), answers it and returns the server WebSocket; null for anything else, the caller then closes the connection unanswered.
    /// </summary>
    public static async Task<WebSocket?> AcceptWebSocketAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[MaximumRequestHeadBytes]; var length = 0;
        while (true)
        {
            if (length == buffer.Length) return null;
            var read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return null;
            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            if (end + 4 != length) return null;
            var response = UpgradeResponse(Encoding.ASCII.GetString(buffer, 0, end + 4));
            if (response is null) return null;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
            // Pings are answered by the stack; the app's own 20 s ping and 60 s idle limit keep the session honest, as on the relay.
            return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        }
    }

    /// <summary>
    /// Whether a socket the listeners just accepted may start its handshake (M/MobileRemoteService.swift acceptDirect): a peer in
    /// Tailscale's ranges (any peer only through the seam), room in the phone table, fewer than four handshakes on the direct path
    /// and two from this address, and fewer than five failed auths from it in the last minute.
    /// </summary>
    public static bool Admit(string? peer, bool allowAnyPeer, int clients, IReadOnlyCollection<string> pendingPeers, int recentFailures) =>
        peer is { Length: > 0 } && (allowAnyPeer || TailscaleAddresses.Contains(peer)) && clients < MaximumClients
        && pendingPeers.Count < MaximumUnauthenticated && pendingPeers.Count(pending => pending == peer) < MaximumUnauthenticatedPerPeer
        && recentFailures < MaximumAuthFailures;
}

/// <summary>Failed direct authentications per remote address, kept for <see cref="MobileDirect.AuthFailureWindow"/>; at most 256 addresses unless all are recent.</summary>
public sealed class DirectAuthFailures
{
    private const int MaximumPeers = 256;
    private readonly object sync = new();
    private readonly Dictionary<string, List<DateTimeOffset>> failures = new(StringComparer.Ordinal);

    public int Peers { get { lock (sync) return failures.Count; } }

    public int Recent(string peer, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!failures.TryGetValue(peer, out var times)) return 0;
            times.RemoveAll(time => now - time >= MobileDirect.AuthFailureWindow);
            if (times.Count == 0) failures.Remove(peer);
            return times.Count;
        }
    }

    public void Record(string peer, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!failures.ContainsKey(peer) && failures.Count >= MaximumPeers)
                foreach (var old in failures.Where(entry => entry.Value.All(time => now - time >= MobileDirect.AuthFailureWindow)).Select(entry => entry.Key).ToArray()) failures.Remove(old);
            if (!failures.TryGetValue(peer, out var times)) failures[peer] = times = [];
            times.Add(now);
        }
    }
}

/// <summary>What the direct path is doing. <c>off</c>: the remote is off; <c>disabled</c>: its direct switch is; <c>notFound</c>: no Tailscale address; <c>listening</c>: on <see cref="Endpoints"/>; <c>failed</c>: no address could be bound, <see cref="Error"/> says why.</summary>
public sealed record MobileDirectStatus(string State, IReadOnlyList<string> Endpoints, string? Error = null)
{
    public static readonly MobileDirectStatus Off = new("off", []);
    [JsonIgnore] public bool Listening => State == "listening";
}

/// <summary>The line under the direct switch, in the Mac's words (mobileRemote.direct.status.*).</summary>
public static class MobileDirectStatusText
{
    public static string Detail(MobileDirectStatus status) => status.State switch
    {
        "disabled" => Locale.Get("mobileRemote.direct.status.off"),
        "notFound" => Locale.Get("mobileRemote.direct.status.notFound"),
        "listening" => Locale.Get("mobileRemote.direct.status.listeningTemplate", new Dictionary<string, string> { ["addresses"] = string.Join(", ", status.Endpoints) }),
        "failed" => Locale.Get("mobileRemote.direct.status.failedTemplate", new Dictionary<string, string> { ["error"] = status.Error ?? "" }),
        _ => "",
    };
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct|tailscale addresses'`
Expected: `PASS mobile direct: endpoints, upgrade, admission and status`, and `FAIL: 9`.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/windows/MightyClaude.Core/Remote/MobileDirect.cs native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the direct path's rules: the Mac's limits, Tailscale-only endpoints, the phone's WebSocket upgrade, who gets a handshake slot, failed auths per address per minute, and the status line's words" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `MobileDirectListener`: per-address `TcpListener`s and 36 slots

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/MobileDirectListener.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (registration)
- Test: `native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs` (add a method and a helper)

**Interfaces:**
- Consumes:
  - `MobileDirect.ConnectionLimit` and `MobileDirect.AcceptWebSocketAsync(Stream, CancellationToken)` (Task 3)
  - `TailscaleAddresses.Canonical(string)` (Task 2)
- Produces:
  - `public sealed class MobileDirectListener`:
    - `MobileDirectListener(Action<DirectIncoming> onConnection)`
    - `int LiveConnections { get; }`
    - `(IReadOnlyList<(string Host, int Port)> Bound, string? Error) Update(IReadOnlyList<string> addresses, int port)` (port 0 picks a free port and keeps it on later updates with 0)
    - `void Stop()`
  - `public sealed class DirectIncoming : IDisposable`:
    - `string? Peer { get; }`
    - `Stream Stream { get; }`
    - `void Refuse()`
    - `void Dispose()`
  - Test helper: `MobileDirectVerification.ReadsEnd(TcpClient, CancellationToken) → Task<bool>`, used by Task 6.

- [ ] **Step 1: Write the failing test**

Add to `MobileDirectVerification` (inside the class):

```csharp
    internal static async Task TheListenerHoldsAtMost36AndUpgradesAPhone()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var delivered = new ConcurrentQueue<DirectIncoming>();
        var listener = new MobileDirectListener(delivered.Enqueue);
        try
        {
            var (bound, error) = listener.Update(["127.0.0.1"], 0);
            Check(error is null && bound.Count == 1 && bound[0].Host == "127.0.0.1" && bound[0].Port > 0, "one listener on the address asked for");
            var port = bound[0].Port;
            Check(listener.Update(["127.0.0.1"], 0).Bound.Single().Port == port, "an address already listened on keeps its listener");

            // A phone's upgrade, then frames both ways.
            using (var phone = new ClientWebSocket())
            {
                var connecting = phone.ConnectAsync(new Uri("ws://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/ws?v=1"), timeout.Token);
                await Verification.Until(() => !delivered.IsEmpty);
                delivered.TryDequeue(out var incoming);
                Check(incoming!.Peer == "127.0.0.1", "the peer is read from the socket");
                using var server = await MobileDirect.AcceptWebSocketAsync(incoming.Stream, timeout.Token) ?? throw new InvalidOperationException("the phone's upgrade was refused");
                await connecting;
                await phone.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, timeout.Token);
                var buffer = new byte[16]; var got = await server.ReceiveAsync(buffer, timeout.Token);
                Check(got.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(buffer, 0, got.Count) == "hello", "frames pass after the upgrade");
                incoming.Dispose();
            }
            await Verification.Until(() => listener.LiveConnections == 0);

            // 36 held at once; the 37th is reset unread and gives its slot straight back.
            var held = new List<TcpClient>();
            try
            {
                for (var i = 0; i < MobileDirect.ConnectionLimit; i++) { var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token); held.Add(client); }
                await Verification.Until(() => listener.LiveConnections == MobileDirect.ConnectionLimit);
                using var extra = new TcpClient(); await extra.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                Check(await ReadsEnd(extra, timeout.Token), "the 37th connection is dropped");
                Check(listener.LiveConnections == MobileDirect.ConnectionLimit, "a dropped connection gives its slot back");
            }
            finally { foreach (var client in held) client.Dispose(); while (delivered.TryDequeue(out var item)) item.Dispose(); }
            await Verification.Until(() => listener.LiveConnections == 0);

            // A port already taken is reported, not thrown.
            using var taken = new TcpListener(IPAddress.Loopback, 0); taken.Start(); var busy = ((IPEndPoint)taken.LocalEndpoint).Port;
            var second = new MobileDirectListener(_ => { });
            var failed = second.Update(["127.0.0.1"], busy); second.Stop();
            Check(failed.Bound.Count == 0 && failed.Error is { Length: > 0 }, "a taken port is a bind error");

            // Stop closes every listener.
            listener.Stop();
            Check(listener.Update(["127.0.0.1"], 0).Bound.Count == 0, "a stopped listener binds nothing again");
            var refused = false;
            using var after = new TcpClient();
            try { await after.ConnectAsync(IPAddress.Loopback, port, timeout.Token); refused = await ReadsEnd(after, timeout.Token); }
            catch (SocketException) { refused = true; }
            Check(refused, "a stopped listener takes no phone");
        }
        finally { listener.Stop(); }
    }

    /// <summary>Whether the server ended the connection (a reset or a close) within three seconds.</summary>
    private static async Task<bool> ReadsEnd(TcpClient client, CancellationToken token)
    {
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(3));
            return await client.GetStream().ReadAsync(new byte[1], wait.Token) == 0;
        }
        catch (IOException) { return true; }
        catch (OperationCanceledException) { return false; }
    }
```

In `Verification.cs`, add after the `mobile direct: endpoints…` line:

```csharp
        await Test("mobile direct: the listener holds at most 36 and upgrades a phone", MobileDirectVerification.TheListenerHoldsAtMost36AndUpgradesAPhone);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct'`
Expected: the build fails with `error CS0246: The type or namespace name 'DirectIncoming' could not be found` (and the same for `MobileDirectListener`).

- [ ] **Step 3: Implement**

Create `native/windows/MightyClaude.Core/Remote/MobileDirectListener.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MightyClaude.Core;

/// <summary>One connection a direct listener delivered; it holds one of the listeners' slots until it is refused or disposed.</summary>
public sealed class DirectIncoming : IDisposable
{
    private readonly Socket socket;
    private readonly Action release;
    private int finished;

    /// <summary>The remote IP in its canonical spelling (an IPv4-mapped address as IPv4, no <c>%scope</c>); null when the endpoint is no IP.</summary>
    public string? Peer { get; }
    public Stream Stream { get; }

    internal DirectIncoming(Socket socket, Action release)
    {
        this.socket = socket; this.release = release;
        Peer = socket.RemoteEndPoint is IPEndPoint remote ? TailscaleAddresses.Canonical((remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString()) : null;
        Stream = new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Drops the connection unread: a reset, nothing said.</summary>
    public void Refuse()
    {
        if (Interlocked.Exchange(ref finished, 1) != 0) return;
        try { socket.LingerState = new LingerOption(true, 0); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        Stream.Dispose(); release();
    }

    public void Dispose() { if (Interlocked.Exchange(ref finished, 1) == 0) { Stream.Dispose(); release(); } }
}

/// <summary>
/// The direct path's listeners (M/MightyCore/Remote/MobileDirect.swift MobileDirectListener): one <see cref="TcpListener"/> per
/// Tailscale address, bound to that address alone (never a wildcard), with exclusive use of the port on Windows. HttpListener is not
/// used: binding it to one IP needs an administrator's URL ACL. Together the listeners hold at most
/// <see cref="MobileDirect.ConnectionLimit"/> connections; one over that is reset before anything is read. Windows' strong host
/// model stands in for the Mac's pinning to the utun interface.
/// </summary>
public sealed class MobileDirectListener
{
    private sealed record Entry(TcpListener Listener, string Host, int Port, CancellationTokenSource Stop);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> listeners = new(StringComparer.Ordinal);
    private readonly Action<DirectIncoming> onConnection;
    private int live;
    private bool stopped;

    public MobileDirectListener(Action<DirectIncoming> onConnection) => this.onConnection = onConnection;

    /// <summary>Connections delivered and not yet ended, across every listener.</summary>
    public int LiveConnections => Volatile.Read(ref live);

    /// <summary>
    /// Makes the listeners match <paramref name="addresses"/> on <paramref name="port"/>: stops the ones no longer wanted, keeps the
    /// ones already up (and the phones on them), binds the rest. Answers every listener up afterwards with its port, and the first error.
    /// </summary>
    public (IReadOnlyList<(string Host, int Port)> Bound, string? Error) Update(IReadOnlyList<string> addresses, int port)
    {
        lock (sync)
        {
            if (stopped) return ([], null);
            var wanted = addresses.Select(address => Key(address, port)).ToHashSet(StringComparer.Ordinal);
            foreach (var key in listeners.Keys.Where(key => !wanted.Contains(key)).ToArray()) { Close(listeners[key]); listeners.Remove(key); }
            string? error = null;
            foreach (var address in addresses)
            {
                var key = Key(address, port);
                if (listeners.ContainsKey(key)) continue;
                if (!IPAddress.TryParse(address, out var ip)) { error ??= address; continue; }
                var listener = new TcpListener(ip, port);
                try
                {
                    if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
                    listener.Start(MobileDirect.ConnectionLimit);
                }
                catch (SocketException ex) { listener.Stop(); error ??= ex.Message; continue; }
                var entry = new Entry(listener, address, ((IPEndPoint)listener.LocalEndpoint).Port, new CancellationTokenSource());
                listeners[key] = entry;
                _ = AcceptLoop(entry);
            }
            return ([.. addresses.Select(address => Key(address, port)).Where(listeners.ContainsKey).Select(key => (listeners[key].Host, listeners[key].Port))], error);
        }
    }

    /// <summary>Stops accepting. Connections already delivered stay up; their owners close them.</summary>
    public void Stop()
    {
        lock (sync) { stopped = true; foreach (var entry in listeners.Values) Close(entry); listeners.Clear(); }
    }

    private async Task AcceptLoop(Entry entry)
    {
        while (!entry.Stop.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await entry.Listener.AcceptSocketAsync(entry.Stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) when (!entry.Stop.IsCancellationRequested) { await Task.Delay(100, CancellationToken.None); continue; }
            catch (SocketException) { return; }
            Deliver(socket);
        }
    }

    private void Deliver(Socket socket)
    {
        var count = Interlocked.Increment(ref live);
        DirectIncoming incoming;
        try { incoming = new DirectIncoming(socket, () => Interlocked.Decrement(ref live)); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { Interlocked.Decrement(ref live); socket.Dispose(); return; }
        bool refuse;
        lock (sync) refuse = stopped || count > MobileDirect.ConnectionLimit;
        if (refuse) { incoming.Refuse(); return; }
        try { onConnection(incoming); }
        catch (Exception) { incoming.Refuse(); }
    }

    private static void Close(Entry entry) { entry.Stop.Cancel(); entry.Listener.Stop(); }
    private static string Key(string address, int port) => address + "|" + port.ToString(CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct'`
Expected: `PASS mobile direct: the listener holds at most 36 and upgrades a phone`, and `FAIL: 9`.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/windows/MightyClaude.Core/Remote/MobileDirectListener.cs native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the direct path listens with one TcpListener per Tailscale address, never a wildcard and no URL ACL, holds at most 36 connections together and resets the rest unread" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The host takes a phone directly on the relay's handshake

**Files:**
- Modify: `native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs`
- Modify: `native/windows/MightyClaude.Core/MightyClaude.Core.csproj` (`InternalsVisibleTo`)
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (registration)
- Test: `native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs` (add a method, `DirectPhone`, `Receive`, `Query`)

**Interfaces:**
- Consumes:
  - `MobileDirect.*`, `DirectAuthFailures`, `MobileDirectStatus` (Task 3)
  - `MobileDirectListener`, `DirectIncoming` (Task 4)
  - `TailscaleAddresses.Current()` (Task 2)
- Produces:
  - `public sealed record MobileRemoteSettings(bool Enabled = false, string RelayURL = "", bool AllowLegacyPhones = true, bool DirectEnabled = true, int DirectPort = MobileDirect.DefaultPort)`. JSON keys are `directEnabled` and `directPort`; an invalid port normalizes to 43139.
  - On `MobileRelayHost`:
    - `public MobileDirectStatus DirectStatus { get; }`
    - `public IReadOnlyList<string> DirectEndpoints { get; }`
    - `public void RefreshDirect(bool force = false)`
    - `internal void SetDirectAddressSource(Func<IReadOnlyList<string>>? source, int port = 0)` (the seam)
    - `internal IReadOnlyList<string> ConnectionIds { get; }`
  - `auth_ok` carries `direct: string[]`.
  - Test helpers in `MobileDirectVerification`, used by Tasks 6 and 7:
    - `DirectPhone.ConnectAsync(string endpoint, string pairingUrl, CancellationToken) → Task<DirectPhone>`
    - `DirectPhone.ExchangeAsync(object, CancellationToken) → Task<JsonDocument>`
    - `Query(string, string) → string`

- [ ] **Step 1: Write the failing test**

Add to `MobileDirectVerification`:

```csharp
    private static string Temp() => Directory.CreateTempSubdirectory("mighty-direct-").FullName;
    private static string Query(string link, string name) => Uri.UnescapeDataString(new Uri(link).Query.TrimStart('?').Split('&').Single(part => part.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..]);
    private static async Task<byte[]> Receive(WebSocket socket, CancellationToken token)
    {
        using var result = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(buffer, token); if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("closed"); result.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
        return result.ToArray();
    }

    /// <summary>A phone on the direct path: ws://endpoint/ws?v=1, hello, the paired key checked, then encrypted frames.</summary>
    private sealed class DirectPhone(ClientWebSocket socket, RelayCipher cipher) : IAsyncDisposable
    {
        internal static async Task<DirectPhone> ConnectAsync(string endpoint, string pairingUrl, CancellationToken token)
        {
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri("ws://" + endpoint + "/ws?v=1"), token);
                var secret = RandomNumberGenerator.GetBytes(32); var nonce = RandomNumberGenerator.GetBytes(16);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", v = 1, clientKey = Convert.ToBase64String(RelayCipher.PublicKey(secret)), nonce = Convert.ToBase64String(nonce) }, Wire.Json), WebSocketMessageType.Text, true, token);
                using var ready = JsonDocument.Parse(await Receive(socket, token));
                var server = Convert.FromBase64String(ready.RootElement.Text("serverKey")!);
                Check(Query(pairingUrl, "pk").Replace('-', '+').Replace('_', '/') + "=" == Convert.ToBase64String(server), "the direct path answers with the paired public key");
                return new(socket, new RelayCipher(secret, server, nonce, Convert.FromBase64String(ready.RootElement.Text("nonce")!), false));
            }
            catch { socket.Dispose(); throw; }
        }

        internal async Task<JsonDocument> ExchangeAsync(object message, CancellationToken token)
        {
            await socket.SendAsync(cipher.Seal(JsonSerializer.SerializeToUtf8Bytes(message, Wire.Json)), WebSocketMessageType.Binary, true, token);
            return JsonDocument.Parse(cipher.Open(await Receive(socket, token)));
        }

        public ValueTask DisposeAsync() { socket.Abort(); socket.Dispose(); cipher.Dispose(); return ValueTask.CompletedTask; }
    }

    internal static async Task APhoneConnectsDirectlyOnTheRelaysHandshake()
    {
        var directory = Temp(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var host = new MobileRelayHost(Path.Combine(directory, "identity"), "Fixture", [], (_, path, _, _, _) => Task.FromResult(new MobileReply(200, new { protocol = 1, path }))) { WatchesNetwork = false };
        try
        {
            // The seam is set first, so this machine's own Tailscale address is never bound.
            host.SetDirectAddressSource(() => ["127.0.0.1"]);
            Check(host.DirectStatus.State == "off" && host.DirectEndpoints.Count == 0, "nothing listens while the remote is off");
            Check(JsonSerializer.Deserialize<MobileRemoteSettings>("{\"enabled\":true,\"relayURL\":\"\",\"allowLegacyPhones\":true}", Wire.Json) is { DirectEnabled: true, DirectPort: MobileDirect.DefaultPort }, "settings saved before the direct path read as on, port 43139");
            Check(new MobileRemoteSettings(DirectPort: 80).Normalized.DirectPort == MobileDirect.DefaultPort && new MobileRemoteSettings(true, "not a relay", DirectEnabled: false).Normalized is { Enabled: false, DirectEnabled: false }, "a bad port normalizes to 43139 and a bad relay keeps the direct switch");
            // The relay address is a closed local port: the relay fails and redials the whole time, and the direct phone never notices.
            await host.ApplyAsync(new MobileRemoteSettings(true, "ws://127.0.0.1:9"));
            Check(host.DirectStatus.Listening && host.DirectEndpoints.Count == 1 && host.DirectEndpoints[0].StartsWith("127.0.0.1:", StringComparison.Ordinal), "the seam's address is listened on and advertised: " + string.Join(",", host.DirectEndpoints));
            var endpoint = host.DirectEndpoints[0];
            await using (var phone = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var auth = await phone.ExchangeAsync(new { type = "auth", pairingKey = Query(host.PairingUrl, "key"), clientId = "abcdefghijklmnopqrstuv", clientName = "Fixture Phone" }, timeout.Token);
                Check(auth.RootElement.Text("type") == "auth_ok" && auth.RootElement.Text("deviceToken") is not null, "a phone pairs over the direct path");
                Check(auth.RootElement.GetProperty("direct").EnumerateArray().Select(value => value.GetString()).SequenceEqual(host.DirectEndpoints), "auth_ok carries the direct endpoints");
                using var info = await phone.ExchangeAsync(new { id = "request-1", method = "GET", path = "/m1/info" }, timeout.Token);
                Check(info.RootElement.Text("id") == "request-1" && info.RootElement.GetProperty("status").GetInt32() == 200, "requests are tunnelled as on the relay");
                Check(host.ConnectionIds.Single().StartsWith(MobileDirect.ConnectionPrefix, StringComparison.Ordinal) && host.Devices.Single().Connected && host.Status.Connections == 1, "the phone joins the same table with a direct- id");
            }
            await Verification.Until(() => host.ConnectionIds.Count == 0);
            // A wrong key hears one word, never why.
            await using (var stranger = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var refused = await stranger.ExchangeAsync(new { type = "auth", pairingKey = "wrong-key-0000000000", clientId = "zyxwvutsrqponmlkjihgfe", clientName = "Stranger" }, timeout.Token);
                Check(refused.RootElement.Text("type") == "auth_error" && refused.RootElement.Text("reason") == MobileDirect.GenericRefusal, "a direct refusal is the generic unauthorized");
            }
        }
        finally { await host.DisposeAsync(); Directory.Delete(directory, true); }
    }
```

In `Verification.cs`, add after the `mobile direct: the listener…` line:

```csharp
        await Test("mobile direct: a phone connects directly on the relay's handshake", MobileDirectVerification.APhoneConnectsDirectlyOnTheRelaysHandshake);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct'`
Expected: the build fails with `error CS1061: 'MobileRelayHost' does not contain a definition for 'SetDirectAddressSource'`. `DirectStatus`, `DirectEndpoints`, `ConnectionIds` and `DirectEnabled` are missing the same way.

- [ ] **Step 3: Implement**

3a. In `native/windows/MightyClaude.Core/MightyClaude.Core.csproj`, add before `</Project>`:

```xml
  <!-- The direct path's loopback seam (MobileRelayHost.SetDirectAddressSource) is internal: only the Core checks reach it, never WinUI or the phone. -->
  <ItemGroup>
    <InternalsVisibleTo Include="MightyClaude.Core.Tests" />
  </ItemGroup>
```

3b. In `MobileRelayHost.cs`, replace the `MobileRemoteSettings` record (the first record in the file) with:

```csharp
/// <param name="DirectEnabled">The direct path's switch (docs/relay.md, "Tailscale 직접 연결"); on unless turned off.</param>
/// <param name="DirectPort">The direct listener's port, 1024-65535.</param>
public sealed record MobileRemoteSettings(bool Enabled = false, string RelayURL = "", bool AllowLegacyPhones = true, bool DirectEnabled = true, int DirectPort = MobileDirect.DefaultPort)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public MobileRemoteSettings Normalized
    {
        get
        {
            var port = MobileDirect.ValidPort(DirectPort) ? DirectPort : MobileDirect.DefaultPort;
            var relay = (RelayURL ?? "").Trim();
            return relay.Length == 0 ? this with { RelayURL = "", DirectPort = port }
                : MobileRelayHost.NormalizeRelay(relay) is { } valid ? this with { RelayURL = valid, DirectPort = port }
                : this with { Enabled = false, RelayURL = "", DirectPort = port };
        }
    }
}
```

3c. In `MobileRelayHost`, add right after the line `public string PairingUrl => identity.PairingUrl(NormalizeRelay(settings.RelayURL) ?? DefaultRelay, hostName);`:

```csharp
    // The direct path (M/MightyCore/Remote/MobileRemoteService.swift "Direct path"): listeners on this PC's Tailscale addresses while
    // the remote and its direct switch are on. Its phones have ids starting with "direct-" and live apart from the relay's: a relay
    // redial, a relay `disconnected` notice and a relay outage never touch them. Guarded by directSync.
    private readonly object directSync = new();
    private MobileDirectListener? directListener;
    private IReadOnlyList<string>? directBoundAddresses;
    private int? directBoundPort;
    private string[] directEndpoints = [];
    private readonly DirectAuthFailures directFailures = new();
    /// <summary>
    /// Test seam: where the direct path reads its addresses instead of this PC's Tailscale adapters (loopback in the checks), and the
    /// port it binds then (0: any free one). Internal and reached only by MightyClaude.Core.Tests (InternalsVisibleTo): neither
    /// Settings nor a phone can set it, so the app only ever binds Tailscale addresses.
    /// </summary>
    private Func<IReadOnlyList<string>>? directAddressSource;
    private int directSourcePort;
    public MobileDirectStatus DirectStatus { get; private set; } = MobileDirectStatus.Off;
    /// <summary>The endpoints the direct path listens on now, as auth_ok, /m1/info and the QR carry them; empty while it is off.</summary>
    public IReadOnlyList<string> DirectEndpoints => Volatile.Read(ref directEndpoints);
    internal IReadOnlyList<string> ConnectionIds => [.. clients.Keys];

    internal void SetDirectAddressSource(Func<IReadOnlyList<string>>? source, int port = 0)
    {
        lock (directSync) { directAddressSource = source; directSourcePort = port; }
        RefreshDirect(force: true);
    }

    /// <summary>
    /// Reads the addresses again and makes the listeners match when they or the port changed; with the remote or the switch off,
    /// stops and drops the direct phones. Listeners whose address and port stay are kept, with their phones. <paramref name="force"/>
    /// rebinds even when nothing seems changed (Settings changed).
    /// </summary>
    public void RefreshDirect(bool force = false)
    {
        MobileDirectStatus before, after;
        lock (directSync)
        {
            before = DirectStatus;
            var current = settings;
            if (disposed || !current.Enabled || !current.DirectEnabled) StopDirect(current);
            else
            {
                var source = directAddressSource;
                var addresses = source is null ? TailscaleAddresses.Current() : source();
                var port = source is null ? current.DirectPort : directSourcePort;
                if (force || directBoundAddresses is not { } was || !was.SequenceEqual(addresses) || directBoundPort != port) BindDirect(addresses, port, source is not null);
            }
            after = DirectStatus;
        }
        // Only a pass that changed something is published (a re-read that found the same addresses keeps the same status object),
        // so the 15 s watch does not redraw Settings or the QR for nothing.
        if (!ReferenceEquals(before, after)) Republish();
    }

    private void BindDirect(IReadOnlyList<string> addresses, int port, bool seam)
    {
        directBoundAddresses = addresses; directBoundPort = port;
        if (addresses.Count == 0)
        {
            directListener?.Stop(); directListener = null; Volatile.Write(ref directEndpoints, []); DirectStatus = new("notFound", []);
            return;
        }
        directListener ??= new MobileDirectListener(AcceptDirect);
        var (bound, error) = directListener.Update(addresses, port);
        Volatile.Write(ref directEndpoints, [.. MobileDirect.Advertised(bound, allowAnyHost: seam)]);
        // Anything not bound is tried again on the next pass rather than taken as settled.
        if (bound.Count < addresses.Count) directBoundAddresses = null;
        if (bound.Count == 0) { directListener.Stop(); directListener = null; DirectStatus = new("failed", [], error); }
        else DirectStatus = new("listening", Volatile.Read(ref directEndpoints));
    }

    /// <summary>Stops listening and drops every phone that came in directly.</summary>
    private void StopDirect(MobileRemoteSettings current)
    {
        directListener?.Stop(); directListener = null;
        directBoundAddresses = null; directBoundPort = null; Volatile.Write(ref directEndpoints, []);
        DirectStatus = !disposed && current.Enabled && !current.DirectEnabled ? new("disabled", []) : MobileDirectStatus.Off;
        foreach (var client in clients.Values.Where(client => client.Direct)) client.Cancel();
    }

    /// <summary>
    /// One socket a direct listener accepted. Admitted ones join the relay's phone table and run the same <see cref="Connection"/>,
    /// held to the same deadline, auth and request limits; anything else is reset unread (<see cref="MobileDirect.Admit"/>).
    /// </summary>
    private void AcceptDirect(DirectIncoming incoming)
    {
        Connection connection;
        lock (directSync)
        {
            var current = settings;
            var pending = clients.Values.Where(client => client.Direct && !client.Authenticated).Select(client => client.Peer ?? "").ToArray();
            var failures = incoming.Peer is { } peer ? directFailures.Recent(peer, DateTimeOffset.UtcNow) : 0;
            if (disposed || directListener is null || !current.Enabled || !current.DirectEnabled
                || !MobileDirect.Admit(incoming.Peer, directAddressSource is not null, clients.Count, pending, failures)) { incoming.Refuse(); return; }
            var id = MobileDirect.ConnectionPrefix + Guid.NewGuid().ToString();
            connection = new Connection(this, id, token => MobileDirect.AcceptWebSocketAsync(incoming.Stream, token), CancellationToken.None, direct: true, peer: incoming.Peer) { Ended = incoming.Dispose };
            clients[id] = connection;
        }
        _ = Serve(connection);
    }

    /// <summary>The relay path's data socket for one connection id, dialled inside the handshake deadline.</summary>
    private Func<CancellationToken, Task<WebSocket?>> Open(Uri uri) => async token =>
    {
        var socket = Socket();
        try { await socket.ConnectAsync(uri, token); return socket; }
        catch { socket.Dispose(); throw; }
    };
```

3d. Replace `ApplyAsync` with:

```csharp
    public async Task ApplyAsync(MobileRemoteSettings value)
    {
        MobileRemoteSettings before;
        await lifecycle.WaitAsync();
        try { ObjectDisposedException.ThrowIf(disposed, this); before = settings; await RestartInternal(value.Normalized); }
        finally { lifecycle.Release(); }
        // The direct path follows its own switch, not the relay's state (M/MobileRemoteService.swift:409-424).
        var now = settings;
        RefreshDirect(force: now.Enabled != before.Enabled || now.DirectEnabled != before.DirectEnabled || now.DirectPort != before.DirectPort);
    }
```

3e. Replace `StopInternal` with this version, which stops only the relay's phones:

```csharp
    private async Task StopInternal()
    {
        ++generation; lifetime?.Cancel();
        foreach (var client in clients.Values.Where(client => !client.Direct)) client.Cancel();
        if (loop is { } running) try { await running; } catch (OperationCanceledException) { }
        await Task.WhenAll(clients.Values.Where(client => !client.Direct).Select(client => client.Completion.Task));
        lifetime?.Dispose(); lifetime = null; loop = null;
    }
```

3f. In `RunAsync`, replace these four lines exactly:

- `if (value.Text("type") == "disconnected") { if (clients.TryGetValue(id, out var prior)) prior.Cancel(); continue; }` becomes
  `if (value.Text("type") == "disconnected") { if (!MobileDirect.IsDirect(id) && clients.TryGetValue(id, out var prior)) prior.Cancel(); continue; }`
- `if (value.Text("type") != "connected" || clients.Count >= 32 || clients.Values.Count(c => !c.Authenticated) >= 4) continue;` becomes
  `if (value.Text("type") != "connected" || MobileDirect.IsDirect(id) || clients.Count >= MobileDirect.MaximumClients || clients.Values.Count(c => !c.Authenticated && !c.Direct) >= 4) continue;`
- `var connection = new Connection(this, id, SocketUri(relay, id), token);` becomes
  `var connection = new Connection(this, id, Open(SocketUri(relay, id)), token);`
- `finally { renew.Cancel(); if (renewal is not null) await renewal; var ended = clients.Values.ToArray(); foreach (var client in ended) client.Cancel(); await Task.WhenAll(ended.Select(c => c.Completion.Task)); }` becomes
  `finally { renew.Cancel(); if (renewal is not null) await renewal; var ended = clients.Values.Where(c => !c.Direct).ToArray(); foreach (var client in ended) client.Cancel(); await Task.WhenAll(ended.Select(c => c.Completion.Task)); }`

3g. Change the signature of the static `Receive` from `private static async Task<(WebSocketMessageType Type, byte[] Bytes)> Receive(ClientWebSocket socket, CancellationToken token)` to `private static async Task<(WebSocketMessageType Type, byte[] Bytes)> Receive(WebSocket socket, CancellationToken token)`. Leave the body unchanged.

3h. Replace the whole nested `private sealed class Connection { … }` with:

```csharp
    private sealed class Connection
    {
        public string Id { get; }
        public bool Authenticated { get; private set; }
        /// <summary>Came in on the direct path: kept through relay redials, refused with one word, its failed auths counted against <see cref="Peer"/>.</summary>
        public bool Direct { get; }
        /// <summary>The direct peer's address; null on the relay path.</summary>
        public string? Peer { get; }
        /// <summary>Runs once the connection has ended and its socket is gone (a direct one gives its listener slot back).</summary>
        public Action? Ended { get; init; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancelled;
        private Task? heartbeat;
        private readonly MobileRelayHost host;
        private readonly Func<CancellationToken, Task<WebSocket?>> open;
        private readonly CancellationTokenSource cancel;
        private WebSocket? socket;
        private readonly SemaphoreSlim writer = new(1), requests = new(8);
        private readonly ConcurrentDictionary<string, Task> inflight = [];
        private RelayCipher? cipher;
        private string device = "";
        public string DeviceId => device;
        public Connection(MobileRelayHost host, string id, Func<CancellationToken, Task<WebSocket?>> open, CancellationToken token, bool direct = false, string? peer = null)
        { this.host = host; Id = id; this.open = open; Direct = direct; Peer = peer; cancel = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public void Cancel() { if (Interlocked.Exchange(ref cancelled, 1) == 0) { cancel.Cancel(); Volatile.Read(ref socket)?.Abort(); } }
        public async Task Run()
        {
            var token = cancel.Token;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(MobileDirect.HandshakeDeadline);
                // The relay path dials its data socket; the direct path reads the phone's upgrade. Both inside the handshake deadline.
                var opened = await open(deadline.Token);
                if (opened is null) return;
                Volatile.Write(ref socket, opened);
                if (Volatile.Read(ref cancelled) == 1) return;
                var hello = await Receive(opened, deadline.Token);
                if (hello.Type != WebSocketMessageType.Text || hello.Bytes.Length > 4096) return;
                using var hd = JsonDocument.Parse(hello.Bytes); var h = hd.RootElement;
                if (h.Text("type") != "hello" || !h.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != 1) return;
                var nonce = RandomNumberGenerator.GetBytes(16);
                cipher = new(host.identity.Secret, Convert.FromBase64String(h.Text("clientKey") ?? ""), Convert.FromBase64String(h.Text("nonce") ?? ""), nonce, true);
                var ready = JsonSerializer.SerializeToUtf8Bytes(new { type = "ready", v = 1, serverKey = host.identity.PublicKey, nonce = Convert.ToBase64String(nonce) }, Wire.Json);
                await opened.SendAsync(ready, WebSocketMessageType.Text, true, deadline.Token);
                var authentication = await Receive(opened, deadline.Token);
                if (authentication.Type != WebSocketMessageType.Binary) return;
                using var ad = JsonDocument.Parse(cipher.Open(authentication.Bytes));
                var auth = host.identity.Authenticate(ad.RootElement, host.settings.AllowLegacyPhones);
                if (auth.DeviceId is null)
                {
                    // A direct refusal is one word, so a tailnet peer learns nothing of which ids or keys this PC knows; the phone then
                    // takes the relay and hears the real reason there (docs/relay.md). Each one counts against the peer's address.
                    await Send(new { type = "auth_error", reason = Direct ? MobileDirect.GenericRefusal : auth.Error });
                    if (Direct && Peer is not null) host.directFailures.Record(Peer, DateTimeOffset.UtcNow);
                    return;
                }
                device = auth.DeviceId; Authenticated = true;
                await Send(new { type = "auth_ok", hostName = host.hostName, hostId = host.identity.HostId, appVersion = host.AppVersion, capabilities = host.capabilities.Concat(host.ExtraCapabilities?.Invoke() ?? []).Distinct().ToArray(), direct = host.DirectEndpoints, deviceToken = auth.DeviceToken });
                host.Republish();
                heartbeat = Heartbeat(token);
                while (!token.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(60));
                    var frame = await Receive(opened, idle.Token);
                    if (frame.Type != WebSocketMessageType.Binary) continue;
                    using var doc = JsonDocument.Parse(cipher.Open(frame.Bytes)); var root = doc.RootElement;
                    if (root.Text("type") == "ping") { await Send(new { type = "pong" }); continue; }
                    if (root.Text("type") is { } signalType && signalType.StartsWith("screen-", StringComparison.Ordinal))
                    { if (host.ScreenSignal is not null && frame.Bytes.Length <= 65536 + 28 && host.identity.Contains(device, false)) await host.ScreenSignal(device, root.Clone(), token); continue; }
                    if (root.TryGetProperty("type", out _)) continue;
                    var id = root.Text("id"); var method = root.Text("method"); var path = root.Text("path");
                    if (id is not { Length: > 0 and <= 64 } || method is not ("GET" or "POST") || path is null || !path.StartsWith('/')) continue;
                    if (Encoding.UTF8.GetByteCount(path) > 16384) { await Send(new { id, status = 414, body = new { protocol = 1, error = "Path exceeds limit.", code = "badPath" } }); continue; }
                    if (inflight.ContainsKey(id) || !await requests.WaitAsync(0, token)) { await Send(new { id, status = 429, body = new { protocol = 1, error = "Too many pending requests." } }); continue; }
                    var body = root.TryGetProperty("body", out var b) ? b.Clone() : (JsonElement?)null;
                    // Insert before the asynchronous route can complete and remove itself.
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); inflight[id] = completion.Task;
                    _ = Handle(id, method, path, body, completion);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException or FormatException or CryptographicException or Org.BouncyCastle.Crypto.CryptoException or InvalidOperationException or ArgumentException or ObjectDisposedException) { }
            finally
            {
                Cancel(); if (heartbeat is not null) await heartbeat; await Task.WhenAll(inflight.Values); cipher?.Dispose(); Volatile.Read(ref socket)?.Dispose(); cancel.Dispose(); Ended?.Invoke();
            }
        }
        private async Task Heartbeat(CancellationToken token)
        {
            try { while (!token.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(20), token); await Send(new { type = "ping" }); } }
            catch (OperationCanceledException) { }
        }
        private async Task Handle(string id, string method, string path, JsonElement? body, TaskCompletionSource completion)
        {
            try
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (!host.identity.Contains(device, host.settings.AllowLegacyPhones)) return;
                var reply = await host.route(method, path, body, device, cancel.Token);
                // A reply the relay would close the connection for is refused instead, so the phone is told and keeps its tunnel.
                var frame = JsonSerializer.SerializeToUtf8Bytes(new { id, status = reply.Status, body = reply.Body }, Wire.Json);
                if (frame.Length > MaximumPlaintext) { await Send(new { id, status = 413, body = new { protocol = 1, error = "Response exceeds the mobile display limit." } }); return; }
                await SendBytes(frame);
                if (!cancel.IsCancellationRequested && reply.AfterSend is not null) await reply.AfterSend();
            }
            catch (OperationCanceledException) { }
            catch { await Send(new { id, status = 500, body = new { protocol = 1, error = "The mobile request failed." } }); }
            finally { inflight.TryRemove(id, out _); requests.Release(); completion.TrySetResult(); }
        }
        public Task Send(object value) => SendBytes(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json));
        private async Task SendBytes(byte[] bytes)
        {
            try
            {
                await writer.WaitAsync(cancel.Token);
                try
                {
                    if (cipher is null || Volatile.Read(ref socket) is not { } current) return;
                    if (bytes.Length > MaximumPlaintext) throw new IOException("Relay response exceeds limit.");
                    await current.SendAsync(cipher.Seal(bytes), WebSocketMessageType.Binary, true, cancel.Token);
                }
                finally { writer.Release(); }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { Cancel(); }
        }
    }
```

3i. Replace `DisposeAsync` with:

```csharp
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try { if (disposed) return; disposed = true; StopWatching(); await StopInternal(); }
        finally { lifecycle.Release(); }
        lock (directSync) StopDirect(settings);
        await Task.WhenAll(clients.Values.Select(client => client.Completion.Task));
    }
```

If `InternalsVisibleTo` makes a Core internal member collide with a test-side name (error CS0104 or CS0121), rename the test-side helper. Never make the seam public.

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct|Mobile remote|Mobile host|Mobile relay'`
Expected:
- `PASS mobile direct: a phone connects directly on the relay's handshake`;
- every existing `Mobile remote …`, `Mobile host …` and `Mobile relay …` line still PASS;
- `FAIL: 9`.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs native/windows/MightyClaude.Core/MightyClaude.Core.csproj native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) a phone on the tailnet connects to the PC directly: the relay's handshake, encryption and auth on a socket the PC accepted itself, a direct- id in the same phone table, auth_ok's direct endpoints, one-word refusals, and relay redials that leave it alone" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Who is kept out, what survives, and re-detection

**Files:**
- Modify: `native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (registration)
- Test: `native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs` (add a method)

**Interfaces:**
- Consumes:
  - `DirectPhone`, `Query`, `Temp` (Task 5)
  - `ReadsEnd` (Task 4)
  - `MobileRelayHost.SetDirectAddressSource`, `RefreshDirect`, `DirectStatus`, `DirectEndpoints`, `ConnectionIds` (Task 5)
- Produces:
  - `public TimeSpan HandshakeDeadline { get; init; }` on `MobileRelayHost` (default 10 s, both paths).
  - `public TimeSpan DirectRedetectInterval { get; init; }` (default 15 s).
  - While the path is on, the host re-reads its addresses on `NetworkChange.NetworkAddressChanged` and on a timer.

- [ ] **Step 1: Write the failing test**

Add to `MobileDirectVerification`:

```csharp
    internal static async Task OnlyWhoMayGetsInAndRelayRedialsLeaveThemAlone()
    {
        var directory = Temp(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var addresses = new List<string> { "127.0.0.1" };
        var host = new MobileRelayHost(Path.Combine(directory, "identity"), "Fixture", [], (_, path, _, _, _) => Task.FromResult(new MobileReply(200, new { protocol = 1, path })))
        { WatchesNetwork = false, HandshakeDeadline = TimeSpan.FromMilliseconds(600), DirectRedetectInterval = TimeSpan.FromMilliseconds(200) };
        try
        {
            host.SetDirectAddressSource(() => [.. addresses]);
            await host.ApplyAsync(new MobileRemoteSettings(true, "ws://127.0.0.1:9"));
            var endpoint = host.DirectEndpoints.Single();
            int Port() => int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);

            // Two stalled handshakes from one address hold its two slots; a third is reset unread; the deadline frees them.
            using (var first = new TcpClient()) using (var second = new TcpClient())
            {
                await first.ConnectAsync(IPAddress.Loopback, Port(), timeout.Token); await second.ConnectAsync(IPAddress.Loopback, Port(), timeout.Token);
                await Verification.Until(() => host.ConnectionIds.Count == 2);
                using var third = new TcpClient(); await third.ConnectAsync(IPAddress.Loopback, Port(), timeout.Token);
                Check(await ReadsEnd(third, timeout.Token), "a third handshake from the same address is dropped");
                Check(await ReadsEnd(first, timeout.Token) && await ReadsEnd(second, timeout.Token), "a stalled handshake is cut at the deadline");
            }
            await Verification.Until(() => host.ConnectionIds.Count == 0);

            // A paired phone stays through a relay redial and a re-read that finds the same address; the watch notices it go away.
            await using (var phone = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var auth = await phone.ExchangeAsync(new { type = "auth", pairingKey = Query(host.PairingUrl, "key"), clientId = "abcdefghijklmnopqrstuv", clientName = "Fixture Phone" }, timeout.Token);
                Check(auth.RootElement.Text("type") == "auth_ok", "the phone is in");
                await host.ReconnectAsync();
                using var afterRedial = await phone.ExchangeAsync(new { id = "after-redial", method = "GET", path = "/m1/info" }, timeout.Token);
                Check(afterRedial.RootElement.Text("id") == "after-redial", "a relay redial leaves a direct phone connected");
                host.RefreshDirect();
                using var afterRefresh = await phone.ExchangeAsync(new { id = "after-refresh", method = "GET", path = "/m1/info" }, timeout.Token);
                Check(afterRefresh.RootElement.Text("id") == "after-refresh", "a re-read that finds the same address changes nothing");
                addresses.Clear();
                await Verification.Until(() => host.DirectStatus.State == "notFound");
                Check(host.DirectEndpoints.Count == 0 && MobileDirectStatusText.Detail(host.DirectStatus) == Locale.Get("mobileRemote.direct.status.notFound"), "the watch re-reads the addresses on its own");
            }
            addresses.Add("127.0.0.1");
            await Verification.Until(() => host.DirectStatus.Listening);
            endpoint = host.DirectEndpoints.Single();
            await Verification.Until(() => host.ConnectionIds.Count == 0);

            // The direct switch off drops the direct phones and says so.
            await using (var phone = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var auth = await phone.ExchangeAsync(new { type = "auth", pairingKey = Query(host.PairingUrl, "key"), clientId = "bcdefghijklmnopqrstuvw", clientName = "Second Phone" }, timeout.Token);
                Check(auth.RootElement.Text("type") == "auth_ok", "a second phone is in");
                await host.ApplyAsync(new MobileRemoteSettings(true, "ws://127.0.0.1:9", DirectEnabled: false));
                Check(host.DirectStatus.State == "disabled" && host.DirectEndpoints.Count == 0 && MobileDirectStatusText.Detail(host.DirectStatus) == Locale.Get("mobileRemote.direct.status.off"), "the switch off says so");
                await Verification.Until(() => !host.ConnectionIds.Any(MobileDirect.IsDirect));
            }

            // Five refused keys from one address within a minute: its sixth socket is reset unread.
            await host.ApplyAsync(new MobileRemoteSettings(true, "ws://127.0.0.1:9"));
            endpoint = host.DirectEndpoints.Single();
            for (var i = 0; i < MobileDirect.MaximumAuthFailures; i++)
            {
                await using (var stranger = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
                {
                    using var refused = await stranger.ExchangeAsync(new { type = "auth", pairingKey = "wrong-key-0000000000", clientId = "zyxwvutsrqponmlkjihgf" + i.ToString(CultureInfo.InvariantCulture), clientName = "Stranger" }, timeout.Token);
                    Check(refused.RootElement.Text("reason") == MobileDirect.GenericRefusal, "every refusal is the same word");
                }
                await Verification.Until(() => host.ConnectionIds.Count == 0);
            }
            var locked = false;
            try { await using var sixth = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or IOException) { locked = true; }
            Check(locked, "an address that keeps failing is shut out");
        }
        finally { await host.DisposeAsync(); Directory.Delete(directory, true); }
    }
```

In `Verification.cs`, add after the `mobile direct: a phone connects directly…` line:

```csharp
        await Test("mobile direct: only who may gets in, and relay redials leave them alone", MobileDirectVerification.OnlyWhoMayGetsInAndRelayRedialsLeaveThemAlone);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct'`
Expected: the build fails with `error CS0117: 'MobileRelayHost' does not contain a definition for 'HandshakeDeadline'` (and the same for `DirectRedetectInterval`).

- [ ] **Step 3: Implement**

3a. In `MobileRelayHost`, add after the line `public TimeSpan PathSettle { get; init; } = RelayLinkPolicy.PathSettle;`:

```csharp
    /// <summary>How long a phone may take from its socket to auth_ok, on either path (docs/relay.md: 10 s).</summary>
    public TimeSpan HandshakeDeadline { get; init; } = MobileDirect.HandshakeDeadline;
    /// <summary>How often the Tailscale addresses are read again while the direct path is on, on top of every address change Windows reports.</summary>
    public TimeSpan DirectRedetectInterval { get; init; } = MobileDirect.RedetectInterval;
```

3b. In `Connection.Run`, replace `deadline.CancelAfter(MobileDirect.HandshakeDeadline);` with `deadline.CancelAfter(host.HandshakeDeadline);`.

3c. Next to the other direct fields (after `private int directSourcePort;`), add:

```csharp
    private CancellationTokenSource? directWatch;
```

and after `StopDirect`, add:

```csharp
    /// <summary>Reads the addresses again every <see cref="DirectRedetectInterval"/> while the path is on: Tailscale coming up does not always raise an address change. Under directSync.</summary>
    private void StartDirectWatch()
    {
        if (directWatch is not null) return;
        var watch = directWatch = new CancellationTokenSource(); var interval = DirectRedetectInterval;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try { while (await timer.WaitForNextTickAsync(watch.Token)) RefreshDirect(); }
            catch (OperationCanceledException) { }
        });
    }
```

3d. In `RefreshDirect`, replace the `else` branch's first line `var source = directAddressSource;` with `StartDirectWatch(); var source = directAddressSource;`.

3e. In `StopDirect`, add as its first line: `directWatch?.Cancel(); directWatch = null;`.

3f. Replace `private void AddressChanged(object? sender, EventArgs e) => Read();` with:

```csharp
    // An address change may be Tailscale coming up or going down: the relay's path is read and the direct path re-detected.
    private void AddressChanged(object? sender, EventArgs e) { Read(); _ = Task.Run(() => RefreshDirect()); }
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct|Mobile remote|Mobile host|Mobile relay'`
Expected:
- `PASS mobile direct: only who may gets in, and relay redials leave them alone`;
- the earlier `mobile direct:` lines and every `Mobile remote|host|relay` line still PASS;
- `FAIL: 9`.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the direct path keeps out a third handshake from one address, a stalled one after 10 s and an address after five failed auths in a minute, keeps its phones through relay redials, drops them when switched off, and reads the Tailscale addresses again on every address change and every 15 s" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Advertising: the `direct` capability, `/m1/info` and the QR

**Files:**
- Modify: `native/windows/MightyClaude.Core/Remote/MobileDesktopRouter.cs`
- Modify: `native/windows/MightyClaude.Core/Remote/MobileIdentity.cs`
- Modify: `native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs` (`PairingUrl`)
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (registration)
- Test: `native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs` (add a method)

**Interfaces:**
- Consumes:
  - `DirectPhone`, `Query`, `Temp` (Task 5)
  - `MobileRelayHost.DirectEndpoints` and `SetDirectAddressSource` (Task 5)
- Produces:
  - `MobileDesktopRouter.Capabilities` now ends with `"direct"`.
  - `public Func<IReadOnlyList<string>>? DirectEndpoints { get; set; }` on `MobileDesktopRouter`. `/m1/info` carries `direct: string[]`, never missing.
  - `public string PairingUrl(string relay, string name, IReadOnlyList<string>? direct = null)` on `MobileIdentity`, which appends `&direct=<Uri.EscapeDataString(joined with ",")>` when the list is non-empty.
  - `MobileRelayHost.PairingUrl` passes `DirectEndpoints`.

- [ ] **Step 1: Write the failing test**

Add to `MobileDirectVerification`:

```csharp
    internal static async Task TheHostAdvertisesItsEndpoints()
    {
        var directory = Temp(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var host = new MobileRelayHost(Path.Combine(directory, "identity"), "Fixture", MobileDesktopRouter.Capabilities, (_, _, _, _, _) => Task.FromResult(new MobileReply(200, new { protocol = 1 }))) { WatchesNetwork = false };
        try
        {
            Check(MobileDesktopRouter.Capabilities.Contains("direct"), "the host advertises the direct capability");
            host.SetDirectAddressSource(() => ["127.0.0.1"]);
            Check(!host.PairingUrl.Contains("direct=", StringComparison.Ordinal), "no direct= while nothing listens");
            await host.ApplyAsync(new MobileRemoteSettings(true, "ws://127.0.0.1:9"));
            var endpoint = host.DirectEndpoints.Single();
            Check(host.PairingUrl.EndsWith("&direct=" + Uri.EscapeDataString(endpoint), StringComparison.Ordinal) && Query(host.PairingUrl, "direct") == endpoint, "the QR carries the endpoints");
            Check(new MobileIdentity(Path.Combine(directory, "other")).PairingUrl("wss://relay.example", "Fixture", ["100.101.102.103:43139", "[fd7a:115c:a1e0::1]:43139"])
                .EndsWith("&direct=100.101.102.103%3A43139%2C%5Bfd7a%3A115c%3Aa1e0%3A%3A1%5D%3A43139", StringComparison.Ordinal), "':', ',', '[' and ']' are percent-encoded as the Mac writes them");
            await using (var phone = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var auth = await phone.ExchangeAsync(new { type = "auth", pairingKey = Query(host.PairingUrl, "key"), clientId = "abcdefghijklmnopqrstuv", clientName = "Fixture Phone" }, timeout.Token);
                Check(auth.RootElement.GetProperty("capabilities").EnumerateArray().Any(value => value.GetString() == "direct"), "auth_ok lists the direct capability");
            }
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync();
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])) { DirectEndpoints = () => host.DirectEndpoints };
            var info = JsonSerializer.SerializeToElement((await router.RouteAsync("GET", "/m1/info", null, "phone", CancellationToken.None)).Body, Wire.Json);
            Check(info.GetProperty("capabilities").EnumerateArray().Any(value => value.GetString() == "direct") && info.GetProperty("direct").EnumerateArray().Select(value => value.GetString()).SequenceEqual([endpoint]), "/m1/info carries the capability and the endpoints");
            router.DirectEndpoints = null;
            Check(JsonSerializer.SerializeToElement((await router.RouteAsync("GET", "/m1/info", null, "phone", CancellationToken.None)).Body, Wire.Json).GetProperty("direct").GetArrayLength() == 0, "without a host the list is empty, never missing");
        }
        finally { await host.DisposeAsync(); Directory.Delete(directory, true); }
    }
```

In `Verification.cs`, add after the `mobile direct: only who may gets in…` line:

```csharp
        await Test("mobile direct: the host advertises its endpoints", MobileDirectVerification.TheHostAdvertisesItsEndpoints);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct'`
Expected: the build fails with `error CS0117: 'MobileDesktopRouter' does not contain a definition for 'DirectEndpoints'`. Once that is stubbed, `PairingUrl` with three arguments fails with CS1501.

- [ ] **Step 3: Implement**

3a. In `MobileDesktopRouter.cs`, make three edits:

- Replace the `Capabilities` line with:

  ```csharp
      public static readonly string[] Capabilities = ["submit-mode", "queue", "pane", "history", "settings", "commands", "mighty", "status", "attachments", "style", "files", "structure", "direct"];
      /// <summary>The direct path's endpoints for /m1/info (docs/mobile-remote.md, "direct"); empty when there is no host or it does not listen.</summary>
      public Func<IReadOnlyList<string>>? DirectEndpoints { get; set; }
  ```

- In the `/m1/info` reply, replace `capabilities = Capabilities.Concat(ExtraCapabilities?.Invoke() ?? []).Distinct().ToArray() });` with:

  ```csharp
  capabilities = Capabilities.Concat(ExtraCapabilities?.Invoke() ?? []).Distinct().ToArray(), direct = DirectEndpoints?.Invoke() ?? [] });
  ```

3b. In `MobileIdentity.cs`, replace the `PairingUrl` method with:

```csharp
    /// <summary>The v2 pairing link; <paramref name="direct"/> adds the direct path's endpoints as <c>direct=</c>, joined with commas and percent-encoded (':' ',' '[' ']' included) as the Mac writes it. A phone that does not know it uses the relay.</summary>
    public string PairingUrl(string relay, string name, IReadOnlyList<string>? direct = null)
    {
        lock (sync) return "mightyclaude://pair?v=2&sid=" + ServerId + "&pk=" + Uri.EscapeDataString(PublicKey.TrimEnd('=').Replace('+', '-').Replace('/', '_')) + "&relay=" + Uri.EscapeDataString(relay) + "&key=" + Uri.EscapeDataString(state.PairingKey) + "&name=" + Uri.EscapeDataString(Wire.Clean(name, 120))
            + (direct is { Count: > 0 } ? "&direct=" + Uri.EscapeDataString(string.Join(",", direct)) : "");
    }
```

3c. In `MobileRelayHost.cs`, replace `public string PairingUrl => identity.PairingUrl(NormalizeRelay(settings.RelayURL) ?? DefaultRelay, hostName);` with:

```csharp
    public string PairingUrl => identity.PairingUrl(NormalizeRelay(settings.RelayURL) ?? DefaultRelay, hostName, DirectEndpoints);
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct|Mobile remote|Mobile structure|Mobile host'`
Expected:
- `PASS mobile direct: the host advertises its endpoints`;
- `Mobile remote DesktopRouteContract`, `Mobile remote EncryptedRelayRoundTrip` and every `Mobile structure` line still PASS;
- `FAIL: 9`.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/windows/MightyClaude.Core/Remote/MobileDesktopRouter.cs native/windows/MightyClaude.Core/Remote/MobileIdentity.cs native/windows/MightyClaude.Core/Remote/MobileRelayHost.cs native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the PC tells phones where to reach it directly: the direct capability, /m1/info's and auth_ok's endpoints, and the pairing QR's direct= written as the Mac writes it" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: `WindowsFirewallRule`: the fixed netsh rule and its runner

**Files:**
- Create: `native/windows/MightyClaude.Core/WindowsFirewallRule.cs`
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (registration)
- Test: `native/windows/MightyClaude.Core.Tests/WindowsFirewallVerification.cs` (create)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces (`namespace MightyClaude.Core`). Part 3 consumes these. Its plan's reconcile step maps its `WindowsFirewallRule.Arguments`/`ApplyAsync` to the members below.
  - `public enum FirewallRuleState { Unknown, Present, Absent }`
  - `public enum FirewallChange { Done, Cancelled, Refused, Failed }`
  - `public sealed record FirewallChangeResult(FirewallChange Kind, string? Detail = null)`. A `Refused` result's `Detail` is `noPorts`, `badProgram` or `noSystemDirectory`. A `Failed` result's `Detail` is netsh's exit code or the launch error.
  - `public static class WindowsFirewallRule`, with constants:
    - `RuleName = "Mighty Claude (Tailscale)"`
    - `RemoteRanges = "100.64.0.0/10,fd7a:115c:a1e0::/48"`
    - `ElevationVerb = "runas"`
    - `ElevationCancelled = 1223`
  - `WindowsFirewallRule` methods:
    - `string? SystemDirectory { get; }`
    - `bool ValidPort(int)`, `bool ValidSystemDirectory(string?)`
    - `string? ValidProgram(string?)`, `string? PortList(IEnumerable<int>)`
    - `IReadOnlyList<string>? AddArguments(string program, IEnumerable<int> ports)`
    - `IReadOnlyList<string> DeleteArguments()`, `IReadOnlyList<string> ShowArguments()`
    - `IReadOnlyList<string>? Arguments(IReadOnlyCollection<int> ports, string program)`: an empty port set gives the delete argv.
    - `string CommandLine(IReadOnlyList<string> arguments)`
    - `ProcessStartInfo QueryStartInfo(string systemDirectory)`
    - `ProcessStartInfo ElevatedStartInfo(string systemDirectory, IReadOnlyList<string> arguments)`
  - `public sealed class WindowsFirewallRuleRunner`:
    - `WindowsFirewallRuleRunner(Func<ProcessStartInfo, CancellationToken, Task<int>> run, string? systemDirectory, string? program)`
    - `static WindowsFirewallRuleRunner ForThisApp()`
    - `Task<FirewallRuleState> QueryAsync(CancellationToken)`
    - `Task<FirewallChangeResult> AddAsync(IEnumerable<int> ports, CancellationToken)`
    - `Task<FirewallChangeResult> RemoveAsync(CancellationToken)`
    - `Task<FirewallChangeResult> ApplyAsync(IReadOnlyCollection<int> ports, CancellationToken)`: empty removes.

- [ ] **Step 1: Write the failing test**

Create `native/windows/MightyClaude.Core.Tests/WindowsFirewallVerification.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using MightyClaude.Core;

// The one command the app ever asks Windows to elevate (spec docs/superpowers/specs/2026-10-17-windows-catch-up-design.md,
// "Windows Firewall"): built from constants, the app's own path and validated ports. Nothing here runs netsh: the runner is given a
// recorder in place of Process.Start.
internal static class WindowsFirewallVerification
{
    private const string Program = @"C:\Program Files\Mighty Claude\MightyClaude.exe";
    private const string System32 = @"C:\Windows\System32";
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    internal static Task TheRuleIsBuiltFromConstants()
    {
        var add = WindowsFirewallRule.AddArguments(Program, [43139]);
        Check(add is not null && add.SequenceEqual(["advfirewall", "firewall", "add", "rule", "name=Mighty Claude (Tailscale)", "dir=in", "action=allow", "protocol=TCP", "localport=43139", "remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48", "program=" + Program, "profile=any"]), "the add rule's argv");
        Check(WindowsFirewallRule.CommandLine(add!) == "advfirewall firewall add rule name=\"Mighty Claude (Tailscale)\" dir=in action=allow protocol=TCP localport=43139 remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 program=\"C:\\Program Files\\Mighty Claude\\MightyClaude.exe\" profile=any", "netsh's own spelling: key=\"value\" around spaces");
        Check(WindowsFirewallRule.AddArguments(Program, [43139, 43137, 43139])![8] == "localport=43137,43139", "a port set (part 3 adds 43137): sorted, each once");
        Check(WindowsFirewallRule.DeleteArguments().SequenceEqual(["advfirewall", "firewall", "delete", "rule", "name=Mighty Claude (Tailscale)"]), "the delete rule's argv");
        Check(WindowsFirewallRule.ShowArguments().SequenceEqual(["advfirewall", "firewall", "show", "rule", "name=Mighty Claude (Tailscale)"]), "the query's argv");
        Check(WindowsFirewallRule.Arguments([43139], Program)!.SequenceEqual(add!) && WindowsFirewallRule.Arguments([], Program)!.SequenceEqual(WindowsFirewallRule.DeleteArguments()), "a port set adds; none deletes");
        foreach (var bad in new[] { "", "MightyClaude.exe", "C:\\Mighty\\Mighty\"Claude.exe", "C:\\Mighty\\Mighty\nClaude.exe", @"C:\Mighty\%TEMP%\MightyClaude.exe", @"C:\Mighty\MightyClaude.bat", @"\\server\share\MightyClaude.exe", @"C:\Mighty\..\MightyClaude.exe", @"C:\Mighty\x.exe:stream.exe", "C:/Mighty/MightyClaude.exe", @"C:\Mighty\\MightyClaude.exe" })
            Check(WindowsFirewallRule.AddArguments(bad, [43139]) is null, "a refused program path: " + bad.Replace("\n", "\\n", StringComparison.Ordinal));
        foreach (var ports in new int[][] { [], [1023], [65536], [0], [43139, -1] })
            Check(WindowsFirewallRule.AddArguments(Program, ports) is null, "refused ports: " + string.Join(",", ports));
        Check(WindowsFirewallRule.AddArguments(Program, [1024, 65535]) is not null, "1024 and 65535 are allowed");
        Check(WindowsFirewallRule.ValidSystemDirectory(System32) && !WindowsFirewallRule.ValidSystemDirectory("") && !WindowsFirewallRule.ValidSystemDirectory(null) && !WindowsFirewallRule.ValidSystemDirectory("System32") && !WindowsFirewallRule.ValidSystemDirectory("C:\\Win\"dows"), "the system folder is an absolute drive path");
        var query = WindowsFirewallRule.QueryStartInfo(System32);
        Check(query.FileName == System32 + @"\netsh.exe" && !query.UseShellExecute && query.CreateNoWindow && query.Verb.Length == 0 && query.RedirectStandardOutput
            && query.Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.ShowArguments()) && query.WorkingDirectory == System32, "the query runs netsh from the system folder, hidden and unelevated");
        var elevated = WindowsFirewallRule.ElevatedStartInfo(System32, add!);
        Check(elevated.FileName == System32 + @"\netsh.exe" && elevated.UseShellExecute && elevated.Verb == WindowsFirewallRule.ElevationVerb && elevated.WindowStyle == ProcessWindowStyle.Hidden
            && elevated.Arguments == WindowsFirewallRule.CommandLine(add!) && elevated.WorkingDirectory == System32, "a change asks Windows to elevate netsh from the system folder");
        return Task.CompletedTask;
    }

    internal static async Task TheRunnerElevatesOnlyThatCommand()
    {
        var launched = new List<ProcessStartInfo>(); var answer = 0; Exception? failure = null;
        var runner = new WindowsFirewallRuleRunner((info, _) => { launched.Add(info); if (failure is not null) throw failure; return Task.FromResult(answer); }, System32, Program);
        answer = 1; Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Absent, "exit 1: no such rule");
        answer = 0; Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Present, "exit 0: the rule is there");
        answer = 2; Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown, "any other exit: unknown");
        Check(launched.All(info => !info.UseShellExecute && info.Verb.Length == 0), "reading the rule never elevates");
        launched.Clear(); answer = 0;
        Check(await runner.AddAsync([43139], CancellationToken.None) == new FirewallChangeResult(FirewallChange.Done) && launched.Single().Verb == WindowsFirewallRule.ElevationVerb
            && launched.Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.AddArguments(Program, [43139])!), "add elevates netsh once with the rule");
        launched.Clear();
        Check(await runner.RemoveAsync(CancellationToken.None) == new FirewallChangeResult(FirewallChange.Done) && launched.Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.DeleteArguments()), "remove elevates the delete");
        launched.Clear();
        Check(await runner.ApplyAsync([], CancellationToken.None) == new FirewallChangeResult(FirewallChange.Done) && launched.Single().Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.DeleteArguments()), "an empty port set removes the rule");
        failure = new Win32Exception(WindowsFirewallRule.ElevationCancelled);
        Check((await runner.AddAsync([43139], CancellationToken.None)).Kind == FirewallChange.Cancelled, "a declined UAC prompt is a cancel, not an error");
        failure = new Win32Exception(5);
        Check((await runner.AddAsync([43139], CancellationToken.None)).Kind == FirewallChange.Failed, "any other launch error is a failure");
        Check(await runner.QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown, "a query that cannot run is unknown");
        failure = null; answer = 1;
        Check(await runner.AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Failed, Detail: "1" }, "netsh's exit code is kept");
        launched.Clear();
        Check(await runner.AddAsync([], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "noPorts" } && launched.Count == 0, "no port, no command");
        Check(await new WindowsFirewallRuleRunner((info, _) => { launched.Add(info); return Task.FromResult(0); }, System32, "MightyClaude.exe").AddAsync([43139], CancellationToken.None) is { Kind: FirewallChange.Refused, Detail: "badProgram" } && launched.Count == 0, "a relative program path, no command");
        Check(await new WindowsFirewallRuleRunner((info, _) => { launched.Add(info); return Task.FromResult(0); }, null, Program).QueryAsync(CancellationToken.None) == FirewallRuleState.Unknown && launched.Count == 0, "no system folder, nothing runs");
    }
}
```

In `Verification.cs`, add after the `mobile direct: the host advertises its endpoints` line:

```csharp
        await Test("windows firewall: the rule is built from constants", WindowsFirewallVerification.TheRuleIsBuiltFromConstants);
        await Test("windows firewall: the runner elevates only that command", WindowsFirewallVerification.TheRunnerElevatesOnlyThatCommand);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash /private/tmp/mighty-core-checks.sh 'windows firewall'`
Expected: the build fails with `error CS0103: The name 'WindowsFirewallRule' does not exist in the current context`.

- [ ] **Step 3: Implement**

Create `native/windows/MightyClaude.Core/WindowsFirewallRule.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public enum FirewallRuleState { Unknown, Present, Absent }
public enum FirewallChange { Done, Cancelled, Refused, Failed }
/// <param name="Detail">Refused: <c>noPorts</c>, <c>badProgram</c> or <c>noSystemDirectory</c>. Failed: netsh's exit code or the launch error.</param>
public sealed record FirewallChangeResult(FirewallChange Kind, string? Detail = null);

/// <summary>
/// The one Windows Firewall rule the app keeps (spec 2026-10-17-windows-catch-up-design.md, "Windows Firewall"): inbound TCP on the
/// app's own ports, only from Tailscale's ranges, only for the app's own executable. This is the one place the app asks Windows to
/// elevate a command, and the command is fixed: <c>%SystemRoot%\System32\netsh.exe advfirewall firewall add|delete rule …</c>, built
/// from these constants, the validated exe path and the validated ports. Reading whether the rule exists needs no elevation.
/// </summary>
public static partial class WindowsFirewallRule
{
    public const string RuleName = "Mighty Claude (Tailscale)";
    public const string RemoteRanges = "100.64.0.0/10,fd7a:115c:a1e0::/48";
    public const string ElevationVerb = "runas";
    /// <summary>ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    public const int ElevationCancelled = 1223;

    /// <summary>Windows' system folder as Windows reports it; null off Windows or when it reads as no absolute drive path.</summary>
    public static string? SystemDirectory => OperatingSystem.IsWindows() && ValidSystemDirectory(Environment.SystemDirectory) ? Environment.SystemDirectory : null;

    public static bool ValidPort(int port) => port is >= 1024 and <= 65535;
    public static bool ValidSystemDirectory(string? path) => path is { Length: > 3 and <= 260 } && SystemDirectoryPattern().IsMatch(path);

    /// <summary><paramref name="path"/> when it is a drive-letter absolute path to an .exe with no quote, %, control character, '/', second ':' or '.'/'..' segment; else null.</summary>
    public static string? ValidProgram(string? path) =>
        path is { Length: > 7 and <= 1024 } && ProgramPattern().IsMatch(path) && !path.Split('\\').Skip(1).Any(segment => segment is "." or "..") ? path : null;

    /// <summary>The ports for <c>localport=</c>: each 1024-65535, sorted, each once, at least one, at most 16; else null.</summary>
    public static string? PortList(IEnumerable<int> ports)
    {
        var list = ports.Distinct().Order().ToArray();
        return list.Length is > 0 and <= 16 && list.All(ValidPort) ? string.Join(",", list.Select(port => port.ToString(CultureInfo.InvariantCulture))) : null;
    }

    public static IReadOnlyList<string>? AddArguments(string program, IEnumerable<int> ports) =>
        ValidProgram(program) is { } valid && PortList(ports) is { } list
            ? ["advfirewall", "firewall", "add", "rule", "name=" + RuleName, "dir=in", "action=allow", "protocol=TCP", "localport=" + list, "remoteip=" + RemoteRanges, "program=" + valid, "profile=any"]
            : null;

    public static IReadOnlyList<string> DeleteArguments() => ["advfirewall", "firewall", "delete", "rule", "name=" + RuleName];
    public static IReadOnlyList<string> ShowArguments() => ["advfirewall", "firewall", "show", "rule", "name=" + RuleName];

    /// <summary>The rule for exactly <paramref name="ports"/>: the add argv, or the delete argv when the set is empty; null when the path or a port is refused.</summary>
    public static IReadOnlyList<string>? Arguments(IReadOnlyCollection<int> ports, string program) => ports.Count == 0 ? DeleteArguments() : AddArguments(program, ports);

    /// <summary>netsh's own spelling: <c>key="value"</c> when the value holds a space, the token as it is otherwise. Values never hold a quote (validated).</summary>
    public static string CommandLine(IReadOnlyList<string> arguments) => string.Join(" ", arguments.Select(argument =>
    {
        if (!argument.Contains(' ')) return argument;
        var equals = argument.IndexOf('=');
        return equals > 0 ? argument[..(equals + 1)] + "\"" + argument[(equals + 1)..] + "\"" : "\"" + argument + "\"";
    }));

    private static string Netsh(string systemDirectory) => systemDirectory.TrimEnd('\\') + "\\netsh.exe";

    /// <summary>Whether the rule exists: <c>show rule</c>, unelevated and hidden; exit 0 means it is there, 1 that it is not.</summary>
    public static ProcessStartInfo QueryStartInfo(string systemDirectory) => new(Netsh(systemDirectory), CommandLine(ShowArguments()))
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = systemDirectory,
    };

    /// <summary>A change: netsh from the system folder, elevated through Windows' own UAC prompt, its window hidden.</summary>
    public static ProcessStartInfo ElevatedStartInfo(string systemDirectory, IReadOnlyList<string> arguments) => new(Netsh(systemDirectory), CommandLine(arguments))
    {
        UseShellExecute = true, Verb = ElevationVerb, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = systemDirectory,
    };

    [GeneratedRegex(@"^[A-Za-z]:\\(?:[^\\/:*?""<>|%\x00-\x1f]+\\)*[^\\/:*?""<>|%\x00-\x1f]+\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgramPattern();
    [GeneratedRegex(@"^[A-Za-z]:\\[^""%\x00-\x1f]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SystemDirectoryPattern();
}

/// <summary>
/// Runs the rule's commands. The app builds it with <see cref="ForThisApp"/>; the checks and the GUI smoke give it a recorder that
/// starts nothing, so no development run ever touches this PC's firewall.
/// </summary>
public sealed class WindowsFirewallRuleRunner(Func<ProcessStartInfo, CancellationToken, Task<int>> run, string? systemDirectory, string? program)
{
    public static WindowsFirewallRuleRunner ForThisApp() => new(RunAsync, WindowsFirewallRule.SystemDirectory, Environment.ProcessPath);

    public async Task<FirewallRuleState> QueryAsync(CancellationToken token)
    {
        if (systemDirectory is null) return FirewallRuleState.Unknown;
        try
        {
            return await run(WindowsFirewallRule.QueryStartInfo(systemDirectory), token) switch { 0 => FirewallRuleState.Present, 1 => FirewallRuleState.Absent, _ => FirewallRuleState.Unknown };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException) { return FirewallRuleState.Unknown; }
    }

    public Task<FirewallChangeResult> AddAsync(IEnumerable<int> ports, CancellationToken token)
    {
        var list = ports.ToArray();
        if (WindowsFirewallRule.PortList(list) is null) return Task.FromResult(new FirewallChangeResult(FirewallChange.Refused, "noPorts"));
        if (program is null || WindowsFirewallRule.ValidProgram(program) is null) return Task.FromResult(new FirewallChangeResult(FirewallChange.Refused, "badProgram"));
        return Elevated(WindowsFirewallRule.AddArguments(program, list)!, token);
    }

    public Task<FirewallChangeResult> RemoveAsync(CancellationToken token) => Elevated(WindowsFirewallRule.DeleteArguments(), token);

    /// <summary>The rule for exactly <paramref name="ports"/>: added for a set, removed for none (part 3 passes the union of its ports and the phone's).</summary>
    public Task<FirewallChangeResult> ApplyAsync(IReadOnlyCollection<int> ports, CancellationToken token) => ports.Count == 0 ? RemoveAsync(token) : AddAsync(ports, token);

    private async Task<FirewallChangeResult> Elevated(IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (systemDirectory is null) return new(FirewallChange.Refused, "noSystemDirectory");
        try
        {
            var code = await run(WindowsFirewallRule.ElevatedStartInfo(systemDirectory, arguments), token);
            return code == 0 ? new(FirewallChange.Done) : new(FirewallChange.Failed, code.ToString(CultureInfo.InvariantCulture));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == WindowsFirewallRule.ElevationCancelled) { return new(FirewallChange.Cancelled); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException) { return new(FirewallChange.Failed, ex.Message); }
    }

    /// <summary>Starts the process off the caller's thread (an elevated start waits on the UAC prompt) and waits up to 30 s for its exit code.</summary>
    private static async Task<int> RunAsync(ProcessStartInfo info, CancellationToken token)
    {
        using var process = await Task.Run(() => Process.Start(info), token) ?? throw new InvalidOperationException("netsh did not start");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(30));
        if (info.RedirectStandardOutput) { _ = process.StandardOutput.ReadToEndAsync(limit.Token); _ = process.StandardError.ReadToEndAsync(limit.Token); }
        await process.WaitForExitAsync(limit.Token);
        return process.ExitCode;
    }
}
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-core-checks.sh 'windows firewall'`
Expected: both `PASS windows firewall: …` lines, and `FAIL: 9`.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/windows/MightyClaude.Core/WindowsFirewallRule.cs native/windows/MightyClaude.Core.Tests/WindowsFirewallVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the one Windows Firewall rule the app may add: inbound TCP on its ports from Tailscale's ranges for its own exe, a fixed netsh command from the system folder elevated through UAC, read without elevation" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Windows-only words in four languages

**Files:**
- Create: `native/windows/MightyClaude.Core/Remote/MobileDirectStrings.cs`
- Modify: `locales/ko.json`, `locales/en.json`, `locales/zh.json`, `locales/ja.json`
- Modify (byte-identical copies): `mobile/src/locales/{ko,en,zh,ja}.json`, `native/macos/Sources/MightyCore/Resources/Locales/{ko,en,zh,ja}.json`
- Modify: `docs/i18n.md` (rewritten by `node scripts/check-locales.js`)
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (registration)
- Test: `native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs` (add a method)

**Interfaces:**
- Consumes:
  - `FirewallRuleState`, `FirewallChange`, `FirewallChangeResult` (Task 8)
  - `Locale.Catalogue(string)`, `Locale.Languages`
- Produces: `public static class MobileDirectStrings` with these members:
  - `IReadOnlyList<string> Keys`
  - `string Description`, `string PortHint`, `string FirewallDescription`
  - `string FirewallState(FirewallRuleState)`, `string FirewallButton(FirewallRuleState)`
  - `string? FirewallOutcome(FirewallChangeResult)`

  The Mac's `mobileRemote.direct.toggle`, `.portLabel` and `.status.*` keys are reused as they are.

- [ ] **Step 1: Write the failing test**

Add to `MobileDirectVerification`:

```csharp
    internal static Task TheWindowsWordsAreInEveryLanguage()
    {
        foreach (var language in Locale.Languages)
        {
            var catalogue = Locale.Catalogue(language);
            foreach (var key in MobileDirectStrings.Keys) Check(catalogue.TryGetValue(key, out var text) && text.Trim().Length > 0, language + " lacks " + key);
            Check(catalogue["mobileRemote.direct.windows.firewall.failedTemplate"].Contains("{error}", StringComparison.Ordinal), language + ": the failure names its error");
            Check(!catalogue["mobileRemote.direct.windows.description"].Contains("Mac", StringComparison.Ordinal), language + ": the Windows description names no Mac");
        }
        Check(MobileDirectStrings.Keys.Count == 12 && MobileDirectStrings.Keys.All(key => key.StartsWith("mobileRemote.direct.windows.", StringComparison.Ordinal)), "twelve Windows-only keys");
        Check(MobileDirectStrings.FirewallState(FirewallRuleState.Present) == Locale.Get("mobileRemote.direct.windows.firewall.present") && MobileDirectStrings.FirewallState(FirewallRuleState.Absent) == Locale.Get("mobileRemote.direct.windows.firewall.absent")
            && MobileDirectStrings.FirewallState(FirewallRuleState.Unknown) == Locale.Get("mobileRemote.direct.windows.firewall.unknown"), "the rule's three states");
        Check(MobileDirectStrings.FirewallButton(FirewallRuleState.Present) == Locale.Get("mobileRemote.direct.windows.firewall.removeButton") && MobileDirectStrings.FirewallButton(FirewallRuleState.Absent) == Locale.Get("mobileRemote.direct.windows.firewall.addButton"), "the button adds when absent and removes when present");
        Check(MobileDirectStrings.FirewallOutcome(new(FirewallChange.Done)) is null
            && MobileDirectStrings.FirewallOutcome(new(FirewallChange.Cancelled)) == Locale.Get("mobileRemote.direct.windows.firewall.cancelled")
            && MobileDirectStrings.FirewallOutcome(new(FirewallChange.Refused, "noPorts")) == Locale.Get("mobileRemote.direct.windows.firewall.noPorts")
            && MobileDirectStrings.FirewallOutcome(new(FirewallChange.Refused, "badProgram")) == Locale.Get("mobileRemote.direct.windows.firewall.badProgram")
            && MobileDirectStrings.FirewallOutcome(new(FirewallChange.Failed, "1")) == Locale.Get("mobileRemote.direct.windows.firewall.failedTemplate", new Dictionary<string, string> { ["error"] = "1" }), "every outcome has its words");
        return Task.CompletedTask;
    }
```

In `Verification.cs`, add after the two `windows firewall:` lines:

```csharp
        await Test("mobile direct: the Windows words are in every language", MobileDirectVerification.TheWindowsWordsAreInEveryLanguage);
```

Create `native/windows/MightyClaude.Core/Remote/MobileDirectStrings.cs`:

```csharp
namespace MightyClaude.Core;

/// <summary>
/// The direct path's words only the Windows app says (mobileRemote.direct.windows.*): this PC instead of this Mac, and the Windows
/// Firewall rule's row. The switch, the port label and the status line reuse the Mac's mobileRemote.direct.* keys.
/// </summary>
public static class MobileDirectStrings
{
    public static readonly IReadOnlyList<string> Keys =
    [
        "mobileRemote.direct.windows.description", "mobileRemote.direct.windows.portHint", "mobileRemote.direct.windows.firewall.description",
        "mobileRemote.direct.windows.firewall.present", "mobileRemote.direct.windows.firewall.absent", "mobileRemote.direct.windows.firewall.unknown",
        "mobileRemote.direct.windows.firewall.addButton", "mobileRemote.direct.windows.firewall.removeButton", "mobileRemote.direct.windows.firewall.cancelled",
        "mobileRemote.direct.windows.firewall.failedTemplate", "mobileRemote.direct.windows.firewall.badProgram", "mobileRemote.direct.windows.firewall.noPorts",
    ];

    public static string Description => Locale.Get("mobileRemote.direct.windows.description");
    public static string PortHint => Locale.Get("mobileRemote.direct.windows.portHint");
    public static string FirewallDescription => Locale.Get("mobileRemote.direct.windows.firewall.description");

    public static string FirewallState(FirewallRuleState state) => Locale.Get(state switch
    {
        FirewallRuleState.Present => "mobileRemote.direct.windows.firewall.present",
        FirewallRuleState.Absent => "mobileRemote.direct.windows.firewall.absent",
        _ => "mobileRemote.direct.windows.firewall.unknown",
    });

    public static string FirewallButton(FirewallRuleState state) =>
        Locale.Get(state == FirewallRuleState.Present ? "mobileRemote.direct.windows.firewall.removeButton" : "mobileRemote.direct.windows.firewall.addButton");

    /// <summary>What a change that did not happen says; null after one that did.</summary>
    public static string? FirewallOutcome(FirewallChangeResult result) => result.Kind switch
    {
        FirewallChange.Done => null,
        FirewallChange.Cancelled => Locale.Get("mobileRemote.direct.windows.firewall.cancelled"),
        FirewallChange.Refused when result.Detail == "noPorts" => Locale.Get("mobileRemote.direct.windows.firewall.noPorts"),
        FirewallChange.Refused when result.Detail == "badProgram" => Locale.Get("mobileRemote.direct.windows.firewall.badProgram"),
        _ => Locale.Get("mobileRemote.direct.windows.firewall.failedTemplate", new Dictionary<string, string> { ["error"] = result.Detail ?? "" }),
    };
}
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct: the Windows words'`
Expected: `FAIL mobile direct: the Windows words are in every language: ko lacks mobileRemote.direct.windows.description`, and `FAIL: 10`.

- [ ] **Step 3: Add the keys and copy the files**

Write the 12 keys to a scratch file outside the repository, insert them after `mobileRemote.direct.error.bindTimeout` in each language, and copy each file to its two client copies. The four files round-trip byte-for-byte through `JSON.stringify(…, null, 2) + "\n"`, so only the new lines change.

```bash
cat > /private/tmp/direct-windows-keys.json <<'JSON'
{
  "ko": {
    "mobileRemote.direct.windows.description": "휴대폰이 이 PC와 같은 Tailscale 네트워크에 있으면 릴레이를 거치지 않고 바로 연결하고, 닿지 않으면 릴레이를 씁니다. 내용은 릴레이와 똑같이 종단 간 암호화되며, 이 PC는 Tailscale 주소에서만 연결을 받습니다.",
    "mobileRemote.direct.windows.portHint": "방화벽 규칙은 추가할 때의 포트만 엽니다. 포트를 바꾸면 규칙을 지우고 다시 추가하세요.",
    "mobileRemote.direct.windows.firewall.description": "Windows 방화벽에 규칙을 하나 둡니다. 이 앱이 이 포트로 Tailscale 주소(100.64.0.0/10, fd7a:115c:a1e0::/48)에서 오는 TCP 연결만 받게 합니다. 추가하거나 지울 때 Windows가 관리자 확인을 한 번 묻습니다.",
    "mobileRemote.direct.windows.firewall.present": "방화벽 규칙: 있음",
    "mobileRemote.direct.windows.firewall.absent": "방화벽 규칙: 없음",
    "mobileRemote.direct.windows.firewall.unknown": "방화벽 규칙: 확인하지 못함",
    "mobileRemote.direct.windows.firewall.addButton": "규칙 추가",
    "mobileRemote.direct.windows.firewall.removeButton": "규칙 지우기",
    "mobileRemote.direct.windows.firewall.cancelled": "관리자 확인이 취소되어 규칙을 바꾸지 않았습니다.",
    "mobileRemote.direct.windows.firewall.failedTemplate": "방화벽 규칙을 바꾸지 못했습니다: {error}",
    "mobileRemote.direct.windows.firewall.badProgram": "이 앱의 실행 파일 경로로는 규칙을 만들 수 없습니다.",
    "mobileRemote.direct.windows.firewall.noPorts": "직접 연결을 켜야 규칙을 추가할 수 있습니다."
  },
  "en": {
    "mobileRemote.direct.windows.description": "When the phone is on the same Tailscale network as this PC, it connects straight to it without the relay, and falls back to the relay when it cannot. Everything stays end-to-end encrypted exactly as through the relay, and this PC accepts connections only on its Tailscale addresses.",
    "mobileRemote.direct.windows.portHint": "The firewall rule opens only the port it was added with. After changing the port, remove the rule and add it again.",
    "mobileRemote.direct.windows.firewall.description": "Adds one Windows Firewall rule that lets this app accept TCP connections on this port only from Tailscale addresses (100.64.0.0/10, fd7a:115c:a1e0::/48). Windows asks for administrator approval once when the rule is added or removed.",
    "mobileRemote.direct.windows.firewall.present": "Firewall rule: present",
    "mobileRemote.direct.windows.firewall.absent": "Firewall rule: none",
    "mobileRemote.direct.windows.firewall.unknown": "Firewall rule: unknown",
    "mobileRemote.direct.windows.firewall.addButton": "Add rule",
    "mobileRemote.direct.windows.firewall.removeButton": "Remove rule",
    "mobileRemote.direct.windows.firewall.cancelled": "Administrator approval was cancelled, so the rule was not changed.",
    "mobileRemote.direct.windows.firewall.failedTemplate": "Could not change the firewall rule: {error}",
    "mobileRemote.direct.windows.firewall.badProgram": "A rule cannot be made for this app's executable path.",
    "mobileRemote.direct.windows.firewall.noPorts": "Turn on the direct connection to add the rule."
  },
  "zh": {
    "mobileRemote.direct.windows.description": "手机与这台电脑在同一个 Tailscale 网络中时，会不经中继直接连接；连不上时改用中继。内容与经由中继时一样端到端加密，这台电脑只在其 Tailscale 地址上接受连接。",
    "mobileRemote.direct.windows.portHint": "防火墙规则只开放添加时的端口。更改端口后，请删除规则并重新添加。",
    "mobileRemote.direct.windows.firewall.description": "在 Windows 防火墙中添加一条规则：只允许本应用在此端口上接受来自 Tailscale 地址（100.64.0.0/10、fd7a:115c:a1e0::/48）的 TCP 连接。添加或删除规则时，Windows 会请求一次管理员确认。",
    "mobileRemote.direct.windows.firewall.present": "防火墙规则：已添加",
    "mobileRemote.direct.windows.firewall.absent": "防火墙规则：无",
    "mobileRemote.direct.windows.firewall.unknown": "防火墙规则：无法确认",
    "mobileRemote.direct.windows.firewall.addButton": "添加规则",
    "mobileRemote.direct.windows.firewall.removeButton": "删除规则",
    "mobileRemote.direct.windows.firewall.cancelled": "管理员确认已取消，规则未更改。",
    "mobileRemote.direct.windows.firewall.failedTemplate": "无法更改防火墙规则：{error}",
    "mobileRemote.direct.windows.firewall.badProgram": "无法为本应用的可执行文件路径创建规则。",
    "mobileRemote.direct.windows.firewall.noPorts": "开启直接连接后才能添加规则。"
  },
  "ja": {
    "mobileRemote.direct.windows.description": "スマートフォンがこの PC と同じ Tailscale ネットワークにあれば、リレーを通さず直接接続し、届かないときはリレーを使います。内容はリレー経由と同じくエンドツーエンドで暗号化され、この PC は Tailscale のアドレスでのみ接続を受け付けます。",
    "mobileRemote.direct.windows.portHint": "ファイアウォール規則は追加したときのポートだけを開きます。ポートを変えたら、規則を削除して追加し直してください。",
    "mobileRemote.direct.windows.firewall.description": "Windows ファイアウォールに規則を 1 つ追加し、このアプリがこのポートで Tailscale のアドレス(100.64.0.0/10、fd7a:115c:a1e0::/48)からの TCP 接続だけを受け付けられるようにします。規則の追加と削除のたびに、Windows が管理者の確認を 1 回求めます。",
    "mobileRemote.direct.windows.firewall.present": "ファイアウォール規則: あり",
    "mobileRemote.direct.windows.firewall.absent": "ファイアウォール規則: なし",
    "mobileRemote.direct.windows.firewall.unknown": "ファイアウォール規則: 確認できません",
    "mobileRemote.direct.windows.firewall.addButton": "規則を追加",
    "mobileRemote.direct.windows.firewall.removeButton": "規則を削除",
    "mobileRemote.direct.windows.firewall.cancelled": "管理者の確認が取り消されたため、規則は変更されていません。",
    "mobileRemote.direct.windows.firewall.failedTemplate": "ファイアウォール規則を変更できませんでした: {error}",
    "mobileRemote.direct.windows.firewall.badProgram": "このアプリの実行ファイルのパスでは規則を作成できません。",
    "mobileRemote.direct.windows.firewall.noPorts": "直接接続をオンにすると規則を追加できます。"
  }
}
JSON
cd /Users/young/Work/MightyClaude
node -e '
const fs = require("fs");
const add = JSON.parse(fs.readFileSync("/private/tmp/direct-windows-keys.json", "utf8"));
for (const lang of ["ko", "en", "zh", "ja"]) {
  const file = `locales/${lang}.json`;
  const entries = Object.entries(JSON.parse(fs.readFileSync(file, "utf8")));
  const at = entries.findIndex(([key]) => key === "mobileRemote.direct.error.bindTimeout");
  if (at < 0 || entries.some(([key]) => key.startsWith("mobileRemote.direct.windows."))) throw new Error(file + ": anchor missing or keys already present");
  entries.splice(at + 1, 0, ...Object.entries(add[lang]));
  fs.writeFileSync(file, JSON.stringify(Object.fromEntries(entries), null, 2) + "\n");
}'
for lang in ko en zh ja; do cp "locales/$lang.json" "mobile/src/locales/$lang.json"; cp "locales/$lang.json" "native/macos/Sources/MightyCore/Resources/Locales/$lang.json"; done
node scripts/check-locales.js
```

Expected from `node scripts/check-locales.js`: every check passes, and `docs/i18n.md` is rewritten. The key counts go up by 12 in each language.

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct|windows firewall|tailscale addresses'`
Expected: `PASS mobile direct: the Windows words are in every language`, every earlier new line still PASS, and `FAIL: 9`.

Run: `node scripts/check-locales.js --check`
Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/windows/MightyClaude.Core/Remote/MobileDirectStrings.cs locales/ko.json locales/en.json locales/zh.json locales/ja.json mobile/src/locales/ko.json mobile/src/locales/en.json mobile/src/locales/zh.json mobile/src/locales/ja.json native/macos/Sources/MightyCore/Resources/Locales/ko.json native/macos/Sources/MightyCore/Resources/Locales/en.json native/macos/Sources/MightyCore/Resources/Locales/zh.json native/macos/Sources/MightyCore/Resources/Locales/ja.json docs/i18n.md native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) the direct path's Windows words in four languages: this PC instead of this Mac, and the firewall rule's state, buttons, note and outcomes" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: WinUI: the direct rows, the firewall row and the smoke step

**Files:**
- Create: `native/windows/MightyClaude.WinUI/MainWindow.MobileDirect.cs`
- Create: `native/windows/MightyClaude.WinUI/MainWindow.MobileDirectSmoke.cs`
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.MobileRemote.cs`
- Modify: `native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs`
- Modify: `scripts/test-native-windows.ps1`
- Modify: `native/windows/MightyClaude.Core.Tests/Verification.cs` (registration)
- Test: `native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs` (add a method and `Root()`)

**Interfaces:**
- Consumes:
  - `MobileRemoteSettings.DirectEnabled`/`DirectPort`, `MobileRelayHost.DirectStatus`/`DirectEndpoints` (Task 5)
  - `MobileDesktopRouter.DirectEndpoints` (Task 7)
  - `MobileDirect.ValidPort`/`DefaultPort`, `MobileDirectStatus`, `MobileDirectStatusText.Detail` (Task 3)
  - `WindowsFirewallRule.*`, `WindowsFirewallRuleRunner`, `FirewallRuleState` (Task 8)
  - `MobileDirectStrings.*` (Task 9)
  - `ClaudePluginVerification.WinUISource()`
- Produces:
  - In WinUI:
    - `internal IReadOnlyList<int> FirewallPorts()`. Part 3 extends it with 43137.
    - `internal Task ChangeFirewallRule(bool add)`
    - `private Task<Dictionary<string, object?>> RunMobileDirectSmoke()`
  - Automation ids:
    - `settings-mobile-direct-toggle`, `settings-mobile-direct-port`, `settings-mobile-direct-apply`, `settings-mobile-direct-status`
    - `settings-mobile-firewall-state`, `settings-mobile-firewall-button`
  - Smoke result keys `mobileDirect.{rows,status,firewallRow,addCommand,removeCommand,passed}`.

- [ ] **Step 1: Write the failing test**

Add to `MobileDirectVerification`:

```csharp
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "tailscale-addresses.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }

    /// The WinUI rows, the firewall row and the smoke step, read from the source (WinUI does not build here).
    internal static Task TheSettingsAndTheSmokeAreWired()
    {
        var winui = ClaudePluginVerification.WinUISource();
        string Source(string file) => File.ReadAllText(Path.Combine(winui, file));
        var remote = Source("MainWindow.MobileRemote.cs");
        foreach (var id in new[] { "settings-mobile-direct-toggle", "settings-mobile-direct-port", "settings-mobile-direct-apply", "settings-mobile-direct-status", "settings-mobile-firewall-state", "settings-mobile-firewall-button" })
            Check(remote.Contains("\"" + id + "\"", StringComparison.Ordinal), "the mobile remote section draws " + id);
        Check(remote.Contains("mobileRouter.DirectEndpoints = () => mobileHost?.DirectEndpoints ?? [];", StringComparison.Ordinal), "/m1/info reads the host's endpoints");
        Check(remote.Contains("MobileDirectStrings.Description", StringComparison.Ordinal) && remote.Contains("MobileDirectStatusText.Detail(", StringComparison.Ordinal) && remote.Contains("MobileDirectStrings.FirewallState(", StringComparison.Ordinal), "the rows say what Core words");
        Check(remote.Contains("await Act(RemoveFirewallRuleIfUnneeded);", StringComparison.Ordinal), "turning the path off takes the rule away from that same click");
        var direct = Source("MainWindow.MobileDirect.cs");
        Check(direct.Contains("if (options.SmokeTest && firewallRunner is null) return;", StringComparison.Ordinal), "a smoke run never reads or changes this PC's firewall");
        // The one elevated command lives in Core; no other source names the verb.
        var core = Path.Combine(Path.GetDirectoryName(winui)!, "MightyClaude.Core");
        var sep = Path.DirectorySeparatorChar.ToString();
        var elevating = Directory.GetFiles(winui, "*.cs", SearchOption.AllDirectories).Concat(Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains(sep + "obj" + sep, StringComparison.Ordinal) && !file.Contains(sep + "bin" + sep, StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("\"runas\"", StringComparison.Ordinal)).Select(Path.GetFileName).ToArray();
        Check(elevating.SequenceEqual(["WindowsFirewallRule.cs"]), "only the firewall rule elevates: " + string.Join(", ", elevating));
        Check(Source("MainWindow.Smoke.cs").Contains("result[\"mobileDirect\"] = await RunMobileDirectSmoke();", StringComparison.Ordinal), "the GUI smoke drives the rows");
        var script = File.ReadAllText(Path.Combine(Root(), "scripts", "test-native-windows.ps1"));
        Check(script.Contains("$result.mobileDirect.$key", StringComparison.Ordinal) && script.Contains("'removeCommand'", StringComparison.Ordinal), "CI reads the smoke's result");
        return Task.CompletedTask;
    }
```

In `Verification.cs`, add after the `mobile direct: the Windows words…` line:

```csharp
        await Test("mobile direct: the settings rows and the smoke are wired", MobileDirectVerification.TheSettingsAndTheSmokeAreWired);
```

- [ ] **Step 2: Run it and expect it to fail**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct: the settings'`
Expected: `FAIL mobile direct: the settings rows and the smoke are wired: the mobile remote section draws settings-mobile-direct-toggle`, and `FAIL: 10`.

- [ ] **Step 3: Implement**

3a. Create `native/windows/MightyClaude.WinUI/MainWindow.MobileDirect.cs`:

```csharp
using MightyClaude.Core;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>The firewall rule as last read; Unknown until read, and for a whole smoke run that set no runner.</summary>
    private FirewallRuleState firewallState = FirewallRuleState.Unknown;
    /// <summary>What the last add or remove that did not happen said; null after one that did.</summary>
    private string? firewallMessage;
    /// <summary>Runs netsh; the GUI smoke puts a recorder here that starts nothing.</summary>
    private WindowsFirewallRuleRunner? firewallRunner;
    /// <summary>The GUI smoke's direct-path status: fixed fake endpoints, never this PC's.</summary>
    private MobileDirectStatus? mobileDirectStatusOverride;
    private MobileDirectStatus MobileDirectStatusNow => mobileDirectStatusOverride ?? mobileHost?.DirectStatus ?? MobileDirectStatus.Off;

    /// <summary>The ports the rule must open: the phone's direct port while the remote and its direct switch are on (part 3 adds the remote workspaces' port).</summary>
    internal IReadOnlyList<int> FirewallPorts() => service.Snapshot.MobileRemote is { Enabled: true, DirectEnabled: true } remote ? [remote.DirectPort] : [];

    /// <summary>Reads whether the rule exists, without elevation.</summary>
    private async Task RefreshFirewallState()
    {
        if (options.SmokeTest && firewallRunner is null) return;
        firewallState = await (firewallRunner ??= WindowsFirewallRuleRunner.ForThisApp()).QueryAsync(CancellationToken.None);
    }

    /// <summary>The firewall row's button: one netsh command, elevated by Windows' own UAC prompt, then the rule read again.</summary>
    internal async Task ChangeFirewallRule(bool add)
    {
        if (options.SmokeTest && firewallRunner is null) return;
        var runner = firewallRunner ??= WindowsFirewallRuleRunner.ForThisApp();
        var result = add ? await runner.AddAsync(FirewallPorts(), CancellationToken.None) : await runner.RemoveAsync(CancellationToken.None);
        firewallMessage = MobileDirectStrings.FirewallOutcome(result);
        await RefreshFirewallState();
    }

    /// <summary>Turning the remote or its direct path off takes the rule away when nothing needs it any more: one UAC prompt from that same click.</summary>
    private async Task RemoveFirewallRuleIfUnneeded()
    {
        if (firewallState == FirewallRuleState.Present && FirewallPorts().Count == 0) await ChangeFirewallRule(add: false);
    }
}
```

3b. In `MainWindow.MobileRemote.cs`, `InitializeMobileRemote`: after the line `mobileRouter.HostId = mobileHost.HostId;`, add:

```csharp
        mobileRouter.DirectEndpoints = () => mobileHost?.DirectEndpoints ?? [];
```

3c. In `BuildMobileRemoteSection`, right after the line `var legacy = SettingsSwitch(legacyLabel, service.Snapshot.MobileRemote.AllowLegacyPhones, "settings-mobile-legacy-toggle");`, add:

```csharp
        // The direct path (M/MobileRemoteSettingsView.swift `direct`): its switch over the Windows wording, the port with its own apply
        // button, what it found, and the Windows Firewall rule's row with the one button that asks Windows to elevate.
        var directLabel = Locale.Get("mobileRemote.direct.toggle");
        var direct = SettingsSwitch(directLabel, service.Snapshot.MobileRemote.DirectEnabled, "settings-mobile-direct-toggle");
        var portLabel = Locale.Get("mobileRemote.direct.portLabel");
        var port = SettingsField(new TextBox { Text = service.Snapshot.MobileRemote.DirectPort.ToString(System.Globalization.CultureInfo.InvariantCulture), MaxLength = 5, Width = MobileDirectPortWidth });
        AutomationProperties.SetName(port, portLabel); AutomationProperties.SetAutomationId(port, "settings-mobile-direct-port");
        var directDot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var directStatus = SettingsText("", 11, DesignToken.Ink2, selectable: true); AutomationProperties.SetAutomationId(directStatus, "settings-mobile-direct-status");
        var firewallText = SettingsText("", 11, DesignToken.Ink2); AutomationProperties.SetAutomationId(firewallText, "settings-mobile-firewall-state");
        Button firewallButton = null!, applyPort = null!;
        Border portRow = null!, directStatusRow = null!, firewallRow = null!;
```

3d. Right after the local function `void ShowApply() { … }`, add:

```csharp
        int? TypedPort() => int.TryParse(port.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && MobileDirect.ValidPort(value) ? value : null;
        void ShowApplyPort() => applyPort.IsEnabled = !writing && !options.SmokeTest && TypedPort() is { } typed && typed != service.Snapshot.MobileRemote.DirectPort;
        Task ApplyPort() => TypedPort() is { } typed && typed != service.Snapshot.MobileRemote.DirectPort ? Write(saved => saved with { DirectPort = typed }) : Task.CompletedTask;
```

3e. In `ShowControls`, make two replacements:

- `showing = true; enabled.IsChecked = saved.Enabled; legacy.IsChecked = saved.AllowLegacyPhones; showing = false;` becomes
  `showing = true; enabled.IsChecked = saved.Enabled; legacy.IsChecked = saved.AllowLegacyPhones; direct.IsChecked = saved.DirectEnabled; showing = false;`
- `enabled.IsEnabled = legacy.IsEnabled = !options.SmokeTest; ShowApply();` becomes
  `enabled.IsEnabled = legacy.IsEnabled = direct.IsEnabled = !options.SmokeTest; ShowApply(); ShowApplyPort(); ShowDirect();`

3f. In `Write`, make three replacements:

- Replace the first `writing = true; ShowApply();` with `writing = true; ShowApply(); ShowApplyPort();`.
- Replace `bool wantsOn = enabled.IsChecked == true, wantsLegacy = legacy.IsChecked == true;` with:

  ```csharp
                  bool wantsOn = enabled.IsChecked == true, wantsLegacy = legacy.IsChecked == true, wantsDirect = direct.IsChecked == true;
  ```

- Replace `if (meant is not null && !closing && (wantsOn != meant.Enabled || wantsLegacy != meant.AllowLegacyPhones)) _ = Write(next => next with { Enabled = wantsOn, AllowLegacyPhones = wantsLegacy });` with:

  ```csharp
                  if (meant is not null && !closing && (wantsOn != meant.Enabled || wantsLegacy != meant.AllowLegacyPhones || wantsDirect != meant.DirectEnabled)) _ = Write(next => next with { Enabled = wantsOn, AllowLegacyPhones = wantsLegacy, DirectEnabled = wantsDirect });
  ```

3g. Right after the line `AutomationProperties.SetAutomationId(apply, "settings-mobile-apply");`, add:

```csharp
        applyPort = SettingsPush(Button(Locale.Get("settings.mobileRemote.applyButton"), ApplyPort), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(applyPort, "settings-mobile-direct-apply");
        port.TextChanged += (_, _) => ShowApplyPort();
        // Enter in the field applies the port, as the Mac's field does on submit.
        port.KeyDown += async (_, args) => { if (args.Key != Windows.System.VirtualKey.Enter || !applyPort.IsEnabled) return; args.Handled = true; await ApplyPort(); };
        firewallButton = SettingsPush(SafeButton(MobileDirectStrings.FirewallButton(FirewallRuleState.Absent), async () =>
        {
            await ChangeFirewallRule(firewallState != FirewallRuleState.Present);
            if (!closing && rows.IsLoaded) ShowDirect();
        }), SettingsControlSize.Small);
        AutomationProperties.SetAutomationId(firewallButton, "settings-mobile-firewall-button");
```

3h. Replace the `enabled.Unchecked` handler line, and add the direct handlers after the `legacy.Unchecked` line:

```csharp
        enabled.Unchecked += async (_, _) => { if (showing) return; await Write(saved => saved with { Enabled = false }); await Act(RemoveFirewallRuleIfUnneeded); if (!closing && rows.IsLoaded) ShowDirect(); };
```

```csharp
        direct.Checked += async (_, _) => { if (!showing) await Write(saved => saved with { DirectEnabled = true }); };
        direct.Unchecked += async (_, _) => { if (showing) return; await Write(saved => saved with { DirectEnabled = false }); await Act(RemoveFirewallRuleIfUnneeded); if (!closing && rows.IsLoaded) ShowDirect(); };
```

3i. Right after the line `SettingsRow(rows, detail);`, add the rows:

```csharp
        SettingsRow(rows, SettingsLabeled(SettingsTitled(directLabel, MobileDirectStrings.Description), direct, top: true));
        var portLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        portLine.Children.Add(SettingsText(portLabel)); portLine.Children.Add(port); portLine.Children.Add(applyPort);
        portRow = SettingsRow(rows, portLine);
        var directLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        directLine.Children.Add(directDot); directLine.Children.Add(directStatus);
        directStatusRow = SettingsRow(rows, directLine);
        var firewallWords = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
        firewallWords.Children.Add(firewallText);
        firewallWords.Children.Add(SettingsText(MobileDirectStrings.FirewallDescription, 11, DesignToken.Ink2));
        firewallWords.Children.Add(SettingsText(MobileDirectStrings.PortHint, 11, DesignToken.Ink2));
        firewallRow = SettingsRow(rows, SettingsLabeled(firewallWords, firewallButton, top: true));
```

3j. Right after the local function `ShowState()`, add:

```csharp
        // The direct rows (M/MobileRemoteSettingsView.swift `direct`): the port and the status line under an on switch, the status only
        // while the remote is on; the firewall row while the path is on or the rule is still there to remove.
        void ShowDirect()
        {
            var saved = service.Snapshot.MobileRemote; var status = MobileDirectStatusNow; var words = MobileDirectStatusText.Detail(status);
            portRow.Visibility = saved.DirectEnabled ? Visibility.Visible : Visibility.Collapsed;
            directStatusRow.Visibility = saved.Enabled && saved.DirectEnabled && words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            firewallRow.Visibility = saved.DirectEnabled || firewallState == FirewallRuleState.Present ? Visibility.Visible : Visibility.Collapsed;
            directDot.Fill = brushes.Brush(status.Listening ? DesignToken.Done : DesignToken.Ink3);
            directStatus.Text = words;
            firewallText.Text = MobileDirectStrings.FirewallState(firewallState) + (firewallMessage is { } message ? " · " + message : "");
            var label = MobileDirectStrings.FirewallButton(firewallState); firewallButton.Content = label; AutomationProperties.SetName(firewallButton, label);
            firewallButton.IsEnabled = !options.SmokeTest && firewallState != FirewallRuleState.Unknown && (firewallState == FirewallRuleState.Present || FirewallPorts().Count > 0);
        }
```

3k. In `Refresh()`, replace `RefreshPairingText(); ShowState();` with `RefreshPairingText(); ShowState(); ShowDirect();`.

3l. Replace the `rows.Loaded` line with:

```csharp
        rows.Loaded += async (_, _) => { if (options.SmokeTest) { enabled.IsEnabled = relay.IsEnabled = legacy.IsEnabled = apply.IsEnabled = reconnect.IsEnabled = direct.IsEnabled = port.IsEnabled = applyPort.IsEnabled = firewallButton.IsEnabled = false; HidePairing(); RefreshPairingText(); return; } await Act(async () => { await InitializeMobileRemote(); await RefreshFirewallState(); if (closing || !rows.IsLoaded || mobileHost is null) return; mobileHost.StatusChanged += Updated; await Refresh(); }); };
```

3m. After `private const double MobileRelayFieldShare = 0.54;`, add:

```csharp
    /// <summary>The direct port field's width (the Mac's is 80).</summary>
    private const double MobileDirectPortWidth = 80;
```

3n. Create `native/windows/MightyClaude.WinUI/MainWindow.MobileDirectSmoke.cs`:

```csharp
using System.Diagnostics;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The direct path's rows in 모바일 리모트 over fixed fake endpoints and a firewall runner that records what it would run and starts
    /// nothing: the switch, the port and its apply button, the status line, the rule read (unelevated) before any click, then the add and
    /// remove commands exactly as Core builds them. Nothing on this PC is probed and no netsh runs.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunMobileDirectSmoke()
    {
        var checks = new Dictionary<string, object?>();
        const string program = @"C:\Program Files\Mighty Claude\MightyClaude.exe";
        const string system = @"C:\Windows\System32";
        var launched = new List<ProcessStartInfo>(); var present = false;
        var heldRunner = firewallRunner; var heldRemote = service.Snapshot.MobileRemote;
        firewallRunner = new((info, _) =>
        {
            launched.Add(info);
            if (info.Verb == WindowsFirewallRule.ElevationVerb) present = info.Arguments.StartsWith("advfirewall firewall add rule ", StringComparison.Ordinal);
            return Task.FromResult(info.Verb == WindowsFirewallRule.ElevationVerb || present ? 0 : 1);
        }, system, program);
        var fixedStatus = new MobileDirectStatus("listening", ["100.101.102.103:43139", "[fd7a:115c:a1e0::1234:5678]:43139"]);
        mobileDirectStatusOverride = fixedStatus;
        try
        {
            await service.UpdateAsync(s => s with { MobileRemote = s.MobileRemote with { Enabled = true, DirectEnabled = true, DirectPort = MobileDirect.DefaultPort } });
            await RefreshFirewallState();
            var rows = BuildMobileRemoteSection();
            T Element<T>(string id) where T : FrameworkElement => SettingsElements(rows).OfType<T>().FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == id)
                ?? throw new InvalidOperationException("the mobile remote section has no " + id);
            Require(Element<ToggleButton>("settings-mobile-direct-toggle").IsChecked == true, "the direct switch must start on");
            Require(Element<TextBox>("settings-mobile-direct-port").Text == "43139", "the port field must show 43139");
            Element<Button>("settings-mobile-direct-apply");
            checks["rows"] = true;
            Require(Element<TextBlock>("settings-mobile-direct-status").Text == MobileDirectStatusText.Detail(fixedStatus), "the status line must list the endpoints");
            checks["status"] = true;
            Require(Element<TextBlock>("settings-mobile-firewall-state").Text == MobileDirectStrings.FirewallState(FirewallRuleState.Absent), "the rule must read as absent");
            Require(launched.Count == 1 && !launched[0].UseShellExecute && launched[0].Verb != WindowsFirewallRule.ElevationVerb, "before a click only the unelevated query may run");
            checks["firewallRow"] = true;
            await ChangeFirewallRule(add: true);
            var adds = launched.Where(info => info.Verb == WindowsFirewallRule.ElevationVerb).ToArray();
            var expectedAdd = WindowsFirewallRule.AddArguments(program, [MobileDirect.DefaultPort]) ?? throw new InvalidOperationException("the fixed program path was refused");
            Require(adds.Length == 1 && adds[0].UseShellExecute && adds[0].FileName == system + @"\netsh.exe" && adds[0].Arguments == WindowsFirewallRule.CommandLine(expectedAdd)
                && firewallState == FirewallRuleState.Present, "add: " + string.Join(" | ", adds.Select(info => info.FileName + " " + info.Arguments)));
            checks["addCommand"] = true;
            await ChangeFirewallRule(add: false);
            var removes = launched.Where(info => info.Verb == WindowsFirewallRule.ElevationVerb).Skip(1).ToArray();
            Require(removes.Length == 1 && removes[0].Arguments == WindowsFirewallRule.CommandLine(WindowsFirewallRule.DeleteArguments()) && firewallState == FirewallRuleState.Absent,
                "remove: " + string.Join(" | ", removes.Select(info => info.FileName + " " + info.Arguments)));
            checks["removeCommand"] = true;
            checks["passed"] = true;
        }
        finally
        {
            firewallRunner = heldRunner; mobileDirectStatusOverride = null; firewallState = FirewallRuleState.Unknown; firewallMessage = null;
            await service.UpdateAsync(s => s with { MobileRemote = heldRemote });
        }
        return checks;
    }
}
```

3o. In `MainWindow.Smoke.cs`, right after `Checkpoint("setupTools", "passed");`, add:

```csharp
            Checkpoint("mobileDirect", "running");
            result["mobileDirect"] = await RunMobileDirectSmoke();
            Checkpoint("mobileDirect", "passed");
```

3p. In `scripts/test-native-windows.ps1`, right after the closing `}` of the `foreach ($key in @('firstRunDecision', … 'passed'))` block for `setupTools`, add:

```powershell
    # The direct path's rows in Mobile remote over fixed endpoints and a recording firewall runner (MainWindow.MobileDirectSmoke.cs): no netsh runs.
    foreach ($key in @('rows', 'status', 'firewallRow', 'addCommand', 'removeCommand', 'passed')) {
        if ($result.mobileDirect.$key -ne $true) {
            $msg = "Windows mobile direct smoke failed: mobileDirect.$key"
            Write-SmokeAnnotation $msg; throw $msg
        }
    }
```

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-core-checks.sh 'mobile direct|windows firewall'`
Expected: `PASS mobile direct: the settings rows and the smoke are wired`, every earlier new line still PASS, and `FAIL: 9`.

Run: `bash scripts/check-winui-compile.sh`
Expected: success. The only expected error, `Program.cs … InitializeComponent`, is reported as expected.

Run: `node scripts/check-design-tokens.js --check && node scripts/check-locales.js --check && node scripts/check-locales.js --check --touched-since 13c855e3`
Expected: exit 0 each. There are no new font or spacing literals and no Korean literals in non-smoke `.cs` files.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add native/windows/MightyClaude.WinUI/MainWindow.MobileDirect.cs native/windows/MightyClaude.WinUI/MainWindow.MobileDirectSmoke.cs native/windows/MightyClaude.WinUI/MainWindow.MobileRemote.cs native/windows/MightyClaude.WinUI/MainWindow.Smoke.cs scripts/test-native-windows.ps1 native/windows/MightyClaude.Core.Tests/MobileDirectVerification.cs native/windows/MightyClaude.Core.Tests/Verification.cs
git commit -m "(windows) 모바일 리모트 has the direct switch, its port, what it found and the firewall rule's row, whose button (and turning the path off) is the one place the app asks Windows to elevate; the GUI smoke reads the rows over fixed endpoints with no netsh run" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Docs and help

**Files:**
- Modify: `docs/relay.md`, `docs/mobile-remote.md`, `docs/windows-mobile-remote.md`, `docs/windows-parity.md`
- Modify: `docs/help/content/{ko,en,zh,ja}/phone.md`, `docs/help/content/{ko,en,zh,ja}/windows.md`

**Interfaces:**
- Consumes the locale keys `mobileRemote.direct.windows.firewall.addButton` (Task 9), `mobileRemote.direct.toggle` and `settings.nav.mobile`.
- Produces documentation only.

- [ ] **Step 1: Write the failing check**

```bash
cd /Users/young/Work/MightyClaude
cat > /private/tmp/mighty-direct-docs-check.sh <<'SH'
#!/bin/bash
cd /Users/young/Work/MightyClaude || exit 2
fail() { echo "MISSING: $1"; exit 1; }
grep -q "^\*\*Windows 리스너\*\*" docs/relay.md || fail "relay.md Windows listener"
grep -q "지금은 Mac 호스트만" docs/relay.md docs/mobile-remote.md && fail "a Mac-only sentence is left"
grep -q "(지금은 Mac만)" docs/mobile-remote.md && fail "mobile-remote.md capability note"
grep -q "^- \*\*Windows\*\*:" docs/mobile-remote.md || fail "mobile-remote.md Windows bullet"
grep -q "^## Direct connection over Tailscale" docs/windows-mobile-remote.md || fail "windows-mobile-remote.md section"
grep -q "^| 휴대폰 Tailscale 직접 연결 |" docs/windows-parity.md || fail "windows-parity.md row"
for l in ko en zh ja; do
  grep -q "mobileRemote.direct.windows.firewall.addButton" "docs/help/content/$l/phone.md" || fail "$l/phone.md"
  grep -q "(phone.md#direct)" "docs/help/content/$l/windows.md" || fail "$l/windows.md"
done
echo DOCS-OK
SH
bash /private/tmp/mighty-direct-docs-check.sh
```

- [ ] **Step 2: Run it and expect it to fail**

Expected output: `MISSING: relay.md Windows listener`, exit 1.

- [ ] **Step 3: Write the docs**

3a. `docs/relay.md`:

- In the opening paragraph, replace `휴대폰은 같은 데이터 소켓을 Mac에 바로 연다` with `휴대폰은 같은 데이터 소켓을 호스트(Mac·Windows PC)에 바로 연다`.
- Replace the table row `| 호스트(데몬) | Mac 앱의 `MobileRemoteService` (직접 연결 리스너 `MobileDirectListener` 포함) | `native/macos/Sources/MightyCore/Remote/` |` with:

  ```markdown
  | 호스트(데몬) | Mac 앱의 `MobileRemoteService`, Windows 앱의 `MobileRelayHost` (둘 다 직접 연결 리스너 `MobileDirectListener` 포함) | `native/macos/Sources/MightyCore/Remote/`, `native/windows/MightyClaude.Core/Remote/` |
  ```

- In the "## Tailscale 직접 연결" intro, replace `지금은 Mac 호스트만 연다(Windows는 나중).` with `Mac과 Windows 호스트가 연다.`
- Insert this block right before `**끝점 알리기**`:

  ```markdown
  **Windows 리스너** (`MightyClaude.Core/Remote/MobileDirect.cs`·`MobileDirectListener.cs`·`MobileRelayHost.cs`)

  - 켜지는 조건(모바일 리모트와 "Tailscale로 직접 연결" 둘 다), 포트(기본 43139, 1024–65535), 받지 않는 연결(두 범위 밖의 상대, 함께 36개, 휴대폰 표 32대, 핸드셰이크 4개·같은 주소 2개, 60초 안에 인증 5번 실패), 일반 거절 `unauthorized`, 10초 핸드셰이크 제한, `direct-` id, 릴레이가 끊기거나 다시 열려도 직접 연결이 그대로인 것은 Mac과 같다.
  - **무엇이 Tailscale인가**: `NetworkInterface.GetAllNetworkInterfaces()`의 켜진 어댑터 가운데 **그 어댑터 자신이 fd7a:115c:a1e0::/48 주소를 가진 것**만 Tailscale로 보고, 그 어댑터의 두 범위 주소만 쓴다(`TailscaleAddresses.Select`). Mac과 달리 어댑터 이름은 보지 않는다: Windows에서는 사용자가 어댑터 이름을 바꿀 수 있고 Wintun 어댑터의 종류는 정해진 `NetworkInterfaceType` 값이 아니다. 두 앱이 같은 사례 파일 `native/contracts/fixtures/tailscale-addresses.json`을 읽고, 플랫폼마다 답(`darwin`·`win32`)이 다른 사례는 이 차이에서 오는 두 가지뿐이다.
  - 주소마다 `TcpListener` 하나를 그 주소에만 묶는다(와일드카드 없음, `ExclusiveAddressUse`). 특정 IP에 묶는 `HttpListener`는 관리자 권한의 URL ACL이 필요해 쓰지 않는다. Mac의 `requiredInterface` 고정은 없고, 주소에 묶는 것과 Windows의 강한 호스트 모델로 대신한다.
  - WebSocket 업그레이드는 앱이 직접 읽는다: 요청 머리 8 KiB까지, `GET /ws`(질의는 보지 않음)·`HTTP/1.1`·`Upgrade: websocket`·`Connection: Upgrade`·`Sec-WebSocket-Version: 13`·16바이트 `Sec-WebSocket-Key`가 아니면 답 없이 닫는다. 업그레이드도 10초 핸드셰이크 제한 안이다. 그 뒤는 `WebSocket.CreateFromStream`(서버, 프레임 1 MiB)이고, 릴레이 데이터 소켓과 같은 `Connection`이 핸드셰이크·인증·m1 터널을 맡는다.
  - 주소는 Windows의 주소 변경 알림(`NetworkChange.NetworkAddressChanged`)과 15초마다 다시 읽는다. MagicDNS 이름은 싣지 않는다(휴대폰은 이름을 열지 않는다).
  - **Windows 방화벽**: 설정의 방화벽 규칙 줄에서 사용자가 누를 때만, 앱이 규칙 하나를 더하거나 지운다. 명령은 상수와 앱 자신의 경로로만 만든 `%SystemRoot%\System32\netsh.exe advfirewall firewall add rule name="Mighty Claude (Tailscale)" dir=in action=allow protocol=TCP localport=<포트> remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 program="<앱 exe>" profile=any`(지울 때는 `delete rule name="Mighty Claude (Tailscale)"`)이고 Windows의 UAC 확인을 거친다. 규칙이 있는지는 관리자 권한 없이 `show rule`의 종료 코드로 읽는다. 원격이나 직접 연결을 끄는 클릭은, 그 규칙을 쓰는 기능이 남지 않으면 규칙도 지운다(다시 UAC). 앱이 Windows에 관리자 권한을 요청하는 곳은 여기 하나다.
  ```

3b. `docs/mobile-remote.md`:

- Replace `지금은 Mac 호스트만 직접 연결을 연다.` with `Mac과 Windows 호스트가 직접 연결을 연다.`
- Replace `(지금은 Mac만)` with `(Mac·Windows)`.
- Insert right after the bullet that starts `- **Mac**: **설정 → 모바일 리모트**의 "Tailscale로 직접 연결" 스위치`:

  ```markdown
  - **Windows**: 같은 스위치(기본 켜짐)·포트(기본 `43139`)·상태 줄에 **방화벽 규칙** 줄이 더 있다. Tailscale 어댑터는 fd7a:115c:a1e0::/48 주소를 가진 어댑터이고(이름은 보지 않는다), 그 어댑터의 두 범위 주소에만 주소마다 하나씩 `TcpListener`로 연다. 한도·거절·끝점 알리기는 Mac과 같고, MagicDNS 이름은 싣지 않는다. Windows 방화벽은 "규칙 추가"를 누를 때 UAC 확인을 거쳐 규칙 하나(이 앱, 이 포트, TCP, Tailscale 범위에서만)를 더하고, "규칙 지우기"나 원격·직접 연결을 끄는 클릭이 지운다. 자세한 규칙은 [relay.md](relay.md)의 "Windows 리스너"와 [windows-mobile-remote.md](windows-mobile-remote.md).
  ```

- Insert right after the bullet that starts `- `MightyCore/Remote/MobileDirect.swift`: Tailscale 직접 연결`:

  ```markdown
  - Windows: `MightyClaude.Core/Remote/TailscaleAddresses.cs`(같은 규칙, 어댑터 이름 없이), `MobileDirect.cs`(한도·끝점·업그레이드·입장·상태 줄), `MobileDirectListener.cs`(주소마다 `TcpListener`, 함께 36개), `MobileRelayHost.cs`(같은 `Connection`, `direct-` id, 일반 거절, 재확인), `MightyClaude.Core/WindowsFirewallRule.cs`(방화벽 규칙 하나, UAC), WinUI `MainWindow.MobileRemote.cs`·`MainWindow.MobileDirect.cs`.
  ```

3c. `docs/windows-mobile-remote.md`: insert right before `## Screen view and control`:

```markdown
## Direct connection over Tailscale

When the phone and this PC are on the same Tailscale network, the phone opens its data socket straight to the PC (`ws://<address>:43139/ws?v=1`) instead of the relay; the handshake, encryption, auth and `/m1` tunnel are the relay path's, unchanged (`docs/relay.md`, "Tailscale 직접 연결" and "Windows 리스너"). Settings → Mobile remote has the switch (on by default), the port (43139, 1024–65535), a status line (the endpoints listened on, "Tailscale not found", or why binding failed) and the Windows Firewall rule's row.

- What counts as Tailscale: an up adapter that carries an fd7a:115c:a1e0::/48 address; its 100.64.0.0/10 and fd7a addresses are used. The adapter's name is not part of the rule (Windows lets the user rename it). The cases are `native/contracts/fixtures/tailscale-addresses.json`, shared with the Mac. The addresses are read again on every address change Windows reports and every 15 s.
- One `TcpListener` per address, never a wildcard; the app reads the WebSocket upgrade itself, so no administrator's URL ACL is needed.
- Limits as on the Mac: peers outside the two ranges reset at accept, 36 connections, 4 handshakes and 2 per address, a 10 s handshake, 5 failed auths per address per 60 s, the one-word `unauthorized`, `direct-` ids; relay redials leave direct phones alone.
- Advertising: the `"direct"` capability, `auth_ok.direct`, `/m1/info` `direct` and the QR's `direct=`. No MagicDNS name.
- Firewall: the one place the app asks Windows to elevate. **Add rule** runs `%SystemRoot%\System32\netsh.exe advfirewall firewall add rule name="Mighty Claude (Tailscale)" dir=in action=allow protocol=TCP localport=<port> remoteip=100.64.0.0/10,fd7a:115c:a1e0::/48 program="<this exe>" profile=any` through UAC; **Remove rule**, or turning the remote or the direct path off while the rule is there, runs `delete rule name="Mighty Claude (Tailscale)"`. Whether the rule exists is read without elevation from `show rule`'s exit code. A changed port needs the rule removed and added again.
- Still to check on a real Windows PC: the UAC prompt and the rule, and a phone connecting directly.
```

3d. `docs/windows-parity.md`: insert right after the row that starts `| 휴대폰 호스트 맞춤 (세션 응답·리비전·파일·릴레이 연결) |`:

```markdown
| 휴대폰 Tailscale 직접 연결 | `MightyCore/Remote/MobileDirect.swift`·`TailscaleAddresses.swift`·`MobileRemoteService.swift`(Direct path), `MobileRemoteSettingsView.swift`; 공유 사례 `native/contracts/fixtures/tailscale-addresses.json` | Core: `Remote/TailscaleAddresses.cs`(fd7a:115c:a1e0::/48를 가진 어댑터만, 이름은 보지 않음), `Remote/MobileDirect.cs`(한도·끝점·업그레이드·입장·상태 줄), `Remote/MobileDirectListener.cs`(주소마다 `TcpListener`, 함께 36개), `Remote/MobileRelayHost.cs`(같은 `Connection`, `direct-` id, 일반 `unauthorized`, 주소별 인증 실패 5회/60초, 릴레이 재연결과 분리, 주소 변경·15초 재확인), `Remote/MobileDesktopRouter.cs`(`direct` 기능·`/m1/info` `direct`), `Remote/MobileIdentity.cs`(QR `direct=`), `WindowsFirewallRule.cs`(netsh 규칙 하나, UAC); WinUI `MainWindow.MobileRemote.cs`·`MainWindow.MobileDirect.cs` — 스위치·포트·상태 줄·방화벽 규칙 줄; 스모크 키 `mobileDirect` | 2단계 | docs/superpowers/specs/2026-10-17-windows-catch-up-design.md 2절 | Core 검사 `tailscale addresses …`·`mobile direct …`·`windows firewall …`; UAC 확인·방화벽 규칙·휴대폰 직접 연결은 Windows 기기 미확인 |
```

3e. Help `phone.md`: in each language, insert a paragraph right after the line that ends with `{{ui:phone.direct.route.relay}}가 표시됩니다.` (ko), `{{ui:phone.direct.route.relay}}.` (en), `{{ui:phone.direct.route.relay}}。` (zh) or `{{ui:phone.direct.route.relay}} と表示されます。` (ja). Leave a blank line before it.

- `ko`: `Windows PC에서도 같습니다. 다만 Windows 방화벽이 연결을 막지 않도록 규칙이 하나 필요합니다. {{ui:settings.nav.mobile}} 탭에서 {{ui:mobileRemote.direct.windows.firewall.addButton}}을 누르고 Windows의 관리자 확인을 허용하세요. 이 규칙은 Tailscale 주소에서 이 앱으로 오는 연결만 허용하고, 직접 연결을 끄면 함께 지웁니다(이때도 관리자 확인을 묻습니다).`
- `en`: `It works the same with a Windows PC, which needs one Windows Firewall rule so the connection isn't blocked. In the {{ui:settings.nav.mobile}} tab, click {{ui:mobileRemote.direct.windows.firewall.addButton}} and allow Windows' administrator prompt. The rule lets only connections from Tailscale addresses reach this app, and it is removed when you turn the direct connection off (Windows asks again).`
- `zh`: `Windows 电脑也一样，只是需要一条 Windows 防火墙规则，以免连接被拦截。在 {{ui:settings.nav.mobile}} 标签页中点击 {{ui:mobileRemote.direct.windows.firewall.addButton}}，并允许 Windows 的管理员确认。该规则只允许来自 Tailscale 地址的连接到达本应用；关闭直接连接时会一并删除（Windows 会再次请求确认）。`
- `ja`: `Windows PC でも同じです。ただし接続が遮断されないよう、Windows ファイアウォールの規則が 1 つ必要です。{{ui:settings.nav.mobile}} タブで {{ui:mobileRemote.direct.windows.firewall.addButton}} を押し、Windows の管理者の確認を許可してください。この規則は Tailscale のアドレスからこのアプリへの接続だけを許可し、直接接続をオフにすると一緒に削除されます(このときも確認を求められます)。`

3f. Help `windows.md`: in each language, add a row to the `{#differences}` table, right after the text-size-and-font row (the row that contains `{{ui:settings.text.sectionTitle}}`).

- `ko`: `| Tailscale로 직접 연결 | 휴대폰이 Windows PC에도 [직접 연결](phone.md#direct)합니다. Windows 방화벽 규칙은 {{ui:settings.nav.mobile}} 탭의 {{ui:mobileRemote.direct.windows.firewall.addButton}}으로 더하고, Windows의 관리자 확인을 한 번 거칩니다. 앱이 관리자 권한을 묻는 곳은 여기뿐입니다. |`
- `en`: `| Direct connection over Tailscale | Phones [connect directly](phone.md#direct) to a Windows PC too. The Windows Firewall rule is added with {{ui:mobileRemote.direct.windows.firewall.addButton}} in the {{ui:settings.nav.mobile}} tab, after one administrator prompt from Windows. This is the only place the app asks for administrator rights. |`
- `zh`: `| 通过 Tailscale 直接连接 | 手机也能[直接连接](phone.md#direct)到 Windows 电脑。在 {{ui:settings.nav.mobile}} 标签页中用 {{ui:mobileRemote.direct.windows.firewall.addButton}} 添加 Windows 防火墙规则，Windows 会请求一次管理员确认。这是本应用唯一请求管理员权限的地方。 |`
- `ja`: `| Tailscale で直接接続 | スマートフォンは Windows PC にも[直接接続](phone.md#direct)します。Windows ファイアウォールの規則は {{ui:settings.nav.mobile}} タブの {{ui:mobileRemote.direct.windows.firewall.addButton}} で追加し、Windows の管理者の確認を 1 回経ます。アプリが管理者権限を求めるのはここだけです。 |`

- [ ] **Step 4: Run it and expect it to pass**

Run: `bash /private/tmp/mighty-direct-docs-check.sh && node scripts/help/build.mjs --check && node scripts/check-locales.js --check`
Expected: `DOCS-OK`, then the help check and the locale check both exit 0. Every `{{ui:…}}` key exists, and `phone.md#direct` resolves.

- [ ] **Step 5: Commit**

```bash
cd /Users/young/Work/MightyClaude
git add docs/relay.md docs/mobile-remote.md docs/windows-mobile-remote.md docs/windows-parity.md docs/help/content/ko/phone.md docs/help/content/en/phone.md docs/help/content/zh/phone.md docs/help/content/ja/phone.md docs/help/content/ko/windows.md docs/help/content/en/windows.md docs/help/content/zh/windows.md docs/help/content/ja/windows.md
git commit -m "(windows) the docs and help describe the phone's direct path to a Windows PC: the same rule and limits, a TcpListener per Tailscale address, and the one firewall rule added through UAC" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Final verification (after Task 11, nothing to commit)

Run each command and keep its output as evidence:

1. Run `bash /private/tmp/mighty-core-checks.sh 'tailscale addresses|mobile direct|windows firewall'`.
   - Expected: the 11 new lines PASS (2 `tailscale addresses`, 7 `mobile direct`, 2 `windows firewall`), and `FAIL: 9`.
   - Then `git status --short native/windows/MightyClaude.Core.Tests/Verification.cs` must be empty.
2. Run `bash scripts/check-winui-compile.sh`. Expected: success.
3. Run `(cd relay && npm run build) && bash scripts/test-native-macos.sh --filter "RemoteWorkspace|MobileRemote|MobileDirect|Relay"`.
   - Expected: all pass, `RemoteWorkspaceAddressFixtureTests` included.
4. Run `(cd mobile && npx jest)`. Expected: all pass. The phone's locale copies changed; its code did not.
5. Run `node scripts/check-locales.js --check && node scripts/check-locales.js --check --touched-since 13c855e3`. Expected: exit 0 each.
6. Run `node scripts/check-design-tokens.js --check`. Expected: exit 0, with no rise in the font, spacing or token counts.
7. Run `bash scripts/check-style-freeze.sh`. Expected: exit 0 (no style file touched).
8. Run `node scripts/help/build.mjs --check`. Expected: exit 0.
9. Run `git diff 13c855e3 --stat`. It must list only the files in the File Structure table.
10. Confirm that no `netsh`, `tailscale` or app process ran: none of the commands above starts one.

**Still to check on real Windows PCs (not here):**
- the UAC prompt and the rule in `wf.msc`;
- a phone pairing and connecting directly to a Windows host, with the host card showing "Tailscale 직접";
- the relay fallback when the rule is absent.

---

## Self-review against the spec

**Spec section 2 coverage**

| Spec item | Where |
|---|---|
| Address rule: an interface counts only with an fd7a:115c:a1e0::/48 address, and both ranges' addresses are used | Task 2 `TailscaleAddresses.Owned` |
| Interfaces come from `NetworkInterface.GetAllNetworkInterfaces()` | Task 2 `InterfaceAddresses` |
| Cases in a shared `native/contracts/fixtures/tailscale-addresses.json`, read by both apps | Task 1 (fixture and Mac test), Task 2 (Windows check) |
| Fixture covers WARP-like 100.96/12 rejected, hotspot CGNAT on Wi-Fi rejected, fd7a on a non-tunnel interface, range edges, `%scope`, loopback and LAN rejected | Task 1 cases 2, 3, 5, 7, 8/9 and 9/10, plus dedupe/sort and WARP beside Tailscale |
| The platform-neutral decision is explained | File Structure, "Fixture decision" |
| Listener on each Tailscale address, port 43139 | Task 4 listener, Task 5 `RefreshDirect`/`BindDirect` |
| Speaks the relay's phone-facing protocol with the same session and auth | Task 5: the same `Connection` refactored to `WebSocket` |
| `HttpListener` URL ACL avoided | Task 4: `TcpListener` and `WebSocket.CreateFromStream` |
| Generic `unauthorized` | Task 5 (`Connection` refusal), checked in Tasks 5 and 6 |
| 5 failed auths per IP per 60 s | Task 3 `DirectAuthFailures`/`Admit`, Task 5 recording, Task 6 integration |
| 4 handshakes, 2 per IP, 36 connections | Task 3 `Admit`, Task 4 listener cap, Task 6 integration of the per-IP cap |
| 1 MiB frames | existing `Receive` limit, now applied to direct sockets |
| 10 s handshake deadline, upgrade included | Task 5 (`Connection` deadline wraps `open`), Task 6 (`HandshakeDeadline`, checked) |
| Peers outside the ranges dropped | Task 3 `Admit` (checked), Task 5 `AcceptDirect` |
| Ids start with `direct-` | Tasks 3 and 5 |
| `auth_ok.direct`, `/m1/info` `direct`, the `"direct"` capability; no phone change | Task 5 (`auth_ok`), Task 7 (router, capability, QR) |
| Settings: direct switch (default on) and port field | Task 5 (record), Task 10 (UI) |
| Status line | Task 3 (text), Task 10 (UI) |
| Firewall rule control | Tasks 8 and 10 |
| Re-detection on network change | Task 6 (`NetworkAddressChanged` and the 15 s watch) |
| Internal-only loopback seam | Task 5 (`InternalsVisibleTo` and `internal` members) |

**Spec firewall decision coverage**

| Spec item | Where |
|---|---|
| A narrow rule: TCP on the app's ports, from 100.64.0.0/10 and fd7a:115c:a1e0::/48, for the app's exe | Task 8 argv |
| One UAC prompt when adding | Task 8 `runas` |
| Turning off removes the rule with a UAC prompt | Task 10 `RemoveFirewallRuleIfUnneeded` |
| The fixed command from constants and the app's own path | Task 8, with validation; Task 10 source check that only `WindowsFirewallRule.cs` names `runas` |
| Part 3 reuse through a port-set API | Task 8 `AddArguments(program, ports)`, `Arguments(ports, program)`, `ApplyAsync(ports)` |

**Verification coverage.** The spec's list is the Final verification above:
- Core with the wrapper and 9 expected FAILs;
- `check-winui-compile`;
- the smoke step registered in `test-native-windows.ps1`;
- the Mac suites reading the fixture;
- `check-locales` in both modes, design tokens, style freeze, `help --check`;
- phone jest, since the locales changed.

**Placeholder scan.** No TBD/TODO. Every code step shows the code, every run step shows the command and the expected output, and no step refers to another task for code.

**Type consistency, checked across tasks**
- `MobileDirectStatus(State, Endpoints, Error)` and `.Listening`;
- `MobileDirectStatusText.Detail`;
- `MobileDirectListener.Update(...) → (Bound, Error)`;
- `DirectIncoming.Peer`/`Stream`/`Refuse`/`Dispose`;
- `MobileRelayHost.SetDirectAddressSource`/`RefreshDirect`/`DirectStatus`/`DirectEndpoints`/`ConnectionIds`/`HandshakeDeadline`/`DirectRedetectInterval`;
- `MobileDesktopRouter.DirectEndpoints`;
- `MobileIdentity.PairingUrl(relay, name, direct)`;
- `WindowsFirewallRule.ElevationVerb`/`AddArguments`/`DeleteArguments`/`CommandLine`;
- `WindowsFirewallRuleRunner(run, systemDirectory, program)`;
- `FirewallChangeResult(Kind, Detail)`;
- `MobileDirectStrings.FirewallState`/`FirewallButton`/`FirewallOutcome`/`Keys`.

**Deliberate differences from the Mac, each documented**
- The Windows rule ignores interface names.
- There is no interface pinning; the strong host model replaces it.
- There is no MagicDNS name, since phones never dial names.
- The Windows HTTP upgrade requires the `/ws` path; the Mac's stack does not read the path.

**Part 3 note.** `docs/superpowers/plans/2026-10-17-windows-remote-workspaces.md` expects a static `WindowsFirewallRule.ApplyAsync(ports, exe) → Task<bool>` and `WindowsFirewallRule.Arguments(ports, exe)`. This plan lands:
- `WindowsFirewallRule.Arguments(IReadOnlyCollection<int> ports, string program)`, the same shape but returning `IReadOnlyList<string>?`;
- the instance `WindowsFirewallRuleRunner.ApplyAsync(IReadOnlyCollection<int> ports, CancellationToken) → Task<FirewallChangeResult>`. The runner is an instance so tests and the smoke can inject a recorder.

That plan's "reconcile the consumed names" step covers both mappings. `FirewallPorts()` in WinUI is the single place part 3 adds 43137.
