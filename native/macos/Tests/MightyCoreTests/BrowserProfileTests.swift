import Foundation
import Testing
@testable import MightyCore

struct BrowserProfileTests {
    @Test func clearStaleLockRemovesOnlyLockFiles() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("cef-lock-test-\(UUID())")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        for name in ["SingletonLock", "SingletonSocket", "SingletonCookie"] {
            try Data("lock".utf8).write(to: dir.appendingPathComponent(name))
        }
        try Data("data".utf8).write(to: dir.appendingPathComponent("Cookies"))

        BrowserProfileSupport.clearStaleLock(at: dir)

        for name in ["SingletonLock", "SingletonSocket", "SingletonCookie"] {
            #expect(!FileManager.default.fileExists(atPath: dir.appendingPathComponent(name).path),
                    "expected \(name) to be removed")
        }
        #expect(FileManager.default.fileExists(atPath: dir.appendingPathComponent("Cookies").path))
    }

    @Test func clearStaleLockKeepsProfileData() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("cef-data-test-\(UUID())")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        for name in ["SingletonLock", "SingletonSocket", "SingletonCookie"] {
            try Data("lock".utf8).write(to: dir.appendingPathComponent(name))
        }
        let dataFiles = ["Cookies", "History", "Local State", "Preferences"]
        for name in dataFiles {
            try Data("data".utf8).write(to: dir.appendingPathComponent(name))
        }

        BrowserProfileSupport.clearStaleLock(at: dir)

        for name in dataFiles {
            #expect(FileManager.default.fileExists(atPath: dir.appendingPathComponent(name).path),
                    "expected \(name) to be kept")
        }
    }
}
