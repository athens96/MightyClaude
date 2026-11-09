import Foundation
import MightyCore
import UserNotifications

extension AppStore {
    static let dailyBudgetKey = "usage.dailyBudgetUSD"

    /// The day's cost past which a notification is sent; 0 is no budget.
    var dailyBudgetUSD: Double {
        get { max(0, UserDefaults.standard.double(forKey: Self.dailyBudgetKey)) }
        set { UserDefaults.standard.set(max(0, newValue), forKey: Self.dailyBudgetKey); objectWillChange.send() }
    }

    /// Adds what the pane's run that just ended used to the ledger (`UsageLedger.record`),
    /// saves it off the main actor, and notifies once when the day goes over the budget.
    func recordRunUsage(_ id: String) {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), session.kind == SessionKind.claude,
              let usage = session.sessionUsage else { return }
        var ledger = usageLedger
        guard let record = ledger.record(paneId: id, workspaceId: session.workspaceId, provider: session.provider, model: session.model, usage: usage) else {
            usageLedger = ledger; return
        }
        usageLedger = ledger
        let directory = dataDirectory
        Task.detached(priority: .utility) { try? ledger.save(directory: directory) }
        let budget = dailyBudgetUSD
        if ledger.crossedBudget(budget, after: record) { notifyBudget(spent: ledger.cost(), budget: budget) }
    }

    private func notifyBudget(spent: Double, budget: Double) {
        Task {
            let center = UNUserNotificationCenter.current()
            guard (await center.notificationSettings()).authorizationStatus == .authorized else { return }
            let content = UNMutableNotificationContent()
            content.title = L("usage.budget.notificationTitle")
            content.body = L("usage.budget.notificationBody", ["spent": String(format: "$%.2f", spent), "budget": String(format: "$%.2f", budget)])
            content.sound = .default
            try? await center.add(UNNotificationRequest(identifier: "mighty-usage-budget", content: content, trigger: nil))
        }
    }
}
