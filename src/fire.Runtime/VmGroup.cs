using System;
using System.Collections.Generic;
using System.Diagnostics;
using fire.Values;
using fire.Bytecode;

namespace fire.Runtime
{
    /// <summary>The main program's group in the VM (docs/ROADMAP_TASKS.md step 5, as natively): an event handler of a window is a task with the main
    /// program's role - a VM of its own on the main program's global scope, on the main program's thread, one member at a time. It starts at once and runs
    /// until it ends or waits (`Sleep`, `await`, a section: VM.Suspend); then the caller goes on - the window stays responsive - and the member goes on at the
    /// next switch point of the main program (the next callback, every Tick, its waits).</summary>
    internal static class VmGroup
    {
        [ThreadStatic] private static List<VmScheduler.VmTask>? _members;
        [ThreadStatic] private static bool _running;

        static VmGroup() { TaskHooks.UiSwitchPoint = RunReady; }

        public static bool Any => _members is { Count: > 0 };

        /// <summary>A callback of a window on the main program `home`: as a member of its group. false: not here (the scheduler is off, or the caller is
        /// a member itself - a modal loop calls its callbacks itself).</summary>
        public static bool TryStart(VM home, LambdaValue lambda, Value[] args, Action<string>? onUnhandled)
        {
            if (!VmScheduler.Enabled || home.IsFireThreadVm || home.IsGroupMember || _running) return false;
            var vm = home.MakeGroupMember(lambda, args);
            var task = new VmScheduler.VmTask(vm, error =>
            {
                if (error != null) onUnhandled?.Invoke(error.Message);
                else if (vm.UnhandledException != null) onUnhandled?.Invoke(new UncaughtScriptException(vm.UnhandledException).Message);
            }) { IsGroup = true };
            vm.PoolTask = task;
            RunOne(task);
            return true;
        }

        /// <summary>A turn of a wait of the program on this thread: the members whose wait is over run, the windows of the thread read their events.</summary>
        public static void WaitTurn()
        {
            RunReady();
            TaskHooks.IdlePump?.Invoke();
        }

        /// <summary>The switch point: the members whose wait is over run (until they end or wait again).</summary>
        public static void RunReady()
        {
            if (_members is not { Count: > 0 } members || _running) return;
            long now = Stopwatch.GetTimestamp();
            foreach (var t in members.ToArray())
            {
                bool ready;
                try { ready = (t.WaitUntil != 0 && now >= t.WaitUntil) || t.WaitFor == null || t.WaitFor(); }
                catch { ready = true; }
                if (!ready) continue;
                members.Remove(t);
                RunOne(t);
            }
        }

        private static void RunOne(VmScheduler.VmTask task)
        {
            var home = VM.CurrentThreadVm;
            bool suspended = false;
            Exception? error = null;
            _running = true;
            try { suspended = task.Vm.RunSlice(); }
            catch (Exception ex) { error = ex; }
            finally { _running = false; VM.SetCurrentThreadVm(home); }
            if (suspended) { (_members ??= new()).Add(task); return; }
            task.Vm.PoolTask = null;
            task.OnEnd(error);
        }
    }
}
