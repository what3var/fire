using System;
using MemoryPack;

namespace fire.Ast
{
    /// <summary>Signature information for a `lambda` type (see TypeRef.LambdaSignature
    /// docs): `ReturnTypeName` is the optional return type placed
    /// BEFORE the `lambda` keyword (`int lambda&lt;...&gt;`, null if omitted,
    /// `lambda&lt;...&gt;` alone); `ParamTypeNames` are the names inside the
    /// angle brackets, empty if no `&lt;...&gt;` given (parameterless).
    /// Both are pure NAMES (no recursive TypeRef) - a lambda parameter
    /// or return type that were itself a lambda type with its own signature
    /// is deliberately not supported (SPEC limit of this stage).
    /// `IsSelector`: `lambda member&lt;T&gt; name` - the passed lambda selects a member (`c => c.radius`); in its body the parameter
    /// instead holds the reflection of this member (see docs/DESIGN_LAMBDA_REFLECTION_PROBE.md); `ParamTypeNames`
    /// then contains exactly the name `T`. `SelectorKind` determines what the lambda may select:
    /// `field` (a field only), `property` (a property only), `member` (field or property), `method` (a method only), `selector` (anything).
    /// Only the NUMBER of parameters is checked at runtime (VM.
    /// CheckLambdaSignature) - the individual type names are purely informational,
    /// since a dynamically typed lambda cannot reliably disclose its parameter TYPES
    /// up front.</summary>
    [MemoryPackable]
    public sealed partial record LambdaSignature(string? ReturnTypeName, IReadOnlyList<string> ParamTypeNames, bool IsSelector = false, string SelectorKind = "member");

    /// <summary>
    /// A type reference: base name (base-type keyword or class name), optional
    /// bit width in brackets directly after the type (only meaningful for int/float,
    /// e.g. `int[16]`) and pointer depth (number of '*', e.g. `int[16]*`).
    ///
    /// Array declarators ("Type name[]") are deliberately NOT part of TypeRef,
    /// but hang as a field of their own on the respective declaration
    /// (VarDeclStmt/FieldDecl/LambdaParam) - the language places the square
    /// brackets for arrays after the IDENTIFIER, not after the type (unlike
    /// the bit-width brackets, which sit directly after the type). Both syntaxes
    /// can thereby be told apart purely positionally, no ambiguity.
    ///
    /// ONE exception: a RETURN TYPE has no identifier after which the brackets
    /// could stand - there one writes `int[] Name()` or `Dog[][] Name()`. The
    /// EMPTY brackets distinguish this from the bit width (`int[8]`, always with a
    /// number). `ArrayRank` counts these bracket pairs (0 = no array).
    ///
    /// LambdaSignature: set if this TypeRef is a lambda type
    /// (`BaseName == "lambda"`) - `[ReturnType] lambda[&lt;Param1,...,ParamN&gt;]`,
    /// see LambdaSignature docs and SPEC "Lambda types with signature".
    /// </summary>
    [MemoryPackable]
    public sealed partial record TypeRef(string BaseName, int? BitWidth, int PointerDepth, LambdaSignature? LambdaSignature = null, IReadOnlyList<string>? Namespaces = null, string? Unit = null, int ArrayRank = 0, int TypeArgCount = 0)
    {
        /// <summary>Sentinel for `BaseName` when a declaration uses `var`
        /// together with an EXPLICIT unit, but WITHOUT an explicit type
        /// (`var a : mm`) - the actual type remains, as with
        /// ordinary `var`, inferred from the initialiser/context,
        /// ONLY the unit is already fixed here (see SPEC
        /// "Unit declarations"). A TypeRef with this BaseName
        /// NEVER has a meaning of its own as a type name - every place that
        /// would validate/resolve `TypeRef.BaseName` as a real type name
        /// (ValidateTypeName/ResolveTypeRef in the resolver, likewise
        /// in the compiler) must check `IsInferred` first and in that case
        /// validate ONLY `Unit`, not `BaseName`.</summary>
        public const string InferredMarker = "var";

        /// <summary>`true` if this TypeRef fixes ONLY a unit,
        /// but has the actual type (like normal `var`) inferred from the context
        /// (see InferredMarker docs).</summary>
        [MemoryPackIgnore]
        public bool IsInferred => BaseName == InferredMarker;

        [MemoryPackIgnore]
        public bool IsPointer => PointerDepth > 0;

        /// <summary>Resolves BaseName to its actual, fully qualified
        /// name IF necessary (SPEC "Namespaces") - `isKnown` checks whether a
        /// candidate name is known (resolver: IsKnownClassName, compiler:
        /// against the set of all RuntimeClass names, Parser.MergeClassExtensions:
        /// against the names in the currently combined program).
        ///
        /// `Namespaces` has the current namespace in FIRST place (if
        /// there was one when parsing), then the `#using` names that were
        /// active at parse time (see Parser._currentNamespace/
        /// _usingNamespaces) - the order itself already encodes the
        /// priority (current namespace before `#using`), no separate
        /// "sibling wins" special case needed: simply take the FIRST
        /// matching candidate.
        ///
        /// Even an exactly known name (including one written fully
        /// qualified by the user, or a non-namespaced
        /// global name like 'Exception') takes precedence over any
        /// namespace combination. No candidate known -> BaseName
        /// returned unchanged, then fails at the caller as usual as an
        /// "unknown class/unknown type".</summary>
        public string ResolveBaseName(Func<string, bool> isKnown)
        {
            if (isKnown(BaseName)) return BaseName;
            if (Namespaces != null)
                foreach (var ns in Namespaces)
                {
                    string candidate = ns + "." + BaseName;
                    if (isKnown(candidate)) return candidate;
                }
            return BaseName;
        }

        public override string ToString()
        {
            if (LambdaSignature != null)
            {
                string ret = LambdaSignature.ReturnTypeName != null ? LambdaSignature.ReturnTypeName + " " : "";
                string ps = LambdaSignature.ParamTypeNames.Count > 0
                    ? "<" + string.Join(", ", LambdaSignature.ParamTypeNames) + ">"
                    : "";
                return ret + "lambda" + ps;
            }
            string s = BitWidth != null ? $"{BaseName}[{BitWidth}]" : BaseName;
            return s + new string('*', PointerDepth);
        }
    }

}
