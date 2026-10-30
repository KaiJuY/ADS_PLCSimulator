using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Host.Live;
using Microsoft.Extensions.FileProviders;

namespace AdsPlcSimulator.Host.Project;

public sealed record WorkflowStatus(string Name, bool Loaded, IReadOnlyList<WorkflowDiagnostic> Diagnostics);

/// <summary>
/// A loaded simulator project: the PLC memory built from plc/*.st|Tc*, and the workflows from workflows/*.flow.json.
/// Saving a workflow writes the file and hot-reloads it.
/// </summary>
public sealed partial class SimulatorSession : IAsyncDisposable
{
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private const string WorkflowExtension = ".flow.json";

    private readonly ConcurrentDictionary<string, WorkflowStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SimulatorSession> _logger;

    public SimulatorSession(string projectDirectory, LiveHub liveHub, ILogger<SimulatorSession> logger)
    {
        _logger = logger;
        ProjectDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        Name = Path.GetFileName(ProjectDirectory);
        Program = PlcProgram.LoadDirectory(Path.Combine(ProjectDirectory, "plc"));
        Image = Program.CreateProcessImage();
        Live = new SessionLiveChannel(liveHub);
        Runtime = new FlowRuntime(Image, Program.Symbols, NodeCatalog.Default, Live, Live, ProjectDirectory);

        try
        {
            string simulationDirectory = Path.Combine(ProjectDirectory, "simulation");
            if (Directory.Exists(simulationDirectory))
                SimulationFiles = new PhysicalFileProvider(simulationDirectory);

            _logger.LogInformation("Loaded {Count} PLC root symbols ({Bytes} bytes) from {Directory}.", Program.Symbols.Roots.Count, Image.Size, ProjectDirectory);
            Directory.CreateDirectory(WorkflowDirectory);
            foreach (string file in Directory.EnumerateFiles(WorkflowDirectory, "*" + WorkflowExtension))
                LoadWorkflowFile(Path.GetFileName(file)[..^WorkflowExtension.Length]);
        }
        catch
        {
            CleanUpAfterFailedConstruction();
            throw;
        }
    }

    public string Name { get; }

    public string ProjectDirectory { get; }

    public PlcProgram Program { get; }

    public ProcessImage Image { get; }

    public FlowRuntime Runtime { get; }

    public IFileProvider SimulationFiles { get; private set; } = new NullFileProvider();

    internal SessionLiveChannel Live { get; }

    private string WorkflowDirectory => Path.Combine(ProjectDirectory, "workflows");

    public IReadOnlyList<WorkflowStatus> Workflows => _statuses.Values.OrderBy(status => status.Name).ToList();

    public static bool IsValidWorkflowName(string name) => WorkflowName().IsMatch(name);

    public string? ReadWorkflow(string name)
    {
        string path = WorkflowPath(name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public WorkflowStatus SaveWorkflow(string name, WorkflowDocument document)
    {
        File.WriteAllText(WorkflowPath(name), document.ToJson());
        return Apply(name, document);
    }

    public async ValueTask DisposeAsync()
    {
        Live.Close();
        await Runtime.DisposeAsync();
        (SimulationFiles as IDisposable)?.Dispose();
    }

    private void CleanUpAfterFailedConstruction()
    {
        Live.Close();
        try
        {
            if (!Runtime.DisposeAsync().AsTask().Wait(CleanupTimeout))
                _logger.LogWarning("Cleanup of {Directory} did not finish within 5 s.", ProjectDirectory);
        }
        catch (AggregateException)
        {
            _logger.LogWarning("Cleanup of {Directory} did not finish within 5 s.", ProjectDirectory);
        }

        (SimulationFiles as IDisposable)?.Dispose();
    }

    private void LoadWorkflowFile(string name)
    {
        try
        {
            Apply(name, WorkflowDocument.Parse(File.ReadAllText(WorkflowPath(name))));
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
        {
            _statuses[name] = new WorkflowStatus(name, false, [new WorkflowDiagnostic(null, exception.Message)]);
            _logger.LogWarning("Workflow {Name} could not be read: {Error}", name, exception.Message);
        }
    }

    private WorkflowStatus Apply(string name, WorkflowDocument document)
    {
        IReadOnlyList<WorkflowDiagnostic> diagnostics = Runtime.Load(name, document);
        bool loaded = diagnostics.Count == 0;
        if (!loaded)
        {
            // A broken file must not keep running its previous version behind the editor's back.
            Runtime.Unload(name);
            _logger.LogWarning("Workflow {Name} has {Count} problem(s): {First}", name, diagnostics.Count, diagnostics[0].Message);
        }

        var status = new WorkflowStatus(name, loaded, diagnostics);
        _statuses[name] = status;
        return status;
    }

    private string WorkflowPath(string name)
    {
        if (!IsValidWorkflowName(name))
            throw new ArgumentException($"Workflow name '{name}' may only contain letters, digits, '_' and '-'.");
        return Path.Combine(WorkflowDirectory, name + WorkflowExtension);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex WorkflowName();
}
