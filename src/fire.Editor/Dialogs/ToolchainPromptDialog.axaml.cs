using Avalonia.Controls;
using Avalonia.Interactivity;
using fire.Native;

namespace fire.Editor
{
    /// <summary>Asked when a native build or a package with native code needs a C++ toolchain and there is none: install it automatically (the portable w64devkit, Windows), change the
    /// toolchain settings, or cancel. The user must not be surprised that a download or a compiler is involved, so the dialog says why.</summary>
    public partial class ToolchainPromptDialog : Window
    {
        public ToolchainChoice Choice { get; private set; } = ToolchainChoice.Cancel;

        public ToolchainPromptDialog(ToolchainRequest request)
        {
            InitializeComponent();
            txtMessage.Text = request.Message;
            Title = request.IsToolchain ? "C++ toolchain needed" : request.Component + " needed";
            txtInstallTitle.Text = request.IsToolchain ? "Install the toolchain automatically" : $"Install {request.Component} automatically";
            btnInstall.IsEnabled = request.CanInstall;
            btnChange.IsEnabled = request.AllowChange;
            txtInstallHint.Text = request.InstallHint;
        }

        private void Install_Click(object? sender, RoutedEventArgs e) { Choice = ToolchainChoice.Install; Close(true); }
        private void Change_Click(object? sender, RoutedEventArgs e) { Choice = ToolchainChoice.Change; Close(true); }
        private void Cancel_Click(object? sender, RoutedEventArgs e) { Choice = ToolchainChoice.Cancel; Close(false); }
    }
}
