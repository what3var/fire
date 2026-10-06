# Embedding fire in your own application

fire is a library first: the editor (**spark**) and the command line compiler are just hosts around it. Running a script from your own .NET program takes a handful of lines. This page shows how, which assemblies you need, and how to control what a script is allowed to do.

New to the language itself? Start with [First Steps](First%20Steps.md).

- [What you need](#what-you-need)
- [Run a script](#run-a-script)
- [Handle errors](#handle-errors)
- [Capture the output](#capture-the-output)
- [Limit what a script may do](#limit-what-a-script-may-do)
- [Stop a script and read its exit code](#stop-a-script-and-read-its-exit-code)
- [Run on a background thread](#run-on-a-background-thread)
- [Build a standalone executable](#build-a-standalone-executable)
- [Call your own C# code from a script](#call-your-own-c-code-from-a-script)

## What you need

The pipeline is *lexer → parser → resolver → compiler → virtual machine*. All of it is in these assemblies:

| Assembly | Contents |
| --- | --- |
| `fire.Compiler.dll` | Preprocessor, parser, resolver, compiler, linker, and `RuntimeSession` - the class you use to run a script. |
| `fire.Runtime.dll` | The virtual machine (`VM`) and the packer for standalone programs. |
| `fire.dll` | The core: values, units, bytecode, the standard prelude. |
| `MemoryPack.Core.dll` | Serialization used by `fire.dll` (NuGet package `MemoryPack`). |
| `fire.IO.Bridge.dll` | The `IO` library (`#import "io"`). |
| `fire.Device.Bridge.dll`, `fire.Device.Manager.dll`, `System.IO.Ports.dll` | The `devices` library: serial ports and simulated devices. |
| `fire.Terminal.Bridge.dll`, `fire.Terminal.dll`, `fire.Terminal.Windows.dll`, `fire.Terminal.Sdl.dll`, `SDL3-CS.dll` and the native `SDL3` library | The `graphics` library: windows, framebuffers and the console. |
| `fire.UI.Bridge.dll` | The `ui` library, built on top of `graphics`. |

`RuntimeSession` links against **all** of the bridges, so the simplest and safest way is to take the complete output folder of `fire.Compiler`: either add a **project reference** to `fire.Compiler` (everything is copied for you), or reference `fire.Compiler.dll` and copy the other files of its `bin` folder - including the `runtimes` subfolder with the native libraries - next to your application.

Your project has to target **`net10.0`** (or newer), the same as `fire.Compiler`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\fire\src\fire.Compiler\fire.Compiler.csproj" />
  </ItemGroup>
</Project>
```

Scripts that do not `#import` a library never touch its bridge at run time, but the DLLs still have to be there.

## Run a script

`RuntimeSession.Build` compiles and links one or more sources into a program and prepares a virtual machine; `Run` executes it on the calling thread and returns when the program has ended.

```csharp
using fire.Compiler;
using fire.Values;

string script = """
    var name = "fire"
    print($"Hello from {name}!")
    """;

var session = RuntimeSession.Build(new[] { script }, executionMode: null, debugWriter: args =>
{
    Console.WriteLine(args[0]);        // what the script printed
    return Value.MakeUndefined();
});

session.Run();
```

- `sources` is a list of source texts that are linked into **one** program, in this order (like passing several files to the command line compiler). Read the files yourself; `#include "file"` inside a script is resolved relative to the `basePath` argument (default: the current directory).
- `executionMode` is `VmExecutionMode.Debug`, `Release` or `Performance`; `null` takes the mode the script asks for with `#debug` / `#performance`, otherwise `Release`.
- A script can use `#import "io"`, `#import "devices"` and so on, exactly as in the editor.

`Build` does all the compiling - after it returns, `Run` can start immediately, or later, or never.

## Handle errors

Two different things can go wrong, and they show up in different places.

**The script does not compile.** `Build` throws. `CompileErrors` turns the exception into readable messages - the resolver and the compiler collect *all* errors, not just the first one:

```csharp
using fire.Bytecode;
using fire.Compiler;

try
{
    session = RuntimeSession.Build(sources, null, writer);
}
catch (Exception ex) when (ex is ParseException or ResolverException or CompilerException
                              or NotSupportedException or PreprocessorException)
{
    foreach (var message in CompileErrors.Messages(ex))
        Console.Error.WriteLine(message);
    return;
}
```

Any other exception type is a bug in the tooling, not in the script, and should stay visible.

**The script throws and nobody catches it.** `Run` returns normally; the exception is on the virtual machine:

```csharp
session.Run();

var vm = session.VirtualMachine!;
if (vm.UnhandledException != null)
    Console.Error.WriteLine(new UncaughtScriptException(vm.UnhandledException).Message);
```

Exceptions that the script handles itself with `try`/`catch` never reach you.

## Capture the output

`print` goes wherever the `debugWriter` of `Build` sends it - into a log window, a `StringBuilder`, a network stream. Without a `debugWriter`, `print` does nothing.

`IO.Stdio` (the script's standard input, output and error streams) is separate and controlled by the `ioStdio` argument. The default is the real console; `IoStdio.Custom` redirects it:

```csharp
using fire.IO.Bridge;

var output = new StringBuilder();
var stdio = IoStdio.Custom(
    output: text => output.Append(text),
    error: text => output.Append("[error] ").Append(text),
    input: new MemoryStream(Encoding.UTF8.GetBytes("typed by the user\n")));

var session = RuntimeSession.Build(sources, null, writer, ioStdio: stdio);
```

## Limit what a script may do

Scripts that come from users should not be able to touch the whole file system. The host decides with an **`IoPolicy`** which files the `IO` library may open:

```csharp
using fire.IO.Bridge;

var policy = IoPolicy.Rooted(@"C:\Sandbox\Scripts");              // only below this folder
var readOnly = IoPolicy.Rooted(@"C:\Sandbox\Data", readOnly: true);
// IoPolicy.AllowAll (the default) and IoPolicy.DenyAll also exist.

var session = RuntimeSession.Build(sources, null, writer, ioPolicy: policy);
```

A script that steps outside gets an error instead of access to the file. The same policy also applies to image files loaded by the `graphics` library.

You can also pass the **`DeviceManager`** your application already uses (`deviceManager:`), so scripts talk to the devices your application has open instead of creating their own. The editor works exactly like this: its manager is shared between all runs, so connections survive from one run to the next.

For anything untrusted you additionally want to decide which libraries are available. The `#import` directives are plain text in the source, so check the source before you build it - or choose not to ship the bridge DLLs at all.

## Stop a script and read its exit code

`terminate(value)` in a script stops the whole program at once. If the value is an integer, it is meant as an exit code:

```csharp
session.Run();

var exit = VM.ExitValue;          // undefined if the program ended normally
int code = exit.Kind == ValueKind.Int ? (int)exit.AsInt() : 0;
```

The host can use the same emergency stop from outside, for instance behind a *Stop* button:

```csharp
VM.RequestTerminate(Value.MakeUndefined());
```

The signal is global to the process (`terminate` is an emergency stop for *all* threads of the program) and the first call wins. The next call of `Run` starts with a clear signal again.

## Run on a background thread

`Run` blocks until the script is done, so a user interface application starts it on its own thread:

```csharp
var thread = new Thread(() =>
{
    session.Run();
    // report the end of the run (and session.VirtualMachine!.UnhandledException) to your UI
}) { IsBackground = true };
thread.Start();
```

The `writer` callback is then called on that thread - marshal to your UI thread before you touch a control.

Scripts can start threads of their own with `fire`. `Run` returns when the main program has ended; the rest is regulated by the script (`leave`, `terminate`), see the threading chapter of the specification.

## Build a standalone executable

The same linker produces a single executable that runs without your host. Pass the output name to `CompileAndLink`:

```csharp
using fire.Compiler;

new Linker().CompileAndLink(sources, debugWriter: null, outname: "hello.exe", executionModeOverride: null);
```

The executable contains the program and - only for the libraries that the script `#import`s - the necessary bridge DLLs. Directives in the script such as `#name`, `#version`, `#icon` and `#noconsole` set its version information, icon and subsystem.

To pack, the **folder of the host** must contain `fire.Runtime.exe`, `fire.Runtime.dll` and `fire.Runtime.runtimeconfig.json` in addition to the DLLs listed above. With a project reference to `fire.Compiler` they are copied along with everything else.

## Call your own C# code from a script

A script declares a native function with `extern`:

```
extern int HostAdd(int a, int b)
print(HostAdd(2, 3))
```

and the host links the implementation through an `ExternRegistry` - by name, after compiling. This needs the lower level of the API, because `RuntimeSession` does not expose the registry:

```csharp
using fire.Bytecode;
using fire.Compiler;
using fire.Runtime;
using fire.Values;

var natives = NativeRegistry.CreateDefault();            // print() and the base types
var externs = new ExternRegistry();
externs.Register("HostAdd", args => (long)args[0]! + (long)args[1]!);

var program = Parser.Parse(script);
var resolved = Resolver.Resolve(program, natives.Names);
var compiled = Compiler.Compile(program, resolved, natives);

var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, externs);
vm.Run();
```

Arguments arrive already converted to .NET types (`bool`, `long`, `double`, `char`, `string`; `IntPtr` for pointers) and so does the return value (`null` for functions without a return type).

This path skips the linker: there is **no prelude** (so no string methods or collection classes written in fire), and no `#import` or `#include`. Use it for small, self-contained scripts; for everything else use `RuntimeSession` and, if you need host code, `#extern "library"` to call into a native library instead.
