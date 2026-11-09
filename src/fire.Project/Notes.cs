using System.Text;
using System.Text.RegularExpressions;

namespace fire.Projects
{
    /// <summary>The state of a task in a todo list (docs/tools/notes.md): `[ ]` open, `[/]` in progress, `[#]` blocked, `[x]` done.</summary>
    public enum TodoState { Open, InProgress, Blocked, Done }

    /// <summary>
    /// Where the notes of a project or a solution live (docs/tools/notes.md): the folder `notes/` next to the `.fireproj` or `.firesln`, or the folder its `"notes"` names.
    /// The folder and the files in it are made when the first entry is written.
    /// </summary>
    public static class ProjectNotes
    {
        public const string DefaultFolder = "notes";
        public const string TodoFileName = "todo.md";
        public const string OverviewFileName = "overview.xmd";

        /// <summary>The notes folder of the project or solution file `ownerFile`; `setting` is its `"notes"` (null or empty: `notes/`).</summary>
        public static string Folder(string ownerFile, string? setting)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(ownerFile))!;
            return Path.GetFullPath(Path.Combine(dir, string.IsNullOrWhiteSpace(setting) ? DefaultFolder : setting.Trim()));
        }

        public static string Folder(FireProject project) => Folder(project.FilePath ?? throw new ProjectException("The project has no file name."), project.Notes);
        public static string Folder(FireSolution solution) => Folder(solution.FilePath ?? throw new ProjectException("The solution has no file name."), solution.Notes);

        public static string TodoPath(FireProject project) => Path.Combine(Folder(project), TodoFileName);
        public static string TodoPath(FireSolution solution) => Path.Combine(Folder(solution), TodoFileName);
        public static string OverviewPath(FireProject project) => Path.Combine(Folder(project), OverviewFileName);
        public static string OverviewPath(FireSolution solution) => Path.Combine(Folder(solution), OverviewFileName);

        /// <summary>The character between the brackets.</summary>
        public static char Mark(TodoState state) => state switch
        {
            TodoState.InProgress => '/', TodoState.Blocked => '#', TodoState.Done => 'x', _ => ' ',
        };

        /// <summary>The state of a mark; null: not a mark of a task.</summary>
        public static TodoState? StateOf(char mark) => mark switch
        {
            ' ' => TodoState.Open, '/' => TodoState.InProgress, '#' => TodoState.Blocked, 'x' or 'X' => TodoState.Done, _ => null,
        };

        /// <summary>What a click on the mark makes of a state: open → in progress → done → open; a blocked task becomes open again.</summary>
        public static TodoState Next(TodoState state) => state switch
        {
            TodoState.Open => TodoState.InProgress, TodoState.InProgress => TodoState.Done, _ => TodoState.Open,
        };
    }

    /// <summary>A task of a <see cref="TodoList"/>: where it is in the file and what it says.</summary>
    public sealed class TodoItem
    {
        /// <summary>The line of the task (0-based).</summary>
        public int Line { get; internal init; }
        /// <summary>The indentation (a tab counts 4).</summary>
        public int Indent { get; internal init; }
        /// <summary>`-`, `*`, `+` or a number with `.` or `)`.</summary>
        public string Bullet { get; internal init; } = "-";
        public TodoState State { get; internal init; }
        public string Text { get; internal init; } = "";
        /// <summary>The heading (level 2 or deeper) the task is below; null: before the first one.</summary>
        public string? Group { get; internal init; }
        public TodoItem? Parent { get; internal init; }
        public List<TodoItem> Children { get; } = new();
        /// <summary>0 for a task, 1 for a subtask, ...</summary>
        public int Depth => Parent == null ? 0 : Parent.Depth + 1;

        internal int MarkColumn { get; init; }
        /// <summary>The line after the task with its subtasks and notes (the lines indented below it).</summary>
        internal int End { get; set; }

        public override string ToString() => $"[{ProjectNotes.Mark(State)}] {Text}";
    }

    /// <summary>
    /// A todo list (docs/tools/notes.md): a Markdown file whose list items with `[ ]`, `[/]`, `[#]` or `[x]` are tasks, grouped by the headings of level 2 and deeper.
    /// Changes go only to the lines they concern - the rest of the file (paragraphs, notes, code, line endings) stays as it was written. After a change the tasks are read
    /// again: <see cref="Items"/> holds new objects, which are found again by their <see cref="TodoItem.Line"/>.
    /// </summary>
    public sealed class TodoList
    {
        private static readonly Regex TaskLine = new(@"^(?<indent>[ \t]*)(?<bullet>[-*+]|\d{1,9}[.)])[ \t]+\[(?<mark>[ xX/#])\](?:[ \t]+(?<text>.*?))?[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex Heading = new(@"^ {0,3}(?<level>#{1,6})(?:[ \t]+(?<text>.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex Fence = new(@"^ {0,3}(```|~~~)", RegexOptions.Compiled);

        private readonly List<string> _lines;
        private readonly string _newline;
        private readonly bool _finalNewline;

        public const string DefaultTitle = "Todo";

        /// <summary>The heading of level 1 (the first one); null: none.</summary>
        public string? Title { get; private set; }
        /// <summary>Every task, in the order of the file.</summary>
        public IReadOnlyList<TodoItem> Items { get; private set; } = Array.Empty<TodoItem>();
        /// <summary>The headings of level 2 and deeper, in the order of the file (also those without tasks).</summary>
        public IReadOnlyList<string> Groups { get; private set; } = Array.Empty<string>();
        /// <summary>false: there is no text yet (a list that does not exist as a file).</summary>
        public bool HasContent => _lines.Count > 0;

        private readonly List<(string Name, int Line)> _groupLines = new();

        private TodoList(List<string> lines, string newline, bool finalNewline)
        {
            _lines = lines;
            _newline = newline;
            _finalNewline = finalNewline;
            Read();
        }

        public static TodoList Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return Empty();
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            bool final = text.EndsWith('\n');
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            if (final) lines.RemoveAt(lines.Count - 1);   // the empty part after the last line break
            return new TodoList(lines, newline, final);
        }

        /// <summary>A list without text: the first task makes `# Todo` and the task.</summary>
        public static TodoList Empty() => new(new List<string>(), "\n", true);

        /// <summary>The list of the file; an empty list if the file does not exist.</summary>
        public static TodoList Load(string path) => File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty();

        /// <summary>Writes the list; the folder is made if needed.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, ToString());
        }

        public override string ToString()
        {
            if (_lines.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i > 0) sb.Append(_newline);
                sb.Append(_lines[i]);
            }
            if (_finalNewline) sb.Append(_newline);
            return sb.ToString();
        }

        /// <summary>The task on a line; null: none (any more).</summary>
        public TodoItem? ItemAt(int line) => Items.FirstOrDefault(i => i.Line == line);

        // ---------------------------------------------------------------- reading

        private static int IndentOf(string line)
        {
            int n = 0;
            foreach (char c in line)
            {
                if (c == ' ') n++;
                else if (c == '\t') n += 4 - n % 4;
                else break;
            }
            return n;
        }

        private void Read()
        {
            var items = new List<TodoItem>();
            var stack = new List<TodoItem>();   // the tasks a new line can be below, outermost first
            var fenced = new bool[_lines.Count];
            _groupLines.Clear();
            string? group = null;
            Title = null;
            bool inFence = false;

            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i];
                bool fenceLine = Fence.IsMatch(line);
                fenced[i] = inFence || fenceLine;
                if (fenceLine) inFence = !inFence;
                if (line.Trim().Length == 0) continue;
                int indent = IndentOf(line);
                if (fenced[i]) { Pop(stack, indent); continue; }

                var task = TaskLine.Match(line);
                if (task.Success)
                {
                    while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
                    var parent = stack.Count > 0 ? stack[^1] : null;
                    var item = new TodoItem
                    {
                        Line = i, Indent = indent, Bullet = task.Groups["bullet"].Value, State = ProjectNotes.StateOf(task.Groups["mark"].Value[0])!.Value,
                        Text = task.Groups["text"].Success ? task.Groups["text"].Value : "", Group = group, Parent = parent,
                        MarkColumn = task.Groups["mark"].Index,
                    };
                    parent?.Children.Add(item);
                    items.Add(item);
                    stack.Add(item);
                    continue;
                }

                var heading = indent < 4 ? Heading.Match(line) : Match.Empty;
                if (heading.Success)
                {
                    stack.Clear();
                    string text = heading.Groups["text"].Success ? heading.Groups["text"].Value.Trim() : "";
                    if (heading.Groups["level"].Value.Length == 1) { Title ??= text; group = null; }
                    else { group = text; _groupLines.Add((text, i)); }
                    continue;
                }
                Pop(stack, indent);   // a line that is not indented below a task ends it
            }

            // where a task ends: after the last line that is indented below it (empty lines between them belong to it, empty lines after it do not)
            foreach (var item in items)
            {
                int end = item.Line + 1;
                for (int j = item.Line + 1; j < _lines.Count; j++)
                {
                    if (_lines[j].Trim().Length == 0) continue;
                    if (IndentOf(_lines[j]) > item.Indent) { end = j + 1; continue; }
                    break;
                }
                item.End = end;
            }

            Items = items;
            Groups = _groupLines.Select(g => g.Name).Distinct().ToList();
        }

        private static void Pop(List<TodoItem> stack, int indent)
        {
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
        }

        /// <summary>The last line before `before` that is not empty; -1: none.</summary>
        private int LastContentLine(int before)
        {
            for (int i = Math.Min(before, _lines.Count) - 1; i >= 0; i--)
                if (_lines[i].Trim().Length > 0) return i;
            return -1;
        }

        // ---------------------------------------------------------------- changes

        private TodoItem Current(TodoItem item) =>
            Items.FirstOrDefault(i => i.Line == item.Line) is { } found && found.Text == item.Text ? found
            : throw new InvalidOperationException("The task is not in the list (any more).");

        /// <summary>Gives the task another state: only the character between the brackets changes.</summary>
        public void SetState(TodoItem item, TodoState state)
        {
            item = Current(item);
            string line = _lines[item.Line];
            _lines[item.Line] = line.Substring(0, item.MarkColumn) + ProjectNotes.Mark(state) + line.Substring(item.MarkColumn + 1);
            Read();
        }

        /// <summary>Gives the task another text (line breaks become spaces): only the text of its line changes.</summary>
        public void SetText(TodoItem item, string text)
        {
            item = Current(item);
            text = OneLine(text);
            string line = _lines[item.Line];
            string head = line.Substring(0, item.MarkColumn + 2);   // up to and with the `]`
            _lines[item.Line] = text.Length == 0 ? head : head + " " + text;
            Read();
        }

        /// <summary>Deletes the task with its subtasks and notes (the lines indented below it).</summary>
        public void Remove(TodoItem item)
        {
            item = Current(item);
            _lines.RemoveRange(item.Line, item.End - item.Line);
            Read();
        }

        /// <summary>
        /// Adds a task: below the last task of `group` (null: of the tasks before the first group), or, if there is none, after the heading of the group; a group that does
        /// not exist yet is added at the end. A list without text gets the title `# Todo` first. Returns the new task.
        /// </summary>
        public TodoItem Add(string text, string? group = null, TodoState state = TodoState.Open)
        {
            text = OneLine(text);
            if (_lines.Count == 0) _lines.Add("# " + DefaultTitle);

            var siblings = Items.Where(i => i.Group == group && i.Parent == null).ToList();
            var model = siblings.LastOrDefault() ?? Items.LastOrDefault(i => i.Parent == null);
            string indent = model == null ? "" : _lines[model.Line].Substring(0, _lines[model.Line].Length - _lines[model.Line].TrimStart(' ', '\t').Length);
            string bullet = model == null ? "-" : NextBullet(model.Bullet);
            string newLine = indent + bullet + " [" + ProjectNotes.Mark(state) + "]" + (text.Length > 0 ? " " + text : "");

            int at;
            if (siblings.Count > 0)
            {
                at = siblings[^1].End;
            }
            else if (group != null && _groupLines.FirstOrDefault(g => g.Name == group) is { Name: not null } heading)
            {
                at = heading.Line + 1;
            }
            else if (group == null && _groupLines.Count > 0)
            {
                // the first task before the first group: after what stands before it (title, text), with an empty line before the group
                at = LastContentLine(_groupLines[0].Line) + 1;
                if (at > 0) { _lines.Insert(at, ""); at++; }
                if (_lines[at].Trim().Length > 0) _lines.Insert(at, "");   // the group heading follows directly: keep it apart
            }
            else
            {
                // at the end: a new group gets its heading, both are set apart from a paragraph above by an empty line
                at = LastContentLine(_lines.Count) + 1;
                bool afterTask = at > 0 && TaskLine.IsMatch(_lines[at - 1]) && group == null;
                if (at > 0 && !afterTask) { _lines.Insert(at, ""); at++; }
                if (group != null)
                {
                    _lines.Insert(at, "## " + group);
                    at++;
                }
            }
            _lines.Insert(at, newLine);
            Read();
            return Items.First(i => i.Line == at);
        }

        private static string NextBullet(string bullet)
        {
            if (bullet.Length > 1 && int.TryParse(bullet[..^1], out int n)) return (n + 1) + bullet[^1..];
            return bullet;
        }

        private static string OneLine(string text) => Regex.Replace(text ?? "", @"\s*[\r\n]+\s*", " ").Trim();
    }
}
