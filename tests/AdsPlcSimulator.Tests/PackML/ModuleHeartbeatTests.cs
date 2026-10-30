using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class ModuleHeartbeatTests
{
    private const string Index1 = "GVL_Test.Modules[1].HeartbeatIndex";
    private const string Flag1 = "GVL_Test.Modules[1].ModuleStatus.IsControlSourceAlive";
    private const string Flag2 = "GVL_Test.Modules[2].ModuleStatus.IsControlSourceAlive";

    private static HostFixture Create(AliveSettings? alive = null) =>
        new(declarations: HostFixture.HeartbeatDeclarations, alive: alive);

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    [Trait("AC", "1")]
    public void Initialize_sets_the_flag_TRUE()
    {
        var fixture = Create();
        fixture.Write(Flag1, false);

        fixture.Host.Initialize(fixture.Image);

        Assert.Equal(true, fixture.Read(Flag1));
        Assert.Equal(true, fixture.Read(Flag2));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Flag_turns_FALSE_when_the_index_is_unchanged_for_the_timeout()
    {
        var fixture = Create();

        fixture.ScanAt(Seconds(0));
        fixture.ScanAt(Seconds(9.99));
        Assert.Equal(true, fixture.Read(Flag1));

        fixture.ScanAt(Seconds(10));
        Assert.Equal(false, fixture.Read(Flag1));
        Assert.Equal(false, fixture.Read(Flag2));
    }

    [Fact]
    [Trait("AC", "3")]
    public void Change_after_the_timeout_turns_the_flag_TRUE_and_restarts_the_timer()
    {
        var fixture = Create();
        fixture.ScanAt(Seconds(0));
        fixture.ScanAt(Seconds(10));
        Assert.Equal(false, fixture.Read(Flag1));

        fixture.Write(Index1, 1L);
        fixture.ScanAt(Seconds(10.5));
        Assert.Equal(true, fixture.Read(Flag1));

        fixture.ScanAt(Seconds(20.49));
        Assert.Equal(true, fixture.Read(Flag1));
        fixture.ScanAt(Seconds(20.5));
        Assert.Equal(false, fixture.Read(Flag1));
    }

    [Fact]
    [Trait("AC", "1")]
    public void Index_changed_every_5_seconds_keeps_the_flag_TRUE()
    {
        var fixture = Create();

        for (int step = 0; step <= 12; step++)
        {
            fixture.Write(Index1, (long)step);
            fixture.ScanAt(Seconds(step * 5));
            Assert.Equal(true, fixture.Read(Flag1));
        }
    }

    [Fact]
    [Trait("AC", "2")]
    public void Initial_index_that_is_not_zero_does_not_count_as_a_change()
    {
        var fixture = Create();
        fixture.Write(Index1, 7L);
        fixture.Host.Initialize(fixture.Image);

        fixture.ScanAt(Seconds(0));
        fixture.ScanAt(Seconds(9.99));
        Assert.Equal(true, fixture.Read(Flag1));

        fixture.ScanAt(Seconds(10));
        Assert.Equal(false, fixture.Read(Flag1));
    }

    [Fact]
    [Trait("AC", "1")]
    public void Initialize_again_sets_the_flag_TRUE_and_restarts_the_timer()
    {
        var fixture = Create();
        fixture.ScanAt(Seconds(0));
        fixture.ScanAt(Seconds(10));
        Assert.Equal(false, fixture.Read(Flag1));

        fixture.Host.Initialize(fixture.Image);
        Assert.Equal(true, fixture.Read(Flag1));

        fixture.ScanAt(Seconds(11));
        fixture.ScanAt(Seconds(20.99));
        Assert.Equal(true, fixture.Read(Flag1));
        fixture.ScanAt(Seconds(21));
        Assert.Equal(false, fixture.Read(Flag1));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Custom_timeout_is_used()
    {
        var fixture = Create(new AliveSettings(Seconds(2), Seconds(1), Seconds(10)));

        fixture.ScanAt(Seconds(0));
        fixture.ScanAt(Seconds(1.99));
        Assert.Equal(true, fixture.Read(Flag1));

        fixture.ScanAt(Seconds(2));
        Assert.Equal(false, fixture.Read(Flag1));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Module_without_HeartbeatIndex_leaves_the_flag_alone_and_PackML_runs()
    {
        var fixture = new HostFixture();

        fixture.ScanAt(Seconds(0));
        fixture.ScanAt(Seconds(60));
        fixture.StartServiceA();

        Assert.Equal(false, fixture.Read(Flag1));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Module_whose_HeartbeatIndex_is_not_UDINT_has_no_heartbeat()
    {
        var fixture = new HostFixture(declarations: HostFixture.HeartbeatDeclarations.Replace("HeartbeatIndex : UDINT;", "HeartbeatIndex : INT;"));

        fixture.ScanAt(Seconds(0));
        fixture.ScanAt(Seconds(60));

        Assert.Equal(false, fixture.Read(Flag1));
    }

    [Fact]
    [Trait("AC", "1")]
    public void Flag_is_written_only_when_it_changes()
    {
        var fixture = Create();
        SymbolRef flag = fixture.Symbols.Resolve(Flag1);
        int writes = 0;
        fixture.Image.Written += write =>
        {
            if (write.Overlaps(flag.Offset, flag.Size))
                writes++;
        };

        fixture.ScanAt(Seconds(0));
        fixture.ScanAt(Seconds(5));
        Assert.Equal(0, writes);

        fixture.ScanAt(Seconds(10));
        fixture.ScanAt(Seconds(11));
        Assert.Equal(1, writes);

        fixture.Write(Index1, 3L);
        writes = 0;
        fixture.ScanAt(Seconds(12));
        fixture.ScanAt(Seconds(13));
        Assert.Equal(1, writes);
    }
}
