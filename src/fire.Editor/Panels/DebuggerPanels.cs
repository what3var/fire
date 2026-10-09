using System;
using System.Collections.Generic;

namespace fire.Editor
{
    /// <summary>Bündelt die drei Debugger-Ansichten (Threads, Scope, Stack) für das Hauptfenster: eine DebugSession
    /// anbinden, alle gemeinsam aktualisieren, die Thread-Auswahl weiterreichen.</summary>
    internal sealed class DebuggerPanels
    {
        private readonly ThreadsPanelControl _threads;
        private readonly ScopePanelControl _scope;
        private readonly StackPanelControl _stack;

        public event Action<DebugThreadContext>? ThreadSelected;

        public DebuggerPanels(ThreadsPanelControl threads, ScopePanelControl scope, StackPanelControl stack)
        {
            _threads = threads;
            _scope = scope;
            _stack = stack;
            _threads.ThreadSelected += ctx => ThreadSelected?.Invoke(ctx);
        }

        public void AttachSession(DebugSession session)
        {
            _threads.AttachSession(session);
            _scope.AttachSession(session);
            _stack.AttachSession(session);
        }

        /// <summary>Baut alle drei Ansichten neu auf - nach JEDER Zustandsänderung (Kompilieren, Schritt, Stopp, Thread-Wechsel).</summary>
        public void Refresh(IEnumerable<string> breakpointDescriptions)
        {
            _threads.Refresh(breakpointDescriptions);
            _scope.Refresh();
            _stack.Refresh();
        }
    }
}
