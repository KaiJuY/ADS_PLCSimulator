using System.Text;
using System.Text.Json;
using AdsPlcSimulator.Host.Api;
using AdsPlcSimulator.Host.Live;
using AdsPlcSimulator.Host.Project;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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

    private async Task RunAlphaAsync() => Assert.Equal(RunOutcome.Started, (await _host.RunAsync("Alpha")).Outcome);

    private static async Task<IResult> SaveAsync(ProjectHost host, LiveHub hub, string name, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return await SimulatorEndpoints.SaveWorkflow(host, hub, name, context.Request);
    }

    [Fact]
    [Trait("AC", "11")]
    public async Task Symbols_while_running_filters_by_query_and_limit()
    {
        await RunAlphaAsync();

        (int? status, string body) = Describe(SimulatorEndpoints.Symbols(_host, "nalpha", null));

        Assert.Equal(200, status);
        Assert.Contains("a.nAlpha", body);
        Assert.DoesNotContain("a.stP", body);
        Assert.Equal("[]", Describe(SimulatorEndpoints.Symbols(_host, "nothing-like-this", 5)).Body);
    }

    [Fact]
    [Trait("AC", "11")]
    public async Task ReadSymbol_while_running_covers_ok_unknown_and_non_scalar()
    {
        await RunAlphaAsync();

        (int? okStatus, string okBody) = Describe(SimulatorEndpoints.ReadSymbol(_host, "a.nAlpha"));
        Assert.Equal(200, okStatus);
        Assert.Contains("7", okBody);
        Assert.Equal(404, Describe(SimulatorEndpoints.ReadSymbol(_host, "a.nMissing")).Status);
        (int? structStatus, string structBody) = Describe(SimulatorEndpoints.ReadSymbol(_host, "a.stP"));
        Assert.Equal(400, structStatus);
        Assert.Contains("not a scalar", structBody);
    }

    [Fact]
    [Trait("AC", "11")]
    public async Task WriteSymbol_while_running_changes_the_value()
    {
        await RunAlphaAsync();

        IResult result = SimulatorEndpoints.WriteSymbol(_host, new SimulatorEndpoints.SymbolWriteRequest("a.nAlpha", "42"));

        Assert.Equal(200, Describe(result).Status);
        Assert.Contains("42", Describe(SimulatorEndpoints.ReadSymbol(_host, "a.nAlpha")).Body);
    }

    [Theory]
    [Trait("AC", "11")]
    [InlineData("a.nMissing", "1", 404)]
    [InlineData("a.stP", "1", 400)]
    [InlineData("a.nAlpha", "abc", 400)]
    public async Task WriteSymbol_while_running_rejects_bad_requests(string path, string value, int expected)
    {
        await RunAlphaAsync();

        IResult result = SimulatorEndpoints.WriteSymbol(_host, new SimulatorEndpoints.SymbolWriteRequest(path, value));

        Assert.Equal(expected, Describe(result).Status);
        Assert.Contains("7", Describe(SimulatorEndpoints.ReadSymbol(_host, "a.nAlpha")).Body);
    }

    [Fact]
    [Trait("AC", "8")]
    public async Task WriteSymbol_and_SaveWorkflow_return_409_while_stopped()
    {
        Assert.Equal(409, Describe(SimulatorEndpoints.WriteSymbol(_host, new SimulatorEndpoints.SymbolWriteRequest("a.nAlpha", "1"))).Status);
        Assert.Equal(409, Describe(await SaveAsync(_host, _root.Hub, "wf", "{}")).Status);
    }

    [Fact]
    [Trait("AC", "11")]
    public async Task ReadWorkflow_while_running_covers_invalid_missing_and_existing()
    {
        await RunAlphaAsync();

        Assert.Equal(400, Describe(SimulatorEndpoints.ReadWorkflow(_host, "bad name")).Status);
        Assert.Equal(404, Describe(SimulatorEndpoints.ReadWorkflow(_host, "wf")).Status);

        await SaveAsync(_host, _root.Hub, "wf", "{\"nodes\":[],\"links\":[]}");

        Assert.True(Describe(SimulatorEndpoints.ReadWorkflow(_host, "wf")).Status is null or 200);
    }

    [Theory]
    [Trait("AC", "11")]
    [InlineData("bad name", "{}", 400)]
    [InlineData("wf", "not json", 400)]
    [InlineData("wf", "null", 400)]
    [InlineData("wf", "{\"nodes\":[],\"links\":[]}", 200)]
    public async Task SaveWorkflow_while_running_validates_name_and_body(string name, string body, int expected)
    {
        await RunAlphaAsync();

        IResult result = await SaveAsync(_host, _root.Hub, name, body);

        Assert.Equal(expected, Describe(result).Status);
        string file = Path.Combine(_root.ProjectPath("Alpha"), "workflows", name + ".flow.json");
        Assert.Equal(expected == 200, File.Exists(file));
        if (expected == 200)
            Assert.Contains(_host.Current!.Workflows, status => status.Name == name && status.Loaded);
    }

    [Fact]
    [Trait("AC", "11")]
    public void MapSimulatorApi_registers_the_api_and_live_routes()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(_host);
        builder.Services.AddSingleton(_root.Hub);
        WebApplication app = builder.Build();

        app.MapSimulatorApi();

        string[] routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ToArray();
        string[] expected =
        [
            "/api/nodes", "/api/symbols", "/api/symbols/value", "/api/workflows", "/api/workflows/{name}",
            "/api/project", "/api/project/run", "/api/project/stop", "/live",
        ];
        Assert.All(expected, route => Assert.Contains(route, routes));
    }
}
