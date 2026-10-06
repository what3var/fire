using fire.Compiler;

// Befehlszeile des Compilers, siehe CommandLineParser (run / build).
fire.Compiler.ConsoleToolchainPrompt.Install();
return CommandLineRunner.Run(args, Console.Out, Console.Error);
