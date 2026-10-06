using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace fire.Editor
{
    /// <summary>The window a floating <see cref="ToolStrip"/> lives in: no frame, a small title bar to drag it by (and to hide it), the strip below. It belongs to the main window, so it
    /// stays above it and closes with it.</summary>
    public sealed class ToolStripFloatingWindow : Window
    {
        public ToolStripFloatingWindow(ToolStrip strip, ToolStripManager manager)
        {
            Strip = strip;
            WindowDecorations = WindowDecorations.None;
            ShowInTaskbar = false;
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;
            Background = Brushes.Transparent;

            var title = new TextBlock { Text = strip.Title, FontSize = 11, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 2, 8, 2), IsHitTestVisible = false };
            var close = new Button
            {
                Content = new PathIcon { Data = Geometry.Parse("M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z"), Width = 8, Height = 8 },
                Padding = new Thickness(5, 3), Background = Brushes.Transparent, MinWidth = 0, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(close, "Hide this tool bar (View > Toolbars shows it again)");
            close.Click += (_, _) => manager.SetVisible(strip, false);
            var header = new Border { Background = new SolidColorBrush(Color.Parse("#2E2934")), Cursor = new Cursor(StandardCursorType.SizeAll) };
            var headerRow = new DockPanel();
            DockPanel.SetDock(close, Avalonia.Controls.Dock.Right);
            headerRow.Children.Add(close);
            headerRow.Children.Add(title);
            header.Child = headerRow;
            strip.AttachDragHandle(header);

            Host = new StackPanel { Orientation = Orientation.Vertical };
            Host.Children.Add(header);
            Content = new Border
            {
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.Parse("#242028")),
                Child = Host,
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true
            };
            ((Border)Content).Bind(Border.BorderBrushProperty, this.GetResourceObservable("DockSeparatorBrush"));
        }

        public ToolStrip Strip { get; }

        /// <summary>Holds the title bar and, below it, the strip.</summary>
        public StackPanel Host { get; }
    }

    /// <summary>A picture of a strip that follows the pointer while a strip in a tray is dragged (the strip itself stays where it is until it is dropped).</summary>
    internal sealed class ToolStripGhostWindow : Window
    {
        public ToolStripGhostWindow(ToolStrip strip)
        {
            WindowDecorations = WindowDecorations.None;
            ShowInTaskbar = false;
            CanResize = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            Opacity = 0.85;
            Background = Brushes.Transparent;
            IsHitTestVisible = false;

            var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(strip.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(strip.Bounds.Height)));
            var picture = new RenderTargetBitmap(size);
            picture.Render(strip);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#242028")),
                BorderBrush = new SolidColorBrush(Color.Parse("#E8447F")),
                BorderThickness = new Thickness(1),
                Child = new Image { Source = picture, Width = strip.Bounds.Width, Height = strip.Bounds.Height, Stretch = Stretch.None }
            };
        }
    }
}
