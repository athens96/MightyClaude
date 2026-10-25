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
        /// card that is not a sub-agent's): running runs, waiting waits, completed is done, failed an error, cancelled or interrupted a
        /// stop, and anything else, or no request at all, runs.
        /// </summary>
        public static DesignTone? OfColumn(BoardColumn column) =>
            column.Step.Step == Step.Current ? Current(column.Cards.LastOrDefault(card => card.Kind != CardKind.Agent)?.Item.Status) : OfStep(column.Step.Step);

        /// <summary>The current step's tone for its newest request's status, in the block buckets; <c>done</c> is done.</summary>
        private static DesignTone Current(string? status) => status == "done" ? DesignTone.Done : StatusGlyph.Tone(MightyTimeline.Status(status ?? "running"));

        /// <summary>A card's: a free board's 요청 and 결과 stand for their step and take their column's; the 실행 card, a styled board's request and every sub-agent, task and 작업 알림 card their own status's.</summary>
        public static DesignTone? OfCard(Card card, BoardColumn column) => card.Kind switch
        {
            CardKind.Request or CardKind.Result => OfColumn(column),
            CardKind.Agent => OfStatus(card.Node!.Status),
            _ => OfStatus(card.Item.Status),
        };
    }
}
