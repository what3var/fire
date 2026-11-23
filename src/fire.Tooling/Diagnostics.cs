using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace fire.Tooling
{
    public enum DiagnosticSeverity
    {
        Error,
        Warning,
    }

    /// <summary>A message of the compiler about the code: an error (with a code of the <see cref="ErrorCatalog"/>, `E3012`) or a warning (`warn 123: "text"` in the code: `W123`). `File` is null
    /// when the compiler does not know it (an error of the resolver or the bytecode compiler in a project of several files; the file of a single script is the script itself).</summary>
    public sealed record Diagnostic(int Line, string Message, DiagnosticSeverity Severity = DiagnosticSeverity.Error, string Code = "", string? File = null, int Column = 0)
    {
        public bool IsError => Severity == DiagnosticSeverity.Error;
        public bool IsWarning => Severity == DiagnosticSeverity.Warning;

        public override string ToString() => $"Line {Line}: {Message}";

        /// <summary>The form of a build log and of the command line: `file(12): error E3012: message`.</summary>
        public string ToLogLine()
        {
            string kind = IsError ? "error" : "warning";
            string code = Code.Length > 0 ? " " + Code : "";
            if (File == null) return $"{kind}{code}: {Message}" + (Line > 0 ? $" (line {Line})" : "");
            return $"{File}{(Line > 0 ? $"({Line})" : "")}: {kind}{code}: {Message}";
        }
    }
}
