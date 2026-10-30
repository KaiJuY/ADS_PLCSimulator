namespace AdsPlcSimulator.Host.Ads;

public enum AdsRouterMode
{
    /// <summary>Register at the AMS router of an installed TwinCAT (XAE/XAR/ADS setup).</summary>
    Local,

    /// <summary>Start the .NET AmsTcpIpRouter in-process, for machines without TwinCAT.</summary>
    Embedded,
}

public sealed class AdsOptions
{
    /// <summary>AMS port of the simulated PLC runtime. Not 851: that is the first runtime of a real TwinCAT PLC on the same router.</summary>
    public ushort Port { get; set; } = 25860;

    public AdsRouterMode Router { get; set; } = AdsRouterMode.Local;

    /// <summary>AMS NetId of the embedded router. Ignored in <see cref="AdsRouterMode.Local"/>.</summary>
    public string NetId { get; set; } = "1.1.1.1.1.1";
}
