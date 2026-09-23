import Foundation
import Testing
@testable import MightyCore

/// Production-path proofs for the model defaults: these go through the entry points
/// the app calls (RunSession.beginGraphRun / recordGraph, StartRunRequest →
/// CoreValidation.validateSelection → ProviderService.arguments), never by
/// constructing a MightyGraphRun with the label by hand.
struct ModelDefaultsProductionPathTests {
    private func usage(_ session: RunSession, model: String) -> RunEvent {
        RunEvent(sessionId: session.id, type: "usage", usage: SessionUsage(provider: session.provider, source: "cli", model: model))
    }

    @Test func beginGraphRunLabelsTheRequestNodeWithTheConfiguredModel() {
        var session = RunSession(workspaceId: "workspace", title: "Graph")
        session.beginGraphRun(input: "Build it", id: "request-one", configuredModel: "acme/custom-v2")
        let run = session.mightyGraphRuns.last
        #expect(run?.configuredModel == "acme/custom-v2")
        #expect(run?.nodeModelLabel == "acme/custom-v2 · 설정")
    }

    @Test func beginGraphRunWithDefaultLeavesNoLabelUntilTheCLIReports() {
        var session = RunSession(workspaceId: "workspace", title: "Graph")
        session.beginGraphRun(input: "Build it", id: "request-one", configuredModel: "default")
        #expect(session.mightyGraphRuns.last?.nodeModelLabel == nil)
        session.recordGraph(usage(session, model: "claude-sonnet-5"))
        #expect(session.mightyGraphRuns.last?.nodeModelLabel == "claude-sonnet-5")
    }

    @Test func cliReportedModelReplacesTheConfiguredLabelOnTheRunPath() {
        var session = RunSession(workspaceId: "workspace", title: "Graph")
        session.beginGraphRun(input: "Build it", id: "request-one", configuredModel: "claude-opus-5")
        #expect(session.mightyGraphRuns.last?.nodeModelLabel == "claude-opus-5 · 설정")
        session.recordGraph(usage(session, model: "claude-sonnet-5"))
        #expect(session.mightyGraphRuns.last?.nodeModelLabel == "claude-sonnet-5")
        // A usage event without a model never erases a label.
        session.recordGraph(RunEvent(sessionId: session.id, type: "usage", usage: SessionUsage(provider: session.provider, source: "cli")))
        #expect(session.mightyGraphRuns.last?.nodeModelLabel == "claude-sonnet-5")
    }

    @Test func usageForAnotherSessionOrProviderDoesNotTouchTheLabel() {
        var session = RunSession(workspaceId: "workspace", title: "Graph")
        session.beginGraphRun(input: "Build it", id: "request-one", configuredModel: "acme/custom")
        session.recordGraph(RunEvent(sessionId: "someone-else", type: "usage", usage: SessionUsage(provider: "claude", source: "cli", model: "claude-sonnet-5")))
        #expect(session.mightyGraphRuns.last?.nodeModelLabel == "acme/custom · 설정")
    }

    @Test func aRegisteredNameReachesTheCLIArgumentsUnchanged() throws {
        let registered = [RegisteredModelEntry(name: "acme/custom-v2")]
        var request = StartRunRequest(sessionId: "s", workspaceId: "w", input: "hi", model: "acme/custom-v2", provider: "claude")
        request.registeredModels = registered
        let catalog = ModelCatalog(source: "cli", models: [ModelOption(value: "claude-sonnet-5", displayName: "Sonnet")])
        try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: registered)
        let args = try ProviderService.arguments(request, pluginDirectory: URL(fileURLWithPath: "/tmp/plugin"))
        #expect(args.contains("--model"))
        #expect(args.firstIndex(of: "--model").map { args[$0 + 1] } == "acme/custom-v2")
    }

    @Test func theSameNameWithoutRegistrationIsRejectedBeforeAnyArgumentIsBuilt() {
        let request = StartRunRequest(sessionId: "s", workspaceId: "w", input: "hi", model: "acme/custom-v2", provider: "claude")
        let catalog = ModelCatalog(source: "cli", models: [ModelOption(value: "claude-sonnet-5", displayName: "Sonnet")])
        #expect(throws: (any Error).self) { try CoreValidation.validateSelection(request, catalog: catalog, registeredModels: []) }
    }
}
