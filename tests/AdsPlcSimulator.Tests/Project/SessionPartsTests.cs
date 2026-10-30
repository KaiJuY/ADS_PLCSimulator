using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Host.Live;
using AdsPlcSimulator.Host.Project;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdsPlcSimulator.Tests.Project;

[Collection("AdsPort25855")]
public sealed class SessionPartsTests
{
    [Fact]
    [Trait("AC", "6")]
    public void Live_channel_forwards_simulation_commands_until_closed()
    {
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);
        var channel = new SessionLiveChannel(hub);

        channel.Invoke("t", "a", 1);
        Assert.Equal(1, hub.SimulationStateCount);

        channel.Close();
        channel.Invoke("other", "a", 1);
        Assert.Equal(1, hub.SimulationStateCount);
    }

    [Fact]
    [Trait("AC", "6")]
    public void Live_channel_forwards_flow_events_until_closed()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var hub = new LiveHub(factory.CreateLogger<LiveHub>());
        var channel = new SessionLiveChannel(hub);
        var failure = new FlowEvent(FlowEventKind.RunFailed, "wf", 1, "node", Error: "boom");

        channel.Publish(failure);
        Assert.Single(logs.Entries);

        channel.Close();
        channel.Publish(failure);
        Assert.Single(logs.Entries);
    }

    [Fact]
    [Trait("AC", "11")]
    public async Task File_provider_is_empty_while_stopped()
    {
        using var root = new ProjectTestRoot();
        ProjectHost host = root.CreateHost();

        Assert.False(new RunningProjectFileProvider(host).GetFileInfo("index.html").Exists);
        await host.DisposeAsync();
    }

    [Theory]
    [Trait("Category", "Ads")]
    [Trait("AC", "11")]
    [InlineData("Alpha", true)]
    [InlineData("Beta", false)]
    public async Task File_provider_serves_the_simulation_folder_of_the_running_project(string project, bool expected)
    {
        using var root = new ProjectTestRoot();
        ProjectHost host = root.CreateHost();
        await host.RunAsync(project);
        try
        {
            Assert.Equal(expected, new RunningProjectFileProvider(host).GetFileInfo("index.html").Exists);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    [Trait("AC", "5")]
    public void Session_construction_failure_rethrows_the_original_exception()
    {
        using var root = new ProjectTestRoot();
        string project = root.ProjectPath("Gamma");
        Directory.CreateDirectory(Path.Combine(project, "plc"));
        File.WriteAllText(Path.Combine(project, "plc", "g.st"), "VAR_GLOBAL n : INT; END_VAR");
        File.WriteAllText(Path.Combine(project, "workflows"), "this is a file");
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);

        Assert.Throws<IOException>(() => new SimulatorSession(project, hub, NullLogger<SimulatorSession>.Instance));

        Assert.Equal(0, hub.SimulationStateCount);
    }
}
