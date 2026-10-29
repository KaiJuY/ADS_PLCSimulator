using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class ServiceHostForwardingTests
{
    private static readonly string PathA = HostFixture.CtrlPath(1, "A");

    private static void BringModuleBackToExecute(HostFixture fixture)
    {
        fixture.ModuleCommand(1, PackMLCommand.Reset, 11);
        fixture.ModuleCommand(1, PackMLCommand.Start, 12);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Command_request_held_through_an_even_number_of_scans_stays_ignored_after_the_module_returns_to_execute()
    {
        var fixture = new HostFixture();
        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);
        fixture.SendToService(1, "A", PackMLCommand.Start, 5);
        fixture.Scan();
        fixture.Scan();

        BringModuleBackToExecute(fixture);

        Assert.Equal(PackMLState.Execute, fixture.ModuleState(1));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Mode_request_held_through_an_even_number_of_scans_stays_ignored_after_the_module_returns_to_execute()
    {
        var fixture = new HostFixture();
        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);
        fixture.Write($"{PathA}.PackMLIn.eMode", 2L);
        fixture.Write($"{PathA}.PackMLIn.bModeChangeRequest", true);
        fixture.Scan();
        fixture.Scan();

        BringModuleBackToExecute(fixture);

        Assert.Equal(PackMLState.Execute, fixture.ModuleState(1));
        Assert.Equal(false, fixture.ServiceStatus(1, "A", "PackMLOut.bModeChanged"));
        Assert.Equal(0L, fixture.ServiceStatus(1, "A", "PackMLOut.eModeCurrent"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_command_that_is_not_forwarded_does_not_reach_the_services_even_when_the_module_rejects_it()
    {
        var fixture = new HostFixture();
        fixture.ModuleCommand(1, PackMLCommand.Stop, 10);

        fixture.SendToModule(1, PackMLCommand.Start, 20);
        fixture.Scan();

        Assert.Equal(true, fixture.ModuleStatus(1, "PackMLOut.bCommandRejected"));
        Assert.Equal(false, fixture.ServiceStatus(1, "A", "PackMLOut.bCommandRejected"));
        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_stop_waits_while_one_service_cannot_stop()
    {
        var fixture = new HostFixture();
        fixture.StartServiceA();
        fixture.Host.Abort(PathA, null);
        fixture.Scan();
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));

        fixture.SendToModule(1, PackMLCommand.Stop, 10);
        fixture.Scan();
        fixture.Scan();
        fixture.Scan();

        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "B"));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
        Assert.Equal(PackMLState.Stopping, fixture.ModuleState(1));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_request_held_before_initialize_is_ignored_until_it_goes_low()
    {
        var fixture = new HostFixture(initialize: false);
        fixture.SendToModule(1, PackMLCommand.Stop, 10);

        fixture.Host.Initialize(fixture.Image);
        fixture.Scan();

        Assert.Equal(PackMLState.Execute, fixture.ModuleState(1));
        Assert.Equal(0L, fixture.ModuleStatus(1, "ResponseId"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_response_id_follows_only_the_rising_edge_of_a_request()
    {
        var fixture = new HostFixture();
        string ctrl = "GVL_Test.Modules[1].ModuleCtrl";

        fixture.SendToModule(1, PackMLCommand.Hold, 31);
        fixture.Scan();
        fixture.Write($"{ctrl}.RequestId", 32L);
        fixture.Scan();
        Assert.Equal(31L, fixture.ModuleStatus(1, "ResponseId"));

        fixture.ReleaseModule(1);
        fixture.Write($"{ctrl}.RequestId", 33L);
        fixture.Scan();
        Assert.Equal(31L, fixture.ModuleStatus(1, "ResponseId"));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Service_status_publishes_current_and_previous_mode_and_the_mode_changed_flag()
    {
        var fixture = new HostFixture();

        fixture.Write($"{PathA}.PackMLIn.eMode", 2L);
        fixture.Write($"{PathA}.PackMLIn.bModeChangeRequest", true);
        fixture.Scan();
        Assert.Equal(2L, fixture.ServiceStatus(1, "A", "PackMLOut.eModeCurrent"));
        Assert.Equal(0L, fixture.ServiceStatus(1, "A", "PackMLOut.eModePrevious"));
        Assert.Equal(true, fixture.ServiceStatus(1, "A", "PackMLOut.bModeChanged"));

        fixture.Write($"{PathA}.PackMLIn.bModeChangeRequest", false);
        fixture.Scan();
        Assert.Equal(false, fixture.ServiceStatus(1, "A", "PackMLOut.bModeChanged"));

        fixture.Write($"{PathA}.PackMLIn.eMode", 1L);
        fixture.Write($"{PathA}.PackMLIn.bModeChangeRequest", true);
        fixture.Scan();
        Assert.Equal(1L, fixture.ServiceStatus(1, "A", "PackMLOut.eModeCurrent"));
        Assert.Equal(2L, fixture.ServiceStatus(1, "A", "PackMLOut.eModePrevious"));
        Assert.Equal(true, fixture.ServiceStatus(1, "A", "PackMLOut.bModeChanged"));
    }
}
