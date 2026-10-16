using MightyClaude.Core;
using static DelegationSupport;

/// <summary>When a save of the workspace state starts, ported from the Mac's StateSaveCacheTests (the schedule half).</summary>
internal static class StateSaveVerification
{
    internal static Task TheScheduleKeepsTheDebounceAndTheGapAndNeverWaitsForEver()
    {
        Check(SaveSchedule.Delay(null) == 0.22 && SaveSchedule.Delay(0) == 1.5 && Math.Abs(SaveSchedule.Delay(1.0) - 0.5) < 1e-9, "the debounce, then the gap from the last start");
        // Past the gap only the debounce is left; a clock that went backwards or a broken value never shortens the wait.
        Check(SaveSchedule.Delay(1.4) == 0.22 && SaveSchedule.Delay(60) == 0.22 && SaveSchedule.Delay(-5) == 1.5 && SaveSchedule.Delay(double.NaN) == 0.22
            && Math.Abs(SaveSchedule.Delay(0.3, debounce: 0.1, minimumGap: 2) - 1.7) < 1e-9, "past the gap, the debounce");
        // Inside the gap and freshly asked for: a new change may restart the debounce.
        Check(!SaveSchedule.KeepsPending(0.5, 0.1) && !SaveSchedule.KeepsPending(null, 1.49), "a fresh wait may restart");
        // The gap since the last save began has passed, or changes kept coming for the whole gap: the waiting save fires.
        Check(SaveSchedule.KeepsPending(1.5, 0) && SaveSchedule.KeepsPending(60, 0.05) && SaveSchedule.KeepsPending(null, 1.5) && SaveSchedule.KeepsPending(0.2, 3), "a waiting save is not pushed back past the gap");
        Check(!SaveSchedule.KeepsPending(-5, -5) && !SaveSchedule.KeepsPending(double.NaN, double.NaN) && SaveSchedule.KeepsPending(0.3, 2, minimumGap: 2), "a broken clock never keeps it");
        return Task.CompletedTask;
    }
}
