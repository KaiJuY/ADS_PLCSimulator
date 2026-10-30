using System.Text.Json;
using AdsPlcSimulator.Host.Ads;

namespace AdsPlcSimulator.Tests.Ads;

public class AdsOptionsTests
{
    [Fact]
    [Trait("AC", "4")]
    public void Default_port_is_25860_not_the_real_plc_runtime_851()
    {
        string settings = File.ReadAllText(Path.Combine(TestPaths.SolutionRoot, "src", "AdsPlcSimulator.Host", "appsettings.json"));
        int configuredPort = JsonDocument.Parse(settings).RootElement.GetProperty("Ads").GetProperty("Port").GetInt32();

        Assert.Equal(25860, new AdsOptions().Port);
        Assert.Equal(25860, configuredPort);
    }
}
