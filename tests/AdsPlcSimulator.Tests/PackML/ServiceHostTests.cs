using System.Text.RegularExpressions;
using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class ServiceHostTests
{
    private static readonly string PathA = HostFixture.CtrlPath(1, "A");

    private static string WithDeclarations(Func<string, string> change) => change(HostFixture.Declarations);

    private static ServiceHostException BuildFails(string declarations, string root = "GVL_Test.Modules", string identities = HostFixture.Identities)
    {
        PlcProgram program = TestPaths.BuildFromText(declarations);
        return Assert.Throws<ServiceHostException>(() => new ServiceHost(
            program.Symbols,
            program.Symbols.Resolve(root),
            HostFixture.ParseIdentities(program.Symbols, identities),
            HostFixture.DefaultDelay,
            _ => null));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Initialize_sets_module_execute_and_services_idle()
    {
        var fixture = new HostFixture();

        Assert.Equal(PackMLState.Execute, fixture.ModuleState(2));
        Assert.Equal((long)PackMLState.Starting, fixture.ModuleStatus(2, "PackMLOut.eStatePrevious"));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(2, "B"));
        Assert.Equal((long)PackMLState.Resetting, fixture.ServiceStatus(1, "A", "PackMLOut.eStatePrevious"));
        Assert.Equal(0L, fixture.ServiceStatus(1, "A", "ResponseId"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Initialize_writes_identity_of_every_table_entry()
    {
        var fixture = new HostFixture();

        foreach (int module in new[] { 1, 2 })
        {
            Assert.Equal(101L, fixture.ServiceStatus(module, "A", "ServiceId"));
            Assert.Equal(1L, fixture.ServiceStatus(module, "A", "CompletionBehavior"));
            Assert.Equal(102L, fixture.ServiceStatus(module, "B", "ServiceId"));
            Assert.Equal(2L, fixture.ServiceStatus(module, "B", "CompletionBehavior"));
            Assert.Equal(0L, fixture.ServiceStatus(module, "Unbound", "ServiceId"));
            Assert.Equal(1L, fixture.ServiceStatus(module, "Unbound", "CompletionBehavior"));
        }
    }

    [Fact]
    [Trait("AC", "2")]
    public void Path_missing_from_the_table_keeps_its_image_content()
    {
        PlcProgram program = TestPaths.BuildFromText(HostFixture.Declarations);
        ProcessImage image = program.CreateProcessImage();
        SymbolRef serviceId = program.Symbols.Resolve("GVL_Test.Modules[1].ServiceStatus.B.ServiceId");
        image.WriteValue(serviceId, 55, WriteSource.Client);
        string onlyA = "F_Set(Status := ServiceStatus.A, ServiceId := E_TestServiceId.ServiceA, CompletionBehavior := E_ServiceCompletionBehavior.NaturalCompletion);";
        var host = new ServiceHost(program.Symbols, program.Symbols.Resolve("GVL_Test.Modules"), HostFixture.ParseIdentities(program.Symbols, onlyA), HostFixture.DefaultDelay, _ => null);

        host.Initialize(image);

        Assert.Equal(55L, image.ReadValue(serviceId));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Bound_services_are_the_ones_with_a_service_id()
    {
        var fixture = new HostFixture();

        Assert.Equal(4, fixture.Host.BoundServices.Count);
        Assert.Contains(PathA, fixture.Host.BoundServices);
        Assert.True(fixture.Host.Contains(PathA));
        Assert.True(fixture.Host.Contains(PathA.ToUpperInvariant()));
        Assert.False(fixture.Host.Contains(HostFixture.CtrlPath(1, "Unbound")));
        Assert.Equal("GVL_Test.Modules", fixture.Host.RootPath);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Unbound_service_ignores_commands_and_keeps_response_id()
    {
        var fixture = new HostFixture();

        fixture.SendToService(1, "Unbound", PackMLCommand.Start, 5);
        fixture.Scan();

        Assert.Equal(PackMLState.Undefined, fixture.ServiceState(1, "Unbound"));
        Assert.Equal(0L, fixture.ServiceStatus(1, "Unbound", "ResponseId"));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Start_echoes_response_id_and_reaches_execute_in_one_scan()
    {
        var fixture = new HostFixture();

        fixture.SendToService(1, "A", PackMLCommand.Start, 77);
        fixture.Scan();

        Assert.Equal(77L, fixture.ServiceStatus(1, "A", "ResponseId"));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal((long)PackMLState.Starting, fixture.ServiceStatus(1, "A", "PackMLOut.eStatePrevious"));
        Assert.Equal(true, fixture.ServiceStatus(1, "A", "PackMLOut.bCommandAccepted"));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "B"));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Status_is_published_in_one_write()
    {
        var fixture = new HostFixture();
        var writes = new List<MemoryWrite>();
        fixture.Image.Written += writes.Add;
        SymbolRef responseId = fixture.Symbols.Resolve("GVL_Test.Modules[1].ServiceStatus.A.ResponseId");
        SymbolRef state = fixture.Symbols.Resolve("GVL_Test.Modules[1].ServiceStatus.A.PackMLOut.eStateCurrent");

        fixture.SendToService(1, "A", PackMLCommand.Start, 3);
        writes.Clear();
        fixture.Scan();

        MemoryWrite published = Assert.Single(writes, write => write.Source == WriteSource.Workflow && write.Overlaps(responseId.Offset, responseId.Size));
        Assert.True(published.Overlaps(state.Offset, state.Size));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Unchanged_status_is_not_written_again()
    {
        var fixture = new HostFixture();
        fixture.Scan();
        var writes = new List<MemoryWrite>();
        fixture.Image.Written += writes.Add;

        fixture.Scan();

        Assert.Empty(writes);
    }

    [Fact]
    [Trait("AC", "4")]
    public void Request_low_clears_accepted_rejected_and_error()
    {
        var fixture = new HostFixture();
        fixture.SendToService(1, "A", PackMLCommand.Reset, 1);
        fixture.Scan();
        Assert.Equal(true, fixture.ServiceStatus(1, "A", "PackMLOut.bCommandRejected"));
        Assert.Equal(1003L, fixture.ServiceStatus(1, "A", "PackMLOut.eStateErrorID"));

        fixture.ReleaseService(1, "A");
        fixture.Scan();

        Assert.Equal(false, fixture.ServiceStatus(1, "A", "PackMLOut.bCommandRejected"));
        Assert.Equal(0L, fixture.ServiceStatus(1, "A", "PackMLOut.eStateErrorID"));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Held_request_runs_command_once()
    {
        var fixture = new HostFixture();

        fixture.SendToService(1, "A", PackMLCommand.Start, 4);
        fixture.Scan();
        fixture.Scan();
        fixture.Scan();

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal(false, fixture.ServiceStatus(1, "A", "PackMLOut.bCommandRejected"));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Natural_completion_completes_after_default_delay()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();

        fixture.ScanAt(HostFixture.DefaultDelay);
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));

        fixture.Scan();
        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
    }

    [Theory]
    [InlineData(4999, PackMLState.Execute)]
    [InlineData(5000, PackMLState.Completing)]
    [InlineData(5001, PackMLState.Completing)]
    [Trait("AC", "4")]
    public void Natural_completion_waits_exactly_the_default_delay(int elapsedMilliseconds, PackMLState expected)
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();

        fixture.ScanAt(TimeSpan.FromMilliseconds(elapsedMilliseconds));

        Assert.Equal(expected, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Natural_completion_with_zero_delay_completes_on_the_scan_after_entering_execute()
    {
        var fixture = new HostFixture(TimeSpan.Zero);

        fixture.SendToService(1, "A", PackMLCommand.Start, 1);
        fixture.Scan();
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));

        fixture.Scan();
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Delay_counts_from_entering_execute_not_from_time_zero()
    {
        var fixture = new HostFixture();
        fixture.Now = TimeSpan.FromSeconds(100);
        fixture.StartServiceA();

        fixture.ScanAt(TimeSpan.FromSeconds(104));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));

        fixture.ScanAt(TimeSpan.FromSeconds(105));
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "5")]
    public void External_stop_service_stays_in_execute_until_stop()
    {
        var fixture = new HostFixture();
        fixture.ServiceCommand(1, "B", PackMLCommand.Start, 1);

        fixture.ScanAt(TimeSpan.FromHours(1));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));

        fixture.ServiceCommand(1, "B", PackMLCommand.Stop, 2);
        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "B"));
        Assert.Equal((long)PackMLState.Stopping, fixture.ServiceStatus(1, "B", "PackMLOut.eStatePrevious"));
    }

    [Fact]
    [Trait("AC", "9")]
    public void Command_to_service_b_is_answered_while_service_a_waits()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA(1);

        fixture.ServiceCommand(1, "B", PackMLCommand.Start, 9);

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Equal(9L, fixture.ServiceStatus(1, "B", "ResponseId"));
    }

    [Fact]
    [Trait("AC", "9")]
    public void Each_service_has_its_own_execute_entry_time()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA(1);
        fixture.Now = TimeSpan.FromSeconds(3);
        fixture.ServiceCommand(1, "A", PackMLCommand.Stop, 2);
        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 3);
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 4);

        fixture.ScanAt(TimeSpan.FromSeconds(7));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));

        fixture.ScanAt(TimeSpan.FromSeconds(8));
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_command_rejected_by_module_is_still_forwarded()
    {
        var fixture = new HostFixture();
        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);
        Assert.Equal(PackMLState.Stopped, fixture.ModuleState(1));

        fixture.SendToModule(1, PackMLCommand.Stop, 20);
        fixture.Scan();

        Assert.Equal(true, fixture.ModuleStatus(1, "PackMLOut.bCommandRejected"));
        Assert.Equal(20L, fixture.ServiceStatus(1, "A", "ResponseId"));
        Assert.Equal(true, fixture.ServiceStatus(1, "A", "PackMLOut.bCommandRejected"));
        Assert.Equal(1005L, fixture.ServiceStatus(1, "A", "PackMLOut.eStateErrorID"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_stop_waits_for_all_services_stopped()
    {
        var fixture = new HostFixture();

        fixture.SendToModule(1, PackMLCommand.Stop, 10);
        fixture.Scan();

        Assert.Equal(PackMLState.Stopping, fixture.ModuleState(1));
        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "A"));
        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "B"));

        fixture.Scan();
        Assert.Equal(PackMLState.Stopped, fixture.ModuleState(1));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_reset_waits_for_all_services_idle()
    {
        var fixture = new HostFixture();
        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);

        fixture.SendToModule(1, PackMLCommand.Reset, 11);
        fixture.Scan();

        Assert.Equal(PackMLState.Resetting, fixture.ModuleState(1));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "B"));

        fixture.Scan();
        Assert.Equal(PackMLState.Idle, fixture.ModuleState(1));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_abort_waits_for_all_services_aborted()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();

        fixture.SendToModule(1, PackMLCommand.Abort, 12);
        fixture.Scan();

        Assert.Equal(PackMLState.Aborting, fixture.ModuleState(1));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));

        fixture.Scan();
        Assert.Equal(PackMLState.Aborted, fixture.ModuleState(1));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_does_not_wait_for_an_unbound_service()
    {
        var fixture = new HostFixture();

        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);

        Assert.Equal(PackMLState.Stopped, fixture.ModuleState(1));
        Assert.Equal(PackMLState.Undefined, fixture.ServiceState(1, "Unbound"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_start_is_not_forwarded_to_services()
    {
        var fixture = new HostFixture();
        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);
        fixture.ModuleCommand(1, PackMLCommand.Reset, 11);

        fixture.ModuleCommand(1, PackMLCommand.Start, 12);

        Assert.Equal(PackMLState.Execute, fixture.ModuleState(1));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Service_request_ignored_while_module_not_in_execute()
    {
        var fixture = new HostFixture();
        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);

        fixture.SendToService(1, "A", PackMLCommand.Start, 99);
        fixture.Scan();

        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "A"));
        Assert.Equal(10L, fixture.ServiceStatus(1, "A", "ResponseId"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Request_high_when_module_returns_to_execute_is_ignored_until_low()
    {
        var fixture = new HostFixture();
        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);
        fixture.ModuleCommand(1, PackMLCommand.Reset, 11);
        fixture.SendToService(1, "A", PackMLCommand.Start, 5);
        fixture.Scan();

        fixture.ModuleCommand(1, PackMLCommand.Start, 12);
        Assert.Equal(PackMLState.Execute, fixture.ModuleState(1));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));

        fixture.ReleaseService(1, "A");
        fixture.Scan();
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 6);

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal(6L, fixture.ServiceStatus(1, "A", "ResponseId"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_response_id_echoes_on_command_and_mode_edge()
    {
        var fixture = new HostFixture();

        fixture.SendToModule(1, PackMLCommand.Hold, 31);
        fixture.Scan();
        Assert.Equal(31L, fixture.ModuleStatus(1, "ResponseId"));
        Assert.Equal(PackMLState.Held, fixture.ModuleState(1));

        fixture.SendModeToModule(1, 1, 32);
        fixture.Scan();
        Assert.Equal(32L, fixture.ModuleStatus(1, "ResponseId"));
        Assert.Equal(true, fixture.ModuleStatus(1, "PackMLOut.bModeChangeRejected"));
        Assert.Equal(2003L, fixture.ModuleStatus(1, "PackMLOut.eModeErrorID"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Modules_run_independently()
    {
        var fixture = new HostFixture();

        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);

        Assert.Equal(PackMLState.Stopped, fixture.ModuleState(1));
        Assert.Equal(PackMLState.Execute, fixture.ModuleState(2));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(2, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Overridden_service_waits_for_complete_service()
    {
        var fixture = new HostFixture();
        fixture.SetOverrideToken(1, "A", new object());
        fixture.StartServiceA();
        fixture.ScanAt(TimeSpan.FromHours(1));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));

        fixture.Host.Complete(PathA, fixture.Host.ExecuteGeneration(PathA));
        fixture.Scan();
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));

        fixture.Scan();
        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Overridden_service_ignores_default_delay()
    {
        var fixture = new HostFixture();
        fixture.SetOverrideToken(1, "A", new object());
        fixture.StartServiceA();

        fixture.ScanAt(HostFixture.DefaultDelay * 2);

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Complete_with_null_generation_skips_the_generation_check()
    {
        var fixture = new HostFixture();
        fixture.SetOverrideToken(1, "A", new object());
        fixture.StartServiceA();

        fixture.Host.Complete(PathA, null);
        fixture.Scan();

        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Override_unloaded_falls_back_to_default_delay()
    {
        var fixture = new HostFixture();
        fixture.SetOverrideToken(1, "A", new object());
        fixture.StartServiceA();
        fixture.SetOverrideToken(1, "A", null);

        fixture.ScanAt(TimeSpan.FromMilliseconds(4999));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));

        fixture.ScanAt(TimeSpan.FromMilliseconds(5000));
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Override_reloaded_with_a_new_token_falls_back_to_default_delay()
    {
        var fixture = new HostFixture();
        fixture.SetOverrideToken(1, "A", new object());
        fixture.StartServiceA();
        fixture.SetOverrideToken(1, "A", new object());

        fixture.ScanAt(HostFixture.DefaultDelay);

        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Override_loaded_during_execute_uses_default_for_that_execute()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();
        fixture.SetOverrideToken(1, "A", new object());

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Host.Complete(PathA, null));
        Assert.Contains("not overridden", exception.Message);

        fixture.ScanAt(HostFixture.DefaultDelay);
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Complete_after_override_loaded_applies_to_the_next_execute()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();
        fixture.SetOverrideToken(1, "A", new object());
        fixture.ServiceCommand(1, "A", PackMLCommand.Stop, 2);
        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 3);
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 4);

        fixture.Host.Complete(PathA, null);
        fixture.Scan();

        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Complete_service_outside_execute_fails_run()
    {
        var fixture = new HostFixture();
        fixture.SetOverrideToken(1, "A", new object());

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Host.Complete(PathA, null));

        Assert.Contains("Idle", exception.Message);
    }

    [Theory]
    [InlineData("GVL_Test.Modules[1].ServiceCtrl.Unbound")]
    [InlineData("GVL_Test.Nothing")]
    [Trait("AC", "12")]
    public void Complete_abort_and_generation_of_a_service_that_is_not_bound_fail(string path)
    {
        var fixture = new HostFixture();

        Assert.Throws<InvalidOperationException>(() => fixture.Host.Complete(path, null));
        Assert.Throws<InvalidOperationException>(() => fixture.Host.Abort(path, null));
        Assert.Throws<InvalidOperationException>(() => fixture.Host.ExecuteGeneration(path));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Abort_service_aborts_overridden_service()
    {
        var fixture = new HostFixture();
        fixture.SetOverrideToken(1, "A", new object());
        fixture.StartServiceA();

        fixture.Host.Abort(PathA, fixture.Host.ExecuteGeneration(PathA));
        fixture.Scan();

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
        Assert.Equal((long)PackMLState.Aborting, fixture.ServiceStatus(1, "A", "PackMLOut.eStatePrevious"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Abort_fails_when_an_internal_command_is_already_queued()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();
        fixture.Host.Abort(PathA, null);

        Assert.Throws<InvalidOperationException>(() => fixture.Host.Abort(PathA, null));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Abort_fails_while_the_mode_request_is_high()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();
        fixture.Write($"{PathA}.PackMLIn.bModeChangeRequest", true);
        fixture.Scan();

        Assert.Throws<InvalidOperationException>(() => fixture.Host.Abort(PathA, null));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Abort_with_a_stale_generation_fails()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();
        long stale = fixture.Host.ExecuteGeneration(PathA);
        fixture.ServiceCommand(1, "A", PackMLCommand.Stop, 2);
        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 3);
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 4);

        Assert.Throws<InvalidOperationException>(() => fixture.Host.Abort(PathA, stale));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Stale_run_cannot_complete_next_execute()
    {
        var fixture = new HostFixture();
        fixture.SetOverrideToken(1, "A", new object());
        fixture.StartServiceA();
        long stale = fixture.Host.ExecuteGeneration(PathA);
        fixture.ServiceCommand(1, "A", PackMLCommand.Stop, 2);
        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 3);
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 4);
        long current = fixture.Host.ExecuteGeneration(PathA);

        Assert.NotEqual(stale, current);
        Assert.Throws<InvalidOperationException>(() => fixture.Host.Complete(PathA, stale));
        fixture.Host.Complete(PathA, current);
        fixture.Scan();
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Stale_run_after_host_reload_cannot_complete_next_execute()
    {
        var fixture = new HostFixture();
        object token = new();
        fixture.SetOverrideToken(1, "A", token);
        fixture.StartServiceA();
        long stale = fixture.Host.ExecuteGeneration(PathA);

        fixture.Host.Retire();
        ServiceHost reloaded = HostFixture.CreateHost(fixture.Program, fixture.Image, HostFixture.DefaultDelay, _ => token);
        fixture.Host = reloaded;
        reloaded.Initialize(fixture.Image);
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 5);

        Assert.NotEqual(stale, reloaded.ExecuteGeneration(PathA));
        Assert.Throws<InvalidOperationException>(() => reloaded.Complete(PathA, stale));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Generation_is_zero_before_the_first_execute_and_new_for_every_entry()
    {
        var fixture = new HostFixture();
        Assert.Equal(0, fixture.Host.ExecuteGeneration(PathA));

        fixture.StartServiceA();
        long first = fixture.Host.ExecuteGeneration(PathA);
        fixture.ServiceCommand(1, "A", PackMLCommand.Stop, 2);
        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 3);
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 4);

        Assert.True(first > 0);
        Assert.True(fixture.Host.ExecuteGeneration(PathA) > first);
    }

    [Fact]
    [Trait("AC", "12")]
    public void Unhold_reenters_execute_with_new_generation()
    {
        var fixture = new HostFixture();
        string pathB = HostFixture.CtrlPath(1, "B");
        fixture.ServiceCommand(1, "B", PackMLCommand.Start, 1);
        long first = fixture.Host.ExecuteGeneration(pathB);
        fixture.ServiceCommand(1, "B", PackMLCommand.Hold, 2);
        Assert.Equal(PackMLState.Held, fixture.ServiceState(1, "B"));
        Assert.Equal(first, fixture.Host.ExecuteGeneration(pathB));

        fixture.ServiceCommand(1, "B", PackMLCommand.Unhold, 3);

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.True(fixture.Host.ExecuteGeneration(pathB) > first);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Reloading_host_keeps_scanning()
    {
        var fixture = new HostFixture();
        fixture.Host.Retire();
        ServiceHost reloaded = HostFixture.CreateHost(fixture.Program, fixture.Image, HostFixture.DefaultDelay, _ => null);
        fixture.Host = reloaded;
        reloaded.Initialize(fixture.Image);

        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 8);

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal(8L, fixture.ServiceStatus(1, "A", "ResponseId"));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Reload_clears_stale_status_and_ignores_held_request()
    {
        var fixture = new HostFixture();
        fixture.SendToService(1, "A", PackMLCommand.Start, 8);
        fixture.Scan();
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));

        fixture.Host.Retire();
        ServiceHost reloaded = HostFixture.CreateHost(fixture.Program, fixture.Image, HostFixture.DefaultDelay, _ => null);
        fixture.Host = reloaded;
        reloaded.Initialize(fixture.Image);
        fixture.Scan();

        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));
        Assert.Equal(0L, fixture.ServiceStatus(1, "A", "ResponseId"));
        Assert.Equal(0L, reloaded.ExecuteGeneration(PathA));

        fixture.ReleaseService(1, "A");
        fixture.Scan();
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 9);
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Retired_host_neither_scans_nor_initializes()
    {
        var fixture = new HostFixture();
        fixture.Host.Retire();
        var writes = new List<MemoryWrite>();
        fixture.Image.Written += writes.Add;

        fixture.SendToService(1, "A", PackMLCommand.Start, 1);
        writes.Clear();
        fixture.Scan();
        fixture.Host.Initialize(fixture.Image);

        Assert.Empty(writes);
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Struct_root_is_a_single_module()
    {
        string declarations = WithDeclarations(text => text.Replace("Modules : ARRAY[1..2] OF ST_TestModule;", "Module : ST_TestModule;"));
        PlcProgram program = TestPaths.BuildFromText(declarations);
        var host = new ServiceHost(program.Symbols, program.Symbols.Resolve("GVL_Test.Module"), HostFixture.ParseIdentities(program.Symbols), HostFixture.DefaultDelay, _ => null);

        Assert.Equal(["GVL_Test.Module.ServiceCtrl.A", "GVL_Test.Module.ServiceCtrl.B"], host.BoundServices);
    }

    [Theory]
    [InlineData("VAR_GLOBAL nValue : INT; END_VAR", "GVL_Test.nValue")]
    [InlineData("VAR_GLOBAL aValues : ARRAY[1..3] OF INT; END_VAR", "GVL_Test.aValues")]
    [InlineData("VAR_GLOBAL aValues : ARRAY[1..2, 1..2] OF ST_Cell; END_VAR", "GVL_Test.aValues")]
    [Trait("AC", "13")]
    public void Host_with_wrong_shape_reports_diagnostic(string declarations, string root)
    {
        ServiceHostException exception = BuildFails("TYPE ST_Cell : STRUCT n : INT; END_STRUCT END_TYPE " + declarations, root, identities: string.Empty);

        Assert.Contains(root, exception.Message);
    }

    [Theory]
    [InlineData("ModuleCtrl : ST_ModuleCtrl;", "ModuleCtrl")]
    [InlineData("ModuleStatus : ST_ModuleStatus;", "ModuleStatus")]
    [InlineData("ServiceCtrl : ST_TestServiceCtrls;", "ServiceCtrl")]
    [InlineData("ServiceStatus : ST_TestServiceStatuses;", "ServiceStatus")]
    [Trait("AC", "13")]
    public void Host_with_missing_module_member_reports_the_member_and_module(string member, string name)
    {
        ServiceHostException exception = BuildFails(WithDeclarations(text => text.Replace(member, string.Empty)));

        Assert.Contains($"no member '{name}'", exception.Message);
        Assert.Contains("GVL_Test.Modules[1]", exception.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_with_wrongly_typed_module_ctrl_reports_diagnostic()
    {
        ServiceHostException exception = BuildFails(WithDeclarations(text => text.Replace("ModuleCtrl : ST_ModuleCtrl;", "ModuleCtrl : ST_ServiceCtrl;")));

        Assert.Contains("ST_ModuleCtrl", exception.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_with_service_ctrl_lacking_a_status_reports_diagnostic()
    {
        ServiceHostException exception = BuildFails(WithDeclarations(text =>
            text.Replace("A : ST_ServiceStatus; B : ST_ServiceStatus; Unbound : ST_ServiceStatus;", "A : ST_ServiceStatus; B : ST_ServiceStatus;")));

        Assert.Contains("Unbound", exception.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_with_response_id_not_next_to_packml_out_reports_diagnostic()
    {
        ServiceHostException exception = BuildFails(WithDeclarations(text => Regex.Replace(
            text,
            @"(ResponseId : UDINT;)(\s+PackMLOut : ST_PML_Base_Out;\s+CompletionBehavior)",
            "$1 Gap : UDINT;$2")));

        Assert.Contains("PackMLOut does not directly follow ResponseId", exception.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_without_bound_service_reports_diagnostic()
    {
        ServiceHostException exception = BuildFails(
            HostFixture.Declarations,
            identities: "ServiceStatus.A.CompletionBehavior := E_ServiceCompletionBehavior.NaturalCompletion;");

        Assert.Equal("No service under GVL_Test.Modules has a ServiceId.", exception.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_with_status_lacking_a_packml_out_member_names_the_owner_and_member()
    {
        ServiceHostException exception = BuildFails(WithDeclarations(text => text.Replace("eStateErrorID : E_PackMLErrorID;", string.Empty)));

        Assert.Equal("GVL_Test.Modules[1].ServiceStatus.A.PackMLOut has no member 'eStateErrorID'.", exception.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_with_module_status_lacking_a_member_names_the_status()
    {
        ServiceHostException exception = BuildFails(WithDeclarations(text =>
            Regex.Replace(text, @"(TYPE ST_ModuleStatus : STRUCT[\s\S]*?)ResponseId : UDINT;", "$1")));

        Assert.Equal("GVL_Test.Modules[1].ModuleStatus has no member 'ResponseId'.", exception.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_with_module_ctrl_lacking_a_member_names_the_ctrl()
    {
        ServiceHostException exception = BuildFails(WithDeclarations(text =>
            Regex.Replace(text, @"(TYPE ST_ModuleCtrl : STRUCT\s+)RequestId : UDINT;", "$1")));

        Assert.Equal("GVL_Test.Modules[1].ModuleCtrl has no member 'RequestId'.", exception.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_with_a_packml_out_of_another_type_reports_diagnostic()
    {
        const string alternativeOut = """
            TYPE ST_Alternative_Out : STRUCT
                eStateCurrent : E_PackMLState;
                eStatePrevious : E_PackMLState;
                eModeCurrent : T_PackMLMode;
                eModePrevious : T_PackMLMode;
                bCommandAccepted : BOOL;
                bCommandRejected : BOOL;
                eStateErrorID : E_PackMLErrorID;
                bModeChanged : BOOL;
                bModeChangeRejected : BOOL;
                eModeErrorID : E_PackMLErrorID;
            END_STRUCT END_TYPE
            """;
        string declarations = WithDeclarations(text => alternativeOut + " " + Regex.Replace(
            text,
            @"(TYPE ST_ServiceStatus : STRUCT\s+ResponseId : UDINT;\s+PackMLOut : )ST_PML_Base_Out;",
            "$1ST_Alternative_Out;"));

        ServiceHostException exception = BuildFails(declarations);

        Assert.Equal("GVL_Test.Modules[1].ServiceStatus.A.PackMLOut is ST_Alternative_Out, not ST_PML_Base_Out.", exception.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Service_ctrl_inside_a_multi_dimensional_array_is_not_a_service()
    {
        string declarations = WithDeclarations(text => text.Replace(
            "Unbound : ST_ServiceCtrl; END_STRUCT",
            "Unbound : ST_ServiceCtrl; Grid : ARRAY[1..2, 1..2] OF ST_ServiceCtrl; END_STRUCT"));
        PlcProgram program = TestPaths.BuildFromText(declarations);

        var host = new ServiceHost(program.Symbols, program.Symbols.Resolve("GVL_Test.Modules"), HostFixture.ParseIdentities(program.Symbols), HostFixture.DefaultDelay, _ => null);

        Assert.Equal(4, host.BoundServices.Count);
    }

    [Fact]
    [Trait("AC", "12")]
    public void Failed_abort_and_unbound_service_messages_name_the_service()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();
        fixture.Host.Abort(PathA, null);

        var queued = Assert.Throws<InvalidOperationException>(() => fixture.Host.Abort(PathA, null));
        var unbound = Assert.Throws<InvalidOperationException>(() => fixture.Host.Complete("GVL_Test.Nothing", null));

        Assert.Equal($"{PathA} cannot take an internal Abort now: a command is queued or a mode request is high.", queued.Message);
        Assert.Equal("GVL_Test.Nothing is not a bound service of GVL_Test.Modules.", unbound.Message);
    }
}
