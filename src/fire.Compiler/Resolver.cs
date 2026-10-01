using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Resolving;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>Ein Resolver-Fehler. Der Resolver bricht beim ersten Fehler
    /// NICHT ab, sondern sammelt alle weiteren mit (das Ergebnis ist ab dem
    /// ersten Fehler ohnehin verworfen, der Nutzer soll aber nicht einen
    /// Fehler nach dem anderen beheben müssen): `Resolver.Resolve` wirft am
    /// Ende EINE ResolverException, deren `Message`/`Line` die des ersten
    /// Fehlers sind (wie bisher) und deren <see cref="Errors"/> ALLE
    /// gefundenen Fehler in Quelltext-Reihenfolge der Auflösung enthält
    /// (der erste eingeschlossen). Eine einzeln geworfene Exception (intern,
    /// vor dem Sammeln) hat nur sich selbst als `Errors`.</summary>
    public sealed class ResolverException : Exception
    {
        public int Line { get; }

        public IReadOnlyList<ResolverException> Errors { get; }

        public ResolverException(string message, int line)
            : base($"{message} ({line})")
        {
            Line = line;
            Errors = new[] { this };
        }

        /// <summary>Fasst mehrere gesammelte Fehler zusammen (mindestens einer).</summary>
        public ResolverException(IReadOnlyList<ResolverException> errors)
            : base(errors[0].Message)
        {
            Line = errors[0].Line;
            Errors = errors;
        }
    }

    /// <summary>Ergebnis eines Resolver-Laufs: alles, was der Evaluator (und später
    /// der Bytecode-Compiler) braucht, um nicht mehr per Namen suchen zu müssen.</summary>
    public sealed class ResolveResult
    {
        public required IReadOnlyDictionary<Expr, ResolvedRef> References { get; init; }
        public required IReadOnlyDictionary<string, ClassDecl> Classes { get; init; }
        public required int GlobalSlotCount { get; init; }
        public required IReadOnlyDictionary<string, ExternDecl> Externs { get; init; }

        /// <summary>`#noshadow` war IRGENDWO im Programm vorhanden (siehe
        /// Ast.NoShadowDirective/Resolver.ResolveFireStmt) - deaktiviert den
        /// Read-only-Globals-Snapshot in JEDEM `fire`-Block. Vom Compiler
        /// gelesen, um seinen `_globalSlotCount` effektiv auf 0 zu setzen
        /// (siehe dortige Doku) - das genügt allein schon, damit
        /// CompileFireStmt exakt wie vor Einführung des Snapshots
        /// kompiliert, ohne eigene bedingte Zweige dafür zu brauchen.</summary>
        public required bool NoShadowGlobals { get; init; }
    }

    /// <summary>
    /// Ein-Pass-Resolver über den AST. Zwei Aufgaben:
    ///
    /// 1) Variablen-Referenzen (IdentifierExpr, Zuweisungsziele) auf statische
    ///    Slots auflösen (Scope-Tiefe + Index), statt später zur Laufzeit per Name
    ///    durch die Scope-Kette zu suchen. Lambdas bekommen dabei absichtlich einen
    ///    Scope, dessen Parent direkt der globale Scope ist (nicht der lexikalisch
    ///    umschließende Scope) - das modelliert die eingeschränkte Lambda-
    ///    Sichtbarkeit (nur eigener Scope + global, SPEC 4.2) ganz von selbst, ohne
    ///    dass die Tiefen-Zählung beim Auflösen einen Sonderfall bräuchte.
    ///
    /// 2) Ein paar statische Prüfungen, die sich in diesem Pass anbieten:
    ///    - 'return' nur innerhalb einer Funktion/Methode/Lambda/Konstruktor/Destruktor.
    ///    - 'base' (als Ausdruck oder als Konstruktor-Initialisierer) nur innerhalb
    ///      einer Klasse, die tatsächlich eine Basisklasse hat.
    ///    - Referenzierte Klassennamen (Basisklasse, 'new X()', 'is of X', Typ-
    ///      Annotationen) müssen bekannt sein (deklariert oder die eingebaute
    ///      Basisklasse 'Exception').
    ///
    /// Bewusst NICHT geprüft: strikte Gültigkeit von 'this' (Lambdas können 'this'
    /// über 'on' auch außerhalb jeder Klasse sinnvoll nutzen, eine rein lexikalische
    /// Prüfung wäre hier eher einschränkend als hilfreich) - das überlassen wir der
    /// Laufzeit.
    /// </summary>
    public sealed class Resolver
    {
        /// <summary>Primitive/eingebaute Typnamen, die ValidateTypeName ohne
        /// Nachschlagen in `_classes` akzeptiert. 'lambda' steht für einen
        /// Lambda-Wert (Runtime.LambdaValue/ValueKind.Lambda), optional mit
        /// Signatur (`[RückgabeTyp] lambda[&lt;Param1,...,ParamN&gt;]`, siehe
        /// Ast.TypeRef.LambdaSignature/SPEC "Lambda-Typen mit Signatur") -
        /// Lambdas waren schon immer als WERTE übergebbar (dynamisch
        /// typisiert), aber bis jetzt gab es keinen Namen, um das (und ihre
        /// erwartete Parameterzahl) in einer Typ-Annotation auszudrücken.
        /// Bewusst NICHT 'func' (das Schlüsselwort, das einen Lambda-
        /// AUSDRUCK einleitet, `func (x) => ...`) - das würde mit der
        /// Ausdrucks-Syntax kollidieren: NextLooksLikeTypeThenName() prüft
        /// nur das AKTUELLE Token, ein alleinstehendes 'func(x) => ...' als
        /// Anweisung würde dann fälschlich als Beginn einer typisierten
        /// Deklaration gelesen (Typname 'func', erwarteter Name als
        /// nächstes) statt als Lambda-Ausdruck. 'lambda' selbst ist nur ein
        /// PLAIN Bezeichner (kein Keyword-Token, siehe Parser.
        /// NextLooksLikeTypeThenName), kollidiert also nur, wenn jemand
        /// tatsächlich etwas 'lambda' nennt - anders als 'func' kein
        /// bestehendes Sprachkonstrukt. Wie 'class' ist 'lambda' syntaktisch
        /// akzeptiert; ANDERS als sonstige Typ-Annotationen wird die
        /// Parameter-ANZAHL einer Signatur aber tatsächlich zur Laufzeit
        /// geprüft (VM.CheckLambdaSignature) - eine bewusste Ausnahme von
        /// SPEC 8.1 ("Typ-Annotationen nicht durchgängig geprüft"), weil
        /// sich das hier (anders als bei Klassen-Typen) mit vertretbarem
        /// Aufwand robust umsetzen ließ.</summary>
        private static readonly HashSet<string> PrimitiveTypeNames = new()
        {
            "bool", "int", "float", "char", "string", "class", "undefined", "lambda",
        };

        private readonly Dictionary<Expr, ResolvedRef> _refs = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, ClassDecl> _classes = new();

        /// <summary>Löst `tr` auf seinen vollqualifizierten Namen auf, WENN
        /// nötig (SPEC "Namespaces") - siehe TypeRef.ResolveBaseName für die
        /// genaue Regel. `tr.Namespaces` trägt den Kontext (aktueller
        /// Namespace + `#using`) schon direkt an sich selbst, gesetzt vom
        /// Parser GENAU an der Stelle, an der `tr` geparst wurde - der
        /// Resolver braucht dafür keinen eigenen "aktuelle Klasse"/"aktive
        /// Usings"-Zustand mehr.</summary>
        private string ResolveTypeRef(TypeRef tr) => tr.ResolveBaseName(IsKnownClassName);

        private readonly Dictionary<string, InterfaceDecl> _interfaces = new();
        private readonly Dictionary<string, ExternDecl> _externs = new();
        private readonly Dictionary<string, Dictionary<string, long>> _enums = new();
        private readonly HashSet<string> _nativeNames;
        private readonly HashSet<string> _tryableNativeNames;
        private readonly ResolverScope _globalScope = new(parent: null, isGlobal: true);

        private ResolverScope _current;
        private int _functionDepth;

        /// <summary>Alle bisher gefundenen Fehler (siehe ResolverException,
        /// RecoverFrom) - wird von Resolve() am Ende ausgewertet.</summary>
        private readonly List<ResolverException> _errors = new();
        private readonly HashSet<string> _errorMessages = new();

        /// <summary>Merkt sich einen Fehler (siehe ResolverException). Derselbe
        /// Fehler - gleiche Meldung, und die enthält die Zeile - kommt oft
        /// von mehreren Stellen (z.B. validieren Getter UND Setter einer
        /// Property beide deren Typ) und wird nur EINMAL gemerkt.</summary>
        private void AddError(ResolverException error)
        {
            if (_errorMessages.Add(error.Message))
                _errors.Add(error);
        }

        /// <summary>Siehe Ast.NoShadowDirective/ResolveResult.NoShadowGlobals
        /// - von Resolve() VOR jeder Statement-Auflösung in einem
        /// Vorab-Durchlauf gesetzt (wie bei Klassen/Enums/Externs), damit es
        /// unabhängig von der Position der `#noshadow`-Direktive im
        /// Quelltext gilt (auch für einen `fire`-Block, der VOR der
        /// Direktive im Quelltext steht).</summary>
        private bool _noShadowGlobals;

        /// <summary>Wie `_functionDepth`, aber für `break`/`continue`: Anzahl
        /// umschließender Schleifen (0 = kein `break`/`continue` gültig) bzw.
        /// `try`/`catch`/`finally`-Blöcke seit der letzten Schleife (>0 = ein
        /// `break`/`continue` hier müsste über eine try-Grenze hinweg
        /// springen - bewusst als Fehler abgelehnt, siehe ResolveTry-Doku).
        /// Beide werden beim Betreten einer neuen Funktion/Methode/Lambda
        /// GESICHERT UND AUF 0 ZURÜCKGESETZT (nicht einfach erhöht, wie
        /// `_functionDepth`) - eine Schleife der UMSCHLIESSENDEN Funktion
        /// darf aus einer verschachtelten Lambda heraus nicht per `break`
        /// erreichbar sein (andere Aufruf-/Scope-Ebene zur Laufzeit).</summary>
        private int _loopDepth;
        private int _tryDepth;
        private int _unsafeDepth;
        private ClassDecl? _currentClass;
        private bool _currentClassHasBase;

        /// <summary>Namen der generischen Typ-Parameter, die an der aktuellen
        /// Stelle sichtbar sind (Klassen-Typ-Parameter der umschließenden
        /// Klasse UNION Typ-Parameter der aktuell resolvten Methode, falls
        /// diese selbst generisch ist) - als Referenzzähler statt reinem
        /// HashSet, damit ein Methoden-Typ-Parameter mit ZUFÄLLIG demselben
        /// Namen wie ein Klassen-Typ-Parameter beim Verlassen der Methode
        /// nicht versehentlich auch den äußeren Namen entfernt (siehe
        /// AddTypeParamNames/RemoveTypeParamNames). ValidateTypeName
        /// akzeptiert diese Namen wie einen bekannten Typ (siehe dort),
        /// obwohl sie keine echte Klasse sind - eine echte Typ-Substitution
        /// findet NICHT statt (siehe SPEC "Generische Klassen"), `T` bleibt
        /// zur Laufzeit einfach unspezifisch.</summary>
        private readonly Dictionary<string, int> _currentTypeParamNames = new();

        private void AddTypeParamNames(IEnumerable<string> names)
        {
            foreach (var n in names)
                _currentTypeParamNames[n] = _currentTypeParamNames.GetValueOrDefault(n) + 1;
        }

        private void RemoveTypeParamNames(IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                int count = _currentTypeParamNames[n] - 1;
                if (count <= 0) _currentTypeParamNames.Remove(n);
                else _currentTypeParamNames[n] = count;
            }
        }
        private bool _inConstructor;
        /// <summary>SPEC "Statische Mitglieder" - `true` während der Body
        /// einer statischen Methode/eines statischen Feld-Initialisierers
        /// aufgelöst wird (siehe ResolveFunctionLike/ResolveFieldInitializer)
        /// - dort gibt es kein gebundenes 'this' (siehe ThisExpr/BaseExpr-
        /// Prüfung in ResolveExpr), anders als bei Instanzmethoden/-feldern.</summary>
        private bool _inStaticMethod;

        private Resolver(IEnumerable<string>? nativeNames, IEnumerable<string>? tryableNativeNames)
        {
            _current = _globalScope;
            _nativeNames = nativeNames != null ? new HashSet<string>(nativeNames) : new HashSet<string>();
            _tryableNativeNames = tryableNativeNames != null ? new HashSet<string>(tryableNativeNames) : new HashSet<string>();
        }

        /// <summary>`nativeNames`/`tryableNativeNames`: bekannte native
        /// Funktionsnamen (SPEC "Natives"). Anders als früher (siehe
        /// Bytecode.Compiler.Compile-Historie) KEIN `activeUsings`/
        /// `usingsByStmt`-Parameter mehr nötig - jede Typ-Referenz im
        /// AST trägt ihren eigenen Namespace-Kontext direkt an sich selbst
        /// (siehe Ast.TypeRef.Namespaces, gesetzt vom Parser beim Parsen),
        /// der Resolver braucht dafür keinen eigenen Usings-Zustand mehr.</summary>
        public static ResolveResult Resolve(
            IReadOnlyList<Stmt> program, IEnumerable<string>? nativeNames = null, IEnumerable<string>? tryableNativeNames = null)
        {
            var resolver = new Resolver(nativeNames, tryableNativeNames);
            resolver.CollectClasses(program);
            resolver.CollectExterns(program);
            resolver.CollectEnums(program);
            resolver._noShadowGlobals = program.Any(s => s is NoShadowDirective);
            foreach (var stmt in program)
                resolver.ResolveStmt(stmt);

            // Ab dem ersten Fehler steht fest, dass es kein Ergebnis gibt -
            // aber erst HIER, nachdem alles aufgelöst wurde, damit der
            // Aufrufer ALLE Fehler auf einmal bekommt (siehe ResolverException).
            if (resolver._errors.Count > 0)
                throw new ResolverException(resolver._errors);

            return new ResolveResult
            {
                References = resolver._refs,
                Classes = resolver._classes,
                GlobalSlotCount = resolver._globalScope.Slots.Count,
                Externs = resolver._externs,
                NoShadowGlobals = resolver._noShadowGlobals,
            };
        }

        // -----------------------------------------------------------
        // extern-Deklarationen vorab einsammeln (erlaubt Aufrufe vor der
        // Deklaration im Quelltext, wie bei Klassen).
        // -----------------------------------------------------------
        private void CollectExterns(IReadOnlyList<Stmt> statements)
        {
            foreach (var stmt in statements)
            {
                if (stmt is not ExternDecl ed) continue;
                // Eine gleichnamige registrierte native Funktion ist kein Konflikt,
                // sondern der Normalfall: 'extern' deklariert die Signatur im
                // Skript, die native Registry liefert die Implementierung dazu.
                if (_externs.ContainsKey(ed.Name))
                {
                    AddError(new ResolverException($"'{ed.Name}' ist bereits als extern deklariert", ed.Line));
                    continue;
                }
                _externs[ed.Name] = ed;
            }
        }

        /// <summary>Sammelt alle `enum`-Deklarationen vorab (erlaubt Vorwärts-
        /// referenzen wie bei Klassen/externs) und berechnet dabei direkt die
        /// tatsächlichen Int-Werte jedes Mitglieds: ein explizit angegebener
        /// Wert MUSS ein Int-Literal sein (echte Compile-Zeit-Konstanten-
        /// auswertung beliebiger Ausdrücke gibt es in dieser Sprache nicht),
        /// sonst Auto-Increment vom Vorgänger + 1 (0 beim ersten Mitglied).</summary>
        private void CollectEnums(IReadOnlyList<Stmt> statements)
        {
            foreach (var stmt in statements)
            {
                if (stmt is not EnumDecl ed) continue;
                if (_enums.ContainsKey(ed.Name))
                {
                    AddError(new ResolverException($"'{ed.Name}' ist bereits als enum deklariert", ed.Line));
                    continue;
                }
                if (IsKnownClassName(ed.Name))
                {
                    AddError(new ResolverException($"'{ed.Name}' ist bereits als Klasse deklariert", ed.Line));
                    continue;
                }

                // Der Name wird auch bei einem fehlerhaften MITGLIED registriert
                // (mit den bis dahin gültigen Mitgliedern) - sonst würde jede
                // Verwendung des Enums später als "Unbekannter Bezeichner"
                // gemeldet, ein reiner Folgefehler des einen echten Fehlers.
                var members = new Dictionary<string, long>();
                _enums[ed.Name] = members;
                long next = 0;
                foreach (var m in ed.Members)
                {
                    if (members.ContainsKey(m.Name))
                    {
                        AddError(new ResolverException($"Enum-Mitglied '{ed.Name}.{m.Name}' ist bereits deklariert", ed.Line));
                        continue;
                    }

                    long value;
                    if (m.ValueExpr != null)
                    {
                        if (m.ValueExpr is not LiteralExpr { Value.Kind: ValueKind.Int } lit)
                        {
                            AddError(new ResolverException(
                                $"Enum-Mitglied '{ed.Name}.{m.Name}': Wert muss ein Int-Literal sein " +
                                "(beliebige Ausdrücke werden hier nicht ausgewertet).", ed.Line));
                            members[m.Name] = next; // Mitglied bleibt bekannt (siehe oben)
                            next++;
                            continue;
                        }
                        value = lit.Value.AsInt();
                    }
                    else
                    {
                        value = next;
                    }

                    members[m.Name] = value;
                    next = value + 1;
                }
            }
        }

        // -----------------------------------------------------------
        // Klassen & Interfaces vorab einsammeln (erlaubt Vorwärtsreferenzen und
        // dass 'new Foo()' funktioniert, auch wenn 'Foo' erst später im
        // Quelltext deklariert wird). Da der Parser nicht wissen kann, welcher
        // Name nach ':' die Basisklasse und welche Interfaces sind (das ist
        // erst hier, mit Kenntnis aller Klassen-/Interface-Namen, entscheidbar),
        // wird das hier aufgelöst: höchstens einer der Namen darf eine echte
        // Klasse (oder 'Exception') sein, alle anderen müssen bekannte
        // Interfaces sein.
        // -----------------------------------------------------------
        /// <summary>Löst einen Basisklassen-/Interface-Namen (Eintrag aus
        /// ClassDecl.BaseRefs) auf seinen vollqualifizierten Namen auf, WENN
        /// nötig (SPEC "Namespaces") - `tr.Namespaces` trägt dafür den beim
        /// Parsen aktuellen Kontext (siehe TypeRef.ResolveBaseName).
        /// "Bekannt" heißt hier: 'Exception' ODER eine bekannte Klasse ODER
        /// ein bekanntes Interface (anders als ResolveTypeRef, das nur
        /// Klassen kennt - eine Basis KANN ja auch ein Interface sein).</summary>
        private string ResolveBaseRef(TypeRef tr) =>
            tr.ResolveBaseName(n => n == "Exception" || _classes.ContainsKey(n) || _interfaces.ContainsKey(n));

        private void CollectClasses(IReadOnlyList<Stmt> statements)
        {
            foreach (var stmt in statements)
            {
                switch (stmt)
                {
                    case ClassDecl cd:
                        if (_classes.ContainsKey(cd.Name))
                            AddError(new ResolverException($"Klasse '{GenericClassNames.PlainName(cd.Name)}' ist bereits definiert", cd.Line));
                        else
                            _classes[cd.Name] = cd;
                        break;
                    case InterfaceDecl id:
                        if (_interfaces.ContainsKey(id.Name))
                            AddError(new ResolverException($"Interface '{id.Name}' ist bereits definiert", id.Line));
                        else
                            _interfaces[id.Name] = id;
                        break;
                }
            }

            foreach (var cd in _classes.Values)
            {
                string? baseName = null;
                foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    string n = ResolveBaseRef(baseRef);
                    bool isClass = n == "Exception" || _classes.ContainsKey(n);
                    if (isClass)
                    {
                        if (baseName != null)
                            AddError(new ResolverException(
                                $"Klasse '{DisplayName(cd)}' kann nicht mehrere Basisklassen haben ('{baseName}' und '{n}')", cd.Line));
                        else
                            baseName = n;
                    }
                    else if (!_interfaces.ContainsKey(n))
                    {
                        AddError(new ResolverException(
                            $"'{baseRef.BaseName}' bei Klasse '{DisplayName(cd)}' ist weder eine bekannte Klasse noch ein bekanntes Interface", cd.Line));
                    }
                }
            }

            foreach (var cd in _classes.Values)
                foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    string n = ResolveBaseRef(baseRef);
                    if (_interfaces.TryGetValue(n, out var iface))
                        Guard(() => ValidateImplementsInterface(cd, iface));
                }
        }

        /// <summary>Der Klassenname für Fehlermeldungen: bei einer generischen
        /// Klasse mit umbenanntem Schlüssel (siehe GenericClassNames) der
        /// Name samt Typ-Parametern (`Box&lt;T&gt;`), damit sie von der
        /// gleichnamigen nicht-generischen zu unterscheiden ist.</summary>
        private static string DisplayName(ClassDecl cd) =>
            cd.TypeParams is { Count: > 0 }
                ? GenericClassNames.PlainName(cd.Name) + "<" + string.Join(", ", cd.TypeParams.Select(tp => tp.Name)) + ">"
                : cd.Name;

        /// <summary>Führt `check` aus und sammelt einen dabei geworfenen
        /// Resolver-Fehler, statt ihn weiterzureichen (siehe
        /// ResolverException) - für Prüfungen, nach denen sinnvoll
        /// weitergemacht werden kann.</summary>
        private void Guard(Action check)
        {
            try { check(); }
            catch (ResolverException ex) { AddError(ex); }
        }

        /// <summary>Wie <see cref="Guard"/>, setzt zusätzlich den Resolver-
        /// Zustand auf den Stand vor `action` zurück, falls ein Fehler
        /// auftrat (siehe ResolveStmt) - für Aktionen, die Scopes/Tiefen
        /// verändern.</summary>
        private void GuardWithState(Action action)
        {
            var state = SaveState();
            try { action(); }
            catch (ResolverException ex)
            {
                AddError(ex);
                RestoreState(state);
            }
        }

        private string? GetBaseClassName(ClassDecl cd)
        {
            foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
            {
                string n = ResolveBaseRef(baseRef);
                if (n == "Exception" || _classes.ContainsKey(n))
                    return n;
            }
            return null;
        }

        private ClassDecl? GetBaseClassDecl(ClassDecl cd)
        {
            var baseName = GetBaseClassName(cd);
            return baseName != null && _classes.TryGetValue(baseName, out var baseCd) ? baseCd : null;
        }

        /// <summary>Sucht über die Basisklassen-Kette (AST-Ebene, ClassDecl.
        /// Members - zum Resolve-Zeitpunkt existiert noch keine RuntimeClass),
        /// ob `name` ein Feld/eine Methode/eine Property ist - `startClass`
        /// zuerst, dann aufwärts. Liefert (Name der deklarierenden Klasse,
        /// IsStatic) der ERSTEN (nächstgelegenen) Klasse mit einem Mitglied
        /// dieses Namens, `null` sonst (kein Mitglied in der ganzen Kette).
        /// Für SPEC "Implizite Mitglieder-Referenzen" (bloßer Name statt
        /// 'this.'/'ClassName.') UND für 'this.Name', wenn Name eine
        /// statische Einheit ist (siehe ResolveExpr/ResolveAssignTarget,
        /// MemberExpr-Fall).</summary>
        private (string ClassName, bool IsStatic)? FindMemberInClassChain(ClassDecl? startClass, string name)
        {
            for (var cd = startClass; cd != null; cd = GetBaseClassDecl(cd))
            {
                foreach (var member in cd.Members)
                {
                    switch (member)
                    {
                        case FieldDecl fd when fd.Name == name: return (cd.Name, fd.IsStatic);
                        case MethodDecl md when md.Name == name: return (cd.Name, md.IsStatic);
                        case PropertyDecl pd when pd.Name == name: return (cd.Name, pd.IsStatic);
                    }
                }
            }
            return null;
        }

        /// <summary>Prüft, ob `cd` (inkl. geerbter Methoden über die
        /// Basisklassen-Kette) alle Methoden von `iface` per Name+Arität
        /// bereitstellt. Reine strukturelle Prüfung, keine Rückgabetyp-
        /// Kontrolle - die Sprache ist dynamisch typisiert.</summary>
        private void ValidateImplementsInterface(ClassDecl cd, InterfaceDecl iface)
        {
            foreach (var m in iface.Methods)
            {
                if (!ClassHasMethod(cd, m.Name, m.Params.Count))
                    throw new ResolverException(
                        $"Klasse '{cd.Name}' implementiert Interface '{iface.Name}' nicht vollständig " +
                        $"(fehlende Methode '{m.Name}' mit {m.Params.Count} Parameter(n))", cd.Line);
            }
        }

        private bool ClassHasMethod(ClassDecl cd, string name, int arity)
        {
            foreach (var member in cd.Members)
                if (member is MethodDecl md && md.Name == name && md.Params.Count == arity)
                    return true;

            var baseName = GetBaseClassName(cd);
            if (baseName != null && _classes.TryGetValue(baseName, out var baseCd))
                return ClassHasMethod(baseCd, name, arity);
            return false;
        }

        private bool IsKnownClassName(string name) => name == "Exception" || _classes.ContainsKey(name);

        /// <summary>Versucht, `me.Target` als geschlossenen, exakt
        /// geschriebenen Klassennamen zu lesen (SPEC "Statische Mitglieder") -
        /// baut dafür die Bezeichner-Kette von `me.Target` (IdentifierExpr
        /// oder verschachtelte MemberExpr, z.B. bei 'Geometry.Circle.Foo')
        /// zu einem punktierten String zusammen und prüft, ob DAS GANZE ein
        /// bekannter Klassenname ist. `null`, wenn `me.Target` keine reine
        /// Bezeichner-Kette ist oder die Kette keinen bekannten Klassennamen
        /// ergibt (dann ist `me` ein normaler, dynamischer Ausdruck).
        ///
        /// BEWUSSTE EINSCHRÄNKUNG: prüft NUR den exakt geschriebenen (ggf.
        /// schon vollqualifizierten) Namen, KEINE Auflösung über #using/den
        /// aktuellen Namespace - anders als ein TypeRef trägt ein
        /// IdentifierExpr/MemberExpr keinen eigenen Namespace-Kontext (der
        /// wird nur beim PARSEN einer TypeRef gesetzt, siehe
        /// Parser.CurrentNamespaces) - 'Circle.Foo()' würde bei
        /// '#using Geometry' also NICHT als 'Geometry.Circle' erkannt, nur
        /// 'Geometry.Circle.Foo()' voll ausgeschrieben funktioniert. Eine
        /// spätere Erweiterung dafür bräuchte Namespace-Info an JEDEM
        /// Bezeichner, nicht nur an TypeRef - eine größere, eigene Änderung.</summary>
        private string? TryResolveStaticMemberAccess(MemberExpr me)
        {
            // Die Klasse, in der wir gerade sind (siehe Ast.SelfClassExpr) -
            // steht nur für das Backing-Field statischer Auto-Properties in
            // generischen Klassen.
            if (me.Target is SelfClassExpr) return _currentClass?.Name;

            string? className = DottedName(me.Target);
            return className != null && IsKnownClassName(className) ? className : null;
        }

        /// <summary>Der punktierte Name, den `target` als reine Bezeichner-Kette
        /// schreibt (`A`, `Geometry.Circle`), oder `null`, wenn es keine
        /// solche Kette ist (Aufruf, Index, `this`, ...).</summary>
        private static string? DottedName(Expr target)
        {
            var pathSegments = new List<string>();
            Expr current = target;
            while (current is MemberExpr innerMe)
            {
                pathSegments.Add(innerMe.Name);
                current = innerMe.Target;
            }
            if (current is not IdentifierExpr rootId) return null;
            pathSegments.Add(rootId.Name);
            pathSegments.Reverse();
            return string.Join(".", pathSegments);
        }

        /// <summary>Prüft bei `new Name&lt;Arg1,...&gt;(...)` (siehe Ast.NewExpr.
        /// TypeArgs), ob die gegebenen Typ-Argumente zu den Typ-Parametern der
        /// Zielklasse passen (Anzahl) und deren 'where'-Constraints erfüllen
        /// (siehe Ast.TypeParam-Doku: ',' zwischen Gruppen = ODER, ':'
        /// innerhalb einer Gruppe = UND). Rein statische Prüfung, da
        /// Typ-Argumente NAMEN sind (Klassen/Interfaces/primitive Typen ODER,
        /// für eine 'is in'-Bedingung, Einheiten-Namen), keine Laufzeit-Werte
        /// - deshalb komplett hier im Resolver, ohne jede VM-Unterstützung.</summary>
        private void CheckTypeArgs(ClassDecl targetClass, NewExpr ne)
        {
            var typeParams = targetClass.TypeParams ?? Array.Empty<TypeParam>();
            string className = GenericClassNames.PlainName(targetClass.Name);

            if (typeParams.Count == 0)
            {
                if (ne.TypeArgs != null && ne.TypeArgs.Count > 0)
                    throw new ResolverException(
                        $"Klasse '{className}' ist nicht generisch, akzeptiert also keine " +
                        "Typ-Argumente in spitzen Klammern", ne.Line);
                return;
            }

            var typeArgs = ne.TypeArgs ?? Array.Empty<string>();
            if (typeArgs.Count != typeParams.Count)
                throw new ResolverException(
                    $"Klasse '{className}' ist generisch mit {typeParams.Count} Typ-Parameter(n) - " +
                    $"'new {className}<...>' braucht explizite Typ-Argumente dafür " +
                    $"(erhalten: {typeArgs.Count})", ne.Line);

            for (int i = 0; i < typeParams.Count; i++)
            {
                var tp = typeParams[i];
                string arg = typeArgs[i];
                if (tp.ConstraintGroups.Count == 0) continue; // uneingeschränkt ('<T>' ohne 'where')

                bool satisfied = tp.ConstraintGroups.Any(
                    group => group.Constraints.All(c => SatisfiesConstraint(arg, c)));
                if (!satisfied)
                    throw new ResolverException(
                        $"Typ-Argument '{arg}' für Typ-Parameter '{tp.Name}' von '{className}' erfüllt " +
                        "keine der 'where'-Bedingungen", ne.Line);
            }
        }

        private bool SatisfiesConstraint(string argName, TypeConstraint c) => c.Kind switch
        {
            TypeConstraintKind.IsOf => TypeNameSatisfiesIsOf(argName, c.Name),
            // 'is in': argName als Einheitenname interpretiert, dimensional
            // kompatibel mit c.Name (Unit.Parse wirft nie - ein unbekanntes
            // Symbol wird zu einer atomaren, nur zu sich selbst kompatiblen
            // Einheit, siehe Unit.Parse-Doku - das Ergebnis ist also einfach
            // "nicht erfüllt", kein Fehler).
            _ => Unit.Parse(argName).IsCompatibleWith(Unit.Parse(c.Name)),
        };

        /// <summary>Erfüllt der Typname `argName` ein 'is of `targetName`' -
        /// exakter Namens-Treffer, ODER (falls argName eine bekannte Klasse
        /// ist) `targetName` kommt irgendwo in dessen Basisklassen-/
        /// Interface-Kette vor. Rein NAMENS-basiert (keine Instanzen, keine
        /// Werte), analog zu ClassHasMethod.</summary>
        private bool TypeNameSatisfiesIsOf(string argName, string targetName)
        {
            if (argName == targetName) return true;
            if (!_classes.TryGetValue(argName, out var cd)) return false;
            foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
                if (TypeNameSatisfiesIsOf(ResolveBaseRef(baseRef), targetName))
                    return true;
            return false;
        }

        private void ValidateTypeName(TypeRef tr, int line)
        {
            // 'var' + nur Einheit (SPEC "Einheiten-Deklarationen") - kein
            // echter Typname zu validieren, der Typ wird ja aus dem
            // Initialisierer/Kontext hergeleitet (siehe TypeRef.IsInferred-Doku).
            if (tr.IsInferred) return;
            if (tr.LambdaSignature is { IsSelector: true } selector)
            {
                if (!_classes.ContainsKey("Reflect"))
                    throw new ResolverException($"'lambda {(selector.FieldOnly ? "field" : "property")}<...>' (Selektor) braucht die Reflection-Bibliothek: #import \"reflection\"", line);
                foreach (var target in selector.ParamTypeNames)
                    if (!PrimitiveTypeNames.Contains(target) && !_currentTypeParamNames.ContainsKey(target) && !IsKnownClassName(target))
                        throw new ResolverException($"Unbekannter Typ '{target}' in 'lambda {(selector.FieldOnly ? "field" : "property")}<{target}>'", line);
                return;
            }
            if (PrimitiveTypeNames.Contains(tr.BaseName)) return;
            if (_currentTypeParamNames.ContainsKey(tr.BaseName)) return;
            if (!IsKnownClassName(ResolveTypeRef(tr)))
                throw new ResolverException($"Unbekannter Typ '{tr.BaseName}'", line);
        }

        /// <summary>Validiert einen vollständigen TypeRef: Basisname wie
        /// ValidateTypeName, plus - falls vorhanden - dass eine Bitbreite nur bei
        /// int/float steht und einer der erlaubten Werte (8/16/32/64) ist.
        /// Pointer-Tiefe ist immer gültig.</summary>
        private void ValidateTypeRef(TypeRef type, int line)
        {
            ValidateTypeName(type, line);
            if (type.BitWidth == null) return;

            if (type.BaseName != "int" && type.BaseName != "float")
                throw new ResolverException(
                    $"Bitbreite ist nur für 'int'/'float' gültig, nicht für '{type.BaseName}'", line);
            if (type.BitWidth is not (8 or 16 or 32 or 64))
                throw new ResolverException(
                    $"Ungültige Bitbreite {type.BitWidth} (erlaubt: 8/16/32/64)", line);
        }

        private void ResolveArrayRanks(IReadOnlyList<Expr?> ranks)
        {
            foreach (var rank in ranks)
                if (rank != null) ResolveExpr(rank);
        }

        // -----------------------------------------------------------
        // Scope-Verwaltung
        // -----------------------------------------------------------
        private sealed class ResolverScope
        {
            public readonly ResolverScope? Parent;
            public readonly bool IsGlobal;
            public readonly Dictionary<string, int> Slots = new();
            public readonly HashSet<string> ReadonlySlots = new();

            /// <summary>Geforderte Einheit (SPEC "Einheiten-Deklarationen") je
            /// Name in DIESEM Scope, wenn die Deklaration ein explizites
            /// `: einheit` hatte - siehe Define/ResolveIdentifierRef.</summary>
            public readonly Dictionary<string, string> RequiredUnits = new();

            /// <summary>Namen, die in DIESEM (Lambda-)Scope als Capture (Kopie einer äußeren Variablen) liegen - Zuweisung ist ein Fehler,
            /// eine eigene Deklaration mit demselben Namen verdeckt sie (siehe Define).</summary>
            public readonly HashSet<string> CaptureNames = new();

            public ResolverScope(ResolverScope? parent, bool isGlobal = false)
            {
                Parent = parent;
                IsGlobal = isGlobal;
            }
        }

        private void PushScope() => _current = new ResolverScope(_current);
        private void PopScope() => _current = _current.Parent!;

        private void Define(string name, int line, bool isReadonly = false, string? requiredUnit = null)
        {
            if (_current.Slots.ContainsKey(name))
            {
                if (!_current.CaptureNames.Remove(name))
                    throw new ResolverException($"'{name}' ist in diesem Scope bereits deklariert", line);
                // Der Lambda-Körper deklariert selbst einen Namen, den der Resolver vorsorglich als Capture angelegt hat: die Deklaration verdeckt
                // ihn (der Capture-Slot bleibt ungenutzt unter einem unzugänglichen Schlüssel, die Slot-Zählung bleibt lückenlos).
                int captureSlot = _current.Slots[name];
                _current.Slots.Remove(name);
                _current.Slots["\u0001capture:" + name] = captureSlot;
                _current.ReadonlySlots.Remove(name);
                _current.RequiredUnits.Remove(name);
            }
            _current.Slots[name] = _current.Slots.Count;
            if (isReadonly) _current.ReadonlySlots.Add(name);
            if (requiredUnit != null) _current.RequiredUnits[name] = requiredUnit;
        }

        private ResolvedRef ResolveIdentifierRef(string name, int line)
        {
            int depth = 0;
            var scope = _current;
            while (scope != null)
            {
                if (scope.Slots.TryGetValue(name, out int slot))
                {
                    scope.RequiredUnits.TryGetValue(name, out var requiredUnit);
                    return scope.IsGlobal
                        ? new ResolvedRef.Global(slot, requiredUnit)
                        : new ResolvedRef.Local(depth, slot, requiredUnit);
                }
                depth++;
                scope = scope.Parent;
            }

            if (_nativeNames.Contains(name))
                return new ResolvedRef.Native(name);
            if (_externs.ContainsKey(name))
                return new ResolvedRef.Extern(name);
            if (_tryableNativeNames.Contains(name))
                throw new ResolverException(
                    $"'{name}' ist als 'tryable' registriert - nur mit 'try {name}(...)' aufrufbar, " +
                    "nicht als direkter Aufruf.", line);

            // SPEC "Implizite Mitglieder-Referenzen" - innerhalb einer Klasse
            // darf ein Feld/eine Methode/eine Property auch OHNE
            // 'this.'/'ClassName.'-Präfix angesprochen werden, genau wie eine
            // lokale Variable (zusätzlich zu, nicht statt, den expliziten
            // Formen - siehe MemberExpr-Fall für 'this.X'/'ClassName.X').
            // Bewusst NACH natives/externs geprüft - ein gleichnamiges
            // Klassenmitglied soll eine bestehende native/extern-Funktion
            // nicht überraschend verschatten.
            if (_currentClass != null)
            {
                var found = FindMemberInClassChain(_currentClass, name);
                if (found != null)
                {
                    if (found.Value.IsStatic)
                        return new ResolvedRef.StaticMember(found.Value.ClassName);
                    if (_inStaticMethod)
                        throw new ResolverException(
                            $"'{name}' ist ein Instanzmitglied - in einer statischen Methode/einem statischen " +
                            "Feld-Initialisierer ohne gebundenes 'this' nicht erreichbar", line);
                    return new ResolvedRef.ImplicitThisMember();
                }
            }

            throw new ResolverException($"Unbekannter Bezeichner '{name}'", line);
        }

        /// <summary>Läuft dieselbe Scope-Kette wie ResolveIdentifierRef ab, nur um
        /// zu prüfen, ob eine Variable per `readonly` deklariert wurde - für
        /// den Zuweisungs-Check in ResolveAssignTarget (der die Variable ja
        /// bereits per ResolveIdentifierRef erfolgreich aufgelöst hat, hier
        /// also immer fündig wird).</summary>
        private bool IsCapturedVariable(string name)
        {
            var scope = _current;
            while (scope != null)
            {
                if (scope.Slots.ContainsKey(name))
                    return scope.CaptureNames.Contains(name);
                scope = scope.Parent;
            }
            return false;
        }

        private bool IsReadonlyVariable(string name)
        {
            var scope = _current;
            while (scope != null)
            {
                if (scope.Slots.ContainsKey(name))
                    return scope.ReadonlySlots.Contains(name);
                scope = scope.Parent;
            }
            return false;
        }

        // -----------------------------------------------------------
        // Statements
        // -----------------------------------------------------------
        /// <summary>Löst ein Statement auf; ein dabei auftretender Fehler wird
        /// GESAMMELT (siehe ResolverException), und die Auflösung macht mit
        /// dem NÄCHSTEN Statement weiter. Jedes Statement - auch jedes in
        /// einem verschachtelten Block/Methodenkörper/einer Lambda - ist ein
        /// eigener Wiederaufsetzpunkt, ein Fehler verdirbt also höchstens den
        /// Rest SEINES Statements. Der Zustand des Resolvers (Scope-Kette,
        /// Tiefenzähler, aktuelle Klasse...) wird dafür auf den Stand VOR dem
        /// Statement zurückgesetzt: die Auflösung selbst stellt ihn nur bei
        /// normalem Ende wieder her, ein Fehler mittendrin würde ihn sonst
        /// verstellt zurücklassen und Folgefehler auslösen.</summary>
        private void ResolveStmt(Stmt stmt)
        {
            var state = SaveState();
            try
            {
                ResolveStmtCore(stmt);
            }
            catch (ResolverException ex)
            {
                AddError(ex);
                RestoreState(state);
                // Ein fehlgeschlagenes `var x = <Fehler>` deklariert x trotzdem,
                // sonst würde jede spätere Verwendung von x als "Unbekannter
                // Bezeichner" gemeldet - ein reiner Folgefehler des einen
                // echten Fehlers.
                if (stmt is VarDeclStmt vd && !_current.Slots.ContainsKey(vd.Name))
                    Define(vd.Name, vd.Line, vd.IsReadonly, vd.Type?.Unit);
            }
        }

        /// <summary>Der Teil des Resolver-Zustands, den die Auflösung eines
        /// Statements/Klassenmitglieds temporär verändert (siehe
        /// ResolveStmt).</summary>
        private readonly record struct ResolverState(
            ResolverScope Current, int FunctionDepth, int LoopDepth, int TryDepth, int UnsafeDepth,
            ClassDecl? CurrentClass, bool CurrentClassHasBase, bool InConstructor, bool InStaticMethod,
            Dictionary<string, int>? TypeParamNames);

        private ResolverState SaveState() => new(
            _current, _functionDepth, _loopDepth, _tryDepth, _unsafeDepth,
            _currentClass, _currentClassHasBase, _inConstructor, _inStaticMethod,
            _currentTypeParamNames.Count > 0 ? new Dictionary<string, int>(_currentTypeParamNames) : null);

        private void RestoreState(ResolverState state)
        {
            _current = state.Current;
            _functionDepth = state.FunctionDepth;
            _loopDepth = state.LoopDepth;
            _tryDepth = state.TryDepth;
            _unsafeDepth = state.UnsafeDepth;
            _currentClass = state.CurrentClass;
            _currentClassHasBase = state.CurrentClassHasBase;
            _inConstructor = state.InConstructor;
            _inStaticMethod = state.InStaticMethod;
            _currentTypeParamNames.Clear();
            if (state.TypeParamNames != null)
                foreach (var (name, count) in state.TypeParamNames)
                    _currentTypeParamNames[name] = count;
        }

        private void ResolveStmtCore(Stmt stmt)
        {
            switch (stmt)
            {
                case Stmt.BlockStmt block:
                    ResolveBlockNewScope(block);
                    break;

                case ExprStmt es:
                    ResolveExpr(es.Expression);
                    break;

                case NoOpStmt:
                    break;

                case NoShadowDirective:
                    // Bereits im Vorab-Pass (CollectNoShadowDirective)
                    // eingesammelt - hier nichts mehr zu tun.
                    break;

                case NoSyncDirective:
                    break; // wirkt erst zur Laufzeit (siehe Compiler.Compile: SetAutoSync)

                case VarDeclStmt vd:
                    if (vd.Initializer != null) ResolveExpr(vd.Initializer);
                    if (vd.Type != null) ValidateTypeRef(vd.Type, vd.Line);
                    ResolveArrayRanks(vd.ArrayRanks);
                    if (vd.IsReadonly && vd.Initializer == null &&
                        !(vd.ArrayRanks.Count > 0 && vd.ArrayRanks[0] != null))
                        throw new ResolverException(
                            $"'readonly {vd.Name}' braucht einen Initializer (oder eine Array-Größe)", vd.Line);
                    // 'var arr[4] = [1,2,3,4]' (explizite Größe UND ein
                    // Array-Literal als Initializer) - nur eine BESTE-EFFORT-
                    // Prüfung, wenn die Größe selbst ein Ganzzahl-LITERAL ist
                    // (der häufige Fall): stimmt sie nicht mit der Anzahl der
                    // Literal-Elemente überein, ist das ein Compile-Fehler
                    // statt eines stillschweigend anders großen Arrays (die
                    // deklarierte Größe würde sonst OHNE jede Meldung vom
                    // Initializer überschrieben, siehe Compiler.CompileStmt).
                    // Eine DYNAMISCHE Größe (Variable/Ausdruck) wird hier
                    // NICHT geprüft - dafür bräuchte es eine Laufzeit-Prüfung,
                    // die diese Ausbaustufe bewusst nicht baut.
                    if (vd.Initializer is ArrayLiteralExpr arrLit
                        && vd.ArrayRanks.Count > 0 && vd.ArrayRanks[0] is LiteralExpr sizeLit
                        && sizeLit.Value.Kind == ValueKind.Int
                        && sizeLit.Value.AsInt() != arrLit.Elements.Count)
                        throw new ResolverException(
                            $"'{vd.Name}[{sizeLit.Value.AsInt()}]' erwartet {sizeLit.Value.AsInt()} Elemente, " +
                            $"der Array-Literal-Initializer hat aber {arrLit.Elements.Count}", vd.Line);
                    Define(vd.Name, vd.Line, vd.IsReadonly, vd.Type?.Unit);
                    break;

                case IfStmt ifs:
                    ResolveExpr(ifs.Condition);
                    ResolveStmtAsScope(ifs.Then);
                    if (ifs.Else != null) ResolveStmtAsScope(ifs.Else);
                    break;

                case WhileStmt ws:
                    ResolveExpr(ws.Condition);
                    _loopDepth++;
                    int savedFinallyDepth = _tryDepth; _tryDepth = 0;
                    ResolveStmtAsScope(ws.Body);
                    _tryDepth = savedFinallyDepth;
                    _loopDepth--;
                    break;

                case ForStmt fs:
                    ResolveFor(fs);
                    break;

                case ForeachStmt fe:
                    ResolveForeach(fe);
                    break;

                case ReturnStmt rs:
                    if (_functionDepth == 0)
                        throw new ResolverException("'return' außerhalb einer Funktion/Methode", rs.Line);
                    if (rs.Value != null) ResolveExpr(rs.Value);
                    break;

                case ThrowStmt ts:
                    ResolveExpr(ts.Value);
                    break;

                case TryStmt trys:
                    ResolveTry(trys);
                    break;

                case BreakStmt bs:
                    if (_loopDepth == 0)
                        throw new ResolverException("'break' außerhalb einer Schleife ('while'/'for'/'foreach')", bs.Line);
                    if (_tryDepth > 0)
                        throw new ResolverException("'break' kann nicht aus einem 'finally'-Block heraus verwendet werden (Einschränkung dieser Ausbaustufe - siehe BYTECODE.md)", bs.Line);
                    break;

                case ContinueStmt cs:
                    if (_loopDepth == 0)
                        throw new ResolverException("'continue' außerhalb einer Schleife ('while'/'for'/'foreach')", cs.Line);
                    if (_tryDepth > 0)
                        throw new ResolverException("'continue' kann nicht aus einem 'finally'-Block heraus verwendet werden (Einschränkung dieser Ausbaustufe - siehe BYTECODE.md)", cs.Line);
                    break;

                case FireStmt fireStmt:
                    ResolveFireStmt(fireStmt);
                    break;

                case SectionEnterStmt:
                case SectionExitStmt:
                    break;

                case SilenceStmt silence:
                    ResolveExpr(silence.Target);
                    break;

                case PostGlobalStmt postGlobal:
                    foreach (var arg in postGlobal.Args) ResolveExpr(arg);
                    ResolveLambda(postGlobal.Lambda);
                    break;

                case LeaveStmt:
                    // Bewusst keine Einschränkung auf "nur innerhalb eines
                    // fire-Blocks" (siehe Ast.LeaveStmt-Doku) - nichts zu
                    // prüfen.
                    break;

                case TerminateStmt terminateStmt:
                    if (terminateStmt.Value != null) ResolveExpr(terminateStmt.Value);
                    break;

                case CatchThreadsDecl threadsDecl:
                    if (threadsDecl.TypeRef != null && !IsKnownClassName(ResolveTypeRef(threadsDecl.TypeRef)))
                        AddError(new ResolverException($"Unbekannter Exception-Typ '{threadsDecl.TypeRef.BaseName}'", threadsDecl.Line));
                    ResolveGlobalHandlerBody(threadsDecl.VarName, threadsDecl.Body);
                    break;

                case CatchTerminateDecl terminateDecl:
                    ResolveGlobalHandlerBody(terminateDecl.VarName, terminateDecl.Body);
                    break;

                case ProcessStmt processStmt:
                    ResolveExpr(processStmt.Target);
                    break;

                case ClassDecl cd:
                    ResolveClass(cd);
                    break;

                case InterfaceDecl:
                    // Bereits im Vorab-Pass (CollectClasses) validiert - hier nichts zu tun.
                    break;

                case EnumDecl:
                    // Bereits im Vorab-Pass (CollectEnums) validiert und mit
                    // Werten befüllt - hier nichts zu tun.
                    break;

                case ClassExtensionDecl cx:
                    // Sollte NIE hier ankommen - Parser.MergeClassExtensions
                    // löst das schon vor dem Resolven vollständig auf (siehe
                    // Ast.ClassExtensionDecl-Doku). Nur als Sicherheitsnetz,
                    // falls das Programm auf einem anderen Weg als über
                    // Parser.Parse()/ParseMultiple() erzeugt wurde.
                    throw new ResolverException(
                        $"Interner Fehler: 'class extends {cx.TargetRef.BaseName}' wurde nicht zusammengeführt " +
                        "(Programm muss über Parser.Parse()/ParseMultiple() erzeugt werden).", cx.Line);

                case ExternDecl ed:
                    if (ed.ReturnType != null) ValidateTypeRef(ed.ReturnType, ed.Line);
                    foreach (var p in ed.Params)
                    {
                        if (p.Type != null) ValidateTypeRef(p.Type, ed.Line);
                        ResolveArrayRanks(p.ArrayRanks);
                    }
                    break;

                case UnsafeStmt us:
                    _unsafeDepth++;
                    ResolveBlockNewScope(us.Body);
                    _unsafeDepth--;
                    break;

                default:
                    throw new ResolverException(
                        $"Unerwartetes Statement {stmt.GetType().Name} an dieser Stelle", stmt.Line);
            }
        }

        /// <summary>Löst ein Statement so auf, dass es einen eigenen Scope bekommt -
        /// entweder direkt (wenn es schon ein Block ist) oder über einen
        /// Wegwerf-Scope (für einzeilige if/while/for-Bodies ohne '{}').</summary>
        private void ResolveStmtAsScope(Stmt body)
        {
            if (body is Stmt.BlockStmt block)
            {
                ResolveBlockNewScope(block);
            }
            else
            {
                PushScope();
                ResolveStmt(body);
                PopScope();
            }
        }

        private void ResolveBlockNewScope(Stmt.BlockStmt block)
        {
            PushScope();
            foreach (var s in block.Statements) ResolveStmt(s);
            PopScope();
        }

        private void ResolveFor(ForStmt fs)
        {
            PushScope(); // umschließt Init/Condition/Increment/Body gemeinsam
            if (fs.Init != null) ResolveStmt(fs.Init);
            if (fs.Condition != null) ResolveExpr(fs.Condition);
            if (fs.Increment != null) ResolveExpr(fs.Increment);
            _loopDepth++;
            int savedFinallyDepth = _tryDepth; _tryDepth = 0;
            ResolveStmtAsScope(fs.Body);
            _tryDepth = savedFinallyDepth;
            _loopDepth--;
            PopScope();
        }

        private void ResolveForeach(ForeachStmt fe)
        {
            ResolveExpr(fe.Iterable); // im umschließenden Scope, nicht im Loop-Scope
            PushScope();
            Define(fe.VarName, fe.Line);
            _loopDepth++;
            int savedFinallyDepth = _tryDepth; _tryDepth = 0;
            ResolveStmtAsScope(fe.Body);
            _tryDepth = savedFinallyDepth;
            _loopDepth--;
            PopScope();
        }

        /// <summary>`break`/`continue` dürfen aus dem `try`- und den `catch`-Blöcken heraus verwendet werden (der Compiler räumt Handler und
        /// Scopes ab und führt ein vorhandenes `finally` vorher aus, siehe Compiler.CompileBreakOrContinue) - aber nicht aus dem `finally`-Block
        /// selbst: `_tryDepth` zählt deshalb nur noch die umgebenden `finally`-Blöcke (je Schleife neu).</summary>
        private void ResolveTry(TryStmt t)
        {
            ResolveBlockNewScope(t.TryBlock);

            foreach (var c in t.Catches)
            {
                // Ein unbekannter Exception-Typ hindert nicht die Auflösung
                // des catch-Körpers (und der übrigen Blöcke).
                if (c.TypeRef != null && !IsKnownClassName(ResolveTypeRef(c.TypeRef)))
                    AddError(new ResolverException($"Unbekannter Exception-Typ '{c.TypeRef.BaseName}'", c.Line));

                PushScope();
                Define(c.VarName, c.Line);
                foreach (var s in c.Body.Statements) ResolveStmt(s);
                PopScope();
            }

            if (t.Finally != null)
            {
                _tryDepth++;
                ResolveBlockNewScope(t.Finally);
                _tryDepth--;
            }
        }

        // -----------------------------------------------------------
        // Klassen
        // -----------------------------------------------------------
        private void ResolveClass(ClassDecl cd)
        {
            var savedClass = _currentClass;
            var savedHasBase = _currentClassHasBase;
            _currentClass = cd;
            _currentClassHasBase = GetBaseClassName(cd) != null;

            var classTypeParamNames = cd.TypeParams?.Select(tp => tp.Name).ToList() ?? new List<string>();
            AddTypeParamNames(classTypeParamNames);

            // Pro Klasse: erlaubt mehrere Methoden desselben Namens
            // (Überladung), aber nur mit UNTERSCHIEDLICHER Parameteranzahl -
            // das ist die einzige zur Aufrufzeit generell unterscheidbare
            // Signatur in einer dynamisch typisierten Sprache (siehe
            // RuntimeClass.FindMethod-Doku). Konstruktoren ebenso, aber in
            // einer EIGENEN Zählung (der Name spielt dort ja keine Rolle -
            // es gibt nur "den" Konstruktor einer Klasse, mehrere
            // Überladungen unterscheiden sich rein über die Arity).
            var seenMethodSignatures = new HashSet<(string Name, int Arity)>();
            var seenConstructorArities = new HashSet<int>();

            // Jedes Mitglied ist ein eigener Wiederaufsetzpunkt (siehe
            // ResolveStmt) - ein Fehler in einem Feld/einer Methode hindert
            // nicht die Auflösung der übrigen Mitglieder.
            foreach (var member in cd.Members)
            {
                GuardWithState(() =>
                {
                    switch (member)
                    {
                        case FieldDecl fd:
                            // Ein ungültiger Typ hindert nicht die Auflösung des
                            // Initialisierers (und umgekehrt) - beides einzeln
                            // abgesichert, damit ALLE Fehler gemeldet werden.
                            if (fd.Type != null) Guard(() => ValidateTypeRef(fd.Type, fd.Line));
                            ResolveArrayRanks(fd.ArrayRanks);
                            ResolveFieldInitializer(fd);
                            break;

                        case ConstructorDecl ctor:
                            if (!seenConstructorArities.Add(ctor.Params.Count))
                                AddError(new ResolverException(
                                    $"Konstruktor mit {ctor.Params.Count} Parameter(n) ist in dieser Klasse " +
                                    "bereits definiert (eine Überladung braucht eine andere Parameteranzahl).", ctor.Line));
                            ResolveFunctionLike(ctor.Params, ctor.Body, ctor.BaseArgs, isConstructor: true);
                            break;

                        case DestructorDecl dtor:
                            ResolveFunctionLike(Array.Empty<LambdaParam>(), dtor.Body, baseArgs: null, isConstructor: false);
                            break;

                        case MethodDecl md:
                            var methodTypeParamNames = md.TypeParams?.Select(tp => tp.Name).ToList() ?? new List<string>();
                            AddTypeParamNames(methodTypeParamNames);
                            try
                            {
                                if (md.ReturnType != null)
                                    Guard(() => ValidateTypeRef(md.ReturnType, md.Line));
                                if (!seenMethodSignatures.Add((md.Name, md.Params.Count)))
                                    AddError(new ResolverException(
                                        $"Methode '{md.Name}' mit {md.Params.Count} Parameter(n) ist in dieser Klasse " +
                                        "bereits definiert (eine Überladung braucht eine andere Parameteranzahl).", md.Line));
                                ResolveFunctionLike(md.Params, md.Body, baseArgs: null, isConstructor: false, isStatic: md.IsStatic);
                            }
                            finally
                            {
                                RemoveTypeParamNames(methodTypeParamNames);
                            }
                            break;

                        case PropertyDecl pd:
                            if (pd.Type != null) Guard(() => ValidateTypeRef(pd.Type, pd.Line));
                            // Getter: wie eine parameterlose Methode. Setter: wie
                            // eine Methode mit genau einem Parameter 'value' vom
                            // Property-Typ (implizit, wie C#s Setter-Parameter) -
                            // ganz normale Parameter-Auflösung, keine
                            // Sonderbehandlung nötig.
                            if (pd.Getter != null)
                                GuardWithState(() => ResolveFunctionLike(
                                    Array.Empty<LambdaParam>(), pd.Getter, baseArgs: null, isConstructor: false, isStatic: pd.IsStatic));
                            if (pd.Setter != null)
                            {
                                var setterParams = new[] { new LambdaParam("value", pd.Type, Array.Empty<Expr?>()) };
                                GuardWithState(() => ResolveFunctionLike(
                                    setterParams, pd.Setter, baseArgs: null, isConstructor: false, isStatic: pd.IsStatic));
                            }
                            break;
                    }
                });
            }

            RemoveTypeParamNames(classTypeParamNames);
            _currentClass = savedClass;
            _currentClassHasBase = savedHasBase;
        }

        /// <summary>Feld-Initialisierer sehen (wie Lambdas) nur ihren eigenen Scope
        /// + global - plus implizit 'this', da sie pro Instanz im Konstruktor-
        /// Kontext ausgewertet werden, aber nicht die Parameter irgendeines
        /// bestimmten Konstruktors kennen.</summary>
        private void ResolveFieldInitializer(FieldDecl fd)
        {
            if (fd.Initializer == null) return;
            var saved = _current;
            _current = new ResolverScope(_globalScope);
            bool savedInStaticMethod = _inStaticMethod;
            _inStaticMethod = fd.IsStatic; // SPEC "Statische Mitglieder" - kein 'this' in einem statischen Feld-Initialisierer
            ResolveExpr(fd.Initializer);
            _inStaticMethod = savedInStaticMethod;
            _current = saved;
        }

        /// <summary>Optionale Parameter (mit Standardwert) müssen am Ende der
        /// Parameterliste ZUSAMMENHÄNGEN - kein Pflichtparameter nach einem
        /// optionalen (sonst wäre bei einem Aufruf mit weniger Argumenten
        /// nicht eindeutig, welcher Parameter "fehlt"). Dieselbe Regel wie in
        /// den meisten Sprachen mit optionalen Parametern.</summary>
        private static void ValidateOptionalParamsAreTrailing(IReadOnlyList<LambdaParam> parms, int line)
        {
            bool seenOptional = false;
            foreach (var p in parms)
            {
                if (p.DefaultValue != null) { seenOptional = true; continue; }
                if (seenOptional)
                    throw new ResolverException(
                        $"Parameter '{p.Name}' ohne Standardwert darf nicht nach einem optionalen Parameter " +
                        "stehen (optionale Parameter müssen am Ende zusammenhängen).", line);
            }
        }

        /// <summary>Standardwert-Ausdrücke sehen wie Feld-Initialisierer (siehe
        /// ResolveFieldInitializer) nur ihren eigenen Scope + global + `this`
        /// - NICHT die anderen Parameter derselben Funktion, da sie
        /// unabhängig von diesen ausgewertet werden (siehe VM.
        /// FillDefaultArgs - eine eigene, isolierte Auswertung pro fehlendem
        /// Parameter, nicht Teil des normalen Funktions-Scopes).</summary>
        private void ResolveParamDefaults(IReadOnlyList<LambdaParam> parms)
        {
            var saved = _current;
            _current = new ResolverScope(_globalScope);
            foreach (var p in parms)
                if (p.DefaultValue != null)
                    ResolveExpr(p.DefaultValue);
            _current = saved;
        }

        private void ResolveFunctionLike(
            IReadOnlyList<LambdaParam> parms, Stmt.BlockStmt body, IReadOnlyList<Expr>? baseArgs, bool isConstructor, bool isStatic = false)
        {
            // Fehler im Kopf (Parameterliste, base(...)) hindern nicht die
            // Auflösung des Körpers - alles einzeln abgesichert, damit ALLE
            // Fehler gemeldet werden (siehe ResolveStmt).
            Guard(() => ValidateOptionalParamsAreTrailing(parms, body.Line));
            GuardWithState(() => ResolveParamDefaults(parms));

            PushScope();
            foreach (var p in parms)
            {
                if (p.Type != null) Guard(() => ValidateTypeRef(p.Type, body.Line));
                ResolveArrayRanks(p.ArrayRanks);
                Guard(() => Define(p.Name, body.Line, requiredUnit: p.Type?.Unit));
            }

            if (baseArgs != null)
            {
                if (!_currentClassHasBase)
                    AddError(new ResolverException(
                        "'base(...)' nur in einer Klasse mit Basisklasse gültig", body.Line));
                foreach (var a in baseArgs) ResolveExpr(a);
            }

            // WICHTIG: NICHT von 'baseArgs != null' ableiten - das heißt nur
            // "hat ein EXPLIZITES 'base(...)'", nicht "ist ein Konstruktor".
            // Eine Klasse ohne Basisklasse hat nie baseArgs, ihr Konstruktor
            // braucht das Flag aber trotzdem.
            bool savedInConstructor = _inConstructor;
            _inConstructor = isConstructor;

            bool savedInStaticMethod = _inStaticMethod;
            _inStaticMethod = isStatic;

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            _loopDepth = 0;
            _tryDepth = 0;

            _functionDepth++;
            foreach (var s in body.Statements) ResolveStmt(s);
            _functionDepth--;

            _loopDepth = savedLoopDepth;
            _tryDepth = savedTryDepth;

            _inConstructor = savedInConstructor;
            _inStaticMethod = savedInStaticMethod;

            PopScope();
        }

        // -----------------------------------------------------------
        // Ausdrücke
        // -----------------------------------------------------------
        /// <summary>Löst einen Ausdruck auf; ein Fehler darin wird gesammelt
        /// (siehe ResolveStmt), die Auflösung macht mit den Geschwister-
        /// Ausdrücken weiter - z.B. werden bei `f(a, b)` beide unbekannten
        /// Bezeichner gemeldet, nicht nur `a`.</summary>
        private void ResolveExpr(Expr expr)
        {
            var state = SaveState();
            try
            {
                ResolveExprCore(expr);
            }
            catch (ResolverException ex)
            {
                AddError(ex);
                RestoreState(state);
            }
        }

        private void ResolveExprCore(Expr expr)
        {
            switch (expr)
            {
                case LiteralExpr:
                    break;

                case IdentifierExpr id:
                    _refs[id] = ResolveIdentifierRef(id.Name, id.Line);
                    break;

                case ThisExpr te:
                    if (_inStaticMethod)
                        throw new ResolverException(
                            "'this' ist in einer statischen Methode/einem statischen Feld-Initialisierer nicht gültig " +
                            "(keine Instanz gebunden)", te.Line);
                    break; // Laufzeit entscheidet, ob/was 'this' aktuell gebunden ist

                case BaseExpr be:
                    if (_inStaticMethod)
                        throw new ResolverException(
                            "'base' ist in einer statischen Methode nicht gültig (keine Instanz gebunden)", be.Line);
                    if (_currentClass == null || !_currentClassHasBase)
                        throw new ResolverException(
                            "'base' nur innerhalb einer Klasse mit Basisklasse gültig", be.Line);
                    break;

                case UnaryExpr u:
                    if ((u.Op == UnaryOp.Dereference || u.Op == UnaryOp.AddressOf) && _unsafeDepth == 0)
                        throw new ResolverException(
                            $"'{(u.Op == UnaryOp.Dereference ? "*" : "&")}' ist nur innerhalb eines 'unsafe'-Blocks gültig", u.Line);
                    ResolveExpr(u.Operand);
                    break;

                case BinaryExpr b:
                    ResolveExpr(b.Left);
                    ResolveExpr(b.Right);
                    break;

                case UnitCoerceExpr uc:
                    ResolveExpr(uc.Operand);
                    break;

                case TypeCoerceExpr tc:
                    ResolveExpr(tc.Operand);
                    break;

                case IsInExpr iin:
                    ResolveExpr(iin.Operand);
                    break;

                case IsOfExpr iof:
                    ResolveExpr(iof.Operand);
                    // `wert is of IFoo`: auch ein Interface ist als Typ erlaubt (die Klasse nennt es in `class X : IFoo`)
                    if (!_interfaces.ContainsKey(iof.TypeRef.BaseName)) ValidateTypeName(iof.TypeRef, iof.Line);
                    break;

                case IsFromExpr ifr:
                    ResolveExpr(ifr.Operand);
                    ResolveExpr(ifr.OwnerExpr);
                    break;

                case CallExpr call:
                    ResolveExpr(call.Callee);
                    foreach (var a in call.Args) ResolveExpr(a);
                    break;

                case MemberExpr me:
                    // 'EnumName.Mitglied' - erkannt rein daran, dass der
                    // Zielname (als bloßer Bezeichner) ein bekannter enum-Name
                    // ist. Bewusste Design-Entscheidung: ein enum-Name "gewinnt"
                    // dabei immer gegen eine gleichnamige Variable im Scope
                    // (wie ein Klassenname auch nicht durch eine Variable
                    // verschattet werden kann) - dieselbe Namenskollision wäre
                    // ohnehin verwirrend und in der Praxis leicht vermeidbar.
                    // Auch ein Enum in einem Namespace ist so erreichbar, dann aber
                    // vollqualifiziert ('Geometry.Kind.Round') - wie beim
                    // statischen Klassenzugriff (siehe TryResolveStaticMemberAccess)
                    // zählt NUR der exakt geschriebene Name, keine `#using`-/
                    // Namespace-Auflösung.
                    string? enumName = DottedName(me.Target);
                    if (enumName != null && _enums.TryGetValue(enumName, out var enumMembers))
                    {
                        if (!enumMembers.TryGetValue(me.Name, out long enumValue))
                            throw new ResolverException($"'{enumName}' hat kein Mitglied '{me.Name}'", me.Line);
                        _refs[me] = new ResolvedRef.EnumMember(enumValue);
                        break;
                    }
                    // 'ClassName.Member' (SPEC "Statische Mitglieder") - wie
                    // beim enum-Fall: ein bekannter Klassenname "gewinnt"
                    // immer gegen eine gleichnamige Variable. Der eigentliche
                    // Zugriff (existiert das Mitglied, ist es WIRKLICH
                    // statisch, Zugriffsmodifikator) wird bewusst NICHT hier,
                    // sondern erst in der VM geprüft (GetStaticField/
                    // CallStaticMethod) - dieselbe Grenze wie bei normalen
                    // Instanzfeldern/-methoden (siehe VM.CheckFieldAccess),
                    // der Resolver kennt Feld-/Methodennamen einer Klasse
                    // nicht vollständig genug, um das schon hier sicher zu
                    // validieren (Vererbung, dynamisch gesetzte Felder).
                    string? staticClassName = TryResolveStaticMemberAccess(me);
                    if (staticClassName != null)
                    {
                        _refs[me] = new ResolvedRef.StaticMember(staticClassName);
                        break;
                    }
                    // 'this.StaticMember' (SPEC "Implizite Mitglieder-
                    // Referenzen") - ein statisches Mitglied ist auch über
                    // 'this.' erreichbar (zusätzlich zu bloßem Namen und
                    // 'ClassName.'), obwohl 'this' selbst nichts mit der
                    // statischen Speicherstelle zu tun hat - der Compiler
                    // wandelt das dann in denselben GetStaticField/
                    // SetStaticField/CallStaticMethod-Pfad um wie 'ClassName.X',
                    // NICHT in GetField/SetField (die würden ein statisches
                    // Feld nicht finden, siehe RuntimeClass.StaticFieldValues).
                    if (me.Target is ThisExpr && _currentClass != null)
                    {
                        var thisMember = FindMemberInClassChain(_currentClass, me.Name);
                        if (thisMember is { IsStatic: true })
                        {
                            _refs[me] = new ResolvedRef.StaticMember(thisMember.Value.ClassName);
                            break;
                        }
                    }
                    ResolveExpr(me.Target); // .Name bleibt unresolved - dynamischer Feld-/Methodenzugriff
                    break;

                case IndexExpr ix:
                    ResolveExpr(ix.Target);
                    ResolveExpr(ix.Index);
                    break;

                case AssignExpr asg:
                    ResolveExpr(asg.Value);
                    ResolveAssignTarget(asg.Target);
                    break;

                case IncDecExpr incDec:
                    // Braucht sowohl Lese- als auch Schreibzugriff auf
                    // dasselbe Ziel - ResolveAssignTarget deckt beides ab
                    // (füllt für IdentifierExpr z.B. _refs genauso wie ein
                    // normales Lesen es täte, siehe dort), zusätzlich noch
                    // die readonly-/unsafe-Prüfungen, die für Zuweisungen
                    // ohnehin gelten und für '++'/'--' genauso gelten müssen.
                    ResolveAssignTarget(incDec.Target);
                    break;

                case NewExpr ne:
                    // Ein Fehler beim Ziel (unbekannte Klasse, falsche
                    // Typ-Argumente) hindert nicht die Auflösung der Argumente.
                    Guard(() =>
                    {
                        // Die Anzahl der Typ-Argumente wählt zwischen einer
                        // nicht-generischen und einer gleichnamigen generischen
                        // Klasse (siehe GenericClassNames).
                        string resolvedNewClassName = GenericClassNames.ResolveNewTarget(
                            ne.ClassRef, ne.TypeArgs?.Count ?? 0, IsKnownClassName);
                        if (!IsKnownClassName(resolvedNewClassName))
                            throw new ResolverException($"Unbekannte Klasse '{ne.ClassRef.BaseName}'", ne.Line);
                        if (_classes.TryGetValue(resolvedNewClassName, out var newTargetCd))
                            CheckTypeArgs(newTargetCd, ne);
                        else if (ne.TypeArgs != null && ne.TypeArgs.Count > 0)
                            throw new ResolverException(
                                $"'{ne.ClassRef.BaseName}' ist nicht generisch, akzeptiert also keine Typ-Argumente in spitzen Klammern",
                                ne.Line);
                    });
                    foreach (var a in ne.Args) ResolveExpr(a);
                    break;

                case NewArrayExpr na:
                    ValidateTypeRef(na.ElementType, na.Line);
                    if (na.SizeExprs.Count == 0 || na.SizeExprs[0] == null)
                        throw new ResolverException(
                            "'new Type[...]' braucht mindestens für die erste Dimension eine Größe " +
                            "(z.B. 'new int[3]' oder 'new int[3][]' - nicht 'new int[]').", na.Line);
                    ResolveArrayRanks(na.SizeExprs);
                    break;

                case NewBufferExpr nb:
                    ResolveExpr(nb.SizeExpr);
                    break;

                case ArrayLiteralExpr al:
                    foreach (var el in al.Elements) ResolveExpr(el);
                    break;

                case InterpolatedStringExpr ise:
                    foreach (var part in ise.Parts)
                        if (part is InterpolationExprPart ep) ResolveExpr(ep.Expression);
                    break;

                case ThrowExpr th:
                    ResolveExpr(th.Value);
                    break;

                case LambdaExpr lam:
                    ResolveLambda(lam);
                    break;

                case SyncExpr syncExpr:
                    ResolveExpr(syncExpr.Target);
                    break;

                case ProbeExpr probe:
                    ResolveExpr(probe.Target);
                    ResolveExpr(probe.Handler);
                    break;

                case SyncGlobalsExpr:
                    break;

                case TryProcessExpr tryProcessExpr:
                    ResolveExpr(tryProcessExpr.Target);
                    break;

                case TryCallExpr tryCallExpr:
                    ResolveTryCallExpr(tryCallExpr);
                    break;

                default:
                    throw new ResolverException($"Unbekannter Ausdruckstyp {expr.GetType().Name}", expr.Line);
            }
        }

        /// <summary>`try Name(args)` (siehe Ast.TryCallExpr) - der innere
        /// Aufruf MUSS ein direkter Aufruf eines BEKANNTEN, als "tryable"
        /// registrierten Namens sein (siehe Bytecode.NativeRegistry.
        /// RegisterTryable) - kein Methodenaufruf, kein Lambda-Aufruf, kein
        /// gewöhnlicher/als 'extern' deklarierter nativer Name (die dürfen
        /// NICHT mit 'try' aufgerufen werden - nur registrierte "tryable"
        /// APIs). Der Callee selbst wird bewusst NICHT über ResolveExpr
        /// aufgelöst (das würde als normaler Bezeichner scheitern oder
        /// fälschlich einen ResolvedRef.Native/Extern setzen) - stattdessen
        /// hängt hier direkt eine eigene ResolvedRef.TryableNative am
        /// TryCallExpr-Knoten selbst, die der Compiler abfragt.</summary>
        private void ResolveTryCallExpr(TryCallExpr tc)
        {
            if (tc.Call is not CallExpr innerCall || innerCall.Callee is not IdentifierExpr calleeIdent)
                throw new ResolverException(
                    "'try' vor einem Aufruf erwartet einen direkten Aufruf einer registrierten Funktion " +
                    "(kein Methodenaufruf, kein Lambda-Aufruf)", tc.Line);

            if (!_tryableNativeNames.Contains(calleeIdent.Name))
                throw new ResolverException(
                    _nativeNames.Contains(calleeIdent.Name) || _externs.ContainsKey(calleeIdent.Name)
                        ? $"'{calleeIdent.Name}' ist keine 'tryable' registrierte Funktion - nur mit 'try' " +
                          "aufrufbare APIs dürfen so aufgerufen werden, normale native/extern-Funktionen nicht."
                        : $"'{calleeIdent.Name}' ist keine bekannte 'tryable' registrierte native Funktion.",
                    tc.Line);

            foreach (var a in innerCall.Args) ResolveExpr(a);
            _refs[tc] = new ResolvedRef.TryableNative(calleeIdent.Name);
        }

        private void ResolveAssignTarget(Expr target)
        {
            switch (target)
            {
                case IdentifierExpr id:
                    if (IsCapturedVariable(id.Name))
                        throw new ResolverException(
                            $"'{id.Name}' ist im Lambda eine KOPIE der äußeren Variablen (Capture) und kann dort nicht zugewiesen werden " +
                            "(eine neue lokale Variable mit anderem Namen anlegen)", id.Line);
                    if (IsReadonlyVariable(id.Name))
                        throw new ResolverException(
                            $"'{id.Name}' ist 'readonly' und kann nach der Deklaration nicht mehr zugewiesen werden", id.Line);
                    _refs[id] = ResolveIdentifierRef(id.Name, id.Line);
                    break;
                case MemberExpr me:
                    // readonly-Felder dürfen NUR als 'this.feld = ...' innerhalb
                    // eines Konstruktors DER DEKLARIERENDEN Klasse zugewiesen
                    // werden - das ist rein statisch prüfbar (Ziel ist lexikalisch
                    // 'this', aktuelle Klasse ist bekannt). Für ein dynamisches
                    // Ziel ('obj.feld = ...', 'obj' beliebiger Ausdruck) kann der
                    // Resolver mangels statischem Typsystem NICHT wissen, welche
                    // Klasse gemeint ist - bewusste Grenze dieser Ausbaustufe,
                    // siehe FieldDecl-Doku.
                    if (me.Target is ThisExpr && _currentClass != null && IsReadonlyField(_currentClass, me.Name) && !_inConstructor)
                        throw new ResolverException(
                            $"'{_currentClass.Name}.{me.Name}' ist 'readonly' und kann nur innerhalb eines " +
                            "Konstruktors der deklarierenden Klasse zugewiesen werden", me.Line);
                    // 'ClassName.Member = ...' (SPEC "Statische Mitglieder") -
                    // dieselbe Erkennung wie beim Lesen (siehe ResolveExpr/
                    // TryResolveStaticMemberAccess), hier separat nötig, weil
                    // ein Zuweisungsziel NICHT über den normalen ResolveExpr-
                    // Fall läuft (sonst würde 'ClassName' als unbekannter
                    // Bezeichner statt als Klassenname behandelt).
                    string? staticAssignClassName = TryResolveStaticMemberAccess(me);
                    if (staticAssignClassName != null)
                    {
                        _refs[me] = new ResolvedRef.StaticMember(staticAssignClassName);
                        break;
                    }
                    // 'this.StaticMember = ...' - siehe dieselbe Begründung im
                    // Lesefall oben (ResolveExpr, case MemberExpr).
                    if (me.Target is ThisExpr && _currentClass != null)
                    {
                        var thisAssignMember = FindMemberInClassChain(_currentClass, me.Name);
                        if (thisAssignMember is { IsStatic: true })
                        {
                            _refs[me] = new ResolvedRef.StaticMember(thisAssignMember.Value.ClassName);
                            break;
                        }
                    }
                    ResolveExpr(me.Target);
                    break;
                case IndexExpr ix:
                    ResolveExpr(ix.Target);
                    ResolveExpr(ix.Index);
                    break;
                case UnaryExpr { Op: UnaryOp.Dereference } deref:
                    if (_unsafeDepth == 0)
                        throw new ResolverException(
                            "'*' als Zuweisungsziel ist nur innerhalb eines 'unsafe'-Blocks gültig", deref.Line);
                    ResolveExpr(deref.Operand);
                    break;
                default:
                    throw new ResolverException("Ungültiges Zuweisungsziel", target.Line);
            }
        }

        /// <summary>Ist `fieldName` als `readonly` in `cd` (NUR diese Klasse
        /// selbst, keine Basisklassen-Kette - bewusste Grenze) deklariert?</summary>
        private static bool IsReadonlyField(ClassDecl cd, string fieldName) =>
            cd.Members.Any(m => m is FieldDecl { IsReadonly: true } fd && fd.Name == fieldName);

        /// <summary>Lambdas sehen nur ihren eigenen Scope + global (SPEC 4.2): der
        /// neue Scope wird bewusst direkt an den globalen Scope gehängt, nicht an
        /// den aktuell umschließenden - dadurch "funktioniert" die eingeschränkte
        /// Sichtbarkeit einfach durch die normale Tiefen-Zählung beim Auflösen,
        /// ganz ohne Sonderfall dort. 'on obj' wird dagegen VOR dem Scope-Wechsel
        /// aufgelöst, weil es im umschließenden Kontext ausgewertet wird (dort, wo
        /// die Lambda selbst definiert wird), nicht innerhalb ihres Bodys.</summary>
        /// <summary>`fire { ... }`/`fire taking X { ... }`/`fire with actorA { ... }`
        /// (siehe Ast.FireStmt-Doku): jede `TakingCaptures`-Quelle sowie
        /// `WithSource` (falls vorhanden) werden ganz normal im AUFRUFENDEN
        /// Scope aufgelöst (die jeweilige Variable muss dort bereits
        /// deklariert sein - oder, bei der `fire MethodA(...)`-Aufrufform,
        /// ein synthetisches `this`/ein Argumentausdruck sein). Der Body
        /// dagegen bekommt einen KOMPLETT ISOLIERTEN Scope (`ResolverScope(null,
        /// isGlobal: true)` - bewusst NICHT `_globalScope` wie bei Lambdas, siehe
        /// FireStmt-Doku: der
        /// Fire-Block läuft auf einer eigenen VM-Instanz mit eigenem,
        /// frischen globalen Scope). `return` ist
        /// innerhalb eines Fire-Blocks nicht sinnvoll (keine Rückgabewerte,
        /// SPEC) - `_functionDepth` wird deshalb für die Dauer der
        /// Body-Auflösung auf 0 zurückgesetzt, damit ein `return` darin wie
        /// "außerhalb einer Funktion" abgelehnt wird.
        ///
        /// WICHTIG: Reihenfolge der Slot-Vergabe hier ist die "Wahrheit", mit
        /// der Compiler.CompileFireStmt und Runtime.FireRuntime.FireVmTaking
        /// übereinstimmen MÜSSEN - ZUERST alle Hauptprogramm-Globals (als
        /// READONLY-Schatten, an DENSELBEN Slots wie im Hauptprogramm - siehe
        /// unten), DANN alle TakingCaptures (in Listenreihenfolge), dann
        /// with. Das ermöglicht den in docs/THREADING_DESIGN.md Abschnitt 8
        /// vorgesehenen Read-only-Snapshot: der Fire-Thread bekommt bei
        /// seinem Start eine EIGENE, isolierte Kopie ALLER Hauptprogramm-
        /// Globals (siehe VM.OpCode.Fire) - lesend sichtbar unter demselben
        /// Namen wie im Hauptprogramm, aber NICHT beschreibbar (eine
        /// Zuweisung würde ja nur die lokale Kopie ändern, nie das Original
        /// - das wäre stillschweigend falsch, deshalb hier hart abgelehnt
        /// statt zugelassen).</summary>
        private void ResolveFireStmt(FireStmt fs)
        {
            foreach (var capture in fs.TakingCaptures)
                ResolveExpr(capture.Source);
            if (fs.WithSource != null)
                ResolveExpr(fs.WithSource);

            var saved = _current;
            _current = new ResolverScope(null, isGlobal: true);

            // Schatten-Einträge für ALLE Hauptprogramm-Globals, an deren
            // ORIGINAL-Slots (0..GlobalSlotCount-1) - der Laufzeit-Snapshot
            // (siehe VM.OpCode.Fire) kopiert exakt diese Slots 1:1 in den
            // frischen globalen Scope des Fire-Threads, deshalb müssen die
            // Slot-NUMMERN hier unverändert übernommen werden, nicht neu
            // vergeben werden. Per '#noshadow' abschaltbar (siehe
            // Ast.NoShadowDirective-Doku) - dann verhält sich alles wie vor
            // Einführung des Snapshots: taking/with bekommen ihre Slots
            // wieder ab 0.
            if (!_noShadowGlobals)
                foreach (var (name, slot) in _globalScope.Slots)
                {
                    // Ein Fire-Thread darf Globals ändern (docs/THREADING_DESIGN.md Abschnitt 7): einzelne Zuweisungen laufen als Sektion, die
                    // das Hauptprogramm bei `sync globals` erteilt - deshalb hier kein Schreibschutz mehr.
                    _current.Slots[name] = slot;
                }

            // taking/with - bekommen NEUE, EIGENE Slots ab hier (direkt
            // nach den Hauptprogramm-Globals, oder ab 0 bei aktivem
            // '#noshadow'). Kollidiert eine Erfassung NAMENTLICH mit einem
            // Hauptprogramm-Global, VERDRÄNGT sie dessen Schatten-Eintrag
            // (normale lexikalische Schattierung - die explizite, eigene
            // Erfassung ist im Fire-Block-Body dann gemeint, nicht das
            // gleichnamige Hauptprogramm-Global) und ist selbst ganz normal
            // beschreibbar wie bisher, NICHT readonly.
            int nextCaptureSlot = _noShadowGlobals ? 0 : _globalScope.Slots.Count;
            var capturesSeen = new HashSet<string>();
            foreach (var capture in fs.TakingCaptures)
            {
                if (!capturesSeen.Add(capture.VarName))
                    throw new ResolverException($"'{capture.VarName}' wurde in diesem 'fire' bereits erfasst", fs.Line);
                _current.Slots[capture.VarName] = nextCaptureSlot++;
                _current.ReadonlySlots.Remove(capture.VarName);
            }
            if (fs.WithVarName != null)
            {
                if (!capturesSeen.Add(fs.WithVarName))
                    throw new ResolverException($"'{fs.WithVarName}' wurde in diesem 'fire' bereits erfasst", fs.Line);
                _current.Slots[fs.WithVarName] = nextCaptureSlot++;
                _current.ReadonlySlots.Remove(fs.WithVarName);
            }

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            int savedFunctionDepth = _functionDepth;
            bool savedInConstructor = _inConstructor;
            _loopDepth = 0;
            _tryDepth = 0;
            _functionDepth = 0;
            _inConstructor = false;

            foreach (var s in fs.Body.Statements) ResolveStmt(s);

            _loopDepth = savedLoopDepth;
            _tryDepth = savedTryDepth;
            _functionDepth = savedFunctionDepth;
            _inConstructor = savedInConstructor;
            _current = saved;
        }

        /// <summary>Gemeinsame Body-Auflösung für `catch threads(...)`/`catch
        /// terminate(v)` (siehe Ast.CatchThreadsDecl/CatchTerminateDecl-Doku)
        /// - exakt dieselbe Isolation wie ResolveFireStmt (eigener, leerer
        /// globaler Scope, `return` verboten), aus demselben Grund: der
        /// Handler-Body läuft später als eigener, unabhängiger Proto,
        /// genestet in eine BELIEBIGE VM-Instanz (siehe VM.
        /// HandleDeliveredThreadException/RunTerminateHandlerIfAny), nicht im
        /// lexikalischen Kontext seiner Deklaration.</summary>
        /// <summary>Gemeinsame Body-Auflösung für `catch threads(...)`/`catch
        /// terminate(v)` (siehe Ast.CatchThreadsDecl/CatchTerminateDecl-Doku).
        ///
        /// WICHTIG, anders als bei ResolveFireStmt: der gebundene Parameter
        /// (`e`/`v`) muss LOKAL aufgelöst werden (`ResolvedRef.Local`), NICHT
        /// global (`ResolvedRef.Global`) - der Handler läuft NICHT auf einer
        /// eigenen, frischen VM-Instanz mit eigenem globalen Scope (wie ein
        /// fire-Block), sondern GENESTET INNERHALB der jeweils zustellenden
        /// VM-Instanz (siehe VM.HandleDeliveredThreadException/
        /// RunTerminateHandlerIfAny), die IHR EIGENES `_globalScope`-Feld
        /// weiterhin für das Hauptprogramm benutzt. Eine globale Auflösung
        /// des Parameters würde deshalb mit dessen SLOT 0 kollidieren (z.B.
        /// mit der ersten `var`-Deklaration des Hauptprogramms) - der
        /// Parameter braucht daher denselben Scope-Aufbau wie eine Lambda
        /// (`new ResolverScope(_globalScope)`, NICHT `isGlobal: true`): sieht
        /// die echten Globals zum NAMEN-Nachschlagen, wird selbst aber als
        /// LOKALE Variable von Tiefe 0 registriert - exakt das, was
        /// `handlerScope.DefineSlot(...)` in der VM zur Laufzeit befüllt.</summary>
        private void ResolveGlobalHandlerBody(string? varName, Stmt.BlockStmt body)
        {
            var saved = _current;
            _current = new ResolverScope(_globalScope);

            if (varName != null) Define(varName, body.Line);

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            int savedFunctionDepth = _functionDepth;
            bool savedInConstructor = _inConstructor;
            _loopDepth = 0;
            _tryDepth = 0;
            _functionDepth = 0;
            _inConstructor = false;

            foreach (var s in body.Statements) ResolveStmt(s);

            _loopDepth = savedLoopDepth;
            _tryDepth = savedTryDepth;
            _functionDepth = savedFunctionDepth;
            _inConstructor = savedInConstructor;
            _current = saved;
        }

        /// <summary>Lambda-Captures (SPEC 4.2): Jeder Name, den der Körper benutzt und der im UMSCHLIESSENDEN Code eine lokale Variable (oder ein
        /// Parameter) ist - nicht global, nicht Parameter der Lambda -, wird beim Erzeugen der Lambda als WERT kopiert und liegt im Lambda-Scope
        /// als Slot direkt hinter den Parametern. Die Namen werden vorab über den ganzen Körper gesammelt (auch in verschachtelten Lambdas),
        /// weil die Slot-Nummern feststehen müssen, bevor der Körper aufgelöst wird; ein zu viel erfasster Name (im Körper neu deklariert)
        /// kostet nur eine Kopie, siehe Define.</summary>
        private void DefineCaptures(LambdaExpr lambda, ResolverScope enclosing)
        {
            var names = new List<string>();
            AstNames.Collect(lambda.Body, names, new HashSet<string>());
            var paramNames = new HashSet<string>();
            foreach (var p in lambda.Params) paramNames.Add(p.Name);

            List<IdentifierExpr>? captures = null;
            foreach (var name in names)
            {
                if (paramNames.Contains(name)) continue;
                int depth = 0;
                ResolverScope? found = null;
                for (var scope = enclosing; scope != null && !scope.IsGlobal; scope = scope.Parent, depth++)
                {
                    if (scope.Slots.ContainsKey(name)) { found = scope; break; }
                }
                if (found == null) continue;

                found.RequiredUnits.TryGetValue(name, out var requiredUnit);
                var outer = new IdentifierExpr(lambda.Line, name);
                _refs[outer] = new ResolvedRef.Local(depth, found.Slots[name], requiredUnit);
                Define(name, lambda.Line, requiredUnit: requiredUnit);
                _current.CaptureNames.Add(name);
                (captures ??= new List<IdentifierExpr>()).Add(outer);
            }
            if (captures != null) _refs[lambda] = new ResolvedRef.LambdaCaptures(captures);
        }

        private void ResolveLambda(LambdaExpr lambda)
        {
            if (lambda.OnTarget != null)
                ResolveExpr(lambda.OnTarget);

            var saved = _current;
            var enclosing = _current;
            _current = new ResolverScope(_globalScope);

            // Ein Lambda, das INNERHALB eines Konstruktors definiert wird, läuft
            // typischerweise erst SPÄTER (nach Abschluss der Konstruktion) -
            // 'this.readonlyFeld = ...' darin wäre daher nicht sicher als
            // "noch während der Konstruktion" einzustufen. Für die Dauer des
            // Lambda-Bodies also bewusst so behandeln, als wäre man NICHT im
            // Konstruktor, unabhängig vom umgebenden Kontext.
            bool savedInConstructor = _inConstructor;
            _inConstructor = false;

            // Standardwerte VOR dem Definieren der Parameter auflösen (siehe
            // ResolveParamDefaults-Kommentar) - der Scope hier ist noch leer
            // (nur global als Parent), also automatisch isoliert von den
            // eigenen Parametern.
            ValidateOptionalParamsAreTrailing(lambda.Params, lambda.Line);
            foreach (var p in lambda.Params)
                if (p.DefaultValue != null)
                    ResolveExpr(p.DefaultValue);

            foreach (var p in lambda.Params)
            {
                if (p.Type != null) ValidateTypeRef(p.Type, lambda.Line);
                ResolveArrayRanks(p.ArrayRanks);
                Define(p.Name, lambda.Line, requiredUnit: p.Type?.Unit);
            }

            if (lambda.AutoCapture) DefineCaptures(lambda, enclosing);

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            _loopDepth = 0;
            _tryDepth = 0;

            _functionDepth++;
            foreach (var s in lambda.Body.Statements) ResolveStmt(s);
            _functionDepth--;

            _loopDepth = savedLoopDepth;
            _tryDepth = savedTryDepth;

            _inConstructor = savedInConstructor;
            _current = saved;
        }
    }

    /// <summary>Sammelt per Reflection alle Bezeichner (<see cref="IdentifierExpr"/>) unterhalb eines AST-Knotens, in der Reihenfolge des
    /// ersten Auftretens - unabhängig davon, welche Knotentypen es gibt (neue Syntax braucht hier keine Pflege). Nur für den Resolver,
    /// einmal je Lambda.</summary>
    internal static class AstNames
    {
        private static readonly Dictionary<Type, System.Reflection.MemberInfo[]> Members = new();

        public static void Collect(object? node, List<string> names, HashSet<string> seen)
        {
            switch (node)
            {
                case null:
                case string:
                    return;
                case IdentifierExpr id:
                    if (seen.Add(id.Name)) names.Add(id.Name);
                    return;
                case System.Collections.IEnumerable items:
                    foreach (var item in items) Collect(item, names, seen);
                    return;
            }

            var type = node.GetType();
            if (type.IsPrimitive || type.IsEnum) return;
            bool isAst = type.Namespace != null && type.Namespace.StartsWith("fire.Ast", StringComparison.Ordinal);
            bool isTuple = type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal);
            if (!isAst && !isTuple) return;

            System.Reflection.MemberInfo[] members;
            lock (Members)
            {
                if (!Members.TryGetValue(type, out members!))
                {
                    var list = new List<System.Reflection.MemberInfo>();
                    foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        if (p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract") list.Add(p);
                    foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        list.Add(f);
                    Members[type] = members = list.ToArray();
                }
            }
            foreach (var m in members)
            {
                object? value = m is System.Reflection.PropertyInfo pi ? pi.GetValue(node) : ((System.Reflection.FieldInfo)m).GetValue(node);
                Collect(value, names, seen);
            }
        }
    }
}
