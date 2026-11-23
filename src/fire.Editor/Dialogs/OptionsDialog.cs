using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using fire.Editor.Setup;
using fire.Tooling;
using fire.Projects;
using static fire.Editor.SettingsLayout;

namespace fire.Editor
{
    /// <summary>
    /// The options of spark for this machine (Tools > Options; docs/tools/spark.md "Options"), in the look of the project properties: pages at the left - General (start, the
    /// engine of a single script, the assistant, the MCP server, the ESP-IDF folder) and Compilers (the grid of <see cref="CompilerGrid"/>: the compiler for Build and the one
    /// for Publish, and the download of missing ones; the level of the quick build). The dialog works on copies; Apply puts them into <see cref="Settings"/> and <see cref="Compilers"/> - the caller saves them
    /// and starts the downloads (<see cref="CompilerChoiceModel.PlanItems"/>). `ShowDialog` returns true on Apply.
    /// </summary>
    internal sealed class OptionsDialog : Window
    {
        private readonly SettingsNav _nav = new();
        private bool _showWelcome;
        private string? _engine;
        private bool _assistant;
        private bool _mcp;
        private string? _idfPath;
        private int _quickLevel;

        public EditorSettings Settings { get; }
        public CompilerChoiceModel Compilers { get; }
        /// <summary>The user asked for the setup wizard (the button of General): the caller opens it after the dialog.</summary>
        public bool RunSetupWizard { get; private set; }

        public OptionsDialog(EditorSettings settings, CompilerChoiceModel compilers, bool assistantAvailable = true)
        {
            Settings = settings;
            Compilers = compilers;
            _showWelcome = settings.ShowWelcome;
            _engine = settings.Engine;
            _assistant = settings.Assistant;
            _mcp = settings.McpServer;
            _idfPath = settings.IdfPath;
            _quickLevel = settings.QuickBuildLevel == 1 ? 1 : 0;

            Title = "Options";
            Width = 900; Height = 640; MinWidth = 760; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _nav.AddScope("spark");
            _nav.Add("general", NavIcons.Sliders, "General", GeneralPage(assistantAvailable));
            _nav.AddScope("Native builds");
            _nav.Add("compilers", NavIcons.Chip, "Compilers", CompilersPage());

            var apply = DialogButton("Apply", accent: true);
            apply.Click += (_, _) => Accept();
            var cancel = DialogButton("Cancel");
            cancel.Click += (_, _) => Close(false);
            var hint = Hint("These options are for this machine. Apply writes spark-settings.json and Toolchain/toolchain.json and downloads what the Compilers page lists.");
            Content = Frame(_nav, new StackPanel { Children = { hint }, VerticalAlignment = VerticalAlignment.Center }, cancel, apply);
            _nav.Show("general");
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };
        }

        /// <summary>Starts on a page (general, compilers).</summary>
        public void SelectPage(string key) => _nav.Show(key);

        public string? CurrentPage => _nav.Current;

        private Control GeneralPage(bool assistantAvailable)
        {
            var welcome = new CheckBox { Content = "Show the welcome window when spark starts", IsChecked = _showWelcome };
            welcome.IsCheckedChanged += (_, _) => _showWelcome = welcome.IsChecked == true;

            var engine = new Segmented("From fire.native.json", "Virtual machine", "Native");
            engine.SelectedIndex = _engine switch { "vm" => 1, "native" => 2, _ => 0 };
            engine.SelectionChanged += (_, _) => _engine = engine.SelectedIndex switch { 1 => "vm", 2 => "native", _ => null };

            var assistant = new CheckBox { Content = "The assistant (a local AI model: tips, answers about fire, errors explained)", IsChecked = _assistant, IsEnabled = assistantAvailable };
            assistant.IsCheckedChanged += (_, _) => _assistant = assistant.IsChecked == true;
            var mcp = new CheckBox { Content = "MCP server: offer the editor to AI tools on this computer", IsChecked = _mcp };
            mcp.IsCheckedChanged += (_, _) => _mcp = mcp.IsChecked == true;

            var idf = TextField("the environment variable IDF_PATH", mono: true);
            idf.Text = _idfPath;
            idf.TextChanged += (_, _) => _idfPath = string.IsNullOrWhiteSpace(idf.Text) ? null : idf.Text!.Trim();

            var wizard = new Button { Content = "Run the setup wizard..." };
            wizard.Click += (_, _) => { RunSetupWizard = true; Close(false); };

            return Page("General", "How spark starts and runs, and its helpers.",
                Group("Start", welcome),
                Group("Running",
                    Row("Engine of a script", engine),
                    Hint("A script that is not in a project runs in this engine; a project keeps its own (Project Properties > General > Runs as). Also in Run > Run Engine.")),
                Group("Assistant",
                    assistant,
                    mcp,
                    Hint(assistantAvailable ? "The model of the assistant is chosen when it is switched on (or in the setup wizard)." : "The assistant did not come with this spark.")),
                Group("Boards", Row("ESP-IDF folder", idf), Hint("For Run and Debug on an ESP32 (also in Tools > Toolchain Settings > ESP32 Board).")),
                Group("Setup", Line(wizard), Hint("Toolchains, the model of the assistant, the first build of the standard packages - as at the first start.")));
        }

        private Control CompilersPage()
        {
            var quick = new Segmented("-O0 (the shortest build)", "-O1 (a faster program)");
            quick.SelectedIndex = _quickLevel;
            quick.SelectionChanged += (_, _) => _quickLevel = quick.SelectedIndex == 1 ? 1 : 0;
            return Page("Compilers", "Which C/C++ compiler a native build uses: one for building (Build, Run, Debug, Build Solution, forge build), one for publishing (Publish, forge publish).",
                new CompilerGrid(Compilers),
                Group("Quick build (the hare)",
                    Row("Optimisation", quick),
                    Hint("The hare of the build tool strip (Run > Quick Build) makes every native build and publish a quick build for tests: the C compiler with this level (msvc /Od or /O1) and " +
                         "the program in units built side by side (gcc, clang, MinGW, Emscripten). -O1 takes a little longer and the program runs faster. Not for releases.")));
        }

        private void Accept()
        {
            Settings.ShowWelcome = _showWelcome;
            Settings.Engine = _engine;
            Settings.Assistant = _assistant;
            Settings.McpServer = _mcp;
            Settings.IdfPath = _idfPath;
            Settings.QuickBuildLevel = _quickLevel;
            Close(true);
        }
    }
}
