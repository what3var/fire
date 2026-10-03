namespace fire.Bytecode
{
    /// <summary>
    /// Inline-Cache für EINE Aufrufstelle im Bytecode (Methoden-/Feldzugriff, siehe VM.OpCallMethod,
    /// OpGetField, ...): merkt sich, wozu sich die Stelle beim letzten Mal aufgelöst hat, damit der
    /// nächste Durchlauf mit derselben Klasse die Namens-Lookups (Dictionary mit Zeichenkettenhash)
    /// überspringen kann. Unveränderlich - ein Thread ersetzt den ganzen Eintrag, statt ihn zu ändern,
    /// dadurch sieht ein anderer Thread nie einen halb geschriebenen Zustand.
    ///
    /// Ein Eintrag entsteht NUR, nachdem alle Prüfungen der langsamen Route für genau diese Stelle bestanden
    /// haben (Zugriffsmodifikator, Argumentanzahl, Einheiten-Vorgabe eines Felds): sie hängen nur von der Stelle
    /// (ihrem Chunk/Besitzer) und der Klasse des Empfängers ab, beides ist im Eintrag festgehalten. Was sich zur
    /// Laufzeit ändern kann (ein Thread-Lock am Objekt, ein Actor-Postfach), prüft der Schnellpfad selbst.
    /// </summary>
    public sealed class SiteCache
    {
        /// <summary>Die Klasse des Empfängers, für die der Eintrag gilt (Methoden-/Feldzugriff auf Instanzen).</summary>
        public readonly RuntimeClass? Class;

        /// <summary>Die aufzurufende Methode (Methodenaufrufe).</summary>
        public readonly FunctionProto? Proto;

        /// <summary>Der Feld-Index in <c>FieldStore</c> (Feldzugriffe).</summary>
        public readonly int FieldIndex;

        /// <summary>Ist die Methode eine reine Weiterleitung an eine native Funktion (siehe NativeForwarder), die
        /// Weiterleitung samt dem Index ihres Feldes (<c>ForwarderFieldIndex</c>) in dieser Klasse - sonst null.</summary>
        public readonly NativeForwarder? Forwarder;
        public readonly int ForwarderFieldIndex;

        public SiteCache(RuntimeClass? @class, FunctionProto? proto, int fieldIndex, NativeForwarder? forwarder = null, int forwarderFieldIndex = -1)
        {
            Class = @class;
            Proto = proto;
            FieldIndex = fieldIndex;
            Forwarder = forwarder;
            ForwarderFieldIndex = forwarderFieldIndex;
        }
    }
}
