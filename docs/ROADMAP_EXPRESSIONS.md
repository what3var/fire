# Roadmap: expression trees (`lambda expression<T>`)

Status: steps 1-3 built (see "Status" at the end); prepared in parallel to [ROADMAP_DATA.md](ROADMAP_DATA.md) (the query builder of `Data` is its first user; LINQ, the UI
bindings and RPC filters can use it as well).

## The idea

A lambda passed to a parameter of type `lambda expression<T>` is not (only) compiled to code: the method gets a **tree** that describes the body -
what it reads, compares and calls - and can translate it (SQL), evaluate it (in memory) or show it. It works like the selectors that exist already
(`lambda member<T>`, [selectors](language/16-probes-and-reflection.md#selectors)): there the compiler replaces the lambda by `Reflect.SelectorOf(...)` with a path it
worked out at compile time (`Compiler.TrySelectorPath`); here it builds a whole tree.

```fire
class Query<T> {
    Query<T> Where(lambda expression<T> condition) {
        print(condition.Body)          // (p.age >= 18) && p.name.StartsWith(prefix)
        ...
    }
}
var prefix = "A"
people.Where(p => p.age >= 18 && p.name.StartsWith(prefix))
```

## Syntax and types

- `lambda expression<T>` - one parameter of type `T`; `lambda expression<A, B>` - two (joins: `(p, o) => p.id == o.personId`). The result type is that of
  the body (`bool` for a condition, a member for an order, a record for a projection); `bool lambda expression<T>` fixes it (like `bool lambda<T>`).
- Only a lambda written at the call goes there (`x => ...` or `(a, b) => ...`); a lambda in a variable, a method group or a lambda with a block body
  (`{ ... }`) is a compile error (`TC0xx: an expression tree needs a lambda with an expression body here`).
- Inside the method the parameter is an `Expression` (a lambda node: `Parameters`, `Body`, `ResultType`) - also `Invoke(args)`: the compiler compiles the
  lambda as well, so the tree can always be run (`data.memory`, LINQ on lists, a fallback).
- Nested lambdas inside the tree are trees too (`p => p.orders.Any(o => o.total > 100)`: the argument of `Any` is a lambda node).

## The nodes

All in the prelude (namespace `Expressions`, or in `reflection` - to be decided), immutable (records where possible), with `Kind`, `Type` (the type name
as the type checker knows it) and `ToString()` in fire syntax:

| Kind | Fields | From |
|---|---|---|
| `Lambda` | `Parameters` (list of `Parameter`), `Body` | the lambda itself, nested lambdas |
| `Parameter` | `Name`, `Index`, `Type` | `p` |
| `Member` | `Target` (node), `Name`, `Type`, `IsField`/`IsProperty` | `p.age`, `p.owner.name`, `s.Length` |
| `Constant` | `Value` | literals (`18`, `"A"`, `true`, `undefined`, enums, units `5m`) |
| `Captured` | `Name`, `Value` | a local, a parameter or a field (`this.limit`) of the enclosing code: its value **when the lambda is made** |
| `Unary` | `Op` (`!`, `-`), `Operand` | |
| `Binary` | `Op` (`==` `!=` `<` `<=` `>` `>=` `&&` `||` `+` `-` `*` `/` `%` `??`), `Left`, `Right` | |
| `Conditional` | `Test`, `IfTrue`, `IfFalse` | `a ? b : c` |
| `Call` | `Target` (node or undefined for static), `Method`, `DeclaringType`, `Arguments` | `s.StartsWith(x)`, `list.Contains(p.id)`, `M.Abs(x)`, `g.Count()` |
| `New` | `Type`, `Arguments` (constructor), or `Members` (name -> node, an initializer) | `new PersonName(p.id, p.name)` |
| `IsType` | `Operand`, `Type` | `x is of Dog` |
| `Index` | `Target`, `Index` | `row["name"]`, `arr[0]` |
| `Convert` | `Operand`, `Type` | `x!int`, an implicit widening the type checker made |

- `x == undefined` stays a `Binary` with a `Constant undefined` (SQL: `IS NULL`) - no special node.
- Units: a `Constant` of a unit keeps its unit; a comparison of two unit values has the converted value (as the type checker does it).
- Operator overloads: a `Binary` with `Method` set to the overload (a translator decides whether it knows it).

## Captured values

- A captured variable becomes a `Captured` node with its **value at the moment the lambda is made** (a copy for an object, like `taking`; a value as it is) -
  the tree never refers to the caller's variables. Its `Name` stays for `ToString()` and error messages.
- Member access on a captured object (`filter.minAge`) is evaluated when the lambda is made too: `Captured("filter.minAge", 18)` - the tree has only
  what depends on the parameters as nodes. (A rule the translators rely on: everything that does not depend on a parameter is a value.)
- A call that does not depend on a parameter (`DateTime.Now`, `Make(x)`) is evaluated then as well and becomes `Captured`. Calls that depend on a parameter
  stay `Call` nodes.

## Compiler

- **Parser:** `lambda expression<...>` as a type, like `lambda member<...>` (Parser.cs, `IsSelector`/`SelectorKind` - a new kind `expression`, or a flag of its own on
  `LambdaSignature`).
- **Type checker:** the body is checked as a normal lambda (same errors, same inference of the parameter types from `T`); then the **tree rules**:
  no assignment, no `++`, no `await`/`fire`, no block, no `take`/`handover`; a call is allowed - a translator decides later, the compiler only refuses what
  can never be a tree.
- **Code:** at the call site the compiler emits (a) the compiled lambda as usual and (b) the construction of the tree: the static part (nodes, member
  names, types) once per call site as a constant, the `Captured` values filled in each time the call is made. VM: a constant prototype plus a native
  `Expressions.Bind(prototype, values[], lambda)`; native: the same as static C data in the generator (CppGenerator), the values in an array.
- **Stripping:** the members and types a tree names are kept (like the literal names of reflection and the selector paths) - a translator reads them by
  reflection (`Type.Find(name)`).
- **Overloads:** a method can have `Where(bool lambda<T> f)` and `Where(lambda expression<T> e)`; a lambda literal prefers the expression overload where
  both fit? - to be decided (LINQ on lists would then get trees and must `Invoke` them; proposal: the expression overload only where the receiver asks for
  it, `Query<T>` of `data` has only the expression one).

## Library

- `Expression.Visit(visitor)` and a class `ExpressionVisitor` (one method per kind, default: visit the children) - the translators are visitors.
- `Expression.Invoke(args)` (the compiled lambda), `Expression.Compile()` gives the `lambda` itself.
- `ToString()` canonical (the same text in the VM and natively - the tests compare it), `Equals` structural (a translator can cache the SQL of a tree
  shape: the same tree with other captured values has the same SQL).
- `Expression.Parameters`, `.Body`, `.Captures` (all `Captured` nodes in order - the parameters of the SQL).
- `Expressions.Replace(tree, node, with)` and `Expressions.And(a, b)` / `Or` for libraries that combine conditions (`query.Where(a).Where(b)`).

## Steps

1. Parser and type checker: the type, the tree rules, the errors (with tests of the diagnostics).
2. VM: tree construction at the call site (constants + captured values), the node classes, `ToString`, `Invoke`, `Visit`.
3. Native: the same trees from the generator (C data, captured values per call), VM == native in the native test cases (`ToString` of many trees).
4. Stripping and reflection: names of the trees kept; `Type.Find` works for them at stripping 3.
5. Structural `Equals`/hash, `Captures`, `And/Or/Replace`.
6. LINQ (`packages/linq`): the expression overloads where they help (none needed at first).
7. Tooling: spark shows the tree of a lambda (hover: "expression tree"), completion inside it as in a normal lambda.

## Tests (examples)

```fire
class Probe<T> { static string Show(lambda expression<T> e) { return e.ToString() } }
class Person { int age; string name; List<Order> orders }
var min = 18
print(Probe<Person>.Show(p => p.age >= min && !(p.name == undefined)))   // p => ((p.age >= min) && !(p.name == undefined))   (min = 18)
print(Probe<Person>.Show(p => p.orders.Any(o => o.total > 10)))         // p => p.orders.Any(o => (o.total > 10))
```

- every node kind once; nested lambdas; captured locals, fields, member chains of captured objects, calls without a parameter (evaluated once);
- the errors (block body, assignment, a lambda in a variable);
- `Invoke` gives the same as the compiled lambda; VM == native for `ToString` and `Invoke`; stripping 3 keeps the named members.

## Status

- **Built (steps 1-3):** `lambda expression<T>` / `<A, B>` (Parser: `LambdaSignature.IsExpression`; a lambda with an expression body:
  `LambdaExpr.ExpressionBody`). The type checker (`InferTreeArg`) checks the lambda against the shape, applies the tree rules (TC030) and keeps the type of
  every part (`ResolveResult.Trees`) - for method calls, static calls, constructors. The compiler (Compiler.Trees.cs) turns the lambda into
  `__builder(...).__Attach(lambda)`: the builder is a lambda with the same parameters and captures whose body is `new BinaryExpression(...)` and so on -
  what does not depend on a parameter is evaluated there (inside a nested lambda through its loads) and copied into a `CapturedExpression`. The nodes are
  prelude classes (Expression and its kinds, ExpressionVisitor, Captures, Invoke, Compile); because they are built by ordinary code, the native backend
  builds the same trees (VM == native in the test "Expression trees: ..."). A call through `any` gets only the lambda: `Expression.__Require` in the
  method throws the ArgumentException. Docs: docs/language/12-lambdas.md "Expression trees".
- **Decided while building:** a tree assigned to a `lambda` (variable, argument, return) is its compiled lambda (`LambdaExpression` -> `.Compiled`,
  TypeChecker `CheckAssignable`); a lambda is not a tree. The values are taken when the call evaluates its arguments (the builder runs right there); the static part is not yet a
  constant per call site (the nodes are made at each call).
- **Open:** step 4 (stripping keeps the nodes already: they are made with `new`; the members a tree names are not kept on purpose yet), step 5
  (structural Equals/hash, And/Or/Replace), step 6 (LINQ), step 7 (spark), overloads `lambda<T>` / `lambda expression<T>`. (Trees in library code: done - the type checker checks the libraries
  always now; a tree parameter can be passed on to another `lambda expression<T>`.)
