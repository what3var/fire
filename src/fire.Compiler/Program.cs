using fire.Compiler;

// Command line of the compiler, see CommandLineParser (run / build / native / test).
// `prebuild-standard <Source> <Prebuilt>`: for the build of the solution - builds the libraries of the standard packages for this machine (docs/PACKAGES.md).
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

// The standard packages are packed and installed from the projects in Source next to the program when they are missing or changed - quick when nothing changed.
StandardSources.EnsureInstalled(message => Console.Error.WriteLine(message));
fire.Compiler.ConsoleToolchainPrompt.Install();
return CommandLineRunner.Run(args, Console.Out, Console.Error);
