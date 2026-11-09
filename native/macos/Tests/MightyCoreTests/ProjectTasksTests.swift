import Foundation
import Testing
@testable import MightyCore

/// 작업 실행 (`ProjectTasks.swift`): what a workspace defines, and the ports listening.
struct ProjectTasksTests {
    @Test func packageScriptsKeepTheirOrderAndManager() throws {
        let json = #"{"name":"x","scripts":{"dev":"vite","build":"vite build","test:unit":"vitest","odd name":"x"}}"#
        let tasks = ProjectTasks.packageScripts(Data(json.utf8), manager: "pnpm")
        #expect(tasks.map(\.name) == ["dev", "build", "test:unit", "odd name"])
        #expect(tasks.map(\.command) == ["pnpm run dev", "pnpm run build", "pnpm run test:unit", "pnpm run 'odd name'"])
    }

    @Test func makeAndJustTargetsSkipVariablesAndSpecials() {
        let make = """
        CC := clang
        VERSION = 1
        .PHONY: all test
        all: build
        build test: deps
        \tswift build
        %.o: %.c
        # comment: no
        $(OUT): x
        install:
        """
        #expect(ProjectTasks.makeTargets(make).map(\.name) == ["all", "build", "test", "install"])
        let just = """
        set shell := ["bash", "-c"]
        default:
            just --list
        deploy env="prod":
            ./deploy {{env}}
        version := "1"
        """
        #expect(ProjectTasks.justRecipes(just).map(\.command) == ["just default", "just deploy"])
    }

    @Test func aFolderIsDetected() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-tasks-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try Data(#"{"scripts":{"dev":"next"}}"#.utf8).write(to: root.appendingPathComponent("package.json"))
        try Data().write(to: root.appendingPathComponent("yarn.lock"))
        try Data("// swift-tools-version:5.9".utf8).write(to: root.appendingPathComponent("Package.swift"))
        #expect(ProjectTasks.detect(root: root).map(\.command) == ["yarn run dev", "swift build", "swift test"])
    }

    @Test func listeningPortsAreReadOncePerPort() {
        let output = "p101\ncnode\nn*:3000\nn[::1]:3000\np202\ncpython3\nn127.0.0.1:8000\np303\ncrapportd\nn*:49152\n"
        let ports = ProjectTasks.parseListening(output)
        #expect(ports == [ListeningPort(port: 3000, process: "node", pid: 101), ListeningPort(port: 8000, process: "python3", pid: 202), ListeningPort(port: 49152, process: "rapportd", pid: 303)])
        #expect(ports[0].url?.absoluteString == "http://localhost:3000")
    }
}
