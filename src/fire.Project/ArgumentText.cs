using System.Text;

namespace fire.Projects
{
    /// <summary>The arguments of a program as one line of text (the field of the editor, settings.arguments shown as text): separated by blanks, an argument
    /// with blanks in double quotes, `\"` a quote inside quotes.</summary>
    public static class ArgumentText
    {
        public static List<string> Split(string? text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            var current = new StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"') { current.Append('"'); i++; any = true; }
                else if (c == '"') { quoted = !quoted; any = true; }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
                }
                else { current.Append(c); any = true; }
            }
            if (any) result.Add(current.ToString());
            return result;
        }

        public static string Join(IEnumerable<string>? args) =>
            args == null ? "" : string.Join(" ", args.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
    }
}
