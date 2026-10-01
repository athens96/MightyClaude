import MightyCore
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
                Image(systemName: "pause.circle").font(.system(size: 12)).foregroundStyle(Palette.waitText)
                    .frame(width: 18, height: 14)
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true) // The adjacent status text carries the state.
    }
}

/// Concept D's edge on a block that is in motion: a 2pt run-blue border with the soft
/// run halo outside it while it runs, the amber one while it waits on the user. Solid,
/// so Reduce Motion changes nothing here; the bars beside the status carry the motion.
struct MightyGraphActivityOutline: View {
    let tone: DesignTone
    var cornerRadius: CGFloat = 12

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius)
        Group {
            switch tone {
            case .run:
                shape.inset(by: 1).stroke(Palette.run, lineWidth: 2)
                    .background { shape.inset(by: -2).stroke(Palette.runSoft, lineWidth: 4) }
            case .wait:
                shape.inset(by: 1).stroke(Palette.wait, lineWidth: 2)
            default:
                EmptyView()
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

/// A block's status as a pill: the tone's soft tint under its text-safe ink.
struct MightyStatusPill: View {
    let text: String
    let tone: DesignTone
    var height: CGFloat = 18

    var body: some View {
        Text(text)
            .font(.system(size: 10, weight: .bold)).lineLimit(1)
            .foregroundStyle(Palette.text(tone))
            .padding(.horizontal, 7).frame(height: height)
            .background(Palette.soft(tone), in: Capsule())
            .fixedSize()
    }
}

extension View {
    /// The white D card every graph block sits on: the card fill and its hairline edge.
    func mightyBlockCard(cornerRadius: CGFloat = 12) -> some View {
        background(Palette.panel, in: RoundedRectangle(cornerRadius: cornerRadius))
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius))
            .overlay { RoundedRectangle(cornerRadius: cornerRadius).strokeBorder(Palette.border, lineWidth: 1).allowsHitTesting(false) }
    }
}
