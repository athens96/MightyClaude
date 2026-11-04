namespace MightyClaude.Core;

public static partial class MightyStructure
{
    /// <summary>
    /// The 파이프라인 view's colours by state (M/MightyCore/MightyStructureTone.swift, spec 2026-10-25): every column takes its step's
    /// tone as a wash under its cards, and every card its own as a tint and a bar down its leading edge (<see cref="DesignMetrics.Pipeline"/>).
    /// The tones are concept D's; there is no colour of their own. A step still to come takes none. The web shell gets these words from
    /// the host (MainWindow.WebShell.Mighty.cs); the WinUI view reads them here.
    /// </summary>
    public static class Tone
    {
        /// <summary>A card's status, in the block buckets (<see cref="MightyTimeline.Status"/>): failed is an error, cancelled and interrupted are stops, anything still in motion runs; <c>done</c> is done and <c>pending</c> none.</summary>
        public static DesignTone? OfStatus(string status) => status switch
        {
            "pending" => null,
            "done" => DesignTone.Done,
            _ => StatusGlyph.Tone(MightyTimeline.Status(status)),
        };

        /// <summary>A step's: done, error and stopped in their own colours, the current step running, or waiting while its request waits; pending none.</summary>
        public static DesignTone? OfStep(Step step, bool waiting = false) => step switch
        {
            Step.Done => DesignTone.Done,
            Step.Current => waiting ? DesignTone.Wait : DesignTone.Run,
            Step.Error => DesignTone.Err,
            Step.Stopped => DesignTone.Stop,
            _ => null,
        };

        /// <summary>
        /// A column's: a done, error, stopped or pending step's own; the current step's follows the column's newest request (its last
        /// card that is not a sub-agent's): once that request finished (<see cref="Item.Finished"/>: it and every sub-agent and task
        /// ended) its status's, completed done, failed an error, cancelled or interrupted a stop; until then it runs, or waits while its
        /// status waits; no request at all runs. A free board's 결과 whose request awaits its background work's notices
        /// (<see cref="Item.Awaiting"/>) waits, as its 알림 대기 중 title does (macOS Tone.column).
        /// </summary>
        public static DesignTone? OfColumn(BoardColumn column) =>
            column.Step.Step == Step.Done && column.Cards.Any(card => card.Kind == CardKind.Result && card.Item.Awaiting) ? DesignTone.Wait
            : column.Step.Step == Step.Current ? Current(column.Cards.LastOrDefault(card => card.Kind != CardKind.Agent)?.Item) : OfStep(column.Step.Step);

        /// <summary>The current step's tone for its newest request: its status's once it finished, else running or waiting.</summary>
        private static DesignTone Current(Item? newest)
        {
            if (newest is null) return DesignTone.Run;
            var tone = OfStatus(newest.Status) ?? DesignTone.Run;
            return newest.Finished ? tone : tone == DesignTone.Wait ? DesignTone.Wait : DesignTone.Run;
        }

        /// <summary>A card's: a free board's 요청 and 결과 stand for their step and take their column's; the 실행 card, a styled board's request and every sub-agent, task and 작업 알림 card their own status's.</summary>
        public static DesignTone? OfCard(Card card, BoardColumn column) => card.Kind switch
        {
            CardKind.Request or CardKind.Result => OfColumn(column),
            CardKind.Agent => OfStatus(card.Node!.Status),
            _ => OfStatus(card.Item.Status),
        };
    }
}
