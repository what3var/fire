using System;
using fire.Native;

namespace fire.Editor
{
    // Work that has no build window (BackgroundWork): the standard packages installed at the start, the natives of a package or the library of graphics and windows built for the
    // VM - by spark itself (a preview, the circuits, a run) or by a child process it started (the assistant: forge writes the lines of BackgroundWork.LinePrefix). The status bar
    // shows the work with a small bar while it runs.
    public partial class MainWindow
    {
        /// <summary>What the assistant's forge reports (null: nothing).</summary>
        private BackgroundWork.Item? _childWork;

        /// <summary>A line of a child process: true when it was a progress line (it is then not part of the child's output).</summary>
        private bool ChildWorkLine(string line)
        {
            if (!BackgroundWork.TryParse(line, out var item)) return false;
            _childWork = item;
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateWorkStatus);
            return true;
        }

        /// <summary>The child process ended: what it reported is over.</summary>
        private void ChildWorkEnded()
        {
            _childWork = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateWorkStatus);
        }

        /// <summary>Hides the bar a moment after the work ended - between two packages it would flicker.</summary>
        private Avalonia.Threading.DispatcherTimer? _workHide;

        private void UpdateWorkStatus()
        {
            var item = BackgroundWork.Latest ?? _childWork;
            if (item == null)
            {
                if (!WorkStatus.IsVisible || _workHide != null) return;
                _workHide = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                _workHide.Tick += (_, _) =>
                {
                    _workHide?.Stop();
                    _workHide = null;
                    if ((BackgroundWork.Latest ?? _childWork) != null) return;
                    WorkStatus.IsVisible = false;
                    // (an indeterminate bar animates even while it is hidden - a redraw of the window 60 times a second, for nothing)
                    WorkStatusBar.IsIndeterminate = false;
                };
                _workHide.Start();
                return;
            }
            WorkStatus.IsVisible = true;
            var i = item.Value;
            WorkStatusText.Text = i.Text + "...";
            WorkStatusBar.IsIndeterminate = i.Total <= 0;
            if (i.Total > 0)
            {
                WorkStatusBar.Maximum = i.Total;
                WorkStatusBar.Value = i.Done;
            }
            Avalonia.Controls.ToolTip.SetTip(WorkStatus, i.Text + " - only the first time (or after an update): later starts are quick.");
        }
    }
}
