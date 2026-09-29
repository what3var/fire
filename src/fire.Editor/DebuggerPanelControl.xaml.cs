using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using fire.Runtime;
using fire.Values;

namespace fire.Editor
{
    /// <summary>Ein eigenständiges, wiederverwendbares Debugger-Panel:
    /// Threads-Liste, aufklappbarer Scope-Baum, Wert-Stack, Aufruftiefe/
    /// aktuelle Zeile/Haltepunkte-Anzeige - arbeitet auf EINER DebugSession
    /// (siehe AttachSession), kennt aber selbst NICHTS vom Editor/Kompilieren
    /// (das bleibt Sache des Host-Fensters, siehe ScriptEditorControl-
    /// Klassendoku für dasselbe Prinzip). Sowohl vom alten Einzeldatei-
    /// Fenster (MainWindow) als auch vom neuen Tabbed-Projekt-Fenster
    /// (ProjectWindow) genutzt.</summary>
    public partial class DebuggerPanelControl : UserControl
    {
        private DebugSession? _session;

        /// <summary>Feuert, wenn der Nutzer in der Threads-Liste einen
        /// ANDEREN als den bisher aktiven Thread auswählt - das Control hat
        /// dabei bereits DebugSession.SelectThread aufgerufen (ein reiner
        /// Session-Zustand, den es selbst verwaltet), der Host reagiert
        /// darauf i.d.R. mit Editor-Hervorhebung/Scrollen zur neuen Zeile
        /// und einer Status-Meldung - beides Dinge, die dieses Control
        /// selbst nicht kennt (siehe Klassendoku).</summary>
        public event Action<DebugThreadContext>? ThreadSelected;

        // Index-parallel zu ThreadsList.ItemsSource (siehe RefreshThreadsList)
        // - die Liste selbst zeigt nur formatierten Text an, die Auswahl wird
        // über den Index auf dieses Parallel-Array zurückgemappt.
        private List<DebugThreadContext> _threadListItems = new();

        public DebuggerPanelControl()
        {
            InitializeComponent();
        }

        /// <summary>Verbindet dieses Control mit einer (neuen) DebugSession -
        /// z.B. nach einem Neukompilieren, wenn der Host eine frische
        /// DebugSession erzeugt hat. Ruft selbst KEIN Refresh() auf - der
        /// Host tut das i.d.R. direkt im Anschluss.</summary>
        public void AttachSession(DebugSession session) => _session = session;

        /// <summary>Baut Threads-Liste, Scope-Baum, Wert-Stack und die
        /// Text-Anzeigen (Aufruftiefe/aktuelle Zeile/Haltepunkte) komplett
        /// neu auf - vom Host nach JEDER Zustandsänderung aufzurufen (nach
        /// Kompilieren, jedem Schritt, Stop, Thread-Wechsel).
        /// `breakpointDescriptions` sind bereits fertig formatierte Einträge
        /// für die Anzeige (z.B. nur "12" bei einer einzelnen Datei - siehe
        /// MainWindow - oder "datei.script:12" bei mehreren - siehe
        /// ProjectWindow) - die Haltepunkt-MENGE selbst verwaltet je ein
        /// ScriptEditorControl, dieses Control weiß nichts über Dateien.</summary>
        public void Refresh(IEnumerable<string> breakpointDescriptions)
        {
            RefreshThreadsList();

            var vm = _session?.Vm;
            CallDepthText.Text = $"Aufruftiefe: {(vm?.DebugCallDepth.ToString() ?? "-")}";
            CurrentLineText.Text = $"Aktuelle Zeile: {(vm != null ? vm.CurrentLine.ToString() : "-")}";
            var descriptions = breakpointDescriptions.ToList();
            BreakpointsText.Text = descriptions.Count == 0
                ? "Haltepunkte: (keine - F9 auf der Cursor-Zeile)"
                : "Haltepunkte: " + string.Join(", ", descriptions);

            ScopeTree.Items.Clear();
            if (vm == null)
            {
                StackView.ItemsSource = null;
                return;
            }

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
                    ? (level.IsFunctionTopLevel ? "Aktiver Scope (Funktionsebene)" : "Aktiver Scope")
                    : $"umschließender Scope (Tiefe {level.Depth})" + (level.IsFunctionTopLevel ? " - Parameter" : "");

                var node = new TreeViewItem { Header = label, IsExpanded = level.Depth == 0 };
                foreach (var (name, value) in level.Variables)
                    node.Items.Add(BuildVariableTreeItem(name, value));
                if (level.Variables.Count == 0)
                    node.Items.Add(new TreeViewItem { Header = "(leer)" });
                ScopeTree.Items.Add(node);
            }

            // Global ganz unten, eingeklappt (meist nicht der primäre Fokus
            // beim Debuggen einer bestimmten Funktion).
            var globals = vm.DebugGlobals().ToList();
            var globalNode = new TreeViewItem { Header = "Global", IsExpanded = false };
            foreach (var (name, value) in globals)
                globalNode.Items.Add(BuildVariableTreeItem(name, value));
            if (globals.Count == 0)
                globalNode.Items.Add(new TreeViewItem { Header = "(leer)" });
            ScopeTree.Items.Add(globalNode);

            StackView.ItemsSource = vm.DebugStackSnapshot
                .Reverse()
                .Select(v => v.ToString())
                .ToList();
        }

        private void RefreshThreadsList()
        {
            _threadListItems = _session?.Threads.ToList() ?? new List<DebugThreadContext>();
            var active = _session?.ActiveThread;

            ThreadsList.ItemsSource = _threadListItems.Select(t =>
            {
                string status = t.RuntimeError != null ? $"Fehler: {t.RuntimeError}"
                    : t.IsFinished ? "beendet"
                    : $"Zeile {t.Vm.CurrentLine}";
                string marker = ReferenceEquals(t, active) ? "-> " : "   ";
                return $"{marker}{t.Name} ({status})";
            }).ToList();

            int activeIndex = active != null ? _threadListItems.FindIndex(t => ReferenceEquals(t, active)) : -1;
            if (activeIndex >= 0) ThreadsList.SelectedIndex = activeIndex;
        }

        private void ThreadsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = ThreadsList.SelectedIndex;
            if (index < 0 || index >= _threadListItems.Count || _session == null) return;

            var chosen = _threadListItems[index];
            if (ReferenceEquals(chosen, _session.ActiveThread)) return;

            _session.SelectThread(chosen);
            ThreadSelected?.Invoke(chosen);
        }

        private void PauseThread_Click(object sender, System.Windows.RoutedEventArgs e) =>
            _session?.PauseActiveThread();

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
                    item.Items.Add(new TreeViewItem { Header = "(Zyklus - Objekt liegt weiter oben im Baum)" });
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
                    item.Items.Add(new TreeViewItem { Header = "(Zyklus - Array liegt weiter oben im Baum)" });
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
