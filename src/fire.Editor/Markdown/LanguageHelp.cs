using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace fire.Editor
{
    /// <summary>The contents page of the language reference (docs/language/README.md, copied to Help\Language): it gives the entries of
    /// Help > The fire Language (see MainWindow.BuildLanguageHelpMenu).</summary>
    public static class LanguageHelp
    {
        /// <summary>The pages that the contents page links: the numbered topics (`1. [Basics](01-basics.md) - ...`, shown as "1. Basics") and the
        /// further pages that stand as a link at the start of a line (`[Roadmap](roadmap.md) - ...`). Only links to `.md` files in the same folder count.</summary>
        public static (List<(string Title, string File)> Topics, List<(string Title, string File)> More) Contents(string readme)
        {
            var topics = new List<(string, string)>();
            var more = new List<(string, string)>();
            foreach (var raw in readme.Split('\n'))
            {
                var line = raw.Trim();
                var numbered = Regex.Match(line, @"^(\d+)\.\s*\[(?<title>[^\]]+)\]\((?<file>[^()#\s/\\]+\.md)\)");
                if (numbered.Success) { topics.Add(($"{numbered.Groups[1].Value}. {numbered.Groups["title"].Value}", numbered.Groups["file"].Value)); continue; }
                var link = Regex.Match(line, @"^\[(?<title>[^\]]+)\]\((?<file>[^()#\s/\\]+\.md)\)");
                if (link.Success) more.Add((link.Groups["title"].Value, link.Groups["file"].Value));
            }
            return (topics, more);
        }
    }
}
