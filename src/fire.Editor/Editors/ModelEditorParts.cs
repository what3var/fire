using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using fire.Compiler;
using Pen = Avalonia.Media.Pen;

namespace fire.Editor
{
    /// <summary>The palette of a model editor (the block editor, the CAD editor): a click chooses a colour, a double click edits it.</summary>
    internal sealed class ModelPaletteStrip : Control
    {
        public const double Cell = 22;
        public List<uint> Palette = new(BlockModel.DefaultPalette);
        public int Current;
        public event Action<int>? Picked;
        public event Action<int>? EditRequested;

        protected override Size MeasureOverride(Size availableSize)
        {
            int perRow = Math.Max(1, (int)((double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width) / Cell));
            return new Size(Math.Min(Palette.Count, perRow) * Cell + 1, Math.Ceiling(Palette.Count / (double)perRow) * Cell + 1);
        }

        private int PerRow => Math.Max(1, (int)(Bounds.Width / Cell));

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var p = e.GetPosition(this);
            int i = (int)(p.Y / Cell) * PerRow + (int)(p.X / Cell);
            if (i < 0 || i >= Palette.Count) return;
            if (e.ClickCount == 2) EditRequested?.Invoke(i); else Picked?.Invoke(i);
        }

        public override void Render(DrawingContext context)
        {
            int perRow = PerRow;
            for (int i = 0; i < Palette.Count; i++)
            {
                uint rgb = Palette[i];
                var r = new Rect((i % perRow) * Cell, (i / perRow) * Cell, Cell - 2, Cell - 2);
                context.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)), r);
                context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1), r);   // (a dark colour on the dark ground)
                if (i == Current) context.DrawRectangle(null, new Pen(Brushes.White, 2), r.Deflate(1));
            }
        }
    }

    /// <summary>
    /// The animations of a model editor (the block editor, the CAD editor - their parts and animations are the same, BlockModel.Part / Animation): choose an
    /// animation (or the rest pose, where the model is built), its name and length, loop, play, a timeline with the keys of the part chosen, and the numbers of
    /// that part at the time (turn, move, scale) - a change of a number makes a key of the part there. The host gives the animations, the part chosen and
    /// its way of changing the model (one step of undo); PoseChanged tells it to draw the pose again.
    /// </summary>
    internal sealed class ModelAnimationBar : WrapPanel
    {
        private sealed class TimelineView : Control
        {
            public BlockModel.Animation? Animation;
            public int CurrentPart;
            public double Time;
            public event Action<double>? TimeChosen;
            private bool _down;

            public TimelineView() { Height = 30; MinWidth = 260; Cursor = new Cursor(StandardCursorType.Hand); }

            private double Length => Animation?.Length > 0 ? Animation.Length : 1;
            private double X(double t) => 8 + (Bounds.Width - 16) * t / Length;

            /// <summary>The time at a point of the timeline, on the frames of the model file (1/30 s).</summary>
            private double TimeAt(Point p)
            {
                double t = (p.X - 8) / Math.Max(1, Bounds.Width - 16) * Length;
                t = Math.Round(t * ModelConverter.FramesPerSecond) / ModelConverter.FramesPerSecond;
                return Math.Clamp(t, 0, Length);
            }

            protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); if (Animation == null) return; _down = true; e.Pointer.Capture(this); TimeChosen?.Invoke(TimeAt(e.GetPosition(this))); }
            protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (_down) TimeChosen?.Invoke(TimeAt(e.GetPosition(this))); }
            protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _down = false; e.Pointer.Capture(null); }

            public override void Render(DrawingContext context)
            {
                double w = Bounds.Width, h = Bounds.Height, mid = h / 2;
                context.FillRectangle(EditorTheme.Solid(Color.FromRgb(0x24, 0x21, 0x28)), new Rect(0, 0, w, h));
                if (Animation == null) return;
                var tick = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1);
                double step = Length <= 2 ? 0.1 : Length <= 10 ? 0.5 : 1;
                for (double t = 0; t <= Length + 1e-9; t += step)
                {
                    bool whole = Math.Abs(t - Math.Round(t)) < 1e-9;
                    context.DrawLine(tick, new Point(X(t), whole ? 4 : mid - 3), new Point(X(t), whole ? h - 4 : mid + 3));
                }
                context.DrawLine(tick, new Point(X(0), mid), new Point(X(Length), mid));
                foreach (var k in Animation.Keys)
                {
                    double x = X(Math.Min(k.Time, Length));
                    if (k.Part == CurrentPart)
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open()) { c.BeginFigure(new Point(x, mid - 6), true); c.LineTo(new Point(x + 6, mid)); c.LineTo(new Point(x, mid + 6)); c.LineTo(new Point(x - 6, mid)); c.EndFigure(true); }
                        context.DrawGeometry(Brushes.Orange, null, g);
                    }
                    else context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x90, 0xC0, 0xC0, 0xC0)), new Rect(x - 1.5, mid - 3, 3, 6));
                }
                context.DrawLine(new Pen(Brushes.White, 2), new Point(X(Time), 2), new Point(X(Time), h - 2));
            }
        }

        private readonly ComboBox _anims = new() { MinWidth = 150 };
        private readonly TimelineView _timeline = new() { Width = 300 };
        private readonly TextBlock _timeText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), MinWidth = 70 };
        private readonly ToggleButton _loop = new() { Content = "Loop" };
        private readonly Button _play = new() { Content = "▶" };
        private readonly NumericUpDown[] _rot = new NumericUpDown[3], _move = new NumericUpDown[3];
        private readonly NumericUpDown _scale = new() { Minimum = 0.05m, Maximum = 20, Increment = 0.1m, FormatString = "0.##", Width = 58, ShowButtonSpinner = false };
        private readonly Avalonia.Threading.DispatcherTimer _player = new() { Interval = TimeSpan.FromMilliseconds(33) };
        private int _anim = -1;
        private double _time;
        private bool _updatingFields, _updatingAnims;

        /// <summary>The animations of the model (the host's list, changed in place).</summary>
        public Func<List<BlockModel.Animation>> Animations = () => new List<BlockModel.Animation>();
        /// <summary>The part whose keys are shown and set.</summary>
        public Func<int> CurrentPart = () => 0;
        /// <summary>The host's way to change the model (a step of undo).</summary>
        public Action<Action> Change = a => a();
        /// <summary>The animation or its time changed: the pose is drawn again.</summary>
        public event Action? PoseChanged;

        public ModelAnimationBar(string moveUnit, decimal moveStep)
        {
            Orientation = Orientation.Horizontal;
            Margin = new Thickness(4, 0, 4, 2);
            _loop.Classes.Add("tool"); _play.Classes.Add("tool");
            ToolTip.SetTip(_loop, "The animation starts again at its end (from its last key to its first)");
            ToolTip.SetTip(_play, "Plays the animation");
            for (int k = 0; k < 3; k++)
            {
                _rot[k] = new NumericUpDown { Minimum = -3600, Maximum = 3600, Increment = 15, FormatString = "0.#", Width = 58, ShowButtonSpinner = false, Watermark = "xyz"[k].ToString() };
                _move[k] = new NumericUpDown { Minimum = -256, Maximum = 256, Increment = moveStep, FormatString = "0.###", Width = 58, ShowButtonSpinner = false, Watermark = "xyz"[k].ToString() };
                ToolTip.SetTip(_rot[k], $"Turn around {"xyz"[k]} in degrees (arrow keys: 15 degrees) - a change makes a key of the part at this time");
                ToolTip.SetTip(_move[k], $"Move in {"xyz"[k]} in {moveUnit} (arrow keys: {moveStep.ToString(CultureInfo.InvariantCulture)}) - a change makes a key of the part at this time");
                _rot[k].ValueChanged += (_, _) => FieldChanged();
                _move[k].ValueChanged += (_, _) => FieldChanged();
            }
            _scale.ValueChanged += (_, _) => FieldChanged();
            Children.Add(new TextBlock { Text = "Animation", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) });
            Children.Add(_anims);
            Children.Add(ModelEditorUi.Button("+", "A new animation", async () => await AddAnimation()));
            Children.Add(ModelEditorUi.Button("Edit...", "The name and the length (seconds) of the animation", async () => await EditAnimation()));
            Children.Add(ModelEditorUi.Button("−", "Take the animation out", RemoveAnimation));
            Children.Add(_loop);
            Children.Add(_play);
            Children.Add(_timeline);
            Children.Add(_timeText);
            Children.Add(ModelEditorUi.Separator());
            Children.Add(ModelEditorUi.Label("Turn"));
            foreach (var n in _rot) Children.Add(n);
            Children.Add(ModelEditorUi.Label("Move"));
            foreach (var n in _move) Children.Add(n);
            Children.Add(ModelEditorUi.Label("Scale"));
            Children.Add(_scale);
            Children.Add(ModelEditorUi.Button("Delete key", "Takes the key of the part at this time away", DeleteKey));
            _anims.SelectionChanged += (_, _) => { if (!_updatingAnims) Choose(_anims.SelectedIndex - 1); };
            _loop.Click += (_, _) => { if (Current is { } anim) { bool loop = _loop.IsChecked == true; Change(() => anim.Loop = loop); } };
            _play.Click += (_, _) => TogglePlay();
            _timeline.TimeChosen += t => { Stop(); SetTime(t); };
            _player.Tick += (_, _) => PlayStep();
        }

        /// <summary>The animation shown (null: the rest pose).</summary>
        public BlockModel.Animation? Current { get { var list = Animations(); return _anim >= 0 && _anim < list.Count ? list[_anim] : null; } }
        public int Index => _anim;
        public double Time => _time;

        /// <summary>Shows an animation (-1: the rest pose).</summary>
        public void Choose(int index)
        {
            Stop();
            _anim = Math.Clamp(index, -1, Animations().Count - 1);
            _time = 0;
            RefreshList();
            PoseChanged?.Invoke();
            Refresh();
        }

        public void SetTime(double time) { _time = Math.Max(0, time); PoseChanged?.Invoke(); Refresh(); }

        /// <summary>Back to the rest pose without telling (a new model).</summary>
        public void Reset() { Stop(); _anim = -1; _time = 0; }

        public void Stop() { _player.Stop(); _play.Content = "▶"; }

        private void TogglePlay() { if (_player.IsEnabled) Stop(); else if (Current != null) { _player.Start(); _play.Content = "■"; } }

        private void PlayStep()
        {
            if (Current is not { } anim) { Stop(); return; }
            double t = _time + _player.Interval.TotalSeconds;
            if (t >= anim.Length) { if (anim.Loop) t -= anim.Length; else { t = anim.Length; Stop(); } }
            SetTime(t);
        }

        /// <summary>The names of the animations again (after a model came or one was renamed).</summary>
        public void RefreshList()
        {
            var list = Animations();
            if (_anim >= list.Count) _anim = list.Count - 1;
            _updatingAnims = true;
            _anims.ItemsSource = new[] { "(rest pose - build)" }.Concat(list.Select(a => a.Name)).ToList();
            _anims.SelectedIndex = _anim + 1;
            _updatingAnims = false;
        }

        /// <summary>The timeline and the numbers of the part chosen at this time.</summary>
        public void Refresh()
        {
            if (_anim >= Animations().Count) { _anim = Animations().Count - 1; RefreshList(); }
            var anim = Current;
            if (anim != null) _time = Math.Clamp(_time, 0, anim.Length);
            int part = CurrentPart();
            _timeline.Animation = anim; _timeline.Time = _time; _timeline.CurrentPart = part;
            _timeline.InvalidateVisual();
            _timeText.Text = anim != null ? $"{_time.ToString("0.00", CultureInfo.InvariantCulture)} / {anim.Length.ToString("0.##", CultureInfo.InvariantCulture)} s" : "";
            _loop.IsChecked = anim?.Loop == true;
            foreach (var c in new Control[] { _loop, _play, _timeline, _scale }.Concat(_rot).Concat(_move)) c.IsEnabled = anim != null;
            _updatingFields = true;
            var (rot, move, scale) = BlockModel.PoseOf(anim, part, _time);
            for (int k = 0; k < 3; k++) { _rot[k].Value = (decimal)Math.Round(rot[k], 3); _move[k].Value = (decimal)Math.Round(move[k], 3); }
            _scale.Value = (decimal)Math.Round(scale[0], 3);
            _updatingFields = false;
        }

        /// <summary>A number of the key fields changed: the part gets a key at this time with what the fields say.</summary>
        private void FieldChanged()
        {
            if (_updatingFields || Current is not { } anim) return;
            double[] rot = _rot.Select(n => (double)(n.Value ?? 0)).ToArray(), move = _move.Select(n => (double)(n.Value ?? 0)).ToArray();
            double sc = (double)(_scale.Value ?? 1);
            SetKey(anim, CurrentPart(), _time, rot, move, new[] { sc, sc, sc });
        }

        /// <summary>Puts (or changes) the key of a part at a time.</summary>
        public void SetKey(BlockModel.Animation anim, int part, double time, double[] rotate, double[] move, double[] scale) => Change(() =>
        {
            var key = BlockModel.KeyAt(anim, part, time);
            if (key == null) { key = new BlockModel.Key { Part = part, Time = time }; anim.Keys.Add(key); }
            key.Rotate = rotate; key.Move = move; key.Scale = scale;
        });

        private void DeleteKey()
        {
            if (Current is not { } anim || BlockModel.KeyAt(anim, CurrentPart(), _time) is not { } key) return;
            Change(() => anim.Keys.Remove(key));
        }

        private async Task<string?> Input(string prompt, string title, string initial) => await Dialogs.Input(Dialogs.WindowOf(this), prompt, title, initial);

        private async Task AddAnimation()
        {
            var list = Animations();
            string? name = await Input("The name of the new animation (a program plays it with obj.Play(\"name\")):", "New animation", "animation" + (list.Count + 1));
            if (string.IsNullOrWhiteSpace(name)) return;
            Change(() => list.Add(new BlockModel.Animation { Name = name.Trim(), Length = 1, Loop = true }));
            Choose(Animations().Count - 1);
        }

        private async Task EditAnimation()
        {
            if (Current is not { } anim) return;
            string? text = await Input("The name and the length in seconds, e.g. walk, 1.5:", "Animation", $"{anim.Name}, {anim.Length.ToString("0.###", CultureInfo.InvariantCulture)}");
            if (text == null) return;
            int comma = text.LastIndexOf(',');
            string name = (comma >= 0 ? text[..comma] : text).Trim();
            double length = anim.Length;
            if (comma >= 0 && double.TryParse(text[(comma + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double l) && l > 0) length = l;
            if (name.Length == 0) return;
            Change(() => { anim.Name = name; anim.Length = length; });
            RefreshList();
        }

        private void RemoveAnimation()
        {
            if (_anim < 0) return;
            int index = _anim;
            var list = Animations();
            Change(() => list.RemoveAt(index));
            Choose(Math.Min(index, Animations().Count - 1));
        }
    }

    /// <summary>The small pieces of the bars of the model editors.</summary>
    internal static class ModelEditorUi
    {
        public static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };

        public static Control Separator() => new Border { Width = 1, Background = EditorTheme.Border, Margin = new Thickness(6, 2) };

        public static Button Button(string text, string tip, Action click)
        {
            var b = new Button { Content = text };
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }

        public static ToggleButton Toggle(string text, string tip, bool on = false)
        {
            var b = new ToggleButton { Content = text, IsChecked = on };
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            return b;
        }
    }
}
