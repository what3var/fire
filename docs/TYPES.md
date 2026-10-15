# Gradual typing

Goal: a leaner and faster native backend (and VM) by proving types where the program states them, without losing what the dynamic language can do today
(reflection, properties by name, probes/bindings, units from data).

## Starting point (measured before step 1; the steps below changed it)

* The parser already keeps `TypeRef` for declarations, parameters, return types, fields, `catch` and `is of` (`src/fire/Ast/TypeRef.cs`), and `var` exists.
* **Annotations are not enforced.** `int x = 3; x = "hello"` and `int y = "text"` run and print the string; a parameter `int a` accepts `"s"`. Types are documentation only.
* The native backend therefore treats every value as a tagged `Value`; every method call goes through `call_<name>_<argc>` (a switch over all registered classes), every field
  access through `gf_/sf_/fp_<name>` (a switch over class ids). In the 3D sample that is about a quarter of the generated lines; the C++ compiler already drops the unused ones.

## Steps

0. **Inventory** (this document): what carries types, what is enforced, what a dispatcher costs.
1. **Make annotations mean something (checker only, no codegen change).** A new pass after the resolver computes a static type for every expression where it can
   (literals, annotated variables/parameters/returns/fields, `new`, `var x = <typed expr>`), with a distinguished `dynamic` type for everything else.
   Incompatible assignments, arguments and returns between two *known* types are reported (first as warnings, `E`-codes in `ErrorCatalog`, later errors by project setting).
   `dynamic` never reports, so existing code keeps running. Units stay out of the first cut (`int` vs `int:m` is the same type here).
2. **Runtime guard for annotated slots that the checker cannot prove** (`int x = f()` where `f` is dynamic): a cheap tag check at the assignment, throwing a catchable
   exception, switchable per project. This makes the annotation a promise the backend may rely on.
3. **Backend use.** Typed locals/params of `int`/`float`/`bool` unboxed in C++ (and typed opcodes in the VM); calls on a receiver with a proven class go straight to the
   method (virtual only if a subclass overrides it); field access by index without the `gf_` switch. Everything unproven keeps the dispatcher.
4. **Units in the type** (`int:m`, `float:unitless`): static unit mismatch errors where both units are known; `::` / `:` coercions typed.
5. **Optional strictness**: a project/file setting `types: strict` makes `dynamic` an error outside explicit `dynamic` declarations.

Each step is independently shippable and tested VM == native.

## Status

Step 1 is in: `src/fire.Compiler/TypeChecker.cs` runs after the resolver on the code of the user (not the preludes and libraries). It reports conflicts between two known types as `TC001`
(assignment, initializer, field), `TC002` (argument of a method or constructor with exactly one candidate) and `TC003` (return value, reserved); in lax mode as warnings, in strict mode as errors.
Select the mode with `--types lax|strict` (compiler switch, wins), the project setting `typeChecking`, and `#lax` / `#strict` in a file (wins for that file). The editor shows the same diagnostics while
typing. `var x` without an initializer is the error `E4058` (fields declared `var name` in a class stay allowed: the libraries use them as untyped fields). Step 2 (the language and the standard library): `any`, `class` for objects only, no `var` fields/properties (`E4059`/`E4060`), untyped parameters reported (`TC004`), the unit `: any`. Step 2b: return types - a method without one is reported (`TC005`; constructors, destructors and operators are exempt), `undefined` is the type of "nothing", a bare `return` is `return undefined`, and a method that returns a value or nothing declares the value's type (`undefined` fits every type); calls of static methods (`Class.Method()`) are looked at too, and operators may carry a return type (`bool operator==(any o)`). The generated code of the `.fxml` markup is typed. All preludes and the UI
library are typed (fields, properties, parameters, callbacks with their number of parameters) and a test compiles every import in strict mode with the checker looking at the library code, too
(`FIRE_TYPECHECK_ALL=1` does the same for a build).

Step 3a: arrays (element types from declarations, `new`, literals; elements, `length`, `foreach`), indexers (`operator[]`), strings (`s[i]` is a `char`, `Length`), typed
lambdas (parameter and result types from the target or from the lambda's own declaration, arity of calls and assignments), `fire` blocks (with their `taking`/`with`
variables), `class extends`, interface signatures (`TC004`/`TC005`) and calls of lambdas in fields and variables are checked.

Step 3b: generics - type arguments in declarations (`List<int>`, nested, on base classes and interfaces), substitution of the type parameters in fields, properties,
methods, constructors and indexers, `foreach` over an `IEnumerable<T>` (or via `GetEnumerator()`/`GetCurrent()`), the raw form (`new List()`: every type argument `any`);
interfaces are types; `List<T>`, `IEnumerable<T>`, `IEnumerator<T>` in the prelude (at runtime nothing changes: no specialisation).

Step 3c: units (SPEC 3.5) - a value carries its unit in the static type (literals, declarations, `:`/`::`, the algebra of `+ - * /` and the comparisons). A declaration
with a unit converts a value of the same dimension into it (at run time `CheckUnit`, an `int` that does not fit is an `OverflowException`), another dimension is `TC006`;
a declaration without a unit takes the unit of its initializer (`int a = 5mm`), otherwise it is unitless (only `var` stays free, and an initializer of unknown unit leaves it free), `: any` takes every unit, `: any m` every unit of one dimension; the unit can stand at the type (`float:m Len()` converts the return value); a `float` declaration converts a whole number into a float. Where the compiler cannot see the unit of a value
that goes to a unitless declaration, it emits a run-time check. A whole number converted into a coarser unit is `TC007` (always a warning). Properties carry a unit
(`float X : m { get ... set ... }`: the setter's `value` and the getter's result are converted); Scene3D declares its lengths (`: m`, parameters `: any m`) and angles (`: any`).

Step 3e: generic methods (`R Select<R>(R lambda<T> fn)`, `static Box<U> Of<U>(U value)`) take their type parameters from the arguments, one after the other
(`IEnumerable<T>` from a `List<int>` or an `int[]`, `R` from the result of a lambda argument; what is not found is `any`). Linq is typed: `Linq.From(xs)` is a `Query<T>`,
`Where`/`OrderBy`/`Take`/... keep `T`, `Select<R>`/`Zip<U, R>`/`Aggregate<A>`/`Sum<R>` take their result types from the lambdas, `First`/`Last`/`Min`/`Max` return `T`,
`ToList()` a `List<T>`; the operators of a `List<T>` are the same, those of an array take `any` (at run time nothing changes). A member that a fully known class does
not have (`e.Message` on an `Exception`) is `TC008`, a call with a number of arguments no overload takes is `TC002`. Assignments to fields and properties without
`this.` (also inherited ones) are checked like those with it.

Known gaps: `SelectMany` and the selector projections (`SelectMember`, ...) are `Query` of `any`.

Step 3 (backend use), first part: the native generator (`src/fire.Native/CppGenerator.cs`) proves types from the bytecode itself, with the same fixpoint as
the ownership analysis (`VarRef`): every stack slot carries a static type (an `int`, `float` or `bool` without a bit width, and the static unit of a number),
joined where paths meet; every variable (local, global, parameter) joins the types of everything stored into it, and a variable written through a pointer,
a parameter or a value of unknown type is unknown. Instructions that are not taught about types forget the types of the slots they write, so an
unknown instruction can never make the generator believe a wrong type. Globals are trusted only in the main program (a method can run before their declaration).
With the types known the C++ is the operation itself: `+ - * / %` (ints wrap; `/` and `%` by 0 throw), the comparisons and the conditional jumps (no
`truthy`), `-x`, `!b`, `x++`/`x += k`, no ownership bookkeeping for numbers (`HoistValue`, `adopt` after a call). Units: `+ - %` and the comparisons
of two numbers of the same unit keep it, `* /` with a number without a unit keep the other one, a float plus a number of another unit of the same dimension
is converted by the factor of `alignUnits` (looked up once per pair), `:`/`::` and the unit check of a declaration disappear where the unit is already
the right one. The return type of a method is the join of its `return` values; a call of a known method (static, base, initializer) or a dispatcher call
whose candidates all return the same type gives it. In a loop like `for (int i ...) { sum += i % 7 }` the generated code is plain C++ on the
`Value` fields (30M iterations: 0.19 s -> 0.027 s; `float:m len = len + 0.5mm`: 0.67 s -> 0.071 s).

Step 2 (the guard): a parameter or field declared `int`/`float`/`bool` holds that kind or `undefined` (nullable), checked at run time (`DeclaredKind`):
the parameters at the end of the prologue of a method (`GuardLocal`), the fields in their setter (VM: the slow route and one compare in the inline
cache; native: `sf_`), static fields on every assignment. A whole number for a `float` becomes a float, another kind is an `ArgumentException`, a number
declared without a unit and without an initializer (or with a number literal as initializer) is converted into `unitless` (another dimension:
`UnitMismatchException`; an initializer with a unit gives the declaration that unit, checked where it is assigned as before, so its unit is not static). With that the generator types parameters and fields
too: the type "kind or undefined" runs the C++ operation behind a test of the kind (`a.kind == K_Float ? Float(a.f * b.f) : mul(a, b)`; the generic
function throws for `undefined`). The result of a call of a function whose `return`s were not seen yet is "nothing yet" (it joins to the other side),
and which scopes need an ownership list is found again in every round, so a type that becomes known later still makes the code lean.

Objects of a known class: `new C(...)` and `this` in a method of `C` give the type "an object of `C` or of a class derived from it", carried
through variables like the other types. A field of such an object is read and written by its index (the fields of a base class come first, so the index
is the same in every derived class; the declaration's unit and kind checks stay, unless the stored value's type already fits), and a method that no
derived class overrides is called directly instead of through the dispatcher (a destroyed object still throws). An object with probes (its flag 2)
is written through the setter, which tells them - one compare per write, every other object is written by the index. Properties, members with an
access check, ownership methods and programs with threads or actors keep the helpers.

Parameters and fields declared with a class (`Point p`, `Node next`) are a promise, too: `undefined` or `is of` the class, checked like the
numbers (VM: `GuardLocalClass` at the end of the prologue, `FieldInfo.RequiredClass` in the setter and the inline cache; native: `isof_` in the
prologue and in `sf_`). Interfaces and type parameters with `where` are checked roughly, by the same guard: the declaration is encoded as
alternatives of conditions (`DeclaredType`: `is of` a class, interface or basic type, `is in` a unit) - an interface by the classes that declare
it (`IEnumerable` also takes arrays, buffers and strings), `T` by its `where` (a single `is of` a class also gives the native type of an object of
it). Type arguments do not exist at run time, so `T` without `where`, type parameters of methods, `any` and `class` stay unchecked. Their type "object of the class or undefined" reaches the
fields and methods directly, too - the object is tested to be one (`kind == K_Class`), and `undefined` takes the helper, which reports it. A
comparison with `undefined` of a number, a bool or an object of a known class is a test of the kind (unless a class overloads `==`). A linked
list walked through typed parameters and fields (`Step(Body first, float dt)`, `b.pos.x`, `b = b.next`) runs a third fewer instructions.
Field access on something that is not an object (`undefined.x`, `5.x = 1`) is an `ArgumentException` in the native code as in the VM.
A scope that owns nothing (nothing in it creates an object or a temporary string) keeps no bookkeeping: hoisting a value out of it is
dropped, and so is its list (`leave` in every loop iteration) and that of the function when only the hoist needed it. In the linked list above the
instructions go from 685M to 418M (-39%).

Unboxed C++ locals (`int64_t`/`double` instead of `Value`) were measured and left out: with wrapper types for the typed locals the number of
instructions of a loop is the same to the instruction (62.0M each), and so is a loop over globals of the main program compared with locals
(13.0M) - the C++ compiler already keeps the fields of a `Value` that do not escape in registers and drops the stores of `kind` and `unit`.
The unit stays static in the type either way.

Typed opcodes in the VM were measured and left out, too: an instruction of the VM costs about 20 ns on the test machine whatever it does
(`s = t` - load, hoist, store - takes 65 ns, `s = s + t` 120 ns), so the test of the kinds in `Add`/`Lt`/... is a small part of it; what
costs is the dispatch of each instruction and the copying of `Value`s on the stack. Two cheap things were done instead: `HoistValue` (every
assignment) and the guards of the prologue (`GuardLocal`, `GuardLocalType`) go through the fast `Step` instead of `Execute`, and the main loop
(`RunLoopCore`, entered once) is compiled fully optimised straight away (about 4 % on the benchmarks). What would really help the VM is fewer
instructions (more fused ones, or registers instead of the stack) - a step of its own.

## Decisions

* **TypeChecking mode `lax` | `strict`.** Project-wide (project setting), per file (`#lax` / `#strict`), or by compiler switch. `lax`: an incompatible assignment, argument or return between two
  known types is a warning; `strict`: an error. `dynamic` values never report in either mode.
* **`var` is inferred from the initializer only.** The initializer may be a value, another variable, a return value, or `undefined!TypeName` (a typed null). `var` without an initializer is
  illegal (unlike a declaration with a type). The type of a target never changes.
* **Assignability.** `int` -> `float` is implicit (coerced). Everything converts implicitly to `string` (a `ToString` like in C#). Anything else between known types is a conflict.
* **Units** (step 4): if both types are known and only the unit is not, the site is a *unit adaptation* `b = AdaptUnit(a, targetUnit)` with three outcomes: same unit -> `a` unchanged;
  same dimension -> `a * factor`; otherwise `UnitMismatchException`. The factor is cached per (source unit, target unit) at the site; where the source unit is also static the whole thing
  folds at compile time (no-op or a constant multiply). A variable whose unit is statically known does not need a unit field in its unboxed representation at all.
