# fire documentation

fire is a programming language with its own tools:

| Name | What it is |
| --- | --- |
| **fire** | the language |
| **forge** | the compiler and runner: VM, native builds, tests (`forge run`, `forge build`, `forge test`) |
| **spark** | the editor (IDE) with debugger, designers and the device panel |
| **ember** | the package manager |

New here? Start with **First Steps** in spark (*Help > First Steps*, [source](../src/fire.Editor/Help/First%20Steps.md)) and then
[the language](language/README.md).

## The language

The [language reference](language/README.md) - one page per topic, with examples that run as they are and exercises. It is also in spark under
*Help > The fire Language*.

* Basics, types, operators, control flow, [units](language/05-units.md), strings, [arrays and lists](language/07-arrays-and-lists.md)
* [Classes](language/08-classes.md), [interfaces and generics](language/09-interfaces-and-generics.md), enums, extensions, lambdas
* [Ownership](language/13-ownership.md) - who owns an object, `take`, `copy`/`flat`, `[NoCopy]`, ownership in property accessors
* [Exceptions](language/14-exceptions.md), [threads and actors](language/15-threads-and-actors.md) (`fire`, `taking`, `handover`, `sync`)
* [Probes and reflection](language/16-probes-and-reflection.md), [directives](language/17-preprocessor.md), [native code](language/18-native-code.md)
* [The standard library](language/19-standard-library.md), [Time](language/20-time.md)
* [Planned for the language](language/roadmap.md)

## Libraries by topic

| Topic | Pages |
| --- | --- |
| **User interfaces** | [Overview and first window](ui/README.md) · [Layout and containers](ui/containers.md) · [Controls](ui/controls.md) · [Lists and trees](ui/lists.md) · [Menus, tool bars, popups](ui/menus.md) · [Shapes, images, drawing](ui/shapes.md) · [Styles, templates, binding](ui/styling.md) · [Input, drag & drop](ui/input.md) · [Windows, theme, effects](ui/windows.md) · [Common dialogs](ui/dialogs.md) · [Docking](ui/docking.md) · [Markup (`.fxml`)](ui/markup.md) |
| **Graphics** | [2D: framebuffers, renderer, windows](graphics/README.md) · [Fonts](graphics/fonts.md) · [3D scenes](graphics/3d.md) · [Models and animations](graphics/models.md) · [Terrains](graphics/terrain.md) |
| **Networking** | [Overview, TCP and UDP](networking/README.md) · [HTTP](networking/http.md) · [TLS](networking/tls.md) · [WiFi](networking/wifi.md) |
| **Audio** | [Sound output and input](audio/README.md) · [Tracker modules](audio/tracker.md) |
| **Circuits** | [Simulation, schematics, the circuit editor](circuits/README.md) |
| **Devices** | [Device framework](devices/README.md) · [GPIO](devices/gpio.md) · [I2C](devices/i2c.md) · [SPI](devices/spi.md) · [USB](devices/usb.md) |
| **Standard library** | [Files](stdlib/filesystem.md) · [Streams](stdlib/io.md) · [Console](stdlib/console.md) · [Machine](stdlib/machine.md) · [DOS and XMS](stdlib/dos.md) · [JSON](stdlib/json.md) · [Hashes](stdlib/hash.md) · [Compression and Zip](stdlib/compression.md) · [Processes](stdlib/process.md) · [Resources](stdlib/resources.md) · [Random, LINQ](stdlib/more.md) |

## Building and sharing code

| Topic | Pages |
| --- | --- |
| **Packages** | [Using packages, ember](packages/README.md) · [Building a package (fire only)](packages/building.md) · [Packages with natives (C/C++)](packages/natives.md) |
| **Libraries** | [Libraries and plugins](libraries/README.md) · [Building a library (fire only)](libraries/building.md) · [Libraries with natives](libraries/natives.md) |
| **Targets** | [Overview, native builds](targets/README.md) · [Windows](targets/windows.md) · [Linux](targets/linux.md) · [macOS](targets/macos.md) · [Browser (wasm)](targets/wasm.md) · [ESP32](targets/esp32.md) · [FreeRTOS](targets/freertos.md) |

## Tools

* [spark, the editor](tools/spark.md)
* [Projects and solutions](tools/projects.md) (`.fireproj`, `.firesln`)
* [forge on the command line](tools/forge.md)
* [Testing](tools/testing.md) (`[Test]`, `forge test`, the Test Explorer)
* [Error codes and warnings](tools/errors.md)
* spark: [templates](tools/templates.md), [git](tools/git.md), [notes](tools/notes.md)
* [Embedding fire in C#](../src/fire.Editor/Help/Embedding.md)

## Inside fire

For contributors: how the compiler, the VM and the native backend work. These pages describe the implementation; where they and the code disagree,
the code is right.

* [Language specification](spec/SPEC.md), [bytecode](spec/BYTECODE.md), [gradual typing](spec/TYPES.md), [threading design](spec/THREADING_DESIGN.md)
* [Native backend (fire -> C)](spec/NATIVE_BACKEND.md), [debug protocol of native programs](spec/NATIVE_DEBUG_PROTOCOL.md)
* [Graphics layer](spec/GRAPHICS_LAYER.md), [own display layer and effects](spec/UI_PLATFORM.md)
* [Roadmap](ROADMAP.md) - what is planned and in which order
