using System;
using System.Text;
using fire.Values;

namespace fire.Standard
{
    /// <summary>The methods of `char` (SPEC 8.12) - IDs for <see cref="CharMethods.NativeName"/>,
    /// fixed values as with <see cref="StringMethod"/>.</summary>
    public enum CharMethod
    {
        IsDigit = 1,
        IsLetter = 2,
        IsLetterOrDigit = 3,
        IsWhiteSpace = 4,
        IsUpper = 5,
        IsLower = 6,
        ToUpper = 7,
        ToLower = 8,
        ToString = 9,
        ToInt = 10,
    }

    /// <summary>
    /// `char` methods as an extension of the base type (`class extends char`, SPEC 5.5.1) in the prelude, all
    /// via ONE native function: `__CharCall(id, char)`. Classification follows Unicode (like
    /// .NET's `char.IsLetter` etc., on the single 16-bit code unit), upper/lower case is
    /// invariant. (`ToByte()`/`ToUnicode(n)` remain the built-in conversions, SPEC 8.10.)
    /// </summary>
    public static class CharMethods
    {
        public const string NativeName = "__CharCall";

        private static readonly (CharMethod Id, string Returns, string Name)[] Signatures =
        {
            (CharMethod.IsDigit, "bool", "IsDigit"),
            (CharMethod.IsLetter, "bool", "IsLetter"),
            (CharMethod.IsLetterOrDigit, "bool", "IsLetterOrDigit"),
            (CharMethod.IsWhiteSpace, "bool", "IsWhiteSpace"),
            (CharMethod.IsUpper, "bool", "IsUpper"),
            (CharMethod.IsLower, "bool", "IsLower"),
            (CharMethod.ToUpper, "char", "ToUpper"),
            (CharMethod.ToLower, "char", "ToLower"),
            (CharMethod.ToString, "string", "ToString"),
            (CharMethod.ToInt, "int", "ToInt"),
        };

        public static string PreludeSource { get; } = BuildPreludeSource();

        private static string BuildPreludeSource()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("// char als Basistyp erweitert (SPEC 5.5.1, 8.12): eine native Funktion " + NativeName + "(id, char).");
            sb.AppendLine("class extends char {");
            foreach (var (id, returns, name) in Signatures)
                sb.AppendLine($"    {returns} {name}() {{ return {NativeName}({(int)id}, this) }}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>The native function `__CharCall(id, char)`.</summary>
        public static Value Call(Value[] args)
        {
            if (args.Length != 2 || args[1].Kind != ValueKind.Char)
                throw new InvalidOperationException($"{NativeName}(id, char) expects exactly one character as the second argument.");

            char c = args[1].AsChar();
            switch ((CharMethod)args[0].AsInt())
            {
                case CharMethod.IsDigit: return Value.MakeBool(char.IsDigit(c));
                case CharMethod.IsLetter: return Value.MakeBool(char.IsLetter(c));
                case CharMethod.IsLetterOrDigit: return Value.MakeBool(char.IsLetterOrDigit(c));
                case CharMethod.IsWhiteSpace: return Value.MakeBool(char.IsWhiteSpace(c));
                case CharMethod.IsUpper: return Value.MakeBool(char.IsUpper(c));
                case CharMethod.IsLower: return Value.MakeBool(char.IsLower(c));
                case CharMethod.ToUpper: return Value.MakeChar(char.ToUpperInvariant(c));
                case CharMethod.ToLower: return Value.MakeChar(char.ToLowerInvariant(c));
                case CharMethod.ToString: return Value.MakeString(c.ToString());
                case CharMethod.ToInt: return Value.MakeInt(c);
                default:
                    throw new InvalidOperationException($"{NativeName}: unknown method ID {args[0].AsInt()}.");
            }
        }
    }
}
