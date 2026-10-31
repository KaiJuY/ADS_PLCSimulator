using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.PackML.Behavior;
using AdsPlcSimulator.Core.PackML.ConfigParam;
using AdsPlcSimulator.Core.Plc;
using static AdsPlcSimulator.Core.PackML.PlcLayout;

namespace AdsPlcSimulator.Core.PackML;

public sealed partial class ServiceHost
{
    private sealed class ServiceUnit
    {
        private readonly InputLayout _input;
        private readonly SymbolRef _serviceIdSymbol;
        private readonly SymbolRef _behaviorSymbol;
        private readonly int _ctrlOffsetInBlock;

        private ServiceUnit(
            string ctrlPath,
            string relativePath,
            ServiceIdentity? identity,
            InputLayout input,
            int ctrlOffsetInBlock,
            StatusBlock status,
            SymbolRef serviceIdSymbol,
            SymbolRef behaviorSymbol,
            string statusPath,
            ServiceErrorList? errors,
            ServiceAliveMonitor? alive,
            ConfigParamService? configParams)
        {
            ConfigParams = configParams;
            Alive = alive;
            StatusPath = statusPath;
            Errors = errors;
            CtrlPath = ctrlPath;
            RelativePath = relativePath;
            Identity = identity;
            _input = input;
            _ctrlOffsetInBlock = ctrlOffsetInBlock;
            Status = status;
            _serviceIdSymbol = serviceIdSymbol;
            _behaviorSymbol = behaviorSymbol;
            Machine = new PackMLMachine(PackMLState.Idle, PackMLState.Resetting);
        }

        public string CtrlPath { get; }

        public string RelativePath { get; }

        public string StatusPath { get; }

        public ServiceErrorList? Errors { get; }

        public ServiceAliveMonitor? Alive { get; }

        public ConfigParamService? ConfigParams { get; }

        public bool StartingChecked { get; set; }

        public bool StartingAbortPending { get; set; }

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

        public ServiceRules Rules { get; set; } = ServiceRules.None;

        public bool ExecuteRejected { get; set; }

        public bool EntryDryRun { get; set; }

        public TimeSpan? EntryDuration { get; set; }

        public bool ExecuteAbortPending { get; set; }

        public static ServiceUnit Create(
            SymbolTable symbols, SymbolRef ctrl, SymbolRef serviceCtrlRoot, SymbolRef serviceStatusRoot, ServiceIdentityTable identities,
            SymbolRef? serviceParamRoot, SymbolRef? configParamList)
        {
            string relative = ctrl.Path[(serviceCtrlRoot.Path.Length + 1)..];
            if (!symbols.TryResolve($"{serviceStatusRoot.Path}.{relative}", out SymbolRef status, out _) || status.Type.Unaliased.Name != "ST_ServiceStatus")
                throw new ServiceHostException($"{serviceStatusRoot.Path} has no ST_ServiceStatus at '{relative}', the match of {ctrl.Path}.");

            StructMember serviceId = Require(status.Type, "ServiceId", status.Path);
            StructMember behavior = Require(status.Type, "CompletionBehavior", status.Path);
            return new ServiceUnit(
                ctrl.Path,
                relative,
                identities.Find(relative),
                new InputLayout(ctrl.Type, ctrl.Path),
                ctrl.Offset - serviceCtrlRoot.Offset,
                new StatusBlock(status),
                // Stryker disable String: ProcessImage.WriteValue uses only the offset and type of these two symbols, never the path
                new SymbolRef($"{status.Path}.ServiceId", serviceId.Type, status.Offset + serviceId.Offset),
                new SymbolRef($"{status.Path}.CompletionBehavior", behavior.Type, status.Offset + behavior.Offset),
                status.Path,
                ServiceErrorList.TryCreate(status),
                ServiceAliveMonitor.TryCreate(ctrl, ctrl.Offset - serviceCtrlRoot.Offset),
                TryCreateConfigParams(symbols, relative, serviceParamRoot, configParamList));
            // Stryker restore String
        }

        private static ConfigParamService? TryCreateConfigParams(SymbolTable symbols, string relative, SymbolRef? serviceParamRoot, SymbolRef? configParamList)
        {
            if (serviceParamRoot is not { } root || configParamList is not { } list)
                return null;
            if (!symbols.TryResolve($"{root.Path}.{relative}", out SymbolRef param, out _))
                return null;

            return ConfigParamService.TryCreate(param, list);
        }

        public void ResetStarting()
        {
            StartingChecked = false;
            // Stryker disable once Boolean: the flag is read only after StartingChecked was set in the same stay, and the check that sets StartingChecked also sets this flag on failure or leaves Starting on success
            StartingAbortPending = false;
        }

        public void BeginExecuteEntry()
        {
            ExecuteRejected = false;
            // Stryker disable once Boolean: every accepted entry sets EntryDryRun before it is read, and a refused entry never reads it
            EntryDryRun = false;
            EntryDuration = null;
            ExecuteAbortPending = false;
        }

        public RawInput ReadInput(byte[] serviceCtrlBlock) =>
            _input.Decode(serviceCtrlBlock.AsSpan(_ctrlOffsetInBlock, _input.Size));

        public AliveInput ReadAlive(byte[] serviceCtrlBlock, bool moduleInExecute) =>
            Alive?.Read(serviceCtrlBlock, moduleInExecute) ?? default;

        public bool AbortPending => Alive is { AbortPending: true };

        public void ClearPendingAbort() => Alive?.ClearPendingAbort();

        public TimeSpan? EvaluateAlive(AliveInput input, TimeSpan now) =>
            Alive?.Evaluate(input, Machine.State == PackMLState.Execute, now);

        public void OnScanned(PackMLState before, AliveInput input, AliveSettings settings, TimeSpan now)
        {
            if (Machine.State != PackMLState.Starting)
                ResetStarting();
            if (Alive is null)
                return;
            if (Machine.State != PackMLState.Execute)
                // Stryker disable once Statement: the monitor only acts in Execute, and every entry into Execute calls EnterExecute, which sets _active again
                Alive.LeaveExecute();
            else if (before != PackMLState.Execute)
                Alive.EnterExecute(input, Behavior, settings, now);
        }

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
            Alive?.Reset();
            ResetStarting();
            BeginExecuteEntry();
            Generation = 0;
            EntryToken = null;
            // Stryker disable once Boolean: EntryToken is null here, so Complete and TakeCompletion cannot read the flag before EnterExecute resets it
            CompletePending = false;
        }

        public void ClearErrors(ProcessImage image)
        {
            if (Errors is null)
                return;

            Errors.Clear();
            Errors.WriteIfChanged(image);
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
}
