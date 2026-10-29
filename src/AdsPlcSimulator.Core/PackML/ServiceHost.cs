using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML;

/// <summary>All modules below one root, scanned together like one PLC task.</summary>
public sealed class ServiceHost
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

    private static long s_generationCounter;

    private readonly Lock _lock = new();
    private readonly List<ModuleUnit> _modules = [];
    private readonly Dictionary<string, ServiceUnit> _bound = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _boundPaths = [];
    private readonly TimeSpan _defaultDelay;
    private readonly Func<string, object?> _overrideToken;
    private bool _retired;

    /// <exception cref="ServiceHostException">The root does not have the module shape, or no service is bound.</exception>
    public ServiceHost(SymbolTable symbols, SymbolRef root, ServiceIdentityTable identities, TimeSpan defaultDelay, Func<string, object?> overrideToken)
    {
        RootPath = root.Path;
        _defaultDelay = defaultDelay;
        _overrideToken = overrideToken;

        foreach (SymbolRef module in FindModules(root))
            _modules.Add(ModuleUnit.Create(symbols, module, identities));

        foreach (ServiceUnit service in _modules.SelectMany(module => module.BoundServices))
        {
            _bound[service.CtrlPath] = service;
            _boundPaths.Add(service.CtrlPath);
        }

        if (_bound.Count == 0)
            throw new ServiceHostException($"No service under {root.Path} has a ServiceId.");
    }

    public string RootPath { get; }

    public IReadOnlyList<string> BoundServices => _boundPaths;

    public void Initialize(ProcessImage image)
    {
        lock (_lock)
        {
            if (_retired)
                return;
            foreach (ModuleUnit module in _modules)
                InitializeModule(image, module);
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
            foreach (ModuleUnit module in _modules)
                ScanModule(image, module, now);
        }
    }

    /// <summary>Stops all later scans and writes; takes the host lock, so a running scan finishes first.</summary>
    public void Retire()
    {
        lock (_lock)
            _retired = true;
    }

    /// <summary>Id of the service's current Execute entry, taken from one process-wide counter, so it never repeats across hosts or reloads; 0 before the first entry.</summary>
    public long ExecuteGeneration(string ctrlPath)
    {
        lock (_lock)
            return Bound(ctrlPath).Generation;
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

    private static void InitializeModule(ProcessImage image, ModuleUnit module)
    {
        module.ReadInputs(image);
        RawInput input = module.ReadModuleInput();
        module.Machine = new PackMLMachine(PackMLState.Execute, PackMLState.Starting);
        module.Status.Reset(image, module.Machine);
        module.Machine.SeedInput(input.PackML);
        module.LastCommandRequest = input.PackML.CommandRequest;
        module.LastModeRequest = input.PackML.ModeRequest;

        foreach (ServiceUnit service in module.Services)
            service.WriteIdentity(image);
        foreach (ServiceUnit service in module.BoundServices)
            service.Reset(image, module.ReadServiceInput(service));
    }

    private void ScanModule(ProcessImage image, ModuleUnit module, TimeSpan now)
    {
        module.ReadInputs(image);
        RawInput moduleInput = module.ReadModuleInput();
        module.Machine.Scan(moduleInput.PackML, state => IsModuleActingStateDone(module, state));
        bool moduleInExecute = module.Machine.State == PackMLState.Execute;

        foreach (ServiceUnit service in module.BoundServices)
            ScanService(image, service, module.ReadServiceInput(service), moduleInput, moduleInExecute, now);

        UpdateModuleResponse(module, moduleInput.PackML);
        module.Status.Write(image, module.Machine);
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

    private void ScanService(ProcessImage image, ServiceUnit service, RawInput client, RawInput module, bool moduleInExecute, TimeSpan now)
    {
        (long requestId, PackMLInput effective) = moduleInExecute
            ? (client.RequestId, ApplyBlocking(service, client.PackML))
            : (module.RequestId, ForwardModuleInput(service, client.PackML, module.PackML));

        if (effective.CommandRequest && !service.LastCommandRequest)
            service.Status.ResponseId = requestId;
        service.LastCommandRequest = effective.CommandRequest;
        service.LastModeRequest = effective.ModeRequest;

        PackMLState before = service.Machine.State;
        service.Machine.Scan(effective, state => IsServiceActingStateDone(service, state, now));
        service.LastState = service.Machine.State;
        if (before != PackMLState.Execute && service.Machine.State == PackMLState.Execute)
            EnterExecute(service, now);

        service.Status.Write(image, service.Machine);
    }

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

    private bool IsServiceActingStateDone(ServiceUnit service, PackMLState state, TimeSpan now)
    {
        if (state != PackMLState.Execute)
            return true;
        if (service.IsOverridden(_overrideToken))
            return service.TakeCompletion();
        if (service.Behavior != CompletionBehavior.NaturalCompletion)
            return false;
        return now - service.ExecuteEntered >= _defaultDelay;
    }

    private void EnterExecute(ServiceUnit service, TimeSpan now)
    {
        service.ExecuteEntered = now;
        service.Generation = Interlocked.Increment(ref s_generationCounter);
        service.EntryToken = _overrideToken(service.CtrlPath);
        service.CompletePending = false;
    }

    private readonly record struct RawInput(long RequestId, PackMLInput PackML);

    private readonly record struct Slot(int Offset, PlcType Type)
    {
        public object Read(ReadOnlySpan<byte> block) => ValueCodec.Decode(Type, block.Slice(Offset, Type.Size));

        public void Write(Span<byte> block, object? value) => ValueCodec.Encode(Type, value, block.Slice(Offset, Type.Size));
    }

    private static StructMember Require(PlcType type, string name, string owner) =>
        (type.Unaliased as StructType)?.Find(name)
            ?? throw new ServiceHostException($"{owner} has no member '{name}'.");

    private static void RequireType(StructMember member, string typeName, string owner)
    {
        if (member.Type.Unaliased.Name != typeName)
            throw new ServiceHostException($"{owner}.{member.Name} is {member.Type.Name}, not {typeName}.");
    }

    /// <summary>Where RequestId and the four PackMLIn fields sit inside an ST_ModuleCtrl or ST_ServiceCtrl.</summary>
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

    /// <summary>
    /// The adjacent ResponseId and PackMLOut of one ST_ServiceStatus or ST_ModuleStatus, written as one block,
    /// so a client that polls with Read never sees half an update.
    /// </summary>
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

        public StatusBlock(SymbolRef status, string owner)
        {
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

        /// <summary>Writes the block and takes it as the cache, whatever the image holds now.</summary>
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

    private sealed class ServiceUnit
    {
        private readonly InputLayout _input;
        private readonly SymbolRef _serviceIdSymbol;
        private readonly SymbolRef _behaviorSymbol;
        private readonly int _ctrlOffsetInBlock;

        private ServiceUnit(
            string ctrlPath,
            ServiceIdentity? identity,
            InputLayout input,
            int ctrlOffsetInBlock,
            StatusBlock status,
            SymbolRef serviceIdSymbol,
            SymbolRef behaviorSymbol)
        {
            CtrlPath = ctrlPath;
            Identity = identity;
            _input = input;
            _ctrlOffsetInBlock = ctrlOffsetInBlock;
            Status = status;
            _serviceIdSymbol = serviceIdSymbol;
            _behaviorSymbol = behaviorSymbol;
            Machine = new PackMLMachine(PackMLState.Idle, PackMLState.Resetting);
        }

        public string CtrlPath { get; }

        public ServiceIdentity? Identity { get; }

        public bool IsBound => Identity is { ServiceId: not 0 };

        public CompletionBehavior Behavior => Identity?.CompletionBehavior ?? CompletionBehavior.Unknown;

        public StatusBlock Status { get; }

        public PackMLMachine Machine { get; set; }

        public PackMLState LastState { get; set; } = PackMLState.Idle;

        public bool LastCommandRequest { get; set; }

        public bool LastModeRequest { get; set; }

        public bool CommandBlocked { get; set; }

        public bool ModeBlocked { get; set; }

        public TimeSpan ExecuteEntered { get; set; }

        public long Generation { get; set; }

        public object? EntryToken { get; set; }

        public bool CompletePending { get; set; }

        public static ServiceUnit Create(SymbolTable symbols, SymbolRef ctrl, SymbolRef serviceCtrlRoot, SymbolRef serviceStatusRoot, ServiceIdentityTable identities)
        {
            string relative = ctrl.Path[(serviceCtrlRoot.Path.Length + 1)..];
            if (!symbols.TryResolve($"{serviceStatusRoot.Path}.{relative}", out SymbolRef status, out _) || status.Type.Unaliased.Name != "ST_ServiceStatus")
                throw new ServiceHostException($"{serviceStatusRoot.Path} has no ST_ServiceStatus at '{relative}', the match of {ctrl.Path}.");

            StructMember serviceId = Require(status.Type, "ServiceId", status.Path);
            StructMember behavior = Require(status.Type, "CompletionBehavior", status.Path);
            return new ServiceUnit(
                ctrl.Path,
                identities.Find(relative),
                new InputLayout(ctrl.Type, ctrl.Path),
                ctrl.Offset - serviceCtrlRoot.Offset,
                new StatusBlock(status, status.Path),
                new SymbolRef($"{status.Path}.ServiceId", serviceId.Type, status.Offset + serviceId.Offset),
                new SymbolRef($"{status.Path}.CompletionBehavior", behavior.Type, status.Offset + behavior.Offset));
        }

        public RawInput ReadInput(byte[] serviceCtrlBlock) =>
            _input.Decode(serviceCtrlBlock.AsSpan(_ctrlOffsetInBlock, _input.Size));

        public void WriteIdentity(ProcessImage image)
        {
            if (Identity is null)
                return;

            image.WriteValue(_serviceIdSymbol, (long)Identity.ServiceId, WriteSource.Workflow);
            image.WriteValue(_behaviorSymbol, (long)Identity.CompletionBehavior, WriteSource.Workflow);
        }

        public void Reset(ProcessImage image, RawInput client)
        {
            Machine = new PackMLMachine(PackMLState.Idle, PackMLState.Resetting);
            Status.Reset(image, Machine);
            Machine.SeedInput(client.PackML);
            LastState = PackMLState.Idle;
            LastCommandRequest = client.PackML.CommandRequest;
            LastModeRequest = client.PackML.ModeRequest;
            CommandBlocked = false;
            ModeBlocked = false;
            Generation = 0;
            EntryToken = null;
            CompletePending = false;
        }

        public bool IsOverridden(Func<string, object?> overrideToken) =>
            EntryToken is not null && Equals(EntryToken, overrideToken(CtrlPath));

        public bool TakeCompletion()
        {
            bool pending = CompletePending;
            CompletePending = false;
            return pending;
        }
    }

    private sealed class ModuleUnit
    {
        private readonly InputLayout _input;
        private readonly int _ctrlOffset;
        private readonly int _serviceCtrlOffset;
        private readonly byte[] _ctrlBuffer;
        private readonly byte[] _serviceCtrlBuffer;

        private ModuleUnit(
            InputLayout input,
            int ctrlOffset,
            int serviceCtrlOffset,
            int serviceCtrlSize,
            StatusBlock status,
            List<ServiceUnit> services)
        {
            _input = input;
            _ctrlOffset = ctrlOffset;
            _serviceCtrlOffset = serviceCtrlOffset;
            _ctrlBuffer = new byte[input.Size];
            _serviceCtrlBuffer = new byte[serviceCtrlSize];
            Status = status;
            Services = services;
            BoundServices = services.Where(service => service.IsBound).ToList();
            Machine = new PackMLMachine(PackMLState.Execute, PackMLState.Starting);
        }

        public StatusBlock Status { get; }

        public IReadOnlyList<ServiceUnit> Services { get; }

        public IReadOnlyList<ServiceUnit> BoundServices { get; }

        public PackMLMachine Machine { get; set; }

        public bool LastCommandRequest { get; set; }

        public bool LastModeRequest { get; set; }

        public static ModuleUnit Create(SymbolTable symbols, SymbolRef module, ServiceIdentityTable identities)
        {
            StructMember ctrl = RequireMember(module, "ModuleCtrl", "ST_ModuleCtrl");
            StructMember status = RequireMember(module, "ModuleStatus", "ST_ModuleStatus");
            StructMember serviceCtrl = RequireMember(module, "ServiceCtrl", null);
            StructMember serviceStatus = RequireMember(module, "ServiceStatus", null);

            var serviceCtrlRoot = new SymbolRef($"{module.Path}.ServiceCtrl", serviceCtrl.Type, module.Offset + serviceCtrl.Offset);
            var serviceStatusRoot = new SymbolRef($"{module.Path}.ServiceStatus", serviceStatus.Type, module.Offset + serviceStatus.Offset);
            var services = FindServiceCtrls(serviceCtrlRoot)
                .Select(ctrlNode => ServiceUnit.Create(symbols, ctrlNode, serviceCtrlRoot, serviceStatusRoot, identities))
                .ToList();

            return new ModuleUnit(
                new InputLayout(ctrl.Type, $"{module.Path}.ModuleCtrl"),
                module.Offset + ctrl.Offset,
                serviceCtrlRoot.Offset,
                serviceCtrl.Type.Size,
                new StatusBlock(new SymbolRef($"{module.Path}.ModuleStatus", status.Type, module.Offset + status.Offset), $"{module.Path}.ModuleStatus"),
                services);
        }

        public void ReadInputs(ProcessImage image)
        {
            image.Read(_ctrlOffset, _ctrlBuffer);
            image.Read(_serviceCtrlOffset, _serviceCtrlBuffer);
        }

        public RawInput ReadModuleInput() => _input.Decode(_ctrlBuffer);

        public RawInput ReadServiceInput(ServiceUnit service) => service.ReadInput(_serviceCtrlBuffer);

        private static StructMember RequireMember(SymbolRef module, string name, string? typeName)
        {
            StructMember? member = (module.Type.Unaliased as StructType)?.Find(name);
            if (member is null)
                throw new ServiceHostException($"Module {module.Path} has no member '{name}'.");
            if (typeName is not null)
                RequireType(member, typeName, module.Path);
            return member;
        }

        private static IEnumerable<SymbolRef> FindServiceCtrls(SymbolRef node)
        {
            PlcType type = node.Type.Unaliased;
            if (type.Name == "ST_ServiceCtrl")
                return [node];
            if (type is StructType structure)
                return structure.Members.SelectMany(member => FindServiceCtrls(
                    new SymbolRef($"{node.Path}.{member.Name}", member.Type, node.Offset + member.Offset)));
            if (type is not ArrayType { Dimensions.Count: 1 } array || array.ElementType.Unaliased is not (StructType or ArrayType))
                return [];

            ArrayDimension bounds = array.Dimensions[0];
            return Enumerable.Range(bounds.LowerBound, bounds.Length).SelectMany(index => FindServiceCtrls(
                new SymbolRef($"{node.Path}[{index}]", array.ElementType, node.Offset + array.OffsetOf([index]))));
        }
    }
}
