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

    /// <summary>The house of MeshModelTests as a mesh model file (.fmesh, the CAD editor's): forge makes an .f3d of it, too.</summary>
    private static string HouseFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "fire-scene3d-house.fmesh");
        string text = MeshModelTests.House();
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
        return path.Replace("\\", "/");
    }

    public static readonly string Script = ScriptText.Replace("{ROBOT}", RobotFile()).Replace("{FIGURE}", FigureFile()).Replace("{HOUSE}", HouseFile());

    private const string ScriptText = """
        #import "graphics"
        #import "scene3d"
        #import "scene3d.terrain"
        #import "scene3d.effects"
        #import "scene3d.physics"

        // (two places after the point)
        class P { static string F(any v) { return "" + Math.Floor(v / 1m * 100.0 + 0.5) / 100.0 } }
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
        var figureModel = Model.FromResource(new Resource("{FIGURE}"))
        print("blocks animations " + figureModel.Animations.Count + " " + figureModel.Animations[0] + " " + figureModel.AnimationLength("nod") + " " + figureModel.AnimationLength("hop"))
        figure.Play("nod")
        figure.Advance(250ms)
        cam.Render()
        print("blocks nod " + H.Sum(frame) + " " + figure.Playing)
        figure.Play("hop")
        figure.Advance(1s)
        cam.Render()
        print("blocks hop " + figure.Playing + " " + figure.AnimationTime + " " + H.Sum(frame))
        scene.Remove(figure)

        // a mesh model (.fmesh, the CAD editor's): a house whose roof is a part that nods
        var house = new Object(Model.FromResource(new Resource("{HOUSE}")))
        house.SetPosition(0m, 0m, 1m)
        scene.Add(house)
        print("mesh " + P.F(house.Width) + " " + P.F(house.Height) + " " + house.Triangles + " " + Model.FromResource(new Resource("{HOUSE}")).Parts)
        cam.Render()
        print("mesh picture " + H.Sum(frame))
        house.Play("nod")
        house.Advance(250ms)
        cam.Render()
        print("mesh nod " + H.Sum(frame) + " " + house.Playing)
        scene.Remove(house)

        // effects (scene3d.effects): particles (the same in the VM and native: the emitter's own chance), fog, tint, spin, a texture that runs
        var flames = Emitter.Fire(0m, 1m, 1m)
        scene.Add(flames)
        var smoke = Emitter.Smoke(-1m, 1m, 0m)
        scene.Add(smoke)
        var boom = Emitter.Explosion(1m, 1m, 1m, 40)
        scene.Add(boom)
        for (var i = 0; i < 20; i++) { scene.Advance(50ms) }
        print("particles " + flames.Count + " " + smoke.Count + " " + boom.Count + " " + flames.Running + " " + boom.Running + " " + flames.Lifetime + " " + flames.Speed + " " + flames.Spread)
        box.SpinY = 90deg
        floor.TextureScrollX = 50cm / 1s
        scene.Advance(500ms)
        print("spin " + box.RotationY + " " + floor.TextureX + " " + box.SpinY)
        boom.Burst(30)
        scene.Advance(100ms)
        cam.Render()
        print("effects software " + H.Sum(frame))
        scene.Renderer = RenderMode.Fast
        cam.Render()
        print("effects fast " + H.Sum(frame))
        palCam.Render()
        print("effects palette " + H.Sum(palFrame))
        scene.Fog(S3D.Rgb(150, 160, 180), 3m, 8m)
        cam.Render()
        print("fog fast " + H.Sum(frame))
        palCam.Render()
        print("fog palette " + H.Sum(palFrame))
        scene.Renderer = RenderMode.Software
        cam.Render()
        print("fog software " + H.Sum(frame) + " " + scene.FogStart + " " + scene.FogEnd)
        scene.Tint(S3D.Rgb(0, 0, 60), 0.3)
        cam.Render()
        print("tint " + H.Sum(frame) + " " + scene.TintAmount)
        scene.NoFog()
        scene.Tint(0, 0.0)
        boom.Clear()
        smoke.Running = false
        smoke.Seed = 7
        print("cleared " + boom.Count + " " + smoke.Running + " " + smoke.Seed)
        scene.Remove(flames)
        scene.Remove(smoke)
        scene.Remove(boom)
        try { flames.Speed = 3 } catch (UnitMismatchException e) { print("unit " + e.message) }

        // physics (scene3d.physics): a ball bounces, crates stack, a character walks up a step, stops at a wall, jumps; a trigger, rays, a terrain, a lift
        var world = new Scene()
        var pFloor = new Object(Template.Plane, 0m, 0m, 0m, 20m, 0m, 20m)
        world.Add(pFloor)
        Body.Static(pFloor)
        var pStep = new Object(Template.Box, 3m, 0.1m, 0m, 2m, 0.2m, 4m)
        world.Add(pStep)
        Body.Static(pStep)
        var pWall = new Object(Template.Box, 3m, 0.5m, 3m, 2m, 1m, 1m)
        world.Add(pWall)
        Body.Static(pWall)
        var pBall = new Object(Template.Sphere, -2m, 3m, 0m, 0.5m, 0.5m, 0.5m)
        world.Add(pBall)
        var bb = Body.Ball(pBall, 1kg).Bouncy()
        var pCrate = new Object(Template.Box, -4m, 2m, 0m, 1m, 1m, 1m)
        world.Add(pCrate)
        var cb = Body.Dynamic(pCrate, 5kg)
        var pCrate2 = new Object(Template.Box, -4.2m, 4m, 0m, 1m, 1m, 1m)
        world.Add(pCrate2)
        Body.Dynamic(pCrate2, 5kg)
        var pHero = new Object(Template.Box, 0m, 0m, 0m, 0.6m, 1.8m, 0.6m)
        pHero.AnchorY = 0.0
        world.Add(pHero)
        var ch = new Character(pHero, 30cm, 1.8m)
        var pGoal = new Object(Template.Box, 4m, 1m, 0m, 1m, 2m, 1m)
        world.Add(pGoal)
        Body.Static(pGoal, BodyShape.Box).Trigger = true
        print("kinds " + bb.Kind + " " + bb.Shape + " " + ch.Kind + " " + ch.Shape + " " + bb.Mass + " " + ch.StepHeight + " " + ch.MaxSlope)
        for (var i = 0; i < 30; i++) { world.Advance(1s / 30.0) }
        print("ball " + P.F(pBall.Y) + " " + P.F(bb.VelocityY * 1s) + " crate " + P.F(pCrate.Y) + " " + P.F(pCrate2.Y) + " " + P.F(pCrate2.X) + " hero " + P.F(pHero.Y) + " " + ch.OnGround)
        for (var i = 0; i < 90; i++) { world.Advance(1s / 30.0) }
        print("rest " + P.F(pBall.Y) + " crate " + P.F(pCrate.Y) + " " + P.F(pCrate2.Y) + " " + cb.OnGround)
        ch.Walk(2m / 1s, 0m / 1s)
        for (var i = 0; i < 60; i++) { world.Advance(1s / 30.0) }
        print("walked " + P.F(pHero.X) + " " + P.F(pHero.Y) + " " + ch.OnGround + " in goal " + ch.Touches(pGoal))
        ch.Walk(0m / 1s, 2m / 1s)
        for (var i = 0; i < 60; i++) { world.Advance(1s / 30.0) }
        print("wall " + P.F(pHero.X) + " " + P.F(pHero.Y) + " " + P.F(pHero.Z))
        ch.Walk(0m / 1s, 0m / 1s)
        print("jump " + ch.Jump(5m / 1s))
        world.Advance(1s / 30.0)
        for (var i = 0; i < 10; i++) { world.Advance(1s / 30.0) }
        print("up " + P.F(pHero.Y) + " " + ch.OnGround + " " + ch.Jump())
        var pHit = world.Raycast(-2m, 5m, 0m, 0.0, -1.0, 0.0)
        print("ray " + pHit.Object.Triangles + " " + P.F(pHit.Distance) + " " + P.F(pHit.Y) + " " + pHit.NormalY)
        var pHit2 = world.Raycast(3m, 5m, 0m, 0.0, -1.0, 0.0)
        print("ray2 " + (pHit2.Object == pStep) + " " + P.F(pHit2.Y) + " none " + (world.Raycast(0m, 5m, 0m, 0.0, 1.0, 0.0) == undefined))
        // a terrain: a pBall rolls down a slope
        var pHeights = new byte[16 * 16]
        for (var z = 0; z < 16; z++) { for (var x = 0; x < 16; x++) { pHeights[z * 16 + x] = x * 10 } }
        var pLand = new Terrain(pHeights, 16, 16, 1m, 5cm)
        pLand.Z = 30m
        world.Add(pLand)
        Body.Static(pLand)
        var pRock = new Object(Template.Sphere, 3m, 10m, 30m, 0.6m, 0.6m, 0.6m)
        world.Add(pRock)
        var rb = Body.Ball(pRock, 2kg)
        for (var i = 0; i < 45; i++) { world.Advance(1s / 30.0) }
        print("rock " + P.F(pRock.X) + " " + P.F(pRock.Y) + " ground " + P.F(pLand.HeightAt(pRock.X, pRock.Z)) + " " + rb.OnGround)
        // a pLift: kinematic, it carries a pCrate up
        var pLift = new Object(Template.Box, 8m, 0.1m, 0m, 2m, 0.2m, 2m)
        world.Add(pLift)
        var lb = Body.Kinematic(pLift)
        lb.SetVelocity(0m / 1s, 1m / 1s, 0m / 1s)
        var pBox3 = new Object(Template.Box, 8m, 1m, 0m, 0.8m, 0.8m, 0.8m)
        world.Add(pBox3)
        Body.Dynamic(pBox3)
        for (var i = 0; i < 60; i++) { world.Advance(1s / 30.0) }
        print("lift " + P.F(pLift.Y) + " box " + P.F(pBox3.Y))
        world.Gravity = 0m / 1s / 1s
        print("gravity " + world.Gravity)
        // turning bodies (a crate falls turned and lies down flat, a stack of three stands, a ball rolls), sleeping, joints (a pendulum on a rope, a spring)
        var spin = new Scene()
        var spinFloor = new Object(Template.Plane, 0m, 0m, 0m, 40m, 0m, 40m)
        spin.Add(spinFloor)
        Body.Static(spinFloor)
        var tumbler = new Object(Template.Box, 0m, 3m, 0m, 1m, 1m, 1m)
        tumbler.SetRotation(30deg, 20deg, 40deg)
        spin.Add(tumbler)
        var tb = Body.Dynamic(tumbler, 5kg)
        var st1 = new Object(Template.Box, 5m, 0.5m, 0m, 1m, 1m, 1m)
        var st2 = new Object(Template.Box, 5.1m, 1.5m, 0m, 1m, 1m, 1m)
        var st3 = new Object(Template.Box, 4.9m, 2.5m, 0m, 1m, 1m, 1m)
        spin.Add(st1)
        spin.Add(st2)
        spin.Add(st3)
        Body.Dynamic(st1, 5kg)
        Body.Dynamic(st2, 5kg)
        var sb3 = Body.Dynamic(st3, 5kg)
        var roller = new Object(Template.Sphere, -5m, 0.25m, 0m, 0.5m, 0.5m, 0.5m)
        spin.Add(roller)
        var rl = Body.Ball(roller)
        rl.SetVelocity(3m / 1s, 0m / 1s, 0m / 1s)
        var bob = new Object(Template.Box, 12m, 5m, 0m, 0.4m, 0.4m, 0.4m)
        spin.Add(bob)
        Body.Dynamic(bob, 1kg)
        var rope = Joint.Rope(spin, bob, 10m, 5m, 0m)
        var sp1 = new Object(Template.Sphere, -10m, 5m, 0m, 0.4m, 0.4m, 0.4m)
        var sp2 = new Object(Template.Sphere, -8m, 5m, 0m, 0.4m, 0.4m, 0.4m)
        spin.Add(sp1)
        spin.Add(sp2)
        Body.Ball(sp1).GravityScale = 0.0
        Body.Ball(sp2).GravityScale = 0.0
        var spring = Joint.Spring(spin, sp1, sp2, 50.0, 1.0, 1m)
        print("turning " + tb.Rotates + " " + rl.Rotates + " " + new Body(pFloor).Rotates + " " + P.F(rope.Length) + " " + spring.Stiffness)
        for (var i = 0; i < 150; i++) {
            spin.Advance(1s / 30.0)
            if (i % 50 == 49) {
                print(i + " tumbler " + P.F(tumbler.Y) + " " + P.F(tumbler.RotationX / 1deg * 1m) + " " + P.F(tumbler.RotationZ / 1deg * 1m) + " " + tb.Sleeping
                    + " stack " + P.F(st2.Y) + " " + P.F(st3.Y) + " " + P.F(st3.X) + " " + sb3.Sleeping + " roller " + P.F(roller.X)
                    + " bob " + P.F(bob.X) + " " + P.F(bob.Y) + " " + P.F(rope.Distance) + " spring " + P.F(spring.Distance))
            }
        }
        rl.AngularVelocityY = 90deg
        print("spun " + P.F(rl.AngularVelocityY / 1deg * 1m) + " " + rl.Sleeping)
        rope.Remove()
        tb.Sleep()
        print("removed " + tb.Sleeping)
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
        // (FIRE_SCENE_DUMP=folder: writes the script and the test models there - scene3d.fire, robot.gltf, robot.f3d, figure.fblocks, house.fmesh)
        if (Environment.GetEnvironmentVariable("FIRE_SCENE_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, "scene3d.fire"), Script);
            File.WriteAllText(Path.Combine(dump, "robot.gltf"), ModelTestData.RobotGltf());
            File.WriteAllBytes(Path.Combine(dump, "robot.f3d"), ModelTestData.RobotF3d());
            File.WriteAllText(Path.Combine(dump, "figure.fblocks"), BlockModelTests.Figure());
            File.WriteAllText(Path.Combine(dump, "house.fmesh"), MeshModelTests.House());
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
        Console.WriteLine("dos16 C: " + Dos16Functions());
    }

    /// <summary>The C of the script for dos16 (no compiler needed): every function is defined once, and every `fN_b` / `fN_p` that is declared is defined - a target that
    /// counts the call depth wraps each function (a method with a C body of `defined` numbers once got its Value wrapper under the wrong name).</summary>
    private static string Dos16Functions()
    {
        if (!TargetProfile.TryGet("dos16", out var dos16)) return "(no profile)";
        var linked = new Linker().CompileAndLink(new[] { Script }, null, null, VmExecutionMode.Release, null, dos16);
        string c = fire.Native.CppGenerator.Generate(linked, dos16);
        var defined = System.Text.RegularExpressions.Regex.Matches(c, @"^(?:FIRE_MAYBE_UNUSED )?static Value (f\d+(?:_b|_p)?)\([^;\n]*\)\r?$", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var declared = System.Text.RegularExpressions.Regex.Matches(c, @"^(?:FIRE_MAYBE_UNUSED )?static Value (f\d+(?:_b|_p))\([^\n]*\);", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct().ToList();
        var twice = defined.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var missing = declared.Where(n => !defined.Contains(n)).ToList();
        return twice.Count == 0 && missing.Count == 0 ? $"OK ({defined.Count} functions)" : "FEHLER - twice: " + string.Join(", ", twice) + "; never defined: " + string.Join(", ", missing);
    }
}
