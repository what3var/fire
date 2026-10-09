using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace fire.Editor
{
    /// <summary>Builds the preview from the Markdown model (see MarkdownParser): a tree of Avalonia controls (headings, paragraphs and code as selectable text blocks, lists, tables,
    /// images). Pure display: links are reported to `LinkClicked`, the host decides what happens with them (browser, open another file in the editor).</summary>
    internal sealed class MarkdownRenderer
    {
        private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
        // Dark page: the colors come from EditorTheme (page = editor background).
        private static readonly IBrush CodeBackground = EditorTheme.CodeBlockBackground;
        private static readonly IBrush LinkBrush = EditorTheme.Solid(EditorTheme.AccentTextColor);
        private static readonly IBrush QuoteBar = EditorTheme.Border;
        private static readonly IBrush QuoteText = EditorTheme.TextDim;
        private static readonly IBrush TableBorder = EditorTheme.Border;
        private static readonly IBrush TableHeader = EditorTheme.CodeBlockBackground;
        private readonly HashSet<string> _usedAnchors = new();
        private static readonly double[] HeadingSizes = { 30, 24, 20, 17, 15, 14 };

        /// <summary>Directory of the document (for relative image paths).</summary>
        public string? BaseDirectory { get; set; }

        /// <summary>Called when a link was clicked (target as written in the document).</summary>
        public Action<string, KeyModifiers>? LinkClicked { get; set; }

        /// <summary>Heading blocks of the last rendered document by anchor (see <see cref="MdAnchors"/>), for links like `file.md#section`.</summary>
        public Dictionary<string, Control> Anchors { get; } = new();

        public Control Render(string markdown)
        {
            Anchors.Clear();
            _usedAnchors.Clear();
            var page = new StackPanel { Margin = new Thickness(18, 12, 18, 12) };
            foreach (var block in MarkdownParser.Parse(markdown))
                AddBlock(page.Children, block, 0, EditorTheme.Text);
            return page;
        }

        /// <summary>A text block that can be selected and that reports clicks on the links in it.</summary>
        private TextBlock NewText(IBrush foreground, out List<(int Start, int End, string Url)> links)
        {
            var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = foreground, FontSize = 14 };
            var found = links = new List<(int, int, string)>();
            string? UrlAt(PointerEventArgs e)
            {
                if (found.Count == 0 || text.TextLayout == null) return null;
                int index = text.TextLayout.HitTestPoint(e.GetPosition(text)).TextPosition;
                foreach (var (start, end, url) in found) if (index >= start && index < end) return url;
                return null;
            }
            text.PointerMoved += (_, e) => text.Cursor = UrlAt(e) != null ? new Cursor(StandardCursorType.Hand) : null;
            text.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(text).Properties.IsLeftButtonPressed) return;
                if (UrlAt(e) is { } url) { LinkClicked?.Invoke(url, e.KeyModifiers); e.Handled = true; }
            };
            return text;
        }

        private void AddBlock(Avalonia.Controls.Controls target, MdBlock block, int listDepth, IBrush foreground)
        {
            switch (block)
            {
                case MdHeading h:
                {
                    var text = NewText(foreground, out var links);
                    text.FontSize = HeadingSizes[Math.Clamp(h.Level, 1, 6) - 1];
                    text.FontWeight = h.Level >= 5 ? FontWeight.SemiBold : FontWeight.Bold;
                    AddInlines(text.Inlines!, h.Content, links);
                    Control element = text;
                    var margin = new Thickness(0, h.Level <= 2 ? 14 : 10, 0, 6);
                    if (h.Level <= 2)
                        element = new Border { BorderBrush = TableBorder, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 3), Child = text };
                    element.Margin = margin;
                    Anchors[MdAnchors.Unique(MarkdownParser.PlainText(h.Content), _usedAnchors)] = element;
                    target.Add(element);
                    break;
                }
                case MdParagraph para:
                {
                    var text = NewText(foreground, out var links);
                    text.Margin = listDepth > 0 ? new Thickness(0, 1, 0, 1) : new Thickness(0, 4, 0, 8);
                    AddInlines(text.Inlines!, para.Content, links);
                    target.Add(text);
                    break;
                }
                case MdCodeBlock code:
                    target.Add(RenderCodeBlock(code));
                    break;
                case MdQuote quote:
                {
                    var inner = new StackPanel();
                    foreach (var b in quote.Blocks) AddBlock(inner.Children, b, listDepth, QuoteText);
                    target.Add(new Border
                    {
                        BorderBrush = QuoteBar,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(10, 0, 0, 0),
                        Margin = new Thickness(0, 4, 0, 8),
                        Child = inner,
                    });
                    break;
                }
                case MdRule:
                    target.Add(new Border { Height = 1, Background = TableBorder, Margin = new Thickness(0, 8, 0, 8) });
                    break;
                case MdList list:
                {
                    bool allTasks = list.Items.Count > 0 && list.Items.All(i => i.Checked != null);
                    var items = new StackPanel { Margin = new Thickness(allTasks ? 6 : 12, 2, 0, 6) };
                    int number = list.Ordered ? list.Start : 1;
                    foreach (var item in list.Items)
                    {
                        string marker = item.Checked != null ? (item.Checked == true ? "☑" : "☐")
                            : list.Ordered ? number + "." : listDepth % 2 == 0 ? "•" : "◦";
                        number++;
                        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                        row.Children.Add(new TextBlock { Text = marker, Foreground = foreground, FontSize = 14, Margin = new Thickness(0, 1, 8, 1), MinWidth = 16, VerticalAlignment = VerticalAlignment.Top });
                        var content = new StackPanel();
                        foreach (var b in item.Blocks) AddBlock(content.Children, b, listDepth + 1, foreground);
                        Grid.SetColumn(content, 1);
                        row.Children.Add(content);
                        items.Children.Add(row);
                    }
                    target.Add(items);
                    break;
                }
                case MdTable table:
                    target.Add(RenderTable(table, foreground));
                    break;
            }
        }

        private Control RenderCodeBlock(MdCodeBlock code)
        {
            var text = new SelectableTextBlock { FontFamily = CodeFont, FontSize = 13, Foreground = EditorTheme.Text, TextWrapping = TextWrapping.NoWrap };

            // Colour fire source with the same lexer as in the editor.
            bool isFire = code.Language != null &&
                (code.Language.Equals("fire", StringComparison.OrdinalIgnoreCase) || code.Language.Equals("firescript", StringComparison.OrdinalIgnoreCase));
            var inlines = text.Inlines!;
            if (isFire)
            {
                int pos = 0;
                foreach (var span in SyntaxHighlighter.Highlight(code.Code))
                {
                    if (span.Start > pos) inlines.Add(new Run(code.Code.Substring(pos, span.Start - pos)));
                    inlines.Add(new Run(code.Code.Substring(span.Start, span.Length)) { Foreground = HighlightingColorizer.BrushFor(span.Category) });
                    pos = span.Start + span.Length;
                }
                if (pos < code.Code.Length) inlines.Add(new Run(code.Code.Substring(pos)));
            }
            else
            {
                inlines.Add(new Run(code.Code));
            }
            return new Border
            {
                Background = CodeBackground,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 4, 0, 8),
                Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = text },
            };
        }

        private Control RenderTable(MdTable md, IBrush foreground)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 8) };
            int columns = md.Header.Count;
            for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

            void Row(IReadOnlyList<IReadOnlyList<MdInline>> cells, bool header, int rowIndex)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int c = 0; c < Math.Min(cells.Count, columns); c++)
                {
                    var text = NewText(foreground, out var links);
                    text.TextAlignment = md.Aligns[c] switch { MdAlign.Center => TextAlignment.Center, MdAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
                    if (header) text.FontWeight = FontWeight.Bold;
                    AddInlines(text.Inlines!, cells[c], links);
                    var cell = new Border
                    {
                        Padding = new Thickness(6, 3, 6, 3),
                        BorderBrush = TableBorder,
                        BorderThickness = new Thickness(1, rowIndex == 0 ? 1 : 0, c == columns - 1 ? 1 : 0, 1),
                        Background = header ? TableHeader : null,
                        Child = text,
                    };
                    Grid.SetRow(cell, rowIndex);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }

            Row(md.Header, true, 0);
            for (int r = 0; r < md.Rows.Count; r++) Row(md.Rows[r], false, r + 1);
            return grid;
        }

        /// <summary>Adds the inlines; `links` collects the character range of every link in the text block (to find the one that was clicked).</summary>
        private void AddInlines(InlineCollection target, IEnumerable<MdInline> inlines, List<(int Start, int End, string Url)> links)
        {
            int position = 0;
            foreach (var inline in inlines) position = AddInline(target, inline, links, position);
        }

        private int AddInline(InlineCollection target, MdInline inline, List<(int Start, int End, string Url)> links, int position)
        {
            switch (inline)
            {
                case MdText t:
                    target.Add(new Run(t.Text));
                    return position + t.Text.Length;
                case MdBold b:
                {
                    var span = new Bold();
                    position = AddChildren(span.Inlines, b.Content, links, position);
                    target.Add(span);
                    return position;
                }
                case MdItalic i:
                {
                    var span = new Italic();
                    position = AddChildren(span.Inlines, i.Content, links, position);
                    target.Add(span);
                    return position;
                }
                case MdStrike s:
                {
                    var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                    position = AddChildren(span.Inlines, s.Content, links, position);
                    target.Add(span);
                    return position;
                }
                case MdCode c:
                    target.Add(new Run(c.Code) { FontFamily = CodeFont, FontSize = 13, Background = CodeBackground, Foreground = EditorTheme.Text });
                    return position + c.Code.Length;
                case MdLink l:
                {
                    var span = new Span { Foreground = LinkBrush, TextDecorations = TextDecorations.Underline };
                    int start = position;
                    position = AddChildren(span.Inlines, l.Content, links, position);
                    links.Add((start, position, l.Url));
                    target.Add(span);
                    return position;
                }
                case MdImage img:
                    target.Add(RenderImage(img));
                    return position + 1;
                case MdLineBreak:
                    target.Add(new LineBreak());
                    return position + 1;
            }
            return position;
        }

        private int AddChildren(InlineCollection target, IEnumerable<MdInline> children, List<(int Start, int End, string Url)> links, int position)
        {
            foreach (var child in children) position = AddInline(target, child, links, position);
            return position;
        }

        private Inline RenderImage(MdImage img)
        {
            // Local files only (relative to the document): a Markdown document should not trigger network access on its own when merely viewed.
            try
            {
                string path = img.Url;
                if (!Path.IsPathRooted(path) && BaseDirectory != null) path = Path.Combine(BaseDirectory, path);
                if (!img.Url.Contains("://") && File.Exists(path))
                {
                    using var stream = File.OpenRead(Path.GetFullPath(path));   // read completely: the file is not kept locked
                    var bitmap = new Bitmap(stream);
                    var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 700 };
                    ToolTip.SetTip(image, img.Alt);
                    return new InlineUIContainer(image);
                }
            }
            catch (Exception)
            {
                // damaged image or similar: show as text below
            }
            return new Run($"[Image: {(img.Alt.Length > 0 ? img.Alt : img.Url)}]") { Foreground = QuoteText, FontStyle = FontStyle.Italic };
        }
    }
}
