import SwiftUI

struct RenameTarget: Identifiable {
    enum Kind: String { case workspace, session }
    let kind: Kind
    let targetID: String
    let initialName: String
    var id: String { kind.rawValue + ":" + targetID }
}

struct RenameSheet: View {
    @EnvironmentObject private var store: AppStore
    let target: RenameTarget
    @ViewState private var name: String
    @ViewState private var saveError: String?
    @FocusState private var nameFocused: Bool

    init(target: RenameTarget) {
        self.target = target
        _name = ViewState(initialValue: target.initialName)
    }

    private var heading: String { target.kind == .workspace ? "워크스페이스 이름 변경" : "실행 창 이름 변경" }
    private var trimmedName: String { name.trimmingCharacters(in: .whitespacesAndNewlines) }
    private var hasControlCharacters: Bool { trimmedName.unicodeScalars.contains { $0.properties.generalCategory == .control } }
    private var validName: Bool { !trimmedName.isEmpty && trimmedName.count <= 120 && !hasControlCharacters }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(heading).font(.headline)
            TextField("이름", text: $name)
                .textFieldStyle(.roundedBorder)
                .focused($nameFocused)
                .onSubmit { save() }
                .accessibilityIdentifier("rename-name-field")
            Text(target.kind == .workspace ? "앱에 표시되는 이름만 바뀌며 폴더 이름과 경로는 유지됩니다." : "사이드바와 탭에 같은 이름이 표시됩니다.")
                .font(.caption).foregroundStyle(.secondary)
            if trimmedName.count > 120 {
                Text("이름은 120자 이내로 입력하세요.").font(.caption).foregroundStyle(.red)
            }
            if hasControlCharacters {
                Text("이름은 줄바꿈 없이 입력하세요.").font(.caption).foregroundStyle(.red)
            }
            if let saveError { Text(saveError).font(.caption).foregroundStyle(.red) }
            HStack {
                Spacer()
                Button("취소") { store.renameTarget = nil }.keyboardShortcut(.cancelAction)
                Button("저장") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!validName)
                    .accessibilityIdentifier("rename-save")
            }
        }
        .padding(22).frame(width: 360)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("rename-sheet")
        .onAppear { nameFocused = true }
    }

    private func save() {
        guard validName, store.renameTarget?.id == target.id else { return }
        let saved = target.kind == .workspace
            ? store.renameWorkspace(target.targetID, to: trimmedName)
            : store.renameSession(target.targetID, to: trimmedName)
        if saved { store.renameTarget = nil }
        else { saveError = "대상을 찾을 수 없습니다. 창을 닫고 다시 시도하세요." }
    }
}
