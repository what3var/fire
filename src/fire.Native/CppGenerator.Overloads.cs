using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using fire.Ast;
using fire.Bytecode;

namespace fire.Native
{
    // Overloading by parameter type (SPEC 5.4.3, fire.Bytecode.Overloads): where the values decide which method of a name runs, the generated code
    // scores the arguments like the VM - per candidate the sum of the scores of its parameters, the highest wins, a tie goes to the earlier candidate,
    // and when nothing fits the first candidate runs (its checks report the mismatch). A call with written type arguments (`M<string>(x)`) leaves
    // their number in g_explicitTArgs: only generic candidates with that many type parameters count then.
    public sealed partial class CppGenerator
    {
        /// <summary>A selector function for a call whose target is known up to the overload (a static method, a base method, a constructor):
        /// `Value ovs_N(Value self, Value a0, ..., OwnList* list)`.</summary>
        private sealed record OverloadSelector(string Name, RuntimeClass Rc, FunctionProto[] Candidates, RuntimeClass[] Declaring, int Argc, FuncKind Kind, string[] Denied, uint Addresses, string Method, OverloadHint? Hint);

        private readonly Dictionary<string, OverloadSelector> _overloadSelectors = new();
        /// <summary>Classes named by a parameter of an overload: each gets `ovd_Name(v)`, the score of an object for it (closer classes score higher).</summary>
        private readonly SortedSet<string> _ovClassNames = new(StringComparer.Ordinal);

        /// <summary>The implementations a call of `name` with `argc` arguments on an object of `rc` can reach: all candidates when the types of the
        /// arguments decide, otherwise the one method.</summary>
        private IReadOnlyList<FunctionProto> MethodsFor(RuntimeClass rc, string name, int argc)
        {
            if (rc.IsTypeOverloaded(name, argc)) return rc.Candidates(name, argc).Protos;
            return rc.FindMethodWithAccess(name, argc).Proto is { } proto ? new[] { proto } : Array.Empty<FunctionProto>();
        }

        /// <summary>The selector for a static or base method call (`kind` Static/Method) or a constructor (Ctor) of `rc`; `denied` holds, per
        /// candidate, the message of the AccessDeniedException when the calling code may not use it (null: allowed);
        /// `addresses`: bit i - the call site passes the address of a variable as argument i (for a `ref` parameter of some candidate).</summary>
        private OverloadSelector OverloadSelectorFor(RuntimeClass rc, string name, int argc, FuncKind kind, System.Func<RuntimeClass, AccessModifier, string?> denied, uint addresses, OverloadHint? hint = null)
        {
            FunctionProto[] protos;
            RuntimeClass[] declaring;
            if (kind == FuncKind.Ctor)
            {
                protos = rc.ConstructorCandidates(argc).ToArray();
                declaring = protos.Select(_ => rc).ToArray();
            }
            else
            {
                var set = rc.Candidates(name, argc);
                protos = set.Protos;
                declaring = set.Declaring;
            }
            var deniedMessages = protos.Select((p, i) => denied(declaring[i], p.Access ?? AccessModifier.Public) ?? "").ToArray();
            // a hint whose overload is found and needs no address: the selector tries it first
            int hinted = hint != null && addresses == 0 && HintTarget(hint, rc) is { } target ? Array.IndexOf(protos, target) : -1;
            if (hinted < 0) hint = null;
            string key = $"{rc.Name}|{(kind == FuncKind.Ctor ? "#new" : name)}|{argc}|{kind}|{addresses}|{string.Join("|", deniedMessages)}|{(hint != null ? string.Join(",", hint.Assumptions) + "@" + hinted : "")}";
            if (_overloadSelectors.TryGetValue(key, out var selector)) return selector;
            foreach (var proto in protos)
            {
                if (kind == FuncKind.Static && !proto.IsStatic) continue;   // (an instance method a static call picks is an error at run time, like in the VM)
                GetFunc(proto, kind);
                DefaultArgs(proto, argc, "self", "list");   // (registers the functions of the default values)
            }
            selector = new OverloadSelector($"ovs_{_overloadSelectors.Count}", rc, protos, declaring, argc, kind, deniedMessages, addresses, name, hint);
            _overloadSelectors[key] = selector;
            return selector;
        }

        /// <summary>The static type of what a selector returns (where all its candidates meet).</summary>
        private int SelectorReturnType(OverloadSelector selector)
        {
            if (selector.Kind == FuncKind.Ctor) return TypeUnknown;
            int? result = null;
            foreach (var proto in selector.Candidates)
            {
                if (selector.Kind == FuncKind.Static && !proto.IsStatic) continue;
                if (!_funcByProto.TryGetValue(proto, out var target)) return TypeUnknown;
                int type = ReturnType(target);
                result = result is { } r ? JoinTypes(r, type) : type;
            }
            return result ?? TypeUnknown;
        }

        private static string SelectorSignature(OverloadSelector selector) =>
            $"[[maybe_unused]] static Value {selector.Name}(Value self{string.Concat(Enumerable.Range(0, selector.Argc).Select(i => $", Value a{i}"))}, OwnList* list)";

        private string SelectorFunction(OverloadSelector selector)
        {
            var sb = new StringBuilder();
            sb.AppendLine(SelectorSignature(selector));
            sb.AppendLine("{");
            sb.AppendLine("    (void)self; (void)list;");
            string ownerObj = selector.Kind == FuncKind.Static ? "nullptr" : "(self.kind == K_Class ? objAt(self) : nullptr)";
            // an argument that carries an address: its value is scored, and a candidate without `ref` there gets the value
            bool IsAddress(int i) => (selector.Addresses >> i & 1) != 0;
            for (int i = 0; i < selector.Argc; i++)
                if (IsAddress(i)) sb.AppendLine($"    Value v{i} = derefArg(a{i});");
            System.Func<int, string> callFor = c =>
            {
                var proto = selector.Candidates[c];
                if (selector.Kind == FuncKind.Static && !proto.IsStatic)
                    return $"{{ fatal({CString($"'{selector.Method}' on '{selector.Rc.Name}' is not a static method - 'ClassName.{selector.Method}(...)' can only be used to call 'static' methods.")}); return Undef(); }}";
                if (selector.Denied[c].Length > 0) return $"return accessDenied({CString(selector.Denied[c])});";
                var (pre, defaults) = DefaultArgs(proto, selector.Argc, "self", "list");
                string args = string.Concat(Enumerable.Range(0, selector.Argc).Select(i => IsAddress(i) && (proto.RefMask >> i & 1) == 0 ? $", v{i}" : $", a{i}"));
                string target = _funcByProto[proto].Name;
                return selector.Kind switch
                {
                    FuncKind.Ctor => $"{{ {pre}{target}(self{args}{defaults}); return Undef(); }}",
                    FuncKind.Static => $"{{ {pre}return {target}({(args + defaults).TrimStart(',', ' ')}); }}",
                    _ => $"{{ {pre}return {target}(self{args}{defaults}); }}",
                };
            };
            if (selector.Hint is { } hint && HintTarget(hint, selector.Rc) is { } hintedProto)
            {
                // the overload the type checker chose: taken when the arguments are what it assumed (no scoring)
                int c = Array.IndexOf(selector.Candidates, hintedProto);
                sb.AppendLine($"    if ({HintHolds(hint, i => $"a{i}")}) {{ g_explicitTArgs = 0; {ClearCallTArgs(hintedProto)}{callFor(c)} }}");
            }
            sb.Append(OverloadChoice(selector.Candidates, selector.Argc, ownerObj, callFor, "    ", i => IsAddress(i) ? $"v{i}" : $"a{i}"));
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>The statements that pick a candidate by the scores of the arguments `a0` ... and run `call(index)` (a statement that returns).</summary>
        private string OverloadChoice(IReadOnlyList<FunctionProto> candidates, int argc, string ownerObj, System.Func<int, string> call, string indent, System.Func<int, string>? argValue = null)
        {
            argValue ??= i => $"a{i}";
            var sb = new StringBuilder();
            sb.AppendLine($"{indent}int ovN = g_explicitTArgs; g_explicitTArgs = 0; (void)ovN;");
            sb.AppendLine($"{indent}int64_t ovBest = INT64_MIN; int ovWin = -1;");
            for (int c = 0; c < candidates.Count; c++)
            {
                var proto = candidates[c];
                string typeCondition = proto.TypeParamCount == 0 ? "ovN == 0" : $"ovN == 0 || ovN == {proto.TypeParamCount}";
                var line = new StringBuilder($"{indent}do {{ if (!({typeCondition})) break; int64_t t = 0; int s; (void)s;");
                for (int i = 0; i < argc && i < proto.ParamCount; i++)
                    line.Append($" s = {OverloadScoreExpr(argValue(i), Overloads.TypeAt(proto, i), ownerObj, proto.TypeParamCount > 0)}; if (s < 0) break; t += s;");
                line.Append($" if (t > ovBest) {{ ovBest = t; ovWin = {c}; }} }} while (0);");
                sb.AppendLine(line.ToString());
            }
            sb.AppendLine($"{indent}switch (ovWin) {{");
            for (int c = 1; c < candidates.Count; c++)
                sb.AppendLine($"{indent}    case {c}: {ClearCallTArgs(candidates[c])}{call(c)}");
            sb.AppendLine($"{indent}    default: {ClearCallTArgs(candidates[0])}{call(0)}");
            sb.AppendLine($"{indent}}}");
            return sb.ToString();
        }

        /// <summary>A method that is not generic takes no type arguments: the ones a call named are dropped (like the VM).</summary>
        private static string ClearCallTArgs(FunctionProto proto) => proto.TypeParamCount == 0 ? "g_callTArgs = nullptr; " : "";

        /// <summary>The OverloadHint in front of a call instruction (before its SetTypeArgs and CopyArgs), or null.</summary>
        private static OverloadHint? HintBefore(Func f, Instr ins)
        {
            int i = f.IndexOf[ins.Addr] - 1;
            if (i >= 0 && f.Decoded[i].Op == OpCode.CopyArgs) i--;
            if (i >= 0 && f.Decoded[i].Op == OpCode.SetTypeArgs) i--;
            return i >= 0 && f.Decoded[i] is { Op: OpCode.OverloadHint } hint ? OverloadHint.Of(f.Chunk.Constants[hint.A[0]].AsString()) : null;
        }

        /// <summary>The overload a hint means on an object of `rc` (or the constructor of `rc`), null when it cannot be found.</summary>
        private FunctionProto? HintTarget(OverloadHint hint, RuntimeClass rc) =>
            hint.Resolve(rc, n => _program.Program.Classes.TryGetValue(n, out var c) ? c : null).Proto;

        /// <summary>The C++ test that the arguments are what the hint assumed (OverloadHint.Holds).</summary>
        private string HintHolds(OverloadHint hint, System.Func<int, string> arg)
        {
            var tests = new List<string>();
            for (int i = 0; i < hint.Assumptions.Length; i++)
            {
                string a = hint.Assumptions[i], v = arg(i);
                string? test = a switch
                {
                    "" => null,
                    "u" => $"{v}.kind == K_Undefined",
                    "k:int" => $"{v}.kind == K_Int",
                    "k:float" => $"{v}.kind == K_Float",
                    "k:bool" => $"{v}.kind == K_Bool",
                    "k:char" => $"{v}.kind == K_Char",
                    "k:string" => $"{v}.kind == K_String",
                    "k:lambda" => $"{v}.kind == K_Lambda",
                    "k:array" => $"({v}.kind == K_Array || {v}.kind == K_Buffer)",
                    "k:class" => $"{v}.kind == K_Class",
                    _ => ClassHolds(v, a.Substring(2), a[0] == 'i'),
                };
                if (test != null) tests.Add(test);
            }
            return tests.Count == 0 ? "true" : string.Join(" && ", tests);
        }

        private string ClassHolds(string v, string name, bool isInterface)
        {
            _isOfTypes.Add(name);
            string enumerable = isInterface && GenericClassNames.PlainName(name) == "IEnumerable" ? $"{v}.kind == K_Array || {v}.kind == K_Buffer || {v}.kind == K_String || " : "";
            return $"({enumerable}({v}.kind == K_Class && isof_{Mangle(name)}({v})))";
        }

        /// <summary>The C++ expression of the score of the value `v` for a parameter with this descriptor (Overloads.Score).</summary>
        private string OverloadScoreExpr(string v, string? descriptor, string ownerObj, bool generic)
        {
            string orUndefined = $"{v}.kind == K_Undefined ? {Overloads.UndefinedScore} : {Overloads.NoMatch}";
            string Exact(string test) => $"({test} ? {Overloads.ExactKind} : {orUndefined})";
            if (string.IsNullOrEmpty(descriptor)) return $"({v}.kind == K_Undefined ? {Overloads.UndefinedScore} : {Overloads.AnyScore})";
            switch (descriptor)
            {
                case "k:int": return Exact($"{v}.kind == K_Int");
                case "k:float": return $"({v}.kind == K_Float ? {Overloads.ExactKind} : {v}.kind == K_Int ? {Overloads.IntToFloat} : {orUndefined})";
                case "k:bool": return Exact($"{v}.kind == K_Bool");
                case "k:char": return Exact($"{v}.kind == K_Char");
                case "k:string": return Exact($"{v}.kind == K_String");
                case "k:lambda": return Exact($"{v}.kind == K_Lambda");
                case "k:array": return Exact($"({v}.kind == K_Array || {v}.kind == K_Buffer)");
                case "k:class": return $"({v}.kind == K_Class ? {Overloads.ObjectScore} : {orUndefined})";
            }
            string name = descriptor.Substring(2);
            switch (descriptor[0])
            {
                case 'c':
                    _ovClassNames.Add(name);
                    return $"({v}.kind == K_Class ? ovd_{Mangle(name)}({v}) : {orUndefined})";
                case 'i':
                {
                    _isOfTypes.Add(name);
                    string enumerable = GenericClassNames.PlainName(name) == "IEnumerable" ? $"{v}.kind == K_Array || {v}.kind == K_Buffer || {v}.kind == K_String || " : "";
                    return $"({enumerable}isof_{Mangle(name)}({v}) ? {Overloads.InterfaceScore} : {orUndefined})";
                }
                case 'g':
                {
                    if (name.Length == 0) return $"({v}.kind == K_Undefined ? {Overloads.UndefinedScore} : {Overloads.GenericScore})";
                    var parsed = DeclaredType.Parse(name);
                    string fits = parsed.Alternatives.Length == 0 ? "true"
                        : string.Join(" || ", parsed.Alternatives.Select(alt => "(" + string.Join(" && ", alt.Select(cond => HoldsExpr("ovc", cond))) + ")"));
                    string? typeArg = null;
                    if (parsed.TypeParamIndex >= 0)
                    {
                        int k = parsed.TypeParamIndex;
                        typeArg = parsed.TypeParamClass!.Length > 0
                            ? (ownerObj != "nullptr" ? TArgOfExpr(parsed.TypeParamClass, k, ownerObj) : null)
                            : (generic ? $"(g_callTArgs ? static_cast<TArgs>(g_callTArgs)[{k}] : nullptr)" : null);
                    }
                    string byTypeArg = typeArg != null ? $"const TArg* tga = {typeArg}; if (tga) return fitsTArg(ovc, tga); " : "";
                    return $"({v}.kind == K_Undefined ? {Overloads.UndefinedScore} : [&]() {{ Value ovc = {v}; {byTypeArg}return {fits}; }}() ? {Overloads.GenericScore} : {Overloads.NoMatch})";
                }
            }
            return $"({v}.kind == K_Undefined ? {Overloads.UndefinedScore} : {Overloads.AnyScore})";
        }

        /// <summary>`ovd_Name(v)`: the score of an object for a parameter of class Name - ClassExact for an object of Name, one less per step to a derived class.</summary>
        private string OverloadClassFunctions()
        {
            var sb = new StringBuilder();
            foreach (var name in _ovClassNames)
            {
                sb.AppendLine($"[[maybe_unused]] static int ovd_{Mangle(name)}(Value v) {{");
                sb.AppendLine($"    if (!leafAlive(v)) return {Overloads.UndefinedScore};");
                var cases = new StringBuilder();
                foreach (var cls in _classList)
                {
                    int distance = 0;
                    for (var rc = cls.Rc; rc != null; rc = rc.Base, distance++)
                        if (rc.Name == name)
                        {
                            cases.Append($"case {cls.Id}: return {(Overloads.ClassExact - distance).ToString(CultureInfo.InvariantCulture)}; ");
                            break;
                        }
                }
                sb.AppendLine(cases.Length == 0 ? $"    return {Overloads.NoMatch};" : $"    switch (objAt(v)->cls) {{ {cases}default: return {Overloads.NoMatch}; }}");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }
    }
}
