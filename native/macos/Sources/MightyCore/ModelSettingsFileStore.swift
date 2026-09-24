import Foundation

/// Reads and writes the owned model knobs in the external tool config files.
///
/// - omc: `agents.<camelCaseKey>.model` in `~/.config/claude-omc/config.jsonc`
/// - Ouroboros: dotted model keys in `~/.ouroboros/config.yaml`
///
/// Every save re-reads the file immediately before writing (to capture external
/// changes), writes a timestamped backup next to the original, then writes
/// atomically via a temp-file rename. Comments in JSONC files are stripped on
/// round-trip. If the file cannot be parsed, the save is refused and the bytes
/// are left byte-identical.
///
/// Pass a `homeDirectory` for tests so no real user file is touched.
public struct ModelSettingsFileStore: Sendable {
    public let homeDirectory: URL

    public init(homeDirectory: URL = URL(fileURLWithPath: NSHomeDirectory())) {
        self.homeDirectory = homeDirectory
    }

    // MARK: - File paths

    public var omcConfigURL: URL {
        homeDirectory
            .appendingPathComponent(".config/claude-omc")
            .appendingPathComponent("config.jsonc")
    }

    public var ouroborosConfigURL: URL {
        homeDirectory
            .appendingPathComponent(".ouroboros")
            .appendingPathComponent("config.yaml")
    }

    // MARK: - omc (config.jsonc)

    /// Returns `[camelCaseKey: model]` for every agent that has a `model` key
    /// in config.jsonc, or nil when the file does not exist.
    /// Throws when the file exists but cannot be parsed.
    public func loadOmcAgents() throws -> [String: String]? {
        let url = omcConfigURL
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        let data = try Data(contentsOf: url)
        let root = try Self.parseJSONC(data)
        guard let agents = root["agents"] as? [String: Any] else { return [:] }
        var result: [String: String] = [:]
        for (key, agentAny) in agents {
            if let agentDict = agentAny as? [String: Any],
               let model = agentDict["model"] as? String {
                result[key] = model
            }
        }
        return result
    }

    /// Merges `agents` into config.jsonc, writing only `agents.<key>.model`
    /// entries for the keys the caller provides.
    ///
    /// - Re-reads the file immediately before writing.
    /// - Writes a backup before modifying.
    /// - Writes atomically (temp file + rename).
    /// - Throws (file left byte-identical) when the current file is unparseable.
    public func saveOmcAgents(_ agents: [String: String]) throws {
        let url = omcConfigURL
        let fm = FileManager.default

        var root: [String: Any] = [:]
        if fm.fileExists(atPath: url.path) {
            let existing = try Data(contentsOf: url)
            // Throws on parse error; file is left byte-identical
            root = try Self.parseJSONC(existing)
            try writeBackup(data: existing, to: url)
        }

        var agentsSection = root["agents"] as? [String: Any] ?? [:]
        for (key, model) in agents {
            if model == "default" {
                if var entry = agentsSection[key] as? [String: Any] {
                    entry.removeValue(forKey: "model")
                    if entry.isEmpty {
                        agentsSection.removeValue(forKey: key)
                    } else {
                        agentsSection[key] = entry
                    }
                }
            } else {
                var entry = agentsSection[key] as? [String: Any] ?? [:]
                entry["model"] = model
                agentsSection[key] = entry
            }
        }
        root["agents"] = agentsSection

        try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let out = try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys])
        try atomicWrite(data: out, to: url)
    }

    // MARK: - Ouroboros (config.yaml)

    /// Returns `[dottedKey: model]` for scalar values whose key ends in `_model`,
    /// or nil when the file does not exist.
    /// Throws when the file exists but is not valid UTF-8.
    public func loadOuroborosKeys() throws -> [String: String]? {
        let url = ouroborosConfigURL
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        let data = try Data(contentsOf: url)
        guard let content = String(data: data, encoding: .utf8) else {
            throw MightyError("config.yaml에 UTF-8로 디코딩할 수 없는 바이트가 있습니다.")
        }
        let all = Self.parseYAMLScalars(content)
        return all.filter { $0.key.hasSuffix("_model") }
    }

    /// Rewrites only the lines in config.yaml whose dotted key is in `owned`.
    /// All other lines — including `orchestrator.cli_path` — are preserved verbatim.
    ///
    /// - No-op when the file does not exist.
    /// - Re-reads the file immediately before writing.
    /// - Writes a backup before modifying.
    /// - Writes atomically (temp file + rename).
    /// - Throws (file left byte-identical) when the file is not valid UTF-8.
    public func saveOuroborosKeys(_ owned: [String: String]) throws {
        let url = ouroborosConfigURL
        guard FileManager.default.fileExists(atPath: url.path) else { return }

        let existing = try Data(contentsOf: url)
        guard let content = String(data: existing, encoding: .utf8) else {
            throw MightyError("config.yaml에 UTF-8로 디코딩할 수 없는 바이트가 있습니다.")
        }

        try writeBackup(data: existing, to: url)
        let updated = Self.rewriteYAMLKeys(content, updates: owned)
        try atomicWrite(data: Data(updated.utf8), to: url)
    }

    // MARK: - JSONC helpers

    /// Strips `//` and `/* */` comments then parses as JSON.
    /// Throws `MightyError` when the data is not parseable.
    static func parseJSONC(_ data: Data) throws -> [String: Any] {
        guard let source = String(data: data, encoding: .utf8) else {
            throw MightyError("config.jsonc이 유효한 UTF-8이 아닙니다.")
        }
        let stripped = stripJSONCComments(source)
        guard let strippedData = stripped.data(using: .utf8) else {
            throw MightyError("config.jsonc 주석 제거 후 인코딩 오류가 발생했습니다.")
        }
        do {
            guard let obj = try JSONSerialization.jsonObject(with: strippedData) as? [String: Any] else {
                throw MightyError("config.jsonc의 루트 값이 JSON 객체가 아닙니다.")
            }
            return obj
        } catch let e as MightyError {
            throw e
        } catch {
            throw MightyError("config.jsonc를 파싱할 수 없습니다: \(error.localizedDescription)")
        }
    }

    /// Removes `//` single-line and `/* */` block comments from JSONC source,
    /// leaving strings intact.
    static func stripJSONCComments(_ source: String) -> String {
        var result = ""
        result.reserveCapacity(source.utf8.count)
        var i = source.startIndex
        var inString = false
        var inLine = false
        var inBlock = false

        while i < source.endIndex {
            let c = source[i]
            let j = source.index(after: i)
            let next: Character = j < source.endIndex ? source[j] : "\0"

            if inLine {
                if c == "\n" { inLine = false; result.append(c) }
                // else discard
            } else if inBlock {
                if c == "*" && next == "/" { inBlock = false; i = j }
                // else discard
            } else if inString {
                result.append(c)
                if c == "\\" {
                    // escaped character: copy the next character verbatim
                    i = j
                    if i < source.endIndex { result.append(source[i]) }
                } else if c == "\"" {
                    inString = false
                }
            } else {
                if c == "\"" {
                    inString = true; result.append(c)
                } else if c == "/" && next == "/" {
                    inLine = true; i = j
                } else if c == "/" && next == "*" {
                    inBlock = true; i = j
                } else {
                    result.append(c)
                }
            }

            i = source.index(after: i)
        }
        return result
    }

    // MARK: - YAML helpers

    /// Parses top-level `section:\n  key: value` pairs into dotted keys.
    /// Only 2-level-deep scalars are returned; nested structures and list
    /// items are skipped.
    static func parseYAMLScalars(_ content: String) -> [String: String] {
        var result: [String: String] = [:]
        var section: String? = nil

        for line in content.components(separatedBy: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty || trimmed.hasPrefix("#") { continue }

            if !line.hasPrefix(" ") && !line.hasPrefix("\t") {
                // Top-level line: detect section headers ("key:") vs key-value pairs
                if trimmed.hasSuffix(":") && !trimmed.contains(" ") {
                    section = String(trimmed.dropLast())
                } else {
                    section = nil
                }
            } else if let sec = section {
                // Indented line under a section
                guard !trimmed.hasPrefix("-"),
                      let colonIdx = trimmed.firstIndex(of: ":") else { continue }
                let key = String(trimmed[..<colonIdx]).trimmingCharacters(in: .whitespaces)
                let afterColon = String(trimmed[trimmed.index(after: colonIdx)...])
                    .trimmingCharacters(in: .whitespaces)
                // Skip sub-section headers (empty value) and complex values
                guard !afterColon.isEmpty,
                      !afterColon.hasPrefix("{"),
                      !afterColon.hasPrefix("["),
                      !afterColon.hasPrefix("|"),
                      !afterColon.hasPrefix(">") else { continue }
                result["\(sec).\(key)"] = afterColon
            }
        }
        return result
    }

    /// Rewrites lines whose `section.key` matches an entry in `updates`.
    /// All other lines are copied verbatim.
    static func rewriteYAMLKeys(_ content: String, updates: [String: String]) -> String {
        var lines: [String] = []
        var section: String? = nil

        for line in content.components(separatedBy: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)

            if !line.hasPrefix(" ") && !line.hasPrefix("\t") {
                if trimmed.hasSuffix(":") && !trimmed.contains(" ") && !trimmed.hasPrefix("#") {
                    section = String(trimmed.dropLast())
                } else {
                    section = nil
                }
                lines.append(line)
                continue
            }

            guard let sec = section,
                  !trimmed.isEmpty,
                  !trimmed.hasPrefix("#"),
                  !trimmed.hasPrefix("-"),
                  let colonIdx = trimmed.firstIndex(of: ":") else {
                lines.append(line)
                continue
            }

            let key = String(trimmed[..<colonIdx]).trimmingCharacters(in: .whitespaces)
            let dottedKey = "\(sec).\(key)"

            if let newValue = updates[dottedKey] {
                let leadingWS = line.prefix(while: { $0 == " " || $0 == "\t" })
                lines.append("\(leadingWS)\(key): \(newValue)")
            } else {
                lines.append(line)
            }
        }
        return lines.joined(separator: "\n")
    }

    // MARK: - File I/O helpers

    private func writeBackup(data: Data, to url: URL) throws {
        let ts = ISO8601DateFormatter().string(from: Date())
            .replacingOccurrences(of: ":", with: "")
        let uid = String(UUID().uuidString.prefix(8))
        let ext = url.pathExtension
        let backupName = "config.mighty-backup-\(ts)-\(uid).\(ext)"
        let backupURL = url.deletingLastPathComponent().appendingPathComponent(backupName)
        try data.write(to: backupURL)
    }

    private func atomicWrite(data: Data, to url: URL) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let tmp = url.deletingLastPathComponent()
            .appendingPathComponent(".\(url.lastPathComponent).tmp-\(UUID().uuidString)")
        try data.write(to: tmp)
        if fm.fileExists(atPath: url.path) {
            do {
                _ = try fm.replaceItem(at: url, withItemAt: tmp, backupItemName: nil,
                                       options: .usingNewMetadataOnly, resultingItemURL: nil)
            } catch {
                try? fm.removeItem(at: tmp)
                throw error
            }
        } else {
            try fm.moveItem(at: tmp, to: url)
        }
    }
}
