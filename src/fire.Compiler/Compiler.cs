using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Bytecode;
using fire.Lexing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;
using MemoryPack;

namespace fire.Compiler
{
    /// <summary>
    /// Übersetzt den AST (nach Resolver-Lauf) in einen Chunk. Deckt aktuell ab:
    /// Literale, Variablen (Global/Local passend zu den Resolver-Slots),
    /// Arithmetik inkl. der Anker-Regel für Einheiten/Typen (SPEC 3.2),
    /// Vergleiche, Kurzschluss-'&amp;&amp;'/'||', unäre Operatoren, if/while/for,
    /// Blöcke mit echtem Ownership-Scope, Native-Calls, Lambdas (Funktions-/
    /// Call-Frames), Klassen/Objekte (`new`, Felder, Methoden inkl. virtueller
    /// Auflösung, `this`/`base`, Konstruktor-Verkettung).
    ///
    /// Noch NICHT abgedeckt (wirft NotSupportedException mit klarer Meldung):
    /// try/catch/throw/resume, foreach (Collections sind noch gar nicht
    /// entworfen), Destruktor-AUSFÜHRUNG (Deklaration/Kompilierung schon, siehe
    /// RuntimeClass-Kommentar) - das sind die nächsten Ausbaustufen.
    /// </summary>
    public sealed class Compiler
    {
        private readonly Chunk _chunk = new();
        private readonly IReadOnlyDictionary<Expr, ResolvedRef> _refs;
        private readonly NativeRegistry _natives;

        // -----------------------------------------------------------
        // break/continue: Scope-Tiefe und aktive Schleifen(n)
        // -----------------------------------------------------------

        /// <summary>Wie viele EnterScope-Opcodes seit Beginn DIESES
        /// Funktionskörpers ohne passendes ExitScope emittiert wurden -
        /// NICHT über Funktionsgrenzen hinweg gezählt, da jede Methode/jeder
        /// Konstruktor/jede Lambda mit einem FRISCHEN Compiler-Objekt
        /// kompiliert wird (siehe CompileMethodProto/CompileLambda: `new
        /// Compiler(_refs, _natives, ...)`) - dieses Feld startet also für
        /// jeden Funktionskörper automatisch wieder bei 0, ganz ohne
        /// manuelles Sichern/Zurücksetzen wie beim Resolver-Gegenstück
        /// (`Resolver._loopDepth`). Ausschließlich über EmitEnterScope/
        /// EmitExitScope verändert - NIE direkt `_chunk.EmitOp(OpCode.
        /// Enter/ExitScope)` aufrufen, sonst verliert `break`/`continue` die
        /// korrekte Anzahl an Scopes, die sie beim Sprung schließen müssen.</summary>
        private int _currentScopeDepth;

        /// <summary>Pro aktiver Schleife (verschachtelbar, daher ein Stack):
        /// die Scope-Tiefe GENAU beim Betreten des Schleifenkörpers (für die
        /// Anzahl nötiger ExitScope-Opcodes bei einem break/continue, siehe
        /// EmitScopeUnwindForJump) sowie die noch zu patchenden Sprungziele.
        /// `continue` und `break` sammeln ihre Sprungadressen hier, bis das
        /// jeweilige Ziel (Schleifenanfang/-ende) beim Fertigkompilieren der
        /// Schleife feststeht.</summary>
        private sealed class LoopCompileContext
        {
            public int ScopeDepthAtLoopBodyStart;
            public readonly List<int> BreakJumpPatchAddrs = new();
            public readonly List<int> ContinueJumpPatchAddrs = new();
        }

        private readonly Stack<LoopCompileContext> _loopStack = new();

        private void EmitEnterScope()
        {
            _chunk.EmitOp(OpCode.EnterScope);
            _currentScopeDepth++;
        }

        private void EmitExitScope()
        {
            _chunk.EmitOp(OpCode.ExitScope);
            _currentScopeDepth--;
        }

        /// <summary>Emittiert vor einem break/continue-Sprung so viele
        /// ExitScope-Opcodes wie nötig, um von der AKTUELLEN Scope-Tiefe
        /// zurück auf die Tiefe beim Betreten des Schleifenkörpers zu
        /// kommen - bewusst OHNE EmitExitScope (das würde `_currentScopeDepth`
        /// mitverändern): das sind rein "temporäre" Closes NUR für diesen
        /// einen Sprungpfad, die NORMALE sequentielle Kompilierung (z.B.
        /// das eigene ExitScope des Blocks, der das break/continue enthält)
        /// läuft danach unverändert weiter, als wäre nichts gewesen - der
        /// Bytecode direkt nach dem Sprung ist ohnehin unerreichbar (Jump
        /// ist unbedingt), genau wie bei `return` mitten in einem Block.</summary>
        private void EmitScopeUnwindForJump(LoopCompileContext ctx)
        {
            int toClose = _currentScopeDepth - ctx.ScopeDepthAtLoopBodyStart;
            for (int i = 0; i < toClose; i++)
                _chunk.EmitOp(OpCode.ExitScope);
        }

        /// <summary>Gemeinsame Kompilierung für `break`/`continue`: erst die
        /// zwischen hier und dem Schleifenkörper-Anfang offenen Scopes
        /// schließen (siehe EmitScopeUnwindForJump), dann ein unbedingter
        /// Sprung, dessen Ziel noch nicht feststeht - die Adresse wird in
        /// der passenden Liste (Break-/ContinueJumpPatchAddrs) gesammelt und
        /// erst beim Fertigkompilieren der jeweiligen Schleife (CompileWhile/
        /// CompileFor/CompileForeach) aufgelöst.</summary>
        private void CompileBreakOrContinue(bool isBreak)
        {
            var ctx = _loopStack.Peek();
            EmitScopeUnwindForJump(ctx);
            _chunk.EmitOp(OpCode.Jump);
            (isBreak ? ctx.BreakJumpPatchAddrs : ctx.ContinueJumpPatchAddrs).Add(_chunk.Here);
            _chunk.EmitU16(0);
        }

        // Nur gesetzt, während ein Feld-Initialisierer/Methoden-/Konstruktor-Body
        // dieser Klasse kompiliert wird - Grundlage dafür, `base.Method(...)`
        // statisch auf DIE Basisklasse aufzulösen, die zur deklarierenden Klasse
        // gehört (nicht zur tatsächlichen Laufzeit-Instanz, die bei mehrstufiger
        // Vererbung eine andere sein kann).
        private readonly RuntimeClass? _enclosingClass;

        /// <summary>Anzahl globaler Slots des HAUPTPROGRAMMS (siehe Resolving.
        /// Resolver.ResolveResult.GlobalSlotCount) - Grundlage für
        /// CompileFireStmt: der Fire-Block-Body läuft auf einer FRISCHEN
        /// VM-Instanz, deren global-Scope zur Laufzeit zuerst mit einem
        /// Snapshot ALLER Hauptprogramm-Globals an DEREN ORIGINAL-Slots
        /// befüllt wird (siehe Bytecode.VM.OpCode.Fire/Runtime.FireRuntime.
        /// FireVmTaking) - taking/with-Erfassungen bekommen deshalb ihre
        /// EIGENEN Slots erst AB diesem Wert, nicht ab 0 (siehe Resolving.
        /// Resolver.ResolveFireStmt für die passende Slot-Vergabe). MUSS
        /// durch JEDEN inneren Compiler weitergereicht werden (nicht nur den
        /// von CompileFireStmt selbst) - ein `fire {}` kann ja auch tief
        /// verschachtelt innerhalb einer Methode/Lambda/eines weiteren
        /// Fire-Blocks stehen.</summary>
        private readonly int _globalSlotCount;

        /// <summary>Quell-Index (Position in der `sources`-Liste, die an
        /// Parser.ParseMultiple ging) für TOP-LEVEL-Code (also AUSSERHALB
        /// jeder Klasse, inkl. einer dort direkt definierten Lambda - siehe
        /// CompileLambda) - innerhalb einer Klasse gilt stattdessen deren
        /// EIGENER `Ast.ClassDecl.SourceIndex` (siehe CurrentSourceIndex).
        /// Wird in der Top-Level-Schleife von Compile() VOR jeder Anweisung
        /// aus `sourceIndexByStmt` neu gesetzt (analog zum früheren
        /// `_topLevelUsings`-Muster) - wichtig, wenn MEHRERE der kombinierten
        /// Quellen eigenen Top-Level-Code haben.</summary>
        private int _topLevelSourceIndex;

        /// <summary>Der für die AKTUELL kompilierte Stelle geltende Quell-Index
        /// (SPEC "Mehrere Quelldateien") - innerhalb einer Klasse deren EIGENER
        /// `Ast.ClassDecl.SourceIndex`, außerhalb jeder Klasse
        /// `_topLevelSourceIndex` (siehe dort). An Chunk.MarkLine übergeben,
        /// damit ein Debugger (siehe Editor-Unterprojekt) bei mehreren
        /// Quelldateien weiß, in welcher Datei eine gegebene Zeile liegt.</summary>
        private int CurrentSourceIndex => _enclosingClass?.Decl.Source ?? _topLevelSourceIndex;

        /// <summary>Alle bekannten (vollqualifizierten) Klassennamen - Grundlage
        /// für ResolveTypeRef (SPEC "Namespaces"). Nicht readonly: der
        /// Top-Level-Compiler bekommt sie erst MITTEN in CompileClasses
        /// zugewiesen (nach dem Sammeln ALLER Klassennamen, aber VOR dem
        /// eigentlichen Kompilieren der Klassenkörper - ein Henne-Ei-Problem,
        /// wenn man sie erst NACH CompileClasses zuweisen würde, da
        /// CompileClasses selbst schon Klassenkörper kompiliert, die sie
        /// brauchen); jeder INNERE Compiler (Methoden-/Konstruktor-/Lambda-
        /// Body) bekommt sie dagegen direkt über den Konstruktor vom äußeren
        /// Compiler mit (zu DEM Zeitpunkt längst gesetzt). `null` nur in
        /// Testszenarien, die nie einen Namen auflösen müssten.</summary>
        private HashSet<string>? _knownClassNames;

        private Compiler(ResolveResult resolveResult, NativeRegistry natives)
            : this(resolveResult.References, natives, null, resolveResult.NoShadowGlobals ? 0 : resolveResult.GlobalSlotCount, null)
        {
        }

        /// <summary>Für die Kompilierung eines Lambda-/Methoden-/Konstruktor-Bodys
        /// in einen eigenen Chunk (FunctionProto): teilt sich die Resolver-
        /// Referenzen und die Native-Registry mit dem äußeren Compiler, baut aber
        /// einen eigenen, frischen Chunk.</summary>
        private Compiler(IReadOnlyDictionary<Expr, ResolvedRef> refs, NativeRegistry natives, RuntimeClass? enclosingClass, int globalSlotCount, HashSet<string>? knownClassNames)
        {
            _refs = refs;
            _natives = natives;
            _enclosingClass = enclosingClass;
            _globalSlotCount = globalSlotCount;
            _knownClassNames = knownClassNames;
        }

        /// <summary>Löst `tr` auf seinen vollqualifizierten Namen auf, WENN
        /// nötig (SPEC "Namespaces") - siehe TypeRef.ResolveBaseName für die
        /// genaue Regel. `tr.Namespaces` trägt den Kontext (aktueller
        /// Namespace + `#using`) schon direkt an sich selbst, gesetzt vom
        /// Parser GENAU an der Stelle, an der `tr` geparst wurde - der
        /// Compiler braucht dafür keinen eigenen "aktuelle Klasse"/"aktive
        /// Usings"-Zustand mehr.</summary>
        private string ResolveTypeRef(TypeRef tr) =>
            _knownClassNames != null ? tr.ResolveBaseName(_knownClassNames.Contains) : tr.BaseName;

        public static CompiledProgram Compile(
            IReadOnlyList<Stmt> program, ResolveResult resolveResult, NativeRegistry natives)
        {
            var compiler = new Compiler(resolveResult, natives);
            var classes = compiler.CompileClasses(program);

            // SPEC "Statische Mitglieder": statische Feld-Initialisierer
            // laufen GENAU EINMAL, vor dem eigentlichen Programm (anders als
            // Instanzfelder, die bei JEDER `new`-Konstruktion neu laufen) -
            // direkt hier an den ANFANG des TopLevel-Chunks emittiert, damit
            // sie exakt einmal laufen, in Klassen-/Felddeklarations-
            // reihenfolge, bevor der eigentliche Nutzer-Code beginnt.
            // Dummy-'this' (undefined): der Resolver verbietet 'this'/'super'
            // in einem statischen Feld-Initialisierer (siehe ResolveExpr/
            // ThisExpr), das Dummy wird also nie tatsächlich gelesen -
            // gebraucht nur, weil CallProtoWithThis (derselbe Opcode wie für
            // Instanzfeld-Initialisierer) IMMER ein 'this' auf dem Stack
            // erwartet.
            foreach (var rc in classes.Values)
            {
                foreach (var (fieldName, initProto) in rc.StaticFields)
                {
                    int protoIdx = compiler._chunk.AddFunctionProto(initProto);
                    compiler.EmitLoadConst(Value.MakeUndefined());
                    compiler._chunk.EmitOp(OpCode.CallProtoWithThis);
                    compiler._chunk.EmitU16(protoIdx);
                    compiler._chunk.EmitByte(0);
                    // SetStaticFieldOnInit statt SetStaticField - KEINE
                    // Zugriffsmodifikator-Prüfung (siehe dortige Doku), sonst
                    // würde ein PRIVATES statisches Feld schon bei seiner
                    // eigenen Initialisierung abgelehnt (diese läuft als
                    // Top-Level-Code, ohne zur eigenen Klasse passenden
                    // OwnerClass-Kontext). Pusht (anders als SetStaticField)
                    // auch nichts zurück, kein Pop nötig.
                    compiler._chunk.EmitOp(OpCode.SetStaticFieldOnInit);
                    compiler._chunk.EmitU16(compiler._chunk.AddConstant(Value.MakeString(rc.Name)));
                    compiler._chunk.EmitU16(compiler._chunk.AddConstant(Value.MakeString(fieldName)));
                }
            }

            foreach (var stmt in program)
            {
                compiler._topLevelSourceIndex = stmt.Source;
                compiler.CompileStmt(stmt);
            }
            compiler._chunk.EmitOp(OpCode.Halt);

            var externSignatures = new Dictionary<string, ExternSignature>();
            foreach (var (name, ed) in resolveResult.Externs)
            {
                externSignatures[name] = new ExternSignature
                {
                    LibName = ed.LibName,
                    ParamTypes = ed.Params.Select(p => p.Type).ToList(),
                    ReturnType = ed.ReturnType,
                };
            }

            return new CompiledProgram { TopLevel = compiler._chunk, Classes = classes, ExternSignatures = externSignatures };
        }

        // -----------------------------------------------------------
        // Klassen (Vorab-Pass: Name -> RuntimeClass, analog zum Resolver)
        // -----------------------------------------------------------
        private Dictionary<string, RuntimeClass> CompileClasses(IReadOnlyList<Stmt> program)
        {
            var classes = new Dictionary<string, RuntimeClass>();
            foreach (var stmt in program)
                if (stmt is ClassDecl cd)
                    classes[cd.Name] = new RuntimeClass(cd.Name, cd);

            // Basis-Verknüpfung getrennt, da Basisklassen im Quelltext später
            // stehen können als die abgeleitete Klasse (Vorwärtsreferenz). Der
            // Resolver hat schon geprüft, dass höchstens ein Name in BaseRefs
            // eine echte Klasse ist - hier also einfach den ersten solchen Namen
            // suchen. 'Exception' (eingebaute Basisklasse, SPEC 7.1) hat bewusst
            // keine eigene RuntimeClass - Klassen mit ': Exception' bekommen
            // hier schlicht Base = null (ihre eigenen Felder/Methoden/
            // Konstruktoren funktionieren trotzdem; beim Werfen/Fangen matcht
            // der Typname "Exception" ohnehin immer, siehe VM.ExceptionMatchesType).
            // Interface-Namen in BaseRefs werden hier ignoriert - Interfaces
            // brauchen keine eigene Laufzeit-Repräsentation (rein dynamischer
            // Methodenaufruf per Name), die Erfüllung hat schon der Resolver geprüft.
            foreach (var rc in classes.Values)
            {
                foreach (var baseRef in rc.Decl.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    string n = baseRef.ResolveBaseName(name => name == "Exception" || classes.ContainsKey(name));
                    if (n != "Exception" && !classes.TryGetValue(n, out _)) continue;
                    if (n == "Exception") break; // keine RuntimeClass verfügbar -> Base bleibt null
                    rc.Base = classes[n];
                    break;
                }
            }

            // RuntimeClass.IsActor ist jetzt ein echtes Feld statt einer
            // berechneten Property (siehe dortige Doku - wegen Decl.
            // [MemoryPackIgnore] für die geplante Serialisierung), hier
            // EINMALIG nach der Basisklassen-Verknüpfung oben berechnet -
            // dieselbe Logik wie die frühere Property, nur als expliziter
            // Kettenwalk statt rekursivem Property-Zugriff (unabhängig von
            // der Iterationsreihenfolge oben: jede Klasse geht ihre EIGENE
            // Kette ab, braucht also nicht, dass Base schon vorverarbeitet ist).
            foreach (var rc in classes.Values)
            {
                bool isActor = false;
                for (var walk = rc; walk != null; walk = walk.Base)
                    if (walk.Decl.IsActor) { isActor = true; break; }
                rc.IsActor = isActor;
            }

            // ERST jetzt (nach dem Sammeln ALLER Klassennamen, aber VOR dem
            // eigentlichen Kompilieren der Klassenkörper unten) - ResolveTypeRef
            // braucht die Menge ALLER Klassennamen, die genau HIER zum ersten
            // Mal vollständig feststeht. Ein Henne-Ei-Problem, wenn man sie
            // stattdessen NACH dieser ganzen Methode zuweisen würde: die
            // Körper-Kompilierung unten (CompileClassBody, u.a. `new X()`
            // INNERHALB von Methoden) braucht sie ja schon WÄHREND dieser
            // Methode noch läuft, nicht erst danach.
            _knownClassNames = new HashSet<string>(classes.Keys);

            foreach (var rc in classes.Values)
                CompileClassBody(rc);

            return classes;
        }

        private void CompileClassBody(RuntimeClass rc)
        {
            var ctorDecls = new List<ConstructorDecl>();

            foreach (var member in rc.Decl.Members)
            {
                switch (member)
                {
                    case FieldDecl fd:
                    {
                        var fieldInit = CompileFieldInitProto(rc, fd.Type, fd.Initializer);
                        rc.OwnFieldInfo[fd.Name] = new FieldInfo()
                        {
                            AccessModifier = fd.Access,
                            RequiredUnit = fd.Type?.Unit,
                            IsStatic = fd.IsStatic,
                        };
                        // SPEC "Statische Mitglieder": statische Felder landen
                        // NICHT in Fields (der Instanz-Init-Liste, die JEDE
                        // `new`-Konstruktion erneut durchläuft) - stattdessen
                        // in StaticFields, EINMALIG beim Programmstart
                        // ausgewertet (siehe RunStaticInitializers, aufgerufen
                        // direkt nach CompileClasses in Compile()).
                        if (fd.IsStatic)
                            rc.StaticFields.Add((fd.Name, fieldInit));
                        else
                            rc.Fields.Add((fd.Name, fieldInit));
                        // SPEC "Einheiten-Deklarationen": geprüft wird das
                        // NICHT hier beim Initialisieren (siehe
                        // CompileFieldInitProto - unverändert), sondern
                        // direkt in der VM bei JEDEM SetField-/SetStaticField-
                        // Aufruf - Feldzuweisungen sind (anders als lokale/
                        // globale Variablen) grundsätzlich dynamisch
                        // aufgelöst, die VM kennt zur Laufzeit die
                        // tatsächliche Klasse des Zielobjekts, der Compiler
                        // an dieser Stelle nicht.
                        break;
                    }

                    case MethodDecl md:
                        rc.AddMethod(md.Name, CompileMethodProto(rc, md.Params, md.Body, md.Access, md.IsStatic));
                        break;

                    case ConstructorDecl ctor:
                        ctorDecls.Add(ctor);
                        break;

                    case DestructorDecl dtor:
                        rc.Destructor = CompileMethodProto(rc, Array.Empty<LambdaParam>(), dtor.Body, AccessModifier.Public);
                        break;

                    case PropertyDecl pd:
                        // Namenskonvention 'get_'/'set_' (siehe Ast.PropertyDecl-
                        // Doku) - registriert als ganz normale Methoden, VM.
                        // GetField/SetField rufen sie per Namenskonvention auf,
                        // wenn kein gleichnamiges Feld existiert. Beide Accessoren
                        // teilen sich den EINEN Modifikator der Property selbst
                        // (SPEC kennt keine getrennten get/set-Modifikatoren) -
                        // genauso teilen sie sich das EINE IsStatic (SPEC kennt
                        // keine gemischt statisch/nicht-statischen Accessoren).
                        if (pd.Getter != null)
                            rc.AddMethod("get_" + pd.Name, CompileMethodProto(rc, Array.Empty<LambdaParam>(), pd.Getter, pd.Access, pd.IsStatic));
                        if (pd.Setter != null)
                        {
                            var setterParams = new[] { new LambdaParam("value", pd.Type, Array.Empty<Expr?>()) };
                            rc.AddMethod("set_" + pd.Name, CompileMethodProto(rc, setterParams, pd.Setter, pd.Access, pd.IsStatic));
                        }
                        break;
                }
            }

            // Keine eigene Deklaration -> genau EIN synthetisierter 0-Arg-public-
            // Konstruktor (Basis-Aufruf + Feld-Inits, sonst leer) - `new`
            // funktioniert dadurch immer einheitlich über denselben
            // Mechanismus. Mit eigenen Deklarationen: EINE Überladung pro
            // `construct(...)` (der Resolver hat schon geprüft, dass keine
            // zwei dieselbe Parameteranzahl haben).
            if (ctorDecls.Count == 0)
            {
                rc.AddConstructor(CompileConstructorProto(rc, null, AccessModifier.Public));
            }
            else
            {
                foreach (var ctor in ctorDecls)
                    rc.AddConstructor(CompileConstructorProto(rc, ctor, ctor.Access));
            }
        }

        /// <summary>Kompiliert für jeden Parameter mit Standardwert einen
        /// eigenen 0-Arg-Proto, der dessen DefaultValue-Ausdruck auswertet
        /// (siehe FunctionProto.ParamDefaults-Doku) - null an der Stelle für
        /// Pflichtparameter. Läuft im selben Compiler-Kontext (`rc`) wie die
        /// eigentliche Methode/der Konstruktor, damit z.B. `this.feld` als
        /// Standardwert funktioniert.</summary>
        private FunctionProto?[] CompileParamDefaults(RuntimeClass? rc, IReadOnlyList<LambdaParam> parms)
        {
            var defaults = new FunctionProto?[parms.Count];
            for (int i = 0; i < parms.Count; i++)
            {
                if (parms[i].DefaultValue == null) continue;
                var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames);
                inner.CompileExpr(parms[i].DefaultValue!);
                inner._chunk.EmitOp(OpCode.Return);
                defaults[i] = new FunctionProto(inner._chunk, 0, AccessModifier.Private);
            }
            return defaults;
        }

        private FunctionProto CompileFieldInitProto(RuntimeClass rc, TypeRef? type, Expr? initializer)
        {
            var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames);
            inner._chunk.OwnerClass = rc;
            if (initializer != null)
            {
                inner.CompileExpr(initializer);
                inner.EmitCheckLambdaSignatureIfNeeded(type);
            }
            else inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);
            return new FunctionProto(inner._chunk, 0, AccessModifier.Private);
        }

        /// <summary>Emittiert für jeden Parameter mit einer Lambda-Signatur-
        /// Typannotation (`lambda&lt;P1,...,Pn&gt;`, siehe Ast.TypeRef.
        /// LambdaSignature) eine Laufzeit-Prüfung GANZ AM ANFANG des
        /// Funktionskörpers (`inner`) - die Parameter-Slots sind zu diesem
        /// Zeitpunkt schon vom AUFRUFER befüllt (siehe VM.CallMethod/
        /// NewObject/Call - Parameterbindung passiert dort VOR dem Sprung in
        /// diesen Chunk, nicht innerhalb von dessen eigenem Bytecode), die
        /// Prüfung liest den Wert also einfach per LoadLocal zurück, prüft
        /// ihn (CheckLambdaSignature) und verwirft die Kopie wieder (Pop) -
        /// der eigentliche Slot-Wert bleibt unangetastet.</summary>
        private void EmitLambdaParamChecks(Compiler inner, IReadOnlyList<LambdaParam> parms)
        {
            for (int i = 0; i < parms.Count; i++)
            {
                var sig = parms[i].Type?.LambdaSignature;
                if (sig == null) continue;
                inner._chunk.EmitOp(OpCode.LoadLocal);
                inner._chunk.EmitU16(0);
                inner._chunk.EmitU16((ushort)i);
                inner._chunk.EmitOp(OpCode.CheckLambdaSignature);
                inner._chunk.EmitByte((byte)sig.ParamTypeNames.Count);
                inner._chunk.EmitOp(OpCode.Pop);
            }

            // SPEC "Einheiten-Deklarationen": ein Parameter mit explizitem
            // `: einheit` (siehe TypeRef.Unit) verlangt beim tatsächlichen
            // Aufruf GENAU diese Einheit im übergebenen Wert - dieselbe
            // LoadLocal+Prüfen+Pop-Technik wie oben für die Lambda-Signatur,
            // nur mit CheckUnit statt CheckLambdaSignature (siehe
            // EmitCheckUnitIfNeeded).
            for (int i = 0; i < parms.Count; i++)
            {
                string? unit = parms[i].Type?.Unit;
                if (unit == null) continue;
                inner._chunk.EmitOp(OpCode.LoadLocal);
                inner._chunk.EmitU16(0);
                inner._chunk.EmitU16((ushort)i);
                EmitCheckUnitIfNeeded(inner, unit);
                inner._chunk.EmitOp(OpCode.Pop);
            }
        }

        /// <summary>Emittiert (falls `unit` != null) einen CheckUnit-Opcode für
        /// den Wert, der GERADE OBEN auf dem Stack liegt (SPEC "Einheiten-
        /// Deklarationen") - prüft (in der VM), ob dessen Einheit exakt
        /// `unit` entspricht, wirft sonst eine `UnitMismatchException`
        /// (siehe VM.ThrowUnitMismatch). Peekt nur (siehe OpCode.CheckUnit-
        /// Doku) - der Aufrufer entscheidet selbst, ob/wann er den Wert
        /// danach noch braucht oder poppt.</summary>
        private static void EmitCheckUnitIfNeeded(Compiler target, string? unit)
        {
            if (unit == null) return;
            target._chunk.EmitOp(OpCode.CheckUnit);
            target._chunk.EmitU16(target._chunk.AddConstant(Value.MakeString(unit)));
        }

        /// <summary>`fire { ... }`/`fire taking X { ... }` (siehe Ast.FireStmt-
        /// Doku) - kompiliert den Body als EIGENEN, isolierten Chunk (0 oder 1
        /// Parameter, je nachdem ob `taking` verwendet wird - der Parameter
        /// wird aber NICHT über die normale Aufruf-Konvention gefüllt,
        /// sondern direkt von Runtime.FireRuntime.FireVmTaking per
        /// `scope.DefineSlot(...)`, BEVOR der Chunk zu laufen beginnt - siehe
        /// dort). Am Fire-Statement selbst wird (falls `taking` verwendet
        /// wird) der AKTUELLE Wert der Quellvariable im AUFRUFENDEN Kontext
        /// ausgewertet und auf den Stack gelegt, dann der neue `Fire`-Opcode
        /// emittiert, der ihn poppt und einen echten Thread startet.</summary>
        /// <summary>Reihenfolge MUSS mit Resolver.ResolveFireStmt (Slot-
        /// Vergabe) und dem VM.OpCode.Fire-Handler (Pop-Reihenfolge, dort
        /// umgekehrt, da Stack) übereinstimmen: alle TakingCaptures zuerst
        /// (in Listenreihenfolge), dann with.</summary>
        /// <summary>Reihenfolge MUSS mit Resolver.ResolveFireStmt (Slot-
        /// Vergabe: erst Hauptprogramm-Globals-Schatten, dann taking, dann
        /// with) und dem VM.OpCode.Fire-Handler (Pop-Reihenfolge, dort
        /// umgekehrt, da Stack) übereinstimmen. Der Chunk-Body selbst
        /// referenziert die Hauptprogramm-Globals (aufgelöst als normale
        /// `Global(slot)`-Referenzen an DEREN ORIGINAL-Slots, siehe
        /// ResolveFireStmt) ganz genauso wie jede taking/with-Erfassung -
        /// hier also nichts Besonderes zu kompilieren, nur die
        /// TakingCaptures/With-Slots müssen ab `_globalSlotCount` (statt ab
        /// 0) vergeben werden, damit sie nicht mit den Schatten-Slots
        /// kollidieren.</summary>
        private void CompileFireStmt(FireStmt fs)
        {
            var inner = new Compiler(_refs, _natives, null, _globalSlotCount, _knownClassNames);
            int slot = _globalSlotCount;
            foreach (var capture in fs.TakingCaptures)
                inner._chunk.MarkLocalName(0, slot++, capture.VarName);
            if (fs.WithVarName != null)
                inner._chunk.MarkLocalName(0, slot++, fs.WithVarName);
            foreach (var stmt in fs.Body.Statements) inner.CompileStmt(stmt);
            // BUGFIX: NICHT 'LoadConst Undefined; Return' wie bei einer
            // echten Methode (CompileMethodProto) - ein Fire-Block läuft als
            // eigene, oberste Ebene einer FRISCHEN VM-Instanz (siehe Runtime.
            // FireRuntime.FireVmTaking: `new VM(fireProto.Chunk, ...)`,
            // direkt als deren _currentChunk, NICHT über CallMethod
            // aufgerufen) - es existiert dort zu KEINEM Zeitpunkt ein
            // CallFrame. `Return`s Handler poppt aber ungeprüft von
            // _frames (einem Stack<CallFrame>) - bei einem leeren Stack
            // wirft das eine "Stack empty."-Exception GENAU beim Erreichen
            // des Chunk-Endes. Das blieb lange unbemerkt, weil
            // FireRuntime.Fire jede Exception aus dem Thread-Body selbst
            // abfängt (in FireThreadHandle.Error) und in der Sprachsyntax
            // niemand dieses Handle je prüft - der Fire-Thread "funktionierte"
            // äußerlich (alles VOR dem Chunk-Ende lief ja normal), starb
            // aber am Ende jedes Mal still mit dieser Exception. `Halt`
            // (wie beim Top-Level-Programm selbst, siehe Compiler.Compile)
            // beendet die VM dagegen korrekt ohne jede Frame-Erwartung.
            inner._chunk.EmitOp(OpCode.Halt);

            var proto = new FunctionProto(inner._chunk, slot, AccessModifier.Private);
            int protoIdx = _chunk.AddFunctionProto(proto);

            foreach (var capture in fs.TakingCaptures)
                CompileExpr(capture.Source);
            bool hasWith = fs.WithSource != null;
            if (hasWith) CompileExpr(fs.WithSource!);

            _chunk.EmitOp(OpCode.Fire);
            _chunk.EmitU16(protoIdx);
            _chunk.EmitU16(_globalSlotCount);
            _chunk.EmitByte((byte)fs.TakingCaptures.Count);
            _chunk.EmitByte(hasWith ? (byte)1 : (byte)0);
        }

        /// <summary>`catch threads(ExceptionType e) { ... }` / `catch threads() { ... }`
        /// (siehe Ast.CatchThreadsDecl-Doku) - kompiliert den Body als
        /// eigenen, isolierten Chunk (0 oder 1 Parameter, je nachdem ob eine
        /// Variable gebunden wird), registriert ihn dann per neuem Opcode
        /// GLOBAL (Bytecode.GlobalHandlers) - läuft später genestet in der
        /// jeweils zustellenden VM-Instanz (siehe VM.
        /// HandleDeliveredThreadException), nicht hier an dieser Stelle.</summary>
        private void CompileCatchThreadsDecl(CatchThreadsDecl decl)
        {
            var inner = new Compiler(_refs, _natives, null, _globalSlotCount, _knownClassNames);
            if (decl.VarName != null)
                inner._chunk.MarkLocalName(0, 0, decl.VarName);
            foreach (var stmt in decl.Body.Statements) inner.CompileStmt(stmt);
            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);

            var proto = new FunctionProto(inner._chunk, decl.VarName != null ? 1 : 0, AccessModifier.Public);
            int protoIdx = _chunk.AddFunctionProto(proto);

            _chunk.EmitOp(OpCode.RegisterThreadsCatch);
            _chunk.EmitU16(protoIdx);
            bool hasType = decl.TypeRef != null;
            _chunk.EmitByte(hasType ? (byte)1 : (byte)0);
            if (hasType) _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveTypeRef(decl.TypeRef!))));
        }

        /// <summary>`catch terminate(v) { ... }` (siehe Ast.CatchTerminateDecl-
        /// Doku) - wie CompileCatchThreadsDecl, aber ohne Typname (es gibt ja
        /// keine "Art" von terminate) und mit dem anderen Register-Opcode.</summary>
        private void CompileCatchTerminateDecl(CatchTerminateDecl decl)
        {
            var inner = new Compiler(_refs, _natives, null, _globalSlotCount, _knownClassNames);
            if (decl.VarName != null)
                inner._chunk.MarkLocalName(0, 0, decl.VarName);
            foreach (var stmt in decl.Body.Statements) inner.CompileStmt(stmt);
            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);

            var proto = new FunctionProto(inner._chunk, decl.VarName != null ? 1 : 0, AccessModifier.Public);
            int protoIdx = _chunk.AddFunctionProto(proto);

            _chunk.EmitOp(OpCode.RegisterTerminateCatch);
            _chunk.EmitU16(protoIdx);
        }

        private FunctionProto CompileMethodProto(RuntimeClass? rc, IReadOnlyList<LambdaParam> parms, Stmt.BlockStmt body, AccessModifier access, bool isStatic = false)
        {
            var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames);
            inner._chunk.OwnerClass = rc;
            // SPEC "Statische Mitglieder": eine statische Methode hat kein
            // gebundenes 'this' - der innere Compiler merkt sich das, um
            // 'this'/'super' im Körper abzulehnen (siehe Resolver statt
            // Compiler: die Prüfung selbst läuft im Resolver, VOR dem
            // Kompilieren, über ResolveResult - hier nur zur Vollständigkeit
            // erwähnt, keine eigene Prüfung an dieser Stelle nötig).
            for (int i = 0; i < parms.Count; i++)
                inner._chunk.MarkLocalName(0, i, parms[i].Name);
            EmitLambdaParamChecks(inner, parms);
            foreach (var stmt in body.Statements) inner.CompileStmt(stmt);
            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);
            return new FunctionProto(inner._chunk, parms.Count, access, CompileParamDefaults(rc, parms), isStatic);
        }

        /// <summary>Konstruktor-Proto: [Basis-Konstruktor-Aufruf (explizit mit
        /// `: base(...)` oder implizit ohne Argumente, falls eine Basisklasse
        /// existiert)] -> [eigene Feld-Initialisierer] -> [eigener Body] ->
        /// implizites `return undefined`. Wird auch synthetisiert, wenn die
        /// Klasse keinen eigenen `construct` deklariert (dann nur Basis-Aufruf +
        /// Feld-Inits, 0 Parameter) - `new` funktioniert dadurch immer
        /// einheitlich über denselben Mechanismus.</summary>
        private FunctionProto CompileConstructorProto(RuntimeClass rc, ConstructorDecl? ctor, AccessModifier access)
        {
            var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames);
            inner._chunk.OwnerClass = rc;

            if (ctor != null)
            {
                for (int i = 0; i < ctor.Params.Count; i++)
                    inner._chunk.MarkLocalName(0, i, ctor.Params[i].Name);
                EmitLambdaParamChecks(inner, ctor.Params);
            }

            if (rc.Base != null)
            {
                var baseArgs = ctor?.BaseArgs;
                if (baseArgs != null)
                    foreach (var a in baseArgs) inner.CompileExpr(a);

                inner._chunk.EmitOp(OpCode.ConstructBase);
                inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString(rc.Base.Name)));
                inner._chunk.EmitByte((byte)(baseArgs?.Count ?? 0));
                inner._chunk.EmitOp(OpCode.Pop); // Platzhalter-Rückgabewert des Basis-Konstruktors verwerfen
            }

            foreach (var (fieldName, initProto) in rc.Fields)
            {
                int protoIdx = inner._chunk.AddFunctionProto(initProto);
                inner._chunk.EmitOp(OpCode.LoadThis);
                inner._chunk.EmitOp(OpCode.CallProtoWithThis);
                inner._chunk.EmitU16(protoIdx);
                inner._chunk.EmitByte(0);
                inner._chunk.EmitOp(OpCode.SetFieldOnThis);
                inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString(fieldName)));
            }

            if (ctor != null)
                foreach (var stmt in ctor.Body.Statements) inner.CompileStmt(stmt);

            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);

            var paramDefaults = ctor != null ? CompileParamDefaults(rc, ctor.Params) : Array.Empty<FunctionProto?>();
            return new FunctionProto(inner._chunk, ctor?.Params.Count ?? 0, access, paramDefaults);
        }

        // -----------------------------------------------------------
        // Statements
        // -----------------------------------------------------------
        private void CompileStmt(Stmt stmt)
        {
            // Für den Step-Debugger im Editor-Unterprojekt (Bytecode.Chunk.
            // MarkLine) - markiert, an welcher Code-Position die aktuelle
            // Quelltextzeile beginnt. Rein additiv, keine Laufzeit-Wirkung.
            _chunk.MarkLine(CurrentSourceIndex, stmt.Line);

            switch (stmt)
            {
                case Stmt.BlockStmt block:
                    CompileBlockNewScope(block);
                    break;

                case ExprStmt es:
                    CompileExpr(es.Expression);
                    _chunk.EmitOp(OpCode.Pop);
                    break;

                case NoOpStmt:
                    break;

                case NoShadowDirective:
                    // Wie NoOpStmt - bereits vom Resolver in einem Vorab-Pass
                    // eingesammelt (siehe ResolveResult.NoShadowGlobals), hier
                    // nichts mehr zu tun. Ohne diesen Fall würde JEDES Programm
                    // mit einer '#noshadow'-Zeile mit einer NotSupportedException
                    // scheitern (siehe CompileStmt's default-Fall) - die Direktive
                    // landet als ganz normales Stmt in der Top-Level-Statement-
                    // Liste und wird deshalb hier, wie jedes andere Statement
                    // auch, kompiliert.
                    break;

                case VarDeclStmt vd:
                {
                    bool autoArrayAlloc = vd.Initializer == null && vd.ArrayRanks.Count > 0 && vd.ArrayRanks[0] != null;

                    if (vd.Initializer != null)
                    {
                        CompileExpr(vd.Initializer);
                        EmitCheckLambdaSignatureIfNeeded(vd.Type);
                    }
                    else if (autoArrayAlloc)
                        // `int arr[10]` / `int matrix[3][4]` ohne Initializer ->
                        // implizit `new int[10]` bzw. ein verschachteltes
                        // ("jagged") Array, jede Dimension per Laufzeit-Schleife
                        // befüllt (SPEC 8.4: mehrere `[...]`-Gruppen = Array von
                        // Arrays, keine echte rechteckige Matrix). Ein Rang ohne
                        // Größe (z.B. `int arr[3][]`) bricht die Rekursion ab -
                        // ab dort bleiben die Slots 'undefined', wie bisher bei
                        // einem komplett unbestimmt-großen Deklarator.
                        CompileArrayAlloc(vd.ArrayRanks, 0);
                    else
                        EmitLoadConst(Value.MakeUndefined());

                    // SPEC "Einheiten-Deklarationen": `var a : mm = ...`/
                    // `int a : mm = ...` - der Wert, der GERADE initial in
                    // den Slot geschrieben wird, muss die geforderte Einheit
                    // schon tragen (KEINE automatische Koersion, siehe
                    // Resolver-Antwort/SPEC - bewusst dieselbe Prüfung wie
                    // bei jeder SPÄTEREN Zuweisung an denselben Slot, siehe
                    // CompileAssign, sonst könnte man die Prüfung durch eine
                    // "unpassende" Erstzuweisung umgehen).
                    EmitCheckUnitIfNeeded(this, vd.Type?.Unit);

                    _chunk.EmitOp(OpCode.DeclareLocal);
                    break;
                }

                case IfStmt ifs:
                    CompileIf(ifs);
                    break;

                case WhileStmt ws:
                    CompileWhile(ws);
                    break;

                case ForStmt fs:
                    CompileFor(fs);
                    break;

                case ForeachStmt fes:
                    CompileForeach(fes);
                    break;

                case ReturnStmt rs:
                    if (rs.Value != null) CompileExpr(rs.Value);
                    else EmitLoadConst(Value.MakeUndefined());
                    _chunk.EmitOp(OpCode.Return);
                    break;

                case ThrowStmt th:
                    CompileExpr(th.Value);
                    _chunk.EmitOp(OpCode.Throw);
                    // Falls diese Exception später per resume() fortgesetzt wird,
                    // landet der resume-Wert genau hier auf dem Stack (siehe
                    // OpCode.ResumeException) - als Statement wird er nicht
                    // gebraucht, also wie jeder andere ExprStmt verwerfen. Ohne
                    // dieses Pop würde ein resume() hier den Stack verschieben.
                    _chunk.EmitOp(OpCode.Pop);
                    break;

                case TryStmt trys:
                    CompileTry(trys);
                    break;

                case BreakStmt:
                    // Resolver hat schon geprüft, dass wir in einer Schleife
                    // sind und keine try/catch/finally-Grenze überschritten
                    // wird - _loopStack.Peek() ist deshalb hier immer sicher.
                    CompileBreakOrContinue(isBreak: true);
                    break;

                case ContinueStmt:
                    CompileBreakOrContinue(isBreak: false);
                    break;

                case FireStmt fireStmt:
                    CompileFireStmt(fireStmt);
                    break;

                case LeaveStmt:
                    _chunk.EmitOp(OpCode.Leave);
                    break;

                case TerminateStmt terminateStmt:
                    if (terminateStmt.Value != null) CompileExpr(terminateStmt.Value);
                    else EmitLoadConst(Value.MakeUndefined());
                    _chunk.EmitOp(OpCode.Terminate);
                    break;

                case CatchThreadsDecl threadsDecl:
                    CompileCatchThreadsDecl(threadsDecl);
                    break;

                case CatchTerminateDecl terminateDecl:
                    CompileCatchTerminateDecl(terminateDecl);
                    break;

                case ProcessStmt processStmt:
                    CompileExpr(processStmt.Target);
                    _chunk.EmitOp(OpCode.Process);
                    break;

                case ClassDecl:
                    // Bereits im Vorab-Pass (CompileClasses) behandelt - hier nichts zu tun.
                    break;

                case InterfaceDecl:
                    // Interfaces brauchen keine eigene Laufzeit-Repräsentation
                    // (rein dynamischer Methodenaufruf per Name) - die Erfüllung
                    // hat schon der Resolver geprüft.
                    break;

                case EnumDecl:
                    // enum-Mitglieder werden vom Compiler bei jedem Zugriff
                    // ('EnumName.Mitglied') direkt zu einem Int-Literal
                    // aufgelöst (siehe MemberExpr-Fall unten) - die Deklaration
                    // selbst erzeugt keinen eigenen Code.
                    break;

                case ClassExtensionDecl cx:
                    // Sollte NIE hier ankommen - siehe derselbe Fall im
                    // Resolver (ResolveStmt) für die Erklärung.
                    throw new NotSupportedException(
                        $"Interner Fehler: 'class extends {cx.TargetRef.BaseName}' wurde nicht zusammengeführt " +
                        "(Programm muss über Parser.Parse()/ParseMultiple() erzeugt werden).");

                case ExternDecl:
                    // Reine Signatur-Deklaration, erzeugt selbst keinen Code (nur
                    // der Resolver braucht sie, um Aufrufe validieren zu können).
                    // Ein Aufruf `name(...)` kompiliert normal über CompileCall,
                    // sobald für 'name' eine native Implementierung registriert ist.
                    break;

                case UnsafeStmt us:
                    // 'unsafe' selbst erzeugt keinen eigenen Code - die
                    // Berechtigungsprüfung (Dereferenzierung/Address-of nur
                    // innerhalb eines solchen Blocks) macht schon der Resolver.
                    CompileBlockNewScope(us.Body);
                    break;

                default:
                    throw new NotSupportedException($"Statement {stmt.GetType().Name} wird nicht unterstützt.");
            }
        }

        /// <summary>Emittiert - falls `type` ein Lambda-Typ mit Signatur ist
        /// (`lambda&lt;P1,...,Pn&gt;`, siehe Ast.TypeRef.LambdaSignature) - eine
        /// CheckLambdaSignature-Prüfung für den WERT, der gerade oben auf dem
        /// Stack liegt (wird dabei nur GEPEEKT, nicht verbraucht - der Aufrufer
        /// nutzt ihn direkt danach normal weiter, z.B. per DeclareLocal). Ein
        /// `lambda`-Typ OHNE `&lt;...&gt;` (also ohne ParamTypeNames-Einträge)
        /// bedeutet "0 Parameter" (siehe SPEC "Lambda-Typen mit Signatur") und
        /// wird deshalb GENAUSO geprüft wie `lambda&lt;&gt;` - nicht "ungeprüft".
        /// Für jeden anderen Typ (auch gar keinen) ein No-Op.</summary>
        private void EmitCheckLambdaSignatureIfNeeded(TypeRef? type)
        {
            if (type?.LambdaSignature == null) return;
            _chunk.EmitOp(OpCode.CheckLambdaSignature);
            _chunk.EmitByte((byte)type.LambdaSignature.ParamTypeNames.Count);
        }


        private void CompileBlockNewScope(Stmt.BlockStmt block)
        {
            EmitEnterScope();
            foreach (var s in block.Statements) CompileStmt(s);
            EmitExitScope();
        }

        /// <summary>Kompiliert ein Statement als eigenen Scope - egal ob es schon
        /// ein Block ist oder ein einzelnes Statement (if/while/for-Body ohne
        /// '{}'). Muss exakt spiegeln, was Resolver.ResolveStmtAsScope tut, sonst
        /// stimmen Slot-/Tiefen-Nummern nicht mehr überein.</summary>
        private void CompileScopedBody(Stmt body)
        {
            EmitEnterScope();
            if (body is Stmt.BlockStmt block)
                foreach (var s in block.Statements) CompileStmt(s);
            else
                CompileStmt(body);
            EmitExitScope();
        }

        private void CompileIf(IfStmt s)
        {
            CompileExpr(s.Condition);
            _chunk.EmitOp(OpCode.JumpIfFalse);
            int elseJumpAt = _chunk.Here;
            _chunk.EmitU16(0); // Platzhalter, wird unten gepatcht

            CompileScopedBody(s.Then);

            if (s.Else != null)
            {
                _chunk.EmitOp(OpCode.Jump);
                int endJumpAt = _chunk.Here;
                _chunk.EmitU16(0);

                _chunk.PatchU16(elseJumpAt, _chunk.Here);
                CompileScopedBody(s.Else);
                _chunk.PatchU16(endJumpAt, _chunk.Here);
            }
            else
            {
                _chunk.PatchU16(elseJumpAt, _chunk.Here);
            }
        }

        private void CompileWhile(WhileStmt s)
        {
            var ctx = new LoopCompileContext { ScopeDepthAtLoopBodyStart = _currentScopeDepth };
            _loopStack.Push(ctx);

            int loopStart = _chunk.Here;
            CompileExpr(s.Condition);
            _chunk.EmitOp(OpCode.JumpIfFalse);
            int endJumpAt = _chunk.Here;
            _chunk.EmitU16(0);

            CompileScopedBody(s.Body);

            // 'continue' springt hierher - direkt vor den Rücksprung zur
            // Condition-Prüfung (für 'while' inhaltlich dasselbe wie
            // 'Jump loopStart' direkt, aber als eigene Adresse gehalten, damit
            // CompileFor/CompileForeach denselben Mechanismus mit einem
            // ANDEREN Ziel (Increment-Schritt bzw. vor dem Rücksprung) nutzen
            // können, ohne eine eigene Fallunterscheidung zu brauchen).
            int continueTarget = _chunk.Here;
            foreach (var addr in ctx.ContinueJumpPatchAddrs) _chunk.PatchU16(addr, continueTarget);

            _chunk.EmitOp(OpCode.Jump);
            _chunk.EmitU16(loopStart);

            int loopEnd = _chunk.Here;
            _chunk.PatchU16(endJumpAt, loopEnd);
            foreach (var addr in ctx.BreakJumpPatchAddrs) _chunk.PatchU16(addr, loopEnd);

            _loopStack.Pop();
        }

        private void CompileFor(ForStmt s)
        {
            // Umschließender Scope für Init (SPEC/Resolver: Init/Condition/
            // Increment/Body teilen sich einen gemeinsamen Scope).
            EmitEnterScope();
            if (s.Init != null) CompileStmt(s.Init);

            var ctx = new LoopCompileContext { ScopeDepthAtLoopBodyStart = _currentScopeDepth };
            _loopStack.Push(ctx);

            int loopStart = _chunk.Here;
            int endJumpAt = -1;
            if (s.Condition != null)
            {
                CompileExpr(s.Condition);
                _chunk.EmitOp(OpCode.JumpIfFalse);
                endJumpAt = _chunk.Here;
                _chunk.EmitU16(0);
            }

            CompileScopedBody(s.Body);

            // 'continue' springt HIERHER - VOR das Increment, damit das bei
            // einem 'continue' trotzdem noch läuft (sonst würde z.B.
            // 'for (i=0; i<10; i=i+1) { if (x) continue }' nie i erhöhen -
            // eine Endlosschleife).
            int continueTarget = _chunk.Here;
            foreach (var addr in ctx.ContinueJumpPatchAddrs) _chunk.PatchU16(addr, continueTarget);

            if (s.Increment != null)
            {
                CompileExpr(s.Increment);
                _chunk.EmitOp(OpCode.Pop);
            }

            _chunk.EmitOp(OpCode.Jump);
            _chunk.EmitU16(loopStart);

            int loopEnd = _chunk.Here;
            if (endJumpAt >= 0) _chunk.PatchU16(endJumpAt, loopEnd);
            foreach (var addr in ctx.BreakJumpPatchAddrs) _chunk.PatchU16(addr, loopEnd);

            _loopStack.Pop();
            EmitExitScope();
        }

        /// <summary>`foreach (x in iterable) { body }` - rein dynamisch über
        /// Methodenaufrufe nach Namen (`GetEnumerator`/`MoveNext`/`GetCurrent`),
        /// funktioniert also auf allem, das diese drei Methoden hat, nicht nur
        /// auf offiziell 'IEnumerable'-deklarierten Klassen (Duck-Typing, wie
        /// Methodenaufruf hier ohnehin überall funktioniert). Der Enumerator
        /// selbst lebt bewusst nur auf dem Werte-Stack (per Dup dupliziert),
        /// nicht in einem Scope-Slot - der Resolver kennt für `foreach` nur EINEN
        /// Scope (den für die Schleifenvariable, siehe Resolver.ResolveForeach),
        /// ein zusätzlicher Slot für den Enumerator hätte dessen Tiefen-
        /// Berechnungen inkonsistent gemacht. Für break/continue bedeutet das:
        /// der Enumerator braucht KEINE eigene Sonderbehandlung beim Sprung -
        /// 'break' zu `loopEnd` läuft ohnehin in das gemeinsame, abschließende
        /// Pop (siehe unten), 'continue' zu `continueTarget` rührt den
        /// Enumerator gar nicht an (bleibt einfach auf dem Stack liegen, wie
        /// bei jeder normalen Iteration auch).</summary>
        private void CompileForeach(ForeachStmt fs)
        {
            CompileExpr(fs.Iterable);
            EmitCallMethodByName("GetEnumerator", 0);
            // Stack: [enumerator]

            var ctx = new LoopCompileContext { ScopeDepthAtLoopBodyStart = _currentScopeDepth };
            _loopStack.Push(ctx);

            int loopStart = _chunk.Here;
            _chunk.EmitOp(OpCode.Dup);
            EmitCallMethodByName("MoveNext", 0);
            // Stack: [enumerator, bool]

            _chunk.EmitOp(OpCode.JumpIfFalse); // pop bool
            int endJumpAt = _chunk.Here;
            _chunk.EmitU16(0);
            // Stack (bei true): [enumerator]

            _chunk.EmitOp(OpCode.Dup);
            EmitCallMethodByName("GetCurrent", 0);
            // Stack: [enumerator, current]

            EmitEnterScope(); // entspricht Resolver.PushScope() für die Schleifenvariable
            _chunk.EmitOp(OpCode.DeclareLocal); // pop 'current', Slot 0 dieser neuen Scope
            // Stack: [enumerator]

            CompileScopedBody(fs.Body); // entspricht Resolver.ResolveStmtAsScope(body)
            EmitExitScope(); // entspricht PopScope()

            int continueTarget = _chunk.Here;
            foreach (var addr in ctx.ContinueJumpPatchAddrs) _chunk.PatchU16(addr, continueTarget);

            _chunk.EmitOp(OpCode.Jump);
            _chunk.EmitU16(loopStart);

            int loopEnd = _chunk.Here;
            _chunk.PatchU16(endJumpAt, loopEnd);
            foreach (var addr in ctx.BreakJumpPatchAddrs) _chunk.PatchU16(addr, loopEnd);
            _chunk.EmitOp(OpCode.Pop); // Enumerator-Referenz verwerfen

            _loopStack.Pop();
        }

        private void EmitCallMethodByName(string methodName, int argCount)
        {
            _chunk.EmitOp(OpCode.CallMethod);
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(methodName)));
            _chunk.EmitByte((byte)argCount);
        }

        /// <summary>`try { A } catch(T1 e1) { B1 } catch(e2) { B2 } finally { C }`:
        ///
        ///   RegisterHandler [Catches: (T1,addrB1), (null,addrB2)], FinallyProtoIdx
        ///   &lt;A&gt;
        ///   UnregisterHandler
        ///   Jump finallyOrEnd
        /// addrB1: &lt;B1&gt;  ExitScope  Jump finallyOrEnd
        /// addrB2: &lt;B2&gt;  ExitScope  Jump finallyOrEnd
        /// finallyOrEnd: &lt;C inline, falls vorhanden&gt;
        ///
        /// Ein passender `throw` springt direkt zu addrB1/addrB2 (nachdem die VM
        /// bis zum registrierten Ziel-Scope/-Frame abgewickelt hat), mit einer
        /// von der VM frisch angelegten Scope, die die Exception-Variable schon
        /// an Slot 0 enthält - die Catch-Bodies selbst brauchen deshalb kein
        /// eigenes EnterScope/DeclareLocal, nur ein abschließendes ExitScope.
        /// FinallyProtoIdx (separat kompiliert) wird nur gebraucht, wenn eine
        /// Exception an DIESEM Handler vorbei nach außen weiterpropagiert (siehe
        /// HandlerTemplate-Kommentar).</summary>
        /// <summary>Kompiliert die Allokation eines (ggf. mehrdimensionalen/
        /// "jagged") Arrays für einen Deklarator wie `int matrix[3][4]`:
        /// äußeres Array allozieren, und falls der NÄCHSTE Rang ebenfalls eine
        /// Größe hat, jedes Element per Laufzeit-Schleife (die Größen sind
        /// Ausdrücke, keine Compile-Zeit-Konstanten - deshalb echte Bytecode-
        /// Schleife statt Unrolling) mit einem rekursiv allozierten inneren
        /// Array befüllen. Lässt am Ende genau EINEN Wert (das fertige äußere
        /// Array) auf dem Stack. Ein Rang ohne Größe (z.B. das zweite `[]` in
        /// `int arr[3][]`) beendet die Rekursion - ab dort bleiben die Slots
        /// 'undefined', wie ein komplett unbestimmt-großer Deklarator das
        /// schon immer war. `EnterScope`/`ExitScope` hier sind unbedenklich,
        /// obwohl der fertige Wert am Ende noch gebraucht wird: Arrays hängen
        /// (anders als class-Instanzen) NICHT am Ownership-System, `Release()`
        /// beim `ExitScope` betrifft also nur die temporären Slots selbst,
        /// nicht den Array-WERT, auf den sie gerade noch gezeigt haben.</summary>
        private void CompileArrayAlloc(IReadOnlyList<Expr?> ranks, int rankIndex)
        {
            CompileExpr(ranks[rankIndex]!);
            _chunk.EmitOp(OpCode.NewArray);

            bool hasNextRank = rankIndex + 1 < ranks.Count && ranks[rankIndex + 1] != null;
            if (!hasNextRank) return;

            _chunk.EmitOp(OpCode.EnterScope);
            _chunk.EmitOp(OpCode.DeclareLocal); // Slot 0: äußeres Array (konsumiert den Stack-Top)
            EmitLoadConst(Value.MakeInt(0));
            _chunk.EmitOp(OpCode.DeclareLocal); // Slot 1: Schleifenindex i

            int loopStart = _chunk.Here;
            _chunk.EmitOp(OpCode.LoadLocal); _chunk.EmitU16(0); _chunk.EmitU16(1); // i
            _chunk.EmitOp(OpCode.LoadLocal); _chunk.EmitU16(0); _chunk.EmitU16(0); // arr
            _chunk.EmitOp(OpCode.GetField);
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString("length")));
            _chunk.EmitOp(OpCode.Lt);
            _chunk.EmitOp(OpCode.JumpIfFalse);
            int endJumpAt = _chunk.Here;
            _chunk.EmitU16(0);

            // arr[i] = <rekursiv alloziertes inneres Array>
            _chunk.EmitOp(OpCode.LoadLocal); _chunk.EmitU16(0); _chunk.EmitU16(0); // arr
            _chunk.EmitOp(OpCode.LoadLocal); _chunk.EmitU16(0); _chunk.EmitU16(1); // i
            CompileArrayAlloc(ranks, rankIndex + 1);
            _chunk.EmitOp(OpCode.ArraySet);
            _chunk.EmitOp(OpCode.Pop); // ArraySet lässt den zugewiesenen Wert auf dem Stack (wie jede Zuweisung) - hier als Statement verwerfen

            // i = i + 1
            _chunk.EmitOp(OpCode.LoadLocal); _chunk.EmitU16(0); _chunk.EmitU16(1);
            EmitLoadConst(Value.MakeInt(1));
            _chunk.EmitOp(OpCode.Add);
            _chunk.EmitOp(OpCode.StoreLocal); _chunk.EmitU16(0); _chunk.EmitU16(1);
            _chunk.EmitOp(OpCode.Pop); // StoreLocal lässt den zugewiesenen Wert auf dem Stack - verwerfen

            _chunk.EmitOp(OpCode.Jump);
            _chunk.EmitU16(loopStart);

            _chunk.PatchU16(endJumpAt, _chunk.Here);

            // Fertiges äußeres Array vor dem ExitScope zurück auf den Stack
            // (siehe Doc-Kommentar oben - unbedenklich, da Arrays nicht am
            // Ownership-System hängen).
            _chunk.EmitOp(OpCode.LoadLocal); _chunk.EmitU16(0); _chunk.EmitU16(0);
            _chunk.EmitOp(OpCode.ExitScope);
        }

        private void CompileTry(TryStmt t)
        {
            FunctionProto? finallyProto = t.Finally != null
                ? CompileMethodProto(_enclosingClass, Array.Empty<LambdaParam>(), t.Finally, AccessModifier.Private)
                : null;

            var template = new HandlerTemplate
            {
                FinallyProtoIdx = finallyProto != null ? _chunk.AddFunctionProto(finallyProto) : null,
            };
            int templateIdx = _chunk.AddHandlerTemplate(template);

            _chunk.EmitOp(OpCode.RegisterHandler);
            _chunk.EmitU16(templateIdx);

            CompileBlockNewScope(t.TryBlock);

            _chunk.EmitOp(OpCode.UnregisterHandler);

            _chunk.EmitOp(OpCode.Jump);
            var jumpsToFinallyOrEnd = new List<int> { _chunk.Here };
            _chunk.EmitU16(0);

            foreach (var c in t.Catches)
            {
                int catchAddr = _chunk.Here;
                template.Catches.Add((c.TypeRef == null ? null : ResolveTypeRef(c.TypeRef), catchAddr));

                foreach (var stmt in c.Body.Statements) CompileStmt(stmt);

                // Falls diese Exception nie per resume() fortgesetzt wurde (der
                // catch-Block also ganz normal hier ankommt), muss der beim
                // Werfen eingefrorene Wurfstellen-Zustand jetzt nachträglich
                // sauber verworfen werden (siehe VM.ClearPendingResume) - die
                // Exception-Variable liegt an dieser Stelle immer an Depth 0/
                // Slot 0 der von der VM frisch erzeugten catch-Scope.
                _chunk.EmitOp(OpCode.LoadLocal);
                _chunk.EmitU16(0);
                _chunk.EmitU16(0);
                _chunk.EmitOp(OpCode.ClearPendingResume);

                _chunk.EmitOp(OpCode.ExitScope); // gibt die von der VM erzeugte Exception-Scope wieder frei

                _chunk.EmitOp(OpCode.Jump);
                jumpsToFinallyOrEnd.Add(_chunk.Here);
                _chunk.EmitU16(0);
            }

            int finallyOrEndAddr = _chunk.Here;
            foreach (var addr in jumpsToFinallyOrEnd) _chunk.PatchU16(addr, finallyOrEndAddr);

            if (t.Finally != null) CompileBlockNewScope(t.Finally);
        }

        // -----------------------------------------------------------
        // Ausdrücke
        // -----------------------------------------------------------
        private void CompileExpr(Expr expr)
        {
            switch (expr)
            {
                case LiteralExpr lit:
                    EmitLoadConst(lit.Value);
                    break;

                case IdentifierExpr id:
                    CompileIdentifierLoad(id);
                    break;

                case UnaryExpr u:
                    CompileUnary(u);
                    break;

                case BinaryExpr b:
                    CompileBinary(b);
                    break;

                case UnitCoerceExpr:
                case TypeCoerceExpr:
                    CompileStandaloneCoercion(expr);
                    break;

                case AssignExpr a:
                    CompileAssign(a);
                    break;

                case IncDecExpr incDec:
                    CompileIncDec(incDec);
                    break;

                case ThisExpr:
                    _chunk.EmitOp(OpCode.LoadThis);
                    break;

                case BaseExpr:
                    throw new NotSupportedException(
                        "'base' ist nur als 'base.Methode(...)' gültig, nicht als eigenständiger Wert.");

                case IsInExpr iin:
                    CompileExpr(iin.Operand);
                    _chunk.EmitOp(OpCode.IsInUnit);
                    _chunk.EmitU16(_chunk.AddUnit(fire.Values.Unit.Parse(iin.UnitName)));
                    break;

                case IsOfExpr iof:
                    CompileExpr(iof.Operand);
                    _chunk.EmitOp(OpCode.IsOfType);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveTypeRef(iof.TypeRef))));
                    break;

                case IsFromExpr ifr:
                    CompileExpr(ifr.Operand);
                    CompileExpr(ifr.OwnerExpr);
                    _chunk.EmitOp(OpCode.IsFrom);
                    _chunk.EmitByte(ifr.Transitive ? (byte)1 : (byte)0);
                    break;

                case NewExpr ne:
                    foreach (var a in ne.Args) CompileExpr(a);
                    _chunk.EmitOp(OpCode.NewObject);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveTypeRef(ne.ClassRef))));
                    _chunk.EmitByte((byte)ne.Args.Count);
                    break;

                case NewArrayExpr na:
                    CompileArrayAlloc(na.SizeExprs, 0);
                    break;

                case NewBufferExpr nb:
                    CompileExpr(nb.SizeExpr);
                    _chunk.EmitOp(OpCode.MakeBuffer);
                    break;

                case ArrayLiteralExpr al:
                    foreach (var el in al.Elements) CompileExpr(el);
                    _chunk.EmitOp(OpCode.MakeArrayLiteral);
                    _chunk.EmitU16(al.Elements.Count);
                    break;

                case InterpolatedStringExpr ise:
                {
                    // Baut das Ergebnis als Kette von String-Konkatenationen
                    // über den ganz normalen '+'-Opcode auf (der bereits
                    // JEDEN Wert über ToString() anhängt, sobald eine Seite
                    // ein String ist, siehe Value.Add) - kein eigener
                    // "String-Aufbau"-Mechanismus nötig. Ein Format-
                    // Spezifizierer (':X' etc.) wandelt den Ausdruckswert
                    // VOR der Konkatenation über OpCode.FormatValue explizit
                    // in einen (formatierten) String um, statt ToString()
                    // dafür zu verwenden.
                    EmitLoadConst(Value.MakeString(""));
                    foreach (var part in ise.Parts)
                    {
                        if (part is InterpolationTextPart tp)
                        {
                            EmitLoadConst(Value.MakeString(tp.Text));
                        }
                        else if (part is InterpolationExprPart ep)
                        {
                            CompileExpr(ep.Expression);
                            if (ep.Format != null)
                            {
                                _chunk.EmitOp(OpCode.FormatValue);
                                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ep.Format)));
                            }
                        }
                        _chunk.EmitOp(OpCode.Add);
                    }
                    break;
                }

                case MemberExpr me:
                    // 'EnumName.Mitglied' wurde vom Resolver schon zu einem
                    // festen Int-Wert aufgelöst (ResolvedRef.EnumMember) - dann
                    // direkt als Konstante laden, keine Laufzeit-Feldzugriff-
                    // Logik nötig (der Compiler versucht in diesem Fall auch
                    // NICHT, me.Target als Bezeichner zu kompilieren - es gibt
                    // ja gar keine Variable dieses Namens).
                    if (_refs.TryGetValue(me, out var memberRef) && memberRef is ResolvedRef.EnumMember em)
                    {
                        EmitLoadConst(Value.MakeInt(em.Value));
                        break;
                    }
                    // 'ClassName.Member' (SPEC "Statische Mitglieder") - kein
                    // Objekt auf dem Stack nötig (anders als GetField), der
                    // Klassenname steht schon als Konstante im Bytecode (der
                    // Resolver hat ihn schon eindeutig aufgelöst, siehe
                    // ResolvedRef.StaticMember).
                    if (memberRef is ResolvedRef.StaticMember sm)
                    {
                        _chunk.EmitOp(OpCode.GetStaticField);
                        _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                        _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                        break;
                    }
                    CompileExpr(me.Target);
                    _chunk.EmitOp(OpCode.GetField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    break;

                case IndexExpr ix:
                    CompileExpr(ix.Target);
                    CompileExpr(ix.Index);
                    _chunk.EmitOp(OpCode.ArrayGet);
                    break;

                case ThrowExpr te:
                    CompileExpr(te.Value);
                    _chunk.EmitOp(OpCode.Throw);
                    break;

                case LambdaExpr lam:
                    CompileLambda(lam);
                    break;

                case SyncExpr syncExpr:
                {
                    CompileExpr(syncExpr.Target);
                    byte flags = 0;
                    if (syncExpr.IsTry) flags |= 1;
                    if (syncExpr.IsFlat) flags |= 2;
                    _chunk.EmitOp(OpCode.Sync);
                    _chunk.EmitByte(flags);
                    break;
                }

                case TryProcessExpr tryProcessExpr:
                    CompileExpr(tryProcessExpr.Target);
                    _chunk.EmitOp(OpCode.TryProcess);
                    break;

                case TryCallExpr tryCallExpr:
                {
                    // Der Callee selbst wird bewusst NICHT kompiliert (siehe
                    // Resolver.ResolveTryCallExpr - er hat keinen normalen
                    // ResolvedRef, nur der TryCallExpr-Knoten selbst hat
                    // ResolvedRef.TryableNative) - nur die Argumente.
                    var innerCall = (CallExpr)tryCallExpr.Call;
                    foreach (var arg in innerCall.Args) CompileExpr(arg);
                    if (_refs.TryGetValue(tryCallExpr, out var tcRef) && tcRef is ResolvedRef.TryableNative tn)
                    {
                        _chunk.EmitOp(OpCode.CallTryableNative);
                        _chunk.EmitU16(_natives.TryableIndexOf(tn.Name));
                        _chunk.EmitByte((byte)innerCall.Args.Count);
                    }
                    else
                    {
                        throw new NotSupportedException(
                            "TryCallExpr ohne aufgelöste TryableNative-Referenz - sollte der Resolver bereits abgefangen haben.");
                    }
                    break;
                }

                case CallExpr call:
                    CompileCall(call);
                    break;

                default:
                    throw new NotSupportedException($"Ausdruckstyp {expr.GetType().Name} wird nicht unterstützt.");
            }
        }

        /// <summary>Kompiliert den Lambda-Body EINMAL in einen eigenen Chunk
        /// (FunctionProto) - kein eingefangener umgebender Scope nötig, da
        /// Lambdas ohnehin nur ihren eigenen Scope + global sehen (SPEC 4.2),
        /// das hat der Resolver schon beim Auflösen berücksichtigt. Jede
        /// Auswertung DIESER LambdaExpr zur Laufzeit (MakeLambda) erzeugt einen
        /// neuen LambdaValue, der denselben Proto wiederverwendet - nur das
        /// 'on'-Target kann sich pro Auswertung unterscheiden.</summary>
        private void CompileLambda(LambdaExpr lambda)
        {
            var inner = new Compiler(_refs, _natives, _enclosingClass, _globalSlotCount, _knownClassNames);
            inner._chunk.OwnerClass = _enclosingClass;
            for (int i = 0; i < lambda.Params.Count; i++)
                inner._chunk.MarkLocalName(0, i, lambda.Params[i].Name);
            EmitLambdaParamChecks(inner, lambda.Params);
            foreach (var stmt in lambda.Body.Statements)
                inner.CompileStmt(stmt);
            // Implizites "return undefined", falls der Body ohne explizites
            // return durchläuft.
            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);

            var proto = new FunctionProto(inner._chunk, lambda.Params.Count, AccessModifier.Public, CompileParamDefaults(_enclosingClass, lambda.Params));
            int protoIdx = _chunk.AddFunctionProto(proto);

            bool hasOnTarget = lambda.OnTarget != null;
            if (hasOnTarget)
                CompileExpr(lambda.OnTarget!); // im UMSCHLIESSENDEN (aktuellen) Scope, nicht im Lambda-Scope

            _chunk.EmitOp(OpCode.MakeLambda);
            _chunk.EmitU16(protoIdx);
            _chunk.EmitByte(hasOnTarget ? (byte)1 : (byte)0);
        }

        /// <summary>Aufrufe registrierter nativer Funktionen (`print(...)` usw.)
        /// laufen über CALL_NATIVE, `obj.Method(...)` über virtuelle Auflösung
        /// (CallMethod), `base.Method(...)` über direkte Basis-Auflösung
        /// (CallBaseMethod), alles andere als allgemeiner Lambda-Aufruf (Callee
        /// muss zur Laufzeit zu einem Lambda-Wert auswerten).</summary>
        private void CompileCall(CallExpr call)
        {
            if (call.Callee is IdentifierExpr calleeId && _refs.TryGetValue(calleeId, out var resolved))
            {
                if (resolved is ResolvedRef.Native nativeRef)
                {
                    foreach (var arg in call.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.CallNative);
                    _chunk.EmitU16(_natives.IndexOf(nativeRef.Name));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (resolved is ResolvedRef.Extern ext)
                {
                    // Anders als bei einem unbekannten Bezeichner ist das hier
                    // KEIN Kompilierfehler - 'extern' deklariert nur die
                    // Signatur, die tatsächliche Implementierung verlinkt der
                    // Host erst zur Laufzeit (ExternRegistry, siehe
                    // VM.CallExtern) - ein Skript kann also kompilieren, auch
                    // wenn (noch) nichts verlinkt ist, und schlägt erst beim
                    // TATSÄCHLICHEN Aufruf fehl, falls dann immer noch nichts
                    // registriert ist.
                    foreach (var arg in call.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.CallExtern);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ext.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (resolved is ResolvedRef.StaticMember callSm)
                {
                    // SPEC "Statische Mitglieder" - bloßer Name statt
                    // 'ClassName.Method(...)'.
                    foreach (var arg in call.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.CallStaticMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(callSm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(calleeId.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (resolved is ResolvedRef.ImplicitThisMember)
                {
                    // SPEC "Implizite Mitglieder-Referenzen" - bloßer Name
                    // statt 'this.Method(...)'. CallMethod erwartet das
                    // Zielobjekt UNTERHALB der Argumente auf dem Stack (siehe
                    // VM.CallMethod: Args zuerst gepoppt, dann erst 'target')
                    // - 'this' also VOR den Argumenten pushen.
                    _chunk.EmitOp(OpCode.LoadThis);
                    foreach (var arg in call.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.CallMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(calleeId.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }
            }

            if (call.Callee is MemberExpr me)
            {
                if (me.Name == "resume")
                {
                    // 'resume' ist ein reservierter Methodenname (wie GetIndex/
                    // SetIndex/GetEnumerator) - kein echter Methodenaufruf,
                    // sondern springt über einen eigenen Opcode direkt zur
                    // eingefrorenen Wurfstelle zurück (siehe VM.ResumeException).
                    if (call.Args.Count > 1)
                        throw new NotSupportedException(
                            "'resume' erwartet höchstens ein Argument (den Fortsetzungswert).");

                    CompileExpr(me.Target);
                    if (call.Args.Count == 1)
                    {
                        CompileExpr(call.Args[0]);
                    }
                    else
                    {
                        // resume() ohne Argument == resume(undefined)
                        EmitLoadConst(Value.MakeUndefined());
                    }
                    _chunk.EmitOp(OpCode.ResumeException);
                    return;
                }

                // 'ClassName.Method(...)' (SPEC "Statische Mitglieder") - kein
                // Objekt auf dem Stack (anders als CallMethod), der
                // Klassenname steht schon als Konstante im Bytecode (siehe
                // ResolvedRef.StaticMember, vom Resolver aufgelöst).
                if (_refs.TryGetValue(me, out var calleeMemberRef) && calleeMemberRef is ResolvedRef.StaticMember sm)
                {
                    foreach (var arg in call.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.CallStaticMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (me.Target is BaseExpr)
                {
                    // 'base.Method(...)' - nicht-virtuell, this bleibt das aktuelle
                    // 'this'. Die Basisklasse wird statisch aus der GERADE
                    // KOMPILIERTEN Klasse (_enclosingClass) genommen, nicht aus der
                    // tatsächlichen Laufzeit-Klasse von 'this' - sonst wäre das bei
                    // mehrstufiger Vererbung falsch (B.base muss immer A sein, auch
                    // wenn 'this' zur Laufzeit eine Instanz von C : B ist).
                    if (_enclosingClass?.Base == null)
                        throw new NotSupportedException(
                            "'base.Method(...)' außerhalb einer Klasse mit Basisklasse - sollte der Resolver bereits abgefangen haben.");

                    foreach (var arg in call.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.CallBaseMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(_enclosingClass.Base.Name)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                CompileExpr(me.Target);
                foreach (var arg in call.Args) CompileExpr(arg);
                _chunk.EmitOp(OpCode.CallMethod);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                _chunk.EmitByte((byte)call.Args.Count);
                return;
            }

            CompileExpr(call.Callee);
            foreach (var arg in call.Args) CompileExpr(arg);
            _chunk.EmitOp(OpCode.Call);
            _chunk.EmitByte((byte)call.Args.Count);
        }

        /// <summary>Eigenständige (nicht in eine Anker-Entscheidung eingebettete)
        /// Coercion, z.B. als Initializer `var y = undefined:km`. Wie im
        /// Binär-Op-Fall: Typ VOR Einheit (Präzision bei int->float +
        /// Einheitenumrechnung), und ohne Geschwister-Anker fällt eine
        /// automatische ('!'/'::' ohne Argument) Einheiten-Anforderung auf
        /// unitless zurück; eine automatische Typ-Anforderung bleibt mangels
        /// Anker unverändert (keine explizite SPEC-Vorgabe für diesen Fall).</summary>
        private void CompileStandaloneCoercion(Expr expr)
        {
            var info = AnalyzeCoercion(expr);
            CompileExpr(info.Inner);

            if (info.ExplicitType != null)
                EmitCoerceTypeStatic(TokenTypeToTag(info.ExplicitType.Value));

            if (info.ExplicitUnit != null)
                EmitCoerceUnitStatic(fire.Values.Unit.Parse(info.ExplicitUnit));
            else if (info.UnitIsAuto)
                EmitCoerceUnitStatic(fire.Values.Unit.Unitless);
        }

        private void CompileIdentifierLoad(IdentifierExpr id)
        {
            switch (_refs[id])
            {
                case ResolvedRef.Local local:
                    _chunk.EmitOp(OpCode.LoadLocal);
                    _chunk.EmitU16(local.Depth);
                    _chunk.EmitU16(local.Slot);
                    break;
                case ResolvedRef.Global global:
                    _chunk.EmitOp(OpCode.LoadGlobal);
                    _chunk.EmitU16(global.Slot);
                    break;
                case ResolvedRef.Native native:
                    throw new NotSupportedException(
                        $"'{native.Name}' ist eine native Funktion und kann nur direkt aufgerufen werden " +
                        $"({native.Name}(...)), nicht als Wert verwendet werden.");
                case ResolvedRef.Extern ext:
                    throw new NotSupportedException(
                        $"'{ext.Name}' ist eine extern deklarierte Funktion und kann nur direkt aufgerufen werden, " +
                        $"nicht als Wert verwendet werden.");
                case ResolvedRef.StaticMember sm:
                    // SPEC "Statische Mitglieder" - bloßer Name statt
                    // 'ClassName.Name' (siehe Resolver.ResolveIdentifierRef).
                    _chunk.EmitOp(OpCode.GetStaticField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
                case ResolvedRef.ImplicitThisMember:
                    // SPEC "Implizite Mitglieder-Referenzen" - bloßer Name
                    // statt 'this.Name' (siehe Resolver.ResolveIdentifierRef).
                    _chunk.EmitOp(OpCode.LoadThis);
                    _chunk.EmitOp(OpCode.GetField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
            }
        }

        private void CompileAssign(AssignExpr a)
        {
            if (a.Target is MemberExpr me)
            {
                // 'ClassName.Member = ...' (SPEC "Statische Mitglieder") - kein
                // Zielobjekt auf dem Stack (anders als bei einer Instanz-
                // Feldzuweisung unten), der Klassenname steht schon als
                // Konstante im Bytecode (siehe ResolvedRef.StaticMember).
                // Bewusst OHNE die NewObjectOwned-Sonderbehandlung unten (SPEC
                // 2.1, kaskadierendes Löschen) - die setzt die Eigentümerschaft
                // eines frisch erzeugten Objekts auf die INSTANZ, die das Feld
                // hält; ein statisches Feld gehört aber keiner Instanz, dafür
                // gibt es hier kein sinnvolles Gegenstück.
                if (_refs.TryGetValue(me, out var staticTargetRef) && staticTargetRef is ResolvedRef.StaticMember sm)
                {
                    CompileExpr(a.Value);
                    _chunk.EmitOp(OpCode.SetStaticField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    return;
                }

                CompileExpr(me.Target);

                // Direkte Feldzuweisung eines frisch erzeugten Objekts: Owner wird
                // das Zielobjekt selbst, nicht der aktuelle Scope (SPEC 2.1). Dafür
                // muss das Zielobjekt beim NewObjectOwned-Aufruf schon auf dem Stack
                // liegen (unterhalb der Konstruktor-Argumente) - daher Dup, bevor die
                // Argumente gepusht werden, und SetField am Ende nutzt die zweite Kopie.
                if (a.Value is NewExpr ne)
                {
                    _chunk.EmitOp(OpCode.Dup);
                    foreach (var arg in ne.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.NewObjectOwned);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveTypeRef(ne.ClassRef))));
                    _chunk.EmitByte((byte)ne.Args.Count);
                }
                else
                {
                    CompileExpr(a.Value);
                }

                _chunk.EmitOp(OpCode.SetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                return;
            }

            if (a.Target is UnaryExpr { Op: UnaryOp.Dereference } deref)
            {
                CompileExpr(deref.Operand); // push Pointer
                CompileExpr(a.Value);       // push Wert
                _chunk.EmitOp(OpCode.PtrWrite);
                return;
            }

            if (a.Target is IndexExpr ix)
            {
                CompileExpr(ix.Target);
                CompileExpr(ix.Index);
                CompileExpr(a.Value);
                _chunk.EmitOp(OpCode.ArraySet);
                return;
            }

            if (a.Target is not IdentifierExpr id)
                throw new NotSupportedException(
                    "Ungültiges Zuweisungsziel für den Bytecode-Compiler.");

            CompileExpr(a.Value);

            switch (_refs[id])
            {
                case ResolvedRef.Local local:
                    // SPEC "Einheiten-Deklarationen": JEDE Zuweisung an einen
                    // Slot mit geforderter Einheit (nicht nur die erste, siehe
                    // VarDeclStmt-Kompilierung) - sonst könnte man die
                    // Anfangsprüfung einfach durch eine spätere, "falsche"
                    // Zuweisung umgehen.
                    EmitCheckUnitIfNeeded(this, local.RequiredUnit);
                    _chunk.EmitOp(OpCode.StoreLocal);
                    _chunk.EmitU16(local.Depth);
                    _chunk.EmitU16(local.Slot);
                    break;
                case ResolvedRef.Global global:
                    EmitCheckUnitIfNeeded(this, global.RequiredUnit);
                    _chunk.EmitOp(OpCode.StoreGlobal);
                    _chunk.EmitU16(global.Slot);
                    break;
                case ResolvedRef.Native native:
                    throw new NotSupportedException(
                        $"Zuweisung an '{native.Name}' ist nicht möglich - das ist eine native Funktion.");
                case ResolvedRef.Extern ext:
                    throw new NotSupportedException(
                        $"Zuweisung an '{ext.Name}' ist nicht möglich - das ist eine extern deklarierte Funktion.");
                case ResolvedRef.StaticMember sm:
                    // SPEC "Statische Mitglieder" - bloßer Name statt
                    // 'ClassName.Name = ...' (siehe Resolver.
                    // ResolveIdentifierRef/ResolveAssignTarget).
                    _chunk.EmitOp(OpCode.SetStaticField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
                case ResolvedRef.ImplicitThisMember:
                    // SPEC "Implizite Mitglieder-Referenzen" - bloßer Name
                    // statt 'this.Name = ...'. Der Wert liegt hier (anders
                    // als beim MemberExpr-Zweig oben) schon OBEN auf dem
                    // Stack (CompileExpr(a.Value) lief schon VOR diesem
                    // switch) - 'this' erst JETZT nachladen und die beiden
                    // vertauschen, damit SetField sein erwartetes [obj,
                    // value] bekommt.
                    _chunk.EmitOp(OpCode.LoadThis); // [value, obj]
                    _chunk.EmitOp(OpCode.Swap);     // [obj, value]
                    _chunk.EmitOp(OpCode.SetField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
            }
        }

        /// <summary>`++x`/`--x`/`x++`/`x--` (siehe Ast.IncDecExpr-Doku).
        /// Vier Zielarten, je eigene Strategie:
        /// - IdentifierExpr/Dereference/MemberExpr (EINE "Adresse" - Slot,
        ///   Pointer bzw. Objektinstanz): per Dup+Lesen+Rechnen+Schreiben
        ///   direkt im Bytecode, für Postfix zusätzlich RotateUnderTop, um
        ///   den alten Wert unter der Adresse aufzuheben, während sowohl
        ///   Adresse als auch neuer Wert für den Schreib-Opcode oben bleiben
        ///   (siehe OpCode.RotateUnderTop-Doku) - OHNE die Zieladresse ein
        ///   zweites Mal auszuwerten.
        /// - IndexExpr (ZWEI "Adress"-Teile - Array UND Index): ein eigener
        ///   Opcode (IncDecIndex) übernimmt Lesen+Rechnen+Schreiben ATOMAR
        ///   in der VM - mit reinem Stack-Umsortieren (nur RotateUnderTop,
        ///   das ja nur 3 Werte kennt) wäre das für VIER zu erhaltende Werte
        ///   (Array, Index, alter Wert, neuer Wert) nicht sauber lösbar
        ///   gewesen, ohne Array/Index ein zweites Mal auszuwerten.</summary>
        private void CompileIncDec(IncDecExpr e)
        {
            var addSubOp = e.IsIncrement ? OpCode.Add : OpCode.Sub;

            if (e.Target is IndexExpr ix)
            {
                CompileExpr(ix.Target);
                CompileExpr(ix.Index);
                _chunk.EmitOp(OpCode.IncDecIndex);
                _chunk.EmitByte(e.IsIncrement ? (byte)1 : (byte)0);
                _chunk.EmitByte(e.IsPrefix ? (byte)1 : (byte)0);
                return;
            }

            if (e.Target is MemberExpr me)
            {
                // 'ClassName.staticField++' (SPEC "Statische Mitglieder") -
                // kein Objekt auf dem Stack, GetStaticField/SetStaticField
                // statt GetField/SetField, sonst dieselbe Technik wie unten.
                if (_refs.TryGetValue(me, out var staticIncDecRef) && staticIncDecRef is ResolvedRef.StaticMember stm)
                {
                    int classNameConstIdx = _chunk.AddConstant(Value.MakeString(stm.ClassName));
                    int fieldNameConstIdx = _chunk.AddConstant(Value.MakeString(me.Name));
                    _chunk.EmitOp(OpCode.GetStaticField);
                    _chunk.EmitU16(classNameConstIdx);
                    _chunk.EmitU16(fieldNameConstIdx);
                    if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [oldVal, oldVal]
                    EmitLoadConst(Value.MakeInt(1));
                    _chunk.EmitOp(addSubOp); // Prefix: [newVal] / Postfix: [oldVal, newVal]
                    _chunk.EmitOp(OpCode.SetStaticField);
                    _chunk.EmitU16(classNameConstIdx);
                    _chunk.EmitU16(fieldNameConstIdx);
                    // SetStaticField poppt+pusht denselben Wert wieder (wie
                    // SetField) - Stackgröße bleibt dabei UNVERÄNDERT. Prefix:
                    // [newVal] ist also schon das gewünschte Ergebnis. Postfix:
                    // [oldVal, newVal] - die obere (neue) Kopie noch weg, damit
                    // oldVal als Ergebnis übrig bleibt.
                    if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop);
                    return;
                }

                CompileExpr(me.Target);           // [obj]
                _chunk.EmitOp(OpCode.Dup);         // [obj, obj]
                _chunk.EmitOp(OpCode.GetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name))); // [obj, oldVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [obj, oldVal, oldVal]
                EmitLoadConst(Value.MakeInt(1));
                _chunk.EmitOp(addSubOp);           // Prefix: [obj, newVal] / Postfix: [obj, oldVal, newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.RotateUnderTop); // [oldVal, obj, newVal]
                _chunk.EmitOp(OpCode.SetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name))); // [...,newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop); // [oldVal]
                return;
            }

            if (e.Target is UnaryExpr { Op: UnaryOp.Dereference } deref)
            {
                CompileExpr(deref.Operand);        // [ptr]
                _chunk.EmitOp(OpCode.Dup);          // [ptr, ptr]
                _chunk.EmitOp(OpCode.PtrRead);       // [ptr, oldVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [ptr, oldVal, oldVal]
                EmitLoadConst(Value.MakeInt(1));
                _chunk.EmitOp(addSubOp);
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.RotateUnderTop); // [oldVal, ptr, newVal]
                _chunk.EmitOp(OpCode.PtrWrite);       // [...,newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop); // [oldVal]
                return;
            }

            if (e.Target is not IdentifierExpr id)
                throw new NotSupportedException("Ungültiges Ziel für '++'/'--' im Bytecode-Compiler.");

            var refKind = _refs[id];

            // SPEC "Statische Mitglieder"/"Implizite Mitglieder-Referenzen":
            // ein bloßer Name, der auf ein statisches oder (implizit über
            // 'this') Instanzfeld verweist - eigene, in sich geschlossene
            // Bytecode-Sequenz statt der generischen EmitLoad/EmitStore
            // unten (die sind auf Local/Global zugeschnitten, brauchen kein
            // zusätzliches Objekt/Klassenname auf dem Stack).
            if (refKind is ResolvedRef.StaticMember sm)
            {
                int classNameConstIdx = _chunk.AddConstant(Value.MakeString(sm.ClassName));
                int fieldNameConstIdx = _chunk.AddConstant(Value.MakeString(id.Name));
                _chunk.EmitOp(OpCode.GetStaticField);
                _chunk.EmitU16(classNameConstIdx);
                _chunk.EmitU16(fieldNameConstIdx);
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [oldVal, oldVal]
                EmitLoadConst(Value.MakeInt(1));
                _chunk.EmitOp(addSubOp); // Prefix: [newVal] / Postfix: [oldVal, newVal]
                _chunk.EmitOp(OpCode.SetStaticField);
                _chunk.EmitU16(classNameConstIdx);
                _chunk.EmitU16(fieldNameConstIdx);
                // SetStaticField poppt+pusht denselben Wert wieder (wie
                // SetField) - Stackgröße bleibt UNVERÄNDERT (siehe dieselbe
                // Herleitung beim MemberExpr-Fall oben).
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop);
                return;
            }

            if (refKind is ResolvedRef.ImplicitThisMember)
            {
                // Wie 'this.feld++' oben (MemberExpr-Fall), nur dass 'this'
                // hier implizit ist statt ausgeschrieben.
                _chunk.EmitOp(OpCode.LoadThis);   // [obj]
                _chunk.EmitOp(OpCode.Dup);         // [obj, obj]
                _chunk.EmitOp(OpCode.GetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name))); // [obj, oldVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [obj, oldVal, oldVal]
                EmitLoadConst(Value.MakeInt(1));
                _chunk.EmitOp(addSubOp);           // Prefix: [obj, newVal] / Postfix: [obj, oldVal, newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.RotateUnderTop); // [oldVal, obj, newVal]
                _chunk.EmitOp(OpCode.SetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name))); // [...,newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop); // [oldVal]
                return;
            }

            void EmitLoad()
            {
                switch (refKind)
                {
                    case ResolvedRef.Local local:
                        _chunk.EmitOp(OpCode.LoadLocal);
                        _chunk.EmitU16(local.Depth);
                        _chunk.EmitU16(local.Slot);
                        break;
                    case ResolvedRef.Global global:
                        _chunk.EmitOp(OpCode.LoadGlobal);
                        _chunk.EmitU16(global.Slot);
                        break;
                    default:
                        throw new NotSupportedException($"'++'/'--' auf '{id.Name}' ist nicht möglich.");
                }
            }
            void EmitStore()
            {
                switch (refKind)
                {
                    case ResolvedRef.Local local:
                        // SPEC "Einheiten-Deklarationen" - dieselbe Prüfung wie
                        // bei jeder normalen Zuweisung (siehe CompileAssign) -
                        // `++`/`--` ist ja auch nur eine (kompakter geschriebene)
                        // Zuweisung.
                        EmitCheckUnitIfNeeded(this, local.RequiredUnit);
                        _chunk.EmitOp(OpCode.StoreLocal);
                        _chunk.EmitU16(local.Depth);
                        _chunk.EmitU16(local.Slot);
                        break;
                    case ResolvedRef.Global global:
                        EmitCheckUnitIfNeeded(this, global.RequiredUnit);
                        _chunk.EmitOp(OpCode.StoreGlobal);
                        _chunk.EmitU16(global.Slot);
                        break;
                }
            }

            EmitLoad();                              // [oldVal]
            if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [oldVal, oldVal]
            EmitLoadConst(Value.MakeInt(1));
            _chunk.EmitOp(addSubOp);                  // Prefix: [newVal] / Postfix: [oldVal, newVal]
            EmitStore();                              // Store* lässt den Wert (Peek statt Pop) auf dem Stack
            if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop); // [oldVal]
        }

        private void CompileUnary(UnaryExpr u)
        {
            if (u.Op == UnaryOp.AddressOf)
            {
                CompileAddressOf(u.Operand);
                return;
            }

            CompileExpr(u.Operand);
            _chunk.EmitOp(u.Op switch
            {
                UnaryOp.Negate => OpCode.Neg,
                UnaryOp.LogicalNot => OpCode.LogicalNot,
                UnaryOp.BitNot => OpCode.BitNot,
                UnaryOp.Dereference => OpCode.PtrRead,
                _ => throw new NotSupportedException($"UnaryOp {u.Op}"),
            });
        }

        /// <summary>`&amp;ausdruck` - nur auf Variablen (lokal/global) oder
        /// Objektfelder anwendbar (die einzigen "adressierbaren" Ausdrücke
        /// dieser Sprache, analog zu lvalues in C#). Der Resolver hat bereits
        /// geprüft, dass wir uns in einem 'unsafe'-Block befinden.</summary>
        private void CompileAddressOf(Expr operand)
        {
            switch (operand)
            {
                case IdentifierExpr id:
                    switch (_refs[id])
                    {
                        case ResolvedRef.Local local:
                            _chunk.EmitOp(OpCode.AddressOfLocal);
                            _chunk.EmitU16(local.Depth);
                            _chunk.EmitU16(local.Slot);
                            return;
                        case ResolvedRef.Global global:
                            _chunk.EmitOp(OpCode.AddressOfGlobal);
                            _chunk.EmitU16(global.Slot);
                            return;
                        default:
                            throw new NotSupportedException(
                                "'&' ist nur auf lokale/globale Variablen oder Objektfelder anwendbar.");
                    }

                case MemberExpr me:
                    CompileExpr(me.Target);
                    _chunk.EmitOp(OpCode.AddressOfField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    return;

                default:
                    throw new NotSupportedException(
                        "'&' ist nur auf Variablen oder Objektfelder anwendbar (kein gültiges Adressierungsziel).");
            }
        }

        private void CompileBinary(BinaryExpr b)
        {
            if (b.Op == BinaryOp.And) { CompileLogicalAnd(b); return; }
            if (b.Op == BinaryOp.Or) { CompileLogicalOr(b); return; }

            EmitCoercedOperands(b.Left, b.Right);

            _chunk.EmitOp(b.Op switch
            {
                BinaryOp.Add => OpCode.Add,
                BinaryOp.Sub => OpCode.Sub,
                BinaryOp.Mul => OpCode.Mul,
                BinaryOp.Div => OpCode.Div,
                BinaryOp.Mod => OpCode.Mod,
                BinaryOp.Power => OpCode.Power,
                BinaryOp.BitAnd => OpCode.BitAnd,
                BinaryOp.BitOr => OpCode.BitOr,
                BinaryOp.BitXor => OpCode.BitXor,
                BinaryOp.ShiftLeft => OpCode.ShiftLeft,
                BinaryOp.ShiftRight => OpCode.ShiftRight,
                BinaryOp.Eq => OpCode.Eq,
                BinaryOp.NotEq => OpCode.NotEq,
                BinaryOp.Lt => OpCode.Lt,
                BinaryOp.LtEq => OpCode.LtEq,
                BinaryOp.Gt => OpCode.Gt,
                BinaryOp.GtEq => OpCode.GtEq,
                _ => throw new NotSupportedException($"BinaryOp {b.Op}"),
            });
        }

        private void CompileLogicalAnd(BinaryExpr b)
        {
            CompileExpr(b.Left);
            _chunk.EmitOp(OpCode.JumpIfFalsePeek);
            int endJumpAt = _chunk.Here;
            _chunk.EmitU16(0);
            _chunk.EmitOp(OpCode.Pop);
            CompileExpr(b.Right);
            _chunk.PatchU16(endJumpAt, _chunk.Here);
        }

        private void CompileLogicalOr(BinaryExpr b)
        {
            CompileExpr(b.Left);
            _chunk.EmitOp(OpCode.JumpIfTruePeek);
            int endJumpAt = _chunk.Here;
            _chunk.EmitU16(0);
            _chunk.EmitOp(OpCode.Pop);
            CompileExpr(b.Right);
            _chunk.PatchU16(endJumpAt, _chunk.Here);
        }

        // -----------------------------------------------------------
        // Die Anker-Regel (SPEC 3.2) - Kern des Compilers.
        //
        // Für jeden Operanden wird per AnalyzeCoercion ermittelt, ob er ':' (Einheit)
        // und/oder '!' (Typ) anfordert, und ob jeweils explizit (fester Wert) oder
        // "automatisch" (kein Argument). Der Operand, der auf KEINER Achse
        // "automatisch" anfordert, ist der Anker; der andere wird dynamisch (zur
        // Laufzeit, da Variablen ihre Einheit/ihren Typ erst dann tragen) an ihn
        // angeglichen. Fordern BEIDE "automatisch" an, fällt die Einheit auf
        // unitless zurück (SPEC-Vorgabe); der Typ bleibt in diesem Fall unangetastet
        // (keine explizite SPEC-Vorgabe - Add/Sub/etc. werten int+float ohnehin
        // automatisch zu float auf, das deckt den praktischen Fall bereits ab).
        // -----------------------------------------------------------
        private void EmitCoercedOperands(Expr leftExpr, Expr rightExpr)
        {
            var left = AnalyzeCoercion(leftExpr);
            var right = AnalyzeCoercion(rightExpr);

            CompileExpr(left.Inner);
            if (left.ExplicitType != null) EmitCoerceTypeStatic(TokenTypeToTag(left.ExplicitType.Value));
            if (left.ExplicitUnit != null) EmitCoerceUnitStatic(fire.Values.Unit.Parse(left.ExplicitUnit));

            CompileExpr(right.Inner);
            if (right.ExplicitType != null) EmitCoerceTypeStatic(TokenTypeToTag(right.ExplicitType.Value));
            if (right.ExplicitUnit != null) EmitCoerceUnitStatic(fire.Values.Unit.Parse(right.ExplicitUnit));

            // Stack jetzt: [..., left', right']
            bool leftAuto = left.RequestsAnyAuto;
            bool rightAuto = right.RequestsAnyAuto;

            if (leftAuto && !rightAuto)
            {
                // rechts (TOS) ist Anker; links liegt darunter -> swap, angleichen, zurück-swap.
                // Typ VOR Einheit angleichen: sonst geht bei int->float-Konvertierung
                // in Kombination mit einer Einheitenumrechnung Präzision durch
                // vorzeitige Ganzzahlrundung verloren (z.B. 500m -> 1km statt 0.5km,
                // wenn erst auf km gerundet und danach erst zu float promoted würde).
                _chunk.EmitOp(OpCode.Swap);
                if (left.TypeIsAuto) _chunk.EmitOp(OpCode.CoerceTypeDynamic);
                if (left.UnitIsAuto) _chunk.EmitOp(OpCode.CoerceUnitDynamic);
                _chunk.EmitOp(OpCode.Swap);
            }
            else if (rightAuto && !leftAuto)
            {
                // links (TOS-1) ist Anker; rechts (TOS) direkt angleichen (Typ vor Einheit, s.o.).
                if (right.TypeIsAuto) _chunk.EmitOp(OpCode.CoerceTypeDynamic);
                if (right.UnitIsAuto) _chunk.EmitOp(OpCode.CoerceUnitDynamic);
            }
            else if (leftAuto && rightAuto)
            {
                // Kein Anker vorhanden -> Einheit fällt auf unitless zurück.
                if (left.UnitIsAuto)
                {
                    _chunk.EmitOp(OpCode.Swap);
                    EmitCoerceUnitStatic(fire.Values.Unit.Unitless);
                    _chunk.EmitOp(OpCode.Swap);
                }
                if (right.UnitIsAuto) EmitCoerceUnitStatic(fire.Values.Unit.Unitless);
            }
            // sonst: beide fix/explizit -> keine weitere Angleichung; die
            // Arithmetik-Operation selbst prüft Kompatibilität zur Laufzeit.
        }

        private readonly record struct CoercionInfo(
            Expr Inner, bool WantsUnit, string? ExplicitUnit, bool WantsType, TokenType? ExplicitType)
        {
            public bool UnitIsAuto => WantsUnit && ExplicitUnit == null;
            public bool TypeIsAuto => WantsType && ExplicitType == null;
            public bool RequestsAnyAuto => UnitIsAuto || TypeIsAuto;
        }

        /// <summary>Schält ':'/'!'-Postfix-Wrapper (in beliebiger Reihenfolge, auch
        /// beide) von einem Ausdruck ab und klassifiziert, was jeweils angefordert
        /// wurde.</summary>
        private static CoercionInfo AnalyzeCoercion(Expr expr)
        {
            bool wantsUnit = false; string? explicitUnit = null;
            bool wantsType = false; TokenType? explicitType = null;
            var current = expr;

            while (true)
            {
                if (current is UnitCoerceExpr uc)
                {
                    wantsUnit = true;
                    explicitUnit = uc.TargetUnitName;
                    current = uc.Operand;
                }
                else if (current is TypeCoerceExpr tc)
                {
                    wantsType = true;
                    explicitType = tc.TargetTypeKeyword;
                    current = tc.Operand;
                }
                else
                {
                    break;
                }
            }

            return new CoercionInfo(current, wantsUnit, explicitUnit, wantsType, explicitType);
        }

        private static TypeTag TokenTypeToTag(TokenType t) => t switch
        {
            TokenType.KwBool => TypeTag.Bool,
            TokenType.KwInt => TypeTag.Int,
            TokenType.KwFloat => TypeTag.Float,
            TokenType.KwChar => TypeTag.Char,
            TokenType.KwString => TypeTag.String,
            _ => throw new NotSupportedException($"Coercion-Ziel {t} wird nicht unterstützt."),
        };

        // -----------------------------------------------------------
        // Emit-Helfer
        // -----------------------------------------------------------
        private void EmitLoadConst(Value v)
        {
            _chunk.EmitOp(OpCode.LoadConst);
            _chunk.EmitU16(_chunk.AddConstant(v));
        }

        private void EmitCoerceUnitStatic(fire.Values.Unit unit)
        {
            _chunk.EmitOp(OpCode.CoerceUnit);
            _chunk.EmitU16(_chunk.AddUnit(unit));
        }

        private void EmitCoerceTypeStatic(TypeTag tag)
        {
            _chunk.EmitOp(OpCode.CoerceType);
            _chunk.EmitByte((byte)tag);
        }
    }
}
