using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML.Alarm;

internal sealed class ExtAlarmCodec
{
    private static readonly string[] MemberNames =
    [
        "ModuleId", "ServiceId", "MainErrorId", "SourceErrorId", "Message", "Path",
        "Severity", "DateTime", "Active", "Acknowledged", "OccurrenceCount",
    ];

    private readonly Slot[] _slots;
    private readonly int _messageLength;
    private readonly int _pathLength;
    private readonly int _dateTimeLength;

    private ExtAlarmCodec(Slot[] slots, int size)
    {
        _slots = slots;
        Size = size;
        _messageLength = ((StringType)slots[4].Type.Unaliased).Length;
        _pathLength = ((StringType)slots[5].Type.Unaliased).Length;
        _dateTimeLength = ((StringType)slots[7].Type.Unaliased).Length;
    }

    public int Size { get; }

    public static ExtAlarmCodec? TryCreate(PlcType extAlarmType)
    {
        Slot[]? slots = AlarmLayout.Locate(extAlarmType, MemberNames);
        if (slots is null || !IsNarrowString(slots[4]) || !IsNarrowString(slots[5]) || !IsNarrowString(slots[7]))
            return null;

        return new ExtAlarmCodec(slots, extAlarmType.Size);
    }

    public void Encode(ExtAlarm? alarm, Span<byte> destination)
    {
        destination = destination[..Size];
        destination.Clear();
        if (alarm is null)
            return;

        _slots[0].Write(destination, (long)alarm.ModuleId);
        _slots[1].Write(destination, (long)alarm.ServiceId);
        _slots[2].Write(destination, (long)alarm.MainErrorId);
        _slots[3].Write(destination, (long)alarm.SourceErrorId);
        _slots[4].Write(destination, Cut(alarm.Message, _messageLength));
        _slots[5].Write(destination, Cut(alarm.Path, _pathLength));
        _slots[6].Write(destination, (long)alarm.Severity);
        _slots[7].Write(destination, Cut(alarm.DateTime, _dateTimeLength));
        _slots[8].Write(destination, alarm.Active);
        _slots[9].Write(destination, alarm.Acknowledged);
        _slots[10].Write(destination, (long)alarm.OccurrenceCount);
    }

    private static bool IsNarrowString(Slot slot) => slot.Type.Unaliased is StringType { IsWide: false };

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];
}

internal static class AlarmLayout
{
    public static Slot[]? Locate(PlcType type, string[] names)
    {
        var slots = new Slot[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            StructMember? member = PlcLayout.Find(type, names[i]);
            if (member is null)
                return null;
            slots[i] = new Slot(member.Offset, member.Type);
        }

        return slots;
    }

    public static StructMember? Member(PlcType type, string name) => PlcLayout.Find(type, name);

    public static (StructMember Member, ArrayType Array)? ArrayMember(PlcType type, string name)
    {
        StructMember? member = Member(type, name);
        if (member?.Type.Unaliased is not ArrayType { Dimensions.Count: 1 } array)
            return null;

        return (member, array);
    }

    public static bool IsZero(ProcessImage image, int offset, int length) =>
        !image.Read(offset, length).AsSpan().ContainsAnyExcept((byte)0);

    public static void WriteZeros(ProcessImage image, int offset, int length) =>
        image.Write(offset, new byte[length], WriteSource.Workflow);
}

internal sealed class ModuleAlarmBlock
{
    private static readonly string[] StatusNames =
    [
        "AckResponseId", "AckAccepted", "AckError", "ConfiguredLatchedCount",
        "ServiceLatchedCount", "Overflow", "HookLatchedCount", "DuplicateId",
    ];

    private static readonly string[] AckNames = ["RequestId", "Command", "MainErrorId", "ServiceId", "bChangeRequest"];

    private readonly ExtAlarmCodec _codec;
    private readonly int _listOffset;
    private readonly Slot[] _statusSlots;
    private readonly int _statusOffset;
    private readonly int _statusSize;
    private readonly Slot[] _ackSlots;
    private readonly int _ackOffset;
    private readonly int _ackSize;
    private readonly int _moduleIdOffset;
    private readonly PlcType _moduleIdType;
    private ExtAlarm?[] _lastList;
    private AlarmStatus _lastStatus = EmptyStatus;

    private ModuleAlarmBlock(
        ExtAlarmCodec codec, int listOffset, int listCapacity,
        Slot[] statusSlots, int statusOffset, int statusSize,
        Slot[] ackSlots, int ackOffset, int ackSize,
        int moduleIdOffset, PlcType moduleIdType)
    {
        _codec = codec;
        _listOffset = listOffset;
        ListCapacity = listCapacity;
        _statusSlots = statusSlots;
        _statusOffset = statusOffset;
        _statusSize = statusSize;
        _ackSlots = ackSlots;
        _ackOffset = ackOffset;
        _ackSize = ackSize;
        _moduleIdOffset = moduleIdOffset;
        _moduleIdType = moduleIdType;
        _lastList = new ExtAlarm?[listCapacity];
    }

    private static AlarmStatus EmptyStatus { get; } = new(0, false, AlarmAckError.None, 0, 0, false, 0, 0);

    public int ListCapacity { get; }

    public static ModuleAlarmBlock? TryCreate(SymbolRef module)
    {
        var list = AlarmLayout.ArrayMember(module.Type, "ModuleAlarmList");
        StructMember? ctrl = AlarmLayout.Member(module.Type, "ModuleCtrl");
        StructMember? status = AlarmLayout.Member(module.Type, "ModuleStatus");
        StructMember? ack = ctrl is null ? null : AlarmLayout.Member(ctrl.Type, "AlarmAckRequest");
        StructMember? alarm = status is null ? null : AlarmLayout.Member(status.Type, "Alarm");
        StructMember? moduleId = status is null ? null : AlarmLayout.Member(status.Type, "ModuleId");
        if (list is null || ctrl is null || status is null || ack is null || alarm is null || moduleId is null)
            return null;

        ExtAlarmCodec? codec = ExtAlarmCodec.TryCreate(list.Value.Array.ElementType);
        Slot[]? statusSlots = AlarmLayout.Locate(alarm.Type, StatusNames);
        Slot[]? ackSlots = AlarmLayout.Locate(ack.Type, AckNames);
        if (codec is null || statusSlots is null || ackSlots is null || !moduleId.Type.IsScalar)
            return null;

        return new ModuleAlarmBlock(
            codec,
            module.Offset + list.Value.Member.Offset,
            list.Value.Array.ElementCount,
            statusSlots,
            module.Offset + status.Offset + alarm.Offset,
            alarm.Type.Size,
            ackSlots,
            module.Offset + ctrl.Offset + ack.Offset,
            ack.Type.Size,
            module.Offset + status.Offset + moduleId.Offset,
            moduleId.Type);
    }

    public AlarmAckRequest ReadAckRequest(ProcessImage image)
    {
        byte[] block = image.Read(_ackOffset, _ackSize);
        return new AlarmAckRequest(
            (uint)PlcValue.ToInt64(_ackSlots[0].Read(block)),
            PlcValue.ToInt64(_ackSlots[1].Read(block)),
            (uint)PlcValue.ToInt64(_ackSlots[2].Read(block)),
            (uint)PlcValue.ToInt64(_ackSlots[3].Read(block)),
            PlcValue.ToBoolean(_ackSlots[4].Read(block)));
    }

    public uint ReadModuleId(ProcessImage image) =>
        (uint)PlcValue.ToInt64(ValueCodec.Decode(_moduleIdType, image.Read(_moduleIdOffset, _moduleIdType.Size)));

    public void Write(ProcessImage image, IReadOnlyList<ExtAlarm?> list, AlarmStatus status)
    {
        byte[] entry = new byte[_codec.Size];
        for (int index = 0; index < ListCapacity; index++)
        {
            if (Equals(list[index], _lastList[index]))
                continue;

            _codec.Encode(list[index], entry);
            image.Write(_listOffset + index * _codec.Size, entry, WriteSource.Workflow);
            _lastList[index] = list[index];
        }

        if (status == _lastStatus)
            return;

        image.Write(_statusOffset, EncodeStatus(status), WriteSource.Workflow);
        _lastStatus = status;
    }

    public void Clear(ProcessImage image)
    {
        int listBytes = ListCapacity * _codec.Size;
        if (!AlarmLayout.IsZero(image, _listOffset, listBytes))
            AlarmLayout.WriteZeros(image, _listOffset, listBytes);
        if (!AlarmLayout.IsZero(image, _statusOffset, _statusSize))
            AlarmLayout.WriteZeros(image, _statusOffset, _statusSize);

        _lastList = new ExtAlarm?[ListCapacity];
        _lastStatus = EmptyStatus;
    }

    private byte[] EncodeStatus(AlarmStatus status)
    {
        var block = new byte[_statusSize];
        _statusSlots[0].Write(block, (long)status.AckResponseId);
        _statusSlots[1].Write(block, status.AckAccepted);
        _statusSlots[2].Write(block, (long)status.AckError);
        _statusSlots[3].Write(block, (long)status.ConfiguredLatchedCount);
        _statusSlots[4].Write(block, (long)status.ServiceLatchedCount);
        _statusSlots[5].Write(block, status.Overflow);
        _statusSlots[6].Write(block, (long)status.HookLatchedCount);
        _statusSlots[7].Write(block, (long)status.DuplicateId);
        return block;
    }
}

internal sealed class ServiceErrorList
{
    private readonly ExtAlarmCodec _codec;
    private readonly int _offset;
    private readonly ExtAlarm?[] _slots;
    private ExtAlarm?[] _lastWritten;

    private ServiceErrorList(ExtAlarmCodec codec, int offset, int capacity)
    {
        _codec = codec;
        _offset = offset;
        _slots = new ExtAlarm?[capacity];
        _lastWritten = new ExtAlarm?[capacity];
    }

    public IEnumerable<ExtAlarm> Entries => _slots.OfType<ExtAlarm>();

    public static ServiceErrorList? TryCreate(SymbolRef serviceStatus)
    {
        var list = AlarmLayout.ArrayMember(serviceStatus.Type, "ErrorList");
        if (list is null)
            return null;

        ExtAlarmCodec? codec = ExtAlarmCodec.TryCreate(list.Value.Array.ElementType);
        return codec is null
            ? null
            : new ServiceErrorList(codec, serviceStatus.Offset + list.Value.Member.Offset, list.Value.Array.ElementCount);
    }

    public ServiceErrorResult Add(ExtAlarm entry)
    {
        int firstEmpty = -1;
        for (int index = 0; index < _slots.Length && firstEmpty < 0; index++)
        {
            ExtAlarm? current = _slots[index];
            if (current is null)
                firstEmpty = index;
            else if (current.MainErrorId == entry.MainErrorId)
                return new ServiceErrorResult(false, $"duplicate MainErrorId {entry.MainErrorId}");
        }

        if (firstEmpty < 0)
            return new ServiceErrorResult(false, $"ErrorList is full ({_slots.Length})");

        _slots[firstEmpty] = entry;
        return new ServiceErrorResult(true, "");
    }

    public void Clear() => Array.Clear(_slots);

    public void WriteIfChanged(ProcessImage image)
    {
        byte[] entry = new byte[_codec.Size];
        for (int index = 0; index < _slots.Length; index++)
        {
            if (Equals(_slots[index], _lastWritten[index]))
                continue;

            _codec.Encode(_slots[index], entry);
            image.Write(_offset + index * _codec.Size, entry, WriteSource.Workflow);
            _lastWritten[index] = _slots[index];
        }
    }

    public void ResetImage(ProcessImage image)
    {
        Clear();
        int length = _slots.Length * _codec.Size;
        if (!AlarmLayout.IsZero(image, _offset, length))
            AlarmLayout.WriteZeros(image, _offset, length);

        _lastWritten = new ExtAlarm?[_slots.Length];
    }
}
