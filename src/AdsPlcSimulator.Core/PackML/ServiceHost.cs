using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.PackML.Behavior;
using AdsPlcSimulator.Core.PackML.ConfigParam;
using AdsPlcSimulator.Core.Plc;
using static AdsPlcSimulator.Core.PackML.PlcLayout;

namespace AdsPlcSimulator.Core.PackML;

/// <summary>All modules below one root, scanned together like one PLC task.</summary>
public sealed partial class ServiceHost
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(10);

    private static readonly IReadOnlyDictionary<PackMLState, PackMLState> ModuleWaitsForServices = new Dictionary<PackMLState, PackMLState>
    {
        [PackMLState.Clearing] = PackMLState.Stopped,
        [PackMLState.Stopping] = PackMLState.Stopped,
        [PackMLState.Resetting] = PackMLState.Idle,
        [PackMLState.Aborting] = PackMLState.Aborted,
    };

    private static readonly PackMLCommand[] ForwardedModuleCommands =
        [PackMLCommand.Stop, PackMLCommand.Abort, PackMLCommand.Clear, PackMLCommand.Reset];

    private const uint AliveTimeoutErrorId = 940;

    private static long s_generationCounter;

    private readonly Lock _lock = new();
    private readonly List<ModuleUnit> _modules = [];
    private readonly Dictionary<string, ServiceUnit> _bound = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _boundPaths = [];
    private readonly TimeSpan _defaultDelay;
    private readonly Func<string, object?> _overrideToken;
    private readonly SymbolTable _symbols;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly AliveSettings _alive;
    private readonly Dictionary<string, ModuleUnit> _moduleByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleUnit> _moduleByService = new(StringComparer.OrdinalIgnoreCase);
    private bool _retired;
    private ProcessImage? _image;

    /// <param name="utcNow">The clock of alarm DateTime; null = DateTimeOffset.UtcNow.</param>
    /// <param name="alive">Heartbeat and Service Alive timeouts; null = AliveSettings.Default.</param>
    /// <exception cref="ServiceHostException">The root does not have the module shape, or no service is bound.</exception>
    public ServiceHost(
        SymbolTable symbols, SymbolRef root, ServiceIdentityTable identities, TimeSpan defaultDelay,
        Func<string, object?> overrideToken, Func<DateTimeOffset>? utcNow = null,
        AliveSettings? alive = null)
    {
        RootPath = root.Path;
        _symbols = symbols;
        _defaultDelay = defaultDelay;
        _overrideToken = overrideToken;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _alive = alive ?? AliveSettings.Default;

        foreach (SymbolRef module in FindModules(root))
            _modules.Add(ModuleUnit.Create(symbols, module, identities, _alive));

        foreach (ModuleUnit module in _modules)
        {
            _moduleByPath[module.Path] = module;
            foreach (ServiceUnit service in module.BoundServices)
            {
                _bound[service.CtrlPath] = service;
                _boundPaths.Add(service.CtrlPath);
                _moduleByService[service.CtrlPath] = module;
            }
        }

        if (_bound.Count == 0)
            throw new ServiceHostException($"No service under {root.Path} has a ServiceId.");
    }

    public string RootPath { get; }

    public IReadOnlyList<string> BoundServices => _boundPaths;

    /// <summary>The same as Initialize(image, ConfiguredAlarmTable.Empty).</summary>
    public void Initialize(ProcessImage image) => Initialize(image, ConfiguredAlarmTable.Empty);

    /// <summary>The same as Initialize(image, configuredAlarms, ServiceBehaviorTable.Empty).</summary>
    public void Initialize(ProcessImage image, ConfiguredAlarmTable configuredAlarms) =>
        Initialize(image, configuredAlarms, ServiceBehaviorTable.Empty);

    /// <exception cref="ServiceHostException">A configured alarm source or a behavior path cannot be resolved.</exception>
    public void Initialize(ProcessImage image, ConfiguredAlarmTable configuredAlarms, ServiceBehaviorTable behaviors)
    {
        lock (_lock)
        {
            if (_retired)
                return;
            foreach (ModuleUnit module in _modules)
                InitializeModule(image, module, configuredAlarms.For(module.Path), behaviors.For(module.Path));
            _image = image;
        }
    }

    /// <summary>Does nothing once the host is retired.</summary>
    /// <param name="now">Monotonic elapsed time, e.g. from TimeProvider.GetElapsedTime(start).</param>
    public void Scan(ProcessImage image, TimeSpan now)
    {
        lock (_lock)
        {
            if (_retired)
                return;
            string utc = AlarmClock.Format(_utcNow());
            foreach (ModuleUnit module in _modules)
                ScanModule(image, module, now, utc);
        }
    }

    public bool ContainsModule(string modulePath) => _moduleByPath.ContainsKey(modulePath);

    /// <exception cref="InvalidOperationException">Not a bound service, its ServiceStatus has no ErrorList, the host is retired or not initialized, or MainErrorId is 0.</exception>
    public ServiceErrorResult AddServiceError(string ctrlPath, uint mainErrorId, uint sourceErrorId, string message)
    {
        lock (_lock)
        {
            ProcessImage image = RequireUsable();
            ServiceUnit service = Bound(ctrlPath);
            if (service.Errors is null)
                throw new InvalidOperationException($"{ctrlPath} has no ErrorList in its ServiceStatus.");
            if (mainErrorId == 0)
                throw new InvalidOperationException("MainErrorId 0 means no error.");

            return AddError(service, image, mainErrorId, sourceErrorId, message);
        }
    }

    /// <exception cref="InvalidOperationException">The host is retired or not initialized, the path is not a module of this host or has no alarm members, MainErrorId is 0, or the module already has 30 other hook alarms.</exception>
    public void SetModuleAlarm(string modulePath, uint mainErrorId, uint sourceErrorId, string message, AlarmSeverity severity, string path, bool active)
    {
        lock (_lock)
        {
            RequireUsable();
            if (!_moduleByPath.TryGetValue(modulePath, out ModuleUnit? module))
                throw new InvalidOperationException($"{modulePath} is not a module of {RootPath}.");
            if (module.Alarm is null)
                throw new InvalidOperationException($"{modulePath} has no alarm members (ModuleAlarmList, ModuleStatus.Alarm, ModuleCtrl.AlarmAckRequest).");
            if (mainErrorId == 0)
                throw new InvalidOperationException("MainErrorId 0 means no alarm.");

            module.SetHook(new HookSubmission(mainErrorId, sourceErrorId, message, severity, path), active);
        }
    }

    private ExtAlarm BuildServiceError(ServiceUnit service, ProcessImage image, uint mainErrorId, uint sourceErrorId, string message)
    {
        uint moduleId = _moduleByService[service.CtrlPath].Alarm?.ReadModuleId(image) ?? 0;
        return new ExtAlarm(
            moduleId, service.Identity!.ServiceId, mainErrorId, sourceErrorId, message, service.StatusPath,
            AlarmSeverity.Alarm, AlarmClock.Format(_utcNow()), true, false, 1);
    }

    private void AddFrameworkError(ServiceUnit service, ProcessImage image, uint mainErrorId, uint sourceErrorId, string message)
    {
        if (service.Errors is null)
            return;

        AddError(service, image, mainErrorId, sourceErrorId, message);
    }

    private ServiceErrorResult AddError(ServiceUnit service, ProcessImage image, uint mainErrorId, uint sourceErrorId, string message)
    {
        ServiceErrorResult result = service.Errors!.Add(BuildServiceError(service, image, mainErrorId, sourceErrorId, message));
        if (result.Added)
            service.Errors.WriteIfChanged(image);
        return result;
    }

    private ProcessImage RequireUsable()
    {
        if (_retired)
            throw new InvalidOperationException($"{RootPath} is retired; the workflow was reloaded.");
        return _image is { } image
            ? image
            : throw new InvalidOperationException($"{RootPath} is not initialized yet.");
    }

    /// <summary>Stops all later scans and writes; takes the host lock, so a running scan finishes first.</summary>
    public void Retire()
    {
        lock (_lock)
            _retired = true;
    }

    /// <summary>Id of the service's current Execute entry, taken from one process-wide counter, so it never repeats across hosts or reloads; 0 before the first entry and after Initialize.</summary>
    public long ExecuteGeneration(string ctrlPath)
    {
        lock (_lock)
            return Bound(ctrlPath).Generation;
    }

    /// <summary>True when the current Execute entry of the service was refused by a Mode or parameter check; false before the first entry.</summary>
    /// <exception cref="InvalidOperationException">The service is not bound.</exception>
    public bool IsExecuteRejected(string ctrlPath)
    {
        lock (_lock)
            return Bound(ctrlPath).ExecuteRejected;
    }

    public bool Contains(string ctrlPath) => _bound.ContainsKey(ctrlPath);

    /// <param name="generation">Null skips the check; otherwise it must equal the current ExecuteGeneration.</param>
    /// <exception cref="InvalidOperationException">The service is not bound, not in Execute, not overridden for this Execute, or the generation differs.</exception>
    public void Complete(string ctrlPath, long? generation)
    {
        lock (_lock)
        {
            ServiceUnit service = Bound(ctrlPath);
            CheckGeneration(service, generation);
            if (service.Machine.State != PackMLState.Execute)
                throw new InvalidOperationException($"{ctrlPath} is {service.Machine.State}, not Execute.");
            if (!service.IsOverridden(_overrideToken))
                throw new InvalidOperationException($"{ctrlPath} is not overridden for this Execute; no workflow completes it.");

            service.CompletePending = true;
        }
    }

    /// <exception cref="InvalidOperationException">The service is not bound, the generation differs, or an internal command is already queued.</exception>
    public void Abort(string ctrlPath, long? generation)
    {
        lock (_lock)
        {
            ServiceUnit service = Bound(ctrlPath);
            CheckGeneration(service, generation);
            if (!service.Machine.RequestCommand(PackMLCommand.Abort, service.LastModeRequest))
                throw new InvalidOperationException($"{ctrlPath} cannot take an internal Abort now: a command is queued or a mode request is high.");
        }
    }

    private static IEnumerable<SymbolRef> FindModules(SymbolRef root)
    {
        PlcType type = root.Type.Unaliased;
        if (type is StructType)
            return [root];
        if (type is not ArrayType { Dimensions.Count: 1 } array || array.ElementType.Unaliased is not StructType)
            throw new ServiceHostException($"{root.Path} is {root.Type.Name}; it must be a module struct or a one-dimensional array of module structs.");

        ArrayDimension bounds = array.Dimensions[0];
        return Enumerable.Range(bounds.LowerBound, bounds.Length)
            .Select(index => new SymbolRef($"{root.Path}[{index}]", array.ElementType, root.Offset + array.OffsetOf([index])));
    }

    private static void CheckGeneration(ServiceUnit service, long? generation)
    {
        if (generation is { } expected && expected != service.Generation)
            throw new InvalidOperationException($"{service.CtrlPath} is at Execute entry {service.Generation}, not {expected}; the run is stale.");
    }

    private ServiceUnit Bound(string ctrlPath) =>
        _bound.TryGetValue(ctrlPath, out ServiceUnit? service)
            ? service
            : throw new InvalidOperationException($"{ctrlPath} is not a bound service of {RootPath}.");

    private void InitializeModule(ProcessImage image, ModuleUnit module, IReadOnlyList<ConfiguredAlarm> configured, ModuleBehavior? behavior)
    {
        module.Rules = ModuleRules.Create(_symbols, module.Path, behavior, module.BoundServices.Select(service => service.RelativePath).ToList());
        module.ResetAlarms(image, ResolveConfigured(module, configured));
        module.ReadInputs(image);
        RawInput input = module.ReadModuleInput();
        module.Machine = new PackMLMachine(PackMLState.Execute, PackMLState.Starting);
        module.Status.Reset(image, module.Machine);
        module.Machine.SeedInput(input.PackML);
        module.LastCommandRequest = input.PackML.CommandRequest;
        module.LastModeRequest = input.PackML.ModeRequest;
        module.ResetHeartbeat(image);

        foreach (ServiceUnit service in module.Services)
            service.WriteIdentity(image);
        foreach (ServiceUnit service in module.BoundServices)
        {
            service.Rules = module.Rules?.For(service.RelativePath);
            service.Reset(image, module.ReadServiceInput(service));
        }
    }

    private List<ConfiguredBinding> ResolveConfigured(ModuleUnit module, IReadOnlyList<ConfiguredAlarm> configured)
    {
        var bindings = new List<ConfiguredBinding>(configured.Count);
        foreach (ConfiguredAlarm alarm in configured)
        {
            string path = $"{module.Path}.{alarm.Source}";
            if (!_symbols.TryResolve(path, out SymbolRef source, out string? error))
                throw new ServiceHostException($"Configured alarm {alarm.MainErrorId} cannot resolve {path}: {error}");
            bindings.Add(new ConfiguredBinding(alarm, source));
        }

        return bindings;
    }

    private void ScanModule(ProcessImage image, ModuleUnit module, TimeSpan now, string utc)
    {
        module.ScanHeartbeat(image, now);
        module.ReadInputs(image);
        RawInput moduleInput = module.ReadModuleInput();
        module.Machine.Scan(moduleInput.PackML, state => IsModuleActingStateDone(module, state));
        bool moduleInExecute = module.Machine.State == PackMLState.Execute;

        foreach (ServiceUnit service in module.BoundServices)
            ScanService(image, module, service, module.ReadServiceInput(service), module.ReadServiceAlive(service, moduleInExecute), moduleInput, moduleInExecute, now);

        UpdateModuleResponse(module, moduleInput.PackML);
        module.Status.Write(image, module.Machine);
        module.ScanAlarms(image, utc);
    }

    private static bool IsModuleActingStateDone(ModuleUnit module, PackMLState state)
    {
        if (state == PackMLState.Execute)
            return false;
        if (!ModuleWaitsForServices.TryGetValue(state, out PackMLState awaited))
            return true;
        return module.BoundServices.All(service => service.LastState == awaited);
    }

    private static void UpdateModuleResponse(ModuleUnit module, PackMLInput input)
    {
        bool commandEdge = input.CommandRequest && !module.LastCommandRequest;
        bool modeEdge = input.ModeRequest && !module.LastModeRequest;
        module.LastCommandRequest = input.CommandRequest;
        module.LastModeRequest = input.ModeRequest;
        if (commandEdge || modeEdge)
            module.Status.ResponseId = module.ReadModuleInput().RequestId;
    }

    private void ScanService(ProcessImage image, ModuleUnit owner, ServiceUnit service, RawInput client, AliveInput alive, RawInput module, bool moduleInExecute, TimeSpan now)
    {
        (long requestId, PackMLInput effective) = moduleInExecute
            ? (client.RequestId, ApplyBlocking(service, client.PackML))
            : (module.RequestId, ForwardModuleInput(service, client.PackML, module.PackML));

        ApplyAlive(service, alive, ref effective, image, now);
        if (effective.CommandRequest && !service.LastCommandRequest)
            service.Status.ResponseId = requestId;
        service.LastCommandRequest = effective.CommandRequest;
        service.LastModeRequest = effective.ModeRequest;

        PackMLState before = service.Machine.State;
        bool modeRequestHigh = effective.ModeRequest;
        service.Machine.Scan(effective, state => IsServiceActingStateDone(service, state, image, modeRequestHigh, now));
        service.LastState = service.Machine.State;
        if (before != PackMLState.Execute && service.Machine.State == PackMLState.Execute)
            EnterExecute(owner, service, image, now, modeRequestHigh);
        else if (before == PackMLState.Execute && service.Machine.State != PackMLState.Execute)
            LeaveExecute(service, image);
        service.OnScanned(before, alive, _alive, now);
        if (PassedThroughClearingThisScan(before, service.Machine))
            service.ClearErrors(image);

        service.Status.Write(image, service.Machine);
    }

    private void ApplyAlive(ServiceUnit service, AliveInput alive, ref PackMLInput effective, ProcessImage image, TimeSpan now)
    {
        if (service.EvaluateAlive(alive, now) is { } timeout)
            AddFrameworkError(service, image, AliveTimeoutErrorId, (uint)timeout.TotalMilliseconds, "Service Alive timeout expired.");
        if (!service.AbortPending)
            return;

        effective = effective with { ModeRequest = false };
        if (service.Machine.RequestCommand(PackMLCommand.Abort, modeRequestHigh: false))
            service.ClearPendingAbort();
    }

    private static bool PassedThroughClearingThisScan(PackMLState before, PackMLMachine machine) =>
        // Stryker disable once Equality: Clearing is an acting state that completes in the scan that enters it, so machine.State is never Clearing after a scan and the right operand is never evaluated
        (machine.State == PackMLState.Clearing && before != PackMLState.Clearing)
        || (machine.State != before && machine.PreviousState == PackMLState.Clearing);

    private static PackMLInput ForwardModuleInput(ServiceUnit service, PackMLInput client, PackMLInput module)
    {
        service.CommandBlocked |= client.CommandRequest;
        service.ModeBlocked |= client.ModeRequest;
        bool forwardsCommand = module.CommandRequest && ForwardedModuleCommands.Contains((PackMLCommand)module.Command);
        return forwardsCommand ? module : module with { Command = 0, CommandRequest = false };
    }

    private static PackMLInput ApplyBlocking(ServiceUnit service, PackMLInput client)
    {
        service.CommandBlocked &= client.CommandRequest;
        service.ModeBlocked &= client.ModeRequest;
        return client with
        {
            CommandRequest = client.CommandRequest && !service.CommandBlocked,
            ModeRequest = client.ModeRequest && !service.ModeBlocked,
        };
    }

    private bool IsServiceActingStateDone(ServiceUnit service, PackMLState state, ProcessImage image, bool modeRequestHigh, TimeSpan now)
    {
        if (state == PackMLState.Starting)
            return IsStartingDone(service, image, modeRequestHigh);
        if (state != PackMLState.Execute)
            return true;
        if (service.ExecuteRejected)
            return RetryRejectedAbort(service, modeRequestHigh);
        if (service.IsOverridden(_overrideToken))
            return service.TakeCompletion();

        bool done = service.ConfigParams is not null || IsNaturalDelayElapsed(service, now);
        if (done)
            service.Rules?.WriteBeforeComplete(image, service.EntryDryRun);
        return done;
    }

    private bool IsNaturalDelayElapsed(ServiceUnit service, TimeSpan now) =>
        service.Behavior == CompletionBehavior.NaturalCompletion
        && now - service.ExecuteEntered >= (service.EntryDuration ?? _defaultDelay);

    // The Abort of a refused entry is queued in the entry scan; it is retried here only when a mode request was high then.
    private static bool RetryRejectedAbort(ServiceUnit service, bool modeRequestHigh)
    {
        if (service.ExecuteAbortPending && service.Machine.RequestCommand(PackMLCommand.Abort, modeRequestHigh))
            service.ExecuteAbortPending = false;
        return false;
    }

    private bool IsStartingDone(ServiceUnit service, ProcessImage image, bool modeRequestHigh)
    {
        if (service.ConfigParams is null)
            return true;

        if (!service.StartingChecked)
        {
            service.StartingChecked = true;
            ConfigParamCheck check = service.ConfigParams.ValidateAndStage(image);
            if (check.Passed)
                return true;

            AddFrameworkError(service, image, check.MainErrorId, check.ParamId, check.Message);
            service.StartingAbortPending = true;
        }

        if (service.StartingAbortPending && service.Machine.RequestCommand(PackMLCommand.Abort, modeRequestHigh))
            // Stryker disable once Boolean: Abort is queued and runs at the start of the next scan, which leaves Starting before this method is asked again; ResetStarting then clears the flag
            service.StartingAbortPending = false;
        return false;
    }

    private void EnterExecute(ModuleUnit owner, ServiceUnit service, ProcessImage image, TimeSpan now, bool modeRequestHigh)
    {
        service.ExecuteEntered = now;
        service.Generation = Interlocked.Increment(ref s_generationCounter);
        service.CompletePending = false;
        object? token = _overrideToken(service.CtrlPath);

        // Unholding and Unsuspending continue one action: no new Mode or parameter check (R7).
        if (service.Machine.PreviousState != PackMLState.Starting)
        {
            service.EntryToken = token;
            service.ConfigParams?.Apply(image);
            if (token is null)
                service.Rules?.WriteOnEnter(image, service.EntryDryRun);
            return;
        }

        service.BeginExecuteEntry();
        EntryCheck check = CheckEntry(owner, service, token is not null, image, out bool dryRun);
        if (!check.Passed)
        {
            RejectEntry(service, image, check, modeRequestHigh);
            return;
        }

        service.EntryToken = token;
        service.ConfigParams?.Apply(image);
        service.EntryDryRun = dryRun;
        if (token is not null)
            return;

        service.EntryDuration = service.Rules?.ReadDuration(image);
        service.Rules?.WriteOnEnter(image, dryRun);
    }

    // The table covers only the Mode for a service without an entry, and only the Mode for an overridden service (a workflow owns its run).
    private static EntryCheck CheckEntry(ModuleUnit owner, ServiceUnit service, bool overridden, ProcessImage image, out bool dryRun)
    {
        dryRun = false;
        if (owner.Rules is null)
            return EntryCheck.Pass;

        EntryCheck mode = owner.Rules.CheckMode(service.RelativePath, image, out dryRun);
        if (!mode.Passed || overridden || service.Rules is null)
            return mode;
        return service.Rules.CheckParams(image);
    }

    private void RejectEntry(ServiceUnit service, ProcessImage image, EntryCheck check, bool modeRequestHigh)
    {
        service.ExecuteRejected = true;
        service.EntryToken = null;
        AddFrameworkError(service, image, check.MainErrorId, check.SourceErrorId, check.Message);
        if (!service.Machine.RequestCommand(PackMLCommand.Abort, modeRequestHigh))
            service.ExecuteAbortPending = true;
    }

    private static void LeaveExecute(ServiceUnit service, ProcessImage image)
    {
        service.ExecuteAbortPending = false;
        if (service.ExecuteRejected || service.EntryToken is not null)
            return;

        service.Rules?.WriteOnLeave(image, service.EntryDryRun);
    }

    private readonly record struct RawInput(long RequestId, PackMLInput PackML);

    private sealed class InputLayout
    {
        private readonly Slot _requestId;
        private readonly Slot _command;
        private readonly Slot _commandRequest;
        private readonly Slot _mode;
        private readonly Slot _modeRequest;

        public InputLayout(PlcType ctrlType, string owner)
        {
            _requestId = Locate(ctrlType, owner, "RequestId");
            _command = Locate(ctrlType, owner, "PackMLIn", "eCommand");
            _commandRequest = Locate(ctrlType, owner, "PackMLIn", "bCommandChangeRequest");
            _mode = Locate(ctrlType, owner, "PackMLIn", "eMode");
            _modeRequest = Locate(ctrlType, owner, "PackMLIn", "bModeChangeRequest");
            Size = ctrlType.Size;
        }

        public int Size { get; }

        public RawInput Decode(ReadOnlySpan<byte> ctrl) => new(
            PlcValue.ToInt64(_requestId.Read(ctrl)),
            new PackMLInput(
                PlcValue.ToInt64(_command.Read(ctrl)),
                PlcValue.ToBoolean(_commandRequest.Read(ctrl)),
                PlcValue.ToInt64(_mode.Read(ctrl)),
                PlcValue.ToBoolean(_modeRequest.Read(ctrl))));

        private static Slot Locate(PlcType type, string owner, params string[] path)
        {
            int offset = 0;
            PlcType current = type;
            foreach (string name in path)
            {
                StructMember member = Require(current, name, owner);
                offset += member.Offset;
                current = member.Type;
            }

            return new Slot(offset, current);
        }
    }

    private sealed class StatusBlock
    {
        private readonly Slot _responseId;
        private readonly Slot _stateCurrent;
        private readonly Slot _statePrevious;
        private readonly Slot _modeCurrent;
        private readonly Slot _modePrevious;
        private readonly Slot _commandAccepted;
        private readonly Slot _commandRejected;
        private readonly Slot _stateError;
        private readonly Slot _modeChanged;
        private readonly Slot _modeRejected;
        private readonly Slot _modeError;
        private readonly int _length;
        private byte[] _last = [];

        public StatusBlock(SymbolRef status)
        {
            string owner = status.Path;
            StructMember responseId = Require(status.Type, "ResponseId", owner);
            StructMember packMLOut = Require(status.Type, "PackMLOut", owner);
            RequireType(packMLOut, "ST_PML_Base_Out", owner);
            if (packMLOut.Offset != responseId.Offset + responseId.Type.Size)
                throw new ServiceHostException($"{owner}: PackMLOut does not directly follow ResponseId.");

            Offset = status.Offset + responseId.Offset;
            _length = responseId.Type.Size + packMLOut.Type.Size;
            int origin = packMLOut.Offset - responseId.Offset;
            _responseId = new Slot(0, responseId.Type);
            _stateCurrent = Out(packMLOut, origin, owner, "eStateCurrent");
            _statePrevious = Out(packMLOut, origin, owner, "eStatePrevious");
            _modeCurrent = Out(packMLOut, origin, owner, "eModeCurrent");
            _modePrevious = Out(packMLOut, origin, owner, "eModePrevious");
            _commandAccepted = Out(packMLOut, origin, owner, "bCommandAccepted");
            _commandRejected = Out(packMLOut, origin, owner, "bCommandRejected");
            _stateError = Out(packMLOut, origin, owner, "eStateErrorID");
            _modeChanged = Out(packMLOut, origin, owner, "bModeChanged");
            _modeRejected = Out(packMLOut, origin, owner, "bModeChangeRejected");
            _modeError = Out(packMLOut, origin, owner, "eModeErrorID");
        }

        public int Offset { get; }

        public long ResponseId { get; set; }

        public void Reset(ProcessImage image, PackMLMachine machine)
        {
            ResponseId = 0;
            _last = Build(machine);
            image.Write(Offset, _last, WriteSource.Workflow);
        }

        public void Write(ProcessImage image, PackMLMachine machine)
        {
            byte[] block = Build(machine);
            if (block.AsSpan().SequenceEqual(_last))
                return;

            _last = block;
            image.Write(Offset, block, WriteSource.Workflow);
        }

        private static Slot Out(StructMember packMLOut, int origin, string owner, string name)
        {
            StructMember member = Require(packMLOut.Type, name, owner + ".PackMLOut");
            return new Slot(origin + member.Offset, member.Type);
        }

        private byte[] Build(PackMLMachine machine)
        {
            var block = new byte[_length];
            _responseId.Write(block, ResponseId);
            _stateCurrent.Write(block, (long)machine.State);
            _statePrevious.Write(block, (long)machine.PreviousState);
            _modeCurrent.Write(block, machine.Mode);
            _modePrevious.Write(block, machine.PreviousMode);
            _commandAccepted.Write(block, machine.CommandAccepted);
            _commandRejected.Write(block, machine.CommandRejected);
            _stateError.Write(block, (long)machine.StateErrorId);
            _modeChanged.Write(block, machine.ModeChanged);
            _modeRejected.Write(block, machine.ModeChangeRejected);
            _modeError.Write(block, (long)machine.ModeErrorId);
            return block;
        }
    }

    private sealed class ModuleUnit
    {
        private readonly InputLayout _input;
        private readonly int _ctrlOffset;
        private readonly int _serviceCtrlOffset;
        private readonly byte[] _ctrlBuffer;
        private readonly byte[] _serviceCtrlBuffer;
        private readonly ModuleAlarmEngine _alarms;

        private ModuleUnit(
            InputLayout input,
            int ctrlOffset,
            int serviceCtrlOffset,
            int serviceCtrlSize,
            StatusBlock status,
            List<ServiceUnit> services,
            string path,
            ModuleAlarmBlock? alarm,
            ModuleHeartbeat? heartbeat)
        {
            Path = path;
            Alarm = alarm;
            Heartbeat = heartbeat;
            _input = input;
            _ctrlOffset = ctrlOffset;
            _serviceCtrlOffset = serviceCtrlOffset;
            _ctrlBuffer = new byte[input.Size];
            _serviceCtrlBuffer = new byte[serviceCtrlSize];
            Status = status;
            Services = services;
            BoundServices = services.Where(service => service.IsBound).ToList();
            _alarms = new ModuleAlarmEngine(
                path, alarm, services.Select(service => service.Errors).ToList(), BoundServices.Select(service => service.Errors).ToList());
            Machine = new PackMLMachine(PackMLState.Execute, PackMLState.Starting);
        }

        public string Path { get; }

        public ModuleAlarmBlock? Alarm { get; }

        public ModuleHeartbeat? Heartbeat { get; }

        public StatusBlock Status { get; }

        public IReadOnlyList<ServiceUnit> Services { get; }

        public IReadOnlyList<ServiceUnit> BoundServices { get; }

        public PackMLMachine Machine { get; set; }

        public ModuleRules? Rules { get; set; }

        public bool LastCommandRequest { get; set; }

        public bool LastModeRequest { get; set; }

        public static ModuleUnit Create(SymbolTable symbols, SymbolRef module, ServiceIdentityTable identities, AliveSettings alive)
        {
            StructMember ctrl = RequireMember(module, "ModuleCtrl", "ST_ModuleCtrl");
            StructMember status = RequireMember(module, "ModuleStatus", "ST_ModuleStatus");
            StructMember serviceCtrl = RequireMember(module, "ServiceCtrl", null);
            StructMember serviceStatus = RequireMember(module, "ServiceStatus", null);

            var serviceCtrlRoot = new SymbolRef($"{module.Path}.ServiceCtrl", serviceCtrl.Type, module.Offset + serviceCtrl.Offset);
            // Stryker disable once Arithmetic: ServiceUnit.Create reads only the Path of this root; the status offset comes from the symbol table
            var serviceStatusRoot = new SymbolRef($"{module.Path}.ServiceStatus", serviceStatus.Type, module.Offset + serviceStatus.Offset);
            SymbolRef? serviceParamRoot = MemberRef(module, "ServiceParam");
            SymbolRef? configParamList = MemberRef(module, "ModuleConfigParamList");
            var services = FindServiceCtrls(serviceCtrlRoot)
                .Select(ctrlNode => ServiceUnit.Create(symbols, ctrlNode, serviceCtrlRoot, serviceStatusRoot, identities, serviceParamRoot, configParamList))
                .ToList();

            return new ModuleUnit(
                new InputLayout(ctrl.Type, $"{module.Path}.ModuleCtrl"),
                module.Offset + ctrl.Offset,
                serviceCtrlRoot.Offset,
                serviceCtrl.Type.Size,
                new StatusBlock(new SymbolRef($"{module.Path}.ModuleStatus", status.Type, module.Offset + status.Offset)),
                services,
                module.Path,
                ModuleAlarmBlock.TryCreate(module),
                ModuleHeartbeat.TryCreate(module, alive.HeartbeatTimeout));
        }

        public void ReadInputs(ProcessImage image)
        {
            image.Read(_ctrlOffset, _ctrlBuffer);
            image.Read(_serviceCtrlOffset, _serviceCtrlBuffer);
        }

        public RawInput ReadModuleInput() => _input.Decode(_ctrlBuffer);

        public void ResetAlarms(ProcessImage image, List<ConfiguredBinding> configured) => _alarms.Reset(image, configured);

        public void SetHook(HookSubmission submission, bool active) => _alarms.SetHook(submission, active);

        public void ScanAlarms(ProcessImage image, string utc) => _alarms.Scan(image, utc);

        public void ResetHeartbeat(ProcessImage image) => Heartbeat?.Reset(image);

        public void ScanHeartbeat(ProcessImage image, TimeSpan now) => Heartbeat?.Scan(image, now);

        public RawInput ReadServiceInput(ServiceUnit service) => service.ReadInput(_serviceCtrlBuffer);

        public AliveInput ReadServiceAlive(ServiceUnit service, bool moduleInExecute) => service.ReadAlive(_serviceCtrlBuffer, moduleInExecute);

        private static SymbolRef? MemberRef(SymbolRef module, string name) =>
            Find(module.Type, name) is { } member
                ? new SymbolRef($"{module.Path}.{name}", member.Type, module.Offset + member.Offset)
                : null;

        private static StructMember RequireMember(SymbolRef module, string name, string? typeName)
        {
            StructMember? member = Find(module.Type, name);
            if (member is null)
                throw new ServiceHostException($"Module {module.Path} has no member '{name}'.");
            if (typeName is not null)
                RequireType(member, typeName, module.Path);
            return member;
        }

        private static IEnumerable<SymbolRef> FindServiceCtrls(SymbolRef node)
        {
            if (node.Type.Unaliased.Name == "ST_ServiceCtrl")
                return [node];
            return ChildrenOf(node).SelectMany(FindServiceCtrls);
        }

        private static IEnumerable<SymbolRef> ChildrenOf(SymbolRef node) => node.Type.Unaliased switch
        {
            StructType structure => structure.Members.Select(member =>
                new SymbolRef($"{node.Path}.{member.Name}", member.Type, node.Offset + member.Offset)),
            ArrayType array => ElementsOf(node, array),
            _ => [],
        };

        private static IEnumerable<SymbolRef> ElementsOf(SymbolRef node, ArrayType array)
        {
            if (array.Dimensions.Count != 1 || array.ElementType.Unaliased is not (StructType or ArrayType))
                return [];

            ArrayDimension bounds = array.Dimensions[0];
            return Enumerable.Range(bounds.LowerBound, bounds.Length).Select(index =>
                new SymbolRef($"{node.Path}[{index}]", array.ElementType, node.Offset + array.OffsetOf([index])));
        }
    }
}
