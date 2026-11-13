// What C# MightyCode.Core answers for fixed inputs, written as JSON for the Rust port's parity check (tests/parity.rs).
// Only public Core functions are called; every input is listed here so the Rust side feeds the same ones.
// Regenerate from the repository root (PowerShell; the build output goes to the temp folder, never into the tree):
//   dotnet run -c Release --artifacts-path "$env:TEMP\mighty-parity" --project native/tauri/crates/mighty-platform-windows/tests/parity -- native/tauri/crates/mighty-platform-windows/tests/parity/expected.json
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;

var output = args.Length > 0 ? args[0] : throw new ArgumentException("the output file");
const string Nonce = "0123456789abcdef0123456789abcdef";

JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
JsonObject Map(IReadOnlyDictionary<string, string>? values)
{
    var map = new JsonObject();
    if (values is not null) foreach (var (key, value) in values) map[key] = value;
    return map;
}

// ChildProcess.QuoteWindows
string[] quoteInputs = ["", "plain", "a b", "a\"b", @"C:\dir\", @"C:\my dir\", "a\\\"b c", "tab\there", "nbsp\u00a0x", "ideographic\u3000x",
    "line\nbreak", @"\\server\share\", "back\\\\\\\"quote", "\"", "\\", "\\\"", "한글 인자", "trailing space ", " leading", "%PATH%",
    "a&b|c<d>e^(f)", "x!y!", "say \"hi\"", @"two\\", "semi;colon,comma=equals"];
var quote = new JsonArray(quoteInputs.Select(input => (JsonNode?)new JsonObject { ["input"] = input, ["output"] = ChildProcess.QuoteWindows(input) }).ToArray());

// WindowsCommand.BatchCommandLine
string[] tricky = ["plain", "with space", "amp&ersand", "caret^here", "pipe|lt<gt>", "paren(s)", @"trailing\", @"two\\", @"mid\dle", "한글 인자", "",
    "semi;colon,comma=equals", "tab\there"];
(string Batch, string[] Arguments)[] batchInputs = [
    (@"C:\Program Files\nodejs\npm.cmd", ["install", "-g", "a&b|c<d>e^(f)", @"C:\dir\"]),
    (@"C:\npm\claude.cmd", ["plugin", "list"]),
    ("x.cmd", []), ("x.cmd", [""]), ("x.cmd", tricky), (@"C:\shim dir & ^carets (x)\tool.cmd", tricky),
    ("x.cmd", ["a\"b"]), ("x.cmd", ["%PATH%"]), ("x.cmd", ["x!y!"]), ("x.cmd", ["a\nb"]), ("x.cmd", ["a\rb"]), ("x.cmd", ["a\0b"]),
    (@"C:\100%\x.cmd", []), ("x.cmd", ["fine", "say \"hi\""]),
];
var batch = new JsonArray(batchInputs.Select(input =>
{
    string? line;
    try { line = WindowsCommand.BatchCommandLine(input.Batch, input.Arguments); } catch (ArgumentException) { line = null; }
    return (JsonNode?)new JsonObject { ["batch"] = input.Batch, ["arguments"] = Strings(input.Arguments), ["output"] = line };
}).ToArray());

// WindowsCommand.Launch: the given environment's names, NO_COLOR and NoDefaultCurrentDirectoryInExePath as the launch leaves them.
(string Resolved, string[] Arguments, Dictionary<string, string>? Environment, Dictionary<string, string>? Variables)[] launchInputs = [
    (@"C:\npm\claude.cmd", ["plugin", "list"], null, new() { ["SystemRoot"] = @"D:\Win" }),
    (@"C:\git\git.exe", ["--version"], new() { ["PATH"] = "x" }, null),
    (@"C:\x\TOOL.BAT", ["a b", "c&d"], new() { ["no_color"] = "0", ["noDefaultCurrentDirectoryInExePath"] = "0", ["Keep"] = "" }, null),
    (@"C:\x\tool.exe", ["a\"b"], null, null),
];
var launch = new JsonArray(launchInputs.Select(input =>
{
    var (info, shellCommand) = WindowsCommand.Launch(input.Resolved, input.Arguments, Path.GetTempPath(), input.Environment, input.Variables);
    var names = (input.Environment?.Keys ?? Enumerable.Empty<string>()).Concat(["NO_COLOR", WindowsCommand.NoCurrentDirectorySearch]);
    var environment = new JsonObject();
    foreach (var (key, value) in info.Environment) if (value is not null && names.Contains(key, StringComparer.OrdinalIgnoreCase)) environment[key] = value;
    return (JsonNode?)new JsonObject
    {
        ["resolved"] = input.Resolved, ["arguments"] = Strings(input.Arguments),
        ["environment"] = input.Environment is null ? null : Map(input.Environment), ["variables"] = input.Variables is null ? null : Map(input.Variables),
        ["programIsInterpreter"] = info.FileName == WindowsCommand.CommandInterpreter(input.Variables), ["program"] = info.FileName,
        ["argumentList"] = Strings(info.ArgumentList), ["shellCommand"] = shellCommand, ["environmentAfter"] = environment,
    };
}).ToArray());

// PseudoTerminal.ShellArguments
string[] shells = [@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"C:\Windows\System32\cmd.exe", @"C:\tools\pwsh.exe", @"C:\x\PowerShell"];
string?[] commands = [null, "ls", "echo \"a b\""];
var shellArguments = new JsonArray();
foreach (var shell in shells) foreach (var command in commands) foreach (var noProfile in new[] { false, true })
    shellArguments.Add(new JsonObject { ["shell"] = shell, ["command"] = command, ["noProfile"] = noProfile, ["output"] = Strings(PseudoTerminal.ShellArguments(shell, command, noProfile)) });

// WindowsShellEnvironment.Command / Arguments
string? Refused(Func<string> make) { try { return make(); } catch (ArgumentException) { return null; } }
var environmentCommand = new JsonObject
{
    ["nonce"] = Nonce, ["command"] = WindowsShellEnvironment.Command(Nonce), ["arguments"] = Strings(WindowsShellEnvironment.Arguments(Nonce)),
    ["uppercaseNonceCommand"] = Refused(() => WindowsShellEnvironment.Command(Nonce.ToUpperInvariant())),
    ["shortNonceCommand"] = Refused(() => WindowsShellEnvironment.Command("abc")),
};

// WindowsShellEnvironment.Parse
string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
string Frame(string nonce, string payload) => "profile chatter\n\0MIGHTY_ENV_" + nonce + "\0" + payload + "\0MIGHTY_END_" + nonce + "\0after profile output";
var valid = B64(JsonSerializer.Serialize(new Dictionary<string, string> { ["Path"] = @"C:\trusted bin", ["AWS_BEARER_TOKEN_BEDROCK"] = "fixture-secret=\nsecond-line" }));
// 13 bytes: the last group is one byte, two characters and "=="; the second of those characters carries 4 unused bits.
var odd = B64("{\"PATH\":\"xy\"}");
var nonCanonical = odd[..^3] + (char)(odd[^3] + 1) + odd[^2..];
var many = (int count) => "{\"PATH\":\"x\"" + string.Concat(Enumerable.Range(1, count - 1).Select(i => ",\"V" + i + "\":\"v\"")) + "}";
(string Name, string Output, string Nonce)[] parseInputs = [
    ("valid", Frame(Nonce, valid), Nonce),
    ("another invocation's nonce", Frame(Nonce, valid), new string('a', 32)),
    ("repeated frame", Frame(Nonce, valid) + Frame(Nonce, valid), Nonce),
    ("end marker twice", Frame(Nonce, valid) + "\0MIGHTY_END_" + Nonce + "\0", Nonce),
    ("uppercase nonce", Frame(Nonce.ToUpperInvariant(), valid), Nonce.ToUpperInvariant()),
    ("empty object", Frame(Nonce, B64("{}")), Nonce),
    ("array", Frame(Nonce, B64("[]")), Nonce),
    ("names in two cases", Frame(Nonce, B64("{\"PATH\":\"x\",\"path\":\"y\"}")), Nonce),
    ("a name twice", Frame(Nonce, B64("{\"PATH\":\"x\",\"PATH\":\"y\"}")), Nonce),
    ("a name with =", Frame(Nonce, B64("{\"PATH\":\"x\",\"bad=name\":\"x\"}")), Nonce),
    ("a true value", Frame(Nonce, B64("{\"PATH\":\"x\",\"AWS_KEY\":true}")), Nonce),
    ("a NUL in a value", Frame(Nonce, B64("{\"PATH\":\"x\",\"AWS_KEY\":\"bad\\u0000value\"}")), Nonce),
    ("an object value", Frame(Nonce, B64("{\"PATH\":\"x\",\"A\":{\"b\":\"c\"}}")), Nonce),
    ("a deep value", Frame(Nonce, B64("{\"PATH\":\"x\",\"A\":[[[\"c\"]]]}")), Nonce),
    ("a null value", Frame(Nonce, B64("{\"PATH\":\"x\",\"A\":null}")), Nonce),
    ("a number PATH", Frame(Nonce, B64("{\"PATH\":1}")), Nonce),
    ("a blank PATH", Frame(Nonce, B64("{\"PATH\":\"  \"}")), Nonce),
    ("no PATH", Frame(Nonce, B64("{\"A\":\"b\"}")), Nonce),
    ("a control character in a name", Frame(Nonce, B64("{\"PATH\":\"x\",\"A\\u0001\":\"b\"}")), Nonce),
    ("a C1 control character in a name", Frame(Nonce, B64("{\"PATH\":\"x\",\"A\\u0085\":\"b\"}")), Nonce),
    ("an empty name", Frame(Nonce, B64("{\"PATH\":\"x\",\"\":\"b\"}")), Nonce),
    ("escapes and Unicode", Frame(Nonce, B64("{\"PATH\":\"x\",\"MIGHTY\":\"한글 \\u00e9 \\\"q\\\" \\\\ \\n\"}")), Nonce),
    ("a byte order mark", Frame(Nonce, Convert.ToBase64String([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"PATH\":\"bom\"}")])), Nonce),
    ("tabs inside the base64", Frame(Nonce, valid[..8] + "\t\t\t\t" + valid[8..]), Nonce),
    ("one tab inside the base64", Frame(Nonce, valid[..8] + "\t" + valid[8..]), Nonce),
    ("a space inside the base64", Frame(Nonce, valid[..8] + "    " + valid[8..]), Nonce),
    ("a line break inside the base64", Frame(Nonce, valid[..8] + "\r\n\r\n" + valid[8..]), Nonce),
    ("unused trailing bits set", Frame(Nonce, nonCanonical), Nonce),
    ("a character outside base64", Frame(Nonce, "*" + valid[1..]), Nonce),
    ("a length not a multiple of 4", Frame(Nonce, valid[..^1]), Nonce),
    ("no payload", Frame(Nonce, ""), Nonce),
    ("trailing text after the JSON", Frame(Nonce, B64("{\"PATH\":\"x\"} x")), Nonce),
    ("a comment in the JSON", Frame(Nonce, B64("{\"PATH\":\"x\"/*c*/}")), Nonce),
    ("a trailing comma", Frame(Nonce, B64("{\"PATH\":\"x\",}")), Nonce),
    ("4096 names", Frame(Nonce, B64(many(4096))), Nonce),
    ("4097 names", Frame(Nonce, B64(many(4097))), Nonce),
    ("a value of 131072 characters", Frame(Nonce, B64("{\"PATH\":\"" + new string('v', 131072) + "\"}")), Nonce),
    ("a value of 131073 characters", Frame(Nonce, B64("{\"PATH\":\"" + new string('v', 131073) + "\"}")), Nonce),
    ("no frame", "profile chatter only", Nonce),
];
var parse = new JsonArray(parseInputs.Select(input =>
{
    var values = WindowsShellEnvironment.Parse(input.Output, input.Nonce);
    var large = input.Output.Length > 4096;
    return (JsonNode?)new JsonObject
    {
        ["name"] = input.Name, ["output"] = large ? null : input.Output, ["nonce"] = input.Nonce,
        ["count"] = values?.Count, ["values"] = values is null || large ? null : Map(values),
    };
}).ToArray());

// UserEnvironment.Parse
string[] blocks = ["=C:=C:\\\0USERPROFILE=C:\\Users\\u\0APPDATA=C:\\Users\\u\\AppData\\Roaming\0PATHEXT=.COM;.EXE;.BAT;.CMD\0SystemRoot=D:\\Other\0Path=C:\\Windows\\System32\0EMPTY=\0\0",
    "A=1\0a=2\0\0", "=hidden=x\0B=\0\0", "noequals\0C==x\0\0", ""];
var userParse = new JsonArray(blocks.Select(block => (JsonNode?)new JsonObject { ["block"] = block, ["values"] = Map(UserEnvironment.Parse(block)) }).ToArray());

// CliDiscovery
var windowsEnvironment = new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\me", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local", ["APPDATA"] = @"C:\Users\me\AppData\Roaming", ["ProgramFiles"] = @"C:\Program Files", ["SystemRoot"] = @"C:\Windows" };
Dictionary<string, string> With(string key, string value) => new(windowsEnvironment) { [key] = value };
Dictionary<string, string>[] hermesEnvironments = [windowsEnvironment, With("hermes_home", @"D:\hermes"), With("HERMES_HOME", "hermes"), With("HERMES_HOME", @"D:\h\"), new(), new() { ["LOCALAPPDATA"] = "relative" }, new() { ["LOCALAPPDATA"] = "C:x" }];
var hermes = new JsonArray(hermesEnvironments.Select(environment => (JsonNode?)new JsonObject { ["environment"] = Map(environment), ["folder"] = CliDiscovery.HermesInstallFolder(environment) }).ToArray());
(string Provider, string[] Directories, bool Windows)[] fileInputs = [
    ("opencode", [@"C:\tools", @"C:\Users\me\AppData\Roaming\npm"], true), ("hermes", [@"C:\tools"], true), ("hermes", [@"C:\USERS\ME\APPDATA\LOCAL\HERMES\BIN"], true),
    ("pi", [@"C:\Users\me\AppData\Roaming\npm"], true), ("codex", [@"C:\tools"], true), ("gemini", [@"C:\tools"], true), ("claude", [@"C:\tools", @"c:\TOOLS", "", "a\0b"], true),
    ("hermes", ["/usr/local/bin"], false), ("opencode", ["/usr/local/bin", "/usr/local/bin"], false), ("claude", [@"C:\tools\"], true),
];
var files = new JsonArray(fileInputs.Select(input => (JsonNode?)new JsonObject
{
    ["provider"] = input.Provider, ["directories"] = Strings(input.Directories), ["windows"] = input.Windows,
    ["files"] = Strings(CliDiscovery.Files(input.Provider, input.Directories, input.Windows, windowsEnvironment)),
}).ToArray());
(string Program, string? Output)[] versionInputs = [("hermes", "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882\nInstall directory: C:\\x\nUp to date\n"),
    ("hermes", "\r\n  Hermes Agent v0.23.1  \r\nInstall method: git\r\n"), ("hermes", ""), ("hermes", null), ("claude", "2.1.300 (Claude Code)\n"),
    ("codex", " codex-cli 0.160.0 "), ("opencode", "opencode v2.2.7\r\n"), ("claude", "a\nb\n")];
var versions = new JsonArray(versionInputs.Select(input => (JsonNode?)new JsonObject { ["program"] = input.Program, ["output"] = input.Output, ["text"] = CliDiscovery.VersionText(input.Program, input.Output) }).ToArray());

// TerminalInputPolicy.HasLineBreak
string[] lines = ["echo one && echo two", "", "a\nb", "a\rb", "a\u2028b", "a\u2029b", "a\u0085b", "a\u000Bb", "a\u000Cb"];
var lineBreaks = new JsonArray(lines.Select(text => (JsonNode?)new JsonObject { ["text"] = text, ["value"] = TerminalInputPolicy.HasLineBreak(text) }).ToArray());

var root = new JsonObject
{
    ["generatedBy"] = "native/tauri/crates/mighty-platform-windows/tests/parity (C# MightyCode.Core)",
    ["quote"] = quote, ["batch"] = batch, ["launch"] = launch, ["shellArguments"] = shellArguments, ["environmentCommand"] = environmentCommand,
    ["environmentParse"] = parse, ["userEnvironmentParse"] = userParse, ["hermesInstallFolder"] = hermes, ["discoveryFiles"] = files,
    ["versionText"] = versions, ["hasLineBreak"] = lineBreaks,
};
File.WriteAllText(output, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));
Console.WriteLine("wrote " + output);
