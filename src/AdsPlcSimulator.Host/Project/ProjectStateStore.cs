using System.Text.Json;

namespace AdsPlcSimulator.Host.Project;

public sealed class ProjectStateStore(string? filePath, ILogger<ProjectStateStore> logger)
{
    private const string LastProjectProperty = "lastProject";

    /// <summary>%LOCALAPPDATA%\AdsPlcSimulator\state.json, or null when the folder is unknown (no user profile).</summary>
    public static string? DefaultPath { get; } = ResolveDefaultPath();

    public string? ReadLastProject()
    {
        if (filePath is null || !File.Exists(filePath))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(LastProjectProperty, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
                return value.GetString();

            // Stryker disable once Statement,String: a log line has no test value
            logger.LogWarning("{File} has no string '{Property}'; ignoring it.", filePath, LastProjectProperty);
            return null;
        }
        // Stryker disable once Block: without the catch body the method still returns null after the try
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Stryker disable once Statement,String: a log line has no test value
            logger.LogWarning("Could not read {File}: {Message}", filePath, exception.Message);
            return null;
        }
    }

    public void WriteLastProject(string name)
    {
        if (filePath is null)
        {
            // Stryker disable once Statement,String: a log line has no test value
            logger.LogDebug("No state file location; not remembering project {Project}.", name);
            return;
        }

        string temporary = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Dictionary<string, string> { [LastProjectProperty] = name }));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Stryker disable once Statement,String: a log line has no test value
            logger.LogWarning("Could not write {File}: {Message}", filePath, exception.Message);
            DeleteQuietly(temporary);
        }
    }

    private static string? ResolveDefaultPath()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Stryker disable once Conditional: LocalApplicationData is empty only without a user profile, which a test machine always has
        return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "AdsPlcSimulator", "state.json");
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
