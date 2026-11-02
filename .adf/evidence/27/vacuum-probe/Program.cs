// #27 stage I: Holder.VacuumEstablish and the 450 layout over ADS, simulator only (port 25860).
// Writes only ServiceCtrl.Holder.VacuumEstablish of module 1 (RequestId, PackMLIn).
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

int port = 25860;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
if (port != 25860)
{
    Console.WriteLine("refused: this probe runs against the simulator port 25860 only");
    return 2;
}

const string Root = "GVL_Module.Chamber_Control";
const string Module1 = Root + "[1]";
const short Start = 2;
const short Reset = 1;
const long Idle = 4, Execute = 6, Complete = 17;

using var c = new AdsClient();
c.Connect(AmsNetId.Local, port);
var loader = SymbolLoaderFactory.Create(c, SymbolLoaderSettings.Default);
ISymbol Sym(string path) => loader.Symbols.TryGetInstance(path, out ISymbol? s) ? s! : throw new InvalidOperationException($"no symbol {path}");
long RL(string path) => Convert.ToInt64(((IValueSymbol)Sym(path)).ReadValue());
void WV(string path, object v) => ((IValueSymbol)Sym(path)).WriteValue(v);
bool Wait(Func<bool> cond, int ms) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { if (cond()) return true; Thread.Sleep(20); } return cond(); }
int failures = 0;
void Check(string name, bool ok, string detail) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: {detail}"); if (!ok) failures++; }

// 1. Layout seen by an ADS client (symbol upload)
Check("Chamber_Control size", Sym(Root).ByteSize == 12964944, $"{Sym(Root).ByteSize}");
Check("module size", Sym(Module1).ByteSize == 2160824, $"{Sym(Module1).ByteSize}");
long baseOffset = ((IProcessImageAddress)Sym(Module1)).IndexOffset;
foreach ((string rel, long offset, int size) in new[]
{
    ("ServiceCtrl.Holder.VacuumEstablish", 129892L, 20),
    ("ServiceParam.Holder.VacuumEstablish", 221232L, 1),
    ("ServiceStatus.Holder.VacuumEstablish", 1868724L, 11684),
    ("ServiceStatus.Shutter.Open", 1973880L, 11684),
    ("ModuleConfigParamList", 58672L, 68400),
})
{
    ISymbol s = Sym($"{Module1}.{rel}");
    long actual = ((IProcessImageAddress)s).IndexOffset - baseOffset;
    Check($"{rel} offset/size", actual == offset && s.ByteSize == size, $"{actual}/{s.ByteSize}");
}
Check("ModuleConfigParamList elements", ((IArrayType)Sym($"{Module1}.ModuleConfigParamList").DataType!).Dimensions[0].ElementCount == 450, "450 expected");

// 2. Identity of every module
Wait(() => RL($"{Root}[6].ServiceStatus.Holder.VacuumEstablish.ServiceId") == 3007, 15000);
for (int m = 1; m <= 6; m++)
{
    string st = $"{Root}[{m}].ServiceStatus.Holder.VacuumEstablish";
    Check($"M{m} ServiceId", RL($"{st}.ServiceId") == 3007, $"{RL($"{st}.ServiceId")}");
    Check($"M{m} CompletionBehavior", RL($"{st}.CompletionBehavior") == 1, $"{RL($"{st}.CompletionBehavior")} (1 = NaturalCompletion)");
}
Check("Shutter.Open ServiceId unchanged", RL($"{Module1}.ServiceStatus.Shutter.Open.ServiceId") == 3301, $"{RL($"{Module1}.ServiceStatus.Shutter.Open.ServiceId")}");
Check("Holder.Rotate.MoveModulo ServiceId unchanged", RL($"{Module1}.ServiceStatus.Holder.Rotate.MoveModulo.ServiceId") == 3006, $"{RL($"{Module1}.ServiceStatus.Holder.Rotate.MoveModulo.ServiceId")}");

// 3. Start -> Execute -> Complete, then Reset back to Idle
string ctrl = $"{Module1}.ServiceCtrl.Holder.VacuumEstablish";
string state = $"{Module1}.ServiceStatus.Holder.VacuumEstablish.PackMLOut.eStateCurrent";
Check("starts Idle", Wait(() => RL(state) == Idle, 15000), $"state={RL(state)}");
void Command(uint requestId, short command)
{
    WV($"{ctrl}.PackMLIn.bCommandChangeRequest", false);
    Thread.Sleep(30);
    WV($"{ctrl}.RequestId", requestId);
    WV($"{ctrl}.PackMLIn.eCommand", command);
    WV($"{ctrl}.PackMLIn.bCommandChangeRequest", true);
}
Command(1, Start);
bool sawExecute = Wait(() => RL(state) == Execute, 3000);
DateTime t0 = DateTime.UtcNow;
bool completed = Wait(() => RL(state) == Complete, 15000);
Check("Start reaches Execute", sawExecute, $"state={RL(state)}");
Check("Execute reaches Complete", completed, $"state={RL(state)} after {(DateTime.UtcNow - t0).TotalMilliseconds:F0} ms");
Command(2, Reset);
Check("Reset returns to Idle", Wait(() => RL(state) == Idle, 15000), $"state={RL(state)}");
WV($"{ctrl}.PackMLIn.bCommandChangeRequest", false);

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
