using System.Net;
using System.Text.Json;
using MightyClaude.Core;

if (args is ["--long-child", var pidFile]) { var ready = pidFile + ".ready"; await File.WriteAllTextAsync(ready, Environment.ProcessId.ToString()); File.Move(ready, pidFile); Console.WriteLine("READY"); await Task.Delay(60000); return; }
if (args.Length >= 3 && args[0] == "--fake-cli") { await Verification.FakeCliAsync(args[1], args[2], args.Skip(3).ToArray()); return; }
if (args is ["--serve-fixture", var manifest])
{
    var directory = Path.Combine(Path.GetTempPath(), "mighty-csharp-peer-" + Wire.Id()); Directory.CreateDirectory(directory);
    var workspace = new Workspace { Name = "C# integration fixture", Path = directory };
    await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null));
    await using var host = new RemoteServer("C# native fixture", [workspace.Id], () => [workspace], id => id == workspace.Id ? Task.FromResult(workspace) : throw new ArgumentException("Unknown workspace"), () => catalog.GetRuntimeAsync(), emit => new RunManager(id => id == workspace.Id ? Task.FromResult(workspace) : throw new ArgumentException("Unknown workspace"), catalog, "", emit), true);
    try
    {
        await host.StartAsync(IPAddress.Loopback, 0);
        await StateStore.AtomicWriteAsync(Path.GetFullPath(manifest), JsonSerializer.SerializeToUtf8Bytes(new { address = host.Address, token = host.Token, workspaceId = workspace.Id }, Wire.Json));
        Console.WriteLine("C# remote fixture ready (credentials only in manifest).");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline && !File.Exists(manifest + ".stop")) await Task.Delay(100);
    }
    finally { await host.DisposeAsync(); if (File.Exists(manifest)) File.Delete(manifest); if (File.Exists(manifest + ".stop")) File.Delete(manifest + ".stop"); Directory.Delete(directory, true); }
    return;
}
await Verification.RunAsync();
