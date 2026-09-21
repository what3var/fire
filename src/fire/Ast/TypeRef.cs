using System;

namespace fire.Ast
{
    /// <summary>Signatur-Angabe für einen `lambda`-Typ (siehe TypeRef.LambdaSignature-
    /// Doku): `ReturnTypeName` ist der optionale, dem `lambda`-Schlüsselwort
    /// VORANGESTELLTE Rückgabetyp (`int lambda&lt;...&gt;`, null wenn weggelassen,
    /// `lambda&lt;...&gt;` allein); `ParamTypeNames` sind die Namen innerhalb der
    /// spitzen Klammern, leer wenn keine `&lt;...&gt;` angegeben (parameterlos).
    /// Beide sind reine NAMEN (kein rekursiver TypeRef) - ein Lambda-Parameter-
    /// oder Rückgabetyp, der selbst wieder ein Lambda-Typ mit eigener Signatur
    /// wäre, ist bewusst nicht unterstützt (SPEC-Grenze dieser Ausbaustufe).
    /// Nur die ANZAHL der Parameter wird zur Laufzeit geprüft (VM.
    /// CheckLambdaSignature) - die einzelnen Typnamen sind rein informativ,
    /// da eine dynamisch typisierte Lambda ihre Parameter-TYPEN nicht
    /// verlässlich vorab offenlegt.</summary>
    public sealed record LambdaSignature(string? ReturnTypeName, IReadOnlyList<string> ParamTypeNames);

    /// <summary>
    /// Ein Typ-Verweis: Basisname (Basistyp-Keyword oder Klassenname), optionale
    /// Bitbreite in Klammern direkt hinter dem Typ (nur für int/float sinnvoll,
    /// z.B. `int[16]`) und Pointer-Tiefe (Anzahl '*', z.B. `int[16]*`).
    ///
    /// Array-Deklaratoren ("Type name[]") sind bewusst NICHT Teil von TypeRef,
    /// sondern hängen als eigenes Feld an der jeweiligen Deklaration
    /// (VarDeclStmt/FieldDecl/LambdaParam) - die Sprache platziert die eckigen
    /// Klammern für Arrays hinter dem BEZEICHNER, nicht hinter dem Typ (anders als
    /// die Bitbreiten-Klammern, die direkt hinter dem Typ stehen). Beide Syntaxen
    /// sind dadurch rein positionell unterscheidbar, keine Mehrdeutigkeit.
    ///
    /// LambdaSignature: gesetzt, wenn dieser TypeRef ein Lambda-Typ ist
    /// (`BaseName == "lambda"`) - `[RückgabeTyp] lambda[&lt;Param1,...,ParamN&gt;]`,
    /// siehe LambdaSignature-Doku und SPEC "Lambda-Typen mit Signatur".
    /// </summary>
    public sealed record TypeRef(string BaseName, int? BitWidth, int PointerDepth, LambdaSignature? LambdaSignature = null, IReadOnlyList<string>? Namespaces = null)
    {
        public bool IsPointer => PointerDepth > 0;

        /// <summary>Löst BaseName auf seinen tatsächlichen, vollqualifizierten
        /// Namen auf, WENN nötig (SPEC "Namespaces") - `isKnown` prüft, ob ein
        /// Kandidatenname bekannt ist (Resolver: IsKnownClassName, Compiler:
        /// gegen die Menge aller RuntimeClass-Namen, Parser.MergeClassExtensions:
        /// gegen die Namen im gerade kombinierten Programm).
        ///
        /// `Namespaces` steht an ERSTER Stelle der aktuelle Namespace (falls
        /// beim Parsen einer war), danach die zum Zeitpunkt des Parsens
        /// aktiven `#using`-Namen (siehe Parser._currentNamespace/
        /// _usingNamespaces) - die Reihenfolge selbst kodiert bereits die
        /// Priorität (aktueller Namespace vor `#using`), kein separater
        /// "Geschwister gewinnt"-Sonderfall nötig: einfach den ERSTEN
        /// passenden Kandidaten nehmen.
        ///
        /// Schon ein exakt bekannter Name (inkl. vom Nutzer selbst
        /// vollqualifiziert geschrieben, oder ein nicht-namespacierter
        /// globaler Name wie 'Exception') hat Vorrang vor jeder
        /// Namespace-Kombination. Kein Kandidat bekannt -> BaseName
        /// unverändert zurück, schlägt beim Aufrufer dann wie gewohnt als
        /// "unbekannte Klasse/unbekannter Typ" fehl.</summary>
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
