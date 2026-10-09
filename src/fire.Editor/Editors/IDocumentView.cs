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

        /// <summary>Read-only documents cannot be edited or saved (the Save commands are disabled).</summary>
        bool IsReadOnly { get; }

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

        // Bearbeiten-Menü (siehe MainWindow) - wirkt immer auf das AKTIVE Dokument.
        bool CanUndo { get; }
        bool CanRedo { get; }
        bool HasSelection { get; }
        void Undo();
        void Redo();
        void Cut();
        void Copy();
        void Paste();
        void Delete();
        void SelectAll();
        void Find();
        void FindNext();
        void FindPrevious();
        int LineCount { get; }

        /// <summary>Springt in Zeile `line` (1-basiert) und setzt den Fokus in den Editor.</summary>
        void GoToLine(int line);
    }
}
