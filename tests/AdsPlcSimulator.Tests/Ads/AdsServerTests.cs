using System.Collections.Concurrent;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Host.Ads;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace AdsPlcSimulator.Tests.Ads;

/// <summary>
/// End-to-end over real ADS: needs the AMS router of a local TwinCAT installation (XAE, XAR or ADS setup).
/// </summary>
[Trait("Category", "Ads")]
public sealed class AdsServerTests(AdsServerTests.SimulatedPlc plc) : IClassFixture<AdsServerTests.SimulatedPlc>
{
    private PlcProgram _program => plc.Program;

    private ProcessImage _image => plc.Image;

    private AdsClient _client => plc.Client;

    /// <summary>One server for the whole class: re-registering the same AMS port per test races its release.</summary>
    public sealed class SimulatedPlc : IAsyncLifetime
    {
        private const ushort TestPort = 25853;

        private readonly CancellationTokenSource _stop = new();
        private PlcAdsServer _server = null!;
        private Task _serverTask = null!;

        public PlcProgram Program { get; private set; } = null!;

        public ProcessImage Image { get; private set; } = null!;

        public AdsClient Client { get; } = new();

        public async Task InitializeAsync()
        {
            Program = PlcProgram.LoadDirectory(Path.Combine(TestPaths.SampleProject, "plc"));
            Image = Program.CreateProcessImage();
            _server = new PlcAdsServer(TestPort, Program.Symbols, Image, NullLoggerFactory.Instance);
            _serverTask = _server.ConnectServerAndWaitAsync(_stop.Token);
            while (!_server.IsConnected)
            {
                if (_serverTask.IsCompleted)
                    throw new InvalidOperationException($"ADS server did not register port {TestPort}; is the TwinCAT router running?");
                await Task.Delay(50);
            }

            Client.Connect(AmsNetId.Local, TestPort);
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await _stop.CancelAsync();
            await _serverTask.ContinueWith(_ => { }, TaskScheduler.Default);
            _server.Dispose();
        }
    }

    [Fact]
    public void Symbol_loader_sees_the_gvl_with_plc_layout()
    {
        ISymbolLoader loader = SymbolLoaderFactory.Create(_client, SymbolLoaderSettings.Default);

        ISymbol control = loader.Symbols["GVL_Module.Chamber1_Control"];
        IArrayType controlType = (IArrayType)control.DataType!;

        Assert.Equal(_program.Symbols.Resolve("GVL_Module.Chamber1_Control").Size, control.ByteSize);
        Assert.Equal(6, controlType.Dimensions.ElementCount);
        Assert.Equal(AdsState.Run, _client.ReadState().AdsState);
    }

    [Fact]
    public void Read_value_by_name_returns_initial_values()
    {
        Assert.Equal("Chamber1", _client.ReadValue("GVL_Module.Chamber1_Runtime[2].ModuleType", typeof(string)));
        Assert.Equal(true, _client.ReadValue("GVL_Module.Chamber1_Runtime[2].Enabled", typeof(bool)));
    }

    [Fact]
    public void Write_through_variable_handle_lands_in_process_image_at_compiled_offset()
    {
        const string path = "GVL_Module.Chamber1_Control[3].ServiceCtrl.DoorOpen.RequestId";
        uint handle = _client.CreateVariableHandle(path);
        try
        {
            _client.WriteAny(handle, 4711u);
            Assert.Equal(4711u, (uint)_client.ReadAny(handle, typeof(uint)));
        }
        finally
        {
            _client.DeleteVariableHandle(handle);
        }

        Assert.Equal(4711L, _image.ReadValue(_program.Symbols.Resolve(path)));
    }

    [Fact]
    public async Task Client_write_triggers_workflow_whose_result_the_client_reads_back()
    {
        var monitor = new CompletionMonitor();
        await using var runtime = new FlowRuntime(_image, _program.Symbols, NodeCatalog.Default, monitor, new NoSimulation());
        var document = new WorkflowDocument
        {
            Nodes =
            [
                new WorkflowNode { Id = "request", Type = "trigger.onClientWrite", Params = { ["symbol"] = "GVL_Module.Chamber1_Control[1].ServiceCtrl.DoorClose.RequestId" } },
                new WorkflowNode { Id = "respond", Type = "action.writeSymbol", Params = { ["symbol"] = "GVL_Module.Chamber1_Control[1].ServiceStatus.DoorClose.ResponseId" } },
            ],
            Links =
            [
                new WorkflowLink("request", "then", "respond", "exec"),
                new WorkflowLink("request", "value", "respond", "value"),
            ],
        };
        Assert.Empty(runtime.Load("handshake", document));

        _client.WriteValue("GVL_Module.Chamber1_Control[1].ServiceCtrl.DoorClose.RequestId", 12u);
        await monitor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(12u, _client.ReadValue("GVL_Module.Chamber1_Control[1].ServiceStatus.DoorClose.ResponseId", typeof(uint)));
    }

    private sealed class CompletionMonitor : IFlowMonitor
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<FlowEvent> Events { get; } = new();

        public void Publish(FlowEvent flowEvent)
        {
            Events.Enqueue(flowEvent);
            if (flowEvent.Kind == FlowEventKind.RunCompleted)
                Completed.TrySetResult();
            if (flowEvent.Kind == FlowEventKind.RunFailed)
                Completed.TrySetException(new InvalidOperationException(flowEvent.Error));
        }
    }
}
