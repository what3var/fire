using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace fire.Editor
{
    /// <summary>Bearbeiten-Funktionen, die ScriptEditorControl und
    /// MarkdownEditorControl gleichermaßen brauchen (Bearbeiten-Menü des
    /// Hauptfensters und Kontextmenüs), einmal für AvalonEdits TextEditor.</summary>
    internal static class EditorCommands
    {
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

        /// <summary>Beim Rechtsklick den Cursor unter die Maus setzen - außer, der Klick liegt in einer bestehenden
        /// Auswahl (dann soll sie für Kopieren/Ausschneiden erhalten bleiben). Liefert den Textoffset unter der Maus
        /// (oder den Cursor, wenn dort kein Text liegt).</summary>
        public static int PlaceCaretForContextMenu(this TextEditor editor, MouseButtonEventArgs e)
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

        /// <summary>Baut ein Kontextmenü aus Gruppen von Einträgen (null = Trennlinie); Standard-Bearbeiten-Einträge hängt
        /// <see cref="StandardEntries"/> an.</summary>
        public static ContextMenu BuildMenu(IEnumerable<Entry?> entries)
        {
            var menu = new ContextMenu();
            var all = new List<Entry>();
            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator());
                    continue;
                }
                var item = new MenuItem { Header = entry.Header, InputGestureText = entry.Gesture };
                var execute = entry.Execute;
                item.Click += (_, _) => execute();
                entry.Item = item;
                menu.Items.Add(item);
                all.Add(entry);
            }
            if (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
            menu.Opened += (_, _) =>
            {
                foreach (var entry in all)
                    if (entry.Enabled != null) entry.Item!.IsEnabled = entry.Enabled();
            };
            return menu;
        }

        /// <summary>Liegt Text in der Zwischenablage? (Sie kann kurz von einem anderen Programm gesperrt sein - dann: nein.)</summary>
        public static bool ClipboardHasText()
        {
            try { return Clipboard.ContainsText(); }
            catch (System.Runtime.InteropServices.COMException) { return false; }
        }

        /// <summary>Rückgängig/Wiederholen, Ausschneiden/Kopieren/Einfügen/Löschen, Alles auswählen, Suchen.</summary>
        public static IEnumerable<Entry?> StandardEntries(TextEditor editor, Action find)
        {
            yield return new Entry { Header = "_Rückgängig", Gesture = "Strg+Z", Execute = () => editor.Undo(), Enabled = () => !editor.IsReadOnly && editor.Document.UndoStack.CanUndo };
            yield return new Entry { Header = "_Wiederholen", Gesture = "Strg+Y", Execute = () => editor.Redo(), Enabled = () => !editor.IsReadOnly && editor.Document.UndoStack.CanRedo };
            yield return null;
            yield return new Entry { Header = "A_usschneiden", Gesture = "Strg+X", Execute = () => editor.Cut(), Enabled = () => !editor.IsReadOnly && editor.SelectionLength > 0 };
            yield return new Entry { Header = "_Kopieren", Gesture = "Strg+C", Execute = () => editor.Copy(), Enabled = () => editor.SelectionLength > 0 };
            yield return new Entry { Header = "_Einfügen", Gesture = "Strg+V", Execute = () => editor.Paste(), Enabled = () => !editor.IsReadOnly && ClipboardHasText() };
            yield return new Entry { Header = "_Löschen", Gesture = "Entf", Execute = () => editor.Delete(), Enabled = () => !editor.IsReadOnly && editor.SelectionLength > 0 };
            yield return null;
            yield return new Entry { Header = "_Alles auswählen", Gesture = "Strg+A", Execute = () => editor.SelectAll() };
            yield return null;
            yield return new Entry { Header = "_Suchen...", Gesture = "Strg+F", Execute = find };
        }
    }
}
