using System.Collections.Generic;
using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace fire.Editor
{
    /// <summary>Builds the default layout of the editor: the documents in the middle, the output and the debugger panels below, the solution explorer and the devices on the right, the properties under them.
    /// The ids are the keys under which a saved layout finds its panels again (see MainWindow.LoadLayout).</summary>
    internal sealed class SparkFactory : Factory
    {
        public const string DocumentsId = "documents";
        public const string BottomToolsId = "tools-bottom";
        public const string RightToolsId = "tools-right";

        /// <summary>The areas in the order of the bottom tool dock: id, title, content (the right dock gets "devices").</summary>
        private readonly List<(string Id, string Title, Control Content)> _bottom;
        private readonly List<(string Id, string Title, Control Content)> _right;
        // under the right tool dock (the properties)
        private readonly List<(string Id, string Title, Control Content)> _rightBottom;

        public const string RightBottomToolsId = "tools-right-bottom";
        /// <summary>The tool dock at the left edge (the toolbox of the circuit editor).</summary>
        public const string LeftToolsId = "tools-left";
        private readonly List<(string Id, string Title, Control Content)> _left;

        public SparkFactory(List<(string Id, string Title, Control Content)> bottom, List<(string Id, string Title, Control Content)> right, List<(string Id, string Title, Control Content)> rightBottom,
            List<(string Id, string Title, Control Content)> left)
        {
            _bottom = bottom;
            _right = right;
            _rightBottom = rightBottom;
            _left = left;
        }

        /// <summary>A new tool dock for the left edge (also for a saved layout that has none yet).</summary>
        public ToolDock NewLeftDock(params IDockable[] tools) => new()
        {
            Id = LeftToolsId,
            Proportion = 0.13,
            Alignment = Alignment.Left,
            VisibleDockables = CreateList(tools),
            ActiveDockable = tools.Length > 0 ? tools[0] : null,
        };

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
                Proportion = 0.67,
                Orientation = Orientation.Vertical,
                VisibleDockables = CreateList<IDockable>(documents, new ProportionalDockSplitter { Id = "split-bottom" }, bottom),
                ActiveDockable = documents,
            };

            var rightTools = new List<IDockable>();
            foreach (var (id, title, content) in _right) rightTools.Add(NewTool(id, title, content));
            var rightTop = new ToolDock
            {
                Id = RightToolsId,
                Proportion = 0.55,
                Alignment = Alignment.Right,
                VisibleDockables = CreateList(rightTools.ToArray()),
                ActiveDockable = rightTools[0],
            };
            var rightBottomTools = new List<IDockable>();
            foreach (var (id, title, content) in _rightBottom) rightBottomTools.Add(NewTool(id, title, content));
            var rightBottom = new ToolDock
            {
                Id = RightBottomToolsId,
                Proportion = 0.45,
                Alignment = Alignment.Right,
                VisibleDockables = CreateList(rightBottomTools.ToArray()),
                ActiveDockable = rightBottomTools[0],
            };
            var right = new ProportionalDock
            {
                Id = "right",
                Proportion = 0.2,
                Orientation = Orientation.Vertical,
                VisibleDockables = CreateList<IDockable>(rightTop, new ProportionalDockSplitter { Id = "split-right-bottom" }, rightBottom),
                ActiveDockable = rightTop,
            };

            var main = new ProportionalDock
            {
                Id = "main",
                Orientation = Orientation.Horizontal,
                VisibleDockables = CreateList<IDockable>(NewLeftDock(_left.ConvertAll(t => (IDockable)NewTool(t.Id, t.Title, t.Content)).ToArray()),
                    new ProportionalDockSplitter { Id = "split-left" }, left, new ProportionalDockSplitter { Id = "split-right" }, right),
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

        public static Tool NewTool(string id, string title, Control content) =>
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
