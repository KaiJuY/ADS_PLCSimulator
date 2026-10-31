using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.PackML.Behavior;

namespace AdsPlcSimulator.Core.PackML;

/// <summary>Shared by every workflow of one runtime: running hosts and the services that a workflow overrides.</summary>
public sealed class ServiceHostRegistry
{
    private sealed record Registration(string Workflow, ServiceHost Host);

    private sealed record WorkflowOverrides(string Workflow, Dictionary<string, object> Tokens);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Registration> _hostsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WorkflowOverrides> _overrides = [];

    /// <summary>A host of the same workflow on the same root replaces the old one, which is retired first.</summary>
    /// <exception cref="InvalidOperationException">
    /// A host of another workflow already uses the same root, or the host being replaced was replaced by someone else
    /// while it retired. The registered host is then left as it is.
    /// </exception>
    public void Register(string workflow, ServiceHost host)
    {
        ServiceHost? replaced;
        lock (_lock)
        {
            replaced = FindOwnHost(workflow, host.RootPath);
            if (replaced is null)
            {
                _hostsByRoot[host.RootPath] = new Registration(workflow, host);
                return;
            }
        }

        replaced.Retire();
        lock (_lock)
        {
            ThrowIfOwnedByOtherWorkflow(workflow, host.RootPath);
            if (_hostsByRoot.TryGetValue(host.RootPath, out Registration? current) && !ReferenceEquals(current.Host, replaced))
                throw new InvalidOperationException($"Another host of workflow '{workflow}' replaced the host of {host.RootPath} while it was retired.");

            _hostsByRoot[host.RootPath] = new Registration(workflow, host);
        }
    }

    /// <summary>Removes the host only if it is still the registered instance.</summary>
    public void Unregister(ServiceHost host)
    {
        lock (_lock)
        {
            if (_hostsByRoot.TryGetValue(host.RootPath, out Registration? registration) && ReferenceEquals(registration.Host, host))
                _hostsByRoot.Remove(host.RootPath);
        }
    }

    /// <summary>Set by SimulatorSession after new FlowRuntime, before workflows load; read only afterwards.</summary>
    public ConfiguredAlarmTable ConfiguredAlarms { get; set; } = ConfiguredAlarmTable.Empty;

    /// <summary>Set by SimulatorSession after new FlowRuntime, before workflows load; read only afterwards.</summary>
    public ServiceBehaviorTable ServiceBehaviors { get; set; } = ServiceBehaviorTable.Empty;

    public ServiceHost? FindByService(string ctrlPath)
    {
        lock (_lock)
            return _hostsByRoot.Values.Select(registration => registration.Host).FirstOrDefault(host => host.Contains(ctrlPath));
    }

    /// <summary>False when no host binds the service.</summary>
    public bool IsExecuteRejected(string ctrlPath) => FindByService(ctrlPath)?.IsExecuteRejected(ctrlPath) == true;

    /// <summary>Null when no host binds the service.</summary>
    public long? ExecuteGeneration(string ctrlPath) => FindByService(ctrlPath)?.ExecuteGeneration(ctrlPath);

    public ServiceHost? FindByModule(string modulePath)
    {
        lock (_lock)
            return _hostsByRoot.Values.Select(registration => registration.Host).FirstOrDefault(host => host.ContainsModule(modulePath));
    }

    public void SetOverrides(string workflow, IReadOnlyCollection<string> ctrlPaths)
    {
        lock (_lock)
        {
            _overrides.RemoveAll(entry => entry.Workflow.Equals(workflow, StringComparison.OrdinalIgnoreCase));
            // Stryker disable once Statement: an entry without tokens is never returned by OverrideToken, so adding it changes nothing
            if (ctrlPaths.Count == 0)
                return;

            var tokens = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in ctrlPaths)
                tokens[path] = new object();
            _overrides.Add(new WorkflowOverrides(workflow, tokens));
        }
    }

    /// <summary>An object that identifies the current override registration of a service, or null.</summary>
    public object? OverrideToken(string ctrlPath)
    {
        lock (_lock)
        {
            for (int index = _overrides.Count - 1; index >= 0; index--)
            {
                if (_overrides[index].Tokens.TryGetValue(ctrlPath, out object? token))
                    return token;
            }

            return null;
        }
    }

    private ServiceHost? FindOwnHost(string workflow, string root)
    {
        ThrowIfOwnedByOtherWorkflow(workflow, root);
        return _hostsByRoot.TryGetValue(root, out Registration? existing) ? existing.Host : null;
    }

    private void ThrowIfOwnedByOtherWorkflow(string workflow, string root)
    {
        if (_hostsByRoot.TryGetValue(root, out Registration? existing) && !existing.Workflow.Equals(workflow, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Workflow '{existing.Workflow}' already hosts {root}.");
    }
}
