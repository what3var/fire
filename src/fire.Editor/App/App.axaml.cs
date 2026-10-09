using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace fire.Editor
{
    public partial class App : Application
    {
        private static readonly TaskCompletionSource SplashDone = new();

        /// <summary>Completes when the splash screen is gone (at once if there is none): the welcome window waits for it.</summary>
        internal static Task SplashClosed => SplashDone.Task;

        /// <summary>How long the splash stays at least, so that it does not just flash.</summary>
        private static readonly TimeSpan SplashMinimum = TimeSpan.FromMilliseconds(1400);

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // The splash screen goes up first and is painted; the main window (the layout, the editors) is made right after, so the splash is there while it takes its time.
                SplashWindow? splash = null;
                try
                {
                    splash = new SplashWindow();
                    splash.Show();
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);   // no splash is no reason not to start
                    splash = null;
                }
                var shown = DateTime.UtcNow;
                Dispatcher.UIThread.Post(() => StartMainWindow(desktop, splash, shown), DispatcherPriority.Background);
            }
            base.OnFrameworkInitializationCompleted();
        }

        private static void StartMainWindow(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow? splash, DateTime splashShown)
        {
            splash?.SetStatus("Loading the editor...");
            var main = new MainWindow();
            desktop.MainWindow = main;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
            // the standard packages (the bridges) are installed from PackageSource when they are missing - quick when nothing changed; never in the way of the start
            Task.Run(() => fire.Package.Manager.StandardPackages.EnsureInstalled());

            if (splash == null) { SplashDone.TrySetResult(); return; }
            splash.SetStatus("Ready");
            var wait = SplashMinimum - (DateTime.UtcNow - splashShown);
            if (wait < TimeSpan.FromMilliseconds(100)) wait = TimeSpan.FromMilliseconds(100);
            var timer = new DispatcherTimer { Interval = wait };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                splash.Close();
                SplashDone.TrySetResult();
            };
            timer.Start();
        }
    }
}
