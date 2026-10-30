using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

internal sealed class HostFixture
{
    public const string Declarations = """
        TYPE E_PackMLState : (Undefined := 0, Clearing := 1, Stopped := 2, Starting := 3, Idle := 4, Suspended := 5, Execute := 6,
            Stopping := 7, Aborting := 8, Aborted := 9, Holding := 10, Held := 11, Unholding := 12, Suspending := 13,
            Unsuspending := 14, Resetting := 15, Completing := 16, Complete := 17); END_TYPE
        TYPE E_PackMLCommand : (None := 0, Reset := 1, Start := 2, Stop := 3, Hold := 4, Unhold := 5, Suspend := 6,
            Unsuspend := 7, Abort := 8, Clear := 9); END_TYPE
        TYPE E_PackMLErrorID : (NoError := 0, ClearNotAllowed := 1002, ResetNotAllowed := 1003, StartNotAllowed := 1004,
            StopNotAllowed := 1005, HoldNotAllowed := 1006, UnholdNotAllowed := 1007, SuspendNotAllowed := 1008,
            UnsuspendNotAllowed := 1009, AbortNotAllowed := 1010, ModeNotSupported := 2001, ModeAlreadyActive := 2002,
            ModeChangeNotAllowed := 2003) UDINT; END_TYPE
        TYPE E_ServiceCompletionBehavior : (Unknown := 0, NaturalCompletion := 1, ExecuteUntilExternalStop := 2) UDINT; END_TYPE
        TYPE E_TestServiceId : (ServiceA := 101, ServiceB := 102) UDINT; END_TYPE
        TYPE T_PackMLMode : UINT; END_TYPE
        TYPE ST_PML_Base_In : STRUCT
            eCommand : E_PackMLCommand;
            bCommandChangeRequest : BOOL;
            eMode : T_PackMLMode;
            bModeChangeRequest : BOOL;
        END_STRUCT END_TYPE
        TYPE ST_PML_Base_Out : STRUCT
            eStateCurrent : E_PackMLState;
            eStatePrevious : E_PackMLState;
            eModeCurrent : T_PackMLMode;
            eModePrevious : T_PackMLMode;
            bCommandAccepted : BOOL;
            bCommandRejected : BOOL;
            eStateErrorID : E_PackMLErrorID;
            bModeChanged : BOOL;
            bModeChangeRejected : BOOL;
            eModeErrorID : E_PackMLErrorID;
        END_STRUCT END_TYPE
        TYPE ST_ServiceCtrl : STRUCT
            RequestId : UDINT;
            PackMLIn : ST_PML_Base_In;
            AliveIndex : UDINT;
            AliveTimeout : TIME;
        END_STRUCT END_TYPE
        TYPE ST_ServiceStatus : STRUCT
            ResponseId : UDINT;
            PackMLOut : ST_PML_Base_Out;
            CompletionBehavior : E_ServiceCompletionBehavior;
            ServiceId : UDINT;
        END_STRUCT END_TYPE
        TYPE ST_ModuleCtrl : STRUCT
            RequestId : UDINT;
            PackMLIn : ST_PML_Base_In;
        END_STRUCT END_TYPE
        TYPE ST_ModuleStatus : STRUCT
            ModuleId : UDINT;
            Enabled : BOOL;
            IsControlSourceAlive : BOOL;
            ModuleType : STRING(16);
            ResponseId : UDINT;
            PackMLOut : ST_PML_Base_Out;
        END_STRUCT END_TYPE
        TYPE ST_TestServiceCtrls : STRUCT A : ST_ServiceCtrl; B : ST_ServiceCtrl; Unbound : ST_ServiceCtrl; END_STRUCT END_TYPE
        TYPE ST_TestServiceStatuses : STRUCT A : ST_ServiceStatus; B : ST_ServiceStatus; Unbound : ST_ServiceStatus; END_STRUCT END_TYPE
        TYPE ST_TestParam : STRUCT Value : INT; END_STRUCT END_TYPE
        TYPE ST_TestServiceParams : STRUCT A : ST_TestParam; B : ST_TestParam; Unbound : ST_TestParam; END_STRUCT END_TYPE
        TYPE ST_TestModule : STRUCT
            ModuleCtrl : ST_ModuleCtrl;
            ModuleStatus : ST_ModuleStatus;
            ServiceCtrl : ST_TestServiceCtrls;
            ServiceStatus : ST_TestServiceStatuses;
            ServiceParam : ST_TestServiceParams;
        END_STRUCT END_TYPE
        VAR_GLOBAL
            Modules : ARRAY[1..2] OF ST_TestModule;
        END_VAR
        """;

    public const string Identities = """
        F_Set(Status := ServiceStatus.A, ServiceId := E_TestServiceId.ServiceA, CompletionBehavior := E_ServiceCompletionBehavior.NaturalCompletion);
        F_Set(Status := ServiceStatus.B, ServiceId := E_TestServiceId.ServiceB, CompletionBehavior := E_ServiceCompletionBehavior.ExecuteUntilExternalStop);
        ServiceStatus.Unbound.CompletionBehavior := E_ServiceCompletionBehavior.NaturalCompletion;
        """;

    public const string AlarmTypes = """
        TYPE E_Severity : (Alarm := 1, Warning := 2); END_TYPE
        TYPE E_AlarmAckCommand : (None := 0, AcknowledgeOne := 1, AcknowledgeAll := 2); END_TYPE
        TYPE E_AlarmAckError : (None := 0, InvalidRequestId := 1, DuplicateRequest := 2, InvalidCommand := 3, AlarmNotFound := 4); END_TYPE
        TYPE ST_ExtAlarm : STRUCT
            ModuleId : UDINT;
            ServiceId : UDINT;
            MainErrorId : UDINT;
            SourceErrorId : UDINT;
            Message : STRING(80);
            Path : STRING(255);
            Severity : E_Severity;
            DateTime : STRING(20);
            Active : BOOL;
            Acknowledged : BOOL;
            OccurrenceCount : UDINT;
        END_STRUCT END_TYPE
        TYPE ST_AlarmAckRequest : STRUCT
            RequestId : UDINT;
            Command : E_AlarmAckCommand;
            MainErrorId : UDINT;
            ServiceId : UDINT;
            bChangeRequest : BOOL;
        END_STRUCT END_TYPE
        TYPE ST_ModuleAlarmStatus : STRUCT
            AckResponseId : UDINT;
            AckAccepted : BOOL;
            AckError : E_AlarmAckError;
            ConfiguredLatchedCount : UINT;
            ServiceLatchedCount : UINT;
            Overflow : BOOL;
            HookLatchedCount : UINT;
            DuplicateId : UDINT;
        END_STRUCT END_TYPE
        TYPE ST_TestSources : STRUCT Pressure : REAL; Flag : BOOL; Text : STRING(80); END_STRUCT END_TYPE
        """;

    /// <summary>The declarations of <see cref="Declarations"/> plus the alarm members of the framework: ErrorList, ModuleAlarmList, ModuleStatus.Alarm, ModuleCtrl.AlarmAckRequest and a Sources struct for configured alarms.</summary>
    public static readonly string AlarmDeclarations = WithAlarmMembers(Declarations);

    /// <summary>Like <see cref="AlarmDeclarations"/> but ModuleStatus has no ModuleId.</summary>
    public static readonly string AlarmDeclarationsWithoutModuleId = WithAlarmMembers(Declarations.Replace("ModuleId : UDINT;", "Spare : UDINT;"));

    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, object?> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public HostFixture(
        TimeSpan? defaultDelay = null, bool initialize = true, string? declarations = null,
        ConfiguredAlarmTable? configuredAlarms = null, Func<DateTimeOffset>? utcNow = null)
    {
        Program = TestPaths.BuildFromText(declarations ?? Declarations);
        Image = Program.CreateProcessImage();
        ConfiguredAlarms = configuredAlarms ?? ConfiguredAlarmTable.Empty;
        Host = CreateHost(Program, Image, defaultDelay ?? DefaultDelay, token => _tokens.GetValueOrDefault(token), utcNow);
        if (initialize)
            Host.Initialize(Image, ConfiguredAlarms);
    }

    public ConfiguredAlarmTable ConfiguredAlarms { get; }

    public PlcProgram Program { get; }

    public ProcessImage Image { get; }

    public SymbolTable Symbols => Program.Symbols;

    public ServiceHost Host { get; set; }

    public TimeSpan Now { get; set; }

    public static ServiceIdentityTable ParseIdentities(SymbolTable symbols, string text = Identities) =>
        ServiceIdentityTable.Parse(text, name => symbols.TryGetEnumValue(name, out long value) ? value : null);

    public static ServiceHost CreateHost(
        PlcProgram program, ProcessImage image, TimeSpan defaultDelay, Func<string, object?> overrideToken, Func<DateTimeOffset>? utcNow = null) =>
        new(program.Symbols, program.Symbols.Resolve("GVL_Test.Modules"), ParseIdentities(program.Symbols), defaultDelay, overrideToken, utcNow);

    private static string WithAlarmMembers(string declarations) => AlarmTypes + Environment.NewLine + declarations
        .Replace("TYPE ST_ModuleCtrl : STRUCT", "TYPE ST_ModuleCtrl : STRUCT AlarmAckRequest : ST_AlarmAckRequest;")
        .Replace("TYPE ST_ModuleStatus : STRUCT", "TYPE ST_ModuleStatus : STRUCT Alarm : ST_ModuleAlarmStatus;")
        .Replace("TYPE ST_ServiceStatus : STRUCT", "TYPE ST_ServiceStatus : STRUCT ErrorList : ARRAY[1..10] OF ST_ExtAlarm;")
        .Replace("TYPE ST_TestModule : STRUCT", "TYPE ST_TestModule : STRUCT ModuleAlarmList : ARRAY[1..100] OF ST_ExtAlarm; Sources : ST_TestSources;");

    public static string CtrlPath(int module, string service) => $"GVL_Test.Modules[{module}].ServiceCtrl.{service}";

    public void SetOverrideToken(int module, string service, object? token) => _tokens[CtrlPath(module, service)] = token;

    public void Scan(TimeSpan? advance = null)
    {
        Now += advance ?? TimeSpan.Zero;
        Host.Scan(Image, Now);
    }

    public void ScanAt(TimeSpan now)
    {
        Now = now;
        Host.Scan(Image, Now);
    }

    public void Write(string path, object value) =>
        Image.WriteValue(Symbols.Resolve(path), value, WriteSource.Client);

    public object Read(string path) => Image.ReadValue(Symbols.Resolve(path));

    public void SendToService(int module, string service, PackMLCommand command, uint requestId = 1)
    {
        string path = CtrlPath(module, service);
        Write($"{path}.RequestId", (long)requestId);
        Write($"{path}.PackMLIn.eCommand", (long)command);
        Write($"{path}.PackMLIn.bCommandChangeRequest", true);
    }

    public void ReleaseService(int module, string service) =>
        Write($"{CtrlPath(module, service)}.PackMLIn.bCommandChangeRequest", false);

    public void SendToModule(int module, PackMLCommand command, uint requestId = 1)
    {
        string path = $"GVL_Test.Modules[{module}].ModuleCtrl";
        Write($"{path}.RequestId", (long)requestId);
        Write($"{path}.PackMLIn.eCommand", (long)command);
        Write($"{path}.PackMLIn.bCommandChangeRequest", true);
    }

    public void ReleaseModule(int module) =>
        Write($"GVL_Test.Modules[{module}].ModuleCtrl.PackMLIn.bCommandChangeRequest", false);

    public void SendModeToModule(int module, long mode, uint requestId = 1)
    {
        string path = $"GVL_Test.Modules[{module}].ModuleCtrl";
        Write($"{path}.RequestId", (long)requestId);
        Write($"{path}.PackMLIn.eMode", mode);
        Write($"{path}.PackMLIn.bModeChangeRequest", true);
    }

    public void ModuleCommand(int module, PackMLCommand command, uint requestId = 1)
    {
        SendToModule(module, command, requestId);
        Scan();
        ReleaseModule(module);
        Scan();
    }

    public PackMLState ServiceState(int module, string service) =>
        (PackMLState)(long)Read($"GVL_Test.Modules[{module}].ServiceStatus.{service}.PackMLOut.eStateCurrent");

    public PackMLState ModuleState(int module) =>
        (PackMLState)(long)Read($"GVL_Test.Modules[{module}].ModuleStatus.PackMLOut.eStateCurrent");

    public object ServiceStatus(int module, string service, string field) =>
        Read($"GVL_Test.Modules[{module}].ServiceStatus.{service}.{field}");

    public object ModuleStatus(int module, string field) => Read($"GVL_Test.Modules[{module}].ModuleStatus.{field}");

    public void ServiceCommand(int module, string service, PackMLCommand command, uint requestId = 1)
    {
        SendToService(module, service, command, requestId);
        Scan();
        ReleaseService(module, service);
        Scan();
    }

    public void StartServiceA(uint requestId = 1) => ServiceCommand(1, "A", PackMLCommand.Start, requestId);
}
