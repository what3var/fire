# Exceptions

An exception reports an error that the current code cannot handle itself. It is thrown with `throw`, travels up through the calls and is caught with
`catch`. Exceptions are ordinary objects of classes derived from `Exception`.

## Throwing and catching

```fire
class NotFound : Exception {
    string key
    construct(string key) : base("not found: " + key) { this.key = key }
}

class Store {
    static int Find(string key) {
        if (key == "a") { return 1 }
        throw new NotFound(key)
    }
}

try {
    print(Store.Find("a"))
    print(Store.Find("b"))
    print("not reached")
} catch (NotFound e) {
    print("caught " + e.message + " / " + e.key)
} catch (e) {
    print("something else: " + e.message)
} finally {
    print("finally")
}
```

- `throw value` needs an object of a class derived from `Exception`. Its constructor takes the message (`message`, default `""`).
- The `catch` blocks are checked in order. `catch (Type name)` catches that class and the classes derived from it; `catch (name)` catches everything.
- `finally` runs **always** when the `try` is left: normally, after a `catch`, when an exception passes on, on `return`, `break`, `continue`, `leave`
  and `terminate`.
- An exception that nobody catches stops the program with its message (in the editor a dialog shows it and offers to go on).

## `finally` and `return`

```fire
class T {
    static int Run() {
        try {
            return 1
        } finally {
            print("cleanup")        // runs before the method returns
        }
    }
}
print(T.Run())                      // cleanup, then 1
```

The value of a `return` in the `try` is fixed before `finally` runs. A `return` in `finally` replaces it, a `throw` in `finally` replaces the exception.
`break` and `continue` cannot leave a `finally` block.

## `catch` without `try`

A `catch` can stand alone in a block: it protects everything that follows it **up to the end of the block**.

```fire
{
    print("start")
    catch (e) {
        print("block catch: " + e.message)
    }
    throw new Exception("boom")     // caught by the catch above
    print("not reached")
}
print("after block")
```

## Going on after an error: `resume`

`throw` is also an expression. A handler can call `e.resume(value)`: the program continues **at the place of the `throw`**, and the `throw`
expression gives `value`.

```fire
class Reader {
    static int Read() {
        int v = throw new ArgumentException("no value")
        return v + 1
    }
}
try {
    print(Reader.Read())            // 42
} catch (ArgumentException e) {
    e.resume(41)                    // the throw gives 41
}
```

`e.resume()` without a value gives `undefined`. For a `throw` statement the value is ignored and the program goes on with the next statement.
Exceptions are objects: a handler can store `e` or pass it to another method that calls `resume` later.

## Errors of the runtime

The runtime reports its errors as exceptions of the standard library:

| Class | When |
|---|---|
| `IndexOutOfBoundsException` | an index outside an array, list, buffer or string |
| `DivideByZeroException` | `int` division or `%` by zero (floats give infinity/NaN) |
| `ArgumentException` | wrong number of arguments, a value that cannot be converted (`"42"!int`), a condition that is not a `bool`, comparing two strings with `<` |
| `MissingMemberException` | a method or field that the object does not have |
| `AccessDeniedException` | a `private`/`protected` member used from outside |
| `DestroyedException` | using an object, array or buffer that was destroyed |
| `UnitMismatchException` | units of different dimensions (`5mm + 2kg`) |
| `OverflowException` | a converted number does not fit into its declared unit |

All derive from `Exception` and can be caught:

```fire
try {
    print(1 / 0)
} catch (DivideByZeroException e) {
    print("div: " + e.message)
}
try {
    var a = [1]
    print(a[3])
} catch (IndexOutOfBoundsException e) {
    print("index: " + e.message)
}
```

## Exceptions and ownership

When an exception leaves a block or a method, the objects of that scope are destroyed as usual (destructors run) before the exception is caught.
With `resume` nothing is left, so nothing is destroyed. See [Ownership](13-ownership.md).

## Exceptions in threads

An exception that is not caught in a `fire` thread ends that thread and is delivered to the main program. There, `catch threads(Type e) { ... }`
catches it; without such a handler the program stops. See [Threads and actors](15-threads-and-actors.md#errors-in-threads).

## Exercises

1. Write `static float Divide(float a, float b)` that throws an `ArgumentException` for `b == 0`, and catch it.
2. Define your own exception `ValidationException` with a field `field` and throw it from a method that checks a form.
3. Show with `print` that `finally` runs after `return`, after `break` and after an exception.
4. Use `resume` to replace a missing configuration value by a default.
5. What is printed, and in which order, when a destructor and a `finally` are both involved? Write a small program to find out.
