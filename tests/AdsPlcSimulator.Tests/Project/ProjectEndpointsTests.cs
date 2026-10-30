using System.Text.Json;
using AdsPlcSimulator.Host.Api;
using AdsPlcSimulator.Host.Project;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AdsPlcSimulator.Tests.Project;

[Collection("AdsPort25855")]
[Trait("Category", "Ads")]
public sealed class ProjectEndpointsTests : IAsyncLifetime
{
    private readonly ProjectTestRoot _root = new();
    private readonly ProjectHost _host;

    public ProjectEndpointsTests()
    {
        _host = _root.CreateHost();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _root.Dispose();
    }

    private static (int? Status, string Body) Describe(IResult result)
    {
        int? status = (result as IStatusCodeHttpResult)?.StatusCode;
        object? value = (result as IValueHttpResult)?.Value;
        return (status, JsonSerializer.Serialize(value));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Status_while_stopped_returns_200_with_state_stopped()
    {
        IResult result = ProjectEndpoints.Status(_host);

        var ok = Assert.IsType<Ok<ProjectStatus>>(result);
        Assert.Equal(ProjectState.Stopped, ok.Value!.State);
        Assert.Equal(["Alpha", "Beta", "Broken"], ok.Value.Projects);
    }

    [Theory]
    [Trait("AC", "5")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Missing")]
    public async Task Run_of_a_missing_project_returns_404(string? name)
    {
        IResult result = await ProjectEndpoints.Run(_host, name is null ? null : new ProjectEndpoints.RunRequest(name));

        (int? status, string body) = Describe(result);
        Assert.Equal(404, status);
        Assert.Contains("Unknown project", body);
    }

    [Fact]
    [Trait("AC", "5")]
    public async Task Run_without_a_body_returns_404()
    {
        IResult result = await ProjectEndpoints.Run(_host, null);

        Assert.Equal(404, Describe(result).Status);
    }

    [Fact]
    [Trait("AC", "5")]
    public async Task Run_of_a_broken_project_returns_400_with_the_error_text()
    {
        IResult result = await ProjectEndpoints.Run(_host, new ProjectEndpoints.RunRequest("Broken"));

        (int? status, string body) = Describe(result);
        Assert.Equal(400, status);
        Assert.Contains("declares no global variables", body);
    }

    [Fact]
    [Trait("AC", "5")]
    [Trait("AC", "8")]
    public async Task Run_then_run_again_returns_200_then_409_and_stop_returns_200()
    {
        IResult first = await ProjectEndpoints.Run(_host, new ProjectEndpoints.RunRequest("Alpha"));
        Assert.Equal(200, Describe(first).Status);
        Assert.Equal(200, Describe(SimulatorEndpoints.Workflows(_host)).Status);

        (int? status, string body) = Describe(await ProjectEndpoints.Run(_host, new ProjectEndpoints.RunRequest("Beta")));
        Assert.Equal(409, status);
        Assert.Contains("Stop it first", body);

        IResult stopped = await ProjectEndpoints.Stop(_host);
        Assert.Equal(200, Describe(stopped).Status);
        Assert.Equal(ProjectState.Stopped, _host.GetStatus().State);
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task Stop_while_stopped_returns_200()
    {
        IResult result = await ProjectEndpoints.Stop(_host);

        Assert.Equal(200, Describe(result).Status);
    }

    [Fact]
    [Trait("AC", "8")]
    public void NotRunning_is_409_with_the_error_text()
    {
        (int? status, string body) = Describe(ProjectEndpoints.NotRunning());

        Assert.Equal(409, status);
        Assert.Contains("No project is running.", body);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Workflow_and_symbol_endpoints_return_409_while_stopped()
    {
        Assert.Equal(409, Describe(SimulatorEndpoints.Workflows(_host)).Status);
        Assert.Equal(409, Describe(SimulatorEndpoints.Symbols(_host, null, null)).Status);
        Assert.Equal(409, Describe(SimulatorEndpoints.ReadSymbol(_host, "a.nAlpha")).Status);
        Assert.Equal(409, Describe(SimulatorEndpoints.ReadWorkflow(_host, "wf")).Status);
    }
}
