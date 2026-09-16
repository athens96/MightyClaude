import { join } from 'node:path'

/**
 * A Windows job closes the whole process tree even if cmd.exe exits first.
 * https://learn.microsoft.com/windows/win32/procthread/job-objects
 * The helper never evaluates the launch specification as PowerShell source.
 * Constrained PowerShell hosts fail before launching the requested command.
 */
const JOB_RUNNER = String.raw`
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try {
  Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
public static class MightyJob {
  [StructLayout(LayoutKind.Sequential)] struct BasicLimits {
    public long ProcessTime, JobTime;
    public uint Flags;
    public UIntPtr MinWorkingSet, MaxWorkingSet;
    public uint ActiveProcesses;
    public UIntPtr Affinity;
    public uint Priority, Scheduling;
  }
  [StructLayout(LayoutKind.Sequential)] struct IoCounters {
    public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
  }
  [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits {
    public BasicLimits Basic;
    public IoCounters Io;
    public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
  }
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimits info, uint length);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
  [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
  [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetConsoleCP();
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool AllocConsole();
  [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int id);
  [DllImport("kernel32.dll")] static extern bool SetStdHandle(int id, IntPtr handle);
  [DllImport("kernel32.dll")] static extern bool SetConsoleCP(uint codePage);
  [DllImport("kernel32.dll")] static extern bool SetConsoleOutputCP(uint codePage);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int show);
  static IntPtr job;
  public static void Initialize() {
    job = CreateJobObject(IntPtr.Zero, null);
    var limits = new ExtendedLimits();
    limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits)) || !AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
      throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create the command process group");
    // A headless console can be attached without a visible window. Allocating
    // another console in that case fails; query the attached console itself.
    if (GetConsoleCP() == 0) {
      var input = GetStdHandle(-10); var output = GetStdHandle(-11); var error = GetStdHandle(-12);
      if (!AllocConsole()) {
        var code = Marshal.GetLastWin32Error();
        throw new System.ComponentModel.Win32Exception(code, "Could not prepare the command console (Win32 " + code + ")");
      }
      ShowWindow(GetConsoleWindow(), 0);
      SetStdHandle(-10, input); SetStdHandle(-11, output); SetStdHandle(-12, error);
    }
    SetConsoleCP(65001); SetConsoleOutputCP(65001);
  }
  public static int Run(string binary, string arguments) {
    var info = new ProcessStartInfo(binary, arguments);
    info.UseShellExecute = false;
    info.CreateNoWindow = false; // Inherit the helper's hidden UTF-8 console.
    info.WindowStyle = ProcessWindowStyle.Hidden;
    info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
    using (var process = Process.Start(info)) {
      var input = Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream);
      input.ContinueWith(task => { try { process.StandardInput.Close(); } catch {} });
      var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
      var errors = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
      process.WaitForExit();
      // A background descendant may still hold the pipe. Exiting this helper
      // closes the job handle and terminates that descendant as well.
      Task.WaitAll(new Task[] { output, errors }, 1000);
      return process.ExitCode;
    }
  }
}
'@
  [MightyJob]::Initialize()
  [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
  $spec = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:MIGHTY_CLAUDE_LAUNCH_SPEC)) | ConvertFrom-Json
  Remove-Item Env:MIGHTY_CLAUDE_LAUNCH_SPEC
  exit ([MightyJob]::Run([string]$spec.binary, [string]$spec.arguments))
} catch {
  [Console]::Error.WriteLine('MightyClaude Windows launcher: ' + $_.Exception.Message)
  exit 125
}
`

/** Windows CRT quoting, used for a native exe rather than cmd's command text. */
export function quoteWindowsArgument(value: string): string {
  if (value && !/[\s"]/.test(value)) return value
  let result = '"'
  let slashes = 0
  for (const character of value) {
    if (character === '\\') { slashes++; continue }
    if (character === '"') result += '\\'.repeat(slashes * 2 + 1) + '"'
    else result += '\\'.repeat(slashes) + character
    slashes = 0
  }
  return result + '\\'.repeat(slashes * 2) + '"'
}

export function windowsShellArguments(command: string): string {
  // /s removes the outer quote pair; the user's own command quotes stay intact.
  // chcp affects cmd built-ins and console tools that honour the console code page.
  return `/d /s /c "chcp 65001>nul & ${command}"`
}

export function windowsLaunch(binary: string, args: string[], env: NodeJS.ProcessEnv, shellCommand?: string): { binary: string; args: string[]; env: NodeJS.ProcessEnv } {
  const argumentsText = shellCommand === undefined ? args.map(quoteWindowsArgument).join(' ') : windowsShellArguments(shellCommand)
  return {
    binary: join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'),
    args: ['-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(JOB_RUNNER, 'utf16le').toString('base64')],
    env: { ...env, MIGHTY_CLAUDE_LAUNCH_SPEC: Buffer.from(JSON.stringify({ binary, arguments: argumentsText })).toString('base64') },
  }
}
