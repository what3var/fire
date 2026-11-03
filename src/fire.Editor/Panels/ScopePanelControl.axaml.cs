using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using fire.Debugging;

namespace fire.Editor
{
    /// <summary>The scope of the active thread as a tree: `this`, the scope chain of the current function (innermost block
    /// first) and the globals. Objects and arrays expand lazily. Works on ONE DebugSession (see
    /// AttachSession) and only through the engine-independent IDebugThread/DebugValue, otherwise knows nothing about the editor.
    /// The context menu of a variable, field or element sets its value (IDebugThread.TrySetValue).</summary>
    public partial class ScopePanelControl : UserControl
    {
        private DebugSession? _session;

        /// <summary>A message for the status bar (the result of setting a value).</summary>
        public event Action<string>? StatusMessage;

        public ScopePanelControl()
        {
            InitializeComponent();
        }

        public void AttachSession(DebugSession session) => _session = session;

        /// <summary>Rebuilds the tree - after every state change (compile, step, stop, thread switch).</summary>
        public void Refresh()
        {
            ScopeTree.Items.Clear();
            var thread = _session?.ActiveThread?.Thread;
            if (thread == null) return;

            // "this" at the very top, if bound at this point - as a real
            // (expandable) value instead of just a text line, see
            // BuildVariableTreeItem.
            if (thread.This is { } thisValue)
                ScopeTree.Items.Add(BuildVariableTreeItem(thisValue, expanded: true));

            // Scope chain of the current function - the FIRST entry (depth 0)
            // is the CURRENTLY ACTIVE (innermost) block, after it enclosing
            // levels up to the function/method/lambda boundary.
            foreach (var level in thread.Scopes())
            {
                string label = level.Depth == 0
                    ? (level.IsFunctionTopLevel ? "Active scope (function level)" : "Active scope")
                    : $"enclosing scope (depth {level.Depth})" + (level.IsFunctionTopLevel ? " - parameters" : "");

                var node = new TreeViewItem { Header = label, IsExpanded = level.Depth == 0 };
                foreach (var variable in level.Variables)
                    node.Items.Add(BuildVariableTreeItem(variable));
                if (level.Variables.Count == 0)
                    node.Items.Add(new TreeViewItem { Header = "(empty)" });
                ScopeTree.Items.Add(node);
            }

            // Global at the very bottom, collapsed (usually not the primary focus
            // when debugging a particular function).
            var globals = thread.Globals();
            var globalNode = new TreeViewItem { Header = "Global", IsExpanded = false };
            foreach (var variable in globals)
                globalNode.Items.Add(BuildVariableTreeItem(variable));
            if (globals.Count == 0)
                globalNode.Items.Add(new TreeViewItem { Header = "(empty)" });
            ScopeTree.Items.Add(globalNode);
        }

        // -----------------------------------------------------------
        // Field/element display for objects and arrays in the scope tree (see
        // Refresh) - builds child nodes LAZILY only on actual
        // expanding (TreeViewItem.Expanded), instead of immediately walking the entire
        // (potentially huge or cyclic) object graph
        // completely. Cycle protection via the ids (DebugValue.Id) of the objects/arrays already
        // visited on the path from the root - a perfectly normal
        // MULTIPLE reference to the same object at different places
        // is not a cycle, on the other hand, and stays expandable (only the path
        // ROOT->...->SAME OBJECT AGAIN is cut off).
        // -----------------------------------------------------------

        private TreeViewItem BuildVariableTreeItem(DebugValue value, HashSet<long>? ancestors = null, bool expanded = false)
        {
            var item = new TreeViewItem { Header = $"{value.Name} = {value.Display}", IsExpanded = expanded };
            AttachChildrenIfExpandable(item, value, ancestors ?? new HashSet<long>());
            if (value.CanSet) AttachSetMenu(item, value);
            return item;
        }

        private void AttachChildrenIfExpandable(TreeViewItem item, DebugValue value, HashSet<long> ancestors)
        {
            if (!value.HasChildren) return; // no fields/elements - no expand arrow needed
            if (value.Id != 0 && ancestors.Contains(value.Id))
            {
                item.Items.Add(new TreeViewItem { Header = value.Kind == DebugValueKind.Array ? "(cycle - array is further up in the tree)" : "(cycle - object is further up in the tree)" });
                return;
            }

            item.Items.Add(new TreeViewItem { Header = "…" }); // placeholder until actually expanded
            bool loaded = false;
            item.Expanded += (_, _) =>
            {
                if (loaded) return;
                loaded = true;
                item.Items.Clear();
                var childAncestors = new HashSet<long>(ancestors);
                if (value.Id != 0) childAncestors.Add(value.Id);
                IReadOnlyList<DebugValue> children;
                try { children = value.Children(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or TimeoutException)
                {
                    item.Items.Add(new TreeViewItem { Header = $"(not available: {ex.Message})" });
                    return;
                }
                foreach (var child in children)
                    item.Items.Add(BuildVariableTreeItem(child, childAncestors));
            };
        }

        /// <summary>"Set Value...": asks for the new value and sets it on the thread's own thread (DebugThreadContext.RequestSetValue); the panels
        /// are rebuilt afterwards like after a step.</summary>
        private void AttachSetMenu(TreeViewItem item, DebugValue value)
        {
            var set = new MenuItem { Header = "Set Value..." };
            set.Click += async (_, e) =>
            {
                e.Handled = true;
                var ctx = _session?.ActiveThread;
                if (ctx == null || _session == null) return;
                var text = await Dialogs.Input(TopLevel.GetTopLevel(this) as Window, $"New value of {value.Name} ({value.Kind}):", "Set Value", value.Display);
                if (text == null) return;
                if (!ctx.RequestSetValue(value, text, (ok, error) =>
                        Dispatcher.UIThread.Post(() => StatusMessage?.Invoke(ok ? $"{value.Name} = {text}" : $"{value.Name} was not set: {error}"))))
                    StatusMessage?.Invoke("Values can only be set while the thread is paused.");
            };
            item.ContextMenu = new ContextMenu { Items = { set } };
        }
    }
}
