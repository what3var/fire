namespace fire.Runtime
{
    /// <summary>
    /// Steuert, wie viel Laufzeit-Overhead die VM sich leistet - zwei Stufen
    /// oberhalb des Standardverhaltens (<see cref="Debug"/>):
    ///
    /// - <see cref="Debug"/> (Default): alle Sicherheitsprüfungen (Array-/Puffer-
    ///   Bounds, Zugriffsmodifikatoren, Einheiten-Vorgaben von Feldern) aktiv.
    ///
    /// - <see cref="Release"/>: wie Debug - früher senkte dieser Modus den
    ///   Overhead der Shutdown-Prüfung pro Instruktion; die läuft inzwischen in
    ///   JEDEM Modus nur noch an sicheren Punkten (Schleifen-Rücksprung, Aufruf,
    ///   `leave`/`terminate`, siehe VM.PollSignals) und kostet dort einen
    ///   Vergleich - der Modus unterscheidet sich von Debug nicht mehr.
    ///
    /// - <see cref="Performance"/>: zusätzlich werden die
    ///   Array-/Puffer-Bounds-Prüfungen übersprungen (siehe ScriptArray/
    ///   Values.ByteBuffer, jeweils *Unchecked-Varianten) - ein ungültiger
    ///   Index führt dann zu einer ROHEN, UNGEFANGENEN .NET-
    ///   IndexOutOfRangeException (das Betriebssystem/.NET prüft Array-
    ///   Zugriffe ohnehin immer selbst, das lässt sich in verwaltetem C#
    ///   nicht vollständig umgehen - "Bounds-Checks abschalten" bedeutet
    ///   hier konkret: die aufwendigere Umwandlung in eine fangbare,
    ///   ordentliche Skript-Exception entfällt, nicht die Speichersicherheit
    ///   selbst) statt einer fangbaren `IndexOutOfBoundsException`. Nur für
    ///   bereits ausführlich getesteten, vertrauenswürdigen Code gedacht.
    /// </summary>
    public enum VmExecutionMode
    {
        Debug = 0,
        Release = 1,
        Performance = 2,
    }
}
