import AppKit
import Combine
import MightyCore
import SwiftUI

/// Plays `ScreenShareReferenceScene` in a window of its own on the main display:
/// a document scrolling beside a terminal being typed into, then a screen that
/// does not change. Nothing outside this window is scrolled, typed into or
/// touched, and each phase change is announced to connected phones so their
/// measurement export can mark it.
@MainActor
enum ScreenShareReferenceSceneWindow {
    private static var window: NSWindow?
    private static var player: ReferenceScenePlayer?
    private static var closeObserver: NSObjectProtocol?

    static func play(announce: @escaping @MainActor (ScreenShareReferenceScene.Phase) -> Void) {
        stop()
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let player = ReferenceScenePlayer(announce: announce)
        let frame = screen.visibleFrame.insetBy(dx: screen.visibleFrame.width * 0.08, dy: screen.visibleFrame.height * 0.08)
        let window = NSWindow(contentRect: frame, styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = L("screenShare.scene.windowTitle")
        window.isReleasedWhenClosed = false
        // Above ordinary windows, so the scene is what the phone sees while it plays.
        window.level = .floating
        window.contentView = NSHostingView(rootView: ReferenceSceneView(player: player, onStop: { stop() }))
        window.setFrame(frame, display: true)
        window.makeKeyAndOrderFront(nil)
        // The close box ends the scene too. Only this window's own close tears
        // down: a replaced scene was torn down before its window closed.
        closeObserver = NotificationCenter.default.addObserver(
            forName: NSWindow.willCloseNotification, object: window, queue: .main
        ) { [weak window] _ in
            MainActor.assumeIsolated {
                guard let window, window === Self.window else { return }
                teardown()
            }
        }
        self.window = window
        self.player = player
        player.start()
    }

    static func stop() {
        guard let window else { return }
        teardown()
        window.close()
    }

    /// Stops the clock (which announces `done` if the scene had not ended) and
    /// forgets the window.
    private static func teardown() {
        player?.stop()
        player = nil
        if let closeObserver { NotificationCenter.default.removeObserver(closeObserver) }
        closeObserver = nil
        window = nil
    }
}

/// The scene's clock. Published values change only when what is drawn has to:
/// a phase change, and the countdown's whole seconds while something moves.
@MainActor
final class ReferenceScenePlayer: ObservableObject {
    @Published private(set) var moment = ScreenShareReferenceScene.moment(at: 0)
    private(set) var startedAt = Date()
    private var timer: Timer?
    private let announce: @MainActor (ScreenShareReferenceScene.Phase) -> Void

    init(announce: @escaping @MainActor (ScreenShareReferenceScene.Phase) -> Void) { self.announce = announce }

    var stillEndsAt: Date { startedAt.addingTimeInterval(ScreenShareReferenceScene.start(of: .done)) }

    func start() {
        startedAt = Date()
        moment = ScreenShareReferenceScene.moment(at: 0)
        announce(moment.phase)
        let timer = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func stop() {
        timer?.invalidate(); timer = nil
        if moment.phase != .done {
            moment = ScreenShareReferenceScene.Moment(phase: .done, secondsLeft: 0, motionElapsed: moment.motionElapsed)
            announce(.done)
        }
    }

    private func tick() {
        let next = ScreenShareReferenceScene.moment(at: Date().timeIntervalSince(startedAt))
        if next.phase != moment.phase {
            moment = next
            announce(next.phase)
            if next.phase == .done { timer?.invalidate(); timer = nil }
            return
        }
        // The still phase must not redraw a single pixel, so its countdown does
        // not tick: the view shows when it ends instead.
        if next.phase != .still, next.secondsLeft != moment.secondsLeft { moment = next }
    }
}

private struct ReferenceSceneView: View {
    @ObservedObject var player: ReferenceScenePlayer
    let onStop: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Text(label).font(.system(size: 15, weight: .semibold, design: .rounded)).monospacedDigit()
                    .accessibilityIdentifier("screen-share-scene-countdown")
                BetaBadge()
                Spacer()
                Button(L("screenShare.scene.stopButton"), action: onStop)
                    .accessibilityIdentifier("screen-share-scene-stop")
            }
            .padding(.horizontal, 16).padding(.vertical, 10)
            Divider()
            if player.moment.phase == .motion {
                // Redrawn every display frame while things move, and only then.
                TimelineView(.animation) { context in
                    let elapsed = ScreenShareReferenceScene.moment(at: context.date.timeIntervalSince(player.startedAt)).motionElapsed
                    stage(motionElapsed: elapsed)
                }
            } else {
                stage(motionElapsed: player.moment.motionElapsed)
            }
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var label: String {
        let moment = player.moment
        switch moment.phase {
        case .preroll: return L("screenShare.scene.prerollLabel", ["seconds": String(moment.secondsLeft)])
        case .motion: return L("screenShare.scene.motionLabel", ["seconds": String(moment.secondsLeft)])
        case .still: return L("screenShare.scene.stillLabel", ["time": Self.clock(player.stillEndsAt)])
        case .done: return L("screenShare.scene.doneLabel")
        }
    }

    private func stage(motionElapsed: TimeInterval) -> some View {
        HStack(spacing: 0) {
            ReferenceDocument(motionElapsed: motionElapsed)
            Divider()
            ReferenceTerminal(motionElapsed: motionElapsed)
        }
    }

    private static func clock(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return formatter.string(from: date)
    }
}

/// A long page of numbered sections, moved by the scene's clock rather than a
/// scroll view, so the motion is the same on every run.
private struct ReferenceDocument: View {
    let motionElapsed: TimeInterval
    static let sections = 40
    static let sectionHeight: CGFloat = 150

    var body: some View {
        GeometryReader { geometry in
            let contentHeight = Double(Self.sectionHeight) * Double(Self.sections)
            let offset = ScreenShareReferenceScene.scrollOffset(motionElapsed: motionElapsed, contentHeight: contentHeight)
            VStack(alignment: .leading, spacing: 0) {
                // Two copies end to end: the wrap from the last section to the
                // first never shows an empty gap.
                ForEach(0..<(Self.sections * 2), id: \.self) { index in
                    section(index % Self.sections + 1)
                }
            }
            .padding(.horizontal, 28)
            .frame(width: geometry.size.width, alignment: .leading)
            .offset(y: -CGFloat(offset))
        }
        .clipped()
        .background(Color.white)
    }

    private func section(_ number: Int) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(L("screenShare.scene.documentHeading", ["number": String(number)]))
                .font(.system(size: 20, weight: .bold)).foregroundStyle(Color.black)
            Text(L("screenShare.scene.documentParagraph", ["number": String(number)]))
                .font(.system(size: 14)).foregroundStyle(Color(white: 0.15)).lineLimit(4)
        }
        .frame(height: Self.sectionHeight, alignment: .topLeading)
    }
}

/// Terminal-like typing: a fixed script revealed at a fixed rate, last lines
/// kept on screen. The script is synthetic tool output, the same every run.
private struct ReferenceTerminal: View {
    let motionElapsed: TimeInterval
    /// The script as characters, so a frame slices it without walking it.
    static let script: [Character] = Array((1...400).map { step in
        "$ swift build --target Module\(step)\n[\(step)/400] Compiling Module\(step) Source\(step % 17).swift\n"
    }.joined())
    /// Where each line starts in `script`, ascending.
    static let lineStarts: [Int] = [0] + script.indices.filter { script[$0] == "\n" }.map { $0 + 1 }
    static let visibleLines = 28

    /// The last `visibleLines` lines of the first `count` characters — what
    /// splitting the typed prefix into lines would give, at the cost of a
    /// binary search and one short slice per frame.
    static func visibleText(typed count: Int) -> String {
        // The line the cursor is on: the last start at or before `count`.
        var low = 0, high = lineStarts.count
        while low < high {
            let middle = (low + high) / 2
            if lineStarts[middle] <= count { low = middle + 1 } else { high = middle }
        }
        let first = max(0, low - 1 - (visibleLines - 1))
        return String(script[lineStarts[first]..<count])
    }

    var body: some View {
        let count = ScreenShareReferenceScene.typedCount(motionElapsed: motionElapsed) % Self.script.count
        Text(Self.visibleText(typed: count) + "▌")
            .font(.system(size: 13, design: .monospaced)).foregroundStyle(Color.green)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .padding(14)
            .background(Color.black)
    }
}
