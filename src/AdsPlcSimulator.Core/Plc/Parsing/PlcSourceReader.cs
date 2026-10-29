using System.Xml.Linq;

namespace AdsPlcSimulator.Core.Plc.Parsing;

/// <summary>
/// Reads declaration sources from plain <c>.st</c> files and from TwinCAT XML objects
/// (<c>.TcDUT</c>, <c>.TcGVL</c>, <c>.TcPOU</c>, <c>.TcIO</c>), so real PLC project files can be dropped in unchanged.
/// </summary>
public static class PlcSourceReader
{
    private static readonly string[] TwinCatExtensions = [".tcdut", ".tcgvl", ".tcpou", ".tcio"];

    public static bool IsSupported(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".st" || TwinCatExtensions.Contains(extension);
    }

    public static DeclarationSet ReadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"PLC source directory '{directory}' does not exist.");

        var declarations = new DeclarationSet();
        IEnumerable<string> files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(IsSupported)
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
            declarations.Add(ReadFile(file));
        return declarations;
    }

    public static DeclarationSet ReadFile(string path)
    {
        string fileName = Path.GetFileName(path);
        string text = File.ReadAllText(path);
        if (Path.GetExtension(path).Equals(".st", StringComparison.OrdinalIgnoreCase))
            return DeclarationParser.Parse(text, fileName, Path.GetFileNameWithoutExtension(path));

        return ReadTwinCatObject(text, fileName);
    }

    public static DeclarationSet ReadTwinCatObject(string xml, string fileName)
    {
        XElement root = XDocument.Parse(xml).Root
            ?? throw new PlcParseException($"{fileName}: empty TwinCAT object file.");

        // Only the object's own declaration is used; METHOD and PROPERTY children are implementation details.
        XElement plcObject = root.Elements().FirstOrDefault(element => element.Element("Declaration") is not null)
            ?? throw new PlcParseException($"{fileName}: no <Declaration> element found.");

        string scope = plcObject.Attribute("Name")?.Value ?? Path.GetFileNameWithoutExtension(fileName);
        return DeclarationParser.Parse(plcObject.Element("Declaration")!.Value, fileName, scope);
    }
}
