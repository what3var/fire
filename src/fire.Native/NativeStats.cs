using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace fire.Native
{
    /// <summary>What the generator made of one fire function (`forge build --stats`, docs/ROADMAP.md section 16): where it comes from, and what of it is a C type and
    /// what a Value.</summary>
    /// <param name="CName">The C function (the body is `CName_b` when a wrapper counts the call depth, `CName_c` with C parameters, `CName_p` with the pointer convention).</param>
    /// <param name="FireName">`Class.Method`, or a description.</param>
    /// <param name="Module">The module of its source: "core" (the prelude), an import, or "program".</param>
    /// <param name="Params">The parameters (without `this`); <paramref name="CParams"/> of them are C numbers.</param>
    /// <param name="ValueLocals">The variables that are Values; <paramref name="CLocals"/> are C numbers.</param>
    /// <param name="StackSlots">The slots of the operand stack (each a Value); <paramref name="Shadows"/>: their scalar shadows (C numbers).</param>
    /// <param name="NoCAbi">Why it has no C interface (null: it has one).</param>
    public sealed record FuncStat(string CName, string FireName, string Module, int Params, int CParams, bool CReturn, int ValueLocals, int CLocals,
        int StackSlots, int Shadows, string? NoCAbi);

    /// <summary>The report of a native build with `--stats`: the code by origin (from the linker's map), the typing of the generated functions, and for DOS the memory
    /// the program needs to load.</summary>
    public static class NativeStats
    {
        public sealed record Origin(string Name, long Bytes, int Functions);
        public sealed record Biggest(string CName, string What, string Origin, long Bytes);
        public sealed record ModuleTyping(string Module, int Functions, long Bytes, int WithCAbi, int Params, int CParams, int ValueLocals, int CLocals, int StackSlots, int Shadows);
        public sealed record Report(string Target, long FileBytes, long? LoadBytes, IReadOnlyList<Origin> Code, IReadOnlyList<Origin> Data, IReadOnlyList<ModuleTyping> Typing,
            IReadOnlyDictionary<string, int> NoCAbi, IReadOnlyList<Biggest> Biggest, IReadOnlyDictionary<string, int> Generated, IReadOnlyList<FuncStat> Functions);

        /// <summary>The map file the linker writes next to the program with `--stats`.</summary>
        public static string MapFileOf(string exe) => exe + ".map";

        /// <summary>The linker options that write <paramref name="map"/> (and keep each function in a section of its own, so that the map has one entry per function);
        /// null for a toolchain without them.</summary>
        public static string? MapOptions(string kind, string map) => kind switch
        {
            "watcom" => $"-Wl,option -Wl,map=\"{map}\"",
            "gcc" or "clang" or "mingw" => $"-ffunction-sections -Wl,-Map=\"{map}\"",
            _ => null,
        };

        /// <summary>The report of the program <paramref name="exe"/> built from the C files in <paramref name="workDir"/> (<paramref name="cFile"/> the program).</summary>
        public static Report Analyze(string target, string exe, string workDir, string cFile, string mapFile, IReadOnlyList<FuncStat> funcs)
        {
            var origins = OriginsOf(workDir, cFile, funcs);
            var (code, data) = ReadMap(mapFile);
            var byFunc = funcs.ToDictionary(f => f.CName);
            var codeBy = new Dictionary<string, (long Bytes, int Count)>();
            var moduleBytes = new Dictionary<string, long>();
            var generated = new Dictionary<string, int>();
            var biggest = new List<Biggest>();
            foreach (var (section, size) in code)
            {
                string name = section.Split('.')[0];   // (gcc: `f.isra.0`, `f.part.1`, `f.constprop.0` are parts of f)
                string fn = BaseName(name);
                string origin;
                FuncStat? stat = byFunc.TryGetValue(fn, out var s) ? s : null;
                if (stat != null) origin = "fire: " + stat.Module;
                else origin = origins.TryGetValue(name, out var o) ? o : "C library";
                if (stat != null) moduleBytes[stat.Module] = (moduleBytes.TryGetValue(stat.Module, out long mb) ? mb : 0) + size;
                if (origin == "fire: helpers") generated[HelperKind(name)] = (generated.TryGetValue(HelperKind(name), out int g) ? g : 0) + (int)size;
                var (b, c) = codeBy.TryGetValue(origin, out var v) ? v : (0, 0);
                codeBy[origin] = (b + size, c + 1);
                biggest.Add(new Biggest(name.Length == 0 ? "_TEXT" : name, stat?.FireName ?? "", origin, size));
            }
            var typing = funcs.GroupBy(f => f.Module).Select(g => new ModuleTyping(g.Key, g.Count(), moduleBytes.TryGetValue(g.Key, out long bytes) ? bytes : 0,
                g.Count(f => f.NoCAbi == null), g.Sum(f => f.Params), g.Sum(f => f.CParams), g.Sum(f => f.ValueLocals), g.Sum(f => f.CLocals), g.Sum(f => f.StackSlots), g.Sum(f => f.Shadows)))
                .OrderByDescending(m => m.Bytes).ToList();
            var reasons = funcs.Where(f => f.NoCAbi != null).GroupBy(f => f.NoCAbi!).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count());
            return new Report(target, File.Exists(exe) ? new FileInfo(exe).Length : 0, DosLoadBytes(exe),
                codeBy.Select(kv => new Origin(kv.Key, kv.Value.Bytes, kv.Value.Count)).OrderByDescending(o => o.Bytes).ToList(),
                data.Select(kv => new Origin(kv.Key, kv.Value, 0)).OrderByDescending(o => o.Bytes).ToList(),
                typing, reasons, biggest.OrderByDescending(b => b.Bytes).Take(25).ToList(),
                generated.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value), funcs);
        }

        /// <summary>The function of a code section: `name_TEXT` (Open Watcom), `.text.name` (GNU).</summary>
        private static string BaseName(string section)
        {
            string fn = section;
            foreach (var suffix in new[] { "_b", "_c", "_p" })
                if (fn.EndsWith(suffix, StringComparison.Ordinal) && Regex.IsMatch(fn, @"f\d+_[bcp]$")) { fn = fn[..^2]; break; }
            return fn;
        }

        /// <summary>The kind of a generated helper by the start of its name (`gf_X`: a field getter, ...).</summary>
        private static string HelperKind(string name)
        {
            var m = Regex.Match(name, @"^([a-zA-Z]+?)(?:_|[A-Z0-9])");
            return m.Success ? m.Groups[1].Value : name;
        }

        /// <summary>Where each C function is defined: the runtime (`fire_rt.h` and its parts, the platform), the C of a package (the markers the generator puts around
        /// what it copies into the program: `// ---- ..., import "x": file` ... `// ---- end of file`), the generated code ("fire: helpers" for what is no fire function).</summary>
        private static Dictionary<string, string> OriginsOf(string workDir, string cFile, IReadOnlyList<FuncStat> funcs)
        {
            var where = new Dictionary<string, string>();
            foreach (var file in Directory.EnumerateFiles(workDir, "*.h*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(workDir, file).Replace('\\', '/');
                string origin = rel.StartsWith("pkg/", StringComparison.Ordinal) ? "C of package " + rel.Split('/')[1]
                    : rel.StartsWith("platform/", StringComparison.Ordinal) || rel.StartsWith("cc/", StringComparison.Ordinal) ? "runtime (platform)" : "runtime";
                foreach (var (name, _) in Definitions(File.ReadAllLines(file))) where.TryAdd(name, origin);
            }
            var lines = File.ReadAllLines(cFile);
            var stack = new Stack<string>();
            var start = new Regex(@"^// ---- (?:package )?(\S+) [^,]*, import ""([^""]+)""");
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.StartsWith("// ---- end of ", StringComparison.Ordinal)) { if (stack.Count > 0) stack.Pop(); continue; }
                var m = start.Match(l);
                if (m.Success) { stack.Push("C of package " + m.Groups[2].Value); continue; }
                if (DefinitionAt(lines, i) is { } name) where[name] = stack.Count > 0 ? stack.Peek() : "fire: helpers";
            }
            return where;
        }

        private static readonly Regex DefinitionRegex = new(@"^(?!typedef|return|else|if|for|while|#|//|/\*|\s)[A-Za-z_][\w \*]*?\b([A-Za-z_]\w*)\s*\(([^;]*)$", RegexOptions.Compiled);

        /// <summary>The name of the function whose definition starts at line <paramref name="i"/> (its `{` on this or the next line), else null.</summary>
        private static string? DefinitionAt(string[] lines, int i)
        {
            var m = DefinitionRegex.Match(lines[i]);
            if (!m.Success) return null;
            string rest = lines[i].TrimEnd();
            if (rest.EndsWith('{')) return m.Groups[1].Value;
            for (int j = i + 1; j < lines.Length && j < i + 6; j++)
            {
                string t = lines[j].Trim();
                if (t.StartsWith('{')) return m.Groups[1].Value;
                if (t.EndsWith(';') || t.Length == 0) return null;
            }
            return null;
        }

        private static IEnumerable<(string Name, int Line)> Definitions(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
                if (DefinitionAt(lines, i) is { } name) yield return (name, i);
        }

        /// <summary>The code sections (function, bytes) and the data (class or section, bytes) of a map of Open Watcom or of GNU ld.</summary>
        private static (List<(string Name, long Size)> Code, Dictionary<string, long> Data) ReadMap(string mapFile)
        {
            var code = new List<(string, long)>();
            var data = new Dictionary<string, long>();
            if (!File.Exists(mapFile)) return (code, data);
            var lines = File.ReadAllLines(mapFile);
            var watcom = new Regex(@"^(\S+)\s+(\w+)\s+(\w+)\s+[0-9a-f]{4}:[0-9a-f]{4}\s+([0-9a-f]{8})\s*$");
            var gnu = new Regex(@"^ (\.[\w.$]+)\s+0x[0-9a-f]+\s+0x([0-9a-f]+)\s");
            var gnuName = new Regex(@"^ (\.[\w.$]+)\s*$");
            var gnuNext = new Regex(@"^\s+0x[0-9a-f]+\s+0x([0-9a-f]+)\s");
            bool inSegments = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Contains("|   Segments   |")) { inSegments = true; continue; }
                if (inSegments && l.Contains("|   Memory Map   |")) inSegments = false;
                if (inSegments)
                {
                    var w = watcom.Match(l);
                    if (!w.Success) continue;
                    long size = Convert.ToInt64(w.Groups[4].Value, 16);
                    string seg = w.Groups[1].Value, cls = w.Groups[2].Value, grp = w.Groups[3].Value;
                    if (cls == "CODE") code.Add((seg.EndsWith("_TEXT", StringComparison.Ordinal) ? seg[..^5] : seg, size));
                    else Add(data, cls == "STACK" ? "stack" : grp == "DGROUP" ? "DGROUP (near data)" : cls == "FAR_DATA" ? "far data" : cls, size);
                    continue;
                }
                string? section = null; long bytes = 0;
                var g = gnu.Match(l);
                if (g.Success) { section = g.Groups[1].Value; bytes = Convert.ToInt64(g.Groups[2].Value, 16); }
                else if (gnuName.Match(l) is { Success: true } gn && i + 1 < lines.Length && gnuNext.Match(lines[i + 1]) is { Success: true } nx)
                { section = gn.Groups[1].Value; bytes = Convert.ToInt64(nx.Groups[1].Value, 16); i++; }
                if (section == null || bytes == 0) continue;
                if (section.StartsWith(".text.", StringComparison.Ordinal)) code.Add((section[6..], bytes));
                else if (section == ".text") code.Add(("", bytes));
                else if (section.StartsWith(".rodata", StringComparison.Ordinal)) Add(data, "read-only data", bytes);
                else if (section.StartsWith(".data", StringComparison.Ordinal)) Add(data, "data", bytes);
                else if (section.StartsWith(".bss", StringComparison.Ordinal)) Add(data, "bss", bytes);
            }
            return (code, data);
        }

        private static void Add(Dictionary<string, long> d, string key, long n) => d[key] = (d.TryGetValue(key, out long v) ? v : 0) + n;

        /// <summary>A DOS program (MZ without a PE part): the bytes it takes when it is loaded - the load module and the memory it asks for at least.</summary>
        public static long? DosLoadBytes(string exe)
        {
            try
            {
                var h = new byte[0x40];
                using (var s = File.OpenRead(exe)) if (s.Read(h, 0, h.Length) < 0x1C) return null;
                if (h[0] != 'M' || h[1] != 'Z') return null;
                int last = h[2] | h[3] << 8, pages = h[4] | h[5] << 8, header = h[8] | h[9] << 8, min = h[10] | h[11] << 8;
                int lfanew = h[0x3C] | h[0x3D] << 8 | h[0x3E] << 16 | h[0x3F] << 24;
                if (lfanew > 0 && lfanew < new FileInfo(exe).Length - 2)
                {
                    using var s = File.OpenRead(exe);
                    s.Position = lfanew;
                    int a = s.ReadByte(), b = s.ReadByte();
                    if (a == 'P' && b == 'E' || a == 'N' && b == 'E' || a == 'L' && b == 'E') return null;   // (Windows: not loaded as a whole)
                }
                long module = (long)pages * 512 - (last == 0 ? 0 : 512 - last) - header * 16L;
                return module + min * 16L;
            }
            catch (IOException) { return null; }
        }

        /// <summary>The report as text.</summary>
        public static string Text(Report r)
        {
            var sb = new StringBuilder();
            string K(long b) => b >= 10240 ? $"{b / 1024.0:0} KB" : $"{b} B";
            sb.AppendLine($"Statistics ({r.Target}): program {K(r.FileBytes)}" + (r.LoadBytes is { } load ? $", loaded {K(load)}{(load > 640 * 1024 ? " - more than the 640 KB of conventional memory" : "")}" : ""));
            long total = r.Code.Sum(o => o.Bytes);
            sb.AppendLine($"  code {K(total)}:");
            foreach (var o in r.Code) sb.AppendLine($"    {o.Bytes,9}  {100.0 * o.Bytes / Math.Max(1, total),5:0.0}%  {o.Name} ({o.Functions} functions)");
            if (r.Generated.Count > 0) sb.AppendLine("  generated helpers: " + string.Join(", ", r.Generated.Take(8).Select(kv => $"{kv.Key} {K(kv.Value)}")));
            if (r.Data.Count > 0) sb.AppendLine("  data: " + string.Join(", ", r.Data.Select(o => $"{o.Name} {K(o.Bytes)}")));
            sb.AppendLine("  fire functions by module (C numbers / all):");
            foreach (var m in r.Typing)
                sb.AppendLine($"    {m.Module,-18} {m.Functions,4} functions {K(m.Bytes),8}  C interface {m.WithCAbi}/{m.Functions}  parameters {m.CParams}/{m.Params}  " +
                    $"variables {m.CLocals}/{m.CLocals + m.ValueLocals}  stack slots {m.StackSlots} (+{m.Shadows} C shadows)");
            if (r.NoCAbi.Count > 0) sb.AppendLine("  no C interface: " + string.Join(", ", r.NoCAbi.Select(kv => $"{kv.Key} ({kv.Value})")));
            sb.AppendLine("  biggest functions:");
            foreach (var b in r.Biggest.Take(15)) sb.AppendLine($"    {b.Bytes,9}  {b.CName}{(b.What.Length > 0 ? " " + b.What : "")}  [{b.Origin}]");
            return sb.ToString();
        }

        public static string Json(Report r) => JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true });

        /// <summary>A report written by <see cref="Json"/> (a `.stats.json` next to a program), null when the text is none.</summary>
        public static Report? FromJson(string json)
        {
            try { return JsonSerializer.Deserialize<Report>(json); }
            catch (JsonException) { return null; }
        }
    }
}
