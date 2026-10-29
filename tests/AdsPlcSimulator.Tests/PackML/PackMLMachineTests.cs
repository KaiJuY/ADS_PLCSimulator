using AdsPlcSimulator.Core.PackML;

namespace AdsPlcSimulator.Tests.PackML;

public class PackMLMachineTests
{
    private static readonly Func<PackMLState, bool> NeverDone = _ => false;
    private static readonly Func<PackMLState, bool> AlwaysDone = _ => true;

    private static PackMLInput Command(PackMLCommand command, bool request = true) => new((long)command, request, 0, false);

    private static PackMLInput Mode(long mode, bool request = true) => new(0, false, mode, request);

    private static PackMLMachine MachineIn(PackMLState state) => new(state, PackMLState.Undefined);

    [Theory]
    [InlineData(PackMLCommand.Clear, PackMLState.Idle, PackMLErrorId.ClearNotAllowed)]
    [InlineData(PackMLCommand.Reset, PackMLState.Idle, PackMLErrorId.ResetNotAllowed)]
    [InlineData(PackMLCommand.Start, PackMLState.Stopped, PackMLErrorId.StartNotAllowed)]
    [InlineData(PackMLCommand.Stop, PackMLState.Stopped, PackMLErrorId.StopNotAllowed)]
    [InlineData(PackMLCommand.Hold, PackMLState.Idle, PackMLErrorId.HoldNotAllowed)]
    [InlineData(PackMLCommand.Unhold, PackMLState.Execute, PackMLErrorId.UnholdNotAllowed)]
    [InlineData(PackMLCommand.Suspend, PackMLState.Idle, PackMLErrorId.SuspendNotAllowed)]
    [InlineData(PackMLCommand.Unsuspend, PackMLState.Execute, PackMLErrorId.UnsuspendNotAllowed)]
    [InlineData(PackMLCommand.Abort, PackMLState.Aborted, PackMLErrorId.AbortNotAllowed)]
    [Trait("AC", "6")]
    public void Command_from_a_state_that_does_not_allow_it_is_rejected_with_its_error_id(PackMLCommand command, PackMLState state, PackMLErrorId expected)
    {
        PackMLMachine machine = MachineIn(state);

        machine.Scan(Command(command), NeverDone);

        Assert.True(machine.CommandRejected);
        Assert.False(machine.CommandAccepted);
        Assert.Equal(expected, machine.StateErrorId);
        Assert.Equal(state, machine.State);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(-1)]
    [Trait("AC", "6")]
    public void Unknown_command_value_sets_neither_accepted_nor_rejected(long command)
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);

        machine.Scan(new PackMLInput(command, true, 0, false), NeverDone);

        Assert.False(machine.CommandAccepted);
        Assert.False(machine.CommandRejected);
        Assert.Equal(PackMLState.Idle, machine.State);
    }

    [Fact]
    [Trait("AC", "6")]
    public void Command_value_nine_is_clear_and_is_accepted_from_aborted()
    {
        PackMLMachine machine = MachineIn(PackMLState.Aborted);

        machine.Scan(new PackMLInput(9, true, 0, false), NeverDone);

        Assert.True(machine.CommandAccepted);
        Assert.Equal(PackMLState.Clearing, machine.State);
    }

    [Fact]
    [Trait("AC", "6")]
    public void Command_none_is_not_consumed()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);

        machine.Scan(Command(PackMLCommand.None), NeverDone);
        machine.Scan(Command(PackMLCommand.Start), NeverDone);

        Assert.True(machine.CommandAccepted);
        Assert.Equal(PackMLState.Starting, machine.State);
    }

    [Fact]
    [Trait("AC", "4")]
    public void Request_low_clears_accepted_rejected_and_error()
    {
        PackMLMachine machine = MachineIn(PackMLState.Stopped);
        machine.Scan(Command(PackMLCommand.Start), NeverDone);
        Assert.True(machine.CommandRejected);

        machine.Scan(Command(PackMLCommand.Start, request: false), NeverDone);

        Assert.False(machine.CommandRejected);
        Assert.False(machine.CommandAccepted);
        Assert.Equal(PackMLErrorId.NoError, machine.StateErrorId);
    }

    [Fact]
    [Trait("AC", "4")]
    public void Held_request_runs_command_once()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);

        machine.Scan(Command(PackMLCommand.Start), NeverDone);
        machine.Scan(Command(PackMLCommand.Start), NeverDone);
        machine.Scan(Command(PackMLCommand.Start), NeverDone);

        Assert.True(machine.CommandAccepted);
        Assert.False(machine.CommandRejected);
        Assert.Equal(PackMLState.Starting, machine.State);
    }

    [Fact]
    [Trait("AC", "4")]
    public void Request_can_run_again_after_it_went_low()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);
        machine.Scan(Command(PackMLCommand.Start), AlwaysDone);
        machine.Scan(Command(PackMLCommand.Start, request: false), NeverDone);

        machine.Scan(Command(PackMLCommand.Start), NeverDone);

        Assert.True(machine.CommandRejected);
        Assert.Equal(PackMLErrorId.StartNotAllowed, machine.StateErrorId);
    }

    [Theory]
    [InlineData(PackMLState.Undefined)]
    [InlineData(PackMLState.Clearing)]
    [InlineData(PackMLState.Stopped)]
    [InlineData(PackMLState.Starting)]
    [InlineData(PackMLState.Idle)]
    [InlineData(PackMLState.Suspended)]
    [InlineData(PackMLState.Execute)]
    [InlineData(PackMLState.Stopping)]
    [InlineData(PackMLState.Holding)]
    [InlineData(PackMLState.Held)]
    [InlineData(PackMLState.Unholding)]
    [InlineData(PackMLState.Suspending)]
    [InlineData(PackMLState.Unsuspending)]
    [InlineData(PackMLState.Resetting)]
    [InlineData(PackMLState.Completing)]
    [InlineData(PackMLState.Complete)]
    [Trait("AC", "7")]
    public void Abort_from_any_state_except_aborting_and_aborted(PackMLState state)
    {
        PackMLMachine machine = new(state, PackMLState.Undefined);

        machine.Scan(Command(PackMLCommand.Abort), NeverDone);

        Assert.True(machine.CommandAccepted);
        Assert.Equal(PackMLState.Aborting, machine.State);
        Assert.Equal(state, machine.PreviousState);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Abort_while_aborting_is_rejected()
    {
        PackMLMachine machine = MachineIn(PackMLState.Aborting);

        machine.Scan(Command(PackMLCommand.Abort), NeverDone);

        Assert.True(machine.CommandRejected);
        Assert.Equal(PackMLErrorId.AbortNotAllowed, machine.StateErrorId);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Clear_from_aborted_goes_to_stopped()
    {
        PackMLMachine machine = MachineIn(PackMLState.Aborted);

        machine.Scan(Command(PackMLCommand.Clear), AlwaysDone);

        Assert.Equal(PackMLState.Stopped, machine.State);
        Assert.Equal(PackMLState.Clearing, machine.PreviousState);
    }

    [Theory]
    [InlineData(PackMLState.Stopped)]
    [InlineData(PackMLState.Complete)]
    [Trait("AC", "7")]
    public void Reset_from_stopped_and_complete_goes_to_idle(PackMLState state)
    {
        PackMLMachine machine = MachineIn(state);

        machine.Scan(Command(PackMLCommand.Reset), AlwaysDone);

        Assert.Equal(PackMLState.Idle, machine.State);
        Assert.Equal(PackMLState.Resetting, machine.PreviousState);
    }

    [Theory]
    [InlineData(PackMLCommand.Hold, PackMLState.Execute, PackMLState.Held)]
    [InlineData(PackMLCommand.Unhold, PackMLState.Held, PackMLState.Execute)]
    [InlineData(PackMLCommand.Suspend, PackMLState.Execute, PackMLState.Suspended)]
    [InlineData(PackMLCommand.Unsuspend, PackMLState.Suspended, PackMLState.Execute)]
    [InlineData(PackMLCommand.Stop, PackMLState.Execute, PackMLState.Stopped)]
    [InlineData(PackMLCommand.Start, PackMLState.Idle, PackMLState.Execute)]
    [Trait("AC", "7")]
    public void Command_runs_through_its_acting_state_to_the_stable_state(PackMLCommand command, PackMLState from, PackMLState expected)
    {
        PackMLMachine machine = MachineIn(from);

        machine.Scan(Command(command), state => state != PackMLState.Execute);

        Assert.True(machine.CommandAccepted);
        Assert.Equal(expected, machine.State);
    }

    [Theory]
    [InlineData(PackMLState.Starting, PackMLState.Execute)]
    [InlineData(PackMLState.Execute, PackMLState.Completing)]
    [InlineData(PackMLState.Completing, PackMLState.Complete)]
    [InlineData(PackMLState.Undefined, PackMLState.Aborted)]
    [InlineData(PackMLState.Aborting, PackMLState.Aborted)]
    [InlineData(PackMLState.Unholding, PackMLState.Execute)]
    [Trait("AC", "7")]
    public void Hook_runs_at_most_one_transition_per_scan(PackMLState from, PackMLState expected)
    {
        PackMLMachine machine = MachineIn(from);
        var asked = new List<PackMLState>();

        machine.Scan(new PackMLInput(0, false, 0, false), state =>
        {
            asked.Add(state);
            return true;
        });

        Assert.Equal(expected, machine.State);
        Assert.Equal([from], asked);
    }

    [Theory]
    [InlineData(PackMLState.Idle)]
    [InlineData(PackMLState.Stopped)]
    [InlineData(PackMLState.Held)]
    [Trait("AC", "7")]
    public void Stable_state_does_not_ask_the_hook(PackMLState state)
    {
        PackMLMachine machine = MachineIn(state);
        bool asked = false;

        machine.Scan(new PackMLInput(0, false, 0, false), _ =>
        {
            asked = true;
            return true;
        });

        Assert.False(asked);
        Assert.Equal(state, machine.State);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(65535)]
    [InlineData(-1)]
    [Trait("AC", "8")]
    public void Mode_outside_zero_to_two_is_not_supported(long mode)
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);

        machine.Scan(Mode(mode), NeverDone);

        Assert.True(machine.ModeChangeRejected);
        Assert.Equal(PackMLErrorId.ModeNotSupported, machine.ModeErrorId);
        Assert.Equal(0, machine.Mode);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Mode_equal_to_current_mode_is_already_active()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);

        machine.Scan(Mode(0), NeverDone);

        Assert.True(machine.ModeChangeRejected);
        Assert.Equal(PackMLErrorId.ModeAlreadyActive, machine.ModeErrorId);
    }

    [Theory]
    [InlineData(PackMLState.Execute)]
    [InlineData(PackMLState.Held)]
    [InlineData(PackMLState.Starting)]
    [Trait("AC", "8")]
    public void Mode_change_in_a_moving_state_is_not_allowed(PackMLState state)
    {
        PackMLMachine machine = MachineIn(state);

        machine.Scan(Mode(1), NeverDone);

        Assert.True(machine.ModeChangeRejected);
        Assert.Equal(PackMLErrorId.ModeChangeNotAllowedFromCurrentState, machine.ModeErrorId);
        Assert.Equal(0, machine.Mode);
    }

    [Theory]
    [InlineData(PackMLState.Aborted)]
    [InlineData(PackMLState.Stopped)]
    [InlineData(PackMLState.Idle)]
    [InlineData(PackMLState.Complete)]
    [Trait("AC", "8")]
    public void Mode_change_in_a_stable_state_is_accepted(PackMLState state)
    {
        PackMLMachine machine = MachineIn(state);

        machine.Scan(Mode(2), NeverDone);

        Assert.True(machine.ModeChanged);
        Assert.False(machine.ModeChangeRejected);
        Assert.Equal(2, machine.Mode);
        Assert.Equal(0, machine.PreviousMode);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Mode_request_low_clears_mode_latches_and_allows_the_next_change()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);
        machine.Scan(Mode(1), NeverDone);

        machine.Scan(Mode(1, request: false), NeverDone);
        machine.Scan(Mode(2), NeverDone);

        Assert.True(machine.ModeChanged);
        Assert.Equal(2, machine.Mode);
        Assert.Equal(1, machine.PreviousMode);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Mode_changed_flag_stays_low_without_a_request_and_drops_when_the_request_goes_low()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);

        machine.Scan(Mode(1, request: false), NeverDone);
        Assert.False(machine.ModeChanged);

        machine.Scan(Mode(1), NeverDone);
        Assert.True(machine.ModeChanged);

        machine.Scan(Mode(1, request: false), NeverDone);
        Assert.False(machine.ModeChanged);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Mode_rejection_drops_when_the_request_goes_low()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);

        machine.Scan(Mode(3), NeverDone);
        Assert.True(machine.ModeChangeRejected);

        machine.Scan(Mode(3, request: false), NeverDone);
        Assert.False(machine.ModeChangeRejected);
        Assert.Equal(PackMLErrorId.NoError, machine.ModeErrorId);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Held_mode_request_is_handled_once()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);

        machine.Scan(Mode(1), NeverDone);
        machine.Scan(Mode(1), NeverDone);

        Assert.True(machine.ModeChanged);
        Assert.False(machine.ModeChangeRejected);
    }

    [Fact]
    [Trait("AC", "8")]
    public void RequestCommand_fails_when_queue_busy_or_mode_request_high()
    {
        PackMLMachine machine = MachineIn(PackMLState.Execute);

        Assert.False(machine.RequestCommand(PackMLCommand.Abort, modeRequestHigh: true));
        Assert.True(machine.RequestCommand(PackMLCommand.Abort, modeRequestHigh: false));
        Assert.False(machine.RequestCommand(PackMLCommand.Stop, modeRequestHigh: false));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Queued_command_wins_over_external_request()
    {
        PackMLMachine machine = MachineIn(PackMLState.Execute);
        machine.RequestCommand(PackMLCommand.Abort, modeRequestHigh: false);

        machine.Scan(Command(PackMLCommand.Hold), NeverDone);

        Assert.Equal(PackMLState.Aborting, machine.State);
        Assert.True(machine.CommandAccepted);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Queue_is_free_again_after_the_scan_took_the_command()
    {
        PackMLMachine machine = MachineIn(PackMLState.Execute);
        machine.RequestCommand(PackMLCommand.Abort, modeRequestHigh: false);
        machine.Scan(new PackMLInput(0, false, 0, false), NeverDone);

        Assert.True(machine.RequestCommand(PackMLCommand.Stop, modeRequestHigh: false));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Seeded_requests_are_consumed_until_they_go_low()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);
        machine.SeedInput(Command(PackMLCommand.Start));

        machine.Scan(Command(PackMLCommand.Start), NeverDone);
        Assert.Equal(PackMLState.Idle, machine.State);
        Assert.False(machine.CommandAccepted);

        machine.Scan(Command(PackMLCommand.Start, request: false), NeverDone);
        machine.Scan(Command(PackMLCommand.Start), NeverDone);
        Assert.Equal(PackMLState.Starting, machine.State);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Seeded_mode_request_is_consumed_until_it_goes_low()
    {
        PackMLMachine machine = MachineIn(PackMLState.Idle);
        machine.SeedInput(Mode(1));

        machine.Scan(Mode(1), NeverDone);

        Assert.False(machine.ModeChanged);
        Assert.Equal(0, machine.Mode);
    }

    [Fact]
    [Trait("AC", "2")]
    public void New_machine_starts_with_given_states_and_clear_flags()
    {
        var machine = new PackMLMachine(PackMLState.Idle, PackMLState.Resetting);

        Assert.Equal(PackMLState.Idle, machine.State);
        Assert.Equal(PackMLState.Resetting, machine.PreviousState);
        Assert.Equal(0, machine.Mode);
        Assert.Equal(0, machine.PreviousMode);
        Assert.False(machine.CommandAccepted);
        Assert.False(machine.CommandRejected);
        Assert.False(machine.ModeChanged);
        Assert.False(machine.ModeChangeRejected);
        Assert.Equal(PackMLErrorId.NoError, machine.StateErrorId);
        Assert.Equal(PackMLErrorId.NoError, machine.ModeErrorId);
    }
}
