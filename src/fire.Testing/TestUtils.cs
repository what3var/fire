using fire.Runtime;
using System;

sealed class LoggingDestructRunner : IDestructRunner
{
    public void RunDestructor(ObjectInstance instance) =>
        Console.WriteLine($"  destruct() aufgerufen für Instanz von '{instance.ClassName}'");
}

sealed class RaceDemoRunner : IDestructRunner
{
    private readonly ObjectInstance _watchFor;
    private readonly ObjectInstance _transferSource;
    private readonly IDestructRunner _inner;

    public RaceDemoRunner(ObjectInstance watchFor, ObjectInstance transferSource, IDestructRunner inner)
    {
        _watchFor = watchFor;
        _transferSource = transferSource;
        _inner = inner;
    }

    public void RunDestructor(ObjectInstance instance)
    {
        _inner.RunDestructor(instance);
        if (ReferenceEquals(instance, _watchFor))
        {
            Console.WriteLine("  (simuliert: waehrend destruct() wird TakeTo auf das gerade zerstoerte Objekt aufgerufen)");
            _transferSource.TakeTo(_watchFor, this);
        }
    }
}

/// <summary>Builds a generated native program for the tests: the program (C) with the C compiler that belongs to <c>cxx</c>, and - when it has natives in C++ -
/// those with <c>cxx</c>, linked together. The files are <c>name.c</c> and <c>name_natives.cpp</c> in <c>dir</c> (next to the runtime files).</summary>
static class NativeTestBuild
{
    /// <summary>Writes the sources and returns the command lines, in order (each: the program, its arguments).</summary>
    public static List<(string Tool, string Arguments)> Commands(string cxx, fire.Native.NativeSources sources, string dir, string name, string flags, string exe, string tail = "")
    {
        string cc = fire.Native.ToolchainDef.CCompilerFor(cxx);
        string cFile = Path.Combine(dir, name + ".c");
        File.WriteAllText(cFile, sources.Program);
        var commands = new List<(string, string)>();
        if (sources.Natives == null)
        {
            commands.Add((cc, $"-std=c11 {flags} \"{cFile}\" -I\"{dir}\" -o \"{exe}\"{tail} -lm"));
            return commands;
        }
        string nativesFile = Path.Combine(dir, name + "_natives.cpp");
        File.WriteAllText(nativesFile, sources.Natives);
        string obj = Path.Combine(dir, name + ".o");
        commands.Add((cc, $"-std=c11 {flags} -c \"{cFile}\" -I\"{dir}\" -o \"{obj}\""));
        commands.Add((cxx, $"-std=c++17 {flags} \"{nativesFile}\" \"{obj}\" -I\"{dir}\" -o \"{exe}\"{tail}"));
        return commands;
    }

    /// <summary>Runs the commands of <see cref="Commands"/> with <paramref name="run"/> (tool, arguments -> output, exit code); the output of all, the exit code of the first that failed.</summary>
    public static string Build(Func<string, string, (string Output, int Exit)> run, string cxx, fire.Native.NativeSources sources, string dir, string name, string flags, string exe, out int exit, string tail = "")
    {
        var output = new System.Text.StringBuilder();
        exit = 0;
        foreach (var (tool, arguments) in Commands(cxx, sources, dir, name, flags, exe, tail))
        {
            var (text, code) = run(tool, arguments);
            output.Append(text);
            if ((exit = code) != 0) break;
        }
        return output.ToString();
    }

    /// <summary>A program for <see cref="Build"/>: runs the tool in <paramref name="dir"/>.</summary>
    public static Func<string, string, (string, int)> In(string dir) => (tool, arguments) =>
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, arguments)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir })!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (output + err.Result, p.ExitCode);
    };
}
