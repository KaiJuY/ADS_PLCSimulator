using AdsPlcSimulator.Host.Api;
using AdsPlcSimulator.Host.Project;
using AdsPlcSimulator.Tests.Project;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AdsPlcSimulator.Tests.Api;

public sealed class CrossOriginGuardTests
{
    private static HttpRequest Request(string method, string? origin)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:5259");
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        return context.Request;
    }

    [Theory]
    [Trait("AC", "1")]
    [InlineData("POST", "http://localhost:8765")]
    [InlineData("PUT", "http://evil.example")]
    [InlineData("DELETE", "https://localhost:5259")]
    [InlineData("POST", "null")]
    public void A_state_change_from_another_origin_is_rejected(string method, string origin)
    {
        Assert.True(CrossOriginGuard.IsRejected(Request(method, origin)));
    }

    [Theory]
    [Trait("AC", "2")]
    [InlineData("http://localhost:5259")]
    [InlineData("HTTP://LOCALHOST:5259")]
    public void A_state_change_from_the_same_origin_passes(string origin)
    {
        Assert.False(CrossOriginGuard.IsRejected(Request("POST", origin)));
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_state_change_without_an_origin_passes()
    {
        Assert.False(CrossOriginGuard.IsRejected(Request("POST", null)));
    }

    [Theory]
    [Trait("AC", "4")]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void A_read_from_another_origin_passes(string method)
    {
        Assert.False(CrossOriginGuard.IsRejected(Request(method, "http://localhost:8765")));
    }

    [Fact]
    [Trait("AC", "1")]
    public async Task The_filter_returns_403_and_does_not_call_the_endpoint()
    {
        var context = new DefaultEndpointFilterInvocationContext(Request("POST", "http://localhost:8765").HttpContext);
        bool called = false;

        object? result = await CrossOriginGuard.InvokeAsync(context, _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Contains(CrossOriginGuard.Error, System.Text.Json.JsonSerializer.Serialize(((IValueHttpResult)result!).Value));
    }

    [Fact]
    [Trait("AC", "2")]
    public async Task The_filter_calls_the_endpoint_for_the_same_origin()
    {
        var context = new DefaultEndpointFilterInvocationContext(Request("POST", "http://localhost:5259").HttpContext);
        object marker = new();

        object? result = await CrossOriginGuard.InvokeAsync(context, _ => ValueTask.FromResult<object?>(marker));

        Assert.Same(marker, result);
    }

    [Theory]
    [Trait("AC", "1")]
    [InlineData("http://localhost:8765", 403)]
    [InlineData(null, 200)]
    public async Task The_api_group_applies_the_guard_to_project_stop(string? origin, int expected)
    {
        using var root = new ProjectTestRoot();
        await using ProjectHost host = root.CreateHost();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(host);
        await using WebApplication app = builder.Build();
        app.MapSimulatorApi();
        RouteEndpoint stop = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/project/stop");

        HttpContext context = Request("POST", origin).HttpContext;
        context.RequestServices = app.Services;
        context.Response.Body = new MemoryStream();
        await stop.RequestDelegate!(context);

        Assert.Equal(expected, context.Response.StatusCode);
    }
}
