using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// Generischer ID-basierter Objekt-Manager - gemeinsame Grundlage für
    /// FramebufferManager/ConsoleManager (hier) und WindowManager (siehe
    /// fire.Terminal.Windows). Vergibt AUFSTEIGENDE, innerhalb dieser
    /// EINEN Instanz eindeutige IDs, beginnend bei 1 - 0 bleibt bewusst als
    /// "ungültige/keine ID" reserviert, damit ein vergessenes oder falsch
    /// initialisiertes Id-Feld (Standardwert 0 in C#) nicht zufällig auf ein
    /// echtes Objekt zeigt. IDs werden nach Destroy() NIE wiederverwendet
    /// (der Zähler läuft nur vorwärts) - ein "zu spät" durch die
    /// Skriptsprache weitergereichter, bereits zerstörter Handle zeigt
    /// dadurch verlässlich ins Leere statt versehentlich auf ein NEUES,
    /// andersartiges Objekt derselben ID.
    /// </summary>
    public sealed class IdManager<T> where T : class
    {
        private readonly Dictionary<int, T> _items = new();
        private int _nextId = 1;

        public int Create(T item)
        {
            int id = _nextId++;
            _items[id] = item;
            return id;
        }

        /// <summary>Wirft KeyNotFoundException bei unbekannter/bereits
        /// zerstörter ID - für die meisten Aufrufstellen die richtige Wahl
        /// (ein Aufruf mit ungültiger ID ist ein Programmierfehler des
        /// Aufrufers, kein normaler, leise zu tolerierender Fall).</summary>
        public T Get(int id) =>
            _items.TryGetValue(id, out var item)
                ? item
                : throw new KeyNotFoundException($"Keine Ressource mit ID {id} (unbekannt oder bereits zerstört).");

        public bool TryGet(int id, out T? item) => _items.TryGetValue(id, out item);

        /// <summary>Liefert false (statt zu werfen), wenn `id` nicht (mehr)
        /// existiert - Zerstören einer bereits zerstörten/unbekannten ID ist
        /// bewusst KEIN Fehler (erleichtert idempotentes Aufräumen).</summary>
        public bool Destroy(int id) => _items.Remove(id);

        public IEnumerable<int> Ids => _items.Keys;
    }
}
