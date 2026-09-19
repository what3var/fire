using System.Collections.Generic;
using fire.Ast;
using fire.Bytecode;
using fire.Lexing;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;

// Kleiner manueller Smoke-Test für Lexer + Parser + Unit-System, bis der
// Evaluator existiert. Bei dir lokal: `dotnet run` im src/ScriptLang-Ordner.

string sample = """
var a : int = 5mm
var b : float = undefined:km

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
} catch (e : InvalidUnitException) {
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
Console.WriteLine("=== Resolver-Test (muss fehlschlagen: Lambda sieht Block-Scope nicht) ===");

string resolverSampleInvalid = """
{
    var blockOnlyLocal = 42
    var lam = func () => {
        return blockOnlyLocal
    }
}
""";

try
{
    var program = Parser.Parse(resolverSampleInvalid);
    Resolver.Resolve(program);
    Console.WriteLine("FEHLER: hätte ResolverException werfen müssen (Lambda sieht Block-Scope nicht)");
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

var dummyClass = new ClassDecl(0, "Dummy", null, new List<Stmt>());
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

var b : float = 2.5km
var a : int = 500m
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

var narrow : int[8] = 300
var wide : int[64] = 9999999999

int arr[10]
int matrix[][]
var dyn = new int[5]

unsafe {
    var x = 42
    var p : int[64]* = &x
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
    var program = Parser.Parse("var x : int[17] = 1");
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
} catch (e : MyError) {
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
} catch (e : ErrB) {
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
print(list.Get(1))
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.ParseWithPrelude(listSample);
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
var x : int = 5mm
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
} catch (e : MyError) {
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
} catch (e : MyError) {
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

ShowMessageBox("Hallo von ScriptLang!", "extern-Test")
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
} catch (e : IndexOutOfBoundsException) {
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
} catch (e : IndexOutOfBoundsException) {
    print("auch ueber List[] gefangen: " + e.index)
}
""";

try
{
    var natives = NativeRegistry.CreateDefault();
    var program = Parser.ParseWithPrelude(boundsCheckSample);
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
    var program = Parser.Parse(includeMainSample, GetTestDataDir());
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
MessageBoxW(0, "Hallo von ScriptLang!", "#extern-Test", 0)
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
        return this.Get(this.count - 1)
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
    var program = Parser.ParseWithPrelude(extendListSample);
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
Console.WriteLine("=== Bytecode-Test: optionale Parameter (Methode, Lambda, beide Syntaxstile) ===");

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

var h = func (x : int = 42) => { return x }
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
    var program = Parser.ParseWithPrelude(foreachBreakSample);
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
Console.WriteLine("=== Bytecode-Test: break in try innerhalb einer Schleife (muss fehlschlagen) ===");

string breakInTrySample = """
var m = 0
while (m < 5) {
    m = m + 1
    try {
        break
    }
    catch (e) {
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
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
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
    var vm = new VM(compiled.TopLevel, vmGlobalScope, natives, compiled.Classes);
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
    var dummyClass2 = new ClassDecl(0, "SyncGoneDummy", null, new List<Stmt>());
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
    var dummyClass3 = new ClassDecl(0, "TakingDummy", null, new List<Stmt>());
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
    var mainVm = new VM(setupCompiled.TopLevel, mainGlobalScope, mainNatives, setupCompiled.Classes);
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
Console.WriteLine("=== Multithreading: Schreiben auf ein Hauptprogramm-Global in 'fire' wird abgelehnt ===");

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
    Console.WriteLine("FEHLER: Hätte einen ResolverException erwarten sollen, ist aber durchgelaufen!");
}
catch (ResolverException ex)
{
    Console.WriteLine($"Erwarteter Fehler (korrekt abgelehnt): {ex.Message}");
}
catch (Exception ex)
{
    Console.WriteLine($"FEHLER (falscher Exception-Typ): {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Multithreading: Objekt-Global im Snapshot ist isoliert (Mutation betrifft nicht das Original) ===");

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
        $"Hauptprogramm NACH fire (erwartet UNVERAENDERT 100): vault.gold = {vault.Fields["gold"].AsInt()}");
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

    string preprocessed = Preprocessor.Process(source, "/home/claude", registry);
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
    string result = Preprocessor.Process(source, "/home/claude", registry);
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
    string tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scriptlang_include_test_" + System.Guid.NewGuid().ToString("N"));
    System.IO.Directory.CreateDirectory(tmpDir);
    string sharedPath = System.IO.Path.Combine(tmpDir, "shared.txt");
    System.IO.File.WriteAllText(sharedPath, "// GEMEINSAM_INKLUDIERTE_MARKIERUNG\n");

    string rootA = "#include \"shared.txt\"\nprint(\"A\")\n";
    string rootB = "#include \"shared.txt\"\nprint(\"B\")\n";

    // Altes Verhalten (zwei UNABHAENGIGE Process()-Aufrufe, je eigene Menge) -
    // die Markierung landet zweimal in der Summe.
    string outA = Preprocessor.Process(rootA, tmpDir);
    string outB = Preprocessor.Process(rootB, tmpDir);
    int countIndependent = CountOccurrences(outA + outB, "GEMEINSAM_INKLUDIERTE_MARKIERUNG");
    Console.WriteLine($"Getrennte Process()-Aufrufe: Markierung {countIndependent}x (erwartet 2x, je einmal pro Aufruf).");

    // Neues Verhalten: EINE geteilte 'alreadyIncluded'-Menge ueber BEIDE
    // Aufrufe hinweg (wie Parser.ParseWithPrelude es jetzt fuer Prelude +
    // Nutzer-Code macht) - die Markierung landet nur noch EINMAL insgesamt.
    var shared = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    string outA2 = Preprocessor.Process(rootA, tmpDir, shared);
    string outB2 = Preprocessor.Process(rootB, tmpDir, shared);
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
    } catch (e : IndexOutOfBoundsException) {
        print("Skript-seitig gefangen: " + e.message)
    }
    """;

foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
{
    Console.WriteLine($"--- Modus: {mode} ---");
    try
    {
        var program = Parser.ParseWithPrelude(modeTestScript);
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
    } catch (e : AccessDeniedException) {
        print("Erwartet gefangen (privates Feld von aussen): " + e.message)
    }

    try {
        new Locked()
        print("FEHLER: haette AccessDeniedException werfen sollen")
    } catch (e : AccessDeniedException) {
        print("Erwartet gefangen (privater Konstruktor von aussen): " + e.message)
    }
    """;

try
{
    var program = Parser.ParseWithPrelude(accessTestScript);
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
    var program = Parser.ParseWithPrelude(namespaceTestScript, out var activeUsings);
    var natives = NativeRegistry.CreateDefault();
    var resolveResult = Resolver.Resolve(program, natives.Names, activeUsings: activeUsings);
    var compiled = Compiler.Compile(program, resolveResult, natives, activeUsings);
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
