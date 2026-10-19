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
```

`-m`, `-f`, `-D`, `--types`, `--config` and `--toolchain` work as with `run`/`build`. The tests run in the virtual machine unless `--engine native` is given.

While the tests run, a console shows a spinner (`\ - / |`) with the test that is running and the count so far. At the end comes the list of the tests:
`ok`, `FAILED` (an `Assertion`: a check did not hold) or `ERROR` (any other exception, or a `[CleanTest]` that threw), with the message, the description of the
test and what the test printed. Problems that are not a single test (an attribute in the wrong place, a class whose object could not be made, a `[PrepareTesting]`
that threw) are listed as `PROBLEM`. The exit code is 0 when everything passed, otherwise 1.

**The top level does not run.** `forge test` compiles the program with its tests, but the statements at the top level of its files (the entry point of a program)
are left out - only the declarations count. Global variables are therefore not set in a test; give a test class what it needs in `[PrepareTesting]`.

## Attributes

Attributes stand in square brackets on the lines before a method (see [Classes](language/08-classes.md#attributes)); the test runner reads these:

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
