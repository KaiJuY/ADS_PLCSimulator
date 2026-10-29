using AdsPlcSimulator.Integration;
using TwinCAT.Ads;

const int DefaultPort = 851;
const string Usage = "Usage: dotnet run --project tests/AdsPlcSimulator.Integration -- [--port 851] [--only S10,S11]";

if (!TryParseArguments(args, out int port, out HashSet<string>? only, out string? error))
{
    Console.Error.WriteLine($"{error}{Environment.NewLine}{Usage}");
    return 1;
}

List<Scenario> selected = Scenarios.All.Where(scenario => only is null || only.Contains(scenario.Id)).ToList();
string[] unknown = (only ?? []).Where(id => Scenarios.All.All(scenario => !scenario.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray();
if (unknown.Length > 0)
{
    Console.Error.WriteLine($"Unknown scenario: {string.Join(", ", unknown)}. Known: {string.Join(", ", Scenarios.All.Select(scenario => scenario.Id))}");
    return 1;
}

using var client = new AdsClient { Timeout = 3000 };
try
{
    client.Connect(AmsNetId.Local, port);
    client.ReadState();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Cannot reach the Host on AMS port {port}: {exception.Message}");
    return 1;
}

var session = new PlcSession(client);
int failed = 0;
foreach (Scenario scenario in selected)
{
    if (!await RunAsync(scenario, session))
        failed++;
}

Console.WriteLine($"{selected.Count - failed}/{selected.Count} scenarios passed");
return failed == 0 ? 0 : 1;

static async Task<bool> RunAsync(Scenario scenario, PlcSession session)
{
    try
    {
        Checks checks = await scenario.Run(session);
        string verdict = checks.Passed ? "PASS" : "FAIL";
        Console.WriteLine($"{verdict} {scenario.Id} {scenario.Title} | expected: {checks.Expected} | actual: {checks.Actual}");
        return checks.Passed;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {scenario.Id} {scenario.Title} | error: {exception.GetType().Name}: {exception.Message}");
        return false;
    }
}

static bool TryParseArguments(string[] args, out int port, out HashSet<string>? only, out string? error)
{
    port = DefaultPort;
    only = null;
    error = null;
    for (int index = 0; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length)
        {
            error = $"Missing value for {args[index]}";
            return false;
        }

        string value = args[index + 1];
        switch (args[index])
        {
            case "--port" when int.TryParse(value, out int parsed) && parsed is > 0 and <= ushort.MaxValue:
                port = parsed;
                break;
            case "--only":
                only = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(id => id.ToUpperInvariant()).ToHashSet();
                break;
            default:
                error = $"Invalid argument: {args[index]} {value}";
                return false;
        }
    }
    return true;
}
