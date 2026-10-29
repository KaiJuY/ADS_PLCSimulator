using System.Globalization;
using System.Text.Json.Serialization;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PortKind
{
    /// <summary>Execution order. An exec output runs every linked node in link order.</summary>
    Exec,

    /// <summary>A value, pulled from the linked output when the node runs.</summary>
    Data,
}

/// <param name="Optional">An optional data input evaluates to null when it has no link and no parameter value.</param>
public sealed record PortDefinition(string Name, PortKind Kind, string Label, bool Optional = false);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParamKind
{
    Symbol,
    Number,
    Text,
    Choice,
}

/// <summary>
/// A node parameter. A data input with the same name uses the parameter as its value when the input has no link.
/// </summary>
public sealed record ParamDefinition(
    string Name,
    ParamKind Kind,
    string Label,
    string DefaultValue = "",
    IReadOnlyList<string>? Choices = null);

/// <summary>Everything the editor and the compiler need to know about one node type.</summary>
public sealed record NodeDefinition(
    string Type,
    string Category,
    string Title,
    string Description,
    IReadOnlyList<PortDefinition> Inputs,
    IReadOnlyList<PortDefinition> Outputs,
    IReadOnlyList<ParamDefinition> Params,
    [property: JsonIgnore] Func<NodeSettings, RuntimeNode> Create)
{
    public bool IsTrigger => Inputs.All(port => port.Kind != PortKind.Exec) && Outputs.Any(port => port.Kind == PortKind.Exec);

    public PortDefinition? FindInput(string name) => Inputs.FirstOrDefault(port => port.Name == name);

    public PortDefinition? FindOutput(string name) => Outputs.FirstOrDefault(port => port.Name == name);

    public ParamDefinition? FindParam(string name) => Params.FirstOrDefault(param => param.Name == name);
}

public sealed class NodeConfigurationException(string message) : Exception(message);

/// <summary>Typed, validated access to a node's parameter values while the node is being created.</summary>
public sealed class NodeSettings(NodeDefinition definition, IReadOnlyDictionary<string, string> values, SymbolTable symbols)
{
    public string Text(string name)
    {
        ParamDefinition param = definition.FindParam(name)
            ?? throw new InvalidOperationException($"{definition.Type} has no parameter '{name}'.");
        return values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : param.DefaultValue;
    }

    public double Number(string name, double minimum = double.MinValue)
    {
        string text = Text(name);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new NodeConfigurationException($"Parameter '{name}' must be a number, not '{text}'.");
        if (value < minimum)
            throw new NodeConfigurationException($"Parameter '{name}' must be at least {minimum}.");
        return value;
    }

    public string Choice(string name)
    {
        string value = Text(name);
        IReadOnlyList<string> choices = definition.FindParam(name)!.Choices ?? [];
        return choices.Contains(value)
            ? value
            : throw new NodeConfigurationException($"Parameter '{name}' must be one of {string.Join(", ", choices)}, not '{value}'.");
    }

    public SymbolRef Symbol(string name)
    {
        string path = Text(name);
        if (string.IsNullOrEmpty(path))
            throw new NodeConfigurationException($"Parameter '{name}' needs a PLC symbol path.");
        if (!symbols.TryResolve(path, out SymbolRef symbol, out string? error))
            throw new NodeConfigurationException(error!);
        return symbol;
    }

    public SymbolRef ScalarSymbol(string name)
    {
        SymbolRef symbol = Symbol(name);
        return symbol.Type.IsScalar
            ? symbol
            : throw new NodeConfigurationException($"'{symbol.Path}' is {symbol.Type.Name}; pick one of its scalar members.");
    }
}
