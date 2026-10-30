// pty <script> <transcript> <exe> [args...]
// Runs <exe> in a pseudo console and follows <script>, one command per line:
//   expect <regex>   wait (max 60 s) until output written since the last expect matches
//   send <text>      type text, then Enter
//   sleep <ms>
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

string[] script = File.ReadAllLines(args[0]);
string transcriptPath = args[1];
string commandLine = string.Join(" ", args.Skip(2).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0);
CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0);
int hr = CreatePseudoConsole(new COORD { X = 200, Y = 50 }, inRead, outWrite, 0, out IntPtr pty);
if (hr != 0) throw new Exception($"CreatePseudoConsole {hr}");

var si = new STARTUPINFOEX();
si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
si.StartupInfo.dwFlags = 0x100; // STARTF_USESTDHANDLES with null handles: the child must not inherit our redirected pipes
IntPtr size = IntPtr.Zero;
InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
si.lpAttributeList = Marshal.AllocHGlobal(size);
InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size);
UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)0x20016, pty, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);
if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x80000, IntPtr.Zero, Path.GetDirectoryName(args[2]), ref si, out PROCESS_INFORMATION pi))
    throw new Exception($"CreateProcess {Marshal.GetLastWin32Error()}");

var output = new StringBuilder();
var ansi = new Regex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07]*\x07|\x1b[=>]");
var reader = new Thread(() =>
{
    using var stream = new FileStream(outRead, FileAccess.Read);
    var buffer = new byte[8192];
    var decoder = Encoding.UTF8.GetDecoder();
    int n;
    while ((n = stream.Read(buffer)) > 0)
    {
        var chars = new char[decoder.GetCharCount(buffer, 0, n)];
        decoder.GetChars(buffer, 0, n, chars, 0);
        lock (output) output.Append(ansi.Replace(new string(chars), ""));
    }
}) { IsBackground = true };
reader.Start();
using var input = new FileStream(inWrite, FileAccess.Write);

int mark = 0;
int exit = 0;
foreach (string line in script.Where(l => l.Length > 0 && !l.StartsWith('#')))
{
    string verb = line.Split(' ', 2)[0];
    string arg = line.Length > verb.Length ? line[(verb.Length + 1)..] : "";
    if (verb == "send")
    {
        input.Write(Encoding.UTF8.GetBytes(arg + "\r"));
        input.Flush();
    }
    else if (verb == "sleep")
        Thread.Sleep(int.Parse(arg));
    else if (verb == "expect")
    {
        var pattern = new Regex(arg);
        var watch = Stopwatch.StartNew();
        Match? match = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            lock (output) match = pattern.Match(output.ToString(), mark);
            if (match.Success) break;
            Thread.Sleep(100);
        }
        if (match is null || !match.Success)
        {
            lock (output) output.Append($"\n#### TIMEOUT waiting for: {arg}\n");
            exit = 2;
            break;
        }
        mark = match.Index + match.Length;
    }
}

Thread.Sleep(500);
ClosePseudoConsole(pty);
WaitForSingleObject(pi.hProcess, 5000);
try { Process.GetProcessById(pi.dwProcessId).Kill(); } catch { }
lock (output) File.WriteAllText(transcriptPath, output.ToString());
return exit;

[DllImport("kernel32", SetLastError = true)] static extern bool CreatePipe(out SafeFileHandle r, out SafeFileHandle w, IntPtr sa, int size);
[DllImport("kernel32")] static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pty);
[DllImport("kernel32")] static extern void ClosePseudoConsole(IntPtr pty);
[DllImport("kernel32", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
[DllImport("kernel32", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
[DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcess(string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
[DllImport("kernel32")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

[StructLayout(LayoutKind.Sequential)] struct COORD { public short X, Y; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct STARTUPINFO
{
    public int cb; public string? lpReserved, lpDesktop, lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
}
[StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
[StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
