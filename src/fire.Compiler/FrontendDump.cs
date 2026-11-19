using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using fire.Ast;
using fire.Lexing;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>`forge tokens <file>` / `forge ast <file>` (docs/spec/FORGE_C.md): the tokens and the syntax tree of one source as text, with the
    /// diagnostics as forge prints them - the measure for the frontend in C (native/forge, docs/ROADMAP_C_PORT.md Phase C 1/2), which prints the same.
    /// The source is preprocessed with the default directives (`#include`, the announced ones disappear) and the symbols of a VM run on this machine;
    /// `ast` parses it alone, as `Parser.ParseMultiple` of one source named by its file name. `--list <file> --out <dir>` dumps many files, one output
    /// each (`<dir>/<n>.txt`, n the line of the list, from 1).</summary>
    public static class FrontendDump
    {
        public static int Run(IReadOnlyList<string> args)
        {
            string command = args[0];
            if (args.Count == 2) { Console.Out.Flush(); return Dump(command, args[1], Console.OpenStandardOutput()); }
            if (args.Count == 5 && args[1] == "--list" && args[3] == "--out")
            {
                var files = File.ReadAllLines(args[2]);
                Directory.CreateDirectory(args[4]);
                for (int i = 0; i < files.Length; i++)
                {
                    if (files[i].Length == 0) continue;
                    using var stream = File.Create(Path.Combine(args[4], (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt"));
                    Dump(command, files[i], stream);
                }
                return 0;
            }
            Console.Error.WriteLine($"usage: {command} <file.fire> | {command} --list <files.txt> --out <folder>");
            return 2;
        }

        private static int Dump(string command, string path, Stream output)
        {
            var o = new StringBuilder();
            int result = 0;
            try
            {
                string text = File.ReadAllText(path);
                var registry = DirectiveRegistry.CreateDefault();
                foreach (var symbol in ConditionalSymbols.For(null, ConditionalSymbols.DefaultEngine)) registry.Symbols.Add(symbol);
                var processed = Preprocessor.Process(text, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", registry) with { Name = Path.GetFileName(path) };
                if (command == "tokens")
                {
                    foreach (var u in processed.Usings) o.Append("using ").Append(Esc(u)).Append('\n');
                    foreach (var t in new Lexer(processed.Source).Tokenize()) Token(o, t);
                }
                else
                {
                    var collector = new DiagnosticCollector();
                    List<fire.Ast.Stmt> program;
                    using (collector.Begin()) program = Parser.ParseMultiple(new[] { processed });
                    Node(o, 0, "program", program);
                    foreach (var d in collector.Items) o.Append(d.ToLogLine()).Append('\n');
                    foreach (var w in collector.Declared)
                        o.Append("declared ").Append(Esc(w.Class)).Append(' ').Append(w.Target).Append(' ').Append(w.Name == null ? "null" : Esc(w.Name)).Append(' ')
                         .Append(w.MinArity).Append(' ').Append(w.MaxArity).Append(' ').Append(w.IsStatic ? "static" : "instance").Append(" W").Append(w.Id).Append(' ')
                         .Append(Esc(w.Text)).Append(" line ").Append(w.Line).Append(' ').Append(w.File == null ? "null" : Esc(w.File)).Append('\n');
                }
            }
            catch (Exception ex) when (ex is LexException or ParseException or PreprocessorException)
            {
                foreach (var d in CompileDiagnostics.Of(ex)) o.Append(d.ToLogLine()).Append('\n');
                result = 1;
            }
            var bytes = new UTF8Encoding(false).GetBytes(o.ToString());
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
            return result;
        }

        /// <summary>A text in quotes: printable ASCII as it is, `\\` and `\"`, every other UTF-16 unit as `\uXXXX`.</summary>
        private static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c >= 0x20 && c < 0x7F) sb.Append(c);
                else sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.Append('"').ToString();
        }

        private static string Bits(double d) => "0x" + BitConverter.DoubleToInt64Bits(d).ToString("X16", System.Globalization.CultureInfo.InvariantCulture);

        private static void Token(StringBuilder o, Token t)
        {
            o.Append(t.Line).Append(':').Append(t.Column).Append(' ').Append(t.Type).Append(' ').Append(Esc(t.Lexeme)).Append(" len=").Append(t.Length);
            if (t.NewlineBefore) o.Append(" nl");
            if (!string.IsNullOrEmpty(t.UnitSuffix)) o.Append(" unit=").Append(Esc(t.UnitSuffix));
            switch (t.LiteralValue)
            {
                case long l: o.Append(" int=").Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case double d: o.Append(" float=").Append(Bits(d)); break;
                case string s: o.Append(" value=").Append(Esc(s)); break;
                case char c: o.Append(" value=").Append(Esc(c.ToString())); break;
                case List<InterpolationSegment> segs:
                    o.Append(" segments=").Append(segs.Count);
                    foreach (var seg in segs)
                    {
                        o.Append(seg.IsExpression ? " E" : " T").Append(Esc(seg.Text));
                        if (seg.Format != null) o.Append(':').Append(Esc(seg.Format));
                    }
                    break;
            }
            o.Append('\n');
        }

        private static string ValueText(Value v)
        {
            string unit = v.Unit is { IsUnitless: false } u ? " unit=" + Esc(u.ToString()) : "";
            return v.Kind switch
            {
                ValueKind.Int => "int " + v.AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture) + unit,
                ValueKind.Float => "float " + Bits(v.AsFloat()) + unit,
                ValueKind.String => "string " + Esc(v.AsString()),
                ValueKind.Char => "char " + Esc(v.AsChar().ToString()),
                ValueKind.Bool => v.AsBool() ? "bool true" : "bool false",
                ValueKind.Undefined => "undefined" + unit,
                _ => "value " + v.Kind,
            };
        }

        private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = new();

        /// <summary>The fields of a node in a fixed order: the parameters of its primary constructor, then the other properties that can be set.</summary>
        private static PropertyInfo[] Fields(Type type)
        {
            if (PropertyCache.TryGetValue(type, out var known)) return known;
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetIndexParameters().Length == 0).ToList();
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            var ordered = new List<PropertyInfo>();
            if (ctor != null)
                foreach (var p in ctor.GetParameters())
                    if (props.FirstOrDefault(x => x.Name == p.Name) is { } prop) ordered.Add(prop);
            foreach (var p in props) if (!ordered.Contains(p)) ordered.Add(p);
            var result = ordered.ToArray();
            PropertyCache[type] = result;
            return result;
        }

        private static void Node(StringBuilder o, int indent, string label, object? v)
        {
            o.Append(' ', indent * 2).Append(label).Append(": ");
            switch (v)
            {
                case null: o.Append("null\n"); return;
                case string s: o.Append(Esc(s)).Append('\n'); return;
                case bool b: o.Append(b ? "true" : "false").Append('\n'); return;
                case int i: o.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n'); return;
                case long l: o.Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n'); return;
                case double d: o.Append(Bits(d)).Append('\n'); return;
                case Enum e: o.Append(e.ToString()).Append('\n'); return;
                case Value val: o.Append(ValueText(val)).Append('\n'); return;
                case IDictionary dict:
                {
                    o.Append('{').Append(dict.Count).Append("}\n");
                    foreach (DictionaryEntry entry in dict) Node(o, indent + 1, Esc((string)entry.Key), entry.Value);
                    return;
                }
                case IEnumerable list:
                {
                    var items = list.Cast<object?>().ToList();
                    o.Append(v is OwnNamespaceList ? "own" : "").Append('[').Append(items.Count).Append("]\n");
                    foreach (var item in items) Node(o, indent + 1, "-", item);
                    return;
                }
            }
            var type = v.GetType();
            o.Append(type.Name).Append('\n');
            foreach (var p in Fields(type)) Node(o, indent + 1, p.Name, p.GetValue(v));
            // what the parser notes beside the tree (TypeChecker reads it)
            if (v is AssignExpr && Parser.ParenthesizedAssignments.TryGetValue((Expr)v, out _)) Node(o, indent + 1, "Parenthesized", true);
            if (v is VarDeclStmt && Parser.Switches.TryGetValue((Stmt)v, out var values)) Node(o, indent + 1, "SwitchValues", values);
        }
    }
}
