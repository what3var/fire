using System;
using System.Collections.Generic;
using Avalonia.Controls;

using Avalonia.Media;
using Avalonia.Media.Imaging;
using fire.Projects;

namespace fire.Editor
{
    /// <summary>The icon of a template: the picture of its folder, else a drawn symbol for the key of its `template.json` ("script", "window", "project", ...; the default is a sheet of paper).</summary>
    internal static class TemplateIcon
    {
        // symbols on a grid of 24 x 24, drawn as lines
        private static readonly Dictionary<string, string> Shapes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["file"] = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19",
            ["script"] = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19 M9,13 H16 M9,17 H16",
            ["class"] = "M9,4 C7,4 7,6 7,8 C7,10 6,12 4,12 C6,12 7,14 7,16 C7,18 7,20 9,20 M15,4 C17,4 17,6 17,8 C17,10 18,12 20,12 C18,12 17,14 17,16 C17,18 17,20 15,20",
            ["window"] = "M3,4 H21 V20 H3 Z M3,8 H21 M5.5,6 H6.5",
            ["view"] = "M4,4 H20 V20 H4 Z M8,9 H16 M8,13 H16 M8,17 H12",
            ["project"] = "M3,6 H10 L12,8 H21 V19 H3 Z",
            ["console"] = "M3,4 H21 V20 H3 Z M7,9 L10,12 L7,15 M12,15 H17",
            ["desktop"] = "M3,4 H21 V16 H3 Z M9,20 H15 M12,16 V20",
            ["library"] = "M4,4 V20 M8,4 V20 M12,6 L16,5 L19,19 L15,20 Z",
            ["native"] = "M7,7 H17 V17 H7 Z M10,4 V7 M14,4 V7 M10,17 V20 M14,17 V20 M4,10 H7 M4,14 H7 M17,10 H20 M17,14 H20",
            ["empty"] = "M4,4 H20 V20 H4 Z",
            ["markdown"] = "M3,6 H21 V18 H3 Z M6,15 V9 L9,12 L12,9 V15 M16,9 V15 M14,13 L16,15 L18,13",
            ["image"] = "M3,4 H21 V20 H3 Z M3,16 L9,10 L14,15 L17,12 L21,16",
            ["package"] = "M12,3 L20,7 V17 L12,21 L4,17 V7 Z M4,7 L12,11 L20,7 M12,11 V21",
        };

        public static Control Create(FireTemplate template, double size = 32)
        {
            if (template.IconPath != null)
            {
                try { return new Image { Source = new Bitmap(template.IconPath), Width = size, Height = size }; }
                catch (Exception) { /* a picture that cannot be read: the symbol instead */ }
            }
            return Create(template.IconKey, size);
        }

        public static Control Create(string key, double size = 32)
        {
            string data = Shapes.TryGetValue(key, out var d) ? d : Shapes["file"];
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = EditorTheme.Magenta,
                StrokeThickness = 1.6,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
            };
            if (string.Equals(key, "empty", StringComparison.OrdinalIgnoreCase)) path.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 3 };
            return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
        }
    }
}
