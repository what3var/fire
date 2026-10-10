using System.Collections.Generic;
using fire.Ast;
using fire.Bytecode;
using fire.Compiler;
using fire.Lexing;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;

// The standard bridges that run as packages (time, ...): built and installed into a store of their own for this run (the C++ libraries of their natives are built on first use).
var standardRoot = Path.Combine(Path.GetTempPath(), "fire-test-standard-" + Guid.NewGuid().ToString("N"));
StandardBridgePackages.Build(Path.Combine(standardRoot, "PackageSource"));
fire.Package.Manager.PackageStore.Default = new fire.Package.Manager.PackageStore(Path.Combine(standardRoot, "Packages"));
foreach (var problem in fire.Package.Manager.StandardPackages.EnsureInstalled(m => Console.WriteLine(m), Path.Combine(standardRoot, "PackageSource")).Count == 0 ? new[] { "the standard packages were not installed" } : System.Array.Empty<string>())
    Console.WriteLine(problem);

// FIRE_TESTS_ONLY=projects runs only the tests of projects, templates and packing (a quick run while working on them).
if (Environment.GetEnvironmentVariable("FIRE_TESTS_ONLY") == "projects") { ProjectTests.Run(); return; }

// `#import "io"` is a package, too: its prelude, and its natives (C++ in a library) bound to a registry; the host's policy and console are the session's (disposing ends it).
string IoPreludeSource() => fire.Package.Manager.PackageStore.Default.FindImport("io")!.ReadPrelude()!;
IDisposable UseIoPackage(NativeRegistry natives, fire.IO.Bridge.IoPolicy? policy, fire.IO.Bridge.IoStdio? stdio)
{
    var ioImport = fire.Package.Manager.PackageStore.Default.FindImport("io")!;
    var (ioNames, ioLibraries) = fire.Compiler.PackageImports.NativesOf(new[] { ioImport.Key });
    string ioLibrary = fire.Compiler.PackageLibrary.Ensure(ioImport);
    fire.Runtime.PackageNativeBinding.Register(natives, ioNames, ioLibraries, _ => ioLibrary);
    return fire.Runtime.PackageHost.Begin(policy, stdio);
}

// Small manual smoke test for lexer + parser + unit system, until the
// evaluator exists. Locally for you: `dotnet run` in the src/fire folder.

string sample = """
int a = 5mm
float b = undefined:km

var c = b + a:!
var d = b: + a:!

class Exception {
    string message

    construct(string message) {
        this.message = message
    }
}

class InvalidUnitException : Exception {
    construct(string message) : base(message) { }
}

class Foo : Bar {
    construct(int x) : base(x) {
        this.x = x
    }

    destruct() {
        // aufraeumen
    }

    Compute(int y) {
        return this.x + y
    }
}

var f = func (x) on obj => { return x + 1 }

if (a is in mm) {
    var ok = true
}

if (a is of int) {
    // Basistyp-Check
}

if (!(a is of float)) {
    // prefix '!' negates a parenthesised expression
}

if (foo is from objList) {
    // direkter Owner
}

if (foo is under globalList) {
    // transitiver Owner
}

try {
    throw new InvalidUnitException("geht nicht")
} catch (InvalidUnitException e) {
    e.resume(0)
} catch (e) {
    // catch-all
} finally {
    cleanup()
}

{
    riskyStepOne()

    catch (e) {
        log(e.message)
    }

    riskyStepTwo()
    riskyStepThree()
}

var neg = !flag
var inv = ~mask
var m = -5

var withContinuation = 1 _
    + 2 + 3

var _ = 42
""";

Console.WriteLine("=== Lexer-Test ===");
var lexer = new Lexer(sample);
var tokens = lexer.Tokenize();
foreach (var token in tokens)
{
    if (token.Type == TokenType.Eof) break;
    Console.WriteLine(token);
}

// Token.Length = length in the source text (editor highlighting): quotation marks, escapes, `$"..."` and char literals count as well.
{
    string src = "var a = \"x\\ny\" + $\"v{1}\" + 'c' + 42mm";
    var lengths = new Lexer(src).Tokenize().Where(t => t.Type is TokenType.StringLiteral or TokenType.InterpolatedStringLiteral or TokenType.CharLiteral or TokenType.IntLiteral)
        .Select(t => src.Substring(t.Column - 1, t.Length)).ToList();
    var expectedTexts = new List<string> { "\"x\\ny\"", "$\"v{1}\"", "'c'", "42mm" };
    bool ok = lengths.SequenceEqual(expectedTexts);
    Console.WriteLine(ok ? "OK: Token.Length deckt String-/Char-/Zahl-Literale vollstaendig ab"
        : $"FEHLER: Token.Length\n  erwartet: {string.Join(" | ", expectedTexts)}\n  erhalten: {string.Join(" | ", lengths)}");
}

Console.WriteLine();
Console.WriteLine("=== Parser-Test ===");
try
{
    var program = Parser.Parse(sample);
    Console.WriteLine($"OK - {program.Count} Top-Level-Statements geparst:");
    foreach (var stmt in program)
        Console.WriteLine($"  {stmt.GetType().Name}");
}
catch (ParseException ex)
{
    Console.WriteLine($"PARSE-FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Resolver-Test (gültiges Programm) ===");

string resolverSampleValid = """
var globalCounter = 0

class Counter {
    int value

    construct(int start) {
        this.value = start
    }

    Increment(int by) {
        var doubled = by * 2
        this.value = this.value + doubled
        return this.value
    }
}

{
    var outer = 1
    {
        var inner = outer + 1
        globalCounter = globalCounter + inner
    }
}

var makeAdder = func (n) => {
    return n + globalCounter
}
""";

try
{
    var program = Parser.Parse(resolverSampleValid);
    var result = Resolver.Resolve(program);
    Console.WriteLine($"OK - {result.GlobalSlotCount} globale Slots, {result.Classes.Count} Klasse(n).");
    foreach (var (exprNode, resolved) in result.References)
    {
        if (exprNode is IdentifierExpr id)
            Console.WriteLine($"  '{id.Name}' (Zeile {id.Line}) -> {resolved}");
    }
}
catch (ResolverException ex)
{
    Console.WriteLine($"UNERWARTETER RESOLVER-FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Resolver-Test (Lambda erfasst Block-Locals als Kopie; ein Name aus einem anderen Block bleibt unbekannt) ===");

string resolverSampleCapture = """
{
    var blockOnlyLocal = 42
    var lam = func () => {
        return blockOnlyLocal
    }
}
""";

try
{
    Resolver.Resolve(Parser.Parse(resolverSampleCapture));
    Console.WriteLine("OK - die Lambda erfasst 'blockOnlyLocal' (Capture).");
}
catch (ResolverException ex)
{
    Console.WriteLine($"FEHLER: unerwarteter Resolver-Fehler: {ex.Message}");
}

string resolverSampleInvalid = """
{
    var blockOnlyLocal = 42
}
{
    var lam = func () => {
        return blockOnlyLocal
    }
}
""";

try
{
    Resolver.Resolve(Parser.Parse(resolverSampleInvalid));
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen (Name aus einem anderen Block)");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Unit-System-Test ===");

var mm = Unit.Parse("mm");
var km = Unit.Parse("km");
var s = Unit.Parse("s");
var min = Unit.Parse("min");
var apples = Unit.Parse("apples");

Console.WriteLine($"mm kompatibel zu km? {mm.IsCompatibleWith(km)}");
Console.WriteLine($"1km in mm: {Value.MakeInt(1, km).CoerceUnit(mm)}");
Console.WriteLine($"5mm + 1km (nach Umrechnung auf mm): " +
    $"{Value.Add(Value.MakeInt(5, mm), Value.MakeInt(1, km).CoerceUnit(mm))}");

Console.WriteLine($"90min in s: {Value.MakeInt(90, min).CoerceUnit(s)}");
Console.WriteLine($"mm * mm Einheit: {Unit.Multiply(mm, mm)}");
Console.WriteLine($"m / s Einheit: {Unit.Divide(Unit.Parse("m"), s)}");
Console.WriteLine($"m / s^2 Einheit: {Unit.Divide(Unit.Parse("m"), Unit.Multiply(s, s))}");
Console.WriteLine($"apples kompatibel zu mm? {apples.IsCompatibleWith(mm)}");

try
{
    _ = Value.Add(Value.MakeInt(1, mm), Value.MakeInt(1, apples));
    Console.WriteLine("FEHLER: hätte UnitMismatchException werfen müssen");
}
catch (UnitMismatchException ex)
{
    Console.WriteLine($"Erwarteter Fehler bei mm + apples: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Runtime/Ownership-Test ===");

var dummyClass = "Dummy";
var globalScope = new Scope(null, isGlobal: true);
var funcScope = new Scope(globalScope);
var innerScope = new Scope(funcScope);
var log = new LoggingDestructRunner();

var objA = new ObjectInstance(dummyClass, innerScope);
Console.WriteLine($"a.Owner == innerScope? {ReferenceEquals(objA.Owner, innerScope)}");

objA.TakeUpwards();
Console.WriteLine($"nach TakeUpwards: a.Owner == funcScope? {ReferenceEquals(objA.Owner, funcScope)}");

objA.TakeGlobal(globalScope);
Console.WriteLine($"nach TakeGlobal: a.Owner == globalScope? {ReferenceEquals(objA.Owner, globalScope)}");

var objB = new ObjectInstance(dummyClass, innerScope);
objB.TakeTo(objA, log);
Console.WriteLine($"nach TakeTo: b.Owner == a? {ReferenceEquals(objB.Owner, objA)}");
Console.WriteLine($"b is from a? {objB.IsOwnedBy(objA)}");
Console.WriteLine($"b is under globalScope? {objB.IsTransitivelyOwnedBy(globalScope)}");

try
{
    objA.TakeTo(objB, log); // a belongs globally, b belongs to a -> a->b would be a cycle
    Console.WriteLine("FEHLER: Zyklus wurde nicht erkannt!");
}
catch (OwnershipException ex)
{
    Console.WriteLine($"Erwarteter Zyklus-Fehler: {ex.Message}");
}

var objC = new ObjectInstance(dummyClass, innerScope);
Console.WriteLine($"Vor Release: innerScope besitzt {innerScope.OwnedObjects.Count} Objekt(e)");
innerScope.Release(log);
Console.WriteLine($"Nach Release: innerScope besitzt {innerScope.OwnedObjects.Count} Objekt(e), c zerstört? {objC.IsDestroyed}");

Console.WriteLine("Race-Test (TakeTo während laufender Zerstörung des Ziels):");
var objD = new ObjectInstance(dummyClass, globalScope);
var raceRunner = new RaceDemoRunner(objA, objD, log);
objA.Destroy(raceRunner);
Console.WriteLine($"  d zerstört (obwohl TakeTo mitten in a's Destroy() aufgerufen wurde)? {objD.IsDestroyed}");


Console.WriteLine();
Console.WriteLine("=== Bytecode-Test (Compiler + VM) ===");

string bytecodeSample = """
var globalTotal = 0

for (var i = 1; i <= 5; i = i + 1) {
    globalTotal = globalTotal + i
}
print(globalTotal)

float b = 2.5km
int a = 500m
var z = b + a:!
print(z)

var x = 10
var y = 3
print(x % y)

if (x > y && y > 0) {
    print(1)
} else {
    print(0)
}

var count = 0
while (count < 3) {
    print(count)
    count = count + 1
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var bcProgram = Parser.Parse(bytecodeSample);
    var bcResolveResult = Resolver.Resolve(bcProgram, natives.Names);
    var compiled = Compiler.Compile(bcProgram, bcResolveResult, natives);

    Console.WriteLine($"Kompiliert: {compiled.TopLevel.Code.Count} Bytes Code, {compiled.TopLevel.Constants.Count} Konstanten, {compiled.TopLevel.Units.Count} Einheiten.");
    Console.WriteLine("Ausgabe:");

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Funktions-/Call-Frames (Lambdas, return) ===");

string callFrameSample = """
var square = func (x) => { return x * x }
var addOne = func (x) => { return x + 1 }
var compute = func (x) => { return addOne(square(x)) }
print(compute(5))

var factorial = func (n) => {
    var result = 1
    var i = 2
    while (i <= n) {
        result = result * i
        i = i + 1
    }
    return result
}
print(factorial(5))

var noReturn = func () => { var x = 1 }
print(noReturn())
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(callFrameSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 26 / 120 / undefined):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Typsystem-Test: Bitbreiten / Arrays / Pointer+unsafe / extern (gültig) ===");

string typeSystemSample = """
extern int GetTickCount()
extern PlaySound(string path)

int[8] narrow = 300
int[64] wide = 9999999999

int arr[10]
int matrix[][]
var dyn = new int[5]

unsafe {
    var x = 42
    int[64]* p = &x
    var y = *p
}
""";

try
{
    var program = Parser.Parse(typeSystemSample);
    var result = Resolver.Resolve(program);
    Console.WriteLine($"OK - {result.GlobalSlotCount} globale Slots geparst und aufgelöst.");
}
catch (Exception ex) when (ex is ParseException or ResolverException)
{
    Console.WriteLine($"UNERWARTETER FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Typsystem-Test: Dereferenzierung außerhalb 'unsafe' (muss fehlschlagen) ===");
try
{
    var program = Parser.Parse("var x = 5\nvar y = *x");
    Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Typsystem-Test: ungültige Bitbreite (muss fehlschlagen) ===");
try
{
    var program = Parser.Parse("int[17] x = 1");
    Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Value.TruncateTo-Test ===");
var wideVal = Value.MakeInt(300);
Console.WriteLine($"300 (int64) -> TruncateTo(W8) = {wideVal.TruncateTo(NumericWidth.W8).AsInt()} (erwartet: 44, da 300 mod 256 - 256 = 44)");
var wideFloat = Value.MakeFloat(3.14159265358979);
Console.WriteLine($"3.14159265358979 (double) -> TruncateTo(W32) = {wideFloat.TruncateTo(NumericWidth.W32).AsFloat()}");

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Klassen/Objekte (new, this, base, virtuelle Methoden) ===");

string classSample = """
class Animal {
    string name

    construct(string name) {
        this.name = name
    }

    Speak() {
        print(this.name)
        return 0
    }
}

class Dog : Animal {
    construct(string name) : base(name) {
    }

    Speak() {
        base.Speak()
        print(42)
        return 1
    }
}

var a = new Animal("Generic")
a.Speak()

var d = new Dog("Rex")
d.Speak()
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(classSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine($"{compiled.Classes.Count} Klasse(n) kompiliert: {string.Join(", ", compiled.Classes.Keys)}");
    Console.WriteLine("Ausgabe (erwartet: Generic / Rex / 42):");

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Ownership-Politik bei direkter Feldzuweisung (SPEC 2.1) ===");

string ownershipSample = """
class Item {
    string tag
    construct(string tag) { this.tag = tag }
}

class Box {
    Item content
    construct() { }
}

var box = new Box()
box.content = new Item("hello")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(ownershipSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();

    var boxInstance = (ObjectInstance)vmGlobalScope.GetSlot(0).AsObjectRef();
    var itemInstance = (ObjectInstance)boxInstance.Fields["content"].AsObjectRef();
    Console.WriteLine($"Owner von 'item' ist die 'box'-Instanz (nicht der globale Scope)? " +
        $"{ReferenceEquals(itemInstance.Owner, boxInstance)}");
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Destruktor-Ausführung bei Kaskadenlöschung (SPEC 2.3) ===");

string destructorSample = """
class Cleanup {
    string label

    construct(string label) {
        this.label = label
    }

    destruct() {
        print(this.label)
    }
}

{
    var r = new Cleanup("cleanup-ran")
    print(1)
}
print(2)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(destructorSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 1 / cleanup-ran / 2):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Pointer/unsafe (echtes Aliasing über Scope-Slots und Objekt-Felder) ===");

string unsafeSample = """
class Box {
    int value

    construct(int value) {
        this.value = value
    }
}

unsafe {
    var x = 10
    var p = &x
    print(*p)
    *p = 99
    print(x)

    var b = new Box(7)
    var fp = &b.value
    print(*fp)
    *fp = 42
    print(b.value)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(unsafeSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 10 / 99 / 7 / 42):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: '&'/'*' außerhalb 'unsafe' (muss fehlschlagen) ===");
try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse("var x = 5\nvar p = &x");
    Resolver.Resolve(program, natives.Names);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Arrays (new, Index-Zugriff, length, Deklarator-Sugar) ===");

string arraySample = """
var arr = new int[5]
arr[0] = 10
arr[1] = 20
print(arr[0] + arr[1])
print(arr.length)

int fixedArr[3]
fixedArr[0] = 99
print(fixedArr[0])
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(arraySample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 30 / 5 / 99):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Exceptions (throw/try/catch/finally, typisiertes Matching) ===");

string exceptionSample = """
class MyError {
    string message
    construct(string message) { this.message = message }
}

var result = 0
try {
    throw new MyError("boom")
} catch (MyError e) {
    print(e.message)
    result = 1
} finally {
    print(999)
}
print(result)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(exceptionSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: boom / 999 / 1):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: finally läuft auch bei propagierender Exception (kein Match im inneren try) ===");

string propagateSample = """
class ErrB {
    string message
    construct(string message) { this.message = message }
}

try {
    try {
        throw new ErrB("inner")
    } finally {
        print(1)
    }
} catch (ErrB e) {
    print(e.message)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(propagateSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 1 / inner):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: unbehandelte Exception (muss abbrechen) ===");

string uncaughtSample = """
class Oops {
    string message
    construct(string message) { this.message = message }
}
throw new Oops("nobody catches me")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(uncaughtSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    // No throw any more - Run() returns normally, VM.UnhandledException
    // carries the uncaught exception (see the VM.UnhandledException documentation).
    if (vm.UnhandledException == null)
        Console.WriteLine("FEHLER: vm.UnhandledException hätte gesetzt sein müssen");
    else
        Console.WriteLine($"Erwarteter Abbruch: {new UncaughtScriptException(vm.UnhandledException).Message}");
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Interfaces + List (Prelude) + foreach ===");

string listSample = """
var list = new List()
list.Add(10)
list.Add(20)
list.Add(30)

var sum = 0
foreach (x in list) {
    sum = sum + x
}
print(sum)
print(list[1])
list[1] = 99
print(list[1])
print(list[1])
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, listSample));
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 60 / 20 / 99 / 99):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Interface nicht erfüllt (muss fehlschlagen) ===");

string brokenInterfaceSample = """
interface IFoo {
    Bar()
}
class Broken : IFoo {
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(brokenInterfaceSample);
    Resolver.Resolve(program, natives.Names);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: is in / is of / is from / is under ===");

string isOperatorsSample = """
int x = 5mm
print(x is in mm)
print(x is in kg)
print(x is of int)
print(x is of float)

class Animal {
    construct() { }
}

class Dog : Animal {
    construct() : base() { }
}

class Cat {
    construct() { }
}

class Box {
    class content
    construct() { }
}

var d = new Dog()
print(d is of Dog)
print(d is of Animal)
print(d is of Cat)

var box = new Box()
box.content = new Dog()
print(box.content is from box)
print(box.content is under box)
print(d is from box)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(isOperatorsSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: True/False/True/False / True/True/False / True/True/False):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: resume (fortsetzbare Exceptions) ===");

string resumeSample = """
class MyError {
    message
    construct(msg) { this.message = msg }
}

var doWork = func () => {
    print("vor throw")
    var result = throw new MyError("etwas ging schief")
    print("nach throw, result = " + result)
    return result
}

var r = 0
try {
    r = doWork()
} catch (MyError e) {
    print("gefangen: " + e.message)
    e.resume(42)
    print("SOLLTE NIE LAUFEN")
}
print("r = " + r)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(resumeSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: vor throw / gefangen: etwas ging schief / nach throw, result = 42 / r = 42):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: nie fortgesetzte Exception (normaler catch-Durchlauf) ===");

string noResumeSample = """
class MyError {
    message
    construct(msg) { this.message = msg }
}

var doWork = func () => {
    var result = throw new MyError("wird nicht fortgesetzt")
    print("SOLLTE NIE LAUFEN")
    return result
}

try {
    doWork()
} catch (MyError e) {
    print("gefangen ohne resume: " + e.message)
}
print("weiter nach try/catch")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(noResumeSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: gefangen ohne resume: wird nicht fortgesetzt / weiter nach try/catch):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: extern-Linking (echte WinAPI-Aufrufe per P/Invoke) ===");

string externSample = """
extern int ShowMessageBox(string text, string caption)
extern int GetTickCount()
extern int QueryPerformanceCounter(int* counter)

ShowMessageBox("Hallo von fire!", "extern-Test")
print(GetTickCount())

unsafe {
    var counter = 0
    QueryPerformanceCounter(&counter)
    print(counter)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var externs = ExternRegistry.CreateWinApiDemo();
    var program = Parser.Parse(externSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: eine MessageBox erscheint, danach zwei Zahlen != 0 - GetTickCount/QueryPerformanceCounter liefern Laufzeitwerte, keine festen):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes, externs);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: extern deklariert, aber NICHT verlinkt (muss erst beim Aufruf fehlschlagen) ===");

string unlinkedExternSample = """
extern int notLinked()
print("vor dem Aufruf - kompilieren/starten funktioniert trotzdem")
notLinked()
print("SOLLTE NIE LAUFEN")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(unlinkedExternSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: Text, dann eine klare Fehlermeldung, kein Absturz mit Stacktrace):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes); // deliberately WITHOUT ExternRegistry
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException or InvalidOperationException)
{
    Console.WriteLine($"FEHLER (erwartet): {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Array-Literale (auch verschachtelt) ===");

string arrayLiteralSample = """
var nums = [10, 20, 30]
print(nums.length)
print(nums[0])
print(nums[2])

var grid = [[1, 2], [3, 4, 5]]
print(grid.length)
print(grid[0].length)
print(grid[1].length)
print(grid[1][2])
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(arrayLiteralSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 3 / 10 / 30 / 2 / 2 / 3 / 5):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: mehrdimensionale Array-Allokation (new + Deklarator-Sugar) ===");

string multiDimSample = """
var m = new int[2][3]
print(m.length)
print(m[0].length)
m[1][2] = 42
print(m[1][2])
print(m[0][0])

int matrix[2][2]
matrix[0][0] = 1
matrix[0][1] = 2
matrix[1][0] = 3
matrix[1][1] = 4
print(matrix[1][1])
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(multiDimSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 2 / 3 / 42 / undefined / 4):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Array-Bounds-Checking als fangbare Skript-Exception ===");

string boundsCheckSample = """
var arr = [1, 2, 3]

try {
    print(arr[10])
    print("SOLLTE NIE LAUFEN")
} catch (IndexOutOfBoundsException e) {
    print("gefangen: " + e.message)
    print(e.index)
    print(e.length)
}

print("weiter nach try/catch")

var list = new List()
list.Add(1)
try {
    // List physically allocates 8 slots internally (see Prelude, doubles
    // only when needed) - only an index beyond THIS physical capacity
    // (not just beyond the .Add() count) triggers the bounds check.
    list[20] = 99
} catch (IndexOutOfBoundsException e) {
    print("auch ueber List[] gefangen: " + e.index)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, boundsCheckSample));
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: gefangen: .../ 10 / 3 / weiter nach try/catch / auch ueber List[] gefangen: 20):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: #include (textuelle Vorverarbeitung) ===");

string includeMainSample = """
#include "shapes_include.script"

var c = new Circle(2.0)
print(c.Area())
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.ParseMultiple(Preprocessed(GetTestDataDir(), fire.Standard.Prelude.Source, includeMainSample));
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 12):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException or PreprocessorException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: #extern \"libName\" (dynamisches Linking ohne Host-Registrierung) ===");

string dynamicExternSample = """
#extern "kernel32.dll"
extern int GetTickCount()

#extern "user32.dll"
extern int MessageBoxW(int hWnd, string text, string caption, int type)

print(GetTickCount())
MessageBoxW(0, "Hallo von fire!", "#extern-Test", 0)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(dynamicExternSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: eine Zahl != 0 (Tickcount), dann eine MessageBox - nur unter Windows lauffähig, da kernel32/user32 nur dort existieren):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    // Deliberately WITHOUT ExternRegistry - both functions are to be linked dynamically purely via the
    // '#extern' directives (VM.ResolveDynamicExtern).
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes, externSignatures: compiled.ExternSignatures);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"Laufzeitfehler beim dynamischen Linking (erwartbar außerhalb von Windows): {ex.Message}");
}

// Returns the TestData directory relative to THIS source file, independent
// of the current working directory at execution (dotnet run can be started from
// different places).
static string GetTestDataDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
    Path.Combine(Path.GetDirectoryName(here)!, "TestData");

// Test helper function: runs each of the `sources` through the real preprocessor
// (which recognises/removes #include/#using as a normal caller would
// do, see RuntimeSession.Build for the same pattern "for real") and
// returns the resulting ProcessedSource objects, which Parser.
// ParseMultiple now expects directly. `basePath` is only relevant for #include
// path resolution, of no significance for most tests.
static IReadOnlyList<ProcessedSource> Preprocessed(string basePath, params string[] sources)
{
    var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    return sources.Select(s => Preprocessor.Process(s, basePath, alreadyIncluded)).ToList();
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: readonly Variablen (Konstanten) ===");

string readonlyVarSample = """
readonly var PI = 3

print(PI)
print(PI * 2)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(readonlyVarSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 3 / 6):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: readonly Zuweisung nach Deklaration (muss fehlschlagen) ===");

string readonlyReassignSample = """
readonly var PI = 3
PI = 4
""";

try
{
    var program = Parser.Parse(readonlyReassignSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: readonly Felder (gültig: nur im eigenen Konstruktor) ===");

string readonlyFieldValidSample = """
class Circle {
    readonly float radius

    construct(float radius) {
        this.radius = radius
    }
}

var c = new Circle(5.0)
print(c.radius)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(readonlyFieldValidSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 5):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: readonly Feld außerhalb des Konstruktors zugewiesen (muss fehlschlagen) ===");

string readonlyFieldInvalidSample = """
class Circle {
    readonly float radius

    construct(float radius) {
        this.radius = radius
    }

    Scale() {
        this.radius = this.radius * 2
    }
}
""";

try
{
    var program = Parser.Parse(readonlyFieldInvalidSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: enum (Auto-Increment und explizite Werte) ===");

string enumSample = """
enum Color {
    Red,
    Green,
    Blue
}

enum Status {
    Active = 10,
    Inactive,
    Paused = 20,
    Done
}

print(Color.Red)
print(Color.Green)
print(Color.Blue)

print(Status.Active)
print(Status.Inactive)
print(Status.Paused)
print(Status.Done)

var c = Color.Green
if (c is of int) {
    print("Color.Green ist (wie erwartet) ein int")
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(enumSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 0/1/2/10/11/20/21/\"Color.Green ist (wie erwartet) ein int\"):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Properties (C#-artig, get/set) ===");

string propertySample = """
class Circle {
    float radius

    construct(float radius) {
        this.radius = radius
    }

    float Diameter {
        get { return this.radius * 2 }
        set { this.radius = value / 2 }
    }

    float Area {
        get { return this.radius * this.radius * 3 }
    }
}

var c = new Circle(5.0)
print(c.Diameter)
c.Diameter = 20.0
print(c.radius)
print(c.Area)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(propertySample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 10 / 10 / 300):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: get-only Property zuweisen (muss zur Laufzeit fehlschlagen) ===");

string getOnlyPropertySample = """
class Circle {
    float radius
    construct(float radius) { this.radius = radius }
    float Area {
        get { return this.radius * this.radius * 3 }
    }
}

var c = new Circle(5.0)
c.Area = 100.0
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(getOnlyPropertySample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    Console.WriteLine("FEHLER: hätte eine InvalidOperationException werfen müssen, ist aber durchgelaufen.");
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER beim Kompilieren: {ex.Message}");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"Erwarteter Laufzeitfehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Einheiten bei Multiplikation/Division mit unitless (Bugfix) ===");

string unitPropertySample = """
class Circle {
    float radius

    construct(float radius) {
        this.radius = radius
    }

    float Diameter {
        get { return this.radius * 2 }
        set { this.radius = value / 2 }
    }

    float Area {
        get { return this.radius * this.radius * 3 }
    }
}

var c = new Circle(5.0)
print(c.Diameter)
c.Diameter = 20.0mm
print(c.radius)
print(c.Area)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(unitPropertySample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 10 / 10mm / 300mm^2):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Methodenüberladung (unterschiedliche Argumentzahl) ===");

string overloadSample = """
class Calculator {
    int Add(int a, int b) {
        return a + b
    }

    int Add(int a, int b, int c) {
        return a + b + c
    }
}

var calc = new Calculator()
print(calc.Add(1, 2))
print(calc.Add(1, 2, 3))
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(overloadSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 3 / 6):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: doppelte Methode mit GLEICHER Argumentzahl (muss fehlschlagen) ===");

string duplicateOverloadSample = """
class Foo {
    int Bar(int a) { return a }
    int Bar(int b) { return b * 2 }
}
""";

try
{
    var program = Parser.Parse(duplicateOverloadSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: with-Statement (BASIC-artig) ===");

string withSample = """
class Point {
    int x
    int y
}

var p = new Point()
with p {
    .x = 10
    .y = 20
}
print(p.x)
print(p.y)

with p {
    print(.x + .y)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(withSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 10 / 20 / 30):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Extension-Klassen (class extends) ===");

string extensionSample = """
class Animal {
    string name

    construct(string name) {
        this.name = name
    }
}

class extends Animal {
    int age

    string Describe() {
        return this.name
    }
}

var a = new Animal("Rex")
a.age = 5
print(a.Describe())
print(a.age)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(extensionSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: Rex / 5):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Extension-Klasse für Prelude-Klasse 'List' (nur mit Prelude) ===");

string extendListSample = """
class extends List {
    Peek() {
        return this[this.count - 1]
    }
}

var list = new List()
list.Add(1)
list.Add(2)
list.Add(3)
print(list.Peek())
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, extendListSample));
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 3):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Extension-Klasse für unbekannte Klasse (muss fehlschlagen) ===");

string extendUnknownSample = """
class extends DoesNotExist {
    var x
}
""";

try
{
    var program = Parser.Parse(extendUnknownSample);
    Console.WriteLine("FEHLER: hätte eine ParseException werfen müssen, ist aber durchgelaufen.");
}
catch (ParseException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Konstruktor-Überladung ===");

string ctorOverloadSample = """
class Point {
    int x
    int y

    construct() {
        this.x = 0
        this.y = 0
    }

    construct(int x, int y) {
        this.x = x
        this.y = y
    }
}

var p1 = new Point()
var p2 = new Point(3, 4)
print(p1.x)
print(p2.x)
print(p2.y)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(ctorOverloadSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 0 / 3 / 4):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: optionale Parameter (Methode, Lambda, Standardwert mit und ohne Typ) ===");

string optionalParamsSample = """
class Greeter {
    string Greet(string name, string greeting = "Hallo") {
        return greeting + ", " + name
    }
}

var g = new Greeter()
print(g.Greet("Welt"))
print(g.Greet("Welt", "Servus"))

var f = func (x, y = 10) => { return x + y }
print(f(5))
print(f(5, 20))

var h = func (int x = 42) => { return x }
print(h())
print(h(7))
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(optionalParamsSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: Hallo, Welt / Servus, Welt / 15 / 25 / 42 / 7):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Pflichtparameter NACH optionalem (muss fehlschlagen) ===");

string badOptionalSample = """
class Bad {
    int Foo(int a = 1, int b) {
        return a + b
    }
}
""";

try
{
    var program = Parser.Parse(badOptionalSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: switch mit Operatoren und case default ===");

string switchSample = """
class Runner {
    Classify(int x) {
        switch (x) {
            case <= 1:
                print("klein")
                break
            case 2:
                print("zwei")
                break
            case default:
                print("gross")
        }
    }
}

var r = new Runner()
r.Classify(5)
r.Classify(1)
r.Classify(2)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(switchSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: gross / klein / zwei):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: switch ohne default, kein Treffer -> tut nichts ===");

string switchNoDefaultSample = """
var x = 99
switch (x) {
    case 1:
        print("eins")
        break
    case 2:
        print("zwei")
        break
}
print("danach")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(switchNoDefaultSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: nur 'danach'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: 'break' außerhalb einer Schleife (muss fehlschlagen) ===");

string breakOutsideSample = """
    print("vorher")
    break
    print("nachher")
    """;

try
{
    var program = Parser.Parse(breakOutsideSample);
    var natives = NativeRegistry.CreateDefault();
    Resolver.Resolve(program, natives.Names);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: generische Klasse, gültige Instanziierung (Vererbung erfüllt 'is of') ===");

string genericsOkSample = """
class Animal {
}

class Dog : Animal {
}

class Container<T> where T is of Animal {
    T item
}

var c = new Container<Dog>()
print("ok")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(genericsOkSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: ok):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: generische Klasse, ODER-Constraint (',') mit zwei Alternativen ===");

string genericsOrSample = """
class Animal {
}

class Dog : Animal {
}

class Box<T> where T is of float, is of Dog {
}

var b1 = new Box<float>()
var b2 = new Box<Dog>()
print("beide ok")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(genericsOrSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: beide ok):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: generische Klasse, UND-Constraint (':') ===");

string genericsAndSample = """
class Precise<T> where T is of float : is of float {
}

var p = new Precise<float>()
print("und ok")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(genericsAndSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: und ok):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: generische Klasse, Constraint verletzt (muss fehlschlagen) ===");

string genericsViolationSample = """
class Animal {
}

class Cat {
}

class Container3<T> where T is of Animal {
}

var bad = new Container3<Cat>()
""";

try
{
    var program = Parser.Parse(genericsViolationSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: generische Klasse, falsche Typ-Argument-Anzahl (muss fehlschlagen) ===");

string genericsArityMismatchSample = """
class Animal {
}

class Dog : Animal {
}

class Container4<T> where T is of Animal {
}

var bad = new Container4<Dog, Animal>()
""";

try
{
    var program = Parser.Parse(genericsArityMismatchSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: nicht-generische Klasse mit Typ-Argumenten (muss fehlschlagen) ===");

string genericsNonGenericSample = """
class Plain {
}

var bad = new Plain<int>()
""";

try
{
    var program = Parser.Parse(genericsNonGenericSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break/continue in while ===");

string whileBreakContinueSample = """
var i = 0
while (i < 10) {
    i = i + 1
    if (i == 3) {
        continue
    }
    if (i == 6) {
        break
    }
    print(i)
}
print("done")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(whileBreakContinueSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 1 / 2 / 4 / 5 / done):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: continue in for (Increment muss trotzdem laufen) ===");

string forContinueSample = """
for (var i = 0; i < 5; i = i + 1) {
    if (i == 2) {
        continue
    }
    print(i)
}
print("for done")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(forContinueSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 0 / 1 / 3 / 4 / for done):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break in foreach (mit Prelude-List) ===");

string foreachBreakSample = """
var list = new List()
list.Add(10)
list.Add(20)
list.Add(30)
list.Add(40)

foreach (x in list) {
    if (x == 30) {
        break
    }
    print(x)
}
print("foreach done")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, foreachBreakSample));
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 10 / 20 / foreach done):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break in verschachtelten Schleifen (nur die innerste) ===");

string nestedBreakSample = """
for (var i = 0; i < 3; i = i + 1) {
    for (var j = 0; j < 3; j = j + 1) {
        if (j == 1) {
            break
        }
        print(i * 10 + j)
    }
}
print("nested done")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(nestedBreakSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 0 / 10 / 20 / nested done):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break durch verschachtelte if-Blöcke hindurch (Scope-Unwind) ===");

string nestedIfBreakSample = """
var k = 0
while (k < 100) {
    k = k + 1
    if (k > 0) {
        if (k == 3) {
            break
        }
    }
    print(k)
}
print("if-nested done")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(nestedIfBreakSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 1 / 2 / if-nested done):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break außerhalb einer Schleife (muss fehlschlagen) ===");

string breakOutsideLoopSample = """
print("before")
break
""";

try
{
    var program = Parser.Parse(breakOutsideLoopSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: continue außerhalb einer Schleife (muss fehlschlagen) ===");

string continueOutsideLoopSample = """
continue
""";

try
{
    var program = Parser.Parse(continueOutsideLoopSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break in Lambda innerhalb einer Schleife (muss fehlschlagen) ===");

string breakInLambdaSample = """
var n = 0
while (n < 5) {
    n = n + 1
    var f = func() => {
        break
    }
}
""";

try
{
    var program = Parser.Parse(breakInLambdaSample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: break in finally innerhalb einer Schleife (muss fehlschlagen) ===");

string breakInTrySample = """
var m = 0
while (m < 5) {
    m = m + 1
    try {
        m = 7
    }
    finally {
        break
    }
}
""";

try
{
    var program = Parser.Parse(breakInTrySample);
    var resolveResult = Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte eine ResolverException werfen müssen, ist aber durchgelaufen.");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Auto-Properties ===");

string autoPropertySample = """
class Person {
    string Name { get; set; }
    int Age { get; }

    construct(string name, int age) {
        this.Name = name
        this._AutoAge = age
    }
}

var p = new Person("Alice", 30)
print(p.Name)
print(p.Age)
p.Name = "Bob"
print(p.Name)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(autoPropertySample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: Alice / 30 / Bob):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Zuweisung an get-only Auto-Property von außen (muss zur Laufzeit fehlschlagen) ===");

string autoPropertyGetOnlySample = """
class Person2 {
    int Age { get; }

    construct(int age) {
        this._AutoAge = age
    }
}

var p = new Person2(30)
p.Age = 99
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(autoPropertyGetOnlySample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    Console.WriteLine("FEHLER: hätte zur Laufzeit fehlschlagen müssen, ist aber durchgelaufen.");
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException or InvalidOperationException)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Lambda-Typ mit Signatur als Parameter (Kurzform '=> ausdruck') ===");

string lambdaSigParamSample = """
class Runner {
    Execute(lambda<int> callback, int x) {
        return callback(x)
    }
}

var r = new Runner()
var doubleIt = func (n) => n * 2
print(r.Execute(doubleIt, 21))
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(lambdaSigParamSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 42):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Lambda-Signatur-Verletzung als Parameter (muss zur Laufzeit fehlschlagen) ===");

string lambdaSigMismatchParamSample = """
class Runner2 {
    Execute(lambda<int> callback) {
        return callback(1, 2)
    }
}

var r = new Runner2()
var twoParams = func (a, b) => a + b
r.Execute(twoParams)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(lambdaSigMismatchParamSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    Console.WriteLine("FEHLER: hätte zur Laufzeit fehlschlagen müssen, ist aber durchgelaufen.");
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException or InvalidOperationException)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: 'RückgabeTyp lambda<...>' bei var-Deklaration (Langform '=> { return ... }') ===");

string lambdaSigVarSample = """
int lambda<int, int> adder = func (a, b) => { return a + b }
print(adder(3, 4))
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(lambdaSigVarSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 7):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: Lambda-Signatur-Verletzung bei var-Deklaration (muss zur Laufzeit fehlschlagen) ===");

string lambdaSigMismatchVarSample = """
lambda<int> f = func (a, b) => a + b
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(lambdaSigMismatchVarSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    Console.WriteLine("FEHLER: hätte zur Laufzeit fehlschlagen müssen, ist aber durchgelaufen.");
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException or InvalidOperationException)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Bytecode-Test: 'lambda' ohne '<>' bedeutet 0 Parameter ===");

string lambdaZeroParamSample = """
lambda greet = func () => { print("hi") }
greet()

lambda bad = func (x) => x
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(lambdaZeroParamSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: hi, danach Fehler wegen 'bad'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    Console.WriteLine("FEHLER: hätte zur Laufzeit fehlschlagen müssen, ist aber durchgelaufen.");
}
catch (Exception ex) when (ex is ParseException or ResolverException or NotSupportedException or InvalidOperationException)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

// =====================================================================
// Multithreading-Architektur (docs/THREADING_DESIGN.md): Threads + Locking
// + taking copy + sync - everything tested directly via the C# API, still without
// parser/language syntax for fire/taking/sync/process/leave/terminate.
// =====================================================================

Console.WriteLine();
Console.WriteLine("=== Multithreading: voller Ablauf (taking -> Fire-Thread -> sync, blockierend) ===");

string mtSetupSample = """
class Inventory {
    int gold
}

class Player {
    int health
    Inventory inventory

    construct() {
        this.health = 100
        this.inventory = new Inventory()
        this.inventory.gold = 50
    }
}

var player = new Player()
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtSetupSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes) { DestroyGlobalsAtEnd = false }; // the objects are afterwards still handed to threads by hand
    vm.Run();

    var player = (ObjectInstance)vmGlobalScope.GetSlot(0).AsObjectRef();
    var inventory = (ObjectInstance)player.Fields["inventory"].AsObjectRef();
    Console.WriteLine($"Ausgangszustand: health={player.Fields["health"].AsInt()}, gold={inventory.Fields["gold"].AsInt()}");

    var childOwnerScope = new Scope(null, isGlobal: true);
    var copy = ObjectCopier.Take(player, childOwnerScope);
    Console.WriteLine(
        $"Nach taking: player.ThreadLock gesetzt = {player.ThreadLock != null}, " +
        $"copy.SyncOrigin == player = {ReferenceEquals(copy.SyncOrigin, player)}, " +
        $"copy ist unabhängige Instanz = {!ReferenceEquals(copy, player)}");

    var handle = FireRuntime.Fire(() =>
    {
        copy.Fields["health"] = Value.MakeInt(copy.Fields["health"].AsInt() - 10);
        var copyInventory = (ObjectInstance)copy.Fields["inventory"].AsObjectRef();
        copyInventory.Fields["gold"] = Value.MakeInt(copyInventory.Fields["gold"].AsInt() + 25);

        var result = SyncEngine.Sync(copy, blocking: true);
        if (result != SyncResult.Success)
            throw new Exception($"Sync fehlgeschlagen: {result}");
    });
    handle.Join();

    if (handle.Error != null)
        Console.WriteLine($"FEHLER im Fire-Thread: {handle.Error}");
    else
        Console.WriteLine(
            $"Nach sync (erwartet: health=90, gold=75): health={player.Fields["health"].AsInt()}, " +
            $"gold={inventory.Fields["gold"].AsInt()}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: zwei Threads, 'try sync' mit Retry (Last-Writer-Wins, kein Crash) ===");

string mtCounterSample = """
class Counter {
    int value

    construct() {
        this.value = 0
    }
}

var counter = new Counter()
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtCounterSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes) { DestroyGlobalsAtEnd = false };
    vm.Run();

    var counter = (ObjectInstance)vmGlobalScope.GetSlot(0).AsObjectRef();

    var scope1 = new Scope(null, isGlobal: true);
    var scope2 = new Scope(null, isGlobal: true);
    var copy1 = ObjectCopier.Take(counter, scope1);
    var copy2 = ObjectCopier.Take(counter, scope2);

    var h1 = FireRuntime.Fire(() =>
    {
        copy1.Fields["value"] = Value.MakeInt(111);
        SyncResult r;
        int attempts = 0;
        do
        {
            r = SyncEngine.SyncFlat(copy1, blocking: false);
            if (r == SyncResult.LockBusy) System.Threading.Thread.Yield();
            attempts++;
        }
        while (r == SyncResult.LockBusy && attempts < 10_000);
        if (r != SyncResult.Success) throw new Exception($"Thread1 sync fehlgeschlagen: {r}");
    });

    var h2 = FireRuntime.Fire(() =>
    {
        copy2.Fields["value"] = Value.MakeInt(222);
        SyncResult r;
        int attempts = 0;
        do
        {
            r = SyncEngine.SyncFlat(copy2, blocking: false);
            if (r == SyncResult.LockBusy) System.Threading.Thread.Yield();
            attempts++;
        }
        while (r == SyncResult.LockBusy && attempts < 10_000);
        if (r != SyncResult.Success) throw new Exception($"Thread2 sync fehlgeschlagen: {r}");
    });

    h1.Join();
    h2.Join();

    if (h1.Error != null) Console.WriteLine($"FEHLER Thread1: {h1.Error}");
    if (h2.Error != null) Console.WriteLine($"FEHLER Thread2: {h2.Error}");

    long finalValue = counter.Fields["value"].AsInt();
    Console.WriteLine($"Endwert nach beiden Syncs (erwartet 111 ODER 222, Last-Writer-Wins, kein Crash): {finalValue}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: sync nach Zerstoerung des Ziels -> TargetGone ===");

try
{
    var dummyClass2 = "SyncGoneDummy";
    var ownerScope = new Scope(null, isGlobal: true);
    var original = new ObjectInstance(dummyClass2, ownerScope);
    original.Fields["x"] = Value.MakeInt(1);

    var childScope2 = new Scope(null, isGlobal: true);
    var copy2 = ObjectCopier.Take(original, childScope2);

    original.Destroy(NullDestructRunner.Instance);

    var result = SyncEngine.Sync(copy2, blocking: true);
    Console.WriteLine($"Sync-Ergebnis nach Zerstoerung des Ziels (erwartet TargetGone): {result}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: 'taking' lehnt baumfremde Referenzen ab ===");

try
{
    var dummyClass3 = "TakingDummy";
    var scopeA = new Scope(null, isGlobal: true);
    var independent = new ObjectInstance(dummyClass3, scopeA);

    var root = new ObjectInstance(dummyClass3, scopeA);
    // Deliberately set WITHOUT reparenting - simulates a reference foreign to the
    // tree, as normal field assignment (which reparents per the ownership
    // policy) would not actually produce.
    root.Fields["escapesTree"] = Value.MakeClassRef(independent);

    var childScope3 = new Scope(null, isGlobal: true);
    try
    {
        ObjectCopier.Take(root, childScope3);
        Console.WriteLine("FEHLER: hätte eine TakingViolationException werfen müssen.");
    }
    catch (TakingViolationException ex)
    {
        Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: ECHTER Bytecode läuft in einer eigenen VM-Instanz im Fire-Thread ===");

string mtRealBytecodeSetup = """
class Player {
    int health

    construct() {
        this.health = 100
    }
}

var player = new Player()
""";

// The fire thread script does not know 'Player' (its own, separately compiled
// program) - that is unproblematic, since field access ('player.health')
// is always a pure runtime name lookup, and needs no compile-time check
// against a class definition. '__fireArg'/'__sync' are ordinary
// native functions (see the FireRuntime.FireVm documentation) - the bridge to
// the 'taking' copy and to SyncEngine, without any parser change.
string mtFireScript = """
var player = __fireArg()
player.health = player.health - 10
var result = __sync(player)
print(result)
""";

try
{
    var mainNatives = NativeRegistry.CreateDefault();
    var setupProgram = Parser.Parse(mtRealBytecodeSetup);
    var setupResolve = Resolver.Resolve(setupProgram, mainNatives.Names);
    var setupCompiled = Compiler.Compile(setupProgram, setupResolve, mainNatives);

    var mainGlobalScope = new Scope(null, isGlobal: true);
    var mainVm = new VM(setupCompiled.TopLevel, mainGlobalScope, mainNatives, setupCompiled.Classes) { DestroyGlobalsAtEnd = false };
    mainVm.Run();

    var player = (ObjectInstance)mainGlobalScope.GetSlot(0).AsObjectRef();
    Console.WriteLine($"Ausgangszustand: health={player.Fields["health"].AsInt()}");

    var childScope = new Scope(null, isGlobal: true);
    var copy = ObjectCopier.Take(player, childScope);

    var fireNatives = NativeRegistry.CreateDefault();
    fireNatives.Register("__fireArg", _ => Value.MakeClassRef(copy));
    fireNatives.Register("__sync", args =>
    {
        var obj = (ObjectInstance)args[0].AsObjectRef();
        var result = SyncEngine.Sync(obj, blocking: true);
        // The same mapping that the real language syntax will use later
        // (see docs/THREADING_DESIGN.md 4.1): true/false/undefined.
        return result switch
        {
            SyncResult.Success => Value.MakeBool(true),
            SyncResult.LockBusy => Value.MakeBool(false),
            _ => Value.MakeUndefined(),
        };
    });

    var fireProgram = Parser.Parse(mtFireScript);
    var fireResolve = Resolver.Resolve(fireProgram, fireNatives.Names);
    var fireCompiled = Compiler.Compile(fireProgram, fireResolve, fireNatives);

    var handle = FireRuntime.FireVm(fireCompiled.TopLevel, fireNatives);
    handle.Join();

    if (handle.Error != null)
        Console.WriteLine($"FEHLER im Fire-Thread: {handle.Error}");
    else
        Console.WriteLine($"Nach Fire-Thread mit echtem Bytecode (erwartet: health=90): health={player.Fields["health"].AsInt()}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: 'leave' laeuft durch finally, aber NIE durch catch ===");

string mtLeaveSample = """
try {
    print("before leave")
    __leave()
    print("NIE ERREICHT (nach leave im selben try)")
}
finally {
    print("finally ran")
}
print("NIE ERREICHT (nach dem try)")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    natives.Register("__leave", _ =>
    {
        VM.CurrentThreadVm?.RequestLeave();
        return Value.MakeUndefined();
    });

    var program = Parser.Parse(mtLeaveSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet NUR: before leave / finally ran):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    Console.WriteLine("(Run() sauber zurückgekehrt, keine Exception)");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: 'leave' wird NICHT von catch(e) gefangen ===");

string mtLeaveNotCaughtSample = """
try {
    __leave()
}
catch (e) {
    print("NIE ERREICHT (leave gefangen)")
}
print("NIE ERREICHT (nach dem try)")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    natives.Register("__leave", _ =>
    {
        VM.CurrentThreadVm?.RequestLeave();
        return Value.MakeUndefined();
    });

    var program = Parser.Parse(mtLeaveNotCaughtSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: GAR KEINE):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    Console.WriteLine("(Run() sauber zurückgekehrt, keine Exception, catch(e) wurde übersprungen)");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: 'terminate' stoppt MEHRERE unabhaengige Threads kooperativ ===");

string mtTerminateCallerSample = """
var i = 0
while (i < 2000000) {
    i = i + 1
    if (i == 5) {
        __terminate(42)
    }
}
print("A: NIE ERREICHT")
""";

string mtTerminateBystanderSample = """
var i = 0
while (i < 2000000) {
    i = i + 1
}
print("B: NIE ERREICHT")
""";

try
{
    VM.ResetTerminateForTests();

    var natives = NativeRegistry.CreateDefault();
    natives.Register("__terminate", args =>
    {
        VM.RequestTerminate(args.Length > 0 ? args[0] : Value.MakeUndefined());
        return Value.MakeUndefined();
    });

    var programA = Parser.Parse(mtTerminateCallerSample);
    var resolveA = Resolver.Resolve(programA, natives.Names);
    var compiledA = Compiler.Compile(programA, resolveA, natives);

    var programB = Parser.Parse(mtTerminateBystanderSample);
    var resolveB = Resolver.Resolve(programB, natives.Names);
    var compiledB = Compiler.Compile(programB, resolveB, natives);

    Console.WriteLine("Ausgabe (erwartet: GAR KEINE von A/B):");
    var handleA = FireRuntime.FireVm(compiledA.TopLevel, natives);
    var handleB = FireRuntime.FireVm(compiledB.TopLevel, natives);
    handleA.Join();
    handleB.Join();

    if (handleA.Error != null) Console.WriteLine($"FEHLER Thread A: {handleA.Error}");
    if (handleB.Error != null) Console.WriteLine($"FEHLER Thread B: {handleB.Error}");

    Console.WriteLine($"Beide Threads beendet. VM.ExitValue (erwartet: 42) = {VM.ExitValue}");

    VM.ResetTerminateForTests();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
    VM.ResetTerminateForTests();
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: unbehandelte Exception aus Fire-Thread -> 'catch threads()' im Main-Thread ===");

string mtThreadsHandlerSetup = """
var handled = func (e) => { print("Main-Thread: catch threads() -> " + e.message) }
""";

string mtThreadsFireScript = """
class MyError {
    string message

    construct(string message) {
        this.message = message
    }
}

throw new MyError("boom aus dem Fire-Thread")
""";

string mtThreadsMainLoopScript = """
var i = 0
while (i < 2000000) {
    i = i + 1
}
print("Main-Thread: Schleife fertig")
""";

try
{
    GlobalHandlers.ResetForTests();

    var handlerNatives = NativeRegistry.CreateDefault();
    var handlerProgram = Parser.Parse(mtThreadsHandlerSetup);
    var handlerResolve = Resolver.Resolve(handlerProgram, handlerNatives.Names);
    var handlerCompiled = Compiler.Compile(handlerProgram, handlerResolve, handlerNatives);
    var handlerScope = new Scope(null, isGlobal: true);
    var handlerVm = new VM(handlerCompiled.TopLevel, handlerScope, handlerNatives, handlerCompiled.Classes);
    handlerVm.Run();

    var handlerLambda = (LambdaValue)handlerScope.GetSlot(0).AsLambda();
    // typeName: null -> corresponds to 'catch threads()' (catches everything, like a
    // bare catch(e)) - avoids the main thread having to know 'MyError' as a
    // class (the two programs are compiled separately).
    GlobalHandlers.RegisterThreadsCatch(null, handlerLambda.Proto);

    var mainNatives = NativeRegistry.CreateDefault();
    var mainProgram = Parser.Parse(mtThreadsMainLoopScript);
    var mainResolve = Resolver.Resolve(mainProgram, mainNatives.Names);
    var mainCompiled = Compiler.Compile(mainProgram, mainResolve, mainNatives);
    var mainScope = new Scope(null, isGlobal: true);
    var mainVm = new VM(mainCompiled.TopLevel, mainScope, mainNatives, mainCompiled.Classes, isMainThreadVm: true);

    var fireNatives = NativeRegistry.CreateDefault();
    var fireProgram = Parser.Parse(mtThreadsFireScript);
    var fireResolve = Resolver.Resolve(fireProgram, fireNatives.Names);
    var fireCompiled = Compiler.Compile(fireProgram, fireResolve, fireNatives);

    var fireHandle = FireRuntime.FireVm(fireCompiled.TopLevel, fireNatives, fireCompiled.Classes);

    Console.WriteLine(
        "Ausgabe (erwartet: 'Main-Thread: catch threads() -> boom...' VOR " +
        "'Main-Thread: Schleife fertig'):");
    mainVm.Run();
    fireHandle.Join();

    if (fireHandle.Error != null)
        Console.WriteLine($"FEHLER Fire-Thread: {fireHandle.Error}");

    GlobalHandlers.ResetForTests();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
    GlobalHandlers.ResetForTests();
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: 'catch terminate(v)' laeuft im Main-Thread, dann sauberer Stopp ===");

string mtTerminateHandlerSetup = """
var onTerminate = func (v) => { print("Main-Thread: catch terminate(v) -> " + v) }
""";

string mtTerminateMainScript = """
var i = 0
while (i < 2000000) {
    i = i + 1
    if (i == 5) {
        __terminate(99)
    }
}
print("NIE ERREICHT")
""";

try
{
    VM.ResetTerminateForTests();
    GlobalHandlers.ResetForTests();

    var handlerNatives2 = NativeRegistry.CreateDefault();
    var handlerProgram2 = Parser.Parse(mtTerminateHandlerSetup);
    var handlerResolve2 = Resolver.Resolve(handlerProgram2, handlerNatives2.Names);
    var handlerCompiled2 = Compiler.Compile(handlerProgram2, handlerResolve2, handlerNatives2);
    var handlerScope2 = new Scope(null, isGlobal: true);
    var handlerVm2 = new VM(handlerCompiled2.TopLevel, handlerScope2, handlerNatives2, handlerCompiled2.Classes);
    handlerVm2.Run();

    var handlerLambda2 = (LambdaValue)handlerScope2.GetSlot(0).AsLambda();
    GlobalHandlers.RegisterTerminateCatch(handlerLambda2.Proto);

    var mainNatives2 = NativeRegistry.CreateDefault();
    mainNatives2.Register("__terminate", args =>
    {
        VM.RequestTerminate(args.Length > 0 ? args[0] : Value.MakeUndefined());
        return Value.MakeUndefined();
    });
    var mainProgram2 = Parser.Parse(mtTerminateMainScript);
    var mainResolve2 = Resolver.Resolve(mainProgram2, mainNatives2.Names);
    var mainCompiled2 = Compiler.Compile(mainProgram2, mainResolve2, mainNatives2);
    var mainScope2 = new Scope(null, isGlobal: true);
    var mainVm2 = new VM(mainCompiled2.TopLevel, mainScope2, mainNatives2, mainCompiled2.Classes, isMainThreadVm: true);

    Console.WriteLine("Ausgabe (erwartet NUR: 'Main-Thread: catch terminate(v) -> 99'):");
    mainVm2.Run();

    Console.WriteLine($"VM.ExitValue (erwartet 99) = {VM.ExitValue}");

    VM.ResetTerminateForTests();
    GlobalHandlers.ResetForTests();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
    VM.ResetTerminateForTests();
    GlobalHandlers.ResetForTests();
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-SPRACHSYNTAX: 'fire { ... }' ganz ohne C#-Bruecken ===");

string mtFireSyntaxSample = """
fire {
    print("hallo aus einem echten fire-Thread")
}
print("Hauptprogramm laeuft weiter")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtFireSyntaxSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (beide Zeilen, Reihenfolge nicht garantiert - kein Join in der Sprache):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    System.Threading.Thread.Sleep(200); // only for a clean test output order, no language requirement
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-SPRACHSYNTAX: 'fire taking X { ... }' - Isolation sichtbar ===");

string mtFireTakingSyntaxSample = """
class Counter {
    int value

    construct() {
        this.value = 10
    }
}

var counter = new Counter()
fire taking counter {
    counter.value = counter.value + 5
    print("im Fire-Thread: " + counter.value)
}
print("Hauptprogramm: counter.value = " + counter.value)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtFireTakingSyntaxSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine(
        "Ausgabe (erwartet: 'im Fire-Thread: 15' und 'Hauptprogramm: counter.value = 10' " +
        "- Hauptprogramm bleibt UNVERAENDERT, da 'taking' eine isolierte Kopie ist):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    System.Threading.Thread.Sleep(200);
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-SPRACHSYNTAX: 'sync' als Ausdruck (blockierend, voll) ===");

string mtSyncSample = """
class Player {
    int health
    construct() { this.health = 100 }
}

var player = new Player()
fire taking player {
    player.health = player.health - 10
    var result = sync player
    print("Fire-Thread: sync-Ergebnis = " + result)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtSyncSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    Console.WriteLine("Ausgabe (erwartet: 'Fire-Thread: sync-Ergebnis = true'):");
    vm.Run();
    System.Threading.Thread.Sleep(300);

    var player = (ObjectInstance)vmGlobalScope.GetSlot(0).AsObjectRef();
    Console.WriteLine($"Hauptprogramm NACH sync (erwartet: 90): player.health = {player.Fields["health"].AsInt()}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-SPRACHSYNTAX: 'try sync flat' als Ausdruck (nicht-blockierend, flach) ===");

string mtTrySyncFlatSample = """
class Inventory {
    int gold
    construct() { this.gold = 0 }
}

var inv = new Inventory()
fire taking inv {
    inv.gold = 42
    var result = try sync flat inv
    print("Fire-Thread: try sync flat Ergebnis = " + result)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtTrySyncFlatSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    Console.WriteLine("Ausgabe (erwartet: 'Fire-Thread: try sync flat Ergebnis = true'):");
    vm.Run();
    System.Threading.Thread.Sleep(300);

    var inv = (ObjectInstance)vmGlobalScope.GetSlot(0).AsObjectRef();
    Console.WriteLine($"Hauptprogramm NACH sync (erwartet: 42): inv.gold = {inv.Fields["gold"].AsInt()}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-SPRACHSYNTAX: 'leave' in echtem fire-Block, laeuft durch finally ===");

string mtLeaveSyntaxSample = """
fire {
    try {
        print("vor leave")
        leave
        print("NIE ERREICHT (im try)")
    }
    finally {
        print("finally im Fire-Thread")
    }
    print("NIE ERREICHT (nach try)")
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtLeaveSyntaxSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet NUR: 'vor leave' / 'finally im Fire-Thread'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    System.Threading.Thread.Sleep(200);
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-SPRACHSYNTAX: 'terminate(wert)' + 'catch terminate(v)' ===");

string mtTerminateSyntaxSample = """
catch terminate(v)
{
    print("Main-Thread: catch terminate(v) -> " + v)
}

var i = 0
while (i < 2000000) {
    i = i + 1
    if (i == 5) {
        terminate(77)
    }
}
print("NIE ERREICHT")
""";

try
{
    VM.ResetTerminateForTests();
    GlobalHandlers.ResetForTests();

    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtTerminateSyntaxSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes, isMainThreadVm: true);

    Console.WriteLine("Ausgabe (erwartet NUR: 'Main-Thread: catch terminate(v) -> 77'):");
    vm.Run();
    Console.WriteLine($"VM.ExitValue (erwartet 77) = {VM.ExitValue}");

    VM.ResetTerminateForTests();
    GlobalHandlers.ResetForTests();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
    VM.ResetTerminateForTests();
    GlobalHandlers.ResetForTests();
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-SPRACHSYNTAX: 'catch threads()' faengt Exception aus echtem fire-Block ===");

string mtThreadsSyntaxSample = """
catch threads()
{
    print("Main-Thread: catch threads() gefangen")
}

class MyError {
    string message
    construct(string message) { this.message = message }
}

fire {
    throw new MyError("boom aus echter Sprachsyntax")
}

var i = 0
while (i < 2000000) {
    i = i + 1
}
print("Hauptprogramm fertig")
""";

try
{
    GlobalHandlers.ResetForTests();

    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtThreadsSyntaxSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes, isMainThreadVm: true);

    Console.WriteLine(
        "Ausgabe (erwartet: 'Main-Thread: catch threads() gefangen' VOR 'Hauptprogramm fertig'):");
    vm.Run();
    System.Threading.Thread.Sleep(200);

    GlobalHandlers.ResetForTests();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
    GlobalHandlers.ResetForTests();
}

Console.WriteLine();
Console.WriteLine("=== Actors: 'fire with actorA' + blockierendes 'process' ===");

string actorBasicSample = """
actor Logger {
    string lastMessage

    construct() {
        this.lastMessage = ""
    }

    log(string msg) {
        this.lastMessage = msg
        print("Logger (Heimat-Thread): " + msg)
    }
}

var logger = new Logger()
fire with logger {
    logger.log("hallo vom fire-Thread")
}
process logger
print("Hauptprogramm: logger.lastMessage = " + logger.lastMessage)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(actorBasicSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine(
        "Ausgabe (erwartet: 'Logger (Heimat-Thread): hallo vom fire-Thread' " +
        "dann 'Hauptprogramm: logger.lastMessage = hallo vom fire-Thread'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Actors: 'try process' nicht-blockierend (leer -> false, dann true) ===");

string actorTryProcessSample = """
actor Counter {
    int value

    construct() {
        this.value = 0
    }

    increment() {
        this.value = this.value + 1
    }
}

var counter = new Counter()
var before = try process counter
print("try process VOR jeder Nachricht (erwartet false): " + before)

fire with counter {
    counter.increment()
}

process counter
print("Hauptprogramm: counter.value = " + counter.value)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(actorTryProcessSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 'false' dann 'counter.value = 1'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Actors: Methodenaufruf ist IMMER asynchron, auch vom eigenen Heimat-Thread ===");

string actorSameThreadSample = """
actor Greeter {
    string name

    construct(string name) {
        this.name = name
    }

    greet() {
        print("greet() ist jetzt gelaufen")
    }
}

var greeter = new Greeter("Welt")
greeter.greet()
print("Vor process: greet() ist noch NICHT gelaufen (landet erst in der Mailbox)")
process greeter
print("Nach process: siehe oben")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(actorSameThreadSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine(
        "Ausgabe (erwartet: 'Vor process...' VOR 'greet() ist jetzt gelaufen'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-Randfall: mehrere 'taking'-Erfassungen (Objekt + Primitiv) ===");

string mtMultiTakingSample = """
class Vault {
    int gold
    construct() { this.gold = 0 }
}

var vault = new Vault()
var bonus = 50
fire taking vault taking bonus {
    vault.gold = vault.gold + bonus
    print("Fire-Thread: vault.gold = " + vault.gold)
}
print("Hauptprogramm laeuft weiter (vault.gold bleibt unveraendert): " + vault.gold)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtMultiTakingSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine(
        "Ausgabe (erwartet: 'Fire-Thread: vault.gold = 50' und 'Hauptprogramm ... 0', " +
        "Reihenfolge nicht garantiert):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    System.Threading.Thread.Sleep(200);
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-Randfall: 'fire MethodA(args)'-Aufrufform ===");

string mtFireCallFormSample = """
class Robot {
    string name
    int energy

    construct(string name) {
        this.name = name
        this.energy = 100
    }

    reportStatus(string context) {
        print("Robot " + this.name + " (" + context + "): energy = " + this.energy)
    }

    reportAsync(string context) {
        fire reportStatus(context)
    }
}

var robot = new Robot("R2")
robot.reportAsync("aus reportAsync")
print("Hauptprogramm laeuft weiter")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtFireCallFormSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine(
        "Ausgabe (erwartet: 'Robot R2 (aus reportAsync): energy = 100' und " +
        "'Hauptprogramm laeuft weiter', Reihenfolge nicht garantiert):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    System.Threading.Thread.Sleep(200);
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading-Randfall: 'actor extends X' (Klassen-Erweiterung für Actors) ===");

string mtActorExtendsSample = """
actor Logger2 {
    string lastMessage
    construct() { this.lastMessage = "" }
}

actor extends Logger2 {
    log(string msg) {
        this.lastMessage = msg
        print("Logger2 (Heimat-Thread, via Erweiterung): " + msg)
    }
}

var logger = new Logger2()
fire with logger {
    logger.log("erweiterte Actor-Methode")
}
process logger
print("Hauptprogramm: logger.lastMessage = " + logger.lastMessage)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtActorExtendsSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine(
        "Ausgabe (erwartet: 'Logger2 (Heimat-Thread, via Erweiterung): erweiterte Actor-Methode' " +
        "dann 'Hauptprogramm: logger.lastMessage = erweiterte Actor-Methode'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: Read-only-Snapshot der Hauptprogramm-Globals in 'fire' ===");

string mtGlobalSnapshotSample = """
var counter = 10
var greeting = "hallo"

fire {
    print("Fire-Thread sieht: counter = " + counter + ", greeting = " + greeting)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtGlobalSnapshotSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 'Fire-Thread sieht: counter = 10, greeting = hallo'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    System.Threading.Thread.Sleep(200);
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: Schreiben auf ein Hauptprogramm-Global in 'fire' wird uebersetzt (laeuft als Sektion, siehe Block 'Globals und Fire-Threads') ===");

string mtGlobalWriteRejectedSample = """
var counter = 10

fire {
    counter = 99
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtGlobalWriteRejectedSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("OK: das Schreiben wird uebersetzt");
}
catch (ResolverException ex)
{
    Console.WriteLine($"FEHLER: Schreiben auf ein Global in 'fire' wurde abgelehnt: {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER (falscher Exception-Typ): {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: Objekt-Global wird vom Thread direkt geaendert (Sektion, bei Programmende vom Hauptprogramm abgearbeitet) ===");

string mtGlobalObjectSnapshotSample = """
class Vault {
    int gold
    construct() { this.gold = 100 }
}

var vault = new Vault()

fire {
    print("Fire-Thread VOR Mutation: vault.gold = " + vault.gold)
    vault.gold = 999
    print("Fire-Thread NACH Mutation (eigene Kopie): vault.gold = " + vault.gold)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtGlobalObjectSnapshotSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    Console.WriteLine(
        "Ausgabe (erwartet: 'VOR Mutation: ... = 100' dann 'NACH Mutation ... = 999'):");
    vm.Run();
    System.Threading.Thread.Sleep(200);

    var vault = (ObjectInstance)vmGlobalScope.GetSlot(0).AsObjectRef();
    Console.WriteLine(
        $"Hauptprogramm NACH fire (erwartet 999, die Sektion lief beim Programmende): vault.gold = {vault.Fields["gold"].AsInt()}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: 'taking' mit gleichem Namen wie ein Hauptprogramm-Global (Schattierung) ===");

string mtShadowingSample = """
var counter = 10

fire taking counter {
    counter = counter + 1
    print("Fire-Thread (eigene, schreibbare Erfassung): counter = " + counter)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtShadowingSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 'Fire-Thread (eigene, schreibbare Erfassung): counter = 11'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    System.Threading.Thread.Sleep(200);
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: '#noshadow' schaltet den Globals-Snapshot ab ===");

string mtNoShadowSample = """
#noshadow

var counter = 10

fire {
    print(counter)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtNoShadowSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("FEHLER: Hätte einen ResolverException erwarten sollen (Snapshot ist ja abgeschaltet), ist aber durchgelaufen!");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler (Snapshot korrekt abgeschaltet): {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER (falscher Exception-Typ): {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: '#noshadow' + taking funktioniert weiterhin normal ===");

string mtNoShadowTakingSample = """
#noshadow

var counter = 10

fire taking counter {
    print("Fire-Thread (taking trotz #noshadow): counter = " + counter)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(mtNoShadowTakingSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 'Fire-Thread (taking trotz #noshadow): counter = 10'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
    System.Threading.Thread.Sleep(200);
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Operatoren: Potenz '^', bitweise '&'/'|'/'#', Shift '<<'/'>>' ===");

string operatorsSample = """
print("2^10 = " + (2 ^ 10))
print("2^3^2 = " + (2 ^ 3 ^ 2))
print("2 * 3^2 = " + (2 * 3 ^ 2))
print("-2^2 = " + (-2 ^ 2))
print("2.0 ^ 0.5 = " + (2.0 ^ 0.5))
print("2 ^ -1 = " + (2 ^ -1))

print("12 & 10 = " + (12 & 10))
print("12 | 10 = " + (12 | 10))
print("12 # 10 = " + (12 # 10))

print("1 << 4 = " + (1 << 4))
print("256 >> 4 = " + (256 >> 4))
print("1 + 2 << 1 = " + (1 + 2 << 1))
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(operatorsSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine(
        "Ausgabe (erwartet: 1024 / 512 / 18 / -4 / 1.41... / 0.5 / 8 / 14 / 6 / 16 / 16 / 6):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Operatoren: 0x/0b-Literale, inkl. weiterhin gültigem '0b' als Bit-Einheit ===");

string radixLiteralsSample = """
var hex = 0xFFCC8080
var bin = 0b01101100
print("0xFFCC8080 = " + hex)
print("0b01101100 = " + bin)
print("0b1010 & 0b0110 = " + (0b1010 & 0b0110))

var zeroBits = 0b
print("0b (null Bit, weiterhin die alte Einheiten-Bedeutung) = " + zeroBits)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(radixLiteralsSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 4291592320 / 108 / 2 / 0b):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Operatoren: '<<'/'>>' kollidieren nicht mit generischen Typargumenten ===");

// Note: NESTED generic type ARGUMENTS at the point of use
// (e.g. 'new Box<Box<int>>(...)') are, independently of
// this change, still NOT supported by this parser (ParseOptionalTypeParamNames reads
// each type argument only as a simple name, without itself allowing
// its own '<...>' recursively) - that is a restriction that already existed,
// independent of the new operators. This test checks
// therefore deliberately only SINGLE-level generics (the only supported form)
// directly NEXT TO a '>>' shift, to prove that the two do not
// get in each other's way.
string genericsNoCollisionSample = """
class Wrapper<T> {
    T value
    construct(T value) { this.value = value }
}

var wrapped = new Wrapper<int>(21)
print("Einstufiges Generic 'Wrapper<int>' funktioniert weiterhin: " + wrapped.value)
print("Direkt danach, in derselben Zeile, als Ausdruck: " + (new Wrapper<int>(5).value << 1))
print("Und unabhaengig davon ein '>>'-Shift: " + (64 >> 2))
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(genericsNoCollisionSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: ... 21 / ... 10 / ... 16):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Generics: verschachtelte Typ-Argumente 'new Box<Box<int>>()' ===");

string nestedGenericsSample = """
class Box<T> {
    T value
    construct(T value) { this.value = value }
}

var inner = new Box<int>(21)
var outer = new Box<Box<int>>(inner)
print("outer.value.value = " + outer.value.value)

var tripleNested = new Box<Box<Box<int>>>(outer)
print("tripleNested.value.value.value = " + tripleNested.value.value.value)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(nestedGenericsSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 21 / 21):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Operator-Überladung: 'operator[]' (lesend + schreibend) ===");

string operatorIndexSample = """
class Pair {
    class first
    class second

    construct(class first, class second) {
        this.first = first
        this.second = second
    }

    operator[](int index) {
        if (index == 0) { return this.first }
        return this.second
    }

    operator[](int index, class value) {
        if (index == 0) { this.first = value }
        else { this.second = value }
    }
}

var p = new Pair(10, 20)
print("p[0] = " + p[0])
print("p[1] = " + p[1])
p[0] = 99
print("nach p[0] = 99: p[0] = " + p[0])
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(operatorIndexSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 10 / 20 / 99):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Operator-Überladung: '+', '==' auf einer eigenen Klasse ===");

string operatorArithmeticSample = """
class Vector2 {
    float x
    float y

    construct(float x, float y) {
        this.x = x
        this.y = y
    }

    operator+(class other) {
        return new Vector2(this.x + other.x, this.y + other.y)
    }

    operator==(class other) {
        return this.x == other.x && this.y == other.y
    }
}

var a = new Vector2(1.0, 2.0)
var b = new Vector2(3.0, 4.0)
var c = a + b
print("a + b = (" + c.x + ", " + c.y + ")")

var d = new Vector2(4.0, 6.0)
print("c == d: " + (c == d))
print("a == b: " + (a == b))
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(operatorArithmeticSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: (4, 6) / true / false):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Operator-Überladung: Objekt ohne Überladung faellt weiterhin auf Standardverhalten zurueck ===");

string operatorFallbackSample = """
class Plain {
    int value
    construct(int value) { this.value = value }
}

var p1 = new Plain(5)
var p2 = new Plain(5)
print("p1 == p2 (keine Ueberladung, Referenzvergleich, erwartet false): " + (p1 == p2))
print("p1 == p1 (dasselbe Objekt, erwartet true): " + (p1 == p1))
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(operatorFallbackSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: false / true):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Format-Strings: Interpolation, Format-Spezifizierer, escapte Klammern ===");

string formatStringSample = """
var name = "Welt"
var x = 255
var pi = 3.14159

var s1 = $"Hallo, {name}!"
print(s1)

var s2 = $"Hex: {x:X}, Bin: {x:B}, gepolstert: {x:D5}"
print(s2)

var s3 = $"Pi ist ungefaehr {pi:F2}"
print(s3)

var s4 = $"Escapte Klammern: {{literal}} und ein Ausdruck: {1 + 2}"
print(s4)

var s5 = $"Verschachtelter String im Ausdruck: {"nested" + "-" + name}"
print(s5)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(formatStringSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 'Hallo, Welt!' / 'Hex: FF, Bin: 11111111, gepolstert: 00255' / " +
        "'Pi ist ungefaehr 3.14' / 'Escapte Klammern: {literal} und ein Ausdruck: 3' / " +
        "'Verschachtelter String im Ausdruck: nested-Welt'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Format-Strings: als Funktionsparameter, sowie Fehlerfall (Format auf falschem Typ) ===");

string formatStringParamSample = """
class Begruesser {
    Grusz(string wer, int alter) {
        print($"Hallo {wer}, du bist {alter} Jahre alt (hex: {alter:X})")
    }
}
new Begruesser().Grusz("Anna", 30)

var text = "kein int"
print($"Das schlaegt fehl: {text:X}")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(formatStringParamSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 'Hallo Anna, du bist 30 Jahre alt (hex: 1E)', danach eine klare Fehlermeldung):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"Laufzeit-Fehler (erwartet, zeigt korrekte Fehlerbehandlung): {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Byte-Puffer: Erzeugung, Indexierung, ASCII-Konvertierung ===");

string bufferAsciiSample = """
var buf = new byte[4]
buf[0] = 0x41
buf[1] = 0x42
buf[2] = 0x43
buf[3] = 0x44
print("buf.length = " + buf.length)
print("buf[0] = " + buf[0])
print("buf.ToString() (ASCII) = " + buf.ToString())

var s = "Hello"
var bytes = s.ToBytes()
print("bytes.length = " + bytes.length)
print("bytes.ToString() = " + bytes.ToString())

var c = 'A'
var b = c.ToByte()
print("c.ToByte() = " + b)
print("b.ToChar() = " + b.ToChar())
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(bufferAsciiSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 4 / 65 / ABCD / 5 / Hello / 65 / A):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Byte-Puffer: 'Unicode' (feste Breite), Endianness ===");

string bufferUnicodeSample = """
var wideStr = "Hi"
var wbuf = wideStr.ToUnicode(2)
print("wbuf.length = " + wbuf.length)
print("wbuf.ToUnicode() = " + wbuf.ToUnicode())

var wc = 'X'.ToUnicode(2)
print("wc.ToUnicodeChar() = " + wc.ToUnicodeChar())

var buf = new byte[4]
buf[0] = 0x41
buf[1] = 0x42
buf[2] = 0x43
buf[3] = 0x44
print("buf.littleEndian = " + buf.littleEndian)
var le = buf.ToLittleEndian()
var be = buf.ToBigEndian()
print("le[0] = " + le[0] + ", be[0] = " + be[0])
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(bufferUnicodeSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 4 / Hi / X / dann host-abhaengig - auf den meisten Systemen " +
        "(Little Endian): littleEndian=True, le[0]=65, be[0]=68):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Timeout-faehige native APIs: 'try Name(args)' ===");

string tryNativeSample = """
var timeoutMs = 500ms

var result1 = try TryReadSensor(timeoutMs)
print("Erster Versuch (erwartet Timeout -> undefined): " + result1)

var result2 = try TryReadSensor(timeoutMs)
print("Zweiter Versuch (erwartet Erfolg -> 42): " + result2)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    int attemptCount = 0;
    natives.RegisterTryable("TryReadSensor", (Value[] args, out Value result) =>
    {
        // Demonstrates that the host implementation can read the unit of a
        // timeout argument directly (Value.Unit is already
        // public) - regardless of whether the call is
        // ultimately successful or not.
        var timeoutVal = args[0];
        Console.WriteLine($"  (native Seite: Timeout-Argument hat Einheit '{timeoutVal.Unit}', Rohwert {timeoutVal.AsInt()})");

        attemptCount++;
        if (attemptCount == 1)
        {
            result = default;
            return false; // simulated timeout on the first attempt
        }
        result = Value.MakeInt(42);
        return true;
    });

    var program = Parser.Parse(tryNativeSample);
    var resolveResult = Resolver.Resolve(program, natives.Names, natives.TryableNames);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: undefined / 42):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Native Callbacks: Lambda registrieren, vom 'nativen' Code (isoliert) aufrufen ===");

string callbackSample = """
var counter = 0

RegisterCallback("OnTick", func(int n) => {
    counter = counter + n
    print("Im Callback: counter = " + counter)
})

print("Hauptprogramm vor dem simulierten nativen Event: counter = " + counter)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var registeredCallbacks = new System.Collections.Generic.Dictionary<string, LambdaValue>();
    natives.Register("RegisterCallback", args =>
    {
        string name = args[0].AsString();
        registeredCallbacks[name] = (LambdaValue)args[1].AsLambda();
        return Value.MakeUndefined();
    });

    var program = Parser.Parse(callbackSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 'counter = 0'):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();

    // Simulates the native host that later (e.g. from another
    // thread, here synchronously for simplicity) fires an event -
    // the snapshot is taken HERE, while the main program is currently
    // NOT running (see the VM.SnapshotGlobals documentation).
    var snapshot = vm.SnapshotGlobals();
    Console.WriteLine("Simuliertes natives Event feuert 'OnTick' mit n=5 auf einer isolierten Kopie:");
    if (registeredCallbacks.TryGetValue("OnTick", out var cb))
    {
        FireRuntime.CallCallback(cb, new[] { Value.MakeInt(5) }, natives, compiled.Classes, snapshot,
            ex => Console.WriteLine($"  (unbehandelte Exception im Callback: {ex.Message})"));
    }

    Console.WriteLine("Ausgabe danach (erwartet weiterhin 'counter = 0' im Hauptprogramm - " +
        "der Callback hat nur seine eigene Kopie veraendert):");
    var counterAfter = vm.SnapshotGlobals()[0];
    Console.WriteLine("Hauptprogramm-'counter' nach dem Callback (aus der VM ausgelesen): " + counterAfter);
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Freie Praeprozessor-Direktiven: #name wert1, wert2, ... ===");

try
{
    var registry = new DirectiveRegistry(); // completely empty, NOT CreateDefault()
    var receivedArgs = new List<Value>();
    registry.Register("mydirective", 4, (ctx, args, line) =>
    {
        receivedArgs.AddRange(args);
        return null; // produces no replacement text
    });

    string source = """
    #mydirective "value1", 632, 84.23, 74mm
    print("nach der Direktive")
    """;

    string preprocessed = Preprocessor.Process(source, "/home/claude", registry).Source;
    Console.WriteLine($"Erhaltene Argumente ({receivedArgs.Count}, erwartet 4):");
    foreach (var v in receivedArgs)
        Console.WriteLine($"  {v.Kind}: {v}");

    Console.WriteLine("Vorverarbeiteter Text enthaelt noch 'print(...)' (erwartet true): " +
        preprocessed.Contains("print(\"nach der Direktive\")"));
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Falsche Parameteranzahl -> klarer Fehler ===");

try
{
    var registry = new DirectiveRegistry();
    registry.Register("needsTwo", 2, (ctx, args, line) => null);
    Preprocessor.Process("#needsTwo \"nur einer\"\n", "/home/claude", registry);
    Console.WriteLine("FEHLER: haette werfen sollen, hat aber nicht.");
}
catch (PreprocessorException ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Unbekannte Direktive (z.B. '#extern'/'#noshadow') wird unveraendert durchgereicht ===");

try
{
    var registry = new DirectiveRegistry(); // 'extern' is NOT registered here
    string source = "#extern \"kernel32.dll\"\nprint(\"x\")\n";
    string result = Preprocessor.Process(source, "/home/claude", registry).Source;
    Console.WriteLine("Zeile blieb erhalten (erwartet true): " + result.Contains("#extern \"kernel32.dll\""));
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== #include: globale Komposition statt Baumprinzip ===");

try
{
    string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fire_include_test_" + System.Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(tmpDir);
    string sharedPath = System.IO.Path.Combine(tmpDir, "shared.txt");
    System.IO.File.WriteAllText(sharedPath, "// GEMEINSAM_INKLUDIERTE_MARKIERUNG\n");

    string rootA = "#include \"shared.txt\"\nprint(\"A\")\n";
    string rootB = "#include \"shared.txt\"\nprint(\"B\")\n";

    // Old behaviour (two INDEPENDENT Process() calls, each with its own set) -
    // the mark ends up twice in the sum.
    string outA = Preprocessor.Process(rootA, tmpDir).Source;
    string outB = Preprocessor.Process(rootB, tmpDir).Source;
    int countIndependent = CountOccurrences(outA + outB, "GEMEINSAM_INKLUDIERTE_MARKIERUNG");
    Console.WriteLine($"Getrennte Process()-Aufrufe: Markierung {countIndependent}x (erwartet 2x, je einmal pro Aufruf).");

    // New behaviour: ONE shared 'alreadyIncluded' set across BOTH
    // calls (as RuntimeSession.Build now does it for prelude +
    // user code) - the mark ends up only ONCE in total.
    var shared = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    string outA2 = Preprocessor.Process(rootA, tmpDir, shared).Source;
    string outB2 = Preprocessor.Process(rootB, tmpDir, shared).Source;
    int countShared = CountOccurrences(outA2 + outB2, "GEMEINSAM_INKLUDIERTE_MARKIERUNG");
    Console.WriteLine($"Geteilte 'alreadyIncluded'-Menge: Markierung {countShared}x (erwartet 1x, global einmal).");
    Console.WriteLine("Beide Root-Ausgaben haben trotzdem noch ihr eigenes print() (erwartet true): " +
        (outA2.Contains("print(\"A\")") && outB2.Contains("print(\"B\")")));

    System.IO.Directory.Delete(tmpDir, recursive: true);
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== VmExecutionMode: Debug/Release/Performance ===");

string modeTestScript = """
    var arr = new int[3]
    arr[0] = 10
    arr[1] = 20
    arr[2] = 30
    print("Summe: " + (arr[0] + arr[1] + arr[2]))

    try {
        print(arr[10])
    } catch (IndexOutOfBoundsException e) {
        print("Skript-seitig gefangen: " + e.message)
    }
    """;

foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
{
    Console.WriteLine($"--- Modus: {mode} ---");
    try
    {
        var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, modeTestScript));
        var natives = NativeRegistry.CreateDefault();
        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        var globalScope2 = new Scope(null, isGlobal: true);
        var vm = new VM(compiled.TopLevel, globalScope2, natives, compiled.Classes, executionMode: mode);
        vm.Run();
    }
    catch (System.Exception ex)
    {
        // Only expected in performance mode - the invalid arr[10] access
        // breaks through there as a raw, uncaught .NET exception, instead of
        // being handled cleanly in the script itself via catch(e : IndexOutOfBoundsException)
        // (as in debug/release).
        Console.WriteLine($"Roh durchgeschlagene Exception ({ex.GetType().Name}): {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Zugriffsmodifikatoren (public/private/protected) ===");

string accessTestScript = """
    class Base {
        private int secret
        protected int shared

        public construct() {
            this.secret = 1
            this.shared = 2
        }

        public int GetSecret() {
            return this.secret
        }
    }

    class Derived : Base {
        public int TryReadShared() {
            // protected - allowed from a derived class
            return this.shared
        }
    }

    class Locked {
        private construct() {
        }
    }

    var b = new Base()
    print("b.GetSecret() (public Methode, greift intern auf private Feld zu) = " + b.GetSecret())

    var d = new Derived()
    print("d.TryReadShared() (protected Feld der Basisklasse, ueber this) = " + d.TryReadShared())

    try {
        print(b.secret)
        print("FEHLER: haette AccessDeniedException werfen sollen")
    } catch (AccessDeniedException e) {
        print("Erwartet gefangen (privates Feld von aussen): " + e.message)
    }

    try {
        new Locked()
        print("FEHLER: haette AccessDeniedException werfen sollen")
    } catch (AccessDeniedException e) {
        print("Erwartet gefangen (privater Konstruktor von aussen): " + e.message)
    }
    """;

try
{
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, accessTestScript));
    var natives = NativeRegistry.CreateDefault();
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    var globalScope2 = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, globalScope2, natives, compiled.Classes);
    vm.Run();
    if (vm.UnhandledException != null)
        Console.WriteLine($"FEHLER: unerwartete unbehandelte Exception: {new UncaughtScriptException(vm.UnhandledException).Message}");
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Namespaces (namespace/#using) ===");

string namespaceTestScript = """
    namespace Geometry {
        class Point {
            int x
            int y
        }

        class Circle : Point {
            int radius

            Point MakeOrigin() {
                return new Point()
            }
        }
    }

    #using Geometry

    class Named3DPoint : Point {
        int z
    }

    var c = new Circle()
    c.x = 5
    c.y = 10
    c.radius = 3
    print("Circle (unqualifiziert via #using erzeugt): x=" + c.x + " y=" + c.y + " radius=" + c.radius)

    var origin = c.MakeOrigin()
    print("MakeOrigin() (unqualifizierte new Point() INNERHALB einer Methode im selben Namespace): ist of Point = " + (origin is of Point))

    var n = new Named3DPoint()
    n.x = 1
    n.y = 2
    n.z = 3
    print("Named3DPoint (Basisklasse per #using aufgeloest): x=" + n.x + " y=" + n.y + " z=" + n.z)
    """;

try
{
    var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var sources = new[] { fire.Standard.Prelude.Source, namespaceTestScript }
        .Select(s => Preprocessor.Process(s, Directory.GetCurrentDirectory(), alreadyIncluded))
        .ToList();
    var program = Parser.ParseMultiple(sources);
    var natives = NativeRegistry.CreateDefault();
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    var globalScope2 = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, globalScope2, natives, compiled.Classes);
    vm.Run();
    if (vm.UnhandledException != null)
        Console.WriteLine($"FEHLER: unerwartete unbehandelte Exception: {new UncaughtScriptException(vm.UnhandledException).Message}");
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== ParseMultiple: Top-Level-Code in ZWEI verschiedenen (nicht-letzten) Dateien mit eigenem #using ===");

// Exactly the scenario that would not work with a single program-wide usings list:
// HERE both fileA (not the last
// source!) and mainFile have top-level code of their own, each referencing a
// DIFFERENT class of the same name unqualified. Each TypeRef
// carries its own namespace context directly on itself (see
// Ast.TypeRef.Namespaces), which is why this now works independently
// of which file/at which position a reference stands.
string twoTopLevelFileA = """
    namespace LibA {
        class Helper {
            int value
        }
    }

    #using LibA

    var globalFromFileA = new Helper()
    globalFromFileA.value = 111
    print("Top-Level in fileA (nicht die letzte Quelle!): globalFromFileA.value = " + globalFromFileA.value)
    """;

string twoTopLevelFileB = """
    namespace LibB {
        class Helper {
            int otherValue
        }
    }

    #using LibB

    var globalFromMain = new Helper()
    globalFromMain.otherValue = 222
    print("Top-Level in der letzten Quelle: globalFromMain.otherValue = " + globalFromMain.otherValue)
    """;

try
{
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, twoTopLevelFileA, twoTopLevelFileB));
    var natives = NativeRegistry.CreateDefault();
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    var globalScope3 = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, globalScope3, natives, compiled.Classes);
    vm.Run();
    if (vm.UnhandledException != null)
        Console.WriteLine($"FEHLER: unerwartete unbehandelte Exception: {new UncaughtScriptException(vm.UnhandledException).Message}");
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== ParseMultiple: lokale #using-Sichtbarkeit pro Datei ===");

// Deliberately the same simple class name 'Helper' in TWO different
// namespaces - with program-wide (instead of local) usings that would be
// ambiguous: 'new Helper()' in fileB should actually hit LibB.Helper,
// but with globally shared usings it would easily (depending on the
// order) wrongly hit LibA.Helper.
string fileA = """
    namespace LibA {
        class Helper {
            int value
        }
    }

    #using LibA

    class UserOfA {
        int result

        public construct() {
            var h = new Helper()
            h.value = 11
            this.result = h.value
        }
    }
    """;

string fileB = """
    namespace LibB {
        class Helper {
            int otherValue
        }
    }

    #using LibA

    class UserOfB {
        int result

        public construct() {
            var h = new Helper()
            h.value = 22
            this.result = h.value
        }
    }
    """;

string mainFile = """
    var a = new UserOfA()
    var b = new UserOfB()
    print("UserOfA.result (ueber lokales #using LibA in fileA) = " + a.result)
    print("UserOfB.result (ueber lokales #using LibB in fileB) = " + b.result)
    """;

try
{
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, fileA, fileB, mainFile));
    var natives = NativeRegistry.CreateDefault();
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    var globalScope2 = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, globalScope2, natives, compiled.Classes);
    vm.Run();
    if (vm.UnhandledException != null)
        Console.WriteLine($"FEHLER: unerwartete unbehandelte Exception: {new UncaughtScriptException(vm.UnhandledException).Message}");
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== '++'/'--' auf Variable, Feld, Index (Praefix und Postfix) ===");

string incDecScript = """
    class Counter {
        int value

        public construct() {
            this.value = 10
        }
    }

    var x = 5
    print("x++ = " + x++)
    print("x danach = " + x)
    print("++x = " + ++x)
    print("x danach = " + x)
    print("--x = " + --x)
    print("x danach = " + x)

    var c = new Counter()
    print("c.value++ = " + c.value++)
    print("c.value danach = " + c.value)
    print("++c.value = " + ++c.value)
    print("c.value danach = " + c.value)

    var arr = new int[3]
    arr[0] = 100
    print("arr[0]++ = " + arr[0]++)
    print("arr[0] danach = " + arr[0])
    arr[1] = 0
    print("++arr[1] = " + ++arr[1])
    print("arr[1] danach = " + arr[1])
    """;

try
{
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, incDecScript));
    var natives = NativeRegistry.CreateDefault();
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    var globalScope2 = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, globalScope2, natives, compiled.Classes);
    vm.Run();
    if (vm.UnhandledException != null)
        Console.WriteLine($"FEHLER: unerwartete unbehandelte Exception: {new UncaughtScriptException(vm.UnhandledException).Message}");
}
catch (System.Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Generics: generische Klasse mit demselben Namen wie eine nicht-generische ===");

string genericSameNameSample = """
class Box {
    int v
    construct(int v) { this.v = v }
    Describe() { return "Box(" + this.v + ")" }
}

class Box<T> {
    T item
    construct(T item) { this.item = item }
    Describe() { return "Box<T>(" + this.item + ")" }
}

class Pair<A, B> {
    A first
    B second
    construct(A a, B b) { this.first = a; this.second = b }
}

class Pair {
    string s
    construct() { this.s = "plain" }
}

class extends Box {
    Extra() { return "extra" }
}

var plain = new Box(1)
var generic = new Box<int>(2)
print(plain.Describe())
print(generic.Describe())
print(plain.Extra())
print(new Pair().s)
var pair = new Pair<int, int>(3, 4)
print(pair.first + pair.second)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(genericSameNameSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: Box(1) / Box<T>(2) / extra / plain / 7):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Generics: gleicher Name, statische Auto-Property in der generischen Klasse ===");

string genericSameNameStaticSample = """
class Counter { }

class Counter<T> {
    static int Total { get; }
    Read() { return Total }
}

var c = new Counter<int>()
print("gelesen: " + c.Read())
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(genericSameNameStaticSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: gelesen: undefined - das Backing-Field gehört zur GENERISCHEN Klasse, nicht zur gleichnamigen):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Generics: gleicher Name - falsche Typ-Argumente / Doppeldefinition (müssen fehlschlagen) ===");

foreach (var (label, source) in new[]
{
    ("falsche Typ-Argument-Anzahl", "class Box { }\nclass Box<T> { }\nvar x = new Box<int, int>()"),
    ("zwei generische mit gleicher Anzahl", "class Box { }\nclass Box<T> { }\nclass Box<U> { }"),
})
{
    try
    {
        var natives = NativeRegistry.CreateDefault();
        var program = Parser.Parse(source);
        Resolver.Resolve(program, natives.Names);
        Console.WriteLine($"FEHLER ({label}): haette fehlschlagen muessen");
    }
    catch (ResolverException ex)
    {
        Console.WriteLine($"Erwarteter Fehler ({label}): {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Resolver: sammelt ALLE Fehler statt beim ersten abzubrechen ===");

string manyResolverErrors = """
var a = unknown1
var b = a + 1
var c = new Nope()
class K : Missing {
    Foo() {
        return alsoMissing
    }
    Bar() {
        return stillMissing
    }
}
print(zzz)
try { print(x1) } catch (NoSuchException e) { print(e) } finally { print(x2) }
print(p1 + p2)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(manyResolverErrors);
    Resolver.Resolve(program, natives.Names);
    Console.WriteLine("FEHLER: haette fehlschlagen muessen");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Message (= Meldung des ersten gesammelten Fehlers, wie bisher): {ex.Message}");
    Console.WriteLine($"Anzahl gesammelter Fehler (erwartet: 11): {ex.Errors.Count}");
    foreach (var error in ex.Errors)
        Console.WriteLine($"  Zeile {error.Line}: {error.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Compiler: sammelt ALLE Fehler statt beim ersten abzubrechen ===");

string manyCompilerErrors = """
class A {
    int f = print
    M1() {
        var x = print
        var y = 2
        var z = print
    }
    M2() { print = 5 }
}
var top1 = print
var ok = 1
var lam = func () => { var inner = print
                       return inner }
if (ok == 1) {
    var nested = print
}
print(ok)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(manyCompilerErrors);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("FEHLER: haette fehlschlagen muessen");
}
catch (CompilerException ex)
{
    Console.WriteLine($"Anzahl gesammelter Fehler (erwartet: 7): {ex.Errors.Count}");
    foreach (var error in ex.Errors)
        Console.WriteLine($"  Zeile {error.Line}: {error.Message}");
    Console.WriteLine($"Ist eine NotSupportedException (bestehender Code faengt sie weiter): {ex is NotSupportedException}");
}

Console.WriteLine();
Console.WriteLine("=== Statische Properties: Schreiben ueber statischen Setter (auto + eigener Body) ===");

string staticPropertySetSample = """
class Counter {
    static int Total { get; set }
    static int Twice {
        get { return _twice }
        set { _twice = value * 2 }
    }
    static int _twice

    static Init() { Total = 5 }
    static Bump() {
        Total = Total + 10
        return Total
    }
}

Counter.Init()
print(Counter.Bump())
Counter.Total = 7
print(Counter.Total)
Counter.Twice = 4
print(Counter.Twice)
var chained = (Counter.Total = 9)
print(chained)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(staticPropertySetSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);

    Console.WriteLine("Ausgabe (erwartet: 15 / 7 / 8 / 9):");
    var vmGlobalScope = new Scope(null, isGlobal: true);
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Statische Properties: Schreiben ohne Setter (muss fehlschlagen) ===");

string staticPropertyNoSetterSample = """
class Counter {
    static int Total { get; }
}
Counter.Total = 1
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(staticPropertyNoSetterSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    var vmGlobalScope = new Scope(null, isGlobal: true);
    new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes).Run();
    Console.WriteLine("FEHLER: haette fehlschlagen muessen");
}
catch (Exception ex)
{
    Console.WriteLine($"Erwarteter Fehler: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Editor-Vervollständigung: Klassen-Mitglieder werden über die Typen aufgelöst ===");

{
    const string classes = """
        class Animal {
            string name
            protected int age
            private int secret
            static int Count
            construct(string n) { this.name = n }
            Speak() { return "..." }
        }
        class Dog : Animal {
            Tail tail = new Tail()
            Bark() { return 1 }
            Wag() { return this.tail }
        }
        class Tail {
            int length
            Curl() { }
        }
        enum Color { Red, Green }

        """;

    int completionFailures = 0;
    // '|' in the source = cursor position. `expected`: these names MUST be suggested,
    // `forbidden`: these MUST NOT be (comma-separated).
    void CheckCompletion(string title, string source, string expected, string forbidden = "", bool exact = false)
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        var want = expected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var deny = forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool ok = want.All(names.Contains) && !deny.Any(names.Contains) && (!exact || names.Count == want.Length);
        if (!ok) completionFailures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(10))}");
    }

    CheckCompletion("typisierte Variable", classes + "Dog d = new Dog(\"a\")\nd.|", "Bark,Wag,tail,name,Speak", "Curl,Count,age,secret,Dog");
    CheckCompletion("typisierte Variable ohne Initialisierer", classes + "Dog d\nd.|", "Bark,Wag,tail,name,Speak", "Curl,Count,age,secret,Dog");
    // 'var x : unit' only fixes a unit, the type comes from the initialiser.
    CheckCompletion("var mit Einheit", classes + "var d : mm = new Dog(\"a\")\nd.|", "Bark,Wag", "Curl");
    CheckCompletion("var = new X()", classes + "var d = new Dog(\"a\")\nd.|", "Bark,name,Speak", "Curl,length");
    CheckCompletion("Praefix filtert", classes + "var d = new Dog(\"a\")\nd.Ba|", "Bark", "Speak", exact: true);
    CheckCompletion("var = andere Variable", classes + "var d = new Dog(\"a\")\nvar e = d\ne.|", "Bark", "Curl");
    CheckCompletion("Methodenkette (Rueckgabetyp aus return)", classes + "var d = new Dog(\"a\")\nd.Wag().|", "Curl,length", "Bark", exact: true);
    CheckCompletion("Feldkette", classes + "var d = new Dog(\"a\")\nd.tail.|", "Curl,length", "Bark", exact: true);
    CheckCompletion("new X().", classes + "new Dog(\"a\").|", "Bark", "Curl");
    CheckCompletion("Zuweisung spaeter", classes + "var d = null\nd = new Dog(\"a\")\nd.|", "Bark", "Curl");
    CheckCompletion("typisierter Parameter", classes + "Foo(Dog d) { d.| }", "Bark", "Curl");
    CheckCompletion("Array-Element", classes + "var arr = new Dog[3]\narr[0].|", "Bark", "Curl");
    CheckCompletion("foreach ueber Array", classes + "Dog ds[]\nforeach (var x in ds) { x.| }", "Bark", "Curl");
    CheckCompletion("foreach ohne var (so schreibt es die Sprache)", classes + "Dog ds[]\nforeach (x in ds) { x.| }", "Bark", "Curl");
    CheckCompletion("foreach-Variable wird vorgeschlagen", "var l = new List()\nforeach (zeile in l) { zei| }", "zeile");
    CheckCompletion("Klasse. nur statisch", classes + "Animal.|", "Count", "Speak,name", exact: true);
    CheckCompletion("Enum.", classes + "var c = Color.|", "Red,Green", exact: true);
    CheckCompletion("private/protected ausserhalb versteckt", classes + "var a = new Animal(\"x\")\na.|", "name,Speak", "secret,age,Count");
    CheckCompletion("private innerhalb sichtbar", "class A { private int p\n M() { this.| } }", "p,M");
    CheckCompletion("protected in Ableitung", "class A { protected int p }\nclass B : A { M() { this.| } }", "p,M");
    CheckCompletion("this.feld.", "class A { B b = new B()\n M() { this.b.| } }\nclass B { Z() {} }", "Z", "M", exact: true);
    CheckCompletion("Feld ohne this.", "class A { B b = new B()\n M() { b.| } }\nclass B { Z() {} }", "Z", "M", exact: true);
    CheckCompletion("Feldtyp aus Konstruktor", "class A { b\n construct() { this.b = new B() }\n M() { this.b.| } }\nclass B { Z() {} }", "Z", "M", exact: true);
    CheckCompletion("Aufruf ohne this.", "class A { B mk() { return new B() }\n M() { mk().| } }\nclass B { Z() {} }", "Z", "M", exact: true);
    CheckCompletion("generische Klasse", "class Box<T> where T is of A {\n T item\n Get() { return this.item }\n}\nclass A { Run() {} }\nvar b = new Box<A>()\nb.|", "Get,item", "Run", exact: true);
    CheckCompletion("Interface-Mitglieder", "interface I { Foo() }\nclass A : I { Bar() {} }\nI a\na.|", "Foo", "Bar", exact: true);
    CheckCompletion("Prelude-Klasse", "var l = new List()\nl.|", "Add,GetEnumerator", "Speak");
    CheckCompletion("Erweiterung per #import", "#import \"graphics\"\nvar fb = new Framebuffer(1, 2)\nfb.|", "Width,Height,ReadByte");
    CheckCompletion("#import io: IO. zeigt Streams und Enums", "#import \"io\"\nIO.|", "FileStream,MemoryStream,Stream,IStream,FileMode,SeekOrigin,IOException");
    CheckCompletion("#import io: Stream-Mitglieder", "#import \"io\"\nvar s = new IO.FileStream(\"a.bin\")\ns.|", "ReadBytes,ReadAll,CopyTo,Position,Length,Close,Seek,Name", "ToBuffer,Throw");
    CheckCompletion("#import io: IO.FileMode.", "#import \"io\"\nvar m = IO.FileMode.|", "Open,Create,CreateNew,OpenOrCreate,Append", exact: true);
    CheckCompletion("nach 'new' nur Klassen (keine Interfaces)", "interface I { Foo() }\nclass A { }\nvar x = new |", "A", "I,var");
    CheckCompletion("bool hat keine Mitglieder", "var b = true\nb.|", "", exact: true);
    CheckCompletion("Variable aus fremder Methode nicht sichtbar -> Fallback", "class A { M() { var q = new B() }\n N() { q.| } }\nclass B { Z() {} }", "Z");
    CheckCompletion("unbestimmbar -> Fallback auf alle Klassen", classes + "var d = something()\nd.|", "Bark,Curl");
    CheckCompletion("Zyklus haengt nicht", "var a = b\nvar b = a\na.|", "");
    // ---- Namespaces and their members ----
    const string geo = """
        namespace Geometry {
            class Shape { string label
                Describe() { return "s" } }
            class Circle : Shape {
                int radius
                static Circle Unit() { return new Circle() }
                Area() { return 1 }
            }
            interface IDrawable { Draw() }
            enum Kind { Round, Flat }
            namespace Inner {
                class Deep { Go() { } }
            }
        }
        class Other { Z() { } }

        """;
    CheckCompletion("Namespace. zeigt Klassen, Enums, Unter-Namespaces", geo + "Geometry.|", "Shape,Circle,IDrawable,Kind,Inner", "Deep,Other,Area", exact: true);
    CheckCompletion("Namespace. Praefix", geo + "Geometry.Ci|", "Circle", "Shape", exact: true);
    CheckCompletion("verschachtelter Namespace.", geo + "Geometry.Inner.|", "Deep", "Shape", exact: true);
    CheckCompletion("Namespace.Klasse. -> statische Mitglieder", geo + "Geometry.Circle.|", "Unit", "Area,radius", exact: true);
    CheckCompletion("Namespace.Enum.", geo + "Geometry.Kind.|", "Round,Flat", exact: true);
    CheckCompletion("new Namespace.", geo + "var c = new Geometry.|", "Shape,Circle,Inner", "IDrawable,Kind", exact: true);
    CheckCompletion("new Namespace.Klasse(...).", geo + "new Geometry.Circle().|", "Area,radius,Describe,label", "Unit,Z");
    CheckCompletion("var = new Namespace.Klasse()", geo + "var c = new Geometry.Circle()\nc.|", "Area,radius,Describe,label", "Unit,Z");
    CheckCompletion("Namespace-Typ als Deklaration", geo + "Geometry.Circle c\nc.|", "Area,radius", "Unit,Z");
    CheckCompletion("Ergebnis einer statischen Methode aus Namespace", geo + "var c = Geometry.Circle.Unit()\nc.|", "Area,radius", "Unit");
    CheckCompletion("Namespace auf oberster Ebene angeboten", geo + "Geo|", "Geometry", "Other,Circle");
    CheckCompletion("Klasse ausserhalb des Namespaces nicht unqualifiziert", geo + "Cir|", "", "Circle,Shape");
    CheckCompletion("Klasse ohne Namespace unqualifiziert", geo + "Ot|", "Other", "Circle");
    CheckCompletion("new: Klasse nur qualifiziert erreichbar", geo + "var x = new Ci|", "", "Circle");
    CheckCompletion("Kontext: innerhalb des Namespaces einfach ansprechbar", geo + "namespace Geometry { class Extra { M() { var c = new Ci| } } }", "Circle", "Other");
    CheckCompletion("#using macht Klassen unqualifiziert ansprechbar", "#using Geometry\n" + geo + "var c = new Ci|", "Circle", "Deep");
    CheckCompletion("#using: var = new Klasse()", "#using Geometry\n" + geo + "var c = new Circle()\nc.|", "Area,radius", "Unit");
    CheckCompletion("#using: Namespaces vorschlagen", "namespace Geometry { namespace Inner { } }\n#using Ge|", "Geometry", "Inner", exact: true);
    CheckCompletion("#using: Unter-Namespaces", "namespace Geometry { namespace Inner { } }\n#using Geometry.|", "Inner", "Geometry", exact: true);
    CheckCompletion("Basisklasse aus Namespace geerbt", geo + "var c = new Geometry.Circle()\nc.|", "label,Describe");
    CheckCompletion("Feldtyp aus Namespace", "namespace N { class A { B b = new B()\n M() { this.b.| } }\n class B { Z() { } } }", "Z", "M", exact: true);
    CheckCompletion("Feldtyp qualifiziert", "namespace N { class B { Z() { } } }\nclass A { N.B b\n M() { this.b.| } }", "Z", "M", exact: true);
    CheckCompletion("gleichnamige Klassen in zwei Namespaces", "namespace P { class Same { OnlyP() { } } }\nnamespace Q { class Same { OnlyQ() { } } }\nvar a = new P.Same()\na.|", "OnlyP", "OnlyQ", exact: true);
    CheckCompletion("class extends im Namespace", "namespace N { class A { X() { } } }\nnamespace N { class extends A { Y() { this.| } } }", "X,Y");
    Console.WriteLine(completionFailures == 0 ? "Alle Vervollstaendigungs-Pruefungen bestanden." : $"FEHLER: {completionFailures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== IO: Streams (FileStream, MemoryStream, eigene Streams) und Host-Richtlinie ===");

{
    string ioDir = Path.Combine(Path.GetTempPath(), "fire-io-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(ioDir);
    string ioFile = Path.Combine(ioDir, "a.bin").Replace("\\", "/");
    string ioMissing = Path.Combine(ioDir, "missing", "x.bin").Replace("\\", "/");
    string ioDirFwd = ioDir.Replace("\\", "/");
    int ioFailures = 0;

    // Runs `script` with prelude + IO prelude and returns all `print` lines.
    List<string> RunIo(string script, fire.IO.Bridge.IoPolicy? policy = null, fire.IO.Bridge.IoStdio? stdio = null)
    {
        var lines = new List<string>();
        var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new[] { fire.Standard.Prelude.Source, IoPreludeSource(), script }
            .Select(s => Preprocessor.Process(s, Directory.GetCurrentDirectory(), alreadyIncluded)).ToList();
        var program = Parser.ParseMultiple(sources);
        var natives = new NativeRegistry();
        natives.Register("print", args => { lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        using var ioHost = UseIoPackage(natives, policy, stdio);
        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes);
        vm.Run();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckIo(string title, string script, string[] expected, fire.IO.Bridge.IoPolicy? policy = null, fire.IO.Bridge.IoStdio? stdio = null)
    {
        string[] actual;
        try { actual = RunIo(script, policy, stdio).ToArray(); }
        catch (Exception ex) { actual = new[] { "AUSNAHME: " + ex.Message }; }
        bool ok = actual.SequenceEqual(expected);
        if (!ok) ioFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
    }

    CheckIo("FileStream: schreiben, lesen, positionieren", $$"""
        {
            var w = new IO.FileStream("{{ioFile}}", IO.FileMode.Create)
            var data = new byte[5]
            for (var i = 0; i < 5; i++) { data[i] = 65 + i }
            print(w.Write(data))
            w.WriteByte(70)
            print("len " + w.Length + " pos " + w.Position)
            w.Close()
            print("closed " + w.IsClosed)
        }
        var r = new IO.FileStream("{{ioFile}}")
        print("read " + r.CanRead + " write " + r.CanWrite + " seek " + r.CanSeek)
        print(r.ReadBytes(3).ToString())
        print(r.ReadByte())
        r.Seek(-1, IO.SeekOrigin.End)
        print(r.ReadByte())
        print(r.ReadByte())
        r.Position = 0
        var all = r.ReadAll()
        print(all.ToString() + " " + all.length)
        r.Close()
        """, new[] { "5", "len 6 pos 6", "closed True", "read True write False seek True", "ABC", "68", "70", "-1", "ABCDEF 6" });

    CheckIo("FileStream: Append und ReadWrite", $$"""
        var a = new IO.FileStream("{{ioFile}}", IO.FileMode.Append)
        a.Write("GH".ToBytes())
        a.Close()
        var rw = new IO.FileStream("{{ioFile}}", IO.FileMode.Open, IO.FileAccess.ReadWrite)
        print(rw.Length)
        rw.Position = 1
        rw.WriteByte(90)
        rw.Position = 0
        print(rw.ReadAll().ToString())
        rw.Length = 3
        print(rw.Length)
        rw.Close()
        """, new[] { "8", "AZCDEFGH", "3" });

    CheckIo("Fehler sind fangbare Exceptions", $$"""
        try { var x = new IO.FileStream("{{ioMissing}}") } catch (IO.DirectoryNotFoundException e) { print("DNF " + e.code) }
        try { var x = new IO.FileStream("{{ioDirFwd}}/nope.txt") } catch (IO.FileNotFoundException e) { print("FNF " + e.code) }
        try { var x = new IO.FileStream("{{ioFile}}", IO.FileMode.CreateNew) } catch (IO.FileExistsException e) { print("EXISTS " + e.code) }
        var s = new IO.FileStream("{{ioFile}}")
        try { s.Write(new byte[2]) } catch (IO.IOException e) { print("IOE " + e.code) }
        s.Close()
        s.Close()
        try { s.ReadByte() } catch (IO.StreamClosedException e) { print("CLOSED " + e.code) }
        try { var m = new IO.MemoryStream(); m.Read(new byte[2], 1, 5) } catch (IO.IOException e) { print("RANGE " + e.code) }
        try { var m = new IO.MemoryStream(); m.Seek(-1) } catch (IO.IOException e) { print("SEEK " + e.code) }
        """, new[] { "DNF 4", "FNF 3", "EXISTS 7", "IOE 8", "CLOSED 2", "RANGE 1", "SEEK 1" });

    CheckIo("MemoryStream und CopyTo", """
        var m = new IO.MemoryStream()
        m.Write("Hallo Welt".ToBytes())
        print("len " + m.Length + " pos " + m.Position)
        m.Position = 0
        var c = new IO.MemoryStream()
        m.CopyTo(c)
        print(c.ToBuffer().ToString())
        var d = new IO.MemoryStream("abc".ToBytes())
        print(d.ReadByte() + " " + d.ReadByte() + " " + d.ReadByte() + " " + d.ReadByte())
        d.Length = 1
        print(d.ToBuffer().length)
        """, new[] { "len 10 pos 10", "Hallo Welt", "97 98 99 -1", "1" });

    CheckIo("destruct() schliesst den Stream, wenn der Besitzer endet", $$"""
        print("offen " + __IOOpenCount())
        {
            var s = new IO.FileStream("{{ioFile}}")
            print("offen " + __IOOpenCount())
        }
        print("offen " + __IOOpenCount())
        try { var bad = new IO.FileStream("{{ioDirFwd}}/nope.txt") } catch (IO.FileNotFoundException e) { print("fehlgeschlagen") }
        print("offen " + __IOOpenCount())
        """, new[] { "offen 0", "offen 1", "offen 0", "fehlgeschlagen", "offen 0" });

    CheckIo("eigener Stream (Basisklasse IO.Stream)", """
        class Upper : IO.Stream {
            var inner
            construct(inner) { this.inner = inner }
            bool CanWrite { get { return true } }
            int Write(buffer, offset, count) {
                var chunk = new byte[count]
                for (var i = 0; i < count; i++) {
                    var c = buffer[offset + i]
                    if (c >= 97 && c <= 122) { c = c - 32 }
                    chunk[i] = c
                }
                return this.inner.Write(chunk, 0, count)
            }
        }
        var m = new IO.MemoryStream()
        var u = new Upper(m)
        u.Write("hallo".ToBytes())
        print(m.ToBuffer().ToString())
        """, new[] { "HALLO" });

    CheckIo("Host-Richtlinie: DenyAll", $$"""
        try { var x = new IO.FileStream("{{ioFile}}") } catch (IO.PermissionException e) { print("verweigert " + e.code) }
        var m = new IO.MemoryStream()
        print("Speicher-Streams gehen trotzdem " + m.CanWrite)
        """, new[] { "verweigert 6", "Speicher-Streams gehen trotzdem True" }, fire.IO.Bridge.IoPolicy.DenyAll);

    CheckIo("Host-Richtlinie: nur lesen innerhalb eines Verzeichnisses", $$"""
        var ok = new IO.FileStream("{{ioFile}}")
        print("lesen ok")
        try { var x = new IO.FileStream("{{ioFile}}", IO.FileMode.Open, IO.FileAccess.ReadWrite) } catch (IO.PermissionException e) { print("schreiben " + e.code) }
        try { var x = new IO.FileStream("{{ioDirFwd}}/../outside.txt") } catch (IO.PermissionException e) { print("ausserhalb " + e.code) }
        """, new[] { "lesen ok", "schreiben 6", "ausserhalb 6" }, fire.IO.Bridge.IoPolicy.Rooted(ioDir, readOnly: true));

    // ---- Step 2: file and directory API ----
    string apiDir = Path.Combine(ioDir, "api").Replace("\\", "/");
    Directory.CreateDirectory(apiDir);

    CheckIo("File: Text (UTF-8), Zeilen, Groesse, Zeit", $$"""
        var d = "{{apiDir}}"
        var f = IO.Path.Combine(d, "t.txt")
        print(IO.File.Exists(f))
        IO.File.WriteAllText(f, "Grüße\nzweite Zeile\r\ndritte")
        print(IO.File.Exists(f) + " " + IO.File.Size(f))
        print(IO.File.ReadAllText(f))
        var lines = IO.File.ReadAllLines(f)
        print(lines.count + " " + lines[0] + "|" + lines[1] + "|" + lines[2])
        IO.File.AppendAllText(f, "\nvierte\n")
        print(IO.File.ReadAllLines(f).count)
        IO.File.WriteAllLines(f, lines)
        print(IO.File.Size(f))
        print(IO.File.ModifiedTime(f) > 1000000000s)
        print(IO.File.ReadAllBytes(f).length)
        IO.File.WriteAllBytes(f, "AB".ToBytes())
        print(IO.File.ReadAllText(f))
        """, new[] { "False", "True 28", "Grüße\nzweite Zeile\r\ndritte", "3 Grüße|zweite Zeile|dritte", "4", "28", "True", "28", "AB" });

    CheckIo("File: Copy, Move, Delete und ihre Fehler", $$"""
        var d = "{{apiDir}}"
        var f = IO.Path.Combine(d, "src.txt")
        IO.File.WriteAllText(f, "x")
        var c = IO.Path.Combine(d, "copy.txt")
        IO.File.Copy(f, c)
        try { IO.File.Copy(f, c) } catch (IO.FileExistsException e) { print("Ziel existiert " + e.code) }
        IO.File.Copy(f, c, true)
        var m = IO.Path.Combine(d, "moved.txt")
        IO.File.Move(c, m)
        print(IO.File.Exists(c) + " " + IO.File.Exists(m))
        IO.File.Delete(m)
        IO.File.Delete(m)
        print(IO.File.Exists(m))
        try { IO.File.ReadAllText(IO.Path.Combine(d, "nope")) } catch (IO.FileNotFoundException e) { print("fehlt " + e.code) }
        try { IO.File.Size(IO.Path.Combine(d, "nope")) } catch (IO.FileNotFoundException e) { print("fehlt " + e.code) }
        """, new[] { "Ziel existiert 7", "False True", "False", "fehlt 3", "fehlt 3" });

    CheckIo("ReadAllLines/GetFiles liefern eine List (foreach)", $$"""
        var d = "{{apiDir}}"
        IO.File.WriteAllLines(IO.Path.Combine(d, "l.txt"), ["eins", "zwei"])
        foreach (line in IO.File.ReadAllLines(IO.Path.Combine(d, "l.txt"))) { print(line) }
        foreach (name in IO.Directory.GetFiles(d, "l*")) { print(IO.Path.FileName(name)) }
        """, new[] { "eins", "zwei", "l.txt" });

    CheckIo("Directory und Path", $$"""
        var d = "{{apiDir}}"
        var sub = IO.Path.Combine(d, "a", "b")
        print(IO.Directory.Exists(sub))
        IO.Directory.Create(sub)
        IO.Directory.Create(sub)
        print(IO.Directory.Exists(sub))
        IO.File.WriteAllText(IO.Path.Combine(sub, "x.txt"), "1")
        IO.File.WriteAllText(IO.Path.Combine(sub, "y.log"), "2")
        IO.File.WriteAllText(IO.Path.Combine(d, "a", "z.txt"), "3")
        var files = IO.Directory.GetFiles(sub)
        print(files.count + " " + IO.Path.FileName(files[0]) + " " + IO.Path.FileName(files[1]))
        print(IO.Directory.GetFiles(sub, "*.txt").count)
        print(IO.Directory.GetFiles(IO.Path.Combine(d, "a"), "*.txt", true).count)
        print(IO.Path.FileName(IO.Directory.GetDirectories(IO.Path.Combine(d, "a"))[0]))
        try { IO.Directory.Delete(IO.Path.Combine(d, "a")) } catch (IO.IOException e) { print("nicht leer") }
        IO.Directory.Delete(IO.Path.Combine(d, "a"), true)
        print(IO.Directory.Exists(IO.Path.Combine(d, "a")))
        var p = "dir/sub/name.tar.gz"
        print(IO.Path.FileName(p) + " " + IO.Path.Stem(p) + " " + IO.Path.Extension(p) + " " + IO.Path.Parent(p))
        print(IO.Path.IsRooted(p) + " " + IO.Path.IsRooted(IO.Path.FullPath(p)))
        print(IO.Path.Combine("a", "/abs") == "/abs" || IO.Path.Combine("a", "/abs") == "/abs")
        try { IO.Directory.GetFiles(IO.Path.Combine(d, "gibtsnicht")) } catch (IO.DirectoryNotFoundException e) { print("kein Verzeichnis " + e.code) }
        """, new[] { "False", "True", "2 x.txt y.log", "1", "2", "b", "nicht leer", "False", "name.tar.gz name.tar .gz dir/sub", "False True", "True", "kein Verzeichnis 4" });

    CheckIo("Host-Richtlinie gilt auch fuer die API (nur lesen)", $$"""
        var d = "{{apiDir}}"
        var f = IO.Path.Combine(d, "src.txt")
        print(IO.File.Exists(f))
        try { IO.File.WriteAllText(IO.Path.Combine(d, "w.txt"), "x") } catch (IO.PermissionException e) { print("write " + e.code) }
        try { IO.File.Delete(f) } catch (IO.PermissionException e) { print("delete " + e.code) }
        try { IO.Directory.Create(IO.Path.Combine(d, "n")) } catch (IO.PermissionException e) { print("mkdir " + e.code) }
        try { IO.Directory.GetFiles("/") } catch (IO.PermissionException e) { print("list " + e.code) }
        try { IO.File.Copy(f, IO.Path.Combine(d, "k.txt")) } catch (IO.PermissionException e) { print("copy " + e.code) }
        try { IO.File.Move(f, IO.Path.Combine(d, "k.txt")) } catch (IO.PermissionException e) { print("move " + e.code) }
        print(IO.File.ReadAllText(f))
        """, new[] { "True", "write 6", "delete 6", "mkdir 6", "list 6", "copy 6", "move 6", "x" }, fire.IO.Bridge.IoPolicy.Rooted(ioDir, readOnly: true));

    // ---- Schritt 3: TextWriter / TextReader ----
    string textDir = Path.Combine(ioDir, "text").Replace("\\", "/");
    Directory.CreateDirectory(textDir);

    CheckIo("TextWriter und TextReader: Zeilen, UTF-8, EndOfStream", $$"""
        var f = "{{textDir}}/t.txt"
        var w = new IO.TextWriter(f)
        w.WriteLine("Grüße")
        w.Write("zwei")
        w.Write(" ")
        w.Write(3)
        w.WriteLine()
        w.WriteLine("")
        w.Write("ohne Ende")
        w.Close()
        print(IO.File.Size(f))
        var r = new IO.TextReader(f)
        var l = r.ReadLine()
        while (l != undefined) { print("[" + l + "]"); l = r.ReadLine() }
        print(r.EndOfStream)
        r.Close()
        """, new[] { "25", "[Grüße]", "[zwei 3]", "[]", "[ohne Ende]", "True" });

    CheckIo("TextReader: foreach, ReadAll, ReadLines, Anhaengen, OpenText", $$"""
        var f = "{{textDir}}/t.txt"
        foreach (zeile in new IO.TextReader(f)) { print("f:" + zeile) }
        var a = new IO.TextWriter(f, true)
        a.WriteLine("!")
        a.Close()
        var rd = IO.File.OpenText(f)
        print(rd.ReadAll())
        rd.Close()
        print(new IO.TextReader(f).ReadLines().count)
        var cw = IO.File.CreateText(f)
        cw.WriteLine("neu")
        cw.Close()
        var ap = IO.File.AppendText(f)
        ap.WriteLine("mehr")
        ap.Close()
        print(IO.File.ReadAllLines(f).count)
        """, new[] { "f:Grüße", "f:zwei 3", "f:", "f:ohne Ende", "Grüße\nzwei 3\n\nohne Ende!\n", "4", "2" });

    CheckIo("TextReader: CRLF, leere Zeilen, fremder Stream, lange Eingabe", """
        var m = new IO.MemoryStream("a\r\nb\n\nlast".ToBytes())
        var r = new IO.TextReader(m, true)
        print("[" + r.ReadLine() + "][" + r.ReadLine() + "][" + r.ReadLine() + "][" + r.ReadLine() + "][" + r.ReadLine() + "]")
        r.Close()
        print("Stream bleibt offen: " + !m.IsClosed)
        var big = new IO.MemoryStream()
        var tw = new IO.TextWriter(big, true)
        for (var i = 0; i < 2000; i++) { tw.WriteLine("Zeile " + i + " äöü") }
        tw.Flush()
        big.Position = 0
        var br = new IO.TextReader(big)
        var n = 0
        var last = ""
        foreach (z in br) { n = n + 1; last = z }
        print(n + " " + last)
        br.Close()
        print("Stream mitgeschlossen: " + big.IsClosed)
        """, new[] { "[a][b][][last][undefined]", "Stream bleibt offen: True", "2000 Zeile 1999 äöü", "Stream mitgeschlossen: True" });

    CheckIo("TextReader/TextWriter: destruct() schliesst, Fehler bleiben Exceptions", $$"""
        {
            var r = new IO.TextReader("{{textDir}}/t.txt")
            var w = new IO.TextWriter("{{textDir}}/d.txt")
            print("offen " + __IOOpenCount())
        }
        print("offen " + __IOOpenCount())
        try { var x = new IO.TextReader("{{textDir}}/gibtsnicht.txt") } catch (IO.FileNotFoundException e) { print("fehlt") }
        try { var w = new IO.TextWriter("{{textDir}}/x.txt"); w.Close(); w.WriteLine("z") } catch (IO.StreamClosedException e) { print("geschlossen") }
        print("offen " + __IOOpenCount())
        """, new[] { "offen 3", "offen 0", "fehlt", "geschlossen", "offen 0" });

    // ---- Schritt 4: Stdio ----
    var stdoutLines = new List<string>();
    var stderrLines = new List<string>();
    var stdinData = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Anna\nBärbel\nrest1\nrest2"));
    CheckIo("IO.Stdio: Ausgabe, Fehler, Eingabe, Streams", """
        IO.Stdio.WriteLine("Hallo Welt")
        IO.Stdio.Write("teil")
        IO.Stdio.Write(1)
        IO.Stdio.WriteLine(" ende")
        IO.Stdio.Write("ohne Umbruch")
        IO.Stdio.Flush()
        IO.Stdio.ErrorLine("Fehler ä")
        print("gelesen: " + IO.Stdio.ReadLine())
        var w = new IO.TextWriter(IO.Stdio.Out(), true)
        w.WriteLine("via TextWriter äöü")
        w.Flush()
        print(IO.Stdio.ReadLine())
        print(IO.Stdio.ReadAll())
        print(IO.Stdio.ReadLine())
        IO.Stdio.Out().Close()
        IO.Stdio.WriteLine("nach Close")
        print("offen: " + __IOOpenCount())
        """, new[] { "gelesen: Anna", "Bärbel", "rest1\nrest2", "undefined", "offen: 0" }, null,
        fire.IO.Bridge.IoStdio.Custom(l => stdoutLines.Add(l), l => stderrLines.Add(l), stdinData));
    Console.WriteLine(stdoutLines.SequenceEqual(new[] { "Hallo Welt", "teil1 ende", "ohne Umbruch", "via TextWriter äöü", "nach Close" })
        && stderrLines.SequenceEqual(new[] { "Fehler ä" })
        ? "OK: Stdio-Ausgabe/-Fehler landen beim Host (zeilenweise)"
        : $"FEHLER: Stdio-Ausgabe: {string.Join(" | ", stdoutLines)} / Fehler: {string.Join(" | ", stderrLines)}");
    if (!(stdoutLines.Count == 5 && stderrLines.Count == 1)) ioFailures++;

    Directory.Delete(ioDir, true);
    Console.WriteLine(ioFailures == 0 ? "Alle IO-Pruefungen bestanden." : $"FEHLER: {ioFailures} IO-Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Destruktoren: die ganze Klassenkette wird aufgeraeumt (abgeleitet zuerst) ===");

string destructChainSample = """
class Base {
    destruct() { print("Base.destruct") }
}
class Middle : Base { }
class Leaf : Middle {
    destruct() { print("Leaf.destruct") }
}
{
    var a = new Leaf()
    var b = new Middle()
}
print("danach")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(destructChainSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("Ausgabe (erwartet: Leaf.destruct / Base.destruct / Base.destruct - je Objekt, Reihenfolge der Objekte egal - dann danach):");
    var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes);
    vm.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Enums in Namespaces: vollqualifizierter Zugriff ===");

string namespacedEnumSample = """
namespace Geo {
    enum Kind { Round, Flat = 5, Sharp }
}
print(Geo.Kind.Round)
print(Geo.Kind.Flat)
print(Geo.Kind.Sharp)
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(namespacedEnumSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("Ausgabe (erwartet: 0 / 5 / 6):");
    new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes).Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Felder eines nicht fertig konstruierten Objekts sind undefined (nicht false) ===");

string halfConstructedSample = """
class Base {
    int handle
    bool closed
    construct(int h) { this.handle = h; this.closed = false }
    destruct() { print("destruct: handle=" + this.handle + " undefined=" + (this.handle == undefined)) }
}
class Boom : Exception { }
class Child : Base {
    construct(int m) : base(Fail(m)) { }
    static int Fail(int m) { throw new Boom() }
}
try { var x = new Child(1) } catch (Boom e) { print("gefangen") }
print("ende")
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.Parse(halfConstructedSample);
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("Ausgabe (erwartet: gefangen / destruct: handle=undefined undefined=True / ende):");
    new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes).Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== foreach ueber Arrays und Puffer ===");

string foreachArraySample = """
var arr = [10, 20, 30]
var sum = 0
foreach (x in arr) { sum = sum + x }
print(sum)
foreach (b in "AB".ToBytes()) { print(b) }
var names = new string[2]
names[0] = "a"
names[1] = "b"
foreach (n in names) { print(n) }
foreach (x in arr) { if (x == 20) { break } print("v" + x) }
foreach (e in new int[0]) { print("nie") }
print("ok")
""";

try
{
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, foreachArraySample));
    var natives = NativeRegistry.CreateDefault();
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    Console.WriteLine("Ausgabe (erwartet: 60 / 65 / 66 / a / b / v10 / ok):");
    new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes).Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Strings: Length, IndexOf, LastIndexOf, Substring & Co. ===");

string stringSample = """
int failures = 0
var Check = func (name, actual, expected) => {
    if (actual == expected) { print("OK: " + name) }
    else { failures = failures + 1; print("FEHLER: " + name + " -> " + actual + " (erwartet " + expected + ")") }
}
string s = "Hello, World, again"
Check("Length", s.Length, 19)
Check("length (Alias)", s.length, 19)
Check("Length leer", "".Length, 0)
Check("IndexOf", s.IndexOf("o"), 4)
Check("IndexOf ab Position", s.IndexOf("o", 5), 8)
Check("IndexOf nicht gefunden", s.IndexOf("xyz"), -1)
Check("IndexOf Zeichen", s.IndexOf('W'), 7)
Check("LastIndexOf", s.LastIndexOf(","), 12)
Check("LastIndexOf ab Position", s.LastIndexOf(",", 11), 5)
Check("LastIndexOf nicht gefunden", s.LastIndexOf("xyz"), -1)
Check("Substring(start)", s.Substring(14), "again")
Check("Substring(start, count)", s.Substring(7, 5), "World")
Check("Substring(Ende)", s.Substring(19), "")
Check("Indexer", s[1], 'e')
Check("CharAt", s.CharAt(0), 'H')
Check("Contains", s.Contains("World"), true)
Check("StartsWith", s.StartsWith("Hello"), true)
Check("EndsWith", s.EndsWith("Hello"), false)
Check("ToUpper", "abc".ToUpper(), "ABC")
Check("ToLower", "ABC".ToLower(), "abc")
Check("Trim", "  x  ".Trim(), "x")
Check("TrimStart", "  x  ".TrimStart(), "x  ")
Check("TrimEnd", "  x  ".TrimEnd(), "  x")
Check("Replace", "a-b-c".Replace("-", "+"), "a+b+c")
Check("PadLeft", "7".PadLeft(3, '0'), "007")
Check("PadRight", "7".PadRight(3), "7  ")
Check("Kette", "  a,b,c ".Trim().Split(",").Length, 3)
var parts = "a,b,c".Split(",")
var joined = ""
foreach (p in parts) { joined = joined + p.ToUpper() }
Check("Split + foreach", joined, "ABC")
Check("Array.Length", [1, 2, 3].Length, 3)
Check("Puffer.Length", "AB".ToBytes().Length, 2)

int caught = 0
try { s.Substring(30) } catch (IndexOutOfBoundsException e) { caught = caught + 1 }
try { s.Substring(5, 100) } catch (IndexOutOfBoundsException e) { caught = caught + 1 }
try { var c = s[99] } catch (IndexOutOfBoundsException e) { caught = caught + 1 }
try { s.LastIndexOf("a", 50) } catch (IndexOutOfBoundsException e) { caught = caught + 1 }
Check("Index-Ausnahmen", caught, 4)
if (failures == 0) { print("Alle String-Pruefungen bestanden.") } else { print("FEHLER: " + failures) }
""";

try
{
    var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, stringSample));
    var natives = NativeRegistry.CreateDefault();
    var resolveResult = Resolver.Resolve(program, natives.Names);
    var compiled = Compiler.Compile(program, resolveResult, natives);
    new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes).Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Strings: Editor-Vervollstaendigung ===");
{
    int failures = 0;
    void Check(string title, string source, string expected, string forbidden = "")
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        bool ok = expected.Split(',').All(names.Contains) && !forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(8))}");
    }
    Check("string-Variable", "string s = \"abc\"\ns.|", "Length,IndexOf,LastIndexOf,Substring", "Bark,length");
    Check("var s = Literal", "var s = \"abc\"\ns.|", "Length,IndexOf,Substring");
    Check("Literal direkt", "\"abc\".|", "Length,Trim");
    Check("Praefix", "var s = \"abc\"\ns.Sub|", "Substring", "Length");
    Check("Kette Trim().", "var s = \"abc\"\ns.Trim().|", "Length,ToUpper");
    Check("Split() liefert Array", "var s = \"a,b\"\ns.Split(\",\").|", "Length", "Substring");
    Check("Array-Element ist string", "var s = \"a,b\"\nvar p = s.Split(\",\")\np[0].|", "Substring");
    Check("foreach ueber Split", "var s = \"a,b\"\nforeach (p in s.Split(\",\")) { p.| }", "Substring");
    Check("Length ist int", "var s = \"abc\"\nvar n = s.Length\nn.|", "ToChar");
    Console.WriteLine(failures == 0 ? "Alle String-Vervollstaendigungs-Pruefungen bestanden." : $"FEHLER: {failures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Basistyp-Erweiterungen (class extends string/char/int/...) ===");
{
    int extFailures = 0;

    List<string> RunExt(string script)
    {
        var lines = new List<string>();
        var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, script));
        var natives = new NativeRegistry();
        natives.Register("print", args => { lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes);
        vm.Run();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckExt(string title, string script, params string[] expected)
    {
        string[] actual;
        try { actual = RunExt(script).ToArray(); }
        catch (Exception ex) { actual = new[] { "AUSNAHME: " + ex.Message }; }
        bool ok = actual.SequenceEqual(expected);
        if (!ok) extFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
    }

    // The error text must contain `fragment` (parser/resolver/runtime error).
    void CheckExtError(string title, string script, string fragment)
    {
        string actual;
        try { actual = "kein Fehler: " + string.Join(" | ", RunExt(script)); }
        catch (Exception ex) { actual = ex.Message; }
        bool ok = actual.Contains(fragment);
        if (!ok) extFailures++;
        Console.WriteLine(ok ? $"OK: {title} -> {actual}" : $"FEHLER: {title}\n  erwartet: ...{fragment}...\n  erhalten: {actual}");
    }

    CheckExt("eigene Methoden auf string (this ist der Wert)", """
        class extends string {
            string Shout() { return this.ToUpper() + "!" }
            bool IsBlank() { return this.Trim().Length == 0 }
            string Twice() { return this + this }
        }
        var s = "hallo"
        print(s.Shout())
        print("abc".Twice())
        print("  ".IsBlank())
        print(s.Shout().Shout())
        print(s)
        """, "HALLO!", "abcabc", "True", "HALLO!!", "hallo");

    CheckExt("Ueberladung nach Parameteranzahl und optionale Parameter", """
        class extends string {
            string Wrap() { return "[" + this + "]" }
            string Wrap(string edge) { return edge + this + edge }
            string Tag(string name = "b") { return "<" + name + ">" + this + "</" + name + ">" }
        }
        print("x".Wrap())
        print("x".Wrap("*"))
        print("x".Tag())
        print("x".Tag("i"))
        """, "[x]", "*x*", "<b>x</b>", "<i>x</i>");

    CheckExt("int, char, bool und float erweitern", """
        class extends int {
            bool IsEven() { return this % 2 == 0 }
            int Twice() { return this * 2 }
        }
        class extends char { bool IsVowel() { return "aeiou".Contains(this.ToLower()) } }
        class extends bool { string Word() { if (this) { return "ja" } return "nein" } }
        class extends float { float Half() { return this / 2.0 } }
        int n = 21
        print(n.IsEven())
        print((n + 1).IsEven())
        print(n.Twice())
        print('E'.IsVowel())
        print('x'.IsVowel())
        print((n > 5).Word())
        float f = 5.0
        print(f.Half())
        """, "False", "True", "42", "True", "False", "ja", "2.5");

    CheckExt("mehrere Bloecke fuer denselben Typ und Namespaces werden zusammengefuehrt", """
        class extends string { string A() { return "a" + this } }
        namespace Util {
            class extends string { string B() { return this + "b" } }
        }
        class extends string { string C() { return this.A().B() } }
        print("x".C())
        """, "axb");

    CheckExt("private Hilfsmethode der Erweiterung", """
        class extends string {
            private string Quote() { return "'" + this + "'" }
            string Quoted() { return this.Quote() }
        }
        print("q".Quoted())
        try { print("q".Quote()) } catch (AccessDeniedException e) { print("verweigert") }
        """, "'q'", "verweigert");

    CheckExt("Ausnahme in der Erweiterung ist fangbar", """
        class extends string {
            string First() { return this.Substring(0, 1) }
        }
        print("abc".First())
        try { print("".First()) } catch (IndexOutOfBoundsException e) { print("leer " + e.index) }
        """, "a", "leer 1");

    // An extension of string only applies to string - an int does not know `Foo` (not a script error, but a VM error).
    CheckExt("Erweiterung gilt nur fuer ihren Typ", """
        class extends string { int Foo() { return 1 } }
        print("s".Foo())
        int i = 5
        print(i.Foo())
        """, "AUSNAHME: 'Foo' (0 argument(s)) is not a known built-in method on a value of type Int.");

    CheckExt("char-Methoden des Prelude", """
        char c = 'a'
        print(c.IsLetter())
        print(c.IsDigit())
        print('7'.IsDigit())
        print(' '.IsWhiteSpace())
        print(c.IsLetterOrDigit())
        print(c.IsUpper())
        print(c.ToUpper())
        print('Q'.ToLower())
        print('Q'.IsUpper())
        print('q'.IsLower())
        print(c.ToInt())
        print(c.ToString() + "b")
        print(c.ToByte())
        """, "True", "False", "True", "True", "True", "False", "A", "q", "True", "True", "97", "ab", "97");

    // The method is chosen via its ID, not via the name: the native function can be called directly.
    CheckExt("native Funktionen nehmen die Methoden-ID", $$"""
        print({{fire.Standard.StringMethods.NativeName}}({{(int)fire.Standard.StringMethod.Substring}}, "hello", 1, 3))
        print({{fire.Standard.StringMethods.NativeName}}({{(int)fire.Standard.StringMethod.ToUpper}}, "hello"))
        print({{fire.Standard.CharMethods.NativeName}}({{(int)fire.Standard.CharMethod.IsDigit}}, '5'))
        try { print({{fire.Standard.StringMethods.NativeName}}({{(int)fire.Standard.StringMethod.Substring}}, "hello", 9)) }
        catch (IndexOutOfBoundsException e) { print("Index " + e.index + " Laenge " + e.length) }
        """, "ell", "HELLO", "True", "Index 9 Laenge 5");

    CheckExt("native Funktion: falsches Argument wird gemeldet", $$"""
        print({{fire.Standard.StringMethods.NativeName}}(1, 5, "x"))
        """, "AUSNAHME: __StringCall(id, text, ...) expects the string as the second argument.");

    CheckExtError("Feld in Basistyp-Erweiterung", "class extends string { int count }", "may only contain methods");
    CheckExtError("Property in Basistyp-Erweiterung", "class extends string { int Size { get { return 1 } } }", "property 'Size' is not allowed");
    CheckExtError("Auto-Property in Basistyp-Erweiterung", "class extends int { int Size { get; set; } }", "is not allowed");
    CheckExtError("Konstruktor in Basistyp-Erweiterung", "class extends string { construct() { } }", "a constructor is not allowed");
    CheckExtError("Destruktor in Basistyp-Erweiterung", "class extends string { destruct() { } }", "a destructor is not allowed");
    CheckExtError("statische Methode in Basistyp-Erweiterung", "class extends string { static int F() { return 1 } }", "static method 'F'");
    CheckExtError("Operator in Basistyp-Erweiterung", "class extends string { operator+(other) { return this } }", "Operators cannot be overloaded");
    CheckExtError("byte nicht erweiterbar", "class extends byte { int F() { return 1 } }", "'byte' cannot be extended");
    CheckExtError("unbekannte Klasse bleibt ein Fehler", "class extends Gibtsnicht { F() { } }", "is not known");
    CheckExtError("doppelte Methode (Prelude + eigene)", "class extends string { int IndexOf(value) { return 0 } }", "IndexOf");

    Console.WriteLine(extFailures == 0 ? "Alle Basistyp-Erweiterungs-Pruefungen bestanden." : $"FEHLER: {extFailures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Basistyp-Erweiterungen: Editor ===");
{
    int failures = 0;
    void Check(string title, string source, string expected, string forbidden = "")
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        bool ok = expected.Split(',', StringSplitOptions.RemoveEmptyEntries).All(names.Contains)
            && !forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(8))}");
    }
    const string userExt = "class extends string { string Shout() { return this.ToUpper() } }\nclass extends int { bool IsEven() { return true } }\n";
    Check("eigene string-Methode", userExt + "var s = \"a\"\ns.|", "Shout,IndexOf,Length");
    Check("eigene Methode in der Kette", userExt + "var s = \"a\"\ns.Shout().|", "Shout,Trim,Length");
    Check("int-Erweiterung", userExt + "int n = 4\nn.|", "IsEven,ToChar", "Shout");
    Check("char-Methoden aus dem Prelude", "char c = 'x'\nc.|", "IsDigit,IsLetter,ToUpper,ToByte", "Shout");
    Check("Sammelklasse taucht nicht als Typ auf", userExt + "var x = |", "string", "$string,$int");
    Check("Split-Kette liefert string", "var s = \"a,b\"\ns.Split(\",\")[0].|", "Substring,Trim");
    Console.WriteLine(failures == 0 ? "Alle Basistyp-Editor-Pruefungen bestanden." : $"FEHLER: {failures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Array als Rueckgabetyp (int[] Name(), leere Klammern) ===");
{
    int arrFailures = 0;

    List<string> RunArr(string script)
    {
        var lines = new List<string>();
        var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, script));
        var natives = new NativeRegistry();
        natives.Register("print", args => { lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes);
        vm.Run();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckArr(string title, string script, params string[] expected)
    {
        string[] actual;
        try { actual = RunArr(script).ToArray(); }
        catch (Exception ex) { actual = new[] { "AUSNAHME: " + ex.Message }; }
        bool ok = actual.SequenceEqual(expected);
        if (!ok) arrFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
    }

    void CheckArrError(string title, string script, string fragment)
    {
        string actual;
        try { actual = "kein Fehler: " + string.Join(" | ", RunArr(script)); }
        catch (Exception ex) { actual = ex.Message; }
        bool ok = actual.Contains(fragment);
        if (!ok) arrFailures++;
        Console.WriteLine(ok ? $"OK: {title} -> {actual}" : $"FEHLER: {title}\n  erwartet: ...{fragment}...\n  erhalten: {actual}");
    }

    CheckArr("Methoden mit Array-Rueckgabetyp (int, Klasse, mehrdimensional, byte)", """
        class Dog { string name; construct(string n) { this.name = n } }
        class Kennel {
            int[] Numbers() { return [1, 2, 3] }
            Dog[] Dogs() { return [new Dog("Rex"), new Dog("Fido")] }
            string[][] Grid() { return [["a", "b"], ["c"]] }
            byte[] Bytes() { return "AB".ToBytes() }
            int[8] Small() { return 7 }
            static int[] Twice() { return [4, 4] }
        }
        var k = new Kennel()
        print(k.Numbers().Length)
        print(k.Numbers()[2])
        foreach (d in k.Dogs()) { print(d.name) }
        print(k.Grid()[0][1])
        print(k.Bytes().Length)
        print(k.Small())
        print(Kennel.Twice().Length)
        """, "3", "3", "Rex", "Fido", "b", "2", "7", "2");

    CheckArr("Interface und Property mit Array-Typ", """
        interface IHolder { int[] Items() }
        class Holder : IHolder {
            int[] Items() { return [5, 6] }
            string[] Names { get { return ["x", "y", "z"] } }
        }
        var h = new Holder()
        print(h.Items()[1])
        print(new Holder().Names.Length)
        """, "6", "3");

    CheckArr("Prelude: Split liefert string[]", """
        var parts = "a,b,c".Split(",")
        print(parts.Length)
        """, "3");

    CheckArrError("Feld mit int[] Typ", "class A { int[] values }", "after the name");
    CheckArrError("Parameter mit int[] Typ", "class A { F(int[] p) { } }", "after the name");
    CheckArrError("lokale Variable mit int[] Typ", "int[] v = [1, 2]", "after the name");
    CheckArrError("extern mit Array-Rueckgabe", "extern int[] Foo()", "after the name");
    CheckArrError("unbekannte Klasse im Array-Rueckgabetyp", "class A { Gibtsnicht[] F() { return [] } }", "Gibtsnicht");
    CheckArrError("byte[8] bleibt widerspruechlich", "class A { byte[8] F() { return 1 } }", "'byte' already has");

    Console.WriteLine(arrFailures == 0 ? "Alle Array-Rueckgabetyp-Pruefungen bestanden." : $"FEHLER: {arrFailures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Array als Rueckgabetyp: Editor ===");
{
    int failures = 0;
    void Check(string title, string source, string expected, string forbidden = "")
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        bool ok = expected.Split(',', StringSplitOptions.RemoveEmptyEntries).All(names.Contains)
            && !forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(8))}");
    }
    const string kennel = "class Dog { Bark() { } }\nclass Kennel {\n  Dog[] Dogs() { return [new Dog()] }\n  string[] Names() { return [\"a\"] }\n  int[8] Small() { return 1 }\n}\nvar k = new Kennel()\n";
    Check("Element eines Array-Rueckgabetyps", kennel + "k.Dogs()[0].|", "Bark", "Length");
    Check("Array-Rueckgabetyp hat Length", kennel + "k.Names().|", "Length", "Bark");
    Check("Element eines string[]", kennel + "k.Names()[0].|", "Substring,Trim");
    Check("Bitbreite ist kein Array", kennel + "k.Small().|", "ToChar", "Length");
    Console.WriteLine(failures == 0 ? "Alle Array-Editor-Pruefungen bestanden." : $"FEHLER: {failures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== VM-Optimierungen: Value, Stack, Inline-Caches (Regressionsschutz) ===");
{
    int perfFailures = 0;

    List<string> RunPerf(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, script));
        var natives = new NativeRegistry();
        natives.Register("print", args => { lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, executionMode: mode);
        vm.Run();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    // Every script runs in ALL three modes with the same expected result (performance omits the
    // access/bounds checks - the examples here therefore do not trigger them, except where `modes` says so).
    void CheckPerf(string title, string script, string[] expected, VmExecutionMode[]? modes = null)
    {
        foreach (var mode in modes ?? new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            string[] actual;
            try { actual = RunPerf(script, mode).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + ex.Message }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) perfFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    CheckPerf("Besitz: Zuweisung schiebt nach oben (bis in die Funktions-Scope), Aufrufergebnis direkt ins Feld, Rueckgabe direkt in einen Parameter", """
        class Box { int items[]; string name; construct(string n) { this.name = n } destruct() { print("free " + this.name) } }
        class Util {
            static int[] Make(int n) { var a = new int[n]; a[0] = n; return a }
            static Box MakeBox(string n) { return new Box(n) }
            static int Len(int xs[]) { return xs.length }
            static int[] Pass(int xs[]) { return xs }
            static int Probe(int xs[]) { return xs[0] }
            static int Loop() {
                var last = [0]
                for (var i = 1; i <= 3; i = i + 1) {
                    last = Make(i)
                    if (i == 2) { var x = Make(9); last = x }
                }
                return last[0]
            }
            static Box BoxLoop() {
                var b = new Box("b0")
                for (var i = 1; i <= 2; i = i + 1) { b = MakeBox("b" + i) }
                return b
            }
        }
        print(Util.Loop())
        var kept = Util.BoxLoop()
        print(kept.name)
        // loop at top level, variable global
        var g = [0]
        for (var i = 1; i <= 3; i = i + 1) { g = Util.Make(i + 10) }
        print(g[0])
        if (true) { g = Util.Make(42) }
        print(g[0])
        // direct assignment of a call result to a field
        class Holder {
            int data[]
            Box child
            construct() { this.data = Util.Make(5); this.child = Util.MakeBox("child") }
        }
        var h = new Holder()
        print(h.data[0] + " " + h.child.name)
        // return value directly into a parameter: it belongs to the called function
        var survivor = Util.Make(3)
        print(Util.Len(Util.Make(4)))
        print(Util.Probe(Util.Pass(Util.Make(6))))
        try { print(Util.Pass(Util.Make(8))[0]) } catch (e) { print("died with the callee") }
        delete h
        print("end")
        """, new[] { "3", "free b0", "free b1", "b2", "13", "42", "5 child", "4", "6", "8", "free child", "end", "free b2" });

    CheckPerf("return in ineinander liegenden finally-Bloecken: die Bewohner des Stacks bleiben fuer das naechste finally liegen", """
        class T {
            static int Nested(int n) {
                try {
                    n = n + 0
                } finally {
                    try {
                        try {
                        } finally {
                            if (n > 1) { return 1 }
                        }
                    } finally {
                        if (n > 0) { return 4 }
                    }
                }
            }
            static int WithLoops(int n) {
                var xs = [1, 2, 3]
                try {
                    foreach (x in xs) {
                        try {
                            foreach (y in xs) {
                                try { if (y == n) { return x * 10 + y } } finally { if (n == 3) { return 99 } }
                            }
                        } finally {
                            n = n + 0
                        }
                    }
                } finally {
                    foreach (z in xs) { if (z == 2 && n == 0) { return 77 } }
                }
                return -1
            }
        }
        print(T.Nested(2))
        print(T.Nested(1))
        print(T.Nested(0))
        print(1 + T.WithLoops(1) + 2)
        print(T.WithLoops(2))
        print(T.WithLoops(3))
        print(T.WithLoops(0))
        """, new[] { "4", "4", "undefined", "14", "12", "99", "77" });

    // --- Value: equality and arithmetic (compact layout, fast paths)
    CheckPerf("Gleichheit: Art, Einheit und Breite", """
        print(1 == 1)
        print(1 == 1.0)
        print(5mm == 5)
        print("a" == "a")
        print('a' == 'a')
        print(true == true)
        print(1.5 == 1.5)
        print(undefined == undefined)
        print(1 != 2)
        """, new[] { "True", "False", "True", "True", "True", "True", "True", "True", "True" });

    CheckPerf("Arithmetik: int, float, gemischt, Einheiten", """
        print(7 + 3)
        print(7 - 10)
        print(6 * 7)
        print(7 / 2)
        print(7 % 4)
        print(2.5 + 1)
        print(1 + 2.5)
        print(7.5 / 2)
        print(-7 % 3)
        print(3mm + 4mm)
        print(2 * 3mm)
        print(6mm / 2)
        print(1 < 2)
        print(2 <= 2)
        print(3 > 4)
        print(4 >= 4.0)
        print(1.5 < 1.6)
        """, new[] { "10", "-3", "42", "3", "3", "3.5", "3.5", "3.75", "-1", "7mm", "6mm", "3mm", "True", "True", "False", "True", "True" });

    CheckPerf("Division und Modulo durch 0 bleiben Fehler", """
        var zero = 0
        try { print(5 % zero) } catch (Exception e) { print("mod") }
        print("weiter")
        """, new[] { "AUSNAHME: Attempted to divide by zero." });

    CheckPerf("Einheiten-Konflikt bleibt ein Fehler", """
        print(1mm + 2)
        """, new[] { "AUSNAHME: Incompatible units: 'mm' cannot be converted to 'unitless'." });

    // --- Stack and scope slots grow
    CheckPerf("tiefe Rekursion (Stack und Frames wachsen)", """
        class R { static int Sum(int n) { if (n == 0) { return 0 } return n + R.Sum(n - 1) } }
        print(R.Sum(1500))
        """, new[] { "1125750" });

    CheckPerf("viele lokale Variablen und langes Array-Literal", """
        class T { static int G() { var a = 1; var b = 2; var c = 3; var d = 4; var e = 5; var f = 6; var g = 7; return a + b + c + d + e + f + g } }
        print(T.G())
        var arr = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]
        var s = 0
        foreach (x in arr) { s = s + x }
        print(s)
        """, new[] { "28", "210" });

    // --- Inline caches: one call site, several classes
    CheckPerf("polymorphe Aufrufstelle: Klasse wechselt, Override, Feldzugriff", """
        class A { int v; construct() { this.v = 1 } Who() { return "A" + this.v } }
        class B : A { construct() : base() { this.v = 2 } Who() { return "B" + this.v } }
        class C { int v; construct() { this.v = 3 } Who() { return "C" + this.v } }
        var items = [new A(), new B(), new C(), new A(), new C(), new B()]
        var text = ""
        foreach (o in items) { text = text + o.Who() + "," + o.v + ";" }
        print(text)
        for (var i = 0; i < 6; i = i + 1) { items[i].v = i * 10 }
        var sum = 0
        foreach (o in items) { sum = sum + o.v }
        print(sum)
        """, new[] { "A1,1;B2,2;C3,3;A1,1;C3,3;B2,2;", "150" });

    CheckPerf("Aufrufstelle: erst erlaubt, dann private Methode/Feld (Zugriffsschutz bleibt)", """
        class Open { int f; Go() { return "offen" } construct() { this.f = 1 } }
        class Closed { private int f; private Go() { return "zu" } construct() { this.f = 2 } }
        var Probe = func (o) => {
            try { return o.Go() } catch (AccessDeniedException e) { return "verweigert" }
        }
        var ProbeField = func (o) => {
            try { return o.f } catch (AccessDeniedException e) { return "feld verweigert" }
        }
        var objs = [new Open(), new Closed(), new Open(), new Closed()]
        for (var i = 0; i < 4; i = i + 1) { print(Probe(objs[i])); print(ProbeField(objs[i])) }
        """, new[] { "offen", "1", "verweigert", "feld verweigert", "offen", "1", "verweigert", "feld verweigert" },
        new[] { VmExecutionMode.Debug, VmExecutionMode.Release });

    CheckPerf("Feld mit Einheit: jede Zuweisung wird geprueft (auch nach dem ersten Erfolg)", """
        class M { int len : mm = 0mm; construct() { this.len = 1mm } }
        var m = new M()
        for (var i = 0; i < 3; i = i + 1) { m.len = 5mm }
        print(m.len)
        try { m.len = 7 } catch (UnitMismatchException e) { print("Einheit") }
        m.len = 6mm
        print(m.len)
        """, new[] { "5mm", "Einheit", "6mm" }, new[] { VmExecutionMode.Debug, VmExecutionMode.Release });

    CheckPerf("Konstruktoren, Standardwerte, Lambdas, statische Aufrufe an einer Stelle", """
        class P { int x; int y; construct(int x, int y = 5) { this.x = x; this.y = y } }
        class Q { static int Twice(int a, int b = 2) { return a * b } }
        var total = 0
        for (var i = 0; i < 5; i = i + 1) {
            var p = new P(i)
            var q = new P(i, i)
            total = total + p.x + p.y + q.y + Q.Twice(i) + Q.Twice(i, 3)
        }
        print(total)
        var add = func (a, b) => { return a + b }
        var inc = func (a) => { return a + 1 }
        print(add(2, 3) + inc(4))
        """, new[] { "95", "10" });

    CheckPerf("ref-Parameter: Variablen, Felder, Array-Elemente, Puffer, Konstruktor, Weitergabe, Basistypen und Strings als Kopie", """
        class Box { int n; string s; construct() { this.n = 1; this.s = "a" } }
        class U {
            static Swap(ref a, ref b) { var t = a; a = b; b = t }
            static Inc(ref int x) { x++; x = x + 10 }
            static Twice(ref int x) { U.Inc(x); U.Inc(x) }
            static Append(ref string s, string t) { s = s + t }
            static Plain(int x) { x = 99 }
            static int Sum(ref int a, int b) { return a + b }
        }
        class Counter {
            int total
            construct(ref int seed) { this.total = seed; seed = 100 }
            Add(ref int v) { v = v + this.total }
            Bump(ref int v) { this.total = this.total + 1; v = this.total }
            Self() { this.Bump(total) }
        }
        var x = 1
        var y = 2
        U.Swap(x, y)
        print(x + " " + y)
        U.Inc(x)
        print(x)
        U.Twice(y)
        print(y)
        var s = "hi"
        U.Append(s, "!!")
        print(s)
        var z = 5
        U.Plain(z)
        print(z)
        var b = new Box()
        U.Swap(b.n, b.s)
        print(b.n + " " + b.s)
        var arr = [1, 2, 3]
        U.Inc(arr[1])
        U.Swap(arr[0], arr[2])
        print(arr[0] + " " + arr[1] + " " + arr[2])
        var buf = new byte[2]
        U.Inc(buf[1])
        print(buf[1])
        var seed = 7
        var c = new Counter(seed)
        print(seed + " " + c.total)
        var k = 3
        c.Add(k)
        print(k)
        c.Self()
        print(c.total)
        print(U.Sum(k, 1))
        var f = (int v) => { U.Inc(v); return v }
        print(f(5))
        {
            var loc = 4
            U.Inc(loc)
            print(loc)
        }
        try { U.Inc(arr[9]) } catch (e) { print("oob " + e.message) }
        """, new[] { "2 1", "13", "23", "hi!!", "5", "a 1", "3 13 1", "11", "100 7", "10", "8", "11", "16", "15", "oob Array index 9 out of range (length 3)." });

    CheckPerf("ref-Parameter: gewoehnliche Methode gleichen Namens (List.Add) bekommt den Wert, ein Wert fuer ein ref ist ein Fehler", """
        class Counter { int total; construct() { this.total = 0 } Add(ref int v) { v = v + 1 } }
        var l = new List()
        var q = 3
        l.Add(q)
        l.Add(5)
        print(l.count + " " + q)
        var c = new Counter()
        c.Add(q)
        print(q)
        """, new[] { "2 3", "4" }, new[] { VmExecutionMode.Release });
    foreach (var (refSource, refMessage) in new[]
    {
        ("var f = func (ref x) { x = 1 }", "only possible in methods and constructors"),
        ("class A { M(ref int x = 1) { } }", "cannot have a default value"),
    })
    {
        string refResult;
        try { new Linker().CompileAndLink(new[] { refSource }, null, null, VmExecutionMode.Release); refResult = "kein Fehler"; }
        catch (Exception ex) { refResult = ex.Message; }
        bool refOk = refResult.Contains(refMessage);
        if (!refOk) perfFailures++;
        Console.WriteLine(refOk ? $"OK: ref-Parameter: Fehler '{refMessage}'" : $"FEHLER: ref-Parameter: erwartet '{refMessage}', erhalten '{refResult}'");
    }

    CheckPerf("Arrays und Puffer im Besitzmodell: Scope, Feld, return, TakeTo/TakeGlobal/Take, delete, zerstoerte Benutzung, innere Arrays", """
        class Holder {
            int data[]
            construct() { this.data = [1, 2, 3] }
            Fill() { var tmp = new int[2]; tmp[0] = 7; this.data = tmp; tmp.TakeTo(this) }
            Bad() { var tmp = new int[2]; this.data = tmp }
        }
        class Res { string n; construct(string n) { this.n = n } destruct() { print("free " + this.n) } }
        class Make {
            static int[] Create() { var a = [4, 5, 6]; return a }
            static int[] Pair() { var a = new int[2]; { var b = new int[1]; b.TakeUpwards(); a[0] = b[0] } return a }
        }
        var h = new Holder()
        print(h.data[1])
        h.Fill()
        print(h.data[0])
        var r = Make.Create()
        print(r[2])
        var p = Make.Pair()
        print(p.length)
        h.Bad()
        try { print(h.data[0]) } catch (DestroyedException e) { print("destroyed: " + e.message) }
        var x = [1, 2]
        delete x
        try { print(x[0]) } catch (e) { print("after delete: " + e.message) }
        try { print(x.length) } catch (e) { print("len: " + e.message) }
        var m = new int[2][3]
        m[1][2] = 9
        print(m[1][2])
        delete m
        try { print(m[0]) } catch (e) { print("matrix gone") }
        var b = new byte[4]
        b[1] = 5
        b.TakeGlobal()
        print(b[1])
        {
            var inner = [9, 9]
            inner.TakeLocal()
            var r2 = new Res("r2")
            r2.TakeLocal()
        }
        print("end")
        var rr = new Res("kept")
        delete rr
        print("last")
        {
            var tmp = [1]
            tmp.TakeGlobal()
            var g = tmp
        }
        print("done")
        class Cell { int vals[]; construct() { this.vals = new int[3]; this.vals[0] = 5 } }
        var c = new Cell()
        print(c.vals[0])
        var grid = [[1, 2], [3, 4]]
        print(grid[1][0])
        class Fn { static int[][] Make() { return [[7, 8], [9]] } }
        print(Fn.Make()[0][1])
        var words = "a,b,c".Split(",")
        print(words.length)
        """, new[] { "2", "7", "6", "2", "destroyed: Access to a destroyed array.", "after delete: Access to a destroyed array.", "len: Access to a destroyed array.", "9", "matrix gone", "5", "free r2", "end", "free kept", "last", "done", "5", "3", "8", "3" }, new[] { VmExecutionMode.Debug, VmExecutionMode.Release });

    CheckPerf("Objekte: Ownership und Destruktor pro Schleifendurchlauf", """
        class D { int id; construct(int id) { this.id = id } destruct() { print("d" + this.id) } }
        for (var i = 0; i < 3; i = i + 1) { var d = new D(i) }
        print("ende")
        """, new[] { "d0", "d1", "d2", "ende" });

    CheckPerf("Arrays: Lesen, Schreiben, Grenzen, byte-Puffer, Strings", """
        var a = new int[3]
        a[0] = 10; a[1] = 20; a[2] = a[0] + a[1]
        print(a[2])
        try { print(a[3]) } catch (IndexOutOfBoundsException e) { print("aussen") }
        try { a[-1] = 1 } catch (IndexOutOfBoundsException e) { print("aussen2") }
        var b = new byte[2]
        b[1] = 200
        print(b[1])
        print("hey"[1])
        """, new[] { "30", "aussen", "aussen2", "200", "e" }, new[] { VmExecutionMode.Debug, VmExecutionMode.Release });

    Console.WriteLine(perfFailures == 0 ? "Alle VM-Optimierungs-Pruefungen bestanden." : $"FEHLER: {perfFailures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Kopieren: flat x / copy x (SPEC 2.4) ===");
{
    int cloneFailures = 0;

    List<string> RunClone(string script)
    {
        var lines = new List<string>();
        var program = Parser.ParseMultiple(Preprocessed(Directory.GetCurrentDirectory(), fire.Standard.Prelude.Source, script));
        var natives = new NativeRegistry();
        natives.Register("print", args => { lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes);
        vm.Run();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckClone(string title, string script, params string[] expected)
    {
        string[] actual;
        try { actual = RunClone(script).ToArray(); }
        catch (Exception ex) { actual = new[] { "AUSNAHME: " + ex.Message }; }
        bool ok = actual.SequenceEqual(expected);
        if (!ok) cloneFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
    }

    void CheckCloneError(string title, string script, string fragment)
    {
        string actual;
        try { actual = "kein Fehler: " + string.Join(" | ", RunClone(script)); }
        catch (Exception ex) { actual = ex.Message; }
        bool ok = actual.Contains(fragment);
        if (!ok) cloneFailures++;
        Console.WriteLine(ok ? $"OK: {title} -> {actual}" : $"FEHLER: {title}\n  erwartet: ...{fragment}...\n  erhalten: {actual}");
    }

    const string cloneClasses = """
        class Item {
            int n
            construct(int n) { this.n = n }
            destruct() { print("~I" + this.n) }
        }
        class Box {
            string name
            int size
            Item item
            Box other
            Item list[]
            int Extra { get; set; }
            construct(string name) { this.name = name; this.size = 1 }
            destruct() { print("~B" + this.name) }
        }

        """;

    CheckClone("flat: Objekt selbst und Werte kopiert, Referenzen bleiben", cloneClasses + """
        var a = new Box("a")
        a.item = new Item(1)
        a.Extra = 5
        var f = flat a
        f.name = "f"
        f.size = 7
        f.Extra = 6
        print(a.name + a.size + a.Extra)
        print(f.name + f.size + f.Extra)
        print(f.item == a.item)
        print(f == a)
        f.item.n = 9
        print(a.item.n)
        """, "a15", "f76", "True", "False", "9", "~Ba", "~I9", "~Bf");

    CheckClone("copy: Tiefenkopie, Kopie ist unabhaengig", cloneClasses + """
        var a = new Box("a")
        a.item = new Item(1)
        var d = copy a
        d.item.n = 99
        d.name = "d"
        print(a.item.n + " " + d.item.n)
        print(d.item == a.item)
        print(a.name)
        """, "1 99", "False", "a", "~Ba", "~I1", "~Bd", "~I99");

    CheckClone("copy: gemeinsame Referenz bleibt gemeinsam, Zyklus bleibt Zyklus", cloneClasses + """
        var shared = new Item(5)
        var x = new Box("x")
        x.item = shared
        x.other = new Box("y")
        x.other.item = shared
        x.other.other = x
        var c = copy x
        print(c.item == c.other.item)
        print(c.item == shared)
        print(c.other.other == c)
        print(c.other.other == x)
        print(c.other != x.other)
        """, "True", "False", "True", "False", "True", "~I5", "~Bx", "~By", "~Bx", "~By", "~I5");

    CheckClone("Arrays: flat teilt die Elemente, copy kopiert sie; Array als Operand", cloneClasses + """
        var b = new Box("arr")
        b.list = [new Item(1), new Item(2)]
        var f = flat b
        var c = copy b
        c.list[0].n = 10
        f.list[1].n = 20
        print(b.list[0].n + " " + b.list[1].n + " " + c.list[0].n)
        print(f.list == b.list)
        print(c.list == b.list)
        var nums = [1, 2, 3]
        var n1 = flat nums
        var n2 = copy nums
        n1[0] = 100
        n2[1] = 200
        print(nums[0] + " " + nums[1] + " " + n1[0] + " " + n2[1])
        var bytes = "AB".ToBytes()
        var b2 = copy bytes
        b2[0] = 67
        print(bytes[0] + " " + b2[0])
        """, "1 20 10", "True", "False", "1 2 100 200", "65 67", "~Barr", "~I1", "~I20", "~Barr", "~Barr", "~I2", "~I10");

    CheckClone("Werte: nichts zu kopieren", """
        print(copy 5)
        print(flat "text")
        print(copy 2.5)
        print(flat true)
        var u = copy undefined
        print(u == undefined)
        """, "5", "text", "2.5", "True", "True");

    CheckClone("Parameter: die Funktion arbeitet auf einer Kopie", cloneClasses + """
        var Touch = func (b) => { b.name = "geaendert"; b.item.n = 77; return b.name }
        var a = new Box("a")
        a.item = new Item(1)
        print(Touch(flat a))
        print(a.name + " " + a.item.n)
        a.name = "a"
        a.item.n = 1
        print(Touch(copy a))
        print(a.name + " " + a.item.n)
        """, "~Bgeaendert", "geaendert", "a 77", "~Bgeaendert", "~I77", "geaendert", "a 1", "~Ba", "~I1");

    CheckClone("Owner: Kopie gehoert dem Scope und wird mit ihm zerstoert (Besitz bleibt erhalten)", cloneClasses + """
        {
            var a = new Box("a")
            a.item = new Item(1)
            var f = flat a
            f.name = "f"
            var d = copy a
            d.name = "d"
            print("|")
        }
        print("danach")
        """, "|", "~Ba", "~I1", "~Bf", "~Bd", "~I1", "danach");

    CheckClone("Owner: direkt einem Feld zugewiesen gehoert die Kopie dem Zielobjekt", cloneClasses + """
        {
            var src = new Box("src")
            src.item = new Item(3)
            var holder = new Box("holder")
            holder.other = copy src
            holder.other.name = "kopie"
            holder.item = flat src.item
            print("|")
        }
        print("danach")
        """, "|", "~Bsrc", "~I3", "~Bholder", "~Bkopie", "~I3", "~I3", "danach");

    CheckClone("Owner ausserhalb der Kopie: die Kopie gehoert dem Scope", cloneClasses + """
        {
            var owner = new Box("own")
            owner.item = new Item(7)
            var holder = new Box("holder")
            holder.item = owner.item
            var c = copy holder
            c.item.n = 8
            print(holder.item.n + " " + c.item.n)
        }
        print("danach")
        """, "7 8", "~Bown", "~I7", "~Bholder", "~Bholder", "~I8", "danach");

    CheckClone("Kopie laeuft ohne Konstruktor, der Destruktor laeuft fuer die Kopie", """
        class K {
            int v
            construct(int v) { this.v = v; print("ctor") }
            destruct() { print("dtor" + this.v) }
        }
        {
            var k = new K(1)
            var c = copy k
            var f = flat k
            c.v = 2
            print("kopiert")
        }
        """, "ctor", "kopiert", "dtor1", "dtor2", "dtor1");

    CheckClone("Kopie in Methode und return: Ownership geht an den Aufrufer", cloneClasses + """
        class Maker { static Box Make(Box src) { var c = copy src; c.name = "made"; return c } }
        var s = new Box("s")
        s.item = new Item(4)
        {
            var m = Maker.Make(s)
            print(m.name + " " + m.item.n)
        }
        print("danach")
        """, "made 4", "~Bmade", "~I4", "danach", "~Bs", "~I4");

    CheckClone("Actor: in der Tiefe geteilt", """
        actor Counter { int n; construct() { this.n = 0 } Inc() { this.n = this.n + 1 } }
        class Holder { Counter c; construct() { this.c = new Counter() } }
        var h = new Holder()
        var d = copy h
        var f = flat h
        print(d.c == h.c)
        print(f.c == h.c)
        """, "True", "True");

    CheckCloneError("Actor als Operand", """
        actor Counter { int n; construct() { this.n = 0 } }
        var c = new Counter()
        var d = copy c
        """, "actor");

    CheckCloneError("zerstoertes Objekt", cloneClasses + """
        var leaked = new Box("x")
        {
            var t = new Box("t")
            leaked = t
            delete t
        }
        var d = copy leaked
        """, "destroyed");

    CheckCloneError("Kopier-Praefix ohne Operand", "var x = copy", "Unexpected token");

    Console.WriteLine(cloneFailures == 0 ? "Alle Kopier-Pruefungen bestanden." : $"FEHLER: {cloneFailures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Kopieren: Editor ===");
{
    int failures = 0;
    void Check(string title, string source, string expected, string forbidden = "")
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        var index = fire.Editor.ScriptSymbolIndex.Build(source);
        var names = fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).Select(i => i.Text).ToList();
        bool ok = expected.Split(',', StringSplitOptions.RemoveEmptyEntries).All(names.Contains)
            && !forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(names.Contains);
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "OK" : "FEHLER")}: {title} -> {string.Join(", ", names.Take(8))}");
    }
    const string dogs = "class Dog { Bark() { } }\nclass Cat { Purr() { } }\nvar a = new Dog()\n";
    Check("var d = copy a", dogs + "var d = copy a\nd.|", "Bark", "Purr");
    Check("var f = flat a.Self", "class Dog { Bark() { } }\nclass Kennel { Dog dog }\nvar k = new Kennel()\nvar f = flat k.dog\nf.|", "Bark");
    Check("copy/flat als Schluesselwoerter", "var x = 1\nco|", "copy");
    Check("flat als Schluesselwort", "var x = 1\nfla|", "flat");
    Console.WriteLine(failures == 0 ? "Alle Kopier-Editor-Pruefungen bestanden." : $"FEHLER: {failures} Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Kopien: Owner bei Parametern und Zuweisungen; leave zerstoert alles ===");
{
    int lifeFailures = 0;
    var allModes = new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance };

    List<string> RunLife(string script, VmExecutionMode mode, bool withIo = false, bool disposeIo = true)
    {
        var lines = new List<string>();
        var sources = withIo
            ? new[] { fire.Standard.Prelude.Source, IoPreludeSource(), script }
            : new[] { fire.Standard.Prelude.Source, script };
        var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = Parser.ParseMultiple(sources.Select(s => Preprocessor.Process(s, Directory.GetCurrentDirectory(), alreadyIncluded)).ToList());
        var natives = new NativeRegistry();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        IDisposable? io = withIo ? UseIoPackage(natives, null, null) : null;
        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, executionMode: mode);
        vm.Run();
        if (disposeIo) io?.Dispose();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckLife(string title, string script, string[] expected, VmExecutionMode[]? modes = null)
    {
        foreach (var mode in modes ?? allModes)
        {
            string[] actual;
            try { actual = RunLife(script, mode).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + ex.Message }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) lifeFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    const string lifeClasses = """
        class Item {
            int n
            construct(int n) { this.n = n }
            destruct() { print("~I" + this.n) }
        }
        class Box {
            string name
            Item item
            Item extra
            construct(string name) { this.name = name }
            destruct() { print("~B" + this.name) }
        }

        """;

    // ---- Copies as parameters: scope of the called function
    CheckLife("Parameter: die Kopie gehoert der aufgerufenen Funktion (auch static/Methode/Lambda)", lifeClasses + """
        class F {
            static Use(b) { b.name = "s"; print("in") }
            static Keep(b) { return b }
            Inst(b) { b.name = "m"; print("inst") }
        }
        var lam = func (b) => { b.name = "l"; print("lam") }
        {
            var a = new Box("a")
            a.item = new Item(1)
            F.Use(copy a)
            print("|")
            new F().Inst(flat a)
            print("|")
            lam(copy a)
            print("|")
            var k = F.Keep(copy a)
            print("k")
        }
        print("ende")
        """, new[] { "in", "~Bs", "~I1", "|", "inst", "~Bm", "|", "lam", "~Bl", "~I1", "|", "k", "~Ba", "~I1", "~Ba", "~I1", "ende" });

    CheckLife("Parameter: dieselbe Aufrufstelle in der Schleife, Kopie jedes Mal neu", lifeClasses + """
        class F { static int Bump(b) { b.item.n = b.item.n + 1; return b.item.n } }
        var a = new Box("a")
        a.item = new Item(1)
        var total = 0
        for (var i = 0; i < 3; i = i + 1) { total = total + F.Bump(copy a) }
        print(total)
        print(a.item.n)
        """, new[] { "~Ba", "~I2", "~Ba", "~I2", "~Ba", "~I2", "6", "1", "~Ba", "~I1" });

    CheckLife("Parameter: Konstruktor (TakeTo behaelt die Kopie) und base(...)", lifeClasses + """
        class Keep { Item held; construct(i) { this.held = i; i.TakeTo(this) } }
        class Base2 { construct(i) { print("base " + i.n) } }
        class Derived : Base2 { construct(i) : base(copy i) { print("derived") } }
        var t = new Item(4)
        {
            var k = new Keep(copy t)
            print(k.held.n)
            var d = new Derived(t)
            print("d")
        }
        print("ende")
        """, new[] { "4", "base 4", "~I4", "derived", "d", "~I4", "ende", "~I4" });

    CheckLife("Parameter: auch bei Methoden von Basistypen (Erweiterung) und verschachtelten Aufrufen", """
        class extends string { bool Has(x) { return this.Contains(x) } }
        var Id = func (x) => { return x }
        var Two = func (a, b) => { return a.n + b.n }
        class N { int n; construct(int n) { this.n = n } }
        print("abc".Has(copy "b"))
        print(Two(copy new N(1), Id(copy new N(2))))
        """, new[] { "True", "3" });

    // ---- Copy assigned to an object: the object becomes the owner (like TakeTo)
    CheckLife("Zuweisung an ein Objekt: Feld, Feld-Initialisierer, bloßer Feldname", lifeClasses + """
        var template = new Item(9)
        class H {
            Item init = new Item(1)
            Item cp = copy template
            Item later
            Item bare
            Setup() { bare = new Item(5) }
            SetupCopy() { later = copy template }
            destruct() { print("~H") }
        }
        {
            var h = new H()
            print("nach ctor")
            h.Setup()
            h.SetupCopy()
            print("nach setup")
        }
        print("ende")
        """, new[] { "nach ctor", "nach setup", "~H", "~I1", "~I9", "~I5", "~I9", "ende", "~I9" });

    CheckLife("Zuweisung an ein Objekt, das schon zerstoert wird: die Kopie wird sofort mit zerstoert (wie TakeTo)", lifeClasses + """
        var template = new Item(9)
        class R { Item late; destruct() { this.late = copy template } }
        {
            var r = new R()
        }
        print("ende")
        """, new[] { "~I9", "ende", "~I9" });

    // ---- leave: immediately, and everything is destroyed
    CheckLife("leave wirkt sofort und zerstoert auch die Objekte des globalen Scopes (finally laeuft)", lifeClasses + """
        var g = new Item(1)
        var f = func () => {
            var local = new Item(2)
            {
                var inner = new Item(3)
                leave
            }
            print("nie")
        }
        try {
            f()
        } finally {
            print("finally")
        }
        print("nicht erreicht")
        """, new[] { "~I3", "~I2", "finally", "~I1" });

    CheckLife("leave: Kopien und verschachtelte Besitzer werden mit zerstoert", lifeClasses + """
        var a = new Box("a")
        a.item = new Item(1)
        var c = copy a
        c.name = "c"
        leave
        print("nie")
        """, new[] { "~Ba", "~I1", "~Bc", "~I1" });

    // ---- leave/terminate: the calling thread stops immediately; both end like the normal program end
    //      (the main program waits for all fire threads, only then are the global destructors run)
    void CheckShutdown(string title, string script, string[] expected)
    {
        foreach (var mode in allModes)
        {
            string[] actual;
            VM.ResetTerminateForTests();
            try
            {
                var task = Task.Run(() => RunLife(script, mode).ToArray());
                actual = task.Wait(TimeSpan.FromSeconds(20)) ? task.Result : new[] { "ZEITUEBERSCHREITUNG (haengt)" };
            }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + ex.InnerException?.Message ?? ex.Message }; }
            VM.ResetTerminateForTests();
            bool ok = actual.SequenceEqual(expected);
            if (!ok) lifeFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    CheckShutdown("leave im Hauptprogramm wartet auf Fire-Threads, dann erst werden die Globals zerstoert", lifeClasses + """
        var g = new Item(1)
        fire {
            var i = 0
            while (i < 300000) { i = i + 1 }
            print("thread fertig")
        }
        leave
        print("nie")
        """, new[] { "thread fertig", "~I1" });

    CheckShutdown("terminate im Hauptprogramm stoppt Fire-Threads (finally laeuft), Globals zuletzt", lifeClasses + """
        var g = new Item(1)
        fire {
            try { while (true) { } } finally { print("thread finally") }
        }
        var j = 0
        while (j < 1000) { j = j + 1 }
        terminate(5)
        print("nie")
        """, new[] { "thread finally", "~I1" });

    CheckShutdown("terminate in einem Fire-Thread stoppt auch das Hauptprogramm; der Aufrufer fuehrt nichts mehr aus", lifeClasses + """
        var g = new Item(1)
        fire {
            var k = 0
            while (k < 1000) { k = k + 1 }
            terminate(3)
            print("nie im thread")
        }
        try { while (true) { } } finally { print("main finally") }
        print("nie")
        """, new[] { "main finally", "~I1" });

    CheckShutdown("terminate: zwei Threads rufen es auf, beide halten sofort an", lifeClasses + """
        var g = new Item(1)
        fire { terminate(1); print("nie a") }
        fire { terminate(2); print("nie b") }
        var w = 0
        while (w < 100000) { w = w + 1 }
        print("nie main")
        """, Array.Empty<string>().Concat(new[] { "~I1" }).ToArray());

    CheckShutdown("terminate in einer Property (verschachtelte Ausfuehrung) haelt sofort an, danach geordnet (finally, Globals)", lifeClasses + """
        class P {
            int v {
                get {
                    print("im getter")
                    terminate(1)
                    print("nie getter")
                    return 5
                }
            }
        }
        var g = new Item(1)
        var p = new P()
        try {
            var x = p.v
            print("nie x")
        } finally {
            print("finally")
        }
        print("nie")
        """, new[] { "im getter", "finally", "~I1" });

    CheckShutdown("leave in einer Property (verschachtelte Ausfuehrung) haelt sofort an", lifeClasses + """
        class P {
            int v {
                get {
                    leave
                    print("nie getter")
                    return 5
                }
            }
        }
        var g = new Item(1)
        var p = new P()
        var x = p.v
        print("nie")
        """, new[] { "~I1" });

    {
        // An open FileStream: its destructor closes it on leave (the content is then completely on the disk).
        string leaveFile = Path.Combine(Path.GetTempPath(), "fire-leave-" + Guid.NewGuid().ToString("N") + ".bin").Replace("\\", "/");
        foreach (var mode in allModes)
        {
            string result;
            try
            {
                var lines = RunLife($$"""
                    #import "io"
                    var w = new IO.FileStream("{{leaveFile}}", IO.FileMode.Create)
                    w.Write("Hallo".ToBytes())
                    var Work = func () => {
                        var w2 = new IO.FileStream("{{leaveFile}}.2", IO.FileMode.Create)
                        w2.Write("Zwei".ToBytes())
                        leave
                    }
                    Work()
                    print("nie")
                    """, mode, withIo: true, disposeIo: false); // ohne Sicherheitsnetz: nur die Destruktoren schliessen
                long size1 = File.Exists(leaveFile) ? new FileInfo(leaveFile).Length : -1;
                long size2 = File.Exists(leaveFile + ".2") ? new FileInfo(leaveFile + ".2").Length : -1;
                result = $"{string.Join(",", lines)}|{size1}|{size2}";
            }
            catch (Exception ex) { result = "AUSNAHME: " + ex.Message; }
            finally
            {
                try { File.Delete(leaveFile); File.Delete(leaveFile + ".2"); } catch { }
            }
            bool ok = result == "|5|4";
            if (!ok) lifeFailures++;
            Console.WriteLine(ok ? $"OK: leave schliesst offene Streams [{mode}]" : $"FEHLER: leave schliesst offene Streams [{mode}]\n  erwartet: |5|4\n  erhalten: {result}");
        }
    }

    {
        // The host's safety net (IoBridge.RegisterAll(...).Dispose()) closes what is still open at the end -
        // here a stream in the global scope that is not destroyed at the normal program end.
        string netFile = Path.Combine(Path.GetTempPath(), "fire-net-" + Guid.NewGuid().ToString("N") + ".bin").Replace("\\", "/");
        string result;
        try
        {
            RunLife($$"""
                var w = new IO.FileStream("{{netFile}}", IO.FileMode.Create)
                w.Write("Netz".ToBytes())
                """, VmExecutionMode.Debug, withIo: true, disposeIo: true);
            result = (File.Exists(netFile) ? new FileInfo(netFile).Length : -1).ToString();
        }
        catch (Exception ex) { result = "AUSNAHME: " + ex.Message; }
        finally { try { File.Delete(netFile); } catch { } }
        bool ok = result == "4";
        if (!ok) lifeFailures++;
        Console.WriteLine(ok ? "OK: Host-Sicherheitsnetz schliesst offene Streams" : $"FEHLER: Host-Sicherheitsnetz\n  erwartet: 4\n  erhalten: {result}");
    }

    // ---- normal program end cleans up the global scope
    CheckLife("Programmende: globale Objekte werden zerstoert (destruct laeuft)", lifeClasses + """
        var a = new Box("a")
        a.item = new Item(1)
        var b = new Item(2)
        print("ende")
        """, new[] { "ende", "~Ba", "~I1", "~I2" });

    CheckLife("Programmende: das Hauptprogramm wartet auf Fire-Threads, dann erst werden die Globals zerstoert", lifeClasses + """
        var g = new Item(1)
        fire {
            var i = 0
            while (i < 300000) { i = i + 1 }
            print("thread fertig")
        }
        print("main fertig")
        """, new[] { "main fertig", "thread fertig", "~I1" });

    {
        // An open FileStream in the global scope is closed by the destructor at the normal end (without the host safety net).
        string endFile = Path.Combine(Path.GetTempPath(), "fire-end-" + Guid.NewGuid().ToString("N") + ".bin").Replace("\\", "/");
        string result;
        try
        {
            RunLife($$"""
                var w = new IO.FileStream("{{endFile}}", IO.FileMode.Create)
                w.Write("Ende".ToBytes())
                """, VmExecutionMode.Debug, withIo: true, disposeIo: false);
            result = (File.Exists(endFile) ? new FileInfo(endFile).Length : -1).ToString();
        }
        catch (Exception ex) { result = "AUSNAHME: " + ex.Message; }
        finally { try { File.Delete(endFile); } catch { } }
        bool ok = result == "4";
        if (!ok) lifeFailures++;
        Console.WriteLine(ok ? "OK: Programmende schliesst offene Streams (Destruktor)" : $"FEHLER: Programmende schliesst offene Streams\n  erwartet: 4\n  erhalten: {result}");
    }

    // ---- signals from other threads are noticed at the safe points (loops, calls)
    foreach (var mode in allModes)
    {
        foreach (var kind in new[] { "terminate", "leave" })
        {
            VM.ResetTerminateForTests();
            var lines = new List<string>();
            var natives = new NativeRegistry();
            natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
            natives.RegisterBaseTypeNatives();
            // A loop with a call and one without - both must be interruptible.
            string script = lifeClasses + """
                class L { static Tick(n) { return n + 1 } }
                var g = new Item(1)
                var i = 0
                while (true) {
                    i = L.Tick(i)
                    var j = 0
                    while (j < 1000) { j = j + 1 }
                }
                """;
            var program = Parser.ParseMultiple(new[] { fire.Standard.Prelude.Source, script }.Select(s => Preprocessor.Process(s, Directory.GetCurrentDirectory(), new HashSet<string>())).ToList());
            var resolveResult = Resolver.Resolve(program, natives.Names);
            var compiled = Compiler.Compile(program, resolveResult, natives);
            var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, executionMode: mode);
            var runner = Task.Run(() => vm.Run());
            Thread.Sleep(100);
            if (kind == "terminate") VM.RequestTerminate(Value.MakeInt(7)); else vm.RequestLeave();
            bool finished = runner.Wait(TimeSpan.FromSeconds(10));
            VM.ResetTerminateForTests();
            // `leave` and `terminate` both end like the normal program end: the global scope is destroyed (destruct runs).
            string[] expected = new[] { "~I1" };
            bool ok = finished && lines.SequenceEqual(expected);
            if (!ok) lifeFailures++;
            Console.WriteLine(ok ? $"OK: {kind} von einem anderen Thread beendet eine Endlosschleife [{mode}]"
                                 : $"FEHLER: {kind} von einem anderen Thread [{mode}] - beendet: {finished}, Ausgabe: {string.Join(" | ", lines)}");
        }
    }

    Console.WriteLine(lifeFailures == 0 ? "Alle Kopie/leave-Pruefungen bestanden." : $"FEHLER: {lifeFailures} Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Packer: self-contained file (bundle + payload), bridges only when needed, loader instead of Costura
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Packer / Payload / Lader ===");
    int packFailures = 0;
    void PackCheck(bool ok, string what)
    {
        if (!ok) packFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    var baseDir = AppContext.BaseDirectory;

    // 1) Pack plan: per import only the necessary DLLs, nothing unresolved.
    PackagePlan Plan(params string[] imports) => PackagePlan.Create(imports, baseDir);
    var planPrint = Plan(NativeImports.Print);
    PackCheck(planPrint.Assemblies.ContainsKey("fire") && planPrint.Assemblies.ContainsKey("MemoryPack.Core"), "Plan: Kern (fire, MemoryPack) ist immer dabei");
    PackCheck(!planPrint.Assemblies.Keys.Any(n => n.StartsWith("fire.Terminal") || n.StartsWith("fire.Device") || n.StartsWith("fire.IO") || n == "SDL3-CS" || n == "System.IO.Ports") && planPrint.Natives.Count == 0,
        "Plan: ohne Import keine Bridge, keine nativen Bibliotheken");
    var planIo = Plan(NativeImports.Print, "pkg:io");
    PackCheck(planIo.Assemblies.Keys.SequenceEqual(planPrint.Assemblies.Keys) && !planIo.Assemblies.ContainsKey("fire.Terminal.Bridge") && !planIo.Assemblies.ContainsKey("fire.Device.Manager"),
        "Plan: io ist ein Paket (C++ in einer Bibliothek): es bringt keine eigene DLL in das gepackte Programm");
    var planGfx = Plan(NativeImports.Print, NativeImports.Graphics);
    PackCheck(new[] { "fire.Terminal.Bridge", "fire.Terminal" }.All(planGfx.Assemblies.ContainsKey) && !planGfx.Assemblies.ContainsKey("fire.IO.Bridge")
        && !planGfx.Assemblies.Keys.Any(n => n is "fire.Terminal.Windows" or "fire.Terminal.Sdl" or "fire.Windows.Bridge" or "SDL3-CS") && planGfx.Natives.Count == 0,
        "Plan: graphics bindet Terminal-Bridge und Terminal ein - ohne Fenster, SDL und native Bibliotheken");
    var planWin = Plan(NativeImports.Print, NativeImports.Graphics, NativeImports.Windows);
    PackCheck(new[] { "fire.Terminal.Bridge", "fire.Windows.Bridge", "fire.Terminal", "fire.Terminal.Windows", "fire.Terminal.Sdl", "SDL3-CS" }.All(planWin.Assemblies.ContainsKey) && planWin.Unresolved.Count == 0,
        "Plan: windows bindet Fenster-Bridge samt Terminal/Windows/SDL ein (Abhaengigkeiten aus den Metadaten)");
    var planDev = Plan(NativeImports.Print, "pkg:devices");
    var planUi = Plan(NativeImports.Print, NativeImports.Graphics, NativeImports.Windows, NativeImports.Ui);
    PackCheck(planUi.Assemblies.Keys.SequenceEqual(planWin.Assemblies.Keys) && planUi.Unresolved.Count == 0, "Plan: ui bringt keine eigene DLL mit (reiner fire-Quelltext, graphics und windows kommen ueber den Import)");
    PackCheck(new[] { "fire.Device.Manager", "System.IO.Ports" }.All(planDev.Assemblies.ContainsKey) && !planDev.Assemblies.ContainsKey("SDL3-CS"),
        "Plan: devices (ein Paket) bindet den Geraetemanager des Hosts und System.IO.Ports ein");
    PackCheck(planGfx.Unresolved.Count == 0 && planDev.Unresolved.Count == 0 && planIo.Unresolved.Count == 0 && planPrint.Unresolved.Count == 0,
        "Plan: alle Verweise aufloesbar (Datei neben dem Compiler oder Teil des Frameworks)");
    PackCheck(planWin.Natives.Count == 0 || planWin.Natives.ContainsKey("SDL3.dll") || planWin.Natives.ContainsKey("libSDL3.so.0") || planWin.Natives.ContainsKey("libSDL3.dylib"),
        "Plan: windows bringt SDL3 mit, wo es die Plattform gibt");
    // `windows` is separate from `graphics`: the window exists only with its own import, which brings `graphics` along; `graphics` alone does not know a Window
    {
        string LinkResult(string src)
        {
            try { var linked = new Linker().CompileAndLink(new[] { src }); return string.Join(",", linked.NativeImports.OrderBy(x => x)); }
            catch (Exception ex) { return "FEHLER " + ex.Message; }
        }
        string onlyGfx = LinkResult("#import \"graphics\"\nvar w = new Window(new Framebuffer(8, 8), \"t\")");
        PackCheck(onlyGfx.StartsWith("FEHLER") && onlyGfx.Contains("Window"), "Import: graphics allein kennt kein Window (" + onlyGfx.Split('\n')[0] + ")");
        string withWin = LinkResult("#import \"windows\"\nvar x = EventType.Close");
        PackCheck(withWin == "graphics,print,windows", "Import: windows bringt graphics mit (" + withWin + ")");
        string withUi = LinkResult("#import \"ui\"\nvar x = EventType.Close");
        PackCheck(withUi == "graphics,print,reflection,ui,windows", "Import: ui bringt graphics, windows und reflection mit (" + withUi + ")");
        string gfxOnly = LinkResult("#import \"graphics\"\nvar fb = new Framebuffer(8, 8)");
        PackCheck(gfxOnly == "graphics,print", "Import: graphics allein ohne Fenster (" + gfxOnly + ")");
    }
    bool unknownImportRejected = false;
    try { Plan("gibtsnicht"); } catch (InvalidOperationException) { unknownImportRejected = true; }
    PackCheck(unknownImportRejected, "Plan: unbekannter Import wird abgelehnt statt still ignoriert");

    // 2) Payload format: round trip, compression, integrity, no marker search.
    var tmpDir = Path.Combine(Path.GetTempPath(), "fire-packtest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try
    {
        var plFile = Path.Combine(tmpDir, "pl.bin");
        var rnd = new Random(42);
        var incompressible = new byte[5000]; rnd.NextBytes(incompressible);
        var compressible = Enumerable.Repeat((byte)7, 20000).ToArray();
        // The old marker bytes (DA 1D) in the middle of the content must not disturb anything.
        var withMarker = new byte[] { 1, 2, 0xDA, 0x1D, 3, 4, 0xDA, 0x1D };
        using (var fs = new FileStream(plFile, FileMode.Create))
        {
            fs.Write(new byte[] { 0xDA, 0x1D, 9, 9, 0xDA, 0x1D });
            PayloadFile.Append(fs, new[]
            {
                (PayloadKind.Program, "program", withMarker),
                (PayloadKind.Assembly, "A", compressible),
                (PayloadKind.Native, "n.dll", incompressible),
            });
        }
        var reader = PayloadFile.Open(plFile);
        PackCheck(reader != null && reader.Entries.Count == 3, "Payload: Index wird am Dateiende gefunden");
        if (reader != null)
        {
            PackCheck(reader.Read(reader.Find(PayloadKind.Program, "program")!)!.SequenceEqual(withMarker), "Payload: Inhalt mit Marker-Bytes bleibt unversehrt");
            var eA = reader.Find(PayloadKind.Assembly, "a")!;
            PackCheck(eA.Compressed && eA.StoredLength < eA.RawLength / 10 && reader.Read(eA)!.SequenceEqual(compressible), "Payload: Brotli packt Kompressibles, Rundlauf stimmt");
            var eN = reader.Find(PayloadKind.Native, "n.dll")!;
            PackCheck(!eN.Compressed && reader.Read(eN)!.SequenceEqual(incompressible), "Payload: Unkomprimierbares wird roh gespeichert");

            // A flipped byte in the stored entry must be noticed.
            var bytes = File.ReadAllBytes(plFile);
            bytes[(int)eN.Offset + 10] ^= 0xFF;
            var badFile = Path.Combine(tmpDir, "bad.bin");
            File.WriteAllBytes(badFile, bytes);
            var badReader = PayloadFile.Open(badFile)!;
            PackCheck(badReader.Read(badReader.Find(PayloadKind.Native, "n.dll")!) == null, "Payload: beschaedigter Eintrag wird erkannt (Pruefsumme)");
        }
        var plain = Path.Combine(tmpDir, "plain.bin");
        File.WriteAllBytes(plain, new byte[1000]);
        PackCheck(PayloadFile.Open(plain) == null, "Payload: Datei ohne Payload liefert null");

        // 3) Loader: native library is unpacked from the payload and loaded (Linux: the .so of the ports library
        //    under a foreign name, so that it is not found via the normal search).
        if (OperatingSystem.IsLinux())
        {
            var so = Directory.GetFiles(Path.Combine(baseDir, "runtimes", "linux-x64", "native"), "libSystem.IO.Ports.Native.so").FirstOrDefault();
            if (so != null && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
            {
                var loaderFile = Path.Combine(tmpDir, "loader.bin");
                using (var fs = new FileStream(loaderFile, FileMode.Create))
                    PayloadFile.Append(fs, new[] { (PayloadKind.Native, "libfiretestnative.so", File.ReadAllBytes(so)) });
                PackCheck(PayloadLoader.Install(loaderFile), "Lader: Payload der eigenen Datei wird erkannt");
                string result;
                try { PackerNativeProbe.Call(); result = "geladen?"; }
                catch (EntryPointNotFoundException) { result = "geladen"; }
                catch (DllNotFoundException) { result = "nicht gefunden"; }
                PackCheck(result == "geladen", "Lader: native Bibliothek wird aus dem Payload entpackt und gefunden");
            }
        }

        // 4) End-to-end: pack a standalone file, start it OUTSIDE the compiler folder.
        var stubName = OperatingSystem.IsWindows() ? "fire.Runtime.exe" : "fire.Runtime";
        if (File.Exists(Path.Combine(baseDir, stubName)))
        {
            string RunPacked(string source, string name, out long size, out PackagePlan? plan)
            {
                var exe = Path.Combine(tmpDir, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
                var linked = new Linker().CompileAndLink(new[] { source }, null, exe);
                plan = PackagePlan.Create(linked.NativeImports, baseDir);
                size = new FileInfo(exe).Length;
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = tmpDir };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                var error = proc.StandardError.ReadToEnd();
                proc.WaitForExit(20000);
                return proc.ExitCode == 0 ? output.Replace("\r\n", "\n") : $"EXIT {proc.ExitCode}: {output}{error}";
            }

            var outPrint = RunPacked("print(\"hallo\")\nprint(\"welt\")", "p_print", out var sizePrint, out _);
            PackCheck(outPrint == "hallo\nwelt\n", $"Ende-zu-Ende: gepackte Datei laeuft allein (ohne DLLs daneben), Ausgabe: {outPrint.Trim()}");
            var outIo = RunPacked("#import \"io\"\nIO.Stdio.WriteLine(\"io ok\")", "p_io", out var sizeIo, out _);
            PackCheck(outIo == "io ok\n", $"Ende-zu-Ende: IO-Bridge wird zur Laufzeit aus der eigenen Datei geladen, Ausgabe: {outIo.Trim()}");
            PackCheck(sizeIo > sizePrint, $"Groesse: mit io ({sizeIo} B) groesser als ohne Bridge ({sizePrint} B)");
            var gfxSize = PackProgramSize("#import \"graphics\"\nprint(\"x\")");
            PackCheck(gfxSize > sizeIo, $"Groesse: graphics ({gfxSize} B) ist die groesste Variante, print ({sizePrint} B) die kleinste");
            // Without a payload (bare runtime) there is a clear message instead of a crash.
            var bare = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(baseDir, stubName)) { RedirectStandardError = true, RedirectStandardOutput = true })!;
            var bareErr = bare.StandardError.ReadToEnd(); bare.WaitForExit(20000);
            PackCheck(bare.ExitCode == 1 && bareErr.Contains("payload"), "Ende-zu-Ende: nackte Runtime ohne Payload meldet das verstaendlich");

            long PackProgramSize(string source)
            {
                var exe = Path.Combine(tmpDir, "size_probe" + (OperatingSystem.IsWindows() ? ".exe" : ""));
                new Linker().CompileAndLink(new[] { source }, null, exe);
                return new FileInfo(exe).Length;
            }
        }
        else Console.WriteLine($"(uebersprungen: {stubName} liegt nicht im Testordner)");
    }
    finally
    {
        try { Directory.Delete(tmpDir, true); } catch (IOException) { }
    }

    Console.WriteLine(packFailures == 0 ? "Alle Packer-Pruefungen bestanden." : $"FEHLER: {packFailures} Packer-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Command line of the compiler (run / build)
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Befehlszeile: run / build ===");
    int cliFailures = 0;
    void CliCheck(bool ok, string what)
    {
        if (!ok) cliFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    var r1 = CommandLineParser.Parse(new[] { "run", "code1", "codeN" });
    CliCheck(r1.Error == null && r1.Command == CommandKind.Run && r1.Files.SequenceEqual(new[] { "code1", "codeN" }) && r1.Mode == null, "run code1 codeN");
    var r2 = CommandLineParser.Parse(new[] { "run", "code1", "codeN", "-m", "DEBUG" });
    CliCheck(r2.Error == null && r2.Mode == VmExecutionMode.Debug && r2.Files.Count == 2, "run ... -m DEBUG");
    CliCheck(CommandLineParser.Parse(new[] { "RUN", "a", "-m", "performance" }).Mode == VmExecutionMode.Performance, "Befehl und Modus ohne Beachtung der Gross-/Kleinschreibung");
    CliCheck(CommandLineParser.Parse(new[] { "run", "--mode=release", "a" }).Mode == VmExecutionMode.Release, "--mode=release vor der Datei");
    var b1 = CommandLineParser.Parse(new[] { "build", "code1", "codeN" });
    CliCheck(b1.Error == null && b1.Command == CommandKind.Build && b1.OutputFile == "out.exe", "build ohne -o -> out.exe");
    var b2 = CommandLineParser.Parse(new[] { "build", "code1", "codeN", "-o", "test.exe" });
    CliCheck(b2.OutputFile == "test.exe" && b2.Files.SequenceEqual(new[] { "code1", "codeN" }), "build ... -o test.exe");
    var b3 = CommandLineParser.Parse(new[] { "build", "-o", "x y.exe", "eins zwei.script", "\"drei vier.script\"" });
    CliCheck(b3.OutputFile == "x y.exe" && b3.Files.SequenceEqual(new[] { "eins zwei.script", "drei vier.script" }), "Dateinamen mit Leerzeichen, -o vor den Dateien, umschliessende Anfuehrungszeichen entfernt");
    CliCheck(CommandLineParser.Parse(Array.Empty<string>()).Command == CommandKind.Help && CommandLineParser.Parse(new[] { "--help" }).Command == CommandKind.Help, "ohne Argumente / --help -> Hilfe");
    CliCheck(CommandLineParser.Parse(new[] { "laufen", "a" }).Error != null, "unbekannter Befehl ist ein Fehler");
    CliCheck(CommandLineParser.Parse(new[] { "run" }).Error != null, "run ohne Datei ist ein Fehler");
    CliCheck(CommandLineParser.Parse(new[] { "run", "a", "-m" }).Error != null && CommandLineParser.Parse(new[] { "run", "a", "-m", "TURBO" }).Error != null, "-m ohne/mit falschem Modus ist ein Fehler");
    CliCheck(CommandLineParser.Parse(new[] { "run", "a", "-o", "x.exe" }).Error != null, "-o gibt es nur bei build");
    CliCheck(CommandLineParser.Parse(new[] { "run", "a", "-x" }).Error != null, "unbekannte Option ist ein Fehler");

    // End to end via the runner (without a process): build produces the file, run returns exit codes.
    var cliDir = Path.Combine(Path.GetTempPath(), "fire-cli-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(cliDir);
    try
    {
        var f1 = Path.Combine(cliDir, "eins.script");
        var f2 = Path.Combine(cliDir, "zwei mit leer.script");
        File.WriteAllText(f1, "class G { Hi() { print(\"hi\") } }\n");
        File.WriteAllText(f2, "new G().Hi()\nterminate(5)\n");
        var errW = new StringWriter();
        var prevOut = Console.Out;
        var capture = new StringWriter();
        Console.SetOut(capture);
        int code;
        try { code = CommandLineRunner.Run(new[] { "run", f1, f2 }, capture, errW); }
        finally { Console.SetOut(prevOut); VM.ResetTerminateForTests(); }
        CliCheck(code == 5 && capture.ToString().Replace("\r", "") == "hi\n", $"run: zwei Dateien zu einem Programm, terminate(5) -> Exitcode 5 (Code {code}, Ausgabe '{capture.ToString().Trim()}')");

        var bad = Path.Combine(cliDir, "bad.script");
        File.WriteAllText(bad, "var x = \n");
        var errBad = new StringWriter();
        CliCheck(CommandLineRunner.Run(new[] { "run", bad }, new StringWriter(), errBad) == CommandLineRunner.ExitScriptError && errBad.ToString().Length > 0, "run: Kompilierfehler -> Exitcode 1 mit Meldung");
        CliCheck(CommandLineRunner.Run(new[] { "run", Path.Combine(cliDir, "nix.script") }, new StringWriter(), new StringWriter()) == CommandLineRunner.ExitUsage, "run: fehlende Datei -> Exitcode 2");

        var stubName = OperatingSystem.IsWindows() ? "fire.Runtime.exe" : "fire.Runtime";
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, stubName)))
        {
            var outFile = Path.Combine(cliDir, "gebaut.exe");
            int bc = CommandLineRunner.Run(new[] { "build", f1, f2, "-o", outFile, "-m", "DEBUG" }, new StringWriter(), new StringWriter());
            CliCheck(bc == 0 && File.Exists(outFile), "build -o: erzeugt die Datei");
            var packed = File.Exists(outFile) ? Packer.UnpackProgram(outFile) : null;
            CliCheck(packed != null && packed.ExecutionMode == VmExecutionMode.Debug, "build -m DEBUG: der Modus steckt im gepackten Programm");
        }
    }
    finally { try { Directory.Delete(cliDir, true); } catch (IOException) { } }

    Console.WriteLine(cliFailures == 0 ? "Alle Befehlszeilen-Pruefungen bestanden." : $"FEHLER: {cliFailures} Befehlszeilen-Pruefung(en) fehlgeschlagen.");
}

Console.WriteLine();
Console.WriteLine("=== Font-Rendering: schneller Weg == Pixel-fuer-Pixel-Weg ===");
{
    int fontFailures = 0;
    void FontCheck(bool ok, string what)
    {
        if (!ok) fontFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    // The same font, but WITHOUT bitmap rows: the renderer must then take the general path (IsPixelSet per pixel).
    var slowFont = new PixelOnlyFont(new fire.Terminal.IntegratedGlyphFont());
    var rng = new Random(7);
    foreach (bool small in new[] { false, true })
    {
        var fastFont = new fire.Terminal.IntegratedGlyphFont(small);
        var slow = new PixelOnlyFont(fastFont);
        foreach (bool opaque in new[] { true, false })
        {
            var fbFast = new fire.Terminal.Framebuffer(203, 97); // odd size: the grid does not end at the edge, texts protrude
            var fbSlow = new fire.Terminal.Framebuffer(203, 97);
            var fast = new fire.Terminal.Renderer(fbFast, fastFont);
            var slowCanvas = new fire.Terminal.Renderer(fbSlow, slow);
            foreach (var cv in new[] { fast, slowCanvas })
            {
                cv.Foreground = new fire.Terminal.PixelColor(200, 100, 50);
                cv.Background = opaque ? new fire.Terminal.PixelColor(10, 20, 30) : null;
                ((fire.Terminal.Framebuffer)cv.Target).Clear(new fire.Terminal.PixelColor(1, 2, 3));
            }
            // Characters of the whole range (also > 255), at random positions including partly outside
            for (int i = 0; i < 400; i++)
            {
                char ch = (char)rng.Next(0, 400);
                int x = rng.Next(-12, 215), y = rng.Next(-16, 110);
                foreach (var cv in new[] { fast, slowCanvas })
                    cv.DrawGlyph(x, y, ch, new fire.Terminal.SolidBrush(cv.Foreground), cv.Background is fire.Terminal.PixelColor bg ? new fire.Terminal.SolidBrush(bg) : null);
            }
            // Print with wrapping and scrolling
            string text = string.Join("\n", Enumerable.Range(0, 40).Select(n => new string((char)('A' + n % 26), 10 + n % 40)));
            fast.Locate(0, 0); fast.Print(text);
            slowCanvas.Locate(0, 0); slowCanvas.Print(text);
            FontCheck(fbFast.Pixels.SequenceEqual(fbSlow.Pixels),
                $"{(small ? "8x8" : "8x14")} {(opaque ? "opak" : "transparent")}: Glyphen an beliebigen (auch ueberstehenden) Positionen und Print mit Umbruch/Scrollen identisch");
        }
    }

    {
        var fb = new fire.Terminal.Framebuffer(100, 40);
        var cv = new fire.Terminal.Renderer(fb, new fire.Terminal.IntegratedGlyphFont());
        cv.DrawText(3, 5, "Hallo", new fire.Terminal.SolidBrush(fire.Terminal.PixelColor.White));
        FontCheck(cv.MeasureText("Hallo") == 5 * cv.CellWidth, "MeasureText: Zeichenzahl mal Zellbreite");
        var fb2 = new fire.Terminal.Framebuffer(100, 40);
        var cv2 = new fire.Terminal.Renderer(fb2, new fire.Terminal.IntegratedGlyphFont());
        for (int i = 0; i < 5; i++) cv2.DrawGlyph(3 + i * cv2.CellWidth, 5, "Hallo"[i], new fire.Terminal.SolidBrush(fire.Terminal.PixelColor.White));
        FontCheck(fb.Pixels.SequenceEqual(fb2.Pixels), "DrawText == DrawGlyph je Zeichen");
        cv.Locate(0, 0); cv.Print("\u20AC\u4E2D"); // Characters outside the table: no crash
        FontCheck(true, "Zeichen > 255 werfen nicht");
    }

    Console.WriteLine(fontFailures == 0 ? "Alle Font-Pruefungen bestanden." : $"FEHLER: {fontFailures} Font-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Graphics: colour modes (RGBA / palette), colour specifications (index or direct value), drawing functions, copying
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Grafik: Farbmodi, Zeichenfunktionen, Blit ===");
    int gfxFailures = 0;
    void GfxCheck(bool ok, string what)
    {
        if (!ok) gfxFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    var RGBA = fire.Terminal.ColorMode.Rgba;
    var IDX = fire.Terminal.ColorMode.Indexed;
    fire.Terminal.PixelColor Col(byte r, byte g, byte b) => new fire.Terminal.PixelColor(r, g, b);
    fire.Terminal.Pixel Idx(fire.Terminal.Framebuffer fb, int i) => fb.ResolvePixel(fire.Terminal.Paint.FromIndex((byte)i));
    fire.Terminal.Brush Bsh(int i) => new fire.Terminal.SolidBrush(fire.Terminal.Paint.FromIndex((byte)i));
    fire.Terminal.Pen Pn(int i) => new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex((byte)i));

    // Set of the set pixels (palette: index != 0, RGBA: value != 0) as an "x,y" set
    HashSet<(int, int)> Lit(fire.Terminal.Framebuffer fb)
    {
        var set = new HashSet<(int, int)>();
        for (int y = 0; y < fb.Height; y++)
            for (int x = 0; x < fb.Width; x++)
                if (fb.GetRaw(x, y) != 0) set.Add((x, y));
        return set;
    }

    // ---- Palette-Framebuffer: Indizes, Palette, Resolve ----
    {
        var fb = new fire.Terminal.Framebuffer(4, 3, IDX);
        GfxCheck(fb.IsIndexed && fb.Indices != null && fb.Indices.Length == 12, "Palette-Framebuffer hat 1 Byte je Pixel");
        fb.Plot(1, 1, new fire.Terminal.Pixel(0, 9));
        fb.Resolve();
        GfxCheck(fb.Pixels[1 * 4 + 1] == fb.Palette.GetPacked(9) && fb.Pixels[0] == fb.Palette.GetPacked(0), "Resolve: Pixels = Palette[Index]");
        fb.Palette.SetColor(9, unchecked((int)new fire.Terminal.PixelColor(1, 2, 3).Packed));
        fb.Resolve();
        GfxCheck(fb.Pixels[1 * 4 + 1] == new fire.Terminal.PixelColor(1, 2, 3).Packed, "Palette aendern faerbt alle Pixel mit diesem Index um (Resolve rechnet neu)");
        fb.Indices![0] = 200; fb.MarkDirty(); fb.Resolve();
        GfxCheck(fb.Pixels[0] == fb.Palette.GetPacked(200), "MarkDirty nach direktem Schreiben der Indizes");
        GfxCheck(fb.GetPixel(0, 0).Packed == fb.Palette.GetPacked(200) && fb.GetIndex(0, 0) == 200, "GetPixel/GetIndex im Palette-Modus");
        fb.Plot(-1, 0, new fire.Terminal.Pixel(0, 5)); fb.Plot(4, 0, new fire.Terminal.Pixel(0, 5)); fb.Plot(0, 3, new fire.Terminal.Pixel(0, 5));
        GfxCheck(fb.GetIndex(0, 0) == 200 && fb.GetRaw(-1, 0) == 0, "Plot ausserhalb: still beschnitten");

        var rgba = new fire.Terminal.Framebuffer(2, 2);
        GfxCheck(!rgba.IsIndexed && rgba.Indices == null && rgba.Mode == RGBA, "RGBA-Framebuffer hat keine Indizes");
        rgba.Resolve(); // No-op
        try { new fire.Terminal.Framebuffer(2, 2, (fire.Terminal.ColorMode)7); GfxCheck(false, "unbekannter Farbmodus wird abgelehnt"); }
        catch (ArgumentOutOfRangeException) { GfxCheck(true, "unbekannter Farbmodus wird abgelehnt"); }
    }

    // ---- Colour specifications: index OR direct value, resolved per framebuffer ----
    {
        var p = fire.Terminal.Paint.FromArgument(7);
        GfxCheck(p.IsIndex && p.Index == 7 && fire.Terminal.Paint.FromArgument(255).IsIndex && !fire.Terminal.Paint.FromArgument(256).IsIndex, "Paint.FromArgument: 0-255 = Index, sonst direkter Wert");
        GfxCheck(!fire.Terminal.Paint.FromArgument(unchecked((int)0xFF0000FFu)).IsIndex && fire.Terminal.Paint.FromArgument(unchecked((int)0xFF0000FFu)).Rgba == 0xFF0000FFu, "ein deckender Wert mit R=255 ist ein direkter Wert, kein Index");
        GfxCheck(fire.Terminal.Paint.FromArgument((1L << 32) + 5).Index == 5, "nur die unteren 32 Bit zaehlen");

        var rgba = new fire.Terminal.Framebuffer(2, 2);
        var idx = new fire.Terminal.Framebuffer(2, 2, IDX);
        var red = fire.Terminal.PixelColor.FromRgb(255, 0, 0);
        var viaIndex = rgba.ResolvePixel(fire.Terminal.Paint.FromIndex(4));
        GfxCheck(viaIndex.Rgba == rgba.Palette.GetPacked(4), "RGBA-Framebuffer: ein Index wird ueber die Palette zur Farbe");
        GfxCheck(rgba.ResolvePixel(fire.Terminal.Paint.FromRgba(red)).Rgba == red.Packed, "RGBA-Framebuffer: ein direkter Wert bleibt");
        GfxCheck(idx.ResolvePixel(fire.Terminal.Paint.FromIndex(4)).Index == 4, "Palette-Framebuffer: ein Index bleibt");
        byte nearest = idx.ResolvePixel(fire.Terminal.Paint.FromRgba(fire.Terminal.PixelColor.FromRgb(250, 3, 3))).Index;
        GfxCheck(idx.Palette.GetColor(nearest).R >= 200 && idx.Palette.GetColor(nearest).G <= 50 && idx.Palette.GetColor(nearest).B <= 50, "Palette-Framebuffer: ein direkter Wert wird der naechste Palette-Eintrag (rot -> rotlich)");
        GfxCheck(idx.Palette.FindNearest(fire.Terminal.PixelColor.FromRgb(0, 0, 0)) == 0 && idx.Palette.FindNearest(idx.Palette.GetColor(200)) <= 200 && idx.Palette.GetPacked(idx.Palette.FindNearest(idx.Palette.GetColor(200))) == idx.Palette.GetPacked(200), "FindNearest findet eine exakt vorhandene Farbe");
    }

    // ---- Text and rectangles: palette framebuffer == RGBA framebuffer with the same palette colours ----
    {
        var font = new fire.Terminal.IntegratedGlyphFont();
        var fbRgba = new fire.Terminal.Framebuffer(203, 97);
        var fbIdx = new fire.Terminal.Framebuffer(203, 97, IDX);
        var cRgba = new fire.Terminal.Renderer(fbRgba, font);
        var cIdx = new fire.Terminal.Renderer(fbIdx, font);
        foreach (var cv in new[] { cRgba, cIdx })
        {
            cv.SetColor(fire.Terminal.Paint.FromIndex(14), fire.Terminal.Paint.FromIndex(1));
            cv.Clear();
            cv.FillRect(5, 5, 40, 20, Bsh(12));
            cv.DrawRect(2, 2, 60, 30, Pn(10));
            cv.DrawLine(0, 0, 202, 96, Pn(9));
            cv.DrawText(7, 40, "Hallo Welt", Bsh(15));
            cv.DrawText(100, 80, "ragt hinaus", Bsh(13), Bsh(4));
            cv.Locate(0, 0);
            cv.Print(string.Join("\n", Enumerable.Range(0, 12).Select(n => new string((char)('A' + n % 26), 8 + n))));
            cv.SetPixel(1, 1, fire.Terminal.Paint.FromIndex(200));
            cv.FillRect(170, 2, 20, 10, Bsh(12));
        }
        fbIdx.Resolve();
        GfxCheck(fbRgba.Pixels.SequenceEqual(fbIdx.Pixels), "Clear/FillRect/DrawRect/DrawLine/DrawText/Print/Scrollen: Palette-Framebuffer zeigt dieselben Pixel wie der RGBA-Framebuffer");

        // On a palette change the palette framebuffer changes, the RGBA framebuffer does not
        fbIdx.Palette.SetColor(12, unchecked((int)fire.Terminal.PixelColor.FromRgb(1, 2, 3).Packed));
        fbIdx.Resolve();
        GfxCheck(fbIdx.GetPixel(175, 5).Packed == fire.Terminal.PixelColor.FromRgb(1, 2, 3).Packed && fbRgba.GetPixel(175, 5).Packed == fbRgba.Palette.GetPacked(12), "Palette-Animation wirkt nur im Palette-Framebuffer");

        // the palette belongs to the framebuffer, not to the console
        GfxCheck(ReferenceEquals(cIdx.Palette, fbIdx.Palette), "Renderer.Palette ist die des Ziel-Framebuffers");

        // Index colour stays an index: a later palette change recolours NEWLY drawn text
        cRgba.SetColor(fire.Terminal.Paint.FromIndex(3), null);
        fbRgba.Palette.SetColor(3, unchecked((int)fire.Terminal.PixelColor.FromRgb(9, 8, 7).Packed));
        cRgba.DrawText(0, 90, "x", Bsh(3));
        bool found = false;
        for (int y = 90; y < 97 && !found; y++) for (int x = 0; x < 8; x++) if (fbRgba.GetPixel(x, y).Packed == fire.Terminal.PixelColor.FromRgb(9, 8, 7).Packed) { found = true; break; }
        GfxCheck(found, "ein Palette-Index wird erst beim Zeichnen aufgeloest");
    }

    // ---- Circle and ellipse ----
    {
        foreach (var mode in new[] { RGBA, IDX })
        {
            for (int r = 0; r <= 12; r++)
            {
                var fb = new fire.Terminal.Framebuffer(60, 60, mode);
                Shp.FillCircle(fb, 30, 30, r, 5);
                var filled = Lit(fb);
                var expected = new HashSet<(int, int)>();
                for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) if (dx * dx + dy * dy <= r * r + r) expected.Add((30 + dx, 30 + dy));
                if (!filled.SetEquals(expected)) { GfxCheck(false, $"FillCircle r={r} [{mode}]: Flaeche = {{dx^2+dy^2 <= r^2+r}}"); break; }
                if (r == 12) GfxCheck(true, $"FillCircle r=0..12 [{mode}]: Flaeche = {{dx^2+dy^2 <= r^2+r}}");
            }
        }
        var fbC = new fire.Terminal.Framebuffer(60, 60);
        Shp.FillCircle(fbC, 30, 30, 10, 5);
        var area = Lit(fbC);
        var fbO = new fire.Terminal.Framebuffer(60, 60);
        Shp.Circle(fbO, 30, 30, 10, 5);
        var ring = Lit(fbO);
        GfxCheck(ring.IsSubsetOf(area) && ring.Count > 30 && ring.Count < area.Count, "Circle: die Linie liegt in der Flaeche und ist ein Ring");
        // the ring is 4-symmetric and encloses the area: no inner area point has a neighbour outside the area without itself lying on the ring
        bool symmetric = ring.All(p => ring.Contains((60 - p.Item1, p.Item2)) && ring.Contains((p.Item1, 60 - p.Item2)) && ring.Contains((p.Item2, p.Item1)));
        GfxCheck(symmetric, "Circle: symmetrisch (Spiegelungen und Diagonale)");
        bool closed = area.All(p => ring.Contains(p) || new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.All(d => area.Contains((p.Item1 + d.Item1, p.Item2 + d.Item2))));
        GfxCheck(closed, "Circle: jedes Flaechenpixel am Rand liegt auf der Linie (keine Luecken)");

        var fbE = new fire.Terminal.Framebuffer(80, 60);
        Shp.FillEllipse(fbE, 40, 30, 20, 8, 5);
        var ell = Lit(fbE);
        GfxCheck(ell.Contains((40 - 20, 30)) && ell.Contains((40 + 20, 30)) && ell.Contains((40, 30 - 8)) && ell.Contains((40, 30 + 8)) && !ell.Contains((40 - 21, 30)) && !ell.Contains((40, 30 + 9)),
            "FillEllipse: Halbachsen rx=20, ry=8 treffen genau die Spitzen");
        var fbE2 = new fire.Terminal.Framebuffer(80, 60);
        Shp.Ellipse(fbE2, 40, 30, 20, 8, 5);
        var ellRing = Lit(fbE2);
        GfxCheck(ellRing.IsSubsetOf(ell) && ellRing.Contains((20, 30)) && ellRing.Contains((40, 22)) && !ellRing.Contains((40, 30)), "Ellipse: Linie in der Flaeche, Mitte frei");
        var fbL = new fire.Terminal.Framebuffer(30, 30);
        Shp.Ellipse(fbL, 15, 15, 6, 0, 5);
        GfxCheck(Lit(fbL).SetEquals(Enumerable.Range(9, 13).Select(x => (x, 15))), "Ellipse mit ry=0: eine waagerechte Linie");
        var fbV = new fire.Terminal.Framebuffer(30, 30);
        Shp.FillEllipse(fbV, 15, 15, 0, 4, 5);
        GfxCheck(Lit(fbV).SetEquals(Enumerable.Range(11, 9).Select(y => (15, y))), "FillEllipse mit rx=0: eine senkrechte Linie");
        var fbN = new fire.Terminal.Framebuffer(30, 30);
        Shp.FillCircle(fbN, 15, 15, -1, 5);
        Shp.Circle(fbN, -100, -100, 20, 5);
        Shp.FillCircle(fbN, 15, 15, int.MaxValue, 5);
        GfxCheck(true, "negativer Radius, Kreis ausserhalb und riesiger Radius werfen nicht");
    }

    // ---- Triangle and polygon ----
    {
        var fb = new fire.Terminal.Framebuffer(40, 40);
        Shp.FillTriangle(fb, 5, 5, 25, 5, 5, 25, 5);
        var tri = Lit(fb);
        GfxCheck(tri.Contains((5, 5)) && tri.Contains((25, 5)) && tri.Contains((5, 25)) && tri.Contains((10, 10)) && !tri.Contains((20, 20)) && !tri.Contains((26, 5)) && !tri.Contains((4, 5)), "FillTriangle: Ecken und Inneres, nicht ausserhalb");
        bool rows = true;
        for (int y = 5; y <= 25; y++) { int n = tri.Count(p => p.Item2 == y); if (Math.Abs(n - (26 - (y - 5) - 5 + 1)) > 1) rows = false; }
        GfxCheck(rows, "FillTriangle: Zeilenbreiten wie bei der Geraden (rechtwinkliges Dreieck)");
        var fbT = new fire.Terminal.Framebuffer(40, 40);
        Shp.Triangle(fbT, 5, 5, 25, 5, 5, 25, 5);
        var outline = Lit(fbT);
        GfxCheck(outline.IsSubsetOf(tri) && !outline.Contains((10, 10)) && outline.Contains((15, 5)) && outline.Contains((5, 15)), "Triangle: nur der Umriss, in der Flaeche enthalten");

        var fbR = new fire.Terminal.Framebuffer(40, 40);
        Shp.FillPolygon(fbR, new[] { 4, 6, 20, 6, 20, 15, 4, 15 }, 5);
        var fbR2 = new fire.Terminal.Framebuffer(40, 40);
        fbR2.FillRect(4, 6, 17, 10, Idx(fbR2, 5));
        GfxCheck(Lit(fbR).SetEquals(Lit(fbR2)), "FillPolygon eines Rechtecks == FillRect (Randpixel gehoeren dazu)");

        // Even-odd: a star made of a pentagon (pentagram) has an empty centre
        var fbS = new fire.Terminal.Framebuffer(60, 60);
        int[] star = { 30, 3, 47, 55, 3, 22, 57, 22, 13, 55 };
        Shp.FillPolygon(fbS, star, 5);
        var starSet = Lit(fbS);
        GfxCheck(starSet.Contains((30, 12)) && !starSet.Contains((30, 30)) && starSet.Count > 200, "FillPolygon: Even-Odd (das Zentrum eines Pentagramms bleibt leer)");

        var fbP = new fire.Terminal.Framebuffer(40, 40);
        Shp.Polygon(fbP, new[] { 5, 5, 30, 5, 30, 30 }, 5, closed: false);
        var open = Lit(fbP);
        GfxCheck(open.Contains((30, 20)) && !open.Contains((15, 18)) && open.Count == 26 + 25, "Polygon offen: Kantenzug ohne Schlusslinie");
        Shp.Polygon(fbP, new int[0], 5);
        Shp.FillPolygon(fbP, new[] { 1, 1, 9, 9 }, 5);
        Shp.FillPolygon(fbP, new[] { 1, 1, 9, 9, 7 }, 5);
        GfxCheck(true, "zu wenige Punkte / ungerade Punktzahl werfen nicht");

        var fbBig = new fire.Terminal.Framebuffer(20, 20);
        Shp.FillTriangle(fbBig, -1000000, -1000000, 1000000, 5, 5, 1000000, 5);
        Shp.Line(fbBig, int.MinValue, 0, int.MaxValue, 7, 5);
        GfxCheck(true, "riesige Koordinaten: kein Ueberlauf, kein Absturz");
    }

    // ---- Flaechenfuellung ----
    {
        foreach (var mode in new[] { RGBA, IDX })
        {
            var fb = new fire.Terminal.Framebuffer(30, 20, mode);
            Shp.Rect(fb, 5, 5, 15, 10, 7);
            Shp.FloodFill(fb, 10, 10, 3);
            int inside = 0, outside = 0, wall = 0;
            for (int y = 0; y < 20; y++) for (int x = 0; x < 30; x++)
            {
                var v = fb.GetIndex(x, y);
                bool isIn = x > 5 && x < 19 && y > 5 && y < 14;
                if (isIn && v == 3) inside++;
                if (!isIn && v == 3) outside++;
                if (v == 7) wall++;
            }
            GfxCheck(inside == 13 * 8 && outside == 0 && wall == 2 * 15 + 2 * 8, $"FloodFill fuellt genau das Innere des Rahmens [{mode}]");
            Shp.FloodFill(fb, 0, 0, 3);
            GfxCheck(fb.GetIndex(0, 0) == 3 && fb.GetIndex(29, 19) == 3 && fb.GetIndex(5, 5) == 7, $"FloodFill aussen: fuellt den Rest, der Rahmen bleibt [{mode}]");
            Shp.FloodFill(fb, 0, 0, 3); // already filled: nothing
            Shp.FloodFill(fb, -5, 100, 3);
        }
        var fbB = new fire.Terminal.Framebuffer(20, 20, IDX);
        Shp.Rect(fbB, 2, 2, 10, 10, 7);
        Shp.Line(fbB, 4, 4, 9, 4, 2); // a different colour in the interior
        Shp.FloodFillBorder(fbB, 5, 6, 3, 7);
        GfxCheck(fbB.GetIndex(5, 4) == 3 && fbB.GetIndex(5, 6) == 3 && fbB.GetIndex(2, 2) == 7 && fbB.GetIndex(15, 15) == 0, "FloodFillBorder: fuellt bis zur Randfarbe, auch ueber andere Farben hinweg");
        // large area: no stack overflow
        var fbHuge = new fire.Terminal.Framebuffer(600, 600, IDX);
        Shp.FloodFill(fbHuge, 0, 0, 4);
        GfxCheck(fbHuge.GetIndex(599, 599) == 4 && fbHuge.GetIndex(300, 300) == 4, "FloodFill einer ganzen 600x600-Flaeche");
        // Spirale: lange, verwinkelte Flaeche
        var fbSp = new fire.Terminal.Framebuffer(64, 64);
        for (int i = 2; i < 60; i += 4) Shp.Rect(fbSp, i, i, 64 - 2 * i, 64 - 2 * i, 7);
        Shp.FloodFill(fbSp, 0, 0, 2);
        GfxCheck(fbSp.GetIndex(1, 1) == 2 && fbSp.GetIndex(3, 3) == 0, "FloodFill bleibt hinter einer Wand");
    }

    // ---- Blit ----
    {
        // source 4x4 (RGBA), every pixel unique
        var src = new fire.Terminal.Framebuffer(4, 4);
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) src.SetPixel(x, y, new fire.Terminal.PixelColor((byte)(x * 10 + 10), (byte)(y * 10 + 10), 5, 255));
        var dst = new fire.Terminal.Framebuffer(10, 10);
        fire.Terminal.Blitter.Blit(dst, src, 3, 2);
        GfxCheck(dst.GetPixel(3, 2).Packed == src.GetPixel(0, 0).Packed && dst.GetPixel(6, 5).Packed == src.GetPixel(3, 3).Packed && dst.GetPixel(2, 2).Packed == 0 && dst.GetPixel(7, 5).Packed == 0, "Blit: ganzes Bild an eine Position");

        var d2 = new fire.Terminal.Framebuffer(10, 10);
        fire.Terminal.Blitter.Blit(d2, src, 1, 1, 2, 2, 0, 0, 2, 2);
        GfxCheck(d2.GetPixel(0, 0).Packed == src.GetPixel(1, 1).Packed && d2.GetPixel(1, 1).Packed == src.GetPixel(2, 2).Packed && d2.GetPixel(2, 0).Packed == 0, "Blit: Ausschnitt");

        var d3 = new fire.Terminal.Framebuffer(10, 10);
        fire.Terminal.Blitter.Blit(d3, src, 0, 0, 4, 4, 0, 0, 8, 8);
        GfxCheck(d3.GetPixel(0, 0).Packed == src.GetPixel(0, 0).Packed && d3.GetPixel(1, 1).Packed == src.GetPixel(0, 0).Packed && d3.GetPixel(2, 2).Packed == src.GetPixel(1, 1).Packed && d3.GetPixel(7, 7).Packed == src.GetPixel(3, 3).Packed, "Blit: 2x vergroessert (nächster Nachbar)");
        var d4 = new fire.Terminal.Framebuffer(10, 10);
        fire.Terminal.Blitter.Blit(d4, src, 0, 0, 4, 4, 0, 0, 2, 2);
        GfxCheck(d4.GetPixel(0, 0).Packed == src.GetPixel(1, 1).Packed || d4.GetPixel(0, 0).Packed == src.GetPixel(0, 0).Packed, "Blit: halbiert nimmt ein Quellpixel je Zielpixel");

        var d5 = new fire.Terminal.Framebuffer(10, 10);
        fire.Terminal.Blitter.Blit(d5, src, 0, 0, 4, 4, 0, 0, -4, 4);
        GfxCheck(d5.GetPixel(0, 0).Packed == src.GetPixel(3, 0).Packed && d5.GetPixel(3, 3).Packed == src.GetPixel(0, 3).Packed, "Blit: negative Zielbreite spiegelt waagerecht");
        var d6 = new fire.Terminal.Framebuffer(10, 10);
        fire.Terminal.Blitter.Blit(d6, src, 0, 0, 4, 4, 0, 0, 4, -4);
        GfxCheck(d6.GetPixel(0, 0).Packed == src.GetPixel(0, 3).Packed && d6.GetPixel(3, 3).Packed == src.GetPixel(3, 0).Packed, "Blit: negative Zielhoehe spiegelt senkrecht");

        // Clipping: target partly outside, source outside, empty sizes
        var d7 = new fire.Terminal.Framebuffer(10, 10);
        fire.Terminal.Blitter.Blit(d7, src, -2, -2);
        fire.Terminal.Blitter.Blit(d7, src, 8, 8);
        fire.Terminal.Blitter.Blit(d7, src, 2, 2, 100, 100, 0, 0, 4, 4);
        fire.Terminal.Blitter.Blit(d7, src, 0, 0, 0, 0, 0, 0, 4, 4);
        fire.Terminal.Blitter.Blit(d7, src, 0, 0, 4, 4, 0, 0, 0, 4);
        fire.Terminal.Blitter.Blit(d7, src, -50, -50, 4, 4, 0, 0, 4, 4);
        GfxCheck(d7.GetPixel(0, 0).Packed == src.GetPixel(2, 2).Packed && d7.GetPixel(9, 9).Packed == src.GetPixel(1, 1).Packed, "Blit: Beschneiden an Quelle und Ziel, leere Groessen werfen nicht");

        // Transparent / Blend (RGBA source with alpha)
        var sprite = new fire.Terminal.Framebuffer(2, 1);
        sprite.SetPixel(0, 0, new fire.Terminal.PixelColor(200, 0, 0, 255));
        sprite.SetPixel(1, 0, new fire.Terminal.PixelColor(0, 0, 0, 0));
        var bg = new fire.Terminal.Framebuffer(2, 1);
        bg.Clear(new fire.Terminal.PixelColor(10, 20, 30, 255));
        fire.Terminal.Blitter.Blit(bg, sprite, 0, 0, fire.Terminal.BlitMode.Transparent);
        GfxCheck(bg.GetPixel(0, 0).R == 200 && bg.GetPixel(1, 0).Packed == new fire.Terminal.PixelColor(10, 20, 30, 255).Packed, "Blit Transparent: Alpha-0-Pixel bleiben unberuehrt");
        fire.Terminal.Blitter.Blit(bg, sprite, 0, 0, fire.Terminal.BlitMode.Copy);
        GfxCheck(bg.GetPixel(1, 0).Packed == 0, "Blit Copy: kopiert auch durchsichtige Pixel");
        var half = new fire.Terminal.Framebuffer(1, 1);
        half.SetPixel(0, 0, new fire.Terminal.PixelColor(200, 100, 0, 128));
        var base1 = new fire.Terminal.Framebuffer(1, 1);
        base1.Clear(new fire.Terminal.PixelColor(0, 0, 100, 255));
        fire.Terminal.Blitter.Blit(base1, half, 0, 0, fire.Terminal.BlitMode.Blend);
        var mixed = base1.GetPixel(0, 0);
        GfxCheck(Math.Abs(mixed.R - 100) <= 2 && Math.Abs(mixed.G - 50) <= 2 && Math.Abs(mixed.B - 50) <= 2 && mixed.A == 255, "Blit Blend: halbdurchsichtig wird nach Alpha gemischt");

        // palette source: colour key / TransparentIndex, via the palette into an RGBA framebuffer
        var pal = new fire.Terminal.Framebuffer(3, 1, IDX);
        pal.Indices![0] = 5; pal.Indices[1] = 0; pal.Indices[2] = 7; pal.MarkDirty();
        var target = new fire.Terminal.Framebuffer(3, 1);
        target.Clear(new fire.Terminal.PixelColor(1, 1, 1, 255));
        fire.Terminal.Blitter.Blit(target, pal, 0, 0, fire.Terminal.BlitMode.Transparent, 0);
        GfxCheck(target.GetPixel(0, 0).Packed == pal.Palette.GetPacked(5) && target.GetPixel(1, 0).R == 1 && target.GetPixel(2, 0).Packed == pal.Palette.GetPacked(7), "Blit Palette->RGBA mit Farbschluessel 0");
        pal.TransparentIndex = 7;
        var target2 = new fire.Terminal.Framebuffer(3, 1);
        target2.Clear(new fire.Terminal.PixelColor(1, 1, 1, 255));
        fire.Terminal.Blitter.Blit(target2, pal, 0, 0, fire.Terminal.BlitMode.Transparent);
        GfxCheck(target2.GetPixel(2, 0).R == 1 && target2.GetPixel(1, 0).Packed == pal.Palette.GetPacked(0), "Blit Transparent nimmt ohne Farbschluessel den TransparentIndex der Quelle");

        // Palette -> palette: same palette = indices directly; other palette = nearest entry
        var palDst = new fire.Terminal.Framebuffer(3, 1, IDX);
        fire.Terminal.Blitter.Blit(palDst, pal, 0, 0);
        GfxCheck(palDst.Indices!.SequenceEqual(pal.Indices), "Blit Palette->Palette (gleiche Palette): Indizes unveraendert");
        var palDst2 = new fire.Terminal.Framebuffer(3, 1, IDX);
        var shifted = new uint[256];
        pal.Palette.CopyPacked(shifted);
        palDst2.Palette.SetAll(shifted.Reverse().ToArray()); // entry i has the colour of 255-i there
        fire.Terminal.Blitter.Blit(palDst2, pal, 0, 0);
        GfxCheck(palDst2.Palette.GetPacked(palDst2.Indices![0]) == pal.Palette.GetPacked(5) && palDst2.Palette.GetPacked(palDst2.Indices[2]) == pal.Palette.GetPacked(7), "Blit Palette->Palette (andere Palette): gleiche FARBE, anderer Index");

        // RGBA -> palette: nearest entry of the destination palette
        var truecolor = new fire.Terminal.Framebuffer(2, 1);
        truecolor.SetPixel(0, 0, fire.Terminal.PixelColor.FromRgb(255, 255, 255));
        truecolor.SetPixel(1, 0, new fire.Terminal.PixelColor(0, 0, 0, 0));
        var palTarget = new fire.Terminal.Framebuffer(2, 1, IDX);
        palTarget.Indices![0] = 3; palTarget.Indices[1] = 3; palTarget.MarkDirty();
        fire.Terminal.Blitter.Blit(palTarget, truecolor, 0, 0, fire.Terminal.BlitMode.Transparent);
        GfxCheck(palTarget.Palette.GetColor(palTarget.Indices[0]).Packed == fire.Terminal.PixelColor.FromRgb(255, 255, 255).Packed && palTarget.Indices[1] == 3, "Blit RGBA->Palette: naechster Palette-Eintrag, Alpha 0 uebersprungen");

        // same buffer, overlapping: like a copy
        var self = new fire.Terminal.Framebuffer(8, 1);
        for (int x = 0; x < 8; x++) self.SetPixel(x, 0, new fire.Terminal.PixelColor((byte)(x + 1), 0, 0, 255));
        fire.Terminal.Blitter.Blit(self, self, 0, 0, 6, 1, 2, 0, 6, 1);
        GfxCheck(Enumerable.Range(0, 8).Select(x => (int)self.GetPixel(x, 0).R).SequenceEqual(new[] { 1, 2, 1, 2, 3, 4, 5, 6 }), "Blit auf sich selbst (ueberlappend) liest vom Stand vor dem Kopieren");
    }

    // ---- Manager: Modus, Rohbytes, Palette ----
    {
        var mgr = new fire.Terminal.FramebufferManager();
        int a = mgr.CreateFramebuffer(4, 2, IDX);
        int b = mgr.CreateFramebuffer(4, 2);
        GfxCheck(mgr.GetMode(a) == IDX && mgr.GetMode(b) == RGBA && mgr.GetByteCount(a) == 8 && mgr.GetByteCount(b) == 32, "FramebufferManager: Modus und Rohdatengroesse (1 bzw. 4 Byte je Pixel)");
        mgr.WriteByte(a, 5, 77);
        GfxCheck(mgr.ReadByte(a, 5) == 77 && mgr.GetFramebuffer(a).GetIndex(1, 1) == 77, "ReadByte/WriteByte im Palette-Modus: ein Byte = ein Index");
        var bytes = Enumerable.Range(0, 8).Select(i => (byte)(i * 3)).ToArray();
        mgr.WriteBytes(a, bytes);
        GfxCheck(mgr.ReadBytes(a).SequenceEqual(bytes), "ReadBytes/WriteBytes im Palette-Modus");
        try { mgr.WriteBytes(a, new byte[32]); GfxCheck(false, "WriteBytes mit falscher Laenge wird abgelehnt"); }
        catch (ArgumentException) { GfxCheck(true, "WriteBytes mit falscher Laenge wird abgelehnt"); }
        try { mgr.ReadByte(a, 8); GfxCheck(false, "ReadByte ausserhalb wird abgelehnt"); }
        catch (ArgumentOutOfRangeException) { GfxCheck(true, "ReadByte ausserhalb wird abgelehnt"); }

        mgr.SetPaletteColor(a, 10, unchecked((int)fire.Terminal.PixelColor.FromRgb(11, 22, 33).Packed));
        var pal768 = mgr.ReadPalette(a);
        GfxCheck(pal768.Length == 768 && pal768[30] == 11 && pal768[31] == 22 && pal768[32] == 33 && mgr.ReadPalette(a, true).Length == 1024, "ReadPalette: 768 Byte RGB bzw. 1024 Byte RGBA");
        var newPal = new byte[768];
        for (int i = 0; i < 256; i++) { newPal[i * 3] = (byte)i; newPal[i * 3 + 1] = (byte)(255 - i); newPal[i * 3 + 2] = 9; }
        mgr.WritePalette(b, newPal);
        var got = mgr.GetFramebuffer(b).Palette.GetColor(100);
        GfxCheck(got.R == 100 && got.G == 155 && got.B == 9 && got.A == 255, "WritePalette (768 Byte RGB, Alpha 255)");
        var rgbaPal = new byte[1024];
        rgbaPal[4 * 7 + 3] = 40; rgbaPal[4 * 7] = 1;
        mgr.WritePalette(b, rgbaPal);
        GfxCheck(mgr.GetFramebuffer(b).Palette.GetColor(7).A == 40, "WritePalette (1024 Byte RGBA)");
        try { mgr.WritePalette(b, new byte[100]); GfxCheck(false, "WritePalette mit falscher Laenge wird abgelehnt"); }
        catch (ArgumentException) { GfxCheck(true, "WritePalette mit falscher Laenge wird abgelehnt"); }
        try { mgr.SetPaletteColor(a, 256, 0); GfxCheck(false, "Palette-Index 256 wird abgelehnt"); }
        catch (ArgumentOutOfRangeException) { GfxCheck(true, "Palette-Index 256 wird abgelehnt"); }

        // RendererManager: colour specifications as numbers (brushes and pens are passed as an ID)
        var cm = new fire.Terminal.RendererManager(mgr, new fire.Terminal.IntegratedGlyphFont());
        int fbId = mgr.CreateFramebuffer(40, 20, IDX);
        int con = cm.CreateRenderer(fbId);
        int bI9 = cm.CreateSolidBrush(9);
        cm.FillRect(con, 0, 0, 10, 10, bI9);
        cm.SetPixel(con, 12, 12, 33);
        int bRed = cm.CreateSolidBrush(unchecked((int)0xFF0000FFu)); // direct value (red) -> nearest palette entry
        cm.FillRect(con, 20, 0, 5, 5, bRed);
        GfxCheck(mgr.GetFramebuffer(fbId).GetIndex(3, 3) == 9 && mgr.GetFramebuffer(fbId).GetIndex(12, 12) == 33 && cm.GetPixelIndex(con, 12, 12) == 33
            && mgr.GetFramebuffer(fbId).Palette.GetColor(mgr.GetFramebuffer(fbId).GetIndex(22, 2)).R >= 170, "RendererManager: 0-255 = Palette-Index, sonst direkter Wert");
        cm.SetColor(con, 14, 1);
        cm.Print(con, "Hi");
        GfxCheck(Enumerable.Range(0, 8).Any(x => Enumerable.Range(0, 14).Any(y => mgr.GetFramebuffer(fbId).GetIndex(x, y) == 14)), "RendererManager.SetColor mit Palette-Indizes (Print schreibt Index 14)");
        cm.DrawText(con, 0, 10, "T", cm.CreateSolidBrush(15), 0);
        GfxCheck(true, "DrawText mit Palette-Index und ohne Hintergrund");
        int[] bs = Enumerable.Range(0, 10).Select(i => cm.CreateSolidBrush(i)).ToArray();
        int[] ps = Enumerable.Range(0, 10).Select(i => cm.CreatePen(i, 1, 0)).ToArray();
        cm.FillCircle(con, 30, 12, 4, bs[6]); cm.DrawCircle(con, 30, 12, 6, ps[7]); cm.FillEllipse(con, 10, 15, 5, 2, bs[8]); cm.DrawEllipse(con, 10, 15, 6, 3, ps[9]);
        cm.FillTriangle(con, 1, 1, 8, 1, 1, 8, bs[2]); cm.DrawTriangle(con, 1, 1, 8, 1, 1, 8, ps[3]);
        cm.FillPolygon(con, new[] { 20, 10, 30, 10, 25, 18 }, bs[4]); cm.DrawPolygon(con, new[] { 20, 10, 30, 10, 25, 18 }, ps[5], true);
        cm.FloodFill(con, 35, 2, bs[6]); cm.FloodFillBorder(con, 35, 2, bs[7], 6);
        cm.DrawPoint(con, 3, 17, ps[2]); cm.DrawLine(con, 0, 19, 39, 19, ps[3]); cm.DrawPath(con, new[] { 0, 18, 10, 18, 10, 16 }, ps[4], false); cm.DrawRect(con, 30, 2, 6, 6, ps[5]);
        GfxCheck(mgr.GetFramebuffer(fbId).GetIndex(30, 12) != 0, "RendererManager: Kreis/Ellipse/Dreieck/Polygon/FloodFill/Punkt/Linie/Pfad laufen im Palette-Framebuffer");
        int src2 = mgr.CreateFramebuffer(4, 4, IDX);
        mgr.GetFramebuffer(src2).FillRect(0, 0, 4, 4, mgr.GetFramebuffer(src2).ResolvePixel(fire.Terminal.Paint.FromIndex(44)));
        cm.Blit(con, src2, 0, 0, 4, 4, 30, 14, 4, 4, 0, -1);
        GfxCheck(mgr.GetFramebuffer(fbId).GetIndex(31, 15) == 44, "RendererManager.Blit kopiert einen anderen Framebuffer");
        GfxCheck(cm.GetBrushColor(bI9) == 9 && cm.GetPenColor(ps[3]) == 3 && cm.GetPenWidth(ps[3]) == 1, "Pinsel und Stifte: Farbe und Breite lesen");
        cm.SetPenWidth(ps[3], 3); cm.SetPenColor(ps[3], 5); cm.SetBrushColor(bI9, 7);
        GfxCheck(cm.GetPenWidth(ps[3]) == 3 && cm.GetPenColor(ps[3]) == 5 && cm.GetBrushColor(bI9) == 7 && cm.DestroyBrush(bI9) && !cm.DestroyBrush(bI9), "Pinsel und Stifte: Eigenschaften aendern, zerstoeren");
    }

    // ---- Window: Tick computes the visible image of a palette framebuffer (Resolve) before the renderer gets it ----
    {
        var mgr = new fire.Terminal.FramebufferManager();
        int fbId = mgr.CreateFramebuffer(8, 4, IDX);
        var fb = mgr.GetFramebuffer(fbId);
        var wm = new fire.Terminal.Windows.WindowManager(mgr, (l, v) => { }, () => new FakeRenderer());
        int win = wm.CreateWindow(fbId, "Test");
        fb.Plot(2, 1, Idx(fb, 9));
        wm.Tick(win);
        GfxCheck(fb.Pixels[1 * 8 + 2] == fb.Palette.GetPacked(9) && fb.Pixels[0] == fb.Palette.GetPacked(0), "Window.Tick: Palette-Framebuffer wird vor dem Anzeigen aufgeloest");
        fb.Palette.SetColor(9, unchecked((int)fire.Terminal.PixelColor.FromRgb(7, 8, 9).Packed));
        wm.Tick(win);
        GfxCheck(fb.Pixels[1 * 8 + 2] == fire.Terminal.PixelColor.FromRgb(7, 8, 9).Packed, "Window.Tick nach einer Palette-Aenderung (ohne dass sich ein Index aenderte)");
        wm.DestroyWindow(win);
    }

    // ---- Brush, Pen, Alpha-Blending ----
    {
        var font = new fire.Terminal.IntegratedGlyphFont();
        var P = (byte r, byte g, byte b, byte a) => fire.Terminal.Paint.FromRgba(new fire.Terminal.PixelColor(r, g, b, a));
        fire.Terminal.Paint Rgb(byte r, byte g, byte b) => P(r, g, b, 255);

        // Blending: alpha 255 = copy, 0 = nothing, in between blended (only in the 32-bit target), can be switched off
        {
            var fb = new fire.Terminal.Framebuffer(8, 4);
            var rd = new fire.Terminal.Renderer(fb, font);
            rd.FillRect(0, 0, 8, 4, new fire.Terminal.SolidBrush(Rgb(0, 0, 100)));
            rd.FillRect(0, 0, 2, 2, new fire.Terminal.SolidBrush(P(200, 100, 0, 128)));
            rd.FillRect(2, 0, 2, 2, new fire.Terminal.SolidBrush(P(200, 100, 0, 0)));
            rd.FillRect(4, 0, 2, 2, new fire.Terminal.SolidBrush(Rgb(9, 8, 7)));
            var mixed = fb.GetPixel(0, 0);
            GfxCheck(Math.Abs(mixed.R - 100) <= 2 && Math.Abs(mixed.G - 50) <= 2 && Math.Abs(mixed.B - 50) <= 2 && mixed.A == 255, "Alpha-Blending: Alpha 128 wird mit dem Untergrund gemischt");
            GfxCheck(fb.GetPixel(2, 0).B == 100 && fb.GetPixel(4, 0).R == 9, "Alpha 0 zeichnet nichts, Alpha 255 kopiert");
            rd.AlphaBlending = false;
            rd.FillRect(0, 2, 2, 1, new fire.Terminal.SolidBrush(P(200, 100, 0, 128)));
            GfxCheck(fb.GetPixel(0, 2).Packed == new fire.Terminal.PixelColor(200, 100, 0, 128).Packed, "ohne AlphaBlending wird die Farbe samt Alpha kopiert");
            rd.AlphaBlending = true;
            rd.Clear(fire.Terminal.Paint.FromIndex(0));
            GfxCheck(fb.GetPixel(5, 3).Packed == fb.Palette.GetPacked(0), "Clear(Paint) setzt ohne Mischen");

            // 8-bit target: from alpha 128 a copy (nearest entry), below that nothing
            var pal = new fire.Terminal.Framebuffer(4, 1, IDX);
            var rp = new fire.Terminal.Renderer(pal, font);
            rp.FillRect(0, 0, 4, 1, new fire.Terminal.SolidBrush(fire.Terminal.Paint.FromIndex(7)));
            rp.SetPixel(0, 0, P(255, 255, 255, 127));
            rp.SetPixel(1, 0, P(255, 255, 255, 128));
            GfxCheck(pal.Indices![0] == 7 && pal.Indices[1] != 7, "Palette-Ziel: Alpha 127 wird nicht gezeichnet, ab 128 kopiert");
            rp.AlphaBlending = false;
            rp.SetPixel(0, 0, P(255, 255, 255, 1));
            GfxCheck(pal.Indices[0] != 7, "Palette-Ziel ohne Blending: immer kopiert");
        }

        // Clipping rectangle: fills, lines, text and blit stay within it; ResetClip lifts it; Clear does not apply
        {
            var fb = new fire.Terminal.Framebuffer(40, 20);
            var rd = new fire.Terminal.Renderer(fb, font);
            var red = new fire.Terminal.SolidBrush(Rgb(255, 0, 0));
            var white = new fire.Terminal.Pen(Rgb(255, 255, 255));
            rd.SetClip(10, 5, 10, 8);
            rd.FillRect(0, 0, 40, 20, red);
            int inside = 0, outside = 0;
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 40; x++)
                {
                    bool isRed = fb.GetPixel(x, y).R == 255;
                    bool inClip = x >= 10 && x < 20 && y >= 5 && y < 13;
                    if (isRed && inClip) inside++;
                    if (isRed && !inClip) outside++;
                }
            GfxCheck(inside == 80 && outside == 0, "Clip: eine Flaeche wird auf das Rechteck beschnitten");
            rd.DrawLine(0, 6, 39, 6, white);
            rd.DrawCircle(15, 9, 30, white);
            int whiteOut = 0;
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 40; x++)
                    if (fb.GetPixel(x, y).B == 255 && fb.GetPixel(x, y).G == 255 && !(x >= 10 && x < 20 && y >= 5 && y < 13)) whiteOut++;
            GfxCheck(whiteOut == 0 && fb.GetPixel(10, 6).G == 255 && fb.GetPixel(19, 6).G == 255 && fb.GetPixel(9, 6).G == 0, "Clip: Linien und Kreise bleiben im Rechteck");
            var bright = new fire.Terminal.SolidBrush(Rgb(0, 255, 0));
            rd.DrawText(8, 5, "AB", bright, null);
            int greenOut = 0;
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 40; x++)
                    if (fb.GetPixel(x, y).G == 255 && fb.GetPixel(x, y).R == 0 && (x < 10 || y < 5 || y >= 13)) greenOut++;
            GfxCheck(greenOut == 0, "Clip: Text wird am Rechteck abgeschnitten (auch im Schnellpfad)");
            var spr = new fire.Terminal.Framebuffer(6, 6);
            new fire.Terminal.Renderer(spr, font).FillRect(0, 0, 6, 6, new fire.Terminal.SolidBrush(Rgb(0, 0, 255)));
            rd.Blit(spr, 0, 0, 6, 6, 17, 10, 6, 6);
            GfxCheck(fb.GetPixel(19, 10).B == 255 && fb.GetPixel(20, 10).B == 0 && fb.GetPixel(18, 13).B == 0 && fb.GetPixel(18, 12).B == 255, "Clip: Blit wird beschnitten");
            var clip = rd.GetClip();
            GfxCheck(clip == (10, 5, 10, 8), "Clip: GetClip liefert das Rechteck");
            rd.ResetClip();
            rd.SetPixel(0, 0, Rgb(1, 2, 3));
            GfxCheck(fb.GetPixel(0, 0).B == 3, "Clip: ResetClip hebt es auf");
            rd.SetClip(30, 15, 5, 3);
            rd.Clear(fire.Terminal.Paint.FromRgba(new fire.Terminal.PixelColor(9, 9, 9, 255)));
            GfxCheck(fb.GetPixel(0, 0).R == 9, "Clip: Clear gilt fuer den ganzen Framebuffer");
        }

        // Pen: width 1 == the simple line, wider pens stamp their tip
        {
            foreach (var mode in new[] { RGBA, IDX })
            {
                var a = new fire.Terminal.Framebuffer(40, 30, mode);
                var b = new fire.Terminal.Framebuffer(40, 30, mode);
                new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex(5)).DrawLine(new fire.Terminal.Surface(a, true), 2, 3, 35, 20);
                var ra = new fire.Terminal.Renderer(b, font);
                ra.DrawLine(2, 3, 35, 20, new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex(5), 1, fire.Terminal.PenShape.Square));
                GfxCheck(Lit(a).SetEquals(Lit(b)) && Lit(a).Count > 30, $"Pen Breite 1: Round == Square == die Bresenham-Linie [{mode}]");
            }
            var fb = new fire.Terminal.Framebuffer(30, 30);
            var rd = new fire.Terminal.Renderer(fb, font);
            rd.DrawPoint(10, 10, new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex(5), 3, fire.Terminal.PenShape.Square));
            var sq = Lit(fb);
            GfxCheck(sq.SetEquals(Enumerable.Range(9, 3).SelectMany(x => Enumerable.Range(9, 3).Select(y => (x, y)))), "Pen Quadrat Breite 3: ein 3x3-Stempel, mittig");
            var fr = new fire.Terminal.Framebuffer(30, 30);
            new fire.Terminal.Renderer(fr, font).DrawPoint(10, 10, new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex(5), 7, fire.Terminal.PenShape.Round));
            var disc = Lit(fr);
            GfxCheck(disc.Contains((10, 7)) && disc.Contains((7, 10)) && disc.Contains((13, 10)) && !disc.Contains((7, 7)) && !disc.Contains((13, 13)) && disc.Count > 30 && disc.Count < 49, "Pen rund Breite 7: Kreisscheibe (Ecken fehlen)");
            // a wide line is the union of the stamps along the line
            var fl = new fire.Terminal.Framebuffer(40, 20);
            var pen3 = new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex(5), 3, fire.Terminal.PenShape.Square);
            new fire.Terminal.Renderer(fl, font).DrawLine(5, 10, 30, 10, pen3);
            GfxCheck(Lit(fl).SetEquals(Enumerable.Range(4, 28).SelectMany(x => Enumerable.Range(9, 3).Select(y => (x, y)))), "Pen Breite 3: waagerechte Linie = 3 Zeilen von x-1 bis x+1");
            // Pfad, Umrisse
            var fp = new fire.Terminal.Framebuffer(30, 30);
            var rp = new fire.Terminal.Renderer(fp, font);
            rp.DrawPath(new[] { 2, 2, 20, 2, 20, 20 }, new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex(5)), false);
            var open = Lit(fp);
            GfxCheck(open.Contains((10, 2)) && open.Contains((20, 10)) && !open.Contains((10, 10)) && open.Count == 19 + 18, "DrawPath offen: Kantenzug");
            rp.Clear(fire.Terminal.Paint.FromIndex(0));
            rp.DrawPath(new[] { 2, 2, 20, 2, 20, 20 }, new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex(5)), true);
            GfxCheck(Lit(fp).Contains((10, 11)), "DrawPath geschlossen: mit Schlusslinie");
            // Pen-Eigenschaften
            var changing = new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex(5));
            changing.Width = 5; changing.Shape = fire.Terminal.PenShape.Square;
            var fc = new fire.Terminal.Framebuffer(20, 20);
            new fire.Terminal.Renderer(fc, font).DrawPoint(10, 10, changing);
            GfxCheck(Lit(fc).Count == 25, "Pen: Breite und Form nachtraeglich aendern rendert die Spitze neu");
            changing.Width = 100000;
            GfxCheck(changing.Width == fire.Terminal.Pen.MaxWidth, "Pen: die Breite wird begrenzt");
        }

        // semi-transparent pen: the overlapping stamps do NOT blend several times (the union is blended once)
        {
            var fb = new fire.Terminal.Framebuffer(30, 10);
            var rd = new fire.Terminal.Renderer(fb, font);
            rd.FillRect(0, 0, 30, 10, new fire.Terminal.SolidBrush(Rgb(0, 0, 0)));
            rd.DrawLine(2, 5, 25, 5, new fire.Terminal.Pen(P(200, 200, 200, 128), 5, fire.Terminal.PenShape.Square));
            uint inner = fb.GetPixel(10, 5).Packed, edge = fb.GetPixel(10, 3).Packed, start = fb.GetPixel(2, 5).Packed;
            GfxCheck(inner == edge && inner == start && fb.GetPixel(10, 5).R is >= 99 and <= 101, "halbdurchsichtiger breiter Stift: gleichmaessig gemischt, ohne dunklere Ueberlappung");
            var fr = new fire.Terminal.Framebuffer(30, 10);
            var rr = new fire.Terminal.Renderer(fr, font);
            rr.FillRect(0, 0, 30, 10, new fire.Terminal.SolidBrush(Rgb(0, 0, 0)));
            rr.DrawRect(3, 1, 20, 7, new fire.Terminal.Pen(P(200, 200, 200, 128), 1));
            GfxCheck(fr.GetPixel(3, 1).R == fr.GetPixel(10, 1).R && fr.GetPixel(3, 4).R == fr.GetPixel(3, 1).R, "halbdurchsichtiger Umriss: auch die Ecken nur einmal gemischt");
        }

        // Brush: fills including FloodFill, blended; FloodFill of a semi-transparent colour stays within the area
        {
            var fb = new fire.Terminal.Framebuffer(30, 20);
            var rd = new fire.Terminal.Renderer(fb, font);
            rd.FillRect(0, 0, 30, 20, new fire.Terminal.SolidBrush(Rgb(0, 0, 100)));
            rd.DrawRect(5, 5, 15, 10, new fire.Terminal.Pen(Rgb(255, 255, 255)));
            rd.FloodFill(10, 10, new fire.Terminal.SolidBrush(P(200, 0, 0, 128)));
            var inside = fb.GetPixel(10, 10);
            GfxCheck(Math.Abs(inside.R - 100) <= 2 && Math.Abs(inside.B - 50) <= 2 && fb.GetPixel(2, 2).R == 0 && fb.GetPixel(5, 5).R == 255, "FloodFill mit halbdurchsichtiger Farbe: die Flaeche wird einmal gemischt, Rahmen und Aussen bleiben");
            GfxCheck(Enumerable.Range(6, 13).All(x => Enumerable.Range(6, 8).All(y => fb.GetPixel(x, y).Packed == inside.Packed)), "FloodFill (gemischt): jedes Pixel der Flaeche gleich");
            rd.Fill(new fire.Terminal.SolidBrush(Rgb(1, 2, 3)));
            GfxCheck(fb.GetPixel(0, 0).Packed == new fire.Terminal.PixelColor(1, 2, 3).Packed && fb.GetPixel(29, 19).Packed == new fire.Terminal.PixelColor(1, 2, 3).Packed, "Renderer.Fill fuellt alles");
        }

        // Text: foreground and background as brushes, also semi-transparent
        {
            var fb = new fire.Terminal.Framebuffer(40, 20);
            var rd = new fire.Terminal.Renderer(fb, font);
            rd.FillRect(0, 0, 40, 20, new fire.Terminal.SolidBrush(Rgb(0, 0, 0)));
            rd.DrawText(0, 0, "A", new fire.Terminal.SolidBrush(P(255, 255, 255, 128)), new fire.Terminal.SolidBrush(P(0, 255, 0, 255)));
            var cell = Enumerable.Range(0, 8).SelectMany(x => Enumerable.Range(0, 14).Select(y => fb.GetPixel(x, y))).ToList();
            GfxCheck(cell.Any(c => c.R is >= 127 and <= 129 && c.G is >= 254) && cell.Any(c => c.R == 0 && c.G == 255), "DrawText: halbdurchsichtiger Vordergrund ueber deckendem Hintergrund (gemischt)");
            var f2 = new fire.Terminal.Framebuffer(40, 20);
            var r2 = new fire.Terminal.Renderer(f2, font);
            r2.DrawText(0, 0, "A", new fire.Terminal.SolidBrush(P(255, 255, 255, 255)), new fire.Terminal.SolidBrush(P(0, 255, 0, 0)));
            GfxCheck(Enumerable.Range(0, 8).SelectMany(x => Enumerable.Range(0, 14).Select(y => f2.GetPixel(x, y))).All(c => c.Packed == 0 || c.Packed == new fire.Terminal.PixelColor(255, 255, 255, 255).Packed), "DrawText: ein Hintergrund mit Alpha 0 ist keiner");
        }

        // Palette index and transparent colour: the index occupies only the R byte (alpha stays 0); the canonical transparent colour (0,1,0,0) = 256 is not an index
        {
            GfxCheck(fire.Terminal.Paint.FromArgument(14).IsIndex && fire.Terminal.Paint.FromArgument(0).IsIndex, "Zahl 0-255 ist ein Palette-Index (nur das R-Byte, Alpha 0)");
            GfxCheck(!fire.Terminal.Paint.FromArgument(fire.Terminal.Paint.Transparent).IsIndex && fire.Terminal.PixelColor.Transparent.Packed == 256 && fire.Terminal.PixelColor.Transparent.A == 0, "Transparent = (0,1,0,0) = 256: durchsichtig, aber kein Palette-Index");
            GfxCheck(fire.Terminal.Paint.ToArgument(0) == 256 && fire.Terminal.Paint.ToArgument(7) == 256 && fire.Terminal.Paint.ToArgument(0xFF102030u) == unchecked((int)0xFF102030u) && fire.Terminal.Paint.ToArgument(0x00102030u) == 0x00102030, "ToArgument: nur Werte, die als Index gelesen wuerden, werden zu Transparent");
            var fb = new fire.Terminal.Framebuffer(4, 2);
            var cv = new fire.Terminal.Renderer(fb, font);
            cv.FillRect(0, 0, 4, 2, new fire.Terminal.SolidBrush(Rgb(30, 40, 50)));
            // writing an empty pixel back (GetPixel -> SetPixel) leaves it transparent instead of palette black
            fb.SetPixel(1, 0, fire.Terminal.PixelColor.Transparent);
            int read = fire.Terminal.Paint.ToArgument(fb.GetPixel(1, 0).Packed);
            cv.SetPixel(2, 0, fire.Terminal.Paint.FromArgument(read));
            GfxCheck(read == 256 && fb.GetPixel(2, 0).Packed == new fire.Terminal.PixelColor(30, 40, 50, 255).Packed, "ein durchsichtiges Pixel als Zahl zurueckgeschrieben zeichnet nichts (kein Palette-Schwarz)");
        }

        // another target: an own IRenderTarget (here a section-free wrapper around two arrays)
        {
            var t = new ArrayTarget(6, 3);
            var rd = new fire.Terminal.Renderer(t, font);
            rd.FillRect(1, 1, 3, 1, new fire.Terminal.SolidBrush(Rgb(5, 6, 7)));
            GfxCheck(t.Pixels[1 * 6 + 1] == new fire.Terminal.PixelColor(5, 6, 7).Packed && t.Pixels[1 * 6 + 4] == 0, "Renderer zeichnet in jedes IRenderTarget");
        }
    }

    Console.WriteLine(gfxFailures == 0 ? "Alle Grafik-Pruefungen bestanden." : $"FEHLER: {gfxFailures} Grafik-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Images: decode PNG, BMP, GIF (test files from Pillow and own writers, see ImageFixtures)
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Bilder: PNG, BMP, GIF ===");
    int imgFailures = 0;
    void ImgCheck(bool ok, string what)
    {
        if (!ok) imgFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    const int IW = 13, IH = 7;
    // dieselben Formeln wie im Erzeugerskript
    uint Rgb(int x, int y) => (uint)((x * 19 + 3) % 256) | (uint)(((y * 35 + 5) % 256) << 8) | (uint)((((x * 7 + y * 13) * 3) % 256) << 16);
    uint Alpha(int x, int y) => (uint)((x * 37 + y * 91) % 256);
    uint PalEntry(int i) => (uint)((i * 40 + 10) % 256) | (uint)(((i * 70 + 20) % 256) << 8) | (uint)(((i * 110 + 30) % 256) << 16) | 0xFF000000u;
    int PalIndex(int x, int y, int n) => (x * 5 + y * 3) % n;
    uint Opaque(uint c) => c | 0xFF000000u;
    uint Gray(uint v) => v | (v << 8) | (v << 16);

    fire.Terminal.ImageData Load(string name) => fire.Terminal.ImageDecoder.Decode(ImageFixtures.Get(name));
    bool Truecolor(string name, Func<int, int, uint> expected, string? format = null)
    {
        try
        {
            var img = Load(name);
            if (img.IsIndexed || img.Width != IW || img.Height != IH || (format != null && img.Format != format)) return false;
            for (int y = 0; y < IH; y++) for (int x = 0; x < IW; x++)
                if (img.Pixels![y * IW + x] != expected(x, y)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }
    // indexed: the COLOUR of each pixel is right (an encoder may renumber the indices), optionally the indices themselves
    bool Indexed(string name, Func<int, int, uint> expectedColor, bool exactIndices = false, int n = 0, string? format = null)
    {
        try
        {
            var img = Load(name);
            if (!img.IsIndexed || img.Width != IW || img.Height != IH || (format != null && img.Format != format)) return false;
            for (int y = 0; y < IH; y++) for (int x = 0; x < IW; x++)
            {
                int pos = y * IW + x;
                if (img.Palette![img.Indices![pos]] != expectedColor(x, y)) return false;
                if (exactIndices && img.Indices[pos] != PalIndex(x, y, n)) return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    // ---- PNG: own encoder (all colour types and depths, all row filters) ----
    ImgCheck(Truecolor("png_rgba8", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24), "PNG"), "PNG RGBA 8 Bit (alle fuenf Zeilenfilter, IDAT in zwei Chunks)");
    ImgCheck(Truecolor("png_rgb8", (x, y) => Opaque(Rgb(x, y))), "PNG RGB 8 Bit");
    ImgCheck(Truecolor("png_rgba16", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "PNG RGBA 16 Bit (auf 8 Bit gekuerzt)");
    ImgCheck(Truecolor("png_rgb16", (x, y) => Opaque(Rgb(x, y))), "PNG RGB 16 Bit");
    ImgCheck(Truecolor("png_gray8", (x, y) => Opaque(Gray(Rgb(x, y) & 0xFF))), "PNG Grau 8 Bit");
    ImgCheck(Truecolor("png_gray16", (x, y) => Opaque(Gray(Rgb(x, y) & 0xFF))), "PNG Grau 16 Bit");
    ImgCheck(Truecolor("png_graya8", (x, y) => Gray(Rgb(x, y) & 0xFF) | (Alpha(x, y) << 24)), "PNG Grau+Alpha 8 Bit");
    ImgCheck(Truecolor("png_graya16", (x, y) => Gray(Rgb(x, y) & 0xFF) | (Alpha(x, y) << 24)), "PNG Grau+Alpha 16 Bit");
    foreach (var (d, mul) in new[] { (1, 255u), (2, 85u), (4, 17u) })
        ImgCheck(Truecolor($"png_gray{d}", (x, y) => Opaque(Gray((uint)((x * 3 + y) % (1 << d)) * mul))), $"PNG Grau {d} Bit");
    ImgCheck(Truecolor("png_gray4_adam7", (x, y) => Opaque(Gray((uint)((x * 3 + y) % 16) * 17u))), "PNG Grau 4 Bit, verschraenkt (Adam7)");
    ImgCheck(Truecolor("png_rgba8_adam7", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "PNG RGBA verschraenkt (Adam7)");
    ImgCheck(Truecolor("png_rgb16_adam7", (x, y) => Opaque(Rgb(x, y))), "PNG RGB 16 Bit verschraenkt");
    ImgCheck(Truecolor("png_gray8_key", (x, y) => (x == 0 ? 0u : 0xFF000000u) | Gray(Rgb(x, y) & 0xFF)), "PNG Grau mit tRNS-Schluessel (Pixel dieses Grauwerts durchsichtig)");
    ImgCheck(Truecolor("png_rgb8_key", (x, y) => (x == 0 && y == 0 ? 0u : 0xFF000000u) | Rgb(x, y)), "PNG RGB mit tRNS-Schluessel");
    {
        var one = Load("png_rgb8_1x1_adam7");
        ImgCheck(one.Width == 1 && one.Height == 1 && one.Pixels![0] == Opaque(Rgb(5, 5)), "PNG 1x1 verschraenkt (leere Durchgaenge)");
        var small = Load("png_rgb8_3x2_adam7");
        bool ok = small.Width == 3 && small.Height == 2;
        for (int y = 0; y < 2 && ok; y++) for (int x = 0; x < 3; x++) ok &= small.Pixels![y * 3 + x] == Opaque(Rgb(x, y));
        ImgCheck(ok, "PNG 3x2 verschraenkt (nicht alle Durchgaenge besetzt)");
    }
    foreach (var (d, n) in new[] { (1, 2), (2, 4), (4, 16), (8, 11) })
        ImgCheck(Indexed($"png_pal{d}", (x, y) => PalEntry(PalIndex(x, y, n)), true, n, "PNG") && Load($"png_pal{d}").TransparentIndex == -1, $"PNG Palette {d} Bit: indiziert, Indizes und Palette stimmen");
    ImgCheck(Indexed("png_pal2_adam7", (x, y) => PalEntry(PalIndex(x, y, 4)), true, 4), "PNG Palette 2 Bit verschraenkt");
    {
        var t = Load("png_pal8_trns");
        ImgCheck(t.IsIndexed && t.TransparentIndex == 1 && (t.Palette![1] >> 24) == 0 && (t.Palette[2] >> 24) == 128 && (t.Palette[3] >> 24) == 255, "PNG Palette mit tRNS: durchsichtiger Index und Alpha je Eintrag");
    }

    // ---- PNG: Pillow ----
    ImgCheck(Truecolor("pil_png_rgba", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "Pillow-PNG RGBA");
    ImgCheck(Truecolor("pil_png_rgba_optimized", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "Pillow-PNG RGBA (optimiert, adaptive Filter)");
    ImgCheck(Truecolor("pil_png_rgb", (x, y) => Opaque(Rgb(x, y))), "Pillow-PNG RGB");
    ImgCheck(Truecolor("pil_png_gray", (x, y) => Opaque(Gray(Rgb(x, y) & 0xFF))), "Pillow-PNG Grau");
    ImgCheck(Truecolor("pil_png_bilevel", (x, y) => Opaque(Gray((x + y) % 3 == 0 ? 255u : 0u))), "Pillow-PNG schwarzweiss (1 Bit)");
    ImgCheck(Indexed("pil_png_pal", (x, y) => PalEntry(PalIndex(x, y, 11)), true, 11), "Pillow-PNG Palette");
    ImgCheck(Indexed("pil_png_pal_bits4", (x, y) => PalEntry(PalIndex(x, y, 11)), true, 11), "Pillow-PNG Palette 4 Bit");
    {
        var t = Load("pil_png_pal_trns");
        ImgCheck(t.IsIndexed && t.TransparentIndex == 3 && (t.Palette![3] >> 24) == 0, "Pillow-PNG Palette mit Transparenz");
    }

    // ---- BMP ----
    ImgCheck(Indexed("pil_bmp_pal8", (x, y) => PalEntry(PalIndex(x, y, 11)), true, 11, "BMP"), "Pillow-BMP 8 Bit mit Palette");
    ImgCheck(Truecolor("pil_bmp_rgb24", (x, y) => Opaque(Rgb(x, y)), "BMP"), "Pillow-BMP 24 Bit");
    ImgCheck(Truecolor("pil_bmp_rgba32", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "Pillow-BMP 32 Bit mit Alpha");
    ImgCheck(Indexed("pil_bmp_bilevel", (x, y) => Opaque(Gray((x + y) % 3 == 0 ? 255u : 0u))), "Pillow-BMP 1 Bit schwarzweiss");
    ImgCheck(Indexed("pil_bmp_gray8", (x, y) => Opaque(Gray(Rgb(x, y) & 0xFF))), "Pillow-BMP 8 Bit Graustufen");
    ImgCheck(Indexed("bmp_pal4", (x, y) => PalEntry(PalIndex(x, y, 16)), true, 16), "BMP 4 Bit mit Palette");
    ImgCheck(Indexed("bmp_pal4_topdown", (x, y) => PalEntry(PalIndex(x, y, 16)), true, 16), "BMP 4 Bit, Zeilen von oben nach unten (negative Hoehe)");
    ImgCheck(Indexed("bmp_pal1", (x, y) => PalEntry(PalIndex(x, y, 2)), true, 2), "BMP 1 Bit mit Palette");
    {
        var part = Load("bmp_pal8_partial");
        ImgCheck(Indexed("bmp_pal8_partial", (x, y) => PalEntry(PalIndex(x, y, 5)), true, 5) && part.Palette![5] == 0xFF000000u, "BMP 8 Bit mit nur 5 Palette-Eintraegen (Rest deckendes Schwarz)");
    }
    ImgCheck(Truecolor("bmp_rgb24_topdown", (x, y) => Opaque(Rgb(x, y))), "BMP 24 Bit von oben nach unten");
    ImgCheck(Truecolor("bmp_rgb32_alpha", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "BMP 32 Bit (BI_RGB) mit Alpha-Byte");
    ImgCheck(Truecolor("bmp_rgb32_noalpha", (x, y) => Opaque(Rgb(x, y))), "BMP 32 Bit mit unbenutztem Alpha-Byte (0) = deckend");
    ImgCheck(Truecolor("bmp_rgb555", (x, y) => { uint c = Rgb(x, y); return Opaque((((c & 0xFF) >> 3) * 255 / 31) | (((c >> 8 & 0xFF) >> 3) * 255 / 31) << 8 | (((c >> 16 & 0xFF) >> 3) * 255 / 31) << 16); }), "BMP 16 Bit 5-5-5");
    ImgCheck(Truecolor("bmp_rgb565", (x, y) => { uint c = Rgb(x, y); return Opaque((((c & 0xFF) >> 3) * 255 / 31) | (((c >> 8 & 0xFF) >> 2) * 255 / 63) << 8 | (((c >> 16 & 0xFF) >> 3) * 255 / 31) << 16); }), "BMP 16 Bit 5-6-5 (Bitmasken)");
    ImgCheck(Truecolor("bmp_rgba32_masks", (x, y) => Rgb(x, y) | (Alpha(x, y) << 24)), "BMP 32 Bit mit Bitmasken und Alpha");
    ImgCheck(Truecolor("bmp_os2_rgb24", (x, y) => Opaque(Rgb(x, y))), "BMP OS/2-Kopfzeile 24 Bit");
    ImgCheck(Indexed("bmp_os2_pal8", (x, y) => PalEntry(PalIndex(x, y, 7)), true, 7), "BMP OS/2-Kopfzeile 8 Bit mit Palette (3-Byte-Eintraege)");
    ImgCheck(Indexed("bmp_rle8", (x, y) => PalEntry(PalIndex(x, y, 9)), true, 9), "BMP RLE8 (Wiederholungen, absolute Laeufe, Zeilenende)");
    ImgCheck(Indexed("bmp_rle4", (x, y) => PalEntry(PalIndex(x, y, 16)), true, 16), "BMP RLE4");

    // ---- GIF ----
    ImgCheck(Indexed("pil_gif_pal", (x, y) => PalEntry(PalIndex(x, y, 11)), false, 0, "GIF") && Load("pil_gif_pal").TransparentIndex == -1, "Pillow-GIF: Farben stimmen");
    ImgCheck(Indexed("pil_gif_interlaced", (x, y) => PalEntry(PalIndex(x, y, 11))), "Pillow-GIF verschraenkt");
    {
        var g = Load("pil_gif_trans");
        bool ok = g.IsIndexed && g.TransparentIndex >= 0;
        for (int y = 0; y < IH && ok; y++) for (int x = 0; x < IW; x++)
            ok &= (g.Indices![y * IW + x] == g.TransparentIndex) == (PalIndex(x, y, 11) == 2);
        ImgCheck(ok, "Pillow-GIF mit Transparenz: genau die Pixel des durchsichtigen Index");
    }
    ImgCheck(Indexed("pil_gif_anim", (x, y) => PalEntry(PalIndex(x, y, 11))), "Pillow-GIF-Animation: das erste Einzelbild");
    {
        var big = Load("pil_gif_big");
        bool ok = big.Width == 40 && big.Height == 30 && big.IsIndexed;
        for (int y = 0; y < 30 && ok; y++) for (int x = 0; x < 40; x++)
            ok &= big.Palette![big.Indices![y * 40 + x]] == PalEntry((x * 7 + y * 11 + x * y) % 64);
        ImgCheck(ok, "Pillow-GIF 40x30 mit 64 Farben, verschraenkt (LZW mit Wachstum der Codebreite)");
    }

    // ---- Errors: unknown, empty, truncated, damaged ----
    void MustFail(string what, byte[] data)
    {
        try { fire.Terminal.ImageDecoder.Decode(data); ImgCheck(false, what + ": haette scheitern muessen"); }
        catch (fire.Terminal.ImageFormatException) { ImgCheck(true, what); }
        catch (Exception ex) { ImgCheck(false, what + ": falsche Ausnahme " + ex.GetType().Name); }
    }
    MustFail("leere Daten werden abgelehnt", new byte[0]);
    MustFail("unbekanntes Format wird abgelehnt", System.Text.Encoding.ASCII.GetBytes("Das ist kein Bild, sondern Text."));
    foreach (var name in new[] { "png_rgba8", "png_pal4", "png_rgba8_adam7", "pil_png_pal", "pil_bmp_rgba32", "bmp_rle8", "bmp_pal4", "pil_gif_big" })
    {
        var full = ImageFixtures.Get(name);
        bool allFailed = true;
        for (int cut = 1; cut < full.Length; cut += Math.Max(1, full.Length / 40))
        {
            try { fire.Terminal.ImageDecoder.Decode(full[..cut]); allFailed = false; }   // a truncated image may (GIF) be missing leniently, but never throw a foreign exception
            catch (fire.Terminal.ImageFormatException) { }
            catch (Exception) { allFailed = false; ImgCheck(false, $"abgeschnittene {name} wirft eine fremde Ausnahme (bei {cut} Byte)"); }
        }
        if (name != "pil_gif_big" && name != "bmp_rle8") ImgCheck(allFailed, $"abgeschnittene {name} (mehrere Laengen) scheitert mit ImageFormatException");
        else ImgCheck(true, $"abgeschnittene {name}: nur ImageFormatException oder ein lenient dekodiertes Bild");
    }
    {
        var bad = (byte[])ImageFixtures.Get("png_rgb8").Clone();
        bad[bad.Length / 2] ^= 0xFF;
        MustFail("PNG mit beschaedigten Daten (Pruefsumme)", bad);
        var noEnd = ImageFixtures.Get("png_rgb8")[..^12];
        MustFail("PNG ohne IEND", noEnd);
        var big = (byte[])ImageFixtures.Get("png_rgb8").Clone();
        big[16] = 0x7F; // width ~2 billion: CRC error AND too large - in any case ImageFormatException
        MustFail("PNG mit absurder Breite", big);
        var zero = (byte[])ImageFixtures.Get("pil_bmp_rgb24").Clone();
        zero[18] = zero[19] = zero[20] = zero[21] = 0;
        MustFail("BMP mit Breite 0", zero);
        var hugeBmp = (byte[])ImageFixtures.Get("pil_bmp_rgb24").Clone();
        hugeBmp[21] = 0x7F; hugeBmp[19] = 0x7F;
        MustFail("BMP mit absurder Groesse", hugeBmp);
        var badBpp = (byte[])ImageFixtures.Get("pil_bmp_rgb24").Clone();
        badBpp[28] = 7;
        MustFail("BMP mit unbekannter Farbtiefe", badBpp);
        var gifNoImage = System.Text.Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[] { 2, 0, 2, 0, 0, 0, 0, 0x3B }).ToArray();
        MustFail("GIF ohne Bild", gifNoImage);
    }

    // ---- Randomly damaged files: never a foreign exception, never a hang ----
    {
        var rng2 = new Random(11);
        bool clean = true; int tried = 0;
        foreach (var name in ImageFixtures.Data.Keys)
        {
            var original = ImageFixtures.Get(name);
            for (int round = 0; round < 60; round++)
            {
                var copy = (byte[])original.Clone();
                int flips = 1 + rng2.Next(3);
                for (int f = 0; f < flips; f++) copy[rng2.Next(copy.Length)] = (byte)rng2.Next(256);
                tried++;
                try { fire.Terminal.ImageDecoder.Decode(copy); }
                catch (fire.Terminal.ImageFormatException) { }
                catch (Exception ex) { clean = false; Console.WriteLine($"  {name}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        ImgCheck(clean, $"{tried} zufaellig beschaedigte Dateien: nur Erfolg oder ImageFormatException");
    }

    // ---- Framebuffer from an image ----
    {
        var gif = Load("pil_gif_trans");
        var fb = gif.ToFramebuffer();
        ImgCheck(fb.IsIndexed && fb.Width == IW && fb.TransparentIndex == gif.TransparentIndex && fb.Palette.GetPacked(5) == gif.Palette![5] && fb.Indices![10] == gif.Indices![10], "ToFramebuffer: indiziert -> Palette-Framebuffer mit Palette der Datei und Transparenz-Index");
        fb.Resolve();
        ImgCheck(fb.Pixels[10] == gif.Palette[gif.Indices[10]], "ToFramebuffer: das sichtbare Abbild stimmt");
        var asRgba = gif.ToFramebuffer(fire.Terminal.ColorMode.Rgba);
        ImgCheck(!asRgba.IsIndexed && Enumerable.Range(0, IW * IH).All(i => asRgba.Pixels[i] == gif.Palette[gif.Indices[i]]), "ToFramebuffer: indiziert, erzwungen RGBA -> ueber die Palette aufgeloest");
        var png = Load("png_rgba8");
        var rgbaFb = png.ToFramebuffer();
        ImgCheck(!rgbaFb.IsIndexed && rgbaFb.Pixels.SequenceEqual(png.Pixels!), "ToFramebuffer: Truecolor -> RGBA-Framebuffer");
        var quant = png.ToFramebuffer(fire.Terminal.ColorMode.Indexed);
        bool nearestOk = quant.IsIndexed;
        for (int i = 0; i < IW * IH && nearestOk; i++)
        {
            var c = new fire.Terminal.PixelColor(png.Pixels![i] | 0xFF000000u);
            nearestOk &= quant.Indices![i] == quant.Palette.FindNearest(c);
        }
        ImgCheck(nearestOk, "ToFramebuffer: Truecolor, erzwungen Palette -> je Pixel der naechste Eintrag der Standard-Palette");
    }

    Console.WriteLine(imgFailures == 0 ? "Alle Bild-Pruefungen bestanden." : $"FEHLER: {imgFailures} Bild-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Graphics from fire: colour modes, loading images, drawing functions (Console/Framebuffer of the graphics prelude)
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Grafik aus fire: Farbmodi, Bilder, Zeichenfunktionen ===");
    int gfFailures = 0;

    uint ImgPal(int i) => (uint)((i * 40 + 10) % 256) | (uint)(((i * 70 + 20) % 256) << 8) | (uint)(((i * 110 + 30) % 256) << 16) | 0xFF000000u;

    List<string> RunGf(string script, VmExecutionMode mode, Func<string, byte[]>? reader = null)
    {
        var lines = new List<string>();
        var sources = new[] { fire.Standard.Prelude.Source, fire.Terminal.Bridge.GraphicsBridge.PreludeSource, fire.Windows.Bridge.WindowsBridge.PreludeSource, script };
        var program = Parser.ParseMultiple(sources.Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), new HashSet<string>(StringComparer.OrdinalIgnoreCase))).ToList());
        var natives = new NativeRegistry();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        var fbManager = new fire.Terminal.FramebufferManager();
        var conManager = new fire.Terminal.RendererManager(fbManager, new fire.Terminal.IntegratedGlyphFont());
        var winManager = new fire.Terminal.Windows.WindowManager(fbManager, (l, v) => { }, () => new FakeRenderer());
        fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, conManager, reader ?? (path => ImageFixtures.Get(path)));
        fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, winManager);
        // the bytes of a test file as a buffer, and a buffer with nonsense
        natives.Register("__TestImage", args => Value.MakeBuffer(new ByteBuffer(ImageFixtures.Get(args[0].AsString()), ByteConversions.HostByteOrder)));
        natives.Register("__TestGarbage", args => Value.MakeBuffer(new ByteBuffer(System.Text.Encoding.ASCII.GetBytes("kein Bild"), ByteConversions.HostByteOrder)));
        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: mode);
        vm.Run();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckGf(string title, string script, string[] expected, Func<string, byte[]>? reader = null)
    {
        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            string[] actual;
            try { actual = RunGf(script, mode, reader).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + CompileErrors.Describe(ex) }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) gfFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    // The expected pixel counts of the drawing functions from the core itself (the graphics tests above check its shape): here it is about the
    // binding from fire, in both colour modes
    string GfDrawExpected()
    {
        var fb = new fire.Terminal.Framebuffer(40, 40);
        var five = fb.ResolvePixel(fire.Terminal.Paint.FromIndex(5));
        var zero = fb.ResolvePixel(fire.Terminal.Paint.FromIndex(0));
        var four = fb.ResolvePixel(fire.Terminal.Paint.FromIndex(4));
        int Count() { int n = 0; for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++) if (fb.GetRaw(x, y) == five.Rgba) n++; return n; }
        var counts = new List<int>();
        void Reset() => fb.FillRect(0, 0, 40, 40, zero);
        Shp.FillCircle(fb, 20, 20, 3, 5); counts.Add(Count()); Reset();
        Shp.Circle(fb, 20, 20, 10, 5); counts.Add(Count()); Reset();
        Shp.FillEllipse(fb, 20, 20, 8, 3, 5); counts.Add(Count()); Reset();
        Shp.FillTriangle(fb, 2, 2, 22, 2, 2, 22, 5); counts.Add(Count()); Reset();
        Shp.FillPolygon(fb, new[] { 5, 5, 15, 5, 15, 12, 5, 12 }, 5); counts.Add(Count()); Reset();
        Shp.Rect(fb, 5, 5, 10, 10, 5); Shp.FloodFill(fb, 8, 8, 5); counts.Add(Count()); Reset();
        Shp.Polygon(fb, new[] { 2, 2, 30, 2, 30, 30 }, 5, closed: false); counts.Add(Count()); Reset();
        Shp.Rect(fb, 5, 5, 10, 10, 4); Shp.FloodFillBorder(fb, 8, 8, 5, 4); counts.Add(Count());
        return string.Join(" ", counts);
    }

    // GetPixel returns the colour value signed (32 bit)
    const string gfHead = """
        #import "graphics"
        class Px { static int Get(console, int x, int y) { var v = console.GetPixel(x, y); if (v < 0) { v = v + 4294967296 } return v } }
        """;

    CheckGf("Framebuffer-Modus: Rgba (Vorgabe) und Palette, Rohdatengroesse, Palette-Zugriff", gfHead + """
        var rgba = new Framebuffer(8, 4)
        var pal = new Framebuffer(8, 4, ColorMode.Palette)
        print(rgba.Mode() + " " + rgba.ByteCount() + " " + pal.Mode() + " " + pal.ByteCount())
        pal.SetPaletteRgb(9, 1, 2, 3)
        print(pal.GetPaletteColor(9))
        pal.SetPaletteColor(10, 4278190080 + 65536 * 30 + 256 * 20 + 10)
        print(pal.GetPaletteColor(10))
        var bytes = pal.ReadPalette()
        print(bytes.length + " " + bytes[27] + " " + bytes[28] + " " + bytes[29] + " " + pal.ReadPalette(true).length)
        var all = new byte[768]
        for (var i = 0; i < 768; i = i + 1) { all[i] = i % 256 }
        pal.WritePalette(all)
        print(pal.GetPaletteColor(1))
        pal.TransparentIndex = 7
        print(pal.TransparentIndex + " " + rgba.TransparentIndex)
        """, new[] { "0 128 1 32", "4278387201", (4278190080L + 30 * 65536 + 20 * 256 + 10).ToString(), "768 1 2 3 1024", (4278190080L + 5 * 65536 + 4 * 256 + 3).ToString(), "7 -1" });

    CheckGf("Farbangaben: 0-255 = Palette-Index, sonst direkter Wert - in beiden Farbmodi", gfHead + """
        var rgba = new Framebuffer(16, 8)
        var pal = new Framebuffer(16, 8, ColorMode.Palette)
        var a = new Renderer(rgba)
        var b = new Renderer(pal)
        a.FillRect(0, 0, 4, 4, new SolidBrush(9))
        b.FillRect(0, 0, 4, 4, new SolidBrush(9))
        print(Px.Get(a, 1, 1) == rgba.GetPaletteColor(9))
        print(b.GetPixelIndex(1, 1))
        a.FillRect(4, 0, 4, 4, new SolidBrush(4278190335))
        b.FillRect(4, 0, 4, 4, new SolidBrush(4278190335))
        print(Px.Get(a, 5, 1) == 4278190335)
        print(b.GetPixelIndex(5, 1))
        print(a.GetPixelIndex(5, 1) == b.GetPixelIndex(5, 1))
        b.SetPixel(10, 6, 77)
        print(pal.ReadByte(6 * 16 + 10))
        a.SetPixel(10, 6, 77)
        print(Px.Get(a, 10, 6) == rgba.GetPaletteColor(77))
        pal.SetPaletteRgb(9, 10, 20, 30)
        print(Px.Get(b, 1, 1))
        """, new[] { "True", "9", "True", "196", "True", "77", "True", (4278190080L + 30 * 65536 + 20 * 256 + 10).ToString() });

    CheckGf("Palette-Animation: ein Palette-Eintrag aendern faerbt alle Pixel mit diesem Index um (nur im Palette-Framebuffer)", gfHead + """
        var rgba = new Framebuffer(8, 8)
        var pal = new Framebuffer(8, 8, ColorMode.Palette)
        var a = new Renderer(rgba)
        var b = new Renderer(pal)
        a.FillRect(0, 0, 8, 8, new SolidBrush(5))
        b.FillRect(0, 0, 8, 8, new SolidBrush(5))
        var before = Px.Get(a, 3, 3)
        pal.SetPaletteRgb(5, 200, 100, 50)
        rgba.SetPaletteRgb(5, 200, 100, 50)
        print(Px.Get(b, 3, 3) == 4278190080 + 50 * 65536 + 100 * 256 + 200)
        print(Px.Get(a, 3, 3) == before)
        """, new[] { "True", "True" });

    CheckGf("Text in einem Palette-Framebuffer: Print und DrawText mit Palette-Indizes", gfHead + """
        var fb = new Framebuffer(80, 28, ColorMode.Palette)
        var con = new Renderer(fb)
        con.SetColor(14, 1)
        con.Clear()
        con.Print("Hi")
        var fg = 0
        var bg = 0
        for (var y = 0; y < 14; y = y + 1) { for (var x = 0; x < 16; x = x + 1) {
            if (con.GetPixelIndex(x, y) == 14) { fg = fg + 1 }
            if (con.GetPixelIndex(x, y) == 1) { bg = bg + 1 }
        } }
        print((fg > 10) + " " + (fg + bg == 16 * 14))
        con.DrawText(0, 14, "T", new SolidBrush(12))
        var t = 0
        for (var y = 14; y < 28; y = y + 1) { for (var x = 0; x < 8; x = x + 1) { if (con.GetPixelIndex(x, y) == 12) { t = t + 1 } } }
        print(t > 5)
        print(con.GetPixelIndex(40, 20) == 1)
        """, new[] { "True True", "True", "True" });

    CheckGf("Zeichenfunktionen: Kreis, Ellipse, Dreieck, Polygon, FloodFill (gleiche Pixel in beiden Farbmodi)", gfHead + """
        class Draw {
            static Count(console, int w, int h) {
                var n = 0
                for (var y = 0; y < h; y = y + 1) { for (var x = 0; x < w; x = x + 1) { if (console.GetPixelIndex(x, y) == 5) { n = n + 1 } } }
                return n
            }
            static Run(int mode) {
                var fb = new Framebuffer(40, 40, mode)
                var con = new Renderer(fb)
                con.FillCircle(20, 20, 3, new SolidBrush(5))
                var circle = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, new SolidBrush(0))
                con.DrawCircle(20, 20, 10, new Pen(5))
                var ring = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, new SolidBrush(0))
                con.FillEllipse(20, 20, 8, 3, new SolidBrush(5))
                var ellipse = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, new SolidBrush(0))
                con.FillTriangle(2, 2, 22, 2, 2, 22, new SolidBrush(5))
                var tri = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, new SolidBrush(0))
                con.FillPolygon([5, 5, 15, 5, 15, 12, 5, 12], new SolidBrush(5))
                var rect = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, new SolidBrush(0))
                con.DrawRect(5, 5, 10, 10, new Pen(5))
                con.FloodFill(8, 8, new SolidBrush(5))
                var flood = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, new SolidBrush(0))
                con.DrawPolygon([2, 2, 30, 2, 30, 30], new Pen(5), false)
                var open = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, new SolidBrush(0))
                con.DrawRect(5, 5, 10, 10, new Pen(4))
                con.FloodFillBorder(8, 8, new SolidBrush(5), 4)
                var border = Draw.Count(con, 40, 40)
                return circle + " " + ring + " " + ellipse + " " + tri + " " + rect + " " + flood + " " + open + " " + border
            }
        }
        print(Draw.Run(ColorMode.Rgba))
        print(Draw.Run(ColorMode.Palette))
        """, new[] { GfDrawExpected(), GfDrawExpected() });

    CheckGf("Bilder: FromImage (Dateiinhalt im Puffer): Palette-Bild -> Palette-Framebuffer, Truecolor -> RGBA, Modus erzwingen", gfHead + """
        var p = Framebuffer.FromImage(__TestImage("png_pal4"))
        print(p.Mode() + " " + p.Width() + "x" + p.Height() + " " + p.GetPaletteColor(1))
        var t = Framebuffer.FromImage(__TestImage("png_rgba8"))
        print(t.Mode() + " " + t.ByteCount() + " " + t.ReadByte(0) + "," + t.ReadByte(1) + "," + t.ReadByte(2) + "," + t.ReadByte(3))
        var forced = Framebuffer.FromImage(__TestImage("png_pal4"), ColorMode.Rgba)
        print(forced.Mode() + " " + forced.ByteCount())
        var g = Framebuffer.FromImage(__TestImage("pil_gif_trans"))
        print(g.Mode() + " " + (g.TransparentIndex >= 0))
        var bm = Framebuffer.FromImage(__TestImage("pil_bmp_rgb24"))
        print(bm.Mode() + " " + bm.Width())
        var q = Framebuffer.FromImage(__TestImage("png_rgb8"), ColorMode.Palette)
        print(q.Mode() + " " + q.ByteCount())
        """, new[] { $"1 13x7 {ImgPal(1)}", "0 364 3,5,0,0", "0 364", "1 True", "0 13", "1 91" });

    CheckGf("Bilder: FromFile liest ueber den Leser des Hosts (Richtlinie), Fehler sind ImageException", gfHead + """
        var fb = Framebuffer.FromFile("png_pal8")
        print(fb.Mode() + " " + fb.Width())
        try { Framebuffer.FromFile("fehlt.png") } catch (e) { print((e is of ImageException) + " " + e.message) }
        try { Framebuffer.FromFile("verboten.png") } catch (e) { print((e is of ImageException) + " " + e.message) }
        try { Framebuffer.FromImage(__TestGarbage()) } catch (e) { print((e is of ImageException) + " " + e.message) }
        try { Framebuffer.FromImage(__TestImage("png_rgb8"), 7) } catch (e) { print(e is of ImageException) }
        print("weiter")
        """, new[] { "1 13", "True Datei nicht da: fehlt.png", "True verboten: verboten.png", "True Unknown image format (expected: PNG, BMP or GIF).", "True", "weiter" },
        reader: path => path switch
        {
            "fehlt.png" => throw new System.IO.FileNotFoundException("Datei nicht da: " + path),
            "verboten.png" => throw new UnauthorizedAccessException("verboten: " + path),
            _ => ImageFixtures.Get(path),
        });

    CheckGf("Rohpixel: FromPixels (RGBA und Palette mit Palette), ReadBytes/WriteBytes, falsche Groessen", gfHead + """
        var raw = new byte[16]
        for (var i = 0; i < 16; i = i + 1) { raw[i] = i * 10 }
        var a = Framebuffer.FromPixels(2, 2, raw)
        print(a.Mode() + " " + a.ReadByte(5) + " " + a.ReadBytes().length)
        var idx = new byte[6]
        for (var i = 0; i < 6; i = i + 1) { idx[i] = i + 1 }
        var pal = new byte[768]
        pal[3] = 11
        pal[4] = 22
        pal[5] = 33
        var b = Framebuffer.FromPixels(3, 2, idx, ColorMode.Palette, pal)
        print(b.Mode() + " " + b.ReadByte(4) + " " + b.GetPaletteColor(1))
        var back = b.ReadBytes()
        back[0] = 99
        b.WriteBytes(back)
        print(b.ReadByte(0))
        try { Framebuffer.FromPixels(2, 2, idx) } catch (e) { print("falsche Groesse: " + (e is of ImageException)) }
        try { b.WriteBytes(raw) } catch (e) { print((e is of GraphicsException) + " " + e.message) }
        try { b.WritePalette(raw) } catch (e) { print((e is of GraphicsException) + " " + e.message) }
        try { b.SetPaletteColor(256, 0) } catch (e) { print((e is of GraphicsException) + " " + e.message) }
        try { print(b.GetPaletteColor(-1)) } catch (e) { print((e is of GraphicsException) + " " + e.message) }
        print(b.ReadByte(0))
        """, new[] { "0 50 16", $"1 5 {4278190080L + 33 * 65536 + 22 * 256 + 11}", "99", "falsche Groesse: True",
            "True Expected exactly 6 bytes, got 16.",
            "True A palette has 768 (RGB) or 1024 (RGBA) bytes, got 16.",
            "True Palette index 256 outside of 0-255.",
            "True Palette index -1 outside of 0-255.", "99" });

    CheckGf("Blit: geladenes Bild in einen anderen Framebuffer (auch ueber die Farbmodi), Ausschnitt, Skalierung, Spiegelung, Transparenz", gfHead + """
        var img = Framebuffer.FromImage(__TestImage("png_pal4"))
        var rgba = new Framebuffer(30, 20)
        var a = new Renderer(rgba)
        a.Blit(img, 2, 3)
        print(Px.Get(a, 2, 3) == img.GetPaletteColor(img.ReadByte(0)))
        print(Px.Get(a, 14, 9) == img.GetPaletteColor(img.ReadByte(6 * 13 + 12)))
        var pal = new Framebuffer(30, 20, ColorMode.Palette)
        pal.WritePalette(img.ReadPalette(true))
        var b = new Renderer(pal)
        b.Blit(img, 0, 0)
        print(pal.ReadByte(5) == img.ReadByte(5))
        b.BlitRegion(img, 3, 2, 4, 3, 20, 10)
        print(pal.ReadByte(10 * 30 + 20) == img.ReadByte(2 * 13 + 3))
        b.BlitScaled(img, 0, 0, 13, 7, 0, 10, 26, 7)
        print(pal.ReadByte(10 * 30 + 2) == img.ReadByte(1))
        b.BlitScaled(img, 0, 0, 13, 7, 13, 0, -13, 7)
        print(pal.ReadByte(13) == img.ReadByte(12) && pal.ReadByte(25) == img.ReadByte(0))
        var bg = new Framebuffer(13, 7, ColorMode.Palette)
        var c = new Renderer(bg)
        c.FillRect(0, 0, 13, 7, new SolidBrush(200))
        var sprite = Framebuffer.FromImage(__TestImage("pil_gif_trans"))
        c.Blit(sprite, 0, 0, BlitMode.Transparent)
        var kept = 0
        for (var i = 0; i < 91; i = i + 1) { if (bg.ReadByte(i) == 200) { kept = kept + 1 } }
        print(kept > 0 && kept < 91)
        var tc = Framebuffer.FromImage(__TestImage("png_rgba8"))
        var dst = new Framebuffer(13, 7)
        var d = new Renderer(dst)
        d.FillRect(0, 0, 13, 7, new SolidBrush(4278190335))
        d.Blit(tc, 0, 0, BlitMode.Transparent)
        print(Px.Get(d, 0, 0) == 4278190335)
        """, new[] { "True", "True", "True", "True", "True", "True", "True", "True" });

    CheckGf("Slicer aus fire: ToMask, Slicer.Slice liefert eine List von ToolPath, Fehler als GraphicsException", gfHead + """
        var img = new Framebuffer(120, 70)
        var con = new Renderer(img)
        con.FillRect(0, 0, 120, 70, new SolidBrush(4294967295))
        con.FillRect(10, 10, 100, 50, new SolidBrush(4278190080))
        var mask = img.ToMask()
        print(mask.Mode() + " " + mask.Width() + "x" + mask.Height() + " " + mask.ReadByte(0) + " " + mask.ReadByte(20 * 120 + 20))
        var slicer = new Slicer(1, 0.1)
        slicer.flipY = false
        print(slicer.StepOver)
        var paths = slicer.Slice(mask)
        print(paths.count)
        var outline = paths[paths.count - 1]
        var minX = 1000.0
        var maxX = 0.0
        for (var i = 0; i < outline.Count(); i = i + 1) {
            if (outline.X(i) < minX) { minX = outline.X(i) }
            if (outline.X(i) > maxX) { maxX = outline.X(i) }
        }
        print((outline.kind == PathKind.Outline) + " " + outline.closed + " " + (minX > 1.44 && minX < 1.56) + " " + (maxX > 10.44 && maxX < 10.56))
        slicer.strategy = FillStrategy.OutlineOnly
        print(slicer.Slice(mask).count)
        slicer.lineWidth = 50
        print(slicer.Slice(mask).count)
        try { var bad = new Slicer(0, 1) } catch (e) { print((e is of GraphicsException) + " " + e.message) }
        slicer.overlap = 0.99
        try { slicer.Slice(mask) } catch (e) { print((e is of GraphicsException) + " " + e.message) }
        foreach (p in new Slicer(1, 0.1).Slice(mask)) { print(p.kind) }
        """, new[] { "1 120x70 0 1", "0.5", "4", "True True True True", "1", "0", "True Line thickness and pixel size must be greater than 0.", "True The overlap must be between 0 and 0.95.", "0", "0", "0", "1" });

    // ---- Real files via the host's session: the IoPolicy decides what Framebuffer.FromFile may read ----
    {
        string imgDir = Path.Combine(Path.GetTempPath(), "fire-img-" + Guid.NewGuid().ToString("N"));
        string otherDir = Path.Combine(Path.GetTempPath(), "fire-img-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(imgDir);
        Directory.CreateDirectory(otherDir);
        try
        {
            string png = Path.Combine(imgDir, "bild.png").Replace("\\", "/");
            File.WriteAllBytes(png, ImageFixtures.Get("png_pal8"));
            string script = $$"""
                #import "graphics"
                try {
                    var fb = Framebuffer.FromFile("{{png}}")
                    print("geladen " + fb.Mode() + " " + fb.Width() + "x" + fb.Height())
                } catch (e) { print((e is of ImageException) + " " + e.message) }
                """;
            List<string> Session(fire.IO.Bridge.IoPolicy? policy)
            {
                var lines = new List<string>();
                var session = fire.Compiler.RuntimeSession.Build(new[] { script }, VmExecutionMode.Release,
                    args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); }, ioPolicy: policy);
                session.Run();
                return lines;
            }
            void CheckSession(string what, fire.IO.Bridge.IoPolicy? policy, Func<string, bool> ok)
            {
                string result;
                try { result = string.Join("|", Session(policy)); }
                catch (Exception ex) { result = "AUSNAHME: " + ex.Message; }
                bool good = ok(result);
                if (!good) gfFailures++;
                Console.WriteLine(good ? $"OK: {what}" : $"FEHLER: {what}\n  erhalten: {result}");
            }
            CheckSession("FromFile ohne Richtlinie (alles erlaubt)", null, r => r == "geladen 1 13x7");
            CheckSession("FromFile mit Richtlinie, die das Verzeichnis erlaubt", fire.IO.Bridge.IoPolicy.Rooted(imgDir, readOnly: true), r => r == "geladen 1 13x7");
            CheckSession("FromFile mit DenyAll: ImageException mit dem Grund der Richtlinie", fire.IO.Bridge.IoPolicy.DenyAll, r => r == "True File access is not allowed for this program.");
            CheckSession("FromFile ausserhalb des erlaubten Verzeichnisses: ImageException", fire.IO.Bridge.IoPolicy.Rooted(otherDir), r => r.StartsWith("True "));
        }
        finally
        {
            try { Directory.Delete(imgDir, true); Directory.Delete(otherDir, true); } catch { }
        }
    }

    Console.WriteLine(gfFailures == 0 ? "Alle Grafik-aus-fire-Pruefungen bestanden." : $"FEHLER: {gfFailures} Grafik-aus-fire-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Network (#import "net"): the host's policy (NetPolicy) for connections, listeners, datagrams and names
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Netzwerk: Host-Richtlinie ===");
    int netFailures = 0;
    List<string> NetSession(string script, fire.Runtime.NetPolicy? policy)
    {
        var lines = new List<string>();
        var session = fire.Compiler.RuntimeSession.Build(new[] { "#import \"net\"\n" + script }, VmExecutionMode.Release,
            args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); }, netPolicy: policy);
        session.Run();
        return lines;
    }
    void CheckNet(string title, string script, fire.Runtime.NetPolicy? policy, string[] expected)
    {
        string[] actual;
        try { actual = NetSession(script, policy).ToArray(); }
        catch (Exception ex) { actual = new[] { "AUSNAHME: " + ex.Message }; }
        bool ok = actual.SequenceEqual(expected);
        if (!ok) netFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
    }

    const string everything = """
        try { var l = new Net.TcpListener("127.0.0.1", 0); print("listen ok " + (l.Port > 0)); l.Close() } catch (Net.PermissionException e) { print("listen denied " + e.code) }
        try { var u = new Net.UdpSocket("127.0.0.1", 0); print("udp ok"); u.Close() } catch (Net.PermissionException e) { print("udp denied " + e.code) }
        try { print("resolve " + Net.Dns.Resolve("127.0.0.1").count) } catch (Net.PermissionException e) { print("resolve denied " + e.code) }
        try { var c = new Net.TcpClient("127.0.0.1", 9, 500); print("connected?") } catch (Net.PermissionException e) { print("connect denied " + e.code) } catch (Net.NetException e) { print("connect failed " + e.code) }
        try { var c = new Net.TcpClient("example.invalid", 80, 500); print("connected?") } catch (Net.PermissionException e) { print("outside denied " + e.code) } catch (Net.NetException e) { print("outside failed " + e.code) }
        """;
    CheckNet("Host-Richtlinie: alles erlaubt (Vorgabe)", everything, null,
        new[] { "listen ok True", "udp ok", "resolve 1", "connect failed 3", "outside failed 10" });
    CheckNet("Host-Richtlinie: DenyAll - jede Art von Zugriff wird zur PermissionException (Code 8)", everything, fire.Runtime.NetPolicy.DenyAll,
        new[] { "listen denied 8", "udp denied 8", "resolve denied 8", "connect denied 8", "outside denied 8" });
    CheckNet("Host-Richtlinie: LoopbackOnly - dieser Rechner geht, andere Namen nicht", everything, fire.Runtime.NetPolicy.LoopbackOnly,
        new[] { "listen ok True", "udp ok", "resolve 1", "connect failed 3", "outside denied 8" });
    CheckNet("Host-Richtlinie: Hosts - nur die genannten (hier: host:port; Namen davon aufloesen darf man), Lauschen nur wenn erlaubt", everything, fire.Runtime.NetPolicy.Hosts(new[] { "127.0.0.1:9", "example.invalid:443" }),
        new[] { "listen denied 8", "udp denied 8", "resolve 1", "connect failed 3", "outside denied 8" });
    CheckNet("Host-Richtlinie: Hosts mit allowListen erlaubt Lauschen auf localhost", everything, fire.Runtime.NetPolicy.Hosts(new[] { "*" }, allowListen: true),
        new[] { "listen ok True", "udp ok", "resolve 1", "connect failed 3", "outside failed 10" });
    CheckNet("Offene Sockets am Programmende: das naechste Programm startet sauber (fire_pkg_reset)", """
        var l = new Net.TcpListener("127.0.0.1", 0)
        var c = new Net.TcpClient("127.0.0.1", l.Port)
        print("offen")
        """, null, new[] { "offen" });

    Console.WriteLine(netFailures == 0 ? "Alle Netzwerk-Pruefungen bestanden." : $"FEHLER: {netFailures} Netzwerk-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Slicer: split a mask from a framebuffer (ToMask) into tool paths
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Slicer: ToMask und Werkzeugbahnen ===");
    int slFailures = 0;
    void SlCheck(bool ok, string what)
    {
        if (!ok) slFailures++;
        Console.WriteLine(ok ? $"OK: {what}" : $"FEHLER: {what}");
    }

    // ---- ToMask ----
    {
        var rgba = new fire.Terminal.Framebuffer(4, 1);
        rgba.SetPixel(0, 0, fire.Terminal.PixelColor.FromRgb(10, 10, 10));      // dark
        rgba.SetPixel(1, 0, fire.Terminal.PixelColor.FromRgb(240, 240, 240));   // bright
        rgba.SetPixel(2, 0, new fire.Terminal.PixelColor(0, 0, 0, 10));         // dark, but almost transparent
        rgba.SetPixel(3, 0, fire.Terminal.PixelColor.FromRgb(0, 255, 0));       // Green: brightness 150
        var dark = rgba.ToMask();
        SlCheck(dark.IsIndexed && dark.Width == 4 && dark.Height == 1 && dark.Indices!.SequenceEqual(new byte[] { 1, 0, 0, 0 }), "ToMask: dunkle Pixel werden ausgefraest, helle und fast durchsichtige nicht");
        SlCheck(dark.Palette.GetPacked(0) == fire.Terminal.PixelColor.Black.Packed && dark.Palette.GetPacked(1) == fire.Terminal.PixelColor.White.Packed && dark.TransparentIndex == 0, "ToMask: Palette schwarz/weiss, Index 0 durchsichtig");
        SlCheck(rgba.ToMask(darkIsRemoved: false).Indices!.SequenceEqual(new byte[] { 0, 1, 0, 1 }), "ToMask: darkIsRemoved = false (helle Pixel, durchsichtige nie)");
        SlCheck(rgba.ToMask(threshold: 200).Indices![3] == 1 && rgba.ToMask(threshold: 100).Indices![3] == 0, "ToMask: Schwelle gegen die Helligkeit 0,299 R + 0,587 G + 0,114 B (Gruen = 150)");
        SlCheck(rgba.ToMask(alphaThreshold: 5).Indices![2] == 1, "ToMask: Alpha-Schwelle");

        var pal = new fire.Terminal.Framebuffer(3, 1, fire.Terminal.ColorMode.Indexed);
        pal.Palette.SetColor(5, unchecked((int)fire.Terminal.PixelColor.FromRgb(5, 5, 5).Packed));
        pal.Palette.SetColor(6, unchecked((int)fire.Terminal.PixelColor.FromRgb(250, 250, 250).Packed));
        pal.Indices![0] = 5; pal.Indices[1] = 6; pal.Indices[2] = 5;
        SlCheck(pal.ToMask().Indices!.SequenceEqual(new byte[] { 1, 0, 1 }), "ToMask aus einem Palette-Bild: ueber die Farben der Palette");
    }

    // ---- Slicer ----
    fire.Terminal.Framebuffer Rect(int w, int h, int x0, int y0, int x1, int y1)
    {
        var fb = new fire.Terminal.Framebuffer(w, h, fire.Terminal.ColorMode.Indexed);
        fb.FillRect(x0, y0, x1 - x0, y1 - y0, fb.ResolvePixel(fire.Terminal.Paint.FromIndex(1)));
        return fb;
    }
    {
        // 100 x 50 pixels at 0.1 mm = 10 x 5 mm, cutter 1 mm: the tool centre may be 0.5 mm away from the edge
        var mask = Rect(120, 70, 10, 10, 110, 60);
        var slicer = new fire.Terminal.ImageSlicer(1.0, 0.1) { FlipY = false };
        var paths = slicer.Slice(mask);
        SlCheck(paths.Count == 4 && paths.Take(3).All(p => p.Kind == fire.Terminal.PathKind.Fill) && paths[3].Kind == fire.Terminal.PathKind.Outline && paths.All(p => p.Closed),
            $"Contour: drei Innenringe (innen zuerst), dann die Randkontur ({paths.Count} Bahnen)");
        var outline = paths[3].Points;
        double minX = outline.Min(p => p.X), maxX = outline.Max(p => p.X), minY = outline.Min(p => p.Y), maxY = outline.Max(p => p.Y);
        SlCheck(Math.Abs(minX - 1.5) < 0.06 && Math.Abs(maxX - 10.5) < 0.06 && Math.Abs(minY - 1.5) < 0.06 && Math.Abs(maxY - 5.5) < 0.06,
            $"Randkontur: das Rechteck 1,0-11,0 x 1,0-6,0 mm um den Fraeserradius 0,5 mm nach innen ({minX:0.00}..{maxX:0.00} x {minY:0.00}..{maxY:0.00})");
        SlCheck(outline.Count <= 8, $"die Punktreduktion macht aus der Kontur wenige Ecken ({outline.Count} Punkte)");
        double innerMin = paths[0].Points.Min(p => p.X);
        SlCheck(innerMin > minX + 1.4, $"die Ringe liegen nach innen versetzt (innerster Ring {innerMin:0.00} mm, Randkontur {minX:0.00} mm)");

        var flipped = new fire.Terminal.ImageSlicer(1.0, 0.1).Slice(mask);   // FlipY is the default
        var fo = flipped[3].Points;
        SlCheck(Math.Abs(fo.Min(p => p.Y) - (7.0 - 5.5)) < 0.06 && Math.Abs(fo.Max(p => p.Y) - (7.0 - 1.5)) < 0.06, "FlipY (Vorgabe): Y nach oben, von der Bildhoehe 7 mm gezaehlt");

        var only = new fire.Terminal.ImageSlicer(1.0, 0.1) { Strategy = fire.Terminal.FillStrategy.OutlineOnly }.Slice(mask);
        SlCheck(only.Count == 1 && only[0].Kind == fire.Terminal.PathKind.Outline, "OutlineOnly: nur die Randkontur");
        var zig = new fire.Terminal.ImageSlicer(1.0, 0.1) { Strategy = fire.Terminal.FillStrategy.ZigZag, FlipY = false }.Slice(mask);
        SlCheck(zig.Count > 4 && zig.Last().Kind == fire.Terminal.PathKind.Outline && zig.Take(zig.Count - 1).All(p => !p.Closed && p.Points.Count == 2), $"ZigZag: waagerechte Bahnen, dann die Randkontur ({zig.Count} Bahnen)");
        var ys = zig.Take(zig.Count - 1).Select(p => p.Points[0].Y).Distinct().OrderBy(y => y).ToList();
        SlCheck(ys.Count >= 8 && ys[1] - ys[0] > 0.45 && ys[1] - ys[0] < 0.55, "ZigZag: Zeilenabstand = Bahnabstand 0,5 mm");

        var tight = new fire.Terminal.ImageSlicer(1.0, 0.1) { Overlap = 0.0 }.Slice(mask);
        SlCheck(tight.Count < paths.Count + 1 && tight.Count >= 2, "Overlap 0: weniger Ringe (Abstand = Linienstaerke)");
    }
    SlCheck(new fire.Terminal.ImageSlicer(1.0, 0.1).Slice(Rect(60, 40, 10, 10, 15, 30)).Count == 0, "zu schmale Flaeche (0,5 mm bei 1 mm Fraeser): keine Bahnen");
    SlCheck(new fire.Terminal.ImageSlicer(1.0, 0.1).Slice(new fire.Terminal.Framebuffer(20, 20, fire.Terminal.ColorMode.Indexed)).Count == 0, "leere Maske: keine Bahnen");
    {
        // two separate areas and a hole: a ring around the hole
        var fb = new fire.Terminal.Framebuffer(200, 80, fire.Terminal.ColorMode.Indexed);
        var one = fb.ResolvePixel(fire.Terminal.Paint.FromIndex(1)); var zero = fb.ResolvePixel(fire.Terminal.Paint.FromIndex(0));
        fb.FillRect(10, 10, 60, 60, one); fb.FillRect(110, 10, 80, 60, one); fb.FillRect(130, 30, 20, 20, zero);
        var outlines = new fire.Terminal.ImageSlicer(1.0, 0.1) { Strategy = fire.Terminal.FillStrategy.OutlineOnly }.Slice(fb);
        SlCheck(outlines.Count == 3 && outlines.All(p => p.Closed), "zwei Flaechen, eine mit Loch: drei geschlossene Randkonturen");
        var rgbaMask = new fire.Terminal.Framebuffer(200, 80);
        for (int y = 0; y < 80; y++) for (int x = 0; x < 200; x++) if (fb.GetRaw(x, y) != 0) rgbaMask.SetPixel(x, y, fire.Terminal.PixelColor.White);
        SlCheck(new fire.Terminal.ImageSlicer(1.0, 0.1) { Strategy = fire.Terminal.FillStrategy.OutlineOnly }.Slice(rgbaMask).Count == 3, "ein RGBA-Framebuffer als Maske (sichtbar und nicht schwarz = ausfraesen)");
    }
    try { new fire.Terminal.ImageSlicer(0, 0.1); SlCheck(false, "Linienstaerke 0 wird abgelehnt"); } catch (ArgumentOutOfRangeException) { SlCheck(true, "Linienstaerke 0 wird abgelehnt"); }

    Console.WriteLine(slFailures == 0 ? "Alle Slicer-Pruefungen bestanden." : $"FEHLER: {slFailures} Slicer-Pruefung(en) fehlgeschlagen.");
}

// the drawing of the UI library without events: run by the VM (fake renderer) below, natively (SDL dummy driver) in the native checks
string uiDrawScript = """
    var fb = new Framebuffer(640, 300)
    var win = new Window(fb, "Test")
    var ui = new UI.Root(fb, win)
    var panel = new UI.Panel(8, 8, 300, 150)
    ui.Add(panel)
    panel.Add(new UI.Label("Hello UI", 6, 6))
    panel.Add(new UI.Button("OK", 6, 26, 80, 26))
    panel.Add(new UI.CheckBox("check me", 100, 30, true))
    var box = new UI.TextBox("text", 6, 64, 160, 24)
    panel.Add(box)
    var stack = new UI.Stack(180, 60, 100, 80)
    stack.Add(new UI.Button("one", 0, 0, 80, 20))
    stack.Add(new UI.Button("two", 0, 0, 80, 20))
    panel.Add(stack)
    // the layout panels: a grid with a fixed, a star and an auto column, a border with padding, a wrap panel that spans all columns
    var lay = new UI.Grid(8, 170, 300, 120)
    lay.SetColumns("60, *, auto")
    lay.SetRows("24, *")
    lay.AddAt(new UI.Button("grid", 0, 0, -1, -1), 0, 0)
    var bd = new UI.Border()
    bd.padding = new UI.Thickness(2)
    bd.SetChild(new UI.Label("border"))
    lay.AddAt(bd, 0, 1)
    lay.AddAt(new UI.Label("auto"), 0, 2)
    var wr = new UI.WrapPanel()
    for (var i = 0; i < 6; i++) { var wb = new UI.Button("w" + i, 0, 0, 44, 18); wb.margin = new UI.Thickness(2); wr.Add(wb) }
    lay.AddAt(wr, 1, 0, 1, 3)
    ui.Add(lay)
    // styles and templates: an implicit style for labels, a button whose look is a template with a part bound to its text and a hover trigger
    var sty = new UI.Style("Label")
    sty.Set("brush", new SolidBrush(UI.Color.Rgb(0, 0, 200)))
    ui.resources.AddStyle(sty)
    var tpl = new UI.ControlTemplate(func (owner) => {
        var tb = new UI.Border()
        tb.name = "tb"
        tb.background = new SolidBrush(UI.Color.Rgb(40, 160, 80))
        tb.padding = new UI.Thickness(2)
        var tl = new UI.Label("")
        tl.name = "tl"
        tb.SetChild(tl)
        return tb
    })
    tpl.Bind("tl", "text", "text")
    var tt = new UI.Trigger("hover", true)
    tt.Set("background", new SolidBrush(UI.Color.Rgb(200, 60, 60)), "tb")
    tpl.AddTrigger(tt)
    var tbtn = new UI.Button("tpl", 200, 100, -1, -1)
    tbtn.template = tpl
    panel.Add(tbtn)
    // scrolling with the clip rectangle, lists, a tree, radio buttons, shapes, a drawing canvas and a menu with its popup
    var sv = new UI.ScrollViewer(330, 8, 120, 90)
    var col = new UI.StackPanel()
    for (var i = 0; i < 9; i++) { col.Add(new UI.Label("row " + i)) }
    sv.SetContent(col)
    ui.Add(sv)
    var lbx = new UI.ListBox(460, 8, 100, 90)
    for (var i = 0; i < 8; i++) { lbx.Add("entry " + i) }
    lbx.Select(2)
    ui.Add(lbx)
    var tv = new UI.TreeView(330, 108, 120, 90)
    var top = tv.AddNode(new UI.TreeNode("root"))
    top.Add(new UI.TreeNode("child a"))
    var cb = top.Add(new UI.TreeNode("child b"))
    cb.Add(new UI.TreeNode("leaf"))
    top.expanded = true
    ui.Add(tv)
    var rbs = new UI.RadioButtons(460, 108)
    rbs.Add("one")
    rbs.Add("two")
    rbs.Select(1)
    ui.Add(rbs)
    var shp = new UI.Path(undefined, 570, 8)
    shp.SetData("M 0 0 L 50 0 C 60 20 60 40 25 50 Z")
    shp.fill = new SolidBrush(UI.Color.Rgb(200, 120, 40))
    shp.stroke = new Pen(UI.Color.Rgb(0, 0, 0))
    ui.Add(shp)
    var dcv = new UI.DrawingCanvas(570, 70, 60, 40)
    dcv.onPaint = func (c) => {
        c.renderer.FillRect(0, 0, 60, 40, new SolidBrush(UI.Color.Rgb(30, 30, 90)))
        c.renderer.DrawLine(0, 0, 59, 39, new Pen(UI.Color.Rgb(255, 255, 255)))
    }
    ui.Add(dcv)
    var mbar = new UI.MenuBar(330, 210, 300, -1)
    var mfile = new UI.MenuItem("File")
    mfile.Add(new UI.MenuItem("Open"))
    mfile.Add(UI.MenuItem.Separator())
    mfile.Add(new UI.MenuItem("Quit"))
    mbar.Add(mfile)
    ui.Add(mbar)
    print(ui.Tick())
    mbar.Open(ui, 0)
    ui.Tick()
    print("layout " + bd.rx + " " + bd.actualWidth + " " + wr.actualWidth + " " + wr.actualHeight + " " + wr.children[5].rx + "," + wr.children[5].ry)
    var bytes = fb.ReadBytes()
    var h = 17
    for (var i = 0; i < bytes.length; i++) { h = (h * 31 + bytes[i]) % 1000000007 }
    print("hash " + h)
    print(ui.Tick())
    """;
string[] uiDrawExpected = Array.Empty<string>();

// ---------------------------------------------------------------------------
// UI library (#import "ui"): headless - real framebuffer/console/WindowManager, only the renderer is a dummy
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== UI-Bibliothek ===");
    int uiFailures = 0;

    IReadOnlyDictionary<string, RuntimeClass>? uiClasses = null;

    // Runs `script` with graphics prelude + UI prelude. The window runs via the real WindowManager (event queue
    // included), only the renderer is `FakeRenderer`: `__TestEvent(type, ...)` stores an event that the next Tick fetches,
    // `__TestClose()` closes the window.
    List<string> RunUi(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var sources = new[] { fire.Standard.Prelude.Source, fire.Terminal.Bridge.GraphicsBridge.PreludeSource, fire.Windows.Bridge.WindowsBridge.PreludeSource, fire.Standard.ReflectionPrelude.Source, fire.UI.Bridge.UiBridge.PreludeSource, script };
        var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = Parser.ParseMultiple(sources.Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), alreadyIncluded)).ToList());
        var natives = new NativeRegistry();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();

        var renderer = new FakeRenderer();
        var fbManager = new fire.Terminal.FramebufferManager();
        var conManager = new fire.Terminal.RendererManager(fbManager, new fire.Terminal.IntegratedGlyphFont());
        var winManager = new fire.Terminal.Windows.WindowManager(fbManager, (l, v) => { }, () => renderer);
        fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, conManager);
        fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, winManager);
        ReflectionNatives.Register(natives);

        natives.Register("__TestClose", args => { renderer.Closed = true; return Value.MakeUndefined(); });
        natives.Register("__TestEvent", args =>
        {
            renderer.Push((int)args[0].AsInt(), args);
            return Value.MakeUndefined();
        });

        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        uiClasses = compiled.Classes;
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: mode);
        vm.Run();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    uiDrawExpected = RunUi(uiDrawScript, VmExecutionMode.Release).ToArray();

    void CheckUi(string title, string script, string[] expected)
    {
        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release })
        {
            string[] actual;
            try { actual = RunUi(script, mode).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + CompileErrors.Describe(ex) }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) uiFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    const string uiHead = """
        // GetPixel returns the colour value signed (32 bit); the colours of the UI library are unsigned values
        class Px { static int Get(console, int x, int y) { var v = console.GetPixel(x, y); if (v < 0) { v = v + 4294967296 } return v } }
        var fb = new Framebuffer(320, 200)
        var win = new Window(fb, "Test")
        var ui = new UI.Root(fb, win)

        """;

    CheckUi("Touch und Joystick: Ereignisse als Warteschlange, TouchMouse, Callbacks mit passender Parameterzahl", """
        var fb = new Framebuffer(100, 80)
        var win = new Window(fb, "Input")
        win.EnableEvents()
        print(win.TouchMouse)
        win.TouchMouse = false
        print(win.TouchMouse)
        __TestEvent(16, 3, 10.5, 20.5)
        __TestEvent(17, 3, 12.0, 22.0)
        __TestEvent(18, 3, 12.0, 22.0)
        __TestEvent(32, 7, 1, -0.5)
        __TestEvent(33, 7, 2)
        __TestEvent(34, 7, 2)
        __TestEvent(35, 7, 0, 5)
        __TestEvent(36, 7)
        __TestEvent(37, 7)
        win.Tick()
        var e = win.NextEvent()
        while (e != undefined) {
            var line = ""
            for (var i = 0; i < e.length; i++) { line = line + e[i] + " " }
            print(line)
            e = win.NextEvent()
        }
        print(win.RegisterTouchDown(func (int f, float x, float y, float p) => { }))
        print(win.RegisterTouchMove(func (int f, float x, float y, float p) => { }))
        print(win.RegisterTouchUp(func (int f, float x, float y, float p) => { }))
        print(win.RegisterJoystickAxis(func (int j, int a, float v) => { }))
        print(win.RegisterJoystickHat(func (int j, int h, int m) => { }))
        print(win.RegisterJoystickButtonDown(func (int j, int b) => { }))
        print(win.RegisterJoystickButtonUp(func (int j, int b) => { }))
        print(win.RegisterJoystickAdded(func (int j) => { }))
        print(win.RegisterJoystickRemoved(func (int j) => { }))
        """, new[] { "True", "False", "16 3 10 20 1 ", "17 3 12 22 1 ", "18 3 12 22 1 ", "32 7 1 -0.5 ", "33 7 2 ", "34 7 2 ", "35 7 0 5 ", "36 7 ", "37 7 ", "True", "True", "True", "True", "True", "True", "True", "True", "True" });

    CheckUi("Touch: UI.Root fuehrt die Oberflaeche mit dem Finger (Antippen, Abheben daneben, zweiter Finger, kein Hover danach)", uiHead + """
        print(win.TouchMouse)
        var clicks = 0
        var b = new UI.Button("OK", 10, 10, 80, 26)
        b.onClick = func () => { clicks = clicks + 1 }
        ui.Add(b)
        ui.Draw()
        __TestEvent(16, 1, 20.0, 20.0)
        __TestEvent(18, 1, 20.0, 20.0)
        ui.Tick()
        print("tippen " + clicks)
        __TestEvent(16, 1, 20.0, 20.0)
        __TestEvent(17, 1, 40.0, 22.0)
        __TestEvent(18, 1, 200.0, 150.0)
        ui.Tick()
        print("daneben " + clicks + " hover " + b.hover + " pressed " + b.pressed)
        __TestEvent(16, 1, 20.0, 20.0)
        __TestEvent(16, 2, 200.0, 150.0)
        __TestEvent(18, 2, 200.0, 150.0)
        __TestEvent(18, 1, 20.0, 20.0)
        ui.Tick()
        print("zweiter Finger " + clicks)
        """, new[] { "False", "tippen 1", "daneben 1 hover False pressed False", "zweiter Finger 2" });

    CheckUi("Touch: Wischen verschiebt einen ScrollViewer, ohne zu klicken; ein Griff der Leiste wird gezogen statt gewischt", uiHead + """
        var hits = 0
        var inc = func () => { hits = hits + 1 }
        var sv = new UI.ScrollViewer(0, 0, 100, 80)
        var col = new UI.StackPanel()
        for (var i = 0; i < 8; i = i + 1) {
            var bt = new UI.Button("b" + i, 0, 0, 60, 30)
            bt.onClick = inc
            col.Add(bt)
        }
        sv.SetContent(col)
        ui.Add(sv)
        ui.Draw()
        print("Inhalt " + sv.vbar.extent + " Ausschnitt " + sv.viewH)
        __TestEvent(16, 1, 30.0, 40.0)
        __TestEvent(17, 1, 30.0, 35.0)
        __TestEvent(17, 1, 30.0, 10.0)
        __TestEvent(17, 1, 30.0, 0.0)
        __TestEvent(18, 1, 30.0, 0.0)
        ui.Tick()
        print("gewischt " + sv.vbar.offset + " Klicks " + hits)
        __TestEvent(16, 1, 30.0, 10.0)
        __TestEvent(17, 1, 30.0, 70.0)
        __TestEvent(18, 1, 30.0, 70.0)
        ui.Tick()
        print("zurueck " + sv.vbar.offset + " Klicks " + hits)
        sv.vbar.Set(0)
        ui.Draw()
        __TestEvent(16, 1, 94.0, 4.0)
        __TestEvent(17, 1, 94.0, 80.0)
        __TestEvent(18, 1, 94.0, 80.0)
        ui.Tick()
        print("Griff gezogen " + (sv.vbar.offset > 0))
        """, new[] { "Inhalt 240 Ausschnitt 80", "gewischt 40 Klicks 0", "zurueck 0 Klicks 0", "Griff gezogen True" });

    CheckUi("Joystick und Pfeiltasten: der Fokus wandert in Richtung, Knopf 0 klickt, der Stick wiederholt, Textfeld und Liste behalten ihre Tasten", uiHead + """
        var log = ""
        var a = new UI.Button("A", 10, 10, 60, 24)
        var b = new UI.Button("B", 90, 10, 60, 24)
        var c = new UI.Button("C", 10, 50, 60, 24)
        var d = new UI.Button("D", 90, 50, 60, 24)
        a.onClick = func () => { log = log + "A" }
        d.onClick = func () => { log = log + "D" }
        ui.Add(a)
        ui.Add(b)
        ui.Add(c)
        ui.Add(d)
        ui.Draw()
        __TestEvent(35, 1, 0, 2)
        ui.Tick()
        print("erstes " + a.focused)
        __TestEvent(35, 1, 0, 0)
        __TestEvent(35, 1, 0, 2)
        ui.Tick()
        print("rechts " + b.focused)
        __TestEvent(35, 1, 0, 0)
        __TestEvent(35, 1, 0, 4)
        ui.Tick()
        print("runter " + d.focused)
        __TestEvent(33, 1, 0)
        ui.Tick()
        print("Knopf 0 " + log)
        __TestEvent(35, 1, 0, 0)
        __TestEvent(35, 1, 0, 8)
        ui.Tick()
        print("links " + c.focused)
        __TestEvent(35, 1, 0, 0)
        __TestEvent(35, 1, 0, 1)
        ui.Tick()
        print("hoch " + a.focused)
        __TestEvent(35, 1, 0, 0)
        __TestEvent(24, 1073741905, 0)
        ui.Tick()
        print("Taste runter " + c.focused)
        __TestEvent(24, 1073741903, 0)
        ui.Tick()
        print("Taste rechts " + d.focused)
        __TestEvent(33, 1, 5)
        ui.Tick()
        print("Knopf 5 (Tab) " + a.focused)
        __TestEvent(33, 1, 4)
        ui.Tick()
        print("Knopf 4 (Umschalt-Tab) " + d.focused)
        """, new[] { "erstes True", "rechts True", "runter True", "Knopf 0 D", "links True", "hoch True", "Taste runter True", "Taste rechts True", "Knopf 5 (Tab) True", "Knopf 4 (Umschalt-Tab) True" });

    CheckUi("Joystick: der Stick wiederholt eine gehaltene Richtung; Totzone und Loslassen", uiHead + """
        var col = new UI.StackPanel(10, 10, 100, 160)
        var e1 = new UI.Button("1", 0, 0, 60, 24)
        var e2 = new UI.Button("2", 0, 0, 60, 24)
        var e3 = new UI.Button("3", 0, 0, 60, 24)
        var e4 = new UI.Button("4", 0, 0, 60, 24)
        col.Add(e1)
        col.Add(e2)
        col.Add(e3)
        col.Add(e4)
        ui.Add(col)
        ui.Draw()
        ui.joyRepeatDelay = 1
        ui.joyRepeatInterval = 1
        __TestEvent(32, 1, 1, 0.4)
        ui.Tick()
        print("Totzone " + e1.focused + " " + e2.focused)
        __TestEvent(32, 1, 1, 0.9)
        ui.Tick()
        print("ausgeschlagen " + e1.focused)
        ui.Tick()
        print("wiederholt " + e2.focused)
        ui.Tick()
        ui.Tick()
        print("am Ende " + e4.focused)
        __TestEvent(32, 1, 1, 0.0)
        ui.Tick()
        __TestEvent(32, 1, 0, -0.9)
        ui.Tick()
        ui.Tick()
        print("Achse 0 links, kein Kandidat: " + e4.focused)
        """, new[] { "Totzone False False", "ausgeschlagen True", "wiederholt True", "am Ende True", "Achse 0 links, kein Kandidat: True" });

    CheckUi("Pfeiltasten: Textfeld behaelt Links/Rechts, eine Liste gibt die Taste am Rand frei, ein Element im ScrollViewer wird sichtbar", uiHead + """
        var tb = new UI.TextBox("abc", 10, 10, 100, 24)
        var btn = new UI.Button("go", 10, 50, 60, 24)
        var lb = new UI.ListBox(10, 90, 100, 60)
        lb.Add("x")
        lb.Add("y")
        lb.Add("z")
        ui.Add(tb)
        ui.Add(btn)
        ui.Add(lb)
        ui.Draw()
        ui.SetFocus(tb)
        __TestEvent(24, 1073741904, 0)
        ui.Tick()
        print("Links " + tb.caret + " " + tb.focused)
        __TestEvent(24, 1073741905, 0)
        ui.Tick()
        print("Runter " + btn.focused)
        __TestEvent(24, 1073741905, 0)
        ui.Tick()
        print("Liste " + lb.focused)
        __TestEvent(24, 1073741905, 0)
        __TestEvent(24, 1073741905, 0)
        __TestEvent(24, 1073741905, 0)
        ui.Tick()
        print("gewaehlt " + lb.selectedIndex)
        __TestEvent(24, 1073741905, 0)
        ui.Tick()
        print("am Ende bleibt " + lb.focused + " " + lb.selectedIndex)
        __TestEvent(24, 1073741906, 0)
        __TestEvent(24, 1073741906, 0)
        __TestEvent(24, 1073741906, 0)
        ui.Tick()
        print("oben raus " + btn.focused + " " + lb.selectedIndex)
        var sv = new UI.ScrollViewer(200, 0, 100, 60)
        var col = new UI.StackPanel()
        var last = undefined
        for (var i = 0; i < 6; i = i + 1) {
            last = new UI.Button("s" + i, 0, 0, 60, 30)
            col.Add(last)
        }
        sv.SetContent(col)
        ui.Add(sv)
        ui.Draw()
        ui.SetFocus(last)
        ui.Draw()
        print("sichtbar " + sv.vbar.offset + " von " + sv.vbar.Max())
        """, new[] { "Links 2 True", "Runter True", "Liste True", "gewaehlt 2", "am Ende bleibt True 2", "oben raus True 0", "sichtbar 120 von 120" });

    CheckUi("Fenster-Resize: AutoResize bringt den Framebuffer auf die gueltige Groesse, sonst bleibt er", """
        var fb = new Framebuffer(100, 80)
        var win = new Window(fb, "Resize")
        print(win.AutoResize)
        win.EnableEvents()
        __TestEvent(4, 150, 120)
        win.Tick()
        print("ohne " + fb.Width() + "x" + fb.Height())
        win.AutoResize = true
        print(win.AutoResize)
        win.NextEvent()
        __TestEvent(4, 150, 120)
        win.Tick()
        print("mit " + fb.Width() + "x" + fb.Height())
        var e = win.NextEvent()
        print(e[0] + " " + e[1] + " " + e[2])
        __TestEvent(4, 0, 50)
        win.Tick()
        print("minimiert " + fb.Width() + "x" + fb.Height())
        __TestEvent(4, 20000, 50)
        win.Tick()
        print("zu gross " + fb.Width() + "x" + fb.Height())
        __TestEvent(4, 16384, 16384)
        win.Tick()
        print("zu viele Pixel " + fb.Width() + "x" + fb.Height())
        __TestEvent(4, 60, 40)
        win.Tick()
        print("klein " + fb.Width() + "x" + fb.Height())
        print(fb.Resize(0, 5) + " " + fb.Resize(5, -1) + " " + fb.Width() + "x" + fb.Height())
        print(fb.Resize(30, 20) + " " + fb.Width() + "x" + fb.Height())
        print(win.RegisterResize(func (int w, int h) => { }))
        """, new[] { "False", "ohne 100x80", "True", "mit 150x120", "4 150 120", "minimiert 150x120", "zu gross 150x120", "zu viele Pixel 150x120", "klein 60x40", "False False 60x40", "True 30x20", "True" });

    CheckUi("Fenster-Resize: der Inhalt bleibt oben links, Renderer und Cursor passen sich an", """
        class Px { static int Get(console, int x, int y) { var v = console.GetPixel(x, y); if (v < 0) { v = v + 4294967296 } return v } }
        var fb = new Framebuffer(40, 28)
        var r = new Renderer(fb)
        r.FillRect(0, 0, 40, 28, new SolidBrush(0xFFFF0000))
        r.Locate(1, 4)
        print(fb.Resize(80, 14))
        print(Px.Get(r, 5, 5) == 4294901760)
        print(Px.Get(r, 60, 5) != 4294901760 && Px.Get(r, 60, 5) == Px.Get(r, 79, 13))
        r.Print("x")
        print(Px.Get(r, 33, 2) == 4278190080)
        print(Px.Get(r, 5, 5) == 4294901760)
        print(fb.Resize(24, 56))
        print(Px.Get(r, 5, 5) == 4294901760)
        """, new[] { "True", "True", "True", "True", "True", "True", "True" });

    CheckUi("Fenster-Resize: UI.Root ordnet neu an und malt in der neuen Groesse", uiHead + """
        class Px2 { static int Get(console, int x, int y) { var v = console.GetPixel(x, y); if (v < 0) { v = v + 4294967296 } return v } }
        var b = new UI.Button("OK", 10, 10, 80, 26)
        ui.Add(b)
        var seen = ""
        ui.onResize = func (int w, int h) => { seen = seen + w + "x" + h + " " }
        ui.Tick()
        print(win.AutoResize)
        print(ui.width + "x" + ui.height)
        __TestEvent(4, 400, 260)
        ui.Tick()
        ui.Tick()
        print(ui.width + "x" + ui.height + " " + fb.Width() + "x" + fb.Height())
        print(seen)
        print(Px2.Get(ui.renderer, 390, 250) == Px2.Get(ui.renderer, 300, 190))
        print(Px2.Get(ui.renderer, 390, 250) != 0)
        __TestEvent(4, 0, 0)
        ui.Tick()
        print(ui.width + "x" + ui.height)
        """, new[] { "True", "320x200", "400x260 400x260", "400x260 ", "True", "True", "400x260" });

    CheckUi("Button: Hover, Druecken, Klick (Abfrage per TakeClicked)", uiHead + """
        var b = new UI.Button("OK", 10, 10, 80, 26)
        ui.Add(b)
        __TestEvent(9, 20.0, 20.0)
        ui.Tick()
        print("hover " + b.hover)
        __TestEvent(8, 1, 20.0, 20.0)
        ui.Tick()
        print("pressed " + b.pressed)
        __TestEvent(11, 1, 20.0, 20.0)
        ui.Tick()
        print("pressed " + b.pressed)
        print("geklickt " + b.TakeClicked())
        print("nochmal " + b.TakeClicked())
        """, new[] { "hover True", "pressed True", "pressed False", "geklickt True", "nochmal False" });

    CheckUi("Button: Loslassen ausserhalb klickt nicht", uiHead + """
        var b = new UI.Button("OK", 10, 10, 80, 26)
        ui.Add(b)
        __TestEvent(8, 1, 20.0, 20.0)
        __TestEvent(11, 1, 200.0, 150.0)
        ui.Tick()
        print("geklickt " + b.TakeClicked())
        """, new[] { "geklickt False" });

    CheckUi("Button: onClick-Lambda sieht die echten globalen Variablen", uiHead + """
        var clicks = 0
        var b = new UI.Button("OK", 10, 10, 80, 26)
        b.onClick = func () => { clicks = clicks + 1 }
        ui.Add(b)
        __TestEvent(8, 1, 20.0, 20.0)
        __TestEvent(11, 1, 20.0, 20.0)
        __TestEvent(8, 1, 20.0, 20.0)
        __TestEvent(11, 1, 20.0, 20.0)
        ui.Tick()
        print("clicks " + clicks)
        """, new[] { "clicks 2" });

    CheckUi("Zeichnen: Flaeche, Hover-Farbe, Text- und Rahmenpixel", uiHead + """
        var b = new UI.Button("OK", 10, 10, 80, 26)
        ui.Add(b)
        ui.Draw()
        var t = ui.theme
        print("back " + (Px.Get(ui.renderer, 300, 190) == t.back.Color))
        print("face " + (Px.Get(ui.renderer, 12, 12) == t.face.Color))
        print("border " + (Px.Get(ui.renderer, 10, 10) == t.border.Color))
        var textPixels = 0
        for (var y = 10; y < 36; y = y + 1) {
            for (var x = 10; x < 90; x = x + 1) {
                if (Px.Get(ui.renderer, x, y) == t.text.Color) { textPixels = textPixels + 1 }
            }
        }
        print("text " + (textPixels > 20))
        __TestEvent(9, 20.0, 20.0)
        ui.Tick()
        ui.Draw()
        print("hover " + (Px.Get(ui.renderer, 12, 12) == t.faceHover.Color))
        """, new[] { "back True", "face True", "border True", "text True", "hover True" });

    CheckUi("CheckBox: Klick und Leertaste schalten um", uiHead + """
        var c = new UI.CheckBox("Option", 10, 10)
        ui.Add(c)
        ui.Draw()
        __TestEvent(8, 1, 15.0, 15.0)
        __TestEvent(11, 1, 15.0, 15.0)
        ui.Tick()
        print("an " + c.isChecked + " " + c.TakeChanged())
        __TestEvent(24, 32, 0)
        ui.Tick()
        print("aus " + c.isChecked)
        """, new[] { "an True True", "aus False" });

    CheckUi("TextBox: Fokus per Klick, Eingabe, Rueck-/Entf-Taste, Pfeile, Enter", uiHead + """
        var t = new UI.TextBox("", 10, 10, 160, 24)
        ui.Add(t)
        ui.Draw()
        __TestEvent(3, "Hallo")
        ui.Tick()
        print("ohne Fokus '" + t.text + "'")
        __TestEvent(8, 1, 20.0, 20.0)
        __TestEvent(11, 1, 20.0, 20.0)
        __TestEvent(3, "Hallo")
        ui.Tick()
        print("eingegeben '" + t.text + "' caret " + t.caret)
        __TestEvent(24, 8, 0)
        ui.Tick()
        print("rueck '" + t.text + "'")
        __TestEvent(24, 1073741904, 0)
        __TestEvent(24, 1073741904, 0)
        __TestEvent(3, "X")
        ui.Tick()
        print("links+X '" + t.text + "' caret " + t.caret)
        __TestEvent(24, 127, 0)
        ui.Tick()
        print("entf '" + t.text + "'")
        __TestEvent(24, 1073741898, 0)
        ui.Tick()
        print("pos1 " + t.caret)
        __TestEvent(24, 1073741901, 0)
        ui.Tick()
        print("ende " + t.caret)
        __TestEvent(24, 13, 0)
        ui.Tick()
        print("enter " + t.TakeEntered() + " geaendert " + t.TakeChanged())
        """, new[] { "ohne Fokus ''", "eingegeben 'Hallo' caret 5", "rueck 'Hall'", "links+X 'HaXll' caret 3", "entf 'HaXl'", "pos1 0", "ende 4", "enter True geaendert True" });

    CheckUi("TextBox: Klick setzt die Einfuegemarke, maxLength, scrollt bei langem Text", uiHead + """
        var t = new UI.TextBox("abcdef", 10, 10, 160, 24)
        t.maxLength = 8
        ui.Add(t)
        ui.Draw()
        __TestEvent(8, 1, 10.0 + 4.0 + 8.0 * 2.0 + 1.0, 20.0)
        __TestEvent(11, 1, 10.0 + 4.0 + 8.0 * 2.0 + 1.0, 20.0)
        ui.Tick()
        print("caret " + t.caret)
        __TestEvent(3, "123456")
        ui.Tick()
        print("max '" + t.text + "'")
        t.SetText("0123456789012345678901234567890123456789")
        ui.Draw()
        print("scroll " + (t.scroll > 0) + " caret " + t.caret)
        """, new[] { "caret 2", "max 'abcdef'", "scroll True caret 40" });

    CheckUi("Tab wechselt den Fokus (Umschalt rueckwaerts), deaktivierte und unsichtbare werden uebersprungen", uiHead + """
        var a = new UI.Button("A", 10, 10)
        var b = new UI.Button("B", 10, 50)
        var c = new UI.TextBox("", 10, 90)
        var d = new UI.Button("D", 10, 130)
        b.enabled = false
        d.visible = false
        ui.Add(a)
        ui.Add(b)
        ui.Add(c)
        ui.Add(d)
        __TestEvent(24, 9, 0)
        ui.Tick()
        print("1: " + a.focused + " " + c.focused)
        __TestEvent(24, 9, 0)
        ui.Tick()
        print("2: " + a.focused + " " + c.focused)
        __TestEvent(24, 9, 0)
        ui.Tick()
        print("3: " + a.focused + " " + c.focused)
        __TestEvent(24, 9, 1)
        ui.Tick()
        print("zurueck: " + a.focused + " " + c.focused)
        """, new[] { "1: True False", "2: False True", "3: True False", "zurueck: False True" });

    CheckUi("Stack ordnet an; Panel verschachtelt Koordinaten; deaktiviert bekommt keine Klicks", uiHead + """
        var panel = new UI.Panel(100, 20, 200, 160)
        var stack = new UI.Stack(10, 10, 150, 120, false, 6, 4)
        var b1 = new UI.Button("Eins", 0, 0, 100, 20)
        var b2 = new UI.Button("Zwei", 0, 0, 100, 30)
        stack.Add(b1)
        stack.Add(b2)
        panel.Add(stack)
        ui.Add(panel)
        ui.Draw()
        print("b1 " + b1.ax + "," + b1.ay + " b2 " + b2.ax + "," + b2.ay)
        __TestEvent(8, 1, 120.0, 44.0)
        __TestEvent(11, 1, 120.0, 44.0)
        ui.Tick()
        print("b1 " + b1.TakeClicked() + " b2 " + b2.TakeClicked())
        b2.enabled = false
        __TestEvent(8, 1, 120.0, 65.0)
        __TestEvent(11, 1, 120.0, 65.0)
        ui.Tick()
        print("b2 deaktiviert " + b2.TakeClicked())
        """, new[] { "b1 114,34 b2 114,60", "b1 True b2 False", "b2 deaktiviert False" });

    CheckUi("Elemente gehoeren ihrem Container: eine Hilfsfunktion darf sie anlegen", uiHead + """
        class Helper {
            static Build(root) {
                var b = new UI.Button("Lokal", 10, 10, 80, 26)
                root.Add(b)
                var l = new UI.Label("Text", 10, 50)
                root.Add(l)
            }
        }
        Helper.Build(ui)
        ui.Draw()
        var first = ui.content.children[0]
        print(first.text + " " + first.ax)
        print(ui.content.children[1].actualWidth)
        """, new[] { "Lokal 10", "32" });

    // ---- Layout: Measure/Arrange wie WPF, in ganzen Pixeln ----
    CheckUi("Layout: StackPanel (Abstand, Innenabstand, Stretch quer), unsichtbare Kinder zaehlen nicht", uiHead + """
        var sp = new UI.StackPanel(10, 10, 200, 200, false, 5, 2)
        var la = new UI.Label("ab")
        var ba = new UI.Button("x", 0, 0, 100, 20)
        var bb = new UI.Button("y", 0, 0, 100, 20)
        sp.Add(la)
        sp.Add(ba)
        sp.Add(bb)
        ui.Add(sp)
        ui.Draw()
        print("label " + la.rx + "," + la.ry + " " + la.actualWidth + "x" + la.actualHeight)
        print("btn1 " + ba.rx + "," + ba.ry + " " + ba.actualWidth + "x" + ba.actualHeight)
        print("btn2 " + bb.rx + "," + bb.ry + " abs " + bb.ax + "," + bb.ay)
        ba.visible = false
        ui.Draw()
        print("ohne btn1: btn2 " + bb.rx + "," + bb.ry)
        """, new[] { "label 2,2 196x14", "btn1 2,21 100x20", "btn2 2,46 abs 12,56", "ohne btn1: btn2 2,21" });

    CheckUi("Layout: Ausrichtung, Rand, Mindest- und Hoechstgroesse", uiHead + """
        var sp2 = new UI.StackPanel(0, 0, 200, 100)
        var c = new UI.Button("c", 0, 0, 60, 20)
        c.halign = UI.HAlign.Center
        var r = new UI.Button("r", 0, 0, 60, 20)
        r.halign = UI.HAlign.Right
        r.margin = new UI.Thickness(5)
        sp2.Add(c)
        sp2.Add(r)
        ui.Add(sp2)
        var sp3 = new UI.StackPanel(0, 100, 300, 100)
        var m1 = new UI.Button("m", 0, 0, 10, 10)
        m1.minWidth = 30
        var m2 = new UI.Button("m", 0, 0, 100, 10)
        m2.maxWidth = 40
        sp3.Add(m1)
        sp3.Add(m2)
        ui.Add(sp3)
        ui.Draw()
        print("mitte " + c.rx + " rechts " + r.rx + "," + r.ry)
        print("min/max " + m1.actualWidth + " " + m2.actualWidth)
        var t = new UI.Thickness(3, 4)
        var u = new UI.Thickness(1, 2, 3, 4)
        print(t.left + " " + t.top + " " + t.right + " " + t.bottom + " | " + u.left + " " + u.top + " " + u.right + " " + u.bottom)
        """, new[] { "mitte 70 rechts 135,25", "min/max 30 40", "3 4 3 4 | 1 2 3 4" });

    CheckUi("Layout: Grid (feste, Stern- und Auto-Spuren, Spannen), WrapPanel und Border", uiHead + """
        var g = new UI.Grid(0, 0, 300, 100)
        g.SetColumns("50, *, auto")
        g.SetRows("20, *")
        var g1 = new UI.Button("a", 0, 0, -1, -1)
        var g2 = new UI.Border()
        var g3 = new UI.Label("lbl")
        g.AddAt(g1, 0, 0)
        g.AddAt(g2, 1, 1)
        g.AddAt(g3, 0, 2)
        ui.Add(g)
        var w = new UI.WrapPanel(0, 100, 100, 100)
        for (var i = 0; i < 7; i = i + 1) { var it = new UI.Button("w", 0, 0, 30, 10); w.Add(it) }
        ui.Add(w)
        var bo = new UI.Border(150, 100, 100, 60)
        bo.padding = new UI.Thickness(3)
        var inner = new UI.Label("in")
        bo.SetChild(inner)
        ui.Add(bo)
        ui.Draw()
        print("g1 " + g1.rx + "," + g1.ry + " " + g1.actualWidth + "x" + g1.actualHeight)
        print("g2 " + g2.rx + "," + g2.ry + " " + g2.actualWidth + "x" + g2.actualHeight)
        print("g3 " + g3.rx + "," + g3.ry + " " + g3.actualWidth + "x" + g3.actualHeight)
        var last = w.children[6]
        print("wrap " + last.rx + "," + last.ry)
        print("border " + inner.rx + "," + inner.ry + " " + inner.actualWidth + "x" + inner.actualHeight)
        """, new[] { "g1 0,0 50x20", "g2 50,20 226x80", "g3 276,0 24x20", "wrap 0,20", "border 4,4 92x52" });

    CheckUi("Layout: DockPanel (Raender in der Reihenfolge, der Rest fuellt)", uiHead + """
        var d = new UI.DockPanel(0, 0, 200, 100)
        var dl = new UI.Button("l", 0, 0, 30, -1)
        var dt = new UI.Button("t", 0, 0, -1, 20)
        var dr = new UI.Button("r", 0, 0, 40, -1)
        var df = new UI.Button("f", 0, 0, -1, -1)
        d.AddDocked(dl, UI.Dock.Left)
        d.AddDocked(dt, UI.Dock.Top)
        d.AddDocked(dr, UI.Dock.Right)
        d.Add(df)
        ui.Add(d)
        ui.Draw()
        print("l " + dl.rx + "," + dl.ry + " " + dl.actualWidth + "x" + dl.actualHeight)
        print("t " + dt.rx + "," + dt.ry + " " + dt.actualWidth + "x" + dt.actualHeight)
        print("r " + dr.rx + "," + dr.ry + " " + dr.actualWidth + "x" + dr.actualHeight)
        print("f " + df.rx + "," + df.ry + " " + df.actualWidth + "x" + df.actualHeight)
        """, new[] { "l 0,0 30x100", "t 30,0 170x20", "r 160,20 40x80", "f 30,20 130x80" });

    CheckUi("Styles: impliziter Style (Setter, Trigger mit Zurueckstellen, basedOn), expliziter Style, Gueltigkeitsbereich", uiHead + """
        var red = new SolidBrush(UI.Color.Rgb(255, 0, 0))
        var baseStyle = new UI.Style("Button")
        baseStyle.Set("margin", new UI.Thickness(3))
        var st = new UI.Style("Button")
        st.basedOn = baseStyle
        st.Set("background", red)
        var tr = new UI.Trigger("hover", true)
        tr.Set("width", 150)
        st.AddTrigger(tr)
        ui.resources.AddStyle(st)
        var sp = new UI.StackPanel(0, 0, 300, 200)
        var b1 = new UI.Button("one", 0, 0, 100, 20)
        sp.Add(b1)
        ui.Add(sp)
        ui.Draw()
        print("b1 " + b1.margin.left + " " + (b1.background == red) + " " + b1.actualWidth + " " + b1.rx + "," + b1.ry)
        b1.hover = true
        ui.Draw()
        print("hover " + b1.actualWidth)
        b1.hover = false
        ui.Draw()
        print("zurueck " + b1.actualWidth)
        // a style in the panel only applies below it and before that of the root
        var inner = new UI.StackPanel(0, 100, 300, 100)
        var local = new UI.Style("Button")
        local.Set("margin", new UI.Thickness(7))
        inner.Resources().AddStyle(local)
        var b2 = new UI.Button("two", 0, 0, 100, 20)
        inner.Add(b2)
        ui.Add(inner)
        ui.Draw()
        print("inner " + b2.margin.left + " " + (b2.background == red) + " " + b2.ry)
        var s2 = new UI.Style()
        s2.Set("margin", new UI.Thickness(9))
        s2.Set("nichtda", 1)
        b1.style = s2
        ui.Draw()
        print("explizit " + b1.margin.left + " " + (b1.background == red) + " " + b1.actualWidth)
        """, new[] { "b1 3 True 100 3,3", "hover 150", "zurueck 100", "inner 7 False 7", "explizit 9 True 100" });

    CheckUi("Vorlagen: ControlTemplate (Teile, Bindung ans Element, Trigger auf einen Teil), Bindung zwischen Elementen, Ressourcen", uiHead + """
        var red = new SolidBrush(UI.Color.Rgb(255, 0, 0))
        var tpl = new UI.ControlTemplate(func (owner) => {
            var bd = new UI.Border()
            bd.name = "bd"
            bd.background = new SolidBrush(UI.Color.Rgb(0, 255, 0))
            bd.padding = new UI.Thickness(4)
            var t = new UI.Label("")
            t.name = "txt"
            bd.SetChild(t)
            return bd
        })
        tpl.Bind("txt", "text", "text")
        var tr = new UI.Trigger("pressed", true)
        tr.Set("background", red, "bd")
        tpl.AddTrigger(tr)
        var sp = new UI.StackPanel(0, 0, 300, 200)
        var b = new UI.Button("hello", 0, 0, -1, -1)
        b.halign = UI.HAlign.Left
        b.template = tpl
        sp.Add(b)
        ui.Add(sp)
        ui.Draw()
        print("groesse " + b.actualWidth + "x" + b.actualHeight)
        var part = b.FindPart("bd")
        var green = part.background
        print("teil " + (part != undefined) + " " + (part.background != red) + " " + b.FindPart("txt").text)
        b.pressed = true
        ui.Draw()
        print("gedrueckt " + (part.background == red))
        b.pressed = false
        ui.Draw()
        print("losgelassen " + (part.background == green))
        b.text = "hi"
        ui.Draw()
        print("text " + b.FindPart("txt").text + " " + b.actualWidth)
        var tb = new UI.TextBox("abc")
        var l2 = new UI.Label("")
        var l3 = new UI.TextBox("")
        sp.Add(tb)
        sp.Add(l2)
        sp.Add(l3)
        l2.Bind("text", tb, "text")
        l3.Bind("text", tb, "text", true)
        ui.Draw()
        print("bindung " + l2.text + " " + l3.text)
        tb.text = "xyz"
        ui.Draw()
        print("quelle " + l2.text + " " + l3.text)
        l3.text = "zurueck"
        ui.Draw()
        ui.Draw()
        print("ziel " + tb.text + " " + l2.text)
        var tplStyle = new UI.Style("CheckBox")
        tplStyle.Set("template", tpl)
        ui.resources.AddStyle(tplStyle)
        var cb = new UI.CheckBox("kaestchen")
        cb.halign = UI.HAlign.Left
        sp.Add(cb)
        ui.Draw()
        print("stilvorlage " + cb.FindPart("txt").text + " " + cb.actualWidth)
        sp.Resources().Set("akzent", 42)
        ui.resources.Set("rot", red)
        print("ressource " + b.FindResource("akzent") + " " + b.FindResource("fehlt") + " " + (b.FindResource("rot") == red))
        """, new[] { "groesse 50x24", "teil True True hello", "gedrueckt True", "losgelassen True", "text hi 26", "bindung abc abc", "quelle xyz xyz", "ziel zurueck zurueck", "stilvorlage kaestchen 82", "ressource 42 undefined True" });

    CheckUi("ScrollViewer: Leisten, Ausschnitt, Mausrad, Ziehen am Griff, Umbrechen ohne waagerechte Leiste", uiHead + """
        var sv = new UI.ScrollViewer(0, 0, 100, 80)
        sv.hmode = UI.ScrollMode.Auto
        var big = new UI.Canvas(0, 0, 300, 200)
        big.Add(new UI.Button("x", 280, 180, 20, 20))
        sv.SetContent(big)
        ui.Add(sv)
        ui.Draw()
        print("beide Leisten " + sv.showV + " " + sv.showH + " Ausschnitt " + sv.viewW + "x" + sv.viewH + " Inhalt " + sv.hbar.extent + "x" + sv.vbar.extent)
        sv.vbar.Set(1000)
        sv.hbar.Set(1000)
        ui.Draw()
        print("ganz unten rechts " + sv.hbar.offset + "," + sv.vbar.offset + " Kind bei " + big.rx + "," + big.ry)
        ui.MouseWheel(0, 1, 20, 20)
        ui.Draw()
        print("Rad nach oben " + sv.vbar.offset)
        ui.MouseWheel(0, -1, 20, 20)
        ui.MouseWheel(0, -1, 20, 20)
        ui.Draw()
        print("Rad nach unten " + sv.vbar.offset)
        ui.MouseWheel(-2, 0, 20, 20)
        ui.Draw()
        print("Rad seitlich " + sv.hbar.offset)
        // drag at the thumb of the vertical bar: grab at the top and drag all the way down
        sv.vbar.Set(0)
        ui.Draw()
        ui.MouseDown(1, 94, 4)
        ui.MouseMove(94, 200)
        ui.MouseUp(1, 94, 200)
        ui.Draw()
        print("gezogen " + sv.vbar.offset)
        var sv2 = new UI.ScrollViewer(110, 0, 100, 80)
        sv2.SetContent(new UI.Label("short"))
        ui.Add(sv2)
        ui.Draw()
        print("klein " + sv2.showV + " " + sv2.showH)
        var sv3 = new UI.ScrollViewer(0, 100, 100, 60)
        var wp = new UI.WrapPanel()
        for (var i = 0; i < 12; i = i + 1) { wp.Add(new UI.Button("b" + i, 0, 0, 30, 20)) }
        sv3.SetContent(wp)
        ui.Add(sv3)
        ui.Draw()
        print("umbrechen " + sv3.showV + " " + sv3.showH + " " + wp.actualWidth + "x" + wp.actualHeight + " Ausschnitt " + sv3.viewW)
        """, new[] { "beide Leisten True True Ausschnitt 88x68 Inhalt 300x200", "ganz unten rechts 212,132 Kind bei -212,-132", "Rad nach oben 90", "Rad nach unten 132", "Rad seitlich 212", "gezogen 132", "klein False False", "umbrechen True False 88x120 Ausschnitt 88" });

    CheckUi("Listen: ListBox (Auswahl, Tastatur, Rad), ListView (Spalten, Sortieren per Kopfzeile), CollectionView (Filter, Sortierung, aktuelles Element)", uiHead + """
        class Person {
            string name
            int age
            construct(string name, int age) {
                this.name = name
                this.age = age
            }
        }
        var lb = new UI.ListBox(10, 10, 100, 80)
        for (var i = 0; i < 12; i = i + 1) { lb.Add("item " + i) }
        ui.Add(lb)
        var lv = new UI.ListView(130, 10, 180, 100)
        lv.AddColumn("Name", "name", 100)
        lv.AddColumn("Age", "age", -1)
        lv.Add(new Person("Carol", 41))
        lv.Add(new Person("Alice", 30))
        lv.Add(new Person("Bob", 25))
        ui.Add(lv)
        ui.Draw()
        print("Liste " + lb.selectedIndex + " Leiste " + lb.scroller.Needed() + " Anzahl " + lb.count)
        ui.MouseDown(1, 20, 25)
        ui.MouseUp(1, 20, 25)
        ui.Draw()
        print("Klick " + lb.selectedIndex + " " + lb.selectedItem + " " + lb.TakeChanged() + " " + lb.TakeChanged())
        ui.KeyDown(1073741905, 0)
        ui.KeyDown(1073741905, 0)
        print("Pfeil " + lb.selectedIndex + " " + lb.selectedItem)
        ui.KeyDown(1073741898, 0)
        print("Pos1 " + lb.selectedIndex)
        ui.KeyDown(1073741901, 0)
        ui.Draw()
        print("Ende " + lb.selectedIndex + " Versatz " + lb.scroller.offset)
        ui.MouseWheel(0, 1, 20, 30)
        ui.Draw()
        print("Rad " + lb.scroller.offset)
        lb.selectedItem = "item 3"
        print("per selectedItem " + lb.selectedIndex)
        var entered = 0
        lb.onActivate = func () => { entered = entered + 1 }
        ui.KeyDown(13, 0)
        print("Enter " + entered + " " + lb.TakeActivated())
        // Kopfzeile: Klick sortiert
        ui.MouseDown(1, 140, 15)
        ui.MouseUp(1, 140, 15)
        ui.Draw()
        print("sortiert " + lv.Rows()[0].name + " " + lv.sortedBy)
        ui.MouseDown(1, 140, 15)
        ui.MouseUp(1, 140, 15)
        ui.Draw()
        print("umgekehrt " + lv.Rows()[0].name)
        var cv = new UI.CollectionView(lv.items)
        cv.filter = func (p) => p.age > 26
        cv.SortBy("age", true)
        cv.Update()
        print("Sicht " + cv.count + " " + cv[0].name + " " + cv[1].name)
        cv.MoveCurrentToFirst()
        cv.MoveCurrentToNext()
        print("aktuell " + cv.CurrentItem().name + " " + cv.MoveCurrentTo(5))
        cv.comparer = func (a, b) => a.age - b.age
        cv.Invalidate()
        cv.Update()
        print("eigener Vergleich " + cv[0].name + " aktuell " + cv.CurrentItem().name)
        var names = new UI.ListBox(10, 100, 100, 80)
        names.displayMember = "name"
        names.SetView(new UI.CollectionView(lv.items))
        ui.Add(names)
        ui.Draw()
        print("Anzeigeeigenschaft " + names.count + " " + names.ItemText(names.Rows()[1]))
        names.view.sortMember = "name"
        names.view.Invalidate()
        ui.Draw()
        print("sortierte Sicht " + names.ItemText(names.Rows()[0]))
        names.Select(2)
        print("aktuell der Sicht " + names.view.current + " " + names.selectedItem.name)
        """, new[] { "Liste -1 Leiste True Anzahl 12", "Klick 0 item 0 True False", "Pfeil 2 item 2", "Pos1 0", "Ende 11 Versatz 162", "Rad 102", "per selectedItem 3", "Enter 1 True", "sortiert Alice name", "umgekehrt Carol", "Sicht 2 Carol Alice", "aktuell Alice False", "eigener Vergleich Carol aktuell Alice", "Anzeigeeigenschaft 3 Alice", "sortierte Sicht Alice", "aktuell der Sicht 2 Carol" });

    CheckUi("TreeView: Auf- und Zuklappen, Auswahl, Tastatur", uiHead + """
        var tv = new UI.TreeView(10, 10, 150, 100)
        var a = tv.AddNode(new UI.TreeNode("Animals"))
        var d = a.Add(new UI.TreeNode("Dogs"))
        d.Add(new UI.TreeNode("Rex"))
        d.Add(new UI.TreeNode("Fido"))
        a.Add(new UI.TreeNode("Cats"))
        var b = tv.AddNode(new UI.TreeNode("Plants"))
        b.Add(new UI.TreeNode("Oak"))
        a.expanded = true
        ui.Add(tv)
        ui.Draw()
        print("sichtbar " + tv.visibleNodes.count)
        ui.MouseDown(1, 32, 36)
        ui.MouseUp(1, 32, 36)
        ui.Draw()
        print("Dogs aufgeklappt " + tv.visibleNodes.count + " " + d.expanded + " ausgewaehlt " + (tv.selected == undefined))
        ui.MouseDown(1, 80, 36)
        ui.MouseUp(1, 80, 36)
        ui.Draw()
        print("gewaehlt " + tv.selected.text + " " + tv.TakeChanged())
        ui.KeyDown(1073741905, 0)
        ui.KeyDown(1073741905, 0)
        print("Pfeil ab " + tv.selected.text)
        ui.KeyDown(1073741904, 0)
        print("Links zum Eltern " + tv.selected.text)
        ui.KeyDown(1073741904, 0)
        ui.Draw()
        print("Links klappt zu " + d.expanded + " " + tv.visibleNodes.count)
        ui.KeyDown(1073741903, 0)
        ui.KeyDown(1073741903, 0)
        print("Rechts klappt auf, dann zum Kind " + d.expanded + " " + tv.selected.text)
        ui.KeyDown(1073741898, 0)
        print("Pos1 " + tv.selected.text + " Tiefe " + a.Depth() + " " + d.children[0].Depth())
        ui.KeyDown(13, 0)
        ui.Draw()
        print("Enter klappt um " + a.expanded + " " + tv.visibleNodes.count)
        """, new[] { "sichtbar 4", "Dogs aufgeklappt 6 True ausgewaehlt True", "gewaehlt Dogs True", "Pfeil ab Fido", "Links zum Eltern Dogs", "Links klappt zu False 4", "Rechts klappt auf, dann zum Kind True Cats", "Pos1 Animals Tiefe 0 2", "Enter klappt um False 2" });

    CheckUi("Menues: MenuBar, Untermenue, Haken, gesperrte Zeile, Tastatur, Kontextmenue, Klick daneben", uiHead + """
        var mb = new UI.MenuBar(0, 0, 320, -1)
        var file = new UI.MenuItem("File")
        var cnt = 0
        file.Add(new UI.MenuItem("New", func () => { cnt = cnt + 1 }))
        file.Add(UI.MenuItem.Separator())
        var rec = file.Add(new UI.MenuItem("Recent"))
        rec.Add(new UI.MenuItem("a.txt", func () => { cnt = cnt + 10 }))
        var chk = file.Add(new UI.MenuItem("Check"))
        chk.checkable = true
        var off = file.Add(new UI.MenuItem("Off", func () => { cnt = cnt + 100 }))
        off.enabled = false
        mb.Add(file)
        var edit = new UI.MenuItem("Edit")
        edit.Add(new UI.MenuItem("Copy", func () => { cnt = cnt + 1000 }))
        mb.Add(edit)
        ui.Add(mb)
        ui.Draw()
        print("Leiste " + mb.actualWidth + "x" + mb.actualHeight)
        ui.MouseMove(10, 5)
        ui.MouseDown(1, 10, 5)
        ui.MouseUp(1, 10, 5)
        ui.Draw()
        print("offen " + ui.popups.count + " " + mb.openIndex)
        // the pointer over the second title switches the menu
        ui.MouseMove(60, 5)
        ui.Draw()
        print("gewechselt " + ui.popups.count + " " + mb.openIndex)
        // back to File, open submenu Recent via pointer
        ui.MouseMove(10, 5)
        ui.Draw()
        var menu = ui.popups[0]
        var y = menu.RowTop(ui, 2) + 4
        ui.MouseMove(menu.ax + 10, y)
        ui.Draw()
        print("Untermenue " + ui.popups.count)
        var subMenu = ui.popups[1]
        var sx = subMenu.ax + 10
        var sy = subMenu.ay + 4
        ui.MouseDown(1, sx, sy)
        ui.MouseUp(1, sx, sy)
        ui.Draw()
        print("gewaehlt " + cnt + " offen " + ui.popups.count)
        // keyboard: Enter on New
        ui.MouseDown(1, 10, 5)
        ui.MouseUp(1, 10, 5)
        ui.Draw()
        ui.KeyDown(1073741905, 0)
        ui.KeyDown(13, 0)
        print("Tastatur " + cnt + " offen " + ui.popups.count)
        // check mark and disabled row
        ui.MouseDown(1, 10, 5)
        ui.MouseUp(1, 10, 5)
        ui.Draw()
        var m2 = ui.popups[0]
        var cx = m2.ax + 10
        var cy = m2.RowTop(ui, 3) + 4
        ui.MouseDown(1, cx, cy)
        ui.MouseUp(1, cx, cy)
        ui.MouseDown(1, 10, 5)
        ui.MouseUp(1, 10, 5)
        ui.Draw()
        var m3 = ui.popups[0]
        var dx = m3.ax + 10
        var dy = m3.RowTop(ui, 4) + 4
        ui.MouseDown(1, dx, dy)
        ui.MouseUp(1, dx, dy)
        ui.Draw()
        print("Haken " + chk.isChecked + " gesperrt " + cnt + " offen " + ui.popups.count)
        ui.KeyDown(27, 0)
        print("Escape " + ui.popups.count)
        // Kontextmenue
        var cm = new List()
        cm.Add(new UI.MenuItem("Copy", func () => { cnt = cnt + 5 }))
        cm.Add(new UI.MenuItem("Paste", func () => { cnt = cnt + 50 }))
        var target = new UI.Label("right click me", 150, 150)
        target.contextMenu = cm
        ui.Add(target)
        ui.Draw()
        ui.MouseDown(3, 155, 155)
        ui.MouseUp(3, 155, 155)
        ui.Draw()
        print("Kontextmenue " + ui.popups.count)
        ui.KeyDown(1073741905, 0)
        ui.KeyDown(1073741905, 0)
        ui.KeyDown(13, 0)
        print("Paste " + cnt + " " + ui.popups.count)
        ui.MouseDown(3, 155, 155)
        ui.MouseUp(3, 155, 155)
        ui.Draw()
        ui.MouseDown(1, 5, 190)
        ui.MouseUp(1, 5, 190)
        ui.Draw()
        print("Klick daneben " + ui.popups.count)
        """, new[] { "Leiste 320x22", "offen 1 0", "gewechselt 1 1", "Untermenue 2", "gewaehlt 10 offen 0", "Tastatur 11 offen 0", "Haken True gesperrt 11 offen 1", "Escape 0", "Kontextmenue 1", "Paste 61 0", "Klick daneben 0" });

    CheckUi("RadioButtons, AutoSuggestBox, ToolBar und Image (alle Dehnungsarten)", uiHead + """
        var rb = new UI.RadioButtons(10, 10)
        rb.header = "Size"
        rb.Add("Small")
        rb.Add("Medium")
        rb.Add("Large")
        ui.Add(rb)
        var asb = new UI.AutoSuggestBox("", 150, 10, 150, 24)
        asb.AddSuggestion("apple")
        asb.AddSuggestion("apricot")
        asb.AddSuggestion("banana")
        asb.AddSuggestion("blueberry")
        ui.Add(asb)
        var tb = new UI.ToolBar(0, 120, 320, -1)
        var clicks = 0
        tb.AddButton("Open", func () => { clicks = clicks + 1 })
        tb.AddSeparator()
        tb.AddButton("Save")
        ui.Add(tb)
        ui.Draw()
        print("RadioButtons " + rb.actualWidth + "x" + rb.actualHeight)
        ui.MouseDown(1, 20, 30)
        ui.MouseUp(1, 20, 30)
        ui.Draw()
        print("gewaehlt " + rb.selectedIndex + " " + rb.selectedItem + " " + rb.TakeChanged())
        ui.KeyDown(1073741905, 0)
        ui.KeyDown(1073741905, 0)
        print("Pfeile " + rb.selectedIndex)
        ui.KeyDown(1073741906, 0)
        print("hoch " + rb.selectedIndex)
        ui.MouseDown(1, 160, 20)
        ui.MouseUp(1, 160, 20)
        ui.TextInput("ap")
        ui.Draw()
        print("Vorschlaege " + ui.popups.count + " " + asb.popupList.count)
        ui.KeyDown(1073741905, 0)
        ui.Draw()
        print("Auswahl " + asb.popupList.selectedIndex)
        ui.KeyDown(13, 0)
        ui.Draw()
        print("uebernommen " + asb.text + " " + ui.popups.count + " " + asb.TakeChosen() + " " + asb.chosenItem)
        asb.SetText("")
        ui.TextInput("B")
        ui.Draw()
        print("ohne Gross/Klein " + asb.popupList.count)
        ui.MouseDown(1, 160, 10 + 24 + 12)
        ui.MouseUp(1, 160, 10 + 24 + 12)
        ui.Draw()
        print("Klick auf Vorschlag " + asb.text + " " + ui.popups.count)
        asb.SetText("")
        ui.TextInput("x")
        ui.Draw()
        print("kein Treffer " + ui.popups.count)
        asb.provider = func (text) => {
            var l = new List()
            l.Add(text + "1")
            l.Add(text + "2")
            return l
        }
        ui.TextInput("y")
        ui.Draw()
        print("Anbieter " + asb.popupList.count + " " + asb.popupList.Rows()[1])
        ui.KeyDown(27, 0)
        print("Escape " + ui.popups.count)
        ui.MouseDown(1, 20, 125)
        ui.MouseUp(1, 20, 125)
        ui.Draw()
        print("Leiste " + clicks + " " + tb.children.count)
        var img = new Framebuffer(16, 16)
        var ir = new Renderer(img)
        ir.FillRect(0, 0, 16, 16, new SolidBrush(UI.Color.Rgb(255, 0, 0)))
        ir.FillRect(4, 4, 8, 8, new SolidBrush(UI.Color.Rgb(0, 0, 255)))
        var im = new UI.Image(img, 10, 160, 60, 30)
        ui.Add(im)
        var im2 = new UI.Image(img, 100, 160, 60, 30)
        im2.stretch = UI.Stretch.UniformToFill
        ui.Add(im2)
        var im3 = new UI.Image(img, 180, 160, 60, 30)
        im3.stretch = UI.Stretch.Fill
        ui.Add(im3)
        var im4 = new UI.Image(img, 250, 160, 20, 20)
        im4.stretch = UI.Stretch.None
        ui.Add(im4)
        ui.Draw()
        var R = UI.Color.Rgb(255, 0, 0)
        var B = UI.Color.Rgb(0, 0, 255)
        print("Uniform " + (Px.Get(ui.renderer, 27, 163) == R) + " " + (Px.Get(ui.renderer, 40, 175) == B) + " " + (Px.Get(ui.renderer, 15, 175) == ui.theme.back.Color))
        print("Fuellen " + (Px.Get(ui.renderer, 101, 161) == R) + " " + (Px.Get(ui.renderer, 130, 175) == B) + " " + (Px.Get(ui.renderer, 101, 175) == R))
        print("Strecken " + (Px.Get(ui.renderer, 181, 161) == R) + " " + (Px.Get(ui.renderer, 210, 175) == B))
        print("Keine " + (Px.Get(ui.renderer, 251, 161) == R) + " " + (Px.Get(ui.renderer, 260, 170) == B) + " " + im4.actualWidth)
        """, new[] { "RadioButtons 68x74", "gewaehlt 0 Small True", "Pfeile 2", "hoch 1", "Vorschlaege 1 2", "Auswahl 0", "uebernommen apple 0 True apple", "ohne Gross/Klein 2", "Klick auf Vorschlag banana 0", "kein Treffer 0", "Anbieter 2 blueberry", "Escape 0", "Leiste 1 3", "Uniform True True True", "Fuellen True True True", "Strecken True True", "Keine True True 20" });

    CheckUi("Formen: Rectangle, Ellipse, Line, Path (Pfaddaten, Kurven), Geometry, DrawingCanvas", uiHead + """
        print(UI.M.Sin(30) + " " + UI.M.Sin(90) + " " + UI.M.Cos(60) + " " + UI.M.Sin(-30) + " " + UI.M.Sin(210) + " " + UI.M.Cos(0))
        var r = new UI.Rectangle(10, 10, 60, 40)
        r.fill = new SolidBrush(UI.Color.Rgb(255, 200, 0))
        r.stroke = new Pen(UI.Color.Rgb(0, 0, 0))
        ui.Add(r)
        var e = new UI.Ellipse(80, 10, 60, 40)
        e.fill = new SolidBrush(UI.Color.Rgb(0, 200, 100))
        ui.Add(e)
        var l = new UI.Line(0, 0, 50, 30)
        l.stroke = new Pen(UI.Color.Rgb(200, 0, 0))
        var lc = new UI.Canvas(150, 10, 60, 40)
        lc.Add(l)
        ui.Add(lc)
        var p = new UI.Path(undefined, 10, 70)
        p.SetData("M 0 0 L 40 0 L 40 30 L 20 50 L 0 30 Z")
        p.fill = new SolidBrush(UI.Color.Rgb(100, 100, 255))
        p.stroke = new Pen(UI.Color.Rgb(0, 0, 80))
        ui.Add(p)
        var p2 = new UI.Path(undefined, 80, 70)
        p2.SetData("M 0 40 c 10 -40 40 -40 50 0 q -25 -30 -50 0 z")
        p2.fill = new SolidBrush(UI.Color.Rgb(255, 120, 120))
        ui.Add(p2)
        var g = new UI.Geometry()
        g.AddEllipse(30, 30, 28, 20)
        var p3 = new UI.Path(g, 150, 70)
        p3.stroke = new Pen(UI.Color.Rgb(0, 0, 0))
        ui.Add(p3)
        ui.Draw()
        print("Rechteck " + (Px.Get(ui.renderer, 30, 30) == r.fill.Color) + " " + (Px.Get(ui.renderer, 10, 10) == r.stroke.Color) + " " + (Px.Get(ui.renderer, 9, 9) == ui.theme.back.Color))
        print("Ellipse " + (Px.Get(ui.renderer, 110, 30) == e.fill.Color) + " " + (Px.Get(ui.renderer, 81, 11) == ui.theme.back.Color))
        print("Strecke " + (Px.Get(ui.renderer, 150, 10) == l.stroke.Color) + " " + (Px.Get(ui.renderer, 200, 40) == l.stroke.Color) + " " + lc.children[0].actualWidth + "x" + lc.children[0].actualHeight)
        print("Pfad " + p.actualWidth + "x" + p.actualHeight + " " + (Px.Get(ui.renderer, 30, 90) == p.fill.Color) + " " + (Px.Get(ui.renderer, 10, 70) == p.stroke.Color) + " " + p.data.figures.count + " " + p.data.figures[0].Count() + " " + p.data.figures[0].closed)
        print("Kurve " + (Px.Get(ui.renderer, 105, 90) == p2.fill.Color) + " " + p2.data.figures[0].closed + " " + p2.actualWidth + "x" + p2.actualHeight)
        print("Ellipse-Pfad " + g.figures[0].Count() + " " + p3.actualWidth + "x" + p3.actualHeight + " " + (Px.Get(ui.renderer, 150 + 30, 70 + 30) == ui.theme.back.Color))
        var dc = new UI.DrawingCanvas(10, 150, 120, 40)
        var paints = 0
        dc.onPaint = func (c) => {
            paints = paints + 1
            c.renderer.FillRect(0, 0, c.framebuffer.Width(), c.framebuffer.Height(), new SolidBrush(UI.Color.Rgb(30, 30, 60)))
        }
        var downs = 0
        var lastX = -1
        dc.onMouseDown = func (x, y, b) => {
            downs = downs + 1
            lastX = x
        }
        ui.Add(dc)
        ui.Draw()
        ui.Draw()
        print("Zeichenflaeche " + paints + " " + dc.framebuffer.Width() + "x" + dc.framebuffer.Height() + " " + (Px.Get(ui.renderer, 20, 160) == UI.Color.Rgb(30, 30, 60)))
        dc.Invalidate()
        ui.Draw()
        print("neu gemalt " + paints)
        dc.width = 80
        ui.Draw()
        ui.Draw()
        print("Groesse geaendert " + paints + " " + dc.framebuffer.Width())
        ui.MouseDown(1, 25, 170)
        ui.MouseUp(1, 25, 170)
        print("Maus " + downs + " " + lastX)
        var src = new Framebuffer(30, 20)
        new Renderer(src).FillRect(0, 0, 30, 20, new SolidBrush(UI.Color.Rgb(255, 0, 255)))
        var dc2 = new UI.DrawingCanvas(150, 150, 30, 20)
        dc2.SetSource(src)
        ui.Add(dc2)
        ui.Draw()
        print("fremder Puffer " + (Px.Get(ui.renderer, 160, 160) == UI.Color.Rgb(255, 0, 255)) + " " + dc2.actualWidth)
        """, new[] { "500 1000 500 -500 -500 1000", "Rechteck True True True", "Ellipse True True", "Strecke True True 51x31", "Pfad 41x51 True True 1 5 True", "Kurve True True 51x41", "Ellipse-Pfad 64 59x51 True", "Zeichenflaeche 1 120x40 True", "neu gemalt 2", "Groesse geaendert 4 80", "Maus 1 15", "fremder Puffer True 30" });

    CheckUi("Listen mit DataTemplate und das Beschneidungsrechteck des Renderers", uiHead + """
        var lb = new UI.ListBox(10, 10, 90, 80)
        lb.itemTemplate = new UI.DataTemplate(func (item) => {
            var st = new UI.StackPanel(0, 0, -1, -1, true, 4, 1)
            st.Add(new UI.Label("#"))
            st.Add(new UI.Label(item))
            return st
        })
        lb.Add("one")
        lb.Add("two")
        lb.Add("three")
        ui.Add(lb)
        ui.Draw()
        print("Vorlagenzeilen " + lb.rowElements.count + " " + lb.rowElements[1].children.count + " " + lb.rowElements[1].children[1].text)
        ui.renderer.SetClip(150, 20, 10, 10)
        ui.renderer.FillRect(100, 0, 100, 100, new SolidBrush(UI.Color.Rgb(255, 0, 0)))
        ui.renderer.DrawText(148, 20, "AB", new SolidBrush(UI.Color.Rgb(0, 255, 0)))
        ui.renderer.ResetClip()
        var inside = 0
        var outside = 0
        for (var y = 0; y < 60; y = y + 1) {
            for (var x = 100; x < 200; x = x + 1) {
                var red = Px.Get(ui.renderer, x, y) == UI.Color.Rgb(255, 0, 0)
                var inClip = x >= 150 && x < 160 && y >= 20 && y < 30
                if (red && inClip) { inside = inside + 1 }
                if (red && !inClip) { outside = outside + 1 }
            }
        }
        print("Clip " + inside + " " + outside)
        """, new[] { "Vorlagenzeilen 3 2 two", "Clip 61 0" });

    CheckUi("Tick zeichnet, verarbeitet Ereignisse und liefert false, sobald das Fenster geschlossen wurde", uiHead + """
        var b = new UI.Button("OK", 10, 10, 80, 26)
        ui.Add(b)
        print("offen " + ui.Tick())
        print("gezeichnet " + (Px.Get(ui.renderer, 12, 12) == ui.theme.face.Color))
        __TestClose()
        print("offen " + ui.Tick() + " geschlossen " + ui.closed)
        """, new[] { "offen True", "gezeichnet True", "offen False geschlossen True" });

    CheckUi("Window.NextEvent: Aufbau der Ereignisse, leere Warteschlange liefert undefined", uiHead + """
        var w2 = new Window(fb, "zwei")
        w2.EnableEvents()
        print("leer " + (w2.NextEvent() == undefined))
        __TestEvent(8, 2, 20.7, 30.2)
        __TestEvent(3, "a")
        w2.Tick()
        var e1 = w2.NextEvent()
        print(e1[0] + " " + e1[1] + " " + e1[2] + " " + e1[3])
        var e2 = w2.NextEvent()
        print(e2[0] + " " + e2[1])
        print("danach " + (w2.NextEvent() == undefined))
        """, new[] { "leer True", "8 2 20 30", "3 a", "danach True" });

    // VSync: default on (Tick waits for the screen refresh), can be switched off via a property - also in a derived window class
    CheckUi("Window.VSync: Vorgabe an, per Property ein-/ausschaltbar (auch in einer abgeleiteten Klasse)", """
        var fb2 = new Framebuffer(8, 8)
        var w2 = new Window(fb2, "VSync")
        print(w2.VSync)
        w2.VSync = false
        print(w2.VSync)
        w2.VSync = true
        print(w2.VSync)
        class MyWin : Window { construct(Framebuffer b, string t) : base(b, t) { } }
        var w3 = new MyWin(fb2, "abgeleitet")
        w3.VSync = false
        print(w3.VSync)
        """, new[] { "True", "False", "True", "False" });

    // ---- UI markup (.fxml): parsing, diagnostics, the generated script, and the generated class running against the library ----
    {
        Console.WriteLine("--- UI-Markup ---");
        void CheckMarkup(string title, bool ok, string detail = "")
        {
            if (!ok) uiFailures++;
            Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title} {detail}");
        }

        string Diagnose(string markup) =>
            string.Join(" | ", fire.UI.Markup.MarkupParser.Parse(markup).Diagnostics.Select(d => d.ToString()));

        CheckMarkup("Markup: ein gueltiges Dokument hat keine Diagnosen",
            fire.UI.Markup.MarkupParser.Parse("<Window class=\"A\"><Button name=\"b\" text=\"x\" onClick=\"Go\"/></Window>").Diagnostics.Count == 0);

        void ExpectDiagnostic(string title, string markup, string expected)
        {
            string actual = Diagnose(markup);
            CheckMarkup($"Markup: {title}", actual.Contains(expected, StringComparison.Ordinal), $"erwartet '{expected}', erhalten '{actual}'");
        }
        ExpectDiagnostic("Fehler im XML mit Zeile", "<Window class=\"A\">\n<Button>\n</Window>", "line 3");
        ExpectDiagnostic("das Wurzelelement", "<Foo class=\"A\"/>", "must be 'Window' or 'View'");
        ExpectDiagnostic("class fehlt", "<Window/>", "needs the attribute class");
        ExpectDiagnostic("unbekanntes Element mit Zeile", "<Window class=\"A\">\n<Slider/>\n</Window>", "line 2: Unknown element 'Slider'");
        ExpectDiagnostic("unbekannte Eigenschaft", "<Window class=\"A\"><Label colour=\"#fff\"/></Window>", "has no property or event 'colour'");
        ExpectDiagnostic("ein Button hat keine Kinder", "<Window class=\"A\"><Button><Label/></Button></Window>", "cannot contain elements");
        ExpectDiagnostic("Name doppelt", "<Window class=\"A\"><Label name=\"a\"/><Label name=\"a\"/></Window>", "used twice");
        ExpectDiagnostic("reservierter Name", "<Window class=\"A\"><Label name=\"ui\"/></Window>", "used by the generated class");
        ExpectDiagnostic("Handlername gleich Elementname", "<Window class=\"A\"><Label name=\"a\"/><Button onClick=\"a\"/></Window>", "already used");
        ExpectDiagnostic("unbekannter Converter", "<Window class=\"A\"><Label text=\"{Binding X, Converter=Nope}\"/></Window>", "Unknown converter 'Nope'");
        ExpectDiagnostic("Binding auf ein Element, das es nicht gibt", "<Window class=\"A\"><Label text=\"{Binding text, ElementName=x}\"/></Window>", "does not exist");
        ExpectDiagnostic("unbekannte Markup-Erweiterung", "<Window class=\"A\"><Label text=\"{Bind X}\"/></Window>", "Unknown markup extension");
        ExpectDiagnostic("Binding ohne Pfad", "<Window class=\"A\"><Label text=\"{Binding Mode=TwoWay}\"/></Window>", "needs a path");
        ExpectDiagnostic("ein Teil ausserhalb seines Containers", "<Window class=\"A\"><Item>x</Item></Window>", "'Item' can only be inside");
        ExpectDiagnostic("ein Container, der nur Teile nimmt", "<Window class=\"A\"><ListBox><Button/></ListBox></Window>", "can only contain 'Item'");
        ExpectDiagnostic("Border nimmt nur ein Kind", "<Window class=\"A\"><Border><Label/><Label/></Border></Window>", "contains only one element");
        ExpectDiagnostic("Style ohne Ziel", "<Window class=\"A\"><Resources><Style key=\"k\"/></Resources></Window>", "needs target");
        ExpectDiagnostic("Style mit unbekanntem Ziel", "<Window class=\"A\"><Resources><Style target=\"Slider\"/></Resources></Window>", "Unknown style target");
        ExpectDiagnostic("Setter mit unbekannter Eigenschaft", "<Window class=\"A\"><Resources><Style target=\"Button\"><Setter property=\"nope\" value=\"1\"/></Style></Resources></Window>", "has no property 'nope'");
        ExpectDiagnostic("zwei Styles ohne Schluessel fuer dasselbe Ziel", "<Window class=\"A\"><Resources><Style target=\"Button\"/><Style target=\"Button\"/></Resources></Window>", "two styles without a key");
        ExpectDiagnostic("basedOn ohne Ziel", "<Window class=\"A\"><Resources><Style key=\"a\" target=\"Button\" basedOn=\"b\"/></Resources></Window>", "does not exist");
        ExpectDiagnostic("Stil, den es nicht gibt", "<Window class=\"A\"><Button style=\"Nope\"/></Window>", "Unknown style 'Nope'");
        ExpectDiagnostic("TemplateBinding ausserhalb einer Vorlage", "<Window class=\"A\"><Label text=\"{TemplateBinding text}\"/></Window>", "only for the elements inside a ControlTemplate");
        ExpectDiagnostic("Binding in einer Vorlage", "<Window class=\"A\"><Resources><ControlTemplate key=\"t\" target=\"Button\"><Label text=\"{Binding X}\"/></ControlTemplate></Resources></Window>", "not available inside a ControlTemplate");
        ExpectDiagnostic("Vorlage mit unbekanntem Teil im Trigger", "<Window class=\"A\"><Resources><ControlTemplate key=\"t\" target=\"Button\"><Label name=\"a\"/><Trigger property=\"hover\" value=\"true\"><Setter target=\"b\" property=\"text\" value=\"x\"/></Trigger></ControlTemplate></Resources></Window>", "has no part 'b'");
        ExpectDiagnostic("unbekannter Binding-Modus", "<Window class=\"A\"><Label text=\"{Binding X, Mode=Sideways}\"/></Window>", "Unknown binding mode");
        const string dtHead = "<Window class=\"A\"><Resources>";
        ExpectDiagnostic("DataTemplate: ein Binding ohne Pfad ausserhalb", "<Window class=\"A\"><Label text=\"{Binding}\"/></Window>", "without a path is for the elements of a DataTemplate");
        ExpectDiagnostic("DataTemplate: genau ein Element", dtHead + "<DataTemplate key=\"d\"><Label/><Label/></DataTemplate></Resources></Window>", "contains exactly one element");
        ExpectDiagnostic("DataTemplate: ohne Schluessel", dtHead + "<DataTemplate><Label/></DataTemplate></Resources></Window>", "needs key");
        ExpectDiagnostic("DataTemplate: keine Namen", dtHead + "<DataTemplate key=\"d\"><Label name=\"a\"/></DataTemplate></Resources></Window>", "have no name");
        ExpectDiagnostic("DataTemplate: keine Handler", dtHead + "<DataTemplate key=\"d\"><Button onClick=\"Go\"/></DataTemplate></Resources></Window>", "Event handlers are not available inside a DataTemplate");
        ExpectDiagnostic("DataTemplate: kein ElementName", dtHead + "<DataTemplate key=\"d\"><Label text=\"{Binding x, ElementName=y}\"/></DataTemplate></Resources></Window>", "'ElementName' is not available inside a DataTemplate");
        ExpectDiagnostic("DataTemplate: das Element selbst ist nur lesbar", dtHead + "<DataTemplate key=\"d\"><TextBox text=\"{Binding Mode=TwoWay}\"/></DataTemplate></Resources></Window>", "read only");
        ExpectDiagnostic("DataTemplate: unbekannter Converter", dtHead + "<DataTemplate key=\"d\"><Label text=\"{Binding x, Converter=Nope}\"/></DataTemplate></Resources></Window>", "Unknown converter 'Nope'");
        ExpectDiagnostic("DataTemplate: Schluessel doppelt", dtHead + "<DataTemplate key=\"d\"><Label/></DataTemplate><DataTemplate key=\"d\"><Label/></DataTemplate></Resources></Window>", "used twice");
        ExpectDiagnostic("itemTemplate, das es nicht gibt", "<Window class=\"A\"><ListBox itemTemplate=\"Nope\"/></Window>", "Unknown data template 'Nope'");
        ExpectDiagnostic("view, die es nicht gibt", "<Window class=\"A\"><ListBox view=\"Nope\"/></Window>", "Unknown view 'Nope'");
        ExpectDiagnostic("itemsSource nimmt keinen festen Wert", "<Window class=\"A\"><ListBox itemsSource=\"abc\"/></Window>", "is a list: give {Binding ...} or {Expr ...}");
        ExpectDiagnostic("view und itemsSource zusammen", dtHead + "<CollectionView key=\"v\"/></Resources><ListBox view=\"v\" itemsSource=\"{Binding xs}\"/></Window>", "a 'view' or an 'itemsSource', not both");
        ExpectDiagnostic("CollectionView: nur Lesen", dtHead + "<CollectionView key=\"v\" source=\"{Binding xs, Mode=TwoWay}\"/></Resources></Window>", "read only");
        ExpectDiagnostic("CollectionView: filter ist Code", dtHead + "<CollectionView key=\"v\" filter=\"abc\"/></Resources></Window>", "is code: give {Expr ...}");
        ExpectDiagnostic("CollectionView: unbekanntes Attribut", dtHead + "<CollectionView key=\"v\" sorted=\"a\"/></Resources></Window>", "no attribute 'sorted'");
        ExpectDiagnostic("Schluessel von Vorlage und View teilen sich den Namensraum", dtHead + "<DataTemplate key=\"d\"><Label/></DataTemplate><CollectionView key=\"d\"/></Resources></Window>", "used twice");

        string Generate(string markup) => fire.UI.Markup.FireUiGenerator.Generate(fire.UI.Markup.MarkupParser.Parse(markup), "T.fxml");
        void ExpectGenerateError(string title, string markup, string expected)
        {
            string actual;
            try { Generate(markup); actual = "(keine Ausnahme)"; }
            catch (fire.UI.Markup.MarkupException ex) { actual = ex.Message; }
            CheckMarkup($"Markup: {title}", actual.Contains(expected, StringComparison.Ordinal), $"erwartet '{expected}', erhalten '{actual}'");
        }
        ExpectGenerateError("Zahl erwartet", "<Window class=\"A\"><Label x=\"abc\"/></Window>", "needs a whole number");
        ExpectGenerateError("true/false erwartet", "<Window class=\"A\"><Label visible=\"yes\"/></Window>", "needs true or false");
        ExpectGenerateError("Ausrichtung erwartet", "<Window class=\"A\"><Stack orientation=\"Diagonal\"/></Window>", "needs Horizontal or Vertical");
        ExpectGenerateError("Abstand erwartet", "<Window class=\"A\"><Label margin=\"1,2,3\"/></Window>", "needs 1 number");
        ExpectGenerateError("Auswahl erwartet", "<Window class=\"A\"><Label halign=\"Middle\"/></Window>", "needs one of Stretch, Left, Center, Right");
        ExpectGenerateError("Stift erwartet", "<Window class=\"A\"><Rectangle stroke=\"black\"/></Window>", "needs a pen");
        ExpectGenerateError("eine Eigenschaft, die eine Methode setzt, ist nicht bindbar", "<Window class=\"A\"><Grid rows=\"{Binding R}\"/></Window>", "cannot be bound");
        ExpectGenerateError("Farbe erwartet", "<Window class=\"A\"><Label color=\"red\"/></Window>", "needs a colour");

        string script = Generate("<Window class=\"Settings\" title=\"Hi &quot;you&quot;\" width=\"200\" height=\"100\"><Button name=\"ok\" text=\"OK\" onClick=\"Save\" x=\"0x10\"/></Window>");
        CheckMarkup("Markup: die erzeugte Basisklasse, der Handler und das Fenster",
            script.Contains("class SettingsBase {") && script.Contains("Save(sender) { }") && script.Contains("new Framebuffer(200, 100)")
            && script.Contains("UI.Button ok") && script.Contains("e0.x = 0x10") && script.Contains("#import \"ui\"") && script.Contains("\"Hi \\\"you\\\"\""), script);
        CheckMarkup("Markup: eine View hat Attach statt Run",
            Generate("<View class=\"V\" base=\"VBase2\"/>") is var viewScript && viewScript.Contains("class VBase2 {") && viewScript.Contains("Attach(container)") && !viewScript.Contains("Run()"));

        // the generated classes against the library: a window (events through the fake window) and a view (bindings)
        string dir = Path.Combine(Path.GetTempPath(), "fire-markup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string P(string name) => Path.Combine(dir, name).Replace('\\', '/');
            File.WriteAllText(P("Form.fxml"), """
                <Window class="Form" title="Form" width="320" height="200">
                  <Stack name="box" x="10" y="10" width="300" height="180" orientation="Vertical" spacing="6">
                    <Label name="caption">Name:</Label>
                    <TextBox name="field" width="200" onChange="FieldChanged"/>
                    <CheckBox name="flag" text="Flag" onChange="FlagChanged"/>
                    <Button name="go" text="Go" width="60" onClick="Go"/>
                  </Stack>
                </Window>
                """);
            CheckUi("Markup: ein Fenster - Handler der abgeleiteten Klasse laufen bei Klick, Tippen und Haken", $$"""
                #include "{{P("Form.fxml")}}"
                class MyForm : FormBase {
                    int clicks = 0
                    Go(sender) { this.clicks = this.clicks + 1 }
                    FieldChanged(sender) { print("text " + sender.text) }
                    FlagChanged(sender) { print("flag " + sender.isChecked) }
                }
                var f = new MyForm()
                f.ui.Draw()
                f.ui.Draw()   // labels and check boxes know their height only after the first drawing, which moves the elements below them in a Stack
                print(f.box.horizontal + " " + f.field.width + " " + f.caption.text)
                // a click on the button (its screen position is known after drawing)
                var bx = f.go.ax + 5
                var by = f.go.ay + 5
                __TestEvent(8, 1, bx + 0.0, by + 0.0)
                __TestEvent(11, 1, bx + 0.0, by + 0.0)
                f.ui.Tick()
                print("clicks " + f.clicks)
                // focus the text field with a click, type into it
                var tx = f.field.ax + 5
                var ty = f.field.ay + 5
                __TestEvent(8, 1, tx + 0.0, ty + 0.0)
                __TestEvent(11, 1, tx + 0.0, ty + 0.0)
                __TestEvent(3, "h")
                __TestEvent(3, "i")
                f.ui.Tick()
                print("field " + f.field.text)
                f.flag.Toggle()
                """, new[] { "False 200 Name:", "clicks 1", "text h", "text hi", "field hi", "flag True" });

            File.WriteAllText(P("Panel.fxml"), """
                <View class="Panel1" width="300" height="200">
                  <Panel width="300" height="200">
                    <TextBox name="src" x="5" y="5" width="120" text="abc"/>
                    <Label name="mirror" x="5" y="40" text="{Binding Path=text, ElementName=src}"/>
                    <Label name="player" x="5" y="60" text="{Binding Player.Name}"/>
                    <Label name="once" x="5" y="80" text="{Binding Title, Mode=OneTime}"/>
                    <TextBox name="edit" x="5" y="100" width="120" text="{Binding Player.Name, Mode=TwoWay}"/>
                    <Label name="shout" x="5" y="120" text="{Binding Player.Name, Converter=Upper}"/>
                    <CheckBox name="cb" x="5" y="130" text="{Binding Title}" isChecked="{Binding Player.Active, Converter=Not, Mode=TwoWay}"/>
                    <Label name="empty" x="5" y="150" visible="{Binding Title, Converter=IsEmpty}" color="{Enum Colors.Red}"/>
                  </Panel>
                  <Resources><Converter key="Upper" type="UpperConverter"/></Resources>
                </View>
                """);
            CheckUi("Markup: Bindings mit Probes - Pfad, Element, TwoWay, Converter, OneTime, Austausch im Pfad, Trennen", $$"""
                #include "{{P("Panel.fxml")}}"
                enum Colors { Black = 0, Red = 255 }
                class UpperConverter : UI.Converter { Convert(value) { return value.ToUpper() } }
                class Player { string Name = "p1"
                               bool Active = true }
                class Model { Player Player
                              string Title = "T1" }
                var d = new Panel1Base()
                var m = new Model()
                m.Player = new Player()
                d.SetDataContext(m)
                print(d.mirror.text + " " + d.player.text + " " + d.once.text + " " + d.edit.text + " " + d.shout.text + " " + d.cb.text + " " + d.cb.isChecked + " " + d.empty.visible + " " + d.empty.brush.Color)
                d.src.text = "xyz"
                print(d.mirror.text)
                m.Player.Name = "p2"
                print(d.player.text + " " + d.edit.text + " " + d.shout.text)
                d.edit.text = "p3"
                print(m.Player.Name + " " + d.player.text + " " + d.shout.text)
                d.cb.isChecked = true
                print(m.Player.Active)
                var np = new Player()
                np.Name = "other"
                m.Player = np
                print(d.player.text + " " + d.edit.text)
                np.Name = "other2"
                print(d.player.text)
                d.edit.text = "p4"
                print(np.Name)
                m.Title = ""
                print(d.once.text + "|" + d.cb.text + "|" + d.empty.visible)
                d.SetDataContext(undefined)
                np.Name = "after"
                print(d.player.text)
                """, new[]
                {
                    "abc p1 T1 p1 P1 T1 False False 255", "xyz", "p2 p2 P2", "p3 p3 P3", "False",
                    "other other", "other2", "p4", "T1||True", "p4",
                });

            File.WriteAllText(P("Rich.fxml"), """
                <Window class="Rich" title="Rich" width="480" height="320">
                  <Resources>
                    <Style target="Label">
                      <Setter property="color" value="#0000C8"/>
                    </Style>
                    <Style key="Primary" target="Button">
                      <Setter property="background" value="#3366AA"/>
                      <Setter property="foreground" value="#FFFFFF"/>
                      <Setter property="margin" value="2,1"/>
                      <Trigger property="hover" value="true">
                        <Setter property="background" value="#4477BB"/>
                      </Trigger>
                    </Style>
                    <ControlTemplate key="Fancy" target="Button">
                      <Border name="bd" background="#33AA66" padding="4">
                        <Label name="txt" text="{TemplateBinding text}"/>
                      </Border>
                      <Trigger property="pressed" value="true">
                        <Setter target="bd" property="background" value="#AA3333"/>
                      </Trigger>
                    </ControlTemplate>
                  </Resources>
                  <DockPanel x="0" y="0" width="480" height="320">
                    <MenuBar name="bar" DockPanel.Dock="Top">
                      <Menu header="File">
                        <MenuItem header="Open" shortcut="Ctrl+O" onClick="OpenFile"/>
                        <MenuSeparator/>
                        <MenuItem header="Recent">
                          <MenuItem header="a.txt"/>
                        </MenuItem>
                        <MenuItem header="Check" checkable="true" isChecked="true"/>
                      </Menu>
                      <Menu header="Edit"><MenuItem header="Copy"/></Menu>
                    </MenuBar>
                    <ToolBar DockPanel.Dock="Top">
                      <Button name="one" text="One" onClick="One"/>
                      <Separator/>
                      <Button text="Two"/>
                    </ToolBar>
                    <Grid rows="*,auto" columns="150,*">
                      <ListBox name="names" Grid.Row="0" Grid.Column="0" margin="4" onSelect="Picked">
                        <Item>Alice</Item>
                        <Item>Bob</Item>
                        <Item text="Carol"/>
                      </ListBox>
                      <ScrollViewer Grid.Row="0" Grid.Column="1" margin="4">
                        <StackPanel spacing="4" padding="4">
                          <TreeView name="tree" height="80">
                            <TreeNode text="root" expanded="true">
                              <TreeNode text="child a"/>
                              <TreeNode text="child b"><TreeNode text="leaf"/></TreeNode>
                            </TreeNode>
                          </TreeView>
                          <RadioButtons name="radio" header="Size" selectedIndex="1"><Item>Small</Item><Item>Large</Item></RadioButtons>
                          <Button name="styled" text="Styled" style="Primary"/>
                          <Button name="templated" text="Templated" template="Fancy"/>
                          <AutoSuggestBox name="sugg" width="150"><Suggestion>apple</Suggestion><Suggestion>banana</Suggestion></AutoSuggestBox>
                          <Rectangle name="rect" width="60" height="20" fill="#FFCC00" stroke="#000000"/>
                          <Path data="M 0 0 L 30 0 L 15 20 Z" fill="#66AAFF" stroke="#003366,2"/>
                          <DrawingCanvas name="canvas" width="80" height="40" onPaint="Paint" onMouseDown="Down"/>
                        </StackPanel>
                      </ScrollViewer>
                      <ListView name="table" Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="2" height="60">
                        <Column header="Name" member="name" width="120"/>
                        <Column header="Age" member="age"/>
                      </ListView>
                    </Grid>
                  </DockPanel>
                </Window>
                """);
            CheckUi("Markup: Layout-Container, Listen, Baum, Menues, Styles, Vorlagen und Formen aus dem Markup", $$"""
                #include "{{P("Rich.fxml")}}"
                class RichApp : RichBase {
                    Picked(sender) { print("picked " + sender.selectedItem) }
                    OpenFile(sender) { print("open") }
                    One(sender) { print("one") }
                    Paint(sender, canvas) { print("paint " + canvas.framebuffer.Width() + "x" + canvas.framebuffer.Height() + " " + (canvas == sender)) }
                    Down(sender, x, y, button) { print("down " + x + "," + y + " " + button) }
                }
                var app = new RichApp()
                app.ui.Draw()
                app.ui.Draw()
                print("namen " + app.names.count + " baum " + app.tree.visibleNodes.count + " vorschlaege " + app.sugg.suggestions.count + " tabelle " + app.table.columns.count)
                var n = app.names
                app.ui.MouseDown(1, n.ax + 10, n.ay + 25)
                app.ui.MouseUp(1, n.ax + 10, n.ay + 25)
                var c = app.canvas
                c.MouseDown(app.ui, 1, c.ax + 5, c.ay + 6)
                app.ui.MouseDown(1, app.one.ax + 5, app.one.ay + 5)
                app.ui.MouseUp(1, app.one.ax + 5, app.one.ay + 5)
                print("Stil " + (app.styled.background != undefined) + " " + app.styled.margin.left + " " + (app.styled.style == app.fxStyle_Primary))
                print("Vorlage " + (app.templated.templateRoot != undefined) + " " + app.templated.FindPart("txt").text)
                print("implizit " + (app.rect.stroke != undefined) + " " + app.radio.selectedIndex + " " + app.radio.items.count)
                app.bar.Open(app.ui, 0)
                app.ui.Draw()
                var menu = app.ui.popups[0]
                var mx = menu.ax + 10
                var my = menu.ay + 4
                app.ui.MouseDown(1, mx, my)
                app.ui.MouseUp(1, mx, my)
                print("Menue " + app.ui.popups.count + " " + app.bar.menus.count)
                """, new[] { "paint 80x40 True", "namen 3 baum 3 vorschlaege 2 tabelle 2", "picked Bob", "down 5,6 1", "one", "Stil True 2 True", "Vorlage True Templated", "implizit True 1 2", "open", "Menue 0 2" });

            // the design view: the same library draws, without handlers and without the code of the program
            {
                var richDoc = fire.UI.Markup.MarkupParser.Parse(File.ReadAllText(P("Rich.fxml")));
                var shot = fire.Compiler.UiPreview.Render(richDoc, P("Rich.fxml"));
                var shown = richDoc.AllElements().Where(e => fire.UI.Markup.MarkupSchema.Find(e.Tag) is { IsPart: false }).ToList();
                CheckMarkup("Markup: Entwurfsansicht - Bild in der Groesse des Fensters, ein Ort je Element", shot.Ok && shot.Width == 480 && shot.Height == 320 && shot.Rects.Count == shown.Count && shot.Rects[0].Width == 480, shot.Error ?? $"{shot.Width}x{shot.Height} {shot.Rects.Count}/{shown.Count}");
                int styledAt = shown.FindIndex(e => e.Name == "styled");
                var styledRect = shot.Rects.FirstOrDefault(r => r.Index == styledAt);
                uint styledColor = 51u | (102u << 8) | (170u << 16) | (255u << 24);
                CheckMarkup("Markup: Entwurfsansicht - der Style steckt im Bild (Hintergrund des Buttons mit style=\"Primary\")", shot.Ok && styledRect != null && shot.Pixels[(styledRect.Y + 3) * shot.Width + styledRect.X + 3] == styledColor);
                var bound = fire.UI.Markup.MarkupParser.Parse("<Window class=\"B\" width=\"200\" height=\"100\"><Label text=\"{Binding Name}\"/><Label color=\"{Enum Colors.Red}\" text=\"x\"/><Button onClick=\"Nope\"/></Window>");
                string previewScript = fire.UI.Markup.FireUiGenerator.GeneratePreview(bound);
                CheckMarkup("Markup: Entwurfsansicht - ein gebundener Text zeigt den Pfad, Code des Programms und Handler fehlen", previewScript.Contains("\u2039Name\u203A") && !previewScript.Contains("Colors.Red") && !previewScript.Contains("Nope(") && fire.Compiler.UiPreview.Render(bound).Ok);
                CheckMarkup("Markup: Entwurfsansicht - ein Fehler im Markup wird gemeldet, nichts wird ausgefuehrt", !fire.Compiler.UiPreview.Render(fire.UI.Markup.MarkupParser.Parse("<Window class=\"B\"><Label x=\"abc\"/></Window>")).Ok);
            }

            // DataTemplate, CollectionView, itemsSource/itemTemplate/view im Markup
            File.WriteAllText(P("Data.fxml"), """
                <Window class="Data" title="Data" width="360" height="240">
                  <Resources>
                    <Converter key="Up" type="UpperConverter"/>
                    <DataTemplate key="Person">
                      <StackPanel horizontal="true" spacing="6">
                        <Label text="{Binding name, Converter=Up}"/>
                        <Label text="{Binding age}"/>
                      </StackPanel>
                    </DataTemplate>
                    <DataTemplate key="Plain">
                      <Label text="{Binding}"/>
                    </DataTemplate>
                    <CollectionView key="ByName" source="{Binding people}" sortBy="name" descending="true"/>
                    <CollectionView key="Young" source="{Binding people}" sortBy="age" filter="{Expr func (p) on this => { return p.age &lt; 30 }}"/>
                  </Resources>
                  <StackPanel padding="4" spacing="4">
                    <ListBox name="viaView" width="200" height="90" view="ByName" itemTemplate="Person" selectedIndex="{Binding sel, Mode=TwoWay}"/>
                    <ListBox name="direct" width="200" height="60" itemsSource="{Binding names}" itemTemplate="Plain"/>
                    <ListBox name="young" width="200" height="40" view="Young" itemTemplate="Person"/>
                  </StackPanel>
                </Window>
                """);
            CheckUi("Markup: DataTemplate, CollectionView und itemsSource folgen dem Datenkontext", $$"""
                #include "{{P("Data.fxml")}}"
                class UpperConverter : UI.Converter {
                    Convert(value) { return value.ToUpper() }
                    ConvertBack(value) { return value }
                }
                class Person {
                    string name
                    int age
                    construct(string name, int age) {
                        this.name = name
                        this.age = age
                    }
                }
                class Model {
                    List people
                    List names
                    int sel = -1
                }
                class Data : DataBase { }
                var app = new Data()
                var m = new Model()
                m.people = new List()
                m.people.Add(new Person("Carol", 31))
                m.people.Add(new Person("Alice", 25))
                m.people.Add(new Person("Bob", 40))
                m.names = new List()
                m.names.Add("one")
                m.names.Add("two")
                app.SetDataContext(m)
                app.ui.Draw()
                app.ui.Draw()
                print("zeilen " + app.viaView.count + " " + app.direct.count + " " + app.young.count + " erste " + app.viaView.selectedItem)
                print("zeile " + app.viaView.rowElements[0].children[0].text + " " + app.viaView.rowElements[0].children[1].text + " " + app.direct.rowElements[1].text)
                m.people.Add(new Person("Dave", 22))
                m.names.Add("three")
                app.ui.Draw()
                print("mehr " + app.viaView.count + " " + app.direct.count + " jung " + app.young.count + " " + app.young.view[0].name)
                m.sel = 1
                app.ui.Draw()
                print("Auswahl " + app.viaView.selectedIndex + " " + app.viaView.selectedItem.name)
                app.viaView.Select(3)
                app.ui.Draw()
                print("zurueck " + m.sel)
                var other = new List()
                other.Add("x")
                m.names = other
                app.ui.Draw()
                print("neue Liste " + app.direct.count + " " + app.ui.Update())
                """, new[] { "zeilen 3 2 1 erste undefined", "zeile CAROL 31 two", "mehr 4 3 jung 2 Dave", "Auswahl 1 Carol", "zurueck 3", "neue Liste 1 False" });

            // the design view shows the rows without knowing the data
            {
                var dataDoc = fire.UI.Markup.MarkupParser.Parse(File.ReadAllText(P("Data.fxml")));
                var dataShot = fire.Compiler.UiPreview.Render(dataDoc, P("Data.fxml"));
                string dataPreview = fire.UI.Markup.FireUiGenerator.GeneratePreview(dataDoc);
                CheckMarkup("Markup: Entwurfsansicht - Listen mit itemTemplate/view/itemsSource werden gezeichnet (ohne die Daten)", dataShot.Ok && dataPreview.Contains("\u2039name\u203A") && !dataPreview.Contains("SetView") && dataPreview.Contains("Add(\"\")"), dataShot.Error ?? dataPreview);
            }

            // Invalidation: only redraw what has changed
            CheckUi("Invalidierung: Update zeichnet nur bei Aenderungen, Hover und Text zeichnen nur den betroffenen Bereich", uiHead + """
                var a = new UI.Label("alpha", 10, 10)
                var b = new UI.Button("btn", 10, 40, 80, 24)
                var c = new UI.Label("gamma", 200, 150)
                ui.Add(a)
                ui.Add(b)
                ui.Add(c)
                print("first " + ui.Update())
                print("idle " + ui.Update())
                var sentinel = UI.Color.Rgb(1, 2, 3)
                ui.renderer.SetPixel(250, 20, sentinel)
                b.hover = true
                print("hover " + ui.Update() + " rest bleibt " + (Px.Get(ui.renderer, 250, 20) == sentinel))
                print("idle " + ui.Update())
                ui.renderer.SetPixel(250, 20, sentinel)
                c.x = 190
                print("Layout " + ui.Update() + " alles neu " + (Px.Get(ui.renderer, 250, 20) != sentinel))
                ui.renderer.SetPixel(250, 20, sentinel)
                ui.theme.back = new SolidBrush(UI.Color.Rgb(10, 10, 10))
                print("Theme " + ui.Update() + " alles neu " + (Px.Get(ui.renderer, 250, 20) != sentinel))
                ui.renderer.SetPixel(250, 20, sentinel)
                b.Invalidate()
                print("Invalidate " + ui.Update() + " rest bleibt " + (Px.Get(ui.renderer, 250, 20) == sentinel))
                ui.renderer.SetPixel(250, 20, sentinel)
                ui.Draw()
                print("Draw " + (Px.Get(ui.renderer, 250, 20) != sentinel))
                """, new[] { "first True", "idle False", "hover True rest bleibt True", "idle False", "Layout True alles neu True", "Theme True alles neu True", "Invalidate True rest bleibt True", "Draw True" });

            // several windows without markup: Attach/Detach/Tick
            CheckUi("Mehrere Fenster: Root.Attach haengt ein Fenster an, Tick arbeitet beide ab, Detach loest es", """
                class Px { static int Get(console, int x, int y) { var v = console.GetPixel(x, y); if (v < 0) { v = v + 4294967296 } return v } }
                var fb1 = new Framebuffer(100, 60)
                var w1 = new Window(fb1, "one")
                var ui1 = new UI.Root(fb1, w1)
                var fb2 = new Framebuffer(100, 60)
                var w2 = new Window(fb2, "two")
                var ui2 = new UI.Root(fb2, w2)
                var ticks = 0
                ui2.onTick = func () => { ticks = ticks + 1 }
                ui2.Add(new UI.Button("two", 5, 5, 60, 20))
                ui1.Add(new UI.Label("one", 5, 5))
                ui1.Attach(ui2)
                ui1.Attach(ui2)
                print("angehaengt " + ui1.attached.count)
                print(ui1.Tick())
                print(ui1.Tick())
                print("ticks " + ticks + " gezeichnet " + (Px.Get(ui2.renderer, 10, 10) == ui2.theme.face.Color))
                ui1.Detach(ui2)
                ui1.Tick()
                print("nach Detach " + ticks)
                """, new[] { "angehaengt 1", "True", "True", "ticks 2 gezeichnet True", "nach Detach 2" });

            // several windows: one window opens another, Tick processes both
            File.WriteAllText(P("Main.fxml"), "<Window class=\"Main\" width=\"120\" height=\"80\"><Button name=\"b\" text=\"main\"/></Window>");
            File.WriteAllText(P("Tool.fxml"), "<Window class=\"Tool\" title=\"Tool\" width=\"100\" height=\"60\"><Label name=\"l\" text=\"tool\"/></Window>");
            CheckUi("Markup: Open/Run(other) - das erzeugte Fenster haengt ein anderes an, ein Tick arbeitet beide ab", $$"""
                #include "{{P("Main.fxml")}}"
                #include "{{P("Tool.fxml")}}"
                var mainTicks = 0
                var toolTicks = 0
                class Main : MainBase { OnTick() { mainTicks = mainTicks + 1 } }
                class Tool : ToolBase { OnTick() { toolTicks = toolTicks + 1 } }
                var a = new Main()
                var t = new Tool()
                a.Open(t)
                a.ui.Tick()
                a.ui.Tick()
                print("ticks " + mainTicks + " " + toolTicks + " angehaengt " + a.ui.attached.count)
                t.l.text = "changed"
                print("Update " + t.ui.Update())
                """, new[] { "ticks 2 2 angehaengt 1", "Update True" });
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    Console.WriteLine(uiFailures == 0 ? "Alle UI-Pruefungen bestanden." : $"FEHLER: {uiFailures} UI-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Native callbacks (window events) run nested on the VM of the thread: real globals, no copying
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Callbacks auf dem VM-Thread ===");
    int cbFailures = 0;

    // Like RuntimeSession.CallLambda: the host runner calls FireRuntime.RunCallback; unhandled callback errors end up as "CB: ..." in the output.
    List<string> RunCb(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var sources = new[] { fire.Standard.Prelude.Source, fire.Terminal.Bridge.GraphicsBridge.PreludeSource, fire.Windows.Bridge.WindowsBridge.PreludeSource, script };
        var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = Parser.ParseMultiple(sources.Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), alreadyIncluded)).ToList());
        var natives = new NativeRegistry();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();

        var renderer = new FakeRenderer();
        VM? vm = null;
        IReadOnlyDictionary<string, RuntimeClass>? classes = null;
        var fbManager = new fire.Terminal.FramebufferManager();
        var conManager = new fire.Terminal.RendererManager(fbManager, new fire.Terminal.IntegratedGlyphFont());
        var winManager = new fire.Terminal.Windows.WindowManager(fbManager,
            (l, v) => FireRuntime.RunCallback(l, v, natives, classes, () => vm!.SnapshotGlobals(), message => { lock (lines) lines.Add("CB: " + message); }, mode, vm),
            () => renderer);
        fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, conManager);
        fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, winManager);

        var kept = new List<LambdaValue>();
        natives.Register("__TestEvent", args => { renderer.Push((int)args[0].AsInt(), args); return Value.MakeUndefined(); });
        natives.Register("__Keep", args => { kept.Add((LambdaValue)args[0].AsLambda()); return Value.MakeUndefined(); });
        // runs the remembered lambda on ANOTHER thread without a running VM (like a host event)
        natives.Register("__RunKeptOnOtherThread", args =>
        {
            var t = new Thread(() => FireRuntime.RunCallback(kept[0], Array.Empty<Value>(), natives, classes, () => vm!.SnapshotGlobals(), message => { lock (lines) lines.Add("CB: " + message); }, mode, vm));
            t.Start();
            t.Join();
            return Value.MakeUndefined();
        });

        var resolveResult = Resolver.Resolve(program, natives.Names);
        var compiled = Compiler.Compile(program, resolveResult, natives);
        classes = compiled.Classes;
        VM.ResetTerminateForTests();
        vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: mode);
        vm.Run();
        VM.ResetTerminateForTests();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckCb(string title, string script, string[] expected, VmExecutionMode[]? modes = null)
    {
        foreach (var mode in modes ?? new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            string[] actual;
            try { actual = RunCb(script, mode).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + CompileErrors.Describe(ex) }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) cbFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    const string cbHead = """
        class Exception { string message; construct(string message = "") { this.message = message } }
        var fb = new Framebuffer(64, 64)
        var win = new Window(fb, "t")

        """;

    CheckCb("Callback schreibt auf die echten Globals", cbHead + """
        var counter = 0
        win.RegisterMouseDown(func (int b, float x, float y) => { counter = counter + b })
        __TestEvent(8, 2, 1.0, 1.0)
        __TestEvent(8, 3, 1.0, 1.0)
        win.Tick()
        print("counter " + counter)
        """, new[] { "counter 5" });

    CheckCb("Objekte mit Lambda-Feld und Verweis auf Fremdes als Globals stoeren den Callback nicht", cbHead + """
        class Holder { lambda cb; Framebuffer other }
        var h = new Holder()
        h.cb = func () => { }
        h.other = fb
        var hits = 0
        win.RegisterMouseDown(func (int b, float x, float y) => { hits = hits + 1; h.other = undefined })
        __TestEvent(8, 1, 1.0, 1.0)
        win.Tick()
        print("hits " + hits + " other " + (h.other == undefined))
        """, new[] { "hits 1 other True" });

    CheckCb("Unbehandelte Exception im Callback: gemeldet, Programm laeuft weiter, try/catch des Aufrufers sieht sie nicht", cbHead + """
        win.RegisterMouseDown(func (int b, float x, float y) => { throw new Exception("boom") })
        __TestEvent(8, 1, 1.0, 1.0)
        try { win.Tick() } catch (e) { print("aeusserer catch") }
        print("weiter " + (1 + 2))
        try { throw new Exception("m") } catch (e) { print("catch " + e.message) }
        """, new[] { "CB: Unbehandelte Exception vom Typ 'Exception': boom", "weiter 3", "catch m" });

    CheckCb("Ein try/catch im Callback selbst faengt", cbHead + """
        var caught = 0
        win.RegisterMouseDown(func (int b, float x, float y) => {
            try { throw new Exception("x") } catch (e) { caught = caught + 1 }
        })
        __TestEvent(8, 1, 1.0, 1.0)
        win.Tick()
        print("caught " + caught)
        """, new[] { "caught 1" });

    CheckCb("Callback in einer Funktion mit offenem try: der Fehler bricht nur den Callback ab", cbHead + """
        win.RegisterMouseDown(func (int b, float x, float y) => { throw new Exception("boom") })
        __TestEvent(8, 1, 1.0, 1.0)
        class T {
            static Step(w) {
                try {
                    w.Tick()
                    print("nach Tick")
                } catch (e) {
                    print("falsch gefangen")
                } finally {
                    print("finally")
                }
            }
        }
        T.Step(win)
        """, new[] { "CB: Unbehandelte Exception vom Typ 'Exception': boom", "nach Tick", "finally" });

    // Performance mode checks nothing: an access outside the array is a raw C# exception. The state of the VM must be right afterwards.
    CheckCb("Rohe C#-Ausnahme im Callback (Performance): Zustand wiederhergestellt, Programm laeuft weiter", cbHead + """
        win.RegisterMouseDown(func (int b, float x, float y) => { var z = new int[2]; z[5] = 1 })
        __TestEvent(8, 1, 1.0, 1.0)
        try { win.Tick(); print("nach Tick") } finally { print("finally") }
        var sum = 0
        for (var i = 0; i < 100; i = i + 1) { sum = sum + i }
        print("sum " + sum)
        """, new[] { "CB: Index was outside the bounds of the array.", "nach Tick", "finally", "sum 4950" }, new[] { VmExecutionMode.Performance });

    CheckCb("leave im Callback beendet das Programm geordnet (Destruktoren laufen)", cbHead + """
        class G { destruct() { print("~G") } }
        var g = new G()
        win.RegisterMouseDown(func (int b, float x, float y) => { leave })
        __TestEvent(8, 1, 1.0, 1.0)
        win.Tick()
        print("nie")
        """, new[] { "~G" });

    CheckCb("terminate im Callback beendet das Programm geordnet", cbHead + """
        class G { destruct() { print("~G") } }
        var g = new G()
        win.RegisterMouseDown(func (int b, float x, float y) => { terminate(3) })
        __TestEvent(8, 1, 1.0, 1.0)
        win.Tick()
        print("nie")
        """, new[] { "~G" });

    CheckCb("Host-Thread-Callback wird dem Hauptprogramm eingereiht und automatisch an einem sicheren Punkt ausgefuehrt (echte Globals)", cbHead + """
        var counter = 0
        __Keep(func () => { counter = counter + 1; print("cb " + counter) })
        __RunKeptOnOtherThread()
        print("counter " + counter)
        """, new[] { "cb 1", "counter 1" });

    CheckCb("Mit #nosync wartet der Host-Thread-Callback auf `sync globals`", "#nosync\n" + cbHead + """
        var counter = 0
        __Keep(func () => { counter = counter + 1; print("cb " + counter) })
        __RunKeptOnOtherThread()
        print("vorher " + counter)
        var n = sync globals
        print("nachher " + counter + " " + n)
        """, new[] { "vorher 0", "cb 1", "nachher 1 1" });

    CheckCb("Host-Thread-Callback: eine unbehandelte Exception geht als Text an den Host, das Hauptprogramm laeuft weiter", cbHead + """
        __Keep(func () => { throw new Exception("kaputt") })
        __RunKeptOnOtherThread()
        print("weiter")
        """, new[] { "CB: Unbehandelte Exception vom Typ 'Exception': kaputt", "weiter" });

    Console.WriteLine(cbFailures == 0 ? "Alle Callback-Pruefungen bestanden." : $"FEHLER: {cbFailures} Callback-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Globals and fire threads: direct reading, writing in sections (sync globals / sync global { } / fire global { })
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Globals und Fire-Threads ===");
    int glFailures = 0;

    List<string> RunGl(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var natives = NativeRegistry.CreateDefault();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        var program = Parser.ParseMultiple(new[] { fire.Standard.Prelude.Source, script }
            .Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), new HashSet<string>(StringComparer.OrdinalIgnoreCase))).ToList());
        var compiled = Compiler.Compile(program, Resolver.Resolve(program, natives.Names), natives);
        VM.ResetTerminateForTests();
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: mode);
        vm.Run();
        VM.ResetTerminateForTests();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckGl(string title, string script, string[] expected, VmExecutionMode[]? modes = null)
    {
        foreach (var mode in modes ?? new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            string[] actual;
            try
            {
                var task = Task.Run(() => RunGl(script, mode).ToArray());
                actual = task.Wait(TimeSpan.FromSeconds(30)) ? task.Result : new[] { "ZEITUEBERSCHREITUNG (haengt)" };
            }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + (ex.InnerException != null ? CompileErrors.Describe(ex.InnerException) : ex.Message) }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) glFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    // With #nosync: otherwise the automatic processing could take care of the thread's registration before the first `sync globals`, whose return value would stay 0
    CheckGl("Ein Thread schreibt ein Global: es wird erst bei `sync globals` des Hauptprogramms wirksam", """
        #nosync
        var counter = 0
        fire { counter = 5 }
        var handled = 0
        while (handled == 0) { handled = sync globals }
        print("counter " + counter + " bearbeitet " + handled)
        """, new[] { "counter 5 bearbeitet 1" });

    CheckGl("Mit #nosync bleibt der Wert unveraendert, solange das Hauptprogramm nicht `sync globals` ruft", """
        #nosync
        var counter = 0
        var seen = 0
        fire { counter = 5 }
        for (var i = 0; i < 200000; i = i + 1) { seen = seen + counter }
        print("gesehen " + seen)
        """, new[] { "gesehen 0" });

    CheckGl("Standardmaessig arbeitet das Hauptprogramm die Warteschlange selbst ab (ohne `sync globals`)", """
        var counter = 0
        fire { counter = 5 }
        var spins = 0
        while (counter == 0 && spins < 100000000) { spins = spins + 1 }
        print("counter " + counter)
        """, new[] { "counter 5" });

    CheckGl("Standardmaessig: `fire global` und Methodenaufrufe auf globalen Objekten werden ohne `sync globals` bearbeitet", """
        class Box { int n; construct() { this.n = 0 } Add(int d) { this.n = this.n + d } }
        var box = new Box()
        var done = 0
        fire { box.Add(2); box.Add(3); fire global { done = done + 1 } }
        var spins = 0
        while (done == 0 && spins < 100000000) { spins = spins + 1 }
        print("n " + box.n + " done " + done)
        """, new[] { "n 5 done 1" });

    CheckGl("Mit #nosync bearbeitet nur `sync globals` die Warteschlange (Methodenaufruf wartet)", """
        #nosync
        class Box { int n; construct() { this.n = 0 } Add(int d) { this.n = this.n + d } }
        var box = new Box()
        var finished = 0
        fire { box.Add(4) }
        for (var i = 0; i < 300000; i = i + 1) { finished = finished + box.n }
        print("vorher " + finished)
        while (box.n == 0) { sync globals }
        print("nachher " + box.n)
        """, new[] { "vorher 0", "nachher 4" });

    CheckGl("Ein Thread liest Globals direkt (kein Snapshot): er sieht spaetere Aenderungen des Hauptprogramms", """
        var flag = 0
        var done = 0
        fire {
            while (flag == 0) { }
            sync global { done = done + 1 }
        }
        flag = 1
        while (done == 0) { sync globals }
        print("fertig " + done)
        """, new[] { "fertig 1" });

    CheckGl("Methoden auf globalen Objekten laufen atomar (4 Threads x 100 Aufrufe)", """
        class Counter {
            int n
            construct() { this.n = 0 }
            Inc() { var old = this.n; this.n = old + 1 }
        }
        var c = new Counter()
        var done = 0
        fire { for (var i = 0; i < 100; i = i + 1) { c.Inc() } sync global { done = done + 1 } }
        fire { for (var i = 0; i < 100; i = i + 1) { c.Inc() } sync global { done = done + 1 } }
        fire { for (var i = 0; i < 100; i = i + 1) { c.Inc() } sync global { done = done + 1 } }
        fire { for (var i = 0; i < 100; i = i + 1) { c.Inc() } sync global { done = done + 1 } }
        while (done < 4) { sync globals }
        print("n " + c.n)
        """, new[] { "n 400" });

    CheckGl("`sync global { }`: Lesen-Aendern-Schreiben im Block ist atomar (4 Threads x 100)", """
        var total = 0
        var done = 0
        fire { for (var i = 0; i < 100; i = i + 1) { sync global { total = total + 1 } } sync global { done = done + 1 } }
        fire { for (var i = 0; i < 100; i = i + 1) { sync global { total = total + 1 } } sync global { done = done + 1 } }
        fire { for (var i = 0; i < 100; i = i + 1) { sync global { total = total + 1 } } sync global { done = done + 1 } }
        fire { for (var i = 0; i < 100; i = i + 1) { sync global { total = total + 1 } } sync global { done = done + 1 } }
        while (done < 4) { sync globals }
        print("total " + total)
        """, new[] { "total 400" });

    CheckGl("Der Block sieht die Locals des Threads", """
        var total = 0
        var done = 0
        fire {
            var step = 7
            sync global { total = total + step }
            done = 1
        }
        while (done == 0) { sync globals }
        print("total " + total)
        """, new[] { "total 7" });

    CheckGl("`fire global { }`: der Thread wartet nicht, das Hauptprogramm fuehrt den Auftrag bei `sync globals` aus (taking als Wert)", """
        var total = 0
        var started = 0
        fire {
            var x = 7
            fire global taking x { total = total + x }
            fire global taking x { total = total + x * 10 }
        }
        while (total == 0) { sync globals }
        while (total < 77) { sync globals }
        print("total " + total)
        """, new[] { "total 77" });

    CheckGl("`fire global` mit einem Objekt als taking: der Auftrag bekommt eine Kopie", """
        class Box { int v; construct(int v) { this.v = v } }
        var seen = 0
        fire {
            var b = new Box(5)
            fire global taking b { seen = seen + b.v }
            b.v = 100
        }
        while (seen == 0) { sync globals }
        print("seen " + seen)
        """, new[] { "seen 5" });

    CheckGl("Programmende ohne `sync globals`: wartende Threads werden bedient, dann erst werden die Globals zerstoert", """
        class G { int v; construct() { this.v = 1 } destruct() { print("~G " + this.v) } }
        var g = new G()
        fire { g.v = 9 }
        print("ende")
        """, new[] { "ende", "~G 9" });

    CheckGl("Eine Exception im Block beendet die Sektion (der Hauptthread haengt nicht)", """
        class Exception { string message; construct(string message = "") { this.message = message } }
        var total = 0
        var done = 0
        fire {
            try { sync global { total = total + 1; throw new Exception("x") } } catch (e) { }
            sync global { total = total + 10; done = 1 }
        }
        while (done == 0) { sync globals }
        print("total " + total)
        """, new[] { "total 11" });

    CheckGl("Array der Globals: Thread liest live und schreibt Elemente (jedes Schreiben eine Sektion)", """
        var arr = new int[5]
        var done = 0
        fire {
            for (var i = 0; i < 5; i = i + 1) { arr[i] = i * 2 }
            sync global { done = 1 }
        }
        while (done == 0) { sync globals }
        var sum = 0
        for (var i = 0; i < 5; i = i + 1) { sum = sum + arr[i] }
        print("sum " + sum)
        """, new[] { "sum 20" });

    CheckGl("Statisches Feld: Thread schreibt, Hauptprogramm liest nach `sync globals`", """
        class Cfg { static int hits = 0 }
        var done = 0
        fire { Cfg.hits = 3; sync global { done = 1 } }
        while (done == 0) { sync globals }
        print("hits " + Cfg.hits)
        """, new[] { "hits 3" });

    CheckGl("Ein Thread startet einen Thread; beide schreiben ueber Sektionen", """
        var total = 0
        var done = 0
        fire {
            sync global { total = total + 1 }
            fire { sync global { total = total + 10; done = 1 } }
        }
        while (done == 0) { sync globals }
        print("total " + total)
        """, new[] { "total 11" });

    CheckGl("Unbehandelte Exception im Hauptprogramm: ein auf eine Sektion wartender Thread haengt nicht", """
        class Exception { string message; construct(string message = "") { this.message = message } }
        class G { int v; construct() { this.v = 1 } }
        var g = new G()
        fire { g.v = 2 }
        for (var i = 0; i < 100000; i = i + 1) { }
        throw new Exception("kaputt")
        """, new[] { "UNBEHANDELT: Unbehandelte Exception vom Typ 'Exception': kaputt" });

    CheckGl("terminate in einem Thread beendet ein Hauptprogramm, das nur `sync globals` ausfuehrt", """
        fire { terminate(1) }
        while (true) { sync globals }
        """, Array.Empty<string>());

    CheckGl("`sync globals` liefert 0 ohne wartende Threads", """
        print("n " + (sync globals))
        """, new[] { "n 0" });

    // ---- break/continue out of try/catch (the compiler unregisters handlers and executes the finally inline)
    CheckGl("break aus try: das finally laeuft, die Schleife endet", """
        var log = ""
        for (var i = 0; i < 5; i = i + 1) {
            try { if (i == 2) { break } log = log + i }
            finally { log = log + "f" }
        }
        print(log)
        """, new[] { "0f1ff" });

    CheckGl("continue aus try mit Locals und verschachtelten Bloecken", """
        var sum = 0
        for (var i = 0; i < 6; i = i + 1) {
            try { var x = i * 2; if (i % 2 == 0) { var y = 1; continue } sum = sum + x }
            catch (e) { }
        }
        print(sum)
        """, new[] { "18" });

    CheckGl("break aus try meldet den Handler ab: eine spaetere Exception faengt der aeussere catch", """
        class Exception { string message; construct(string message = "") { this.message = message } }
        try {
            while (true) { try { break } catch (e) { print("innen") } }
            throw new Exception("aussen")
        } catch (e) { print("gefangen " + e.message) }
        """, new[] { "gefangen aussen" });

    CheckGl("break aus catch (mit finally und eigenen Locals im catch)", """
        class Exception { string message; construct(string message = "") { this.message = message } }
        var log = ""
        var i = 0
        while (i < 5) {
            i = i + 1
            try { if (i == 3) { throw new Exception("drei") } log = log + i }
            catch (e) { var m = e.message; log = log + m; break }
            finally { log = log + "f" }
        }
        print(log + " " + i)
        """, new[] { "1f2fdreif 3" });

    CheckGl("continue aus catch", """
        class Exception { string message; construct(string message = "") { this.message = message } }
        var n = 0
        for (var i = 0; i < 4; i = i + 1) {
            try { throw new Exception("x") }
            catch (e) { n = n + 1; continue }
            n = n + 100
        }
        print(n)
        """, new[] { "4" });

    CheckGl("break aus zwei verschachtelten try: die finally laufen von innen nach aussen", """
        var log = ""
        while (true) {
            try {
                try { break } finally { log = log + "a" }
            } finally { log = log + "b" }
        }
        print(log)
        """, new[] { "ab" });

    CheckGl("break in einer inneren Schleife im try beruehrt das aeussere try nicht", """
        var log = ""
        try {
            for (var i = 0; i < 3; i = i + 1) { try { break } finally { log = log + "i" } }
            log = log + "x"
        } finally { log = log + "o" }
        print(log)
        """, new[] { "ixo" });

    CheckGl("break/continue aus `sync global { }`: die Sektion wird freigegeben", """
        var total = 0
        var done = 0
        fire {
            for (var i = 0; i < 10; i = i + 1) {
                sync global { if (i == 3) { break } total = total + 1 }
            }
            for (var j = 0; j < 4; j = j + 1) {
                sync global { if (j % 2 == 0) { continue } total = total + 10 }
            }
            sync global { done = 1 }
        }
        while (done == 0) { sync globals }
        print("total " + total)
        """, new[] { "total 23" });

    Console.WriteLine(glFailures == 0 ? "Alle Globals-Pruefungen bestanden." : $"FEHLER: {glFailures} Globals-Pruefung(en) fehlgeschlagen.");
}

// The scripts of the device checks with their expected output, for the native backend (a loopback device)
var devNativeCases = new List<(string Title, string Script, string[] Expected, string? DefaultId)>();

// ---------------------------------------------------------------------------
// Geraete: geteilter DeviceManager, Standardgeraet, EnsureConnected, IsShared, Paketverfolgung, Paketprotokoll
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Geraete ===");
    int devFailures = 0;

    fire.Device.Manager.DeviceManager.DeviceManager NewLoopbackManager(bool shared)
    {
        var manager = new fire.Device.Manager.DeviceManager.DeviceManager { IsShared = shared };
        manager.RegisterDriver(new fire.Device.Manager.Drivers.Loopback.LoopbackDriver());
        manager.RefreshDevices(true);
        return manager;
    }

    List<string> RunDev(string script, fire.Device.Manager.DeviceManager.DeviceManager manager, VmExecutionMode mode)
    {
        var lines = new List<string>();
        VM.ResetTerminateForTests();
        var session = fire.Compiler.RuntimeSession.Build(new[] { script }, mode, args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); }, deviceManager: manager);
        session.Run();
        VM.ResetTerminateForTests();
        if (session.VirtualMachine!.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(session.VirtualMachine.UnhandledException).Message);
        return lines;
    }

    void CheckDev(string title, string script, string[] expected, bool shared = true, string? defaultId = null,
        Action<fire.Device.Manager.DeviceManager.DeviceManager>? after = null)
    {
        if (after == null && !title.StartsWith("IsShared") && !script.Contains("#import \"graphics\"")) devNativeCases.Add((title, script, expected, defaultId));
        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            string[] actual;
            var manager = NewLoopbackManager(shared);
            manager.DefaultIdentifier = defaultId;
            try
            {
                var task = Task.Run(() => RunDev(script, manager, mode).ToArray());
                actual = task.Wait(TimeSpan.FromSeconds(30)) ? task.Result : new[] { "ZEITUEBERSCHREITUNG (haengt)" };
            }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + (ex.InnerException != null ? CompileErrors.Describe(ex.InnerException) : ex.Message) }; }
            bool ok = actual.SequenceEqual(expected);
            if (ok && after != null)
            {
                try { after(manager); }
                catch (Exception ex) { ok = false; actual = new[] { "NACHPRUEFUNG: " + ex.Message }; }
            }
            if (!ok) devFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
            manager.Shutdown();
        }
    }

    CheckDev("Ohne Standardgeraet: HasDefault ist false, Device.Default wirft DeviceNotFoundException", """
        #import "devices"
        print(Device.HasDefault)
        try {
            var d = Device.Default
            print("kein Fehler")
        } catch (DeviceNotFoundException e) {
            print("keins: " + e.message)
        }
        """, new[] { "False", "keins: No default device selected" });

    CheckDev("Standardgeraet: Device.Default, IsConnected (Property und Methode), EnsureConnected, Senden/Empfangen", """
        #import "devices"
        #import "time"
        var d = Device.Default
        print(d.Identifier())
        print(d.IsConnected)
        print(d.IsConnected())
        d.EnsureConnected().EnsureConnected()
        print(d.IsConnected)
        print(d.DoCommand("hallo"))
        var tries = 0
        while (!d.HasData() && tries < 200) { Sleep(TimeSpan.FromMilliseconds(10)); tries = tries + 1 }
        print(d.ReadString())
        d.Disconnect()
        print(d.IsConnected)
        print(d.DoCommand("weg"))
        """, new[] { "loopback:echo", "False", "False", "True", "True", "hallo\n", "False", "False" }, defaultId: "loopback:echo");

    CheckDev("IsShared: Geraet und Manager eines geteilten Managers melden es, ein eigener Manager nicht", """
        #import "devices"
        var m = new DeviceManagerFacade()
        print(m.IsShared())
        print(m.GetByIdentifier("loopback:echo").IsShared)
        """, new[] { "True", "True" }, shared: true);
    CheckDev("IsShared: ein nicht geteilter Manager meldet false", """
        #import "devices"
        var m = new DeviceManagerFacade()
        print(m.IsShared())
        print(m.GetByIdentifier("loopback:echo").IsShared)
        """, new[] { "False", "False" }, shared: false);

    CheckDev("Geteilter Manager ueberlebt den Lauf: verbundenes Geraet bleibt verbunden, Manager ist nicht abgebaut", """
        #import "devices"
        Device.Default.EnsureConnected()
        print("verbunden")
        """, new[] { "verbunden" }, shared: true, defaultId: "loopback:echo", after: m =>
        {
            if (m.DeviceCount != 1) throw new Exception("Geraet verschwunden");
            if (!m.GetDeviceByHandle(m.GetHandleByIdentifier("loopback:echo")!.Value)!.IsConnected) throw new Exception("Verbindung wurde getrennt");
            m.Dispose(); // no effect with a shared manager
            if (m.DeviceCount != 1) throw new Exception("Dispose hat den geteilten Manager abgebaut");
        });

    CheckDev("Eigener Manager wird nach dem Lauf freigegeben (Geraete getrennt)", """
        #import "devices"
        var d = new DeviceManagerFacade().GetByIdentifier("loopback:echo")
        d.EnsureConnected()
        print(d.IsConnected)
        """, new[] { "True" }, shared: false, after: m =>
        {
            if (m.DeviceCount != 0) throw new Exception("Manager nicht abgebaut");
        });

    CheckDev("Paketverfolgung: gesendete und empfangene Pakete werden mitgeschnitten", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        d.DoCommand("ab")
        while (!d.HasData()) { }
        print(d.ReadString())
        """, new[] { "ab\n" }, defaultId: "loopback:echo");

    // ---- Befehlsebene: Write/Read, WaitFor, Command ----
    CheckDev("Write/Read: rohe Bytes hinaus, als Puffer zurueck; WriteString ohne Zeilenende, ReadString als Text", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        var data = new byte[4]
        data[0] = 1
        data[1] = 200
        data[2] = 0
        data[3] = 255
        print(d.Write(data))
        while (!d.HasData()) { }
        var back = d.Read()
        print(back.length + " " + back[0] + " " + back[1] + " " + back[2] + " " + back[3])
        print(d.Read().length)
        print(d.WriteString("abc"))
        while (!d.HasData()) { }
        print("[" + d.ReadString() + "]")
        print("[" + d.ReadString() + "]")
        d.Disconnect()
        print(d.Write(data))
        print(d.WriteString("x"))
        """, new[] { "True", "4 1 200 0 255", "0", "True", "[abc]", "[]", "False", "False" }, defaultId: "loopback:echo");

    CheckDev("WaitForString: ueber Paketgrenzen, schneidet den Puffer hinter dem Treffer ab, dasselbe Muster zweimal hintereinander", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        d.WriteString("ok")
        d.WriteString("ay-ab-ab-ende")
        print(d.WaitForString("kay"))
        print(d.WaitForString("ab"))
        print(d.WaitForString("ab"))
        print(d.WaitForString("ab", 150))
        print(d.WaitForString("ende"))
        print("[" + d.ReadString() + "]")
        d.WriteString("xyz")
        print(d.WaitForString(""))
        print(d.WaitForString("z"))
        print("[" + d.ReadString() + "]")
        """, new[] { "True", "True", "True", "False", "True", "[]", "True", "True", "[]" }, defaultId: "loopback:echo");

    CheckDev("WaitForString: was vor dem Treffer lag, ist verbraucht; was danach kam, bleibt lesbar", """
        #import "devices"
        #import "time"
        var d = Device.Default.EnsureConnected()
        d.WriteString("vorspann|nutzlast")
        print(d.WaitForString("|"))
        print("[" + d.ReadString() + "]")
        d.WriteString("eins")
        d.WriteString("zwei")
        print(d.WaitForString("ei"))
        print("[" + d.ReadString() + "]")
        var tries = 0
        while (!d.HasData() && tries < 200) { Sleep(TimeSpan.FromMilliseconds(10)); tries = tries + 1 }   // the echo of "zwei" arrives a moment later
        print("[" + d.ReadString() + "]")
        """, new[] { "True", "[nutzlast]", "True", "[ns]", "[zwei]" }, defaultId: "loopback:echo");

    CheckDev("WaitFor mit Bytes: Muster mit Nullbytes, ueber Pakete", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        var a = new byte[3]
        a[0] = 9
        a[1] = 0
        a[2] = 7
        var b = new byte[3]
        b[0] = 0
        b[1] = 7
        b[2] = 5
        d.Write(a)
        d.Write(b)
        var pattern = new byte[3]
        pattern[0] = 0
        pattern[1] = 7
        pattern[2] = 0
        print(d.WaitFor(pattern))
        var rest = d.Read()
        print(rest.length + " " + rest[0] + " " + rest[1])
        """, new[] { "True", "2 7 5" }, defaultId: "loopback:echo");

    CheckDev("WaitForString: Wartezeit als Zeitwert, Millisekunden und TimeSpan; Ablauf liefert false; ungueltige Wartezeit ist eine DeviceArgumentException", """
        #import "devices"
        #import "time"
        var d = Device.Default.EnsureConnected()
        var t0 = DateTime.Now()
        print(d.WaitForString("nie", 120ms))
        print(d.WaitForString("nie", 120))
        print(d.WaitForString("nie", TimeSpan.FromMilliseconds(120)))
        var ms = (DateTime.Now() - t0).TotalMilliseconds
        print((ms >= 330) + " " + (ms < 5000))
        try { d.WaitForString("a", "x") } catch (e) { print((e is of DeviceArgumentException) + " " + e.message) }
        """, new[] { "False", "False", "False", "True True", "True Invalid wait time (expected: TimeSpan, a time value like 5s, or milliseconds)" }, defaultId: "loopback:echo");

    CheckDev("#timeout: die Standard-Wartezeit ohne eigene Zeitangabe (sonst 30 Sekunden)", """
        #import "devices"
        #import "time"
        #timeout 200ms
        var d = Device.Default.EnsureConnected()
        var t0 = DateTime.Now()
        print(d.WaitForString("nie"))
        var ms = (DateTime.Now() - t0).TotalMilliseconds
        print((ms >= 190) + " " + (ms < 5000))
        """, new[] { "False", "True True" }, defaultId: "loopback:echo");

    CheckDev("WaitFor bei getrenntem Geraet endet sofort mit false", """
        #import "devices"
        #import "time"
        var d = Device.Default.EnsureConnected()
        d.Disconnect()
        var t0 = DateTime.Now()
        print(d.WaitForString("x", 20s))
        print((DateTime.Now() - t0).TotalMilliseconds < 3000)
        """, new[] { "False", "True" }, defaultId: "loopback:echo");

    CheckDev("DoCommand: Text als Zeile, Command<IDevice> mit dem Geraet als Kontext, abgeleitete Befehle, DoCommands", """
        #import "devices"
        var d = Device.Default.EnsureConnected()
        print(d.DoCommand("M105"))
        print(d.WaitForString("M105\n"))

        var hello = new Command<IDevice>()
        hello.Command = dev => { return dev.WriteString("hallo;") }
        print(d.DoCommand(hello))
        print(d.WaitForString("hallo;"))

        class Ping : Command<IDevice> {
            Execute(IDevice context) { return context.WriteString("ping;") }
        }
        print(d.DoCommand(new Ping()))
        print(d.WaitForString("ping;"))

        var list = new List()
        list.Add(new Ping())
        list.Add(hello)
        print(d.DoCommands(list))
        print(d.WaitForString("ping;hallo;"))
        print(d.DoCommands([new Ping(), new Ping()]))
        print(d.WaitForString("ping;ping;"))

        var failing = new Command<IDevice>(dev => false)
        var counted = new Command<IDevice>(dev => { dev.WriteString("nie;") })
        print(d.DoCommands([failing, counted]))
        print(d.WaitForString("nie;", 100))
        print(d.DoCommand(new Command<IDevice>()))
        d.Disconnect()
        print(d.DoCommand(hello))
        print(d.DoCommand("weg"))
        """, new[] { "True", "True", "True", "True", "True", "True", "True", "True", "True", "True", "False", "False", "True", "False", "False" }, defaultId: "loopback:echo");

    // Packet tracking directly on the manager (without a script)
    {
        var manager = NewLoopbackManager(true);
        var captured = new List<fire.Device.Manager.DeviceManager.PacketRecord>();
        manager.PacketCaptured += p => { lock (captured) captured.Add(p); };
        var device = manager.GetDeviceByHandle(manager.GetHandleByIdentifier("loopback:echo")!.Value)!;
        device.Connect();
        device.SendCommand("ping");
        for (int i = 0; i < 200 && captured.Count < 2; i++) Thread.Sleep(10);
        bool ok;
        lock (captured)
            ok = captured.Count == 2
                && captured[0].Direction == fire.Device.Manager.DeviceManager.PacketDirection.HostToDevice
                && captured[1].Direction == fire.Device.Manager.DeviceManager.PacketDirection.DeviceToHost
                && captured.All(c => c.DeviceIdentifier == "loopback:echo" && System.Text.Encoding.UTF8.GetString(c.Data) == "ping\n");
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Paketverfolgung am Manager: Senden und Empfangen werden in Reihenfolge mitgeschnitten" : "FEHLER: Paketverfolgung am Manager");

        // Zustandsaenderungen
        var states = new List<bool>();
        manager.DeviceStateChanged += slot => { lock (states) states.Add(slot.Device.IsConnected); };
        device.Disconnect();
        device.Connect();
        ok = states.SequenceEqual(new[] { false, true });
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: DeviceStateChanged meldet Trennen und Verbinden" : "FEHLER: DeviceStateChanged: " + string.Join(",", states));

        // Standardgeraet
        int changes = 0;
        manager.DefaultChanged += () => changes++;
        manager.DefaultIdentifier = "loopback:echo";
        manager.DefaultIdentifier = "loopback:echo";
        ok = changes == 1 && manager.DefaultHandle == manager.GetHandleByIdentifier("loopback:echo");
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Standardgeraet: DefaultChanged feuert nur bei einer Aenderung, DefaultHandle passt" : "FEHLER: Standardgeraet");

        // Treiber entfernen
        ok = manager.RemoveDriver("loopback") && manager.DeviceCount == 0 && manager.DefaultHandle == null;
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: RemoveDriver entfernt Treiber samt Geraeten" : "FEHLER: RemoveDriver");
        manager.Shutdown();
    }

    // All extensions together: native functions are jumped to via their index, the order of registration
    // when translating and when executing must match (formerly: graphics + time -> wrong function)
    CheckDev("Alle Erweiterungen in einem Programm (graphics, time, reflection, linq, devices, io)", """
        #import "graphics"
        #import "time"
        #import "reflection"
        #import "linq"
        #import "devices"
        #import "io"
        var fb = new Framebuffer(8, 4)
        print(fb.Width() + "x" + fb.Height())
        print(DateTime.Now().Year > 2000)
        print(Type.Of(fb).Name)
        print(new DeviceManagerFacade().Count())
        print(IO.File.Exists("/gibt/es/nicht"))
        var con = new Renderer(fb)
        con.AlphaBlending = false      // without blending the value including alpha 0 is simply copied
        con.FillRect(0, 0, 2, 2, new SolidBrush(256))
        print(con.GetPixel(1, 1))
        """, new[] { "8x4", "True", "Framebuffer", "1", "False", "256" });

    // Packet log: saving and loading lossless
    {
        var t0 = new DateTime(2026, 10, 3, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567);
        var packets = new List<fire.Device.Manager.DeviceManager.PacketRecord>
        {
            new(t0, "serial:COM3", fire.Device.Manager.DeviceManager.PacketDirection.HostToDevice, System.Text.Encoding.UTF8.GetBytes("M105\n")),
            new(t0.AddMilliseconds(30), "serial:COM3", fire.Device.Manager.DeviceManager.PacketDirection.DeviceToHost, new byte[] { 0x00, 0xFF, 0x7F, 0xC3, 0xA4 }),
            new(t0.AddMilliseconds(40), "serial:COM3", fire.Device.Manager.DeviceManager.PacketDirection.DeviceToHost, Array.Empty<byte>()),
        };
        var text = fire.Device.Manager.DeviceManager.PacketLog.Serialize(packets);
        var back = fire.Device.Manager.DeviceManager.PacketLog.Parse(text);
        bool ok = back.Count == 3 && Enumerable.Range(0, 3).All(i =>
            back[i].Time == packets[i].Time && back[i].Direction == packets[i].Direction && back[i].DeviceIdentifier == packets[i].DeviceIdentifier && back[i].Data.SequenceEqual(packets[i].Data));
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Paketprotokoll: Speichern und Laden ist verlustfrei (Zeit, Richtung, Geraet, Binaerdaten, leeres Paket)" : "FEHLER: Paketprotokoll Roundtrip\n" + text);

        string[] bad = { "x\tH2D\ta\t00", "2026-10-03T12:00:00Z\tUP\ta\t00", "2026-10-03T12:00:00Z\tH2D\ta\tZZ", "nur eine Spalte" };
        ok = bad.All(b =>
        {
            try { fire.Device.Manager.DeviceManager.PacketLog.Parse(b); return false; }
            catch (FormatException) { return true; }
        });
        if (!ok) devFailures++;
        Console.WriteLine(ok ? "OK: Paketprotokoll: fehlerhafte Zeilen werden mit FormatException abgelehnt" : "FEHLER: Paketprotokoll nimmt fehlerhafte Zeilen an");
    }

    Console.WriteLine(devFailures == 0 ? "Alle Geraete-Pruefungen bestanden." : $"FEHLER: {devFailures} Geraete-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Debugger run of the editor: VM.RunUntilBreakpoint/RunUntilEnd (F5), line table (binary search), native forwarding
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Debugger-Lauf ===");
    int dbgFailures = 0;
    void DbgCheck(bool ok, string title, string? detail = null)
    {
        if (!ok) dbgFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}{(detail != null ? "\n  " + detail : "")}");
    }

    (fire.Compiler.RuntimeSession Session, List<string> Lines) BuildDbg(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var session = fire.Compiler.RuntimeSession.Build(new[] { script }, mode, args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        return (session, lines);
    }

    foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
    {
        // Loops (for/while/foreach): a breakpoint in the last line of the body hits exactly once per pass - not once more
        // when leaving the loop. In addition the earlier loop as a reference (StepInstruction + CurrentLocation per instruction):
        // RunUntilBreakpoint must stop at the same places.
        foreach (var (kind, loopScript) in new[]
        {
            ("for", "var total = 0\nfor (var i = 0; i < 3; i = i + 1) {\n    total = total + i\n}\nprint(\"fertig \" + total)\n"),
            ("while", "var total = 0\nvar i = 0\nwhile (i < 3) {\n    total = total + i\n    i = i + 1\n}\nprint(\"fertig \" + total)\n"),
            ("foreach", "var total = 0\nvar items = [0, 1, 2]\nforeach (x in items) {\n    total = total + x\n}\nprint(\"fertig \" + total)\n"),
        })
        {
            int bpLine = kind == "for" ? 3 : kind == "while" ? 5 : 4;
            VM.ResetTerminateForTests();
            var (refSession, refLines) = BuildDbg(loopScript, mode);
            var refVm = refSession.VirtualMachine!;
            var refBps = new HashSet<(int SourceIndex, int Line)> { (refSession.FirstUserSourceIndex, bpLine) };
            int refHits = 0;
            {
                var lastLocation = refVm.CurrentLocation;
                while (refVm.StepInstruction())
                {
                    var location = refVm.CurrentLocation;
                    if (location != lastLocation) { lastLocation = location; if (refBps.Contains(location)) refHits++; }
                }
            }
            VM.ResetTerminateForTests();

            VM.ResetTerminateForTests();
            var (session, lines) = BuildDbg(loopScript, mode);
            var vm = session.VirtualMachine!;
            var bps = new HashSet<(int SourceIndex, int Line)> { (session.FirstUserSourceIndex, bpLine) };
            int hits = 0;
            var lineAtHit = new List<int>();
            while (vm.RunUntilBreakpoint(bps, () => false))
            {
                hits++;
                lineAtHit.Add(vm.CurrentLine);
                if (hits > 10) break;
            }
            DbgCheck(hits == 3 && hits == refHits && lineAtHit.All(l => l == bpLine) && lines.SequenceEqual(new[] { "fertig 3" }) && refLines.SequenceEqual(lines),
                $"RunUntilBreakpoint ({kind}): Haltepunkt im Body trifft genau einmal je Durchlauf (wie die Einzelschritt-Schleife), danach laeuft das Programm zu Ende [{mode}]",
                $"Treffer {hits} (Referenz {refHits}), Zeilen {string.Join(",", lineAtHit)}, Ausgabe {string.Join("|", lines)}");
            VM.ResetTerminateForTests();
        }
    }

    {
        // Without a breakpoint a program runs to the end in one go; a breakpoint in the FIRST line does not count at the start
        VM.ResetTerminateForTests();
        var (session, lines) = BuildDbg("print(\"a\")\nprint(\"b\")\n", VmExecutionMode.Debug);
        var vm = session.VirtualMachine!;
        bool stopped = vm.RunUntilBreakpoint(new HashSet<(int, int)> { (session.FirstUserSourceIndex, 1) }, () => false);
        DbgCheck(!stopped && lines.SequenceEqual(new[] { "a", "b" }), "RunUntilBreakpoint: ein Haltepunkt auf der Startzeile haelt nicht sofort an, das Programm laeuft zu Ende");
        VM.ResetTerminateForTests();
    }

    {
        // A pause request interrupts an infinite loop (RunUntilEnd and RunUntilBreakpoint)
        foreach (var useBreakpointRun in new[] { false, true })
        {
            VM.ResetTerminateForTests();
            var (session, _) = BuildDbg("var n = 0\nwhile (true) { n = n + 1 }\n", VmExecutionMode.Release);
            var vm = session.VirtualMachine!;
            volatile_bool pause = new();
            var timer = Task.Run(() => { Thread.Sleep(100); pause.Value = true; });
            var run = Task.Run(() => useBreakpointRun ? vm.RunUntilBreakpoint(new HashSet<(int, int)>(), () => pause.Value) : vm.RunUntilEnd(() => pause.Value));
            bool finishedInTime = run.Wait(TimeSpan.FromSeconds(20));
            DbgCheck(finishedInTime && run.Result, $"Pause-Anforderung haelt eine Endlosschleife an ({(useBreakpointRun ? "RunUntilBreakpoint" : "RunUntilEnd")})");
            VM.ResetTerminateForTests();
        }
    }

    {
        // Line table: binary search yields the same places as the line of each instruction suggests
        VM.ResetTerminateForTests();
        var (session, _) = BuildDbg("var a = 1\nvar b = 2\n\nvar c = a + b\nprint(c)\n", VmExecutionMode.Debug);
        var chunk = session.CompiledProgram.TopLevel;
        var seen = new List<int>();
        int ip = 0, lastLine = -1;
        for (; ip < chunk.Code.Count; ip++)
        {
            var (src, line) = chunk.GetLocation(ip);
            if (src == session.FirstUserSourceIndex && line != lastLine) { seen.Add(line); lastLine = line; }
        }
        DbgCheck(seen.SequenceEqual(new[] { 1, 2, 4, 5 }), "Chunk.GetLocation (binaere Suche): die Zeilen des Programms in Reihenfolge", string.Join(",", seen));
        var (s0, l0) = chunk.GetLocationRange(0, out int rs, out int re);
        DbgCheck(rs == 0 && re > 0 && re < chunk.Code.Count, "Chunk.GetLocationRange: Bereich der ersten Stelle beginnt bei 0 und endet vor dem Chunk-Ende");
    }

    {
        // Forwarding to native functions (methods of the bridge preludes): same result as the direct call, also with a return value
        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            VM.ResetTerminateForTests();
            var (session, lines) = BuildDbg("""
                #import "graphics"
                var fb = new Framebuffer(16, 16)
                var con = new Renderer(fb)
                con.AlphaBlending = false
                var viaMethod = 0
                var direct = 0
                for (var i = 0; i < 5; i = i + 1) {
                    con.FillRect(i, 0, 1, 1, new SolidBrush(256 + i))
                    viaMethod = viaMethod + con.GetPixel(i, 0) + con.CellWidth()
                    direct = direct + __GRPHRndGetPixel(con.id, i, 0) + __GRPHRndCellWidth(con.id)
                }
                print(viaMethod == direct)
                print(viaMethod)
                """, mode);
            session.Run();
            DbgCheck(lines.SequenceEqual(new[] { "True", "1330" }), $"Methoden der Grafik-Bruecke (Weiterleitung an native Funktionen) liefern dasselbe wie der direkte Aufruf [{mode}]", string.Join("|", lines));
            VM.ResetTerminateForTests();
        }
    }

    Console.WriteLine(dbgFailures == 0 ? "Alle Debugger-Lauf-Pruefungen bestanden." : $"FEHLER: {dbgFailures} Debugger-Lauf-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Lambda captures, short syntax `x => ...` and the query library (#import "linq")
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Lambda-Captures und LINQ ===");
    int lqFailures = 0;

    List<string> RunLq(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var natives = NativeRegistry.CreateDefault();
        natives.Register("print", args =>
        {
            var shown = VM.StringifyForPrint(args);
            if (shown != null) lock (lines) lines.Add(shown[0].ToString());
            return Value.MakeUndefined();
        });
        natives.RegisterBaseTypeNatives();
        var sources = new List<string> { fire.Standard.Prelude.Source };
        if (script.Contains("#import \"linq\""))
        {
            // linq brings reflection along (SelectProperty/SelectField)
            fire.Runtime.ReflectionNatives.Register(natives);
            sources.Add(fire.Standard.ReflectionPrelude.Source);
            sources.Add(fire.Standard.LinqPrelude.Source);
        }
        if (script.Contains("#import \"time\""))
        {
            // time is a package: its prelude, and its natives in the library built from the C++ of the package (the VM runs Sleep itself)
            var timeImport = fire.Package.Manager.PackageStore.Default.FindImport("time")!;
            sources.Add(timeImport.ReadPrelude()!);
            var (timeNames, timeLibraries) = fire.Compiler.PackageImports.NativesOf(new[] { timeImport.Key });
            string timeLibrary = fire.Compiler.PackageLibrary.Ensure(timeImport);
            fire.Runtime.PackageNativeBinding.Register(natives, timeNames, timeLibraries, _ => timeLibrary);
        }
        sources.Add(script);
        var program = Parser.ParseMultiple(sources
            .Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                fire.Compiler.RuntimeSession.CreateProjectDirectiveRegistry())).ToList());
        var compiled = Compiler.Compile(program, Resolver.Resolve(program, natives.Names), natives);
        VM.ResetTerminateForTests();
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: mode);
        vm.Run();
        VM.ResetTerminateForTests();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckLq(string title, string script, string[] expected, VmExecutionMode[]? modes = null)
    {
        foreach (var mode in modes ?? new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            string[] actual;
            try { actual = RunLq(script, mode).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + CompileErrors.Describe(ex) }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) lqFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    CheckLq("Kurzsyntax: x => ..., (a, b) => ..., () => ..., mit Block", """
        var inc = x => x + 1
        var add = (a, b) => a + b
        var five = () => 5
        var blk = x => { var t = x * 2; return t + 1 }
        print(inc(1) + " " + add(2, 3) + " " + five() + " " + blk(4))
        """, new[] { "2 5 5 9" });

    CheckLq("func (...) on ziel => ...: das Ziel von `on` ist kein Kurzform-Lambda (Bezeichner und geklammert)", """
        class W { string n; construct(string n) { this.n = n } Run(class f) { f(1, 2, 3) } }
        var win = new W("w")
        win.Run(func (x, y, b) on win => { print($"{x} {y} {b} {this.n}") })
        win.Run(func (x, y, b) on (win) => print("paren " + this.n))
        win.Run(func (x, y, b) on win => print("expr " + x))
        var f = x => x + 1
        print(f(1))
        """, new[] { "1 2 3 w", "paren w", "expr 1", "2" });

    CheckLq("func (...) on ziel => ...: Mitglieder des Ziels sind unqualifiziert sichtbar (lesen, schreiben, ++, Methoden)", """
        class W { string n; int c; construct(string n) { this.n = n; this.c = 0 } Hello(string s) { return "hello " + s + " " + this.n } Run(class f) { f(1, 2, 3) } }
        var win = new W("w")
        win.Run(func (x, y, b) on win => { c = c + x; c++; print($"x: {x} y: {y} b: {b} n: {n} c: {c}"); print(Hello("a")) })
        print(win.c)
        var plain = func (x) => x + 1
        print(plain(1))
        """, new[] { "x: 1 y: 2 b: 3 n: w c: 2", "hello a w", "2", "2" });

    const string timeHead = "#import \"time\"\nclass Exception { string message; construct(string message = \"\") { this.message = message } }\n";

    CheckLq("TimeSpan: Fabriken, Komponenten, Summen, Vergleiche, Text", timeHead + """
        var a = TimeSpan.FromSeconds(90)
        var b = new TimeSpan(1, 2, 3, 4, 500)
        print(a.ToString() + " " + a.TotalMinutes + " " + a.Minutes + " " + a.Seconds)
        print(b.ToString() + " " + b.Days + " " + b.Hours + " " + b.Milliseconds)
        print((a + b).ToString() + " | " + (b - a).ToString() + " | " + (a * 2).ToString() + " | " + (a / 3).ToString())
        print((a < b) + " " + (a ## b) + " " + (a == TimeSpan.FromSeconds(90)) + " " + a.CompareTo(b) + " " + a.Negate().Abs().Equals(a))
        print(TimeSpan.Of(1.5s).TotalMilliseconds + " " + TimeSpan.Of(250ms).TotalSeconds + " " + TimeSpan.Of(2).TotalMilliseconds + " " + TimeSpan.FromMinutes(2).TotalSeconds)
        """, new[]
        {
            "00:01:30 1.5 1 30", "1.02:03:04.5000000 1 2 500",
            "1.02:04:34.5000000 | 1.02:01:34.5000000 | 00:03:00 | 00:00:30",
            "True True True -1 True",
            "1500 0.25 2 120",
        });

    CheckLq("DateTime: Komponenten, Rechnen, Format, Parse, Differenz, Vergleiche, Unix", timeHead + """
        var d = new DateTime(2024, 3, 15, 14, 30, 5)
        var a = TimeSpan.FromSeconds(90)
        print(d.ToString() + " " + d.Year + " " + d.Month + " " + d.Day + " " + d.DayOfWeek + " " + d.DayName() + " " + d.DayOfYear)
        print(d.AddDays(20).ToString() + " | " + d.AddMonths(11).ToString("dd.MM.yyyy") + " | " + (d + a).ToString("HH:mm:ss") + " | " + d.AddYears(-1).Year)
        var e = new DateTime(2024, 3, 17)
        print((e - d).ToString() + " " + (e > d) + " " + (d - TimeSpan.FromHours(15)).ToString() + " " + (d ## e))
        print(DateTime.Parse("2024-12-24 18:00").ToString() + " " + DateTime.IsLeapYear(2024) + " " + DateTime.DaysInMonth(2023, 2) + " " + (DateTime.TryParse("quatsch") == undefined))
        print(d.Date().ToString() + " " + d.TimeOfDay().ToString())
        print(DateTime.FromUnixSeconds(86400).ToString() + " " + DateTime.FromUnixSeconds(86400).ToUnixSeconds())
        try { new DateTime(2024, 13, 1) } catch (x) { print("1 " + x.message) }
        try { DateTime.Parse("quatsch") } catch (x) { print("2 " + x.message) }
        print(DateTime.Now().Year >= 2024)
        print(DateTime.UtcNow().Kind + " " + DateTime.Now().Kind)
        """, new[]
        {
            "2024-03-15 14:30:05 2024 3 15 5 Friday 75",
            "2024-04-04 14:30:05 | 15.02.2025 | 14:31:35 | 2023",
            "1.09:29:55 True 2024-03-14 23:30:05 True",
            "2024-12-24 18:00:00 True 28 True",
            "2024-03-15 00:00:00 14:30:05",
            "1970-01-02 00:00:00 86400",
            "1 Invalid date/time: 2024-13-1 0:0:0.0",
            "2 Not a valid date: 'quatsch'",
            "True",
            "utc local",
        });

    CheckLq("Objekte mit ToString(): print, Textverkettung (beide Seiten), Interpolation; ohne ToString bleibt die alte Darstellung, eine Exception darin laeuft zum Aufrufer", timeHead + """
        class P { string n; construct(string n) { this.n = n } ToString() { return "P(" + this.n + ")" } }
        class Q { int x; construct() { this.x = 1 } }
        class Bad { ToString() { throw new Exception("nein") } }
        var d = new DateTime(2024, 3, 15, 14, 30, 5)
        print(d)
        print("jetzt: " + d + " / " + new P("x") + " / " + TimeSpan.FromSeconds(75))
        print(d + " ist heute")
        print($"{d} {new P("y")}")
        print(new P("z"))
        print(("q=" + new Q()).Length > 3)
        try { print("b=" + new Bad()) } catch (e) { print("1 " + e.message) }
        try { print(new Bad()) } catch (e) { print("2 " + e.message) }
        print("ende")
        """, new[]
        {
            "2024-03-15 14:30:05", "jetzt: 2024-03-15 14:30:05 / P(x) / 00:01:15", "2024-03-15 14:30:05 ist heute", "2024-03-15 14:30:05 P(y)",
            "P(z)", "True", "1 nein", "2 nein", "ende",
        });

    CheckLq("Sleep: Zeitspanne, Zeitwert und Millisekunden; Dauer stimmt", timeHead + """
        var t0 = DateTime.UtcNow()
        Sleep(TimeSpan.FromMilliseconds(120))
        Sleep(60ms)
        Sleep(30)
        var ms = (DateTime.UtcNow() - t0).TotalMilliseconds
        print((ms >= 190) + " " + (ms < 2000))
        try { Sleep("x") } catch (e) { print(e.message) }
        """, new[] { "True True", "Sleep expects a TimeSpan, a time value or milliseconds, got: String." });

    CheckLq("Sleep arbeitet die Warteschlange ab (automatischer Globals-Sync); mit #nosync nicht; fire global-Auftraege auch", timeHead + """
        var counter = 0
        var jobs = 0
        fire { sync global { counter = counter + 1 } }
        fire { fire global { jobs = jobs + 10 } }
        Sleep(400ms)
        print("counter " + counter + " jobs " + jobs)
        """, new[] { "counter 1 jobs 10" });

    CheckLq("Sleep mit #nosync laesst die Warteschlange liegen, bis `sync globals` kommt", "#nosync\n" + timeHead + """
        var counter = 0
        fire { sync global { counter = counter + 1 } }
        Sleep(300ms)
        print("vorher " + counter)
        var n = 0
        while (counter == 0) { n = sync globals }
        print("nachher " + counter)
        """, new[] { "vorher 0", "nachher 1" });

    CheckLq("terminate aus einem Thread beendet ein laufendes Sleep sofort", timeHead + """
        fire { Sleep(100ms); terminate(3) }
        Sleep(20s)
        print("nie")
        """, new string[0]);

    CheckLq("Capture: ein lokaler Wert wird kopiert (spaetere Aenderungen sind unsichtbar)", """
        class T {
            static Run() {
                var local = 3
                var f = x => x > local
                local = 10
                print(f(5) + " " + f(2))
            }
        }
        T.Run()
        """, new[] { "True False" });

    CheckLq("Capture: Parameter der umgebenden Methode, verschachtelte Lambdas, Schleifenvariable", """
        class T {
            static Make(int offset) { return x => x + offset }
            static Run() {
                var f = T.Make(100)
                var nested = a => (b => a + b)
                var fs = new List()
                for (var i = 0; i < 3; i = i + 1) { var q = i * 10; fs.Add(() => q + i) }
                var parts = ""
                foreach (fn in fs) { parts = parts + fn() + " " }
                print(f(1) + " " + nested(1)(2) + " " + parts)
            }
        }
        T.Run()
        """, new[] { "101 3 0 11 22 " });

    CheckLq("Capture: eine Deklaration im Lambda verdeckt den gleichnamigen aeusseren Wert", """
        class T {
            static Run() {
                var local = 3
                var f = x => { var local = 100; return local + x }
                print(f(1))
            }
        }
        T.Run()
        """, new[] { "101" });

    CheckLq("Capture: Globals bleiben lebendig (kein Kopieren)", """
        var g = 1
        var f = () => g
        g = 7
        print(f())
        """, new[] { "7" });

    CheckLq("Capture: Zuweisung an den Capture ist ein Fehler", """
        class T {
            static Run() {
                var n = 1
                var f = () => { n = 2; return n }
                print(f())
            }
        }
        T.Run()
        """, new[] { "AUSNAHME: 'n' is a COPY of the outer variable inside the lambda (capture) and cannot be assigned there (declare a new local variable with a different name) (4)" });

    CheckLq("Capture: ein Objekt wird als Referenz geteilt", """
        class Box { int n; construct() { this.n = 0 } }
        class T {
            static Run() {
                var b = new Box()
                var bump = () => { b.n = b.n + 1 }
                bump(); bump()
                print(b.n)
            }
        }
        T.Run()
        """, new[] { "2" });

    CheckLq("return mitten in einem foreach hinterlaesst nichts auf dem Stack", """
        class T {
            static First(class l) { foreach (x in l) { return x } return 0 }
            static Run() {
                var l = [5, 3]
                print(10 + T.First(l) + T.First(l))
            }
        }
        T.Run()
        """, new[] { "20" });

    const string linqHead = "#import \"linq\"\nclass P { string name; int price; construct(string n, int p) { this.name = n; this.price = p } }\n";

    CheckLq("LINQ: Where/Select mit Capture, trage Auswertung, mehrfach durchlaufbar", linqHead + """
        class T {
            static Run() {
                var limit = 2
                var nums = new List([5, 3, 8, 1, 9, 2, 8])
                var q = nums.Where(x => x > limit).Select(x => x * 10)
                print(q.Join(",") + " | " + q.Count())
                print(Linq.From([1, 2, 3, 4, 5, 6]).Where(x => x % 2 == 0).Select(x => x * x).Join(" "))
            }
        }
        T.Run()
        """, new[] { "50,30,80,90,80 | 5", "4 16 36" });

    CheckLq("LINQ: Sortieren (stabil), Distinct, Reverse", linqHead + """
        var nums = new List([5, 3, 8, 1, 9, 2, 8])
        print(nums.OrderBy(x => x).Join(",") + " | " + nums.OrderByDescending(x => x).Join(",") + " | " + nums.Distinct().Count() + " | " + nums.Reverse().Join(","))
        var ps = new List()
        ps.Add(new P("a", 20)); ps.Add(new P("b", 10)); ps.Add(new P("c", 20)); ps.Add(new P("d", 10))
        print(ps.OrderBy(p => p.price).Select(p => p.name).Join(""))
        """, new[] { "1,2,3,5,8,8,9 | 9,8,8,5,3,2,1 | 6 | 8,2,9,1,8,3,5", "bdac" });

    CheckLq("LINQ: Take/Skip/TakeWhile/SkipWhile/Concat/Zip/SelectMany/Range", linqHead + """
        var nums = new List([5, 3, 8, 1, 9, 2, 8])
        print(nums.Take(3).Join(",") + " | " + nums.Skip(5).Join(",") + " | " + nums.TakeWhile(x => x > 2).Join(",") + " | " + nums.SkipWhile(x => x > 2).Join(","))
        print(Linq.Range(1, 3).Concat([7, 8]).Join(",") + " | " + Linq.Range(1, 3).Zip([10, 20, 30], (a, b) => a * b).Join(",") + " | " + Linq.Range(1, 3).SelectMany(x => Linq.Range(0, x)).Join(","))
        """, new[] { "5,3,8 | 2,8 | 5,3,8 | 1,9,2,8", "1,2,3,7,8 | 10,40,90 | 0,0,1,0,1,2" });

    CheckLq("LINQ: Abschluss-Operatoren (First/Last/Any/All/Count/Sum/Min/Max/Average/Aggregate/Contains/ElementAt)", linqHead + """
        class T {
            static Run() {
                var nums = new List([5, 3, 8, 1, 9, 2, 8])
                print(nums.First() + " " + nums.First(x => x > 5) + " " + nums.Last() + " " + nums.ElementAt(2))
                print(nums.Any() + " " + nums.Any(x => x > 8) + " " + nums.All(x => x > 0) + " " + nums.Contains(9) + " " + nums.Count(x => x > 4))
                print(nums.Sum() + " " + nums.Min() + " " + nums.Max() + " " + nums.Aggregate(0, (a, b) => a + b))
                var ps = new List()
                ps.Add(new P("a", 30)); ps.Add(new P("b", 10)); ps.Add(new P("c", 20))
                print(ps.Sum(p => p.price) + " " + ps.Min(p => p.price) + " " + ps.Max(p => p.price) + " " + ps.Average(p => p.price) + " " + ps.FirstOrDefault(p => p.price > 100, "none"))
            }
        }
        T.Run()
        """, new[] { "5 8 8 8", "True True True True 4", "36 1 9 36", "60 10 30 20 none" });

    CheckLq("LINQ: First auf einer leeren Folge wirft LinqEmptyException", linqHead + """
        try { Linq.From([]).First() } catch (e) { print("leer: " + e.message) }
        """, new[] { "leer: The sequence contains no element" });

    CheckLq("LINQ: foreach ueber eine Abfrage, ToArray, Query auf einem Array", linqHead + """
        var arr = Linq.From([3, 1, 2]).OrderBy(x => x).ToArray()
        var s = ""
        foreach (x in Linq.From(arr).Select(x => x * 2)) { s = s + x + " " }
        print(arr.length + " " + s)
        """, new[] { "3 2 4 6 " });

    // ---- Operand stack and exceptions: what the throw site leaves on the stack must not shift the caller
    const string excHead = "class Exception { string message; construct(string message = \"\") { this.message = message } }\n";

    CheckLq("Exception aus einem foreach, im selben try gefangen: keine Operanden-Leichen (catch mit return, Aufrufer mitten im Ausdruck)", excHead + """
        class T {
            static C(class l) { try { foreach (x in l) { throw new Exception("x") } } catch (e) { return 7 } return 0 }
            static D(class l) { foreach (x in l) { try { throw new Exception("y") } catch (e) { return 8 } } return 0 }
            static Run() {
                print(10 + T.C([1, 2]) + 1)
                print(10 + T.D([1, 2]) + 1)
            }
        }
        T.Run()
        """, new[] { "18", "19" });

    CheckLq("Exception aus einer tieferen Funktion mit halb ausgewerteten Ausdruecken, weiter aussen gefangen", excHead + """
        class T {
            static Boom(int n) { return 100 + n + T.Fail() }
            static Fail() { throw new Exception("boom") }
            static Run() {
                var total = 0
                for (var i = 0; i < 3; i = i + 1) {
                    try { total = total + 1 + T.Boom(i) } catch (e) { total = total + 1000 }
                }
                print(total + 5)
            }
        }
        T.Run()
        """, new[] { "3005" });

    CheckLq("return mitten in einem try meldet seinen Handler ab (keine fremde Exception landet in der beendeten Funktion)", excHead + """
        class T {
            static A() { try { return 1 } catch (e) { return 2 } }
            static Run() {
                print(1 + T.A())
                try { T.A(); throw new Exception("spaeter") } catch (e) { print("gefangen " + e.message) }
                print(7 + T.A())
            }
        }
        T.Run()
        """, new[] { "2", "gefangen spaeter", "8" });

    CheckLq("resume() setzt mit den Operanden der Wurfstelle fort (Stack wird beim catch beiseitegelegt und zurueckgespielt)", excHead + """
        class T {
            static Get() { return 10 + (throw new Exception("fehlt")) }
            static Run() {
                try { print(1 + T.Get()) } catch (e) { e.resume(5) }
                var s = 0
                try { foreach (x in [1, 2, 3]) { s = s + x + (throw new Exception("n")) } } catch (e) { e.resume(100) }
                print(s)
            }
        }
        T.Run()
        """, new[] { "16", "306" });

    CheckLq("finally: EINE Kopie fuer alle Wege - normal, Exception, return (auch im catch/foreach), break/continue, Fehler im catch; sieht die Locals", excHead + """
        class T {
            static N() { var x = 1; try { x = x + 1 } finally { print("N fin x=" + x) } return x }
            static P() { var x = 5; try { throw new Exception("p") } finally { print("P fin x=" + x) } }
            static R() { var x = 7; try { return x } finally { print("R fin x=" + x); x = 99 } }
            static RC() { var y = 3; try { throw new Exception("c") } catch (e) { return y + 1 } finally { print("RC fin y=" + y) } }
            static CE() { try { throw new Exception("a") } catch (e) { throw new Exception("b") } finally { print("CE fin") } }
            static RR() { try { try { return 1 } finally { print("inner") } } finally { print("outer") } }
            static RF(class l) { try { foreach (x in l) { return x } } finally { print("RF fin") } return 0 }
            static FR() { try { return 1 } finally { return 2 } }
            static Run() {
                print(1 + T.N() + 1)
                try { T.P() } catch (e) { print("caught " + e.message) }
                print(1 + T.R() + 1)
                print(1 + T.RC() + 1)
                try { T.CE() } catch (e) { print("caught " + e.message) }
                print(10 + T.RR() + 10)
                print(10 + T.RF([4, 5]) + 10)
                print(10 + T.FR() + 10)
                var log = ""
                for (var i = 0; i < 4; i = i + 1) {
                    try {
                        if (i == 1) { continue }
                        if (i == 3) { break }
                        log = log + "b" + i
                    } finally { log = log + "f" + i }
                }
                print(log)
                var log2 = ""
                var n = 0
                while (true) {
                    n = n + 1
                    try {
                        try { if (n == 2) { break } log2 = log2 + "t" + n }
                        finally { log2 = log2 + "i" + n }
                    } finally { log2 = log2 + "o" + n }
                }
                print(log2)
                var log3 = ""
                for (var j = 0; j < 3; j = j + 1) {
                    try { throw new Exception("x") } catch (e) { if (j == 1) { continue } log3 = log3 + "c" + j } finally { log3 = log3 + "f" + j }
                }
                print(log3)
                try { try { throw new Exception("in") } finally { print("f1") } } catch (e) { print("out " + e.message) }
                print(2 * T.N() + 3)
            }
        }
        T.Run()
        """, new[] { "N fin x=2", "4", "P fin x=5", "caught p", "R fin x=7", "9", "RC fin y=3", "6", "CE fin", "caught b", "inner", "outer", "21", "RF fin", "24", "22", "b0f0f1b2f2f3", "t1i1o1i2o2", "c0f0f1c2f2", "f1", "out in", "N fin x=2", "7" });

    CheckLq("finally: zurueckgegebene Objekte ueberleben, resume() durch ein finally, Exception im finally ersetzt die erste", excHead + """
        class Box { int n; construct(int n) { this.n = n } destruct() { print("~Box " + this.n) } }
        class T {
            static Make() { try { var b = new Box(5); return b } finally { print("mk fin") } }
            static Make2() { var keep = new Box(6); try { return keep } finally { print("mk2 fin") } }
            static Resume() { try { return 1 + (throw new Exception("r")) } catch (e) { e.resume(10) } finally { print("res fin") } }
            static Replace() { try { throw new Exception("first") } finally { throw new Exception("second") } }
            static Run() {
                var b = T.Make()
                print(b.n)
                var c = T.Make2()
                print(c.n)
                print(T.Resume())
                try { T.Replace() } catch (e) { print(e.message) }
            }
        }
        T.Run()
        """, new[] { "mk fin", "5", "mk2 fin", "6", "res fin", "11", "second", "~Box 5", "~Box 6" });

    CheckLq("Arrays sind IEnumerable: is of, GetEnumerator, foreach, fluent LINQ direkt auf dem Array (class extends array)", linqHead + """
        class Bag : IEnumerable { GetEnumerator() { var items = [1, 2]; var e = new ListEnumerator(items, 2); items.TakeTo(e); return e } }
        class T {
            static Count(class src) { var n = 0; foreach (x in src) { n = n + 1 } return n }
            static Run() {
                var arr = [5, 3, 8, 1]
                print(arr is of IEnumerable)
                print(new Bag() is of IEnumerable)
                print(5 is of IEnumerable)
                var e = arr.GetEnumerator()
                e.MoveNext()
                print(e.GetCurrent() + " " + T.Count(arr))
                print(arr.Where(x => x > 2).Select(x => x * 10).Join(","))
                print(arr.OrderBy(x => x).ToList().count + " " + arr.Sum() + " " + arr.Count(x => x > 2) + " " + arr.First() + " " + arr.Any(x => x > 7))
                print(Linq.From(new Bag()).Select(x => x + 1).Join(","))
            }
        }
        T.Run()
        """, new[] { "True", "True", "False", "5 4", "50,30,80", "4 17 3 5 True", "2,3" });

    CheckLq("SelectMember/SelectProperty/SelectField: Projektion per Selektor (Feld oder Property / nur Property / nur Feld), Fehler als ReflectionException", linqHead + """
        class Item { string name; int price; int Double { get { return this.price * 2 } } construct(string n, int p) { this.name = n; this.price = p } }
        class T {
            static Run() {
                var items = [new Item("a", 3), new Item("b", 1)]
                print(items.SelectMember(p => p.name).Join(",") + " " + items.SelectMember(p => p.Double).Join(",") + " " + items.SelectField(p => p.price).Join(",") + " " + items.SelectProperty(p => p.Double).Join(","))
                var l = new List(items)
                print(l.SelectField(p => p.name).Join("") + " " + l.OrderBy(p => p.price).SelectMember(p => p.name).Join(""))
                try { items.SelectField(p => p.Double).ToList() } catch (e) { print("1 " + e.message) }
                try { items.SelectProperty(p => p.price).ToList() } catch (e) { print("2 " + e.message) }
                try { items.SelectMember(p => p.nope).ToList() } catch (e) { print("3 " + e.message) }
                try { items.SelectMember(p => p.price + 1) } catch (e) { print("4 " + e.message) }
            }
        }
        T.Run()
        """, new[]
        {
            "a,b 6,2 3,1 6,2", "ab ba",
            "1 'Double' is a property, expected (lambda field<...>): a field",
            "2 'price' is a field, expected (lambda property<...>): a property",
            "3 'nope' is not a member",
            "4 The lambda is not a selector: it needs exactly one parameter, and its body may only be a member chain on it (`c => c.radius`, `p => p.address.city`).",
        });

    CheckLq("## ist ein Synonym fuer !=", """
        var x = 5
        print((1 ## 2) + " " + (2 ## 2) + " " + (true ## false) + " " + ("a" ## "a") + " " + (x ## 5 || x ## 4) + " " + (6 # 3))
        """, new[] { "True False True False True 5" });

    Console.WriteLine(lqFailures == 0 ? "Alle Lambda-/LINQ-Pruefungen bestanden." : $"FEHLER: {lqFailures} Lambda-/LINQ-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Reflection (#import "reflection"): Typ-Beschreibungen, Get/Set/Call/New, Zugriffsregeln, Selektoren
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Reflection ===");
    int rfFailures = 0;

    List<string> RunRf(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var natives = NativeRegistry.CreateDefault();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        fire.Runtime.ReflectionNatives.Register(natives);
        var sources = new List<string> { fire.Standard.Prelude.Source, fire.Standard.ReflectionPrelude.Source, script };
        var program = Parser.ParseMultiple(sources
            .Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                fire.Compiler.RuntimeSession.CreateProjectDirectiveRegistry())).ToList());
        var compiled = Compiler.Compile(program, Resolver.Resolve(program, natives.Names), natives);
        VM.ResetTerminateForTests();
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: mode);
        vm.Run();
        VM.ResetTerminateForTests();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    // Like a packed program: compile, serialise, load again, execute with the packed runtime
    List<string> RunRfPacked(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var linked = new fire.Compiler.Linker().CompileAndLink(new[] { script }, null, null, mode);
        var restored = Packer.Deserialize(MemoryPack.MemoryPackSerializer.Serialize(linked))!;
        VM.ResetTerminateForTests();
        var session = fire.Runtime.Session.Build(restored, mode, args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        session.Run();
        VM.ResetTerminateForTests();
        return lines;
    }

    void CheckRf(string title, string script, string[] expected, VmExecutionMode[]? modes = null, bool packed = false)
    {
        foreach (var mode in modes ?? new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            string[] actual;
            try { actual = (packed ? RunRfPacked(script, mode) : RunRf(script, mode)).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + CompileErrors.Describe(ex) }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) rfFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    var debugRelease = new[] { VmExecutionMode.Debug, VmExecutionMode.Release };

    const string shapes = """
        class Shape {
            string name
            construct(string name) { this.name = name }
            string Describe() { return "shape " + this.name }
        }
        class Circle : Shape {
            float radius
            private int secret
            readonly int id
            float w : mm = 5mm
            construct(float radius) : base("circle") { this.radius = radius; this.secret = 42; this.id = 7 }
            float Diameter { get { return this.radius * 2 } set { this.radius = value / 2 } }
            float Area() { return this.radius * this.radius * 3 }
            Scale(float f, int times) { this.radius = this.radius * f }
            private Hidden() { return "hidden" }
            int PeekSecret() { return Reflect.Get(this, "secret") }
        }

        """;

    CheckRf("Type.Of: Name, Basisklasse, Mitglieder mit Art, Zugriff, Typ, Einheit, readonly, Herkunft", shapes + """
        var t = Type.Of(new Circle(5.0))
        print(t.Name + " : " + t.Base.Name)
        foreach (m in t.All) { print(m.Kind + " " + m.Access + " " + m.TypeName + " " + m.Name + " " + m.ParamCount() + " " + m.DeclaredIn + " " + m.IsReadonly + " " + m.Unit) }
        """, new[]
        {
            "Circle : Shape",
            "field public float radius 0 Circle False ",
            "field private int secret 0 Circle False ",
            "field public int id 0 Circle True ",
            "field public float w 0 Circle False mm",
            "constructor public  Circle 1 Circle False ",
            "property public float Diameter 0 Circle False ",
            "method public float Area 0 Circle False ",
            "method public  Scale 2 Circle False ",
            "method private  Hidden 0 Circle False ",
            "method public int PeekSecret 0 Circle False ",
            "field public string name 0 Shape False ",
            "method public string Describe 0 Shape False ",
        });

    CheckRf("Type: Find/Has/Fields/Properties/Methods/Interfaces, Named und IsSubclassOf", shapes + """
        interface IThing { Ping() }
        class Thing : Circle, IThing { construct() : base(1.0) { } Ping() { return 1 } }
        var t = Type.Of("Thing")
        print(t.Base.Name + " " + t.Interfaces.length + " " + t.Interfaces[0])
        print(t.Fields().count + " " + t.Properties().count + " " + t.Methods().count + " " + t.Constructors().count)
        print(t.Has("radius") + " " + t.Has("nope") + " " + t.Find("Diameter").CanWrite + " " + t.Find("Area").IsMethod())
        print(t.IsSubclassOf(Type.Of("Shape")) + " " + Type.Of("Shape").IsSubclassOf(t) + " " + (Type.Named("Gibts") == undefined))
        """, new[] { "Circle 1 IThing", "5 1 6 1", "True False True True", "True False True" }, debugRelease);

    CheckRf("Get/Set/Call/New: Felder, Properties, geerbte Methoden, Konstruktion", shapes + """
        var c = new Circle(5.0)
        print(Reflect.Get(c, "radius"))
        Reflect.Set(c, "radius", 6.0)
        print(Reflect.Get(c, "Diameter") + " " + Reflect.Has(c, "Area") + " " + Reflect.Has(c, "nope"))
        Reflect.Set(c, "Diameter", 20.0)
        print(c.radius)
        print(Reflect.Call(c, "Area", []) + " " + Reflect.Call(c, "Describe", []))
        Reflect.Call(c, "Scale", [2.0, 1])
        print(c.radius)
        var c2 = Reflect.New("Circle", [2.0])
        print(c2.radius + " " + c2.Describe())
        var m = Type.Of(c).Find("radius")
        m.Set(c, 1.5)
        print(m.Get(c))
        """, new[] { "5", "12 True False", "10", "300 shape circle", "20", "2 shape circle", "1.5" });

    CheckRf("Zugriffsregeln: private/readonly/Einheit gelten auch fuer die Reflection (aus der Klasse selbst ist private erlaubt)", shapes + """
        var c = new Circle(5.0)
        try { Reflect.Get(c, "secret") } catch (e) { print("1 " + e.message) }
        try { Reflect.Call(c, "Hidden", []) } catch (e) { print("2 " + e.message) }
        try { Reflect.Set(c, "id", 9) } catch (e) { print("3 " + e.message) }
        try { Reflect.Set(c, "w", 5.0) } catch (e) { print("4 Einheit") }
        print(c.PeekSecret())
        """, new[]
        {
            "1 Field 'secret' of 'Circle' is private and cannot be accessed from here.",
            "2 'Hidden' of 'Circle' is private and cannot be accessed from here.",
            "3 The field 'id' of 'Circle' is 'readonly' and cannot be assigned.",
            "4 Einheit",
            "42",
        }, debugRelease);

    CheckRf("Fehler sind fangbare ReflectionExceptions; Exceptions aus Getter/Methode laufen zum aeusseren catch", """
        class Exception { string message; construct(string message = "") { this.message = message } }
        class P {
            int n
            int Age { get { throw new Exception("kein Alter") } }
            Boom() { throw new Exception("boom") }
        }
        var p = new P()
        try { Reflect.Get(p, "nope") } catch (e) { print("1 " + e.message) }
        try { Reflect.Set(p, "Age", 1) } catch (e) { print("2 " + e.message) }
        try { Reflect.Call(p, "Nope", []) } catch (e) { print("3 " + e.message) }
        try { Reflect.Get(5, "x") } catch (e) { print("4 " + e.message) }
        try { Reflect.New("Gibts", []) } catch (e) { print("5 " + e.message) }
        try { Reflect.Get(p, "Age") } catch (e) { print("6 " + e.message) }
        try { Reflect.Call(p, "Boom", []) } catch (e) { print("7 " + e.message) }
        print("weiter")
        """, new[]
        {
            "1 'P' has no readable member 'nope'.",
            "2 The property 'Age' of 'P' has no setter (only 'get').",
            "3 'P' has no method 'Nope' with 0 parameter(s).",
            "4 Reflect.Get: expects an object, got: Int.",
            "5 Unknown class 'Gibts'.",
            "6 kein Alter",
            "7 boom",
            "weiter",
        });

    CheckRf("Selektor: lambda member<T> enthaelt die Reflection des gewaehlten Mitglieds (Get/Set/Describe, verschachtelt, durchgereicht)", """
        class Address { string city; construct(string c) { this.city = c } }
        class Person { string name; Address address; construct(string n, Address a) { this.name = n; this.address = a } }
        class W {
            static Show(lambda member<Person> sel, Person p) {
                print(sel.Name + "=" + sel.Get(p) + " " + sel.Describe(p).TypeName + " " + sel.Path.length)
                sel.Set(p, "X")
            }
            static Pass(lambda member<Person> sel, Person p) { W.Show(sel, p) }
        }
        var p = new Person("Ann", new Address("Wien"))
        W.Show(q => q.name, p)
        W.Show(q => q.address.city, p)
        W.Pass(q => q.address.city, p)
        print(p.name + " " + p.address.city)
        """, new[] { "name=Ann string 1", "city=Wien string 2", "city=X string 2", "X X" });

    CheckRf("Selektor: eine Lambda, die keine reine Mitgliedskette ist, wird abgelehnt", """
        class P { string name }
        class W { static Show(lambda member<P> sel, P p) { print(sel.Name) } }
        try { W.Show(q => q.name + "x", new P()) } catch (e) { print("1 " + e.message) }
        try { W.Show(5, new P()) } catch (e) { print("2 " + e.message) }
        """, new[]
        {
            "1 The lambda is not a selector: it needs exactly one parameter, and its body may only be a member chain on it (`c => c.radius`, `p => p.address.city`).",
            "2 A selector ('lambda member<...>' etc.) expects a lambda like `c => c.radius`, got: Int.",
        });

    // packed: metadata and try/catch must survive the serialisation (catch clauses used to be lost)
    CheckRf("Gepacktes Programm: Typ-Metadaten, Zugriffsregeln und try/catch ueberleben die Serialisierung", """
        #import "reflection"
        class Exception { string message; construct(string message = "") { this.message = message } }
        class A { float r; private int s; construct() { this.r = 1.5; this.s = 3 } }
        var t = Type.Of(new A())
        foreach (m in t.All) { print(m.Access + " " + m.TypeName + " " + m.Name) }
        try { Reflect.Get(new A(), "s") } catch (e) { print("privat") }
        try { throw new Exception("x") } catch (e) { print("gefangen") }
        """, new[] { "public float r", "private int s", "public  A", "privat", "gefangen" }, debugRelease, packed: true);

    // ---- probe / silence
    const string probeHead = """
        class Exception { string message; construct(string message = "") { this.message = message } }
        class C {
            int v
            string name
            int Loud { get { return this.v * 2 } set { this.v = value / 2 } }
            construct() { this.v = 1; this.name = "a" }
        }

        """;

    CheckRf("probe changed: implizite Namen, nur bei geaendertem Wert, silence mit Handle", probeHead + """
        var c = new C()
        var h = probe c.v changed { print(name + " " + old + "->" + value + " " + sender.name) }
        c.v = 5
        c.v = 5
        c.v++
        silence h
        c.v = 9
        print(c.v)
        """, new[] { "v 1->5 a", "v 5->6 a", "9" });

    CheckRf("probe changing: ein Handler mit false bricht das Schreiben ab (Ausdrucksform mit Capture)", probeHead + """
        class T {
            static Run() {
                var c = new C()
                var limit = 100
                probe c.v changing (o, n) => n <= limit
                c.v = 50
                c.v = 500
                print(c.v)
                var r = (c.v = 700)
                print(r + " " + c.v)
            }
        }
        T.Run()
        """, new[] { "50", "700 50" });

    CheckRf("probe: Handler-Argumente nach Parameterzahl (0, 1, 2, 3, 4)", probeHead + """
        var c = new C()
        probe c.v changed () => print("0")
        probe c.v changed x => print("1 " + x)
        probe c.v changed (a, b) => print("2 " + a + " " + b)
        probe c.v changed (s, a, b) => print("3 " + s.v + " " + a + " " + b)
        probe c.v changed (s, n, a, b) => print("4 " + n + " " + a + " " + b)
        c.v = 2
        """, new[] { "0", "1 2", "2 1 2", "3 2 1 2", "4 v 1 2" });

    CheckRf("probe obj.*: alle Mitglieder; Properties feuern, die Writes im Setter ebenfalls; silence obj.* entfernt alle", probeHead + """
        var c = new C()
        probe c.* changed (s, n, a, b) => print(n + " " + a + "->" + b)
        c.name = "b"
        c.Loud = 20
        silence c.*
        c.name = "c"
        c.v = 77
        print("still")
        """, new[] { "name a->b", "v 1->10", "Loud 2->20", "still" });

    CheckRf("probe: Pfad (a.b.c), nur das Objekt mit Probe ist betroffen, eine schon gecachte Schreibstelle sieht die Probe", probeHead + """
        class Holder { C c; construct() { this.c = new C() } }
        class T { static Bump(C c) { c.v = c.v + 1 } }
        var a = new C()
        var b = new C()
        T.Bump(a); T.Bump(b); T.Bump(a)
        probe a.v changed (o, n) => print("a " + o + "->" + n)
        T.Bump(a); T.Bump(b); T.Bump(a)
        var h = new Holder()
        probe h.c.v changing (o, n) => n < 3
        h.c.v = 2
        h.c.v = 9
        print(h.c.v + " " + b.v)
        """, new[] { "a 3->4", "a 4->5", "2 3" });

    CheckRf("probe: ein Handler, der dasselbe Mitglied schreibt, loest sich nicht selbst aus", probeHead + """
        var c = new C()
        probe c.v changed { c.v = 0 }
        c.v = 3
        print(c.v)
        """, new[] { "0" });

    CheckRf("probe: Exceptions im Handler laufen zum Schreiber (changing bricht das Schreiben ab, changed kommt nach dem Schreiben)", probeHead + """
        var c = new C()
        probe c.v changing (o, n) => { throw new Exception("nein") }
        try { c.v = 5 } catch (e) { print("1 " + e.message) }
        print(c.v)
        silence c
        probe c.v changed (o, n) => { throw new Exception("danach") }
        try { c.v = 6 } catch (e) { print("2 " + e.message) }
        print(c.v)
        print("weiter")
        """, new[] { "1 nein", "1", "2 danach", "6", "weiter" });

    CheckRf("probe/silence bleiben als Variablennamen nutzbar", """
        var probe = 5
        var silence = probe + 1
        print(probe + " " + silence)
        """, new[] { "5 6" });

    CheckRf("Reflect.Probe/Silence, Selector.Probe, Handle und Fehler", probeHead + """
        class W { static Watch(lambda member<C> s, C c) { return s.Probe(c, "changing", (o, n) => n < 10) } }
        var c = new C()
        var h = Reflect.Probe(c, "v", "changed", (o, n) => print("r " + o + " " + n))
        c.v = 4
        Reflect.SilenceHandle(h)
        c.v = 5
        W.Watch(x => x.v, c)
        c.v = 50
        print(c.v)
        Reflect.SilenceAll(c)
        c.v = 50
        print(c.v)
        try { Reflect.Probe(c, "nope", "changed", () => 1) } catch (e) { print(e.message) }
        try { Reflect.Probe(c, "v", "gestern", () => 1) } catch (e) { print(e.message) }
        try { Reflect.Probe(c, "v", "changed", (a, b, c, d, e) => 1) } catch (e) { print(e.message) }
        """, new[]
        {
            "r 1 4", "5", "50",
            "'C' has no member 'nope' - no probe can be registered there.",
            "The kind of a probe is \"changed\" or \"changing\", got: \"gestern\".",
            "The handler of a probe may have at most 4 parameters (object, name, old, new), it has 5.",
        }, debugRelease);

    CheckRf("Selektor-Arten: field, property, member (Feld oder Property), method (nur Methode), selector (alles)", """
        class C { int v; int P { get { return 7 } } Twice(int n) { return n * 2 } construct() { this.v = 1 } }
        class W {
            static F(lambda field<C> s, C c) { return s.Name + "=" + s.Get(c) }
            static P(lambda property<C> s, C c) { return s.Name + "=" + s.Get(c) }
            static M(lambda member<C> s, C c) { return s.Name + "=" + s.Get(c) }
            static S(lambda selector<C> s, C c) { return s.Name + ":" + s.ActualKind(c) }
            static Call(lambda selector<> s, C c) { return s.Call(c, [21]) }
            static Meth(lambda method<C> s, C c) { return s.Name + "->" + s.Call(c, [4]) }
            static GetOnly(lambda selector<C> s, C c) { return s.Get(c) }
        }
        var c = new C()
        print(W.F(x => x.v, c) + " " + W.P(x => x.P, c) + " " + W.M(x => x.v, c) + " " + W.M(x => x.P, c))
        print(W.S(x => x.v, c) + " " + W.S(x => x.P, c) + " " + W.S(x => x.Twice, c) + " " + W.Call(x => x.Twice, c))
        try { W.F(x => x.P, c) } catch (e) { print("1 " + e.message) }
        try { W.P(x => x.v, c) } catch (e) { print("2 " + e.message) }
        try { W.M(x => x.Twice, c) } catch (e) { print("3 " + e.message) }
        try { W.F(x => x.nope, c) } catch (e) { print("4 " + e.message) }
        try { W.GetOnly(x => x.Twice, c) } catch (e) { print("5 " + e.message) }
        try { W.Call(x => x.v, c) } catch (e) { print("6 " + e.message) }
        print(W.Meth(x => x.Twice, c))
        try { W.Meth(x => x.v, c) } catch (e) { print("7 " + e.message) }
        try { W.Meth(x => x.P, c) } catch (e) { print("8 " + e.message) }
        """, new[]
        {
            "v=1 P=7 v=1 P=7",
            "v:field P:property Twice:method 42",
            "1 'P' is a property, expected (lambda field<...>): a field",
            "2 'v' is a field, expected (lambda property<...>): a property",
            "3 'Twice' is a method, expected (lambda member<...>): a field or a property",
            "4 'nope' is not a member",
            "5 'Twice' is a method - Call(obj, args) calls it",
            "6 'v' is not a method",
            "Twice->8",
            "7 'v' is a field, expected (lambda method<...>): a method",
            "8 'P' is a property, expected (lambda method<...>): a method",
        });

    Console.WriteLine(rfFailures == 0 ? "Alle Reflection-Pruefungen bestanden." : $"FEHLER: {rfFailures} Reflection-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------------------------------------------------------
// Scope management: return from nested blocks, reuse of scopes (pooling)
// ---------------------------------------------------------------------------------------------------------------------------
{
    Console.WriteLine("=== Scope-Verwaltung (return aus verschachtelten Bloecken, Wiederverwendung) ===");
    int scFailures = 0;

    List<string> RunSc(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var natives = NativeRegistry.CreateDefault();
        natives.Register("print", args =>
        {
            var shown = VM.StringifyForPrint(args);
            if (shown != null) lock (lines) lines.Add(shown[0].ToString());
            return Value.MakeUndefined();
        });
        natives.RegisterBaseTypeNatives();
        var sources = new List<string> { fire.Standard.Prelude.Source, script };
        var program = Parser.ParseMultiple(sources
            .Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                fire.Compiler.RuntimeSession.CreateProjectDirectiveRegistry())).ToList());
        var compiled = Compiler.Compile(program, Resolver.Resolve(program, natives.Names), natives);
        VM.ResetTerminateForTests();
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: mode);
        vm.Run();
        VM.ResetTerminateForTests();
        if (vm.UnhandledException != null)
            lines.Add("UNBEHANDELT: " + new UncaughtScriptException(vm.UnhandledException).Message);
        return lines;
    }

    void CheckSc(string title, string script, string[] expected)
    {
        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            string[] actual;
            try { actual = RunSc(script, mode).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + CompileErrors.Describe(ex) }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) scFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    // like CheckSc, but not in the Performance mode (it does not check for destroyed objects)
    void CheckScChecked(string title, string script, string[] expected)
    {
        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release })
        {
            string[] actual;
            try { actual = RunSc(script, mode).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + CompileErrors.Describe(ex) }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) scFailures++;
            Console.WriteLine(ok ? $"OK: {title} [{mode}]" : $"FEHLER: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    const string scHead = """
        class D {
            string n
            construct(string n) { this.n = n }
            destruct() { print("~" + this.n) }
        }
        """;

    CheckScChecked("Zerstoerte Objekte: Benutzung nach dem Zerstoerungsstapel ist eine DestroyedException, im Stapel noch erlaubt; return nimmt den Baum der Locals mit", """
        class D {
            string n
            D next
            construct(string n) { this.n = n }
            destruct() { print("~" + this.n) }
        }
        class W {
            D target
            construct(D target) { this.target = target }
            destruct() { print("~W sees " + this.target.n) }
        }
        class T {
            static Dead() {
                var a = new D("a")
                return a.n
            }
            // the destructor of w uses d, which was destroyed before it in the same scope: allowed until the scope is gone
            static Batch() {
                var d = new D("d")
                var w = new W(d)
            }
            // a returned list takes its elements along
            static Items() {
                var list = new List()
                for (var i = 0; i < 3; i = i + 1) { var x = new D("i" + i); x.TakeTo(list); list.Add(x) }
                return list
            }
            // by reference only: everything local travels with the returned object
            static Ring() {
                var a = new D("ra")
                var b = new D("rb")
                a.next = b
                b.next = a
                return a
            }
            // not returned: gone
            static Lost() {
                var keep = new D("lost")
                return 1
            }
            // taken out of an owner that dies with the scope
            static Inner() {
                var outer = new D("outer")
                outer.next = new D("inner")
                outer.next.TakeTo(outer)
                return outer.next
            }
        }
        var x = new D("x")
        delete x
        try { print(x.n) } catch (DestroyedException e) { print("dead: " + e.message) }
        try { x.next = x } catch (DestroyedException e) { print("dead set") }
        T.Batch()
        print("batch done")
        var items = T.Items()
        print(items.count + " " + items[2].n)
        var ring = T.Ring()
        print(ring.next.next.n)
        T.Lost()
        print(T.Inner().n)
        print("end")
        """, new[] { "~x", "dead: Access to a destroyed object.", "dead set", "~d", "~W sees d", "batch done", "3 i2", "ra", "~lost", "~outer", "inner", "end", "~i0", "~i1", "~i2", "~ra", "~rb", "~inner" });

    CheckScChecked("Takes: This, Children, Locals, All bei TakeUpwards/TakeTo/TakeGlobal und fuer Arrays", """
        class N {
            string name
            N next
            N other
            construct(string name) { this.name = name }
            destruct() { print("~" + this.name) }
        }
        class F {
            // This: the child stays behind and dies with the function
            static UpThis(holder) {
                var p = new N("p1")
                var q = new N("q1")
                p.next = q
                p.TakeUpwards()
                holder.next = p
            }
            // Locals: the child travels along (to the object that points to it)
            static UpLocals(holder) {
                var p = new N("p2")
                var q = new N("q2")
                p.next = q
                p.TakeUpwards(Takes.Locals)
                holder.next = p
            }
            // Children: what the fields point to directly, not what those point to
            static UpChildren(holder) {
                var p = new N("p3")
                var q = new N("q3")
                var r = new N("r3")
                p.next = q
                q.next = r
                p.TakeUpwards(Takes.Children)
                holder.next = p
            }
            // All: also what is owned by somebody else
            static TakeAll(holder, foreign) {
                var p = new N("p4")
                p.other = foreign
                p.TakeUpwards(Takes.All)
                holder.next = p
            }
            // TakeTo with a mode, and TakeGlobal
            static ToObject(holder) {
                var p = new N("p5")
                var q = new N("q5")
                p.next = q
                p.TakeTo(holder, Takes.Locals)
            }
            static Global() {
                var p = new N("p6")
                var q = new N("q6")
                p.next = q
                p.TakeGlobal(Takes.Children)
                return 0
            }
            // an array travels with the objects it holds
            static Arr() {
                var a = [new N("a1"), new N("a2")]
                a.TakeUpwards(Takes.Locals)
                return a
            }
        }
        var h = new N("h")
        var foreign = new N("foreign")
        F.UpThis(h)
        print("1 " + h.next.name)
        try { print(h.next.next.name) } catch (DestroyedException e) { print("q1 dead") }
        F.UpLocals(h)
        print("2 " + h.next.next.name)
        F.UpChildren(h)
        print("3 " + h.next.next.name)
        try { print(h.next.next.next.name) } catch (DestroyedException e) { print("r3 dead") }
        F.TakeAll(h, foreign)
        print("4 " + h.next.other.name)
        F.ToObject(h)
        print("5 " + h.next.name)
        F.Global()
        print("6")
        var arr = F.Arr()
        print("7 " + arr[0].name + arr[1].name)
        print("end")
        """, new[] { "~q1", "1 p1", "q1 dead", "2 q2", "~r3", "3 q3", "r3 dead", "4 foreign", "5 p4", "6", "7 a1a2", "end", "~h", "~p5", "~q5", "~p1", "~p2", "~q2", "~p3", "~q3", "~p4", "~foreign", "~p6", "~q6", "~a1", "~a2" });

    CheckScChecked("try x.Take...: verschiebt nur der Besitzer (bool); Takes.Children nimmt die Items von Array und IEnumerable mit", """
        class Item { string n
          construct(string n) { this.n = n }
          destruct() { print("~" + this.n) } }
        class Holder { var kept
          construct() { }
          // x is the result of a call passed straight on: it belongs to this method's scope, so the Holder may take it
          Adopt(x) { return try x.TakeTo(this) }
          AdoptNew(x) { return try x.TakeTo(this) }
          // the thing is owned by this object: only the owner moves it
          Release() { return try this.kept.TakeLocal() }
        }
        class F { static Make(string n) { return new Item(n) } }
        var h = new Holder()
        print(h.Adopt(F.Make("fresh")))
        var mine = new Item("mine")
        print(h.AdoptNew(mine))
        print(h.AdoptNew(new Item("tmp")))
        // children of a list: the items go to the list
        class Bag { var list
          construct() { this.list = new List() } }
        var items = new List()
        var a = new Item("la")
        var b = new Item("lb")
        items.Add(a)
        items.Add(b)
        var arr = [new Item("a1"), new Item("a2")]
        class T { static Run(items, arr) {
            items.TakeLocal(Takes.Children)
            arr.TakeLocal(Takes.Children)
            return 0
        } }
        T.Run(items, arr)
        print("end")
        """, new[] { "True", "False", "False", "~la", "~lb", "~a1", "~a2", "end", "~fresh", "~mine", "~tmp" });

    CheckScChecked("TakeTo(list, Takes), TakeLocal", """
        class Item { string n
          construct(string n) { this.n = n }
          destruct() { print("~" + this.n) } }
        class F {
          static Fill() {
            var list = new List()
            for (var i = 0; i < 3; i++) { var it = new Item("i" + i); it.TakeTo(list); list.Add(it) }
            return list
          }
          static FillChildren() {
            var list = new List()
            var a = new Item("c1")
            var b = new Item("c2")
            list.Add(a)
            list.Add(b)
            a.TakeTo(list, Takes.Children)
            return list
          }
          static Local(holder) {
            var x = new Item("x")
            x.TakeLocal()
            print(try x.TakeLocal())
            return 0
          }
        }
        var l = F.Fill()
        print(l.count + " " + l[2].n)
        var l2 = F.FillChildren()
        print(l2.count)
        F.Local(0)
        print("end")
        """, new[] { "3 i2", "2", "True", "~x", "end", "~i0", "~i1", "~i2", "~c1", "~c2" });

    CheckScChecked("take: Argument, Feld, Array-Element, Variable; zerstoertes Objekt", """
        class Item { string n
          construct(string n) { this.n = n }
          destruct() { print("~" + this.n) } }
        class Box { Item slot
          items = [new Item("x0")]
          Put(x) { x.TakeTo(this); print("put " + x.n) }
          destruct() { print("~box") } }
        class T {
          static Eat(x) { var mine = new Item("mine"); print("eat " + x.n) }
          static Keep(x, b) { x.TakeTo(b); print("kept") }
          static Drop(x, b) { print(try x.TakeTo(b)) }
        }
        var a = new Item("a")
        T.Eat(take a)
        print("1")
        var box = new Box()
        var b = new Item("b")
        T.Keep(take b, box)
        print("2")
        var c = new Item("c")
        box.slot = take c
        var d = new Item("d")
        T.Drop(d, box)
        T.Drop(take d, box)
        print("3")
        {
          var inner = new Item("inner")
          var out = new Item("out")
          var x
          x = take out
          box.items[0] = take inner
        }
        print("4")
        var e = new Item("e")
        var f = take e
        print("5")
        try { print(a.n) } catch (DestroyedException ex) { print("dead a") }
        try { T.Eat(take a) } catch (DestroyedException ex) { print("dead take") }
        print("end")
        """, new[] { "eat a","~mine","~a","1","~x0","kept","2","False","True","3","~out","4","5","dead a","dead take","end","~box","~b","~c","~d","~inner","~e" });

    CheckScChecked("Ein Array besitzt, was return und Takes mitnehmen", """
        class Item { string n
          var arr
          construct(string n) { this.n = n }
          destruct() { print("~" + this.n) } }
        class F {
          static Make() {
            var a = [new Item("a1"), new Item("a2")]
            return a
          }
          static Inner() {
            var holder = new Item("holder")
            var a = [new Item("b1")]
            holder.arr = a
            a.TakeTo(holder)
            return a
          }
        }
        var arr = F.Make()
        arr.TakeGlobal()
        class G { static Run() { var x = F.Make(); print(x.length); return 0 } }
        G.Run()
        print("after G")
        var inner = F.Inner()
        print(inner[0].n)
        delete arr
        print("deleted")
        print("end")
        """, new[] { "2", "~a1", "~a2", "after G", "~holder", "b1", "~a1", "~a2", "deleted", "end", "~b1" });

    CheckScChecked("Ein weitergereichtes Argument stirbt nach dem Aufruf, nach den Locals des Aufgerufenen", """
        class D { string n
          construct(string n) { this.n = n }
          destruct() { print("~" + this.n) } }
        class W { D d
          construct(D d) { this.d = d } }
        class F { static Make() { return new D("arg") }
          static Use(D x) { var local = new D("local"); print("in Use") }
          static Pass(D x) { return x } }
        F.Use(F.Make())
        print("after Use")
        var kept = F.Pass(F.Make())
        print("kept " + kept.n)
        print("end")
        """, new[] { "in Use", "~local", "~arg", "after Use", "kept arg", "end", "~arg" });

    CheckSc("return aus verschachtelten Bloecken zerstoert die Objekte ALLER verlassenen Scopes (innerster zuerst)", scHead + """
        class T {
            static F() {
                var a = new D("a")
                if (true) {
                    var b = new D("b")
                    if (true) {
                        var c = new D("c")
                        return 1
                    }
                }
                return 0
            }
        }
        print("vor")
        T.F()
        print("nach")
        """, new[] { "vor", "~c", "~b", "~a", "nach" });

    CheckSc("return eines Objekts aus einem aeusseren Block: es geht an den Aufrufer, die uebrigen werden zerstoert", scHead + """
        class T {
            static Make() {
                var keep = new D("k")
                if (true) {
                    var tmp = new D("t")
                    if (true) { return keep }
                }
                return keep
            }
            static Run() {
                var x = T.Make()
                print("got " + x.n)
            }
        }
        T.Run()
        print("ende")
        """, new[] { "~t", "got k", "~k", "ende" });

    CheckSc("return mitten in for/foreach/while: Objekte der Schleifenkoerper und der Schleifenvariablen werden zerstoert", scHead + """
        class T {
            static FindFor() {
                var outer = new D("outer")
                for (var i = 0; i < 5; i = i + 1) {
                    var o = new D("f" + i)
                    if (i == 1) { return i }
                }
                return -1
            }
            static FindForeach() {
                var items = [1, 2, 3]
                foreach (x in items) {
                    var o = new D("e" + x)
                    if (x == 2) { return x }
                }
                return -1
            }
            static FindWhile() {
                var k = 0
                while (true) {
                    var o = new D("w" + k)
                    if (k == 1) { return k }
                    k = k + 1
                }
            }
        }
        print(T.FindFor())
        print(T.FindForeach())
        print(T.FindWhile())
        """, new[] { "~f0", "~f1", "~outer", "1", "~e1", "~e2", "2", "~w0", "~w1", "1" });

    CheckSc("Exception: die Basisklasse des Prelude (message, eigene Ableitungen mit und ohne base(...), Standardmeldung leer)", """
        class NotFound : Exception {
            construct(string what) : base(what + " not found") { }
        }
        class Quiet : Exception { }
        try { throw new NotFound("key") } catch (NotFound e) { print(e.message) }
        try { throw new Exception("plain") } catch (Exception e) { print(e.message) }
        try { throw new Quiet() } catch (Quiet e) { print("[" + e.message + "]") }
        var q = new Quiet()
        print(q is of Exception)
        """, new[] { "key not found", "plain", "[]", "True" });

    CheckSc("Exception: eine eigene Klasse Exception des Programms ersetzt die des Prelude (die Ableitungen bekommen ihre Basis)", """
        class Exception {
            string message
            construct(string message) { this.message = message + "!" }
        }
        class MyErr : Exception {
            construct(string m) : base(m) { }
        }
        try { throw new MyErr("x") } catch (MyErr e) { print(e.message) }
        """, new[] { "x!" });

    CheckScChecked("Exception: die Fehler der Laufzeit sind Exceptions (IndexOutOfBounds, Zugriff auf Zerstoertes)", """
        class Box { int v }
        var a = [1]
        try { print(a[5]) } catch (Exception e) { print(e.message) }
        try { print(a[5]) } catch (IndexOutOfBoundsException e) { print(e.index + "/" + e.length) }
        var b = new Box()
        delete b
        try { print(b.v) } catch (DestroyedException e) { print("destroyed: " + e.message) }
        """, new[] { "Array index 5 out of range (length 1).", "5/1", "destroyed: Access to a destroyed object." });

    CheckSc("return im try/catch/finally in verschachtelten Bloecken: jedes Objekt genau einmal zerstoert", scHead + "class Exception { string message; construct(string message = \"\") { this.message = message } }\n" + """
        class T {
            static F() {
                var a = new D("a")
                try {
                    var b = new D("b")
                    if (true) {
                        var c = new D("c")
                        return 1
                    }
                } finally { print("fin") }
                return 0
            }
            static G() {
                var a = new D("ga")
                try {
                    var b = new D("gb")
                    throw new Exception("x")
                } catch (e) {
                    var c = new D("gc")
                    if (true) { return 2 }
                }
                return 0
            }
        }
        print(T.F())
        print(T.G())
        """, new[] { "~c", "~b", "fin", "~a", "1", "~gb", "~gc", "~ga", "2" });

    // ---- Reuse of scopes: nothing may point to a scope that is about to belong to another block ----

    CheckSc("Pointer auf eine Lokale ueberlebt das Verlassen der Funktion/des Blocks, auch wenn danach viele Scopes wiederverwendet werden", """
        class T {
            static Ptr(int v) { var x = v; unsafe { var p = &x; return p } }
            static Busy(int n) { var s = 0; for (var i = 0; i < n; i = i + 1) { var t = i * 3; s = s + T.Add(t, 1) } return s }
            static Add(int a, int b) { var r = a + b; return r }
        }
        unsafe {
            var p1 = T.Ptr(11)
            var p2 = T.Ptr(22)
            var ptrs = [p1, p1, p1]
            for (var i = 0; i < 3; i = i + 1) { var v = i * 10; ptrs[i] = &v }
            print(T.Busy(50))
            print(*p1 + " " + *p2)
            print(*ptrs[0] + " " + *ptrs[1] + " " + *ptrs[2])
            *p1 = 99
            *ptrs[1] = 77
            print(T.Busy(10))
            print(*p1 + " " + *p2 + " " + *ptrs[0] + " " + *ptrs[1] + " " + *ptrs[2])
        }
        """, new[] { "3725", "11 22", "0 10 20", "145", "99 22 0 77 20" });

    CheckSc("Rekursion, Parameter, Lokale und Lambda-Captures bleiben je Aufruf unabhaengig (wiederverwendete Scopes sind sauber)", """
        class T {
            static Fib(int n) { if (n < 2) { return n } var a = T.Fib(n - 1); var b = T.Fib(n - 2); return a + b }
            static Even(int n) { if (n == 0) { return true } return T.Odd(n - 1) }
            static Odd(int n) { if (n == 0) { return false } return T.Even(n - 1) }
            static MakeAdder(int n) { var k = n * 2; return x => x + k }
            static Sum(int n) { var local = [n]; if (n == 0) { return 0 } return local[0] + T.Sum(n - 1) }
            static Locals() { var a; var b; var c = 3; return "" + a + "," + b + "," + c }
        }
        print(T.Fib(15))
        print(T.Even(10) + " " + T.Odd(10))
        var f = T.MakeAdder(1)
        var g = T.MakeAdder(10)
        print(T.Fib(10))
        print(f(1) + " " + g(1))
        print(T.Sum(100))
        print(T.Locals())
        print(T.Locals())
        """, new[] { "610", "True False", "55", "3 21", "5050", "undefined,undefined,3", "undefined,undefined,3" });

    CheckSc("Exceptions durch viele Aufrufe/Bloecke: danach arbeiten die wiederverwendeten Scopes unveraendert weiter", "class Exception { string message; construct(string message = \"\") { this.message = message } }\n" + """
        class T {
            static Deep(int n) { var a = n * 2; if (n == 0) { throw new Exception("bottom") } var r = T.Deep(n - 1); return r + a }
            static Run() {
                var total = 0
                for (var i = 0; i < 4; i = i + 1) {
                    var local = i + 100
                    try { T.Deep(3) } catch (e) { total = total + local }
                }
                print(total)
                var sum = 0
                for (var j = 0; j < 3; j = j + 1) { var w = j; if (w > 0) { var z = w * 2; sum = sum + z } }
                print(sum)
            }
        }
        T.Run()
        """, new[] { "406", "6" });

    CheckSc("Objekte in Schleifenkoerpern werden je Durchlauf zerstoert, Bloecke ohne Objekte daneben bleiben unberuehrt", scHead + """
        class T {
            static Run() {
                var n = 0
                for (var i = 0; i < 3; i = i + 1) {
                    if (i % 2 == 0) { var o = new D("o" + i); n = n + 1 } else { var k = i * 10; n = n + k }
                }
                print(n)
            }
        }
        T.Run()
        """, new[] { "~o0", "~o2", "12" });

    // ---- Object creation: field pre-assignment without a call, ownership without lists, destruction ----

    CheckSc("Feld-Vorbelegung: Konstanten, fehlende Initialisierer (undefined), Ausdruecke und Initialisierer mit this", """
        class A {
            int a = 3
            string s = "q"
            bool b = true
            float f = 1.5
            int none
            int c = 2 + 3
            int d = this.a * 2
            string t
            int neg = -4
        }
        var o = new A()
        print(o.a + " " + o.s + " " + o.b + " " + o.f + " " + o.c + " " + o.d + " " + o.neg)
        print(o.none == undefined)
        print(o.t == undefined)
        """, new[] { "3 q True 1.5 5 6 -4", "True", "True" });

    CheckSc("Feld-Vorbelegung mit Basisklasse: was der Basis-Konstruktor in ein Feld der abgeleiteten Klasse schreibt, wird von dessen Initialisierer wie bisher ueberschrieben", """
        class B {
            construct() { this.Setup() }
            Setup() { }
        }
        class D : B {
            int x
            int y = 5
            string s = "init"
            int z
            Setup() { this.x = 99; this.y = 7; this.z = 8 }
        }
        var d = new D()
        print((d.x == undefined) + " " + d.y + " " + d.s + " " + (d.z == undefined))
        """, new[] { "True 5 init True" });

    CheckSc("Feld-Initialisierer mit Aufruf setzt ein spaeteres Feld ohne Initialisierer zurueck (wie bisher)", """
        class A {
            int a = this.Init()
            int b
            Init() { this.b = 41; return 1 }
        }
        var o = new A()
        print(o.a + " " + (o.b == undefined))
        """, new[] { "1 True" });

    CheckSc("Objekte einer Scope werden in der Reihenfolge ihrer Erzeugung zerstoert; ein Objekt mit eigenen Kindern erst selbst, dann die Kinder", scHead + """
        class P {
            D k1 = new D("k1")
            D k2 = new D("k2")
            D k3 = new D("k3")
            destruct() { print("~P") }
        }
        class T {
            static Run() {
                var x = new D("1")
                var y = new D("2")
                var p = new P()
                var z = new D("3")
                print("ende")
            }
        }
        T.Run()
        """, new[] { "ende", "~1", "~2", "~P", "~k1", "~k2", "~k3", "~3" });

    CheckSc("TakeUpwards haengt ein Objekt an die umgebende Scope (hier die der Schleife): es ueberlebt den Block, nicht die Schleife", scHead + """
        class T {
            static Run() {
                for (var i = 0; i < 3; i = i + 1) {
                    var a = new D("a" + i)
                    var b = new D("b" + i)
                    if (i == 1) { b.TakeUpwards() }
                }
                print("nach der Schleife")
            }
        }
        T.Run()
        print("ende")
        """, new[] { "~a0", "~b0", "~a1", "~a2", "~b2", "~b1", "nach der Schleife", "ende" });

    CheckSc("Zuweisung an eine aeussere Variable schiebt den Besitz bis in die Funktions-Scope (nicht aus der Funktion hinaus)", scHead + """
        class T {
            static Run() {
                var keep
                for (var i = 0; i < 3; i = i + 1) {
                    var a = new D("a" + i)
                    var b = new D("b" + i)
                    if (i == 1) { keep = b }
                }
                print("nach der Schleife " + keep.n)
                var u = 0
                for (var j = 0; j < 3; j = j + 1) { var w = j; u = u + w }
                print(u + " " + keep.n)
            }
        }
        T.Run()
        print("ende")
        """, new[] { "~a0", "~b0", "~a1", "~a2", "~b2", "nach der Schleife b1", "3 b1", "~b1", "ende" });

    CheckSc("Destruktoren in Schleifen mit gemischten Bloecken (mit/ohne Objekte) und verschachtelten Aufrufen", scHead + """
        class T {
            static Make(int i) { var d = new D("m" + i); return d }
            static Run() {
                var n = 0
                for (var i = 0; i < 3; i = i + 1) {
                    var d = T.Make(i)
                    if (i == 1) { var e = new D("e" + i); n = n + 1 }
                    n = n + 10
                }
                print(n)
            }
        }
        T.Run()
        """, new[] { "~m0", "~e1", "~m1", "~m2", "31" });

    // ---- Command/ICommand of the standard prelude, generic base classes and interfaces, interfaces as parameter type ----

    CheckSc("Command, Command<T>: Lambda als Rumpf, Konstruktor mit Lambda, ohne Lambda geschieht nichts", """
        var a = new Command(() => 5)
        print(a.Execute())
        var b = new Command<int>(x => x + 1)
        print(b.Execute(9))
        var e = new Command<string>()
        e.Command = s => s + "!"
        print(e.Execute("hi"))
        print(new Command().Execute())
        print(new Command<int>().Execute(1))
        """, new[] { "5", "10", "hi!", "undefined", "undefined" });

    CheckSc("class X : Command<T> - eine generische Basisklasse mit Typ-Argumenten, Execute ueberschreiben, Lambda erben", """
        class Twice : Command<int> {
            Execute(int context) { return context * 2 }
        }
        class Plus : Command<int> {
            construct(int n) { this.Command = x => x + n }
        }
        print(new Twice().Execute(4))
        print(new Plus(10).Execute(4))
        var c = new Command<int>()
        print(c is of Command)
        """, new[] { "8", "14", "False" });

    CheckSc("Generisches Interface neben dem nicht-generischen gleichen Namens, Implementierung wird geprueft", """
        interface IBox { Open() }
        interface IBox<T> { Open(T key) }
        class Plain : IBox { Open() { return "auf" } }
        class Locked<T> : IBox<T> { Open(T key) { return "mit " + key } }
        class Tagged : Locked<int> { }
        print(new Plain().Open())
        print(new Locked<int>().Open(7))
        print(new Tagged().Open(8))
        """, new[] { "auf", "mit 7", "mit 8" });

    CheckSc("Interfaces als Typ eines Parameters", """
        interface IShape { Area() }
        class Sq : IShape { Area() { return 4 } }
        class User { static Use(IShape s) { return s.Area() } }
        print(User.Use(new Sq()))
        """, new[] { "4" });

    CheckSc("Einheiten: gleiche Dimension, andere Skalierung wird implizit umgerechnet (ohne ':'), ints bleiben ints", """
        print(500mm + 2m)
        print(2m + 500mm)
        print(2m - 500mm)
        print(1m > 500mm)
        print(5mm < 1m)
        print(1500mm % 1m)
        var x = 3m
        x = x + 250cm
        print(x)
        print((2m + 500mm) / 3)
        """, new[] { "2500mm", "2500mm", "1500mm", "True", "True", "500mm", "550cm", "833mm" });

    CheckSc("Einheiten: Floats bleiben Floats und behalten die Einheit des linken Operanden", """
        print(1.5mm + 1m)
        print(2.5m + 250mm)
        print((1.5mm + 1m) / 2)
        """, new[] { "1001.5mm", "2.75m", "500.75mm" });

    CheckSc("Einheiten: bei Ueberlauf wird die groebere Einheit Ziel, das Komma faellt weg", """
        print(1500mm + 900000000000000000m)
        print(900000000000000000m + 1500mm)
        """, new[] { "900000000000000001m", "900000000000000001m" });

    CheckSc("Einheiten: verschiedene Dimensionen bleiben ein Fehler", """
        print(5mm + 2kg)
        """, new[] { "AUSNAHME: Incompatible units: 'mm' cannot be converted to 'kg'." });

    Console.WriteLine(scFailures == 0 ? "Alle Scope-Pruefungen bestanden." : $"FEHLER: {scFailures} Scope-Pruefung(en) fehlgeschlagen.");
}

static int CountOccurrences(string haystack, string needle)
{
    int count = 0, idx = 0;
    while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) != -1)
    {
        count++;
        idx += needle.Length;
    }
    return count;
}

// ---------------------------------------------------------------------------------------------------------------------------
// Editor help: anchors for headings (links like "File.md#section") and the bundled "First Steps.md"
// ---------------------------------------------------------------------------------------------------------------------------
{
    Console.WriteLine("=== Markdown-Anker und First Steps ===");
    int mdFailures = 0;
    void CheckMd(string title, string actual, string expected)
    {
        bool ok = actual == expected;
        if (!ok) mdFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}\n  erwartet: {expected}\n  erhalten: {actual}");
    }

    CheckMd("Anker: Kleinbuchstaben, Leerzeichen zu Bindestrichen, Satzzeichen weg", fire.Editor.MdAnchors.Slug("First Steps!"), "first-steps");
    CheckMd("Anker: Kommas und Zahlen", fire.Editor.MdAnchors.Slug("Variables, types and 2 units"), "variables-types-and-2-units");
    var usedAnchors = new HashSet<string>();
    CheckMd("Anker: erste Ueberschrift", fire.Editor.MdAnchors.Unique("Setup", usedAnchors), "setup");
    CheckMd("Anker: zweite gleichnamige Ueberschrift bekommt -1", fire.Editor.MdAnchors.Unique("Setup", usedAnchors), "setup-1");
    CheckMd("Anker: dritte gleichnamige Ueberschrift bekommt -2", fire.Editor.MdAnchors.Unique("Setup", usedAnchors), "setup-2");

    // Every link "(#anchor)" of the bundled help pages must point to a heading, every link to another page to an existing file.
    string helpDir = Path.Combine(Path.GetDirectoryName(GetTestDataDir())!, "..", "fire.Editor", "Help");
    foreach (var helpFile in new[] { "First Steps.md", "Embedding.md" })
    {
        string helpPath = Path.Combine(helpDir, helpFile);
        if (File.Exists(helpPath))
        {
            string helpText = File.ReadAllText(helpPath);
            var anchors = new HashSet<string>();
            foreach (var block in fire.Editor.MarkdownParser.Parse(helpText))
                if (block is fire.Editor.MdHeading h)
                    fire.Editor.MdAnchors.Unique(fire.Editor.MarkdownParser.PlainText(h.Content), anchors);
            var missing = System.Text.RegularExpressions.Regex.Matches(helpText, @"\]\(#([^)]+)\)")
                .Select(m => m.Groups[1].Value).Where(a => !anchors.Contains(a)).ToList();
            CheckMd($"{helpFile}: alle Inhaltsverzeichnis-Links zeigen auf Ueberschriften", string.Join(",", missing), "");
            var brokenFiles = System.Text.RegularExpressions.Regex.Matches(helpText, @"\]\(([^)#:]+\.md)(?:#[^)]*)?\)")
                .Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).Where(f => !File.Exists(Path.Combine(helpDir, f))).ToList();
            CheckMd($"{helpFile}: Links auf andere Hilfeseiten zeigen auf vorhandene Dateien", string.Join(",", brokenFiles), "");
        }
        else
        {
            mdFailures++;
            Console.WriteLine($"FEHLER: {helpFile} nicht gefunden: {helpPath}");
        }
    }

    Console.WriteLine(mdFailures == 0 ? "Alle Markdown-Pruefungen bestanden." : $"FEHLER: {mdFailures} Markdown-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------------------------------------------------------
// Editor: documentation comments (///) for classes, fields, properties and methods
// ---------------------------------------------------------------------------------------------------------------------------
{
    Console.WriteLine("=== Dokumentationskommentare (///) ===");
    int docFailures = 0;
    void CheckDoc(string title, bool ok, string detail = "")
    {
        if (!ok) docFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title} {detail}");
    }

    var plain = fire.Editor.DocComments.Parse("Draws a circle.\nSecond line of the same paragraph.\n\nNew paragraph.");
    CheckDoc("Parse: reiner Text, Zeilen eines Absatzes werden verbunden, Leerzeile trennt Absaetze",
        plain?.Summary == "Draws a circle. Second line of the same paragraph.\nNew paragraph.", plain?.Summary);

    var tagged = fire.Editor.DocComments.Parse("<summary>\nAdds <c>two</c> numbers, see <see cref=\"Sub\"/>.\n</summary>\n<param name=\"a\">first &lt;int&gt;</param>\n<param name=\"b\">second</param>\n<returns>the sum</returns>\n<remarks>Fast.</remarks>");
    CheckDoc("Parse: summary, c, see cref, Entities", tagged?.Summary == "Adds two numbers, see Sub.", tagged?.Summary);
    CheckDoc("Parse: Parameter in Reihenfolge mit Text", tagged != null && tagged.Parameters.Count == 2 && tagged.Parameters[0] == ("a", "first <int>") && tagged.Parameters[1] == ("b", "second"));
    CheckDoc("Parse: returns und remarks", tagged?.Returns == "the sum" && tagged?.Remarks == "Fast.");
    CheckDoc("Parse: leerer Kommentar ist kein Dokument", fire.Editor.DocComments.Parse("  \n ") == null);

    string docSource = """
        /// A calculator.
        /// Works on ints.
        class Calc {
            /// <summary>Adds two numbers.</summary>
            /// <param name="a">first</param>
            /// <param name="b">second &amp; last</param>
            /// <returns>the sum</returns>
            int Add(int a, int b) { return a + b }

            /// Current total.
            int total

            /// <summary>Doubled total</summary>
            int Double { get { return this.total * 2 } }

            // an ordinary comment is no documentation
            Plain() { }

            /// separated from its method by a blank line

            Separated() { }
        }
        var c = new Calc()
        c.Add(1, 2)
        c.total
        c.Double
        c.Plain()
        c.Separated()
        """;

    var docIndex = fire.Editor.ScriptSymbolIndex.Build(docSource);
    fire.Editor.ResolvedSymbol? SymbolAt(string needle)
    {
        int offset = docSource.LastIndexOf(needle, StringComparison.Ordinal) + 1;
        return fire.Editor.NavigationEngine.TryResolveSymbol(docSource, offset, docIndex);
    }

    var calcDoc = SymbolAt("Calc()")?.Documentation;
    CheckDoc("Klasse: /// ueber der Deklaration, mehrere Zeilen werden verbunden", calcDoc?.Summary == "A calculator. Works on ints.", calcDoc?.Summary);
    var addSymbol = SymbolAt("Add(1");
    var addDoc = addSymbol?.Documentation;
    CheckDoc("Methode: summary, Parameter und returns", addDoc?.Summary == "Adds two numbers." && addDoc.Parameters.Count == 2
        && addDoc.Parameters[1].Text == "second & last" && addDoc.Returns == "the sum", addDoc?.ToPlainText());
    CheckDoc("Methode: die Kopfzeile nennt Klasse und Signatur", addSymbol?.Header.Contains("Calc.Add(int a, int b)") == true, addSymbol?.Header);
    CheckDoc("Feld: einfacher /// Text", SymbolAt("total\nc.Double")?.Documentation?.Summary == "Current total.");
    CheckDoc("Property: summary", SymbolAt("Double\nc.Plain")?.Documentation?.Summary == "Doubled total");
    CheckDoc("Gewoehnlicher // Kommentar ist keine Dokumentation", SymbolAt("Plain()\nc.Sep")?.Documentation == null);
    CheckDoc("Leerzeile zwischen /// und Deklaration: keine Dokumentation", SymbolAt("Separated()")?.Documentation == null);
    CheckDoc("Die Deklarationsstelle selbst loest ebenfalls auf (Cursor auf dem Namen in der Klasse)",
        fire.Editor.NavigationEngine.TryResolveSymbol(docSource, docSource.IndexOf("Add(int a") + 1, docIndex)?.Documentation?.Summary == "Adds two numbers.");

    var items = fire.Editor.CompletionEngine.GetSuggestions(docSource + "\nc.", docSource.Length + 3, fire.Editor.ScriptSymbolIndex.Build(docSource + "\nc."));
    CheckDoc("Vervollstaendigung: Eintraege tragen ihre Dokumentation, andere nicht",
        items.FirstOrDefault(i => i.Text == "Add")?.Documentation?.Summary == "Adds two numbers." && items.FirstOrDefault(i => i.Text == "Plain")?.Documentation == null);
    var classItems = fire.Editor.CompletionEngine.GetSuggestions(docSource + "\nvar d = new Ca", docSource.Length + 15, fire.Editor.ScriptSymbolIndex.Build(docSource + "\nvar d = new Ca"));
    CheckDoc("Vervollstaendigung: Klassen tragen ihre Dokumentation", classItems.FirstOrDefault(i => i.Text == "Calc")?.Documentation?.Summary == "A calculator. Works on ints.");

    // Call context: tooltip stays during the arguments, new Foo( jumps to the constructor
    string callSource = """
        /// A point.
        class Point {
            int x
            /// <summary>Creates the origin.</summary>
            construct() { this.x = 0 }
            /// <summary>Creates a point.</summary>
            /// <param name="x">the x value</param>
            construct(int x) { this.x = x }
            /// <summary>Moves it.</summary>
            Move(int dx, int dy) { }
        }
        """;
    fire.Editor.ResolvedSymbol? CallAt(string text)
    {
        string full = callSource + "\n" + text;
        var idx = fire.Editor.ScriptSymbolIndex.Build(full);
        var call = fire.Editor.NavigationEngine.FindOpenCall(full, full.Length);
        return call == null ? null : fire.Editor.NavigationEngine.TryResolveCall(full, call, idx);
    }
    CheckDoc("Aufruf: nach 'new Point(' zeigt der Tooltip den Konstruktor", CallAt("var p = new Point(")?.Documentation?.Summary == "Creates the origin.", CallAt("var p = new Point(")?.Documentation?.Summary);
    CheckDoc("Aufruf: mit einem Argument wird der passende Konstruktor gewaehlt", CallAt("var p = new Point(5")?.Documentation?.Summary == "Creates a point.", CallAt("var p = new Point(5")?.Documentation?.Summary);
    CheckDoc("Aufruf: Konstruktor-Kopfzeile", CallAt("var p = new Point(5")?.Header.Contains("new Point(int x)") == true, CallAt("var p = new Point(5")?.Header);
    CheckDoc("Aufruf: Methode bleibt waehrend der Argumente dokumentiert", CallAt("var p = new Point(1)\np.Move(1, ")?.Documentation?.Summary == "Moves it.", CallAt("var p = new Point(1)\np.Move(1, ")?.Documentation?.Summary);
    CheckDoc("Aufruf: nach der schliessenden Klammer gibt es keinen offenen Aufruf", CallAt("var p = new Point(1)") == null);
    CheckDoc("Aufruf: Klammern in Strings zaehlen nicht", CallAt("var p = new Point(\"(\")") == null);

    Console.WriteLine(docFailures == 0 ? "Alle Dokumentationskommentar-Pruefungen bestanden." : $"FEHLER: {docFailures} Dokumentationskommentar-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------------------------------------------------------
// Help page "Embedding.md": the host examples shown there must run with the real API
// ---------------------------------------------------------------------------------------------------------------------------
{
    Console.WriteLine("=== Embedding.md: Host-Beispiele ===");
    int embFailures = 0;
    void CheckEmb(string title, bool ok, string? detail = null)
    {
        if (!ok) embFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}" + (detail == null ? "" : $"\n  erhalten: {detail}"));
    }

    // Skript ausfuehren, print einsammeln
    var printed = new List<string>();
    Func<Value[], Value> writer = args => { printed.Add(args[0].ToString()!); return Value.MakeUndefined(); };
    var hello = RuntimeSession.Build(new[] { "var name = \"fire\"\nprint($\"Hello from {name}!\")" }, null, writer);
    hello.Run();
    CheckEmb("Skript ausfuehren: print geht an den debugWriter", string.Join("|", printed) == "Hello from fire!", string.Join("|", printed));

    // Several sources are joined into one program
    printed.Clear();
    RuntimeSession.Build(new[] { "var a = 20", "print(a + 22)" }, VmExecutionMode.Release, writer).Run();
    CheckEmb("Mehrere Quellen, ausdrueckliche Betriebsart", string.Join("|", printed) == "42", string.Join("|", printed));

    // Uebersetzungsfehler
    List<string>? compileMessages = null;
    try { RuntimeSession.Build(new[] { "var x = ;\nprint(unknownName)" }, null, writer); }
    catch (Exception ex) when (ex is ParseException or ResolverException or CompilerException or NotSupportedException or PreprocessorException)
    {
        compileMessages = CompileErrors.Messages(ex).ToList();
    }
    CheckEmb("Uebersetzungsfehler: Build wirft, CompileErrors liefert Meldungen", compileMessages is { Count: > 0 }, compileMessages == null ? "keine Exception" : null);

    // Unhandled exception: Run returns normally
    var failing = RuntimeSession.Build(new[] { "class Oops {\n    string message\n    construct(string message) { this.message = message }\n}\nthrow new Oops(\"boom\")" }, null, writer);
    failing.Run();
    var failingVm = failing.VirtualMachine!;
    CheckEmb("Nicht behandelte Exception steht in vm.UnhandledException",
        failingVm.UnhandledException != null && new UncaughtScriptException(failingVm.UnhandledException).Message.Contains("boom"));

    // IoStdio.Custom and IoPolicy are accepted by Build
    var stdioOut = new System.Text.StringBuilder();
    var stdio = fire.IO.Bridge.IoStdio.Custom(text => stdioOut.Append(text), text => stdioOut.Append("[error] ").Append(text), new MemoryStream(System.Text.Encoding.UTF8.GetBytes("in\n")));
    var policy = fire.IO.Bridge.IoPolicy.Rooted(Path.GetTempPath(), readOnly: false);
    printed.Clear();
    RuntimeSession.Build(new[] { "print(1)" }, null, writer, ioPolicy: policy, ioStdio: stdio).Run();
    CheckEmb("ioPolicy und ioStdio sind Parameter von Build", string.Join("|", printed) == "1");

    // terminate(value): exit code via VM.ExitValue; the next program starts normally again
    RuntimeSession.Build(new[] { "terminate(7)" }, null, writer).Run();
    var exit = VM.ExitValue;
    CheckEmb("terminate(7): VM.ExitValue ist 7", exit.Kind == ValueKind.Int && exit.AsInt() == 7);
    printed.Clear();
    RuntimeSession.Build(new[] { "print(\"again\")" }, null, writer).Run();
    CheckEmb("Nach terminate laeuft das naechste Programm im selben Prozess normal", string.Join("|", printed) == "again", string.Join("|", printed));
    CheckEmb("Ein normal beendetes Programm hat keinen Exitwert", VM.ExitValue.Kind == ValueKind.Undefined);

    // Script on a background thread
    printed.Clear();
    var bgSession = RuntimeSession.Build(new[] { "print(\"background\")" }, null, writer);
    var bgThread = new Thread(() => bgSession.Run()) { IsBackground = true };
    bgThread.Start();
    CheckEmb("Hintergrundthread: Run laeuft dort", bgThread.Join(10000) && string.Join("|", printed) == "background");

    // Untere Ebene: eigene extern-Funktion per ExternRegistry
    var lowNatives = NativeRegistry.CreateDefault();
    var lowExterns = new ExternRegistry();
    lowExterns.Register("HostAdd", args => (long)args[0]! + (long)args[1]!);
    var lowProgram = Parser.Parse("extern int HostAdd(int a, int b)\nprint(HostAdd(2, 3))");
    var lowResolved = Resolver.Resolve(lowProgram, lowNatives.Names);
    var lowCompiled = Compiler.Compile(lowProgram, lowResolved, lowNatives);
    var lowOut = new StringWriter();
    var oldOut = Console.Out;
    Console.SetOut(lowOut);
    try { new VM(lowCompiled.TopLevel, new Scope(null, isGlobal: true), lowNatives, lowCompiled.Classes, lowExterns).Run(); }
    finally { Console.SetOut(oldOut); }
    CheckEmb("Eigene C#-Funktion per extern + ExternRegistry", lowOut.ToString().Trim() == "5", lowOut.ToString());

    Console.WriteLine(embFailures == 0 ? "Alle Embedding-Pruefungen bestanden." : $"FEHLER: {embFailures} Embedding-Pruefung(en) fehlgeschlagen.");
}

ProjectTests.Run();
ImageTests.Run();
GitTests.Run();

// ---------------------------------------------------------------------------------------------------------------------------
// Native backend (fire.Native): the same source runs in the VM and as generated C++ - the output must be identical
// ---------------------------------------------------------------------------------------------------------------------------
if (Environment.GetEnvironmentVariable("FIRE_TEST_NO_NATIVE") == "1") Console.WriteLine("(FIRE_TEST_NO_NATIVE: the native checks are skipped)");
else
{
    Console.WriteLine("=== Native-Backend: erzeugtes C++ gegen die VM ===");
    int natFailures = 0;
    void CheckNat(string title, bool ok, string? detail = null)
    {
        if (!ok) natFailures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    string? FindCxx()
    {
        foreach (var name in new[] { "g++", "clang++", "c++" })
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(name, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                p.WaitForExit();
                if (p.ExitCode == 0) return name;
            }
            catch (Exception) { }
        }
        return null;
    }

    string vmOutput(string source)
    {
        // `terminate` and `catch threads/terminate` are process-wide state of the VM: an earlier case must not leave its handlers behind
        VM.ResetTerminateForTests();
        GlobalHandlers.ResetForTests();
        var lines = new List<string>();
        var session = RuntimeSession.Build(new[] { source }, VmExecutionMode.Release, args => { lines.Add(args[0].ToString()!); return Value.MakeUndefined(); });
        session.Run();
        return string.Join("\n", lines) + (lines.Count > 0 ? "\n" : "");
    }

    var natCases = new (string Name, string Source)[]
    {
        ("Ganzzahl-Arithmetik und Bit-Operationen", """
            print(7 / 2)
            print(-7 / 2)
            print(7 % 3)
            print(-7 % 3)
            print(1 << 4)
            print(256 >> 2)
            print(6 & 3)
            print(6 | 3)
            print(6 # 3)
            print(~5)
            print(-(3 + 4))
            var big = 9223372036854775807
            print(big + 1)
            print(big * 2)
            """),
        ("Gleitkomma und Ausgabeformat", """
            print(1.5 + 2)
            print(10 / 4.0)
            print(0.1 + 0.2)
            print(1.0 / 3)
            print(1000000.0 * 1000000.0 * 1000.0)
            print(1000000.0 * 1000000.0 * 100.0)
            print(1000000.0 * 1000000.0 * 10000.0)
            print(1000000.0 * 1000000.0 * 100000.0)
            print(123456789.0 * 1000000000.0 * 1000000000.0)
            print(0.00001)
            print(0.000123)
            print(0.000001 * 2.5)
            print(0.0001 * 1)
            print(100.0)
            print(-2.5 * 4)
            print(7.5 % 2)
            """),
        ("Vergleiche und Logik", """
            print(1 < 2)
            print(2 <= 2)
            print(3 > 4)
            print(3 >= 3)
            print(1 == 1)
            print(1 == 1.0)
            print(1.5 != 2.5)
            print(!(1 < 2))
            print(true && false || true)
            print(false || false)
            var x = 5
            print(x > 3 && x < 10)
            """),
        ("Schleifen, break, continue und verschachtelte Bloecke", """
            var total = 0
            var i = 0
            while (i < 20) {
                i++
                if (i % 2 == 0) { continue }
                if (i > 15) { break }
                total = total + i
            }
            print(total)
            for (var a = 0; a < 4; a++) {
                for (var b = 0; b < 4; b++) {
                    var p = a * b
                    if (p == 2 || p == 6) { print(p) }
                }
            }
            """),
        ("Statische Methoden und Rekursion", """
            class MathX {
                static int Gcd(int a, int b) {
                    if (b == 0) { return a }
                    return MathX.Gcd(b, a % b)
                }
                static float Half(float x) { return x / 2 }
                static int Fact(int n) {
                    if (n <= 1) { return 1 }
                    return n * MathX.Fact(n - 1)
                }
            }
            print(MathX.Gcd(48, 18))
            print(MathX.Half(5.0))
            print(MathX.Fact(15))
            """),
        ("Einheiten gleicher Art", """
            print(500mm + 250mm)
            print(5mm < 3mm)
            var l = 2.5m
            print(l - 1m)
            print(l)
            var n = 3mm
            n = n + 4mm
            print(n)
            """),
        ("Zeichenketten und Zeichen als Ausgabe", """
            print("Hello, wörld")
            print("tab\there")
            var s = "same"
            print(s == "same")
            print(s != "other")
            """),
        ("float 32 (#floatwidth 32): Rundung, Ausgabe, Ganzzahl-nach-float", """
            #floatwidth 32
            print(0.1 + 0.2)
            print(1.0 / 3)
            print(16777216.0 + 1.0)
            print(16777217 + 0.0)
            print(123456789.0)
            print(1000.0 * 1000000.0)
            print(1000.0 * 100000.0)
            print(0.00001)
            print(0.0001 * 1)
            print(7.5 % 2)
            print(2.5 < 2.75)
            var s = 0.0
            for (var i = 0; i < 1000; i = i + 1) { s = s + 0.1 }
            print(s)
            print(-1.5 * 3)
            """),
        ("float 32: Einheiten und Kontrollfluss", """
            #floatwidth 32
            var total = 0.0mm
            for (var i = 0; i < 100; i = i + 1) { total = total + 0.3mm }
            print(total)
            class F { static float Mean(float a, float b) { return (a + b) / 2 } }
            print(F.Mean(0.1, 0.2))
            """),
        ("Strings: Konkatenation, Laenge, Methoden, Format, Vergleich", """
            var name = "fire"
            print("Hello, " + name + "!")
            var n = 3
            print("n=" + n + " f=" + 2.5 + " b=" + true + " c=" + 'x' + " u=" + undefined)
            print("mm: " + 5mm + " " + 2.5mm)
            var s = "Hello, wörld"
            print(s.Length)
            print(s.ToUpper())
            print(s.ToLower())
            print(s.Substring(7, 3))
            print(s.Substring(7))
            print(s.IndexOf("w"))
            print(s.IndexOf("o", 5))
            print(s.IndexOf("zz"))
            print(s.LastIndexOf("o"))
            print(s.LastIndexOf("l", 5))
            print(s.LastIndexOf(""))
            print(s.Contains("lo"))
            print(s.StartsWith("Hell"))
            print(s.EndsWith("ld"))
            print(s.CharAt(4))
            print(s.Replace("l", "LL"))
            print("  padded\t ".Trim() + "|")
            print("  padded ".TrimStart() + "|")
            print("  padded ".TrimEnd() + "|")
            print("7".PadLeft(3) + "|" + "7".PadLeft(3, '0') + "|" + "ab".PadRight(5, '.') + "|")
            print($"{name}: {n} {2.5:F2} {255:X4} {255:x} {7:D3} {-7:D3} {5:B8} {1234.5678:F1}")
            print($"{0.5:F0} {1.5:F0} {2.5:F0} {0.125:F2} {1.005:F2}")
            var text = ""
            for (var i = 0; i < 5; i = i + 1) { text = text + "ab" }
            print(text)
            print(text.Length)
            var t = s
            s = "other"
            print(t)
            print(s == "other")
            print(s != t)
            print("a" + 1 + 2)
            print('q'.IsLetter())
            print('7'.IsDigit())
            print('a'.ToUpper())
            print('Z'.ToLower())
            print(' '.IsWhiteSpace())
            print('x'.ToInt())
            """),
        ("Strings: Lebensdauer (Rueckgabe, Felder, innere Bloecke, Aliase, Objekte mit Zeichenketten)", """
            class Person {
                string name
                construct(string name) { this.name = name }
                destruct() { print("bye " + this.name) }
                string Greet() { return "hi " + this.name }
                Rename(string n) { this.name = n + "!" }
            }
            class Maker {
                static string Join(string a, string b) {
                    var r = a + "-" + b
                    return r
                }
                static string Pick(int i) {
                    if (i > 0) { return "positive" }
                    var inner = "non" + "-" + "positive"
                    return inner
                }
            }
            var outer = "start"
            {
                var inner = "in" + "ner"
                outer = outer + ":" + inner
            }
            print(outer)
            var p = new Person("Ada")
            print(p.Greet())
            p.Rename("Grace")
            print(p.Greet())
            var kept = p.name
            p.Rename("Linus")
            print(kept)
            print(p.name)
            var joined = Maker.Join("a", "b")
            print(Maker.Join(joined, "c"))
            print(Maker.Pick(1) + " " + Maker.Pick(0))
            var words = ["one", "two"]
            words[1] = words[0] + words[1]
            print(words[1])
            var all = ""
            foreach (w in words) { all = all + w + ";" }
            print(all)
            {
                var q = new Person("Temp")
                var local = q.Greet()
                print(local)
            }
            var alias = outer
            outer = "changed"
            print(alias)
            print(outer)
            """),
        ("Strings: ToString() von Klassen, in print, + und $\"\"", """
            class Money {
                int cents
                construct(int cents) { this.cents = cents }
                string ToString() { return "$" + this.cents / 100 + "." + this.cents % 100 }
            }
            var m = new Money(1999)
            print(m)
            print("price: " + m)
            print(m + " total")
            print($"[{m}]")
            """),
        ("Strings: Unicode (Umlaute, Griechisch, Kyrillisch, Surrogatpaare)", """
            var s = "Größe ÀÉÎõ αβγ жя"
            print(s.Length)
            print(s.ToUpper())
            print(s.ToLower())
            var emoji = "a😀b"
            print(emoji.Length)
            print(emoji)
            print(emoji.IndexOf("b"))
            print("é".Length)
            print("ÄÖÜ".ToLower() + "äöü".ToUpper())
            print('é'.IsLetter())
            print("x" + 'ä' + 'ö')
            """),
        ("Arrays: Literal, new, Zugriff, ++, length, verschachtelt, foreach, Puffer, Zeichenkette indexieren", """
            var names = ["Ada", "Grace", "Linus"]
            var total = 0
            foreach (n in names) {
                total = total + n.Length
                print(n)
            }
            print(total)
            var a = new int[4]
            a[1] = 5
            a[1]++
            ++a[1]
            a[2] = a[1] * 2
            print(a[1] + a.length)
            print(a[2])
            print(a[0])
            var m = [[1, 2], [3, 4]]
            print(m[1][0] + m[0][1])
            m[0][0] = 10
            print(m[0][0])
            var b = new byte[3]
            b[0] = 200
            b[2] = b[0] + 100
            print(b[0] + b.length)
            print(b[2])
            var cs = "xyz"
            print(cs[1])
            var parts = "a,b,,c".Split(",")
            print(parts.Length)
            foreach (p in parts) { print("[" + p + "]") }
            print("x".Split("").Length)
            var mixed = [1, "two", 3.5, true, 'c']
            foreach (e in mixed) { print(e) }
            var sum = 0
            for (var i = 1; i < 3; i = i + 1) { sum = sum + a[i] }
            print(sum)
            """),
        ("Listen aus dem Prelude (List, Add, Index, foreach, Wachstum)", """
            var l = new List()
            for (var i = 0; i < 20; i = i + 1) { l.Add(i * i) }
            var sum = 0
            foreach (v in l) { sum = sum + v }
            print(sum)
            print(l[3])
            l[3] = 100
            print(l[3])
            print(l.count)
            var names = new List(["x", "y", "z"])
            var joined = ""
            foreach (n in names) { joined = joined + n }
            print(joined)
            """),
        ("Benchmark array", """
            var n = 100000
            var a = new int[n]
            var total = 0
            for (var pass = 0; pass < 5; pass = pass + 1) {
                for (var i = 0; i < n; i = i + 1) { a[i] = i + pass }
                for (var i = 0; i < n; i = i + 1) { total = total + a[i] }
            }
            print(total)
            """),
        ("Benchmark string", """
            var s = "Hello, World, again"
            var n = 0
            for (var i = 0; i < 40000; i = i + 1) {
                n = n + s.IndexOf("o") + s.Substring(3, 4).Length + s.Length
            }
            var text = ""
            for (var i = 0; i < 2000; i = i + 1) { text = text + "ab" }
            print(n + text.Length)
            """),
        ("Benchmark list", """
            var l = new List()
            for (var i = 0; i < 40000; i = i + 1) { l.Add(i) }
            var total = 0
            foreach (v in l) { total = total + v }
            print(total)
            """),
        ("Lambdas: Captures als Kopie, on-Ziel, Verschachtelung, Signaturpruefung, Lambdas in Listen", """
            var add = func (a, b) => { return a + b }
            print(add(2, 3))
            var twice = x => x * 2
            print(twice(21))
            var limit = 3
            var f = x => x > limit
            limit = 10
            print(f(5))
            class T {
                int n
                construct(int n) { this.n = n }
                static Run() {
                    var k = 7
                    var g = (a) => a + k
                    k = 100
                    return g(1)
                }
                Make() {
                    var inc = func (x) on this => { this.n = this.n + x; return this.n }
                    return inc
                }
            }
            print(T.Run())
            var t = new T(10)
            var inc = t.Make()
            print(inc(5))
            print(inc(5))
            var fs = new List()
            for (var i = 0; i < 3; i = i + 1) { fs.Add(() => i * 10) }
            foreach (h in fs) { print(h()) }
            var greet = (name) => "hi " + name
            print(greet("Ada"))
            var compose = (f1, f2) => (x) => f2(f1(x))
            var both = compose(twice, x => x + 1)
            print(both(5))
            var acc = 0
            for (var j = 0; j < 100; j = j + 1) { acc = add(acc, j) }
            print(acc)
            lambda w = func () => { print("zero") }
            w()
            int lambda<int> sq = x => x * x
            print(sq(9))
            """),
        ("Benchmark lambda", """
            var add = func (a, b) => { return a + b }
            var acc = 0
            for (var i = 0; i < 200000; i = i + 1) {
                acc = add(acc, i)
            }
            print(acc)
            """),
        ("Objekte: Klassen, Felder, Konstruktoren, Vererbung, Destruktoren, statische Felder", """
            class Animal {
                int id
                int legs = 4
                static int count = 0
                construct(int id) { this.id = id; Animal.count = Animal.count + 1 }
                destruct() { print(0 - this.id) }
                Speak() { print(this.id) }
                int Legs() { return this.legs }
            }
            class Dog : Animal {
                int tricks
                construct(int id) : base(id) { this.tricks = 2 }
                Speak() { base.Speak(); print(100 + this.tricks) }
            }
            class Holder {
                Dog pet
                construct() { this.pet = new Dog(7) }
            }
            class Maker {
                static Dog Make(int id) { var d = new Dog(id); return d }
            }
            var d = new Dog(1)
            d.Speak()
            print(d.Legs())
            var h = new Holder()
            h.pet.Speak()
            {
                var inner = new Animal(5)
                inner.Speak()
            }
            var m = Maker.Make(9)
            m.Speak()
            print(Animal.count)
            """),
        ("Objekte: virtuelle Methoden und gleiche Feldnamen in verschiedenen Klassen", """
            class Shape {
                int w
                int h
                construct(int w, int h) { this.w = w; this.h = h }
                int Area() { return 0 }
                Describe() { print(this.Area()) }
            }
            class Rect : Shape {
                construct(int w, int h) : base(w, h) { }
                int Area() { return this.w * this.h }
            }
            class Square : Rect {
                construct(int s) : base(s, s) { }
            }
            class Triangle : Shape {
                construct(int w, int h) : base(w, h) { }
                int Area() { return this.w * this.h / 2 }
            }
            class Other {
                int pad
                int w
                construct() { this.pad = 1; this.w = 99 }
                int Area() { return this.w }
            }
            var shapes = new Shape(3, 4)
            shapes.Describe()
            var r = new Rect(3, 4)
            r.Describe()
            var q = new Square(5)
            q.Describe()
            var t = new Triangle(6, 5)
            t.Describe()
            var o = new Other()
            print(o.Area())
            print(o.w)
            print(q.w)
            """),
        ("Objekte: Besitz - Rueckgabe, Bloecke, Schleifen, break/continue, fruehes return", """
            class Res {
                int id
                construct(int id) { this.id = id; print(this.id) }
                destruct() { print(0 - this.id) }
            }
            class F {
                static Res Make(int id) {
                    var tmp = new Res(id + 1000)
                    if (id > 5) {
                        var inner = new Res(id + 2000)
                        return new Res(id)
                    }
                    var kept = new Res(id)
                    return kept
                }
                static int Early(int n) {
                    var guard = new Res(n + 500)
                    if (n > 0) { return n }
                    return 0 - 1
                }
            }
            var a = F.Make(1)
            var b = F.Make(9)
            print(F.Early(3))
            print(F.Early(0))
            for (var i = 0; i < 4; i = i + 1) {
                var loopRes = new Res(10 + i)
                if (i == 1) { continue }
                if (i == 3) { break }
                print(loopRes.id)
            }
            {
                var x = new Res(70)
                {
                    var y = new Res(71)
                }
                print(1)
            }
            print(a.id + b.id)
            """),
        ("Objekte: Besitzer-Kaskade und verkettete Objekte", """
            class Node {
                int value
                Node next
                construct(int value) { this.value = value }
                destruct() { print(0 - this.value) }
                Add(int v) {
                    if (this.next == undefined) { this.next = new Node(v) }
                    else { this.next.Add(v) }
                }
                int Sum() {
                    if (this.next == undefined) { return this.value }
                    return this.value + this.next.Sum()
                }
            }
            var head = new Node(1)
            head.Add(2)
            head.Add(3)
            head.Add(4)
            print(head.Sum())
            {
                var local = new Node(10)
                local.Add(20)
                print(local.Sum())
            }
            print(head.next.next.value)
            """),
        ("Benchmark method", """
            class Counter {
                int count
                int step
                construct() { this.count = 0; this.step = 2 }
                Inc() { this.count = this.count + this.step }
                int Get() { return this.count }
            }
            var c = new Counter()
            for (var i = 0; i < 250000; i = i + 1) {
                c.Inc()
            }
            print(c.Get())
            """),
        ("Ausnahmen: throw, catch nach Typ, Destruktoren beim Abwickeln, finally", """
            class Err : Exception {
                string message
                int code
                construct(string m, int c) { this.message = m; this.code = c }
            }
            class Other : Exception {
                construct() { }
            }
            class Res {
                string name
                construct(string n) { this.name = n; print("open " + n) }
                destruct() { print("close " + name) }
            }
            class T {
                static int Deep(int n) {
                    var r = new Res("deep" + n)
                    if (n == 0) { throw new Err("bottom", 42) }
                    return T.Deep(n - 1) + 1
                }
                static int Safe(int x) {
                    try {
                        return T.Deep(x)
                    } catch (Other o) {
                        print("wrong handler")
                        return -1
                    } finally {
                        print("finally in Safe " + x)
                    }
                }
            }
            try {
                var a = new Res("A")
                print(T.Safe(2))
            } catch (Err e) {
                print("caught " + e.message + " " + e.code)
            } finally {
                print("outer finally")
            }
            print("next")
            try {
                throw new Other()
            } catch (e) {
                print("catch-all")
            }
            """),
        ("Ausnahmen: resume an der Wurfstelle, Indexfehler als Ausnahme", """
            class Ask : Exception {
                string what
                construct(string w) { this.what = w }
            }
            class T {
                static int Value(string k) {
                    var v = throw new Ask(k)
                    return v
                }
            }
            try {
                var x = T.Value("a") + T.Value("b")
                print("x = " + x)
            } catch (Ask e) {
                print("asked " + e.what)
                e.resume(10)
            }
            print("done")
            var arr = new int[3]
            try {
                arr[1] = 5
                print(arr[1])
                print(arr[7])
                print("after read")
                arr[9] = 1
                print("after write")
            } catch (IndexOutOfBoundsException e) {
                print("index " + e.index + " of " + e.length)
                e.resume(0)
            }
            try {
                print("abc".Substring(5))
            } catch (e) {
                print("sub: " + e.message)
            }
            """),
        ("Ausnahmen: finally bei break, continue, return, Weiterwerfen", """
            class E1 : Exception { construct() { } }
            var i = 0
            while (i < 6) {
                try {
                    i = i + 1
                    if (i == 2) { continue }
                    if (i == 4) { break }
                    print("body " + i)
                } finally {
                    print("fin " + i)
                }
            }
            print("after loop " + i)
            class F {
                static int G(int n) {
                    for (var k = 0; k < 10; k = k + 1) {
                        try {
                            if (k == n) { return k * 10 }
                        } finally {
                            print("G fin " + k)
                        }
                    }
                    return -1
                }
                static int H() {
                    try {
                        try {
                            throw new E1()
                        } finally {
                            print("inner fin")
                        }
                    } catch (E1 e) {
                        print("H caught")
                        return 7
                    } finally {
                        print("H outer fin")
                    }
                }
            }
            print(F.G(2))
            print(F.H())
            try {
                try {
                    throw new E1()
                } catch (E1 e) {
                    print("rethrow")
                    throw e
                }
            } catch (e) {
                print("outer caught")
            }
            """),
        ("Ausnahmen: Klassenhierarchie, continue/break im catch, return durch finally", """
            class Base : Exception {
                string message
                construct(string m) { this.message = m }
            }
            class Derived : Base {
                construct(string m) : base(m) { }
            }
            class Res {
                string n
                construct(string n) { this.n = n }
                destruct() { print("free " + n) }
            }
            class W {
                int count
                construct() { this.count = 0 }
                int Step(int k) {
                    this.count = this.count + 1
                    if (k % 3 == 0) { throw new Derived("k=" + k) }
                    return k
                }
            }
            var w = new W()
            var sum = 0
            for (var i = 1; i <= 8; i = i + 1) {
                try {
                    var r = new Res("r" + i)
                    sum = sum + w.Step(i)
                    if (i == 7) { continue }
                    print("ok " + i)
                } catch (Base e) {
                    print("caught " + e.message)
                    if (i == 6) { break }
                    continue
                } finally {
                    print("fin " + i)
                }
                print("tail " + i)
            }
            print("sum " + sum + " count " + w.count)
            // nested try in catch, rethrow from nested
            try {
                try {
                    throw new Derived("first")
                } catch (Base e) {
                    try {
                        throw new Derived("second")
                    } catch (Derived d) {
                        print("inner " + d.message)
                    }
                    print("after inner")
                    throw e
                }
            } catch (e) {
                print("outer " + e.message)
            }
            // return in catch with finally, finally return replaces
            class R {
                static int A() {
                    try {
                        throw new Derived("x")
                    } catch (e) {
                        return 1
                    } finally {
                        print("A fin")
                    }
                }
                static int B() {
                    try {
                        return 1
                    } finally {
                        print("B fin")
                        return 2
                    }
                }
                static string C() {
                    var arr = [1, 2, 3]
                    foreach (v in arr) {
                        try {
                            if (v == 2) { return "two" }
                        } finally {
                            print("C fin " + v)
                        }
                    }
                    return "none"
                }
            }
            print(R.A())
            print(R.B())
            print(R.C())
            """),
        ("Abbruch: nicht gefangene Ausnahme beendet das Programm", """
            class Boom : Exception {
                construct() { }
            }
            class Res {
                string n
                construct(string n) { this.n = n }
                destruct() { print("free " + n) }
            }
            class T {
                static F(int n) {
                    var r = new Res("f" + n)
                    if (n == 0) { throw new Boom() }
                    T.F(n - 1)
                }
            }
            var g = new Res("global")
            print("start")
            T.F(2)
            print("never")
            """),
        ("Ausnahmen: Lambdas, ToString, Konstruktor, catch ohne try", """
            class Boom : Exception {
                string message
                construct(string m) { this.message = m }
            }
            class Bad {
                string ToString() { throw new Boom("tostring") }
            }
            var f = (int x) => {
                if (x > 2) { throw new Boom("big " + x) }
                return x * 2
            }
            for (var i = 1; i < 5; i = i + 1) {
                try {
                    print(f(i))
                } catch (Boom b) {
                    print("lambda threw: " + b.message)
                }
            }
            try {
                var b = new Bad()
                print("v: " + b)
            } catch (Boom e) {
                print("ts: " + e.message)
            }
            try {
                print(new Bad())
            } catch (Boom e) {
                print("print: " + e.message)
            }
            class C {
                construct(int x) {
                    if (x < 0) { throw new Boom("ctor") }
                }
            }
            try { var c = new C(-1) } catch (e) { print("ctor failed " + e.message) }
            var u = 3
            {
                catch (e) { print("implicit " + e.message) }
                throw new Boom("blocky")
            }
            print("end " + u)
            """),
        ("Abbruch: finally vor dem Abbruch, Methoden, resume aus verschachtelten Aufrufen", """
            class Oops : Exception {
                string message
                construct(string m) { this.message = m }
            }
            class Node {
                string name
                Node child
                construct(string n) { this.name = n; print("new " + n) }
                destruct() { print("del " + name) }
            }
            class Svc {
                int tries
                string log
                construct() { this.tries = 0; this.log = "" }
                string Run(int n) {
                    var tmp = "run" + n
                    try {
                        this.tries = this.tries + 1
                        var node = new Node("n" + n)
                        if (n % 2 == 1) { throw new Oops("odd " + tmp) }
                        this.log = this.log + tmp + ","
                        return "ok " + tmp
                    } catch (Oops o) {
                        this.log = this.log + "!" + o.message + ","
                        return "failed " + o.message
                    } finally {
                        this.log = this.log + "f" + n + ","
                    }
                }
                int Parse(string text) {
                    // resume with a default value
                    var v = throw new Oops("parse " + text)
                    return v
                }
            }
            var s = new Svc()
            for (var i = 0; i < 4; i = i + 1) { print(s.Run(i)) }
            print(s.log + " tries=" + s.tries)
            var total = 0
            try {
                total = s.Parse("a") + s.Parse("b") + 1
            } catch (Oops o) {
                print("fix " + o.message)
                o.resume(100)
            }
            print("total " + total)

            // deep resume from a nested call
            class Handler2 {
                static Fix(Oops o) { o.resume(5) }
            }
            try {
                var q = throw new Oops("q")
                print("q=" + q)
            } catch (Oops o) {
                Handler2.Fix(o)
            }

            // uncaught through finally: finally runs, then the program ends
            try {
                print("before")
                throw new Oops("final")
            } finally {
                print("cleanup")
            }
            print("unreachable")
            """),
        ("Ausnahmen: break/continue aus verschachtelten catch-Bloecken, foreach", """
            class A : Exception { string message; construct(string m) { this.message = m } }
            class B : Exception { construct() { } }
            var items = [1, 2, 3, 4, 5, 6]
            // break/continue from catch and try bodies inside a foreach
            foreach (v in items) {
                try {
                    if (v == 1) { continue }
                    if (v == 3) { throw new A("three") }
                    if (v == 5) { throw new B() }
                    print("v" + v)
                } catch (A a) {
                    print("A " + a.message)
                    continue
                } catch (B b) {
                    print("B stops")
                    break
                } finally {
                    print("fin " + v)
                }
                print("end " + v)
            }
            // two levels of catch: break from the inner catch leaves the outer catch too
            for (var i = 0; i < 3; i = i + 1) {
                try {
                    throw new A("outer" + i)
                } catch (A a) {
                    try {
                        throw new B()
                    } catch (B b) {
                        print("inner at " + i)
                        if (i == 1) { break }
                        continue
                    }
                    print("not here")
                }
            }
            // return in nested catches through finally blocks
            class X {
                static int F(int n) {
                    try {
                        try {
                            throw new A("a")
                        } catch (A a) {
                            try {
                                throw new B()
                            } catch (B b) {
                                return n + 1
                            } finally {
                                print("f1")
                            }
                        } finally {
                            print("f2")
                        }
                    } finally {
                        print("f3")
                    }
                }
                static int G(int n) {
                    foreach (v in [1, 2, 3]) {
                        try {
                            if (v == n) { throw new A("g") }
                        } catch (A a) {
                            return v * 100
                        } finally {
                            print("G" + v)
                        }
                    }
                    return 0
                }
            }
            print(X.F(1))
            print(X.G(2))
            print(X.G(9))
            // a try that is left with an exception thrown in the catch and caught outside
            try {
                try {
                    throw new A("1")
                } catch (A a) {
                    throw new B()
                } finally {
                    print("mid finally")
                }
            } catch (B b) {
                print("got B")
            }
            """),
        ("Ausnahmen: Einheiten und Indexfehler von Zeichenketten/Puffern", """
            class M {
                int len : mm = 0mm
                construct() { this.len = 1mm }
            }
            var m = new M()
            print(m.len)
            try {
                m.len = 7
                print("no error")
            } catch (UnitMismatchException e) {
                print("unit: " + e.message)
            }
            float w : mm = 5mm
            try {
                w = 3
            } catch (e) {
                print("w: " + e.message + " | " + e.expectedUnit + " | " + e.actualUnit)
            }
            print(w)
            var s = "hello"
            try {
                print(s.CharAt(10))
            } catch (IndexOutOfBoundsException e) {
                print(e.message + " " + e.index + " " + e.length)
            }
            try {
                print(s[9])
            } catch (IndexOutOfBoundsException e) {
                print(e.message)
            }
            try {
                var b = new byte[2]
                b[5] = 1
            } catch (IndexOutOfBoundsException e) {
                print(e.message)
            }
            """),
        ("Ausnahmen: try in Konstruktor, Lambda und foreach, Zeichenketten beim Werfen", """
            class Bad : Exception { string message; construct(string m) { this.message = m } }
            class Thing {
                string state
                int value
                construct(int v) {
                    this.state = "init"
                    try {
                        if (v < 0) { throw new Bad("negative") }
                        this.value = v
                        this.state = "ok"
                    } catch (Bad b) {
                        this.value = 0
                        this.state = "recovered " + b.message
                        return
                    } finally {
                        print("ctor finally " + v)
                    }
                    print("ctor end " + v)
                }
                destruct() {
                    try { print("dtor " + this.state) } finally { print("dtor fin") }
                }
            }
            var a = new Thing(5)
            var b = new Thing(-1)
            print(a.state + " / " + b.state)
            var f = (int x) => {
                try {
                    if (x == 0) { throw new Bad("zero") }
                    return 100 / x
                } catch (e) {
                    return -1
                } finally {
                    print("lambda fin " + x)
                }
            }
            print(f(0))
            print(f(4))
            var words = ["a", "b", "c"]
            var out = ""
            foreach (w in words) {
                try {
                    if (w == "b") { throw new Bad("b!") }
                    out = out + w
                } catch (e) {
                    out = out + "[" + e.message + "]"
                }
            }
            print(out)
            // exception thrown while building a string, temporaries must not leak or crash
            var s = ""
            for (var i = 0; i < 20; i = i + 1) {
                try {
                    s = s + "x" + i
                    if (i % 7 == 6) { throw new Bad("seven " + s) }
                } catch (Bad e) {
                    s = e.message + "|"
                }
            }
            print(s)
            """),
        ("ref-Parameter: Variablen, Felder, Array-Elemente, Puffer, Konstruktor, Weitergabe", """
            class Box { int n; string s; construct() { this.n = 1; this.s = "a" } }
            class U {
                static Swap(ref a, ref b) { var t = a; a = b; b = t }
                static Inc(ref int x) { x++; x = x + 10 }
                static Twice(ref int x) { U.Inc(x); U.Inc(x) }
                static Append(ref string s, string t) { s = s + t }
                static Plain(int x) { x = 99 }
                static int Sum(ref int a, int b) { return a + b }
            }
            class Counter {
                int total
                construct(ref int seed) { this.total = seed; seed = 100 }
                Add(ref int v) { v = v + this.total }
                Bump(ref int v) { this.total = this.total + 1; v = this.total }
                Self() { this.Bump(total) }
            }
            var x = 1
            var y = 2
            U.Swap(x, y)
            print(x + " " + y)
            U.Inc(x)
            print(x)
            U.Twice(y)
            print(y)
            var s = "hi"
            U.Append(s, "!!")
            print(s)
            var z = 5
            U.Plain(z)
            print(z)
            var b = new Box()
            U.Swap(b.n, b.s)
            print(b.n + " " + b.s)
            var arr = [1, 2, 3]
            U.Inc(arr[1])
            U.Swap(arr[0], arr[2])
            print(arr[0] + " " + arr[1] + " " + arr[2])
            var buf = new byte[2]
            U.Inc(buf[1])
            print(buf[1])
            var seed = 7
            var c = new Counter(seed)
            print(seed + " " + c.total)
            var k = 3
            c.Add(k)
            print(k)
            c.Self()
            print(c.total)
            print(U.Sum(k, 1))
            var f = (int v) => { U.Inc(v); return v }
            print(f(5))
            {
                var loc = 4
                U.Inc(loc)
                print(loc)
            }
            try { U.Inc(arr[9]) } catch (e) { print("oob " + e.message) }
            """),
        ("ref-Parameter: Strings und Arrays ueber Felder und Elemente, Schleifen", """
            class Holder { string name; construct(string n) { this.name = n } }
            class U {
                static Swap(ref a, ref b) { var t = a; a = b; b = t }
                static Grow(ref string s, int n) {
                    for (var i = 0; i < n; i = i + 1) { s = s + i }
                }
                static Pick(ref string s, string alt) { if (s == "") { s = alt } return s }
                static Fill(ref arr) { var made = [1, 2, 3]; made.TakeGlobal(); arr = made }
                static int Step(ref int n) { n = n - 1; return n }
            }
            var words = ["x", "y", "z"]
            U.Swap(words[0], words[2])
            print(words[0] + words[1] + words[2])
            var h = new Holder("h")
            var s = "start"
            U.Swap(h.name, s)
            print(h.name + " " + s)
            U.Grow(h.name, 5)
            print(h.name)
            var e = ""
            print(U.Pick(e, "alt") + e)
            var arr = 0
            U.Fill(arr)
            print(arr[1])
            var n = 5
            var total = 0
            while (U.Step(n) > 0) { total = total + n }
            print(total)
            for (var i = 0; i < 100; i = i + 1) { var t = "q" + i; U.Grow(t, 3); U.Swap(t, s) }
            print(s)
            """),
        ("ref-Parameter: List.Add und eine Methode mit ref gleichen Namens", """
            class Counter {
                int total
                construct() { this.total = 0 }
                Add(ref int v) { v = v + 1 }
            }
            var l = new List()
            var q = 3
            l.Add(q)
            l.Add(5)
            print(l.count + " " + q)
            var c = new Counter()
            c.Add(q)
            print(q)
            """),
        ("Besitz: Arrays und Puffer (Scope, Feld, return, Take-Methoden, delete, zerstoerte Benutzung)", """
            class Holder {
                int data[]
                construct() { this.data = [1, 2, 3] }
                Fill() { var tmp = new int[2]; tmp[0] = 7; this.data = tmp; tmp.TakeTo(this) }
                Bad() { var tmp = new int[2]; this.data = tmp }
            }
            class Res { string n; construct(string n) { this.n = n } destruct() { print("free " + this.n) } }
            class Make {
                static int[] Create() { var a = [4, 5, 6]; return a }
                static int[] Pair() { var a = new int[2]; { var b = new int[1]; b.TakeUpwards(); a[0] = b[0] } return a }
            }
            var h = new Holder()
            print(h.data[1])
            h.Fill()
            print(h.data[0])
            var r = Make.Create()
            print(r[2])
            var p = Make.Pair()
            print(p.length)
            h.Bad()
            try { print(h.data[0]) } catch (DestroyedException e) { print("destroyed: " + e.message) }
            var x = [1, 2]
            delete x
            try { print(x[0]) } catch (e) { print("after delete: " + e.message) }
            try { print(x.length) } catch (e) { print("len: " + e.message) }
            var m = new int[2][3]
            m[1][2] = 9
            print(m[1][2])
            delete m
            try { print(m[0]) } catch (e) { print("matrix gone") }
            var b = new byte[4]
            b[1] = 5
            b.TakeGlobal()
            print(b[1])
            {
                var inner = [9, 9]
                inner.TakeLocal()
                var r2 = new Res("r2")
                r2.TakeLocal()
            }
            print("end")
            var rr = new Res("kept")
            delete rr
            print("last")
            {
                var tmp = [1]
                tmp.TakeGlobal()
                var g = tmp
            }
            print("done")
            class Cell { int vals[]; construct() { this.vals = new int[3]; this.vals[0] = 5 } }
            var c = new Cell()
            print(c.vals[0])
            var grid = [[1, 2], [3, 4]]
            print(grid[1][0])
            class Fn { static int[][] Make() { return [[7, 8], [9]] } }
            print(Fn.Make()[0][1])
            var words = "a,b,c".Split(",")
            print(words.length)
            """),
        ("Besitz: Kaskade, TakeUpwards, TakeTo, innere Arrays, List-Wachstum, Ausnahmen", """
            class Node {
                string name
                int items[]
                construct(string n) { this.name = n; this.items = new int[2]; print("new " + n) }
                destruct() { print("free " + this.name) }
            }
            class Keeper {
                int store[]
                Node kid
                construct() { this.store = [1, 2, 3]; this.kid = new Node("kid") }
            }
            class Util {
                static int[] Up() {
                    var a = [7, 8]
                    {
                        var inner = new int[3]
                        inner.TakeUpwards()
                        inner[0] = 5
                        a[0] = inner[0]
                    }
                    return a
                }
                static int Sum(int xs[]) { var t = 0; foreach (x in xs) { t = t + x } return t }
            }
            var k = new Keeper()
            print(k.store[2])
            var kept = k.store
            delete k
            try { print(kept[0]) } catch (DestroyedException e) { print("store died with its owner") }
            var up = Util.Up()
            print(up[0])
            var big = new int[3][2]
            big[2][1] = 4
            print(big[2][1] + " " + big.length + " " + big[0].length)
            var g = [[1, 2], [3, 4, 5]]
            print(Util.Sum(g[1]))
            var buf = new byte[3]
            var holder = new Node("holder")
            buf.TakeTo(holder)
            holder.items.TakeGlobal()
            print(buf.length)
            delete holder
            try { print(buf.length) } catch (e) { print("buffer died with holder") }
            class Pool {
                static int[] Make(int n) {
                    var tmp = new int[n]
                    for (var i = 0; i < n; i = i + 1) { tmp[i] = i * i }
                    return tmp
                }
            }
            var total = 0
            for (var round = 0; round < 50; round = round + 1) {
                var a = Pool.Make(20)
                total = total + a[19]
                var s = [a[1], a[2]]
                total = total + s[1]
            }
            print(total)
            var l = new List()
            for (var i = 0; i < 40; i = i + 1) { l.Add(i) }
            var sum = 0
            foreach (v in l) { sum = sum + v }
            print(sum + " " + l.count)
            try {
                var local = new int[4]
                local[9] = 1
            } catch (e) {
                print("oob")
            }
            var cur = 0
            {
                var scratch = [1, 2]
                scratch.TakeLocal()
                cur = scratch[1]
            }
            print(cur)
            """),
        ("Besitz: Zuweisung nach oben, Direktzuweisung von Aufrufergebnissen, Rueckgabe direkt in einen Parameter", """
            class Box { int items[]; string name; construct(string n) { this.name = n } destruct() { print("free " + this.name) } }
            class Util {
                static int[] Make(int n) { var a = new int[n]; a[0] = n; return a }
                static Box MakeBox(string n) { return new Box(n) }
                static int Len(int xs[]) { return xs.length }
                static int[] Pass(int xs[]) { return xs }
                static int Probe(int xs[]) { return xs[0] }
                static int Loop() {
                    var last = [0]
                    for (var i = 1; i <= 3; i = i + 1) {
                        last = Make(i)
                        if (i == 2) { var x = Make(9); last = x }
                    }
                    return last[0]
                }
                static Box BoxLoop() {
                    var b = new Box("b0")
                    for (var i = 1; i <= 2; i = i + 1) { b = MakeBox("b" + i) }
                    return b
                }
            }
            print(Util.Loop())
            var kept = Util.BoxLoop()
            print(kept.name)
            // loop at top level, variable global
            var g = [0]
            for (var i = 1; i <= 3; i = i + 1) { g = Util.Make(i + 10) }
            print(g[0])
            if (true) { g = Util.Make(42) }
            print(g[0])
            // direct assignment of a call result to a field
            class Holder {
                int data[]
                Box child
                construct() { this.data = Util.Make(5); this.child = Util.MakeBox("child") }
            }
            var h = new Holder()
            print(h.data[0] + " " + h.child.name)
            // return value directly into a parameter: it belongs to the called function
            var survivor = Util.Make(3)
            print(Util.Len(Util.Make(4)))
            print(Util.Probe(Util.Pass(Util.Make(6))))
            try { print(Util.Pass(Util.Make(8))[0]) } catch (e) { print("died with the callee") }
            delete h
            print("end")
            """),
        ("return in ineinander liegenden finally-Bloecken und foreach", """
            class T {
                static int Nested(int n) {
                    try {
                        n = n + 0
                    } finally {
                        try {
                            try {
                            } finally {
                                if (n > 1) { return 1 }
                            }
                        } finally {
                            if (n > 0) { return 4 }
                        }
                    }
                }
                static int WithLoops(int n) {
                    var xs = [1, 2, 3]
                    try {
                        foreach (x in xs) {
                            try {
                                foreach (y in xs) {
                                    try { if (y == n) { return x * 10 + y } } finally { if (n == 3) { return 99 } }
                                }
                            } finally {
                                n = n + 0
                            }
                        }
                    } finally {
                        foreach (z in xs) { if (z == 2 && n == 0) { return 77 } }
                    }
                    return -1
                }
            }
            print(T.Nested(2))
            print(T.Nested(1))
            print(T.Nested(0))
            print(1 + T.WithLoops(1) + 2)
            print(T.WithLoops(2))
            print(T.WithLoops(3))
            print(T.WithLoops(0))
            """),
        ("Standardargumente: Methoden, Konstruktoren, base, Erweiterungen, Lambdas, virtuelle Aufrufe", """
            class Counter {
                static int n = 0
                static int Next() { Counter.n = Counter.n + 1; return Counter.n }
            }
            class Greeter {
                string greeting
                construct(string g = "Hi", int times = 2) { this.greeting = g; print("ctor " + g + " " + times) }
                string Greet(string name, string suffix = "!", int n = Counter.Next()) { return this.greeting + ", " + name + suffix + n }
                string Own(string mark = this.greeting + "?") { return mark }
                static int Sum(int a, int b = 10, int c = 100) { return a + b + c }
            }
            class Loud : Greeter {
                construct(string g = "HEY") : base(g) { }
                string Greet(string name, string suffix = "!!", int n = 7) { return base.Greet(name, suffix) + "/" + n }
            }
            class extends string {
                string Wrap(string left = "[", string right = "]") { return left + this + right }
            }
            var g = new Greeter()
            var h = new Greeter("Yo")
            var k = new Greeter("Hello", 5)
            var l = new Loud()
            print(g.Greet("Ann"))
            print(g.Greet("Bob", "?"))
            print(h.Greet("Cy", ".", 3))
            print(l.Greet("Di"))
            print(l.Greet("Ed", "~"))
            print(h.Own())
            print(h.Own("x"))
            print(Greeter.Sum(1))
            print(Greeter.Sum(1, 2))
            print(Greeter.Sum(1, 2, 3))
            print("abc".Wrap())
            print("abc".Wrap("<"))
            print("abc".Wrap("<", ">"))
            var f = func (x, y = 10) => { return x + y }
            print(f(5))
            print(f(5, 20))
            var q = func (int x = 42, string s = "z") => { return s + x }
            print(q())
            print(q(7))
            print(q(7, "a"))
            var list = [g, h, l]
            var total = 0
            for (var i = 0; i < 3; i = i + 1) { print(list[i].Greet("Zed")) }
            """),
        ("Properties: get/set, nur lesen/schreiben, vererbt, statisch, ueber den Dispatcher", """
            class Circle {
                float radius
                string label = "c"
                construct(float radius) { this.radius = radius }
                float Diameter {
                    get { return this.radius * 2 }
                    set { this.radius = value / 2 }
                }
                float Area { get { return this.radius * this.radius * 3 } }
                string Label {
                    get { return "<" + this.label + ">" }
                    set { this.label = value + "!" }
                }
                string Secret { set { this.label = "secret " + value } }
                static int count = 0
                static int Count { get { return Circle.count } set { Circle.count = value * 10 } }
            }
            class Ring : Circle {
                float hole = 1.0
                construct(float r) : base(r) { }
                float Width { get { return this.radius - this.hole } }
                float Area { get { return this.radius * 3 - 3 } }
            }
            var c = new Circle(5.0)
            print(c.Diameter)
            c.Diameter = 20.0
            print(c.radius)
            print(c.Area)
            print(c.Label)
            c.Label = "big"
            print(c.Label)
            c.Secret = "x"
            print(c.label)
            c.radius = c.Diameter + 1
            print(c.radius)
            var r = new Ring(4.0)
            print(r.Width)
            print(r.Area)
            print(r.Diameter)
            r.Diameter = 6.0
            print(r.Width)
            Circle.Count = 3
            print(Circle.Count)
            var all = [c, r]
            var sum = 0.0
            for (var i = 0; i < 2; i = i + 1) { sum = sum + all[i].Diameter }
            print(sum)
            """),
        ("Operator-Ueberladung: Arithmetik, Vergleiche, ==/!= getrennt, Rueckfall auf die eingebaute Operation, in Bedingungen", """
            class Vec {
                float x
                float y
                construct(float x, float y) { this.x = x; this.y = y }
                operator+(class o) { return new Vec(this.x + o.x, this.y + o.y) }
                operator-(class o) { return new Vec(this.x - o.x, this.y - o.y) }
                operator*(float k) { return new Vec(this.x * k, this.y * k) }
                operator==(class o) { return this.x == o.x && this.y == o.y }
                operator<(class o) { return this.x * this.x + this.y * this.y < o.x * o.x + o.y * o.y }
                operator>(class o) { return o < this }
                string ToString() { return "(" + this.x + "," + this.y + ")" }
            }
            class Money {
                int cents
                construct(int c) { this.cents = c }
                operator+(class o) { return new Money(this.cents + o.cents) }
                operator!=(class o) { return this.cents != o.cents }
                operator<<(int n) { return new Money(this.cents << n) }
                operator%(int n) { return this.cents % n }
                string ToString() { return "$" + this.cents }
            }
            class Plain { int v; construct(int v) { this.v = v } }
            var a = new Vec(1.0, 2.0)
            var b = new Vec(3.0, 4.0)
            print((a + b).ToString())
            print((b - a).ToString())
            print((a * 2.0).ToString())
            print(a == b)
            print(a == new Vec(1.0, 2.0))
            print(a < b)
            print(a > b)
            var m = new Money(250) + new Money(50)
            print(m.ToString())
            print(m != new Money(300))
            print(m != new Money(1))
            print((m << 2).ToString())
            print(m % 7)
            var p = new Plain(1)
            var q = new Plain(1)
            print(p == q)
            print(p == p)
            print(p != q)
            print(1 + 2)
            print("a" + "b")
            print("v=" + a)
            if (a < b) { print("lt") } else { print("ge") }
            if (b < a) { print("lt") } else { print("ge") }
            var s = a
            for (var i = 0; i < 3; i = i + 1) { s = s + b }
            print(s.ToString())
            print(m + new Money(1))
            """),
        ("Einheiten: Umrechnung, Algebra (m*m, m/s), Vergleiche, Coercing mit : und !", """
            var a = 500mm
            print(a + 2m)
            print(2m + a)
            print(a * 2m)
            print(2m * 3m)
            print(1m / 1mm)
            print(3km / 2h)
            print(2m < 300cm)
            print(5kg:g)
            print(1.5 * 2m)
            print((2m * 3m * 4m))
            print(7mm % 2m)
            print(2m ^ 2m)
            print(a == 500mm)
            print(a == 0.5m)
            print(1.5m + 100cm)
            var x = 10m
            var y = 3s
            var v = x / y
            print(v)
            print(v * 6s)
            print(5mm is in m)
            print(5mm is in kg)
            print(5 is of int)
            var q = 7m
            print(q:mm)
            print(q:cm!)
            var w = 5mm
            print(w:cm)
            var z = 5
            print(z!float)
            print(1.5km!int)
            """),
        ("is of / is in / is from / is under, Potenz, Typ-Coercing", """
            interface Shape { }
            class Animal { }
            class Dog : Animal { }
            class Sq : Shape { }
            class Holder { Animal pet; Holder inner
                construct() { this.pet = new Dog(); this.inner = new Holder2() } }
            class Holder2 { Animal pet
                construct() { this.pet = new Animal() } }
            var d = new Dog()
            var s = new Sq()
            print(d is of Animal)
            print(d is of Dog)
            print(s is of Animal)
            print(s is of Shape)
            print(d is of class)
            print(5 is of int)
            print(5 is of float)
            print(2.5 is of float)
            print("x" is of string)
            print([1, 2] is of IEnumerable)
            print(undefined is of undefined)
            print(5mm is in m)
            print(5mm is in kg)
            print(5 is in m)
            print(3kg is in g)
            var h = new Holder()
            print(h.pet is from h)
            print(h.inner is from h)
            print(h.inner.pet is from h)
            print(h.inner.pet is under h)
            print(h.inner.pet is from h.inner)
            print(h is from h.inner)
            print(2 ^ 10)
            print(2.0 ^ 0.5)
            print(2 ^ -1)
            print(3m ^ 2m)
            var i = 7
            print(i!float / 2)
            print(i / 2)
            var f = 7.5
            print(f!int)
            print(2.5!int)
            print(3.5!int)
            """),
        ("Zahlenformat E und F, X, B im Zusammenspiel", """
            var x = 12345.678
            print($"{x:E}")
            print($"{x:E2}")
            print($"{x:e3}")
            print($"{x:E0}")
            var small = 0.000123
            print($"{small:E3}")
            var n = 42
            print($"{n:E}")
            print($"{n:E1}")
            var z = 0.0
            print($"{z:E2}")
            print($"{x:F}")
            print($"{x:F1}")
            print($"{n:X4}")
            print($"{n:B8}")
            var big = 1500000000000.0 * 1000000000000.0
            print($"{big:E2}")
            var neg = -0.5
            print($"{neg:E1}")
            """),
        ("copy und flat: Objekte, Zyklen, Arrays, Puffer, Feldzuweisung, als Argument (die Kopie gehoert der aufgerufenen Funktion)", """
            class Leaf { string name; int data[]
                construct(string n) { this.name = n; this.data = new int[2]; this.data[0] = 7 }
                destruct() { print("~" + this.name) } }
            class Node { string label; Leaf leaf; Node next; int nums[]
                construct(string l) { this.label = l; this.leaf = new Leaf(l + "-leaf"); this.nums = [1, 2, 3] }
                destruct() { print("~" + this.label) } }
            class Box { Node held
                construct(Node n) { this.held = copy n }
                Node Same() { return this.held } }
            class Eat {
                static string Consume(Node n) { n.label = "eaten"; return n.label }
                static Node Keep(Node n) { return n }
                static int Sum(int xs[]) { var t = 0; for (var i = 0; i < xs.length; i = i + 1) { t = t + xs[i] }
                    return t }
            }
            var a = new Node("a")
            var f = flat a
            var c = copy a
            print(f.label + " " + (f.leaf == a.leaf) + " " + (f.nums == a.nums))
            print(c.label + " " + (c.leaf == a.leaf) + " " + (c.nums == a.nums))
            c.leaf.name = "changed"
            print(a.leaf.name)
            f.label = "f2"
            print(a.label)
            a.next = a
            var cyc = copy a
            print(cyc.next == cyc)
            print(cyc.next == a)
            var b = new Box(a)
            print(b.held.label + " " + (b.held == a))
            print(Eat.Consume(copy a))
            print(a.label)
            print(Eat.Consume(flat a))
            var kept = Eat.Keep(copy a)
            print(kept.label)
            var arr = [1, 2, 3]
            var arr2 = copy arr
            arr2[0] = 99
            print(arr[0] + " " + arr2[0])
            var fa = flat arr
            fa[1] = 5
            print(arr[1] + " " + Eat.Sum(copy arr))
            var buf = new byte[4]
            buf[0] = 9
            var buf2 = copy buf
            buf2[0] = 1
            print(buf[0] + " " + buf2[0])
            var nested = [[1, 2], [3]]
            var n2 = copy nested
            n2[0][0] = 50
            print(nested[0][0] + " " + n2[0][0])
            var s = copy "text"
            print(s)
            var q = copy 5
            print(q)
            print("end")
            """),
        ("Zugriffsmodifikatoren: private/protected bei Feldern, Methoden, statischen Mitgliedern, Properties, Konstruktoren", """
            class Base {
                private int secret
                protected int shared
                int open
                construct() { this.secret = 1; this.shared = 2; this.open = 3 }
                private int Hidden() { return this.secret * 10 }
                protected int Guarded() { return this.shared * 10 }
                int Reveal() { return this.Hidden() + this.Guarded() }
                private static int Counter() { return 7 }
                static int PublicCounter() { return Base.Counter() }
                int Total { get { return this.secret + this.shared } set { this.secret = value } }
                private int Hush { get { return this.secret } set { this.secret = value } }
                Reset() { this.Total = 100; this.Hush = 3 }
            }
            class Child : Base {
                int Peek() { return this.shared + this.Guarded() }
                int PeekSecret() { return this.secret }
            }
            class Locked {
                private construct() { }
                static Locked Make() { return new Locked() }
            }
            var b = new Base()
            var c = new Child()
            print(b.open)
            print(b.Reveal())
            print(c.Peek())
            print(Base.PublicCounter())
            try { print(b.secret) } catch (AccessDeniedException e) { print("denied 1: " + e.message) }
            try { b.secret = 5 } catch (AccessDeniedException e) { print("denied 2: " + e.message) }
            try { print(b.shared) } catch (AccessDeniedException e) { print("denied 3: " + e.message) }
            try { print(b.Hidden()) } catch (AccessDeniedException e) { print("denied 4: " + e.message) }
            try { print(b.Guarded()) } catch (AccessDeniedException e) { print("denied 5: " + e.message) }
            try { print(Base.Counter()) } catch (AccessDeniedException e) { print("denied 6: " + e.message) }
            try { print(c.PeekSecret()) } catch (AccessDeniedException e) { print("denied 7: " + e.message) }
            try { b.Hush = 5 } catch (AccessDeniedException e) { print("denied 8: " + e.message) }
            try { print(b.Hush) } catch (AccessDeniedException e) { print("denied 8b: " + e.message) }
            try { var l = new Locked() } catch (AccessDeniedException e) { print("denied 9: " + e.message) }
            print(Locked.Make() is of Locked)
            print(b.Total)
            b.Reset()
            print(b.Total)
            var lam = func () => { return b.open }
            print(lam())
            """),
        ("Zugriffsmodifikatoren: #performance prueft nicht", """
            #performance
            class Base {
                private int secret
                protected int shared
                int open
                construct() { this.secret = 1; this.shared = 2; this.open = 3 }
                private int Hidden() { return this.secret * 10 }
                protected int Guarded() { return this.shared * 10 }
                int Reveal() { return this.Hidden() + this.Guarded() }
                private static int Counter() { return 7 }
                static int PublicCounter() { return Base.Counter() }
                int Total { get { return this.secret + this.shared } set { this.secret = value } }
                private int Hush { get { return this.secret } set { this.secret = value } }
                Reset() { this.Total = 100; this.Hush = 3 }
            }
            class Child : Base {
                int Peek() { return this.shared + this.Guarded() }
                int PeekSecret() { return this.secret }
            }
            class Locked {
                private construct() { }
                static Locked Make() { return new Locked() }
            }
            var b = new Base()
            var c = new Child()
            print(b.open)
            print(b.Reveal())
            print(c.Peek())
            print(Base.PublicCounter())
            try { print(b.secret) } catch (AccessDeniedException e) { print("denied 1: " + e.message) }
            try { b.secret = 5 } catch (AccessDeniedException e) { print("denied 2: " + e.message) }
            try { print(b.shared) } catch (AccessDeniedException e) { print("denied 3: " + e.message) }
            try { print(b.Hidden()) } catch (AccessDeniedException e) { print("denied 4: " + e.message) }
            try { print(b.Guarded()) } catch (AccessDeniedException e) { print("denied 5: " + e.message) }
            try { print(Base.Counter()) } catch (AccessDeniedException e) { print("denied 6: " + e.message) }
            try { print(c.PeekSecret()) } catch (AccessDeniedException e) { print("denied 7: " + e.message) }
            try { b.Hush = 5 } catch (AccessDeniedException e) { print("denied 8: " + e.message) }
            try { print(b.Hush) } catch (AccessDeniedException e) { print("denied 8b: " + e.message) }
            try { var l = new Locked() } catch (AccessDeniedException e) { print("denied 9: " + e.message) }
            print(Locked.Make() is of Locked)
            print(b.Total)
            b.Reset()
            print(b.Total)
            var lam = func () => { return b.open }
            print(lam())
            """),
        ("Reflection: Type, Member, Get/Set/Call/New/Has, Selektoren, Zugriffsregeln; probe/silence", """
            #import "reflection"
            class Circle {
                float radius
                private int secret
                string label = "c"
                construct(float r) { this.radius = r; this.secret = 42 }
                float Diameter { get { return this.radius * 2 } set { this.radius = value / 2 } }
                float Area { get { return this.radius * this.radius * 3 } }
                float Scale(float k) { return this.radius * k }
                Grow() { this.radius = this.radius + 1 }
            }
            class Ring : Circle {
                float hole
                construct(float r, float h) : base(r) { this.hole = h }
            }
            var c = new Circle(5.0)
            var t = Type.Of(c)
            print(t.Name)
            print(t.Base == undefined)
            print(t.Fields().count)
            print(t.Properties().count)
            var names = ""
            foreach (m in t.All) { names = names + m.Kind + ":" + m.Name + " " }
            print(names)
            print(Reflect.Get(c, "radius"))
            print(Reflect.Get(c, "Diameter"))
            Reflect.Set(c, "Diameter", 20.0)
            print(c.radius)
            print(Reflect.Call(c, "Scale", [2.0]))
            Reflect.Call(c, "Grow", [])
            print(c.radius)
            print(Reflect.Has(c, "radius") + " " + Reflect.Has(c, "nothing") + " " + Reflect.Has(c, "Area") + " " + Reflect.Has(c, "Grow"))
            var r = Reflect.New("Ring", [3.0, 1.0])
            print(Type.Of(r).Name + " " + r.hole)
            print(Type.Of(r).Base.Name)
            print(Type.Of(r).IsSubclassOf(Type.Of(c)))
            print(Type.Named("Nope") == undefined)
            try { Reflect.Get(c, "zzz") } catch (e) { print(e.message) }
            try { Reflect.Set(c, "Area", 1.0) } catch (e) { print(e.message) }
            try { Reflect.Call(c, "Nope", []) } catch (e) { print(e.message) }
            try { Reflect.New("Circle", []) } catch (e) { print(e.message) }
            try { Reflect.Get(5, "x") } catch (e) { print(e.message) }
            try { print(Reflect.Get(c, "secret")) } catch (e) { print("private: " + e.message) }
            var cl = Type.Names()
            print(cl.length > 3)
            class W {
                static Watch(lambda field<Circle> sel, Circle x) {
                    print(sel.Name + " " + sel.Kind)
                    print(sel.Get(x))
                }
            }
            W.Watch(q => q.radius, c)
            var mem = Type.Of(c).Find("Scale")
            print(mem.Kind + " " + mem.ParamCount() + " " + mem.TypeName)
            print(mem.Call(c, [3.0]))
            class Cfg {
                int volume
                string name
                Cfg sub
                construct() { this.volume = 1; this.name = "n" }
                int Level { get { return this.volume * 10 } set { this.volume = value / 10 } }
            }
            var cfg = new Cfg()
            var h1 = probe cfg.volume changed { print("changed: " + old + " -> " + value) }
            var h2 = probe cfg.volume changing (o, n) => n <= 100
            cfg.volume = 5
            cfg.volume = 500
            print(cfg.volume)
            cfg.volume = 5
            probe cfg.name changed (obj, member, o, n) => print(member + ": " + o + " -> " + n)
            cfg.name = "other"
            cfg.name = "other"
            silence h1
            cfg.volume = 7
            print(cfg.volume)
            probe cfg.* changed (obj, member, o, n) => print("any " + member + " " + n)
            cfg.volume = 8
            cfg.name = "z"
            cfg.Level = 90
            silence cfg.*
            cfg.volume = 9
            print(cfg.volume)
            var h3 = Reflect.Probe(cfg, "volume", "changed", func (o, n) => print("reflect " + o + " " + n))
            cfg.volume = 10
            Reflect.SilenceHandle(h3)
            cfg.volume = 11
            try { Reflect.Probe(cfg, "nope", "changed", func () => 1) } catch (e) { print(e.message) }
            try { Reflect.Probe(cfg, "volume", "weird", func () => 1) } catch (e) { print(e.message) }
            var c2 = new Cfg()
            probe c2.volume changed { print("c2 " + value) }
            c2.volume = 4
            delete c2
            """),
        ("Besitz: #performance prueft zerstoerte Arrays nicht (FIRE_UNCHECKED), Ergebnis wie die VM", """
            #performance
            var a = new int[100]
            for (var i = 0; i < 100; i = i + 1) { a[i] = i }
            var m = new int[4][4]
            m[3][3] = 9
            var b = [[1, 2], [3]]
            print(a[99] + m[3][3] + b[1][0])
            """),
        ("Benchmark alloc", """
            class Point {
                int x
                int y
                construct(int x, int y) { this.x = x; this.y = y }
            }
            var sum = 0
            for (var i = 0; i < 60000; i = i + 1) {
                var p = new Point(i, i + 1)
                sum = sum + p.x + p.y
            }
            print(sum)
            """),
        ("Benchmark loop", """
            var sum = 0
            for (var i = 0; i < 1500000; i = i + 1) {
                sum = sum + i % 7
            }
            print(sum)
            """),
        ("Benchmark float", """
            var x = 0.0
            for (var i = 0; i < 600000; i = i + 1) {
                x = x + i * 0.5 - x / 3.0
            }
            print(x)
            """),
        ("Benchmark fib", """
            class M {
                static int Fib(int n) {
                    if (n < 2) { return n }
                    return M.Fib(n - 1) + M.Fib(n - 2)
                }
            }
            print(M.Fib(23))
            """),
    };

    // extern: C functions of the C library (Linux; other systems name the library differently)
    if (OperatingSystem.IsLinux())
        natCases = natCases.Append(("extern: Funktionen der C-Bibliothek (Zahlen, Zeichenketten, Zeiger auf Variablen)", """
            #extern "libc.so.6"
            extern int abs(int n)
            extern int atoi(string s)
            extern int strlen(string s)
            extern string getenv(string name)
            extern int toupper(int c)
            extern float atof(string s)
            extern int sscanf(string text, string format, int* out)
            print(abs(-5))
            print(atoi("1234") + 1)
            print(strlen("hello"))
            print(strlen("äö"))
            print(getenv("FIRE_SURELY_UNSET_VARIABLE") == undefined)
            print(toupper(97))
            print(atof("2.5") * 2)
            unsafe {
                var n = 0
                var matched = sscanf("42", "%ld", &n)
                print(matched)
                print(n)
            }
            """)).ToArray();

    // Fire threads (THREADING_DESIGN): only programs whose output does not depend on how the threads interleave
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Threads: leave im Hauptprogramm wartet auf Fire-Threads, dann werden die Globals zerstoert", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            var g = new Item(1)
            fire {
                var i = 0
                while (i < 300000) { i = i + 1 }
                print("thread fertig")
            }
            leave
            print("nie")
            """),
        ("Threads: terminate im Hauptprogramm stoppt Fire-Threads (finally laeuft), Globals zuletzt", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            var g = new Item(1)
            fire {
                try { while (true) { } } finally { print("thread finally") }
            }
            var j = 0
            while (j < 1000) { j = j + 1 }
            terminate(5)
            print("nie")
            """),
        ("Threads: terminate in einem Fire-Thread stoppt auch das Hauptprogramm", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            var g = new Item(1)
            fire {
                var k = 0
                while (k < 1000) { k = k + 1 }
                terminate(3)
                print("nie im thread")
            }
            try { while (true) { } } finally { print("main finally") }
            print("nie")
            """),
        ("Threads: terminate in einer Property haelt sofort an, danach geordnet", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            class P {
                int v {
                    get {
                        print("im getter")
                        terminate(1)
                        print("nie getter")
                        return 5
                    }
                }
            }
            var g = new Item(1)
            var p = new P()
            try {
                var x = p.v
                print("nie x")
            } finally {
                print("finally")
            }
            print("nie")
            """),
        ("Threads: Programmende wartet auf Fire-Threads, dann werden die Globals zerstoert", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            var g = new Item(1)
            fire {
                var i = 0
                while (i < 300000) { i = i + 1 }
                print("thread fertig")
            }
            print("main fertig")
            """),
        ("Threads: catch terminate(v) laeuft im Hauptprogramm", """
            catch terminate(v)
            {
                print("Main-Thread: catch terminate(v) -> " + v)
            }
            var i = 0
            while (i < 2000000) {
                i = i + 1
                if (i == 5) {
                    terminate(77)
                }
            }
            print("NIE ERREICHT")
            """),
        ("Threads: catch threads() faengt die Ausnahme eines Threads", """
            catch threads()
            {
                print("Main-Thread: catch threads() gefangen")
            }
            class MyError {
                string message
                construct(string message) { this.message = message }
            }
            fire {
                throw new MyError("boom aus echter Sprachsyntax")
            }
            var i = 0
            while (i < 2000000) {
                i = i + 1
            }
            print("Hauptprogramm fertig")
            """),
        ("Threads: taking: der Thread bekommt eine isolierte Kopie, die Zerstoerung ruft keinen Destruktor", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            var b = new Box("orig")
            b.item = new Item(5)
            fire taking b {
                print("thread sieht " + b.name + " " + b.item.n)
                b.name = "geaendert"
                b.item.n = 6
            }
            var i = 0
            while (i < 3000000) { i = i + 1 }
            print("main " + b.name + " " + b.item.n)
            """),
        ("Threads: leave laeuft durch finally, kein catch faengt es", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            fire {
                try {
                    try { print("a"); leave; print("nie") } catch (e) { print("nie catch") } finally { print("fin1") }
                } finally { print("fin2") }
                print("nie")
            }
            var i = 0
            while (i < 3000000) { i = i + 1 }
            print("fertig")
            """),
        ("Threads: leave aus einer Funktion im Thread, finally laeuft", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            var Work = func (n) => {
                var it = new Item(n)
                if (n == 2) { leave }
                print("work " + n)
            }
            fire {
                var a = new Item(100)
                Work(1)
                try { Work(2) } catch (e) { print("nie") } finally { print("fin") }
                print("nie")
            }
            var i = 0
            while (i < 3000000) { i = i + 1 }
            print("fertig")
            """),
        ("Threads: Thread startet Thread", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            fire {
                print("outer")
                fire { print("inner") }
                var i = 0
                while (i < 1000000) { i = i + 1 }
                print("outer end")
            }
            var i = 0
            while (i < 6000000) { i = i + 1 }
            print("fertig")
            """),
        ("Threads: sync: die Kopie schreibt ins Original zurueck", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            var player = new Box("p")
            player.item = new Item(100)
            var done = 0
            fire taking player {
                player.item.n = player.item.n - 10
                var r = sync player
                print("sync = " + r)
                sync global { done = 1 }
            }
            while (done == 0) { sync globals }
            print("main item " + player.item.n)
            """),
        ("Threads: sync: Arrays und Objekte (Fall A/B/C)", """
            class Item {
                int n
                construct(int n) { this.n = n }
                destruct() { print("~I" + this.n) }
            }
            class Box {
                string name
                Item item
                Item extra
                construct(string name) { this.name = name }
                destruct() { print("~B" + this.name) }
            }
            class Inv {
                int gold
                var items
                Item best
                construct() { this.gold = 0; this.items = [1, 2]; this.best = new Item(1) }
            }
            var inv = new Inv()
            var done = 0
            fire taking inv {
                inv.gold = 5
                inv.items = [10, 20, 30]
                inv.best.n = 99
                sync inv
                sync global { done = 1 }
            }
            while (done == 0) { sync globals }
            print(inv.gold + " " + inv.items.length + " " + inv.best.n)
            for (var i = 0; i < inv.items.length; i = i + 1) { print(inv.items[i]) }
            """),
        ("Threads: Actor: fire with + process", """
            actor Logger {
                string lastMessage
                construct() {
                    this.lastMessage = ""
                }
                log(string msg) {
                    this.lastMessage = msg
                    print("Logger (Heimat-Thread): " + msg)
                }
            }
            var logger = new Logger()
            fire with logger {
                logger.log("hallo vom fire-Thread")
            }
            process logger
            print("Hauptprogramm: logger.lastMessage = " + logger.lastMessage)
            """),
        ("Threads: Actor: try process", """
            actor Counter {
                int value
                construct() {
                    this.value = 0
                }
                increment() {
                    this.value = this.value + 1
                }
            }
            var counter = new Counter()
            var before = try process counter
            print("try process VOR jeder Nachricht (erwartet false): " + before)
            fire with counter {
                counter.increment()
            }
            process counter
            print("Hauptprogramm: counter.value = " + counter.value)
            """),
        ("Threads: Actor: Aufruf ist immer eine Nachricht", """
            actor Greeter {
                string name
                construct(string name) {
                    this.name = name
                }
                greet() {
                    print("greet() ist jetzt gelaufen")
                }
            }
            var greeter = new Greeter("Welt")
            greeter.greet()
            print("Vor process: greet() ist noch NICHT gelaufen")
            process greeter
            print("Nach process: siehe oben")
            """),
        ("Threads: Actor: mehrere Nachrichten in Reihenfolge", """
            actor Worker {
                int sum
                int pending
                construct() { this.sum = 0; this.pending = 3 }
                add(int x) { this.sum = this.sum + x; this.pending = this.pending - 1 }
                done() { print("done " + this.sum) }
            }
            var w = new Worker()
            fire with w { for (var i = 1; i <= 3; i = i + 1) { w.add(i * 10) } w.done() }
            while (w.pending > 0 || true) {
                process w
                if (w.pending == 0) { break }
            }
            process w
            """),
        ("Threads: Globals: Schreiben erst bei sync globals (#nosync)", """
            #nosync
            var counter = 0
            fire { counter = 5 }
            var handled = 0
            while (handled == 0) { handled = sync globals }
            print("counter " + counter + " bearbeitet " + handled)
            """),
        ("Threads: Globals: #nosync haelt den Wert", """
            #nosync
            var counter = 0
            var seen = 0
            fire { counter = 5 }
            for (var i = 0; i < 200000; i = i + 1) { seen = seen + counter }
            print("gesehen " + seen)
            """),
        ("Threads: Globals: automatisches Abarbeiten", """
            var counter = 0
            fire { counter = 5 }
            var spins = 0
            while (counter == 0 && spins < 100000000) { spins = spins + 1 }
            print("counter " + counter)
            """),
        ("Threads: Globals: fire global und Methodenaufrufe ohne sync globals", """
            class Box { int n; construct() { this.n = 0 } Add(int d) { this.n = this.n + d } }
            var box = new Box()
            var done = 0
            fire { box.Add(2); box.Add(3); fire global { done = done + 1 } }
            var spins = 0
            while (done == 0 && spins < 100000000) { spins = spins + 1 }
            print("n " + box.n + " done " + done)
            """),
        ("Threads: Globals: Methodenaufruf wartet bei #nosync", """
            #nosync
            class Box { int n; construct() { this.n = 0 } Add(int d) { this.n = this.n + d } }
            var box = new Box()
            var finished = 0
            fire { box.Add(4) }
            for (var i = 0; i < 300000; i = i + 1) { finished = finished + box.n }
            print("vorher " + finished)
            while (box.n == 0) { sync globals }
            print("nachher " + box.n)
            """),
        ("Threads: Globals: der Thread liest live", """
            var flag = 0
            var done = 0
            fire {
                while (flag == 0) { }
                sync global { done = done + 1 }
            }
            flag = 1
            while (done == 0) { sync globals }
            print("fertig " + done)
            """),
        ("Threads: Globals: Methoden auf globalen Objekten laufen atomar", """
            class Counter {
                int n
                construct() { this.n = 0 }
                Inc() { var old = this.n; this.n = old + 1 }
            }
            var c = new Counter()
            var done = 0
            fire { for (var i = 0; i < 100; i = i + 1) { c.Inc() } sync global { done = done + 1 } }
            fire { for (var i = 0; i < 100; i = i + 1) { c.Inc() } sync global { done = done + 1 } }
            fire { for (var i = 0; i < 100; i = i + 1) { c.Inc() } sync global { done = done + 1 } }
            fire { for (var i = 0; i < 100; i = i + 1) { c.Inc() } sync global { done = done + 1 } }
            while (done < 4) { sync globals }
            print("n " + c.n)
            """),
        ("Threads: Globals: sync global ist atomar", """
            var total = 0
            var done = 0
            fire { for (var i = 0; i < 100; i = i + 1) { sync global { total = total + 1 } } sync global { done = done + 1 } }
            fire { for (var i = 0; i < 100; i = i + 1) { sync global { total = total + 1 } } sync global { done = done + 1 } }
            fire { for (var i = 0; i < 100; i = i + 1) { sync global { total = total + 1 } } sync global { done = done + 1 } }
            fire { for (var i = 0; i < 100; i = i + 1) { sync global { total = total + 1 } } sync global { done = done + 1 } }
            while (done < 4) { sync globals }
            print("total " + total)
            """),
        ("Threads: Globals: Block sieht Locals des Threads", """
            var total = 0
            var done = 0
            fire {
                var step = 7
                sync global { total = total + step }
                done = 1
            }
            while (done == 0) { sync globals }
            print("total " + total)
            """),
        ("Threads: Globals: fire global mit taking", """
            var total = 0
            var started = 0
            fire {
                var x = 7
                fire global taking x { total = total + x }
                fire global taking x { total = total + x * 10 }
            }
            while (total == 0) { sync globals }
            while (total < 77) { sync globals }
            print("total " + total)
            """),
        ("Threads: Globals: fire global mit Objekt als taking", """
            class Box { int v; construct(int v) { this.v = v } }
            var seen = 0
            fire {
                var b = new Box(5)
                fire global taking b { seen = seen + b.v }
                b.v = 100
            }
            while (seen == 0) { sync globals }
            print("seen " + seen)
            """),
        ("Threads: Globals: Programmende bedient Threads", """
            class G { int v; construct() { this.v = 1 } destruct() { print("~G " + this.v) } }
            var g = new G()
            fire { g.v = 9 }
            print("ende")
            """),
        ("Threads: Globals: Ausnahme im Block beendet die Sektion", """
            class Exception { string message; construct(string message = "") { this.message = message } }
            var total = 0
            var done = 0
            fire {
                try { sync global { total = total + 1; throw new Exception("x") } } catch (e) { }
                sync global { total = total + 10; done = 1 }
            }
            while (done == 0) { sync globals }
            print("total " + total)
            """),
        ("Threads: Globals: Array der Globals", """
            var arr = new int[5]
            var done = 0
            fire {
                for (var i = 0; i < 5; i = i + 1) { arr[i] = i * 2 }
                sync global { done = 1 }
            }
            while (done == 0) { sync globals }
            var sum = 0
            for (var i = 0; i < 5; i = i + 1) { sum = sum + arr[i] }
            print("sum " + sum)
            """),
        ("Threads: Globals: statisches Feld", """
            class Cfg { static int hits = 0 }
            var done = 0
            fire { Cfg.hits = 3; sync global { done = 1 } }
            while (done == 0) { sync globals }
            print("hits " + Cfg.hits)
            """),
        ("Threads: Globals: Thread startet Thread, beide schreiben", """
            var total = 0
            var done = 0
            fire {
                sync global { total = total + 1 }
                fire { sync global { total = total + 10; done = 1 } }
            }
            while (done == 0) { sync globals }
            print("total " + total)
            """),
        ("Threads: Globals: terminate im Thread beendet sync-globals-Schleife", """
            fire { terminate(1) }
            while (true) { sync globals }
            """),
        ("Threads: Globals: sync globals ohne Threads liefert 0", """
            print("n " + (sync globals))
            """),
        ("Threads: Globals: break/continue aus sync global", """
            var total = 0
            var done = 0
            fire {
                for (var i = 0; i < 10; i = i + 1) {
                    sync global { if (i == 3) { break } total = total + 1 }
                }
                for (var j = 0; j < 4; j = j + 1) {
                    sync global { if (j % 2 == 0) { continue } total = total + 10 }
                }
                sync global { done = 1 }
            }
            while (done == 0) { sync globals }
            print("total " + total)
            """),
    }).ToArray();

    // The IO bridge (bridges/fire_bridge_io.hpp); the console is tested separately (a program with input)
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("IO: Dateien, Verzeichnisse, Text, Fehler (FileNotFound, DirectoryNotFound, FileExists)", """
            #import "io"
            var dir = IO.Path.Combine(IO.Path.Temp(), "fire_io_test_i1")
            if (IO.Directory.Exists(dir)) { IO.Directory.Delete(dir, true) }
            IO.Directory.Create(IO.Path.Combine(dir, "sub/deeper"))
            print(IO.Directory.Exists(dir))
            var f = IO.Path.Combine(dir, "a.txt")
            IO.File.WriteAllText(f, "Hallo Welt\nZeile 2 äöü €\r\nZeile 3 é 😀\n\nletzte")
            print(IO.File.Exists(f) + " " + IO.File.Size(f))
            print(IO.File.ReadAllText(f))
            foreach (line in IO.File.ReadAllLines(f)) { print("[" + line + "]") }
            IO.File.AppendAllText(f, "\nangehaengt")
            print(IO.File.ReadAllLines(f).count)
            var reader = new IO.TextReader(f)
            print(reader.ReadLine())
            print(reader.ReadLine())
            reader.Close()
            var w = IO.File.CreateText(IO.Path.Combine(dir, "b.txt"))
            w.WriteLine("eins")
            w.WriteLine("zwei")
            w.Close()
            print(IO.File.ReadAllText(IO.Path.Combine(dir, "b.txt")))
            IO.File.Copy(f, IO.Path.Combine(dir, "sub/c.txt"))
            IO.File.Move(IO.Path.Combine(dir, "b.txt"), IO.Path.Combine(dir, "sub/deeper/d.txt"))
            foreach (p in IO.Directory.GetFiles(dir, "*", true)) { print(IO.Path.FileName(p)) }
            foreach (p in IO.Directory.GetFiles(dir, "*.txt")) { print("top " + IO.Path.FileName(p)) }
            foreach (p in IO.Directory.GetDirectories(dir, "*", true)) { print("dir " + IO.Path.FileName(p)) }
            try { IO.File.Copy(f, IO.Path.Combine(dir, "sub/c.txt")) } catch (IO.FileExistsException e) { print("exists") }
            IO.File.Copy(f, IO.Path.Combine(dir, "sub/c.txt"), true)
            try { IO.File.ReadAllText(IO.Path.Combine(dir, "nope.txt")) } catch (IO.FileNotFoundException e) { print("not found") }
            try { IO.File.WriteAllText(IO.Path.Combine(dir, "nodir/x.txt"), "x") } catch (IO.DirectoryNotFoundException e) { print("no dir") }
            try { IO.Directory.Delete(dir) } catch (IO.IOException e) { print("not empty") }
            var t = IO.File.ModifiedTime(f)
            print(t > 1000000000s)
            IO.File.Delete(f)
            IO.File.Delete(f)
            print(IO.File.Exists(f))
            IO.Directory.Delete(dir, true)
            print(IO.Directory.Exists(dir))
            """),
        ("IO: MemoryStream, UTF-8, Pfade", """
            #import "io"
            var m = new IO.MemoryStream()
            m.Write(IO.Utf8.GetBytes("0123456789"))
            print(m.Length + " " + m.Position)
            m.Position = 3
            var b = new byte[4]
            print(m.Read(b, 0, 4))
            print(IO.Utf8.GetString(b))
            print(m.Position)
            m.Seek(-2, IO.SeekOrigin.End)
            print(IO.Utf8.GetString(m.ReadAll()))
            m.Length = 5
            print(m.Length + " " + m.Position)
            m.Length = 8
            print(m.ToBuffer().length)
            var buf = m.ToBuffer()
            print(buf[0] + " " + buf[4] + " " + buf[5] + " " + buf[7])
            print(m.ReadByte())
            m.Position = 0
            print(m.ReadByte() + " " + m.ReadByte())
            m.Close()
            try { m.ReadByte() } catch (IO.IOException e) { print("closed") }
            print(IO.Utf8.GetString(IO.Utf8.GetBytes("äöü€😀")) == "äöü€😀")
            print(IO.Utf8.GetBytes("äöü€😀").length)
            var bad = new byte[5]
            bad[0] = 65
            bad[1] = 255
            bad[2] = 195
            bad[3] = 66
            bad[4] = 226
            print(IO.Utf8.GetString(bad).length)
            var bom = new byte[4]
            bom[0] = 239
            bom[1] = 187
            bom[2] = 191
            bom[3] = 65
            print(IO.Utf8.GetString(bom))
            print(IO.Path.Combine("a", "b") + " " + IO.Path.Combine("a/", "b") + " " + IO.Path.Combine("a", "/b") + " [" + IO.Path.Combine("", "b") + "]")
            print(IO.Path.FileName("/x/y/z.tar.gz") + " " + IO.Path.Stem("/x/y/z.tar.gz") + " " + IO.Path.Extension("/x/y/z.tar.gz"))
            print("[" + IO.Path.Extension(".gitignore") + "] [" + IO.Path.Stem(".gitignore") + "] [" + IO.Path.Extension("noext") + "] [" + IO.Path.Extension("dot.") + "] [" + IO.Path.FileName("dir/") + "]")
            print(IO.Path.Parent("/x/y/z") + "|" + IO.Path.Parent("/x") + "|" + IO.Path.Parent("x") + "|" + IO.Path.Parent("a/b/") + "|" + IO.Path.Parent("a//b") + "|" + IO.Path.Parent("/"))
            print(IO.Path.IsRooted("/x") + " " + IO.Path.IsRooted("x") + " " + IO.Path.Separator())
            print(IO.Path.FullPath("/a/b/../c/./d//e") )
            print(IO.Path.FullPath("x/../y") == IO.Path.Combine(IO.Directory.Current(), "y"))
            print(IO.Path.Temp().length > 1)
            print("done")
            """),
        ("IO: FileStream (Modi, Zugriff, Position, Laenge, Fehlercodes), TextReader/TextWriter", """
            #import "io"
            var dir = IO.Path.Combine(IO.Path.Temp(), "fire_io_test_i4")
            if (IO.Directory.Exists(dir)) { IO.Directory.Delete(dir, true) }
            IO.Directory.Create(dir)
            var p = IO.Path.Combine(dir, "data.bin")
            var s = new IO.FileStream(p, IO.FileMode.Create)
            print(s.CanRead + " " + s.CanWrite + " " + s.CanSeek)
            var data = new byte[10]
            for (var i = 0; i < 10; i++) { data[i] = i * 3 }
            print(s.Write(data, 0, 10))
            print(s.Length + " " + s.Position)
            s.Position = 2
            var chunk = new byte[4]
            print(s.Read(chunk, 0, 4) + " " + chunk[0] + " " + chunk[3])
            s.Seek(-3, IO.SeekOrigin.End)
            print(s.ReadByte() + " " + s.ReadByte() + " " + s.ReadByte() + " " + s.ReadByte())
            s.Position = 5
            s.WriteByte(200)
            s.Position = 5
            print(s.ReadByte())
            s.Length = 4
            print(s.Length + " " + s.Position)
            s.Length = 12
            s.Position = 0
            var all = s.ReadAll()
            print(all.length + " " + all[3] + " " + all[11])
            s.Flush()
            s.Close()
            try { s.Position } catch (IO.StreamClosedException e) { print("closed " + e.code) }
            var r = new IO.FileStream(p)
            print(r.CanRead + " " + r.CanWrite)
            try { r.Write(data, 0, 1) } catch (IO.IOException e) { print("read only " + e.code) }
            try { r.Seek(-5, IO.SeekOrigin.Begin) } catch (IO.IOException e) { print("before start " + e.code) }
            try { r.Read(chunk, 2, 4) } catch (IO.IOException e) { print("range " + e.code) }
            r.Close()
            var a = new IO.FileStream(p, IO.FileMode.Append)
            print(a.CanRead + " " + a.CanWrite + " " + a.Position)
            a.Write(data, 0, 3)
            print(a.Length)
            a.Close()
            try { var n = new IO.FileStream(p, IO.FileMode.CreateNew) } catch (IO.FileExistsException e) { print("exists " + e.code) }
            try { var n = new IO.FileStream(IO.Path.Combine(dir, "missing")) } catch (IO.FileNotFoundException e) { print("missing " + e.code) }
            try { var n = new IO.FileStream(IO.Path.Combine(dir, "nodir/x"), IO.FileMode.Create) } catch (IO.DirectoryNotFoundException e) { print("nodir " + e.code) }
            try { var n = new IO.FileStream(dir) } catch (IO.IOException e) { print("dir " + e.code) }
            try { var n = new IO.FileStream("  ") } catch (IO.IOException e) { print("blank " + e.code) }
            var oc = new IO.FileStream(IO.Path.Combine(dir, "oc.bin"), IO.FileMode.OpenOrCreate)
            oc.Write(data, 0, 2)
            oc.Close()
            var oc2 = new IO.FileStream(IO.Path.Combine(dir, "oc.bin"), IO.FileMode.OpenOrCreate, IO.FileAccess.ReadWrite)
            print(oc2.Length)
            oc2.Close()
            var w = new IO.FileStream(p, IO.FileMode.Open, IO.FileAccess.ReadWrite)
            w.Seek(0, IO.SeekOrigin.End)
            w.Write(data, 0, 1)
            w.Position = 0
            print(w.ReadByte())
            w.Close()
            print(IO.File.Size(p))
            IO.File.WriteAllLines(IO.Path.Combine(dir, "lines.txt"), ["alpha", "beta", "", "gamma"])
            var rd = new IO.TextReader(IO.Path.Combine(dir, "lines.txt"))
            print(rd.EndOfStream)
            var count = 0
            foreach (l in rd) { count = count + 1 }
            print(count + " " + rd.EndOfStream)
            rd.Close()
            var lines = IO.File.ReadAllLines(IO.Path.Combine(dir, "lines.txt"))
            print(lines.count + " [" + lines[2] + "] " + lines[3])
            var open = IO.File.OpenText(IO.Path.Combine(dir, "lines.txt"))
            print(open.ReadLine() + "|" + open.ReadAll().length)
            open.Close()
            try { open.ReadLine() } catch (IO.StreamClosedException e) { print("reader closed") }
            IO.File.AppendAllText(IO.Path.Combine(dir, "lines.txt"), "tail")
            print(IO.File.ReadAllText(IO.Path.Combine(dir, "lines.txt")).length)
            print(IO.Directory.GetFiles(dir).count)
            IO.Directory.Delete(dir, true)
            print(IO.Directory.Exists(dir))
            """),
    }).ToArray();

    // Graphics (bridges/fire_bridge_graphics.hpp)
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Grafik: Framebuffer, Zeichnen, Palette, Blit, Fehler, Slicer (Konsole)", """
            #import "graphics"
            // a checksum of the pixels, order dependent
            class Util {
                static Hash(Framebuffer fb) {
                    var bytes = fb.ReadBytes()
                    var h = 17
                    for (var i = 0; i < bytes.length; i++) { h = (h * 31 + bytes[i]) % 1000000007 }
                    return h
                }
            }
            var fb = new Framebuffer(64, 48)
            var con = new Renderer(fb)
            print(fb.Width() + "x" + fb.Height() + " mode " + fb.Mode() + " bytes " + fb.ByteCount())
            con.Clear()
            print("clear " + Util.Hash(fb))
            con.FillRect(2, 3, 10, 7, new SolidBrush(4))
            con.DrawRect(1, 1, 20, 15, new Pen(14))
            con.DrawLine(0, 0, 63, 47, new Pen(0xFF00FF00))
            con.DrawLine(0, 47, 63, 0, new Pen(12))
            con.DrawLine(5, 20, 50, 20, new Pen(0xFFFF8000))
            print("basic " + Util.Hash(fb))
            con.DrawCircle(30, 24, 10, new Pen(15))
            con.FillCircle(10, 35, 6, new SolidBrush(9))
            con.DrawEllipse(40, 30, 12, 5, new Pen(11))
            con.FillEllipse(45, 10, 8, 4, new SolidBrush(13))
            con.DrawCircle(0, 0, 0, new Pen(7))
            con.FillCircle(5, 5, 1, new SolidBrush(7))
            print("round " + Util.Hash(fb))
            con.DrawTriangle(2, 40, 14, 30, 22, 46, new Pen(10))
            con.FillTriangle(50, 40, 60, 30, 62, 46, new SolidBrush(3))
            con.DrawPolygon([5, 5, 25, 8, 20, 25, 8, 20], new Pen(2), true)
            con.DrawPolygon([30, 5, 40, 5, 35, 12], new Pen(5), false)
            con.FillPolygon([0, 0, 12, 4, 6, 12, 10, 20, 0, 14], new SolidBrush(6))
            print("poly " + Util.Hash(fb))
            con.FloodFill(20, 40, new SolidBrush(200))
            con.FloodFillBorder(60, 2, new SolidBrush(100), 14)
            print("flood " + Util.Hash(fb))
            con.SetColor(15, 1)
            con.Locate(0, 0)
            con.Print("Hello, fire!\nSecond line äöü ÿ")
            con.DrawText(3, 30, "Text", new SolidBrush(0xFF0000FF))
            con.DrawText(3, 38, "Bg", new SolidBrush(0xFFFFFFFF), new SolidBrush(0xFF800000))
            print("text " + Util.Hash(fb) + " " + con.CellWidth() + "x" + con.CellHeight())
            print(con.GetPixel(2, 3) + " " + con.GetPixel(200, 3) + " " + con.GetPixelIndex(2, 3))
            for (var i = 0; i < 8; i++) { con.Print("scroll " + i + "\n") }
            print("scroll " + Util.Hash(fb))

            // palette mode
            var pal = new Framebuffer(32, 24, ColorMode.Palette)
            var pcon = new Renderer(pal)
            pcon.Clear()
            pcon.FillRect(2, 2, 12, 9, new SolidBrush(4))
            pcon.DrawCircle(20, 12, 8, new Pen(0xFF3366FF))
            pcon.DrawLine(0, 23, 31, 0, new Pen(40))
            pcon.FillTriangle(3, 20, 10, 14, 14, 22, new SolidBrush(200))
            pcon.Print("Pal")
            pal.SetPaletteRgb(4, 10, 200, 30)
            print("pal " + Util.Hash(pal) + " " + pal.GetPaletteColor(4) + " " + pal.ReadPalette().length + " " + pal.ReadPalette(true).length)
            pal.TransparentIndex = 0
            print(pal.TransparentIndex)
            // blit between modes and with scaling / flipping
            con.Blit(pal, 40, 2)
            con.BlitScaled(pal, 0, 0, 32, 24, 0, 24, -48, 20, BlitMode.Transparent)
            con.BlitRegion(pal, 4, 4, 10, 8, 20, 36, BlitMode.Blend)
            pcon.Blit(fb, 0, 0, 0, 0)
            con.Blit(fb, 5, 5)
            print("blit " + Util.Hash(fb) + " " + Util.Hash(pal))
            var raw = new byte[16]
            for (var i = 0; i < 16; i++) { raw[i] = (i * 37) % 256 }
            raw[3] = 255
            raw[7] = 128
            raw[11] = 0
            raw[15] = 255
            var small = Framebuffer.FromPixels(2, 2, raw, ColorMode.Rgba)
            print(small.ReadByte(5) + " " + small.ReadBytes().length)
            small.WriteByte(0, 99)
            print(small.ReadByte(0))
            var sc = new Renderer(small)
            print(sc.GetPixel(0, 0) + " " + sc.GetPixel(1, 0) + " " + sc.GetPixel(0, 1))
            var idx = Framebuffer.FromPixels(2, 2, new byte[4], ColorMode.Palette)
            print(idx.Mode() + " " + idx.ByteCount())
            var mask = fb.ToMask(100, true, 128)
            print(mask.Mode() + " " + Util.Hash(mask) + " " + mask.TransparentIndex)
            try { Framebuffer.FromPixels(2, 2, new byte[3], ColorMode.Rgba) } catch (ImageException e) { print(e.message) }
            try { small.WriteBytes(new byte[3]) } catch (GraphicsException e) { print(e.message) }
            try { small.GetPaletteColor(300) } catch (GraphicsException e) { print(e.message) }
            try { small.WritePalette(new byte[10]) } catch (GraphicsException e) { print(e.message) }
            try { var bad = new Framebuffer(0, 5) } catch (HandleUnavailableException e) { print("bad size") }
            try { var bad = new Framebuffer(5, 5, 7) } catch (HandleUnavailableException e) { print("bad mode") }
            try { Framebuffer.FromImage(new byte[4]) } catch (ImageException e) { print(e.message) }
            try { Framebuffer.FromImage(new byte[0]) } catch (ImageException e) { print(e.message) }
            // the slicer
            var slicer = new Slicer(2.0, 1.0)
            var paths = slicer.Slice(mask)
            print(paths.count)
            var total = 0
            foreach (p in paths) { total = total + p.Count() }
            print(total)
            if (paths.count > 0) { print(paths[0].kind + " " + paths[0].closed + " " + paths[0].Count() + " " + paths[0].X(0) + " " + paths[0].Y(0)) }
            var zz = new Slicer(1.5, 1.0)
            zz.strategy = FillStrategy.ZigZag
            zz.flipY = false
            var zp = zz.Slice(mask)
            print(zp.count)
            """),
        ("Grafik: Bilder PNG/BMP/GIF aus Bytes", """
            #import "graphics"
            class Img {
                static Bytes(a) { var b = new byte[a.length]; for (var i = 0; i < a.length; i++) { b[i] = a[i] } return b }
                static Show(string name, data) {
                    var fb = Framebuffer.FromImage(data)
                    print(name + " " + fb.Width() + "x" + fb.Height() + " mode " + fb.Mode() + " transparent " + fb.TransparentIndex)
                    var bytes = fb.ReadBytes()
                    var line = ""
                    for (var i = 0; i < bytes.length; i++) { line = line + bytes[i] + " " }
                    print(line)
                    if (fb.Mode() == 1) { print("palette0 " + fb.GetPaletteColor(0) + " palette1 " + fb.GetPaletteColor(1)) }
                }
            }
            var png1 = Img.Bytes([137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,1,0,0,0,1,8,6,0,0,0,31,21,196,137,0,0,0,13,73,68,65,84,120,218,99,252,207,192,80,15,0,4,133,1,128,132,169,140,33,0,0,0,0,73,69,78,68,174,66,96,130])
            Img.Show("png1", png1)
            var gif = Img.Bytes([71,73,70,56,57,97,1,0,1,0,128,0,0,255,255,255,0,0,0,33,249,4,1,0,0,0,0,44,0,0,0,0,1,0,1,0,0,2,2,68,1,0,59])
            Img.Show("gif", gif)
            var bmp = Img.Bytes([66,77,78,0,0,0,0,0,0,0,54,0,0,0,40,0,0,0,3,0,0,0,2,0,0,0,1,0,24,0,0,0,0,0,24,0,0,0,19,11,0,0,19,11,0,0,0,0,0,0,0,0,0,0,30,20,10,60,50,40,90,80,70,0,0,0,0,0,255,0,255,0,255,0,0,0,0,0])
            Img.Show("bmp", bmp)
            var png2 = Img.Bytes([137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,4,0,0,0,3,8,2,0,0,0,59,150,57,145,0,0,0,41,73,68,65,84,120,156,13,197,49,1,0,32,0,195,176,42,65,201,148,84,201,148,160,100,2,33,79,0,194,41,25,130,39,166,58,11,75,254,235,118,31,169,95,11,245,222,135,168,195,0,0,0,0,73,69,78,68,174,66,96,130])
            Img.Show("png2", png2)
            """),
        ("Grafik: Bild aus einer Datei laden (IO-Richtlinie)", """
            #import "graphics"
            #import "io"
            class Img {
                static Bytes(a) { var b = new byte[a.length]; for (var i = 0; i < a.length; i++) { b[i] = a[i] } return b }
            }
            var bmp = Img.Bytes([66,77,78,0,0,0,0,0,0,0,54,0,0,0,40,0,0,0,3,0,0,0,2,0,0,0,1,0,24,0,0,0,0,0,24,0,0,0,19,11,0,0,19,11,0,0,0,0,0,0,0,0,0,0,30,20,10,60,50,40,90,80,70,0,0,0,0,0,255,0,255,0,255,0,0,0,0,0])
            var dir = IO.Path.Combine(IO.Path.Temp(), "fire_gfx_test_g3")
            IO.Directory.Create(dir)
            var path = IO.Path.Combine(dir, "t.bmp")
            IO.File.WriteAllBytes(path, bmp)
            var fb = Framebuffer.FromFile(path)
            print(fb.Width() + "x" + fb.Height() + " " + fb.ByteCount())
            var raw = fb.ReadBytes()
            var line = ""
            for (var i = 0; i < raw.length; i++) { line = line + raw[i] + " " }
            print(line)
            try { Framebuffer.FromFile(IO.Path.Combine(dir, "missing.png")) } catch (ImageException e) { print("missing " + (e.message.Length > 0)) }
            IO.File.Delete(path)
            IO.Directory.Delete(dir)
            """),
    }).ToArray();

    // Ownership (SPEC 2.2, 2.3): dead objects, what `return` takes along, `Takes`
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Besitz: zerstoerte Objekte sind tot, im Zerstoerungsstapel noch benutzbar, return nimmt den Baum der Locals mit", """
            class D {
                string n
                D next
                construct(string n) { this.n = n }
                destruct() { print("~" + this.n) }
            }
            class W {
                D target
                construct(D target) { this.target = target }
                destruct() { print("~W sees " + this.target.n) }
            }
            class T {
                static Dead() {
                    var a = new D("a")
                    return a.n
                }
                // the destructor of w uses d, which was destroyed before it in the same scope: allowed until the scope is gone
                static Batch() {
                    var d = new D("d")
                    var w = new W(d)
                }
                // a returned list takes its elements along
                static Items() {
                    var list = new List()
                    for (var i = 0; i < 3; i = i + 1) { var x = new D("i" + i); x.TakeTo(list); list.Add(x) }
                    return list
                }
                // by reference only: everything local travels with the returned object
                static Ring() {
                    var a = new D("ra")
                    var b = new D("rb")
                    a.next = b
                    b.next = a
                    return a
                }
                // not returned: gone
                static Lost() {
                    var keep = new D("lost")
                    return 1
                }
                // taken out of an owner that dies with the scope
                static Inner() {
                    var outer = new D("outer")
                    outer.next = new D("inner")
                    outer.next.TakeTo(outer)
                    return outer.next
                }
            }
            var x = new D("x")
            delete x
            try { print(x.n) } catch (DestroyedException e) { print("dead: " + e.message) }
            try { x.next = x } catch (DestroyedException e) { print("dead set") }
            T.Batch()
            print("batch done")
            var items = T.Items()
            print(items.count + " " + items[2].n)
            var ring = T.Ring()
            print(ring.next.next.n)
            T.Lost()
            print(T.Inner().n)
            print("end")
            """),
        ("Besitz: Takes.This/Children/Locals/All bei TakeUpwards/TakeTo/TakeGlobal und fuer Arrays", """
            class N {
                string name
                N next
                N other
                construct(string name) { this.name = name }
                destruct() { print("~" + this.name) }
            }
            class F {
                // This: the child stays behind and dies with the function
                static UpThis(holder) {
                    var p = new N("p1")
                    var q = new N("q1")
                    p.next = q
                    p.TakeUpwards()
                    holder.next = p
                }
                // Locals: the child travels along (to the object that points to it)
                static UpLocals(holder) {
                    var p = new N("p2")
                    var q = new N("q2")
                    p.next = q
                    p.TakeUpwards(Takes.Locals)
                    holder.next = p
                }
                // Children: what the fields point to directly, not what those point to
                static UpChildren(holder) {
                    var p = new N("p3")
                    var q = new N("q3")
                    var r = new N("r3")
                    p.next = q
                    q.next = r
                    p.TakeUpwards(Takes.Children)
                    holder.next = p
                }
                // All: also what is owned by somebody else
                static TakeAll(holder, foreign) {
                    var p = new N("p4")
                    p.other = foreign
                    p.TakeUpwards(Takes.All)
                    holder.next = p
                }
                // TakeTo with a mode, and TakeGlobal
                static ToObject(holder) {
                    var p = new N("p5")
                    var q = new N("q5")
                    p.next = q
                    p.TakeTo(holder, Takes.Locals)
                }
                static Global() {
                    var p = new N("p6")
                    var q = new N("q6")
                    p.next = q
                    p.TakeGlobal(Takes.Children)
                    return 0
                }
                // an array travels with the objects it holds
                static Arr() {
                    var a = [new N("a1"), new N("a2")]
                    a.TakeUpwards(Takes.Locals)
                    return a
                }
            }
            var h = new N("h")
            var foreign = new N("foreign")
            F.UpThis(h)
            print("1 " + h.next.name)
            try { print(h.next.next.name) } catch (DestroyedException e) { print("q1 dead") }
            F.UpLocals(h)
            print("2 " + h.next.next.name)
            F.UpChildren(h)
            print("3 " + h.next.next.name)
            try { print(h.next.next.next.name) } catch (DestroyedException e) { print("r3 dead") }
            F.TakeAll(h, foreign)
            print("4 " + h.next.other.name)
            F.ToObject(h)
            print("5 " + h.next.name)
            F.Global()
            print("6")
            var arr = F.Arr()
            print("7 " + arr[0].name + arr[1].name)
            print("end")
            """),
        ("Besitz: Liste mit Objekten aus einer Funktion, Zugriff auf ein geloeschtes Objekt", """
            class Item { int n
              construct(int n) { this.n = n }
              destruct() { print("~Item" + this.n) } }
            class F { static Make() {
                var l = new List()
                l.Add(new Item(1))
                l.Add(new Item(2))
                return l
            } }
            var l = F.Make()
            print("made " + l.count)
            print(l[0].n + l[1].n)
            class G { static Gone() { var i = new Item(9); return i }
              static Lost() { var a = new Item(7); return 1 } }
            var kept = G.Gone()
            print(kept.n)
            delete kept
            try { print(kept.n) } catch (DestroyedException e) { print("caught " + e.message) }
            class P { Item it
              construct() { this.it = new Item(5) } }
            var p = new P()
            var ref = p.it
            delete p
            try { print(ref.n) } catch (DestroyedException e) { print("caught2") }
            """),
    }).ToArray();

    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Besitz: try x.Take... und Takes.Children (Array, IEnumerable)", """
            class Item { string n
              construct(string n) { this.n = n }
              destruct() { print("~" + this.n) } }
            class Holder { var kept
              construct() { }
              // x is the result of a call passed straight on: it belongs to this method's scope, so the Holder may take it
              Adopt(x) { return try x.TakeTo(this) }
              AdoptNew(x) { return try x.TakeTo(this) }
              // the thing is owned by this object: only the owner moves it
              Release() { return try this.kept.TakeLocal() }
            }
            class F { static Make(string n) { return new Item(n) } }
            var h = new Holder()
            print(h.Adopt(F.Make("fresh")))
            var mine = new Item("mine")
            print(h.AdoptNew(mine))
            print(h.AdoptNew(new Item("tmp")))
            // children of a list: the items go to the list
            class Bag { var list
              construct() { this.list = new List() } }
            var items = new List()
            var a = new Item("la")
            var b = new Item("lb")
            items.Add(a)
            items.Add(b)
            var arr = [new Item("a1"), new Item("a2")]
            class T { static Run(items, arr) {
                items.TakeLocal(Takes.Children)
                arr.TakeLocal(Takes.Children)
                return 0
            } }
            T.Run(items, arr)
            print("end")
            """),
    }).ToArray();

    // Ownership: arrays own, call arguments, List.Take
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Besitz: TakeTo(list, Takes), TakeLocal", """
            class Item { string n
              construct(string n) { this.n = n }
              destruct() { print("~" + this.n) } }
            class F {
              static Fill() {
                var list = new List()
                for (var i = 0; i < 3; i++) { var it = new Item("i" + i); it.TakeTo(list); list.Add(it) }
                return list
              }
              static FillChildren() {
                var list = new List()
                var a = new Item("c1")
                var b = new Item("c2")
                list.Add(a)
                list.Add(b)
                a.TakeTo(list, Takes.Children)
                return list
              }
              static Local(holder) {
                var x = new Item("x")
                x.TakeLocal()
                print(try x.TakeLocal())
                return 0
              }
            }
            var l = F.Fill()
            print(l.count + " " + l[2].n)
            var l2 = F.FillChildren()
            print(l2.count)
            F.Local(0)
            print("end")
            """),
        ("Besitz: take als Argument und in Zuweisungen", """
            class Item { string n
              construct(string n) { this.n = n }
              destruct() { print("~" + this.n) } }
            class Box { Item slot
              items = [new Item("x0")]
              Put(x) { x.TakeTo(this); print("put " + x.n) }
              destruct() { print("~box") } }
            class T {
              static Eat(x) { var mine = new Item("mine"); print("eat " + x.n) }
              static Keep(x, b) { x.TakeTo(b); print("kept") }
              static Drop(x, b) { print(try x.TakeTo(b)) }
            }
            var a = new Item("a")
            T.Eat(take a)
            print("1")
            var box = new Box()
            var b = new Item("b")
            T.Keep(take b, box)
            print("2")
            var c = new Item("c")
            box.slot = take c
            var d = new Item("d")
            T.Drop(d, box)
            T.Drop(take d, box)
            print("3")
            {
              var inner = new Item("inner")
              var out = new Item("out")
              var x
              x = take out
              box.items[0] = take inner
            }
            print("4")
            var e = new Item("e")
            var f = take e
            print("5")
            try { print(a.n) } catch (DestroyedException ex) { print("dead a") }
            try { T.Eat(take a) } catch (DestroyedException ex) { print("dead take") }
            print("end")
            """),
        ("Besitz: take in Konstruktor, Rueckgabe, Array-Element", """
            class Item { string n
              construct(string n) { this.n = n }
              destruct() { print("~" + this.n) } }
            class Holder { Item kept
              construct(it) { this.kept = take it; print("holder " + this.kept.n) } 
              destruct() { print("~holder") } }
            class Cell { int v }
            class T {
              static Make(tag) { var t = new Item(tag); var u = take t; return u }
              static Num(x) { return x + 1 }
              static Fill(arr, a, b) {
                arr[0] = take a
                arr[1] = take b
                print("filled")
              }
            }
            var h
            {
              var it = new Item("i1")
              h = new Holder(take it)
            }
            print("1")
            var m = T.Make("m")
            print(m.n)
            print(T.Num(take 5))
            var arr = new Item[2]
            {
              var p = new Item("p")
              var q = new Item("q")
              T.Fill(arr, p, q)
              print("block end")
            }
            print("2")
            var data = new int[3]
            var holder2 = new Holder(new Item("fresh"))
            var ar2 = [new Item("z")]
            print(ar2[0].n)
            print("end")
            """),
        ("Besitz: try nimmt nur das Argument des eigenen Aufrufs", """
            class Item { string n
              construct(string n) { this.n = n }
              destruct() { print("~" + this.n) } }
            class T {
              static Make(n) { return new Item(n) }
              static Deep(x, b) { print("deep " + (try x.TakeTo(b))) }
              static Mid(x, b) { T.Deep(x, b); print("mid " + (try x.TakeTo(b))) }
              static Pass(x, b) { T.Mid(T.Make("inner"), b); T.Deep(take x, b) }
            }
            var box = new Item("box")
            T.Mid(T.Make("a"), box)
            var c = new Item("c")
            T.Mid(take c, box)
            T.Pass(new Item("p"), box)
            print("end")
            """),
        ("Besitz: Takes.Locals ueber den Methoden-Verteiler", """
            class Item { string n
              var child
              construct(string n) { this.n = n }
              destruct() { print("~" + this.n) } }
            class Fake { TakeTo(a, b) { print("fake") } }
            class T {
              static F(box) {
                var a = new Item("a")
                var b = new Item("b")
                a.child = b
                a.TakeTo(box, Takes.Locals)
                var c = new Item("c")
                print("F end")
              }
            }
            var box = new Item("box")
            T.F(box)
            print("after F")
            var f = new Fake()
            f.TakeTo(1, 2)
            print("end")
            """),
        ("Besitz: Ein Array besitzt, was return und Takes mitnehmen", """
            class Item { string n
              var arr
              construct(string n) { this.n = n }
              destruct() { print("~" + this.n) } }
            class F {
              static Make() {
                var a = [new Item("a1"), new Item("a2")]
                return a
              }
              static Inner() {
                var holder = new Item("holder")
                var a = [new Item("b1")]
                holder.arr = a
                a.TakeTo(holder)
                return a
              }
            }
            var arr = F.Make()
            arr.TakeGlobal()
            class G { static Run() { var x = F.Make(); print(x.length); return 0 } }
            G.Run()
            print("after G")
            var inner = F.Inner()
            print(inner[0].n)
            delete arr
            print("deleted")
            print("end")
            """),
        ("Besitz: Ein weitergereichtes Argument stirbt nach dem Aufruf, nach den Locals des Aufgerufenen", """
            class D { string n
              construct(string n) { this.n = n }
              destruct() { print("~" + this.n) } }
            class W { D d
              construct(D d) { this.d = d } }
            class F { static Make() { return new D("arg") }
              static Use(D x) { var local = new D("local"); print("in Use") }
              static Pass(D x) { return x } }
            F.Use(F.Make())
            print("after Use")
            var kept = F.Pass(F.Make())
            print("kept " + kept.n)
            print("end")
            """),
    }).ToArray();

    // Differences that were aligned
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Ausnahmen: Destruktor-Reihenfolge beim Verlassen eines catch (Wurfort zuerst, dann der catch)", """
            class D { string n
              construct(string n) { this.n = n }
              destruct() { print("~" + this.n) } }
            class Boom : Exception { string message
              construct(string m) { this.message = m } }
            class F {
              static Thrower() {
                var t = new D("thrower-local")
                throw new Boom("x")
              }
              static Run() {
                var outer = new D("outer-local")
                try {
                  var inTry = new D("in-try")
                  F.Thrower()
                } catch (Boom e) {
                  var inCatch = new D("in-catch")
                  print("caught " + e.message)
                }
                print("after catch")
              }
            }
            F.Run()
            print("end")
            """),
        ("print eines Objekts ohne ToString zeigt die Klasse", """
            class P { int x
              construct() { this.x = 3 } }
            class Q { int y
              construct() { this.y = 4 }
              ToString() { return "Q(" + this.y + ")" } }
            var p = new P()
            var q = new Q()
            print(p)
            print(q)
            print("" + p)
            print("v " + q)
            """),
    }).ToArray();

    // Pointers (SPEC 8.3)
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Zeiger (unsafe): Variablen, Felder, ref-Parameter, Parameter vom Typ int*, Tausch", """
            class Box { int v = 1
              string s = "a" }
            class F {
              static Inc(int* p) { unsafe { *p = *p + 1 } }
              static Swap(int* a, int* b) { unsafe { var t = *a; *a = *b; *b = t } }
            }
            var x = 5
            unsafe {
                int* p = &x
                *p = *p + 10
                print(x)
                F.Inc(p)
                print(x)
                var y = 7
                F.Swap(&x, &y)
                print(x + " " + y)
                var b = new Box()
                int* pv = &b.v
                *pv = 42
                print(b.v)
                string* ps = &b.s
                *ps = *ps + "z"
                print(b.s)
                int* r = &x
                r = r + 0
                print(*r)
            }
            """),
        ("Zeiger (unsafe): Grenzen, Versatz bei Variablen und Feldern, Differenz", """
            class Box { int v = 1
              int w = 2 }
            class F {
              static Read(ref int first, int n) {
                var t = 0
                unsafe { int* p = &first
                  for (var i = 0; i < n; i++) { t = t + *p; p = p + 1 } }
                return t
              }
              static Back(ref int first) {
                var t = 0
                unsafe { int* p = &first
                  p = p + 2
                  t = *p; p = p - 1; t = t * 10 + *p; p = p - 1; t = t * 10 + *p
                  int* q = p + 2
                  print(q - p)
                  print(p == q - 2)
                  try { p = p - 1; print(*p) } catch (e) { print("before: " + e.message) }
                }
                return t
              }
              static Bytes(ref byte first, int n) {
                var t = 0
                unsafe { byte* p = &first
                  for (var i = 0; i < n; i++) { t = t + *p; *p = 0; p = p + 1 } }
                return t
              }
              static Gone(ref int first) {
                return first
              }
            }
            var a = [1, 2, 3]
            print(F.Read(a[1], 2))
            try { print(F.Read(a[1], 3)) } catch (e) { print("oob: " + e.message) }
            print(F.Back(a[0]))
            var buf = new byte[3]
            buf[0] = 5; buf[1] = 6; buf[2] = 7
            print(F.Bytes(buf[1], 2))
            try { print(F.Bytes(buf[1], 3)) } catch (e) { print("oob buf: " + e.message) }
            print(buf[1] + " " + buf[2])
            var x = 10
            var y = 20
            unsafe {
              int* p = &x
              try { print(*(p + 1)) } catch (e) { print("var: " + e.message) }
              int* q = p + 1
              try { *q = 99 } catch (e) { print("var write: " + e.message) }
              print(y)
              print(q == p)
              print(q - 1 == p)
              print(*(q - 1))
              var b = new Box()
              int* pf = &b.v
              try { int* pg = pf + 1; print(*pg) } catch (e) { print("field: " + e.message) }
              print(pf == pf + 0)
            }
            print("end")
            """),
        ("Zeiger (unsafe): Arithmetik ueber Array- und Puffer-Elemente (ref), Vergleich, Zeiger auf Zeiger", """
            class F {
              static Sum(ref int first, int n) {
                var t = 0
                unsafe {
                  int* p = &first
                  for (var i = 0; i < n; i++) { t = t + *p; p = p + 1 }
                }
                return t
              }
              static Zero(ref int first, int n) {
                unsafe {
                  int* p = &first
                  for (var i = 0; i < n; i++) { *p = 0; p = p + 1 }
                }
              }
              static Bytes(ref byte first, int n) {
                var t = 0
                unsafe {
                  byte* p = &first
                  for (var i = 0; i < n; i++) { t = t + *p; p = p + 1 }
                }
                return t
              }
            }
            var a = [1, 2, 3, 4, 5]
            print(F.Sum(a[1], 3))
            F.Zero(a[2], 2)
            print(a[0] + " " + a[1] + " " + a[2] + " " + a[3] + " " + a[4])
            var buf = new byte[4]
            buf[0] = 1; buf[1] = 2; buf[2] = 3; buf[3] = 4
            print(F.Bytes(buf[1], 3))
            var x = 1
            var y = 1
            unsafe {
              int* p = &x
              int* q = &x
              print(p == q)
              int* r = &y
              print(p == r)
              int** pp = &p
              **pp = 99
            }
            print(x)
            """),
    }).ToArray();

    // PseudoRandom (#import "random"): pure fire, the same numbers in the VM and in the native build
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Random: PseudoRandom - Folge fuer einen Seed, Bereiche, Mischen, Fehler", """
            #import "random"
            var r = new PseudoRandom(42)
            print(r.NextUInt32() + " " + r.NextUInt32() + " " + r.NextUInt32())
            print(new PseudoRandom(42).NextUInt32() == 2014437610)
            print(new PseudoRandom(43).NextUInt32() != 2014437610)
            var seen = new List()
            for (var i = 0; i < 6; i = i + 1) { seen.Add(0) }
            var inRange = true
            var floats = true
            var big = true
            var between = true
            for (var i = 0; i < 600; i = i + 1) {
                var d = r.Next(6)
                if (d < 0 || d > 5) { inRange = false } else { seen[d] = seen[d] + 1 }
                var f = r.NextFloat()
                if (f < 0.0 || f >= 1.0) { floats = false }
                var n = r.Next()
                if (n < 0 || n >= 2147483647) { inRange = false }
                var w = r.Next(10000000000)
                if (w < 0 || w >= 10000000000) { big = false }
                var m = r.Next(-5, 5)
                if (m < -5 || m >= 5) { between = false }
            }
            var all = true
            for (var i = 0; i < 6; i = i + 1) { if (seen[i] < 60) { all = false } }
            print("Bereiche " + inRange + " " + floats + " " + big + " " + between + " jede Seite oft genug " + all)
            var list = new List()
            for (var i = 0; i < 20; i = i + 1) { list.Add(i) }
            var shuffled = r.Shuffle(list)
            var sum = 0
            var moved = 0
            for (var i = 0; i < 20; i = i + 1) { sum = sum + shuffled[i]; if (shuffled[i] != i) { moved = moved + 1 } }
            print("Mischen " + shuffled.count + " " + sum + " " + (moved > 5))
            print(r.Pick(list) >= 0)
            print(r.Pick(new List()))
            var bools = 0
            for (var i = 0; i < 400; i = i + 1) { if (r.NextBool()) { bools = bools + 1 } }
            print("Bool " + (bools > 120 && bools < 280))
            var ints = r.NextInt() != r.NextInt()
            print("NextInt " + ints)
            var a = new PseudoRandom()
            print(a.Next() >= 0)
            try { r.Next(0) } catch (RandomException e) { print("Fehler " + e.message) }
            try { r.Next(5, 5) } catch (RandomException e) { print("Fehler " + e.message) }
            var s1 = new PseudoRandom(7)
            var s2 = new PseudoRandom(7)
            var same = true
            for (var i = 0; i < 100; i = i + 1) { if (s1.Next(1000) != s2.Next(1000)) { same = false } }
            print("gleicher Seed " + same)
            """),
    }).ToArray();

    // TLS (bridges/fire_bridge_tls.hpp) and HTTPS: a certificate for localhost is made here (the tests trust exactly it); needs the development files of OpenSSL on this machine
    {
        bool hasOpenSsl = new[] { "/usr/include/openssl/ssl.h", "/usr/local/include/openssl/ssl.h", "/opt/homebrew/include/openssl/ssl.h", "/usr/include/x86_64-linux-gnu/openssl/ssl.h" }.Any(File.Exists);
        if (!hasOpenSsl)
        {
            Console.WriteLine("(Tls/Https: uebersprungen - die Entwicklerdateien von OpenSSL (openssl/ssl.h) sind auf diesem Rechner nicht da)");
        }
        else
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            string FireText(string pem) => "\"" + pem.Replace("\r", "").Replace("\n", "\\n") + "\"";
            string certText = FireText(certificate.ExportCertificatePem()), keyText = FireText(rsa.ExportPkcs8PrivateKeyPem());
            string tlsHead = "#import \"tls\"\n#import \"http\"\n#import \"time\"\nvar certPem = " + certText + "\nvar keyPem = " + keyText + "\n";
            natCases = natCases.Concat(new (string Name, string Source)[]
            {
                ("Tls: Handshake, verschluesselte Daten, Zertifikatspruefung (unbekannt, falscher Name, ausgeschaltet) ueber Loopback", tlsHead + """
                    print("available " + Tls.Support.Available())
                    var probe = new Net.TcpListener("127.0.0.1", 0)
                    var port = probe.Port
                    probe.Close()
                    fire {
                        var l = new Net.TcpListener("127.0.0.1", port)
                        var server = new Tls.Server(certPem, keyPem)
                        var served = 0
                        while (served < 5) {
                            var tcp = l.TryAccept(10000)
                            if (tcp == undefined) { break }
                            served = served + 1
                            try {
                                var s = server.Accept(tcp, 5000)
                                var buf = new byte[64]
                                var n = s.Read(buf, 0, 64)
                                s.WriteString("echo:" + IO.Utf8.GetString(buf, 0, n))
                                s.Close()
                            } catch (Net.NetException e) { }
                        }
                        server.Close()
                        l.Close()
                    }
                    Sleep(400)
                    var o = new Tls.Options()
                    o.caPem = certPem
                    var s = Tls.Stream.Connect("localhost", port, o)
                    print("connected " + s.Info.StartsWith("TLSv1"))
                    s.WriteString("hello tls")
                    var buf = new byte[64]
                    var n = s.Read(buf, 0, 64)
                    print("got " + IO.Utf8.GetString(buf, 0, n))
                    print("eof " + s.Read(buf, 0, 64))
                    s.Close()
                    try { var t = Tls.Stream.Connect("localhost", port) } catch (Tls.CertificateException e) { print("untrusted " + e.code) }
                    var tcp2 = new Net.TcpClient("127.0.0.1", port)
                    try { var w = new Tls.Stream(tcp2, "wrong.example", o, 5000) } catch (Tls.CertificateException e) { print("wrong name " + e.code) }
                    var o2 = new Tls.Options()
                    o2.verify = false
                    var u = Tls.Stream.Connect("localhost", port, o2)
                    u.WriteString("unchecked")
                    var m = u.Read(buf, 0, 64)
                    print("unchecked " + IO.Utf8.GetString(buf, 0, m))
                    u.Close()
                    var tcp3 = new Net.TcpClient("127.0.0.1", port)
                    var v = new Tls.Stream(tcp3, "127.0.0.1", o, 5000)
                    v.WriteString("by ip")
                    var k = v.Read(buf, 0, 64)
                    print("by ip " + IO.Utf8.GetString(buf, 0, k))
                    v.Close()
                    """),
                ("Https: Client und Server ueber TLS (https://-URL, Zertifikat des Tests, Zertifikatsfehler)", tlsHead + """
                    var probe = new Net.TcpListener("127.0.0.1", 0)
                    var port = probe.Port
                    probe.Close()
                    fire {
                        var server = new Http.Server("127.0.0.1", port)
                        server.UseTls(certPem, keyPem)
                        server.Route("GET", "/hello", func (r) => Http.Response.FromText("secure " + r.Query("name", "world")))
                        server.Route("POST", "/echo", func (r) => r.Text().ToUpper())
                        var served = 0
                        while (served < 4) {
                            if (server.ServeOne(10000)) { served = served + 1 }
                        }
                        server.Close()
                    }
                    Sleep(400)
                    var client = new Http.Client()
                    client.timeout = 10000
                    var o = new Tls.Options()
                    o.caPem = certPem
                    client.tls = o
                    var site = "https://localhost:" + port
                    var r1 = client.Get(site + "/hello?name=Anna")
                    print(r1.status + " [" + r1.Text() + "] " + r1.url.StartsWith("https://"))
                    var r2 = client.Post(site + "/echo", "grüß dich")
                    print(r2.status + " [" + r2.Text() + "]")
                    var strict = new Http.Client()
                    strict.timeout = 5000
                    try { strict.Get(site + "/hello") } catch (Tls.CertificateException e) { print("untrusted " + e.code) }
                    var loose = new Tls.Options()
                    loose.verify = false
                    strict.tls = loose
                    print(strict.Get(site + "/hello?name=loose").Text())
                    """),
            }).ToArray();
        }
    }

    // GPIO (bridges/fire_bridge_gpio.hpp): the simulated chip "sim" - outputs, inputs, wires, pulls, edges, errors - the same in the VM and in the native build
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Gpio: simulierter Chip - Ausgang, Eingang, Draht, Pull, Flanken, Fehler", """
            #import "gpio"
            print("chips " + Gpio.Board.Chips()[0] + " " + (Gpio.Board.Chips().count >= 1))
            var led = new Gpio.Pin("sim", 1).Output()
            var btn = new Gpio.Pin("sim", 2).Input(Gpio.Pull.Down, Gpio.Edge.Both)
            print("props " + led.Chip + " " + led.Line + " " + led.IsOutput + " " + btn.IsInput + " " + led.IsClosed)
            Gpio.Sim.Wire(1, 2)
            print("low " + btn.Read() + " " + btn.ReadInt() + " " + led.Read())
            led.Write(true)
            print("high " + btn.Read() + " edge " + btn.TakeEdge())
            var first = btn.EdgeTime
            print("none " + btn.TakeEdge())
            print("toggle " + led.Toggle() + " " + btn.Read() + " edge " + btn.TakeEdge() + " " + (btn.EdgeTime >= first))
            Gpio.Sim.Unwire(1, 2)
            var up = new Gpio.Pin("sim", 3).Input(Gpio.Pull.Up, Gpio.Edge.Falling)
            print("pull up " + up.Read())
            Gpio.Sim.Drive(3, false)
            print("driven " + up.Read() + " " + Gpio.Sim.Level(3) + " edge " + up.WaitEdge(200ms))
            Gpio.Sim.Drive(3, true)
            print("rising is not watched: " + up.WaitEdge(30ms) + " " + up.Read())
            Gpio.Sim.Release(3)
            print("released " + up.Read())
            print("timeout " + up.WaitEdge(20))

            class Counter {
                int rising
                int falling
                construct() { this.rising = 0; this.falling = 0 }
                Count(e, t) {
                    if (e == Gpio.Edge.Rising) { this.rising = this.rising + 1 }
                    if (e == Gpio.Edge.Falling) { this.falling = this.falling + 1 }
                }
            }
            var counter = new Counter()
            var watch = new Gpio.Pin("sim", 4).Input(Gpio.Pull.None, Gpio.Edge.Both)
            watch.onEdge = (e, t) => { counter.Count(e, t) }
            for (var i = 0; i < 3; i++) {
                Gpio.Sim.Drive(4, true)
                Gpio.Sim.Drive(4, false)
            }
            print("poll " + watch.Poll() + " " + counter.rising + " " + counter.falling)
            for (var i = 0; i < 40; i++) { Gpio.Sim.Drive(4, i % 2 == 0) }
            print("queue " + watch.Poll())

            var out1 = new Gpio.Pin("sim", 5).Output(true)
            var out2 = new Gpio.Pin("sim", 6).Output(false)
            Gpio.Sim.Wire(5, 6)
            print("fight " + Gpio.Sim.Level(5) + " " + out1.Read() + " " + out2.Read())
            out2.Write(true)
            print("agree " + Gpio.Sim.Level(6))
            Gpio.Sim.Reset()

            try { var x = new Gpio.Pin("sim", 1).Input() } catch (Gpio.BusyException e) { print("busy " + e.code) }
            led.Close()
            var again = new Gpio.Pin("sim", 1).Output()
            print("reclaimed " + again.IsOutput)
            try { var x = new Gpio.Pin("sim", 99) } catch (Gpio.NotFoundException e) { print("no line " + e.code) }
            try { var x = new Gpio.Pin("nochip", 1) } catch (Gpio.NotFoundException e) { print("no chip " + e.code) }
            try { again.TakeEdge() } catch (Gpio.GpioException e) { print("output edge " + e.code) }
            try { btn.Write(true) } catch (Gpio.GpioException e) { print("input write " + e.code) }
            try { new Gpio.Pin("sim", 7).Read() } catch (Gpio.GpioException e) { print("unset " + e.code) }
            try { Gpio.Sim.Wire(1, 40) } catch (Gpio.NotFoundException e) { print("wire " + e.code) }
            try { led.Read() } catch (Gpio.GpioException e) { print("closed " + e.code) }
            """),
    }).ToArray();

    // Audio (bridges/fire_bridge_audio.hpp): the simulated device "sim" records what is played - the same in the VM and in the native build
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Audio: simuliertes Geraet - Schreiben, Wellen, Lautstaerke, WAV, Halten/Stop, Fehler", """
            #import "audio"

            print("devices " + Audio.Board.Devices()[0] + " " + (Audio.Board.Devices().count >= 1))
            var out = new Audio.Output("sim", 8000, 1)
            print("output " + out.Name + " " + out.Rate + " " + out.Channels + " " + out.Volume + " " + out.IsClosed)
            print("sim " + Audio.Sim.Rate() + " " + Audio.Sim.Channels())
            var pcm = new byte[8]
            for (var i = 0; i < 8; i++) { pcm[i] = i + 1 }
            out.Write(pcm)
            print("played " + Audio.Sim.Played() + " queued " + out.Queued + " " + out.Playing)
            var got = Audio.Sim.Data()
            print("data " + got.length + " " + got[0] + " " + got[7])
            out.Write(pcm, 2, 4)
            got = Audio.Sim.Data()
            print("part " + got.length + " " + got[8] + " " + got[11])

            Audio.Sim.Reset()
            out.Tone(2000, 5, Audio.Wave.Sine)
            got = Audio.Sim.Data()
            print("sine " + got.length + " " + got[0] + got[1] + " " + got[2] + "," + got[3] + " " + got[4] + got[5] + " " + got[6] + "," + got[7])
            Audio.Sim.Reset()
            out.Tone(1000, 10, Audio.Wave.Square, 50)
            got = Audio.Sim.Data()
            print("square " + got.length + " " + got[0] + "," + got[1] + " " + got[6] + "," + got[7] + " " + got[8] + "," + got[9] + " " + got[22] + "," + got[23])
            Audio.Sim.Reset()
            out.Tone(500, 4, Audio.Wave.Saw)
            out.Tone(500, 4, Audio.Wave.Triangle, 10)
            out.Tone(500, 4, Audio.Wave.Noise, 20)
            print("kinds " + Audio.Sim.Played())
            Audio.Sim.Reset()
            out.Volume = 50
            var loud = new byte[2]
            loud[0] = 0xFF
            loud[1] = 0x7F
            out.Write(loud)
            got = Audio.Sim.Data()
            print("volume " + out.Volume + " " + got[0] + "," + got[1])
            out.Volume = 100

            var stereo = new Audio.Output("sim", 22050, 2)
            print("stereo " + Audio.Sim.Rate() + " " + Audio.Sim.Channels())
            Audio.Sim.Reset()
            stereo.Tone(440, 2, Audio.Wave.Square)
            print("stereo bytes " + Audio.Sim.Played() + " " + (22050 * 2 / 1000 * 4))
            stereo.Close()

            var w = new byte[47]
            w[0] = 82
            w[1] = 73
            w[2] = 70
            w[3] = 70
            w[4] = 39
            w[8] = 87
            w[9] = 65
            w[10] = 86
            w[11] = 69
            w[12] = 102
            w[13] = 109
            w[14] = 116
            w[15] = 32
            w[16] = 16
            w[20] = 1
            w[22] = 1
            w[24] = 0x40
            w[25] = 0x1F
            w[28] = 0x40
            w[29] = 0x1F
            w[32] = 1
            w[34] = 8
            w[36] = 100
            w[37] = 97
            w[38] = 116
            w[39] = 97
            w[40] = 3
            w[44] = 0
            w[45] = 128
            w[46] = 255
            var snd = Audio.Sound.FromWav(w)
            print("wav " + snd.Rate + " " + snd.Channels + " " + snd.Frames + " " + snd.Length + " " + snd.Milliseconds)
            Audio.Sim.Reset()
            var player = snd.Open("sim")
            player.Play(snd)
            got = Audio.Sim.Data()
            print("wav data " + got.length + " " + got[0] + "," + got[1] + " " + got[2] + "," + got[3] + " " + got[4] + "," + got[5])
            try { out.Play(snd) } catch (Audio.AudioException e) { print("rate " + e.code) }
            var gen = Audio.Sound.Tone(440, 100)
            print("tone sound " + gen.Rate + " " + gen.Frames + " " + gen.Length)
            try { Audio.Sound.FromWav(pcm) } catch (Audio.AudioException e) { print("notwav " + e.code) }
            w[34] = 24
            try { Audio.Sound.FromWav(w) } catch (Audio.UnsupportedException e) { print("bits " + e.code) }

            Audio.Sim.Reset()
            Audio.Sim.Hold(true)
            var big = new byte[10000]
            var took = out.Offer(big)
            print("held " + took + " " + out.Queued + " " + Audio.Sim.Waiting() + " " + out.Playing + " " + out.Drain(20))
            out.Stop()
            print("stopped " + out.Queued + " " + out.Drain(20))
            took = out.Offer(big, 0, 100)
            Audio.Sim.Hold(false)
            print("released " + took + " " + Audio.Sim.Played() + " " + out.Queued)

            try { new Audio.Output("sim", 5, 1) } catch (Audio.AudioException e) { print("rate error " + e.code) }
            try { new Audio.Output("sim", 8000, 3) } catch (Audio.AudioException e) { print("channels error " + e.code) }
            try { new Audio.Output("nodevice") } catch (Audio.NotFoundException e) { print("nodevice " + e.code) }
            try { out.Write(pcm, 0, 3) } catch (Audio.AudioException e) { print("frame " + e.code) }
            try { out.Write(pcm, 6, 4) } catch (Audio.AudioException e) { print("range " + e.code) }
            try { out.Volume = 101 } catch (Audio.AudioException e) { print("volume error " + e.code) }
            try { out.Tone(5000, 10) } catch (Audio.AudioException e) { print("freq " + e.code) }
            try { new Audio.Output(25) } catch (Audio.NotFoundException e) { print("pwm pin " + e.code) }
            var hasAlsa = false
            var names = Audio.Board.Devices()
            for (var i = 0; i < names.count; i++) { if (names[i] == "alsa") { hasAlsa = true } }
            if (hasAlsa) {
                // the PCM "null" of ALSA throws the sound away: the real code path of the Linux backend without a sound card
                try {
                    var nul = new Audio.Output("alsa:null", 44100, 2)
                    nul.Tone(440, 100)
                    print("alsa null " + nul.Drain(3000) + " " + nul.Queued)
                    nul.Close()
                } catch (Audio.AudioException e) { print("alsa null error " + e.code) }
            }
            out.Close()
            try { var q = out.Queued } catch (Audio.AudioException e) { print("closed " + e.code) }
            print("avail " + Audio.Board.Available() + " " + Audio.Sim.Played())
            """),
    }).ToArray();

    // I2C (bridges/fire_bridge_i2c.hpp): the simulated bus "sim" with register-file devices - the same in the VM and in the native build
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("I2c: simulierter Bus - Geraete, Register, Scan, Fehler", """
            #import "i2c"

            print("buses " + I2c.Board.Buses()[0] + " " + I2c.Board.Buses().count)
            var bus = new I2c.Bus("sim")
            print("name " + bus.Name)
            print("scan empty " + bus.Scan().count)
            I2c.Sim.Add(0x50)
            I2c.Sim.Add(0x76)
            I2c.Sim.SetRegister(0x76, 0xD0, 0x58)
            print("probe " + bus.Probe(0x50) + " " + bus.Probe(0x51))
            var found = bus.Scan()
            print("scan " + found.count + " " + found[0] + " " + found[1])
            print("id " + bus.ReadRegister(0x76, 0xD0))
            bus.WriteRegister(0x76, 0xF4, 0x27)
            print("wrote " + I2c.Sim.GetRegister(0x76, 0xF4) + " " + bus.ReadRegister(0x76, 0xF4))
            var data = new byte[4]
            data[0] = 1
            data[1] = 2
            data[2] = 3
            data[3] = 4
            bus.WriteRegisters(0x50, 0x10, data)
            var back = bus.ReadRegisters(0x50, 0x10, 4)
            print("block " + back[0] + back[1] + back[2] + back[3])
            var raw = new byte[3]
            raw[0] = 0x12
            raw[1] = 99
            raw[2] = 100
            bus.Write(0x50, raw)
            bus.Write(0x50, raw, 0, 1)
            var two = bus.Read(0x50, 2)
            print("seq " + two[0] + " " + two[1])
            var viaWr = bus.WriteRead(0x50, raw, 2, 1)
            print("wr " + viaWr[0] + " " + viaWr[1])
            bus.Speed = 400000
            print("speed " + bus.Speed)
            bus.WriteByte(0x50, 0x20)
            try { bus.Write(0x60, raw) } catch (I2c.NoAckException e) { print("noack " + e.code + " " + e.message) }
            try { bus.Read(0x60, 1) } catch (I2c.I2cException e) { print("noack2 " + e.code) }
            try { bus.Probe(200) } catch (I2c.I2cException e) { print("addr " + e.code) }
            try { bus.Write(0x50, raw, 2, 5) } catch (I2c.I2cException e) { print("range " + e.code) }
            try { new I2c.Bus(7) } catch (I2c.NotFoundException e) { print("nobus " + e.code) }
            try { I2c.Sim.SetRegister(0x33, 1, 1) } catch (I2c.NoAckException e) { print("simdev " + e.code) }
            I2c.Sim.Remove(0x50)
            print("removed " + bus.Probe(0x50))
            bus.Close()
            try { bus.Probe(1) } catch (I2c.I2cException e) { print("closed " + e.code) }
            print("avail " + I2c.Board.Available())
            """),
    }).ToArray();

    // SPI (bridges/fire_bridge_spi.hpp): the simulated device "sim" (loopback, queued answers, log of what was sent) - the same in the VM and in the native build
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Spi: simuliertes Geraet - Loopback, Antworten, Protokoll, Einstellungen, Fehler", """
            #import "spi"

            print("devices " + Spi.Board.Devices()[0] + " " + Spi.Board.Devices().count + " " + Spi.Board.Available())
            var chip = new Spi.Device("sim", 0, 2000000)
            print("name " + chip.Name + " " + chip.Mode + " " + chip.Speed + " " + chip.LsbFirst)
            print("sim setup " + Spi.Sim.Mode() + " " + Spi.Sim.Speed() + " " + Spi.Sim.LsbFirst())
            var cmd = new byte[4]
            cmd[0] = 0x9F
            cmd[1] = 1
            cmd[2] = 2
            cmd[3] = 255
            var echo = chip.Transfer(cmd)
            print("loopback " + echo.length + " " + echo[0] + " " + echo[1] + " " + echo[2] + " " + echo[3])
            chip.Write(cmd, 1, 2)
            print("sent " + Spi.Sim.SentCount() + " " + Spi.Sim.Transfers())
            var sent = Spi.Sim.Sent()
            print("log " + sent[0] + " " + sent[3] + " " + sent[4] + " " + sent[5])
            var zeros = chip.Read(3)
            print("read echoes zeros " + zeros[0] + zeros[1] + zeros[2])
            var reply = new byte[4]
            reply[0] = 0
            reply[1] = 0xEF
            reply[2] = 0x40
            reply[3] = 0x18
            Spi.Sim.Reply(reply)
            var one = new byte[1]
            one[0] = 0x9F
            var id = chip.WriteRead(one, 3)
            print("id " + id[0] + " " + id[1] + " " + id[2])
            var after = chip.Read(2)
            print("empty queue " + after[0] + after[1])
            Spi.Sim.Loopback()
            var back = chip.Transfer(one)
            print("loop again " + back[0])
            var src = new byte[3]
            src[0] = 7
            src[1] = 8
            src[2] = 9
            var dst = new byte[5]
            chip.TransferInto(src, 0, dst, 2, 3)
            print("into " + dst[0] + dst[1] + dst[2] + dst[3] + dst[4])
            chip.Mode = 3
            chip.Speed = 500000
            chip.LsbFirst = true
            print("changed " + Spi.Sim.Mode() + " " + Spi.Sim.Speed() + " " + Spi.Sim.LsbFirst() + " " + chip.Mode)
            chip.Configure(1, 1000)
            print("configure " + Spi.Sim.Mode() + " " + chip.LsbFirst)
            Spi.Sim.Clear()
            print("cleared " + Spi.Sim.SentCount() + " " + Spi.Sim.Transfers())
            try { chip.Mode = 5 } catch (Spi.SpiException e) { print("mode " + e.code) }
            try { chip.Write(cmd, 3, 5) } catch (Spi.SpiException e) { print("range " + e.code) }
            try { new Spi.Device("sim", 0, 0) } catch (Spi.SpiException e) { print("speed " + e.code) }
            try { new Spi.Device("9.9") } catch (Spi.NotFoundException e) { print("nodev " + e.code) }
            chip.Close()
            try { chip.Transfer(cmd) } catch (Spi.SpiException e) { print("closed " + e.code) }
            """),
    }).ToArray();

    // WiFi (bridges/fire_bridge_wifi.hpp): the simulated radio "sim" - scan, join, failures, access point - the same in the VM and in the native build
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("WiFi: simuliertes Funkmodul - Scan, Verbinden, Fehler, Zugangspunkt", """
            #import "wifi"

            print("ifaces " + WiFi.Board.Interfaces()[0] + " " + WiFi.Board.Interfaces().count + " " + WiFi.Board.Available())
            var sta = new WiFi.Station()
            print("name " + sta.Name + " state " + sta.State + " ip [" + sta.Ip + "] mac " + sta.Mac + " rssi " + sta.Rssi)
            print("empty scan " + sta.Scan().count)
            WiFi.Sim.AddNetwork("home", "secret-pass", -55, 6)
            WiFi.Sim.AddNetwork("cafe", "", -70, 1)
            WiFi.Sim.AddNetwork("far", "farfarfar", -85, 11)
            var nets = sta.Scan()
            print("scan " + nets.count)
            for (var i = 0; i < nets.count; i++) {
                var n = nets[i]
                print(n.ssid + " " + n.rssi + " ch" + n.channel + " auth" + n.auth + " secure " + n.Secure + " " + n.bssid)
            }
            sta.Connect("home", "secret-pass", 2s)
            print("connected " + sta.IsConnected + " ssid " + sta.Ssid + " ip " + sta.Ip + " rssi " + sta.Rssi)
            print("scan while connected " + sta.Scan().count)
            sta.Disconnect()
            print("after disconnect " + sta.State + " [" + sta.Ssid + "]")
            try { sta.Connect("home", "wrong-password", 2s) } catch (WiFi.AuthException e) { print("auth " + e.code + " " + e.message) }
            try { sta.Connect("nowhere", "", 2s) } catch (WiFi.NotFoundException e) { print("notfound " + e.code) }
            sta.Connect("cafe")
            print("open " + sta.Ssid + " " + sta.Rssi)
            WiFi.Sim.Drop()
            print("dropped " + sta.State + " [" + sta.Ip + "]")
            WiFi.Sim.Delays(100000, 0)
            try { sta.Connect("home", "secret-pass", 30ms) } catch (WiFi.TimeoutException e) { print("timeout " + e.code) }
            WiFi.Sim.Delays(3, 0)
            sta.Start("home", "secret-pass")
            var seen = 0
            sta.onState = (s) => { seen = seen + 1; print("state change " + s) }
            for (var i = 0; i < 10; i++) { sta.Poll() }
            print("polled " + sta.IsConnected)
            WiFi.Sim.RemoveNetwork("home")
            print("network gone " + sta.State)

            var ap = new WiFi.AccessPoint("sim")
            print("ap running " + ap.IsRunning + " clients ")
            ap.Start("fire-board", "password123", 6, 2)
            print("ap " + ap.IsRunning + " " + ap.Ip + " " + ap.Mac + " " + ap.Clients)
            print("join " + WiFi.Sim.ClientJoins() + " " + WiFi.Sim.ClientJoins() + " clients " + ap.Clients)
            try { WiFi.Sim.ClientJoins() } catch (WiFi.WiFiException e) { print("full " + e.code) }
            try { ap.Start("x", "short") } catch (WiFi.WiFiException e) { print("short password " + e.code) }
            try { ap.Start("x", "", 20) } catch (WiFi.WiFiException e) { print("channel " + e.code) }
            ap.Stop()
            print("stopped " + ap.IsRunning)
            try { new WiFi.Station("nothing") } catch (WiFi.NotFoundException e) { print("no interface " + e.code) }
            sta.Close()
            try { sta.State } catch (WiFi.WiFiException e) { print("closed " + e.code) }
            """),
    }).ToArray();

    // Network (bridges/fire_bridge_net.hpp): TCP, UDP and name resolution on the loopback interface - the same in the VM and in the native build
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Net: TCP, UDP, DNS ueber Loopback, Zeitlimits, Fehler", """
            #import "net"
            var l = new Net.TcpListener("127.0.0.1", 0)
            print("port " + (l.Port > 0) + " " + l.Host)
            var c = new Net.TcpClient("127.0.0.1", l.Port)
            var s = l.Accept(2000)
            print("pending " + l.Pending(0))
            c.WriteString("hello")
            var buf = new byte[16]
            var n = s.Read(buf, 0, 16)
            print("got " + n + " " + IO.Utf8.GetString(buf, 0, n))
            print("remote " + s.RemoteHost + " " + (s.RemotePort == c.LocalPort) + " " + (c.RemotePort == l.Port))
            s.WriteString("pong!")
            c.ReadTimeout = 500
            var m = c.Read(buf, 0, 16)
            print("reply " + IO.Utf8.GetString(buf, 0, m))
            try { c.Read(buf, 0, 16) } catch (Net.TimeoutException e) { print("timeout " + e.code) }
            var big = new byte[40000]
            for (var i = 0; i < big.length; i++) { big[i] = i % 251 }
            s.Write(big, 0, big.length)
            var total = 0
            var ok = true
            var chunk = new byte[4096]
            c.ReadTimeout = 5000
            while (total < big.length) {
                var got = c.Read(chunk, 0, 4096)
                if (got <= 0) { break }
                for (var i = 0; i < got; i++) { if (chunk[i] != (total + i) % 251) { ok = false } }
                total = total + got
            }
            print("big " + total + " " + ok)
            s.Shutdown(1)
            print("eof " + c.Read(buf, 0, 16))
            print("waitReadable " + c.WaitReadable(0))
            c.Close()
            s.Close()
            print("closed " + c.IsClosed)
            try { c.Read(buf, 0, 1) } catch (Net.ClosedException e) { print("closed " + e.code) }
            print("accept timeout " + (l.TryAccept(50) == undefined))
            l.Close()
            try { var x = new Net.TcpClient("127.0.0.1", 1, 1000) } catch (Net.RefusedException e) { print("refused " + e.code) }
            var u1 = new Net.UdpSocket("127.0.0.1", 0)
            var u2 = new Net.UdpSocket()
            print("udp avail " + u1.Available)
            u2.SendString("datagram", "127.0.0.1", u1.Port)
            var got2 = u1.ReceiveFrom(buf, 0, 16, 2000)
            print("udp " + got2 + " " + IO.Utf8.GetString(buf, 0, got2) + " from " + u1.FromHost + " " + (u1.FromPort == u2.Port))
            u1.SendTo(buf, 0, 3, "127.0.0.1", u2.Port)
            print("udp back " + u2.ReceiveFrom(buf, 0, 16, 2000))
            try { u1.ReceiveFrom(buf, 0, 16, 100) } catch (Net.TimeoutException e) { print("udp timeout") }
            var addresses = Net.Dns.Resolve("127.0.0.1")
            print("dns " + addresses.count + " " + addresses[0] + " " + (Net.Dns.Resolve("localhost").count > 0))
            try { Net.Dns.Resolve("no-such-host.invalid") } catch (Net.ResolveException e) { print("resolve " + e.code) }
            try { var bad = new Net.TcpListener("127.0.0.1", 70000) } catch (Net.NetException e) { print("port " + e.code) }
            var l2 = new Net.TcpListener("127.0.0.1", 0)
            try { var l3 = new Net.TcpListener("127.0.0.1", l2.Port) } catch (Net.NetException e) { print("in use " + e.code) }
            """),
        ("Http: Client und Server (Routen, Redirects, chunked, Body bis Verbindungsende, Fehler) ueber Loopback", """
            #import "http"
            #import "time"
            class Boom : Exception {
                string message
                construct(string message) { this.message = message }
            }
            // ---- pure helpers
            var u = Http.Url.Parse("HTTP://User@Example.org:8080/a/b?x=1#frag")
            print(u.scheme + " " + u.host + " " + u.port + " " + u.path)
            print(Http.Url.Parse("https://example.org").path + " " + Http.Url.Parse("https://example.org").port + " " + Http.Url.Parse("http://[::1]:81/x").host)
            print(u.Resolve("/c") + " | " + u.Resolve("d") + " | " + u.Resolve("http://other/z"))
            try { Http.Url.Parse("ftp://x/") } catch (Http.HttpException e) { print("bad scheme " + e.code) }
            try { Http.Url.Parse("no-scheme") } catch (Http.HttpException e) { print("bad url " + e.code) }
            print(Http.Uri.Encode("a b/ü?&=") + " " + Http.Uri.Decode("a%20b%2Fx%C3%BC+%zz%4") + " " + Http.Uri.Decode("a+b", true))
            var h = new Http.Headers()
            h.Set("Content-Type", "text/x").Add("X-A", "1").Add("x-a", "2")
            print(h.Get("content-type") + " " + h.Get("X-A") + " " + h.Count + " " + h.Has("nothing"))
            h.Set("X-A", "3")
            print(h.Count + " " + h.Get("x-a"))
            var req = new Http.Request("GET", "/p%20q?name=Anna+M&a=b%26c&flag")
            print(req.path + " " + req.Query("name") + " " + req.Query("a") + " [" + req.Query("flag") + "] " + req.Query("none", "dflt"))

            // ---- a server in a thread
            var probe = new Net.TcpListener("127.0.0.1", 0)
            var port = probe.Port
            probe.Close()
            fire {
                var server = new Http.Server("127.0.0.1", port)
                server.Route("GET", "/hello", func (r) => Http.Response.FromText("hello " + r.Query("name", "world")))
                server.Route("POST", "/echo", func (r) => Http.Response.FromText(r.Text().ToUpper() + " " + r.body.length, 201))
                server.Route("GET", "/moved", func (r) => Http.Response.Redirect("/hello?name=redirect"))
                server.Route("GET", "/loop", func (r) => Http.Response.Redirect("/loop"))
                server.Route("*", "/any/*", func (r) => r.method + " " + r.path)
                server.Route("GET", "/boom", func (r) => { throw new Boom("boom") })
                server.Route("GET", "/big", func (r) => Http.Response.FromText("0123456789".Replace("0", "ab").Replace("1", "cd") + "x"))
                server.Route("POST", "/size", func (r) => "" + r.body.length)
                server.Route("GET", "/none", func (r) => undefined)
                server.OnError(func (e) => print("server error: " + e.message))
                var served = 0
                while (served < 18) {
                    if (server.ServeOne(10000)) { served = served + 1 }
                }
                server.Close()
            }
            Sleep(400)
            var client = new Http.Client()
            client.timeout = 10000
            var site = "http://127.0.0.1:" + port
            var r1 = client.Get(site + "/hello?name=Anna%20M")
            print(r1.status + " " + r1.reason + " [" + r1.Text() + "] " + r1.headers.Get("content-type") + " ok=" + r1.Ok + " len=" + r1.Length)
            var r2 = client.Post(site + "/echo", "grüß dich")
            print(r2.status + " [" + r2.Text() + "]")
            var r3 = client.Get(site + "/moved")
            print(r3.status + " [" + r3.Text() + "] " + r3.url.EndsWith("/hello?name=redirect"))
            try { client.Get(site + "/loop") } catch (Http.HttpException e) { print("loop " + e.code) }
            var r4 = client.Get(site + "/nothing")
            print(r4.status + " " + r4.Text() + " ok=" + r4.Ok)
            var r5 = client.Get(site + "/boom")
            print(r5.status)
            var r6 = client.Request("PATCH", site + "/any/x/y", undefined, undefined)
            print(r6.status + " [" + r6.Text() + "]")
            var r7 = client.Head(site + "/hello")
            print("head " + r7.status + " " + r7.headers.Get("Content-Length") + " body=" + r7.Length)
            var data = new byte[200000]
            for (var i = 0; i < data.length; i++) { data[i] = i % 253 }
            var r8 = client.Post(site + "/size", data)
            print("size " + r8.Text())
            var r9 = client.Get(site + "/none")
            print(r9.status + " " + r9.reason + " " + r9.Length)
            client.followRedirects = false
            var r10 = client.Get(site + "/moved")
            print(r10.status + " " + r10.headers.Get("Location") + " " + r10.Length)
            var custom = new Http.Headers()
            custom.Set("X-Test", "1")
            client.headers.Set("X-Default", "2")
            var r11 = client.Get(site + "/hello", custom)
            print(r11.status)
            // ---- raw servers: chunked, until close
            var chunkedPort = 0
            var closePort = 0
            var l1 = new Net.TcpListener("127.0.0.1", 0)
            var l2 = new Net.TcpListener("127.0.0.1", 0)
            chunkedPort = l1.Port
            closePort = l2.Port
            l1.Close()
            l2.Close()
            fire {
                var l = new Net.TcpListener("127.0.0.1", chunkedPort)
                var c = l.Accept(10000)
                var rd = new Http.Reader(c)
                var line = rd.ReadLine()
                while (line != undefined && line.Length > 0) { line = rd.ReadLine() }
                c.WriteString("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nX-T: t\r\n\r\n5\r\nhello\r\nB;ext=1\r\n, chunked w\r\n3\r\norl\r\n1\r\nd\r\n0\r\nTrailer: v\r\n\r\n")
                c.Close()
                l.Close()
            }
            fire {
                var l = new Net.TcpListener("127.0.0.1", closePort)
                var c = l.Accept(10000)
                var rd = new Http.Reader(c)
                var line = rd.ReadLine()
                while (line != undefined && line.Length > 0) { line = rd.ReadLine() }
                c.WriteString("HTTP/1.0 203 Odd Reason Phrase\r\n\r\nuntil close")
                c.Close()
                l.Close()
            }
            Sleep(400)
            var c1 = client.Get("http://127.0.0.1:" + chunkedPort + "/")
            print(c1.status + " [" + c1.Text() + "] " + c1.headers.Get("x-t"))
            var c2 = client.Get("http://127.0.0.1:" + closePort + "/")
            print(c2.status + " " + c2.reason + " [" + c2.Text() + "]")
            // ---- errors
            var silent = new Net.TcpListener("127.0.0.1", 0)
            client.timeout = 300
            try { client.Get("http://127.0.0.1:" + silent.Port + "/") } catch (Net.TimeoutException e) { print("silent " + e.code) }
            silent.Close()
            try { client.Get("https://127.0.0.1:1/") } catch (Net.RefusedException e) { print("https refused " + e.code) }
            try { client.Get("ftp://127.0.0.1/") } catch (Http.HttpException e) { print("ftp " + e.code) }
            """),
    }).ToArray();

    // Time and Sleep (bridges/fire_bridge_time.hpp)
    natCases = natCases.Concat(new (string Name, string Source)[]
    {
        ("Time: TimeSpan, DateTime, Formate, Parse, Sleep", """
            #import "time"
            var a = TimeSpan.FromSeconds(90)
            print(a)
            print(new TimeSpan(1, 2, 3, 4, 5))
            print(TimeSpan.FromMilliseconds(1500) * 2)
            print(TimeSpan.FromHours(2.5).Negate())
            var d = new DateTime(2024, 3, 15, 14, 30, 5, 123)
            print(d)
            print(d.ToString("dd.MM.yyyy HH:mm:ss.fff"))
            print(d.ToString("o"))
            print(d.ToString("dddd, d MMMM yyyy h:mm tt"))
            print(d.ToString("D"))
            print(d.ToString("G"))
            print(d.Year + "-" + d.Month + "-" + d.Day + " " + d.Hour + ":" + d.Minute + " dow " + d.DayOfWeek + " doy " + d.DayOfYear)
            print(d.AddMonths(11))
            print(d.AddMonths(-3))
            print(new DateTime(2024, 1, 31).AddMonths(1))
            print(d.AddDays(20.5))
            print(d + TimeSpan.FromHours(10))
            print((d + TimeSpan.FromHours(10)) - d)
            print(DateTime.DaysInMonth(2023, 2) + " " + DateTime.DaysInMonth(2024, 2) + " " + DateTime.IsLeapYear(1900))
            print(d.Date())
            print(d.TimeOfDay())
            print(d.ToUnixSeconds())
            print(DateTime.FromUnixSeconds(1700000000).ToString("yyyy-MM-dd HH:mm:ss"))
            print(DateTime.Parse("2024-03-15 14:30:00"))
            print(DateTime.Parse("2024-03-15T14:30:00.5Z"))
            print(DateTime.Parse("2024-03-15T14:30:00+02:00"))
            print(DateTime.Parse("3/15/2024"))
            print(DateTime.Parse("March 15, 2024 3:45 PM"))
            print(DateTime.Parse("Fri, 15 Mar 2024 14:30:00 GMT"))
            print(DateTime.TryParse("rubbish"))
            print(d < d.AddDays(1))
            print(d.ToUtc().Kind)
            Sleep(5ms)
            Sleep(TimeSpan.FromMilliseconds(5))
            Sleep(2)
            Sleep(0.002s)
            print("slept")
            var t0 = DateTime.UtcNow()
            Sleep(60ms)
            var el = DateTime.UtcNow() - t0
            print(el >= TimeSpan.FromMilliseconds(55))
            print(el < TimeSpan.FromMilliseconds(500))
            """),
        ("Time: Formatstrings, Fehler (TimeException), Parse-Formen, Sleep mit falschen Angaben", """
            #import "time"
            class Thing { int x
              construct() { this.x = 1 } }
            var d = new DateTime(2024, 12, 5, 0, 7, 9, 50)
            var f = ["yyyy", "yy", "y", "yyy", "M", "MM", "MMM", "MMMM", "d", "dd", "ddd", "dddd", "H", "HH", "h", "hh", "m", "mm", "s", "ss", "t", "tt", "f", "ff", "fff", "ffffff", "F", "FF", "ss.FFF", "ss.FFFFFF", "'quoted' yyyy", "\"dq\" MM", "yyyy\\MM", "%d", "%y", "d/M/yyyy", "HH:mm:ss", "g", "m", "u", "s", "r", "t", "T", "y", "M", "f", "F", "O", "dddd dd MMMM yyyy 'at' H:mm", "x yy z"]
            foreach (x in f) {
                try { print(x + " => " + d.ToString(x)) } catch (TimeException e) { print(x + " => error: " + e.message) }
            }
            foreach (bad in ["", "yyyyyyyy", "fffffffff", "Z", "q", "%", "\\", "'abc", "hhh"]) {
                try { print("[" + bad + "] => " + d.ToString(bad)) } catch (TimeException e) { print("error: " + e.message) }
            }
            try { var x = new DateTime(2023, 2, 29) } catch (TimeException e) { print("error: " + e.message) }
            try { var x = new DateTime(2023, 13, 1) } catch (TimeException e) { print("error: " + e.message) }
            try { var x = new DateTime(2023, 1, 1, 24, 0, 0) } catch (TimeException e) { print("error: " + e.message) }
            try { print(DateTime.DaysInMonth(2023, 13)) } catch (TimeException e) { print("error: " + e.message) }
            try { Sleep("x") } catch (TimeException e) { print("error: " + e.message) }
            try { Sleep(3m) } catch (TimeException e) { print("error: " + e.message) }
            try { Sleep(new Thing()) } catch (TimeException e) { print("error: " + e.message) }
            try { Sleep(true) } catch (TimeException e) { print("error: " + e.message) }
            try { Sleep(undefined) } catch (TimeException e) { print("error: " + e.message) }
            try { print(new DateTime(9999, 12, 31).AddDays(2)) } catch (TimeException e) { print("error: " + e.message) }
            try { print(new DateTime(9999, 12, 31).AddMonths(1)) } catch (TimeException e) { print("error: " + e.message) }
            try { print(new DateTime(1, 1, 1).AddYears(-1)) } catch (TimeException e) { print("error: " + e.message) }
            try { print(DateTime.Parse("nonsense")) } catch (TimeException e) { print("error: " + e.message) }
            try { print(TimeSpan.FromSeconds(2m)) } catch (Exception e) { print("error") }
            print(TimeSpan.Of(1.5s))
            print(TimeSpan.Of(250ms).TotalMilliseconds)
            print(TimeSpan.Of(2))
            print(new TimeSpan(0, 0, 0, 0, 1).ToString())
            print(new TimeSpan(-5).ToString())
            print(new TimeSpan(10, 0, 0, 0))
            for (var y = 1; y <= 3; y++) { print(new DateTime(2000 + y * 400, 2, 29).DayOfWeek) }
            print(new DateTime(1, 1, 1).DayName() + " " + new DateTime(9999, 12, 31).DayName() + " " + new DateTime(1900, 3, 1).DayOfYear + " " + new DateTime(2000, 12, 31).DayOfYear)
            print(new DateTime(1, 1, 1).Ticks)
            print(new DateTime(9999, 12, 31, 23, 59, 59, 999).Ticks)
            print(DateTime.Parse("1999-12-31 23:59:59").AddSeconds(1))
            print(DateTime.Parse("12/31/99"))
            print(DateTime.Parse("1/2/30"))
            print(DateTime.Parse("14:30"))
            print(DateTime.Parse("2:30:15 PM"))
            print(DateTime.Parse("12:00 AM"))
            print(DateTime.Parse("Mar 5 2020"))
            print(DateTime.Parse("5 March 2020 08:15"))
            print(DateTime.Parse("2024-03-15 14:30:00 +0530"))
            print(DateTime.Parse("2024/03/15"))
            print(DateTime.TryParse("2024-02-30") == undefined)
            """),
        ("Threads: Sleep gibt den anderen Threads frei, das Hauptprogramm bedient dabei die Sektionen", """
            #import "time"
            var done = 0
            var log = []
            class Counter { int n
              construct() { this.n = 0 }
              Add() { this.n = this.n + 1 }
            }
            var c = new Counter()
            fire {
                for (var i = 0; i < 5; i++) { Sleep(10ms); c.Add() }
                done = done + 1
            }
            fire {
                for (var i = 0; i < 5; i++) { Sleep(7); c.Add() }
                done = done + 1
            }
            // the main program sleeps and meanwhile serves the sections of the threads
            Sleep(600ms)
            print("after sleep: " + done + " " + c.n)
            while (done < 2) { Sleep(5ms) }
            print("n = " + c.n)
            """),
        ("Threads: terminate beendet Sleep sofort", """
            #import "time"
            // terminate cuts a sleep short and runs the handler; a thread that sleeps ends too
            fire {
                Sleep(5s)
                print("never")
            }
            catch terminate(v) { print("handler " + v) }
            Sleep(50ms)
            print("before terminate")
            terminate(7)
            Sleep(10s)
            print("never either")
            """),
        ("Threads: eine Ausnahme eines Threads erreicht das Hauptprogramm waehrend Sleep", """
            #import "time"
            class Boom { string message
              construct() { this.message = "from thread" } }
            // a sleeping thread does not hold the others up; an exception of a thread reaches the main program during its Sleep
            fire {
                Sleep(20ms)
                throw new Boom()
            }
            catch threads(Boom e) { print("caught in main: " + e.message) }
            var start = DateTime.UtcNow()
            Sleep(400ms)
            print("main woke up")
            print((DateTime.UtcNow() - start) >= TimeSpan.FromMilliseconds(100))
            """),
    }).ToArray();

    // Resources: `new Resource("path")` is read when the program is compiled and travels with it (VM, packed file, native binary)
    {
        string resDir = Path.Combine(Path.GetTempPath(), "fire-resource-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resDir);
        File.WriteAllText(Path.Combine(resDir, "hello.txt"), "Gr\u00fc\u00dfe, Welt!\n", new System.Text.UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(resDir, "data.bin"), new byte[] { 0, 1, 2, 250, 255 });
        string rd = resDir.Replace('\\', '/');
        string resScript = $$"""
            var text = new Resource("{{rd}}/hello.txt")
            print(text.Name().EndsWith("hello.txt"))
            print(text.Length())
            print(text.Text().Length)
            print(text.Text().Substring(0, 5))
            var again = new Resource("{{rd}}/hello.txt")
            print(again.id == text.id)
            var bin = new Resource("{{rd}}/data.bin")
            var bytes = bin.Bytes()
            print(bytes.length)
            print(bytes[3])
            // new Resource("{{rd}}/missing.bin")   <- in a comment: not embedded
            print("new Resource(\"{{rd}}/missing.bin\")".Length > 0)
            """;
        string[] resExpected = { "True", "15", "13", "Gr\u00fc\u00dfe", "True", "5", "250", "True" };
        string resOutput = vmOutput(resScript);
        CheckNat("Ressourcen: new Resource(\"pfad\") in der VM (Text UTF-8, Bytes, dieselbe Datei einmal, Kommentar und String bleiben)", resOutput == string.Concat(resExpected.Select(l => l + "\n")), "  erhalten:\n" + resOutput);
        // the program carries the bytes: a packed file (serialized program) and a program without the file at run time
        {
            var linked = new Linker().CompileAndLink(new[] { resScript });
            var back = MemoryPack.MemoryPackSerializer.Deserialize<LinkedProgram>(MemoryPack.MemoryPackSerializer.Serialize(linked))!;
            CheckNat("Ressourcen: das serialisierte Programm traegt die Dateien (2 Ressourcen, Bytes gleich)",
                back.Program.Resources.Count == 2 && back.Program.Resources[1].Data.SequenceEqual(new byte[] { 0, 1, 2, 250, 255 }) && back.Program.Resources[0].Name.EndsWith("hello.txt"), "");
        }
        // a missing file is a compile error with the line
        {
            string message = "";
            try { new Linker().CompileAndLink(new[] { "var a = 1\nvar r = new Resource(\"" + rd + "/gibt-es-nicht.png\")\n" }); } catch (Exception ex) { message = ex.Message; }
            CheckNat("Ressourcen: eine fehlende Datei ist ein Uebersetzungsfehler mit Zeile", message.Contains("not found") && message.Contains("line 2"), message);
        }
        natCases = natCases.Append(("Ressourcen: new Resource(\"pfad\") ist eingebettet", resScript)).ToArray();
    }


    string? cxx = FindCxx();
    if (cxx == null)
        Console.WriteLine("(kein C++-Compiler gefunden - die Native-Backend-Pruefungen werden uebersprungen)");
    else
    {
        string workDir = Path.Combine(Path.GetTempPath(), "fire-native-test-" + Guid.NewGuid().ToString("N"));
        fire.Native.NativeRuntimeFiles.WriteTo(workDir);
        fire.Native.NativeRuntimeFiles.WriteSimulatorTo(Path.Combine(workDir, "sim"));

        // Memory errors in the generated code (ownership, freeing) must not slip through: run the cases under the sanitizers when the compiler has them.
        string sanitize = "";
        {
            string probeFile = Path.Combine(workDir, "probe.cpp");
            File.WriteAllText(probeFile, "int main() { return 0; }\n");
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cxx, $"-fsanitize=address,undefined \"{probeFile}\" -o \"{probeFile}.bin\"") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
            probe.StandardError.ReadToEnd(); probe.StandardOutput.ReadToEnd(); probe.WaitForExit();
            if (probe.ExitCode == 0 && System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(probeFile + ".bin") { UseShellExecute = false, RedirectStandardError = true }) is { } run) { run.StandardError.ReadToEnd(); run.WaitForExit(); if (run.ExitCode == 0) sanitize = "-fsanitize=address,undefined -fno-sanitize-recover=undefined "; }
        }
        Console.WriteLine(sanitize.Length > 0 ? "(die Faelle laufen unter AddressSanitizer/UBSan)" : "(Sanitizer nicht verfuegbar)");

        // The VM runs sequentially: the precision of float is process-wide while a program runs.
        var vmResults = natCases.Select(c => { var text = vmOutput(c.Source); Value.SingleFloats = false; return text; }).ToArray();

        var compiled = natCases.Select((c, index) => Task.Run(() =>
        {
            string expected = vmResults[index];
            string cpp;
            try { cpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { c.Source }, null, null, VmExecutionMode.Release)); }
            catch (fire.Native.NativeNotSupportedException ex) { return (c.Name, expected, $"nicht uebersetzbar: {ex.Message}"); }
            string cppFile = Path.Combine(workDir, $"case{index}.cpp"), exeFile = Path.Combine(workDir, $"case{index}.bin");
            File.WriteAllText(cppFile, cpp);

            string RunTool(string tool, string arguments, out int exit)
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, arguments)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDir })!;
                var errTask = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                exit = p.ExitCode;
                return output + errTask.Result;
            }

            // the libraries that the program asks for (`// fire-link: ssl` from the `linkLibraries` of a package)
            string linkFlags = string.Concat(cpp.Split('\n').Take(400).Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => " -l" + l.Substring("// fire-link: ".Length).Trim()).Distinct());
            string build = RunTool(cxx, $"-std=c++17 -pthread -O2 -Wall -Wextra {sanitize}\"{cppFile}\" -I\"{workDir}\" -o \"{exeFile}\"{linkFlags}", out int buildExit);
            if (buildExit != 0 || build.Contains("warning:")) return (c.Name, expected, "C++-Compiler: " + build);
            if (c.Name.StartsWith("Abbruch:"))
            {
                // an exception that nothing catches: the program ends with exit code 1 after the output so far (the VM stops the same way)
                using var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exeFile) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDir })!;
                var stderrTask = run.StandardError.ReadToEndAsync();
                string stdout = run.StandardOutput.ReadToEnd();
                run.WaitForExit();
                return (c.Name, expected, run.ExitCode == 1 && stderrTask.Result.Contains("Unhandled exception") ? stdout : $"Exitcode {run.ExitCode}: {stdout}{stderrTask.Result}");
            }
            string actual = RunTool(exeFile, "", out int runExit);
            // `terminate(n)` with a whole number is the exit code of the process (like the command line runner)
            return (c.Name, expected, runExit == 0 || (c.Name.StartsWith("Threads:") && c.Source.Contains("terminate(")) ? actual : $"Exitcode {runExit}: {actual}");
        })).ToArray();
        Task.WaitAll(compiled);

        foreach (var task in compiled)
        {
            var (name, expected, actual) = task.Result;
            CheckNat($"Native == VM: {name}", expected == actual, $"  erwartet (VM):\n{expected}\n  erhalten (C++):\n{actual}");
        }

        // SChannel, the TLS of the Windows platform: the TLS case built for Windows with MinGW and run under Wine (when both are on this machine) must print what the VM prints
        {
            string? FindTool(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
            string? mingw = FindTool("x86_64-w64-mingw32-g++");
            string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists) ?? FindTool("wine64") ?? FindTool("wine");
            int tlsIndex = Array.FindIndex(natCases, c => c.Name.StartsWith("Tls: Handshake", StringComparison.Ordinal));
            if (mingw == null || wine == null || tlsIndex < 0)
            {
                Console.WriteLine("(Tls mit SChannel: uebersprungen - MinGW (x86_64-w64-mingw32-g++) und Wine sind auf diesem Rechner nicht da)");
            }
            else
            {
                string name = "Tls (SChannel, fuer Windows gebaut, unter Wine)";
                try
                {
                    string cpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { natCases[tlsIndex].Source }, null, null, VmExecutionMode.Release, null, TargetProfile.Windows), TargetProfile.Windows);
                    string cppFile = Path.Combine(workDir, "schannel.cpp"), exeFile = Path.Combine(workDir, "schannel.exe");
                    File.WriteAllText(cppFile, cpp);
                    string linkFlags = string.Concat(cpp.Split('\n').Take(400).Where(l => l.StartsWith("// fire-link: ", StringComparison.Ordinal)).Select(l => " -l" + l.Substring("// fire-link: ".Length).Trim()).Distinct());
                    string Run(string tool, string arguments, out int exit)
                    {
                        var info = new System.Diagnostics.ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDir };
                        info.Environment["WINEPREFIX"] = Path.Combine(workDir, "wineprefix");
                        info.Environment["WINEDEBUG"] = "-all";
                        using var p = System.Diagnostics.Process.Start(info)!;
                        var errTask = p.StandardError.ReadToEndAsync();
                        string output = p.StandardOutput.ReadToEnd();
                        if (!p.WaitForExit(240000)) { try { p.Kill(true); } catch { } exit = -1; return output + "(Zeitueberschreitung)"; }
                        exit = p.ExitCode;
                        return output + errTask.Result;
                    }
                    string build = Run(mingw, $"-std=c++17 -O2 -Wall -Wextra -static \"{cppFile}\" -I\"{workDir}\" -o \"{exeFile}\"{linkFlags}", out int buildExit);
                    if (buildExit != 0 || build.Contains("warning:")) CheckNat(name, false, "MinGW: " + build);
                    else
                    {
                        string actual = Run(wine, $"\"{exeFile}\"", out int runExit);
                        // (the Wine output has no stray lines: WINEDEBUG is off; lines Wine itself prints about the prefix are dropped)
                        actual = string.Join("\n", actual.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("wine:", StringComparison.Ordinal)));
                        string expected = vmResults[tlsIndex].Replace("\r", "");
                        CheckNat(name, runExit == 0 && expected.TrimEnd() == actual.TrimEnd(), $"  erwartet (VM):\n{expected}\n  erhalten (Wine, Exitcode {runExit}):\n{actual}");
                    }
                }
                catch (Exception ex) { CheckNat(name, false, ex.Message); }
            }
        }

        // The switch itself: VM output differs between the precisions, the directive is validated, the override wins
        string out64 = vmOutput("print(0.1 + 0.2)\nprint(1.0 / 3)"), out32 = vmOutput("#floatwidth 32\nprint(0.1 + 0.2)\nprint(1.0 / 3)");
        Value.SingleFloats = false;
        CheckNat("#floatwidth: 64 Bit ist der Standard", out64 == "0.30000000000000004\n0.3333333333333333\n", out64);
        CheckNat("#floatwidth 32: float rechnet und druckt mit 32 Bit", out32 == "0.3\n0.33333334\n", out32);
        var overridden = new Linker().CompileAndLink(new[] { "#floatwidth 64\nprint(1)" }, null, null, null, floatWidthOverride: 32);
        CheckNat("Linker: floatWidthOverride gewinnt gegen die Direktive", overridden.FloatWidth == 32 && new Linker().CompileAndLink(new[] { "print(1)" }).FloatWidth == 64);
        try { new Linker().CompileAndLink(new[] { "#floatwidth 16\nprint(1)" }); CheckNat("#floatwidth 16 wird abgelehnt", false); }
        catch (Exception ex) { CheckNat("#floatwidth 16 wird abgelehnt", ex.Message.Contains("32 or 64"), ex.Message); }
        var cli32 = CommandLineParser.Parse(new[] { "run", "a.script", "-f", "32" });
        var cliBad = CommandLineParser.Parse(new[] { "run", "a.script", "-f", "16" });
        CheckNat("Befehlszeile: -f 32 / -f 16", cli32.Error == null && cli32.FloatWidth == 32 && cliBad.Error != null && CommandLineParser.Parse(new[] { "run", "a.script" }).FloatWidth == null);
        var narrowed = new Linker().CompileAndLink(new[] { "var x = 0.1\nprint(x)" }, null, null, null, floatWidthOverride: 32);
        CheckNat("Konstanten werden auf 32 Bit gerundet", narrowed.Program.TopLevel.Constants.Any(c => c.Kind == ValueKind.Float && c.AsFloat() == (double)0.1f));

        // Zielprofil
        var esp = TargetProfile.Esp32;
        var espLinked = new Linker().CompileAndLink(new[] { "print(0.1 + 0.2)" }, null, null, null, null, esp);
        CheckNat("Zielprofil esp32: float ist 32 Bit, wenn nichts anderes gesagt wird", espLinked.FloatWidth == 32);
        CheckNat("Zielprofil: #floatwidth gewinnt gegen das Ziel, -f gegen beides",
            new Linker().CompileAndLink(new[] { "#floatwidth 64\nprint(1)" }, null, null, null, null, esp).FloatWidth == 64
            && new Linker().CompileAndLink(new[] { "#floatwidth 64\nprint(1)" }, null, null, null, 32, TargetProfile.Windows).FloatWidth == 32
            && new Linker().CompileAndLink(new[] { "print(1)" }, null, null, null, null, TargetProfile.Windows).FloatWidth == 64);
        try { new Linker().CompileAndLink(new[] { "#import \"graphics\"\nprint(1)" }, null, null, null, null, esp); CheckNat("Zielprofil: nicht verfuegbare Bibliothek wird abgelehnt", false); }
        catch (NotSupportedException ex) { CheckNat("Zielprofil: nicht verfuegbare Bibliothek wird abgelehnt", ex.Message.Contains("'graphics'") && ex.Message.Contains("'esp32'"), ex.Message); }
        CheckNat("Zielprofil: Windows erlaubt die Grafik, esp32 hat Devices/IO/Time", TargetProfile.Windows.HasImport("graphics") && esp.HasImport("devices") && esp.HasImport("io") && esp.HasImport("time") && !esp.HasImport("ui"));
        CheckNat("Zielprofil: Namen und Host", TargetProfile.TryGet("ESP32", out var byName) && byName == esp && !TargetProfile.TryGet("amiga", out _) && TargetProfile.All.Contains(TargetProfile.Host));
        string espCpp = fire.Native.CppGenerator.Generate(espLinked, esp);
        CheckNat("esp32: Einstieg app_main statt main, Defines, 32-Bit-float",
            espCpp.Contains("extern \"C\" void app_main(void)") && !espCpp.Contains("int main()") && espCpp.Contains("#define FIRE_TARGET_ESP32 1")
            && espCpp.Contains("#define FIRE_HAL_ESP32 1") && espCpp.Contains("#define FIRE_DEFAULT_STACK_BYTES 8192") && espCpp.Contains("#define FIRE_FLOAT32 1"));
        {
            string espFile = Path.Combine(workDir, "esp.cpp");
            File.WriteAllText(espFile, espCpp);
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cxx, $"-std=c++17 -Wall -Wextra -c \"{espFile}\" -I\"{workDir}\" -I\"{Path.Combine(workDir, "sim")}\" -o \"{espFile}.o\"") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
            string espBuild = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            CheckNat("esp32: das erzeugte C++ uebersetzt ohne Warnung", p.ExitCode == 0 && !espBuild.Contains("warning"), espBuild);
        }
        var cliEsp = CommandLineParser.Parse(new[] { "native", "a.script", "-t", "esp32" });
        CheckNat("Befehlszeile: -t esp32 / unbekanntes Ziel / -t ausserhalb von native",
            cliEsp.Error == null && cliEsp.Target == esp && CommandLineParser.Parse(new[] { "native", "a.script", "-t", "amiga" }).TargetName == "amiga"
            && CommandLineParser.Parse(new[] { "run", "a.script", "-t", "esp32" }).Error != null && CommandLineParser.Parse(new[] { "native", "a.script" }).Target == null);

        string RunProc(string tool, string arguments, string dir, out int exit, string? input = null)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, arguments)
            { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null, UseShellExecute = false, WorkingDirectory = dir })!;
            if (input != null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
            var errTask = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            exit = p.ExitCode;
            return output + errTask.Result;
        }
        // ---- Devices: the same scripts as in the device checks of the VM, natively with the loopback device (FIRE_DEVICES), against the expected outputs
        {
            var devTasks = devNativeCases.Select((c, index) => Task.Run(() =>
            {
                var defines = new List<string> { "FIRE_DEVICES=\"loopback\"" };
                if (c.DefaultId != null) defines.Add($"FIRE_DEFAULT_DEVICE=\"{c.DefaultId}\"");
                var target = TargetProfile.Host with { Native = TargetProfile.Host.Native with { Defines = defines } };
                string cpp;
                try { cpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { c.Script }, null, null, VmExecutionMode.Release, null, target), target); }
                catch (fire.Native.NativeNotSupportedException ex) { return (c.Title, "", $"nicht uebersetzbar: {ex.Message}"); }
                string file = Path.Combine(workDir, $"dev{index}.cpp"), exe = Path.Combine(workDir, $"dev{index}.bin");
                File.WriteAllText(file, cpp);
                string build = RunProc(cxx, $"-std=c++17 -pthread -O2 -Wall -Wextra {sanitize}\"{file}\" -I\"{workDir}\" -o \"{exe}\"", workDir, out int buildExit);
                if (buildExit != 0 || build.Contains("warning:")) return (c.Title, "", "C++-Compiler: " + build);
                string actual = RunProc(exe, "", workDir, out int runExit);
                return (c.Title, string.Concat(c.Expected.Select(l => l + "\n")), runExit == 0 ? actual : $"Exitcode {runExit}: {actual}");
            })).ToArray();
            Task.WaitAll(devTasks);
            foreach (var task in devTasks)
            {
                var (title, expected, actual) = task.Result;
                CheckNat($"Geraete nativ: {title}", expected == actual, $"  erwartet:\n{expected}\n  erhalten:\n{actual}");
            }
            CheckNat("Geraete nativ: es gibt Faelle", devNativeCases.Count >= 10, devNativeCases.Count.ToString());
        }

        // ---- Window (bridges/fire_bridge_windows.hpp): SDL2 with the dummy driver; the events are provided by FIRE_DISPLAY_SELFTEST (the VM uses SDL3, there is no comparison here)
        {
            string sdlProbe = Path.Combine(workDir, "sdlprobe.cpp");
            File.WriteAllText(sdlProbe, "#if __has_include(<SDL2/SDL.h>)\n#include <SDL2/SDL.h>\n#else\n#include <SDL.h>\n#endif\nint main() { return SDL_Init(0); }\n");
            RunProc(cxx, $"-std=c++17 \"{sdlProbe}\" -lSDL2 -o \"{sdlProbe}.bin\"", workDir, out int sdlExit);
            var windowCases = new (string Title, string Script, string[] Expected, string Define)[]
            {
                ("Fenster: Framebuffer anzeigen, VSync, Tick, Ereignisse anmelden", """
            #import "windows"
            var fb = new Framebuffer(64, 48)
            var con = new Renderer(fb)
            con.Clear()
            con.FillRect(4, 4, 20, 10, new SolidBrush(12))
            var win = new Window(fb, "fire test")
            print(win.VSync)
            win.VSync = false
            print(win.VSync)
            print(win.EnableEvents())
            print(win.Tick())
            print(win.NextEvent())
            print(win.RegisterKeyDown(func(int k, int s, int m, bool r) => { print("key") }))
            print(win.RegisterClose(func() => { print("close") }))
            print(win.RegisterTextInput(func(string t) => { print("text " + t) }))
            for (var i = 0; i < 3; i++) { con.FillRect(i * 5, 20, 4, 4, new SolidBrush(9)); win.Tick() }
            print(win.Tick())
            """, new[] { "True", "False", "True", "True", "undefined", "True", "True", "True", "True" }, ""),
                ("Fenster: AutoResize bringt den Framebuffer auf die Groesse des Fensters", """
            #import "windows"
            var fb = new Framebuffer(64, 48)
            var win = new Window(fb, "resize")
            print(win.AutoResize)
            win.AutoResize = true
            print(win.AutoResize)
            win.EnableEvents()
            win.RegisterResize(func(int w, int h) => { print("cb " + w + " " + h) })
            print(win.Tick())
            print(fb.Width() + "x" + fb.Height())
            var e = win.NextEvent()
            print(e[0] + " " + e[1] + " " + e[2])
            print(fb.Resize(0, 5))
            print(fb.Resize(20000, 5))
            print(fb.Width() + "x" + fb.Height())
            print(fb.Resize(100, 80))
            print(fb.Width() + "x" + fb.Height())
            """, new[] { "False", "True", "cb 90 70", "True", "90x70", "4 90 70", "False", "False", "90x70", "True", "100x80" }, "-DFIRE_DISPLAY_SELFTEST_RESIZE "),
                ("Fenster: Touch- und Joystick-Ereignisse, TouchMouse", """
            #import "windows"
            var fb = new Framebuffer(64, 48)
            var win = new Window(fb, "input")
            print(win.TouchMouse)
            win.TouchMouse = false
            print(win.TouchMouse)
            win.RegisterTouchDown(func(int f, float x, float y, float p) => { print("down " + f + " " + x + " " + y + " " + p) })
            win.RegisterJoystickAxis(func(int j, int a, float v) => { print("axis " + j + " " + a + " " + v) })
            win.RegisterJoystickHat(func(int j, int h, int m) => { print("hat " + j + " " + h + " " + m) })
            win.RegisterJoystickButtonDown(func(int j, int b) => { print("button " + j + " " + b) })
            win.RegisterJoystickRemoved(func(int j) => { print("removed " + j) })
            win.EnableEvents()
            print(win.Tick())
            var e = win.NextEvent()
            while (e != undefined) {
                var line = ""
                for (var i = 0; i < e.length; i++) { line = line + e[i] + " " }
                print("queued " + line)
                e = win.NextEvent()
            }
            """, new[] { "True", "False", "down 5 32 12 1", "axis 7 1 -1", "button 7 3", "hat 7 0 3", "removed 7", "True", "queued 16 5 32 12 1 ", "queued 17 5 48 24 0.5 ", "queued 18 5 48 24 0 ", "queued 32 7 1 -1 ", "queued 33 7 3 ", "queued 34 7 3 ", "queued 35 7 0 3 ", "queued 37 7 " }, "-DFIRE_DISPLAY_SELFTEST_INPUT "),
                ("Fenster: Ereignisse als Callbacks und aus der Warteschlange, Schliessen beendet Tick", """
            #import "windows"
            var fb = new Framebuffer(64, 48)
            var win = new Window(fb, "events")
            var log = ""
            win.RegisterKeyDown(func(int k, int s, int m, bool r) => { print("keydown " + k + " " + s + " " + m + " " + r) })
            win.RegisterKeyUp(func(int k, int s, int m, bool r) => { print("keyup " + k + " " + s + " " + m + " " + r) })
            win.RegisterMouseDown(func(int b, float x, float y) => { print("down " + b + " " + x + " " + y) })
            win.RegisterMouseUp(func(int b, float x, float y) => { print("up " + b + " " + x + " " + y) })
            win.RegisterMouseMove(func(float x, float y, int st) => { print("move " + x + " " + y + " " + st) })
            win.RegisterMouseMoveRelative(func(float x, float y, int st) => { print("rel " + x + " " + y + " " + st) })
            win.RegisterMouseScroll(func(float sx, float sy, float x, float y) => { print("scroll " + sx + " " + sy) })
            win.RegisterTextInput(func(string t) => { print("text " + t + " " + t.Length) })
            win.RegisterCloseRequest(func() => { print("closerequest") })
            win.RegisterClose(func() => { print("close") })
            win.EnableEvents()
            var open = win.Tick()
            print("open " + open)
            var e = win.NextEvent()
            while (e != undefined) {
                var line = ""
                for (var i = 0; i < e.length; i++) { line = line + e[i] + " " }
                print("queued " + line)
                e = win.NextEvent()
            }
            print(win.NextEvent())
            """, new[] { "keydown 97 4 1 False", "keyup 97 4 0 False", "down 1 32 24", "up 1 32 24", "move 10 12 1", "rel 3 -2 1", "scroll 0 1.5", "text éx 2", "closerequest", "close", "open False", "queued 24 97 4 1 False ", "queued 25 97 4 0 False ", "queued 8 1 32 24 ", "queued 11 1 32 24 ", "queued 9 10 12 1 ", "queued 10 3 -2 1 ", "queued 12 0 1.5 0 0 ", "queued 3 éx ", "queued 2 ", "queued 1 ", "undefined" }, "-DFIRE_DISPLAY_SELFTEST "),
            };
            var windowTarget = TargetProfile.Host;
            foreach (var wc in windowCases)
            {
                string cpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { wc.Script }, null, null, VmExecutionMode.Release, null, windowTarget), windowTarget);
                CheckNat($"Fenster: {wc.Title}: das Programm bittet um SDL2", cpp.Contains("// fire-link: SDL2") && cpp.Contains("fire_display.hpp"), "");
                if (sdlExit != 0) { Console.WriteLine("(SDL2 nicht verfuegbar: das Fenster wird nicht ausgefuehrt)"); continue; }
                string file = Path.Combine(workDir, "window_" + Math.Abs(wc.Title.GetHashCode()) + ".cpp"), exe = file + ".bin";
                File.WriteAllText(file, cpp);
                var command = fire.Compiler.NativeBuilder.CompilerCommand(fire.Native.ToolchainDef.BuiltIn["gcc"], windowTarget, file, workDir, exe);
                string build = RunProc(cxx, $"-std=c++17 -pthread -O2 -Wall -Wextra {sanitize}{wc.Define}\"{file}\" -I\"{workDir}\" -lSDL2 -o \"{exe}\"", workDir, out int buildExit);
                if (buildExit != 0 || build.Contains("warning:")) { CheckNat($"Fenster: {wc.Title}", false, "C++-Compiler: " + build); continue; }
                Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "dummy");
                string actual = RunProc(exe, "", workDir, out int runExit);
                string expected = string.Concat(wc.Expected.Select(l => l + "\n"));
                CheckNat($"Fenster: {wc.Title}", runExit == 0 && actual == expected, $"  erwartet:\n{expected}\n  erhalten:\n{actual}");
                CheckNat($"Fenster: {wc.Title}: der Build haengt -lSDL2 an", command.Arguments.Contains("-lSDL2"), command.Arguments);
            }
        }

        // ---- UI library: the same elements natively (window with the dummy driver) against the VM with the dummy
        {
            string sdlProbe2 = Path.Combine(workDir, "sdlprobe.cpp.bin");
            if (!File.Exists(sdlProbe2)) Console.WriteLine("(SDL2 nicht verfuegbar: die UI wird nicht nativ ausgefuehrt)");
            else
            {
                string cpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "#import \"ui\"\n" + uiDrawScript }, null, null, VmExecutionMode.Release));
                string file = Path.Combine(workDir, "uidraw.cpp"), exe = file + ".bin";
                File.WriteAllText(file, cpp);
                string build = RunProc(cxx, $"-std=c++17 -pthread -O2 -Wall -Wextra {sanitize}\"{file}\" -I\"{workDir}\" -lSDL2 -o \"{exe}\"", workDir, out int buildExit);
                if (buildExit != 0 || build.Contains("warning:")) CheckNat("UI nativ == VM", false, "C++-Compiler: " + build);
                else
                {
                    Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "dummy");
                    string actual = RunProc(exe, "", workDir, out int runExit);
                    string expected = string.Concat(uiDrawExpected.Select(l => l + "\n"));
                    CheckNat("UI nativ == VM: Label, Button, CheckBox, TextBox, Stack zeichnen", runExit == 0 && actual == expected && uiDrawExpected.Length == 4, $"  erwartet (VM):\n{expected}\n  erhalten:\n{actual}");
                }
            }
        }

        // ---- Platform layer: the same thread programs on FreeRTOS (tasks, semaphores) - here on the simulator (native/sim, pthreads)
        {
            string[] rtosCases = { "Actor: fire with", "Actor: mehrere", "sync: die Kopie", "sync: Arrays", "taking: der Thread", "terminate im Hauptprogramm", "catch threads()",
                "Globals: sync global ist atomar", "Globals: fire global mit taking", "Thread startet Thread", "Globals: #nosync haelt", "leave aus einer Funktion" };
            var rtosIndexes = Enumerable.Range(0, natCases.Length).Where(i => natCases[i].Name.StartsWith("Threads:") && rtosCases.Any(c => natCases[i].Name.Contains(c))).ToArray();
            var rtosTasks = rtosIndexes.Select(index => Task.Run(() =>
            {
                string expected = vmResults[index];
                string cpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { natCases[index].Source }, null, null, VmExecutionMode.Release, null, TargetProfile.FreeRtos), TargetProfile.FreeRtos);
                string file = Path.Combine(workDir, $"rtos{index}.cpp"), exe = Path.Combine(workDir, $"rtos{index}.bin");
                // the board's startup code would call the entry point from a task; here main does
                File.WriteAllText(file, cpp + "\nint main() { fire_start(); return 0; }\n");
                string build = RunProc(cxx, $"-std=c++17 -pthread -O2 -Wall -Wextra {sanitize}\"{file}\" -I\"{workDir}\" -I\"{Path.Combine(workDir, "sim")}\" -o \"{exe}\"", workDir, out int buildExit);
                if (buildExit != 0 || build.Contains("warning:")) return (natCases[index].Name, expected, "C++-Compiler: " + build);
                string actual = RunProc(exe, "", workDir, out _);   // (a FreeRTOS program has no exit code)
                return (natCases[index].Name, expected, actual);
            })).ToArray();
            Task.WaitAll(rtosTasks);
            foreach (var task in rtosTasks)
            {
                var (name, expected, actual) = task.Result;
                CheckNat($"FreeRTOS == VM: {name}", expected == actual, $"  erwartet (VM):\n{expected}\n  erhalten (FreeRTOS):\n{actual}");
            }
            CheckNat("FreeRTOS: es gibt Faelle", rtosIndexes.Length >= 10, rtosIndexes.Length.ToString());
        }

        // ---- Target configuration (fire.native.json) and `build` with the native engine
        {
            var config = fire.Native.NativeConfig.Parse("""
                {
                  // a comment, and a trailing comma
                  "engine": "native", "target": "board",
                  "targets": {
                    "board": { "extends": "freertos", "includes": ["my_rtos.h"], "defines": ["BOARD_X=3", "FIRE_THREAD_PRIORITY=5"], "stackBytes": 6000,
                               "entry": { "name": "board_main", "externC": false }, "toolchain": "mine" },
                    "pc": { "platform": "posix", "compileArgs": ["-pthread"], },
                  },
                  "toolchains": { "mine": { "extends": "gcc", "optimization": "-O1", "args": ["-Wall"] } }
                }
                """);
            var board = config.ResolveTarget();
            CheckNat("Konfiguration: Ziel erbt vom eingebauten und ueberschreibt", board.Name == "board" && board.Native.Platform == "freertos" && board.FloatWidth == 32
                && board.DefaultStackBytes == 6000 && board.Native.Includes.SequenceEqual(new[] { "my_rtos.h" }) && board.Native.Entry.Name == "board_main"
                && !board.Native.Entry.ExternC && board.Native.Entry.Kind == fire.Runtime.EntryKind.Function && board.IsEmbedded);
            var tc = config.ResolveToolchain(board);
            CheckNat("Konfiguration: Toolchain erbt vom eingebauten", tc.EffectiveKind == "gcc" && tc.Optimization == "-O1" && tc.EffectiveCompiler == "g++" && tc.Std == "c++17" && tc.Args!.SequenceEqual(new[] { "-Wall" }));
            bool unknownTarget = false;
            try { config.ResolveTarget("amiga"); } catch (fire.Native.NativeConfigException) { unknownTarget = true; }
            CheckNat("Konfiguration: unbekanntes Ziel wird abgelehnt, Standard ist der Rechner", unknownTarget && new fire.Native.NativeConfig().ResolveTarget() == TargetProfile.Host);
            var roundTrip = fire.Native.NativeConfig.Parse(config.ToJson());
            CheckNat("Konfiguration: speichern und wieder lesen", roundTrip.ResolveTarget("board").Native.Defines.SequenceEqual(new[] { "BOARD_X=3", "FIRE_THREAD_PRIORITY=5" }) && roundTrip.Engine == "native");
            string boardCpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "print(1)" }, null, null, VmExecutionMode.Release, null, board), board);
            int incAt = boardCpp.IndexOf("#include <my_rtos.h>", StringComparison.Ordinal), platAt = boardCpp.IndexOf("#define FIRE_PLATFORM_HEADER \"platform/freertos/fire_platform.hpp\"", StringComparison.Ordinal);
            CheckNat("Konfiguration: Defines, Includes vor der Plattform und Einsprung stehen im C++", boardCpp.Contains("#define BOARD_X 3") && incAt > 0 && platAt > incAt
                && boardCpp.Contains("void board_main(void) {") && !boardCpp.Contains("extern \"C\" void board_main") && boardCpp.IndexOf("#define BOARD_X 3", StringComparison.Ordinal) < incAt);

            // conditional compilation (#if): symbols come from the target, the engine and -D
            {
                string Pp(string src, TargetProfile t, string engine = "vm", params string[] defs)
                {
                    var reg = new DirectiveRegistry();
                    foreach (var sym in ConditionalSymbols.For(t, engine, null, defs)) reg.Symbols.Add(sym);
                    return Preprocessor.Process(src, ".", reg).Source;
                }
                string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                string pick = "#if windows\nW\n#elif esp32 && native\nE\n#elif linux || macos\nP\n#else\nO\n#endif\n";
                CheckNat("#if: das Ziel waehlt den Zweig", Lines(Pp(pick, TargetProfile.Windows)).SequenceEqual(new[] { "W" }) && Lines(Pp(pick, TargetProfile.Esp32, "native")).SequenceEqual(new[] { "E" })
                    && Lines(Pp(pick, TargetProfile.Esp32, "vm")).SequenceEqual(new[] { "O" }) && Lines(Pp(pick, TargetProfile.Linux)).SequenceEqual(new[] { "P" }) && Lines(Pp(pick, TargetProfile.MacOs)).SequenceEqual(new[] { "P" }));
                string numbered = "a\n#if false\nb\nc\n#endif\nd\n";
                CheckNat("#if: die Zeilennummern bleiben erhalten", Pp(numbered, TargetProfile.Linux).Split('\n').ToList().IndexOf("d") == 5 && Lines(Pp(numbered, TargetProfile.Linux)).SequenceEqual(new[] { "a", "d" }));
                CheckNat("#if: Ausdruecke (!, &&, ||, Klammern, true/false, Gross-/Kleinschreibung)",
                    Lines(Pp("#if !(windows || macos) && LINUX\nyes\n#endif\n#if false || (true && !linux)\nno\n#endif\n#if float32\nf32\n#endif\n", TargetProfile.Linux)).SequenceEqual(new[] { "yes" })
                    && Lines(Pp("#if float32\nf32\n#endif\n", TargetProfile.Esp32)).SequenceEqual(new[] { "f32" }));
                string nested = "#if linux\n1\n#if windows\n2\n#else\n3\n#endif\n#else\n4\n#if broken ((\n5\n#elif also broken\n6\n#endif\n#unknownthing\n#endif\n";
                CheckNat("#if: verschachtelt, ein nicht gewaehlter Zweig wird nicht gelesen", Lines(Pp(nested, TargetProfile.Linux)).SequenceEqual(new[] { "1", "3" }));
                CheckNat("#define, #undef, #ifdef, #ifndef", Lines(Pp("#ifdef X\nA\n#endif\n#define X\n#ifdef X\nB\n#endif\n#ifndef X\nC\n#endif\n#undef X\n#ifndef X\nD\n#endif\n#if Y\nE\n#endif\n", TargetProfile.Linux, "vm", "Y"))
                    .SequenceEqual(new[] { "B", "D", "E" }));
                string ifErr(string src) { try { Pp(src, TargetProfile.Linux); return "no error"; } catch (PreprocessorException ex) { return ex.Message; } }
                CheckNat("#if: Fehler (fehlendes #endif, #else/#endif/#elif ohne #if, zweites #else, #elif nach #else, falscher Ausdruck, #error)",
                    ifErr("#if linux\nx\n").Contains("no '#endif'") && ifErr("#else\n").Contains("without '#if'") && ifErr("#endif\n").Contains("without '#if'") && ifErr("#elif a\n").Contains("without '#if'")
                    && ifErr("#if a\n#else\n#else\n#endif\n").Contains("second '#else'") && ifErr("#if a\n#else\n#elif b\n#endif\n").Contains("after '#else'") && ifErr("#if a ||\n#endif\n").Contains("ends too early")
                    && ifErr("#if (a\n#endif\n").Contains("')' is missing") && ifErr("#if a $ b\n#endif\n").Contains("unexpected character") && ifErr("#if linux\n#error nur Windows\n#endif\n").Contains("#error nur Windows") && ifErr("#if windows\n#error nicht gelesen\n#endif\n") == "no error");
                var gated = "#if esp32\n#import \"graphics\"\n#endif\n#if linux\n#import \"time\"\n#endif\n";
                CheckNat("#if: ein #import in einem nicht gewaehlten Zweig zaehlt nicht (Editor)", ImportedPreludes.FindImportNames(gated, ConditionalSymbols.For(TargetProfile.Linux)).SequenceEqual(new[] { "time" })
                    && ImportedPreludes.FindImportNames(gated, ConditionalSymbols.For(TargetProfile.Esp32)).SequenceEqual(new[] { "graphics" }));
                var greySource = "a\n#if esp32\nb\n#else\nc\n#endif\n#ifdef EXTRA\nd\n#endif\ne";
                var greyLinux = ConditionalSymbols.InactiveLines(greySource, ConditionalSymbols.For(TargetProfile.Linux));
                var greyDefined = ConditionalSymbols.InactiveLines(greySource, ConditionalSymbols.For(TargetProfile.Esp32, "native", null, new[] { "EXTRA" }));
                CheckNat("#if: die Zeilen nicht gewaehlter Zweige werden erkannt (Editor: ausgegraut)",
                    string.Join(",", greyLinux.Select(x => x ? "1" : "0")) == "0,0,1,0,0,0,0,1,0,0" && string.Join(",", greyDefined.Select(x => x ? "1" : "0")) == "0,0,0,0,1,0,0,0,0,0");
                string ifDir = Path.Combine(workDir, "ifbuild");
                Directory.CreateDirectory(ifDir);
                string ifScript = Path.Combine(ifDir, "cond.script");
                File.WriteAllText(ifScript, "#if native\nprint(\"engine native\")\n#elif vm\nprint(\"engine vm\")\n#endif\n#if EXTRA\nprint(\"extra\")\n#endif\n#if windows\nprint(\"windows\")\n#elif posix\nprint(\"posix\")\n#endif\n");
                File.WriteAllText(Path.Combine(ifDir, "fire.native.json"), "{ \"engine\": \"native\", \"target\": \"" + TargetProfile.Host.Name + "\", \"toolchain\": \"" + (cxx.Contains("clang") ? "clang" : "gcc") + "\" }");
                string ifExe = Path.Combine(ifDir, "cond.out");
                int ifCode = CommandLineRunner.Run(new[] { "build", ifScript, "-D", "EXTRA", "-o", ifExe }, new StringWriter(), new StringWriter());
                string ifRan = ifCode == 0 ? RunProc(ifExe, "", ifDir, out _) : "";
                CheckNat("#if: build --engine native setzt native, das Ziel und -D", ifCode == 0 && ifRan == "engine native\nextra\n" + (TargetProfile.Host.Name == "windows" ? "windows" : "posix") + "\n", ifRan);
                var badDefine = CommandLineParser.Parse(new[] { "run", ifScript, "-D", "1x" });
                CheckNat("#if: -D braucht einen Namen", badDefine.Error != null && CommandLineParser.Parse(new[] { "run", ifScript, "-DA", "--define", "B", "-D=C" }).Defines.SequenceEqual(new[] { "A", "B", "C" }));
            }

            // the console through IO.Stdio: the standard input is read (lines end with \n, \r\n or \r), output and errors go to their streams
            {
                string ioDir = Path.Combine(workDir, "iobuild");
                Directory.CreateDirectory(ioDir);
                string ioScript = Path.Combine(ioDir, "console.script");
                File.WriteAllText(ioScript, "#import \"io\"\nprint(\"first\")\nvar a = IO.Stdio.ReadLine()\nIO.Stdio.WriteLine(\"a=\" + a)\nIO.Stdio.ErrorLine(\"to stderr\")\nvar b = IO.Stdio.ReadLine()\nvar rest = IO.Stdio.ReadAll()\nprint(\"b=\" + b + \" rest=\" + rest.length)\nprint(IO.Stdio.ReadLine() == undefined)\nvar so = IO.Stdio.Out()\nvar w = new IO.TextWriter(so, true)\nw.WriteLine(\"via writer\")\nw.Flush()\n");
                File.WriteAllText(Path.Combine(ioDir, "fire.native.json"), "{ \"engine\": \"native\", \"target\": \"" + TargetProfile.Host.Name + "\", \"toolchain\": \"" + (cxx.Contains("clang") ? "clang" : "gcc") + "\" }");
                string ioExe = Path.Combine(ioDir, "console.out");
                int ioCode = CommandLineRunner.Run(new[] { "build", ioScript, "-o", ioExe }, new StringWriter(), new StringWriter());
                string ioRan = ioCode == 0 ? RunProc(ioExe, "", ioDir, out _, "one\r\ntwo\nthree\rfour") : "";
                string[] ioLines = ioRan.Split('\n');
                CheckNat("IO: Standardeingabe, -ausgabe und -fehler", ioCode == 0 && ioLines.Contains("first") && ioLines.Contains("a=one") && ioLines.Contains("to stderr") && ioLines.Contains("b=two rest=10") && ioLines.Contains("True") && ioLines.Contains("via writer"), ioRan);
            }

            // build --engine native through the command line runner: a program, and the files of a project
            string dir = Path.Combine(workDir, "build");
            Directory.CreateDirectory(dir);
            string script = Path.Combine(dir, "hello.script");
            File.WriteAllText(script, "var n = 0\nfire { sync global { n = 41 + 1 } }\nwhile (n == 0) { sync globals }\nprint(\"n \" + n)\n");
            File.WriteAllText(Path.Combine(dir, "fire.native.json"), "{ \"engine\": \"native\", \"target\": \"" + TargetProfile.Host.Name + "\", \"toolchain\": \"" + (cxx.Contains("clang") ? "clang" : "gcc") + "\" }");
            string exeOut = Path.Combine(dir, "hello.out");
            var outW = new StringWriter(); var errW = new StringWriter();
            int code = CommandLineRunner.Run(new[] { "build", script, "-o", exeOut }, outW, errW);
            string ran = code == 0 ? RunProc(exeOut, "", dir, out _) : "";
            CheckNat("build: der native Motor aus fire.native.json baut ein Programm", code == 0 && ran == "n 42\n", $"{code}\n{outW}\n{errW}\n{ran}");
            string projectOut = Path.Combine(dir, "project");
            var out2 = new StringWriter(); var err2 = new StringWriter();
            int code2 = CommandLineRunner.Run(new[] { "build", script, "-t", "esp32", "-o", projectOut }, out2, err2);
            CheckNat("build: Ziel esp32 schreibt die Dateien eines ESP-IDF-Komponenten", code2 == 0 && File.Exists(Path.Combine(projectOut, "fire_program.cpp")) && File.Exists(Path.Combine(projectOut, "fire_rt.hpp"))
                && File.Exists(Path.Combine(projectOut, "platform", "esp32", "fire_platform.hpp")) && File.Exists(Path.Combine(projectOut, "platform", "freertos", "fire_platform.hpp"))
                && File.ReadAllText(Path.Combine(projectOut, "CMakeLists.txt")).Contains("idf_component_register") && File.ReadAllText(Path.Combine(projectOut, "fire_program.cpp")).Contains("void app_main(void)"), $"{code2}\n{out2}\n{err2}");
            var err3 = new StringWriter();
            int code3 = CommandLineRunner.Run(new[] { "build", script, "-t", "amiga", "-o", Path.Combine(dir, "x") }, new StringWriter(), err3);
            CheckNat("build: unbekanntes Ziel ist ein Fehler der Befehlszeile", code3 == CommandLineRunner.ExitUsage && err3.ToString().Contains("amiga"), err3.ToString());
            var out4 = new StringWriter();
            int code4 = CommandLineRunner.Run(new[] { "build", script, "--engine", "vm", "-o", Path.Combine(dir, "vm.exe") }, out4, new StringWriter());
            CheckNat("build: --engine vm gewinnt gegen die Konfiguration", code4 == 0 && File.Exists(Path.Combine(dir, "vm.exe")));
        }

        // What is not yet translated must be rejected clearly - never translated wrongly
        try
        {
            fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "#import \"windows\"\nvar x = __GRPHWinCreate(1)" }, null, null, VmExecutionMode.Release));
            CheckNat("Nicht unterstuetzte Opcodes werden abgelehnt", false, "keine Ausnahme");
        }
        catch (fire.Native.NativeNotSupportedException ex)
        {
            CheckNat("Nicht unterstuetzte native Funktionen werden abgelehnt", ex.Message.Contains("native function"), ex.Message);
        }
        try { Directory.Delete(workDir, true); } catch (IOException) { }
    }

    // ---- Packages (ember): fpk, store, sources, #import "name" in VM and native ----
    {
        Console.WriteLine("=== Pakete (ember) ===");
        string pkgDir = Path.Combine(Path.GetTempPath(), "fire-pkg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pkgDir);
        var savedStore = fire.Package.Manager.PackageStore.Default;
        try
        {
            // a package whose parts are files in a folder of its own; returns the path of the forge file
            string MakeForge(string name, string version, string importName, string? prelude, string? nativeCpp, Action<fire.Package.Manager.PackageManifest>? tweak = null)
            {
                string dir = Path.Combine(pkgDir, "src-" + name + "-" + version);
                Directory.CreateDirectory(dir);
                var m = new fire.Package.Manager.PackageManifest { Name = name, Version = version, Author = "tester", Description = "test package " + name };
                var import = new fire.Package.Manager.PackageImport { Name = importName };
                if (prelude != null) { File.WriteAllText(Path.Combine(dir, importName + ".fire"), prelude); import.Prelude = Path.Combine(dir, importName + ".fire"); }
                if (nativeCpp != null)
                {
                    File.WriteAllText(Path.Combine(dir, importName + ".hpp"), nativeCpp);
                    import.Native = new fire.Package.Manager.PackageNative { Sources = { Path.Combine(dir, importName + ".hpp") } };
                }
                m.Imports.Add(import);
                tweak?.Invoke(m);
                string json = Path.Combine(dir, "forge.json");
                m.Save(json);
                return json;
            }

            // the template: forging it works as it is, the package.json has relative paths, the kept description absolute ones and forges the same package again
            string exJson = Path.Combine(pkgDir, "example", "mathkit.json");
            fire.Package.Manager.Templates.Example(exJson, writeFiles: true).Save(exJson);
            var forged = fire.Package.Manager.Fpk.Forge(exJson, Path.Combine(pkgDir, "out"));
            var inside = fire.Package.Manager.Fpk.ReadManifest(forged.PackagePath);
            var kept = fire.Package.Manager.PackageManifest.Load(forged.JsonCopyPath);
            CheckNat("Paket: ember create + forge: package.json im fpk mit relativen Pfaden, die Beschreibung daneben mit absoluten",
                File.Exists(forged.PackagePath) && inside.Name == "mathkit" && inside.Imports[0].Prelude == "mathkit/mathkit.fire" && inside.Imports[0].Native!.Sources[0] == "mathkit/mathkit.hpp"
                && Path.IsPathRooted(kept.Imports[0].Prelude!) && Path.IsPathRooted(kept.Imports[0].Native!.Sources[0]) && forged.JsonCopyPath.Replace('\\', '/').EndsWith("out/json/mathkit-1.0.0.json"));
            var again = fire.Package.Manager.Fpk.Forge(forged.JsonCopyPath, Path.Combine(pkgDir, "out2"));
            CheckNat("Paket: die kopierte Beschreibung baut dasselbe Paket noch einmal", again.Manifest.ToJson() == forged.Manifest.ToJson());
            var blank = fire.Package.Manager.Templates.Blank();
            CheckNat("Paket: ember blank hat alle Felder, aber leer, und ist ungueltig", blank.Imports.Count == 1 && blank.Name == "" && blank.Validate().Count >= 3);
            var reserved = new fire.Package.Manager.PackageManifest { Name = "x", Version = "1.0", Imports = { new fire.Package.Manager.PackageImport { Name = "io", Prelude = "a.fire" } } };
            CheckNat("Paket: ein Importname des Compilers ist verboten, eine fehlende Datei beim Schmieden ein Fehler",
                reserved.Validate().Any(p => p.Contains("belongs to the compiler")) && PkgThrows(() => fire.Package.Manager.Fpk.Forge(MakeForge("missing", "1.0.0", "missing", "class A {}", null, m => m.Imports[0].Prelude = Path.Combine(pkgDir, "nothere.fire")), Path.Combine(pkgDir, "out"))));

            // the store: install, find the import (not case sensitive), replace, remove
            var store = new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "Packages"));
            string demoFpk = fire.Package.Manager.Fpk.Forge(MakeForge("pkgdemo", "1.0.0", "pkgdemo", "class PkgDemo { static Twice(x) { return x * 2 } }", null), Path.Combine(pkgDir, "out")).PackagePath;
            store.Install(demoFpk);
            CheckNat("Paket: installieren, den Import finden (Gross-/Kleinschreibung egal) und entfernen",
                store.Find("PKGDEMO") is { Version: "1.0.0" } && store.FindImport("PkgDemo")?.Key == "pkg:pkgdemo" && store.FindImport("PkgDemo")!.ReadPrelude()!.Contains("Twice")
                && store.Remove("pkgdemo") && store.Find("pkgdemo") == null && !store.Remove("pkgdemo"));

            // a zip that leaves its folder is refused
            string evil = Path.Combine(pkgDir, "evil.fpk");
            using (var zip = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
            {
                var m = new fire.Package.Manager.PackageManifest { Name = "evil", Version = "1.0.0", Imports = { new fire.Package.Manager.PackageImport { Name = "evil", Prelude = "../evil.fire" } } };
                using (var w = new StreamWriter(zip.CreateEntry("package.json").Open())) w.Write(m.ToJson());
                using (var w = new StreamWriter(zip.CreateEntry("../evil.fire").Open())) w.Write("class E {}");
            }
            CheckNat("Paket: ein fpk mit einem Pfad aus dem Ordner heraus wird abgelehnt", PkgThrows(() => store.Install(evil)) && !File.Exists(Path.Combine(pkgDir, "evil.fire")));

            // the sources: a folder of packages (two versions, a dependency) and an index with checksums
            string sourceDir = Path.Combine(pkgDir, "PackageSource");
            Directory.CreateDirectory(sourceDir);
            foreach (var (n, v, dep) in new[] { ("libbase", "1.0.0", ""), ("libtop", "1.0.0", "libbase"), ("libtop", "1.2.0", "libbase"), ("libtop", "1.10.0", "libbase") })
            {
                string forge = MakeForge(n, v, n, $"class {n}V{v.Replace(".", "_")} {{ }}", null, m => { if (dep.Length > 0) m.Dependencies.Add(dep); });
                File.Copy(fire.Package.Manager.Fpk.Forge(forge, Path.Combine(pkgDir, "built")).PackagePath, Path.Combine(sourceDir, $"{n}-{v}.fpk"), true);
            }
            var service = new fire.Package.Manager.PackageManagerService(new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "P2")), new fire.Package.Manager.IPackageSource[] { new fire.Package.Manager.FolderSource(sourceDir) });
            var found = service.Find("lib");
            var top = found.First(l => l.Name == "libtop");
            CheckNat("Paket: find sucht in Namen und Beschreibung, die neueste Version zaehlt numerisch (1.10 > 1.2)", found.Count == 2 && top.Versions.Count == 3 && top.Latest!.Version == "1.10.0" && service.Find("LIBTOP").Count == 1 && service.Find("nothing like it").Count == 0);
            var pkgLog = new List<string>();
            service.Install("libtop@1.2.0", pkgLog.Add);
            CheckNat("Paket: install holt die gewuenschte Version samt Abhaengigkeit, ein zweites Mal ist nichts zu tun",
                service.Store.Find("libtop")?.Version == "1.2.0" && service.Store.Find("libbase") != null && PkgThrows(() => service.Install("libtop@9.9.9")) && service.Install("libtop@1.2.0", pkgLog.Add).Count == 0);
            bool refusedDependency = PkgThrows(() => service.Remove("libbase"));
            service.Remove("libtop");
            service.Remove("libbase");
            CheckNat("Paket: remove verweigert, was ein anderes Paket braucht", refusedDependency && service.Store.Installed().Count == 0);
            string indexFile = Path.Combine(sourceDir, "index.json");
            File.WriteAllText(indexFile, fire.Package.Manager.PackageIndex.Build(sourceDir, null));
            var indexed = new fire.Package.Manager.IndexSource(indexFile).List();
            CheckNat("Paket: ein Index (ember index) nennt Namen, Autor, Beschreibung, Versionen und Pruefsummen", indexed.Count == 2 && indexed.First(l => l.Name == "libtop").Versions.Count == 3 && indexed.First(l => l.Name == "libtop").Author == "tester"
                && indexed.All(l => l.Versions.All(v => v.Sha256 is { Length: 64 } && File.Exists(v.Url))));
            var viaIndex = new fire.Package.Manager.PackageManagerService(new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "P3")), new fire.Package.Manager.IPackageSource[] { new fire.Package.Manager.IndexSource(indexFile) });
            viaIndex.Install("libbase");
            var broken = new fire.Package.Manager.PackageVersion("1.0.0", Path.Combine(sourceDir, "libbase-1.0.0.fpk"), new string('0', 64));
            CheckNat("Paket: install ueber einen Index, eine falsche Pruefsumme wird abgelehnt", viaIndex.Store.Find("libbase") != null && PkgThrows(() => new fire.Package.Manager.IndexSource(indexFile).Fetch(broken, Path.Combine(pkgDir, "dl"))));

            // #import "name": the prelude of a package in the VM; an unknown import points to ember
            fire.Package.Manager.PackageStore.Default = new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "Packages"));
            fire.Package.Manager.PackageStore.Default.Install(demoFpk);
            string vmPkg = vmOutput("#import \"pkgdemo\"\nprint(PkgDemo.Twice(21))");
            string unknown;
            try { vmOutput("#import \"nosuchpackage\"\nprint(1)"); unknown = ""; } catch (Exception ex) { unknown = ex.Message; }
            CheckNat("Paket: #import \"name\" bringt die Prelude eines installierten Pakets (VM); ein unbekannter Import verweist auf ember", vmPkg == "42\n" && unknown.Contains("not a known extension") && unknown.Contains("ember"), vmPkg + unknown);
            var onEsp = new Linker().CompileAndLink(new[] { "#import \"pkgdemo\"\nprint(PkgDemo.Twice(2))" }, null, null, VmExecutionMode.Release, null, TargetProfile.Esp32);
            CheckNat("Paket: ein Import eines Pakets ist auf jedem Ziel erlaubt (die Plattformen nennt der native Teil)", onEsp.NativeImports.Contains("pkg:pkgdemo"));

            // the natives (C++): not in the VM, in the native backend the source is part of the generated file; needsList gives the native the list of the scope
            string natPrelude = "class PkgNat { static Twice(x) { return __pk_twice(x) }\n static Squares(n) { return __pk_squares(n) } }";
            string natCpp = "#include <cstdint>\nnamespace fire {\ninline Value pk_twice(Value a) { return Int(a.i * 2); }\n"
                + "inline Value pk_squares(Value n, OwnList* list) { Arr* a = allocArr((uint32_t)n.i, list); for (int64_t i = 0; i < n.i; i++) a->items()[i] = Int(i * i); return ArrV(a); }\n}\n";
            string natFpk = fire.Package.Manager.Fpk.Forge(MakeForge("pknat", "1.0.0", "pknat", natPrelude, natCpp, m =>
            {
                m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pk_twice", Arguments = 1, Cpp = "pk_twice" });
                m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pk_squares", Arguments = 1, Cpp = "pk_squares", NeedsList = true, ReturnsReference = true });
            }), Path.Combine(pkgDir, "out")).PackagePath;
            fire.Package.Manager.PackageStore.Default.Install(natFpk);
            string natScript = "#import \"pknat\"\nprint(PkgNat.Twice(21))\nvar s = PkgNat.Squares(4)\nprint(s.length)\nprint(s[3])";
            string pkgCpp = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { natScript }, null, null, VmExecutionMode.Release));
            CheckNat("Paket: der C++-Quelltext des Natives steht im erzeugten C++", pkgCpp.Contains("---- package pknat 1.0.0, import \"pknat\"") && pkgCpp.Contains("inline Value pk_twice") && pkgCpp.Contains("= pk_squares("));
            string? pkgCxx = FindCxx();
            if (pkgCxx != null)
            {
                string run = Path.Combine(pkgDir, "run");
                fire.Native.NativeRuntimeFiles.WriteTo(run);
                File.WriteAllText(Path.Combine(run, "pkg.cpp"), pkgCpp);
                string RunTool(string tool, string args, string? workDirectory = null)
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDirectory ?? run })!;
                    var err = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return (p.ExitCode == 0 ? "" : $"[exit {p.ExitCode}] ") + output + err.Result;
                }
                string build = RunTool(pkgCxx, $"-std=c++17 -pthread -O1 -Wall -Wextra \"{Path.Combine(run, "pkg.cpp")}\" -I\"{run}\" -o \"{Path.Combine(run, "pkg.bin")}\"");
                string result = build.Length == 0 ? RunTool(Path.Combine(run, "pkg.bin"), "") : "C++-Compiler: " + build;
                CheckNat("Paket: das C++-Native laeuft im uebersetzten Programm (Zahlen, ein Array aus der Scope-Liste)", result == "42\n4\n9\n", result);

                // the same natives in the virtual machine: built into a shared library with the C ABI, called through it (numbers, text, arrays, buffers cross; errors are reported)
                string vmNative;
                try { vmNative = vmOutput(natScript); } catch (Exception ex) { vmNative = ex.Message; }
                CheckNat("Paket: dasselbe C++-Native laeuft in der VM ueber eine Shared Library (C-ABI) und liefert dasselbe wie nativ", vmNative == result, vmNative);
                string abiCpp = "#include <cstdint>\nnamespace fire {\n"
                    + "inline Value ab_upper(Value s, OwnList* list) { const Str* t = strOf(s); Str* r = allocStr(t->length, list); for (uint32_t i = 0; i < t->length; i++) strChars(r)[i] = t->data[i] >= 'a' && t->data[i] <= 'z' ? (char16_t)(t->data[i] - 32) : t->data[i]; return StrV(r); }\n"
                    + "inline Value ab_sum(Value arr) { Arr* a = arrOf(arr); double t = 0; for (uint32_t i = 0; i < a->length; i++) t += (double)toR(a->items()[i]); return Float((Real)t); }\n"
                    + "inline Value ab_bytes(Value buf, OwnList* list) { Buf* b = bufOf(buf); Buf* r = allocBuf(b->length, list); for (uint32_t i = 0; i < b->length; i++) r->bytes()[i] = (uint8_t)(b->bytes()[i] + 1); return BufV(r); }\n"
                    + "inline Value ab_fill(Value buf, Value v) { Buf* b = bufOf(buf); for (uint32_t i = 0; i < b->length; i++) b->bytes()[i] = (uint8_t)v.i; return Int(b->length); }\n"
                    + "inline Value ab_fail(Value n) { return indexError(\"Array index\", n.i, 3); }\n}\n";
                string abiFpk = fire.Package.Manager.Fpk.Forge(MakeForge("pkabi", "1.0.0", "pkabi", "class PkAbi { static Upper(s) { return __ab_upper(s) }\n static Sum(a) { return __ab_sum(a) }\n static Bytes(b) { return __ab_bytes(b) }\n static Fill(b, v) { return __ab_fill(b, v) }\n static Fail(n) { return __ab_fail(n) } }", abiCpp, m =>
                {
                    foreach (var (n, c, argc, list) in new[] { ("__ab_upper", "ab_upper", 1, true), ("__ab_sum", "ab_sum", 1, false), ("__ab_bytes", "ab_bytes", 1, true), ("__ab_fill", "ab_fill", 2, false), ("__ab_fail", "ab_fail", 1, false) })
                        m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = n, Arguments = argc, Cpp = c, NeedsList = list, ReturnsReference = list });
                }), Path.Combine(pkgDir, "out")).PackagePath;
                fire.Package.Manager.PackageStore.Default.Install(abiFpk);
                string abiScript = "#import \"pkabi\"\nprint(PkAbi.Upper(\"hello w\u00f6rld\"))\nprint(PkAbi.Sum([1, 2.5, 3]))\nvar b = new byte[3]\nb[0] = 1; b[1] = 2; b[2] = 255\nvar c = PkAbi.Bytes(b)\nprint(c[0] + \" \" + c[1] + \" \" + c[2] + \" \" + b[2])";
                string abiVm;
                try { abiVm = vmOutput(abiScript); } catch (Exception ex) { abiVm = ex.Message; }
                string abiFail;
                try { vmOutput("#import \"pkabi\"\nPkAbi.Fail(7)"); abiFail = ""; } catch (Exception ex) { abiFail = ex.Message; }
                CheckNat("Paket: Zahlen, Text (UTF-16), Arrays und Byte-Puffer gehen ueber die C-ABI hin und zurueck",
                    abiVm == "HELLO W\u00f6RLD\n6.5\n2 3 0 255\n", abiVm);
                string fillVm;
                try { fillVm = vmOutput("#import \"pkabi\"\nvar big = new byte[1000000]\nprint(PkAbi.Fill(big, 7))\nprint(big[0] + \" \" + big[500000] + \" \" + big[999999])\nvar small = new byte[4]\nPkAbi.Fill(small, 9)\nprint(small[3])"); } catch (Exception ex) { fillVm = ex.Message; }
                CheckNat("Paket: ein Byte-Puffer geht ohne Kopie an die Native (in place beschrieben, auch ein grosser wie ein Framebuffer)", fillVm == "1000000\n7 7 7\n9\n", fillVm);
                CheckNat("Paket: ein Fehler der Native (hier IndexOutOfBounds) meldet die VM mit seinem Text", abiFail.Contains("Array index 7 out of range"), abiFail);

                // per platform: sources of the target are added; the VM uses those of this machine
                string whichFpk = fire.Package.Manager.Fpk.Forge(MakeForge("pkwhich", "1.0.0", "pkwhich", "class PkWhich { static Which() { return __pk_which() } }", "namespace fire { }\n", m =>
                {
                    string d = Path.Combine(pkgDir, "which");
                    Directory.CreateDirectory(d);
                    var native = m.Imports[0].Native!;
                    foreach (var (key, value) in new[] { ("posix", 1), ("windows", 2), ("freertos", 3) })
                    {
                        File.WriteAllText(Path.Combine(d, key + ".hpp"), $"namespace fire {{ inline Value pk_which_{key}() {{ return Int({value}); }} }}\n");
                        native.PlatformSources[key] = new List<string> { Path.Combine(d, key + ".hpp") };
                    }
                    File.WriteAllText(Path.Combine(d, "common.hpp"), "namespace fire {\n#if defined(FIRE_HAL_WINDOWS)\ninline Value pk_which() { return pk_which_windows(); }\n#elif defined(FIRE_HAL_FREERTOS)\ninline Value pk_which() { return pk_which_freertos(); }\n#else\ninline Value pk_which() { return pk_which_posix(); }\n#endif\n}\n");
                    native.Sources.Clear();
                    native.Sources.Add(Path.Combine(d, "common.hpp"));
                    native.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pk_which", Arguments = 0, Cpp = "pk_which" });
                }), Path.Combine(pkgDir, "out")).PackagePath;
                fire.Package.Manager.PackageStore.Default.Install(whichFpk);
                string whichScript = "#import \"pkwhich\"\nprint(PkWhich.Which())";
                string cppLinux = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { whichScript }, null, null, VmExecutionMode.Release, null, TargetProfile.Linux), TargetProfile.Linux);
                string cppRtos = fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { whichScript }, null, null, VmExecutionMode.Release, null, TargetProfile.FreeRtos), TargetProfile.FreeRtos);
                string vmWhich;
                try { vmWhich = vmOutput(whichScript); } catch (Exception ex) { vmWhich = ex.Message; }
                CheckNat("Paket: platformSources - jedes Ziel bekommt seine Quellen (Linux: posix, FreeRTOS: freertos), die VM die der eigenen Plattform",
                    cppLinux.Contains("pk_which_posix() {") && !cppLinux.Contains("pk_which_freertos() {") && !cppLinux.Contains("pk_which_windows() {") && cppRtos.Contains("pk_which_freertos() {") && !cppRtos.Contains("pk_which_posix() {")
                    && vmWhich == (OperatingSystem.IsWindows() ? "2\n" : "1\n"), vmWhich);

                // a program that is packed carries the library of its package: it runs where the package is not installed
                string packed = Path.Combine(pkgDir, "packed.bin");
                new Linker().CompileAndLink(new[] { natScript }, null, packed, VmExecutionMode.Release);
                var storeForPack = fire.Package.Manager.PackageStore.Default;
                fire.Package.Manager.PackageStore.Default = new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "EmptyStore"));
                string packedRun = "";
                if (File.Exists(packed) && !OperatingSystem.IsWindows())
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(packed) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
                    var err = p.StandardError.ReadToEndAsync();
                    packedRun = p.StandardOutput.ReadToEnd() + err.Result;
                    p.WaitForExit();
                }
                CheckNat("Paket: ein gepacktes Programm bringt die Bibliothek seiner Pakete mit (laeuft ohne das Paket)", OperatingSystem.IsWindows() || packedRun == "42\n4\n9\n", packedRun);
                fire.Package.Manager.PackageStore.Default = storeForPack;
            }
            // the platform of the native part: a package for windows only is refused for another target
            string winOnly = fire.Package.Manager.Fpk.Forge(MakeForge("pkgwin", "1.0.0", "pkgwin", "class PkgWin { static F(x) { return __pk_win(x) } }", "namespace fire { inline Value pk_win(Value a) { return a; } }\n", m =>
            {
                m.Imports[0].Native!.Platforms.Add("windows");
                m.Imports[0].Native!.Functions.Add(new fire.Package.Manager.PackageNativeFunction { Name = "__pk_win", Arguments = 1, Cpp = "pk_win" });
            }), Path.Combine(pkgDir, "out")).PackagePath;
            fire.Package.Manager.PackageStore.Default.Install(winOnly);
            bool refused = false;
            try { fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "#import \"pkgwin\"\nprint(PkgWin.F(1))" }, null, null, VmExecutionMode.Release, null, TargetProfile.Linux), TargetProfile.Linux); }
            catch (fire.Native.NativeNotSupportedException ex) { refused = ex.Message.Contains("pkgwin") && ex.Message.Contains("windows"); }
            CheckNat("Paket: ein natives Paket nur fuer andere Plattformen wird fuers Ziel abgelehnt", refused);

            // the standard bridges as packages: built into a folder, installed when missing, not again when nothing changed; the compiler still resolves the names to its built-in bridges
            {
                string bridgeSource = Path.Combine(pkgDir, "BridgeSource");
                var built = fire.Compiler.StandardBridgePackages.Build(bridgeSource);
                var ioPackage = fire.Package.Manager.Fpk.ReadManifest(built.First(f => Path.GetFileName(f).StartsWith("fire-io-")));
                var bridgeStore = new fire.Package.Manager.PackageStore(Path.Combine(pkgDir, "BridgePackages"));
                var savedDefault = fire.Package.Manager.PackageStore.Default;
                fire.Package.Manager.PackageStore.Default = bridgeStore;
                try
                {
                    var first = fire.Package.Manager.StandardPackages.EnsureInstalled(null, bridgeSource);
                    var second = fire.Package.Manager.StandardPackages.EnsureInstalled(null, bridgeSource);
                    CheckNat("Bruecken-Pakete: je Standard-Bridge ein Paket mit Prelude und C++-Quellen, als standard markiert",
                        built.Count == fire.Package.Manager.StandardPackages.Bridges.Count && ioPackage.Imports[0].Standard && ioPackage.Imports[0].Name == "io" && ioPackage.Imports[0].Prelude != null && ioPackage.Imports[0].Native?.Sources.Count > 0
                        && fire.Package.Manager.Fpk.ReadManifest(built.First(f => Path.GetFileName(f).StartsWith("fire-linq-"))).Dependencies.Contains("fire-reflection"));
                    CheckNat("Bruecken-Pakete: beim Start werden fehlende installiert (mit Abhaengigkeiten), danach nichts mehr",
                        first.Count > 0 && bridgeStore.Installed().Count == fire.Package.Manager.StandardPackages.Bridges.Count && second.Count == 0 && bridgeStore.Find("fire-time") != null && bridgeStore.Find("fire-reflection") != null);
                    var windowsPackage = bridgeStore.Find("fire-windows");
                    CheckNat("Bruecken-Pakete: fire-windows bringt die Vorlage Desktop mit; sie steht nach dem Installieren im Katalog",
                        windowsPackage != null && File.Exists(Path.Combine(windowsPackage.Directory, "templates", "Project", "Desktop", "template.json"))
                        && fire.Projects.TemplateCatalog.Load(builtinRoot: Path.Combine(pkgDir, "none"), userRoot: Path.Combine(pkgDir, "none"), store: bridgeStore).Find(fire.Projects.TemplateScope.Project, "Desktop") is { } desktopTemplate && desktopTemplate.Source.Contains("fire-windows"));
                    string builtIn = vmOutput("#import \"time\"\nprint(TimeSpan.FromSeconds(90).TotalSeconds)");
                    CheckNat("Bruecken-Pakete: #import \"time\" nimmt weiter die eingebaute Bridge (kein doppelter Import)", builtIn == "90\n", builtIn);
                }
                finally { fire.Package.Manager.PackageStore.Default = savedDefault; }
            }

            // the toolchain provider: without a compiler the host is asked (cancel / change / install); the answer decides
            {
                string? savedPath = Environment.GetEnvironmentVariable("PATH");
                var savedAsk = fire.Native.ToolchainProvider.Ask;
                var savedChange = fire.Native.ToolchainProvider.ChangeToolchain;
                try
                {
                    var wantedToolchain = fire.Native.ToolchainDef.BuiltIn["gcc"];
                    Environment.SetEnvironmentVariable("PATH", "");
                    bool noneFound = fire.Native.ToolchainDetector.Find(wantedToolchain) == null && fire.Native.ToolchainSetup.Detect() == null;
                    if (noneFound)
                    {
                        fire.Native.ToolchainRequest? seen = null;
                        fire.Native.ToolchainProvider.Ask = r => { seen = r; return fire.Native.ToolchainChoice.Cancel; };
                        var canceled = fire.Native.ToolchainProvider.Require(wantedToolchain, "A package needs it (test).");
                        bool cancelOk = canceled == null && seen != null && seen.Message.Contains("A package needs it (test).") && seen.Message.Contains("none was found");
                        fire.Native.ToolchainProvider.Ask = r => fire.Native.ToolchainChoice.Change;
                        fire.Native.ToolchainProvider.ChangeToolchain = () => { Environment.SetEnvironmentVariable("PATH", savedPath); return true; };
                        var changed = fire.Native.ToolchainProvider.Require(wantedToolchain, "test");
                        Environment.SetEnvironmentVariable("PATH", "");
                        fire.Native.ToolchainProvider.Ask = null;
                        var unasked = fire.Native.ToolchainProvider.Require(wantedToolchain, "test");
                        CheckNat("Toolchain: ohne Compiler wird der Host gefragt (abbrechen = nichts; aendern = es wird neu gesucht; ohne Host keine Frage)", cancelOk && changed != null && unasked == null);
                    }
                    else CheckNat("Toolchain: (uebersprungen - ein Compiler liegt ausserhalb des PATH)", true);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("PATH", savedPath);
                    fire.Native.ToolchainProvider.Ask = savedAsk;
                    fire.Native.ToolchainProvider.ChangeToolchain = savedChange;
                }
                if (fire.Native.ToolchainSetup.Detect() is { } detected)
                {
                    var test = fire.Compiler.NativeBuilder.TestToolchain(detected.Toolchain);
                    CheckNat("Toolchain: der Test uebersetzt und startet ein kleines Programm", test.Ok, test.Log);
                }
            }

            // SDL2 for a window: unpacking the MinGW package, finding it (SDL2_DIR), the compiler arguments, SDL2.dll next to the program
            {
                string sdlDir = Path.Combine(Path.GetTempPath(), "fire-sdl-test-" + Guid.NewGuid().ToString("N"));
                string? savedSdl = Environment.GetEnvironmentVariable("SDL2_DIR");
                try
                {
                    Directory.CreateDirectory(sdlDir);
                    string archive = Path.Combine(sdlDir, "sdl.tar.gz");
                    using (var gz = new System.IO.Compression.GZipStream(File.Create(archive), System.IO.Compression.CompressionLevel.Fastest))
                    using (var tar = new System.Formats.Tar.TarWriter(gz))
                    {
                        void Add(string name, string text) => tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text)) });
                        Add("SDL2-9.9.9/x86_64-w64-mingw32/include/SDL2/SDL.h", "// header");
                        Add("SDL2-9.9.9/x86_64-w64-mingw32/bin/SDL2.dll", "dll");
                        Add("SDL2-9.9.9/x86_64-w64-mingw32/lib/libSDL2.dll.a", "implib");
                        Add("SDL2-9.9.9/x86_64-w64-mingw32/lib/cmake/SDL2/x.cmake", "skipped");
                        Add("SDL2-9.9.9/i686-w64-mingw32/include/SDL2/SDL.h", "32 bit");
                        Add("SDL2-9.9.9/test/testsprite.c", "skipped");
                    }
                    string unpacked = Path.Combine(sdlDir, "SDL2");
                    fire.Native.SdlSetup.Unpack(archive, unpacked);
                    CheckNat("SDL2: das MinGW-Paket wird ausgepackt (include, SDL2.dll, Import-Bibliothek des 64-Bit-Teils; nichts sonst)",
                        File.Exists(Path.Combine(unpacked, "include", "SDL2", "SDL.h")) && File.Exists(Path.Combine(unpacked, "bin", "SDL2.dll")) && File.Exists(Path.Combine(unpacked, "lib", "libSDL2.dll.a"))
                        && !Directory.Exists(Path.Combine(unpacked, "lib", "cmake")) && !Directory.Exists(Path.Combine(unpacked, "test")) && File.ReadAllText(Path.Combine(unpacked, "include", "SDL2", "SDL.h")) == "// header");

                    Environment.SetEnvironmentVariable("SDL2_DIR", unpacked);
                    var location = fire.Native.SdlSetup.Locate();
                    CheckNat("SDL2: SDL2_DIR wird zuerst gefunden (Include- und Lib-Ordner, SDL2.dll)",
                        location != null && location.IncludeDirs.Contains(Path.Combine(unpacked, "include")) && location.IncludeDirs.Contains(Path.Combine(unpacked, "include", "SDL2"))
                        && location.LibDirs.Contains(Path.Combine(unpacked, "lib")) && location.RuntimeDll == Path.Combine(unpacked, "bin", "SDL2.dll"));

                    string cpp = Path.Combine(sdlDir, "p.cpp");
                    File.WriteAllText(cpp, "// fire-link: SDL2\nint main() { return 0; }\n");
                    CheckNat("SDL2: das Programm bittet mit `// fire-link: SDL2` darum", fire.Native.SdlSetup.IsRequiredBy(cpp) && !fire.Native.SdlSetup.IsRequiredBy(Path.Combine(sdlDir, "none.cpp")));
                    var (exeName, arguments) = fire.Compiler.NativeBuilder.CompilerCommand(fire.Native.ToolchainDef.BuiltIn["gcc"], fire.Runtime.TargetProfile.Host, cpp, sdlDir, Path.Combine(sdlDir, "p.exe"));
                    CheckNat("SDL2: der Compiler bekommt -I, -L und -lSDL2",
                        arguments.Contains($"-I\"{Path.Combine(unpacked, "include")}\"") && arguments.Contains($"-L\"{Path.Combine(unpacked, "lib")}\"") && arguments.Contains("-lSDL2"), arguments);

                    string program = Path.Combine(sdlDir, "out", "p.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(program)!);
                    bool copied = fire.Native.SdlSetup.CopyRuntimeNextTo(program, location);
                    CheckNat("SDL2: SDL2.dll wird neben ein Windows-Programm gelegt (nicht neben ein anderes)",
                        copied && File.Exists(Path.Combine(sdlDir, "out", "SDL2.dll")) && !fire.Native.SdlSetup.CopyRuntimeNextTo(Path.Combine(sdlDir, "out", "p"), location));

                    string missing = fire.Native.SdlSetup.HelpText;
                    CheckNat("SDL2: die Hilfe sagt, was zu tun ist (Linux, macOS, Windows, SDL2_DIR)", missing.Contains("libsdl2-dev") && missing.Contains("brew install sdl2") && missing.Contains("SDL2_DIR"));
                }
                finally
                {
                    Environment.SetEnvironmentVariable("SDL2_DIR", savedSdl);
                    try { Directory.Delete(sdlDir, true); } catch (IOException) { }
                }
            }
        }
        finally
        {
            fire.Package.Manager.PackageStore.Default = savedStore;
            try { Directory.Delete(pkgDir, true); } catch (IOException) { }
        }

        bool PkgThrows(Action action)
        {
            try { action(); return false; }
            catch (fire.Package.Manager.PackageException) { return true; }
        }
    }

    Console.WriteLine(natFailures == 0 ? "Alle Native-Backend-Pruefungen bestanden." : $"FEHLER: {natFailures} Native-Backend-Pruefung(en) fehlgeschlagen.");
}

static class PackerNativeProbe
{
    [System.Runtime.InteropServices.DllImport("libfiretestnative")] private static extern int Nonexistent();
    public static int Call() => Nonexistent();
}

/// <summary>Font without bitmap rows (only IsPixelSet) - forces the general drawing path of Renderer.</summary>
sealed class PixelOnlyFont : fire.Terminal.IGlyphFont
{
    private readonly fire.Terminal.IntegratedGlyphFont _inner;
    public PixelOnlyFont(fire.Terminal.IntegratedGlyphFont inner) { _inner = inner; }
    public int GlyphWidth => _inner.GlyphWidth;
    public int GlyphHeight => _inner.GlyphHeight;
    public bool IsPixelSet(char c, int px, int py) => _inner.IsPixelSet(c, px, py);
}

/// <summary>Renderer dummy for the UI tests: delivers the stored events at the next PumpEvents, draws nothing.</summary>
sealed class FakeRenderer : fire.Terminal.IFramebufferRenderer
{
    private readonly List<fire.Terminal.Event.IEvent> _pending = new();
    public bool Closed { get; set; }

    public bool VSync { get; set; } = true;
    public bool TouchMouse { get; set; } = true;
    public void Initialize(string title, int initialWidth, int initialHeight, int internalHandle) { }
    public void Present(fire.Terminal.Framebuffer framebuffer) { }
    public void Dispose() { }

    public fire.Terminal.WindowPumpResult PumpEvents()
    {
        var events = _pending.ToArray();
        _pending.Clear();
        return new fire.Terminal.WindowPumpResult { StillOpen = !Closed, Events = events };
    }

    /// <summary>Stores an event: args[0] type, then depending on the type: mouse (button, x, y) or motion (x, y), key (keycode, modifier), text (text).</summary>
    public void Push(int type, IReadOnlyList<Value> args)
    {
        var kind = (fire.Terminal.Event.EventType)type;
        switch (kind)
        {
            case fire.Terminal.Event.EventType.MouseDown:
            case fire.Terminal.Event.EventType.MouseUp:
                _pending.Add(new fire.Terminal.Event.ClickEvent { Type = kind, Button = (int)args[1].AsInt(), X = (float)args[2].AsFloat(), Y = (float)args[3].AsFloat() });
                break;
            case fire.Terminal.Event.EventType.MouseMove:
                _pending.Add(new fire.Terminal.Event.MotionEvent { Type = kind, X = (float)args[1].AsFloat(), Y = (float)args[2].AsFloat() });
                break;
            case fire.Terminal.Event.EventType.KeyDown:
            case fire.Terminal.Event.EventType.KeyUp:
                _pending.Add(new fire.Terminal.Event.KeyEvent { Type = kind, KeyCode = (int)args[1].AsInt(), Modifier = (int)args[2].AsInt() });
                break;
            case fire.Terminal.Event.EventType.TextInput:
                _pending.Add(new fire.Terminal.Event.TextEvent { Type = kind, Text = args[1].AsString() });
                break;
            case fire.Terminal.Event.EventType.Resize:
                _pending.Add(new fire.Terminal.Event.ResizeEvent { Type = kind, Width = (int)args[1].AsInt(), Height = (int)args[2].AsInt() });
                break;
            case fire.Terminal.Event.EventType.TouchDown:
            case fire.Terminal.Event.EventType.TouchMove:
            case fire.Terminal.Event.EventType.TouchUp:
                _pending.Add(new fire.Terminal.Event.TouchEvent { Type = kind, Finger = args[1].AsInt(), X = (float)args[2].AsFloat(), Y = (float)args[3].AsFloat(), Pressure = 1f });
                break;
            case fire.Terminal.Event.EventType.JoystickAxis:
                _pending.Add(new fire.Terminal.Event.JoystickEvent { Type = kind, Joystick = (int)args[1].AsInt(), Index = (int)args[2].AsInt(), Value = (float)args[3].AsFloat() });
                break;
            case fire.Terminal.Event.EventType.JoystickHat:
                _pending.Add(new fire.Terminal.Event.JoystickEvent { Type = kind, Joystick = (int)args[1].AsInt(), Index = (int)args[2].AsInt(), Value = (float)args[3].AsInt() });
                break;
            case fire.Terminal.Event.EventType.JoystickButtonDown:
            case fire.Terminal.Event.EventType.JoystickButtonUp:
                _pending.Add(new fire.Terminal.Event.JoystickEvent { Type = kind, Joystick = (int)args[1].AsInt(), Index = (int)args[2].AsInt() });
                break;
            case fire.Terminal.Event.EventType.JoystickAdded:
            case fire.Terminal.Event.EventType.JoystickRemoved:
                _pending.Add(new fire.Terminal.Event.JoystickEvent { Type = kind, Joystick = (int)args[1].AsInt() });
                break;
        }
    }
}

sealed class volatile_bool
{
    private volatile bool _value;
    public bool Value { get => _value; set => _value = value; }
}

/// <summary>The drawing functions with a palette index as colour (a SolidBrush or Pen on the framebuffer) - short form for the checks of the raster geometry.</summary>
static class Shp
{
    private static fire.Terminal.Surface S(fire.Terminal.Framebuffer fb) => new(fb, true);
    private static fire.Terminal.Brush B(int i) => new fire.Terminal.SolidBrush(fire.Terminal.Paint.FromIndex((byte)i));
    private static fire.Terminal.Pen P(int i) => new fire.Terminal.Pen(fire.Terminal.Paint.FromIndex((byte)i));

    public static void FillCircle(fire.Terminal.Framebuffer fb, int cx, int cy, int r, int i) => B(i).FillCircle(S(fb), cx, cy, r);
    public static void Circle(fire.Terminal.Framebuffer fb, int cx, int cy, int r, int i) => P(i).DrawCircle(S(fb), cx, cy, r);
    public static void FillEllipse(fire.Terminal.Framebuffer fb, int cx, int cy, int rx, int ry, int i) => B(i).FillEllipse(S(fb), cx, cy, rx, ry);
    public static void Ellipse(fire.Terminal.Framebuffer fb, int cx, int cy, int rx, int ry, int i) => P(i).DrawEllipse(S(fb), cx, cy, rx, ry);
    public static void FillTriangle(fire.Terminal.Framebuffer fb, int x0, int y0, int x1, int y1, int x2, int y2, int i) => B(i).FillTriangle(S(fb), x0, y0, x1, y1, x2, y2);
    public static void Triangle(fire.Terminal.Framebuffer fb, int x0, int y0, int x1, int y1, int x2, int y2, int i) => P(i).DrawTriangle(S(fb), x0, y0, x1, y1, x2, y2);
    public static void FillPolygon(fire.Terminal.Framebuffer fb, int[] points, int i) => B(i).FillPolygon(S(fb), points);
    public static void Polygon(fire.Terminal.Framebuffer fb, int[] points, int i, bool closed = true) => P(i).DrawPolygon(S(fb), points, closed);
    public static void Rect(fire.Terminal.Framebuffer fb, int x, int y, int w, int h, int i) => P(i).DrawRect(S(fb), x, y, w, h);
    public static void Line(fire.Terminal.Framebuffer fb, int x0, int y0, int x1, int y1, int i) => P(i).DrawLine(S(fb), x0, y0, x1, y1);
    public static void FloodFill(fire.Terminal.Framebuffer fb, int x, int y, int i) => B(i).FloodFill(S(fb), x, y);
    public static void FloodFillBorder(fire.Terminal.Framebuffer fb, int x, int y, int i, int border) => B(i).FloodFillBorder(S(fb), x, y, fire.Terminal.Paint.FromIndex((byte)border));
}

/// <summary>A minimal own render target (two arrays, no framebuffer) - shows that the renderer only needs the IRenderTarget.</summary>
sealed class ArrayTarget : fire.Terminal.IRenderTarget
{
    public ArrayTarget(int width, int height) { Width = width; Height = height; Pixels = new uint[width * height]; }
    public int Width { get; }
    public int Height { get; }
    public fire.Terminal.ColorMode Mode => fire.Terminal.ColorMode.Rgba;
    public fire.Terminal.Palette Palette { get; } = new();
    public uint[] Pixels { get; }
    public byte[]? Indices => null;
    public int TransparentIndex => -1;
    public void MarkDirty() { }
}
