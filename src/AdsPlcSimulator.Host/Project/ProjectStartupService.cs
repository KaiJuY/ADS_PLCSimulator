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
            // Stryker disable once Statement: the embedded router cannot run beside the TwinCAT router (TCP 48898), so tests use Local mode, where this has no effect
            await StartRouterAsync(cancellationToken);
            await RunProjectPathAsync();
        }
        catch
        {
            // Stryker disable once Statement: the embedded router cannot run beside the TwinCAT router (TCP 48898), so tests use Local mode, where this has no effect
            await StopRouterAsync();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await host.DisposeAsync();
        // Stryker disable once Statement: the embedded router cannot run beside the TwinCAT router (TCP 48898), so tests use Local mode, where this has no effect
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
        // Stryker disable once Statement: the embedded router cannot run beside the TwinCAT router (TCP 48898), so tests use Local mode, where this has no effect
        await WaitUntilRunningAsync(() => router.IsRunning, _routerTask, cancellationToken);
    }

    internal static async Task WaitUntilRunningAsync(Func<bool> isRunning, Task routerTask, CancellationToken cancellationToken)
    {
        while (!isRunning() && !routerTask.IsCompleted)
            // Stryker disable once Statement: the delay only paces the polling loop
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

    // Stryker disable all: the embedded router cannot run beside the TwinCAT router (TCP 48898), so tests use Local mode, where this has no effect
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
    // Stryker restore all
}
