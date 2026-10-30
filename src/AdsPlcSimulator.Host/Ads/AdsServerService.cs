using AdsPlcSimulator.Host.Project;
using Microsoft.Extensions.Options;
using TwinCAT.Ads;
using TwinCAT.Ads.TcpRouter;

namespace AdsPlcSimulator.Host.Ads;

public enum AdsRouterMode
{
    /// <summary>Register at the AMS router of an installed TwinCAT (XAE/XAR/ADS setup).</summary>
    Local,

    /// <summary>Start the .NET AmsTcpIpRouter in-process, for machines without TwinCAT.</summary>
    Embedded,
}

public sealed class AdsOptions
{
    /// <summary>AMS port of the simulated PLC runtime. Not 851: that is the first runtime of a real TwinCAT PLC on the same router.</summary>
    public ushort Port { get; set; } = 25860;

    public AdsRouterMode Router { get; set; } = AdsRouterMode.Local;

    /// <summary>AMS NetId of the embedded router. Ignored in <see cref="AdsRouterMode.Local"/>.</summary>
    public string NetId { get; set; } = "1.1.1.1.1.1";
}

internal sealed class AdsServerService(
    SimulatorSession session,
    IOptions<AdsOptions> options,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AdsServerService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AdsOptions settings = options.Value;
        AmsTcpIpRouter? router = null;
        Task routerTask = Task.CompletedTask;
        if (settings.Router == AdsRouterMode.Embedded)
        {
            router = new AmsTcpIpRouter(AmsNetId.Parse(settings.NetId), loggerFactory);
            routerTask = router.StartAsync(stoppingToken);
            while (!router.IsRunning && !routerTask.IsCompleted)
                await Task.Delay(100, stoppingToken);
            await ThrowIfFaulted(routerTask, "The embedded AMS router could not start (is TwinCAT's router already using TCP 48898?)");
        }

        using var server = new PlcAdsServer(settings.Port, session.Program.Symbols, session.Image, loggerFactory);
        try
        {
            _logger.LogInformation("Simulated PLC listening on AMS port {Port} via {Router} router.", settings.Port, settings.Router);
            await server.ConnectServerAndWaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "ADS server on port {Port} failed. Is another runtime already using this AMS port?", settings.Port);
            throw;
        }
        finally
        {
            router?.Stop();
        }

        await routerTask.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private static async Task ThrowIfFaulted(Task task, string message)
    {
        if (!task.IsCompleted)
            return;
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(message, exception);
        }
    }
}
