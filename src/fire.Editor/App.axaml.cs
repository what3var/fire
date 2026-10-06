using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace fire.Editor
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
                // the standard packages (the bridges) are installed from PackageSource when they are missing - quick when nothing changed; never in the way of the start
                Task.Run(() => fire.Package.Manager.StandardPackages.EnsureInstalled());
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
