using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Runtime;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>
    /// Overloading by parameter type (SPEC 5.4.3): methods (and constructors) of the same name may have the same number of parameters when their
    /// parameter types differ. A call takes the overload whose parameters fit the given values most closely - per argument: the exact basic type or
    /// class first, then a base class (the closer, the better), then an interface, then a type parameter, then `class` (any object), then an untyped (`any`) parameter.
    /// An `int` goes into a `float` parameter, but an exact `float` beats that.
    ///
    /// Every parameter of a method has a descriptor (FunctionProto.ParamTypes), made by the compiler:
    /// <list type="bullet">
    /// <item>"" - untyped, `var`, `any`, `ref`: takes everything</item>
    /// <item>"k:int", "k:float", "k:bool", "k:char", "k:string", "k:lambda", "k:array", "k:class": a basic type (`class`: any object)</item>
    /// <item>"c:Name": a class (or a class derived from it), "i:Name": an interface</item>
    /// <item>"g:" + DeclaredType text: a type parameter (of the class or of the method; its `where` conditions and a known type argument apply)</item>
    /// </list>
    /// The native backend (fire_rt.hpp) scores the same way.
    /// </summary>
    public static class Overloads
    {
        public const int NoMatch = -1;

        // the ranks (higher = closer); a class at distance d from the class of the object scores ClassExact - d
        public const int AnyScore = 10;
        public const int GenericScore = 20;
        public const int ObjectScore = 15;
        public const int InterfaceScore = 30;
        public const int ClassExact = 1000;
        public const int ExactKind = 1000;
        public const int IntToFloat = 900;
        public const int UndefinedScore = 1;

        /// <summary>How closely the value fits a parameter with this descriptor (NoMatch: not at all).</summary>
        public static int Score(in Value value, string? descriptor, ObjectInstance? owner = null, string[]? callTypeArgs = null)
        {
            if (string.IsNullOrEmpty(descriptor)) return value.Kind == ValueKind.Undefined ? UndefinedScore : AnyScore;
            if (value.Kind == ValueKind.Undefined) return UndefinedScore;   // (undefined goes everywhere; the order of the overloads decides)
            switch (descriptor[0])
            {
                case 'k':
                    switch (descriptor)
                    {
                        case "k:int": return value.Kind == ValueKind.Int ? ExactKind : NoMatch;
                        case "k:float": return value.Kind == ValueKind.Float ? ExactKind : value.Kind == ValueKind.Int ? IntToFloat : NoMatch;
                        case "k:bool": return value.Kind == ValueKind.Bool ? ExactKind : NoMatch;
                        case "k:char": return value.Kind == ValueKind.Char ? ExactKind : NoMatch;
                        case "k:string": return value.Kind == ValueKind.String ? ExactKind : NoMatch;
                        case "k:lambda": return value.Kind == ValueKind.Lambda ? ExactKind : NoMatch;
                        case "k:array": return value.Kind is ValueKind.Array or ValueKind.Buffer ? ExactKind : NoMatch;
                        case "k:class": return value.Kind == ValueKind.Class ? ObjectScore : NoMatch;
                    }
                    return AnyScore;
                case 'c':
                {
                    if (value.Kind != ValueKind.Class || value.AsObjectRef() is not ObjectInstance obj) return NoMatch;
                    var name = descriptor.AsSpan(2);
                    int distance = 0;
                    for (var rc = obj.RtClass; rc != null; rc = rc.Base, distance++)
                        if (name.SequenceEqual(rc.Name)) return ClassExact - distance;
                    return NoMatch;
                }
                case 'i':
                {
                    var name = descriptor.Substring(2);
                    if (GenericClassNames.PlainName(name) == "IEnumerable" && value.Kind is ValueKind.Array or ValueKind.Buffer or ValueKind.String) return InterfaceScore;
                    if (value.Kind != ValueKind.Class || value.AsObjectRef() is not ObjectInstance obj) return NoMatch;
                    for (var rc = obj.RtClass; rc != null; rc = rc.Base)
                        if (rc.Interfaces.Contains(name)) return InterfaceScore;
                    return NoMatch;
                }
                case 'g':
                    if (descriptor.Length == 2) return GenericScore;
                    return DeclaredType.Fits(value, descriptor.Substring(2), owner, callTypeArgs) ? GenericScore : NoMatch;
            }
            return AnyScore;
        }

        /// <summary>Is this a descriptor that takes everything (untyped or a type parameter)? Such a parameter overrides any parameter of a base class.</summary>
        public static bool IsLoose(string? descriptor) => string.IsNullOrEmpty(descriptor) || descriptor[0] == 'g';

        /// <summary>Does a method of a derived class with these parameter types override the one of a base class (instead of standing next to it as
        /// another overload)? The same number of parameters, and per parameter the same type, or an untyped one / a type parameter on either side.</summary>
        public static bool Overrides(FunctionProto derived, FunctionProto baseProto)
        {
            if (derived.ParamCount != baseProto.ParamCount) return false;
            for (int i = 0; i < derived.ParamCount; i++)
            {
                string? a = TypeAt(derived, i), b = TypeAt(baseProto, i);
                if (a == b || IsLoose(a) || IsLoose(b)) continue;
                return false;
            }
            return true;
        }

        /// <summary>Do two overloads of one class have the same signature (an error: the second one could never be called)?</summary>
        public static bool SameSignature(FunctionProto a, FunctionProto b)
        {
            if (a.ParamCount != b.ParamCount) return false;
            for (int i = 0; i < a.ParamCount; i++)
            {
                string? x = TypeAt(a, i), y = TypeAt(b, i);
                if (IsLoose(x) && IsLoose(y)) continue;
                if (x != y) return false;
            }
            return true;
        }

        public static string? TypeAt(FunctionProto proto, int index) => proto.ParamTypes is { } types && index < types.Length ? types[index] : null;

        /// <summary>The overload that fits the arguments most closely; the candidates come in their order of preference for a tie (the most derived class
        /// first, in it an exact number of parameters before one that fills in default values). Null when no candidate takes the arguments (the caller
        /// then uses the first candidate, whose checks report the mismatch). `typeArgCount` &gt; 0: the call names type arguments (`M&lt;string&gt;(x)`) -
        /// only generic methods with that many type parameters are meant.</summary>
        public static int Select(IReadOnlyList<FunctionProto> candidates, ReadOnlySpan<Value> args, ObjectInstance? owner, string[]? callTypeArgs, int typeArgCount = 0)
        {
            int best = -1;
            long bestScore = long.MinValue;
            for (int c = 0; c < candidates.Count; c++)
            {
                var proto = candidates[c];
                if (typeArgCount > 0 && proto.TypeParamCount != typeArgCount) continue;
                long total = 0;
                bool fits = true;
                for (int i = 0; i < args.Length && i < proto.ParamCount; i++)
                {
                    int score = Score(args[i], TypeAt(proto, i), owner, proto.TypeParamCount > 0 ? callTypeArgs : null);
                    if (score == NoMatch) { fits = false; break; }
                    total += score;
                }
                if (!fits) continue;
                if (total > bestScore)
                {
                    best = c;
                    bestScore = total;
                }
            }
            return best;
        }
    }
}

namespace fire.Bytecode
{
    /// <summary>
    /// The overload a call takes when the type checker could tell it from the static types of the arguments (ResolveResult.OverloadHints, emitted as
    /// OpCode.OverloadHint right before the call): the method (its class, name and position among the methods of that name there; for a constructor its
    /// position among the constructors with as many parameters) and per argument what the choice assumed of it. At run time the arguments are checked
    /// against the assumptions (a kind, a class, an interface - one test each); when they hold, the overload is taken without scoring the candidates,
    /// otherwise the call scores them as usual. So a static type that turns out wrong (a lax program, a value from code that was not checked) only
    /// costs the shortcut, never the right choice.
    /// </summary>
    public sealed class OverloadHint
    {
        public const string ConstructorName = "#new";
        private const char Separator = '\u001f';

        private enum Need : byte { Any, Undefined, Int, Float, Bool, Char, String, Lambda, Array, Object, Class, Interface }

        public string DeclaringClass { get; }
        public string Method { get; }
        public int Ordinal { get; }
        public int ParamCount { get; }
        public int Argc { get; }
        public string[] Assumptions { get; }
        private readonly Need[] _needs;
        public bool IsConstructor => Method == ConstructorName;

        private OverloadHint(string[] parts)
        {
            DeclaringClass = parts[0];
            Method = parts[1];
            Ordinal = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            ParamCount = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            Argc = int.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);
            Assumptions = parts.AsSpan(5).ToArray();
            _needs = new Need[Assumptions.Length];
            for (int i = 0; i < Assumptions.Length; i++)
                _needs[i] = Assumptions[i] switch
                {
                    "" => Need.Any, "u" => Need.Undefined, "k:int" => Need.Int, "k:float" => Need.Float, "k:bool" => Need.Bool, "k:char" => Need.Char,
                    "k:string" => Need.String, "k:lambda" => Need.Lambda, "k:array" => Need.Array, "k:class" => Need.Object,
                    var a when a.StartsWith("i:", StringComparison.Ordinal) => Need.Interface,
                    _ => Need.Class,
                };
        }

        public static string Format(string declaringClass, string method, int ordinal, int paramCount, int argc, IEnumerable<string> assumptions) =>
            string.Join(Separator, new[] { declaringClass, method, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                paramCount.ToString(System.Globalization.CultureInfo.InvariantCulture), argc.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Concat(assumptions));

        public static OverloadHint Parse(string text) => new(text.Split(Separator));

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, OverloadHint> s_parsed = new();

        /// <summary>The hint of a constant text (parsed once per text object).</summary>
        public static OverloadHint Of(string text) => s_parsed.GetValue(text, Parse);

        /// <summary>Do the arguments meet what the choice assumed?</summary>
        public bool Holds(ReadOnlySpan<Value> args)
        {
            if (args.Length != Argc) return false;
            for (int i = 0; i < args.Length; i++)
            {
                ref readonly var v = ref args[i];
                bool ok = _needs[i] switch
                {
                    Need.Any => true,
                    Need.Undefined => v.Kind == ValueKind.Undefined,
                    Need.Int => v.Kind == ValueKind.Int,
                    Need.Float => v.Kind == ValueKind.Float,
                    Need.Bool => v.Kind == ValueKind.Bool,
                    Need.Char => v.Kind == ValueKind.Char,
                    Need.String => v.Kind == ValueKind.String,
                    Need.Lambda => v.Kind == ValueKind.Lambda,
                    Need.Array => v.Kind is ValueKind.Array or ValueKind.Buffer,
                    Need.Object => v.Kind == ValueKind.Class,
                    _ => v.Kind != ValueKind.Undefined && Overloads.Score(v, Assumptions[i]) != Overloads.NoMatch,
                };
                if (!ok) return false;
            }
            return true;
        }

        private sealed record Entry(RuntimeClass Rc, FunctionProto? Proto, RuntimeClass? Declaring);
        private volatile Entry? _last;

        /// <summary>The overload on an object of class `rc` (its own override of the method, if it has one; for a constructor `rc` is the class
        /// itself); null when it cannot be found (then the call scores the candidates). The last class is remembered.</summary>
        public (FunctionProto? Proto, RuntimeClass? Declaring) Resolve(RuntimeClass rc, Func<string, RuntimeClass?> classByName)
        {
            var last = _last;
            if (last != null && ReferenceEquals(last.Rc, rc)) return (last.Proto, last.Declaring);
            var (proto, declaring) = Find(rc, classByName);
            _last = new Entry(rc, proto, declaring);
            return (proto, declaring);
        }

        private (FunctionProto?, RuntimeClass?) Find(RuntimeClass rc, Func<string, RuntimeClass?> classByName)
        {
            if (IsConstructor)
            {
                if (rc.Name != DeclaringClass || !rc.Constructors.TryGetValue(ParamCount, out var first)) return (null, null);
                var group = new List<FunctionProto> { first };
                group.AddRange(rc.ExtraConstructors.Where(c => c.ParamCount == ParamCount));
                return Ordinal < group.Count ? (group[Ordinal], rc) : (null, null);
            }
            // the receiver must be of the class the hint was made for (or derived from it)
            bool derives = false;
            for (var c = rc; c != null; c = c.Base) if (c.Name == DeclaringClass) { derives = true; break; }
            if (!derives || classByName(DeclaringClass) is not { } declaringRc || !declaringRc.Methods.TryGetValue(Method, out var list) || Ordinal >= list.Count)
                return (null, null);
            var chosen = list[Ordinal];
            var set = rc.Candidates(Method, Argc);
            for (int i = 0; i < set.Count; i++)
                if (ReferenceEquals(set.Protos[i], chosen)) return (chosen, set.Declaring[i]);
            // overridden in the class of the object (with the same parameter types)
            for (int i = 0; i < set.Count; i++)
                if (set.Protos[i].ParamCount == chosen.ParamCount && Overloads.SameSignature(set.Protos[i], chosen)) return (set.Protos[i], set.Declaring[i]);
            return (null, null);
        }
    }
}
