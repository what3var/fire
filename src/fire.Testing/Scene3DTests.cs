using System.Text;
using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>The scene3d package (docs/SCENE3D.md): the program below runs in the VM and as a native build and must print the same.</summary>
static class Scene3DTests
{
    public const string Name = "Scene3D: Szene, Kamera, Objekte, Texturen, Licht, Einheiten - nativ == VM";

    public const string Script = """
        #import "graphics"
        #import "scene3d"

        // a checksum of the picture (the same in the VM and in C++)
        class H {
            static int Sum(Framebuffer f) {
                var b = f.ReadBytes()
                var s = 0
                for (var i = 0; i < b.length; i++) { s = (s * 31 + b[i]) % 1000003 }
                return s
            }
        }

        var frame = new Framebuffer(64, 48)
        var scene = new Scene()

        // objects from templates: x, y, z, width, height, depth
        var box = new Object(Template.Box, 0m, 0.5m, 0m, 1m, 1m, 1m)
        box.RotationY = 30deg
        scene.Add(box, "crate")
        var ball = new Object(Template.Sphere, 1.5m, 0.4m, 0m, 80cm, 80cm, 80cm)
        ball.Color = S3D.Rgb(220, 40, 40)
        ball.Material = Material.Metal()
        scene.Add(ball, "ball")
        var floor = new Object(Template.Plane, 0m, 0m, 0m, 6m, 0m, 6m)
        floor.Texture = Texture.Checker(32, 4)
        floor.TextureWidth = 1m
        floor.TextureHeight = 1m
        scene.Add(floor, "floor")
        scene.Add(Light.Directional(-0.4, -1.0, -0.6))
        scene.Add(Light.Point(2m, 3m, 2m, S3D.Rgb(255, 240, 200), 0.8))

        print("sizes " + box.Width + " " + ball.Width + " " + (ball.Height / 1cm) + " " + floor.Depth)
        print("pos " + box.X + " " + box.Y + " " + ball.X)
        print("rot " + box.RotationY + " " + (box.RotationY is in deg))
        print("counts " + scene.Objects.count + " " + scene.Lights.count + " " + scene.Cameras.count + " " + scene.Textures.count)
        print("keys " + (scene.Get("ball") == ball) + " " + (scene.Get("nothing") == undefined))
        print("triangles " + box.Triangles + " " + floor.Triangles)

        // a size that is set becomes the scale of the axis
        box.Height = 2m
        print("scaled " + box.Height + " " + box.ScaleY + " " + box.Width)

        var cam = new Camera(frame)
        cam.X = 0m
        cam.Y = 2m
        cam.Z = 5m
        cam.RotationX = -20deg
        print("cam " + cam.Y + " " + cam.RotationX)
        scene.Add(cam)
        cam.Render()
        var first = H.Sum(frame)
        print("picture " + first)
        cam.Render()
        print("same " + (H.Sum(frame) == first))

        // the corner of the picture is the background, the middle is the box
        var px = frame.ReadBytes()
        print("corner " + px[0] + "," + px[1] + "," + px[2] + "," + px[3])

        // things that change the picture
        cam.LookAt(0m, 0.5m, 0m)
        cam.Render()
        var looked = H.Sum(frame)
        print("lookat " + (looked != first))
        box.Visible = false
        cam.Render()
        print("hidden " + (H.Sum(frame) != looked))
        box.Visible = true
        box.Texture = Texture.Checker(16, 2, S3D.Rgb(255, 255, 0), S3D.Rgb(0, 0, 255))
        box.TextureRotation = 45deg
        cam.Render()
        print("textured " + H.Sum(frame))
        box.AnchorY = 0.0
        box.Y = 0m
        cam.Render()
        print("anchored " + H.Sum(frame))
        scene.Remove(ball)
        print("removed " + scene.Objects.count + " " + (scene.Get("ball") == undefined))

        // an object from triangles and from an OBJ text
        var tri = new Object()
        tri.AddTriangle(0m, 0m, 0m, 1m, 0m, 0m, 0m, 1m, 0m)
        print("tri " + tri.Triangles + " " + tri.Width + " " + tri.Height)
        var obj = new Object("v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n")
        print("obj " + obj.Triangles + " " + obj.Width + " " + obj.Depth)

        // transparency, billboards, shadows, layers, bump maps
        var glass = new Object(Template.Box, 0m, 0.5m, 1.5m, 1m, 1m, 0.2m)
        glass.Color = S3D.Rgb(80, 160, 255)
        glass.Opacity = 0.5
        scene.Add(glass, "glass")
        cam.LookAt(0m, 0.5m, 0m)
        cam.Render()
        var withGlass = H.Sum(frame)
        glass.Visible = false
        cam.Render()
        print("transparent " + (withGlass != H.Sum(frame)))
        glass.Visible = true
        var sprite = new Object(Template.Quad, -1.5m, 0.5m, 0.5m, 1m, 1m, 0m)
        sprite.Texture = Texture.Checker(8, 2, S3D.Rgb(255, 0, 0), S3D.Rgb(255, 255, 255))
        sprite.Billboard = BillboardMode.Facing
        sprite.CastShadows = false
        scene.Add(sprite, "sprite")
        cam.Render()
        var billboard1 = H.Sum(frame)
        cam.X = 2m
        cam.LookAt(0m, 0.5m, 0m)
        cam.Render()
        print("billboard " + billboard1 + " " + H.Sum(frame))
        scene.Shadows = true
        cam.Render()
        var shadowed = H.Sum(frame)
        scene.Shadows = false
        cam.Render()
        print("shadows " + (shadowed != H.Sum(frame)) + " " + scene.Shadows)
        scene.Shadows = true
        scene.Threads = 1
        cam.Render()
        var oneThread = H.Sum(frame)
        scene.Threads = 3
        cam.Render()
        print("threads " + (oneThread == H.Sum(frame)) + " " + (oneThread == shadowed))
        scene.Threads = 0
        floor.Layers = 0
        cam.Render()
        var layered = H.Sum(frame)
        print("layers " + (layered != shadowed))
        floor.Layers = 16777215
        var sun = scene.Lights[0]
        sun.Enabled = false
        cam.Render()
        var dark = H.Sum(frame)
        sun.Enabled = true
        print("light " + (dark != shadowed))
        floor.Lit = false
        cam.Render()
        print("unlit " + (H.Sum(frame) != shadowed))
        floor.Lit = true
        var bumpy = new Object(Template.Plane, 0m, 0.01m, 0m, 2m, 0m, 2m)
        bumpy.Texture = Texture.Checker(16, 4, S3D.Rgb(200, 200, 200), S3D.Rgb(60, 60, 60))
        scene.Add(bumpy, "bumpy")
        cam.Render()
        var plain = H.Sum(frame)
        bumpy.Texture.AddBumpmap()
        bumpy.Texture.BumpStrength = 2.0
        cam.Render()
        print("bump " + (H.Sum(frame) != plain) + " " + bumpy.Texture.hasBump)
        var heights = new byte[16 * 16]
        for (var i = 0; i < 256; i++) { heights[i] = (i * 7) % 256 }
        bumpy.Texture.AddBumpmap(16, 16, heights)
        cam.Render()
        print("bumpdata " + H.Sum(frame))
        try { bumpy.Texture.AddBumpmap(3, 3, heights) } catch (Scene3DException e) { print("error " + e.message) }

        // lengths and angles need units
        try { box.X = 3 } catch (Scene3DException e) { print("error " + e.message) }
        try { box.RotationX = 1.5 } catch (Scene3DException e) { print("error " + e.message) }
        try { scene.Add(3) } catch (Scene3DException e) { print("error " + e.message) }
        try { scene.Add(floor, "ball") ; scene.Add(box, "floor") } catch (Scene3DException e) { print("error " + e.message) }
        var lone = new Camera(new Framebuffer(4, 4))
        try { lone.Render() } catch (Scene3DException e) { print("error " + e.message) }
        """;

    private static string Vm(string source)
    {
        var lines = new List<string>();
        var session = RuntimeSession.Build(new[] { source }, VmExecutionMode.Release, args => { lines.Add(args[0].ToString()!); return Value.MakeUndefined(); });
        session.Run();
        Value.SingleFloats = false;
        return string.Join("\n", lines) + (lines.Count > 0 ? "\n" : "");
    }

    /// <summary>A quick run while working on the package: FIRE_TESTS_ONLY=scene3d prints what the VM prints and what the C++ build prints.</summary>
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("FIRE_SCENE_FILE") is { Length: > 0 } file) { Console.WriteLine(Vm(File.ReadAllText(file))); return; }
        string vm = Vm(Script);
        Console.WriteLine("--- VM ---\n" + vm);
        string work = Path.Combine(Path.GetTempPath(), "fire-scene3d-" + Guid.NewGuid().ToString("N"));
        fire.Native.NativeRuntimeFiles.WriteTo(work);
        string cpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { Script }, null, null, VmExecutionMode.Release));
        File.WriteAllText(Path.Combine(work, "case.cpp"), cpp);
        string Tool(string tool, string arguments)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = work })!;
            var err = p.StandardError.ReadToEndAsync();
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o + err.Result;
        }
        string build = Tool("g++", $"-std=c++17 -pthread -O2 -Wall -Wextra \"{Path.Combine(work, "case.cpp")}\" -I\"{work}\" -o \"{Path.Combine(work, "case.bin")}\"");
        if (build.Length > 0) Console.WriteLine("--- build ---\n" + build);
        string native = File.Exists(Path.Combine(work, "case.bin")) ? Tool(Path.Combine(work, "case.bin"), "") : "(no binary)";
        Console.WriteLine("--- C++ ---\n" + native);
        Console.WriteLine(vm == native ? "SAME" : "DIFFERENT");
    }
}
