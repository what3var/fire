# Models and animations (`Model`, the file `.f3d`)

A **model** is what an object of a [3D scene](3d.md) shows. Besides the templates, OBJ text and arrays, a model can come from fire's own **model file** (`.f3d`):
a tree of **parts** with their colours, and **animations** that move the parts. forge makes `.f3d` from **glTF 2.0**, the open format that Blender and most
3D tools write - when the program is built, so the program carries only the small file.

![The robot of the tests: at rest, waving, jumping, and waving in the lean renderer](../images/model-animation.png)

```fire
#import "scene3d"

var robot = new Object(Model.FromResource(new Resource("robot.glb")))   // glTF: converted when the program is built
robot.AnchorY = 0.0                                                       // stand on the ground
scene.Add(robot)

robot.Play("walk")                  // an animation of the model, from its start (looping as the model says)
while (running) {
    robot.Advance(16ms)             // move it on by the time that passed
    cam.Render()
}
```

## Making a model file

* **A resource:** `new Resource("x.gltf")` or `new Resource("x.glb")` is converted to `.f3d` when the program is compiled (in the VM, a packed program and a
  native build alike); a file that is not glTF 2.0 is a compile error with the reason. `.gltf` may keep its buffers next to it or inside (data URIs).
* **By hand:** `forge model x.gltf [-o x.f3d] [--fps 30]` writes the file; `Model.FromBytes(bytes)` reads it (`IO.File.ReadAllBytes`).
* What is read from glTF: the nodes of the scene become the **parts** (a node's translation, rotation and scale - or its matrix - is the part's rest pose,
  its children are child parts); a node's mesh (all primitives of triangles; positions, normals, texture coordinates) is the part's mesh, the base colour of its
  first material the part's colour. **Animations** become clips: translation, rotation and scale of the nodes, sampled at 30 frames per second (linear, step
  and cubic spline keys). Skins (bones that bend a mesh), morph targets, textures and lights are not read.

## Playing

| | |
| --- | --- |
| `obj.Play("name")` / `obj.Play("name", loop)` | starts the animation at its beginning (loop as the model says - clips from glTF loop - or as given); `Play("")` shows the rest pose |
| `obj.Advance(time)` | moves it on (`16ms`, `0.5s`); a clip that does not loop stops at its end |
| `obj.AnimationTime` | the time in the clip (set it to show a moment) |
| `obj.Stop()` | stops where it is |
| `obj.Animation`, `obj.Playing` | the name of the clip ("" none), whether it runs |
| `model.Parts`, `model.Triangles`, `model.Animations`, `model.AnimationLength("name")` | what the model holds |

Times need a unit (`s`, `ms`). An object made from a model has everything an object has: position, rotation, size (the box of the model in its rest pose),
anchor, texture, material, shadows. A part with a colour of its own keeps it; `obj.Color` colours the parts without one.

## The file

Small on purpose (packages/scene3d/native/s3d_model.h describes it byte by byte): a corner takes **8 bytes** (positions as 16-bit numbers in the box of the
part, the normal in two bytes), 12 with texture coordinates; indices are 16 bit (at most 65535 corners a part); a part in a frame of a clip takes 26 bytes.
The robot of the tests - two parts, 24 triangles, two clips - is 3 KB (1.9 KB at 15 frames per second). Parts are **rigid**: an animation moves, turns and
scales whole parts - the way games of the 90s animated their figures, and cheap to compute on a small machine (also with the lean renderer, also on DOS).

Planned (ROADMAP §16): the parts' meshes kept in this compact form in memory too (today they are read into the renderer's meshes), textures in the file,
blending from one clip into the next, the block editor in spark that writes `.f3d`.
