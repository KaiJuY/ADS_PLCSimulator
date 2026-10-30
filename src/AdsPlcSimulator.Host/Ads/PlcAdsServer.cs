using System.Buffers.Binary;
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
    private const uint SumReadIndexGroup = 0xF080;
    private const uint SumWriteIndexGroup = 0xF081;
    private const uint SumReadWriteIndexGroup = 0xF082;
    private const uint SymbolVersionIndexGroup = 0xF008;

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
            return ReadByHandle(indexOffset, readLength);
        if (indexGroup != PlcDataIndexGroup)
            return base.OnReadAsync(sender, invokeId, indexGroup, indexOffset, readLength, cancel);

        if (!TryResolveRaw(indexOffset, readLength, _image.Size, out int offset))
            return Task.FromResult(ResultReadBytes.CreateError(AdsErrorCode.DeviceInvalidOffset));

        return Task.FromResult(ResultReadBytes.CreateSuccess(_image.Read(offset, readLength)));
    }

    // AMS 854 (#3): a handle read answers min(requested, symbol size) bytes.
    private Task<ResultReadBytes> ReadByHandle(uint handle, int readLength)
    {
        if (!TryResolveHandle(handle, out int offset, out int size))
            return Task.FromResult(ResultReadBytes.CreateError(AdsErrorCode.DeviceSymbolVersionInvalid));

        return Task.FromResult(ResultReadBytes.CreateSuccess(_image.Read(offset, (int)Math.Min((uint)readLength, (uint)size))));
    }

    protected override Task<ResultWrite> OnWriteAsync(AmsAddress sender, uint invokeId, uint indexGroup, uint indexOffset, ReadOnlyMemory<byte> writeData, CancellationToken cancel)
    {
        if (indexGroup == VariableHandleIndexGroup)
            return WriteByHandle(invokeId, indexOffset, writeData);
        if (indexGroup != PlcDataIndexGroup)
            return base.OnWriteAsync(sender, invokeId, indexGroup, indexOffset, writeData, cancel);

        if (!TryResolveRaw(indexOffset, writeData.Length, _image.Size, out int offset))
            return Task.FromResult(ResultWrite.CreateError(AdsErrorCode.DeviceInvalidOffset));

        return Task.FromResult(WriteUnlessEmpty(invokeId, offset, writeData));
    }

    private Task<ResultWrite> WriteByHandle(uint invokeId, uint handle, ReadOnlyMemory<byte> writeData)
    {
        if (!TryResolveHandle(handle, out int offset, out int size))
            return Task.FromResult(ResultWrite.CreateError(AdsErrorCode.DeviceSymbolVersionInvalid));
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

    protected override Task<ResultReadWriteBytes> OnReadWriteAsync(AmsAddress sender, uint invokeId, uint indexGroup, uint indexOffset, int readLength, ReadOnlyMemory<byte> writeData, CancellationToken cancel)
    {
        SumKind? kind = indexGroup switch
        {
            SumReadIndexGroup => SumKind.Read,
            SumWriteIndexGroup => SumKind.Write,
            SumReadWriteIndexGroup => SumKind.ReadWrite,
            _ => null,
        };
        if (kind is null)
            return base.OnReadWriteAsync(sender, invokeId, indexGroup, indexOffset, readLength, writeData, cancel);

        return SumAsync(sender, invokeId, kind.Value, indexOffset, readLength, writeData, cancel);
    }

    private async Task<ResultReadWriteBytes> SumAsync(AmsAddress sender, uint invokeId, SumKind kind, uint count, int readLength, ReadOnlyMemory<byte> request, CancellationToken cancel)
    {
        AdsErrorCode parseError = ParseSumItems(request.Span, count, kind, out SumItem[] items);
        if (parseError != AdsErrorCode.NoError)
            return ResultReadWriteBytes.CreateError(parseError);

        return kind switch
        {
            SumKind.Read => await SumReadAsync(sender, invokeId, items, readLength, cancel),
            SumKind.Write => await SumWriteAsync(sender, invokeId, items, readLength, request, cancel),
            _ => await SumReadWriteAsync(sender, invokeId, items, readLength, request, cancel),
        };
    }

    private async Task<ResultReadWriteBytes> SumReadAsync(AmsAddress sender, uint invokeId, SumItem[] items, int readLength, CancellationToken cancel)
    {
        long expected = items.Length * 4L + items.Sum(item => (long)item.ReadLength);
        if (readLength != expected)
            return ResultReadWriteBytes.CreateError(AdsErrorCode.DeviceInvalidSize);

        byte[] response = new byte[readLength];
        int slot = items.Length * 4;
        for (int index = 0; index < items.Length; index++)
        {
            SumItem item = items[index];
            ResultReadBytes result = await OnReadAsync(sender, invokeId, item.IndexGroup, item.IndexOffset, item.ReadLength, cancel);
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(index * 4), (uint)result.ErrorCode);
            if (result.ErrorCode == AdsErrorCode.NoError)
                result.Data.Span[..Math.Min(result.Data.Length, item.ReadLength)].CopyTo(response.AsSpan(slot));

            slot += item.ReadLength;
        }

        return ResultReadWriteBytes.CreateSuccess(response);
    }

    private async Task<ResultReadWriteBytes> SumWriteAsync(AmsAddress sender, uint invokeId, SumItem[] items, int readLength, ReadOnlyMemory<byte> request, CancellationToken cancel)
    {
        if (readLength < items.Length * 4L)
            return ResultReadWriteBytes.CreateError(AdsErrorCode.DeviceInvalidSize);

        byte[] response = new byte[items.Length * 4];
        for (int index = 0; index < items.Length; index++)
        {
            SumItem item = items[index];
            ResultWrite result = await OnWriteAsync(sender, invokeId, item.IndexGroup, item.IndexOffset, request.Slice(item.WriteOffset, item.WriteLength), cancel);
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(index * 4), (uint)result.ErrorCode);
        }

        return ResultReadWriteBytes.CreateSuccess(response);
    }

    private async Task<ResultReadWriteBytes> SumReadWriteAsync(AmsAddress sender, uint invokeId, SumItem[] items, int readLength, ReadOnlyMemory<byte> request, CancellationToken cancel)
    {
        if (readLength < items.Length * 8L + items.Sum(item => (long)item.ReadLength))
            return ResultReadWriteBytes.CreateError(AdsErrorCode.DeviceInvalidSize);

        var results = new List<ResultReadWriteBytes>(items.Length);
        foreach (SumItem item in items)
            results.Add(await ReadWriteItemAsync(sender, invokeId, item, request.Slice(item.WriteOffset, item.WriteLength), cancel));

        return ResultReadWriteBytes.CreateSuccess(BuildReadWriteResponse(items, results));
    }

    private Task<ResultReadWriteBytes> ReadWriteItemAsync(AmsAddress sender, uint invokeId, SumItem item, ReadOnlyMemory<byte> writeData, CancellationToken cancel)
    {
        if (item.IndexGroup is SumReadIndexGroup or SumWriteIndexGroup or SumReadWriteIndexGroup)
            return Task.FromResult(ResultReadWriteBytes.CreateError(AdsErrorCode.DeviceServiceNotSupported));

        return base.OnReadWriteAsync(sender, invokeId, item.IndexGroup, item.IndexOffset, item.ReadLength, writeData, cancel);
    }

    private static byte[] BuildReadWriteResponse(SumItem[] items, List<ResultReadWriteBytes> results)
    {
        var response = new List<byte>(items.Length * 8);
        var data = new List<byte>();
        for (int index = 0; index < items.Length; index++)
        {
            ResultReadWriteBytes result = results[index];
            ReadOnlyMemory<byte> bytes = result.ErrorCode == AdsErrorCode.NoError
                ? result.Data[..Math.Min(result.Data.Length, items[index].ReadLength)]
                : ReadOnlyMemory<byte>.Empty;
            response.AddRange(BitConverter.GetBytes((uint)result.ErrorCode));
            response.AddRange(BitConverter.GetBytes((uint)bytes.Length));
            data.AddRange(bytes.ToArray());
        }

        response.AddRange(data);
        return response.ToArray();
    }

    internal enum SumKind
    {
        Read,
        Write,
        ReadWrite,
    }

    internal readonly record struct SumItem(uint IndexGroup, uint IndexOffset, int ReadLength, int WriteLength, int WriteOffset);

    internal static AdsErrorCode ParseSumItems(ReadOnlySpan<byte> request, uint count, SumKind kind, out SumItem[] items)
    {
        items = [];
        if (count == 0)
            return AdsErrorCode.DeviceInvalidSize;

        int headerSize = kind == SumKind.ReadWrite ? 16 : 12;
        long headerTotal = count * (long)headerSize;
        if (headerTotal > request.Length)
            return AdsErrorCode.DeviceInvalidOffset;

        var parsed = new SumItem[count];
        long dataTotal = 0;
        for (int index = 0; index < parsed.Length; index++)
        {
            if (!TryParseSumItem(request.Slice(index * headerSize, headerSize), kind, headerTotal + dataTotal, out parsed[index]))
                return AdsErrorCode.DeviceInvalidSize;

            dataTotal += parsed[index].WriteLength;
        }

        if (request.Length != headerTotal + dataTotal)
            return AdsErrorCode.DeviceInvalidOffset;

        items = parsed;
        return AdsErrorCode.NoError;
    }

    private static bool TryParseSumItem(ReadOnlySpan<byte> header, SumKind kind, long writeOffset, out SumItem item)
    {
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint writeLength = kind switch
        {
            SumKind.Read => 0,
            SumKind.Write => length,
            _ => BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
        };
        uint readLength = kind == SumKind.Write ? 0 : length;
        item = default;
        if (readLength > int.MaxValue || writeLength > int.MaxValue)
            return false;

        item = new SumItem(
            BinaryPrimitives.ReadUInt32LittleEndian(header),
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..]),
            (int)readLength,
            (int)writeLength,
            (int)Math.Min(writeOffset, int.MaxValue));
        return true;
    }

    protected override Task<ResultHandle> OnAddDeviceNotificationAsync(AmsAddress sender, uint invokeId, uint indexGroup, uint indexOffset, int dataLength, AmsAddress receiver, NotificationSettings settings, CancellationToken cancel)
    {
        if (indexGroup == SymbolVersionIndexGroup)
            return Task.FromResult(AddSymbolVersionNotification(dataLength, receiver, settings));

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

        return Task.FromResult(Register(receiver, settings, offset, watchLength, null));
    }

    private ResultHandle AddSymbolVersionNotification(int dataLength, AmsAddress receiver, NotificationSettings settings)
    {
        if (dataLength <= 0)
            return ResultHandle.CreateError(AdsErrorCode.DeviceInvalidWatchsize);

        return Register(receiver, settings, 0, 1, [symbolVersion]);
    }

    private ResultHandle Register(AmsAddress receiver, NotificationSettings settings, int offset, int watchLength, byte[]? fixedData)
    {
        uint handle = (uint)Interlocked.Increment(ref _nextNotificationHandle);
        var subscription = new Subscription
        {
            Receiver = receiver,
            NotificationHandle = handle,
            Offset = offset,
            Cyclic = IsCyclic(settings.NotificationMode),
            Interval = TimeSpan.FromMilliseconds(Math.Max(settings.CycleTime, MinimumInterval.TotalMilliseconds)),
            Buffer = new byte[watchLength],
            FixedData = fixedData,
            NextDue = Stopwatch.GetTimestamp() + ToTicks(FirstSampleDelayAfterAddResponse),
        };
        lock (_subscriptionLock)
            _subscriptions[handle] = subscription;

        return ResultHandle.CreateSuccess(handle);
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
        if (subscription.FixedData is null)
            _image.Read(subscription.Offset, subscription.Buffer);
        else
            subscription.FixedData.CopyTo(subscription.Buffer, 0);

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

        public byte[]? FixedData { get; init; }

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
