using Avalonia;

namespace fire.Editor
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            int code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            // The window is closed and spark has cleaned up (MainWindow.OnClosing). What still runs must not keep the process alive in the background, where
            // nobody sees it: a thread of a program that ran in the VM (a fire thread, a server loop, a call that waits), a device driver's program. They get
            // a moment to end, then the process ends anyway.
            ExitGuard.Arm(code, ExitGuard.Grace);
            return code;
        }

        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
