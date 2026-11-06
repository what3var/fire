using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace fire.Editor
{
    // -----------------------------------------------------------
    // The model of a project overview (docs/tools/notes.md), without any user interface - testable on its own.
    // -----------------------------------------------------------

    /// <summary>The attributes of a definition: what stands between `&lt;` and `&gt;` - `key=value` pairs (a value with spaces in quotes) and words without a key.</summary>
    public sealed class OvAttributes
    {
        public Dictionary<string, string> Named { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positional { get; } = new();

        public string? Get(string key) => Named.TryGetValue(key, out var v) ? v : null;

        /// <summary>The number of `key`, else the positional word at `index` (if given); null: none or not a number.</summary>
        public double? Number(string key, int index = -1)
        {
            string? text = Get(key) ?? (index >= 0 && index < Positional.Count ? Positional[index] : null);
            return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
        }

        public static OvAttributes Parse(string text)
        {
            var result = new OvAttributes();
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                int start = i;
                var key = new StringBuilder();
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-')) key.Append(text[i++]);
                if (key.Length > 0 && char.IsLetter(key[0]) && i < text.Length && text[i] == '=')
                {
                    i++;
                    result.Named[key.ToString()] = Value(text, ref i);
                    continue;
                }
                i = start;
                result.Positional.Add(Value(text, ref i));
            }
            return result;
        }

        private static string Value(string text, ref int i)
        {
            if (i < text.Length && text[i] is '"' or '\'')
            {
                char quote = text[i++];
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    sb.Append(text[i++]);
                }
                i++;
                return sb.ToString();
            }
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            return text.Substring(start, i - start);
        }
    }

    public abstract class OvElement
    {
        /// <summary>The name (the title of the definition); null: none.</summary>
        public string? Name { get; init; }
        /// <summary>The line of the definition (0-based).</summary>
        public int Line { get; init; }
        public OvAttributes Attributes { get; init; } = new();
    }

    /// <summary>A container: at X/Y (relative to the container it is in), W wide, H high (null: as high as its content); text and further elements - or a document (`src`).</summary>
    public sealed class OvContainer : OvElement
    {
        public double X => Attributes.Number("x") ?? 0;
        public double Y => Attributes.Number("y") ?? 0;
        public double W => Attributes.Number("w") ?? 300;
        public double? H => Attributes.Number("h");
        /// <summary>The document it shows (a path relative to the overview); null: its own content.</summary>
        public string? Src => Attributes.Get("src");
        /// <summary>Its text (Markdown), without the definitions of the elements in it.</summary>
        public string Markdown { get; internal set; } = "";
        public List<OvElement> Children { get; } = new();
        /// <summary>The line of its end (`[/]: &lt;&gt;`); -1: none (a container with `src`, or one that is not closed).</summary>
        public int EndLine { get; internal set; } = -1;
    }

    /// <summary>A shape of a sketch: `rect`, `ellipse`, `path` or `text`, with its attributes; the title of the definition is the text of a `text`.</summary>
    public sealed class OvShape : OvElement
    {
        public required string Kind { get; init; }
    }

    /// <summary>Drawings, relative to the container the sketch is in.</summary>
    public sealed class OvSketch : OvElement
    {
        public List<OvShape> Shapes { get; } = new();
        public int EndLine { get; internal set; } = -1;
    }

    /// <summary>A line between two named containers; it follows them when they move. `Name` is its label.</summary>
    public sealed class OvLine : OvElement
    {
        public string From => Attributes.Get("from") ?? "";
        public string To => Attributes.Get("to") ?? "";
        /// <summary>`none`, `end`, `start` or `both`.</summary>
        public string Arrow => Attributes.Get("arrow") ?? "none";
    }

    /// <summary>A parsed overview: its own text, the elements on it, the lines between containers and what is wrong in the file.</summary>
    public sealed class OverviewDocument
    {
        public string Markdown { get; internal set; } = "";
        public List<OvElement> Children { get; } = new();
        public List<OvLine> Lines { get; } = new();
        /// <summary>Problems, each with its line (1-based) in front.</summary>
        public List<string> Problems { get; } = new();

        /// <summary>Every container, also the nested ones, in the order of the file.</summary>
        public IEnumerable<OvContainer> AllContainers()
        {
            IEnumerable<OvContainer> Walk(IEnumerable<OvElement> elements)
            {
                foreach (var c in elements.OfType<OvContainer>())
                {
                    yield return c;
                    foreach (var inner in Walk(c.Children)) yield return inner;
                }
            }
            return Walk(Children);
        }
    }

    /// <summary>
    /// Reads a project overview (docs/tools/notes.md): Markdown in which what only spark understands is written as link reference definitions -
    /// `[container]: &lt;x= y= w=&gt; "name"` … `[/]: &lt;&gt;`, `[sketch]: &lt;&gt;` with shapes, `[line]: &lt;from= to=&gt;`. Everything else is the text of the overview or
    /// of the container it is in.
    /// </summary>
    public static class OverviewParser
    {
        private static readonly Regex Definition = new(
            @"^ {0,3}\[(?<label>[^\]\r\n]+)\]:[ \t]*<(?<dest>(?:\\.|[^<>\\\r\n])*)>(?:[ \t]+(?:""(?<title>(?:\\.|[^""\\])*)""|'(?<title>(?:\\.|[^'\\])*)'|\((?<title>[^)]*)\)))?[ \t]*$",
            RegexOptions.Compiled);
        private static readonly Regex OverviewMark = new(@"^ {0,3}\[(container|sketch|line)\]:[ \t]*<", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
        private static readonly Regex Fence = new(@"^ {0,3}(```|~~~)", RegexOptions.Compiled);
        private static readonly Regex HeadingLine = new(@"^ {0,3}#{1,6}(\s|$)", RegexOptions.Compiled);
        private static readonly HashSet<string> ShapeKinds = new(StringComparer.OrdinalIgnoreCase) { "rect", "ellipse", "path", "text" };

        /// <summary>The extension of a canvas: Markdown with containers, sketches and lines (docs/tools/notes.md).</summary>
        public const string CanvasExtension = ".xmd";

        /// <summary>A Markdown text is an overview when it has at least one container, sketch or line.</summary>
        public static bool IsOverview(string markdown) => OverviewMark.IsMatch(markdown);

        public static OverviewDocument Parse(string markdown)
        {
            var doc = new OverviewDocument();
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var open = new List<OvElement>();   // the open containers and sketches, outermost first
            var texts = new Dictionary<object, StringBuilder> { [doc] = new StringBuilder() };
            bool inFence = false;
            bool sketchFence = false;   // shapes in a code fence: shown as code elsewhere, on purpose
            string previous = "";

            StringBuilder TextOf() => texts[(object?)open.OfType<OvContainer>().LastOrDefault() ?? doc];
            List<OvElement> ChildrenOf() => open.LastOrDefault() is OvContainer c ? c.Children : open.Count == 0 ? doc.Children : new List<OvElement>();
            void Problem(int line, string text) => doc.Problems.Add($"{line + 1}: {text}");

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                var sketch = open.LastOrDefault() as OvSketch;

                if (sketch == null && Fence.IsMatch(line)) { inFence = !inFence; TextOf().AppendLine(line); previous = line; continue; }
                if (inFence) { TextOf().AppendLine(line); previous = line; continue; }

                if (sketch != null && Fence.IsMatch(line)) { sketchFence = !sketchFence; previous = line; continue; }

                var m = Definition.Match(line);
                if (!m.Success)
                {
                    // in a sketch only shapes count (a code fence around them shows them as code elsewhere)
                    if (sketch == null) TextOf().AppendLine(line);
                    previous = line;
                    continue;
                }

                string label = m.Groups["label"].Value.Trim();
                var attributes = OvAttributes.Parse(Unescape(m.Groups["dest"].Value));
                string? title = m.Groups["title"].Success ? Unescape(m.Groups["title"].Value) : null;
                bool known = label.StartsWith('/') || label.Equals("container", StringComparison.OrdinalIgnoreCase) || label.Equals("sketch", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("line", StringComparison.OrdinalIgnoreCase) || (sketch != null && ShapeKinds.Contains(label));
                if (known && !(sketch != null && sketchFence) && previous.Trim().Length > 0 && !Definition.IsMatch(previous) && !HeadingLine.IsMatch(previous) && !Fence.IsMatch(previous))
                    Problem(i, $"[{label}] directly follows text: other programs show it as a link - put an empty line before it.");

                if (label.StartsWith('/'))
                {
                    string what = label.Substring(1).Trim();
                    if (open.Count == 0) Problem(i, "[/] ends nothing: there is no open container or sketch.");
                    else
                    {
                        var closing = open[^1];
                        string kind = closing is OvSketch ? "sketch" : "container";
                        if (what.Length > 0 && !what.Equals(kind, StringComparison.OrdinalIgnoreCase)) Problem(i, $"[/{what}] ends a {kind}.");
                        open.RemoveAt(open.Count - 1);
                        if (closing is OvContainer c) { c.EndLine = i; c.Markdown = texts[c].ToString().TrimEnd(); }
                        else if (closing is OvSketch s) { s.EndLine = i; sketchFence = false; }
                    }
                }
                else if (sketch != null && ShapeKinds.Contains(label))
                {
                    sketch.Shapes.Add(new OvShape { Kind = label.ToLowerInvariant(), Attributes = attributes, Name = title, Line = i });
                }
                else if (label.Equals("container", StringComparison.OrdinalIgnoreCase))
                {
                    if (sketch != null) Problem(i, "A container cannot be in a sketch.");
                    var container = new OvContainer { Name = title, Attributes = attributes, Line = i };
                    if (attributes.Get("w") == null) Problem(i, "The container has no width (w=): 300 is used.");
                    ChildrenOf().Add(container);
                    if (container.Src == null) { open.Add(container); texts[container] = new StringBuilder(); }
                }
                else if (label.Equals("sketch", StringComparison.OrdinalIgnoreCase))
                {
                    var s = new OvSketch { Name = title, Attributes = attributes, Line = i };
                    ChildrenOf().Add(s);
                    open.Add(s);
                }
                else if (label.Equals("line", StringComparison.OrdinalIgnoreCase))
                {
                    var l = new OvLine { Name = title, Attributes = attributes, Line = i };
                    if (l.From.Length == 0 || l.To.Length == 0) Problem(i, "A line needs from= and to= (the names of two containers).");
                    doc.Lines.Add(l);
                }
                else if (sketch == null)
                {
                    TextOf().AppendLine(line);   // a definition of the author's own (a link reference): part of the text
                }
                previous = line;
            }

            for (int k = open.Count - 1; k >= 0; k--)
            {
                Problem(open[k].Line, $"The {(open[k] is OvSketch ? "sketch" : "container")} is not ended: [/]: <> is missing.");
                if (open[k] is OvContainer c) c.Markdown = texts[c].ToString().TrimEnd();
            }
            doc.Markdown = texts[doc].ToString().TrimEnd();

            var names = doc.AllContainers().Where(c => c.Name != null).GroupBy(c => c.Name!, StringComparer.Ordinal).ToList();
            foreach (var dup in names.Where(g => g.Count() > 1)) Problem(dup.Skip(1).First().Line, $"The name '{dup.Key}' is used by more than one container.");
            var known2 = names.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var l in doc.Lines)
                foreach (string end in new[] { l.From, l.To })
                    if (end.Length > 0 && !known2.Contains(end)) Problem(l.Line, $"The line goes to '{end}', but no container has that name.");
            return doc;
        }

        private static string Unescape(string text) => Regex.Replace(text, @"\\(.)", "$1");
    }
}

namespace fire.Editor
{
    /// <summary>
    /// Changes to the text of an overview that the surface makes (docs/tools/notes.md): only the lines concerned - the attributes of one definition (a container moved or resized),
    /// or the own text of a container (or of the overview), the elements in it kept after the text.
    /// </summary>
    public static class OverviewSource
    {
        private static readonly Regex Definition = new(@"^(?<head> {0,3}\[[^\]\r\n]+\]:[ \t]*<)(?<dest>(?:\\.|[^<>\\\r\n])*)(?<tail>>.*)$", RegexOptions.Compiled);

        /// <summary>The line with the attributes `values` set: an attribute that is there gets the new value in its place, a new one is added at the end; the rest stays as written.</summary>
        public static string SetAttributes(string line, IReadOnlyList<(string Key, string Value)> values)
        {
            var m = Definition.Match(line);
            if (!m.Success) return line;
            string dest = m.Groups["dest"].Value;
            foreach (var (key, raw) in values)
            {
                string value = raw.Any(char.IsWhiteSpace) || raw.Length == 0 ? "\"" + raw.Replace("\"", "\\\"") + "\"" : raw;
                var token = Regex.Match(dest, @"(?<=^|\s)" + Regex.Escape(key) + @"=(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|\S*)", RegexOptions.IgnoreCase);
                dest = token.Success
                    ? dest.Substring(0, token.Index) + key + "=" + value + dest.Substring(token.Index + token.Length)
                    : (dest.TrimEnd().Length > 0 ? dest.TrimEnd() + " " : "") + key + "=" + value;
            }
            return m.Groups["head"].Value + dest + m.Groups["tail"].Value;
        }

        private static readonly Regex TitleTail = new(@"^(?<head> {0,3}\[[^\]\r\n]+\]:[ \t]*<(?:\\.|[^<>\\\r\n])*>)(?<title>.*)$", RegexOptions.Compiled);
        private static readonly Regex AnyDefinition = new(@"^ {0,3}\[[^\]\r\n]+\]:[ \t]*<", RegexOptions.Compiled);
        private static readonly Regex Heading = new(@"^ {0,3}#{1,6}(\s|$)", RegexOptions.Compiled);

        /// <summary>The line with the name (the title) `name`.</summary>
        public static string SetTitle(string line, string name)
        {
            var m = TitleTail.Match(line);
            return m.Success ? m.Groups["head"].Value + " \"" + name.Replace("\"", "\\\"") + "\"" : line;
        }

        /// <summary>A name no container has yet: `stem`, `stem2`, `stem3`, ...</summary>
        public static string UniqueName(OverviewDocument doc, string stem = "box", IEnumerable<string>? taken = null)
        {
            var used = doc.AllContainers().Select(c => c.Name).Where(n => n != null).Concat(taken ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
            if (!used.Contains(stem)) return stem;
            for (int n = 2; ; n++) if (!used.Contains(stem + n)) return stem + n;
        }

        /// <summary>
        /// Inserts lines of definitions (a container, a sketch, a line) at the end of a container (null: of the overview): before the end of the container, after an empty
        /// line when text stands before it. Returns the change (first, count 0, lines) and the line the block starts at after it.
        /// </summary>
        public static ((int First, int Count, List<string> Lines) Change, int BlockLine) Insert(string[] lines, OvContainer? scope, IReadOnlyList<string> block)
        {
            int at;
            if (scope != null && scope.EndLine >= 0) at = scope.EndLine;
            else if (scope != null) at = lines.Length;
            else
            {
                at = lines.Length;
                while (at > 0 && lines[at - 1].Trim().Length == 0) at--;   // after the last line that is not empty (the final line break stays)
            }
            var result = new List<string>();
            string before = at > 0 ? lines[at - 1] : "";
            if (before.Trim().Length > 0 && !AnyDefinition.IsMatch(before) && !Heading.IsMatch(before)) result.Add("");
            else if (scope == null && at > 0 && before.Trim().Length > 0) result.Add("");   // set apart at the end of the overview
            int blockLine = at + result.Count;
            result.AddRange(block);
            return ((at, 0, result), blockLine);
        }

        /// <summary>Adds a shape to the sketch of a container (null: of the overview): to its first sketch, or to a new one at its end.</summary>
        public static ((int First, int Count, List<string> Lines) Change, int ShapeLine) AddShape(string[] lines, OverviewDocument doc, OvContainer? scope, string shape)
        {
            var sketch = (scope?.Children ?? doc.Children).OfType<OvSketch>().FirstOrDefault(s => s.EndLine >= 0);
            if (sketch != null) return ((sketch.EndLine, 0, new List<string> { shape }), sketch.EndLine);
            var (change, blockLine) = Insert(lines, scope, new[] { "[sketch]: <>", shape, "[/]: <>" });
            return (change, blockLine + 1);
        }

        /// <summary>Removes a container or sketch with everything in it (and an empty line after it, so that no gap of two is left).</summary>
        public static (int First, int Count, List<string> Lines) Remove(string[] lines, OvElement element)
        {
            int end = Math.Min(EndOf(element, lines.Length), lines.Length - 1);
            int count = end - element.Line + 1;
            if (end + 1 < lines.Length && lines[end + 1].Trim().Length == 0 && (element.Line == 0 || lines[element.Line - 1].Trim().Length == 0)) count++;
            return (element.Line, count, new List<string>());
        }

        private static string Quote(string value) =>
            value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"') ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : value;

        /// <summary>The definition with these attributes: the words without a key first, then the keys in their order; the label and the title stay.</summary>
        public static string Rewrite(string line, OvAttributes attributes)
        {
            var m = Definition.Match(line);
            if (!m.Success) return line;
            string dest = string.Join(" ", attributes.Positional.Select(Quote).Concat(attributes.Named.Select(kv => kv.Key + "=" + Quote(kv.Value))));
            return m.Groups["head"].Value + dest + m.Groups["tail"].Value;
        }

        /// <summary>The attributes of a definition line (empty if it is none).</summary>
        public static OvAttributes AttributesOf(string line)
        {
            var m = Definition.Match(line);
            return m.Success ? OvAttributes.Parse(Regex.Replace(m.Groups["dest"].Value, @"\\(.)", "$1")) : new OvAttributes();
        }

        /// <summary>The line without the attribute `key`.</summary>
        public static string RemoveAttribute(string line, string key)
        {
            var attributes = AttributesOf(line);
            return attributes.Named.Remove(key) ? Rewrite(line, attributes) : line;
        }

        /// <summary>Sets a number of a shape: the key when it is written with its key, else the word at `index` (if there are enough words), else the key.</summary>
        private static void SetNumber(OvAttributes a, string key, int index, double value)
        {
            if (a.Named.ContainsKey(key) || index >= a.Positional.Count) a.Named[key] = Number(value);
            else a.Positional[index] = Number(value);
        }

        private static readonly Regex PathToken = new(@"[MmLlHhVvCcSsQqTtAaZz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

        /// <summary>Path data (SVG) moved by dx/dy: the coordinates of absolute commands move, relative ones stay (but a relative first `m`).</summary>
        public static string TranslatePath(string data, double dx, double dy)
        {
            var output = new List<string>();
            char command = 'M';
            int index = 0;
            bool first = true;
            foreach (Match t in PathToken.Matches(data))
            {
                if (char.IsLetter(t.Value[0]))
                {
                    command = t.Value[0];
                    index = 0;
                    output.Add(t.Value);
                    continue;
                }
                double v = double.Parse(t.Value, CultureInfo.InvariantCulture);
                bool absolute = char.IsUpper(command) || (first && command == 'm' && index < 2);
                char upper = char.ToUpperInvariant(command);
                int group = upper switch { 'H' or 'V' => 1, 'C' => 6, 'S' or 'Q' => 4, 'A' => 7, _ => 2 };
                int i = index % group;
                if (absolute)
                {
                    bool isX = upper switch { 'H' => true, 'V' => false, 'A' => i == 5, _ => i % 2 == 0 };
                    bool isY = upper switch { 'H' => false, 'V' => true, 'A' => i == 6, _ => i % 2 == 1 };
                    if (isX) v += dx;
                    else if (isY) v += dy;
                }
                output.Add(Number(v));
                index++;
                if (index >= 2 && command is 'm' or 'M') first = false;
                if (command is not ('m' or 'M')) first = false;
            }
            return string.Join(" ", output);
        }

        /// <summary>A shape moved by dx/dy (rect, ellipse, text: their position; path: its data).</summary>
        public static string MoveShape(string line, string kind, double dx, double dy)
        {
            var a = AttributesOf(line);
            if (kind == "path")
            {
                if (a.Get("d") is { } d) a.Named["d"] = TranslatePath(d, dx, dy);
                else
                {
                    string moved = TranslatePath(string.Join(" ", a.Positional), dx, dy);
                    a.Positional.Clear();
                    a.Positional.AddRange(moved.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }
            }
            else
            {
                SetNumber(a, "x", 0, (a.Number("x", 0) ?? 0) + dx);
                SetNumber(a, "y", 1, (a.Number("y", 1) ?? 0) + dy);
            }
            return Rewrite(line, a);
        }

        /// <summary>A rect or ellipse with a new size.</summary>
        public static string ResizeShape(string line, double w, double h)
        {
            var a = AttributesOf(line);
            SetNumber(a, "w", 2, w);
            SetNumber(a, "h", 3, h);
            return Rewrite(line, a);
        }

        /// <summary>Removes a shape; the last shape of a sketch takes the sketch with it.</summary>
        public static (int First, int Count, List<string> Lines) RemoveShape(string[] lines, OverviewDocument doc, OvShape shape)
        {
            var sketch = AllSketches(doc.Children).FirstOrDefault(s => s.Shapes.Contains(shape));
            if (sketch != null && sketch.Shapes.Count == 1 && sketch.EndLine >= 0) return Remove(lines, sketch);
            return (shape.Line, 1, new List<string>());
        }

        /// <summary>Every sketch, also those in containers.</summary>
        public static IEnumerable<OvSketch> AllSketches(IEnumerable<OvElement> elements)
        {
            foreach (var e in elements)
            {
                if (e is OvSketch s) yield return s;
                else if (e is OvContainer c) foreach (var inner in AllSketches(c.Children)) yield return inner;
            }
        }

        /// <summary>A number as it is written into an attribute.</summary>
        public static string Number(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

        /// <summary>The lines between the definition and the end of a container (null: of the overview itself): first and count.</summary>
        private static (int First, int Count) Inside(string[] lines, OvContainer? container)
        {
            if (container == null) return (0, lines.Length);
            int first = container.Line + 1;
            int end = container.EndLine >= 0 ? container.EndLine : lines.Length;
            return (first, Math.Max(0, end - first));
        }

        private static int EndOf(OvElement e, int limit) => e switch
        {
            OvContainer { Src: null, EndLine: >= 0 } c => c.EndLine,
            OvContainer { Src: null } => limit - 1,
            OvSketch { EndLine: >= 0 } s => s.EndLine,
            OvSketch => limit - 1,
            _ => e.Line,
        };

        /// <summary>The own text of a container (null: of the overview): its lines without the elements in it and without lines between containers.</summary>
        public static string OwnText(string[] lines, OverviewDocument doc, OvContainer? container)
        {
            var (own, _, _) = Split(lines, doc, container);
            return string.Join("\n", own);
        }

        private static (List<string> Own, List<List<string>> Blocks, List<string> LineDefinitions) Split(string[] lines, OverviewDocument doc, OvContainer? container)
        {
            var (first, count) = Inside(lines, container);
            int limit = first + count;
            var children = (container?.Children ?? doc.Children).OrderBy(c => c.Line).ToList();
            var lineDefs = doc.Lines.Where(l => l.Line >= first && l.Line < limit).Select(l => l.Line).ToHashSet();
            var own = new List<string>();
            var blocks = new List<List<string>>();
            var defs = new List<string>();
            for (int i = first; i < limit; i++)
            {
                var child = children.FirstOrDefault(c => c.Line == i);
                if (child != null)
                {
                    int end = Math.Min(EndOf(child, limit), limit - 1);
                    blocks.Add(lines.Skip(i).Take(end - i + 1).ToList());
                    i = end;
                    continue;
                }
                if (lineDefs.Contains(i)) { defs.Add(lines[i]); continue; }
                // lines of a line definition inside a child belong to the child; here only own lines
                own.Add(lines[i]);
            }
            while (own.Count > 0 && own[0].Trim().Length == 0) own.RemoveAt(0);
            while (own.Count > 0 && own[^1].Trim().Length == 0) own.RemoveAt(own.Count - 1);
            // where elements were taken out, their empty lines stay: one empty line is enough (Markdown does not tell them apart)
            for (int k = own.Count - 1; k > 0; k--)
                if (own[k].Trim().Length == 0 && own[k - 1].Trim().Length == 0) own.RemoveAt(k);
            return (own, blocks, defs);
        }

        /// <summary>
        /// Replaces the own text of a container (null: of the overview) by `text`: the text comes first, the elements in it follow (each after an empty line, in their order),
        /// then the lines between containers that stood there. Returns the lines to replace (first, count) and their new lines.
        /// </summary>
        public static (int First, int Count, List<string> Lines) ReplaceOwnText(string[] lines, OverviewDocument doc, OvContainer? container, string text)
        {
            var (first, count) = Inside(lines, container);
            var (_, blocks, defs) = Split(lines, doc, container);
            var result = new List<string>();
            var own = text.Replace("\r\n", "\n").Split('\n').ToList();
            while (own.Count > 0 && own[0].Trim().Length == 0) own.RemoveAt(0);
            while (own.Count > 0 && own[^1].Trim().Length == 0) own.RemoveAt(own.Count - 1);
            result.AddRange(own);
            foreach (var block in blocks)
            {
                if (result.Count > 0) result.Add("");
                result.AddRange(block);
            }
            if (defs.Count > 0)
            {
                if (result.Count > 0) result.Add("");
                result.AddRange(defs);
            }
            // the end of a container is a definition: it must not stick to a paragraph
            if (container != null && container.EndLine >= 0 && result.Count > 0) result.Add("");
            if (container == null && first + count == lines.Length && lines.Length > 0 && lines[^1].Length == 0) result.Add("");   // keep the final line break
            return (first, count, result);
        }
    }
}
