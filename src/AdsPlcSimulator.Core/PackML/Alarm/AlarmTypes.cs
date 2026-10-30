namespace AdsPlcSimulator.Core.PackML.Alarm;

public enum AlarmSeverity { Alarm = 1, Warning = 2 }

public enum AlarmAckCommand { None = 0, AcknowledgeOne = 1, AcknowledgeAll = 2 }

public enum AlarmAckError { None = 0, InvalidRequestId = 1, DuplicateRequest = 2, InvalidCommand = 3, AlarmNotFound = 4 }

public enum AlarmOperator { GreaterThan, LessThan, Equal, GreaterOrEqual, LessOrEqual }

/// <summary>The dataType families the framework samples (FW: FB_ConfigValueSnapshotReader.M_GetLReal). String80 passes the load but every sample is invalid.</summary>
public enum AlarmSourceType { Boolean, SInt, USInt, Int, UInt, DInt, UDInt, Real, LReal, String80 }

public sealed record ExtAlarm(
    uint ModuleId, uint ServiceId, uint MainErrorId, uint SourceErrorId,
    string Message, string Path, AlarmSeverity Severity, string DateTime,
    bool Active, bool Acknowledged, uint OccurrenceCount);

/// <summary>ST_AlarmAckRequest as read from ModuleCtrl. Command stays a raw number so an undefined value reaches InvalidCommand.</summary>
public readonly record struct AlarmAckRequest(uint RequestId, long Command, uint MainErrorId, uint ServiceId, bool ChangeRequest);

public sealed record AlarmStatus(
    uint AckResponseId, bool AckAccepted, AlarmAckError AckError,
    ushort ConfiguredLatchedCount, ushort ServiceLatchedCount, bool Overflow,
    ushort HookLatchedCount, uint DuplicateId);

/// <summary>One alarmList entry of module-config.json, after validation.</summary>
public sealed record ConfiguredAlarm(
    string Source, AlarmSourceType SourceType, AlarmOperator Operator, double Threshold, double Tolerance,
    uint MainErrorId, uint SourceErrorId, string Message, AlarmSeverity Severity);

public sealed record ServiceErrorResult(bool Added, string Reason);
