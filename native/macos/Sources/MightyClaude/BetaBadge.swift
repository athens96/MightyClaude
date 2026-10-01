import SwiftUI
import MightyCore

/// The small capsule drawn after a beta provider's name (`ProviderOptions.isBeta`).
/// It sits beside the name and is never part of a title string.
struct BetaBadge: View {
    var body: some View {
        Text(verbatim: L("badge.beta"))
            .font(.system(size: 9, weight: .medium))
            .foregroundStyle(Palette.stopText)
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(Palette.stopSoft, in: Capsule())
            .fixedSize()
            .accessibilityLabel(L("badge.betaAccessibility"))
    }
}
