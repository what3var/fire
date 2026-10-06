using System.Windows;
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
            btnInstall.IsEnabled = request.CanInstall;
            txtInstallHint.Text = request.CanInstall
                ? $"w64devkit is downloaded ({ToolchainSetup.DownloadSizeHint}) and kept locally in {request.InstallDirectory}. Nothing is installed on the system."
                : "Only available on Windows. Install a C++ compiler with the package manager of your system.";
        }

        private void Install_Click(object sender, RoutedEventArgs e) { Choice = ToolchainChoice.Install; DialogResult = true; }
        private void Change_Click(object sender, RoutedEventArgs e) { Choice = ToolchainChoice.Change; DialogResult = true; }
        private void Cancel_Click(object sender, RoutedEventArgs e) { Choice = ToolchainChoice.Cancel; DialogResult = false; }
    }
}
