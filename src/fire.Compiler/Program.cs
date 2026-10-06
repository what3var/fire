using fire.Compiler;

// Befehlszeile des Compilers, siehe CommandLineParser (run / build).
// `bridge-packages <folder>`: for the build of the solution - writes the packages of the standard bridges (docs/PACKAGES.md).
if (args.Length >= 2 && args[0] == "bridge-packages")
{
    foreach (var file in StandardBridgePackages.Build(args[1])) Console.WriteLine(file);
    return 0;
}

// The standard packages (the bridges as packages) are installed from the folder PackageSource next to the program when they are missing - quick when nothing changed.
fire.Package.Manager.StandardPackages.EnsureInstalled(message => Console.Error.WriteLine(message));
fire.Compiler.ConsoleToolchainPrompt.Install();
return CommandLineRunner.Run(args, Console.Out, Console.Error);
