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

        var router = new AmsTcpIpRouter(AmsNetId.Parse(settings.NetId), loggerFactory);
        _router = router;
        _routerTask = router.StartAsync(_routerStop.Token);
        await WaitUntilRunningAsync(() => router.IsRunning, _routerTask, cancellationToken);
    }

    internal static async Task WaitUntilRunningAsync(Func<bool> isRunning, Task routerTask, CancellationToken cancellationToken)
    {
        while (!isRunning() && !routerTask.IsCompleted)
            await Task.Delay(100, cancellationToken);

        if (isRunning())
            return;
        throw new InvalidOperationException(RouterStartFailure, routerTask.Exception?.GetBaseException());
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
