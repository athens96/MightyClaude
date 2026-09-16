import AppKit
import Darwin
import Foundation
import GhosttyTerminal
import MightyCore

extension AppStore {
    /// Exercises a real Ghostty surface in an explicitly selected, disposable
    /// profile. All commands are fixed fixtures; no AI process or clipboard is used.
    func runTerminalSmokeTest() async {
        let arguments = ProcessInfo.processInfo.arguments
        guard arguments.contains("--profile"), terminalSmokeMode else {
            error = "터미널 스모크 테스트에는 --profile 임시 폴더가 필요합니다."
            if arguments.contains("--smoke-exit") { Darwin.exit(1) }
            return
        }
        var result: [String: Any] = ["native": true, "engine": "Ghostty", "passed": false]
        var stage = "prepare"
        var currentTerminal: LocalTerminalSession?
        var originalFrame: NSRect?
        var smokeWindow: NSWindow?
        do {
            let folder = dataDirectory.appendingPathComponent("Terminal Smoke Workspace", isDirectory: true)
            let nested = folder.appendingPathComponent("Persistent Folder", isDirectory: true)
            let otherFolder = dataDirectory.appendingPathComponent("Terminal Smoke Other", isDirectory: true)
            try FileManager.default.createDirectory(at: nested, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: otherFolder, withIntermediateDirectories: true)
            let workspace = try await repository.approveWorkspace(Workspace(name: "Ghostty Smoke", path: folder.path))
            let other = try await repository.approveWorkspace(Workspace(name: "Other Workspace", path: otherFolder.path))
            addWorkspace(workspace)
            addSession(kind: "shell")
            guard let id = snapshot.activeSessionId else { throw MightyError("터미널 실행 창을 만들지 못했습니다.") }
            setPaneFocus(true)
            await ensureLocalTerminal(id)
            try await waitForSmoke(timeout: 15) {
                self.localTerminals[id]?.ready == true && self.localTerminals[id]?.view.window != nil
            }
            guard let terminal = localTerminals[id], let window = terminal.view.window else {
                throw MightyError("Ghostty AppKit 화면이 준비되지 않았습니다.")
            }
            currentTerminal = terminal
            smokeWindow = window
            originalFrame = window.frame
            window.makeKeyAndOrderFront(nil)
            terminal.view.acquireProgrammaticFocus()

            stage = "tty"
            let shellPIDFile = folder.appendingPathComponent("shell-pid.txt")
            let initialGridFile = folder.appendingPathComponent("initial-grid.txt")
            try terminalSmokeSend(terminal, "HISTSIZE=100; SAVEHIST=0; PROMPT='mighty-smoke% '; RPROMPT=''; MIGHTY_HISTORY=0; /usr/bin/tty; [[ -t 0 && -t 1 && -t 2 ]] && printf 'MIGHTY_%s\\n' 'PTY_OK'; printf '%s\\n' \"$$\" > \(terminalSmokeQuote(shellPIDFile.path)); /bin/stty size > \(terminalSmokeQuote(initialGridFile.path))")
            try await terminalSmokeWaitText(terminal, "MIGHTY_PTY_OK")
            try await waitForSmoke(timeout: 5) { self.terminalSmokePID(shellPIDFile) != nil && self.terminalSmokeGrid(initialGridFile) != nil && terminal.grid != nil }
            guard let shellPID = terminalSmokePID(shellPIDFile), terminalSmokeAlive(shellPID), let initialGrid = terminalSmokeGrid(initialGridFile) else {
                throw MightyError("실제 셸 PID와 PTY 크기를 확인하지 못했습니다.")
            }
            result["tty"] = true
            result["shellPID"] = Int(shellPID)
            result["initialPTYGrid"] = initialGrid

            stage = "persistent-directory"
            let cwdFile = folder.appendingPathComponent("cwd.txt")
            guard let encodedDirectory = URLComponents(url: nested, resolvingAgainstBaseURL: false)?.percentEncodedPath else { throw MightyError("검증 폴더 URL을 만들지 못했습니다.") }
            // Ghostty accepts OSC 7 only for the local hostname. Match the
            // package's zsh integration instead of sending a hostless file URL.
            try terminalSmokeSend(terminal, "cd \(terminalSmokeQuote(nested.path)); /bin/pwd -P > \(terminalSmokeQuote(cwdFile.path)); printf '\\033]7;file://%s%s\\007' \"$HOST\" \(terminalSmokeQuote(encodedDirectory)); printf '\\033]2;Mighty Smoke Terminal\\007'; printf 'MIGHTY_%s\\n' 'CWD_OK'")
            try await terminalSmokeWaitText(terminal, "MIGHTY_CWD_OK")
            try await waitForSmoke(timeout: 5) { terminal.title == "Mighty Smoke Terminal" }
            let expectedDirectory = terminalSmokeCanonicalPath(nested.path)
            guard terminalSmokeRead(cwdFile).map(terminalSmokeCanonicalPath) == expectedDirectory else {
                throw MightyError("cd 후 셸의 현재 폴더가 유지되지 않았습니다.")
            }
            try await waitForSmoke(timeout: 5) { self.terminalSmokeCanonicalPath(terminal.workingDirectory) == expectedDirectory }
            result["persistentDirectory"] = true
            result["titleCallback"] = true
            result["workingDirectoryCallback"] = true
            result["reportedWorkingDirectory"] = terminal.workingDirectory

            stage = "history-and-arrow-keys"
            try terminalSmokeSend(terminal, "printf 'MIGHTY_%s_%s\\n' 'HISTORY' \"$((++MIGHTY_HISTORY))\"")
            try await terminalSmokeWaitText(terminal, "MIGHTY_HISTORY_1")
            try terminalSmokeKey(terminal, .arrowUp)
            try terminalSmokeKey(terminal, .enter)
            try await terminalSmokeWaitText(terminal, "MIGHTY_HISTORY_2")
            guard terminal.view.paste(text: "printf 'MIGHTY_%s\\n' 'DOWN_OK'") else { throw MightyError("진행 중인 입력을 붙여 넣지 못했습니다.") }
            try terminalSmokeKey(terminal, .arrowUp)
            try terminalSmokeKey(terminal, .arrowDown)
            try terminalSmokeKey(terminal, .enter)
            try await terminalSmokeWaitText(terminal, "MIGHTY_DOWN_OK")
            result["historyAndArrows"] = true

            stage = "ansi-and-alternate-screen"
            try terminalSmokeSend(terminal, "printf '\\033[?1049h\\033[2J\\033[H\\033[31mMIGHTY_%s\\033[0m\\n' 'ALT_OK'; /bin/sleep 2; printf '\\033[?1049lMIGHTY_%s\\n' 'ALT_RETURN'")
            try await terminalSmokeWaitText(terminal, "MIGHTY_ALT_OK", timeout: 3)
            let alternate = terminal.diagnosticText()
            guard !alternate.contains("MIGHTY_PTY_OK"), !alternate.contains("\u{1b}[") else { throw MightyError("ANSI 제어 또는 대체 화면 전환이 적용되지 않았습니다.") }
            await terminalSmokeClearSelection(terminal)
            result["alternateScreenshot"] = try captureSmokeWindow(window, filename: "terminal-alternate.png").path
            try await terminalSmokeWaitText(terminal, "MIGHTY_ALT_RETURN")
            guard terminal.diagnosticText().contains("MIGHTY_PTY_OK") else { throw MightyError("대체 화면에서 원래 스크롤 기록으로 돌아오지 못했습니다.") }
            result["ansiAndAlternateScreen"] = true

            stage = "control-c"
            let interruptedPIDFile = folder.appendingPathComponent("interrupted-pid.txt")
            try terminalSmokeSend(terminal, terminalSmokeSleepCommand(pidFile: interruptedPIDFile))
            try await waitForSmoke(timeout: 5) { self.terminalSmokePID(interruptedPIDFile).map(self.terminalSmokeAlive) == true }
            guard let interruptedPID = terminalSmokePID(interruptedPIDFile) else { throw MightyError("중지할 전경 작업 PID가 없습니다.") }
            try terminalSmokeKey(terminal, .c, modifiers: .ctrl)
            try await waitForSmoke(timeout: 5) { !self.terminalSmokeAlive(interruptedPID) }
            try terminalSmokeSend(terminal, "printf 'MIGHTY_%s\\n' 'INTERRUPT_OK'")
            try await terminalSmokeWaitText(terminal, "MIGHTY_INTERRUPT_OK")
            result["controlC"] = true
            result["interruptedPID"] = Int(interruptedPID)

            stage = "resize"
            guard let before = terminal.grid else { throw MightyError("터미널 행·열 콜백이 없습니다.") }
            let width: CGFloat = window.frame.width > 1150 ? 1040 : 1380
            window.setContentSize(NSSize(width: width, height: 730))
            try await waitForSmoke(timeout: 5) { terminal.grid.map { $0.columns != before.columns || $0.rows != before.rows } == true }
            let resizedGridFile = folder.appendingPathComponent("resized-grid.txt")
            try terminalSmokeSend(terminal, "/bin/stty size > \(terminalSmokeQuote(resizedGridFile.path)); printf 'MIGHTY_%s\\n' 'RESIZE_OK'")
            try await terminalSmokeWaitText(terminal, "MIGHTY_RESIZE_OK")
            guard let resized = terminalSmokeGrid(resizedGridFile), let reported = terminal.grid,
                  resized == [Int(reported.rows), Int(reported.columns)], resized != initialGrid else {
                throw MightyError("화면 크기 변경이 PTY 행·열에 전달되지 않았습니다.")
            }
            result["resize"] = true
            result["resizedPTYGrid"] = resized
            if let originalFrame { window.setFrame(originalFrame, display: true) }

            stage = "workspace-and-layout-cache"
            setPaneLayoutPreset("columns")
            try await Task.sleep(for: .milliseconds(350))
            setPaneLayoutPreset("grid")
            try await Task.sleep(for: .milliseconds(350))
            setPaneFocus(true)
            addWorkspace(other)
            try await Task.sleep(for: .milliseconds(350))
            guard localTerminals[id] === terminal, !terminal.disposed, terminalSmokeAlive(shellPID) else {
                throw MightyError("워크스페이스 전환 중 터미널 세션이 종료되었습니다.")
            }
            selectSession(id)
            try await waitForSmoke(timeout: 5) { terminal.view.window != nil }
            let returnedPIDFile = folder.appendingPathComponent("returned-pid.txt")
            let returnedCwdFile = folder.appendingPathComponent("returned-cwd.txt")
            try terminalSmokeSend(terminal, "printf '%s\\n' \"$$\" > \(terminalSmokeQuote(returnedPIDFile.path)); /bin/pwd -P > \(terminalSmokeQuote(returnedCwdFile.path)); printf 'MIGHTY_%s\\n' 'CACHE_OK'")
            try await terminalSmokeWaitText(terminal, "MIGHTY_CACHE_OK")
            guard localTerminals[id] === terminal, terminalSmokePID(returnedPIDFile) == shellPID,
                  terminalSmokeRead(returnedCwdFile).map(terminalSmokeCanonicalPath) == expectedDirectory else {
                throw MightyError("화면에 돌아온 터미널의 셸 PID 또는 현재 폴더가 바뀌었습니다.")
            }
            result["workspaceAndLayoutCache"] = true
            result["transcript"] = String(terminal.diagnosticText().suffix(20_000))
            await terminalSmokeClearSelection(terminal)
            result["screenshot"] = try captureSmokeWindow(window, filename: "terminal-window.png").path

            stage = "exit-and-restart"
            try terminalSmokeSend(terminal, "exit")
            try await waitForSmoke(timeout: 5) { terminal.exited && !self.terminalSmokeAlive(shellPID) }
            await restartTerminal(id)
            try await waitForSmoke(timeout: 10) { self.localTerminals[id]?.ready == true && self.localTerminals[id]?.view.window != nil }
            guard let restarted = localTerminals[id], restarted !== terminal, terminal.disposed else {
                throw MightyError("터미널 다시 시작이 이전 화면과 셸을 교체하지 못했습니다.")
            }
            currentTerminal = restarted
            let restartedPIDFile = folder.appendingPathComponent("restarted-pid.txt")
            try terminalSmokeSend(restarted, "printf '%s\\n' \"$$\" > \(terminalSmokeQuote(restartedPIDFile.path)); printf 'MIGHTY_%s\\n' 'RESTART_OK'")
            try await terminalSmokeWaitText(restarted, "MIGHTY_RESTART_OK")
            guard let restartedPID = terminalSmokePID(restartedPIDFile), restartedPID != shellPID, terminalSmokeAlive(restartedPID) else {
                throw MightyError("다시 시작한 셸의 새 PID를 확인하지 못했습니다.")
            }
            result["exitAndRestart"] = true
            result["restartedPID"] = Int(restartedPID)

            stage = "close-foreground-cleanup"
            let closingPIDFile = folder.appendingPathComponent("closing-pid.txt")
            try terminalSmokeSend(restarted, terminalSmokeSleepCommand(pidFile: closingPIDFile))
            try await waitForSmoke(timeout: 5) { self.terminalSmokePID(closingPIDFile).map(self.terminalSmokeAlive) == true }
            guard let closingPID = terminalSmokePID(closingPIDFile) else { throw MightyError("닫기 검증용 전경 작업 PID가 없습니다.") }
            closeSession(id)
            try await waitForSmoke(timeout: 8) {
                self.localTerminals[id] == nil && !self.snapshot.sessions.contains { $0.id == id }
                    && !self.terminalSmokeAlive(closingPID) && !self.terminalSmokeAlive(restartedPID)
            }
            guard restarted.disposed else { throw MightyError("실행 창을 닫은 후 Ghostty surface가 해제되지 않았습니다.") }
            result["closeForegroundCleanup"] = true
            result["closedForegroundPID"] = Int(closingPID)
            currentTerminal = nil
            try await flush()
            result["passed"] = true
        } catch {
            result["error"] = error.localizedDescription
            result["failedStage"] = stage
            if let currentTerminal {
                result["failureTranscript"] = String(currentTerminal.diagnosticText().suffix(20_000))
                result["failureWorkingDirectory"] = currentTerminal.workingDirectory
                await terminalSmokeClearSelection(currentTerminal)
            }
            if let smokeWindow, let path = try? captureSmokeWindow(smokeWindow, filename: "terminal-failure.png").path { result["failureScreenshot"] = path }
            self.error = "터미널 스모크 테스트 실패: \(error.localizedDescription)"
        }
        if let smokeWindow, let originalFrame { smokeWindow.setFrame(originalFrame, display: true) }
        shutdownTerminals()
        do {
            try FileManager.default.createDirectory(at: dataDirectory, withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
            try data.write(to: dataDirectory.appendingPathComponent("terminal-smoke-result.json"), options: .atomic)
        } catch {
            result["passed"] = false
            self.error = "터미널 스모크 결과 저장 실패: \(error.localizedDescription)"
        }
        if arguments.contains("--smoke-exit") {
            await shutdown()
            Darwin.exit(result["passed"] as? Bool == true ? 0 : 1)
        }
    }

    private func terminalSmokeSend(_ terminal: LocalTerminalSession, _ command: String) throws {
        guard terminal.ready, !terminal.disposed, !terminal.exited, terminal.view.paste(text: command) else { throw MightyError("Ghostty 터미널에 검증 명령을 입력하지 못했습니다.") }
        try terminalSmokeKey(terminal, .enter)
    }

    private func terminalSmokeKey(_ terminal: LocalTerminalSession, _ key: TerminalKey, modifiers: TerminalInputModifiers = []) throws {
        guard terminal.view.sendKey(key, modifiers: modifiers) else { throw MightyError("Ghostty 키 입력이 거부되었습니다: \(key)") }
    }

    private func terminalSmokeWaitText(_ terminal: LocalTerminalSession, _ marker: String, timeout: TimeInterval = 5) async throws {
        // Markers are assembled by printf, so the echoed command cannot satisfy this check.
        try await waitForSmoke(timeout: timeout) { terminal.diagnosticText().contains(marker) }
    }

    private func terminalSmokeClearSelection(_ terminal: LocalTerminalSession) async {
        // This package exposes select_all but no deselect action. A single local
        // click clears diagnostic highlighting without touching the pasteboard.
        guard let window = terminal.view.window else { return }
        let point = terminal.view.convert(NSPoint(x: 8, y: 8), to: nil)
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            if let event = NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                             windowNumber: window.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: type == .leftMouseDown ? 1 : 0) {
                if type == .leftMouseDown { terminal.view.mouseDown(with: event) } else { terminal.view.mouseUp(with: event) }
            }
        }
        try? await Task.sleep(for: .milliseconds(150))
    }

    private func terminalSmokeCanonicalPath(_ path: String) -> String {
        let clean = path.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let resolved = clean.withCString({ Darwin.realpath($0, nil) }) else { return clean }
        defer { Darwin.free(resolved) }
        return String(cString: resolved)
    }

    private func terminalSmokeQuote(_ value: String) -> String { "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'" }

    private func terminalSmokeSleepCommand(pidFile: URL) -> String {
        let script = "printf '%s\\n' \"$$\" > \(terminalSmokeQuote(pidFile.path)); exec /bin/sleep 30"
        return "/bin/sh -c " + terminalSmokeQuote(script)
    }

    private func terminalSmokeRead(_ file: URL) -> String? { try? String(contentsOf: file, encoding: .utf8) }
    private func terminalSmokePID(_ file: URL) -> pid_t? {
        guard let value = terminalSmokeRead(file)?.trimmingCharacters(in: .whitespacesAndNewlines), let pid = pid_t(value), pid > 1 else { return nil }
        return pid
    }
    private func terminalSmokeAlive(_ pid: pid_t) -> Bool { Darwin.kill(pid, 0) == 0 || errno == EPERM }
    private func terminalSmokeGrid(_ file: URL) -> [Int]? {
        guard let value = terminalSmokeRead(file) else { return nil }
        let values = value.split(whereSeparator: { $0.isWhitespace }).compactMap { Int($0) }
        return values.count == 2 && values.allSatisfy { $0 > 0 } ? values : nil
    }
}
