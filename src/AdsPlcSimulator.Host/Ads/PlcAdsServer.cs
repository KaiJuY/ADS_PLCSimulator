using AdsPlcSimulator.Core.Plc;
using TwinCAT.Ads;
using TwinCAT.Ads.Server;
using TwinCAT.Ads.Server.TypeSystem;
using TwinCAT.TypeSystem;

namespace AdsPlcSimulator.Host.Ads;

/// <summary>
/// ADS face of the simulated PLC. Publishes the symbol table (so SymbolLoader, ReadValue and variable handles work)
/// and serves every symbol read/write directly from the shared <see cref="ProcessImage"/>.
/// </summary>
internal sealed class PlcAdsServer : AdsSymbolicServer
{
    /// <summary>Index group TwinCAT PLC runtimes use for symbol data.</summary>
    public const uint PlcDataIndexGroup = 0x4040;

    private readonly SymbolTable _symbols;
    private readonly ProcessImage _image;
    private readonly Dictionary<string, int> _coreOffsetMinusAdsOffset = new(StringComparer.OrdinalIgnoreCase);

    public PlcAdsServer(ushort port, SymbolTable symbols, ProcessImage image, ILoggerFactory loggerFactory)
        : base(port, "AdsPlcSimulator", loggerFactory)
    {
        _symbols = symbols;
        _image = image;
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
            _coreOffsetMinusAdsOffset[root.Path] = root.Offset - (int)adsOffset;
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

    /// <summary>Maps any (sub-)symbol to its process-image offset through the offset of its root variable.</summary>
    private bool TryLocate(ISymbol symbol, int length, out int offset)
    {
        offset = 0;
        // The server adds virtual parents such as "GVL_Module" above root variables, so stop at the first registered root.
        ISymbol? root = symbol;
        int delta = 0;
        while (root is not null && !_coreOffsetMinusAdsOffset.TryGetValue(root.InstancePath, out delta))
            root = root.Parent;

        if (root is null || symbol is not IProcessImageAddress address)
            return false;

        offset = (int)address.IndexOffset + delta;
        return length <= symbol.ByteSize && offset >= 0 && offset + length <= _image.Size;
    }
}
