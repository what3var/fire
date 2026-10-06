using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>A place a strip can be put into a tray: into a band at an index, or into a band of its own at the position <see cref="Band"/>.</summary>
    public readonly record struct ToolStripSlot(int Band, int Index, bool NewBand);

    /// <summary>
    /// The tray at one edge of the window. It holds <see cref="ToolStrip"/>s in bands (rows at the top and bottom, columns at the left and right) in the order of
    /// <see cref="ToolStrip.Band"/> and <see cref="ToolStrip.BandIndex"/>; it is as small as its strips, and empty it has no size at all. While a strip is dragged over it
    /// it shows where the strip would go (<see cref="ShowInsertion"/>).
    /// </summary>
    public class ToolStripTray : Panel
    {
        /// <summary>The space between two strips of a band.</summary>
        private const double Gap = 4;

        public static readonly StyledProperty<bool> HasStripsProperty = AvaloniaProperty.Register<ToolStripTray, bool>(nameof(HasStrips));
        public static readonly StyledProperty<IBrush?> InsertionBrushProperty = AvaloniaProperty.Register<ToolStripTray, IBrush?>(nameof(InsertionBrush), new SolidColorBrush(Color.Parse("#E8447F")));

        // the mark of the insertion place: a border over the strips (a panel cannot draw over its children itself)
        private readonly Border _mark = new() { IsHitTestVisible = false, IsVisible = false, ZIndex = 10 };
        private (ToolStripSlot Slot, Rect Mark)? _insertion;

        public ToolStripTray()
        {
            Children.Add(_mark);
            _mark.Bind(Border.BackgroundProperty, this.GetObservable(InsertionBrushProperty));
            Children.CollectionChanged += (_, _) => HasStrips = Strips.Any();
        }

        /// <summary>The edge of the window the tray is at (Top, Bottom, Left or Right).</summary>
        public ToolStripSide Side { get; set; } = ToolStripSide.Top;

        public bool IsVertical => Side is ToolStripSide.Left or ToolStripSide.Right;

        /// <summary>True as long as a strip is in the tray (the window hides an empty one).</summary>
        public bool HasStrips { get => GetValue(HasStripsProperty); private set => SetValue(HasStripsProperty, value); }

        /// <summary>Looks at the strips again (one was shown or hidden).</summary>
        public void Refresh()
        {
            HasStrips = Strips.Any();
            InvalidateMeasure();
        }

        public IBrush? InsertionBrush { get => GetValue(InsertionBrushProperty); set => SetValue(InsertionBrushProperty, value); }

        /// <summary>The strips that are shown, in the order of the tray.</summary>
        public IEnumerable<ToolStrip> Strips => Children.OfType<ToolStrip>().Where(s => s.IsVisible)
            .OrderBy(s => s.Band).ThenBy(s => s.BandIndex);

        private List<List<ToolStrip>> Bands() => Strips.GroupBy(s => s.Band).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();

        // ---------------------------------------------------------------------------------------------------------
        // layout: band after band, the strips of a band next to each other
        // ---------------------------------------------------------------------------------------------------------

        protected override Size MeasureOverride(Size availableSize)
        {
            bool v = IsVertical;
            double along = v ? availableSize.Height : availableSize.Width;    // the length of a band
            double across = 0, longest = 0;
            foreach (var band in Bands())
            {
                // natural sizes first; when the band is too long the strips get a share of the length each (a command bar then moves what does not fit into its overflow menu)
                foreach (var s in band) s.Measure(Size.Infinity);
                double natural = band.Sum(s => v ? s.DesiredSize.Height : s.DesiredSize.Width) + Gap * (band.Count - 1);
                if (!double.IsInfinity(along) && natural > along)
                {
                    double usable = Math.Max(0, along - Gap * (band.Count - 1));
                    double total = band.Sum(s => v ? s.DesiredSize.Height : s.DesiredSize.Width);
                    foreach (var s in band)
                    {
                        double share = usable * (v ? s.DesiredSize.Height : s.DesiredSize.Width) / Math.Max(1, total);
                        s.Measure(v ? new Size(double.PositiveInfinity, share) : new Size(share, double.PositiveInfinity));
                    }
                    natural = along;
                }
                longest = Math.Max(longest, natural);
                across += band.Max(s => v ? s.DesiredSize.Width : s.DesiredSize.Height);
            }
            if (!Strips.Any()) return default;
            return v ? new Size(across, double.IsInfinity(availableSize.Height) ? longest : availableSize.Height)
                     : new Size(double.IsInfinity(availableSize.Width) ? longest : availableSize.Width, across);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            bool v = IsVertical;
            double offset = 0;
            foreach (var band in Bands())
            {
                double thickness = band.Max(s => v ? s.DesiredSize.Width : s.DesiredSize.Height);
                double pos = 0;
                foreach (var s in band)
                {
                    double length = v ? s.DesiredSize.Height : s.DesiredSize.Width;
                    s.Arrange(v ? new Rect(offset, pos, thickness, length) : new Rect(pos, offset, length, thickness));
                    pos += length + Gap;
                }
                offset += thickness;
            }
            _mark.Arrange(_insertion is { } i ? i.Mark : default);
            return finalSize;
        }

        // ---------------------------------------------------------------------------------------------------------
        // putting strips in, finding the place for a dragged one
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Puts the strip into the slot (it may come from another tray or be a strip of this one that is moved); the bands and indices of the others move on.</summary>
        public void Place(ToolStrip strip, ToolStripSlot slot)
        {
            var others = Children.OfType<ToolStrip>().Where(s => s != strip).ToList();
            if (slot.NewBand) foreach (var o in others.Where(o => o.Band >= slot.Band)) o.Band++;
            else foreach (var o in others.Where(o => o.Band == slot.Band && o.BandIndex >= slot.Index)) o.BandIndex++;
            strip.Band = slot.Band;
            strip.BandIndex = slot.NewBand ? 0 : slot.Index;
            if (!ReferenceEquals(strip.Parent, this))
            {
                (strip.Parent as Panel)?.Children.Remove(strip);
                Children.Add(strip);
            }
            Normalize();
        }

        /// <summary>Numbers the bands and the strips in them 0, 1, 2, ... again (after a strip left or came).</summary>
        public void Normalize()
        {
            int b = 0;
            foreach (var group in Children.OfType<ToolStrip>().GroupBy(s => s.Band).OrderBy(g => g.Key))
            {
                int i = 0;
                foreach (var s in group.OrderBy(s => s.BandIndex)) { s.Band = b; s.BandIndex = i++; }
                b++;
            }
            InvalidateMeasure();
        }

        /// <summary>The slot for a strip dragged to <paramref name="p"/> (in the coordinates of the tray); <paramref name="dragged"/> is not counted among the strips.</summary>
        public (ToolStripSlot Slot, Rect Mark) GetSlot(Point p, ToolStrip? dragged)
        {
            bool v = IsVertical;
            var bands = Strips.Where(s => s != dragged).GroupBy(s => s.Band).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
            if (bands.Count == 0) return (new ToolStripSlot(0, 0, true), default);

            double across = v ? p.X : p.Y, along = v ? p.Y : p.X;
            Rect BandRect(List<ToolStrip> band) => band.Select(s => s.Bounds).Aggregate((a, b) => a.Union(b));
            double Start(Rect r) => v ? r.Left : r.Top;
            double End(Rect r) => v ? r.Right : r.Bottom;

            var first = BandRect(bands[0]);
            var last = BandRect(bands[^1]);
            Rect Line(double position) => v ? new Rect(position - 1, 0, 2, Math.Max(Bounds.Height, 1)) : new Rect(0, position - 1, Math.Max(Bounds.Width, 1), 2);
            if (across < Start(first) - 2) return (new ToolStripSlot(bands[0][0].Band, 0, true), Line(Start(first)));
            if (across > End(last) + 2) return (new ToolStripSlot(bands[^1][0].Band + 1, 0, true), Line(End(last)));

            foreach (var band in bands)
            {
                var r = BandRect(band);
                if (across > End(r) + 2 || across < Start(r) - 2) continue;
                int index = band.Count;
                for (int i = 0; i < band.Count; i++)
                {
                    var b = band[i].Bounds;
                    double centre = v ? b.Center.Y : b.Center.X;
                    if (along < centre) { index = i; break; }
                }
                double mark = index < band.Count ? (v ? band[index].Bounds.Top : band[index].Bounds.Left) - Gap / 2
                                                 : (v ? band[^1].Bounds.Bottom : band[^1].Bounds.Right) + Gap / 2;
                var rect = v ? new Rect(r.Left, mark - 1, r.Width, 2) : new Rect(mark - 1, r.Top, 2, r.Height);
                return (new ToolStripSlot(band[0].Band, index, false), rect);
            }
            // between two bands
            for (int i = 1; i < bands.Count; i++)
            {
                var upper = BandRect(bands[i - 1]);
                if (across < Start(BandRect(bands[i])))
                    return (new ToolStripSlot(bands[i][0].Band, 0, true), Line((End(upper) + Start(BandRect(bands[i]))) / 2));
            }
            return (new ToolStripSlot(bands[^1][0].Band + 1, 0, true), Line(End(last)));
        }

        /// <summary>Shows (or, with null, hides) the mark of the place a dragged strip would go to.</summary>
        public void ShowInsertion((ToolStripSlot Slot, Rect Mark)? insertion)
        {
            _insertion = insertion;
            _mark.IsVisible = insertion is { Mark: { Width: > 0, Height: > 0 } };
            InvalidateArrange();
        }

    }
}
