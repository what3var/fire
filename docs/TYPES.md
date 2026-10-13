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

## Open design questions

* Mismatch policy in step 1: warning first, error later (proposed), or error at once?
* `var`: infer from the initializer only (proposed), or also from later assignments?
* Is an `int` assignable to `float` implicitly (as in the VM today)? Proposed: yes, `float` accepts `int`.
