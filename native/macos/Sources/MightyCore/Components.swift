import Foundation

/// One thing the app depends on and can check or install from Settings:
/// each agent CLI and any plugin the app requires for an installed agent
/// (see `ComponentCatalog`).
public struct ComponentStatus: Codable, Sendable, Equatable, Identifiable {
    public var id: String
    public var title: String
    /// installed · missing · attention · checking · unsupported
    public var state: String
    public var version: String?
    public var detail: String
    public var actions: [ComponentAction]
    public init(id: String, title: String, state: String, version: String? = nil, detail: String, actions: [ComponentAction] = []) {
        self.id = id; self.title = title; self.state = state; self.version = version; self.detail = detail; self.actions = actions
    }
}

public struct ComponentAction: Codable, Sendable, Equatable, Identifiable {
    /// install · open-store · launch · login · connect · update · install-plugin · copy-command
    public var id: String
    public var title: String
    public var primary: Bool
    public init(id: String, title: String, primary: Bool = true) { self.id = id; self.title = title; self.primary = primary }
}

/// A CLI marketplace plugin the app needs when that agent is installed.
/// Empty today: the Claude Mod the app relies on is bundled and loaded per
/// run, so nothing has to be installed into the agent. Adding an entry here
/// makes Settings check and install it through the existing plugin services.
public struct RequiredPlugin: Codable, Sendable, Equatable, Identifiable {
    public var provider: String
    public var pluginID: String
    public var title: String
    public var reason: String
    public var id: String { provider + ":" + pluginID }
    public init(provider: String, pluginID: String, title: String, reason: String) {
        self.provider = provider; self.pluginID = pluginID; self.title = title; self.reason = reason
    }
}

public enum ComponentCatalog {
    public static let requiredPlugins: [RequiredPlugin] = []
    /// Copyable install commands for agents that are not installed. The app
    /// never runs these itself: fresh CLI installs stay a deliberate user step.
    public static func installCommand(provider: String) -> String? {
        switch provider {
        case "claude": return "npm install -g @anthropic-ai/claude-code"
        case "codex": return "npm install -g @openai/codex"
        case "gemini": return "npm install -g @google/gemini-cli"
        default: return nil
        }
    }
}
