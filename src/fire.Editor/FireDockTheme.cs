using System.Windows.Media;
using AvalonDock.Themes.VS;
using AvalonDock.Themes.VS.Themes;

namespace fire.Editor
{
    /// <summary>Das dunkle AvalonDock-Thema (VS2026 Dark) mit den Farben des Editors: das Hauptfenster samt Docking-Rahmen und
    /// Tab-Leisten ist <see cref="EditorTheme.ShellColor"/>, der Inhalt der Bereiche und die aktive Registerkarte
    /// <see cref="EditorTheme.PanelColor"/> (dieselbe Farbe wie der Editor). Die Farben werden direkt im Ressourcen-Wörterbuch des
    /// Themas ersetzt, damit sie auch in frei schwebenden Fenstern gelten (die bekommen das Wörterbuch des Themas, nicht das der
    /// Anwendung).</summary>
    public sealed class FireDockTheme : VS2026DarkTheme
    {
        public FireDockTheme()
        {
            var dict = ThemeResourceDictionary;
            var shell = EditorTheme.Solid(EditorTheme.ShellColor);
            var panel = EditorTheme.Solid(EditorTheme.PanelColor);
            var border = EditorTheme.Solid(EditorTheme.BorderColor);
            var header = EditorTheme.Solid(Color.FromRgb(0x2E, 0x29, 0x33)); // Titelleiste des aktiven Bereichs: etwas heller als der Rahmen
            var accent = EditorTheme.Solid(EditorTheme.AccentColor);

            // Rahmen hinter allen Bereichen, Teiler, Menüs
            dict[ResourceKeys.Background] = shell;
            dict[ResourceKeys.PanelBorderBrush] = border;

            // Inhalt der Bereiche (Dokument- und Werkzeug-Panes, automatisch ausgeblendete Bereiche)
            dict[ResourceKeys.TabBackground] = panel;

            // Registerkarten: nicht ausgewählte verschwinden im Rahmen, die ausgewählte geht in den Inhalt über
            dict[ResourceKeys.DocumentWellTabUnselectedBackground] = shell;
            dict[ResourceKeys.DocumentWellTabSelectedInactiveBackground] = panel;
            dict[ResourceKeys.ToolWindowTabUnselectedBackground] = shell;
            dict[ResourceKeys.ToolWindowTabSelectedInactiveBackground] = panel;
            dict[ResourceKeys.AutoHideTabDefaultBackground] = shell;

            // Titelleisten der Werkzeugfenster
            dict[ResourceKeys.ToolWindowCaptionActiveBackground] = header;
            dict[ResourceKeys.ToolWindowCaptionInactiveBackground] = shell;

            // Schwebende Fenster, Fensterwechsler
            dict[ResourceKeys.FloatingDocumentWindowBackground] = shell;
            dict[ResourceKeys.FloatingToolWindowBackground] = shell;
            dict[ResourceKeys.NavigatorWindowBackground] = panel;

            // Tab-Beschriftungen: Fokussiert (aktive Registerkarte) weiß, bei Hover und bei ausgewählten, aber nicht fokussierten
            // Werkzeug-Registerkarten die Akzentfarbe. Ausgewählte Dokument-Registerkarten ohne Fokus hell statt des dunklen Standards.
            var white = EditorTheme.Solid(Colors.White);
            var light = EditorTheme.Solid(EditorTheme.TextColor);
            var accentText = EditorTheme.Solid(EditorTheme.AccentTextColor); // #B70052 selbst wäre auf dunklem Grund zu dunkel
            dict[ResourceKeys.DocumentWellTabSelectedActiveText] = white;
            dict[ResourceKeys.DocumentWellTabSelectedInactiveText] = light;
            dict[ResourceKeys.DocumentWellTabUnselectedHoveredText] = accentText;
            dict[ResourceKeys.ToolWindowTabSelectedActiveText] = white;
            dict[ResourceKeys.ToolWindowTabSelectedInactiveText] = accentText;
            dict[ResourceKeys.ToolWindowTabUnselectedHoveredText] = accentText;

            // Akzentfarbe statt des Blaus des VS-Themas: aktive Registerkarten, Hover/Gedrückt-Zustände, Andock-Vorschau
            dict[ResourceKeys.ControlAccentColorKey] = EditorTheme.AccentColor;
            dict[ResourceKeys.ControlAccentBrushKey] = accent;
            dict[ResourceKeys.DocumentWellTabSelectedActiveBackground] = accent;
            dict[ResourceKeys.ToolWindowTabSelectedActiveBackground] = accent;
            dict[ResourceKeys.AutoHideTabHoveredBorder] = accent;
            dict[ResourceKeys.AutoHideTabHoveredText] = accentText;
            dict[ResourceKeys.DocumentWellOverflowButtonHoveredGlyph] = accent;
            dict[ResourceKeys.DocumentWellOverflowButtonPressedBackground] = accent;
            dict[ResourceKeys.DocumentWellOverflowButtonPressedBorder] = accent;
            dict[ResourceKeys.ToolWindowCaptionButtonActivePressedBackground] = accent;
            dict[ResourceKeys.ToolWindowCaptionButtonActivePressedBorder] = accent;
            dict[ResourceKeys.NavigatorWindowSelectedBackground] = accent;
            dict[ResourceKeys.DockingButtonForegroundBrushKey] = accent;
            dict[ResourceKeys.PreviewBoxBorderBrushKey] = accent;

            // Schließen-Schaltfläche der Dokument-Registerkarten: Hover/Gedrückt statt des Blaus des Themas.
            var accentHover = EditorTheme.Solid(Color.FromRgb(0xD6, 0x1F, 0x70));   // etwas heller als der Akzent, für die aktive (akzentfarbene) Registerkarte
            var accentPressed = EditorTheme.Solid(Color.FromRgb(0x8C, 0x00, 0x3F)); // etwas dunkler
            var neutralHover = EditorTheme.Solid(EditorTheme.BorderColor);          // für Registerkarten ohne Akzent-Hintergrund
            foreach (var (key, brush) in new (object, Brush)[]
            {
                (ResourceKeys.DocumentWellTabButtonSelectedActiveHoveredBackground, accentHover),
                (ResourceKeys.DocumentWellTabButtonSelectedActiveHoveredBorder, accentHover),
                (ResourceKeys.DocumentWellTabButtonSelectedActivePressedBackground, accentPressed),
                (ResourceKeys.DocumentWellTabButtonSelectedActivePressedBorder, accentPressed),
                (ResourceKeys.DocumentWellTabButtonSelectedInactiveHoveredBackground, neutralHover),
                (ResourceKeys.DocumentWellTabButtonSelectedInactiveHoveredBorder, neutralHover),
                (ResourceKeys.DocumentWellTabButtonSelectedInactivePressedBackground, accent),
                (ResourceKeys.DocumentWellTabButtonSelectedInactivePressedBorder, accent),
                (ResourceKeys.DocumentWellTabButtonUnselectedTabHoveredButtonHoveredBackground, neutralHover),
                (ResourceKeys.DocumentWellTabButtonUnselectedTabHoveredButtonHoveredBorder, neutralHover),
                (ResourceKeys.DocumentWellTabButtonUnselectedTabHoveredButtonPressedBackground, accent),
                (ResourceKeys.DocumentWellTabButtonUnselectedTabHoveredButtonPressedBorder, accent),
                (ResourceKeys.DocumentWellOverflowButtonHoveredBackground, neutralHover),
                (ResourceKeys.DocumentWellOverflowButtonHoveredBorder, neutralHover),
            })
                dict[key] = brush;
            foreach (var key in new object[]
            {
                ResourceKeys.DocumentWellTabButtonSelectedActiveHoveredGlyph, ResourceKeys.DocumentWellTabButtonSelectedActivePressedGlyph,
                ResourceKeys.DocumentWellTabButtonSelectedInactiveHoveredGlyph, ResourceKeys.DocumentWellTabButtonSelectedInactivePressedGlyph,
                ResourceKeys.DocumentWellTabButtonUnselectedTabHoveredButtonHoveredGlyph, ResourceKeys.DocumentWellTabButtonUnselectedTabHoveredButtonPressedGlyph,
            })
                dict[key] = white;

            var preview = new SolidColorBrush(EditorTheme.AccentColor) { Opacity = 0.5 };
            preview.Freeze();
            dict[ResourceKeys.PreviewBoxBackgroundBrushKey] = preview;
        }
    }
}
