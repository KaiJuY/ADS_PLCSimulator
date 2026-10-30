using AdsPlcSimulator.Host.Project;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AdsPlcSimulator.Host.Api;

internal static class ProjectEndpoints
{
    public static void MapProjectApi(this RouteGroupBuilder api)
    {
        api.MapGet("/project", Status);
        api.MapPost("/project/run", Run);
        api.MapPost("/project/stop", Stop);
    }

    internal static IResult Status(ProjectHost host) => Results.Ok(host.GetStatus());

    internal static async Task<IResult> Run(ProjectHost host, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RunRequest? request)
    {
        RunResult result = await host.RunAsync(request?.Name);
        return result.Outcome switch
        {
            RunOutcome.Started => Status(host),
            RunOutcome.AlreadyRunning => Results.Conflict(new { error = result.Error }),
            RunOutcome.Failed => Results.BadRequest(new { error = result.Error }),
            _ => Results.NotFound(new { error = $"Unknown project '{request?.Name}'." }),
        };
    }

    internal static async Task<IResult> Stop(ProjectHost host)
    {
        await host.StopAsync();
        return Status(host);
    }

    internal static IResult NotRunning() => Results.Conflict(new { error = "No project is running." });

    internal sealed record RunRequest(string? Name);
}
