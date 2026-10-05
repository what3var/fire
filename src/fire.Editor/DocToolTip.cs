using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace fire.Editor
{
    /// <summary>Builds the content of the documentation tooltips (completion list, caret, mouse hover): a header line
    /// with the symbol, then the summary, the parameters and the return value of its `///` comment. The content brings
    /// its own dark background, so it is readable inside whatever frame the tooltip control draws around it.</summary>
    internal static class DocToolTip
    {
        public static FrameworkElement Build(string? header, DocComment doc)
        {
            var panel = new StackPanel { MaxWidth = 520 };

            if (!string.IsNullOrEmpty(header))
                panel.Children.Add(new TextBlock
                {
                    Text = header,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = EditorTheme.Text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, doc.IsEmpty ? 0 : 5),
                });

            if (doc.Summary.Length > 0)
                panel.Children.Add(Paragraph(doc.Summary, EditorTheme.Text, new Thickness(0, 0, 0, 4)));

            foreach (var (name, text) in doc.Parameters)
            {
                var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.Text, Margin = new Thickness(0, 1, 0, 1) };
                block.Inlines.Add(new Run(name) { FontFamily = new FontFamily("Consolas"), Foreground = EditorTheme.Orange, FontWeight = FontWeights.SemiBold });
                block.Inlines.Add(new Run(" – " + text));
                panel.Children.Add(block);
            }

            if (!string.IsNullOrEmpty(doc.Returns))
            {
                var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.Text, Margin = new Thickness(0, 3, 0, 0) };
                block.Inlines.Add(new Run("Returns: ") { Foreground = EditorTheme.Magenta, FontWeight = FontWeights.SemiBold });
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

        private static TextBlock Paragraph(string text, Brush foreground, Thickness margin) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = foreground, Margin = margin };

        /// <summary>A ready-to-show tooltip control around <see cref="Build"/> (the tooltip chrome itself is kept minimal).</summary>
        public static System.Windows.Controls.ToolTip Create(string? header, DocComment doc) => new()
        {
            Content = Build(header, doc),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };
    }
}
