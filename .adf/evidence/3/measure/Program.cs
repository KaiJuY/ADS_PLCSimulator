// measure <port>: the issue #3 access matrix. On a real PLC it writes only ServiceCtrl RequestId fields and restores them.
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using var c = new AdsClient();
c.Connect(AmsNetId.Local, int.Parse(args[0]));
const string MS = "GVL_Module.Chamber_Control[1].ModuleStatus";
const string SC = "GVL_Module.Chamber_Control[1].ServiceCtrl.Shutter.";
IProcessImageAddress Addr(string p) => (IProcessImageAddress)c.ReadSymbol(p);
int msSize = c.ReadSymbol(MS).ByteSize;
var ms = Addr(MS);
uint IG = ms.IndexGroup;
uint openReq = Addr(SC + "Open.RequestId").IndexOffset, aliveTo = Addr(SC + "Open.AliveTimeout").IndexOffset, closeReq = Addr(SC + "Close.RequestId").IndexOffset;
byte[] Rd(uint io, int n) { var b = new byte[n]; c.Read(IG, io, b.AsMemory()); return b; }
uint U(byte[] b) => BitConverter.ToUInt32(b, 0);
byte[] B(uint x) => BitConverter.GetBytes(x);
byte[] original = Rd(openReq, 40);
uint o = U(Rd(openReq, 4)), a = U(Rd(aliveTo, 4)), cl = U(Rd(closeReq, 4));
void Restore() { c.Write(IG, openReq, B(o).AsMemory()); c.Write(IG, closeReq, B(cl).AsMemory()); }
void Line(string id, string what, object result) => Console.WriteLine($"{id,-4} {what,-38} {result}");
string Rd2(uint ig, uint io, int n) { var r = c.TryRead(ig, io, new byte[n].AsMemory(), out int k); return $"{r} read={k}"; }

uint h = c.CreateVariableHandle(MS);
uint hSub = c.CreateVariableHandle(MS + ".PackMLOut");
int subSize = c.ReadSymbol(MS + ".PackMLOut").ByteSize;
Line("R1", "handle read len=8", Rd2(0xF005, h, 8));
Line("R2", "handle read len=size+4", Rd2(0xF005, h, msSize + 4));
Line("R2b", "handle sub read len=size+4", Rd2(0xF005, hSub, subSize + 4) + $" (size {subSize})");
Line("R3", "handle read len=0", Rd2(0xF005, h, 0));
Line("R4a", "igio read len=size+4", Rd2(IG, ms.IndexOffset, msSize + 4));
Line("R4b", "igio read mid(+2) len=4", Rd2(IG, ms.IndexOffset + 2, 4));
Line("R4c", "igio read far offset", Rd2(IG, 0x7FFFFFF0, 4));

uint hReq = c.CreateVariableHandle(SC + "Open.RequestId");
uint hTo = c.CreateVariableHandle(SC + "Open.AliveTimeout");
var w = c.TryWrite(0xF005, hReq, B(o ^ 0xFFFF).AsMemory(0, 2));
Line("W5a", "handle write len=2", $"{w} -> {U(Rd(openReq, 4))} (want {o ^ 0xFFFF})"); Restore();
w = c.TryWrite(0xF005, hReq, ReadOnlyMemory<byte>.Empty);
Line("W5b", "handle write len=0", w);
w = c.TryWrite(0xF005, hTo, B(a).Concat(B(cl + 1)).ToArray().AsMemory());
Line("W6", "handle write len=size+4", $"{w} -> Close.RequestId={U(Rd(closeReq, 4))} (orig {cl})"); Restore();
w = c.TryWrite(IG, aliveTo, B(a).Concat(B(cl + 1)).ToArray().AsMemory());
Line("W7a", "igio write across symbols", $"{w} -> Close.RequestId={U(Rd(closeReq, 4))} (orig {cl})"); Restore();
w = c.TryWrite(IG, 0x7FFFFFF0, B(0).AsMemory());
Line("W7b", "igio write far offset", w);

string Notify(uint ig, uint io, int n, bool deliver)
{
    int hits = 0;
    void On(object? s, AdsNotificationEventArgs e) => Interlocked.Increment(ref hits);
    c.AdsNotification += On;
    var r = c.TryAddDeviceNotification(ig, io, n, new NotificationSettings(AdsTransMode.OnChange, 10, 0), null, out uint nh);
    if (r == AdsErrorCode.NoError && deliver)
    {
        Thread.Sleep(300);
        int before = hits;
        c.Write(IG, openReq, B(o + 7).AsMemory());
        Thread.Sleep(800);
        Restore();
        c.TryDeleteDeviceNotification(nh);
        c.AdsNotification -= On;
        return $"{r} delivered-after-change={hits - before > 0}";
    }
    if (r == AdsErrorCode.NoError) c.TryDeleteDeviceNotification(nh);
    c.AdsNotification -= On;
    return r.ToString();
}
// Each handle notification gets a fresh handle: deleting a handle notification makes AdsClient release the handle too.
uint hScOpen = c.CreateVariableHandle(SC + "Open");
Line("N8a", "notify handle len=8 (<size 20)", Notify(0xF005, hScOpen, 8, true));
Line("N8c", "notify with the handle released above", Notify(0xF005, hScOpen, 8, false));
Line("N8b", "notify handle len=size+4", Notify(0xF005, c.CreateVariableHandle(SC + "Open"), 24, false));
Line("N9", "notify igio across symbols", Notify(IG, aliveTo - 4, 16, false));
Line("N9b", "notify igio len=4 delivers", Notify(IG, openReq, 4, true));

Line("N0a", "notify handle len=0", Notify(0xF005, c.CreateVariableHandle(SC + "Open"), 0, false));
Line("N0b", "notify igio len=0", Notify(IG, openReq, 0, false));
{
    int hits = 0;
    void On(object? s, AdsNotificationEventArgs e) => Interlocked.Increment(ref hits);
    c.AdsNotification += On;
    var r = c.TryAddDeviceNotification(IG, openReq, 4, new NotificationSettings(AdsTransMode.Cyclic, 100, 0), null, out uint nh);
    Thread.Sleep(1000);
    int count = hits;
    if (r == AdsErrorCode.NoError) c.TryDeleteDeviceNotification(nh);
    c.AdsNotification -= On;
    Line("N9c", "notify igio Cyclic 100 ms, 1 s unchanged", $"{r} samples={count}");
}

uint h2 = c.CreateVariableHandle(MS);
Line("H10a", "release IO=handle data=handle", c.TryWrite(0xF006, h2, B(h2).AsMemory()));
Line("H10c", "release same handle again IO=0", c.TryWrite(0xF006, 0, B(h2).AsMemory()));
Line("H10b", "release unknown handle", c.TryWrite(0xF006, 0, B(0x7FFF0000).AsMemory()));
foreach (var x in new[] { h, hSub, hReq, hTo, hScOpen }) c.TryDeleteVariableHandle(x);
Line("--", "restored", Rd(openReq, 40).SequenceEqual(original));
