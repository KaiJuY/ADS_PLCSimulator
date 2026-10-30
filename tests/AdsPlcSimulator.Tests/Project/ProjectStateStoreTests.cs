using AdsPlcSimulator.Host.Project;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdsPlcSimulator.Tests.Project;

public sealed class ProjectStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adsplc-state-" + Guid.NewGuid().ToString("N"));

    public ProjectStateStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string StateFile => Path.Combine(_directory, "sub", "state.json");

    private static ProjectStateStore Store(string? path) => new(path, NullLogger<ProjectStateStore>.Instance);

    [Fact]
    [Trait("AC", "14")]
    public void Without_a_file_path_read_returns_null_and_write_does_nothing()
    {
        ProjectStateStore store = Store(null);

        store.WriteLastProject("Alpha");

        Assert.Null(store.ReadLastProject());
    }

    [Fact]
    [Trait("AC", "14")]
    public void Read_returns_null_when_the_file_does_not_exist()
    {
        Assert.Null(Store(StateFile).ReadLastProject());
    }

    [Fact]
    [Trait("AC", "13")]
    [Trait("AC", "14")]
    public void Written_project_is_read_back()
    {
        ProjectStateStore store = Store(StateFile);

        store.WriteLastProject("Polar2Chamber");

        Assert.Equal("Polar2Chamber", store.ReadLastProject());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StateFile)!, "*.tmp"));
    }

    [Fact]
    [Trait("AC", "13")]
    public void A_second_write_replaces_the_first()
    {
        ProjectStateStore store = Store(StateFile);

        store.WriteLastProject("Alpha");
        store.WriteLastProject("Beta");

        Assert.Equal("Beta", store.ReadLastProject());
    }

    [Theory]
    [Trait("AC", "14")]
    [InlineData("{ not json")]
    [InlineData("{\"lastProject\": 5}")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Read_returns_null_for_a_file_without_a_string_last_project(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, content);

        Assert.Null(Store(StateFile).ReadLastProject());
    }

    [Fact]
    [Trait("AC", "13")]
    public void Write_does_not_throw_when_the_parent_path_is_a_file()
    {
        string blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");
        ProjectStateStore store = Store(Path.Combine(blocker, "state.json"));

        store.WriteLastProject("Alpha");

        Assert.Null(store.ReadLastProject());
    }

    [Fact]
    [Trait("AC", "13")]
    public void Failed_write_leaves_no_temporary_file()
    {
        string path = Path.Combine(_directory, "state.json");
        Directory.CreateDirectory(path);

        Store(path).WriteLastProject("Alpha");

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    [Trait("AC", "13")]
    public void DefaultPath_is_the_state_file_under_the_local_application_data_folder()
    {
        string? path = ProjectStateStore.DefaultPath;

        Assert.NotNull(path);
        Assert.EndsWith(Path.Combine("AdsPlcSimulator", "state.json"), path);
    }
}
