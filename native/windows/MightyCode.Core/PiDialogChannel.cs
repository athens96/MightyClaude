using System.Text.Json;
using System.Text.Json.Nodes;

namespace MightyCode.Core;

/// <summary>
/// The dialogs a pi extension opens in RPC mode (<c>extension_ui_request</c>, pi's docs/rpc-extension-ui.md) (M/MightyCore/AgentQuestions.swift
/// PiDialogChannel): <c>select</c>, <c>confirm</c> and <c>input</c> become the question card, and the answer leaves as an
/// <c>extension_ui_response</c>. The app's own <c>ask_user</c> tool (the shipped pi-ask-user.js) asks through <c>select</c> and <c>input</c>, and
/// takes a typed answer, or several picks, in place of one choice; any other extension's <c>select</c> is answered with one of its options
/// only. An <c>editor</c> dialog (a text to edit) is not a question: the parser cancels it as before.
/// </summary>
public sealed class PiDialogChannel(string runId, Action<string> write, Action<ToolPermissionRequest> emit, Action<ToolPermissionRequest, string> activity, Action<string> warning, Action<string, double>? timer = null)
{
    public const int MaximumPending = 16;
    /// <summary>The <c>ask_user</c> call a dialog belongs to, when its title is that call's question.</summary>
    public sealed record Asking(string ToolCallId, bool Multiple);
    private sealed record Pending(string DialogId, string Method, Asking? Asking, ToolPermissionRequest Display);
    private readonly object sync = new();
    private readonly Dictionary<string, Pending> pending = [];
    private bool closed;

    /// <summary>Shows one dialog as the card; false when the card cannot ask it (the caller cancels it).</summary>
    public bool Receive(JsonElement value, Asking? asking)
    {
        lock (sync)
        {
            if (closed || pending.Count >= MaximumPending || value.Text("id") is not { Length: > 0 } dialogId || System.Text.Encoding.UTF8.GetByteCount(dialogId) > 256
                || value.Text("method") is not { } method || AgentQuestions.Text(value.Text("title"), 8_192) is not { } title) return false;
            var item = new AgentQuestions.Item(Locale.Get("questionnaire.header.agent"), title);
            switch (method)
            {
                case "select":
                    var raw = MetadataJson.Property(value, "options") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().ToList() : [];
                    var options = raw.Select(o => o.ValueKind == JsonValueKind.String ? AgentQuestions.Text(o.GetString(), 1_024, singleLine: true) : null).ToList();
                    // The option itself is the answer pi reads: one the card would have to shorten is not asked.
                    if (options.Count == 0 || raw.Zip(options).Any(pair => pair.First.ValueKind != JsonValueKind.String || pair.First.GetString() != pair.Second)) return false;
                    item.Options.AddRange(options.Select(o => (o!, "")));
                    item.MultiSelect = asking?.Multiple == true;
                    break;
                case "confirm":
                    if (AgentQuestions.Text(value.Text("message"), 8_192) is { } message) { item.Header = ActivitySupport.Clean(title, 256, singleLine: true); item.Question = message; }
                    item.Options.Add((Locale.Get("questionnaire.option.yes"), "")); item.Options.Add((Locale.Get("questionnaire.option.no"), ""));
                    break;
                case "input": break;
                default: return false;
            }
            if (AgentQuestions.Request(Wire.Id(), runId, asking?.ToolCallId ?? "pi-dialog-" + dialogId, [item]) is not { } request) return false;
            var display = request with { ReceivedAt = Wire.Now() };
            pending[display.Id] = new(dialogId, method, asking, display);
            if (asking is not null) activity(display, "waiting");
            emit(display);
            if (MetadataJson.Number(value, "timeout") is { } milliseconds && double.IsFinite(milliseconds) && milliseconds > 0) timer?.Invoke(display.Id, milliseconds / 1_000);
            return true;
        }
    }

    public void AnswerQuestions(string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers)
    {
        lock (sync)
        {
            if (closed || !pending.TryGetValue(requestId, out var ask)) throw new InvalidOperationException(Locale.Get("permission.error.questionGone"));
            var answer = AgentQuestions.Answers(ask.Display, answers).Values.FirstOrDefault();
            var response = new JsonObject { ["type"] = "extension_ui_response", ["id"] = ask.DialogId };
            switch (ask.Method)
            {
                case "confirm":
                    if (answer.Typed is not { Length: 0 } || answer.Picked is not [var picked, ..]) throw new ArgumentException(Locale.Get("questionnaire.error.choicesOnly"));
                    response["confirmed"] = picked == Locale.Get("questionnaire.option.yes");
                    break;
                case "select":
                    // Only the app's own tool reads anything but one of its options.
                    if (ask.Asking is null && (answer.Typed.Length > 0 || answer.Picked.Count != 1)) throw new ArgumentException(Locale.Get("questionnaire.error.choicesOnly"));
                    response["value"] = string.Join(", ", answer.Picked.Concat(answer.Typed.Length == 0 ? [] : [answer.Typed]));
                    break;
                default:
                    if (answer.Typed is not { Length: > 0 } typed) throw new ArgumentException(Locale.Get("questionnaire.error.missingAnswer"));
                    response["value"] = typed;
                    break;
            }
            pending.Remove(requestId);
            Send(response);
            var display = ask.Display with { State = "answered" };
            if (ask.Asking is not null) activity(display, "running");
            emit(display);
        }
    }

    /// <summary>The card's Cancel: pi reads the dialog as dismissed.</summary>
    public void Respond(string requestId, bool allow)
    {
        lock (sync)
        {
            if (closed || !pending.TryGetValue(requestId, out var ask)) throw new InvalidOperationException(ToolPermissionStrings.AlreadySettled);
            if (allow) throw new InvalidOperationException(Locale.Get("permission.error.noAnswers"));
            pending.Remove(requestId);
            Send(new JsonObject { ["type"] = "extension_ui_response", ["id"] = ask.DialogId, ["cancelled"] = true });
            var display = ask.Display with { State = "denied" };
            if (ask.Asking is not null) activity(display, "running");
            emit(display);
        }
    }

    /// <summary>pi answered the dialog itself when its deadline passed.</summary>
    public void TimedOut(string requestId)
    {
        lock (sync)
        {
            if (!pending.TryGetValue(requestId, out var ask)) return;
            Withdraw(requestId);
            warning(Locale.Get("run.notice.piDialogTimedOut", new Dictionary<string, string> { ["title"] = ActivitySupport.Clean(ask.Display.Summary, 256, singleLine: true) }));
        }
    }
    /// <summary>The tool that asked has ended (pi aborted it): its card has nothing left to answer.</summary>
    public void ToolEnded(string toolCallId) { lock (sync) foreach (var id in pending.Where(p => p.Value.Asking?.ToolCallId == toolCallId).Select(p => p.Key).ToList()) Withdraw(id); }
    public void CancelAll() { lock (sync) { if (closed) return; closed = true; foreach (var id in pending.Keys.ToList()) Withdraw(id); } }
    private void Withdraw(string id) { if (pending.Remove(id, out var ask)) emit(ask.Display with { State = "cancelled" }); }
    private void Send(JsonObject value) => write(value.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
}
