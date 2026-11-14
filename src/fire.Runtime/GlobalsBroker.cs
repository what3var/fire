using System;
using System.Collections.Generic;
using System.Threading;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// The mediation between the main program (which owns the global variables) and its `fire` threads
    /// (docs/spec/THREADING_DESIGN.md section 7):
    ///
    /// - Fire threads READ globals directly (no snapshot), protected by a shared lock (<see cref="Lock"/>, the same for
    ///   the values of the globals and for all objects that belong to the global scope).
    /// - They may CHANGE the shared area only within a SECTION: the thread registers (<see cref="EnterSection"/>) and
    ///   waits; the main program grants the sections one after the other when it calls `sync globals` (<see cref="Drain"/>) - only
    ///   one at a time, while it itself waits. The state of the globals is thus clear to the main program at all times, and there is
    ///   exactly one writer.
    /// - `fire global { ... }` instead queues a job (<see cref="PostJob"/>) which the main program executes at the next
    ///   `sync globals`; the thread does not wait.
    ///
    /// The order is FIFO. When the main program ends (<see cref="Close"/>), waiting and future sections are granted immediately
    /// - nobody hangs on an owner that no longer answers.
    /// </summary>
    public sealed class GlobalsBroker
    {
        /// <summary>The global scope of the main program.</summary>
        public Scope Scope { get; }

        /// <summary>Protects globals values and all objects of the shared area.</summary>
        public ThreadShareLock Lock { get; }

        /// <summary>The main program: only its VM executes `sync globals`.</summary>
        public VM Owner { get; }

        private abstract class Request { }

        private sealed class SectionRequest : Request
        {
            public readonly ManualResetEventSlim Granted = new(false);
            public readonly ManualResetEventSlim Done = new(false);
            public GlobalsBroker? Broker;
            /// <summary>Taken by the thread itself while the main program was away (<see cref="AwayBegin"/>).</summary>
            public bool Away;
        }

        // ---- The main program away in a native of a package (docs/spec/THREADING_DESIGN.md section 7): it does not touch the globals meanwhile, so a thread
        // takes its section itself - one at a time - instead of waiting for the main program's next safe point. The request stays in the queue: Drain finds
        // it done. 0 present, 1 away, 2 away and a thread's section runs, 3 back and waiting for that section (no thread takes one any more).
        private readonly object _awayGate = new();
        private readonly ManualResetEventSlim _awayOpen = new(false);
        private int _away;

        /// <summary>Is the main program away in a native now (a method of the globals then runs in a section of the thread).</summary>
        public bool IsAway => Volatile.Read(ref _away) != 0;

        /// <summary>The main program goes into a native that gets no object of the shared area.</summary>
        public void AwayBegin()
        {
            lock (_awayGate) _away = 1;
            _awayOpen.Set();
            VmScheduler.Wake();   // (a task of the pool that waits for a section looks again)
        }

        /// <summary>The main program is back: it waits for the section a thread took meanwhile.</summary>
        public void AwayEnd()
        {
            lock (_awayGate)
            {
                _awayOpen.Reset();
                if (_away == 2)
                {
                    _away = 3;
                    while (_away == 3) Monitor.Wait(_awayGate);
                }
                _away = 0;
            }
        }

        /// <summary>The section at once while the main program is away and no other thread's section runs - without the queue (null: ask as usual).</summary>
        internal object? TryEnterAway()
        {
            if (Volatile.Read(ref _away) != 1) return null;
            var request = new SectionRequest { Broker = this };
            return TakeAway(request) ? request : null;
        }

        private static bool TakeAway(SectionRequest request)
        {
            var broker = request.Broker;
            if (broker == null || Volatile.Read(ref broker._away) != 1) return false;
            lock (broker._awayGate)
            {
                if (broker._away != 1) return false;
                broker._away = 2;
            }
            request.Away = true;
            request.Granted.Set();
            return true;
        }

        private sealed class JobRequest : Request
        {
            public LambdaValue Lambda = null!;
            public Value[] Args = Array.Empty<Value>();
            public Scope Holder = null!;
            public ActorReply? Reply;   // `await on`: the result goes back
            public ObjectInstance? Target;   // a method call of a fire thread on an object of the globals (rule 10): no lambda
            public string? Method;
        }

        private readonly object _gate = new();
        private readonly Queue<Request> _queue = new();
        private bool _closed;

        public GlobalsBroker(VM owner, Scope scope)
        {
            Owner = owner;
            Scope = scope;
            Lock = new ThreadShareLock();
            scope.SharingLock = Lock;
        }

        /// <summary>Is anything in the queue?</summary>
        public bool HasPending
        {
            get { lock (_gate) return _queue.Count > 0; }
        }

        public bool IsClosed
        {
            get { lock (_gate) return _closed; }
        }

        // -------------------------------------------------------------
        // Thread-Seite
        // -------------------------------------------------------------

        /// <summary>Registers the calling thread and waits until the main program grants the section. Afterwards it may change the shared
        /// area until it calls <see cref="ExitSection"/>. Returns a handle for the completion (or null if the
        /// main program has already ended - then it is granted immediately and there is nothing to complete).</summary>
        public object? EnterSection()
        {
            var request = (SectionRequest?)RequestSection();
            if (request == null) return null;
            while (!request.Granted.IsSet)
            {
                WaitHandle.WaitAny(new[] { request.Granted.WaitHandle, _awayOpen.WaitHandle });
                if (request.Granted.IsSet || TakeAway(request)) break;
                // (away, but another thread's section runs: wait until it ends or the main program is back)
                lock (_awayGate) if (_away is 2 or 3) Monitor.Wait(_awayGate, 50);
            }
            return request;
        }

        /// <summary>The request of <see cref="EnterSection"/> without the wait (a task of the pool waits by leaving its loop): granted when
        /// <see cref="IsGranted"/> says so; null - the main program has ended - is granted.</summary>
        internal object? RequestSection()
        {
            var request = new SectionRequest { Broker = this };
            lock (_gate)
            {
                if (_closed) return null;
                _queue.Enqueue(request);
            }
            VM.RaiseQueued(); // the main program notices it at its next safe point (see VM.AutoSyncAt)
            FireRuntime.WakeWaitingOwner();
            return request;
        }

        internal static bool IsGranted(object? handle) => handle is not SectionRequest request || request.Granted.IsSet || TakeAway(request);

        /// <summary>Ends the section that <see cref="EnterSection"/> granted.</summary>
        public void ExitSection(object? handle)
        {
            if (handle is not SectionRequest request) return;
            if (request.Away)
            {
                lock (_awayGate)
                {
                    _away = _away == 3 ? 0 : 1;
                    Monitor.PulseAll(_awayGate);
                }
                VmScheduler.Wake();
            }
            request.Done.Set();
        }

        /// <summary>`fire global { ... }`: queues the job and returns immediately. `holder` owns the objects copied for the job
        /// and is released as soon as it has run.</summary>
        /// <summary>A method call of a fire thread on an object of the globals (rule 10 of step 8): the main program runs it, the reply carries the result.</summary>
        public void PostMethodJob(ObjectInstance target, string method, Value[] args, Scope holder, ActorReply reply)
        {
            lock (_gate)
            {
                if (_closed) { holder.Release(NullDestructRunner.Instance); reply.Failure = "The main program has ended."; reply.Done.Set(); return; }
                _queue.Enqueue(new JobRequest { Target = target, Method = method, Args = args, Holder = holder, Reply = reply });
            }
            VM.RaiseQueued();
            FireRuntime.WakeWaitingOwner();
        }

        public void PostJob(LambdaValue lambda, Value[] args, Scope holder, ActorReply? reply = null)
        {
            lock (_gate)
            {
                if (_closed) { holder.Release(NullDestructRunner.Instance); reply?.Done.Set(); return; }
                _queue.Enqueue(new JobRequest { Lambda = lambda, Args = args, Holder = holder, Reply = reply });
            }
            VM.RaiseQueued(); // the main program notices it at its next safe point (see VM.AutoSyncAt)
            FireRuntime.WakeWaitingOwner();
        }

        // -------------------------------------------------------------
        // Owner side
        // -------------------------------------------------------------

        /// <summary>`sync globals`: processes everything that is in the queue up to now (also what is added
        /// meanwhile), in order. Returns the number of processed entries. Call only on the owner's thread.</summary>
        public int Drain()
        {
            int handled = 0;
            while (true)
            {
                Request? next;
                lock (_gate)
                {
                    if (_queue.Count == 0) return handled;
                    next = _queue.Dequeue();
                }
                handled++;
                switch (next)
                {
                    case SectionRequest section:
                        section.Granted.Set();
                        VmScheduler.Wake();   // (a task of the pool that waits for it)
                        section.Done.Wait(); // the thread works, the main program waits
                        break;
                    case JobRequest { Target: { } target } call:
                        Owner.RunGlobalsMethodJob(target, call.Method!, call.Args, call.Holder, call.Reply!);
                        call.Holder.Release(Owner);
                        break;
                    case JobRequest job:
                        Owner.RunGlobalsJob(job.Lambda, job.Args, job.Reply);
                        job.Holder.Release(Owner);
                        break;
                }
            }
        }

        /// <summary>The main program has ended: waiting sections are granted immediately (without waiting for their end), future ones
        /// likewise, queued jobs lapse.</summary>
        public void Close()
        {
            List<Request> pending;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                pending = new List<Request>(_queue);
                _queue.Clear();
            }
            foreach (var request in pending)
            {
                if (request is SectionRequest section) section.Granted.Set();
                else if (request is JobRequest job) { job.Holder.Release(NullDestructRunner.Instance); job.Reply?.Done.Set(); }
            }
        }
    }
}
