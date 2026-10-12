namespace fire.Standard
{
    /// <summary>
    /// `#import "scene3d"`: 3D scenes drawn by a software renderer (docs/SCENE3D.md). The renderer is C++ (native/bridges/fire_bridge_scene3d.hpp), in the VM as well as in a native build, so a
    /// picture is the same everywhere; this fire source holds the scene (objects, cameras, lights, textures) and converts the units. Lengths are metric (`1.5m`, `20cm`, `3mm`), angles are `deg` or
    /// `rad`; a plain number where a length or an angle belongs is an error, so no unit is ever guessed.
    ///
    ///   var frame = new Framebuffer(640, 480)
    ///   var scene = new Scene()
    ///   var box = new Object(Template.Box, 0m, 0.5m, 0m, 1m, 1m, 1m)      // x, y, z of the position, then width, height, depth
    ///   box.RotationY = 30deg
    ///   scene.Add(box, "crate")
    ///   var cam = new Camera(frame)
    ///   cam.X = 0m; cam.Y = 1.5m; cam.Z = 4m
    ///   cam.LookAt(0m, 0.5m, 0m)
    ///   scene.Add(cam)
    ///   cam.Render()                                                          // the view of the camera is in `frame`
    ///
    /// The space is right handed, Y is up, a camera without rotation looks along -Z. An object has a position and an anchor: the anchor is the point of the object (as a fraction of its width,
    /// height and depth: 0.5, 0.5, 0.5 is the centre, 0.5, 0, 0.5 the middle of the bottom) that sits at the position. Width, Height and Depth are the outer dimensions; setting one scales the
    /// object on that axis. A texture or a colour, and a material, decide how it looks; the texture has a size of its own (how much of the object one copy covers), a position and a rotation.
    /// </summary>
    public static class Scene3DPrelude
    {
        public const string Source = """
            enum Template { Box = 0, Sphere = 1, Cylinder = 2, Cone = 3, Plane = 4 }
            enum LightKind { Ambient = 0, Directional = 1, Point = 2 }

            class Scene3DException : Exception {
                construct(string message) : base(message) { }
            }

            // Units: lengths in meters, angles in radians (the units of the language convert, a number without a unit is refused).
            class S3D {
                static float Length(v) {
                    if (!(v is in m)) { throw new Scene3DException("A length needs a unit of length (m, cm, mm, ...), got: " + v) }
                    var f = v * 1.0
                    return (f:m) / 1m
                }

                static float Angle(v) {
                    if (v is in deg) {
                        var d = v * 1.0
                        return ((d:deg) / 1deg) * 0.017453292519943295
                    }
                    if (v is in rad) {
                        var r = v * 1.0
                        return (r:rad) / 1rad
                    }
                    throw new Scene3DException("An angle needs a unit (deg or rad), got: " + v)
                }

                static Meters(float m) { return m * 1m }
                static Degrees(float radians) { return (radians * 57.29577951308232) * 1deg }

                // The colour 0xAABBGGRR as three fractions.
                static float Red(int c) { return (c & 255) / 255.0 }
                static float Green(int c) { return ((c >> 8) & 255) / 255.0 }
                static float Blue(int c) { return ((c >> 16) & 255) / 255.0 }

                // A colour from its parts (0..255 each).
                static int Rgb(int r, int g, int b) { return r + g * 256 + b * 65536 + 255 * 16777216 }
            }

            // The triangles of an object, to be shared by objects: from a template, from the text of a Wavefront OBJ file, or from arrays.
            class Model {
                int handle

                // (the handle of a mesh: use FromObj, FromArrays or FromTemplate)
                construct(int handle) { this.handle = handle }

                destruct() {
                    if (this.handle > 0) { __S3DMeshFree(this.handle) }
                }

                // `v`, `vt` and `f` lines (polygons are cut into triangles); the normals are computed. The unit of the coordinates is the meter.
                static FromObj(string text) {
                    var h = __S3DMeshFromObj(text)
                    if (h == 0) { throw new Scene3DException("The text holds no triangles (a Wavefront OBJ file with v and f lines is expected).") }
                    return new Model(h)
                }

                // Positions: x, y, z per vertex (meters); uvs: u, v per vertex (or an empty array); indices: three per triangle, counter clockwise seen from outside.
                static FromArrays(positions, uvs, indices) {
                    var h = __S3DMeshNew(positions, uvs, indices)
                    if (h == 0) { throw new Scene3DException("The arrays do not make a mesh (3 numbers per vertex, 2 per texture coordinate, 3 indices per triangle, indices below the number of vertices).") }
                    return new Model(h)
                }

                // A box, sphere, cylinder, cone or plane of the size 1m (a plane is 1m x 1m); `detail` is the number of segments around a round shape.
                static FromTemplate(int template, int detail = 24) {
                    var h = __S3DMeshTemplate(template, detail)
                    if (h == 0) { throw new Scene3DException("Unknown template: " + template) }
                    return new Model(h)
                }

                int Triangles { get { return __S3DMeshTriangleCount(this.handle) } }
            }

            // How a surface answers to light: Specular is the strength of the highlights (0 = matt), Shininess how small they are, Smooth shades with the interpolated normals (false: every triangle
            // flat), TwoSided draws both sides of a triangle, Emissive is a colour that the surface gives off by itself.
            class Material {
                float Specular
                float Shininess
                bool Smooth
                bool TwoSided
                int Emissive

                construct(float specular = 0.0, float shininess = 32.0, bool smooth = true, bool twoSided = false, int emissive = 0) {
                    this.Specular = specular
                    this.Shininess = shininess
                    this.Smooth = smooth
                    this.TwoSided = twoSided
                    this.Emissive = emissive
                }

                static Matte() { return new Material(0.0, 32.0) }
                static Plastic() { return new Material(0.4, 32.0) }
                static Metal() { return new Material(0.9, 96.0) }
            }

            // A picture to put on objects (RGBA). It belongs to a scene (scene.Add(texture, "key")) or is assigned to an object directly.
            class Texture {
                int handle
                int width
                int height

                // From a framebuffer in the colour mode Rgba (the pixels are copied).
                construct(Framebuffer source) {
                    this.handle = 0
                    this.width = source.Width()
                    this.height = source.Height()
                    this.handle = __S3DTexNew(this.width, this.height, source.ReadBytes())
                    if (this.handle == 0) { throw new Scene3DException("The framebuffer cannot be a texture (it has to be in the colour mode Rgba).") }
                }

                construct(int width, int height, bytes) {
                    this.handle = __S3DTexNew(width, height, bytes)
                    this.width = width
                    this.height = height
                    if (this.handle == 0) { throw new Scene3DException("A texture needs width * height * 4 bytes (R, G, B, A per pixel).") }
                }

                // (the handle of a picture of the renderer: use Checker)
                construct(int handle, int width, int height, bool made) {
                    this.handle = handle
                    this.width = width
                    this.height = height
                }

                destruct() {
                    if (this.handle > 0) { __S3DTexFree(this.handle) }
                }

                // A checker board of cells x cells squares (the colours are 0xAABBGGRR) in a picture of size x size pixels.
                static Checker(int size = 64, int cells = 8, int colorA = 4294967295, int colorB = 4282400832) {
                    var h = __S3DTexChecker(size, cells, colorA, colorB)
                    if (h == 0) { throw new Scene3DException("A checker board needs 1 <= cells <= size <= 4096.") }
                    var t = new Texture(h, size, size, true)
                    return t
                }

                int Width { get { return this.width } }
                int Height { get { return this.height } }
            }

            // A light: ambient (everywhere), directional (parallel rays, like the sun) or point (from a place, weaker with the distance).
            class Light {
                int Kind
                int Color
                float Intensity
                float x
                float y
                float z
                float dx
                float dy
                float dz
                float range

                construct(int kind = 2, int color = 4294967295, float intensity = 1.0) {
                    this.Kind = kind
                    this.Color = color
                    this.Intensity = intensity
                    this.x = 0.0
                    this.y = 2.0
                    this.z = 0.0
                    this.dx = -0.5
                    this.dy = -1.0
                    this.dz = -0.5
                    this.range = 10.0
                }

                static Ambient(int color = 4294967295, float intensity = 0.3) { return new Light(0, color, intensity) }

                // Rays that travel in the direction (dx, dy, dz) (a vector without a unit).
                static Directional(float dx, float dy, float dz, int color = 4294967295, float intensity = 1.0) {
                    var l = new Light(1, color, intensity)
                    l.Direction(dx, dy, dz)
                    return l
                }

                static Point(x, y, z, int color = 4294967295, float intensity = 1.0) {
                    var l = new Light(2, color, intensity)
                    l.X = x
                    l.Y = y
                    l.Z = z
                    return l
                }

                Direction(float dx, float dy, float dz) {
                    this.dx = dx
                    this.dy = dy
                    this.dz = dz
                }

                X { get { return this.x * 1m } set { this.x = S3D.Length(value) } }
                Y { get { return this.y * 1m } set { this.y = S3D.Length(value) } }
                Z { get { return this.z * 1m } set { this.z = S3D.Length(value) } }
                // The distance at which a point light has half of its strength.
                Range { get { return this.range * 1m } set { this.range = S3D.Length(value) } }

                // The numbers the renderer takes: kind, r, g, b, intensity, position (3), direction (3), range.
                Parameters() {
                    return [this.Kind * 1.0, S3D.Red(this.Color), S3D.Green(this.Color), S3D.Blue(this.Color), this.Intensity, this.x, this.y, this.z, this.dx, this.dy, this.dz, this.range]
                }
            }

            // Something in the scene with triangles, a position, a size and a look.
            class Object {
                int mesh
                float minX
                float minY
                float minZ
                float maxX
                float maxY
                float maxZ
                float px
                float py
                float pz
                float rx
                float ry
                float rz
                float sx
                float sy
                float sz
                float ax
                float ay
                float az
                float tx
                float ty
                float tw
                float th
                float trot
                int Color
                bool Visible
                bool twoSided
                Texture texture
                Material material
                string Key
                List posList
                List uvList
                List idxList
                bool dirty

                // An empty object: add triangles with AddTriangle.
                construct() {
                    this.Init()
                    this.mesh = 0
                    this.posList = new List()
                    this.uvList = new List()
                    this.idxList = new List()
                    this.dirty = false
                }

                // An object from a model (or from the text of a Wavefront OBJ file): at the origin, in the size of the model.
                construct(modelData) {
                    this.Init()
                    var model = modelData
                    if (modelData is of string) { model = Model.FromObj(modelData) }
                    if (!(model is of Model)) { throw new Scene3DException("An object is made from a Model or the text of an OBJ file.") }
                    this.UseMesh(model.handle)
                    __S3DMeshRetain(model.handle)
                }

                // From a template (Template.Box, Sphere, Cylinder, Cone, Plane): at (x, y, z), the outer dimensions width, height and depth (a plane has no height).
                construct(int template, x, y, z, width, height, depth, int detail = 24) {
                    this.Init()
                    var h = __S3DMeshTemplate(template, detail)
                    if (h == 0) { throw new Scene3DException("Unknown template: " + template) }
                    this.UseMesh(h)
                    if (template == 4) { this.twoSided = true }
                    this.X = x
                    this.Y = y
                    this.Z = z
                    this.Width = width
                    if (template != 4) { this.Height = height }
                    this.Depth = depth
                }

                destruct() {
                    if (this.mesh > 0) { __S3DMeshFree(this.mesh) }
                }

                Init() {
                    this.px = 0.0
                    this.py = 0.0
                    this.pz = 0.0
                    this.rx = 0.0
                    this.ry = 0.0
                    this.rz = 0.0
                    this.sx = 1.0
                    this.sy = 1.0
                    this.sz = 1.0
                    this.ax = 0.5
                    this.ay = 0.5
                    this.az = 0.5
                    this.tx = 0.0
                    this.ty = 0.0
                    this.tw = 0.0
                    this.th = 0.0
                    this.trot = 0.0
                    this.Color = 4291611852
                    this.Visible = true
                    this.twoSided = false
                    this.Key = ""
                    this.dirty = false
                }

                UseMesh(int handle) {
                    this.mesh = handle
                    var b = __S3DMeshBounds(handle)
                    this.minX = b[0]
                    this.minY = b[1]
                    this.minZ = b[2]
                    this.maxX = b[3]
                    this.maxY = b[4]
                    this.maxZ = b[5]
                }

                // ---- position, rotation, anchor ----
                X { get { return this.px * 1m } set { this.px = S3D.Length(value) } }
                Y { get { return this.py * 1m } set { this.py = S3D.Length(value) } }
                Z { get { return this.pz * 1m } set { this.pz = S3D.Length(value) } }
                RotationX { get { return S3D.Degrees(this.rx) } set { this.rx = S3D.Angle(value) } }
                RotationY { get { return S3D.Degrees(this.ry) } set { this.ry = S3D.Angle(value) } }
                RotationZ { get { return S3D.Degrees(this.rz) } set { this.rz = S3D.Angle(value) } }

                // Where the position takes hold of the object, as a fraction of its width, height and depth (default 0.5: the centre).
                AnchorX { get { return this.ax } set { this.ax = value } }
                AnchorY { get { return this.ay } set { this.ay = value } }
                AnchorZ { get { return this.az } set { this.az = value } }

                SetPosition(x, y, z) {
                    this.X = x
                    this.Y = y
                    this.Z = z
                }

                SetAnchor(float x, float y, float z) {
                    this.ax = x
                    this.ay = y
                    this.az = z
                }

                SetRotation(x, y, z) {
                    this.RotationX = x
                    this.RotationY = y
                    this.RotationZ = z
                }

                Move(dx, dy, dz) {
                    this.px = this.px + S3D.Length(dx)
                    this.py = this.py + S3D.Length(dy)
                    this.pz = this.pz + S3D.Length(dz)
                }

                Rotate(x, y, z) {
                    this.rx = this.rx + S3D.Angle(x)
                    this.ry = this.ry + S3D.Angle(y)
                    this.rz = this.rz + S3D.Angle(z)
                }

                // ---- size: the outer dimensions (what is set becomes the scale of the axis) ----
                // (the size of the mesh itself, before it is scaled)
                MeshWidth() { return this.maxX - this.minX }
                MeshHeight() { return this.maxY - this.minY }
                MeshDepth() { return this.maxZ - this.minZ }

                Width {
                    get { return (this.sx * this.MeshWidth()) * 1m }
                    set {
                        var w = this.MeshWidth()
                        if (w < 0.000001) { throw new Scene3DException("This object has no width to scale.") }
                        this.sx = S3D.Length(value) / w
                    }
                }
                Height {
                    get { return (this.sy * this.MeshHeight()) * 1m }
                    set {
                        var h = this.MeshHeight()
                        if (h < 0.000001) { throw new Scene3DException("This object has no height to scale.") }
                        this.sy = S3D.Length(value) / h
                    }
                }
                Depth {
                    get { return (this.sz * this.MeshDepth()) * 1m }
                    set {
                        var d = this.MeshDepth()
                        if (d < 0.000001) { throw new Scene3DException("This object has no depth to scale.") }
                        this.sz = S3D.Length(value) / d
                    }
                }

                SetSize(width, height, depth) {
                    this.Width = width
                    this.Height = height
                    this.Depth = depth
                }

                // The scale factors themselves (1 = the size of the mesh).
                ScaleX { get { return this.sx } set { this.sx = value } }
                ScaleY { get { return this.sy } set { this.sy = value } }
                ScaleZ { get { return this.sz } set { this.sz = value } }

                // ---- look ----
                Texture {
                    get { return this.texture }
                    set {
                        if (value != undefined) { try value.TakeTo(this) }
                        this.texture = value
                    }
                }

                Material {
                    get { return this.material }
                    set {
                        if (value != undefined) { try value.TakeTo(this) }
                        this.material = value
                    }
                }

                // The part of the object one copy of the texture covers (default: the whole object once); a smaller size repeats the texture.
                TextureWidth { get { return this.tw * 1m } set { this.tw = S3D.Length(value) } }
                TextureHeight { get { return this.th * 1m } set { this.th = S3D.Length(value) } }
                // Where the texture starts on the object, and its rotation.
                TextureX { get { return this.tx * 1m } set { this.tx = S3D.Length(value) } }
                TextureY { get { return this.ty * 1m } set { this.ty = S3D.Length(value) } }
                TextureRotation { get { return S3D.Degrees(this.trot) } set { this.trot = S3D.Angle(value) } }

                int Triangles {
                    get {
                        this.Build()
                        if (this.mesh == 0) { return 0 }
                        return __S3DMeshTriangleCount(this.mesh)
                    }
                }

                // ---- triangles of an empty object (coordinates in meters, counter clockwise seen from outside) ----
                AddTriangle(x1, y1, z1, x2, y2, z2, x3, y3, z3) {
                    if (this.posList == undefined) { throw new Scene3DException("Only an empty object takes triangles.") }
                    var n = this.posList.count / 3
                    this.posList.Add(S3D.Length(x1))
                    this.posList.Add(S3D.Length(y1))
                    this.posList.Add(S3D.Length(z1))
                    this.posList.Add(S3D.Length(x2))
                    this.posList.Add(S3D.Length(y2))
                    this.posList.Add(S3D.Length(z2))
                    this.posList.Add(S3D.Length(x3))
                    this.posList.Add(S3D.Length(y3))
                    this.posList.Add(S3D.Length(z3))
                    this.uvList.Add(0.0)
                    this.uvList.Add(1.0)
                    this.uvList.Add(1.0)
                    this.uvList.Add(1.0)
                    this.uvList.Add(0.0)
                    this.uvList.Add(0.0)
                    this.idxList.Add(n)
                    this.idxList.Add(n + 1)
                    this.idxList.Add(n + 2)
                    this.dirty = true
                }

                // Makes the mesh of an empty object from its triangles.
                Build() {
                    if (!this.dirty) { return }
                    this.dirty = false
                    if (this.mesh > 0) { __S3DMeshFree(this.mesh) }
                    var p = new float[this.posList.count]
                    for (var i = 0; i < this.posList.count; i++) { p[i] = this.posList[i] }
                    var t = new float[this.uvList.count]
                    for (var i = 0; i < this.uvList.count; i++) { t[i] = this.uvList[i] }
                    var ix = new int[this.idxList.count]
                    for (var i = 0; i < this.idxList.count; i++) { ix[i] = this.idxList[i] }
                    var h = __S3DMeshNew(p, t, ix)
                    if (h == 0) { throw new Scene3DException("The triangles do not make a mesh.") }
                    this.UseMesh(h)
                }

                // The numbers the renderer takes for this object: position, rotation, scale, pivot, colour, specular, shininess, smooth, two sided, emissive (3), texture, repeats (2), rotation, offset (2).
                Parameters() {
                    var pivotX = this.minX + this.ax * (this.maxX - this.minX)
                    var pivotY = this.minY + this.ay * (this.maxY - this.minY)
                    var pivotZ = this.minZ + this.az * (this.maxZ - this.minZ)
                    var spec = 0.0
                    var shin = 32.0
                    var smooth = 1.0
                    var two = 0.0
                    var emissive = 0
                    if (this.twoSided) { two = 1.0 }
                    if (this.material != undefined) {
                        spec = this.material.Specular
                        shin = this.material.Shininess
                        if (!this.material.Smooth) { smooth = 0.0 }
                        if (this.material.TwoSided) { two = 1.0 }
                        emissive = this.material.Emissive
                    }
                    var useTexture = 0.0
                    var repU = 1.0
                    var repV = 1.0
                    var offU = 0.0
                    var offV = 0.0
                    if (this.texture != undefined) {
                        useTexture = 1.0
                        var objectWidth = this.sx * this.MeshWidth()
                        var objectHeight = this.sy * this.MeshHeight()
                        if (objectHeight < 0.000001) { objectHeight = this.sz * this.MeshDepth() }
                        var tileW = objectWidth
                        var tileH = objectHeight
                        if (this.tw > 0.0) { tileW = this.tw }
                        if (this.th > 0.0) { tileH = this.th }
                        if (this.tw > 0.0 && objectWidth > 0.000001) { repU = objectWidth / this.tw }
                        if (this.th > 0.0 && objectHeight > 0.000001) { repV = objectHeight / this.th }
                        if (tileW > 0.000001) { offU = this.tx / tileW }
                        if (tileH > 0.000001) { offV = this.ty / tileH }
                    }
                    return [this.px, this.py, this.pz, this.rx, this.ry, this.rz, this.sx, this.sy, this.sz, pivotX, pivotY, pivotZ,
                        S3D.Red(this.Color), S3D.Green(this.Color), S3D.Blue(this.Color), spec, shin, smooth, two,
                        S3D.Red(emissive), S3D.Green(emissive), S3D.Blue(emissive), useTexture, repU, repV, this.trot, offU, offV]
                }

                Draw(int frame, pixels, camera, lights) {
                    if (!this.Visible) { return }
                    this.Build()
                    if (this.mesh == 0) { return }
                    var tex = 0
                    if (this.texture != undefined) { tex = this.texture.handle }
                    if (!__S3DDraw(frame, pixels, this.mesh, tex, this.Parameters(), camera, lights)) {
                        throw new Scene3DException("The object cannot be drawn (a handle is no longer valid).")
                    }
                }
            }

            // The view of a camera on its scene, drawn into a framebuffer (in the colour mode Rgba).
            class Camera {
                Framebuffer framebuffer
                int frame
                pixels
                int frameWidth
                int frameHeight
                Scene scene
                float x
                float y
                float z
                float rx
                float ry
                float rz
                float fov
                float nearPlane
                float farPlane
                int Background
                string Key

                construct(Framebuffer target) {
                    this.framebuffer = target
                    try target.TakeTo(this)
                    this.frame = 0
                    this.frameWidth = 0
                    this.frameHeight = 0
                    this.x = 0.0
                    this.y = 0.0
                    this.z = 0.0
                    this.rx = 0.0
                    this.ry = 0.0
                    this.rz = 0.0
                    this.fov = 1.0471975511965976
                    this.nearPlane = 0.1
                    this.farPlane = 500.0
                    this.Background = 4282198552
                    this.Key = ""
                }

                destruct() {
                    if (this.frame > 0) { __S3DFrameFree(this.frame) }
                }

                Framebuffer { get { return this.framebuffer } }
                X { get { return this.x * 1m } set { this.x = S3D.Length(value) } }
                Y { get { return this.y * 1m } set { this.y = S3D.Length(value) } }
                Z { get { return this.z * 1m } set { this.z = S3D.Length(value) } }
                RotationX { get { return S3D.Degrees(this.rx) } set { this.rx = S3D.Angle(value) } }
                RotationY { get { return S3D.Degrees(this.ry) } set { this.ry = S3D.Angle(value) } }
                RotationZ { get { return S3D.Degrees(this.rz) } set { this.rz = S3D.Angle(value) } }
                // The vertical angle of view (default 60deg).
                FieldOfView {
                    get { return S3D.Degrees(this.fov) }
                    set {
                        var a = S3D.Angle(value)
                        if (a < 0.01 || a > 3.0) { throw new Scene3DException("The field of view has to be between about 1deg and 170deg.") }
                        this.fov = a
                    }
                }
                // What is nearer than Near or farther than Far is not drawn.
                Near { get { return this.nearPlane * 1m } set { this.nearPlane = S3D.Length(value) } }
                Far { get { return this.farPlane * 1m } set { this.farPlane = S3D.Length(value) } }

                SetPosition(x, y, z) {
                    this.X = x
                    this.Y = y
                    this.Z = z
                }

                // Turns the camera (pitch and yaw, no roll) towards a point.
                LookAt(x, y, z) {
                    var a = __S3DLookAt(this.x, this.y, this.z, S3D.Length(x), S3D.Length(y), S3D.Length(z))
                    this.rx = a[0]
                    this.ry = a[1]
                    this.rz = 0.0
                }

                // Draws what the camera sees into its framebuffer.
                Render() {
                    if (this.scene == undefined) { throw new Scene3DException("The camera is not in a scene: scene.Add(camera).") }
                    var w = this.framebuffer.Width()
                    var h = this.framebuffer.Height()
                    if (this.frame == 0) {
                        this.frame = __S3DFrameNew(w, h)
                        if (this.frame == 0) { throw new Scene3DException("The framebuffer has no usable size.") }
                    }
                    if (this.pixels == undefined || w != this.frameWidth || h != this.frameHeight) {
                        this.pixels = new byte[w * h * 4]
                        this.frameWidth = w
                        this.frameHeight = h
                    }
                    if (!__S3DFrameClear(this.frame, this.pixels, w, h, this.Background)) { throw new Scene3DException("The frame cannot be cleared.") }
                    var camera = [this.x, this.y, this.z, this.rx, this.ry, this.rz, this.fov, this.nearPlane, this.farPlane]
                    var lights = this.scene.LightParameters()
                    foreach (o in this.scene.Objects) { o.Draw(this.frame, this.pixels, camera, lights) }
                    this.framebuffer.WriteBytes(this.pixels)
                }
            }

            // The space: objects, cameras, lights and textures, each also reachable by a key.
            class Scene {
                List Objects
                List Cameras
                List Lights
                List Textures
                List keyNames
                List keyItems
                int AmbientColor
                float AmbientIntensity

                construct() {
                    this.Objects = new List()
                    this.Cameras = new List()
                    this.Lights = new List()
                    this.Textures = new List()
                    this.keyNames = new List()
                    this.keyItems = new List()
                    this.AmbientColor = 4294967295
                    this.AmbientIntensity = 0.25
                }

                // Adds an object, camera, light or texture (to Objects, Cameras, Lights or Textures); with a key the item can be fetched with Get(key). Returns the item.
                Add(item, string key = "") {
                    if (!(item is of Object || item is of Camera || item is of Light || item is of Texture)) {
                        throw new Scene3DException("A scene takes objects, cameras, lights and textures.")
                    }
                    if (key != "") {
                        for (var i = 0; i < this.keyNames.count; i++) {
                            if (this.keyNames[i] == key) { throw new Scene3DException("The key '" + key + "' is used already.") }
                        }
                    }
                    try item.TakeTo(this)
                    if (item is of Object) {
                        this.Objects.Add(item)
                    } else if (item is of Camera) {
                        this.Cameras.Add(item)
                        item.scene = this
                    } else if (item is of Light) {
                        this.Lights.Add(item)
                    } else {
                        this.Textures.Add(item)
                    }
                    if (key != "") {
                        this.keyNames.Add(key)
                        this.keyItems.Add(item)
                    }
                    return item
                }

                // The item with the key (undefined if there is none).
                Get(string key) {
                    for (var i = 0; i < this.keyNames.count; i++) {
                        if (this.keyNames[i] == key) { return this.keyItems[i] }
                    }
                    return undefined
                }

                // Takes an item out of the scene again.
                Remove(item) {
                    if (item is of Object) { this.RemoveFrom(this.Objects, item) }
                    else if (item is of Camera) { this.RemoveFrom(this.Cameras, item); item.scene = undefined }
                    else if (item is of Light) { this.RemoveFrom(this.Lights, item) }
                    else if (item is of Texture) { this.RemoveFrom(this.Textures, item) }
                    for (var i = 0; i < this.keyItems.count; i++) {
                        if (this.keyItems[i] == item) {
                            this.keyItems.RemoveAt(i)
                            this.keyNames.RemoveAt(i)
                            break
                        }
                    }
                }

                RemoveFrom(list, item) {
                    for (var i = 0; i < list.count; i++) {
                        if (list[i] == item) {
                            list.RemoveAt(i)
                            return
                        }
                    }
                }

                // The numbers the renderer takes for the lights (the ambient light of the scene first).
                LightParameters() {
                    var n = this.Lights.count + 1
                    var all = new float[n * 12]
                    var first = [0.0, S3D.Red(this.AmbientColor), S3D.Green(this.AmbientColor), S3D.Blue(this.AmbientColor), this.AmbientIntensity, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0]
                    for (var k = 0; k < 12; k++) { all[k] = first[k] }
                    for (var i = 0; i < this.Lights.count; i++) {
                        var p = this.Lights[i].Parameters()
                        for (var k = 0; k < 12; k++) { all[(i + 1) * 12 + k] = p[k] }
                    }
                    return all
                }
            }
            """;
    }
}
