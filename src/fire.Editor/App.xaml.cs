using System.Threading.Tasks;
using System.Windows;

namespace fire.Editor
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // the standard packages (the bridges) are installed from PackageSource when they are missing - quick when nothing changed; never in the way of the start
            Task.Run(() => fire.Package.Manager.StandardPackages.EnsureInstalled());
        }
    }
}
