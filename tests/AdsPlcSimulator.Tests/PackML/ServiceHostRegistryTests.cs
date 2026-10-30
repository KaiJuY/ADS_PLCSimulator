using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class ServiceHostRegistryTests
{
    private const string PathA = "GVL_Test.Modules[1].ServiceCtrl.A";

    private static ServiceHost NewHost(HostFixture fixture) =>
        HostFixture.CreateHost(fixture.Program, fixture.Image, HostFixture.DefaultDelay, _ => null);

    [Fact]
    [Trait("AC", "13")]
    public void Registered_host_is_found_by_its_bound_service_ignoring_case()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        registry.Register("host", fixture.Host);

        Assert.Same(fixture.Host, registry.FindByService(PathA));
        Assert.Same(fixture.Host, registry.FindByService(PathA.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("GVL_Test.Modules[1].ServiceCtrl.Unbound")]
    [InlineData("GVL_Test.Other")]
    [Trait("AC", "13")]
    public void Service_that_no_host_binds_is_not_found(string path)
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        registry.Register("host", fixture.Host);

        Assert.Null(registry.FindByService(path));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Module_is_found_only_when_a_registered_host_runs_it()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        registry.Register("host", fixture.Host);

        Assert.Same(fixture.Host, registry.FindByModule("GVL_Test.Modules[2]"));
        Assert.Null(registry.FindByModule("GVL_Test.Modules[9]"));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Same_workflow_replaces_the_host_and_retires_the_old_one()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        ServiceHost old = fixture.Host;
        registry.Register("host", old);
        ServiceHost replacement = NewHost(fixture);

        registry.Register("HOST", replacement);

        Assert.Same(replacement, registry.FindByService(PathA));
        fixture.SendToService(1, "A", PackMLCommand.Start, 1);
        var writes = new List<MemoryWrite>();
        fixture.Image.Written += writes.Add;
        old.Scan(fixture.Image, TimeSpan.Zero);
        Assert.Empty(writes);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Other_workflow_on_the_same_root_is_refused()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        registry.Register("first", fixture.Host);

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Register("second", NewHost(fixture)));

        Assert.Contains("first", exception.Message);
        Assert.Same(fixture.Host, registry.FindByService(PathA));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Unregister_removes_the_registered_host()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        registry.Register("host", fixture.Host);

        registry.Unregister(fixture.Host);

        Assert.Null(registry.FindByService(PathA));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Unregister_of_a_replaced_host_keeps_the_new_one()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        ServiceHost old = fixture.Host;
        registry.Register("host", old);
        ServiceHost replacement = NewHost(fixture);
        registry.Register("host", replacement);

        registry.Unregister(old);

        Assert.Same(replacement, registry.FindByService(PathA));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Root_is_free_again_after_unregister()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        registry.Register("first", fixture.Host);
        registry.Unregister(fixture.Host);

        registry.Register("second", NewHost(fixture));

        Assert.NotNull(registry.FindByService(PathA));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Service_without_override_has_no_token()
    {
        var registry = new ServiceHostRegistry();

        Assert.Null(registry.OverrideToken(PathA));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Override_gives_a_token_that_is_the_same_until_the_workflow_sets_overrides_again()
    {
        var registry = new ServiceHostRegistry();
        registry.SetOverrides("wf", [PathA]);
        object? first = registry.OverrideToken(PathA);

        Assert.NotNull(first);
        Assert.Same(first, registry.OverrideToken(PathA.ToUpperInvariant()));

        registry.SetOverrides("wf", [PathA]);
        Assert.NotSame(first, registry.OverrideToken(PathA));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Empty_override_list_removes_the_workflow_override()
    {
        var registry = new ServiceHostRegistry();
        registry.SetOverrides("wf", [PathA]);

        registry.SetOverrides("wf", []);

        Assert.Null(registry.OverrideToken(PathA));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Workflow_that_set_overrides_last_wins_and_the_other_takes_over_again_when_it_leaves()
    {
        var registry = new ServiceHostRegistry();
        registry.SetOverrides("first", [PathA]);
        object? firstToken = registry.OverrideToken(PathA);
        registry.SetOverrides("second", [PathA]);
        object? secondToken = registry.OverrideToken(PathA);

        Assert.NotSame(firstToken, secondToken);

        registry.SetOverrides("second", []);
        Assert.Same(firstToken, registry.OverrideToken(PathA));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Setting_overrides_of_one_workflow_does_not_touch_another_workflows_paths()
    {
        var registry = new ServiceHostRegistry();
        registry.SetOverrides("first", [PathA]);
        object? firstToken = registry.OverrideToken(PathA);

        registry.SetOverrides("second", ["GVL_Test.Modules[2].ServiceCtrl.A"]);

        Assert.Same(firstToken, registry.OverrideToken(PathA));
        Assert.NotNull(registry.OverrideToken("GVL_Test.Modules[2].ServiceCtrl.A"));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Refused_registration_does_not_retire_the_host_of_the_other_workflow()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        registry.Register("first", fixture.Host);

        Assert.Throws<InvalidOperationException>(() => registry.Register("second", NewHost(fixture)));

        fixture.SendToService(1, "A", PackMLCommand.Start, 1);
        fixture.Scan();
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
    }

    private sealed record RaceResult(ServiceHostRegistry Registry, Exception? Refusal, ServiceHost Replacement, ServiceHost Taker);

    /// <summary>
    /// Registers a replacement of "first" while the old host is held busy by a scan, so the registering thread can only wait for the
    /// host lock. While it waits, the old host is unregistered and a taker registers the same root.
    /// </summary>
    private static async Task<RaceResult> RegisterReplacementWhileOldHostRetiresAsync(string takerWorkflow)
    {
        var fixture = new HostFixture(initialize: false);
        using var scanning = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ServiceHost old = HostFixture.CreateHost(fixture.Program, fixture.Image, HostFixture.DefaultDelay, _ =>
        {
            scanning.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return null;
        });
        old.Initialize(fixture.Image);
        var registry = new ServiceHostRegistry();
        registry.Register("first", old);
        fixture.SendToService(1, "A", PackMLCommand.Start, 1);
        var scan = Task.Run(() => old.Scan(fixture.Image, TimeSpan.Zero));
        Assert.True(scanning.Wait(TimeSpan.FromSeconds(10)), "the scan did not reach the override callback");
        ServiceHost replacement = NewHost(fixture);
        ServiceHost taker = NewHost(fixture);

        Exception? refusal = null;
        var replacing = new Thread(() =>
        {
            try
            {
                registry.Register("first", replacement);
            }
            catch (InvalidOperationException exception)
            {
                refusal = exception;
            }
        });
        replacing.Start();
        Assert.True(
            SpinWait.SpinUntil(() => replacing.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10)),
            "the registering thread did not wait for the host lock");
        registry.Unregister(old);
        registry.Register(takerWorkflow, taker);
        release.Set();
        Assert.True(replacing.Join(TimeSpan.FromSeconds(10)), "the registering thread did not finish");
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        return new RaceResult(registry, refusal, replacement, taker);
    }

    [Fact]
    [Trait("AC", "13")]
    public async Task Replacement_is_refused_when_another_workflow_takes_the_root_while_the_old_host_retires()
    {
        RaceResult result = await RegisterReplacementWhileOldHostRetiresAsync("second");

        Assert.NotNull(result.Refusal);
        Assert.Contains("second", result.Refusal.Message);
        Assert.Same(result.Taker, result.Registry.FindByService(PathA));
    }

    [Fact]
    [Trait("AC", "13")]
    public async Task Replacement_does_not_overwrite_a_host_that_the_same_workflow_registered_while_the_old_host_retires()
    {
        RaceResult result = await RegisterReplacementWhileOldHostRetiresAsync("first");

        Assert.NotNull(result.Refusal);
        Assert.Contains("replaced", result.Refusal.Message);
        Assert.Same(result.Taker, result.Registry.FindByService(PathA));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Replacement_registers_when_the_old_host_was_unregistered_meanwhile_and_nobody_took_the_root()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        ServiceHost old = fixture.Host;
        registry.Register("host", old);
        registry.Unregister(old);
        ServiceHost replacement = NewHost(fixture);

        registry.Register("host", replacement);

        Assert.Same(replacement, registry.FindByService(PathA));
    }
}
