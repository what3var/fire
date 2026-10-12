using System;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace fire.Editor
{
    /// <summary>The log of the last build, next to the output of the program: what the compilers said (the C++ compiler of a native build with its stdout and stderr) and the errors and warnings of
    /// the fire compiler (`main.fire(12): error E4022: Unknown identifier 'x'`). Read-only text that only grows at the end; a new build clears it. A double click on a line of an error or a
    /// warning jumps to its place.</summary>
    public partial class BuildLogPanelControl : UserControl
    {
        private static readonly Regex Located = new(@"^(?<file>.+?)\((?<line>\d+)\): (?:error|warning)\b", RegexOptions.Compiled);
        private static readonly Regex Unlocated = new(@"\b(?:error|warning) [EW]\d+: .*\(line (?<line>\d+)\)\s*$", RegexOptions.Compiled);

        /// <summary>Fires when a line of an error or a warning is double clicked: the file (null: the file the build was for) and the line.</summary>
        public event Action<string?, int>? LocationActivated;

        public BuildLogPanelControl()
        {
            InitializeComponent();
            Editor.TextArea.Options.EnableHyperlinks = false;
            Editor.TextArea.Options.EnableEmailHyperlinks = false;
            Editor.TextArea.TextView.LineTransformers.Add(new LogColorizer());
            Editor.TextArea.DoubleTapped += (_, _) => ActivateCurrentLine();
        }

        public void Append(string text)
        {
            if (text.Length == 0) return;
            Editor.Document.Insert(Editor.Document.TextLength, text);
            Editor.ScrollToLine(Editor.Document.LineCount);
        }

        public void AppendLine(string text) => Append(text + Environment.NewLine);

        public void Clear() => Editor.Document.Text = "";

        public string Text => Editor.Document.Text;

        private void ActivateCurrentLine()
        {
            int number = Editor.TextArea.Caret.Line;
            if (number < 1 || number > Editor.Document.LineCount) return;
            var line = Editor.Document.GetLineByNumber(number);
            string text = Editor.Document.GetText(line.Offset, line.Length).TrimEnd();
            var located = Located.Match(text);
            if (located.Success) { LocationActivated?.Invoke(located.Groups["file"].Value, int.Parse(located.Groups["line"].Value)); return; }
            var unlocated = Unlocated.Match(text);
            if (unlocated.Success) LocationActivated?.Invoke(null, int.Parse(unlocated.Groups["line"].Value));
        }

        /// <summary>Errors red, warnings amber, the rest as it is.</summary>
        private sealed class LogColorizer : DocumentColorizingTransformer
        {
            private static readonly IBrush Error = new SolidColorBrush(Color.FromRgb(0xE5, 0x56, 0x6F));
            private static readonly IBrush Warning = new SolidColorBrush(Color.FromRgb(0xE5, 0xB5, 0x4F));

            protected override void ColorizeLine(DocumentLine line)
            {
                string text = CurrentContext.Document.GetText(line);
                IBrush? brush = null;
                if (Regex.IsMatch(text, @"(^|: |\s)error( [EW]\d+)?:")) brush = Error;
                else if (Regex.IsMatch(text, @"(^|: |\s)warning( [EW]\d+)?:")) brush = Warning;
                if (brush == null) return;
                ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }
}
