using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MightyClaude.Core;
using static MightyStructureVerification;
using Board = MightyClaude.Core.MightyStructure.Board;
using Step = MightyClaude.Core.MightyStructure.Step;
using Tone = MightyClaude.Core.MightyStructure.Tone;

/// <summary>
/// The 파이프라인 view's state colours against the Mac's MightyStructureToneTests: a status, a step, a column and a card take the
/// same tone, a current step takes its newest request's (amber while it waits, green once it is done), and a step still to come
/// takes none.
/// </summary>
internal static class MightyStructureToneVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static string Words(IEnumerable<DesignTone?> tones) => string.Join(",", tones.Select(tone => tone?.ToString().ToLowerInvariant() ?? "-"));
    private static IReadOnlyList<Board> Boards(IReadOnlyList<MightyGraphRun> runs, StyleEvaluator? style = null, string? phase = null, bool startingNew = false) =>
        MightyStructure.Boards(MightyStructure.Make(runs, style, phase is null || style is null ? null : Phase(style, phase), startingNew));
    private static string Columns(Board board) => Words(board.Columns.Select(Tone.OfColumn));
    private static string Cards(MightyStructure.BoardColumn column) => Words(column.Cards.Select(card => Tone.OfCard(card, column)));

    internal static Task AStatusAndAStepTakeThePipelineTone()
    {
        var statuses = Words(new[] { "running", "starting", "queued", "waiting", "completed", "done", "error", "failed", "stopped", "cancelled", "interrupted", "pending" }.Select(Tone.OfStatus));
        Check(statuses == "run,run,run,wait,done,done,err,err,stop,stop,stop,-", "a status takes the Mac's block bucket, done is done and pending none: " + statuses);
        var steps = Words([Tone.OfStep(Step.Done), Tone.OfStep(Step.Current), Tone.OfStep(Step.Current, waiting: true), Tone.OfStep(Step.Error), Tone.OfStep(Step.Stopped),
            Tone.OfStep(Step.Pending), Tone.OfStep(Step.Pending, waiting: true), Tone.OfStep(Step.Done, waiting: true)]);
        Check(steps == "done,run,wait,err,stop,-,-,done", "a step's tone, the current one waiting while its request waits: " + steps);
        return Task.CompletedTask;
    }

    internal static Task AFreeBoardTintsItsColumnsAndCards()
    {
        var board = Boards([Run("r1", status: "waiting", agents: [Agent("a", status: "running")])])[0];
        Check(Columns(board) == "done,wait,-", "a waiting request: 요청 done, 실행 waits, 결과 to come: " + Columns(board));
        Check(Cards(board.Columns[1]) == "wait,run", "the 실행 card waits, its running sub-agent runs: " + Cards(board.Columns[1]));
        Check(Cards(board.Columns[0]) == "done" && Cards(board.Columns[2]) == "-", "the 요청 and 결과 cards take their column's tone: " + Cards(board.Columns[0]) + " " + Cards(board.Columns[2]));
        var boards = Boards([Run("r1", final: "끝"), Run("r2", status: "failed"), Run("r3", status: "cancelled"), Run("r4", status: "running")]);
        var ended = string.Join("|", boards.Select(Columns));
        Check(ended == "done,done,done|done,err,err|done,stop,stop|done,run,-", "a free board by how its request ended: " + ended);
        var results = Words(boards.Select(b => Tone.OfCard(b.Columns[2].Cards[0], b.Columns[2])));
        Check(results == "done,err,stop,-", "the 결과 card by how its request ended: " + results);
        return Task.CompletedTask;
    }

    internal static Task APipelineTintsItsPhasesAndEachCardByItsOwnStatus()
    {
        var style = Style("ouroboros");
        var waiting = Boards([Run("r1", "ooo interview 시작", agents: [Agent("x"), Agent("y", "x", "failed")]), Run("r2", "ooo seed", status: "waiting")], style)[0];
        Check(Columns(waiting) == "done,done,wait,-,-,-", "the phases done, the waiting request's phase waits, the rest to come: " + Columns(waiting));
        Check(Cards(waiting.Columns[1]) == "done,done,err", "each card its own status's: " + Cards(waiting.Columns[1]));
        var running = Boards([Run("r1", "ooo interview 시작"), Run("r2", "ooo seed", status: "running")], style)[0];
        Check(Columns(running) == "done,done,run,-,-,-", "the running request's phase runs: " + Columns(running));
        return Task.CompletedTask;
    }

    /// <summary>native/windows/web, beside this project.</summary>
    private static string WebFolder([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here)!)!, "web");

    /// <summary>
    /// The words the host sends (MainWindow.WebShell.Mighty.cs ToneWord: a tone lowercased; <see cref="Tone"/> never gives idle) are
    /// the ones the page turns into classes (web/src/mighty/structureTone.ts <c>tones</c>), so no state is dropped on the way.
    /// </summary>
    internal static Task TheHostsToneWordsAreThePagesClasses()
    {
        var host = Enum.GetValues<DesignTone>().Where(tone => tone != DesignTone.Idle).Select(tone => tone.ToString().ToLowerInvariant()).Order().ToList();
        var source = File.ReadAllText(Path.Combine(WebFolder(), "src", "mighty", "structureTone.ts"));
        var set = Regex.Match(source, @"const tones = new Set\(\[([^\]]*)\]\)");
        Check(set.Success, "structureTone.ts names its tones in `const tones = new Set([...])`");
        var page = Regex.Matches(set.Groups[1].Value, "'([a-z]+)'").Select(m => m.Groups[1].Value).Order().ToList();
        Check(host.SequenceEqual(page), $"host [{string.Join(",", host)}] vs page [{string.Join(",", page)}]");
        return Task.CompletedTask;
    }

    internal static Task ACurrentColumnTakesItsNewestRequestsStatus()
    {
        var style = Style("ouroboros");
        // The pipeline stands at 시드 with its request over: the step reads as that request ended.
        var seed = new[] { "completed", "stopped", "cancelled", "failed" }.Select(status =>
        {
            var board = Boards([Run("r1", "ooo interview 시작"), Run("r2", "ooo seed", status: status)], style)[0];
            Check(board.Columns[2].Step.Step == Step.Current, status + ": the pipeline stands at 시드");
            return Tone.OfColumn(board.Columns[2]);
        }).ToList();
        Check(Words(seed) == "done,stop,stop,err", "the current step by how its newest request ended: " + Words(seed));
        // The pane stands at 실행 for the next request, which no request reached yet: the current step runs.
        var moved = Boards([Run("r1", "ooo interview 시작"), Run("r2", "ooo seed")], style, "run", startingNew: true)[0];
        Check(moved.Columns[3].Step.Step == Step.Current && moved.Columns[3].Cards.Count == 0, "the pane's next phase is current and empty");
        Check(Tone.OfColumn(moved.Columns[3]) == DesignTone.Run, "an empty current step runs: " + Tone.OfColumn(moved.Columns[3]));
        return Task.CompletedTask;
    }
}
