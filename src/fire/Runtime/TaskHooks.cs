using System;

namespace fire.Runtime
{
    /// <summary>Hooks of the scheduler for the layers below the runtime (the window bridge): set by fire.Runtime.</summary>
    public static class TaskHooks
    {
        /// <summary>The switch point of the UI loop (every Tick of a window): the members of the main program's group that waited and may go on now run.</summary>
        public static Action? UiSwitchPoint;

        /// <summary>The windows that this thread pumps itself (set by the window bridge at their Pump/Tick): the waits of the program on this thread call it
        /// at each turn, so the system's events for them are read meanwhile - the window repaints and answers the system while the program sleeps or
        /// waits (ROADMAP_TASKS 5b); the program takes the events at its next Pump/Tick.</summary>
        [ThreadStatic] public static Action? IdlePump;
    }
}
