using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Host.Ads;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.Ads;

namespace AdsPlcSimulator.Tests.Ads;

/// <summary>
/// End-to-end over real ADS with the Polar2Chamber project: needs the AMS router of a local TwinCAT installation.
/// </summary>
[Trait("Category", "Ads")]
public sealed class Polar2AdsTests : IAsyncLifetime
{
    private const ushort TestPort = 25854;
    private const string Shutter = "GVL_Module.Chamber_Control[1]";

    private readonly CancellationTokenSource _stop = new();
    private readonly AdsClient _client = new();
    private PlcAdsServer _server = null!;
    private Task _serverTask = null!;
    private FlowRuntime _runtime = null!;

    public async Task InitializeAsync()
    {
        PlcProgram program = PlcProgram.LoadDirectory(Path.Combine(TestPaths.Polar2Project, "plc"));
        ProcessImage image = program.CreateProcessImage();
        _server = new PlcAdsServer(TestPort, program.Symbols, image, NullLoggerFactory.Instance);
        _serverTask = _server.ConnectServerAndWaitAsync(_stop.Token);
        while (!_server.IsConnected)
        {
            if (_serverTask.IsCompleted)
                throw new InvalidOperationException($"ADS server did not register port {TestPort}; is the TwinCAT router running?");
            await Task.Delay(50);
        }

        _client.Connect(AmsNetId.Local, TestPort);
        _runtime = new FlowRuntime(image, program.Symbols, NodeCatalog.Default, new NullMonitor(), new NoSimulation(), TestPaths.Polar2Project);
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        _client.Dispose();
        await _stop.CancelAsync();
        await _serverTask.ContinueWith(_ => { }, TaskScheduler.Default);
        _server.Dispose();
    }

    [Fact]
    [Trait("AC", "14")]
    public async Task Polar2_shutter_open_handshake_over_ads()
    {
        WorkflowDocument host = WorkflowDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Polar2Project, "workflows", "Polar2_ServiceHost.flow.json")));
        host.Nodes.Single(node => node.Type == "service.host").Params["defaultDelayMs"] = "200";
        Assert.Empty(_runtime.Load("host", host));

        await WaitUntilAsync(() => ReadState() == PackMLState.Idle, "Shutter.Open to be Idle");
        _client.WriteValue($"{Shutter}.ServiceCtrl.Shutter.Open.RequestId", 7u);
        _client.WriteValue($"{Shutter}.ServiceCtrl.Shutter.Open.PackMLIn.eCommand", (short)PackMLCommand.Start);
        _client.WriteValue($"{Shutter}.ServiceCtrl.Shutter.Open.PackMLIn.bCommandChangeRequest", true);

        await WaitUntilAsync(() => ReadState() == PackMLState.Complete, "Shutter.Open to be Complete");

        Assert.Equal(7u, _client.ReadValue($"{Shutter}.ServiceStatus.Shutter.Open.ResponseId", typeof(uint)));
        Assert.Equal(true, _client.ReadValue($"{Shutter}.ServiceStatus.Shutter.Open.PackMLOut.bCommandAccepted", typeof(bool)));
    }

    private PackMLState ReadState() =>
        (PackMLState)Convert.ToInt64(_client.ReadValue($"{Shutter}.ServiceStatus.Shutter.Open.PackMLOut.eStateCurrent", typeof(short)));

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    private sealed class NullMonitor : IFlowMonitor
    {
        public void Publish(FlowEvent flowEvent)
        {
        }
    }

    private sealed class NoSimulation : ISimulationSink
    {
        public void Invoke(string target, string action, object? value)
        {
        }
    }
}
