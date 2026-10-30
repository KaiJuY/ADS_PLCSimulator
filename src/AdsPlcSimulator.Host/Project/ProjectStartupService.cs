using AdsPlcSimulator.Host.Ads;
using Microsoft.Extensions.Options;
using TwinCAT.Ads;
using TwinCAT.Ads.TcpRouter;

namespace AdsPlcSimulator.Host.Project;

internal sealed class ProjectStartupService(
    ProjectHost host,
    IOptions<SimulatorOptions> options,
    IOptions<AdsOptions> adsOptions,
    IHostEnvironment environment,
    ILoggerFactory loggerFactory) : IHostedService
{
    private const string RouterStartFailure = "The embedded AMS router could not start (is TwinCAT's router already using TCP 48898?)";

    private readonly CancellationTokenSource _routerStop = new();
    private AmsTcpIpRouter? _router;
    private Task _routerTask = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartRouterAsync(cancellationToken);
            await RunProjectPathAsync();
        }
        catch
        {
            await StopRouterAsync();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await host.DisposeAsync();
        await StopRouterAsync();
    }

    private async Task StartRouterAsync(CancellationToken cancellationToken)
    {
        AdsOptions settings = adsOptions.Value;
        if (settings.Router != AdsRouterMode.Embedded)
            return;

        _router = new AmsTcpIpRouter(AmsNetId.Parse(settings.NetId), loggerFactory);
        _routerTask = _router.StartAsync(_routerStop.Token);
        while (!_router.IsRunning && !_routerTask.IsCompleted)
            await Task.Delay(100, cancellationToken);

        if (_router.IsRunning)
            return;
        throw new InvalidOperationException(RouterStartFailure, _routerTask.Exception?.GetBaseException());
    }

    private async Task RunProjectPathAsync()
    {
        string? projectPath = options.Value.ProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath))
            return;

        RunResult result = await host.RunPathAsync(Path.GetFullPath(Path.Combine(environment.ContentRootPath, projectPath)));
        if (result.Outcome == RunOutcome.Failed)
            throw new InvalidOperationException(result.Error);
    }

    private async Task StopRouterAsync()
    {
        _router?.Stop();
        await _routerStop.CancelAsync();
        try
        {
            await _routerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
    }
}
