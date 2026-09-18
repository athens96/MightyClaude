import AppKit
import SwiftUI
import MightyCore

/// The composer's Paperthin chrome: the 2×2 map (how many artifacts, over how
/// much time) picks a domain, the domain lists its skills, and coil shows the
/// iteration casebook on disk. Choosing a skill sends `/skill <what was typed>`.
struct PaperthinPanel: View {
    @EnvironmentObject private var store: AppStore
    let session: RunSession
    let running: Bool
    @Binding var domain: PaperthinDomain?
    var onPrepare: () -> Void = {}

    private var casebook: PaperthinCasebook? { store.paperthinCasebooks[session.workspaceId] }
    private var loaded: Bool { store.paperthinLoaded.contains(session.workspaceId) }
    private var selected: PaperthinDomain { domain ?? .depth }
    private let columns = [GridItem(.adaptive(minimum: 104, maximum: 170), spacing: 5, alignment: .leading)]

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let (request, questionnaire) = store.ouroborosQuestion(for: session.id) {
                AgentQuestionPanel(sessionId: session.id, request: request, questionnaire: questionnaire, onPrepare: onPrepare)
            } else {
                if store.paperthinInstalled == false { setup }
                map
                Text(selected.question).font(.system(size: 11)).foregroundStyle(.secondary)
                if selected == .coil { casebookRow }
                skills
                Text(running ? "실행 중 · 고른 스킬은 다음 요청으로 대기합니다" : "대상(파일 경로나 지시)을 아래에 적고 스킬을 누르세요. 비워 두면 스킬만 보냅니다.")
                    .font(.system(size: 10)).foregroundStyle(.tertiary)
            }
        }
        .padding(.horizontal, 12).padding(.top, 8)
        .accessibilityElement(children: .contain).accessibilityIdentifier("paperthin-panel-\(session.id)")
        .onAppear { seedDomain() }
        .onChange(of: loaded) { _, _ in seedDomain() }
    }

    /// Chosen once, when the casebook is first known: coil if a cycle is open
    /// in this workspace, otherwise depth. A casebook that appears later does
    /// not move the user off the domain they are working in.
    private func seedDomain() {
        guard domain == nil, loaded else { return }
        domain = casebook == nil ? .depth : .coil
    }

    private var map: some View {
        HStack(spacing: 5) {
            ForEach(PaperthinDomain.allCases, id: \.self) { item in
                Button { domain = item } label: {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(item.title).font(.system(size: 11, weight: .semibold, design: .monospaced))
                        Text(item.axis).font(.system(size: 9)).foregroundStyle(.secondary)
                    }
                    .padding(.horizontal, 9).padding(.vertical, 5).frame(maxWidth: .infinity, alignment: .leading)
                    .background(item == selected ? Palette.accent.opacity(0.16) : Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
                    .overlay { RoundedRectangle(cornerRadius: 7).stroke(item == selected ? Palette.accent.opacity(0.7) : Color.clear) }
                    .contentShape(RoundedRectangle(cornerRadius: 7))
                }
                .buttonStyle(.plain).help(item.question)
                .accessibilityLabel("\(item.title) · \(item.axis)").accessibilityAddTraits(item == selected ? .isSelected : [])
                .accessibilityIdentifier("paperthin-domain-\(item.rawValue)-\(session.id)")
            }
        }
    }

    private var skills: some View {
        let listed = PaperthinCatalog.skills(in: selected)
        let recommended = selected == .coil ? PaperthinCatalog.recommendedCoilSkill(casebook: casebook) : nil
        return ScrollView {
            LazyVGrid(columns: columns, alignment: .leading, spacing: 5) {
                ForEach(listed) { skill in
                    PaperthinSkillChip(skill: skill, recommended: skill.name == recommended, sessionId: session.id) {
                        onPrepare()
                        store.sendPaperthin(session.id, skill: skill.name, text: store.drafts[session.id] ?? "")
                    }
                }
            }
        }
        .frame(maxHeight: Self.gridHeight(count: listed.count)).scrollIndicators(.hidden)
    }

    /// Tall enough for the domain's rows at the narrowest pane (three chips a
    /// row), capped so a long domain scrolls instead of pushing the editor down.
    static func gridHeight(count: Int) -> CGFloat {
        let rows = max(1, (count + 2) / 3)
        return min(92, CGFloat(rows) * 27 + CGFloat(rows - 1) * 5)
    }

    /// Kept out of the view builder: chained conditionals are slow to type-check.
    static func helpText(_ skill: PaperthinSkill) -> String {
        var parts = [skill.summary, "범위: " + skill.scope]
        parts.append(skill.userInvoked ? "사람만 부를 수 있는 스킬" : "모델도 스스로 꺼내 씀")
        if skill.readOnly { parts.append("읽기 전용") }
        return parts.joined(separator: " · ")
    }

    private var casebookRow: some View {
        HStack(spacing: 6) {
            Image(systemName: "folder").font(.system(size: 10)).foregroundStyle(.secondary)
            if let casebook {
                ScrollView(.horizontal) {
                    HStack(spacing: 6) {
                        Text(casebook.name).font(.system(size: 11, weight: .medium, design: .monospaced)).lineLimit(1)
                        Text(casebook.weight).font(.system(size: 9)).foregroundStyle(.secondary).padding(.horizontal, 5).padding(.vertical, 1).background(Palette.subtle, in: Capsule())
                        ForEach(casebook.files.prefix(6), id: \.self) { file in
                            Button(file.replacingOccurrences(of: ".local.md", with: "")) {
                                NSWorkspace.shared.open(URL(fileURLWithPath: casebook.path).appendingPathComponent(file))
                            }
                            .buttonStyle(.plain).font(.system(size: 10, design: .monospaced)).foregroundStyle(Palette.accent).help("열기: " + file)
                        }
                    }
                }
                .scrollIndicators(.hidden)
            } else {
                Text("열린 사이클이 없습니다. re0-plan으로 케이스북을 여세요.").font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                Spacer(minLength: 0)
            }
            Button { store.refreshPaperthin(for: session) } label: { Image(systemName: "arrow.clockwise").font(.system(size: 9)) }.buttonStyle(.plain).help("케이스북 다시 읽기")
        }
        .accessibilityElement(children: .contain).accessibilityIdentifier("paperthin-casebook-\(session.id)")
    }

    private var setup: some View {
        VStack(alignment: .leading, spacing: 5) {
            Label("Paperthin 스킬이 설치되어 있지 않습니다", systemImage: "shippingbox").font(.system(size: 11, weight: .medium))
            Text("터미널 실행 창을 열어 다음 명령을 실행합니다(Claude Code의 전역 스킬 폴더에 연결): " + PaperthinCatalog.installCommand)
                .font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 6) {
                Button("스킬 설치") { store.startPaperthinInstall(from: session) }.controlSize(.small)
                Button("다시 확인") { store.refreshPaperthin(for: session) }.controlSize(.small)
            }
        }
    }
}

/// One skill button. Its own view keeps the grid's builder cheap to type-check.
private struct PaperthinSkillChip: View {
    let skill: PaperthinSkill
    let recommended: Bool
    let sessionId: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                Text(skill.emoji).font(.system(size: 11))
                Text(skill.name).font(.system(size: 11, weight: recommended ? .semibold : .regular, design: .monospaced)).lineLimit(1)
                Spacer(minLength: 0)
                if skill.userInvoked { Image(systemName: "person.fill").font(.system(size: 8)).foregroundStyle(.secondary) }
                if skill.readOnly { Image(systemName: "eye").font(.system(size: 8)).foregroundStyle(.secondary) }
            }
            .padding(.horizontal, 8).padding(.vertical, 5)
            .background(recommended ? Palette.accent.opacity(0.18) : Palette.subtle, in: RoundedRectangle(cornerRadius: 6))
            .contentShape(RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
        .help(PaperthinPanel.helpText(skill))
        .accessibilityLabel(skill.name).accessibilityHint(skill.summary)
        .accessibilityIdentifier("paperthin-skill-\(skill.name)-\(sessionId)")
    }
}
