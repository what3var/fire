using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace fire.Editor
{
    /// <summary>Shows the progress of the toolchain download while it runs on a worker thread; closes itself when it is done.</summary>
    public partial class ToolchainProgressDialog : Window
    {
        private readonly Action<Action<string>> _work;
        public bool Succeeded { get; private set; }
        public string? Error { get; private set; }

        public ToolchainProgressDialog(Action<Action<string>> work)
        {
            InitializeComponent();
            _work = work;
            Opened += async (_, _) =>
            {
                try { await Task.Run(() => _work(m => Dispatcher.UIThread.Post(() => txtStatus.Text = m))); Succeeded = true; }
                catch (Exception ex) { Error = ex.Message; }
                if (Error != null) await Dialogs.Message(this, Error, "Toolchain");
                Close();
            };
            Closing += (_, e) => { if (!Succeeded && Error == null) e.Cancel = true; };   // not while the download runs
        }
    }
}
