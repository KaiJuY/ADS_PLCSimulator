using System.Text.Json;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.PackML.Behavior;
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

    [Fact]
    [Trait("AC", "2")]
    public void Session_without_service_behaviors_keeps_the_empty_table()
    {
        using var root = new ProjectTestRoot();
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);

        SimulatorSession session = new(root.ProjectPath("Alpha"), hub, NullLogger<SimulatorSession>.Instance);

        Assert.Same(ServiceBehaviorTable.Empty, session.ServiceBehaviors);
        Assert.Same(ServiceBehaviorTable.Empty, session.Runtime.Registry.ServiceBehaviors);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Session_construction_rejects_service_behaviors_without_a_module_config()
    {
        using var root = new ProjectTestRoot();
        string config = Path.Combine(root.ProjectPath("Alpha"), "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "service-behaviors.json"), "{}");
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);

        var exception = Assert.Throws<ModuleConfigException>(() => new SimulatorSession(root.ProjectPath("Alpha"), hub, NullLogger<SimulatorSession>.Instance));

        Assert.Contains("module-config.json", exception.Message);
    }

    [Fact]
    [Trait("AC", "1")]
    public async Task Session_of_the_Polar2_project_loads_its_service_behaviors_into_the_registry()
    {
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);
        await using var session = new SimulatorSession(TestPaths.Polar2Project, hub, NullLogger<SimulatorSession>.Instance);

        Assert.NotNull(session.ServiceBehaviors.For("GVL_Module.Chamber_Control[1]"));
        Assert.Same(session.ServiceBehaviors, session.Runtime.Registry.ServiceBehaviors);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Session_construction_rejects_ServiceAliveTimeoutMin_above_Max()
    {
        using var root = new ProjectTestRoot();
        File.WriteAllText(Path.Combine(root.ProjectPath("Alpha"), "plc", "Param_Config.st"), """
            VAR_GLOBAL CONSTANT
                ServiceAliveTimeoutMin : TIME := T#2S;
                ServiceAliveTimeoutMax : TIME := T#1S;
            END_VAR
            """);
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);

        var exception = Assert.Throws<ServiceHostException>(() => new SimulatorSession(root.ProjectPath("Alpha"), hub, NullLogger<SimulatorSession>.Instance));

        Assert.Contains("ServiceAliveTimeoutMin", exception.Message);
    }

    [Fact]
    [Trait("AC", "12")]
    public void Session_construction_hands_the_configured_alarms_to_the_runtime_before_workflows_load()
    {
        using var root = new ProjectTestRoot();
        string project = root.ProjectPath("Alpha");
        File.WriteAllText(Path.Combine(project, "plc", "GVL_Module.st"), """
            TYPE ST_VariableStatus : STRUCT PublishedCount : UINT; END_STRUCT END_TYPE
            TYPE ST_ModuleStatus : STRUCT ModuleId : UDINT; Enabled : BOOL; ModuleType : STRING(16); Variable : ST_VariableStatus; END_STRUCT END_TYPE
            TYPE ST_ExtAlarm : STRUCT Message : STRING(80); Path : STRING(255); END_STRUCT END_TYPE
            TYPE ST_Descriptor : STRUCT Id : UDINT; END_STRUCT END_TYPE
            TYPE ST_Module : STRUCT
                ModuleStatus : ST_ModuleStatus; Level : REAL; ModuleAlarmList : ARRAY[1..2] OF ST_ExtAlarm;
                ModuleVariableList : ARRAY[1..1] OF ST_Descriptor; ModuleConfigParamList : ARRAY[1..1] OF ST_Descriptor;
            END_STRUCT END_TYPE
            VAR_GLOBAL Chamber_Control : ARRAY[1..1] OF ST_Module; END_VAR
            """);
        Directory.CreateDirectory(Path.Combine(project, "config"));
        File.WriteAllText(Path.Combine(project, "config", "module-config.json"), """
            {"schemaVersion":4,"modules":[{"enabled":true,"slot":1,"id":1,"variableList":[],"configParamList":[],
             "alarmList":[{"source":"Level","dataType":"REAL","condition":{"operator":"gt","value":5},"mainErrorId":9,"message":"High"}]}]}
            """);
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);

        SimulatorSession session = new(project, hub, NullLogger<SimulatorSession>.Instance);

        Assert.Same(session.ConfiguredAlarms, session.Runtime.Registry.ConfiguredAlarms);
        ConfiguredAlarm alarm = Assert.Single(session.Runtime.Registry.ConfiguredAlarms.For("GVL_Module.Chamber_Control[1]"));
        Assert.Equal(9u, alarm.MainErrorId);
    }
}
