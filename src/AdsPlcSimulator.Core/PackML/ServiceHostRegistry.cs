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
    /// <exception cref="InvalidOperationException">A host of another workflow already uses the same root.</exception>
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

    public ServiceHost? FindByService(string ctrlPath)
    {
        lock (_lock)
            return _hostsByRoot.Values.Select(registration => registration.Host).FirstOrDefault(host => host.Contains(ctrlPath));
    }

    public void SetOverrides(string workflow, IReadOnlyCollection<string> ctrlPaths)
    {
        lock (_lock)
        {
            _overrides.RemoveAll(entry => entry.Workflow.Equals(workflow, StringComparison.OrdinalIgnoreCase));
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
