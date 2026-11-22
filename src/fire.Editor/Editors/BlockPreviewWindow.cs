using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using fire.Compiler;

namespace fire.Editor
{
    /// <summary>
    /// A model of spark's model editors (the block editor, the CAD editor) as a program sees it: scene3d itself draws it (in the virtual machine, from the model file forge makes - .f3d), on a floor, lit by the sun,
    /// with an animation playing. The renderer (Software / Fast), the animation and the side the camera looks from can be chosen; the pictures are made once
    /// (a frame of the animation each 1/30 s) and then shown in a loop.
    /// </summary>
    public sealed class BlockPreviewWindow : Window
    {
        private readonly Func<byte[]> _f3d;
        private readonly List<BlockModel.Animation> _animations;
        private readonly double _w, _h, _d;
        private readonly int _triangles;
        private readonly Image _image = new() { Stretch = Stretch.Uniform };
        private readonly ComboBox _anims = new() { MinWidth = 140 };
        private readonly ComboBox _renderer = new() { ItemsSource = new[] { "Software", "Fast" }, SelectedIndex = 0 };
        private readonly Slider _yaw = new() { Minimum = -180, Maximum = 180, Value = 35, Width = 160 };
        private readonly TextBlock _status = new() { Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / ModelConverter.FramesPerSecond) };
        private List<Bitmap> _frames = new();
        private int _shown, _request;

        public BlockPreviewWindow(BlockModel model, int animation)
            : this(model.ToF3d, model.Animations, model.SizeX * model.BlockSize, model.SizeY * model.BlockSize, model.SizeZ * model.BlockSize, model.Quads().Count * 2, animation) { }

        /// <summary>A mesh model (the CAD editor's): its box around the origin.</summary>
        public BlockPreviewWindow(MeshModel model, int animation)
            : this(model.ToF3d, model.Animations, Extent(model, 0), Extent(model, 1), Extent(model, 2), model.TriangleCount, animation) { }

        private static double Extent(MeshModel m, int axis) { var (min, max) = m.Bounds(); return 1.6 * Math.Max(0.1, axis == 1 ? max[1] - Math.Min(0, min[1]) : 2 * Math.Max(Math.Abs(min[axis]), Math.Abs(max[axis]))); }   // (with room around it, as the grid of a block model has)

        /// <summary>Any model: the model file, its animations, its size in meters (for the camera), its triangles.</summary>
        public BlockPreviewWindow(Func<byte[]> f3d, List<BlockModel.Animation> animations, double width, double height, double depth, int triangles, int animation)
        {
            _f3d = f3d; _animations = animations; _w = width; _h = height; _d = depth; _triangles = triangles;
            Title = "Preview (scene3d)";
            Width = 560; Height = 500;
            Background = EditorTheme.Solid(Color.FromRgb(0x1B, 0x18, 0x1E));
            _anims.ItemsSource = new[] { "(rest pose)" }.Concat(animations.Select(a => a.Name)).ToList();
            _anims.SelectedIndex = Math.Clamp(animation + 1, 0, animations.Count);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6) };
            bar.Children.Add(new TextBlock { Text = "Animation", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(_anims);
            bar.Children.Add(new TextBlock { Text = "Renderer", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(_renderer);
            bar.Children.Add(new TextBlock { Text = "Side", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(_yaw);
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(_status, Avalonia.Controls.Dock.Bottom);
            dock.Children.Add(bar); dock.Children.Add(_status); dock.Children.Add(_image);
            Content = dock;
            _anims.SelectionChanged += (_, _) => _ = Render();
            _renderer.SelectionChanged += (_, _) => _ = Render();
            _yaw.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) _ = Render(); };
            _timer.Tick += (_, _) => { if (_frames.Count > 0) { _shown = (_shown + 1) % _frames.Count; _image.Source = _frames[_shown]; } };
            Opened += (_, _) => _ = Render();
            Closed += (_, _) => { _timer.Stop(); _running?.Cancel(); foreach (var f in _frames) f.Dispose(); };
        }

        private System.Threading.CancellationTokenSource? _running;

        private async Task Render()
        {
            int request = ++_request;
            _timer.Stop();
            _status.Text = "scene3d draws ...";
            int anim = _anims.SelectedIndex - 1;
            var animation = anim >= 0 && anim < _animations.Count ? _animations[anim] : null;
            int frames = animation == null ? 1 : Math.Clamp((int)Math.Round(animation.Length * ModelConverter.FramesPerSecond) + (animation.Loop ? 0 : 1), 1, 240);
            bool fast = _renderer.SelectedIndex == 1;
            double yaw = _yaw.Value;
            // a newer choice ends the drawing of the older one (its process in `fire`)
            _running?.Cancel();
            var cancel = new System.Threading.CancellationTokenSource();
            _running = cancel;
            var rendered = await Task.Run(() =>
            {
                byte[] f3dBytes;
                try { f3dBytes = _f3d(); }
                catch (Exception ex) { return ((List<byte[]>?)null, CompileErrors.Describe(ex), "", 0.0, 0.0); }
                return BlockPreview.RenderFrames(f3dBytes, _w, _h, _d, animation?.Name ?? "", fast, yaw, frames, cancel.Token);
            });
            if (request != _request) return;
            if (rendered.Frames is not { Count: > 0 } pngs) { _status.Text = "scene3d could not draw it: " + rendered.Error; return; }
            var list = new List<Bitmap>();
            foreach (var png in pngs) { using var stream = new MemoryStream(png); list.Add(new Bitmap(stream)); }
            foreach (var f in _frames) f.Dispose();
            _frames = list;
            _shown = 0;
            _image.Source = _frames[0];
            _status.Text = $"{_triangles} triangles, {(fast ? "the lean renderer" : "the software renderer")}" + (_frames.Count > 1 ? $", {_frames.Count} frames" : "");
            if (_frames.Count > 1) _timer.Start();
        }

        private static class RangeBase { public static readonly AvaloniaProperty ValueProperty = Avalonia.Controls.Primitives.RangeBase.ValueProperty; }
    }
}
