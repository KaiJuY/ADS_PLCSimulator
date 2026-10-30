using AdsPlcSimulator.Host.Ads;
using AdsPlcSimulator.Host.Project;
using Microsoft.Extensions.Options;
using TwinCAT.Ads;

namespace AdsPlcSimulator.Tests.Project;

[Collection("AdsPort25855")]
[Trait("Category", "Ads")]
public sealed class ProjectStartupServiceTests : IAsyncLifetime
{
    private readonly ProjectTestRoot _root = new();
    private readonly ProjectHost _host;

    public ProjectStartupServiceTests()
    {
        _host = _root.CreateHost();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _root.Dispose();
    }

    private ProjectStartupService NewService(string? projectPath) =>
        new(
            _host,
            Options.Create(new SimulatorOptions { ProjectPath = projectPath }),
            Options.Create(new AdsOptions { Port = ProjectTestRoot.Port }),
            new TestHostEnvironment(_root.Root),
            _root.LoggerFactory);

    [Fact]
    [Trait("AC", "2")]
    public async Task Start_without_a_project_path_stays_stopped()
    {
        await NewService(null).StartAsync(CancellationToken.None);

        Assert.Equal(ProjectState.Stopped, _host.GetStatus().State);
        Assert.Equal(AdsErrorCode.TargetPortNotFound, ProjectTestRoot.ReadState());
    }

    [Fact]
    [Trait("AC", "3")]
    [Trait("AC", "13")]
    public async Task Start_with_a_project_path_runs_it_without_remembering_it()
    {
        await NewService(_root.ProjectPath("Alpha")).StartAsync(CancellationToken.None);

        Assert.Equal("Alpha", _host.GetStatus().Running);
        Assert.False(File.Exists(_root.StatePath));
    }

    [Fact]
    [Trait("AC", "3")]
    public async Task Start_with_a_broken_project_path_throws_the_declaration_error()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(_root.ProjectPath("Broken")).StartAsync(CancellationToken.None));

        Assert.Contains("declares no global variables", exception.Message);
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task Stop_releases_the_port_and_refuses_later_runs()
    {
        ProjectStartupService service = NewService(_root.ProjectPath("Alpha"));
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(AdsErrorCode.TargetPortNotFound, ProjectTestRoot.ReadState());
        RunResult result = await _host.RunAsync("Alpha");
        Assert.Equal(RunOutcome.Failed, result.Outcome);
        Assert.Contains("shutting down", result.Error);
    }

    [Fact]
    [Trait("AC", "2")]
    public async Task Start_with_the_embedded_router_throws_when_the_router_port_is_taken()
    {
        var service = new ProjectStartupService(
            _host,
            Options.Create(new SimulatorOptions()),
            Options.Create(new AdsOptions { Router = AdsRouterMode.Embedded, Port = ProjectTestRoot.Port }),
            new TestHostEnvironment(_root.Root),
            _root.LoggerFactory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(timeout.Token));

        Assert.Contains("could not start", exception.Message);
        await service.StopAsync(CancellationToken.None);
    }
}
