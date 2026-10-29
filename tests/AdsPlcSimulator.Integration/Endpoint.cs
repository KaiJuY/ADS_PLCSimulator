namespace AdsPlcSimulator.Integration;

/// <summary>A module or a service: the control and status symbol paths of one PackML handshake.</summary>
internal sealed record Endpoint(string Name, string Ctrl, string Status)
{
    public static Endpoint Service(int module, string path) => new(
        $"M{module}.{path}",
        $"GVL_Module.Chamber_Control[{module}].ServiceCtrl.{path}",
        $"GVL_Module.Chamber_Control[{module}].ServiceStatus.{path}");

    public static Endpoint Module(int module) => new(
        $"M{module}.Module",
        $"GVL_Module.Chamber_Control[{module}].ModuleCtrl",
        $"GVL_Module.Chamber_Control[{module}].ModuleStatus");
}
