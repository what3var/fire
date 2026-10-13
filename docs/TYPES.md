# Gradual typing (plan)

Goal: a leaner and faster native backend (and VM) by proving types where the program states them, without losing what the dynamic language can do today
(reflection, properties by name, probes/bindings, units from data).

## Where we are (measured)

* The parser already keeps `TypeRef` for declarations, parameters, return types, fields, `catch` and `is of` (`src/fire/Ast/TypeRef.cs`), and `var` exists.
* **Annotations are not enforced.** `int x = 3; x = "hello"` and `int y = "text"` run and print the string; a parameter `int a` accepts `"s"`. Types are documentation only.
* The native backend therefore treats every value as a tagged `Value`; every method call goes through `call_<name>_<argc>` (a switch over all registered classes), every field
  access through `gf_/sf_/fp_<name>` (a switch over class ids). In the 3D sample that is about a quarter of the generated lines; the C++ compiler already drops the unused ones.

## Plan

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
(`FIRE_TYPECHECK_ALL=1` does the same for a build). Properties have no unit yet (`float X : m { ... }` is to come with the unit step), lengths of Scene3D are plain `float`.

Step 3a: arrays (element types from declarations, `new`, literals; elements, `length`, `foreach`), indexers (`operator[]`), strings (`s[i]` is a `char`, `Length`), typed
lambdas (parameter and result types from the target or from the lambda's own declaration, arity of calls and assignments), `fire` blocks (with their `taking`/`with`
variables), `class extends`, interface signatures (`TC004`/`TC005`) and calls of lambdas in fields and variables are checked.

Known gaps: generics (type arguments in annotations, `List<T>`, `foreach` over an `IEnumerable<T>`), units, properties set through `this`-less access.

## Decisions

* **TypeChecking mode `lax` | `strict`.** Project-wide (project setting), per file (`#lax` / `#strict`), or by compiler switch. `lax`: an incompatible assignment, argument or return between two
  known types is a warning; `strict`: an error. `dynamic` values never report in either mode.
* **`var` is inferred from the initializer only.** The initializer may be a value, another variable, a return value, or `undefined!TypeName` (a typed null). `var` without an initializer is
  illegal (unlike a declaration with a type). The type of a target never changes.
* **Assignability.** `int` -> `float` is implicit (coerced). Everything converts implicitly to `string` (a `ToString` like in C#). Anything else between known types is a conflict.
* **Units** (step 4): if both types are known and only the unit is not, the site is a *unit adaptation* `b = AdaptUnit(a, targetUnit)` with three outcomes: same unit -> `a` unchanged;
  same dimension -> `a * factor`; otherwise `UnitMismatchException`. The factor is cached per (source unit, target unit) at the site; where the source unit is also static the whole thing
  folds at compile time (no-op or a constant multiply). A variable whose unit is statically known does not need a unit field in its unboxed representation at all.
