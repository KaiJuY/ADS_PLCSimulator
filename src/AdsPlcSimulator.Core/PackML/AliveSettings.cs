using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML;

/// <summary>The timeouts of the module heartbeat and of the Service Alive check, from Param_Config.</summary>
public sealed record AliveSettings(TimeSpan HeartbeatTimeout, TimeSpan ServiceAliveMin, TimeSpan ServiceAliveMax)
{
    public static readonly AliveSettings Default = new(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));

    /// <summary>Reads Param_Config.ModuleHeartbeat_Timeout, ServiceAliveTimeoutMin, ServiceAliveTimeoutMax;
    /// a missing constant, one that is not a TimeSpan, or one at or below 0 takes the Default value.
    /// Only a literal T# value is a TimeSpan; an expression such as T#5S * 2 is evaluated as a long and counts as "not a TIME".</summary>
    /// <exception cref="ServiceHostException">ServiceAliveMin is greater than ServiceAliveMax, compared after defaults are applied; the message says which value is the default.</exception>
    public static AliveSettings FromSymbols(SymbolTable symbols)
    {
        TimeSpan heartbeat = Read(symbols, "ModuleHeartbeat_Timeout", Default.HeartbeatTimeout, out _);
        TimeSpan min = Read(symbols, "ServiceAliveTimeoutMin", Default.ServiceAliveMin, out bool minIsDefault);
        TimeSpan max = Read(symbols, "ServiceAliveTimeoutMax", Default.ServiceAliveMax, out bool maxIsDefault);
        RequireOrdered(min, minIsDefault, max, maxIsDefault);
        return new AliveSettings(heartbeat, min, max);
    }

    /// <summary>Checks the Param_Config values without building the settings.</summary>
    /// <exception cref="ServiceHostException">Same condition as <see cref="FromSymbols"/>.</exception>
    public static void Validate(SymbolTable symbols) => FromSymbols(symbols);

    private static void RequireOrdered(TimeSpan min, bool minIsDefault, TimeSpan max, bool maxIsDefault)
    {
        if (min <= max)
            return;

        throw new ServiceHostException(
            $"Param_Config.ServiceAliveTimeoutMin ({Describe(min, minIsDefault)}) is greater than ServiceAliveTimeoutMax ({Describe(max, maxIsDefault)}).");
    }

    public TimeSpan Clamp(TimeSpan aliveTimeout) =>
        // Stryker disable once Equality: at the boundary the unchanged branch returns the limit, which equals the timeout, so both comparisons give the same value
        aliveTimeout < ServiceAliveMin ? ServiceAliveMin : aliveTimeout > ServiceAliveMax ? ServiceAliveMax : aliveTimeout;

    private static TimeSpan Read(SymbolTable symbols, string name, TimeSpan fallback, out bool isDefault)
    {
        isDefault = !(symbols.TryGetConstant($"Param_Config.{name}", out object value) && value is TimeSpan { Ticks: > 0 });
        return isDefault ? fallback : (TimeSpan)value;
    }

    private static string Describe(TimeSpan value, bool isDefault) =>
        $"T#{value.TotalMilliseconds}MS{(isDefault ? " (default)" : "")}";
}
