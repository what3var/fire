using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>
    /// A hex editor for the files of a project that are no text (data, a resource): the offset, 16 bytes in hex, and the same bytes as text. Typing hex digits (or, in the text column, characters)
    /// overwrites the byte at the caret - at the end it appends; Insert puts in a zero byte, Delete takes the byte out. Ctrl+G jumps to an offset, Find looks for bytes (`DE AD BE EF`) or text
    /// (in quotes). Everything can be undone. Files of up to 128 MB are opened.
    /// </summary>
    public sealed class HexEditorControl : UserControl, IDocumentView, IBinaryDocument
    {
        public const long MaxFileSize = 128L * 1024 * 1024;
        private const int Columns = 16;

        /// <summary>One change: `Removed` at `Offset` was replaced by `Inserted` (undo does it the other way round).</summary>
        private sealed record Edit(int Offset, byte[] Removed, byte[] Inserted);

        private readonly List<byte> _data = new();
        private readonly List<Edit> _undo = new(), _redo = new();
        private int _caret;           // byte index (may be one past the end)
        private bool _lowNibble;      // the next hex digit is the low half of the byte
        private bool _asciiPane;      // the caret is in the text column
        private int _topRow;
        private byte[]? _lastSearch;
        private bool _modified;
        private readonly HexView _view;
        private readonly ScrollBar _scrollBar = new() { Orientation = Avalonia.Layout.Orientation.Vertical, Width = 14 };
        private readonly TextBlock _info = new() { Foreground = EditorTheme.TextDim, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(8, 0) };

        public string? FilePath { get; set; }
        public bool IsModified => _modified;
        public bool IsReadOnly => false;
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged;

        /// <summary>The picture of the bytes; all the logic is in the control around it.</summary>
        private sealed class HexView : Control
        {
            private static readonly Typeface Face = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
            private const double FontSize = 14;
            private const double Pad = 8;
            public double CharWidth { get; }
            public double LineHeight { get; }
            public HexEditorControl Owner { get; }
            public int VisibleRows => Math.Max(1, (int)(Bounds.Height / LineHeight));

            public HexView(HexEditorControl owner)
            {
                Owner = owner;
                Focusable = true;
                ClipToBounds = true;
                var probe = Format("0");
                CharWidth = probe.Width;
                LineHeight = Math.Ceiling(probe.Height) + 2;
            }

            private static FormattedText Format(string text, IBrush? brush = null) =>
                new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSize, brush ?? EditorTheme.Text);

            public double HexX(int column) => Pad + 10 * CharWidth + column * 3 * CharWidth + (column >= 8 ? CharWidth : 0);
            public double AsciiX => HexX(Columns) + 2 * CharWidth;

            public override void Render(DrawingContext context)
            {
                context.FillRectangle(EditorTheme.DarkSurface, new Rect(Bounds.Size));
                var data = Owner._data;
                int rows = Math.Max(1, (data.Count + Columns - 1) / Columns + (data.Count % Columns == 0 ? 1 : 0));
                int caretRow = Owner._caret / Columns, caretColumn = Owner._caret % Columns;
                for (int r = 0; r < VisibleRows + 1; r++)
                {
                    int row = Owner._topRow + r;
                    if (row >= rows) break;
                    double y = r * LineHeight;
                    context.DrawText(Format($"{row * Columns:X8}", EditorTheme.LineNumber), new Point(Pad, y));
                    var hex = new StringBuilder();
                    var text = new StringBuilder();
                    for (int c = 0; c < Columns; c++)
                    {
                        int index = row * Columns + c;
                        if (index < data.Count)
                        {
                            hex.Append(data[index].ToString("X2"));
                            text.Append(data[index] is >= 32 and < 127 ? (char)data[index] : '.');
                        }
                        else { hex.Append("  "); text.Append(' '); }
                        if (c < Columns - 1) hex.Append(c == 7 ? "  " : " ");
                    }
                    context.DrawText(Format(hex.ToString()), new Point(HexX(0), y));
                    context.DrawText(Format(text.ToString(), EditorTheme.TextDim), new Point(AsciiX, y));
                    if (row == caretRow)
                    {
                        var hexCell = new Rect(HexX(caretColumn) - 1, y, 2 * CharWidth + 2, LineHeight);
                        var asciiCell = new Rect(AsciiX + caretColumn * CharWidth - 1, y, CharWidth + 2, LineHeight);
                        var active = Owner._asciiPane ? asciiCell : hexCell;
                        var other = Owner._asciiPane ? hexCell : asciiCell;
                        context.FillRectangle(EditorTheme.Selection, active);
                        context.DrawRectangle(null, new Pen(EditorTheme.Selection, 1), other);
                        if (!Owner._asciiPane)   // which half the next digit goes to
                            context.DrawLine(new Pen(Brushes.White, 2), new Point(HexX(caretColumn) + (Owner._lowNibble ? CharWidth : 0), y + LineHeight - 2), new Point(HexX(caretColumn) + (Owner._lowNibble ? 2 : 1) * CharWidth, y + LineHeight - 2));
                    }
                }
            }

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                Focus();
                var p = e.GetCurrentPoint(this).Position;
                int row = Owner._topRow + (int)(p.Y / LineHeight);
                int column;
                bool ascii = p.X >= AsciiX - CharWidth;
                if (ascii) column = (int)((p.X - AsciiX) / CharWidth);
                else
                {
                    column = 0;
                    for (int c = 0; c < Columns; c++) if (p.X >= HexX(c) - CharWidth / 2) column = c;
                }
                Owner.MoveCaretTo(Math.Max(0, row) * Columns + Math.Clamp(column, 0, Columns - 1), ascii);
            }

            protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            {
                Owner.ScrollRows(e.Delta.Y > 0 ? -3 : 3);
                e.Handled = true;
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                Owner.HandleKey(e);
                base.OnKeyDown(e);
            }

            protected override void OnTextInput(TextInputEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Text)) Owner.TypeText(e.Text);
                base.OnTextInput(e);
            }
        }

        public HexEditorControl()
        {
            _view = new HexView(this);
            _view.SizeChanged += (_, _) => UpdateScrollBar();
            _scrollBar.ValueChanged += (_, e) => { _topRow = Math.Max(0, (int)Math.Round(e.NewValue)); _view.InvalidateVisual(); };
            var goTo = new Button { Content = "Go to..." }; goTo.Classes.Add("tool"); goTo.Click += async (_, _) => await GoToOffsetDialog();
            var find = new Button { Content = "Find..." }; find.Classes.Add("tool"); find.Click += (_, _) => Find();
            var bar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2), Children = { goTo, find, _info } };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(_view);
            Grid.SetColumn(_scrollBar, 1);
            grid.Children.Add(_scrollBar);
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(grid);
            Content = dock;
            Focusable = true;
        }

        // ---- the data and the caret -----------------------------------------------------------------------------------------------------------

        private int RowCount => Math.Max(1, (_data.Count + Columns - 1) / Columns + (_data.Count % Columns == 0 ? 1 : 0));

        private void UpdateScrollBar()
        {
            _scrollBar.Minimum = 0;
            _scrollBar.Maximum = Math.Max(0, RowCount - _view.VisibleRows);
            _scrollBar.ViewportSize = _view.VisibleRows;
            _scrollBar.LargeChange = Math.Max(1, _view.VisibleRows - 1);
            _scrollBar.SmallChange = 1;
            _topRow = Math.Clamp(_topRow, 0, (int)_scrollBar.Maximum);
            _scrollBar.Value = _topRow;
            _scrollBar.IsVisible = _scrollBar.Maximum > 0;
        }

        private void UpdateInfo() =>
            _info.Text = $"{_data.Count:N0} bytes   offset {_caret:X8} ({_caret:N0})" + (_caret < _data.Count ? $"   value {_data[_caret]} = 0x{_data[_caret]:X2}" : "   (end)");

        internal void ScrollRows(int delta)
        {
            _topRow = Math.Clamp(_topRow + delta, 0, Math.Max(0, RowCount - _view.VisibleRows));
            _scrollBar.Value = _topRow;
            _view.InvalidateVisual();
        }

        private void MoveCaretTo(int index, bool ascii)
        {
            _caret = Math.Clamp(index, 0, _data.Count);
            _asciiPane = ascii;
            _lowNibble = false;
            int row = _caret / Columns;
            if (row < _topRow) _topRow = row;
            else if (row >= _topRow + _view.VisibleRows) _topRow = row - _view.VisibleRows + 1;
            _topRow = Math.Clamp(_topRow, 0, Math.Max(0, RowCount - _view.VisibleRows));
            UpdateScrollBar();
            UpdateInfo();
            CaretLineChanged?.Invoke(row + 1);
            _view.InvalidateVisual();
        }

        private void SetModified(bool value)
        {
            if (_modified == value) return;
            _modified = value;
            ModifiedChanged?.Invoke();
        }

        // ---- editing ----------------------------------------------------------------------------------------------------------------------------

        private void Apply(Edit edit)
        {
            _data.RemoveRange(edit.Offset, edit.Removed.Length);
            _data.InsertRange(edit.Offset, edit.Inserted);
        }

        private void Do(Edit edit)
        {
            Apply(edit);
            _undo.Add(edit);
            _redo.Clear();
            SetModified(true);
            UpdateScrollBar();
        }

        internal void HandleKey(KeyEventArgs e)
        {
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            int page = Math.Max(1, _view.VisibleRows - 1) * Columns;
            switch (e.Key)
            {
                case Key.Left: MoveCaretTo(_caret - 1, _asciiPane); break;
                case Key.Right: MoveCaretTo(_caret + 1, _asciiPane); break;
                case Key.Up: MoveCaretTo(_caret - Columns, _asciiPane); break;
                case Key.Down: MoveCaretTo(_caret + Columns, _asciiPane); break;
                case Key.PageUp: MoveCaretTo(_caret - page, _asciiPane); break;
                case Key.PageDown: MoveCaretTo(_caret + page, _asciiPane); break;
                case Key.Home: MoveCaretTo(ctrl ? 0 : _caret / Columns * Columns, _asciiPane); break;
                case Key.End: MoveCaretTo(ctrl ? _data.Count : Math.Min(_data.Count, _caret / Columns * Columns + Columns - 1), _asciiPane); break;
                case Key.Tab: MoveCaretTo(_caret, !_asciiPane); break;
                case Key.Insert: Do(new Edit(_caret, Array.Empty<byte>(), new byte[] { 0 })); MoveCaretTo(_caret, _asciiPane); break;
                case Key.Delete when _caret < _data.Count: Do(new Edit(_caret, new[] { _data[_caret] }, Array.Empty<byte>())); MoveCaretTo(_caret, _asciiPane); break;
                case Key.Back when _caret > 0: Do(new Edit(_caret - 1, new[] { _data[_caret - 1] }, Array.Empty<byte>())); MoveCaretTo(_caret - 1, _asciiPane); break;
                case Key.G when ctrl: _ = GoToOffsetDialog(); break;
                case Key.F when ctrl: Find(); break;
                case Key.Z when ctrl: Undo(); break;
                case Key.Y when ctrl: Redo(); break;
                default: return;
            }
            e.Handled = true;
        }

        internal void TypeText(string text)
        {
            foreach (char ch in text)
            {
                if (_asciiPane)
                {
                    if (ch < 32 || ch > 126) continue;
                    WriteByte((byte)ch);
                    MoveCaretTo(_caret + 1, true);
                }
                else
                {
                    int digit = Uri.IsHexDigit(ch) ? Convert.ToInt32(ch.ToString(), 16) : -1;
                    if (digit < 0) continue;
                    byte old = _caret < _data.Count ? _data[_caret] : (byte)0;
                    byte value = _lowNibble ? (byte)((old & 0xF0) | digit) : (byte)((digit << 4) | (old & 0x0F));
                    WriteByte(value);
                    if (_lowNibble) MoveCaretTo(_caret + 1, false);
                    else { _lowNibble = true; UpdateInfo(); _view.InvalidateVisual(); }
                }
            }
        }

        private void WriteByte(byte value)
        {
            if (_caret < _data.Count)
            {
                if (_data[_caret] == value) return;
                Do(new Edit(_caret, new[] { _data[_caret] }, new[] { value }));
            }
            else Do(new Edit(_caret, Array.Empty<byte>(), new[] { value }));   // at the end: append
        }

        // ---- searching, jumping -----------------------------------------------------------------------------------------------------------------

        private async System.Threading.Tasks.Task GoToOffsetDialog()
        {
            string? text = await Dialogs.Input(Dialogs.WindowOf(this), "Offset (hex like 1F40, or decimal with a leading #, like #8000):", "Go to offset", _caret.ToString("X"));
            if (text == null) return;
            text = text.Trim();
            bool ok = text.StartsWith('#') ? int.TryParse(text.AsSpan(1), out int dec) : int.TryParse(text.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out dec);
            if (!ok || dec < 0) { await Dialogs.Message(Dialogs.WindowOf(this), "That is not an offset.", "Go to offset"); return; }
            MoveCaretTo(Math.Min(dec, _data.Count), _asciiPane);
        }

        /// <summary>The bytes that a search text names: `"text"` (in quotes) is ASCII, anything else is hex bytes (`DE AD be ef`). Null if it is neither.</summary>
        public static byte[]? ParsePattern(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') return Encoding.Latin1.GetBytes(text.Substring(1, text.Length - 2));
            var digits = text.Where(c => !char.IsWhiteSpace(c) && c != ',').ToArray();
            if (digits.Length == 0 || digits.Length % 2 != 0 || !digits.All(Uri.IsHexDigit)) return null;
            return Enumerable.Range(0, digits.Length / 2).Select(i => Convert.ToByte(new string(digits, i * 2, 2), 16)).ToArray();
        }

        public void Find() => _ = FindDialog();

        private async System.Threading.Tasks.Task FindDialog()
        {
            string? text = await Dialogs.Input(Dialogs.WindowOf(this), "Bytes in hex (DE AD BE EF) or text in quotes (\"abc\"):", "Find", _lastSearch == null ? "" : string.Join(" ", _lastSearch.Select(b => b.ToString("X2"))));
            if (text == null) return;
            var pattern = ParsePattern(text);
            if (pattern == null) { await Dialogs.Message(Dialogs.WindowOf(this), "Write hex bytes like DE AD BE EF, or text in quotes like \"abc\".", "Find"); return; }
            _lastSearch = pattern;
            FindNext();
        }

        public void FindNext() => Search(+1);
        public void FindPrevious() => Search(-1);

        private void Search(int direction)
        {
            if (_lastSearch == null) { Find(); return; }
            int n = _lastSearch.Length;
            for (int step = 1; step <= _data.Count; step++)
            {
                int at = ((_caret + direction * step) % _data.Count + _data.Count) % _data.Count;
                if (at + n > _data.Count) continue;
                bool match = true;
                for (int i = 0; i < n && match; i++) match = _data[at + i] == _lastSearch[i];
                if (match) { MoveCaretTo(at, _asciiPane); return; }
            }
        }

        // ---- IDocumentView ----------------------------------------------------------------------------------------------------------------------

        public string GetText() => "";
        public int GetCaretLine() => _caret / Columns + 1;
        public void FocusEditor() => _view.Focus();
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public bool HasSelection => false;
        public int LineCount => RowCount;
        public void Cut() { }
        public void Copy() { }
        public void Paste() { }
        public void Delete() { if (_caret < _data.Count) { Do(new Edit(_caret, new[] { _data[_caret] }, Array.Empty<byte>())); MoveCaretTo(_caret, _asciiPane); } }
        public void SelectAll() { }
        public void GoToLine(int line) => MoveCaretTo((Math.Max(1, line) - 1) * Columns, _asciiPane);
        public void MarkSaved() => SetModified(false);

        public void Undo()
        {
            if (_undo.Count == 0) return;
            var edit = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _data.RemoveRange(edit.Offset, edit.Inserted.Length);
            _data.InsertRange(edit.Offset, edit.Removed);
            _redo.Add(edit);
            SetModified(true);
            MoveCaretTo(edit.Offset, _asciiPane);
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            var edit = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            Apply(edit);
            _undo.Add(edit);
            SetModified(true);
            MoveCaretTo(edit.Offset, _asciiPane);
        }

        public byte[] GetBytes() => _data.ToArray();

        /// <summary>Loads the file (`text` is not used); a file of more than <see cref="MaxFileSize"/> bytes is not opened.</summary>
        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            _data.Clear(); _undo.Clear(); _redo.Clear();
            _caret = 0; _topRow = 0; _lowNibble = false;
            if (filePath != null && File.Exists(filePath))
            {
                var info = new FileInfo(filePath);
                if (info.Length > MaxFileSize) _info.Text = $"This file is too big for the hex editor ({info.Length / (1024 * 1024)} MB; the limit is {MaxFileSize / (1024 * 1024)} MB).";
                else
                    try { _data.AddRange(File.ReadAllBytes(filePath)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _info.Text = ex.Message; }
            }
            SetModified(false);
            UpdateScrollBar();
            if (_data.Count > 0 || FilePath == null) UpdateInfo();
            _view.InvalidateVisual();
        }
    }
}
