using System.Collections.Generic;
using fire.Ast;
using fire.Bytecode;
using fire.Compiler;
using fire.Lexing;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;

// Kleiner manueller Smoke-Test für Lexer + Parser + Unit-System, bis der
// Evaluator existiert. Bei dir lokal: `dotnet run` im src/fire-Ordner.

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
    // Präfix-'!' negiert einen geklammerten Ausdruck
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

// Token.Length = Laenge im Quelltext (Editor-Hervorhebung): Anfuehrungszeichen, Escapes, `$"..."` und Char-Literale zaehlen mit.
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
    objA.TakeTo(objB, log); // a gehört global, b gehört a -> a->b wäre ein Zyklus
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
class Resource {
    string label

    construct(string label) {
        this.label = label
    }

    destruct() {
        print(this.label)
    }
}

{
    var r = new Resource("cleanup-ran")
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
    // Kein throw mehr - Run() kehrt normal zurück, VM.UnhandledException
    // trägt die nicht abgefangene Exception (siehe VM.UnhandledException-Doku).
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
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes); // bewusst OHNE ExternRegistry
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
    // List legt intern physisch 8 Slots an (siehe Prelude, verdoppelt sich
    // erst bei Bedarf) - erst ein Index jenseits DIESER physischen Kapazität
    // (nicht nur jenseits von .Add()-Count) triggert den Bounds-Check.
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
    // Bewusst OHNE ExternRegistry - beide Funktionen sollen rein über die
    // '#extern'-Direktiven dynamisch verlinkt werden (VM.ResolveDynamicExtern).
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

// Liefert das TestData-Verzeichnis relativ zu DIESER Quelldatei, unabhängig
// vom aktuellen Arbeitsverzeichnis beim Ausführen (dotnet run kann von
// verschiedenen Orten aus gestartet werden).
static string GetTestDataDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
    Path.Combine(Path.GetDirectoryName(here)!, "TestData");

// Test-Hilfsfunktion: jagt jede der `sources` durch den echten Preprocessor
// (erkennt/entfernt dabei #include/#using wie ein normaler Aufrufer das
// tun würde, siehe RuntimeSession.Build für dasselbe Muster in "echt") und
// liefert die daraus entstehenden ProcessedSource-Objekte, die Parser.
// ParseMultiple jetzt direkt erwartet. `basePath` nur für #include-
// Pfadauflösung relevant, für die meisten Tests ohne Bedeutung.
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
// + taking-Kopie + sync - alles über die C#-API direkt getestet, noch ohne
// Parser-/Sprachsyntax für fire/taking/sync/process/leave/terminate.
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
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes) { DestroyGlobalsAtEnd = false }; // die Objekte werden danach noch von Hand an Threads gegeben
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
    // Absichtlich OHNE Reparenting gesetzt - simuliert eine baumfremde
    // Referenz, wie sie normale Feldzuweisung (die per Ownership-Politik
    // reparentet) so eigentlich nicht erzeugen würde.
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

// Das Fire-Thread-Skript kennt 'Player' nicht (eigenes, separat kompiliertes
// Programm) - das ist unproblematisch, da Feldzugriff ('player.health')
// immer ein reiner Laufzeit-Namens-Lookup ist, keine Compile-Zeit-Prüfung
// gegen eine Klassendefinition braucht. '__fireArg'/'__sync' sind gewöhnliche
// native Funktionen (siehe FireRuntime.FireVm-Doku) - die Brücke zur
// 'taking'-Kopie bzw. zu SyncEngine, ganz ohne Parser-Änderung.
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
        // Dieselbe Abbildung, die später auch die echte Sprachsyntax nutzen
        // wird (siehe docs/THREADING_DESIGN.md 4.1): true/false/undefined.
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
    // typeName: null -> entspricht 'catch threads()' (fängt alles, wie ein
    // bloßes catch(e)) - vermeidet, dass der Main-Thread 'MyError' als
    // Klasse kennen müsste (die beiden Programme sind separat kompiliert).
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
    System.Threading.Thread.Sleep(200); // nur für saubere Test-Ausgabe-Reihenfolge, keine Sprachanforderung
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

// Hinweis: VERSCHACHTELTE generische Typ-ARGUMENTE an der Verwendungsstelle
// (z.B. 'new Box<Box<int>>(...)') werden von diesem Parser unabhängig von
// dieser Änderung noch NICHT unterstützt (ParseOptionalTypeParamNames liest
// jedes Typ-Argument nur als einfachen Namen, ohne selbst wieder rekursiv
// eigene '<...>' zuzulassen) - das ist eine bereits vorher bestehende,
// von den neuen Operatoren unabhängige Einschränkung. Dieser Test prüft
// deshalb bewusst nur EINSTUFIGE Generics (die einzige unterstützte Form)
// direkt NEBEN einem '>>'-Shift, um zu belegen, dass beide sich nicht in
// die Quere kommen.
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
        // Demonstriert, dass die Host-Implementierung die Einheit eines
        // Timeout-Arguments direkt auslesen kann (Value.Unit ist bereits
        // oeffentlich) - unabhaengig davon, ob der Aufruf letztlich
        // erfolgreich ist oder nicht.
        var timeoutVal = args[0];
        Console.WriteLine($"  (native Seite: Timeout-Argument hat Einheit '{timeoutVal.Unit}', Rohwert {timeoutVal.AsInt()})");

        attemptCount++;
        if (attemptCount == 1)
        {
            result = default;
            return false; // simulierter Timeout beim ersten Versuch
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

    // Simuliert den nativen Host, der spaeter (z.B. von einem anderen
    // Thread, hier der Einfachheit halber synchron) ein Event feuert -
    // der Snapshot wird HIER genommen, waehrend das Hauptprogramm gerade
    // NICHT laeuft (siehe VM.SnapshotGlobals-Doku).
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
    var registry = new DirectiveRegistry(); // komplett leer, NICHT CreateDefault()
    var receivedArgs = new List<Value>();
    registry.Register("mydirective", 4, (ctx, args, line) =>
    {
        receivedArgs.AddRange(args);
        return null; // erzeugt keinen Ersatz-Text
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
    var registry = new DirectiveRegistry(); // 'extern' ist hier NICHT registriert
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

    // Altes Verhalten (zwei UNABHAENGIGE Process()-Aufrufe, je eigene Menge) -
    // die Markierung landet zweimal in der Summe.
    string outA = Preprocessor.Process(rootA, tmpDir).Source;
    string outB = Preprocessor.Process(rootB, tmpDir).Source;
    int countIndependent = CountOccurrences(outA + outB, "GEMEINSAM_INKLUDIERTE_MARKIERUNG");
    Console.WriteLine($"Getrennte Process()-Aufrufe: Markierung {countIndependent}x (erwartet 2x, je einmal pro Aufruf).");

    // Neues Verhalten: EINE geteilte 'alreadyIncluded'-Menge ueber BEIDE
    // Aufrufe hinweg (wie RuntimeSession.Build es jetzt fuer Prelude +
    // Nutzer-Code macht) - die Markierung landet nur noch EINMAL insgesamt.
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
        // Nur im Performance-Modus erwartet - der ungültige arr[10]-Zugriff
        // schlägt dort als rohe, ungefangene .NET-Exception durch, statt
        // (wie in Debug/Release) sauber per catch(e : IndexOutOfBoundsException)
        // im Skript selbst behandelt zu werden.
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
            // protected - von einer abgeleiteten Klasse aus erlaubt
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

// Genau das Szenario, das mit einer einzigen programmweiten Usings-Liste
// nicht funktionieren wuerde: HIER hat sowohl fileA (nicht die letzte
// Quelle!) als auch mainFile eigenen Top-Level-Code, der jeweils eine
// ANDERE, gleichnamige Klasse unqualifiziert referenziert. Jede TypeRef
// traegt ihren eigenen Namespace-Kontext direkt an sich selbst (siehe
// Ast.TypeRef.Namespaces), deshalb funktioniert das jetzt unabhaengig
// davon, in welcher Datei/an welcher Position eine Referenz steht.
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

// Absichtlich derselbe einfache Klassenname 'Helper' in ZWEI verschiedenen
// Namespaces - mit programmweiten (statt lokalen) Usings wäre das
// zweideutig: 'new Helper()' in fileB müsste eigentlich LibB.Helper
// treffen, würde bei global geteilten Usings aber leicht (je nach
// Reihenfolge) fälschlich LibA.Helper treffen.
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
    // '|' im Quelltext = Cursor-Position. `expected`: diese Namen MÜSSEN vorgeschlagen
    // werden, `forbidden`: diese DÜRFEN NICHT (Komma-getrennt).
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
    // 'var x : einheit' legt nur eine Einheit fest, der Typ kommt aus dem Initialisierer.
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
    // ---- Namespaces und ihre Mitglieder ----
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

    // Führt `script` mit Prelude + IO-Prelude aus und liefert alle `print`-Zeilen.
    List<string> RunIo(string script, fire.IO.Bridge.IoPolicy? policy = null, fire.IO.Bridge.IoStdio? stdio = null)
    {
        var lines = new List<string>();
        var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new[] { fire.Standard.Prelude.Source, fire.IO.Bridge.IoBridge.PreludeSource, script }
            .Select(s => Preprocessor.Process(s, Directory.GetCurrentDirectory(), alreadyIncluded)).ToList();
        var program = Parser.ParseMultiple(sources);
        var natives = new NativeRegistry();
        natives.Register("print", args => { lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        fire.IO.Bridge.IoBridge.RegisterAll(natives, policy, stdio);
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

    // ---- Schritt 2: Datei- und Verzeichnis-API ----
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

    // Der Fehlertext muss `fragment` enthalten (Parser-/Resolver-/Laufzeitfehler).
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

    // Eine Erweiterung von string gilt nur für string - ein int kennt `Foo` nicht (kein Skript-, sondern ein VM-Fehler).
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

    // Die Methode wird über ihre ID gewählt, nicht über den Namen: die native Funktion ist direkt aufrufbar.
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

    // Jedes Skript läuft in ALLEN drei Modi mit demselben erwarteten Ergebnis (Performance lässt die
    // Zugriffs-/Grenzprüfungen weg - die Beispiele hier lösen sie deshalb nicht aus, außer wo `modes` es sagt).
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

    // --- Value: Gleichheit und Arithmetik (kompaktes Layout, Schnellpfade)
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

    // --- Stack und Scope-Slots wachsen
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

    // --- Inline-Caches: eine Aufrufstelle, mehrere Klassen
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
            inner.Take()
            var r2 = new Res("r2")
            r2.Take()
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
            ? new[] { fire.Standard.Prelude.Source, fire.IO.Bridge.IoBridge.PreludeSource, script }
            : new[] { fire.Standard.Prelude.Source, script };
        var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = Parser.ParseMultiple(sources.Select(s => Preprocessor.Process(s, Directory.GetCurrentDirectory(), alreadyIncluded)).ToList());
        var natives = new NativeRegistry();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        IDisposable? io = withIo ? fire.IO.Bridge.IoBridge.RegisterAll(natives) : null;
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

    // ---- Kopien als Parameter: Scope der aufgerufenen Funktion
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

    // ---- Kopie einem Objekt zugewiesen: das Objekt wird der Owner (wie TakeTo)
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

    // ---- leave: sofort, und alles wird zerstoert
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

    // ---- leave/terminate: der aufrufende Thread haelt sofort an; beide enden wie das normale Programmende
    //      (Hauptprogramm wartet auf alle Fire-Threads, erst danach werden die globalen Destruktoren ausgefuehrt)
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
        // Ein offener FileStream: sein Destruktor schliesst ihn beim leave (der Inhalt ist danach vollstaendig auf der Platte).
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
        // Das Sicherheitsnetz des Hosts (IoBridge.RegisterAll(...).Dispose()) schliesst, was am Ende noch offen ist -
        // hier ein Stream im globalen Scope, der beim normalen Programmende nicht zerstoert wird.
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

    // ---- normales Programmende raeumt den globalen Scope ab
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
        // Ein offener FileStream im globalen Scope wird beim normalen Ende vom Destruktor geschlossen (ohne Host-Sicherheitsnetz).
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

    // ---- Signale von anderen Threads werden an den sicheren Punkten (Schleifen, Aufrufe) bemerkt
    foreach (var mode in allModes)
    {
        foreach (var kind in new[] { "terminate", "leave" })
        {
            VM.ResetTerminateForTests();
            var lines = new List<string>();
            var natives = new NativeRegistry();
            natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
            natives.RegisterBaseTypeNatives();
            // Eine Schleife mit Aufruf und eine ohne - beide muessen unterbrechbar sein.
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
            // `leave` und `terminate` enden beide wie das normale Programmende: der globale Scope wird zerstoert (destruct laeuft).
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
// Packer: eigenstaendige Datei (Bundle + Payload), Bridges nur bei Bedarf, Lader statt Costura
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

    // 1) Pack-Plan: je Import nur die noetigen DLLs, nichts Unaufgeloestes.
    PackagePlan Plan(params string[] imports) => PackagePlan.Create(imports, baseDir);
    var planPrint = Plan(NativeImports.Print);
    PackCheck(planPrint.Assemblies.ContainsKey("fire") && planPrint.Assemblies.ContainsKey("MemoryPack.Core"), "Plan: Kern (fire, MemoryPack) ist immer dabei");
    PackCheck(!planPrint.Assemblies.Keys.Any(n => n.StartsWith("fire.Terminal") || n.StartsWith("fire.Device") || n.StartsWith("fire.IO") || n == "SDL3-CS" || n == "System.IO.Ports") && planPrint.Natives.Count == 0,
        "Plan: ohne Import keine Bridge, keine nativen Bibliotheken");
    var planIo = Plan(NativeImports.Print, NativeImports.IO);
    PackCheck(planIo.Assemblies.ContainsKey("fire.IO.Bridge") && !planIo.Assemblies.ContainsKey("fire.Terminal.Bridge") && !planIo.Assemblies.ContainsKey("fire.Device.Bridge"),
        "Plan: io bindet nur die IO-Bridge ein");
    var planGfx = Plan(NativeImports.Print, NativeImports.Graphics);
    PackCheck(new[] { "fire.Terminal.Bridge", "fire.Terminal" }.All(planGfx.Assemblies.ContainsKey) && !planGfx.Assemblies.ContainsKey("fire.IO.Bridge")
        && !planGfx.Assemblies.Keys.Any(n => n is "fire.Terminal.Windows" or "fire.Terminal.Sdl" or "fire.Windows.Bridge" or "SDL3-CS") && planGfx.Natives.Count == 0,
        "Plan: graphics bindet Terminal-Bridge und Terminal ein - ohne Fenster, SDL und native Bibliotheken");
    var planWin = Plan(NativeImports.Print, NativeImports.Graphics, NativeImports.Windows);
    PackCheck(new[] { "fire.Terminal.Bridge", "fire.Windows.Bridge", "fire.Terminal", "fire.Terminal.Windows", "fire.Terminal.Sdl", "SDL3-CS" }.All(planWin.Assemblies.ContainsKey) && planWin.Unresolved.Count == 0,
        "Plan: windows bindet Fenster-Bridge samt Terminal/Windows/SDL ein (Abhaengigkeiten aus den Metadaten)");
    var planDev = Plan(NativeImports.Print, NativeImports.Devices);
    var planUi = Plan(NativeImports.Print, NativeImports.Graphics, NativeImports.Windows, NativeImports.Ui);
    PackCheck(planUi.Assemblies.Keys.SequenceEqual(planWin.Assemblies.Keys) && planUi.Unresolved.Count == 0, "Plan: ui bringt keine eigene DLL mit (reiner fire-Quelltext, graphics und windows kommen ueber den Import)");
    PackCheck(new[] { "fire.Device.Bridge", "fire.Device.Manager", "System.IO.Ports" }.All(planDev.Assemblies.ContainsKey) && !planDev.Assemblies.ContainsKey("SDL3-CS"),
        "Plan: devices bindet Device-Bridge, Manager und System.IO.Ports ein");
    PackCheck(planGfx.Unresolved.Count == 0 && planDev.Unresolved.Count == 0 && planIo.Unresolved.Count == 0 && planPrint.Unresolved.Count == 0,
        "Plan: alle Verweise aufloesbar (Datei neben dem Compiler oder Teil des Frameworks)");
    PackCheck(planWin.Natives.Count == 0 || planWin.Natives.ContainsKey("SDL3.dll") || planWin.Natives.ContainsKey("libSDL3.so.0") || planWin.Natives.ContainsKey("libSDL3.dylib"),
        "Plan: windows bringt SDL3 mit, wo es die Plattform gibt");
    // `windows` ist von `graphics` getrennt: das Fenster gibt es nur mit dem eigenen Import, der `graphics` mitbringt; `graphics` allein kennt kein Window
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
        PackCheck(withUi == "graphics,print,ui,windows", "Import: ui bringt graphics und windows mit (" + withUi + ")");
        string gfxOnly = LinkResult("#import \"graphics\"\nvar fb = new Framebuffer(8, 8)");
        PackCheck(gfxOnly == "graphics,print", "Import: graphics allein ohne Fenster (" + gfxOnly + ")");
    }
    bool unknownImportRejected = false;
    try { Plan("gibtsnicht"); } catch (InvalidOperationException) { unknownImportRejected = true; }
    PackCheck(unknownImportRejected, "Plan: unbekannter Import wird abgelehnt statt still ignoriert");

    // 2) Payload-Format: Rundlauf, Kompression, Integritaet, keine Marker-Suche.
    var tmpDir = Path.Combine(Path.GetTempPath(), "fire-packtest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try
    {
        var plFile = Path.Combine(tmpDir, "pl.bin");
        var rnd = new Random(42);
        var incompressible = new byte[5000]; rnd.NextBytes(incompressible);
        var compressible = Enumerable.Repeat((byte)7, 20000).ToArray();
        // Die alten Marker-Bytes (DA 1D) mitten im Inhalt duerfen nichts stoeren.
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

            // Ein geflipptes Byte im gespeicherten Eintrag muss auffallen.
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

        // 3) Lader: native Bibliothek wird aus dem Payload entpackt und geladen (Linux: die .so der Ports-Bibliothek
        //    unter fremdem Namen, damit sie nicht ueber die normale Suche gefunden wird).
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

        // 4) Ende-zu-Ende: eigenstaendige Datei packen, AUSSERHALB des Compiler-Ordners starten.
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
            // Ohne Payload (nackte Runtime) gibt es eine klare Meldung statt eines Absturzes.
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
// Befehlszeile des Compilers (run / build)
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

    // Ende-zu-Ende ueber den Runner (ohne Prozess): build erzeugt die Datei, run liefert Exitcodes.
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

    // Dieselbe Schrift, aber OHNE Bitmap-Zeilen: TerminalCanvas muss dann den allgemeinen Weg (IsPixelSet je Pixel) nehmen.
    var slowFont = new PixelOnlyFont(new fire.Terminal.IntegratedGlyphFont());
    var rng = new Random(7);
    foreach (bool small in new[] { false, true })
    {
        var fastFont = new fire.Terminal.IntegratedGlyphFont(small);
        var slow = new PixelOnlyFont(fastFont);
        foreach (bool opaque in new[] { true, false })
        {
            var fbFast = new fire.Terminal.Framebuffer(203, 97); // krumme Groesse: Raster endet nicht am Rand, Texte ragen hinaus
            var fbSlow = new fire.Terminal.Framebuffer(203, 97);
            var fast = new fire.Terminal.TerminalCanvas(fbFast, fastFont);
            var slowCanvas = new fire.Terminal.TerminalCanvas(fbSlow, slow);
            foreach (var cv in new[] { fast, slowCanvas })
            {
                cv.Foreground = new fire.Terminal.PixelColor(200, 100, 50);
                cv.Background = opaque ? new fire.Terminal.PixelColor(10, 20, 30) : null;
                cv.Target.Clear(new fire.Terminal.PixelColor(1, 2, 3));
            }
            // Zeichen des ganzen Bereichs (auch > 255), an zufaelligen Positionen inkl. teilweise ausserhalb
            for (int i = 0; i < 400; i++)
            {
                char ch = (char)rng.Next(0, 400);
                int x = rng.Next(-12, 215), y = rng.Next(-16, 110);
                fast.DrawGlyph(x, y, ch, fast.Foreground, fast.Background);
                slowCanvas.DrawGlyph(x, y, ch, slowCanvas.Foreground, slowCanvas.Background);
            }
            // Print mit Umbruch und Scrollen
            string text = string.Join("\n", Enumerable.Range(0, 40).Select(n => new string((char)('A' + n % 26), 10 + n % 40)));
            fast.Locate(0, 0); fast.Print(text);
            slowCanvas.Locate(0, 0); slowCanvas.Print(text);
            FontCheck(fbFast.Pixels.SequenceEqual(fbSlow.Pixels),
                $"{(small ? "8x8" : "8x14")} {(opaque ? "opak" : "transparent")}: Glyphen an beliebigen (auch ueberstehenden) Positionen und Print mit Umbruch/Scrollen identisch");
        }
    }

    {
        var fb = new fire.Terminal.Framebuffer(100, 40);
        var cv = new fire.Terminal.TerminalCanvas(fb, new fire.Terminal.IntegratedGlyphFont());
        cv.DrawText(3, 5, "Hallo", fire.Terminal.PixelColor.White);
        FontCheck(cv.MeasureText("Hallo") == 5 * cv.CellWidth, "MeasureText: Zeichenzahl mal Zellbreite");
        var fb2 = new fire.Terminal.Framebuffer(100, 40);
        var cv2 = new fire.Terminal.TerminalCanvas(fb2, new fire.Terminal.IntegratedGlyphFont());
        for (int i = 0; i < 5; i++) cv2.DrawGlyph(3 + i * cv2.CellWidth, 5, "Hallo"[i], fire.Terminal.PixelColor.White, null);
        FontCheck(fb.Pixels.SequenceEqual(fb2.Pixels), "DrawText == DrawGlyph je Zeichen");
        cv.Locate(0, 0); cv.Print("\u20AC\u4E2D"); // Zeichen ausserhalb der Tabelle: kein Absturz
        FontCheck(true, "Zeichen > 255 werfen nicht");
    }

    Console.WriteLine(fontFailures == 0 ? "Alle Font-Pruefungen bestanden." : $"FEHLER: {fontFailures} Font-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Grafik: Farbmodi (RGBA / Palette), Farbangaben (Index oder direkter Wert), Zeichenfunktionen, Kopieren
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
    fire.Terminal.Brush Idx(fire.Terminal.Framebuffer fb, int i) => fb.ResolveBrush(fire.Terminal.Paint.FromIndex((byte)i));

    // Menge der gesetzten Pixel (Palette: Index != 0, RGBA: Wert != 0) als "x,y"-Menge
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
        fb.Plot(1, 1, new fire.Terminal.Brush(0, 9));
        fb.Resolve();
        GfxCheck(fb.Pixels[1 * 4 + 1] == fb.Palette.GetPacked(9) && fb.Pixels[0] == fb.Palette.GetPacked(0), "Resolve: Pixels = Palette[Index]");
        fb.Palette.SetColor(9, unchecked((int)new fire.Terminal.PixelColor(1, 2, 3).Packed));
        fb.Resolve();
        GfxCheck(fb.Pixels[1 * 4 + 1] == new fire.Terminal.PixelColor(1, 2, 3).Packed, "Palette aendern faerbt alle Pixel mit diesem Index um (Resolve rechnet neu)");
        fb.Indices![0] = 200; fb.MarkDirty(); fb.Resolve();
        GfxCheck(fb.Pixels[0] == fb.Palette.GetPacked(200), "MarkDirty nach direktem Schreiben der Indizes");
        GfxCheck(fb.GetPixel(0, 0).Packed == fb.Palette.GetPacked(200) && fb.GetIndex(0, 0) == 200, "GetPixel/GetIndex im Palette-Modus");
        fb.Plot(-1, 0, new fire.Terminal.Brush(0, 5)); fb.Plot(4, 0, new fire.Terminal.Brush(0, 5)); fb.Plot(0, 3, new fire.Terminal.Brush(0, 5));
        GfxCheck(fb.GetIndex(0, 0) == 200 && fb.GetRaw(-1, 0) == 0, "Plot ausserhalb: still beschnitten");

        var rgba = new fire.Terminal.Framebuffer(2, 2);
        GfxCheck(!rgba.IsIndexed && rgba.Indices == null && rgba.Mode == RGBA, "RGBA-Framebuffer hat keine Indizes");
        rgba.Resolve(); // No-op
        try { new fire.Terminal.Framebuffer(2, 2, (fire.Terminal.ColorMode)7); GfxCheck(false, "unbekannter Farbmodus wird abgelehnt"); }
        catch (ArgumentOutOfRangeException) { GfxCheck(true, "unbekannter Farbmodus wird abgelehnt"); }
    }

    // ---- Farbangaben: Index ODER direkter Wert, aufgeloest je Framebuffer ----
    {
        var p = fire.Terminal.Paint.FromArgument(7);
        GfxCheck(p.IsIndex && p.Index == 7 && fire.Terminal.Paint.FromArgument(255).IsIndex && !fire.Terminal.Paint.FromArgument(256).IsIndex, "Paint.FromArgument: 0-255 = Index, sonst direkter Wert");
        GfxCheck(!fire.Terminal.Paint.FromArgument(unchecked((int)0xFF0000FFu)).IsIndex && fire.Terminal.Paint.FromArgument(unchecked((int)0xFF0000FFu)).Rgba == 0xFF0000FFu, "ein deckender Wert mit R=255 ist ein direkter Wert, kein Index");
        GfxCheck(fire.Terminal.Paint.FromArgument((1L << 32) + 5).Index == 5, "nur die unteren 32 Bit zaehlen");

        var rgba = new fire.Terminal.Framebuffer(2, 2);
        var idx = new fire.Terminal.Framebuffer(2, 2, IDX);
        var red = fire.Terminal.PixelColor.FromRgb(255, 0, 0);
        var viaIndex = rgba.ResolveBrush(fire.Terminal.Paint.FromIndex(4));
        GfxCheck(viaIndex.Rgba == rgba.Palette.GetPacked(4), "RGBA-Framebuffer: ein Index wird ueber die Palette zur Farbe");
        GfxCheck(rgba.ResolveBrush(fire.Terminal.Paint.FromRgba(red)).Rgba == red.Packed, "RGBA-Framebuffer: ein direkter Wert bleibt");
        GfxCheck(idx.ResolveBrush(fire.Terminal.Paint.FromIndex(4)).Index == 4, "Palette-Framebuffer: ein Index bleibt");
        byte nearest = idx.ResolveBrush(fire.Terminal.Paint.FromRgba(fire.Terminal.PixelColor.FromRgb(250, 3, 3))).Index;
        GfxCheck(idx.Palette.GetColor(nearest).R >= 200 && idx.Palette.GetColor(nearest).G <= 50 && idx.Palette.GetColor(nearest).B <= 50, "Palette-Framebuffer: ein direkter Wert wird der naechste Palette-Eintrag (rot -> rotlich)");
        GfxCheck(idx.Palette.FindNearest(fire.Terminal.PixelColor.FromRgb(0, 0, 0)) == 0 && idx.Palette.FindNearest(idx.Palette.GetColor(200)) <= 200 && idx.Palette.GetPacked(idx.Palette.FindNearest(idx.Palette.GetColor(200))) == idx.Palette.GetPacked(200), "FindNearest findet eine exakt vorhandene Farbe");
    }

    // ---- Text und Rechtecke: Palette-Framebuffer == RGBA-Framebuffer mit denselben Palettenfarben ----
    {
        var font = new fire.Terminal.IntegratedGlyphFont();
        var fbRgba = new fire.Terminal.Framebuffer(203, 97);
        var fbIdx = new fire.Terminal.Framebuffer(203, 97, IDX);
        var cRgba = new fire.Terminal.TerminalCanvas(fbRgba, font);
        var cIdx = new fire.Terminal.TerminalCanvas(fbIdx, font);
        foreach (var cv in new[] { cRgba, cIdx })
        {
            cv.SetColor(fire.Terminal.Paint.FromIndex(14), fire.Terminal.Paint.FromIndex(1));
            cv.Clear();
            cv.FillRect(5, 5, 40, 20, (byte)12);
            cv.DrawRect(2, 2, 60, 30, (byte)10);
            cv.DrawLine(0, 0, 202, 96, (byte)9);
            cv.DrawText(7, 40, "Hallo Welt", fire.Terminal.Paint.FromIndex(15), null);
            cv.DrawText(100, 80, "ragt hinaus", fire.Terminal.Paint.FromIndex(13), fire.Terminal.Paint.FromIndex(4));
            cv.Locate(0, 0);
            cv.Print(string.Join("\n", Enumerable.Range(0, 12).Select(n => new string((char)('A' + n % 26), 8 + n))));
            cv.SetPixel(1, 1, (byte)200);
            cv.FillRect(170, 2, 20, 10, (byte)12);
        }
        fbIdx.Resolve();
        GfxCheck(fbRgba.Pixels.SequenceEqual(fbIdx.Pixels), "Clear/FillRect/DrawRect/DrawLine/DrawText/Print/Scrollen: Palette-Framebuffer zeigt dieselben Pixel wie der RGBA-Framebuffer");

        // Bei einem Palette-Wechsel aendert sich der Palette-Framebuffer, der RGBA-Framebuffer nicht
        fbIdx.Palette.SetColor(12, unchecked((int)fire.Terminal.PixelColor.FromRgb(1, 2, 3).Packed));
        fbIdx.Resolve();
        GfxCheck(fbIdx.GetPixel(175, 5).Packed == fire.Terminal.PixelColor.FromRgb(1, 2, 3).Packed && fbRgba.GetPixel(175, 5).Packed == fbRgba.Palette.GetPacked(12), "Palette-Animation wirkt nur im Palette-Framebuffer");

        // die Palette gehoert dem Framebuffer, nicht der Konsole
        GfxCheck(ReferenceEquals(cIdx.Palette, fbIdx.Palette), "TerminalCanvas.Palette ist die des Ziel-Framebuffers");

        // Index-Farbe bleibt Index: eine spaetere Palette-Aenderung faerbt NEU gezeichneten Text um
        cRgba.SetColor(fire.Terminal.Paint.FromIndex(3), null);
        fbRgba.Palette.SetColor(3, unchecked((int)fire.Terminal.PixelColor.FromRgb(9, 8, 7).Packed));
        cRgba.DrawText(0, 90, "x", fire.Terminal.Paint.FromIndex(3), null);
        bool found = false;
        for (int y = 90; y < 97 && !found; y++) for (int x = 0; x < 8; x++) if (fbRgba.GetPixel(x, y).Packed == fire.Terminal.PixelColor.FromRgb(9, 8, 7).Packed) { found = true; break; }
        GfxCheck(found, "ein Palette-Index wird erst beim Zeichnen aufgeloest");
    }

    // ---- Kreis und Ellipse ----
    {
        foreach (var mode in new[] { RGBA, IDX })
        {
            for (int r = 0; r <= 12; r++)
            {
                var fb = new fire.Terminal.Framebuffer(60, 60, mode);
                fire.Terminal.Shapes.FillCircle(fb, 30, 30, r, Idx(fb, 5));
                var filled = Lit(fb);
                var expected = new HashSet<(int, int)>();
                for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) if (dx * dx + dy * dy <= r * r + r) expected.Add((30 + dx, 30 + dy));
                if (!filled.SetEquals(expected)) { GfxCheck(false, $"FillCircle r={r} [{mode}]: Flaeche = {{dx^2+dy^2 <= r^2+r}}"); break; }
                if (r == 12) GfxCheck(true, $"FillCircle r=0..12 [{mode}]: Flaeche = {{dx^2+dy^2 <= r^2+r}}");
            }
        }
        var fbC = new fire.Terminal.Framebuffer(60, 60);
        fire.Terminal.Shapes.FillCircle(fbC, 30, 30, 10, Idx(fbC, 5));
        var area = Lit(fbC);
        var fbO = new fire.Terminal.Framebuffer(60, 60);
        fire.Terminal.Shapes.Circle(fbO, 30, 30, 10, Idx(fbO, 5));
        var ring = Lit(fbO);
        GfxCheck(ring.IsSubsetOf(area) && ring.Count > 30 && ring.Count < area.Count, "Circle: die Linie liegt in der Flaeche und ist ein Ring");
        // der Ring ist 4-symmetrisch und schliesst die Flaeche ein: kein innerer Flaechenpunkt hat einen Nachbarn ausserhalb der Flaeche ohne selbst auf dem Ring zu liegen
        bool symmetric = ring.All(p => ring.Contains((60 - p.Item1, p.Item2)) && ring.Contains((p.Item1, 60 - p.Item2)) && ring.Contains((p.Item2, p.Item1)));
        GfxCheck(symmetric, "Circle: symmetrisch (Spiegelungen und Diagonale)");
        bool closed = area.All(p => ring.Contains(p) || new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.All(d => area.Contains((p.Item1 + d.Item1, p.Item2 + d.Item2))));
        GfxCheck(closed, "Circle: jedes Flaechenpixel am Rand liegt auf der Linie (keine Luecken)");

        var fbE = new fire.Terminal.Framebuffer(80, 60);
        fire.Terminal.Shapes.FillEllipse(fbE, 40, 30, 20, 8, Idx(fbE, 5));
        var ell = Lit(fbE);
        GfxCheck(ell.Contains((40 - 20, 30)) && ell.Contains((40 + 20, 30)) && ell.Contains((40, 30 - 8)) && ell.Contains((40, 30 + 8)) && !ell.Contains((40 - 21, 30)) && !ell.Contains((40, 30 + 9)),
            "FillEllipse: Halbachsen rx=20, ry=8 treffen genau die Spitzen");
        var fbE2 = new fire.Terminal.Framebuffer(80, 60);
        fire.Terminal.Shapes.Ellipse(fbE2, 40, 30, 20, 8, Idx(fbE2, 5));
        var ellRing = Lit(fbE2);
        GfxCheck(ellRing.IsSubsetOf(ell) && ellRing.Contains((20, 30)) && ellRing.Contains((40, 22)) && !ellRing.Contains((40, 30)), "Ellipse: Linie in der Flaeche, Mitte frei");
        var fbL = new fire.Terminal.Framebuffer(30, 30);
        fire.Terminal.Shapes.Ellipse(fbL, 15, 15, 6, 0, Idx(fbL, 5));
        GfxCheck(Lit(fbL).SetEquals(Enumerable.Range(9, 13).Select(x => (x, 15))), "Ellipse mit ry=0: eine waagerechte Linie");
        var fbV = new fire.Terminal.Framebuffer(30, 30);
        fire.Terminal.Shapes.FillEllipse(fbV, 15, 15, 0, 4, Idx(fbV, 5));
        GfxCheck(Lit(fbV).SetEquals(Enumerable.Range(11, 9).Select(y => (15, y))), "FillEllipse mit rx=0: eine senkrechte Linie");
        var fbN = new fire.Terminal.Framebuffer(30, 30);
        fire.Terminal.Shapes.FillCircle(fbN, 15, 15, -1, Idx(fbN, 5));
        fire.Terminal.Shapes.Circle(fbN, -100, -100, 20, Idx(fbN, 5));
        fire.Terminal.Shapes.FillCircle(fbN, 15, 15, int.MaxValue, Idx(fbN, 5));
        GfxCheck(true, "negativer Radius, Kreis ausserhalb und riesiger Radius werfen nicht");
    }

    // ---- Dreieck und Polygon ----
    {
        var fb = new fire.Terminal.Framebuffer(40, 40);
        fire.Terminal.Shapes.FillTriangle(fb, 5, 5, 25, 5, 5, 25, Idx(fb, 5));
        var tri = Lit(fb);
        GfxCheck(tri.Contains((5, 5)) && tri.Contains((25, 5)) && tri.Contains((5, 25)) && tri.Contains((10, 10)) && !tri.Contains((20, 20)) && !tri.Contains((26, 5)) && !tri.Contains((4, 5)), "FillTriangle: Ecken und Inneres, nicht ausserhalb");
        bool rows = true;
        for (int y = 5; y <= 25; y++) { int n = tri.Count(p => p.Item2 == y); if (Math.Abs(n - (26 - (y - 5) - 5 + 1)) > 1) rows = false; }
        GfxCheck(rows, "FillTriangle: Zeilenbreiten wie bei der Geraden (rechtwinkliges Dreieck)");
        var fbT = new fire.Terminal.Framebuffer(40, 40);
        fire.Terminal.Shapes.Triangle(fbT, 5, 5, 25, 5, 5, 25, Idx(fbT, 5));
        var outline = Lit(fbT);
        GfxCheck(outline.IsSubsetOf(tri) && !outline.Contains((10, 10)) && outline.Contains((15, 5)) && outline.Contains((5, 15)), "Triangle: nur der Umriss, in der Flaeche enthalten");

        var fbR = new fire.Terminal.Framebuffer(40, 40);
        fire.Terminal.Shapes.FillPolygon(fbR, new[] { 4, 6, 20, 6, 20, 15, 4, 15 }, Idx(fbR, 5));
        var fbR2 = new fire.Terminal.Framebuffer(40, 40);
        fbR2.FillRect(4, 6, 17, 10, Idx(fbR2, 5));
        GfxCheck(Lit(fbR).SetEquals(Lit(fbR2)), "FillPolygon eines Rechtecks == FillRect (Randpixel gehoeren dazu)");

        // Even-Odd: ein Stern aus einem Fuenfeck (Pentagramm) hat ein leeres Zentrum
        var fbS = new fire.Terminal.Framebuffer(60, 60);
        int[] star = { 30, 3, 47, 55, 3, 22, 57, 22, 13, 55 };
        fire.Terminal.Shapes.FillPolygon(fbS, star, Idx(fbS, 5));
        var starSet = Lit(fbS);
        GfxCheck(starSet.Contains((30, 12)) && !starSet.Contains((30, 30)) && starSet.Count > 200, "FillPolygon: Even-Odd (das Zentrum eines Pentagramms bleibt leer)");

        var fbP = new fire.Terminal.Framebuffer(40, 40);
        fire.Terminal.Shapes.Polygon(fbP, new[] { 5, 5, 30, 5, 30, 30 }, Idx(fbP, 5), closed: false);
        var open = Lit(fbP);
        GfxCheck(open.Contains((30, 20)) && !open.Contains((15, 18)) && open.Count == 26 + 25, "Polygon offen: Kantenzug ohne Schlusslinie");
        fire.Terminal.Shapes.Polygon(fbP, new int[0], Idx(fbP, 5));
        fire.Terminal.Shapes.FillPolygon(fbP, new[] { 1, 1, 9, 9 }, Idx(fbP, 5));
        fire.Terminal.Shapes.FillPolygon(fbP, new[] { 1, 1, 9, 9, 7 }, Idx(fbP, 5));
        GfxCheck(true, "zu wenige Punkte / ungerade Punktzahl werfen nicht");

        var fbBig = new fire.Terminal.Framebuffer(20, 20);
        fire.Terminal.Shapes.FillTriangle(fbBig, -1000000, -1000000, 1000000, 5, 5, 1000000, Idx(fbBig, 5));
        fire.Terminal.Shapes.Line(fbBig, int.MinValue, 0, int.MaxValue, 7, Idx(fbBig, 5));
        GfxCheck(true, "riesige Koordinaten: kein Ueberlauf, kein Absturz");
    }

    // ---- Flaechenfuellung ----
    {
        foreach (var mode in new[] { RGBA, IDX })
        {
            var fb = new fire.Terminal.Framebuffer(30, 20, mode);
            fire.Terminal.Shapes.Rect(fb, 5, 5, 15, 10, Idx(fb, 7));
            fire.Terminal.Shapes.FloodFill(fb, 10, 10, Idx(fb, 3));
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
            fire.Terminal.Shapes.FloodFill(fb, 0, 0, Idx(fb, 3));
            GfxCheck(fb.GetIndex(0, 0) == 3 && fb.GetIndex(29, 19) == 3 && fb.GetIndex(5, 5) == 7, $"FloodFill aussen: fuellt den Rest, der Rahmen bleibt [{mode}]");
            fire.Terminal.Shapes.FloodFill(fb, 0, 0, Idx(fb, 3)); // schon gefuellt: nichts
            fire.Terminal.Shapes.FloodFill(fb, -5, 100, Idx(fb, 3));
        }
        var fbB = new fire.Terminal.Framebuffer(20, 20, IDX);
        fire.Terminal.Shapes.Rect(fbB, 2, 2, 10, 10, Idx(fbB, 7));
        fire.Terminal.Shapes.Line(fbB, 4, 4, 9, 4, Idx(fbB, 2)); // eine andere Farbe im Innern
        fire.Terminal.Shapes.FloodFillBorder(fbB, 5, 6, Idx(fbB, 3), Idx(fbB, 7));
        GfxCheck(fbB.GetIndex(5, 4) == 3 && fbB.GetIndex(5, 6) == 3 && fbB.GetIndex(2, 2) == 7 && fbB.GetIndex(15, 15) == 0, "FloodFillBorder: fuellt bis zur Randfarbe, auch ueber andere Farben hinweg");
        // grosse Flaeche: kein Stapelueberlauf
        var fbHuge = new fire.Terminal.Framebuffer(600, 600, IDX);
        fire.Terminal.Shapes.FloodFill(fbHuge, 0, 0, Idx(fbHuge, 4));
        GfxCheck(fbHuge.GetIndex(599, 599) == 4 && fbHuge.GetIndex(300, 300) == 4, "FloodFill einer ganzen 600x600-Flaeche");
        // Spirale: lange, verwinkelte Flaeche
        var fbSp = new fire.Terminal.Framebuffer(64, 64);
        for (int i = 2; i < 60; i += 4) fire.Terminal.Shapes.Rect(fbSp, i, i, 64 - 2 * i, 64 - 2 * i, Idx(fbSp, 7));
        fire.Terminal.Shapes.FloodFill(fbSp, 0, 0, Idx(fbSp, 2));
        GfxCheck(fbSp.GetIndex(1, 1) == 2 && fbSp.GetIndex(3, 3) == 0, "FloodFill bleibt hinter einer Wand");
    }

    // ---- Blit ----
    {
        // Quelle 4x4 (RGBA), jedes Pixel eindeutig
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

        // Clipping: Ziel teilweise ausserhalb, Quelle ausserhalb, leere Groessen
        var d7 = new fire.Terminal.Framebuffer(10, 10);
        fire.Terminal.Blitter.Blit(d7, src, -2, -2);
        fire.Terminal.Blitter.Blit(d7, src, 8, 8);
        fire.Terminal.Blitter.Blit(d7, src, 2, 2, 100, 100, 0, 0, 4, 4);
        fire.Terminal.Blitter.Blit(d7, src, 0, 0, 0, 0, 0, 0, 4, 4);
        fire.Terminal.Blitter.Blit(d7, src, 0, 0, 4, 4, 0, 0, 0, 4);
        fire.Terminal.Blitter.Blit(d7, src, -50, -50, 4, 4, 0, 0, 4, 4);
        GfxCheck(d7.GetPixel(0, 0).Packed == src.GetPixel(2, 2).Packed && d7.GetPixel(9, 9).Packed == src.GetPixel(1, 1).Packed, "Blit: Beschneiden an Quelle und Ziel, leere Groessen werfen nicht");

        // Transparent / Blend (RGBA-Quelle mit Alpha)
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

        // Palette-Quelle: Farbschluessel / TransparentIndex, ueber die Palette in einen RGBA-Framebuffer
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

        // Palette -> Palette: gleiche Palette = Indizes direkt; andere Palette = naechster Eintrag
        var palDst = new fire.Terminal.Framebuffer(3, 1, IDX);
        fire.Terminal.Blitter.Blit(palDst, pal, 0, 0);
        GfxCheck(palDst.Indices!.SequenceEqual(pal.Indices), "Blit Palette->Palette (gleiche Palette): Indizes unveraendert");
        var palDst2 = new fire.Terminal.Framebuffer(3, 1, IDX);
        var shifted = new uint[256];
        pal.Palette.CopyPacked(shifted);
        palDst2.Palette.SetAll(shifted.Reverse().ToArray()); // Eintrag i hat dort die Farbe von 255-i
        fire.Terminal.Blitter.Blit(palDst2, pal, 0, 0);
        GfxCheck(palDst2.Palette.GetPacked(palDst2.Indices![0]) == pal.Palette.GetPacked(5) && palDst2.Palette.GetPacked(palDst2.Indices[2]) == pal.Palette.GetPacked(7), "Blit Palette->Palette (andere Palette): gleiche FARBE, anderer Index");

        // RGBA -> Palette: naechster Eintrag der Ziel-Palette
        var truecolor = new fire.Terminal.Framebuffer(2, 1);
        truecolor.SetPixel(0, 0, fire.Terminal.PixelColor.FromRgb(255, 255, 255));
        truecolor.SetPixel(1, 0, new fire.Terminal.PixelColor(0, 0, 0, 0));
        var palTarget = new fire.Terminal.Framebuffer(2, 1, IDX);
        palTarget.Indices![0] = 3; palTarget.Indices[1] = 3; palTarget.MarkDirty();
        fire.Terminal.Blitter.Blit(palTarget, truecolor, 0, 0, fire.Terminal.BlitMode.Transparent);
        GfxCheck(palTarget.Palette.GetColor(palTarget.Indices[0]).Packed == fire.Terminal.PixelColor.FromRgb(255, 255, 255).Packed && palTarget.Indices[1] == 3, "Blit RGBA->Palette: naechster Palette-Eintrag, Alpha 0 uebersprungen");

        // gleicher Puffer, sich ueberlappend: wie eine Kopie
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

        // ConsoleManager: Farbangaben als Zahlen
        var cm = new fire.Terminal.ConsoleManager(mgr, new fire.Terminal.IntegratedGlyphFont());
        int fbId = mgr.CreateFramebuffer(40, 20, IDX);
        int con = cm.CreateConsole(fbId);
        cm.FillRect(con, 0, 0, 10, 10, 9);
        cm.SetPixel(con, 12, 12, 33);
        cm.FillRect(con, 20, 0, 5, 5, unchecked((int)0xFF0000FFu)); // direkter Wert (rot) -> naechster Palette-Eintrag
        GfxCheck(mgr.GetFramebuffer(fbId).GetIndex(3, 3) == 9 && mgr.GetFramebuffer(fbId).GetIndex(12, 12) == 33 && cm.GetPixelIndex(con, 12, 12) == 33
            && mgr.GetFramebuffer(fbId).Palette.GetColor(mgr.GetFramebuffer(fbId).GetIndex(22, 2)).R >= 170, "ConsoleManager: 0-255 = Palette-Index, sonst direkter Wert");
        cm.SetColor(con, 14, 1);
        cm.Print(con, "Hi");
        GfxCheck(Enumerable.Range(0, 8).Any(x => Enumerable.Range(0, 14).Any(y => mgr.GetFramebuffer(fbId).GetIndex(x, y) == 14)), "ConsoleManager.SetColor mit Palette-Indizes (Print schreibt Index 14)");
        cm.DrawText(con, 0, 10, "T", 15, 0);
        GfxCheck(true, "DrawText mit Palette-Index und transparentem Hintergrund");
        cm.FillCircle(con, 30, 12, 4, 6); cm.DrawCircle(con, 30, 12, 6, 7); cm.FillEllipse(con, 10, 15, 5, 2, 8); cm.DrawEllipse(con, 10, 15, 6, 3, 9);
        cm.FillTriangle(con, 1, 1, 8, 1, 1, 8, 2); cm.DrawTriangle(con, 1, 1, 8, 1, 1, 8, 3);
        cm.FillPolygon(con, new[] { 20, 10, 30, 10, 25, 18 }, 4); cm.DrawPolygon(con, new[] { 20, 10, 30, 10, 25, 18 }, 5, true);
        cm.FloodFill(con, 35, 2, 6); cm.FloodFillBorder(con, 35, 2, 7, 6);
        GfxCheck(mgr.GetFramebuffer(fbId).GetIndex(30, 12) != 0, "ConsoleManager: Kreis/Ellipse/Dreieck/Polygon/FloodFill laufen im Palette-Framebuffer");
        int src2 = mgr.CreateFramebuffer(4, 4, IDX);
        mgr.GetFramebuffer(src2).FillRect(0, 0, 4, 4, mgr.GetFramebuffer(src2).ResolveBrush(fire.Terminal.Paint.FromIndex(44)));
        cm.Blit(con, src2, 0, 0, 4, 4, 30, 14, 4, 4, 0, -1);
        GfxCheck(mgr.GetFramebuffer(fbId).GetIndex(31, 15) == 44, "ConsoleManager.Blit kopiert einen anderen Framebuffer");
    }

    // ---- Fenster: Tick rechnet das sichtbare Abbild eines Palette-Framebuffers aus (Resolve), bevor der Renderer es bekommt ----
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

    Console.WriteLine(gfxFailures == 0 ? "Alle Grafik-Pruefungen bestanden." : $"FEHLER: {gfxFailures} Grafik-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Bilder: PNG, BMP, GIF dekodieren (Testdateien von Pillow und eigenen Schreibern, siehe ImageFixtures)
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
    // indiziert: die FARBE jedes Pixels stimmt (Indizes duerfen ein Encoder umnummerieren), optional die Indizes selbst
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

    // ---- PNG: eigener Encoder (alle Farbarten und Tiefen, alle Zeilenfilter) ----
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

    // ---- Fehler: unbekannt, leer, abgeschnitten, beschaedigt ----
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
            try { fire.Terminal.ImageDecoder.Decode(full[..cut]); allFailed = false; }   // ein abgeschnittenes Bild darf (GIF) lenient fehlen, aber nie eine fremde Ausnahme werfen
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
        big[16] = 0x7F; // Breite ~2 Milliarden: CRC-Fehler UND zu gross - in jedem Fall ImageFormatException
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

    // ---- Zufaellig beschaedigte Dateien: nie eine fremde Ausnahme, nie ein Haenger ----
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

    // ---- Framebuffer aus einem Bild ----
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
// Grafik aus fire: Farbmodi, Bilder laden, Zeichenfunktionen (Console/Framebuffer der Grafik-Prelude)
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
        var conManager = new fire.Terminal.ConsoleManager(fbManager, new fire.Terminal.IntegratedGlyphFont());
        var winManager = new fire.Terminal.Windows.WindowManager(fbManager, (l, v) => { }, () => new FakeRenderer());
        fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, conManager, reader ?? (path => ImageFixtures.Get(path)));
        fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, winManager);
        // die Bytes einer Testdatei als Puffer, und ein Puffer mit Unsinn
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

    // Die erwarteten Pixelzahlen der Zeichenfunktionen aus dem Kern selbst (dessen Form pruefen die Grafik-Tests oben): hier geht es um die
    // Anbindung aus fire, in beiden Farbmodi
    string GfDrawExpected()
    {
        var fb = new fire.Terminal.Framebuffer(40, 40);
        var five = fb.ResolveBrush(fire.Terminal.Paint.FromIndex(5));
        var zero = fb.ResolveBrush(fire.Terminal.Paint.FromIndex(0));
        var four = fb.ResolveBrush(fire.Terminal.Paint.FromIndex(4));
        int Count() { int n = 0; for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++) if (fb.GetRaw(x, y) == five.Rgba) n++; return n; }
        var counts = new List<int>();
        void Reset() => fb.FillRect(0, 0, 40, 40, zero);
        fire.Terminal.Shapes.FillCircle(fb, 20, 20, 3, five); counts.Add(Count()); Reset();
        fire.Terminal.Shapes.Circle(fb, 20, 20, 10, five); counts.Add(Count()); Reset();
        fire.Terminal.Shapes.FillEllipse(fb, 20, 20, 8, 3, five); counts.Add(Count()); Reset();
        fire.Terminal.Shapes.FillTriangle(fb, 2, 2, 22, 2, 2, 22, five); counts.Add(Count()); Reset();
        fire.Terminal.Shapes.FillPolygon(fb, new[] { 5, 5, 15, 5, 15, 12, 5, 12 }, five); counts.Add(Count()); Reset();
        fire.Terminal.Shapes.Rect(fb, 5, 5, 10, 10, five); fire.Terminal.Shapes.FloodFill(fb, 8, 8, five); counts.Add(Count()); Reset();
        fire.Terminal.Shapes.Polygon(fb, new[] { 2, 2, 30, 2, 30, 30 }, five, closed: false); counts.Add(Count()); Reset();
        fire.Terminal.Shapes.Rect(fb, 5, 5, 10, 10, four); fire.Terminal.Shapes.FloodFillBorder(fb, 8, 8, five, four); counts.Add(Count());
        return string.Join(" ", counts);
    }

    // GetPixel liefert den Farbwert mit Vorzeichen (32 Bit)
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
        var a = new Console(rgba)
        var b = new Console(pal)
        a.FillRect(0, 0, 4, 4, 9)
        b.FillRect(0, 0, 4, 4, 9)
        print(Px.Get(a, 1, 1) == rgba.GetPaletteColor(9))
        print(b.GetPixelIndex(1, 1))
        a.FillRect(4, 0, 4, 4, 4278190335)
        b.FillRect(4, 0, 4, 4, 4278190335)
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
        var a = new Console(rgba)
        var b = new Console(pal)
        a.FillRect(0, 0, 8, 8, 5)
        b.FillRect(0, 0, 8, 8, 5)
        var before = Px.Get(a, 3, 3)
        pal.SetPaletteRgb(5, 200, 100, 50)
        rgba.SetPaletteRgb(5, 200, 100, 50)
        print(Px.Get(b, 3, 3) == 4278190080 + 50 * 65536 + 100 * 256 + 200)
        print(Px.Get(a, 3, 3) == before)
        """, new[] { "True", "True" });

    CheckGf("Text in einem Palette-Framebuffer: Print und DrawText mit Palette-Indizes", gfHead + """
        var fb = new Framebuffer(80, 28, ColorMode.Palette)
        var con = new Console(fb)
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
        con.DrawText(0, 14, "T", 12, 0)
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
                var con = new Console(fb)
                con.FillCircle(20, 20, 3, 5)
                var circle = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, 0)
                con.DrawCircle(20, 20, 10, 5)
                var ring = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, 0)
                con.FillEllipse(20, 20, 8, 3, 5)
                var ellipse = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, 0)
                con.FillTriangle(2, 2, 22, 2, 2, 22, 5)
                var tri = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, 0)
                con.FillPolygon([5, 5, 15, 5, 15, 12, 5, 12], 5)
                var rect = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, 0)
                con.DrawRect(5, 5, 10, 10, 5)
                con.FloodFill(8, 8, 5)
                var flood = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, 0)
                con.DrawPolygon([2, 2, 30, 2, 30, 30], 5, false)
                var open = Draw.Count(con, 40, 40)
                con.FillRect(0, 0, 40, 40, 0)
                con.DrawRect(5, 5, 10, 10, 4)
                con.FloodFillBorder(8, 8, 5, 4)
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
        var a = new Console(rgba)
        a.Blit(img, 2, 3)
        print(Px.Get(a, 2, 3) == img.GetPaletteColor(img.ReadByte(0)))
        print(Px.Get(a, 14, 9) == img.GetPaletteColor(img.ReadByte(6 * 13 + 12)))
        var pal = new Framebuffer(30, 20, ColorMode.Palette)
        pal.WritePalette(img.ReadPalette(true))
        var b = new Console(pal)
        b.Blit(img, 0, 0)
        print(pal.ReadByte(5) == img.ReadByte(5))
        b.BlitRegion(img, 3, 2, 4, 3, 20, 10)
        print(pal.ReadByte(10 * 30 + 20) == img.ReadByte(2 * 13 + 3))
        b.BlitScaled(img, 0, 0, 13, 7, 0, 10, 26, 7)
        print(pal.ReadByte(10 * 30 + 2) == img.ReadByte(1))
        b.BlitScaled(img, 0, 0, 13, 7, 13, 0, -13, 7)
        print(pal.ReadByte(13) == img.ReadByte(12) && pal.ReadByte(25) == img.ReadByte(0))
        var bg = new Framebuffer(13, 7, ColorMode.Palette)
        var c = new Console(bg)
        c.FillRect(0, 0, 13, 7, 200)
        var sprite = Framebuffer.FromImage(__TestImage("pil_gif_trans"))
        c.Blit(sprite, 0, 0, BlitMode.Transparent)
        var kept = 0
        for (var i = 0; i < 91; i = i + 1) { if (bg.ReadByte(i) == 200) { kept = kept + 1 } }
        print(kept > 0 && kept < 91)
        var tc = Framebuffer.FromImage(__TestImage("png_rgba8"))
        var dst = new Framebuffer(13, 7)
        var d = new Console(dst)
        d.FillRect(0, 0, 13, 7, 4278190335)
        d.Blit(tc, 0, 0, BlitMode.Transparent)
        print(Px.Get(d, 0, 0) == 4278190335)
        """, new[] { "True", "True", "True", "True", "True", "True", "True", "True" });

    CheckGf("Slicer aus fire: ToMask, Slicer.Slice liefert eine List von ToolPath, Fehler als GraphicsException", gfHead + """
        var img = new Framebuffer(120, 70)
        var con = new Console(img)
        con.FillRect(0, 0, 120, 70, 4294967295)
        con.FillRect(10, 10, 100, 50, 4278190080)
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

    // ---- Echte Dateien ueber die Sitzung des Hosts: die IoPolicy entscheidet, was Framebuffer.FromFile lesen darf ----
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
// Slicer: Maske aus einem Framebuffer (ToMask) in Werkzeugbahnen zerlegen
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
        rgba.SetPixel(0, 0, fire.Terminal.PixelColor.FromRgb(10, 10, 10));      // dunkel
        rgba.SetPixel(1, 0, fire.Terminal.PixelColor.FromRgb(240, 240, 240));   // hell
        rgba.SetPixel(2, 0, new fire.Terminal.PixelColor(0, 0, 0, 10));         // dunkel, aber fast durchsichtig
        rgba.SetPixel(3, 0, fire.Terminal.PixelColor.FromRgb(0, 255, 0));       // Gruen: Helligkeit 150
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
        fb.FillRect(x0, y0, x1 - x0, y1 - y0, fb.ResolveBrush(fire.Terminal.Paint.FromIndex(1)));
        return fb;
    }
    {
        // 100 x 50 Pixel zu 0,1 mm = 10 x 5 mm, Fraeser 1 mm: die Werkzeugmitte darf 0,5 mm vom Rand weg sein
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

        var flipped = new fire.Terminal.ImageSlicer(1.0, 0.1).Slice(mask);   // FlipY ist die Vorgabe
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
        // zwei getrennte Flaechen und ein Loch: ein Ring um das Loch
        var fb = new fire.Terminal.Framebuffer(200, 80, fire.Terminal.ColorMode.Indexed);
        var one = fb.ResolveBrush(fire.Terminal.Paint.FromIndex(1)); var zero = fb.ResolveBrush(fire.Terminal.Paint.FromIndex(0));
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

// ---------------------------------------------------------------------------
// UI-Bibliothek (#import "ui"): headless - echte Framebuffer/Konsole/WindowManager, nur der Renderer ist eine Attrappe
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== UI-Bibliothek ===");
    int uiFailures = 0;

    IReadOnlyDictionary<string, RuntimeClass>? uiClasses = null;

    // Fuehrt `script` mit Grafik-Prelude + UI-Prelude aus. Das Fenster laeuft ueber den echten WindowManager (Ereignis-Warteschlange
    // inklusive), nur der Renderer ist `FakeRenderer`: `__TestEvent(typ, ...)` legt ein Ereignis ab, das das naechste Tick abholt,
    // `__TestClose()` schliesst das Fenster.
    List<string> RunUi(string script, VmExecutionMode mode)
    {
        var lines = new List<string>();
        var sources = new[] { fire.Standard.Prelude.Source, fire.Terminal.Bridge.GraphicsBridge.PreludeSource, fire.Windows.Bridge.WindowsBridge.PreludeSource, fire.UI.Bridge.UiBridge.PreludeSource, script };
        var alreadyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = Parser.ParseMultiple(sources.Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), alreadyIncluded)).ToList());
        var natives = new NativeRegistry();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();

        var renderer = new FakeRenderer();
        var fbManager = new fire.Terminal.FramebufferManager();
        var conManager = new fire.Terminal.ConsoleManager(fbManager, new fire.Terminal.IntegratedGlyphFont());
        var winManager = new fire.Terminal.Windows.WindowManager(fbManager, (l, v) => { }, () => renderer);
        fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, conManager);
        fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, winManager);

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
        // GetPixel liefert den Farbwert mit Vorzeichen (32 Bit); die Farben der UI-Bibliothek sind vorzeichenlose Werte
        class Px { static int Get(console, int x, int y) { var v = console.GetPixel(x, y); if (v < 0) { v = v + 4294967296 } return v } }
        var fb = new Framebuffer(320, 200)
        var win = new Window(fb, "Test")
        var ui = new UI.Root(fb, win)

        """;

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
        print("back " + (Px.Get(ui.console, 300, 190) == t.back))
        print("face " + (Px.Get(ui.console, 12, 12) == t.face))
        print("border " + (Px.Get(ui.console, 10, 10) == t.border))
        var textPixels = 0
        for (var y = 10; y < 36; y = y + 1) {
            for (var x = 10; x < 90; x = x + 1) {
                if (Px.Get(ui.console, x, y) == t.text) { textPixels = textPixels + 1 }
            }
        }
        print("text " + (textPixels > 20))
        __TestEvent(9, 20.0, 20.0)
        ui.Tick()
        ui.Draw()
        print("hover " + (Px.Get(ui.console, 12, 12) == t.faceHover))
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
        print(ui.content.children[1].width)
        """, new[] { "Lokal 10", "32" });

    CheckUi("Tick zeichnet, verarbeitet Ereignisse und liefert false, sobald das Fenster geschlossen wurde", uiHead + """
        var b = new UI.Button("OK", 10, 10, 80, 26)
        ui.Add(b)
        print("offen " + ui.Tick())
        print("gezeichnet " + (Px.Get(ui.console, 12, 12) == ui.theme.face))
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

    // VSync: Vorgabe an (Tick wartet auf die Bildwiederholung), per Property abschaltbar - auch in einer abgeleiteten Fensterklasse
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

    Console.WriteLine(uiFailures == 0 ? "Alle UI-Pruefungen bestanden." : $"FEHLER: {uiFailures} UI-Pruefung(en) fehlgeschlagen.");
}

// ---------------------------------------------------------------------------
// Native Callbacks (Fenster-Ereignisse) laufen verschachtelt auf der VM des Threads: echte Globals, kein Kopieren
// ---------------------------------------------------------------------------
{
    Console.WriteLine();
    Console.WriteLine("=== Callbacks auf dem VM-Thread ===");
    int cbFailures = 0;

    // Wie RuntimeSession.CallLambda: der Host-Runner ruft FireRuntime.RunCallback; unbehandelte Callback-Fehler landen als "CB: ..." in der Ausgabe.
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
        var conManager = new fire.Terminal.ConsoleManager(fbManager, new fire.Terminal.IntegratedGlyphFont());
        var winManager = new fire.Terminal.Windows.WindowManager(fbManager,
            (l, v) => FireRuntime.RunCallback(l, v, natives, classes, () => vm!.SnapshotGlobals(), message => { lock (lines) lines.Add("CB: " + message); }, mode, vm),
            () => renderer);
        fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, conManager);
        fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, winManager);

        var kept = new List<LambdaValue>();
        natives.Register("__TestEvent", args => { renderer.Push((int)args[0].AsInt(), args); return Value.MakeUndefined(); });
        natives.Register("__Keep", args => { kept.Add((LambdaValue)args[0].AsLambda()); return Value.MakeUndefined(); });
        // fuehrt das gemerkte Lambda auf einem ANDEREN Thread ohne laufende VM aus (wie ein Host-Ereignis)
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
        class Exception { string message; construct(string message) { this.message = message } }
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

    // Der Performance-Modus prueft nichts: ein Zugriff ausserhalb des Arrays ist eine rohe C#-Ausnahme. Der Zustand der VM muss danach stimmen.
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
// Globals und Fire-Threads: direktes Lesen, Schreiben in Sektionen (sync globals / sync global { } / fire global { })
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

    // Mit #nosync: sonst koennte das automatische Abarbeiten die Anmeldung des Threads vor dem ersten `sync globals` erledigen, dessen Rueckgabe bliebe 0
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
        class Exception { string message; construct(string message) { this.message = message } }
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
        class Exception { string message; construct(string message) { this.message = message } }
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

    // ---- break/continue aus try/catch heraus (der Compiler meldet Handler ab und fuehrt das finally inline aus)
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
        class Exception { string message; construct(string message) { this.message = message } }
        try {
            while (true) { try { break } catch (e) { print("innen") } }
            throw new Exception("aussen")
        } catch (e) { print("gefangen " + e.message) }
        """, new[] { "gefangen aussen" });

    CheckGl("break aus catch (mit finally und eigenen Locals im catch)", """
        class Exception { string message; construct(string message) { this.message = message } }
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
        class Exception { string message; construct(string message) { this.message = message } }
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
            m.Dispose(); // wirkungslos bei geteiltem Manager
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

    // Paketverfolgung direkt am Manager (ohne Skript)
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

    // Alle Erweiterungen zusammen: native Funktionen werden über ihren Index angesprungen, die Reihenfolge der Registrierung
    // beim Übersetzen und beim Ausführen muss übereinstimmen (früher: graphics + time -> falsche Funktion)
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
        var con = new Console(fb)
        con.FillRect(0, 0, 2, 2, 256)
        print(con.GetPixel(1, 1))
        """, new[] { "8x4", "True", "Framebuffer", "1", "False", "256" });

    // Paketprotokoll: Speichern und Laden verlustfrei
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
// Debugger-Lauf des Editors: VM.RunUntilBreakpoint/RunUntilEnd (F5), Zeilentabelle (binaere Suche), Native-Weiterleitung
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
        // Schleifen (for/while/foreach): ein Haltepunkt in der letzten Zeile des Bodys trifft genau einmal je Durchlauf - nicht noch einmal
        // beim Verlassen der Schleife. Dazu als Referenz die fruehere Schleife (StepInstruction + CurrentLocation je Instruktion):
        // RunUntilBreakpoint muss an denselben Stellen anhalten.
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
        // Ohne Haltepunkt laeuft ein Programm in einem Zug zu Ende; ein Haltepunkt in der ERSTEN Zeile zaehlt beim Start nicht
        VM.ResetTerminateForTests();
        var (session, lines) = BuildDbg("print(\"a\")\nprint(\"b\")\n", VmExecutionMode.Debug);
        var vm = session.VirtualMachine!;
        bool stopped = vm.RunUntilBreakpoint(new HashSet<(int, int)> { (session.FirstUserSourceIndex, 1) }, () => false);
        DbgCheck(!stopped && lines.SequenceEqual(new[] { "a", "b" }), "RunUntilBreakpoint: ein Haltepunkt auf der Startzeile haelt nicht sofort an, das Programm laeuft zu Ende");
        VM.ResetTerminateForTests();
    }

    {
        // Pause-Anforderung unterbricht eine Endlosschleife (RunUntilEnd und RunUntilBreakpoint)
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
        // Zeilentabelle: binaere Suche liefert dieselben Stellen wie die Zeile jeder Instruktion erwarten laesst
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
        // Weiterleitung an native Funktionen (Methoden der Brücken-Preludes): gleiches Ergebnis wie der direkte Aufruf, auch mit Rueckgabewert
        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            VM.ResetTerminateForTests();
            var (session, lines) = BuildDbg("""
                #import "graphics"
                var fb = new Framebuffer(16, 16)
                var con = new Console(fb)
                var viaMethod = 0
                var direct = 0
                for (var i = 0; i < 5; i = i + 1) {
                    con.FillRect(i, 0, 1, 1, 256 + i)
                    viaMethod = viaMethod + con.GetPixel(i, 0) + con.CellWidth()
                    direct = direct + __GRPHConGetPixel(con.id, i, 0) + __GRPHConCellWidth(con.id)
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
// Lambda-Captures, Kurzsyntax `x => ...` und die Abfrage-Bibliothek (#import "linq")
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
            // linq bringt reflection mit (SelectProperty/SelectField)
            fire.Runtime.ReflectionNatives.Register(natives);
            sources.Add(fire.Standard.ReflectionPrelude.Source);
            sources.Add(fire.Standard.LinqPrelude.Source);
        }
        if (script.Contains("#import \"time\""))
        {
            fire.Runtime.TimeNatives.Register(natives);
            sources.Add(fire.Standard.TimePrelude.Source);
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

    const string timeHead = "#import \"time\"\nclass Exception { string message; construct(string message) { this.message = message } }\n";

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

    // ---- Operanden-Stack und Exceptions: was die Wurfstelle auf dem Stack hinterlaesst, darf den Aufrufer nicht verschieben
    const string excHead = "class Exception { string message; construct(string message) { this.message = message } }\n";

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

    // Wie ein gepacktes Programm: kompilieren, serialisieren, wieder laden, mit der gepackten Runtime ausfuehren
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
        class Exception { string message; construct(string message) { this.message = message } }
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

    // gepackt: Metadaten und try/catch muessen die Serialisierung ueberleben (catch-Klauseln gingen frueher verloren)
    CheckRf("Gepacktes Programm: Typ-Metadaten, Zugriffsregeln und try/catch ueberleben die Serialisierung", """
        #import "reflection"
        class Exception { string message; construct(string message) { this.message = message } }
        class A { float r; private int s; construct() { this.r = 1.5; this.s = 3 } }
        var t = Type.Of(new A())
        foreach (m in t.All) { print(m.Access + " " + m.TypeName + " " + m.Name) }
        try { Reflect.Get(new A(), "s") } catch (e) { print("privat") }
        try { throw new Exception("x") } catch (e) { print("gefangen") }
        """, new[] { "public float r", "private int s", "public  A", "privat", "gefangen" }, debugRelease, packed: true);

    // ---- probe / silence
    const string probeHead = """
        class Exception { string message; construct(string message) { this.message = message } }
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
// Scope-Verwaltung: return aus verschachtelten Bloecken, Wiederverwendung von Scopes (Pooling)
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

    const string scHead = """
        class D {
            string n
            construct(string n) { this.n = n }
            destruct() { print("~" + this.n) }
        }
        """;

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

    CheckSc("return im try/catch/finally in verschachtelten Bloecken: jedes Objekt genau einmal zerstoert", scHead + "class Exception { string message; construct(string message) { this.message = message } }\n" + """
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

    // ---- Wiederverwendung von Scopes: nichts darf auf eine Scope zeigen, die gleich einem anderen Block gehoert ----

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

    CheckSc("Exceptions durch viele Aufrufe/Bloecke: danach arbeiten die wiederverwendeten Scopes unveraendert weiter", "class Exception { string message; construct(string message) { this.message = message } }\n" + """
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

    // ---- Objekterzeugung: Feld-Vorbelegung ohne Aufruf, Besitz ohne Listen, Zerstoerung ----

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

    // ---- Command/ICommand der Standard-Prelude, generische Basisklassen und Interfaces, Interfaces als Parametertyp ----

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
// Editor-Hilfe: Anker fuer Ueberschriften (Links wie "Datei.md#abschnitt") und die mitgelieferte "First Steps.md"
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

    // Jeder Link "(#anker)" der mitgelieferten Hilfeseiten muss auf eine Ueberschrift zeigen, jeder Link auf eine andere Seite auf eine vorhandene Datei.
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
// Editor: Dokumentationskommentare (///) fuer Klassen, Felder, Properties und Methoden
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

    // Aufrufkontext: Tooltip bleibt waehrend der Argumente, new Foo( springt auf den Konstruktor
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
// Hilfeseite "Embedding.md": die dort gezeigten Host-Beispiele muessen mit der echten API laufen
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

    // Mehrere Quellen werden zu einem Programm verbunden
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

    // Nicht behandelte Exception: Run kehrt normal zurueck
    var failing = RuntimeSession.Build(new[] { "class Oops {\n    string message\n    construct(string message) { this.message = message }\n}\nthrow new Oops(\"boom\")" }, null, writer);
    failing.Run();
    var failingVm = failing.VirtualMachine!;
    CheckEmb("Nicht behandelte Exception steht in vm.UnhandledException",
        failingVm.UnhandledException != null && new UncaughtScriptException(failingVm.UnhandledException).Message.Contains("boom"));

    // IoStdio.Custom und IoPolicy werden von Build angenommen
    var stdioOut = new System.Text.StringBuilder();
    var stdio = fire.IO.Bridge.IoStdio.Custom(text => stdioOut.Append(text), text => stdioOut.Append("[error] ").Append(text), new MemoryStream(System.Text.Encoding.UTF8.GetBytes("in\n")));
    var policy = fire.IO.Bridge.IoPolicy.Rooted(Path.GetTempPath(), readOnly: false);
    printed.Clear();
    RuntimeSession.Build(new[] { "print(1)" }, null, writer, ioPolicy: policy, ioStdio: stdio).Run();
    CheckEmb("ioPolicy und ioStdio sind Parameter von Build", string.Join("|", printed) == "1");

    // terminate(wert): Exitcode ueber VM.ExitValue; das naechste Programm startet wieder normal
    RuntimeSession.Build(new[] { "terminate(7)" }, null, writer).Run();
    var exit = VM.ExitValue;
    CheckEmb("terminate(7): VM.ExitValue ist 7", exit.Kind == ValueKind.Int && exit.AsInt() == 7);
    printed.Clear();
    RuntimeSession.Build(new[] { "print(\"again\")" }, null, writer).Run();
    CheckEmb("Nach terminate laeuft das naechste Programm im selben Prozess normal", string.Join("|", printed) == "again", string.Join("|", printed));
    CheckEmb("Ein normal beendetes Programm hat keinen Exitwert", VM.ExitValue.Kind == ValueKind.Undefined);

    // Skript auf einem Hintergrundthread
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

// ---------------------------------------------------------------------------------------------------------------------------
// Native-Backend (fire.Native): derselbe Quelltext laeuft in der VM und als erzeugtes C++ - die Ausgabe muss identisch sein
// ---------------------------------------------------------------------------------------------------------------------------
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
                inner.Take()
                var r2 = new Res("r2")
                r2.Take()
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
                scratch.Take()
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

    string? cxx = FindCxx();
    if (cxx == null)
        Console.WriteLine("(kein C++-Compiler gefunden - die Native-Backend-Pruefungen werden uebersprungen)");
    else
    {
        string workDir = Path.Combine(Path.GetTempPath(), "fire-native-test-" + Guid.NewGuid().ToString("N"));
        fire.Native.NativeRuntimeFiles.WriteTo(workDir);

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

            string build = RunTool(cxx, $"-std=c++17 -O2 -Wall -Wextra {sanitize}\"{cppFile}\" -I\"{workDir}\" -o \"{exeFile}\"", out int buildExit);
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
            return (c.Name, expected, runExit == 0 ? actual : $"Exitcode {runExit}: {actual}");
        })).ToArray();
        Task.WaitAll(compiled);

        foreach (var task in compiled)
        {
            var (name, expected, actual) = task.Result;
            CheckNat($"Native == VM: {name}", expected == actual, $"  erwartet (VM):\n{expected}\n  erhalten (C++):\n{actual}");
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
            && espCpp.Contains("#define FIRE_HAL_ESP32 1") && espCpp.Contains("#define FIRE_DEFAULT_STACK_BYTES 4096") && espCpp.Contains("#define FIRE_FLOAT32 1"));
        {
            string espFile = Path.Combine(workDir, "esp.cpp");
            File.WriteAllText(espFile, espCpp);
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cxx, $"-std=c++17 -Wall -Wextra -c \"{espFile}\" -I\"{workDir}\" -o \"{espFile}.o\"") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
            string espBuild = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            CheckNat("esp32: das erzeugte C++ uebersetzt ohne Warnung", p.ExitCode == 0 && !espBuild.Contains("warning"), espBuild);
        }
        var cliEsp = CommandLineParser.Parse(new[] { "native", "a.script", "-t", "esp32" });
        CheckNat("Befehlszeile: -t esp32 / unbekanntes Ziel / -t ausserhalb von native",
            cliEsp.Error == null && cliEsp.Target == esp && CommandLineParser.Parse(new[] { "native", "a.script", "-t", "amiga" }).Error != null
            && CommandLineParser.Parse(new[] { "run", "a.script", "-t", "esp32" }).Error != null && CommandLineParser.Parse(new[] { "native", "a.script" }).Target == null);

        // Was noch nicht uebersetzt wird, muss klar abgelehnt werden - nie falsch uebersetzt
        try
        {
            fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "class P { int x\n construct() { this.x = 1 } }\nvar p = new P()\nvar q = copy p" }, null, null, VmExecutionMode.Release));
            CheckNat("Nicht unterstuetzte Opcodes werden abgelehnt", false, "keine Ausnahme");
        }
        catch (fire.Native.NativeNotSupportedException ex)
        {
            CheckNat("Nicht unterstuetzte Opcodes werden abgelehnt (CopyValue)", ex.Message.Contains("CopyValue"), ex.Message);
        }
        try
        {
            fire.Native.CppGenerator.Generate(new Linker().CompileAndLink(new[] { "class P { int v { get { return 5 } } }\nvar p = new P()\nprint(p.v)" }, null, null, VmExecutionMode.Release));
            CheckNat("Properties werden abgelehnt", false, "keine Ausnahme");
        }
        catch (fire.Native.NativeNotSupportedException ex)
        {
            CheckNat("Properties werden abgelehnt", ex.Message.Contains("property"), ex.Message);
        }
        try { Directory.Delete(workDir, true); } catch (IOException) { }
    }

    Console.WriteLine(natFailures == 0 ? "Alle Native-Backend-Pruefungen bestanden." : $"FEHLER: {natFailures} Native-Backend-Pruefung(en) fehlgeschlagen.");
}

static class PackerNativeProbe
{
    [System.Runtime.InteropServices.DllImport("libfiretestnative")] private static extern int Nonexistent();
    public static int Call() => Nonexistent();
}

/// <summary>Schrift ohne Bitmap-Zeilen (nur IsPixelSet) - erzwingt den allgemeinen Zeichenweg von TerminalCanvas.</summary>
sealed class PixelOnlyFont : fire.Terminal.IGlyphFont
{
    private readonly fire.Terminal.IntegratedGlyphFont _inner;
    public PixelOnlyFont(fire.Terminal.IntegratedGlyphFont inner) { _inner = inner; }
    public int GlyphWidth => _inner.GlyphWidth;
    public int GlyphHeight => _inner.GlyphHeight;
    public bool IsPixelSet(char c, int px, int py) => _inner.IsPixelSet(c, px, py);
}

/// <summary>Renderer-Attrappe fuer die UI-Tests: liefert die abgelegten Ereignisse beim naechsten PumpEvents, zeichnet nichts.</summary>
sealed class FakeRenderer : fire.Terminal.IFramebufferRenderer
{
    private readonly List<fire.Terminal.Event.IEvent> _pending = new();
    public bool Closed { get; set; }

    public bool VSync { get; set; } = true;
    public void Initialize(string title, int initialWidth, int initialHeight, int internalHandle) { }
    public void Present(fire.Terminal.Framebuffer framebuffer) { }
    public void Dispose() { }

    public fire.Terminal.WindowPumpResult PumpEvents()
    {
        var events = _pending.ToArray();
        _pending.Clear();
        return new fire.Terminal.WindowPumpResult { StillOpen = !Closed, Events = events };
    }

    /// <summary>Legt ein Ereignis ab: args[0] Typ, danach je nach Typ: Maus (taste, x, y) bzw. Bewegung (x, y), Taste (keycode, modifier), Text (text).</summary>
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
        }
    }
}

sealed class volatile_bool
{
    private volatile bool _value;
    public bool Value { get => _value; set => _value = value; }
}
