import MightyCore
import SwiftUI

/// Completion list shown above the composer while the draft is `/…`.
/// Keyboard moves the highlight; Enter/Tab or a click inserts the command.
struct SlashCommandPalette: View {
    let commands: [SlashCommand]
    let selectedIndex: Int
    let onSelect: (SlashCommand) -> Void
    let onHover: (Int) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(commands.enumerated()), id: \.element.id) { index, command in
                            row(command, highlighted: index == selectedIndex)
                                .id(command.id)
                                .contentShape(Rectangle())
                                .onTapGesture { onSelect(command) }
                                .onHover { inside in if inside { onHover(index) } }
                        }
                    }
                }
                .frame(maxHeight: min(CGFloat(commands.count), 8) * 40)
                .onChange(of: selectedIndex) { _, index in
                    guard commands.indices.contains(index) else { return }
                    proxy.scrollTo(commands[index].id, anchor: .center)
                }
            }
            Divider()
            HStack(spacing: 10) {
                Text("↑↓ 이동").font(.system(size: 10)).foregroundStyle(.secondary)
                Text("Enter · Tab 선택").font(.system(size: 10)).foregroundStyle(.secondary)
                Text("Esc 닫기").font(.system(size: 10)).foregroundStyle(.secondary)
                Spacer()
                Text("\(commands.count)개").font(.system(size: 10)).foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 12).padding(.vertical, 5)
        }
        .background(Palette.panel, in: RoundedRectangle(cornerRadius: 10))
        .overlay { RoundedRectangle(cornerRadius: 10).stroke(Palette.border, lineWidth: 1) }
        .accessibilityElement(children: .contain).accessibilityIdentifier("slash-palette")
    }

    private func row(_ command: SlashCommand, highlighted: Bool) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Text("/" + command.invocation).font(.system(size: 12, weight: .semibold, design: .monospaced)).lineLimit(1)
                .foregroundStyle(highlighted ? Palette.canvas : .primary)
            VStack(alignment: .leading, spacing: 1) {
                Text(command.description.isEmpty ? "설명 없음" : command.description).font(.system(size: 11)).lineLimit(1)
                    .foregroundStyle(highlighted ? Palette.canvas.opacity(0.9) : .secondary)
                Text(command.source).font(.system(size: 9, weight: .medium))
                    .foregroundStyle(highlighted ? Palette.canvas.opacity(0.75) : Color.secondary.opacity(0.7))
            }
            Spacer(minLength: 0)
            if command.action != nil {
                Image(systemName: "arrow.turn.down.left").font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(highlighted ? Palette.canvas.opacity(0.75) : Color.secondary.opacity(0.7))
                    .help("앱에서 바로 실행됩니다")
            } else if command.argument != nil {
                Image(systemName: "chevron.right").font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(highlighted ? Palette.canvas.opacity(0.75) : Color.secondary.opacity(0.7))
                    .help("이어서 선택합니다")
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 6)
        .background(highlighted ? Palette.accent : Color.clear)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(highlighted ? .isSelected : [])
        .accessibilityIdentifier("slash-command-" + command.invocation)
    }
}

/// Keys the composer forwards to an open palette before its own Return handling.
enum ComposerNavigationKey { case up, down, select, dismiss }
