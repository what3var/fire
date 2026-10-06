using System;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;

namespace fire.Editor
{
    /// <summary>Where a <see cref="ToolStrip"/> sits: in the tray at one of the four window edges, or floating in a window of its own.</summary>
    public enum ToolStripSide { Top, Bottom, Left, Right, Floating }

    /// <summary>
    /// A strip of tool buttons that can be moved: a grip on its left (top, when it stands vertically) is dragged to another place in its tray, to another
    /// <see cref="ToolStripTray"/> at a window edge, or out of the window, where it floats (<see cref="ToolStripManager"/> does the moving).
    /// Horizontally the items are the commands of an Avalonia <see cref="CommandBar"/> - with its overflow menu when the strip is too narrow; anything that is not a
    /// command (combo boxes, split buttons, texts) goes into the content of the bar. Vertically (left and right trays) the same items are stacked; the ones marked
    /// <see cref="HorizontalOnlyProperty"/> (labels, combo boxes) are left out there.
    /// </summary>
    public class ToolStrip : Border
    {
        /// <summary>The way the strip is laid out; the tray (or the floating window) sets it.</summary>
        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<ToolStrip, Orientation>(nameof(Orientation), Orientation.Horizontal);

        /// <summary>Marks an item that is not shown when the strip stands vertically.</summary>
        public static readonly AttachedProperty<bool> HorizontalOnlyProperty =
            AvaloniaProperty.RegisterAttached<ToolStrip, Control, bool>("HorizontalOnly");

        public static bool GetHorizontalOnly(Control c) => c.GetValue(HorizontalOnlyProperty);
        public static void SetHorizontalOnly(Control c, bool value) => c.SetValue(HorizontalOnlyProperty, value);

        public static readonly StyledProperty<int> BandProperty = AvaloniaProperty.Register<ToolStrip, int>(nameof(Band));
        public static readonly StyledProperty<int> BandIndexProperty = AvaloniaProperty.Register<ToolStrip, int>(nameof(BandIndex));

        private readonly CommandBar _bar = new() { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsDynamicOverflowEnabled = false, Background = Brushes.Transparent };
        private readonly StackPanel _barContent = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _stack = new() { Orientation = Orientation.Vertical, Spacing = 2 };
        private readonly Border _grip;
        private readonly DockPanel _root = new();
        private ToolStripManager? _manager;

        static ToolStrip()
        {
            OrientationProperty.Changed.AddClassHandler<ToolStrip>((s, _) => s.Rebuild());
            BandProperty.Changed.AddClassHandler<ToolStrip>((s, _) => (s.Parent as ToolStripTray)?.InvalidateMeasure());
            BandIndexProperty.Changed.AddClassHandler<ToolStrip>((s, _) => (s.Parent as ToolStripTray)?.InvalidateMeasure());
        }

        public ToolStrip()
        {
            Items = new AvaloniaList<Control>();
            Items.CollectionChanged += (_, _) => Rebuild();
            _grip = new Border
            {
                Background = Brushes.Transparent,   // the whole area is hit-testable, the bar in it is only drawn
                Cursor = new Cursor(StandardCursorType.SizeAll),
                Child = new Border { CornerRadius = new CornerRadius(2), Opacity = 0.55 }
            };
            ((Border)_grip.Child).Bind(BackgroundProperty, this.GetResourceObservable("DockSeparatorBrush"));
            _root.Children.Add(_grip);
            Child = _root;
            Padding = new Thickness(2, 1);
            AttachDragHandle(_grip);
            Rebuild();
        }

        /// <summary>The items: commands (<c>CommandBarButton</c>, <c>CommandBarToggleButton</c>, <c>CommandBarSeparator</c>) and other controls.</summary>
        [Content]
        public AvaloniaList<Control> Items { get; }

        /// <summary>The name the layout is saved under (and the View menu finds the strip by).</summary>
        public string Id { get; set; } = "";

        /// <summary>The name shown in the View menu, as the tool tip of the grip and in the title of a floating window.</summary>
        public string Title { get; set; } = "";

        public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

        /// <summary>The row (column, when it stands vertically) of the tray the strip is in, counted from the outside.</summary>
        public int Band { get => GetValue(BandProperty); set => SetValue(BandProperty, value); }

        /// <summary>The place of the strip in its band.</summary>
        public int BandIndex { get => GetValue(BandIndexProperty); set => SetValue(BandIndexProperty, value); }

        /// <summary>False when every item is for horizontal strips only (combo boxes and labels): such a strip is not docked at the left or right edge.</summary>
        public bool SupportsVertical => Items.Any(i => !GetHorizontalOnly(i));

        public ToolStripSide Side { get; internal set; } = ToolStripSide.Top;

        /// <summary>The window the strip floats in, null when it is in a tray.</summary>
        public ToolStripFloatingWindow? FloatingWindow { get; internal set; }

        /// <summary>The side of the last tray the strip was in (where a double click on a floating strip puts it back).</summary>
        public ToolStripSide LastDockedSide { get; internal set; } = ToolStripSide.Top;

        /// <summary>The grip menu is set by the manager (docking and hiding commands).</summary>
        public ContextMenu? GripMenu { get => _grip.ContextMenu; set => _grip.ContextMenu = value; }

        public ToolStripManager? Manager { get => _manager; set => _manager = value; }

        /// <summary>The grip is a drag handle; the floating window's title bar is another one.</summary>
        public void AttachDragHandle(Control handle)
        {
            handle.PointerPressed += (_, e) =>
            {
                if (_manager == null || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
                e.Pointer.Capture(handle);
                _manager.BeginDrag(this, handle, e);
                e.Handled = true;
            };
            handle.PointerMoved += (_, e) => _manager?.DragMove(this, handle, e);
            handle.PointerReleased += (_, e) =>
            {
                if (_manager == null) return;
                // the drag ends before the capture is let go (that raises PointerCaptureLost, which cancels a drag that is still going)
                _manager.EndDrag(this, handle, e);
                e.Pointer.Capture(null);
            };
            handle.PointerCaptureLost += (_, _) => _manager?.CancelDrag(this);
            handle.DoubleTapped += (_, e) => { _manager?.ToggleFloating(this); e.Handled = true; };
        }

        /// <summary>The commands of the bar (the items that are commands).</summary>
        public CommandBar Bar => _bar;

        private void Rebuild()
        {
            bool vertical = Orientation == Orientation.Vertical;

            // take everything out of the places it was in (a control has one parent)
            _bar.PrimaryCommands.Clear();
            _bar.Content = null;
            _barContent.Children.Clear();
            _stack.Children.Clear();
            _root.Children.Remove(_bar);
            _root.Children.Remove(_stack);

            DockPanel.SetDock(_grip, vertical ? Avalonia.Controls.Dock.Top : Avalonia.Controls.Dock.Left);
            _grip.Width = vertical ? double.NaN : 12;
            _grip.Height = vertical ? 12 : double.NaN;
            _grip.Child!.Margin = vertical ? new Thickness(4, 3) : new Thickness(3, 4);

            if (vertical)
            {
                foreach (var item in Items)
                {
                    item.IsVisible = !GetHorizontalOnly(item);
                    _stack.Children.Add(item);
                }
                _root.Children.Add(_stack);
            }
            else
            {
                foreach (var item in Items)
                {
                    item.IsVisible = true;
                    if (item is ICommandBarElement command) _bar.PrimaryCommands.Add(command);
                    else _barContent.Children.Add(item);
                }
                _bar.Content = _barContent.Children.Count > 0 ? _barContent : null;
                _root.Children.Add(_bar);
            }
        }
    }
}
