using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow.Nodes;

/// <summary>Runs the PackML scan of all modules below a root, like one PLC task, for as long as the workflow is loaded.</summary>
public sealed class ServiceHostNode : HostedNode
{
    public static readonly NodeDefinition Definition = new(
        "service.host",
        "Service",
        "Service Host",
        "Runs the RobustSolution PackML handshake of every module below the root: ServiceId and CompletionBehavior come from the identity file.",
        [],
        [],
        [
            new("root", ParamKind.Symbol, "Root"),
            new("identityFile", ParamKind.Text, "Identity file"),
            new("defaultDelayMs", ParamKind.Number, "Default delay (ms)", "5000"),
        ],
        Create);

    private readonly ServiceHost _host;
    private ServiceHostRegistry? _registry;

    private ServiceHostNode(ServiceHost host)
    {
        _host = host;
    }

    public override async Task RunAsync(HostedNodeContext context, CancellationToken cancellation)
    {
        _registry = context.Registry;
        cancellation.ThrowIfCancellationRequested();
        context.Registry.Register(context.Workflow, _host);
        try
        {
            _host.Initialize(context.Image, context.Registry.ConfiguredAlarms);
            long start = TimeProvider.System.GetTimestamp();
            using var timer = new PeriodicTimer(ServiceHost.ScanInterval);
            while (await timer.WaitForNextTickAsync(cancellation))
                _host.Scan(context.Image, TimeProvider.System.GetElapsedTime(start));
        }
        finally
        {
            context.Registry.Unregister(_host);
        }
    }

    private static ServiceHostNode Create(NodeSettings settings)
    {
        SymbolRef root = settings.Symbol("root");
        string implementation = ReadIdentityFile(settings.ProjectFile("identityFile"));
        TimeSpan defaultDelay = TimeSpan.FromMilliseconds(settings.Number("defaultDelayMs", minimum: 0));

        ServiceHostNode? node = null;
        try
        {
            ServiceIdentityTable identities = ServiceIdentityTable.Parse(
                implementation,
                name => settings.Symbols.TryGetEnumValue(name, out long value) ? value : null);
            var host = new ServiceHost(settings.Symbols, root, identities, defaultDelay, path => node?._registry?.OverrideToken(path));
            node = new ServiceHostNode(host);
            return node;
        }
        catch (Exception exception) when (exception is ServiceHostException or ServiceIdentityException)
        {
            throw new NodeConfigurationException(exception.Message);
        }
    }

    private static string ReadIdentityFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new NodeConfigurationException($"Identity file could not be read: {exception.Message}");
        }
    }
}

/// <summary>Starts a run when a service enters Execute, so a workflow can take over how that service finishes.</summary>
public sealed class ServiceExecuteTriggerNode : MemoryTriggerNode, IRegistryAware
{
    private const string CtrlSegment = ".ServiceCtrl.";
    private const string StatusSegment = ".ServiceStatus.";
    private const string StatePath = ".PackMLOut.eStateCurrent";

    public static readonly NodeDefinition Definition = new(
        "trigger.onServiceExecute",
        "Trigger",
        "On Service Execute",
        "Runs when a service enters Execute. The service then waits for Complete Service or Abort Service instead of the default delay. A [#] in the service path gives the module index.",
        [],
        [new("then", PortKind.Exec, "then"), new("index", PortKind.Data, "index"), new("generation", PortKind.Data, "generation")],
        [new("service", ParamKind.Symbol, "Service (ST_ServiceCtrl)")],
        Create);

    private readonly IReadOnlyList<ServiceEntry> _entries;
    private ServiceHostRegistry? _registry;

    private ServiceExecuteTriggerNode(IReadOnlyList<ServiceEntry> entries)
    {
        _entries = entries;
    }

    private sealed record ServiceEntry(long? Index, string CtrlPath, SymbolRef State);

    public IReadOnlyList<string> CtrlPaths => _entries.Select(entry => entry.CtrlPath).ToList();

    public void Attach(ServiceHostRegistry registry) => _registry = registry;

    public override bool TryFire(MemoryWrite write, ProcessImage image, out Dictionary<string, object?> outputs)
    {
        outputs = [];
        foreach (ServiceEntry entry in _entries)
        {
            if (!write.Overlaps(entry.State.Offset, entry.State.Size) || !EntersExecute(write, image, entry.State))
                continue;

            outputs["index"] = entry.Index;
            outputs["generation"] = _registry?.FindByService(entry.CtrlPath)?.ExecuteGeneration(entry.CtrlPath);
            return true;
        }

        return false;
    }

    public override string BusyKey(IReadOnlyDictionary<string, object?> outputs) =>
        $"{outputs.GetValueOrDefault("index")}:{outputs.GetValueOrDefault("generation")}";

    private static bool EntersExecute(MemoryWrite write, ProcessImage image, SymbolRef state)
    {
        byte[] after = image.Read(state.Offset, state.Size);
        byte[] before = (byte[])after.Clone();
        int start = Math.Max(write.Offset, state.Offset);
        int end = Math.Min(write.Offset + write.Length, state.Offset + state.Size);
        write.NewData.AsSpan(start - write.Offset, end - start).CopyTo(after.AsSpan(start - state.Offset));
        write.OldData.AsSpan(start - write.Offset, end - start).CopyTo(before.AsSpan(start - state.Offset));

        long execute = (long)PackMLState.Execute;
        return PlcValue.ToInt64(ValueCodec.Decode(state.Type, after)) == execute
            && PlcValue.ToInt64(ValueCodec.Decode(state.Type, before)) != execute;
    }

    private static ServiceExecuteTriggerNode Create(NodeSettings settings)
    {
        IndexedSymbol service = settings.IndexedSymbol("service", scalar: false);
        if (service.Type.Unaliased.Name != "ST_ServiceCtrl")
            throw new NodeConfigurationException($"'{service.Template}' is {service.Type.Name}, not ST_ServiceCtrl.");

        int marker = service.Template.IndexOf(CtrlSegment, StringComparison.OrdinalIgnoreCase);
        // Stryker disable once Equality: a template that starts with the segment is no symbol path, so Parse has already refused it
        if (marker < 0)
            throw new NodeConfigurationException($"'{service.Template}' must contain '{CtrlSegment}' to find the matching ServiceStatus.");

        string statusPath = service.Template[..marker] + StatusSegment + service.Template[(marker + CtrlSegment.Length)..] + StatePath;
        IndexedSymbol state = IndexedSymbol.Parse(statusPath, settings.Symbols, scalar: true);
        IReadOnlyList<(long? Index, SymbolRef Symbol)> ctrls = service.ResolveAll(settings.Symbols);
        IReadOnlyList<(long? Index, SymbolRef Symbol)> states = state.ResolveAll(settings.Symbols);
        if (ctrls.Count != states.Count)
            throw new NodeConfigurationException($"'{service.Template}' and '{statusPath}' do not index the same range.");

        return new ServiceExecuteTriggerNode(ctrls.Zip(states, (ctrl, status) => new ServiceEntry(ctrl.Index, ctrl.Symbol.Path, status.Symbol)).ToList());
    }
}

public abstract class ServiceActionNode(IndexedSymbol service) : ExecNode
{
    protected static readonly IReadOnlyList<PortDefinition> Inputs =
    [
        new("exec", PortKind.Exec, "exec"),
        new("index", PortKind.Data, "index", Optional: true),
        new("generation", PortKind.Data, "generation", Optional: true),
    ];

    protected static readonly IReadOnlyList<ParamDefinition> ServiceParam = [new("service", ParamKind.Symbol, "Service (ST_ServiceCtrl)")];

    public override Task<string?> ExecuteAsync(NodeExecution execution)
    {
        string path = service.Resolve(execution.Symbols, execution.Input("index")).Path;
        object? generationInput = execution.Input("generation");
        long? generation = generationInput is null ? null : PlcValue.ToInt64(generationInput);
        ServiceHost host = execution.Registry.FindByService(path)
            ?? throw new InvalidOperationException($"No service.host runs {path}.");

        Apply(host, path, generation);
        return Task.FromResult<string?>("then");
    }

    protected abstract void Apply(ServiceHost host, string ctrlPath, long? generation);
}

public sealed class CompleteServiceNode(IndexedSymbol service) : ServiceActionNode(service)
{
    public static readonly NodeDefinition Definition = new(
        "action.completeService",
        "Action",
        "Complete Service",
        "Ends the Execute of a service that a trigger.onServiceExecute workflow handles. Fails when the service is not in the Execute that 'generation' names.",
        Inputs,
        [new("then", PortKind.Exec, "then")],
        ServiceParam,
        settings => new CompleteServiceNode(settings.IndexedSymbol("service", scalar: false)));

    protected override void Apply(ServiceHost host, string ctrlPath, long? generation) => host.Complete(ctrlPath, generation);
}

public sealed class AbortServiceNode(IndexedSymbol service) : ServiceActionNode(service)
{
    public static readonly NodeDefinition Definition = new(
        "action.abortService",
        "Action",
        "Abort Service",
        "Aborts a running service the way a PLC program calls RequestCommand(Abort). Fails when 'generation' is stale or a command is already queued.",
        Inputs,
        [new("then", PortKind.Exec, "then")],
        ServiceParam,
        settings => new AbortServiceNode(settings.IndexedSymbol("service", scalar: false)));

    protected override void Apply(ServiceHost host, string ctrlPath, long? generation) => host.Abort(ctrlPath, generation);
}
