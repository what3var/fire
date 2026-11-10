using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace fire.Runtime
{
    /// <summary>Step 7 (docs/ROADMAP_TASKS.md): the fire threads of the VM as tasks on a pool of system threads - as many as the machine has cores, more
    /// while some are blocked (a wait that cannot be suspended: inside a callback or a getter, a section, a native that calls back). The CLR cannot switch
    /// stacks, but the VM keeps its frames on the heap: at a wait (`Sleep`, `await actor.M()`, `await on`) a task that runs on its own level leaves its
    /// loop (<see cref="VM.Suspend"/>), the worker takes the next one, and the task goes on - on any worker - when what it waits for is there. A task never
    /// moves while it runs. FIRE_VM_THREADS=1 keeps one system thread per fire thread.</summary>
    internal static class VmScheduler
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("FIRE_VM_THREADS") != "1";

        static VmScheduler() { ActorMailbox.Enqueued = Wake; }   // (a message: a task that waits in `process` looks again)

        private static readonly object Gate = new();
        private static readonly Queue<VmTask> Ready = new();
        // (the waiting tasks - each entry with the number of the wait it is for: an entry of an earlier wait of the task is skipped)
        private static readonly List<(VmTask Task, int Wait)> Waiting = new();
        private static readonly PriorityQueue<(VmTask Task, int Wait), long> Timers = new();   // (the waiting tasks with a time, by it)
        private static long _seenWakes = -1;
        private static readonly int Cores = Math.Max(2, Environment.ProcessorCount);
        private static int _workers, _idle, _blocked;
        private static long _wakes;

        /// <summary>A fire thread of the VM: its VM, what it waits for while it is suspended, and what is done when it has ended.</summary>
        internal sealed class VmTask
        {
            public readonly VM Vm;
            public readonly Action<Exception?> OnEnd;
            public Func<bool>? WaitFor;     // (true: it goes on)
            public long WaitUntil;          // Stopwatch timestamp, 0: none
            public bool IsWaiting;          // (it waits: its wait number is WaitNumber)
            public bool IsGroup;            // a member of the main program's group (VmGroup): it runs on the main program's thread, never in the pool
            public int WaitNumber;
            public VmTask(VM vm, Action<Exception?> onEnd) { Vm = vm; OnEnd = onEnd; }
        }

        public static void Start(VM vm, Action<Exception?> onEnd)
        {
            var task = new VmTask(vm, onEnd);
            vm.PoolTask = task;
            lock (Gate)
            {
                Ready.Enqueue(task);
                Kick();
            }
        }

        /// <summary>Something may have changed that a suspended task waits for (a reply, a cancel, a signal): the workers look again.</summary>
        public static void Wake()
        {
            Interlocked.Increment(ref _wakes);
            lock (Gate) Monitor.PulseAll(Gate);
        }

        /// <summary>A worker waits inside a task (it could not be suspended): another one may start for the tasks that are ready.</summary>
        public static void BlockEnter()
        {
            lock (Gate)
            {
                _blocked++;
                if (Ready.Count > 0) Kick();
            }
        }

        public static void BlockLeave() { lock (Gate) _blocked--; }

        // (under Gate) a worker that waits is woken, or one more is started while all are busy
        private static void Kick()
        {
            if (_idle > 0) Monitor.PulseAll(Gate);
            else if (_workers - _blocked < Cores)
            {
                _workers++;
                new Thread(WorkerMain) { IsBackground = true, Name = "fire task worker" }.Start();
            }
        }

        // (under Gate) the suspended tasks whose wait is over go to the ready queue - those whose time has come, and, after a wake (Wake: a reply, a
        // cancel, a section granted, a signal), those whose condition holds; the time until the next time comes (ms)
        private static int ReleaseWaiting()
        {
            long now = Stopwatch.GetTimestamp();
            while (Timers.TryPeek(out var timed, out long at) && at <= now)
            {
                Timers.Dequeue();
                if (timed.Task.IsWaiting && timed.Task.WaitNumber == timed.Wait) MakeReady(timed.Task);
            }
            long wakes = Interlocked.Read(ref _wakes);
            if (wakes != _seenWakes)
            {
                _seenWakes = wakes;
                for (int i = 0; i < Waiting.Count; i++)
                {
                    var (t, number) = Waiting[i];
                    if (!t.IsWaiting || t.WaitNumber != number) continue;
                    bool ready;
                    try { ready = t.WaitFor == null || t.WaitFor(); }
                    catch { ready = true; }
                    if (ready) MakeReady(t);
                }
            }
            Waiting.RemoveAll(e => !e.Task.IsWaiting || e.Task.WaitNumber != e.Wait);
            if (Timers.TryPeek(out _, out long next)) return (int)Math.Clamp((next - now) * 1000 / Stopwatch.Frequency + 1, 1, 50);
            return 50;
        }

        // (under Gate)
        private static void MakeReady(VmTask t)
        {
            t.IsWaiting = false;
            Ready.Enqueue(t);
        }

        private static void WorkerMain()
        {
            long idleSince = 0;
            while (true)
            {
                VmTask? task = null;
                lock (Gate)
                {
                    while (true)
                    {
                        int wait = ReleaseWaiting();
                        if (Ready.Count > 0) { task = Ready.Dequeue(); idleSince = 0; break; }
                        long now = Stopwatch.GetTimestamp();
                        if (idleSince == 0) idleSince = now;
                        // (idle for long and not the last one: it ends; the last one stays while tasks are suspended)
                        if (Waiting.Count == 0 && (now - idleSince) > Stopwatch.Frequency * 10 && _workers > 1) { _workers--; return; }
                        if (Waiting.Count == 0 && (now - idleSince) > Stopwatch.Frequency * 30) { _workers--; return; }
                        _idle++;
                        Monitor.Wait(Gate, wait);
                        _idle--;
                    }
                }
                Run(task);
            }
        }

        private static void Run(VmTask task)
        {
            bool suspended;
            try { suspended = task.Vm.RunSlice(); }
            catch (Exception ex)
            {
                task.Vm.PoolTask = null;
                task.OnEnd(ex);
                return;
            }
            if (!suspended)
            {
                task.Vm.PoolTask = null;
                task.OnEnd(null);
                return;
            }
            lock (Gate)
            {
                task.IsWaiting = true;
                int number = ++task.WaitNumber;
                bool ready;
                try { ready = task.WaitFor?.Invoke() ?? false; }
                catch { ready = true; }
                if (ready) { MakeReady(task); return; }   // (it is there already)
                Waiting.Add((task, number));
                if (task.WaitUntil != 0) Timers.Enqueue((task, number), task.WaitUntil);
                if (task.WaitUntil == 0 || task.WaitUntil < Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20) Monitor.PulseAll(Gate);   // (an idle worker waits for an earlier time)
            }
        }
    }
}
