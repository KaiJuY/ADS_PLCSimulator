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

    private PlcAdsServer _server => plc.Server;

    /// <summary>One server for the whole class: re-registering the same AMS port per test races its release.</summary>
    public sealed class SimulatedPlc : IAsyncLifetime
    {
        public const ushort Port = 25853;

        private const ushort TestPort = Port;

        private readonly CancellationTokenSource _stop = new();
        private PlcAdsServer _server = null!;
        private Task _serverTask = null!;

        public PlcProgram Program { get; private set; } = null!;

        public ProcessImage Image { get; private set; } = null!;

        public AdsClient Client { get; } = new();

        internal PlcAdsServer Server => _server;

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

    private const string Control = "GVL_Module.Chamber1_Control";
    private const string RequestIdOf3 = "GVL_Module.Chamber1_Control[3].ServiceCtrl.DoorOpen.RequestId";
    private const uint HandleGroup = 0xF005;
    private const uint ReleaseGroup = 0xF006;
    private const uint DataGroup = PlcAdsServer.PlcDataIndexGroup;

    private SymbolRef Sym(string path) => _program.Symbols.Resolve(path);

    private byte[] ImageBytes(int offset, int length) => _image.Read(offset, length);

    private void WithImageBytes(int offset, byte[] bytes, Action body)
    {
        byte[] before = ImageBytes(offset, bytes.Length);
        _image.Write(offset, bytes, WriteSource.Client);
        try
        {
            body();
        }
        finally
        {
            _image.Write(offset, before, WriteSource.Client);
        }
    }

    private static byte[] Sequence(int length, byte start) =>
        Enumerable.Range(0, length).Select(index => (byte)(start + index)).ToArray();

    private void WithHandle(string path, Action<uint> body)
    {
        uint handle = _client.CreateVariableHandle(path);
        try
        {
            body(handle);
        }
        finally
        {
            _client.TryDeleteVariableHandle(handle);
        }
    }

    private AdsErrorCode TryReadBytes(uint indexGroup, uint indexOffset, int length, out byte[] data)
    {
        var result = _client.ReadAsync(indexGroup, indexOffset, length, CancellationToken.None).GetAwaiter().GetResult();
        data = result.Data.ToArray();
        return result.ErrorCode;
    }

    private AdsErrorCode TryWriteBytes(uint indexGroup, uint indexOffset, byte[] data) =>
        _client.TryWrite(indexGroup, indexOffset, data.AsMemory());

    private uint AddNotification(uint indexGroup, uint indexOffset, int length, AdsTransMode mode, out AdsErrorCode result, uint cycleTime = 10)
    {
        result = _client.TryAddDeviceNotification(indexGroup, indexOffset, length, new NotificationSettings(mode, (int)cycleTime, 0), null, out uint handle);
        return handle;
    }

    private static WorkflowDocument RespondOnClientWrite(string requestId, string responseId) => new()
    {
        Nodes =
        [
            new WorkflowNode { Id = "request", Type = "trigger.onClientWrite", Params = { ["symbol"] = requestId } },
            new WorkflowNode { Id = "respond", Type = "action.writeSymbol", Params = { ["symbol"] = responseId } },
        ],
        Links =
        [
            new WorkflowLink("request", "then", "respond", "exec"),
            new WorkflowLink("request", "value", "respond", "value"),
        ],
    };

    [Fact]
    [Trait("AC", "1")]
    public void Handle_read_of_a_root_member_returns_the_first_bytes_of_the_process_image()
    {
        SymbolRef element = Sym($"{Control}[1]");
        WithImageBytes(element.Offset, Sequence(8, 1), () =>
            WithHandle($"{Control}[1]", handle =>
            {
                Assert.Equal(AdsErrorCode.NoError, TryReadBytes(HandleGroup, handle, 8, out byte[] data));
                Assert.Equal(Sequence(8, 1), data);
            }));
    }

    [Fact]
    [Trait("AC", "1")]
    public void Handle_read_of_a_struct_field_returns_its_first_bytes()
    {
        const string path = "GVL_Module.Chamber1_Control[3].ServiceCtrl";
        WithImageBytes(Sym(path).Offset, Sequence(8, 40), () =>
            WithHandle(path, handle =>
            {
                Assert.Equal(AdsErrorCode.NoError, TryReadBytes(HandleGroup, handle, 4, out byte[] data));
                Assert.Equal(Sequence(4, 40), data);
            }));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Handle_read_longer_than_the_symbol_returns_only_the_symbol_bytes()
    {
        SymbolRef element = Sym($"{Control}[1]");
        WithHandle($"{Control}[1]", handle =>
        {
            Assert.Equal(AdsErrorCode.NoError, TryReadBytes(HandleGroup, handle, element.Size + 4, out byte[] data));
            Assert.Equal(ImageBytes(element.Offset, element.Size), data);
        });

        WithHandle(RequestIdOf3, handle =>
        {
            Assert.Equal(AdsErrorCode.NoError, TryReadBytes(HandleGroup, handle, 8, out byte[] data));
            Assert.Equal(4, data.Length);
        });
    }

    [Fact]
    [Trait("AC", "3")]
    public void Handle_read_of_zero_bytes_succeeds_with_no_data()
    {
        WithHandle(RequestIdOf3, handle =>
        {
            Assert.Equal(AdsErrorCode.NoError, TryReadBytes(HandleGroup, handle, 0, out byte[] data));
            Assert.Empty(data);
        });
    }

    [Fact]
    [Trait("AC", "4")]
    public void Raw_read_may_start_inside_a_symbol_and_cross_into_the_next_one()
    {
        SymbolRef first = Sym($"{Control}[1]");
        SymbolRef second = Sym($"{Control}[2]");
        int start = first.Offset + 2;
        int length = second.Offset + 4 - start;
        Assert.True(length > first.Size - 2);

        Assert.Equal(AdsErrorCode.NoError, TryReadBytes(DataGroup, (uint)start, length, out byte[] data));
        Assert.Equal(ImageBytes(start, length), data);

        Assert.Equal(AdsErrorCode.NoError, TryReadBytes(DataGroup, (uint)(_image.Size - 1), 1, out byte[] last));
        Assert.Equal(ImageBytes(_image.Size - 1, 1), last);
    }

    [Fact]
    [Trait("AC", "4")]
    public void Raw_read_beyond_the_process_image_fails_with_invalid_offset()
    {
        Assert.Equal(AdsErrorCode.DeviceInvalidOffset, TryReadBytes(DataGroup, (uint)(_image.Size - 1), 2, out _));
        Assert.Equal(AdsErrorCode.DeviceInvalidOffset, TryReadBytes(DataGroup, uint.MaxValue - 1, 4, out _));
    }

    [Fact]
    [Trait("AC", "5")]
    public void Handle_write_shorter_than_the_symbol_changes_only_the_first_bytes()
    {
        SymbolRef field = Sym(RequestIdOf3);
        WithImageBytes(field.Offset, [0xAA, 0xBB, 0xCC, 0xDD], () =>
            WithHandle(RequestIdOf3, handle =>
            {
                Assert.Equal(AdsErrorCode.NoError, TryWriteBytes(HandleGroup, handle, [0x01, 0x02]));
                Assert.Equal(new byte[] { 0x01, 0x02, 0xCC, 0xDD }, ImageBytes(field.Offset, 4));
            }));
    }

    [Fact]
    [Trait("AC", "5")]
    public async Task Handle_write_of_zero_bytes_succeeds_without_triggering_on_client_write()
    {
        const string requestId = "GVL_Module.Chamber1_Control[2].ServiceCtrl.DoorClose.RequestId";
        var monitor = new CompletionMonitor();
        await using var runtime = new FlowRuntime(_image, _program.Symbols, NodeCatalog.Default, monitor, new NoSimulation());
        Assert.Empty(runtime.Load("zero-write", RespondOnClientWrite(requestId, "GVL_Module.Chamber1_Control[2].ServiceStatus.DoorClose.ResponseId")));

        WithHandle(requestId, handle => Assert.Equal(AdsErrorCode.NoError, TryWriteBytes(HandleGroup, handle, [])));
        await Task.Delay(300);
        Assert.False(monitor.Completed.Task.IsCompleted);
        Assert.Empty(monitor.Events);

        WithHandle(requestId, handle => Assert.Equal(AdsErrorCode.NoError, TryWriteBytes(HandleGroup, handle, BitConverter.GetBytes(77u))));
        await monitor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("AC", "6")]
    public void Handle_write_longer_than_the_symbol_fails_and_leaves_the_neighbour_unchanged()
    {
        SymbolRef field = Sym(RequestIdOf3);
        byte[] neighbour = Sequence(4, 90);
        WithImageBytes(field.Offset, [1, 2, 3, 4, .. neighbour], () =>
            WithHandle(RequestIdOf3, handle =>
            {
                Assert.Equal(AdsErrorCode.DeviceInvalidSize, TryWriteBytes(HandleGroup, handle, [9, 9, 9, 9, 9, 9, 9, 9]));
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, ImageBytes(field.Offset, 4));
                Assert.Equal(neighbour, ImageBytes(field.Offset + 4, 4));
            }));
    }

    [Fact]
    [Trait("AC", "7")]
    public async Task Raw_write_across_two_symbols_changes_both_and_triggers_on_client_write()
    {
        const string requestId = "GVL_Module.Chamber1_Control[4].ServiceCtrl.DoorClose.RequestId";
        SymbolRef field = Sym(requestId);
        var monitor = new CompletionMonitor();
        await using var runtime = new FlowRuntime(_image, _program.Symbols, NodeCatalog.Default, monitor, new NoSimulation());
        Assert.Empty(runtime.Load("raw-write", RespondOnClientWrite(requestId, "GVL_Module.Chamber1_Control[4].ServiceStatus.DoorClose.ResponseId")));

        Assert.Equal(AdsErrorCode.NoError, TryWriteBytes(DataGroup, (uint)(field.Offset + 2), [0x34, 0x12, 0x78, 0x56]));
        await monitor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 0x34, 0x12, 0x78, 0x56 }, ImageBytes(field.Offset + 2, 4));
    }

    [Fact]
    [Trait("AC", "7")]
    public void Raw_write_beyond_the_process_image_fails_with_invalid_offset()
    {
        byte[] last = ImageBytes(_image.Size - 2, 2);
        Assert.Equal(AdsErrorCode.DeviceInvalidOffset, TryWriteBytes(DataGroup, (uint)(_image.Size - 2), [1, 2, 3, 4]));
        Assert.Equal(last, ImageBytes(_image.Size - 2, 2));
    }

    [Fact]
    [Trait("AC", "8")]
    public async Task Handle_notification_sends_the_initial_value_and_then_each_change()
    {
        SymbolRef element = Sym($"{Control}[1]");
        byte[] original = ImageBytes(element.Offset, 8);
        using var watcher = new NotificationWatcher(_client);
        uint variable = _client.CreateVariableHandle($"{Control}[1]");
        _image.Write(element.Offset, Sequence(8, 1), WriteSource.Client);
        uint handle = 0;
        try
        {
            handle = AddNotification(HandleGroup, variable, 8, AdsTransMode.OnChange, out AdsErrorCode result);
            Assert.Equal(AdsErrorCode.NoError, result);
            Assert.True(await watcher.WaitFor(handle, data => data.SequenceEqual(Sequence(8, 1))), "initial value not received");

            _image.Write(element.Offset, Sequence(8, 100), WriteSource.Client);
            Assert.True(await watcher.WaitFor(handle, data => data.SequenceEqual(Sequence(8, 100))), "changed value not received");
        }
        finally
        {
            _client.TryDeleteDeviceNotification(handle);
            _client.TryDeleteVariableHandle(variable);
            _image.Write(element.Offset, original, WriteSource.Client);
        }
    }

    [Fact]
    [Trait("AC", "8")]
    public void Notification_with_an_unusable_length_is_refused_with_the_measured_error_codes()
    {
        SymbolRef element = Sym($"{Control}[1]");
        WithHandle($"{Control}[1]", handle =>
        {
            AddNotification(HandleGroup, handle, element.Size + 4, AdsTransMode.OnChange, out AdsErrorCode tooLong);
            AddNotification(HandleGroup, handle, 0, AdsTransMode.OnChange, out AdsErrorCode zeroByHandle);
            Assert.Equal(AdsErrorCode.DeviceInvalidOffset, tooLong);
            Assert.Equal(AdsErrorCode.DeviceInvalidOffset, zeroByHandle);
        });

        AddNotification(DataGroup, (uint)element.Offset, 0, AdsTransMode.OnChange, out AdsErrorCode zeroRaw);
        Assert.Equal(AdsErrorCode.DeviceInvalidWatchsize, zeroRaw);
    }

    [Fact]
    [Trait("AC", "8")]
    public async Task Deleted_notification_stops_arriving()
    {
        SymbolRef field = Sym(RequestIdOf3);
        byte[] original = ImageBytes(field.Offset, 4);
        using var watcher = new NotificationWatcher(_client);
        try
        {
            uint handle = AddNotification(DataGroup, (uint)field.Offset, 4, AdsTransMode.OnChange, out AdsErrorCode result);
            Assert.Equal(AdsErrorCode.NoError, result);
            Assert.True(await watcher.WaitFor(handle, _ => true), "initial value not received");

            Assert.Equal(AdsErrorCode.NoError, _client.TryDeleteDeviceNotification(handle));
            await Task.Delay(200);
            int received = watcher.Count(handle);

            _image.Write(field.Offset, [0xEE, 0xEE, 0xEE, 0xEE], WriteSource.Client);
            await Task.Delay(500);
            Assert.Equal(received, watcher.Count(handle));
        }
        finally
        {
            _image.Write(field.Offset, original, WriteSource.Client);
        }
    }

    [Fact]
    [Trait("AC", "9")]
    public async Task Raw_notification_over_two_symbols_sends_after_a_change()
    {
        SymbolRef first = Sym($"{Control}[5]");
        int start = first.Offset + first.Size - 2;
        byte[] original = ImageBytes(start, 4);
        using var watcher = new NotificationWatcher(_client);
        uint handle = 0;
        try
        {
            handle = AddNotification(DataGroup, (uint)start, 4, AdsTransMode.OnChange, out AdsErrorCode result);
            Assert.Equal(AdsErrorCode.NoError, result);
            Assert.True(await watcher.WaitFor(handle, _ => true), "initial value not received");

            byte[] changed = [0xC1, 0xC2, 0xC3, 0xC4];
            _image.Write(start, changed, WriteSource.Client);
            Assert.True(await watcher.WaitFor(handle, data => data.SequenceEqual(changed)), "changed value not received");
        }
        finally
        {
            _client.TryDeleteDeviceNotification(handle);
            _image.Write(start, original, WriteSource.Client);
        }
    }

    [Fact]
    [Trait("AC", "9a")]
    public async Task Cyclic_notification_keeps_sending_an_unchanged_value()
    {
        SymbolRef field = Sym(RequestIdOf3);
        using var watcher = new NotificationWatcher(_client);
        uint handle = AddNotification(DataGroup, (uint)field.Offset, 4, AdsTransMode.Cyclic, out AdsErrorCode result, cycleTime: 100);
        Assert.Equal(AdsErrorCode.NoError, result);
        try
        {
            await Task.Delay(1000);
            Assert.True(watcher.Count(handle) >= 3, $"only {watcher.Count(handle)} cyclic samples in 1 second");
        }
        finally
        {
            _client.TryDeleteDeviceNotification(handle);
        }
    }

    [Fact]
    [Trait("AC", "10")]
    public void Releasing_a_handle_with_its_own_number_as_offset_succeeds_and_invalidates_it()
    {
        const string symbolNoOtherTestUses = "GVL_Module.Chamber1_Control[6].ServiceCtrl.DoorOpen.RequestId";
        uint handle = _client.CreateVariableHandle(symbolNoOtherTestUses);
        Assert.Equal(AdsErrorCode.NoError, TryWriteBytes(ReleaseGroup, handle, BitConverter.GetBytes(handle)));
        Assert.NotEqual(AdsErrorCode.NoError, TryReadBytes(HandleGroup, handle, 4, out _));
    }

    [Fact]
    public void Ads_offset_of_every_root_and_sample_sub_symbol_equals_the_process_image_offset()
    {
        ISymbolLoader loader = SymbolLoaderFactory.Create(_client, SymbolLoaderSettings.Default);
        string[] paths =
        [
            .. _program.Symbols.Roots.Select(root => root.Path),
            RequestIdOf3,
            "GVL_Module.Chamber1_Control[6].ServiceCtrl",
            $"{Control}[2]",
        ];

        foreach (string path in paths)
        {
            Assert.True(loader.Symbols.TryGetInstance(path, out ISymbol? symbol), $"{path} not published");
            Assert.Equal(DataGroup, ((IAdsSymbol)symbol!).IndexGroup);
            Assert.Equal((uint)Sym(path).Offset, ((IAdsSymbol)symbol).IndexOffset);
        }
    }

    private const uint ValueByNameGroup = 0xF004;
    private const uint UnknownHandle = 0x0BAD_0001;

    private AdsErrorCode ReadValueByName(string path, int readLength, out byte[] data)
    {
        byte[] name = System.Text.Encoding.ASCII.GetBytes(path + " ");
        byte[] buffer = new byte[readLength];
        AdsErrorCode result = _client.TryReadWrite(ValueByNameGroup, 0, buffer.AsMemory(), name.AsMemory(), out int read);
        data = buffer[..read];
        return result;
    }

    [Fact]
    [Trait("AC", "1")]
    public void Value_by_name_read_returns_the_process_image_bytes()
    {
        SymbolRef field = Sym(RequestIdOf3);
        WithImageBytes(field.Offset, [0x11, 0x22, 0x33, 0x44], () =>
        {
            Assert.Equal(AdsErrorCode.NoError, ReadValueByName(RequestIdOf3, 4, out byte[] data));
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, data);
        });
    }

    [Fact]
    [Trait("AC", "2")]
    public void Value_by_name_read_with_too_small_a_buffer_or_an_unknown_name_fails()
    {
        Assert.NotEqual(AdsErrorCode.NoError, ReadValueByName(RequestIdOf3, 2, out _));
        Assert.NotEqual(AdsErrorCode.NoError, ReadValueByName("GVL_Module.NoSuchSymbol", 4, out _));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Locating_a_symbol_accepts_lengths_up_to_its_size_only()
    {
        Assert.True(_server.Symbols.TryGetInstance(RequestIdOf3, out ISymbol? symbol));
        SymbolRef field = Sym(RequestIdOf3);

        Assert.True(_server.TryLocate(symbol!, field.Size, out int offset));
        Assert.Equal(field.Offset, offset);
        Assert.False(_server.TryLocate(symbol!, field.Size + 1, out _));
    }

    [Fact]
    [Trait("AC", "5")]
    public void Requests_for_an_unknown_handle_are_answered_by_the_base_server()
    {
        Assert.NotEqual(AdsErrorCode.NoError, TryWriteBytes(HandleGroup, UnknownHandle, [1, 2, 3, 4]));
        Assert.NotEqual(AdsErrorCode.NoError, TryReadBytes(HandleGroup, UnknownHandle, 4, out _));
        AddNotification(HandleGroup, UnknownHandle, 4, AdsTransMode.OnChange, out AdsErrorCode add);
        Assert.NotEqual(AdsErrorCode.NoError, add);
        Assert.NotEqual(AdsErrorCode.NoError, _client.TryDeleteDeviceNotification(UnknownHandle));
    }

    [Fact]
    [Trait("AC", "8")]
    public async Task Notification_cannot_be_deleted_by_another_client_and_keeps_arriving_for_its_owner()
    {
        SymbolRef field = Sym(RequestIdOf3);
        byte[] original = ImageBytes(field.Offset, 4);
        using var watcher = new NotificationWatcher(_client);
        using var other = new AdsClient();
        other.Connect(AmsNetId.Local, AdsServerTests.SimulatedPlc.Port);
        uint handle = AddNotification(DataGroup, (uint)field.Offset, 4, AdsTransMode.OnChange, out AdsErrorCode result);
        try
        {
            Assert.Equal(AdsErrorCode.NoError, result);
            Assert.True(await watcher.WaitFor(handle, _ => true), "initial value not received");

            Assert.Equal(AdsErrorCode.DeviceNotifyHandleInvalid, other.TryDeleteDeviceNotification(handle));

            byte[] changed = [0xD1, 0xD2, 0xD3, 0xD4];
            _image.Write(field.Offset, changed, WriteSource.Client);
            Assert.True(await watcher.WaitFor(handle, data => data.SequenceEqual(changed)), "owner stopped receiving");
        }
        finally
        {
            _client.TryDeleteDeviceNotification(handle);
            _image.Write(field.Offset, original, WriteSource.Client);
        }
    }

    [Fact]
    [Trait("AC", "8")]
    public async Task Subscription_of_a_disconnected_client_is_dropped_after_the_failed_send()
    {
        const string path = "GVL_Module.Chamber1_Control[6].ServiceCtrl.DoorClose.RequestId";
        SymbolRef field = Sym(path);
        byte[] original = ImageBytes(field.Offset, 4);
        int before = _server.SubscriptionCount;
        var other = new AdsClient();
        other.Connect(AmsNetId.Local, AdsServerTests.SimulatedPlc.Port);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.AdsNotification += (_, _) => arrived.TrySetResult();
        try
        {
            Assert.Equal(AdsErrorCode.NoError, other.TryAddDeviceNotification(DataGroup, (uint)field.Offset, 4, new NotificationSettings(AdsTransMode.OnChange, 10, 0), null, out _));
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(before + 1, _server.SubscriptionCount);

            other.Dispose();
            _image.Write(field.Offset, [0xE1, 0xE2, 0xE3, 0xE4], WriteSource.Client);

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (_server.SubscriptionCount != before && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.Equal(before, _server.SubscriptionCount);
        }
        finally
        {
            other.Dispose();
            _image.Write(field.Offset, original, WriteSource.Client);
        }
    }

    [Theory]
    [Trait("AC", "8")]
    [InlineData(true, 0, 4, 0u, 100, AdsErrorCode.DeviceInvalidOffset)]
    [InlineData(false, 0, 4, 0u, 100, AdsErrorCode.DeviceInvalidWatchsize)]
    [InlineData(false, -1, 4, 0u, 100, AdsErrorCode.DeviceInvalidWatchsize)]
    [InlineData(true, 5, 4, 0u, 100, AdsErrorCode.DeviceInvalidOffset)]
    [InlineData(true, 4, 4, 0u, 100, AdsErrorCode.NoError)]
    [InlineData(false, 4, 0, 97u, 100, AdsErrorCode.DeviceInvalidOffset)]
    [InlineData(false, 4, 0, 96u, 100, AdsErrorCode.NoError)]
    public void Notification_range_check_returns_the_measured_error_codes(bool byHandle, int dataLength, int size, uint indexOffset, int imageSize, AdsErrorCode expected)
    {
        Assert.Equal(expected, PlcAdsServer.CheckNotificationRange(byHandle, dataLength, size, indexOffset, imageSize, out int offset));
        Assert.Equal(expected == AdsErrorCode.NoError && !byHandle ? (int)indexOffset : 0, offset);
    }

    private sealed class NotificationWatcher : IDisposable
    {
        private readonly AdsClient _watched;
        private readonly ConcurrentQueue<(uint Handle, byte[] Data)> _samples = new();

        public NotificationWatcher(AdsClient client)
        {
            _watched = client;
            _watched.AdsNotification += OnNotification;
        }

        public int Count(uint handle) => _samples.Count(sample => sample.Handle == handle);

        public async Task<bool> WaitFor(uint handle, Func<byte[], bool> match)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (_samples.Any(sample => sample.Handle == handle && match(sample.Data)))
                    return true;

                await Task.Delay(10);
            }

            return false;
        }

        public void Dispose() => _watched.AdsNotification -= OnNotification;

        private void OnNotification(object? sender, AdsNotificationEventArgs args) =>
            _samples.Enqueue((args.Handle, args.Data.ToArray()));
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
