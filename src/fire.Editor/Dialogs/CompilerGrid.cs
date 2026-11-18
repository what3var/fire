using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using fire.Editor.Setup;
using fire.Native;

namespace fire.Editor
{
    /// <summary>
    /// The grid of the compilers (Tools > Options > Compilers, the toolchain page of the setup wizard): one row per toolchain - its name, what it is like, its path and version
    /// or "not installed - download" - and two columns of radio buttons, Build and Publish (one choice per column), with "Automatic" on top and the recommended one marked.
    /// Below it "Download missing toolchains (TCC/GCC)" (when this machine can download any), what the choice comes to, and that a project's own toolchain goes first.
    /// Works on a <see cref="CompilerChoiceModel"/>; <see cref="Changed"/> after every click.
    /// </summary>
    internal sealed class CompilerGrid : StackPanel
    {
        private readonly CompilerChoiceModel _model;
        private readonly string _group = Guid.NewGuid().ToString("N");
        private readonly TextBlock _build = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _publish = new() { TextWrapping = TextWrapping.Wrap };
        private readonly List<(RadioButton Radio, ToolchainPurpose Purpose, string Kind)> _radios = new();

        public event Action? Changed;

        public CompilerGrid(CompilerChoiceModel model, bool compact = false)
        {
            _model = model;
            Spacing = 10;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,130,130") };
            int row = 0;
            void AddRow(Control first, Control build, Control publish, bool line = true)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                if (line)
                {
                    var border = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 1, 0, 0) };
                    Grid.SetRow(border, row); Grid.SetColumnSpan(border, 3);
                    grid.Children.Add(border);
                }
                Grid.SetRow(first, row); Grid.SetRow(build, row); Grid.SetRow(publish, row);
                Grid.SetColumn(build, 1); Grid.SetColumn(publish, 2);
                grid.Children.Add(first); grid.Children.Add(build); grid.Children.Add(publish);
                row++;
            }
            TextBlock Head(string text) => new()
            {
                Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = SettingsLayout.AccentText, LetterSpacing = 0.8,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
            };
            var headBuild = new StackPanel { Children = { Head("Build"), Sub("Build, Run, Debug") } };
            var headPublish = new StackPanel { Children = { Head("Publish"), Sub("Publish, forge publish") } };
            AddRow(new TextBlock { Text = "TOOLCHAIN", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = SettingsLayout.AccentText, LetterSpacing = 0.8, Margin = new Thickness(0, 0, 0, 6) },
                headBuild, headPublish, line: false);

            AddRow(Cell("Automatic", "the recommended one when it is there, else the best one there", null, true),
                Radio(ToolchainPurpose.Build, MachineToolchains.Automatic, true), Radio(ToolchainPurpose.Publish, MachineToolchains.Automatic, true));
            foreach (var r in model.Rows)
            {
                bool can = CompilerChoiceModel.CanChoose(r);
                AddRow(Cell(r.Name, r.Traits, r, can), Radio(ToolchainPurpose.Build, r.Kind, can), Radio(ToolchainPurpose.Publish, r.Kind, can));
            }
            Children.Add(new Border
            {
                BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 4), Child = grid,
            });

            if (model.OffersDownloads)
            {
                var box = new CheckBox { Content = "Download missing toolchains (TCC/GCC)", IsChecked = model.DownloadMissing };
                box.IsCheckedChanged += (_, _) => { model.DownloadMissing = box.IsChecked == true; Refresh(); };
                ToolTip.SetTip(box, $"The chosen compilers that are not there are downloaded and kept next to spark in {ToolchainSetup.Root} - nothing is installed on the system.");
                Children.Add(box);
            }
            Children.Add(new Border
            {
                Background = new SolidColorBrush(EditorTheme.AccentColor, 0.12), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8),
                Child = new StackPanel { Spacing = 2, Children = { _build, _publish } },
            });
            if (!compact)
                Children.Add(SettingsLayout.Hint("A project's own toolchain goes first: Project Properties > Native & Toolchain (or \"toolchain\" in fire.native.json, a target that names one, "
                    + "forge --toolchain). Then this choice; Debug, Release and Performance stay build modes. TinyCC compiles only C: a program with natives in C++ is handed to the "
                    + "next compiler, and the build log says so."));
            Refresh();
        }

        private static TextBlock Sub(string text) => new() { Text = text, FontSize = 11, Foreground = EditorTheme.TextDim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };

        private Control Cell(string name, string traits, CompilerRow? row, bool enabled)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (row != null)
            {
                if (_model.Recommended(ToolchainPurpose.Build) == row.Kind) head.Children.Add(Badge("Recommended for Build", EditorTheme.Magenta));
                if (_model.Recommended(ToolchainPurpose.Publish) == row.Kind) head.Children.Add(Badge("Recommended for Publish", EditorTheme.Purple));
            }
            var cell = new StackPanel { Margin = new Thickness(0, 8, 8, 8), Spacing = 1, Opacity = enabled ? 1 : 0.55 };
            cell.Children.Add(head);
            cell.Children.Add(new TextBlock { Text = traits, FontSize = 12, Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap });
            if (row != null)
            {
                var state = new TextBlock
                {
                    Text = row.StateText, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = row.Installed ? EditorTheme.Comment : row.Installable ? EditorTheme.Yellow : EditorTheme.WineRed,
                };
                if (row.Path != null) ToolTip.SetTip(state, row.Path);
                cell.Children.Add(state);
            }
            return cell;
        }

        public static Control Badge(string text, IBrush brush) => new Border
        {
            BorderBrush = brush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10, Foreground = brush },
        };

        private Control Radio(ToolchainPurpose purpose, string kind, bool enabled)
        {
            var radio = new RadioButton
            {
                GroupName = _group + purpose, IsChecked = _model.Choice(purpose) == kind, IsEnabled = enabled,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(radio, (purpose == ToolchainPurpose.Build ? "Build, Run, Debug and Build Solution use " : "Publish and forge publish use ") + (kind == MachineToolchains.Automatic ? "the automatic choice" : kind));
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked != true) return;
                _model.Choose(purpose, kind);
                Refresh();
            };
            _radios.Add((radio, purpose, kind));
            return radio;
        }

        private void Refresh()
        {
            _build.Text = _model.Summary(ToolchainPurpose.Build);
            _publish.Text = _model.Summary(ToolchainPurpose.Publish);
            Changed?.Invoke();
        }
    }
}
