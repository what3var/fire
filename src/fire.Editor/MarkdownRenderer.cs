using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace fire.Editor
{
    /// <summary>Baut aus dem Markdown-Modell (siehe MarkdownParser) ein
    /// FlowDocument für die Vorschau. Reines Darstellen: Links werden an
    /// `LinkClicked` gemeldet, der Host entscheidet, was damit passiert
    /// (Browser, andere Datei im Editor öffnen).</summary>
    internal sealed class MarkdownRenderer
    {
        private static readonly FontFamily CodeFont = new("Consolas");
        private static readonly Brush CodeBackground = EditorTheme.Background;
        private static readonly Brush InlineCodeBackground = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA)).AsFrozen();
        private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x66, 0xCC)).AsFrozen();
        private static readonly Brush QuoteBar = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)).AsFrozen();
        private static readonly Brush QuoteText = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)).AsFrozen();
        private static readonly Brush TableBorder = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)).AsFrozen();
        private static readonly Brush TableHeader = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)).AsFrozen();
        private static readonly double[] HeadingSizes = { 30, 24, 20, 17, 15, 14 };

        /// <summary>Verzeichnis des Dokuments (für relative Bildpfade).</summary>
        public string? BaseDirectory { get; set; }

        /// <summary>Wird aufgerufen, wenn ein Link angeklickt wurde (Ziel wie im Dokument geschrieben).</summary>
        public Action<string>? LinkClicked { get; set; }

        public FlowDocument Render(string markdown)
        {
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                PagePadding = new Thickness(18, 12, 18, 12),
                // Bei ColumnWidth = Auto bricht FlowDocument in mehrere Spalten um - hier soll es immer eine sein.
                ColumnWidth = double.PositiveInfinity,
                TextAlignment = TextAlignment.Left,
            };
            foreach (var block in MarkdownParser.Parse(markdown))
                AddBlock(doc.Blocks, block, 0);
            return doc;
        }

        private void AddBlock(BlockCollection target, MdBlock block, int listDepth)
        {
            switch (block)
            {
                case MdHeading h:
                {
                    var p = new Paragraph
                    {
                        FontSize = HeadingSizes[Math.Clamp(h.Level, 1, 6) - 1],
                        FontWeight = h.Level >= 5 ? FontWeights.SemiBold : FontWeights.Bold,
                        Margin = new Thickness(0, h.Level <= 2 ? 14 : 10, 0, 6),
                    };
                    if (h.Level <= 2)
                    {
                        p.BorderBrush = TableBorder;
                        p.BorderThickness = new Thickness(0, 0, 0, 1);
                        p.Padding = new Thickness(0, 0, 0, 3);
                    }
                    AddInlines(p.Inlines, h.Content);
                    target.Add(p);
                    break;
                }
                case MdParagraph para:
                {
                    var p = new Paragraph { Margin = listDepth > 0 ? new Thickness(0, 1, 0, 1) : new Thickness(0, 4, 0, 8) };
                    AddInlines(p.Inlines, para.Content);
                    target.Add(p);
                    break;
                }
                case MdCodeBlock code:
                    target.Add(RenderCodeBlock(code));
                    break;
                case MdQuote quote:
                {
                    var section = new Section
                    {
                        BorderBrush = QuoteBar,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(10, 0, 0, 0),
                        Margin = new Thickness(0, 4, 0, 8),
                        Foreground = QuoteText,
                    };
                    foreach (var inner in quote.Blocks) AddBlock(section.Blocks, inner, listDepth);
                    target.Add(section);
                    break;
                }
                case MdRule:
                    target.Add(new BlockUIContainer(new Border
                    {
                        Height = 1,
                        Background = TableBorder,
                        Margin = new Thickness(0, 8, 0, 8),
                    }));
                    break;
                case MdList list:
                {
                    bool allTasks = list.Items.Count > 0 && list.Items.All(i => i.Checked != null);
                    var fl = new List
                    {
                        MarkerStyle = allTasks ? TextMarkerStyle.None : list.Ordered ? TextMarkerStyle.Decimal : listDepth % 2 == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle,
                        StartIndex = list.Ordered ? list.Start : 1,
                        Margin = new Thickness(0, 2, 0, 6),
                        Padding = new Thickness(allTasks ? 6 : 24, 0, 0, 0),
                    };
                    foreach (var item in list.Items)
                    {
                        var li = new ListItem();
                        for (int k = 0; k < item.Blocks.Count; k++)
                        {
                            AddBlock(li.Blocks, item.Blocks[k], listDepth + 1);
                            if (k == 0 && item.Checked != null && li.Blocks.FirstBlock is Paragraph first)
                            {
                                var box = new Run(item.Checked == true ? "☑ " : "☐ ") { FontFamily = new FontFamily("Segoe UI Symbol") };
                                if (first.Inlines.FirstInline is { } firstInline) first.Inlines.InsertBefore(firstInline, box);
                                else first.Inlines.Add(box);
                            }
                        }
                        fl.ListItems.Add(li);
                    }
                    target.Add(fl);
                    break;
                }
                case MdTable table:
                    target.Add(RenderTable(table));
                    break;
            }
        }

        private Block RenderCodeBlock(MdCodeBlock code)
        {
            var p = new Paragraph
            {
                FontFamily = CodeFont,
                FontSize = 13,
                Background = CodeBackground,
                Foreground = EditorTheme.Text,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 4, 0, 8),
                LineHeight = double.NaN,
            };

            // Fire-Quelltext mit demselben Lexer einfärben wie im Editor.
            bool isFire = code.Language != null &&
                (code.Language.Equals("fire", StringComparison.OrdinalIgnoreCase) || code.Language.Equals("firescript", StringComparison.OrdinalIgnoreCase));
            if (isFire)
            {
                int pos = 0;
                foreach (var span in SyntaxHighlighter.Highlight(code.Code))
                {
                    if (span.Start > pos) p.Inlines.Add(new Run(code.Code.Substring(pos, span.Start - pos)));
                    p.Inlines.Add(new Run(code.Code.Substring(span.Start, span.Length)) { Foreground = HighlightingColorizer.BrushFor(span.Category) });
                    pos = span.Start + span.Length;
                }
                if (pos < code.Code.Length) p.Inlines.Add(new Run(code.Code.Substring(pos)));
            }
            else
            {
                p.Inlines.Add(new Run(code.Code));
            }
            return p;
        }

        private Block RenderTable(MdTable md)
        {
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8), BorderBrush = TableBorder, BorderThickness = new Thickness(1, 1, 0, 0) };
            for (int c = 0; c < md.Header.Count; c++) table.Columns.Add(new TableColumn());
            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            TableRow Row(IReadOnlyList<IReadOnlyList<MdInline>> cells, bool header)
            {
                var row = new TableRow();
                if (header) row.Background = TableHeader;
                for (int c = 0; c < cells.Count; c++)
                {
                    var p = new Paragraph { Margin = new Thickness(0), TextAlignment = md.Aligns[c] switch { MdAlign.Center => TextAlignment.Center, MdAlign.Right => TextAlignment.Right, _ => TextAlignment.Left } };
                    if (header) p.FontWeight = FontWeights.Bold;
                    AddInlines(p.Inlines, cells[c]);
                    row.Cells.Add(new TableCell(p) { Padding = new Thickness(6, 3, 6, 3), BorderBrush = TableBorder, BorderThickness = new Thickness(0, 0, 1, 1) });
                }
                return row;
            }

            group.Rows.Add(Row(md.Header, true));
            foreach (var r in md.Rows) group.Rows.Add(Row(r, false));
            return table;
        }

        private void AddInlines(InlineCollection target, IEnumerable<MdInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case MdText t:
                        target.Add(new Run(t.Text));
                        break;
                    case MdBold b:
                    {
                        var span = new Bold();
                        AddInlines(span.Inlines, b.Content);
                        target.Add(span);
                        break;
                    }
                    case MdItalic i:
                    {
                        var span = new Italic();
                        AddInlines(span.Inlines, i.Content);
                        target.Add(span);
                        break;
                    }
                    case MdStrike s:
                    {
                        var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                        AddInlines(span.Inlines, s.Content);
                        target.Add(span);
                        break;
                    }
                    case MdCode c:
                        target.Add(new Run(c.Code) { FontFamily = CodeFont, FontSize = 13, Background = InlineCodeBackground });
                        break;
                    case MdLink l:
                    {
                        var link = new Hyperlink { Foreground = LinkBrush, ToolTip = l.Url };
                        AddInlines(link.Inlines, l.Content);
                        string url = l.Url;
                        link.Click += (_, _) => LinkClicked?.Invoke(url);
                        target.Add(link);
                        break;
                    }
                    case MdImage img:
                        target.Add(RenderImage(img));
                        break;
                    case MdLineBreak:
                        target.Add(new LineBreak());
                        break;
                }
            }
        }

        private Inline RenderImage(MdImage img)
        {
            // Nur lokale Dateien (relativ zum Dokument): ein Markdown-Dokument soll beim bloßen Ansehen
            // nicht selbständig Netzwerkzugriffe auslösen.
            try
            {
                string path = img.Url;
                if (!Path.IsPathRooted(path) && BaseDirectory != null) path = Path.Combine(BaseDirectory, path);
                if (!img.Url.Contains("://") && File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // Datei nicht gesperrt halten
                    bmp.UriSource = new Uri(Path.GetFullPath(path));
                    bmp.EndInit();
                    bmp.Freeze();
                    return new InlineUIContainer(new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 700, ToolTip = img.Alt });
                }
            }
            catch (Exception)
            {
                // beschädigtes Bild o.Ä.: unten als Text anzeigen
            }
            return new Run($"[Bild: {(img.Alt.Length > 0 ? img.Alt : img.Url)}]") { Foreground = QuoteText, FontStyle = FontStyles.Italic };
        }
    }
}
