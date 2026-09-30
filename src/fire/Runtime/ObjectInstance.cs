using System.Collections.Generic;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Laufzeit-Instanz einer Klasse. Trägt genau einen Owner (Scope oder eine
    /// andere ObjectInstance, SPEC 2) und ist selbst wieder ein IOwner (Felder
    /// können weitere Objektinstanzen besitzen).
    ///
    /// Felder liegen in einem Runtime.FieldStore (siehe dort) - für ZUR
    /// KOMPILIERZEIT bekannte, deklarierte Felder (der Normalfall für jeden
    /// vom Compiler erzeugten Feldzugriff) ein fester Array-Slot statt eines
    /// Dictionary-Lookups pro Zugriff (siehe RtClass/RuntimeClass.FieldIndex),
    /// mit Dictionary-Fallback für alles andere (z.B. Testcode ohne
    /// RuntimeClass). Member-Zugriff (`obj.feld`) bleibt zur Laufzeit
    /// namentlich (MemberExpr.Name bleibt ein String, siehe Resolver-
    /// Kommentar dazu - der statische Typ von `obj` ist nicht immer bekannt),
    /// die Namensauflösung selbst ist aber über RuntimeClass.FieldIndex
    /// gecacht statt bei jedem Zugriff neu berechnet zu werden.
    /// </summary>
    public sealed class ObjectInstance : IOwner
    {
        /// <summary>Name der Klasse (SPEC 2) - bewusst NUR der Name, nicht
        /// der volle AST (ClassDecl) wie früher: die VM liest zur Laufzeit
        /// ausschließlich diesen Namen (für RuntimeClass-Lookups, Fehler-
        /// meldungen), nie Felder/Methodenkörper direkt aus dem AST - die
        /// sind ja längst zu RuntimeClass.Fields/Methods/Chunks kompiliert.
        /// Wichtig für die geplante Programm-Serialisierung: damit hängt an
        /// jeder ObjectInstance (und damit potenziell an jedem gespeicherten
        /// Programmzustand) nicht die komplette Stmt/Expr-AST-Hierarchie.</summary>
        public string ClassName { get; }

        /// <summary>Das kompilierte Gegenstück zu ClassDef (siehe Bytecode.
        /// RuntimeClass) - Grundlage für den schnellen, Slot-indizierten
        /// Feldzugriff (siehe Fields/FieldStore-Doku). Null nur für
        /// ObjectInstances, die AUSSERHALB der normalen Compiler/VM-Pipeline
        /// direkt konstruiert werden (z.B. reine Ownership-Modell-Tests in
        /// Program.cs) - Fields fällt dann komplett auf den Dictionary-
        /// Fallback zurück, funktional unverändert, nur ohne den
        /// Geschwindigkeitsvorteil.</summary>
        public RuntimeClass? RtClass { get; }

        public IOwner Owner { get; private set; }
        public FieldStore Fields { get; }

        // Erst beim ersten besessenen Kind angelegt - die meisten Objekte besitzen keins.
        private List<ObjectInstance>? _owned;
        private bool _destroyed;

        public bool IsDestroyed => _destroyed;

        /// <summary>Eindeutige, monoton steigende Erzeugungs-ID - siehe
        /// ThreadShareLock.Order-Doku (Grundlage einer künftigen globalen
        /// Lock-Reihenfolge über mehrere Bäume hinweg).</summary>
        public long Id { get; } = System.Threading.Interlocked.Increment(ref _nextId);
        private static long _nextId;

        /// <summary>Null, solange dieses Objekt nie (direkt oder als
        /// Nachfahre eines ausgecheckten Vorfahren) Ziel von `taking` war -
        /// dann läuft jeder Feldzugriff ganz ohne Locking-Overhead (siehe
        /// TryGetFieldLocked/SetFieldLocked). Gesetzt über
        /// ActivateThreadSharing, siehe dort für die genaue Semantik.</summary>
        public ThreadShareLock? ThreadLock { get; private set; }

        /// <summary>Gehört dieses Objekt zum geteilten Bereich der GLOBALEN Variablen (siehe GlobalsBroker/docs/THREADING_DESIGN.md
        /// Abschnitt 7)? Das sind alle Objekte, die dem globalen Scope des Hauptprogramms (direkt oder über andere Objekte) gehören,
        /// sobald ein `fire`-Thread läuft. Fire-Threads lesen sie direkt (unter dem Baum-Lock), ändern sie aber nur innerhalb einer
        /// Sektion, die das Hauptprogramm bei `sync globals` erteilt.</summary>
        public bool InGlobalsDomain { get; private set; }

        /// <summary>Nimmt diesen Baum (sich und alle besessenen Objekte) in den geteilten Bereich der Globals auf und aktiviert dafür
        /// das Locking (ein schon vorhandener Baum-Lock, z.B. von `taking`, bleibt bestehen). Idempotent.</summary>
        public void MarkGlobalsDomain(ThreadShareLock treeLock)
        {
            if (InGlobalsDomain) return;
            InGlobalsDomain = true;
            ThreadLock ??= treeLock;
            if (_owned != null)
                foreach (var child in _owned)
                    child.MarkGlobalsDomain(ThreadLock);
        }

        /// <summary>Versteckte Rückverknüpfung zum Original, falls DIESES
        /// Objekt selbst eine `taking`-Kopie ist (siehe
        /// docs/THREADING_DESIGN.md Abschnitt 3) - Grundlage für `sync`/
        /// `sync flat`, die ja wissen müssen, wohin zurückgeschrieben wird.
        /// Null für ein Objekt, das keine Kopie ist (der Normalfall).</summary>
        public ObjectInstance? SyncOrigin { get; set; }

        /// <summary>Gesetzt (bei `new`, siehe VM.NewObject), wenn diese
        /// Instanz von einer `actor`-Deklaration stammt (docs/
        /// THREADING_DESIGN.md Abschnitt 2) - null für ganz normale Objekte.
        /// Diese Instanz selbst ist der eigentliche "ist es ein Actor?"-
        /// Marker im ganzen Rest der VM (statt eines eigenen bool-Felds):
        /// jeder Methodenaufruf auf einem Objekt mit gesetzter Mailbox wird
        /// zu einer Nachricht (VM.CallMethod), statt direkt auszuführen -
        /// unabhängig davon, von welchem Thread aus der Aufruf kommt (auch
        /// vom "Heimat"-Thread des Actors selbst, siehe THREADING_DESIGN.md
        /// für die bewusste Vereinfachung dieser Ausbaustufe).</summary>
        public ActorMailbox? Mailbox { get; set; }

        public ObjectInstance(string className, IOwner initialOwner, RuntimeClass? rtClass = null)
        {
            ClassName = className;
            RtClass = rtClass;
            Fields = new FieldStore(rtClass);
            Owner = initialOwner;
            Owner.AddOwned(this);
        }

        // -----------------------------------------------------------
        // Multithreading: Baum-weites Locking (docs/THREADING_DESIGN.md 4.5)
        // -----------------------------------------------------------

        /// <summary>Aktiviert Thread-Sharing für DIESEN gesamten Ownership-
        /// Baum (sich selbst und rekursiv alle besessenen Objekte) mit dem
        /// gegebenen, gemeinsamen Baum-Lock - idempotent: bricht die
        /// Rekursion ab, sobald ein Knoten schon denselben oder einen
        /// (theoretisch) anderen Lock trägt, läuft also bei wiederholtem
        /// `taking` desselben (Teil-)Baums nicht erneut komplett durch.
        /// Zyklen sind hier unproblematisch, da der Ownership-Graph per
        /// Konstruktion zyklenfrei ist (siehe TakeTo/IsAncestorOf).</summary>
        public void ActivateThreadSharing(ThreadShareLock treeLock)
        {
            if (ThreadLock != null) return;
            ThreadLock = treeLock;
            if (_owned != null)
                foreach (var child in _owned)
                    child.ActivateThreadSharing(treeLock);
        }

        /// <summary>Liest ein Feld unter dem Baum-Lock, falls dieses Objekt
        /// jemals Ziel von `taking` war (sonst ungesichert, siehe
        /// ThreadLock-Doku - der Normalfall, kein Overhead).</summary>
        public bool TryGetFieldLocked(string name, out Value value)
        {
            if (ThreadLock == null) return Fields.TryGetValue(name, out value);
            ThreadLock.Enter();
            try { return Fields.TryGetValue(name, out value); }
            finally { ThreadLock.Exit(); }
        }

        public bool HasFieldLocked(string name)
        {
            if (ThreadLock == null) return Fields.ContainsKey(name);
            ThreadLock.Enter();
            try { return Fields.ContainsKey(name); }
            finally { ThreadLock.Exit(); }
        }

        /// <summary>Schreibt ein Feld unter dem Baum-Lock. Bewusst als KURZE,
        /// auf genau diese eine Dictionary-Operation beschränkte Sperre
        /// gehalten (nicht über eine ganze Property-Fallback-Kette hinweg,
        /// siehe VM.SetField) - eine minimale Racemöglichkeit zwischen einem
        /// vorherigen HasFieldLocked-Check und diesem Set ist hier bewusst in
        /// Kauf genommen (Last-Writer-Wins ist ohnehin die Grundphilosophie
        /// des gesamten Sync-Modells), eine über mehrere Schritte gehaltene
        /// Sperre würde dagegen beliebigen, potenziell langsamen Nutzer-Code
        /// (Property-Setter-Aufrufe) mit gehaltenem Lock ausführen - das
        /// würde andere Threads unnötig lange blockieren.</summary>
        public void SetFieldLocked(string name, Value value)
        {
            if (ThreadLock == null) { Fields[name] = value; return; }
            ThreadLock.Enter();
            try { Fields[name] = value; }
            finally { ThreadLock.Exit(); }
        }

        // -----------------------------------------------------------
        // IOwner (Felder dieser Instanz können selbst wieder Objekte besitzen)
        // -----------------------------------------------------------
        public IReadOnlyList<ObjectInstance> OwnedObjects => (IReadOnlyList<ObjectInstance>?)_owned ?? System.Array.Empty<ObjectInstance>();
        public void AddOwned(ObjectInstance obj)
        {
            (_owned ??= new List<ObjectInstance>()).Add(obj);
            // Ein neuer Besitz in einem geteilten Baum gehört sofort dazu (sonst wäre er ohne Sperre lesbar).
            if (InGlobalsDomain) obj.MarkGlobalsDomain(ThreadLock!);
            else if (ThreadLock != null) obj.ActivateThreadSharing(ThreadLock);
        }
        public void RemoveOwned(ObjectInstance obj) => _owned?.Remove(obj);

        // -----------------------------------------------------------
        // Ownership-Transfer: TakeUpwards / TakeGlobal / TakeTo (SPEC 2.2)
        // -----------------------------------------------------------

        /// <summary>Owner wird der Parent-Scope des aktuellen Owner-Scopes. Nur
        /// gültig, wenn der aktuelle Owner ein Scope ist (nicht ein Objekt) und
        /// dieser Scope einen Parent hat (der globale Scope hat keinen).</summary>
        public void TakeUpwards()
        {
            if (Owner is not Scope currentScope)
                throw new OwnershipException(
                    "TakeUpwards ist nur gültig, wenn der aktuelle Owner ein Scope ist.");
            if (currentScope.Parent == null)
                throw new OwnershipException(
                    "TakeUpwards: der aktuelle Scope hat keinen Parent-Scope (bereits global).");
            Reparent(currentScope.Parent);
        }

        /// <summary>Owner wird der globale Scope.</summary>
        public void TakeGlobal(Scope globalScope) => Reparent(globalScope);

        /// <summary>Owner wird <paramref name="target"/>. Prüft auf Zyklen
        /// (Zielobjekt darf nicht bereits transitiv im Besitz dieses Objekts sein)
        /// und auf eine laufende Kaskadenlöschung des Ziels: befindet sich das Ziel
        /// bereits in Zerstörung, wird dieses Objekt so behandelt, als wäre die
        /// Übergabe eine Sekunde VOR Beginn der Löschung erfolgt - es wird sofort
        /// mit zerstört (SPEC 2.2, "Race mit laufender Löschung").</summary>
        public void TakeTo(ObjectInstance target, IDestructRunner runner)
        {
            if (target._destroyed)
            {
                Owner.RemoveOwned(this);
                Destroy(runner);
                return;
            }

            if (IsAncestorOf(target))
                throw new OwnershipException(
                    "TakeTo: Zyklus erkannt - das Zielobjekt ist bereits (direkt oder transitiv) im Besitz dieses Objekts.");

            Reparent(target);
        }

        private void Reparent(IOwner newOwner)
        {
            Owner.RemoveOwned(this);
            Owner = newOwner;
            newOwner.AddOwned(this);
        }

        /// <summary>Verallgemeinerte Variante von TakeUpwards/TakeGlobal für
        /// einen beliebigen Ziel-Scope - intern für die VM gedacht (SPEC 2.3:
        /// "return übergibt Ownership an den aufrufenden Scope"), nicht Teil der
        /// öffentlichen Skript-API (dafür bleiben TakeUpwards/TakeGlobal/TakeTo).</summary>
        public void ReparentTo(Scope newOwner) => Reparent(newOwner);

        /// <summary>true, wenn <paramref name="candidate"/> irgendwo unterhalb von
        /// diesem Objekt im Ownership-Baum hängt (direkt oder transitiv) -
        /// Grundlage des Zyklenschutzes bei TakeTo.</summary>
        private bool IsAncestorOf(ObjectInstance candidate)
        {
            if (_owned == null) return false;
            foreach (var child in _owned)
            {
                if (ReferenceEquals(child, candidate)) return true;
                if (child.IsAncestorOf(candidate)) return true;
            }
            return false;
        }

        // -----------------------------------------------------------
        // is from / is under (SPEC 6)
        // -----------------------------------------------------------

        /// <summary>`objekt is from ownerKandidat` - direkter Owner-Vergleich.</summary>
        public bool IsOwnedBy(object ownerCandidate) => ReferenceEquals(Owner, ownerCandidate);

        /// <summary>`objekt is under ownerKandidat` - transitiv über die
        /// Ownership-Kette (nicht die lexikalische Scope-Elternkette!): der
        /// unmittelbare Owner, dessen Owner (falls wieder ein Objekt), usw. Die
        /// Kette endet, sobald ein Scope erreicht wird (Scopes haben keinen
        /// "Owner", nur einen lexikalischen Parent - das ist bewusst eine andere
        /// Relation und wird hier nicht mit einbezogen).</summary>
        public bool IsTransitivelyOwnedBy(object ownerCandidate)
        {
            IOwner current = Owner;
            while (true)
            {
                if (ReferenceEquals(current, ownerCandidate)) return true;
                if (current is ObjectInstance oi) current = oi.Owner;
                else return false;
            }
        }

        // -----------------------------------------------------------
        // Kaskadenlöschung (SPEC 2.3)
        // -----------------------------------------------------------

        /// <summary>Zerstört dieses Objekt: ruft zuerst destruct() auf (über den
        /// vom Evaluator bereitgestellten Runner), dann kaskadierend alle noch von
        /// diesem Objekt besessenen Objekte. Idempotent (mehrfacher Aufruf ist
        /// ungefährlich, z.B. wenn ein Objekt sowohl regulär als auch über die
        /// TakeTo-Race-Behandlung in dieselbe Kaskade gerät).</summary>
        public void Destroy(IDestructRunner runner)
        {
            if (_destroyed) return;
            _destroyed = true;

            runner.RunDestructor(this);

            if (_owned == null) return;
            foreach (var child in _owned.ToArray())
                child.Destroy(runner);
            _owned.Clear();
        }
    }
}
