using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Controls.Presenters;

namespace fire.Editor
{
    /// <summary>
    /// The pieces of the settings windows (Project Properties, Toolchain Settings): pages chosen in a list at the left, groups with a small accent title, rows with a label,
    /// hints, and buttons side by side for a choice of a few values. One look for all settings.
    /// </summary>
    internal static class SettingsLayout
    {
        public static readonly IBrush Accent = new SolidColorBrush(EditorTheme.AccentColor);
        public static readonly IBrush AccentText = new SolidColorBrush(EditorTheme.AccentTextColor);
        public static readonly IBrush NavSelected = new SolidColorBrush(Color.FromRgb(0x3A, 0x21, 0x31));
        public static readonly IBrush Field = new SolidColorBrush(Color.FromRgb(0x2E, 0x29, 0x34));
        public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

        /// <summary>The icons of the lists at the left (path data in a 14x14 box, drawn with a line: the glyphs of the fonts differ too much in size).</summary>
        public static class NavIcons
        {
            public const string Sliders = "M1.5,4 H12.5 M1.5,10 H12.5 M5,1.5 V6.5 M9,7.5 V12.5";
            public const string Document = "M1.5,1.5 H8 L12.5,6 V12.5 H1.5 Z M8,1.5 V6 H12.5 M4,8.5 H10 M4,10.5 H8";
            public const string Cube = "M7,1.5 L12.5,4.5 V10 L7,13 L1.5,10 V4.5 Z M1.5,4.5 L7,7.5 L12.5,4.5 M7,7.5 V13";
            public const string Chip = "M4,4 H10 V10 H4 Z M6,1 V4 M8,1 V4 M6,10 V13 M8,10 V13 M1,6 H4 M1,8 H4 M10,6 H13 M10,8 H13";
            public const string Play = "M3.5,1.8 L12,7 L3.5,12.2 Z";
            public const string Arrows = "M1.5,4.5 H11 M8.5,2 L11,4.5 L8.5,7 M12.5,9.5 H3 M5.5,7 L3,9.5 L5.5,12";
            public const string Pencil = "M1.5,12.5 L2.5,9.5 L9.5,2.5 L11.5,4.5 L4.5,11.5 Z M8,4 L10,6";
            public const string Grid = "M1.5,1.5 H6 V6 H1.5 Z M8,1.5 H12.5 V6 H8 Z M1.5,8 H6 V12.5 H1.5 Z M8,8 H12.5 V12.5 H8 Z";
            public const string Target = "M7,1.5 A5.5,5.5 0 1 1 6.99,1.5 Z M7,4.5 A2.5,2.5 0 1 1 6.99,4.5 Z M7,0.5 V3 M7,11 V13.5 M0.5,7 H3 M11,7 H13.5";
            public const string Wrench = "M9.5,1.5 A3,3 0 0 0 7,5.5 L1.8,10.7 A1.1,1.1 0 0 0 3.3,12.2 L8.5,7 A3,3 0 0 0 12.5,4.5 L10.5,5.5 L8.5,3.5 Z";
        }

        /// <summary>The hover of a selected segment keeps the accent (the theme would paint it grey: it would look unselected). Added to the window.</summary>
        public static IEnumerable<IStyle> SegmentStyles()
        {
            foreach (var state in new[] { ":pointerover", ":pressed" })
                yield return new Style(x => x.OfType<Button>().Class("seg").Class("on").Class(state).Template().OfType<ContentPresenter>())
                {
                    Setters = { new Setter(ContentPresenter.BackgroundProperty, Accent), new Setter(ContentPresenter.ForegroundProperty, Brushes.White) },
                };
        }

        /// <summary>Marks a button of a segmented choice as selected or not.</summary>
        public static void PaintSegment(Button button, bool on)
        {
            if (!button.Classes.Contains("seg")) button.Classes.Add("seg");
            button.Classes.Set("on", on);
            button.Background = on ? Accent : Brushes.Transparent;
            button.Foreground = on ? Brushes.White : EditorTheme.TextDim;
        }

        public static TextBlock Scope(string text) =>
            new() { Text = text.ToUpperInvariant(), FontSize = 11, Foreground = EditorTheme.TextDim, Margin = new Thickness(8, 6, 8, 4), LetterSpacing = 0.8 };

        /// <summary>A page: a title, a line about it, and its groups.</summary>
        public static Control Page(string title, string subtitle, params Control[] content)
        {
            var stack = new StackPanel { Spacing = 14, Margin = new Thickness(24, 18, 24, 24) };
            stack.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = subtitle, Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap },
                },
            });
            foreach (var c in content) stack.Children.Add(c);
            return stack;
        }

        public static Control Group(string title, params Control[] rows)
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = AccentText, LetterSpacing = 0.8 });
            foreach (var r in rows) stack.Children.Add(r);
            return new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12), Child = stack };
        }

        public static Control Columns(Control left, Control right)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,*") };
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(right);
            return grid;
        }

        /// <summary>A label at the left and the field.</summary>
        public static Control Row(string label, Control control, double labelWidth = 150)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{labelWidth},*") };
            var text = new TextBlock { Text = label, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
            return grid;
        }

        /// <summary>A label above the field (for a field of several lines).</summary>
        public static Control Block(string label, Control control) =>
            new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap }, control } };

        public static TextBlock Hint(string text) => new() { Text = text, Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap };

        /// <summary>Controls side by side.</summary>
        public static StackPanel Line(params Control[] controls)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var c in controls) line.Children.Add(c);
            return line;
        }

        public static TextBox TextField(string? watermark = null, double width = double.NaN, bool mono = false)
        {
            var box = new TextBox { Background = Field, Watermark = watermark, Width = width };
            if (double.IsNaN(width)) box.HorizontalAlignment = HorizontalAlignment.Stretch; else box.HorizontalAlignment = HorizontalAlignment.Left;
            if (mono) box.FontFamily = Mono;
            return box;
        }

        /// <summary>A field of several lines (one entry per line), in the monospace font.</summary>
        public static TextBox LinesField(string? watermark = null, double height = 64) => new()
        {
            Background = Field, Watermark = watermark, AcceptsReturn = true, Height = height, FontFamily = Mono, HorizontalAlignment = HorizontalAlignment.Stretch,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        public static ComboBox Combo() => new() { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Field };

        /// <summary>
        /// The frame of a settings window: the list at the left, the page, and at the bottom a hint (and an error) with Cancel and Save.
        /// </summary>
        public static Control Frame(SettingsNav nav, Control footerText, Button cancel, Button save)
        {
            var footer = new DockPanel { Margin = new Thickness(16, 10) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, save } };
            DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
            footer.Children.Add(buttons);
            footer.Children.Add(footerText);
            var footerBorder = new Border { Background = EditorTheme.DarkSurface, BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };

            var body = new DockPanel();
            DockPanel.SetDock(nav.View, Avalonia.Controls.Dock.Left);
            body.Children.Add(nav.View);
            body.Children.Add(nav.Host);
            var root = new DockPanel();
            DockPanel.SetDock(footerBorder, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(footerBorder);
            root.Children.Add(body);
            return root;
        }

        public static Button DialogButton(string text, bool accent = false)
        {
            var button = new Button { Content = text, MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (accent) button.Classes.Add("accent");
            return button;
        }
    }

    /// <summary>The list of pages at the left of a settings window and the place where the chosen page is shown.</summary>
    internal sealed class SettingsNav
    {
        private readonly StackPanel _list = new() { Spacing = 2, Margin = new Thickness(8, 10, 8, 10) };
        private readonly List<(string Key, Button Button, Avalonia.Controls.Shapes.Path Icon, Control Page)> _pages = new();

        public Border View { get; }
        public ScrollViewer Host { get; } = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        public string? Current { get; private set; }

        public SettingsNav(double width = 210)
        {
            View = new Border { Width = width, Background = EditorTheme.DarkSurface, BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = _list };
        }

        public void AddScope(string text) => _list.Children.Add(SettingsLayout.Scope(text));

        public void AddSeparator() => _list.Children.Add(new Border { Height = 1, Background = EditorTheme.Border, Margin = new Thickness(6, 8) });

        public void Add(string key, string icon, string title, Control page)
        {
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(icon), Width = 14, Height = 14, StrokeThickness = 1.3, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center,
            };
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
                BorderBrush = SettingsLayout.AccentText, BorderThickness = new Thickness(0), Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(6),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { path, new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis } } },
            };
            button.Click += (_, _) => Show(key);
            _list.Children.Add(button);
            _pages.Add((key, button, path, page));
        }

        /// <summary>Shows a page (an unknown key: the first one).</summary>
        public void Show(string key)
        {
            var page = _pages.FirstOrDefault(p => p.Key == key);
            if (page.Page == null) page = _pages[0];
            foreach (var (k, button, icon, _) in _pages)
            {
                bool on = k == page.Key;
                button.Background = on ? SettingsLayout.NavSelected : Brushes.Transparent;
                button.BorderThickness = new Thickness(on ? 3 : 0, 0, 0, 0);
                button.Foreground = on ? EditorTheme.Text : EditorTheme.TextDim;
                icon.Stroke = on ? SettingsLayout.AccentText : EditorTheme.TextDim;
            }
            Current = page.Key;
            Host.Content = page.Page;
            Host.Offset = default;
        }
    }

    /// <summary>A choice of a few values as buttons side by side (the selected one in the accent colour).</summary>
    internal sealed class Segmented : Border
    {
        private readonly List<Button> _buttons = new();
        private int _selected = -1;

        public event EventHandler? SelectionChanged;

        public Segmented(params string[] labels)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var button = new Button
                {
                    Content = labels[i], Padding = new Thickness(14, 5), CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                    BorderBrush = EditorTheme.Border, MinWidth = 64, HorizontalContentAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent,
                };
                button.Click += (_, _) => SelectedIndex = index;
                _buttons.Add(button);
                row.Children.Add(button);
            }
            BorderBrush = EditorTheme.Border;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            ClipToBounds = true;
            HorizontalAlignment = HorizontalAlignment.Left;
            Child = row;
            Styles.AddRange(SettingsLayout.SegmentStyles());
            Paint();
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value == _selected) return;
                _selected = value;
                Paint();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string? SelectedLabel => _selected >= 0 && _selected < _buttons.Count ? _buttons[_selected].Content as string : null;

        private void Paint()
        {
            for (int i = 0; i < _buttons.Count; i++) SettingsLayout.PaintSegment(_buttons[i], i == _selected);
        }
    }
}
