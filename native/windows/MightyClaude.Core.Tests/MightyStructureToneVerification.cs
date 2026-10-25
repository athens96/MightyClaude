using MightyClaude.Core;
using static MightyStructureVerification;
using Board = MightyClaude.Core.MightyStructure.Board;
using Step = MightyClaude.Core.MightyStructure.Step;
using Tone = MightyClaude.Core.MightyStructure.Tone;

/// <summary>
/// The 파이프라인 view's state colours against the Mac's MightyStructureToneTests: a status, a step, a column and a card take the
/// same tone, a waiting request turns its current step amber, and a step still to come takes none.
/// </summary>
internal static class MightyStructureToneVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static string Words(IEnumerable<DesignTone?> tones) => string.Join(",", tones.Select(tone => tone?.ToString().ToLowerInvariant() ?? "-"));
    private static IReadOnlyList<Board> Boards(IReadOnlyList<MightyGraphRun> runs, StyleEvaluator? style = null) => MightyStructure.Boards(MightyStructure.Make(runs, style, null));
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
}
