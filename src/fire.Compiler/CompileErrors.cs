using System;
using System.Collections.Generic;
using System.Linq;

namespace fire.Compiler
{
    /// <summary>Helpers for callers who want to show a
    /// compilation error: the resolver and compiler collect several errors (see
    /// ResolverException/CompilerException), but `Exception.Message` names
    /// only the FIRST of them - here all of them are.</summary>
    public static class CompileErrors
    {
        /// <summary>The messages of ALL errors collected in `ex` (for a
        /// parser/preprocessor/other error only its one message).</summary>
        public static IReadOnlyList<string> Messages(Exception ex) => ex switch
        {
            ResolverException r => r.Errors.Select(e => e.Message).ToList(),
            CompilerException c => c.Errors.Select(e => e.Message).ToList(),
            _ => new[] { ex.Message },
        };

        /// <summary>All messages, one per line.</summary>
        public static string Describe(Exception ex) => string.Join(Environment.NewLine, Messages(ex));
    }
}
