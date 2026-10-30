namespace AdsPlcSimulator.Core.PackML.Alarm;

/// <summary>C# copy of FB_ModuleAlarmManager. Not thread-safe; ServiceHost calls it under its lock.</summary>
public sealed class ModuleAlarmManager
{
    public const int MaxConfiguredAlarmsPerModule = 30;
    public const int MaxHookAlarmsPerModule = 30;
    public const int MaxServiceAlarmsPerModule = 40;

    private readonly int _publishCapacity;
    private readonly Lifecycle[] _configured = NewStates(MaxConfiguredAlarmsPerModule);
    private readonly Lifecycle[] _service = NewStates(MaxServiceAlarmsPerModule);
    private readonly Lifecycle[] _hook = NewStates(MaxHookAlarmsPerModule);

    private uint _moduleId;
    private bool _previousChangeRequest;
    private uint _lastAckRequestId;
    private uint _ackResponseId;
    private bool _ackAccepted;
    private AlarmAckError _ackError;
    private ushort _configuredLatchedCount;
    private ushort _serviceLatchedCount;
    private ushort _hookLatchedCount;
    private bool _overflow;
    private uint _duplicateId;

    /// <param name="publishCapacity">Length of ModuleAlarmList (100 in Polar2).</param>
    public ModuleAlarmManager(int publishCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(publishCapacity);
        _publishCapacity = publishCapacity;
    }

    public AlarmStatus Status => new(
        _ackResponseId, _ackAccepted, _ackError,
        _configuredLatchedCount, _serviceLatchedCount, _overflow,
        _hookLatchedCount, _duplicateId);

    /// <summary>A changed ModuleId clears all three sources and the Ack memory.</summary>
    public void BeginCycle(uint moduleId)
    {
        if (moduleId != _moduleId)
        {
            ClearAll(_configured);
            ClearAll(_service);
            ClearAll(_hook);
            ResetAcknowledgement();
            _moduleId = moduleId;
        }

        _overflow = false;
        _duplicateId = 0;
        foreach (Lifecycle state in _service)
            state.Seen = false;
        foreach (Lifecycle state in _hook)
            state.Seen = false;
    }

    /// <summary>Configuration ownership: a latched hook alarm with this MainErrorId is dropped.</summary>
    public void SuppressHookAlarm(uint mainErrorId)
    {
        Lifecycle? state = Find(_hook, alarm => alarm.MainErrorId == mainErrorId);
        state?.Clear();
    }

    public void ProcessAcknowledgement(AlarmAckRequest request)
    {
        bool risingEdge = request.ChangeRequest && !_previousChangeRequest;
        _previousChangeRequest = request.ChangeRequest;

        if (!request.ChangeRequest)
        {
            _ackAccepted = false;
            _ackError = AlarmAckError.None;
            // Stryker disable once Statement: without ChangeRequest risingEdge is false, so the guard below returns the same way
            return;
        }

        if (!risingEdge)
            return;

        _ackResponseId = request.RequestId;
        _ackError = RunRequest(request);
        _ackAccepted = _ackError == AlarmAckError.None;
    }

    /// <param name="index">1..30, the position in the module's alarmList (1-based).</param>
    public void UpdateConfiguredAlarm(int index, bool sampleValid, bool conditionMet, ExtAlarm snapshot, string utcDateTime)
    {
        if (index < 1 || index > MaxConfiguredAlarmsPerModule || !sampleValid)
            return;

        Lifecycle state = _configured[index - 1];
        if (conditionMet)
            RaiseConfigured(state, snapshot, utcDateTime);
        else
            ClearConfigured(state);

        state.PreviousActive = conditionMet;
    }

    public bool ObserveHookAlarm(ExtAlarm alarm)
    {
        if (alarm.ServiceId != 0 || alarm.MainErrorId == 0)
            return false;

        (Lifecycle? match, Lifecycle? firstEmpty) = Scan(_hook, alarm);
        return match is null ? LatchInto(firstEmpty, alarm) : RefreshHook(match, alarm);
    }

    public bool ObserveServiceAlarm(ExtAlarm alarm)
    {
        if (alarm.ServiceId == 0 || alarm.MainErrorId == 0)
            return false;

        (Lifecycle? match, Lifecycle? firstEmpty) = Scan(_service, alarm);
        if (match is null)
            return LatchInto(firstEmpty, alarm);

        match.Seen = true;
        Reactivate(match);
        return true;
    }

    /// <summary>The latched entry with the same (ServiceId, MainErrorId) as <paramref name="alarm"/>, and the first empty slot seen before it.</summary>
    private static (Lifecycle? Match, Lifecycle? FirstEmpty) Scan(Lifecycle[] states, ExtAlarm alarm)
    {
        Lifecycle? firstEmpty = null;
        foreach (Lifecycle state in states)
        {
            if (!state.Latched)
            {
                firstEmpty ??= state;
                continue;
            }

            if (state.Alarm!.ServiceId == alarm.ServiceId && state.Alarm.MainErrorId == alarm.MainErrorId)
                return (state, firstEmpty);
        }

        return (null, firstEmpty);
    }

    public void EndCycle()
    {
        EndCycleOf(_service);
        EndCycleOf(_hook);
    }

    /// <summary>Exactly publishCapacity entries: latched alarms in service, configured, hook order from index 0 (= ModuleAlarmList[lower bound]), then null. Updates the latched counts and Overflow; changes no lifecycle.</summary>
    public IReadOnlyList<ExtAlarm?> Publish()
    {
        var list = new ExtAlarm?[_publishCapacity];
        int target = 0;

        _serviceLatchedCount = PublishGroup(_service, list, ref target);
        _configuredLatchedCount = PublishGroup(_configured, list, ref target);
        _hookLatchedCount = PublishGroup(_hook, list, ref target);
        return list;
    }

    private ushort PublishGroup(Lifecycle[] states, ExtAlarm?[] list, ref int target)
    {
        ushort latched = 0;
        foreach (Lifecycle state in states)
        {
            if (!state.Latched)
                continue;

            latched++;
            if (target >= list.Length)
            {
                _overflow = true;
                continue;
            }

            list[target++] = state.Alarm;
        }

        return latched;
    }

    private AlarmAckError RunRequest(AlarmAckRequest request)
    {
        if (request.RequestId == 0)
            return AlarmAckError.InvalidRequestId;
        if (request.RequestId == _lastAckRequestId)
            return AlarmAckError.DuplicateRequest;

        _lastAckRequestId = request.RequestId;
        return request.Command switch
        {
            (long)AlarmAckCommand.AcknowledgeOne => AcknowledgeOne(request),
            (long)AlarmAckCommand.AcknowledgeAll => AcknowledgeAll(),
            _ => AlarmAckError.InvalidCommand,
        };
    }

    private AlarmAckError AcknowledgeOne(AlarmAckRequest request)
    {
        if (request.MainErrorId == 0)
            return AlarmAckError.AlarmNotFound;

        Lifecycle? found = request.ServiceId == 0
            ? Find(_configured, alarm => alarm.MainErrorId == request.MainErrorId)
                ?? Find(_hook, alarm => alarm.MainErrorId == request.MainErrorId)
            : Find(_service, alarm => alarm.ServiceId == request.ServiceId && alarm.MainErrorId == request.MainErrorId);
        if (found is null)
            return AlarmAckError.AlarmNotFound;

        Acknowledge(found);
        return AlarmAckError.None;
    }

    private AlarmAckError AcknowledgeAll()
    {
        foreach (Lifecycle[] states in new[] { _configured, _service, _hook })
        {
            foreach (Lifecycle state in states)
            {
                if (state.Latched)
                    Acknowledge(state);
            }
        }

        return AlarmAckError.None;
    }

    private void RaiseConfigured(Lifecycle state, ExtAlarm snapshot, string utcDateTime)
    {
        if (!state.PreviousActive)
        {
            if (state.Latched)
                // Stryker disable once Boolean: the statement after the if sets Active = true again
                state.Alarm = state.Alarm! with { Active = true, Acknowledged = false, OccurrenceCount = state.Alarm.OccurrenceCount + 1 };
            else
                Latch(state, snapshot, 0, utcDateTime);
        }

        state.Alarm = state.Alarm! with { Active = true };
    }

    private static void ClearConfigured(Lifecycle state)
    {
        // Stryker disable once Logical: a latched configured alarm that is not PreviousActive is already inactive and unacknowledged (acknowledging an inactive alarm clears it), and Clear resets PreviousActive with Latched, so no state differs between || and &&
        if (!state.PreviousActive || !state.Latched)
            return;

        state.Alarm = state.Alarm! with { Active = false };
        if (state.Alarm.Acknowledged)
            state.Clear();
    }

    private bool RefreshHook(Lifecycle state, ExtAlarm alarm)
    {
        if (state.Seen)
        {
            if (_duplicateId == 0)
                _duplicateId = alarm.MainErrorId;
            return false;
        }

        state.Seen = true;
        Reactivate(state);
        return true;
    }

    private bool LatchInto(Lifecycle? slot, ExtAlarm alarm)
    {
        if (slot is null)
        {
            _overflow = true;
            return false;
        }

        Latch(slot, alarm, alarm.ServiceId, alarm.DateTime);
        slot.Seen = true;
        return true;
    }

    private void Latch(Lifecycle state, ExtAlarm snapshot, uint serviceId, string dateTime)
    {
        // Stryker disable once Statement: a state that is not latched always holds the Clear defaults, because every path that unlatches a state calls Clear
        state.Clear();
        state.Latched = true;
        state.Alarm = snapshot with
        {
            ModuleId = _moduleId,
            ServiceId = serviceId,
            DateTime = dateTime,
            Active = true,
            Acknowledged = false,
            OccurrenceCount = 1,
        };
    }

    private static void Reactivate(Lifecycle state)
    {
        if (state.Alarm!.Active)
            return;

        state.Alarm = state.Alarm with { Active = true, Acknowledged = false, OccurrenceCount = state.Alarm.OccurrenceCount + 1 };
    }

    private static void Acknowledge(Lifecycle state)
    {
        state.Alarm = state.Alarm! with { Acknowledged = true };
        if (!state.Alarm.Active)
            state.Clear();
    }

    private static void EndCycleOf(Lifecycle[] states)
    {
        foreach (Lifecycle state in states)
        {
            if (!state.Latched)
                continue;

            if (!state.Seen)
                state.Alarm = state.Alarm! with { Active = false };

            if (!state.Alarm!.Active && state.Alarm.Acknowledged)
                state.Clear();
        }
    }

    private static Lifecycle? Find(Lifecycle[] states, Func<ExtAlarm, bool> match)
    {
        foreach (Lifecycle state in states)
        {
            if (state.Latched && match(state.Alarm!))
                return state;
        }

        return null;
    }

    private void ResetAcknowledgement()
    {
        _ackResponseId = 0;
        _ackAccepted = false;
        _ackError = AlarmAckError.None;
        _lastAckRequestId = 0;
        _previousChangeRequest = false;
    }

    private static void ClearAll(Lifecycle[] states)
    {
        foreach (Lifecycle state in states)
            state.Clear();
    }

    private static Lifecycle[] NewStates(int count)
    {
        var states = new Lifecycle[count];
        for (int i = 0; i < count; i++)
            states[i] = new Lifecycle();
        return states;
    }

    private sealed class Lifecycle
    {
        public bool Latched;
        public ExtAlarm? Alarm;
        public bool Seen;
        public bool PreviousActive;

        public void Clear()
        {
            Latched = false;
            Alarm = null;
            // Stryker disable once Boolean: Seen of an unlatched state is never read, because BeginCycle resets it each cycle and LatchInto sets it after Latch
            Seen = false;
            PreviousActive = false;
        }
    }
}
