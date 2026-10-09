import Foundation

/// Carry-on for the per-pane stdio MCP server attached to one Claude or Codex run.
///
/// Generate one binding immediately before spawning the CLI and discard it when
/// the pane closes or the app quits. The token is random and held only in memory.
/// It reaches the MCP server through the CLI process environment
/// (``environment``), never through argv or a config file.
/// **Never log `token`.** `description` and `debugDescription` are deliberately
/// token-free so that interpolating a binding into a log line cannot leak it;
/// use ``redactingToken(in:)`` for any other dump.
public struct PaneMCPBinding: Sendable, Equatable, CustomStringConvertible, CustomDebugStringConvertible {
    public static let serverName = "mighty-terminal"
    public static let tokenEnvironmentKey = "MIGHTY_PANE_TOKEN"
    public static let socketEnvironmentKey = "MIGHTY_AGENT_IO_SOCKET"

    /// Placeholder substituted for the token whenever text has to be logged.
    public static let redactedTokenPlaceholder = "<pane-token-redacted>"

    /// The agent pane that owns this binding. Every tool call arriving with
    /// `token` belongs to this pane and to no other.
    public let agentPaneId: String
    /// Random in-memory token. Never written to disk, argv or logs.
    public let token: String
    public let server: PaneMCPServerLocation
    /// Workspace of the run: scope of the remembered web choice and cwd of the terminal pane.
    public let workspaceId: String
    public let workspacePath: String
    public let provider: String
    /// The pane's kind. Bindings are only made for agent-pane runs.
    public let kind: String
    /// Whether this run also gets the delegation MCP server (``DelegationMCPServer``)
    /// on the same token: only a Claude pane's run started while the hidden
    /// ``DelegationSwitch`` was on.
    public let delegation: Bool

    public init(agentPaneId: String, token: String, server: PaneMCPServerLocation, workspaceId: String, workspacePath: String, provider: String, kind: String = SessionKind.claude, delegation: Bool = false) {
        self.agentPaneId = agentPaneId; self.token = token; self.server = server
        self.workspaceId = workspaceId; self.workspacePath = workspacePath; self.provider = provider
        self.kind = kind; self.delegation = delegation
    }

    /// Create a binding for one agent pane with a fresh 256-bit random token.
    public static func generate(agentPaneId: String, server: PaneMCPServerLocation, workspaceId: String, workspacePath: String, provider: String, kind: String = SessionKind.claude, delegation: Bool = false) -> PaneMCPBinding {
        PaneMCPBinding(agentPaneId: agentPaneId, token: randomToken(), server: server, workspaceId: workspaceId, workspacePath: workspacePath, provider: provider, kind: kind, delegation: delegation)
    }

    static func randomToken() -> String {
        var generator = SystemRandomNumberGenerator()
        return (0 ..< 32).map { _ in String(format: "%02x", UInt8.random(in: UInt8.min ... UInt8.max, using: &generator)) }.joined()
    }

    public var socketPath: String { server.socketPath }

    /// Variables set on the spawned CLI process. Claude Code stdio servers inherit
    /// the parent environment; Codex passes them on through `env_vars`.
    public var environment: [String: String] { [Self.tokenEnvironmentKey: token, Self.socketEnvironmentKey: server.socketPath] }

    /// Replace every occurrence of the token so a log line can never carry it.
    public func redactingToken(in text: String) -> String {
        guard !token.isEmpty else { return text }
        return text.replacingOccurrences(of: token, with: PaneMCPBinding.redactedTokenPlaceholder)
    }

    public var description: String {
        "PaneMCPBinding(agentPaneId: \(agentPaneId), server: \(PaneMCPBinding.serverName), socketPath: \(socketPath), token: \(PaneMCPBinding.redactedTokenPlaceholder))"
    }

    public var debugDescription: String { description }

    /// The `--mcp-config` JSON for a Claude run. It names the servers only; the
    /// token and socket path arrive through the inherited environment. The
    /// delegation server is its own entry, so mighty-terminal keeps its four tools.
    func claudeMCPConfigJSON() throws -> String {
        var servers: [String: Any] = [PaneMCPBinding.serverName: ["type": "stdio", "command": server.executable.path, "args": server.arguments]]
        if delegation { servers[DelegationMCPServer.serverName] = ["type": "stdio", "command": server.executable.path, "args": server.delegationArguments] }
        let data = try JSONSerialization.data(withJSONObject: ["mcpServers": servers], options: [.sortedKeys, .withoutEscapingSlashes])
        return String(decoding: data, as: UTF8.self)
    }

    /// The `-c mcp_servers.<name>.*` args for a Codex run. `env_vars` whitelists
    /// the two variables from the Codex process environment for the server.
    ///
    /// `-c developer_instructions=` replaces the user's configured value, so
    /// ours is appended after theirs, and when their value cannot be read the
    /// flag is left out rather than clobbering it.
    func codexMCPArgs(userInstructions: CodexUserInstructions.Setting) -> [String] {
        let name = PaneMCPBinding.serverName
        var args = [
            "-c", "mcp_servers.\(name).command=\(Self.tomlLiteral(server.executable.path))",
            "-c", "mcp_servers.\(name).args=\(Self.tomlLiteral(server.arguments))",
            "-c", "mcp_servers.\(name).env_vars=\(Self.tomlLiteral([Self.tokenEnvironmentKey, Self.socketEnvironmentKey]))"
        ]
        let ours = PaneMCPToolManifest.codexDeveloperInstructions
        switch userInstructions {
        case .absent: args += ["-c", "developer_instructions=\(Self.tomlLiteral(ours))"]
        case .value(let theirs): args += ["-c", "developer_instructions=\(Self.tomlLiteral(theirs + "\n\n" + ours))"]
        case .unreadable: break
        }
        return args
    }

    /// JSON string and array literals are valid TOML basic strings and arrays.
    static func tomlLiteral<T: Encodable>(_ value: T) -> String {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        return (try? encoder.encode(value)).map { String(decoding: $0, as: UTF8.self) } ?? "\"\""
    }
}

/// In-memory table of the live per-pane MCP bindings.
///
/// The app keeps exactly one of these for its lifetime and shares it between
/// the process runners that mint tokens and the socket server that resolves
/// them. It never touches disk: a token exists only while its agent pane is
/// open, is revoked on pane close (``revoke(agentPaneId:)``) and on app quit
/// (``revokeAll()``), and a call that presents a revoked or foreign token
/// resolves to no pane at all.
public final class PaneMCPBindingRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var bindingsByPane: [String: PaneMCPBinding] = [:]

    public init() {}

    /// Mint a fresh binding for `agentPaneId`, replacing (and so revoking) any
    /// earlier token for the same pane. A reopened pane gets a new token.
    @discardableResult
    public func bind(agentPaneId: String, server: PaneMCPServerLocation, workspaceId: String, workspacePath: String, provider: String, kind: String = SessionKind.claude, delegation: Bool = false) -> PaneMCPBinding {
        let binding = PaneMCPBinding.generate(agentPaneId: agentPaneId, server: server, workspaceId: workspaceId, workspacePath: workspacePath, provider: provider, kind: kind, delegation: delegation)
        lock.lock(); bindingsByPane[agentPaneId] = binding; lock.unlock()
        return binding
    }

    /// The live binding for a pane, or nil once it has been revoked.
    public func binding(forPane agentPaneId: String) -> PaneMCPBinding? {
        lock.lock(); defer { lock.unlock() }
        return bindingsByPane[agentPaneId]
    }

    /// Resolve an incoming tool call to the one pane that owns its token.
    /// A revoked or unknown token resolves to nil, so it can reach no pane.
    public func binding(forToken token: String) -> PaneMCPBinding? {
        guard !token.isEmpty else { return nil }
        let presented = Array(token.utf8)
        lock.lock(); defer { lock.unlock() }
        return bindingsByPane.values.first { Self.constantTimeEqual(Array($0.token.utf8), presented) }
    }

    /// The agent pane a tool call is bound to, or nil if the token is not live.
    public func agentPaneId(forToken token: String) -> String? { binding(forToken: token)?.agentPaneId }

    /// Revoke the token of one agent pane. Called when the pane closes.
    public func revoke(agentPaneId: String) {
        lock.lock(); bindingsByPane.removeValue(forKey: agentPaneId); lock.unlock()
    }

    /// Revoke every token. Called when the app quits.
    public func revokeAll() {
        lock.lock(); bindingsByPane.removeAll(); lock.unlock()
    }

    public var activePaneIds: [String] {
        lock.lock(); defer { lock.unlock() }
        return bindingsByPane.keys.sorted()
    }

    private static func constantTimeEqual(_ a: [UInt8], _ b: [UInt8]) -> Bool {
        guard a.count == b.count else { return false }
        var difference: UInt8 = 0
        for index in a.indices { difference |= a[index] ^ b[index] }
        return difference == 0
    }
}

/// How a CLI run launches the per-pane stdio MCP server, and the local socket
/// it talks back to. Fixed for the app's lifetime; the token is what varies per pane.
/// The server is the app binary itself in its headless `--agent-io-mcp` mode, and
/// the delegation server the same binary in its `--agent-delegation-mcp` mode.
public struct PaneMCPServerLocation: Sendable, Equatable {
    public static let headlessArgument = "--agent-io-mcp"

    public let socketPath: String
    public let executable: URL
    public let arguments: [String]
    public let delegationArguments: [String]

    public init(socketPath: String, executable: URL, arguments: [String] = [PaneMCPServerLocation.headlessArgument], delegationArguments: [String] = [DelegationMCPServer.headlessArgument]) {
        self.socketPath = socketPath; self.executable = executable; self.arguments = arguments; self.delegationArguments = delegationArguments
    }
}

/// The four tools the per-pane MCP server exposes to Claude and Codex — no others.
///
/// Descriptions guide the agent to route user-visible and long-running commands
/// through the terminal tool and keep short internal work in its built-in Bash tool.
public struct PaneMCPToolManifest {
    public struct Tool: Sendable, Equatable {
        public let name: String
        public let description: String
        /// The single required string argument and what it means.
        public let argument: String
        public let argumentDescription: String
    }

    /// Server-level instructions sent with `initialize`.
    public static let routingGuidance = "Use run_in_terminal for commands the user should see and for long-running processes such as dev servers, watchers and long builds. Keep short internal work such as grep, file reads and quick build or test checks in your built-in Bash tool. " + interactiveGuidance + " " + webGuidance

    /// Commands that wait for the user must run where the user can answer them.
    public static let interactiveGuidance = "Any command that asks the user something or waits for them to type, pick or approve (sign-ins such as glab auth login, gh auth login, codex login or claude auth login, a browser or SSO approval, a password or passphrase prompt) must be started with run_in_terminal so the user answers it in the terminal pane; then follow it with read_latest_output until it ends. Never drive such a prompt from your own shell session or by sending keystrokes to it."

    /// Web pages open where the user chose for this workspace, not wherever
    /// a shell command happens to send them.
    public static let webGuidance = "When the user asks you to show or open a web page, open it with open_url, which shows it in the app's browser pane or the system browser as the user chose for this workspace; never open web pages with shell commands such as open, xdg-open or osascript."

    /// Codex does not surface MCP server instructions to the model, so a Codex
    /// run carries the same guidance as developer instructions.
    public static var codexDeveloperInstructions: String {
        "This app gives you a \(PaneMCPBinding.serverName) MCP server whose tools reach the user's terminal pane. " + routingGuidance
    }

    public static let runInTerminal = Tool(
        name: "run_in_terminal",
        description: "Run a shell command in the user's dedicated terminal pane, where they can watch it live and type into it. Use this tool for commands the user should see and for long-running processes such as dev servers, watchers and long builds. Keep short internal work such as grep, file reads and quick build or test checks in your built-in Bash tool. Returns the full output and exit code when the command finishes within 12 seconds; otherwise returns the output so far, a running status and a handle for read_latest_output and stop.",
        argument: "command",
        argumentDescription: "The shell command to run in the workspace folder."
    )

    public static let readLatestOutput = Tool(
        name: "read_latest_output",
        description: "Read the output a process started with run_in_terminal produced since your last read, including anything the user typed into the terminal pane (at most 64 KB per call). Says whether the process is still running, its exit code or signal once it ended, when older output was dropped and when more remains. It follows processes started with run_in_terminal, which is for commands the user should see and long-running processes; keep short internal work in your built-in Bash tool.",
        argument: "handle",
        argumentDescription: "The handle returned by run_in_terminal."
    )

    public static let stop = Tool(
        name: "stop",
        description: "Stop a process started with run_in_terminal. Sends Ctrl+C (SIGINT) to its process group, then SIGTERM after 3 seconds and SIGKILL after 5 more, and reports the exit code or signal within 10 seconds. Only processes started with run_in_terminal, which is for commands the user should see and long-running processes; short internal work belongs in your built-in Bash tool.",
        argument: "handle",
        argumentDescription: "The handle returned by run_in_terminal."
    )

    public static let openURL = Tool(
        name: "open_url",
        description: "Open an http or https web page for the user, inside the app's browser pane or in the system browser according to the user's choice for this workspace. Only http and https URLs (localhost included) are accepted; file, javascript, data and other schemes are rejected with an error and nothing opens. For commands, use run_in_terminal when the user should see them or they run long, and keep short internal work in your built-in Bash tool.",
        argument: "url",
        argumentDescription: "The http or https URL to open (at most 8192 characters)."
    )

    /// All four tools in declaration order. Exactly this many; no more, no fewer.
    public static let all: [Tool] = [runInTerminal, readLatestOutput, stop, openURL]
}
