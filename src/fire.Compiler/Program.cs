using fire.Compiler;

// Command line of the compiler, see CommandLineParser (run / build / native / test).
// `prebuild-standard <Source> <Prebuilt>`: for the build of the solution - builds the libraries of the standard packages for this machine (docs/packages/README.md).
if (args.Length >= 3 && args[0] == "prebuild-standard")
{
    foreach (var file in StandardSources.Prebuild(args[1], args[2], Console.Error.WriteLine)) Console.WriteLine(file);
    return 0;
}
// `install-standard`: packs and installs the standard packages from Source next to the program (what every start does; ember asks for it).
if (args.Length >= 1 && args[0] == "install-standard")
{
    StandardSources.EnsureInstalled(Console.WriteLine);
    return 0;
}

// `falstad <circuit.txt> [-o <file.biscuit>]`: a circuit of Falstad's simulator (File > Export As Text) as a schematic (docs/circuits/README.md); without -o to the output.
if (args.Length >= 2 && args[0] == "falstad")
{
    try
    {
        var result = fire.UI.Markup.FalstadImport.Convert(File.ReadAllText(args[1]));
        foreach (var w in result.Warnings) Console.Error.WriteLine($"{Path.GetFileName(args[1])}: {w}");
        int o = Array.IndexOf(args, "-o");
        if (o >= 0 && o + 1 < args.Length) File.WriteAllText(args[o + 1], result.Biscuit);
        else Console.Out.Write(result.Biscuit);
        return 0;
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

// `model <file.gltf|file.glb> [-o <file.f3d>] [--fps n]`: fire's model file (docs/graphics/models.md) from glTF 2.0 - what a resource "x.gltf" becomes when a program is built.
if (args.Length >= 2 && args[0] == "model")
{
    try
    {
        int o = Array.IndexOf(args, "-o"), f = Array.IndexOf(args, "--fps");
        int fps = f >= 0 && f + 1 < args.Length && int.TryParse(args[f + 1], out int n) && n > 0 && n <= 240 ? n : ModelConverter.FramesPerSecond;
        byte[] f3d = ModelConverter.ConvertFile(args[1], fps);
        string output = o >= 0 && o + 1 < args.Length ? args[o + 1] : Path.ChangeExtension(args[1], ".f3d");
        File.WriteAllBytes(output, f3d);
        Console.WriteLine($"{output} ({f3d.Length} bytes)");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
    {
        Console.Error.WriteLine($"{args[1]}: {ex.Message}");
        return 1;
    }
}

// `ui <file.fxml> [-o <file>]` / `biscuit <file.biscuit> [-o <file>]` / `fdata <file.fdata> [-o <file>]`: writes the script generated from the markup of a user interface (docs/ui/markup.md), a data model (docs/data/designer.md) or from the
// schematic of a circuit (docs/circuits/README.md); without -o to the output.
if (args.Length >= 2 && (args[0] == "ui" || args[0] == "biscuit" || args[0] == "fdata"))
{
    try
    {
        string path = args[1];
        string script = fire.UI.Markup.MarkupFiles.GenerateScript(path, File.ReadAllText(path));
        int o = Array.IndexOf(args, "-o");
        if (o >= 0 && o + 1 < args.Length) File.WriteAllText(args[o + 1], script);
        else Console.Out.Write(script);
        return 0;
    }
    catch (fire.UI.Markup.MarkupException ex)
    {
        foreach (var d in ex.Diagnostics) Console.Error.WriteLine($"{Path.GetFileName(args[1])}: {d}");
        return 1;
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

// The standard packages are packed and installed from the projects in Source next to the program when they are missing or changed - quick when nothing changed.
StandardSources.EnsureInstalled(message => Console.Error.WriteLine(message));
fire.Compiler.ConsoleToolchainPrompt.Install();
return CommandLineRunner.Run(args, Console.Out, Console.Error);
