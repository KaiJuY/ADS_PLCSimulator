using System.Diagnostics;
using System.Text.Json;
using AdsPlcSimulator.Host.Project;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.Ads;

namespace AdsPlcSimulator.Tests.Project;

[Collection("AdsPort25855")]
[Trait("Category", "Ads")]
public sealed class ProjectHostTests : IAsyncLifetime
{
    private readonly ProjectTestRoot _root = new();
    private readonly List<ProjectHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (ProjectHost host in _hosts)
            await host.DisposeAsync();
        _root.Dispose();
    }

    private ProjectHost NewHost(
        TimeSpan? registerTimeout = null,
        TimeSpan? stopTimeout = null,
        Func<AdsPlcSimulator.Host.Ads.PlcAdsServer, CancellationToken, Task>? connect = null,
        Func<SimulatorSession, ValueTask>? disposeSession = null,
        string? projectsRoot = null)
    {
        ProjectHost host = _root.CreateHost(registerTimeout, stopTimeout, connect, disposeSession, projectsRoot);
        _hosts.Add(host);
        return host;
    }

    [Fact]
    [Trait("AC", "4")]
    public void ListProjects_returns_the_folders_with_a_plc_folder_sorted()
    {
        ProjectHost host = NewHost();

        Assert.Equal(["Alpha", "Beta", "Broken"], host.ListProjects());
    }

    [Fact]
    [Trait("AC", "4")]
    public void ListProjects_returns_empty_when_the_root_does_not_exist()
    {
        ProjectHost host = NewHost(projectsRoot: Path.Combine(_root.Root, "missing"));

        Assert.Empty(host.ListProjects());
    }

    [Fact]
    [Trait("AC", "2")]
    [Trait("AC", "4")]
    public void New_host_is_stopped_and_does_not_register_the_port()
    {
        ProjectHost host = NewHost();

        ProjectStatus status = host.GetStatus();

        Assert.Equal(ProjectState.Stopped, status.State);
        Assert.Null(status.Running);
        Assert.Equal(ProjectTestRoot.Port, status.Port);
        Assert.Equal(AdsErrorCode.TargetPortNotFound, ProjectTestRoot.ReadState());
    }

    [Fact]
    [Trait("AC", "5")]
    [Trait("AC", "10")]
    [Trait("AC", "13")]
    public async Task Run_registers_the_project_and_keeps_the_port_after_the_register_timeout()
    {
        ProjectHost host = NewHost(registerTimeout: TimeSpan.FromMilliseconds(200));

        RunResult result = await host.RunAsync("Alpha");
        await Task.Delay(500);

        Assert.Equal(RunOutcome.Started, result.Outcome);
        Assert.Equal((AdsErrorCode.NoError, (object)(short)7), ProjectTestRoot.ReadSymbol("a.nAlpha", typeof(short)));
        Assert.Equal(1, ProjectTestRoot.ReadSymbolVersion());
        using JsonDocument state = JsonDocument.Parse(File.ReadAllText(_root.StatePath));
        Assert.Equal("Alpha", state.RootElement.GetProperty("lastProject").GetString());
        ProjectStatus status = host.GetStatus();
        Assert.Equal("Alpha", status.Last);
        Assert.Equal("Alpha", status.Running);
        Assert.Equal(ProjectState.Running, status.State);
    }

    [Fact]
    [Trait("AC", "5")]
    [Trait("AC", "7")]
    public async Task Run_while_running_returns_AlreadyRunning()
    {
        ProjectHost host = NewHost();
        await host.RunAsync("Alpha");

        RunResult second = await host.RunAsync("Beta");

        Assert.Equal(RunOutcome.AlreadyRunning, second.Outcome);
        Assert.Equal("Alpha", host.GetStatus().Running);
    }

    [Fact]
    [Trait("AC", "7")]
    public async Task Two_concurrent_runs_start_one_project_and_refuse_the_other()
    {
        ProjectHost host = NewHost();

        RunResult[] results = await Task.WhenAll(host.RunAsync("Alpha"), host.RunAsync("Alpha"));

        Assert.Equal([RunOutcome.Started, RunOutcome.AlreadyRunning], results.Select(result => result.Outcome).Order());
    }

    [Fact]
    [Trait("AC", "6")]
    [Trait("AC", "10")]
    public async Task Stop_releases_the_port_and_the_next_run_serves_the_new_project_with_a_higher_symbol_version()
    {
        ProjectHost host = NewHost();
        await host.RunAsync("Alpha");

        await host.StopAsync();

        Assert.Equal(AdsErrorCode.TargetPortNotFound, ProjectTestRoot.ReadState());
        Assert.Null(host.Current);

        RunResult beta = await host.RunAsync("Beta");

        Assert.Equal(RunOutcome.Started, beta.Outcome);
        Assert.Equal(2, ProjectTestRoot.ReadSymbolVersion());
        Assert.Equal(AdsErrorCode.NoError, ProjectTestRoot.ReadSymbol("b.nBeta", typeof(int)).Error);
        Assert.Equal(AdsErrorCode.DeviceSymbolNotFound, ProjectTestRoot.ReadSymbol("a.nAlpha", typeof(short)).Error);
    }

    [Theory]
    [Trait("AC", "5")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Missing")]
    [InlineData("NoPlc")]
    public async Task Run_of_an_unknown_project_returns_UnknownProject_and_stays_stopped(string? name)
    {
        ProjectHost host = NewHost();

        RunResult result = await host.RunAsync(name);

        Assert.Equal(RunOutcome.UnknownProject, result.Outcome);
        Assert.Equal(ProjectState.Stopped, host.GetStatus().State);
    }

    [Fact]
    [Trait("AC", "5")]
    [Trait("AC", "9")]
    public async Task Run_of_a_project_without_variables_fails_logs_and_does_not_count_a_run()
    {
        ProjectHost host = NewHost();

        RunResult result = await host.RunAsync("Broken");

        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Contains("declares no global variables", result.Error);
        Assert.Equal(ProjectState.Stopped, host.GetStatus().State);
        Assert.Equal(AdsErrorCode.TargetPortNotFound, ProjectTestRoot.ReadState());
        Assert.Contains(_root.Logs, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Broken"));

        await host.RunAsync("Alpha");

        Assert.Equal(1, ProjectTestRoot.ReadSymbolVersion());
    }

    [Fact]
    [Trait("AC", "5")]
    [Trait("AC", "9")]
    public async Task Run_fails_when_another_server_holds_the_port_and_works_after_it_is_released()
    {
        ProjectHost host = NewHost();
        var occupant = ProjectTestRoot.CreateOccupant();
        await occupant.ConnectServerAsync(CancellationToken.None);

        RunResult failed;
        try
        {
            failed = await host.RunAsync("Alpha");
        }
        finally
        {
            occupant.Disconnect();
            occupant.Dispose();
        }

        Assert.Equal(RunOutcome.Failed, failed.Outcome);
        Assert.Contains("25855", failed.Error);
        Assert.Equal(ProjectState.Stopped, host.GetStatus().State);
        Assert.Contains(_root.Logs, entry => entry.Level == LogLevel.Error && entry.Message.Contains("25855"));

        RunResult retry = await host.RunAsync("Alpha");

        Assert.Equal(RunOutcome.Started, retry.Outcome);
    }

    [Fact]
    [Trait("AC", "5")]
    public async Task Run_gives_up_when_the_registration_never_completes_and_then_asks_for_a_restart()
    {
        ProjectHost host = NewHost(
            registerTimeout: TimeSpan.FromMilliseconds(200),
            connect: (_, _) => new TaskCompletionSource().Task);
        var clock = Stopwatch.StartNew();

        RunResult first = await host.RunAsync("Alpha");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(RunOutcome.Failed, first.Outcome);
        Assert.Contains("25855", first.Error);
        Assert.Equal(ProjectState.Stopped, host.GetStatus().State);

        RunResult second = await host.RunAsync("Alpha");

        Assert.Equal(RunOutcome.Failed, second.Outcome);
        Assert.Contains("Restart the Host", second.Error);
    }

    [Fact]
    [Trait("AC", "6")]
    [Trait("AC", "9")]
    public async Task Stop_returns_when_the_session_does_not_dispose_and_warns()
    {
        ProjectHost host = NewHost(
            stopTimeout: TimeSpan.FromMilliseconds(200),
            disposeSession: _ => new ValueTask(Task.Delay(Timeout.Infinite)));
        await host.RunAsync("Alpha");
        var clock = Stopwatch.StartNew();

        await host.StopAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Null(host.Current);
        Assert.Equal(AdsErrorCode.TargetPortNotFound, ProjectTestRoot.ReadState());
        Assert.Contains(_root.Logs, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("did not stop"));
    }

    [Fact]
    [Trait("AC", "5")]
    [Trait("AC", "9")]
    public async Task Failed_run_returns_when_the_session_does_not_dispose()
    {
        ProjectHost host = NewHost(
            stopTimeout: TimeSpan.FromMilliseconds(200),
            disposeSession: _ => new ValueTask(Task.Delay(Timeout.Infinite)));
        var occupant = ProjectTestRoot.CreateOccupant();
        await occupant.ConnectServerAsync(CancellationToken.None);
        var clock = Stopwatch.StartNew();

        RunResult result;
        try
        {
            result = await host.RunAsync("Alpha");
        }
        finally
        {
            occupant.Disconnect();
            occupant.Dispose();
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(RunOutcome.Failed, result.Outcome);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task Stop_clears_the_simulation_state_and_closes_the_live_channel_of_the_session()
    {
        ProjectHost host = NewHost();
        await host.RunAsync("Alpha");
        SessionLiveChannel live = host.Current!.Live;

        live.Invoke("t", "a", 1);
        Assert.Equal(1, _root.Hub.SimulationStateCount);

        await host.StopAsync();
        Assert.Equal(0, _root.Hub.SimulationStateCount);

        live.Invoke("t", "a", 1);
        Assert.Equal(0, _root.Hub.SimulationStateCount);
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task Stop_while_stopped_does_nothing()
    {
        ProjectHost host = NewHost();

        await host.StopAsync();

        Assert.Equal(ProjectState.Stopped, host.GetStatus().State);
    }

    [Fact]
    [Trait("AC", "3")]
    [Trait("AC", "13")]
    public async Task RunPath_runs_any_folder_without_remembering_it()
    {
        ProjectHost host = NewHost();

        RunResult result = await host.RunPathAsync(_root.ProjectPath("Alpha") + Path.DirectorySeparatorChar);

        Assert.Equal(RunOutcome.Started, result.Outcome);
        Assert.Equal("Alpha", host.GetStatus().Running);
        Assert.False(File.Exists(_root.StatePath));
    }

    [Theory]
    [Trait("AC", "10")]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(255, 255)]
    [InlineData(256, 1)]
    [InlineData(510, 255)]
    [InlineData(511, 1)]
    public void SymbolVersionForRun_cycles_from_1_to_255(int run, int expected)
    {
        Assert.Equal((byte)expected, ProjectHost.SymbolVersionForRun(run));
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task Dispose_releases_the_port_and_later_runs_fail()
    {
        ProjectHost host = NewHost();
        await host.RunAsync("Alpha");

        await host.DisposeAsync();

        Assert.Equal(AdsErrorCode.TargetPortNotFound, ProjectTestRoot.ReadState());
        RunResult result = await host.RunAsync("Alpha");
        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Contains("shutting down", result.Error);
    }

    [Fact]
    [Trait("AC", "14")]
    public void New_host_reports_the_project_remembered_by_an_earlier_host()
    {
        new ProjectStateStore(_root.StatePath, NullLogger<ProjectStateStore>.Instance).WriteLastProject("Beta");

        ProjectStatus status = NewHost().GetStatus();

        Assert.Equal(ProjectState.Stopped, status.State);
        Assert.Equal("Beta", status.Last);
    }

    [Fact]
    [Trait("AC", "4")]
    public void ListProjects_returns_empty_and_warns_when_the_root_is_a_file()
    {
        string file = Path.Combine(_root.Root, "not-a-folder.txt");
        File.WriteAllText(file, "x");
        ProjectHost host = NewHost(projectsRoot: file);

        Assert.Empty(host.ListProjects());
    }

    [Fact]
    [Trait("AC", "9")]
    public async Task Run_proceeds_once_the_previous_registration_has_finished()
    {
        var release = new TaskCompletionSource();
        int calls = 0;
        ProjectHost host = NewHost(
            registerTimeout: TimeSpan.FromMilliseconds(200),
            connect: (server, token) => Interlocked.Increment(ref calls) == 1 ? release.Task : server.ConnectServerAsync(token));

        RunResult first = await host.RunAsync("Alpha");
        Assert.Equal(RunOutcome.Failed, first.Outcome);
        RunResult blocked = await host.RunAsync("Alpha");
        Assert.Contains("still held", blocked.Error);

        release.SetResult();
        await Task.Delay(300);
        RunResult second = await host.RunAsync("Alpha");

        Assert.Equal(RunOutcome.Started, second.Outcome);
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task Stop_completes_and_warns_when_disposing_the_session_throws()
    {
        ProjectHost host = NewHost(disposeSession: _ => throw new InvalidOperationException("boom"));
        await host.RunAsync("Alpha");

        await host.StopAsync();

        Assert.Equal(ProjectState.Stopped, host.GetStatus().State);
        Assert.Contains(_root.Logs, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Disposing Alpha failed"));
    }
}
