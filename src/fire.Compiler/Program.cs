using fire.Compiler;

// Befehlszeile des Compilers, siehe CommandLineParser (run / build).
// `bridge-packages <folder>`: for the build of the solution - writes the packages of the standard bridges (docs/PACKAGES.md).
if (args.Length >= 2 && args[0] == "bridge-packages")
{
    foreach (var file in StandardBridgePackages.Build(args[1])) Console.WriteLine(file);
    return 0;
}

// `ui <file.fxml> [-o <file>]`: writes the script generated from the markup of a user interface (docs/UI_MARKUP.md); without -o to the output.
if (args.Length >= 2 && args[0] == "ui")
{
    try
    {
        string path = args[1];
        string script = fire.UI.Markup.FireUiGenerator.Generate(fire.UI.Markup.MarkupParser.Parse(File.ReadAllText(path)), path);
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

// The standard packages (the bridges as packages) are installed from the folder PackageSource next to the program when they are missing - quick when nothing changed.
fire.Package.Manager.StandardPackages.EnsureInstalled(message => Console.Error.WriteLine(message));
fire.Compiler.ConsoleToolchainPrompt.Install();
return CommandLineRunner.Run(args, Console.Out, Console.Error);
