using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>Builds the content of the documentation tooltips (completion list, caret, mouse hover): a header line with the symbol, then the summary, the parameters and the return
    /// value of its `///` comment. The content brings its own dark background, so it is readable inside whatever frame the tooltip draws around it.</summary>
    internal static class DocToolTip
    {
        private static readonly FontFamily Mono = new("Consolas, Menlo, DejaVu Sans Mono, monospace");

        public static Control Build(string? header, DocComment doc)
        {
            var panel = new StackPanel { MaxWidth = 520 };

            if (!string.IsNullOrEmpty(header))
                panel.Children.Add(new TextBlock
                {
                    Text = header,
                    FontFamily = Mono,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = EditorTheme.Text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, doc.IsEmpty ? 0 : 5),
                });

            if (doc.Summary.Length > 0)
                panel.Children.Add(Paragraph(doc.Summary, EditorTheme.Text, new Thickness(0, 0, 0, 4)));

            foreach (var (name, text) in doc.Parameters)
            {
                var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.Text, Margin = new Thickness(0, 1, 0, 1) };
                block.Inlines!.Add(new Run(name) { FontFamily = Mono, Foreground = EditorTheme.Orange, FontWeight = FontWeight.SemiBold });
                block.Inlines.Add(new Run(" – " + text));
                panel.Children.Add(block);
            }

            if (!string.IsNullOrEmpty(doc.Returns))
            {
                var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.Text, Margin = new Thickness(0, 3, 0, 0) };
                block.Inlines!.Add(new Run("Returns: ") { Foreground = EditorTheme.Magenta, FontWeight = FontWeight.SemiBold });
                block.Inlines.Add(new Run(doc.Returns));
                panel.Children.Add(block);
            }

            if (!string.IsNullOrEmpty(doc.Remarks))
                panel.Children.Add(Paragraph(doc.Remarks, EditorTheme.TextDim, new Thickness(0, 5, 0, 0)));

            return new Border
            {
                Background = EditorTheme.DarkSurface,
                BorderBrush = EditorTheme.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = panel,
            };
        }

        private static TextBlock Paragraph(string text, IBrush foreground, Thickness margin) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = foreground, Margin = margin };

        /// <summary>A popup (not taking the focus) around <see cref="Build"/>: the tooltip that stays open until the host closes it.</summary>
        public static Popup Create(string? header, DocComment doc) => new()
        {
            Child = Build(header, doc),
            IsLightDismissEnabled = false,
            Focusable = false,
        };
    }
}
