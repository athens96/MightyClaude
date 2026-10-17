import Foundation

/// Which items an install runs and in what order: the picked ones plus every
/// prerequisite that is not there yet, prerequisites first, the catalog's order
/// breaking ties. Pure, so the screen and the tests share it.
public enum SetupPlan {
    public static func order(selected: Set<String>, catalog: SetupCatalog, platform: SetupPlatform, isSatisfied: (String) -> Bool) -> [String] {
        var included = Set<String>()
        func include(_ id: String) {
            guard let entry = catalog.item(id)?.platforms[platform], included.insert(id).inserted else { return }
            for prerequisite in entry.prerequisites where !isSatisfied(prerequisite) { include(prerequisite) }
        }
        for item in catalog.items where selected.contains(item.id) { include(item.id) }
        return topological(included, catalog: catalog, platform: platform) ?? catalog.items.map(\.id).filter(included.contains)
    }

    /// The prerequisites `order` adds that were not picked themselves.
    public static func addedPrerequisites(selected: Set<String>, catalog: SetupCatalog, platform: SetupPlatform, isSatisfied: (String) -> Bool) -> Set<String> {
        Set(order(selected: selected, catalog: catalog, platform: platform, isSatisfied: isSatisfied)).subtracting(selected)
    }

    static func hasNoCycle(catalog: SetupCatalog, platform: SetupPlatform) -> Bool {
        topological(Set(catalog.items.map(\.id)), catalog: catalog, platform: platform) != nil
    }

    /// Kahn's order over `ids`, earliest catalog item first among the ready ones; nil on a cycle.
    private static func topological(_ ids: Set<String>, catalog: SetupCatalog, platform: SetupPlatform) -> [String]? {
        let ordered = catalog.items.map(\.id).filter(ids.contains)
        var waiting = Dictionary(uniqueKeysWithValues: ordered.map { id in
            (id, Set((catalog.item(id)?.platforms[platform]?.prerequisites ?? []).filter(ids.contains)))
        })
        var result: [String] = []
        while !waiting.isEmpty {
            guard let next = ordered.first(where: { waiting[$0]?.isEmpty == true }) else { return nil }
            result.append(next)
            waiting.removeValue(forKey: next)
            for key in waiting.keys { waiting[key]?.remove(next) }
        }
        return result
    }
}

/// One item's part of the generated install script.
public struct SetupScriptStep: Sendable, Equatable, Identifiable {
    public var itemId: String
    public var title: String
    /// What runs, exactly as the script has it; the confirmation shows this.
    public var body: String
    /// The step waits for a system window to finish the install; Control-C stops
    /// only that wait, not the script.
    public var waitsOutside: Bool
    public var id: String { itemId }
    public init(itemId: String, title: String, body: String, waitsOutside: Bool = false) {
        self.itemId = itemId; self.title = title; self.body = body; self.waitsOutside = waitsOutside
    }
}

/// The words the script prints, in the app's language.
public struct SetupScriptLabels: Sendable {
    public var step: @Sendable (_ index: Int, _ count: Int, _ title: String) -> String
    public var failed: String
    public var waiting: String
    public var done: String
    public init(step: @escaping @Sendable (Int, Int, String) -> String, failed: String, waiting: String, done: String) {
        self.step = step; self.failed = failed; self.waiting = waiting; self.done = done
    }
}

/// Builds the one script the setup terminal runs, from catalog items only:
/// every value is single-quoted, and nothing the user typed or a file outside
/// the bundled catalog said reaches it. The app never adds sudo; a step that
/// needs a password asks for it in the terminal itself.
public enum SetupScript {
    /// npm's public registry: a project's .npmrc cannot point an install elsewhere.
    public static let npmRegistry = "https://registry.npmjs.org/"
    /// curl over https only, redirects included, TLS 1.2 or newer.
    public static let curl = "/usr/bin/curl --proto '=https' --proto-redir '=https' --tlsv1.2 -fsSL"
    /// Programs an argv recipe names, by the absolute path the script calls.
    public static let absolutePrograms = ["xcode-select": "/usr/bin/xcode-select"]
    /// How long the script waits for a system window to finish an install.
    public static let outsideWaitSeconds = 1800

    /// POSIX single quotes: the text is taken literally, a quote inside is closed, escaped and reopened.
    public static func quote(_ text: String) -> String {
        "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    /// The lines that run before any step, shown in the confirmation too: the
    /// home folder as the working folder (not a project whose settings could steer
    /// an install), the installers' folders after the PATH the shell already has
    /// (`CLIEnvironmentResolver.wellKnownFolders`), and npm's public registry.
    public static func preamble(home: URL) -> [String] {
        [
            "cd \"$HOME\" || exit 1",
            "export PATH=\"$PATH\"" + CLIEnvironmentResolver.wellKnownFolders(home: home).map { ":" + quote($0) }.joined(),
            "export npm_config_registry=" + quote(npmRegistry),
        ]
    }

    /// The steps for `ids` in that order. Items that only open a page, or have no
    /// entry for the platform, are left out.
    public static func steps(for ids: [String], catalog: SetupCatalog, platform: SetupPlatform, title: (SetupItem) -> String) -> [SetupScriptStep] {
        ids.compactMap { id in
            guard let item = catalog.item(id), let entry = item.platforms[platform], let body = body(entry) else { return nil }
            return SetupScriptStep(itemId: id, title: title(item), body: body, waitsOutside: waitsOutside(entry))
        }
    }

    static func waitsOutside(_ entry: SetupPlatformEntry) -> Bool {
        if case .argv(_, true) = entry.install, entry.probe == .commandLineTools { return true }
        return false
    }

    /// What one platform entry runs, or nil when nothing runs in the terminal.
    public static func body(_ entry: SetupPlatformEntry) -> String? {
        switch entry.install {
        case .argv(let argv, _):
            let program = argv.first.map { absolutePrograms[$0] ?? $0 }
            let command = ([program].compactMap { $0 } + argv.dropFirst()).map(quote).joined(separator: " ")
            // The system window finishes the install; later steps wait for it, up to
            // half an hour. Control-C ends only this wait (`render` keeps the script going).
            if waitsOutside(entry) {
                return command + "; trap 'break' INT; mighty_waited=0; until /usr/bin/xcode-select -p >/dev/null 2>&1 || [ \"$mighty_waited\" -ge \(outsideWaitSeconds) ]; do sleep 5; mighty_waited=$((mighty_waited + 5)); done; trap - INT"
            }
            return command
        case .officialScript(let url, let shell):
            // Fetched first and handed to the shell as its script, so the script's
            // own questions (a password, a yes/no) still read the terminal.
            let interpreter = shell == "sh" ? "/bin/sh" : "/bin/bash"
            return "mighty_script=\"$(\(curl) \(quote(url.absoluteString)))\" && \(interpreter) -c \"$mighty_script\""
        case .brewFormula(let name): return "brew install " + quote(name)
        case .brewCask(let name): return "brew install --cask " + quote(name)
        case .npmGlobal(let name): return "npm install -g " + quote(name)
        case .claudePlugin(let marketplace, let plugin):
            // An already added marketplace is fine; the install decides. The
            // marketplace, not the app, decides the plugin's version.
            return "claude plugin marketplace add \(quote(marketplace)); claude plugin install \(quote(plugin)) --scope user"
        case .skill(let repository, let folder, let commit, let setup):
            let destination = "\"$HOME\"/" + quote(".claude/skills/" + folder)
            let url = quote(repository.absoluteString), sha = quote(commit)
            let installing = destination + "/" + quote(SetupProber.skillInstallingMarker)
            let done = destination + "/" + quote(SetupProber.skillDoneMarker)
            // An existing clone moves to the pinned commit; otherwise the commit is
            // fetched into a temporary folder that is moved into place only when complete.
            var body = "if [ -d \(destination)/.git ]; then git -C \(destination) fetch -q --depth 1 \(url) \(sha) && git -C \(destination) checkout -q --detach FETCH_HEAD; "
                + "else mighty_tmp=\"$(mktemp -d)\" && git init -q \"$mighty_tmp/repo\" && git -C \"$mighty_tmp/repo\" fetch -q --depth 1 \(url) \(sha) && git -C \"$mighty_tmp/repo\" checkout -q --detach FETCH_HEAD "
                + "&& mkdir -p \"$HOME\"/" + quote(".claude/skills") + " && mv \"$mighty_tmp/repo\" \(destination); fi"
                + " && rm -f \(done) && : > \(installing)"
            if let setup { body += " && (cd \(destination) && " + quote("./" + setup) + ")" }
            return body + " && : > \(done)"
        // A Windows install: the Windows app writes its own PowerShell for it, and a Mac entry never has one.
        case .winget, .vendorPage: return nil
        }
    }

    /// The whole script. `startedMarker` is made first thing and `doneMarker` when
    /// the script ends for any reason (finished, a failed step, Control-C); both
    /// are folders made with mkdir, which never follows a link that is already
    /// there. They tell the app the script started and ended.
    public static func render(steps: [SetupScriptStep], labels: SetupScriptLabels, home: URL, startedMarker: String, doneMarker: String) -> String {
        var lines = [
            "#!/bin/bash",
            "mkdir -- \(quote(startedMarker)) 2>/dev/null",
            "# Written by Mighty Claude from its bundled tool catalog (setup-catalog.json).",
            "mighty_marker=" + quote(doneMarker),
            "trap 'mkdir -- \"$mighty_marker\" 2>/dev/null' EXIT",
        ] + preamble(home: home) + ["mighty_failed=0"]
        for (index, step) in steps.enumerated() {
            lines.append("printf '\\n\\033[1m%s\\033[0m\\n' " + quote(labels.step(index + 1, steps.count, step.title)))
            if step.waitsOutside {
                lines.append("printf '%s\\n' " + quote(labels.waiting))
                // Control-C reaches the whole group: this shell only notes it, and the step's own trap ends the wait.
                lines.append("trap ':' INT")
            }
            lines.append("if ( \(step.body) ); then :; else mighty_failed=1; printf '%s\\n' \(quote(labels.failed)); fi")
            if step.waitsOutside { lines.append("trap - INT") }
        }
        lines.append("printf '\\n%s\\n' " + quote(labels.done))
        lines.append("exit $mighty_failed")
        return lines.joined(separator: "\n") + "\n"
    }

    /// What the terminal types to run the written script.
    public static func launchCommand(scriptPath: String) -> String { "/bin/bash " + quote(scriptPath) }
}

/// The folder the install script and its markers live in, held to what only
/// this user can touch: a real folder (not a link), owned by this user, with no
/// group or other permissions. The script is created new under a random name
/// and never through a link.
public enum SetupScriptFiles {
    /// Creates `directory` (0700) when it is not there, then checks it.
    public static func prepareDirectory(_ directory: URL) throws {
        let path = directory.path
        if Darwin.mkdir(path, 0o700) != 0, errno != EEXIST { throw MightyError("setup folder: " + String(cString: strerror(errno))) }
        var info = stat()
        guard Darwin.lstat(path, &info) == 0 else { throw MightyError("setup folder: " + String(cString: strerror(errno))) }
        guard info.st_mode & S_IFMT == S_IFDIR else { throw MightyError("setup folder is not a plain folder: " + path) }
        guard info.st_uid == getuid() else { throw MightyError("setup folder belongs to another user: " + path) }
        guard info.st_mode & 0o077 == 0 else { throw MightyError("setup folder is open to other users: " + path) }
    }

    /// Writes `text` to a new file with a random name in `directory`, 0700.
    public static func writeScript(_ text: String, in directory: URL) throws -> URL {
        let url = directory.appendingPathComponent("install-\(UUID().uuidString).sh")
        let fd = Darwin.open(url.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o700)
        guard fd >= 0 else { throw MightyError("setup script: " + String(cString: strerror(errno))) }
        defer { Darwin.close(fd) }
        guard fchmod(fd, 0o700) == 0 else { throw MightyError("setup script: " + String(cString: strerror(errno))) }
        let data = Array(text.utf8)
        var offset = 0
        while offset < data.count {
            let written = data[offset...].withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) }
            if written < 0 { if errno == EINTR { continue }; throw MightyError("setup script: " + String(cString: strerror(errno))) }
            offset += written
        }
        return url
    }

    /// Fresh marker paths in `directory`, which nothing has made yet.
    public static func markers(in directory: URL) -> (started: URL, done: URL) {
        let id = UUID().uuidString
        return (directory.appendingPathComponent("started-\(id)"), directory.appendingPathComponent("done-\(id)"))
    }

    /// True only for a real folder at `url`: a link there does not count.
    public static func markerExists(_ url: URL) -> Bool {
        var info = stat()
        return Darwin.lstat(url.path, &info) == 0 && info.st_mode & S_IFMT == S_IFDIR
    }

    /// Removes what earlier runs left in `directory`; a link is removed, never followed.
    public static func removeLeftovers(in directory: URL) {
        for entry in (try? FileManager.default.contentsOfDirectory(atPath: directory.path)) ?? [] {
            try? FileManager.default.removeItem(atPath: directory.appendingPathComponent(entry).path)
        }
    }
}
