using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using fire.Ast;
using fire.Runtime;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>
    /// A linked program as text (`forge dump`): everything a program file holds (ProgramFile), in the order it holds it, one thing per line - made to be compared.
    /// Two compilers that mean the same program give the same text (docs/ROADMAP_C_PORT.md: the C# compiler against the one in C), and a program that was
    /// written and read back gives the text it gave before. A function or a chunk the program holds twice is printed once and then named by its number.
    /// </summary>
    public static class ProgramDump
    {
        public static string Text(LinkedProgram program, bool lineTables = true)
        {
            var w = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            new Dumper(w, lineTables).Program(program);
            return w.ToString();
        }

        private sealed class Dumper
        {
            private readonly TextWriter _w;
            private readonly bool _lines;
            private readonly Dictionary<object, int> _protos = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<object, int> _chunks = new(ReferenceEqualityComparer.Instance);

            public Dumper(TextWriter w, bool lines) { _w = w; _lines = lines; }

            private void Line(int indent, string text) { _w.Write(new string(' ', indent * 2)); _w.Write(text); _w.Write('\n'); }

            private static string Q(string? s)
            {
                if (s == null) return "null";
                var b = new StringBuilder("\"");
                foreach (char c in s)
                {
                    if (c == '"' || c == '\\') b.Append('\\').Append(c);
                    else if (c == '\n') b.Append("\\n");
                    else if (c == '\r') b.Append("\\r");
                    else if (c == '\t') b.Append("\\t");
                    else if (c < 0x20 || c > 0x7E) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else b.Append(c);
                }
                return b.Append('"').ToString();
            }

            private static string L(IEnumerable<string?>? list) => list == null ? "null" : "[" + string.Join(", ", list.Select(Q)) + "]";
            private static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture) + (BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(-0.0) ? " (-0)" : "");

            private static string UnitText(Unit? u)
            {
                if (u == null) return "none";
                return "{" + string.Join(" ", u.Dimensions.Select(d => d.Key + "^" + d.Value.ToString(CultureInfo.InvariantCulture))) + "} scale=" + F(u.Scale) + " symbol=" + Q(u.DisplaySymbol)
                    + " offset=" + F(u.Offset) + " converter=" + Q(u.Converter) + " unitless=" + Q(u.UnitlessConverter);
            }

            private static string ConstantText(Value v) => v.Kind switch
            {
                ValueKind.Undefined => "undefined unit=" + UnitText(v.Unit),
                ValueKind.Bool => v.AsBool() ? "true" : "false",
                ValueKind.Int => "int " + v.AsInt().ToString(CultureInfo.InvariantCulture) + " width=" + (int)v.Width + " wide=" + v.Wide
                    + (v.Enum is { } e ? " enum=" + Q(e.Name) + (e.IsFlags ? " flags" : "") + " {" + string.Join(", ", e.Members.Select(m => m.Name + "=" + m.Value.ToString(CultureInfo.InvariantCulture))) + "}" : " unit=" + UnitText(v.Unit)),
                ValueKind.Float => "float " + F(v.AsFloat()) + " width=" + (int)v.Width + " unit=" + UnitText(v.Unit),
                ValueKind.Char => "char " + Q(v.AsChar().ToString()),
                ValueKind.String => "string " + Q(v.AsString()),
                ValueKind.Word => "word 0x" + v.AsWord().ToString("x", CultureInfo.InvariantCulture),
                _ => throw new NotSupportedException($"A value of type {v.Kind} cannot be part of a program."),
            };

            public void Program(LinkedProgram linked)
            {
                var p = linked.Program;
                Line(0, $"program mode={linked.ExecutionMode} float={linked.FloatWidth} int={linked.IntWidth} word={linked.WordWidth} firstUserSource={linked.FirstUserSource}");
                Line(0, "nativeImports " + L(linked.NativeImports));
                Line(0, "packageNatives " + L(linked.PackageNatives));
                Line(0, "packageNativeLibraries " + L(linked.PackageNativeLibraries));
                Line(0, "graphicsNatives " + L(linked.GraphicsNatives));
                foreach (var u in p.CustomUnits)
                    Line(0, $"unit {Q(u.Name)} symbol={Q(u.Symbol)} prefixed={u.Prefixed} {{{string.Join(" ", u.Dimensions.Select(d => d.Key + "^" + d.Value.ToString(CultureInfo.InvariantCulture)))}}} scale={F(u.Scale)} offset={F(u.Offset)} converter={Q(u.Converter)} unitless={Q(u.UnitlessConverter)}");
                foreach (var e in p.EnumDecls)
                    Line(0, $"enum {Q(e.Name)}{(e.Flags ? " flags" : "")} {{{string.Join(", ", e.Members.Zip(e.Values).Select(m => m.First + "=" + m.Second.ToString(CultureInfo.InvariantCulture)))}}}");
                foreach (var r in p.Resources)
                    Line(0, $"resource {Q(r.Name)} bytes={r.Data.Length} sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(r.Data)).ToLowerInvariant()}");
                foreach (var (name, sig) in p.ExternSignatures)
                {
                    Line(0, $"extern {Q(name)} lib={Q(sig.LibName)} display={Q(sig.DisplayName)} returns={TypeText(sig.ReturnType)} params=[{string.Join(", ", sig.ParamTypes.Select(TypeText))}] names={L(sig.ParamNames)}");
                    if (sig.InlineC != null) Line(1, $"inline source={sig.InlineSource} line={sig.InlineLine} c={Q(sig.InlineC)}");
                }
                var printed = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var (name, rc) in p.Classes)
                {
                    if (!printed.Add(rc)) { Line(0, $"class {Q(name)} = class {Q(rc.Name)} (above)"); continue; }
                    Class(name, rc);
                }
                Line(0, "top");
                ChunkRef(1, p.TopLevel);
            }

            private static string TypeText(TypeRef? t)
            {
                if (t == null) return "none";
                var b = new StringBuilder(Q(t.BaseName));
                if (t.BitWidth is { } bits) b.Append(" bits=").Append(bits);
                if (t.PointerDepth != 0) b.Append(" pointer=").Append(t.PointerDepth);
                if (t.LambdaSignature is { } s) b.Append($" lambda(returns={Q(s.ReturnTypeName)} params={L(s.ParamTypeNames)} selector={s.IsSelector} kind={Q(s.SelectorKind)} expression={s.IsExpression})");
                if (t.Namespaces != null) b.Append(" namespaces=").Append(L(t.Namespaces));
                if (t.Unit != null) b.Append(" unit=").Append(Q(t.Unit));
                if (t.ArrayRank != 0) b.Append(" rank=").Append(t.ArrayRank);
                if (t.TypeArgCount != 0) b.Append(" typeArgCount=").Append(t.TypeArgCount);
                if (t.TypeArgs != null) b.Append(" typeArgs=<").Append(string.Join(", ", t.TypeArgs.Select(TypeText))).Append('>');
                if (t.Unsigned) b.Append(" unsigned");
                if (t.NonNull) b.Append(" defined");
                return "(" + b + ")";
            }

            private void Attributes(int indent, List<AttributeMeta> list)
            {
                foreach (var a in list) Line(indent, $"attribute {Q(a.Name)} args={L(a.ArgTexts)} json={Q(a.ArgsJson)}");
            }

            private void Class(string key, RuntimeClass rc)
            {
                var flags = new List<string>();
                if (rc.IsActor) flags.Add("actor");
                if (rc.IsRecord) flags.Add("record");
                if (rc.IsReflectionHelper) flags.Add("reflectionHelper");
                if (rc.Keep) flags.Add("keep");
                if (rc.NotSerializable) flags.Add("notSerializable");
                if (rc.NoCopy) flags.Add("noCopy");
                Line(0, $"class {Q(key)}{(key != rc.Name ? " name=" + Q(rc.Name) : "")} base={Q(rc.Base?.Name)} flags=[{string.Join(" ", flags)}]");
                Line(1, "interfaces " + L(rc.Interfaces));
                Line(1, "baseTypeArgs " + L(rc.BaseTypeArgs));
                Line(1, "keptMembers " + L(rc.KeptMembers));
                Line(1, "readonlyStatics " + L(rc.ReadonlyStatics));
                if (rc.Meta is { } meta)
                {
                    Line(1, $"meta bases={L(meta.BaseNames)} declaredInterfaces={L(meta.DeclaredInterfaces)}");
                    Attributes(2, meta.Attributes);
                    foreach (var m in meta.Members)
                    {
                        Line(2, $"member {Q(m.Name)} kind={Q(m.Kind)} type={Q(m.TypeName)} access={Q(m.Access)} static={m.IsStatic} readonly={m.IsReadonly} read={m.CanRead} write={m.CanWrite} unit={Q(m.Unit)} params={L(m.ParamNames)} types={L(m.ParamTypes)}");
                        Attributes(3, m.Attributes);
                    }
                }
                foreach (var (name, f) in rc.OwnFieldInfo)
                {
                    Line(1, $"fieldInfo {Q(name)} access={f.AccessModifier} static={f.IsStatic} type={Q(f.TypeText)} unit={Q(f.RequiredUnit)} kind={f.RequiredKind} requiredType={Q(f.RequiredType)} array={f.RequiredArray} arrayType={Q(f.RequiredArrayType)} arrayOfParam={Q(f.ArrayOfParam)} derivedDefined={f.DerivedDefined}");
                    if (f.Attributes != null) { Line(2, "attributes"); Attributes(3, f.Attributes); }
                }
                foreach (var (name, v) in rc.StaticFieldValues) Line(1, $"staticValue {Q(name)} = {ConstantText(v)}");
                foreach (var (name, init) in rc.Fields) { Line(1, $"field {Q(name)}"); ProtoRef(2, init); }
                foreach (var (name, init) in rc.StaticFields) { Line(1, $"staticField {Q(name)}"); ProtoRef(2, init); }
                foreach (var (name, overloads) in rc.Methods)
                    foreach (var m in overloads) { Line(1, $"method {Q(name)}"); ProtoRef(2, m); }
                foreach (var (arity, ctor) in rc.Constructors) { Line(1, $"constructor {arity}"); ProtoRef(2, ctor); }
                foreach (var ctor in rc.ExtraConstructors) { Line(1, "constructor extra"); ProtoRef(2, ctor); }
                if (rc.Destructor != null) { Line(1, "destructor"); ProtoRef(2, rc.Destructor); }
                if (rc.ThisReads != null)
                    foreach (var (name, reads) in rc.ThisReads) Line(1, $"thisReads {Q(name)} {L(reads)}");
            }

            private void ProtoRef(int indent, FunctionProto? proto)
            {
                if (proto == null) { Line(indent, "function none"); return; }
                if (_protos.TryGetValue(proto, out int id)) { Line(indent, $"function #{id} (above)"); return; }
                id = _protos.Count;
                _protos[proto] = id;
                Line(indent, $"function #{id} params={proto.ParamCount} access={(proto.Access is { } a ? a.ToString() : "none")} static={proto.IsStatic} readsOnly={proto.ReadsOnly} refMask=0x{proto.RefMask:x} captureCopy=0x{proto.CaptureCopyMask:x} captureMoved=0x{proto.CaptureMovedMask:x} typeParams={proto.TypeParamCount}");
                Line(indent + 1, $"paramNames={L(proto.ParamNames)} paramTypes={L(proto.ParamTypes)} selectorPath={L(proto.SelectorPath)} fieldGetterOf={Q(proto.FieldGetterOf)}");
                if (proto.SpecOwner != null || proto.SpecArgs != null || proto.SpecDefinedMask != 0 || proto.SpecParamKinds != null)
                    Line(indent + 1, $"spec owner={Q(proto.SpecOwner)} args={L(proto.SpecArgs)} definedMask=0x{proto.SpecDefinedMask:x} paramKinds={(proto.SpecParamKinds == null ? "null" : Convert.ToHexString(proto.SpecParamKinds).ToLowerInvariant())}");
                for (int i = 0; i < proto.ParamDefaults.Count; i++)
                    if (proto.ParamDefaults[i] is { } d) { Line(indent + 1, $"default {i}"); ProtoRef(indent + 2, d); }
                    else Line(indent + 1, $"default {i} none");
                ChunkRef(indent + 1, proto.Chunk);
                if (proto.Specializations != null)
                    for (int i = 0; i < proto.Specializations.Length; i++) { Line(indent + 1, $"specialization {i}"); ProtoRef(indent + 2, proto.Specializations[i]); }
            }

            private void ChunkRef(int indent, Chunk chunk)
            {
                if (_chunks.TryGetValue(chunk, out int id)) { Line(indent, $"chunk @{id} (above)"); return; }
                id = _chunks.Count;
                _chunks[chunk] = id;
                Line(indent, $"chunk @{id} code={chunk.Code.Count}");
                for (int i = 0; i < chunk.Constants.Count; i++) Line(indent + 1, $"const {i} {ConstantText(chunk.Constants[i])}");
                for (int i = 0; i < chunk.Units.Count; i++) Line(indent + 1, $"unit {i} {UnitText(chunk.Units[i])}");
                if (chunk.ReceiverTypeArgs != null)
                    foreach (var (offset, text) in chunk.ReceiverTypeArgs) Line(indent + 1, $"receiverTypeArgs {offset} {Q(text)}");
                for (int i = 0; i < chunk.Handlers.Count; i++)
                    Line(indent + 1, $"handler {i} catches=[{string.Join(", ", chunk.Handlers[i].Catches.Select(c => Q(c.TypeName) + "@" + c.CatchAddr))}] finally={(chunk.Handlers[i].FinallyAddr is { } f ? f.ToString(CultureInfo.InvariantCulture) : "none")}");
                foreach (var ((depth, slot), name) in chunk.DebugLocalNames) Line(indent + 1, $"name {depth} {slot} {Q(name)}");
                if (_lines)
                    foreach (var (offset, source, line) in chunk.LineTable) Line(indent + 1, $"line {offset} source={source} line={line}");
                Code(indent + 1, chunk);
                for (int i = 0; i < chunk.Functions.Count; i++) { Line(indent + 1, $"proto {i}"); ProtoRef(indent + 2, chunk.Functions[i]); }
            }

            private void Code(int indent, Chunk chunk)
            {
                var code = chunk.CodeArray;
                int ip = 0;
                while (ip < code.Length)
                {
                    var op = (OpCode)code[ip];
                    int at = ip++;
                    if (!OperandSizes.Table.TryGetValue(op, out var sizes))
                    {
                        // (an opcode without a row in the table: the rest as bytes, so that nothing is lost)
                        Line(indent, $"{at:d5} ?{code[at]} " + Convert.ToHexString(code, ip, code.Length - ip).ToLowerInvariant());
                        return;
                    }
                    var b = new StringBuilder();
                    b.Append(at.ToString("d5", CultureInfo.InvariantCulture)).Append(' ').Append(op);
                    var operands = new int[sizes.Length];
                    for (int i = 0; i < sizes.Length && ip + sizes[i] <= code.Length; i++)
                    {
                        // `catch threads()` without a type has no type operand (OpInfo.Decode)
                        if (op == OpCode.RegisterThreadsCatch && i == 2 && operands[1] == 0) break;
                        operands[i] = sizes[i] == 1 ? code[ip] : code[ip] | (code[ip + 1] << 8);
                        ip += sizes[i];
                        b.Append(' ').Append(operands[i].ToString(CultureInfo.InvariantCulture));
                    }
                    Line(indent, b.ToString());
                }
            }
        }
    }
}
