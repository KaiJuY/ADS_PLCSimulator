using System.Text.Json.Serialization;
using AdsPlcSimulator.Host.Ads;
using AdsPlcSimulator.Host.Api;
using AdsPlcSimulator.Host.Live;
using AdsPlcSimulator.Host.Project;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection("Simulator"));
builder.Services.Configure<AdsOptions>(builder.Configuration.GetSection("Ads"));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<LiveHub>();
builder.Services.AddSingleton(services => new ProjectStateStore(ProjectStateStore.DefaultPath, services.GetRequiredService<ILogger<ProjectStateStore>>()));
builder.Services.AddSingleton<ProjectHost>();
builder.Services.AddHostedService<ProjectStartupService>();

var app = builder.Build();

ProjectHost host = app.Services.GetRequiredService<ProjectHost>();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseFileServer(new FileServerOptions
{
    FileProvider = new RunningProjectFileProvider(host),
    RequestPath = "/simulation",
});

app.MapGet("/", () => Results.Redirect("/editor/"));
app.MapSimulatorApi();

app.Run();
