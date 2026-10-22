using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

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

    /// <summary>An Antigravity card from a host (M/Remote/RemoteWorkspaceModels.swift antigravityCard): ids, at most 16 actions, bounded text, and only well-formed rules.</summary>
    public static bool AntigravityCard(AntigravityPermissionRequest card) =>
        Wire.Identifier(card.Id) && Wire.Identifier(card.RunId) && card.Actions is { Count: > 0 and <= AntigravityPermissionRequest.MaximumActions }
        && card.Actions.All(action => action is not null && action.Action is { Length: > 0 } name && Encoding.UTF8.GetByteCount(name) <= 128 && action.DisplayName is { Length: > 0 } shown && Encoding.UTF8.GetByteCount(shown) <= 128
            && Encoding.UTF8.GetByteCount(action.Target ?? "") <= AntigravityAllowGrant.MaximumRuleBytes && (action.Rule is null || AntigravityAllowGrant.ValidRule(action.Rule)));

    /// <summary>A host's /info as the Mac checks it, after RemoteWire.Json read it: nulls the Mac's decoder refuses are refused here too.</summary>
    public static void Info(RemoteWireInfo? value)
    {
        if (value is null || value.Protocol != RemoteProtocol.Version || !Wire.Identifier(value.HostId) || value.HostName is not { Length: > 0 and <= 120 } || value.Workspaces is null
            || value.Workspaces.Count > RemoteProtocol.MaximumSharedWorkspaces || !value.Workspaces.All(SharedWorkspace) || value.Workspaces.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != value.Workspaces.Count
            || value.Runtime is null || value.Runtime.Platform is not ("darwin" or "win32" or "linux") || value.Runtime.AppVersion is not { Length: <= 80 } || (value.Runtime.Providers?.Count ?? 0) > Wire.Providers.Length)
            throw new InvalidDataException(Locale.Get("remote.connect.error.notMighty"));
        foreach (var provider in value.Runtime.Providers ?? [])
            if (provider is null || !Wire.Providers.Contains(provider.Id) || provider.Name is not { Length: <= 120 } || provider.Detail is not { Length: <= 2_000 } || (provider.Version?.Length ?? 0) > 80
                || provider.ModelCatalog?.Models is null || provider.ModelCatalog.Models.Count > 128 || provider.ModelCatalog.Detail is not { Length: <= 2_000 }
                || !provider.ModelCatalog.Models.All(m => m is not null && Wire.Model(m.Value) && m.DisplayName is { Length: <= 160 } && m.Description is { Length: <= 2_400 })
                || provider.Capabilities?.PermissionModes is null || !provider.Capabilities.PermissionModes.All(mode => mode is not null && PermissionMode(mode, provider.Id)))
                throw new InvalidDataException(Locale.Get("remote.connect.error.badRuntime"));
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
