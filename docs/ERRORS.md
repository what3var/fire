# Error codes and warnings

Every error of the compiler has a **code**: `E` and four digits, `E3012`. The first digit is the part of the compiler it comes from (below), the rest numbers the messages of that part. The editor
shows the code in the **Error List** and in the **Build Log**, the command line prints it (`error E4022: Unknown identifier 'x' (line 3)`). A code stays with its message: when the wording of a message is
improved the code is the same, so a search for `E4022`, a note in a bug report or a link keeps working. A message that is not in the list has the general code of its part (`E1000`, `E2000`, ... `E7000`).

A **warning** is something that the developer writes into the code, `warn 123: "text"` (SPEC 8.1.8): it is reported with the code `W123` and the text; it is no code, so nothing of it is compiled.
A `warn` in a class or in a member is reported where the class or the member is **used** (`new`, a call, a property access) - never when it is not:

```
class Cache {
    Flush() {
        warn 204: "Flush is obsolete, use Clear() - it goes away in the next version"
        this.Clear()
    }
}

var c = new Cache()
c.Flush()                // warning W204 here: Flush is obsolete, use Clear() ... (Cache.Flush)
```

Native builds add their own: `E7001` (the generator cannot translate something for the target), `E7002` (the C++ compiler reported errors - its messages are in the Build Log), `E7003` (the
configuration of the native build, a toolchain that is missing).

## The list

### E1xxx - Lexer: the text of the program (characters, literals, comments)

| Code | Message |
|---|---|
| `E1000` | Another error in the text of the program (general) |
| `E1001` | Unterminated block comment |
| `E1002` | Expected at least one valid digit after '…' |
| `E1003` | Unterminated string literal |
| `E1004` | String literals must not span multiple lines |
| `E1005` | Unterminated format string |
| `E1006` | Format strings must not span multiple lines |
| `E1007` | Unterminated expression in a format string |
| `E1008` | Unterminated format specifier in a format string |
| `E1009` | Unterminated char literal |
| `E1010` | A char literal must contain exactly one character |
| `E1011` | Unexpected character '…' |

### E2xxx - Preprocessor: directives (`#include`, `#if`, `#import`, ...)

| Code | Message |
|---|---|
| `E2000` | Another error in a directive (general) |
| `E2001` | '#include' expects a string as the path, not … (line …). |
| `E2002` | '#include "…"' - file not found: '…' (line …). |
| `E2003` | Circular include of '…' (chain: … -> …). |
| `E2004` | '…' could not be read: … |
| `E2005` | '#using' expects a (possibly dotted) namespace name, not '…' (line …). |
| `E2006` | Resource file not found: '…' ('…', line …). |
| `E2007` | '#…' expects no parameters (line …). |
| `E2008` | Invalid parameters for '#…' (line …): … |
| `E2009` | Unexpected token '…' in the parameters of '#…' (line …). |
| `E2010` | '#…' expects … parameters, got … (line …). |
| `E2011` | Expected a number after '-' in the parameters of '#…' (line …). |
| `E2012` | Expected a literal value (string/number/char/bool/undefined) in the parameters of '#…', not '…' (line …). |
| `E2013` | '#if' expression '…': unexpected character '…' (line …). |
| `E2014` | '#if' expects a condition (line …). |
| `E2015` | '#if' expression '…': unexpected '…' (line …). |
| `E2016` | '#if' expression '…' ends too early (line …). |
| `E2017` | '#if' expression '…': ')' is missing (line …). |
| `E2018` | '#elif' after '#else' (line …). |
| `E2019` | A second '#else' for the '#if' of line … (line …). |
| `E2020` | '#endif' without '#if' (line …). |
| `E2021` | '#…' expects a name (line …). |
| `E2022` | #error … (line …) |
| `E2023` | The '#if' of line … has no '#endif'. |
| `E2024` | '#…' without '#if' (line …). |

### E3xxx - Parser: syntax

| Code | Message |
|---|---|
| `E3000` | Another syntax error (general) |
| `E3001` | 'class extends …' - class '…' is not known in the same program (extensions cannot create a new class). |
| `E3002` | …static method '…' is not allowed - extensions of base types consist of instance methods only. |
| `E3003` | …Operators cannot be overloaded for base types. |
| `E3004` | …field '…' is not allowed - extensions of base types may only contain methods. |
| `E3005` | …property '…' is not allowed - extensions of base types may only contain methods. |
| `E3006` | …a constructor is not allowed - extensions of base types may only contain methods. |
| `E3007` | …a destructor is not allowed - extensions of base types may only contain methods. |
| `E3008` | …only methods are allowed. |
| `E3009` | 'try' needs at least one 'catch' block or 'finally' |
| `E3010` | Expected 'var' or a type declaration after 'readonly' |
| `E3011` | 'byte' already has a fixed width of 8 bits - no additional '[...]' may follow |
| `E3012` | For variables, fields and parameters an array is written with the brackets after the name ('int values[]'); 'int[]' as a type only exists as the return type of a method or property |
| `E3013` | Unknown preprocessor directive '#…' (known: '#extern "libName"', '#noshadow', '#nosync', '#timeout value') |
| `E3014` | Multiple 'case default' in one switch are not allowed |
| `E3015` | 'with' does not exist for 'fire global' |
| `E3016` | 'with' was already used in this 'fire' |
| `E3017` | 'where …' does not refer to a declared type parameter (declared: …) |
| `E3018` | Multiple 'where …' clauses for the same type parameter |
| `E3019` | Expected 'of' or 'in' after 'is' in a where condition |
| `E3020` | 'get' is already defined for property '…' |
| `E3021` | 'set' is already defined for property '…' |
| `E3022` | Expected 'get' or 'set' in the property body |
| `E3023` | Property '…' needs at least 'get' or 'set' |
| `E3024` | 'readonly' is only valid for fields, not for methods |
| `E3025` | A generic type parameter head '<...>' must be followed by a parameter list '(...)' |
| `E3026` | 'readonly' is not valid for properties - a property without 'set' is already read-only |
| `E3027` | An array field is written with the brackets after the name ('int values[]'); 'int[]' as a type only exists as the return type of a method or property |
| `E3028` | 'operator[]' needs either 1 parameter (read access: index) or 2 parameters (write access: index, value) |
| `E3029` | 'operator…' needs exactly 1 parameter (the right operand - 'this' is the left one) |
| `E3030` | Expected an overloadable operator after 'operator' ('[]', '+', '-', '*', '/', '%', '^', '&', '\|', '#', '<<', '>>', '==', '!=', '<', '<=', '>', '>=') |
| `E3031` | 'ref' parameters are only possible in methods and constructors |
| `E3032` | A 'ref' parameter cannot have a default value |
| `E3033` | Invalid assignment target |
| `E3034` | Expected 'in', 'of', 'from' or 'under' after 'is' |
| `E3035` | 'try sync globals' does not exist |
| `E3036` | Expected 'sync', 'process' or a function call after 'try' |
| `E3037` | '.' at the start of an expression is only valid inside a 'with' block |
| `E3038` | Expected '[' after 'byte' in 'new' |
| `E3039` | 'new byte[...]' expects exactly ONE size (no multidimensional byte buffer, no unspecified size) |
| `E3040` | Expected '[' after the array element type in 'new' |
| `E3041` | Unexpected token '…' (…) |
| `E3042` | Invalid expression in a format string: … |
| `E3043` | Unexpected further tokens after the expression in a format string interpolation (is an operator missing, or is a ':' not enclosed in parentheses by mistake?) |
| `E3044` | 'probe' expects a member: 'probe object.member changed ...' (or 'object.*' for all) |
| `E3045` | Expected 'changed' or 'changing' after the target of 'probe' |
| `E3046` | Expected ';' or a line break between statements |
| `E3047` | Expected '}' at the end of the block |
| `E3048` | Expected 'catch' |
| `E3049` | Expected '(' after 'catch' |
| `E3050` | Expected an identifier in catch(...) |
| `E3051` | Expected ')' after the catch parameters |
| `E3052` | Expected 'try' |
| `E3053` | Expected 'readonly' |
| `E3054` | Expected 'var' |
| `E3055` | Expected a variable name |
| `E3056` | Expected a unit name after ':' |
| `E3057` | Expected an identifier |
| `E3058` | Expected a type name |
| `E3059` | Expected '>' after the type of 'lambda …<...>' |
| `E3060` | Expected a bit width (8/16/32/64) |
| `E3061` | Expected ']' after the bit width |
| `E3062` | Expected '>' after the lambda parameter types |
| `E3063` | Expected ']' |
| `E3064` | Expected 'if' |
| `E3065` | Expected '(' after 'if' |
| `E3066` | Expected ')' after the if condition |
| `E3067` | Expected 'while' |
| `E3068` | Expected '(' after 'while' |
| `E3069` | Expected ')' after the while condition |
| `E3070` | Expected 'for' |
| `E3071` | Expected '(' after 'for' |
| `E3072` | Expected ';' after the for initializer |
| `E3073` | Expected ';' after the for condition |
| `E3074` | Expected ')' after the for clauses |
| `E3075` | Expected 'foreach' |
| `E3076` | Expected '(' after 'foreach' |
| `E3077` | Expected 'in' in foreach |
| `E3078` | Expected ')' after the foreach clauses |
| `E3079` | Expected 'return' |
| `E3080` | Expected 'throw' |
| `E3081` | Expected 'extern' |
| `E3082` | Expected a function name after 'extern' |
| `E3083` | Expected '#' |
| `E3084` | Expected a library name (string) after '#extern' |
| `E3085` | Expected 'unsafe' |
| `E3086` | Expected 'with' |
| `E3087` | Expected '}' at the end of the with block |
| `E3088` | Expected 'switch' |
| `E3089` | Expected '(' after 'switch' |
| `E3090` | Expected ')' after the switch expression |
| `E3091` | Expected 'case' in the switch body |
| `E3092` | Expected ':' after 'case default' |
| `E3093` | Expected ':' after the case condition |
| `E3094` | Expected '}' at the end of the switch |
| `E3095` | Expected 'fire' |
| `E3096` | Expected 'sync' |
| `E3097` | Expected an identifier after 'taking' |
| `E3098` | Expected an identifier after 'with' |
| `E3099` | Expected '(' after '…' |
| `E3100` | Expected ')' after 'fire …(...)' |
| `E3101` | Expected 'terminate' |
| `E3102` | Expected '(' after 'terminate' |
| `E3103` | Expected ')' after the terminate argument |
| `E3104` | Expected 'process' |
| `E3105` | Expected '(' after 'catch terminate' |
| `E3106` | Expected a parameter name in 'catch terminate(...)' |
| `E3107` | Expected ')' after 'catch terminate(...)' |
| `E3108` | Expected 'threads' or 'terminate' after 'catch' |
| `E3109` | Expected '(' after 'catch threads' |
| `E3110` | Expected a type name in 'catch threads(...)' |
| `E3111` | Expected a parameter name in 'catch threads(...)' |
| `E3112` | Expected ')' after 'catch threads(...)' |
| `E3113` | Expected 'actor' |
| `E3114` | Expected 'class' |
| `E3115` | Expected a class name |
| `E3116` | Expected '}' at the end of the class |
| `E3117` | Expected '>' after the type parameter list |
| `E3118` | Expected '>' after the nested type argument list |
| `E3119` | Expected a type parameter name after 'where' |
| `E3120` | Expected 'is' in a where condition |
| `E3121` | Expected a unit name (as a string) after 'is in' |
| `E3122` | Expected 'extends' |
| `E3123` | Expected the name of the class to extend |
| `E3124` | Expected '}' at the end of the extension |
| `E3125` | Expected 'namespace' |
| `E3126` | Expected '}' at the end of the namespace |
| `E3127` | Expected … after '.' |
| `E3128` | Expected 'interface' |
| `E3129` | Expected an interface name |
| `E3130` | Expected a method name |
| `E3131` | Expected '}' at the end of the interface |
| `E3132` | Expected 'enum' |
| `E3133` | Expected an enum name |
| `E3134` | Expected an enum member name |
| `E3135` | Expected '}' after the enum members |
| `E3136` | Expected '}' at the end of the property |
| `E3137` | Expected a field or method name |
| `E3138` | Expected 'operator' |
| `E3139` | Expected ']' after 'operator[' |
| `E3140` | Expected 'construct' |
| `E3141` | Expected 'base' after ':' in the constructor |
| `E3142` | Expected 'destruct' |
| `E3143` | Expected '(' after 'destruct' |
| `E3144` | 'destruct' takes no parameters |
| `E3145` | Expected a parameter name |
| `E3146` | Expected '(' |
| `E3147` | Expected ')' after the parameter list |
| `E3148` | Expected ')' after the argument list |
| `E3149` | Expected a unit name after 'is in' |
| `E3150` | Expected a name after '.' |
| `E3151` | Expected ']' after the index |
| `E3152` | Expected ']' after the array literal |
| `E3153` | Expected ')' after the parenthesized expression |
| `E3154` | Expected 'func' |
| `E3155` | Expected a member name after '.' |
| `E3156` | Expected '=>' in the lambda |
| `E3157` | Expected the text of the warning (a string) after 'warn <id>:' |
| `E3158` | The id of a warning ('warn <id>: "text"') is a whole number from 0 to 99999 |
| `E3159` | The target of '{*}' must not contain a call, '++', '--' or an assignment (write the assignment out) |

### E4xxx - Resolver: names, types, declarations

| Code | Message |
|---|---|
| `E4000` | Another error in names, types or declarations (general) |
| `E4001` | '…' is already declared as extern |
| `E4002` | '…' is already declared as an enum |
| `E4003` | '…' is already declared as a class |
| `E4004` | Enum member '….…' is already declared |
| `E4005` | Enum member '….…': the value must be an int literal (arbitrary expressions are not evaluated here). |
| `E4006` | Class '…' is already defined |
| `E4007` | Interface '…' is already defined |
| `E4008` | Class '…' cannot have multiple base classes ('…' and '…') |
| `E4009` | '…' of class '…' is neither a known class nor a known interface |
| `E4010` | Class '…' does not implement interface '…' completely (missing method '…' with … parameter(s)) |
| `E4011` | Class '…' is not generic, so it does not accept type arguments in angle brackets |
| `E4012` | Class '…' is generic with … type parameter(s) - 'new …<...>' needs explicit type arguments for it (got: …) |
| `E4013` | Type argument '…' for type parameter '…' of '…' does not satisfy any of the 'where' conditions |
| `E4014` | 'lambda …<...>' (selector) needs the reflection library: #import "reflection" |
| `E4015` | Unknown type '…' in 'lambda …<…>' |
| `E4016` | Unknown type '…' |
| `E4017` | A bit width is only valid for 'int'/'float', not for '…' |
| `E4018` | Invalid bit width … (allowed: 8/16/32/64) |
| `E4019` | '…' is already declared in this scope |
| `E4020` | '…' is registered as 'tryable' - it can only be called with 'try …(...)', not as a direct call. |
| `E4021` | '…' is an instance member - it is not reachable in a static method or static field initializer without a bound 'this' |
| `E4022` | Unknown identifier '…' |
| `E4023` | 'readonly …' needs an initializer (or an array size) |
| `E4024` | '…[…]' expects … elements, but the array literal initializer has … |
| `E4025` | 'return' outside of a function/method |
| `E4026` | 'break' outside of a loop ('while'/'for'/'foreach') |
| `E4027` | 'break' cannot be used to leave a 'finally' block (a limitation of this stage - see BYTECODE.md) |
| `E4028` | 'continue' outside of a loop ('while'/'for'/'foreach') |
| `E4029` | 'continue' cannot be used to leave a 'finally' block (a limitation of this stage - see BYTECODE.md) |
| `E4030` | Unknown exception type '…' |
| `E4031` | Internal error: 'class extends …' was not merged (the program must be produced by Parser.Parse()/ParseMultiple()). |
| `E4032` | Unexpected statement … at this position |
| `E4033` | A constructor with … parameter(s) is already defined in this class (an overload needs a different number of parameters). |
| `E4034` | Method '…' with … parameter(s) is already defined in this class (an overload needs a different number of parameters). |
| `E4035` | Parameter '…' without a default value must not follow an optional parameter (optional parameters must be contiguous at the end). |
| `E4036` | 'base(...)' is only valid in a class with a base class |
| `E4037` | 'this' is not valid in a static method or static field initializer (no instance bound) |
| `E4038` | 'base' is not valid in a static method (no instance bound) |
| `E4039` | 'base' is only valid inside a class with a base class |
| `E4040` | 'take' is only valid as an argument of a call or on the right of '=' / 'var x =' (SPEC 2.2) |
| `E4041` | '…' is only valid inside an 'unsafe' block |
| `E4042` | '…' has no member '…' |
| `E4043` | 'take' needs a holder: a variable, a field or an array element (not a pointer) |
| `E4044` | Unknown class '…' |
| `E4045` | '…' is not generic, so it does not accept type arguments in angle brackets |
| `E4046` | 'new Type[...]' needs a size at least for the first dimension (e.g. 'new int[3]' or 'new int[3][]' - not 'new int[]'). |
| `E4047` | Unknown expression type … |
| `E4048` | 'try …(...)' expects … |
| `E4049` | 'try' before a call expects a direct call of a registered function (no method call except obj.TakeLocal/TakeUpwards/TakeGlobal/TakeTo, no lambda call) |
| `E4050` | '…' is a COPY of the outer variable inside the lambda (capture) and cannot be assigned there (declare a new local variable with a different name) |
| `E4051` | '…' is 'readonly' and cannot be assigned after its declaration |
| `E4052` | '….…' is 'readonly' and can only be assigned inside a constructor of the declaring class |
| `E4053` | '*' as an assignment target is only valid inside an 'unsafe' block |
| `E4054` | Invalid assignment target |
| `E4055` | '…' was already captured in this 'fire' |
| `E4056` | Class '…' has no static member '…' |
| `E4057` | '….…' is an instance member - it needs an object, not the class name |
| `E4059` | Field '…' of class '…' needs a type: a field is not declared with 'var' (use the type it holds, or 'any' if every type is allowed) |
| `E4060` | Property '…' of class '…' needs a type (use the type it holds, or 'any' if every type is allowed) |
| `E4061` | '…' is generic with … type parameter(s), not … |
| `E4058` | 'var …' needs an initializer: the type of a 'var' comes from its value (or declare it with a type: 'int …') |

### E5xxx - Bytecode compiler

| Code | Message |
|---|---|
| `E5000` | Another error while the bytecode is made (general) |
| `E5001` | Only the first 16 parameters of a method can be 'ref'. |
| `E5002` | Internal error: 'class extends …' was not merged (the program must be produced by Parser.Parse()/ParseMultiple()). |
| `E5003` | Statement … is not supported. |
| `E5004` | 'base' is only valid as 'base.Method(...)', not as a standalone value. |
| `E5005` | TryCallExpr without a resolved TryableNative reference - the resolver should have caught this. |
| `E5006` | Expression type … is not supported. |
| `E5007` | A lambda can capture at most 255 outer variables. |
| `E5008` | A 'ref' argument is only possible for the first 16 arguments of a call. |
| `E5009` | `flat`/`copy` as an argument is only possible for the first 16 arguments of a call. |
| `E5010` | 'resume' expects at most one argument (the resume value). |
| `E5011` | 'base.Method(...)' outside of a class with a base class - the resolver should have caught this. |
| `E5012` | '…' is a native function and can only be called directly (…(...)), not used as a value. |
| `E5013` | '…' is a function declared extern and can only be called directly, not used as a value. |
| `E5014` | Invalid assignment target for the bytecode compiler. |
| `E5015` | Cannot assign to '…' - it is a native function. |
| `E5016` | Cannot assign to '…' - it is a function declared extern. |
| `E5017` | 'take' needs a holder: '…' is not a variable or a field. |
| `E5018` | Invalid target for '++'/'--' in the bytecode compiler. |
| `E5019` | '++'/'--' on '…' is not possible. |
| `E5020` | '&' can only be applied to local/global variables or object fields. |
| `E5021` | '&' can only be applied to variables or object fields (not a valid address target). |
| `E5022` | Coercion target … is not supported. |

### E6xxx - Linker: the program and its libraries put together

| Code | Message |
|---|---|
| `E6000` | Another error while the program and its libraries are put together (general) |
| `E6001` | The library '…' is not available on the target '…' (available: …). |
| `E6002` | The native function '…' of a package is written in C++: it is only available in a native build (`fire native`), not in the virtual machine. |

### E7xxx - Native build

| Code | Message |
|---|---|
| `E7000` | Another error of the native build (general) |
| `E7001` | The native generator cannot translate this program |
| `E7002` | The C++ compiler of the native build reported errors |
| `E7003` | The configuration of the native build cannot be used |

### TCxxx - Type checker (docs/TYPES.md)

A warning in lax mode, an error in strict mode (`--types`, the project setting `typeChecking`, `#lax` / `#strict` in a file); the code is the same.

| Code | Message |
|---|---|
| `TC001` | Cannot assign a value of type '…' to '…' of type '…' (assignment, initializer, field) |
| `TC002` | Cannot assign a value of type '…' to parameter '…' of '…' of type '…' (an argument that does not fit) |
| `TC003` | Cannot assign a value of type '…' to the return value of type '…' |
| `TC004` | Parameter '…' of '…' has no type and is assumed to be 'any'. Please add a type (use 'any' if every type is allowed). |
| `TC005` | Method '…' has no return type and is assumed to return 'any'. Please add one (use 'undefined' if it returns nothing). |

`…` stands for a part of the message that varies (a name, a number).
