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
    /// The block model as a program sees it: scene3d itself draws it (in the virtual machine, from the model file forge makes - .f3d), on a floor, lit by the sun,
    /// with an animation playing. The renderer (Software / Fast), the animation and the side the camera looks from can be chosen; the pictures are made once
    /// (a frame of the animation each 1/30 s) and then shown in a loop.
    /// </summary>
    public sealed class BlockPreviewWindow : Window
    {
        private readonly BlockModel _model;
        private readonly Image _image = new() { Stretch = Stretch.Uniform };
        private readonly ComboBox _anims = new() { MinWidth = 140 };
        private readonly ComboBox _renderer = new() { ItemsSource = new[] { "Software", "Fast" }, SelectedIndex = 0 };
        private readonly Slider _yaw = new() { Minimum = -180, Maximum = 180, Value = 35, Width = 160 };
        private readonly TextBlock _status = new() { Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / ModelConverter.FramesPerSecond) };
        private List<Bitmap> _frames = new();
        private int _shown, _request;

        public BlockPreviewWindow(BlockModel model, int animation)
        {
            _model = model;
            Title = "Preview (scene3d)";
            Width = 560; Height = 500;
            Background = EditorTheme.Solid(Color.FromRgb(0x1B, 0x18, 0x1E));
            _anims.ItemsSource = new[] { "(rest pose)" }.Concat(model.Animations.Select(a => a.Name)).ToList();
            _anims.SelectedIndex = Math.Clamp(animation + 1, 0, model.Animations.Count);
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
            Closed += (_, _) => { _timer.Stop(); foreach (var f in _frames) f.Dispose(); };
        }

        /// <summary>The program that draws the frames: the model file, a floor, the sun, a camera on the side chosen; a PNG per frame into `dir`.</summary>
        public static string Script(BlockModel model, string f3dPath, string dir, string animation, double length, bool fast, double yawDegrees, int frames)
        {
            var inv = CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("0.####", inv);
            double s = model.BlockSize, w = model.SizeX * s, h = model.SizeY * s, d = model.SizeZ * s, size = Math.Max(w, Math.Max(h, d));
            double yaw = yawDegrees * Math.PI / 180, dist = size * 1.25;
            string Q(string p) => "\"" + p.Replace("\\", "/").Replace("\"", "\\\"") + "\"";
            return $$"""
                #import "graphics"
                #import "scene3d"
                #import "filesystem"
                var model = Model.FromBytes(IO.File.ReadAllBytes({{Q(f3dPath)}}))
                var frame = new Framebuffer(480, 360)
                var scene = new Scene()
                {{(fast ? "scene.Renderer = RenderMode.Fast" : "")}}
                scene.ShadowResolution = 512
                var floor = new Object(Template.Plane, 0m, 0m, 0m, {{N(size * 2)}}m, 0m, {{N(size * 2)}}m)
                floor.Texture = Texture.Checker(32, 8, S3D.Rgb(70, 70, 78), S3D.Rgb(58, 58, 66))
                scene.Add(floor)
                var obj = new Object(model)
                obj.AnchorY = 0.0
                scene.Add(obj)
                scene.Add(Light.Directional(-0.5, -1.0, -0.35))
                var cam = new Camera(frame)
                cam.Background = S3D.Rgb(30, 27, 34)
                cam.SetPosition({{N(Math.Sin(yaw) * dist)}}m, {{N(h * 0.5 + size * 0.6)}}m, {{N(Math.Cos(yaw) * dist)}}m)
                cam.LookAt(0m, {{N(h * 0.4)}}m, 0m)
                scene.Add(cam)
                {{(animation.Length > 0 ? $"obj.Play({Q(animation)})" : "")}}
                for (var i = 0; i < {{frames}}; i++) {
                    if (i > 0) { obj.Advance({{N(1.0 / ModelConverter.FramesPerSecond)}}s) }
                    cam.Render()
                    IO.File.WriteAllBytes({{Q(dir)}} + "/f" + i + ".png", frame.ToPng())
                }
                """;
        }

        private async Task Render()
        {
            int request = ++_request;
            _timer.Stop();
            _status.Text = "scene3d draws ...";
            int anim = _anims.SelectedIndex - 1;
            var animation = anim >= 0 && anim < _model.Animations.Count ? _model.Animations[anim] : null;
            int frames = animation == null ? 1 : Math.Clamp((int)Math.Round(animation.Length * ModelConverter.FramesPerSecond) + (animation.Loop ? 0 : 1), 1, 240);
            bool fast = _renderer.SelectedIndex == 1;
            double yaw = _yaw.Value;
            string dir = Path.Combine(Path.GetTempPath(), "fire-block-preview-" + Guid.NewGuid().ToString("N"));
            (List<Bitmap>? Frames, string Error) result;
            try
            {
                result = await Task.Run(() =>
                {
                    Directory.CreateDirectory(dir);
                    string f3d = Path.Combine(dir, "model.f3d");
                    File.WriteAllBytes(f3d, _model.ToF3d());
                    var printed = new List<string>();
                    var session = RuntimeSession.Build(new[] { Script(_model, f3d, dir, animation?.Name ?? "", animation?.Length ?? 0, fast, yaw, frames) }, fire.Runtime.VmExecutionMode.Release,
                        a => { printed.Add(a.Length > 0 ? a[0].ToString() ?? "" : ""); return fire.Values.Value.MakeUndefined(); });
                    session.Run();
                    if (session.VirtualMachine?.UnhandledException is { } ex) return ((List<Bitmap>?)null, ex.ToString());
                    var list = new List<Bitmap>();
                    for (int i = 0; i < frames; i++)
                    {
                        string png = Path.Combine(dir, "f" + i + ".png");
                        if (!File.Exists(png)) break;
                        using var stream = File.OpenRead(png);
                        list.Add(new Bitmap(stream));
                    }
                    return (list, string.Join("\n", printed));
                });
            }
            catch (Exception ex) { result = (null, CompileErrors.Describe(ex)); }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (request != _request) { foreach (var f in result.Frames ?? new()) f.Dispose(); return; }
            if (result.Frames is not { Count: > 0 } list) { _status.Text = "scene3d could not draw it: " + result.Error; return; }
            foreach (var f in _frames) f.Dispose();
            _frames = list;
            _shown = 0;
            _image.Source = _frames[0];
            _status.Text = $"{_model.Quads().Count * 2} triangles, {(fast ? "the lean renderer" : "the software renderer")}" + (_frames.Count > 1 ? $", {_frames.Count} frames" : "");
            if (_frames.Count > 1) _timer.Start();
        }

        private static class RangeBase { public static readonly AvaloniaProperty ValueProperty = Avalonia.Controls.Primitives.RangeBase.ValueProperty; }
    }
}
