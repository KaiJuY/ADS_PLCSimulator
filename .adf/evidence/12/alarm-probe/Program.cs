// #12 stage I measurement on AMS 853 (or the simulator with --port 25860 --root <module> --service <svc>).
// Writes only: <module>.ModuleCtrl.AlarmAckRequest and <module>.ServiceCtrl.<svc>.RequestId / PackMLIn.
// Every written field is restored in finally; an alarm created here is cleared, acknowledged and removed.
using System.Text;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

int port = 853;
string module = "GVL_Module.Chamber1_Control[1]";
string svc = "SetConfigParam";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--root") module = args[i + 1];
    if (args[i] == "--service") svc = args[i + 1];
}
bool simulator = port != 853;

using var c = new AdsClient();
c.Connect(AmsNetId.Local, port);
var loader = SymbolLoaderFactory.Create(c, new SymbolLoaderSettings(SymbolsLoadMode.VirtualTree));
const uint IG = 0xF005;

ISymbol Sym(string path) => loader.Symbols.TryGetInstance(path, out ISymbol? s) ? s! : throw new InvalidOperationException($"no symbol {path}");
uint H(string path) => c.CreateVariableHandle(path);
byte[] RB(string path) { var s = Sym(path); var b = new byte[s.ByteSize]; var h = H(path); c.Read(IG, h, b.AsMemory()); c.DeleteVariableHandle(h); return b; }
void WB(string path, byte[] b) { var h = H(path); c.Write(IG, h, b.AsMemory()); c.DeleteVariableHandle(h); }
object RV(string path) => ((IValueSymbol)Sym(path)).ReadValue();
long RL(string path) => Convert.ToInt64(RV(path));
void WV(string path, object v) => ((IValueSymbol)Sym(path)).WriteValue(v);
int Off(string structPath, string member)
{
    var st = (IStructType)Sym(structPath).DataType!;
    return st.AllMembers.First(m => m.InstanceName == member).Offset;
}
bool Wait(Func<bool> cond, int ms = 3000) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { if (cond()) return true; Thread.Sleep(10); } return cond(); }
void Log(string s) => Console.WriteLine(s);

string ctrl = $"{module}.ServiceCtrl.{svc}";
string status = $"{module}.ServiceStatus.{svc}";
string ack = $"{module}.ModuleCtrl.AlarmAckRequest";
string alarm = $"{module}.ModuleStatus.Alarm";
long State() => RL($"{status}.PackMLOut.eStateCurrent");

string DumpAlarm(string p) =>
    $"ModuleId={RV(p + ".ModuleId")} ServiceId={RV(p + ".ServiceId")} MainErrorId={RV(p + ".MainErrorId")} SourceErrorId={RV(p + ".SourceErrorId")} " +
    $"Message='{RV(p + ".Message")}' Path='{RV(p + ".Path")}' Severity={RV(p + ".Severity")} DateTime='{RV(p + ".DateTime")}' " +
    $"Active={RV(p + ".Active")} Acknowledged={RV(p + ".Acknowledged")} Count={RV(p + ".OccurrenceCount")}";
string DumpStatus() =>
    $"AckResponseId={RV(alarm + ".AckResponseId")} AckAccepted={RV(alarm + ".AckAccepted")} AckError={RV(alarm + ".AckError")} " +
    $"Svc={RV(alarm + ".ServiceLatchedCount")} Cfg={RV(alarm + ".ConfiguredLatchedCount")} Hook={RV(alarm + ".HookLatchedCount")} Overflow={RV(alarm + ".Overflow")} DuplicateId={RV(alarm + ".DuplicateId")}";
int ListLength() => ((IArrayType)Sym($"{module}.ModuleAlarmList").DataType!).Dimensions[0].ElementCount;
int FindAlarm(long serviceId, long mainErrorId)
{
    for (int i = 1; i <= ListLength(); i++)
    {
        string p = $"{module}.ModuleAlarmList[{i}]";
        if (RL(p + ".MainErrorId") == 0) return -1;
        if (RL(p + ".ServiceId") == serviceId && RL(p + ".MainErrorId") == mainErrorId) return i;
    }
    return -1;
}

// 1. precondition and saved state
Log($"== target port {port}, {module}, service {svc}");
Log($"ADS state={c.ReadState().AdsState}");
long serviceId = RL($"{status}.ServiceId");
Log($"{svc}: ServiceId={serviceId} State={State()} ErrorList[1].MainErrorId={RV(status + ".ErrorList[1].MainErrorId")}");
if (!simulator && (c.ReadState().AdsState != AdsState.Run || State() != 4))
{
    Log("STOP: 853 is not in Run or the service is not Idle.");
    return;
}
Log($"ModuleAlarmList[1]: {DumpAlarm(module + ".ModuleAlarmList[1]")}");
Log($"Alarm status: {DumpStatus()}");
byte[] ackOriginal = RB(ack);
byte[] ctrlRequestOriginal = RB($"{ctrl}.RequestId");
byte[] ctrlPackMLOriginal = RB($"{ctrl}.PackMLIn");
uint ackBase = (uint)RL(ack + ".RequestId") + 1000;
uint ctrlBase = (uint)RL(ctrl + ".RequestId") + 1000;
Log($"saved AlarmAckRequest={Convert.ToHexString(ackOriginal)} ctrl.RequestId={Convert.ToHexString(ctrlRequestOriginal)} PackMLIn={Convert.ToHexString(ctrlPackMLOriginal)}");

int oRequest = Off(ack, "RequestId"), oCommand = Off(ack, "Command"), oMain = Off(ack, "MainErrorId"), oService = Off(ack, "ServiceId"), oEdge = Off(ack, "bChangeRequest");
byte[] AckBytes(uint requestId, short command, uint main, uint service, bool edge)
{
    var b = new byte[ackOriginal.Length];
    BitConverter.GetBytes(requestId).CopyTo(b, oRequest);
    BitConverter.GetBytes(command).CopyTo(b, oCommand);
    BitConverter.GetBytes(main).CopyTo(b, oMain);
    BitConverter.GetBytes(service).CopyTo(b, oService);
    b[oEdge] = edge ? (byte)1 : (byte)0;
    return b;
}
string SendAck(string name, uint requestId, short command, uint main, uint service)
{
    WB(ack, AckBytes(requestId, command, main, service, true));
    Wait(() => RL(alarm + ".AckResponseId") == requestId && (Convert.ToBoolean(RV(alarm + ".AckAccepted")) || RL(alarm + ".AckError") != 0), 2000);
    Thread.Sleep(50);
    string result = $"{name}: RequestId={requestId} -> {DumpStatus()}";
    WB(ack, AckBytes(requestId, command, main, service, false));
    Wait(() => RL(alarm + ".AckError") == 0 && !Convert.ToBoolean(RV(alarm + ".AckAccepted")), 2000);
    Thread.Sleep(50);
    return result;
}
uint ctrlId = ctrlBase;
bool Command(short command, long expectedState)
{
    ctrlId++;
    WV($"{ctrl}.RequestId", ctrlId);
    WV($"{ctrl}.PackMLIn.eCommand", command);
    WV($"{ctrl}.PackMLIn.bCommandChangeRequest", true);
    bool reached = Wait(() => State() == expectedState, 5000);
    WV($"{ctrl}.PackMLIn.bCommandChangeRequest", false);
    Thread.Sleep(50);
    Log($"command {command} -> state {State()} (expected {expectedState}) ResponseId={RV(status + ".ResponseId")}");
    return reached;
}

long mainErrorId = 0;
try
{
    // 3. create the alarm: Start with the batch the module holds (853: Batch[1].Id is 0 = empty; simulator SetMode: TargetMode set by caller)
    if (!Command(2, 9)) throw new InvalidOperationException("service did not reach Aborted");
    Thread.Sleep(100);
    Log($"ErrorList[1]: {DumpAlarm(status + ".ErrorList[1]")}");
    mainErrorId = RL(status + ".ErrorList[1].MainErrorId");
    int index = FindAlarm(serviceId, mainErrorId);
    Log($"test alarm at ModuleAlarmList[{index}]: {(index > 0 ? DumpAlarm($"{module}.ModuleAlarmList[{index}]") : "missing")}");
    Log($"Alarm status: {DumpStatus()}");

    // 4. Ack cases
    Log(SendAck("RequestId 0", 0, 1, (uint)mainErrorId, (uint)serviceId));
    Log(SendAck("Command 3", ackBase + 1, 3, (uint)mainErrorId, (uint)serviceId));
    Log(SendAck("AcknowledgeOne unknown", ackBase + 2, 1, 999999, (uint)serviceId));
    Log(SendAck("AcknowledgeOne test alarm", ackBase + 3, 1, (uint)mainErrorId, (uint)serviceId));
    index = FindAlarm(serviceId, mainErrorId);
    Log($"after Ack: ModuleAlarmList[{index}]: {(index > 0 ? DumpAlarm($"{module}.ModuleAlarmList[{index}]") : "missing")}");
    Log(SendAck("same RequestId again", ackBase + 3, 1, (uint)mainErrorId, (uint)serviceId));

    // 5. Clear and Reset
    Command(9, 2);
    Thread.Sleep(100);
    index = FindAlarm(serviceId, mainErrorId);
    Log($"after Clear: ErrorList[1].MainErrorId={RV(status + ".ErrorList[1].MainErrorId")} test alarm index={index} status: {DumpStatus()}");
    Command(1, 4);
}
catch (Exception exception)
{
    Log($"ABORT PATH: {exception.Message}");
}
finally
{
    // abort path / step 6: return the service to Idle, remove the test alarm, restore the Ctrl fields
    if (State() == 9) Command(9, 2);
    if (State() == 2) Command(1, 4);
    if (mainErrorId != 0 && FindAlarm(serviceId, mainErrorId) > 0)
        Log(SendAck("cleanup AcknowledgeOne", ackBase + 10, 1, (uint)mainErrorId, (uint)serviceId));
    WB(ack, ackOriginal);
    WB($"{ctrl}.PackMLIn", ctrlPackMLOriginal);
    WB($"{ctrl}.RequestId", ctrlRequestOriginal);
    Thread.Sleep(100);
    Log($"final: state={State()} test alarm present={(mainErrorId != 0 && FindAlarm(serviceId, mainErrorId) > 0)} " +
        $"ack restored={RB(ack).SequenceEqual(ackOriginal)} ctrl restored={RB($"{ctrl}.RequestId").SequenceEqual(ctrlRequestOriginal) && RB($"{ctrl}.PackMLIn").SequenceEqual(ctrlPackMLOriginal)}");
    Log($"final ModuleAlarmList[1]: {DumpAlarm(module + ".ModuleAlarmList[1]")}");
}
