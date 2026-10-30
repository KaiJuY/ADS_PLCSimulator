// sum <port>: measures ADS sum commands (0xF080-0xF082) and symbol version (0xF008).
// Writes only ServiceCtrl.Shutter.*.RequestId and restores it. Every handle is fresh.
using System.Buffers.Binary;
using System.Text;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;

using var c = new AdsClient();
c.Connect(AmsNetId.Local, int.Parse(args[0]));
const string MS = "GVL_Module.Chamber_Control[1].ModuleStatus";
const string SC = "GVL_Module.Chamber_Control[1].ServiceCtrl.Shutter.";
var ms = (IProcessImageAddress)c.ReadSymbol(MS);
uint IG = ms.IndexGroup;
uint openReq = ((IProcessImageAddress)c.ReadSymbol(SC + "Open.RequestId")).IndexOffset;
uint closeReq = ((IProcessImageAddress)c.ReadSymbol(SC + "Close.RequestId")).IndexOffset;
byte[] original = Rd(openReq, 40);

uint h = c.CreateVariableHandle(MS);
var (r1, k1, d1) = SumRead([(IG, ms.IndexOffset, 8), (IG, openReq, 4), (IG, 0x7FFFFFF0, 4), (0xF005, h, 8), (0xF005, h, 432 + 4)]);
Console.WriteLine($"F080a mixed 5 items          {r1} read={k1} codes={Codes(d1, 5)}");
Console.WriteLine($"F080a data after codes       {Hex(d1.AsSpan(20, Math.Max(0, Math.Min(k1 - 20, 24))))} (expected first 8 = {Hex(Rd(ms.IndexOffset, 8))})");
var (r2, k2, _) = SumRead([(IG, ms.IndexOffset, 8)], extra: 16);
Console.WriteLine($"F080b 1 item, buffer +16     {r2} read={k2}");
var (r3, k3, _) = SumRead([]);
Console.WriteLine($"F080c 0 items                {r3} read={k3}");
var (r4, k4, d4) = SumRead([(0xF005, 0x7FFF0000, 4)]);
Console.WriteLine($"F080d unknown handle         {r4} read={k4} codes={Codes(d4, 1)}");
{
    var req = new byte[12];
    BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(0), IG);
    BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), ms.IndexOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(8), 4);
    var r = c.TryReadWrite(0xF080, 2, new byte[64].AsMemory(), req.AsMemory(), out int k);
    Console.WriteLine($"F080e IO=2 but 1 item sent   {r} read={k}");
}
{
    var r = c.TryReadWrite(0xF080, 1, new byte[2].AsMemory(), new byte[12].AsMemory(), out int k);
    Console.WriteLine($"F080f read buffer too small  {r} read={k}");
}

uint o = U(Rd(openReq, 4)), cl = U(Rd(closeReq, 4));
uint hClose = c.CreateVariableHandle(SC + "Close.RequestId");
var (w1, wk1, wd1) = SumWrite([(IG, openReq, BitConverter.GetBytes(o + 3)), (IG, 0x7FFFFFF0, new byte[4]), (0xF005, hClose, BitConverter.GetBytes(cl + 4)), (0xF005, hClose, new byte[8])]);
Console.WriteLine($"F081a mixed 4 items          {w1} read={wk1} codes={Codes(wd1, 4)} -> Open={U(Rd(openReq, 4))} (o+3={o + 3}) Close={U(Rd(closeReq, 4))} (cl+4={cl + 4})");
c.Write(IG, openReq, BitConverter.GetBytes(o).AsMemory());
c.Write(IG, closeReq, BitConverter.GetBytes(cl).AsMemory());

var (x1, xk1, xd1) = SumReadWrite([(0xF003, 0, 4, Name(MS)), (0xF003, 0, 4, Name("GVL_Module.NoSuchSymbol")), (0xF003, 0, 4, Name(SC + "Open"))]);
Console.WriteLine($"F082a 3 handle creates       {x1} read={xk1} code/len={Pairs(xd1, 3)}");
Console.WriteLine($"F082a data after headers     {Hex(xd1.AsSpan(24, Math.Max(0, xk1 - 24)))}");
var handles = new List<uint>();
for (int i = 0, at = 24; i < 3; i++)
{
    int len = (int)U(xd1.AsSpan(i * 8 + 4));
    if (len == 4)
        handles.Add(U(xd1.AsSpan(at)));
    at += len;
}
Console.WriteLine($"F082a handles                {string.Join(",", handles)}");
if (handles.Count > 0)
{
    var rd = c.TryRead(0xF005, handles[0], new byte[8].AsMemory(), out int kk);
    Console.WriteLine($"F082a read via first handle  {rd} read={kk}");
}
var (x2, xk2, xd2) = SumReadWrite([(0xF003, 0, 8, Name(MS))]);
Console.WriteLine($"F082b readLen=8 for a handle {x2} read={xk2} code/len={Pairs(xd2, 1)} data={Hex(xd2.AsSpan(8, Math.Max(0, xk2 - 8)))}");
var (x3, xk3, xd3) = SumReadWrite([(IG, ms.IndexOffset, 8, Array.Empty<byte>())]);
Console.WriteLine($"F082c 0x4040 read via F082   {x3} read={xk3} code/len={Pairs(xd3, 1)}");
var rel = handles.Append(U(xd2.AsSpan(8))).Where(x => x != 0).Select(x => (0xF006u, 0u, BitConverter.GetBytes(x))).ToArray();
var (z1, zk1, zd1) = SumWrite(rel);
Console.WriteLine($"F081b release {rel.Length} handles      {z1} read={zk1} codes={Codes(zd1, rel.Length)}");

var sv = new byte[1];
var rv = c.TryRead(0xF008, 0, sv.AsMemory(), out int kv);
Console.WriteLine($"F008a read 1 byte            {rv} read={kv} value={sv[0]}");
var rv4 = c.TryRead(0xF008, 0, new byte[4].AsMemory(), out int kv4);
Console.WriteLine($"F008b read 4 bytes           {rv4} read={kv4}");
int hits = 0;
int sampleLength = -1;
void On(object? s, AdsNotificationEventArgs e) { Interlocked.Increment(ref hits); sampleLength = e.Data.Length; }
c.AdsNotification += On;
var rn = c.TryAddDeviceNotification(0xF008, 0, 1, new NotificationSettings(AdsTransMode.OnChange, 100, 0), null, out uint nh);
Thread.Sleep(1000);
Console.WriteLine($"F008c notify OnChange len=1  {rn} samples={hits} sampleLen={sampleLength}");
if (rn == AdsErrorCode.NoError)
    c.TryDeleteDeviceNotification(nh);
c.AdsNotification -= On;
c.TryDeleteVariableHandle(h);
c.TryDeleteVariableHandle(hClose);
Console.WriteLine($"--   restored                  {Rd(openReq, 40).SequenceEqual(original)}");

byte[] Rd(uint io, int n) { var b = new byte[n]; c.Read(IG, io, b.AsMemory()); return b; }
static uint U(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);
static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);
static string Codes(byte[] r, int n) => string.Join(",", Enumerable.Range(0, n).Select(i => ((AdsErrorCode)U(r.AsSpan(i * 4))).ToString()));
static string Pairs(byte[] r, int n) => string.Join(" ", Enumerable.Range(0, n).Select(i => $"{(AdsErrorCode)U(r.AsSpan(i * 8))}/{U(r.AsSpan(i * 8 + 4))}"));
static byte[] Name(string s) => Encoding.ASCII.GetBytes(s + "\0");

(AdsErrorCode, int, byte[]) SumRead((uint ig, uint io, int len)[] items, int extra = 0)
{
    var req = new byte[items.Length * 12];
    for (int i = 0; i < items.Length; i++)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(i * 12), items[i].ig);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(i * 12 + 4), items[i].io);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(i * 12 + 8), (uint)items[i].len);
    }
    var res = new byte[items.Length * 4 + items.Sum(x => x.len) + extra];
    var r = c.TryReadWrite(0xF080, (uint)items.Length, res.AsMemory(), req.AsMemory(), out int k);
    return (r, k, res);
}

(AdsErrorCode, int, byte[]) SumWrite((uint ig, uint io, byte[] data)[] items)
{
    var head = new byte[items.Length * 12];
    for (int i = 0; i < items.Length; i++)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 12), items[i].ig);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 12 + 4), items[i].io);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 12 + 8), (uint)items[i].data.Length);
    }
    var req = head.Concat(items.SelectMany(x => x.data)).ToArray();
    var res = new byte[items.Length * 4 + 8];
    var r = c.TryReadWrite(0xF081, (uint)items.Length, res.AsMemory(), req.AsMemory(), out int k);
    return (r, k, res);
}

(AdsErrorCode, int, byte[]) SumReadWrite((uint ig, uint io, int readLen, byte[] write)[] items)
{
    var head = new byte[items.Length * 16];
    for (int i = 0; i < items.Length; i++)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 16), items[i].ig);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 16 + 4), items[i].io);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 16 + 8), (uint)items[i].readLen);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 16 + 12), (uint)items[i].write.Length);
    }
    var req = head.Concat(items.SelectMany(x => x.write)).ToArray();
    var res = new byte[items.Length * 8 + items.Sum(x => x.readLen)];
    var r = c.TryReadWrite(0xF082, (uint)items.Length, res.AsMemory(), req.AsMemory(), out int k);
    return (r, k, res);
}
