using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit;
using AvaloniaEdit.Search;

namespace fire.Editor
{
    /// <summary>Das dunkle Farbschema aller Editor-Fenster (Skript-Editor, Markdown-Editor, Datei-Ansicht) an EINER Stelle. Hintergrund und Text sind neutral dunkel bzw. fast weiß;
    /// die besonderen Elemente tragen die Farbtöne eines Feuers (wie im Programmlogo): Magenta, Lila (Richtung Magenta), Weinrot, Orange und Gelb.</summary>
    internal static class EditorTheme
    {
        private static IBrush Solid(byte r, byte g, byte b, byte a = 255) => Solid(Color.FromArgb(a, r, g, b));

        internal static IBrush Solid(Color color) => new ImmutableSolidColorBrush(color);

        // ---- Fläche und Text ----
        public static readonly Color PanelColor = Color.FromRgb(0x24, 0x20, 0x28);
        public static readonly Color ShellColor = Color.FromRgb(0x1B, 0x18, 0x1E);
        public static readonly Color BorderColor = Color.FromRgb(0x3C, 0x35, 0x44);
        /// <summary>Akzentfarbe der Oberfläche (Docking-Tabs, aktive Knöpfe, Fokusrand der Textfelder) - in App.axaml dieselbe.</summary>
        public static readonly Color AccentColor = Color.FromRgb(0xB7, 0x00, 0x52);
        /// <summary>Aufgehellte Akzentfarbe (gleicher Farbton) für Schrift auf dunklem Grund, wo #B70052 selbst zu dunkel wäre.</summary>
        public static readonly Color AccentTextColor = Color.FromRgb(0xFF, 0x47, 0x9A);

        /// <summary>Hintergrund des Editors und aller Inhalte der Bereiche (Textfelder, Listen, Tabellen, Bäume).</summary>
        public static readonly IBrush Background = Solid(PanelColor);
        /// <summary>Etwas dunkler als der Editor: der Rahmen um die Bereiche (Hauptfenster, Docking), Kopfzeilen, Popups.</summary>
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

        // ---- Zustände im Editor ----
        /// <summary>Weinrote Punkte im Haltepunkt-Rand.</summary>
        public static readonly IBrush BreakpointDot = Solid(0xD6, 0x2F, 0x4B);
        public static readonly IBrush Selection = Solid(0xD6, 0x3C, 0x8C, 0x70);
        public static readonly IBrush SearchMarker = Solid(0xFF, 0x91, 0x42, 0x70);
        public static readonly IBrush CurrentDebugLine = Solid(0xF9, 0xCB, 0x5C, 0x50);
        public static readonly IBrush BreakpointLine = Solid(0xB0, 0x20, 0x45, 0x70);
        public static readonly IBrush ErrorMark = Solid(0xE8, 0x3A, 0x3A);

        // ---- Markdown ----
        public static readonly IBrush CodeBlockBackground = Solid(0x2E, 0x29, 0x34);

        /// <summary>Wendet das Schema auf einen Editor an (und auf dessen Suchleiste, falls vorhanden).</summary>
        public static void Apply(TextEditor editor, SearchPanel? searchPanel = null)
        {
            editor.Background = Background;
            editor.Foreground = Text;
            editor.LineNumbersForeground = LineNumber;

            var area = editor.TextArea;
            area.SelectionBrush = Selection;
            area.SelectionForeground = null; // die Syntaxfarben bleiben auch in der Markierung sichtbar
            area.SelectionBorder = new Pen(Magenta, 1);
            area.SelectionCornerRadius = 2;

            searchPanel?.SetSearchResultsBrush(SearchMarker);
        }
    }
}
