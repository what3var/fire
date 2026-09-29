using System.Diagnostics;
using System.Text;
using fire.Benchmarks;
using fire.Bytecode;
using fire.Compiler;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;

// Mikro-Benchmarks für die VM.
//
//   dotnet run -c Release --project src/fire.Benchmarks -- [Optionen]
//
//   --mode debug|release|performance|all   Ausführungsmodus (Vorgabe: performance)
//   --runs N                                Messläufe pro Benchmark (Vorgabe: 5), dazu 1 Aufwärmlauf
//   --filter TEXT                           nur Benchmarks, deren Name TEXT enthält
//   --list                                  Benchmarks auflisten
//   --save DATEI                            Ergebnisse (Name=Ausgabe) als Referenz speichern
//   --check DATEI                           Ergebnisse gegen eine Referenz prüfen (Exit-Code 1 bei Abweichung)
//
// Gemessen wird nur `VM.Run()` (Kompilieren zählt nicht); angezeigt: kleinste und mittlere Zeit.

var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) continue;
    string key = args[i].Substring(2);
    string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
    options[key] = value;
}

if (options.ContainsKey("list"))
{
    foreach (var script in BenchmarkScripts.All) Console.WriteLine($"{script.Name,-8} {script.Description}");
    return 0;
}

int runs = options.TryGetValue("runs", out var runsText) && int.TryParse(runsText, out var parsedRuns) ? parsedRuns : 5;
string modeText = options.TryGetValue("mode", out var m) && m != null ? m.ToLowerInvariant() : "performance";
string? filter = options.GetValueOrDefault("filter");
var modes = modeText switch
{
    "debug" => new[] { VmExecutionMode.Debug },
    "release" => new[] { VmExecutionMode.Release },
    "performance" => new[] { VmExecutionMode.Performance },
    "all" => new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance },
    _ => throw new ArgumentException("--mode: debug, release, performance oder all"),
};

var results = new SortedDictionary<string, string>();
var totals = new Dictionary<VmExecutionMode, double>();

Console.WriteLine($"{"Benchmark",-8} {"Modus",-12} {"min ms",9} {"Mittel ms",10}  Ergebnis");
foreach (var mode in modes)
{
    foreach (var script in BenchmarkScripts.All)
    {
        if (filter != null && !script.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

        var times = new List<double>();
        string output = string.Empty;
        for (int run = 0; run <= runs; run++) // Lauf 0 = Aufwärmen (JIT), zählt nicht
        {
            var (ms, text) = RunOnce(script.Source, mode);
            output = text;
            if (run > 0) times.Add(ms);
        }
        double min = times.Min();
        totals[mode] = totals.GetValueOrDefault(mode) + min;
        results[script.Name] = output;
        Console.WriteLine($"{script.Name,-8} {mode,-12} {min,9:F1} {times.Average(),10:F1}  {output}");
    }
}
foreach (var (mode, total) in totals)
    Console.WriteLine($"{"SUMME",-8} {mode,-12} {total,9:F1}");

if (options.TryGetValue("save", out var savePath) && savePath != null)
{
    File.WriteAllLines(savePath, results.Select(r => $"{r.Key}={r.Value}"));
    Console.WriteLine($"Referenz gespeichert: {savePath}");
}

if (options.TryGetValue("check", out var checkPath) && checkPath != null)
{
    var expected = File.ReadAllLines(checkPath).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    int mismatches = 0;
    foreach (var (name, actual) in results)
    {
        if (!expected.TryGetValue(name, out var want)) continue;
        if (want != actual)
        {
            mismatches++;
            Console.WriteLine($"ABWEICHUNG {name}: erwartet '{want}', erhalten '{actual}'");
        }
    }
    Console.WriteLine(mismatches == 0 ? "Alle Ergebnisse stimmen mit der Referenz überein." : $"{mismatches} Abweichung(en)!");
    return mismatches == 0 ? 0 : 1;
}
return 0;

static (double Milliseconds, string Output) RunOnce(string source, VmExecutionMode mode)
{
    var output = new StringBuilder();
    var sources = new[] { fire.Standard.Prelude.Source, source }
        .Select(s => Preprocessor.Process(s, Directory.GetCurrentDirectory(), new HashSet<string>())).ToList();
    var program = Parser.ParseMultiple(sources);
    var natives = new NativeRegistry();
    natives.Register("print", a => { output.Append(a[0].ToString()); return Value.MakeUndefined(); });
    natives.RegisterBaseTypeNatives();
    var resolved = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolved, natives);
    var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, executionMode: mode);

    GC.Collect();
    GC.WaitForPendingFinalizers();
    var sw = Stopwatch.StartNew();
    vm.Run();
    sw.Stop();
    if (vm.UnhandledException != null)
        output.Append("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
    return (sw.Elapsed.TotalMilliseconds, output.ToString());
}
