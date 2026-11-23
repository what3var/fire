namespace fire.Editor
{
    /// <summary>The kind of a suggestion of the completion (forge serve, `completion`: its `kind`).</summary>
    public enum CompletionKind
    {
        Keyword,
        TypeKeyword,
        ClassName,
        EnumName,
        EnumMember,
        Namespace,
        Field,
        Method,
        Property,
        Variable,
        Parameter,
    }

    /// <summary>A suggestion of the completion as forge serve sends it (docs/spec/FORGE_SERVE.md; the logic is native/forge/serve_complete.c).</summary>
    public sealed record CompletionItem(string Text, CompletionKind Kind, float Score, string? Detail = null)
    {
        /// <summary>The `///` documentation of the class/member this item stands for (shown as a tooltip next to the list).</summary>
        public DocComment? Documentation { get; init; }

        public string Display => Detail != null ? $"{Text}  {Detail}" : Text;
    }

    /// <summary>Where a click navigation goes (forge serve, `definition`). `FilePath`: null means "the target lies in the current document itself" (only `Line`
    /// counts); a set path is the path of a file of the project or the relative path of an `#include` line - MainWindow resolves it relative to the directory of
    /// the file shown. `PreludeName`: the target lies in a built-in prelude (the standard library or an import) - `FilePath` is then null and `Line` is a line of
    /// that prelude, which the caller shows read-only (FileViewerWindow.ShowPrelude).</summary>
    public sealed record NavigationTarget(string? FilePath, int Line, string? PreludeName = null)
    {
        public bool IsPrelude => PreludeName != null;
    }

    /// <summary>What forge serve found at a position (`hover`, `signature`): where it is declared (<see cref="Target"/>), its one-line header and its `///` documentation.</summary>
    public sealed record ResolvedSymbol(NavigationTarget Target, string? Text = null, DocComment? Doc = null)
    {
        /// <summary>The `///` documentation of the member or class, null if there is none.</summary>
        public DocComment? Documentation => Doc;

        /// <summary>A one-line description of the symbol, e.g. `(method) Calc.Add(int a, int b) → int` (made by the server).</summary>
        public string Header => Text ?? "";
    }

    /// <summary>What the editor finds in its own text without the server: the open call at the caret (where the tooltip of a call is placed; which overload is the server's).</summary>
    public static class NavigationEngine
    {
        /// <summary>An open call at the caret: the innermost `(` that is not closed yet, and how many arguments have been started.</summary>
        /// <param name="ParenOffset">Offset of the `(`.</param>
        /// <param name="RequiredArgs">How many parameters the called overload needs at least: 0 right after `(`, otherwise the number of arguments started so far.</param>
        public sealed record OpenCall(int ParenOffset, int RequiredArgs);

        /// <summary>Finds the call whose argument list the caret is in (null if the caret is not inside any parentheses of the current statement).
        /// Strings, characters and comments are skipped; a `;`, `{` or `}` ends the search.</summary>
        public static OpenCall? FindOpenCall(string source, int caret)
        {
            if (caret < 0 || caret > source.Length) return null;
            int limit = Math.Max(0, caret - 3000);
            int start = limit;
            for (int i = caret - 1; i >= limit; i--)
            {
                if (source[i] is ';' or '{' or '}') { start = i + 1; break; }
            }

            var open = new List<(int Pos, int Commas, bool HasText)>();
            void MarkText()
            {
                if (open.Count > 0) open[^1] = (open[^1].Pos, open[^1].Commas, true);
            }

            for (int i = start; i < caret; i++)
            {
                char c = source[i];
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < caret && source[i] != quote && source[i] != '\n')
                    {
                        if (source[i] == '\\') i++;
                        i++;
                    }
                    MarkText();
                }
                else if (c == '/' && i + 1 < caret && source[i + 1] == '/')
                {
                    while (i < caret && source[i] != '\n') i++;
                }
                else if (c == '/' && i + 1 < caret && source[i + 1] == '*')
                {
                    int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0 || close + 1 >= caret) return null; // inside an unfinished block comment
                    i = close + 1;
                }
                else if (c == '(')
                {
                    MarkText();
                    open.Add((i, 0, false));
                }
                else if (c == ')')
                {
                    if (open.Count > 0) open.RemoveAt(open.Count - 1);
                    MarkText();
                }
                else if (c == ',' && open.Count > 0)
                {
                    open[^1] = (open[^1].Pos, open[^1].Commas + 1, false);
                }
                else if (!char.IsWhiteSpace(c))
                {
                    MarkText();
                }
            }

            if (open.Count == 0) return null;
            var (pos, commas, hasText) = open[^1];
            return new OpenCall(pos, commas > 0 || hasText ? commas + 1 : 0);
        }
    }
}
