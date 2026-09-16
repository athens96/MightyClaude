import Foundation

/// CLI settings state for this working directory, not proof that an already
/// running Claude process has loaded the plugin successfully.
public struct ClaudeInstalledPlugin: Sendable, Equatable, Identifiable {
    public var pluginID: String
    public var name: String
    public var marketplace: String?
    public var version: String?
    public var scope: String
    public var enabled: Bool?
    public var projectPath: String?
    public var description: String
    public var errors: [String]
    public var notes: [String]
    public var id: String { [pluginID, scope, projectPath ?? ""].map { "\($0.utf8.count):\($0)" }.joined(separator: "|") }

    public init(pluginID: String, name: String, marketplace: String? = nil, version: String? = nil,
                scope: String = "user", enabled: Bool? = nil, projectPath: String? = nil,
                description: String = "", errors: [String] = [], notes: [String] = []) {
        self.pluginID = pluginID; self.name = name; self.marketplace = marketplace; self.version = version
        self.scope = scope; self.enabled = enabled; self.projectPath = projectPath
        self.description = description; self.errors = errors; self.notes = notes
    }
}

public struct ClaudeCatalogPlugin: Sendable, Equatable, Identifiable {
    public var id: String
    public var name: String
    public var description: String
    public var marketplace: String
    public var version: String?
    public var sourceKind: String
    public init(id: String, name: String, description: String = "", marketplace: String, version: String? = nil, sourceKind: String = "unknown") {
        self.id = id; self.name = name; self.description = description; self.marketplace = marketplace
        self.version = version; self.sourceKind = sourceKind
    }
}

public struct ClaudePluginMarketplace: Sendable, Equatable, Identifiable {
    public var name: String
    public var sourceKind: String
    public var id: String { name }
    public init(name: String, sourceKind: String = "unknown") { self.name = name; self.sourceKind = sourceKind }
}

public struct ClaudePluginSnapshot: Sendable, Equatable {
    /// ready, missing, unsupported, failed, busy, cancelled, or remote.
    public var status: String
    public var detail: String
    public var cliVersion: String?
    public var installed: [ClaudeInstalledPlugin]
    public var available: [ClaudeCatalogPlugin]
    public var marketplaces: [ClaudePluginMarketplace]
    public var updatedAt: String?
    /// Bounded CLI output. Only show after an explicit diagnostics action.
    public var diagnosticOutput: String
    public init(status: String, detail: String, cliVersion: String? = nil, installed: [ClaudeInstalledPlugin] = [],
                available: [ClaudeCatalogPlugin] = [], marketplaces: [ClaudePluginMarketplace] = [], updatedAt: String? = nil, diagnosticOutput: String = "") {
        self.status = status; self.detail = detail; self.cliVersion = cliVersion; self.installed = installed
        self.available = available; self.marketplaces = marketplaces; self.updatedAt = updatedAt; self.diagnosticOutput = diagnosticOutput
    }
}

public struct ClaudePluginOperationResult: Sendable, Equatable {
    /// succeeded, skipped, failed, busy, cancelled, or remote.
    public var status: String
    public var detail: String
    /// Bounded CLI output. Only show after an explicit diagnostics action.
    public var output: String
    public init(status: String, detail: String, output: String = "") { self.status = status; self.detail = detail; self.output = output }
}
