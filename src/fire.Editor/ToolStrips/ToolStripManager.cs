using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace fire.Editor
{
    /// <summary>
    /// Moves the <see cref="ToolStrip"/>s of a window: between the four <see cref="ToolStripTray"/>s at its edges and out of it into floating windows. A strip is dragged by its grip
    /// (or the title bar of its floating window): over a tray - or near the edge of the window - a mark shows where it would land, a drop there puts it into that tray (into a
    /// band and at an index, or into a band of its own); a drop anywhere else lets it float. A double click on the grip floats a docked strip and docks a floating one again.
    /// The arrangement can be saved and loaded (<see cref="Save"/>, <see cref="Load"/>).
    /// </summary>
    public sealed class ToolStripManager
    {
        /// <summary>How close to a window edge (DIPs) a strip has to be dropped to dock there.</summary>
        private const double EdgeZone = 44;
        /// <summary>How far (DIPs) around a tray that has strips a drop still counts as one into the tray.</summary>
        private const double NearTray = 24;
        /// <summary>How far (pixels) the pointer has to go before a press on the grip is a drag.</summary>
        private const int DragThreshold = 5;

        private readonly Window _main;
        private readonly Control _area;
        private readonly Border _hint;
        private readonly Dictionary<ToolStripSide, ToolStripTray> _trays;
        private readonly List<ToolStrip> _strips = new();
        private Drag? _drag;

        private sealed class Drag
        {
            public ToolStrip Strip = null!;
            public PixelPoint Start;
            public PixelPoint Offset;
            public bool Moved;
            public ToolStripGhostWindow? Ghost;
            public PixelPoint Position;
            public (ToolStripTray Tray, ToolStripSlot Slot, Rect Mark)? Zone;
        }

        /// <param name="main">The window the trays are in (the floating windows belong to it).</param>
        /// <param name="area">The control that spans the part of the window the trays are in (the hint is laid over it).</param>
        /// <param name="hint">A border in the same cell as <paramref name="area"/>; it lights up at an edge whose tray is empty.</param>
        public ToolStripManager(Window main, Control area, Border hint, ToolStripTray top, ToolStripTray bottom, ToolStripTray left, ToolStripTray right)
        {
            _main = main;
            _area = area;
            _hint = hint;
            top.Side = ToolStripSide.Top;
            bottom.Side = ToolStripSide.Bottom;
            left.Side = ToolStripSide.Left;
            right.Side = ToolStripSide.Right;
            _trays = new() { [ToolStripSide.Top] = top, [ToolStripSide.Bottom] = bottom, [ToolStripSide.Left] = left, [ToolStripSide.Right] = right };
        }

        /// <summary>The strips of the window.</summary>
        public IReadOnlyList<ToolStrip> Strips => _strips;

        /// <summary>The arrangement changed (a strip was moved, floated, hidden or shown).</summary>
        public event Action? Changed;

        public ToolStripTray TrayOf(ToolStripSide side) => _trays[side];

        /// <summary>Takes over the strips that are in the trays (the markup puts them there) - once, when the window has been built.</summary>
        public void Adopt()
        {
            foreach (var (side, tray) in _trays)
            {
                tray.Normalize();
                foreach (var strip in tray.Children.OfType<ToolStrip>().ToList())
                {
                    strip.Manager = this;
                    strip.Side = strip.LastDockedSide = side;
                    strip.Orientation = tray.IsVertical ? Orientation.Vertical : Orientation.Horizontal;
                    strip.GripMenu = BuildGripMenu(strip);
                    _strips.Add(strip);
                }
                tray.Refresh();
            }
        }

        public ToolStrip? Find(string id) => _strips.FirstOrDefault(s => s.Id == id);

        // ---------------------------------------------------------------------------------------------------------
        // docking, floating, hiding
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Puts a strip into the tray at <paramref name="side"/>; without a slot at the end of the last band.</summary>
        public void Dock(ToolStrip strip, ToolStripSide side, ToolStripSlot? slot = null)
        {
            if (side == ToolStripSide.Floating) { Float(strip, null); return; }
            if (side is ToolStripSide.Left or ToolStripSide.Right && !strip.SupportsVertical) return;
            var oldTray = strip.Parent as ToolStripTray;
            ReleaseFloatingWindow(strip);
            var tray = _trays[side];
            if (slot == null)
            {
                var bands = tray.Strips.Where(s => s != strip).GroupBy(s => s.Band).OrderBy(g => g.Key).ToList();
                slot = bands.Count == 0 ? new ToolStripSlot(0, 0, true) : new ToolStripSlot(bands[^1].Key, bands[^1].Count(), false);
            }
            tray.Place(strip, slot.Value);
            strip.Side = strip.LastDockedSide = side;
            strip.Orientation = tray.IsVertical ? Orientation.Vertical : Orientation.Horizontal;
            strip.IsVisible = true;
            oldTray?.Refresh();
            tray.Refresh();
            Changed?.Invoke();
        }

        /// <summary>Lets a strip float in a window of its own at <paramref name="screenPosition"/> (without one: near the top left of the main window).</summary>
        public ToolStripFloatingWindow Float(ToolStrip strip, PixelPoint? screenPosition)
        {
            if (strip.FloatingWindow is { } existing)
            {
                if (screenPosition is { } p) existing.Position = p;
                return existing;
            }
            if (strip.Parent is ToolStripTray tray)
            {
                tray.Children.Remove(strip);
                tray.Normalize();
                tray.Refresh();
            }
            strip.Orientation = Orientation.Horizontal;
            var window = new ToolStripFloatingWindow(strip, this)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = screenPosition ?? new PixelPoint(_main.Position.X + 120, _main.Position.Y + 160)
            };
            window.Host.Children.Add(strip);
            strip.FloatingWindow = window;
            strip.Side = ToolStripSide.Floating;
            strip.IsVisible = true;
            strip.Band = 0;
            strip.BandIndex = 0;
            window.Show(_main);
            Changed?.Invoke();
            return window;
        }

        private static void ReleaseFloatingWindow(ToolStrip strip)
        {
            if (strip.FloatingWindow is not { } window) return;
            window.Host.Children.Remove(strip);
            strip.FloatingWindow = null;
            window.Close();
        }

        /// <summary>A double click on the grip: a docked strip floats, a floating one goes back to the tray it came from.</summary>
        public void ToggleFloating(ToolStrip strip)
        {
            if (strip.FloatingWindow != null) { Dock(strip, strip.LastDockedSide); return; }
            var topLeft = strip.PointToScreen(new Point(0, 0));
            Float(strip, new PixelPoint(topLeft.X + 24, topLeft.Y + 24));
        }

        /// <summary>Shows or hides a strip (the View menu, the close button of a floating window).</summary>
        public void SetVisible(ToolStrip strip, bool visible)
        {
            strip.IsVisible = visible;
            if (strip.FloatingWindow is { } window)
            {
                if (visible) window.Show(_main); else window.Hide();
            }
            (strip.Parent as ToolStripTray)?.Refresh();
            Changed?.Invoke();
        }

        /// <summary>Closes the floating windows (the main window is closing).</summary>
        public void CloseFloatingWindows()
        {
            foreach (var strip in _strips.Where(s => s.FloatingWindow != null).ToList()) strip.FloatingWindow!.Close();
        }

        private ContextMenu BuildGripMenu(ToolStrip strip)
        {
            MenuItem Item(string header, Action action)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => action();
                return item;
            }
            return new ContextMenu
            {
                ItemsSource = new Control[]
                {
                    Item("Dock at the _Top", () => Dock(strip, ToolStripSide.Top)),
                    Item("Dock at the _Bottom", () => Dock(strip, ToolStripSide.Bottom)),
                    Item("Dock at the _Left", () => Dock(strip, ToolStripSide.Left)).Enable(strip.SupportsVertical),
                    Item("Dock at the _Right", () => Dock(strip, ToolStripSide.Right)).Enable(strip.SupportsVertical),
                    Item("_Float", () => Float(strip, null)),
                    new Separator(),
                    Item("_Hide", () => SetVisible(strip, false))
                }
            };
        }

        // ---------------------------------------------------------------------------------------------------------
        // dragging
        // ---------------------------------------------------------------------------------------------------------

        internal void BeginDrag(ToolStrip strip, Control handle, PointerPressedEventArgs e)
        {
            var screen = handle.PointToScreen(e.GetPosition(handle));
            var drag = new Drag { Strip = strip, Start = screen };
            if (strip.FloatingWindow is { } window) drag.Offset = new PixelPoint(screen.X - window.Position.X, screen.Y - window.Position.Y);
            else
            {
                var inStrip = e.GetPosition(strip);
                double scale = _main.DesktopScaling;
                drag.Offset = new PixelPoint((int)(inStrip.X * scale), (int)(inStrip.Y * scale));
            }
            _drag = drag;
        }

        internal void DragMove(ToolStrip strip, Control handle, PointerEventArgs e)
        {
            if (_drag is not { } drag || drag.Strip != strip) return;
            var screen = handle.PointToScreen(e.GetPosition(handle));
            if (!drag.Moved)
            {
                if (Math.Abs(screen.X - drag.Start.X) < DragThreshold && Math.Abs(screen.Y - drag.Start.Y) < DragThreshold) return;
                drag.Moved = true;
                if (strip.FloatingWindow == null)
                {
                    drag.Ghost = new ToolStripGhostWindow(strip) { WindowStartupLocation = WindowStartupLocation.Manual };
                    drag.Ghost.Position = new PixelPoint(screen.X - drag.Offset.X, screen.Y - drag.Offset.Y);
                    drag.Ghost.Show();
                    strip.Opacity = 0.4;
                }
            }
            drag.Position = new PixelPoint(screen.X - drag.Offset.X, screen.Y - drag.Offset.Y);
            if (strip.FloatingWindow is { } window) window.Position = drag.Position;
            else if (drag.Ghost != null) drag.Ghost.Position = drag.Position;
            ShowZone(drag, FindZone(screen, strip));
        }

        internal void EndDrag(ToolStrip strip, Control handle, PointerReleasedEventArgs e)
        {
            if (_drag is not { } drag || drag.Strip != strip) return;
            _drag = null;
            var landing = drag.Zone;
            ShowZone(drag, null);
            strip.Opacity = 1;
            drag.Ghost?.Close();
            if (!drag.Moved) return;
            if (landing is { } zone)
            {
                // closing the window the pointer is captured in is done after the event
                Dispatcher.UIThread.Post(() => Dock(strip, zone.Tray.Side, zone.Slot));
            }
            else if (strip.FloatingWindow == null) Float(strip, drag.Position);
            else Changed?.Invoke();
        }

        internal void CancelDrag(ToolStrip strip)
        {
            if (_drag is not { } drag || drag.Strip != strip) return;
            _drag = null;
            ShowZone(drag, null);
            strip.Opacity = 1;
            drag.Ghost?.Close();
        }

        private void ShowZone(Drag drag, (ToolStripTray Tray, ToolStripSlot Slot, Rect Mark)? zone)
        {
            drag.Zone = zone;
            foreach (var tray in _trays.Values) tray.ShowInsertion(zone is { } z && z.Tray == tray && tray.HasStrips ? (z.Slot, z.Mark) : null);
            if (zone is not { } hit || hit.Tray.HasStrips)
            {
                _hint.IsVisible = false;
                return;
            }
            // an empty tray has no size: the edge of the window lights up instead
            bool vertical = hit.Tray.IsVertical;
            _hint.HorizontalAlignment = hit.Tray.Side switch { ToolStripSide.Left => HorizontalAlignment.Left, ToolStripSide.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Stretch };
            _hint.VerticalAlignment = hit.Tray.Side switch { ToolStripSide.Top => VerticalAlignment.Top, ToolStripSide.Bottom => VerticalAlignment.Bottom, _ => VerticalAlignment.Stretch };
            _hint.Width = vertical ? 6 : double.NaN;
            _hint.Height = vertical ? double.NaN : 6;
            _hint.IsVisible = true;
        }

        /// <summary>The tray (and the place in it) a strip dropped at <paramref name="screen"/> would go to; null: it would float.</summary>
        private (ToolStripTray Tray, ToolStripSlot Slot, Rect Mark)? FindZone(PixelPoint screen, ToolStrip dragged)
        {
            var c = _main.PointToClient(screen);
            if (!new Rect(_main.Bounds.Size).Contains(c)) return null;

            (ToolStripTray, ToolStripSlot, Rect)? In(ToolStripTray tray)
            {
                var p = _main.TranslatePoint(c, tray) ?? default;
                var (slot, mark) = tray.GetSlot(p, dragged);
                return (tray, slot, mark);
            }

            // over a tray that has strips
            foreach (var tray in _trays.Values)
            {
                if (tray.IsVertical && !dragged.SupportsVertical) continue;
                if (!tray.HasStrips || tray.Bounds.Width <= 0 || tray.Bounds.Height <= 0) continue;
                var origin = tray.TranslatePoint(default, _main);
                // a little more than the tray itself, so that a drop just beside it still lands in it (and can start a band of its own there)
                if (origin != null && new Rect(origin.Value, tray.Bounds.Size).Inflate(NearTray).Contains(c)) return In(tray);
            }

            // near an edge of the area the trays are in
            var topLeft = _area.TranslatePoint(default, _main) ?? default;
            var area = new Rect(topLeft, _area.Bounds.Size);
            var distances = new (ToolStripSide Side, double Distance)[]
            {
                (ToolStripSide.Top, Math.Max(0, c.Y - area.Top)),
                (ToolStripSide.Bottom, Math.Max(0, area.Bottom - c.Y)),
                (ToolStripSide.Left, Math.Max(0, c.X - area.Left)),
                (ToolStripSide.Right, Math.Max(0, area.Right - c.X))
            };
            var nearest = distances.Where(d => dragged.SupportsVertical || d.Side is ToolStripSide.Top or ToolStripSide.Bottom).OrderBy(d => d.Distance).First();
            return nearest.Distance <= EdgeZone ? In(_trays[nearest.Side]) : null;
        }

        // ---------------------------------------------------------------------------------------------------------
        // saving
        // ---------------------------------------------------------------------------------------------------------

        private sealed record Entry(string Id, string Side, int Band, int Index, bool Visible, int X, int Y, string LastSide);

        /// <summary>The arrangement as JSON.</summary>
        public string Save()
        {
            var entries = _strips.Select(s => new Entry(s.Id, s.Side.ToString(), s.Band, s.BandIndex, s.IsVisible,
                s.FloatingWindow?.Position.X ?? 0, s.FloatingWindow?.Position.Y ?? 0, s.LastDockedSide.ToString())).ToList();
            return JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>Arranges the strips as <paramref name="json"/> says; strips it does not know stay where they are.</summary>
        public void Load(string json)
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(json) ?? throw new InvalidDataException("empty tool bar layout");
            foreach (var entry in entries.OrderBy(e => e.Band).ThenBy(e => e.Index))
            {
                if (Find(entry.Id) is not { } strip) continue;
                if (!Enum.TryParse<ToolStripSide>(entry.Side, out var side)) continue;
                if (Enum.TryParse<ToolStripSide>(entry.LastSide, out var last) && last != ToolStripSide.Floating) strip.LastDockedSide = last;
                if (side == ToolStripSide.Floating) Float(strip, new PixelPoint(entry.X, entry.Y));
                else
                {
                    var oldTray = strip.Parent as ToolStripTray;
                    ReleaseFloatingWindow(strip);
                    var tray = _trays[side];
                    strip.Band = entry.Band;
                    strip.BandIndex = entry.Index;
                    if (!ReferenceEquals(strip.Parent, tray))
                    {
                        (strip.Parent as Panel)?.Children.Remove(strip);
                        tray.Children.Add(strip);
                    }
                    oldTray?.Normalize();
                    strip.Side = strip.LastDockedSide = side;
                    strip.Orientation = tray.IsVertical ? Orientation.Vertical : Orientation.Horizontal;
                }
                SetVisible(strip, entry.Visible);
            }
            foreach (var tray in _trays.Values) { tray.Normalize(); tray.Refresh(); }
            Changed?.Invoke();
        }
    }

    internal static class MenuItemExtensions
    {
        public static MenuItem Enable(this MenuItem item, bool enabled)
        {
            item.IsEnabled = enabled;
            return item;
        }
    }
}
