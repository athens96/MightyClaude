namespace MightyClaude.Core;

/// <summary>
/// Text the app puts into a terminal for the user: a style's install command, a command the app wrote itself
/// (M/MightyCore/TerminalInputPolicy.swift:17-34). Whatever its source, a line break means nothing is put in at all, and Enter
/// follows only what was taken whole and only where the app says so.
/// </summary>
public static class TerminalInputPolicy
{
    public enum Outcome { Pasted, PastedAndRan, Refused, Failed }
    public static string NewlineRefusal => Locale.Get("terminal.input.newlineRefused");
    public static bool HasLineBreak(string text) => text.AsSpan().IndexOfAny("\n\r\u2028\u2029") >= 0;
    /// <param name="write">Puts text into the terminal; false when the terminal took none of it.</param>
    public static Outcome Apply(string text, bool autoRun, Func<string, bool> write)
    {
        if (HasLineBreak(text)) return Outcome.Refused;
        if (!write(text)) return Outcome.Failed;
        if (!autoRun) return Outcome.Pasted;
        return write("\r") ? Outcome.PastedAndRan : Outcome.Failed;
    }
}
