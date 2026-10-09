using System;

namespace fire.Ast
{
    /// <summary>
    /// Internal names for generic classes that carry the same name as a
    /// NON-generic class (SPEC "Generic classes": `class
    /// Box { }` and `class Box&lt;T&gt; { }` may exist side by side,
    /// as in C#). All tables in Resolver/Compiler/VM are keyed by
    /// class name - therefore in such a case the GENERIC class gets
    /// the name `Box`1` (name + '`' + number of
    /// type parameters, likewise as in C#); the non-generic one keeps
    /// its name. A generic class WITHOUT a non-generic one of the same name
    /// keeps its normal name - it is renamed only in case of a real collision
    /// (see Parser.DisambiguateGenericClasses), so existing code
    /// sees no difference.
    ///
    /// It is referenced only via `new Box&lt;Arg&gt;(...)` - the number of
    /// type arguments selects the class (see <see cref="ResolveNewTarget"/>);
    /// every place without type arguments (type annotation, `is of Box`,
    /// `catch (Box e)`, `Box.Static`, base class) means the
    /// non-generic class.
    /// </summary>
    public static class GenericClassNames
    {
        public static string Mangle(string name, int typeParamCount) => name + "`" + typeParamCount;

        /// <summary>The name without the `'`N suffix - for error messages.</summary>
        public static string PlainName(string name)
        {
            int i = name.IndexOf('`');
            return i < 0 ? name : name.Substring(0, i);
        }

        /// <summary>Resolves the target of a `new Name&lt;A1,...,An&gt;(...)` to the
        /// name of the class being instantiated: the generic
        /// class with `typeArgCount` type parameters, if under that
        /// name there is one with a renamed key (see above), otherwise -
        /// as before - the class with the ordinary name (a
        /// violation of its type parameters is then reported by the resolver).
        /// `isKnown`: the same name check that TypeRef.
        /// ResolveBaseName gets for ordinary references.</summary>
        public static string ResolveNewTarget(TypeRef classRef, int typeArgCount, Func<string, bool> isKnown)
        {
            if (typeArgCount == 0)
                return classRef.ResolveBaseName(isKnown);

            string plain = classRef.ResolveBaseName(n => isKnown(n) || isKnown(Mangle(n, typeArgCount)));
            string mangled = Mangle(plain, typeArgCount);
            return isKnown(mangled) ? mangled : plain;
        }
    }
}
