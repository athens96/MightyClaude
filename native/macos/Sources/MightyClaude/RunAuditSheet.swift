import AppKit
import MightyCore
import SwiftUI

/// 이 요청이 한 일: the commands one request ran, the files it wrote, the
/// pages it fetched and the files it read, one row each (`RunAudit`), with a
/// copy of the list as text. A panel (kind C).
struct RunAuditSheet: View {
    let run: MightyGraphRun
    let onClose: () -> Void

    private var rows: [RunAudit.Row] { RunAudit.rows(run) }
    private static let labels: [RunAudit.Kind: String] = [
        .command: L("audit.kind.command"), .write: L("audit.kind.write"), .web: L("audit.kind.web"), .read: L("audit.kind.read"), .other: L("audit.kind.other"),
    ]

    var body: some View {
        DialogScaffold(L("audit.title"), subtitle: run.input, systemImage: "list.bullet.rectangle",
                       close: DialogClose(identifier: "audit-close", action: onClose)) {
            Button(L("audit.copy")) {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(RunAudit.text(rows, title: "# " + run.input, labels: Self.labels), forType: .string)
            }
            .disabled(rows.isEmpty)
        } content: {
            if rows.isEmpty {
                Text(L("audit.empty")).font(.ui(size: 12)).foregroundStyle(Palette.ink2).frame(maxWidth: .infinity, minHeight: 200)
            } else {
                ScrollView(.vertical) {
                    LazyVStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                        ForEach(RunAudit.Kind.allCases, id: \.self) { kind in
                            let group = rows.filter { $0.kind == kind }
                            if !group.isEmpty {
                                Text("\(Self.labels[kind] ?? "") · \(group.count)").font(.ui(size: 12, weight: .semibold))
                                ForEach(group) { row in
                                    HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                                        Text(row.tool).font(.code(size: 10)).foregroundStyle(Palette.ink2).frame(width: 90, alignment: .leading)
                                        Text(row.summary).font(.code(size: 11)).foregroundStyle(Palette.ink).textSelection(.enabled)
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                        if ["error", "failed"].contains(row.state) {
                                            Label(L("audit.failed"), systemImage: "xmark.octagon").font(.ui(size: 10)).foregroundStyle(Palette.errText)
                                        }
                                        if let agent = row.agent { Text(agent).font(.ui(size: 10)).foregroundStyle(Palette.ink2) }
                                    }
                                }
                            }
                        }
                    }
                }
                .frame(height: 420)
            }
        }
        .dialogSheetWidth(DesignMetrics.Dialog.sheetWidthL)
    }
}
