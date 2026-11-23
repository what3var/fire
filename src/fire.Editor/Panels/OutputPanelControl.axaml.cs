using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace fire.Editor
{
    /// <summary>The output of the running program: a read-only text that only ever grows at the end (and scrolls along). Below it, while a program takes
    /// input from here (<see cref="InputTarget"/>: a program on a board, whose console is its standard input), a line for its standard input. A double click on a
    /// line of a stack trace (`   at Store.Find (main.fire:16)`, docs/spec/STACKTRACE.md) jumps to its place (<see cref="LocationActivated"/>).</summary>
    public partial class OutputPanelControl : UserControl
    {
        private Func<string, bool>? _inputTarget;

        /// <summary>Fires when a line of a stack trace is double clicked: its file as the trace names it (relative to the folder of the program's first source) and
        /// its line. A frame of the prelude or one without a line has no place.</summary>
        public event Action<string, int>? LocationActivated;

        public OutputPanelControl()
        {
            InitializeComponent();
            Editor.TextArea.Options.EnableHyperlinks = false;
            Editor.TextArea.Options.EnableEmailHyperlinks = false;
            InputBox.KeyDown += InputBox_KeyDown;
            Editor.TextArea.DoubleTapped += (_, _) => ActivateCurrentLine();
        }

        private void ActivateCurrentLine()
        {
            int number = Editor.TextArea.Caret.Line;
            if (number < 1 || number > Editor.Document.LineCount) return;
            var line = Editor.Document.GetLineByNumber(number);
            if (fire.Tooling.OutputLocations.TraceFrame(Editor.Document.GetText(line.Offset, line.Length)) is { } at) LocationActivated?.Invoke(at.File, at.Line);
        }

        public void Append(string text)
        {
            Editor.Document.Insert(Editor.Document.TextLength, text);
            Editor.ScrollToLine(Editor.Document.LineCount);
        }

        public void Clear() => Editor.Document.Text = "";

        /// <summary>What the program wrote so far.</summary>
        public string Text => Editor.Document.Text;

        /// <summary>Where a line typed below the output goes (true: it went out); null: no program takes input from here - the line is hidden.</summary>
        public Func<string, bool>? InputTarget
        {
            get => _inputTarget;
            set
            {
                _inputTarget = value;
                InputBox.IsVisible = value != null;
                if (value == null) InputBox.Text = "";
            }
        }

        private void InputBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _inputTarget == null) return;
            e.Handled = true;
            string line = InputBox.Text ?? "";
            if (_inputTarget(line)) InputBox.Text = "";
        }
    }
}
