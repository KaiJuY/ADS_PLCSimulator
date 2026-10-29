namespace AdsPlcSimulator.Core.PackML;

/// <summary>One FB_PackMLBase instance: command and mode handling plus the state machine, one scan at a time.</summary>
/// <remarks>Mode and PreviousMode start at 0 (Unknown); every flag starts FALSE; error ids start at NoError.</remarks>
public sealed class PackMLMachine(PackMLState initialState, PackMLState initialPrevious)
{
    private const long MaxSupportedMode = 2;

    private sealed record CommandRule(Func<PackMLState, bool> IsAllowedFrom, PackMLState Target, PackMLErrorId Rejection);

    private static readonly IReadOnlyDictionary<PackMLCommand, CommandRule> CommandRules = new Dictionary<PackMLCommand, CommandRule>
    {
        [PackMLCommand.Clear] = Rule([PackMLState.Aborted], PackMLState.Clearing, PackMLErrorId.ClearNotAllowed),
        [PackMLCommand.Reset] = Rule([PackMLState.Stopped, PackMLState.Complete], PackMLState.Resetting, PackMLErrorId.ResetNotAllowed),
        [PackMLCommand.Start] = Rule([PackMLState.Idle], PackMLState.Starting, PackMLErrorId.StartNotAllowed),
        [PackMLCommand.Stop] = Rule(
            [
                PackMLState.Starting, PackMLState.Idle, PackMLState.Execute, PackMLState.Completing, PackMLState.Complete,
                PackMLState.Holding, PackMLState.Held, PackMLState.Unholding, PackMLState.Suspending, PackMLState.Suspended,
                PackMLState.Unsuspending,
            ],
            PackMLState.Stopping,
            PackMLErrorId.StopNotAllowed),
        [PackMLCommand.Hold] = Rule([PackMLState.Execute], PackMLState.Holding, PackMLErrorId.HoldNotAllowed),
        [PackMLCommand.Unhold] = Rule([PackMLState.Held], PackMLState.Unholding, PackMLErrorId.UnholdNotAllowed),
        [PackMLCommand.Suspend] = Rule([PackMLState.Execute], PackMLState.Suspending, PackMLErrorId.SuspendNotAllowed),
        [PackMLCommand.Unsuspend] = Rule([PackMLState.Suspended], PackMLState.Unsuspending, PackMLErrorId.UnsuspendNotAllowed),
        [PackMLCommand.Abort] = new CommandRule(
            state => state is not (PackMLState.Aborting or PackMLState.Aborted),
            PackMLState.Aborting,
            PackMLErrorId.AbortNotAllowed),
    };

    private static readonly IReadOnlyDictionary<PackMLState, PackMLState> ActingStateTargets = new Dictionary<PackMLState, PackMLState>
    {
        [PackMLState.Undefined] = PackMLState.Aborted,
        [PackMLState.Clearing] = PackMLState.Stopped,
        [PackMLState.Starting] = PackMLState.Execute,
        [PackMLState.Execute] = PackMLState.Completing,
        [PackMLState.Completing] = PackMLState.Complete,
        [PackMLState.Resetting] = PackMLState.Idle,
        [PackMLState.Stopping] = PackMLState.Stopped,
        [PackMLState.Aborting] = PackMLState.Aborted,
        [PackMLState.Holding] = PackMLState.Held,
        [PackMLState.Unholding] = PackMLState.Execute,
        [PackMLState.Suspending] = PackMLState.Suspended,
        [PackMLState.Unsuspending] = PackMLState.Execute,
    };

    private static readonly PackMLState[] ModeChangeStates = [PackMLState.Aborted, PackMLState.Stopped, PackMLState.Idle, PackMLState.Complete];

    private PackMLCommand? _queuedCommand;
    private bool _commandConsumed;
    private bool _modeConsumed;

    public PackMLState State { get; private set; } = initialState;

    public PackMLState PreviousState { get; private set; } = initialPrevious;

    public long Mode { get; private set; }

    public long PreviousMode { get; private set; }

    public bool CommandAccepted { get; private set; }

    public bool CommandRejected { get; private set; }

    public PackMLErrorId StateErrorId { get; private set; }

    public bool ModeChanged { get; private set; }

    public bool ModeChangeRejected { get; private set; }

    public PackMLErrorId ModeErrorId { get; private set; }

    /// <summary>Queues an internal command (FB_PackMLBase.RequestCommand). False when one is already queued or a mode request is high.</summary>
    public bool RequestCommand(PackMLCommand command, bool modeRequestHigh)
    {
        if (_queuedCommand is not null || modeRequestHigh)
            return false;

        _queuedCommand = command;
        return true;
    }

    /// <param name="isActingStateDone">Asked once per scan for the current acting state; true ends it.</param>
    public void Scan(PackMLInput input, Func<PackMLState, bool> isActingStateDone)
    {
        ClearLatches(input);
        ProcessModeRequest(input);
        Dispatch(SelectCommand(input));
        RunStateHook(isActingStateDone);
    }

    /// <summary>Treats requests that are already high as consumed, so they do not run after a (re)start.</summary>
    public void SeedInput(PackMLInput input)
    {
        _commandConsumed = input.CommandRequest;
        _modeConsumed = input.ModeRequest;
    }

    private static CommandRule Rule(PackMLState[] allowedFrom, PackMLState target, PackMLErrorId rejection) =>
        new(state => allowedFrom.Contains(state), target, rejection);

    private void ClearLatches(PackMLInput input)
    {
        if (!input.CommandRequest)
        {
            CommandAccepted = false;
            CommandRejected = false;
            StateErrorId = PackMLErrorId.NoError;
            _commandConsumed = false;
        }

        if (!input.ModeRequest)
        {
            ModeChanged = false;
            ModeChangeRejected = false;
            ModeErrorId = PackMLErrorId.NoError;
            _modeConsumed = false;
        }
    }

    private void ProcessModeRequest(PackMLInput input)
    {
        if (!input.ModeRequest || _modeConsumed)
            return;

        _modeConsumed = true;
        PackMLErrorId error = CheckModeChange(input.Mode);
        if (error != PackMLErrorId.NoError)
        {
            ModeChangeRejected = true;
            ModeErrorId = error;
            return;
        }

        PreviousMode = Mode;
        Mode = input.Mode;
        ModeChanged = true;
    }

    private PackMLErrorId CheckModeChange(long requestedMode)
    {
        if (requestedMode is < 0 or > MaxSupportedMode)
            return PackMLErrorId.ModeNotSupported;
        if (requestedMode == Mode)
            return PackMLErrorId.ModeAlreadyActive;
        return ModeChangeStates.Contains(State) ? PackMLErrorId.NoError : PackMLErrorId.ModeChangeNotAllowedFromCurrentState;
    }

    private long SelectCommand(PackMLInput input)
    {
        if (_queuedCommand is { } queued)
        {
            _queuedCommand = null;
            return (long)queued;
        }

        if (!input.CommandRequest || input.Command == (long)PackMLCommand.None || _commandConsumed)
            return (long)PackMLCommand.None;

        _commandConsumed = true;
        return input.Command;
    }

    private void Dispatch(long command)
    {
        if (command < (long)PackMLCommand.Reset || command > (long)PackMLCommand.Clear)
            return;

        CommandRule rule = CommandRules[(PackMLCommand)command];
        if (!rule.IsAllowedFrom(State))
        {
            CommandRejected = true;
            StateErrorId = rule.Rejection;
            return;
        }

        CommandAccepted = true;
        ChangeState(rule.Target);
    }

    private void RunStateHook(Func<PackMLState, bool> isActingStateDone)
    {
        if (!ActingStateTargets.TryGetValue(State, out PackMLState target))
            return;
        if (isActingStateDone(State))
            ChangeState(target);
    }

    private void ChangeState(PackMLState next)
    {
        // Stryker disable once Statement: no command rule and no acting-state target equals its source state, so this guard never fires
        if (next == State)
            return;

        PreviousState = State;
        State = next;
    }
}
