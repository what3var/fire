# Regular expressions (`#import "regex"`)

The package `fire-regex` is a small regular expression engine written in fire: the pattern is parsed and compiled into the instructions of a little
backtracking machine, which runs them with a stack of its own (a deep pattern does not grow the call stack). The syntax is a **subset of .NET's**
(and of what most engines share); the results are the same as .NET's for the patterns of that subset. It is the same code in the VM and in a native
build.

```fire
#import "regex"

var date = new Regex("(?<year>\\d{4})-(\\d\\d)-(\\d\\d)")
var m = date.Match("released on 2026-11-23")
print(m.Value + " " + m.Group("year") + " " + m.Group(2))    // 2026-11-23 2026 11
print(date.Replace("2026-11-23", "$3.$2.${year}"))           // 23.11.2026
foreach (x in date.Matches(text)) { print(x.Index) }
print(Regex.Test("kit-1.2", "^[A-Za-z0-9][A-Za-z0-9._-]*$"))  // True
print("|".Join(new Regex(",\\s*").Split("a, b,c")))           // a|b|c
```

## The API

| | |
|---|---|
| `new Regex(pattern)`, `new Regex(pattern, options)` | compiles the pattern (a `RegexException` when it cannot be read); options: `Regex.IgnoreCase`, `Regex.Multiline`, `Regex.Singleline`, joined with `\|` |
| `IsMatch(text)` | does it match somewhere? |
| `Match(text, start = 0)` | the first match at or after `start`: a `RegexMatch` (`Success` is `false` when there is none) |
| `Matches(text)` | all matches from left to right that do not overlap (after an empty match the search goes on one character further) |
| `Replace(text, replacement)`, `Replace(text, replacement, count)` | every match (or the first `count`) replaced; in the replacement `$0` or `$&` is the match, `$1` ... `$99` a group, `${name}` a named group (or `${1}`), `$$` a `$` |
| `Split(text)` | the parts between the matches; the texts of the capturing groups of each match go in between (as in .NET) |
| `Regex.Escape(text)` | the text with every character that means something in a pattern escaped |
| `Regex.Test(text, pattern[, options])`, `Regex.Find(text, pattern[, options])`, `Regex.ReplaceAll(text, pattern, replacement)` | one-off uses without keeping the `Regex` (the names differ from the methods: fire does not allow a static and an instance method of one name) |
| `GroupCount`, `Pattern` | the number of capturing groups with group 0; the pattern |
| `RegexMatch` | `Success`, `Index`, `Length`, `Value`, `GroupCount`, `Group(n)`, `Group(name)` (`""` for a group that did not take part), `GroupIndex(n)` (-1), `Has(n)` |
| `Regex.MaxSteps` | how many steps one call may take (default 1 000 000): more is a `RegexException` instead of a program that seems to hang on a pattern that backtracks too much |
| `RegexException` | a pattern that cannot be read (`Invalid pattern '(ab' at offset 3: a missing ')'.`), or a match that needs more than `MaxSteps` steps |

## The syntax

| | |
|---|---|
| characters | everything that is not special stands for itself; `\` before a character that is not a letter or digit makes it plain (`\.`, `\*`, `\\`) |
| escapes | `\n` `\r` `\t` `\f` `\v` `\e` `\0`, `\xHH`, `\uHHHH` |
| `.` | any character but `\n` (with `Singleline` any character) |
| classes | `[abc]`, `[a-z0-9_]`, `[^...]` (negated); `\d` (the digits 0-9), `\w` (letters, digits and `_` - letters of all scripts), `\s` (white space), `\D` `\W` `\S`; inside the brackets as well (`[\w.-]`); `]` first in a class and `-` first or last are themselves |
| groups | `(...)` captures (numbered from 1 by their `(`), `(?<name>...)` or `(?'name'...)` captures with a name, `(?:...)` does not capture |
| alternatives | `a\|b\|c` - the first that lets the whole pattern match wins |
| quantifiers | `*`, `+`, `?`, `{n}`, `{n,}`, `{n,m}` - greedy; with a `?` after them lazy (`*?`, `+?`, `??`, `{n,m}?`) |
| anchors | `^` and `$` (the start and the end of the text; `$` also before a `\n` at the very end; with `Multiline` of every line), `\A`, `\z`, `\Z`, `\b` and `\B` (a word boundary and none) |
| back references | `\1` ... `\99`, `\k<name>` (with `IgnoreCase` without regard to case) |
| look-ahead | `(?=...)`, `(?!...)` (the groups inside do not keep their texts) |

**Not in the subset:** look-behind (`(?<=...)`, `(?<!...)`), atomic groups and possessive quantifiers, conditionals, inline options (`(?i)`), Unicode
categories (`\p{L}`), balancing groups, `RightToLeft`. They are errors, not silently something else. `\d` is only `0-9` (in .NET it is every Unicode
digit unless `ECMAScript` is given). `IgnoreCase` compares characters as `char.ToUpper`/`ToLower` do (ASCII, Latin-1, Greek, Cyrillic).

**Costs.** A backtracking engine can take exponential time on patterns like `(a|aa)*$x`; `Regex.MaxSteps` bounds one call. A count `{n,m}` copies its
group `m` times into the program, which may have at most 200 000 instructions. Compile a pattern you use often once (`new Regex(...)`) and keep it; the
one-off helpers compile it every time.
