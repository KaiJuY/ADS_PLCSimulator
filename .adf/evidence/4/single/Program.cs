// Read-only: single-request counterparts of the sum cases.
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using var c = new AdsClient();
c.Connect(AmsNetId.Local, int.Parse(args[0]));
var ms = (IProcessImageAddress)c.ReadSymbol("GVL_Module.Chamber_Control[1].ModuleStatus");
var r1 = c.TryRead(0xF005, 0x7FFF0000, new byte[4].AsMemory(), out _);
Console.WriteLine($"S1 read never-issued handle      {r1}");
uint h = c.CreateVariableHandle("GVL_Module.Chamber_Control[1].ModuleStatus");
c.TryWrite(0xF006, 0, BitConverter.GetBytes(h).AsMemory());
var r2 = c.TryRead(0xF005, h, new byte[4].AsMemory(), out _);
Console.WriteLine($"S2 read released handle          {r2}");
var r3 = c.TryReadWrite(ms.IndexGroup, ms.IndexOffset, new byte[8].AsMemory(), ReadOnlyMemory<byte>.Empty, out int k3);
Console.WriteLine($"S3 readwrite 0x4040 len 8        {r3} read={k3}");
var r4 = c.TryReadWrite(0xF003, 0, new byte[8].AsMemory(), System.Text.Encoding.ASCII.GetBytes("GVL_Module.Chamber_Control[1].ModuleStatus\0").AsMemory(), out int k4);
Console.WriteLine($"S4 create handle readLen 8       {r4} read={k4}");
var r5 = c.TryWrite(0xF005, 0x7FFF0000, new byte[4].AsMemory());
Console.WriteLine($"S5 write never-issued handle     {r5}");
