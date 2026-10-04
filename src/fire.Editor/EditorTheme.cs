using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Search;

namespace fire.Editor
{
    /// <summary>Das dunkle Farbschema aller AvalonEdit-Fenster (Skript-Editor, Markdown-Editor,
    /// Datei-Ansicht) an EINER Stelle. Hintergrund und Text sind neutral dunkel bzw. fast weiß;
    /// die besonderen Elemente tragen die Farbtöne eines Feuers (wie im Programmlogo): Magenta,
    /// Lila (Richtung Magenta), Weinrot, Orange und Gelb.</summary>
    internal static class EditorTheme
    {
        private static Brush Solid(byte r, byte g, byte b, byte a = 255)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        // ---- Fläche und Text ----
        public static readonly Brush Background = Solid(0x24, 0x20, 0x28);
        /// <summary>Etwas dunkler als der Editor: Kopfzeilen, Code-Blöcke in der Vorschau.</summary>
        public static readonly Brush DarkSurface = Solid(0x1B, 0x18, 0x1F);
        public static readonly Brush Border = Solid(0x3C, 0x35, 0x44);
        public static readonly Brush Text = Solid(0xF4, 0xF0, 0xF2);
        public static readonly Brush TextDim = Solid(0xA0, 0x94, 0xA8);
        public static readonly Brush LineNumber = Solid(0x7E, 0x73, 0x88);

        // ---- Feuerfarben ----
        public static readonly Brush Magenta = Solid(0xF2, 0x47, 0x9E);
        public static readonly Brush Purple = Solid(0xC0, 0x6A, 0xDE);
        public static readonly Brush WineRed = Solid(0xE5, 0x56, 0x6F);
        public static readonly Brush Orange = Solid(0xFF, 0x91, 0x42);
        public static readonly Brush Yellow = Solid(0xF9, 0xCB, 0x5C);
        public static readonly Brush Comment = Solid(0x8B, 0x7F, 0x93);

        // ---- Zustände im Editor ----
        /// <summary>Weinrote Punkte im Haltepunkt-Rand.</summary>
        public static readonly Brush BreakpointDot = Solid(0xD6, 0x2F, 0x4B);
        public static readonly Brush Selection = Solid(0xD6, 0x3C, 0x8C, 0x70);
        public static readonly Brush SearchMarker = Solid(0xFF, 0x91, 0x42, 0x70);
        public static readonly Brush CurrentDebugLine = Solid(0xF9, 0xCB, 0x5C, 0x50);
        public static readonly Brush BreakpointLine = Solid(0xB0, 0x20, 0x45, 0x70);

        // ---- Markdown ----
        public static readonly Brush CodeBlockBackground = Solid(0x2E, 0x29, 0x34);

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

            if (searchPanel != null) searchPanel.MarkerBrush = SearchMarker;
        }
    }
}
