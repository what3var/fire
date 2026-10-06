using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Values;
using MemoryPack;

namespace fire.Bytecode
{
    /// <summary>
    /// Kompiliertes Gegenstück zu einer ClassDecl: Felder (als 0-Arg-Protos, die
    /// mit gebundenem 'this' ausgewertet werden), Methoden (Name -> Liste von
    /// Protos, EINE pro überladener Parameteranzahl - siehe FindMethod), für
    /// virtuelle Auflösung über die Basisklassen-Kette), Konstruktor (IMMER
    /// vorhanden - wird synthetisiert, falls die Klasse keinen eigenen deklariert)
    /// und optional ein Destruktor.
    ///
    /// Destruktor-HINWEIS: Der Destruktor-Proto wird kompiliert, aber vom VM
    /// aktuell noch NICHT ausgeführt (siehe VM.RunDestructor) - die
    /// Kaskadenlöschung selbst (SPEC 2.3) funktioniert bereits über die
    /// Runtime-Schicht, nur der eigentliche destruct()-Methodenkörper läuft noch
    /// nicht, weil das während einer laufenden Scope-Auflösung eine erneute
    /// Verschachtelung der Interpreter-Schleife bräuchte, die hier bewusst noch
    /// nicht gebaut ist (Reentrancy-Risiko, siehe BYTECODE.md).
    /// </summary>
    /// 

    [MemoryPackable]
    public sealed partial class FieldInfo
    {
        public string? RequiredUnit { get; set; }

        public AccessModifier AccessModifier { get; set; }

        /// <summary>SPEC "Statische Mitglieder" - `true` für ein `static`
        /// deklariertes Feld: EINE geteilte Speicherstelle pro Klasse
        /// (RuntimeClass.StaticFieldValues/StaticFields), NICHT pro Instanz
        /// (ObjectInstance.Fields, siehe RuntimeClass.Fields).</summary>
        public bool IsStatic { get; set; }
    }

    [MemoryPackable]
    public sealed partial class RuntimeClass
    {
        public string Name { get; }

        /// <summary>Nur vom Compiler gebraucht (Basisklassen-Auflösung,
        /// SourceIndex für Multi-Datei-Debugging) - NICHT mehr von der VM zur
        /// Laufzeit (ObjectInstance trägt nur noch den Klassennamen, siehe
        /// dortige Doku, genau damit die komplette Stmt/Expr-AST-Hierarchie
        /// nicht an jedem Laufzeit-Objekt hängt). Deshalb von der geplanten
        /// Programm-Serialisierung ausgenommen - ein aus dem Cache geladenes
        /// Programm ist bereits fertig kompiliert, braucht den Quell-AST
        /// nicht mehr.</summary>
        [MemoryPackIgnore]
        public ClassDecl Decl { get; }
        public RuntimeClass? Base { get; set; }

        /// <summary>`actor Name { ... }` statt `class Name { ... }` (siehe
        /// Ast.ClassDecl.IsActor) - jede Instanz dieser Klasse bekommt bei
        /// `new` eine Mailbox (siehe VM.NewObject/Runtime.ObjectInstance.
        /// Mailbox). Läuft die Basisklassen-Kette hoch: eine Klasse, die von
        /// einem Actor erbt, ist selbst ebenfalls ein Actor (Erben von einer
        /// NICHT-Actor-Basis durch eine Actor-Klasse ist dagegen nicht
        /// sinnvoll möglich, da eine normale Klasse keine Mailbox-Semantik
        /// kennt - wird in dieser Ausbaustufe nicht eigens geprüft).
        ///
        /// War früher über Decl.IsActor abgeleitet - Decl ist jetzt aber
        /// [MemoryPackIgnore] (siehe dort), stünde nach dem Deserialisieren
        /// also nicht mehr zur Verfügung. Eigenes, echtes Feld statt
        /// berechneter Property, damit der Wert den Sprung über die
        /// Serialisierung übersteht - vom Compiler einmalig beim Anlegen der
        /// RuntimeClass gesetzt (siehe Compiler.CompileClasses).</summary>
        public bool IsActor { get; set; }

        /// <summary>Deklarierte Typen/Signaturen für die Reflection (nur wenn das Programm `#import "reflection"` nutzt), sonst null.</summary>
        public ClassMeta? Meta { get; set; }

        /// <summary>Die Interfaces, die diese Klasse in `class X : Basis, IFoo` nennt (für `wert is of IFoo`; geerbte kommen über die Basisklassen-Kette).</summary>
        public List<string> Interfaces { get; set; } = new();

        /// <summary>Klasse der Reflection-Bibliothek (`Reflect`, `Type`, `Member`, `Selector`): für die Zugriffsprüfung zählt der Aufrufer DAVOR.</summary>
        public bool IsReflectionHelper { get; set; }

        public List<(string Name, FunctionProto Init)> Fields { get; }

        /// <summary>Zugriffsmodifikator jedes in DIESER Klasse selbst
        /// deklarierten Feldes (nicht geerbter) - siehe FindFieldAccess für
        /// die Basisklassen-Kette. Vom Compiler direkt befüllt (siehe
        /// CompileClass), Default beim Fehlen eines Eintrags ist `Public`
        /// (siehe FindFieldAccess).</summary>
        public Dictionary<string, FieldInfo> OwnFieldInfo { get; }

        // ------------------------------------------------------------
        // Statische Mitglieder (SPEC "Statische Mitglieder") - EINE geteilte
        // Speicherstelle pro KLASSE statt pro Instanz. Zugriffsmodifikator/
        // geforderte Einheit eines statischen Feldes laufen bewusst über
        // DIESELBEN OwnFieldInfo-Einträge wie Instanzfelder (FieldInfo.
        // IsStatic unterscheidet nur, WO der eigentliche WERT liegt) - ein
        // Name ist pro Klasse ohnehin eindeutig, egal ob statisch oder nicht.
        // ------------------------------------------------------------

        /// <summary>Statische Feldwerte - lebt HIER direkt auf der
        /// RuntimeClass (nicht wie normale Felder in ObjectInstance.Fields),
        /// weil es davon je Klasse nur GENAU EINE Speicherstelle gibt, keine
        /// pro Instanz. Initialisiert einmalig beim Programmstart (siehe
        /// VM.RunStaticInitializers), danach ganz normal per GetStaticField/
        /// SetStaticField gelesen/geschrieben.</summary>
        public Dictionary<string, Value> StaticFieldValues { get; }

        /// <summary>Statische Feld-Initialisierer DIESER Klasse (Name -> 0-Arg-
        /// Proto) - wie Fields, aber getrennt geführt: laufen NICHT wie
        /// Fields bei JEDER `new`-Konstruktion, sondern GENAU EINMAL beim
        /// Programmstart (siehe VM.RunStaticInitializers), in
        /// Deklarationsreihenfolge.</summary>
        public List<(string Name, FunctionProto Init)> StaticFields { get; }

        /// <summary>Wie FindFieldAccess, aber für statische Felder: die
        /// Klasse, die `name` als statisches Feld SELBST deklariert
        /// (Basisklassen-Kette, eigene Klasse zuerst) - `null`, wenn keine
        /// Klasse in der Kette ein statisches Feld dieses Namens hat. Eine
        /// abgeleitete Klasse OHNE eigenes gleichnamiges statisches Feld
        /// teilt sich die Speicherstelle der Basisklasse (`Derived.X` und
        /// `Base.X` sind dann DASSELBE Feld, dieselbe StaticFieldValues-
        /// Instanz) - deklariert Derived selbst eins mit demselben Namen,
        /// ist es eine GETRENNTE, unabhängige Speicherstelle (verdeckt die
        /// der Basis, wie bei Instanzfeldern nicht möglich, aber bei
        /// statischen in den meisten OO-Sprachen üblich).</summary>
        public RuntimeClass? FindStaticFieldOwner(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.OwnFieldInfo.TryGetValue(name, out var info) && info.IsStatic)
                    return rc;
            return null;
        }

        /// <summary>Wie FindFieldAccess, aber für die geforderte Einheit -
        /// sucht über die Basisklassen-Kette (eigene Klasse zuerst) nach der
        /// Klasse, die `name` tatsächlich SELBST mit einer Einheit deklariert.
        /// `null`, wenn keine Klasse in der Kette für dieses Feld eine feste
        /// Einheit vorschreibt.</summary>
        public string? FindFieldRequiredUnit(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.OwnFieldInfo.TryGetValue(name, out var field))
                    return field.RequiredUnit;
            return null;
        }

        /// <summary>Wie FindMethod, aber für Felder: sucht über die
        /// Basisklassen-Kette (eigene Klasse zuerst) nach der Klasse, die
        /// `name` tatsächlich SELBST deklariert, samt ihrem
        /// Zugriffsmodifikator - `null`, wenn kein Feld dieses Namens
        /// irgendwo in der Kette deklariert ist (z.B. ein dynamisch über
        /// den Dictionary-Fallback gesetztes Feld, siehe Runtime.
        /// FieldStore - dafür gibt es keine Zugriffsprüfung, siehe VM.
        /// CheckFieldAccess).</summary>
        public (RuntimeClass DeclaringClass, AccessModifier Access)? FindFieldAccess(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.OwnFieldInfo.TryGetValue(name, out var field))
                    return (rc, field.AccessModifier);
            return null;
        }

        /// <summary>Alle Feldnamen dieser Klasse INKLUSIVE aller geerbten
        /// (Basis zuerst, rekursiv, dann die eigenen, jeweils in
        /// Deklarationsreihenfolge) - genau die Reihenfolge, in der
        /// ConstructBase + die eigenen Feld-Initialisierer sie zur Laufzeit
        /// auch tatsächlich setzen (siehe Compiler.CompileConstructorProto).
        /// Grundlage für FieldIndex/Runtime.FieldStore (SPEC-Optimierung:
        /// Feldzugriff über einen festen Array-Slot statt eines Dictionary-
        /// Lookups pro Zugriff, siehe docs/BYTECODE.md). Einmalig berechnet
        /// und gecacht - wie bei FindMethod (siehe dort) gilt: Fields/Base
        /// werden NUR während der einmaligen Kompilierung befüllt, nie
        /// danach zur Laufzeit verändert, der Cache ist deshalb dauerhaft
        /// gültig.</summary>

        //ZU MESSAGEPACK: ERSTMAL IGNORIEREN, WIRD IM ZWEIFEL SOWIESO NACHGEBAUT

        [MemoryPackIgnore]
        public IReadOnlyList<string> FlattenedFieldNames => _flattenedFieldNames ??= ComputeFlattenedFieldNames();
        
        private List<string>? _flattenedFieldNames;

        private List<string> ComputeFlattenedFieldNames()
        {
            var names = Base != null ? new List<string>(Base.FlattenedFieldNames) : new List<string>();
            foreach (var (name, _) in Fields) names.Add(name);
            return names;
        }

        /// <summary>Feldname -> fester Slot-Index in Runtime.FieldStore, für
        /// O(1)-Feldzugriff statt eines Dictionary-Lookups pro Instanz und
        /// Zugriff (siehe FlattenedFieldNames-Doku). Deklariert eine
        /// abgeleitete Klasse ein Feld mit demselben Namen wie eine
        /// Basisklasse erneut (ungewöhnlich, aber nicht verboten), gewinnt
        /// hier automatisch der SPÄTERE (eigene) Index - der geerbte Slot
        /// wird dadurch ungenutzt (etwas Speicher verschwendet, aber
        /// funktional unbedenklich: genau wie beim alten Dictionary-basierten
        /// Verhalten gewinnt am Ende ohnehin der letzte Schreibzugriff unter
        /// demselben Namen).</summary>

        //ZU MESSAGEPACK: ERSTMAL IGNORIEREN!

        [MemoryPackIgnore]
        public IReadOnlyDictionary<string, int> FieldIndex => _fieldIndex ??= ComputeFieldIndex();
        private Dictionary<string, int>? _fieldIndex;

        private Dictionary<string, int> ComputeFieldIndex()
        {
            var names = FlattenedFieldNames;
            var index = new Dictionary<string, int>(names.Count);
            for (int i = 0; i < names.Count; i++) index[names[i]] = i;
            return index;
        }

        /// <summary>Methodenname -> alle Überladungen dieses Namens in DIESER
        /// Klasse (jeweils mit unterschiedlicher Parameteranzahl - siehe
        /// FindMethod für die Auflösung nach Aufruf-Argumentzahl). Properties
        /// (get_X/set_X, siehe PropertyDecl-Doku) landen ebenfalls hier, als
        /// Liste mit genau einem Eintrag (keine Überladung für Properties).</summary>
        public Dictionary<string, List<FunctionProto>> Methods { get; }

        /// <summary>Konstruktor-Überladungen dieser Klasse, nach
        /// Parameteranzahl - anders als Methoden OHNE Basisklassen-Kette:
        /// `new Derived(...)` nutzt immer nur Deriveds EIGENE Konstruktoren,
        /// nie die der Basisklasse (die werden höchstens per `: base(...)`
        /// AUS einem eigenen Konstruktor heraus aufgerufen). Immer mindestens
        /// ein Eintrag (Arity 0) - wird synthetisiert, falls die Klasse
        /// keinen eigenen `construct` deklariert.</summary>
        public Dictionary<int, FunctionProto> Constructors { get; }

        /// <summary>Zugriffsmodifikator jedes Konstruktors, nach
        /// Parameteranzahl (parallel zu Constructors) - ein privater
        /// Konstruktor verhindert `new X(...)` von außerhalb der Klasse
        /// (klassisches Singleton-/Factory-Method-Muster), siehe VM.
        /// CheckConstructorAccess.</summary>
        public void AddConstructor(FunctionProto proto)
        {
            if (!Constructors.TryAdd(proto.ParamCount, proto))
                throw new System.InvalidOperationException(
                    $"Internal error: a constructor with {proto.ParamCount} parameters was registered twice.");
        }

        public FunctionProto? Destructor { get; set; }

        /// <summary>Hat diese Klasse oder eine ihrer Basisklassen einen Destruktor? Ohne einen gibt es beim Zerstören eines Objekts nichts
        /// auszuführen (siehe ObjectInstance.Destroy) - die Kette ist kurz, eine Abfrage kostet nur ein paar Zeigerzugriffe.</summary>
        public bool HasDestructorInChain()
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.Destructor != null) return true;
            return false;
        }

        public RuntimeClass(string name, ClassDecl decl)
        {
            Name = name;
            Decl = decl;
            Fields = new();
            OwnFieldInfo = new();
            StaticFieldValues = new();
            StaticFields = new();
            Methods = new();
            Constructors = new();
        }

        /// <summary>Für MemoryPack (siehe Decl-Doku und Chunk.Chunk(List&lt;byte&gt;,...)-
        /// Doku für die ausführliche Begründung) - Decl bleibt dabei `null!`
        /// (nicht gebraucht, nie gelesen nach dem Kompilieren). Die sechs
        /// Sammlungs-Parameter sind zwingend nötig, weil Fields/OwnFieldInfo/
        /// StaticFieldValues/StaticFields/Methods/Constructors KEINEN Setter
        /// haben (bewusst, siehe deren Doku) - ohne einen Konstruktor, der sie
        /// entgegennimmt, hätte der generierte Deserialisierer keine
        /// Möglichkeit, die aus dem Stream gelesenen Werte irgendwo
        /// unterzubringen, und würde sie stillschweigend verwerfen (Base/
        /// IsActor/Destructor betrifft das NICHT, die haben normale Setter).</summary>
        [MemoryPackConstructor]
        public RuntimeClass(string name, List<(string Name, FunctionProto Init)> fields,
            Dictionary<string, FieldInfo> ownFieldInfo, Dictionary<string, Value> staticFieldValues,
            List<(string Name, FunctionProto Init)> staticFields, Dictionary<string, List<FunctionProto>> methods,
            Dictionary<int, FunctionProto> constructors)
        {
            Name = name;
            Decl = null!;
            Fields = fields;
            OwnFieldInfo = ownFieldInfo;
            StaticFieldValues = staticFieldValues;
            StaticFields = staticFields;
            Methods = methods;
            Constructors = constructors;
        }

        /// <summary>Registriert eine (überladene) Methode unter ihrem Namen -
        /// wirft, falls in DIESER Klasse (nicht Basisklassen - dort ist
        /// erneutes Überladen mit derselben Arity in einer abgeleiteten
        /// Klasse als "Override" erlaubt, siehe FindMethod) bereits eine
        /// Überladung mit EXAKT derselben Parameteranzahl existiert -
        /// eigentlich schon vom Resolver abgefangen (siehe Resolver.
        /// ResolveClass), hier als zusätzliches Sicherheitsnetz auf
        /// Compiler-Ebene. `access` gilt für ALLE Überladungen dieses Namens
        /// zusammen (nicht pro einzelner Arity) - eine bewusste
        /// Vereinfachung: unterschiedliche Modifikatoren auf Überladungen
        /// desselben Namens sind ein seltener, nicht besonders sinnvoller
        /// Fall, der letzte kompilierte Aufruf gewinnt.</summary>
        public void AddMethod(string name, FunctionProto proto)
        {
            if (!Methods.TryGetValue(name, out var overloads))
            {
                overloads = new List<FunctionProto>();
                Methods[name] = overloads;
            }

            if (overloads.Any(p => p.ParamCount == proto.ParamCount))
                throw new System.InvalidOperationException(
                    $"Internal error: method '{name}' with {proto.ParamCount} parameters was registered twice.");

            overloads.Add(proto);
        }

        /// <summary>Sucht eine Methode über die Basisklassen-Kette (eigene
        /// Klasse zuerst) nach Namen UND Argumentanzahl - Grundlage der
        /// virtuellen Auflösung bei `obj.Method(...)`. Da die Sprache
        /// dynamisch typisiert ist, ist die Argumentanzahl das einzige zur
        /// Aufrufzeit sicher bekannte Unterscheidungsmerkmal zwischen
        /// Überladungen (eine Überladung nach TYP wäre nicht generell
        /// prüfbar). Sucht dabei über die GESAMTE Kette nach einer
        /// passenden Arity, nicht nur in der ERSTEN Klasse, die den Namen
        /// überhaupt kennt - eine abgeleitete Klasse kann also eine Methode
        /// gleichen Namens mit ANDERER Arity hinzufügen, ohne die
        /// geerbten Überladungen der Basisklasse zu verdecken. Akzeptiert
        /// auch eine Überladung mit MEHR Parametern als `argCount`, wenn die
        /// fehlenden (immer TRAILING) Parameter Standardwerte haben (siehe
        /// FindBestMatch) - ein exakter Treffer hat dabei immer Vorrang.</summary>
        /// <summary>Memoisiert FindMethod-Ergebnisse nach (Name, Argumentzahl) -
        /// die zugrunde liegenden Daten (Methods/Base) werden NUR während der
        /// EINMALIGEN Kompilierung befüllt (siehe Compiler.CompileClass),
        /// nie mehr danach zur Laufzeit verändert - der Cache ist deshalb ab
        /// dem ersten Treffer für immer gültig, keine Invalidierung nötig.
        /// Ohne das würde JEDER einzelne `obj.Methode(...)`-Aufruf zur
        /// Laufzeit die komplette Basisklassen-Kette erneut per
        /// Dictionary-Lookup + linearem Überladungs-Scan durchlaufen, auch
        /// wenn das Ergebnis (bei gleichbleibendem Aufrufort/gleicher
        /// Klasse) immer dasselbe ist.</summary>
        /// <summary>Memoisiert FindMethod-Ergebnisse nach (Name, Argumentzahl) -
        /// die zugrunde liegenden Daten (Methods/Base) werden NUR während der
        /// EINMALIGEN Kompilierung befüllt (siehe Compiler.CompileClass),
        /// nie mehr danach zur Laufzeit verändert - der Cache ist deshalb ab
        /// dem ersten Treffer für immer gültig, keine Invalidierung nötig.
        /// Ohne das würde JEDER einzelne `obj.Methode(...)`-Aufruf zur
        /// Laufzeit die komplette Basisklassen-Kette erneut per
        /// Dictionary-Lookup + linearem Überladungs-Scan durchlaufen, auch
        /// wenn das Ergebnis (bei gleichbleibendem Aufrufort/gleicher
        /// Klasse) immer dasselbe ist. Trägt zusätzlich zum Proto die
        /// DEKLARIERENDE Klasse und ihren Zugriffsmodifikator mit (siehe
        /// FindMethodWithAccess) - kostet nichts Zusätzliches, da der Walk
        /// die deklarierende Ebene ohnehin schon kennt, sobald er sie
        /// gefunden hat.</summary>
        private readonly Dictionary<(string Name, int ArgCount), (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access)> _methodCache = new();

        public FunctionProto? FindMethod(string name, int argCount) => FindMethodWithAccess(name, argCount).Proto;

        /// <summary>Wie FindMethod, liefert zusätzlich die Klasse, die
        /// `name` tatsächlich SELBST deklariert (für die Basisklassen-Kette
        /// relevant bei Vererbung/Overrides) samt ihrem Zugriffsmodifikator -
        /// siehe VM.CheckMethodAccess. DeclaringClass/Access sind bedeutungslos,
        /// wenn Proto `null` ist (keine passende Methode gefunden).</summary>
        public (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access) FindMethodWithAccess(string name, int argCount)
        {
            var key = (name, argCount);
            if (_methodCache.TryGetValue(key, out var cached))
                return cached;

            (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access) result = (null, null, AccessModifier.Public);
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.Methods.TryGetValue(name, out var overloads))
                {
                    var match = FindBestMatch(overloads, argCount);
                    if (match != null)
                    {
                        var access = match.Access ?? AccessModifier.Public;
                        result = (match, rc, access);
                        break;
                    }
                }

            _methodCache[key] = result;
            return result;
        }

        /// <summary>Sucht den Konstruktor mit dieser Argumentzahl - siehe
        /// Constructors-Doku (keine Basisklassen-Kette, anders als
        /// FindMethod). Akzeptiert wie FindMethod auch eine Überladung mit
        /// mehr Parametern, wenn die fehlenden Standardwerte haben.
        /// Exakter Treffer zuerst per direktem O(1)-Dictionary-Zugriff (statt
        /// über den allgemeinen linearen FindBestMatch-Scan, der für den
        /// häufigsten Fall - Klasse hat genau einen Konstruktor mit exakt
        /// passender Arity - unnötig wäre) - `Constructors` ist ja ohnehin
        /// schon nach Arity indiziert.</summary>
        public FunctionProto? FindConstructor(int argCount) =>
            Constructors.TryGetValue(argCount, out var exact) ? exact : FindBestMatch(Constructors.Values, argCount);

        /// <summary>Gemeinsame Auflösung für FindMethod/FindConstructor: ein
        /// exakter Arity-Treffer gewinnt immer; sonst die Überladung mit den
        /// WENIGSTEN Parametern unter allen, die (a) mehr Parameter als
        /// `argCount` haben UND (b) für jeden darüber hinausgehenden
        /// (Trailing-)Parameter einen Standardwert haben (siehe
        /// AllTrailingHaveDefaults) - die "engste" passende Überladung.</summary>
        private static FunctionProto? FindBestMatch(IEnumerable<FunctionProto> overloads, int argCount)
        {
            FunctionProto? exact = null;
            FunctionProto? bestWithDefaults = null;

            foreach (var proto in overloads)
            {
                if (proto.ParamCount == argCount)
                {
                    exact = proto;
                    continue;
                }
                if (proto.ParamCount > argCount && AllTrailingHaveDefaults(proto, argCount))
                    if (bestWithDefaults == null || proto.ParamCount < bestWithDefaults.ParamCount)
                        bestWithDefaults = proto;
            }

            return exact ?? bestWithDefaults;
        }

        /// <summary>Haben alle Parameter von `proto` ab Index `suppliedCount`
        /// (also alle, die bei einem Aufruf mit `suppliedCount` Argumenten
        /// FEHLEN würden) einen Standardwert? Öffentlich, da auch VM.
        /// CheckArity/FillDefaultArgs das für die Aufruf-Validierung und das
        /// tatsächliche Auffüllen brauchen (nicht nur die Überladungs-
        /// Auflösung hier in FindBestMatch).</summary>
        public static bool AllTrailingHaveDefaults(FunctionProto proto, int suppliedCount)
        {
            for (int i = suppliedCount; i < proto.ParamCount; i++)
                if (i >= proto.ParamDefaults.Count || proto.ParamDefaults[i] == null)
                    return false;
            return true;
        }

        /// <summary>Existiert IRGENDEINE Methode dieses Namens (unabhängig von
        /// der Argumentzahl), über die Basisklassen-Kette? Für
        /// Namenskonventions-Checks, die (noch) keine feste Arity haben -
        /// aktuell ungenutzt, da GetIndex/SetIndex/get_/set_ jeweils eine
        /// FESTE, bekannte Arity haben und direkt FindMethod(name, arity)
        /// nutzen; als Hilfsmethode für künftige Fälle vorgehalten.</summary>
        public bool HasMethod(string name)
        {
            for (var rc = this; rc != null; rc = rc.Base)
                if (rc.Methods.ContainsKey(name))
                    return true;
            return false;
        }

        /// <summary>`true`, wenn `name` (über die Basisklassen-Kette, wie
        /// FindMethod) eine statische Methode/Property-Accessor ist - für
        /// den Fehlerfall "ClassName.Method()" auf einer NICHT-statischen
        /// Methode (siehe VM.CallStaticMethod) bzw. umgekehrt "instanz.
        /// Method()" auf einer statischen. `argCount` wie bei FindMethod,
        /// da Überladungen unterschiedlicher Arity theoretisch unterschiedlich
        /// statisch sein könnten (SPEC macht dazu zwar keine Vorgabe, aber
        /// FunctionProto.IsStatic ist ohnehin pro Überladung gespeichert -
        /// kein Grund, das hier künstlich einzuschränken).</summary>
        public bool IsStaticMethod(string name, int argCount) => FindMethod(name, argCount)?.IsStatic ?? false;
    }
}
