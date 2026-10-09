using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>Small modal dialogs (message, question) - Avalonia has no MessageBox of its own. All of them are asynchronous: they return when the user has answered.</summary>
    internal static class Dialogs
    {
        public enum Answer { Ok, Yes, No, Cancel }

        /// <summary>The window of a control (the owner of a dialog), null if the control is not shown.</summary>
        public static Window? WindowOf(Visual? control) => control == null ? null : TopLevel.GetTopLevel(control) as Window;

        /// <summary>Shows a message with an OK button.</summary>
        public static Task Message(Window? owner, string text, string title = "spark") => Ask(owner, text, title, ("OK", Answer.Ok));

        /// <summary>Asks a question with the given buttons (the first one is the default); the answer of the pressed one, Cancel if the window is closed.</summary>
        public static Task<Answer> Ask(Window? owner, string text, string title, params (string Label, Answer Answer)[] buttons)
        {
            var result = Answer.Cancel;
            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
            var dialog = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 320,
                MaxWidth = 640,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                CanResize = false,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(18),
                    Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, buttonRow },
                },
            };
            bool first = true;
            foreach (var (label, answer) in buttons)
            {
                var button = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
                if (first) button.Classes.Add("accent");
                first = false;
                var captured = answer;
                button.Click += (_, _) => { result = captured; dialog.Close(); };
                buttonRow.Children.Add(button);
            }
            return ShowAsync(dialog, owner, () => result);
        }

        private static async Task<Answer> ShowAsync(Window dialog, Window? owner, Func<Answer> result)
        {
            if (owner != null) await dialog.ShowDialog(owner);
            else
            {
                var closed = new TaskCompletionSource();
                dialog.Closed += (_, _) => closed.TrySetResult();
                dialog.Show();
                await closed.Task;
            }
            return result();
        }

        /// <summary>Asks for a line of text; null if the user cancels.</summary>
        public static async Task<string?> Input(Window? owner, string prompt, string title, string initial = "")
        {
            string? result = null;
            var box = new TextBox { Text = initial, MinWidth = 280, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "OK", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            var dialog = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                CanResize = false,
                ShowInTaskbar = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(18),
                    Children =
                    {
                        new TextBlock { Text = prompt }, box,
                        new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } },
                    },
                },
            };
            ok.Click += (_, _) => { result = box.Text; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { result = box.Text; dialog.Close(); } };
            dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
            await ShowAsync(dialog, owner, () => Answer.Cancel);
            return result;
        }
    }
}
