using System.Collections.Concurrent;
using AdsPlcSimulator.Host.Ads;
using AdsPlcSimulator.Host.Live;
using AdsPlcSimulator.Host.Project;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TwinCAT;
using TwinCAT.Ads;

namespace AdsPlcSimulator.Tests.Project;

// Needs the AMS router of a local TwinCAT installation.
[CollectionDefinition("AdsPort25855", DisableParallelization = true)]
public sealed class AdsPort25855Collection;

internal sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";

    public string ApplicationName { get; set; } = "Test";

    public string ContentRootPath { get; set; } = contentRoot;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, formatter(state, exception)));
    }
}

internal sealed class ProjectTestRoot : IDisposable
{
    public const ushort Port = 25855;

    private readonly CapturingLoggerProvider _logs = new();

    public ProjectTestRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "adsplc-" + Guid.NewGuid().ToString("N"));
        WriteFile("Alpha/plc/a.st", "TYPE ST_P : STRUCT n : INT; END_STRUCT END_TYPE VAR_GLOBAL nAlpha : INT := 7; stP : ST_P; END_VAR");
        WriteFile("Alpha/simulation/index.html", "<html></html>");
        WriteFile("Beta/plc/b.st", "VAR_GLOBAL nBeta : DINT; END_VAR");
        WriteFile("Broken/plc/x.st", "TYPE ST_X : STRUCT n : INT; END_STRUCT END_TYPE");
        Directory.CreateDirectory(Path.Combine(Root, "NoPlc"));
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddProvider(_logs));
        Hub = new LiveHub(NullLogger<LiveHub>.Instance);
    }

    public string Root { get; }

    public string StatePath => Path.Combine(Root, "state-dir", "state.json");

    public LiveHub Hub { get; }

    public ILoggerFactory LoggerFactory { get; }

    public IReadOnlyList<(LogLevel Level, string Message)> Logs => _logs.Entries;

    public string ProjectPath(string name) => Path.Combine(Root, name);

    public ProjectHost CreateHost(
        TimeSpan? registerTimeout = null,
        TimeSpan? stopTimeout = null,
        Func<PlcAdsServer, CancellationToken, Task>? connect = null,
        Func<SimulatorSession, ValueTask>? disposeSession = null,
        string? projectsRoot = null) =>
        new(
            Options.Create(new SimulatorOptions { ProjectsRoot = projectsRoot ?? Root }),
            Options.Create(new AdsOptions { Port = Port }),
            new TestHostEnvironment(Root),
            Hub,
            new ProjectStateStore(StatePath, NullLogger<ProjectStateStore>.Instance),
            LoggerFactory)
        {
            RegisterTimeout = registerTimeout ?? TimeSpan.FromSeconds(5),
            StopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5),
            Connect = connect ?? ((server, token) => server.ConnectServerAsync(token)),
            DisposeSession = disposeSession ?? (session => session.DisposeAsync()),
        };

    public static AdsErrorCode ReadState()
    {
        using var client = new AdsClient { Timeout = 1000 };
        client.Connect(AmsNetId.Local, Port);
        return client.TryReadState(out _);
    }

    public static byte ReadSymbolVersion()
    {
        using var client = new AdsClient { Timeout = 1000 };
        client.Connect(AmsNetId.Local, Port);
        var version = new byte[1];
        AdsErrorCode error = client.TryRead(0xF008, 0, version.AsMemory(), out _);
        Assert.Equal(AdsErrorCode.NoError, error);
        return version[0];
    }

    public static (AdsErrorCode Error, object? Value) ReadSymbol(string path, Type type)
    {
        using var client = new AdsClient { Timeout = 1000 };
        client.Connect(AmsNetId.Local, Port);
        AdsErrorCode error = client.TryCreateVariableHandle(path, out uint handle);
        if (error != AdsErrorCode.NoError)
            return (error, null);
        return (AdsErrorCode.NoError, client.ReadAny(handle, type));
    }

    public static PlcAdsServer CreateOccupant()
    {
        var program = TestPaths.BuildFromText("VAR_GLOBAL nOther : INT; END_VAR");
        return new PlcAdsServer(Port, program.Symbols, program.CreateProcessImage(), NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
