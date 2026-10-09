using System.Linq;
using Avalonia.Controls;

namespace fire.Editor
{
    /// <summary>A row of the value stack.</summary>
    public sealed record StackRow(int Position, string Type, string Text);

    /// <summary>The value stack of the active thread, topmost value first.</summary>
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
            var vm = _session?.Vm;
            if (vm == null)
            {
                StackView.ItemsSource = null;
                return;
            }

            StackView.ItemsSource = vm.DebugStackSnapshot
                .Reverse()
                .Select((v, i) => new StackRow(i, v.Kind.ToString(), v.ToString()))
                .ToList();
        }
    }
}
