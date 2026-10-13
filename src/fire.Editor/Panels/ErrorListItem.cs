namespace fire.Editor
{
    /// <summary>A row of the error list (columns: severity, code, description, file, line). `Path` is the full path of the file when it is known (a double click opens it).</summary>
    public sealed record ErrorListItem(string Severity, string Code, string Description, string File, int Line, string? Path = null)
    {
        public bool IsError => Severity == "Error";
        public bool IsWarning => Severity == "Warning";
    }
}
