// #13 stage I: heartbeat measurement on AMS 853 (or the simulator: --port 25860 --root GVL_Module.Chamber_Control[1]).
// Writes only <root>.HeartbeatIndex. Restores the original value only if nobody else wrote in between.
using System.Diagnostics;
using TwinCAT.Ads;

int port = 853;
int maxSeconds = 15;
string root = "GVL_Module.Chamber1_Control[1]";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--root") root = args[i + 1];
    if (args[i] == "--max") maxSeconds = int.Parse(args[i + 1]);
}

using var c = new AdsClient();
c.Connect(AmsNetId.Local, port);
string hbPath = root + ".HeartbeatIndex";
string alivePath = root + ".ModuleStatus.IsControlSourceAlive";
uint hHb = c.CreateVariableHandle(hbPath);
uint hAlive = c.CreateVariableHandle(alivePath);
uint ReadHb() => (uint)c.ReadAny(hHb, typeof(uint));
bool ReadAlive() => (bool)c.ReadAny(hAlive, typeof(bool));
void WriteHb(uint v) => c.WriteAny(hHb, v);
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");

Log($"== port {port}, {root}, ADS state={c.ReadState().AdsState}");

// (0) other writer check: 3 s, every 500 ms
uint first = ReadHb();
for (int i = 0; i < 6; i++)
{
    Thread.Sleep(500);
    if (ReadHb() != first)
    {
        Log($"STOP: HeartbeatIndex changes without us ({first} -> {ReadHb()}); another client writes it.");
        return;
    }
}

// (1) original value
uint original = ReadHb();
Log($"ORIGINAL HeartbeatIndex={original} (restore manually with this value if the program is interrupted); IsControlSourceAlive={ReadAlive()}");

uint last = original;
try
{
    // (2) increment every 200 ms for 2 s
    for (int i = 0; i < 10; i++)
    {
        last = unchecked(last + 1);
        WriteHb(last);
        Thread.Sleep(200);
    }
    Log($"after 2 s of increments: HeartbeatIndex={ReadHb()} IsControlSourceAlive={ReadAlive()}");

    // (3) stop; poll every 200 ms until FALSE (max 15 s)
    var watch = Stopwatch.StartNew();
    bool foreign = false;
    double? fellAt = null;
    while (watch.Elapsed < TimeSpan.FromSeconds(maxSeconds))
    {
        if (ReadHb() != last) { foreign = true; break; }
        if (!ReadAlive()) { fellAt = watch.Elapsed.TotalSeconds; break; }
        Thread.Sleep(200);
    }
    if (foreign)
        Log("INVALID: another writer changed HeartbeatIndex during the idle phase.");
    else
        Log(fellAt is { } t ? $"IsControlSourceAlive became FALSE {t:F2} s after the last write" : $"IsControlSourceAlive stayed TRUE for {maxSeconds} s");
}
finally
{
    // (4) restore only if the value is still ours
    if (ReadHb() == last)
    {
        WriteHb(original);
        Log($"restored HeartbeatIndex={ReadHb()} (alive right after restore={ReadAlive()})");
    }
    else
        Log($"NOT restored: HeartbeatIndex={ReadHb()} is not our last value {last}");
    c.DeleteVariableHandle(hHb);
    c.DeleteVariableHandle(hAlive);
}
