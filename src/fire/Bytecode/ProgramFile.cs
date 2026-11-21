using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using fire.Ast;
using fire.Runtime;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>The file is not a program of this format, is damaged, or is of a version this reader does not know.</summary>
    public sealed class ProgramFileException : Exception
    {
        public ProgramFileException(string message) : base(message) { }
    }

    /// <summary>
    /// A linked program as bytes: the format `.fbc` (docs/spec/BYTECODE.md, "Das Dateiformat .fbc"). It is what a packed program carries in its payload, what a
    /// plugin for the VM carries behind its header, and the seam between the compiler and everything that runs or translates its bytecode
    /// (docs/ROADMAP_C_PORT.md, L2): written and read here in C#, read by the VM in C later.
    ///
    /// The format is plain data, little endian, with nothing of .NET in it: numbers as LEB128, names as indexes into one table of UTF-8 strings, the units and
    /// the enums of constants as tables. Everything is written in the order the compiler made it (dictionaries in the order of their entries), so the same
    /// program gives the same bytes. Objects that the program holds twice (a function of two classes, a class under two names) are written once and referred
    /// to: the program that is read back has the shape of the one that was written.
    /// </summary>
    public static class ProgramFile
    {
        /// <summary>The first bytes of a file: "FBC" and the SUB character (a text viewer stops there).</summary>
        public static ReadOnlySpan<byte> Magic => new byte[] { (byte)'F', (byte)'B', (byte)'C', 0x1A };

        /// <summary>The version of the format this code writes; a reader refuses any other.</summary>
        public const int Version = 1;

        /// <summary>Flag of the header: the chunks carry their line tables (what an error message, the debugger and the native backend's `#line` need).</summary>
        private const int FlagLines = 1;
        /// <summary>Flag of the header: behind the top level chunk the sources of the program - file and SHA-256 by source index (LinkedProgram.SourceFiles,
        /// SourceHashes), what the debugger of the VM in C says in its hello (docs/spec/NATIVE_DEBUG_PROTOCOL.md). Written for a program in the mode Debug with
        /// line tables.</summary>
        private const int FlagSources = 2;
        /// <summary>Flag of the header: behind the sources the setting of the stack traces (docs/spec/STACKTRACE.md: a byte, StackTraceMode) and the name of each source
        /// in a trace (LinkedProgram.TraceFiles, by source index; none: the prelude). Written when the program shows traces (not with `#stacktrace off`).</summary>
        private const int FlagTrace = 4;

        // the tags of a constant
        private const byte TagUndefined = 0, TagBool = 1, TagInt = 2, TagFloat = 3, TagChar = 4, TagString = 5, TagWord = 6;

        public static bool IsProgram(ReadOnlySpan<byte> bytes) => bytes.Length >= 6 && bytes.Slice(0, 4).SequenceEqual(Magic);

        /// <summary>The program as bytes. <paramref name="lineTables"/>: with the line tables of its chunks (without them the program runs the same, but has no lines to show).</summary>
        public static byte[] Write(LinkedProgram program, bool lineTables = true) => new Writer(lineTables).Run(program);

        /// <summary>The program of the bytes, ready to run (its classes linked to each other, its enums known).</summary>
        public static LinkedProgram Read(ReadOnlySpan<byte> bytes) => new Reader(bytes.ToArray()).Run();

        /// <summary>One unit as bytes (what a module's interface file keeps of the units it needs): the table of names, then the unit as in a program file.</summary>
        public static byte[] WriteUnit(Unit unit) => new Writer(false).RunUnit(unit);

        /// <summary>The unit of <see cref="WriteUnit"/>.</summary>
        public static Unit ReadUnit(ReadOnlySpan<byte> bytes) => new Reader(bytes.ToArray()).RunUnit();

        // ---------------------------------------------------------------------------------------------------------------------------------------------------

        private sealed class Writer
        {
            private readonly bool _lines;
            private MemoryStream _out = new();
            private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);
            private readonly List<string> _stringList = new();
            private readonly Dictionary<string, int> _units = new(StringComparer.Ordinal);
            private readonly List<Unit> _unitList = new();
            private readonly Dictionary<string, int> _enums = new(StringComparer.Ordinal);
            private readonly List<EnumType> _enumList = new();
            private readonly Dictionary<object, int> _protos = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<object, int> _chunks = new(ReferenceEqualityComparer.Instance);
            private readonly HashSet<object> _open = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<object, int> _classes = new(ReferenceEqualityComparer.Instance);
            private readonly List<RuntimeClass> _classList = new();

            public Writer(bool lines) => _lines = lines;

            public byte[] Run(LinkedProgram linked)
            {
                var p = linked.Program;
                // the classes: every object once, in the order of the program's table; a base class that is not in the table (it always is) follows the class that names it
                foreach (var rc in p.Classes.Values) AddClass(rc);

                U8((byte)linked.ExecutionMode);
                U8((byte)linked.FloatWidth);
                U8((byte)linked.IntWidth);
                U8((byte)linked.WordWidth);
                Int(linked.FirstUserSource);
                StrList(linked.NativeImports);
                OptStrList(linked.PackageNatives);
                OptStrList(linked.PackageNativeLibraries);
                OptStrList(linked.GraphicsNatives);

                Count(p.CustomUnits.Count);
                foreach (var u in p.CustomUnits)
                {
                    Str(u.Name); Str(u.Symbol); Bool(u.Prefixed);
                    Dimensions(u.Dimensions);
                    F64(u.Scale); F64(u.Offset); Str(u.Converter); Str(u.UnitlessConverter);
                }

                Count(p.EnumDecls.Count);
                foreach (var e in p.EnumDecls)
                {
                    if (e.Members.Count != e.Values.Count) throw new InvalidOperationException($"The enum '{e.Name}' has {e.Members.Count} names and {e.Values.Count} values.");
                    Str(e.Name); Bool(e.Flags);
                    Count(e.Members.Count);
                    for (int i = 0; i < e.Members.Count; i++) { Str(e.Members[i]); Int(e.Values[i]); }
                }

                Count(p.Resources.Count);
                foreach (var r in p.Resources) { Str(r.Name); Bytes(r.Data); }

                Count(p.ExternSignatures.Count);
                foreach (var (name, sig) in p.ExternSignatures) { Str(name); Extern(sig); }

                // (the list grows while it is written only if a base class was not in the table - AddClass above has taken them all)
                Count(_classList.Count);
                foreach (var rc in _classList) Class(rc);
                Count(p.Classes.Count);
                foreach (var (name, rc) in p.Classes) { Str(name); Count(_classes[rc]); }

                ChunkRef(p.TopLevel);
                bool sources = _lines && linked.ExecutionMode == VmExecutionMode.Debug && linked.SourceFiles != null;
                if (sources)
                {
                    var files = linked.SourceFiles!;
                    var hashes = linked.SourceHashes;
                    Count(files.Count);
                    for (int i = 0; i < files.Count; i++) { Str(files[i]); Str(hashes != null && i < hashes.Count ? hashes[i] : null); }
                }

                bool trace = linked.StackTrace != StackTraceMode.Off;
                if (trace)
                {
                    U8((byte)linked.StackTrace);
                    var names = linked.TraceFiles ?? Array.Empty<string?>();
                    Count(names.Count);
                    foreach (var name in names) Str(name);
                }

                // the tables are complete only now, and they stand before the program: the units and the enums first (they add names), then the names
                var body = _out;
                _out = new MemoryStream();
                Count(_unitList.Count);
                foreach (var u in _unitList) UnitBody(u);
                Count(_enumList.Count);
                foreach (var e in _enumList)
                {
                    Str(e.Name); Bool(e.IsFlags);
                    Count(e.Members.Count);
                    foreach (var (name, value) in e.Members) { Str(name); Int(value); }
                }
                var tables = _out;

                _out = new MemoryStream();
                _out.Write(Magic);
                U8(Version);
                U8((byte)((_lines ? FlagLines : 0) | (sources ? FlagSources : 0) | (trace ? FlagTrace : 0)));
                Count(_stringList.Count);
                foreach (var s in _stringList) Bytes(Encoding.UTF8.GetBytes(s));
                tables.WriteTo(_out);
                body.WriteTo(_out);
                return _out.ToArray();
            }

            public byte[] RunUnit(Unit u)
            {
                UnitBody(u);
                var body = _out;
                _out = new MemoryStream();
                Count(_stringList.Count);
                foreach (var s in _stringList) Bytes(Encoding.UTF8.GetBytes(s));
                body.WriteTo(_out);
                return _out.ToArray();
            }

            private void UnitBody(Unit u)
            {
                Dimensions(u.Dimensions);
                F64(u.Scale); Str(u.DisplaySymbol); F64(u.Offset); Str(u.Converter); Str(u.UnitlessConverter);
            }

            private void AddClass(RuntimeClass rc)
            {
                if (_classes.ContainsKey(rc)) return;
                _classes[rc] = _classList.Count;
                _classList.Add(rc);
                if (rc.Base != null) AddClass(rc.Base);
            }

            // ---- the pieces ----

            private void U8(int b) => _out.WriteByte((byte)b);
            private void Bool(bool b) => _out.WriteByte(b ? (byte)1 : (byte)0);

            private void UInt(ulong v)
            {
                while (v >= 0x80) { _out.WriteByte((byte)(v | 0x80)); v >>= 7; }
                _out.WriteByte((byte)v);
            }

            private void Count(int n)
            {
                if (n < 0) throw new InvalidOperationException("A negative count.");
                UInt((ulong)n);
            }

            /// <summary>A signed number (zigzag: small numbers of either sign are short).</summary>
            private void Int(long v) => UInt((ulong)((v << 1) ^ (v >> 63)));

            private void F64(double d)
            {
                Span<byte> b = stackalloc byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b, BitConverter.DoubleToInt64Bits(d));
                _out.Write(b);
            }

            private void Bytes(byte[] data) { Count(data.Length); _out.Write(data); }

            /// <summary>A name: 0 for none, else its place in the table of strings + 1.</summary>
            private void Str(string? s)
            {
                if (s == null) { UInt(0); return; }
                if (!_strings.TryGetValue(s, out int index))
                {
                    // (a name the table cannot hold as UTF-8 - half of a surrogate pair - would come back as another name)
                    if (Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s)) != s) throw new InvalidOperationException($"The name '{s}' is not valid Unicode text.");
                    index = _stringList.Count;
                    _strings[s] = index;
                    _stringList.Add(s);
                }
                UInt((ulong)index + 1);
            }

            private void StrList(IEnumerable<string?> list)
            {
                var items = list as IReadOnlyCollection<string?> ?? list.ToList();
                Count(items.Count);
                foreach (var s in items) Str(s);
            }

            /// <summary>A list that may be missing: 0 for none, else its length + 1.</summary>
            private void OptStrList(IEnumerable<string?>? list)
            {
                if (list == null) { UInt(0); return; }
                var items = list as IReadOnlyCollection<string?> ?? list.ToList();
                Count(items.Count + 1);
                foreach (var s in items) Str(s);
            }

            private void Dimensions(IReadOnlyDictionary<string, int> dims)
            {
                Count(dims.Count);
                foreach (var (symbol, exponent) in dims) { Str(symbol); Int(exponent); }
            }

            private void UnitRef(Unit? u)
            {
                if (u == null) { UInt(0); return; }
                var key = new StringBuilder();
                foreach (var (symbol, exponent) in u.Dimensions) key.Append(symbol).Append('^').Append(exponent).Append(';');
                key.Append('|').Append(BitConverter.DoubleToInt64Bits(u.Scale)).Append('|').Append(u.DisplaySymbol ?? "\0").Append('|').Append(BitConverter.DoubleToInt64Bits(u.Offset))
                    .Append('|').Append(u.Converter ?? "\0").Append('|').Append(u.UnitlessConverter ?? "\0");
                string k = key.ToString();
                if (!_units.TryGetValue(k, out int index))
                {
                    index = _unitList.Count;
                    _units[k] = index;
                    _unitList.Add(u);
                }
                UInt((ulong)index + 1);
            }

            private void EnumRef(EnumType? e)
            {
                if (e == null) { UInt(0); return; }
                if (!_enums.TryGetValue(e.Key, out int index))
                {
                    index = _enumList.Count;
                    _enums[e.Key] = index;
                    _enumList.Add(e);
                }
                UInt((ulong)index + 1);
            }

            private void Constant(Value v)
            {
                switch (v.Kind)
                {
                    case ValueKind.Undefined: U8(TagUndefined); UnitRef(v.Unit); break;
                    case ValueKind.Bool: U8(TagBool); Bool(v.AsBool()); break;
                    case ValueKind.Int:
                        U8(TagInt); Int(v.AsInt()); U8((byte)v.Width); U8(v.Wide);
                        UnitRef(v.Unit); EnumRef(v.Enum);
                        break;
                    case ValueKind.Float: U8(TagFloat); F64(v.AsFloat()); U8((byte)v.Width); UnitRef(v.Unit); break;
                    case ValueKind.Char: U8(TagChar); UInt(v.AsChar()); break;
                    case ValueKind.String:
                    {
                        // (the text of the program, exactly: UTF-16 code units, as the runtime holds a string)
                        string s = v.AsString();
                        U8(TagString); Count(s.Length);
                        foreach (char c in s) { _out.WriteByte((byte)c); _out.WriteByte((byte)(c >> 8)); }
                        break;
                    }
                    case ValueKind.Word:
                    {
                        U8(TagWord);
                        Span<byte> b = stackalloc byte[8];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(b, v.AsWord());
                        _out.Write(b);
                        break;
                    }
                    default:
                        throw new NotSupportedException($"A value of type {v.Kind} cannot be part of a program file - a constant is a number, a text, a char, a bool or undefined.");
                }
            }

            /// <summary>A function: 0 for none, 1 and the function itself where it stands first, else the number it got there + 2.</summary>
            private void ProtoRef(FunctionProto? proto)
            {
                if (proto == null) { UInt(0); return; }
                if (_protos.TryGetValue(proto, out int id))
                {
                    if (_open.Contains(proto)) throw new InvalidOperationException("A function of the program contains itself.");
                    UInt((ulong)id + 2);
                    return;
                }
                UInt(1);
                _protos[proto] = _protos.Count;
                _open.Add(proto);

                ChunkRef(proto.Chunk);
                Int(proto.ParamCount);
                Count(proto.ParamDefaults.Count);
                foreach (var d in proto.ParamDefaults) ProtoRef(d);
                U8(proto.Access is { } access ? (int)access + 1 : 0);
                U8((proto.IsStatic ? 1 : 0) | (proto.ReadsOnly ? 2 : 0));
                UInt(proto.RefMask);
                UInt(proto.CaptureCopyMask);
                UInt(proto.CaptureMovedMask);
                OptStrList(proto.SelectorPath);
                OptStrList(proto.ParamTypes);
                Str(proto.FieldGetterOf);
                OptStrList(proto.ParamNames);
                Int(proto.TypeParamCount);
                if (proto.Specializations == null) UInt(0);
                else
                {
                    Count(proto.Specializations.Length + 1);
                    foreach (var s in proto.Specializations) ProtoRef(s);
                }
                Str(proto.SpecOwner);
                OptStrList(proto.SpecArgs);
                Int(proto.SpecDefinedMask);
                if (proto.SpecParamKinds == null) UInt(0);
                else { Count(proto.SpecParamKinds.Length + 1); _out.Write(proto.SpecParamKinds); }

                _open.Remove(proto);
            }

            /// <summary>A chunk, like a function: 1 and the chunk where it stands first, else its number + 2.</summary>
            private void ChunkRef(Chunk chunk)
            {
                if (_chunks.TryGetValue(chunk, out int id))
                {
                    if (_open.Contains(chunk)) throw new InvalidOperationException("A chunk of the program contains itself.");
                    UInt((ulong)id + 2);
                    return;
                }
                UInt(1);
                _chunks[chunk] = _chunks.Count;
                _open.Add(chunk);

                Count(chunk.Code.Count);
                _out.Write(chunk.CodeArray);
                Count(chunk.Constants.Count);
                foreach (var c in chunk.Constants) Constant(c);
                Count(chunk.Units.Count);
                foreach (var u in chunk.Units) UnitRef(u);
                Count(chunk.Functions.Count);
                foreach (var f in chunk.Functions) ProtoRef(f);
                if (chunk.ReceiverTypeArgs == null) UInt(0);
                else
                {
                    Count(chunk.ReceiverTypeArgs.Count + 1);
                    foreach (var (offset, text) in chunk.ReceiverTypeArgs) { Int(offset); Str(text); }
                }
                Count(chunk.Handlers.Count);
                foreach (var h in chunk.Handlers)
                {
                    Count(h.Catches.Count);
                    foreach (var (type, addr) in h.Catches) { Str(type); Int(addr); }
                    if (h.FinallyAddr is { } f) Int((long)f + 1); else Int(0);
                }
                if (_lines)
                {
                    var table = chunk.LineTable;
                    Count(table.Count);
                    foreach (var (offset, source, line) in table) { Int(offset); Int(source); Int(line); }
                }
                Count(chunk.DebugLocalNames.Count);
                foreach (var ((depth, slot), name) in chunk.DebugLocalNames) { Int(depth); Int(slot); Str(name); }

                _open.Remove(chunk);
            }

            private void Attributes(List<AttributeMeta> list)
            {
                Count(list.Count);
                foreach (var a in list) { Str(a.Name); StrList(a.ArgTexts); Str(a.ArgsJson); }
            }

            private void Class(RuntimeClass rc)
            {
                Str(rc.Name);
                if (rc.Base == null) UInt(0); else Count(_classes[rc.Base] + 1);
                U8((rc.IsActor ? 1 : 0) | (rc.IsRecord ? 2 : 0) | (rc.IsReflectionHelper ? 4 : 0) | (rc.Keep ? 8 : 0) | (rc.NotSerializable ? 16 : 0) | (rc.NoCopy ? 32 : 0));
                if (rc.Meta == null) Bool(false);
                else
                {
                    Bool(true);
                    Count(rc.Meta.Members.Count);
                    foreach (var m in rc.Meta.Members)
                    {
                        Str(m.Name); Str(m.Kind); Str(m.TypeName); Str(m.Access);
                        U8((m.IsStatic ? 1 : 0) | (m.IsReadonly ? 2 : 0) | (m.CanRead ? 4 : 0) | (m.CanWrite ? 8 : 0));
                        Str(m.Unit); StrList(m.ParamNames); StrList(m.ParamTypes);
                        Attributes(m.Attributes);
                    }
                    StrList(rc.Meta.BaseNames);
                    Attributes(rc.Meta.Attributes);
                    StrList(rc.Meta.DeclaredInterfaces);
                }
                StrList(rc.Interfaces);
                OptStrList(rc.BaseTypeArgs);
                OptStrList(rc.KeptMembers);

                Count(rc.Fields.Count);
                foreach (var (name, init) in rc.Fields) { Str(name); ProtoRef(init); }
                Count(rc.OwnFieldInfo.Count);
                foreach (var (name, f) in rc.OwnFieldInfo)
                {
                    Str(name);
                    Str(f.RequiredUnit); U8(f.RequiredKind); Str(f.RequiredType); UInt(f.RequiredArray); Str(f.RequiredArrayType); Str(f.ArrayOfParam);
                    U8((f.DerivedDefined ? 1 : 0) | (f.IsStatic ? 2 : 0));
                    U8((int)f.AccessModifier);
                    Str(f.TypeText);
                    if (f.Attributes == null) Bool(false); else { Bool(true); Attributes(f.Attributes); }
                }
                Count(rc.StaticFieldValues.Count);
                foreach (var (name, v) in rc.StaticFieldValues) { Str(name); Constant(v); }
                Count(rc.StaticFields.Count);
                foreach (var (name, init) in rc.StaticFields) { Str(name); ProtoRef(init); }
                OptStrList(rc.ReadonlyStatics);
                Count(rc.Methods.Count);
                foreach (var (name, overloads) in rc.Methods)
                {
                    Str(name);
                    Count(overloads.Count);
                    foreach (var m in overloads) ProtoRef(m);
                }
                Count(rc.Constructors.Count);
                foreach (var (arity, ctor) in rc.Constructors) { Int(arity); ProtoRef(ctor); }
                Count(rc.ExtraConstructors.Count);
                foreach (var ctor in rc.ExtraConstructors) ProtoRef(ctor);
                ProtoRef(rc.Destructor);
                if (rc.ThisReads == null) UInt(0);
                else
                {
                    Count(rc.ThisReads.Count + 1);
                    foreach (var (name, reads) in rc.ThisReads) { Str(name); StrList(reads); }
                }
            }

            private void Type(TypeRef? t)
            {
                if (t == null) { Bool(false); return; }
                Bool(true);
                Str(t.BaseName);
                if (t.BitWidth is { } bits) Int((long)bits + 1); else Int(0);
                Int(t.PointerDepth);
                if (t.LambdaSignature is not { } sig) Bool(false);
                else
                {
                    Bool(true);
                    Str(sig.ReturnTypeName); StrList(sig.ParamTypeNames); Bool(sig.IsSelector); Str(sig.SelectorKind); Bool(sig.IsExpression);
                }
                OptStrList(t.Namespaces);
                Str(t.Unit);
                Int(t.ArrayRank);
                Int(t.TypeArgCount);
                if (t.TypeArgs == null) UInt(0);
                else
                {
                    Count(t.TypeArgs.Count + 1);
                    foreach (var a in t.TypeArgs) Type(a);
                }
                U8((t.Unsigned ? 1 : 0) | (t.NonNull ? 2 : 0));
            }

            private void Extern(ExternSignature sig)
            {
                Str(sig.LibName);
                Count(sig.ParamTypes.Count);
                foreach (var t in sig.ParamTypes) Type(t);
                Type(sig.ReturnType);
                Str(sig.InlineC);
                OptStrList(sig.ParamNames);
                Int(sig.InlineSource);
                Int(sig.InlineLine);
                Str(sig.DisplayName);
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------------------------------------

        private sealed class Reader
        {
            private readonly byte[] _in;
            private int _at;
            private bool _lines;
            private string[] _strings = Array.Empty<string>();
            private Unit[] _units = Array.Empty<Unit>();
            private EnumType[] _enums = Array.Empty<EnumType>();
            private readonly List<FunctionProto> _protos = new();
            private readonly List<Chunk> _chunks = new();

            public Reader(byte[] bytes) => _in = bytes;

            public LinkedProgram Run()
            {
                try { return Program(); }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidCastException or DecoderFallbackException)
                {
                    throw new ProgramFileException("The program file is damaged (" + ex.Message + ").");
                }
            }

            public Unit RunUnit()
            {
                try
                {
                    Strings();
                    var unit = UnitBody();
                    if (_at != _in.Length) throw new ProgramFileException("The unit is damaged (bytes behind its end).");
                    return unit;
                }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or DecoderFallbackException)
                {
                    throw new ProgramFileException("The unit is damaged (" + ex.Message + ").");
                }
            }

            private void Strings()
            {
                _strings = new string[Count()];
                var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
                for (int i = 0; i < _strings.Length; i++)
                {
                    int n = Count();
                    _strings[i] = utf8.GetString(_in, Need(n), n);
                }
            }

            private Unit UnitBody()
            {
                var dims = Dimensions();
                double scale = F64(); string? symbol = Str(); double offset = F64(); string? converter = Str(); string? unitless = Str();
                return Unit.Restore(dims, scale, symbol, offset, converter, unitless);
            }

            private LinkedProgram Program()
            {
                if (!IsProgram(_in)) throw new ProgramFileException("Not a fire program file (.fbc).");
                _at = 4;
                int version = U8();
                if (version != Version) throw new ProgramFileException($"The program file has version {version} of the format; this program reads version {Version}.");
                int headerFlags = U8();
                _lines = (headerFlags & FlagLines) != 0;

                Strings();
                _units = new Unit[Count()];
                for (int i = 0; i < _units.Length; i++) _units[i] = UnitBody();
                _enums = new EnumType[Count()];
                for (int i = 0; i < _enums.Length; i++)
                {
                    string name = Name(); bool flags = Bool();
                    var members = new List<(string, long)>();
                    for (int n = Count(); n > 0; n--) members.Add((Name(), Int()));
                    _enums[i] = EnumType.Intern(name, flags, members);
                }

                var mode = (VmExecutionMode)U8();
                int floatWidth = U8(), intWidth = U8(), wordWidth = U8();
                int firstUserSource = (int)Int();
                var nativeImports = new HashSet<string>(StrList()!);
                var packageNatives = OptStrList();
                var packageLibraries = OptStrList();
                var graphicsNatives = OptStrList();

                var customUnits = new List<CustomUnit>();
                for (int n = Count(); n > 0; n--)
                {
                    string name = Name(), symbol = Name(); bool prefixed = Bool();
                    var dims = Dimensions();
                    double scale = F64(), offset = F64();
                    customUnits.Add(new CustomUnit(name, symbol, prefixed, dims, scale, offset, Str(), Str()));
                }

                var enumDecls = new List<CompiledProgram.EnumDecl>();
                for (int n = Count(); n > 0; n--)
                {
                    var e = new CompiledProgram.EnumDecl { Name = Name(), Flags = Bool() };
                    for (int m = Count(); m > 0; m--) { e.Members.Add(Name()); e.Values.Add(Int()); }
                    enumDecls.Add(e);
                }

                var resources = new List<ResourceEntry>();
                for (int n = Count(); n > 0; n--) resources.Add(new ResourceEntry { Name = Name(), Data = Bytes() });

                var externs = new Dictionary<string, ExternSignature>();
                for (int n = Count(); n > 0; n--) { string name = Name(); externs[name] = Extern(); }

                var classList = new RuntimeClass[Count()];
                var bases = new int[classList.Length];
                for (int i = 0; i < classList.Length; i++) classList[i] = Class(out bases[i]);
                for (int i = 0; i < classList.Length; i++)
                    if (bases[i] > 0) classList[i].Base = classList[bases[i] - 1];
                var classes = new Dictionary<string, RuntimeClass>();
                for (int n = Count(); n > 0; n--) { string name = Name(); classes[name] = classList[Count()]; }

                var topLevel = ChunkRef();
                List<string?>? sourceFiles = null, sourceHashes = null;
                if ((headerFlags & FlagSources) != 0)
                {
                    sourceFiles = new List<string?>();
                    sourceHashes = new List<string?>();
                    for (int n = Count(); n > 0; n--) { sourceFiles.Add(Str()); sourceHashes.Add(Str()); }
                }
                var stackTrace = StackTraceMode.Off;
                List<string?>? traceFiles = null;
                if ((headerFlags & FlagTrace) != 0)
                {
                    stackTrace = (StackTraceMode)U8();
                    traceFiles = new List<string?>();
                    for (int n = Count(); n > 0; n--) traceFiles.Add(Str());
                }
                if (_at != _in.Length) throw new ProgramFileException("The program file is damaged (bytes behind its end).");

                var compiled = new CompiledProgram { TopLevel = topLevel, Classes = classes, ExternSignatures = externs, Resources = resources, EnumDecls = enumDecls, CustomUnits = customUnits };
                compiled.RelinkAfterDeserialize();
                return new LinkedProgram(compiled, nativeImports, firstUserSource, mode)
                {
                    FloatWidth = floatWidth, IntWidth = intWidth, WordWidth = wordWidth,
                    PackageNatives = packageNatives?.ToList()!, PackageNativeLibraries = packageLibraries?.ToList()!, GraphicsNatives = graphicsNatives?.ToList()!,
                    SourceFiles = sourceFiles, SourceHashes = sourceHashes, StackTrace = stackTrace, TraceFiles = traceFiles,
                };
            }

            // ---- the pieces ----

            /// <summary>The place of the next <paramref name="n"/> bytes, which are then read.</summary>
            private int Need(int n)
            {
                if (n < 0 || n > _in.Length - _at) throw new ProgramFileException("The program file is damaged (it ends too early).");
                int at = _at;
                _at += n;
                return at;
            }

            private int U8() => _in[Need(1)];
            private bool Bool() => U8() != 0;

            private ulong UInt()
            {
                ulong v = 0;
                for (int shift = 0; shift < 64; shift += 7)
                {
                    int b = U8();
                    v |= (ulong)(b & 0x7F) << shift;
                    if (b < 0x80) return v;
                }
                throw new ProgramFileException("The program file is damaged (a number is too long).");
            }

            private int Count()
            {
                ulong v = UInt();
                if (v > int.MaxValue) throw new ProgramFileException("The program file is damaged (a count is too big).");
                return (int)v;
            }

            private long Int()
            {
                ulong v = UInt();
                return (long)(v >> 1) ^ -(long)(v & 1);
            }

            private double F64() => BitConverter.Int64BitsToDouble(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(_in.AsSpan(Need(8), 8)));

            private byte[] Bytes()
            {
                int n = Count();
                return _in.AsSpan(Need(n), n).ToArray();
            }

            private string? Str()
            {
                int i = Count();
                return i == 0 ? null : _strings[i - 1];
            }

            /// <summary>A name that must be there.</summary>
            private string Name() => Str() ?? throw new ProgramFileException("The program file is damaged (a name is missing).");

            private string?[] StrList()
            {
                var list = new string?[Count()];
                for (int i = 0; i < list.Length; i++) list[i] = Str();
                return list;
            }

            private string?[]? OptStrList()
            {
                int n = Count();
                if (n == 0) return null;
                var list = new string?[n - 1];
                for (int i = 0; i < list.Length; i++) list[i] = Str();
                return list;
            }

            private Dictionary<string, int> Dimensions()
            {
                var dims = new Dictionary<string, int>();
                for (int n = Count(); n > 0; n--) { string symbol = Name(); dims[symbol] = (int)Int(); }
                return dims;
            }

            private Unit? UnitRef()
            {
                int i = Count();
                return i == 0 ? null : _units[i - 1];
            }

            private Value Constant()
            {
                int tag = U8();
                switch (tag)
                {
                    case TagUndefined: return Value.MakeUndefined(UnitRef());
                    case TagBool: return Value.MakeBool(Bool());
                    case TagInt:
                    {
                        long i = Int();
                        var width = (NumericWidth)U8();
                        byte wide = (byte)U8();
                        var unit = UnitRef();
                        int e = Count();
                        return e != 0 ? Value.MakeEnum(i, _enums[e - 1]) : Value.MakeInt(i, unit, width, wide);
                    }
                    case TagFloat:
                    {
                        double f = F64();
                        var width = (NumericWidth)U8();
                        return Value.MakeFloat(f, UnitRef(), width);
                    }
                    case TagChar: return Value.MakeChar((char)Count());
                    case TagString:
                    {
                        int n = Count();
                        int at = Need(checked(n * 2));
                        return Value.MakeString(string.Create(n, (_in, at), static (span, s) =>
                        {
                            for (int i = 0; i < span.Length; i++) span[i] = (char)(s._in[s.at + 2 * i] | (s._in[s.at + 2 * i + 1] << 8));
                        }));
                    }
                    case TagWord: return Value.MakeWord(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(_in.AsSpan(Need(8), 8)));
                    default: throw new ProgramFileException($"The program file is damaged (a constant of an unknown kind {tag}).");
                }
            }

            private FunctionProto? ProtoRef()
            {
                int r = Count();
                if (r == 0) return null;
                if (r >= 2) return _protos[r - 2];
                int id = _protos.Count;
                _protos.Add(null!);   // (its number is taken where it begins, as the writer counted)

                var chunk = ChunkRef();
                int paramCount = (int)Int();
                var defaults = new FunctionProto?[Count()];
                for (int i = 0; i < defaults.Length; i++) defaults[i] = ProtoRef();
                int access = U8();
                int flags = U8();
                var proto = new FunctionProto(chunk, paramCount, access == 0 ? null : (AccessModifier)(access - 1), defaults, (flags & 1) != 0)
                {
                    ReadsOnly = (flags & 2) != 0,
                    RefMask = checked((uint)UInt()),
                    CaptureCopyMask = UInt(),
                    CaptureMovedMask = UInt(),
                    SelectorPath = OptStrList()!,
                    ParamTypes = OptStrList(),
                    FieldGetterOf = Str(),
                    ParamNames = OptStrList()!,
                    TypeParamCount = (int)Int(),
                };
                int specs = Count();
                if (specs > 0)
                {
                    proto.Specializations = new FunctionProto[specs - 1];
                    for (int i = 0; i < specs - 1; i++) proto.Specializations[i] = ProtoRef() ?? throw new ProgramFileException("The program file is damaged (a specialization is missing).");
                }
                proto.SpecOwner = Str();
                proto.SpecArgs = OptStrList()!;
                proto.SpecDefinedMask = Int();
                int kinds = Count();
                if (kinds > 0) proto.SpecParamKinds = _in.AsSpan(Need(kinds - 1), kinds - 1).ToArray();

                _protos[id] = proto;
                return proto;
            }

            private Chunk ChunkRef()
            {
                int r = Count();
                if (r == 0) throw new ProgramFileException("The program file is damaged (a chunk is missing).");
                if (r >= 2) return _chunks[r - 2];
                int id = _chunks.Count;
                _chunks.Add(null!);

                var code = new List<byte>(Bytes());
                var constants = new List<Value>();
                for (int n = Count(); n > 0; n--) constants.Add(Constant());
                var units = new List<Unit>();
                for (int n = Count(); n > 0; n--) units.Add(UnitRef() ?? throw new ProgramFileException("The program file is damaged (a unit is missing)."));
                var functions = new List<FunctionProto>();
                for (int n = Count(); n > 0; n--) functions.Add(ProtoRef() ?? throw new ProgramFileException("The program file is damaged (a function is missing)."));
                Dictionary<int, string>? receiverTypeArgs = null;
                int receivers = Count();
                if (receivers > 0)
                {
                    receiverTypeArgs = new Dictionary<int, string>();
                    for (int n = receivers - 1; n > 0; n--) { int offset = (int)Int(); receiverTypeArgs[offset] = Name(); }
                }
                var handlers = new List<HandlerTemplate>();
                for (int n = Count(); n > 0; n--)
                {
                    var h = new HandlerTemplate();
                    for (int c = Count(); c > 0; c--) { string? type = Str(); h.Catches.Add((type, (int)Int())); }
                    long f = Int();
                    if (f != 0) h.FinallyAddr = (int)(f - 1);
                    handlers.Add(h);
                }
                var lines = new List<(int, int, int)>();
                if (_lines)
                    for (int n = Count(); n > 0; n--) lines.Add(((int)Int(), (int)Int(), (int)Int()));
                var names = new Dictionary<(int Depth, int Slot), string>();
                for (int n = Count(); n > 0; n--) { int depth = (int)Int(), slot = (int)Int(); names[(depth, slot)] = Name(); }

                var chunk = new Chunk(code, constants, units, functions, handlers, names) { ReceiverTypeArgs = receiverTypeArgs };
                chunk.LoadLineTable(lines);
                _chunks[id] = chunk;
                return chunk;
            }

            private List<AttributeMeta> Attributes()
            {
                var list = new List<AttributeMeta>();
                for (int n = Count(); n > 0; n--) list.Add(new AttributeMeta { Name = Name(), ArgTexts = StrList().ToList()!, ArgsJson = Name() });
                return list;
            }

            private RuntimeClass Class(out int baseRef)
            {
                string name = Name();
                baseRef = Count();
                int flags = U8();
                ClassMeta? meta = null;
                if (Bool())
                {
                    meta = new ClassMeta();
                    for (int n = Count(); n > 0; n--)
                    {
                        var m = new MemberMeta { Name = Name(), Kind = Name(), TypeName = Name(), Access = Name() };
                        int f = U8();
                        m.IsStatic = (f & 1) != 0; m.IsReadonly = (f & 2) != 0; m.CanRead = (f & 4) != 0; m.CanWrite = (f & 8) != 0;
                        m.Unit = Name(); m.ParamNames = StrList().ToList()!; m.ParamTypes = StrList().ToList()!;
                        m.Attributes = Attributes();
                        meta.Members.Add(m);
                    }
                    meta.BaseNames = StrList().ToList()!;
                    meta.Attributes = Attributes();
                    meta.DeclaredInterfaces = StrList().ToList()!;
                }
                var interfaces = StrList().ToList();
                var baseTypeArgs = OptStrList();
                var keptMembers = OptStrList();

                var fields = new List<(string Name, FunctionProto Init)>();
                for (int n = Count(); n > 0; n--) fields.Add((Name(), ProtoRef() ?? throw new ProgramFileException("The program file is damaged (a field without its initializer).")));
                var info = new Dictionary<string, FieldInfo>();
                for (int n = Count(); n > 0; n--)
                {
                    string field = Name();
                    var f = new FieldInfo { RequiredUnit = Str(), RequiredKind = (byte)U8(), RequiredType = Str(), RequiredArray = checked((ushort)UInt()), RequiredArrayType = Str(), ArrayOfParam = Str() };
                    int bits = U8();
                    f.DerivedDefined = (bits & 1) != 0; f.IsStatic = (bits & 2) != 0;
                    f.AccessModifier = (AccessModifier)U8();
                    f.TypeText = Name();
                    if (Bool()) f.Attributes = Attributes();
                    info[field] = f;
                }
                var staticValues = new Dictionary<string, Value>();
                for (int n = Count(); n > 0; n--) { string field = Name(); staticValues[field] = Constant(); }
                var staticFields = new List<(string Name, FunctionProto Init)>();
                for (int n = Count(); n > 0; n--) staticFields.Add((Name(), ProtoRef() ?? throw new ProgramFileException("The program file is damaged (a static field without its initializer).")));
                var readonlyStatics = OptStrList();
                var methods = new Dictionary<string, List<FunctionProto>>();
                for (int n = Count(); n > 0; n--)
                {
                    string method = Name();
                    var overloads = new List<FunctionProto>();
                    for (int m = Count(); m > 0; m--) overloads.Add(ProtoRef() ?? throw new ProgramFileException("The program file is damaged (a method is missing)."));
                    methods[method] = overloads;
                }
                var constructors = new Dictionary<int, FunctionProto>();
                for (int n = Count(); n > 0; n--) { int arity = (int)Int(); constructors[arity] = ProtoRef() ?? throw new ProgramFileException("The program file is damaged (a constructor is missing)."); }

                var rc = new RuntimeClass(name, fields, info, staticValues, staticFields, methods, constructors)
                {
                    IsActor = (flags & 1) != 0, IsRecord = (flags & 2) != 0, IsReflectionHelper = (flags & 4) != 0, Keep = (flags & 8) != 0, NotSerializable = (flags & 16) != 0, NoCopy = (flags & 32) != 0,
                    Meta = meta, Interfaces = interfaces!, BaseTypeArgs = baseTypeArgs!, KeptMembers = keptMembers?.ToList()!,
                    ReadonlyStatics = readonlyStatics == null ? null : new HashSet<string>(readonlyStatics!),
                };
                for (int n = Count(); n > 0; n--) rc.ExtraConstructors.Add(ProtoRef() ?? throw new ProgramFileException("The program file is damaged (a constructor is missing)."));
                rc.Destructor = ProtoRef();
                int reads = Count();
                if (reads > 0)
                {
                    rc.ThisReads = new Dictionary<string, string[]>();
                    for (int n = reads - 1; n > 0; n--) { string method = Name(); rc.ThisReads[method] = StrList()!; }
                }
                return rc;
            }

            private TypeRef? Type()
            {
                if (!Bool()) return null;
                string baseName = Name();
                long bits = Int();
                int pointerDepth = (int)Int();
                LambdaSignature? sig = null;
                if (Bool())
                {
                    string? ret = Str(); var pars = StrList(); bool selector = Bool(); string kind = Name(); bool expression = Bool();
                    sig = new LambdaSignature(ret, pars!, selector, kind, expression);
                }
                var namespaces = OptStrList();
                string? unit = Str();
                int arrayRank = (int)Int(), typeArgCount = (int)Int();
                List<TypeRef>? typeArgs = null;
                int args = Count();
                if (args > 0)
                {
                    typeArgs = new List<TypeRef>();
                    for (int n = args - 1; n > 0; n--) typeArgs.Add(Type() ?? throw new ProgramFileException("The program file is damaged (a type argument is missing)."));
                }
                int flags = U8();
                return new TypeRef(baseName, bits == 0 ? null : (int)(bits - 1), pointerDepth, sig, namespaces!, unit, arrayRank, typeArgCount, typeArgs, (flags & 1) != 0, (flags & 2) != 0);
            }

            private ExternSignature Extern()
            {
                string? lib = Str();
                var pars = new List<TypeRef?>();
                for (int n = Count(); n > 0; n--) pars.Add(Type());
                return new ExternSignature
                {
                    LibName = lib, ParamTypes = pars, ReturnType = Type(), InlineC = Str(), ParamNames = OptStrList()!,
                    InlineSource = (int)Int(), InlineLine = (int)Int(), DisplayName = Str(),
                };
            }
        }
    }
}
