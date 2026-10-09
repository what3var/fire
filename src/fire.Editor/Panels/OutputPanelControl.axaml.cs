using Avalonia.Controls;

namespace fire.Editor
{
    /// <summary>The output of the running program: a read-only text that only ever grows at the end (and scrolls along).</summary>
    public partial class OutputPanelControl : UserControl
    {
        public OutputPanelControl()
        {
            InitializeComponent();
            Editor.TextArea.Options.EnableHyperlinks = false;
            Editor.TextArea.Options.EnableEmailHyperlinks = false;
        }

        public void Append(string text)
        {
            Editor.Document.Insert(Editor.Document.TextLength, text);
            Editor.ScrollToLine(Editor.Document.LineCount);
        }

        public void Clear() => Editor.Document.Text = "";
    }
}
