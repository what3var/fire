using System;
using System.Collections.Generic;
using System.Linq;
using fire.Bytecode;
using fire.Values;
using fire.Standard;

namespace fire.Native
{
    // Ebene 2, Stufe 2b (docs/spec/NATIVE_BACKEND.md "Parameter und Rückgaben als C-Typen"): a method whose parameters or return value are declared
    // `defined int` / `defined float` / `defined word` (`defined bool` for the return value, SPEC 8.10b) gets a body with those C types (`fN_c`) - its direct
    // callers pass C numbers and take a C number back. `fN` stays the function with Values for everything else (dispatchers, lambdas, reflection): it checks
    // the parameters like the prologue would and puts the result into a Value.
    public sealed partial class CppGenerator
    {
        /// <summary>The C interface of a method: per parameter the static type it has in C (0: a Value), the static type of its C return value (0: a Value).</summary>
        private sealed class CAbi
        {
            public required int[] ParamType;
            public required string[] ParamWhat;
            /// <summary>The unit demand of a C parameter with a unit (`#m`, `mm`: CheckUnit of the prologue, done by the caller), null without one.</summary>
            public required string?[] ParamDemand;
            public int RetType;
            public bool Active => RetType != 0 || ParamType.Any(t => t != 0);
        }

        /// <summary>The C return type each method was found to have in the last round (absent: a Value) - a change makes the generator run another round.</summary>
        private readonly Dictionary<Func, int> _cRet = new();
        private readonly Dictionary<Func, CAbi?> _cAbiParams = new();

        /// <summary>The C interface of a method, or null when it has none: only plain methods (no generic one with a trampoline, no `try` - the frame
        /// of a catch holds Values, no accessor, no module export) with scalars on.</summary>
        private CAbi? CAbiOf(Func f)
        {
            if (!_cAbiParams.TryGetValue(f, out var abi))
            {
                abi = CAbiParams(f);
                _cAbiParams[f] = abi;
            }
            if (abi == null) return null;
            abi.RetType = _cRet.TryGetValue(f, out int ret) ? ret : 0;
            return abi.Active ? abi : null;
        }

        private CAbi? CAbiParams(Func f)
        {
            if (!ScalarsOn || Wrapped || Modular || LibraryRole || f.External || f.Export != null || f.Kind is not (FuncKind.Method or FuncKind.Static)) return null;
            if (f.SpecParams != null || f.SpecTArgs != null || f.SpecClassOwner != null || f.Proto is not { } proto || f.Chunk.Handlers.Count > 0) return null;
            if (MethodKey(proto) is { } key && (key.Name.StartsWith("get_", StringComparison.Ordinal) || key.Name.StartsWith("set_", StringComparison.Ordinal))) return null;
            var code = OpInfo.Decode(f.Chunk.Code);
            if (code.Any(c => c.Op == OpCode.TakeCallTypeArgs)) return null;
            if (f.HasSelf && code.Any(c => c.Op == OpCode.GuardLocalType && !string.IsNullOrEmpty(DeclaredType.Parse(f.Chunk.Constants[c.A[1]].AsString()).TypeParamClass))) return null;
            var types = new int[f.ParamCount];
            var whats = new string[f.ParamCount];
            var demands = new string?[f.ParamCount];
            foreach (var c in code.Where(c => c.Op == OpCode.GuardLocal && c.A[0] < f.ParamCount))
            {
                int i = c.A[0];
                if ((proto.RefMask >> i & 1) != 0) continue;
                byte declared = (byte)((c.A[1] & 0x7F) + 1 | c.A[1] & DeclaredKind.UnitlessBit);
                if (!DeclaredKind.IsNonNull(declared)) continue;
                byte kind = KindOfDeclared(declared);
                // (a number without a unit, or in one fixed unit: the prologue converts into it - CheckUnit - before its check, the caller does that
                // then; an open unit - `: any`, `: any m` - stays a Value)
                int unit = 0;
                if (kind is KindInt or KindFloat)
                {
                    if (c.A[3] == 0xFFFF) continue;
                    string demand = f.Chunk.Constants[c.A[3]].AsString();
                    if (demand != "unitless")
                    {
                        if (demand.TrimStart('#').StartsWith("any", StringComparison.Ordinal) || (unit = DemandedUnitId(demand)) <= 0) continue;
                        demands[i] = demand;
                    }
                }
                int type = kind switch
                {
                    KindInt when !DeclaredKind.KeepsWidth(declared) => TypeOf(KindInt, unit) | NarrowBit,
                    KindFloat => TypeOf(KindFloat, unit),
                    KindWord => TypeOf(KindWord, 0),
                    _ => 0,
                };
                types[i] = type;
                whats[i] = f.Chunk.Constants[c.A[2]].AsString();
            }
            return new CAbi { ParamType = types, ParamWhat = whats, ParamDemand = demands };
        }

        /// <summary>After a round's code of the method: the C return value it can have - every `return` is checked (`defined`, the chunk ends with
        /// LoadConst undefined, GuardTop, Return) and gives a scalar of one static type (ReturnType).</summary>
        private void NoteCReturn(Func f)
        {
            if (!_cAbiParams.TryGetValue(f, out var abi) || abi == null) return;
            int ret = 0;
            var code = f.Decoded;
            if (code.Count >= 3 && code[^1].Op == OpCode.Return && code[^2].Op == OpCode.GuardTop && DeclaredKind.IsNonNull((byte)(code[^2].A[0] + 1))
                && code[^3].Op == OpCode.LoadConst && f.Chunk.Constants[code[^3].A[0]].Kind == ValueKind.Undefined)
            {
                int rt = ReturnType(f);
                byte kind = KindOfType(rt);
                if (IsNarrowIntType(rt) || kind is KindFloat or KindBool or KindWord) ret = kind is KindBool or KindWord ? TypeOf(kind, 0) : rt & ~(CutBit | NullableBit | TypeNone);
            }
            if ((_cRet.TryGetValue(f, out int old) ? old : 0) == ret) return;
            if (ret == 0) _cRet.Remove(f); else _cRet[f] = ret;
            _version++;
        }

        private static string CName(Func f) => f.Name + "_c";

        /// <summary>The C type of a static scalar type.</summary>
        private static string CTypeOf(int type) => ScalarCType(KindOfType(type));

        /// <summary>The C parameter `Pi` keeps its name when it is a variable that is a C number (ScalarVarOf); otherwise the body makes a Value `Pi` of `Pi_c`.</summary>
        private string CParamName(Func f, int i) => ScalarVarOf(f, $"P{i}") ? $"P{i}" : $"P{i}_c";

        private string CSignature(Func f, CAbi abi)
        {
            var ps = new List<string>();
            if (f.HasSelf) ps.Add("Value self");
            for (int i = 0; i < f.ParamCount; i++)
                ps.Add(abi.ParamType[i] != 0 ? $"{CTypeOf(abi.ParamType[i])} {CParamName(f, i)}" : $"Value P{i}");
            return $"static {(abi.RetType != 0 ? CTypeOf(abi.RetType) : "Value")} {CName(f)}({(ps.Count == 0 ? "void" : string.Join(", ", ps))})";
        }

        /// <summary>What a function returns when it is left by an exception (the caller looks at the unwinding flag).</summary>
        private static string UndefReturnOf(CAbi? abi) => abi is not { RetType: not 0 } ? "Undef()" : KindOfType(abi.RetType) switch
        {
            KindBool => "false", KindFloat => "(Real)0", KindWord => "(FireWord)0", _ => "(FireInt)0",
        };

        /// <summary>The DeclaredKind a C parameter of this static type is checked against (`defined`, without a unit).</summary>
        private static byte CDeclared(int type)
        {
            byte kind = KindOfType(type);
            return (byte)((kind == KindInt ? (int)TypeTag.Int : kind == KindFloat ? (int)TypeTag.Float : (int)TypeTag.Word) + 1 | DeclaredKind.NonNullBit
                | (kind == KindWord || UnitOfType(type) != 0 ? 0 : DeclaredKind.UnitlessBit));   // (one with a unit was converted into it before: AdaptUnitCall)
        }

        /// <summary>The C number of a checked Value.</summary>
        private static string CNumberOf(int type, string value) => KindOfType(type) switch { KindFloat => $"{value}.f", KindWord => $"{value}.w", _ => $"(FireInt){value}.i" };

        /// <summary>The method a CallStaticMethod calls when it has a C body (and the call can use it: no named arguments, every argument given, no
        /// overloading by type, no reflection helper); null otherwise.</summary>
        private Func? CAbiStaticCallee(Func f, Instr ins)
        {
            if (!ScalarsOn) return null;
            string cls = f.Chunk.Constants[ins.A[0]].AsString(), method = f.Chunk.Constants[ins.A[1]].AsString();
            int argc = ins.A[2];
            if (ReflectionPrelude.HelperClasses.Contains(cls)) return null;
            int at = f.IndexOf.TryGetValue(ins.Addr, out int index) ? index : -1;
            if (at > 0 && f.Decoded[at - 1].Op is OpCode.ArgNames or OpCode.CopyArgs or OpCode.SetTypeArgs or OpCode.OverloadHint) return null;
            RuntimeClass rc;
            try { rc = FindClass(cls); } catch (NativeNotSupportedException) { return null; }
            if (rc.IsTypeOverloaded(method, argc) || rc.FindMethodWithAccess(method, argc).Proto is not { IsStatic: true } proto || proto.ParamCount != argc) return null;
            var target = GetFunc(proto, FuncKind.Static);
            return CAbiOf(target) != null ? target : null;
        }

        /// <summary>Like CAbiStaticCallee for a CallMethod on an object of a known class whose method no derived class overrides (DirectMethod).</summary>
        private Func? CAbiMethodCallee(Func f, Flow st, Instr ins)
        {
            if (!ScalarsOn) return null;
            int argc = ins.A[1], slot = st.Depth - argc - 1;
            if (slot < 0 || slot >= 64) return null;
            string method = f.Chunk.Constants[ins.A[0]].AsString();
            int at = f.IndexOf.TryGetValue(ins.Addr, out int index) ? index : -1;
            if (at > 0 && f.Decoded[at - 1].Op is OpCode.ArgNames or OpCode.CopyArgs or OpCode.SetTypeArgs or OpCode.OverloadHint) return null;
            int type = st.Types[slot];
            if (BaseKindOfType(type) != KindObj || (type & TypeNone) != 0 || InterfaceOfType(type) != null) return null;
            if (_usesReflection && ReflectionMethodNames.Contains(method)) return null;
            if (DirectMethod(ObjClass(type), method, argc, f) is not { ParamCount: var count } proto || count != argc) return null;
            var target = GetFunc(proto, FuncKind.Method);
            return CAbiOf(target) != null ? target : null;
        }

        /// <summary>The function with Values (`fN`) in front of the C body: the parameters are checked like the prologue would (a failed check returns,
        /// the caller sees the exception), the C result becomes a Value again. `name`: `fN`, or `fN_b` when a wrapper counts the call depth around it.</summary>
        private string CAbiWrapper(Func f, CAbi abi, string name)
        {
            var body = new System.Text.StringBuilder();
            body.AppendLine(name == f.Name ? f.Signature : f.SignatureOf(name, ""));
            body.AppendLine("{");
            var args = new List<string>();
            if (f.HasSelf) args.Add("self");
            for (int i = 0; i < f.ParamCount; i++)
            {
                int type = abi.ParamType[i];
                if (type == 0) { args.Add($"P{i}"); continue; }
                if (abi.ParamDemand[i] is { } demand)
                {
                    // (the CheckUnit of the prologue first: into the unit of the parameter)
                    body.AppendLine($"    P{i} = {AdaptUnitCall($"P{i}", demand)};");
                    if (_usesExceptions) body.AppendLine($"    if (FIRE_UNLIKELY(g_unwind.active)) return Undef();");
                }
                body.AppendLine($"    P{i} = {GuardKindCall($"P{i}", CDeclared(type), abi.ParamWhat[i])};");
                if (_usesExceptions) body.AppendLine($"    if (FIRE_UNLIKELY(g_unwind.active)) return Undef();");
                args.Add(CNumberOf(type, $"P{i}"));
            }
            string call = $"{CName(f)}({string.Join(", ", args)})";
            body.AppendLine(abi.RetType != 0 ? $"    return {ScalarValueOf(KindOfType(abi.RetType), UnitOfType(abi.RetType), call)};" : $"    return {call};");
            body.AppendLine("}");
            return body.ToString();
        }
    }
}
