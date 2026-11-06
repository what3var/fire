# The text console (`#import "console"`)

The package `fire-console` writes to the screen of a text console in colour, places the cursor, clears the screen and reads single keys without waiting
for Enter. It works the same on every target that has a console: in a terminal on Linux and macOS (ANSI escape sequences, termios), in the console of
Windows (its console API, also on old versions) and on DOS (the text mode through the BIOS, in code page 437).

```
#import "console"

Console.Clear()
Console.SetColor(ConsoleColor.Yellow, ConsoleColor.DarkBlue)
Console.WriteAt(2, 1, "╔══ fire ══╗")
Console.ResetColor()
Console.SetCursor(0, 3)
Console.Write("Press a key: ")
var key = Console.ReadKey()                        // waits; the key is not shown
if (key.Key == ConsoleKey.Escape) { print("bye") }
else if (key.Key == ConsoleKey.Character) { print("you typed " + key.Char) }
else { print(key) }                                // "Up", "F5", "Ctrl+Right", ...

while (!Console.KeyAvailable()) { /* animate something */ }
```

**Without a console** - the output or the input is redirected (a file, a pipe), the target has no console of its own (a board, the browser), or the
program runs in the virtual machine (the editor's output panel) - the text goes to standard output as it is and the keys are the characters of standard
input (`\n` is Enter, `\t` Tab, a control character is Ctrl and a letter); colours, the cursor and `Clear` do nothing, `Width`/`Height` are 80 x 25 and the
cursor is at 0, 0. `Console.IsConsole()` says which it is. A program therefore runs the same both ways; only the screen looks different.

## Reference

| Member | Meaning |
|---|---|
| `Console.Write(value)`, `Console.WriteLine(value = "")` | text at the cursor in the current colours (`\n`, `\r`, `\b`, `\t`; the screen scrolls at the bottom) |
| `Console.WriteAt(column, row, value)` | text at a place (0-based); the cursor stays after it |
| `Console.SetColor(foreground, background)` | the colours from now on (`ConsoleColor`); without `background` the one the console had at the start |
| `Console.ResetColor()` | the colours the console had at the start (also restored when the program ends) |
| `Console.Clear()` | the visible console empty in the current colours, the cursor at the top left |
| `Console.SetCursor(column, row)`, `Console.Column()`, `Console.Row()` | the cursor, 0-based within the visible part |
| `Console.ShowCursor(show = true)` | show or hide the cursor (shown again at the end) |
| `Console.Width()`, `Console.Height()` | the size of the visible console in characters |
| `Console.KeyAvailable()` | is a key waiting? (never waits) |
| `Console.ReadKey()` | the next key as a `ConsoleKeyInfo` (waits for it; other fire threads go on meanwhile); `undefined` when no more keys can come (the end of a redirected input) |
| `Console.Flush()` | writes out what is buffered |
| `Console.IsConsole()` | `true`: a screen and a keyboard; `false`: plain text (see above) |
| `ConsoleKeyInfo` | `Key` (a `ConsoleKey`), `Char` (the character it gives: the letter, `'\n'` for Enter, `'\t'` for Tab, `'\b'` for Backspace, `'\x1b'` for Escape; `'\0'` for the others), `Shift`, `Control`, `Alt`; `ToString()` gives `"a"`, `"Ctrl+a"`, `"Up"`, `"Shift+F5"` |
| `ConsoleKey` | `None`, `Character`, `Enter`, `Escape`, `Backspace`, `Tab`, `Up`, `Down`, `Left`, `Right`, `Home`, `End`, `PageUp`, `PageDown`, `Insert`, `Delete`, `F1` .. `F12` |
| `ConsoleColor` | the 16 colours of the PC's text mode: `Black`, `DarkBlue`, `DarkGreen`, `DarkCyan`, `DarkRed`, `DarkMagenta`, `DarkYellow`, `Gray`, `DarkGray`, `Blue`, `Green`, `Cyan`, `Red`, `Magenta`, `Yellow`, `White` |

## The targets

| Target | Screen | Keys |
|---|---|---|
| Linux, macOS (a terminal) | ANSI escape sequences (colours 30-37/90-97, cursor `ESC[row;colH`, the cursor position is asked with `ESC[6n`) | termios, raw only while a key is read or looked for; the sequences of xterm, the Linux console and VT220 (cursor keys, Home/End, Insert/Delete, Page Up/Down, F1-F12, with Shift/Ctrl/Alt) |
| Windows | the console API (attributes, `WriteConsoleW`) | `ReadConsoleInputW` |
| DOS (`dos16`) | the BIOS (INT 10h): characters with their attribute, code page 437 (box drawing, `äöü`, ...; a character it does not have is `?`) | the BIOS (INT 16h, the enhanced keyboard: F11/F12, the cursor block) |
| boards, the browser | standard output | standard input |

The colours are those of the PC's text mode in its order; a terminal shows them as its ANSI colours. On DOS a bright background (`DarkGray` and above)
blinks on the CGA, or is bright after the program switched blinking off.

The console of each platform comes with the package (`packages/console/native/fire_console_<platform>.h`, the interface in `fire_console_c.h`,
chosen by `platformSources` of `console.fireproj`): a new console is a new file in the package and a line there - an update of the package brings it.
