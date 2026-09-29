using System;

namespace fire.Ast
{
    /// <summary>
    /// Interne Namen für generische Klassen, die denselben Namen wie eine
    /// NICHT-generische Klasse tragen (SPEC "Generische Klassen": `class
    /// Box { }` und `class Box&lt;T&gt; { }` dürfen nebeneinander existieren,
    /// wie in C#). Alle Tabellen in Resolver/Compiler/VM sind nach dem
    /// Klassennamen geschlüsselt - deshalb bekommt die GENERISCHE Klasse in
    /// so einem Fall den Namen `Box`1` (Name + '`' + Anzahl der
    /// Typ-Parameter, ebenfalls wie in C#); die nicht-generische behält
    /// ihren Namen. Eine generische Klasse OHNE gleichnamige nicht-generische
    /// behält ihren normalen Namen - nur bei einer echten Kollision wird
    /// umbenannt (siehe Parser.DisambiguateGenericClasses), bestehender Code
    /// sieht also keinen Unterschied.
    ///
    /// Referenziert wird sie nur über `new Box&lt;Arg&gt;(...)` - die Anzahl der
    /// Typ-Argumente wählt die Klasse (siehe <see cref="ResolveNewTarget"/>);
    /// jede Stelle ohne Typ-Argumente (Typ-Annotation, `is of Box`,
    /// `catch (Box e)`, `Box.Statisch`, Basisklasse) meint die
    /// nicht-generische Klasse.
    /// </summary>
    public static class GenericClassNames
    {
        public static string Mangle(string name, int typeParamCount) => name + "`" + typeParamCount;

        /// <summary>Der Name ohne `'`N`-Suffix - für Fehlermeldungen.</summary>
        public static string PlainName(string name)
        {
            int i = name.IndexOf('`');
            return i < 0 ? name : name.Substring(0, i);
        }

        /// <summary>Löst das Ziel eines `new Name&lt;A1,...,An&gt;(...)` auf den
        /// Namen der Klasse auf, die instanziiert wird: die generische
        /// Klasse mit `typeArgCount` Typ-Parametern, falls es unter dem
        /// Namen eine mit umbenanntem (siehe oben) Schlüssel gibt, sonst -
        /// wie bisher - die Klasse mit dem gewöhnlichen Namen (ein
        /// Verstoß gegen deren Typ-Parameter meldet dann der Resolver).
        /// `isKnown`: dieselbe Namens-Prüfung, die auch TypeRef.
        /// ResolveBaseName für gewöhnliche Referenzen bekommt.</summary>
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
