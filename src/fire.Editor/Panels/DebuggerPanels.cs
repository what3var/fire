using System;
using System.Collections.Generic;

namespace fire.Editor
{
    /// <summary>Bundles the three debugger views (threads, scope, stack) for the main window: attach a DebugSession,
    /// update all together, pass on the thread selection.</summary>
    internal sealed class DebuggerPanels
    {
        private readonly ThreadsPanelControl _threads;
        private readonly ScopePanelControl _scope;
        private readonly StackPanelControl _stack;

        public event Action<DebugThreadContext>? ThreadSelected;

        /// <summary>A message for the status bar (e.g. the result of setting a value in the scope view).</summary>
        public event Action<string>? StatusMessage;

        public DebuggerPanels(ThreadsPanelControl threads, ScopePanelControl scope, StackPanelControl stack)
        {
            _threads = threads;
            _scope = scope;
            _stack = stack;
            _threads.ThreadSelected += ctx => ThreadSelected?.Invoke(ctx);
            _scope.StatusMessage += text => StatusMessage?.Invoke(text);
        }

        public void AttachSession(DebugSession session)
        {
            _threads.AttachSession(session);
            _scope.AttachSession(session);
            _stack.AttachSession(session);
        }

        /// <summary>Rebuilds all three views - after EVERY state change (compile, step, stop, thread switch).</summary>
        public void Refresh(IEnumerable<string> breakpointDescriptions)
        {
            _threads.Refresh(breakpointDescriptions);
            _scope.Refresh();
            _stack.Refresh();
        }
    }
}
