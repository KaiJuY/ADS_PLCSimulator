using AdsPlcSimulator.Core.PackML.Alarm;

namespace AdsPlcSimulator.Tests.PackML;

public class ModuleAlarmManagerTests
{
    private const uint Module = 7;

    private static ExtAlarm Alarm(uint serviceId, uint mainErrorId, string message = "msg", uint sourceErrorId = 0, string dateTime = "T1") =>
        new(0, serviceId, mainErrorId, sourceErrorId, message, "path", AlarmSeverity.Alarm, dateTime, false, false, 0);

    private static AlarmAckRequest AckOne(uint requestId, uint serviceId, uint mainErrorId) =>
        new(requestId, (long)AlarmAckCommand.AcknowledgeOne, mainErrorId, serviceId, true);

    private static AlarmAckRequest AckAll(uint requestId) =>
        new(requestId, (long)AlarmAckCommand.AcknowledgeAll, 0, 0, true);

    /// <summary>One scan in the order of ServiceHost: begin, acknowledgement, sources, end, publish.</summary>
    private static IReadOnlyList<ExtAlarm?> Cycle(ModuleAlarmManager manager, Action? sources = null, AlarmAckRequest ack = default, uint moduleId = Module)
    {
        manager.BeginCycle(moduleId);
        manager.ProcessAcknowledgement(ack);
        sources?.Invoke();
        manager.EndCycle();
        return manager.Publish();
    }

    [Fact]
    [Trait("AC", "1")]
    public void Service_occurrence_count_changes_only_on_active_edges()
    {
        var manager = new ModuleAlarmManager(100);

        var first = Cycle(manager, () => manager.ObserveServiceAlarm(Alarm(1, 10, "first", 5, "T1")));
        Assert.Equal(1u, first[0]!.OccurrenceCount);
        Assert.True(first[0]!.Active);
        Assert.Equal(Module, first[0]!.ModuleId);

        var steady = Cycle(manager, () => manager.ObserveServiceAlarm(Alarm(1, 10, "second", 6, "T2")));
        Assert.Equal(1u, steady[0]!.OccurrenceCount);

        var gone = Cycle(manager);
        Assert.False(gone[0]!.Active);
        Assert.Equal(1u, gone[0]!.OccurrenceCount);

        var again = Cycle(manager, () => manager.ObserveServiceAlarm(Alarm(1, 10, "third", 7, "T3")));
        Assert.Equal(2u, again[0]!.OccurrenceCount);
        Assert.True(again[0]!.Active);
        Assert.False(again[0]!.Acknowledged);
        Assert.Equal("first", again[0]!.Message);
        Assert.Equal(5u, again[0]!.SourceErrorId);
        Assert.Equal("T1", again[0]!.DateTime);
    }

    [Fact]
    [Trait("AC", "5")]
    public void AcknowledgeOne_uses_service_and_error_id_and_removal_leaves_a_continuous_list()
    {
        var manager = new ModuleAlarmManager(100);
        void Sources()
        {
            manager.ObserveServiceAlarm(Alarm(1, 10));
            manager.ObserveServiceAlarm(Alarm(2, 10));
            manager.ObserveServiceAlarm(Alarm(1, 11));
        }
        Cycle(manager, Sources);

        var acked = Cycle(manager, Sources, AckOne(100, 2, 10));

        Assert.True(manager.Status.AckAccepted);
        Assert.Equal(100u, manager.Status.AckResponseId);
        Assert.Equal([false, true, false], acked.Take(3).Select(a => a!.Acknowledged));

        Cycle(manager, Sources);
        var removed = Cycle(manager, () =>
        {
            manager.ObserveServiceAlarm(Alarm(1, 10));
            manager.ObserveServiceAlarm(Alarm(1, 11));
        });
        Assert.Equal([(1u, 10u), (1u, 11u)], removed.Take(2).Select(a => (a!.ServiceId, a.MainErrorId)));
        Assert.Null(removed[2]);

        var unacked = Cycle(manager, () => manager.ObserveServiceAlarm(Alarm(1, 10)));
        Assert.Equal(2, unacked.Count(a => a is not null));
        Assert.False(unacked[1]!.Active);
        Assert.False(unacked[1]!.Acknowledged);
    }

    [Fact]
    [Trait("AC", "5")]
    public void Acknowledgement_precedes_new_sources_and_a_module_id_change_clears_everything()
    {
        var manager = new ModuleAlarmManager(100);
        var snapshot = Alarm(0, 20);
        Cycle(manager, () =>
        {
            manager.ObserveServiceAlarm(Alarm(1, 10));
            manager.UpdateConfiguredAlarm(1, true, true, snapshot, "TC");
        });

        var list = Cycle(manager, () =>
        {
            manager.ObserveServiceAlarm(Alarm(1, 10));
            manager.ObserveServiceAlarm(Alarm(1, 11));
            manager.UpdateConfiguredAlarm(1, true, true, snapshot, "TC");
            manager.UpdateConfiguredAlarm(2, true, true, Alarm(0, 21), "TC");
        }, AckAll(1));

        Assert.Equal([true, false, true, false], list.Take(4).Select(a => a!.Acknowledged));
        Assert.Equal(0u, list[2]!.ServiceId);
        Assert.Equal(2, manager.Status.ConfiguredLatchedCount);
        Assert.Equal(2, manager.Status.ServiceLatchedCount);

        var cleared = Cycle(manager, moduleId: 8);

        Assert.All(cleared, a => Assert.Null(a));
        Assert.Equal(new AlarmStatus(0, false, AlarmAckError.None, 0, 0, false, 0, 0), manager.Status);
    }

    [Fact]
    [Trait("AC", "4")]
    public void A_forty_first_service_alarm_sets_overflow_and_keeps_existing_alarms()
    {
        var manager = new ModuleAlarmManager(100);

        var list = Cycle(manager, () =>
        {
            for (uint id = 1; id <= 40; id++)
                Assert.True(manager.ObserveServiceAlarm(Alarm(1, id)));
            Assert.False(manager.ObserveServiceAlarm(Alarm(1, 41)));
        });

        Assert.True(manager.Status.Overflow);
        Assert.Equal(40, manager.Status.ServiceLatchedCount);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (uint)i), list.Take(40).Select(a => a!.MainErrorId));
        Assert.Null(list[40]);

        Cycle(manager, () =>
        {
            for (uint id = 1; id <= 40; id++)
                manager.ObserveServiceAlarm(Alarm(1, id));
        });
        Assert.False(manager.Status.Overflow);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Configured_alarm_counts_recurrence_and_acknowledges_with_service_id_zero()
    {
        var manager = new ModuleAlarmManager(100);
        var snapshot = Alarm(0, 20, "cfg", 3, "ignored");

        var first = Cycle(manager, () => manager.UpdateConfiguredAlarm(1, true, true, snapshot, "T1"));
        Assert.Equal(1u, first[0]!.OccurrenceCount);
        Assert.Equal("T1", first[0]!.DateTime);
        Assert.Equal(0u, first[0]!.ServiceId);
        Assert.Equal(Module, first[0]!.ModuleId);

        var steady = Cycle(manager, () => manager.UpdateConfiguredAlarm(1, true, true, snapshot, "T2"));
        Assert.Equal(1u, steady[0]!.OccurrenceCount);

        var cleared = Cycle(manager, () => manager.UpdateConfiguredAlarm(1, true, false, snapshot, "T3"));
        Assert.False(cleared[0]!.Active);

        var invalid = Cycle(manager, () => manager.UpdateConfiguredAlarm(1, false, true, snapshot, "T4"));
        Assert.False(invalid[0]!.Active);
        Assert.Equal(1u, invalid[0]!.OccurrenceCount);

        var again = Cycle(manager, () => manager.UpdateConfiguredAlarm(1, true, true, snapshot, "T5"));
        Assert.Equal(2u, again[0]!.OccurrenceCount);
        Assert.False(again[0]!.Acknowledged);
        Assert.Equal("T1", again[0]!.DateTime);

        var acked = Cycle(manager, () => manager.UpdateConfiguredAlarm(1, true, true, snapshot, "T6"), AckOne(1, 0, 20));
        Assert.True(manager.Status.AckAccepted);
        Assert.True(acked[0]!.Acknowledged);
        Assert.True(acked[0]!.Active);

        Cycle(manager, () => manager.UpdateConfiguredAlarm(1, true, true, snapshot, "T7"));
        var duplicate = Cycle(manager, () => manager.UpdateConfiguredAlarm(1, true, true, snapshot, "T8"), AckOne(1, 0, 20));
        Assert.Equal(AlarmAckError.DuplicateRequest, manager.Status.AckError);
        Assert.False(manager.Status.AckAccepted);
        Assert.NotNull(duplicate[0]);

        var removed = Cycle(manager, () => manager.UpdateConfiguredAlarm(1, true, false, snapshot, "T9"));
        Assert.Null(removed[0]);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Configured_alarm_with_an_invalid_index_is_ignored()
    {
        var manager = new ModuleAlarmManager(100);

        var list = Cycle(manager, () =>
        {
            manager.UpdateConfiguredAlarm(0, true, true, Alarm(0, 1), "T");
            manager.UpdateConfiguredAlarm(31, true, true, Alarm(0, 2), "T");
        });

        Assert.All(list, a => Assert.Null(a));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Hook_alarm_takes_the_module_id_and_handles_duplicates_and_recurrence()
    {
        var manager = new ModuleAlarmManager(100);

        var first = Cycle(manager, () =>
        {
            Assert.True(manager.ObserveHookAlarm(Alarm(0, 30, "first", 1, "T1")));
            Assert.False(manager.ObserveHookAlarm(Alarm(0, 30, "dup", 2, "T2")));
        });
        Assert.Equal(Module, first[0]!.ModuleId);
        Assert.Equal(1u, first[0]!.OccurrenceCount);
        Assert.Equal("first", first[0]!.Message);
        Assert.Equal(30u, manager.Status.DuplicateId);

        var gone = Cycle(manager);
        Assert.False(gone[0]!.Active);
        Assert.Equal(0u, manager.Status.DuplicateId);

        var again = Cycle(manager, () => manager.ObserveHookAlarm(Alarm(0, 30, "third", 3, "T3")));
        Assert.Equal(2u, again[0]!.OccurrenceCount);
        Assert.False(again[0]!.Acknowledged);
        Assert.Equal("first", again[0]!.Message);

        var activeAck = Cycle(manager, () => manager.ObserveHookAlarm(Alarm(0, 30)), AckOne(1, 0, 30));
        Assert.True(activeAck[0]!.Acknowledged);
        Assert.True(activeAck[0]!.Active);

        Cycle(manager, () => manager.ObserveHookAlarm(Alarm(0, 30)));
        var removed = Cycle(manager);
        Assert.Null(removed[0]);

        var fresh = Cycle(manager, () => manager.ObserveHookAlarm(Alarm(0, 30, "new", 9, "T9")));
        Assert.Equal(1u, fresh[0]!.OccurrenceCount);
        Assert.Equal("new", fresh[0]!.Message);
        Assert.Equal("T9", fresh[0]!.DateTime);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Hook_and_service_observation_reject_ids_that_belong_to_the_other_source()
    {
        var manager = new ModuleAlarmManager(100);
        manager.BeginCycle(Module);

        Assert.False(manager.ObserveHookAlarm(Alarm(0, 0)));
        Assert.False(manager.ObserveHookAlarm(Alarm(1, 5)));
        Assert.False(manager.ObserveServiceAlarm(Alarm(0, 5)));
        Assert.False(manager.ObserveServiceAlarm(Alarm(1, 0)));
        Assert.All(manager.Publish(), a => Assert.Null(a));
    }

    [Fact]
    [Trait("AC", "5")]
    public void Publish_order_is_service_configured_hook_and_AcknowledgeAll_skips_alarms_new_in_the_same_scan()
    {
        var manager = new ModuleAlarmManager(100);
        void Sources(bool withNewHook)
        {
            manager.ObserveHookAlarm(Alarm(0, 30));
            manager.UpdateConfiguredAlarm(1, true, true, Alarm(0, 20), "TC");
            manager.ObserveServiceAlarm(Alarm(1, 10));
            if (withNewHook)
                manager.ObserveHookAlarm(Alarm(0, 31));
        }

        var order = Cycle(manager, () => Sources(false));
        Assert.Equal([10u, 20u, 30u], order.Take(3).Select(a => a!.MainErrorId));

        var acked = Cycle(manager, () => Sources(true), AckAll(1));

        Assert.True(manager.Status.AckAccepted);
        Assert.Equal([10u, 20u, 30u, 31u], acked.Take(4).Select(a => a!.MainErrorId));
        Assert.Equal([true, true, true, false], acked.Take(4).Select(a => a!.Acknowledged));
    }

    [Fact]
    [Trait("AC", "4")]
    public void Capacity_fills_all_one_hundred_entries_in_order_and_a_thirty_first_hook_overflows()
    {
        var manager = new ModuleAlarmManager(100);

        var list = Cycle(manager, () =>
        {
            for (uint i = 1; i <= 30; i++)
            {
                manager.ObserveHookAlarm(Alarm(0, 300 + i));
                manager.UpdateConfiguredAlarm((int)i, true, true, Alarm(0, 200 + i), "TC");
            }
            for (uint i = 1; i <= 40; i++)
                manager.ObserveServiceAlarm(Alarm(1, 100 + i));
        });

        Assert.All(list, a => Assert.NotNull(a));
        Assert.Equal(100, list.Count);
        Assert.Equal(101u, list[0]!.MainErrorId);
        Assert.Equal(140u, list[39]!.MainErrorId);
        Assert.Equal(201u, list[40]!.MainErrorId);
        Assert.Equal(330u, list[99]!.MainErrorId);
        Assert.False(manager.Status.Overflow);

        Cycle(manager, () => Assert.False(manager.ObserveHookAlarm(Alarm(0, 999))));
        Assert.True(manager.Status.Overflow);
    }

    [Fact]
    [Trait("AC", "4")]
    public void A_smaller_publish_capacity_overflows_but_still_counts_every_latched_alarm()
    {
        var manager = new ModuleAlarmManager(5);

        var list = Cycle(manager, () =>
        {
            for (uint i = 1; i <= 3; i++)
            {
                manager.ObserveServiceAlarm(Alarm(1, i));
                manager.UpdateConfiguredAlarm((int)i, true, true, Alarm(0, 20 + i), "TC");
            }
            manager.ObserveHookAlarm(Alarm(0, 30));
        });

        Assert.Equal(5, list.Count);
        Assert.Equal([1u, 2u, 3u, 21u, 22u], list.Select(a => a!.MainErrorId));
        Assert.True(manager.Status.Overflow);
        Assert.Equal(3, manager.Status.ServiceLatchedCount);
        Assert.Equal(3, manager.Status.ConfiguredLatchedCount);
        Assert.Equal(1, manager.Status.HookLatchedCount);
    }

    [Fact]
    [Trait("AC", "5")]
    public void Acknowledgement_errors_are_reported_and_the_response_is_held_until_the_request_is_released()
    {
        var manager = new ModuleAlarmManager(100);
        Action source = () => manager.ObserveServiceAlarm(Alarm(1, 10));
        Cycle(manager, source);

        Cycle(manager, source, new AlarmAckRequest(5, 3, 10, 1, true));
        Assert.Equal(new AlarmStatus(5, false, AlarmAckError.InvalidCommand, 0, 1, false, 0, 0), manager.Status);

        Cycle(manager, source, new AlarmAckRequest(5, 3, 10, 1, false));
        Assert.Equal(5u, manager.Status.AckResponseId);
        Assert.False(manager.Status.AckAccepted);
        Assert.Equal(AlarmAckError.None, manager.Status.AckError);

        Cycle(manager, source, new AlarmAckRequest(5, 1, 10, 1, true));
        Assert.Equal(AlarmAckError.DuplicateRequest, manager.Status.AckError);

        Cycle(manager, source, new AlarmAckRequest(5, 1, 10, 1, false));
        Cycle(manager, source, new AlarmAckRequest(0, 1, 10, 1, true));
        Assert.Equal(AlarmAckError.InvalidRequestId, manager.Status.AckError);
        Assert.Equal(0u, manager.Status.AckResponseId);
    }

    [Theory]
    [InlineData(0L, 10u, 1u, AlarmAckError.InvalidCommand)]
    [InlineData(3L, 10u, 1u, AlarmAckError.InvalidCommand)]
    [InlineData(-1L, 10u, 1u, AlarmAckError.InvalidCommand)]
    [InlineData(1L, 0u, 1u, AlarmAckError.AlarmNotFound)]
    [InlineData(1L, 99u, 1u, AlarmAckError.AlarmNotFound)]
    [InlineData(1L, 10u, 2u, AlarmAckError.AlarmNotFound)]
    [InlineData(1L, 10u, 1u, AlarmAckError.None)]
    [Trait("AC", "5")]
    public void Request_is_judged_by_command_and_alarm_identity(long command, uint mainErrorId, uint serviceId, AlarmAckError expected)
    {
        var manager = new ModuleAlarmManager(100);
        Action source = () => manager.ObserveServiceAlarm(Alarm(1, 10));
        Cycle(manager, source);

        Cycle(manager, source, new AlarmAckRequest(1, command, mainErrorId, serviceId, true));

        Assert.Equal(expected, manager.Status.AckError);
        Assert.Equal(expected == AlarmAckError.None, manager.Status.AckAccepted);
    }

    [Fact]
    [Trait("AC", "5")]
    public void A_request_that_stays_high_is_processed_once()
    {
        var manager = new ModuleAlarmManager(100);
        Action source = () => manager.ObserveServiceAlarm(Alarm(1, 10));
        Cycle(manager, source);
        var request = new AlarmAckRequest(1, 1, 10, 1, true);

        Cycle(manager, source, request);
        Cycle(manager, source, request with { RequestId = 2 });

        Assert.Equal(1u, manager.Status.AckResponseId);
        Assert.True(manager.Status.AckAccepted);
    }

    [Fact]
    [Trait("AC", "3")]
    public void SuppressHookAlarm_removes_a_latched_hook_alarm_that_the_configuration_now_owns()
    {
        var manager = new ModuleAlarmManager(100);
        Cycle(manager, () => manager.ObserveHookAlarm(Alarm(0, 30)));

        manager.BeginCycle(Module);
        manager.SuppressHookAlarm(30);
        manager.EndCycle();
        var list = manager.Publish();

        Assert.All(list, a => Assert.Null(a));
        Assert.Equal(0, manager.Status.HookLatchedCount);
    }

    [Fact]
    [Trait("AC", "3")]
    public void SuppressHookAlarm_keeps_hook_alarms_with_other_ids()
    {
        var manager = new ModuleAlarmManager(100);
        Cycle(manager, () => manager.ObserveHookAlarm(Alarm(0, 30)));

        manager.BeginCycle(Module);
        manager.SuppressHookAlarm(31);
        manager.ObserveHookAlarm(Alarm(0, 30));
        manager.EndCycle();

        Assert.Equal(30u, manager.Publish()[0]!.MainErrorId);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Publish_changes_no_lifecycle()
    {
        var manager = new ModuleAlarmManager(100);
        Cycle(manager, () => manager.ObserveServiceAlarm(Alarm(1, 10)));

        var again = manager.Publish();

        Assert.True(again[0]!.Active);
        Assert.Equal(1u, again[0]!.OccurrenceCount);
    }
}
