import Foundation
import MightyCore
import SwiftUI

extension Palette {
    /// A role's or a custom agent's ink (`FlowRoles`): one of the palette's own inks.
    static func flow(_ ink: FlowInk) -> Color { token(ink.token) }
    /// The ink of a role with no colour of its own.
    static func flow(_ role: FlowRole) -> Color { flow(FlowRoles.ink(role)) }
}

/// The custom agents the pane's blocks name, read off the main actor (`AgentDefinitions`): their colour takes the
/// sub-agent ink's place on their blocks. Only a local workspace's pane reads them: another computer's agents are
/// not on this Mac.
@MainActor
final class MightyGraphAgentDefinitionsModel: ObservableObject {
    struct Request: Hashable, Sendable {
        let root: URL?
        let types: [String]
    }
    @Published private(set) var definitions: [String: AgentDefinition] = [:]

    func load(_ request: Request) async {
        guard let root = request.root, !request.types.isEmpty else {
            if !definitions.isEmpty { definitions = [:] }
            return
        }
        let types = Set(request.types)
        let reading = Task.detached(priority: .utility) {
            AgentDefinitions.load(types: types, workspace: root, configDirectory: AgentDefinitions.configDirectory())
        }
        let found = await withTaskCancellationHandler(operation: { await reading.value }, onCancel: { reading.cancel() })
        guard !Task.isCancelled, found != definitions else { return }
        definitions = found
    }
}

/// The identity chips of a flow block's header, after its title: the sub-agent's type, the lead's effort, and the
/// marks for background, fork and worktree. Which of them show is decided from the block's width
/// (`FlowRoles.headerFit`), so the title, the status and the tokens keep their room; they give way first.
struct FlowIdentityChips: View {
    let identity: FlowIdentity
    let fit: FlowRoles.HeaderFit
    let nodeID: String
    /// The header is a filled strip: the chips take its ink instead of the role's.
    var stripInk: Color? = nil

    var body: some View {
        let ink = stripInk ?? Palette.flow(identity.ink)
        if let width = fit.typeWidth, let type = identity.typeChip {
            Text(verbatim: type).font(.ui(size: 10, weight: .semibold)).foregroundStyle(ink).lineLimit(1).truncationMode(.tail)
                .padding(.horizontal, DesignMetrics.Spacing.sm).padding(.vertical, DesignMetrics.Spacing.xxs)
                .background(ink.opacity(0.12), in: Capsule())
                .frame(maxWidth: width, alignment: .leading)
                .layoutPriority(-1)
                .help(identity.type ?? type)
                .accessibilityLabel(identity.type ?? type)
                .accessibilityIdentifier("mighty-type-\(nodeID)")
        }
        if fit.showsEffort, let effort = identity.effort {
            HStack(spacing: DesignMetrics.Spacing.xxs) {
                Image(systemName: "brain").font(.ui(size: 9, weight: .semibold))
                Text(verbatim: effort).font(.ui(size: 10, weight: .semibold)).lineLimit(1)
            }
            .foregroundStyle(stripInk ?? Palette.ink2).fixedSize()
            .help(L("composer.effort.menu", ["effort": effort]))
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(L("composer.effort.menu", ["effort": effort]))
            .accessibilityIdentifier("mighty-effort-\(nodeID)")
        }
        if fit.showsMarks {
            ForEach(identity.marks, id: \.self) { mark in
                Image(systemName: mark.symbol).font(.ui(size: 9, weight: .semibold)).foregroundStyle(stripInk ?? Palette.ink2)
                    .frame(width: 12).fixedSize()
                    .help(mark.help)
                    .accessibilityLabel(mark.help)
                    .accessibilityIdentifier("mighty-mark-\(mark.rawValue)-\(nodeID)")
            }
        }
    }
}
