# 3D scenes (`#import "scene3d"`, the package `fire-scene3d`)

A small 3D engine for programs: a **scene** is a space that holds objects, cameras, lights and textures; a **camera** is the link between the scene and a framebuffer - `camera.Render()` draws what
the camera sees into it. The renderer is a **software renderer** written in C++ (`native/bridges/scene3d/s3d_engine.hpp`), so it runs everywhere fire runs - on a PC, in a native build, on an ESP32,
in the browser (WebAssembly) - and **the picture is the same pixel for pixel** in the virtual machine and in a native build. Like every bridge it is a package (`ember`, docs/PACKAGES.md): a prelude in
fire (`Scene3DPrelude.cs`: the scene, the units, the objects) plus the C++ of the renderer. It needs `graphics`.

```
program         #import "scene3d"          Scene, Camera, Object, Light, Texture, Material, Model, Template   (fire, package fire-scene3d)
bridge          fire_bridge_scene3d.hpp    meshes, textures and depth buffers by handle                         (C++, the same code in the VM and in a native build)
renderer        scene3d/s3d_engine.hpp     triangles in, RGBA pixels out; knows nothing of fire                 (plain C++)
```

## Quick start

```
#import "windows"
#import "scene3d"

var fb = new Framebuffer(480, 320)
var win = new Window(fb, "Scene3D")
var scene = new Scene()

var box = new Object(Template.Box, 0m, 0.5m, 0m, 1m, 1m, 1m)   // x, y, z of the position, then width, height, depth
box.Texture = Texture.Checker(32, 4)
scene.Add(box, "crate")                                         // the optional key: scene.Get("crate")
scene.Add(Light.Directional(-0.5, -1.0, -0.4))

var cam = new Camera(fb)                                        // the camera draws into this framebuffer
cam.SetPosition(0m, 2m, 4m)
cam.RotationX = -20deg                                          // or cam.LookAt(0m, 0.5m, 0m)
scene.Add(cam)

while (win.Tick()) {
    box.Rotate(0deg, 1deg, 0deg)
    cam.Render()
}
```

The sample **Scene3D** (Help > Samples > Graphics and UI) is a bigger scene.

## Units

Space is metric. A length is a number **with a unit** (`1m`, `20cm`, `3mm`, `2.5m`), an angle is `deg` or `rad`. A plain number where a length or an angle belongs is an error
(`Scene3DException`: *A length needs a unit of length*) - a unit is never guessed. The properties give back lengths in meters (`box.X` is `0m`) and angles in degrees (`box.RotationY` is `30deg`);
`ball.Height / 1cm` is the number of centimetres. Colours are integers `0xAABBGGRR` like everywhere in fire (`S3D.Rgb(r, g, b)` makes one from 0..255 parts). Vectors without a unit
(the direction of a light) are plain numbers.

The space is right handed, **Y is up**; a camera without rotation looks along **-Z**. Rotations are applied in the order Z, X, Y (yaw, pitch, roll as you would expect for a turned head
or a turned object).

## Scene

```
var scene = new Scene()
scene.Add(item)               // object, camera, light or texture; returns the item
scene.Add(item, "key")        // the same, and the item can be fetched with the key (a key is used once)
scene.Get("key")              // the item, or undefined
scene.Remove(item)
scene.Objects  scene.Cameras  scene.Lights  scene.Textures        // the lists (List)
scene.AmbientColor  scene.AmbientIntensity                         // the light that is everywhere (default white, 0.25)
```

The scene owns what it holds (ownership like everywhere in fire: `Add` takes the item, destroying the scene destroys its objects, cameras, lights and textures).

## Objects

```
new Object(Template.Box, x, y, z, width, height, depth)     // from a template; Box, Sphere, Cylinder, Cone, Plane (lies in XZ, no height), Quad (standing, facing +Z, no depth: pass 0m)
new Object(model)                                           // from a Model (or the text of a Wavefront OBJ file), shared triangles
new Object()                                                // empty: add triangles with AddTriangle(x1, y1, z1, x2, y2, z2, x3, y3, z3)
```

| Property | |
|---|---|
| `X`, `Y`, `Z` | position of the **anchor** (lengths) |
| `AnchorX`, `AnchorY`, `AnchorZ` | the point of the object that sits at the position, as a fraction of its width, height and depth. `0.5, 0.5, 0.5` (default) is the centre, `0.5, 0, 0.5` the middle of the bottom |
| `RotationX`, `RotationY`, `RotationZ` | angles; the object turns around its anchor |
| `Width`, `Height`, `Depth` | the **outer dimensions**; setting one scales the object on that axis (`box.Height = 2m`) |
| `ScaleX`, `ScaleY`, `ScaleZ` | the scale factors themselves (1 = the size of the mesh) |
| `Color` | the colour when there is no texture (default light grey) |
| `Texture`, `Material` | the look (below) |
| `Visible` | `false`: not drawn |
| `Opacity` | 1 = solid, less is see-through (see *Transparency*) |
| `AlphaCutoff` | above 0: texture pixels that are more transparent than this are left out, the rest is solid |
| `Lit` | `false`: no lighting, the colour as it is |
| `CastShadows`, `ReceiveShadows` | whether the object throws shadows / shows the shadows of others (both default `true`) |
| `Layers` | a number whose bits are layers (default all); only lights that share a layer with the object light it |
| `Billboard` | `BillboardMode.Facing` (always faces the camera) or `BillboardMode.Vertical` (turns around the vertical axis only), default `Off` |
| `Triangles` | the number of triangles |

`SetPosition(x, y, z)`, `SetRotation(x, y, z)`, `SetAnchor(x, y, z)`, `SetSize(w, h, d)`, `Move(dx, dy, dz)` and `Rotate(rx, ry, rz)` set or change several at once.

A **Model** holds triangles that several objects can share: `Model.FromTemplate(Template.Sphere, 32)`, `Model.FromObj(text)` (`v`, `vt` and `f` lines; polygons are cut into triangles; the normals are
computed; the unit of the coordinates is the meter), `Model.FromArrays(positions, uvs, indices)`.

## Textures and materials

A **Texture** is an RGBA picture: `new Texture(framebuffer)` (a framebuffer in the colour mode Rgba; the pixels are copied - load a PNG into a framebuffer with the graphics package),
`new Texture(width, height, bytes)` or `Texture.Checker(size, cells, colorA, colorB)`. Assign it to an object (`obj.Texture = t`) or put it into the scene (`scene.Add(t, "wall")`) to keep it with a key.

A texture has a position, a size and a rotation of its own on the object: `TextureWidth` and `TextureHeight` are the part of the object that one copy of the picture covers (the default is the whole
object once; a smaller size repeats the picture, `50cm` on a 1 m box is a 2x2 pattern), `TextureX` and `TextureY` move it, `TextureRotation` turns it.

**Bump maps.** `texture.AddBumpmap()` makes a bump map from the brightness of the texture itself (bright is high, a little smoothed); `texture.AddBumpmap(framebuffer)` takes a picture (a framebuffer
in the colour mode Rgba; its brightness is the height; another size is scaled to the texture) and `texture.AddBumpmap(width, height, bytes)` bytes (`width * height` heights, or `width * height * 4`
bytes R, G, B, A). The lighting then tilts the surface normal against the slope of the heights, so the surface looks rough or carved although its triangles are flat. `texture.BumpStrength` (default 1)
says how much, `texture.RemoveBumpmap()` takes it away.

A **Material** says how a surface answers to light: `Specular` (strength of the highlights, 0 = matt), `Shininess` (how small they are), `Smooth` (shade with the interpolated normals; `false`: every
triangle flat), `TwoSided` and `Emissive` (a colour the surface gives off by itself). `Material.Matte()`, `Material.Plastic()` and `Material.Metal()` are ready made.

## Lights

```
Light.Ambient(color, intensity)                              // everywhere
Light.Directional(dx, dy, dz, color, intensity)              // parallel rays (the sun) travelling in the direction (dx, dy, dz)
Light.Point(x, y, z, color, intensity)                       // from a place; weaker with the distance (Range: where it has half of its strength)
```

The lighting is computed per pixel (Blinn-Phong). Without a light an object is black except for the ambient light of the scene.

**Switching light off and limiting it.** `light.Enabled = false` turns a light off. `light.CastShadows` (default `true`) says whether what it hits throws shadows; the scene can switch all shadows
off (`scene.Shadows = false`, much faster) and sets their sharpness (`scene.ShadowResolution`, 64..4096 pixels per shadow map, default 1024). Objects choose too: `obj.CastShadows`, `obj.ReceiveShadows`,
`obj.Lit`. To *limit* a light to some objects give both **layers**: `light.Layers` and `obj.Layers` are numbers whose bits (1, 2, 4, ... up to 8388608) are layers, and a light only lights the objects
that share a layer with it (the default is all of them). The ambient light of the scene is not limited.

**Shadows** are shadow maps: the casters are drawn as seen from the light (one orthographic map for a directional light, six for a point light) and a pixel is dark when something is nearer to the
light. The edge is soft (bilinear filtering of the comparison). Transparent objects throw no shadow, and a cut-out (`AlphaCutoff`) throws the shadow of its whole mesh (set `CastShadows = false`
for sprites).

## Cameras

```
var cam = new Camera(framebuffer)         // the framebuffer is the one the camera draws into (the camera takes it)
cam.X  cam.Y  cam.Z                       // position
cam.RotationX  cam.RotationY  cam.RotationZ
cam.LookAt(x, y, z)                       // turns it (pitch and yaw) towards a point
cam.FieldOfView = 60deg                   // vertical angle of view
cam.Near  cam.Far                         // what is nearer or farther is not drawn
cam.Background                            // the colour of the empty space
scene.Add(cam)
cam.Render()                              // draws every object of the scene into the framebuffer
```

A scene can have several cameras with different framebuffers (a second view, a mirror, a texture: `new Texture(cameraFramebuffer)` after `Render()`). The framebuffer has to be in the colour mode
`Rgba` (the default). Its size can change between two `Render` calls.

## Transparency and billboards

An object with `Opacity < 1`, or a texture with transparent pixels (a PNG with an alpha channel), is **blended** with what is behind it. Solid objects are drawn first, then the transparent ones from the
far to the near (by the distance of their centres; objects that interpenetrate or are very long can show sorting errors, as in every renderer that sorts objects). A transparent object that is
`TwoSided` shows its far side through the near one. For hard edges (leaves, fences, sprites with a clear outline) use `AlphaCutoff = 0.5`: the pixels are either there or not, and no sorting is needed.

A **billboard** is an object that turns to the camera: `var tree = new Object(Template.Quad, x, y, z, 2m, 3m, 0m); tree.Texture = ...; tree.Billboard = BillboardMode.Vertical` (the quad turns around the
vertical axis only, so it stays upright) or `BillboardMode.Facing` (always faces the camera: particles, labels). The anchor of the object is the point it turns around.

## Speed

A picture is **queued, then drawn**: `camera.Render()` hands every object to the renderer, which sets up the triangles and then fills the rows of the picture in bands of 16 rows that are shared between
threads (`scene.Threads`: 0 = as many as the machine has, up to 8; 1 = one thread). Every pixel belongs to one band and gets its triangles in the order they were queued, so **the picture is the same
for every number of threads** and in a native build. WebAssembly and the ESP32 draw with one thread. What costs time is the pixels, not the triangles: the lighting is computed per pixel and per light,
so the time follows the size of the picture and the number of lights; copying the finished picture (800 x 600 x 4 bytes) takes about 0.3 ms. On the development machine a picture of 800 x 600 with
four objects and three lights takes about 20 ms on one thread and 6 ms on four; shadows add about 40 ms on one thread, 10 ms on four. Ways to go faster: a smaller framebuffer (stretch it with
the window), fewer lights, `scene.Shadows = false`, a smaller `ShadowResolution`. There is no anti-aliasing yet.

## What the renderer does

Perspective projection, clipping against the near plane, back-face culling (`TwoSided` turns it off), a depth buffer, perspective-correct texture coordinates, bilinear texture filtering with
repeat, Blinn-Phong lighting per pixel with an ambient, directional and point lights, flat or smooth shading. Shadows (shadow maps, soft edge), transparency (opacity, alpha textures, cut-outs), billboards, bump maps, lights and surfaces that can be switched off or limited to layers. There is no
anti-aliasing. All numbers are `float`; the VM and a native build run the same C++ and give the same pixels (the tests compare them).

**Hardware.** Everything above is software - nothing needs a GPU or a driver, which is what makes the same program run on a microcontroller and in the browser. A hardware backend (OpenGL/Vulkan
through SDL) could draw the same scene later: the scene, the objects and the camera are plain data, only the last step - `Draw` of an object into a frame - would change.

## Errors

`Scene3DException`: a bump map with the wrong number of bytes, a length or an angle without a unit, a key used twice, an item that is not an object, camera, light or texture, a camera that is not in a scene (`scene.Add(cam)` first), a
framebuffer that is not `Rgba`, a model that has no triangles, a texture with the wrong number of bytes.
