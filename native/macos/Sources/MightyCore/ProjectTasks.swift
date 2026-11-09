import Foundation

/// A command the workspace's own files define (a package.json script, a
/// Makefile target, …), to type into a terminal pane.
public struct ProjectTask: Sendable, Equatable, Identifiable {
    public var id: String { source + "|" + name }
    public let name: String
    public let command: String
    /// The file it came from: `package.json`, `Makefile`, `Package.swift`, …
    public let source: String
}

/// A TCP port a process of this user listens on.
public struct ListeningPort: Sendable, Equatable, Identifiable {
    public var id: Int { port }
    public let port: Int
    public let process: String
    public let pid: Int
    public var url: URL? { URL(string: "http://localhost:\(port)") }
}

/// 작업 실행: the commands a workspace defines and the dev servers running,
/// for the header's 작업 panel. The commands are typed into a new terminal
/// pane and left for the user to start (they come from the project's files,
/// so the app never presses Enter for them).
public enum ProjectTasks {
    public static let maximumTasks = 80

    /// `name` as one shell word: as it is when it holds only safe characters, else single-quoted.
    public static func shellWord(_ name: String) -> String {
        let safe = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_.:/@+="))
        if !name.isEmpty, name.unicodeScalars.allSatisfy({ safe.contains($0) && $0.isASCII }) { return name }
        return "'" + name.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    /// The package manager a JavaScript project uses, by its lock file.
    static func packageManager(root: URL) -> String {
        let has = { (name: String) in FileManager.default.fileExists(atPath: root.appendingPathComponent(name).path) }
        if has("pnpm-lock.yaml") { return "pnpm" }
        if has("yarn.lock") { return "yarn" }
        if has("bun.lockb") || has("bun.lock") { return "bun" }
        return "npm"
    }

    /// `package.json`'s scripts, in the file's order.
    public static func packageScripts(_ data: Data, manager: String) -> [ProjectTask] {
        guard let text = String(data: data, encoding: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let scripts = object["scripts"] as? [String: Any] else { return [] }
        // JSONSerialization forgets the order; the names' places in the text keep it.
        let names = scripts.keys.sorted { (text.range(of: "\"\($0)\"")?.lowerBound ?? text.endIndex) < (text.range(of: "\"\($1)\"")?.lowerBound ?? text.endIndex) }
        return names.map { ProjectTask(name: $0, command: "\(manager) run \(shellWord($0))", source: "package.json") }
    }

    /// A Makefile's explicit targets (not `.PHONY`-like special ones, pattern rules or variables), in order.
    public static func makeTargets(_ text: String) -> [ProjectTask] {
        var seen = Set<String>(), tasks: [ProjectTask] = []
        for line in text.split(whereSeparator: \.isNewline) {
            guard let first = line.first, !first.isWhitespace, first != "#", first != ".",
                  let colon = line.firstIndex(of: ":") else { continue }
            let after = line[line.index(after: colon)...]
            if after.hasPrefix("=") || line[..<colon].contains("=") || line[..<colon].contains("%") || line[..<colon].contains("$") { continue }
            for target in line[..<colon].split(separator: " ").map(String.init) where seen.insert(target).inserted {
                tasks.append(ProjectTask(name: target, command: "make \(shellWord(target))", source: "Makefile"))
            }
        }
        return tasks
    }

    /// A justfile's recipes (lines `name args*:` at the start of a line).
    public static func justRecipes(_ text: String) -> [ProjectTask] {
        text.split(whereSeparator: \.isNewline).compactMap { line in
            guard let first = line.first, first.isLetter || first == "_", let colon = line.firstIndex(of: ":"),
                  !line[line.index(after: colon)...].hasPrefix("=") else { return nil }
            // A recipe's parameters may carry defaults (`env="prod"`); its name never holds `=`.
            guard let name = line[..<colon].split(separator: " ").first.map(String.init), !name.contains("="),
                  !["set", "export", "alias", "import", "mod"].contains(name) else { return nil }
            return ProjectTask(name: name, command: "just \(shellWord(name))", source: "justfile")
        }
    }

    /// Everything the workspace at `root` defines.
    public static func detect(root: URL) -> [ProjectTask] {
        func read(_ name: String) -> Data? { try? Data(contentsOf: root.appendingPathComponent(name)) }
        func exists(_ name: String) -> Bool { FileManager.default.fileExists(atPath: root.appendingPathComponent(name).path) }
        var tasks: [ProjectTask] = []
        if let data = read("package.json") { tasks += packageScripts(data, manager: packageManager(root: root)) }
        for name in ["Makefile", "makefile", "GNUmakefile"] {
            if let data = read(name), let text = String(data: data, encoding: .utf8) { tasks += makeTargets(text); break }
        }
        for name in ["justfile", "Justfile", ".justfile"] {
            if let data = read(name), let text = String(data: data, encoding: .utf8) { tasks += justRecipes(text); break }
        }
        if exists("Package.swift") {
            tasks += ["build", "test"].map { ProjectTask(name: $0, command: "swift \($0)", source: "Package.swift") }
        }
        if exists("Cargo.toml") {
            tasks += ["build", "test", "run"].map { ProjectTask(name: $0, command: "cargo \($0)", source: "Cargo.toml") }
        }
        if exists("go.mod") {
            tasks += [("build", "go build ./..."), ("test", "go test ./...")].map { ProjectTask(name: $0.0, command: $0.1, source: "go.mod") }
        }
        return Array(tasks.prefix(maximumTasks))
    }

    // MARK: - Listening ports

    /// Reads `lsof -nP -iTCP -sTCP:LISTEN -F pcn`: `p<pid>`, `c<command>`, `n<address>` lines.
    public static func parseListening(_ output: String) -> [ListeningPort] {
        var result: [Int: ListeningPort] = [:]
        var pid = 0, command = ""
        for line in output.split(whereSeparator: \.isNewline) {
            guard let tag = line.first else { continue }
            let value = String(line.dropFirst())
            switch tag {
            case "p": pid = Int(value) ?? 0
            case "c": command = value
            case "n":
                guard let colon = value.lastIndex(of: ":"), let port = Int(value[value.index(after: colon)...]), port > 0 else { continue }
                if result[port] == nil { result[port] = ListeningPort(port: port, process: command, pid: pid) }
            default: continue
            }
        }
        return result.values.sorted { $0.port < $1.port }
    }

    /// This user's listening TCP ports, from 1024 up (system services left out); empty when lsof cannot tell.
    public static func listeningPorts() async -> [ListeningPort] {
        guard let result = try? await ProcessCapture.run(executable: URL(fileURLWithPath: "/usr/sbin/lsof"), arguments: ["-nP", "-iTCP", "-sTCP:LISTEN", "-F", "pcn"],
                                                         timeout: 5, maximumBytes: 1_048_576) else { return [] }
        return parseListening(String(decoding: result.stdout, as: UTF8.self)).filter { $0.port >= 1024 }
    }
}
