using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using fire.Runtime;
using fire.Values;

namespace fire.Editor
{
    /// <summary>The scope of the active thread as a tree: `this`, the scope chain of the current function (innermost block
    /// first) and the globals. Objects and arrays expand lazily. Works on ONE DebugSession (see
    /// AttachSession), otherwise knows nothing about the editor.</summary>
    public partial class ScopePanelControl : UserControl
    {
        private DebugSession? _session;

        public ScopePanelControl()
        {
            InitializeComponent();
        }

        public void AttachSession(DebugSession session) => _session = session;

        /// <summary>Rebuilds the tree - after every state change (compile, step, stop, thread switch).</summary>
        public void Refresh()
        {
            ScopeTree.Items.Clear();
            var vm = _session?.Vm;
            if (vm == null) return;

            // "this" at the very top, if bound at this point - as a real
            // (expandable) value instead of just a text line, see
            // BuildVariableTreeItem.
            if (vm.DebugThisValue is Value thisValue)
                ScopeTree.Items.Add(BuildVariableTreeItem("this", thisValue, expanded: true));

            // Scope chain of the current function - the FIRST entry (depth 0)
            // is the CURRENTLY ACTIVE (innermost) block, after it enclosing
            // levels up to the function/method/lambda boundary.
            foreach (var level in vm.DebugScopeChain())
            {
                string label = level.Depth == 0
                    ? (level.IsFunctionTopLevel ? "Active scope (function level)" : "Active scope")
                    : $"enclosing scope (depth {level.Depth})" + (level.IsFunctionTopLevel ? " - parameters" : "");

                var node = new TreeViewItem { Header = label, IsExpanded = level.Depth == 0 };
                foreach (var (name, value) in level.Variables)
                    node.Items.Add(BuildVariableTreeItem(name, value));
                if (level.Variables.Count == 0)
                    node.Items.Add(new TreeViewItem { Header = "(empty)" });
                ScopeTree.Items.Add(node);
            }

            // Global at the very bottom, collapsed (usually not the primary focus
            // when debugging a particular function).
            var globals = vm.DebugGlobals().ToList();
            var globalNode = new TreeViewItem { Header = "Global", IsExpanded = false };
            foreach (var (name, value) in globals)
                globalNode.Items.Add(BuildVariableTreeItem(name, value));
            if (globals.Count == 0)
                globalNode.Items.Add(new TreeViewItem { Header = "(empty)" });
            ScopeTree.Items.Add(globalNode);
        }

        // -----------------------------------------------------------
        // Field/element display for objects and arrays in the scope tree (see
        // Refresh) - builds child nodes LAZILY only on actual
        // expanding (TreeViewItem.Expanded), instead of immediately walking the entire
        // (potentially huge or cyclic) object graph
        // completely. Cycle protection via the set of objects/arrays already
        // visited on the path from the root
        // (reference identity, not value equality) - a perfectly normal
        // MULTIPLE reference to the same object at different places
        // is not a cycle, on the other hand, and stays expandable (only the path
        // ROOT->...->SAME OBJECT AGAIN is cut off).
        // -----------------------------------------------------------

        private TreeViewItem BuildVariableTreeItem(string name, Value value, HashSet<object>? ancestors = null, bool expanded = false)
        {
            var item = new TreeViewItem { Header = $"{name} = {DescribeForTree(value)}", IsExpanded = expanded };
            AttachChildrenIfExpandable(item, value, ancestors ?? new HashSet<object>());
            return item;
        }

        /// <summary>Like Value.ToString(), but for objects/arrays with a
        /// short description more useful for the debugger (class name +
        /// creation ID instead of the raw .NET type name, element count instead of
        /// just "&lt;array&gt;") - pure display convenience, changes nothing about
        /// Value.ToString() itself (which is needed among other things for script-side
        /// string concatenation and is to stay unchanged
        /// for that).</summary>
        private static string DescribeForTree(Value value) => value.Kind switch
        {
            ValueKind.Class => $"{((ObjectInstance)value.AsObjectRef()).ClassName} (#{((ObjectInstance)value.AsObjectRef()).Id})",
            ValueKind.Array => $"Array[{value.AsArray().Length}]",
            _ => value.ToString(),
        };

        private void AttachChildrenIfExpandable(TreeViewItem item, Value value, HashSet<object> ancestors)
        {
            if (value.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)value.AsObjectRef();
                if (ancestors.Contains(obj))
                {
                    item.Items.Add(new TreeViewItem { Header = "(cycle - object is further up in the tree)" });
                    return;
                }
                if (!obj.Fields.Any()) return; // no fields - no expand arrow needed

                item.Items.Add(new TreeViewItem { Header = "…" }); // placeholder until actually expanded
                bool loaded = false;
                item.Expanded += (_, _) =>
                {
                    if (loaded) return;
                    loaded = true;
                    item.Items.Clear();
                    var childAncestors = new HashSet<object>(ancestors) { obj };
                    foreach (var (fieldName, fieldValue) in obj.Fields)
                        item.Items.Add(BuildVariableTreeItem(fieldName, fieldValue, childAncestors));
                };
            }
            else if (value.Kind == ValueKind.Array)
            {
                var arr = value.AsArray();
                if (arr.Length == 0) return;
                if (ancestors.Contains(arr))
                {
                    item.Items.Add(new TreeViewItem { Header = "(cycle - array is further up in the tree)" });
                    return;
                }

                item.Items.Add(new TreeViewItem { Header = "…" });
                bool loaded = false;
                item.Expanded += (_, _) =>
                {
                    if (loaded) return;
                    loaded = true;
                    item.Items.Clear();
                    var childAncestors = new HashSet<object>(ancestors) { arr };
                    for (int i = 0; i < arr.Length; i++)
                        item.Items.Add(BuildVariableTreeItem($"[{i}]", arr.Items[i], childAncestors));
                };
            }
        }
    }
}
