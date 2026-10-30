using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML.Alarm;

internal sealed record HookSubmission(uint MainErrorId, uint SourceErrorId, string Message, AlarmSeverity Severity, string Path);

internal sealed record ConfiguredBinding(ConfiguredAlarm Alarm, SymbolRef Source);

/// <summary>The alarm work of one module. Not thread-safe; ServiceHost calls it under its lock.</summary>
internal sealed class ModuleAlarmEngine(
    string modulePath,
    ModuleAlarmBlock? block,
    IReadOnlyList<ServiceErrorList?> allErrors,
    IReadOnlyList<ServiceErrorList?> boundErrors)
{
    private readonly List<HookSubmission> _hooks = [];
    private List<ConfiguredBinding> _configured = [];
    private ModuleAlarmManager _manager = new(0);

    public void Reset(ProcessImage image, List<ConfiguredBinding> configured)
    {
        _configured = configured;
        _hooks.Clear();
        foreach (ServiceErrorList? errors in allErrors)
            errors?.ResetImage(image);

        if (block is null)
            return;

        _manager = new ModuleAlarmManager(block.ListCapacity);
        block.Clear(image);
    }

    public void SetHook(HookSubmission submission, bool active)
    {
        int index = _hooks.FindIndex(hook => hook.MainErrorId == submission.MainErrorId);
        if (!active)
        {
            if (index >= 0)
                _hooks.RemoveAt(index);
            return;
        }

        if (index >= 0)
            _hooks[index] = submission;
        else if (_hooks.Count >= ModuleAlarmManager.MaxHookAlarmsPerModule)
            throw new InvalidOperationException($"{modulePath} already has {ModuleAlarmManager.MaxHookAlarmsPerModule} hook alarms.");
        else
            _hooks.Add(submission);
    }

    public void Scan(ProcessImage image, string utc)
    {
        if (block is null)
            return;

        uint moduleId = block.ReadModuleId(image);
        _manager.BeginCycle(moduleId);
        SuppressHooksOwnedByConfig();
        Acknowledge(block, image);
        ObserveConfigured(image, moduleId, utc);
        ObserveHooks(utc);
        ObserveServices();
        _manager.EndCycle();
        Publish(block, image);
    }

    private void SuppressHooksOwnedByConfig()
    {
        foreach (ConfiguredBinding binding in _configured)
            _manager.SuppressHookAlarm(binding.Alarm.MainErrorId);
    }

    private void Acknowledge(ModuleAlarmBlock target, ProcessImage image) =>
        _manager.ProcessAcknowledgement(target.ReadAckRequest(image));

    private void ObserveConfigured(ProcessImage image, uint moduleId, string utc)
    {
        for (int index = 0; index < _configured.Count; index++)
        {
            ConfiguredAlarm alarm = _configured[index].Alarm;
            bool valid = TrySample(image, _configured[index], out double value);
            bool met = valid && AlarmCondition.IsMet(value, alarm.Operator, alarm.Threshold, alarm.Tolerance);
            var snapshot = new ExtAlarm(
                moduleId, 0, alarm.MainErrorId, alarm.SourceErrorId, alarm.Message, $"{modulePath}.{alarm.Source}",
                alarm.Severity, "", false, false, 0);
            _manager.UpdateConfiguredAlarm(index + 1, valid, met, snapshot, utc);
        }
    }

    private static bool TrySample(ProcessImage image, ConfiguredBinding binding, out double value)
    {
        value = 0;
        if (binding.Alarm.SourceType == AlarmSourceType.String80)
            return false;

        value = PlcValue.ToDouble(image.ReadValue(binding.Source));
        return true;
    }

    private void ObserveHooks(string utc)
    {
        foreach (HookSubmission hook in _hooks)
        {
            if (_configured.Any(binding => binding.Alarm.MainErrorId == hook.MainErrorId))
                continue;

            string path = hook.Path.Length == 0 ? modulePath : hook.Path;
            _manager.ObserveHookAlarm(new ExtAlarm(0, 0, hook.MainErrorId, hook.SourceErrorId, hook.Message, path, hook.Severity, utc, false, false, 0));
        }
    }

    private void ObserveServices()
    {
        foreach (ServiceErrorList? errors in boundErrors)
        {
            foreach (ExtAlarm entry in errors?.Entries ?? [])
                _manager.ObserveServiceAlarm(entry);
        }
    }

    private void Publish(ModuleAlarmBlock target, ProcessImage image) =>
        target.Write(image, _manager.Publish(), _manager.Status);
}
