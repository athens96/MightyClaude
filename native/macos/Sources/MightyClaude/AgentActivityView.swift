import AppKit
import MightyCore
import SwiftUI

struct AgentLogEntryView: View {
    let entry: LogEntry
    let sessionRunning: Bool
    let fallbackProvider: String

    @ViewBuilder var body: some View {
        if let activity = entry.activity {
            AgentActivityRow(activity: activity, sessionRunning: sessionRunning)
        } else if entry.kind == "assistant" {
            VStack(alignment: .leading, spacing: DesignMetrics.Spacing.md) {
                HStack(spacing: DesignMetrics.Spacing.sm) {
                    ProviderIcon(provider: entry.provider ?? fallbackProvider, size: 12)
                    Text(ProviderOptions.label(entry.provider ?? fallbackProvider)).fontWeight(.semibold)
                    Spacer()
                    Text(shortTime).font(.system(size: 9, design: .monospaced)).foregroundStyle(.tertiary)
                }.font(.system(size: 11)).foregroundStyle(.secondary)
                AgentMarkdownView(source: entry.text)
            }
            .padding(.vertical, DesignMetrics.Spacing.sm)
            .accessibilityIdentifier("markdown-\(entry.id)")
        } else if entry.kind == "user" {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: "arrow.up.right").font(.system(size: 11, weight: .semibold)).foregroundStyle(Palette.accent).padding(.top, DesignMetrics.Spacing.xxs)
                Text(entry.text).font(.system(size: 13)).lineSpacing(4).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.horizontal, DesignMetrics.Spacing.md).padding(.vertical, DesignMetrics.Spacing.md)
            .background(Palette.accent.opacity(0.07), in: RoundedRectangle(cornerRadius: 10))
            .padding(.vertical, DesignMetrics.Spacing.sm)
            .accessibilityLabel(L("transcript.sentMessage"))
        } else if entry.kind == "output" {
            ScrollView(.horizontal) {
                Text(entry.text).font(.system(size: 11, design: .monospaced)).lineSpacing(3).textSelection(.enabled)
                    .fixedSize(horizontal: true, vertical: false).padding(DesignMetrics.Spacing.md)
            }
            .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
            .padding(.vertical, DesignMetrics.Spacing.xxs)
        } else {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: entry.kind == "error" ? "exclamationmark.circle" : "info.circle")
                    .font(.system(size: 11)).frame(width: 15).padding(.top, DesignMetrics.Spacing.xxs)
                Text(entry.text).font(.system(size: 12)).lineSpacing(3).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .foregroundStyle(entry.kind == "error" ? Palette.errText : Color.secondary)
            .padding(.vertical, DesignMetrics.Spacing.xxs)
        }
    }

    private var shortTime: String {
        guard let date = ISO8601DateFormatter().date(from: entry.timestamp) else { return "" }
        return date.formatted(date: .omitted, time: .shortened)
    }
}

struct AgentActivityRow: View {
    let activity: AgentActivity
    let sessionRunning: Bool
    @ViewState private var expanded = false

    private var animating: Bool { sessionRunning && activity.state == "running" }
    private var hasDetails: Bool { !(activity.output ?? "").isEmpty || activity.summary.contains("\n") || activity.summary.count > 140 }
    private var monospace: Bool { ["command", "read", "edit"].contains(activity.kind) }
    private var stateDescription: String {
        switch activity.state {
        case "running": return sessionRunning ? L("transcript.tool.running") : L("transcript.tool.runEnded")
        case "waiting": return sessionRunning ? L("transcript.tool.waiting") : L("transcript.tool.runEnded")
        case "error": return L("transcript.tool.failed")
        case "stopped": return L("session.state.stopped")
        default: return L("session.state.completed")
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: DesignMetrics.Spacing.sm) {
            HStack(alignment: .top, spacing: DesignMetrics.Spacing.sm) {
                Image(systemName: Self.symbol(activity.kind))
                    .font(.system(size: 11, weight: .medium)).frame(width: 15, height: 17)
                    .foregroundStyle(activity.state == "error" ? Palette.errText : animating ? Palette.accent : Color.secondary)
                Text(activity.summary.isEmpty ? activity.toolName ?? L("transcript.tool.fallback") : activity.summary)
                    .font(.system(size: 12, design: monospace ? .monospaced : .default))
                    .lineSpacing(3).lineLimit(expanded ? nil : 2).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if animating { AgentRunningIndicator().padding(.top, DesignMetrics.Spacing.xxs) }
                else if sessionRunning && activity.state == "waiting" { Image(systemName: "clock").font(.system(size: 10)).foregroundStyle(Palette.accent).help(L("transcript.tool.waiting")) }
                else if activity.state == "error" { Text(L("transcript.tool.failed")).font(.system(size: 10, weight: .medium)).foregroundStyle(Palette.errText) }
                if hasDetails {
                    Button { expanded.toggle() } label: {
                        Image(systemName: expanded ? "chevron.up" : "chevron.down").font(.system(size: 9, weight: .medium)).frame(width: 18, height: 18)
                    }.buttonStyle(.plain).foregroundStyle(.secondary)
                        .accessibilityLabel(expanded ? L("transcript.tool.collapseDetail") : L("transcript.tool.expandDetail"))
                }
            }
            if expanded, let output = activity.output, !output.isEmpty {
                ScrollView([.horizontal, .vertical]) {
                    Text(output).font(.system(size: 11, design: .monospaced)).lineSpacing(3)
                        .fixedSize(horizontal: true, vertical: false).textSelection(.enabled).padding(DesignMetrics.Spacing.md)
                }
                .frame(maxHeight: 220)
                .background(Palette.subtle, in: RoundedRectangle(cornerRadius: 7))
                .padding(.leading, 15 + DesignMetrics.Spacing.sm) // under the summary: the icon column and its gap
            }
        }
        .padding(.vertical, DesignMetrics.Spacing.xxs)
        .foregroundStyle(.secondary)
        .help("\(stateDescription) · \(activity.summary)")
        .accessibilityElement(children: .contain)
        .accessibilityLabel(stateDescription)
        .accessibilityIdentifier("activity-\(activity.id)")
    }

    static func symbol(_ kind: String) -> String {
        switch kind {
        case "command": return "terminal"
        case "read": return "doc.text.magnifyingglass"
        case "edit": return "pencil.line"
        case "search": return "magnifyingglass"
        case "web": return "globe"
        case "agent": return "person.2"
        case "turn": return "sparkles"
        default: return "wrench.and.screwdriver"
        }
    }
}

/// A native indeterminate spinner, shown only for an actual running AI request.
/// An open interactive shell does not mean an agent is working.
struct AgentRunningIndicator: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Group {
            if reduceMotion { Image(systemName: "circle.dotted").font(.system(size: 12)).foregroundStyle(Palette.accent) }
            else { ProgressView().controlSize(.mini).progressViewStyle(.circular).tint(Palette.accent) }
        }
        .frame(width: 12, height: 12)
        .accessibilityLabel(L("transcript.runningIndicator"))
    }
}
