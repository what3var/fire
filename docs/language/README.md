# The fire language

This is the reference of the fire programming language: one page per topic, each with examples that run as they are and a few exercises. The pages
build on each other, but each can be read on its own.

New to fire? Start with **Help > First Steps** in the editor, then come back here.

## Contents

1. [Basics](01-basics.md) - programs, statements, comments, variables, literals, `print`
2. [Types](02-types.md) - the basic types, inference, the type checker, conversions, bit widths
3. [Operators](03-operators.md) - precedence, arithmetic, comparisons, logic, tests
4. [Control flow](04-control-flow.md) - `if`, loops, `break`/`continue`, `switch`, `with`
5. [Units](05-units.md) - numbers with units, conversion, units in declarations
6. [Strings and characters](06-strings.md) - texts, format strings, string and char methods
7. [Arrays and lists](07-arrays-and-lists.md) - arrays, `List<T>`, queries, byte buffers
8. [Classes](08-classes.md) - fields, methods, constructors, inheritance, properties, operators
9. [Interfaces and generics](09-interfaces-and-generics.md) - interfaces, `IEnumerable`, generic classes and methods
10. [Enums and namespaces](10-enums-and-namespaces.md) - `enum`, `namespace`, `#using`
11. [Extensions](11-extensions.md) - `class extends` for classes and base types
12. [Lambdas](12-lambdas.md) - functions as values, captures, `on`
13. [Ownership](13-ownership.md) - owners, lifetime, `TakeTo`, `delete`, `copy`/`flat`
14. [Exceptions](14-exceptions.md) - `throw`, `try`/`catch`/`finally`, `resume`
15. [Threads and actors](15-threads-and-actors.md) - `fire`, `taking`/`sync`, actors, `leave`/`terminate`
16. [Probes and reflection](16-probes-and-reflection.md) - watching members, describing classes at run time
17. [Directives, warnings and documentation comments](17-preprocessor.md) - `#import`, `#if`, `warn`, `///`
18. [Native code](18-native-code.md) - `extern`, `unsafe` pointers, native builds
19. [The standard library](19-standard-library.md) - what is always there and what `#import` adds

[Roadmap](roadmap.md) - what is planned for the language and its tools.

## Conventions on these pages

- `// 42` after a statement shows what it prints.
- Every code block is a complete program; you can copy it into a new file in the editor and run it with F5.
- Words in `this style` are code: keywords, names, values.
