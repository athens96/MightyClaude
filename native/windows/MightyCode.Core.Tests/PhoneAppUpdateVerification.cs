using System.Text.Json;
using MightyCode.Core;

// The desktop app's update as the phone sees it and starts it (PhoneAppUpdate.cs; mirrors M/Tests/MightyCoreTests/TerminalPromptTests.swift
// MobileAppUpdateTests; docs/phone-desktop-update.md).
internal static class PhoneAppUpdateVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire.Json);
    private static AppUpdateAvailability Offer(string version, string? notes = null) =>
        new("0.4.55", new AppUpdateManifest(version, null, notes, null, new Dictionary<string, AppUpdateAsset>()));

    internal static Task TheStateTravelsAsPlainJsonAndKeepsItsNotesShort()
    {
        var state = new MobileAppUpdate("available", "0.4.55", "0.4.56", new string('n', 3_000), Running: 2);
        Check(state.Notes!.Length == MobileAppUpdate.MaximumNotes, "notes are cut");
        var json = Json(state);
        Check(json.GetProperty("phase").GetString() == "available" && json.GetProperty("running").GetInt32() == 2 && json.GetProperty("available").GetString() == "0.4.56", "names");
        Check(!json.TryGetProperty("error", out _) && !json.TryGetProperty("progress", out _), "absent fields are left out");
        Check(MobileDesktopRouter.Capabilities.Contains("appUpdate"), "the capability");
        var request = JsonSerializer.Deserialize<MobileAppUpdateRequest>("""{"action":"install","confirm":true}""", Wire.Json)!;
        Check(request.Action == "install" && request.Confirm == true, "the request");
        return Task.CompletedTask;
    }

    internal static Task ThePhasesMapToThePhones()
    {
        Check(PhoneAppUpdate.Map(null, "1", 0) is null, "a build that cannot update has no state");
        MobileAppUpdate? Of(AppUpdateState s, int running = 0) => PhoneAppUpdate.Map(s, "0.4.55", running);
        Check(Of(new())!.Phase == "idle" && Of(new() { Phase = AppUpdatePhase.Checking })!.Phase == "checking" && Of(new() { Phase = AppUpdatePhase.UpToDate })!.Phase == "upToDate", "idle, checking, upToDate");
        var available = Of(new() { Phase = AppUpdatePhase.Available, Availability = Offer("0.4.56", "notes") }, 3)!;
        Check(available is { Phase: "available", Current: "0.4.55", Available: "0.4.56", Notes: "notes", Running: 3 }, "available carries the version, notes and running count");
        Check(Of(new() { Phase = AppUpdatePhase.UpToDate, Availability = Offer("0.4.55") })!.Available is null, "an offer that is not newer is no offer");
        var downloading = Of(new() { Phase = AppUpdatePhase.Downloading, DownloadFraction = 0.456, Availability = Offer("0.4.56") })!;
        Check(downloading.Phase == "downloading" && downloading.Progress == 0.46, "downloading has progress, rounded");
        Check(Of(new() { Phase = AppUpdatePhase.Staging, Availability = Offer("0.4.56") }) is { Phase: "downloading", Progress: 1 }, "staging reads as a full download");
        Check(Of(new() { Phase = AppUpdatePhase.Ready, Availability = Offer("0.4.56") })!.Phase == "ready" && Of(new() { Phase = AppUpdatePhase.Installing })!.Phase == "installing", "ready, installing");
        Check(Of(new() { Phase = AppUpdatePhase.Failed, ErrorMessage = "boom" }) is { Phase: "failed", Error: "boom" }, "failed has the error");
        return Task.CompletedTask;
    }

    internal static Task ThePhonesInstallAdvancesAndNeverLoops()
    {
        PhoneAppUpdateStep Begin(AppUpdatePhase p, AppUpdateAvailability? a = null) => PhoneAppUpdate.Begin(new() { Phase = p, Availability = a });
        PhoneAppUpdateStep Next(AppUpdatePhase p, AppUpdateAvailability? a = null) => PhoneAppUpdate.Next(new() { Phase = p, Availability = a });
        foreach (var phase in new[] { AppUpdatePhase.Idle, AppUpdatePhase.UpToDate, AppUpdatePhase.Failed }) Check(Begin(phase) == PhoneAppUpdateStep.Check, "what is known may be old: look again from " + phase);
        Check(Begin(AppUpdatePhase.Ready, Offer("0.4.56")) == PhoneAppUpdateStep.Install, "a ready update installs at once");
        Check(Begin(AppUpdatePhase.Available, Offer("0.4.56")) == PhoneAppUpdateStep.Download, "an offer downloads");
        Check(Next(AppUpdatePhase.Checking) == PhoneAppUpdateStep.Wait && Next(AppUpdatePhase.Downloading) == PhoneAppUpdateStep.Wait && Next(AppUpdatePhase.Staging) == PhoneAppUpdateStep.Wait && Next(AppUpdatePhase.Installing) == PhoneAppUpdateStep.Wait, "steps under way wait");
        Check(Next(AppUpdatePhase.UpToDate) == PhoneAppUpdateStep.Done && Next(AppUpdatePhase.Idle) == PhoneAppUpdateStep.Done && Next(AppUpdatePhase.Failed) == PhoneAppUpdateStep.Done, "nothing newer, or a failure, ends the request");
        Check(Next(AppUpdatePhase.Available, Offer("0.4.55")) == PhoneAppUpdateStep.Done && Next(AppUpdatePhase.Available) == PhoneAppUpdateStep.Done, "an available that is not newer ends the request instead of downloading");
        return Task.CompletedTask;
    }

    internal static async Task TheRouteValidatesAndAsksBeforeStoppingRequests()
    {
        var directory = Directory.CreateTempSubdirectory("mighty-phoneupdate-").FullName;
        try
        {
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync();
            var workspace = await service.AddWorkspaceAsync(Directory.CreateDirectory(Path.Combine(directory, "ws")).FullName);
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])) { AppVersion = "0.4.55" };
            async Task<(int Status, JsonElement Body)> Post(string body) { var reply = await router.RouteAsync("POST", "/m1/app-update", JsonDocument.Parse(body).RootElement, "phone", CancellationToken.None); return (reply.Status, Json(reply.Body)); }
            async Task<JsonElement> State() => Json((await router.RouteAsync("GET", "/m1/state", null, "phone", CancellationToken.None)).Body);

            Check((await Post("""{"action":"check"}""")).Status == 409 && !(await State()).TryGetProperty("appUpdate", out _), "without update support: 409 and no state");
            var acts = new List<bool>(); var update = new AppUpdateState { Phase = AppUpdatePhase.Available, Availability = Offer("0.4.56") };
            router.AppUpdateRead = () => update; router.AppUpdateAct = install => { acts.Add(install); return Task.CompletedTask; };
            Check((await Post("""{"action":"launch"}""")).Status == 400, "a wrong action is 400");
            Check((await Post("""{"action":"check"}""")) is { Status: 200 } check && check.Body.GetProperty("phase").GetString() == "available" && acts.SequenceEqual([false]), "check");
            Check((await Post("""{"action":"install"}""")).Status == 200 && acts.SequenceEqual([false, true]), "install with nothing running");
            Check((await State()).GetProperty("appUpdate").GetProperty("available").GetString() == "0.4.56", "the state carries the update");

            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "busy", WorkspaceId = workspace.Id, Status = "running" }] });
            var refused = await Post("""{"action":"install"}""");
            Check(refused.Status == 409 && acts.Count == 2, "running requests without confirm: 409 and nothing done");
            Check((await Post("""{"action":"install","confirm":false}""")).Status == 409 && acts.Count == 2, "confirm false is no confirm");
            Check((await Post("""{"action":"install","confirm":true}""")).Status == 200 && acts.Count == 3, "confirm lets it through");
            Check((await Post("""{"action":"check"}""")).Status == 200 && acts.Count == 4, "check needs no confirm");
            Check((await State()).GetProperty("appUpdate").GetProperty("running").GetInt32() == 1, "the running count");

            // The state revision moves when the phone-visible update moves, and only then.
            router.Changed(); router.Observe(); router.TakeNotifications();
            var revision = router.StateRevision;
            router.Changed(); router.Observe();
            Check(router.StateRevision == revision, "no change, no new revision");
            update = update with { Phase = AppUpdatePhase.Downloading, DownloadFraction = 0.5 };
            router.Changed(); router.Observe();
            Check(router.StateRevision == revision + 1 && router.TakeNotifications().Any(n => n.Scope == "state"), "a changed update moves the state revision");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
