using System.Linq;
using Avalonia.Controls;

namespace fire.Editor
{
    /// <summary>A row of the value stack.</summary>
    public sealed record StackRow(int Position, string Type, string Text);

    /// <summary>The value stack of the active thread, topmost value first (only the VM has one - the panel is hidden for a native run, see
    /// MainWindow.ApplyEngineToPanels).</summary>
    public partial class StackPanelControl : UserControl
    {
        private DebugSession? _session;

        public StackPanelControl()
        {
            InitializeComponent();
        }

        public void AttachSession(DebugSession session) => _session = session;

        public void Refresh()
        {
            var stack = _session?.ActiveThread?.Thread.OperandStack();
            if (stack == null)
            {
                StackView.ItemsSource = null;
                return;
            }

            StackView.ItemsSource = stack
                .Reverse()
                .Select((v, i) => new StackRow(i, v.TypeName, v.Display))
                .ToList();
        }
    }
}
