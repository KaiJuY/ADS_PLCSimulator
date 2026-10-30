using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow.Nodes;

internal static class AlarmInputs
{
    public static uint ErrorId(object? value, string name, long minimum)
    {
        if (value is null)
            throw new InvalidOperationException($"Input '{name}' has no link and no value.");

        long number = PlcValue.ToInt64(value);
        if (number < minimum || number > uint.MaxValue)
            throw new InvalidOperationException($"{name} {number} is outside {minimum}..{uint.MaxValue}.");
        return (uint)number;
    }
}

public sealed class AddServiceErrorNode(IndexedSymbol service, string message) : ExecNode
{
    public static readonly NodeDefinition Definition = new(
        "action.addServiceError",
        "Action",
        "Add Service Error",
        "Adds an entry to ServiceStatus.ErrorList of a service, like FB_ServiceBase.M_AddError. Outputs 'added' FALSE and the 'reason' when the MainErrorId is a duplicate or the list is full. A [#] in the service is replaced by the 'index' input.",
        [
            new("exec", PortKind.Exec, "exec"),
            new("index", PortKind.Data, "index", Optional: true),
            new("mainErrorId", PortKind.Data, "mainErrorId", Optional: true),
            new("sourceErrorId", PortKind.Data, "sourceErrorId", Optional: true),
        ],
        [
            new("then", PortKind.Exec, "then"),
            new("added", PortKind.Data, "added"),
            new("reason", PortKind.Data, "reason"),
        ],
        [
            new("service", ParamKind.Symbol, "Service (ST_ServiceCtrl)"),
            new("mainErrorId", ParamKind.Number, "Main error ID"),
            new("sourceErrorId", ParamKind.Number, "Source error ID", "0"),
            new("message", ParamKind.Text, "Message"),
        ],
        Create);

    public override Task<string?> ExecuteAsync(NodeExecution execution)
    {
        string path = service.Resolve(execution.Symbols, execution.Input("index")).Path;
        uint mainErrorId = AlarmInputs.ErrorId(execution.Input("mainErrorId"), "mainErrorId", 1);
        uint sourceErrorId = AlarmInputs.ErrorId(execution.Input("sourceErrorId"), "sourceErrorId", 0);
        ServiceHost host = execution.Registry.FindByService(path)
            ?? throw new InvalidOperationException($"No service.host runs {path}.");

        ServiceErrorResult result = host.AddServiceError(path, mainErrorId, sourceErrorId, message);
        execution.Output("added", result.Added);
        execution.Output("reason", result.Reason);
        return Task.FromResult<string?>("then");
    }

    private static AddServiceErrorNode Create(NodeSettings settings)
    {
        IndexedSymbol service = settings.IndexedSymbol("service", scalar: false);
        if (service.Type.Unaliased.Name != "ST_ServiceCtrl")
            throw new NodeConfigurationException($"'{service.Template}' is {service.Type.Name}, not ST_ServiceCtrl.");

        return new AddServiceErrorNode(service, settings.Text("message"));
    }
}

public sealed class SetModuleAlarmNode(IndexedSymbol module, string message, AlarmSeverity severity, string path) : ExecNode
{
    public static readonly NodeDefinition Definition = new(
        "action.setModuleAlarm",
        "Action",
        "Set Module Alarm",
        "Turns a hook alarm of a module on or off, like FB_ModuleBase.M_AddAlarm. It shows in ModuleAlarmList while active. A [#] in the module is replaced by the 'index' input.",
        [
            new("exec", PortKind.Exec, "exec"),
            new("index", PortKind.Data, "index", Optional: true),
            new("mainErrorId", PortKind.Data, "mainErrorId", Optional: true),
            new("sourceErrorId", PortKind.Data, "sourceErrorId", Optional: true),
            new("active", PortKind.Data, "active", Optional: true),
        ],
        [new("then", PortKind.Exec, "then")],
        [
            new("module", ParamKind.Symbol, "Module"),
            new("mainErrorId", ParamKind.Number, "Main error ID"),
            new("sourceErrorId", ParamKind.Number, "Source error ID", "0"),
            new("message", ParamKind.Text, "Message"),
            new("severity", ParamKind.Choice, "Severity", "Alarm", ["Alarm", "Warning"]),
            new("active", ParamKind.Choice, "Active", "TRUE", ["TRUE", "FALSE"]),
            new("path", ParamKind.Text, "Path (empty = module path)"),
        ],
        Create);

    public override Task<string?> ExecuteAsync(NodeExecution execution)
    {
        string modulePath = module.Resolve(execution.Symbols, execution.Input("index")).Path;
        uint mainErrorId = AlarmInputs.ErrorId(execution.Input("mainErrorId"), "mainErrorId", 1);
        uint sourceErrorId = AlarmInputs.ErrorId(execution.Input("sourceErrorId"), "sourceErrorId", 0);
        bool active = PlcValue.ToBoolean(execution.Input("active") ?? true);
        ServiceHost host = execution.Registry.FindByModule(modulePath)
            ?? throw new InvalidOperationException($"No service.host runs {modulePath}.");

        host.SetModuleAlarm(modulePath, mainErrorId, sourceErrorId, message, severity, path, active);
        return Task.FromResult<string?>("then");
    }

    private static SetModuleAlarmNode Create(NodeSettings settings)
    {
        IndexedSymbol module = settings.IndexedSymbol("module", scalar: false);
        if (module.Type.Unaliased is not StructType)
            throw new NodeConfigurationException($"'{module.Template}' is {module.Type.Name}, not a module struct.");

        AlarmSeverity severity = settings.Choice("severity") == "Warning" ? AlarmSeverity.Warning : AlarmSeverity.Alarm;
        return new SetModuleAlarmNode(module, settings.Text("message"), severity, settings.Text("path"));
    }
}
