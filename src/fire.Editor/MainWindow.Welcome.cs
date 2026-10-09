using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using fire.Projects;

namespace fire.Editor
{
    public partial class MainWindow
    {
        private readonly RecentWorkspaces _recent = new();
        private readonly EditorSettings _settings = EditorSettings.Load();

        /// <summary>Notes a project or solution that was opened or made: it goes to the top of the list of the welcome window and of File > Open Recent.</summary>
        private void RememberWorkspace(string? path)
        {
            if (!string.IsNullOrEmpty(path)) _recent.Add(path);
        }

        /// <summary>The welcome window: the recent projects and what to do next. Shown when the editor starts (unless the user turned it off) and with ? > Welcome.</summary>
        private async Task ShowWelcome()
        {
            var dialog = new WelcomeWindow(_recent, _settings.ShowWelcome);
            await dialog.ShowDialog<bool?>(this);
            if (_settings.ShowWelcome != dialog.ShowAtStartup)
            {
                _settings.ShowWelcome = dialog.ShowAtStartup;
                _settings.Save();
            }
            switch (dialog.Action)
            {
                case WelcomeAction.NewSolution: NewSolution_Click(this, new RoutedEventArgs()); break;
                case WelcomeAction.NewProject: await NewProject(addToSolution: _workspace.Solution != null, null); break;
                case WelcomeAction.OpenWorkspace: OpenWorkspace_Click(this, new RoutedEventArgs()); break;
                case WelcomeAction.OpenFile: Open_Click(this, new RoutedEventArgs()); break;
                case WelcomeAction.NewScript: New_Click(this, new RoutedEventArgs()); break;
                case WelcomeAction.FirstSteps: HelpFirstSteps_Click(this, new RoutedEventArgs()); break;
                case WelcomeAction.OpenRecent: await OpenRecent(dialog.Path!); break;
            }
        }

        /// <summary>Opens a project or solution from the list; one that is gone is offered for removal from the list.</summary>
        private async Task OpenRecent(string path)
        {
            if (!File.Exists(path))
            {
                var answer = await Dialogs.Ask(this, $"'{path}' does not exist any more.\nTake it out of the list?", "Open Recent", ("Remove", Dialogs.Answer.Yes), ("Keep", Dialogs.Answer.No));
                if (answer == Dialogs.Answer.Yes) _recent.Remove(path);
                return;
            }
            OpenWorkspace(path);
        }

        private async void Welcome_Click(object? sender, RoutedEventArgs e) => await ShowWelcome();

        private void OpenRecentMenu_SubmenuOpened(object? sender, RoutedEventArgs e)
        {
            mnuOpenRecent.Items.Clear();
            if (_recent.Entries.Count == 0)
            {
                mnuOpenRecent.Items.Add(new MenuItem { Header = "(nothing opened yet)", IsEnabled = false });
                return;
            }
            foreach (var entry in _recent.Entries.Take(10))
            {
                var item = new MenuItem { Header = entry.Name.Replace("_", "__") + (entry.IsSolution ? "  (solution)" : "") };
                ToolTip.SetTip(item, entry.Path);
                string path = entry.Path;
                item.Click += async (_, _) => await OpenRecent(path);
                mnuOpenRecent.Items.Add(item);
            }
            mnuOpenRecent.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear the List" };
            clear.Click += (_, _) => _recent.Clear();
            mnuOpenRecent.Items.Add(clear);
        }
    }
}
