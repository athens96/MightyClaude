import SwiftUI

/// Uses the workspace's own window so startup never changes key-window or input ownership.
struct LaunchSplashView: View {
    var body: some View {
        ZStack {
            Palette.canvas
                .overlay {
                    RadialGradient(
                        colors: [Palette.accent.opacity(0.09), .clear],
                        center: .center,
                        startRadius: 30,
                        endRadius: 360
                    )
                }
                .ignoresSafeArea()

            VStack(spacing: 0) {
                brandIcon
                    .frame(width: 104, height: 104)
                    .shadow(color: Palette.accent.opacity(0.10), radius: 24, y: 8)
                    .padding(.bottom, 24)

                Text("Mighty Claude")
                    .font(.system(size: 30, weight: .semibold, design: .rounded))
                    .tracking(-0.6)
                    .foregroundStyle(.primary)
                    .padding(.bottom, 10)

                Text("작업 공간을 준비하고 있어요")
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)

                ProgressView()
                    .progressViewStyle(.circular)
                    .controlSize(.small)
                    .padding(.top, 28)
                    .accessibilityLabel("작업 공간 불러오는 중")
            }
            .padding(48)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityIdentifier("launch-splash")
    }

    private var brandIcon: some View {
        Group {
            if let image = BrandAssets.icon {
                Image(nsImage: image)
                    .resizable()
                    .interpolation(.high)
                    .scaledToFit()
            } else {
                Image(systemName: "sparkles")
                    .font(.system(size: 52, weight: .light))
                    .foregroundStyle(Palette.accent)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Palette.accent.opacity(0.08), in: RoundedRectangle(cornerRadius: 24))
            }
        }
        .accessibilityHidden(true)
    }
}
