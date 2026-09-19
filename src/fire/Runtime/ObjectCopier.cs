using System.Collections.Generic;
using ScriptLang.Values;

namespace ScriptLang.Runtime
{
    /// <summary>Wird geworfen, wenn `taking` (ObjectCopier.Take) eine Referenz
    /// findet, die nicht sicher isoliert kopiert werden kann - entweder weil
    /// sie aus dem kopierten Ownership-Baum HINAUSZEIGT (siehe
    /// docs/THREADING_DESIGN.md Abschnitt 3: "harte Regel"), oder weil es sich
    /// um eine Werteart handelt, die diese Ausbaustufe noch gar nicht
    /// unterstützt (Lambda, Pointer - siehe ObjectCopier-Klassenkommentar).</summary>
    public sealed class TakingViolationException : System.Exception
    {
        public TakingViolationException(string message) : base(message) { }
    }

    /// <summary>
    /// `taking X` (docs/THREADING_DESIGN.md Abschnitt 3): erzeugt eine
    /// vollständige, isolierte Tiefenkopie von X's gesamtem EIGENEM
    /// Ownership-Baum für einen Fire-Thread. Aktiviert dabei Thread-Sharing
    /// (ObjectInstance.ActivateThreadSharing) auf dem ORIGINAL-Baum - ab
    /// diesem Zeitpunkt respektiert JEDER Feldzugriff auf JEDEN Knoten in
    /// diesem Baum den gemeinsamen Baum-Lock, auch normale Zugriffe vom
    /// besitzenden (Ursprungs-)Thread selbst (siehe ThreadShareLock-Doku).
    ///
    /// BEWUSST NICHT unterstützt in dieser ersten Ausbaustufe (wirft
    /// TakingViolationException, statt eine unsichere/inkorrekte Kopie zu
    /// erzeugen):
    /// - Referenzen (Objekt-Felder), die aus dem kopierten Baum HINAUSZEIGEN
    ///   (auf ein unabhängig besessenes Objekt) - das wäre ein verstecktes
    ///   Shared-Memory-Loch, genau das, was `taking` verhindern soll.
    /// - Lambda-Werte irgendwo im erreichbaren Graphen - eine korrekte Kopie
    ///   müsste bei gebundenem `this` (`on obj`) dessen Referenz auf die neu
    ///   erzeugte KOPIE dieses Objekts ummappen (zweistufiges Verfahren:
    ///   erst den ganzen Objektgraphen kopieren, dann in einem zweiten
    ///   Durchlauf alle Lambda-Bindungen nachträglich umbiegen) - das ist
    ///   eine sinnvolle Erweiterung für eine spätere Ausbaustufe, hier aus
    ///   Zeit-/Risikogründen bewusst zurückgestellt.
    /// - Rohe Pointer (`unsafe`/`&`) irgendwo im erreichbaren Graphen - ein
    ///   Pointer zeigt auf einen konkreten, verwalteten Speicherort (Scope-
    ///   Slot oder Objekt-Feld, siehe PointerTarget-Doku), für den es keine
    ///   sinnvolle "Kopie" gibt, ohne die Ziel-Adresse selbst neu aufzulösen
    ///   - unabhängig davon, ob er in den eigenen Baum oder hinaus zeigt.
    /// </summary>
    public static class ObjectCopier
    {
        /// <summary>Erzeugt die isolierte Kopie von `root`s Ownership-Baum,
        /// mit `newOwner` als Owner der Kopie (typischerweise ein Scope im
        /// neuen Fire-Thread). Die Kopie trägt hinterher `SyncOrigin == root`
        /// (siehe ObjectInstance.SyncOrigin-Doku), Grundlage für `sync`/
        /// `sync flat` (siehe SyncEngine).</summary>
        public static ObjectInstance Take(ObjectInstance root, IOwner newOwner)
        {
            var treeLock = new ThreadShareLock();
            root.ActivateThreadSharing(treeLock);

            var map = new Dictionary<ObjectInstance, ObjectInstance>(ReferenceEqualityComparer.Instance);
            var copy = CopyNode(root, root, newOwner, map);
            copy.SyncOrigin = root;
            return copy;
        }

        private static ObjectInstance CopyNode(
            ObjectInstance node, ObjectInstance treeRoot, IOwner copyOwner,
            Dictionary<ObjectInstance, ObjectInstance> map)
        {
            if (map.TryGetValue(node, out var existing)) return existing;

            var copy = new ObjectInstance(node.ClassDef, copyOwner, node.RtClass);
            map[node] = copy;

            // Unter dem Baum-Lock lesen (node kann seit ActivateThreadSharing
            // theoretisch schon von einem parallel laufenden anderen Thread
            // beobachtet werden, falls root selbst schon vorher Ziel von
            // taking war - ein verschachteltes taking auf einen bereits
            // ausgecheckten Teilbaum).
            var fieldsSnapshot = new List<KeyValuePair<string, Value>>();
            if (node.ThreadLock != null)
            {
                node.ThreadLock.Enter();
                try { fieldsSnapshot.AddRange(node.Fields); }
                finally { node.ThreadLock.Exit(); }
            }
            else
            {
                fieldsSnapshot.AddRange(node.Fields);
            }

            foreach (var (name, value) in fieldsSnapshot)
                copy.Fields[name] = CopyValue(value, treeRoot, copyOwner, map);

            return copy;
        }

        private static Value CopyValue(
            Value v, ObjectInstance treeRoot, IOwner copyOwner,
            Dictionary<ObjectInstance, ObjectInstance> map)
        {
            switch (v.Kind)
            {
                case ValueKind.Class:
                {
                    var target = (ObjectInstance)v.AsObjectRef();
                    if (!IsWithinTree(target, treeRoot))
                        throw new TakingViolationException(
                            $"'taking' abgelehnt: ein Feld verweist auf eine Instanz von " +
                            $"'{target.ClassDef.Name}', die nicht zum eigenen Ownership-Baum gehört.");
                    var childCopy = CopyNode(target, treeRoot, copyOwner, map);
                    return Value.MakeClassRef(childCopy);
                }

                case ValueKind.Array:
                {
                    var arr = (ScriptArray)v.AsArray();
                    var newArr = new ScriptArray(arr.Length);
                    for (int i = 0; i < arr.Length; i++)
                        newArr.Items[i] = CopyValue(arr.Items[i], treeRoot, copyOwner, map);
                    return Value.MakeArray(newArr);
                }

                case ValueKind.Lambda:
                    throw new TakingViolationException(
                        "'taking' abgelehnt: Lambda-Werte können in dieser Ausbaustufe nicht " +
                        "kopiert werden (siehe ObjectCopier-Klassenkommentar).");

                case ValueKind.Pointer:
                    throw new TakingViolationException(
                        "'taking' abgelehnt: rohe Pointer können in dieser Ausbaustufe nicht " +
                        "kopiert werden (siehe ObjectCopier-Klassenkommentar).");

                default:
                    // bool/int/float/char/string/undefined - wertartig, direkt kopierbar.
                    return v;
            }
        }

        /// <summary>`node` gehört zu `treeRoot`s eigenem Ownership-Baum, wenn es
        /// entweder die Wurzel selbst ist oder transitiv über die Owner-Kette
        /// von ihr besessen wird (ObjectInstance.IsTransitivelyOwnedBy).</summary>
        private static bool IsWithinTree(ObjectInstance node, ObjectInstance treeRoot) =>
            ReferenceEquals(node, treeRoot) || node.IsTransitivelyOwnedBy(treeRoot);
    }
}
