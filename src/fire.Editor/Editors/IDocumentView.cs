using System;

namespace fire.Editor
{
    /// <summary>What the main window needs from EVERY document tab,
    /// regardless of whether it is a fire script (ScriptEditorControl) or a
    /// Markdown document (MarkdownEditorControl).</summary>
    public interface IDocumentView
    {
        /// <summary>File path, `null` as long as the document has never been saved/opened.</summary>
        string? FilePath { get; set; }

        /// <summary>Read-only documents cannot be edited or saved (the Save commands are disabled).</summary>
        bool IsReadOnly { get; }

        /// <summary>Changed since loading/the last save?</summary>
        bool IsModified { get; }
        event Action? ModifiedChanged;

        /// <summary>Fires on every cursor movement with the new 1-based line.</summary>
        event Action<int>? CaretLineChanged;

        int GetCaretLine();
        string GetText();

        /// <summary>Replaces the content (discards breakpoints, diagnostics etc.) and sets path/"unchanged".</summary>
        void ResetTo(string text, string? filePath);

        /// <summary>The host has saved the text.</summary>
        void MarkSaved();

        void FocusEditor();

        // Edit menu (see MainWindow) - always acts on the ACTIVE document.
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

        /// <summary>Jumps to line `line` (1-based) and sets the focus into the editor.</summary>
        void GoToLine(int line);
    }
}
