using AdsPlcSimulator.Core.PackML;
using TwinCAT.Ads;

namespace AdsPlcSimulator.Integration;

/// <summary>Reads and writes symbols over ADS and performs the PackML request handshake.</summary>
internal sealed class PlcSession(AdsClient client)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ReactionTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NegativeWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan EdgeGap = TimeSpan.FromMilliseconds(30);

    private uint _requestId = 1000;

    public AdsClient Client => client;

    public T Read<T>(string path) => (T)client.ReadValue(path, typeof(T));

    public void Write(string path, object value) => client.WriteValue(path, value);

    public PackMLState State(Endpoint endpoint) => (PackMLState)Read<short>($"{endpoint.Status}.PackMLOut.eStateCurrent");

    public PackMLState PreviousState(Endpoint endpoint) => (PackMLState)Read<short>($"{endpoint.Status}.PackMLOut.eStatePrevious");

    public uint ResponseId(Endpoint endpoint) => Read<uint>($"{endpoint.Status}.ResponseId");

    public bool Accepted(Endpoint endpoint) => Read<bool>($"{endpoint.Status}.PackMLOut.bCommandAccepted");

    public bool Rejected(Endpoint endpoint) => Read<bool>($"{endpoint.Status}.PackMLOut.bCommandRejected");

    public uint StateErrorId(Endpoint endpoint) => Read<uint>($"{endpoint.Status}.PackMLOut.eStateErrorID");

    public uint NextRequestId() => ++_requestId;

    /// <summary>Writes RequestId and eCommand, then raises bCommandChangeRequest (a rising edge).</summary>
    public void Send(Endpoint endpoint, PackMLCommand command, uint requestId)
    {
        Write($"{endpoint.Ctrl}.PackMLIn.bCommandChangeRequest", false);
        // The PLC sees an edge only if one scan reads the FALSE; a FALSE and TRUE written within one 10 ms scan are lost (S6/S7 failed this way).
        Thread.Sleep(EdgeGap);
        Write($"{endpoint.Ctrl}.RequestId", requestId);
        Write($"{endpoint.Ctrl}.PackMLIn.eCommand", (short)command);
        Write($"{endpoint.Ctrl}.PackMLIn.bCommandChangeRequest", true);
    }

    public void Release(Endpoint endpoint) => Write($"{endpoint.Ctrl}.PackMLIn.bCommandChangeRequest", false);

    public async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (condition())
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(PollInterval);
        }
    }

    public Task<bool> WaitForStateAsync(Endpoint endpoint, PackMLState state, TimeSpan timeout) =>
        WaitForAsync(() => State(endpoint) == state, timeout);

    public Task<bool> WaitForResponseAsync(Endpoint endpoint, uint requestId) =>
        WaitForAsync(() => ResponseId(endpoint) == requestId, ReactionTimeout);

    public Task ObserveNegativeAsync() => Task.Delay(NegativeWindow);

    /// <summary>Brings a service back to Idle with the commands PackML allows (Reset, Clear, Abort).</summary>
    public async Task<bool> EnsureIdleAsync(Endpoint endpoint)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            PackMLState state = State(endpoint);
            if (state == PackMLState.Idle)
                return true;

            await SendAndSettleAsync(endpoint, RecoveryCommand(state));
        }
        return State(endpoint) == PackMLState.Idle;
    }

    private async Task SendAndSettleAsync(Endpoint endpoint, PackMLCommand command)
    {
        uint requestId = NextRequestId();
        Send(endpoint, command, requestId);
        await WaitForResponseAsync(endpoint, requestId);
        Release(endpoint);
        await WaitForAsync(() => IsResting(State(endpoint)), SettleTimeout);
    }

    private static PackMLCommand RecoveryCommand(PackMLState state) => state switch
    {
        PackMLState.Complete or PackMLState.Stopped => PackMLCommand.Reset,
        PackMLState.Aborted => PackMLCommand.Clear,
        _ => PackMLCommand.Abort,
    };

    private static bool IsResting(PackMLState state) => state is PackMLState.Idle or PackMLState.Complete
        or PackMLState.Stopped or PackMLState.Aborted or PackMLState.Execute or PackMLState.Held or PackMLState.Suspended;

    public async Task<bool> RunSettledAsync(Endpoint endpoint, PackMLCommand command, PackMLState expected)
    {
        uint requestId = NextRequestId();
        Send(endpoint, command, requestId);
        bool reached = await WaitForStateAsync(endpoint, expected, SettleTimeout);
        Release(endpoint);
        return reached;
    }
}
