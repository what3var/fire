# fire – Language Specification (v0.1)

Reference document for the interpreter. Extended as the implementation grows.

Multithreading (`fire`/`taking`/`with`/`sync`/`leave`/`terminate`/
`catch threads`/`catch terminate`/actors) is deliberately NOT part of this
document – see `THREADING_DESIGN.md` for it (fully implemented,
architecture AND language syntax).

## 0. Statement separation

Two statements must be separated by a `;` **or** a line break – if both are missing, that is a parse error (instead of silent misbehavior in which the parser ambiguously keeps reading across a line). The end of a block (`}`) and the end of the file also count as a valid termination, which allows one-line blocks such as `{ return x }`.

Consequence for the parser: every "optional keep reading" decision (binary operators, assignment `=`, the postfix chain including the coercion suffixes `:`/`!`) stops at a line break instead of greedily reaching into the next line. This does not apply inside an open `(` or `[` – multi-line function calls, argument lists and conditions work as usual, because the bracket makes it clear anyway that the expression continues. An expression can still span several lines if the operator is at the **end** of the first line (`var x = a +\n    b`), since parsing the right operand after an already consumed operator is no longer an "optional keep reading" but is mandatory.

**Explicit continuation with `_`:** If a lone `_` is the last "word" of a line (optionally followed by a line comment), the following line break is not counted as a statement separator – the next line continues the current statement as if there were no line break. The `_` itself produces no token (it is swallowed by the lexer) and in every other position remains a perfectly normal, valid identifier (e.g. as a variable name).

```
var x = a _
    + b + c    // one statement, even without '(' and without a trailing operator

var _ = foo()  // '_' here is a perfectly normal identifier, not a continuation
```

```
var a = 1
var b = 2          // ok: the line break separates

var c = 1; var d = 2   // ok: the semicolon separates

var e = 1 var f = 2    // ERROR: neither ';' nor a line break
```

## 1. Basic types

`bool`, `int`, `float`, `char`, `string`, `class`, `undefined`

- `bool`, `char`, `string` are value types (copied on assignment) and carry no unit.
- `int`, `float`, `undefined` additionally carry a **unit** (see section 3). If it is missing, the default is `unitless`.
- `class` references an object instance (reference type, subject to the ownership model, see section 2).

```
var x = 1;              // int, unitless
var y = 2mm;             // int, unit mm
var z = undefined : kg;  // undefined, unit kg
```

### 1.1 Operators (arithmetic, bitwise) & precedence

From loose (far up) to tightly binding (far down):

```
||                        logical or
&&                        logical and
|                         bitwise or     (int only)
#                         bitwise exclusive or (int only - NOT '^', that is exponentiation)
&                         bitwise and      (int only)
== != ##                  equality (`##` is a synonym for `!=`)
< <= > >=                 comparison
<< >>                     bit shift operators (int only)
+ -                       addition/subtraction
* / %                     multiplication/division/modulo
^                         exponentiation       (right-associative, NOT bitwise XOR)
- ! ~ * &                 unary (sign/negation/bit inversion/dereference/address-of)
```

**Special case `^` and unary operators** (Python's `**` convention, not
"unary always binds tightest"): a sign IN FRONT OF the whole power
binds LESS tightly than `^` itself - `-2^2` is `-(2^2) = -4`, not
`(-2)^2 = 4`. The EXPONENT (right of `^`), on the other hand, may simply
start with a unary operator without needing parentheses - `2^-2`
is `0.25`, neither rejected nor `-4`.

`+`/`-`/`*`/`/`/`%` work on `int`/`float` (see section 3 for the
interplay with units), and `+` additionally for string
concatenation (as soon as one side is a `string`, the other is appended via
its normal `ToString()` representation). `^` (exponentiation) also accepts
`int`/`float`; for `int^int` with a non-negative exponent
the result stays `int` (fast integer exponentiation), otherwise the
calculation is done via `float` (also for a negative exponent, since a plain
`int` integer rounding of values <1 would almost always yield just 0).

`&`/`|`/`#`/`<<`/`>>` (bitwise operators, including the unary `~`)
accept **only** `int` - a run-time error for `float`/
other types. The right operand of `<<`/`>>` (the shift distance) is
deliberately exempt from any unit check (a pure count), and the
result takes over the unit of the left operand unchanged; `&`/`|`/
`#`, on the other hand, require the same unit on both sides, like `+`/`-`/`%`.

**Important:** `<<`/`>>` are deliberately NOT recognized by the lexer as separate two-
character tokens (unlike e.g. `==`), but are only combined by the parser from
two consecutive `<`/`>` tokens - for the lexer `<`/`>` always remain single characters (except `<=`/`>=`), because `>` also serves to
close a generic type argument list, even arbitrarily deeply
NESTED (`new Box<Box<int>>()`, see section 5.8) - a lexer
that greedily merged `>>` into a shift operator token would
break that. Since type argument lists run through their own, completely separate parser
methods (never through the normal expression precedence chain), there is
no conflict anyway.

**Number literals in binary/hexadecimal notation**: `0b`/`0x` prefix
(upper or lower case of both letters allowed), e.g. `0b01101100`,
`0xFFCC8080` - always `int`, never `float` (no fraction/exponent for a
bit pattern notation). They support the same unit suffix as a
normal number literal (`0xFFmm`). Since the language already has a base unit
`b`/`B` (bit, with SI prefixes), `0b`/`0x` is only read as a radix
prefix if DIRECTLY after it at least one digit valid for the respective
radix follows - `0b` without a following valid binary digit
therefore still means "the number `0` with unit suffix `b`" (zero bits), not a
lexer error.

## 2. Ownership model

Every object instance (`class`) has **exactly one owner**: either a scope (block/function/global) or another object instance.
**Arrays and byte buffers** (`new int[n]`, `[1, 2]`, `new byte[n]`) are part of the model as well (2.5): they also have exactly one owner, but they own nothing themselves
(the elements of an array do not change their owner). Strings and the basic types are plain values and have no owner.

### 2.1 Initial owner on creation (`new Foo()`)

- If the new object is **assigned directly to a field of another object** (`obj1.Foo = new Bar()`), the owner is immediately `obj1`.
- In all other cases (local variable, parameter value, expression) the owner is the **current scope**.
- This also applies to the initializer of an instance field (`Item it = new Item(1)` in the class body) and to a bare field name in a class (`field = new X()` instead of `this.field = new X()`): the object belongs to the instance. (It used to belong to the initializer/method scope there and was destroyed when that scope was left, although the field still pointed to it.)
- Arrays and buffers follow the same rule: `obj.items = new int[8]`, `obj.items = [1, 2]`, `obj.data = new byte[4]`, a bare `items = ...` in a class and a field initializer give the array to the
  object; everywhere else (a variable, an argument, a return value, the result of a native function such as `text.Split(",")`) it belongs to the **current scope**. An array that is made **in one
  expression together with inner arrays** (`new int[3][4]`, `[[1, 2], [3]]`) owns them: the inner arrays live and die with the outer one. An array that is only assigned to an element later
  (`a[i] = new int[2]`) does not change its owner.
- **Direct assignment of any call result to a field** (`this.child = Make()`, `this.data = Util.Make(5)`) gives the value to the object as well, not only a `new`: a value that is still in the hands of the
  current function (freshly returned, or belonging to one of its scopes) becomes the object's. A value that belongs to someone else (an object, the caller, the global scope seen from a function) stays where it is.
- **Assignment moves ownership up to the function scope.** `x = value` where `x` is a variable of an *outer* block scope moves a value that belongs to an inner block (loop body, `if`, bare block) into the scope of the
  enclosing **function** (top-level code: the global scope) - never out of the function. `keep = b` inside a loop therefore keeps `b` alive after the loop and until the function ends; without the assignment it
  would die with the loop body. (Objects and arrays assigned in a loop accumulate until the function ends: use `delete` or `TakeLocal`/`TakeTo` for something that should not.)
- **A call result passed on as an argument belongs to the called function, not to the caller:** in `f(g())` the value that `g` returns belongs to the call of `f`: it dies when the call is over - after everything `f` created
  itself - unless `f` keeps it (`TakeTo`, `try x.TakeTo(this)` (2.2), returns it). Only values that are fresh at the caller move; `f(g())` where `g` returns something that belongs to an object does not change that owner.
- The same rule applies to lambda values: direct field assignment → owner is the object; otherwise → current scope. The `on` binding (this context, see 4.2) is independent of this and does not change the owner.

### 2.2 Ownership transfer (member functions on object instances)

- `obj.TakeLocal()` – the owner becomes the **current scope** (the scope that contains the call).
- `obj.TakeUpwards()` – the owner becomes the parent scope of the current owner scope (only meaningful if the current owner is a scope).
- `obj.TakeGlobal()` – the owner becomes the global scope.
- **`take x`** - the keyword form of the transfer, valid as an **argument** of a call and on the **right of `=`** or `var a =`: `x` (an object, an array or a buffer; anything else is let through) belongs to the receiver
  from now on, **whoever owned it before** (no check, unlike `try`). `var a = b(take e, d)` is the same as `var a = b(c(), d)` with `var e = c()`: the value belongs to the **call** of `b` and dies after the
  callee's own locals unless `b` keeps it (`TakeTo`, `try x.TakeTo(this)`, return). In an assignment the receiver is the holder of the target: `obj.field = take x` / `field = take x` the object,
  `arr[i] = take x` the array (an array can own objects and arrays, 2.5), `v = take x` the scope of the variable `v`, `var a = take x` the current scope. For a native function or a built-in method, which have no call scope of their own, an argument
  `take x` goes to the current scope. A destroyed value throws a `DestroyedException`; moving it below itself is a run-time error (cycle). Anywhere else `take` is a compile error; it is a reserved word (not an identifier any more) - `Take`, `TakeTo` and the other methods are unaffected.
  `list.Add(take it)` therefore does not make the list the owner - `Add` only keeps a reference; use `it.TakeTo(list)` for that.
- `obj.TakeTo(other)` – the owner becomes `other` (an object instance).
- All four are built-in methods of every object instance - and of every array and buffer (`TakeTo(obj)` needs an object as the target; a class that declares a method of the same name itself takes precedence). A function can thereby keep an object that belongs to it (e.g. a copy passed as a parameter, 2.4): `param.TakeTo(this)`.
- **What travels along.** Every one of them takes a last argument of the enum `Takes` (always available): `TakeLocal(Takes.Locals)`, `TakeUpwards(Takes.Children)`, `TakeTo(obj, Takes.All)`, `TakeGlobal(Takes.This)`. Without it the mode is `Takes.This`.
  - `Takes.This` - only the object itself (what it owns goes along anyway, it is part of its tree).
  - `Takes.Children` - the object and everything it points to directly; it owns them afterwards. For an array and for everything that implements `IEnumerable` that are its **items** (an object: through its
    enumerator - `GetEnumerator`, `MoveNext`, `GetCurrent`), for any other object its fields.
  - `Takes.Locals` - like `return` (2.3): everything reachable from the object that belongs to a scope of the running call, recursively. A thing that is taken along belongs to the object that points to it
    (for an array: to the owner of the array, if that is an object, else to the array itself - an array can own objects and arrays that were taken along; they die with it), otherwise to the new owner of the object.
  - `Takes.All` - everything reachable, recursively, whoever owns it.
  
  Every thing is visited once (references can form cycles; the ownership stays a tree: what would end up below itself stays where it is). A destroyed object is never taken.
- **`try`: only the owner moves it.** `try obj.Take...(...)` (also `try obj.TakeTo(other, Takes.X)`) moves the object only if the caller is its owner at this moment - it belongs to a scope of the running call or to the
  current object (`this`) - and gives `true` if it moved it, else `false` (nothing changes). A library can thereby take what is handed to it without stealing what belongs to somebody else:
  `construct(source) { this.source = source; try source.TakeTo(this) }` keeps a stream that is only the result of a call passed straight on (`new Reader(File.Open(p))`, 2.1) and leaves a stream of the caller alone.
  What travels along (`Takes`) only moves when the object itself moved.
- **Cycle protection:** `TakeTo(other)` checks whether `other` is transitively already a "descendant" (directly or indirectly owned) of `obj`. If so: run-time error instead of a cycle in the ownership tree.
- **Race with an ongoing deletion:** If `other` (the target of `TakeTo`) is itself currently in cascade deletion (its own owner was just destroyed, its `destruct()` cascade is already running), the transfer is treated as if it had happened one second *before* the start of that deletion: `obj` is also taken into the running cascade immediately and deleted along with it (including the `destruct()` call), instead of remaining behind as an orphan with a half-destroyed owner.
- Variable bindings (name → value) themselves do **not** move – only object ownership is transferable.

### 2.3 Lifetime / cascade deletion

- When an owner is destroyed (a scope when it is left, or an object when it is deleted), all objects whose owner it still is are destroyed recursively along with it (cascade). `destruct()` is called per object (see 5.3).
- **End of program, `leave` and `terminate`:** At the **normal end** of the program, on `leave` and on `terminate`, the **global scope** is released like any other
  scope when it is left: `destruct()` runs for everything it (transitively) owns, so open streams are closed. `leave` ends
  the **calling thread**, `terminate` **all threads** (can be triggered from anywhere, the gentle end for everything). The thread that calls `leave`/`terminate`
  stops **immediately** (no further statement, in every execution mode, even in the middle of a property/a destructor/an operator overload); afterwards its
  open scopes are unwound and `finally` blocks run. The main program **waits at its end for all running
  `fire` threads** (they can write back into its objects via `sync`; this also applies after `leave`/`terminate`), and only then are its globals destroyed - the
  global destructors therefore run after the end of the last thread. At its
  end a fire thread only destroys objects it created itself - copies of objects of the main program (globals snapshot, `taking`) are left untouched, so that they e.g. do not close a
  shared handle. An unhandled exception unwinds the open scopes but does not release the global scope. As a
  safety net the host additionally closes all streams that are still open at the end (the host session of the io package: `PackageHost.Begin(...).Dispose()`). A destructor should never
  throw: an unhandled error in it ends the program (the `IO` destructors therefore swallow IO errors).
- **`return`:** If a value that belongs to one of the scopes being left (directly, or through objects that belong to them) is returned - an object, an array or a buffer -, it does not die with them: the
  ownership passes to the calling scope, **together with everything that hangs on it and also belongs to those scopes** (that is `Takes.Locals`, 2.2): the elements of a returned list, the objects a returned
  object points to. What is taken along belongs to the object that points to it, so it lives and dies with it. A value that belongs to someone else (an object that stays, the caller, the global scope) is not touched.
- **Destroyed objects.** A destroyed object is **dead** when the destruction that destroyed it is over (the scope is left, `delete` returned): using it - a field, a method, a property - throws a catchable
  `DestroyedException`, like an array (2.5). While the destruction is still running (the destructors of one scope), the objects of that scope can still be used, also those that were destroyed before - the destructor of a
  writer can flush a stream that was destroyed before it. A reference does not keep anything alive: what a function wants to hand out has to be returned, taken (`Take...`) or stored in an owner that lives on.

### 2.5 Arrays and buffers in the ownership model, `delete`

- An **array or buffer is destroyed** when its owner is destroyed or left (a scope is left, an object is deleted) and when `delete` is applied to it. Destroying it releases its memory; nothing runs
  (there is no destructor), the inner arrays it owns (2.1) are destroyed with it.
- **Using a destroyed array or buffer is an error**: reading, writing, `Length` and `foreach` throw a catchable `DestroyedException` in the execution modes Debug and Release. In `#performance` mode
  nothing is checked (the behaviour is undefined; natively that is a read of freed memory). A destroyed **object** is treated the same way (2.3): dead objects throw `DestroyedException` (a shallow copy, 2.4, may point to one).
- A reference does not keep anything alive: `this.items = tmp` does **not** move `tmp` to the object - `tmp` still belongs to the scope and dies with it. The owner has to be changed explicitly:
  `tmp.TakeTo(this)`. The same holds for an array that is stored in a variable of an outer scope in a loop body (`e = Next()` inside a loop: `e.TakeUpwards()`, or declare it in the loop).
- **`delete expression`** destroys an object, an array or a buffer at once and detaches it from its owner: for an object the destructor runs and everything it owns is destroyed (2.3), exactly as if its owner
  had been left. Variables that still refer to it see a destroyed object (see above). `delete` on anything else is a run-time error. `delete` is a word only in front of a name on the same line.

```
var a = new int[4]
delete a
print(a[0])                 // DestroyedException

class Cache { int items[]; Grow() { var bigger = new int[8]; this.items = bigger; bigger.TakeTo(this) } }
```

### 2.4 Copying: `flat` and `copy`

```
var a = new Box("a")
a.item = new Item(1)

var f = flat a        // shallow copy: new Box object, a.item is shared
var d = copy a        // deep copy: new Box object AND new Item
holder.other = copy a // assigned directly to a field: the copy belongs to holder (as with `new`, 2.1)
Work(flat a)          // also as an argument
```

`flat` and `copy` are prefixes in front of an expression (`copy a.b` copies `a.b`) and reserved as words - `copy` is thus
no longer an identifier; `flat` already was one (`sync flat`).

**`flat x`** copies the object itself together with its fields (including the backing fields of auto-properties). Value-like fields
(`bool`/`int`/`float`/`char`/`string`/`undefined`) are taken over as a value, everything reference-like - objects, arrays, buffers,
lambdas, pointers - remains **the same reference as in the original**.

**`copy x`** is a deep copy: every instance reachable from the operand via fields and array elements is copied exactly **once**.
If the same instance (or the same array) occurs again - shared or cyclic -, the copy points to the
copy already made: the structure of the original (sharing, cycles) is preserved.

**Owner.** The copy is a new object and gets an owner, depending on where it goes:
- **As an argument** of a call (`f(copy a)`, `obj.M(flat a)`, `new X(copy a)`, `base(copy a)`, lambda call): the copy belongs to the **scope of the
  called function** and is destroyed when it ends - unless the function returns it (then it goes to the caller, 2.3) or
  keeps it with `param.TakeTo(...)`/`TakeGlobal()` (2.2). This also applies to constructors: `construct(i) { this.held = i }` alone is not enough,
  `i.TakeTo(this)` is part of it. For a **native** function (`print(copy a)`) and for messages to an actor there is no such scope - there the
  copy belongs to the current scope. The copy is only created **at the call**, after all arguments have been evaluated (`f(copy a, a.Inc())` thus copies the
  state after `Inc()`); `flat`/`copy` as an argument is possible for the first 16 arguments.
- **Assigned to an object** (`obj.field = copy x`, `this.field = ...`, bare `field = ...` in a class, field initializer `Item i = copy x`): the
  object becomes the owner, as with `copy.TakeTo(obj)` (2.2) - the special rule applies as well: if the target object is already in cascade deletion, the copy
  is destroyed along with it immediately.
- **Otherwise** (local variable, index assignment, expression): the current scope.

For `copy`, the following applies to the instances below: if a copied instance was owned in the original by an instance that is also copied, its copy belongs to that
instance's copy (the ownership tree is reproduced); everything else - in particular instances that belong to someone else in the original (a scope, an object outside
the copy) - belongs to the owner of the root copy. This way every copy is destroyed with its owner (2.3), and the originals remain untouched.

**Further rules**
- **No constructor** runs - the field values are simply transferred. The destructor runs for the copy as for any object.
  Be careful with objects that hold an external resource (e.g. an `IO.FileStream` with its handle): the copy shares the
  handle with the original, and both close it when destroyed.
- An **actor** as an operand is an error; an actor inside a copy remains a shared reference (actor references are meant to be
  passed around). Likewise an already destroyed object inside remains a shared reference; as an operand it is an error.
- Lambdas and pointers are shared in both cases, not copied.
- Operands without content (number, text, `true`, `undefined`) simply yield themselves. An **array** as an operand: `flat` creates a new
  array with the same elements, `copy` also copies the elements; a **buffer** (`byte[]`) is copied byte by byte in both cases.
- `flat` can leave shared references between original and copy whose owner is the original: if the original is
  destroyed (together with what it owns), the shallow copy points to destroyed objects. Whoever needs a self-contained structure uses `copy`.
- Unlike `taking` (a copy for a thread, rejects every reference out of the ownership tree, `sync` writes back), this is an
  ordinary copy without a link back to the original.

## 3. Units

### 3.1 Syntax

- Directly on a number literal without a separator: `5mm`, `2mm`, `1km`.
- `:` operator for **unit** annotation/coercion on a value: `undefined : km`.
  - `value:` (without an argument) → the unit is derived automatically from the context of the operation (see 3.4).
  - `value:unit` → the unit is explicitly forced/converted to `unit`.
- `!` operator for **type** coercion: `value!`, `value!int`.
  - `value!` (without an argument) → the type is derived from the context.
  - `value!type` → the type is explicitly forced.
- `:` (unit) and `!` (type) are independent operators and can be combined freely: `a:km!`, `a:!`.

### 3.1.1 `!` depends on the context by position (prefix vs. suffix)

- **Prefix** `!expression` → logical negation (classic "not").
- **Suffix** `expression!`, `expression!type` → type coercion (see above).
- Since both positions are syntactically unambiguous (prefix in front of a unary operand, suffix after an already parsed expression), a single lexer token (`Bang`) is enough; the disambiguation is done in the parser by grammar position.
- **`~expression`** (prefix only) → bitwise inversion.

The **type** of a variable is given BEFORE the name (`int x`, `float y = 2.5`, `Foo f`); `var x` derives it from the value. A `:` after the variable name (`var x : mm`), on the other hand, only fixes the **unit** (see "unit declarations") – `var x : int` as a type declaration no longer exists.

### 3.2 Target unit in operations ("anchor rule")

For a binary operation between operands with units:

- If **exactly one** operand does not request `:` coercion, its unit is the target unit of the whole operation; all other (compatible) operands are converted to it.
- If **several/all** operands request `:` auto-coercion (without any of them naming an explicit target unit), the target unit is `unitless`.
- If **no** operand requests a coercion and the units have the **same dimension but a different scale** (`500mm + 2m`), the operands are converted **implicitly** - no `:` needed. The type never changes: two `int`s stay `int`, and the **finer** unit is the target as long as the converted value does not overflow (`2m + 500mm` and `500mm + 2m` are both `2500mm`). If it would overflow, the **coarser** unit is the target and the fraction is cut off (`1500mm + 900000000000000000m` is `900000000000000001m`). With a `float` operand the result is a `float` in the unit of the left operand (`1.5mm + 1m` is `1001.5mm`, `2.5m + 250mm` is `2.75m`). This applies to `+`, `-`, `%` and the comparisons.
- Incompatible dimensions (`5mm + 2kg`, `5mm + 2`) are a run-time error, unless a suitable coercion is requested.
- **Chains with more than two operands** (`a + b + c`) are evaluated in the classic left-associative way: `(a + b) + c`. The anchor rule is applied again at every partial step, with the intermediate result (including its already determined unit) counting as the left operand of the next step – so there is no global "all operands at once" view across the whole chain.

```
int a = 5mm
float b = undefined:km

var c = b + a:!     // a is auto-coerced (unit+type), b is the anchor -> c : float:km
var d = b: + a:!    // both request auto-coercion -> no anchor -> d : float:unitless

b = a:km!            // a explicitly to km (unit) and automatically to float (type)
```

### 3.3 Prefix system

Prefixes (decimal, implicitly coerced):

| Symbol | Name  | Factor |
|--------|-------|--------|
| f      | femto | 1e-15  |
| p      | pico  | 1e-12  |
| µ      | micro | 1e-6   |
| m      | milli | 1e-3   |
| c      | centi | 1e-2   |
| k      | kilo  | 1e3    |
| M      | mega  | 1e6    |
| G      | giga  | 1e9    |

Base units that accept prefixes: `m` (meter), `g` (gram), `s` (second), `b` (bit), `B` (byte, 8 `b`).

Time units that do not accept prefixes but are dimension-compatible with `s`, with fixed factors:

| Unit    | Factor to `s`  |
|---------|----------------|
| `min`   | 60             |
| `h`     | 3600           |
| `d`     | 86400          |

Any other, unrecognized suffix string on a literal is treated as an **atomic unit** (compatible only with itself), e.g. `5apples`.

### 3.4 Dimension arithmetic

- Multiplication/division combines dimension vectors additively/subtractively via the exponents (`mm * mm → m²` dimension with scaling factor 1e-6, `m / s → m·s⁻¹`).
- Two units are compatible (can be added/compared) if their dimension vectors match; the conversion is done via the ratio of their scaling factors to the base unit.
- Multiplication/division with a unitless operand returns EXACTLY the unit of the other operand (`20mm / 2 → 10mm`, not some newly constructed, unnamed unit) - otherwise the display would swallow the scaling factor (see the `mm` example). The same named unit times itself (`radius * radius`) likewise gets a synthesized display name (`mm^2`). For all other composite cases (e.g. chained `a*a*a`, or genuinely mixed units with a scaling factor ≠ 1.0) the output shows the factor explicitly (`m^3(×1e-09)`) instead of silently ignoring it - a deliberate, simple limit instead of a complete "synthesize pretty unit names for arbitrary combinations" system.

## 4. Scopes & lambdas

### 4.1 Scope hierarchy

- Every block (`{}`), every loop iteration and every function creates its own scope node in a tree with a parent pointer, up to the global scope.
- Name resolution for **normal code** walks up the scope chain in the classic (lexical) way.

### 4.2 Lambdas

- During name lookup a lambda sees **its own scope, the global scope and copies of the outer local values that its body uses** (captures, see 4.2.1) – no closure over scopes in between.
- `on obj` binds an object as the `this` context, either at definition (`func (X) on obj => { ... }`) or afterwards on assignment (`var b = a on obj2;`, creates a new lambda value with a different `this`, `a` remains unchanged).
- Member variables of the bound `this` object are visible unqualified in the lambda body: for a lambda with `on target` (`func (x) on win => { n = n + x; Hello() }`) unknown names are members of the target - reading, writing, `++` and method calls act as if `this.` were in front (the class is only known at run time, an unknown name there is a run-time error). Local variables, parameters and captures take precedence. A lambda without `on` knows no members (an unknown name is a compile error).
- Ownership of the lambda value follows section 2.1 (field assignment → object owner, otherwise scope owner) – independent of the `on` binding.
- For a type annotation that expects a lambda value (field, parameter, return type, `var`), the type name **`lambda`** is available, optionally with a signature: `[ReturnType] lambda[<ParamType1,...,ParamTypeN>]`. Deliberately **not** `func` (that introduces a lambda *expression*, `func (x) => ...`, and as a type name it would collide with that expression syntax). See 4.3 for details.

### 4.2.1 Short syntax and captures

**Short syntax.** Besides `func (x) => ...` there are `x => expression`, `(a, b) => expression`, `() => expression` and in each case `=> { ... }` with a block. Parameters may carry
types/default values as usual (`(int a, int b) => a + b`); `on obj` exists only for the `func` form.

**Captures.** If the body of a lambda uses names that are **local variables or parameters** in the enclosing code (method parameters, `var` in blocks and
loops, parameters of an enclosing lambda), their **values are copied when the lambda is created**:

```
class T {
    static Run() {
        var limit = 3
        var f = x => x > limit          // limit is copied
        limit = 10
        print(f(5))                     // True - the lambda still sees 3
    }
}
```

- The **value** is copied, not the variable: later changes outside are invisible inside and vice versa (there are no shared, mutable variables - and also
  no loop-variable trap: `for (...) { fs.Add(() => i) }` captures the current value on each iteration).
- An **assignment to a capture** in the lambda is an error ("... is a COPY ..." inside the lambda); a declaration of its own with the same name (`var limit = 100`) hides it.
- **Objects** are copied as a reference (the value is the reference): the lambda sees and modifies the same object. It stays owned by its original owner - if the lambda
  outlives it, the object is destroyed afterwards. A copy of its own is forced with `copy x`/`flat x` in a local variable beforehand.
- **Global** variables are not copied, they stay alive (`g = 7` is visible in the lambda). This also applies to top-level variables.
- `this` is not captured (use `on this` for that). `fire global { }` captures nothing - only `taking` applies there.
- Names that are not a local of the enclosing code (class members, natives, classes) are not captured: they resolve as before.

### 4.3 Lambda types with a signature

(`lambda member<T> name` & co. - a selector that picks a member of an object - are described in 8.13.)

```
class Runner {
    Execute(lambda<int> callback, int x) {
        return callback(x)
    }
}

var r = new Runner()
var doubleIt = func (n) => n * 2      // short form: '=> expression' instead of '=> { return expression }'
print(r.Execute(doubleIt, 21))         // 42

int lambda<int, int> adder = func (a, b) => { return a + b }
print(adder(3, 4))                     // 7

lambda greet = func () => { print("hi") }   // 'lambda' without '<...>' = 0 parameters
greet()
```

`lambda` alone stands for a lambda without parameters; `lambda<P1,...,Pn>` for
one with `n` parameters; an optional return type goes **before** `lambda`
(`int lambda<int>`, not inside the angle brackets) – this makes the
grammar unambiguous without needing a separator between return and
parameter types. Unlike other type annotations (SPEC
8.1: purely syntactic, not enforced at run time), for `lambda` the
**number of parameters is actually checked** – and everywhere a
value is assigned to a `lambda` annotation: in a `var`
declaration with an initializer, and in every function call for a parameter
typed this way (right at the start of the function body, before any
user code runs). If the number of parameters does not match, that is a
run-time error. The individual parameter/return **type names** themselves,
on the other hand, are not checked (only their number) – a lambda does not reliably expose its
parameter types at run time (dynamically typed
language), and a parameter or return type that is itself a
`lambda<...>` with a signature of its own (nested) is not
supported.

## 5. Classes

### 5.1 Declaration & inheritance

- Single inheritance: `class Foo : Bar { ... }`
- `base(...)` in the constructor calls the parent constructor.
- `base.Method(...)` calls an overridden parent method.

### 5.2 Constructor

```
class Foo : Bar {
    construct(int x) : base(x) {
        // ...
    }
}
```

### 5.3 Destructor

```
class Foo {
    destruct() {
        // clean-up work, called during cascade deletion through ownership
    }
}
```

For a derived class the **whole chain** of destructors runs: first that of the
derived class, then that of each base class (as in C#) - a class without its own
`destruct()` therefore still cleans up with that of its base class.

### 5.4 Method overloading

```
class Calculator {
    int Add(int a, int b) {
        return a + b
    }

    int Add(int a, int b, int c) {
        return a + b + c
    }
}
```

Several methods with the same name are allowed as long as they differ in the
**number of parameters** – in a dynamically typed
language that is the only distinguishing feature that is generally reliable at call time
(overloading by parameter *type* could not be checked
consistently, since types are attached to values at run time, not to
variables). Two methods with the same name **and** the same
number of parameters in the same class are an error. A derived
class can add an overload with a DIFFERENT arity without hiding the
inherited overloads of the base class – the call searches across
the whole inheritance chain for the overload that matches the actual
number of arguments.

**Constructors** work the same way – several `construct(...)` with
different numbers of parameters in the same class:

```
class Point {
    int x
    int y

    construct() { this.x = 0; this.y = 0 }
    construct(int x, int y) { this.x = x; this.y = y }
}
```

(unlike methods, however, without a base class chain – `new Derived(...)`
only ever uses Derived's own constructors, never those of the base class.)

### 5.4.1 Optional parameters

```
string Greet(string name, string greeting = "Hello") {
    return greeting + ", " + name
}

Greet("World")             // "Hello, World"
Greet("World", "Howdy")    // "Howdy, World"
```

A parameter can get a **default value** (`= expression`), which is
used if the call supplies fewer arguments. Optional
parameters must be contiguous at the **end** of the parameter list – no
required parameter after an optional one. Applies to methods, constructors
and lambdas alike. A parameter is written like a variable:
`Type name` (the type before the name, as with `int x`), optionally followed by
`= default value`. The earlier notation `name : Type` no longer exists.

```
f(int x = 42) { ... }
```

The default value expression only sees its own context + global +
`this` (like a field initializer) – **not** the other parameters
of the same function, since it is evaluated independently of them (only at the actual
call, if the argument is missing).

### 5.4.2 Passing arguments: reference or copy, `ref`

What a parameter receives depends on the kind of the argument:

- **Objects and arrays (also byte buffers) are passed by reference.** The function works on the very same instance; nothing changes about its owner (2).
  Whoever wants a copy writes `flat x` or `copy x` in front of the argument (2.4).
- **Basic types (`bool`, `int`, `float`, `char`) and strings are copied.** Assigning to the parameter does not change the variable of the caller.
- **`ref`**: a parameter declared with a leading `ref` is passed by reference, whatever its type. The function reads and writes the variable of the caller itself.

```
class Util {
    static Swap(ref a, ref b) { var t = a; a = b; b = t }
    static Add10(ref int x) { x = x + 10 }
}
var p = 1
var q = 2
Util.Swap(p, q)        // p = 2, q = 1 - no marker at the call: the declaration of the parameter decides
Util.Add10(arr[3])     // a variable, a field (`obj.n`, a bare field name in a class) or an array/buffer element
```

- The call needs **no marker**: when the parameter is declared `ref`, the passing is implicitly by reference. The argument has to be a **variable, a
  field or an element of an array or buffer** (`a`, `obj.field`, `a[i]`); a value (`Util.Add10(5)`) is a run-time error in the function ("Parameter 'x'
  is declared 'ref'"). A `ref` argument can be handed on to another `ref` parameter. `copy x`/`flat x` do not combine with `ref`.
- `ref` is possible for the parameters of **methods and constructors** (not for lambdas, `extern` functions and operators), also with a unit
  (`ref int len : mm`, checked at the call and on every assignment), but a `ref` parameter **cannot have a default value**. The first 16 parameters of a call can be `ref`.
- A lambda that is created inside the function captures the **value** of a `ref` parameter (4.2.1), not the reference.
- A call is bound to its method only at run time (by name and number of arguments). The caller therefore passes the reference when **any** method of this name
  and number of parameters declares that position `ref`; a method that declares it without `ref` receives the value (a copy for basic types and strings).
- `ref` is a word only in front of a parameter; as a variable name it stays possible.

### 5.5 Extension classes (`class extends`)

```
class Animal {
    string name
    construct(string name) { this.name = name }
}

class extends Animal {
    int age

    string Describe() {
        return this.name
    }
}
```

`class extends Name { ... }` adds the members it contains (fields,
methods, properties, ...) directly to the **existing** class `Name` –
no new class name, no inheritance, but "reopening" as in Ruby:
the new members end up 1:1 in the original class as if
they had been there from the start. `Name` must be known in the same (when used with the
prelude: combined) program, otherwise it is an error –
an extension cannot create a new class. Several `class extends
Name` blocks for the same name are all merged. This allows
e.g. your own additional methods for `List` from the standard library without having to
touch its source code yourself.

### 5.5.1 Extending base types (`class extends string`)

```
class extends string {
    string Shout() { return this.ToUpper() + "!" }
    bool IsBlank() { return this.Trim().Length == 0 }
}

class extends int {
    bool IsEven() { return this % 2 == 0 }
}

print("hello".Shout())      // HELLO!
int n = 21
print(n.IsEven())           // False
```

The base types `string`, `char`, `int`, `float` and `bool` can also be extended with `class extends`.
Inside the methods `this` is the **value itself** (not an object), otherwise everything is as with
methods (overloading by number of parameters, optional parameters, `private`, exceptions, calling other
extension methods via `this.`). Several blocks for the same type - also from other files or
namespaces - are merged; the extension applies to all values of that type.

**Only methods** are allowed: a base value has no storage in which a field or a property
could live. A field, an (auto-)property, a constructor/destructor, a `static` method
(`string.Foo()` does not exist) and an operator overload are a compile error
("'class extends string': field 'x' is not allowed ..."). `byte` cannot be extended - a `byte`
is an `int` at run time, so use `class extends int`. For **arrays** there is `class extends array { ... }` (an identifier, not a keyword; `this` is the array, the same rules apply: instance methods only) - this gives every
array e.g. the LINQ operators (`#import "linq"`). Buffers cannot be extended.

The prelude methods for `string` and `char` (8.12) are exactly such extensions. A method of your own
with the same name and the same number of parameters as an existing one is - as with any class -
a duplicate definition.

### 5.6 `with` statement

```
with obj {
    .field = 5
    .Method()
    print(.otherField)
}
```

BASIC-style `with`: the `with` expression is evaluated **once**, and
every expression inside the block that starts with `.` implicitly refers
to that result (`.field` instead of `obj.field`) – both for reading
and as an assignment target. Pure syntactic sugar, no run-time
semantics of its own: `with p { .x = 5 }` behaves exactly like
```
{
    var __temp__ = p
    __temp__.x = 5
}
```
(including a block scope of its own for the temporary variable). `with` blocks
may be nested; a `.` always refers to the
**innermost** enclosing `with` block.

### 5.8 Generic classes (`class Name<T>`)

```
class Animal { }
class Dog : Animal { }

class Container<T> where T is of Animal {
    T item
}

var c = new Container<Dog>()   // ok, Dog satisfies "is of Animal"
```

A class can declare type parameters (`class Name<T1, T2>`), with
optional `where` conditions (one `where` clause **per** type parameter,
any order, before the opening `{`):

```
class Precise<T> where T is of float : is in "mm" { }
```

Every `where` clause consists of one or more **condition groups**,
joined with `,` (**OR** – at least one group must be satisfied);
within a group several conditions can be joined with `:`
(**AND** – all must be satisfied). A single condition is either
`is of Name` (Name is a class/an interface, or the base class/
interface chain of the type argument contains Name) or `is in "unit"`
(the type argument, read as a unit name, is dimensionally compatible).

A generic class is instantiated with **explicit** type arguments:
`new Name<Arg1, Arg2>(...)`. The arguments are checked against the `where` conditions at
resolve time – a violation, a
wrong number of arguments or type arguments on a non-generic
class are errors. `T` itself may be used inside the class anywhere
a type name would otherwise stand (fields, parameters,
return types) – without the resolver reporting "unknown type".

Type ARGUMENTS may be NESTED arbitrarily deeply
(`new Container<Box<int>>(...)`, `new Container<Box<Box<int>>>(...)`, ...)
- fully supported syntactically. However, since (see next paragraph) there is
NO real generic specialization, for the
constraint check (`where`) on a nested type argument only
its OUTER name counts (`Box`, not `Box<int>`) – the inner arguments
are read purely syntactically and then discarded.

**Same name as a non-generic class**: A generic class
may bear the same name as a non-generic one (as in C#):

```
class Box { }                 // non-generic
class Box<T> { T item }       // generic, a different type

var a = new Box()             // the non-generic one
var b = new Box<int>()        // the generic one
```

The **number of type arguments** in `new` selects the class. Every place
WITHOUT type arguments (type annotation, `is of Box`, `catch (Box e)`,
`Box.Static`, base class, `class extends Box`) means the non-generic
class - for the generic one there is (only) the form `new Box<...>(...)`.
Internally the generic class is then called `Box`1` (name + '`' + number of
type parameters, see `Ast.GenericClassNames`), which is only visible in error messages
and stack output. Only "generic NEXT TO non-generic" is allowed;
two generic classes of the same name remain a duplicate definition (even
with a different number of type parameters).

**Important limitation**: Since this language is dynamically typed,
**no real type substitution** takes place (unlike C#, where the
compiler generates specialized code per instantiation) – the check
of the type arguments against the `where` conditions happens **once at
`new Name<...>(...)`**, afterwards `T` behaves in the body of the class like an
ordinary placeholder type name that is not checked any further. Generic
**methods** (`RetType Name<T>(params) where T ... { }`) are parsed
and the type parameters accepted, but **not** checked against explicit
type arguments at the call site – `Name<T>(x)` as call syntax would be
ambiguous with `<`/`>` as comparison operators at normal expression positions
(unlike `new Name<...>`, where an argument list must
follow directly after the class name, which makes it unambiguous). A
generic method is therefore called quite normally without `<...>`.

### 5.9 `switch` statement (with comparison operators)

```
switch (x) {
    case <= 1:
        print("small")
        break
    case 2:
        print("two")
        break
    case default:
        print("large")
}
```

Each `case` consists of an optional comparison operator (`<`, `<=`,
`>`, `>=`, `==`, `!=`) followed by a value – if the operator is missing, `==` is assumed
(`case 2:` therefore means `x == 2`). The switch expression
is evaluated **once** and compared again at every `case` with the
given condition; unlike in C-style switches there is
**no fallthrough** – every branch is exclusive, at most one runs.
`case default:` catches everything that does not satisfy any of the previous conditions
(optional, preferably – but not necessarily – as the last branch).
`break` ends the current branch; since there is no fallthrough anyway,
it is a purely syntactic termination (no jump) and therefore **optional**
– a branch ends just the same at the next `case` or at the `}`. If `break` is
used, it must be the last statement of the branch. `switch (x) {
case OP value: ... }` behaves exactly like an if/else-if chain:
```
{
    var __temp__ = x
    if (__temp__ <= 1) { print("small") }
    else if (__temp__ == 2) { print("two") }
    else { print("large") }
}
```

Inside a `switch`, `break` is valid **exclusively** as a branch termination
(see above) – that is something different from the general
loop `break` of 5.10, even though both use the same keyword.
If a `switch` is inside a loop, a `break`
directly in one of its `case` branches therefore ends **the switch branch, not the
loop** (as in most C-style languages) – for the loop
itself the `break` would have to be inside a loop construct of its own nested in the `case` branch.

### 5.10 `break`/`continue` in loops

```
var i = 0
while (i < 10) {
    i = i + 1
    if (i == 3) { continue }
    if (i == 6) { break }
    print(i)
}
```

`break` ends the **innermost** enclosing loop (`while`/`for`/
`foreach`) immediately; `continue` skips the rest of the current
iteration and jumps to the next condition check – for `for`
**after** the increment step has still been executed (otherwise
`continue` would never advance the loop variable). Both may stand arbitrarily
deep in nested blocks/`if`s within the loop body;
all scopes in between are closed cleanly in the process.

**Restrictions**:
- `break`/`continue` are only valid inside a loop – outside
  that is an error.
- A nested lambda does not "see" the loop of an enclosing
  function – `break`/`continue` inside a lambda are only
  valid if the lambda ITSELF encloses a loop.
- `break`/`continue` may be used to leave the `try` and `catch` blocks: the
  exception handler is deregistered and an existing `finally` runs before the jump (for several
  nested `try`s from the inside out). Only out of the `finally` block itself they are an
  error (a loop INSIDE the `finally` may of course use `break`/`continue`).

### 5.11 Operator overloading

```
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
print(a + b)     // Vector2(4, 6)
print(a == b)    // false
```

A class can overload any of the arithmetic/bitwise/comparison operators
(`+ - * / % ^ & | # << >> == != < <= > >=`) as well as the index operator `[]`:
`operator SYMBOL(params) { body }`. Internally purely syntactic
sugar – every overload becomes an ordinary method with a
special name that script code itself cannot write as an ordinary identifier
(`"operator+"`, `"operator=="`, ...) – inheritance,
overloading by number of parameters and everything else in the existing
method infrastructure therefore works automatically.

- **All operators except `[]`** expect exactly **one** parameter (the
  right operand – `this` is implicitly the left one). Only the **left**
  operand is checked for a matching overload at run time (no
  equivalent to C#'s overloading for swapped operand types or
  Python's `__radd__`) – if it is not an object, or an object without a matching
  method, the built-in default operation applies (for `==`/`!=` e.g.
  reference equality).
- **`[]` (index operator)** is a special case: `operator[](int index) { ... }`
  (one parameter) overloads **read** access (`arr[i]`),
  `operator[](int index, class value) { ... }` (two parameters) overloads **write** access
  (`arr[i] = value`) – distinguished purely by the number of
  parameters, like any other method overload in this language.
  `operator[]` is merely parser sugar for a naming convention that
  has existed for some time: internally ordinary methods
  named `GetIndex`/`SetIndex` are created, which index access (`OpCode.ArrayGet`/
  `ArraySet`) had already been looking up by naming convention if the target is
  not a real array but an object – writing `operator[](...)` and
  `GetIndex(...)`/`SetIndex(...)` by hand are therefore
  equivalent, the former merely being the more explicit, recommended notation.

## 6. Test operators: `is in`, `is of`, `is from`

- **`value is in unit`** → `bool`. Checks whether `value` (int/float/undefined) carries a unit that is dimensionally compatible with `unit` (see 3.4), regardless of prefix/scaling factor. Example: `5mm is in m` → `true`, `5mm is in kg` → `false`.
- **`value is of Type`** → `bool`. Checks type membership **recursively**: for base types a simple kind comparison; for `class` instances the inheritance chain is searched upwards (the instance itself or one of its parent classes matches `Type`). Example: `a is of float`. An **interface** is also allowed as a type (`value is of IEnumerable`): true if the class (or a base class) names it in `class X : IFoo`; **arrays and buffers** satisfy `IEnumerable`.
- **`object is from ownerExpression`** → `bool`. Checks whether the current owner of `object` is exactly `ownerExpression` (direct owner comparison). Example: `obj is from objList`.
- **`object is under ownerExpression`** → `bool`. Like `is from`, but **transitive**: checks whether `ownerExpression` is anywhere in the ownership chain above `object` (direct owner, its owner, and so on, arbitrarily deep).

## 7. Exceptions

### 7.1 Base class

There is a built-in base class `Exception` (at least with a `message` property) from which all exception classes inherit:

```
class Exception {
    string message

    construct(string message) {
        this.message = message
    }
}

class InvalidUnitException : Exception {
    construct(string message) : base(message) { }
}
```

`throw` expects a value that derives (directly or indirectly) from `Exception` (checked like `is of`); otherwise a run-time error already at the `throw` itself.

### 7.2 Throwing

```
throw expression;
```

### 7.3 Catching

```
try {
    // ...
} catch (ErrorType e) {
    // only if the thrown value is an instance of ErrorType or a subclass
} catch (e) {
    // untyped catch-all, catches everything else
} finally {
    // always executed, also for uncaught exceptions before they propagate further
}
```

- Several `catch` blocks are checked in order; a typed `catch (Type name)` filters via an `is of` check, an untyped `catch (name)` catches everything. The type stands - as with every declaration (`int x`) - BEFORE the name.
- `finally` is optional and runs **always**, on every path that leaves the `try`: normally, after a `catch`, on an exception that passes this `try` (also out of a `catch` block), on `return`
  (also in the `try`/`catch`/in a `foreach` inside it), on `break`/`continue` and on `leave`/`terminate`. The block sees the local variables of the function. A `return` in the `finally` replaces the return value, a `throw`
  in it replaces the original exception; `break`/`continue` out of the `finally` are an error. The return value of a `return` in the `try` is fixed before the `finally` runs (if it changes the variable, the return value stays).

### 7.4 `catch` without `try` – implicit block-scope catch

`catch` can also stand **without a preceding `try`**. It then acts like an implicit `try` block that starts at the position of the `catch` and extends to the end of the current enclosing block – all errors thrown afterwards in the same block are handled by this `catch`:

```
{
    riskyStepOne()

    catch (e) {
        log(e.message)
    }

    riskyStepTwo()   // errors here are caught by the catch above
    riskyStepThree()
} // the protected area ends here with the end of the block
```

### 7.5 Default behavior & resume

Exceptions are perfectly normal `class` instances (subclasses of `Exception`) – resume is not a special function of a special type, but a built-in method (`resume()`, lower case) that every `Exception` instance brings along. Since exceptions are normal objects, they can be passed on like any other value, stored in variables or handed to other functions before `resume()` is called.

- `throw expression` can be used both as a standalone **statement** (`throw new Foo();`) and as an **expression** inside a larger expression. A **resume point** is created at the position where it is thrown.
- **Default:** Without an active `catch` the program aborts with an error message.
- **With resume:** If a `catch` handler calls `e.resume(replacementValue)`, execution continues *exactly at the resume point* – the `throw` expression evaluates to `replacementValue` at that position, the surrounding code carries on normally as if there had never been an exception. For `throw` as a pure statement `replacementValue` is simply ignored and execution continues with the next statement.
- `e.resume()` without an argument is equivalent to `e.resume(undefined)`.
- Since the exception instance "lives" until the resume (its owner – typically the throwing scope – remains as long as the stack is open at that position), `resume()` can also be called from more deeply nested code to which the exception instance was passed on.

### 7.6 Interplay with ownership

During stack unwinding through a thrown exception, all scopes that are left are dissolved regularly as when a block is left normally: objects whose owner is one of these scopes are deleted in a cascade (including `destruct()` calls) before the exception propagates further or is handled in the `catch`. With `resume()` the unwinding is correspondingly omitted, since execution continues at the original position.

## 8. APIs, bit widths, pointers/unsafe, arrays, enumerables

### 8.1 Declaring native functions (`extern`)

```
extern int MessageBoxW(int hwnd, string text, string caption, int type)
extern PlaySound(string path)   // no return type -> effectively returns 'undefined'
```

`extern` declares only the **signature** – no body. The name thereby becomes
known as callable. There are two independent ways in which an actual
call then reaches a real native implementation:

1. **Manual host registration**: an embedding host registers the
   name at run time via `Bytecode.ExternRegistry` with an implementation of its own
   in C# (signature in the script, implementation in C#).
2. **Dynamic linking against a native library** via the
   `#extern "libName"` directive (section 8.1.1) – entirely without host code.

Without either of the two the name is known/declared callable, but an
actual call fails at run time with a clear "neither linked
nor dynamically loaded" error – compiling itself NEVER fails
because of it (declaration and linking are deliberately decoupled, as with
a real "extern" in compiled languages).

#### 8.1.1 `#extern "libName"` – dynamic linking

```
#extern "kernel32.dll"
extern int GetTickCount()

#extern "user32.dll"
extern int MessageBoxW(int hwnd, string text, string caption, int type)

print(GetTickCount())
MessageBoxW(0, "Hello!", "Title", 0)
```

`#extern "libName"` is a preprocessor directive (not an expression, not a
statement with a run-time effect) – it determines against which native
library **all following** `extern` declarations in the source text
are dynamically bound, until another `#extern` directive changes that.
On the actual call the runtime loads the library (if that has not
happened yet), looks up the export under exactly the declared name and
calls it with the passed arguments – entirely without host-side
C# code. Supported parameter/return types: `bool`/`int`/`float`/
`char`/`string` as well as pointer types (as a raw native address). A
manual host registration (see above) always takes precedence over dynamic
linking if both exist for the same name.

The actual **mirroring** of individual operating system APIs (prefabricated
`extern` declarations for the complete WinAPI/POSIX or the like) is deliberately
NOT part of the language itself, but the task of a later
library framework that builds on `extern`/`#extern`.

### 8.1.2 `#include "fileName"`

```
#include "shapes.script"

var c = new Circle(2.0)
```

Pure **textual preprocessing** before the actual parsing (like a
classic C preprocessor): `#include "fileName"` is replaced by the (recursively
also preprocessed) content of the referenced file, relative
to the directory of the including file. Unlike in C the same
file is inserted only **once** in total (later `#include`s of the same,
already inserted file are silently skipped – like
`#pragma once`) – this prevents duplicate class/`extern` declarations
when two files include the same third file. Circular includes
are detected and abort with a clear error.

**Markup of a user interface**: a file ending in `.fxml` is not read as text but translated first - `#include "Form.fxml"` inserts the fire script generated from it (docs/UI_MARKUP.md): the base
class of the code-behind with the elements, handlers and data bindings of the interface. Mistakes in the markup abort with the line of the markup file.

**Global composition instead of separate insertion trees**: "only
once in total" has applied since the directive stage (see 8.1.5) across the
ENTIRE compilation, not only within a single root
file. `Parser.ParseWithPrelude` (prelude + user script together) therefore shares ONE common "already inserted" set between the two halves
(`Preprocessor.Process(..., alreadyIncluded)`, the same `HashSet<string>`
instance for both calls) - if both the prelude and the
user script (directly or transitively via their own includes) include the same file,
it still ends up only ONCE in the combined program, instead of
once per side. `#include` itself is only the built-in
default registration of the generic directive mechanism (see 8.1.5),
no longer a special case in the rest of the preprocessor.

### 8.1.3 Timeout-capable native APIs (`try Name(...)`)

```csharp
// Host-side C# registration:
natives.RegisterTryable("TryReadSensor", (Value[] args, out Value result) =>
{
    // The actual timeout/error logic lies entirely here, in the host -
    // the language itself knows nothing about it. args[0].Unit is already
    // publicly readable if a timeout argument with a unit was passed
    // (e.g. "500ms").
    if (/* timeout or the like */ false) { result = default; return false; }
    result = Value.MakeInt(42);
    return true;
});
```
```
var value = try TryReadSensor(500ms)
if (value == undefined) { print("Timeout or failure") }
```

A native function registered as "tryable" (`Bytecode.NativeRegistry.
RegisterTryable`, delegate type `TryableNativeFunction`) can ONLY be called via
`try Name(...)` - a direct call `Name(...)` without `try` is
a compile error. On success the expression returns the result value, on
failure/timeout `undefined` - **no exception**: the actual
success/failure decision (timeout, unavailable hardware, or the like)
is made exclusively by the host implementation (typically IO:
serial ports, network, files), via the `bool` return value
of its C# delegate (`TryableNativeFunction`, the same basic pattern as C#'s
own `TryParse` - success/failure via `bool`, result via `out`).

`try Name(...)` is syntactically related to `try sync`/`try process`
(SPEC 3 - all three are expressions, not a try/catch block), but
independent: `try sync`/`try process` return a pure `bool`
(success of the synchronization itself), whereas `try Name(...)` returns the
ACTUAL result value of the native function on success. The parser
distinguishes a try/catch block from all three expression forms purely
by whether a `{` follows directly after `try` (block) or not (expression).

### 8.1.4 Native callbacks

**Where a callback runs.** `FireRuntime.RunCallback(lambda, args, natives, classes, snapshotGlobals, onUnhandled)` decides:

- **On the thread of a running VM** (the normal case: the script itself calls e.g. `Window.Tick`, and the events fire during it) the lambda runs **nested on this VM**
  (`VM.CallLambdaInline`): with the **real global variables**, reading and writing, like any other lambda (4.2) - there is nothing to isolate because nothing is concurrent. Objects with
  lambda fields or references to foreign objects are no problem as globals, and `leave`/`terminate` in the callback act on the program. An unhandled exception in the callback only aborts the
  callback (a `try`/`catch` around the triggering call does not see it): it goes to `onUnhandled` as text, the program keeps running. As with destructors and properties, during a
  nested callback `leave`/`terminate` of other threads is not checked (only afterwards).
- **On a thread without a running VM** (a host thread, e.g. a serial event) access to the globals would be a data race: the callback is therefore **queued** to the main program
  (`VM.PostCallback`, via `RunCallback(..., owner)`) and executed there - automatically at a safe point or at `sync globals`, with `#nosync` only then - with the real globals. If the
  main program is not (or no longer) running, the isolated copy applies as described below (`FireRuntime.CallCallback`).

The rest of this section describes this isolated case.

```csharp
// Host-side C# registration (RegisterCallback is an ORDINARY
// native function - lambdas are already first-class values, no
// special syntax needed):
var callbacks = new Dictionary<string, LambdaValue>();
natives.Register("RegisterCallback", args =>
{
    callbacks[args[0].AsString()] = (LambdaValue)args[1].AsLambda();
    return Value.MakeUndefined();
});

// When the native event fires later (possibly on ANOTHER thread):
var snapshot = vm.SnapshotGlobals();   // SYNCHRONOUS, while the script is not running
FireRuntime.CallCallback(callbacks["OnTick"], args, natives, classes, snapshot);
```
```
var counter = 0
RegisterCallback("OnTick", func(int n) => {
    counter = counter + n   // only changes the SNAPSHOT copy, not the original
})
```

A callback lambda is passed like any other lambda as an argument to an
(ordinary) native function - `RegisterCallback` itself is
nothing special, just a normal, host-registered function that keeps the
passed `LambdaValue` somewhere (e.g. a `Dictionary`) for later.
The actual novelty lies on the C# side:

- **`VM.SnapshotGlobals()`**: takes a snapshot of all current values
  of the global scope of this VM - the same basic idea as the internal
  snapshot before a `fire` block, only triggered from OUTSIDE (host code) instead of
  by a script opcode. MUST be called synchronously while
  this VM instance is not running on another thread at the same time (no
  built-in locking) - for the usual case (registration directly following
  the native call, while the script is in exactly this
  call) that is automatically the case.
- **`FireRuntime.CallCallback(lambda, args, natives, classes, snapshot)`**:
  calls the lambda SYNCHRONOUSLY on the calling (native) thread on a
  FRESH VM instance of its own (`VM.CallLambdaEntry`) - unlike
  `Fire`/`FireVm`, deliberately NO new thread is started here: the native
  host code has already determined call time and thread itself (e.g.
  a .NET thread-pool thread for a serial port event), this method
  just joins in. The new global scope is created entirely from the
  `snapshot` (objects in it as an isolated deep copy, as with `taking`) -
  **never** the real, shared global scope of the main program, exactly
  as required. An unhandled script exception in the callback body
  does NOT propagate out into the native caller, but only goes
  to an optional `onUnhandled` callback.

This matches the existing lambda semantics exactly (SPEC 4.2,
`Runtime.LambdaValue`): a lambda anyway ALWAYS sees only its own
(newly created on every call) scope plus the global scope, never
surrounding locals of any kind - "the global scope" is here simply the
snapshot instead of the original, a callback needs no more restriction
in addition. The snapshot time is deliberately at the registration
(or whenever the host calls `SnapshotGlobals()`) - no
automatic, continuous updating.

### 8.1.5 Freely definable preprocessor directives

```
#greeting "World", 42
```
```csharp
// Host-side C# registration:
var registry = new DirectiveRegistry();  // or DirectiveRegistry.CreateDefault() for '#include' on top
registry.Register("greeting", 2, (ctx, args, line) =>
{
    Console.WriteLine($"Hello {args[0].AsString()}, the answer is {args[1].AsInt()}");
    return null; // no replacement text output for this line
});
string preprocessed = Preprocessor.Process(source, basePath, registry);
```

A preprocessor directive has the form `#name value1, value2, ...` - the
NAME decides which handler (registered by host C# code via `DirectiveRegistry.
Register(name, paramCount, handler)`) is called.
`paramCount` fixes the EXPECTED number of parameters - a different
number in an actual call is a clear compile error. Every
parameter is read like a normal, literal argument list (tokenized by the
real lexer): string, integer/floating-point number (optionally with a
unit suffix, e.g. `74mm`), character, `true`/`false`, `undefined`,
optionally with a leading `-`. Deliberately NO full expression grammar (no
variables, no operators except the sign) - at the time of
preprocessing, BEFORE the actual lexing/parsing of the rest of the program,
there are no variables yet that could be referenced.

A handler receives the parsed values (as `Values.Value`, the same
run-time value representation as everywhere else in this language) as well as
a `DirectiveContext` (current base directory for relative paths,
a method `ProcessFile(fullPath)` for recursively pulling in
another file) and returns the text that replaces the directive line in the
preprocessed output - empty/`null` means "insert nothing".

`#include` (see 8.1.2) is the only BUILT-IN directive
(`DirectiveRegistry.CreateDefault()`) - technically nothing special any more,
just a registration like any other, with 1 parameter (the path as a
string) and a handler that reads the target file and recursively
preprocesses it. A `#name ...` line whose name is NOT registered
(e.g. `#extern "libName"`, `#noshadow` - both remain PARSER directives,
see SPEC 8.1/THREADING_DESIGN, because they have a STICKY meaning that acts across several following
statements, not a pure "replace
this one line" semantics like `#include`) is passed through unchanged -
the preprocessor only interferes with directives it actually
knows.

### 8.1.6 Grouped native registration (`NativeRegistry.RegisterGroup`)

```csharp
var names = registry.RegisterGroup("__GRPH", new Dictionary<string, NativeFunction>
{
    ["set"] = args => { /* ... */ return Value.MakeUndefined(); },
    ["get"] = args => { /* ... */ },
});
// names == ["__GRPHset", "__GRPHget"]
```

Registers several related native functions at once, all
under the same name prefix - the name actually registered for each
entry is `prefix + suffix`. A pure convenience/naming scheme for
self-contained API groups (e.g. a graphics/console bridge, see
`ScriptLang.Terminal.Bridge`, docs/CONSOLE.md) - equivalent to several
individual `Register(...)` calls, and additionally returns the list of the
names created.

### 8.1.7 Conditional compilation (`#if`)

```
#if windows
    var port = "serial:COM3"
#elif esp32 && native
    var port = "uart:1"
#else
    var port = "serial:/dev/ttyUSB0"
#endif

#ifdef DEBUGGING
print("starting")
#endif
```

A preprocessor feature (text, before the lexer, like `#include`): the lines of a branch that is **not taken** are replaced by empty lines - they are
never lexed, parsed or checked, so they may use libraries, classes or syntax that do not exist on the target (`#import "graphics"` in a branch for
`windows`), and the line numbers of everything else stay the same.

| Directive | Meaning |
|---|---|
| `#if expr`, `#elif expr`, `#else`, `#endif` | choose a branch; `#if` can be nested; every `#if` is closed in the file it starts in |
| `#ifdef NAME`, `#ifndef NAME` | the same for one symbol |
| `#define NAME`, `#undef NAME` | add or remove a symbol (from here on, also for the files processed after this one) |
| `#error text` | in a branch that is taken: stops the compilation with `text` |

`expr`: symbol names, `true`, `false`, `!`, `&&` (binds tighter), `||` and parentheses. Names are case-insensitive; a name that is not defined is false.

**Symbols** come from the build:

* the target (`TargetProfile.Symbols`): `windows`, `linux`, `macos` (and `posix` for the last two), `esp32` and `freertos` for the board, the symbols a target of
  `fire.native.json` lists under `symbols`;
* the engine: `vm` or `native`;
* `float32` when `float` has 32 bits (`-f 32` or the default of the target; `#floatwidth` is read after the symbols are needed);
* `-D NAME` on the command line (repeatable, also `--define NAME`, `-DNAME`).

The VM that runs a script in the editor uses the machine it runs on (`windows`, `linux` or `macos`, `vm`), so the same script does the same thing in the editor and
as a native program for the same system. The live diagnostics of the editor do the same; an `#import` in a branch that is not taken does not import. The editor greys out the lines of the branches that are not taken (the symbols follow the configuration of the script: with `"engine": "native"` in the nearest `fire.native.json` its target, engine `native` and its defines, otherwise the machine and `vm`); they get no syntax colours.

### 8.2 Bit widths for `int`/`float`

```
int[8] a        // 8 bits
int[16] b       // 16 bits
int c           // default: highest precision (64 bits)
float[32] Compute(int[16] x) { ... }   // also for parameters/return types
```

Syntax: `[bit width]` directly after the base type (`int`/`float`), allowed
are `8`, `16`, `32`, `64`. Without it the highest precision applies (64 bits,
i.e. int64 or double). This bracket deliberately stands directly after the *type*
– in contrast to array brackets, which stand after the *identifier* (see
8.4), so there is no ambiguity. An **array return type** (8.4.1)
has no identifier after which the brackets could go: there *empty*
brackets stand after the type (`int[]`) - a bit width always has a number
(`int[8]`), which tells the two apart.

**Copy behavior:** If a value is copied into a variable/parameter with a
lower declared bit width, it is truncated (`Value.TruncateTo`):
for `int` classic two's-complement truncation (cast chain via
sbyte/short/int), for `float` correspondingly via float (32 bits) or
IEEE754 binary16 (16 bits, `System.Half`) or a documented,
simple 8-bit minifloat (no widespread standard format exists, see
the comment at `NumericWidth`). The mechanism (`Value.Width` +
`Value.TruncateTo`) is implemented; the *automatic* application on every
assignment (the resolver would have to keep track of the declared type of every slot
for that) is not wired up yet – next stage.

#### 8.2.1 Program-wide float precision (`#floatwidth`)

```
#floatwidth 32      // every `float` without an explicit bit width is a 32-bit float (default: 64)
```

Without a directive `float` is a `double` (64 bits). `#floatwidth 32` makes it a 32-bit IEEE float for the whole program, which is
what small targets with a single-precision FPU (ESP32) want. It is meant to be **the same program with the same results** in
every engine: the VM rounds every float result (and every float constant, and every int that is converted to float) to the
nearest 32-bit float, which is exactly what a float32 CPU computes for `+ - * /` and `%`; `print` shows the shortest text that reads back
as the same 32-bit float (`0.1 + 0.2` is `0.3`, `1.0 / 3` is `0.33333334`). The native backend (see `NATIVE_BACKEND.md`) then computes
with `float` instead of `double`.

The build can override the directive: `fire.Compiler run|build|native ... -f 32|64` (and `floatWidth:` / `floatWidthOverride:` for
hosts that call `RuntimeSession.Build` / `Linker.CompileAndLink`). Any other value is an error. The precision is process-wide while a
program runs (`Value.SingleFloats`); explicit widths (`float[16]`, `float[64]`) keep their meaning.

### 8.3 Pointers & `unsafe`

```
unsafe {
    int[32]* p = &x
    var y = *p
    p = p + 1        // pointer arithmetic
}
```

- `Type*` (one or more `*`) is a pointer type, C#-style directly after
  the (possibly bit-width-qualified) base type.
- `&expression` (address-of) and `*expression` (dereference) are prefix
  operators, **valid only inside an `unsafe { }` block** (checked by the
  resolver) – as in C#.
- For APIs, pointers point to the *underlying values*, not to the
  objects with unit/width metadata – this conversion only happens at the
  actual API call (marshalling), not already at `&x` itself.

**Implemented** (see the section in `docs/BYTECODE.md` for details): a
pointer points to an *existing, managed storage location* – a
scope slot (local/global variable) or an object field – instead of to a
raw memory address. This gives real aliasing (`*p = x` actually changes
the variable `p` points to) without rebuilding a byte
memory model of its own. `&` is thereby only applicable to *addressable*
expressions (variables, object fields) – like lvalues in C#,
not to arbitrary intermediate values. "Pointer arithmetic" (`ptr + n`, `ptr - n`) means
accordingly "n elements further" instead of "n bytes further", and `ptr1 - ptr2` is the number of elements
between two pointers into the same array, buffer or variable. A pointer **to a variable or a field behaves like a pointer to an
array with exactly one element** (as in C): `p + n` is always allowed (also past the end and back: `p + 1 - 1`), but dereferencing it
only works at offset 0 - otherwise (and for an element beyond the bounds of its array or buffer, or of a destroyed one) the
dereference throws a catchable `IndexOutOfBoundsException` (`DestroyedException`); with `#performance` nothing is checked
(natively: reading or writing outside of the memory). Pointers into arrays and buffers (`ref a[i]` arguments) carry their array, so
the bounds are exact in the VM and in the native backend alike. The conversion into
real native addresses for actual `extern` calls (marshalling into
a pinned buffer) is deliberately deferred until `extern` linking
itself is due.

### 8.4 Arrays

```
int arr[]                // array of unspecified size
int fixedArr[10]          // array of fixed size
int matrix[][]            // multidimensional (array of arrays)
var a = new int[10]       // array allocation
```

The array brackets stand **after the identifier**, not after the type
(deliberately different from C#, where `int[] arr` is usual) – this avoids a collision
with the bit width bracket, which stands after the type. Several `[...]` groups
in a row make a multidimensional array (rank = number of groups).

`new Type[sizeExpr]` allocates an array. The element type here is deliberately only
a base name (without a bit width bracket of its own, for the same
collision reason) – a specific element width is instead set via the
declared variable type (`int[16] a = new int[10]`).

**Implemented**: run-time representation (`Values.ScriptArray`, a fixed-size
allocated `Value[]`, elements initialized to `undefined`), index access
reading/writing (`arr[i]`/`arr[i] = value`, opcodes `ArrayGet`/`ArraySet`),
array literals (`[1, 2, 3]`, opcode `MakeArrayLiteral`), bounds checking
(an index outside `0..Length-1` yields a catchable
`IndexOutOfBoundsException`, see VM.ThrowIndexOutOfBounds - internally via
`ScriptArray.TryGet`/`TrySet`, without a C# exception in the hot path, see
docs/BYTECODE.md sections 18/19).
Multidimensional arrays are deliberately NOT a multidimensional
run-time representation of their own, but nested `ScriptArray` instances
("jagged arrays", as in Java/C#).

**Array initializers**: a `var` declaration may carry array brackets AND
an array literal initializer at the same time:

```
var a[] = [1, 2, 3, 4]        // size implicitly from the literal (4)
var b[] : int = [1, 2, 3, 4]  // with a type annotation
var c[4] = [1, 2, 3, 4]       // explicit size, MUST match the literal size
```

If an explicit size is given AND it is itself an integer literal
(the common case), the resolver checks that it matches the number of
literal elements – a mismatch is a compile error instead of
a silently differently sized array. A dynamic size (variable/
expression instead of a literal) is not checked.

For `List` (see the prelude) there is the same idea via a second
constructor overload: `new List([1, 2, 3, 4])` copies the array elements
one by one via `Add()` into a new list.

### 8.4.1 Arrays as a return type

```
class Kennel {
    int[] Numbers() { return [1, 2, 3] }
    Dog[] Dogs() { ... }
    string[][] Grid() { ... }        // several bracket pairs: array of arrays
    byte[] Bytes() { return "AB".ToBytes() }
    int[8][] Small() { ... }         // array of 8-bit integers (bit width + empty brackets)
}
interface IHolder { int[] Items() }
```

A method, an interface entry or a property may return an array: `Type[] Name(...)`, with **empty**
brackets after the type. Only there - wherever an identifier exists, the brackets stay after it
(`int values[]`); `int[] values` as a field, parameter or variable is an error with exactly this hint, and so is
an array return type on `extern` (the native interface knows no script arrays). Like every return type it
is not enforced at run time, the resolver only checks that the type name exists; the editor uses it for
type inference (`k.Dogs()[0].` suggests the members of `Dog`, `k.Numbers().` suggests `Length`).

### 8.5 `IEnumerable`/`IEnumerator` & `interface`

**Implemented.** Since `IEnumerable`/`IEnumerator` must be implementable by several otherwise
independent classes (e.g. `List` should be
able to be enumerable, while other classes have entirely different base classes),
the existing single inheritance is not sufficient. For this
there is the lean **`interface` construct** in addition to `class`:

```
interface IEnumerator {
    bool MoveNext()
    class GetCurrent()
}

interface IEnumerable {
    IEnumerator GetEnumerator()
}

class List : IEnumerable {
    // ...
    IEnumerator GetEnumerator() { ... }
}
```

- A class still has at most **one** base class (`class Foo : Bar`),
  but can additionally implement **several** interfaces
  (`class Foo : Bar, IEnumerable, IComparable`) – the inheritance model remains
  single, only interfaces can be implemented multiple times.
- An `interface` only declares method signatures (no fields, no
  constructor), similar to `extern`, just for class-internal contracts instead of
  native functions.
- An `interface` may be **generic** (`interface ICommand<T> { Execute(T context) }`) and stand next to a non-generic one of the same name (as with classes,
  see 5.8); a class names it with type arguments (`class Command<T> : ICommand<T>`). As everywhere, only the name and the NUMBER of type arguments count.
  Likewise a **base class** may carry type arguments: `class Home : Command<IDevice>` inherits from the generic class `Command<T>`. An interface is also allowed as the **type of a
  parameter** (`Use(IShape s)`).
- **`Command`, `Command<T>`, `ICommand`, `ICommand<T>`** (part of the prelude): a command as an object. `Command` has the lambda field `Command` and `Execute()`, which
  calls it; `Command<T>` has a `lambda<T> Command` and `Execute(T context)`. You assign the lambda (`c.Command = d => { ... }`), pass it to the constructor
  (`new Command<IDevice>(d => ...)`) or derive and override `Execute` (`class Home : Command<IDevice> { Execute(IDevice context) { ... } }`). Without a lambda `Execute` does nothing
  (returns `undefined`), otherwise it returns its result. `Device.DoCommand`/`DoCommands` (8.16) execute such commands with the device as the context.
  (A class of your own named `Command` collides with the prelude class.)
- **Arrays and byte buffers are `IEnumerable`:** `arr.GetEnumerator()` returns an enumerator (`ListEnumerator`), `arr is of IEnumerable` is true, and everything that processes an `IEnumerable` (`foreach`, `Linq.From`, your own methods) accepts them.
- `foreach (x in collection)` (SPEC 5) runs via `GetEnumerator()`/
  `MoveNext()`/`GetCurrent()` – purely by NAME dispatch, so it also works
  on any other class with the same three methods, not only on
  `IEnumerable` instances in the formal sense. An **array** (`[1, 2, 3]`,
  `new string[3]`) and a **byte buffer** can also be iterated directly
  (`foreach (x in arr)`) - the VM supplies a `ListEnumerator` of the
  prelude for that (without the prelude it remains an error).
- `IEnumerable`/`IEnumerator`/`List`/`ListEnumerator` as well as
  `IndexOutOfBoundsException` are part of the **prelude**
  (`Standard/Prelude.cs`) – deliberately written in ScriptLang itself instead of
  natively in C#, since the language has long had enough substance for it (classes,
  arrays, interfaces), and it is automatically prepended to every program
  (`Parser.ParseWithPrelude`). `List` internally uses a fixed-size array
  that is doubled when needed (a classic dynamic array); an
  invalid index throws `IndexOutOfBoundsException` (constructed by the VM itself,
  see `VM.ThrowIndexOutOfBounds`), which can be caught quite normally with `try`/
  `catch`.

### 8.6 `readonly` (constants)

```
readonly var PI = 3.14159
readonly int MAX_SIZE = 100

class Circle {
    readonly float radius

    construct(float radius) {
        this.radius = radius
    }
}
```

`readonly` is an optional prefix before any variable or field
declaration. A `readonly` variable (local or global) MUST have an
initializer (or an array size, `readonly int arr[10]`) and can
never be assigned again afterwards – every later `=` assignment is a
compile error. A `readonly` field can only be assigned inside a constructor
**of the declaring class itself** via `this.field = ...` (not
in other methods of the same class, not in derived classes, not
from outside) – this allows fields to be expressed that are set once per instance and are then
immutable (like C#'s `readonly`).

**Known limit**: the check is purely static and only refers to
the lexically recognizable case `this.field = ...`. An assignment via a
dynamic target (`obj.field = ...`, where `obj` is an arbitrary expression)
is NOT detected, since the language has no static type system that would know at
compile time which class `obj` is an instance of – a real
run-time safeguard for that is a possible later stage.

### 8.7 `enum`

```
enum Color { Red, Green, Blue }
enum Status {
    Active = 10,
    Inactive,   // 11 - auto-increment from the predecessor
    Paused = 20,
    Done        // 21
}

print(Color.Red)     // 0
print(Status.Paused)  // 20
```

An `enum` is a pure compile-time constant list – the compiler resolves `Name.Member`
directly to an int literal, there is **no run-time representation of its own** (no value type of its own, no instances). Without an
explicit value a member gets the value of its predecessor + 1 (0 for the
first member) – classic C-style auto-increment. An explicit value
must be an int literal (no arbitrary expressions, since this language has
no general compile-time constant evaluation).

Since an enum value is simply an `int`, it also behaves like one:
`Color.Red is of int` is true, comparisons/arithmetic work normally,
but there is no separate `is of Color` check (no type `Color` at
run time) and no automatic name representation on output
(`print(Color.Red)` shows `0`, not `"Red"`).

### 8.8 Properties

```
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
        get { return this.radius * this.radius * 3 }   // no 'set' -> read-only
    }
}

var c = new Circle(5.0)
print(c.Diameter)      // 10 - calls the getter
c.Diameter = 20.0       // calls the setter, 'value' is implicitly 20.0
print(c.radius)         // 10
```

C#-style properties: `Type Name { get { ... } set { ... } }`, at least
one of `get`/`set` must be present (a read-only property leaves out
`set`, a write-only property leaves out `get`). In the setter body
`value` implicitly stands for the assigned value, as in C#. `get`, `set`
and `value` are deliberately NOT reserved keywords - purely
context-dependent identifiers, with a special meaning only inside a property body (or the
setter body for `value`); elsewhere in the program
they can still be used as normal names (variables, methods, ...).

#### Auto-properties

```
class Person {
    string Name { get; set; }
    int Age { get; }

    construct(string name, int age) {
        this.Name = name
        this._AutoAge = age   // backing field directly, since 'Age' has no setter
    }
}
```

`get;`/`set;` (without a body of their own, with `;` instead of `{ ... }`) are a
short form that automatically creates a **backing field** together with a trivial getter/
setter - pure parser sugar, desugared completely to exactly the form
one would also write by hand. The backing field is called
`_AutoName` (`_Auto` + property name) and is a **perfectly normal field**
of the class, without a special status - code inside the class (including the
constructor) can access it directly at any time (`this._AutoName`),
for example to initialize a get-only auto-property anyway, since
it has no setter via the property itself. Explicit and
automatic accessors may be mixed (`get { ... } set;`). Since the
backing field comes about by a naming convention, not by a guaranteed
unique mechanism: a field you declare yourself with the same name
(`_AutoName`) would collide - this is not specially checked, just
avoid it (the `Auto` infix makes an accidental collision with a
"normal" `_name` field considerably less likely than a plain
underscore prefix).

**Resolution is purely name-based** (like `[]` operator overloading via
`GetIndex`/`SetIndex`, SPEC 8.4): a property `Name` internally creates two
ordinary methods `get_Name`/`set_Name`. `obj.Name` (reading) or
`obj.Name = x` (writing) first try a REAL field named `Name` -
only if none exists, `get_Name()`/`set_Name(x)` is called. A
property therefore practically always needs a field with a DIFFERENT name as
backing store (`radius`, not `Diameter`) - a field and a property with
the same name would otherwise be ambiguous (the field wins).

**Error cases**: assigning to a read-only property (`get` without `set`)
is a clear run-time error (no silent field creation - that
would otherwise permanently shadow the property with a field of the same name).
Reading a write-only property is likewise a clear
error.

**No auto-properties**: unlike C#'s `{ get; set; }` short form (without a
body, with an implicit backing field), a real body must ALWAYS
be given here - a field alone already covers the "simple case without logic of its own",
an automatically generated backing-field variant would bring
little additional value for it.

### 8.9 Format strings (`$"..."`)

```
var name = "World"
var x = 255
print($"Hello, {name}!")                    // Hello, World!
print($"Hex: {x:X}, Binary: {x:B}")         // Hex: FF, Binary: 11111111
print($"Padded: {x:D5}")                    // Padded: 00255
print($"Pi ≈ {3.14159:F2}")                 // Pi ≈ 3.14
print($"Escaped braces: {{like these}}")    // Escaped braces: {like these}
```

A `$"..."` string works like a normal string literal, but may contain
`{expression}` or `{expression:Format}` - replaced at run time by the
(possibly formatted) value of the expression. `{{`/`}}` produce a
literal `{`/`}`. Inside `{...}` ANY expression is allowed (also
method calls, object fields, further string literals, ...), not only
simple variable names.

A `{...}` section is lexed and parsed anew on its own (not part
of the outer grammar) - as a result this feature needs no expression parser
of its own running in parallel. A restriction follows from this: a `:`
INSIDE the expression itself (e.g. the unit coercion `x : km`) is
ONLY recognized if it is inside parentheses of its own - a
`:` at the top level always separates expression and format specifier.
If the expression itself needs an unparenthesized `:`, it must be put in
parentheses: `{(x : km)}` instead of `{x : km}`.

**Format specifiers** (after a `:`, optional):

| Specifier | Meaning | Type | Example |
|---|---|---|---|
| `X`/`x` | Hexadecimal (upper/lower case) | `int` | `{255:X}` → `FF` |
| `B` | Binary | `int` | `{5:B}` → `101` |
| `D` | Decimal, padded with `0` | `int` | `{5:D3}` → `005` |
| `F` | Fixed point | `int`/`float` | `{3.14159:F2}` → `3.14` |
| `E` | Scientific notation | `int`/`float` | `{1234.5:E2}` → `1.23E+003` |

All except `B` pass the specifier directly through to .NET's built-in
number format strings (among other things, this is why an optional width/
decimals specification directly on the letter also works: `X4`, `F2`, `D5`, ...); `B`
(binary) is built by hand, since .NET has no native binary format. A
format specifier on a value of the wrong type (e.g. `X` on a
`float`) or an unknown specifier is a clear run-time error.

**Implementation**: `$"..."` is split by the lexer into text/expression segments
(`Lexing.InterpolationSegment`), parsed into an `InterpolatedStringExpr`
and compiled into a chain of normal `+` concatenations (uses the
existing string concatenation from `Value.Add`); a format specifier
converts its value beforehand explicitly via an opcode of its own (`FormatValue`,
`Value.Format`), instead of relying on `ToString()`.

### 8.10 Byte buffers (`new byte[n]`) & string/char conversion

```
var buf = new byte[4]      // Values.ByteBuffer, NOT ScriptArray
buf[0] = 0x48
buf[1] = 0x69
print(buf.ToString())      // "Hi" (ASCII decoding)

var bytes = "Hi".ToBytes()             // string -> buffer (ASCII, 1 byte/character)
var wide = "Hi".ToUnicode(2)           // string -> buffer (fixed 2 bytes/character)
print(wide.ToUnicode())                // "Hi" (decoded with the same width)
```

A `byte` buffer is a run-time type of its own (`Values.ByteBuffer`,
`ValueKind.Buffer`) - deliberately SEPARATE from `ScriptArray` (which holds boxed
`Value[]` elements, several bytes of overhead per element): a
`ByteBuffer` is a real, compact `byte[]`, intended for binary data from
IO (serial, network, files).

`byte` as a **scalar type** (e.g. `byte b = 5`), on the other hand, is NOT an
`ValueKind` of its own, but pure parser sugar for `int[8]` (an
explicit bit width directly after `byte` is therefore an error, it would be
redundant) - a single element of a buffer (`buf[i]`) is thus
simply a perfectly normal `int` value with this width, no
special representation.

**`new byte[n]`** creates the buffer (deliberately only one-dimensional - unlike
normal arrays there is no `new byte[n][m]`), with `n` bytes initialized to `0`
and the host byte order as the default (see below).
Index access reading/writing works as with arrays (`buf[i]`/
`buf[i] = value`, the same opcodes `ArrayGet`/`ArraySet`, the same catchable
`IndexOutOfBoundsException` for an invalid index). `buf.length` (int) and
`buf.littleEndian` (bool) are readable fields.

**Conversion methods** (method calls on primitive values - see
"Primitive methods" below):

| Call | Result | Meaning |
|---|---|---|
| `string.ToBytes()` | `buffer` | ASCII, 1 byte/character (lowest byte of the `char` value) |
| `char.ToByte()` | `byte` (= `int[8]`) | likewise, a single character |
| `buffer.ToString()` | `string` | ASCII decoding, 1 character/byte |
| `byte.ToChar()` | `char` | likewise, a single byte (lowest 8 bits of the `int` value) |
| `string.ToUnicode(int len)` | `buffer` | fixed width `len` bytes/character (typically 2, "Unicode" in the Windows sense/UTF-16 width) |
| `char.ToUnicode(int len)` | `buffer` | likewise, a single character |
| `buffer.ToUnicode()` / `buffer.ToUnicode(int len)` | `string` | fixed-width decoding (default `len=2` if omitted) |
| `buffer.ToUnicodeChar()` / `buffer.ToUnicodeChar(int len)` | `char` | likewise, reads only the FIRST `len` bytes |

"Unicode" here explicitly does NOT mean a complete Unicode library
(no normalization, no surrogate pair handling) - since a `char`
in this language is only a single 16-bit code unit anyway, the
encoding is simply "each character as `len` bytes of its ordinal value,
according to the current byte order" (freely selectable width instead of a hard-coded
UTF-16/UTF-32). `ToBytes`/`ToUnicode(len)` always create the buffer
with the HOST byte order.

**Endianness**: `ByteBuffer` carries a `ByteOrder` (little/big) - pure
metadata, the stored bytes themselves never change on their own.
`buffer.ToLittleEndian()`/`buffer.ToBigEndian()` return a copy: if
the target order already matches the current one, an unchanged copy
(no byte swap); otherwise a copy mirrored with the WHOLE buffer as ONE
contiguous block (not element by element in
fixed width - for a single multi-byte field, e.g. a 4-byte `int`, that is
exactly the right meaning). An explicit order at creation
is deliberately not offered as syntax of its own - `new byte[n].ToLittleEndian()`
achieves the same directly afterwards.

The HOST byte order itself is determined at RUN TIME (no compiler flag,
no assumption about the target platform): a 16-bit value is written,
then it is checked whether the least significant byte comes first in memory
(`Values.ByteConversions.HostByteOrder`) - the same classic run-time
test that a later C++ port of this VM would also use
(there typically via a raw pointer cast instead of `BitConverter`).

**Primitive methods**: `CallMethod` (the opcode behind every
`target.Method(...)` call) now accepts string/char/int/buffer values as a target
IN ADDITION to object instances - for this a fixed list of
built-in methods is checked first (see VM.TryCallBuiltinMethod), before (only
for an ObjectInstance) the normal, dynamic method dispatch via
a RuntimeClass applies. These conversions are therefore perfectly normal
method calls, not operators/special syntax - the operator
overloading of `SPEC 5.11` remains unaffected (different opcodes: `CallMethod`
here, `BinaryNumericOrOperator` there).

### 8.11 Streams and file access (`#import "io"`)

`#import "io"` unlocks the namespace `IO` (the package `fire-io`: its prelude and the C++ of `native/bridges/fire_bridge_io.hpp`; in the VM a library built from it, like `graphics`/
`devices`: native functions `__IO...` plus a fire prelude). Everything lives in `namespace IO`,
so that it does not collide with your own classes such as `File` or `Stream`; an enum in a
namespace is only reachable **fully qualified** (`IO.FileMode.Create`).

Structure: streams (`IO.FileStream`, `IO.MemoryStream`, your own streams), file/directory API
(`IO.File`, `IO.Directory`, `IO.Path`, `IO.Utf8`), text (`IO.TextReader`, `IO.TextWriter`) and
standard input/output (`IO.Stdio`) - all described below.

```
#import "io"

var w = new IO.FileStream("out.bin", IO.FileMode.Create)   // without access: Open->Read, Append->Write, otherwise ReadWrite
w.Write("Hello".ToBytes())          // Write(buffer) / Write(buffer, offset, count) -> number of bytes
w.WriteByte(33)
w.Close()                           // a second Close() has no effect

var r = new IO.FileStream("out.bin")             // IO.FileMode.Open, IO.FileAccess.Read
var head = r.ReadBytes(3)           // up to 3 bytes as a new buffer (shorter at the end)
var b = r.ReadByte()                // 0..255, -1 at the end
r.Seek(-1, IO.SeekOrigin.End)       // -> new position;  r.Position = 0 works too
var rest = r.ReadAll()              // everything up to the end as a buffer
```

| Member | Meaning |
|---|---|
| `Read(buffer[, offset, count])` | reads into a buffer, returns the count (0 = end) |
| `Write(buffer[, offset, count])` | writes from a buffer, returns the count |
| `ReadByte()` / `WriteByte(v)` | a single byte (`ReadByte` returns -1 at the end) |
| `ReadBytes(n)` / `ReadAll()` / `CopyTo(target)` | helpers built on `Read`/`Write` |
| `Position`, `Length` | property (read/set); only with `CanSeek` |
| `Seek(offset, origin)` | `IO.SeekOrigin.Begin/Current/End`, returns the new position |
| `CanRead`/`CanWrite`/`CanSeek`, `IsClosed` | capabilities |
| `Flush()`, `Close()` | |
| `MemoryStream.ToBuffer()` | the entire content as a buffer; `new IO.MemoryStream(buffer)` starts with a copy |
| `FileStream.Name` | the path as given |

`IO.FileMode`: `Open` (must exist), `Create` (create/truncate), `CreateNew` (must be
new), `OpenOrCreate`, `Append`. `IO.FileAccess`: `Read`, `Write`, `ReadWrite`.

**File and directory API.** Static methods, always written with the namespace
(`IO.File.Exists(...)`, a static access is only resolved via the exactly written
name).

```
var f = IO.Path.Combine("data", "notes.txt")
IO.Directory.Create("data")
IO.File.WriteAllText(f, "Greetings\nsecond line")
foreach (line in IO.File.ReadAllLines(f)) { print(line) }   // a List of strings (.count, [i])
print(IO.File.Size(f) + " bytes, modified " + IO.File.ModifiedTime(f))   // seconds since 1970 with unit s
IO.File.Copy(f, "backup.txt", true)
foreach (name in IO.Directory.GetFiles("data", "*.txt")) { print(IO.Path.FileName(name)) }
```

| Class | Methods |
|---|---|
| `IO.File` | `Exists`, `Size`, `ModifiedTime`, `Delete` (a missing file is not an error), `Copy(source, target, overwrite = false)`, `Move(source, target, overwrite = false)`, `ReadAllBytes`, `WriteAllBytes`, `AppendAllBytes`, `ReadAllText`, `WriteAllText`, `AppendAllText`, `ReadAllLines` (returns a `List`), `WriteAllLines` (List or array) |
| `IO.Directory` | `Exists`, `Create` (also intermediate directories, an existing one is not an error), `Delete(path, recursive = false)`, `GetFiles(path, pattern = "*", recursive = false)`, `GetDirectories(...)` (full paths as a sorted `List`), `Current()` |
| `IO.Path` | `Combine(a, b[, c])` (an absolute part discards everything before it), `FileName`, `Stem`, `Extension` (with the dot), `Parent`, `FullPath`, `Temp()`, `IsRooted`, `Separator()` |
| `IO.Utf8` | `GetBytes(text)`, `GetString(buffer[, offset, count])` |

Text is **UTF-8** (`string.ToBytes()`, on the other hand, is ASCII only): written without a byte order mark,
read with removal of a BOM, invalid sequences become U+FFFD. `WriteAllLines` terminates every
line with `\n`, `ReadAllLines` recognizes `\n`, `\r\n` and `\r` (a trailing line break
does not produce an empty last line). Every path goes through the `IoPolicy` before access (reading:
`Exists`/`Size`/`ModifiedTime`/`Copy` source; writing: `Create`/`Copy` target/`Move` target;
deleting: `Delete`/`Move` source; listing: `GetFiles`/`GetDirectories`); a rejected access
is an `IO.PermissionException` (code 6), also for `Exists` - a query must not reveal what
exists outside the permitted area. `IO.Path` itself is pure text processing without
file access. `Copy`/`Move` onto an existing target without `overwrite` throws
`IO.FileExistsException`, a missing directory `IO.DirectoryNotFoundException`.

**Text (`IO.TextReader`, `IO.TextWriter`).** UTF-8, line by line, on files or arbitrary streams:

```
var out = new IO.TextWriter("log.txt")            // overwrites; ("log.txt", true) appends
out.WriteLine("Greetings")
out.Write("Value: ")
out.Write(42)                                      // numbers etc. are written as text
out.Close()

foreach (line in new IO.TextReader("log.txt")) { print(line) }   // line by line

var reader = IO.File.OpenText("log.txt")           // also CreateText / AppendText
var first = reader.ReadLine()                      // undefined at the end
var rest = reader.ReadAll()                        // the rest as one string
reader.Close()
```

`new IO.TextReader(source[, leaveOpen])` / `new IO.TextWriter(target[, flag])`: with a **path**
they open the file themselves (writer: `flag` = append); with a **stream** (`IO.IStream`) they read/
write on it and **close it as well**, unless `leaveOpen`/`flag` is true. Lines end with
`\n` or `\r\n` (the `\r` is not part of the line), `\n` is written. `TextReader`: `ReadLine()`,
`ReadAll()`, `ReadLines()` (List), `EndOfStream`, `foreach`. Both close themselves in `destruct()`
and throw `IO.StreamClosedException` if you carry on after `Close()`.

**Standard input/output (`IO.Stdio`).** `IO.Stdio.Write(x)`, `WriteLine(x)`, `ErrorWrite(x)`,
`ErrorLine(x)`, `Flush()`, `ReadLine()` (undefined at the end), `ReadAll()`; `In()`/`Out()`/`Err()`
return them as a stream (e.g. `new IO.TextWriter(IO.Stdio.Out(), true)`), `Close()` on them changes
nothing. **Where** this leads is decided by the host: the real console (default, `IoStdio.SystemConsole`)
or callback functions (`IoStdio.Custom(output, error, input)` - the editor routes them into its
output window, the input is empty there). Output always runs as UTF-8; with the `Custom` target it is passed on
line by line, an incomplete line stays until the line break or `Flush()`.
`ReadLine`/`ReadAll` read buffered - do not mix with raw reads on `In()`. (Unlike
`print`, there is no automatic line break here.)

**Cleaning up:** `NativeStream.destruct()` closes the handle when the owner scope ends
(see 2 and 5.3) - a forgotten `Close()` does not stay open.

**Custom streams:** `IO.IStream` (`Read`, `Write`, `Flush`, `Close`) is the smallest
interface; it is more convenient to derive from `IO.Stream` and override `Read`/`Write` (and
`CanRead`/`CanWrite`/`Position`/... whatever is supported) - `ReadByte`, `ReadBytes`, `ReadAll`,
`CopyTo` then work automatically.

**Errors** are catchable exceptions, all deriving from `IO.IOException` (fields `message`, `code`):
`IO.FileNotFoundException` (3), `IO.DirectoryNotFoundException` (4), `IO.FileExistsException`
(7), `IO.StreamClosedException` (2), `IO.PermissionException` (5 = operating system, 6 =
host policy), otherwise `IO.IOException` (1 invalid argument, 8 not supported,
9 other). The names deliberately avoid `AccessDeniedException`, which the VM itself throws for
access modifiers.

**Security: the HOST decides.** The script cannot restrict or relax anything:
`RuntimeSession.Build(..., ioPolicy)` receives an `IoPolicy` (`AllowAll` = default, `DenyAll`,
`Rooted(directory, readOnly)` or a derivation of your own). Every path is fully normalized before opening
(`Path.GetFullPath`, i.e. without `..`) and checked; a rejected access
becomes `IO.PermissionException`. Symbolic links are not resolved (a link inside
a permitted directory that points outward leads out). `MemoryStream` is not affected by the
policy.

**Threads:** the handle table is thread-safe, every access to a stream is
locked; the error state (`__IOLastError`) applies per thread. Reading blocks the calling
VM thread (use `fire { ... }` for background work).

### 8.12 Strings and characters: `Length`, search, substrings

A `string` is an **immutable** sequence of 16-bit characters (`char`); all positions count in
such units, comparisons and searches are **ordinal** (case counts, no culture).

The methods are in the **prelude** as an extension of the base type (`class extends string { ... }`, 5.5.1)
and each call the **one** native function `__StringCall(id, text, arguments...)`; for `char` likewise
`__CharCall(id, char)`. The method is selected via its **ID** (`StringMethod`/`CharMethod` in
`fire.Standard`, fixed numbers), not via the name - no string comparison in the VM. The fire text
`class extends string { ... }` is produced by `StringMethods.PreludeSource` from a table, so the IDs exist
only in C#. Without the prelude these methods do not exist.

Not in the prelude, but provided by the VM itself, are the properties `Length` (also on arrays and
buffers; `length` is an alias) and the indexing `s[i]` (returns a `char`; assignment is an error).
A property cannot be defined by an extension (5.5.1).

```
string s = "Hello, World, again"
print(s.Length)                 // 19   (`s.length` is an alias)
print(s.IndexOf("o"))           // 4    -1 if nothing is found
print(s.IndexOf("o", 5))        // 8    search from position 5
print(s.LastIndexOf(","))       // 12   from the back
print(s.Substring(7, 5))        // "World";  Substring(14) = "again"
print(s[1])                     // 'e'
foreach (p in "a,b,c".Split(",")) { print(p) }
```

| `string` | Meaning |
|---|---|
| `IndexOf(x[, start])` | first position of `x` (string or char) from `start`, otherwise -1 |
| `LastIndexOf(x[, start])` | last position of `x`; with `start` the backward search begins there (`0 <= start < Length`) |
| `Substring(start[, count])` | substring; without `count` up to the end (`Substring(Length)` = `""`) |
| `CharAt(i)` / `s[i]` | the character at position `i` |
| `Contains(x)`, `StartsWith(x)`, `EndsWith(x)` | `bool` |
| `ToUpper()`, `ToLower()` | invariant upper/lower case |
| `Trim()`, `TrimStart()`, `TrimEnd()` | remove whitespace |
| `Replace(old, new)` | replaces all occurrences (an empty `old` leaves the string unchanged) |
| `Split(separator)` | array of strings (empty separator: the whole string as the only element) |
| `PadLeft(width[, fill])`, `PadRight(...)` | pad to a minimum width (default: spaces) |

| `char` | Meaning |
|---|---|
| `IsDigit()`, `IsLetter()`, `IsLetterOrDigit()`, `IsWhiteSpace()`, `IsUpper()`, `IsLower()` | Unicode classification of the single 16-bit unit, `bool` |
| `ToUpper()`, `ToLower()` | invariant, returns a `char` |
| `ToString()`, `ToInt()` | as a string or as the numeric value of the code unit |

(`ToByte()` and `ToUnicode(n)` on `char`, `ToBytes()`/`ToUnicode(n)` on `string` and `ToChar()` on `int`
remain the built-in conversions from 8.10.)

A position outside the permitted range throws `IndexOutOfBoundsException` (the text begins with
"String index"). The result of `Split` is an array and can therefore be iterated with `foreach`.
The editor knows these methods from the prelude (and also the user's own extensions): `text.`
suggests them, and the types of the results (`Trim()` → string, `Split()` → string[], `IndexOf()` → int,
…) run through method chains.

### 8.13 Reflection (`#import "reflection"`)

Classes and their members can be described at run time and used via their **name**. The library is pure fire source text over a few native functions
(`fire.Standard.ReflectionPrelude`, `fire.Runtime.ReflectionNatives`); programs without the import incur no overhead, and the compiler only writes the type metadata (`ClassMeta`)
when it needs it.

```
#import "reflection"

var t = Type.Of(circle)                 // or Type.Of("Circle"); Type.Named("Nope") returns undefined instead of throwing
print(t.Name + " : " + t.Base.Name)     // Circle : Shape
foreach (m in t.All) { print(m.Kind + " " + m.Access + " " + m.TypeName + " " + m.Name) }

Reflect.Get(circle, "radius")           Reflect.Set(circle, "Diameter", 20.0)      // fields AND properties
Reflect.Call(circle, "Scale", [2.0, 1]) Reflect.New("Circle", [5.0])
Reflect.Has(circle, "Area")
t.Find("radius").Get(circle)            // Member.Get/Set/Call(obj, ...)
```

- **`Type`**: `Name`, `Base` (a `Type` or `undefined`), `IsActor`, `Interfaces` (names), `All` (all `Member`s, also inherited ones; a derived class hides same-named ones of the base, constructors only its own),
  `Fields()`/`Properties()`/`Methods()`/`Constructors()`, `Find(name)`/`Has(name)`, `IsSubclassOf(type)`, `New(args)`; `Type.Of(x)`, `Type.Named(name)`, `Type.Names()`.
- **`Member`**: `Name`, `Kind` (`"field"`, `"property"`, `"method"`, `"constructor"`), `TypeName` (the declared type as in the source, for methods the return type, `""` if not given), `Access` (`"public"`/`"private"`/`"protected"`),
  `IsStatic`, `IsReadonly`, `CanRead`/`CanWrite` (properties), `Unit` (required unit), `DeclaredIn`, `ParamNames`/`ParamTypes`, `ParamCount()`, plus `Get(obj)`, `Set(obj, value)`, `Call(obj, args)`.
- **Rules:** Reflection bypasses nothing, it runs through the same paths as normal code. `private`/`protected` apply to the code that **called** the library (from a method of the class itself `Reflect.Get(this, "secret")`
  is allowed, from outside not: `AccessDeniedException`; in `Performance` mode the check is omitted as everywhere); a `readonly` field cannot be assigned; units are checked (`UnitMismatchException`); property accessors
  run as normal methods (an exception in them runs to the outer `catch`); for objects of the globals the section rules apply (THREADING_DESIGN.md section 7).
- **Errors** are catchable **`ReflectionException`**s (`message`): unknown or unreadable/unwritable member, wrong number of arguments, not an object, unknown class.
- **Limits:** Static members appear in the description, but cannot be read/written/called via `Reflect`. `Reflect.New` builds via a nested execution: if a constructor throws an exception, that is an
  internal error instead of a catchable exception. Dynamically created fields (without a declaration) do not appear in `Type`, `Reflect.Has` knows them.

#### Selectors: `lambda member<T> name` (and `field`, `property`, `method`, `selector`)

A parameter with a selector type accepts a lambda that **selects** a member; in the body the parameter then holds the **reflection of the selected member** (a `Selector`), not the lambda:

```
class Watch {
    static Show(lambda member<Circle> sel, Circle c) {
        print(sel.Name + " = " + sel.Get(c))       // radius = 5
        sel.Set(c, 3.0)
        print(sel.Describe(c).TypeName)             // float (the `Member`)
    }
}
Watch.Show(c => c.radius, myCircle)
```

Five kinds, depending on what the lambda may select:

| Type | allowed |
|---|---|
| `lambda field<T>` | only a **field** |
| `lambda property<T>` | only a **property** |
| `lambda member<T>` | a field **or** a property |
| `lambda method<T>` | only a **method** |
| `lambda selector<T>` | **anything**: field, property and method |

`T` is the class name against which the resolver checks (`lambda selector<>` without a type also works); the instance is not checked against `T` at run time. If the selected member does not match the kind, that is a `ReflectionException`
("'P' is a property, expected (lambda field<...>): a field") - as soon as there is an object (`Get`/`Set`/`Call`/`Describe`/`Probe`).

`Selector`: `Name` (the selected member), `Path` (all names, for `p => p.address.city`: `address`, `city`), `Kind` (the kind of the parameter type), `Parent(obj)`, `ActualKind(obj)` (`"field"`, `"property"`, `"method"`), `Get(obj)`, `Set(obj, value)`,
`Call(obj, args)` (only for a method, i.e. with `method<T>` or `selector<T>`), `Describe(obj)` (the `Member`), `Probe`/`Silence` (not for methods). `Get`/`Set` on a method are a `ReflectionException` (use `Call` for that). A method is selected without a call: `x => x.Twice`.
The lambda must have exactly one parameter, and its body may only be a **member chain on this parameter**; anything else is a `ReflectionException` ("The lambda is not a selector ..."). If an already converted selector is passed on to another
selector parameter, it remains unchanged (and keeps the kind of the first parameter). Without `#import "reflection"` every selector type is an error.

### 8.14 `probe` and `silence`

A `probe` attaches a handler to **write accesses to a member of an object** - also from outside, without changing the class. `silence` removes probes again. Neither needs an import.

```
var h = probe cfg.volume changed { print(name + ": " + old + " -> " + value) }   // block: implicit names sender, name, old, value
probe cfg.volume changing (old, new) => new <= 100                              // false aborts the write
probe player.stats.hp changed (o, v) => ui.Refresh(v)                           // path: object = player.stats, member = hp
probe cfg.volume changed handlerLambda                                          // any lambda value
probe cfg.* changed (s, n, a, b) => print(n + " " + a + "->" + b)               // all members

silence h                // handle (int) -> exactly this probe
silence cfg.volume       // all probes of this member
silence cfg.*            // all probes of the object (likewise: silence cfg)
```

- **Target:** `probe a.b.c ...` evaluates `a.b` **once**; the probe is attached to **this object**, not to the slot (if `a.b` is replaced later, it stays on the old object). The member must exist (field, property or method),
  otherwise it is an error when registering. `probe ...` is an expression and returns the handle (`int`), as a statement it is discarded. `silence x` with an object removes all of its probes; an already removed handle is not an error.
- **Handler:** the block and `=> expression` receive the four names `sender` (the object), `name` (the member), `old`, `value`; a lambda with a parameter list receives, depending on the **number**, 0 nothing, 1 `(new)`, 2 `(old, new)`,
  3 `(object, old, new)`, 4 `(object, name, old, new)` (more than 4 is an error). It may capture local values (4.2.1).
- **When:** `changing` runs **before** the write; if a handler returns `false`, nothing is written (the assignment expression still evaluates to the assigned value), the remaining ones no longer run. `changed` runs **after** the
  write and only if the value has really changed (comparison like `==`, objects by reference). Several probes run in the order of their registration.
- **What is observed:** write accesses to the member (`=`, `++`, `+=`, via reflection) - for properties before/after the call of the setter (old value = result of the getter, if there is one); if the setter itself writes fields, their
  probes fire as well. **Not** observed: changes *inside* an object (`obj.list.Add(...)`, array elements), a computed property whose source changes (probe the field for that), and write accesses from `sync` write-backs.
- **Flow:** synchronously on the thread of the writer (for objects of the globals inside the section). If a handler writes the same member of the same object, nothing fires again for it. An exception in the handler runs to
  the writer: with `changing` the value stays unchanged, with `changed` it has already been written. Probes live with the object (its end removes them).
- **Cost:** only objects with a probe take the slow write path; all others keep the fast paths unchanged.
- **Keywords:** `probe`, `silence`, `changed`, `changing` are context-dependent (`probe`/`silence` only if an identifier or `this` follows directly) - they remain usable as variable names.
- **With reflection** (`#import "reflection"`, 8.13): `Reflect.Probe(obj, "name", "changed"|"changing", handler)`, `Reflect.ProbeAll`, `Reflect.Silence(obj, "name")`, `Reflect.SilenceAll(obj)`, `Reflect.SilenceHandle(h)`, `Member.Probe(obj, kind, handler)`
  and `Selector.Probe(obj, kind, handler)`/`Selector.Silence(obj)` - e.g. `Watch(c => c.volume, cfg)` with `lambda member<Cfg> sel` and `sel.Probe(cfg, "changed", ...)`.

### 8.15 `DateTime`, `TimeSpan` and `Sleep` (`#import "time"`)

Time is counted in **ticks** of 100 ns (as in .NET); `DateTime` counts from 0001-01-01. The classes are written in fire (`fire.Standard.TimePrelude`) over a few native functions (`fire.Runtime.TimeNatives`); errors are catchable **`TimeException`**s.

```
#import "time"

var duration = TimeSpan.FromSeconds(90)           // also FromMilliseconds/FromMinutes/FromHours/FromDays/FromTicks, Zero(); new TimeSpan(h, m, s) / (d, h, m, s) / (d, h, m, s, ms)
print(duration)                                   // 00:01:30
print(duration.TotalMinutes + " " + duration.Seconds)   // 1.5 30
var w = TimeSpan.Of(250ms)                        // from a value with a time unit, a number (ms) or a TimeSpan

var now = DateTime.Now()                          // UtcNow(), Today(); new DateTime(2024, 3, 15) / (y, m, d, h, mi, s) / (..., ms)
var tomorrow = now + TimeSpan.FromDays(1)
print(tomorrow.ToString("dd.MM.yyyy HH:mm"))
print(DateTime.Parse("2024-12-24 18:00") - now)   // a TimeSpan
Sleep(500ms)                                      // or Sleep(duration), Sleep(250) (milliseconds)
```

- **`TimeSpan`**: factories (see above), `Of(value)`; components `Days`/`Hours`/`Minutes`/`Seconds`/`Milliseconds` (integer), `Total...` (decimal numbers), `Ticks`; arithmetic `+`, `-`, `* number`, `/ number` (also `Add`/`Subtract`/`Multiply`/`Divide`/`Negate`/`Abs`),
  comparisons `< <= > >= == != ##`, `CompareTo`, `Equals`; `ToString()` returns `[-][d.]hh:mm:ss[.fffffff]`.
- **`DateTime`**: components `Year`/`Month`/`Day`/`Hour`/`Minute`/`Second`/`Millisecond`/`DayOfWeek` (0 = Sunday)/`DayOfYear`/`Ticks`/`Kind`, `DayName()`/`MonthName()`, `Date()`, `TimeOfDay()`; arithmetic `dt + timespan`, `dt - timespan` (a `DateTime`),
  `dt - dt` (a `TimeSpan`), `AddDays`/`AddHours`/`AddMinutes`/`AddSeconds`/`AddMilliseconds`/`AddTicks`/`AddMonths`/`AddYears`; comparisons as for `TimeSpan`; `ToString()` (`yyyy-MM-dd HH:mm:ss`) and `ToString(format)` with the .NET time formats (invariant culture);
  `Parse(text)` (throws `TimeException`), `TryParse(text)` (undefined), `DaysInMonth`, `IsLeapYear`, `FromUnixSeconds`/`ToUnixSeconds`. **`Kind`** is `"local"` (`Now`, `Today`, constructors, `Parse`) or `"utc"` (`UtcNow`, `FromUnixSeconds`); `ToUtc()`/`ToLocal()` convert. Comparisons and
  differences require the same kind. Time zones beyond local/UTC do not exist.
- **`Sleep(time)`** puts the thread to sleep. `time` is a `TimeSpan`, a value with a time unit (`500ms`, `2s`, `1.5min`) or a number (milliseconds). Sleeping is **not deaf**: in short slices, whatever otherwise runs at the safe points runs - `leave`/`terminate` end
  it immediately, delivered fire-thread exceptions are handled, and in the main program the **automatic processing of the queue** works (sections of the fire threads, `fire global` jobs, host callbacks, see THREADING_DESIGN.md section 7; with `#nosync` only at `sync globals`).
  A new entry in the queue wakes the sleep up earlier. A callback or destructor (nested execution) sleeps without these tasks.

**`ToString()` on output and concatenation.** If an object has a parameterless method `ToString()`, it is used by `print(object)`, `"text" + object`, `object + "text"` (if the class has an `operator+`, that applies, `DateTime`/`TimeSpan` handle a string there themselves) and `$"{object}"`. An
exception in `ToString()` runs to the enclosing `catch`. Without `ToString()` the old representation (`<object ...>`) remains.

### 8.16 Devices (`#import "devices"`)

The extension controls devices via the **DeviceManager** (`src/fire.Device.Manager`: drivers, devices, handles; the package `fire-devices`: the fire classes and the C++ of `native/bridges/fire_bridge_devices.hpp`, which in the VM asks the host's device manager through the callbacks of the package ABI). A device has an identifier `driver:port`
(`serial:COM3`, `loopback:echo`). The `serial` driver (115200 baud) is built in; `loopback` is a simulated echo device without hardware (everything you send comes back after a few milliseconds).

You work at the **command level**: send text and bytes, wait for certain characters, execute commands as objects.

```
#import "devices"
#timeout 10s                                 // default wait time of the wait functions (otherwise 30 seconds)

var d = Device.Default.EnsureConnected()     // the host's default device, connects if necessary
d.DoCommand("M105")                          // send a line
if (d.WaitForString("ok\n")) {               // waits for the end of the reply, cuts the buffer behind it
    print(d.ReadString())                    // whatever came after it
}

d.WriteString("G28")                         // without a line ending
d.Write(bytes)                               // raw bytes (a byte buffer)
d.WaitFor(bytes, 2s)                         // wait for these bytes, at most 2 seconds

var home = new Command<IDevice>(dev => { return dev.WriteString("G28\n") })
d.DoCommand(home)                            // runs home.Execute(d)
d.DoCommands([home, home])                   // several, stops at the first one that returns false
```

| Member | Meaning |
|---|---|
| `Device.Default` (static property) | the default device chosen by the host; throws `DeviceNotFoundException` if none is chosen (e.g. in a standalone program) |
| `Device.HasDefault` (static property) | is a default device chosen? |
| `IsConnected` (property; the method `IsConnected()` remains) | connected? |
| `IsShared` (property) | does the device belong to a shared manager (the editor's)? |
| `Connect()` / `Disconnect()` | connect (`bool`) / disconnect |
| `EnsureConnected()` | connects only if necessary; throws `DeviceConnectionException` if that fails; returns the device itself (chainable) |
| `DoCommand(command)` | a text goes out as a line (with a line ending, in the device's encoding; formerly `SendCommand`); `false` if not connected. A command object (`Command<IDevice>`, derived from it, or any object with `Execute(device)`) is executed with `command.Execute(device)`; the result is `false` if `Execute` returns `false`, otherwise `true` |
| `DoCommands(commands)` | array, `List` or anything usable with `foreach`: executes in order, stops at the first `false`; `true` if all ran |
| `HasData()` | is received material available? |
| `ReadString()` | the next received packet as text, one character per byte (Latin1; formerly `ReadData`); `""` if nothing is there |
| `Read()` | the same as a `byte` buffer (empty if nothing is there) |
| `WriteString(text)` | writes exactly these characters, WITHOUT a line ending (one character = one byte, Latin1; for UTF-8 use `text.ToBytes()` and `Write`); `false` if not connected |
| `Write(buffer)` | writes raw bytes; `false` if not connected |
| `WaitForString(text, timeout)` / `WaitFor(buffer, timeout)` | see below |
| `Identifier()`, `PortName()`, `Availability()`, `TestAvailability()` | identifier, port, availability (0 = unavailable, 1 = unchecked, 2 = available) |
| `DeviceManagerFacade` | `Refresh(fastScan)`, `Count()`, `GetAt(i)`, `GetByHandle(h)`, `GetByIdentifier(id)`, `IsShared()` |
| `IDevice` | the interface that `Device` fulfills (type argument and parameter type for `Command<IDevice>`) |

**`WaitForString` / `WaitFor`.** Wait until the characters or bytes are in the receive buffer - even if they arrive in several packets - and cut the buffer BEHIND the first match: everything before it and the match
are consumed, whatever came after stays (readable with `ReadString()`/`Read()`). If the same sequence arrives twice, the same call therefore succeeds twice in a row. An empty sequence counts as found immediately. The result is `true` if the
sequence arrived, `false` after the wait time has expired, if the device is (or becomes) disconnected, or if the program ends (`leave`/`terminate` act immediately, waiting is not deaf like `Sleep`; the main program's queue keeps running).
`timeout` is optional: a `TimeSpan`, a time value (`5s`, `500ms`) or a number in milliseconds (as with `Sleep`); if omitted, the program's `#timeout` applies, otherwise 30 seconds. An invalid value is a `DeviceArgumentException`.

**`#timeout value`** (top-level directive like `#nosync`, applies to the whole program): sets the default wait time of the wait functions. `value` is a time value (`10s`, `500ms`, `2min`) or a number in milliseconds; the expression is evaluated right at the start
of the program (so it may not use variables) and also applies in the fire threads. A new program starts with 30 seconds again.

**Shared DeviceManager.** If a script runs in the editor, it uses the editor's common manager (`DeviceManager.IsShared`): devices and open connections survive the run, several scripts one after the other work with
the same devices. A script can destroy neither the manager nor a shared device (there is no function for it; `DeviceManager.Dispose()` has no effect on a shared manager, only the owner tears it down via `Shutdown()`).
A standalone program (or `fire.Compiler run`) gets an own, non-shared manager with the built-in drivers, which is released (devices disconnected) after the run. Receive hooks of a run are released at the end.

**Default device.** The host sets `DeviceManager.DefaultIdentifier`; in the editor by right-click in the device tree ("Set as Default Device") or via the selection in the toolbar (kept across restarts).

**Packet trace.** The manager attaches to `IDevice.OnRawDataSent`/`OnRawDataReceived` and reports every packet via `DeviceManager.PacketCaptured` (`PacketRecord`: time, device, direction, bytes) - no matter whether the script or the host sent it.
`PacketLog` stores logs as a text file `.fplog` (header line `# fire-packetlog 1`, one line per packet: UTC time, `H2D`/`D2H`, identifier, hex bytes, separated by tabs; lossless).

### 8.17 Documentation comments (`///`)

Lines that start with `///` directly above a **class, interface, field, property or method** document it (constructors included). The editor shows the text as a tooltip when the symbol is selected in the completion list, when the caret rests on its name, and when the mouse hovers over it. For the compiler they are ordinary comments.

```
/// A circle with a radius.
class Circle {
    /// The radius, in millimeters.
    float radius

    /// <summary>Scales the circle.</summary>
    /// <param name="factor">What the radius is multiplied by.</param>
    /// <returns>The new radius.</returns>
    float Scale(float factor) {
        this.radius = this.radius * factor
        return this.radius
    }
}
```

- While you type the arguments of a call, the tooltip stays visible until the closing `)`. After `new Foo(` it shows the documentation of the constructor (the overload whose parameter count fits the arguments typed so far).
- The `///` lines must be contiguous and directly above the declaration - a blank line or an ordinary `//` comment in between means "no documentation".
- Plain text is the summary: the lines of a paragraph are joined, a blank line starts a new paragraph.
- Like in Visual Studio, the XML-style tags `<summary>`, `<param name="...">`, `<returns>` and `<remarks>` are recognized (also `<c>`, `<para>` and `<see cref="..."/>`, which are shown as plain text); other tags are dropped and the entities `&lt; &gt; &amp; &quot; &apos;` are resolved.
- Declarations in files pulled in with `#include` are not covered (the editor only looks at the document itself and the built-in libraries).

### 8.18 Imports of packages (`ember`)

Besides the libraries of the compiler, `#import "name"` also finds the imports of **installed packages**: a package (`.fpk`, a zip with a `package.json`) brings a prelude in fire
and/or natives as C++ source for the native backend, and is installed for the machine with the package manager `ember` (also in the editor). An unknown import is an error
that points to `ember`. The natives of a package are C++ (docs/PACKAGE_NATIVES.md): a native build puts the source into the generated file, the virtual machine calls a shared library that the compiler builds from it for the machine. See docs/PACKAGES.md.

### 8.19 Random numbers (`#import "random"`)

`PseudoRandom` is a pseudo random number generator written in fire (the package `fire-random`, no natives): xoshiro128** with four 32-bit words computed in the 64-bit `int`, so the same seed gives the
same numbers in the virtual machine, in a native build and on every platform. It brings `time` along: `new PseudoRandom()` starts from the clock (`DateTime.UtcNow().Ticks`; two generators made within the same
100 ns tick get the same sequence - give them seeds then), `new PseudoRandom(seed)` from a number.

- `Next()`: 0 .. 2147483646 (like .NET); `Next(max)`: 0 .. max - 1 (any `max` above 0, also above 2^32); `Next(min, max)`: min .. max - 1. The values are unbiased (no modulo skew). A `max` that is not above 0 (or `max <= min`) throws `RandomException`.
- `NextUInt32()`: 32 random bits (0 .. 4294967295); `NextInt()`: any `int` (63 bits and the sign); `NextFloat()`: 0.0 up to (not including) 1.0 with 53 random bits; `NextBool()`.
- `Pick(list)`: a random element (`undefined` for an empty list); `Shuffle(list)`: mixes the list in place (Fisher-Yates) and returns it; `Seed(seed)` starts the sequence again.

Not for secrets: the sequence can be predicted from a few outputs.

### 8.20 Network (`#import "net"`)

The package `fire-net` (docs/NETWORK.md is the reference): `Net.TcpClient` (an `IO.Stream`), `Net.TcpListener`, `Net.UdpSocket` and `Net.Dns`, written in fire over C++ natives on the sockets of the platform (BSD sockets, Winsock, lwIP). It needs
`io` and `time`. Every call that waits takes a time limit; waiting polls the natives and sleeps 1 to 10 ms between the attempts, so `terminate` and the other threads work while a program waits for the network. Errors are `Net.NetException` (with a `code`) and subclasses;
the host can restrict the network with a `NetPolicy` (a refused access is a `Net.PermissionException`). A platform without a network throws `NetException` (code 9).

### 8.21 HTTP (`#import "http"`)

The package `fire-http` (docs/NETWORK.md is the reference): `Http.Client` (GET/POST/..., redirects, chunked bodies), `Http.Server` (routes, one connection after the other) and the classes around them, written in fire on the `net` package.
Errors are `Http.HttpException` (with a `code`) and the exceptions of `net`.

### 8.22 TLS (`#import "tls"`)

The package `fire-tls` (docs/NETWORK.md is the reference): `Tls.Stream` (an `IO.Stream` over a `Net.TcpClient`), `Tls.Server`, `Tls.Options`; SChannel (Windows), OpenSSL or mbedTLS underneath. `#import "http"` brings it along for `https://`. Errors are
`Tls.TlsException` and `Tls.CertificateException` (both `Net.NetException`s).

### 8.23 GPIO (`#import "gpio"`)

The package `fire-gpio` (docs/NETWORK.md is the reference): `Gpio.Pin` (input with pull and edge events, output, read/write/toggle), `Gpio.Board` (the chips of the machine) and `Gpio.Sim` (the simulated chip `"sim"` that every platform has); the character device of Linux or
ESP-IDF underneath. It needs `time`. Nothing blocks: edges are collected and handed out by `TakeEdge`/`WaitEdge`/`Poll`. Errors are `Gpio.GpioException` (with a `code`) and subclasses.

### 8.24 I2C (`#import "i2c"`)

The package `fire-i2c` (docs/NETWORK.md is the reference): `I2c.Bus` (write, read, write-then-read with a repeated start, register helpers, probe, scan), `I2c.Board` (the buses of the machine) and `I2c.Sim` (devices on the simulated bus `"sim"` that every platform has);
`/dev/i2c-N` of Linux or the master driver of ESP-IDF underneath. Transfers are done in the call; errors are `I2c.I2cException` (with a `code`) and subclasses, `I2c.NoAckException` when no device answers.

### 8.25 SPI (`#import "spi"`)

The package `fire-spi` (docs/NETWORK.md is the reference): `Spi.Device` (a bus with one chip select: full-duplex `Transfer`, `Write`, `Read`, `WriteRead`, mode/speed/bit order), `Spi.Board` (the devices of the machine) and `Spi.Sim` (the simulated device `"sim"`: loopback or
queued answers, a log of what was sent); `/dev/spidevB.C` of Linux or the SPI master driver of ESP-IDF underneath. Transfers are done in the call; errors are `Spi.SpiException` (with a `code`) and subclasses.

### 8.26 WiFi (`#import "wifi"`)

The package `fire-wifi` (docs/NETWORK.md is the reference): `WiFi.Station` (scan, join, state, address), `WiFi.AccessPoint`, `WiFi.Board` (the radios of the machine) and `WiFi.Sim` (the simulated radio `"sim"` that every platform has); the WiFi driver of ESP-IDF underneath, "not
supported" where the operating system owns the network. It needs `time`; scanning and joining poll the natives and sleep between the questions. Errors are `WiFi.WiFiException` (with a `code`) and subclasses (`AuthException`, `NotFoundException`, `TimeoutException`, ...).

### 8.27 Projects and solutions

A program can be given to the compiler as a **project** (`name.fireproj`, JSON) or a **solution** (`name.firesln`) instead of a list of files (docs/PROJECTS.md is the reference). A project names its files (default: all `*.script` of its
folder), its type (`exe`, or `library` without an entry point - a statement at the top level is an error), its build settings, and its references (projects of the solution, installed packages). A reference makes a library available;
`#import "Name"` in the source turns it on, exactly as for a package, and the library's files are processed before the files of the project. The files of a project are part of the program without an `#include` (UI markup files, `.fxml`, as the script generated from them); an `#include` of such a file adds nothing. A project may have C++ natives in its folder `native/`: each `inline Value name(Value ...)` there is the native function `__name`, built like the natives of a package. A library is packed with `fire build Core.fireproj` into an ordinary `.fpk`.
Build settings are taken from the project first, then from the solution, then from the tags in the source (`#debug`, `#name "..."`, `#floatwidth 32`, `#noconsole`, ...), then the defaults; `#if` symbols of all levels are added up; options on the command line go before the project.

## 9. Open points

The only earlier point here – the method declaration syntax
`[TypeKeyword] name(params) { }` inside classes – has long been confirmed by the
actual implementation and wide use.

One known point that is not yet wired up:
- **8.2**: the *automatic* application of the bit width truncation on every
  assignment (the mechanism itself, `Value.Width`/`Value.TruncateTo`, is
  finished; the resolver would additionally have to keep track of the declared type of every
  slot in order to apply it automatically).

## 10. Architecture note

The interpreter no longer runs as a tree walker, but as a
bytecode VM (lexer → parser → resolver → compiler → VM) - see
`BYTECODE.md` for the complete opcode list and run-time architecture.
All design decisions listed below, originally intended as preparation for it,
have been implemented, including resumable exceptions
(7.5) via explicit, resumable handler/continuation objects instead of
pure native call-stack unwinding.

