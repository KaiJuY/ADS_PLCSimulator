namespace AdsPlcSimulator.Core.PackML.Alarm;

/// <summary>Configured alarms per module path ("GVL_Module.Chamber_Control[1]"); built once per SimulatorSession (= once per Run) by the Host.</summary>
public sealed class ConfiguredAlarmTable(IReadOnlyDictionary<string, IReadOnlyList<ConfiguredAlarm>> byModulePath)
{
    public static readonly ConfiguredAlarmTable Empty = new(new Dictionary<string, IReadOnlyList<ConfiguredAlarm>>());

    private readonly Dictionary<string, IReadOnlyList<ConfiguredAlarm>> _byModulePath =
        new(byModulePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Empty list when the module has none; the path match is case-insensitive.</summary>
    public IReadOnlyList<ConfiguredAlarm> For(string modulePath) =>
        _byModulePath.TryGetValue(modulePath, out IReadOnlyList<ConfiguredAlarm>? alarms) ? alarms : [];
}
