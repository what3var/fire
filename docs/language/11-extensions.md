# Extensions: `class extends`

`class extends Name { ... }` adds members to a class that already exists - also to classes of the standard library and to the base types. It creates
no new class and no inheritance: the members become part of `Name`, as if they had been written there.

## Extending a class

```fire
class Animal {
    string name
    construct(string name) { this.name = name }
}

class extends Animal {
    int age
    string Describe() { return this.name + " (" + this.age + ")" }
}

var a = new Animal("Tom")
a.age = 3
print(a.Describe())         // Tom (3)
```

- Fields, methods and properties can be added.
- The class must exist in the program (or its standard library). Several `class extends` blocks for the same class are merged, also from other files.
- A member that the class already has with the same name and number of parameters is a duplicate definition, not a replacement.
- Generic classes are extended with their raw name: `class extends List { T Last() { return this[this.count - 1] } }`.

## Extending base types

`string`, `char`, `int`, `float`, `bool` and arrays (`array`) can get methods too. Inside them `this` is the value itself.

```fire
class extends string {
    string Shout() { return this.ToUpper() + "!" }
    bool IsBlank() { return this.Trim().Length == 0 }
}
class extends int {
    bool IsEven() { return this % 2 == 0 }
}
class extends char {
    bool IsVowel() { return "aeiou".Contains(this) }
}
class extends array {
    int Total() {
        int t = 0
        foreach (x in this) { t += x }
        return t
    }
}

print("hello".Shout())      // HELLO!
print("   ".IsBlank())      // True
print(21.IsEven())          // False
print('e'.IsVowel())        // True
print([1, 2, 3].Total())    // 6
```

Rules for base types:

- **Only instance methods**: a value has no place for fields, so fields, properties, constructors, `static` methods and operators are a compile error.
- `byte` is an `int` at run time: extend `int`. Byte buffers cannot be extended.
- The methods of strings and characters of the standard library (`ToUpper`, `Split`, ...) are such extensions themselves, and `#import "linq"` adds
  its queries to `array` the same way.

## When to use it

- To add helpers to classes you cannot or do not want to change (the standard library, a package).
- To split a large class over several files.
- To give base types small convenience methods (`"x".IsBlank()`, `n.IsEven()`).

For a new kind of object with its own behaviour, derive a class instead ([Classes](08-classes.md#inheritance)).

## Exercises

1. Add `float Average()` to `array` and call it on `[2, 4, 9]`.
2. Add `string Repeat(int n)` to `string`.
3. Add a method `Reverse()` to `List` that reverses the list in place.
4. Try to add a field to `int` - read the error message.
