using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace fire.Native
{
    // The pointer convention (docs/spec/NATIVE_BACKEND.md "Zeigerkonvention"): on the targets with PointerCalls (Open Watcom: dos16, win16, dos32, bios16) a fire
    // function takes its Values by address - `static Value fN_p(const Value* self_p, const Value* P0_p)` - instead of by value: a call passes `&s0` (4 bytes,
    // `lea; push`) instead of a copy of 16 bytes (`sub sp,16; rep movsw`) per argument. Inside, a parameter that the function only reads is `(*P0_p)` (a
    // macro around the function); one it writes (a guard, a unit, a store) is copied in at the start. Both steps work on the C text: the function after it
    // is made (PointerFunction), the calls in the whole program at the end (PointerCallsPass) - a call whose arguments are not all variables of the caller
    // (an expression, a global, which the callee could change while it reads it) goes to a small function with Values (`fN`), made only when one is needed.
    public sealed partial class CppGenerator
    {
        /// <summary>The convention is on for this file: a target with PointerCalls, not a debug build (its wrappers keep Values), one file (no modules or plugins,
        /// whose functions are called across files).</summary>
        private bool PointerCallsOn => _target.Native.PointerCalls && !DebugBuild && !Wrapped && !Modular && !_openWorld;

        /// <summary>The functions with the convention: name -> the names of their parameters and which of them go by address (the Values; the C numbers of a
        /// C body stay by value).</summary>
        private readonly Dictionary<string, (string[] Names, bool[] ByAddress)> _pointerFuncs = new(StringComparer.Ordinal);

        /// <summary>The parameters of a C parameter list (`Value self, Real P0`): each declaration, its name and whether it is a Value.</summary>
        private static List<(string Decl, string Name, bool IsValue)> ParamDecls(string list)
        {
            var result = new List<(string, string, bool)>();
            if (list.Trim() is "" or "void") return result;
            foreach (var raw in list.Split(','))
            {
                string d = raw.Trim();
                int sp = d.LastIndexOfAny(new[] { ' ', '*' });
                result.Add((d, d.Substring(sp + 1), d.StartsWith("Value ", StringComparison.Ordinal)));
            }
            return result;
        }

        /// <summary>A parameter list with the Values by address (`Value P0` -> `const Value* P0_p`).</summary>
        private static string PointerParams(List<(string Decl, string Name, bool IsValue)> ps) =>
            string.Join(", ", ps.Select(p => p.IsValue ? $"const Value* {p.Name}_p" : p.Decl));

        /// <summary>The runtime functions that take a Value by a `const Value*` (they only read it): `&P0` in one of them does not write the parameter.</summary>
        private static readonly Regex ReadOnlyAddressCall = new(@"\b(?:leafAliveP|destroyedErrorP|retainP|releaseP|truthyP|objAtP|adoptP|ctorEndP|hoistFromP|transferTreeP|finishArgsP|claimToListP)\(\s*&\s*$", RegexOptions.Compiled);

        /// <summary>Whether the body writes the parameter `p`: a store, `++`/`--`, a member store, or its address given to anything but a runtime function that only reads.</summary>
        private static bool WritesParam(string body, string p)
        {
            foreach (Match m in Regex.Matches(body, $@"(?<![\w.>]){Regex.Escape(p)}(?!\w)"))
            {
                string after = body.Substring(m.Index + m.Length, Math.Min(64, body.Length - m.Index - m.Length));
                if (Regex.IsMatch(after, @"^\s*(?:=(?!=)|\+=|-=|\*=|/=|\|=|&=|\+\+|--)")) return true;
                if (Regex.IsMatch(after, @"^\s*\.\s*\w+\s*(?:=(?!=)|\+=|-=|\*=|/=|\|=|&=|\+\+|--)")) return true;
                string before = body.Substring(Math.Max(0, m.Index - 64), Math.Min(64, m.Index)).TrimEnd();
                if (before.EndsWith("++", StringComparison.Ordinal) || before.EndsWith("--", StringComparison.Ordinal)) return true;
                if (before.EndsWith("&", StringComparison.Ordinal) && !before.EndsWith("&&", StringComparison.Ordinal) && !ReadOnlyAddressCall.IsMatch(before)) return true;
            }
            return false;
        }

        /// <summary>The function `name` in `code` (its signature `signature`) with the convention, or the text unchanged when it cannot have it: its Value
        /// parameters by address. The function ends at the first closing brace in the first column; what follows it stays as it is (the wrapper of a C body).</summary>
        private string PointerFunction(string name, string code, string signature)
        {
            _pointerFuncs.Remove(name);
            int open = signature.IndexOf(name + "(", StringComparison.Ordinal);
            int sigAt = code.IndexOf(signature + "\n", StringComparison.Ordinal);
            if (open < 0 || sigAt < 0 || !signature.EndsWith(")", StringComparison.Ordinal)) return code;
            var decls = ParamDecls(signature.Substring(open + name.Length + 1, signature.Length - open - name.Length - 2));
            var ps = decls.Where(p => p.IsValue).Select(p => p.Name).ToArray();
            if (ps.Length == 0) return code;
            int bodyAt = sigAt + signature.Length + 1;
            int endAt = code.IndexOf("\n}\n", bodyAt - 1, StringComparison.Ordinal);
            if (endAt < 0) return code;
            string body = code.Substring(bodyAt, endAt + 3 - bodyAt), rest = code.Substring(endAt + 3);
            var written = ps.Where(p => WritesParam(body, p)).ToHashSet(StringComparer.Ordinal);
            string head = signature.Substring(0, open);
            string newSignature = $"{head}{name}_p({PointerParams(decls)})";
            // the copies of the written parameters right after the brace that opens the body
            int brace = body.IndexOf("{\n", StringComparison.Ordinal);
            if (brace < 0) return code;
            string copies = string.Concat(ps.Where(written.Contains).Select(p => $"    Value {p} = *{p}_p;\n"));
            body = body.Substring(0, brace + 2) + copies + body.Substring(brace + 2);
            var readOnly = ps.Where(p => !written.Contains(p)).ToList();
            var sb = new StringBuilder();
            sb.Append(code, 0, sigAt);
            foreach (var p in readOnly) sb.Append($"#define {p} (*{p}_p)\n");
            sb.Append(newSignature).Append('\n').Append(body);
            foreach (var p in readOnly) sb.Append($"#undef {p}\n");
            sb.Append(rest);
            _pointerFuncs[name] = (decls.Select(p => p.Name).ToArray(), decls.Select(p => p.IsValue).ToArray());
            return sb.ToString();
        }

        /// <summary>An argument a call can pass by its address: a variable of the caller (a slot, a parameter, a local, an element of the arguments of a thunk) -
        /// not an expression, not a global or a static field (the callee could change it while it reads its parameter).</summary>
        private static readonly Regex AddressableArg = new(@"^(?!(?:G|SF)\d+$)(?!g_)[A-Za-z_]\w*(?:\[\d+\])?$", RegexOptions.Compiled);

        /// <summary>The calls of the whole program (not the C of the packages) to the functions with the convention: `fN(s0, s1)` becomes `fN_p(&s0, &s1)`;
        /// the prototypes become those of `fN_p`; a function that is called with an expression or used as a pointer keeps a function `fN` with Values.</summary>
        private string PointerCallsPass(string text)
        {
            if (_pointerFuncs.Count == 0) return text;
            var lines = text.Split('\n');
            // (the C of the packages and bridges is not the generator's: its names are its own)
            var ours = new bool[lines.Length];
            bool inPackage = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.StartsWith("// ---- end of ", StringComparison.Ordinal)) { inPackage = false; ours[i] = false; continue; }
                if (l.StartsWith("// ---- ", StringComparison.Ordinal) && l.Contains("import \"", StringComparison.Ordinal)) { inPackage = true; ours[i] = false; continue; }
                ours[i] = !inPackage;
            }
            var needsValues = new HashSet<string>(StringComparer.Ordinal);
            var protoLine = new Regex(@"^((?:FIRE_MAYBE_UNUSED )?static [A-Za-z_]\w* )([A-Za-z_]\w*)\((.*)\);$");
            var name = new Regex(@"(?<![\w.>])([A-Za-z_]\w*)(?!\w)");
            for (int i = 0; i < lines.Length; i++)
            {
                if (!ours[i]) continue;
                if (protoLine.Match(lines[i]) is { Success: true } pm && _pointerFuncs.ContainsKey(pm.Groups[2].Value)) continue;
                lines[i] = RewriteCalls(lines[i], name, needsValues);
            }
            for (int i = 0; i < lines.Length; i++)
            {
                if (!ours[i] || protoLine.Match(lines[i]) is not { Success: true } pm || !_pointerFuncs.ContainsKey(pm.Groups[2].Value)) continue;
                string fn = pm.Groups[2].Value, prefix = pm.Groups[1].Value;
                var decls = ParamDecls(pm.Groups[3].Value);
                string proto = $"{prefix}{fn}_p({PointerParams(decls)});";
                if (needsValues.Contains(fn))
                    proto += $"\nFIRE_MAYBE_UNUSED {prefix.Replace("FIRE_MAYBE_UNUSED ", "")}{fn}({pm.Groups[3].Value}) {{ return {fn}_p({string.Join(", ", decls.Select(p => p.IsValue ? "&" + p.Name : p.Name))}); }}";
                lines[i] = proto;
            }
            return string.Join("\n", lines);
        }

        /// <summary>One line: its calls of functions with the convention by address where every argument can be; the others (and a name used as a pointer)
        /// are noted in <paramref name="needsValues"/>.</summary>
        private string RewriteCalls(string line, Regex name, HashSet<string> needsValues)
        {
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal) && !line.TrimStart().StartsWith("#line", StringComparison.Ordinal)) return line;
            StringBuilder? sb = null;
            int copied = 0, pos = 0;
            while (pos < line.Length)
            {
                // (string and character literals are skipped whole)
                char c = line[pos];
                if (c == '"' || c == '\'') { pos = SkipLiteral(line, pos); continue; }
                if (c == '/' && pos + 1 < line.Length && line[pos + 1] == '/') break;
                if (!(char.IsLetter(c) || c == '_') || (pos > 0 && (char.IsLetterOrDigit(line[pos - 1]) || line[pos - 1] == '_'))) { pos++; continue; }
                var m = name.Match(line, pos);
                if (!m.Success || m.Index != pos) { pos++; continue; }
                string fn = m.Groups[1].Value;
                int end = m.Index + m.Length;
                if (!_pointerFuncs.TryGetValue(fn, out var pf)) { pos = end; continue; }
                int open = end;
                while (open < line.Length && line[open] == ' ') open++;
                if (open >= line.Length || line[open] != '(') { needsValues.Add(fn); pos = end; continue; }   // (used as a pointer)
                int close = MatchingParen(line, open);
                if (close < 0) { needsValues.Add(fn); pos = end; continue; }
                var args = SplitArgs(line.Substring(open + 1, close - open - 1));
                if (args.Count != pf.Names.Length || args.Where((a, k) => pf.ByAddress[k] && !AddressableArg.IsMatch(a)).Any())
                {
                    needsValues.Add(fn);
                    pos = open + 1;   // (its arguments may hold calls of their own)
                    continue;
                }
                sb ??= new StringBuilder(line.Length + 16);
                sb.Append(line, copied, m.Index - copied);
                // (a C number of a C body stays by value - it may hold calls of its own)
                sb.Append(fn).Append("_p(").Append(string.Join(", ", args.Select((a, k) => pf.ByAddress[k] ? "&" + a : RewriteCalls(a, name, needsValues)))).Append(')');
                copied = pos = close + 1;
            }
            if (sb == null) return line;
            sb.Append(line, copied, line.Length - copied);
            return sb.ToString();
        }

        private static int SkipLiteral(string s, int at)
        {
            char q = s[at];
            int i = at + 1;
            while (i < s.Length && s[i] != q) i += s[i] == '\\' ? 2 : 1;
            return Math.Min(s.Length, i + 1);
        }

        private static int MatchingParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\'') { i = SkipLiteral(s, i) - 1; continue; }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') { if (--depth == 0) return c == ')' ? i : -1; }
            }
            return -1;
        }

        private static List<string> SplitArgs(string s)
        {
            var result = new List<string>();
            if (s.Trim().Length == 0) return result;
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\'') { i = SkipLiteral(s, i) - 1; continue; }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0) { result.Add(s.Substring(start, i - start).Trim()); start = i + 1; }
            }
            result.Add(s.Substring(start).Trim());
            return result;
        }
    }
}
