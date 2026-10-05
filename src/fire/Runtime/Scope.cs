using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Laufzeit-Gegenstück zu einem vom Resolver erkannten Scope-Knoten (Block,
    /// Funktion/Methode/Lambda-Body, globaler Scope). Variablen liegen slot-indiziert
    /// vor (Index entspricht exakt dem, was der Resolver für die jeweilige
    /// IdentifierExpr ermittelt hat - kein Namens-Lookup zur Laufzeit nötig).
    ///
    /// Scope ist gleichzeitig ein IOwner: Objektinstanzen, deren Owner dieser Scope
    /// ist, werden in <see cref="Release"/> kaskadierend zerstört, wenn der Scope
    /// verlassen wird (Block-/Funktionsende) - es sei denn, sie wurden vorher per
    /// TakeUpwards/TakeGlobal/TakeTo transferiert oder per return an den Parent-
    /// Scope weitergereicht (SPEC 2.3; das "return übergibt Ownership"-Verhalten
    /// wird vom Evaluator umgesetzt, indem er vor dem Release des Funktions-Scopes
    /// den Rückgabewert - falls es eine von diesem Scope besessene Objektinstanz
    /// ist - per TakeUpwards an den aufrufenden Scope überträgt).
    /// </summary>
    public sealed class Scope : IOwner
    {
        // Veränderlich nur wegen der Wiederverwendung (siehe Reinit/Recycle): eine Scope wird nach dem Verlassen vom Pool der VM
        // erneut ausgegeben und bekommt dann einen neuen Parent.
        private Scope? _parent;
        public Scope? Parent => _parent;
        public bool IsGlobal { get; }

        // Bewusst NULL statt vorab angelegter leerer Arrays (siehe DefineSlot) - JEDE Blockausführung (z.B. jeder einzelne
        // Schleifendurchlauf, siehe Compiler.CompileScopedBody: ein EnterScope/ExitScope-Paar PRO Iteration) bräuchte sonst
        // ein Slot-Array, selbst wenn der Block gar keine lokale Variable deklariert (der häufigste Fall bei einfachen
        // Schleifenkörpern). Besessene Objekte: siehe OwnedSet (das erste ohne Listenobjekt).
        // Slots als Array mit Zähler statt List<Value>: eine Scope entsteht bei JEDEM Aufruf und jedem
        // Schleifendurchlauf, und List<T> bringt pro Instanz ein Extra-Objekt sowie Versionszähler mit.
        private Value[]? _slots;
        private int _slotCount;
        private OwnedSet _owned;
        private List<IOwnedLeaf>? _leaves; // besessene Arrays und Puffer (selten: meist null)

        public Scope(Scope? parent, bool isGlobal = false)
        {
            _parent = parent;
            IsGlobal = isGlobal;
        }

        /// <summary>Scope mit bereits belegten Slots: `slots` gehört ab jetzt dieser Scope, die ersten `count`
        /// Einträge sind die Parameter (der Aufrufer hat sie direkt vom Stack hineinkopiert, statt sie über ein
        /// Zwischenarray und einzelne DefineSlot-Aufrufe zu verteilen); der Rest ist Platz für lokale Variablen.</summary>
        public Scope(Scope? parent, Value[] slots, int count)
        {
            _parent = parent;
            _slots = slots;
            _slotCount = count;
        }

        // -----------------------------------------------------------
        // Wiederverwendung (Pool der VM)
        //
        // Jeder Block, jede Schleifeniteration und jeder Aufruf legt eine Scope an - und die allermeisten besitzen weder Objekte noch
        // werden sie jemals von außen referenziert. Die VM gibt solche Scopes beim Verlassen in einen Pool zurück und reicht sie beim
        // nächsten Betreten wieder aus (samt ihrem Slot-Array), statt jedes Mal zwei Objekte neu anzulegen.
        //
        // Wiederverwendbar ist eine Scope nur, solange NICHTS sonst auf sie zeigen kann:
        //  - sie stammt aus dem Pool (`IsPooled`: nur diese Scopes werden zurückgegeben, nie die globale oder von anderem Code angelegte),
        //  - sie besitzt kein Objekt mehr (ein zerstörtes Objekt vergisst seinen Owner, siehe ObjectInstance.Destroy; ein weitergegebenes
        //    hat längst einen neuen),
        //  - kein Pointer zeigt auf einen ihrer Slots (`MarkEscaped`, gesetzt von ScopeSlotPointerTarget).
        // -----------------------------------------------------------
        private bool _pooled;
        private bool _escaped;

        /// <summary>Eine neue Scope für den Pool der VM: wird beim Verlassen (ExitScope/return) zurückgegeben, falls sie dann noch wiederverwendbar ist.</summary>
        public static Scope CreatePooled(Scope? parent) => new Scope(parent) { _pooled = true };

        /// <summary>Kann diese Scope jetzt in den Pool zurück (siehe oben)?</summary>
        public bool CanRecycle => _pooled && !_escaped && _owned.IsEmpty && (_leaves == null || _leaves.Count == 0);

        /// <summary>Ein Pointer auf einen Slot dieser Scope existiert (ScopeSlotPointerTarget): die Scope darf nie wiederverwendet werden,
        /// der Pointer bliebe sonst auf die Variablen eines ganz anderen Blocks gerichtet.</summary>
        public void MarkEscaped() => _escaped = true;

        /// <summary>Gibt die Scope wieder aus (vom Pool genommen): neuer Parent, leer.</summary>
        public void Reinit(Scope? parent)
        {
            _parent = parent;
            _pooled = true;
        }

        /// <summary>Wie <see cref="Reinit"/> für einen Aufruf: sorgt für ein Slot-Array mit mindestens `capacity` Plätzen (das vorhandene wird
        /// weiterverwendet, wenn es reicht) und belegt die ersten `paramCount` Slots - der Aufrufer kopiert die Parameter direkt hinein
        /// (siehe <see cref="SlotArray"/>).</summary>
        public void ReinitForCall(Scope? parent, int paramCount, int capacity)
        {
            _parent = parent;
            _pooled = true;
            if (_slots == null || _slots.Length < capacity) _slots = new Value[capacity];
            _slotCount = paramCount;
        }

        /// <summary>Das rohe Slot-Array (nur für die VM direkt nach <see cref="ReinitForCall"/>: Parameter hineinkopieren).</summary>
        public Value[] SlotArray => _slots!;

        /// <summary>Räumt eine verlassene Scope für die Wiederverwendung auf: die Werte werden vergessen (sonst hielte der Pool Objekte am
        /// Leben), Parent und Pool-Kennzeichen zurückgesetzt. Nur aufrufen, wenn <see cref="CanRecycle"/> gilt.</summary>
        public void Recycle()
        {
            if (_slotCount > 0)
            {
                Array.Clear(_slots!, 0, _slotCount);
                _slotCount = 0;
            }
            _parent = null;
            _pooled = false;
        }

        // -----------------------------------------------------------
        // Slot-Zugriff
        // -----------------------------------------------------------

        /// <summary>Legt einen neuen Slot an (Reihenfolge muss exakt der
        /// Deklarationsreihenfolge entsprechen, die der Resolver zugrunde gelegt
        /// hat) und gibt seinen Index zurück.</summary>
        public int DefineSlot(Value initialValue)
        {
            var slots = _slots;
            if (slots == null)
                _slots = slots = new Value[4];
            else if (_slotCount == slots.Length)
            {
                Array.Resize(ref _slots, slots.Length * 2);
                slots = _slots;
            }
            slots[_slotCount] = initialValue;
            return _slotCount++;
        }

        /// <summary>GetSlot/SetSlot werden nur mit einem Index aufgerufen, der
        /// aus einer vorherigen DefineSlot-Reihenfolge stammt (siehe
        /// Resolver/Compiler - der Index ist zur Kompilierzeit fest bekannt) -
        /// `_slots` ist an dieser Stelle deshalb garantiert bereits belegt.
        /// Ein Index jenseits der definierten Slots ist ein VM-/Compiler-Bug
        /// (siehe Values.VmInvariantViolationException-Doku) und wirft wie
        /// bisher.</summary>
        public Value GetSlot(int index)
        {
            if ((uint)index >= (uint)_slotCount) ThrowBadSlot(index);
            return _slots![index];
        }

        /// <summary>Referenz auf einen Slot (für die VM: ein Slot wird direkt auf den Stack kopiert bzw. vom Stack
        /// überschrieben, ohne Zwischenkopien). Gleiche Bereichsprüfung wie GetSlot.</summary>
        public ref Value SlotRef(int index)
        {
            if ((uint)index >= (uint)_slotCount) ThrowBadSlot(index);
            return ref _slots![index];
        }

        public void SetSlot(int index, Value value)
        {
            if ((uint)index >= (uint)_slotCount) ThrowBadSlot(index);
            _slots![index] = value;
        }

        private void ThrowBadSlot(int index) =>
            throw new System.ArgumentOutOfRangeException(nameof(index), $"Slot {index} is not defined (slots: {_slotCount}).");

        /// <summary>Anzahl belegter Slots - für Debug-/Inspektionszwecke (siehe
        /// VM.DebugLocals), von der normalen Ausführung selbst nicht gebraucht.</summary>
        public int SlotCount => _slotCount;

        /// <summary>Läuft `depth` Elternschritte nach oben - depth entspricht exakt
        /// dem, was der Resolver in ResolvedRef.Local(depth, slot) ermittelt hat.</summary>
        public Scope GetAncestor(int depth)
        {
            var scope = this;
            for (int i = 0; i < depth; i++)
                scope = scope.Parent!;
            return scope;
        }

        // -----------------------------------------------------------
        // IOwner
        // -----------------------------------------------------------
        public IReadOnlyList<ObjectInstance> OwnedObjects => _owned.AsList();
        /// <summary>Besitzt diese Scope gerade Objekte? Verlassen ist sonst ein reines Umhängen des Parent-Zeigers
        /// (siehe VM.Step, ExitScope).</summary>
        public bool HasOwned => !_owned.IsEmpty || _leaves is { Count: > 0 };

        /// <summary>Gesetzt für den globalen Scope des Hauptprogramms, sobald ein `fire`-Thread läuft (siehe GlobalsBroker): jedes Objekt,
        /// das ihm gehört - auch eines, das erst später entsteht - gehört dann zum geteilten Bereich (siehe
        /// ObjectInstance.MarkGlobalsDomain).</summary>
        public ThreadShareLock? SharingLock { get; set; }

        public void AddOwned(ObjectInstance obj)
        {
            _owned.Add(obj);
            if (SharingLock != null) obj.MarkGlobalsDomain(SharingLock);
        }
        public void RemoveOwned(ObjectInstance obj) => _owned.Remove(obj);
        public void AddLeaf(IOwnedLeaf leaf) => (_leaves ??= new List<IOwnedLeaf>()).Add(leaf);
        public void RemoveLeaf(IOwnedLeaf leaf) => _leaves?.Remove(leaf);

        /// <summary>Wie <see cref="Release"/>, aber nur für Objekte, die `filter` bejaht - die übrigen bleiben im Besitz dieser Scope
        /// (für das Ende eines Fire-Threads: seine Globals-Schnappschüsse und `taking`-Kopien sind Kopien von Objekten des
        /// Hauptprogramms und dürfen dort keine Destruktoren auslösen, z.B. ein geteiltes Handle schließen).</summary>
        public void ReleaseWhere(IDestructRunner runner, Func<ObjectInstance, bool> filter)
        {
            if (_owned.IsEmpty) return;   // (Arrays und Puffer bleiben: Fire-Thread-Schnappschuesse sind Kopien des Hauptprogramms)
            var all = _owned.ToArray();
            foreach (var obj in all)
            {
                if (filter(obj)) obj.Destroy(runner);
                else obj.DestroyOwnedNonCopies(runner);   // (a copy stays, what the thread made inside of it does not)
            }
            _owned.RemoveDestroyed();
        }

        /// <summary>Wird beim Verlassen des Scopes aufgerufen: zerstört kaskadierend alle noch von diesem Scope besessenen Objekte.</summary>
        public void Release(IDestructRunner runner)
        {
            if (!_owned.IsEmpty) _owned.DestroyAll(runner);
            if (_leaves != null) LeafOwnership.DestroyAll(_leaves); // (sonst: nichts zu tun - der häufigste Fall bei einfachen Blöcken/Schleifenkörpern)
        }
    }
}
