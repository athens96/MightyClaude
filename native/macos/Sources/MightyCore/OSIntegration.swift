import Foundation

/// `mightyclaude://open?path=<folder>&prompt=<text>&agent=<claude|codex|gemini>`:
/// opens the folder as a workspace (asking first when it is not one yet) and
/// puts the text in an agent pane's composer. Nothing is ever sent by a link:
/// any web page can open one.
public struct DeepLink: Equatable, Sendable {
    public static let scheme = "mightyclaude"
    public static let maximumPromptBytes = 64 * 1024

    public let path: String
    public let prompt: String?
    public let agent: String?

    public static func parse(_ url: URL) -> DeepLink? {
        guard url.scheme?.lowercased() == scheme, url.host?.lowercased() == "open",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems else { return nil }
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        guard let raw = value("path"), raw.hasPrefix("/"), !raw.contains("\0") else { return nil }
        let path = URL(fileURLWithPath: raw).standardizedFileURL.path
        let prompt = value("prompt").flatMap { $0.isEmpty || $0.utf8.count > maximumPromptBytes ? nil : $0 }
        let agent = value("agent").flatMap { ResumableSessions.providers.contains($0) ? $0 : nil }
        return DeepLink(path: path, prompt: prompt, agent: agent)
    }

    public static func url(path: String, prompt: String? = nil) -> URL? {
        var components = URLComponents()
        components.scheme = scheme; components.host = "open"
        components.queryItems = [URLQueryItem(name: "path", value: path)] + (prompt.map { [URLQueryItem(name: "prompt", value: $0)] } ?? [])
        return components.url
    }
}

/// The `mighty` shell command: `mighty [folder] [request…]` opens the folder
/// (default: the current one) through the link above.
public enum ShellCommand {
    public static let name = "mighty"

    public static let script = """
    #!/bin/sh
    # Mighty Claude: open a folder (default: the current one) as a workspace,
    # with an optional request put in the composer (never sent).
    #   mighty                 open this folder
    #   mighty ../other        open another folder
    #   mighty "fix the tests" open this folder with a request
    dir="$PWD"
    if [ $# -gt 0 ] && [ -d "$1" ]; then dir="$(cd "$1" && pwd)"; shift; fi
    enc() { osascript -l JavaScript -e 'function run(a) { return encodeURIComponent(a[0]) }' "$1"; }
    url="mightyclaude://open?path=$(enc "$dir")"
    if [ $# -gt 0 ]; then url="$url&prompt=$(enc "$*")"; fi
    exec open "$url"
    """

    /// `~/.local/bin`, the folder the command goes in (no administrator needed).
    public static func folder(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        home.appendingPathComponent(".local/bin", isDirectory: true)
    }

    /// Writes the command (executable); returns where.
    @discardableResult
    public static func install(home: URL = FileManager.default.homeDirectoryForCurrentUser) throws -> URL {
        let folder = folder(home: home)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent(name)
        try Data((script + "\n").utf8).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return url
    }

    /// Whether `folder` is on a login shell's PATH.
    public static func onPath(_ path: String?, folder: URL) -> Bool {
        (path ?? "").split(separator: ":").contains { URL(fileURLWithPath: String($0)).standardizedFileURL.path == folder.standardizedFileURL.path }
    }
}

/// A finished run told to a chat webhook (Slack, Discord or any endpoint
/// that takes JSON): the same words as the Mac's own notification, no
/// prompt, output or path.
public enum CompletionWebhook {
    public static let defaultsKey = "notifications.webhookURL"

    /// Only https, or http to this Mac.
    public static func validURL(_ text: String) -> URL? {
        guard let url = URL(string: text.trimmingCharacters(in: .whitespacesAndNewlines)), let host = url.host, !host.isEmpty else { return nil }
        switch url.scheme?.lowercased() {
        case "https": return url
        case "http": return ["localhost", "127.0.0.1", "::1"].contains(host) ? url : nil
        default: return nil
        }
    }

    /// Slack reads `text`, Discord `content`; both are sent.
    public static func body(message: String) -> Data {
        (try? JSONSerialization.data(withJSONObject: ["text": message, "content": message], options: [.sortedKeys])) ?? Data()
    }

    public static func request(url: URL, message: String) -> URLRequest {
        var request = URLRequest(url: url, timeoutInterval: 10)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = body(message: message)
        return request
    }
}
