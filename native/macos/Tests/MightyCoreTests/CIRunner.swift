import Foundation
import Testing

/// Tests that hold a wall-clock bound (a process stopping within seconds, a loopback answer within the request timeout, an
/// event deadline) and failed on GitHub's loaded macOS runners with no code change behind it (2026-11-12, read from the
/// failed runs of native-macos.yml). They run on every Mac; on GitHub Actions they are skipped, so a release is not held by
/// the runner's load.
enum CIRunner {
    static let onGitHubActions = ProcessInfo.processInfo.environment["GITHUB_ACTIONS"] == "true"
    static let timing: Comment = "Holds a wall-clock bound that GitHub's loaded macOS runners miss; runs on every Mac."
}
