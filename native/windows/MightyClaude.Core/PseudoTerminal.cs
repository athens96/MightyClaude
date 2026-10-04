using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace MightyClaude.Core;

/// <summary>A persistent ConPTY session. Input, output and teardown run independently;
/// ClosePseudoConsole must never block the UI or the thread draining its output.
/// https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session</summary>
public sealed class PseudoTerminal : IAsyncDisposable
{
    private readonly object lifecycle = new();
    private nint console, process, job;
    private readonly Stream input, output;
    private readonly Channel<byte[]> writes = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task reader, writer;
    private Task? disposal;
    public int ProcessId { get; }
    public Task<int> Completion { get; }

    private PseudoTerminal(nint console, nint process, nint job, int pid, Stream input, Stream output, Action<string> receive)
    {
        this.console = console; this.process = process; this.job = job;
        this.input = input; this.output = output; ProcessId = pid;
        reader = Task.Factory.StartNew(() =>
        {
            using var text = new StreamReader(output, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            var buffer = new char[8192];
            try { int count; while ((count = text.Read(buffer, 0, buffer.Length)) > 0) receive(new string(buffer, 0, count)); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        writer = Task.Run(async () =>
        {
            try { await foreach (var bytes in writes.Reader.ReadAllAsync()) { await input.WriteAsync(bytes); await input.FlushAsync(); } }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { writes.Writer.TryComplete(); }
        });
        Completion = Task.Factory.StartNew(() =>
        {
            if (ChildProcess.Native.WaitForSingleObject(process, uint.MaxValue) != 0 || !ChildProcess.Native.GetExitCodeProcess(process, out var code)) throw new Win32Exception();
            return unchecked((int)code);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public static string DefaultShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") is var powershell && File.Exists(powershell)
        ? powershell : Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    public static PseudoTerminal Start(string directory, Action<string> receive, int columns = 100, int rows = 30, string? executable = null, IReadOnlyList<string>? arguments = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY requires Windows.");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new ArgumentException("A local workspace directory is required.", nameof(directory));
        ArgumentNullException.ThrowIfNull(receive);
        executable ??= DefaultShell;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new FileNotFoundException("Terminal shell was not found.", executable);
        arguments ??= Path.GetFileNameWithoutExtension(executable).Equals("powershell", StringComparison.OrdinalIgnoreCase) ? ["-NoLogo"] : ["/d"];
        nint inRead = 0, inWrite = 0, outRead = 0, outWrite = 0, pc = 0, attrs = 0, job = 0, environmentBlock = 0;
        var attrsInitialized = false;
        Stream? ownedInput = null, ownedOutput = null;
        ChildProcess.Native.ProcessInformation child = default;
        try
        {
            var security = new ChildProcess.Native.SecurityAttributes { Length = Marshal.SizeOf<ChildProcess.Native.SecurityAttributes>() };
            if (!ChildProcess.Native.CreatePipe(out inRead, out inWrite, ref security, 0) || !ChildProcess.Native.CreatePipe(out outRead, out outWrite, ref security, 0)) throw new Win32Exception();
            Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(Size(columns, rows), inRead, outWrite, 0, out pc));
            nuint bytes = 0;
            Native.InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
            attrs = Marshal.AllocHGlobal(checked((int)bytes));
            if (!Native.InitializeProcThreadAttributeList(attrs, 1, 0, ref bytes)) throw new Win32Exception();
            attrsInitialized = true;
            if (!Native.UpdateProcThreadAttribute(attrs, 0, 0x00020016, pc, (nuint)nint.Size, 0, 0)) throw new Win32Exception();
            job = ChildProcess.Native.CreateJobObject(0, null);
            if (job == 0) throw new Win32Exception();
            var limits = new ChildProcess.Native.ExtendedLimits { Basic = new() { LimitFlags = 0x2000 } };
            if (!ChildProcess.Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ChildProcess.Native.ExtendedLimits>())) throw new Win32Exception();
            var startup = new Native.StartupInfoEx { Startup = new() { Size = Marshal.SizeOf<Native.StartupInfoEx>() }, Attributes = attrs };
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(ChildProcess.QuoteWindows)));
            var variables = environment ?? CliEnvironment.Current();
            if (variables.Any(pair => pair.Key.Length == 0 || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0')))
                throw new ArgumentException("Invalid terminal environment.", nameof(environment));
            environmentBlock = Marshal.StringToHGlobalUni(string.Join('\0', variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
            if (!Native.CreateProcess(executable, command, 0, 0, false, 0x00080404, environmentBlock, directory, ref startup, out child)) throw new Win32Exception();
            if (!ChildProcess.Native.AssignProcessToJobObject(job, child.Process) || ChildProcess.Native.ResumeThread(child.Thread) == uint.MaxValue) throw new Win32Exception();
            ChildProcess.Native.CloseHandle(child.Thread); child.Thread = 0;
            ChildProcess.Native.CloseHandle(inRead); inRead = 0;
            ChildProcess.Native.CloseHandle(outWrite); outWrite = 0;
            ownedInput = new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write); inWrite = 0;
            ownedOutput = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read); outRead = 0;
            var result = new PseudoTerminal(pc, child.Process, job, child.ProcessId, ownedInput, ownedOutput, receive);
            ownedInput = ownedOutput = null;
            pc = job = child.Process = 0;
            return result;
        }
        catch { if (child.Process != 0) ChildProcess.Native.TerminateProcess(child.Process, 1); throw; }
        finally
        {
            if (attrsInitialized) Native.DeleteProcThreadAttributeList(attrs);
            if (attrs != 0) Marshal.FreeHGlobal(attrs);
            if (environmentBlock != 0) Marshal.FreeHGlobal(environmentBlock);
            ownedInput?.Dispose(); ownedOutput?.Dispose();
            // With no output reader yet, close the read end before closing a failed console.
            foreach (var handle in new[] { inRead, inWrite, outRead, outWrite, child.Process, child.Thread, job }) if (handle != 0) ChildProcess.Native.CloseHandle(handle);
            if (pc != 0) Native.ClosePseudoConsole(pc);
        }
    }

    public bool TryWrite(string text)
    {
        if (text.Length > 65_536) return false;
        lock (lifecycle) return disposal is null && !Completion.IsCompleted && writes.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    }

    public void Resize(int columns, int rows)
    {
        lock (lifecycle) if (disposal is null && console != 0) Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(console, Size(columns, rows)));
    }
    private static Native.Coord Size(int columns, int rows) => new() { X = (short)Math.Clamp(columns, 2, 1000), Y = (short)Math.Clamp(rows, 1, 1000) };

    public ValueTask DisposeAsync()
    {
        lock (lifecycle) return new(disposal ??= Task.Run(CloseAsync));
    }
    private async Task CloseAsync()
    {
        writes.Writer.TryComplete();
        if (job != 0) ChildProcess.Native.TerminateJobObject(job, 1);
        if (console != 0) { Native.ClosePseudoConsole(console); console = 0; }
        // The output drain stays alive until ClosePseudoConsole's final frame has passed.
        try { await Completion; await reader; await writer; }
        finally
        {
            input.Dispose(); output.Dispose();
            if (job != 0) { ChildProcess.Native.CloseHandle(job); job = 0; }
            if (process != 0) { ChildProcess.Native.CloseHandle(process); process = 0; }
        }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Coord { internal short X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal ChildProcess.Native.StartupInfo Startup; internal nint Attributes; }
        [DllImport("kernel32.dll")] internal static extern int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint console);
        [DllImport("kernel32.dll")] internal static extern int ResizePseudoConsole(nint console, Coord size);
        [DllImport("kernel32.dll")] internal static extern void ClosePseudoConsole(nint console);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint bytes);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ChildProcess.Native.ProcessInformation child);
    }
}
