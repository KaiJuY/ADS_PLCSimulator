using AdsPlcSimulator.Host.Ads;
using AdsPlcSimulator.Host.Live;
using Microsoft.Extensions.Options;

namespace AdsPlcSimulator.Host.Project;

public enum ProjectState { Stopped, Running }

public enum RunOutcome { Started, AlreadyRunning, UnknownProject, Failed }

public sealed record RunResult(RunOutcome Outcome, string? Error);

public sealed record ProjectStatus(
    ProjectState State, IReadOnlyList<string> Projects, string? Running, string? RunningPath, string? Last, ushort Port);

/// <summary>Owns the running project and its ADS server. Run and Stop are serialized.</summary>
public sealed class ProjectHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IOptions<AdsOptions> _adsOptions;
    private readonly LiveHub _liveHub;
    private readonly ProjectStateStore _stateStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ILogger<SimulatorSession> _sessionLogger;

    private SimulatorSession? _current;
    private PlcAdsServer? _server;
    private Task? _pendingRelease;
    private string? _lastProject;
    private int _runCount;
    private bool _disposed;

    public ProjectHost(
        IOptions<SimulatorOptions> options,
        IOptions<AdsOptions> adsOptions,
        IHostEnvironment environment,
        LiveHub liveHub,
        ProjectStateStore stateStore,
        ILoggerFactory loggerFactory)
    {
        _adsOptions = adsOptions;
        _liveHub = liveHub;
        _stateStore = stateStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ProjectHost>();
        _sessionLogger = loggerFactory.CreateLogger<SimulatorSession>();
        ProjectsRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.ProjectsRoot));
        _lastProject = stateStore.ReadLastProject();
    }

    internal TimeSpan RegisterTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal Func<PlcAdsServer, CancellationToken, Task> Connect { get; init; } = (server, token) => server.ConnectServerAsync(token);

    internal Func<SimulatorSession, ValueTask> DisposeSession { get; init; } = session => session.DisposeAsync();

    public string ProjectsRoot { get; }

    public SimulatorSession? Current => Volatile.Read(ref _current);

    private ushort Port => _adsOptions.Value.Port;

    internal static byte SymbolVersionForRun(int run) => (byte)(((run - 1) % 255) + 1);

    public ProjectStatus GetStatus()
    {
        SimulatorSession? session = Current;
        return new ProjectStatus(
            session is null ? ProjectState.Stopped : ProjectState.Running,
            ListProjects(),
            session?.Name,
            session?.ProjectDirectory,
            Volatile.Read(ref _lastProject),
            Port);
    }

    public IReadOnlyList<string> ListProjects()
    {
        try
        {
            if (!Directory.Exists(ProjectsRoot))
                return [];

            return Directory.EnumerateDirectories(ProjectsRoot)
                .Where(directory => Directory.Exists(Path.Combine(directory, "plc")))
                .Select(directory => Path.GetFileName(directory))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not list projects in {Root}: {Message}", ProjectsRoot, exception.Message);
            return [];
        }
    }

    public Task<RunResult> RunAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !ListProjects().Contains(name))
            return Task.FromResult(new RunResult(RunOutcome.UnknownProject, null));
        return RunCoreAsync(Path.Combine(ProjectsRoot, name), name, remember: true);
    }

    public Task<RunResult> RunPathAsync(string projectDirectory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        return RunCoreAsync(full, Path.GetFileName(full), remember: false);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static RunResult Failed(string error) => new(RunOutcome.Failed, error);

    private async Task<RunResult> RunCoreAsync(string directory, string name, bool remember)
    {
        await _gate.WaitAsync();
        try
        {
            RunResult? refusal = CheckCanRun();
            if (refusal is not null)
                return refusal;

            _liveHub.ClearSimulationState();
            byte version = SymbolVersionForRun(_runCount + 1);

            SimulatorSession session;
            try
            {
                session = new SimulatorSession(directory, _liveHub, _sessionLogger);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogError("Run {Project} failed: {Message}", name, exception.Message);
                return Failed(exception.Message);
            }

            var server = new PlcAdsServer(Port, session.Program.Symbols, session.Image, _loggerFactory, version);
            string? registerError = await RegisterAsync(name, session, server);
            if (registerError is not null)
                return Failed(registerError);

            _runCount++;
            _server = server;
            Volatile.Write(ref _current, session);
            if (remember)
            {
                _stateStore.WriteLastProject(name);
                Volatile.Write(ref _lastProject, name);
            }

            _logger.LogInformation("Project {Project} running on AMS port {Port}, symbol version {Version}.", name, Port, version);
            return new RunResult(RunOutcome.Started, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RunResult? CheckCanRun()
    {
        if (_disposed)
            return Failed("The Host is shutting down.");
        if (_current is not null)
            return new RunResult(RunOutcome.AlreadyRunning, $"Project '{_current.Name}' is running. Stop it first.");
        if (_pendingRelease is null)
            return null;
        if (_pendingRelease.IsCompleted)
        {
            _pendingRelease = null;
            return null;
        }

        string message = $"AMS port {Port} is still held by the previous project. Restart the Host.";
        _logger.LogError("{Message}", message);
        return Failed(message);
    }

    private async Task<string?> RegisterAsync(string name, SimulatorSession session, PlcAdsServer server)
    {
        using var cts = new CancellationTokenSource(RegisterTimeout);
        Task? connect = null;
        try
        {
            connect = Connect(server, cts.Token);
            await connect.WaitAsync(RegisterTimeout);
            return null;
        }
        catch (Exception exception)
        {
            await ReleaseFailedRegistrationAsync(session, server, connect);
            string message = $"AMS port {Port} could not be registered ({exception.Message}). Is another runtime using this port?";
            _logger.LogError("Run {Project} failed: {Message}", name, message);
            return message;
        }
    }

    private async Task ReleaseFailedRegistrationAsync(SimulatorSession session, PlcAdsServer server, Task? connect)
    {
        if (connect is { IsCompleted: false })
            await WaitQuietlyAsync(connect, RegisterTimeout);

        if (connect is null || connect.IsCompleted)
            server.Dispose();
        else
            _pendingRelease = connect.ContinueWith(_ => server.Dispose(), TaskScheduler.Default);

        await DisposeSessionBoundedAsync(session, "Workflows of {Project} did not stop within 5 s; releasing anyway.");
        _liveHub.ClearSimulationState();
    }

    private async Task StopCoreAsync()
    {
        if (_current is not { } session)
            return;

        PlcAdsServer server = _server!;
        Volatile.Write(ref _current, null);

        bool released = await ReleasePortAsync(server, session.Name);
        _server = null;

        await DisposeSessionBoundedAsync(session, "Workflows of {Project} did not stop within 5 s; releasing anyway.");
        _liveHub.ClearSimulationState();

        if (released)
            _logger.LogInformation("Project {Project} stopped; AMS port {Port} released.", session.Name, Port);
        else
            _logger.LogWarning("Project {Project} stopped; AMS port {Port} may still be registered.", session.Name, Port);
    }

    private async Task<bool> ReleasePortAsync(PlcAdsServer server, string name)
    {
        Task release = Task.Run(() =>
        {
            server.Disconnect();
            server.Dispose();
        });

        try
        {
            await release.WaitAsync(StopTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            _pendingRelease = release;
            _logger.LogError("Stop {Project}: AMS port {Port} was not released within 5 s.", name, Port);
        }
        catch (Exception exception)
        {
            _logger.LogError("Stop {Project}: AMS port {Port} release failed ({Message}).", name, Port, exception.Message);
        }

        return false;
    }

    private async Task DisposeSessionBoundedAsync(SimulatorSession session, string timeoutMessage)
    {
        try
        {
            await DisposeSession(session).AsTask().WaitAsync(StopTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(timeoutMessage, session.Name);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Disposing {Project} failed: {Message}", session.Name, exception.Message);
        }
    }

    private static async Task WaitQuietlyAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (Exception)
        {
        }
    }
}
