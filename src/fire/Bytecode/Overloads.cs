using System;
using System.Collections.Generic;
using fire.Ast;
using fire.Runtime;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>
    /// Overloading by parameter type (SPEC 5.4.3): methods (and constructors) of the same name may have the same number of parameters when their
    /// parameter types differ. A call takes the overload whose parameters fit the given values most closely - per argument: the exact basic type or
    /// class first, then a base class (the closer, the better), then an interface, then a type parameter, then an untyped (`any`) parameter.
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
        public const int ObjectScore = 25;
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
