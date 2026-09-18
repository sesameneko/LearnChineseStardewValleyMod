using System.Text.Json;
using XnbStringTool;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
};

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "read":
            return CmdRead(args);
        case "write":
            return CmdWrite(args);
        case "dump-dir":
            return CmdDumpDir(args);
        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage:
          XnbStringTool read <input.xnb> <output.json>
          XnbStringTool write <input.json> <output.xnb>
          XnbStringTool dump-dir <inputDir> <outputDir>   (converts every *.xnb found, mirroring the directory structure)
        """);
}

int CmdRead(string[] args)
{
    if (args.Length != 3) { PrintUsage(); return 1; }
    string inPath = args[1], outPath = args[2];

    using var input = File.OpenRead(inPath);
    var parsed = XnbStringTable.Read(input);

    WriteJson(parsed, outPath);
    Console.WriteLine($"read {parsed.Entries.Count} entries from {inPath} (compressed={parsed.WasCompressed}) -> {outPath}");
    return 0;
}

int CmdWrite(string[] args)
{
    if (args.Length != 3) { PrintUsage(); return 1; }
    string inPath = args[1], outPath = args[2];

    using var doc = JsonDocument.Parse(File.ReadAllText(inPath));
    var entriesElement = doc.RootElement.TryGetProperty("entries", out var e) ? e : doc.RootElement;

    var entries = new Dictionary<string, string>();
    foreach (var prop in entriesElement.EnumerateObject())
        entries[prop.Name] = prop.Value.GetString() ?? "";

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    using var output = File.Create(outPath);
    XnbStringTable.Write(output, entries);

    Console.WriteLine($"wrote {entries.Count} entries to {outPath}");
    return 0;
}

int CmdDumpDir(string[] args)
{
    if (args.Length != 3) { PrintUsage(); return 1; }
    string inDir = args[1], outDir = args[2];

    int ok = 0, failed = 0;
    foreach (var file in Directory.EnumerateFiles(inDir, "*.xnb", SearchOption.AllDirectories))
    {
        string rel = Path.GetRelativePath(inDir, file);
        string outPath = Path.Combine(outDir, Path.ChangeExtension(rel, ".json"));
        try
        {
            using var input = File.OpenRead(file);
            var parsed = XnbStringTable.Read(input);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            WriteJson(parsed, outPath);
            ok++;
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"skip {rel}: {ex.Message}");
        }
    }

    Console.WriteLine($"dump-dir complete: {ok} converted, {failed} skipped");
    return 0;
}

void WriteJson(XnbStringTableFile parsed, string outPath)
{
    var ordered = new SortedDictionary<string, string>(parsed.Entries, StringComparer.Ordinal);
    var payload = new
    {
        typeReaders = parsed.TypeReaders.Select(r => new { r.Name, r.Version }),
        wasCompressed = parsed.WasCompressed,
        entries = ordered,
    };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllText(outPath, JsonSerializer.Serialize(payload, jsonOptions));
}
