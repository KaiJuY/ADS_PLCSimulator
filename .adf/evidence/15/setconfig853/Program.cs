// #15 stage I: SetConfig measurement on AMS 853 (real PLC, SetConfigParam) or the simulator (25860, Chamber.SetConfig).
// Writes only: <ctrl>.PackMLIn.*, the <param> batch elements, <root>.ModuleCtrl.AlarmAckRequest.
// Usage: --port 853|25860 [--root <path>] [--service <name>] [--restore] [--out <dir>]
using System.Globalization;
using System.Text.Json;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

int port = 853;
string? root = null;
string? service = null;
bool restoreOnly = false;
string outDir = AppContext.BaseDirectory;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length: port = int.TryParse(args[++i], out int p) ? p : -1; break;
        case "--root" when i + 1 < args.Length: root = args[++i]; break;
        case "--service" when i + 1 < args.Length: service = args[++i]; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--restore": restoreOnly = true; break;
        default: Console.WriteLine($"Invalid argument: {args[i]}"); return 1;
    }
}

if (port != 853 && port != 25860)
{
    Console.WriteLine($"Refused: port {port}. Only 853 (real PLC demo) and 25860 (simulator) are allowed; 851, 852 and 854 are never touched.");
    return 1;
}

root ??= port == 853 ? "GVL_Module.Chamber1_Control[1]" : "GVL_Module.Chamber_Control[1]";
service ??= port == 853 ? "SetConfigParam" : "Chamber.SetConfig";
string ctrl = $"{root}.ServiceCtrl.{service}";
string param = $"{root}.ServiceParam.{service}";
string status = $"{root}.ServiceStatus.{service}";
string ackPath = $"{root}.ModuleCtrl.AlarmAckRequest";
string originalFile = Path.Combine(outDir, $"{port}-original.json");

const short Stopped = 2, Idle = 4, Aborted = 9, Complete = 17;
const short CmdReset = 1, CmdStart = 2, CmdClear = 9;
const short DescDint = 1, DescReal = 2;
const short ConfigInt32 = 2, ConfigFloat32 = 4;

using var client = new AdsClient { Timeout = 3000 };
client.Connect(AmsNetId.Local, port);
var handles = new Dictionary<string, uint>();
var loader = SymbolLoaderFactory.Create(client, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree));
int SizeOf(string path) => loader.Symbols.TryGetInstance(path, out ISymbol? sym) ? sym!.ByteSize : throw new InvalidOperationException($"no symbol {path}");
byte[] RawRead(string path) { var b = new byte[SizeOf(path)]; client.Read(0xF005, Handle(path), b.AsMemory()); return b; }
// Raw bytes of every area this program writes, restored at the end so the PLC returns to its starting state.
string[] rawAreas = [];
var rawOriginal = new Dictionary<string, byte[]>();
void SaveRaw()
{
    rawAreas = [$"{ctrl}.RequestId", $"{ctrl}.PackMLIn", ackPath, $"{param}[1]", $"{param}[2]"];
    foreach (string a in rawAreas) rawOriginal[a] = RawRead(a);
    Log("raw saved: " + string.Join(", ", rawAreas.Select(a => $"{a}={Convert.ToHexString(rawOriginal[a]).Substring(0, Math.Min(24, rawOriginal[a].Length * 2))}..")));
}
bool RestoreRaw()
{
    bool ok = true;
    foreach (string a in new[] { $"{ctrl}.PackMLIn", ackPath, $"{param}[1]", $"{param}[2]", $"{ctrl}.RequestId" })
    {
        if (!rawOriginal.TryGetValue(a, out byte[]? b)) continue;
        Log($"WRITE raw {a} (restore)");
        client.Write(0xF005, Handle(a), b.AsMemory());
        bool same = RawRead(a).SequenceEqual(b);
        ok &= same;
        Log($"raw restored {a}: {same}");
    }
    return ok;
}
uint requestId = (uint)Environment.TickCount;
var rows = new List<string[]>();

void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");

uint Handle(string path)
{
    if (!handles.TryGetValue(path, out uint h))
        handles[path] = h = client.CreateVariableHandle(path);
    return h;
}

T Read<T>(string path) => (T)client.ReadAny(Handle(path), typeof(T));
string ReadStr(string path) => (string)client.ReadAny(Handle(path), typeof(string), [80]);

bool WriteAllowed(string path) =>
    path.StartsWith(ctrl + ".PackMLIn.") || path.StartsWith(ctrl + ".RequestId") || path.StartsWith(param + "[") || path.StartsWith(ackPath + ".");

void Write(string path, object value)
{
    if (!WriteAllowed(path))
        throw new InvalidOperationException($"Write outside the allowed set refused: {path}");
    Log($"WRITE {path} = {value}");
    if (value is string text)
        client.WriteAny(Handle(path), text, [80]);
    else
        client.WriteAny(Handle(path), value);
}

short StateOf() => Read<short>($"{status}.PackMLOut.eStateCurrent");

string StateName(short s) => s switch
{
    1 => "Clearing", 2 => "Stopped", 3 => "Starting", 4 => "Idle", 5 => "Suspended", 6 => "Execute",
    7 => "Stopping", 8 => "Aborting", 9 => "Aborted", 10 => "Holding", 11 => "Held", 12 => "Unholding",
    13 => "Suspending", 14 => "Unsuspending", 15 => "Resetting", 16 => "Completing", 17 => "Complete",
    _ => $"?{s}",
};

bool WaitState(short target, double seconds)
{
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end)
    {
        if (StateOf() == target) return true;
        Thread.Sleep(20);
    }
    return StateOf() == target;
}

void Send(short command)
{
    Write($"{ctrl}.PackMLIn.bCommandChangeRequest", false);
    Thread.Sleep(100); // let the PLC scan see FALSE so the next TRUE is a rising edge
    Write($"{ctrl}.RequestId", ++requestId);
    Write($"{ctrl}.PackMLIn.eCommand", command);
    Write($"{ctrl}.PackMLIn.bCommandChangeRequest", true);
}

void Release() => Write($"{ctrl}.PackMLIn.bCommandChangeRequest", false);

bool Command(short command, short expected, double seconds)
{
    Send(command);
    bool ok = WaitState(expected, seconds);
    Release();
    return ok;
}

bool ResetToIdle() => StateOf() == Idle || Command(CmdReset, Idle, 5);

void WriteBatch(uint id, short dataType, string value)
{
    Write($"{param}[1].Id", id);
    Write($"{param}[1].DataType", dataType);
    Write($"{param}[1].Value", value);
    Write($"{param}[2].Id", 0u);
}

void ZeroBatch() => Write($"{param}[1].Id", 0u);

void AcknowledgeAll()
{
    Write($"{ackPath}.bChangeRequest", false);
    Thread.Sleep(100);
    Write($"{ackPath}.RequestId", ++requestId);
    Write($"{ackPath}.Command", (short)2);
    Write($"{ackPath}.MainErrorId", 0u);
    Write($"{ackPath}.ServiceId", 0u);
    Write($"{ackPath}.bChangeRequest", true);
    Thread.Sleep(500);
    Write($"{ackPath}.bChangeRequest", false);
}

void PrintErrors()
{
    Log($"ErrorList[1] Main={Read<uint>($"{status}.ErrorList[1].MainErrorId")} Source={Read<uint>($"{status}.ErrorList[1].SourceErrorId")} Message=\"{ReadStr($"{status}.ErrorList[1].Message")}\"");
    for (int i = 1; i <= 10; i++)
    {
        try
        {
            string entry = $"{root}.ModuleAlarmList[{i}]";
            uint main = Read<uint>($"{entry}.MainErrorId");
            if (main == 0) continue;
            Log($"ModuleAlarmList[{i}] Main={main} Source={Read<uint>($"{entry}.SourceErrorId")} ServiceId={Read<uint>($"{entry}.ServiceId")} Active={Read<bool>($"{entry}.Active")} Ack={Read<bool>($"{entry}.Acknowledged")}");
        }
        catch (Exception e)
        {
            Log($"ModuleAlarmList[{i}] unreadable: {e.Message}");
            break;
        }
    }
}

string Entry(int index) => $"{root}.ModuleConfigParamList[{index}]";

int FindIndex(uint id)
{
    for (int i = 1; i <= 2000; i++)
    {
        try
        {
            if (Read<uint>($"{Entry(i)}.Id") == id) return i;
        }
        catch
        {
            return 0;
        }
    }
    return 0;
}

short ConfigType(short descriptorType) => descriptorType == DescDint ? ConfigInt32 : ConfigFloat32;

bool RestoreAll(Original[] originals)
{
    bool all = true;
    try
    {
        if (!ResetToIdle())
        {
            Log($"restore: service not Idle ({StateName(StateOf())})");
            return false;
        }

        foreach (Original o in originals)
        {
            WriteBatch(o.Id, ConfigType(o.DescriptorType), o.Value);
            Send(CmdStart);
            bool done = WaitState(Complete, 5);
            Release();
            int index = FindIndex(o.Id);
            string now = index > 0 ? ReadStr($"{Entry(index)}.Value") : "<not found>";
            bool same = done && now == o.Value;
            all &= same;
            Log($"RESTORE Id={o.Id} complete={done} readback=\"{now}\" original=\"{o.Value}\" -> {(same ? "OK" : "MISMATCH")}");
            rows.Add([$"restore {o.Id}", "", "", StateName(StateOf()), now]);
            ResetToIdle();
        }
    }
    finally
    {
        ZeroBatch();
    }
    return all;
}

void SuccessRounds(Original p)
{
    bool real = p.DescriptorType == DescReal;
    var values = new List<string>();
    if (real)
        values.AddRange(["1.5", "0.1", "+2", "1e2"]);
    else
        values.Add((long.Parse(p.Value, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture));

    foreach (string v in values)
    {
        double number = double.Parse(v, CultureInfo.InvariantCulture);
        if (number < p.LowerLimit || number > p.UpperLimit)
        {
            Log($"SKIP Id={p.Id} value {v}: outside [{p.LowerLimit}, {p.UpperLimit}]");
            rows.Add([$"success {p.Id} {v}", "skipped", "", "", ""]);
            continue;
        }

        Log($"== success Id={p.Id} value {v}");
        WriteBatch(p.Id, ConfigType(p.DescriptorType), v);
        Send(CmdStart);
        bool done = WaitState(Complete, 5);
        Release();
        string now = ReadStr($"{Entry(FindIndex(p.Id))}.Value");
        uint main = Read<uint>($"{status}.ErrorList[1].MainErrorId");
        uint source = Read<uint>($"{status}.ErrorList[1].SourceErrorId");
        Log($"state={StateName(StateOf())} readback=\"{now}\" ErrorList[1].Main={main}");
        rows.Add([$"success {p.Id} {v}", main.ToString(), source.ToString(), StateName(StateOf()), now]);
        if (!done) throw new TimeoutException("Complete not reached within 5 s");
        if (!ResetToIdle()) throw new InvalidOperationException("Reset did not reach Idle");
    }
}

void FailureRounds(Original dint)
{
    bool isReal = dint.DescriptorType == DescReal;
    string badValue = isReal ? (dint.UpperLimit + 1).ToString(CultureInfo.InvariantCulture)
        : dint.UpperLimit >= int.MaxValue ? "3000000000" : (dint.UpperLimit + 1).ToString("F0", CultureInfo.InvariantCulture);
    int index = FindIndex(dint.Id);
    string before = ReadStr($"{Entry(index)}.Value");
    var rounds = new (string Name, uint Id, string Value)[]
    {
        ("empty batch (920)", 0, ""),
        ("unknown Id (922)", 4294967000, "1"),
        ($"above limit (926) {badValue}", dint.Id, badValue),
    };

    foreach ((string name, uint id, string value) in rounds)
    {
        Log($"== failure {name}");
        if (id == 0) ZeroBatch(); else WriteBatch(id, ConfigType(dint.DescriptorType), value);
        Send(CmdStart);
        bool aborted = WaitState(Aborted, 5);
        Release();
        Log($"state={StateName(StateOf())}");
        PrintErrors();
        uint main = Read<uint>($"{status}.ErrorList[1].MainErrorId");
        uint source = Read<uint>($"{status}.ErrorList[1].SourceErrorId");
        if (!aborted) throw new TimeoutException("Aborted not reached within 5 s");

        AcknowledgeAll();
        if (!Command(CmdClear, Stopped, 5)) Log("Clear did not reach Stopped");
        bool idle = ResetToIdle();
        string now = ReadStr($"{Entry(index)}.Value");
        Log($"state={StateName(StateOf())} Idle={idle} Value=\"{now}\" unchanged={now == before}");
        rows.Add([name, main.ToString(), source.ToString(), StateName(StateOf()), now]);
        if (!idle) throw new InvalidOperationException("Reset did not reach Idle");
        if (now != before) throw new InvalidOperationException("ECID value changed by a rejected batch");
    }
}

void PrintTable()
{
    Console.WriteLine();
    Console.WriteLine($"{"round",-34}{"Main",-9}{"Source",-12}{"final state",-13}Value");
    foreach (string[] r in rows)
        Console.WriteLine($"{r[0],-34}{r[1],-9}{r[2],-12}{r[3],-13}{r[4]}");
}

int Measure()
{
    var picks = new List<Original>();
    int count = 0;
    for (int i = 1; i <= 2000; i++)
    {
        uint id;
        try { id = Read<uint>($"{Entry(i)}.Id"); } catch { break; }
        if (id == 0) continue;
        count++;
        short dt = Read<short>($"{Entry(i)}.DataType");
        if (!Read<bool>($"{Entry(i)}.IsBound") || (dt != DescDint && dt != DescReal) || picks.Any(p => p.DescriptorType == dt)) continue;
        picks.Add(new Original(id, dt, ReadStr($"{Entry(i)}.Value"), Read<double>($"{Entry(i)}.LowerLimit"), Read<double>($"{Entry(i)}.UpperLimit"), Read<bool>($"{Entry(i)}.HasLimits")));
    }

    Log($"{count} ECID with Id != 0");
    foreach (Original p in picks)
        Log($"PICK Id={p.Id} DescriptorType={p.DescriptorType} ({(p.DescriptorType == DescDint ? "DINT" : "REAL")}) Value=\"{p.Value}\" Lower={p.LowerLimit} Upper={p.UpperLimit} HasLimits={p.HasLimits}");
    Original? dint = picks.FirstOrDefault(p => p.DescriptorType == DescDint) ?? picks.FirstOrDefault();
    if (dint is null)
    {
        Log("No bound DINT or REAL ECID. Stop.");
        return 1;
    }
    if (dint.DescriptorType != DescDint) Log("No bound DINT ECID: DINT not measured; failure rounds use the REAL ECID.");
    if (picks.All(p => p.DescriptorType != DescReal)) Log("No bound REAL ECID: REAL not measured.");

    File.WriteAllText(originalFile, JsonSerializer.Serialize(picks, new JsonSerializerOptions { WriteIndented = true }));
    if (!File.Exists(originalFile))
    {
        Log("Original file not written. Stop.");
        return 1;
    }
    Log($"originals written to {originalFile}");

    SaveRaw();
    short state = StateOf();
    if (state != Idle)
    {
        Log($"Service is {StateName(state)}, not Idle. Stop.");
        return 1;
    }

    bool restored;
    try
    {
        foreach (Original p in picks)
            SuccessRounds(p);
        FailureRounds(dint);
    }
    catch (Exception e)
    {
        Log($"ERROR {e.GetType().Name}: {e.Message}");
        throw;
    }
    finally
    {
        Log("== restore");
        restored = RestoreAll(picks.ToArray());
        restored &= RestoreRaw();
        Log($"restore result: {(restored ? "OK" : "FAILED (run --restore)")}");
        PrintTable();
    }
    return restored ? 0 : 3;
}

int Restore()
{
    if (!File.Exists(originalFile))
    {
        Log($"No {originalFile}");
        return 1;
    }
    Original[] originals = JsonSerializer.Deserialize<Original[]>(File.ReadAllText(originalFile))!;
    SaveRaw(); // the control areas are back at their start values after the raw restore of the failed run
    bool ok = RestoreAll(originals);
    ok &= RestoreRaw();
    PrintTable();
    return ok ? 0 : 3;
}

try
{
    Log($"== port {port}, root {root}, service {service}, ADS state={client.ReadState().AdsState}");
    return restoreOnly ? Restore() : Measure();
}
catch (Exception e)
{
    Log($"ERROR {e.GetType().Name}: {e.Message}");
    return 2;
}

record Original(uint Id, short DescriptorType, string Value, double LowerLimit, double UpperLimit, bool HasLimits);
