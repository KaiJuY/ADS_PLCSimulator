// lists <port>: prints the non-empty SVID/DVID/ECID/ALID entries of GVL_Module.Chamber_Control[1..6]. Read-only.
using TwinCAT.Ads;

ushort port = ushort.Parse(args[0]);
if (port is 851 or 852)
    throw new ArgumentException("AMS 851/852 belong to another PLC on this machine.");
using var c = new AdsClient { Timeout = 3000 };
c.Connect(AmsNetId.Local, port);
const string Root = "GVL_Module.Chamber_Control";

for (int slot = 1; slot <= 6; slot++)
{
    string m = $"{Root}[{slot}]";
    Console.WriteLine($"== slot {slot} ModuleId={Read<uint>($"{m}.ModuleStatus.ModuleId")} Enabled={Read<bool>($"{m}.ModuleStatus.Enabled")} Type='{Str($"{m}.ModuleStatus.ModuleType")}' SVIDcount={Read<ushort>($"{m}.ModuleStatus.Variable.PublishedCount")} DVIDcount={Read<ushort>($"{m}.ModuleStatus.Data.PublishedCount")}");
    Descriptors($"{m}.ModuleVariableList", 100, "SVID", ecid: false);
    Descriptors($"{m}.ModuleDataList", 50, "DVID", ecid: false);
    Descriptors($"{m}.ModuleConfigParamList", 300, "ECID", ecid: true);
    for (int i = 1; ; i++)
    {
        string a = $"{m}.ModuleAlarmList[{i}]";
        if (!TryRead($"{a}.MainErrorId", out uint main))
            break;
        uint module = Read<uint>($"{a}.ModuleId"), service = Read<uint>($"{a}.ServiceId"), source = Read<uint>($"{a}.SourceErrorId");
        string msg = Str($"{a}.Message");
        if (main != 0 || module != 0 || service != 0 || msg.Length > 0)
            Console.WriteLine($"ALID[{i}] module={module} service={service} main={main} source={source} sev={Read<short>($"{a}.Severity")} active={Read<bool>($"{a}.Active")} ack={Read<bool>($"{a}.Acknowledged")} msg='{msg}' path='{Str($"{a}.Path")}'");
    }
}

void Descriptors(string list, int max, string kind, bool ecid)
{
    for (int i = 1; i <= max; i++)
    {
        string e = $"{list}[{i}]";
        uint id = Read<uint>($"{e}.Id");
        string name = Str($"{e}.Name");
        if (id == 0 && name.Length == 0)
            continue;
        string extra = ecid ? $" bound={Read<bool>($"{e}.IsBound")} limits={Read<bool>($"{e}.HasLimits")} [{Read<double>($"{e}.LowerLimit")}..{Read<double>($"{e}.UpperLimit")}]" : "";
        Console.WriteLine($"{kind}[{i}] id={id} name='{name}' type={Read<int>($"{e}.DataType")} unit='{Str($"{e}.Unit")}' value='{Str($"{e}.Value")}'{extra}");
    }
}

T Read<T>(string path) where T : notnull => (T)c.ReadValue(path, typeof(T));
bool TryRead(string path, out uint value)
{
    try { value = Read<uint>(path); return true; } catch (AdsErrorException) { value = 0; return false; }
}
string Str(string path)
{
    uint h = c.CreateVariableHandle(path);
    try
    {
        var buffer = new byte[256];
        int read = c.Read(0xF005, h, buffer.AsMemory());
        int end = Array.IndexOf(buffer, (byte)0, 0, read);
        return System.Text.Encoding.Latin1.GetString(buffer, 0, end < 0 ? read : end);
    }
    finally { c.DeleteVariableHandle(h); }
}
