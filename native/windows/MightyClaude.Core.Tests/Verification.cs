using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MightyClaude.Core;

internal static class Verification
{
    private static int passed, skipped;
    /// <summary>Checks that failed: each is printed as FAIL and the run goes on, so one run shows every failure; the run fails at the end.</summary>
    private static readonly List<string> failedChecks = [];
    internal static string Temp() { var path = Path.Combine(Path.GetTempPath(), "mighty-core-test-" + Wire.Id()); Directory.CreateDirectory(path); return path; }
    private static void Check(bool value, string message = "Assertion failed") { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (Exception) { return; } throw new InvalidOperationException("Expected rejection"); }
    internal static async Task Until(Func<bool> condition, int milliseconds = 7000) { var end = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds); while (!condition()) { if (DateTimeOffset.UtcNow >= end) throw new TimeoutException("Expected condition was not reached"); await Task.Delay(20); } }
    private static async Task Test(string name, Func<Task> action)
    {
        try { await action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failedChecks.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    internal static CliCommand Self(params string[] args)
    {
        var binary = Environment.ProcessPath ?? throw new InvalidOperationException("Executable unavailable"); return new(binary, Path.GetFileNameWithoutExtension(binary).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? new[] { Assembly.GetExecutingAssembly().Location }.Concat(args).ToArray() : args, "2.1.271");
    }
    private static ProviderCatalog Absent() => new((_, _) => Task.FromResult<CliCommand?>(null));
    private static StartRunRequest Shell(string id, Workspace workspace, string text) => new(id, workspace.Id, "shell", text, []);
    private static string LongCommand(string pidFile) { var self = Self("--long-child", pidFile); return OperatingSystem.IsWindows() ? string.Join(" ", new[] { self.Binary }.Concat(self.Prefix).Select(ChildProcess.QuoteWindows)) : string.Join(" ", new[] { self.Binary }.Concat(self.Prefix).Select(v => "'" + v.Replace("'", "'\"'\"'") + "'")); }
    /// <param name="plain">Antigravity's prompt names each copy by its plain path in quotes, never JSON-escaped.</param>
    private static string AttachmentReferencePath(string prompt, string name, bool plain = false)
    {
        var prefix = JsonSerializer.Serialize(name, Wire.Json) + ": ";
        var line = prompt.Split('\n').Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (plain) return line[prefix.Length..].Trim('"');
        return JsonSerializer.Deserialize<string>(line[prefix.Length..], Wire.Json) ?? throw new InvalidOperationException("Missing attachment path.");
    }
    private sealed class FakeManager(Action<RunEvent> emit) : IRunManager
    {
        internal readonly ConcurrentBag<StartRunRequest> Started = [];
        internal readonly ConcurrentBag<string> Stopped = [];
        internal void Emit(RunEvent value) => emit(value);
        public Task StartAsync(StartRunRequest request) { Started.Add(request); emit(RunEvent.State(request.SessionId, "running")); return Task.CompletedTask; }
        public Task StopAsync(string id) { Stopped.Add(id); emit(RunEvent.State(id, "stopped")); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { foreach (var request in Started.Where(r => !Stopped.Contains(r.SessionId))) Stopped.Add(request.SessionId); return ValueTask.CompletedTask; }
    }
    internal static async Task FakeCliAsync(string provider, string record, string[] args)
    {
        await File.WriteAllTextAsync(record + ".args", JsonSerializer.Serialize(args, Wire.Json));
        if (args.Contains("--version")) { Console.WriteLine("2.1.271"); return; }
        // A Claude launched with host permission prompts: answer the handshake,
        // ask to use one tool once the prompt frame arrives, record the answer.
        if (args.Contains("--permission-prompt-tool"))
        {
            var planFixture = false;
            while (await Console.In.ReadLineAsync() is { } line)
            {
                await File.AppendAllTextAsync(record + ".input", line + "\n");
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                switch (root.Text("type"))
                {
                    case "control_request":
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "control_response", response = new { subtype = "success", request_id = root.Text("request_id"), response = new { } } }, Wire.Json));
                        break;
                    case "user" when line.Contains("sign-in fixture", StringComparison.Ordinal):
                    {
                        // What Claude Code 2.1.296 prints when its sign-in expired and could not be refreshed: the
                        // failed message marked authentication_failed, then its text again as the error result.
                        var text = line.Contains("expired sign-in fixture", StringComparison.Ordinal) ? "Failed to authenticate: OAuth session expired and could not be refreshed" : "Sign-in went stale · try later";
                        var mark = line.Contains("unmarked sign-in fixture", StringComparison.Ordinal) ? "server_error" : "authentication_failed";
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "assistant", error = mark, message = new { content = new[] { new { type = "text", text } } } }, Wire.Json));
                        // A sub-agent's own failure in between never decides the run's reason.
                        if (line.Contains("child sign-in fixture", StringComparison.Ordinal))
                            Console.WriteLine(JsonSerializer.Serialize(new { type = "assistant", parent_tool_use_id = "toolu_child", error = "rate_limit", message = new { content = new[] { new { type = "text", text = "Rate limited" } } } }, Wire.Json));
                        // A different failure, or a Bedrock policy denial beside the repeated words, is no lost sign-in.
                        var result = line.Contains("mismatched sign-in fixture", StringComparison.Ordinal) ? "API Error: 500 Internal server error" : text;
                        string[]? errors = line.Contains("denied sign-in fixture", StringComparison.Ordinal)
                            ? ["User: arn:aws:sts::123456789012:assumed-role/fixture/session is not authorized to perform: bedrock:InvokeModel with an explicit deny in a service control policy"] : null;
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "result", subtype = errors is null ? "success" : "error_during_execution", is_error = true, result, errors }, Wire.Json));
                        Environment.ExitCode = 1;
                        return;
                    }
                    case "user" when line.Contains("background stop fixture", StringComparison.Ordinal):
                        // The turn ends while a background shell still runs; only a stop ends the process.
                        await File.WriteAllTextAsync(record + ".pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        Console.WriteLine("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"sh1\",\"description\":\"npm run dev\",\"task_type\":\"local_bash\"}");
                        Console.WriteLine("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"started\"}");
                        while (await Console.In.ReadLineAsync() is not null) { }
                        return;
                    case "user":
                        // A plan-mode prompt ends planning with ExitPlanMode (ClaudePlanModeVerification).
                        planFixture = line.Contains("plan fixture", StringComparison.Ordinal);
                        // A style's own tool (StylesVerification.AutoAllowTool), asked for as any other tool is.
                        if (line.Contains("style fixture", StringComparison.Ordinal))
                        {
                            Console.WriteLine("{\"type\":\"control_request\",\"request_id\":\"style-1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"" + StylesVerification.AutoAllowTool + "\",\"tool_use_id\":\"tool-style-1\",\"input\":{\"step\":1}}}");
                            break;
                        }
                        Console.WriteLine(planFixture
                            ? "{\"type\":\"control_request\",\"request_id\":\"plan-1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"ExitPlanMode\",\"tool_use_id\":\"toolu_plan\",\"input\":{\"plan\":\"# Plan\\n1. Do it\",\"planFilePath\":\"/tmp/plan.md\"},\"requires_user_interaction\":true}}"
                            : "{\"type\":\"control_request\",\"request_id\":\"ask-1\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Read\",\"tool_use_id\":\"tool-ask-1\",\"input\":{\"file_path\":\"~/.claude/CLAUDE.md\"},\"blocked_path\":\"~/.claude/CLAUDE.md\"}}");
                        break;
                    case "control_response":
                        await File.WriteAllTextAsync(record + ".decision", line);
                        if (planFixture)
                        {
                            await File.WriteAllTextAsync(record + ".env", Environment.GetEnvironmentVariable("CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS") ?? "");
                            if (line.Contains("\"interrupt\":true", StringComparison.Ordinal))
                            {
                                Console.WriteLine("{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"errors\":[\"[Request interrupted by user]\"]}");
                                while (await Console.In.ReadLineAsync() is not null) { }
                                return;
                            }
                            Console.WriteLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_todo\",\"name\":\"TodoWrite\",\"input\":{\"todos\":[{\"content\":\"Do it\",\"status\":\"in_progress\",\"activeForm\":\"Doing it\"}]}}]}}");
                            Console.WriteLine("{\"type\":\"system\",\"subtype\":\"task_started\",\"task_id\":\"bg1\",\"tool_use_id\":\"toolu_bg\",\"description\":\"Review\",\"is_backgrounded\":true,\"task_type\":\"local_agent\"}");
                        }
                        Console.WriteLine("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"fixture\",\"session_id\":\"fixture\"}");
                        if (!planFixture) return;
                        if (File.Exists(record + ".quiet"))
                        {
                            // The agent ends without a follow-up turn or an idle event.
                            Console.WriteLine("{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"bg1\",\"status\":\"completed\",\"summary\":\"quiet\"}");
                            while (await Console.In.ReadLineAsync() is not null) { }
                            return;
                        }
                        // stdin stays open while the background agent runs: new input joins this process.
                        var follow = await Console.In.ReadLineAsync();
                        if (follow is null) { Environment.Exit(24); return; }
                        await File.WriteAllTextAsync(record + ".follow", follow);
                        Console.WriteLine("{\"type\":\"system\",\"subtype\":\"task_notification\",\"task_id\":\"bg1\",\"tool_use_id\":\"toolu_bg\",\"status\":\"completed\",\"summary\":\"All good\"}");
                        Console.WriteLine("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"noted\",\"origin\":{\"kind\":\"task-notification\"}}");
                        while (await Console.In.ReadLineAsync() is { } extra) await File.AppendAllTextAsync(record + ".extra", extra + "\n");
                        return;
                }
            }
            return;
        }
        if (args.Contains("app-server") || args.Contains("--safe-mode"))
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                await File.AppendAllTextAsync(record + ".input", line + "\n"); using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (provider == "claude")
                {
                    if (root.GetProperty("type").GetString() != "control_request") throw new InvalidOperationException("Prompt forbidden");
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "control_response", response = new { subtype = "success", request_id = root.GetProperty("request_id").GetString(), response = new { models = new[] { new { value = "sonnet", displayName = "Sonnet", description = "Fixture", supportsEffort = true, supportedEffortLevels = new[] { "low", "high" } } } } } }, Wire.Json));
                }
                else if (root.GetProperty("method").GetString() == "initialize") Console.WriteLine("{\"id\":1,\"result\":{}}");
                else if (root.GetProperty("method").GetString() == "model/list") Console.WriteLine("{\"id\":2,\"result\":{\"data\":[{\"id\":\"gpt-6-astra\",\"model\":\"gpt-6-astra\",\"displayName\":\"Astra\",\"supportedReasoningEfforts\":[{\"reasoningEffort\":\"high\"}]}],\"nextCursor\":null}}");
                else if (root.GetProperty("method").GetString() != "initialized") throw new InvalidOperationException("Turn creation forbidden");
            }
        }
        else
        {
            var input = await Console.In.ReadToEndAsync(); await File.WriteAllTextAsync(record + ".prompt", input);
            if (args.Contains("--verify-attachments"))
            {
                var flag = provider == "codex" ? "--image" : "--add-dir";
                var index = Array.IndexOf(args, flag); Check(index >= 0 && index + 1 < args.Length, provider + " staged attachment argument missing");
                var directory = provider == "codex" ? Path.GetDirectoryName(args[index + 1])! : args[index + 1];
                var captured = new Dictionary<string, string>();
                foreach (var file in Directory.EnumerateFiles(directory)) captured.Add(file, Convert.ToBase64String(await File.ReadAllBytesAsync(file)));
                await File.WriteAllTextAsync(record + ".attachments", JsonSerializer.Serialize(captured, Wire.Json));
            }
            // An agy that is refused a command on its first turn and, continued (--conversation), says whether its settings allow it.
            if (args.Contains("--agy-denied")) { AntigravityPermissionVerification.FakeAgy(args[Array.IndexOf(args, "--agy-denied") + 1], args.Contains("--conversation"), input); return; }
            // What Antigravity CLI 1.3.5 prints on stderr when its sign-in is gone, and the same words as a model answer.
            const string antigravitySignInLost = "Error: authentication required. Run 'agy' to log in, then retry.";
            if (args.Contains("--agy-signin-lost")) { await Console.Error.WriteLineAsync(antigravitySignInLost); Environment.ExitCode = 1; return; }
            if (args.Contains("--agy-quoted"))
            {
                Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["event"] = "step_update", ["step_update"] = new Dictionary<string, object> { ["step_index"] = 1, ["state"] = "DONE", ["step_type"] = "agent_response", ["text_delta"] = antigravitySignInLost } }));
                Console.WriteLine("{\"event\":\"result\",\"result\":{\"status\":\"ERROR\",\"error\":{\"message\":\"Internal error encountered.\"}}}");
                Environment.ExitCode = 1; return;
            }
            // agy prints its own stream (Antigravity's `event` lines); the other agents read Codex's exec items here.
            if (provider == "gemini")
            {
                Console.WriteLine("{\"event\":\"init\",\"conversation_id\":\"11111111-2222-4333-8444-555555555555\",\"init\":{\"model\":\"gemini-3.8-flash-low\"}}");
                Console.WriteLine("{\"event\":\"step_update\",\"step_update\":{\"step_index\":1,\"state\":\"DONE\",\"step_type\":\"agent_response\",\"text_delta\":\"FAKE_CLI_OK\\n\"}}");
                Console.WriteLine("{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"FAKE_CLI_OK\\n\"}}");
            }
            else { Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"fixture-thread\"}"); Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"id\":\"answer\",\"type\":\"agent_message\",\"text\":\"FAKE_CLI_OK\"}}"); }
            if (args.Contains("--hold-run")) await Task.Delay(60000);
            if (args.Contains("--fail-run")) Environment.ExitCode = 2;
        }
    }
    internal static async Task RunAsync()
    {
        // The strings checks compare the Windows copy with locales/ko.json, and the
        // per-feature strings classes capture Locale.Get once at type initialisation.
        // Pin Korean before anything touches them so the checks mean the same on an
        // English CI runner as on a Korean Mac; the locale checks set their own preference.
        Locale.LanguagePreference = "ko";
        Locale.ResetCache();
        await Test("TIFF image headers and allocation bounds", NativeImageParityVerification.TiffHeaderHandlesEndianAndRefusesInvalidDirectories);
        await Test("SVG native raster boundary rejects active and external documents", NativeImageParityVerification.SvgRasterInputRefusesActiveOrExternalContent);
        await Test("CodexSessionWatcherVerification NestedChildrenStayScopedAndPublishLiveModels", CodexSessionWatcherVerification.NestedChildrenStayScopedAndPublishLiveModels);
        await Test("CodexSessionWatcherVerification ForksDoNotExposeInheritedHistoryOrSealedMessages", CodexSessionWatcherVerification.ForksDoNotExposeInheritedHistoryOrSealedMessages);
        await Test("CodexSessionWatcherVerification PartialOversizedAndTruncatedRecordsRemainBounded", CodexSessionWatcherVerification.PartialOversizedAndTruncatedRecordsRemainBounded);
        await Test("CodexSessionWatcherVerification UntrustedMetadataAndLinksNeverBecomeChildren", CodexSessionWatcherVerification.UntrustedMetadataAndLinksNeverBecomeChildren);
        await Test("CodexSessionWatcherVerification ReusedChildKeepsPreviousAnswersAndResponseUsageOnce", CodexSessionWatcherVerification.ReusedChildKeepsPreviousAnswersAndResponseUsageOnce);
        await Test("CodexSessionWatcherVerification RunningProcessPublishesGraphBeforeItExits", CodexSessionWatcherVerification.RunningProcessPublishesGraphBeforeItExits);
        await Test("GraphPreviewVerification RememberedGraphSizesDriveLayoutAndNormalize", GraphPreviewVerification.RememberedGraphSizesDriveLayoutAndNormalize);
        await Test("GraphPreviewVerification LocalHtmlReadsOnlyItsOwnBoundedDirectory", GraphPreviewVerification.LocalHtmlReadsOnlyItsOwnBoundedDirectory);
        await Test("MightyTimelineVerification RecordedOrderAndResultsMatchDiagram", MightyTimelineVerification.RecordedOrderAndResultsMatchDiagram);
        await Test("MightyTimelineVerification TheRequestRowHoldsTheInputAndTheExecutionRowTheWork", MightyTimelineVerification.TheRequestRowHoldsTheInputAndTheExecutionRowTheWork);
        await Test("MightyTimelineVerification StatusRailsCountsAndDurationStayHonest", MightyTimelineVerification.StatusRailsCountsAndDurationStayHonest);
        await Test("mighty execution block: a stable node id", MightyGraphExecutionBlockVerification.TheExecutionBlockHasAStableNodeID);
        await Test("mighty execution block: the request sits over it in one column", MightyGraphExecutionBlockVerification.TheRequestSitsOverItsExecutionBlockInOneColumn);
        await Test("mighty execution block: sub-agents branch from it", MightyGraphExecutionBlockVerification.SubAgentsBranchFromTheExecutionBlock);
        await Test("mighty execution block: an empty run still shows it", MightyGraphExecutionBlockVerification.AnEmptyRunStillShowsItsExecutionBlock);
        await Test("mighty execution block: the request block holds its input and it holds the work", MightyGraphExecutionBlockVerification.TheRequestBlockHoldsItsInputAndTheExecutionBlockTheWork);
        await Test("mighty execution block: the camera stands on it while the request runs", MightyGraphExecutionBlockVerification.TheCameraStandsOnTheExecutionBlockWhileTheRequestRuns);
        await Test("mighty execution block: the request's attachments hang off it", MightyGraphExecutionBlockVerification.TheRequestsAttachmentsHangOffItsExecutionBlock);
        await Test("mighty execution block: without the split the shared layout is unchanged", MightyGraphExecutionBlockVerification.WithoutTheSplitTheSharedLayoutIsUnchanged);
        await Test("mighty execution block: a size saved before the split moves to it once", MightyGraphExecutionBlockVerification.ASizeSavedBeforeTheSplitMovesToTheExecutionBlockOnce);
        await Test("mighty execution block: a request and it share one saved-size place", MightyGraphExecutionBlockVerification.ARequestAndItsExecutionBlockShareOneSavedSizePlace);
        await Test("mighty execution block: its saved size is kept, with the pane's own blocks only", MightyGraphExecutionBlockVerification.AnExecutionBlocksSavedSizeIsKept);
        await Test("mighty follow: typing a draft never changes what the camera follows", MightyGraphFollowVerification.TypingADraftNeverChangesWhatTheCameraFollows);
        await Test("mighty follow: the followed block is the newest request's lowest block", MightyGraphFollowVerification.TheFollowedBlockIsTheNewestRequestsLowestBlock);
        await Test("mighty follow: a send puts the new request's bottom just above the composer", MightyGraphFollowVerification.ASendPutsTheNewRequestsBottomJustAboveTheComposer);
        await Test("mighty follow: a following camera puts each new block right above the composer", MightyGraphFollowVerification.AFollowingCameraPutsEachNewBlockRightAboveTheComposer);
        await Test("mighty follow: a taller composer keeps the followed block above it", MightyGraphFollowVerification.ATallerComposerKeepsTheFollowedBlockAboveIt);
        await Test("mighty follow: a reader who moved away is never pulled back", MightyGraphFollowVerification.AReaderWhoMovedAwayIsNeverPulledBack);
        await Test("mighty follow: a reader who scrolled up before a layout landed is not followed through it", MightyGraphFollowVerification.AReaderWhoScrolledUpBeforeALayoutLandedIsNotFollowedThroughIt);
        await Test("mighty follow: a reader who comes back to the newest block follows again", MightyGraphFollowVerification.AReaderWhoComesBackToTheNewestBlockFollowsAgain);
        await Test("mighty follow: a reader below the newest block keeps their place while it settles", MightyGraphFollowVerification.AReaderBelowTheNewestBlockKeepsTheirPlaceWhileItSettles);
        await Test("mighty follow: a trim above keeps the followed block where it was", MightyGraphFollowVerification.ATrimAboveKeepsTheFollowedBlockWhereItWas);
        await Test("mighty follow: opening or resizing a block never moves the camera", MightyGraphFollowVerification.OpeningOrResizingABlockNeverMovesTheCameraAndKeepsFollowOnlyAtTheNewest);
        await Test("mighty follow: a block taller than the viewport shows its bottom", MightyGraphFollowVerification.ABlockTallerThanTheViewportShowsItsBottom);
        await Test("mighty follow: an aim elsewhere stops following until the next send", MightyGraphFollowVerification.AnAimElsewhereStopsFollowingUntilTheNextSend);
        await Test("mighty follow: older requests loaded above and a zoom keep the reader's place", MightyGraphFollowVerification.OlderRequestsLoadedAboveAndAZoomKeepTheReadersPlace);
        await Test("mighty follow: the run-id rules follow a send and re-aim a trim in every view", MightyGraphFollowVerification.TheRunIdRulesFollowASendAndReaimATrimInEveryView);
        await Test("mighty interaction: a border's band reaches further outside than inside", MightyGraphInteractionVerification.TheBandReachesFurtherOutsideThanInside);
        await Test("mighty interaction: every side grows away from the box and honours zoom and bounds", MightyGraphInteractionVerification.EverySideGrowsAwayFromTheBoxAndHonoursZoomAndBounds);
        await Test("mighty interaction: the corner opposite the dragged sides stays put", MightyGraphInteractionVerification.TheCornerOppositeTheDraggedSidesStaysPut);
        await Test("mighty interaction: a press resizes by the handle, then by a band", MightyGraphInteractionVerification.APressResizesByTheHandleThenByABand);
        await Test("mighty interaction: the stranded watch judges only layout moves under a still camera", MightyGraphInteractionVerification.TheStrandedWatchJudgesOnlyLayoutMovesUnderAStillCamera);
        await Test("mighty interaction: a loss names the card the user saw most of", MightyGraphInteractionVerification.ALossNamesTheCardTheUserSawMostOf);
        await Test("mighty interaction: the reference bubble's size is kept for every pane", MightyGraphInteractionVerification.TheReferenceBubblesSizeIsKeptForEveryPane);
        await Test("mighty images: the preview attaches to the step that produced the pictures", MightyGraphImagesVerification.TheGraphPreviewAttachesToTheStepThatProducedThePictures);
        await Test("mighty images: a step's pictures are worked out once until it changes", MightyGraphImagesVerification.AStepsPicturesAreWorkedOutOnceUntilItChanges);
        await Test("mighty images: the attachments hang off the execution block and stack", MightyGraphImagesVerification.TheAttachmentsHangOffTheExecutionBlockAndStack);
        await Test("ouroboros execution: start tool results name their executions", OuroborosExecutionVerification.StartToolResultsNameTheirExecutions);
        await Test("ouroboros execution: ids that could break out of a URL or an argument are rejected", OuroborosExecutionVerification.IdsThatCouldBreakOutOfAUrlOrAnArgumentAreRejected);
        await Test("ouroboros execution: only plain http on a loopback host with a port is ever an endpoint", OuroborosExecutionVerification.OnlyPlainHttpOnALoopbackHostWithAPortIsEverAnEndpoint);
        await Test("ouroboros execution: dashboard payloads are read tolerantly", OuroborosExecutionVerification.DashboardPayloadsAreReadTolerantly);
        await Test("ouroboros execution: the summary owns status and counts and the board fills the rest", OuroborosExecutionVerification.TheSummaryOwnsStatusAndCountsAndTheBoardFillsTheRest);
        await Test("ouroboros execution: polling is fast while things change, slow when quiet, and stops when terminal", OuroborosExecutionVerification.PollingIsFastWhileThingsChangeSlowWhenQuietAndStopsWhenTerminal);
        await Test("ouroboros execution: reads that never tell the status back off and the daemon is started sparingly", OuroborosExecutionVerification.ReadsThatNeverTellTheStatusBackOffAndTheDaemonIsStartedSparingly);
        await Test("ouroboros execution: the client reads the daemon's own record and only GETs the two endpoints", OuroborosExecutionVerification.TheClientReadsTheDaemonsOwnRecordAndOnlyGetsTheTwoEndpoints);
        await Test("ouroboros execution: without a record only a loopback hint is used and the daemon is ensured without a shell", OuroborosExecutionVerification.WithoutARecordOnlyALoopbackHintIsUsedAndTheDaemonIsEnsuredWithoutAShell);
        await Test("ouroboros execution: the loopback transport reads only loopback within its limits", OuroborosExecutionVerification.TheLoopbackTransportReadsOnlyLoopbackWithinItsLimits);
        await Test("ouroboros execution: the pane's monitor reads its executions and starts the daemon sparingly", OuroborosExecutionVerification.ThePanesMonitorReadsItsExecutionsAndStartsTheDaemonSparingly);
        await Test("MightyTimelineVerification SavedModeIsIndependentAndLenient", MightyTimelineVerification.SavedModeIsIndependentAndLenient);
        await Test("Mighty structure: each request's phase", MightyStructureVerification.PhaseAssignment);
        await Test("Mighty structure: phase columns and free rows", MightyStructureVerification.ColumnsAndFreeRows);
        await Test("Mighty structure: cards show the work in place", MightyStructureVerification.CardsShowTheWorkInPlace);
        await Test("Mighty structure: texts are capped and folded", MightyStructureVerification.TextsAreCappedAndFolded);
        await Test("Mighty structure: clusters are the root trees", MightyStructureVerification.ClustersAreTheRootTrees);
        await Test("Mighty structure: the signature and the card cache", MightyStructureVerification.SignatureAndItemCache);
        await Test("Mighty structure: the saved choice survives a restart", MightyStructureVerification.SavedChoiceSurvivesARestart);
        await Test("Mighty structure: finished requests fold", MightyStructureVerification.FinishedRequestsFold);
        await Test("Mighty structure: dragging empty space pans", MightyStructureVerification.DraggingEmptySpacePans);
        await Test("Mighty structure: the page follows the newest card", MightyStructureVerification.ThePageFollowsTheNewestCard);
        await Test("Mighty structure: a user's move is judged when it happens", MightyStructureVerification.AUsersMoveIsJudgedWhenItHappens);
        await Test("Mighty structure: a drag in a column moves it up and down and the page sideways", MightyStructureVerification.ADragInAColumnMovesTheColumnUpAndDownAndThePageSideways);
        await Test("Mighty structure: each column follows its own end", MightyStructureVerification.EachColumnFollowsItsOwnEnd);
        await Test("Mighty structure: a text opened in a following column leaves the reader where it begins", MightyStructureVerification.ATextOpenedInAFollowingColumnLeavesTheReaderWhereItBegins);
        await Test("Mighty structure: a new request or another board starts the columns over", MightyStructureVerification.ANewRequestOrAnotherBoardStartsTheColumnsOver);
        await Test("Mighty structure board: a free pane has one board per request", MightyStructureBoardVerification.AFreePaneHasOneBoardPerRequest);
        await Test("Mighty structure board: a styled pane splits into pipelines where the phase goes back", MightyStructureBoardVerification.AStyledPaneSplitsIntoPipelinesWhereThePhaseGoesBack);
        await Test("Mighty structure board: the newest pipeline takes the pane's phase only when it lies ahead", MightyStructureBoardVerification.TheNewestPipelineTakesThePanesPhaseOnlyWhenItLiesAhead);
        await Test("Mighty structure board: claude-plan stages split when a request plans again", MightyStructureBoardVerification.ClaudePlanStagesSplitWhenARequestPlansAgain);
        await Test("Mighty structure board: a styled column holds its requests with their sub-agents", MightyStructureBoardVerification.AStyledColumnHoldsItsRequestsWithTheirSubAgentsUnderEach);
        await Test("Mighty structure board: ids hold as runs arrive and old ones are trimmed", MightyStructureBoardVerification.BoardIdsHoldAsRunsArriveAndOldOnesAreTrimmed);
        await Test("Mighty structure board: the view moves on with the newest board until the user picks an older one", MightyStructureBoardVerification.TheViewMovesOnWithTheNewestBoardUntilTheUserPicksAnOlderOne);
        await Test("Mighty structure board: a picked board is found again after a trim", MightyStructureBoardVerification.APickedBoardIsFoundAgainAfterItsFirstRequestIsTrimmed);
        await Test("Mighty structure wheel: the origin, the axis and the taker", MightyStructureBoardVerification.TheWheelMovesTheOriginAndStaysOnItsAxis);
        await Test("Mighty structure wheel: a gesture is decided once and is the user's until it settles", MightyStructureBoardVerification.AGestureIsDecidedOnceAndIsTheUsersUntilItSettles);
        await Test("Mighty structure wheel: a vertical wheel moves the column and a sideways one the page", MightyStructureBoardVerification.AVerticalWheelMovesTheColumnAndASidewaysOneThePage);
        await Test("WorkDashboardVerification CountsAndAttentionMatchMac", WorkDashboardVerification.CountsAndAttentionMatchMac);
        await Test("WorkDashboardVerification CardsRespectUsageIdentityAndStablePriority", WorkDashboardVerification.CardsRespectUsageIdentityAndStablePriority);
        await Test("WorkDashboardVerification ActivityAndAgeRemainBoundedAndTruthful", WorkDashboardVerification.ActivityAndAgeRemainBoundedAndTruthful);
        await Test("WorkDashboardVerification SettingsSelectionSurvivesPersistence", WorkDashboardVerification.SettingsSelectionSurvivesPersistence);
        await Test("WorkDashboardVerification SidebarCollapsedPersistsAndKeepsWidth", WorkDashboardVerification.SidebarCollapsedPersistsAndKeepsWidth);
        await Test("WorkDashboardVerification BackgroundWorkLinePersistsAndHides", WorkDashboardVerification.BackgroundWorkLinePersistsAndHides);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) await Test("WorkDashboardVerification BackgroundWorkHideSurvivesABriefReplaceBlock", WorkDashboardVerification.BackgroundWorkHideSurvivesABriefReplaceBlock);
        else { skipped++; Console.WriteLine("SKIP background work hide survives a brief replace block (needs Windows file sharing or macOS file flags)"); }
        await Test("windows parity: the running count leaves out agent and file panes", WindowsParityVerification.RunningCountLeavesOutAgentAndFilePanes);
        await Test("windows parity: a shell pane's status follows its shell", WindowsParityVerification.ShellPaneStatusFollowsItsShell);
        if (OperatingSystem.IsWindows()) await Test("windows parity: a real shell reads running only while a command runs", WindowsParityVerification.RealShellReadsRunningOnlyWhileACommandRuns);
        else { skipped++; Console.WriteLine("SKIP windows parity: a real shell reads running only while a command runs (requires Windows)"); }
        await Test("windows parity: component actions carry their own titles", WindowsParityVerification.ComponentActionsCarryTheirOwnTitles);
        await Test("windows parity: the model reset reads every workspace and says how it went", WindowsParityVerification.ModelResetReadsEveryWorkspaceAndSaysHowItWent);
        await Test("windows parity: the 129th pane is refused", WindowsParityVerification.TheHundredAndTwentyNinthPaneIsRefused);
        await Test("windows parity: a dragged tab lands where the Mac's zones say", WindowsParityVerification.TabDropZonesFollowTheMac);
        await Test("windows parity: find in the files pane source matches and steps as the Mac's find bar", WindowsParityVerification.SourceFindMatchesAndSteps);
        await Test("windows parity: missing resources beside the app are named in the warning band", WindowsParityVerification.MissingResourcesAreNamedInTheBand);
        await Test("toolkit add takes one entry object as the Mac does", ToolkitFileVerification.AddTakesOneEntryObjectAsTheMacDoes);
        await Test("composer parity: a pane's model and effort follow a newly read list", ComposerParityVerification.ModelSelectionFollowsTheNewList);
        await Test("composer parity: a model list is read again when it must be", ComposerParityVerification.ModelListIsReadAgainWhenItMustBe);
        await Test("composer parity: a built-in slash command sent in full never reaches the CLI", ComposerParityVerification.SentBuiltinNeverReachesTheCli);
        await Test("composer parity: a transcript keeps the newest 400 entries", ComposerParityVerification.TranscriptKeepsTheNewestFourHundred);
        await Test("composer parity: compaction, collaboration, rate limits and over-long lines reach the pane", ComposerParityVerification.StreamEventsReachTheTranscript);
        await Test("composer parity: a Bedrock policy denial reads as guidance", ComposerParityVerification.BedrockPolicyDenialReadsAsGuidance);
        await Test("composer parity: the queue says what became of it", ComposerParityVerification.QueueSaysWhatBecameOfIt);
        await Test("composer parity: switching the agent says so", ComposerParityVerification.SwitchingTheAgentSaysSo);
        await Test("composer parity: the status line asks again on a tick", ComposerParityVerification.StatusLineAsksAgainOnATick);
        await Test("composer parity: a terminal sign-in is polled until it takes", ComposerParityVerification.TerminalSignInIsPolledUntilItTakes);
        await Test("composer parity: text with a line break is not put into a terminal", ComposerParityVerification.TerminalInputRefusesALineBreak);
        await Test("composer parity: the run log is copied behind its roles", ComposerParityVerification.RunLogIsCopiedBehindItsRoles);
        await Test("composer parity: the resume look-up asks only for a session to continue", ComposerParityVerification.ResumeLookUpAsksOnlyForASessionToContinue);
        await Test("composer parity: the toolkit names its tools and says why a file failed", ComposerParityVerification.ToolkitNamesItsToolsAndSaysWhy);
        await Test("QueuedComposerVerification QueuePreservesOrderBoundsAndSettlementPolicy", QueuedComposerVerification.QueuePreservesOrderBoundsAndSettlementPolicy);
        await Test("QueuedComposerVerification SteerRequiresLiveInitializedChannelAndSuccessfulWrite", QueuedComposerVerification.SteerRequiresLiveInitializedChannelAndSuccessfulWrite);
        await Test("QueuedComposerVerification PermissionResponsesAndCancellationSettleOnlyOnce", QueuedComposerVerification.PermissionResponsesAndCancellationSettleOnlyOnce);
        await Test("BedrockSettingsVerification ExternalStatusDoesNotClaimVerifiedAccess", BedrockSettingsVerification.ExternalStatusDoesNotClaimVerifiedAccess);
        await Test("BedrockSettingsVerification ResetPreservesUnrelatedCredentialsAndMakesBackup", BedrockSettingsVerification.ResetPreservesUnrelatedCredentialsAndMakesBackup);
        await Test("QuestionnaireVerification QuestionnaireParsingRequiresCompleteBoundedChoices", QuestionnaireVerification.QuestionnaireParsingRequiresCompleteBoundedChoices);
        await Test("QuestionnaireVerification QuestionnaireAnswersEnforceSelectionModeAndOriginalOrder", QuestionnaireVerification.QuestionnaireAnswersEnforceSelectionModeAndOriginalOrder);
        await Test("QuestionnaireVerification AnswerRpcSettlesOnceAndReplacesModelSuppliedResponse", QuestionnaireVerification.AnswerRpcSettlesOnceAndReplacesModelSuppliedResponse);
        await Test("Questionnaire reply delivery failure", QuestionnaireVerification.BrokenQuestionnaireReplyFailsWithoutClaimingDelivery);
        await Test("Mobile remote SharedCipherVectors", MobileRemoteVerification.SharedCipherVectors);
        await Test("Mobile remote DeviceTrustAndRotation", MobileRemoteVerification.DeviceTrustAndRotation);
        await Test("Mobile remote UploadOwnershipAndClaims", MobileRemoteVerification.UploadOwnershipAndClaims);
        await Test("Mobile remote DesktopRouteContract", MobileRemoteVerification.DesktopRouteContract);
        await Test("Mobile remote EncryptedRelayRoundTrip", MobileRemoteVerification.EncryptedRelayRoundTrip);
        await Test("Mobile relay shutdown rejects reconnect and rotation", MobileRemoteVerification.DisposedHostCannotRestart);
        await Test("Mobile structure: the phone gets the desktop's shape", MobileStructureVerification.CarriesTheDesktopShape);
        await Test("Mobile structure: the shared fixture is the wire shape", MobileStructureVerification.SharedFixtureIsTheWireShape);
        await Test("Mobile structure: bounds and budget", MobileStructureVerification.BoundsAndBudget);
        await Test("Mobile structure: rebuilt only when the structure moves", MobileStructureVerification.CacheRebuildsOnlyWhenTheStructureMoves);
        await Test("Mobile structure: sent only when asked and changed, shed first", MobileStructureVerification.RouteSendsTheStructureOnlyWhenAskedAndChanged);
        await Test("Mobile host: runs carry the Mac's shape and caps", MobileHostVerification.RunsCarryTheMacsShapeAndCaps);
        await Test("Mobile host: a pane without a graph is grouped from its transcript", MobileHostVerification.APaneWithoutAGraphIsGroupedFromItsTranscript);
        await Test("Mobile host: a phone is told only of what changed", MobileHostVerification.APhoneIsToldOnlyOfWhatChanged);
        await Test("Mobile host: a session reply reads as the desktop does", MobileHostVerification.ASessionReplyReadsAsTheDesktopDoes);
        await Test("Mobile host: a pane travels as its provider runs", MobileHostVerification.APaneTravelsAsItsProviderRuns);
        await Test("Mobile host: settings offer what the pane can pick", MobileHostVerification.SettingsOfferWhatThePaneCanPick);
        await Test("Mobile host: older phones still get the Ouroboros payload", MobileHostVerification.OlderPhonesStillGetTheOuroborosPayload);
        await Test("Mobile host: file replies carry a code and the Mac's caps", MobileHostVerification.FileRepliesCarryACodeAndTheMacsCaps);
        await Test("Mobile host: the link policy decides when to redial", MobileHostVerification.LinkPolicyDecidesWhenToRedial);
        await Test("Mobile host: the link redials after a wake and waits out no network", MobileHostVerification.TheLinkRedialsAfterAWakeAndWaitsOutNoNetwork);
        await Test("Mobile host: a half-dead control socket is replaced", MobileHostVerification.AHalfDeadControlSocketIsReplaced);
        await Test("Mobile host: the device list marks arrivals and names the legacy row", MobileHostVerification.TheDeviceListMarksArrivalsAndNamesTheLegacyRow);
        await Test("Mobile host: reply limits are the Mac's", MobileHostVerification.ReplyLimitsAreTheMacs);
        await Test("Mobile host: a display that disappears moves its viewers to the main one", MobileHostVerification.ADisplayThatDisappearsMovesItsViewersToTheMainOne);
        await Test("tailscale addresses: the shared fixture's cases", TailscaleAddressesVerification.SharedFixture);
        await Test("tailscale addresses: a mapped address counts as its IPv4", TailscaleAddressesVerification.AMappedAddressCountsAsItsIPv4);
        await Test("tailscale addresses: detection offers only Tailscale addresses", TailscaleAddressesVerification.DetectionOffersOnlyTailscaleAddresses);
        await Test("tailscale addresses: one unreadable adapter is skipped, none readable is a fault", TailscaleAddressesVerification.OneUnreadableAdapterIsSkipped);
        await Test("mobile direct: endpoints, upgrade, admission and status", MobileDirectVerification.EndpointsUpgradeAdmissionAndStatus);
        await Test("mobile direct: the listener holds at most 36 and upgrades a phone", MobileDirectVerification.TheListenerHoldsAtMost36AndUpgradesAPhone);
        await Test("mobile direct: the upgrade and the peer are read strictly", MobileDirectVerification.TheUpgradeAndThePeerAreReadStrictly);
        await Test("mobile direct: a phone connects directly on the relay's handshake", MobileDirectVerification.APhoneConnectsDirectlyOnTheRelaysHandshake);
        await Test("mobile direct: only who may gets in, and relay redials leave them alone", MobileDirectVerification.OnlyWhoMayGetsInAndRelayRedialsLeaveThemAlone);
        await Test("mobile direct: the host advertises its endpoints", MobileDirectVerification.TheHostAdvertisesItsEndpoints);
        await Test("windows firewall: the rule is built from constants", WindowsFirewallVerification.TheRuleIsBuiltFromConstants);
        await Test("windows firewall: the runner elevates only that command", WindowsFirewallVerification.TheRunnerElevatesOnlyThatCommand);
        await Test("windows firewall: only the rule file elevates", WindowsFirewallVerification.OnlyTheRuleFileElevates);
        await Test("mobile direct: the Windows words are in every language", MobileDirectVerification.TheWindowsWordsAreInEveryLanguage);
        await Test("mobile direct: the settings rows and the smoke are wired", MobileDirectVerification.TheSettingsAndTheSmokeAreWired);
        await Test("mobile direct: relay notices carrying a direct- id leave the direct phone alone", MobileDirectVerification.RelayNoticesCarryingADirectIdLeaveItAlone);
        await Test("mobile direct: a direct frame over 1 MiB is refused", MobileDirectVerification.ADirectFrameOverOneMiBIsRefused);
        await Test("mobile direct: a relay auth frame that is no JSON is answered malformed", MobileDirectVerification.ARelayAuthFrameThatIsNoJsonIsMalformed);
        await Test("mobile direct: Settings read the adapters off the caller and outside the lock", MobileDirectVerification.SettingsReadTheAdaptersOffTheCallerAndOutsideTheLock);
        await Test("Companion animation and carousel", CompanionVerification.AnimationAndCarousel);
        await Test("Companion bubble resize edges and DPI anchors", CompanionLayoutVerification.EdgesAndAnchors);
        await Test("Workspace disclosure migration, independent selection and disk persistence", WorkspaceDisclosureVerification.MigrationSelectionAndPersistence);
        await Test("CLI environment profile frame rejects ambiguity and redacts diagnostics", CliEnvironmentVerification.StrictFramingAndSecrets);
        await Test("CLI environment refresh rotates tokens and preserves explicit unsets", CliEnvironmentVerification.RefreshRotationAndUnset);
        await Test("CLI environment timeout cancellation and forced freshness", CliEnvironmentVerification.CancellationTimeoutAndFreshness);
        if (OperatingSystem.IsWindows()) await Test("CLI environment real PowerShell capture wire", CliEnvironmentVerification.WindowsCaptureWire);
        else { skipped++; Console.WriteLine("SKIP CLI environment real PowerShell capture wire (requires Windows)"); }
        await Test("Companion catalog boundaries", CompanionVerification.CatalogBoundaries);
        await Test("Agent IO BindingSecretsAndArgumentBoundaries", AgentIOVerification.BindingSecretsAndArgumentBoundaries);
        await Test("Screen sharing policy and typed wire boundaries", ScreenShareVerification.PolicyAndWireBoundaries);
        await Test("Screen sharing clipboard bounds and replay", ScreenShareVerification.ClipboardBoundsAndReplay);
        await Test("Screen sharing enrollment challenge and response ordering", ScreenShareVerification.EnrollmentChallengeAndResponseOrdering);
        await Test("Screen sharing revocation and input safety", ScreenShareVerification.RevocationAndInputSafety);
        await Test("Screen sharing enrollment race and session limits", ScreenShareVerification.EnrollmentRaceAndSessionLimits);
        await Test("Screen sharing teardown survives relay and settings failures", ScreenShareVerification.TeardownIgnoresBrokenRelayAndPersistence);
        await Test("Screen reference scene phase timing and frozen frame", ScreenReferenceSceneVerification.TimingAndStillFrame);
        await Test("Agent IO StdioMcpContract", AgentIOVerification.StdioMcpContract);
        await Test("Agent IO AuthenticatedPipeRejectsRevocation", AgentIOVerification.AuthenticatedPipeRejectsRevocation);
        await Test("Agent IO revocation cancels queued authenticated calls", AgentIORevocationVerification.RevocationCancelsAuthenticatedQueuedRequest);
        await Test("Agent URL independent choices and thirty-second fallback", AgentWebOpenVerification.IndependentChoicesAndFallback);
        await Test("Agent URL revocation, cancellation and shutdown", AgentWebOpenVerification.RevocationCancellationAndShutdown);
        await Test("StyleSurfacesVerification ApprovalAndReset", StyleSurfacesVerification.ApprovalAndReset);
        await Test("StyleSurfacesVerification MetadataAndFeatureState", StyleSurfacesVerification.MetadataAndFeatureState);
        await Test("StyleSurfacesVerification TrustedRequestProjectionAndLockedRegistry", StyleSurfacesVerification.TrustedRequestProjectionAndLockedRegistry);
        await Test("Login recovery authentication errors exclude ordinary content and external credentials", CliLoginRecoveryVerification.ErrorSignalsExcludeContentAndExternalProviders);
        await Test("Login recovery bounded partial URLs and pasted-code prompts", CliLoginRecoveryVerification.OutputHandlesPartialUrlsAndCodePrompts);
        await Test("Login recovery latest request generation and one-shot retry", CliLoginRecoveryVerification.RetryGenerationPreservesAttachmentsAndDropsStaleWork);
        await Test("Login recovery requires fresh successful sign-in", CliLoginRecoveryVerification.WaitRequiresFreshSuccessWhenAlreadySignedIn);
        await Test("Login recovery automatic start once per provider with cooldown", CliLoginRecoveryVerification.AutomaticStartOncePerProviderWithCooldown);
        await Test("Login recovery Antigravity sign-in failures, and only a signed-out status asks for a sign-in", CliLoginRecoveryVerification.GeminiSignalsAndMethods);
        await Test("Login recovery Antigravity sign-in is its status reading signed in", CliLoginRecoveryVerification.AntigravitySignInIsItsStatusReadingSignedIn);
        await Test("Login recovery Antigravity fake CLI run: the sign-in message on stderr is the reason, the same words as an answer are not", CliLoginRecoveryVerification.GeminiFakeCliRunRaisesTheReasonFromStderrOnly);
        await Test("Login recovery Claude fake CLI run: a result repeating the marked sign-in failure keeps the reason, whatever its words", CliLoginRecoveryVerification.ClaudeFakeCliRunKeepsTheReasonWhenTheResultRepeatsTheMarkedFailure);
        // The `gemini` provider runs Antigravity CLI (agy), ported from the Mac's AntigravityTests and AntigravityGraphTests.
        await Test("antigravity: agy's stream is read for replies, tool rows, resume and usage", AntigravityVerification.TheStreamIsReadForRepliesToolsResumeAndUsage);
        await Test("antigravity: results, declined actions, failures and notices", AntigravityVerification.ResultsDeniedActionsFailuresAndNotices);
        await Test("antigravity: arguments, the stdin line and attachments", AntigravityVerification.ArgumentsStdinAndAttachments);
        await Test("antigravity: the model list, labels, install and a fake agy's status, update and models", AntigravityVerification.TheModelListLabelsAndInstall);
        await Test("antigravity: saved Gemini CLI panes are moved once", AntigravityVerification.ASavedGeminiCliPaneIsMovedOnce);
        await Test("antigravity: a run draws the request, its sub-agents' work and the latest answer", AntigravityVerification.ARunDrawsTheRequestItsSubagentsWorkAndTheLatestAnswer);
        await Test("antigravity: a sub-agent waiting for its helper is not done until it answers", AntigravityVerification.ASubagentWaitingForItsHelperIsNotDoneUntilItAnswers);
        await Test("antigravity: the watcher follows growing transcripts and nested sub-agents", AntigravityVerification.TheWatcherFollowsGrowingTranscriptsAndNestedSubagents);
        await Test("antigravity: older requests are read from the conversation's transcript", AntigravityVerification.OlderRequestsAreReadFromTheConversationTranscript);
        await Test("antigravity: the conversations to continue and the skills", AntigravityVerification.TheConversationListAndSkills);
        // Antigravity permission cards, ported from the Mac's AntigravityPermissionTests over the shared fixture.
        await Test("antigravity cards: every fixture turn gives its actions", AntigravityPermissionVerification.EveryFixtureTurnGivesItsActions);
        await Test("antigravity cards: without a card the older notice stays", AntigravityPermissionVerification.WithoutACardTheOlderNoticeStays);
        await Test("antigravity cards: two turns in one run give two cards and nothing carries over", AntigravityPermissionVerification.TwoTurnsInOneRunGiveTwoCardsAndNothingCarriesOver);
        await Test("antigravity cards: rules are well formed or absent", AntigravityPermissionVerification.RulesAreWellFormedOrAbsent);
        await Test("antigravity cards: which rules a card may offer follows the shared cases", AntigravityPermissionVerification.WhichRulesACardMayOfferFollowsTheSharedCases);
        await Test("antigravity cards: settings edits change only the allow list, byte for byte", AntigravityPermissionVerification.SettingsEditsChangeOnlyTheAllowList);
        await Test("antigravity cards: settings of an unexpected shape are refused", AntigravityPermissionVerification.SettingsThatAreNotTheExpectedShapeAreRefused);
        await Test("antigravity cards: a temporary rule is added, recorded and taken out leaving every other byte", AntigravityPermissionVerification.ATemporaryRuleIsAddedRecordedAndTakenOutLeavingEveryOtherByte);
        await Test("antigravity cards: a rule two panes hold stays until the last is done", AntigravityPermissionVerification.ARuleTwoPanesHoldStaysUntilTheLastIsDone);
        await Test("antigravity cards: a rule the settings already had is never removed", AntigravityPermissionVerification.ARuleTheSettingsAlreadyHadIsNeverRemoved);
        await Test("antigravity cards: settings that cannot be read are left alone and refused", AntigravityPermissionVerification.SettingsThatCannotBeReadAreLeftAloneAndRefused);
        await Test("antigravity cards: a missing file is created with the rule alone and goes with it", AntigravityPermissionVerification.AMissingFileIsCreatedWithTheRuleAloneAndGoesWithIt);
        await Test("antigravity cards: a failed removal is tried again and kept in the ledger until it works", AntigravityPermissionVerification.AFailedRemovalIsTriedAgainAndKeptInTheLedgerUntilItWorks);
        await Test("antigravity cards: a rule agy writes back is taken out again", AntigravityPermissionVerification.ARuleAgyWritesBackIsTakenOutAgain);
        await Test("antigravity cards: after quit nothing is added and a link to nothing is left alone", AntigravityPermissionVerification.AfterQuitNothingIsAddedAndALinkToNothingIsLeftAlone);
        await Test("antigravity cards: a continuation that cannot start brings its card back with the reason", AntigravityPermissionVerification.AContinuationThatCannotStartBringsItsCardBack);
        await Test("antigravity cards: what a crash left is removed at the next launch", AntigravityPermissionVerification.WhatACrashLeftIsRemovedAtTheNextLaunch);
        await Test("antigravity cards: quit releases everything and a linked file stays linked", AntigravityPermissionVerification.QuitReleasesEverythingAndALinkedFileStaysLinked);
        await Test("antigravity cards: a card stays until a later run ends or it is answered", AntigravityPermissionVerification.ACardStaysUntilALaterRunEndsOrItIsAnswered);
        await Test("antigravity cards: an allow continues with the rules and a deny does nothing", AntigravityPermissionVerification.AnAllowContinuesWithTheRulesAndADenyDoesNothing);
        await Test("antigravity cards: the card rides a run event and an unknown kind reads as other", AntigravityPermissionVerification.TheCardRidesARunEventAndAnUnknownKindReadsAsOther);
        await Test("antigravity cards: the phone sees the card as a permission an older phone can answer", AntigravityPermissionVerification.ThePhoneSeesTheCardAsAPermissionAnOlderPhoneCanAnswer);
        await Test("antigravity cards: the /v2 samples are the Mac's own and pass the client's checks", AntigravityPermissionVerification.TheRemoteSamplesAreTheMacsOwnAndPassTheClientsChecks);
        await Test("antigravity cards: the phone sample is what this host sends", AntigravityPermissionVerification.ThePhoneSampleIsWhatTheMacSends);
        await Test("antigravity cards: the runner sends the card before the run's end", AntigravityPermissionVerification.TheRunnerSendsTheCardBeforeTheRunsEnd);
        await Test("antigravity cards: a pane's continuation holds the rule for its run and keeps the title", AntigravityPermissionVerification.APanesContinuationHoldsTheRuleForItsRunAndKeepsTheTitle);
        await Test("antigravity cards: a continuation the settings refuse never starts", AntigravityPermissionVerification.AContinuationTheSettingsRefuseNeverStarts);
        await Test("antigravity cards: the phone answers a card on the permission route", AntigravityPermissionVerification.AntigravityCardsAreAnsweredOnThePermissionRoute);
        await Test("antigravity cards: a remote pane's host puts the rules in its own settings and refuses forged ones", AntigravityPermissionVerification.ARemotePanesHostPutsTheRulesInItsOwnSettingsAndRefusesForgedOnes);
        await Test("Agent IO TerminalCleanerAndEphemeralOwnership", AgentIOVerification.TerminalCleanerAndEphemeralOwnership);
        if (OperatingSystem.IsWindows()) await Test("Agent IO real ConPTY process ownership", AgentIOVerification.RealWindowsInteractiveProcess);
        else { skipped++; Console.WriteLine("SKIP Agent IO real ConPTY process (requires Windows)"); }
        // Agent delegation (Core half): docs/help/content/ko/delegation.md, ported from the Mac's Delegation*Tests.
        await Test("state: a save starts at most every 1.5 s and is never pushed back for ever", StateSaveVerification.TheScheduleKeepsTheDebounceAndTheGapAndNeverWaitsForEver);
        await Test("delegation: child states, events and the only moves between them", DelegationVerification.StatesAndMoves);
        await Test("delegation: reason codes, records on the wire and the request key", DelegationVerification.NamesRecordsAndRequestKey);
        await Test("delegation: the file saves, restores, refuses at 4 MiB and waits out a reader", DelegationVerification.FileStoreSavesRestoresAndRefuses);
        await Test("delegation: TASK and REPORT copies are cut at 64 KiB as stored", DelegationVerification.CopiesAreCutAt64KiB);
        await Test("delegation: pruning goes oldest first and keeps what it must", DelegationVerification.PruningGoesOldestFirstAndKeepsWhatItMust);
        await Test("delegation: a new child needs room, and saving near the cap prunes", DelegationVerification.ANewChildNeedsRoomAndSavingNearTheCapPrunes);
        await Test("delegation: the switch, the gates and the six tools", DelegationVerification.SwitchGatesAndTheSixTools);
        await Test("delegation: the sidebar tree, rows and card actions", DelegationVerification.SidebarTreeRowsAndCards);
        await Test("delegation: child panes, the run ledger and the pane's snapshot keys", DelegationVerification.PanesLedgerAndSnapshotKeys);
        await Test("delegation: worktrees are made beside the checkout, on a flat branch, with ignored notes", DelegationGitVerification.WorktreesAreMadeBesideTheCheckout);
        await Test("delegation: a workspace that cannot host a child is refused before anything is made", DelegationGitVerification.WorkspacesThatCannotHostAChildAreRefused);
        await Test("delegation: the tool's merge fast-forwards or refuses, changing nothing", DelegationGitVerification.TheToolMergeFastForwardsOrRefuses);
        await Test("delegation: the card merges with at most one merge commit, and undoes", DelegationGitVerification.TheCardMergesAndUndoes);
        await Test("delegation: cleanup never forces, and discard removes what is left", DelegationGitVerification.CleanupNeverForcesAndDiscardDoes);
        await Test("delegation: Windows paths, line endings, long paths and folders held open", DelegationGitVerification.WindowsPathsLineEndingsAndOpenHandles);
        await Test("delegation: delegate starts a child in its own worktree, once per call, three at most", DelegationCoordinatorVerification.DelegateStartsAChildInItsOwnWorktree);
        await Test("delegation: delegate refuses before anything is made, and a failed start is kept for a human", DelegationCoordinatorVerification.DelegateRefusesBeforeAnythingIsMade);
        await Test("delegation: run ends record report revisions and send one notice each", DelegationCoordinatorVerification.RunEndsRecordReportRevisions);
        await Test("delegation: the gates, discard_human_only, the status tools and the server's protocol", DelegationCoordinatorVerification.TheGatesDiscardAndStatusTools);
        await Test("delegation: the merge tool takes turns, and follow-ups stop at two", DelegationCoordinatorVerification.TheMergeToolAndFollowUps);
        await Test("delegation: a card merges, undoes and discards, in the app's language", DelegationCoordinatorVerification.CardsMergeUndoAndDiscard);
        await Test("delegation: closing stores copies before cleanup, and a discard that fails says so", DelegationCoordinatorVerification.ClosingAndDiscardingThroughTheCoordinator);
        await Test("delegation: a child whose run would not end is never cleaned up", DelegationCoordinatorVerification.AChildWhoseRunWouldNotEndIsNeverCleanedUp);
        await Test("delegation: the file near its cap, and the switch turned off", DelegationCoordinatorVerification.StoragePressureAndTheSwitchTurnedOff);
        await Test("delegation: notices are steered, start the next run, or are held", DelegationDeliveryVerification.NoticesAreSteeredQueuedOrHeld);
        await Test("delegation: held items are released by a send or run next, once", DelegationDeliveryVerification.HeldItemsAreReleasedByASendOrRunNext);
        await Test("delegation: pending items are offered again and never overtaken", DelegationDeliveryVerification.PendingItemsAreOfferedAgainAndNeverOvertaken);
        await Test("delegation: launch holds what was pending, and a closed parent's notices stay on the cards", DelegationDeliveryVerification.LaunchHoldsWhatWasPendingAndClosedParentsKeepTheirNotices);
        await Test("delegation: follow-ups reach their child exactly once across launches", DelegationDeliveryVerification.FollowUpsReachTheirChildOnce);
        await Test("delegation: the binding attaches the server only to a Claude run with the switch on", DelegationAppVerification.TheBindingAttachesTheServerOnlyToAClaudeRunWithTheSwitchOn);
        await Test("delegation: the pipe carries delegation calls and answers as the Mac does", DelegationAppVerification.ThePipeCarriesDelegationCallsAndAnswersAsTheMacDoes);
        await Test("delegation: the headless server lists the six tools and forwards over the pipe", DelegationAppVerification.TheHeadlessServerListsTheSixToolsAndForwardsOverThePipe);
        await Test("delegation: the real run path attaches the server and runs a child in its worktree", DelegationAppVerification.TheRealRunPathAttachesTheServerAndRunsAChildInItsWorktree);
        await Test("delegation: the desktop service hosts delegation and offers the window its entry points", DelegationAppVerification.TheServiceHostsDelegationAndOffersTheWindowItsEntryPoints);
        await Test("delegation: a junction in a child worktree never reaches what it points to", DelegationAppVerification.AJunctionInAChildWorktreeNeverReachesWhatItPointsTo);
        await Test("delegation: the window says each state, action and answer in the app's language", DelegationAppVerification.TheWindowSaysEachStateActionAndAnswerInTheAppsLanguage);
        await Test("delegation: a host that throws is a host that did not take the call", DelegationAppVerification.AHostThatThrowsIsAHostThatDidNotTakeTheCall);
        await Test("delegation: git ends on its deadline, and reports and the file read the Windows way", DelegationAppVerification.GitEndsOnItsDeadlineAndReadsTheWindowsWay);
        await Test("delegation: the second send opens a worktree only where a child can work", DelegationHumanChildVerification.TheSecondSendChoosesAWorktreeOnlyWhereAChildCanWork);
        await Test("delegation: each agent has its modes, its words and its way to commit", DelegationHumanChildVerification.EachAgentHasItsModesAndItsWayToCommit);
        await Test("delegation: records and panes carry the human's mark and the parent's agent", DelegationHumanChildVerification.RecordsAndPanesCarryTheMarkAndTheParentsAgent);
        await Test("delegation: the human's send makes a child that never notifies its parent", DelegationHumanChildVerification.TheSendMakesAChildThatNeverNotifiesItsParent);
        await Test("delegation: the parent's tools and caps leave the human's child alone", DelegationHumanChildVerification.TheParentsToolsAndCapsLeaveTheHumansChildAlone);
        await Test("delegation: the app commits a Codex or Antigravity child's work", DelegationHumanChildVerification.TheAppCommitsACodexOrAntigravityChildsWork);
        await Test("delegation: a card merge that conflicts asks the child once per report", DelegationHumanChildVerification.ACardMergeThatConflictsAsksTheChildOncePerReport);
        await Test("delegation: git says why it gave no result, and only its own lock goes", DelegationHumanChildVerification.GitSaysWhyItGaveNoResultAndOnlyItsOwnLockGoes);
        await Test("delegation: every agent's modes and widest child modes are native/contracts/fixtures/delegation-modes.json", DelegationCrossAgentVerification.TheSharedModeTablesHold);
        await Test("delegation: agents are named by their words, and modes compare within and across agents", DelegationCrossAgentVerification.AgentsAndModesAcrossAgents);
        await Test("delegation: what the app knows of each CLI, the worktree menu and its cap", DelegationCrossAgentVerification.AvailabilityTheMenuAndItsCap);
        await Test("delegation: delegate takes an optional agent, and every answer names the child's agent", DelegationCrossAgentVerification.TheToolTakesAnOptionalAgentAndNamesItEverywhere);
        await Test("delegation: a child's words follow its agent and name a Claude parent as one", DelegationCrossAgentVerification.AChildsWordsFollowItsAgent);
        await Test("delegation: the human's child of another agent takes that agent's last-used model and effort", DelegationCrossAgentVerification.AChildOfAnotherAgentTakesThatAgentsDefaults);
        await Test("delegation: a Claude parent's Codex child gets the worktree and the app's commit", DelegationCrossAgentVerification.AClaudeParentsCodexChildGetsTheAppsCommit);
        await Test("delegation: a Codex parent's Claude child commits its own work", DelegationCrossAgentVerification.ACodexParentsClaudeChildCommitsItsOwnWork);
        await Test("delegation: an unknown or unavailable agent, or a mode it cannot take, is refused and nothing is made", DelegationCrossAgentVerification.TheWrongAgentOrModeIsRefusedAndNothingIsMade);
        await Test("delegation: the cap counts every agent, and the same call for another agent is another child", DelegationCrossAgentVerification.TheCapCountsEveryAgent);
        await Test("delegation: the human's menu starts a child of the agent it names, in that agent's widest mode", DelegationCrossAgentVerification.TheHumansMenuStartsTheNamedAgentsChild);
        await Test("delegation: the human's menu says why another agent cannot run the text", DelegationCrossAgentVerification.TheHumansMenuSaysWhy);
        await Test("delegation: the human's child of the pane's own agent never asks about its CLI", DelegationCrossAgentVerification.ThePanesOwnAgentIsNeverAskedAbout);
        await Test("delegation: the service offers the worktree menu and starts another agent's child", DelegationCrossAgentVerification.TheServiceOffersTheMenuAndStartsAnotherAgentsChild);
        await Test("delegation scenario S1: two children, a human's approval, a fast-forward and a discard", DelegationScenarioVerification.S1);
        await Test("delegation scenario S2: held notices released together by a send or run next after a relaunch", DelegationScenarioVerification.S2);
        await Test("delegation scenario S3: a diverged child's held follow-ups reach it once after a relaunch", DelegationScenarioVerification.S3);
        await Test("styles Conformance", StylesVerification.Conformance);
        await Test("styles: a style that is not bundled cannot take a bundled style's name", StylesVerification.ReservedNames);
        await Test("styles: the app knows no built-in feature, and the rules that name one are refused in a fixed order", StylesVerification.BuiltInFeatures);
        await Test("styles EvaluationAndTrust", StylesVerification.EvaluationAndTrust);
        await Test("styles: every manifest, bundled style and test fixture projects its golden panel in full", StyleGoldenVerification.EveryStyleMatchesItsGolden);
        await Test("styles: the golden's serialisation rules are fixed", StyleGoldenVerification.TheSerialisationRulesAreFixed);
        await Test("styles: removing a user style forgets the decision made for it", StylesVerification.RemovalForgetsTheDecision);
        await Test("styles: a style's own tools are allowed request by request, never past a revocation", StylesVerification.AutoAllowIsJudgedAtEachRequest);
        await Test("styles: a run allows its style's tools until the style is revoked", StylesVerification.ARunAllowsItsStyleToolsUntilTheStyleIsRevoked);
        await Test("styles: which file changes make a pane read its style again", StylesVerification.ChangesThatConcernAStyle);
        if (OperatingSystem.IsWindows()) await Test("styles: the change watcher reports file changes, a folder made later and one taken away", StylesVerification.ChangeWatcherReportsFileChanges);
        else { skipped++; Console.WriteLine("SKIP styles: the change watcher (requires Windows directory notifications)"); }
        await Test("questionnaire progress walks the questions and produces valid answers", QuestionnaireProgressVerification.ProgressWalksQuestionsAndProducesValidAnswers);
        await Test("questionnaire: a style's panel takes the waiting question and leaves the rest to the card", QuestionnaireProgressVerification.TheStylePanelTakesTheWaitingQuestion);
        await Test("companion bubble answers questions: tap, back, next and send", QuestionnaireProgressVerification.ThePetAnswersFromItsBubble);
        await Test("companion bubble buttons for an approval and a finished plan", CompanionVerification.BubbleButtons);
        await Test("companion bubble draws the whole plan as blocks", CompanionVerification.PlanBlocks);
        await Test("plan card: a pending plan's answers have one place", PlanCardVerification.ThePlanAnswersHaveOnePlace);
        await Test("styles StateBoundaries", StylesVerification.StateBoundaries);
        await Test("claude plan style: manifest and v6 validation", ClaudePlanStyleVerification.ManifestAndValidation);
        await Test("claude plan style: stage rules, chips, lines and the plan-mode launch", ClaudePlanStyleVerification.StageRules);
        await Test("claude plan style: run-state widgets match the shared golden", ClaudePlanStyleVerification.WidgetsMatchTheGolden);
        await Test("claude plan style: background tasks and the strip outside the style", ClaudePlanStyleVerification.TasksAndBackgroundStrip);
        await Test("claude plan style: queue, phone sends, stop and the stored mode", ClaudePlanStyleVerification.QueueAndStoredMode);
        await Test("desktop parity NewPanesInheritChoicesWithoutConversationIdentity", DesktopParityVerification.NewPanesInheritChoicesWithoutConversationIdentity);
        await Test("desktop parity GitStatusParsesOnlyLocalRepositoryMetadata", DesktopParityVerification.GitStatusParsesOnlyLocalRepositoryMetadata);
        await Test("desktop parity TranscriptAndResultFilesResolveTheSameSafePreview", DesktopParityVerification.TranscriptAndResultFilesResolveTheSameSafePreview);
        await Test("desktop parity ReferencePreviewsRefuseEscapesAndFinalSymlinks", DesktopParityVerification.ReferencePreviewsRefuseEscapesAndFinalSymlinks);
        await Test("Codex approval SettingsAndHandshake", CodexApprovalVerification.SettingsAndHandshake);
        await Test("Codex approval ApprovalScopeAndOnceOnly", CodexApprovalVerification.ApprovalScopeAndOnceOnly);
        await Test("Codex approval FileChangesRequireCompleteDiff", CodexApprovalVerification.FileChangesRequireCompleteDiff);
        await Test("Codex approval CancellationMalformedAndBounds", CodexApprovalVerification.CancellationMalformedAndBounds);
        await Test("Codex approval CompletionAndLegacyEvents", CodexApprovalVerification.CompletionAndLegacyEvents);
        await Test("Codex approval ActualRunManagerProtocol", CodexApprovalVerification.ActualRunManagerProtocol);
        if (OperatingSystem.IsWindows()) await Test("ConPTY persistent shell, resize and deterministic shutdown", PseudoTerminalVerification.PersistentShellAndShutdown);
        else { skipped++; Console.WriteLine("SKIP ConPTY persistent shell (requires Windows)"); }
        await Test("structured tool identity, duration, Mods dedup and cancellation", ActivityUsageVerification.Activities);
        await Test("Codex/Antigravity direct usage snapshots and intact long Markdown", ActivityUsageVerification.ProviderUsage);
        await Test("Claude current context, cumulative totals and quota observation age", ActivityUsageVerification.ClaudeContext);
        await Test("a leftover background task's report does not end the real request", ActivityUsageVerification.LeftoverTaskResult);
        await Test("optional metadata recovery, elapsed checkpoints and workspace state", ActivityUsageVerification.Persistence);
        await Test("authenticated structured Mods output and usage boundaries", ActivityUsageVerification.Mods);
        await Test("pane docking, split geometry, normalization and layout persistence", PaneLayoutVerification.Run);
        await Test("execution settings wire, permissions, fast/search/network overrides", SettingsVerification.Run);
        await Test("Claude Auto permissions, supported runtime gate and legacy-safe persistence", AutoPermissionVerification.Run);
        await Test("attachment validation, Unicode wire, safe staging and multimodal CLI payloads", AttachmentVerification.Run);
        await Test("wire settings include explicit nullable limits; provider validation", async () =>
        {
            var json = JsonSerializer.Serialize(new RunSettings(), Wire.Json); Check(json.Contains("\"maxTurns\":null") && json.Contains("\"maxBudgetUsd\":null"));
            await Reject(() => Task.FromResult(new StartRunRequest("p", "w", "claude", "x", [], Provider: "gemini", Settings: new("high")).Validate()));
            await Reject(() => Task.FromResult(new StartRunRequest("p", "w", "claude", "x", [], Provider: "codex", Settings: new(PermissionMode: "plan")).Validate()));
            Check(ProviderCatalog.Arguments(new("p", "w", "claude", "secret", [], "sonnet", "claude", new("high", "manual", 3, 1.5)), "/plugin").Contains("--effort"));
            Check(ProviderCatalog.Efforts("codex", "default", ProviderCatalog.Fallback("codex")).Length == 0); Check(!ProviderCatalog.SupportsMods("2.1.263")); Check(ProviderCatalog.SupportsMods("2.1.271 (Claude Code)"));
        });
        await Test("Codex and Antigravity are beta, Claude is official, and Name never carries the badge", () =>
        {
            Check(!ProviderCatalog.IsBeta("claude") && ProviderCatalog.IsBeta("codex") && ProviderCatalog.IsBeta("gemini") && !ProviderCatalog.IsBeta("unknown"), "beta providers");
            Check(Wire.Providers.Select(ProviderCatalog.Name).SequenceEqual(new[] { "Claude Code", "Codex CLI", "Antigravity CLI" }), "provider names");
            var badge = Locale.Get("badge.beta"); Check(badge is "베타" or "Beta", "badge text " + badge);
            Check(Locale.Get("badge.betaAccessibility") is "베타 기능" or "Beta feature", "badge accessibility text");
            Check(ProviderCatalog.BetaLabel("claude", "Claude Code") == "Claude Code", "official label untouched");
            Check(ProviderCatalog.BetaLabel("codex", "Codex CLI") == "Codex CLI · " + badge && ProviderCatalog.BetaLabel("gemini", "Antigravity CLI") == "Antigravity CLI · " + badge, "beta labels");
            var title = Locale.Get("window.title.betaTemplate", new Dictionary<string, string> { ["app"] = "Mighty Claude" }); Check(title is "Mighty Claude (베타)" or "Mighty Claude (Beta)", "window title " + title);
            var version = Locale.Get("settings.appUpdate.betaVersionTemplate", new Dictionary<string, string> { ["version"] = "0.2.8" }); Check(version is "0.2.8 (베타)" or "0.2.8 (Beta)", "version line " + version);
            return Task.CompletedTask;
        });
        await Test("beta badge: only a Codex or Antigravity agent pane's sidebar row and tab carry the capsule", () =>
        {
            Check(!ProviderCatalog.ShowsBetaBadge(new RunSession { Provider = "claude" }), "claude agent pane");
            Check(ProviderCatalog.ShowsBetaBadge(new RunSession { Provider = "codex" }) && ProviderCatalog.ShowsBetaBadge(new RunSession { Provider = "gemini" }), "codex and gemini agent panes");
            foreach (var kind in new[] { "shell", "browser", FilePaneKind.Kind })
                Check(!ProviderCatalog.ShowsBetaBadge(new RunSession { Kind = kind, Provider = "codex" }), kind + " pane carries no badge");
            Check(!ProviderCatalog.ShowsBetaBadge(new RunSession { Provider = "unknown" }), "an unknown provider is not beta");
            var codex = new RunSession { Provider = "codex", Title = ProviderCatalog.Name("codex") };
            Check(ProviderCatalog.ShowsBetaBadge(codex) && codex.Title == "Codex CLI", "the badge sits beside the title, never in it");
            return Task.CompletedTask;
        });
        await Test("native profile imports a copy, preserves settings and drafts", async () =>
        {
            var legacy = Temp(); var native = Temp();
            try
            {
                var workspace = new Workspace { Path = legacy }; var pane = new RunSession { WorkspaceId = workspace.Id, Provider = "codex", Model = "gpt-6-astra", Settings = new("high", "acceptEdits", FastMode: true, WebSearch: "cached", NetworkAccess: true), Status = "running", Draft = "한글 draft" };
                var original = JsonSerializer.Serialize(new AppSnapshot { Workspaces = [workspace], Sessions = [pane], Theme = "light", Layout = "columns" }, Wire.Json); await File.WriteAllTextAsync(Path.Combine(legacy, "workspace-state.json"), original);
                var store = new StateStore(native, legacy); var state = await store.LoadAsync(); Check(state.Sessions[0].Status == "stopped" && state.Sessions[0].Draft == pane.Draft && state.Sessions[0].Settings.Effort == "high");
                await store.SaveAsync(state); Check(await File.ReadAllTextAsync(Path.Combine(legacy, "workspace-state.json")) == original);
                var restored = await new StateStore(native).LoadAsync(); Check(restored.Sessions[0].Settings == pane.Settings, "Selected composer settings must survive profile restart.");
                await Reject(() => store.SaveAsync(state with { Workspaces = [workspace with { Path = Path.GetTempPath() }] }));
            }
            finally { Directory.Delete(legacy, true); Directory.Delete(native, true); }
        });
        await Test("saved remote workspaces are dropped with their panes and layout entries", async () =>
        {
            var directory = Temp(); var native = Temp();
            try
            {
                // Old state: one local workspace and one with a "remote" key, panes and layout entries in both.
                // The remote path is fully qualified on every OS, so only the "remote" key can drop it.
                var local = JsonSerializer.Serialize(directory, Wire.Json); var far = JsonSerializer.Serialize(Path.Combine(directory, "far"), Wire.Json);
                var legacy = "{\"version\":1,\"workspaces\":[{\"id\":\"local\",\"name\":\"Local\",\"path\":" + local + "},{\"id\":\"far\",\"name\":\"Far\",\"path\":" + far + ",\"remote\":{\"connectionId\":\"conn1\",\"workspaceId\":\"w1\",\"hostName\":\"Peer\"}}],"
                    + "\"sessions\":[{\"id\":\"s1\",\"workspaceId\":\"local\"},{\"id\":\"s2\",\"workspaceId\":\"local\",\"kind\":\"shell\"},{\"id\":\"r1\",\"workspaceId\":\"far\"},{\"id\":\"r2\",\"workspaceId\":\"far\",\"kind\":\"shell\"}],"
                    + "\"activeWorkspaceId\":\"far\",\"activeSessionId\":\"r1\","
                    + "\"paneLayouts\":{\"local\":{\"id\":\"l\",\"kind\":\"tabs\",\"sessionIds\":[\"s1\",\"s2\"]},\"far\":{\"id\":\"f\",\"kind\":\"tabs\",\"sessionIds\":[\"r1\",\"r2\"]}},"
                    + "\"paneLayoutModes\":{\"local\":\"tabs\",\"far\":\"custom\"},\"paneLayoutActiveSessionIds\":{\"local\":\"s2\",\"far\":\"r1\"}}";
                // Written as an old profile, so the first-run import path is the one that drops it.
                await File.WriteAllTextAsync(Path.Combine(directory, "workspace-state.json"), legacy);
                var store = new StateStore(native, directory); var state = await store.LoadAsync();
                Check(state.Workspaces.Select(w => w.Id).SequenceEqual(["local"]), "Remote workspace must be dropped");
                Check(state.Sessions.Select(s => s.Id).SequenceEqual(["s1", "s2"]), "Remote panes must be dropped");
                Check(state.ActiveWorkspaceId == "local" && state.ActiveSessionId == "s1", "Active ids fall back to the local workspace");
                Check(state.PaneLayouts!.Keys.SequenceEqual(["local"]) && state.PaneLayoutModes!.Keys.SequenceEqual(["local"]) && state.PaneLayoutActiveSessionIds!.Keys.SequenceEqual(["local"]), "Layout entries keyed by the remote workspace must be dropped");
                await Reject(() => Task.FromResult(store.GetWorkspace("far")));
                await store.SaveAsync(state); var saved = await File.ReadAllTextAsync(Path.Combine(native, "workspace-state.json"));
                Check(!saved.Contains("\"far\"") && !saved.Contains("\"remote\""), "Saved state must not keep the remote workspace");
                Check(await File.ReadAllTextAsync(Path.Combine(directory, "workspace-state.json")) == legacy, "The old profile is left untouched");
            }
            finally { Directory.Delete(directory, true); Directory.Delete(native, true); }
        });
        await Test("remote strings: every Windows-only string resolves", RemoteStringsVerification.EveryWindowsOnlyStringResolves);
        await Test("remote strings: host OS labels cover every platform", RemoteStringsVerification.HostOSLabelsCoverEveryPlatform);
        await Test("remote state: the shared reference cases decide what is kept", RemoteWorkspaceStateVerification.SharedReferenceCasesDecideWhatIsKept);
        await Test("remote state: version 2 loads as remote and older references are dropped", RemoteWorkspaceStateVerification.VersionTwoLoadsAsRemoteAndOlderReferencesAreDropped);
        await Test("remote state: a local workspace is never written or read as remote", RemoteWorkspaceStateVerification.ALocalWorkspaceIsNeverWrittenOrReadAsRemote);
        await Test("remote wire: every body is the Mac's shape", RemoteWireFixtureVerification.EveryBodyIsTheMacsShape);
        await Test("remote wire: the index names every route and every file is accounted for", RemoteWireFixtureVerification.TheIndexNamesEveryRouteAndEveryFileIsAccountedFor);
        await Test("remote wire: every sample is accepted by the Windows checks", RemoteWireFixtureVerification.EverySampleIsAcceptedByTheWindowsChecks);
        await Test("remote wire: bodies are read as strictly as the Mac reads them", RemoteWireFixtureVerification.BodiesAreReadAsStrictlyAsTheMacReadsThem);
        await Test("remote wire: /info refuses what the Mac refuses", RemoteWireFixtureVerification.InfoRefusesWhatTheMacRefuses);
        await Test("remote wire: the host key never leaves the state", RemoteWireFixtureVerification.TheHostKeyNeverLeavesTheState);
        await Test("remote wire: every wire event reads and writes back", RemoteWireFixtureVerification.EveryWireEventReadsAndWritesBack);
        await Test("remote wire: each wire event is refused alone, as the Mac refuses it", RemoteWireFixtureVerification.EachWireEventIsRefusedAloneAsTheMacRefusesIt);
        await Test("remote wire: timestamps, images and pending requests are bounded", RemoteWireFixtureVerification.TimestampsImagesAndPendingRequestsAreBounded);
        await Test("remote client: saved connections keep no key", RemoteClientVerification.SavedConnectionsKeepNoKey);
        await Test("remote client: keys stay in the vault", RemoteClientVerification.KeysStayInTheVault);
        await Test("remote client: addresses parse with the default port", RemoteClientVerification.AddressesParseWithTheDefaultPort);
        await Test("remote client: saved addresses read as the Mac reads them", RemoteClientVerification.SavedAddressesReadAsTheMacReadsThem);
        await Test("remote client: only Tailscale peers, loopback only when injected", RemoteClientVerification.OnlyTailscalePeersAndLoopbackWhenInjected);
        await Test("remote client: one authenticated request follows no redirect and reads at most 2 MiB", RemoteClientVerification.OneAuthenticatedRequestFollowsNoRedirectAndReadsAtMostTwoMebibytes);
        await Test("remote client: hosts resolve only to Tailscale", RemoteClientVerification.HostsResolveOnlyToTailscale);
        await Test("remote client: connections are saved without their key and read it on first use", RemoteClientVerification.ConnectionsAreSavedWithoutTheirKeyAndReadItOnFirstUse);
        await Test("remote client: no connection leaves this PC's tunnel", RemoteClientVerification.NoConnectionLeavesThisPCsTunnel);
        await Test("remote client: a connections file held open is read later and never written over", RemoteClientVerification.AConnectionsFileHeldOpenIsReadLaterAndNeverWrittenOver);
        await Test("remote client: a connect waits for a held connections file and keeps the file's ids", RemoteClientVerification.AConnectWaitsForAHeldConnectionsFile);
        await Test("remote client: the host is read strictly and a key the vault refuses stays in memory", RemoteClientVerification.TheHostIsReadStrictlyAndAKeyTheVaultRefusesStaysInMemory);
        await Test("remote client: text is cut at whole characters", RemoteClientVerification.TextIsCutAtWholeCharacters);
        await Test("remote client: overlapping changes keep the newer state", RemoteClientVerification.OverlappingChangesKeepTheNewerState);
        await Test("remote client: a run posts the Mac's request and applies the host's events as the pane's", RemoteClientVerification.ARunPostsTheMacsRequestAndAppliesTheHostsEventsAsThePanes);
        await Test("remote client: answers go to the host run and a stop reaches the host", RemoteClientVerification.AnswersGoToTheHostRunAndAStopReachesTheHost);
        await Test("remote client: one unusable event is skipped and a gap is said", RemoteClientVerification.OneUnusableEventIsSkippedAndAGapIsSaid);
        await Test("remote client: a busy host is asked again for a while, then the connection is lost", RemoteClientVerification.ABusyHostIsAskedAgainForTenSecondsAndThenTheConnectionIsLost);
        await Test("remote client: unsupported runs and a refused key never run", RemoteClientVerification.UnsupportedRunsAndARefusedKeyNeverRun);
        await Test("remote client: a malformed event is dropped on its own", RemoteClientVerification.AMalformedEventIsDroppedOnItsOwn);
        await Test("remote client: a route outside the tunnel ends the connection, and a stale run never fails a newer one", RemoteClientVerification.ARouteOutsideTheTunnelEndsTheConnectionAndAStaleRunNeverFailsANewerOne);
        await Test("remote client: only a passing failure is asked again", RemoteClientVerification.OnlyAPassingFailureIsAskedAgain);
        await Test("remote client: a lost connection ends its runs with its reason cut", RemoteClientVerification.ALostConnectionEndsItsRunsWithItsReasonCut);
        await Test("remote client: closing the app stops its runs on the host", RemoteClientVerification.ClosingTheAppStopsItsRunsOnTheHost);
        await Test("remote client: a remote pane keeps the newest requests and answers with the host's run id", RemoteClientVerification.ARemotePaneKeepsTheNewestRequestsAndAnswersWithTheHostsRunId);
        await Test("remote client: a stop waits for the poll being applied and is the pane's last word", RemoteClientVerification.AStopWaitsForThePollBeingAppliedAndIsThePanesLastWord);
        await Test("remote client: no run starts or outlives its connection while its stops are on their way", RemoteClientVerification.NoRunStartsOrOutlivesItsConnectionWhileItsStopsAreOnTheirWay);
        await Test("remote client: a callback that throws ends the run", RemoteClientVerification.ACallbackThatThrowsEndsTheRun);
        await Test("remote client: a disconnect from inside an event ends the poll there", RemoteClientVerification.ADisconnectFromInsideAnEventEndsThePollThere);
        await Test("remote client: a disconnect while a poll is in flight ends the pane stopped", RemoteClientVerification.ADisconnectWhileAPollIsInFlightEndsThePaneStopped);
        await Test("remote client: a pane that throws during a remove leaves the others their last word and the key goes", RemoteClientVerification.APaneThatThrowsDuringARemoveLeavesTheOthersTheirLastWordAndTheKeyGoes);
        await Test("remote host: a refused peer is cut off at accept and one peer holds at most its share", RemoteHostVerification.ARefusedPeerIsCutOffAtAcceptAndOnePeerHoldsAtMostItsShare);
        await Test("remote host: no body is read without leave and the head is bounded", RemoteHostVerification.NoBodyIsReadWithoutLeaveAndTheHeadIsBounded);
        await Test("remote host: a slow or stuck request is cut off and stopping closes everything", RemoteHostVerification.ASlowOrStuckRequestIsCutOffAndStoppingClosesEverything);
        await Test("remote host: a throwing filter or handler never stops the listener", RemoteHostVerification.AThrowingFilterOrHandlerNeverStopsTheListener);
        await Test("remote host: a draining connection holds its peer's slot until the drain ends", RemoteHostVerification.ADrainingConnectionHoldsItsPeersSlotUntilTheDrainEnds);
        await Test("remote host: all peers together hold at most the open-connection cap", RemoteHostVerification.AllPeersTogetherHoldAtMostTheOpenConnectionCap);
        await Test("remote host: commands run in PowerShell from the system folder and graph nodes reach the host", RemoteHostVerification.CommandsRunInPowerShellFromTheSystemFolderAndGraphNodesReachTheHost);
        await Test("remote host: only a keyed run request gets the attachment body", RemoteHostVerification.OnlyAKeyedRunRequestGetsTheAttachmentBody);
        await Test("remote host: sharing starts with a new key and a stop revokes it", RemoteHostVerification.SharingStartsWithANewKeyAndStopRevokesIt);
        await Test("remote host: a flood is refused and the refusal carries the version", RemoteHostVerification.AFloodIsRefusedAndTheRefusalCarriesTheVersion);
        await Test("remote host: a listener that stops accepting turns sharing off", RemoteHostVerification.AListenerThatStopsAcceptingTurnsSharingOff);
        await Test("remote host: a stop while a start binds closes what it opened", RemoteHostVerification.AStopWhileAStartBindsClosesWhatItOpened);
        await Test("remote host: a stop while a start makes its runner disposes that runner", RemoteHostVerification.AStopWhileAStartMakesItsRunnerDisposesThatRunner);
        await Test("remote host: the run count is told outside the host's lock and a failing listener of it changes nothing", RemoteHostVerification.TheRunCountIsToldOutsideTheHostsLockAndAFailingListenerOfItChangesNothing);
        await Test("remote host: sharing refuses what it cannot serve", RemoteHostVerification.SharingRefusesWhatItCannotServe);
        await Test("remote loopback: commands and full access wait for the switch, then run on the host", RemoteLoopbackVerification.CommandsAndFullAccessWaitForTheSwitchThenRunOnTheHost);
        await Test("remote loopback: a Windows host refuses Codex and Antigravity runs until the switch is on", RemoteLoopbackVerification.AWindowsHostRefusesCodexAndAntigravityUntilTheSwitchIsOn);
        await Test("remote loopback: tool requests are answered from the client and graph runs go only where asked", RemoteLoopbackVerification.ToolRequestsAreAnsweredFromTheClientAndGraphRunsGoOnlyWhereAsked);
        await Test("remote loopback: unshared folders, busy hosts and oversize requests are refused", RemoteLoopbackVerification.UnsharedFoldersBusyHostsAndOversizeRequestsAreRefused);
        await Test("remote loopback: a run nobody polls is stopped and a stopped host ends the client's runs", RemoteLoopbackVerification.ARunNobodyPollsIsStoppedAndAStoppedHostEndsTheClientsRuns);
        await Test("remote loopback: a real command runs in the shared folder and stops", RemoteLoopbackVerification.ARealCommandRunsInTheSharedFolderAndStops);
        await Test("remote loopback: an old stop's run count never lands after a newer one", RemoteLoopbackVerification.AnOldStopsRunCountNeverLandsAfterANewerOne);
        await Test("remote loopback: answers reach only their own job's requests", RemoteLoopbackVerification.AnswersReachOnlyTheirOwnJobsRequests);
        await Test("remote loopback: a stop before the runner takes the run still ends it", RemoteLoopbackVerification.AStopBeforeTheRunnerTakesTheRunStillEndsIt);
        await Test("remote loopback: stopping sharing cancels a launch not begun", RemoteLoopbackVerification.StoppingSharingCancelsALaunchNotBegun);
        await Test("remote loopback: the buffer keeps the newest events within its bounds and says the gap", RemoteLoopbackVerification.TheBufferKeepsTheNewestEventsWithinItsBoundsAndSaysTheGap);
        await Test("remote desktop: a remote pane runs through the client and the phone never sees it", RemoteDesktopVerification.ARemotePaneRunsThroughTheClientAndThePhoneNeverSeesIt);
        await Test("remote desktop: the sharing host runs only in shared folders with no pane tools", RemoteDesktopVerification.TheSharingHostRunsOnlyInSharedFoldersWithNoPaneTools);
        await Test("remote desktop: remote panes keep to the host's features", RemoteDesktopVerification.RemotePanesKeepToTheHostsFeatures);
        await Test("remote desktop: a closed parent's child is never placed in a remote workspace", RemoteDesktopVerification.AClosedParentsChildIsNeverPlacedInARemoteWorkspace);
        await Test("remote desktop: the firewall rows say when the rule lacks a port in use", RemoteDesktopVerification.TheFirewallRowsSayWhenTheRuleLacksAPortInUse);
        await Test("remote desktop: no client code runs under the service's lock, and a stop while connecting ends the run once", RemoteDesktopVerification.NoClientCodeRunsUnderTheServicesLock);
        await Test("remote desktop: a stop during the host's accept never ends the next send", RemoteDesktopVerification.AStopDuringTheHostsAcceptNeverEndsTheNextSend);
        await Test("remote desktop: a send to a workspace the host no longer shares is refused", RemoteDesktopVerification.ASendToAWorkspaceTheHostNoLongerSharesIsRefused);
        await Test("remote desktop: the host reads this PC's CLIs once, and again when stale, failed or forgotten", RemoteDesktopVerification.TheHostReadsThisPCsCLIsOnceAndAgainWhenStaleFailedOrForgotten);
        await Test("remote desktop: the window reads a workspace's folder only through LocalPath", RemoteDesktopVerification.TheWindowReadsAWorkspacesFolderOnlyThroughLocalPath);
        await Test("remote desktop: the remote page copies the key secretly and host runs hold updates", RemoteDesktopVerification.TheRemotePageCopiesTheKeySecretlyAndHostRunsHoldUpdates);
        await Test("remote desktop: the window names a remote host and keeps its panes to it", RemoteDesktopVerification.TheWindowNamesARemoteHostAndKeepsItsPanesToIt);
        await Test("corrupt/unknown/oversize state cannot be overwritten by shutdown", async () =>
        {
            foreach (var text in new[] { "null", "{", "{\"version\":2,\"workspaces\":[],\"sessions\":[]}", "{\"version\":1,\"workspaces\":null,\"sessions\":[]}" })
            {
                var directory = Temp(); try { var path = Path.Combine(directory, "workspace-state.json"); await File.WriteAllTextAsync(path, text); var store = new StateStore(directory); await Reject(() => store.LoadAsync()); await Reject(() => store.SaveAsync(new())); Check(await File.ReadAllTextAsync(path) == text); } finally { Directory.Delete(directory, true); }
            }
            var source = Temp(); var target = Temp(); try { await File.WriteAllBytesAsync(Path.Combine(source, "workspace-state.json"), new byte[8 * 1024 * 1024 + 1]); await Reject(() => new StateStore(target, source).LoadAsync()); Check(!File.Exists(Path.Combine(target, "workspace-state.json"))); } finally { Directory.Delete(source, true); Directory.Delete(target, true); }
        });
        await Test("stream parser keeps text after thinking, errors latch and dedupe is bounded", () =>
        {
            var output = new List<string>(); var parser = new OutputParser("claude", (_, value) => output.Add(value), _ => { });
            parser.Parse("{\"type\":\"assistant\",\"message\":{\"id\":\"a\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"private\"}]}}"); parser.Parse("{\"type\":\"assistant\",\"message\":{\"id\":\"a\",\"content\":[{\"type\":\"text\",\"text\":\"visible\"}]}}"); Check(output.SequenceEqual(["visible"]));
            var gemini = new OutputParser("gemini", (_, _) => { }, _ => { }); gemini.Parse("{\"event\":\"result\",\"result\":{\"status\":\"ERROR\"}}"); gemini.Parse("{\"event\":\"result\",\"result\":{\"status\":\"WAITING\"}}"); Check(gemini.Failed);
            var codex = new OutputParser("codex", (_, _) => { }, _ => { }); codex.Parse(JsonSerializer.Serialize(new { type = "item.completed", item = new { id = "a", type = "agent_message", text = new string('x', 100000) } })); var seen = (HashSet<string>)typeof(OutputParser).GetField("seen", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(codex)!; Check(seen.Single().Length < 200); return Task.CompletedTask;
        });
        await Test("Claude/Codex metadata protocol sends no user prompt or model turn", async () =>
        {
            var directory = Temp(); try
            {
                foreach (var provider in new[] { "claude", "codex" })
                {
                    var record = Path.Combine(directory, provider); var catalog = await ProviderCatalog.ReadModelsAsync(provider, Self("--fake-cli", provider, record)); Check(catalog.Source == "cli" && catalog.Models.Count == 2, provider + " metadata");
                    var input = await File.ReadAllTextAsync(record + ".input"); Check(!input.Contains("turn/start") && !input.Contains("\"type\":\"user\""));
                    var arguments = await File.ReadAllTextAsync(record + ".args"); if (provider == "claude") Check(arguments.Contains("--no-session-persistence") && arguments.Contains("--safe-mode") && input.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1);
                }
            } finally { Directory.Delete(directory, true); }
        });
        await Test("provider prompt goes to stdin; shell output and cancellation execute real children", async () =>
        {
            var directory = Temp(); var workspace = new Workspace { Path = directory }; var record = Path.Combine(directory, "fake"); var events = new ConcurrentQueue<RunEvent>();
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Self("--fake-cli", "codex", record)));
            await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue);
            try
            {
                var prompt = "stdin only ` & $() 한글"; await manager.StartAsync(new("provider", workspace.Id, "claude", prompt, [], Provider: "codex")); await Until(() => events.Any(e => e.SessionId == "provider" && e.Status == "completed")); Check(await File.ReadAllTextAsync(record + ".prompt") == prompt); Check(!(await File.ReadAllTextAsync(record + ".args")).Contains(prompt));
                await manager.StartAsync(Shell("echo", workspace, "echo MIGHTY_NATIVE_OK")); await Until(() => events.Any(e => e.SessionId == "echo" && e.Status == "completed")); Check(events.Any(e => e.SessionId == "echo" && e.Entry?.Text.Contains("MIGHTY_NATIVE_OK") == true));
                var pid = Path.Combine(directory, "pid"); await manager.StartAsync(Shell("long", workspace, LongCommand(pid))); await Until(() => File.Exists(pid)); var processId = int.Parse(await File.ReadAllTextAsync(pid)); await manager.StopAsync("long"); Check(events.Any(e => e.SessionId == "long" && e.Status == "stopped")); await Until(() => !Alive(processId));
            }
            finally { await manager.DisposeAsync(); Directory.Delete(directory, true); }
        });
        await Test("fake CLI receives attachment bytes and staging is removed on completion, error and stop", async () =>
        {
            var directory = Temp(); var workspace = new Workspace { Path = directory }; var events = new ConcurrentQueue<RunEvent>(); var files = new[] { AttachmentSupport.Make("image.png", AttachmentVerification.Png), AttachmentSupport.Make("source.cs", "attachment-fixture-contents"u8.ToArray()) };
            try
            {
                var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin")); await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
                foreach (var provider in Wire.Providers)
                {
                    var record = Path.Combine(directory, provider); await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Self("--fake-cli", provider, record, "--verify-attachments"))); await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, events.Enqueue);
                    await manager.StartAsync(new(provider, workspace.Id, "claude", "", [], Provider: provider, Attachments: files)); await Until(() => !manager.IsRunning(provider));
                    Check(events.Any(e => e.SessionId == provider && e.Status == "completed"), provider + " attachment run did not complete");
                    var args = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(record + ".args"), Wire.Json)!;
                    var flag = provider == "codex" ? "--image" : "--add-dir";
                    var path = args[Array.IndexOf(args, flag) + 1]; if (provider == "codex") path = Path.GetDirectoryName(path)!;
                    Check(!Directory.Exists(path), provider + " staging cleanup");
                    var prompt = await File.ReadAllTextAsync(record + ".prompt");
                    var captured = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(record + ".attachments"), Wire.Json)!;
                    Check(captured.Count == files.Length, provider + " child did not read every staged attachment");
                    if (provider == "claude")
                    {
                        using var json = JsonDocument.Parse(prompt); var content = json.RootElement.GetProperty("message").GetProperty("content").EnumerateArray().ToArray();
                        Check(content.Single(c => c.GetProperty("type").GetString() == "image").GetProperty("source").GetProperty("data").GetString() == files[0].DataBase64, "Claude inline image bytes changed");
                        prompt = content.Single(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
                    }
                    // Antigravity reads its prompt as one stream-json line.
                    if (provider == "gemini")
                    {
                        Check(prompt.EndsWith('\n') && prompt.Count(c => c == '\n') == 1, "Antigravity's prompt is one stdin line");
                        using var json = JsonDocument.Parse(prompt); Check(json.RootElement.GetProperty("event").GetString() == "user", "Antigravity's prompt is a user event");
                        prompt = json.RootElement.GetProperty("message").GetProperty("content").GetString()!;
                    }
                    // References are JSON strings (Windows separators must be decoded before comparing paths), Antigravity's plain paths.
                    foreach (var attachment in provider == "claude" ? files.Skip(1) : files)
                    {
                        var referencedPath = AttachmentReferencePath(prompt, attachment.Name, provider == "gemini");
                        Check(Path.GetDirectoryName(referencedPath) == path, provider + " reference escaped its staged directory");
                        Check(captured.TryGetValue(referencedPath, out var bytes) && bytes == attachment.DataBase64, provider + " referenced bytes changed before CLI consumption");
                        if (provider == "codex" && attachment.MediaType.StartsWith("image/", StringComparison.Ordinal)) Check(args[Array.IndexOf(args, "--image") + 1] == referencedPath, "Codex image argument and reference differ");
                    }
                    Check(!args.Contains(files[0].DataBase64), provider + " attachment bytes leaked into argv");
                }
                foreach (var behavior in new[] { "--hold-run", "--fail-run" })
                {
                    var record = Path.Combine(directory, behavior); await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Self("--fake-cli", "codex", record, behavior))); await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, events.Enqueue); var id = Wire.Id();
                    await manager.StartAsync(new(id, workspace.Id, "claude", "", [], Provider: "codex", Attachments: files)); await Until(() => File.Exists(record + ".prompt")); var args = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(record + ".args"), Wire.Json)!; var stage = Path.GetDirectoryName(args[Array.IndexOf(args, "--image") + 1])!;
                    if (behavior == "--hold-run") { Check(Directory.Exists(stage)); await manager.StopAsync(id); } else await Until(() => !manager.IsRunning(id));
                    Check(!Directory.Exists(stage)); Check(events.Any(e => e.SessionId == id && e.Status == (behavior == "--hold-run" ? "stopped" : "error")));
                }
                await using var absent = Absent(); await using var unavailable = new RunManager(_ => Task.FromResult(workspace), absent, plugin, events.Enqueue);
                await Reject(() => unavailable.StartAsync(new("no-cli", workspace.Id, "claude", "", [], Provider: "codex", Attachments: files))); Check(!unavailable.IsRunning("no-cli"));
                var pending = new TaskCompletionSource<Workspace>(TaskCreationOptions.RunContinuationsAsynchronously); var cancelled = new RunManager(_ => pending.Task, absent, plugin, events.Enqueue); var start = cancelled.StartAsync(new("cancel-attach", workspace.Id, "claude", "", [], Provider: "codex", Attachments: files)); var closing = cancelled.DisposeAsync().AsTask(); pending.SetResult(workspace); await Reject(() => start); await closing;
            }
            finally { Directory.Delete(directory, true); }
        });
        await Test("pending stop and disposal prevent a late child launch", async () =>
        {
            var directory = Temp(); var workspace = new Workspace { Path = directory }; var pending = new TaskCompletionSource<Workspace>(TaskCreationOptions.RunContinuationsAsynchronously); var events = new ConcurrentQueue<RunEvent>(); await using var catalog = Absent();
            var manager = new RunManager(_ => pending.Task, catalog, "", events.Enqueue); await manager.StartAsync(Shell("pending", workspace, "echo NEVER")); var shutdown = manager.DisposeAsync().AsTask(); pending.SetResult(workspace); await shutdown; Check(events.All(e => e.Entry?.Text != "NEVER")); await Reject(() => manager.StartAsync(Shell("late", workspace, "echo NEVER")));
            Directory.Delete(directory, true);
        });
        await Test("desktop routing retains cancellation before manager admission", async () =>
        {
            var directory = Temp(); var workspacePath = Temp(); await using var catalog = Absent(); var service = new DesktopService(directory, null, "", catalog);
            try
            {
                await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath); var pane = new RunSession { WorkspaceId = workspace.Id, Kind = "shell" }; await service.UpdateAsync(s => s with { Sessions = [pane] });
                service.RunEventReceived += value => { if (value.Status == "running") _ = service.StopAsync(value.SessionId); };
                await service.StartAsync(Shell(pane.Id, workspace, "echo SHOULD_NOT_RUN")); Check(service.Snapshot.Sessions[0].Status == "stopped"); Check(service.Snapshot.Sessions[0].Logs.All(l => l.Kind != "output"));
            }
            finally { await service.DisposeAsync(); Directory.Delete(directory, true); Directory.Delete(workspacePath, true); }
        });
        await Test("slash query parsing", SlashCommandVerification.QueryParsing);
        await Test("slash argument query", SlashCommandVerification.ArgumentQuery);
        await Test("slash builtins per provider", SlashCommandVerification.BuiltinsPerProvider);
        await Test("slash filter", SlashCommandVerification.Filter);
        await Test("next actions match every shared Mac and phone vector", NextActionsVerification.SharedContract);
        await Test("next actions invalidate old replies and preserve drafts", NextActionsVerification.LatestReplyAndDraftSafety);
        await Test("plan mode requests match every shared Mac vector", ClaudePlanModeVerification.SharedRequests);
        await Test("plan mode answers match every shared Mac vector", ClaudePlanModeVerification.SharedResponses);
        await Test("plan mode answers settle once and keep every plan", ClaudePlanModeVerification.ChannelAnswersOnceAndKeepsEveryPlan);
        await Test("plan mode checklists match every shared Mac vector", ClaudePlanModeVerification.SharedChecklists);
        await Test("plan mode background tasks match every shared Mac vector", ClaudePlanModeVerification.SharedBackgroundRuns);
        await Test("plan mode approval sets the pane mode and keeps a bounded history", ClaudePlanModeVerification.PaneKeepsThePlanModeAndHistory);
        await Test("plan mode state round-trips and old snapshots still load", ClaudePlanModeVerification.SavedStateRoundTripsAndOldSnapshotsLoad);
        await Test("plan mode runner answers a fake CLI's plan and reports its checklist and background work", ClaudePlanModeVerification.RunnerAnswersAPlanAndReportsProgress);
        await Test("plan mode stopping a pane waiting on background work ends everything", ClaudePlanModeVerification.StoppingAPaneWaitingOnBackgroundWorkEndsEverything);
        await Test("plan card helpers pick the plan, place it and read its outcome", PlanCardVerification.Helpers);
        await Test("plan card takes the result's place in the diagram", PlanCardVerification.LayoutPlacesThePlanCard);
        await Test("plan block fits the pane like the newest result", PlanCardVerification.PlanFitsThePaneLikeTheResult);
        await Test("plan answers sit in the composer area while the diagram draws the plan", PlanCardVerification.ComposerShowsTheAnswersAndThePlanSizeIsKept);
        await Test("plan mode approval never lowers the pane's own mode", ClaudePlanModeVerification.ApprovalNeverLowersThePaneMode);
        await Test("plan mode per-run override launches in plan and is never saved", ClaudePlanModeVerification.PerRunPlanOverride);
        await Test("plan mode history keeps new plans whole within its budget", ClaudePlanModeVerification.HistoryKeepsNewPlansWholeWithinItsBudget);
        await Test("slash frontmatter", SlashCommandVerification.Frontmatter);
        await Test("slash discovery from temp home and workspace", SlashCommandVerification.Discovery);
        await Test("slash 400 cap", SlashCommandVerification.CapAt400);
        await Test("slash palette opens on a query with matches", SlashPaletteVerification.Opens);
        await Test("slash palette closes with no match or no query", SlashPaletteVerification.Closes);
        await Test("slash palette highlight wraps and clamps", SlashPaletteVerification.HighlightMoves);
        await Test("slash palette chooses a plain command", SlashPaletteVerification.ChoosesPlainCommand);
        await Test("slash palette chooses an app action", SlashPaletteVerification.ChoosesAppAction);
        await Test("slash palette chooses an argument command", SlashPaletteVerification.ChoosesArgumentCommand);
        await Test("slash palette Esc closes without changing the draft", SlashPaletteVerification.EscapeClosesWithoutChangingTheDraft);
        await Test("slash palette rows and footer use the macOS copy", SlashPaletteVerification.RowsAndFooterUseMacCopy);
        await Test("slash palette leaves out app actions Windows cannot do", SlashPaletteVerification.LeavesOutActionsWindowsCannotDo);
        await Test("slash palette caps the list at 60 rows", SlashPaletteVerification.CapsRowsAt60);
        await Test("status line config follows Claude settings precedence and command-only entries", StatusLineVerification.ConfigFollowsPrecedenceAndOnlyCommandEntries);
        await Test("status line fingerprint matches macOS formula", StatusLineVerification.FingerprintMatchesMacOS);
        await Test("status line payload uses the CLI field names and transcript layout", StatusLineVerification.StatusLinePayloadUsesTheCLIsFieldNamesAndTranscriptLayout);
        await Test("status line payload nests cost and fast mode like the CLI", StatusLineVerification.StatusLinePayloadNestsCostAndFastModeLikeTheCLI);
        await Test("status line payload nests the context window like the CLI", StatusLineVerification.StatusLinePayloadNestsTheContextWindowLikeTheCLI);
        await Test("status line payload omits unknown sections like the CLI", StatusLineVerification.StatusLinePayloadOmitsUnknownSectionsLikeTheCLI);
        await Test("status line payload keys rate limits by canonical kind", StatusLineVerification.StatusLinePayloadKeysRateLimitsByCanonicalKind);
        await Test("status line payload has exactly the macOS top-level keys", StatusLineVerification.StatusLinePayloadHasExactlyTheMacOSTopLevelKeys);
        await Test("status line ANSI parsing keeps colours weight and strips other escapes", StatusLineVerification.AnsiParsingKeepsColoursWeightAndStripsEscapes);
        await Test("status line parses 256-colour and RGB like macOS", StatusLineVerification.AnsiParsingSupports256ColourAndRgbLikeMacOS);
        await Test("status line palette index maps to the xterm colours", StatusLineVerification.PaletteIndexMapsToXtermColours);
        await Test("status line trust rule requires fingerprint for workspace commands", StatusLineVerification.TrustRuleRequiresFingerprintForWorkspaceCommands);
        await Test("status line falls back to the user command while the workspace command is gated", StatusLineVerification.FallsBackToUserCommandWhileWorkspaceCommandIsGated);
        await Test("status line runner feeds stdin captures output and colour", StatusLineVerification.RunnerFeedsStdinCapturesOutputAndColour);
        await Test("status line runner enforces timeout", StatusLineVerification.RunnerEnforcesTimeout);
        await Test("status line shell follows Claude Code on Windows", StatusLineVerification.ShellFollowsClaudeCodeOnWindows);
        await Test("status line refresher debounces single run from rapid triggers", StatusLineVerification.StatusLineRefresherDebouncesSingleRunFromRapidTriggers);
        await Test("status line refresher generation counter discards stale results", StatusLineVerification.StatusLineRefresherGenerationCounterDiscardsStaleResult);
        await Test("status line refresher reruns when pending during a run", StatusLineVerification.StatusLineRefresherRerunsWhenPendingDuringARun);
        await Test("status line refresher ignores requests after close", StatusLineVerification.StatusLineRefresherIgnoresRequestsAfterClose);
        await Test("status line cancellation drains before replacement", StatusLineShutdownVerification.CloseCancelsAndDrainsBeforeReplacement);
        await Test("status line real process cancellation drains stderr", StatusLineShutdownVerification.RealCommandCancellationAndStderrDrain);
        await Test("status line refresher never starts two commands at once", StatusLineVerification.StatusLineRefresherNeverStartsTwoCommandsAtOnce);
        await Test("status line refresher falls back to user command while workspace command is gated", StatusLineVerification.StatusLineRefresherFallsBackToUserCommandWhileWorkspaceIsGated);
        await Test("status line refresher trust unblocks the workspace command", StatusLineVerification.StatusLineRefresherTrustUnblocksWorkspaceCommand);
        await Test("status line refresher shows nothing for a disabled or missing entry", StatusLineVerification.StatusLineRefresherShowsNothingForDisabledOrMissingEntry);
        await Test("cli runner runs a real short command", CliRunnerVerification.RealShortCommand);
        await Test("cli runner kills a process that exceeds the timeout", CliRunnerVerification.KillsCommandThatExceedsTimeout);
        await Test("cli runner reports the exit code and error output of a failing command", CliRunnerVerification.ReportsExitCodeAndErrorOutput);
        await Test("cli runner caps the output it captures", CliRunnerVerification.CapsTheCapturedOutput);
        await Test("cli runner keeps arguments and output out of the log unredacted", CliRunnerVerification.KeepsSecretsOutOfTheLog);
        await Test("cli runner kills the whole process group when a command times out", CliRunnerVerification.TimeoutKillsTheWholeProcessGroup);
        await Test("cli runner cancellation stops the command and its children", CliRunnerVerification.CancellationStopsTheCommandAndItsChildren);
        await Test("strings match macOS", StringsVerification.MatchMacOS);
        await Test("tool permission handshake sends initialize before the prompt", ToolPermissionVerification.HandshakeRunsBeforeThePrompt);
        await Test("tool permission handshake failure and timeout fail closed", ToolPermissionVerification.HandshakeFailureAndTimeoutFailClosed);
        await Test("tool permission 이번만 허용 returns the original input once", ToolPermissionVerification.AllowOnceReturnsTheOriginalInput);
        await Test("tool permission 거부 never returns allow rules or settings", ToolPermissionVerification.DenyNeverReturnsAllow);
        await Test("tool permission channel accepts only can_use_tool and validates identifiers", ToolPermissionVerification.OnlyCanUseToolIsAcceptedAndIdentifiersAreValidated);
        await Test("tool permission channel caps waiting requests at 16", ToolPermissionVerification.PendingRequestsAreCappedAtSixteen);
        await Test("tool permission input too large to show completely is denied", ToolPermissionVerification.InputTooLargeToShowIsDenied);
        await Test("tool permission questionnaires and extra screens can only be denied", ToolPermissionVerification.QuestionnairesAndExtraScreensCanOnlyBeDenied);
        await Test("tool permission stopping the run settles every waiting request", ToolPermissionVerification.StoppingTheRunSettlesEveryWaitingRequest);
        await Test("tool permission bar shows the title summary reason path and count", ToolPermissionVerification.BarShowsTheTitleSummaryReasonPathAndCount);
        await Test("tool permission host prompts are added only for a Claude launch that can show the bar", ToolPermissionVerification.HostPromptsOnlyWhereTheBarExists);
        await Test("tool permission run launches with host prompts over stdio and answers one request", ToolPermissionVerification.RunLaunchesWithHostPromptsAndAnswersOneRequest);
        await Test("tool permission strings match macOS", StringsVerification.ToolPermissionsMatchMacOS);
        await Test("completion notification fires once for running then completed", CompletionNotificationVerification.FiresOnceAfterRunning);
        await Test("completion notification title includes workspace name", CompletionNotificationVerification.TitleIncludesWorkspaceName);
        await Test("completion notification title is session alone without workspace", CompletionNotificationVerification.TitleIsSessionAloneWithoutWorkspace);
        await Test("completion notification does not fire for error or stopped", CompletionNotificationVerification.DoesNotFireForErrorOrStopped);
        await Test("completion notification does not fire for completed without running", CompletionNotificationVerification.DoesNotFireForCompletedWithoutRunning);
        await Test("completion notification does not fire twice for the same run", CompletionNotificationVerification.DoesNotFireTwiceForSameRun);
        await Test("completion notification does not fire when preference is off", CompletionNotificationVerification.DoesNotFireWhenPreferenceOff);
        await Test("completion notification strings match macOS", StringsVerification.CompletionNotificationStringsMatchMacOS);
        await Test("completion notification smoke sends one fixture call and records sent", CompletionNotificationVerification.SmokeSendsOneFixtureCallAndRecordsSent);
        await Test("completion notification smoke records skipped with a reason", CompletionNotificationVerification.SmokeRecordsSkippedWithAReason);
        await Test("completion notification smoke failure after support is a failure", CompletionNotificationVerification.SmokeFailureAfterSupportIsAFailure);
        await Test("completion notification smoke keeps the saved state version at 1", CompletionNotificationVerification.SmokeKeepsTheSavedStateVersion);
        await Test("cli update reports a CLI that is not installed as skipped and installs nothing", CliUpdateVerification.MissingCliIsSkipped);
        await Test("cli update uses Claude Code's own update command for a native install", CliUpdateVerification.NativeClaudeUsesItsOwnUpdateCommand);
        await Test("cli update upgrades exactly the one winget package it found", CliUpdateVerification.WingetUpgradesExactlyThePackageItFound);
        await Test("cli update updates only the official npm package in its own prefix", CliUpdateVerification.NpmUpdatesOnlyTheOfficialPackageInItsPrefix);
        await Test("cli update skips a prerelease npm channel with the macOS sentence", CliUpdateVerification.PrereleaseNpmChannelIsSkipped);
        await Test("cli update skips an install method it does not recognise", CliUpdateVerification.UnknownInstallMethodIsSkipped);
        await Test("cli update reports a failing installer with a bounded diagnostic output", CliUpdateVerification.FailingInstallerIsReportedWithBoundedOutput);
        await Test("cli update runs one at a time, reports busy and cancels cleanly", CliUpdateVerification.SecondRequestIsBusyAndCancelStopsTheRun);
        await Test("cli update start-up pass covers every provider only when the switch is on", CliUpdateVerification.StartupPassCoversEveryProviderOnlyWhenTheSwitchIsOn);
        await Test("cli update coordinator tracks state through a complete run", CliUpdateVerification.CoordinatorTracksStateAndRunsInOrder);
        await Test("cli update coordinator refuses a second start while running", CliUpdateVerification.CoordinatorRefusesSecondStartWhileRunning);
        await Test("cli update coordinator begins automatic only once and only when the switch is on", CliUpdateVerification.CoordinatorBeginsAutomaticOnlyOnceAndOnlyWhenSwitchIsOn);
        await Test("cli update coordinator cancel stops the run and shutdown refuses new work", CliUpdateVerification.CoordinatorCancelStopsRunAndShutdownRefusesNew);
        await Test("cli update coordinator shows each result while the button reads 업데이트 중…", CliUpdateVerification.CoordinatorShowsEachResultWhileTheButtonReadsUpdating);
        await Test("cli update coordinator reports a failed provider and still updates the rest", CliUpdateVerification.CoordinatorReportsAFailedProviderAndStillUpdatesTheRest);
        await Test("cli update coordinator keeps the last run's results and finish time", CliUpdateVerification.CoordinatorKeepsTheLastRunResultsAndFinishTime);
        await Test("cli update strings match macOS", StringsVerification.CliUpdateStringsMatchMacOS);
        await Test("plugin auto update preference and idle scheduling match macOS", PluginAutoUpdateVerification.DefaultPreferenceAndScheduling);
        await Test("plugin auto update respects Claude scope approval and budget", PluginAutoUpdateVerification.ClaudeScopeApprovalAndBudget);
        await Test("plugin auto update shares mutation exclusion and upgrades Codex Git marketplaces", PluginAutoUpdateVerification.CodexMarketplacesAndGlobalExclusion);
        await Test("settings preferences missing key keeps default off and version stays 1", SettingsPreferencesVerification.MissingKeyKeepsDefaultOff);
        await Test("settings preferences explicit on and off persist across state store reloads", SettingsPreferencesVerification.ExplicitOnAndOffPersistAcrossReloads);
        await Test("settings preferences only JSON booleans enable the setting and malformed values keep sessions", SettingsPreferencesVerification.OnlyJsonBooleansEnableSettingAndMalformedValuesKeepSessions);
        await Test("phase models legacy per-mode keys load", ModelDefaultsVerification.LegacyPerModeKeysLoad);
        await Test("phase models row rule matches macOS", PhaseModelVerification.RowRuleMatchesMacOS);
        await Test("phase models file store keeps unrelated keys", PhaseModelVerification.FileStoreKeepsUnrelatedKeys);
        await Test("phase models refuse unparseable file", PhaseModelVerification.RefuseUnparseableFile);
        await Test("phase models omc agent catalog", PhaseModelVerification.OmcAgentCatalog);
        await Test("phase models omc save writes only changed agents", PhaseModelVerification.OmcSaveWritesOnlyChangedAgents);
        await Test("phase models launch args merge one settings json", PhaseModelVerification.LaunchArgsMergeOneSettingsJson);
        await Test("phase models snapshot shares macOS field names", PhaseModelVerification.SnapshotSharesMacOSFieldNames);
        await Test("phase models section registered in the macOS order", PhaseModelVerification.SectionRegisteredInMacOrder);
        await Test("phase models section rows follow installed tools", PhaseModelVerification.SectionRowsFollowInstalledTools);
        await Test("phase models provider rows and version pins", PhaseModelPreferencesVerification.ProviderRowsAndVersionPins);
        await Test("phase models effort and arguments reach every launch path", PhaseModelPreferencesVerification.EffortAndArgumentsReachAllLaunchPaths);
        await Test("phase models persistence and local start snapshot", PhaseModelPreferencesVerification.PersistenceAndLocalStartSnapshot);
        await Test("settings sections appear in the macOS order with their titles", SettingsSectionsVerification.SectionsAppearInMacOrderWithTheirTitles);
        await Test("settings sections leave out features that are not on Windows yet", SettingsSectionsVerification.SectionsLeaveOutFeaturesNotOnWindowsYet);
        await Test("settings sections registering a section does not touch the others", SettingsSectionsVerification.RegisteringASectionDoesNotTouchTheOthers);
        await Test("settings sections smoke flips the CLI auto-update switch and restores it", SettingsSectionsVerification.SmokeFlipsTheAutoUpdateSwitchAndRestoresIt);
        await Test("settings sections smoke shows every fixture status and rejects a wrong screen", SettingsSectionsVerification.SmokeShowsEveryFixtureStatusAndRejectsAWrongScreen);
        await Test("remote settings: the remote page sits between mobile and companion", SettingsSectionsVerification.TheRemotePageSitsBetweenMobileAndCompanion);
        await Test("locale key leak detector flags exact keys", LocalizationVerification.LocaleKeyLeakDetectorFlagsExactKeys);
        await Test("locale key leak detector ignores non-key text", LocalizationVerification.LocaleKeyLeakDetectorIgnoresNonKeyText);
        await Test("locale key leak detector trims invisible characters", LocalizationVerification.LocaleKeyLeakDetectorTrimsInvisibleCharacters);
        await Test("files pane tree sorts folders first in natural order and keeps noise folders collapsed", FilesPaneVerification.TreeSortsFoldersFirstInNaturalOrderAndKeepsNoiseCollapsed);
        await Test("files pane listing stops at its ceiling and says it is truncated", FilesPaneVerification.ListingStopsAtItsCeilingAndSaysItIsTruncated);
        await Test("files pane refuses paths and links that leave the workspace", FilesPaneVerification.PathsAndLinksThatLeaveTheWorkspaceAreRefused);
        await Test("files pane classifies by name then by sniffing the first bytes", FilesPaneVerification.ClassificationUsesNamesThenSniffing);
        await Test("files pane text follows the byte order mark then UTF-8 then CP949", FilesPaneVerification.EncodingsFollowTheByteOrderMarkThenUtf8ThenCp949);
        await Test("files pane preview shows source, markdown and images with the macOS caps", FilesPaneVerification.PreviewLoaderShowsSourceMarkdownAndImagesWithTheMacCaps);
        await Test("files pane refuses svg that loads outside content", FilesPaneVerification.SvgThatLoadsOutsideContentIsRefused);
        await Test("files pane line starts and highlighting match macOS", FilesPaneVerification.LinesBreakAndHighlightingMatchMacOS);
        await Test("files pane filter searches only opened folders and caps results", FilesPaneVerification.FilterSearchesOnlyOpenedFoldersAndCapsResults);
        await Test("files pane opens with Ctrl+Shift+E, one per workspace, left of the current pane, never saved", FilesPaneVerification.PaneOpensWithCtrlShiftEOnePerWorkspaceLeftAndIsNeverSaved);
        await Test("files pane links to another machine are refused on paper", FilesPaneVerification.LinksToAnotherMachineAreRefusedOnPaper);
        await Test("files pane svg size is read from its root element and checked like pdf", FilesPaneVerification.SvgSizeIsReadFromItsRootElement);
        await Test("files pane on Windows draws at most 2,000 rows and 256 KB of source", FilesPaneVerification.TheWindowsPaneDrawsAtMostTwoThousandRowsAnd256KB);
        await Test("status glyph design A: each state has its own glyph", StatusGlyphVerification.EachStateHasItsOwnGlyph);
        await Test("status glyph design A: a pane that is not an agent's shows its own symbol only while idle", StatusGlyphVerification.APaneThatIsNotAnAgentsShowsItsOwnSymbolOnlyWhileIdle);
        await Test("status glyph design A: status strings and pending requests reach their glyphs", StatusGlyphVerification.StatusStringsReachTheirGlyphs);
        await Test("status glyph design A: only the spark turns and only wait and error are discs", StatusGlyphVerification.OnlyTheSparkTurnsAndOnlyWaitAndErrorAreDiscs);
        await Test("status glyph design A: glyph colours follow the macOS palette", StatusGlyphVerification.GlyphColoursFollowTheMacPalette);
        await Test("status glyph design A: glyph geometry matches the mockup", StatusGlyphVerification.GlyphGeometryMatchesTheMockup);
        await Test("status glyph design A: status words use the shared locale keys", StatusGlyphVerification.StatusWordsUseTheSharedLocaleKeys);
        // native/contracts/fixtures/design-tokens.json is the palette macOS (DesignTokenParityTests) shares.
        await Test("design tokens: every palette field equals native/contracts/fixtures/design-tokens.json in both themes", DesignTokenVerification.EveryFixtureHexEqualsThePaletteField);
        await Test("design tokens: derived tones, glyph and outline colours match the fixture", DesignTokenVerification.DerivedTonesAndGlyphsMatchTheFixture);
        await Test("design tokens: provider and Windows-only colours match the fixture", DesignTokenVerification.WindowsOnlyColoursMatchTheFixture);
        await Test("design tokens: dashes convert to WinUI stroke units ([9,7] at 2pt is [4.5,3.5])", DesignTokenVerification.DashesConvertToStrokeUnits);
        await Test("design tokens: metrics and opacities match the fixture", DesignTokenVerification.MetricsAndOpacitiesMatchTheFixture);
        await Test("design tokens: only the light theme setting picks the light palette", DesignTokenVerification.ThemeSettingPicksThePalette);
        await Test("design tokens: inks, fills and marks clear the macOS contrast rules", DesignTokenVerification.InksFillsAndMarksClearTheirContrast);
        // The text setting (M/MightyCore/TextAppearance.swift): at the standard values every size is today's.
        await Test("text appearance: the standard setting draws every base size exactly", TextAppearanceVerification.StandardDrawsEveryBaseSizeExactly);
        await Test("text appearance: sizes round to the half point and scales snap to the 85-150% steps", TextAppearanceVerification.SizesRoundToTheHalfPointAndScalesSnapToSteps);
        await Test("text appearance: stepping stops at both ends and keeps the families", TextAppearanceVerification.SteppingStopsAtBothEndsAndKeepsTheFamilies);
        await Test("text appearance: the conversation follows the interface font and code offers monospaced families", TextAppearanceVerification.TheConversationFollowsTheInterfaceUntilItHasItsOwn);
        await Test("text appearance: a name with a control character or line separator is no family", TextAppearanceVerification.ANameThatCouldBreakTheTerminalIsNoFamily);
        await Test("text appearance: registry values round-trip and the standard setting stores nothing", TextAppearanceVerification.TheRegistryValuesRoundTripAndTheStandardStoresNothing);
        await Test("design tokens: the launch splash reads the saved theme before the state loads", DesignTokenVerification.TheSplashReadsTheSavedThemeBeforeTheStateLoads);
        await Test("agent marks: only an agent's sidebar row names its provider, so only it carries the mark", ProviderMarkVerification.OnlyAnAgentRowNamesItsProviderSoOnlyItCarriesTheMark);
        await Test("agent marks: marks are the macOS outlines in their brand colours", ProviderMarkVerification.MarksAreTheMacOutlinesInTheirBrandColours);
        await Test("agent marks: right-side cards put the mark before the agent name their title ends with", ProviderMarkVerification.RightSideCardsMarkTheAgentNameTheirTitleEndsWith);
        await Test("add pane menu: 프로젝트 폴더 열기… comes last, after a separator", AddPaneMenuVerification.TheMenuEndsWithOpenProjectAfterASeparator);
        await Test("add pane menu: the sidebar open-folder button shows only when no workspace is listed", AddPaneMenuVerification.TheOpenFolderButtonShowsOnlyWhenNoWorkspaceIsListed);
        await Test("add pane menu: only Claude and Codex picked from the menu may ask 새로 시작 / 이어가기", AddPaneMenuVerification.OnlyClaudeAndCodexFromTheMenuMayAskToResume);
        await Test("agent images: Claude Read and MCP screenshot results become inline image entries without base64", AgentImageVerification.ClaudeReadAndMcpScreenshotResultsBecomeImageEntriesWithoutBase64);
        await Test("agent images: without a cache or for a sub-agent pictures stay out of the transcript", AgentImageVerification.WithoutACacheOrForASubAgentPicturesStayOut);
        await Test("agent images: refused pictures leave a notice and no entry", AgentImageVerification.RefusedPicturesLeaveANoticeAndNoEntry);
        await Test("agent images: Codex MCP results, image_view and image_generation become entries", AgentImageVerification.CodexMcpResultsImageViewAndImageGenerationBecomeEntries);
        await Test("agent images: headers give type and size, and the caps refuse oversize pictures", AgentImageVerification.HeadersGiveTypeAndSizeAndCapsRefuseOversizePictures);
        await Test("agent images: the cache writes once by hash, rewrites a damaged file and evicts the least recently used", AgentImageVerification.TheCacheWritesOnceByHashRewritesADamagedFileAndEvictsTheLeastRecentlyUsed);
        await Test("agent images: Markdown pictures outside code fences follow the workspace and temporary folder rule", AgentImageVerification.MarkdownPicturesOutsideCodeFencesFollowThePathRule);
        await Test("agent images: image entries survive saved state with references only", AgentImageVerification.ImageEntriesSurviveSavedStateWithReferencesOnly);
        await Test("agent images: the transcript draws a PNG thumbnail at most 480 x 640", AgentImageVerification.TheTranscriptDrawsAPngThumbnailAtMost480By640);
        await Test("agent images: a line carrying a picture over 1 MiB reaches the parser", AgentImageVerification.ALineCarryingAPictureOverOneMiBReachesTheParser);
        await Test("session history: scrolling up loads the ten requests above the retained one from a Claude record", SessionHistoryVerification.ScrollingUpLoadsTheTenRequestsAboveTheRetainedOneFromAClaudeRecord);
        await Test("session history: a replaced record starts over and a pane without a session has none", SessionHistoryVerification.AReplacedRecordStartsOverAndAPaneWithoutASessionHasNone);
        await Test("session history: only what the user typed is a request", SessionHistoryVerification.OnlyWhatTheUserTypedIsARequest);
        await Test("session history: Codex rollouts replay through the live tracker", SessionHistoryVerification.CodexRolloutsReplayThroughTheLiveTracker);
        await Test("session history: loaded runs stay bounded and follow a trim", SessionHistoryVerification.LoadedRunsStayBoundedAndFollowATrim);
        await Test("session history: the layout stacks loaded requests above without moving the retained one", SessionHistoryVerification.TheLayoutStacksLoadedRequestsAboveWithoutMovingTheRetainedOne);
        await Test("resume picker hides nested Ouroboros runs unless every session is shown", SessionHistoryVerification.ResumePickerHidesNestedOuroborosRunsUnlessEverySessionIsShown);
        await Test("resume picker lists only this folder's sessions, newest first, minus open panes", SessionHistoryVerification.ResumePickerListsOnlyThisFoldersSessionsNewestFirstMinusOpenPanes);
        await Test("resume picker continues the session in a new pane and remembers it", SessionHistoryVerification.ResumingContinuesTheSessionInANewPaneAndRemembersIt);
        // native/contracts/fixtures/help-site.json is the help address macOS and the phone share.
        await Test("help site: every language opens its folder of the shared base in native/contracts/fixtures/help-site.json", HelpSiteVerification.EveryLanguageOpensItsFolderOfTheSharedBase);
        await Test("help site: the current guide follows the chosen language", HelpSiteVerification.TheCurrentGuideFollowsTheChosenLanguage);
        // native/contracts/fixtures/model-labels.json is the label table macOS and the phone share.
        await Test("model labels: every shared vector in native/contracts/fixtures/model-labels.json labels as committed", ModelLabelVerification.EveryFixtureCaseLabelsAsCommitted);
        await Test("model labels: CLI catalogue rows carry their version while the values sent stay the aliases", ModelLabelVerification.CliCatalogueRowsCarryTheirVersion);
        await Test("model labels: the fallback catalogue never invents a version", ModelLabelVerification.FallbackCatalogueNeverInventsAVersion);
        await Test("model labels: a reported model labels only the selection it was reported for", ModelLabelVerification.AReportedModelLabelsOnlyTheSelectionItWasReportedFor);
        await Test("model labels: picks and displays differ only by picker marks", ModelLabelVerification.PicksAndDisplaysDifferOnlyByPickerMarks);
        await Test("model labels: the status line names the current selection", ModelLabelVerification.StatusLineNamesTheCurrentSelection);
        await Test("model labels: graph capsules and resume rows read versions", ModelLabelVerification.GraphCapsulesAndResumeRowsReadVersions);
        // native/contracts/graph-vectors.json is macOS truth; every group must
        // reproduce on Windows exactly as the Swift implementation produces it.
        await Test("graph vectors match macOS claude stream", GraphVectorVerification.ClaudeStream);
        await Test("graph vectors match macOS codex stream", GraphVectorVerification.CodexStream);
        await Test("graph vectors match macOS mods events", GraphVectorVerification.Mods);
        await Test("graph history bounds and restore match macOS", GraphVectorVerification.Bounds);
        await Test("graph layout matches macOS frames", GraphVectorVerification.Layout);
        await Test("graph camera anchors match macOS", GraphVectorVerification.Camera);
        await Test("graph capsule text matches macOS", GraphVectorVerification.Capsule);
        await Test("graph result files match macOS", GraphVectorVerification.Files);
        await Test("graph session fields share macOS names", GraphVectorVerification.SessionFields);
        await Test("graph runs recorded from a Windows run", GraphVectorVerification.RecordedFromARun);
        await Test("mighty switch shows only on claude and codex panes", MightyViewModelVerification.ShowsOnlyOnClaudeAndCodexPanes);
        await Test("mighty switch keeps run and draft", MightyViewModelVerification.SwitchKeepsRunAndDraft);
        await Test("mighty canvas blocks follow the layout", MightyViewModelVerification.CanvasBlocksFollowTheLayout);
        await Test("mighty capsule and tooltip match macOS", MightyViewModelVerification.CapsuleAndTooltipMatchMacOS);
        await Test("mighty zoom steps match macOS", MightyViewModelVerification.ZoomStepsMatchMacOS);
        await Test("mighty selection routes the wheel", MightyViewModelVerification.SelectionRoutesTheWheel);
        await Test("mighty result files panel rules match macOS", MightyViewModelVerification.ResultFilesPanelRulesMatchMacOS);
        await Test("mighty indicators respect animations off", MightyViewModelVerification.IndicatorsRespectAnimationsOff);
        await Test("mighty result box: a saved size larger than the pane is kept within it", ResultFitVerification.ASavedSizeLargerThanThePaneIsKeptWithinIt);
        await Test("mighty result box shrinks with the agent pane and grows back to its saved size", ResultFitVerification.TheCardShrinksWithThePaneAndGrowsBackToTheSavedSize);
        await Test("mighty result box: the pane limit is in diagram coordinates at the zoom", ResultFitVerification.TheLimitIsInDiagramCoordinatesAtTheZoom);
        await Test("mighty result box: a tiny pane keeps the block minimum", ResultFitVerification.ATinyPaneKeepsTheBlockMinimum);
        await Test("mighty result box: the layout carries the limits a drag uses", ResultFitVerification.TheLayoutCarriesTheLimitsADragUses);
        await Test("mighty result box: a live drag follows the cursor within the pane only", ResultFitVerification.ALiveDragFollowsTheCursorWithinThePaneOnly);
        await Test("mighty result box: releasing saves only the sides moved inside the pane", ResultFitVerification.ReleasingSavesOnlyTheSidesMovedInsideThePane);
        await Test("mighty result box: a saved card the pane bounds follows the pane on resize", ResultFitVerification.ASavedCardThePaneBoundsFollowsThePaneOnResize);
        await Test("mighty result box: the saved size is kept under the macOS field", ResultFitVerification.TheSavedSizeIsKeptUnderTheMacField);
        await Test("mighty result reveal: the newest result is as tall as its content up to its cap", ResultRevealVerification.TheNewestResultIsAsTallAsItsContentUpToItsCap);
        await Test("mighty result reveal: the saved size is a maximum for every new result", ResultRevealVerification.TheSavedSizeIsAMaximumForEveryNewResult);
        await Test("mighty result reveal: the cap is the visible pane", ResultRevealVerification.TheCapIsTheVisiblePane);
        await Test("mighty result reveal: shrinking to content never writes the saved size", ResultRevealVerification.ShrinkingToContentNeverWritesTheSavedSize);
        await Test("mighty result reveal: a shorter result moves nothing above it and pulls the draft up", ResultRevealVerification.AShorterResultMovesNothingAboveItAndPullsTheDraftUp);
        await Test("mighty result reveal: a new result sits right above the composer", ResultRevealVerification.ANewResultSitsRightAboveTheComposer);
        await Test("mighty result reveal: a request that finishes while watched is revealed once", ResultRevealVerification.ARequestThatFinishesWhileWatchedIsRevealedOnce);
        await Test("mighty result reveal: runs a pane opens or hydrates with are never revealed", ResultRevealVerification.RunsAPaneOpensOrHydratesWithAreNeverRevealed);
        await Test("mighty result reveal: an unmeasured card is held until its height arrives", ResultRevealVerification.AnUnmeasuredCardIsHeldUntilItsHeightArrives);
        await Test("mighty result reveal: the user's own scroll, zoom or draft ends the hold", ResultRevealVerification.TheUsersOwnScrollZoomOrDraftEndsTheHold);
        await Test("mighty running outline: running marches, waiting stays amber, finished has none", ActivityOutlineVerification.RunningMarchesWaitingStaysStillAndFinishedHasNone);
        await Test("mighty running outline: 9/7 dashes fit the card outline", ActivityOutlineVerification.DashesAreNineSevenAndFitTheOutline);
        await Test("mighty running outline: one period per 1.6 s, still under reduced motion", ActivityOutlineVerification.OnePeriodPassesEveryOnePointSixSecondsAndStaysStillUnderReducedMotion);
        await Test("mighty running outline: diagram blocks carry their outline", ActivityOutlineVerification.DiagramBlocksCarryTheirOutline);
        await Test("mighty running outline: colours are tokens and the dash counts in stroke widths", ActivityOutlineVerification.OutlinesReadTokensAndDashInStrokeUnits);
        await Test("mighty activity mark: four capsules in a wave", ActivityOutlineVerification.ActivityMarkIsFourCapsulesInAWave);
        await Test("mighty dot grid: 18pt × zoom from the camera offset", ActivityOutlineVerification.DotGridFollowsTheZoomAndTheOffset);
        await Test("mighty blocks: each carries the tone of its pill, strip and incoming edge", ActivityOutlineVerification.BlocksCarryTheirToneForThePillStripAndEdge);
        await Test("mighty view model exposes the WinUI surface", MightyViewModelVerification.ExposesWinUiSurface);
        await Test("mighty canvas lays out a nested delegation run", MightyViewModelVerification.LaysOutNestedDelegationRun);
        await Test("locale Korean file loads from shared locales path", LocalizationVerification.KoreanLocaleLoadsFromSharedFile);
        await Test("locale English file loads from shared locales path", LocalizationVerification.EnglishLocaleLoadsFromSharedFile);
        await Test("locale key absent from both catalogs returns the key itself", LocalizationVerification.MissingKeyInChosenLanguageFallsBackToKorean);
        await Test("locale placeholders are substituted when subs supplied", LocalizationVerification.PlaceholdersAreSubstituted);
        await Test("locale language picker keys exist with expected values", LocalizationVerification.LanguagePickerLocaleKeysExist);
        await Test("locale system language maps each OS language tag", LocalizationVerification.SystemLanguageMapsEachOSLanguageTag);
        await Test("locale system language takes the first supported preferred language", LocalizationVerification.SystemLanguageTakesTheFirstSupportedPreferredLanguage);
        await Test("locale lookup falls back to English then Korean", LocalizationVerification.LookupOrderFallsBackToEnglishThenKorean);
        await Test("locale system passes over a language whose catalogue has no keys", LocalizationVerification.SystemPassesOverALanguageWhoseCatalogueHasNoKeys);
        await Test("locale system checks the display language first", LocalizationVerification.SystemChecksTheDisplayLanguageFirst);
        await Test("locale language picker offers four languages", LocalizationVerification.LanguagePickerOffersFourLanguages);
        await Test("locale language preference defaults to system and persists through Normalize", LocalizationVerification.LanguagePreferencePersistsInSnapshot);
        await Test("cli account strings match macOS", StringsVerification.CliAccountStringsMatchMacOS);
        await Test("cli account claude and codex statuses expose account labels only", CliAccountVerification.ClaudeAndCodexStatusesExposeAccountLabelsOnly);
        await Test("cli account token never survives the read", CliAccountVerification.TokenNeverSurvivesTheRead);
        await Test("cli account antigravity status is its model list, with no sign-out", CliAccountVerification.AntigravityStatusIsItsModelListWithNoSignOut);
        await Test("cli account commands are the CLIs own", CliAccountVerification.CommandsAreTheCLIsOwn);
        await Test("cli account coordinator reads statuses and calls logout", CliAccountVerification.CoordinatorReadsStatusesAndCallsLogout);
        await Test("cli account smoke shows fixture statuses and confirmation flow works", CliAccountVerification.SmokeShowsFixtureStatusesAndConfirmationFlowWorks);
        await Test("account usage strings match macOS", StringsVerification.AccountUsageStringsMatchMacOS);
        await Test("notification and usage copy follows saved language", LocalizedStatusVerification.LanguageChangesReachNotificationsAndUsage);
        await Test("account usage secret never reaches a snapshot, a log line or an error", AccountUsageVerification.Secret);
        await Test("account usage refuses another host or a redirect", AccountUsageVerification.RefusesAnotherHostOrARedirect);
        await Test("account usage direct claude lookup is off by default", AccountUsageVerification.DirectLookupIsOffByDefault);
        await Test("account usage dashboard bars are the chips' leading windows", AccountUsageVerification.DashboardUsageBarsAreTheLeadingWindows);
        await Test("account usage claude reads quota and profile from the credentials file", AccountUsageVerification.ClaudeReadsQuotaAndProfileFromTheCredentialsFile);
        await Test("account usage failure keeps the last known value and backs off", AccountUsageVerification.FailureKeepsTheLastKnownValueAndBacksOff);
        await Test("account usage codex is asked through its own app-server", AccountUsageVerification.CodexIsAskedThroughItsOwnAppServer);
        await Test("account usage closing the app cancels pending reads", AccountUsageVerification.ClosingTheAppCancelsPendingReads);
        await Test("usage reset renders the seven states from the shared keys", AccountUsageVerification.UsageResetRendersSevenStates);
        await Test("usageReset unknown", AccountUsageVerification.UsageResetUnknown);
        await Test("usageReset ineligible", AccountUsageVerification.UsageResetIneligible);
        await Test("usageReset none", AccountUsageVerification.UsageResetNone);
        await Test("usageReset exhausted", AccountUsageVerification.UsageResetExhausted);
        await Test("usageReset cooldown", AccountUsageVerification.UsageResetCooldown);
        await Test("usageReset held", AccountUsageVerification.UsageResetHeld);
        await Test("usageReset available", AccountUsageVerification.UsageResetAvailable);
        await Test("usage reset smoke renders available and unknown", AccountUsageVerification.UsageResetSmokeRendersAvailableAndUnknown);
        await Test("usage reset smoke is wired into both platforms", AccountUsageVerification.UsageResetSmokeIsWiredIntoBothPlatforms);
        await Test("usageReset first-response log", AccountUsageVerification.UsageResetFirstResponseLog);
        await Test("usageReset rate-limit", AccountUsageVerification.UsageResetRateLimitDeadline);
        await Test("usageReset guard blocks transport", AccountUsageVerification.UsageResetGuardCheck);
        await Test("usageReset log paths match macOS", AccountUsageVerification.UsageResetShapeLogPathsMatchMacOS);
        await Test("app update ed25519 matches the RFC 8032 vectors", AppUpdateVerification.Ed25519MatchesTheRfc8032Vectors);
        await Test("app update strings match macOS", StringsVerification.AppUpdateStringsMatchMacOS);
        await Test("app update manifest accepts a fixture signature and refuses everything else", AppUpdateVerification.ManifestAcceptsAFixtureSignatureAndRefusesEverythingElse);
        await Test("app update manifest refuses an asset without sha256 or size", AppUpdateVerification.ManifestRefusesAnAssetWithoutSha256OrSize);
        await Test("app update version comparison orders releases and pre-releases", AppUpdateVerification.VersionComparisonOrdersReleasesAndPreReleases);
        await Test("app update without a public key there is no check at all", AppUpdateVerification.NoPublicKeyMeansNoCheckAtAll);
        await Test("app update a built-in address ignores a user address", AppUpdateVerification.ABuiltInAddressIgnoresAUserAddress);
        await Test("app update transport refuses a non-https hop", AppUpdateVerification.TransportRefusesANonHttpsHop);
        await Test("app update download verifies the bytes on disk and cleans up", AppUpdateVerification.DownloadVerifiesTheBytesOnDiskAndCleansUp);
        await Test("app update staging refuses an escaping entry or the wrong package", AppUpdateVerification.StagingRefusesAnEscapingEntryOrTheWrongPackage);
        await Test("app update replacement verifies again and replaces the install", AppUpdateVerification.ReplacementVerifiesAgainAndReplacesTheInstall);
        await Test("app update replacement leaves the install untouched when it cannot proceed", AppUpdateVerification.ReplacementLeavesTheInstallUntouchedWhenItCannotProceed);
        await Test("app update helper executable is inside the staged folder", AppUpdateVerification.AppUpdateHelperExecutableIsInsideTheStagedFolder);
        await Test("app update helper copies the staged folder to the install path", AppUpdateVerification.AppUpdateHelperCopiesTheStagedFolderToTheInstallPath);
        await Test("app update helper rolls back after a copy that fails half way", AppUpdateVerification.AppUpdateHelperRollsBackAfterACopyThatFailsHalfWay);
        await Test("app update helper waits for the app to quit before it moves anything", AppUpdateVerification.AppUpdateHelperWaitsForTheAppToQuitBeforeItMovesAnything);
        await Test("app update helper verifies the package again before the install is moved aside", AppUpdateVerification.AppUpdateHelperVerifiesThePackageAgainBeforeTheInstallIsMovedAside);
        await Test("app update helper keeps the backup until the new app has started", AppUpdateVerification.AppUpdateHelperKeepsTheBackupUntilTheNewAppHasStarted);
        await Test("app update automatic check happens at most once a day", AppUpdateVerification.AutomaticCheckHappensAtMostOnceADay);
        await Test("app update section shows the macOS copy for every phase", AppUpdateVerification.SectionShowsTheMacOSCopyForEveryPhase);
        await Test("app update pipeline runs from a fixture-signed manifest to a ready install plan", AppUpdateVerification.PipelineRunsFromAFixtureSignedManifestToAReadyInstallPlan);
        await Test("rename validation accepts valid names", RenameVerification.renameValidationAcceptsValidNames);
        await Test("rename validation refuses empty", RenameVerification.renameValidationRefusesEmpty);
        await Test("rename validation refuses over 120 characters", RenameVerification.renameValidationRefusesOver120);
        await Test("rename validation refuses control characters", RenameVerification.renameValidationRefusesControlCharacters);
        await Test("rename workspace stores name in snapshot", RenameVerification.renameWorkspaceStoresNameInSnapshot);
        await Test("rename session stores title in snapshot", RenameVerification.renameSessionStoresTitleInSnapshot);
        await Test("rename session title survives new output", RenameVerification.renameSessionTitleSurvivesNewOutput);
        await Test("rename name survives restart", RenameVerification.renameNameSurvivesRestart);
        await Test("rename rejects invalid name", RenameVerification.renameRejectsInvalidName);
        await Test("rename rejects unknown target", RenameVerification.renameRejectsUnknownTarget);
        await Test("rename via DesktopService persists name", RenameVerification.renameViaDesktopServicePersistsName);
        await Test("rename strings match macOS", RenameVerification.renameStringsMatchMacOS);
        await Test("rename messages mirror the macOS captions", RenameVerification.renameMessagesMirrorTheMacOSCaptions);
        await Test("rename save button is disabled exactly when macOS disables it", RenameVerification.renameSaveButtonIsDisabledExactlyWhenMacOSDisablesIt);
        await Test("rename keeps a long emoji name whole across a restart", RenameVerification.renameKeepsALongEmojiNameWholeAcrossARestart);
        await Test("rename clamp title still bounds an overlong stored title", RenameVerification.renameClampTitleStillBoundsAnOverlongStoredTitle);
        await Test("pane auto-titles: a request is shortened to one line of 40 characters as on macOS", PaneTitleVerification.shortenedFollowsTheMacOSRule);
        await Test("pane auto-titles: the title follows the latest request until a rename fixes it", PaneTitleVerification.titleFollowsTheLatestRequestUntilARenameFixesIt);
        await Test("pane auto-titles: a sent request retitles the agent pane through DesktopService", PaneTitleVerification.aRequestRetitlesTheAgentPaneThroughDesktopService);
        await Test("pane auto-titles: the automatic choice retitles from the latest typed request", PaneTitleVerification.automaticChoiceRetitlesFromTheLatestTypedRequest);
        await Test("pane auto-titles: a restart retitles automatic agent panes only", PaneTitleVerification.restoreRetitlesAutomaticAgentPanesOnly);
        await Test("claude plugin list reads this workspace's scopes and cached catalog without mutation", ClaudePluginVerification.ListsWorkspaceScopesAndCachedCatalogWithoutMutation);
        await Test("claude plugin list shows an ancestor's record for a nested workspace", ClaudePluginVerification.NestedWorkspaceSeesItsAncestorsRecord);
        await Test("claude plugin missing, old and empty sources are explained without installing", ClaudePluginVerification.MissingOldAndEmptyAreExplainedWithoutInstalling);
        await Test("claude plugin malformed or oversized answer never becomes an empty list", ClaudePluginVerification.MalformedOrOversizedAnswerNeverBecomesAnEmptyList);
        await Test("claude plugin failed runs and timeouts keep the screen intact", ClaudePluginVerification.FailedRunsAndTimeoutsKeepTheScreenIntact);
        await Test("claude plugin second request joins the running read", ClaudePluginVerification.SecondRequestJoinsTheRunningRead);
        await Test("claude plugin window tabs, filter, search and rows match macOS", ClaudePluginVerification.BrowserTabsFilterSearchAndRowsMatchMacOS);
        await Test("claude plugin reload reads again and replaces the list", ClaudePluginVerification.ReloadReadsAgainAndReplacesTheList);
        await Test("claude plugin palette offers /plugin and Codex /plugins", ClaudePluginVerification.PaletteOffersPluginForBothProviders);
        await Test("claude plugin strings match macOS", ClaudePluginVerification.StringsMatchMacOS);
        await Test("claude plugin window is a real WinUI surface wired into the app", ClaudePluginVerification.WindowIsARealWinUISurfaceWiredIntoTheApp);
        await Test("claude plugin models stay reusable for the Codex and marketplace features", ClaudePluginVerification.ModelsStayReusable);
        await Test("codex plugin list reads the user-level registry and catalog without mutation", CodexPluginVerification.ReadsParsesListing);
        await Test("codex plugin capability probe blocks a CLI without the JSON flags", CodexPluginVerification.CapabilityProbeBlocksUnsupportedCli);
        await Test("codex plugin missing CLI is explained without installing", CodexPluginVerification.MissingCodexBecomesMissing);
        await Test("codex plugin malformed or oversized answer never becomes an empty list", CodexPluginVerification.MalformedOrOversizedNeverBecomesEmptyList);
        await Test("codex plugin restricted install policies are left out and counted", CodexPluginVerification.RestrictedPolicyExcludesRows);
        await Test("codex plugin installed rows are always user scope", CodexPluginVerification.InstalledRowsAreAlwaysUserScope);
        await Test("codex plugin second request joins the running read", CodexPluginVerification.SecondRequestJoinsTheRunningRead);
        await Test("codex plugin window shows the Codex footer, scopes and empty sentence", CodexPluginVerification.BrowserShowsCodexFooterAndScopes);
        await Test("codex plugin palette offers /plugins and leaves no app action out", CodexPluginVerification.PaletteOffersCodexPlugins);
        await Test("codex plugin failed runs and timeouts keep the screen intact", CodexPluginVerification.FailedRunsAndTimeoutsKeepTheScreenIntact);
        await Test("codex plugin strings match macOS", CodexPluginVerification.StringsMatchMacOS);
        await Test("codex plugin window is a real WinUI surface wired into the app", CodexPluginVerification.WindowIsARealWinUISurfaceWiredIntoTheApp);
        await Test("plugin marketplace claude install runs the CLI command and reports success", PluginMarketplaceVerification.ClaudeInstallRunsTheCliCommandAndReportsSuccess);
        await Test("plugin marketplace claude install reports failure and unconfirmed results", PluginMarketplaceVerification.ClaudeInstallReportsFailureAndUnconfirmedResults);
        await Test("plugin marketplace claude install needing a command is never approved here", PluginMarketplaceVerification.ClaudeInstallNeedingACommandIsNeverApprovedHere);
        await Test("plugin marketplace claude install is skipped when the scope already has it", PluginMarketplaceVerification.ClaudeInstallIsSkippedWhenTheScopeAlreadyHasIt);
        await Test("plugin marketplace claude refuses values that did not come from the list", PluginMarketplaceVerification.ClaudeInstallRefusesValuesThatDidNotComeFromTheList);
        await Test("plugin marketplace claude install reports an unsupported CLI version", PluginMarketplaceVerification.ClaudeInstallReportsAnUnsupportedCliVersion);
        await Test("plugin marketplace claude install is cancellable", PluginMarketplaceVerification.ClaudeInstallIsCancellable);
        await Test("plugin marketplace claude runs one operation at a time", PluginMarketplaceVerification.ClaudeRunsOneOperationAtATime);
        await Test("plugin marketplace claude refresh runs the CLI command", PluginMarketplaceVerification.ClaudeMarketplaceRefreshRunsTheCliCommand);
        await Test("plugin marketplace codex install runs add at user level and verifies the result", PluginMarketplaceVerification.CodexInstallRunsAddAtUserLevelAndVerifiesTheResult);
        await Test("plugin marketplace codex install refuses another scope and an id not from the list", PluginMarketplaceVerification.CodexInstallRefusesAnotherScopeAndAnIdNotFromTheList);
        await Test("plugin marketplace codex install reports an unverifiable result", PluginMarketplaceVerification.CodexInstallReportsAnUnverifiableResult);
        await Test("plugin marketplace codex upgrade runs only for a registered git source", PluginMarketplaceVerification.CodexMarketplaceUpgradeRunsOnlyForARegisteredGitSource);
        await Test("plugin marketplace window offers the macOS scope choice and controls", PluginMarketplaceVerification.WindowOffersTheMacOSScopeChoiceAndControls);
        await Test("plugin marketplace window install shows progress, cancel, result and reloads", PluginMarketplaceVerification.WindowInstallShowsProgressCancelResultAndReloads);
        await Test("plugin marketplace window cancel stops the operation and says so", PluginMarketplaceVerification.WindowCancelStopsTheOperationAndSaysSo);
        await Test("plugin marketplace window refuses a change it cannot start", PluginMarketplaceVerification.WindowRefusesAChangeItCannotStart);
        await Test("plugin marketplace copy matches macOS", PluginMarketplaceVerification.CopyMatchesMacOS);
        await Test("plugin marketplace window is wired into the running app and the smoke run", PluginMarketplaceVerification.WindowIsWiredIntoTheRunningAppAndTheSmokeRun);
        await Test("plugin marketplace the running app owns the operations object", PluginMarketplaceVerification.TheRunningAppOwnsTheOperationsObject);
        await Test("toolkit file parses", ToolkitFileVerification.ToolkitFileParsesAllFiveTemplates);
        await Test("toolkit file refuses version mismatch", ToolkitFileVerification.ToolkitFileRefusesVersionMismatch);
        await Test("toolkit file skips unknown install kinds", ToolkitFileVerification.ToolkitFileSkipsUnknownKinds);
        await Test("toolkit file npm package name is accepted", ToolkitFileVerification.NpmPackageNameIsAccepted);
        await Test("toolkit platform rule matches macOS", ToolkitVerification.ToolkitPlatformRuleMatchesMacOS);
        await Test("toolkit winget template decodes and builds its command", ToolkitVerification.ToolkitWingetTemplateDecodesAndBuildsItsCommand);
        await Test("toolkit store keeps other-OS entries", ToolkitVerification.ToolkitStoreKeepsOtherOsEntries);
        await Test("toolkit approval binds to content hash", ToolkitVerification.ToolkitApprovalBindsToContentHash);
        await Test("toolkit plan runs only missing approved entries", ToolkitVerification.ToolkitPlanRunsOnlyMissingApprovedEntries);
        await Test("toolkit probes are file-only and run twice and decide the result table", ToolkitVerification.ToolkitProbesAreFileOnly);
        await Test("toolkit plan is a step list", ToolkitVerification.ToolkitInstallIsAnOrderedStepList);
        await Test("bundled entry sorts first and survives an unreadable store", ToolkitVerification.BundledEntrySortsFirstAndSurvivesAnUnreadableStore);
        await Test("result table separates not attempted, failed and succeeded", ToolkitVerification.ResultTableSeparatesNotAttemptedFailedAndSucceeded);
        await Test("result truth is authoritative: probe decides verdict, steps are explanation", ToolkitVerification.ResultTruthIsAuthoritative);
        await Test("components rows follow installed CLIs", ToolkitVerification.ComponentsRowsFollowInstalledCLIs);
        await Test("bare names are found along the PATH with PATHEXT", WindowsCommandVerification.BareNamesAreFoundAlongThePathWithPathExt);
        await Test("batch files run through cmd with every argument quoted", WindowsCommandVerification.BatchFilesRunThroughCmdWithEveryArgumentQuoted);
        await Test("the toolkit executor fails a step that cannot start", WindowsCommandVerification.ToolkitExecutorFailsAStepThatCannotStart);
        await Test("an old Claude is out of date, not missing", WindowsCommandVerification.AnOldClaudeIsOutOfDateNotMissing);
        await Test("setup: the catalog the app carries is the contract with a Windows entry per tool", SetupVerification.TheCatalogTheAppCarriesIsTheContractWithAWindowsEntryPerTool);
        await Test("setup: the decoder refuses what the Mac's refuses and keeps winget to Windows", SetupVerification.TheDecoderRefusesWhatTheMacsRefusesAndKeepsWingetToWindows);
        await Test("setup: prerequisites come first on Windows", SetupVerification.PrerequisitesComeFirstOnWindows);
        await Test("setup: the script is PowerShell from the catalog alone", SetupVerification.TheScriptIsPowerShellFromTheCatalogAlone);
        await Test("setup: the script folder is ours and markers never follow links", SetupVerification.TheScriptFolderIsOursAndMarkersNeverFollowLinks);
        await Test("setup: probes read the PATH, the installers' folders, plugins and skills", SetupVerification.ProbesReadThePathTheInstallersFoldersPluginsAndSkills);
        await Test("setup: rows and the first run follow the probes", SetupVerification.RowsAndTheFirstRunFollowTheProbes);
        await Test("setup: the screen is wired into the app", SetupVerification.TheScreenIsWiredIntoTheApp);
        await Test("components screen is live in the running app", ToolkitVerification.ComponentsScreenIsLiveInTheRunningApp);
        await Test("toolkit file round-trip is a fixed point", ToolkitVerification.ToolkitFileRoundTripIsAFixedPoint);
        await Test("step fetch eligibility is derived", ToolkitVerification.StepFetchEligibilityIsDerived);
        await Test("shared-format fixture round-trip", ToolkitVerification.SharedFormatFixtureRoundTrip);
        await Test("shared-format platform table", ToolkitVerification.SharedFormatPlatformTable);
        await Test("hash parity canonical bytes match fixture", ToolkitVerification.HashParityCanonicalBytes);
        await Test("hash parity sha256 digests match fixture", ToolkitVerification.HashParityDigests);
        await Test("hash parity stale platforms key excluded from hash surface", ToolkitVerification.HashParityStalePlatformsKeyExcluded);
        await Test("hash parity changing a hashed field changes the digest", ToolkitVerification.HashParityChangingFieldChangesHash);
        await Test("hash parity round-trip with stale platforms key is byte-identical", ToolkitVerification.HashParityRoundTripWithStalePlatformsKey);
        await Test("browser session loads with workspaceProfileKey on Windows state load", BrowserSessionVerification.BrowserSessionLoadsWithWorkspaceProfileKey);
        await Test("browser address resolves like macOS", BrowserVerification.AddressResolvesLikeMacOS);
        await Test("browser history follows macOS rules", BrowserVerification.HistoryFollowsMacOSRules);
        await Test("browser profile folder per workspace follows profile", BrowserVerification.ProfileFolderPerWorkspaceFollowsProfile);
        await Test("browser setting is off by default and read once", BrowserVerification.SettingIsOffByDefaultAndReadOnce);
        await Test("browser runtime missing offers install only on click", BrowserVerification.RuntimeMissingOffersInstallOnlyOnClick);
        await Test("browser installer requires a Microsoft signature", BrowserVerification.InstallerRequiresMicrosoftSignature);
        await Test("browser installer failure cleans up", BrowserVerification.InstallerFailureCleansUp);
        await Test("browser session fields share macOS names", BrowserVerification.SessionFieldsShareMacOSNames);
        await Test("Mod bridge authenticates metadata and rejects browser/secret/stale events", async () =>
        {
            var received = 0; await using var bridge = new ModBridge(); using var connection = await bridge.RegisterAsync(_ => Interlocked.Increment(ref received), CancellationToken.None); using var client = new HttpClient();
            async Task<HttpStatusCode> Post(string token, object body, bool browser = false)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, connection.Url); request.Headers.Add("Authorization", "Bearer " + token); if (browser) request.Headers.Add("Origin", "http://localhost"); request.Content = new StringContent(JsonSerializer.Serialize(body, Wire.Json), Encoding.UTF8, "application/json"); using var response = await client.SendAsync(request); return response.StatusCode;
            }
            var body = new { version = 1, runId = connection.Id, claudeSessionId = "claude-session", @event = "session.start" };
            Check(await Post(connection.Token, body) == HttpStatusCode.NoContent); Check(received == 1);
            Check(await Post(new string('0', 64), body) == HttpStatusCode.Unauthorized); Check(await Post(connection.Token, body, true) == HttpStatusCode.Forbidden);
            Check(await Post(connection.Token, new { version = 1, runId = connection.Id, claudeSessionId = "claude-session", @event = "session.start", prompt = "must never enter metadata" }) == HttpStatusCode.BadRequest);
            connection.Dispose(); Check(await Post(connection.Token, body) == HttpStatusCode.Unauthorized); Check(received == 1);
        });
        if (OperatingSystem.IsWindows())
        {
            await Test("Windows Job Object handles Unicode and kills descendants after parent exits", async () =>
            {
                var directory = Temp(); var pid = Path.Combine(directory, "descendant.pid");
                ChildProcess? tree = null; var failures = new List<Exception>(); var phase = "Unicode output"; var jobExitVerified = false;
                try
                {
                    var command = "echo 안녕하세요";
                    await using (var child = ChildProcess.Start(ChildProcess.StartInfo(Environment.GetEnvironmentVariable("ComSpec")!, ["/d", "/s", "/c", command], directory), command))
                    {
                        child.Input.Close(); var output = await child.Output.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        Check(await child.Completion.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "Unicode shell failed");
                        Check(output.Contains("안녕하세요"), "Unicode shell output changed: " + JsonSerializer.Serialize(output));
                    }
                    phase = "descendant startup";
                    var start = "start \"\" /b " + LongCommand(pid);
                    tree = ChildProcess.Start(ChildProcess.StartInfo(Environment.GetEnvironmentVariable("ComSpec")!, ["/d", "/s", "/c", start], directory), start);
                    tree.Input.Close(); await Until(() => File.Exists(pid)); var processId = int.Parse(await File.ReadAllTextAsync(pid));
                    phase = "parent exit";
                    Check(await tree.Completion.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "Parent shell failed");
                    Check(Alive(processId), "The fixture descendant must outlive its parent before job disposal");
                    phase = "job disposal";
                    await tree.DisposeAsync(); tree = null;
                    Check(!Alive(processId), "Job disposal returned while its descendant was alive");
                    jobExitVerified = true;
                }
                catch (Exception error) { failures.Add(new InvalidOperationException("Windows child fixture failed during " + phase, error)); }
                finally
                {
                    // Always terminate the job before deleting its cwd, including assertion failures.
                    if (tree is not null) try { await tree.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
                    try
                    {
                        var cleanup = Stopwatch.StartNew();
                        while (true)
                        {
                            try { Directory.Delete(directory, true); break; }
                            // Only after the empty job and dead descendant assertions passed:
                            // Windows may briefly retain directory handles outside that job.
                            catch (IOException error) when (jobExitVerified && ((error.HResult & 0xffff) is 32 or 145) && cleanup.Elapsed < TimeSpan.FromSeconds(3))
                            { await Task.Delay(40); }
                        }
                    }
                    catch (Exception error) { failures.Add(new IOException("Windows fixture directory cleanup failed", error)); }
                }
                if (failures.Count > 0) throw new AggregateException("Windows child verification failed", failures);
            });
        }
        else { skipped++; Console.WriteLine("SKIP Windows Job Object / UTF-8 console (requires Windows)"); }
        Console.WriteLine($"Native Core: {passed} passed, {failedChecks.Count} failed, {skipped} platform-specific skipped.");
        if (failedChecks.Count > 0) throw new InvalidOperationException($"{failedChecks.Count} Core check(s) failed: " + string.Join("; ", failedChecks));
    }
    private static bool Alive(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
}
