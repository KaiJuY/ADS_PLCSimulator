namespace AdsPlcSimulator.Core.PackML;

public enum PackMLState
{
    Undefined = 0,
    Clearing = 1,
    Stopped = 2,
    Starting = 3,
    Idle = 4,
    Suspended = 5,
    Execute = 6,
    Stopping = 7,
    Aborting = 8,
    Aborted = 9,
    Holding = 10,
    Held = 11,
    Unholding = 12,
    Suspending = 13,
    Unsuspending = 14,
    Resetting = 15,
    Completing = 16,
    Complete = 17,
}

public enum PackMLCommand
{
    None = 0,
    Reset = 1,
    Start = 2,
    Stop = 3,
    Hold = 4,
    Unhold = 5,
    Suspend = 6,
    Unsuspend = 7,
    Abort = 8,
    Clear = 9,
}

public enum PackMLErrorId : uint
{
    NoError = 0,
    ClearNotAllowed = 1002,
    ResetNotAllowed = 1003,
    StartNotAllowed = 1004,
    StopNotAllowed = 1005,
    HoldNotAllowed = 1006,
    UnholdNotAllowed = 1007,
    SuspendNotAllowed = 1008,
    UnsuspendNotAllowed = 1009,
    AbortNotAllowed = 1010,
    ModeNotSupported = 2001,
    ModeAlreadyActive = 2002,
    ModeChangeNotAllowedFromCurrentState = 2003,
}

public enum CompletionBehavior
{
    Unknown = 0,
    NaturalCompletion = 1,
    ExecuteUntilExternalStop = 2,
}

public readonly record struct PackMLInput(long Command, bool CommandRequest, long Mode, bool ModeRequest);

public sealed record ServiceIdentity(uint ServiceId, CompletionBehavior CompletionBehavior);

public sealed class ServiceIdentityException(string message) : Exception(message);

public sealed class ServiceHostException(string message) : Exception(message);
