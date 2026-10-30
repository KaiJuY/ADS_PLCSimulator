using System.Text.Json;
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
    }

    [Fact]
    [Trait("AC", "1")]
    public void Options_default_to_the_projects_folder_without_a_project_path()
    {
        string settings = File.ReadAllText(Path.Combine(TestPaths.SolutionRoot, "src", "AdsPlcSimulator.Host", "appsettings.json"));
        JsonElement simulator = JsonDocument.Parse(settings).RootElement.GetProperty("Simulator");

        Assert.Equal("../../projects", new SimulatorOptions().ProjectsRoot);
        Assert.Null(new SimulatorOptions().ProjectPath);
        Assert.Equal("../../projects", simulator.GetProperty("ProjectsRoot").GetString());
        Assert.False(simulator.TryGetProperty("ProjectPath", out _));
    }

    [Fact]
    [Trait("AC", "11")]
    public async Task Running_project_exposes_its_simulation_folder_as_directory_contents()
    {
        using var root = new ProjectTestRoot();
        ProjectHost host = root.CreateHost();
        var provider = new RunningProjectFileProvider(host);
        Assert.False(provider.GetDirectoryContents("").Exists);

        await host.RunAsync("Alpha");
        try
        {
            Assert.Contains(provider.GetDirectoryContents(""), file => file.Name == "index.html");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    [Trait("AC", "5")]
    public void Session_construction_loads_the_workflow_files_of_the_project()
    {
        using var root = new ProjectTestRoot();
        string workflows = Path.Combine(root.ProjectPath("Alpha"), "workflows");
        Directory.CreateDirectory(workflows);
        File.WriteAllText(Path.Combine(workflows, "wf.flow.json"), "{\"nodes\":[],\"links\":[]}");
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);

        SimulatorSession session = new(root.ProjectPath("Alpha"), hub, NullLogger<SimulatorSession>.Instance);

        WorkflowStatus status = Assert.Single(session.Workflows);
        Assert.Equal("wf", status.Name);
        Assert.True(status.Loaded);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Session_construction_rejects_an_invalid_module_config()
    {
        using var root = new ProjectTestRoot();
        string config = Path.Combine(root.ProjectPath("Alpha"), "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "module-config.json"), "{ not json");
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);

        var exception = Assert.Throws<ModuleConfigException>(() => new SimulatorSession(root.ProjectPath("Alpha"), hub, NullLogger<SimulatorSession>.Instance));

        Assert.Contains("module-config.json", exception.Message);
    }
}
