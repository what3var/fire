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
        public Scope? Parent { get; }
        public bool IsGlobal { get; }

        // Bewusst NULL statt vorab angelegter leerer Listen (siehe
        // DefineSlot/AddOwned) - JEDE Blockausführung (z.B. jeder einzelne
        // Schleifendurchlauf, siehe Compiler.CompileScopedBody: ein
        // EnterScope/ExitScope-Paar PRO Iteration) legt sonst zwei leere
        // List<T>-Instanzen an, selbst wenn der Block gar keine lokale
        // Variable deklariert und kein Objekt besitzt (der häufigste Fall
        // bei einfachen Schleifenkörpern) - spart zwei von drei Allokationen
        // pro Scope in genau diesem, sehr heißen Pfad.
        // Slots als Array mit Zähler statt List<Value>: eine Scope entsteht bei JEDEM Aufruf und jedem
        // Schleifendurchlauf, und List<T> bringt pro Instanz ein Extra-Objekt sowie Versionszähler mit.
        private Value[]? _slots;
        private int _slotCount;
        private List<ObjectInstance>? _owned;

        public Scope(Scope? parent, bool isGlobal = false)
        {
            Parent = parent;
            IsGlobal = isGlobal;
        }

        /// <summary>Scope mit bereits belegten Slots: `slots` gehört ab jetzt dieser Scope, die ersten `count`
        /// Einträge sind die Parameter (der Aufrufer hat sie direkt vom Stack hineinkopiert, statt sie über ein
        /// Zwischenarray und einzelne DefineSlot-Aufrufe zu verteilen); der Rest ist Platz für lokale Variablen.</summary>
        public Scope(Scope? parent, Value[] slots, int count)
        {
            Parent = parent;
            _slots = slots;
            _slotCount = count;
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
            throw new System.ArgumentOutOfRangeException(nameof(index), $"Slot {index} ist nicht definiert (Slots: {_slotCount}).");

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
        public IReadOnlyList<ObjectInstance> OwnedObjects => (IReadOnlyList<ObjectInstance>?)_owned ?? System.Array.Empty<ObjectInstance>();
        /// <summary>Besitzt diese Scope Objekte? Verlassen ist sonst ein reines Umhängen des Parent-Zeigers
        /// (siehe VM.Step, ExitScope).</summary>
        public bool HasOwned => _owned != null;

        /// <summary>Gesetzt für den globalen Scope des Hauptprogramms, sobald ein `fire`-Thread läuft (siehe GlobalsBroker): jedes Objekt,
        /// das ihm gehört - auch eines, das erst später entsteht - gehört dann zum geteilten Bereich (siehe
        /// ObjectInstance.MarkGlobalsDomain).</summary>
        public ThreadShareLock? SharingLock { get; set; }

        public void AddOwned(ObjectInstance obj)
        {
            (_owned ??= new List<ObjectInstance>()).Add(obj);
            if (SharingLock != null) obj.MarkGlobalsDomain(SharingLock);
        }
        public void RemoveOwned(ObjectInstance obj) => _owned?.Remove(obj);

        /// <summary>Wird beim Verlassen des Scopes aufgerufen: zerstört
        /// kaskadierend alle noch von diesem Scope besessenen Objekte.</summary>
        /// <summary>Wie <see cref="Release"/>, aber nur für Objekte, die `filter` bejaht - die übrigen bleiben im Besitz dieser Scope
        /// (für das Ende eines Fire-Threads: seine Globals-Schnappschüsse und `taking`-Kopien sind Kopien von Objekten des
        /// Hauptprogramms und dürfen dort keine Destruktoren auslösen, z.B. ein geteiltes Handle schließen).</summary>
        public void ReleaseWhere(IDestructRunner runner, Func<ObjectInstance, bool> filter)
        {
            if (_owned == null) return;
            foreach (var obj in _owned.ToArray())
                if (filter(obj)) obj.Destroy(runner);
            _owned.RemoveAll(o => o.IsDestroyed);
        }

        public void Release(IDestructRunner runner)
        {
            if (_owned == null) return; // nichts zu tun - der häufigste Fall bei einfachen Blöcken/Schleifenkörpern
            // Kopie, da Destroy() während der Iteration _owned weiterer Objekte
            // verändern kann (verschachtelte Kaskaden).
            foreach (var obj in _owned.ToArray())
                obj.Destroy(runner);
            _owned.Clear();
        }
    }
}
