# Processes: `#import "process"`

The standard library `process` starts other programs, talks to them through their standard input, output and error, waits for them and reads their exit code. It
works the same in the virtual machine and in native programs (Linux, macOS, Windows); a board (ESP32, FreeRTOS) and the browser cannot start processes
(`Process.IsSupported` is `False`, `Start` throws a `ProcessException` with `ProcessCodes.NotSupported()`).

## Run a program and take its output

```fire
#import "process"

var r = Process.Run("git", ["status", "--short"])
print("exit code " + r.ExitCode)
print(r.Output)
if (r.Error != "") { print("errors: " + r.Error) }
```

`Process.Run(file, args, options, timeout)` starts the program, gives it an empty standard input, collects everything it writes and returns a `ProcessResult`
(`ExitCode`, `Output`, `Error`, as text from UTF-8). With a `timeout` (milliseconds or a `TimeSpan`) a program that runs longer is ended and a
`ProcessException` is thrown.

The program is a path or a name that is looked up in `PATH` (on Windows also with `.exe`). **Every argument reaches the program as it is** - there is no
shell in between, so blanks and quotes need no escaping. A shell command line is run by starting the shell: `Process.Run("sh", ["-c", "ls | wc -l"])`,
`Process.Run("cmd", ["/c", "dir"])`.

## Start, talk, wait

```fire
#import "process"
#import "io"

var options = new ProcessOptions()
options.RedirectInput = true
options.RedirectOutput = true
var p = Process.Start("sort", [], options)

var input = new IO.TextWriter(p.StandardInput)
input.WriteLine("pear")
input.WriteLine("apple")
input.Close()                         // the end of the input: sort writes its result

var output = new IO.TextReader(p.StandardOutput)
foreach (line in output) { print(line) }   // apple, pear
p.WaitForExit()
print(p.ExitCode)
p.Close()
```

| `ProcessOptions` | |
|---|---|
| `WorkingDirectory` | the folder the program starts in (default: the current one) |
| `Environment` | its environment variables as a `Dictionary<string, string>` (default: those of this program, `Environment.Variables`, with what it `Set`) |
| `RedirectInput`, `RedirectOutput`, `RedirectError` | the pipe becomes a stream of this program (otherwise the process uses the console of this program) |
| `MergeError` | the standard error goes into the standard output: one stream, in the order it was written |

| `Process` | |
|---|---|
| `Process.Start(file, args, options)` | starts it and returns at once |
| `StandardInput`, `StandardOutput`, `StandardError` | `ProcessStream`s (IO streams) of the redirected pipes; `new IO.TextReader(...)` / `new IO.TextWriter(...)` read and write text |
| `WaitForExit()`, `WaitForExit(timeout)` | waits until it has ended (`True`), or until the time is up (`False`) |
| `HasExited`, `ExitCode`, `Id` | whether it has ended, its exit code (a process ended by a signal: 128 + its number), its process id |
| `Kill()` | ends it at once |
| `OnOutput`, `OnError`, `OnExit`, `Pump()` | the lines it writes as events (below) |
| `Close()` | releases the pipes; the process itself goes on if it still runs (the destructor closes too) |

Reading from a pipe waits until the process writes something or closes the pipe; `ProcessStream.ReadAvailable(buffer, offset, count)` takes only what is
there (`-1`: nothing yet). Writing waits until the pipe has taken everything.

## The lines as events

A program that shows what another program writes while it runs - a build log in an editor, a progress display - sets `OnOutput` (and `OnError`); each
complete line comes without its line break:

```fire
#import "process"

var options = new ProcessOptions()
options.MergeError = true             // errors and output in one stream, in their order
var build = Process.Start("make", ["all"], options)
build.OnOutput = (string line) => print("make: " + line)
build.OnExit = (int code) => print("make ended with " + code)
build.WaitForExit()
```

The events never interrupt the program: they are delivered by `Pump()`, which reads what is there **without waiting**, and by `WaitForExit()` and
`HasExited`, which call it. A user interface calls `Pump()` in its loop (`root.onTick`), so the window stays responsive while the process runs. `OnExit`
comes once, after the last line. The output must be redirected (`RedirectOutput` or `MergeError`, `RedirectError` for `OnError`); do not read the same
stream yourself then.

## Waiting does not block the other threads

The natives of the library only ask - is there output, has the process ended - and never wait themselves; the waiting is done in fire with short `Sleep`s (1
ms at first, up to 10 ms). A fire thread that waits for a process does not hold the library: the other threads, `terminate` and the main program go on.

## Errors

Everything that goes wrong is a `ProcessException` with `code`:

| `ProcessCodes` | |
|---|---|
| `FileNotFound()` | the program was not found |
| `Permission()` | it may not be run |
| `DirectoryNotFound()` | the working directory cannot be used |
| `NotSupported()` | this platform has no processes |
| `NotExited()` | `ExitCode` of a process that still runs |
| `InvalidHandle()` | the process was closed |
| `InvalidArgument()`, `Other()` | a wrong argument; any other error of the operating system (with its message) |

## How it works

`packages/process/native/fire_bridge_process.h` (C99, also with TinyCC) is the one implementation (in the VM a shared library built from it, natively part of the program). POSIX:
`fork` and `execvp` (an error of `exec` comes back through a pipe, so `Start` reports "not found" at once), pipes that do not block. Windows: `CreateProcessW`
with anonymous pipes (the ends of this program do not wait), the command line quoted by the rules of the C runtime (each argument arrives as it was given),
a Unicode environment block. The tests: `forge test packages/process/process.fireproj` (also with `--engine native`).
