namespace AdsPlcSimulator.Host.Project;

public sealed class SimulatorOptions
{
    /// <summary>Folder whose subfolders with a plc/ folder are the projects the editor can run.</summary>
    public string ProjectsRoot { get; set; } = "../../projects";

    /// <summary>When set, the Host runs this project folder at startup. Not remembered.</summary>
    public string? ProjectPath { get; set; }
}
