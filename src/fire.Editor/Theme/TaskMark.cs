using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>
    /// The mark of a task (docs/NOTES.md), drawn as a box with rounded corners: empty (open), filled from the lower right corner to the diagonal (in progress), filled (done),
    /// filled red (blocked). The filling is a little darker than the outline, so that the outline stays visible. Used by the Todo window and the Markdown preview.
    /// </summary>
    internal sealed class TaskMark : Control
    {
        public static readonly StyledProperty<MdTaskState> StateProperty = AvaloniaProperty.Register<TaskMark, MdTaskState>(nameof(State));

        public MdTaskState State
        {
            get => GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        static TaskMark() => AffectsRender<TaskMark>(StateProperty);

        public TaskMark()
        {
            Width = 15;
            Height = 15;
        }

        /// <summary>The red of a blocked task: warmer than the accent colour, so that it does not look like done.</summary>
        public static readonly Color BlockedColor = Color.FromRgb(0xFF, 0x52, 0x3B);

        private static readonly IPen OpenPen = new Pen(new SolidColorBrush(Color.FromRgb(0xA0, 0x94, 0xA8)), 1.5);
        private static readonly IPen AccentPen = new Pen(new SolidColorBrush(EditorTheme.AccentTextColor), 1.5);
        private static readonly IBrush AccentFill = new SolidColorBrush(EditorTheme.AccentColor);
        private static readonly IPen BlockedPen = new Pen(new SolidColorBrush(BlockedColor), 1.5);
        private static readonly IBrush BlockedFill = new SolidColorBrush(Color.FromRgb(0xC4, 0x2E, 0x1C));

        public override void Render(DrawingContext context)
        {
            // the whole box takes clicks, also where nothing is drawn
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(Brushes.Transparent, bounds);

            var box = bounds.Deflate(0.75);   // the outline lies inside the control
            double radius = System.Math.Min(box.Width, box.Height) * 0.25;
            var (pen, fill) = State switch
            {
                MdTaskState.Done or MdTaskState.InProgress => (AccentPen, AccentFill),
                MdTaskState.Blocked => (BlockedPen, BlockedFill),
                _ => (OpenPen, (IBrush?)null),
            };

            if (fill != null && State == MdTaskState.InProgress)
            {
                // the lower right half, cut off by the diagonal from the lower left to the upper right corner
                var half = new StreamGeometry();
                using (var g = half.Open())
                {
                    g.BeginFigure(box.BottomLeft, true);
                    g.LineTo(box.TopRight);
                    g.LineTo(box.BottomRight);
                    g.EndFigure(true);
                }
                using (context.PushGeometryClip(half))
                    context.DrawRectangle(fill, null, box, radius, radius);
            }
            else if (fill != null)
            {
                context.DrawRectangle(fill, null, box, radius, radius);
            }
            context.DrawRectangle(null, pen, box, radius, radius);
        }
    }
}
