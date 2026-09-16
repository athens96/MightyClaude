using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MightyClaude.Core;

/// <summary>Redirected native child; Windows joins a kill-on-close job before its first instruction.</summary>
public sealed class ChildProcess : IAsyncDisposable
{
    private static readonly object WindowsLaunchGate = new();
    private readonly Process? managed;
    private nint nativeProcess, job;
    private bool disposed;
    public StreamWriter Input { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }
    public Task<int> Completion { get; }
    public int Id { get; }

    private ChildProcess(Process process)
    {
        managed = process; Id = process.Id; Input = process.StandardInput; Output = process.StandardOutput; Error = process.StandardError;
        Completion = WaitManagedAsync(process);
    }
    private ChildProcess(nint process, nint jobHandle, int pid, StreamWriter input, StreamReader output, StreamReader error)
    {
        nativeProcess = process; job = jobHandle; Id = pid; Input = input; Output = output; Error = error;
        Completion = Task.Run(() =>
        {
            if (Native.WaitForSingleObject(process, uint.MaxValue) != 0 || !Native.GetExitCodeProcess(process, out var code)) throw new System.ComponentModel.Win32Exception();
            return unchecked((int)code);
        });
    }
    private static async Task<int> WaitManagedAsync(Process process) { await process.WaitForExitAsync(); return process.ExitCode; }
    public static ProcessStartInfo StartInfo(string binary, IEnumerable<string> arguments, string cwd, IDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(binary) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (key, value) in environment) info.Environment[key] = value;
        info.Environment["NO_COLOR"] = "1";
        return info;
    }
    public static ChildProcess Start(ProcessStartInfo info, string? shellCommand = null)
    {
        if (info.UseShellExecute || !info.RedirectStandardInput || !info.RedirectStandardOutput || !info.RedirectStandardError) throw new ArgumentException("Redirected direct process execution is required.");
        if (OperatingSystem.IsWindows()) { lock (WindowsLaunchGate) return StartWindows(info, shellCommand); }
        return new(Process.Start(info) ?? throw new IOException("프로세스를 시작하지 못했습니다."));
    }
    public void Kill()
    {
        try { if (job != 0) Native.TerminateJobObject(job, 1); else if (managed is { HasExited: false }) managed.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        Kill();
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(3));
            ReleaseProcessHandle();
            // TerminateJobObject is asynchronous. The parent can already have exited
            // while a descendant still owns the workspace or attachment files.
            if (job != 0)
            {
                var deadline = Stopwatch.StartNew();
                while (true)
                {
                    if (!Native.QueryInformationJobObject(job, 1, out var state, (uint)Marshal.SizeOf<Native.BasicAccounting>(), 0)) throw new System.ComponentModel.Win32Exception();
                    if (state.ActiveProcesses == 0) break;
                    if (deadline.Elapsed >= TimeSpan.FromSeconds(3)) throw new TimeoutException($"Windows 실행의 자식 프로세스 {state.ActiveProcesses}개가 아직 종료 중입니다.");
                    await Task.Delay(10);
                }
            }
        }
        finally
        {
            try { Input.Dispose(); Output.Dispose(); Error.Dispose(); managed?.Dispose(); }
            finally
            {
                if (job != 0) { Native.CloseHandle(job); job = 0; }
                ReleaseProcessHandle();
            }
        }
    }
    private void ReleaseProcessHandle()
    {
        if (nativeProcess == 0) return;
        var handle = nativeProcess; nativeProcess = 0;
        if (Completion.IsCompleted) Native.CloseHandle(handle);
        else _ = Completion.ContinueWith(_ => Native.CloseHandle(handle), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public static string QuoteWindows(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes); slashes = 0; result.Append(ch);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static ChildProcess StartWindows(ProcessStartInfo info, string? shellCommand)
    {
        // A hidden inherited console gives cmd built-ins and native CLIs UTF-8 pipes.
        var stdIn = Native.GetStdHandle(-10); var stdOut = Native.GetStdHandle(-11); var stdErr = Native.GetStdHandle(-12);
        if (Native.GetConsoleWindow() == 0) { Native.AllocConsole(); Native.ShowWindow(Native.GetConsoleWindow(), 0); Native.SetStdHandle(-10, stdIn); Native.SetStdHandle(-11, stdOut); Native.SetStdHandle(-12, stdErr); }
        Native.SetConsoleCP(65001); Native.SetConsoleOutputCP(65001);
        var security = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = 1 };
        nint stdinRead = 0, stdinWrite = 0, stdoutRead = 0, stdoutWrite = 0, stderrRead = 0, stderrWrite = 0, environment = 0, job = 0;
        Native.ProcessInformation child = default;
        try
        {
            if (!Native.CreatePipe(out stdinRead, out stdinWrite, ref security, 0) || !Native.CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0) || !Native.CreatePipe(out stderrRead, out stderrWrite, ref security, 0)) throw new System.ComponentModel.Win32Exception();
            foreach (var handle in new[] { stdinWrite, stdoutRead, stderrRead }) if (!Native.SetHandleInformation(handle, 1, 0)) throw new System.ComponentModel.Win32Exception();
            job = Native.CreateJobObject(0, null);
            if (job == 0) throw new System.ComponentModel.Win32Exception();
            var limits = new Native.ExtendedLimits { Basic = new() { LimitFlags = 0x2000 } };
            if (!Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>())) throw new System.ComponentModel.Win32Exception();
            var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Flags = 0x100, StdInput = stdinRead, StdOutput = stdoutWrite, StdError = stderrWrite };
            var command = shellCommand is null ? string.Join(" ", new[] { info.FileName }.Concat(info.ArgumentList).Select(QuoteWindows)) : $"{QuoteWindows(info.FileName)} /d /s /c \"{shellCommand}\"";
            environment = Marshal.StringToHGlobalUni(string.Join('\0', info.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}={p.Value}")) + "\0\0");
            if (!Native.CreateProcess(info.FileName, new StringBuilder(command), 0, 0, true, 0x4 | 0x400, environment, info.WorkingDirectory, ref startup, out child)) throw new System.ComponentModel.Win32Exception();
            if (!Native.AssignProcessToJobObject(job, child.Process) || Native.ResumeThread(child.Thread) == uint.MaxValue) throw new System.ComponentModel.Win32Exception();
            Native.CloseHandle(child.Thread); child.Thread = 0;
            Native.CloseHandle(stdinRead); stdinRead = 0; Native.CloseHandle(stdoutWrite); stdoutWrite = 0; Native.CloseHandle(stderrWrite); stderrWrite = 0;
            var input = new StreamWriter(new FileStream(new SafeFileHandle(stdinWrite, true), FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true }; stdinWrite = 0;
            var output = new StreamReader(new FileStream(new SafeFileHandle(stdoutRead, true), FileAccess.Read), Encoding.UTF8); stdoutRead = 0;
            var error = new StreamReader(new FileStream(new SafeFileHandle(stderrRead, true), FileAccess.Read), Encoding.UTF8); stderrRead = 0;
            var result = new ChildProcess(child.Process, job, child.ProcessId, input, output, error); child.Process = 0; job = 0; return result;
        }
        catch { if (child.Process != 0) Native.TerminateProcess(child.Process, 1); throw; }
        finally
        {
            if (environment != 0) Marshal.FreeHGlobal(environment);
            foreach (var handle in new[] { stdinRead, stdinWrite, stdoutRead, stdoutWrite, stderrRead, stderrWrite, child.Process, child.Thread, job }) if (handle != 0) Native.CloseHandle(handle);
        }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public nint Descriptor; public int InheritHandle; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2Size; public nint Reserved2, StdInput, StdOutput, StdError; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicAccounting { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits { public long UserTime, JobTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out nint read, out nint write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateJobObject(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(nint job, int type, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(nint job, int type, out BasicAccounting state, uint length, nint returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(nint job, nint process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInformation child);
        [DllImport("kernel32.dll")] internal static extern uint ResumeThread(nint thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(nint process, out uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(nint job, uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(nint process, uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AllocConsole();
        [DllImport("kernel32.dll")] internal static extern nint GetConsoleWindow();
        [DllImport("kernel32.dll")] internal static extern nint GetStdHandle(int type);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetStdHandle(int type, nint handle);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleCP(uint page);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleOutputCP(uint page);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    }
}
