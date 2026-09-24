import Foundation

/// Scans the installed oh-my-claudecode plugin for its agents and their frontmatter models.
///
/// Returns nil when omc is not installed (no user-scope record whose installPath
/// contains agents/*.md).  Returns a [camelCaseKey: frontmatterModel] map otherwise.
public struct OmcAgentCatalog: Sendable {
    public let homeDirectory: URL

    public init(homeDirectory: URL = URL(fileURLWithPath: NSHomeDirectory())) {
        self.homeDirectory = homeDirectory
    }

    /// Scans installed_plugins.json for an oh-my-claudecode@ plugin, reads its
    /// agents/*.md frontmatter, and returns [camelCaseKey → frontmatterModel].
    /// Returns nil when the plugin is absent or has no user-scope record with agents.
    public func scan() -> [String: String]? {
        let pluginsJSON = homeDirectory
            .appendingPathComponent(".claude/plugins/installed_plugins.json")
        guard let data = try? Data(contentsOf: pluginsJSON),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let plugins = obj["plugins"] as? [String: Any] else { return nil }

        // Find the first key starting with "oh-my-claudecode@"
        guard let (_, recordsAny) = plugins.first(where: { $0.key.hasPrefix("oh-my-claudecode@") }),
              let records = recordsAny as? [[String: Any]] else { return nil }

        // Find a user-scope record whose installPath contains agents/*.md
        for record in records {
            guard record["scope"] as? String == "user",
                  let installPath = record["installPath"] as? String else { continue }
            let agentsDir = URL(fileURLWithPath: installPath).appendingPathComponent("agents")
            guard let files = try? FileManager.default.contentsOfDirectory(
                at: agentsDir, includingPropertiesForKeys: nil) else { continue }
            let mdFiles = files.filter { $0.pathExtension == "md" }
            guard !mdFiles.isEmpty else { continue }

            var result: [String: String] = [:]
            for file in mdFiles {
                let baseName = file.deletingPathExtension().lastPathComponent
                let key = Self.kebabToCamelCase(baseName)
                result[key] = Self.readFrontmatterModel(file) ?? "default"
            }
            return result
        }
        return nil
    }

    // MARK: - Helpers

    /// Converts a kebab-case file base-name to lowerCamelCase.
    /// Single-word names are returned unchanged.
    public static func kebabToCamelCase(_ s: String) -> String {
        let parts = s.components(separatedBy: "-")
        guard parts.count > 1 else { return s }
        return parts[0] + parts.dropFirst().map { part in
            guard let first = part.first else { return part }
            return first.uppercased() + part.dropFirst()
        }.joined()
    }

    /// Reads the `model:` scalar from YAML frontmatter delimited by `---`.
    static func readFrontmatterModel(_ url: URL) -> String? {
        guard let content = try? String(contentsOf: url, encoding: .utf8) else { return nil }
        var inFrontmatter = false
        for line in content.components(separatedBy: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed == "---" {
                if !inFrontmatter { inFrontmatter = true; continue }
                else { break }
            }
            if inFrontmatter, trimmed.hasPrefix("model:") {
                return String(trimmed.dropFirst("model:".count)).trimmingCharacters(in: .whitespaces)
            }
        }
        return nil
    }
}
