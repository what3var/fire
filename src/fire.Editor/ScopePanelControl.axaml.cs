using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using fire.Runtime;
using fire.Values;

namespace fire.Editor
{
    /// <summary>Der Scope des aktiven Threads als Baum: `this`, die Scope-Kette der aktuellen Funktion (innerster Block
    /// zuerst) und die Globals. Objekte und Arrays klappen lazy auf. Arbeitet auf EINER DebugSession (siehe
    /// AttachSession), kennt sonst nichts vom Editor.</summary>
    public partial class ScopePanelControl : UserControl
    {
        private DebugSession? _session;

        public ScopePanelControl()
        {
            InitializeComponent();
        }

        public void AttachSession(DebugSession session) => _session = session;

        /// <summary>Baut den Baum neu auf - nach jeder Zustandsänderung (Kompilieren, Schritt, Stopp, Thread-Wechsel).</summary>
        public void Refresh()
        {
            ScopeTree.Items.Clear();
            var vm = _session?.Vm;
            if (vm == null) return;

            // "this" ganz oben, falls an dieser Stelle gebunden - als echter
            // (aufklappbarer) Wert statt nur als Textzeile, siehe
            // BuildVariableTreeItem.
            if (vm.DebugThisValue is Value thisValue)
                ScopeTree.Items.Add(BuildVariableTreeItem("this", thisValue, expanded: true));

            // Scope-Kette der aktuellen Funktion - der ERSTE Eintrag (Depth 0)
            // ist der GERADE AKTIVE (innerste) Block, danach umschließende
            // Ebenen bis zur Funktions-/Methoden-/Lambda-Grenze.
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

            // Global ganz unten, eingeklappt (meist nicht der primäre Fokus
            // beim Debuggen einer bestimmten Funktion).
            var globals = vm.DebugGlobals().ToList();
            var globalNode = new TreeViewItem { Header = "Global", IsExpanded = false };
            foreach (var (name, value) in globals)
                globalNode.Items.Add(BuildVariableTreeItem(name, value));
            if (globals.Count == 0)
                globalNode.Items.Add(new TreeViewItem { Header = "(empty)" });
            ScopeTree.Items.Add(globalNode);
        }

        // -----------------------------------------------------------
        // Feld-/Element-Anzeige für Objekte und Arrays im Scope-Baum (siehe
        // Refresh) - baut Kind-Knoten LAZY erst beim tatsächlichen
        // Aufklappen auf (TreeViewItem.Expanded), statt den kompletten
        // (potenziell riesigen oder zyklischen) Objektgraphen sofort
        // komplett zu durchlaufen. Zyklenschutz über die Menge der bereits
        // auf dem Pfad von der Wurzel besuchten Objekte/Arrays
        // (Referenzidentität, nicht Wert-Gleichheit) - eine ganz normale
        // MEHRFACHE Referenz auf dasselbe Objekt an verschiedenen Stellen
        // ist dagegen kein Zyklus und bleibt aufklappbar (nur der Pfad
        // WURZEL->...->SELBES OBJEKT NOCHMAL wird abgeschnitten).
        // -----------------------------------------------------------

        private TreeViewItem BuildVariableTreeItem(string name, Value value, HashSet<object>? ancestors = null, bool expanded = false)
        {
            var item = new TreeViewItem { Header = $"{name} = {DescribeForTree(value)}", IsExpanded = expanded };
            AttachChildrenIfExpandable(item, value, ancestors ?? new HashSet<object>());
            return item;
        }

        /// <summary>Wie Value.ToString(), aber für Objekte/Arrays mit einer
        /// für den Debugger nützlicheren Kurzbeschreibung (Klassenname +
        /// Erzeugungs-ID statt des rohen .NET-Typnamens, Elementanzahl statt
        /// nur "&lt;array&gt;") - reine Anzeige-Bequemlichkeit, ändert nichts an
        /// Value.ToString() selbst (das wird u.a. für Skript-seitige
        /// String-Konkatenation gebraucht und soll dafür unverändert
        /// bleiben).</summary>
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
                if (!obj.Fields.Any()) return; // keine Felder - kein Aufklapp-Pfeil nötig

                item.Items.Add(new TreeViewItem { Header = "…" }); // Platzhalter, bis tatsächlich aufgeklappt
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
