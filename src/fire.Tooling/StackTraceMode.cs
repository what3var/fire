using System.Collections.Generic;

namespace fire.Tooling
{
    /// <summary>
    /// What an exception shows of the stack it was thrown on (docs/spec/STACKTRACE.md section 7): `#stacktrace off | calls | lines`, `"stackTrace"` of the project,
    /// `--stacktrace`, `"stackTrace"` of a target in fire.native.json. The default is `lines` in Release, `off` in Performance; Debug is always `lines`.
    /// The value is the byte of the program file (`.fbc`, flag "trace").
    /// </summary>
    public enum StackTraceMode : byte
    {
        /// <summary>No trace: an exception that nothing catches writes only its line, `e.StackTrace` is "".</summary>
        Off = 0,
        /// <summary>The functions without their lines: `   at Store.Find (main.fire)`.</summary>
        Calls = 1,
        /// <summary>The functions with their lines: `   at Store.Find (main.fire:16)`.</summary>
        Lines = 2,
    }

    public static class StackTraceModes
    {
        /// <summary>The names in the order of the setting's documentation.</summary>
        public static readonly IReadOnlyList<string> Names = new[] { "off", "calls", "lines" };

        /// <summary>A name (any case) or null.</summary>
        public static StackTraceMode? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
        {
            "off" => StackTraceMode.Off,
            "calls" => StackTraceMode.Calls,
            "lines" => StackTraceMode.Lines,
            _ => null,
        };

        public static string Name(StackTraceMode mode) => mode switch
        {
            StackTraceMode.Off => "off",
            StackTraceMode.Calls => "calls",
            _ => "lines",
        };

        /// <summary>The setting a program runs with: Debug always `lines`; otherwise the command line (<paramref name="forced"/>), `#stacktrace` of the source,
        /// the project, fire.native.json (<paramref name="configured"/>), then the default of the mode (Performance `off`, else `lines`).</summary>
        public static StackTraceMode Effective(VmExecutionMode mode, StackTraceMode? forced, StackTraceMode? source, StackTraceMode? project, StackTraceMode? configured) =>
            mode == VmExecutionMode.Debug ? StackTraceMode.Lines
            : forced ?? source ?? project ?? configured ?? (mode == VmExecutionMode.Performance ? StackTraceMode.Off : StackTraceMode.Lines);
    }
}
