import SwiftUI

/// Keeps animation inside tiny decoration subtrees. Transcript contents and
/// graph coordinates do not receive frame-by-frame state changes.
enum MightyGraphActivityStyle {
    static func isActive(_ status: String) -> Bool {
        ["running", "starting", "queued"].contains(status)
    }

    static func phase(at date: Date, reducedMotion: Bool) -> Double {
        guard !reducedMotion else { return 0 }
        return date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.6) / 1.6
    }

    static func barHeight(_ index: Int, phase: Double) -> CGFloat {
        4 + 8 * CGFloat((sin((phase + Double(index) / 5) * 2 * .pi) + 1) / 2)
    }
}

struct MightyGraphActivityIndicator: View {
    let status: String
    let tint: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Group {
            if MightyGraphActivityStyle.isActive(status) {
                if reduceMotion {
                    Image(systemName: "bolt.fill").font(.system(size: 10)).foregroundStyle(tint)
                        .frame(width: 18, height: 14)
                } else {
                    TimelineView(.animation(minimumInterval: 1.0 / 24)) { context in
                        let phase = MightyGraphActivityStyle.phase(at: context.date, reducedMotion: false)
                        HStack(alignment: .center, spacing: 2) {
                            ForEach(0..<4) { index in
                                Capsule().fill(tint)
                                    .frame(width: 3, height: MightyGraphActivityStyle.barHeight(index, phase: phase))
                            }
                        }
                        .frame(width: 18, height: 14)
                    }
                }
            } else if status == "waiting" {
                Image(systemName: "pause.circle").font(.system(size: 12)).foregroundStyle(.orange)
                    .frame(width: 18, height: 14)
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true) // The adjacent status text carries the state.
    }
}

struct MightyGraphActivityOutline: View {
    let status: String
    let tint: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Group {
            if MightyGraphActivityStyle.isActive(status) {
                if reduceMotion {
                    RoundedRectangle(cornerRadius: 12).inset(by: 1)
                        .stroke(tint.opacity(0.8), lineWidth: 2)
                } else {
                    TimelineView(.animation(minimumInterval: 1.0 / 24)) { context in
                        let phase = MightyGraphActivityStyle.phase(at: context.date, reducedMotion: false)
                        RoundedRectangle(cornerRadius: 12).inset(by: 1)
                            .stroke(tint.opacity(0.9), style: StrokeStyle(lineWidth: 2, lineCap: .round, dash: [9, 7], dashPhase: -CGFloat(phase * 16)))
                    }
                }
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}
