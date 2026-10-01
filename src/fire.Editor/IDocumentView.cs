using System;

namespace fire.Editor
{
    /// <summary>Das, was das Hauptfenster von JEDEM Dokument-Tab braucht,
    /// unabhängig davon, ob es ein fire-Skript (ScriptEditorControl) oder ein
    /// Markdown-Dokument (MarkdownEditorControl) ist.</summary>
    public interface IDocumentView
    {
        /// <summary>Dateipfad, `null` solange das Dokument noch nie gespeichert/geöffnet wurde.</summary>
        string? FilePath { get; set; }

        /// <summary>Seit dem Laden/letzten Speichern verändert?</summary>
        bool IsModified { get; }
        event Action? ModifiedChanged;

        /// <summary>Feuert bei jeder Cursor-Bewegung mit der neuen 1-basierten Zeile.</summary>
        event Action<int>? CaretLineChanged;

        int GetCaretLine();
        string GetText();

        /// <summary>Ersetzt den Inhalt (verwirft Haltepunkte, Diagnostik usw.) und setzt Pfad/„unverändert“.</summary>
        void ResetTo(string text, string? filePath);

        /// <summary>Der Host hat den Text gespeichert.</summary>
        void MarkSaved();

        void FocusEditor();
    }
}
