import Foundation

/// Compares credential sources in memory. A mismatch identifies conflicting
/// configuration, not which key is valid or which credential AWS accepted.
public enum BedrockAuthDiagnostics {
    public static var conflictMessage: String { L("bedrock.conflict") }
    public static var scpInvokeModelDeniedMessage: String { L("bedrock.scpDenied") }
    static let maximumSettingsBytes = 1_048_576
    private static let bearerKey = "AWS_BEARER_TOKEN_BEDROCK"

    /// The CLI may prepend generic credential advice to any Bedrock 403. Only
    /// the bounded AWS JSON message can establish this narrower failure cause.
    public static func runtimeFailureGuidance(_ text: String) -> String? {
        guard text.utf8.count <= 65_536,
              let marker = text.range(of: #"(?:^|\bAPI Error:\s*)403\s+(?=\{)"#, options: [.regularExpression, .caseInsensitive]),
              let payload = String(text[marker.upperBound...]).data(using: .utf8),
              let body = try? JSONSerialization.jsonObject(with: payload) as? [String: Any],
              let message = (body["Message"] ?? body["message"]) as? String else { return nil }
        for key in ["__type", "code", "Code"] {
            if let value = body[key] as? String {
                let name = value.split(separator: "#").last?.split(separator: ":").first.map(String.init)
                guard name == "AccessDeniedException" else { return nil }
            }
        }
        let pattern = #"\AUser:\s+\S+\s+is not authorized to perform:?\s+bedrock:(?:InvokeModel|InvokeModelWithResponseStream)(?:\s+on resource:\s+\S+)?\s+(?:with|because of) an explicit deny in a service control policy(?::\s+arn:aws:organizations::[0-9]{12}:policy/o-[a-z0-9]{10,32}/service_control_policy/p-[a-z0-9]{8,128})?\.?\z"#
        guard message.trimmingCharacters(in: .whitespacesAndNewlines).range(of: pattern, options: [.regularExpression, .caseInsensitive]) != nil else { return nil }
        return scpInvokeModelDeniedMessage
    }

    public static func conflictDetail(environment: [String: String], userSettings: Data?) -> String? {
        guard let shellToken = environment[bearerKey], !shellToken.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let userSettings, userSettings.count <= maximumSettingsBytes,
              let object = try? JSONSerialization.jsonObject(with: userSettings) as? [String: Any],
              let settingsEnvironment = object["env"] as? [String: Any],
              let settingsToken = settingsEnvironment[bearerKey] as? String,
              !settingsToken.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              shellToken.trimmingCharacters(in: .whitespacesAndNewlines) != settingsToken.trimmingCharacters(in: .whitespacesAndNewlines) else { return nil }
        return conflictMessage
    }

    /// The settings URL is chosen by the same environment used to launch the
    /// CLI. Only a bounded regular file is read; nothing is written or logged.
    public static func conflictDetail(environment: [String: String], home: URL) -> String? {
        let directory: URL
        if let path = environment["CLAUDE_CONFIG_DIR"], !path.isEmpty {
            directory = path.hasPrefix("/") ? URL(fileURLWithPath: path, isDirectory: true) : home.appendingPathComponent(path, isDirectory: true)
        } else {
            directory = home.appendingPathComponent(".claude", isDirectory: true)
        }
        return conflictDetail(environment: environment, userSettings: boundedSettingsData(directory.appendingPathComponent("settings.json")))
    }

    static func boundedSettingsData(_ url: URL) -> Data? {
        guard let attributes = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey]),
              attributes.isRegularFile == true, let size = attributes.fileSize, size <= maximumSettingsBytes,
              let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let data = try? handle.read(upToCount: maximumSettingsBytes + 1), data.count <= maximumSettingsBytes else { return nil }
        return data
    }
}
