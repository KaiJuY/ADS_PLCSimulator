// probe6: ADS server lifecycle on AMS 25860 only (the simulator port).
using TwinCAT.Ads;
using TwinCAT.Ads.Server;
using TwinCAT.TypeSystem;
const ushort Port = 25860;

static string State()
{
    using var c = new AdsClient { Timeout = 1000 };
    c.Connect(AmsNetId.Local, Port);
    var r = c.TryReadState(out StateInfo s);
    var v = new byte[1];
    var rv = c.TryRead(0xF008, 0, v.AsMemory(), out _);
    return $"ReadState={r}/{s.AdsState} F008={rv}/{v[0]}";
}

var a = new Probe(7);
var p = await a.ConnectServerAsync(CancellationToken.None);
Console.WriteLine($"A connect -> {p} IsConnected={a.IsConnected} {State()}");
var b = new Probe(9);
try { var pb = await b.ConnectServerAsync(CancellationToken.None); Console.WriteLine($"B same port -> {pb} IsConnected={b.IsConnected}"); }
catch (Exception e) { Console.WriteLine($"B same port threw {e.GetType().Name}: {e.Message}"); }
b.Dispose();
Console.WriteLine($"after B dispose: {State()}");
Console.WriteLine($"A disconnect -> {a.Disconnect()}");
a.Dispose();
Console.WriteLine($"after A dispose: {State()}");
var c2 = new Probe(8);
var p2 = await c2.ConnectServerAsync(CancellationToken.None);
Console.WriteLine($"C reconnect -> {p2} IsConnected={c2.IsConnected} {State()}");
c2.Disconnect(); c2.Dispose();
Console.WriteLine($"end: {State()}");
// Token lifetime: cancel and dispose the token after a successful connect.
var d = new Probe(5);
using (var cts = new CancellationTokenSource())
{
    var pd = await d.ConnectServerAsync(cts.Token);
    cts.Cancel();
    Console.WriteLine($"D connect -> {pd}, token cancelled");
}
await Task.Delay(1500);
Console.WriteLine($"D 1.5 s after cancel+dispose: IsConnected={d.IsConnected} {State()}");
d.Disconnect(); d.Dispose();
// Already-cancelled token.
var pe = new Probe(6);
try { await pe.ConnectServerAsync(new CancellationToken(true)); Console.WriteLine($"E pre-cancelled -> connected {pe.IsConnected}"); }
catch (Exception ex) { Console.WriteLine($"E pre-cancelled threw {ex.GetType().Name}"); }
pe.Dispose();
Console.WriteLine($"final: {State()}");


sealed class Probe : AdsSymbolicServer
{
    public Probe(byte version) : base(25860, "probe6", null) { symbolVersion = version; }
    protected override void OnCreateSymbols() { }
    protected override AdsErrorCode OnReadRawValue(ISymbol symbol, Span<byte> span) => AdsErrorCode.DeviceServiceNotSupported;
    protected override AdsErrorCode OnWriteRawValue(ISymbol symbol, ReadOnlySpan<byte> span) => AdsErrorCode.DeviceServiceNotSupported;
    protected override AdsErrorCode OnGetValue(ISymbol symbol, out object? value) { value = null; return AdsErrorCode.DeviceServiceNotSupported; }
    protected override AdsErrorCode OnSetValue(ISymbol symbol, object value, out bool valueChanged) { valueChanged = false; return AdsErrorCode.DeviceServiceNotSupported; }
}
