# Ownership

fire has no garbage collector. Instead, **every object has exactly one owner** - a scope (a block, a method call, the global scope) or another
object. When the owner ends, everything it owns is destroyed with it, and the destructors run. This makes the end of an object predictable: files
are closed, connections released, exactly when their owner ends.

Arrays and byte buffers have an owner too. Numbers, `bool`, `char` and strings are plain values and have none.

## Who owns a new object?

| Where the object is created | Owner |
|---|---|
| `var x = new Foo()` in a block or method | that block / method call |
| `obj.field = new Foo()`, a field initializer, `field = new Foo()` in a class | the object `obj` / `this` |
| `return new Foo()` | goes to the caller (with everything that hangs on it) |
| `f(new Foo())` - a new object as an argument | the call of `f`; `f` keeps it with `try x.TakeTo(this)` |
| top-level code | the global scope (lives until the program ends) |

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

## Returning objects

A returned object goes to the caller, together with everything it points to that belongs to the method:

```fire
class Item {
    string n
    construct(string n) { this.n = n }
}
class Factory {
    static Item Make(string n) { return new Item(n) }
}
var kept = Factory.Make("returned")
print(kept.n)                       // still alive
```

## Assignment moves up to the method

When an object of an inner block (a loop body, an `if`) is assigned to a variable of an outer block, it moves to the scope of the **method**
(top-level code: the global scope) and lives until the method ends:

```fire
class Item {
    string n
    construct(string n) { this.n = n }
    destruct() { print("destroy " + this.n) }
}
Item last = undefined
for (int i = 0; i < 2; i++) {
    var it = new Item("loop " + i)
    if (i == 1) { last = it }       // "loop 1" survives the loop
}                                   // destroy loop 0
print("kept: " + last.n)
```

Objects that are assigned in a loop collect until the method ends; use `delete` or `TakeLocal()` when that matters.

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

`List<T>.Add` keeps objects the same way (`list.Add(new Item("x"))` works). A last argument of the enum `Takes` decides what travels along:
`Takes.This` (only the object), `Takes.Children` (also what it points to directly), `Takes.Locals` (everything reachable that belongs to the current
call), `Takes.All` (everything reachable).

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
