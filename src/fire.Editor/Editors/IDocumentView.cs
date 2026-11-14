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
        /// <summary>Find with replace (a text editor opens its search with the replace field).</summary>
        void FindReplace() => Find();
        void FindNext();
        void FindPrevious();
        int LineCount { get; }

        /// <summary>Jumps to line `line` (1-based) and sets the focus into the editor.</summary>
        void GoToLine(int line);

        // For the tools of an assistant (the MCP server of spark): the selection, and a change of it. A view that has no text keeps the defaults.
        /// <summary>The selected text ("" when nothing is selected).</summary>
        string GetSelectedText() => "";
        /// <summary>The first and the last line of the selection (both the caret's line when nothing is selected).</summary>
        (int First, int Last) GetSelectionLines() => (GetCaretLine(), GetCaretLine());
        /// <summary>The column of the caret (1-based).</summary>
        int GetCaretColumn() => 1;
        /// <summary>Replaces the selection by `text` (with nothing selected: puts it in at the caret) - one step of undo. False when the view cannot (read-only, no text).</summary>
        bool ReplaceSelection(string text) => false;
        /// <summary>Replaces the lines first..last (1-based; last = first - 1: puts `text` in before line `first`) by `text` - one step of undo. False when the view cannot.</summary>
        bool ReplaceLines(int first, int last, string text) => false;
    }
}
