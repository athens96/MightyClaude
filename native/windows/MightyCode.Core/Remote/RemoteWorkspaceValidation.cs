using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MightyCode.Core;

/// <summary>The Mac's checks (M/Remote/RemoteWorkspaceModels.swift RemoteWorkspaceValidation) on what crosses the remote wire.</summary>
public static class RemoteWorkspaceValidation
{
    /// <summary>A connection key: 256 random bits, base64url (43 characters) or longer.</summary>
    public static bool Key(string? value) => value is not null && Regex.IsMatch(value, @"^[A-Za-z0-9_-]{43,128}\z");

    /// <summary>Constant-time: both sides are hashed first, so neither the length nor the first differing byte shows in the time taken.</summary>
    public static bool KeysMatch(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public static bool SharedWorkspace(RemoteSharedWorkspace? value) => value is not null && Wire.Identifier(value.Id) && value.Name is { Length: > 0 and <= 120 }
        && !value.Name.Any(char.IsControl) && StateStore.RemotePath(value.Path);

    public static bool PermissionMode(string mode, string provider) =>
        mode is "manual" or "plan" or "acceptEdits" or "fullAccess" || mode == "auto" && provider == "claude" || mode == "onRequest" && provider == "codex";

    public static readonly IReadOnlySet<string> EventTypes = new HashSet<string>(StringComparer.Ordinal) { "log", "status", "resume", "activity", "usage", "graph", "permission", "plan", "todos", "background", "antigravityPermission" };

    /// <summary>
    /// An Antigravity card from a host (M/Remote/RemoteWorkspaceModels.swift antigravityCard): ids, at most 16 actions, bounded text, and only
    /// well-formed rules <see cref="AntigravityRuleSafety.TextVerdict"/> does not refuse (by their text: nothing on this computer's disk is asked for a host's path).
    /// </summary>
    public static bool AntigravityCard(AntigravityPermissionRequest card) =>
        Wire.Identifier(card.Id) && Wire.Identifier(card.RunId) && card.Actions is { Count: > 0 and <= AntigravityPermissionRequest.MaximumActions }
        && card.Actions.All(action => action is not null && action.Action is { Length: > 0 } name && Encoding.UTF8.GetByteCount(name) <= 128 && action.DisplayName is { Length: > 0 } shown && Encoding.UTF8.GetByteCount(shown) <= 128
            && Encoding.UTF8.GetByteCount(action.Target ?? "") <= AntigravityAllowGrant.MaximumRuleBytes && (action.Rule is null || AntigravityAllowGrant.ValidRule(action.Rule) && AntigravityRuleSafety.TextVerdict(action.Rule) != AntigravityRuleVerdict.Refused));

    /// <summary>A host's /info as the Mac checks it, after RemoteWire.Json read it: nulls the Mac's decoder refuses are refused here too.</summary>
    public static void Info(RemoteWireInfo? value)
    {
        if (value is null || value.Protocol != RemoteProtocol.Version || !Wire.Identifier(value.HostId) || value.HostName is not { Length: > 0 and <= 120 } || value.Workspaces is null
            || value.Workspaces.Count > RemoteProtocol.MaximumSharedWorkspaces || !value.Workspaces.All(SharedWorkspace) || value.Workspaces.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != value.Workspaces.Count
            || value.Runtime is null || value.Runtime.Platform is not ("darwin" or "win32" or "linux") || value.Runtime.AppVersion is not { Length: <= 80 } || (value.Runtime.Providers?.Count ?? 0) > Wire.Providers.Length)
            throw new InvalidDataException(Locale.Get("remote.connect.error.notMighty"));
        foreach (var provider in value.Runtime.Providers ?? [])
            if (provider is null || !Wire.Providers.Contains(provider.Id) || provider.Name is not { Length: <= 120 } || provider.Detail is not { Length: <= 2_000 } || (provider.Version?.Length ?? 0) > 80
                // A longer list is cut before this (Bounded), never refused: one long list (opencode lists hundreds) would drop the whole host.
                || provider.ModelCatalog?.Models is null || provider.ModelCatalog.Models.Count > MaximumModels(provider.Id) || provider.ModelCatalog.Detail is not { Length: <= 2_000 }
                // A newer host says why its latest read failed and when a kept list was read; an older one says neither.
                || (provider.ModelCatalog.ReadFailure?.Length ?? 0) > 2_000 || (provider.ModelCatalog.ListedAt?.Length ?? 0) > 64
                || !provider.ModelCatalog.Models.All(m => m is not null && Wire.Model(m.Value) && m.DisplayName is { Length: <= 160 } && m.Description is { Length: <= 2_400 })
                || provider.Capabilities?.PermissionModes is null || !provider.Capabilities.PermissionModes.All(mode => mode is not null && PermissionMode(mode, provider.Id)))
                throw new InvalidDataException(Locale.Get("remote.connect.error.badRuntime"));
    }

    /// <summary>
    /// The most models one provider's list holds on the wire (M/Remote/RemoteWorkspaceModels.swift maximumModels): its own default and as many of
    /// the CLI's as this app's own read of that CLI keeps (opencode and Hermes 1,024, pi 512, the others 128).
    /// </summary>
    public static int MaximumModels(string provider) => 1 + Wire.ListedModelLimit(provider);

    /// <summary>
    /// A host's runtime with each provider's model list cut to its first <see cref="MaximumModels"/> (M/Remote/RemoteWorkspaceModels.swift
    /// bounded). The host sends no more, and a client keeps an older host's longer list to that many instead of refusing its whole /info.
    /// </summary>
    public static RuntimeInfo Bounded(RuntimeInfo runtime)
    {
        static ModelCatalog? Cut(ModelCatalog? catalog, string provider) =>
            catalog?.Models is { } models && models.Count > MaximumModels(provider) ? catalog with { Models = models.Take(MaximumModels(provider)).ToList() } : catalog;
        return runtime with
        {
            // The older field, Claude's list again.
            ModelCatalog = Cut(runtime.ModelCatalog, "claude"),
            Providers = runtime.Providers?.Select(provider => provider is null ? provider! : provider with { ModelCatalog = Cut(provider.ModelCatalog, provider.Id)! }).ToList()!,
        };
    }

    /// <summary>Settings → Models as the client sent it: every value a model name or "default", every effort a known one.</summary>
    public static bool PhaseModels(PhaseModelsSnapshot value)
    {
        string[] models = [value.ClaudeMain, value.ClaudeOpusAlias, value.ClaudeSonnetAlias, value.ClaudeHaikuAlias, value.ClaudeSubagentDefault, value.CodexReviewModel, value.CodexSubagentDefault];
        string[] efforts = [value.CodexPlanModeReasoningEffort, value.ClaudeMainEffort ?? "default", value.CodexMainEffort ?? "default", value.CodexSubagentEffort ?? "default"];
        return models.All(m => m == "default" || Wire.Model(m)) && efforts.All(e => e == "default" || Wire.Efforts.Contains(e));
    }

    /// <summary>What the host's CLI cannot run (M/CoreValidation.validateCapabilities); null when it can.</summary>
    public static string? CapabilityProblem(StartRunRequest request, ProviderCapabilities caps)
    {
        var s = request.Settings ?? new();
        if (!caps.Attachments && request.Attachments is { Count: > 0 }) return Locale.Get("validation.run.attachmentsUnsupported");
        var supported = caps.PermissionModes.Contains(s.PermissionMode) && (request.PermissionModeOverride is null || caps.PermissionModes.Contains(request.PermissionModeOverride))
            && (caps.Effort || s.Effort == "default") && (caps.MaxTurns || s.MaxTurns is null) && (caps.MaxBudgetUsd || s.MaxBudgetUsd is null)
            && (caps.FastMode || !s.FastMode) && (caps.WebSearch || s.WebSearch == "default") && (caps.NetworkAccess || !s.NetworkAccess)
            && (!s.NetworkAccess || request.Provider == "codex" && s.PermissionMode is "acceptEdits" or "onRequest") && (caps.Resume || request.ResumeId is null);
        return supported ? null : Locale.Get("validation.run.settingsUnsupported");
    }
}
