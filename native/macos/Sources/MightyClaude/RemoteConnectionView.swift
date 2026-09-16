import SwiftUI
import AppKit
import MightyCore

struct RemoteConnectionView: View {
    var onClose: (() -> Void)? = nil
    @EnvironmentObject private var store: AppStore
    @Environment(\.dismiss) private var dismiss
    @ViewState private var selectedWorkspaces = Set<String>()
    @ViewState private var port = "43137"
    @ViewState private var connectionName = ""
    @ViewState private var address = ""
    @ViewState private var token = ""
    @ViewState private var revealKey = false
    @ViewState private var localError: String?
    @ViewState private var copiedKey = false

    var body: some View {
        VStack(spacing: 0) {
            SheetHeading(title: "원격 연결", subtitle: "Tailscale로 연결된 컴퓨터에서 프로젝트를 실행합니다.", systemImage: "network") { close() }
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    tailscaleStatus
                    HStack(alignment: .top, spacing: 18) {
                        sharingSection.frame(maxWidth: .infinity, alignment: .topLeading)
                        connectingSection.frame(maxWidth: .infinity, alignment: .topLeading)
                    }
                    if let message = localError ?? store.remoteError {
                        Label(message, systemImage: "exclamationmark.triangle").font(.system(size: 12)).foregroundStyle(.orange).textSelection(.enabled)
                            .padding(13).frame(maxWidth: .infinity, alignment: .leading).background(Color.orange.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
                    }
                    if !store.remoteState.connections.isEmpty {
                        VStack(alignment: .leading, spacing: 12) {
                            Text("연결한 컴퓨터").font(.system(size: 14, weight: .semibold))
                            ForEach(store.remoteState.connections) { connection in connectionCard(connection) }
                        }
                    }
                }.padding(22)
            }
            Divider()
            HStack {
                Text("CLI 설치와 로그인은 실행할 컴퓨터에서 진행하세요. 로그인 정보는 전송하지 않습니다.")
                    .font(.system(size: 10)).foregroundStyle(.secondary)
                Spacer()
                Button(onClose == nil ? "닫기" : "설정으로 돌아가기") { close() }.keyboardShortcut(.cancelAction)
            }.padding(18)
        }
        .frame(width: 800, height: 735)
        .task {
            await store.reloadRemoteState()
            selectedWorkspaces = Set(store.remoteState.host.workspaceIds)
            if let active = store.activeWorkspace, active.remote == nil, selectedWorkspaces.isEmpty { selectedWorkspaces.insert(active.id) }
            port = String(store.remoteState.host.port ?? 43137)
        }
        .onChange(of: store.remoteState.connections) { _, connections in
            if connections.contains(where: { $0.status == "connected" && $0.address == address.trimmingCharacters(in: .whitespacesAndNewlines) }) { token = "" }
        }
    }

    private func close() {
        if let onClose { onClose() }
        else { dismiss() }
    }

    private var tailscaleStatus: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: store.remoteState.tailscale.available ? "checkmark.circle.fill" : "info.circle").foregroundStyle(store.remoteState.tailscale.available ? .green : .secondary)
            VStack(alignment: .leading, spacing: 5) {
                Text(store.remoteState.tailscale.available ? "Tailscale 연결됨" : "Tailscale 연결 확인").font(.system(size: 12, weight: .medium))
                Text(store.remoteState.tailscale.detail).font(.system(size: 11)).foregroundStyle(.secondary).textSelection(.enabled)
                if !store.remoteState.tailscale.addresses.isEmpty { Text(store.remoteState.tailscale.addresses.joined(separator: " · ")).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled) }
            }
            Spacer()
            Button { Task { await store.reloadRemoteState() } } label: { Image(systemName: "arrow.clockwise") }.buttonStyle(.plain).help("Tailscale 상태 새로고침").accessibilityLabel("Tailscale 상태 새로고침")
        }.padding(14).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 9))
    }

    private var sharingSection: some View {
        VStack(alignment: .leading, spacing: 13) {
            Label("이 컴퓨터 공유", systemImage: "desktopcomputer").font(.system(size: 14, weight: .semibold))
            Text("연결 키를 가진 기기가 선택한 폴더에서 명령을 실행할 수 있습니다.")
                .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            if store.remoteState.host.enabled { activeSharing }
            else {
                let workspaces = store.snapshot.workspaces.filter { $0.remote == nil }
                if workspaces.isEmpty {
                    Text("공유할 로컬 워크스페이스가 없습니다. 먼저 프로젝트 폴더를 열어주세요.").font(.system(size: 11)).foregroundStyle(.secondary).padding(.vertical, 8)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(workspaces) { workspace in
                            Toggle(isOn: Binding(get: { selectedWorkspaces.contains(workspace.id) }, set: { if $0 { selectedWorkspaces.insert(workspace.id) } else { selectedWorkspaces.remove(workspace.id) } })) {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(workspace.name).font(.system(size: 12))
                                    Text(workspace.path).font(.system(size: 9, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                                }
                            }.toggleStyle(.checkbox).accessibilityLabel("\(workspace.name) 공유")
                        }
                    }.padding(11).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
                }
                HStack {
                    Text("포트").font(.system(size: 11)).foregroundStyle(.secondary)
                    Spacer()
                    TextField("포트", text: $port).textFieldStyle(.roundedBorder).frame(width: 100).accessibilityLabel("공유 포트")
                }
                Button("공유 시작") { startSharing() }.buttonStyle(.borderedProminent).disabled(store.remoteBusy || selectedWorkspaces.isEmpty)
                Text("앱을 다시 시작하면 공유는 꺼집니다.").font(.system(size: 10)).foregroundStyle(.secondary)
            }
        }
        .padding(16).background(Palette.panel, in: RoundedRectangle(cornerRadius: 10)).overlay { RoundedRectangle(cornerRadius: 10).stroke(Palette.border) }
    }

    private var activeSharing: some View {
        VStack(alignment: .leading, spacing: 10) {
            Label("공유 중 · \(store.remoteState.host.activeRuns)개 작업 실행 중", systemImage: "dot.radiowaves.left.and.right").font(.system(size: 11)).foregroundStyle(.green)
            if let address = store.remoteState.host.address {
                Text("연결 주소").font(.system(size: 10)).foregroundStyle(.secondary)
                HStack {
                    Text(address).font(.system(size: 11, design: .monospaced)).textSelection(.enabled).lineLimit(2)
                    Spacer(minLength: 0)
                    Button { copy(address) } label: { Image(systemName: "doc.on.doc") }.buttonStyle(.plain).accessibilityLabel("연결 주소 복사")
                }
            }
            if let key = store.remoteState.host.token {
                Text("연결 키").font(.system(size: 10)).foregroundStyle(.secondary)
                HStack {
                    Text(revealKey ? key : "••••••••••••••••••••••••").font(.system(size: 10, design: .monospaced)).lineLimit(2).textSelection(.enabled)
                    Spacer(minLength: 0)
                    Button { revealKey.toggle() } label: { Image(systemName: revealKey ? "eye.slash" : "eye") }.buttonStyle(.plain).accessibilityLabel(revealKey ? "연결 키 숨기기" : "연결 키 보기")
                    Button { copy(key); copiedKey = true } label: { Image(systemName: copiedKey ? "checkmark" : "doc.on.doc") }.buttonStyle(.plain).accessibilityLabel("연결 키 복사")
                }
            }
            if let detail = store.remoteState.host.detail { Text(detail).font(.system(size: 10)).foregroundStyle(.secondary) }
            Button("공유 중지", role: .destructive) { store.stopSharing() }.disabled(store.remoteBusy)
        }.padding(.top, 4)
    }

    private var connectingSection: some View {
        VStack(alignment: .leading, spacing: 13) {
            Label("다른 컴퓨터 연결", systemImage: "network").font(.system(size: 14, weight: .semibold))
            Text("상대 컴퓨터에서 공유를 시작한 뒤 주소와 연결 키를 입력하세요.").font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            VStack(alignment: .leading, spacing: 5) {
                Text("컴퓨터 이름").font(.system(size: 10)).foregroundStyle(.secondary)
                TextField("예: 작업용 Mac", text: $connectionName).textFieldStyle(.roundedBorder).accessibilityLabel("컴퓨터 이름")
            }
            VStack(alignment: .leading, spacing: 5) {
                Text("Tailscale 주소").font(.system(size: 10)).foregroundStyle(.secondary)
                TextField("http://100.x.x.x:43137", text: $address).textFieldStyle(.roundedBorder).accessibilityLabel("Tailscale 주소")
            }
            VStack(alignment: .leading, spacing: 5) {
                Text("연결 키").font(.system(size: 10)).foregroundStyle(.secondary)
                SecureField("공유 컴퓨터의 연결 키", text: $token).textFieldStyle(.roundedBorder).accessibilityLabel("연결 키")
            }
            HStack {
                Button("연결") { connect() }.buttonStyle(.borderedProminent).disabled(store.remoteBusy || address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || token.isEmpty)
                if store.remoteBusy { ProgressView().controlSize(.small).scaleEffect(0.8) }
            }
            Text("연결 키는 이 Mac의 키체인에 보관됩니다.").font(.system(size: 10)).foregroundStyle(.secondary)
        }
        .padding(16).background(Palette.panel, in: RoundedRectangle(cornerRadius: 10)).overlay { RoundedRectangle(cornerRadius: 10).stroke(Palette.border) }
    }

    private func connectionCard(_ connection: RemoteConnectionInfo) -> some View {
        VStack(alignment: .leading, spacing: 11) {
            HStack(spacing: 9) {
                Image(systemName: "desktopcomputer").foregroundStyle(Palette.accent)
                VStack(alignment: .leading, spacing: 3) {
                    Text(connection.name).font(.system(size: 12, weight: .semibold))
                    Text(connection.address).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).textSelection(.enabled)
                }
                Spacer()
                Text(connection.status == "connected" ? "연결됨" : connection.status == "error" ? "연결 오류" : "연결 끊김").font(.system(size: 10)).foregroundStyle(connection.status == "connected" ? .green : .secondary)
                Button("새로고침") { store.refreshConnection(connection.id) }.disabled(store.remoteBusy)
                Button("연결 해제") { store.disconnectRemote(connection.id) }.disabled(store.remoteBusy || connection.status != "connected")
            }
            if let detail = connection.detail { Text(detail).font(.system(size: 11)).foregroundStyle(.secondary).textSelection(.enabled) }
            if let providers = connection.runtime?.providers {
                HStack(spacing: 14) {
                    ForEach(providers) { provider in
                        Label("\(provider.name) \(provider.available ? "준비됨" : "설정 필요")", systemImage: provider.available ? "checkmark.circle" : "exclamationmark.circle")
                            .font(.system(size: 10)).foregroundStyle(provider.available ? Color.secondary : Color.orange).help(provider.detail)
                    }
                }
            }
            if connection.status == "connected" {
                ForEach(connection.workspaces) { workspace in
                    HStack(spacing: 9) {
                        Image(systemName: "folder").font(.system(size: 12)).foregroundStyle(.secondary)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(workspace.name).font(.system(size: 12))
                            Text(workspace.path).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                        }
                        Spacer()
                        Button("열기") { store.importRemoteWorkspace(connectionId: connection.id, workspaceId: workspace.id) }.disabled(store.remoteBusy).accessibilityLabel("\(workspace.name) 원격 워크스페이스 열기")
                    }.padding(10).background(Palette.subtle, in: RoundedRectangle(cornerRadius: 6))
                }
                if connection.workspaces.isEmpty { Text("상대 컴퓨터가 공유한 폴더가 없습니다.").font(.system(size: 11)).foregroundStyle(.secondary) }
            }
        }.padding(15).background(Palette.panel, in: RoundedRectangle(cornerRadius: 9)).overlay { RoundedRectangle(cornerRadius: 9).stroke(Palette.border) }
    }

    private func startSharing() {
        guard let number = Int(port), (1024...65535).contains(number) else { localError = "포트는 1024–65535 사이의 숫자로 입력하세요."; return }
        localError = nil
        store.startSharing(workspaceIds: Array(selectedWorkspaces), port: number)
    }
    private func connect() {
        localError = nil
        store.connectRemote(name: connectionName.trimmingCharacters(in: .whitespacesAndNewlines), address: address.trimmingCharacters(in: .whitespacesAndNewlines), token: token.trimmingCharacters(in: .whitespacesAndNewlines))
    }
    private func copy(_ value: String) { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(value, forType: .string) }
}
