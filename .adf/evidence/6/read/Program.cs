// read <port> [symbol]: prints ReadState, the symbol version (IG 0xF008) and, when given, a BOOL symbol value. Read-only.
using TwinCAT.Ads;

ushort port = ushort.Parse(args[0]);
if (port is 851 or 852)
    throw new ArgumentException("AMS 851/852 belong to a real PLC on this machine.");
using var client = new AdsClient { Timeout = 2000 };
client.Connect(AmsNetId.Local, port);
AdsErrorCode state = client.TryReadState(out StateInfo info);
var version = new byte[1];
AdsErrorCode versionError = client.TryRead(0xF008, 0, version.AsMemory(), out _);
Console.WriteLine($"ReadState={state}/{info.AdsState} F008={versionError}/{version[0]}");
if (args.Length > 1 && state == AdsErrorCode.NoError)
{
    uint handle = client.CreateVariableHandle(args[1]);
    Console.WriteLine($"{args[1]}={client.ReadAny<bool>(handle)}");
    client.DeleteVariableHandle(handle);
}
