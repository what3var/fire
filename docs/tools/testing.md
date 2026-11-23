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
`ok`, `FAILED` (an `Assertion`: a check did not hold) or `ERROR` (any other exception, or a `[CleanTest]` that threw), with the message and its stack trace (docs/spec/STACKTRACE.md: from the test on - the frames of `Assert` and of the driver are left out, so a `FAILED` starts at the line of the `Assert`), the description of the
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


## The tests of fire itself

Since milestone M3 of [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md) forge and the VM are C programs and their tests are **data**: the cases of `tests/cases`
(below), run by **`forge conformance`** without .NET. The checks in C# that tested the C# forge - the native backend against the VM with its sanitizer runs,
windows, FreeRTOS and Wine, the linkages, the C# frontend (`DiagnosticTests`, `TypeTests`, `SyntaxTests`), the program file, the stack traces, the packer,
the registry of the C# ember, BIOS under QEMU, scene3d, the AI programs - went with it; what of them is not a case yet is listed in
[FORGE_C.md](../spec/FORGE_C.md#phase-d-schritt-6-conformance-und-die-distribution-ohne-net) ("Was in fire.Testing bleibt").

### The test program of spark (`src/fire.Testing`)

`fire.Testing` checks spark and the C# libraries it keeps (`fire.Tooling`, `fire.Project`, `fire.Git`, `fire.Imaging`, `fire.UI.Markup`): its build puts forge,
the VM, `native/`, `Source/` and ember next to it (`src/ForgeDist.targets`; `FIRE_FORGE` names another forge), it installs the standard packages into a store
of its own for the run (`forge install-standard`, `FIRE_PACKAGES`) and runs its groups - all of them, or one with `FIRE_TESTS_ONLY=<group>`:

| Group | What it checks |
|---|---|
| `resume` | the key and the records of resume and shards (a second, nothing built) |
| `startproject` | the start project of a solution, the order of Build Solution and Clean |
| `explorer` | the filter, Sync and Show All Files of the solution explorer (the folders of a project: forge compiles it) |
| `forgereport` | spark's runner of `forge build --report json`: the report written and read, a fake forge with Cancel, the real forge on a library |
| `editorchecks` | how spark checks documents in the background (debounce, cancellation, stale results, the gate) and the protocol of `forge serve` |
| `cvmrun` | spark's Run and Debug in `fire`: a fake `fire`, then forge and `fire` (an overlay, an error, a console and a window program, the debugger) |
| `previews` | the design view of markup and the block preview, drawn by programs forge compiles and `fire` runs |
| `projects` | projects, solutions, templates, samples, packing a library and installing it (forge and ember), the analysis of a project's documents (forge serve) |
| `serve`, `completion` | the language services over `forge serve`: diagnostics of cases and of projects (what each must say), the completion (visibility, the type of `x.`, the order) |
| `notes`, `git`, `circuiteditor`, `blocks`, `mesh`, `setupwizard`, `mcpserver`, `aimodels`, `image` | the notes, git and the git panel, the circuit editor (its view in `fire`), the block and mesh models, the setup wizard (its downloads are `forge toolchain-setup`), the MCP server, the model catalog, the image codecs |
| `debugger` | spark's `DebugSession` against a fake agent and on programs in `fire` (and natively built by forge, when there is a C compiler) |
| `drivers` | the device drivers of the installed packages in spark's driver agent (compiled by forge) |

`FIRE_TESTS_RESUME`, `FIRE_TESTS_SHARD`, `FIRE_TESTS_FILTER` (where a group reads it) and the progress lines are as they were (below).

### The cases as files (`tests/cases`)

The cases "native == VM" - a program that must print the same in the VM and as a native program - are files ([ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md), L5:
the same files check the VM and the compiler in C; `forge conformance` runs them):

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
tests/tools/cases.py record <forge> tests/cases/native       # writes what forge and the VM say of the cases (M3: the C# test program is gone)
forge conformance native --filter "<title>" --engine all
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

**With the VM in C:** `tests/run-cases-vm.sh <forge> [<group>...] [--cache <dir>] [--fire <fire>]` compiles every case with the forge to a program file
(`forge bytecode`, in each mode of the case) and runs it with the VM in C (`native/vm`, built with `cc` unless `--fire` names one), by the same rules -
the measure of phase B of [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md); `--cache` keeps the program files between runs ([VM_C.md](../spec/VM_C.md)). The
libraries of the packages' natives come from the folder of the forge (its `Packages/` and `Prebuilt/`: `FIRE_PKG_PATH`).

**Without .NET at all: `forge-c conformance`** (`native/forge/tools/conformance.c`, [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md) phase D step 6) is the same
runner inside the forge in C - the core of this test program that is data: the cases of `tests/cases` by the rules of the two scripts (and of `TestCasesVm`),
and with `--engine native` the cases "native == VM" of the group native built as native programs and compared the way `Program.cs` compares them
(`Abbruch:` with its exit code 1 and the line on stderr, `(Meldung auf stderr)`, `(Http.Pages-Zeile auf stderr)`, `Threads:` with `terminate(n)`; a case with
`//# output: varies` against what the VM prints of it).

```
forge-c conformance                                   every group in the VM in C (the six variants of the VM are built into the user's cache once)
forge-c conformance smoke vm-gl native/04 -j 2        groups, or the cases of a group whose names start so; two jobs at a time
forge-c conformance native --engine native            the group native as native programs (the C compiler forge chooses; --toolchain gcc)
forge-c conformance --engine all --filter "Records"   both engines, only the cases whose title or name contains the text (| between several)
forge-c conformance --cache ~/.cache/fire-cases       keeps the program files; a second run only runs the VM
```

Every case is compiled by forge-c in a process of its own (`forge-c bytecode`, `forge-c build --engine native`) in the folder of the case, and run as a
process with a timeout (the VM: 20 s, `VM_TIMEOUT`; a native program: 120 s), so a case that crashes or hangs takes only itself along. The default is half
the cores, at most 4 (`-j`). A case that is wrong runs once more alone at the end (`--no-retry`: not). The output lists what is not right with the first line
that differs (`--list`: every case), then the counts per group and engine; the exit code is 0 when every case that ran is right. forge-c finds the cases in
`--cases`, `FIRE_CASES` or the nearest `tests/cases` above the working folder, the VM with `--fire` or `FIRE_VM_EXE` (its variants next to it), otherwise it
builds the VM of its own sources (`forge-c build-vm`). On 23.11.2026: 1173 of 1173 cases right, the same verdicts as `tests/run-cases-vm.sh` with forge-c.

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

### Resume and shards

A full run of the native group takes hours on a small machine. Two switches let it be stopped and continued, and split:

**`FIRE_TESTS_RESUME=<dir>`** (or `1` for `~/.cache/fire-tests/resume`) records every check line in `<dir>/<time>-<pid>.jsonl` the moment it is printed -
one JSON object per line, flushed per line: the title, OK or FEHLER (with the lines after it), the **unit** the check belongs to and the unit's **key**.
When a unit has printed all its checks, a line marks it finished (synced to the disk). A run that is killed (a restart of the session, a full disk) keeps
everything finished so far. The next run with the same folder skips every unit whose last finished run with the same key had only OK checks and prints
its checks again as `OK (fortgesetzt): [n/total] title` (counted like `OK:`; `grep '^OK'` finds both); a unit that failed, did not finish or whose key
changed runs again. At the end: `[ resume ] 412 Einheit(en) fortgesetzt (830 Pruefung(en) 'OK (fortgesetzt)'), 17 gelaufen`.

The units are where the work is, not every single check:

| Unit | Key (besides the build, below) |
|---|---|
| a case "native == VM" (`Native == VM: <title>`, with its `VM: <title>: die erwartete Ausgabe`) | its source, `.expected`, `.unhandled`, `output: varies`, the sanitizer flags |
| a debugger case (`Native-Debugger: <case>`, `VM-Debugger: <case>`), a check of the protocol | the script, breakpoints and plan; the title |
| a window case (`Fenster: <title>`, also Wayland), a device case (`Geraete nativ: <title>`), FreeRTOS and green threads per case | what is built and run, the expected lines (for FreeRTOS: what the VM printed) |
| the checks of the native group that stand under a title of their own (ThreadSanitizer, TinyCC, Teilen, Weglassen, console, the clipboard under Xvfb/Wayland/Wine, UI nativ == VM, ESP32) | the title |
| a group of a test class (`ProjectTests`, `TypeTests`, ... - in the full run and with `FIRE_TESTS_ONLY`) | the files of `tests/cases` |

The key of every unit also holds a hash of **the build**: the MVID of `fire.Testing.dll` and the contents of the files next to it (the fire assemblies,
forge, the standard packages as `Source/` - not the .NET runtime), the files of `native/` and `packages/` of the repository, the variables `FIRE_TESTS_ONLY`,
`FIRE_SPLIT`, `FIRE_SPLIT_UNIT_KB`, `FIRE_MODULES`, `FIRE_TEST_OPT`, `FIRE_TEST_SANITIZE`, `FIRE_TEST_NO_NATIVE`, `FIRE_TEST_GIL`, `FIRE_S3D_GPU`, `FIRE_GFX_GPU`,
`FIRE_TEST_WIN_FORGE`, `IDF_PATH`, `CC`, `CXX`, and the compilers on the PATH (gcc, g++, clang, tcc, MinGW, wine, ccache: the file each resolves to, its size
and time). So a rebuilt test program or compiler runs everything again, while a changed `.expected` reruns only its case. `FIRE_TESTS_FILTER` is not part
of the key: a run with a filter continues a full one and the other way round; `FIRE_TESTS_ONLY` is, so `FIRE_TESTS_ONLY=native` does not continue a full run.
The checks that stand alone in `Program.cs` (no unit) always run - their work is done before the check line is printed, so there is nothing to skip; they
are recorded (unit `""`, for the reader of the file) but never resumed. With a narrow filter they are most of the time of a resumed run (about a minute). `FIRE_TESTS_UPDATE=1` ignores the records.

**`FIRE_TESTS_SHARD=<i>/<n>`** runs only the units whose title hash (SHA-256 of the title, modulo n) is part i: `1/2` and `2/2` together run every unit
once, on two machines or in two sessions (the parts are as big as the hash makes them, not balanced by time); the checks outside units run in every part. A unit inside a unit (a group) is not split again. Combined with
resume, every part records into the same folder (each run its own file) and a later run without a shard continues from all of them. At the end:
`[ shard ] Teil 1/2: 214 Einheit(en) hier, 209 in den anderen Teilen (Einheiten je Teil - 1/2: 214, 2/2: 209)`.

```
FIRE_TESTS_ONLY=native FIRE_TESTS_RESUME=1 dotnet fire.Testing.dll                          # stop it any time, start it again: it goes on
FIRE_TESTS_ONLY=native FIRE_TESTS_RESUME=1 FIRE_TESTS_SHARD=1/2 dotnet fire.Testing.dll     # session A
FIRE_TESTS_ONLY=native FIRE_TESTS_RESUME=1 FIRE_TESTS_SHARD=2/2 dotnet fire.Testing.dll     # session B
```

The folder is never cleaned by the test program: delete it (or a file of an old run) to start from zero. The VM part of the "native == VM" cases runs for
every case even when its unit is skipped - the sections after it take their expected outputs from there; it is the cheap part.

**Disk.** The native group deletes the program, objects and sources of a case as soon as it passed (unless `FIRE_TESTS_KEEP=1`), and the object files of
every build once it ran (only the ones its own commands wrote); a failed case keeps its files in `/tmp/fire-native-test-*` for a look. Before a build it checks the free space
(`FIRE_TESTS_MIN_FREE_MB`, `FIRE_TESTS_DISK_WAIT`) and waits for space rather than letting every build fail.
