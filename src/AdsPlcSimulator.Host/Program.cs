using AdsPlcSimulator.Host.Ads;
using AdsPlcSimulator.Host.Api;
using AdsPlcSimulator.Host.Live;
using AdsPlcSimulator.Host.Project;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.Configure<AdsOptions>(builder.Configuration.GetSection("Ads"));
builder.Services.AddSingleton<LiveHub>();
builder.Services.AddSingleton<SimulatorSession>();
builder.Services.AddHostedService<AdsServerService>();

var app = builder.Build();

// Build the project eagerly so declaration errors stop the process at startup instead of on the first request.
SimulatorSession session = app.Services.GetRequiredService<SimulatorSession>();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();
if (Directory.Exists(session.SimulationDirectory))
{
    app.UseFileServer(new FileServerOptions
    {
        FileProvider = new PhysicalFileProvider(session.SimulationDirectory),
        RequestPath = "/simulation",
    });
}

app.MapGet("/", () => Results.Redirect("/editor/"));
app.MapSimulatorApi();

app.Run();
