using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace fire.Tooling
{
    /// <summary>The preview of spark's model editors (the block editor, the CAD editor; the window is fire.Editor's BlockPreviewWindow): scene3d itself draws the model
    /// (from the model file forge makes - .f3d) on a floor, lit by the sun, a PNG per frame of the animation. The script runs in `fire` (the VM in C, <see cref="CVmScript"/>).</summary>
    public static class BlockPreview
    {
        /// <summary>The program that draws the frames: the model file, a floor, the sun, a camera on the side chosen; a PNG per frame into `dir`.</summary>
        public static string Script(BlockModel model, string f3dPath, string dir, string animation, double length, bool fast, double yawDegrees, int frames) =>
            Script(model.SizeX * model.BlockSize, model.SizeY * model.BlockSize, model.SizeZ * model.BlockSize, f3dPath, dir, animation, fast, yawDegrees, frames);

        public static string Script(double w, double h, double d, string f3dPath, string dir, string animation, bool fast, double yawDegrees, int frames)
        {
            var inv = CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("0.####", inv);
            double size = Math.Max(w, Math.Max(h, d));
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

        /// <summary>The pictures of a model (PNG, a frame each 1/30 s) and what went wrong (the script's output when there are none). The script runs in `fire` (the VM in C,
        /// <see cref="CVmScript"/>; docs/spec/VM_C.md) - with FIRE_SPARK_VM=dotnet (or without a `fire`) in the C# VM of this process. `Engine`: where it ran.</summary>
        public static (List<byte[]>? Frames, string Error, string Engine, double CompileMs, double RunMs) RenderFrames(byte[] f3dBytes, double w, double h, double d, string animation,
            bool fast, double yaw, int frames, System.Threading.CancellationToken cancel = default)
        {
            string dir = CVmScript.NewFolder("fire-block-preview");
            try
            {
                string f3d = Path.Combine(dir, "model.f3d");
                File.WriteAllBytes(f3d, f3dBytes);
                string script = Script(w, h, d, f3d, dir, animation, fast, yaw, frames);
                string engine, said;
                double compileMs, runMs;
                if (!CVmScript.Available) return (null, "The preview needs `fire` (the VM in C), which could not be had on this machine: " + CVmScript.Unavailable, "", 0, 0);
                var run = CVmScript.Run(script, dir, timeoutMs: 120000, cancel: cancel);
                engine = "c"; compileMs = run.CompileMs; runMs = run.RunMs;
                if (run.Failure is { } failure) return (null, failure, engine, compileMs, runMs);
                said = string.Join("\n", run.Printed);
                var list = new List<byte[]>();
                for (int i = 0; i < frames; i++)
                {
                    string png = Path.Combine(dir, "f" + i + ".png");
                    if (!File.Exists(png)) break;
                    list.Add(File.ReadAllBytes(png));
                }
                return (list, said, engine, compileMs, runMs);
            }
            catch (Exception ex) { return (null, ex.Message, "", 0, 0); }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

    }
}
