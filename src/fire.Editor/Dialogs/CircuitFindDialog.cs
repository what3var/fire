using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace fire.Editor
{
    /// <summary>Find (and replace) in a schematic: names, kinds and property values of the elements. Find Next selects the next element that matches and moves the
    /// view to it; Replace All changes names and values in one step of undo. The dialog stays open while searching.</summary>
    internal sealed class CircuitFindDialog : Window
    {
        public CircuitFindDialog(string text, bool replace, Action<string, bool> find, Action<string, string> replaceAll)
        {
            Title = replace ? "Find and Replace" : "Find";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var what = new TextBox { Text = text, Watermark = "a name, a kind or a value (R1, Resistor, 4k7)" };
            var with = new TextBox { Watermark = "replace with" };
            var next = new Button { Content = "Find Next", IsDefault = true };
            var previous = new Button { Content = "Find Previous" };
            var all = new Button { Content = "Replace All" };
            var close = new Button { Content = "Close", IsCancel = true };
            next.Click += (_, _) => { if ((what.Text ?? "") != "") find(what.Text!, false); };
            previous.Click += (_, _) => { if ((what.Text ?? "") != "") find(what.Text!, true); };
            all.Click += (_, _) => { if ((what.Text ?? "") != "") replaceAll(what.Text!, with.Text ?? ""); };
            close.Click += (_, _) => Close();

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 6 };
            void Row(int row, string label, Control box)
            {
                var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(t, row);
                Grid.SetRow(box, row);
                Grid.SetColumn(box, 1);
                grid.Children.Add(t);
                grid.Children.Add(box);
            }
            Row(0, "Find", what);
            if (replace) Row(1, "Replace with", with);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(previous);
            buttons.Children.Add(next);
            if (replace) buttons.Children.Add(all);
            buttons.Children.Add(close);
            Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { grid, buttons } };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Opened += (_, _) => { what.Focus(); what.SelectAll(); };
        }
    }
}
