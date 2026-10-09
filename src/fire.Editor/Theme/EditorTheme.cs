using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit;
using AvaloniaEdit.Search;

namespace fire.Editor
{
    /// <summary>The dark colour scheme of all editor windows (script editor, Markdown editor, file view) in ONE place. Background and text are neutral dark or almost white;
    /// the special elements carry the hues of a fire (as in the program logo): magenta, purple (towards magenta), wine red, orange and yellow.</summary>
    internal static class EditorTheme
    {
        private static IBrush Solid(byte r, byte g, byte b, byte a = 255) => Solid(Color.FromArgb(a, r, g, b));

        internal static IBrush Solid(Color color) => new ImmutableSolidColorBrush(color);

        // ---- Surface and text ----
        public static readonly Color PanelColor = Color.FromRgb(0x24, 0x20, 0x28);
        public static readonly Color ShellColor = Color.FromRgb(0x1B, 0x18, 0x1E);
        public static readonly Color BorderColor = Color.FromRgb(0x3C, 0x35, 0x44);
        /// <summary>Accent colour of the interface (docking tabs, active buttons, focus border of the text fields) - the same in App.axaml.</summary>
        public static readonly Color AccentColor = Color.FromRgb(0xB7, 0x00, 0x52);
        /// <summary>Lightened accent colour (same hue) for text on a dark background, where #B70052 itself would be too dark.</summary>
        public static readonly Color AccentTextColor = Color.FromRgb(0xFF, 0x47, 0x9A);

        /// <summary>Background of the editor and of all contents of the areas (text fields, lists, tables, trees).</summary>
        public static readonly IBrush Background = Solid(PanelColor);
        /// <summary>Somewhat darker than the editor: the border around the areas (main window, docking), header rows, popups.</summary>
        public static readonly IBrush DarkSurface = Solid(ShellColor);
        public static readonly IBrush Border = Solid(BorderColor);
        public static readonly Color TextColor = Color.FromRgb(0xF4, 0xF0, 0xF2);
        public static readonly IBrush Text = Solid(TextColor);
        public static readonly IBrush TextDim = Solid(0xA0, 0x94, 0xA8);
        public static readonly IBrush LineNumber = Solid(0x7E, 0x73, 0x88);

        // ---- Feuerfarben ----
        public static readonly IBrush Magenta = Solid(0xF2, 0x47, 0x9E);
        public static readonly IBrush Purple = Solid(0xC0, 0x6A, 0xDE);
        public static readonly IBrush WineRed = Solid(0xE5, 0x56, 0x6F);
        public static readonly IBrush Orange = Solid(0xFF, 0x91, 0x42);
        public static readonly IBrush Yellow = Solid(0xF9, 0xCB, 0x5C);
        public static readonly IBrush Comment = Solid(0x8B, 0x7F, 0x93);

        // ---- States in the editor ----
        /// <summary>Wine-red dots in the breakpoint margin.</summary>
        public static readonly IBrush BreakpointDot = Solid(0xD6, 0x2F, 0x4B);
        public static readonly IBrush Selection = Solid(0xD6, 0x3C, 0x8C, 0x70);
        public static readonly IBrush SearchMarker = Solid(0xFF, 0x91, 0x42, 0x70);
        public static readonly IBrush CurrentDebugLine = Solid(0xF9, 0xCB, 0x5C, 0x50);
        public static readonly IBrush BreakpointLine = Solid(0xB0, 0x20, 0x45, 0x70);
        public static readonly IBrush ErrorMark = Solid(0xE8, 0x3A, 0x3A);

        // ---- Markdown ----
        public static readonly IBrush CodeBlockBackground = Solid(0x2E, 0x29, 0x34);

        /// <summary>Applies the scheme to an editor (and to its search bar, if present).</summary>
        public static void Apply(TextEditor editor, SearchPanel? searchPanel = null)
        {
            editor.Background = Background;
            editor.Foreground = Text;
            editor.LineNumbersForeground = LineNumber;

            var area = editor.TextArea;
            area.SelectionBrush = Selection;
            area.SelectionForeground = null; // the syntax colours stay visible even in the selection
            area.SelectionBorder = new Pen(Magenta, 1);
            area.SelectionCornerRadius = 2;

            searchPanel?.SetSearchResultsBrush(SearchMarker);
        }
    }
}
