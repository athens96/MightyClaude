using MightyClaude.Core;

internal static class UserEnvironmentVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task Repair()
    {
        // The helper's environment is SystemRoot alone: bare.
        var helper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["SystemRoot"] = "C:\\Windows" };
        string? Read(Dictionary<string, string> env, string name) => env.TryGetValue(name, out var value) ? value : null;
        Check(UserEnvironment.IsBare(name => Read(helper, name)), "an environment with only SystemRoot is bare");
        Check(!UserEnvironment.IsBare(name => name switch { "USERPROFILE" => "C:\\Users\\u", "APPDATA" => "a", "LOCALAPPDATA" => "l", _ => null }), "a whole environment is not bare");
        Check(UserEnvironment.IsBare(name => name == "USERPROFILE" ? "" : "x"), "an empty USERPROFILE is bare");

        var fresh = UserEnvironment.Parse("=C:=C:\\\0USERPROFILE=C:\\Users\\u\0APPDATA=C:\\Users\\u\\AppData\\Roaming\0PATHEXT=.COM;.EXE;.BAT;.CMD\0SystemRoot=D:\\Other\0Path=C:\\Windows\\System32\0EMPTY=\0\0");
        Check(!fresh.ContainsKey("") && !fresh.Keys.Any(k => k.StartsWith('=')), "drive-letter entries are left out");
        Check(fresh["userprofile"] == "C:\\Users\\u" && fresh["PATH"] == "C:\\Windows\\System32", "the parsed block is case-blind");

        var missing = UserEnvironment.Missing(name => Read(helper, name), fresh).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        Check(missing.ContainsKey("USERPROFILE") && missing.ContainsKey("APPDATA") && missing["PATHEXT"] == ".COM;.EXE;.BAT;.CMD" && missing.ContainsKey("Path"), "the missing user variables are added");
        Check(!missing.ContainsKey("SystemRoot"), "a variable the process already has keeps its value");
        Check(!missing.ContainsKey("EMPTY"), "an empty fresh value adds nothing");

        // On Windows the real block names the user's home, so a relaunched app has one.
        if (OperatingSystem.IsWindows() && UserEnvironment.Fresh() is { } real)
            Check(real.TryGetValue("USERPROFILE", out var home) && home.Length > 0 && real.ContainsKey("PATHEXT"), "the user's fresh environment has USERPROFILE and PATHEXT");
        return Task.CompletedTask;
    }
}
