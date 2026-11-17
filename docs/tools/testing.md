# Testing: `forge test`

fire has a small test framework, built into the language and the compiler:

- a test is a method with the attribute **`[Test]`**,
- a failed check throws an **`Assertion`** (an `Exception` of the core prelude),
- **`#import "testing"`** brings the class **`Testing.Assert`** with the usual checks,
- **`forge test`** finds the tests, runs them and reports.

```fire
#import "testing"

class Calc {
    static int Add(int a, int b) { return a + b }
}

class CalcTests {
    [Test "Adds two numbers", "2 + 2 is 4"]
    undefined Adds() {
        Testing.Assert.AreEqual(4, Calc.Add(2, 2))
    }

    [Test]
    [Parameters 1..3, 10]
    [Parameters -1, 1]
    undefined Commutes(int a, int b) {
        Testing.Assert.AreEqual(Calc.Add(a, b), Calc.Add(b, a))
    }
}
```

```
$ forge test calc.fire
Tests of calc.fire:
  ok      CalcTests.Adds two numbers
  ok      CalcTests.Commutes(1, 10)
  ok      CalcTests.Commutes(2, 10)
  ok      CalcTests.Commutes(3, 10)
  ok      CalcTests.Commutes(-1, 1)
5 tests: 5 passed, 0 failed, 0 errors (0.12 s)
```

## Running the tests

```
forge test                        the solution (or project) in the current folder
forge test Demo.firesln           every project of the solution (-p <name>: only that one)
forge test Lib/Lib.fireproj       one project
forge test a.fire b.fire          files (joined into one program, like `forge run`)
forge test ... --engine native    as a native program for this machine (built with the toolchain of fire.native.json)
forge test ... --filter A.B,C     only these tests: Class.Method, or a class for all its tests; * stands for any text (CalcTests.*, *.Add*)
```

With `--filter`, a class without a chosen test is left out completely (no object, no `[PrepareTesting]`). A class whose `[CleanTesting]` checks that all its
tests ran will of course complain when only some of them run.

`-m`, `-f`, `-D`, `--types`, `--config` and `--toolchain` work as with `run`/`build`. The tests run in the virtual machine unless `--engine native` is given.

While the tests run, a console shows a spinner (`\ - / |`) with the test that is running and the count so far. At the end comes the list of the tests:
`ok`, `FAILED` (an `Assertion`: a check did not hold) or `ERROR` (any other exception, or a `[CleanTest]` that threw), with the message, the description of the
test and what the test printed. Problems that are not a single test (an attribute in the wrong place, a class whose object could not be made, a `[PrepareTesting]`
that threw) are listed as `PROBLEM`. The exit code is 0 when everything passed, otherwise 1.

**The top level does not run.** `forge test` compiles the program with its tests, but the statements at the top level of its files (the entry point of a program)
are left out - only the declarations count. Global variables are therefore not set in a test; give a test class what it needs in `[PrepareTesting]`.

## Attributes

Attributes stand in square brackets on the lines before a method (see [Classes](../language/08-classes.md#attributes)); the test runner reads these:

| Attribute | Meaning |
|---|---|
| `[Test]`, `[Test "Title"]`, `[Test "Title", "Description"]` | The method is a test. The title replaces the method name in the list; the description is shown when the test fails. |
| `[Parameters v1, v2, ...]` | The values for the parameters of the test, one per parameter. Can be given several times: the test runs once for each. A range (`1..5`) runs it for each of its numbers; several ranges in one `[Parameters]` run every combination. An array (`[1, 2]` or `{1, 2}`) is passed as one value. |
| `[PrepareTest]` | Runs before every test of the class. If it throws, the test fails with that exception. |
| `[CleanTest]` | Runs after every test of the class, also after a failed one. If it throws, the test is an error. |
| `[PrepareTesting]` | Runs once, before the first test of the class. If it throws, no test of the class runs. |
| `[CleanTesting]` | Runs once, after the last test of the class. |

The arguments of an attribute are ordinary expressions (`[Parameters "a" + "b", new List<int>(), Color.Red]`); they are evaluated when the test runs.

**One object per class.** Like NUnit, the runner makes ONE object of every class that has tests (`new Name()` - the class needs a constructor without
arguments) and runs all its tests on it, in the order of the source. Fields therefore carry over from one test to the next; reset them in `[PrepareTest]`. A
static method can be a test, too. A generic class cannot hold tests.

## `Testing.Assert`

`#import "testing"` (the standard package `fire-testing`, pure fire: the same in the VM and native). Every check takes an optional message as its last argument;
it comes first in the text of the `Assertion`.

| Check | Holds when |
|---|---|
| `AreEqual(expected, actual)` | `expected == actual` (numbers with units compare by value: `100cm == 1m`) |
| `AreNotEqual(notExpected, actual)` | `notExpected != actual` |
| `AreNear(expected, actual, tolerance)` | the numbers differ by at most `tolerance` |
| `AreSequenceEqual(expected, actual)` | two arrays (or lists) have the same items in the same order |
| `IsTrue(condition)`, `IsFalse(condition)` | |
| `IsUndefined(value)`, `IsNotUndefined(value)` | |
| `Contains(part, whole)` | a string contains `part`, or an array/list contains the item `part` |
| `Throws(() => { ... })` | the lambda throws; returns the exception (`Testing.Assert.IsTrue(e is of ArgumentException)`) |
| `DoesNotThrow(() => { ... })` | the lambda does not throw |
| `Fail(message)` | never: the test fails at once |

An `Assertion` can also be thrown directly: `throw new Assertion("the cache is empty")`.

## Where tests live

By convention the tests of a project are in the folder **`testing/`** of the project (like the C++ in `native/`). The files there belong to the project only
for `forge test`: `build`, `run` and the package of a library leave them out. Tests may stand in any other file, too - then they are also part of the program
(they do not run, they are just classes).

The template **Test Class** (Add > New Item) makes a class with a test and a test with parameters; a new **Library** project brings `testing/` with a first test.

## The Test Explorer (spark)

**Test > Test Explorer** (or View > Test Explorer) opens the tests in the editor, a panel next to the Solution Explorer. The box at the top picks the scope:

- **Solution** - every project of the open solution (one group per project);
- **Project** - the project of the active document (also when the document is in its `testing/` folder), else the one selected in the Solution Explorer,
  else the startup project;
- **Current File** - the tests of the active file: in a project, the project is compiled with its tests and only the tests of this file run; a file that belongs
  to no project runs alone (like `forge test file.fire`).

The tree shows project, class and test, with the title of `[Test]`. The explorer finds the tests without compiling anything (it only parses the files, also the
unsaved text of open documents) and looks again when a file is saved, the workspace changes or - for Project and Current File - another document becomes active.

The buttons: **Run all** (Ctrl+R, also Test > Run All Tests), **Run selected** (the tests below the selected row: a test, a class or a project), **Run failed**,
**Stop** and **Refresh**. Each group runs in the virtual machine, like `forge test` (the same driver, so `[PrepareTesting]`, `[PrepareTest]` and the others
work the same). The results come in while the tests run: ✔ passed, ✖ failed (an `Assertion`), ⚠ error (another exception), ▶ running, ◌ waiting, ○ not run.
A test with `[Parameters]` gets one row per set of values. Below the tree, the details of the selected row: the description, the message, what the test printed
and the file and line; for a class or a project the tests that failed and the problems of the run (a program that does not compile, an attribute in the wrong
place). A double click (or Enter) opens the test in the editor.

Stop ends the run at the next instruction of the VM; the test that was running is an error "cancelled". A program that is started with F5 and a test run do
not run at the same time - stop the program first.


## The test suite of the compiler (`src/fire.Testing`)

The compiler, the VM, the native backend and the libraries are checked by `fire.Testing` (`dotnet build -c Release`, then `dotnet bin/Release/net10.0/linux-x64/fire.Testing.dll`).
A full run takes a while, most of it the native backend: every case is built with gcc and AddressSanitizer/UBSan and compared with the VM (in parallel, see below). To check a change
quickly:

| Switch | Effect |
|---|---|
| `FIRE_TESTS_ONLY=<group>` | only one group: `native` (the native backend: cases against the VM, windows, FreeRTOS, toolchains), `ui` (the UI library in the VM), `types`, `syntax`, `diagnostics`, `projects`, `testing`, `windows`, `dialogs`, `docking`, `circuiteditor`, `scene3d`, `snippets`, `notes`, `drivers` (the device drivers of the packages in spark's driver agent, as plugins and through `#import`), `debugger` (the debugger without a window: the stepping run of the VM, the debug targets of `fire.Debugging` - the VM, and the native one against a fake agent - and spark's `DebugSession`), `packer` (the packer and the wasm target: with Emscripten on the PATH a program under node and a window in headless Chromium), `callbacks` (the host callbacks in the VM), `graphics` (graphics from fire in the VM: colour modes, images, `Framebuffer.FromFile` against the host's IoPolicy, drawing functions) |
| ESP32 | with `IDF_PATH` set (ESP-IDF) the native group builds a program for the ESP32 and runs it in Espressif's QEMU (`FIRE_TESTS_FILTER="ESP32 (ESP-IDF"`; the project is kept in ~/.cache/fire-tests/esp32) |
| `FIRE_TESTS_FILTER=<text>` | of the checks listed as cases (the native cases, the native window and device cases, the UI checks) only those whose name contains the text; several texts separated by `\|`, case does not matter |
| `FIRE_S3D_GPU` | the test program sets it to `cpu` unless it is set already: the scene3d hardware checks then draw on a Vulkan device that is a CPU (lavapipe) and compare with software, instead of only seeing the fallback. Set it to `0` to test the fallback (no GPU at all). Without `XDG_RUNTIME_DIR` the program makes a private one for Vulkan |
| Native debugger | the native group drives debug builds over the debug protocol (`NativeDebugTests.cs`, [Debug-Protokoll nativer Programme](../spec/NATIVE_DEBUG_PROTOCOL.md)) and compares them with the VM: `FIRE_TESTS_ONLY=native FIRE_TESTS_FILTER="Native-Debugger"` (gdb, clang, tcc, MinGW and Wine are used when they are there); the framed transports: `"Native-Debugger: stdio"`, `"Native-Debugger: FreeRTOS"` (the simulator), and with `IDF_PATH` an ESP32 debug build on QEMU: `"Native-Debugger: ESP32"` (project in ~/.cache/fire-tests/esp32-dbg; the first build of ESP-IDF takes minutes) |
| `FIRE_TESTS_ONLY=linkage` | (`FIRE_TESTS_FILTER=plugin`: only the fire plugins, `=plugindebug`: only debugging a plugin, `=library`: only `Library.Load`/`Get`, `=vmgraphics`: only the VM's library of graphics and windows - built for Windows with MinGW, for Linux with `--no-undefined`, `new Window` without a display throws why; with `FIRE_TEST_WIN_FORGE=<folder with forge.exe for win-x64>` the VM of Windows opens a ui window under Wine, with release:) linkage "static" of native builds (ROADMAP 9): a program with several packages from the cache, the second build without a compile of a library, a project whose library changes, the interface files, the classes of the program and of the libraries meeting (every module serves its own, the others through the tables of the runtime) and the size of the program's C, the stripping levels 2 and 3 with static libraries (the program strips like with source, a library keeps its API), a breakpoint in a static library, TinyCC, MinGW/Wine when there, `ember cache clean` |
| `FIRE_MODULES=1` | every native build is generated from modules in one file (the libraries as linkage "static" would build them, ROADMAP 9 phase A): run the native group with it to check that the modules behave like the whole program; `FIRE_MODULES_LOG=<file>` lists the programs that could not be split |
| `FIRE_TEST_NO_NATIVE=1` | everything but the native backend |
| `FIRE_TEST_CCACHE=0` | no compiler cache |
| `FIRE_TEST_JOBS=<n>` | how many native builds and test programs at a time (default: the cores of the machine); `1` builds and runs one case after the other |
| `FIRE_TEST_SANITIZE=0` | the native cases without AddressSanitizer/UBSan (by default they run under them when the compiler has them): the paths the runtime keeps for the sanitizers' own checks then run - the pool of small objects (fire_rt.h objMemAlloc) instead of malloc per object |
| `FIRE_TEST_OPT=<flags>` | the optimization of the native test builds (default `-O1`; e.g. `-O2` for a run that looks for the warnings only `-O2` finds) |

The window cases of the native group (filter `Fenster`) run every window program with fire's own display without a screen (`FIRE_DISPLAY=headless`) and on Wayland (`weston`); the
clipboard of X11 needs `xvfb-run` (one program, two programs, the VM as the other one), the Windows cases MinGW and Wine; the VM's Windows clipboard a `dotnet publish -r win-x64` (the runtime
pack for win-x64 comes from NuGet once). Drag-out on Wayland needs `weston` (it runs with its X11 backend under Xvfb). A case whose tools are missing is skipped with a note.

When `ccache` is installed, the native builds of the tests go through it: a case whose generated program did not change is not compiled again (a run of the native group
with an unchanged program takes seconds per case instead of a minute). Example: `FIRE_TESTS_ONLY=native FIRE_TESTS_FILTER="Drop|Compression:"` runs the native checks of
two topics in under a minute.

**In parallel.** The native group builds and runs its cases on all cores (`FIRE_TEST_JOBS`). First the VM runs all cases, one after the other and alone (the
precision of `float` and the handlers of `terminate` are process-wide while a program runs, and some thread cases depend on timing that a machine busy with
compilers upsets); then the C of the cases is generated in parallel and handed to workers that compile (at most `FIRE_TEST_JOBS` compilers at a time - with
AddressSanitizer a compiler needs hundreds of MB; the biggest program first, a case with the ui library compiles for minutes) and run the programs.
The parts after that - the debugger, the devices, the windows with the UI, the cases under Wine, FreeRTOS - run at the same time, too. The output does not change: every
part prints its checks in the order of its cases, a part's lines are held back until the parts before it are done, and `OK:`/`FEHLER:` come in the same order as with
`FIRE_TEST_JOBS=1`. What cannot run at the same time takes turns: the cases under Wine share one prefix, every `xvfb-run` gets an X server of its own (`-n`, a number
far from those of `-a`), a test program gets its variables (`FIRE_DISPLAY`, `LSAN_OPTIONS`) per process. The cases are built with `-O1` (`FIRE_TEST_OPT`) - with
the sanitizers 20-30 % less compile time than `-O2` (a case of 1.3 MB C: 33 s instead of 48 s) and the same memory errors; a few warnings of gcc appear only with
`-O2` (`FIRE_TEST_OPT=-O2` looks for them), the checks that are about `-O2` itself name it.

### The cases as files (`tests/cases`)

The cases "native == VM" - a program that must print the same in the VM and as a native program - are files, not text in the test program
(`TestCases.cs`; [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md), L5: the same files will check the VM and the compiler in C):

```
tests/cases/native/0420-records-gleichheit-nach-inhalt.fire        the source, exactly; then lines about the case
tests/cases/native/0420-records-gleichheit-nach-inhalt.expected    what the program prints (every line of print, ended by a line feed)
tests/cases/native/0420-records-gleichheit-nach-inhalt.unhandled   only if the program ends with an exception nothing catches: the line of the command line
```

A `.fire` file ends with its **title** - `//# name: Records: Gleichheit nach Inhalt` (the name `FIRE_TESTS_FILTER` looks at; a title that starts with
`Abbruch:`, `Threads:`, ... is treated as before) - and may have `//# note: ...` lines before it (a remark) and `//# output: varies` (what the program
prints differs from run to run or from machine to machine: no expected output, the VM and the native program are only compared with each other). These
lines are not part of the program: the source is everything before them, so the lines of the program keep their numbers. The files are UTF-8 with line
feeds and git never converts them (`.gitattributes`: an output may hold a carriage return on purpose). The cases run in the order of their file names; the numbers leave room to put a case between two others.

The native group checks every such case three ways: the VM prints the expected output (`OK: VM: <title>: die erwartete Ausgabe`), the native program
prints what the VM printed, and - as before - the special runs (sanitizers, FreeRTOS, tcc, ...) pick their cases by title.

**A new case:** write `<number>-<name>.fire` with its title line, then record what the VM prints and look at it before you check it in:

```
FIRE_TESTS_ONLY=native FIRE_TESTS_UPDATE=1 dotnet fire.Testing.dll     # writes the .expected (and .unhandled) of new and changed cases, runs nothing else
FIRE_TESTS_ONLY=native FIRE_TESTS_FILTER="<title>" dotnet fire.Testing.dll
```

A case without an expected output is a failed check that says so, and so is a case the compiler refuses (`WIRD NICHT UEBERSETZT: <errors>`).

**The cases that only run in the VM** are files too, one folder per check of the test program (`tests/cases/vm-perf`, `vm-life`, `vm-shutdown`, `vm-gl`,
`vm-lq`, `vm-rf`, `vm-sc`, `vm-sc-checked`, `vm-ext`, `vm-arr`, `vm-clone` and `vm-ext-error`, `vm-arr-error`, `vm-clone-error`): the same `.fire` and
`.expected` - an exception nothing catches is the last line there, `UNBEHANDELT: <message>` - and, for a case that is no program, `.error` instead of
`.expected`: a part of the compiler's message. More lines about a case: `//# modes: Debug Release` (the execution modes of the VM it runs in; without the
line those of its check, usually all three) and `//# packed` (it also runs as a packed program does: written as a program file and read back). The test
program runs each folder with the check it came from (`foreach (var c in TestCases.Load("vm-perf")) CheckPerf(...)`), in the place the calls were. These
expected outputs are written by hand, as they were in the code; `FIRE_TESTS_UPDATE` does not touch them. What hangs on the host - the UI with its fake
renderer, the device manager, host callbacks, the rules for files and the network - stays code. `FIRE_TESTS_COLLECT=<file>` writes what every such check
checks (title, source, expected lines) as one JSON object per line: the list before and after moving cases must be the same.

`tests/cases/smoke` holds the oldest tests of the language - one small program per feature, run the way `forge run` runs it, compared with the output
next to it (`//# note:` keeps the sentence the test program used to print for a reader instead of checking).

`tests/cases/diag`, `types` and `syntax` hold every source the checks of `DiagnosticTests`, `TypeTests` and `SyntaxTests` compile, each with what forge
says about it: `.diagnostics` is the error output (the warnings and errors with their codes and lines, an exception nothing catches), `.expected` what
the program prints; `//# types: strict` runs the case with `--types strict`. These files are recorded, not written by hand
(`tests/tools/cases.py record <forge> tests/cases/types`): they say what the compiler does today, the checks in C# say what is meant - a new message or
a moved line shows up as a difference to look at.

**Without the test program:** `tests/run-cases.sh <forge> [<group>...]` runs every case with `forge run` and compares what it prints - the check a forge
and a VM in C will be measured with, and a quick one for the ones in C# (all 630 cases: a few minutes). A case that only makes sense inside the test
program says so: `//# skip: forge run - <why>`. `tests/reference.sh <forge> <folder>` writes what the compiler makes of every case (the program as
text, the program file, the C) to compare two compilers.

**Cases that master adds.** As long as master holds these cases as text in `Program.cs`, `tests/tools/merge-master.sh` merges it: the cases master added
or changed go into the files (`tests/tools/cases.py`: a new case gets a number between its neighbours, a changed one loses its expected output, a case
keeps its file as long as it keeps its title), the rest of `Program.cs` is merged as usual. Then build, record with `FIRE_TESTS_UPDATE=1`, look, commit.
 Cases whose source is made at run time (a certificate, a folder of the run, a database
server of the machine) stay in `Program.cs` (`natCases`), next to the place that makes what they need. `FIRE_TESTS_LISTING=<file>` (with
`FIRE_TESTS_ONLY=native`) writes the titles and sources of all cases as one text and runs nothing - to compare the set of cases before and after moving some.

**Progress.** Every check line carries its number and the number of checks of the last run with the same `FIRE_TESTS_ONLY` and `FIRE_TESTS_FILTER`:
`OK: [123/530] Native == VM: ...`, `FEHLER: [124/530] ...` (`?` on the first run; the counts are kept in `~/.cache/fire-tests/totals.json`). The status word stays first,
so `grep '^OK:'`, `grep -c FEHLER` and the like keep working. Every line is written at once (a log of a run in the background grows line by line), and while a long
phase prints no check, a line `[ .. ] native Faelle gebaut und gelaufen 37/212 (120 s)` appears every 10 seconds.
