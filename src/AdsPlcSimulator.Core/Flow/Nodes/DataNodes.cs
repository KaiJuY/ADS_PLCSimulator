using System.Globalization;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow.Nodes;

public sealed class ReadSymbolNode(SymbolRef symbol) : PureNode
{
    public static readonly NodeDefinition Definition = new(
        "data.readSymbol",
        "Data",
        "Read Symbol",
        "Current value of a PLC variable.",
        [],
        [new("value", PortKind.Data, "value")],
        [new("symbol", ParamKind.Symbol, "Symbol")],
        settings => new ReadSymbolNode(settings.ScalarSymbol("symbol")));

    public override void Evaluate(NodeExecution execution) => execution.Output("value", execution.Image.ReadValue(symbol));
}

public sealed class ConstantNode(object value) : PureNode
{
    public static readonly NodeDefinition Definition = new(
        "data.constant",
        "Data",
        "Constant",
        "A fixed value: TRUE/FALSE, a number or text.",
        [],
        [new("value", PortKind.Data, "value")],
        [new("value", ParamKind.Text, "Value", "0")],
        settings => new ConstantNode(PlcValue.ParseLiteral(settings.Text("value"))));

    public override void Evaluate(NodeExecution execution) => execution.Output("value", value);
}

public sealed class CompareNode(string comparison) : PureNode
{
    private static readonly string[] Comparisons = [">", ">=", "<", "<=", "==", "!="];

    public static readonly NodeDefinition Definition = new(
        "logic.compare",
        "Logic",
        "Compare",
        "Compares a with b. Numbers compare numerically, anything else as text.",
        [new("a", PortKind.Data, "a"), new("b", PortKind.Data, "b")],
        [new("result", PortKind.Data, "result")],
        [new("operator", ParamKind.Choice, "Operator", "==", Comparisons), new("a", ParamKind.Text, "a"), new("b", ParamKind.Text, "b")],
        settings => new CompareNode(settings.Choice("operator")));

    public override void Evaluate(NodeExecution execution)
    {
        object? a = execution.Input("a");
        object? b = execution.Input("b");
        int order = IsNumberLike(a) && IsNumberLike(b)
            ? PlcValue.ToDouble(a).CompareTo(PlcValue.ToDouble(b))
            : string.CompareOrdinal(PlcValue.Format(a), PlcValue.Format(b));

        execution.Output("result", comparison switch
        {
            ">" => order > 0,
            ">=" => order >= 0,
            "<" => order < 0,
            "<=" => order <= 0,
            "==" => order == 0,
            _ => order != 0,
        });
    }

    private static bool IsNumberLike(object? value) => value is bool || PlcValue.IsNumeric(value);
}

public sealed class ArithmeticNode(string operation) : PureNode
{
    private static readonly string[] Operations = ["+", "-", "*", "/", "MOD"];

    public static readonly NodeDefinition Definition = new(
        "math.arithmetic",
        "Math",
        "Arithmetic",
        "a (op) b. Two integers give an integer (division truncates, like ST); otherwise a real.",
        [new("a", PortKind.Data, "a"), new("b", PortKind.Data, "b")],
        [new("result", PortKind.Data, "result")],
        [new("operator", ParamKind.Choice, "Operator", "+", Operations), new("a", ParamKind.Text, "a"), new("b", ParamKind.Text, "b")],
        settings => new ArithmeticNode(settings.Choice("operator")));

    public override void Evaluate(NodeExecution execution)
    {
        object? a = execution.Input("a");
        object? b = execution.Input("b");
        execution.Output("result", a is long && b is long ? Integer((long)a, (long)b) : Real(PlcValue.ToDouble(a), PlcValue.ToDouble(b)));
    }

    private object Integer(long a, long b)
    {
        if (b == 0 && operation is "/" or "MOD")
            throw new DivideByZeroException($"{a} {operation} 0");
        return operation switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" => a / b,
            _ => a % b,
        };
    }

    private object Real(double a, double b)
    {
        if (b == 0 && operation is "/" or "MOD")
            throw new DivideByZeroException($"{a.ToString(CultureInfo.InvariantCulture)} {operation} 0");
        return operation switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" => a / b,
            _ => a % b,
        };
    }
}
