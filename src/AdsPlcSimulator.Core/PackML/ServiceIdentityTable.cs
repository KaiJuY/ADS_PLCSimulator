using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AdsPlcSimulator.Core.PackML;

/// <summary>ServiceId and CompletionBehavior per Status path, read from an F_Initialize…ServiceStatus implementation.</summary>
public sealed partial class ServiceIdentityTable
{
    private const string StatusPrefix = "ServiceStatus.";

    private readonly Dictionary<string, ServiceIdentity> _identities = new(StringComparer.OrdinalIgnoreCase);

    private ServiceIdentityTable()
    {
    }

    public int Count => _identities.Count;

    /// <param name="implementation">ST statements, or a TwinCAT .TcPOU XML whose &lt;ST&gt; is used.</param>
    /// <param name="enumValue">Resolves "Enum.Member" to its value; null when unknown.</param>
    public static ServiceIdentityTable Parse(string implementation, Func<string, long?> enumValue)
    {
        var table = new ServiceIdentityTable();
        string code = StripComments(ExtractStatements(implementation));
        foreach (string statement in code.Split(';'))
            table.ApplyStatement(statement.Trim(), enumValue);
        return table;
    }

    /// <param name="relativePath">Path below ServiceStatus, e.g. "Shutter.Open"; case-insensitive.</param>
    public ServiceIdentity? Find(string relativePath) => _identities.GetValueOrDefault(relativePath);

    [GeneratedRegex(@"//[^\r\n]*|\(\*.*?\*\)", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"^F_\w+\s*\((?<arguments>.*)\)$", RegexOptions.Singleline)]
    private static partial Regex IdentityCallPattern();

    [GeneratedRegex(@"^(?<path>ServiceStatus\.[^\s:]+?)\.CompletionBehavior\s*:=\s*(?<value>\S+)$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BehaviorAssignmentPattern();

    private static string ExtractStatements(string implementation)
    {
        string text = implementation.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (!text.StartsWith('<'))
            return text;

        try
        {
            XElement? statements = XDocument.Parse(text).Descendants("ST").FirstOrDefault();
            return statements?.Value ?? throw new ServiceIdentityException("The XML has no <ST> element.");
        }
        catch (XmlException exception)
        {
            throw new ServiceIdentityException($"The identity file is not valid XML: {exception.Message}");
        }
    }

    private static string StripComments(string code) => CommentPattern().Replace(code, " ");

    private void ApplyStatement(string statement, Func<string, long?> enumValue)
    {
        Match call = IdentityCallPattern().Match(statement);
        if (call.Success)
        {
            ApplyIdentityCall(call.Groups["arguments"].Value, enumValue);
            return;
        }

        Match assignment = BehaviorAssignmentPattern().Match(statement);
        if (assignment.Success)
            ApplyBehavior(assignment.Groups["path"].Value, assignment.Groups["value"].Value, enumValue);
    }

    private void ApplyIdentityCall(string arguments, Func<string, long?> enumValue)
    {
        Dictionary<string, string> named = SplitNamedArguments(arguments);
        if (!named.TryGetValue("Status", out string? status)
            || !named.TryGetValue("ServiceId", out string? serviceId)
            || !named.TryGetValue("CompletionBehavior", out string? behavior)
            || !status.StartsWith(StatusPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _identities[status[StatusPrefix.Length..]] = new ServiceIdentity(
            (uint)ResolveEnum(serviceId, enumValue),
            (CompletionBehavior)ResolveEnum(behavior, enumValue));
    }

    private void ApplyBehavior(string path, string value, Func<string, long?> enumValue)
    {
        string relative = path[StatusPrefix.Length..];
        var behavior = (CompletionBehavior)ResolveEnum(value, enumValue);
        uint serviceId = _identities.TryGetValue(relative, out ServiceIdentity? previous) ? previous.ServiceId : 0;
        _identities[relative] = new ServiceIdentity(serviceId, behavior);
    }

    private static long ResolveEnum(string name, Func<string, long?> enumValue) =>
        enumValue(name) ?? throw new ServiceIdentityException($"Unknown enum value '{name}'.");

    private static Dictionary<string, string> SplitNamedArguments(string arguments)
    {
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int depth = 0;
        int start = 0;
        for (int position = 0; position <= arguments.Length; position++)
        {
            if (position < arguments.Length)
            {
                char current = arguments[position];
                depth += current == '[' ? 1 : current == ']' ? -1 : 0;
                if (current != ',' || depth > 0)
                    continue;
            }

            AddNamedArgument(named, arguments[start..position]);
            start = position + 1;
        }

        return named;
    }

    private static void AddNamedArgument(Dictionary<string, string> named, string argument)
    {
        int separator = argument.IndexOf(":=", StringComparison.Ordinal);
        if (separator < 0)
            return;

        named[argument[..separator].Trim()] = argument[(separator + 2)..].Trim();
    }
}
