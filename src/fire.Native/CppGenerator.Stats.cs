using System;
using System.Collections.Generic;
using System.Linq;
using fire.Bytecode;

namespace fire.Native
{
    // `forge build --stats` (docs/ROADMAP.md section 16): what the generator made of each fire function - NativeStats puts it together with the linker's map.
    public sealed partial class CppGenerator
    {
        private bool _collectStats;

        /// <summary>The functions of the last round (those that are generated here).</summary>
        private List<FuncStat> FuncStats() =>
            new[] { _main! }.Concat(_funcs).Where(f => f.Stat != null && !f.External).Select(f => f.Stat!).GroupBy(s => s.CName).Select(g => g.Last()).ToList();

        private FuncStat StatOf(Func f, int valueLocals, int cLocals)
        {
            string module = ModuleOfStat(f);
            string what = f.Kind switch
            {
                FuncKind.Main => "(the program)",
                FuncKind.FireBody => "(a fire thread)",
                FuncKind.Lambda => "(a lambda)",
                FuncKind.Ctor => (f.Chunk.OwnerClass?.Name ?? "?") + " (constructor)",
                FuncKind.Init => (f.Chunk.OwnerClass?.Name ?? "?") + " (field initializers)",
                FuncKind.Dtor => (f.Chunk.OwnerClass?.Name ?? "?") + " (destructor)",
                _ => f.Proto is { } proto ? Describe(proto).Trim('\'') : "",
            };
            var abi = f.CAbi;
            return new FuncStat(f.Name, what, module, f.ParamCount, abi?.ParamType.Count(t => t != 0) ?? 0, abi != null && abi.RetType != 0, valueLocals, cLocals,
                f.Root.MaxDepth, f.Scalars.Count, abi != null ? null : NoCAbiReason(f));
        }

        private string ModuleOfStat(Func f)
        {
            if (f.Kind == FuncKind.Main) return "program";   // (the top level of the program; the top levels of the libraries run in it too)
            int? src = f.Chunk.FirstSourceIndex;
            if (src is not int i) return "(no source)";
            if (i >= _program.FirstUserSource) return "program";
            return _program.SourceModules is { } modules && i < modules.Count && modules[i] is { } m ? m : "core";
        }

        /// <summary>Why a function has no C interface (CAbiOf, CAbiParams): the first reason that applies.</summary>
        private string NoCAbiReason(Func f)
        {
            if (DebugBuild) return "debug build";
            if (!ScalarsOn) return "no scalars";
            if (Modular || LibraryRole) return "module";
            if (f.External || f.Export != null) return "exported";
            if (f.Kind is not (FuncKind.Method or FuncKind.Static or FuncKind.Ctor)) return f.Kind switch
            {
                FuncKind.Main => "the top level",
                FuncKind.Init => "field initializers",
                FuncKind.Dtor => "destructor",
                FuncKind.Lambda => "lambda",
                _ => "fire thread",
            };
            if (f.SpecParams != null || f.SpecTArgs != null || f.SpecClassOwner != null) return "copy of a generic method";
            if (f.Chunk.Handlers.Count > 0) return "try/catch";
            if (f.Proto is { } proto && MethodKey(proto) is { } key && (key.Name.StartsWith("get_", StringComparison.Ordinal) || key.Name.StartsWith("set_", StringComparison.Ordinal)))
                return "property accessor";
            var code = f.Decoded;
            if (code.Any(c => c.Op == OpCode.TakeCallTypeArgs)) return "generic method";
            bool unitAny = code.Any(c => c.Op == OpCode.GuardLocal && c.A[0] < f.ParamCount && c.A[3] != 0xFFFF
                && f.Chunk.Constants[c.A[3]].AsString().TrimStart('#').StartsWith("any", StringComparison.Ordinal));
            return unitAny ? "no defined parameter or return (parameters with `: any` units)" : "no defined parameter or return";
        }
    }
}
