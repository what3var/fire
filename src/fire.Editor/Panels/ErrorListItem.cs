namespace fire.Editor
{
    /// <summary>Eine Zeile der Fehlerliste (Spalten: Schweregrad, Beschreibung, Datei, Zeile).</summary>
    public sealed record ErrorListItem(string Severity, string Description, string File, int Line);
}
