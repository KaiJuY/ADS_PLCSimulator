using System.Diagnostics;
using AdsPlcSimulator.Core.Plc;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.Server;
using TwinCAT.Ads.Server.TypeSystem;
using TwinCAT.TypeSystem;

namespace AdsPlcSimulator.Host.Ads;

/// <summary>
/// ADS face of the simulated PLC. Publishes the symbol table (so SymbolLoader, ReadValue and variable handles work)
/// and serves every symbol read/write directly from the shared <see cref="ProcessImage"/>.
/// Read, write and notification lengths follow the AMS 854 measurements of issue #3.
/// </summary>
internal sealed class PlcAdsServer : AdsSymbolicServer
{
    /// <summary>Index group TwinCAT PLC runtimes use for symbol data.</summary>
    public const uint PlcDataIndexGroup = 0x4040;

    private const uint VariableHandleIndexGroup = 0xF005;

    private const int FirstHandleAboveBaseClassRange = 0x4000_0000;

    private static readonly TimeSpan ScanPeriod = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(10);

    private static readonly TimeSpan FirstSampleDelayAfterAddResponse = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(1);

    private readonly SymbolTable _symbols;
    private readonly ProcessImage _image;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, Subscription> _subscriptions = new();
    private readonly object _subscriptionLock = new();
    private readonly CancellationTokenSource _notifyStop = new();
    private int _nextNotificationHandle = FirstHandleAboveBaseClassRange - 1;
    private int _nextInvokeId;
    private int _notifyLoopStarted;
    private Task? _notifyLoop;

    public PlcAdsServer(ushort port, SymbolTable symbols, ProcessImage image, ILoggerFactory loggerFactory)
        : base(port, "AdsPlcSimulator", loggerFactory)
    {
        _symbols = symbols;
        _image = image;
        _logger = loggerFactory.CreateLogger<PlcAdsServer>();
    }

    protected override void OnCreateSymbols()
    {
        var typeFactory = new AdsTypeFactory();
        var rootTypes = _symbols.Roots.Select(root => typeFactory.GetOrCreate(root.Type)).ToList();
        foreach (var type in typeFactory.CreatedTypes)
            symbolFactory!.AddType(type);

        for (int index = 0; index < _symbols.Roots.Count; index++)
        {
            RootSymbol root = _symbols.Roots[index];
            var area = new DataArea(root.Path, PlcDataIndexGroup, (uint)root.Offset, (uint)root.Type.Size);
            symbolFactory!.AddDataArea(area).AddSymbol(root.Path, rootTypes[index], area);

            uint adsOffset = ((IProcessImageAddress)Symbols[root.Path]).IndexOffset;
            if (adsOffset != (uint)root.Offset)
                throw new InvalidOperationException($"Root '{root.Path}' has process image offset {root.Offset} but ADS offset {adsOffset}.");
        }

        base.OnCreateSymbols();
    }

    protected override Task<ResultReadDeviceState> OnReadDeviceStateAsync(AmsAddress sender, uint invokeId, CancellationToken cancel) =>
        Task.FromResult(ResultReadDeviceState.CreateSuccess(new StateInfo(AdsState.Run, 0)));

    protected override AdsErrorCode OnReadRawValue(ISymbol symbol, Span<byte> span)
    {
        if (!TryLocate(symbol, span.Length, out int offset))
            return AdsErrorCode.DeviceSymbolNotFound;

        _image.Read(offset, span);
        return AdsErrorCode.NoError;
    }

    protected override AdsErrorCode OnWriteRawValue(ISymbol symbol, ReadOnlySpan<byte> span)
    {
        if (!TryLocate(symbol, span.Length, out int offset))
            return AdsErrorCode.DeviceSymbolNotFound;

        _image.Write(offset, span, WriteSource.Client);
        return AdsErrorCode.NoError;
    }

    // The base class only routes its own SetValue/GetValue helpers here; ADS requests use the raw handlers above.
    protected override AdsErrorCode OnGetValue(ISymbol symbol, out object? value)
    {
        value = null;
        if (!_symbols.TryResolve(symbol.InstancePath, out SymbolRef resolved, out _) || !resolved.Type.IsScalar)
            return AdsErrorCode.DeviceSymbolNotFound;

        value = _image.ReadValue(resolved);
        return AdsErrorCode.NoError;
    }

    protected override AdsErrorCode OnSetValue(ISymbol symbol, object value, out bool valueChanged)
    {
        valueChanged = false;
        if (!_symbols.TryResolve(symbol.InstancePath, out SymbolRef resolved, out _) || !resolved.Type.IsScalar)
            return AdsErrorCode.DeviceSymbolNotFound;

        object before = _image.ReadValue(resolved);
        _image.WriteValue(resolved, value, WriteSource.Client);
        valueChanged = !Equals(before, _image.ReadValue(resolved));
        return AdsErrorCode.NoError;
    }

    protected override Task<ResultReadBytes> OnReadAsync(AmsAddress sender, uint invokeId, uint indexGroup, uint indexOffset, int readLength, CancellationToken cancel)
    {
        if (indexGroup == VariableHandleIndexGroup)
            return ReadByHandle(sender, invokeId, indexOffset, readLength, cancel);
        if (indexGroup != PlcDataIndexGroup)
            return base.OnReadAsync(sender, invokeId, indexGroup, indexOffset, readLength, cancel);

        if (!TryResolveRaw(indexOffset, readLength, _image.Size, out int offset))
            return Task.FromResult(ResultReadBytes.CreateError(AdsErrorCode.DeviceInvalidOffset));

        return Task.FromResult(ResultReadBytes.CreateSuccess(_image.Read(offset, readLength)));
    }

    // AMS 854 (#3): a handle read answers min(requested, symbol size) bytes.
    private Task<ResultReadBytes> ReadByHandle(AmsAddress sender, uint invokeId, uint handle, int readLength, CancellationToken cancel)
    {
        if (!TryResolveHandle(handle, out int offset, out int size))
            return base.OnReadAsync(sender, invokeId, VariableHandleIndexGroup, handle, readLength, cancel);

        return Task.FromResult(ResultReadBytes.CreateSuccess(_image.Read(offset, (int)Math.Min((uint)readLength, (uint)size))));
    }

    protected override Task<ResultWrite> OnWriteAsync(AmsAddress sender, uint invokeId, uint indexGroup, uint indexOffset, ReadOnlyMemory<byte> writeData, CancellationToken cancel)
    {
        if (indexGroup == VariableHandleIndexGroup)
            return WriteByHandle(sender, invokeId, indexOffset, writeData, cancel);
        if (indexGroup != PlcDataIndexGroup)
            return base.OnWriteAsync(sender, invokeId, indexGroup, indexOffset, writeData, cancel);

        if (!TryResolveRaw(indexOffset, writeData.Length, _image.Size, out int offset))
            return Task.FromResult(ResultWrite.CreateError(AdsErrorCode.DeviceInvalidOffset));

        return Task.FromResult(WriteUnlessEmpty(invokeId, offset, writeData));
    }

    private Task<ResultWrite> WriteByHandle(AmsAddress sender, uint invokeId, uint handle, ReadOnlyMemory<byte> writeData, CancellationToken cancel)
    {
        if (!TryResolveHandle(handle, out int offset, out int size))
            return base.OnWriteAsync(sender, invokeId, VariableHandleIndexGroup, handle, writeData, cancel);
        if (writeData.Length > size)
            return Task.FromResult(ResultWrite.CreateError(AdsErrorCode.DeviceInvalidSize));

        return Task.FromResult(WriteUnlessEmpty(invokeId, offset, writeData));
    }

    private ResultWrite WriteUnlessEmpty(uint invokeId, int offset, ReadOnlyMemory<byte> data)
    {
        if (data.Length > 0)
            _image.Write(offset, data.Span, WriteSource.Client);

        return ResultWrite.CreateSuccess(invokeId);
    }

    protected override Task<ResultHandle> OnAddDeviceNotificationAsync(AmsAddress sender, uint invokeId, uint indexGroup, uint indexOffset, int dataLength, AmsAddress receiver, NotificationSettings settings, CancellationToken cancel)
    {
        bool byHandle = indexGroup == VariableHandleIndexGroup;
        if (!byHandle && indexGroup != PlcDataIndexGroup)
            return base.OnAddDeviceNotificationAsync(sender, invokeId, indexGroup, indexOffset, dataLength, receiver, settings, cancel);

        int handleOffset = 0;
        int size = 0;
        if (byHandle && !TryResolveHandle(indexOffset, out handleOffset, out size))
            return Task.FromResult(ResultHandle.CreateError(AdsErrorCode.DeviceInvalidOffset));

        AdsErrorCode rangeError = CheckNotificationRange(byHandle, dataLength, size, indexOffset, _image.Size, out int rawOffset, out int watchLength);
        if (rangeError != AdsErrorCode.NoError)
            return Task.FromResult(ResultHandle.CreateError(rangeError));

        int offset = byHandle ? handleOffset : rawOffset;

        uint handle = (uint)Interlocked.Increment(ref _nextNotificationHandle);
        var subscription = new Subscription
        {
            Receiver = receiver,
            NotificationHandle = handle,
            Offset = offset,
            Cyclic = IsCyclic(settings.NotificationMode),
            Interval = TimeSpan.FromMilliseconds(Math.Max(settings.CycleTime, MinimumInterval.TotalMilliseconds)),
            Buffer = new byte[watchLength],
            NextDue = Stopwatch.GetTimestamp() + ToTicks(FirstSampleDelayAfterAddResponse),
        };
        lock (_subscriptionLock)
            _subscriptions[handle] = subscription;

        return Task.FromResult(ResultHandle.CreateSuccess(handle));
    }

    protected override Task<ResultAds> OnDeleteDeviceNotificationAsync(AmsAddress sender, uint invokeId, uint hNotification, CancellationToken cancel)
    {
        lock (_subscriptionLock)
        {
            if (!_subscriptions.TryGetValue(hNotification, out Subscription? subscription))
                return base.OnDeleteDeviceNotificationAsync(sender, invokeId, hNotification, cancel);
            if (!subscription.Receiver.Equals(sender))
                return Task.FromResult(ResultAds.CreateError(AdsErrorCode.DeviceNotifyHandleInvalid));

            _subscriptions.Remove(hNotification);
        }

        return Task.FromResult(ResultAds.CreateSuccess(invokeId));
    }

    protected override void OnConnected()
    {
        base.OnConnected();
        if (Interlocked.Exchange(ref _notifyLoopStarted, 1) == 1)
            return;

        _notifyLoop = Task.Run(() => NotifyLoopAsync(_notifyStop.Token));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyStop.Cancel();
            try
            {
                _notifyLoop?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }

            lock (_subscriptionLock)
                _subscriptions.Clear();
            _notifyStop.Dispose();
        }

        base.Dispose(disposing);
    }

    internal bool TryLocate(ISymbol symbol, int length, out int offset)
    {
        if (!TryResolvePath(symbol.InstancePath, out offset, out int size))
            return false;

        return length <= size && offset >= 0 && offset + length <= _image.Size;
    }

    private bool TryResolveHandle(uint handle, out int offset, out int size)
    {
        offset = 0;
        size = 0;
        if (handleTable is null || !handleTable.TryGetReferencedItem(handle, out string? path) || path is null)
            return false;

        return TryResolvePath(path, out offset, out size);
    }

    private bool TryResolvePath(string path, out int offset, out int size)
    {
        offset = 0;
        size = 0;
        if (!_symbols.TryResolve(path, out SymbolRef resolved, out _))
            return false;

        offset = resolved.Offset;
        size = resolved.Size;
        return true;
    }

    internal static AdsErrorCode CheckNotificationRange(bool byHandle, int dataLength, int size, uint indexOffset, int imageSize, out int offset, out int watchLength)
    {
        offset = 0;
        watchLength = dataLength;
        if (dataLength <= 0)
            return AdsErrorCode.DeviceInvalidWatchsize;
        if (!byHandle)
            return TryResolveRaw(indexOffset, dataLength, imageSize, out offset) ? AdsErrorCode.NoError : AdsErrorCode.DeviceInvalidOffset;

        watchLength = Math.Min(dataLength, size);
        return AdsErrorCode.NoError;
    }

    private static bool TryResolveRaw(uint indexOffset, int length, int imageSize, out int offset)
    {
        offset = 0;
        if (length < 0 || (long)indexOffset + length > imageSize)
            return false;

        offset = (int)indexOffset;
        return true;
    }

    // ClientCycle is obsolete in the SDK but a client may still send it; it means the same as Cyclic.
    private static bool IsCyclic(AdsTransMode mode)
    {
#pragma warning disable CS0612
        return mode is AdsTransMode.Cyclic or AdsTransMode.ClientCycle;
#pragma warning restore CS0612
    }

    private static long ToTicks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    private async Task NotifyLoopAsync(CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(ScanPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(cancel))
            {
                foreach (Subscription subscription in TakeDue())
                    await ScanAsync(subscription, cancel);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private List<Subscription> TakeDue()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_subscriptionLock)
            return _subscriptions.Values.Where(subscription => subscription.NextDue <= now).ToList();
    }

    private async Task ScanAsync(Subscription subscription, CancellationToken cancel)
    {
        _image.Read(subscription.Offset, subscription.Buffer);

        byte[]? data = null;
        lock (_subscriptionLock)
        {
            if (!_subscriptions.TryGetValue(subscription.NotificationHandle, out Subscription? current) || current != subscription)
                return;

            data = subscription.TakeSample(Stopwatch.GetTimestamp());
        }

        if (data is null)
            return;

        await SendAsync(subscription, data, cancel);
    }

    private async Task SendAsync(Subscription subscription, byte[] data, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(SendTimeout);
        var stamp = new NotificationSamplesStamp(DateTimeOffset.UtcNow, [new NotificationDataSample(subscription.NotificationHandle, data)]);
        try
        {
            uint invokeId = (uint)Interlocked.Increment(ref _nextInvokeId);
            AdsErrorCode result = await DeviceNotificationRequestAsync(subscription.Receiver, invokeId, 1, [stamp], timeout.Token);
            if (result != AdsErrorCode.NoError)
                Drop(subscription, result.ToString());
        }
        catch (Exception exception) when (!cancel.IsCancellationRequested)
        {
            Drop(subscription, exception.Message);
        }
    }

    private void Drop(Subscription subscription, string reason)
    {
        lock (_subscriptionLock)
            _subscriptions.Remove(subscription.NotificationHandle);

        _logger.LogWarning("Dropped notification {Handle} for {Receiver}: {Reason}", subscription.NotificationHandle, subscription.Receiver, reason);
    }

    private sealed class Subscription
    {
        public required AmsAddress Receiver { get; init; }

        public required uint NotificationHandle { get; init; }

        public required int Offset { get; init; }

        public required bool Cyclic { get; init; }

        public required TimeSpan Interval { get; init; }

        public required byte[] Buffer { get; init; }

        public byte[]? LastSent { get; set; }

        public long NextDue { get; set; }

        public byte[]? TakeSample(long now)
        {
            NextDue = now + ToTicks(Interval);
            bool changed = LastSent is null || !Buffer.AsSpan().SequenceEqual(LastSent);
            if (!changed && !Cyclic)
                return null;

            LastSent = Buffer.ToArray();
            return LastSent;
        }
    }
}
