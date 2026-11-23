namespace fire.Tooling
{
    /// <summary>One event a host hands to a program's window (the display file, <see cref="HostDisplayFile"/>): <see cref="Type"/> as EventType (mouse, keys, text, resize, close);
    /// positions in pixels of the window. MouseDown/Up: Button, X, Y; MouseMove: X, Y (the buttons the window keeps itself); MouseScroll: Dx, Dy (notches, Dy &gt; 0: up), X, Y;
    /// KeyDown/Up: KeyCode, Modifiers (the numbers of UI.Keys); TextInput: Text; Resize: Width, Height; Touch*: Finger, X, Y, Pressure; Joystick*: Joystick, Index, Value;
    /// DropFiles/DropText: Data, X, Y; DragHover: X, Y; DragLeave; Close.</summary>
    public struct HostDisplayEvent
    {
        public int Type, Button, Buttons, KeyCode, Modifiers, Width, Height;
        public float X, Y, Dx, Dy;
        public string? Text;
        /// <summary>TouchDown/Move/Up: the finger and how hard it presses (0 to 1).</summary>
        public long Finger;
        public float Pressure;
        /// <summary>Joystick events: the device, the axis/button/hat and its value.</summary>
        public int Joystick, Index;
        public float Value;
        /// <summary>DropFiles (the paths, one per line) / DropText: what was dropped.</summary>
        public string? Data;
    }

    /// <summary>Whether the C of a program is one file or several translation units (docs/spec/NATIVE_SPLIT.md): `"split"` in fire.native.json, `--split`,
    /// FIRE_SPLIT=1/0 (which goes before both). (The configuration is read here for the dialogs; forge splits.)</summary>
    public enum SplitMode
    {
        /// <summary>Several units with gcc, clang (MinGW too) and Emscripten when the program is large; one file otherwise.</summary>
        Auto,
        /// <summary>Several units whenever the toolchain can build them.</summary>
        On,
        /// <summary>One file, always.</summary>
        Off,
    }

    public static class SplitModes
    {
        /// <summary>`auto`/`on`/`off` (also `true`/`false`, `1`/`0`); null: not one of them.</summary>
        public static SplitMode? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
        {
            "auto" or "" => SplitMode.Auto,
            "on" or "true" or "1" or "yes" => SplitMode.On,
            "off" or "false" or "0" or "no" => SplitMode.Off,
            _ => null,
        };
    }

    /// <summary>A line of a directive (`#name args`), as the preprocessor of forge reads it: for the greyed-out branches of `#if` in the editor (<see cref="ConditionalSymbols"/>).</summary>
    public static class Preprocessor
    {
        public static readonly System.Text.RegularExpressions.Regex DirectiveLineRegex =
            new(@"^\s*#([A-Za-z_][A-Za-z0-9_]*)(?:[ \t]+(.*))?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    }

    /// <summary>A wrong `#if`/`#define` line (the editor's evaluation of the conditions; forge reports it when it compiles).</summary>
    public sealed class PreprocessorException : Exception
    {
        public PreprocessorException(string message) : base(message) { }
        public PreprocessorException(string message, Exception inner) : base(message, inner) { }
    }
}
