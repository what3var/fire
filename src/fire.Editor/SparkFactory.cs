using System.Collections.Generic;
using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace fire.Editor
{
    /// <summary>Builds the default layout of the editor: the documents in the middle, the output and the debugger panels below, the solution explorer and the devices on the right.
    /// The ids are the keys under which a saved layout finds its panels again (see MainWindow.LoadLayout).</summary>
    internal sealed class SparkFactory : Factory
    {
        public const string DocumentsId = "documents";
        public const string BottomToolsId = "tools-bottom";
        public const string RightToolsId = "tools-right";

        /// <summary>The areas in the order of the bottom tool dock: id, title, content (the right dock gets "devices").</summary>
        private readonly List<(string Id, string Title, Control Content)> _bottom;
        private readonly List<(string Id, string Title, Control Content)> _right;

        public SparkFactory(List<(string Id, string Title, Control Content)> bottom, List<(string Id, string Title, Control Content)> right)
        {
            _bottom = bottom;
            _right = right;
        }

        public override IRootDock CreateLayout()
        {
            var documents = new DocumentDock
            {
                Id = DocumentsId,
                Title = "Documents",
                CanCreateDocument = false,
                IsCollapsable = false,
                VisibleDockables = CreateList<IDockable>(),
            };

            var bottomTools = new List<IDockable>();
            foreach (var (id, title, content) in _bottom) bottomTools.Add(NewTool(id, title, content));
            var bottom = new ToolDock
            {
                Id = BottomToolsId,
                Proportion = 0.28,
                Alignment = Alignment.Bottom,
                VisibleDockables = CreateList(bottomTools.ToArray()),
                ActiveDockable = bottomTools[0],
            };

            var left = new ProportionalDock
            {
                Id = "left",
                Proportion = 0.8,
                Orientation = Orientation.Vertical,
                VisibleDockables = CreateList<IDockable>(documents, new ProportionalDockSplitter { Id = "split-bottom" }, bottom),
                ActiveDockable = documents,
            };

            var rightTools = new List<IDockable>();
            foreach (var (id, title, content) in _right) rightTools.Add(NewTool(id, title, content));
            var right = new ToolDock
            {
                Id = RightToolsId,
                Proportion = 0.2,
                Alignment = Alignment.Right,
                VisibleDockables = CreateList(rightTools.ToArray()),
                ActiveDockable = rightTools[0],
            };

            var main = new ProportionalDock
            {
                Id = "main",
                Orientation = Orientation.Horizontal,
                VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter { Id = "split-right" }, right),
                ActiveDockable = left,
            };

            var root = CreateRootDock();
            root.Id = "root";
            root.IsCollapsable = false;
            root.VisibleDockables = CreateList<IDockable>(main);
            root.ActiveDockable = main;
            root.DefaultDockable = main;
            return root;
        }

        private static Tool NewTool(string id, string title, Control content) =>
            new() { Id = id, Title = title, Content = content, CanClose = true, CanFloat = true };

        public override void InitLayout(IDockable layout)
        {
            // floating windows (a panel dragged out of the window)
            HostWindowLocator = new Dictionary<string, System.Func<IHostWindow?>>
            {
                [nameof(IDockWindow)] = () => new HostWindow(),
            };
            base.InitLayout(layout);
        }
    }
}
