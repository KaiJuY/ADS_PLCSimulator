using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Core.Plc.Parsing;

namespace AdsPlcSimulator.Tests;

internal static class TestPaths
{
    public static string SolutionRoot { get; } = FindSolutionRoot();

    public static string SampleProject => Path.Combine(SolutionRoot, "projects", "RobustSolutionDemo");

    public static string Polar2Project => Path.Combine(SolutionRoot, "projects", "Polar2Chamber");

    public static PlcProgram BuildFromText(string text, string globalScope = "GVL_Test") =>
        PlcProgramBuilder.Build(DeclarationParser.Parse(text, "test.st", globalScope));

    private static string FindSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AdsPlcSimulator.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("AdsPlcSimulator.sln not found above the test output folder.");
    }
}
