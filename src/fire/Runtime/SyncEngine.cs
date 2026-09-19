using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Ergebnis von sync/try sync/sync flat/try sync flat (siehe
    /// docs/THREADING_DESIGN.md Abschnitt 4.1) - `LockBusy` kann nur bei den
    /// `try`-Varianten auftreten (die blockierenden Varianten warten
    /// stattdessen, bis der Lock frei ist). Wird bei der späteren Anbindung
    /// an die Sprache 1:1 zu true/false/undefined.</summary>
    public enum SyncResult
    {
        Success,
        LockBusy,
        TargetGone,
    }

    /// <summary>
    /// Implementiert `sync`/`try sync`/`sync flat`/`try sync flat` (siehe
    /// docs/THREADING_DESIGN.md Abschnitt 4). `copy` muss eine über
    /// ObjectCopier.Take erzeugte Kopie sein (SyncOrigin gesetzt) - `sync`
    /// schreibt IMMER von der Kopie (Quelle) zum Original (Ziel), nie
    /// umgekehrt, Last-Writer-Wins, keine Konfliktauflösung.
    /// </summary>
    public static class SyncEngine
    {
        public static SyncResult Sync(ObjectInstance copy, bool blocking, IDestructRunner? runner = null) =>
            Run(copy, blocking, flat: false, runner ?? NullDestructRunner.Instance);

        public static SyncResult SyncFlat(ObjectInstance copy, bool blocking, IDestructRunner? runner = null) =>
            Run(copy, blocking, flat: true, runner ?? NullDestructRunner.Instance);

        private static SyncResult Run(ObjectInstance copy, bool blocking, bool flat, IDestructRunner runner)
        {
            var target = copy.SyncOrigin
                ?? throw new System.InvalidOperationException(
                    "sync: dieses Objekt ist keine 'taking'-Kopie (kein SyncOrigin gesetzt).");
            var treeLock = target.ThreadLock
                ?? throw new System.InvalidOperationException(
                    "sync: interner Fehler - Ziel hat keinen ThreadLock (hätte durch 'taking' gesetzt sein müssen).");

            if (blocking) treeLock.Enter();
            else if (!treeLock.TryEnter()) return SyncResult.LockBusy;

            try
            {
                // Erst NACH dem Lock-Erwerb prüfen (nicht vorher) - ein
                // gleichzeitiges Destroy könnte sonst genau zwischen Check und
                // Lock-Erwerb passieren.
                if (target.IsDestroyed) return SyncResult.TargetGone;

                SyncFields(copy, target, flat, runner);
                return SyncResult.Success;
            }
            finally
            {
                treeLock.Exit();
            }
        }

        /// <summary>Überträgt alle Felder von `source` (der Kopie/dem
        /// jeweiligen Teilbaum-Knoten der Kopie) nach `target` (Original) -
        /// wird bei `flat: false` (vollem sync) rekursiv für Fall C erneut
        /// aufgerufen, bei `flat: true` (sync flat) genau einmal, ohne in
        /// bestehende Objektreferenzen hineinzusteigen.</summary>
        private static void SyncFields(ObjectInstance source, ObjectInstance target, bool flat, IDestructRunner runner)
        {
            // Snapshot der Quell-Felder - source gehört dem aufrufenden Thread
            // exklusiv (es ist SEINE eigene taking-Kopie), eine Kopie der
            // Enumeration ist hier nur nötig, falls source selbst später (bei
            // einem verschachtelten taking) doch geteilt würde.
            foreach (var (name, srcVal) in new List<KeyValuePair<string, Value>>(source.Fields))
            {
                target.Fields.TryGetValue(name, out var tgtVal);
                target.Fields[name] = SyncSingleValue(srcVal, tgtVal, target, flat, runner);
            }
        }

        /// <summary>Ein einzelner Feld-/Array-Element-Wert, nach Fall A/B/C
        /// (siehe docs/THREADING_DESIGN.md 4.3) für Objektreferenzen, mit
        /// Sonderbehandlung für Arrays (4.4) und primitive Werte (direkt
        /// übernommen).</summary>
        private static Value SyncSingleValue(
            Value srcVal, Value tgtVal, ObjectInstance containingTarget, bool flat, IDestructRunner runner)
        {
            bool srcIsObj = srcVal.Kind == ValueKind.Class;
            bool tgtIsObj = tgtVal.Kind == ValueKind.Class;

            if (srcIsObj && tgtIsObj)
            {
                // Fall C: beide vorhanden - Referenz bleibt bestehen. Beim
                // vollen (nicht-flachen) sync steigt die Synchronisation
                // TROTZDEM rekursiv in beide hinein (das ist der einzige
                // Unterschied zwischen 'sync' und 'sync flat' - flat lässt
                // Fall C komplett unangetastet, voller sync synct auch hier
                // weiter).
                var srcChild = (ObjectInstance)srcVal.AsObjectRef();
                var tgtChild = (ObjectInstance)tgtVal.AsObjectRef();
                if (!flat)
                    SyncFields(srcChild, tgtChild, flat: false, runner);
                return tgtVal;
            }

            if (srcIsObj && !tgtIsObj)
            {
                // Fall A: Quelle vorhanden, Ziel nicht - komplette,
                // unabhängige Kopie erzeugen (KEINE Referenzverknüpfung mit
                // der Quelle - ab jetzt divergieren beide wieder, bis zum
                // nächsten sync). Owner der neuen Kopie ist das Objekt, in
                // dessen Feld sie landet. Muss dem gemeinsamen Baum-Lock
                // beitreten (ActivateThreadSharing) - sie ist ab sofort Teil
                // des bereits geteilten Ziel-Baums, jeder künftige Zugriff
                // muss also denselben Lock respektieren wie der Rest des
                // Baums, sonst entstünde genau an dieser Stelle eine
                // ungesicherte Lücke.
                var srcChild = (ObjectInstance)srcVal.AsObjectRef();
                var newCopy = PlainDeepCopy(srcChild, containingTarget);
                newCopy.ActivateThreadSharing(containingTarget.ThreadLock!);
                return Value.MakeClassRef(newCopy);
            }

            if (!srcIsObj && tgtIsObj)
            {
                // Fall B: Quelle leer, Ziel hatte ein Objekt - Ziel wird
                // geleert (übernimmt srcVal, i.d.R. 'undefined'). Verliert das
                // referenzierte Objekt dadurch seinen (einzigen) Owner, läuft
                // die Destruct-Kaskade.
                var tgtChild = (ObjectInstance)tgtVal.AsObjectRef();
                if (ReferenceEquals(tgtChild.Owner, containingTarget))
                {
                    containingTarget.RemoveOwned(tgtChild);
                    tgtChild.Destroy(runner);
                }
                return srcVal;
            }

            if (srcVal.Kind == ValueKind.Array)
            {
                var srcArr = (ScriptArray)srcVal.AsArray();
                var oldArr = tgtVal.Kind == ValueKind.Array ? (ScriptArray)tgtVal.AsArray() : null;

                // Ziel wird eine möglichst exakte 1:1-Kopie der Quelle -
                // neues Array in Quell-Länge, danach elementweise dieselben
                // Fall-A/B/C-Regeln wie bei normalen Feldern (Array-Elemente
                // sind die "direkten Kinder" eines Arrays, siehe SPEC/
                // THREADING_DESIGN.md 4.4).
                var newArr = new ScriptArray(srcArr.Length);
                for (int i = 0; i < srcArr.Length; i++)
                {
                    Value oldElem = (oldArr != null && i < oldArr.Length) ? oldArr.Items[i] : Value.MakeUndefined();
                    newArr.Items[i] = SyncSingleValue(srcArr.Items[i], oldElem, containingTarget, flat, runner);
                }
                return Value.MakeArray(newArr);
            }

            if (srcVal.Kind == ValueKind.Lambda || srcVal.Kind == ValueKind.Pointer)
                throw new TakingViolationException(
                    "'sync' abgelehnt: Lambda-/Pointer-Werte werden in dieser Ausbaustufe nicht unterstützt " +
                    "(dieselbe Einschränkung wie bei 'taking', siehe ObjectCopier-Klassenkommentar).");

            // Primitive Werte (bool/int/float/char/string/undefined) - wertartig, direkt übernommen.
            return srcVal;
        }

        /// <summary>Reine, unabhängige Tiefenkopie (ohne SyncOrigin/Locking -
        /// das übernimmt der Aufrufer, siehe Fall A oben) für frisch bei einem
        /// sync entstehende Objekte. Bewusst eine EIGENE, einfachere Kopie
        /// als ObjectCopier.Take (die für 'taking' gedacht ist und zusätzlich
        /// Baum-Fremd-Referenzen ablehnt/Thread-Sharing aktiviert) - hier ist
        /// das Ziel bereits Teil eines etablierten, geteilten Baums, die
        /// Baum-Zugehörigkeits-Prüfung von ObjectCopier wäre hier nicht
        /// sinnvoll anwendbar (die Quelle lebt ja im KIND-Thread, nicht im
        /// Zielbaum selbst).</summary>
        private static ObjectInstance PlainDeepCopy(ObjectInstance node, IOwner owner)
        {
            var copy = new ObjectInstance(node.ClassDef, owner, node.RtClass);
            foreach (var (name, val) in node.Fields)
            {
                if (val.Kind == ValueKind.Class)
                {
                    var child = (ObjectInstance)val.AsObjectRef();
                    copy.Fields[name] = Value.MakeClassRef(PlainDeepCopy(child, copy));
                }
                else if (val.Kind == ValueKind.Array)
                {
                    var arr = (ScriptArray)val.AsArray();
                    var newArr = new ScriptArray(arr.Length);
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var elem = arr.Items[i];
                        newArr.Items[i] = elem.Kind == ValueKind.Class
                            ? Value.MakeClassRef(PlainDeepCopy((ObjectInstance)elem.AsObjectRef(), copy))
                            : elem;
                    }
                    copy.Fields[name] = Value.MakeArray(newArr);
                }
                else
                {
                    copy.Fields[name] = val;
                }
            }
            return copy;
        }
    }
}
