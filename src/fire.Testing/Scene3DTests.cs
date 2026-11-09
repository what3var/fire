using System.Text;
using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>The scene3d package (docs/graphics/3d.md): the program below runs in the VM and as a native build and must print the same.</summary>
static class Scene3DTests
{
    public const string Name = "Scene3D: Szene, Kamera, Objekte, Texturen, Licht, Einheiten - nativ == VM";

    /// <summary>The test model as a glTF file in the temporary folder: the script embeds it as a resource, which forge makes an .f3d when the program is built.</summary>
    private static string RobotFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "fire-scene3d-robot.gltf");
        string text = ModelTestData.RobotGltf();
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        return path.Replace("\\", "/");
    }

    /// <summary>The figure of BlockModelTests as a block model file (.fblocks): forge makes an .f3d of it when the program is built.</summary>
    private static string FigureFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "fire-scene3d-figure.fblocks");
        string text = BlockModelTests.Figure();
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        return path.Replace("\\", "/");
    }

    public static readonly string Script = ScriptText.Replace("{ROBOT}", RobotFile()).Replace("{FIGURE}", FigureFile());

    private const string ScriptText = """
        #import "graphics"
        #import "scene3d"
        #import "scene3d.terrain"

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
        print("counts " + scene.Objects.Count + " " + scene.Lights.Count + " " + scene.Cameras.Count + " " + scene.Textures.Count)
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
        print("removed " + scene.Objects.Count + " " + (scene.Get("ball") == undefined))

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

        // the hardware renderer: where there is a GPU it draws, and its picture is close to the software one; where there is none the software renderer draws the same picture
        var software = frame.ReadBytes()
        print("renderer " + scene.Renderer + " " + cam.LastRenderer)
        scene.Renderer = RenderMode.Hardware
        cam.Render()
        var hardware = frame.ReadBytes()
        var gpu = S3D.HardwareName() != ""
        var off = 0
        for (var i = 0; i < hardware.length; i++) {
            var d = hardware[i] - software[i]
            if (d > 24 || d < -24) { off++ }
        }
        if (gpu) { print("hardware " + (cam.LastRenderer == 1) + " " + (off * 100 < hardware.length) + " " + (S3D.HardwareError() == "")) }
        else { print("hardware " + (cam.LastRenderer == 0) + " " + (off == 0) + " " + (S3D.HardwareError() != "")) }
        cam.Render()
        print("again " + (frame.ReadBytes().length == software.length))
        // DirectDrawing without a window that shows the framebuffer: the camera draws as without (the picture is in the framebuffer)
        var before = H.Sum(frame)
        cam.DirectDrawing = true
        cam.Render()
        cam.Render()
        print("direct " + cam.DrewDirectly + " " + (H.Sum(frame) == before))
        cam.DirectDrawing = false
        cam.Render()
        print("indirect " + cam.DrewDirectly + " " + (H.Sum(frame) == before))
        scene.Renderer = RenderMode.Software
        cam.Render()
        print("software " + (H.Sum(frame) == H.Sum(frame)) + " " + cam.LastRenderer)

        // the lean renderer (RenderMode.Fast): light per corner, bands, 16-bit depth, a palette picture, the shadow of one light, dithered transparency
        var fastFrame = new Framebuffer(64, 48)
        var fastCam = new Camera(fastFrame)
        fastCam.SetPosition(0m, 2m, 5m)
        fastCam.LookAt(0m, 0.5m, 0m)
        scene.Add(fastCam)
        scene.Renderer = RenderMode.Fast
        scene.Shadows = false
        fastCam.Render()
        print("fast " + H.Sum(fastFrame) + " " + fastCam.LastRenderer)
        scene.Shadows = true
        scene.ShadowResolution = 128
        fastCam.Render()
        var fastShadow = H.Sum(fastFrame)
        print("fast shadows " + fastShadow)
        var palFrame = new Framebuffer(64, 48, ColorMode.Palette!int)
        var palCam = new Camera(palFrame)
        palCam.SetPosition(0m, 2m, 5m)
        palCam.LookAt(0m, 0.5m, 0m)
        scene.Add(palCam)
        palCam.Render()
        print("fast palette " + H.Sum(palFrame) + " " + palFrame.ReadBytes().length)
        glass.Opacity = 0.3
        fastCam.Render()
        print("fast dither " + (H.Sum(fastFrame) != fastShadow))
        scene.Renderer = RenderMode.Software
        scene.ShadowResolution = 1024

        // terrains (scene3d.terrain): smooth and in blocks, the height of the ground, from an array and from a picture
        var hills = new byte[8 * 8]
        for (var i = 0; i < 64; i++) { hills[i] = ((i % 8) * (i / 8)) % 7 }
        var land = new Terrain(hills, 8, 8, 50cm, 10cm)
        land.Z = -2m
        var steps = new Terrain(hills, 8, 8, 50cm, 10cm, TerrainMode.Hard)
        steps.X = 3m
        steps.RotationY = 90deg
        scene.Add(land)
        scene.Add(steps)
        print("terrain " + land.Triangles + " " + steps.Triangles + " " + land.Width + " " + steps.Width + " " + (steps.Mode == TerrainMode.Hard))
        print("ground " + land.HeightAt(0m, -2m) + " " + land.HeightAt(0.3m, -1.6m) + " " + steps.HeightAt(3.2m, 0.6m) + " " + (land.HeightAt(10m, 0m) == undefined))
        var ramp = new Terrain([0, 1, 2, 3, 0, 1, 2, 3], 4, 2, 1m, 1m)
        print("ramp " + ramp.HeightAt(0m, 0m) + " " + ramp.HeightAt(-1m, 0.2m) + " " + ramp.Height)
        var picture = new Framebuffer(4, 4)
        var white = new byte[64]
        for (var i = 0; i < 64; i++) { white[i] = 255 }
        picture.WriteBytes(white)
        var lit = Terrain.FromImage(picture, 1m, 1cm, TerrainMode.Hard)
        print("image " + lit.Triangles + " " + lit.HeightAt(0m, 0m))
        cam.Render()
        print("terrain picture " + H.Sum(frame))
        scene.Renderer = RenderMode.Fast
        cam.Render()
        print("terrain fast " + H.Sum(frame))
        scene.Renderer = RenderMode.Software
        try { var bad = new Terrain(hills, 3, 3) } catch (Scene3DException e) { print("error " + e.message) }

        // a model file (.f3d, made from glTF by forge): parts in a tree with their colours, animations
        var robotModel = Model.FromResource(new Resource("{ROBOT}"))
        print("model " + robotModel.Parts + " " + robotModel.Triangles + " " + robotModel.Animations.Count + " " + robotModel.Animations[0] + " " + robotModel.Animations[1] + " " + robotModel.AnimationLength("wave") + " " + robotModel.AnimationLength("jump"))
        var robot = new Object(robotModel)
        robot.SetPosition(-1.5m, 0m, 1m)
        robot.AnchorY = 0.0
        scene.Add(robot)
        print("robot " + robot.Width + " " + robot.Height + " " + robot.Triangles + " '" + robot.Animation + "' " + robot.Playing)
        scene.Renderer = RenderMode.Software
        cam.Render()
        var rest = H.Sum(frame)
        robot.Play("wave")
        robot.Advance(500ms)
        cam.Render()
        var waved = H.Sum(frame)
        print("wave " + robot.Animation + " " + robot.Playing + " " + robot.AnimationTime + " " + (waved != rest))
        robot.Advance(0.75s)
        print("loop " + robot.AnimationTime)
        robot.Play("jump", false)
        robot.AnimationTime = 250ms
        cam.Render()
        print("jump " + H.Sum(frame) + " " + robot.AnimationTime)
        robot.Advance(1s)
        print("end " + robot.Playing + " " + robot.AnimationTime)
        robot.Play("")
        cam.Render()
        print("rest " + (H.Sum(frame) == rest))
        robot.Play("wave")
        robot.AnimationTime = 0.5s
        robot.Stop()
        robot.Advance(1s)
        print("stopped " + robot.Playing + " " + robot.AnimationTime)
        scene.Renderer = RenderMode.Fast
        cam.Render()
        print("model fast " + H.Sum(frame))
        scene.Renderer = RenderMode.Software
        var figure = new Object(Model.FromResource(new Resource("{FIGURE}")))
        figure.SetPosition(1.5m, 0m, 1.5m)
        figure.AnchorY = 0.0
        scene.Add(figure)
        print("blocks " + figure.Width + " " + figure.Height + " " + figure.Triangles + " " + Model.FromResource(new Resource("{FIGURE}")).Parts)
        cam.Render()
        print("blocks picture " + H.Sum(frame))
        scene.Remove(figure)
        try { robot.Play("dance") } catch (Scene3DException e) { print("error " + e.message) }
        try { Model.FromBytes(new byte[10]) } catch (Scene3DException e) { print("error " + e.message) }
        try { robot.Advance(3) } catch (UnitMismatchException e) { print("unit " + e.message) }

        // lengths and angles need units (a length property converts and refuses a number without a unit itself)
        try { box.X = 3 } catch (UnitMismatchException e) { print("unit " + e.message) }
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
        Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
        return string.Join("\n", lines) + (lines.Count > 0 ? "\n" : "");
    }

    /// <summary>A quick run while working on the package: FIRE_TESTS_ONLY=scene3d prints what the VM prints and what the C++ build prints.</summary>
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("FIRE_SCENE_FILE") is { Length: > 0 } file) { Console.WriteLine(Vm(File.ReadAllText(file))); return; }
        // (FIRE_SCENE_DUMP=folder: writes the script and the test model there - scene3d.fire, robot.gltf, robot.f3d)
        if (Environment.GetEnvironmentVariable("FIRE_SCENE_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, "scene3d.fire"), Script);
            File.WriteAllText(Path.Combine(dump, "robot.gltf"), ModelTestData.RobotGltf());
            File.WriteAllBytes(Path.Combine(dump, "robot.f3d"), ModelTestData.RobotF3d());
            return;
        }
        string vm = Vm(Script);
        Console.WriteLine("--- VM ---\n" + vm);
        string work = Path.Combine(Path.GetTempPath(), "fire-scene3d-" + Guid.NewGuid().ToString("N"));
        fire.Native.NativeRuntimeFiles.WriteTo(work);
        var generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { Script }, null, null, VmExecutionMode.Release));
        string Tool(string tool, string arguments)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = work })!;
            var err = p.StandardError.ReadToEndAsync();
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o + err.Result;
        }
        string build = NativeTestBuild.Build(NativeTestBuild.In(work), "g++", generated, work, "case", "-pthread -O2 -Wall -Wextra", Path.Combine(work, "case.bin"), out _);
        if (build.Length > 0) Console.WriteLine("--- build ---\n" + build);
        string native = File.Exists(Path.Combine(work, "case.bin")) ? Tool(Path.Combine(work, "case.bin"), "") : "(no binary)";
        Console.WriteLine("--- C++ ---\n" + native);
        Console.WriteLine(vm == native ? "SAME" : "DIFFERENT");
    }
}
