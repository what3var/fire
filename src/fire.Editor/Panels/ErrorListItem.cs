namespace fire.Editor
{
    /// <summary>A row of the error list (columns: severity, description, file, line).</summary>
    public sealed record ErrorListItem(string Severity, string Description, string File, int Line);
}
