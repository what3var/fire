namespace fire.Bytecode
{
    /// <summary>
    /// Steuert, wie viel Laufzeit-Overhead die VM sich leistet - zwei Stufen
    /// oberhalb des Standardverhaltens (<see cref="Debug"/>):
    ///
    /// - <see cref="Debug"/> (Default, unverändertes bisheriges Verhalten):
    ///   der kooperative Shutdown-Prüfpunkt (siehe VM.CheckShutdownSignals)
    ///   läuft vor JEDER einzelnen Instruktion - maximale Reaktionsfreude
    ///   auf `leave`/`terminate`, auf Kosten von etwas Overhead pro
    ///   Instruktion. Alle Sicherheitsprüfungen (Array-/Puffer-Bounds) aktiv.
    ///
    /// - <see cref="Release"/>: NUR der kooperative Overhead wird reduziert
    ///   (Shutdown-Prüfpunkt läuft nur noch periodisch, nicht mehr vor jeder
    ///   Instruktion) - alle sicherheitsrelevanten Prüfungen (Array-/Puffer-
    ///   Bounds) bleiben vollständig aktiv, ein Skriptfehler bleibt also
    ///   weiterhin eine saubere, fangbare Skript-Exception statt eines
    ///   rohen Absturzes.
    ///
    /// - <see cref="Performance"/>: wie Release, zusätzlich werden auch
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
        Debug,
        Release,
        Performance,
    }
}
