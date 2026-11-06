# Ownership

fire has no garbage collector. Instead, **every object has exactly one owner** - a scope (a block, a method call, the global scope) or another
object. When the owner ends, everything it owns is destroyed with it, and the destructors run. This makes the end of an object predictable: files
are closed, connections released, exactly when their owner ends.

Arrays and byte buffers have an owner too. Numbers, `bool`, `char` and strings are plain values and have none.

## Who owns a new object?

**A new value belongs to the first slot it lands in.** "New" - *unclaimed* - is what an expression makes right there: `new Foo()`, an array literal,
`new int[n]`, `copy x`/`flat x`, and the result that a call hands back with `return`. `c ? a : b` and parentheses pass it on.

| Where the new value lands | Owner |
|---|---|
| `var x = new Foo()` | the block that declares `x` |
| `x = new Foo()`, `x` declared in an outer block | the block that **declares** `x` |
| `g = new Foo()`, `g` a global variable | the global scope |
| `obj.field = new Foo()`, a field initializer, `field = new Foo()` in a class | the object `obj` / `this` |
| `arr[i] = new Foo()` | the array `arr` |
| `f(new Foo())` - an argument | the call of `f`; `f` keeps it with `take`, `TakeTo` or `try x.TakeTo(this)` |
| `obj.Prop = new Foo()` - a property | the setter, which passes it on to the field it stores it in (see [In setters](#in-setters)) |
| `return new Foo()` | goes on to wherever the call's result lands (with everything that hangs on it) |
| nowhere: `new Foo().Run()`, `print(new Foo())` | nobody: it is destroyed at the **end of the statement** |

```fire
class Item {
    string n
    construct(string n) { this.n = n }
    destruct() { print("destroy " + this.n) }
}
class Bag {
    Item item
    construct() { this.item = new Item("in bag") }      // the bag owns its item
    destruct() { print("destroy bag") }
}
{
    var b = new Bag()
    print(b.item is from b)         // True
}                                   // destroy bag, destroy in bag
print("after the block")
```

The objects of a scope are destroyed when the scope is left - at the end of the block, at `return`, at `break`, and when an exception passes through.

## Temporaries die at the end of the statement

A new value that no slot takes is a **temporary**: it lives until the statement is done, then it is destroyed.

```fire
class Item {
    string n
    construct(string n) { this.n = n }
    destruct() { print("destroy " + this.n) }
    string Name() { return this.n }
}
print(new Item("temp").Name())      // temp, then: destroy temp
print("next statement")             // next statement
```

A method that returns `this` passes the temporary on, so builders work: the last result still counts as new, and the variable takes it.

```fire
class Builder {
    int size = 0
    Builder With(int n) { this.size = this.size + n; return this }
    destruct() { print("destroy builder") }
}
var b = new Builder().With(2).With(3)
print(b.size)                       // 5 - the builder lives on in b
```

The same holds for what hangs on a temporary: `var m = Type.Of(c).Find("radius")` keeps the member, although the type it belongs to dies at the end of the
statement.

The collection of a `foreach` is a temporary of the loop: `foreach (d in Bus.Devices()) { found.Add(d) }` leaves `found` with destroyed devices after the
loop. Keep the collection in a variable when its elements should outlive the loop:

```fire
var all = Bus.Devices()
foreach (d in all) { if (d.Matches()) { found.Add(d) } }
return found                          // the devices in found go along with the return
```

## Returning objects

A returned object goes on to where the call's result lands - a variable, a field, an element, the next call - together with everything it points to
that belongs to the method:

```fire
class Item {
    string n
    construct(string n) { this.n = n }
}
class Factory {
    static Item Make(string n) { return new Item(n) }
}
class Box { Item content }
var kept = Factory.Make("returned")
var box = new Box()
box.content = Factory.Make("boxed")   // the box owns it
print(kept.n + ", " + box.content.n)  // returned, boxed
print(box.content is from box)        // True
```

Arguments count as the method's own (they belong to the call), and a returned lambda takes along what it captured: `return new Wrapper(arg)` and
`return () => list.Count` both keep their values alive at the caller.

A method that returns something that belongs to somebody else - an object's field, a variable of the caller - does not hand it over: the caller
only gets a reference, and the owner stays.

## Assignment moves up to the declaring block

When a value of an inner block (a loop body, an `if`) is assigned to a variable of an outer block, it moves to the block that **declares** the variable
and lives as long as that block:

```fire
class Item {
    string n
    construct(string n) { this.n = n }
    destruct() { print("destroy " + this.n) }
}
Item last = undefined
for (int i = 0; i < 2; i++) {
    var it = new Item("loop " + i)
    if (i == 1) { last = it }       // "loop 1" moves up to the block of `last`
}                                   // destroy loop 0
print("kept: " + last.n)
```

It moves only upwards and never out of the method - out of a method a value goes only with `return` or `take`. Objects that are assigned in a loop
collect in the declaring block; use `delete` or `take` when that matters.

## Arrays own what is stored into them

`arr[i] = new Foo()` (or an array literal, a copy, a returned value) gives the new value to the array: it lives as long as the array. Overwriting the
element does not destroy it.

```fire
class Item {
    string n
    construct(string n) { this.n = n }
    destruct() { print("destroy " + this.n) }
}
{
    Item items[2]
    items[0] = new Item("a")
    items[1] = new Item("b")
    items[0] = new Item("c")        // "a" stays: it belongs to the array
    print("end of block")
}                                   // destroy a, destroy b, destroy c - with the array
```

## A reference does not keep anything alive

Storing an object in a field does **not** change its owner. When the owner ends, the object is destroyed even if a field still points to it -
using it then throws a `DestroyedException`. Whoever wants to keep an object takes it over:

| Call | New owner |
|---|---|
| `obj.TakeTo(other)` | the object `other` |
| `obj.TakeLocal()` | the current scope |
| `obj.TakeUpwards()` | the scope around the current owner |
| `obj.TakeGlobal()` | the global scope |
| `try obj.TakeTo(other)` | only if the caller owns `obj` now; returns `True`/`False` |

`take x` as an argument or on the right of `=` hands `x` over to the receiver, whoever owned it before:

```fire
class Item {
    string n
    construct(string n) { this.n = n }
}
class Holder {
    Item held
    undefined Keep(Item i) {
        this.held = i
        try i.TakeTo(this)          // keep it if it was handed over
    }
}
var h = new Holder()
{
    var temp = new Item("kept")
    h.Keep(take temp)               // hand temp over to the call
}
print(h.held.n)                     // kept - the holder owns it now
```

Without `take`, `temp` would still belong to the block and die at its end; `try` would not move it, because `Keep` is not its owner.

What a method gets with `take` belongs to that call - like a local of it. If the method does not keep it (`try i.TakeTo(this)`) or hand it on with `take`
again, it dies when the method ends, even when a field or a list still points to it:

```fire
class Item {
    string n
    construct(string n) { this.n = n }
}
class Shelf {
    List items
    construct() { this.items = new List() }
    undefined Put(Item i) { this.items.Add(take i) }     // hand it on: the list owns it
    undefined Look(Item i) { this.items.Add(i) }         // only a reference: it dies with the call
}
var shelf = new Shelf()
shelf.Put(new Item("kept"))
shelf.Look(new Item("lost"))
print(shelf.items[0].n)                 // kept
try {
    print(shelf.items[1].n)
} catch (DestroyedException e) {
    print("lost: destroyed")
}
```

In Debug mode (the default in spark, `forge run -m debug`) the VM warns about this when it happens - at the end of the call, not only at the
next use:

```
warning (line 14): an Item is destroyed at the end of the call it belonged to, but the List in the field 'items' of a Shelf still points to it
(it was given only a reference: pass the object on with `take`, e.g. `list.Add(take item)`, or let a longer-living owner take it).
```

It watches the objects that a field or an array is given while they belong to a call (or a block), and reports each place once. Release and
Performance mode and native programs do not check.

`List<T>.Add` keeps objects the same way (`list.Add(new Item("x"))` works); a list, dictionary or set made with `Owns.Values` (or `Owns.Keys`,
`Owns.All`) owns everything that comes in and destroys it when it falls out (see [Containers and ownership](07-arrays-and-lists.md#containers-and-ownership-owns)). A last argument of the enum `Takes` decides what travels along:
`Takes.This` (only the object), `Takes.Children` (also what it points to directly), `Takes.Locals` (everything reachable that belongs to the current
call), `Takes.All` (everything reachable).

## In property accessors

In the `get` and `set` of a property, an assignment hands on what belongs to the accessor call itself (a local object, array or buffer of it):

| Assignment in `get`/`set` | `it` (a local of the accessor) |
|---|---|
| `this.field = it` / `field = it` (a field of `this`) | `this` owns it |
| `this.Prop = it` / `other.Prop = it` (a property) | goes to the setter like `take it` |
| `this.cache = it; return it` in a getter | goes to the caller - `return` wins |

```fire
class Item {
    string n
    construct(string n) { this.n = n }
}
class Holder {
    Item cache
    int Count {
        get {
            var it = new Item("cached")
            this.cache = it             // the holder owns it now
            return 1
        }
    }
}
var h = new Holder()
print(h.Count)
print(h.cache is from h)            // True - without the rule it would have died with the getter
```

Only fields of `this` take a local of the accessor over (`this.node.child = it` does not). Methods do not do this - there, use `take`/`TakeTo`. An
exception thrown after the assignment does not undo it.

## In setters

`obj.Prop = new Foo()` gives the new value to the **setter**. The setter passes it on at its first assignment of `value` to a field or a property - of
any object - and if it has none, to the first call it passes `value` to:

```fire
class Item {
    string n
    construct(string n) { this.n = n }
    destruct() { print("destroy " + this.n) }
}
class Shelf {
    Item top
    List log = new List()
    Item Top {
        get { return this.top }
        set { this.top = value }            // the shelf owns the new item
    }
    Item Peek {
        get { return this.top }
        set { print("only looked at " + value.n) }   // no field, no call: the value dies with the setter
    }
}
var shelf = new Shelf()
shelf.Top = new Item("kept")
shelf.Peek = new Item("seen")       // only looked at seen, then: destroy seen
print(shelf.top.n)                  // kept
print(shelf.top is from shelf)      // True
```

The compiler decides this for every path through the setter. If the path that runs has no such assignment or call (a branch that is not taken), the
value is destroyed when the setter returns. Only the name `value` counts (`var v = value; this.f = v` does not pass it on). A value that is not new
(`shelf.Top = existing`) stays with its owner; the setter only refers to it.

## `delete`

`delete x` destroys an object, array or buffer at once:

```fire
class Item {
    string n
    construct(string n) { this.n = n }
    destruct() { print("destroy " + this.n) }
}
var x = new Item("deleted")
delete x                            // destroy deleted
try {
    print(x.n)
} catch (DestroyedException e) {
    print("caught: " + e.message)
}
```

A destroyed object has no class any more: `x is of Item`, `x!Item` and `throw x` throw the `DestroyedException` as well. Only the kind of the value is
still known - `x is of class` is `True`.

## Testing the owner

`obj is from owner` is `True` when `owner` owns `obj` directly, `obj is under owner` when `owner` is anywhere above it.

## Copying: `flat` and `copy`

| Expression | Result |
|---|---|
| `flat x` | a new object with the same field values; objects, arrays and lambdas in it are **shared** |
| `copy x` | a deep copy: everything reachable is copied once (shared parts and cycles stay shared/cyclic in the copy) |

```fire
class Item { string n }
class Bag {
    Item item
    construct() { this.item = new Item() }
}
var bag = new Bag()
var shallow = flat bag
var deep = copy bag
print(shallow.item == bag.item)     // True - shared
print(deep.item == bag.item)        // False - copied
```

No constructor runs for a copy. The copy has an owner like a new object (the current scope, the object it is assigned to, or the called method when it is
an argument). Be careful with objects that hold an external resource (a file): a `flat` copy shares it.

A class that stands for something that exists only once - a handle, a connection - is marked **`[NoCopy]`**. Its instances are never duplicated: `flat x`
and `copy x` of one, and a `copy` that reaches one through a field or an array element, throw an `ArgumentException` before anything is copied.
The attribute is inherited (a class derived from a resource is still that resource). A `flat` copy of an object that only refers to one is fine - the
reference is shared. `NativeHandle` ([native code](18-native-code.md)) is `[NoCopy]`.

```fire
[NoCopy]
class Connection { int id }
class Client { Connection conn
    construct() { this.conn = new Connection() } }
var client = new Client()
var same = flat client              // fine: same.conn == client.conn
try {
    var twin = copy client
} catch (ArgumentException e) {
    print(e.message)                // 'Connection' cannot be copied ([NoCopy]) - 'copy' reaches an instance of it ...
}
```

The same holds for the copies that [threads](15-threads-and-actors.md#copies-taking-and-sync) make - no form of `taking` moves an object to the
thread, every one copies it; `handover` moves it instead (the way to give a resource to a thread):

| Form | Copies or shares | With a `[NoCopy]` instance |
|---|---|---|
| `fire taking x { }`, `fire Method(args)` (`this` and every argument that is not a direct `new`) | a copy of everything reachable from `x` (an array too), whoever owns the parts; the copies belong to the thread | refused when `x`, a field or an element reaches one: the `ArgumentException` at the `fire`, nothing is copied, the thread does not start |
| `fire handover x { }` (also `fire Method(args) handover x`, and an argument `fire Method(new X())`) | nothing is copied: `x` and everything reachable from it move to the thread, the variable becomes `undefined`; every other reference to them from before is dead (a `DestroyedException` "... was handed over to a thread") | fine - that is what it is for; the thread owns the instance and its destructor runs there |
| `fire global { } taking x` | an object is copied for the job; an array or a text is handed over as it is | an object that reaches one is refused before the job is queued (the job never runs); an array that holds one is fine |
| `sync x`, `try sync x` | a new object in the copy is copied into the original (case A); an object on both sides stays and is synced field by field (case C) | refused before anything is written back when case A reaches one, or when case C would write one into another instance |
| `sync flat x`, `try sync flat x` | like `sync`, but case C keeps the reference and copies nothing | case A is refused as with `sync`; case C is fine |
| `fire with a`, the global variables (also in `sync global { }`) | shared: the actor or the object itself, no copy |
| a message to an actor `a.M(arg)` | a direct `new` is handed over to the actor's thread; another new value (`copy x`, a call result) belongs to the message and then to the method; a variable or field is passed as a reference | fine - a resource in a global variable is used by the threads in a section |
| a host callback (an event from a thread of the host) while the program runs | shared: it runs in the main program with the real global variables | fine |
| a host callback after the end of the program (VM only, while the host still holds the globals) | an isolated copy of the global variables: an object is copied, an array is shared as it is | refused when a global variable (a field, an element of one of its objects) reaches one: the callback does not run, the host gets the `ArgumentException` as the text of an unhandled exception (`'host callback' would copy ...`). A native program runs no callback after its end, so it copies nothing |

The text names the form: "'Connection' cannot be copied ([NoCopy]) - 'taking' would copy an instance of it, which exists only once." (`'sync' reaches an
instance of it ...` for a field or an element). A resource a thread needs is therefore created in the thread, handed over with `fire handover x`, or kept in a global variable.

## Destructors

`destruct()` runs once, when the object is destroyed - before the objects it owns are destroyed. For a derived class, the destructor of the derived
class runs first, then those of the base classes. At the normal end of the program (and on `leave`/`terminate`) the global scope is released, so the
destructors of global objects run too. A destructor should not throw.

## Exercises

1. Write a class with a destructor and create objects in a block, a loop and a method. Predict the order of the messages, then run it.
2. Write a class `Library` that keeps `Book`s in a `List<Book>` and check that the books die with the library.
3. Make an object in a method, store it in a field of a global object without `TakeTo` - what happens when you use it after the method has ended?
   Fix it.
4. Compare `flat` and `copy` on an object that holds an array. Change an element in the copy and print the original.
