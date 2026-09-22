import Foundation

/// Compares credential sources in memory. A mismatch identifies conflicting
/// configuration, not which key is valid or which credential AWS accepted.
public enum BedrockAuthDiagnostics {
    public static let conflictMessage = "터미널 환경과 Claude 설정 파일에 서로 다른 Bedrock 키가 있습니다. Bedrock 설정에서 사용할 인증 정보를 확인하세요."
    public static let scpInvokeModelDeniedMessage = "AWS 조직 정책(SCP)이 Bedrock 모델 호출을 명시적으로 거부했습니다. AWS Organizations 관리자에게 해당 계정·역할·모델의 호출을 차단하는 SCP를 확인해 달라고 요청하세요. 이 오류만으로 어떤 정책이나 리전 조건이 적용됐는지는 확인할 수 없습니다."
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
