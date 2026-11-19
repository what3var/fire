using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using fire.Runtime;

namespace fire.Native
{
    // The program in several translation units (docs/spec/NATIVE_SPLIT.md, phase 1): Run writes the marks of ProgramSplit between the parts of the text, ProgramSplit
    // puts the parts into the files once the text is complete.
    public sealed partial class CppGenerator
    {
        /// <summary>The program may be split (null: one file, no marks in the text): `On` always, `Auto` when its C is large.</summary>
        private SplitMode? _splitMode;

        /// <summary>The units of the program, once it was split (null: one file).</summary>
        private List<SplitUnit>? _splitUnits;

        /// <summary>The key a function is grouped by: its module (the program, `core`, an import, a library of the project), then its source and first line (a
        /// class is a run of lines; lambdas and the copies of a generic method sit next to their function), its class and its kind.</summary>
        private ProgramSplit.FuncKey SplitKeyOf(Func f)
        {
            string module = ModuleOfStat(f);
            var first = f.Chunk.Lines.FirstOrDefault();
            string key = string.Create(CultureInfo.InvariantCulture, $"{first.SourceIndex:D5}:{first.Line:D7}:{f.Chunk.OwnerClass?.Name ?? ""}:{(int)f.Kind}");
            return new ProgramSplit.FuncKey(module, key);
        }

        /// <summary>The unit the natives in C of an import go to: their own when they stand alone (StaticLinkage.NativesStandAlone, as with linkage "static"), else the
        /// one they share with the others (graphics and windows, scene3d).</summary>
        private string SplitNativesGroup(string import)
        {
            if (!NativesStandAlone(_program, import, _target)) return "shared";
            var package = _packageImports.FirstOrDefault(p => string.Equals(p.Key, import, StringComparison.Ordinal)).Import;
            return (package?.Name ?? import).ToLowerInvariant();
        }

        /// <summary>The natives in C that the code of the program may call, with their C signatures (a typed one with its C numbers).</summary>
        private List<ProgramSplit.NativeFn> SplitNatives()
        {
            var result = new List<ProgramSplit.NativeFn>();
            static List<string> Values(int argc, bool needsList) => Enumerable.Repeat("Value", argc).Concat(needsList ? new[] { "OwnList*" } : Array.Empty<string>()).ToList();
            if (_usesGraphics && _standardNativesWritten.Contains("graphics"))
            {
                string group = SplitNativesGroup(NativeImports.Graphics);
                foreach (var (name, (argc, _)) in GraphicsBridgeNatives) result.Add(new ProgramSplit.NativeFn(group, $"fire_gfx_{name}", "Value", Values(argc, GraphicsNeedsList.Contains(name))));
            }
            if (_usesWindows && _standardNativesWritten.Contains("windows"))
            {
                string group = SplitNativesGroup(NativeImports.Windows);
                foreach (var (name, (argc, reference)) in WindowsBridgeNatives) result.Add(new ProgramSplit.NativeFn(group, $"fire_win_Win{name}", "Value", Values(argc, reference)));
            }
            foreach (var (import, key) in _packageImports.Where(p => p.Import.Import.Native is { IsC: true } && !_standardNativesWritten.Contains(p.Import.Name)))
            {
                string group = SplitNativesGroup(key);
                foreach (var fn in import.Import.Native!.Functions)
                {
                    bool typed = NativeCTypes.IsTyped(fn, true);
                    var ps = Enumerable.Range(0, fn.Arguments).Select(i => (typed ? NativeCTypes.ParamCType(fn, i) : null) ?? "Value").Concat(fn.NeedsList ? new[] { "OwnList*" } : Array.Empty<string>()).ToList();
                    result.Add(new ProgramSplit.NativeFn(group, fn.Symbol, (typed ? NativeCTypes.ReturnCType(fn) : null) ?? "Value", ps));
                }
            }
            return result;
        }

        /// <summary>The text without the marks of the split (each is a line of its own): what the generator writes for one file.</summary>
        private static string WithoutSplitMarks(string text)
        {
            var sb = new StringBuilder(text.Length);
            int pos = 0;
            while (pos < text.Length)
            {
                int e = text.IndexOf('\n', pos);
                int next = e < 0 ? text.Length : e + 1;
                if (string.CompareOrdinal(text, pos, ProgramSplit.MarkPrefix, 0, ProgramSplit.MarkPrefix.Length) != 0) sb.Append(text, pos, next - pos);
                pos = next;
            }
            return sb.ToString();
        }
    }
}
