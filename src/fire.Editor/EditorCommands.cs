using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace fire.Editor
{
    /// <summary>Bearbeiten-Funktionen, die ScriptEditorControl und MarkdownEditorControl gleichermaßen brauchen (Bearbeiten-Menü des Hauptfensters und Kontextmenüs), einmal für den
    /// AvaloniaEdit-TextEditor.</summary>
    internal static class EditorCommands
    {
        /// <summary>Is there text on the clipboard (enables Paste)?</summary>
        public static async System.Threading.Tasks.Task<bool> ClipboardHasText(TopLevel top)
        {
            try { return top.Clipboard != null && !string.IsNullOrEmpty(await top.Clipboard.GetTextAsync()); }
            catch (Exception) { return false; }
        }

        /// <summary>Springt in Zeile `line` (1-basiert, wird auf den gültigen Bereich begrenzt), Cursor an den Zeilenanfang.</summary>
        public static void GoToLine(this TextEditor editor, int line)
        {
            int target = Math.Max(1, Math.Min(line, editor.Document.LineCount));
            editor.TextArea.Caret.Line = target;
            editor.TextArea.Caret.Column = 1;
            editor.TextArea.Caret.BringCaretToView();
            editor.ScrollToLine(target);
            editor.Focus();
        }

        /// <summary>Die vom Cursor/der Auswahl berührten Zeilen (eine Auswahl, die genau am Anfang einer Folgezeile endet, zählt diese nicht mit).</summary>
        public static List<DocumentLine> SelectedLines(this TextEditor editor)
        {
            var doc = editor.Document;
            int a = editor.SelectionStart;
            int b = editor.SelectionStart + editor.SelectionLength;
            var first = doc.GetLineByOffset(a);
            var last = doc.GetLineByOffset(b);
            if (b > a && b == last.Offset && last.LineNumber > first.LineNumber) last = last.PreviousLine;
            var lines = new List<DocumentLine>();
            for (var l = first; l != null; l = l.NextLine)
            {
                lines.Add(l);
                if (l == last) break;
            }
            return lines;
        }

        /// <summary>Beim Rechtsklick den Cursor unter die Maus setzen - außer, der Klick liegt in einer bestehenden Auswahl (dann soll sie für Kopieren/Ausschneiden erhalten bleiben).
        /// Liefert den Textoffset unter der Maus (oder den Cursor, wenn dort kein Text liegt).</summary>
        public static int PlaceCaretForContextMenu(this TextEditor editor, PointerPressedEventArgs e)
        {
            var pos = editor.GetPositionFromPoint(e.GetPosition(editor));
            if (pos == null) return editor.CaretOffset;
            int offset = editor.Document.GetOffset(pos.Value.Location);
            bool inSelection = editor.SelectionLength > 0 &&
                offset >= editor.SelectionStart && offset <= editor.SelectionStart + editor.SelectionLength;
            if (!inSelection) editor.CaretOffset = offset;
            return offset;
        }

        /// <summary>Ein Eintrag eines Kontextmenüs; `enabled` wird bei jedem Öffnen neu ausgewertet.</summary>
        public sealed class Entry
        {
            public required string Header { get; init; }
            public string? Gesture { get; init; }
            public required Action Execute { get; init; }
            public Func<bool>? Enabled { get; init; }
            internal MenuItem? Item;
        }

        /// <summary>Baut ein Kontextmenü aus Gruppen von Einträgen (null = Trennlinie); Standard-Bearbeiten-Einträge hängt <see cref="StandardEntries"/> an.</summary>
        public static ContextMenu BuildMenu(IEnumerable<Entry?> entries)
        {
            var menu = new ContextMenu();
            var items = new List<object>();
            var all = new List<Entry>();
            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    if (items.Count > 0 && items[^1] is not Separator) items.Add(new Separator());
                    continue;
                }
                var item = new MenuItem { Header = entry.Header };
                MenuGestures.Apply(item, entry.Gesture);
                var execute = entry.Execute;
                item.Click += (_, _) => execute();
                entry.Item = item;
                items.Add(item);
                all.Add(entry);
            }
            if (items.Count > 0 && items[^1] is Separator) items.RemoveAt(items.Count - 1);
            foreach (var item in items) menu.Items.Add(item);
            menu.Opening += (_, _) =>
            {
                foreach (var entry in all)
                    if (entry.Enabled != null) entry.Item!.IsEnabled = entry.Enabled();
            };
            return menu;
        }

        /// <summary>Rückgängig/Wiederholen, Ausschneiden/Kopieren/Einfügen/Löschen, Alles auswählen, Suchen.</summary>
        public static IEnumerable<Entry?> StandardEntries(TextEditor editor, Action find)
        {
            yield return new Entry { Header = "_Undo", Gesture = "Ctrl+Z", Execute = () => editor.Undo(), Enabled = () => !editor.IsReadOnly && editor.Document.UndoStack.CanUndo };
            yield return new Entry { Header = "_Redo", Gesture = "Ctrl+Y", Execute = () => editor.Redo(), Enabled = () => !editor.IsReadOnly && editor.Document.UndoStack.CanRedo };
            yield return null;
            yield return new Entry { Header = "Cu_t", Gesture = "Ctrl+X", Execute = () => editor.Cut(), Enabled = () => !editor.IsReadOnly && editor.SelectionLength > 0 };
            yield return new Entry { Header = "_Copy", Gesture = "Ctrl+C", Execute = () => editor.Copy(), Enabled = () => editor.SelectionLength > 0 };
            yield return new Entry { Header = "_Paste", Gesture = "Ctrl+V", Execute = () => editor.Paste(), Enabled = () => !editor.IsReadOnly };
            yield return new Entry { Header = "_Delete", Gesture = "Del", Execute = () => editor.Delete(), Enabled = () => !editor.IsReadOnly && editor.SelectionLength > 0 };
            yield return null;
            yield return new Entry { Header = "Select _All", Gesture = "Ctrl+A", Execute = () => editor.SelectAll() };
            yield return null;
            yield return new Entry { Header = "_Find...", Gesture = "Ctrl+F", Execute = find };
        }
    }

    /// <summary>The keyboard shortcut shown next to a menu item. Avalonia shows a <see cref="KeyGesture"/>; a text that is not one (`F12 / Ctrl+Click`) goes behind the header instead.</summary>
    internal static class MenuGestures
    {
        public static void Apply(MenuItem item, string? gesture)
        {
            if (string.IsNullOrEmpty(gesture)) return;
            try { item.InputGesture = KeyGesture.Parse(gesture); }
            catch (Exception) { item.Header = $"{item.Header}    ({gesture})"; }
        }
    }
}
