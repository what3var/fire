using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>
    /// Conditional compilation for the preprocessor (SPEC 8.1.6): `#if`, `#elif`, `#else`, `#endif`, `#ifdef`, `#ifndef`, `#define`, `#undef`, `#error`.
    /// A line of a branch that is not taken is replaced by an empty line (line numbers stay), so what it contains - also `#import` and `#include` - is never read.
    /// The symbols are plain names (case-insensitive): the target (`windows`, `linux`, `macos`, `posix`, `esp32`, `freertos`), the engine (`vm`, `native`),
    /// `float32`, whatever `-D name` / `#define name` adds. An unknown name is false.
    /// </summary>
    public static class ConditionalSymbols
    {
        public const string DefaultEngine = "vm";

        /// <summary>The symbols of a build: the symbols of the target (null: the machine the process runs on), the engine (`vm` or `native`), `float32` when the
        /// precision of `float` is 32 bits (the command line override, else the default of the target - `#floatwidth` is read later than the symbols are needed)
        /// and the defines of the command line.</summary>
        public static HashSet<string> For(TargetProfile? target, string? engine = null, int? floatWidth = null, IEnumerable<string>? defines = null)
        {
            target ??= TargetProfile.Host;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in target.Symbols) set.Add(s);
            set.Add(string.IsNullOrWhiteSpace(engine) ? DefaultEngine : engine);
            if ((floatWidth ?? target.FloatWidth) == 32) set.Add("float32");
            if (defines != null) foreach (var d in defines) if (!string.IsNullOrWhiteSpace(d)) set.Add(d.Trim());
            return set;
        }

        private static readonly Regex Token = new(@"\G(?:(?<name>[A-Za-z_][A-Za-z0-9_]*)|(?<op>&&|\|\||!|\(|\)))", RegexOptions.Compiled);
        private static readonly Regex NameOnly = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        /// <summary>Evaluates `a && (b || !c)`: names, `true`, `false`, `!`, `&&`, `||` (`&&` binds tighter) and parentheses.</summary>
        public static bool Evaluate(string expression, ISet<string> symbols, int line)
        {
            var tokens = new List<string>();
            int pos = 0;
            while (pos < expression.Length)
            {
                if (char.IsWhiteSpace(expression[pos])) { pos++; continue; }
                var m = Token.Match(expression, pos);
                if (!m.Success)
                    throw new PreprocessorException($"'#if' expression '{expression.Trim()}': unexpected character '{expression[pos]}' (line {line}).");
                tokens.Add(m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["op"].Value);
                pos = m.Index + m.Length;
            }
            if (tokens.Count == 0) throw new PreprocessorException($"'#if' expects a condition (line {line}).");

            int i = 0;
            bool result = Or();
            if (i < tokens.Count) throw new PreprocessorException($"'#if' expression '{expression.Trim()}': unexpected '{tokens[i]}' (line {line}).");
            return result;

            bool Or()
            {
                bool v = And();
                while (i < tokens.Count && tokens[i] == "||") { i++; bool r = And(); v = v || r; }
                return v;
            }
            bool And()
            {
                bool v = Not();
                while (i < tokens.Count && tokens[i] == "&&") { i++; bool r = Not(); v = v && r; }
                return v;
            }
            bool Not()
            {
                if (i < tokens.Count && tokens[i] == "!") { i++; return !Not(); }
                return Primary();
            }
            bool Primary()
            {
                if (i >= tokens.Count) throw new PreprocessorException($"'#if' expression '{expression.Trim()}' ends too early (line {line}).");
                string t = tokens[i++];
                if (t == "(")
                {
                    bool v = Or();
                    if (i >= tokens.Count || tokens[i] != ")") throw new PreprocessorException($"'#if' expression '{expression.Trim()}': ')' is missing (line {line}).");
                    i++;
                    return v;
                }
                if (t is ")" or "&&" or "||") throw new PreprocessorException($"'#if' expression '{expression.Trim()}': unexpected '{t}' (line {line}).");
                if (t == "true") return true;
                if (t == "false") return false;
                return symbols.Contains(t);
            }
        }

        /// <summary>Does one pass over `source`: handles only the conditional directives (and `#define`/`#undef`) and returns the text with the lines of branches
        /// that are not taken emptied - for what has to know which `#import`s are active without running the whole preprocessor (the editor).</summary>
        public static string Apply(string source, ISet<string> symbols)
        {
            var state = new ConditionalState();
            var lines = source.Split('\n');
            var sb = new System.Text.StringBuilder(source.Length);
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].TrimEnd('\r');
                var m = Preprocessor.DirectiveLineRegex.Match(line);
                bool handled = m.Success && state.TryHandle(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : "", n + 1, symbols, throwOnError: false);
                if (n > 0) sb.Append('\n');
                if (!handled && state.Active) sb.Append(line);
            }
            return sb.ToString();
        }

        /// <summary>The lines (0-based) that lie in a branch that is not taken - for the editor, which greys them out. The directive lines themselves count as active.
        /// A `#define`/`#undef` in the text is honoured; the given set is not changed.</summary>
        public static bool[] InactiveLines(string source, ISet<string> symbols)
        {
            var copy = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
            var state = new ConditionalState();
            var lines = source.Split('\n');
            var inactive = new bool[lines.Length];
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].TrimEnd('\r');
                var m = Preprocessor.DirectiveLineRegex.Match(line);
                bool handled = m.Success && state.TryHandle(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : "", n + 1, copy, throwOnError: false);
                inactive[n] = !handled && !state.Active;
            }
            return inactive;
        }
    }

    /// <summary>The `#if` nesting of one file.</summary>
    internal sealed class ConditionalState
    {
        private sealed class Frame
        {
            public bool ParentActive, Taken, Active, SawElse;
            public int Line;
        }

        private readonly Stack<Frame> _frames = new();

        /// <summary>Are the lines being read at the moment part of a branch that is taken?</summary>
        public bool Active => _frames.Count == 0 || _frames.Peek().Active;

        /// <summary>Handles the directive if it is a conditional one (returns true: the line is consumed); otherwise false. `throwOnError` false: a malformed
        /// condition counts as false instead of an error (the editor reads text that is just being typed).</summary>
        public bool TryHandle(string name, string argument, int line, ISet<string> symbols, bool throwOnError = true)
        {
            argument = argument.Trim();
            switch (name.ToLowerInvariant())
            {
                case "if": Push(Active && Eval(argument, symbols, line, throwOnError), line); return true;
                case "ifdef": Push(Active && NameCondition(argument, "ifdef", symbols, line, throwOnError), line); return true;
                case "ifndef": Push(Active && !NameCondition(argument, "ifndef", symbols, line, throwOnError), line); return true;
                case "elif":
                {
                    var f = Top("elif", line, throwOnError);
                    if (f == null) return true;
                    if (f.SawElse) { if (throwOnError) throw new PreprocessorException($"'#elif' after '#else' (line {line})."); return true; }
                    if (f.ParentActive && !f.Taken)
                    {
                        bool v = Eval(argument, symbols, line, throwOnError);
                        f.Active = v; f.Taken = v;
                    }
                    else f.Active = false;
                    return true;
                }
                case "else":
                {
                    var f = Top("else", line, throwOnError);
                    if (f == null) return true;
                    if (f.SawElse) { if (throwOnError) throw new PreprocessorException($"A second '#else' for the '#if' of line {f.Line} (line {line})."); return true; }
                    f.SawElse = true;
                    f.Active = f.ParentActive && !f.Taken;
                    f.Taken = true;
                    return true;
                }
                case "endif":
                    if (_frames.Count == 0) { if (throwOnError) throw new PreprocessorException($"'#endif' without '#if' (line {line})."); return true; }
                    _frames.Pop();
                    return true;
                case "define":
                case "undef":
                {
                    if (!Active) return true;
                    if (!NameOnlyOk(argument)) { if (throwOnError) throw new PreprocessorException($"'#{name}' expects a name (line {line})."); return true; }
                    if (name.Equals("define", StringComparison.OrdinalIgnoreCase)) symbols.Add(argument); else symbols.Remove(argument);
                    return true;
                }
                case "error":
                    if (Active && throwOnError) throw new PreprocessorException($"#error {argument} (line {line})");
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Every '#if' of the file has to be closed in it.</summary>
        public void EnsureClosed()
        {
            if (_frames.Count > 0) throw new PreprocessorException($"The '#if' of line {_frames.Peek().Line} has no '#endif'.");
        }

        private static bool NameOnlyOk(string s) => Regex.IsMatch(s, @"^[A-Za-z_][A-Za-z0-9_]*$");

        private void Push(bool condition, int line)
        {
            bool parent = Active;
            bool v = parent && condition;
            _frames.Push(new Frame { ParentActive = parent, Taken = v, Active = v, Line = line });
        }

        private Frame? Top(string directive, int line, bool throwOnError)
        {
            if (_frames.Count > 0) return _frames.Peek();
            if (throwOnError) throw new PreprocessorException($"'#{directive}' without '#if' (line {line}).");
            return null;
        }

        // the caller has made sure that the condition is read (the enclosing branch is taken): in a branch that is not taken it may be wrong
        private bool Eval(string expression, ISet<string> symbols, int line, bool throwOnError)
        {
            try { return ConditionalSymbols.Evaluate(expression, symbols, line); }
            catch (PreprocessorException) when (!throwOnError) { return false; }
        }

        private bool NameCondition(string argument, string directive, ISet<string> symbols, int line, bool throwOnError)
        {
            if (!NameOnlyOk(argument))
            {
                if (throwOnError) throw new PreprocessorException($"'#{directive}' expects a name (line {line}).");
                return false;
            }
            return symbols.Contains(argument);
        }
    }
}
