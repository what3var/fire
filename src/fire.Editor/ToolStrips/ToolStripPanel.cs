using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>
    /// The layout of a <see cref="ToolStrip"/>: its items in a line - next to each other (<see cref="Orientation.Horizontal"/>) or under each other (<see cref="Orientation.Vertical"/>) - and an
    /// overflow button at the end that opens a popup with the items that do not fit. The panel is as long as the items that fit (and the overflow button): when the space given to it gets shorter,
    /// items move into the popup and the panel shrinks; when there is room again they come back and it grows - never longer than the items themselves are. An item that is marked
    /// <see cref="KeepWithNextProperty"/> (the label of a combo box) always moves together with the item after it.
    /// </summary>
    public class ToolStripPanel : Panel
    {
        public static readonly StyledProperty<Orientation> OrientationProperty =
            AvaloniaProperty.Register<ToolStripPanel, Orientation>(nameof(Orientation), Orientation.Horizontal);

        /// <summary>Marks an item that stays together with the item after it (it overflows with it, never alone).</summary>
        public static readonly AttachedProperty<bool> KeepWithNextProperty =
            AvaloniaProperty.RegisterAttached<ToolStripPanel, Control, bool>("KeepWithNext");

        public static bool GetKeepWithNext(Control c) => c.GetValue(KeepWithNextProperty);
        public static void SetKeepWithNext(Control c, bool value) => c.SetValue(KeepWithNextProperty, value);

        /// <summary>The space between two items.</summary>
        public double Spacing { get; set; } = 2;

        private static readonly Point Away = new(-10000, -10000);

        private readonly List<Control> _items = new();
        private readonly ToolStripButton _more = new()
        {
            IconData = Geometry.Parse("M4,6 L12,6 L8,10 Z"),
            IsVisible = true,
            IconSize = 8,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1)
        };

        private readonly WrapPanel _popup = new() { Orientation = Orientation.Horizontal, MaxWidth = 360 };
        private readonly Flyout _flyout;
        private readonly HashSet<Control> _hidden = new();     // the items that do not fit at the moment
        private bool _overflowing;
        private bool _popupOpen;

        static ToolStripPanel()
        {
            AffectsMeasure<ToolStripPanel>(OrientationProperty);
            OrientationProperty.Changed.AddClassHandler<ToolStripPanel>((p, _) => p.UpdateMoreButton());
        }

        public ToolStripPanel()
        {
            ClipToBounds = true;
            ToolTip.SetTip(_more, "More");
            _flyout = new Flyout { Content = _popup, Placement = PlacementMode.BottomEdgeAlignedRight };
            _flyout.Closed += (_, _) => CloseOverflow();
            _more.Click += (_, _) => OpenOverflow();
            UpdateMoreButton();
            Children.Add(_more);
        }

        public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

        /// <summary>True while some items do not fit and are in the overflow popup.</summary>
        public bool IsOverflowing => _overflowing;

        /// <summary>Takes these items (in this order) - the panel shows them in a line, the others in its overflow popup.</summary>
        public void SetItems(IEnumerable<Control> items)
        {
            if (_popupOpen) _flyout.Hide();
            _items.Clear();
            _items.AddRange(items);
            RebuildChildren();
        }

        private void RebuildChildren()
        {
            Children.Clear();
            _popup.Children.Clear();
            foreach (var item in _items) Children.Add(item);
            Children.Add(_more);
            InvalidateMeasure();
        }

        private void UpdateMoreButton()
        {
            bool vertical = Orientation == Orientation.Vertical;
            _more.IconData = Geometry.Parse(vertical ? "M3,2 L5,4 L3,6 Z" : "M2,3 L6,3 L4,5 Z");
            _flyout.Placement = vertical ? PlacementMode.RightEdgeAlignedTop : PlacementMode.BottomEdgeAlignedRight;
            _popup.MaxWidth = vertical ? 360 : 360;
        }

        // the hidden items go into the popup while it is open (a control has one parent) and come back when it closes
        private void OpenOverflow()
        {
            if (_popupOpen || _hidden.Count == 0) return;
            _popupOpen = true;
            foreach (var item in _items.Where(_hidden.Contains))
            {
                Children.Remove(item);
                item.IsHitTestVisible = true;
                _popup.Children.Add(item);
            }
            _flyout.ShowAt(_more);
        }

        private void CloseOverflow()
        {
            if (!_popupOpen) return;
            _popupOpen = false;
            RebuildChildren();
        }

        // ---------------------------------------------------------------------------------------------------------
        // layout
        // ---------------------------------------------------------------------------------------------------------

        private double Along(Size s) => Orientation == Orientation.Vertical ? s.Height : s.Width;
        private double Across(Size s) => Orientation == Orientation.Vertical ? s.Width : s.Height;

        /// <summary>The items that are shown, in groups that overflow together.</summary>
        private List<List<Control>> Groups()
        {
            var groups = new List<List<Control>>();
            bool joinNext = false;
            foreach (var item in _items.Where(i => i.IsVisible))
            {
                if (joinNext && groups.Count > 0) groups[^1].Add(item); else groups.Add(new List<Control> { item });
                joinNext = GetKeepWithNext(item);
            }
            return groups;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var groups = Groups();
            foreach (var item in _items.Where(i => i.IsVisible)) item.Measure(Size.Infinity);
            _more.Measure(Size.Infinity);

            double limit = Along(availableSize);
            double GroupLength(List<Control> g) => g.Sum(i => Along(i.DesiredSize)) + Spacing * (g.Count - 1);
            double total = groups.Sum(GroupLength) + Spacing * System.Math.Max(0, groups.Count - 1);
            double moreLength = Along(_more.DesiredSize);

            _hidden.Clear();
            double used = 0;
            if (double.IsInfinity(limit) || total <= limit)
            {
                used = total;
                _overflowing = false;
            }
            else
            {
                int accepted = 0;
                foreach (var g in groups)
                {
                    double need = used + (accepted > 0 ? Spacing : 0) + GroupLength(g);
                    if (need + Spacing + moreLength > limit) break;
                    used = need;
                    accepted++;
                }
                for (int i = accepted; i < groups.Count; i++) foreach (var item in groups[i]) _hidden.Add(item);
                used += (accepted > 0 ? Spacing : 0) + moreLength;
                _overflowing = true;
            }

            double across = _items.Where(i => i.IsVisible).Select(i => Across(i.DesiredSize)).DefaultIfEmpty(0).Max();
            if (_overflowing) across = System.Math.Max(across, Across(_more.DesiredSize));
            return Orientation == Orientation.Vertical ? new Size(across, used) : new Size(used, across);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            bool v = Orientation == Orientation.Vertical;
            double across = v ? finalSize.Width : finalSize.Height;
            double pos = 0;
            foreach (var item in _items)
            {
                if (!item.IsVisible) continue;
                if (_hidden.Contains(item))
                {
                    if (!_popupOpen) { item.Arrange(new Rect(Away, new Size(0, 0))); item.IsHitTestVisible = false; }
                    continue;
                }
                item.IsHitTestVisible = true;
                double length = Along(item.DesiredSize);
                item.Arrange(v ? new Rect(0, pos, across, length) : new Rect(pos, 0, length, across));
                pos += length + Spacing;
            }
            if (_overflowing)
            {
                double length = Along(_more.DesiredSize);
                _more.IsHitTestVisible = true;
                _more.Arrange(v ? new Rect(0, pos, across, length) : new Rect(pos, 0, length, across));
            }
            else
            {
                _more.IsHitTestVisible = false;
                _more.Arrange(new Rect(Away, new Size(0, 0)));
            }
            return finalSize;
        }
    }
}
