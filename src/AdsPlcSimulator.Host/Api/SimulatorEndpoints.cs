using System.Text.Json;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Host.Live;
using AdsPlcSimulator.Host.Project;

namespace AdsPlcSimulator.Host.Api;

/// <summary>HTTP API used by the editor and the simulation page.</summary>
internal static class SimulatorEndpoints
{
    private const int MaxSymbolResults = 500;

    public static void MapSimulatorApi(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api");

        api.MapGet("/nodes", () => NodeCatalog.Default.Definitions);
        api.MapGet("/symbols", Symbols);
        api.MapGet("/symbols/value", ReadSymbol);
        api.MapPut("/symbols/value", WriteSymbol);
        api.MapGet("/workflows", Workflows);
        api.MapGet("/workflows/{name}", ReadWorkflow);
        api.MapPut("/workflows/{name}", SaveWorkflow);
        api.MapProjectApi();

        app.Map("/live", async (HttpContext context, LiveHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.ServeAsync(socket, context.RequestAborted);
        });
    }

    internal static IResult Symbols(ProjectHost host, string? query, int? limit)
    {
        if (host.Current is not { } session)
            return ProjectEndpoints.NotRunning();

        return Results.Ok(session.Program.Symbols.EnumerateScalars()
            .Where(symbol => string.IsNullOrEmpty(query) || symbol.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit ?? 100, 1, MaxSymbolResults))
            .Select(symbol => new { symbol.Path, Type = symbol.Type.Name }));
    }

    internal static IResult ReadSymbol(ProjectHost host, string path)
    {
        if (host.Current is not { } session)
            return ProjectEndpoints.NotRunning();

        if (!session.Program.Symbols.TryResolve(path, out SymbolRef symbol, out string? error))
            return Results.NotFound(new { error });
        if (!symbol.Type.IsScalar)
            return Results.BadRequest(new { error = $"'{symbol.Path}' is {symbol.Type.Name}, not a scalar." });
        return Results.Ok(new { symbol.Path, Type = symbol.Type.Name, Value = session.Image.ReadValue(symbol) });
    }

    // The simulator owns the process image, so the editor writes it directly instead of going through ADS.
    // The write counts as a client write: it stands in for an HMI and must fire On Client Write triggers.
    internal static IResult WriteSymbol(ProjectHost host, SymbolWriteRequest request)
    {
        if (host.Current is not { } session)
            return ProjectEndpoints.NotRunning();

        if (!session.Program.Symbols.TryResolve(request.Path, out SymbolRef symbol, out string? error))
            return Results.NotFound(new { error });
        if (!symbol.Type.IsScalar)
            return Results.BadRequest(new { error = $"'{symbol.Path}' is {symbol.Type.Name}, not a scalar." });

        try
        {
            session.Image.WriteValue(symbol, request.Value, WriteSource.Client);
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException or ArgumentException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        return Results.Ok(new { symbol.Path, Type = symbol.Type.Name, Value = session.Image.ReadValue(symbol) });
    }

    internal static IResult Workflows(ProjectHost host)
    {
        if (host.Current is not { } session)
            return ProjectEndpoints.NotRunning();

        return Results.Ok(session.Workflows);
    }

    internal static IResult ReadWorkflow(ProjectHost host, string name)
    {
        if (host.Current is not { } session)
            return ProjectEndpoints.NotRunning();

        if (!SimulatorSession.IsValidWorkflowName(name))
            return Results.BadRequest(new { error = "Invalid workflow name." });
        string? json = session.ReadWorkflow(name);
        return json is null ? Results.NotFound() : Results.Text(json, "application/json");
    }

    internal static async Task<IResult> SaveWorkflow(ProjectHost host, LiveHub hub, string name, HttpRequest request)
    {
        if (host.Current is not { } session)
            return ProjectEndpoints.NotRunning();

        if (!SimulatorSession.IsValidWorkflowName(name))
            return Results.BadRequest(new { error = "Workflow name may only contain letters, digits, '_' and '-'." });

        WorkflowDocument document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<WorkflowDocument>(request.Body, WorkflowDocument.JsonOptions)
                ?? throw new JsonException("Empty body.");
        }
        catch (JsonException exception)
        {
            return Results.BadRequest(new { error = $"Invalid workflow JSON: {exception.Message}" });
        }

        WorkflowStatus status = session.SaveWorkflow(name, document);
        // Stryker disable once Statement: the broadcast goes to connected editors, and a unit test has no WebSocket client
        hub.PublishWorkflowStatus(status);
        return Results.Ok(status);
    }

    /// <param name="Value">Text as typed in the editor: TRUE/FALSE, a number, an enum value name or a string.</param>
    internal sealed record SymbolWriteRequest(string Path, string Value);
}
